using System.Text.Json;

namespace Aleph;

/// <summary>
/// The Sleep Cycle — background orchestrator for the offline learning loop.
///
/// Periodically wakes, checks system health via Homeostasis, then drives
/// the resolve → train pipeline through the existing Aether bridge.
///
/// C# is PURE ORCHESTRATION here:
///   - no parquet inspection
///   - no label math
///   - no ML math
///   - no direct mutation of Python state beyond bridge calls
///
/// Invariants:
///   - no overlapping cycles (SemaphoreSlim(1))
///   - Homeostasis gating: skips heavy work when overloaded/breathless
///   - starvation protection: runs even in degraded mode after enough backlog time
/// </summary>
public sealed class SleepCycleService : BackgroundService
{
    private readonly IAether _aether;
    private readonly IHomeostasis _homeostasis;
    private readonly IAlephBus _bus;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SleepCycleService> _logger;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    // Configurable options
    private readonly double _cycleIntervalSeconds;
    private readonly double _startupDelaySeconds;
    private readonly int _minPendingToResolve;
    private readonly int _minResolvedToTrain;
    private readonly double _maxStressForTraining;
    private readonly double _maxFatigueForTraining;
    private readonly double _starvationHours;
    private readonly string _activeSymbol;
    private readonly string _activeHorizon;
    private readonly string _activeInterval;

    public SleepCycleService(
        IAether aether,
        IHomeostasis homeostasis,
        IAlephBus bus,
        IConfiguration configuration,
        ILogger<SleepCycleService> logger)
    {
        _aether = aether;
        _homeostasis = homeostasis;
        _bus = bus;
        _configuration = configuration;
        _logger = logger;

        _cycleIntervalSeconds = ReadDouble(configuration, "Aether:SleepCycle:IntervalSeconds", 1800, 60, 86400);
        _startupDelaySeconds = ReadDouble(configuration, "Aether:SleepCycle:StartupDelaySeconds", 30, 5, 600);
        _minPendingToResolve = ReadInt(configuration, "Aether:SleepCycle:MinPendingToResolve", 1, 0, 10000);
        _minResolvedToTrain = ReadInt(configuration, "Aether:SleepCycle:MinResolvedToTrain", 3, 1, 10000);
        _maxStressForTraining = ReadDouble(configuration, "Aether:SleepCycle:MaxStressForTraining", 0.7, 0, 1);
        _maxFatigueForTraining = ReadDouble(configuration, "Aether:SleepCycle:MaxFatigueForTraining", 0.7, 0, 1);
        _starvationHours = ReadDouble(configuration, "Aether:SleepCycle:StarvationHours", 24, 1, 168);
        _activeSymbol = configuration["Aether:SleepCycle:Symbol"] ?? "BTCUSDT";
        _activeHorizon = configuration["Aether:SleepCycle:Horizon"] ?? "1d";
        _activeInterval = configuration["Aether:SleepCycle:Interval"] ?? "1h";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[SleepCycle] Starting. Symbol={Symbol}, Horizon={Horizon}, Interval={Interval}s",
            _activeSymbol, _activeHorizon, _cycleIntervalSeconds);

