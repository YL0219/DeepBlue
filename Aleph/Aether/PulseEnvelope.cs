namespace Aleph;

/// <summary>
/// Foundational data contract for internal signaling between Heartbeat and Homeostasis.
/// Immutable record — safe to pass across async boundaries without locking.
/// </summary>
public sealed record PulseEnvelope
{
    public required string Source { get; init; }
    public required PulseKind Kind { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required PulseSeverity Severity { get; init; }
    public string? Message { get; init; }
    public IReadOnlyDictionary<string, double>? Metrics { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }

    public static PulseEnvelope HeartbeatReport(
        long durationMs,
        bool success,
        HeartbeatMode mode,
        string? message = null)
    {
        return new PulseEnvelope
        {
            Source = "Heartbeat",
            Kind = PulseKind.PulseResult,
            TimestampUtc = DateTimeOffset.UtcNow,
            Severity = success ? PulseSeverity.Normal : PulseSeverity.Warning,
            Message = message,
            Metrics = new Dictionary<string, double>
            {
                ["durationMs"] = durationMs,
                ["success"] = success ? 1 : 0,
                ["mode"] = (double)mode
            },
            Tags = success ? null : new[] { "failure" }
        };
    }

    public static PulseEnvelope ExternalStress(
        string source,
        PulseSeverity severity,
        string? message = null,
        IReadOnlyDictionary<string, double>? metrics = null)
    {
        return new PulseEnvelope
        {
            Source = source,
            Kind = PulseKind.ExternalStress,
            TimestampUtc = DateTimeOffset.UtcNow,
            Severity = severity,
            Message = message,
            Metrics = metrics,
            Tags = new[] { "external" }
        };
    }
}

public enum PulseKind
{
    PulseResult = 0,
    ExternalStress = 1,
    FatigueReport = 2,
    SystemEvent = 3
}

public enum PulseSeverity
{
    Normal = 0,
    Elevated = 1,
    Warning = 2,
    Critical = 3
}
