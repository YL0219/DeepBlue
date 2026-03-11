// CONTRACT / INVARIANTS
// - MCP tools for the Plan Engine Skill Library.
// - Tool names are snake_case: get_available_skills, read_skill_playbook.
// - JSON output keys are snake_case.
// - Skill reads are routed through IAxiom.Skills.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Aleph
{
    [McpServerToolType]
    public class McpSkillTools
    {
        private readonly IAxiom _axiom;

        public McpSkillTools(IAxiom axiom)
        {
            _axiom = axiom;
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
            return _axiom.Skills.GetAvailableSkills(include_deprecated);
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
            return _axiom.Skills.ReadPlaybook(skill_name);
        }
    }
}
