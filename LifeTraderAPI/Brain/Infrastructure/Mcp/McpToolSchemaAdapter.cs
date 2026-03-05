// CONTRACT / INVARIANTS
// - Converts MCP tool metadata into OpenAI function-calling JSON schemas.
// - Uses reflection to discover [McpServerToolType] classes and [McpServerTool] methods.
// - Scans the executing assembly (same assembly as McpMarketTools).
// - Caches results via IMemoryCache with 24h TTL (metadata is static at runtime).
// - Thread-safe: cached reads, no mutable state.
// - Skips MCP-injected parameters (CancellationToken, IServiceProvider, etc.).
// - Maps CLR types to JSON Schema types (string→string, int→integer, etc.).
// - Parameters with default values are optional; others are required.

using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol.Server;

namespace LifeTrader_AI.Infrastructure.Mcp
{
    /// <summary>
    /// Bridges MCP tool metadata to OpenAI function-calling schema format.
    /// Singleton — scans assembly once and caches results.
    /// </summary>
    public sealed class McpToolSchemaAdapter
    {
        private const string SchemasCacheKey = "mcp_openai_tool_schemas";
        private const string NamesCacheKey = "mcp_openai_tool_names";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

        private readonly IMemoryCache _cache;
        private readonly ILogger<McpToolSchemaAdapter> _logger;

        public McpToolSchemaAdapter(IMemoryCache cache, ILogger<McpToolSchemaAdapter> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// Returns the set of MCP tool names (for routing decisions in the controller).
        /// Cached — O(1) after first call.
        /// </summary>
        public IReadOnlySet<string> GetMcpToolNames()
        {
            return _cache.GetOrCreate(NamesCacheKey, entry =>
            {
                entry.SetAbsoluteExpiration(CacheTtl);

                var schemas = GetOpenAiToolSchemas();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var schema in schemas)
                {
                    var name = schema["function"]?["name"]?.GetValue<string>();
                    if (name != null) names.Add(name);
                }

                _logger.LogInformation("[McpSchema] Discovered {Count} MCP tool name(s): {Names}",
                    names.Count, string.Join(", ", names));
                return (IReadOnlySet<string>)names;
            })!;
        }

        /// <summary>
        /// Returns OpenAI function-calling tool schemas generated from [McpServerTool] methods.
        /// Format: [{ type: "function", function: { name, description, parameters: { type: "object", properties, required } } }]
        /// Cached — reflection runs only once.
        /// </summary>
        public IReadOnlyList<JsonNode> GetOpenAiToolSchemas()
        {
            return _cache.GetOrCreate(SchemasCacheKey, entry =>
            {
                entry.SetAbsoluteExpiration(CacheTtl);
                return BuildSchemasFromAssembly();
            })!;
        }

        // ================================================================
        // REFLECTION: Assembly scan → OpenAI schemas
        // ================================================================

        private List<JsonNode> BuildSchemasFromAssembly()
        {
            var schemas = new List<JsonNode>();
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var type in assembly.GetTypes())
            {
                if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null)
                    continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                    if (toolAttr == null) continue;

                    string toolName = toolAttr.Name ?? method.Name;
                    string? description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

                    var properties = new JsonObject();
                    var required = new JsonArray();

                    foreach (var param in method.GetParameters())
                    {
                        // Skip MCP-framework-injected parameters (not part of tool schema)
                        if (IsFrameworkParameter(param)) continue;

                        var paramObj = new JsonObject
                        {
                            ["type"] = MapClrTypeToJsonSchema(param.ParameterType)
                        };

                        var paramDesc = param.GetCustomAttribute<DescriptionAttribute>();
                        if (paramDesc != null)
                            paramObj["description"] = paramDesc.Description;

                        properties[param.Name!] = paramObj;

                        // Parameters without default values are required
                        if (!param.HasDefaultValue)
                            required.Add(JsonValue.Create(param.Name!));
                    }

                    var functionNode = new JsonObject
                    {
                        ["name"] = toolName
                    };
                    if (description != null)
                        functionNode["description"] = description;

                    functionNode["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required
                    };

                    var toolSchema = new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = functionNode
                    };

                    schemas.Add(toolSchema);

                    _logger.LogDebug("[McpSchema] Registered MCP tool schema: {ToolName} ({ParamCount} params)",
                        toolName, properties.Count);
                }
            }

            _logger.LogInformation("[McpSchema] Built {Count} OpenAI tool schema(s) from MCP assembly scan.", schemas.Count);
            return schemas;
        }

        /// <summary>
        /// Returns true for parameters injected by the MCP framework (not exposed in schema).
        /// </summary>
        private static bool IsFrameworkParameter(ParameterInfo param)
        {
            var type = param.ParameterType;

            return type == typeof(CancellationToken)
                || type == typeof(IServiceProvider)
                || (type.FullName?.StartsWith("ModelContextProtocol.") ?? false);
        }

        /// <summary>
        /// Maps a CLR type to its JSON Schema type string.
        /// </summary>
        private static string MapClrTypeToJsonSchema(Type type)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return "integer";
            if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "number";
            if (type == typeof(bool)) return "boolean";
            return "string"; // safe fallback
        }
    }
}
