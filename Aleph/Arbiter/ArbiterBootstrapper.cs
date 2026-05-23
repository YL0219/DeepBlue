using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aleph;

/// <summary>
/// Phase 10 DI isolation: registers the Arbiter sector — consciousness,
/// memory, reasoning, orchestration, and final arbitration.
///
/// Arbiter must not receive <c>TradingService</c>, broker gateways, or
/// any direct portfolio mutation services. It interacts with the real
/// world only via the <see cref="IAxiom"/> contract (chiefly through
/// MCP tool invocation).
///
/// Arbiter is currently the thinnest sector. Phase 10.3 will deepen it
/// with dedicated memory, an inner reasoning loop, guardrails, and
/// multi-agent coordination — those registrations will land here.
/// </summary>
public static class ArbiterBootstrapper
{
    public static IServiceCollection AddArbiter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ─── Sector contract ─────────────────────────────────────────
        services.AddSingleton<IArbiter, Arbiter>();

        return services;
    }
}
