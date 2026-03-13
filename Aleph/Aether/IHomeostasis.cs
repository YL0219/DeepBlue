namespace Aleph;

/// <summary>
/// Autonomic regulator tracking external stress and internal fatigue.
/// Thread-safe, singleton-friendly. All reads return immutable snapshots.
/// </summary>
public interface IHomeostasis
{
    /// <summary>Returns an immutable snapshot of the current homeostatic state.</summary>
    HomeostasisSnapshot GetSnapshot();

    /// <summary>Ingest a pulse envelope (stress signal, heartbeat report, etc.).</summary>
    void Ingest(PulseEnvelope envelope);

    /// <summary>Record the result of a completed heartbeat pulse.</summary>
    void RecordPulse(PulseEnvelope report);

    /// <summary>Check if the system is currently overloaded (should shed heavy work).</summary>
    bool IsOverloaded { get; }

    /// <summary>Check if the system is breathless (consecutive failures or extreme fatigue).</summary>
    bool IsBreathless { get; }
}

/// <summary>
/// Immutable snapshot of all homeostatic vitals at a point in time.
/// </summary>
public sealed record HomeostasisSnapshot
{
    public required double StressLevel { get; init; }
    public required double FatigueLevel { get; init; }
    public required double OverloadLevel { get; init; }
    public required int FailureStreak { get; init; }
    public required long LastPulseDurationMs { get; init; }
    public required DateTimeOffset LastUpdatedUtc { get; init; }
    public required IReadOnlyList<string> ActiveFlags { get; init; }

    /// <summary>Recent distinct stress sources for observability.</summary>
    public IReadOnlyList<string> RecentStressSources { get; init; } = Array.Empty<string>();

    public static HomeostasisSnapshot Default => new()
    {
        StressLevel = 0,
        FatigueLevel = 0,
        OverloadLevel = 0,
        FailureStreak = 0,
        LastPulseDurationMs = 0,
        LastUpdatedUtc = DateTimeOffset.UtcNow,
        ActiveFlags = Array.Empty<string>(),
        RecentStressSources = Array.Empty<string>()
    };
}
