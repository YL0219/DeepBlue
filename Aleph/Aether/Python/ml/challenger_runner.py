"""
challenger_runner.py — Offline incumbent-vs-challenger policy evaluation.

Compares the incumbent policy setup against one or more challenger
policy variants using the resolved truth archive as common ground.

Design principles:
  - Purely offline, auditable, and safe
  - No live model retraining — evaluation uses resolved history only
  - Policy objects remain versioned and inspectable
  - Results preserve full provenance for human and Arbiter review
  - Supports label-policy and training-policy challenger dimensions

Challenger types:
  A. Label policy challengers:
     Re-label resolved samples using a different LabelPolicy,
     re-grade predictions against new labels, and compare scorecards.

  B. Training policy challengers:
     Analyze what the training batch composition would look like under
     a different TrainingPolicy. Does not retrain — composition only.
"""

from __future__ import annotations

from dataclasses import dataclass, field, asdict
from datetime import datetime, timezone
from typing import Any

from .policies import LabelPolicy, TrainingPolicy, DEFAULT_LABEL_POLICY, DEFAULT_TRAINING_POLICY
from .scorecard import compute_scorecard, ScorecardPolicy, DEFAULT_SCORECARD_POLICY
from .grading import grade_directional
from .promotion import evaluate_promotion, PromotionPolicy, DEFAULT_PROMOTION_POLICY


# ═══════════════════════════════════════════════════════════════════
# CHALLENGER SPECIFICATION
# ═══════════════════════════════════════════════════════════════════

@dataclass(frozen=True)
class ChallengerSpec:
    """
    Specification for a single challenger policy variant.

    Fields:
        name: Human-readable identifier (e.g., "tight_labels", "high_replay").
        label_policy: Override label policy. None = use incumbent's.
        training_policy: Override training policy. None = use incumbent's.
        description: Optional notes for provenance.
    """
    name: str
    label_policy: LabelPolicy | None = None
    training_policy: TrainingPolicy | None = None
    description: str = ""

    def to_dict(self) -> dict[str, Any]:
        d: dict[str, Any] = {
            "name": self.name,
            "description": self.description,
        }
        if self.label_policy:
            d["label_policy"] = self.label_policy.to_dict()
        if self.training_policy:
            d["training_policy"] = self.training_policy.to_dict()
        return d


# ═══════════════════════════════════════════════════════════════════
# COMPARISON RESULT
# ═══════════════════════════════════════════════════════════════════

@dataclass
class ComparisonResult:
    """Full comparison output: incumbent vs one challenger."""
    challenger_name: str
    incumbent_scorecard: dict[str, Any]
    challenger_scorecard: dict[str, Any]
    delta: dict[str, Any]
    promotion_decision: dict[str, Any]
    sample_count: int
    provenance: dict[str, Any]

    def to_dict(self) -> dict[str, Any]:
        return {
            "challenger_name": self.challenger_name,
            "incumbent_scorecard": self.incumbent_scorecard,
            "challenger_scorecard": self.challenger_scorecard,
            "delta": self.delta,
            "promotion_decision": self.promotion_decision,
            "sample_count": self.sample_count,
            "provenance": self.provenance,
        }


# ═══════════════════════════════════════════════════════════════════
# CHALLENGER RUNNER
# ═══════════════════════════════════════════════════════════════════

