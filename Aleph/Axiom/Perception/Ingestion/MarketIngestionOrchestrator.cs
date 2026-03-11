// CONTRACT / INVARIANTS
// - BackgroundService: runs a 5-minute ingestion loop for active symbols.
// - Active symbols = portfolio holdings (IsOpen or Quantity > 0) UNION watchlist (IsActive).
// - Batches of 10 symbols per ingestion invocation.
// - DB writes and Python execution are routed through IAxiom.MarketIngestion.
// - If Python is unavailable at startup, logs a warning and disables ingestion (does NOT crash).
// - Parquet path contract: data_lake/market/ohlcv/symbol=<SYM>/interval=<INTERVAL>/latest.parquet

namespace Aleph
{
    /// <summary>
    /// Periodically ingests 1-year daily OHLCV data for all active symbols.
    /// </summary>
    public class MarketIngestionOrchestrator : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
        private const int BatchSize = 10;
        private const int LookbackDays = 365;
        private const string DefaultInterval = "1d";
        private const string OutRoot = "data_lake/market/ohlcv";

        private readonly IAxiom _axiom;
        private readonly ILogger<MarketIngestionOrchestrator> _logger;

        public MarketIngestionOrchestrator(
            IAxiom axiom,
            ILogger<MarketIngestionOrchestrator> logger)
        {
            _axiom = axiom;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // If Python is not available, disable ingestion gracefully (don't crash the app).
            if (!_axiom.MarketIngestion.IsPythonAvailable)
            {
                _logger.LogWarning(
                    "[Ingestion] Python not available. " +
                    "Ingestion service is DISABLED. API endpoints remain active. " +
                    "Run setup_venv.ps1 to enable ingestion.");
                return;
            }

            _logger.LogInformation("[Ingestion] Market data ingestion service started.");

            // Short delay so the app, DB migration, and WAL pragma all finish first.
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            // Run once immediately, then on a 5-minute timer.
            await RunIngestionCycleAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunIngestionCycleAsync(stoppingToken);
            }
        }

        private async Task RunIngestionCycleAsync(CancellationToken ct)
        {
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

                _logger.LogInformation("[Ingestion] Cycle complete.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Ingestion] Cycle cancelled (shutting down).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Ingestion] Cycle failed.");
            }
        }
    }
}
