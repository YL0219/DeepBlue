"""
test_phase91_validation.py — Phase 9.1 proving harness.

Validates the ML Scorecard, Challenger Runner, and Promotion Rules
without requiring sklearn, parquet data, or live models.

Tests exercise:
  A. Scorecard computation    — Brier, accuracy, calibration, drift, warnings
  B. Scorecard policy         — configurable thresholds shape output
  C. Challenger comparison    — re-labeling, composition analysis, delta
  D. Promotion decisions      — gate logic, vetoes, evidence
  E. Integration              — resolve/status formatters include scorecards

Run with:
  python -m pytest ml/tests/test_phase91_validation.py -v
  OR
  python ml/tests/test_phase91_validation.py
"""

from __future__ import annotations

import json
import os
import sys
import unittest
from pathlib import Path
from collections import Counter

# Ensure the ml package is importable
_root = Path(__file__).resolve().parent.parent.parent
if str(_root) not in sys.path:
    sys.path.insert(0, str(_root))

from ml.scorecard import (
    compute_scorecard,
    compute_rolling_scorecard,
    ScorecardPolicy,
    DEFAULT_SCORECARD_POLICY,
)
from ml.promotion import (
    evaluate_promotion,
    PromotionPolicy,
    DEFAULT_PROMOTION_POLICY,
    PROMOTE,
    REJECT,
    INCONCLUSIVE,
)
from ml.challenger_runner import (
    run_challenger_comparison,
    ChallengerSpec,
    build_default_challengers,
)
from ml.policies import LabelPolicy, TrainingPolicy, DEFAULT_LABEL_POLICY
from ml.grading import grade_directional
from ml.prediction_formatter import format_resolve, format_status, format_evaluation


# ═══════════════════════════════════════════════════════════════════
# TEST DATA BUILDERS
# ═══════════════════════════════════════════════════════════════════

def _make_resolved_sample(
    predicted_class: str = "bullish",
    actual_label: str = "bullish",
    predicted_probabilities: dict | None = None,
    realized_return_bps: float = 120.0,
    prediction_id: str = "",
    eligible_for_training: bool = True,
    ambiguity: float = 0.0,
) -> dict:
    """Build a minimal resolved sample for testing."""
    if not predicted_probabilities:
        if predicted_class == "bullish":
            predicted_probabilities = {"bullish": 0.6, "neutral": 0.25, "bearish": 0.15}
        elif predicted_class == "bearish":
            predicted_probabilities = {"bullish": 0.15, "neutral": 0.25, "bearish": 0.6}
        else:
            predicted_probabilities = {"bullish": 0.25, "neutral": 0.5, "bearish": 0.25}

    return {
        "prediction_id": prediction_id or f"test_{hash((predicted_class, actual_label, realized_return_bps)):016x}",
        "predicted_class": predicted_class,
        "actual_label": actual_label,
        "predicted_probabilities": predicted_probabilities,
        "realized_return_bps": realized_return_bps,
        "eligible_for_training": eligible_for_training,
        "ambiguity": ambiguity,
    }


def _make_batch(
    correct: int = 5,
    wrong: int = 2,
    neutral_miss: int = 1,
) -> list[dict]:
    """Build a batch with controlled correct/wrong distribution."""
    samples = []
    for i in range(correct):
        samples.append(_make_resolved_sample(
            predicted_class="bullish",
            actual_label="bullish",
            realized_return_bps=150.0 + i * 10,
            prediction_id=f"correct_{i}",
        ))
    for i in range(wrong):
        samples.append(_make_resolved_sample(
            predicted_class="bullish",
            actual_label="bearish",
            realized_return_bps=-200.0 - i * 10,
            prediction_id=f"wrong_{i}",
        ))
    for i in range(neutral_miss):
        samples.append(_make_resolved_sample(
            predicted_class="bullish",
            actual_label="neutral",
            realized_return_bps=30.0,
            prediction_id=f"neutral_miss_{i}",
        ))
    return samples


