using Microsoft.EntityFrameworkCore;
using LifeTrader_AI.Data;
using LifeTrader_AI.Models;

namespace LifeTrader_AI.Services
{
    // Alias inside namespace block: C# checks aliases here BEFORE
    // checking the parent namespace, so this overrides LifeTrader_AI.Position (Portfolio.cs).
    using Position = LifeTrader_AI.Models.Position;
    // ====================================================================
    // REQUEST / RESULT DTOs
    // ====================================================================

    /// <summary>
    /// Incoming trade request from the AI agent or controller.
    /// </summary>
    [System.Serializable]
    public class TradeRequest
    {
        public required string ClientRequestId { get; set; }
        public required string Symbol { get; set; }
        public required string Side { get; set; }  // "BUY" or "SELL"
        public required decimal Quantity { get; set; }
        public required decimal ExecutedPrice { get; set; }
        public decimal? Fees { get; set; }
        public string? Currency { get; set; }
        public string? Notes { get; set; }
        public string? RawJson { get; set; }
    }

    /// <summary>
    /// Result of a trade execution attempt.
    /// </summary>
    [System.Serializable]
    public class TradeResult
    {
        public bool Success { get; set; }
        public bool IsDuplicate { get; set; }
        public bool IsConflict { get; set; }
        public Trade? Trade { get; set; }
        public string? ErrorMessage { get; set; }

        public static TradeResult Ok(Trade trade) =>
            new() { Success = true, Trade = trade };

        public static TradeResult Duplicate(Trade trade) =>
            new() { Success = true, IsDuplicate = true, Trade = trade };

        public static TradeResult Conflict(string message) =>
            new() { Success = false, IsConflict = true, ErrorMessage = message };

        public static TradeResult Failure(string message) =>
            new() { Success = false, ErrorMessage = message };
    }

    // ====================================================================
    // TRADING SERVICE — Transactional execution with idempotency + retry
    // ====================================================================

    /// <summary>
    /// Handles trade execution with idempotency and optimistic concurrency.
    /// Registered as Scoped in Program.cs (shares the DbContext lifetime).
    ///
    /// Flow:
    /// 1. Fast idempotency check (outside transaction).
    /// 2. Begin transaction.
    /// 3. Re-check idempotency (race condition guard).
    /// 4. Insert Trade row.
    /// 5. Load or create Position.
    /// 6. Update Position quantity/price + auto-increment RowVersion.
    /// 7. SaveChanges (WHERE RowVersion = @old fires here).
    /// 8. If DbUpdateConcurrencyException → detach all, retry (up to 3x).
    /// 9. Commit.
    /// </summary>
    public class TradingService
    {
        private readonly AppDbContext _db;
        private const int MaxRetries = 3;

        public TradingService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<TradeResult> ExecuteTradeAsync(TradeRequest request)
        {
            // --- Step 1: Fast idempotency check (avoids transaction overhead for dupes) ---
            var existingTrade = await _db.Trades
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.ClientRequestId == request.ClientRequestId);

            if (existingTrade != null)
            {
                Console.WriteLine($"[Backend] Duplicate trade detected: {request.ClientRequestId}");
                return TradeResult.Duplicate(existingTrade);
            }

            // --- Step 2-9: Transactional execution with concurrency retry ---
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                // Detach all tracked entities on retry to get a clean slate
                if (attempt > 1)
                {
                    foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    {
                        entry.State = EntityState.Detached;
                    }
                    Console.WriteLine($"[Backend] Retrying trade (attempt {attempt}/{MaxRetries}): {request.ClientRequestId}");
                }

                using var transaction = await _db.Database.BeginTransactionAsync();

