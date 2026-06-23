using ClickHouse.Client.ADO;
using LMS.PremiumProfile.Api.Services;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// ── ClickHouse connection ────────────────────────────────────────────────────
// Set once per machine (then restart the terminal / VS):
//   setx LMS_CH_CONN "Host=34.185.193.227;Port=8123;Database=lms;Username=default;Password=Prithivi"
var chConn = Environment.GetEnvironmentVariable("LMS_CH_CONN")
    ?? builder.Configuration["ClickHouse:ConnectionString"]
    ?? throw new InvalidOperationException(
        "ClickHouse connection string not found. " +
        "Set the LMS_CH_CONN environment variable or ClickHouse:ConnectionString in appsettings.");

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "LMS Premium Profile API", Version = "v1" });
});

builder.Services.AddScoped<IBattingProfileService>(_ => new BattingProfileService(chConn));
builder.Services.AddScoped<IBowlingProfileService>(_ => new BowlingProfileService(chConn));
builder.Services.AddScoped<ITeamProfileService>(_ => new TeamProfileService(chConn));

// API 4 — H2H and Clips are implemented and ready in InsightsController (currently hidden).
// To enable: (1) remove [ApiExplorerSettings(IgnoreApi = true)] from InsightsController,
//            (2) uncomment the registration below.
// builder.Services.AddScoped<IInsightsService>(_ => new InsightsService(chConn));

// ── Build + run ───────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Exception handler — always return JSON error details (never empty body) ──
// In production ASP.NET Core swallows unhandled exceptions and returns an
// empty 500 response.  This middleware intercepts them and writes the
// exception message + type so API consumers can see what went wrong.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode  = 500;
        context.Response.ContentType = "application/json";
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        if (feature?.Error is not null)
        {
            var ex = feature.Error;
            await context.Response.WriteAsJsonAsync(new
            {
                error     = ex.Message,
                type      = ex.GetType().Name,
                innerError = ex.InnerException?.Message,
                stackTrace = ex.StackTrace
            });
        }
    });
});

app.UseSwagger();
app.UseSwaggerUI();

// API key + secret authentication (ApiKeyMiddleware) is implemented and ready.
// Re-enable before production by uncommenting the line below and configuring
// ApiAuth:ApiKey and ApiAuth:ApiSecret in appsettings or environment variables.
// app.UseMiddleware<ApiKeyMiddleware>();

// ── Diagnostic: GET /api/health ──────────────────────────────────────────────
// Verifies ClickHouse connectivity and confirms key ball_events columns exist.
// Hit this first if any endpoint returns 500.
app.MapGet("/api/health", async () =>
{
    try
    {
        using var conn = new ClickHouseConnection(chConn);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        // Confirm that the columns used by BattingProfileService exist
        cmd.CommandText = @"
            SELECT
                sum(toUInt64(is_legal_ball))   AS legal_balls,
                sum(toUInt64(is_six))          AS sixes,
                sum(toUInt64(is_boundary))     AS boundaries,
                sum(toUInt64(is_dot_ball))     AS dots,
                sum(toUInt64(home_runs))       AS home_runs,
                sum(toUInt64(steal))           AS steals,
                max(over_phase)                AS sample_phase
            FROM lms.ball_events
            LIMIT 1";
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return Results.Ok(new
            {
                status       = "ok",
                legal_balls  = reader.GetValue(0),
                sixes        = reader.GetValue(1),
                boundaries   = reader.GetValue(2),
                dots         = reader.GetValue(3),
                home_runs    = reader.GetValue(4),
                steals       = reader.GetValue(5),
                sample_phase = reader.GetValue(6)
            });
        }
        return Results.Ok(new { status = "ok", note = "ball_events is empty" });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            status     = "error",
            error      = ex.Message,
            type       = ex.GetType().Name,
            innerError = ex.InnerException?.Message
        }, statusCode: 500);
    }
});

app.MapControllers();

app.Run();
