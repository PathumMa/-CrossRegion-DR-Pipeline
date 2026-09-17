var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Root welcome page - confirms the app is alive and deployed correctly
app.MapGet("/", () => Results.Content("""
    <!DOCTYPE html>
    <html>
    <head>
        <title>Cross-Region DR Pipeline - Demo App</title>
        <style>
            body { font-family: Segoe UI, Arial, sans-serif; background: #21295C; color: #fff; text-align: center; padding-top: 80px; }
            h1 { font-size: 2rem; }
            p { color: #9ab; }
            a { color: #1C7293; }
        </style>
    </head>
    <body>
        <h1>Cross-Region DR Pipeline &mdash; Demo Application</h1>
        <p>This is a sample ASP.NET Web API used to validate the CI/CD pipeline.</p>
        <p>Try the sample API: <a href="/weatherforecast" style="color:#7fd0e0;">/weatherforecast</a></p>
        <p>API docs: <a href="/swagger" style="color:#7fd0e0;">/swagger</a></p>
    </body>
    </html>
    """, "text/html"));

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