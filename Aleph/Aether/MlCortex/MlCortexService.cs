using System.Text.Json;

namespace Aleph;

/// <summary>
/// The ML Cortex — predictive organ of the Aleph organism.
///
/// Subscribes to the AlephBus for MetabolicEvent blood cells, passes them
/// through the Python sandbox brain for prediction, and publishes refined
/// PredictionEvent blood cells back into the bloodstream.
///
/// Design principles:
///   - C# is thin plumbing only — all ML logic lives in Python
///   - Real-time inference path is always available (even cold start)
///   - Heavier training path is gated by Homeostasis (Calm/DeepWork only)
///   - Multi-horizon-ready architecture, v1 uses a single configured horizon
///   - Hot-swappable: Python brain can be replaced without C# changes
/// </summary>
public sealed class MlCortexService : BackgroundService, IAlephOrgan
{
    private const int PredictionTimeoutMs = 30_000;

    private readonly IAlephBus _bus;
    private readonly IAether _aether;
    private readonly IHomeostasis _homeostasis;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MlCortexService> _logger;

    private volatile bool _isActive;

    public string OrganName => "MlCortex";

    public IReadOnlyList<string> EventInterests { get; } =
        new[] { nameof(MetabolicEvent) }.AsReadOnly();

    public bool IsActive => _isActive;

    /// <summary>The active horizon key for v1. Configurable, defaults to "1d".</summary>
    private string ActiveHorizon { get; }

