using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aleph;

/// <summary>
/// Phase 10 DI isolation: registers the Aether sector — the quantitative
/// black box. Owns autonomic regulation (Homeostasis, Heartbeat),
/// metabolism (Liver), the ML Cortex, Sleep Cycle, and the Aether-side
/// Python gateways used for quant, ML, simulation, and macro analysis.
///
/// Aether must not receive:
///   - <c>AppDbContext</c>
///   - <c>TradingService</c>
///   - broker gateways
///   - real portfolio repositories
///   - real order executors
///   - Axiom concrete internal types
///
/// Aether may depend on <see cref="IAxiom"/> for dispatching its Python
/// scripts through the shared dispatcher — this is the documented Phase
/// 10 boundary compromise (see <c>Architecture/CELL_RULES.md</c>).
/// </summary>
public static class AetherBootstrapper
{
    public static IServiceCollection AddAether(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ─── Homeostasis: autonomic regulation singleton ─────────────
        // Serves both IHomeostasis (full read/write for Heartbeat)
        // and IStressInjector (narrow write-only for external domains).
        services.AddSingleton<Homeostasis>();
        services.AddSingleton<IHomeostasis>(sp => sp.GetRequiredService<Homeostasis>());
        services.AddSingleton<IStressInjector>(sp => sp.GetRequiredService<Homeostasis>());

        // ─── Heart: autonomic background heartbeat ───────────────────
        services.AddHostedService<HeartbeatService>();

        // ─── Liver: metabolic digester (MarketDataEvent → MetabolicEvent) ───
        services.AddSingleton<MetabolicArtifactWriter>();
        services.AddHostedService<LiverService>();

        // ─── ML Cortex: predictive organ (MetabolicEvent → PredictionEvent) ──
        services.AddHostedService<MlCortexService>();

        // ─── Sleep Cycle: offline learning orchestrator ──────────────
        // Preserved in Phase 10; not aggressively expanded.
        services.AddHostedService<SleepCycleService>();

        // ─── Sector contract ─────────────────────────────────────────
        services.AddSingleton<IAether, Aether>();

        return services;
    }
}
