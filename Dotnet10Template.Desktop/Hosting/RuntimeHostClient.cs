using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dotnet10Template.Desktop.Hosting;

internal sealed class RuntimeHostClient : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    private readonly string pipeName = $"{ProductIdentity.ShortName}.RuntimeHost.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly NamedPipeServerStream pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private Process? process;
    private readonly CancellationTokenSource messagePumpCancellation = new();
    private readonly TaskCompletionSource shutdownComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? messagePumpTask;
    private bool shutdownRequested;
    private bool fatalReported;
    private bool disposed;

    public RuntimeHostClient()
    {
        pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    public Uri ApiBaseUri { get; private set; } = new("http://127.0.0.1:0");

    public RuntimeReadyPayload? ReadyPayload { get; private set; }

    public event EventHandler<int>? RuntimeHostExited;
    public event EventHandler<string>? RuntimeHostFatal;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (process is not null)
        {
            return;
        }

        var runtimeHostEntryPoint = ResolveRuntimeHostEntryPoint();
        try
        {
            process = StartRuntimeHost(runtimeHostEntryPoint);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"RuntimeHost failed to launch from '{runtimeHostEntryPoint}'.", ex);
        }

        process.EnableRaisingEvents = true;
        process.Exited += Process_Exited;

        DesktopHostLog.Append(
            $"Started RuntimeHost PID {process.Id} for Desktop PID {Environment.ProcessId} using pipe '{pipeName}'.");

        using var timeout = new CancellationTokenSource(StartupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var connectionTask = pipe.WaitForConnectionAsync(linked.Token);
        var processExitTask = process.WaitForExitAsync(CancellationToken.None);
        var completed = await Task.WhenAny(connectionTask, processExitTask);

        if (completed == processExitTask)
        {
            throw new InvalidOperationException(
                $"RuntimeHost exited before connecting to the Desktop control pipe. PID: {process.Id}. Exit code: {process.ExitCode}.");
        }

        try
        {
            await connectionTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"RuntimeHost did not connect to the Desktop control pipe within {StartupTimeout.TotalSeconds:n0} seconds. PID: {process.Id}.");
        }

        reader = new StreamReader(pipe);
        writer = new StreamWriter(pipe)
        {
            AutoFlush = true
        };
        DesktopHostLog.Append("RuntimeHost connected to Desktop control pipe.");

        while (!linked.IsCancellationRequested)
        {
            RuntimePipeMessage message;
            try
            {
                message = await ReadMessageAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"RuntimeHost connected to the Desktop control pipe but did not send ready/error within {StartupTimeout.TotalSeconds:n0} seconds. PID: {process.Id}.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("RuntimeHost sent a malformed IPC response.", ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("RuntimeHost sent a malformed IPC response.", ex);
            }

            if (message.Type.Equals("ready", StringComparison.Ordinal))
            {
                ReadyPayload = ReadReadyPayload(message.Payload);
                ApiBaseUri = new Uri(ReadyPayload.ApiBaseUri);
                DesktopHostLog.Append(
                    $"RuntimeHost ready. RuntimeHost PID {ReadyPayload.RuntimeHostProcessId}; API PID {ReadyPayload.ApiProcessId}; PostgreSQL PID {ReadyPayload.PostgresProcessId}; PostgreSQL {ReadyPayload.PostgresHost}:{ReadyPayload.PostgresPort}; API {ApiBaseUri}.");
                messagePumpTask = ListenForRuntimeMessagesAsync(messagePumpCancellation.Token);
                return;
            }

            if (message.Type.Equals("error", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"RuntimeHost returned a startup error: {ReadErrorMessage(message.Payload)}");
            }
        }

        throw new TimeoutException($"RuntimeHost did not report ready within {StartupTimeout.TotalSeconds:n0} seconds.");
    }

    public async Task StopAsync()
    {
        if (shutdownRequested)
        {
            return;
        }

        shutdownRequested = true;
        DesktopHostLog.Append("RuntimeHost shutdown requested by Desktop.");

        if (process is null)
        {
            return;
        }

        try
        {
            if (pipe.IsConnected)
            {
                if (writer is null)
                {
                    return;
                }

                await writer.WriteLineAsync("""{"type":"shutdown","payload":{}}""".AsMemory(), CancellationToken.None);
                await shutdownComplete.Task.WaitAsync(ShutdownTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            DesktopHostLog.Append("Timed out waiting for RuntimeHost graceful shutdown confirmation.");
        }
        catch (TimeoutException)
        {
            DesktopHostLog.Append("Timed out waiting for RuntimeHost graceful shutdown confirmation.");
        }
        catch (IOException ex)
        {
            DesktopHostLog.Append($"RuntimeHost control pipe closed during shutdown: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            DesktopHostLog.Append($"RuntimeHost reported shutdown error: {ex.Message}");
        }

        if (!process.HasExited)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                DesktopHostLog.Append(
                    $"RuntimeHost PID {process.Id} did not exit after graceful shutdown request; Desktop will exit and parent-death supervision will complete cleanup.");
            }
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
        messagePumpCancellation.Cancel();
        if (messagePumpTask is not null)
        {
            try
            {
                await messagePumpTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }

        writer?.Dispose();
        reader?.Dispose();
        pipe.Dispose();
        process?.Dispose();
        messagePumpCancellation.Dispose();
    }

    private Process StartRuntimeHost(string runtimeHostEntryPoint)
    {
        var isExecutable = Path.GetExtension(runtimeHostEntryPoint)
            .Equals(".exe", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = isExecutable ? runtimeHostEntryPoint : "dotnet",
            WorkingDirectory = Path.GetDirectoryName(runtimeHostEntryPoint) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (!isExecutable)
        {
            startInfo.ArgumentList.Add(runtimeHostEntryPoint);
        }

        startInfo.ArgumentList.Add("--desktop-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(DesktopHostLog.ApplicationDataDirectory);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("RuntimeHost process could not be started.");
    }

    private async Task<RuntimePipeMessage> ReadMessageAsync(CancellationToken cancellationToken)
    {
        if (reader is null)
        {
            throw new InvalidOperationException("RuntimeHost control pipe reader has not been initialized.");
        }

        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            throw new IOException("RuntimeHost control pipe closed.");
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString()
            ?? throw new InvalidOperationException("RuntimeHost control message did not include a type.");
        var payload = root.GetProperty("payload").Clone();
        return new RuntimePipeMessage(type, payload);
    }

    private async Task ListenForRuntimeMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(cancellationToken);
                if (message.Type.Equals("shutdown-complete", StringComparison.Ordinal))
                {
                    DesktopHostLog.Append("RuntimeHost confirmed graceful shutdown complete.");
                    shutdownComplete.TrySetResult();
                    return;
                }

                if (message.Type.Equals("fatal", StringComparison.Ordinal))
                {
                    var fatalMessage = ReadErrorMessage(message.Payload);
                    fatalReported = true;
                    DesktopHostLog.Append($"RuntimeHost reported fatal runtime failure: {fatalMessage}");
                    RuntimeHostFatal?.Invoke(this, fatalMessage);
                    continue;
                }

                if (message.Type.Equals("error", StringComparison.Ordinal))
                {
                    var errorMessage = ReadErrorMessage(message.Payload);
                    DesktopHostLog.Append($"RuntimeHost reported error after startup: {errorMessage}");
                    shutdownComplete.TrySetException(new InvalidOperationException(errorMessage));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException ex)
        {
            DesktopHostLog.Append($"RuntimeHost control pipe closed after startup: {ex.Message}");
            shutdownComplete.TrySetException(ex);
        }
        catch (Exception ex)
        {
            DesktopHostLog.Append($"RuntimeHost control pipe reader failed after startup: {ex}");
            shutdownComplete.TrySetException(ex);
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        var exitCode = 0;
        try
        {
            exitCode = process?.ExitCode ?? 0;
        }
        catch
        {
        }

        if (!shutdownRequested && !fatalReported)
        {
            DesktopHostLog.Append($"RuntimeHost exited unexpectedly with code {exitCode}.");
            RuntimeHostExited?.Invoke(this, exitCode);
        }
        else if (fatalReported)
        {
            DesktopHostLog.Append($"RuntimeHost exited after reporting fatal runtime failure. Exit code: {exitCode}.");
        }
    }

    private static string ResolveRuntimeHostEntryPoint()
    {
        var executableName = ProductIdentity.RuntimeHostExecutableName;
        var runtimeHostFolders = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "RuntimeHost"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "RuntimeHost")),
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "DesktopRuntimeHostPublishPath")
                ?.Value
        };

        foreach (var folder in runtimeHostFolders.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var executablePath = Path.Combine(folder!, $"{executableName}.exe");
            if (File.Exists(executablePath))
            {
                return executablePath;
            }

            var dllPath = Path.Combine(folder!, $"{executableName}.dll");
            if (File.Exists(dllPath))
            {
                return dllPath;
            }
        }

        throw new FileNotFoundException(
            "RuntimeHost was not found. Build the desktop project to publish and copy the runtime host artifact.",
            Path.Combine(AppContext.BaseDirectory, "RuntimeHost", $"{executableName}.dll"));
    }

    private static RuntimeReadyPayload ReadReadyPayload(JsonElement payload)
    {
        return new RuntimeReadyPayload(
            payload.GetProperty("apiBaseUri").GetString()
                ?? throw new InvalidOperationException("RuntimeHost ready payload did not include apiBaseUri."),
            payload.GetProperty("postgresHost").GetString()
                ?? throw new InvalidOperationException("RuntimeHost ready payload did not include postgresHost."),
            payload.GetProperty("postgresPort").GetInt32(),
            payload.GetProperty("runtimeHostProcessId").GetInt32(),
            payload.GetProperty("apiProcessId").GetInt32(),
            payload.GetProperty("postgresProcessId").GetInt32());
    }

    private static string ReadErrorMessage(JsonElement payload)
    {
        return payload.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? "RuntimeHost reported an error."
            : "RuntimeHost reported an error.";
    }

    private sealed record RuntimePipeMessage(string Type, JsonElement Payload);
}

internal sealed record RuntimeReadyPayload(
    string ApiBaseUri,
    string PostgresHost,
    int PostgresPort,
    int RuntimeHostProcessId,
    int ApiProcessId,
    int PostgresProcessId);