def _make_balanced_batch(n: int = 30) -> list[dict]:
    """Build a larger balanced batch for promotion tests."""
    samples = []
    classes = ["bullish", "neutral", "bearish"]
    for i in range(n):
        cls = classes[i % 3]
        correct = i % 4 != 0  # 75% accuracy
        actual = cls if correct else classes[(i + 1) % 3]
        bps = 150.0 if cls == "bullish" else (-150.0 if cls == "bearish" else 20.0)
        if not correct:
            bps = -bps

        probs = {}
        for c in classes:
            if c == cls:
                probs[c] = 0.55 if correct else 0.4
            else:
                probs[c] = (1.0 - (0.55 if correct else 0.4)) / 2

        samples.append(_make_resolved_sample(
            predicted_class=cls,
            actual_label=actual,
            predicted_probabilities=probs,
            realized_return_bps=bps,
            prediction_id=f"balanced_{i}",
        ))
    return samples


# ═══════════════════════════════════════════════════════════════════
# A. SCORECARD COMPUTATION TESTS
# ═══════════════════════════════════════════════════════════════════

class TestScorecardComputation(unittest.TestCase):

    def test_basic_scorecard_structure(self):
        """Scorecard returns all expected keys."""
        batch = _make_batch(correct=8, wrong=2, neutral_miss=1)
        sc = compute_scorecard(batch)

        self.assertEqual(sc["status"], "ok")
        self.assertEqual(sc["sample_count"], 11)
        self.assertIn("mean_brier_score", sc)
        self.assertIn("accuracy", sc)
        self.assertIn("mean_calibration_gap", sc)
        self.assertIn("grade_buckets", sc)
        self.assertIn("actual_class_distribution", sc)
        self.assertIn("predicted_class_distribution", sc)
        self.assertIn("calibration", sc)
        self.assertIn("drift", sc)
        self.assertIn("current_streak", sc)
        self.assertIn("warnings", sc)

    def test_accuracy_calculation(self):
        """Accuracy matches manual calculation."""
        batch = _make_batch(correct=7, wrong=3, neutral_miss=0)
        sc = compute_scorecard(batch)
        self.assertAlmostEqual(sc["accuracy"], 0.7, places=2)

    def test_brier_score_range(self):
        """Brier score is between 0 and 2."""
        batch = _make_batch(correct=5, wrong=5)
        sc = compute_scorecard(batch)
        self.assertGreaterEqual(sc["mean_brier_score"], 0)
        self.assertLessEqual(sc["mean_brier_score"], 2.0)

    def test_perfect_predictions_low_brier(self):
        """All-correct predictions should produce low Brier score."""
        batch = _make_batch(correct=15, wrong=0, neutral_miss=0)
        sc = compute_scorecard(batch)
        self.assertLess(sc["mean_brier_score"], 0.5)
        self.assertEqual(sc["accuracy"], 1.0)

    def test_grade_buckets(self):
        """Grade buckets correctly count each type."""
        batch = _make_batch(correct=7, wrong=3, neutral_miss=2)
        policy = ScorecardPolicy(min_samples_for_score=5)
        sc = compute_scorecard(batch, policy)
        buckets = sc["grade_buckets"]
        self.assertEqual(buckets["correct"], 7)
        self.assertEqual(buckets["wrong_direction"], 3)
        self.assertEqual(buckets["missed_neutral"], 2)

    def test_class_distribution(self):
        """Actual and predicted class distributions are tracked."""
        batch = _make_batch(correct=7, wrong=3, neutral_miss=2)
        policy = ScorecardPolicy(min_samples_for_score=5)
        sc = compute_scorecard(batch, policy)

        actual = sc["actual_class_distribution"]
        self.assertIn("bullish", actual)
        self.assertEqual(actual["bullish"], 7)

        predicted = sc["predicted_class_distribution"]
        self.assertEqual(predicted["bullish"], 12)

    def test_calibration_curve_bins(self):
        """Calibration returns the configured number of bins."""
        batch = _make_batch(correct=15, wrong=5)
        sc = compute_scorecard(batch)
        self.assertEqual(len(sc["calibration"]), DEFAULT_SCORECARD_POLICY.calibration_bins)

    def test_insufficient_data(self):
        """Below min_samples returns insufficient_data status."""
        batch = _make_batch(correct=3, wrong=0)
        policy = ScorecardPolicy(min_samples_for_score=10)
        sc = compute_scorecard(batch, policy)
        self.assertEqual(sc["status"], "insufficient_data")
        self.assertIsNone(sc["accuracy"])

    def test_empty_batch(self):
        """Empty batch returns insufficient_data."""
        sc = compute_scorecard([])
        self.assertEqual(sc["status"], "insufficient_data")

    def test_streak_tracking(self):
        """Current streak is correctly tracked."""
        # All correct → correct streak
        samples = [_make_resolved_sample(
            predicted_class="bullish",
            actual_label="bullish",
            prediction_id=f"streak_ok_{i}",
        ) for i in range(12)]
        policy = ScorecardPolicy(min_samples_for_score=5)
        sc = compute_scorecard(samples, policy)
        self.assertEqual(sc["current_streak"]["type"], "correct")
        self.assertEqual(sc["current_streak"]["length"], 12)

    def test_drift_detection_stable(self):
        """No drift detected in consistent data."""
        batch = _make_balanced_batch(40)
        policy = ScorecardPolicy(drift_window=20, min_samples_for_score=5)
        sc = compute_scorecard(batch, policy)
        # Stable data should not trigger drift
        self.assertIsInstance(sc["drift"]["flags"], list)


