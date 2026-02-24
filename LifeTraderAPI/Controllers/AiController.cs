using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using LifeTrader_AI.Data;
using LifeTrader_AI.Models;
using LifeTrader_AI.Services;

namespace LifeTraderAPI.Controllers
{
    // ====================================================================
    // MARKET DATA CONTROLLER — Serves candle/quote data for the chart web app
    // ====================================================================
    [ApiController]
    [Route("api/market")]
    public class MarketController : ControllerBase
    {
        // Strict allowlists for parameter validation
        private static readonly HashSet<string> AllowedTimeframes = new(StringComparer.OrdinalIgnoreCase)
            { "1m", "5m", "15m", "1h", "1d", "1w", "1mo" };

        private static readonly HashSet<string> AllowedRanges = new(StringComparer.OrdinalIgnoreCase)
            { "7d", "30d", "90d", "180d", "1y", "2y" };

        private const int DefaultLimit = 500;
        private const int MaxLimit = 2000;
        private const int PythonTimeoutMs = 30_000;

        // Regex: 1-10 uppercase letters/digits (stock tickers)
        private static readonly System.Text.RegularExpressions.Regex SymbolRegex =
            new(@"^[A-Z0-9]{1,10}$", System.Text.RegularExpressions.RegexOptions.Compiled);


        // GET /api/market/quote?symbol=AMD
        [HttpGet("quote")]
        public async Task<IActionResult> GetQuote([FromQuery] string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol) || !SymbolRegex.IsMatch(symbol.ToUpperInvariant()))
                return BadRequest(new { error = "Invalid symbol. Must be 1-10 uppercase alphanumeric characters." });

            symbol = symbol.ToUpperInvariant();
            Console.WriteLine($"[Market API] Quote request for {symbol}");

            var (success, json, errorMsg) = await RunPythonFetcher($"quote --symbol {symbol}");
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

