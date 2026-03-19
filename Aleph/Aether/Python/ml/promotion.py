"""
promotion.py — Promotion rules for challenger evaluation.

Defines strict, configurable, policy-driven promotion decisions.
A challenger must earn promotion through sufficient evidence —
not just brief superiority.

Philosophy:
  - Brier score is the primary promotion metric (probability quality)
  - Accuracy is secondary support (directional hit rate)
  - Class-balance stability acts as a veto-style safeguard
  - Sample sufficiency must be met
  - Inconclusive is a valid and common outcome
  - Promotion happens at the policy scope, not globally

Decision space: PROMOTE | REJECT | INCONCLUSIVE
"""

from __future__ import annotations

from dataclasses import dataclass, asdict
from typing import Any


# ═══════════════════════════════════════════════════════════════════
# PROMOTION POLICY
# ═══════════════════════════════════════════════════════════════════

@dataclass(frozen=True)
class PromotionPolicy:
    """
    Configurable rules governing promotion decisions.

    All thresholds are policy-driven with sensible defaults.
    Future evolution can change these without code changes.
    """
    version: str = "pp_v1"

    # ── Sample sufficiency ──
    min_samples: int = 30              # minimum resolved samples for valid comparison
    min_samples_hard_floor: int = 10   # below this, always inconclusive

    # ── Primary metric: Brier score (lower = better) ──
    min_brier_improvement: float = 0.02    # challenger must beat incumbent by >= this
    max_brier_regression: float = 0.01     # challenger can't be worse by more than this

    # ── Secondary metric: Accuracy (higher = better) ──
    max_accuracy_regression: float = 0.05  # can't lose more than 5% accuracy
    min_accuracy_for_promotion: float = 0.25  # absolute floor — don't promote if below

    # ── Calibration guard ──
    max_calibration_gap_for_promotion: float = 0.30  # absolute calibration gap ceiling

    # ── Class stability safeguards ──
    require_no_class_collapse: bool = True
    class_collapse_veto_threshold: float = 0.03  # any class below 3% in predictions = veto

    # ── Drift veto ──
    veto_on_drift: bool = True  # if challenger shows drift, block promotion

    # ── Warning ceiling ──
    max_warnings_for_promotion: int = 3  # challenger can't have more than N warnings

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> PromotionPolicy:
        known = {f.name for f in cls.__dataclass_fields__.values()}
        return cls(**{k: v for k, v in d.items() if k in known})


DEFAULT_PROMOTION_POLICY = PromotionPolicy()


# ═══════════════════════════════════════════════════════════════════
# PROMOTION DECISION
# ═══════════════════════════════════════════════════════════════════

PROMOTE = "promote"
REJECT = "reject"
INCONCLUSIVE = "inconclusive"


