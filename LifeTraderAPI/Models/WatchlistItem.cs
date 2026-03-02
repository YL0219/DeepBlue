using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LifeTrader_AI.Models
{
    /// <summary>
    /// A symbol the user is watching but may not hold a position in.
    /// Used by the ingestion orchestrator to determine which symbols to ingest.
    /// Active symbols = Portfolio holdings UNION Watchlist (IsActive=true).
    /// </summary>
    [System.Serializable]
    public class WatchlistItem
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [Required]
        [MaxLength(10)]
        public string Symbol { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        [Required]
        public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
