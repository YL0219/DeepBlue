using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LifeTrader_AI.Data;
using LifeTrader_AI.Models;

namespace LifeTrader_AI.Services.Ingestion
{
    /// <summary>
    /// BackgroundService that periodically ingests 1-year daily OHLCV data
    /// for all ACTIVE symbols (portfolio holdings + watchlist).
    ///
    /// Each tick:
    ///   1. Creates a DI scope (fresh DbContext)
    ///   2. Queries active symbols via ActiveSymbolSource
    ///   3. Chunks into batches of 10
    ///   4. Runs PythonWorkerRunner per batch (writes Parquet, returns report)
    ///   5. Upserts MarketDataAsset metadata rows from the report
    ///
    /// The Python worker writes heavy OHLCV data to Parquet on disk.
    /// No candle arrays ever return to C# — only a small JSON report.
    ///
    /// Phase 4 Step 1.5: Resolves Python:ExePath from config at startup and
    /// passes the absolute path to PythonWorkerRunner.
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
        private readonly SemaphoreSlim _pythonGate;
        private readonly string _pythonExePath;

        public MarketIngestionOrchestrator(
            IServiceScopeFactory scopeFactory,
            SemaphoreSlim pythonGate,
            IConfiguration config,
            IHostEnvironment env)
        {
            _scopeFactory = scopeFactory;
            _pythonGate = pythonGate;

            // Resolve Python:ExePath — relative paths anchored to ContentRootPath
            var configPath = config["Python:ExePath"] ?? ".venv/Scripts/python.exe";
            _pythonExePath = Path.IsPathRooted(configPath)
                ? configPath
                : Path.GetFullPath(Path.Combine(env.ContentRootPath, configPath));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[Ingestion] Market data ingestion service started.");

            // Validate Python executable at startup
            if (!File.Exists(_pythonExePath))
            {
                Console.WriteLine($"[Ingestion] WARNING: Python not found at '{_pythonExePath}'. " +
                                  "Ingestion will fail until venv is created. Run: setup_venv.ps1");
            }
            else
            {
                Console.WriteLine($"[Ingestion] Using Python: {_pythonExePath}");
            }

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
            Console.WriteLine("[Ingestion] Starting ingestion cycle...");

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var symbolSource = new ActiveSymbolSource(db);
                var runner = new PythonWorkerRunner(_pythonGate, _pythonExePath);

                var symbols = await symbolSource.GetActiveSymbolsAsync(ct);
                if (symbols.Count == 0)
                {
                    Console.WriteLine("[Ingestion] No active symbols to ingest. Skipping.");
                    return;
                }

                Console.WriteLine($"[Ingestion] Active symbols ({symbols.Count}): {string.Join(", ", symbols)}");

                // Chunk into batches of BatchSize
                var batches = symbols.Chunk(BatchSize).ToList();

                for (int i = 0; i < batches.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = batches[i];
                    Console.WriteLine($"[Ingestion] Processing batch {i + 1}/{batches.Count}: {string.Join(", ", batch)}");

                    var report = await runner.RunMarketIngestAsync(
                        batch.ToList(), DefaultInterval, LookbackDays, OutRoot, ct);

                    if (report == null)
                    {
                        // Worker failed entirely — increment failures for all symbols in batch
                        Console.WriteLine("[Ingestion] Worker returned null report. Marking batch as failed.");
                        foreach (var sym in batch)
                        {
                            await UpsertAssetFailureAsync(db, sym, DefaultInterval,
                                "Worker process failed or timed out.", ct);
                        }
                        await db.SaveChangesAsync(ct);
                        continue;
                    }

                    Console.WriteLine($"[Ingestion] Report received: jobId={report.JobId}, duration={report.DurationMs}ms, results={report.Results.Count}");

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

                            Console.WriteLine($"[Ingestion]   {result.Symbol}: OK via {result.ProviderUsed} ({result.RowsWritten} rows -> {result.ParquetPath})");
                        }
                        else
                        {
                            asset.ConsecutiveFailures++;
                            asset.LastError = result.Error?.Message ?? "Unknown error";
                            Console.WriteLine($"[Ingestion]   {result.Symbol}: FAIL ({asset.ConsecutiveFailures}x) — {asset.LastError}");
                        }

                        asset.UpdatedAtUtc = DateTime.UtcNow;
                    }

                    await db.SaveChangesAsync(ct);

                    // Log warnings from the worker
                    if (report.Warnings.Count > 0)
                    {
                        foreach (var w in report.Warnings)
                            Console.WriteLine($"[Ingestion] WARNING: {w}");
                    }
                }

                Console.WriteLine("[Ingestion] Cycle complete.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[Ingestion] Cycle cancelled (shutting down).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Ingestion] Cycle failed: {ex.Message}");
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