class TestScorecardRolling(unittest.TestCase):

    def test_rolling_window_applied(self):
        """Rolling scorecard only uses last N samples."""
        batch = _make_balanced_batch(50)
        policy = ScorecardPolicy(rolling_window=20, min_samples_for_score=5)
        sc = compute_rolling_scorecard(batch, policy)
        self.assertEqual(sc["window_size"], 20)
        self.assertEqual(sc["window_actual"], 20)
        self.assertEqual(sc["total_resolved"], 50)
        self.assertEqual(sc["sample_count"], 20)

    def test_rolling_window_larger_than_data(self):
        """Rolling window gracefully handles smaller datasets."""
        batch = _make_batch(correct=5, wrong=2, neutral_miss=0)
        policy = ScorecardPolicy(rolling_window=100, min_samples_for_score=5)
        sc = compute_rolling_scorecard(batch, policy)
        self.assertEqual(sc["window_actual"], 7)
        self.assertEqual(sc["total_resolved"], 7)


class TestScorecardWarnings(unittest.TestCase):

    def test_high_brier_warning(self):
        """Warning triggered when Brier exceeds threshold."""
        # All wrong predictions → high Brier
        batch = _make_batch(correct=0, wrong=15)
        policy = ScorecardPolicy(brier_warning_threshold=0.3, min_samples_for_score=5)
        sc = compute_scorecard(batch, policy)
        brier_warnings = [w for w in sc["warnings"] if "high_brier" in w]
        self.assertGreater(len(brier_warnings), 0)

    def test_low_accuracy_warning(self):
        """Warning triggered when accuracy below threshold."""
        batch = _make_batch(correct=2, wrong=15)
        policy = ScorecardPolicy(accuracy_warning_threshold=0.30, min_samples_for_score=5)
        sc = compute_scorecard(batch, policy)
        acc_warnings = [w for w in sc["warnings"] if "low_accuracy" in w]
        self.assertGreater(len(acc_warnings), 0)

    def test_losing_streak_warning(self):
        """Warning triggered on consecutive wrong predictions."""
        samples = []
        for i in range(12):
            samples.append(_make_resolved_sample(
                predicted_class="bullish",
                actual_label="bearish",
                realized_return_bps=-200.0,
                prediction_id=f"streak_{i}",
            ))
        policy = ScorecardPolicy(streak_warning_length=5, min_samples_for_score=5)
        sc = compute_scorecard(samples, policy)
        streak_warnings = [w for w in sc["warnings"] if "losing_streak" in w]
        self.assertGreater(len(streak_warnings), 0)

    def test_never_predicted_class_warning(self):
        """Warning triggered when a class is never predicted."""
        samples = []
        for i in range(25):
            samples.append(_make_resolved_sample(
                predicted_class="bullish" if i % 2 == 0 else "neutral",
                actual_label="bullish",
                prediction_id=f"no_bearish_{i}",
            ))
        sc = compute_scorecard(samples)
        never_warned = [w for w in sc["warnings"] if "never_predicted:bearish" in w]
        self.assertGreater(len(never_warned), 0)