def run_challenger_comparison(
    resolved_samples: list[dict],
    challengers: list[ChallengerSpec],
    incumbent_label_policy: LabelPolicy | None = None,
    incumbent_training_policy: TrainingPolicy | None = None,
    scorecard_policy: ScorecardPolicy | None = None,
    promotion_policy: PromotionPolicy | None = None,
) -> dict[str, Any]:
    """
    Run offline comparison of incumbent vs one or more challengers.

    Args:
        resolved_samples: Full resolved truth archive.
        challengers: List of ChallengerSpec variants to evaluate.
        incumbent_label_policy: The current production label policy.
        incumbent_training_policy: The current production training policy.
        scorecard_policy: Thresholds for scorecard computation.
        promotion_policy: Rules for promotion decisions.

    Returns:
        Dict with incumbent scorecard, per-challenger comparisons,
        and a summary of recommendations.
    """
    inc_lp = incumbent_label_policy or DEFAULT_LABEL_POLICY
    inc_tp = incumbent_training_policy or DEFAULT_TRAINING_POLICY
    sc_pol = scorecard_policy or DEFAULT_SCORECARD_POLICY
    promo_pol = promotion_policy or DEFAULT_PROMOTION_POLICY

    # ── Compute incumbent scorecard ──
    incumbent_sc = compute_scorecard(resolved_samples, sc_pol)

    # ── Run each challenger ──
    comparisons: list[dict[str, Any]] = []
    for spec in challengers:
        result = _evaluate_one_challenger(
            resolved_samples, spec, inc_lp, inc_tp, sc_pol, promo_pol, incumbent_sc
        )
        comparisons.append(result.to_dict())

    # ── Build summary ──
    promote_count = sum(
        1 for c in comparisons
        if c["promotion_decision"].get("decision") == "promote"
    )
    reject_count = sum(
        1 for c in comparisons
        if c["promotion_decision"].get("decision") == "reject"
    )
    inconclusive_count = sum(
        1 for c in comparisons
        if c["promotion_decision"].get("decision") == "inconclusive"
    )

    return {
        "ok": True,
        "evaluation_type": "challenger_comparison",
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "sample_count": len(resolved_samples),
        "incumbent_scorecard": incumbent_sc,
        "incumbent_policies": {
            "label_policy": inc_lp.to_dict(),
            "training_policy": inc_tp.to_dict(),
        },
        "challengers_evaluated": len(challengers),
        "comparisons": comparisons,
        "summary": {
            "promote": promote_count,
            "reject": reject_count,
            "inconclusive": inconclusive_count,
            "best_challenger": _find_best_challenger(comparisons),
        },
        "scorecard_policy": sc_pol.to_dict(),
        "promotion_policy": promo_pol.to_dict(),
    }


# ═══════════════════════════════════════════════════════════════════
# SINGLE CHALLENGER EVALUATION
# ═══════════════════════════════════════════════════════════════════

def _evaluate_one_challenger(
    resolved_samples: list[dict],
    spec: ChallengerSpec,
    incumbent_lp: LabelPolicy,
    incumbent_tp: TrainingPolicy,
    sc_pol: ScorecardPolicy,
    promo_pol: PromotionPolicy,
    incumbent_sc: dict[str, Any],
) -> ComparisonResult:
    """Evaluate a single challenger against the incumbent."""

    challenger_lp = spec.label_policy or incumbent_lp
    has_label_change = spec.label_policy is not None
    has_training_change = spec.training_policy is not None

    # ── Label policy evaluation ──
    if has_label_change:
        # Re-label all resolved samples with challenger's label policy
        relabeled = _relabel_samples(resolved_samples, challenger_lp)
        challenger_sc = compute_scorecard(
            relabeled, sc_pol, label_override="challenger_label"
        )
    else:
        challenger_sc = incumbent_sc.copy()

    # ── Training policy composition analysis ──
    training_analysis = None
    if has_training_change:
        challenger_tp = spec.training_policy
        training_analysis = _analyze_training_composition(
            resolved_samples, incumbent_tp, challenger_tp
        )
        # Merge training analysis into challenger scorecard
        challenger_sc["training_composition"] = training_analysis

    # ── Compute deltas ──
    delta = _compute_delta(incumbent_sc, challenger_sc)

    # ── Promotion decision ──
    decision = evaluate_promotion(incumbent_sc, challenger_sc, promo_pol)

    return ComparisonResult(
        challenger_name=spec.name,
        incumbent_scorecard=incumbent_sc,
        challenger_scorecard=challenger_sc,
        delta=delta,
        promotion_decision=decision,
        sample_count=len(resolved_samples),
        provenance={
            "challenger_spec": spec.to_dict(),
            "has_label_change": has_label_change,
            "has_training_change": has_training_change,
            "evaluation_utc": datetime.now(timezone.utc).isoformat(),
        },
    )


