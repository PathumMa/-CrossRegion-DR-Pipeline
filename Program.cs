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
        app.Logger.LogWarning(
            "Blocked request without valid Front Door ID. Path={Path} Client={Client}",
            context.Request.Path, context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden");
        return;
    }

    await next();
});

// Swagger only enabled in Development (set via ASPNETCORE_ENVIRONMENT) -
// deliberately not exposed by default in Production, per project security notes.
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

// ---------- Page layout ----------
const string BaseCss = """
        :root{--bg:#F3F5F9;--surface:#fff;--ink:#172036;--muted:#566079;--line:#D9DEE8;--navy:#21295C;--sea:#0F8B8D;--ea:#5B5BD6;--fail:#D0382F;--fail-tint:#FDEDEC;--ok-tint:#E6F5F4}
        *{box-sizing:border-box}
        [hidden]{display:none !important}
        body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,Roboto,sans-serif}
        .wrap{max-width:880px;margin:0 auto;padding:24px 20px 64px}
        header{display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap;margin-bottom:28px}
        .brand{font-weight:650;letter-spacing:-0.01em;color:var(--navy);text-decoration:none}
        nav a{color:var(--muted);text-decoration:none;margin-left:18px;font-size:.95rem}
        nav a:hover{color:var(--navy)}
        nav a[aria-current]{color:var(--navy);font-weight:600;border-bottom:2px solid var(--navy);padding-bottom:2px}
        h1{font-size:1.9rem;line-height:1.2;letter-spacing:-0.02em;margin:0 0 6px}
        h2{font-size:1.05rem;margin:0 0 12px}
        .lead{color:var(--muted);margin:0 0 24px;max-width:62ch}
        .muted{color:var(--muted);font-size:.9rem}
        .card{background:var(--surface);border:1px solid var(--line);border-radius:12px;padding:20px 22px;margin-bottom:16px}
        .hero{border-left:6px solid var(--line)}
        .hero[data-region="Southeast Asia"]{border-left-color:var(--sea)}
        .hero[data-region="East Asia"]{border-left-color:var(--ea)}
        .big{font-size:2.2rem;font-weight:650;letter-spacing:-0.02em;margin:2px 0 12px}
        .board{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin-bottom:12px}
        .tile{background:var(--surface);border:1px solid var(--line);border-left:6px solid var(--line);border-radius:12px;padding:14px 18px}
        .tile[data-region="Southeast Asia"]{border-left-color:var(--sea)}
        .tile[data-region="East Asia"]{border-left-color:var(--ea)}
        .tile[data-state="live"][data-region="Southeast Asia"]{background:var(--ok-tint)}
        .tile[data-state="live"][data-region="East Asia"]{background:#EDEDFB}
        .tile[data-state="down"]{background:var(--fail-tint);border-color:var(--fail)}
        .tile .name{font-size:1.2rem;font-weight:650;letter-spacing:-0.01em}
        .tile .role{color:var(--muted);font-size:.9rem}
        .tile .state{margin-top:8px;font-size:.95rem}
        .dot{display:inline-block;width:9px;height:9px;border-radius:50%;background:#AEB6C7;margin-right:8px}
        .tile[data-state="live"][data-region="Southeast Asia"] .dot{background:var(--sea)}
        .tile[data-state="live"][data-region="East Asia"] .dot{background:var(--ea)}
        .tile[data-state="down"] .dot{background:var(--fail)}
        .banner{display:none;border-radius:10px;padding:12px 16px;margin-bottom:12px;font-weight:550}
        .banner.show{display:block}
        .banner.fail{background:var(--fail-tint);color:#8F211B}
        .banner.ok{background:var(--ok-tint);color:#0B5557}
        .strip{display:flex;justify-content:flex-end;gap:3px;height:34px;background:var(--surface);border:1px solid var(--line);border-radius:10px;padding:6px 8px;overflow:hidden}
        .bar{flex:0 0 6px;border-radius:2px;background:var(--line)}
        .bar.sea{background:var(--sea)}.bar.ea{background:var(--ea)}.bar.fail{background:var(--fail)}
        .legend{display:flex;gap:16px;flex-wrap:wrap;color:var(--muted);font-size:.85rem;margin:8px 2px 16px}
        .legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px}
        label{display:block;font-weight:600;font-size:.9rem;margin:12px 0 4px}
        input[type=email],input[type=password]{width:100%;padding:10px 12px;border:1px solid #AEB6C7;border-radius:8px;font:inherit;background:#fff;color:var(--ink)}
        button,.btn{display:inline-block;font:inherit;font-weight:600;padding:9px 16px;border-radius:8px;border:1px solid var(--navy);background:var(--navy);color:#fff;cursor:pointer;text-decoration:none}
        button:hover,.btn:hover{filter:brightness(1.15)}
        .secondary{background:#fff;color:var(--navy)}
        .secondary:hover{background:#EEF0F8;filter:none}
        .quiet{background:transparent;border-color:transparent;color:var(--muted);font-weight:500}
        .quiet:hover{background:#EEF0F8;filter:none}
        button:disabled{opacity:.5;cursor:not-allowed}
        input:focus-visible,button:focus-visible,a:focus-visible{outline:3px solid #8EA2FF;outline-offset:2px}
        .row{display:flex;gap:10px;flex-wrap:wrap;align-items:center;margin-top:16px}
        .row.top{margin:0 0 12px}
        .spacer{flex:1}
        .switch{display:flex;align-items:center;gap:8px;margin:0;font-weight:500;color:var(--muted)}
        .msg{min-height:1.4em;margin-top:10px;font-size:.92rem}
        .msg.err{color:#A62A23}.msg.good{color:#0B6E70}
        .facts{display:grid;grid-template-columns:auto 1fr;gap:6px 18px;margin:8px 0 0}
        .facts dt{color:var(--muted)}.facts dd{margin:0;font-weight:600}
        .logbar{display:flex;justify-content:space-between;align-items:baseline}
        .log{background:#0F1633;color:#DDE3F5;border-radius:10px;padding:12px 14px;font:12.5px/1.6 Consolas,"Cascadia Mono",monospace;height:260px;overflow:auto;white-space:pre-wrap;word-break:break-word}
        .log:empty::before{content:attr(data-empty);color:#8190B8}
        .log .f{color:#FF9B93}.log .n{color:#8AA0FF}
        .explain{background:var(--surface);border:1px solid var(--line);border-radius:12px;padding:16px 20px;margin-bottom:16px}
        .explain h2{margin:0 0 6px}
        .explain p{margin:0 0 8px;color:var(--muted)}
        .steps{margin:6px 0 0;padding-left:22px}
        .steps li{margin:4px 0}
        .grid2{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin-bottom:16px}
        .linkcard{display:block;text-decoration:none;color:inherit;background:var(--surface);border:1px solid var(--line);border-radius:12px;padding:16px 20px}
        .linkcard:hover{border-color:var(--navy)}
        .linkcard .t{font-weight:650;font-size:1.05rem;color:var(--navy)}
        .linkcard .d{color:var(--muted);font-size:.92rem;margin-top:4px}
        .pill{display:inline-block;padding:2px 12px;border-radius:999px;background:#EEF0F8;color:var(--navy);font-size:.85rem;font-weight:600}
        .terms p{margin:0 0 10px}
        @media (max-width:640px){.board,.grid2{grid-template-columns:1fr}.big{font-size:1.8rem}}
        """;