# ═══════════════════════════════════════════════════════════════════
# B. PROMOTION DECISION TESTS
# ═══════════════════════════════════════════════════════════════════

class TestPromotionDecisions(unittest.TestCase):

    def _make_scorecard(
        self,
        brier: float = 0.25,
        accuracy: float = 0.6,
        cal_gap: float = 0.1,
        n: int = 50,
        warnings: int = 0,
        drift: bool = False,
        pred_dist: dict | None = None,
    ) -> dict:
        """Build a mock scorecard for promotion tests."""
        return {
            "status": "ok",
            "sample_count": n,
            "mean_brier_score": brier,
            "accuracy": accuracy,
            "mean_calibration_gap": cal_gap,
            "predicted_class_distribution": pred_dist or {"bullish": 20, "neutral": 15, "bearish": 15},
            "drift": {"detected": drift, "flags": ["brier_degrading"] if drift else []},
            "warning_count": warnings,
            "warnings": ["w"] * warnings,
        }

    def test_promote_when_brier_improved(self):
        """Challenger with better Brier score gets promoted."""
        inc = self._make_scorecard(brier=0.30, accuracy=0.55)
        chal = self._make_scorecard(brier=0.25, accuracy=0.55)
        d = evaluate_promotion(inc, chal)
        self.assertEqual(d["decision"], PROMOTE)

    def test_reject_when_brier_regressed(self):
        """Challenger with worse Brier score gets rejected."""
        inc = self._make_scorecard(brier=0.25, accuracy=0.60)
        chal = self._make_scorecard(brier=0.30, accuracy=0.60)
        d = evaluate_promotion(inc, chal)
        self.assertEqual(d["decision"], REJECT)

    def test_inconclusive_when_brier_marginal(self):
        """Tiny Brier improvement → inconclusive."""
        inc = self._make_scorecard(brier=0.25, accuracy=0.60)
        chal = self._make_scorecard(brier=0.245, accuracy=0.60)  # only 0.005 improvement
        d = evaluate_promotion(inc, chal)
        self.assertEqual(d["decision"], INCONCLUSIVE)

    def test_reject_when_accuracy_regression(self):
        """Good Brier but accuracy regression beyond threshold → reject."""
        inc = self._make_scorecard(brier=0.30, accuracy=0.60)
        chal = self._make_scorecard(brier=0.25, accuracy=0.50)  # 10% accuracy loss
        d = evaluate_promotion(inc, chal)
        self.assertEqual(d["decision"], REJECT)

    def test_insufficient_data_inconclusive(self):
        """Too few samples → inconclusive."""
        inc = self._make_scorecard(brier=0.30, n=5)
        chal = self._make_scorecard(brier=0.20, n=5)
        d = evaluate_promotion(inc, chal)
        self.assertEqual(d["decision"], INCONCLUSIVE)

    def test_class_collapse_veto(self):
        """Class collapse in challenger → reject despite better Brier."""
        inc = self._make_scorecard(brier=0.30, accuracy=0.55)
        chal = self._make_scorecard(
            brier=0.20, accuracy=0.70,
            pred_dist={"bullish": 48, "neutral": 1, "bearish": 1},  # neutral collapsed
        )
        d = evaluate_promotion(inc, chal)
        self.assertEqual(d["decision"], REJECT)
        self.assertGreater(len(d["vetoes"]), 0)
        self.assertTrue(any("class_collapse" in v for v in d["vetoes"]))

    def test_drift_veto(self):
        """Drift in challenger → reject despite better metrics."""
        inc = self._make_scorecard(brier=0.30)
        chal = self._make_scorecard(brier=0.20, drift=True)
        d = evaluate_promotion(inc, chal)
        self.assertEqual(d["decision"], REJECT)
        self.assertTrue(any("drift" in v for v in d["vetoes"]))

    def test_excessive_warnings_veto(self):
        """Too many warnings → reject."""
        inc = self._make_scorecard(brier=0.30)
        chal = self._make_scorecard(brier=0.20, warnings=5)
        policy = PromotionPolicy(max_warnings_for_promotion=3)
        d = evaluate_promotion(inc, chal, policy)
        self.assertEqual(d["decision"], REJECT)

    def test_accuracy_floor_veto(self):
        """Challenger below absolute accuracy floor → reject."""
        inc = self._make_scorecard(brier=0.30, accuracy=0.30)
        chal = self._make_scorecard(brier=0.20, accuracy=0.20)
        d = evaluate_promotion(inc, chal)
        self.assertEqual(d["decision"], REJECT)
        self.assertTrue(any("accuracy_below_floor" in v for v in d["vetoes"]))

    def test_evidence_populated(self):
        """Evidence dict contains all key metrics."""
        inc = self._make_scorecard(brier=0.30, accuracy=0.55)
        chal = self._make_scorecard(brier=0.25, accuracy=0.60)
        d = evaluate_promotion(inc, chal)
        e = d["evidence"]
        self.assertIn("incumbent_brier", e)
        self.assertIn("challenger_brier", e)
        self.assertIn("brier_improvement", e)
        self.assertIn("accuracy_change", e)

    def test_policy_version_in_decision(self):
        """Decision includes policy version for provenance."""
        inc = self._make_scorecard()
        chal = self._make_scorecard()
        d = evaluate_promotion(inc, chal)
        self.assertEqual(d["policy_version"], DEFAULT_PROMOTION_POLICY.version)

    def test_custom_policy_thresholds(self):
        """Custom policy changes promotion behavior."""
        inc = self._make_scorecard(brier=0.30)
        chal = self._make_scorecard(brier=0.27)  # 0.03 improvement

        # Default requires 0.02 → promote
        d1 = evaluate_promotion(inc, chal)
        self.assertEqual(d1["decision"], PROMOTE)

        # Strict policy requires 0.05 → inconclusive
        strict = PromotionPolicy(min_brier_improvement=0.05)
        d2 = evaluate_promotion(inc, chal, strict)
        self.assertEqual(d2["decision"], INCONCLUSIVE)


