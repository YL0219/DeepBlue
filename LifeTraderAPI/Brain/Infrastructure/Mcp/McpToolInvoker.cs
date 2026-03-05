// CONTRACT / INVARIANTS
// - Bridges OpenAI tool_call dispatch to MCP tool execution.
// - Switch/case MVP: routes by tool name to the correct MCP tool method.
// - Accepts (toolName, argumentsJson) and returns the tool's result string.
// - Parses OpenAI-format JSON arguments and maps to method parameters.
// - Returns JSON error object on failure (never throws to caller).
// - Thread-safe: no mutable state; tool class dependencies are singletons.
// - When new MCP tools are added, extend the switch/case and add to this file.

using System.Text.Json;

namespace LifeTrader_AI.Infrastructure.Mcp
{
    /// <summary>
    /// Routes OpenAI tool calls to MCP tool methods.
    /// Singleton — dependencies (McpMarketTools) are effectively stateless/singleton.
    /// </summary>
    public sealed class McpToolInvoker
    {
        private readonly McpMarketTools _marketTools;
        private readonly ILogger<McpToolInvoker> _logger;

        public McpToolInvoker(McpMarketTools marketTools, ILogger<McpToolInvoker> logger)
        {
            _marketTools = marketTools;
            _logger = logger;
        }

        /// <summary>
        /// Invoke an MCP tool by name with JSON arguments from OpenAI.
        /// Returns the tool result string (JSON on success, JSON error on failure).
        /// </summary>
        public async Task<string> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct = default)
        {
            _logger.LogDebug("[McpInvoker] Invoking MCP tool: {ToolName}", toolName);

            try
            {
                switch (toolName)
                {
                    case "query_local_market_data":
                        return await InvokeQueryLocalMarketData(argumentsJson);

                    default:
                        _logger.LogWarning("[McpInvoker] Unknown MCP tool: {ToolName}", toolName);
                        return BuildErrorJson($"Unknown MCP tool: '{toolName}'");
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[McpInvoker] Failed to parse arguments for tool '{ToolName}'", toolName);
                return BuildErrorJson($"Invalid arguments JSON for tool '{toolName}'.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[McpInvoker] Unhandled exception invoking tool '{ToolName}'", toolName);
                return BuildErrorJson($"Internal error invoking tool '{toolName}'.");
            }
        }

        // ================================================================
        // Tool dispatch methods — one per MCP tool
        // ================================================================

        private async Task<string> InvokeQueryLocalMarketData(string argumentsJson)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            string symbol = root.GetProperty("symbol").GetString() ?? "";

            int days = 7; // default
            if (root.TryGetProperty("days", out var daysProp) && daysProp.ValueKind == JsonValueKind.Number)
            {
                days = daysProp.GetInt32();
            }

            return await _marketTools.QueryLocalMarketData(symbol, days);
        }

        // ================================================================

        private static string BuildErrorJson(string message)
        {
            var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"{{\"ok\":false,\"error\":\"{escaped}\"}}";
        }
    }
}
