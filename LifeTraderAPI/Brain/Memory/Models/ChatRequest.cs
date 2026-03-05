namespace LifeTrader_AI.Models
{
    /// <summary>
    /// Incoming request DTO for POST /api/ai/ask.
    /// </summary>
    public class ChatRequest
    {
        public string Message { get; set; } = "";
        public string? ThreadId { get; set; }
    }
}