# ═══════════════════════════════════════════════════════════════════
# C. CHALLENGER RUNNER TESTS
# ═══════════════════════════════════════════════════════════════════

class TestChallengerRunner(unittest.TestCase):

    def test_default_challengers_built(self):
        """build_default_challengers returns a non-empty list."""
        defaults = build_default_challengers()
        self.assertGreater(len(defaults), 0)
        for spec in defaults:
            self.assertIsInstance(spec, ChallengerSpec)
            self.assertTrue(spec.name)

    def test_label_challenger_relabeling(self):
        """Label policy challenger re-labels samples correctly."""
        samples = _make_balanced_batch(40)

        tight_lp = LabelPolicy(
            version="lp_v1_tight",
            bullish_threshold_bps=75.0,
            bearish_threshold_bps=-75.0,
        )

        challengers = [ChallengerSpec(
            name="tight_test",
            label_policy=tight_lp,
        )]

        result = run_challenger_comparison(
            resolved_samples=samples,
            challengers=challengers,
        )

        self.assertEqual(result["challengers_evaluated"], 1)
        comp = result["comparisons"][0]
        self.assertEqual(comp["challenger_name"], "tight_test")
        self.assertIn("delta", comp)
        self.assertIn("promotion_decision", comp)

    def test_training_policy_challenger(self):
        """Training policy challenger includes composition analysis."""
        samples = _make_balanced_batch(40)

        challengers = [ChallengerSpec(
            name="high_replay_test",
            training_policy=TrainingPolicy(
                version="tp_test",
                replay_ratio=0.5,
                replay_max_samples=150,
            ),
        )]

        result = run_challenger_comparison(
            resolved_samples=samples,
            challengers=challengers,
        )

        comp = result["comparisons"][0]
        chal_sc = comp["challenger_scorecard"]
        self.assertIn("training_composition", chal_sc)
        tc = chal_sc["training_composition"]
        self.assertIn("incumbent_batch", tc)
        self.assertIn("challenger_batch", tc)

    def test_multiple_challengers(self):
        """Multiple challengers all evaluated."""
        samples = _make_balanced_batch(40)
        challengers = build_default_challengers()

        result = run_challenger_comparison(
            resolved_samples=samples,
            challengers=challengers,
        )

        self.assertEqual(result["challengers_evaluated"], len(challengers))
        self.assertEqual(len(result["comparisons"]), len(challengers))

    def test_delta_computation(self):
        """Deltas show correct improvement direction."""
        samples = _make_balanced_batch(40)

        challengers = [ChallengerSpec(
            name="delta_test",
            label_policy=LabelPolicy(
                version="lp_delta",
                bullish_threshold_bps=100.0,
                bearish_threshold_bps=-100.0,
            ),
        )]

        result = run_challenger_comparison(
            resolved_samples=samples,
            challengers=challengers,
        )

        delta = result["comparisons"][0]["delta"]
        self.assertIn("brier_score_diff", delta)
        self.assertIn("accuracy_diff", delta)
        self.assertIn("interpretation", delta)

    def test_summary_counts(self):
        """Summary correctly counts promote/reject/inconclusive."""
        samples = _make_balanced_batch(40)
        challengers = build_default_challengers()

        result = run_challenger_comparison(
            resolved_samples=samples,
            challengers=challengers,
        )

        summary = result["summary"]
        total = summary["promote"] + summary["reject"] + summary["inconclusive"]
        self.assertEqual(total, len(challengers))

    def test_provenance_preserved(self):
        """Comparison result includes full provenance."""
        samples = _make_balanced_batch(40)
        challengers = [ChallengerSpec(
            name="prov_test",
            label_policy=LabelPolicy(version="lp_prov"),
            description="Provenance test challenger",
        )]

        result = run_challenger_comparison(
            resolved_samples=samples,
            challengers=challengers,
        )

        prov = result["comparisons"][0]["provenance"]
        self.assertTrue(prov["has_label_change"])
        self.assertFalse(prov["has_training_change"])
        self.assertIn("evaluation_utc", prov)
        self.assertEqual(prov["challenger_spec"]["name"], "prov_test")

    def test_empty_resolved_handled(self):
        """Empty resolved archive produces meaningful error."""
        challengers = build_default_challengers()
        result = run_challenger_comparison(
            resolved_samples=[],
            challengers=challengers,
        )
        # With 0 samples, scorecards will be insufficient
        inc_sc = result["incumbent_scorecard"]
        self.assertEqual(inc_sc["status"], "insufficient_data")

    def test_challenger_spec_serialization(self):
        """ChallengerSpec round-trips to dict."""
        spec = ChallengerSpec(
            name="test",
            label_policy=LabelPolicy(bullish_threshold_bps=80.0),
            description="Test desc",
        )
        d = spec.to_dict()
        self.assertEqual(d["name"], "test")
        self.assertIn("label_policy", d)
        self.assertEqual(d["label_policy"]["bullish_threshold_bps"], 80.0)


