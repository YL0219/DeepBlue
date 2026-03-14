namespace Aleph;

/// <summary>
/// Write-only stress injection interface.
/// External domains (Arbiter, Axiom) use this narrow contract to inject adrenaline
/// WITHOUT gaining access to the full homeostatic regulator.
/// Homeostasis is the single implementation — it ingests and governs.
/// </summary>
public interface IStressInjector
{
    /// <summary>
    /// Inject an external stress signal into the autonomic system.
    /// Thread-safe. The envelope must be of kind ExternalStress.
    /// Returns a lightweight receipt for observability.
    /// </summary>
    StressInjectionReceipt InjectStress(PulseEnvelope envelope);
}

/// <summary>
/// Lightweight receipt returned after a stress injection.
/// Provides observability without exposing internal state.
/// </summary>
public sealed record StressInjectionReceipt
{
    public required bool Accepted { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string Source { get; init; }
    public required PulseSeverity Severity { get; init; }
    public string? RejectionReason { get; init; }

    public static StressInjectionReceipt Ok(string source, PulseSeverity severity) => new()
    {
        Accepted = true,
        TimestampUtc = DateTimeOffset.UtcNow,
        Source = source,
        Severity = severity
    };

    public static StressInjectionReceipt Rejected(string source, PulseSeverity severity, string reason) => new()
    {
        Accepted = false,
        TimestampUtc = DateTimeOffset.UtcNow,
        Source = source,
        Severity = severity,
        RejectionReason = reason
    };
}
