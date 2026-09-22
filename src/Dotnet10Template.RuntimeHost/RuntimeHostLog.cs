using System;
using System.IO;

namespace Dotnet10Template.RuntimeHost;

internal sealed class RuntimeHostLog
{
    private readonly object sync = new();
    private readonly string logFilePath;

    public RuntimeHostLog(string applicationDataDirectory)
    {
        logFilePath = Path.Combine(applicationDataDirectory, "runtime-host.log");
    }

    public void Append(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
                File.AppendAllText(
                    logFilePath,
                    $"[{DateTimeOffset.Now:u}] {line}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