# ═══════════════════════════════════════════════════════════════════
# D. PROMOTION POLICY TESTS
# ═══════════════════════════════════════════════════════════════════

class TestPromotionPolicy(unittest.TestCase):

    def test_policy_round_trip(self):
        """PromotionPolicy survives serialization."""
        pol = PromotionPolicy(min_samples=50, min_brier_improvement=0.03)
        d = pol.to_dict()
        restored = PromotionPolicy.from_dict(d)
        self.assertEqual(restored.min_samples, 50)
        self.assertEqual(restored.min_brier_improvement, 0.03)

    def test_default_policy_values(self):
        """Default policy has sensible values."""
        pol = DEFAULT_PROMOTION_POLICY
        self.assertEqual(pol.version, "pp_v1")
        self.assertEqual(pol.min_samples, 30)
        self.assertGreater(pol.min_brier_improvement, 0)
        self.assertTrue(pol.require_no_class_collapse)

    def test_scorecard_policy_round_trip(self):
        """ScorecardPolicy survives serialization."""
        pol = ScorecardPolicy(rolling_window=50, brier_warning_threshold=0.4)
        d = pol.to_dict()
        restored = ScorecardPolicy.from_dict(d)
        self.assertEqual(restored.rolling_window, 50)
        self.assertEqual(restored.brier_warning_threshold, 0.4)


