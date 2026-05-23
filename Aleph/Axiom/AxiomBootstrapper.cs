using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aleph;

/// <summary>
/// Phase 10 DI isolation: registers the Axiom sector — the only sector
/// allowed to touch the real world. Owns reality-facing services:
/// perception, ingestion, Python dispatch infrastructure, MCP tools,
/// AppDbContext, real trading execution, and skill registry.
///
/// Aether and Arbiter must not receive any of these dependencies
/// directly; they must go through the <see cref="IAxiom"/> contract or
/// through Circulation events.
/// </summary>
public static class AxiomBootstrapper
{
    public static IServiceCollection AddAxiom(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ─── Memory: EF Core context ─────────────────────────────────
        var sqliteConnectionString =
            configuration.GetConnectionString("Aleph")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=deepblue.db";

        // AddDbContextFactory automatically registers both the Singleton Factory
        // AND the Scoped DbContext for legacy controllers.
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite(sqliteConnectionString));

        // ─── Execution: real trading (Phase 10 freeze in effect) ─────
        services.AddScoped<TradingService>();

        // ─── Shared cross-cutting infrastructure ─────────────────────
        services.AddMemoryCache();

        // ─── Infrastructure: Python dispatch ─────────────────────────
        services.AddSingleton<PythonPathResolver>();
        services.AddSingleton<PythonDispatcherService>();

        // ─── Memory: autonomic event persistence (kidneys) ───────────
        services.AddHostedService<AutonomicPersistenceService>();

        // ─── Perception: ingestion + stress detection + snapshot cache ───
        services.AddScoped<IMarketStressDetector, MarketStressDetector>();
        services.AddScoped<IMarketIngestionCycle, MarketIngestionOrchestrator>();
        services.AddSingleton<PerceptionSnapshotCache>();

        // ─── Infrastructure: Skills registry ─────────────────────────
        services.AddSingleton<ISkillRegistry, FileSkillRegistry>();

        // ─── Infrastructure: MCP server + tools ──────────────────────
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "DeepBlue",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport()
            .WithToolsFromAssembly();

        services.AddSingleton<McpMarketTools>();
        services.AddSingleton<McpExecutionTools>();
        services.AddSingleton<McpNewsTools>();
        services.AddSingleton<McpSkillTools>();
        services.AddSingleton<McpAetherTools>();
        services.AddSingleton<IMcpToolRegistry, McpToolRegistry>();
        services.AddSingleton<McpToolSchemaAdapter>();
        services.AddSingleton<McpToolInvoker>();

        // ─── Sector contract ─────────────────────────────────────────
        services.AddSingleton<IAxiom, Axiom>();

        return services;
    }
}