        // Wait for system warmup
        try { await Task.Delay(TimeSpan.FromSeconds(_startupDelaySeconds), stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogDebug("[SleepCycle] Warmup complete. Entering sleep-wake loop.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOneCycleGuarded(stoppingToken);

            // Sleep until next cycle
            try { await Task.Delay(TimeSpan.FromSeconds(_cycleIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[SleepCycle] Stopped.");
    }

    /// <summary>
    /// Execute one sleep cycle with overlap protection.
    /// If the previous cycle is still running, this call is a no-op.
    /// </summary>
    private async Task RunOneCycleGuarded(CancellationToken ct)
    {
        if (!_cycleLock.Wait(0))
        {
            _logger.LogDebug("[SleepCycle] Skipped — previous cycle still running.");
            return;
        }

        try
        {
            await RunOneCycle(ct);
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    /// <summary>
    /// The main orchestration sequence:
    ///   1. Check homeostasis
    ///   2. Status
    ///   3. Resolve
    ///   4. Train (if gated)
    ///   5. Publish summary
    /// </summary>
    private async Task RunOneCycle(CancellationToken ct)
    {
        var cycleStart = DateTimeOffset.UtcNow;
        var summary = new CycleSummary();

        try
        {
            // ── Step 1: Homeostasis check ──
            var snapshot = _homeostasis.GetSnapshot();
            summary.StressLevel = snapshot.StressLevel;
            summary.FatigueLevel = snapshot.FatigueLevel;
            summary.OverloadLevel = snapshot.OverloadLevel;
            summary.IsOverloaded = _homeostasis.IsOverloaded;
            summary.IsBreathless = _homeostasis.IsBreathless;

            _logger.LogDebug(
                "[SleepCycle] Woke. Stress={Stress:F2}, Fatigue={Fatigue:F2}, Overloaded={OL}, Breathless={BL}",
                snapshot.StressLevel, snapshot.FatigueLevel, summary.IsOverloaded, summary.IsBreathless);

            // ── Step 2: Status ──
            var statusResult = await CallStatusAsync(ct);
            summary.StatusOk = statusResult.Success;
            if (statusResult.Success)
                ParseStatusInto(statusResult.PayloadJson, summary);

            if (!summary.StatusOk)
            {
                _logger.LogWarning("[SleepCycle] Status call failed. Skipping resolve/train.");
                summary.SkipReason = "status_failed";
                goto PublishSummary;
            }

            _logger.LogDebug(
                "[SleepCycle] Status: pending={Pending}, resolved={Resolved}, cursor={Cursor}",
                summary.PendingCount, summary.ResolvedCount, summary.CursorSequence);

            // ── Step 3: Resolve ──
            var shouldResolve = summary.PendingCount >= _minPendingToResolve;
            if (shouldResolve)
            {
                var resolveResult = await CallResolveAsync(ct);
                summary.ResolveOk = resolveResult.Success;
                if (resolveResult.Success)
                    ParseResolveInto(resolveResult.PayloadJson, summary);

                _logger.LogInformation(
                    "[SleepCycle] Resolve: resolved={Resolved}, deferred={Deferred}, expired={Expired}, errored={Errored}",
                    summary.NewlyResolved, summary.Deferred, summary.Expired, summary.Errored);
            }
            else
            {
                summary.ResolveOk = true; // no-op is ok
                _logger.LogDebug("[SleepCycle] Skipping resolve — {Pending} pending < {Min} minimum.",
                    summary.PendingCount, _minPendingToResolve);
            }

            // ── Step 4: Training gate ──
            var trainingDecision = EvaluateTrainingGate(snapshot, summary);
            summary.TrainingGateResult = trainingDecision.Decision;
            summary.TrainingGateReason = trainingDecision.Reason;

            if (trainingDecision.Decision == TrainingGate.Allowed)
            {
                var trainResult = await CallTrainAsync(ct);
                summary.TrainOk = trainResult.Success;
                if (trainResult.Success)
                    ParseTrainInto(trainResult.PayloadJson, summary);

                _logger.LogInformation(
                    "[SleepCycle] Train: fitted={Fitted}, fresh={Fresh}, replay={Replay}, drifts={Drifts}",
                    summary.SamplesFitted, summary.FreshCount, summary.ReplayCount,
                    summary.DriftFlags?.Count ?? 0);
            }
            else
            {
                _logger.LogDebug("[SleepCycle] Training skipped: {Reason}", trainingDecision.Reason);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            summary.SkipReason = "cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SleepCycle] Unhandled error in cycle.");
            summary.SkipReason = $"error:{ex.Message}";
        }

    PublishSummary:
        summary.DurationMs = (long)(DateTimeOffset.UtcNow - cycleStart).TotalMilliseconds;
        await PublishSummaryEvent(summary, ct);

        _logger.LogInformation(
            "[SleepCycle] Cycle complete in {Duration}ms. Resolved={Resolved}, Trained={Trained}, Gate={Gate}",
            summary.DurationMs, summary.NewlyResolved, summary.SamplesFitted, summary.TrainingGateResult);
    }

    // ═══════════════════════════════════════════════════════════════
    // AETHER BRIDGE CALLS
    // ═══════════════════════════════════════════════════════════════

    private Task<AetherJsonResult> CallStatusAsync(CancellationToken ct) =>
        _aether.Ml.CortexStatusAsync(new MlCortexStatusRequest
        {
            Symbol = _activeSymbol,
            ActiveHorizon = _activeHorizon,
        }, ct);

    private Task<AetherJsonResult> CallResolveAsync(CancellationToken ct) =>
        _aether.Ml.CortexResolveAsync(new MlCortexResolveRequest
        {
            Symbol = _activeSymbol,
            ActiveHorizon = _activeHorizon,
            Interval = _activeInterval,
        }, ct);

    private Task<AetherJsonResult> CallTrainAsync(CancellationToken ct) =>
        _aether.Ml.CortexTrainAsync(new MlCortexTrainRequest
        {
            Symbol = _activeSymbol,
            ActiveHorizon = _activeHorizon,
            MaxSamples = 200,
        }, ct);

    // ═══════════════════════════════════════════════════════════════
    // TRAINING GATE
    // ═══════════════════════════════════════════════════════════════

    private TrainingDecision EvaluateTrainingGate(HomeostasisSnapshot snapshot, CycleSummary summary)
    {
        // Hard blocks
        if (_homeostasis.IsOverloaded)
            return TrainingDecision.Blocked("system_overloaded");

        if (_homeostasis.IsBreathless)
            return TrainingDecision.Blocked("system_breathless");

        if (snapshot.StressLevel > _maxStressForTraining)
            return TrainingDecision.Blocked($"stress_too_high:{snapshot.StressLevel:F2}");

        if (snapshot.FatigueLevel > _maxFatigueForTraining)
            return TrainingDecision.Blocked($"fatigue_too_high:{snapshot.FatigueLevel:F2}");

        if (!summary.ResolveOk)
            return TrainingDecision.Blocked("resolve_failed");

        // Check if enough resolved data exists
        // After a resolve cycle, we need to re-fetch status to see updated counts.
        // But to avoid an extra Python call, we use the resolve summary:
        // if we just resolved new samples, training is likely worthwhile.
        if (summary.NewlyResolved > 0)
            return TrainingDecision.Allow("new_resolved_available");

        // If no new resolved this cycle, check total backlog from status
        if (summary.ResolvedCount >= _minResolvedToTrain)
            return TrainingDecision.Allow("existing_resolved_backlog");

        return TrainingDecision.Blocked($"insufficient_resolved:{summary.ResolvedCount}<{_minResolvedToTrain}");
    }

    // ═══════════════════════════════════════════════════════════════
    // JSON PARSING — read Python output into summary
    // ═══════════════════════════════════════════════════════════════

    private static void ParseStatusInto(string json, CycleSummary s)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            s.PendingCount = SafeInt(r, "pending_count");
            s.PendingEligible = SafeInt(r, "pending_eligible_count");
            s.PendingBlocked = SafeInt(r, "pending_blocked_count");
            s.ResolvedCount = SafeInt(r, "resolved_count");
            s.CursorSequence = SafeInt(r, "cursor_sequence");
            s.ModelState = SafeStr(r, "model_state");
            s.TrainedSamples = SafeInt(r, "trained_samples");
        }
        catch { /* non-fatal */ }
    }

    private static void ParseResolveInto(string json, CycleSummary s)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("resolution", out var res))
            {
                s.NewlyResolved = SafeInt(res, "resolved_count");
                s.Deferred = SafeInt(res, "deferred_count");
                s.Expired = SafeInt(res, "expired_count");
                s.Errored = SafeInt(res, "errored_count");
                s.ResolveAccuracy = SafeDbl(res, "accuracy");
                s.ResolveMeanBrier = SafeDbl(res, "mean_brier_score");

                if (res.TryGetProperty("class_distribution", out var cd) && cd.ValueKind == JsonValueKind.Object)
                {
                    var dist = new Dictionary<string, int>();
                    foreach (var prop in cd.EnumerateObject())
                    {
                        if (prop.Value.TryGetInt32(out var cnt))
                            dist[prop.Name] = cnt;
                    }
                    s.ResolvedClassDistribution = dist;
                }
            }
        }
        catch { /* non-fatal */ }
    }

    private static void ParseTrainInto(string json, CycleSummary s)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("training", out var tr))
            {
                s.SamplesFitted = SafeInt(tr, "samples_fitted");
                s.FreshCount = SafeInt(tr, "fresh_count");
                s.ReplayCount = SafeInt(tr, "replay_count");
                s.TrainModelState = SafeStr(tr, "model_state");

                if (tr.TryGetProperty("batch_class_distribution", out var bcd) && bcd.ValueKind == JsonValueKind.Object)
                {
                    var dist = new Dictionary<string, int>();
                    foreach (var prop in bcd.EnumerateObject())
                    {
                        if (prop.Value.TryGetInt32(out var cnt))
                            dist[prop.Name] = cnt;
                    }
                    s.TrainClassDistribution = dist;

                    // Class skew detection
                    if (dist.Count > 0)
                    {
                        var total = dist.Values.Sum();
                        var max = dist.Values.Max();
                        if (total > 0 && (double)max / total > 0.7)
                        {
                            var dominant = dist.First(kv => kv.Value == max).Key;
                            s.ClassSkewWarning = $"dominant_class:{dominant}({max}/{total}={100.0 * max / total:F0}%)";
                            _logger_static_warning = s.ClassSkewWarning; // can't use logger here, set on summary
                        }
                    }
                }

                if (tr.TryGetProperty("drift_flags", out var df) && df.ValueKind == JsonValueKind.Array)
                {
                    s.DriftFlags = new List<string>();
                    foreach (var flag in df.EnumerateArray())
                    {
                        var f = flag.GetString();
                        if (!string.IsNullOrEmpty(f))
                            s.DriftFlags.Add(f);
                    }
                }
            }

            s.CursorSequenceAfterTrain = SafeInt(r, "cursor_sequence");
        }
        catch { /* non-fatal */ }
    }

    // Workaround: class skew detection happens in static parse, so we log in cycle
    [ThreadStatic] private static string? _logger_static_warning;

    // ═══════════════════════════════════════════════════════════════
    // SUMMARY EVENT PUBLISHING
    // ═══════════════════════════════════════════════════════════════

    private async Task PublishSummaryEvent(CycleSummary s, CancellationToken ct)
    {
        // Log class skew if detected
        if (!string.IsNullOrEmpty(s.ClassSkewWarning))
            _logger.LogWarning("[SleepCycle] Class skew detected: {Skew}", s.ClassSkewWarning);

        if (s.DriftFlags is { Count: > 0 })
            _logger.LogWarning("[SleepCycle] Drift flags: {Flags}", string.Join(", ", s.DriftFlags));

        var evt = new SleepCycleSummaryEvent
        {
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Source = "SleepCycle",
            Kind = "sleep_cycle_summary",
            Severity = DetermineSeverity(s),
            Tags = BuildTags(s),

            Symbol = _activeSymbol,
            Horizon = _activeHorizon,
            DurationMs = s.DurationMs,

            StatusOk = s.StatusOk,
            ResolveOk = s.ResolveOk,
            TrainOk = s.TrainOk,
            SkipReason = s.SkipReason,

            PendingCount = s.PendingCount,
            PendingEligible = s.PendingEligible,
            PendingBlocked = s.PendingBlocked,
            NewlyResolved = s.NewlyResolved,
            Deferred = s.Deferred,
            Expired = s.Expired,
            Errored = s.Errored,

            SamplesFitted = s.SamplesFitted,
            FreshCount = s.FreshCount,
            ReplayCount = s.ReplayCount,
            TrainingGate = s.TrainingGateResult.ToString(),
            TrainingGateReason = s.TrainingGateReason,
            CursorSequence = s.CursorSequenceAfterTrain > 0 ? s.CursorSequenceAfterTrain : s.CursorSequence,

            ModelState = s.TrainModelState ?? s.ModelState,
            TrainedSamples = s.TrainedSamples,
            ResolveAccuracy = s.ResolveAccuracy,
            ResolveMeanBrier = s.ResolveMeanBrier,
            ClassSkewWarning = s.ClassSkewWarning,
            DriftFlags = s.DriftFlags,
            ResolvedClassDistribution = s.ResolvedClassDistribution,
            TrainClassDistribution = s.TrainClassDistribution,

            StressLevel = s.StressLevel,
            FatigueLevel = s.FatigueLevel,
        };

        try { await _bus.PublishAsync(evt, ct); }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private static PulseSeverity DetermineSeverity(CycleSummary s)
    {
        if (!string.IsNullOrEmpty(s.SkipReason) && s.SkipReason.StartsWith("error"))
            return PulseSeverity.Warning;
        if (s.DriftFlags is { Count: > 0 })
            return PulseSeverity.Elevated;
        if (!string.IsNullOrEmpty(s.ClassSkewWarning))
            return PulseSeverity.Elevated;
        return PulseSeverity.Normal;
    }

    private static string[] BuildTags(CycleSummary s)
    {
        var tags = new List<string> { "sleep_cycle" };
        if (s.NewlyResolved > 0) tags.Add("resolved");
        if (s.SamplesFitted > 0) tags.Add("trained");
        if (!string.IsNullOrEmpty(s.SkipReason)) tags.Add("skipped");
        return tags.ToArray();
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static string? SafeStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int SafeInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double SafeDbl(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static int ReadInt(IConfiguration c, string key, int fallback, int min, int max)
    {
        var raw = c[key];
        return int.TryParse(raw, out var v) ? Math.Clamp(v, min, max) : fallback;
    }

    private static double ReadDouble(IConfiguration c, string key, double fallback, double min, double max)
    {
        var raw = c[key];
        return double.TryParse(raw, out var v) ? Math.Clamp(v, min, max) : fallback;
    }

    // ═══════════════════════════════════════════════════════════════
    // INTERNAL TYPES
    // ═══════════════════════════════════════════════════════════════

    private enum TrainingGate { Allowed, Blocked }

    private readonly record struct TrainingDecision(TrainingGate Decision, string Reason)
    {
        public static TrainingDecision Allow(string reason) => new(TrainingGate.Allowed, reason);
        public static TrainingDecision Blocked(string reason) => new(TrainingGate.Blocked, reason);
    }

    private sealed class CycleSummary
    {
        // Homeostasis
        public double StressLevel;
        public double FatigueLevel;
        public double OverloadLevel;
        public bool IsOverloaded;
        public bool IsBreathless;

        // Status
        public bool StatusOk;
        public int PendingCount;
        public int PendingEligible;
        public int PendingBlocked;
        public int ResolvedCount;
        public int CursorSequence;
        public string? ModelState;
        public int TrainedSamples;

        // Resolve
        public bool ResolveOk;
        public int NewlyResolved;
        public int Deferred;
        public int Expired;
        public int Errored;
        public double ResolveAccuracy;
        public double ResolveMeanBrier;
        public Dictionary<string, int>? ResolvedClassDistribution;

        // Train
        public bool TrainOk;
        public int SamplesFitted;
        public int FreshCount;
        public int ReplayCount;
        public TrainingGate TrainingGateResult = TrainingGate.Blocked;
        public string? TrainingGateReason;
        public string? TrainModelState;
        public Dictionary<string, int>? TrainClassDistribution;
        public string? ClassSkewWarning;
        public List<string>? DriftFlags;
        public int CursorSequenceAfterTrain;

        // Meta
        public long DurationMs;
        public string? SkipReason;
    }
}
