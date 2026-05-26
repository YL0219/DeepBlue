using System.Threading;

namespace Aleph;

/// <summary>
/// Thread-safe, lock-free in-memory accumulator for Arbiter LLM proxy
/// telemetry. Uses <see cref="Interlocked"/> for all mutations so concurrent
/// agent turns from different threads don't corrupt the counters.
///
/// Phase 10 scope: process-local only. No DB, no event bus, no persistence.
/// </summary>
public sealed class BenchmarkMetrics : IBenchmarkMetrics
{
    private long _totalRequests;
    private long _totalLatencyMs;
    private long _latestLatencyMs;
    private long _totalPromptTokens;
    private long _totalCompletionTokens;

    public void RecordRequest(long latencyMs, int promptTokens, int completionTokens)
    {
        if (latencyMs < 0) latencyMs = 0;
        if (promptTokens < 0) promptTokens = 0;
        if (completionTokens < 0) completionTokens = 0;

        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _totalLatencyMs, latencyMs);
        Interlocked.Exchange(ref _latestLatencyMs, latencyMs);
        Interlocked.Add(ref _totalPromptTokens, promptTokens);
        Interlocked.Add(ref _totalCompletionTokens, completionTokens);
    }

    public BenchmarkSnapshot Snapshot()
    {
        var total = Interlocked.Read(ref _totalRequests);
        var sumLatency = Interlocked.Read(ref _totalLatencyMs);
        var latest = Interlocked.Read(ref _latestLatencyMs);
        var prompt = Interlocked.Read(ref _totalPromptTokens);
        var completion = Interlocked.Read(ref _totalCompletionTokens);

        var avg = total > 0 ? (double)sumLatency / total : 0.0;

        return new BenchmarkSnapshot(
            TotalRequests: total,
            AverageLatencyMs: avg,
            LatestLatencyMs: latest,
            TotalPromptTokens: prompt,
            TotalCompletionTokens: completion,
            TotalTokens: prompt + completion);
    }
}
