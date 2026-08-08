using Dotnet10Template.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Dotnet10Template.Application.Services;

public sealed class HelloService(
    IPersonRepository personRepository,
    ILogger<HelloService> logger)
{
    public async Task<string> GetGreetingAsync(
        CancellationToken cancellationToken = default)
    {
        var people = await personRepository.GetAllAsync(cancellationToken);

        var names = string.Join(", ", people.Select(x => x.Name));

        logger.LogInformation(
            "Creating greeting for {PersonCount} people",
            people.Count);

        return $"Hello World, the names are {names}";
    }
}