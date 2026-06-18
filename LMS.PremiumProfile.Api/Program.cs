using LMS.PremiumProfile.Api.Services;

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

// ── Build + run ───────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// TODO: re-enable API key/secret auth before going live
// app.UseMiddleware<ApiKeyMiddleware>();

app.MapControllers();

app.Run();
