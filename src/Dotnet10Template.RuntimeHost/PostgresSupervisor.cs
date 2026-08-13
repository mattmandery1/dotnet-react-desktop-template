using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Dotnet10Template.RuntimeHost;

internal sealed class PostgresSupervisor : IAsyncDisposable
{
    private const string RuntimeRelativePath = "Runtime\\Postgres";
    private static readonly string PortEnvironmentVariable =
        ProductIdentity.GetEnvironmentVariableName("DESKTOP_POSTGRES_PORT");
    private const string Host = "127.0.0.1";
    private const string User = "postgres";
    private const int StartupRetryCount = 5;

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly string runtimeDirectory;
    private readonly string binDirectory;
    private readonly string applicationDataDirectory;
    private readonly string dataDirectory;
    private readonly string logFilePath;
    private readonly RuntimeHostLog log;
    private readonly WindowsJobObject jobObject;
    private int port;
    private RuntimeEndpoint? endpoint;
    private Process? process;
    private bool disposed;

    public PostgresSupervisor(
        string applicationDataDirectory,
        RuntimeHostLog log,
        WindowsJobObject jobObject)
    {
        this.applicationDataDirectory = applicationDataDirectory;
        this.log = log;
        this.jobObject = jobObject;
        runtimeDirectory = PrepareWritableRuntimeDirectory(ResolveRuntimeDirectory(), applicationDataDirectory);
        binDirectory = Path.Combine(runtimeDirectory, "bin");
        dataDirectory = Path.GetFullPath(Path.Combine(applicationDataDirectory, "PostgresData"));
        logFilePath = Path.GetFullPath(Path.Combine(applicationDataDirectory, "postgres.log"));

        AppendPostgresLog("PostgresSupervisor created.");
        AppendPostgresLog($"Application data directory: {applicationDataDirectory}");
        if (File.Exists(Path.Combine(dataDirectory, "postmaster.pid")))
        {
            AppendPostgresLog("Existing postmaster.pid detected before startup; PostgreSQL may perform crash recovery.");
        }
    }

    public RuntimeEndpoint Endpoint =>
        endpoint ?? throw new InvalidOperationException("PostgreSQL has not selected an endpoint yet.");

    public int ProcessId =>
        process?.Id ?? throw new InvalidOperationException("PostgreSQL process has not started.");

    public async Task<CriticalChildExit> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var postgresProcess = process
            ?? throw new InvalidOperationException("PostgreSQL process has not started.");
        await postgresProcess.WaitForExitAsync(cancellationToken);
        return new CriticalChildExit("PostgreSQL", postgresProcess.Id, postgresProcess.ExitCode);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (process is not null)
        {
            return;
        }

        EnsureRequiredBinariesExist();
        Directory.CreateDirectory(applicationDataDirectory);

        var configuredPort = ResolveConfiguredPort();
        var retryCount = configuredPort.HasValue ? 1 : StartupRetryCount;

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            port = configuredPort ?? SelectAvailableLoopbackPort();
            EnsureResolvedPort(port);
            endpoint = new RuntimeEndpoint(Host, port, ProductIdentity.PostgresDatabaseName, User);

            AppendPostgresLog($"Resolved PostgreSQL port: {port}.");
            AppendPostgresLog($"Selected PostgreSQL endpoint: {Endpoint.Host}:{Endpoint.Port}.");

