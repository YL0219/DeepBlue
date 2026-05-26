namespace Aleph;

/// <summary>
/// Lightweight in-memory telemetry sink for Arbiter's upstream LLM proxy
/// calls. Used by the Compute Arbitrage benchmark — not persisted, reset
/// on process restart, not part of any sector contract.
///
/// Lives in the Arbiter sector because the calls it measures belong to
/// the Arbiter reasoning loop. Does not touch Axiom, Aether, Circulation,
/// the DB, or trading state.
/// </summary>
public interface IBenchmarkMetrics
{
    /// <summary>
    /// Record one upstream LLM proxy round-trip. Token counts may be zero
    /// when the response had no <c>usage</c> block (e.g. error envelopes).
    /// </summary>
    void RecordRequest(long latencyMs, int promptTokens, int completionTokens);

    /// <summary>Immutable snapshot for the GET /api/ai/benchmark endpoint.</summary>
    BenchmarkSnapshot Snapshot();
}

/// <summary>
/// Read-only view of accumulated benchmark counters.
/// </summary>
public sealed record BenchmarkSnapshot(
    long TotalRequests,
    double AverageLatencyMs,
    long LatestLatencyMs,
    long TotalPromptTokens,
    long TotalCompletionTokens,
    long TotalTokens);
