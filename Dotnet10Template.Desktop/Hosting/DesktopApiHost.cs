using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Dotnet10Template.Desktop.Hosting;

internal sealed class DesktopApiHost : IAsyncDisposable
{
    private const int ApiPort = 8080;
    private const string ApiAssemblyName = "Dotnet10Template.Api";
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient httpClient;
    private readonly string connectionString;
    private Process? process;
    private bool disposed;

    public DesktopApiHost(string connectionString)
    {
        this.connectionString = connectionString;
        BaseUri = new Uri($"http://127.0.0.1:{ApiPort}");
        httpClient = new HttpClient
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public Uri BaseUri { get; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (process is not null)
        {
            return;
        }

        EnsurePortIsAvailable();

        var apiEntryPoint = ResolveApiEntryPoint();
        var startInfo = CreateStartInfo(apiEntryPoint);

        process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The bundled API process could not be started.");
        DesktopHostLog.Append($"Started owned API process. PID: {process.Id}.");

        try
        {
            await WaitUntilReadyAsync(cancellationToken);
            DesktopHostLog.Append("Owned API process reported healthy.");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            await StopAsync();
        }
        finally
        {
            httpClient.Dispose();
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ReadinessTimeout);
        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        while (!linked.IsCancellationRequested)
        {
            if (process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"The bundled API process exited before it became ready. Exit code: {process.ExitCode}.");
            }

            try
            {
                using var response = await httpClient.GetAsync("/health", linked.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            try
            {
                await Task.Delay(ReadinessPollInterval, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"The bundled API did not report healthy at {new Uri(BaseUri, "/health")} within {ReadinessTimeout.TotalSeconds:n0} seconds.");
    }

    private async Task StopAsync()
    {
        if (process is null)
        {
            DesktopHostLog.Append("API shutdown requested, but this desktop instance has no owned API process.");
            return;
        }

        var apiProcess = process;
        process = null;
        DesktopHostLog.Append($"API shutdown requested for owned process PID {apiProcess.Id}.");

        try
        {
            if (!apiProcess.HasExited)
            {
                var gracefulShutdownRequested = false;

                try
                {
                    gracefulShutdownRequested = apiProcess.CloseMainWindow();
                    DesktopHostLog.Append(gracefulShutdownRequested
                        ? $"CloseMainWindow sent to API process PID {apiProcess.Id}."
                        : $"API process PID {apiProcess.Id} has no main window; graceful shutdown is unavailable.");
                }
                catch (InvalidOperationException ex)
                {
                    DesktopHostLog.Append($"Unable to request graceful API shutdown for PID {apiProcess.Id}: {ex.Message}");
                }

                if (gracefulShutdownRequested)
                {
                    using var gracefulTimeout = new CancellationTokenSource(ShutdownTimeout);
                    try
                    {
                        await apiProcess.WaitForExitAsync(gracefulTimeout.Token);
                        DesktopHostLog.Append($"Owned API process PID {apiProcess.Id} stopped. Exit code: {apiProcess.ExitCode}.");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                if (!apiProcess.HasExited)
                {
                    DesktopHostLog.Append($"Killing owned API process tree for PID {apiProcess.Id}.");
                    apiProcess.Kill(entireProcessTree: true);
                    await apiProcess.WaitForExitAsync();
                    DesktopHostLog.Append($"Owned API process PID {apiProcess.Id} was killed. Exit code: {apiProcess.ExitCode}.");
                }
            }
            else
            {
                DesktopHostLog.Append($"Owned API process PID {apiProcess.Id} had already exited with code {apiProcess.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            DesktopHostLog.Append($"API stop failed for owned process PID {apiProcess.Id}: {ex}");
            throw;
        }
        finally
        {
            apiProcess.Dispose();
        }
    }

    private ProcessStartInfo CreateStartInfo(string apiEntryPoint)
    {
        var isExecutable = Path.GetExtension(apiEntryPoint)
            .Equals(".exe", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = isExecutable ? apiEntryPoint : "dotnet",
            Arguments = isExecutable ? string.Empty : $"\"{apiEntryPoint}\"",
            WorkingDirectory = Path.GetDirectoryName(apiEntryPoint) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{ApiPort}";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__DefaultConnection"] = connectionString;

        return startInfo;
    }

    private static string ResolveApiEntryPoint()
    {
        var apiFolders = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Api"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Api")),
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "DesktopApiPublishPath")
                ?.Value
        };

        foreach (var apiFolder in apiFolders.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var executablePath = Path.Combine(apiFolder!, $"{ApiAssemblyName}.exe");
            if (File.Exists(executablePath))
            {
                return executablePath;
            }

            var dllPath = Path.Combine(apiFolder!, $"{ApiAssemblyName}.dll");
            if (File.Exists(dllPath))
            {
                return dllPath;
            }
        }

        throw new FileNotFoundException(
            "The bundled API was not found. Build the desktop project to publish and copy the API artifact.",
            Path.Combine(AppContext.BaseDirectory, "Api", $"{ApiAssemblyName}.dll"));
    }

    private static void EnsurePortIsAvailable()
    {
        TcpListener? listener = null;

        try
        {
            listener = new TcpListener(IPAddress.Loopback, ApiPort)
            {
                ExclusiveAddressUse = true
            };
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Port {ApiPort} is already in use. Close the process using http://127.0.0.1:{ApiPort} before starting the desktop app.",
                ex);
        }
        finally
        {
            listener?.Stop();
        }
    }
}
