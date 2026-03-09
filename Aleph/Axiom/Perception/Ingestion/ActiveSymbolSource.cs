using Microsoft.EntityFrameworkCore;


namespace Aleph
{
    /// <summary>
    /// Resolves the set of ACTIVE symbols that should be ingested.
    /// Active = Portfolio holdings (open/quantity>0) UNION Watchlist (IsActive).
    /// Returns a distinct, sorted, uppercased list.
    /// </summary>
    public class ActiveSymbolSource
    {
        private readonly AppDbContext _db;

        public ActiveSymbolSource(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<string>> GetActiveSymbolsAsync(CancellationToken ct)
        {
            // Portfolio: positions that are open or still have quantity
            var portfolioSymbols = await _db.Positions
                .Where(p => p.IsOpen || p.Quantity > 0)
                .Select(p => p.Symbol)
                .ToListAsync(ct);

            // Watchlist: active watchlist items
            var watchlistSymbols = await _db.WatchlistItems
                .Where(w => w.IsActive)
                .Select(w => w.Symbol)
                .ToListAsync(ct);

            // Merge, deduplicate, normalize, sort
            return portfolioSymbols
                .Concat(watchlistSymbols)
                .Select(s => s.ToUpperInvariant())
                .Distinct()
                .OrderBy(s => s)
                .ToList();
        }
    }
}
