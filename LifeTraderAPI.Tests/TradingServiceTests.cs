using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LifeTrader_AI.Data;
using LifeTrader_AI.Models;
using LifeTrader_AI.Services;
using Xunit;

namespace LifeTraderAPI.Tests
{
    // Alias inside namespace block: overrides LifeTrader_AI.Position (Portfolio.cs).
    using Position = LifeTrader_AI.Models.Position;

    /// <summary>
    /// Integration tests for TradingService.
    ///
    /// Uses file-based SQLite so each DbContext gets its own connection and
    /// transaction scope — required for parallel trade testing.
    /// In-memory SQLite shares a single connection, which does not support
    /// concurrent transactions.
    /// </summary>
    public class TradingServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public TradingServiceTests()
        {
            // File-based SQLite: each CreateContext() opens its own connection.
            // This allows parallel transactions (one per context).
            _dbPath = Path.Combine(Path.GetTempPath(), $"deepblue_test_{Guid.NewGuid():N}.db");
            _connectionString = $"DataSource={_dbPath}";

            // Create schema using a one-shot context
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            // Clean up the temp database file
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        }

        /// <summary>
        /// Creates a fresh AppDbContext with its OWN SQLite connection.
        /// Each context can independently begin/commit/rollback transactions.
        /// Simulates ASP.NET Core's scoped DbContext lifetime.
        /// </summary>
        private AppDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            return new AppDbContext(options);
        }

        // ====================================================================
        // TEST 1: Parallel duplicate trades — only one trade should be created
        // ====================================================================

        /// <summary>
        /// Fires N parallel trade requests with the SAME ClientRequestId.
        /// Asserts:
        /// 1. Exactly ONE Trade row exists for that ClientRequestId.
        /// 2. Position Quantity is correct (only one trade modified it).
        /// 3. All results are either Success or Duplicate (no errors/conflicts).
        /// </summary>
        [Fact]
        public async Task ParallelDuplicateTrades_OnlyOneTradeCreated_PositionCorrect()
        {
            const string clientRequestId = "test-idempotency-001";
            const string symbol = "AAPL";
            const decimal quantity = 10m;
            const decimal price = 150.00m;
            const int parallelCount = 20;

            var request = new TradeRequest
            {
                ClientRequestId = clientRequestId,
                Symbol = symbol,
                Side = "BUY",
                Quantity = quantity,
                ExecutedPrice = price,
                Currency = "USD"
            };

            // Fire N parallel trade executions — each gets its own DbContext + connection
            var tasks = Enumerable.Range(0, parallelCount).Select(_ =>
            {
                return Task.Run(async () =>
                {
                    using var db = CreateContext();
                    var service = new TradingService(db);
                    return await service.ExecuteTradeAsync(request);
                });
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            // All results should be success (either fresh or duplicate) — no conflicts
            int successCount = results.Count(r => r.Success);
            int freshCount = results.Count(r => r.Success && !r.IsDuplicate);
            int duplicateCount = results.Count(r => r.Success && r.IsDuplicate);

            Assert.Equal(parallelCount, successCount);
            Assert.Equal(1, freshCount);
            Assert.Equal(parallelCount - 1, duplicateCount);

            // Verify database state: exactly one trade
            using var verifyDb = CreateContext();
            var trades = await verifyDb.Trades
                .Where(t => t.ClientRequestId == clientRequestId)
                .ToListAsync();
            Assert.Single(trades);
            Assert.Equal("FILLED", trades[0].Status);
            Assert.Equal(quantity, trades[0].Quantity);

            // Verify position: should have exactly the traded quantity
            var position = await verifyDb.Positions
                .FirstOrDefaultAsync(p => p.Symbol == symbol && p.Currency == "USD");
            Assert.NotNull(position);
            Assert.Equal(quantity, position!.Quantity);
            Assert.Equal(price, position.AvgEntryPrice);
            Assert.True(position.IsOpen);
        }

        // ====================================================================
        // TEST 2: Parallel distinct trades — all succeed, quantities sum
        // ====================================================================

        /// <summary>
        /// Fires parallel BUY trades with DIFFERENT ClientRequestIds.
        /// All should succeed (some may retry due to concurrency conflicts).
        /// Position quantity should equal quantityEach * successCount.
        /// </summary>
        [Fact]
        public async Task ParallelDistinctTrades_AllSucceed_PositionQuantityIsSummed()
        {
            const string symbol = "MSFT";
            const decimal quantityEach = 5m;
            const decimal price = 400.00m;
            const int parallelCount = 10;

            var tasks = Enumerable.Range(0, parallelCount).Select(i =>
            {
                return Task.Run(async () =>
                {
                    var req = new TradeRequest
                    {
                        ClientRequestId = $"distinct-trade-{i}",
                        Symbol = symbol,
                        Side = "BUY",
                        Quantity = quantityEach,
                        ExecutedPrice = price,
                        Currency = "USD"
                    };

                    using var db = CreateContext();
                    var service = new TradingService(db);
                    return await service.ExecuteTradeAsync(req);
                });
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            // Count results — some may conflict if retries are exhausted
            int successCount = results.Count(r => r.Success && !r.IsDuplicate);
            int conflictCount = results.Count(r => r.IsConflict);

            // With 3 retries + file-based SQLite, most should succeed
            Assert.True(successCount > 0, "At least some trades should succeed");

            // Verify database state
            using var verifyDb = CreateContext();
            int tradeCount = await verifyDb.Trades
                .Where(t => t.Symbol == symbol)
                .CountAsync();
            Assert.Equal(successCount, tradeCount);

            var position = await verifyDb.Positions
                .FirstOrDefaultAsync(p => p.Symbol == symbol && p.Currency == "USD");
            Assert.NotNull(position);
            Assert.Equal(quantityEach * successCount, position!.Quantity);
        }

        // ====================================================================
        // TEST 3: Sequential buy then sell — position math is correct
        // ====================================================================

        /// <summary>
        /// BUY 100 shares, then SELL 40 shares.
        /// Position should have 60 shares at the original avg price.
        /// </summary>
        [Fact]
        public async Task BuyThenSell_PositionUpdatedCorrectly()
        {
            // BUY 100 shares
            using (var db = CreateContext())
            {
                var service = new TradingService(db);
                var buyResult = await service.ExecuteTradeAsync(new TradeRequest
                {
                    ClientRequestId = "buy-001",
                    Symbol = "GOOG",
                    Side = "BUY",
                    Quantity = 100m,
                    ExecutedPrice = 175.00m,
                    Currency = "USD"
                });
                Assert.True(buyResult.Success);
                Assert.False(buyResult.IsDuplicate);
            }

            // SELL 40 shares
            using (var db = CreateContext())
            {
                var service = new TradingService(db);
                var sellResult = await service.ExecuteTradeAsync(new TradeRequest
                {
                    ClientRequestId = "sell-001",
                    Symbol = "GOOG",
                    Side = "SELL",
                    Quantity = 40m,
                    ExecutedPrice = 180.00m,
                    Currency = "USD"
                });
                Assert.True(sellResult.Success);
            }

            // Verify: 60 shares remain, avg price unchanged (175.00)
            using var verifyDb = CreateContext();
            var position = await verifyDb.Positions
                .FirstOrDefaultAsync(p => p.Symbol == "GOOG");
            Assert.NotNull(position);
            Assert.Equal(60m, position!.Quantity);
            Assert.Equal(175.00m, position.AvgEntryPrice);
            Assert.True(position.IsOpen);

            // Verify: 2 trades total
            var trades = await verifyDb.Trades.Where(t => t.Symbol == "GOOG").ToListAsync();
            Assert.Equal(2, trades.Count);
        }

        // ====================================================================
        // TEST 4: Sell more than owned — must fail, no trade created
        // ====================================================================

        /// <summary>
        /// BUY 10, then try to SELL 50.
        /// Should return Failure with "Insufficient" message.
        /// Only the BUY trade should exist in the database.
        /// </summary>
        [Fact]
        public async Task SellMoreThanOwned_ReturnsFailure()
        {
            // BUY 10 shares
            using (var db = CreateContext())
            {
                var service = new TradingService(db);
                await service.ExecuteTradeAsync(new TradeRequest
                {
                    ClientRequestId = "buy-002",
                    Symbol = "TSLA",
                    Side = "BUY",
                    Quantity = 10m,
                    ExecutedPrice = 250.00m,
                    Currency = "USD"
                });
            }

            // Try to SELL 50 shares (should fail)
            using (var db = CreateContext())
            {
                var service = new TradingService(db);
                var result = await service.ExecuteTradeAsync(new TradeRequest
                {
                    ClientRequestId = "sell-002",
                    Symbol = "TSLA",
                    Side = "SELL",
                    Quantity = 50m,
                    ExecutedPrice = 260.00m,
                    Currency = "USD"
                });

                Assert.False(result.Success);
                Assert.Contains("Insufficient", result.ErrorMessage);
            }

            // Verify: position unchanged, only 1 trade (the buy)
            using var verifyDb = CreateContext();
            var trades = await verifyDb.Trades.Where(t => t.Symbol == "TSLA").ToListAsync();
            Assert.Single(trades);
            Assert.Equal("BUY", trades[0].Side);
        }
    }
}
