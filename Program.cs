using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ---------- Front Door only ----------
var frontDoorId = Environment.GetEnvironmentVariable("FRONTDOOR_ID");

app.Use(async (context, next) =>
{
    // Local development: no Front Door in front, so skip the check.
    // /health stays open for the pipeline smoke test and Front Door probes.
    if (app.Environment.IsDevelopment() || context.Request.Path == "/health")
    {
        await next();
        return;
    }

    var incoming = context.Request.Headers["X-Azure-FDID"].ToString();

    if (string.IsNullOrEmpty(frontDoorId) ||
        !string.Equals(incoming, frontDoorId, StringComparison.OrdinalIgnoreCase))
    {
        app.Logger.LogWarning(
            "Blocked request without valid Front Door ID. Path={Path} Client={Client}",
            context.Request.Path, context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden");
        return;
    }

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ---------- MongoDB ----------
var mongoUri = Environment.GetEnvironmentVariable("MONGODB_URI");
var dbName = Environment.GetEnvironmentVariable("MONGODB_DB") ?? "demoapp";

IMongoDatabase? db = null;
IMongoCollection<UserDoc>? users = null;
IMongoCollection<SessionDoc>? sessions = null;

if (!string.IsNullOrEmpty(mongoUri))
{
    var settings = MongoClientSettings.FromConnectionString(mongoUri);
    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
    var client = new MongoClient(settings);
    db = client.GetDatabase(dbName);
    users = db.GetCollection<UserDoc>("users");
    sessions = db.GetCollection<SessionDoc>("sessions");

    // Create indexes in the background so a slow database never blocks startup.
    _ = Task.Run(async () =>
    {
        try
        {
            await users.Indexes.CreateOneAsync(new CreateIndexModel<UserDoc>(
                Builders<UserDoc>.IndexKeys.Ascending(u => u.Email),
                new CreateIndexOptions { Unique = true }));
            await sessions.Indexes.CreateOneAsync(new CreateIndexModel<SessionDoc>(
                Builders<SessionDoc>.IndexKeys.Ascending(s => s.TokenHash),
                new CreateIndexOptions { Unique = true }));
            await sessions.Indexes.CreateOneAsync(new CreateIndexModel<SessionDoc>(
                Builders<SessionDoc>.IndexKeys.Ascending(s => s.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Index creation failed");
        }
    });
}

// ---------- Helpers ----------
string Region() => Environment.GetEnvironmentVariable("REGION") ?? "Unknown region";

static string Hash(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

// Finds the session for this request's cookie, refreshes it (sliding 8-hour expiry)
// and returns the record as it was BEFORE this request, so we can see which region
// served the previous request.
async Task<SessionDoc?> GetSessionAsync(HttpContext ctx)
{
    if (sessions is null) return null;
    if (!ctx.Request.Cookies.TryGetValue("sid", out var token) || string.IsNullOrEmpty(token))
        return null;

    var hash = Hash(token);
    var now = DateTime.UtcNow;
    var update = Builders<SessionDoc>.Update
        .Set(s => s.LastSeenUtc, now)
        .Set(s => s.LastSeenRegion, Region())
        .Set(s => s.ExpiresAt, now.AddHours(8));

    return await sessions.FindOneAndUpdateAsync(
        s => s.TokenHash == hash && s.ExpiresAt > now,
        update,
        new FindOneAndUpdateOptions<SessionDoc> { ReturnDocument = ReturnDocument.Before });
}

// ---------- Auth endpoints ----------
app.MapPost("/api/auth/register", async (Credentials body) =>
{
    if (users is null) return Results.Json(new { error = "Database not configured." }, statusCode: 503);

    var email = body.Email?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') ||
        body.Password is null || body.Password.Length < 8 || body.Password.Length > 128)
    {
        return Results.BadRequest(new { error = "Valid email and a password of 8 to 128 characters are required." });
    }

    try
    {
        await users.InsertOneAsync(new UserDoc
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(body.Password),
            CreatedAtUtc = DateTime.UtcNow
        });
    }
    catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
    {
        return Results.Conflict(new { error = "That email is already registered." });
    }

    return Results.Ok(new { message = "Registered. You can sign in now." });
});

app.MapPost("/api/auth/login", async (Credentials body, HttpContext ctx) =>
{
    if (users is null || sessions is null)
        return Results.Json(new { error = "Database not configured." }, statusCode: 503);

    var email = body.Email?.Trim().ToLowerInvariant() ?? "";
    var user = await users.Find(u => u.Email == email).FirstOrDefaultAsync();

    if (user is null || !BCrypt.Net.BCrypt.Verify(body.Password ?? "", user.PasswordHash))
        return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);

    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    var now = DateTime.UtcNow;
    await sessions.InsertOneAsync(new SessionDoc
    {
        TokenHash = Hash(token),
        UserEmail = user.Email,
        CreatedRegion = Region(),
        LastSeenRegion = Region(),
        CreatedAtUtc = now,
        LastSeenUtc = now,
        ExpiresAt = now.AddHours(8)
    });

    ctx.Response.Cookies.Append("sid", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = DateTimeOffset.UtcNow.AddHours(8)
    });

    return Results.Ok(new { message = "Signed in.", region = Region() });
});

app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    if (sessions is not null &&
        ctx.Request.Cookies.TryGetValue("sid", out var token) && !string.IsNullOrEmpty(token))
    {
        var hash = Hash(token);
        await sessions.DeleteOneAsync(s => s.TokenHash == hash);
    }
    ctx.Response.Cookies.Delete("sid");
    return Results.Ok(new { message = "Signed out." });
});

app.MapGet("/api/me", async (HttpContext ctx) =>
{
    var s = await GetSessionAsync(ctx);
    if (s is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

    return Results.Ok(new
    {
        email = s.UserEmail,
        sessionCreatedInRegion = s.CreatedRegion,
        previousRequestServedBy = s.LastSeenRegion,
        thisRequestServedBy = Region(),
        sessionCreatedUtc = s.CreatedAtUtc
    });
});

// Database connectivity check. Deliberately NOT used by Front Door's probe,
// so a database problem never triggers a pointless region failover.
app.MapGet("/health/db", async () =>
{
    if (db is null) return Results.Json(new { status = "Unhealthy", database = "not configured" }, statusCode: 503);

    var sw = Stopwatch.StartNew();
    try
    {
        await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        return Results.Ok(new { status = "Healthy", database = "reachable", latencyMs = sw.ElapsedMilliseconds, region = Region() });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database ping failed");
        return Results.Json(new { status = "Unhealthy", database = "unreachable" }, statusCode: 503);
    }
});

// ---------- Login page ----------
app.MapGet("/login", () =>
{
    var html = """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">
            <title>Sign in - Cross-Region DR Demo</title>
            <style>
                body { font-family: Segoe UI, Arial, sans-serif; background: #21295C; color: #fff; padding: 40px; }
                .card { max-width: 640px; margin: 0 auto; background: rgba(255,255,255,0.06); border-radius: 8px; padding: 24px 30px; }
                input { width: 100%; padding: 8px; margin: 6px 0 12px; border-radius: 4px; border: 0; box-sizing: border-box; }
                button { padding: 8px 14px; margin: 4px 6px 4px 0; border: 0; border-radius: 4px; background: #7fd0e0; cursor: pointer; }
                pre { background: #0f1433; padding: 12px; border-radius: 6px; max-height: 320px; overflow: auto; font-size: 0.8rem; white-space: pre-wrap; word-break: break-word; }
                a { color: #7fd0e0; }
            </style>
        </head>
        <body>
            <div class="card">
                <h2>Session demo</h2>
                <label>Email</label><input id="email" type="email" autocomplete="username">
                <label>Password (min 8 characters)</label><input id="password" type="password" autocomplete="current-password">
                <button id="register">Register</button>
                <button id="login">Sign in</button>
                <button id="me">Check session</button>
                <button id="copy">Copy log</button>
                <button id="logout">Sign out</button>
                <div><label><input id="poll" type="checkbox" style="width:auto"> Check session every 2 seconds</label></div>
                <pre id="out">Log appears here...</pre>
                <a href="/">Back to home</a>
            </div>
            <script>
                const out = document.getElementById('out');
                const lines = [];
                function show(status, data) {
                    let text;
                    if (data && data.thisRequestServedBy) {
                        text = 'created=' + data.sessionCreatedInRegion + ' | previous=' + data.previousRequestServedBy + ' | now=' + data.thisRequestServedBy;
                    } else {
                        text = JSON.stringify(data);
                    }
                    lines.unshift(new Date().toISOString().slice(11, 23) + '  ' + status + '  ' + text);
                    if (lines.length > 200) lines.pop();
                    out.textContent = lines.slice(0, 40).join('\n');
                }
                async function call(method, url, body) {
                    try {
                        const r = await fetch(url, {
                            method: method,
                            headers: { 'Content-Type': 'application/json' },
                            body: body ? JSON.stringify(body) : undefined,
                            credentials: 'same-origin',
                            signal: AbortSignal.timeout(5000)
                        });
                        const text = await r.text();
                        let data;
                        try { data = JSON.parse(text); } catch (e) { data = text.slice(0, 120); }
                        show(r.status, data);
                    } catch (e) {
                        show('FAILED', String(e).slice(0, 80));
                    }
                }
                function creds() {
                    return { email: document.getElementById('email').value, password: document.getElementById('password').value };
                }
                document.getElementById('register').onclick = () => call('POST', '/api/auth/register', creds());
                document.getElementById('login').onclick = () => call('POST', '/api/auth/login', creds());
                document.getElementById('me').onclick = () => call('GET', '/api/me');
                document.getElementById('logout').onclick = () => call('POST', '/api/auth/logout');
                document.getElementById('copy').onclick = () => navigator.clipboard.writeText(lines.slice().reverse().join('\n'));
                let timer = null;
                document.getElementById('poll').onchange = (e) => {
                    if (e.target.checked) { timer = setInterval(() => call('GET', '/api/me'), 2000); }
                    else { clearInterval(timer); }
                };
            </script>
        </body>
        </html>
        """;
    return Results.Content(html, "text/html");
});

// ---------- Home page ----------
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
            <p>Try: <a href="/login">/login</a> &nbsp;|&nbsp; <a href="/weatherforecast">/weatherforecast</a> &nbsp;|&nbsp; Health details: <a href="/health">/health</a></p>

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

record Credentials(string? Email, string? Password);

class UserDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

class SessionDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public string TokenHash { get; set; } = "";
    public string UserEmail { get; set; } = "";
    public string CreatedRegion { get; set; } = "";
    public string LastSeenRegion { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime ExpiresAt { get; set; }
}