                try
                {
                    // Re-check idempotency inside transaction (race condition guard)
                    var duplicateCheck = await _db.Trades
                        .FirstOrDefaultAsync(t => t.ClientRequestId == request.ClientRequestId);

                    if (duplicateCheck != null)
                    {
                        await transaction.RollbackAsync();
                        Console.WriteLine($"[Backend] Duplicate caught inside transaction: {request.ClientRequestId}");
                        return TradeResult.Duplicate(duplicateCheck);
                    }

                    // Build the trade record
                    string normalizedSymbol = request.Symbol.ToUpperInvariant();
                    string normalizedCurrency = request.Currency ?? "USD";

                    var trade = new Trade
                    {
                        ClientRequestId = request.ClientRequestId,
                        Symbol = normalizedSymbol,
                        Side = request.Side.ToUpperInvariant(),
                        Quantity = request.Quantity,
                        ExecutedPrice = request.ExecutedPrice,
                        Fees = request.Fees,
                        Currency = normalizedCurrency,
                        ExecutedAtUtc = DateTime.UtcNow,
                        Status = "FILLED",
                        Notes = request.Notes,
                        RawJson = request.RawJson
                    };

                    // Load or create position for this symbol+currency
                    var position = await _db.Positions
                        .FirstOrDefaultAsync(p =>
                            p.Symbol == normalizedSymbol &&
                            p.Currency == normalizedCurrency);

                    if (position == null)
                    {
                        position = new Position
                        {
                            Symbol = normalizedSymbol,
                            Quantity = 0,
                            AvgEntryPrice = 0,
                            Currency = normalizedCurrency,
                            IsOpen = true,
                            CreatedAtUtc = DateTime.UtcNow,
                            UpdatedAtUtc = DateTime.UtcNow,
                            RowVersion = 0
                        };
                        _db.Positions.Add(position);
                        // Flush to get the auto-generated Id for the FK
                        await _db.SaveChangesAsync();
                    }

                    // Update position based on trade side
                    if (trade.Side == "BUY")
                    {
                        decimal totalCost = (position.Quantity * position.AvgEntryPrice)
                                          + (trade.Quantity * trade.ExecutedPrice);
                        position.Quantity += trade.Quantity;
                        position.AvgEntryPrice = position.Quantity > 0
                            ? totalCost / position.Quantity
                            : 0;
                        position.IsOpen = true;
                    }
                    else if (trade.Side == "SELL")
                    {
                        if (trade.Quantity > position.Quantity)
                        {
                            await transaction.RollbackAsync();
                            return TradeResult.Failure(
                                $"Insufficient quantity. Have {position.Quantity}, tried to sell {trade.Quantity}.");
                        }

                        position.Quantity -= trade.Quantity;

                        if (position.Quantity == 0)
                        {
                            position.IsOpen = false;
                            position.AvgEntryPrice = 0;
                        }
                        // AvgEntryPrice stays the same for partial sells
                    }

                    // Link trade to position and add it
                    trade.PositionId = position.Id;
                    _db.Trades.Add(trade);

                    // SaveChangesAsync auto-increments RowVersion via AppDbContext override.
                    // EF Core generates: UPDATE ... WHERE Id = @id AND RowVersion = @old
                    await _db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    Console.WriteLine($"[Backend] Trade executed: {trade.Side} {trade.Quantity} {trade.Symbol} @ {trade.ExecutedPrice}");
                    return TradeResult.Ok(trade);
                }
                catch (DbUpdateConcurrencyException)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"[Backend] Concurrency conflict on attempt {attempt}/{MaxRetries}");

                    if (attempt == MaxRetries)
                    {
                        return TradeResult.Conflict(
                            $"Position was modified by another request. Failed after {MaxRetries} retries.");
                    }

                    // Exponential backoff to reduce contention
                    await Task.Delay(50 * attempt);
                }
                catch (DbUpdateException ex) when (
                    ex.InnerException?.Message.Contains("UNIQUE constraint failed") == true &&
                    ex.InnerException.Message.Contains("ClientRequestId"))
                {
                    // Race condition: another thread inserted the same ClientRequestId
                    // between our check and our insert. The UNIQUE constraint caught it.
                    await transaction.RollbackAsync();

                    foreach (var entry in _db.ChangeTracker.Entries().ToList())
                        entry.State = EntityState.Detached;

                    var raceDuplicate = await _db.Trades
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.ClientRequestId == request.ClientRequestId);

                    if (raceDuplicate != null)
                    {
                        Console.WriteLine($"[Backend] Race-condition duplicate resolved: {request.ClientRequestId}");
                        return TradeResult.Duplicate(raceDuplicate);
                    }

                    return TradeResult.Failure("Unexpected UNIQUE constraint error on ClientRequestId.");
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"[Backend] Trade execution error: {ex.Message}");
                    return TradeResult.Failure($"Unexpected error: {ex.Message}");
                }
            }

            return TradeResult.Conflict("Max retries exceeded.");
        }
    }
}
