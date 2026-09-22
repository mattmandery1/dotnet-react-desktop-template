using System;
using System.Linq;
using System.Reflection;

namespace PokerTrainer.RuntimeHost;

internal static class ProductIdentity
{
    public static string ShortName { get; } = GetMetadata("ProductShortName", "PokerTrainer");

    public static string DataFolderName { get; } = GetMetadata("ProductDataFolderName", "PokerTrainer");

    public static string EnvPrefix { get; } = GetMetadata("ProductEnvPrefix", "POKERTRAINER");

    public static string PostgresDatabaseName { get; } = GetMetadata("PostgresDatabaseName", "poker-trainer");

    public static string ApiExecutableName { get; } = GetMetadata("ApiExecutableName", "PokerTrainer.Api");

    public static string RuntimeHostExecutableName { get; } =
        GetMetadata("RuntimeHostExecutableName", "PokerTrainer.RuntimeHost");

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
