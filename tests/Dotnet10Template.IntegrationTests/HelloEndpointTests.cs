using System.Reflection;
using DotNet.Testcontainers.Builders;
using Dotnet10Template.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace Dotnet10Template.IntegrationTests;

public sealed class HelloEndpointTests : IAsyncLifetime
{
    private static readonly string TestDatabaseName =
        $"{GetProductMetadata("PostgresDatabaseName", "dotnet10template")}_tests";

    private readonly PostgreSqlContainer _postgresContainer =
        new PostgreSqlBuilder("postgres:17")
            .WithDatabase(TestDatabaseName)
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

    public async ValueTask InitializeAsync()
    {
        await _postgresContainer.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
    }

    [Fact]
    public async Task GetHello_ReturnsGreetingFromDatabase()
    {
        await using var factory =
            new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<
                            DbContextOptions<AppDbContext>>();

                        services.AddDbContext<AppDbContext>(options =>
                            options.UseNpgsql(
                                _postgresContainer.GetConnectionString()));
                    });
                });

        var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/hello",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "Hello World, the names are Matt, Tony, Bob",
            content);
    }

    private static string GetProductMetadata(string key, string fallback)
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
