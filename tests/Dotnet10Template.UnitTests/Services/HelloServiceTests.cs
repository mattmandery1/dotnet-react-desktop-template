using Dotnet10Template.Application.Interfaces;
using Dotnet10Template.Application.Services;
using Dotnet10Template.Domain.Entities;
using Microsoft.Extensions.Logging;
using Moq;

namespace Dotnet10Template.UnitTests.Services;

public sealed class HelloServiceTests
{
    [Fact]
    public async Task GetGreetingAsync_WithPeople_ReturnsFormattedGreeting()
    {
        var people = new List<Person>
        {
            new() { Id = 1, Name = "Matt" },
            new() { Id = 2, Name = "Tony" },
            new() { Id = 3, Name = "Bob" }
        };

        var repository = new Mock<IPersonRepository>();

        repository
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(people);

        var logger = Mock.Of<ILogger<HelloService>>();

        var service = new HelloService(
            repository.Object,
            logger);

        var result = await service.GetGreetingAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "Hello World, the names are Matt, Tony, Bob",
            result);
    }
}