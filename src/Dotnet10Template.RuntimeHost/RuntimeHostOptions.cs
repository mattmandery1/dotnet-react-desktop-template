using System;
using System.Diagnostics;
using System.IO;

namespace Dotnet10Template.RuntimeHost;

internal sealed record RuntimeHostOptions(
    int DesktopProcessId,
    string PipeName,
    string ApplicationDataDirectory)
{
    public static RuntimeHostOptions Parse(string[] args)
    {
        int? desktopPid = null;
        string? pipeName = null;
        string? applicationDataDirectory = null;

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{name}'.");
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Argument '{name}' requires a value.");
            }

            var value = args[++index];
            switch (name)
            {
                case "--desktop-pid":
                    if (!int.TryParse(value, out var parsedPid) || parsedPid <= 0)
                    {
                        throw new ArgumentException("--desktop-pid must be a positive process id.");
                    }

                    desktopPid = parsedPid;
                    break;
                case "--pipe":
                    pipeName = value;
                    break;
                case "--data-dir":
                    applicationDataDirectory = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{name}'.");
            }
        }

        if (desktopPid is null)
        {
            throw new ArgumentException("--desktop-pid is required.");
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("--pipe is required.");
        }

        if (string.IsNullOrWhiteSpace(applicationDataDirectory))
        {
            throw new ArgumentException("--data-dir is required.");
        }

        return new RuntimeHostOptions(
            desktopPid.Value,
            pipeName,
            Path.GetFullPath(applicationDataDirectory));
    }

    public Process GetDesktopProcess()
    {
        try
        {
            return Process.GetProcessById(DesktopProcessId);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Desktop process PID {DesktopProcessId} is not running.", ex);
        }
    }
}
