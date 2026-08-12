using System.Reflection;

internal static class ProductIdentity
{
    public static string PostgresDatabaseName { get; } =
        GetMetadata("PostgresDatabaseName", "dotnet10template");

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
