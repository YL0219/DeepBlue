using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LifeTrader_AI.Models
{
    /// <summary>
    /// Observability record for each tool invocation in the AI agent loop.
    /// Logs tool name, arguments, result, execution time, and success status.
    /// Written immediately after each tool call to avoid losing data on crash.
    /// </summary>
    [System.Serializable]
    public class ToolRun
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [Required]
        [MaxLength(64)]
        public string ThreadId { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string ToolName { get; set; } = string.Empty;

        public string? ArgumentsJson { get; set; }

        public string? ResultJson { get; set; }

        public long ExecutionTimeMs { get; set; }

        public bool IsSuccess { get; set; }

        [Required]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
