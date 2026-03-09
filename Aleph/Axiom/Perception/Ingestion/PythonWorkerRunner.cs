// CONTRACT / INVARIANTS
// - Runs market ingestion via PythonDispatcherService (single gateway through python_router.py).
// - The Python worker writes Parquet to disk and prints exactly ONE JSON object (IngestionReport) to stdout.
// - All worker logs go to stderr (not stdout). No candle data returns to C#.
// - Symbols validated via SymbolValidator before passing to Python args.
// - Timeout: 90s per batch. Process tree killed on timeout.
// - Concurrency gating handled by PythonDispatcherService (SemaphoreSlim cap of 3).

using System.Text.Json;




namespace Aleph
{
    /// <summary>
    /// Spawns market ingestion via PythonDispatcherService and parses the JSON report.
    /// </summary>
    public class PythonWorkerRunner
    {
        private readonly PythonDispatcherService _dispatcher;
        private readonly ILogger<PythonWorkerRunner> _logger;
        private const int TimeoutMs = 90_000; // 90s for a 10-symbol batch

        public PythonWorkerRunner(
            PythonDispatcherService dispatcher,
            ILogger<PythonWorkerRunner> logger)
        {
            _dispatcher = dispatcher;
            _logger = logger;
        }

        public async Task<IngestionReport?> RunMarketIngestAsync(
            List<string> symbols, string interval, int lookbackDays, string outRoot,
            CancellationToken ct)
        {
            // Validate all symbols before passing to Python
            var validSymbols = symbols.Where(s => SymbolValidator.IsValid(s)).ToList();
            if (validSymbols.Count == 0)
            {
                _logger.LogWarning("[Ingestion] No valid symbols in batch. Skipping.");
                return null;
            }
            if (validSymbols.Count < symbols.Count)
            {
                var rejected = symbols.Except(validSymbols);
                _logger.LogWarning("[Ingestion] Rejected invalid symbols: {Rejected}", string.Join(", ", rejected));
            }

            string symbolsCsv = string.Join(",", validSymbols);

            try
            {
                var result = await _dispatcher.RunMarketIngestAsync(
                    symbolsCsv, interval, lookbackDays, outRoot, TimeoutMs, ct);

                if (result.TimedOut)
                {
                    _logger.LogWarning("[Ingestion] Worker timed out after {Seconds}s for batch: {Symbols}",
                        TimeoutMs / 1000, symbolsCsv);
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(result.Stderr))
                {
                    // stderr is used for progress/debug logs by the worker — not an error
                    _logger.LogDebug("[Ingestion] Worker stderr:\n{Stderr}", result.Stderr);
                }

                if (!result.Success)
                {
                    if (ct.IsCancellationRequested)
                    {
                        _logger.LogInformation("[Ingestion] Worker cancelled (app shutting down).");
                        return null;
                    }
                    _logger.LogWarning("[Ingestion] Worker exited with code {ExitCode} for batch: {Symbols}",
                        result.ExitCode, symbolsCsv);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(result.Stdout))
                {
                    _logger.LogWarning("[Ingestion] Worker returned empty stdout.");
                    return null;
                }

                try
                {
                    var report = JsonSerializer.Deserialize<IngestionReport>(result.Stdout.Trim());
                    return report;
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "[Ingestion] Failed to parse ingestion report JSON.");
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Ingestion] Worker run cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Ingestion] Process error.");
                return null;
            }
        }
    }
}
