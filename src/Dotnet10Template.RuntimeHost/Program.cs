using Dotnet10Template.RuntimeHost;
using System;
using System.Threading;

var options = RuntimeHostOptions.Parse(args);
var log = new RuntimeHostLog(options.ApplicationDataDirectory);
log.Append("RuntimeHost starting.");

using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
RuntimeHostPipe? pipe = null;

try
{
    pipe = new RuntimeHostPipe(options.PipeName, log);
    await pipe.ConnectAsync(startupTimeout.Token);

    using var desktopProcess = options.GetDesktopProcess();
    using var jobObject = new WindowsJobObject(log);
    await using var supervisor = new RuntimeSupervisor(options, log, jobObject);

    var ready = await supervisor.StartAsync(startupTimeout.Token);
    await pipe.SendReadyAsync(ready, CancellationToken.None);

    using var shutdownRequested = new CancellationTokenSource();
    var pipeShutdown = pipe.WaitForShutdownRequestAsync(shutdownRequested.Token);
    var desktopExit = supervisor.WaitForDesktopExitAsync(desktopProcess, shutdownRequested.Token);
    var criticalChildExit = supervisor.WaitForCriticalChildExitAsync(shutdownRequested.Token);

    var completed = await Task.WhenAny(pipeShutdown, desktopExit, criticalChildExit);
    shutdownRequested.Cancel();
    var sendShutdownComplete = false;

    if (completed == pipeShutdown)
    {
        try
        {
            await pipeShutdown;
            log.Append("Normal shutdown request won RuntimeHost shutdown race.");
            sendShutdownComplete = true;
        }
        catch (System.IO.IOException ex)
        {
            log.Append($"Desktop control pipe closed before shutdown request completed: {ex.Message}");
        }
    }
    else if (completed == criticalChildExit)
    {
        var childExit = await criticalChildExit;
        var fatalMessage =
            $"{childExit.ProcessKind} process PID {childExit.ProcessId} exited unexpectedly with code {childExit.ExitCode}. The local runtime session is unhealthy and must be restarted.";
        log.Append(fatalMessage);
        try
        {
            await pipe.SendFatalAsync(fatalMessage, CancellationToken.None);
            log.Append("Fatal runtime failure message sent to Desktop.");
        }
        catch (Exception ex)
        {
            log.Append($"Unable to send fatal runtime failure message to Desktop: {ex.Message}");
        }
    }

    await supervisor.StopAsync();
    if (sendShutdownComplete)
    {
        await pipe.SendShutdownCompleteAsync(CancellationToken.None);
    }

    log.Append("RuntimeHost exiting after cleanup.");
    return 0;
}
catch (Exception ex)
{
    log.Append($"RuntimeHost failed: {ex}");
    if (pipe is { IsConnected: true })
    {
        try
        {
            await pipe.SendErrorAsync(ex.Message, CancellationToken.None);
        }
        catch
        {
        }
    }

    return 1;
}
finally
{
    if (pipe is not null)
    {
        await pipe.DisposeAsync();
    }
}
