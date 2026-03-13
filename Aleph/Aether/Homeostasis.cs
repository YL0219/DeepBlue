namespace Aleph;

/// <summary>
/// Thread-safe singleton implementing autonomic regulation.
/// Tracks external stress (market danger) and internal fatigue (system overload).
/// All mutations go through lock; all reads return immutable snapshots.
/// </summary>
public sealed class Homeostasis : IHomeostasis
{
    private readonly object _lock = new();
    private readonly ILogger<Homeostasis> _logger;

    // Mutable internal state — only touched under _lock
    private double _stressLevel;
    private double _fatigueLevel;
    private double _overloadLevel;
    private int _failureStreak;
    private long _lastPulseDurationMs;
    private DateTimeOffset _lastUpdatedUtc = DateTimeOffset.UtcNow;
    private readonly List<string> _activeFlags = new();

    // Tuning constants
    private const double StressDecayRate = 0.85;
    private const double FatigueDecayRate = 0.90;
    private const double OverloadDecayRate = 0.80;
    private const double StressInjectionScale = 0.25;
    private const double FatiguePerFailure = 0.15;
    private const double OverloadThreshold = 0.7;
    private const double BreathlessThreshold = 0.85;
    private const int BreathlessFailureStreak = 3;
    private const long SlowPulseThresholdMs = 15_000;

    public Homeostasis(ILogger<Homeostasis> logger)
    {
        _logger = logger;
    }

    public HomeostasisSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new HomeostasisSnapshot
            {
                StressLevel = _stressLevel,
                FatigueLevel = _fatigueLevel,
                OverloadLevel = _overloadLevel,
                FailureStreak = _failureStreak,
                LastPulseDurationMs = _lastPulseDurationMs,
                LastUpdatedUtc = _lastUpdatedUtc,
                ActiveFlags = _activeFlags.ToList().AsReadOnly()
            };
        }
    }

    public void Ingest(PulseEnvelope envelope)
    {
        if (envelope is null) return;

        lock (_lock)
        {
            switch (envelope.Kind)
            {
                case PulseKind.ExternalStress:
                    var intensity = (double)envelope.Severity / (double)PulseSeverity.Critical;
                    _stressLevel = Math.Clamp(_stressLevel + intensity * StressInjectionScale, 0, 1);
                    _logger.LogDebug(
                        "[Homeostasis] External stress ingested from {Source}. Severity={Severity}, StressLevel={Stress:F3}",
                        envelope.Source, envelope.Severity, _stressLevel);
                    break;

                case PulseKind.SystemEvent:
                    if (envelope.Tags is not null)
                    {
                        foreach (var tag in envelope.Tags)
                        {
                            if (!_activeFlags.Contains(tag))
                                _activeFlags.Add(tag);
                        }
                    }
                    break;
            }

            _lastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void RecordPulse(PulseEnvelope report)
    {
        if (report is null) return;

        lock (_lock)
        {
            var durationMs = report.Metrics?.GetValueOrDefault("durationMs") ?? 0;
            var success = (report.Metrics?.GetValueOrDefault("success") ?? 0) > 0.5;

            _lastPulseDurationMs = (long)durationMs;

            if (success)
            {
                _failureStreak = 0;
                // Decay fatigue on success
                _fatigueLevel = Math.Clamp(_fatigueLevel * FatigueDecayRate, 0, 1);
            }
            else
            {
                _failureStreak++;
                // Accumulate fatigue on failure
                _fatigueLevel = Math.Clamp(_fatigueLevel + FatiguePerFailure, 0, 1);
            }

            // Overload tracks how slow pulses are relative to threshold
            if (durationMs > SlowPulseThresholdMs)
            {
                var overloadIncrement = Math.Min((durationMs - SlowPulseThresholdMs) / (double)SlowPulseThresholdMs, 0.3);
                _overloadLevel = Math.Clamp(_overloadLevel + overloadIncrement, 0, 1);
            }
            else
            {
                _overloadLevel = Math.Clamp(_overloadLevel * OverloadDecayRate, 0, 1);
            }

            // Natural decay of stress each pulse
            _stressLevel = Math.Clamp(_stressLevel * StressDecayRate, 0, 1);

            _lastUpdatedUtc = DateTimeOffset.UtcNow;

            // Update flags
            _activeFlags.RemoveAll(f => f == "overloaded" || f == "breathless");
            if (IsOverloadedUnsafe) _activeFlags.Add("overloaded");
            if (IsBreathlessUnsafe) _activeFlags.Add("breathless");

            _logger.LogDebug(
                "[Homeostasis] Pulse recorded. Duration={DurationMs}ms, Success={Success}, " +
                "Stress={Stress:F3}, Fatigue={Fatigue:F3}, Overload={Overload:F3}, FailStreak={FailStreak}",
                (long)durationMs, success, _stressLevel, _fatigueLevel, _overloadLevel, _failureStreak);
        }
    }

    public bool IsOverloaded
    {
        get { lock (_lock) { return IsOverloadedUnsafe; } }
    }

    public bool IsBreathless
    {
        get { lock (_lock) { return IsBreathlessUnsafe; } }
    }

    // Must be called under _lock
    private bool IsOverloadedUnsafe => _overloadLevel >= OverloadThreshold;

    // Must be called under _lock
    private bool IsBreathlessUnsafe =>
        _fatigueLevel >= BreathlessThreshold || _failureStreak >= BreathlessFailureStreak;
}
