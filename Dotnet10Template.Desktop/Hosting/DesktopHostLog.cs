using System;
using System.IO;
using WinAppApplicationData = Microsoft.Windows.Storage.ApplicationData;

namespace Dotnet10Template.Desktop.Hosting;

internal static class DesktopHostLog
{
    private static readonly object Sync = new();

    public static string ApplicationDataDirectory => GetApplicationDataDirectory();

    public static void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            var logFilePath = Path.Combine(ApplicationDataDirectory, "desktop-host.log");

            lock (Sync)
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

        return Path.GetFullPath(Path.Combine(localAppData, "Dotnet10Template"));
    }
}
