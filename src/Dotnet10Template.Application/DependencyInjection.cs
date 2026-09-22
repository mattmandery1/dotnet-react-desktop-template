using Dotnet10Template.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Dotnet10Template.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services)
    {
        services.AddScoped<HelloService>();

        return services;
    }
}