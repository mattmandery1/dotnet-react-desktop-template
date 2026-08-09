using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dotnet10Template.Infrastructure.Data;

public static class DatabaseExtensions
{
    public static async Task ApplyDatabaseMigrationsAsync(
        this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();

        await dbContext.Database.MigrateAsync();
    }
}