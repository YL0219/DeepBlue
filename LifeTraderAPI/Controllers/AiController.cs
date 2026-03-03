// CONTRACT / INVARIANTS
// - Route: POST /api/ai/ask
// - AI agent loop: sends user message to OpenAI, dispatches tool calls, returns response.
// - Response shape: { response, uiActions, terminatedByCircuitBreaker, iterations }
// - Read-only tools (fetch_market_data, open_chart) run in PARALLEL via Task.WhenAll.
// - State-changing tools (execute_trade) run SERIALLY, max 1 per turn.
// - Each tool task gets an ISOLATED DI scope (fresh DbContext). NEVER share DbContext across threads.
// - history and uiActions are NEVER mutated inside parallel tasks — merged on main thread only.
// - ToolRuns persisted in a single batch SaveChanges per turn for observability.
// - Circuit breaker: max 10 iterations per request to prevent runaway token consumption.
// - Symbols validated via SymbolValidator before any external call or Python arg.

using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using LifeTrader_AI.Data;
using LifeTrader_AI.Models;
using LifeTrader_AI.Services;
using LifeTrader_AI.Infrastructure;

namespace LifeTraderAPI.Controllers
{
    [ApiController]
    [Route("api/ai")]
    public class AiController : ControllerBase
    {
        // === Hard limits ===
        private const int MAX_AGENT_ITERATIONS = 10;      // Circuit breaker: max OpenAI loop iterations
        private const int MAX_TOOLS_PER_TURN = 6;         // Max tool calls honored per single AI response
        private const int MAX_PARALLEL_TOOL_TASKS = 4;    // Concurrent read-only tool task cap per turn
        private const int MAX_STATE_CHANGING_PER_TURN = 1; // Only 1 write tool per turn
        private const int PythonTimeoutMs = 20_000;        // Hard timeout for python processes
        private const int CurlTimeoutMs = 15_000;          // Hard timeout for curl (Finnhub)

        // Tools that mutate state — serialized, limited to 1 per AI turn.
        // All other tools are treated as read-only and safe to parallelize.
        private static readonly HashSet<string> StateChangingTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "execute_trade"
        };

        static HttpClient client = new HttpClient();
        static string modelId = "gpt-4o-mini";

        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly SemaphoreSlim _pythonGate;
        private readonly IMemoryCache _cache;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly PythonPathResolver _pythonPath;
        private readonly ILogger<AiController> _logger;

        // --- Internal DTOs for parallel tool execution ---

        /// <summary>Parsed tool_call from OpenAI response, preserving original array index.</summary>
        private sealed record ToolCallInfo(int Index, string ToolCallId, string ToolName, string ArgumentsJson);

        /// <summary>Data needed to persist a single ToolRun row after execution.</summary>
        private sealed record ToolRunPayload(
            string ThreadId, string ToolName, string ArgumentsJson, string ResultJson,
            long ExecutionTimeMs, bool IsSuccess, DateTime CreatedAtUtc);

        /// <summary>Complete output of one tool execution. Collected per-task, merged on main thread.</summary>
        private sealed record ToolExecResult(
            int Index, string ToolCallId, string ToolName,
            string ToolContent, List<object> UiActionsLocal, ToolRunPayload RunPayload);


        public AiController(
            AppDbContext db,
            IConfiguration config,
            SemaphoreSlim pythonGate,
            IMemoryCache cache,
            IServiceScopeFactory scopeFactory,
            PythonPathResolver pythonPath,
            ILogger<AiController> logger)
        {
            _db = db;
            _config = config;
            _pythonGate = pythonGate;
            _cache = cache;
            _scopeFactory = scopeFactory;
            _pythonPath = pythonPath;
            _logger = logger;

            // TODO: Migrate API keys to user-secrets or environment variables (separate task).
            // Keys in appsettings.json is a security risk in shared/production environments.
            string apiKey = _config["OpenAI:ApiKey"] ?? "";
            if (!client.DefaultRequestHeaders.Contains("Authorization") && !string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }
        }


