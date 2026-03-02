using Microsoft.EntityFrameworkCore;
using LifeTrader_AI.Data;
using LifeTrader_AI.Services;
using LifeTrader_AI.Services.Ingestion;
using Serilog; // <--- This is the new logger

// --- 0. THE SILENT ENTERPRISE LOGGER (Serilog) ---
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug() // Capture everything for the text file
    // Mute the noisy Microsoft and EF Core SQL logs in the terminal
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning) 
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    // 1. CLEAN CONSOLE: Show only our clean [Ingestion] and [Backend] messages
    .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    // 2. DETAILED FILE: Save the noisy SQL queries to a file for debugging
    .WriteTo.File("logs/deepblue_log_.txt", 
        rollingInterval: RollingInterval.Day, // New file every day
        retainedFileCountLimit: 7,            // Keep only the last 7 days
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Nuke the default Microsoft terminal logger so it stops overriding us
builder.Logging.ClearProviders(); 
builder.Host.UseSerilog(); // Hand control over to Serilog
// --------------------------------------------------

// 1. HIRE THE STAFF
// This tells the server we are going to use "Controllers" (separate files) to handle logic.
builder.Services.AddControllers();

// 1.5 DATABASE FOUNDATION (EF Core + SQLite)
// Registered as Scoped — each HTTP request gets its own DbContext instance (thread-safe).
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=deepblue.db"));

// 1.6 SERVICE LAYER
// TradingService is Scoped — shares the same DbContext lifetime as the request.
builder.Services.AddScoped<TradingService>();

// 1.7 MEMORY CACHE (Phase 3: reduces redundant API calls, 10s TTL used in controllers)
builder.Services.AddMemoryCache();

// 1.8 PYTHON PROCESS THROTTLE (Phase 3: limits concurrent Process.Start to 3)
builder.Services.AddSingleton(new SemaphoreSlim(3, 3));

// 1.9 MARKET DATA INGESTION (Phase 4: background 5-min OHLCV ingestion cycle)
builder.Services.AddHostedService<MarketIngestionOrchestrator>();

// 2. OPEN THE DOORS FOR UNITY (CORS)
// By default, web servers block requests from other apps. This disables that security feature for local testing.
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

// 3. MIDDLEWARE PIPELINE
app.UseStaticFiles(); // Serves your chart/index.html
app.UseCors("AllowUnity");
app.MapControllers();

// Add this so Serilog safely flushes the log file when you press Ctrl+C
app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush); 

app.Run();