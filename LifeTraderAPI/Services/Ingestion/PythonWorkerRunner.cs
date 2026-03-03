// CONTRACT / INVARIANTS
// - Runs market_ingest_worker.py as a child process, gated by the global SemaphoreSlim.
// - The Python worker writes Parquet to disk and prints exactly ONE JSON object (IngestionReport) to stdout.
// - All worker logs go to stderr (not stdout). No candle data returns to C#.
// - Uses ProcessRunner with ArgumentList (injection-safe, no string concatenation).
// - Symbols validated via SymbolValidator before passing to Python args.
// - Timeout: 90s per batch. Process tree killed on timeout.
// - Thread-safe: SemaphoreSlim gates concurrent Python processes.

using System.Text.Json;
using LifeTrader_AI.Models;
using LifeTrader_AI.Infrastructure;

namespace LifeTrader_AI.Services.Ingestion
{
    /// <summary>
    /// Spawns market_ingest_worker.py, gates via SemaphoreSlim, and parses the JSON report.
    /// </summary>
    public class PythonWorkerRunner
    {
        private readonly SemaphoreSlim _pythonGate;
        private readonly string _pythonExePath;
        private readonly ILogger<PythonWorkerRunner> _logger;
        private const int TimeoutMs = 90_000; // 90s for a 10-symbol batch

        public PythonWorkerRunner(
            SemaphoreSlim pythonGate,
            string pythonExePath,
            ILogger<PythonWorkerRunner> logger)
        {
            _pythonGate = pythonGate;
            _pythonExePath = pythonExePath;
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

            string symbolsArg = string.Join(",", validSymbols);

            await _pythonGate.WaitAsync(ct);
            try
            {
                if (!File.Exists(_pythonExePath))
                {
                    _logger.LogError("[Ingestion] Python executable not found at '{Path}'. " +
                                     "Run setup_venv.ps1 or set Python:ExePath in appsettings.json.", _pythonExePath);
                    return null;
                }

                // Arguments passed as a list — injection-safe via ProcessStartInfo.ArgumentList
                var args = new List<string>
                {
                    "market_ingest_worker.py",
                    "--symbols", symbolsArg,
                    "--interval", interval,
                    "--lookbackDays", lookbackDays.ToString(),
                    "--outRoot", outRoot
                };

                var result = await ProcessRunner.RunAsync(_pythonExePath, args, TimeoutMs, ct);

                if (result.TimedOut)
                {
                    _logger.LogWarning("[Ingestion] Worker timed out after {Seconds}s for batch: {Symbols}",
                        TimeoutMs / 1000, symbolsArg);
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
                        result.ExitCode, symbolsArg);
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
            finally
            {
                _pythonGate.Release();
            }
        }
    }
}
