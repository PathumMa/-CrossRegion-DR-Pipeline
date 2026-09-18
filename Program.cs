var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Swagger only enabled in Development (set via ASPNETCORE_ENVIRONMENT) —
// deliberately not exposed by default in Production, per project security notes.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Root welcome page - confirms the app is alive, and shows which region/build is live.
app.MapGet("/", () =>
{
    var region = Environment.GetEnvironmentVariable("REGION") ?? "Unknown region";
    var version = Environment.GetEnvironmentVariable("APP_VERSION") ?? "local-dev";
    var now = DateTime.UtcNow;

    var html = $"""
        <!DOCTYPE html>
        <html>
        <head>
            <title>Cross-Region DR Pipeline - Demo App</title>
            <style>
                body {{ font-family: Segoe UI, Arial, sans-serif; background: #21295C; color: #fff; text-align: center; padding-top: 60px; }}
                h1 {{ font-size: 2rem; }}
                p {{ color: #9ab; }}
                a {{ color: #7fd0e0; }}
                .meta {{ margin-top: 40px; display: inline-block; text-align: left; background: rgba(255,255,255,0.06); border-radius: 8px; padding: 20px 30px; }}
                .meta div {{ margin: 6px 0; font-size: 0.95rem; }}
                .label {{ color: #7fd0e0; font-weight: 600; display: inline-block; width: 110px; }}
            </style>
        </head>
        <body>
            <h1>Cross-Region DR Pipeline &mdash; Demo Application</h1>
            <p>This is a sample ASP.NET Web API used to validate the CI/CD pipeline.</p>
            <p>Try the sample API: <a href="/weatherforecast">/weatherforecast</a> &nbsp;|&nbsp; API docs: <a href="/swagger">/swagger</a></p>

            <div class="meta">
                <div><span class="label">Region:</span> {region}</div>
                <div><span class="label">Version:</span> {version}</div>
                <div><span class="label">Server time (UTC):</span> {now:yyyy-MM-dd HH:mm:ss}</div>
            </div>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html");
});

// Sample API endpoint (kept from the default template)
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild",
    "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
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

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}