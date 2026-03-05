// CONTRACT / INVARIANTS
// - BackgroundService: runs a 5-minute ingestion loop for active symbols.
// - Active symbols = portfolio holdings (IsOpen or Quantity > 0) UNION watchlist (IsActive).
// - Each cycle creates a fresh DI scope (scoped DbContext). NEVER reuse DbContext across cycles.
// - Batches of 10 symbols per PythonWorkerRunner invocation.
// - Upserts MarketDataAsset metadata rows from the worker's IngestionReport.
// - If Python is unavailable at startup, logs a warning and disables ingestion (does NOT crash).
// - Parquet path contract: data_lake/market/ohlcv/symbol=<SYM>/interval=<INTERVAL>/latest.parquet

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LifeTrader_AI.Data;
using LifeTrader_AI.Models;
using LifeTrader_AI.Infrastructure;
using LifeTrader_AI.Infrastructure.Python;

namespace LifeTrader_AI.Services.Ingestion
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

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly PythonDispatcherService _dispatcher;
        private readonly PythonWorkerRunner _runner;
        private readonly ILogger<MarketIngestionOrchestrator> _logger;

        public MarketIngestionOrchestrator(
            IServiceScopeFactory scopeFactory,
            PythonDispatcherService dispatcher,
            ILogger<MarketIngestionOrchestrator> logger,
            ILoggerFactory loggerFactory)
        {
            _scopeFactory = scopeFactory;
            _dispatcher = dispatcher;
            _logger = logger;

            // PythonWorkerRunner is stateless — safe to create once and reuse.
            _runner = new PythonWorkerRunner(
                dispatcher,
                loggerFactory.CreateLogger<PythonWorkerRunner>());
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // If Python is not available, disable ingestion gracefully (don't crash the app).
            if (!_dispatcher.IsAvailable)
            {
                _logger.LogWarning(
                    "[Ingestion] Python not available. " +
                    "Ingestion service is DISABLED. API endpoints remain active. " +
                    "Run setup_venv.ps1 to enable ingestion.");
                return;
            }

            _logger.LogInformation("[Ingestion] Market data ingestion service started.");

            // Short delay so the app, DB migration, and WAL pragma all finish first
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            // Run once immediately, then on a 5-minute timer
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
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var symbolSource = new ActiveSymbolSource(db);

                var symbols = await symbolSource.GetActiveSymbolsAsync(ct);
                if (symbols.Count == 0)
                {
                    _logger.LogInformation("[Ingestion] No active symbols to ingest. Skipping.");
                    return;
                }

                _logger.LogInformation("[Ingestion] Active symbols ({Count}): {Symbols}",
                    symbols.Count, string.Join(", ", symbols));

                // Chunk into batches of BatchSize
                var batches = symbols.Chunk(BatchSize).ToList();

                for (int i = 0; i < batches.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = batches[i];
                    _logger.LogInformation("[Ingestion] Processing batch {Current}/{Total}: {Symbols}",
                        i + 1, batches.Count, string.Join(", ", batch));

                    var report = await _runner.RunMarketIngestAsync(
                        batch.ToList(), DefaultInterval, LookbackDays, OutRoot, ct);

                    if (report == null)
                    {
                        // Worker failed entirely — increment failures for all symbols in batch
                        _logger.LogWarning("[Ingestion] Worker returned null report. Marking batch as failed.");
                        foreach (var sym in batch)
                        {
                            await UpsertAssetFailureAsync(db, sym, DefaultInterval,
                                "Worker process failed or timed out.", ct);
                        }
                        await db.SaveChangesAsync(ct);
                        continue;
                    }

                    _logger.LogInformation("[Ingestion] Report received: jobId={JobId}, duration={Duration}ms, results={Count}",
                        report.JobId, report.DurationMs, report.Results.Count);

                    // Upsert MarketDataAsset for each result
                    foreach (var result in report.Results)
                    {
                        var asset = await db.MarketDataAssets
                            .FirstOrDefaultAsync(a => a.Symbol == result.Symbol && a.Interval == result.Interval, ct);

                        if (asset == null)
                        {
                            asset = new MarketDataAsset
                            {
                                Symbol = result.Symbol,
                                Interval = result.Interval
                            };
                            db.MarketDataAssets.Add(asset);
                        }

                        if (result.IsSuccess)
                        {
                            asset.ParquetPath = result.ParquetPath;
                            asset.LastIngestedAtUtc = DateTime.UtcNow;
                            asset.ProviderUsed = result.ProviderUsed;
                            asset.RowsWritten = result.RowsWritten;
                            asset.ConsecutiveFailures = 0;
                            asset.LastError = null;

                            if (DateTime.TryParse(result.DataEndUtc, CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out var dataEnd))
                            {
                                asset.LastDataEndUtc = dataEnd;
                            }

                            _logger.LogInformation("[Ingestion]   {Symbol}: OK via {Provider} ({Rows} rows -> {Path})",
                                result.Symbol, result.ProviderUsed, result.RowsWritten, result.ParquetPath);
                        }
                        else
                        {
                            asset.ConsecutiveFailures++;
                            asset.LastError = result.Error?.Message ?? "Unknown error";
                            _logger.LogWarning("[Ingestion]   {Symbol}: FAIL ({Failures}x) - {Error}",
                                result.Symbol, asset.ConsecutiveFailures, asset.LastError);
                        }

                        asset.UpdatedAtUtc = DateTime.UtcNow;
                    }

                    await db.SaveChangesAsync(ct);

                    // Log warnings from the worker
                    if (report.Warnings.Count > 0)
                    {
                        foreach (var w in report.Warnings)
                            _logger.LogWarning("[Ingestion] Worker warning: {Warning}", w);
                    }
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

        private static async Task UpsertAssetFailureAsync(
            AppDbContext db, string symbol, string interval, string error, CancellationToken ct)
        {
            var asset = await db.MarketDataAssets
                .FirstOrDefaultAsync(a => a.Symbol == symbol && a.Interval == interval, ct);

            if (asset == null)
            {
                asset = new MarketDataAsset { Symbol = symbol, Interval = interval };
                db.MarketDataAssets.Add(asset);
            }

            asset.ConsecutiveFailures++;
            asset.LastError = error;
            asset.UpdatedAtUtc = DateTime.UtcNow;
        }
    }
}