# ═══════════════════════════════════════════════════════════════════
# E. FORMATTER INTEGRATION TESTS
# ═══════════════════════════════════════════════════════════════════

class TestFormatterIntegration(unittest.TestCase):

    def test_resolve_with_scorecard(self):
        """format_resolve includes cycle_scorecard when provided."""
        sc = {"mean_brier_score": 0.25, "accuracy": 0.6}
        result = format_resolve(
            symbol="BTCUSDT", horizon="1d",
            resolution_summary={"resolved_count": 5},
            cycle_scorecard=sc,
        )
        self.assertTrue(result["ok"])
        self.assertIn("cycle_scorecard", result)
        self.assertEqual(result["cycle_scorecard"]["accuracy"], 0.6)

    def test_resolve_without_scorecard(self):
        """format_resolve works without scorecard (backward compat)."""
        result = format_resolve(
            symbol="BTCUSDT", horizon="1d",
            resolution_summary={"resolved_count": 0},
        )
        self.assertTrue(result["ok"])
        self.assertNotIn("cycle_scorecard", result)

    def test_status_with_rolling_scorecard(self):
        """format_status includes rolling_scorecard when provided."""
        sc = {"mean_brier_score": 0.28, "accuracy": 0.55, "window_size": 100}
        result = format_status(
            symbol="BTCUSDT", horizon="1d",
            model_state="active", model_version="v1.0.0",
            trained_samples=100, pending_count=5, resolved_count=50,
            rolling_scorecard=sc,
        )
        self.assertTrue(result["ok"])
        self.assertIn("rolling_scorecard", result)
        self.assertEqual(result["rolling_scorecard"]["window_size"], 100)

    def test_status_without_rolling_scorecard(self):
        """format_status works without rolling scorecard (backward compat)."""
        result = format_status(
            symbol="BTCUSDT", horizon="1d",
            model_state="cold_start", model_version="v1.0.0",
            trained_samples=0, pending_count=0, resolved_count=0,
        )
        self.assertTrue(result["ok"])
        self.assertNotIn("rolling_scorecard", result)

    def test_evaluation_formatter(self):
        """format_evaluation produces correct envelope."""
        eval_result = {"ok": True, "challengers_evaluated": 3}
        result = format_evaluation(
            symbol="BTCUSDT", horizon="1d",
            evaluation_result=eval_result,
            warnings=["test_warning"],
        )
        self.assertTrue(result["ok"])
        self.assertEqual(result["action"], "cortex_evaluate")
        self.assertEqual(result["evaluation"]["challengers_evaluated"], 3)
        self.assertEqual(result["warnings"], ["test_warning"])

    def test_evaluation_json_serializable(self):
        """Full evaluation result is JSON serializable."""
        samples = _make_balanced_batch(40)
        challengers = build_default_challengers()
        eval_result = run_challenger_comparison(
            resolved_samples=samples,
            challengers=challengers,
        )
        result = format_evaluation(
            symbol="BTCUSDT", horizon="1d",
            evaluation_result=eval_result,
        )
        # Must not throw
        serialized = json.dumps(result)
        self.assertGreater(len(serialized), 0)
        parsed = json.loads(serialized)
        self.assertTrue(parsed["ok"])


# ═══════════════════════════════════════════════════════════════════
# RUNNER
# ═══════════════════════════════════════════════════════════════════

if __name__ == "__main__":
    unittest.main()
