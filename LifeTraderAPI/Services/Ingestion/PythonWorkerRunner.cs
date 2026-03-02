using System.Diagnostics;
using System.Text.Json;
using LifeTrader_AI.Models;

namespace LifeTrader_AI.Services.Ingestion
{
    /// <summary>
    /// Runs market_ingest_worker.py as a child process, gated by the global
    /// SemaphoreSlim python throttle. Returns a parsed IngestionReport or null on failure.
    ///
    /// Contract: the Python worker writes Parquet to disk and prints exactly ONE
    /// JSON object (the ingestion report) to stdout. No candle data returns to C#.
    ///
    /// Phase 4 Step 1.5: Uses Python:ExePath from config (resolved to absolute path
    /// by the Orchestrator) instead of relying on a global "python" on PATH.
    /// </summary>
    public class PythonWorkerRunner
    {
        private readonly SemaphoreSlim _pythonGate;
        private readonly string _pythonExePath;
        private const int TimeoutMs = 90_000; // 90s for a 10-symbol batch

        public PythonWorkerRunner(SemaphoreSlim pythonGate, string pythonExePath)
        {
            _pythonGate = pythonGate;
            _pythonExePath = pythonExePath;
        }

        public async Task<IngestionReport?> RunMarketIngestAsync(
            List<string> symbols, string interval, int lookbackDays, string outRoot,
            CancellationToken ct)
        {
            string symbolsArg = string.Join(",", symbols);

            await _pythonGate.WaitAsync(ct);
            try
            {
                if (!File.Exists(_pythonExePath))
                {
                    Console.WriteLine($"[Ingestion] ERROR: Python executable not found at '{_pythonExePath}'. " +
                                      "Run setup_venv.ps1 or set Python:ExePath in appsettings.json.");
                    return null;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = _pythonExePath,
                    Arguments = $"market_ingest_worker.py --symbols {symbolsArg} --interval {interval} --lookbackDays {lookbackDays} --outRoot {outRoot}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Console.WriteLine("[Ingestion] ERROR: Failed to start Python process.");
                    return null;
                }

                // Drain both streams concurrently to avoid deadlocks
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeoutMs);

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }

                    if (ct.IsCancellationRequested)
                    {
                        Console.WriteLine("[Ingestion] Worker cancelled (app shutting down).");
                        return null;
                    }

                    Console.WriteLine($"[Ingestion] Worker timed out after {TimeoutMs / 1000}s for batch: {symbolsArg}");
                    return null;
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    // stderr is used for progress/debug logs by the worker — not an error
                    Console.WriteLine($"[Ingestion] Worker stderr:\n{stderr.TrimEnd()}");
                }

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"[Ingestion] Worker exited with code {process.ExitCode} for batch: {symbolsArg}");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(stdout))
                {
                    Console.WriteLine("[Ingestion] Worker returned empty stdout.");
                    return null;
                }

                try
                {
                    var report = JsonSerializer.Deserialize<IngestionReport>(stdout.Trim());
                    return report;
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[Ingestion] Failed to parse ingestion report JSON: {ex.Message}");
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[Ingestion] Worker run cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Ingestion] Process error: {ex.Message}");
                return null;
            }
            finally
            {
                _pythonGate.Release();
            }
        }
    }
}
