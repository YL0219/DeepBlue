using Aleph;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    .WriteTo.File(
        "logs/deepblue_log_.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseSerilog();

builder.Services.AddControllers();

// ─── Phase 10 Cellular Triad: sector-isolated DI bootstrappers ─────
// Order is Circulation → Axiom → Aether → Arbiter so that downstream
// sectors can resolve upstream contracts (IAlephBus, IAxiom) without
// surprises. Registration order does not affect resolution order for
// constructor injection, but this order matches the intent of the
// architecture and keeps the file readable.
builder.Services.AddCirculation(builder.Configuration);
builder.Services.AddAxiom(builder.Configuration);
builder.Services.AddAether(builder.Configuration);
builder.Services.AddArbiter(builder.Configuration);

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

app.UseStaticFiles();
app.UseCors("AllowUnity");
app.MapControllers();
app.MapMcp();

app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);

try
{
    var skillRegistry = app.Services.GetRequiredService<ISkillRegistry>();
    skillRegistry.LoadAsync().GetAwaiter().GetResult();
}
catch (Exception ex)
{
    Log.Error(ex, "[Startup] Skill registry load failed - continuing with empty snapshot.");
}

app.Run();
