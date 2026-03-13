namespace Aleph;

/// <summary>
/// Reflexive market stress detector — inspects freshly ingested data
/// and autonomically injects stress into Homeostasis when danger thresholds fire.
/// Scoped service: resolved alongside the ingestion cycle runner.
/// </summary>
public interface IMarketStressDetector
{
    /// <summary>
    /// Evaluate current market conditions and inject stress if warranted.
    /// Called after a successful ingestion cycle.
    /// </summary>
    Task EvaluateAsync(CancellationToken ct = default);
}