            try
            {
                if (!IsInitialized())
                {
                    CleanPartialDataDirectory();
                    await InitializeAsync(cancellationToken);
                }

                await StartOnSelectedPortAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (!configuredPort.HasValue && attempt < retryCount)
            {
                AppendPostgresLog(
                    $"PostgreSQL startup failed on {Host}:{port}; retrying with a new dynamic port. {ex.Message}");
                await StopAsync();
            }
        }
    }

    public async Task StopAsync()
    {
        if (process is null)
        {
            AppendPostgresLog("PostgreSQL shutdown requested, but RuntimeHost has no owned PostgreSQL process.");
            return;
        }

        var postgresProcess = process;
        process = null;
        AppendPostgresLog($"PostgreSQL shutdown requested for owned process PID {postgresProcess.Id}.");

        try
        {
            if (!postgresProcess.HasExited)
            {
                var result = await RunToolAsync(
                    "pg_ctl.exe",
                    [
                        "stop",
                        "-D",
                        dataDirectory,
                        "-m",
                        "fast",
                        "-w",
                        "-t",
                        ShutdownTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                    ],
                    CancellationToken.None,
                    throwOnCancellation: false);

                AppendPostgresLog(result.ExitCode == 0
                    ? $"pg_ctl stop completed for owned PostgreSQL process PID {postgresProcess.Id}."
                    : $"pg_ctl stop returned exit code {result.ExitCode} for owned PostgreSQL process PID {postgresProcess.Id}.");

                if (!postgresProcess.HasExited)
                {
                    using var shutdownTimeout = new CancellationTokenSource(ShutdownTimeout);
                    try
                    {
                        await postgresProcess.WaitForExitAsync(shutdownTimeout.Token);
                        AppendPostgresLog($"Owned PostgreSQL process PID {postgresProcess.Id} stopped. Exit code: {postgresProcess.ExitCode}.");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                if (!postgresProcess.HasExited)
                {
                    AppendPostgresLog($"PostgreSQL process PID {postgresProcess.Id} did not exit after pg_ctl stop; killing owned process tree.");
                    postgresProcess.Kill(entireProcessTree: true);
                    await postgresProcess.WaitForExitAsync();
                    AppendPostgresLog($"Owned PostgreSQL process PID {postgresProcess.Id} was killed. Exit code: {postgresProcess.ExitCode}.");
                }
                else
                {
                    AppendPostgresLog($"Owned PostgreSQL process PID {postgresProcess.Id} exited after pg_ctl stop. Exit code: {postgresProcess.ExitCode}.");
                }
            }
            else
            {
                AppendPostgresLog($"Owned PostgreSQL process PID {postgresProcess.Id} had already exited with code {postgresProcess.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            AppendPostgresLog($"PostgreSQL stop failed for owned process PID {postgresProcess.Id}: {ex}");
            throw;
        }
        finally
        {
            postgresProcess.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopAsync();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        AppendPostgresLog("Initializing PostgreSQL data directory with initdb.");

        var result = await RunToolAsync(
            "initdb.exe",
            ["-D", dataDirectory, "-U", User, "-A", "trust", "-E", "UTF8"],
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"PostgreSQL data directory initialization failed during initdb. Data directory: {dataDirectory}. Log: {logFilePath}. {result.GetMessage()}");
        }

        if (!IsInitialized())
        {
            throw new InvalidOperationException(
                $"PostgreSQL initdb completed without creating PG_VERSION. Data directory: {dataDirectory}. Log: {logFilePath}.");
        }

        AppendPostgresLog("initdb completed successfully.");
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
                    $"The PostgreSQL server process exited before it became ready. Exit code: {process.ExitCode}. Data directory: {dataDirectory}. Log: {logFilePath}.");
            }

            var result = await RunToolAsync(
                "pg_isready.exe",
                ["-h", Host, "-p", port.ToString(CultureInfo.InvariantCulture), "-d", "postgres", "-U", User],
                linked.Token,
                throwOnCancellation: false);

            if (result.ExitCode == 0)
            {
                return;
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
            $"PostgreSQL did not accept local connections on {Host}:{port} within {ReadinessTimeout.TotalSeconds:n0} seconds. Data directory: {dataDirectory}. Log: {logFilePath}.");
    }

    private async Task EnsureApplicationDatabaseAsync(CancellationToken cancellationToken)
    {
        var existsResult = await RunToolAsync(
            "psql.exe",
            [
                "-h",
                Host,
                "-p",
                port.ToString(CultureInfo.InvariantCulture),
                "-U",
                User,
                "-d",
                "postgres",
                "-tAc",
                $"SELECT 1 FROM pg_database WHERE datname = '{ProductIdentity.PostgresDatabaseName}';"
            ],
            cancellationToken);

        if (existsResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to inspect the PostgreSQL databases. {existsResult.GetMessage()}");
        }

        if (existsResult.StandardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line == "1"))
        {
            return;
        }

        var createResult = await RunToolAsync(
            "createdb.exe",
            ["-h", Host, "-p", port.ToString(CultureInfo.InvariantCulture), "-U", User, ProductIdentity.PostgresDatabaseName],
            cancellationToken);

        if (createResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to create the PostgreSQL database '{ProductIdentity.PostgresDatabaseName}'. {createResult.GetMessage()}");
        }
    }

    private void CleanPartialDataDirectory()
    {
        if (!Directory.Exists(dataDirectory))
        {
            return;
        }

        AppendPostgresLog(
            $"Removing partial PostgreSQL data directory because PG_VERSION does not exist: {dataDirectory}");
        Directory.Delete(dataDirectory, recursive: true);
    }

    private async Task StartOnSelectedPortAsync(CancellationToken cancellationToken)
    {
        var startInfo = CreatePostgresStartInfo();
        AppendPostgresLog("Starting PostgreSQL.");
        process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The PostgreSQL process could not be started.");
        jobObject.Add(process);
        AppendPostgresLog($"Started owned PostgreSQL process. PID: {process.Id}.");
        process.OutputDataReceived += (_, args) => AppendPostgresLog(args.Data);
        process.ErrorDataReceived += (_, args) => AppendPostgresLog(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await WaitUntilReadyAsync(cancellationToken);
            await EnsureApplicationDatabaseAsync(cancellationToken);
            AppendPostgresLog($"PostgreSQL ready at {Endpoint.Host}:{Endpoint.Port}.");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    private ProcessStartInfo CreatePostgresStartInfo()
    {
        EnsureResolvedPort(port);

        var startInfo = new ProcessStartInfo
        {
            FileName = GetBinaryPath("postgres.exe"),
            WorkingDirectory = binDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.ArgumentList.Add("-D");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add(Host);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("listen_addresses=127.0.0.1");
        startInfo.Environment["PGDATA"] = dataDirectory;
        return startInfo;
    }

    private void AppendPostgresLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            File.AppendAllText(
                logFilePath,
                $"[{DateTimeOffset.Now:u}] {line}{Environment.NewLine}");
        }
        catch
        {
        }

        log.Append($"PostgreSQL: {line}");
    }

    private async Task<ToolResult> RunToolAsync(
        string toolName,
        string[] arguments,
        CancellationToken cancellationToken,
        bool throwOnCancellation = true)
    {
        EnsureResolvedPort(port);

        var startInfo = new ProcessStartInfo
        {
            FileName = GetBinaryPath(toolName),
            WorkingDirectory = binDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PGHOST"] = Host;
        startInfo.Environment["PGPORT"] = port.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["PGUSER"] = User;
        startInfo.Environment["PGDATA"] = dataDirectory;

        AppendPostgresLog($"Running PostgreSQL tool: {toolName} {string.Join(" ", arguments.Select(FormatLogArgument))}");

        using var toolProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"PostgreSQL tool '{toolName}' could not be started.");

        var stdout = toolProcess.StandardOutput.ReadToEndAsync();
        var stderr = toolProcess.StandardError.ReadToEndAsync();

        try
        {
            await toolProcess.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!throwOnCancellation)
        {
            if (!toolProcess.HasExited)
            {
                toolProcess.Kill(entireProcessTree: true);
                await toolProcess.WaitForExitAsync();
            }

            AppendPostgresLog($"PostgreSQL tool timed out or was canceled: {toolName}");
            return new ToolResult(-1, string.Empty, string.Empty);
        }
        catch (OperationCanceledException)
        {
            if (!toolProcess.HasExited)
            {
                toolProcess.Kill(entireProcessTree: true);
                await toolProcess.WaitForExitAsync();
            }

            throw;
        }

        var result = new ToolResult(toolProcess.ExitCode, await stdout, await stderr);
        AppendToolResult(toolName, result);
        return result;
    }

    private void AppendToolResult(string toolName, ToolResult result)
    {
        AppendPostgresLog($"{toolName} exited with code {result.ExitCode}.");

        foreach (var line in GetLogLines(result.StandardOutput))
        {
            AppendPostgresLog($"{toolName} stdout: {line}");
        }

        foreach (var line in GetLogLines(result.StandardError))
        {
            AppendPostgresLog($"{toolName} stderr: {line}");
        }
    }

    private bool IsInitialized()
    {
        return File.Exists(Path.Combine(dataDirectory, "PG_VERSION"));
    }

    private void EnsureRequiredBinariesExist()
    {
        foreach (var binary in new[] { "postgres.exe", "initdb.exe", "pg_ctl.exe", "pg_isready.exe", "psql.exe", "createdb.exe" })
        {
            if (!File.Exists(GetBinaryPath(binary)))
            {
                throw new FileNotFoundException(
                    $"The PostgreSQL runtime is missing '{binary}'. Populate {runtimeDirectory} with the PostgreSQL Windows binaries before launching the desktop app.",
                    GetBinaryPath(binary));
            }
        }
    }

    private string GetBinaryPath(string binaryName)
    {
        return Path.Combine(binDirectory, binaryName);
    }

    private static string ResolveRuntimeDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, RuntimeRelativePath),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", RuntimeRelativePath)),
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "DesktopPostgresRuntimePath")
                ?.Value
        };

        return candidates.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate) &&
                Directory.Exists(candidate))
            ?? Path.Combine(AppContext.BaseDirectory, RuntimeRelativePath);
    }

    private static string PrepareWritableRuntimeDirectory(string sourceRuntimeDirectory, string applicationDataDirectory)
    {
        var writableRuntimeDirectory = Path.GetFullPath(Path.Combine(applicationDataDirectory, "PostgresRuntime"));
        var writablePostgresPath = Path.Combine(writableRuntimeDirectory, "bin", "postgres.exe");

        if (File.Exists(writablePostgresPath))
        {
            return writableRuntimeDirectory;
        }

        if (!Directory.Exists(sourceRuntimeDirectory))
        {
            return sourceRuntimeDirectory;
        }

        Directory.CreateDirectory(applicationDataDirectory);
        if (Directory.Exists(writableRuntimeDirectory))
        {
            Directory.Delete(writableRuntimeDirectory, recursive: true);
        }

        CopyDirectory(sourceRuntimeDirectory, writableRuntimeDirectory);
        return writableRuntimeDirectory;
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

    private int? ResolveConfiguredPort()
    {
        var configuredPort = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPort))
        {
            AppendPostgresLog(
                $"{PortEnvironmentVariable} is not set; PostgreSQL will use a dynamically selected loopback port.");
            return null;
        }

        AppendPostgresLog($"{PortEnvironmentVariable} override requested: {configuredPort}.");

        if (int.TryParse(configuredPort, CultureInfo.InvariantCulture, out var configured) &&
            configured is > IPEndPoint.MinPort and <= IPEndPoint.MaxPort)
        {
            EnsureResolvedPort(configured);
            return configured;
        }

        throw new InvalidOperationException(
            $"{PortEnvironmentVariable} must be a TCP port from 1 to 65535. The value '{configuredPort}' is not valid; unset it to use dynamic port allocation.");
    }

    private static int SelectAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0)
        {
            ExclusiveAddressUse = true
        };

        listener.Start();
        try
        {
            if (listener.LocalEndpoint is not IPEndPoint endpoint)
            {
                throw new InvalidOperationException(
                    "Windows did not return an IP endpoint for the temporary PostgreSQL port listener.");
            }

            EnsureResolvedPort(endpoint.Port);
            return endpoint.Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void EnsureResolvedPort(int resolvedPort)
    {
        if (resolvedPort is <= IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException(
                $"PostgreSQL requires a resolved TCP port from 1 to 65535, but '{resolvedPort}' was selected.");
        }
    }

    private static string FormatLogArgument(string argument)
    {
        return argument.Contains(' ', StringComparison.Ordinal)
            ? $"\"{argument}\""
            : argument;
    }

    private static string[] GetLogLines(string content)
    {
        return content
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private sealed record ToolResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public string GetMessage()
        {
            var message = string.Join(
                Environment.NewLine,
                new[] { StandardError.Trim(), StandardOutput.Trim() }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

            return string.IsNullOrWhiteSpace(message)
                ? $"Exit code: {ExitCode}."
                : message;
        }
    }
}