                return Ok(new
                {
                    symbol = root.GetProperty("symbol").GetString(),
                    price = root.GetProperty("price").GetDouble(),
                    timestampUtc = root.GetProperty("timestampUtc").GetString()
                });
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[Market API] JSON parse error: {ex.Message}");
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
            [FromQuery] string? to = null)
        {
            // Validate symbol
            if (string.IsNullOrWhiteSpace(symbol) || !SymbolRegex.IsMatch(symbol.ToUpperInvariant()))
                return BadRequest(new { error = "Invalid symbol." });

            symbol = symbol.ToUpperInvariant();
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

            Console.WriteLine($"[Market API] Candles request: {symbol} tf={tf} range={range} limit={limit} to={to ?? "latest"}");

            // Build python args
            var args = $"candles --symbol {symbol} --tf {tf} --range {range} --limit {limit}";
            if (to != null) args += $" --to {to}";

            var (success, json, errorMsg) = await RunPythonFetcher(args);
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
                return Ok(new
                {
                    symbol = root.GetProperty("symbol").GetString(),
                    tf = root.GetProperty("tf").GetString(),
                    candles = root.GetProperty("candles").Clone(),
                    nextTo = root.TryGetProperty("nextTo", out var ntProp) && ntProp.ValueKind != JsonValueKind.Null
                        ? ntProp.GetInt64() : (long?)null
                });
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[Market API] JSON parse error: {ex.Message}");
                return StatusCode(502, new { error = "Invalid response from data fetcher." });
            }
        }


        /// <summary>
        /// Runs fetchmarketdata.py with the given arguments.
        /// Drains both stdout and stderr to avoid deadlocks. Has a timeout.
        /// </summary>
        private static async Task<(bool Success, string StdOut, string Error)> RunPythonFetcher(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"fetchmarketdata.py {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                    return (false, "", "Failed to start Python process.");

                // Drain stdout and stderr concurrently to prevent deadlocks
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                bool exited = process.WaitForExit(PythonTimeoutMs);
                if (!exited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return (false, "", "Python process timed out.");
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"[Market API] Python stderr: {stderr}");
                    return (false, "", $"Python exited with code {process.ExitCode}: {stderr}");
                }

                if (string.IsNullOrWhiteSpace(stdout))
                    return (false, "", "Python returned empty output.");

                return (true, stdout.Trim(), "");
            }
            catch (Exception ex)
            {
                return (false, "", $"Process error: {ex.Message}");
            }
        }
    }


    // ====================================================================
    // AI CONTROLLER — The AI Brain (with uiActions + open_chart tool)
    // ====================================================================
    [ApiController]
    [Route("api/ai")]
    public class AiController : ControllerBase
    {
        static HttpClient client = new HttpClient();
        static string modelId = "gpt-4o-mini";

        private readonly AppDbContext _db;
        private readonly TradingService _tradingService;
        private readonly IConfiguration _config;

        public AiController(AppDbContext db, TradingService tradingService, IConfiguration config)
        {
            _db = db;
            _tradingService = tradingService;
            _config = config;

            // Read keys from configuration (appsettings.json / env vars)
            string apiKey = _config["OpenAI:ApiKey"] ?? "";
            if (!client.DefaultRequestHeaders.Contains("Authorization") && !string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }
        }

        // 2. THE POST ENDPOINT
        [HttpPost("ask")]
        public async Task<IActionResult> AskTheAgent([FromBody] ChatRequest request)
        {
            Console.WriteLine($"\n[Server] Unity asked: {request.Message}");
            string threadId = !string.IsNullOrWhiteSpace(request.ThreadId) ? request.ThreadId : "default-user-session";

            // Load memory from the SQLite Database
            var rawHistory = await _db.ChatMessages
                .Where(m => m.ThreadId == threadId)
                .OrderBy(m => m.CreatedAtUtc)
                .ToListAsync();

            var messageHistory = new List<object>();
            foreach (var msg in rawHistory)
            {
                messageHistory.Add(new { role = msg.Role, content = msg.Content });
            }

            // Trigger the AI Agent — now returns text + uiActions
            var (aiResponse, uiActions) = await RunAutonomousAgent(messageHistory, request.Message, threadId);

            Console.WriteLine($"[Server] Sending answer back to Unity...");
            return Ok(new { response = aiResponse, uiActions = uiActions });
        }

        // 3. AI ENGINE — Returns (responseText, uiActions)
        private async Task<(string ResponseText, List<object> UiActions)> RunAutonomousAgent(
            List<object> history, string userMessage, string threadId)
        {
            string apiKey = _config["OpenAI:ApiKey"] ?? "";
            string finnhubKey = _config["Finnhub:ApiKey"] ?? "";

            // Save user message to Database
            _db.ChatMessages.Add(new ChatMessage { ThreadId = threadId, Role = "user", Content = userMessage });
            await _db.SaveChangesAsync();

            history.Add(new { role = "user", content = userMessage });

            // Collect UI actions triggered by tool calls during this request
            var uiActions = new List<object>();

            var toolsArray = new object[]
            {
                new {
                    type = "function",
                    function = new {
                        name = "fetch_market_data",
                        description = "Fetches the live stock price and latest news for a given company.",
                        parameters = new {
                            type = "object",
                            properties = new { symbol = new { type = "string", description = "The stock ticker symbol, e.g., AAPL" } },
                            required = new[] { "symbol" }
                        }
                    }
                },
                new {
                    type = "function",
                    function = new {
                        name = "execute_trade",
                        description = "Buys or sells shares of a stock and updates the user's portfolio.",
                        parameters = new {
                            type = "object",
                            properties = new {
                                action = new { type = "string", description = "Must be either 'buy' or 'sell'" },
                                symbol = new { type = "string", description = "The stock ticker symbol, e.g., AAPL" },
                                shares = new { type = "integer", description = "The number of shares to trade" },
                                price = new { type = "number", description = "The current price per share." }
                            },
                            required = new[] { "action", "symbol", "shares", "price" }
                        }
                    }
                },
                new {
                    type = "function",
                    function = new {
                        name = "open_chart",
                        description = "Opens an interactive candlestick price chart for a given stock symbol. Use this when the user wants to see a chart, graph, or visual price history.",
                        parameters = new {
                            type = "object",
                            properties = new {
                                symbol = new { type = "string", description = "The stock ticker symbol, e.g., AAPL" },
                                tf = new { type = "string", description = "Timeframe: 1m, 5m, 15m, 1h, 1d, 1w, or 1mo. Defaults to 1d.", @enum = new[] { "1m", "5m", "15m", "1h", "1d", "1w", "1mo" } },
                                range = new { type = "string", description = "Date range: 7d, 30d, 90d, 180d, 1y, or 2y. Defaults to 180d.", @enum = new[] { "7d", "30d", "90d", "180d", "1y", "2y" } }
                            },
                            required = new[] { "symbol" }
                        }
                    }
                }
            };

            bool isAgentThinking = true;
            string finalAiResponse = "";

            while (isAgentThinking)
            {
                var payload = new { model = modelId, messages = history, temperature = 0.3, tools = toolsArray, tool_choice = "auto" };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                try
                {
                    var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);
                    string responseString = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode) return ($"Error: API returned {response.StatusCode}\n{responseString}", uiActions);

                    using (JsonDocument doc = JsonDocument.Parse(responseString))
                    {
                        var choice = doc.RootElement.GetProperty("choices")[0];
                        string finishReason = choice.GetProperty("finish_reason").GetString() ?? "";
                        var message = choice.GetProperty("message");

                        if (finishReason == "tool_calls")
                        {
                            int toolCount = message.GetProperty("tool_calls").GetArrayLength();
                            Console.WriteLine($"\n[Agent Engine] AI requested {toolCount} tool(s) in a single breath!");

                            history.Add(new
                            {
                                role = "assistant",
                                content = (string?)null,
                                tool_calls = message.GetProperty("tool_calls").Clone()
                            });

                            foreach (var toolCall in message.GetProperty("tool_calls").EnumerateArray())
                            {
                                string toolCallId = toolCall.GetProperty("id").GetString() ?? "";
                                string functionName = toolCall.GetProperty("function").GetProperty("name").GetString() ?? "";
                                string arguments = toolCall.GetProperty("function").GetProperty("arguments").GetString() ?? "";

                                Console.ForegroundColor = ConsoleColor.Magenta;
                                Console.WriteLine($"[Agent Engine] Executing Tool: {functionName}...");
                                Console.ResetColor();

                                string toolResult = "";

                                try
                                {
                                    if (functionName == "fetch_market_data")
                                    {
                                        using (JsonDocument argDoc = JsonDocument.Parse(arguments))
                                        {
                                            string symbol = argDoc.RootElement.GetProperty("symbol").GetString() ?? "";
                                            var data = await FetchMarketData(symbol, finnhubKey);
                                            toolResult = $"Price: {data.price}\nNews: {data.news}";
                                        }
                                    }
                                    else if (functionName == "execute_trade")
                                    {
                                        using (JsonDocument argDoc = JsonDocument.Parse(arguments))
                                        {
                                            string action = argDoc.RootElement.GetProperty("action").GetString() ?? "".ToLower();
                                            string symbol = argDoc.RootElement.GetProperty("symbol").GetString() ?? "".ToUpper();
                                            int shares = argDoc.RootElement.GetProperty("shares").GetInt32();
                                            decimal price = argDoc.RootElement.GetProperty("price").GetDecimal();

                                            var tradeReq = new TradeRequest
                                            {
                                                ClientRequestId = Guid.NewGuid().ToString(),
                                                Symbol = symbol,
                                                Side = action.ToUpper(),
                                                Quantity = shares,
                                                ExecutedPrice = price,
                                                Currency = "USD"
                                            };

                                            var result = await _tradingService.ExecuteTradeAsync(tradeReq);

                                            if (result.Success)
                                            {
                                                toolResult = $"SUCCESS: {action.ToUpper()} {shares} shares of {symbol} at ${price}.";
                                            }
                                            else
                                            {
                                                toolResult = $"ERROR: {result.ErrorMessage}";
                                            }
                                        }
                                    }
                                    else if (functionName == "open_chart")
                                    {
                                        using (JsonDocument argDoc = JsonDocument.Parse(arguments))
                                        {
                                            string symbol = (argDoc.RootElement.GetProperty("symbol").GetString() ?? "AMD").ToUpperInvariant();
                                            string tf = argDoc.RootElement.TryGetProperty("tf", out var tfProp)
                                                ? tfProp.GetString() ?? "1d" : "1d";
                                            string range = argDoc.RootElement.TryGetProperty("range", out var rangeProp)
                                                ? rangeProp.GetString() ?? "180d" : "180d";

                                            string chartUrl = $"/chart/index.html?symbol={symbol}&tf={tf}&range={range}";

                                            // Append UI action — do NOT fetch candles here
                                            uiActions.Add(new
                                            {
                                                type = "openChart",
                                                symbol = symbol,
                                                tf = tf,
                                                range = range,
                                                url = chartUrl
                                            });

                                            toolResult = $"Chart opened for {symbol} tf={tf} range={range}";
                                            Console.WriteLine($"[Agent Engine] UI Action: openChart -> {chartUrl}");
                                        }
                                    }
                                    else
                                    {
                                        toolResult = $"SYSTEM ERROR: Tool '{functionName}' does not exist.";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    toolResult = $"SYSTEM ERROR: Exception: {ex.Message}";
                                }

                                history.Add(new
                                {
                                    role = "tool",
                                    tool_call_id = toolCallId,
                                    name = functionName,
                                    content = toolResult
                                });
                            }
                        }
                        else
                        {
                            finalAiResponse = message.GetProperty("content").GetString() ?? "";
                            history.Add(new { role = "assistant", content = finalAiResponse });

                            // Save AI response to DB
                            _db.ChatMessages.Add(new ChatMessage { ThreadId = threadId, Role = "assistant", Content = finalAiResponse });
                            await _db.SaveChangesAsync();

                            isAgentThinking = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    return ($"Connection Failed: {ex.Message}", uiActions);
                }
            }

            return (finalAiResponse, uiActions);
        }

        // ====================================================================
        // DATA SCRAPERS
        // ====================================================================
        private async Task<(string price, string news)> FetchMarketData(string symbol, string finnhubKey)
        {
            var priceTask = GetStockPrice(symbol, finnhubKey);
            var newsTask = RunPythonHunter(symbol);
            await Task.WhenAll(priceTask, newsTask);
            return (priceTask.Result, newsTask.Result);
        }

        private async Task<string> GetStockPrice(string symbol, string finnhubKey)
        {
            string url = $"https://finnhub.io/api/v1/quote?symbol={symbol.ToUpper()}&token={finnhubKey.Trim()}";
            var processInfo = new ProcessStartInfo
            {
                FileName = "curl.exe",
                Arguments = $"-k -s -S -L -4 --ssl-no-revoke --http1.1 --retry 3 --retry-delay 2 -H \"User-Agent: Mozilla/5.0\" \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(processInfo))
                {
                    if (process == null) return "Error: Could not start Curl.";

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    bool exited = process.WaitForExit(15_000);
                    if (!exited)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        return "Error: Curl timed out.";
                    }

                    string json = await stdoutTask;
                    await stderrTask; // drain stderr

                    if (string.IsNullOrWhiteSpace(json)) return "Error: Connection Failed.";

                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("c", out JsonElement priceEl) && priceEl.ValueKind != JsonValueKind.Null)
                        {
                            double price = priceEl.GetDouble();
                            if (price == 0) return "Error: Symbol not found.";
                            return $"${price}";
                        }
                        return "Error: Symbol not found.";
                    }
                }
            }
            catch (Exception ex) { return $"SYSTEM ERROR: {ex.Message}"; }
        }

        private async Task<string> RunPythonHunter(string symbol)
        {
            var pythonInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"fetch_news.py {symbol}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(pythonInfo))
                {
                    if (process == null) return "Error: Could not start Python.";

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    bool exited = process.WaitForExit(30_000);
                    if (!exited)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        return "Error: Python timed out.";
                    }

                    string output = await stdoutTask;
                    await stderrTask; // drain stderr

                    return string.IsNullOrWhiteSpace(output) ? "No data returned." : output;
                }
            }
            catch (Exception ex) { return $"System Error: {ex.Message}"; }
        }
    }

    // ====================================================================
    // REQUEST DTO
    // ====================================================================
    public class ChatRequest
    {
        public string Message { get; set; } = "";
        public string? ThreadId { get; set; }
    }
}