# ═══════════════════════════════════════════════════════════════════
# RE-LABELING
# ═══════════════════════════════════════════════════════════════════

def _relabel_samples(
    samples: list[dict],
    challenger_lp: LabelPolicy,
) -> list[dict]:
    """
    Re-label resolved samples using a challenger label policy.
    Uses realized_return_bps to compute what the label WOULD have been.
    Preserves original label as 'original_label' for provenance.
    """
    relabeled = []
    for sample in samples:
        realized_bps = sample.get("realized_return_bps")
        if realized_bps is None:
            continue

        new_label = challenger_lp.assign_label(realized_bps)
        new_ambiguity = challenger_lp.compute_ambiguity(realized_bps)

        record = dict(sample)
        record["original_label"] = sample.get("actual_label", "neutral")
        record["challenger_label"] = new_label
        record["challenger_ambiguity"] = new_ambiguity
        record["challenger_label_policy_version"] = challenger_lp.version

        # Re-grade against new label
        predicted = sample.get("predicted_class", "neutral")
        probs = sample.get("predicted_probabilities", {})
        new_grade = grade_directional(predicted, new_label, probs)
        record["challenger_directional_grade"] = new_grade

        relabeled.append(record)

    return relabeled


# ═══════════════════════════════════════════════════════════════════
# TRAINING COMPOSITION ANALYSIS
# ═══════════════════════════════════════════════════════════════════

def _analyze_training_composition(
    samples: list[dict],
    incumbent_tp: TrainingPolicy,
    challenger_tp: TrainingPolicy,
) -> dict[str, Any]:
    """
    Analyze what the training batch would look like under
    incumbent vs challenger training policies.

    Does NOT retrain — only simulates batch composition.
    """
    eligible = [s for s in samples if s.get("eligible_for_training", False)]

    def _simulate_batch(tp: TrainingPolicy) -> dict:
        # Cap fresh samples
        fresh = eligible[:tp.max_fresh_samples]
        fresh_count = len(fresh)

        # Replay pool is everything else
        replay_pool = eligible[tp.max_fresh_samples:]
        replay_target = int(fresh_count * tp.replay_ratio)
        replay_count = min(replay_target, tp.replay_max_samples, len(replay_pool))

        total = fresh_count + replay_count

        # Class distribution in simulated batch
        batch = fresh + replay_pool[:replay_count]
        from collections import Counter
        class_dist = dict(Counter(s.get("actual_label", "neutral") for s in batch))

        # Ambiguity filter simulation
        low_ambiguity = sum(
            1 for s in batch
            if s.get("ambiguity", 0.0) <= tp.max_ambiguity
        )

        return {
            "fresh_count": fresh_count,
            "replay_count": replay_count,
            "total_batch": total,
            "class_distribution": class_dist,
            "low_ambiguity_count": low_ambiguity,
            "replay_ratio_effective": round(replay_count / fresh_count, 3) if fresh_count > 0 else 0.0,
            "policy_version": tp.version,
        }

    incumbent_batch = _simulate_batch(incumbent_tp)
    challenger_batch = _simulate_batch(challenger_tp)

    return {
        "eligible_samples": len(eligible),
        "incumbent_batch": incumbent_batch,
        "challenger_batch": challenger_batch,
        "delta": {
            "total_batch_diff": challenger_batch["total_batch"] - incumbent_batch["total_batch"],
            "replay_ratio_diff": round(
                challenger_batch["replay_ratio_effective"] -
                incumbent_batch["replay_ratio_effective"], 4
            ),
        },
    }


