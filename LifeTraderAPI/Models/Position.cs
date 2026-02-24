using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LifeTrader_AI.Models
{
    /// <summary>
    /// Represents a single stock/asset holding in the portfolio.
    /// Maps to the "Positions" table in the SQLite database.
    /// Uses optimistic concurrency via RowVersion for thread-safe updates.
    /// </summary>
    [System.Serializable]
    public class Position
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [MaxLength(10)]
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Number of shares/units held. Decimal to support fractional shares.
        /// </summary>
        [Required]
        [Column(TypeName = "decimal(18,6)")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// Weighted average entry price for this position.
        /// </summary>
        [Required]
        [Column(TypeName = "decimal(18,4)")]
        public decimal AvgEntryPrice { get; set; }

        /// <summary>
        /// Trading currency (e.g., "USD", "EUR"). Null defaults to USD.
        /// </summary>
        [MaxLength(10)]
        public string? Currency { get; set; }

        /// <summary>
        /// Whether this position is currently open (true) or closed/flat (false).
        /// </summary>
        public bool IsOpen { get; set; } = true;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Optimistic concurrency token for SQLite.
        /// Auto-incremented by AppDbContext.SaveChangesAsync override.
        /// EF Core adds WHERE RowVersion = @old to UPDATE statements.
        /// </summary>
        public uint RowVersion { get; set; }

        // Navigation properties
        public ICollection<Trade> Trades { get; set; } = new List<Trade>();
        public ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();
    }
}