    public MlCortexService(
        IAlephBus bus,
        IAether aether,
        IHomeostasis homeostasis,
        IConfiguration configuration,
        ILogger<MlCortexService> logger)
    {
        _bus = bus;
        _aether = aether;
        _homeostasis = homeostasis;
        _configuration = configuration;
        _logger = logger;

        ActiveHorizon = configuration["Aether:Cortex:ActiveHorizon"] ?? "1d";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[MlCortex] Predictive organ starting. Subscribing to bloodstream...");

        var reader = _bus.Subscribe(
            OrganName,
            static evt => evt is MetabolicEvent);

        _isActive = true;

        _logger.LogInformation("[MlCortex] Active. Horizon={Horizon}. Awaiting MetabolicEvent blood cells.", ActiveHorizon);

        try
        {
            await foreach (var evt in reader.ReadAllAsync(stoppingToken))
            {
                if (evt is not MetabolicEvent me)
                    continue;

                try
                {
                    await ProcessMetabolicEventAsync(me, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[MlCortex] Unhandled error processing {Symbol}/{Interval}.",
                        me.Symbol, me.Interval);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        finally
        {
            _isActive = false;
            _logger.LogInformation("[MlCortex] Predictive organ stopped.");
        }
    }

    /// <summary>
    /// Process a single MetabolicEvent: build payload → call Python brain → publish PredictionEvent.
    /// </summary>
    private async Task ProcessMetabolicEventAsync(MetabolicEvent me, CancellationToken ct)
    {
        _logger.LogDebug(
            "[MlCortex] Processing {Symbol}/{Interval} (metabolic event {EventId}).",
            me.Symbol, me.Interval, me.EventId);

        // ── Step 1: Build the ML input payload from the MetabolicEvent ──
        var metabolicPayloadJson = BuildMetabolicPayload(me);

        // ── Step 2: Determine if training is allowed this cycle ──
        var snapshot = _homeostasis.GetSnapshot();
        var trainingAllowed = !_homeostasis.IsOverloaded && !_homeostasis.IsBreathless
            && snapshot.StressLevel < 0.5 && snapshot.FatigueLevel < 0.5;

        // ── Step 3: Call IAether.Ml.CortexPredictAsync ──
        var request = new MlCortexPredictRequest
        {
            Symbol = me.Symbol,
            Interval = me.Interval,
            ActiveHorizon = ActiveHorizon,
            AsOfUtc = me.AsOfUtc,
            MetabolicPayloadJson = metabolicPayloadJson
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PredictionTimeoutMs);

        AetherJsonResult result;
        try
        {
            result = await _aether.Ml.CortexPredictAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[MlCortex] Prediction timed out for {Symbol}/{Interval} after {Timeout}ms.",
                me.Symbol, me.Interval, PredictionTimeoutMs);
            return;
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.PayloadJson))
        {
            _logger.LogWarning(
                "[MlCortex] Python brain failed for {Symbol}/{Interval}: {Error}",
                me.Symbol, me.Interval, result.Error ?? "empty payload");
            return;
        }

        // ── Step 4: Parse Python brain output ──
        PredictionEvent? predictionEvent;
        try
        {
            predictionEvent = ParsePredictionOutput(me, result.PayloadJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[MlCortex] Failed to parse prediction JSON for {Symbol}/{Interval}.",
                me.Symbol, me.Interval);
            return;
        }

        if (predictionEvent is null)
            return;

        // ── Step 5: Publish PredictionEvent into the bloodstream ──
        await _bus.PublishAsync(predictionEvent, ct);

        _logger.LogInformation(
            "[MlCortex] Prediction for {Symbol}/{Interval}: Class={Class}, Confidence={Confidence:F3}, " +
            "Tendency={Tendency:F3}, ModelState={State}, Samples={Samples}",
            me.Symbol, me.Interval,
            predictionEvent.PredictedClass, predictionEvent.Confidence,
            predictionEvent.ActionTendency, predictionEvent.ModelState,
            predictionEvent.TrainedSamples);
    }

    /// <summary>
    /// Serialize the metabolic features into a compact JSON payload for the Python brain.
    /// Extracts only the fields the ML pipeline needs — keeps the boundary thin.
    /// </summary>
    private static string BuildMetabolicPayload(MetabolicEvent me)
    {
        var payload = new Dictionary<string, object?>
        {
            ["symbol"] = me.Symbol,
            ["interval"] = me.Interval,
            ["asof_utc"] = me.AsOfUtc,
            ["source_event_id"] = me.EventId.ToString(),
            ["bias"] = me.Bias,
            ["confidence"] = me.Confidence,
            ["row_count"] = me.RowCount,
            ["enough_for_long_trend"] = me.EnoughForLongTrend,
        };

        // Snapshot features
        var snap = me.Snapshot;
        if (snap is not null)
        {
            payload["price"] = snap.Price;
            payload["sma_20"] = snap.Sma20;
            payload["sma_50"] = snap.Sma50;
            payload["sma_200"] = snap.Sma200;
            payload["ema_12"] = snap.Ema12;
            payload["ema_26"] = snap.Ema26;
            payload["rsi_14"] = snap.Rsi14;
            payload["atr_14"] = snap.Atr14;
            payload["atr_pct"] = snap.AtrPct;
            payload["volatility_20"] = snap.Volatility20;
            payload["volume_sma_20"] = snap.VolumeSma20;
            payload["dist_sma_20"] = snap.DistSma20;
            payload["dist_sma_50"] = snap.DistSma50;
            payload["dist_sma_200"] = snap.DistSma200;

            if (snap.Macd is not null)
            {
                payload["macd_line"] = snap.Macd.Line;
                payload["macd_signal"] = snap.Macd.Signal;
                payload["macd_histogram"] = snap.Macd.Histogram;
            }

            if (snap.Bollinger is not null)
            {
                payload["bb_bandwidth"] = snap.Bollinger.Bandwidth;
            }
        }

        // Factor scores
        var fs = me.FactorScores;
        if (fs is not null)
        {
            payload["factor_trend"] = fs.Trend.Score;
            payload["factor_momentum"] = fs.Momentum.Score;
            payload["factor_volatility"] = fs.Volatility.Score;
            payload["factor_participation"] = fs.Participation.Score;
        }

        // Composite probabilities
        var comp = me.Composite;
        if (comp is not null)
        {
            payload["composite_bullish"] = comp.BullishProbability;
            payload["composite_bearish"] = comp.BearishProbability;
            payload["composite_neutral"] = comp.NeutralProbability;
            payload["composite_confidence"] = comp.Confidence;
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    /// <summary>
    /// Parse the Python brain's JSON output into a PredictionEvent blood cell.
    /// </summary>
    private PredictionEvent? ParsePredictionOutput(MetabolicEvent source, string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var errProp)
                ? errProp.GetString() ?? "unknown"
                : "unknown";
            _logger.LogWarning("[MlCortex] Python brain returned ok=false: {Error}", error);
            return null;
        }

        var predClass = SafeStr(root, "predicted_class") ?? "neutral";
        var modelState = SafeStr(root, "model_state") ?? "cold_start";
        var modelVersion = SafeStr(root, "model_version") ?? "v1.0.0";
        var confidence = SafeDbl(root, "confidence");
        var actionTendency = SafeDbl(root, "action_tendency");
        var trainedSamples = SafeInt(root, "trained_samples");
        var pendingStored = SafeBool(root, "pending_sample_stored");
        var trainingOccurred = SafeBool(root, "training_occurred");

        // Parse probabilities
        double pBullish = 0.33, pNeutral = 0.34, pBearish = 0.33;
        if (root.TryGetProperty("probabilities", out var probEl) && probEl.ValueKind == JsonValueKind.Object)
        {
            pBullish = SafeDbl(probEl, "bullish", 0.33);
            pNeutral = SafeDbl(probEl, "neutral", 0.34);
            pBearish = SafeDbl(probEl, "bearish", 0.33);
        }

        // Parse warnings
        var warnings = new List<string>();
        if (root.TryGetProperty("warnings", out var warnArr) && warnArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in warnArr.EnumerateArray())
            {
                var ws = w.GetString();
                if (!string.IsNullOrWhiteSpace(ws))
                    warnings.Add(ws);
            }
        }

        return new PredictionEvent
        {
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Source = "MlCortex",
            Kind = "cortex_prediction",
            Severity = PulseSeverity.Normal,
            Tags = new[] { "ml", "prediction", modelState },
            CorrelationId = source.CorrelationId,
            CausationId = source.EventId,

            Symbol = source.Symbol,
            Interval = source.Interval,
            ActiveHorizon = ActiveHorizon,
            AsOfUtc = source.AsOfUtc,
            SourceMetabolicEventId = source.EventId,
            ModelVersion = modelVersion,
            ModelState = modelState,
            TrainedSamples = trainedSamples,

            PredictedClass = predClass,
            Probabilities = new PredictionProbabilities
            {
                Bullish = pBullish,
                Neutral = pNeutral,
                Bearish = pBearish,
            },
            Confidence = confidence,
            ActionTendency = actionTendency,

            PendingSampleStored = pendingStored,
            TrainingOccurred = trainingOccurred,
            Warnings = warnings.AsReadOnly(),
        };
    }

    // ─── Minimal JSON helpers ────────────────────────────────────────

    private static string? SafeStr(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static double SafeDbl(JsonElement el, string prop, double fallback = 0)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : fallback;
    }

    private static int SafeInt(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;
    }

    private static bool SafeBool(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            && v.GetBoolean();
    }
}