        private static bool IsStateChangingTool(string toolName) => StateChangingTools.Contains(toolName);

        /// <summary>
        /// Creates a warning/error result for a tool call that was rejected by policy
        /// (overflow beyond MAX_TOOLS_PER_TURN, or duplicate state-changing call).
        /// Still logged as a ToolRun for full observability.
        /// </summary>
        private static ToolExecResult MakeWarningResult(ToolCallInfo call, string warningMsg, string threadId)
        {
            return new ToolExecResult(
                call.Index, call.ToolCallId, call.ToolName, warningMsg,
                new List<object>(),
                new ToolRunPayload(threadId, call.ToolName, call.ArgumentsJson, warningMsg, 0, false, DateTime.UtcNow));
        }


        // ================================================================
        // 2. THE POST ENDPOINT
        // ================================================================
        [HttpPost("ask")]
        public async Task<IActionResult> AskTheAgent([FromBody] ChatRequest request, CancellationToken ct)
        {
            _logger.LogInformation("[AI] Request received: {Message}", request.Message);
            string threadId = !string.IsNullOrWhiteSpace(request.ThreadId) ? request.ThreadId : "default-user-session";

            // Load memory from the SQLite Database
            var rawHistory = await _db.ChatMessages
                .Where(m => m.ThreadId == threadId)
                .OrderBy(m => m.CreatedAtUtc)
                .ToListAsync(ct);

            var messageHistory = new List<object>();
            foreach (var msg in rawHistory)
            {
                messageHistory.Add(new { role = msg.Role, content = msg.Content });
            }

            // Trigger the AI Agent — returns text + uiActions + circuit breaker metadata
            var (aiResponse, uiActions, terminatedByCircuitBreaker, iterations) =
                await RunAutonomousAgent(messageHistory, request.Message, threadId, ct);

            _logger.LogInformation("[AI] Response sent (iterations={Iterations}, circuitBreaker={CircuitBreaker})",
                iterations, terminatedByCircuitBreaker);
            return Ok(new
            {
                response = aiResponse,
                uiActions = uiActions,
                terminatedByCircuitBreaker = terminatedByCircuitBreaker,
                iterations = iterations
            });
        }


