namespace Dotnet10Template.RuntimeHost;

internal sealed record RuntimeEndpoint(
    string Host,
    int Port,
    string Database,
    string User)
{
    public string CreateConnectionString() =>
        $"Host={Host};Port={Port};Database={Database};Username={User};Pooling=true";
}
