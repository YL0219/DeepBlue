using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

        private readonly IMemoryCache _cache;
        private readonly SemaphoreSlim _pythonGate;

        public MarketController(IMemoryCache cache, SemaphoreSlim pythonGate)
        {
            _cache = cache;
            _pythonGate = pythonGate;
        }


        // GET /api/market/quote?symbol=AMD
        [HttpGet("quote")]
        public async Task<IActionResult> GetQuote([FromQuery] string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol) || !SymbolRegex.IsMatch(symbol.ToUpperInvariant()))
                return BadRequest(new { error = "Invalid symbol. Must be 1-10 uppercase alphanumeric characters." });

            symbol = symbol.ToUpperInvariant();

            // Check cache first
            string cacheKey = $"quote:{symbol}";
            if (_cache.TryGetValue(cacheKey, out object? cachedQuote))
            {
                Console.WriteLine($"[Market API] Cache hit for {cacheKey}");
                return Ok(cachedQuote);
            }

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

            // Check cache first
            string cacheKey = $"candles:{symbol}:{tf}:{range}:{limit}:{to ?? "latest"}";
            if (_cache.TryGetValue(cacheKey, out object? cachedCandles))
            {
                Console.WriteLine($"[Market API] Cache hit for {cacheKey}");
                return Ok(cachedCandles);
            }

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
                Console.WriteLine($"[Market API] JSON parse error: {ex.Message}");
                return StatusCode(502, new { error = "Invalid response from data fetcher." });
            }
        }


        /// <summary>
        /// Runs fetchmarketdata.py with the given arguments.
        /// Gated by SemaphoreSlim to limit concurrent python processes.
        /// Drains both stdout and stderr to avoid deadlocks. Has a timeout.
        /// </summary>
        private async Task<(bool Success, string StdOut, string Error)> RunPythonFetcher(string arguments)
        {
            await _pythonGate.WaitAsync();
            try
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

                    using var cts = new CancellationTokenSource(PythonTimeoutMs);
                    try
                    {
                        await process.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
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
            finally
            {
                _pythonGate.Release();
            }
        }
    }


    // ====================================================================
    // AI CONTROLLER — The AI Brain (with uiActions + open_chart tool)
    // Phase 3: Circuit breaker, ToolRun observability, process throttle, caching
    // Optimization: Read-only tools execute in parallel via Task.WhenAll;
    //               state-changing tools (execute_trade) run serially after reads.
    // ====================================================================
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
            IServiceScopeFactory scopeFactory)
        {
            _db = db;
            _config = config;
            _pythonGate = pythonGate;
            _cache = cache;
            _scopeFactory = scopeFactory;

            // Read keys from configuration (appsettings.json / env vars)
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
            Console.WriteLine($"\n[Server] Unity asked: {request.Message}");
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

            Console.WriteLine($"[Server] Sending answer back to Unity... (iterations={iterations}, circuitBreaker={terminatedByCircuitBreaker})");
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
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Agent Engine] {warningMsg}");
                    Console.ResetColor();

                    // Persist warning to chat history
                    _db.ChatMessages.Add(new ChatMessage
                    {
                        ThreadId = threadId,
                        Role = "system",
                        Content = warningMsg
                    });
                    await _db.SaveChangesAsync(ct);

                    // Build safe response: include last assistant content if available
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
                            Console.WriteLine($"\n[Agent Engine] AI requested {toolCount} tool(s) in a single breath! (iteration {iteration}/{MAX_AGENT_ITERATIONS})");

                            // Step 1: Append the assistant message with tool_calls to history
                            // (OpenAI expects this before the tool role messages)
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

                            // Step 5: Enforce MAX_STATE_CHANGING_PER_TURN — keep first, reject rest
                            ToolCallInfo? allowedWrite = writeCalls.FirstOrDefault();
                            var rejectedWrites = writeCalls.Skip(MAX_STATE_CHANGING_PER_TURN).ToList();

                            // Collect all results here; merged into history/uiActions after all tasks complete
                            var allResults = new List<ToolExecResult>();

                            // Log rejected overflow calls (still need tool role messages for OpenAI)
                            foreach (var oc in overflowCalls)
                            {
                                Console.WriteLine($"[Agent Engine] OVERFLOW: tool '{oc.ToolName}' rejected (>{MAX_TOOLS_PER_TURN} per turn).");
                                allResults.Add(MakeWarningResult(oc,
                                    $"SYSTEM WARNING: max {MAX_TOOLS_PER_TURN} tool calls per turn exceeded; this call was ignored.", threadId));
                            }

                            // Log rejected duplicate state-changing calls
                            foreach (var rw in rejectedWrites)
                            {
                                Console.WriteLine($"[Agent Engine] POLICY: duplicate state-changing tool '{rw.ToolName}' rejected.");
                                allResults.Add(MakeWarningResult(rw,
                                    "SYSTEM WARNING: multiple state-changing tools requested in one turn; only the first execute_trade is allowed.", threadId));
                            }

                            // Step 6: Execute read-only tools in PARALLEL with bounded concurrency.
                            //   - Each task gets its own DI scope so it never shares the controller's
                            //     scoped DbContext across threads (EF Core is NOT thread-safe).
                            //   - Singletons (IMemoryCache, SemaphoreSlim python gate) resolve to
                            //     the same instance inside the scope — safe for concurrent access.
                            //   - history and uiActions are NEVER mutated inside tasks;
                            //     each task returns a ToolExecResult that is merged afterwards.
                            if (readOnlyCalls.Count > 0)
                            {
                                // Local semaphore caps parallel tool tasks this turn (separate from python gate)
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
                                }).ToList(); // .ToList() eagerly starts all tasks

                                var readResults = await Task.WhenAll(readTasks);
                                allResults.AddRange(readResults);
                            }

                            // Step 7: Execute the single allowed state-changing tool SERIALLY after reads.
                            //   execute_trade is never run concurrently with another write in the same turn.
                            //   It uses its own DI scope (fresh DbContext + TradingService).
                            if (allowedWrite != null)
                            {
                                var writeResult = await ExecuteToolInIsolatedScopeAsync(allowedWrite, threadId, finnhubKey, ct);
                                allResults.Add(writeResult);
                            }

                            // Step 8: Sort by original tool_calls index and merge into shared state.
                            //   OpenAI expects tool role messages in the same order as tool_calls.
                            //   This merge happens on the main thread — no concurrency concerns.
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

                            // Step 9: Persist all ToolRuns in a single batch using the controller-scoped
                            //   DbContext. This avoids SQLite writer contention from parallel saves and
                            //   guarantees one atomic SaveChanges for all observability rows this turn.
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
                                Console.WriteLine($"[Agent Engine] Failed to batch-log ToolRuns: {logEx.Message}");
                            }
                        }
                        else
                        {
                            finalAiResponse = message.GetProperty("content").GetString() ?? "";
                            history.Add(new { role = "assistant", content = finalAiResponse });

                            // Save AI response to DB
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
        //    WHY a fresh scope? EF Core's DbContext is NOT thread-safe.
        //    Parallel read-only tasks would corrupt the controller's shared
        //    DbContext if they resolved scoped services from it. Each scope
        //    provides an independent DbContext + TradingService instance.
        //    Singletons (IMemoryCache, SemaphoreSlim) resolve to the same
        //    instance — safe by design for concurrent access.
        //
        //    The method returns a ToolExecResult instead of mutating any
        //    shared collection (history, uiActions). The caller merges
        //    results on the main thread after all tasks complete.
        // ================================================================
        private async Task<ToolExecResult> ExecuteToolInIsolatedScopeAsync(
            ToolCallInfo call, string threadId, string finnhubKey, CancellationToken ct)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[Agent Engine] Executing Tool: {call.ToolName} (idx={call.Index})...");
            Console.ResetColor();

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
                            string symbol = argDoc.RootElement.GetProperty("symbol").GetString() ?? "";
                            string upperSymbol = symbol.ToUpperInvariant();

                            // Check cache first (10s TTL). IMemoryCache is singleton — thread-safe.
                            string cacheKey = $"quote:{upperSymbol}";
                            string? cachedResult = _cache.Get<string>(cacheKey);

                            if (cachedResult != null)
                            {
                                toolResult = cachedResult;
                                Console.WriteLine($"[Agent Engine] Cache hit for {cacheKey}");
                            }
                            else
                            {
                                var data = await FetchMarketData(symbol, finnhubKey);
                                toolResult = $"Price: {data.price}\nNews: {data.news}";

                                // Only cache successful results (price starts with "$")
                                if (data.price.StartsWith("$"))
                                {
                                    _cache.Set(cacheKey, toolResult, TimeSpan.FromSeconds(10));
                                }
                            }
                            break;
                        }

                    case "execute_trade":
                        {
                            // Resolve scoped TradingService from the isolated scope.
                            // This gives us a fresh DbContext that no other thread touches.
                            var tradingService = scope.ServiceProvider.GetRequiredService<TradingService>();

                            using var argDoc = JsonDocument.Parse(call.ArgumentsJson);
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

                            var result = await tradingService.ExecuteTradeAsync(tradeReq);

                            if (result.Success)
                            {
                                toolResult = $"SUCCESS: {action.ToUpper()} {shares} shares of {symbol} at ${price}.";
                            }
                            else
                            {
                                toolResult = $"ERROR: {result.ErrorMessage}";
                            }
                            break;
                        }

                    case "open_chart":
                        {
                            using var argDoc = JsonDocument.Parse(call.ArgumentsJson);
                            string symbol = (argDoc.RootElement.GetProperty("symbol").GetString() ?? "AMD").ToUpperInvariant();
                            string tf = argDoc.RootElement.TryGetProperty("tf", out var tfProp)
                                ? tfProp.GetString() ?? "1d" : "1d";
                            string range = argDoc.RootElement.TryGetProperty("range", out var rangeProp)
                                ? rangeProp.GetString() ?? "180d" : "180d";

                            string chartUrl = $"/chart/index.html?symbol={symbol}&tf={tf}&range={range}";

                            // UI action collected LOCALLY — merged into the shared list on main thread only.
                            // Never write to shared uiActions from a parallel task.
                            localUiActions.Add(new
                            {
                                type = "openChart",
                                symbol = symbol,
                                tf = tf,
                                range = range,
                                url = chartUrl
                            });

                            toolResult = $"Chart opened for {symbol} tf={tf} range={range}";
                            Console.WriteLine($"[Agent Engine] UI Action: openChart -> {chartUrl}");
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

            Console.WriteLine($"[Agent Engine] Tool '{call.ToolName}' completed in {sw.ElapsedMilliseconds}ms (success={isSuccess})");

            return new ToolExecResult(call.Index, call.ToolCallId, call.ToolName,
                toolResult, localUiActions, runPayload);
        }


        // ====================================================================
        // DATA SCRAPERS — Use only singletons (cache, python gate, config).
        //   Safe to call from parallel tasks because they never touch DbContext.
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

                    using var cts = new CancellationTokenSource(15_000);
                    try
                    {
                        await process.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
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

        /// <summary>
        /// Runs fetch_news.py gated by SemaphoreSlim to limit concurrent python processes.
        /// Hard timeout of 20 seconds; kills process on timeout.
        /// The python gate is a singleton — concurrent calls from parallel tool tasks
        /// safely contend on the same semaphore (global cap of 3 python processes).
        /// </summary>
        private async Task<string> RunPythonHunter(string symbol)
        {
            await _pythonGate.WaitAsync();
            try
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

                        // Drain stdout and stderr concurrently to prevent deadlocks
                        var stdoutTask = process.StandardOutput.ReadToEndAsync();
                        var stderrTask = process.StandardError.ReadToEndAsync();

                        using var cts = new CancellationTokenSource(PythonTimeoutMs);
                        try
                        {
                            await process.WaitForExitAsync(cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            try { process.Kill(entireProcessTree: true); } catch { }
                            return "SYSTEM ERROR: Python process timed out (20s).";
                        }

                        string output = await stdoutTask;
                        await stderrTask; // drain stderr

                        return string.IsNullOrWhiteSpace(output) ? "No data returned." : output;
                    }
                }
                catch (Exception ex) { return $"SYSTEM ERROR: {ex.Message}"; }
            }
            finally
            {
                _pythonGate.Release();
            }
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
