"""
scorecard.py — ML Scorecard engine for the Cortex learning loop.

Computes operationally useful intelligence scorecards over resolved
prediction history. Measures directional quality, calibration,
class stability, and drift — surfaced to C# as compact summaries.

Design principles:
  - Policy-driven thresholds, not hardcoded magic numbers
  - Supports both full-archive and rolling-window computation
  - Grading reuses existing grade_directional() from grading.py
  - All outputs are plain dicts — JSON-serializable, no custom objects
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field, asdict
from collections import Counter
from typing import Any

from .grading import grade_directional


# ═══════════════════════════════════════════════════════════════════
# SCORECARD POLICY
# ═══════════════════════════════════════════════════════════════════

@dataclass(frozen=True)
class ScorecardPolicy:
    """Policy governing scorecard computation thresholds and warnings."""

    version: str = "sc_v1"

    # ── Window configuration ──
    rolling_window: int = 100          # last N resolved samples for rolling metrics
    min_samples_for_score: int = 10    # below this, scorecard is "insufficient_data"

    # ── Quality warning thresholds ──
    brier_warning_threshold: float = 0.35      # mean Brier above = degrading
    accuracy_warning_threshold: float = 0.30   # accuracy below = degrading
    class_collapse_threshold: float = 0.05     # any class below 5% of predictions = collapse

    # ── Calibration configuration ──
    calibration_bins: int = 5          # number of confidence bins (quintiles)
    calibration_gap_warning: float = 0.20  # mean calibration gap above = warning

    # ── Drift detection ──
    drift_window: int = 20             # compare first-half vs second-half of this window
    drift_brier_threshold: float = 0.10    # Brier shift between halves = drift flag
    drift_accuracy_threshold: float = 0.15 # accuracy shift between halves = drift flag

    # ── Streak tracking ──
    streak_warning_length: int = 5     # N consecutive wrong predictions = warning

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> ScorecardPolicy:
        known = {f.name for f in cls.__dataclass_fields__.values()}
        return cls(**{k: v for k, v in d.items() if k in known})


DEFAULT_SCORECARD_POLICY = ScorecardPolicy()


# ═══════════════════════════════════════════════════════════════════
# SCORECARD COMPUTATION
# ═══════════════════════════════════════════════════════════════════

def compute_scorecard(
    resolved_samples: list[dict],
    policy: ScorecardPolicy | None = None,
    label_override: str | None = None,
) -> dict[str, Any]:
    """
    Compute a full scorecard over a batch of resolved samples.

    Args:
        resolved_samples: List of resolved prediction records.
            Each must have: predicted_class, predicted_probabilities,
            actual_label (or the label from label_override).
        policy: Scorecard policy for thresholds. Uses default if None.
        label_override: If set, use this key instead of 'actual_label'
            for the ground truth label. Useful for challenger re-labeling.

    Returns:
        Dict with scorecard metrics, warnings, and metadata.
    """
    pol = policy or DEFAULT_SCORECARD_POLICY
    label_key = label_override or "actual_label"

    n = len(resolved_samples)
    if n < pol.min_samples_for_score:
        return _insufficient_scorecard(n, pol)

    # ── Grade each sample ──
    grades = []
    for sample in resolved_samples:
        predicted = sample.get("predicted_class", "neutral")
        actual = sample.get(label_key, "neutral")
        probs = sample.get("predicted_probabilities", {})
        grade = grade_directional(predicted, actual, probs)
        grades.append(grade)

    # ── Core metrics ──
    brier_scores = [g["brier_score"] for g in grades]
    correct_flags = [g["correct"] for g in grades]
    calibration_gaps = [g["calibration_gap"] for g in grades]

    mean_brier = _safe_mean(brier_scores)
    accuracy = sum(correct_flags) / n if n > 0 else 0.0
    mean_calibration_gap = _safe_mean(calibration_gaps)

    # ── Grade bucket distribution ──
    bucket_counter = Counter(g["grade_bucket"] for g in grades)
    grade_buckets = {
        "correct": bucket_counter.get("correct", 0),
        "wrong_direction": bucket_counter.get("wrong_direction", 0),
        "missed_neutral": bucket_counter.get("missed_neutral", 0),
        "false_neutral": bucket_counter.get("false_neutral", 0),
    }

    # ── Class distributions ──
    actual_dist = Counter(s.get(label_key, "neutral") for s in resolved_samples)
    predicted_dist = Counter(s.get("predicted_class", "neutral") for s in resolved_samples)

    # ── Calibration curve (binned) ──
    calibration = _compute_calibration_curve(resolved_samples, label_key, pol)

    # ── Drift detection ──
    drift = _detect_drift(grades, pol)

    # ── Streak analysis ──
    streak = _compute_streak(correct_flags)

    # ── Warnings ──
    warnings = _generate_warnings(
        mean_brier, accuracy, mean_calibration_gap,
        actual_dist, predicted_dist, n, streak, pol
    )

    return {
        "scorecard_version": pol.version,
        "sample_count": n,
        "status": "ok",

        # Primary metrics
        "mean_brier_score": round(mean_brier, 5),
        "accuracy": round(accuracy, 4),
        "mean_calibration_gap": round(mean_calibration_gap, 5),

        # Grade buckets
        "grade_buckets": grade_buckets,

        # Class distributions
        "actual_class_distribution": dict(actual_dist),
        "predicted_class_distribution": dict(predicted_dist),

        # Calibration curve
        "calibration": calibration,

        # Drift
        "drift": drift,

        # Streak
        "current_streak": streak,

        # Warnings
        "warnings": warnings,
        "warning_count": len(warnings),
    }


def compute_rolling_scorecard(
    resolved_samples: list[dict],
    policy: ScorecardPolicy | None = None,
) -> dict[str, Any]:
    """
    Compute a rolling-window scorecard over the most recent N samples.
    Uses policy.rolling_window to determine window size.
    """
    pol = policy or DEFAULT_SCORECARD_POLICY
    window = pol.rolling_window

    recent = resolved_samples[-window:] if len(resolved_samples) > window else resolved_samples

    sc = compute_scorecard(recent, pol)
    sc["window_size"] = window
    sc["total_resolved"] = len(resolved_samples)
    sc["window_actual"] = len(recent)
    return sc


# ═══════════════════════════════════════════════════════════════════
# CALIBRATION CURVE
# ═══════════════════════════════════════════════════════════════════

def _compute_calibration_curve(
    samples: list[dict],
    label_key: str,
    policy: ScorecardPolicy,
) -> list[dict]:
    """
    Bin predictions by predicted confidence and compare
    predicted probability vs actual hit rate.

    Returns list of bin dicts:
        { bin_lower, bin_upper, count, mean_predicted_prob, actual_hit_rate, gap }
    """
    n_bins = policy.calibration_bins
    if n_bins < 2:
        n_bins = 5

    # Collect (predicted_prob_for_predicted_class, was_correct) pairs
    entries = []
    for sample in samples:
        predicted = sample.get("predicted_class", "neutral")
        actual = sample.get(label_key, "neutral")
        probs = sample.get("predicted_probabilities", {})
        prob_for_predicted = probs.get(predicted, 1.0 / 3.0)
        correct = 1 if predicted == actual else 0
        entries.append((prob_for_predicted, correct))

    if not entries:
        return []

    # Sort by predicted probability
    entries.sort(key=lambda e: e[0])

    # Create equal-width bins from 0 to 1
    bins = []
    bin_width = 1.0 / n_bins
    for i in range(n_bins):
        lo = i * bin_width
        hi = (i + 1) * bin_width
        bin_entries = [e for e in entries if lo <= e[0] < hi or (i == n_bins - 1 and e[0] == hi)]

        if bin_entries:
            mean_pred = _safe_mean([e[0] for e in bin_entries])
            actual_rate = sum(e[1] for e in bin_entries) / len(bin_entries)
            gap = abs(mean_pred - actual_rate)
        else:
            mean_pred = (lo + hi) / 2.0
            actual_rate = 0.0
            gap = 0.0

        bins.append({
            "bin_lower": round(lo, 3),
            "bin_upper": round(hi, 3),
            "count": len(bin_entries),
            "mean_predicted_prob": round(mean_pred, 4),
            "actual_hit_rate": round(actual_rate, 4),
            "gap": round(gap, 4),
        })

    return bins


# ═══════════════════════════════════════════════════════════════════
# DRIFT DETECTION
# ═══════════════════════════════════════════════════════════════════

def _detect_drift(
    grades: list[dict],
    policy: ScorecardPolicy,
) -> dict[str, Any]:
    """
    Compare first-half vs second-half of the sample window.
    Flags drift if Brier or accuracy shift beyond thresholds.
    """
    n = len(grades)
    if n < policy.drift_window:
        return {"detected": False, "reason": "insufficient_samples", "flags": []}

    # Use the last drift_window samples
    window = grades[-policy.drift_window:]
    mid = len(window) // 2
    first_half = window[:mid]
    second_half = window[mid:]

    first_brier = _safe_mean([g["brier_score"] for g in first_half])
    second_brier = _safe_mean([g["brier_score"] for g in second_half])
    brier_shift = second_brier - first_brier

    first_acc = sum(g["correct"] for g in first_half) / len(first_half) if first_half else 0
    second_acc = sum(g["correct"] for g in second_half) / len(second_half) if second_half else 0
    acc_shift = first_acc - second_acc  # positive = degrading

    flags = []
    if brier_shift > policy.drift_brier_threshold:
        flags.append(f"brier_degrading:{brier_shift:+.3f}")
    if acc_shift > policy.drift_accuracy_threshold:
        flags.append(f"accuracy_degrading:{acc_shift:+.3f}")

    # Check for class collapse in recent predictions
    recent_preds = Counter(
        g.get("grade_bucket", "unknown") for g in second_half
    )
    if recent_preds.get("wrong_direction", 0) > len(second_half) * 0.6:
        flags.append("directional_collapse")

    return {
        "detected": len(flags) > 0,
        "brier_shift": round(brier_shift, 4),
        "accuracy_shift": round(acc_shift, 4),
        "first_half_brier": round(first_brier, 4),
        "second_half_brier": round(second_brier, 4),
        "first_half_accuracy": round(first_acc, 4),
        "second_half_accuracy": round(second_acc, 4),
        "flags": flags,
    }


# ═══════════════════════════════════════════════════════════════════
# STREAK ANALYSIS
# ═══════════════════════════════════════════════════════════════════

def _compute_streak(correct_flags: list[bool]) -> dict[str, Any]:
    """Track current prediction streak (consecutive correct or wrong)."""
    if not correct_flags:
        return {"type": "none", "length": 0}

    current_val = correct_flags[-1]
    streak_len = 0
    for flag in reversed(correct_flags):
        if flag == current_val:
            streak_len += 1
        else:
            break

    return {
        "type": "correct" if current_val else "wrong",
        "length": streak_len,
    }


# ═══════════════════════════════════════════════════════════════════
# WARNING GENERATION
# ═══════════════════════════════════════════════════════════════════

def _generate_warnings(
    mean_brier: float,
    accuracy: float,
    mean_cal_gap: float,
    actual_dist: Counter,
    predicted_dist: Counter,
    n: int,
    streak: dict,
    policy: ScorecardPolicy,
) -> list[str]:
    """Generate human-readable warning strings based on policy thresholds."""
    warnings = []

    if mean_brier > policy.brier_warning_threshold:
        warnings.append(f"high_brier_score:{mean_brier:.3f}>{policy.brier_warning_threshold}")

    if accuracy < policy.accuracy_warning_threshold:
        warnings.append(f"low_accuracy:{accuracy:.3f}<{policy.accuracy_warning_threshold}")

    if mean_cal_gap > policy.calibration_gap_warning:
        warnings.append(f"poor_calibration:{mean_cal_gap:.3f}>{policy.calibration_gap_warning}")

    # Class collapse detection — any class below threshold in actual labels
    for cls in ("bullish", "neutral", "bearish"):
        actual_frac = actual_dist.get(cls, 0) / n if n > 0 else 0
        pred_frac = predicted_dist.get(cls, 0) / n if n > 0 else 0

        if actual_frac < policy.class_collapse_threshold and actual_dist.get(cls, 0) > 0:
            warnings.append(f"actual_class_thin:{cls}({actual_frac:.1%})")
        if pred_frac < policy.class_collapse_threshold and n > 20:
            warnings.append(f"predicted_class_collapse:{cls}({pred_frac:.1%})")

    # Never-predicted class
    all_classes = {"bullish", "neutral", "bearish"}
    predicted_classes = set(predicted_dist.keys())
    missing = all_classes - predicted_classes
    if missing and n > 20:
        for m in missing:
            warnings.append(f"never_predicted:{m}")

    # Streak warning
    if streak.get("type") == "wrong" and streak.get("length", 0) >= policy.streak_warning_length:
        warnings.append(f"losing_streak:{streak['length']}")

    return warnings


# ═══════════════════════════════════════════════════════════════════
# INSUFFICIENT DATA FALLBACK
# ═══════════════════════════════════════════════════════════════════

def _insufficient_scorecard(n: int, policy: ScorecardPolicy) -> dict[str, Any]:
    """Return a minimal scorecard when sample count is too low."""
    return {
        "scorecard_version": policy.version,
        "sample_count": n,
        "status": "insufficient_data",
        "min_required": policy.min_samples_for_score,
        "mean_brier_score": None,
        "accuracy": None,
        "mean_calibration_gap": None,
        "grade_buckets": {},
        "actual_class_distribution": {},
        "predicted_class_distribution": {},
        "calibration": [],
        "drift": {"detected": False, "flags": []},
        "current_streak": {"type": "none", "length": 0},
        "warnings": ["insufficient_samples"],
        "warning_count": 1,
    }


# ═══════════════════════════════════════════════════════════════════
# HELPERS
# ═══════════════════════════════════════════════════════════════════

def _safe_mean(values: list[float]) -> float:
    """Mean that handles empty lists and NaN values gracefully."""
    clean = [v for v in values if v is not None and not math.isnan(v)]
    return sum(clean) / len(clean) if clean else 0.0
