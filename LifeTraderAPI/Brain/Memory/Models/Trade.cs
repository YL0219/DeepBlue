using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LifeTrader_AI.Models
{
    /// <summary>
    /// Represents a single executed trade.
    /// ClientRequestId provides idempotency — duplicate requests return the existing trade.
    /// Maps to the "Trades" table in the SQLite database.
    /// </summary>
    [System.Serializable]
    public class Trade
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// Idempotency key. Must be unique per trade.
        /// If a duplicate ClientRequestId arrives, the service returns the existing trade.
        /// </summary>
        [Required]
        [MaxLength(64)]
        public string ClientRequestId { get; set; } = string.Empty;

        [Required]
        [MaxLength(10)]
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Trade direction: "BUY" or "SELL".
        /// Validated via a check constraint in AppDbContext.OnModelCreating.
        /// </summary>
        [Required]
        [MaxLength(10)]
        public string Side { get; set; } = string.Empty;

        [Required]
        [Column(TypeName = "decimal(18,6)")]
        public decimal Quantity { get; set; }

        [Required]
        [Column(TypeName = "decimal(18,4)")]
        public decimal ExecutedPrice { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal? Fees { get; set; }

        [MaxLength(10)]
        public string? Currency { get; set; }

        [Required]
        public DateTime ExecutedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Trade status: "FILLED", "PENDING", or "REJECTED".
        /// Validated via a check constraint in AppDbContext.OnModelCreating.
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string Status { get; set; } = "PENDING";

        [MaxLength(500)]
        public string? Notes { get; set; }

        /// <summary>
        /// Raw JSON from the exchange/broker response, for audit trail.
        /// </summary>
        public string? RawJson { get; set; }

        // Foreign key to Position (nullable — trade may not yet be linked)
        public int? PositionId { get; set; }

        [ForeignKey(nameof(PositionId))]
        public Position? Position { get; set; }
    }
}
