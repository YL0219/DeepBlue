// CONTRACT / INVARIANTS
// - Resolves Python:ExePath from IConfiguration, anchored to ContentRootPath if relative.
// - Singleton: resolved once at startup, immutable thereafter.
// - IsAvailable = false when Python exe not found; callers MUST degrade gracefully.
// - In Development: missing Python logs a warning but does NOT crash the app.
// - TODO: In Production, consider throwing (fail-loud) instead of degrading.

namespace Aleph
{
    /// <summary>
    /// Resolves and validates the Python executable path from configuration.
    /// Registered as a singleton — the path is resolved once at startup.
    /// </summary>
    public sealed class PythonPathResolver
    {
        /// <summary>Absolute path to the Python executable.</summary>
        public string ExePath { get; }

        /// <summary>True if the Python executable exists on disk at startup.</summary>
        public bool IsAvailable { get; }

        public PythonPathResolver(
            IConfiguration config,
            IHostEnvironment env,
            ILogger<PythonPathResolver> logger)
        {
            var configPath = config["Python:ExePath"] ?? ".venv/Scripts/python.exe";
            ExePath = Path.IsPathRooted(configPath)
                ? configPath
                : Path.GetFullPath(Path.Combine(env.ContentRootPath, configPath));

            IsAvailable = File.Exists(ExePath);

            if (!IsAvailable)
            {
                logger.LogWarning(
                    "[Python] Executable not found at '{ExePath}'. " +
                    "Python-dependent features (ingestion, market data, news) are disabled. " +
                    "Run setup_venv.ps1 to create the venv.", ExePath);
                // TODO: In Production, consider throwing to fail-loud instead of silent degradation.
            }
            else
            {
                logger.LogInformation("[Python] Using executable: {ExePath}", ExePath);
            }
        }
    }
}
