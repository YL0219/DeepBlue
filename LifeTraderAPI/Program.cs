using Microsoft.EntityFrameworkCore;
using LifeTrader_AI.Data;
using LifeTrader_AI.Services;
using LifeTrader_AI.Services.Ingestion;
using LifeTrader_AI.Infrastructure;
using Serilog;

// --- 0. ENTERPRISE LOGGER (Serilog) ---
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    .WriteTo.File("logs/deepblue_log_.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseSerilog();

// 1. CONTROLLERS
builder.Services.AddControllers();

// 1.5 DATABASE (EF Core + SQLite, Scoped per-request)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=deepblue.db"));

// 1.6 SERVICE LAYER (Scoped — shares DbContext lifetime)
builder.Services.AddScoped<TradingService>();

// 1.7 MEMORY CACHE (10s TTL, reduces redundant API calls)
builder.Services.AddMemoryCache();

// 1.8 PYTHON PROCESS THROTTLE (global cap of 3 concurrent Python processes)
builder.Services.AddSingleton(new SemaphoreSlim(3, 3));

// 1.9 PYTHON PATH RESOLVER (Singleton — resolves Python:ExePath once at startup)
// If the venv is missing, PythonPathResolver logs a warning but does NOT crash.
builder.Services.AddSingleton<PythonPathResolver>();

// 1.10 MARKET DATA INGESTION (background 5-min OHLCV cycle)
builder.Services.AddHostedService<MarketIngestionOrchestrator>();

// 2. CORS (Unity frontend)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowUnity", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// TODO: Migrate API keys from appsettings.json to user-secrets or environment variables.
// appsettings.json should NOT contain secrets in shared/production environments.
if (app.Environment.IsDevelopment())
{
    var config = app.Services.GetRequiredService<IConfiguration>();
    if (!string.IsNullOrEmpty(config["OpenAI:ApiKey"]) && config["OpenAI:ApiKey"]!.StartsWith("sk-"))
    {
        Log.Warning("[Startup] API keys detected in appsettings.json. " +
                    "Migrate to user-secrets or environment variables before deploying.");
    }
}

// 3. MIDDLEWARE PIPELINE
app.UseStaticFiles();
app.UseCors("AllowUnity");
app.MapControllers();

app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);

app.Run();
