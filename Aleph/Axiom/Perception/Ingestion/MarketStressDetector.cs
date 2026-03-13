using System.Text.Json;

namespace Aleph;

/// <summary>
/// Reflexive market stress detector — autonomic (unconscious) pathway.
/// Inspects freshly ingested OHLCV data for v1 sentinel symbols and
/// injects stress signals into Homeostasis via IStressInjector.
///
/// V1 Reflex Rules (deterministic):
///   - SPY daily move <= -2.0%  → Warning
///   - QQQ daily move <= -2.0%  → Warning
///   - VIX level >= 25 OR daily jump >= +15%  → Warning/Critical
///   - TLT acts as contextual amplifier (flight-to-safety confirmation)
///   - Multiple triggers compound severity.
/// </summary>
public sealed class MarketStressDetector : IMarketStressDetector
{
    private static readonly string[] SentinelSymbols = { "SPY", "QQQ", "VIX", "TLT" };

    // Thresholds
    private const double EquityDropThresholdPct = -2.0;
    private const double VixLevelThreshold = 25.0;
    private const double VixJumpThresholdPct = 15.0;
    private const double TltRallyThresholdPct = 1.5; // flight-to-safety amplifier

    private readonly IAxiom _axiom;
    private readonly IStressInjector _stressInjector;
    private readonly ILogger<MarketStressDetector> _logger;

    public MarketStressDetector(
        IAxiom axiom,
        IStressInjector stressInjector,
        ILogger<MarketStressDetector> logger)
    {
        _axiom = axiom;
        _stressInjector = stressInjector;
        _logger = logger;
    }

    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[StressDetector] Evaluating sentinel symbols...");

        var triggers = new List<StressTrigger>();
        var metrics = new Dictionary<string, double>();

        foreach (var symbol in SentinelSymbols)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var candleResult = await _axiom.Market.GetCandlesAsync(
                    new MarketCandlesQuery(symbol, "1d", "5d", 5, null), ct);

                if (!candleResult.Success || candleResult.Candles is null)
                {
                    _logger.LogDebug("[StressDetector] No candle data for {Symbol}. Skipping.", symbol);
                    continue;
                }

                var dailyChange = ExtractLatestDailyChangePct(candleResult.Candles);
                var latestClose = ExtractLatestClose(candleResult.Candles);

                if (dailyChange is null)
                {
                    _logger.LogDebug("[StressDetector] Could not compute daily change for {Symbol}.", symbol);
                    continue;
                }

                metrics[$"{symbol.ToLowerInvariant()}_daily_pct"] = dailyChange.Value;
                if (latestClose.HasValue)
                    metrics[$"{symbol.ToLowerInvariant()}_close"] = latestClose.Value;

                switch (symbol)
                {
                    case "SPY" when dailyChange <= EquityDropThresholdPct:
                        triggers.Add(new StressTrigger(symbol, PulseSeverity.Warning,
                            $"SPY dropped {dailyChange:F2}% (threshold: {EquityDropThresholdPct}%)"));
                        break;

                    case "QQQ" when dailyChange <= EquityDropThresholdPct:
                        triggers.Add(new StressTrigger(symbol, PulseSeverity.Warning,
                            $"QQQ dropped {dailyChange:F2}% (threshold: {EquityDropThresholdPct}%)"));
                        break;

                    case "VIX":
                        var vixTriggered = false;
                        if (latestClose.HasValue && latestClose.Value >= VixLevelThreshold)
                        {
                            triggers.Add(new StressTrigger(symbol, PulseSeverity.Warning,
                                $"VIX at {latestClose:F2} (threshold: {VixLevelThreshold})"));
                            vixTriggered = true;
                        }
                        if (dailyChange >= VixJumpThresholdPct)
                        {
                            var severity = vixTriggered ? PulseSeverity.Critical : PulseSeverity.Warning;
                            triggers.Add(new StressTrigger(symbol, severity,
                                $"VIX jumped +{dailyChange:F2}% (threshold: +{VixJumpThresholdPct}%)"));
                        }
                        break;

                    case "TLT" when dailyChange >= TltRallyThresholdPct:
                        // Flight-to-safety: TLT rallying amplifies existing triggers
                        triggers.Add(new StressTrigger(symbol, PulseSeverity.Elevated,
                            $"TLT rallied +{dailyChange:F2}% — flight-to-safety signal"));
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[StressDetector] Error evaluating {Symbol}.", symbol);
            }
        }

        if (triggers.Count == 0)
        {
            _logger.LogDebug("[StressDetector] No reflex stress triggers fired.");
            return;
        }

        // Compound severity: multiple triggers escalate
        var maxSeverity = triggers.Max(t => t.Severity);
        if (triggers.Count >= 3 && maxSeverity < PulseSeverity.Critical)
            maxSeverity = PulseSeverity.Critical;
        else if (triggers.Count >= 2 && maxSeverity < PulseSeverity.Warning)
            maxSeverity = PulseSeverity.Warning;

        var triggerMessages = triggers.Select(t => t.Message);
        var compoundMessage = string.Join(" | ", triggerMessages);

        var tags = triggers.Select(t => t.Symbol.ToLowerInvariant()).Distinct().ToList();
        tags.Add("reflex");
        tags.Add("market_stress");

        var envelope = PulseEnvelope.ExternalStress(
            source: "axiom_reflex/market_stress_detector",
            severity: maxSeverity,
            message: compoundMessage,
            metrics: metrics,
            tags: tags);

        var receipt = _stressInjector.InjectStress(envelope);

        _logger.LogInformation(
            "[StressDetector] Reflex stress injected. Triggers={Count}, Severity={Severity}, Accepted={Accepted}: {Message}",
            triggers.Count, maxSeverity, receipt.Accepted, compoundMessage);
    }

    // ─── OHLCV parsing helpers ──────────────────────────────────────

    /// <summary>
    /// Extract percentage change between the last two daily closes.
    /// Candles JSON is expected as an array of objects with a "close" field.
    /// </summary>
    private static double? ExtractLatestDailyChangePct(MarketCandlesDto candles)
    {
        try
        {
            var arr = candles.Candles;
            if (arr.ValueKind != JsonValueKind.Array) return null;

            var count = arr.GetArrayLength();
            if (count < 2) return null;

            var prev = arr[count - 2];
            var last = arr[count - 1];

            if (!prev.TryGetProperty("close", out var prevClose) ||
                !last.TryGetProperty("close", out var lastClose))
                return null;

            var prevVal = prevClose.GetDouble();
            var lastVal = lastClose.GetDouble();

            if (prevVal == 0) return null;

            return ((lastVal - prevVal) / prevVal) * 100.0;
        }
        catch
        {
            return null;
        }
    }

    private static double? ExtractLatestClose(MarketCandlesDto candles)
    {
        try
        {
            var arr = candles.Candles;
            if (arr.ValueKind != JsonValueKind.Array) return null;

            var count = arr.GetArrayLength();
            if (count < 1) return null;

            var last = arr[count - 1];
            if (!last.TryGetProperty("close", out var closeVal))
                return null;

            return closeVal.GetDouble();
        }
        catch
        {
            return null;
        }
    }

    private sealed record StressTrigger(string Symbol, PulseSeverity Severity, string Message);
}
