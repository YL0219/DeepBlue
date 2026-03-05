// CONTRACT / INVARIANTS
// - Exposes market data tools via Model Context Protocol (MCP).
// - Decorated with [McpServerToolType] for assembly-level discovery.
// - Each tool method decorated with [McpServerTool] + [Description].
// - Validates all symbol inputs via SymbolValidator — rejects invalid symbols.
// - Clamps "days" to [1..365] to prevent unbounded data reads.
// - Calls PythonDispatcherService.RunParquetReadAsync (never spawns Python directly).
// - Returns JSON string: success passthrough from Python stdout, or error JSON object.
// - Thread-safe: no mutable state; dispatcher handles concurrency gating.

using System.ComponentModel;
using ModelContextProtocol.Server;
using LifeTrader_AI.Infrastructure.Python;

namespace LifeTrader_AI.Infrastructure.Mcp
{
    /// <summary>
    /// MCP tool class for market data queries.
    /// Discovered by WithToolsFromAssembly() via [McpServerToolType].
    /// Instance methods — framework constructs a new instance per invocation,
    /// injecting registered services via constructor.
    /// </summary>
    [McpServerToolType]
    public class McpMarketTools
    {
        private const int MinDays = 1;
        private const int MaxDays = 365;
        private const int ParquetReadTimeoutMs = 30_000;

        // Parquet data lake root — matches MarketIngestionOrchestrator.OutRoot
        private const string DataRoot = "data_lake/market/ohlcv";

        private readonly PythonDispatcherService _dispatcher;
        private readonly ILogger<McpMarketTools> _logger;

        public McpMarketTools(
            PythonDispatcherService dispatcher,
            ILogger<McpMarketTools> logger)
        {
            _dispatcher = dispatcher;
            _logger = logger;
        }

        /// <summary>
        /// Reads local OHLCV market data from the Parquet data lake for a given symbol.
        /// Returns a JSON object with candle data, summary statistics, and metadata.
        /// Data must have been previously ingested by the market ingestion pipeline.
        /// </summary>
        [McpServerTool(Name = "query_local_market_data", ReadOnly = true)]
        [Description(
            "Query locally-stored OHLCV market data for a stock symbol. " +
            "Returns daily candles (open, high, low, close, volume) from the Parquet data lake. " +
            "Data is sourced from prior ingestion cycles — not a live API call. " +
            "Use this for historical analysis, trend review, and portfolio monitoring.")]
        public async Task<string> QueryLocalMarketData(
            [Description("Stock ticker symbol (e.g. AAPL, MSFT, BRK.B). 1-15 uppercase alphanumeric characters, dots, or hyphens.")]
            string symbol,
            [Description("Number of days of historical data to retrieve (1-365). Defaults to 7.")]
            int days = 7)
        {
            // --- Validate symbol ---
            if (!SymbolValidator.TryNormalize(symbol, out var normalizedSymbol))
            {
                _logger.LogWarning("[MCP] Invalid symbol rejected: '{Symbol}'", symbol);
                return BuildErrorJson($"Invalid symbol: '{symbol}'. Must be 1-15 characters, uppercase letters, digits, dots, or hyphens.");
            }

            // --- Clamp days ---
            var clampedDays = Math.Clamp(days, MinDays, MaxDays);
            if (clampedDays != days)
            {
                _logger.LogDebug("[MCP] Days clamped from {Requested} to {Clamped}", days, clampedDays);
            }

            // --- Check dispatcher availability ---
            if (!_dispatcher.IsAvailable)
            {
                _logger.LogWarning("[MCP] Python dispatcher not available for parquet read.");
                return BuildErrorJson("Python environment not available. Run setup_venv.ps1 to create the venv.");
            }

            // --- Execute Parquet read via dispatcher ---
            _logger.LogDebug("[MCP] QueryLocalMarketData: symbol={Symbol}, days={Days}", normalizedSymbol, clampedDays);

            var result = await _dispatcher.RunParquetReadAsync(
                normalizedSymbol,
                clampedDays,
                DataRoot,
                ParquetReadTimeoutMs);

            if (!result.Success)
            {
                _logger.LogWarning("[MCP] Parquet read failed for {Symbol}: exit={ExitCode}, stderr={Stderr}",
                    normalizedSymbol, result.ExitCode, result.Stderr);

                var reason = result.TimedOut
                    ? "Parquet read timed out."
                    : $"Parquet read failed (exit code {result.ExitCode}).";

                return BuildErrorJson(reason);
            }

            // Success — return raw JSON from Python stdout
            return result.Stdout;
        }

        /// <summary>
        /// Builds a simple JSON error object string.
        /// </summary>
        private static string BuildErrorJson(string message)
        {
            // Manual construction avoids System.Text.Json allocation for trivial shape
            var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"{{\"ok\":false,\"error\":\"{escaped}\"}}";
        }
    }
}
