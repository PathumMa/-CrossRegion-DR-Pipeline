var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Only accept traffic that came through our Azure Front Door profile.
// Front Door adds the X-Azure-FDID header; /health is exempt so the pipeline
// smoke test and Front Door's own health probe keep working.
var frontDoorId = Environment.GetEnvironmentVariable("FRONTDOOR_ID");

app.Use(async (context, next) =>
{
    // Local development: no Front Door in front, so skip the check.
    if (app.Environment.IsDevelopment() || context.Request.Path == "/health")
    {
        await next();
        return;
    }

    var incoming = context.Request.Headers["X-Azure-FDID"].ToString();

    if (string.IsNullOrEmpty(frontDoorId) ||
        !string.Equals(incoming, frontDoorId, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden");
        return;
    }

    await next();
});

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

    var html = """
        <!DOCTYPE html>
        <html>
        <head>
            <title>Welcome - Cross-Region DR Pipeline - Demo App</title>
            <style>
                body { font-family: Segoe UI, Arial, sans-serif; background: #21295C; color: #fff; text-align: center; padding-top: 60px; }
                h1 { font-size: 2rem; }
                p { color: #9ab; }
                a { color: #7fd0e0; }
                .meta { margin-top: 40px; display: inline-block; text-align: left; background: rgba(255,255,255,0.06); border-radius: 8px; padding: 20px 30px; }
                .meta div { margin: 6px 0; font-size: 0.95rem; }
                .label { color: #7fd0e0; font-weight: 600; display: inline-block; width: 110px; }
            </style>
        </head>
        <body>
            <h1>Cross-Region DR &mdash; Demo Application</h1>
            <p>A demonstration API for showcasing cross-region deployments and service health.</p>
            <p>Try the sample API: <a href="/weatherforecast">/weatherforecast</a> &nbsp;|&nbsp; Health details: <a href="/health">/health</a> &nbsp;|&nbsp; API docs: <a href="/swagger">/swagger</a></p>

            <div class="meta">
                <div><span class="label">Region:</span> __REGION__</div>
                <div><span class="label">Version:</span> __VERSION__</div>
                <div><span class="label">Server time (UTC):</span> __TIME__</div>
            </div>
        </body>
        </html>
        """;

    html = html
        .Replace("__REGION__", region)
        .Replace("__VERSION__", version)
        .Replace("__TIME__", now.ToString("yyyy-MM-dd HH:mm:ss"));

    return Results.Content(html, "text/html");
});

app.MapGet("/health", () => TypedResults.Ok(new
{
    status = "Healthy",
    region = Environment.GetEnvironmentVariable("REGION") ?? "Unknown region",
    version = Environment.GetEnvironmentVariable("APP_VERSION") ?? "local-dev",
    commit = Environment.GetEnvironmentVariable("GIT_COMMIT") ?? "unknown",
    checkedAtUtc = DateTimeOffset.UtcNow
}))
    .WithName("GetHealth")
    .WithSummary("Check application health and deployment details")
    .WithDescription("Returns the application health status and deployment metadata.")
    .Produces(StatusCodes.Status200OK);

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