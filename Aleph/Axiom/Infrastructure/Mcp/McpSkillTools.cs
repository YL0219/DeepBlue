// CONTRACT / INVARIANTS
// - MCP tools for the Plan Engine Skill Library.
// - Tool names are snake_case: get_available_skills, read_skill_playbook.
// - JSON output keys are snake_case.
// - skill_name validation: regex ^[a-z][a-z0-9_]{0,63}$, reject path characters.
// - Lookup by BySkillName dictionary only — never constructs file paths.
// - Thread-safe: reads immutable snapshot from ISkillRegistry.

using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;


namespace Aleph
{
    [McpServerToolType]
    public class McpSkillTools
    {
        private static readonly Regex ValidSkillName =
            new(@"^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false
        };

        private readonly ISkillRegistry _registry;
        private readonly ILogger<McpSkillTools> _logger;

        public McpSkillTools(
            ISkillRegistry registry,
            ILogger<McpSkillTools> logger)
        {
            _registry = registry;
            _logger = logger;
        }

        [McpServerTool(Name = "get_available_skills", ReadOnly = true)]
        [Description(
            "List all available skill playbooks in the Plan Engine library. " +
            "Returns skill metadata including name, description, tags, and required tools. " +
            "Deprecated skills are excluded by default unless include_deprecated is true.")]
        public string GetAvailableSkills(
            [Description("Include deprecated skills in results. Defaults to false.")]
            bool include_deprecated = false)
        {
            var snapshot = _registry.Snapshot;

            var skills = snapshot.Playbooks
                .Where(p => include_deprecated || !p.Metadata.Deprecated)
                .Select(p => new Dictionary<string, object?>
                {
                    ["skill_name"] = p.Metadata.SkillName,
                    ["display_name"] = p.Metadata.DisplayName,
                    ["version"] = p.Metadata.Version,
                    ["description"] = p.Metadata.Description,
                    ["tags"] = p.Metadata.Tags,
                    ["required_tools"] = p.Metadata.RequiredTools,
                    ["deprecated"] = p.Metadata.Deprecated
                })
                .ToList();

            var response = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["schema_version"] = 1,
                ["skills"] = skills,
                ["total_count"] = skills.Count
            };

            return JsonSerializer.Serialize(response, JsonOpts);
        }

        [McpServerTool(Name = "read_skill_playbook", ReadOnly = true)]
        [Description(
            "Read the full playbook for a specific skill by name. " +
            "Returns skill metadata, markdown instructions, content hash, and file size. " +
            "Use get_available_skills first to discover available skill names.")]
        public string ReadSkillPlaybook(
            [Description("The skill name to read (snake_case, e.g. 'macro_news_analysis').")]
            string skill_name)
        {
            // Validate format
            if (string.IsNullOrWhiteSpace(skill_name) || !ValidSkillName.IsMatch(skill_name))
            {
                _logger.LogWarning("[McpSkillTools] Invalid skill_name: '{SkillName}'", skill_name);
                return BuildErrorResponse("invalid_skill_name",
                    $"Invalid skill_name: '{skill_name}'. Must match pattern: ^[a-z][a-z0-9_]{{0,63}}$");
            }

            // Defense-in-depth: reject path characters (regex already blocks these, but be explicit)
            if (skill_name.Contains('/') || skill_name.Contains('\\') || skill_name.Contains(".."))
            {
                _logger.LogWarning("[McpSkillTools] Path characters in skill_name: '{SkillName}'", skill_name);
                return BuildErrorResponse("invalid_skill_name",
                    "Skill name must not contain path characters.");
            }

            // Dictionary lookup only — no file path construction
            var snapshot = _registry.Snapshot;
            if (!snapshot.BySkillName.TryGetValue(skill_name, out var playbook))
            {
                return BuildErrorResponse("skill_not_found",
                    $"Skill '{skill_name}' not found.");
            }

            var metadata = new Dictionary<string, object?>
            {
                ["skill_name"] = playbook.Metadata.SkillName,
                ["display_name"] = playbook.Metadata.DisplayName,
                ["version"] = playbook.Metadata.Version,
                ["description"] = playbook.Metadata.Description,
                ["tags"] = playbook.Metadata.Tags,
                ["required_tools"] = playbook.Metadata.RequiredTools,
                ["deprecated"] = playbook.Metadata.Deprecated,
                ["extras"] = playbook.Metadata.Extras.Count > 0
                    ? playbook.Metadata.Extras
                    : new Dictionary<string, object>()
            };

            var response = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["skill_name"] = playbook.Metadata.SkillName,
                ["metadata"] = metadata,
                ["markdown_body"] = playbook.MarkdownBody,
                ["content_hash"] = playbook.ContentHash,
                ["file_size_bytes"] = playbook.FileSizeBytes
            };

            return JsonSerializer.Serialize(response, JsonOpts);
        }

        private static string BuildErrorResponse(string error, string message)
        {
            var response = new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["error"] = error,
                ["message"] = message
            };
            return JsonSerializer.Serialize(response, JsonOpts);
        }
    }
}
