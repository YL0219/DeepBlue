using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LifeTrader_AI.Models
{
    /// <summary>
    /// Metadata row for a single symbol+interval of ingested OHLCV data.
    /// The heavy candle data lives in Parquet on disk; this table lets C# query
    /// what exists, when it was last ingested, and whether the last run succeeded.
    /// </summary>
    [System.Serializable]
    public class MarketDataAsset
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [Required]
        [MaxLength(10)]
        public string Symbol { get; set; } = string.Empty;

        /// <summary>Candle interval, e.g. "1d". MVP uses daily only.</summary>
        [Required]
        [MaxLength(10)]
        public string Interval { get; set; } = "1d";

        /// <summary>Relative path to the Parquet file on disk.</summary>
        [MaxLength(500)]
        public string? ParquetPath { get; set; }

        public DateTime? LastIngestedAtUtc { get; set; }

        /// <summary>Timestamp of the last candle in the Parquet file.</summary>
        public DateTime? LastDataEndUtc { get; set; }

        [MaxLength(50)]
        public string? ProviderUsed { get; set; }

        public int RowsWritten { get; set; }

        public int ConsecutiveFailures { get; set; }

        [MaxLength(1000)]
        public string? LastError { get; set; }

        [Required]
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
