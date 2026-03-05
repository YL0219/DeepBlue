// CONTRACT / INVARIANTS
// - Routes: GET /api/market/quote, GET /api/market/candles
// - Serves real-time market data for the chart web app (Unity frontend).
// - Symbols validated via SymbolValidator before any external call.
// - All Python calls routed through PythonDispatcherService (single gateway).
// - Responses cached 10s via IMemoryCache.

using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using LifeTrader_AI.Infrastructure;
using LifeTrader_AI.Infrastructure.Python;

namespace LifeTraderAPI.Controllers
{
    [ApiController]
    [Route("api/market")]
    public class MarketController : ControllerBase
    {
        private static readonly HashSet<string> AllowedTimeframes = new(StringComparer.OrdinalIgnoreCase)
            { "1m", "5m", "15m", "1h", "1d", "1w", "1mo" };

        private static readonly HashSet<string> AllowedRanges = new(StringComparer.OrdinalIgnoreCase)
            { "7d", "30d", "90d", "180d", "1y", "2y" };

        private const int DefaultLimit = 500;
        private const int MaxLimit = 2000;
        private const int PythonTimeoutMs = 30_000;

        private readonly IMemoryCache _cache;
        private readonly PythonDispatcherService _dispatcher;
        private readonly ILogger<MarketController> _logger;

        public MarketController(
            IMemoryCache cache,
            PythonDispatcherService dispatcher,
            ILogger<MarketController> logger)
        {
            _cache = cache;
            _dispatcher = dispatcher;
            _logger = logger;
        }


        // GET /api/market/quote?symbol=AMD
        [HttpGet("quote")]
        public async Task<IActionResult> GetQuote([FromQuery] string symbol, CancellationToken ct)
        {
            if (!SymbolValidator.TryNormalize(symbol, out var normalized))
                return BadRequest(new { error = "Invalid symbol. Must be 1-15 alphanumeric characters (A-Z, 0-9, dot, hyphen)." });

            // Check cache first
            string cacheKey = $"quote:{normalized}";
            if (_cache.TryGetValue(cacheKey, out object? cachedQuote))
            {
                _logger.LogDebug("[Market] Cache hit: {CacheKey}", cacheKey);
                return Ok(cachedQuote);
            }

            _logger.LogInformation("[Market] Quote request for {Symbol}", normalized);

            var (success, json, errorMsg) = await RunPythonFetcher(
                "fetch-quote", new[] { "--symbol", normalized }, ct);
            if (!success)
                return StatusCode(502, new { error = errorMsg });

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
                {
                    string pyError = root.TryGetProperty("error", out var errProp) ? errProp.GetString() ?? "Unknown" : "Unknown";
                    return StatusCode(502, new { error = pyError });
                }

                var result = new
                {
                    symbol = root.GetProperty("symbol").GetString(),
                    price = root.GetProperty("price").GetDouble(),
                    timestampUtc = root.GetProperty("timestampUtc").GetString()
                };

                // Cache successful quote for 10 seconds
                _cache.Set(cacheKey, result, TimeSpan.FromSeconds(10));
                return Ok(result);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[Market] JSON parse error for quote {Symbol}", normalized);
                return StatusCode(502, new { error = "Invalid response from data fetcher." });
            }
        }


        // GET /api/market/candles?symbol=AMD&tf=1d&range=180d&limit=500&to=optional
        [HttpGet("candles")]
        public async Task<IActionResult> GetCandles(
            [FromQuery] string symbol,
            [FromQuery] string tf = "1d",
            [FromQuery] string range = "180d",
            [FromQuery] int limit = DefaultLimit,
            [FromQuery] string? to = null,
            CancellationToken ct = default)
        {
            // Validate symbol
            if (!SymbolValidator.TryNormalize(symbol, out var normalized))
                return BadRequest(new { error = "Invalid symbol." });

            tf = tf.ToLowerInvariant();
            range = range.ToLowerInvariant();

            // Validate timeframe
            if (!AllowedTimeframes.Contains(tf))
                return BadRequest(new { error = $"Invalid timeframe '{tf}'. Allowed: {string.Join(", ", AllowedTimeframes)}" });

            // Validate range
            if (!AllowedRanges.Contains(range))
                return BadRequest(new { error = $"Invalid range '{range}'. Allowed: {string.Join(", ", AllowedRanges)}" });

            // Clamp limit
            limit = Math.Clamp(limit, 1, MaxLimit);

            // Validate 'to' if provided (must be numeric unix timestamp)
            if (to != null && !double.TryParse(to, out _))
                return BadRequest(new { error = "Invalid 'to' parameter. Must be a unix timestamp." });

            // Check cache first
            string cacheKey = $"candles:{normalized}:{tf}:{range}:{limit}:{to ?? "latest"}";
            if (_cache.TryGetValue(cacheKey, out object? cachedCandles))
            {
                _logger.LogDebug("[Market] Cache hit: {CacheKey}", cacheKey);
                return Ok(cachedCandles);
            }

            _logger.LogInformation("[Market] Candles request: {Symbol} tf={Tf} range={Range} limit={Limit} to={To}",
                normalized, tf, range, limit, to ?? "latest");

            // Build python args as a list (injection-safe via ArgumentList)
            var args = new List<string>
            {
                "--symbol", normalized, "--tf", tf, "--range", range, "--limit", limit.ToString()
            };
            if (to != null) { args.Add("--to"); args.Add(to); }

            var (success, json, errorMsg) = await RunPythonFetcher("fetch-candles", args, ct);
            if (!success)
                return StatusCode(502, new { error = errorMsg });

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
                {
                    string pyError = root.TryGetProperty("error", out var errProp) ? errProp.GetString() ?? "Unknown" : "Unknown";
                    return StatusCode(502, new { error = pyError });
                }

                // Pass through the structured response
                var result = new
                {
                    symbol = root.GetProperty("symbol").GetString(),
                    tf = root.GetProperty("tf").GetString(),
                    candles = root.GetProperty("candles").Clone(),
                    nextTo = root.TryGetProperty("nextTo", out var ntProp) && ntProp.ValueKind != JsonValueKind.Null
                        ? ntProp.GetInt64() : (long?)null
                };

                // Cache successful candles for 10 seconds
                _cache.Set(cacheKey, result, TimeSpan.FromSeconds(10));
                return Ok(result);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[Market] JSON parse error for candles {Symbol}", normalized);
                return StatusCode(502, new { error = "Invalid response from data fetcher." });
            }
        }


        /// <summary>
        /// Runs a market fetch action via PythonDispatcherService (single gateway).
        /// </summary>
        private async Task<(bool Success, string StdOut, string Error)> RunPythonFetcher(
            string action, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            if (!_dispatcher.IsAvailable)
                return (false, "", "Python not available. Run setup_venv.ps1 to create the venv.");

            try
            {
                var result = await _dispatcher.RunAsync("market", action, arguments, PythonTimeoutMs, ct);

                if (result.TimedOut)
                    return (false, "", "Python process timed out.");

                if (!result.Success)
                {
                    _logger.LogWarning("[Market] Python stderr: {Stderr}", result.Stderr);
                    return (false, "", $"Python exited with code {result.ExitCode}: {result.Stderr}");
                }

                if (string.IsNullOrWhiteSpace(result.Stdout))
                    return (false, "", "Python returned empty output.");

                return (true, result.Stdout, "");
            }
            catch (OperationCanceledException)
            {
                return (false, "", "Request cancelled.");
            }
        }
    }
}