# ═══════════════════════════════════════════════════════════════════
# DELTA COMPUTATION
# ═══════════════════════════════════════════════════════════════════

def _compute_delta(
    incumbent_sc: dict[str, Any],
    challenger_sc: dict[str, Any],
) -> dict[str, Any]:
    """Compute numerical differences between two scorecards."""

    def _diff(key: str) -> float | None:
        inc_val = incumbent_sc.get(key)
        chal_val = challenger_sc.get(key)
        if inc_val is None or chal_val is None:
            return None
        try:
            return round(float(chal_val) - float(inc_val), 5)
        except (TypeError, ValueError):
            return None

    return {
        "brier_score_diff": _diff("mean_brier_score"),       # negative = challenger better
        "accuracy_diff": _diff("accuracy"),                    # positive = challenger better
        "calibration_gap_diff": _diff("mean_calibration_gap"), # negative = challenger better
        "interpretation": {
            "brier": "lower is better (negative diff = challenger improvement)",
            "accuracy": "higher is better (positive diff = challenger improvement)",
            "calibration": "lower is better (negative diff = challenger improvement)",
        },
    }


# ═══════════════════════════════════════════════════════════════════
# BEST CHALLENGER FINDER
# ═══════════════════════════════════════════════════════════════════

def _find_best_challenger(comparisons: list[dict]) -> str | None:
    """
    Among challengers that received a 'promote' decision,
    find the one with the best (lowest) Brier score improvement.
    """
    promoted = [
        c for c in comparisons
        if c["promotion_decision"].get("decision") == "promote"
    ]

    if not promoted:
        return None

    # Best = largest Brier improvement (most negative diff)
    best = min(
        promoted,
        key=lambda c: c["delta"].get("brier_score_diff") or 0.0,
    )
    return best["challenger_name"]


# ═══════════════════════════════════════════════════════════════════
# PRESET CHALLENGERS — common policy variants to test
# ═══════════════════════════════════════════════════════════════════

def build_default_challengers() -> list[ChallengerSpec]:
    """
    Return a set of sensible default challengers covering
    the primary policy dimensions: label thresholds and replay ratios.
    """
    return [
        # ── Label threshold variants ──
        ChallengerSpec(
            name="tight_labels",
            label_policy=LabelPolicy(
                version="lp_v1_tight",
                bullish_threshold_bps=75.0,
                bearish_threshold_bps=-75.0,
                ambiguity_zone_bps=20.0,
            ),
            description="Tighter ±75bps thresholds with 20bps ambiguity zone",
        ),
        ChallengerSpec(
            name="wide_labels",
            label_policy=LabelPolicy(
                version="lp_v1_wide",
                bullish_threshold_bps=150.0,
                bearish_threshold_bps=-150.0,
                ambiguity_zone_bps=40.0,
            ),
            description="Wider ±150bps thresholds with 40bps ambiguity zone",
        ),
        ChallengerSpec(
            name="asymmetric_labels",
            label_policy=LabelPolicy(
                version="lp_v1_asym",
                bullish_threshold_bps=120.0,
                bearish_threshold_bps=-80.0,
                ambiguity_zone_bps=25.0,
            ),
            description="Asymmetric thresholds: bullish +120bps, bearish -80bps",
        ),

        # ── Replay ratio variants ──
        ChallengerSpec(
            name="high_replay",
            training_policy=TrainingPolicy(
                version="tp_v1_high_replay",
                replay_ratio=0.5,
                replay_max_samples=150,
            ),
            description="Higher replay ratio (0.5) with more replay samples (150)",
        ),
        ChallengerSpec(
            name="no_replay",
            training_policy=TrainingPolicy(
                version="tp_v1_no_replay",
                replay_ratio=0.0,
                replay_max_samples=0,
            ),
            description="No replay — train only on fresh samples",
        ),
    ]
