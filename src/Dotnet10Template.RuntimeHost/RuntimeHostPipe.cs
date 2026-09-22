using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dotnet10Template.RuntimeHost;

internal sealed class RuntimeHostPipe : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly NamedPipeClientStream pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private readonly RuntimeHostLog log;

    public RuntimeHostPipe(string pipeName, RuntimeHostLog log)
    {
        this.log = log;
        pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await pipe.ConnectAsync(TimeSpan.FromSeconds(15), cancellationToken);
        reader = new StreamReader(pipe);
        writer = new StreamWriter(pipe)
        {
            AutoFlush = true
        };
        log.Append("RuntimeHost connected to Desktop control pipe.");
    }

    public bool IsConnected => pipe.IsConnected;

    public async Task SendReadyAsync(RuntimeReadyMessage message, CancellationToken cancellationToken)
    {
        await WriteAsync(new RuntimePipeMessage("ready", message), cancellationToken);
    }

    public async Task SendErrorAsync(string message, CancellationToken cancellationToken)
    {
        await WriteAsync(new RuntimePipeMessage("error", new RuntimeErrorMessage(message)), cancellationToken);
    }

    public async Task SendFatalAsync(string message, CancellationToken cancellationToken)
    {
        await WriteAsync(new RuntimePipeMessage("fatal", new RuntimeErrorMessage(message)), cancellationToken);
    }

    public async Task SendShutdownCompleteAsync(CancellationToken cancellationToken)
    {
        await WriteAsync(new RuntimePipeMessage("shutdown-complete", new { }), cancellationToken);
    }

    public async Task WaitForShutdownRequestAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await GetReader().ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new IOException("Desktop control pipe closed before shutdown was requested.");
            }

            using var document = JsonDocument.Parse(line);
            var type = document.RootElement.GetProperty("type").GetString();
            if (string.Equals(type, "shutdown", StringComparison.Ordinal))
            {
                log.Append("Normal shutdown request received from Desktop.");
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (writer is not null)
        {
            await writer.DisposeAsync();
        }

        reader?.Dispose();
        await pipe.DisposeAsync();
    }

    private async Task WriteAsync<T>(T message, CancellationToken cancellationToken)
    {
        if (writer is null)
        {
            throw new InvalidOperationException("RuntimeHost control pipe writer has not been initialized.");
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(message, SerializerOptions).AsMemory(), cancellationToken);
    }

    private StreamReader GetReader()
    {
        return reader ?? throw new InvalidOperationException("RuntimeHost control pipe reader has not been initialized.");
    }

    private sealed record RuntimePipeMessage(string Type, object Payload);
}

internal sealed record RuntimeReadyMessage(
    string ApiBaseUri,
    string PostgresHost,
    int PostgresPort,
    int RuntimeHostProcessId,
    int ApiProcessId,
    int PostgresProcessId);

internal sealed record RuntimeErrorMessage(string Message);
