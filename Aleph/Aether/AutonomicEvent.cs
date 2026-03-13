namespace Aleph;

/// <summary>
/// Bounded in-memory record of an autonomic state change.
/// Used for observability and diagnostics — NOT a full event bus.
/// Retention: max 2000 entries, max 14 days age (enforced by Homeostasis).
/// </summary>
public sealed record AutonomicEvent
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string Source { get; init; }
    public required string Kind { get; init; }
    public required PulseSeverity Severity { get; init; }
    public required string Message { get; init; }
    public IReadOnlyDictionary<string, double>? Metrics { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}
