using System;
using System.Linq;
using System.Reflection;

namespace Dotnet10Template.Desktop.Hosting;

internal static class ProductIdentity
{
    public static string ShortName { get; } = GetMetadata("ProductShortName", "Dotnet10Template");

    public static string DisplayName { get; } = GetMetadata("ProductDisplayName", "Dotnet10Template Desktop");

    public static string DataFolderName { get; } = GetMetadata("ProductDataFolderName", "Dotnet10Template");

    public static string EnvPrefix { get; } = GetMetadata("ProductEnvPrefix", "DOTNET10TEMPLATE");

    public static string PostgresDatabaseName { get; } = GetMetadata("PostgresDatabaseName", "dotnet10template");

    public static string ApiExecutableName { get; } = GetMetadata("DesktopApiExecutableName", "Dotnet10Template.Api");

    public static string GetEnvironmentVariableName(string suffix) =>
        $"{EnvPrefix}_{suffix}";

    private static string GetMetadata(string key, string fallback)
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(key, StringComparison.Ordinal))
            ?.Value;

        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value;
    }
}
