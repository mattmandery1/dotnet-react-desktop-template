using Dotnet10Template.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Dotnet10Template.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class HelloController(
    HelloService helloService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<string>> Get(
        CancellationToken cancellationToken)
    {
        var greeting =
            await helloService.GetGreetingAsync(cancellationToken);

        return Ok(greeting);
    }
}