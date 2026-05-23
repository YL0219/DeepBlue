using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aleph;

/// <summary>
/// Phase 10 DI isolation: registers the Circulation sector — the shared
/// event bloodstream (AlephBus + Quarantine). Circulation is not a fourth
/// intelligence domain; it owns shared event flow and stays at the root
/// of <c>Aleph/Circulation/</c>.
/// </summary>
public static class CirculationBootstrapper
{
    public static IServiceCollection AddCirculation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // AlephBus — singleton in-memory fan-out event bus (the Veins).
        services.AddSingleton<AlephBus>();
        services.AddSingleton<IAlephBus>(sp => sp.GetRequiredService<AlephBus>());

        // Quarantine — isolation ward for corrupted blood cells rejected by the immune system.
        services.AddSingleton<Quarantine>();

        return services;
    }
}
