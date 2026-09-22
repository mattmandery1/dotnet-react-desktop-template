using Dotnet10Template.Infrastructure;
using Dotnet10Template.Application;
using Dotnet10Template.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("DefaultConnection")))
{
    builder.Configuration["ConnectionStrings:DefaultConnection"] =
        $"Host=localhost;Port=5432;Database={ProductIdentity.PostgresDatabaseName};Username=postgres;Password=postgres";
}

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddHealthChecks();

var app = builder.Build();
await app.Services.ApplyDatabaseMigrationsAsync();
app.MapHealthChecks("/health");

app.MapControllers();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

StartDesktopOwnedShutdownMonitor(app);

await app.RunAsync();

static void StartDesktopOwnedShutdownMonitor(WebApplication app)
{
    var enabled = Environment.GetEnvironmentVariable(
        ProductIdentity.GetEnvironmentVariableName("DESKTOP_OWNED_API_STDIN_SHUTDOWN"));

    if (!string.Equals(enabled, "1", StringComparison.Ordinal))
    {
        return;
    }

    _ = Task.Run(async () =>
    {
        while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(app.Lifetime.ApplicationStopping);
            if (line is null)
            {
                app.Lifetime.StopApplication();
                return;
            }

            if (string.Equals(line, "shutdown", StringComparison.Ordinal))
            {
                app.Lifetime.StopApplication();
                return;
            }
        }
    });
}

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

/*
 * Expose the generated Program type to the test project.
 */
public partial class Program;
