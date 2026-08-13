using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Dotnet10Template.RuntimeHost;

internal sealed class RuntimeSupervisor : IAsyncDisposable
{
    private readonly RuntimeHostOptions options;
    private readonly RuntimeHostLog log;
    private readonly WindowsJobObject jobObject;
    private PostgresSupervisor? postgres;
    private ApiSupervisor? api;
    private bool shutdownExpected;
    private bool stopStarted;
    private bool disposed;

    public RuntimeSupervisor(
        RuntimeHostOptions options,
        RuntimeHostLog log,
        WindowsJobObject jobObject)
    {
        this.options = options;
        this.log = log;
        this.jobObject = jobObject;
    }

    public async Task<RuntimeReadyMessage> StartAsync(CancellationToken cancellationToken)
    {
        log.Append($"RuntimeHost PID {Environment.ProcessId} supervising Desktop PID {options.DesktopProcessId}.");
        log.Append($"Product data directory: {options.ApplicationDataDirectory}");

        postgres = new PostgresSupervisor(options.ApplicationDataDirectory, log, jobObject);
        await postgres.StartAsync(cancellationToken);

        api = new ApiSupervisor(options.ApplicationDataDirectory, postgres.Endpoint, log, jobObject);
        await api.StartAsync(cancellationToken);

        log.Append($"Runtime endpoints: PostgreSQL {postgres.Endpoint.Host}:{postgres.Endpoint.Port}; API {api.BaseUri}.");

        return new RuntimeReadyMessage(
            api.BaseUri.ToString(),
            postgres.Endpoint.Host,
            postgres.Endpoint.Port,
            Environment.ProcessId,
            api.ProcessId,
            postgres.ProcessId);
    }

    public async Task WaitForDesktopExitAsync(Process desktopProcess, CancellationToken cancellationToken)
    {
        try
        {
            await desktopProcess.WaitForExitAsync(cancellationToken);
            log.Append($"Desktop PID {options.DesktopProcessId} unexpectedly disappeared; RuntimeHost cleanup starting.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task<CriticalChildExit> WaitForCriticalChildExitAsync(CancellationToken cancellationToken)
    {
        var postgresExit = postgres?.WaitForExitAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL has not started.");
        var apiExit = api?.WaitForExitAsync(cancellationToken)
            ?? throw new InvalidOperationException("API has not started.");

        var completed = await Task.WhenAny(postgresExit, apiExit);
        var childExit = await completed;

        if (!shutdownExpected)
        {
            log.Append(
                $"Unexpected {childExit.ProcessKind} exit detected for owned PID {childExit.ProcessId}. Exit code: {childExit.ExitCode}.");
        }

        return childExit;
    }

    public async Task StopAsync()
    {
        shutdownExpected = true;

        if (stopStarted)
        {
            log.Append("RuntimeHost cleanup already started; duplicate stop request ignored.");
            return;
        }

        stopStarted = true;
        log.Append("RuntimeHost graceful cleanup starting.");

        if (api is not null)
        {
            await api.StopAsync();
            log.Append("API shutdown result: completed.");
        }

        if (postgres is not null)
        {
            await postgres.StopAsync();
            log.Append("PostgreSQL shutdown result: completed.");
        }

        log.Append("RuntimeHost graceful cleanup complete.");
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
}

internal sealed record CriticalChildExit(
    string ProcessKind,
    int ProcessId,
    int ExitCode);
