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

    private readonly HttpClient httpClient;
    private Process? process;

    public DesktopApiHost()
    {
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

        try
        {
            await WaitUntilReadyAsync(cancellationToken);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        httpClient.Dispose();
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
            return;
        }

        var apiProcess = process;
        process = null;

        try
        {
            if (!apiProcess.HasExited)
            {
                apiProcess.CloseMainWindow();

                using var gracefulTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await apiProcess.WaitForExitAsync(gracefulTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!apiProcess.HasExited)
                    {
                        apiProcess.Kill(entireProcessTree: true);
                        await apiProcess.WaitForExitAsync();
                    }
                }
            }
        }
        finally
        {
            apiProcess.Dispose();
        }
    }

    private static ProcessStartInfo CreateStartInfo(string apiEntryPoint)
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
        startInfo.Environment["ConnectionStrings__DefaultConnection"] =
            "Host=localhost;Port=5432;Database=dotnet10template;Username=postgres;Password=postgres";

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
