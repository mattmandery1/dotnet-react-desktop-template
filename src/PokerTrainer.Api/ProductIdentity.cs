using System.Reflection;

internal static class ProductIdentity
{
    public static string EnvPrefix { get; } =
        GetMetadata("ProductEnvPrefix", "POKERTRAINER");

    public static string PostgresDatabaseName { get; } =
        GetMetadata("PostgresDatabaseName", "poker-trainer");

    public static string GetEnvironmentVariableName(string suffix) =>
        $"{EnvPrefix}_{suffix}";

    private static string GetMetadata(string key, string fallback)
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)
            ?.Value;

        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value;
    }
}
