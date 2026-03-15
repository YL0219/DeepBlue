namespace Aleph;

/// <summary>
/// Refined blood cell produced by the ML Cortex after processing a MetabolicEvent.
/// Carries a probabilistic prediction for downstream consumers (primarily Arbiter).
///
/// This is a prediction, NOT a trade decision. Arbiter decides what to do with it.
///
/// Key properties:
///   - 3-class probabilities (bullish / neutral / bearish)
///   - Confidence and action tendency for Arbiter reasoning
///   - Model state tracking (cold_start → warming → active)
///   - Provenance back to the source MetabolicEvent
///   - Multi-horizon-ready via ActiveHorizon key
/// </summary>
public sealed record PredictionEvent : AlephEvent
{
    // ─── Identity ────────────────────────────────────────────────────

    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required string ActiveHorizon { get; init; }
    public required string AsOfUtc { get; init; }

    // ─── Provenance ──────────────────────────────────────────────────

    /// <summary>EventId of the MetabolicEvent that triggered this prediction.</summary>
    public required Guid SourceMetabolicEventId { get; init; }

    /// <summary>Version identifier of the model that produced this prediction.</summary>
    public required string ModelVersion { get; init; }

    // ─── Model State ─────────────────────────────────────────────────

    /// <summary>Current state of the model: cold_start, warming, active.</summary>
    public required string ModelState { get; init; }

    /// <summary>Number of samples the model has been trained on.</summary>
    public int TrainedSamples { get; init; }

    // ─── Prediction Output ───────────────────────────────────────────

    /// <summary>Predicted class: bullish, neutral, or bearish.</summary>
    public required string PredictedClass { get; init; }

    /// <summary>Class probabilities (bullish, neutral, bearish).</summary>
    public required PredictionProbabilities Probabilities { get; init; }

    /// <summary>Overall confidence in the prediction [0, 1].</summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Directional tendency in [-1, 1].
    /// Negative = bearish lean, 0 = neutral, Positive = bullish lean.
    /// Arbiter uses this as a continuous signal, not a binary decision.
    /// </summary>
    public required double ActionTendency { get; init; }

    // ─── Training Status (lightweight) ───────────────────────────────

    /// <summary>Whether a pending sample was stored for future training.</summary>
    public bool PendingSampleStored { get; init; }

    /// <summary>Whether an incremental training update occurred on this cycle.</summary>
    public bool TrainingOccurred { get; init; }

    // ─── Warnings ────────────────────────────────────────────────────

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 3-class probability distribution for market direction prediction.
/// Values should sum to ~1.0.
/// </summary>
public sealed record PredictionProbabilities
{
    public required double Bullish { get; init; }
    public required double Neutral { get; init; }
    public required double Bearish { get; init; }
}
