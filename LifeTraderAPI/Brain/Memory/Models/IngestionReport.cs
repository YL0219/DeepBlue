using System.Text.Json.Serialization;

namespace LifeTrader_AI.Models
{
    /// <summary>
    /// DTO deserialized from the JSON that market_ingest_worker.py prints to stdout.
    /// NOT an EF entity — never stored in SQLite directly.
    /// C# reads this report and upserts MarketDataAsset rows from its contents.
    /// </summary>
    public sealed class IngestionReport
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("jobId")]
        public string JobId { get; set; } = "";

        [JsonPropertyName("startedAtUtc")]
        public string StartedAtUtc { get; set; } = "";

        [JsonPropertyName("finishedAtUtc")]
        public string FinishedAtUtc { get; set; } = "";

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("request")]
        public IngestionRequest Request { get; set; } = new();

        [JsonPropertyName("results")]
        public List<IngestionResult> Results { get; set; } = new();

        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new();
    }

    public sealed class IngestionRequest
    {
        [JsonPropertyName("symbols")]
        public List<string> Symbols { get; set; } = new();

        [JsonPropertyName("interval")]
        public string Interval { get; set; } = "1d";

        [JsonPropertyName("startDate")]
        public string StartDate { get; set; } = "";

        [JsonPropertyName("endDate")]
        public string EndDate { get; set; } = "";

        [JsonPropertyName("outRoot")]
        public string OutRoot { get; set; } = "";

        [JsonPropertyName("providerUsed")]
        public string ProviderUsed { get; set; } = "yfinance";
    }

    public sealed class IngestionResult
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        [JsonPropertyName("interval")]
        public string Interval { get; set; } = "1d";

        [JsonPropertyName("providerUsed")]
        public string ProviderUsed { get; set; } = "yfinance";

        [JsonPropertyName("parquetPath")]
        public string ParquetPath { get; set; } = "";

        [JsonPropertyName("rowsWritten")]
        public int RowsWritten { get; set; }

        [JsonPropertyName("dataStartUtc")]
        public string? DataStartUtc { get; set; }

        [JsonPropertyName("dataEndUtc")]
        public string? DataEndUtc { get; set; }

        [JsonPropertyName("isSuccess")]
        public bool IsSuccess { get; set; }

        [JsonPropertyName("error")]
        public IngestionError? Error { get; set; }
    }

    public sealed class IngestionError
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "";
    }
}
