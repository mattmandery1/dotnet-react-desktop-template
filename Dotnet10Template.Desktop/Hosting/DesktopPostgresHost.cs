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
using WinAppApplicationData = Microsoft.Windows.Storage.ApplicationData;

namespace Dotnet10Template.Desktop.Hosting;

internal sealed class DesktopPostgresHost : IAsyncDisposable
{
    private const string RuntimeRelativePath = "Runtime\\Postgres";
    private const string PortEnvironmentVariable = "DOTNET10TEMPLATE_DESKTOP_POSTGRES_PORT";
    private const string Host = "127.0.0.1";
    private const string User = "postgres";
    private const string Database = "dotnet10template";
    private const int DefaultPort = 55432;

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private static readonly object LogSync = new();

    private readonly string runtimeDirectory;
    private readonly string binDirectory;
    private readonly string applicationDataDirectory;
    private readonly string dataDirectory;
    private readonly string logFilePath;
    private readonly int port;
    private Process? process;
    private bool disposed;

    public DesktopPostgresHost()
    {
        runtimeDirectory = ResolveRuntimeDirectory();
        binDirectory = Path.Combine(runtimeDirectory, "bin");
        applicationDataDirectory = GetApplicationDataDirectory();
        dataDirectory = Path.GetFullPath(Path.Combine(applicationDataDirectory, "PostgresData"));
        logFilePath = Path.GetFullPath(Path.Combine(applicationDataDirectory, "postgres.log"));
        port = ResolvePort();

        ConnectionString =
            $"Host={Host};Port={port};Database={Database};Username={User};Pooling=true";

        AppendPostgresLog("DesktopPostgresHost created.");
        AppendPostgresLog($"Application data directory: {applicationDataDirectory}");
    }

    public string ConnectionString { get; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (process is not null)
        {
            return;
        }

        EnsureRequiredBinariesExist();
        EnsurePortIsAvailable();
        Directory.CreateDirectory(applicationDataDirectory);

        if (!IsInitialized())
        {
            CleanPartialDataDirectory();
            await InitializeAsync(cancellationToken);
        }

        var startInfo = CreatePostgresStartInfo();
        AppendPostgresLog("Starting desktop PostgreSQL.");
        AppendPostgresLog($"Runtime directory: {runtimeDirectory}");
        AppendPostgresLog($"Data directory: {dataDirectory}");
        process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The desktop PostgreSQL process could not be started.");
        AppendPostgresLog($"Started owned PostgreSQL process. PID: {process.Id}.");
        process.OutputDataReceived += (_, args) => AppendPostgresLog(args.Data);
        process.ErrorDataReceived += (_, args) => AppendPostgresLog(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await WaitUntilReadyAsync(cancellationToken);
            await EnsureApplicationDatabaseAsync(cancellationToken);
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

        await StopAsync();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        AppendPostgresLog("Initializing desktop PostgreSQL data directory with initdb.");
        AppendPostgresLog($"initdb data directory: {dataDirectory}");

        var result = await RunToolAsync(
            "initdb.exe",
            [
                "-D",
                dataDirectory,
                "-U",
                User,
                "-A",
                "trust",
                "-E",
                "UTF8"
            ],
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
                    $"The desktop PostgreSQL server process exited before it became ready. Exit code: {process.ExitCode}. Data directory: {dataDirectory}. Log: {logFilePath}.");
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
            $"Desktop PostgreSQL did not accept local connections on {Host}:{port} within {ReadinessTimeout.TotalSeconds:n0} seconds. Data directory: {dataDirectory}. Log: {logFilePath}.");
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
                $"SELECT 1 FROM pg_database WHERE datname = '{Database}';"
            ],
            cancellationToken);

        if (existsResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to inspect the desktop PostgreSQL databases. {existsResult.GetMessage()}");
        }

        if (existsResult.StandardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line == "1"))
        {
            return;
        }

        var createResult = await RunToolAsync(
            "createdb.exe",
            [
                "-h",
                Host,
                "-p",
                port.ToString(CultureInfo.InvariantCulture),
                "-U",
                User,
                Database
            ],
            cancellationToken);

        if (createResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to create the desktop PostgreSQL database '{Database}'. {createResult.GetMessage()}");
        }
    }

    private async Task StopAsync()
    {
        if (process is null)
        {
            AppendPostgresLog("PostgreSQL shutdown requested, but this desktop instance has no owned PostgreSQL process.");
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

                if (result.ExitCode == 0)
                {
                    AppendPostgresLog($"pg_ctl stop completed for owned PostgreSQL process PID {postgresProcess.Id}.");
                }

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

    private ProcessStartInfo CreatePostgresStartInfo()
    {
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

        lock (LogSync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            File.AppendAllText(
                logFilePath,
                $"[{DateTimeOffset.Now:u}] {line}{Environment.NewLine}");
        }

        DesktopHostLog.Append($"PostgreSQL: {line}");
    }

    private async Task<ToolResult> RunToolAsync(
        string toolName,
        string[] arguments,
        CancellationToken cancellationToken,
        bool throwOnCancellation = true)
    {
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

        var result = new ToolResult(
            toolProcess.ExitCode,
            await stdout,
            await stderr);
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
                    $"The desktop PostgreSQL runtime is missing '{binary}'. Populate {runtimeDirectory} with the PostgreSQL Windows binaries before launching the desktop app.",
                    GetBinaryPath(binary));
            }
        }
    }

    private void EnsurePortIsAvailable()
    {
        TcpListener? listener = null;

        try
        {
            listener = new TcpListener(IPAddress.Parse(Host), port)
            {
                ExclusiveAddressUse = true
            };
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Desktop PostgreSQL port {port} is already in use on {Host}. Set {PortEnvironmentVariable} to a free local port or close the unrelated process using that port.",
                ex);
        }
        finally
        {
            listener?.Stop();
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

    private static int ResolvePort()
    {
        var configuredPort = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPort))
        {
            return DefaultPort;
        }

        if (int.TryParse(configuredPort, CultureInfo.InvariantCulture, out var port) &&
            port is > IPEndPoint.MinPort and <= IPEndPoint.MaxPort)
        {
            return port;
        }

        throw new InvalidOperationException(
            $"{PortEnvironmentVariable} must be a TCP port from 1 to 65535.");
    }

    private static string GetApplicationDataDirectory()
    {
        try
        {
            var packagedLocalFolder = WinAppApplicationData.GetDefault().LocalPath;

            if (!string.IsNullOrWhiteSpace(packagedLocalFolder))
            {
                return Path.GetFullPath(Path.Combine(packagedLocalFolder, "Dotnet10Template"));
            }
        }
        catch (Exception)
        {
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "Local");
        }

        return Path.Combine(localAppData, "Dotnet10Template");
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