string Page(string title, string active, string main, string script = "")
{
    string Nav(string href, string label, string key) =>
        "<a href=\"" + href + "\"" + (active == key ? " aria-current=\"page\"" : "") + ">" + label + "</a>";

    return """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>__TITLE__</title>
        <style>__CSS__</style>
        </head>
        <body>
        <div class="wrap">
        <header>
        <a class="brand" href="/">Cross-region DR demo</a>
        <nav>__NAV__</nav>
        </header>
        __MAIN__
        </div>
        <script>__SCRIPT__</script>
        </body>
        </html>
        """
        .Replace("__TITLE__", title)
        .Replace("__CSS__", BaseCss)
        .Replace("__NAV__", Nav("/", "Home", "home") + Nav("/failover", "Failover test", "session") + Nav("/data", "Data test", "rpo") + Nav("/health", "Health", "health"))
        .Replace("__MAIN__", main)
        .Replace("__SCRIPT__", script);
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
    if (s is null)
        return Results.Json(new { error = "Not signed in.", servedBy = Region() }, statusCode: 401);

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

// ---------- RPO test endpoints (used by the DR drill) ----------
// Every write is sent with a majority write concern. The drill records which writes were
// acknowledged (HTTP 200) and later checks that every one of them is still in the database.
var records = db?.GetCollection<RecordDoc>("records")
    .WithReadConcern(ReadConcern.Majority)
    .WithWriteConcern(WriteConcern.WMajority);

app.MapPost("/api/records", async (HttpContext ctx, RecordIn body) =>
{
    var s = await GetSessionAsync(ctx);
    if (s is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);
    if (records is null) return Results.Json(new { error = "Database not configured." }, statusCode: 503);
    if (string.IsNullOrWhiteSpace(body.RunId) || body.RunId.Length > 64)
        return Results.BadRequest(new { error = "runId is required (max 64 characters)." });

    await records.InsertOneAsync(new RecordDoc
    {
        RunId = body.RunId,
        Seq = body.Seq,
        Region = Region(),
        CreatedUtc = DateTime.UtcNow
    });
    return Results.Ok(new { ok = true, seq = body.Seq, region = Region() });
});

app.MapGet("/api/records", async (HttpContext ctx, string runId) =>
{
    var s = await GetSessionAsync(ctx);
    if (s is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);
    if (records is null) return Results.Json(new { error = "Database not configured." }, statusCode: 503);

    var docs = await records.Find(r => r.RunId == runId).Project(r => new { r.Seq, r.Region }).ToListAsync();
    var seqs = docs.Select(d => d.Seq).ToList();
    var byRegion = docs.GroupBy(d => d.Region).ToDictionary(g => g.Key, g => g.Count());
    return Results.Ok(new { runId, count = seqs.Count, seqs, byRegion });
});

// ---------- Data test page (durability of writes, RPO) ----------
app.MapGet("/rpo", () => Results.Redirect("/data"));
app.MapGet("/login", () => Results.Redirect("/failover"));

app.MapGet("/data", () =>
{
    var main = """
        <h1>Data test: durability of writes (RPO)</h1>
        <p class="lead">This test shows whether any data is lost when the primary region fails.</p>

        <section class="explain">
            <h2>What this test shows</h2>
            <p>The page writes one numbered record per second to the shared database. The server acknowledges a record only after a majority of the database replica set members have stored it. After the failover, Verify compares every acknowledged record with what is stored in the database.</p>
            <p>An acknowledged record that is missing would be data loss. Records that were not acknowledged because the region failed during the request are counted separately, because the client was told that they failed.</p>
            <h2 style="margin-top:12px">How to run it</h2>
            <ol class="steps">
                <li>Sign in on the <a href="/failover">Failover test</a> page.</li>
                <li>Click Start writing and wait about 20 seconds.</li>
                <li>Stop the primary Container App in the Azure portal. Keep writing while the standby takes over.</li>
                <li>Start the primary again and wait until the log shows writes served by Southeast Asia.</li>
                <li>Click Stop writing, wait 10 seconds, then click Verify.</li>
                <li>Click Copy log to save the evidence.</li>
            </ol>
        </section>

        <section class="card">
            <div class="row top">
                <button type="button" id="start">Start writing</button>
                <button type="button" id="stop" class="secondary" disabled>Stop writing</button>
                <button type="button" id="verify" class="secondary">Verify</button>
                <span class="spacer"></span>
                <span id="state" class="pill">Idle</span>
            </div>
            <dl class="facts">
                <dt>Records sent</dt><dd id="c-sent">0</dd>
                <dt>Acknowledged by the server</dt><dd id="c-acked">0</dd>
                <dt>No answer or error</dt><dd id="c-failed">0</dd>
                <dt>Last write served by</dt><dd id="c-region">-</dd>
            </dl>
        </section>

        <section class="card" id="result" hidden>
            <h2>Verification result</h2>
            <div id="verdict" class="banner" role="status"></div>
            <dl class="facts">
                <dt>Acknowledged and found</dt><dd id="r-ok"></dd>
                <dt>Acknowledged but missing</dt><dd id="r-missing"></dd>
                <dt>Observed data loss</dt><dd id="r-loss"></dd>
                <dt>Not acknowledged but stored</dt><dd id="r-late"></dd>
                <dt>Not acknowledged and not stored</dt><dd id="r-none"></dd>
                <dt>Stored by Southeast Asia</dt><dd id="r-sea"></dd>
                <dt>Stored by East Asia</dt><dd id="r-ea"></dd>
            </dl>
        </section>

        <section class="card">
            <div class="logbar"><h2>Write log</h2><span class="muted">Times are UTC</span></div>
            <div class="row top">
                <span class="spacer"></span>
                <button type="button" id="copy" class="quiet">Copy log</button>
                <button type="button" id="clear" class="quiet" title="Clears the log and the results, and starts a new run">Clear log</button>
            </div>
            <div id="log" class="log" data-empty="No writes yet. Click Start writing."></div>
        </section>
        """;

    var script = """
        (function () {
            const $ = (id) => document.getElementById(id);
            const logEl = $('log');
            const stamp = (d) => d.toISOString().slice(11, 23);
            const newRun = () => (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random()).replace(/[^a-zA-Z0-9]/g, '').slice(0, 32);
            let runId = newRun(), seq = 0, timer = null, lastRegion = null, lines = [];
            const sent = new Map();

            function addLog(text, cls) {
                const t = stamp(new Date());
                lines.push(t + '  ' + text);
                if (lines.length > 1000) lines.shift();
                const div = document.createElement('div');
                if (cls) div.className = cls;
                div.textContent = t + '  ' + text;
                logEl.appendChild(div);
                while (logEl.childNodes.length > 200) logEl.removeChild(logEl.firstChild);
                logEl.scrollTop = logEl.scrollHeight;
            }
            function render() {
                let acked = 0, failed = 0;
                sent.forEach((v) => { if (v.state === 'acked') acked++; else if (v.state === 'failed') failed++; });
                $('c-sent').textContent = sent.size;
                $('c-acked').textContent = acked;
                $('c-failed').textContent = failed;
            }
            function stopTimer() {
                clearInterval(timer);
                timer = null;
                $('start').disabled = false;
                $('stop').disabled = true;
            }

            async function writeOne() {
                const n = ++seq;
                const rec = { state: 'pending', ack: 0 };
                sent.set(n, rec);
                render();
                try {
                    const r = await fetch('/api/records', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ runId: runId, seq: n }),
                        credentials: 'same-origin',
                        signal: AbortSignal.timeout(8000)
                    });
                    let d = {};
                    try { d = await r.json(); } catch (e) {}
                    if (r.status === 200) {
                        rec.state = 'acked';
                        rec.ack = Date.now();
                        if (d.region !== lastRegion) {
                            addLog('#' + n + ' acknowledged by ' + d.region + (lastRegion ? '  (previous: ' + lastRegion + ')' : ''), 'n');
                            lastRegion = d.region;
                            $('c-region').textContent = d.region;
                        }
                    } else if (r.status === 401) {
                        rec.state = 'failed';
                        addLog('Not signed in. Sign in on the Failover test page, then come back.', 'f');
                        stopTimer();
                        $('state').textContent = 'Stopped';
                    } else {
                        rec.state = 'failed';
                        addLog('#' + n + ' failed: HTTP ' + r.status + (d.error ? '  ' + d.error : ''), 'f');
                    }
                } catch (e) {
                    rec.state = 'failed';
                    addLog('#' + n + ' failed: no response', 'f');
                }
                render();
            }

            async function verify() {
                $('verify').disabled = true;
                try {
                    const r = await fetch('/api/records?runId=' + encodeURIComponent(runId), {
                        credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(8000)
                    });
                    const d = await r.json();
                    if (r.status !== 200) throw new Error(d.error || ('HTTP ' + r.status));
                    const stored = new Set(d.seqs);
                    let ok = 0, late = 0, none = 0, pending = 0;
                    const missing = [], missingAck = [];
                    sent.forEach((v, n) => {
                        const has = stored.has(n);
                        if (v.state === 'acked') { if (has) ok++; else { missing.push(n); missingAck.push(v.ack); } }
                        else if (v.state === 'failed') { if (has) late++; else none++; }
                        else pending++;
                    });
                    const br = d.byRegion || {};
                    const loss = missing.length
                        ? ((Math.max.apply(null, missingAck) - Math.min.apply(null, missingAck)) / 1000).toFixed(1) + ' s span, ' + missing.length + ' records'
                        : '0 records (0 s)';

                    $('result').hidden = false;
                    $('r-ok').textContent = ok;
                    $('r-missing').textContent = missing.length ? missing.length + '  (#' + missing.slice(0, 10).join(', #') + ')' : '0';
                    $('r-loss').textContent = loss;
                    $('r-late').textContent = late;
                    $('r-none').textContent = none + (pending ? '  (' + pending + ' still pending, verify again)' : '');
                    $('r-sea').textContent = br['Southeast Asia'] || 0;
                    $('r-ea').textContent = br['East Asia'] || 0;

                    const v = $('verdict');
                    if (missing.length === 0) {
                        v.className = 'banner show ok';
                        v.textContent = 'No acknowledged record was lost. Observed data loss: 0 records (0 s).';
                    } else {
                        v.className = 'banner show fail';
                        v.textContent = missing.length + ' acknowledged records are missing from the database. This is data loss.';
                    }

                    addLog('VERIFY  sent=' + sent.size + '  acknowledged=' + (ok + missing.length) + '  acknowledged and found=' + ok + '  acknowledged but missing=' + missing.length, missing.length ? 'f' : 'n');
                    addLog('VERIFY  not acknowledged but stored=' + late + '  not acknowledged and not stored=' + none + '  pending=' + pending, 'n');
                    addLog('VERIFY  stored by Southeast Asia=' + (br['Southeast Asia'] || 0) + '  stored by East Asia=' + (br['East Asia'] || 0), 'n');
                    addLog('VERIFY  observed data loss: ' + loss, missing.length ? 'f' : 'n');
                } catch (e) {
                    addLog('VERIFY failed: ' + (e.message || e), 'f');
                }
                $('verify').disabled = false;
            }

            $('start').onclick = () => {
                if (timer) return;
                $('start').disabled = true;
                $('stop').disabled = false;
                $('state').textContent = 'Writing';
                addLog('Run ' + runId + ': writing one record per second');
                writeOne();
                timer = setInterval(writeOne, 1000);
            };
            $('stop').onclick = () => {
                stopTimer();
                $('state').textContent = 'Stopped';
                addLog('Stopped. Wait about 10 seconds for pending requests, then click Verify.');
            };
            $('verify').onclick = verify;
            $('copy').onclick = async () => {
                try { await navigator.clipboard.writeText('Run ' + runId + '\n' + lines.join('\n')); $('copy').textContent = 'Copied'; }
                catch (e) { $('copy').textContent = 'Copy failed'; }
                setTimeout(() => { $('copy').textContent = 'Copy log'; }, 1500);
            };
            $('clear').onclick = () => {
                stopTimer();
                runId = newRun(); seq = 0; lastRegion = null; lines = [];
                sent.clear();
                logEl.textContent = '';
                $('result').hidden = true;
                $('c-region').textContent = '-';
                $('state').textContent = 'Idle';
                render();
            };
        })();
        """;

    return Results.Content(Page("Data test", "rpo", main, script), "text/html");
});

// ---------- Failover test page (availability and sessions, RTO) ----------
app.MapGet("/failover", () =>
{
    var main = """
        <h1>Failover test: availability and sessions (RTO)</h1>
        <p class="lead">This test shows how long the application is unavailable when the primary region fails, and whether a signed-in user stays signed in.</p>

        <section class="explain">
            <h2>What this test shows</h2>
            <p>The page checks your session through Azure Front Door every 2 seconds. Sessions are stored in the shared database, so the standby region can continue a session that was created in the primary region. The outage length in the banner is the time from the first failed request to the first answer from a healthy region. The automated drill pipeline uses a stricter rule, so quote its figures in the report.</p>
            <h2 style="margin-top:12px">How to run it</h2>
            <ol class="steps">
                <li>Create an account or sign in.</li>
                <li>Turn on Check session every 2 seconds.</li>
                <li>Stop the primary Container App in the Azure portal.</li>
                <li>Watch the primary tile turn red and the standby take over. A banner reports the outage length.</li>
                <li>Start the primary again to see failback, then click Copy log to save the evidence.</li>
            </ol>
        </section>

        <div id="banner" class="banner" role="status" aria-live="polite"></div>

        <div class="board">
            <div class="tile" data-region="Southeast Asia" data-state="idle">
                <div class="name">Southeast Asia</div><div class="role">Primary</div>
                <div class="state"><span class="dot"></span><span class="state-text">Waiting for a request</span></div>
            </div>
            <div class="tile" data-region="East Asia" data-state="idle">
                <div class="name">East Asia</div><div class="role">Failover</div>
                <div class="state"><span class="dot"></span><span class="state-text">Waiting for a request</span></div>
            </div>
        </div>
        <div id="strip" class="strip" aria-hidden="true"></div>
        <div class="legend">
            <span><i style="background:var(--sea)"></i>Southeast Asia</span>
            <span><i style="background:var(--ea)"></i>East Asia</span>
            <span><i style="background:var(--fail)"></i>No response</span>
            <span>Newest request on the right</span>
        </div>

        <section class="card">
            <div id="signedOut">
                <h2>Test account</h2>
                <form id="authForm" novalidate>
                    <label for="email">Email</label>
                    <input id="email" type="email" autocomplete="username" placeholder="demo.user@example.com">
                    <label for="password">Password</label>
                    <input id="password" type="password" autocomplete="current-password" placeholder="At least 8 characters">
                    <div class="row">
                        <button type="submit" id="login">Sign in</button>
                        <button type="button" id="register" class="secondary">Create account</button>
                    </div>
                    <div id="msg" class="msg" role="status"></div>
                </form>
            </div>
            <div id="signedIn" hidden>
                <h2>Signed in as <span id="who"></span></h2>
                <dl class="facts">
                    <dt>Session created in</dt><dd id="f-created"></dd>
                    <dt>Previous request served by</dt><dd id="f-prev"></dd>
                    <dt>This request served by</dt><dd id="f-now"></dd>
                </dl>
                <div class="row"><button type="button" id="logout" class="secondary">Sign out</button></div>
            </div>
        </section>

        <section class="card">
            <div class="logbar"><h2>Request log</h2><span class="muted">Times are UTC</span></div>
            <div class="row top">
                <label class="switch"><input type="checkbox" id="poll"> Check session every 2 seconds</label>
                <button type="button" id="check" class="secondary">Check now</button>
                <span class="spacer"></span>
                <button type="button" id="copy" class="quiet">Copy log</button>
                <button type="button" id="clear" class="quiet" title="Clears the log, the request strip and the banner">Clear log</button>
            </div>
            <div id="log" class="log" data-empty="No requests yet. Sign in, then turn on auto-check."></div>
        </section>
        """;

    var script = """
        (function () {
            const REGIONS = ['Southeast Asia', 'East Asia'];
            const MAX_BARS = 80, MAX_LOG = 500;
            const $ = (id) => document.getElementById(id);
            const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
            const strip = $('strip'), logEl = $('log'), banner = $('banner');
            let lines = [], lastRegion = null, failing = false, failStart = 0, polling = false, loopId = 0;

            const stamp = (d) => d.toISOString().slice(11, 23);
            const regionClass = (r) => r === 'Southeast Asia' ? 'sea' : (r === 'East Asia' ? 'ea' : 'other');

            function addLog(text, cls) {
                const t = stamp(new Date());
                lines.push(t + '  ' + text);
                if (lines.length > MAX_LOG) lines.shift();
                const div = document.createElement('div');
                if (cls) div.className = cls;
                div.textContent = t + '  ' + text;
                logEl.appendChild(div);
                while (logEl.childNodes.length > 200) logEl.removeChild(logEl.firstChild);
                logEl.scrollTop = logEl.scrollHeight;
            }
            function addBar(cls, title) {
                const b = document.createElement('div');
                b.className = 'bar ' + cls;
                b.title = title;
                strip.appendChild(b);
                while (strip.childNodes.length > MAX_BARS) strip.removeChild(strip.firstChild);
            }
            function setBoard() {
                REGIONS.forEach((r) => {
                    const tile = document.querySelector('.tile[data-region="' + r + '"]');
                    let state = 'idle', label = 'Waiting for a request';
                    if (lastRegion === r) {
                        state = failing ? 'down' : 'live';
                        label = failing ? 'Not responding' : 'Serving your requests';
                    } else if (lastRegion) {
                        state = 'standby';
                        label = 'Not in use';
                    }
                    tile.dataset.state = state;
                    tile.querySelector('.state-text').textContent = label;
                });
            }
            function showBanner(kind, text) { banner.className = 'banner show ' + kind; banner.textContent = text; }

            function onSuccess(region) {
                if (failing) {
                    const secs = ((Date.now() - failStart) / 1000).toFixed(1);
                    const where = region || 'a healthy region';
                    failing = false;
                    showBanner('ok', 'Recovered. ' + where + ' is serving requests. Outage lasted ' + secs + ' s.');
                    addLog('RECOVERED after ' + secs + ' s (' + where + ')', 'n');
                } else if (region && lastRegion && region !== lastRegion) {
                    showBanner('ok', 'Traffic moved back to ' + region + '.');
                    addLog('SWITCHED to ' + region, 'n');
                }
                if (region) lastRegion = region;
                addBar(regionClass(region), region || 'Responded');
                setBoard();
            }
            function onFailure(startedAt, detail) {
                if (!failing) {
                    failing = true;
                    failStart = startedAt;
                    showBanner('fail', 'No response since ' + stamp(new Date(startedAt)) + ' UTC. Waiting for a healthy region.');
                }
                addBar('fail', detail);
                setBoard();
            }

            function renderAccount(d) {
                $('signedOut').hidden = !!d;
                $('signedIn').hidden = !d;
                if (d) {
                    $('who').textContent = d.email;
                    $('f-created').textContent = d.sessionCreatedInRegion;
                    $('f-prev').textContent = d.previousRequestServedBy;
                    $('f-now').textContent = d.thisRequestServedBy;
                }
            }

            async function check() {
                const started = Date.now();
                try {
                    const r = await fetch('/api/me', { credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(5000) });
                    const text = await r.text();
                    let data = null;
                    try { data = JSON.parse(text); } catch (e) {}
                    if (r.status === 200 && data) {
                        renderAccount(data);
                        addLog('200  created=' + data.sessionCreatedInRegion + ' | previous=' + data.previousRequestServedBy + ' | now=' + data.thisRequestServedBy);
                        onSuccess(data.thisRequestServedBy);
                    } else if (r.status === 401 && data) {
                        renderAccount(null);
                        addLog('401  not signed in' + (data.servedBy ? ' (' + data.servedBy + ')' : ''));
                        onSuccess(data.servedBy || null);
                    } else {
                        addLog(r.status + '  region unavailable', 'f');
                        onFailure(started, 'HTTP ' + r.status);
                    }
                } catch (e) {
                    addLog('FAILED  no response (' + (e.name === 'TimeoutError' ? 'timed out after 5 s' : 'network error') + ')', 'f');
                    onFailure(started, 'No response');
                }
            }

            async function post(url, body) {
                const r = await fetch(url, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: body ? JSON.stringify(body) : undefined,
                    credentials: 'same-origin',
                    signal: AbortSignal.timeout(8000)
                });
                let data = {};
                try { data = await r.json(); } catch (e) {}
                return { status: r.status, data: data };
            }

            const creds = () => ({ email: $('email').value.trim(), password: $('password').value });
            function setMsg(kind, text) { const m = $('msg'); m.className = 'msg ' + kind; m.textContent = text; }
            function validate(forRegister) {
                const c = creds();
                if (!c.email || c.email.indexOf('@') < 1) { setMsg('err', 'Enter a valid email address.'); return false; }
                if (forRegister && c.password.length < 8) { setMsg('err', 'Use a password with at least 8 characters.'); return false; }
                if (!c.password) { setMsg('err', 'Enter your password.'); return false; }
                return true;
            }
            function busy(on) { ['login', 'register', 'logout', 'check'].forEach((id) => { $(id).disabled = on; }); }
            async function run(fn) {
                busy(true);
                try { await fn(); }
                catch (e) {
                    setMsg('err', 'No response from the server. Try again in a moment.');
                    addLog('FAILED  ' + (e.name === 'TimeoutError' ? 'request timed out' : 'network error'), 'f');
                }
                busy(false);
            }

            async function login() {
                const res = await post('/api/auth/login', creds());
                if (res.status === 200) {
                    addLog('200  signed in via ' + res.data.region);
                    $('password').value = '';
                    setMsg('', '');
                    await check();
                } else {
                    setMsg('err', res.data.error || 'Could not sign in.');
                    addLog(res.status + '  ' + (res.data.error || 'sign-in failed'), 'f');
                }
            }

            $('authForm').onsubmit = (e) => { e.preventDefault(); setMsg('', ''); run(async () => { if (validate(false)) await login(); }); };
            $('register').onclick = () => {
                setMsg('', '');
                run(async () => {
                    if (!validate(true)) return;
                    const res = await post('/api/auth/register', creds());
                    if (res.status === 200) { addLog('200  account created'); await login(); }
                    else { setMsg('err', res.data.error || 'Could not create the account.'); addLog(res.status + '  ' + (res.data.error || 'register failed'), 'f'); }
                });
            };
            $('logout').onclick = () => run(async () => {
                const res = await post('/api/auth/logout');
                addLog(res.status + '  signed out');
                await check();
                setMsg('good', 'You signed out.');
            });
            $('check').onclick = () => run(check);

            async function loop(id) { while (polling && id === loopId) { await check(); await sleep(2000); } }
            $('poll').onchange = (e) => { loopId++; polling = e.target.checked; if (polling) loop(loopId); };

            $('copy').onclick = async () => {
                try { await navigator.clipboard.writeText(lines.join('\n')); $('copy').textContent = 'Copied'; }
                catch (e) { $('copy').textContent = 'Copy failed'; }
                setTimeout(() => { $('copy').textContent = 'Copy log'; }, 1500);
            };
            $('clear').onclick = () => {
                lines = []; logEl.textContent = ''; strip.textContent = '';
                banner.className = 'banner'; banner.textContent = '';
                failing = false; lastRegion = null; setBoard();
            };

            check();
        })();
        """;

    return Results.Content(Page("Failover test", "session", main, script), "text/html");
});

// ---------- Home page ----------
app.MapGet("/", () =>
{
    var region = System.Net.WebUtility.HtmlEncode(Environment.GetEnvironmentVariable("REGION") ?? "Unknown region");
    var version = System.Net.WebUtility.HtmlEncode(Environment.GetEnvironmentVariable("APP_VERSION") ?? "local-dev");

    var main = """
        <h1>Cross-region disaster recovery demo</h1>
        <p class="lead">This application runs in two Azure regions behind Azure Front Door. Front Door sends traffic to the primary region (Southeast Asia) and moves it to the standby region (East Asia) if the primary stops responding. Both regions use the same MongoDB Atlas database. Two tests show what happens during a regional failure.</p>

        <section class="card hero" data-region="__REGION__">
            <div class="muted">This page was served from</div>
            <div class="big">__REGION__</div>
            <dl class="facts">
                <dt>Version</dt><dd>__VERSION__</dd>
                <dt>Server time (UTC)</dt><dd>__TIME__</dd>
            </dl>
        </section>

        <div class="grid2">
            <a class="linkcard" href="/failover">
                <div class="t">Failover test (RTO)</div>
                <div class="d">How long users wait when the primary region fails, and whether their session survives.</div>
            </a>
            <a class="linkcard" href="/data">
                <div class="t">Data test (RPO)</div>
                <div class="d">Writes records during a failure and checks that no acknowledged record is lost.</div>
            </a>
        </div>

        <section class="card terms">
            <h2>Key terms</h2>
            <p><strong>RTO (recovery time objective)</strong> is how long the service may be unavailable after a failure. The Failover test measures the observed recovery time.</p>
            <p><strong>RPO (recovery point objective)</strong> is how much data may be lost, expressed as an amount of data or a period of time. The Data test measures the observed data loss.</p>
            <p><strong>Failback</strong> is the return of traffic to the primary region after it recovers.</p>
            <div class="row">
                <a class="btn secondary" href="/health">Health check</a>
                <a class="btn secondary" href="/weatherforecast">Sample API</a>
            </div>
        </section>
        """
        .Replace("__REGION__", region)
        .Replace("__VERSION__", version)
        .Replace("__TIME__", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

    return Results.Content(Page("Cross-region DR demo", "home", main), "text/html");
});

// ---------- Health ----------
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

// ---------- Sample API endpoint (kept from the default template) ----------
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

// ---------- Types ----------
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

record RecordIn(string? RunId, int Seq);

class RecordDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public string RunId { get; set; } = "";
    public int Seq { get; set; }
    public string Region { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}