using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LifeTrader_AI.Models
{
    /// <summary>
    /// Represents a single message in the OpenAI conversation history.
    /// Maps to the "ChatMessages" table in the SQLite database.
    /// Replaces the line-based trade_log.txt parsing in MemoryHelper.
    /// </summary>
    [System.Serializable]
    public class ChatMessage
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// Groups messages into conversation threads.
        /// Allows multiple concurrent conversations to be tracked independently.
        /// </summary>
        [Required]
        [MaxLength(64)]
        public string ThreadId { get; set; } = string.Empty;

        /// <summary>
        /// The OpenAI role: "system", "user", "assistant", or "tool".
        /// Validated via a check constraint in AppDbContext.OnModelCreating.
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// The message content sent to or received from the OpenAI API.
        /// </summary>
        [Required]
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Optional JSON blob for tool_call_id, function_name, token usage, etc.
        /// Stored as raw JSON string for flexibility.
        /// </summary>
        public string? MetadataJson { get; set; }

        /// <summary>
        /// UTC timestamp of when this message was created.
        /// Indexed for efficient pruning queries.
        /// </summary>
        [Required]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        // Optional FK to Position (links a message to a specific trade/position context)
        public int? PositionId { get; set; }

        [ForeignKey(nameof(PositionId))]
        public Position? Position { get; set; }
    }
}