        // ================================================================
        // 3. AI ENGINE — Autonomous agent loop with parallel tool dispatch
        // ================================================================
        private async Task<(string ResponseText, List<object> UiActions, bool TerminatedByCircuitBreaker, int Iterations)>
            RunAutonomousAgent(List<object> history, string userMessage, string threadId, CancellationToken ct)
        {
            string finnhubKey = _config["Finnhub:ApiKey"] ?? "";

            // Save user message to Database
            _db.ChatMessages.Add(new ChatMessage { ThreadId = threadId, Role = "user", Content = userMessage });
            await _db.SaveChangesAsync(ct);

            history.Add(new { role = "user", content = userMessage });

            // Collect UI actions triggered by tool calls during this request.
            // THREAD SAFETY: never mutated inside parallel tasks — only merged on main thread.
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
            int iteration = 0;

            while (isAgentThinking)
            {
                // === CIRCUIT BREAKER: Anti-runaway token protection ===
                iteration++;
                if (iteration > MAX_AGENT_ITERATIONS)
                {
                    string warningMsg = $"[Circuit Breaker] Agent terminated after {MAX_AGENT_ITERATIONS} iterations to prevent runaway token consumption.";
                    _logger.LogWarning("[AI] {Warning}", warningMsg);

                    // Persist warning to chat history
                    _db.ChatMessages.Add(new ChatMessage
                    {
                        ThreadId = threadId,
                        Role = "system",
                        Content = warningMsg
                    });
                    await _db.SaveChangesAsync(ct);

                    string safeResponse = string.IsNullOrEmpty(finalAiResponse)
                        ? warningMsg
                        : $"{finalAiResponse}\n\n{warningMsg}";

                    return (safeResponse, uiActions, true, iteration - 1);
                }

                var payload = new { model = modelId, messages = history, temperature = 0.3, tools = toolsArray, tool_choice = "auto" };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                try
                {
                    var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", content, ct);
                    string responseString = await response.Content.ReadAsStringAsync(ct);

                    if (!response.IsSuccessStatusCode) return ($"Error: API returned {response.StatusCode}\n{responseString}", uiActions, false, iteration);

                    using (JsonDocument doc = JsonDocument.Parse(responseString))
                    {
                        var choice = doc.RootElement.GetProperty("choices")[0];
                        string finishReason = choice.GetProperty("finish_reason").GetString() ?? "";
                        var message = choice.GetProperty("message");

                        if (finishReason == "tool_calls")
                        {
                            int toolCount = message.GetProperty("tool_calls").GetArrayLength();
                            _logger.LogInformation("[AI] AI requested {ToolCount} tool(s) (iteration {Iter}/{Max})",
                                toolCount, iteration, MAX_AGENT_ITERATIONS);

                            // Step 1: Append the assistant message with tool_calls to history
                            history.Add(new
                            {
                                role = "assistant",
                                content = (string?)null,
                                tool_calls = message.GetProperty("tool_calls").Clone()
                            });

                            // Step 2: Parse all tool_calls into ToolCallInfo with original index
                            var allCalls = new List<ToolCallInfo>();
                            int idx = 0;
                            foreach (var tc in message.GetProperty("tool_calls").EnumerateArray())
                            {
                                allCalls.Add(new ToolCallInfo(
                                    idx++,
                                    tc.GetProperty("id").GetString() ?? "",
                                    tc.GetProperty("function").GetProperty("name").GetString() ?? "",
                                    tc.GetProperty("function").GetProperty("arguments").GetString() ?? ""));
                            }

                            // Step 3: Enforce MAX_TOOLS_PER_TURN — reject overflow
                            var activeCalls = allCalls.Take(MAX_TOOLS_PER_TURN).ToList();
                            var overflowCalls = allCalls.Skip(MAX_TOOLS_PER_TURN).ToList();

                            // Step 4: Partition into read-only vs state-changing
                            var readOnlyCalls = activeCalls.Where(c => !IsStateChangingTool(c.ToolName)).ToList();
                            var writeCalls = activeCalls.Where(c => IsStateChangingTool(c.ToolName)).ToList();

                            // Step 5: Enforce MAX_STATE_CHANGING_PER_TURN
                            ToolCallInfo? allowedWrite = writeCalls.FirstOrDefault();
                            var rejectedWrites = writeCalls.Skip(MAX_STATE_CHANGING_PER_TURN).ToList();

                            var allResults = new List<ToolExecResult>();

                            // Log rejected overflow calls
                            foreach (var oc in overflowCalls)
                            {
                                _logger.LogWarning("[AI] Overflow: tool '{ToolName}' rejected (>{Max} per turn).",
                                    oc.ToolName, MAX_TOOLS_PER_TURN);
                                allResults.Add(MakeWarningResult(oc,
                                    $"SYSTEM WARNING: max {MAX_TOOLS_PER_TURN} tool calls per turn exceeded; this call was ignored.", threadId));
                            }

                            // Log rejected duplicate state-changing calls
                            foreach (var rw in rejectedWrites)
                            {
                                _logger.LogWarning("[AI] Policy: duplicate state-changing tool '{ToolName}' rejected.", rw.ToolName);
                                allResults.Add(MakeWarningResult(rw,
                                    "SYSTEM WARNING: multiple state-changing tools requested in one turn; only the first execute_trade is allowed.", threadId));
                            }

                            // Step 6: Execute read-only tools in PARALLEL with bounded concurrency.
                            if (readOnlyCalls.Count > 0)
                            {
                                using var toolGate = new SemaphoreSlim(MAX_PARALLEL_TOOL_TASKS, MAX_PARALLEL_TOOL_TASKS);

                                var readTasks = readOnlyCalls.Select(async call =>
                                {
                                    await toolGate.WaitAsync(ct);
                                    try
                                    {
                                        return await ExecuteToolInIsolatedScopeAsync(call, threadId, finnhubKey, ct);
                                    }
                                    finally
                                    {
                                        toolGate.Release();
                                    }
                                }).ToList();

                                var readResults = await Task.WhenAll(readTasks);
                                allResults.AddRange(readResults);
                            }

                            // Step 7: Execute the single allowed state-changing tool SERIALLY after reads.
                            if (allowedWrite != null)
                            {
                                var writeResult = await ExecuteToolInIsolatedScopeAsync(allowedWrite, threadId, finnhubKey, ct);
                                allResults.Add(writeResult);
                            }

                            // Step 8: Sort by original index and merge into shared state.
                            allResults.Sort((a, b) => a.Index.CompareTo(b.Index));

                            foreach (var r in allResults)
                            {
                                history.Add(new
                                {
                                    role = "tool",
                                    tool_call_id = r.ToolCallId,
                                    name = r.ToolName,
                                    content = r.ToolContent
                                });
                                uiActions.AddRange(r.UiActionsLocal);
                            }

                            // Step 9: Persist all ToolRuns in a single batch.
                            try
                            {
                                foreach (var r in allResults)
                                {
                                    var p = r.RunPayload;
                                    _db.ToolRuns.Add(new ToolRun
                                    {
                                        ThreadId = p.ThreadId,
                                        ToolName = p.ToolName,
                                        ArgumentsJson = p.ArgumentsJson,
                                        ResultJson = p.ResultJson,
                                        ExecutionTimeMs = p.ExecutionTimeMs,
                                        IsSuccess = p.IsSuccess,
                                        CreatedAtUtc = p.CreatedAtUtc
                                    });
                                }
                                await _db.SaveChangesAsync(ct);
                            }
                            catch (Exception logEx)
                            {
                                _logger.LogError(logEx, "[AI] Failed to batch-log ToolRuns.");
                            }
                        }
                        else
                        {
                            finalAiResponse = message.GetProperty("content").GetString() ?? "";
                            history.Add(new { role = "assistant", content = finalAiResponse });

                            _db.ChatMessages.Add(new ChatMessage { ThreadId = threadId, Role = "assistant", Content = finalAiResponse });
                            await _db.SaveChangesAsync(ct);

                            isAgentThinking = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    return ($"Connection Failed: {ex.Message}", uiActions, false, iteration);
                }
            }

            return (finalAiResponse, uiActions, false, iteration);
        }


        // ================================================================
        // 4. ISOLATED TOOL EXECUTION — One DI scope per tool call
        //
        //    Each scope provides an independent DbContext + TradingService.
        //    Singletons (IMemoryCache, SemaphoreSlim) resolve to the same instance — safe.
        //    Returns a ToolExecResult; the caller merges results on the main thread.
        // ================================================================
        private async Task<ToolExecResult> ExecuteToolInIsolatedScopeAsync(
            ToolCallInfo call, string threadId, string finnhubKey, CancellationToken ct)
        {
            _logger.LogInformation("[AI] Executing tool: {ToolName} (idx={Index})", call.ToolName, call.Index);

            var sw = Stopwatch.StartNew();
            string toolResult = "";
            var localUiActions = new List<object>();

            try
            {
                using var scope = _scopeFactory.CreateScope();

                switch (call.ToolName)
                {
                    case "fetch_market_data":
                        {
                            using var argDoc = JsonDocument.Parse(call.ArgumentsJson);
                            string rawSymbol = argDoc.RootElement.GetProperty("symbol").GetString() ?? "";

                            if (!SymbolValidator.TryNormalize(rawSymbol, out var symbol))
                            {
                                toolResult = "ERROR: Invalid symbol format. Must be 1-15 alphanumeric characters.";
                                break;
                            }

                            // Check cache first (10s TTL). IMemoryCache is singleton — thread-safe.
                            string cacheKey = $"quote:{symbol}";
                            string? cachedResult = _cache.Get<string>(cacheKey);

                            if (cachedResult != null)
                            {
                                toolResult = cachedResult;
                                _logger.LogDebug("[AI] Cache hit: {CacheKey}", cacheKey);
                            }
                            else
                            {
                                var data = await FetchMarketData(symbol, finnhubKey, ct);
                                toolResult = $"Price: {data.price}\nNews: {data.news}";

                                if (data.price.StartsWith("$"))
                                {
                                    _cache.Set(cacheKey, toolResult, TimeSpan.FromSeconds(10));
                                }
                            }
                            break;
                        }

                    case "execute_trade":
                        {
                            var tradingService = scope.ServiceProvider.GetRequiredService<TradingService>();

                            using var argDoc = JsonDocument.Parse(call.ArgumentsJson);
                            string action = (argDoc.RootElement.GetProperty("action").GetString() ?? "").ToLowerInvariant();
                            string rawSymbol = argDoc.RootElement.GetProperty("symbol").GetString() ?? "";
                            int shares = argDoc.RootElement.GetProperty("shares").GetInt32();
                            decimal price = argDoc.RootElement.GetProperty("price").GetDecimal();

                            if (!SymbolValidator.TryNormalize(rawSymbol, out var symbol))
                            {
                                toolResult = "ERROR: Invalid symbol format.";
                                break;
                            }

                            var tradeReq = new TradeRequest
                            {
                                ClientRequestId = Guid.NewGuid().ToString(),
                                Symbol = symbol,
                                Side = action.ToUpperInvariant(),
                                Quantity = shares,
                                ExecutedPrice = price,
                                Currency = "USD"
                            };

                            var result = await tradingService.ExecuteTradeAsync(tradeReq);

                            toolResult = result.Success
                                ? $"SUCCESS: {action.ToUpperInvariant()} {shares} shares of {symbol} at ${price}."
                                : $"ERROR: {result.ErrorMessage}";
                            break;
                        }

                    case "open_chart":
                        {
                            using var argDoc = JsonDocument.Parse(call.ArgumentsJson);
                            string rawSymbol = argDoc.RootElement.GetProperty("symbol").GetString() ?? "AMD";

                            if (!SymbolValidator.TryNormalize(rawSymbol, out var symbol))
                            {
                                toolResult = "ERROR: Invalid symbol format.";
                                break;
                            }

                            string tf = argDoc.RootElement.TryGetProperty("tf", out var tfProp)
                                ? tfProp.GetString() ?? "1d" : "1d";
                            string range = argDoc.RootElement.TryGetProperty("range", out var rangeProp)
                                ? rangeProp.GetString() ?? "180d" : "180d";

                            string chartUrl = $"/chart/index.html?symbol={symbol}&tf={tf}&range={range}";

                            localUiActions.Add(new
                            {
                                type = "openChart",
                                symbol = symbol,
                                tf = tf,
                                range = range,
                                url = chartUrl
                            });

                            toolResult = $"Chart opened for {symbol} tf={tf} range={range}";
                            _logger.LogInformation("[AI] UI action: openChart -> {ChartUrl}", chartUrl);
                            break;
                        }

                    default:
                        toolResult = $"SYSTEM ERROR: Tool '{call.ToolName}' does not exist.";
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                toolResult = "SYSTEM ERROR: Operation was cancelled.";
            }
            catch (Exception ex)
            {
                toolResult = $"SYSTEM ERROR: Exception: {ex.Message}";
            }

            sw.Stop();

            bool isSuccess = !string.IsNullOrEmpty(toolResult)
                && !toolResult.StartsWith("ERROR")
                && !toolResult.StartsWith("Error:")
                && !toolResult.StartsWith("SYSTEM ERROR")
                && !toolResult.StartsWith("SYSTEM WARNING");

            var runPayload = new ToolRunPayload(
                threadId, call.ToolName, call.ArgumentsJson, toolResult,
                sw.ElapsedMilliseconds, isSuccess, DateTime.UtcNow);

            _logger.LogInformation("[AI] Tool '{ToolName}' completed in {Ms}ms (success={Success})",
                call.ToolName, sw.ElapsedMilliseconds, isSuccess);

            return new ToolExecResult(call.Index, call.ToolCallId, call.ToolName,
                toolResult, localUiActions, runPayload);
        }


        // ====================================================================
        // DATA SCRAPERS — Use only singletons (cache, python gate, config).
        //   Safe to call from parallel tasks because they never touch DbContext.
        // ====================================================================
        private async Task<(string price, string news)> FetchMarketData(
            string symbol, string finnhubKey, CancellationToken ct)
        {
            var priceTask = GetStockPrice(symbol, finnhubKey, ct);
            var newsTask = RunPythonHunter(symbol, ct);
            await Task.WhenAll(priceTask, newsTask);
            return (priceTask.Result, newsTask.Result);
        }

        /// <summary>
        /// Fetches the current stock price from Finnhub via curl.
        /// Uses ProcessRunner with ArgumentList (injection-safe).
        /// </summary>
        private async Task<string> GetStockPrice(string symbol, string finnhubKey, CancellationToken ct)
        {
            string normalized = SymbolValidator.Normalize(symbol);
            string url = $"https://finnhub.io/api/v1/quote?symbol={normalized}&token={finnhubKey.Trim()}";

            var result = await ProcessRunner.RunAsync(
                "curl.exe",
                new[] { "-k", "-s", "-S", "-L", "-4", "--ssl-no-revoke", "--http1.1",
                        "--retry", "3", "--retry-delay", "2",
                        "-H", "User-Agent: Mozilla/5.0", url },
                CurlTimeoutMs, ct);

            if (result.TimedOut)
                return "Error: Curl timed out.";

            if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
                return "Error: Connection Failed.";

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(result.Stdout))
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
            catch (Exception ex) { return $"SYSTEM ERROR: {ex.Message}"; }
        }

        /// <summary>
        /// Runs fetch_news.py gated by SemaphoreSlim to limit concurrent python processes.
        /// Uses ProcessRunner with ArgumentList (injection-safe).
        /// The python gate is a singleton — concurrent calls from parallel tool tasks
        /// safely contend on the same semaphore (global cap of 3 python processes).
        /// </summary>
        private async Task<string> RunPythonHunter(string symbol, CancellationToken ct)
        {
            if (!_pythonPath.IsAvailable)
                return "SYSTEM ERROR: Python not available. Run setup_venv.ps1.";

            string normalized = SymbolValidator.Normalize(symbol);

            await _pythonGate.WaitAsync(ct);
            try
            {
                var result = await ProcessRunner.RunAsync(
                    _pythonPath.ExePath,
                    new[] { "fetch_news.py", normalized },
                    PythonTimeoutMs, ct);

                if (result.TimedOut)
                    return "SYSTEM ERROR: Python process timed out (20s).";

                if (!result.Success)
                    return string.IsNullOrWhiteSpace(result.Stderr)
                        ? "No data returned."
                        : $"SYSTEM ERROR: {result.Stderr}";

                return string.IsNullOrWhiteSpace(result.Stdout) ? "No data returned." : result.Stdout;
            }
            catch (Exception ex) { return $"SYSTEM ERROR: {ex.Message}"; }
            finally
            {
                _pythonGate.Release();
            }
        }
    }
}
