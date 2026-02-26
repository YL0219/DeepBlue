using Microsoft.EntityFrameworkCore;
using LifeTrader_AI.Data;
using LifeTrader_AI.Services;

var builder = WebApplication.CreateBuilder(args);

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

// 1.9 SECURITY: Validate required API keys at startup
if (string.IsNullOrEmpty(builder.Configuration["OpenAI:ApiKey"]))
    Console.WriteLine("[Backend] WARNING: OpenAI:ApiKey is not configured! AI endpoints will fail.");
if (string.IsNullOrEmpty(builder.Configuration["Finnhub:ApiKey"]))
    Console.WriteLine("[Backend] WARNING: Finnhub:ApiKey is not configured! Market data will fail.");

var app = builder.Build();

// 2.5 DATABASE INITIALIZATION
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Apply pending migrations (replaces EnsureCreated for schema versioning)
    db.Database.Migrate();
    Console.WriteLine("[Backend] SQLite database migrated.");

    // Enable WAL mode for better concurrent read/write performance.
    // Default journal_mode=DELETE locks the entire DB during writes.
    // WAL allows concurrent readers during a write operation.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    Console.WriteLine("[Backend] SQLite WAL mode enabled.");

    // Set busy timeout to 5 seconds — SQLite will retry internally if locked
    // instead of immediately returning SQLITE_BUSY.
    db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
    Console.WriteLine("[Backend] SQLite busy_timeout set to 5000ms.");
}

// 3. START DIRECTING TRAFFIC
app.UseCors("AllowUnity");

// Serve static files from wwwroot/ (chart web app lives here)
app.UseDefaultFiles(); // Allows /chart/ to resolve to /chart/index.html
app.UseStaticFiles();  // Serves files from wwwroot/

app.MapControllers(); // This reads the URL and sends it to the right C# file

var baseUrl = app.Urls.FirstOrDefault() ?? "http://localhost:5000";
Console.WriteLine($"[Backend] LifeTrader API is running on {baseUrl}");
Console.WriteLine($"[Backend] Chart available at {baseUrl}/chart/index.html");
Console.WriteLine("[Backend] Waiting for Unity...");

app.Run();
