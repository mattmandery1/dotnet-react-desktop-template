using PokerTrainer.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace PokerTrainer.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services)
    {
        services.AddScoped<HelloService>();

        return services;
    }
}