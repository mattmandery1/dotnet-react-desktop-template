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
    private readonly PostgreSqlContainer _postgresContainer =
        new PostgreSqlBuilder()
            .WithImage("postgres:17")
            .WithDatabase("dotnet10template_tests")
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

        var response = await client.GetAsync("/api/hello");

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(
            "Hello World, the names are Matt, Tony, Bob",
            content);
    }
}