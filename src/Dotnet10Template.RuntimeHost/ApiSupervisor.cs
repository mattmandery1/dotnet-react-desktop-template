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

namespace Dotnet10Template.RuntimeHost;

internal sealed class ApiSupervisor : IAsyncDisposable
{
    private const string Host = "127.0.0.1";
    private static readonly string PortEnvironmentVariable =
        ProductIdentity.GetEnvironmentVariableName("DESKTOP_API_PORT");
    private const int StartupRetryCount = 5;
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly string StdinShutdownEnvironmentVariable =
        ProductIdentity.GetEnvironmentVariableName("DESKTOP_OWNED_API_STDIN_SHUTDOWN");

    private readonly HttpClient httpClient;
    private readonly RuntimeEndpoint postgresEndpoint;
    private readonly RuntimeHostLog log;
    private readonly WindowsJobObject jobObject;
    private readonly string applicationDataDirectory;
    private Process? process;
    private int port;
    private bool disposed;

    public ApiSupervisor(
        string applicationDataDirectory,
        RuntimeEndpoint postgresEndpoint,
        RuntimeHostLog log,
        WindowsJobObject jobObject)
    {
        this.applicationDataDirectory = applicationDataDirectory;
        this.postgresEndpoint = postgresEndpoint;
        this.log = log;
        this.jobObject = jobObject;
        BaseUri = new Uri($"http://{Host}:0");
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public Uri BaseUri { get; private set; }

    public int ProcessId =>
        process?.Id ?? throw new InvalidOperationException("API process has not started.");

    public async Task<CriticalChildExit> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var apiProcess = process
            ?? throw new InvalidOperationException("API process has not started.");
        await apiProcess.WaitForExitAsync(cancellationToken);
        return new CriticalChildExit("API", apiProcess.Id, apiProcess.ExitCode);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (process is not null)
        {
            return;
        }

        var apiEntryPoint = ResolveApiEntryPoint();
        var configuredPort = ResolveConfiguredPort();
        var retryCount = configuredPort.HasValue ? 1 : StartupRetryCount;

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            port = configuredPort ?? SelectAvailableLoopbackPort();
            BaseUri = new Uri($"http://{Host}:{port}");

            log.Append($"Selected API endpoint: {BaseUri}.");

            try
            {
                var startInfo = CreateStartInfo(apiEntryPoint);
                await StartOnSelectedPortAsync(startInfo, cancellationToken);
                return;
            }
            catch (Exception ex) when (!configuredPort.HasValue && attempt < retryCount)
            {
                log.Append($"API startup failed on {BaseUri}; retrying with a new dynamic port. {ex.Message}");
                await StopAsync();
            }
        }
    }

    public async Task StopAsync()
    {
        if (process is null)
        {
            log.Append("API shutdown requested, but RuntimeHost has no owned API process.");
            return;
        }

        var apiProcess = process;
        process = null;
        log.Append($"API shutdown requested for owned process PID {apiProcess.Id}.");

        try
        {
            if (!apiProcess.HasExited)
            {
                var gracefulShutdownRequested = false;

                try
                {
                    if (apiProcess.StartInfo.RedirectStandardInput)
                    {
                        await apiProcess.StandardInput.WriteLineAsync("shutdown");
                        await apiProcess.StandardInput.FlushAsync();
                        gracefulShutdownRequested = true;
                        log.Append($"Sent stdin shutdown request to API process PID {apiProcess.Id}.");
                    }
                    else
                    {
                        gracefulShutdownRequested = apiProcess.CloseMainWindow();
                    }

                    log.Append(gracefulShutdownRequested
                        ? $"Graceful shutdown requested for API process PID {apiProcess.Id}."
                        : $"API process PID {apiProcess.Id} has no main window; graceful shutdown is unavailable.");
                }
                catch (InvalidOperationException ex)
                {
                    log.Append($"Unable to request graceful API shutdown for PID {apiProcess.Id}: {ex.Message}");
                }

                if (gracefulShutdownRequested)
                {
                    using var gracefulTimeout = new CancellationTokenSource(ShutdownTimeout);
                    try
                    {
                        await apiProcess.WaitForExitAsync(gracefulTimeout.Token);
                        log.Append($"Owned API process PID {apiProcess.Id} stopped. Exit code: {apiProcess.ExitCode}.");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                if (!apiProcess.HasExited)
                {
                    log.Append($"Killing owned API process tree for PID {apiProcess.Id}.");
                    apiProcess.Kill(entireProcessTree: true);
                    await apiProcess.WaitForExitAsync();
                    log.Append($"Owned API process PID {apiProcess.Id} was killed. Exit code: {apiProcess.ExitCode}.");
                }
            }
            else
            {
                log.Append($"Owned API process PID {apiProcess.Id} had already exited with code {apiProcess.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            log.Append($"API stop failed for owned process PID {apiProcess.Id}: {ex}");
            throw;
        }
        finally
        {
            apiProcess.Dispose();
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
                using var response = await httpClient.GetAsync(new Uri(BaseUri, "/health"), linked.Token);
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

    private async Task StartOnSelectedPortAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The bundled API process could not be started.");
        jobObject.Add(process);
        log.Append($"Started owned API process. PID: {process.Id}.");

        try
        {
            await WaitUntilReadyAsync(cancellationToken);
            log.Append($"Owned API process reported healthy at {BaseUri}.");
        }
        catch
        {
            await StopAsync();
            throw;
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

        startInfo.RedirectStandardInput = true;
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://{Host}:{port}";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__DefaultConnection"] =
            postgresEndpoint.CreateConnectionString();
        startInfo.Environment[StdinShutdownEnvironmentVariable] = "1";

        return startInfo;
    }

    private string ResolveApiEntryPoint()
    {
        var apiExecutableName = ProductIdentity.ApiExecutableName;
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
            var resolvedApiFolder = PrepareWritableApiDirectory(apiFolder!);
            var executablePath = Path.Combine(resolvedApiFolder, $"{apiExecutableName}.exe");
            if (File.Exists(executablePath))
            {
                return executablePath;
            }

            var dllPath = Path.Combine(resolvedApiFolder, $"{apiExecutableName}.dll");
            if (File.Exists(dllPath))
            {
                return dllPath;
            }
        }

        throw new FileNotFoundException(
            "The bundled API was not found. Build the desktop project to publish and copy the API artifact.",
            Path.Combine(AppContext.BaseDirectory, "Api", $"{apiExecutableName}.dll"));
    }

    private string PrepareWritableApiDirectory(string sourceApiDirectory)
    {
        if (!Directory.Exists(sourceApiDirectory))
        {
            return sourceApiDirectory;
        }

        var writableApiDirectory = Path.GetFullPath(Path.Combine(applicationDataDirectory, "ApiRuntime"));
        Directory.CreateDirectory(applicationDataDirectory);

        if (Directory.Exists(writableApiDirectory))
        {
            Directory.Delete(writableApiDirectory, recursive: true);
        }

        CopyDirectory(sourceApiDirectory, writableApiDirectory);
        log.Append($"Copied bundled API runtime to writable directory: {writableApiDirectory}");
        return writableApiDirectory;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        }
    }

    private static int? ResolveConfiguredPort()
    {
        var configuredPort = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPort))
        {
            return null;
        }

        if (int.TryParse(configuredPort, out var resolvedPort) &&
            resolvedPort is > IPEndPoint.MinPort and <= IPEndPoint.MaxPort)
        {
            return resolvedPort;
        }

        throw new InvalidOperationException(
            $"{PortEnvironmentVariable} must be a TCP port from 1 to 65535.");
    }

    private static int SelectAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0)
        {
            ExclusiveAddressUse = true
        };

        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