def evaluate_promotion(
    incumbent_scorecard: dict[str, Any],
    challenger_scorecard: dict[str, Any],
    policy: PromotionPolicy | None = None,
) -> dict[str, Any]:
    """
    Evaluate whether a challenger should be promoted over the incumbent.

    Returns a decision dict with:
      - decision: "promote" | "reject" | "inconclusive"
      - reasons: list of human-readable justification strings
      - vetoes: list of veto conditions triggered
      - evidence: dict of supporting metric comparisons
      - policy_version: which promotion policy was used
    """
    pol = policy or DEFAULT_PROMOTION_POLICY
    reasons: list[str] = []
    vetoes: list[str] = []
    evidence: dict[str, Any] = {}

    inc_status = incumbent_scorecard.get("status", "unknown")
    chal_status = challenger_scorecard.get("status", "unknown")

    # ═══════════════════════════════════════════════════
    # GATE 1: Data sufficiency
    # ═══════════════════════════════════════════════════

    inc_n = incumbent_scorecard.get("sample_count", 0)
    chal_n = challenger_scorecard.get("sample_count", 0)
    evidence["incumbent_samples"] = inc_n
    evidence["challenger_samples"] = chal_n

    if inc_n < pol.min_samples_hard_floor or chal_n < pol.min_samples_hard_floor:
        return _decision(INCONCLUSIVE, ["insufficient_data_hard_floor"], vetoes, evidence, pol)

    if inc_status == "insufficient_data" or chal_status == "insufficient_data":
        return _decision(INCONCLUSIVE, ["scorecard_insufficient_data"], vetoes, evidence, pol)

    if inc_n < pol.min_samples or chal_n < pol.min_samples:
        reasons.append(f"sample_count_below_minimum({min(inc_n, chal_n)}<{pol.min_samples})")
        return _decision(INCONCLUSIVE, reasons, vetoes, evidence, pol)

    # ═══════════════════════════════════════════════════
    # GATE 2: Extract metrics
    # ═══════════════════════════════════════════════════

    inc_brier = incumbent_scorecard.get("mean_brier_score")
    chal_brier = challenger_scorecard.get("mean_brier_score")
    inc_acc = incumbent_scorecard.get("accuracy")
    chal_acc = challenger_scorecard.get("accuracy")
    inc_cal = incumbent_scorecard.get("mean_calibration_gap")
    chal_cal = challenger_scorecard.get("mean_calibration_gap")

    if any(v is None for v in (inc_brier, chal_brier, inc_acc, chal_acc)):
        return _decision(INCONCLUSIVE, ["missing_core_metrics"], vetoes, evidence, pol)

    brier_diff = chal_brier - inc_brier   # negative = challenger better
    acc_diff = chal_acc - inc_acc         # positive = challenger better

    evidence["incumbent_brier"] = round(inc_brier, 5)
    evidence["challenger_brier"] = round(chal_brier, 5)
    evidence["brier_improvement"] = round(-brier_diff, 5)  # positive = good
    evidence["incumbent_accuracy"] = round(inc_acc, 4)
    evidence["challenger_accuracy"] = round(chal_acc, 4)
    evidence["accuracy_change"] = round(acc_diff, 4)

    if inc_cal is not None and chal_cal is not None:
        evidence["incumbent_calibration_gap"] = round(inc_cal, 5)
        evidence["challenger_calibration_gap"] = round(chal_cal, 5)

    # ═══════════════════════════════════════════════════
    # GATE 3: Veto checks (hard blocks)
    # ═══════════════════════════════════════════════════

    # Veto: class collapse in challenger predictions
    if pol.require_no_class_collapse:
        chal_pred_dist = challenger_scorecard.get("predicted_class_distribution", {})
        total_preds = sum(chal_pred_dist.values()) if chal_pred_dist else 0
        if total_preds > 0:
            for cls in ("bullish", "neutral", "bearish"):
                cls_frac = chal_pred_dist.get(cls, 0) / total_preds
                if cls_frac < pol.class_collapse_veto_threshold:
                    vetoes.append(
                        f"class_collapse:{cls}({cls_frac:.1%}<{pol.class_collapse_veto_threshold:.1%})"
                    )

    # Veto: drift detected in challenger
    if pol.veto_on_drift:
        chal_drift = challenger_scorecard.get("drift", {})
        if chal_drift.get("detected", False):
            drift_flags = chal_drift.get("flags", [])
            vetoes.append(f"challenger_drift_detected:{','.join(drift_flags)}")

    # Veto: too many warnings
    chal_warnings = challenger_scorecard.get("warning_count", 0)
    if chal_warnings > pol.max_warnings_for_promotion:
        vetoes.append(f"excessive_warnings:{chal_warnings}>{pol.max_warnings_for_promotion}")

    # Veto: absolute accuracy floor
    if chal_acc < pol.min_accuracy_for_promotion:
        vetoes.append(f"accuracy_below_floor:{chal_acc:.3f}<{pol.min_accuracy_for_promotion}")

    # Veto: calibration ceiling
    if chal_cal is not None and chal_cal > pol.max_calibration_gap_for_promotion:
        vetoes.append(
            f"calibration_gap_too_high:{chal_cal:.3f}>{pol.max_calibration_gap_for_promotion}"
        )

    # If any vetoes triggered, reject
    if vetoes:
        reasons.append("veto_conditions_triggered")
        return _decision(REJECT, reasons, vetoes, evidence, pol)

    # ═══════════════════════════════════════════════════
    # GATE 4: Primary metric — Brier score
    # ═══════════════════════════════════════════════════

    brier_improved = -brier_diff >= pol.min_brier_improvement
    brier_regressed = brier_diff > pol.max_brier_regression

    if brier_regressed:
        reasons.append(f"brier_regression:{brier_diff:+.4f}")
        return _decision(REJECT, reasons, vetoes, evidence, pol)

    if not brier_improved:
        # Brier didn't improve enough — inconclusive
        reasons.append(
            f"brier_improvement_insufficient:{-brier_diff:.4f}<{pol.min_brier_improvement}"
        )
        return _decision(INCONCLUSIVE, reasons, vetoes, evidence, pol)

    # ═══════════════════════════════════════════════════
    # GATE 5: Secondary metric — Accuracy guard
    # ═══════════════════════════════════════════════════

    if acc_diff < -pol.max_accuracy_regression:
        reasons.append(f"accuracy_regression:{acc_diff:+.4f}")
        return _decision(REJECT, reasons, vetoes, evidence, pol)

    # ═══════════════════════════════════════════════════
    # GATE 6: All gates passed — promote
    # ═══════════════════════════════════════════════════

    reasons.append(f"brier_improved_by:{-brier_diff:.4f}")
    if acc_diff >= 0:
        reasons.append(f"accuracy_also_improved:{acc_diff:+.4f}")
    else:
        reasons.append(f"accuracy_regressed_within_tolerance:{acc_diff:+.4f}")

    return _decision(PROMOTE, reasons, vetoes, evidence, pol)


# ═══════════════════════════════════════════════════════════════════
# DECISION BUILDER
# ═══════════════════════════════════════════════════════════════════

def _decision(
    decision: str,
    reasons: list[str],
    vetoes: list[str],
    evidence: dict[str, Any],
    policy: PromotionPolicy,
) -> dict[str, Any]:
    """Build a structured promotion decision dict."""
    return {
        "decision": decision,
        "reasons": reasons,
        "vetoes": vetoes,
        "evidence": evidence,
        "policy_version": policy.version,
    }
