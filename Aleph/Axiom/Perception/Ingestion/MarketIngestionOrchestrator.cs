// CONTRACT / INVARIANTS
// - Scoped callable cycle runner: performs ONE ingestion cycle on demand.
// - No internal timers, no while-loop, no hosted behavior.
// - Heartbeat resolves this via IServiceScopeFactory each pulse.
// - Active symbols = portfolio holdings (IsOpen or Quantity > 0) UNION watchlist (IsActive).
// - Batches of 10 symbols per ingestion invocation.
// - DB writes and Python execution are routed through IAxiom.MarketIngestion.
// - If Python is unavailable, logs a warning and returns (does NOT crash).
// - Parquet path contract: data_lake/market/ohlcv/symbol=<SYM>/interval=<INTERVAL>/latest.parquet

namespace Aleph;

/// <summary>
/// Interface for the demoted ingestion cycle runner.
/// Resolved as Scoped via IServiceScopeFactory from HeartbeatService.
/// </summary>
public interface IMarketIngestionCycle
{
    /// <summary>Run a single ingestion cycle for all active symbols.</summary>
    Task RunCycleAsync(CancellationToken ct);
}

/// <summary>
/// Performs one ingestion cycle on demand. No longer a BackgroundService.
/// </summary>
public class MarketIngestionOrchestrator : IMarketIngestionCycle
{
    private const int BatchSize = 10;
    private const int LookbackDays = 365;
    private const string DefaultInterval = "1d";
    private const string OutRoot = "data_lake/market/ohlcv";

    private readonly IAxiom _axiom;
    private readonly IMarketStressDetector _stressDetector;
    private readonly ILogger<MarketIngestionOrchestrator> _logger;

    public MarketIngestionOrchestrator(
        IAxiom axiom,
        IMarketStressDetector stressDetector,
        ILogger<MarketIngestionOrchestrator> logger)
    {
        _axiom = axiom;
        _stressDetector = stressDetector;
        _logger = logger;
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        if (!_axiom.MarketIngestion.IsPythonAvailable)
        {
            _logger.LogWarning(
                "[Ingestion] Python not available. " +
                "Ingestion cycle skipped. Run setup_venv.ps1 to enable ingestion.");
            return;
        }

        _logger.LogInformation("[Ingestion] Starting ingestion cycle...");

        try
        {
            var symbols = await _axiom.MarketIngestion.GetActiveSymbolsAsync(ct);
            if (symbols.Count == 0)
            {
                _logger.LogInformation("[Ingestion] No active symbols to ingest. Skipping.");
                return;
            }

            _logger.LogInformation("[Ingestion] Active symbols ({Count}): {Symbols}",
                symbols.Count, string.Join(", ", symbols));

            var batches = symbols.Chunk(BatchSize).ToList();
            for (int i = 0; i < batches.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var batchSymbols = batches[i].ToList();
                _logger.LogInformation("[Ingestion] Processing batch {Current}/{Total}: {Symbols}",
                    i + 1, batches.Count, string.Join(", ", batchSymbols));

                var runResult = await _axiom.MarketIngestion.RunIngestionBatchAsync(
                    batchSymbols, DefaultInterval, LookbackDays, OutRoot, ct);

                if (runResult.Report is not null)
                {
                    _logger.LogInformation(
                        "[Ingestion] Report received: jobId={JobId}, duration={Duration}ms, results={Count}",
                        runResult.Report.JobId,
                        runResult.Report.DurationMs,
                        runResult.Report.Results.Count);
                }

                await _axiom.MarketIngestion.ApplyIngestionBatchAsync(
                    batchSymbols, DefaultInterval, runResult, ct);
            }

            _logger.LogInformation("[Ingestion] Cycle complete. Running reflexive stress evaluation...");

            // Reflexive stress detection — runs after fresh data is available
            try
            {
                await _stressDetector.EvaluateAsync(ct);
            }
            catch (Exception stressEx) when (stressEx is not OperationCanceledException)
            {
                // Stress detection failure must NOT crash the ingestion cycle
                _logger.LogWarning(stressEx, "[Ingestion] Reflexive stress evaluation failed (non-fatal).");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Ingestion] Cycle cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ingestion] Cycle failed.");
            throw; // Let HeartbeatService catch and track in Homeostasis
        }
    }
}
