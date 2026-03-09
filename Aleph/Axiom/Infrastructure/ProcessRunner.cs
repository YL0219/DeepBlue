// CONTRACT / INVARIANTS
// - Runs external processes using ArgumentList (no shell interpretation, injection-safe).
// - Drains stdout + stderr concurrently to prevent OS pipe-buffer deadlocks.
// - Supports timeout via CancelAfter; kills entire process tree on timeout.
// - Returns structured ProcessResult; callers decide error handling.
// - Thread-safe: no shared mutable state. Safe to call from parallel tasks.

using System.Diagnostics;

namespace Aleph
{
    /// <summary>
    /// Structured result from a process execution.
    /// </summary>
    public sealed record ProcessResult(
        bool Success,
        string Stdout,
        string Stderr,
        int ExitCode,
        bool TimedOut);

    /// <summary>
    /// Centralized helper for running external processes (Python, curl, etc.).
    /// All arguments go through ProcessStartInfo.ArgumentList to avoid
    /// shell-interpretation and injection/quoting issues.
    /// </summary>
    public static class ProcessRunner
    {
        /// <summary>
        /// Spawns a process, drains stdout/stderr, enforces a timeout, and returns results.
        /// </summary>
        /// <param name="fileName">Executable path (e.g., python.exe, curl.exe).</param>
        /// <param name="arguments">Argument list — each element is one logical argument.</param>
        /// <param name="timeoutMs">Hard timeout in milliseconds. Process tree is killed on expiry.</param>
        /// <param name="ct">External cancellation token (e.g., app shutdown).</param>
        public static async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            int timeoutMs,
            CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                return new ProcessResult(false, "", $"Failed to start process: {ex.Message}", -1, false);
            }

            if (process == null)
                return new ProcessResult(false, "", "Process.Start returned null.", -1, false);

            using (process)
            {
                // Drain both streams concurrently to avoid OS pipe buffer deadlocks.
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }

                    bool appShutdown = ct.IsCancellationRequested;
                    return new ProcessResult(
                        false, "",
                        appShutdown ? "Operation cancelled." : $"Process timed out after {timeoutMs / 1000}s.",
                        -1,
                        TimedOut: !appShutdown);
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;

                return new ProcessResult(
                    Success: process.ExitCode == 0,
                    Stdout: stdout.TrimEnd(),
                    Stderr: stderr.TrimEnd(),
                    ExitCode: process.ExitCode,
                    TimedOut: false);
            }
        }
    }
}
