using Microsoft.AspNetCore.Mvc;

namespace Aleph;

[ApiController]
[Route("api/ai")]
public sealed class AiController : ControllerBase
{
    private readonly IArbiter _arbiter;
    private readonly IBenchmarkMetrics _metrics;
    private readonly ILogger<AiController> _logger;

    public AiController(
        IArbiter arbiter,
        IBenchmarkMetrics metrics,
        ILogger<AiController> logger)
    {
        _arbiter = arbiter;
        _metrics = metrics;
        _logger = logger;
    }

    [HttpPost("ask")]
    public async Task<IActionResult> AskTheAgent([FromBody] ChatRequest request, CancellationToken ct)
    {
        var safeRequest = request ?? new ChatRequest();

        var result = await _arbiter.HandleAsync(safeRequest, ct);

        return Ok(new
        {
            response = result.Response,
            uiActions = result.UiActions,
            terminatedByCircuitBreaker = result.TerminatedByCircuitBreaker,
            iterations = result.Iterations
        });
    }

    /// <summary>
    /// Read-only Compute Arbitrage benchmark snapshot. In-memory only;
    /// resets on process restart. No DB, no trading state.
    /// </summary>
    [HttpGet("benchmark")]
    public IActionResult GetBenchmark()
    {
        var snapshot = _metrics.Snapshot();

        return Ok(new
        {
            totalRequests = snapshot.TotalRequests,
            averageLatencyMs = snapshot.AverageLatencyMs,
            latestLatencyMs = snapshot.LatestLatencyMs,
            totalPromptTokens = snapshot.TotalPromptTokens,
            totalCompletionTokens = snapshot.TotalCompletionTokens,
            totalTokens = snapshot.TotalTokens
        });
    }
}
