"""
regime_rules.py — Deterministic rule-based regime classification from basket features.

Components scored:
    equities    — SPY + QQQ trend/momentum → risk_on / risk_off
    duration    — TLT trend → flight-to-safety signal
    defensive   — GLD trend → hedging / inflation signal
    cross_asset — relative strength across the basket
"""

import numpy as np


def _score_equity_component(features_by_sym: dict, warnings: list) -> dict:
    """Score equities component from SPY and QQQ features."""
    signals = 0
    reasons = []
    count = 0

    for sym in ("SPY", "QQQ"):
        f = features_by_sym.get(sym)
        if f is None:
            continue
        count += 1

        if f.get("above_sma_short") is True:
            signals += 1
            reasons.append(f"{sym} above short MA")
        elif f.get("above_sma_short") is False:
            signals -= 1
            reasons.append(f"{sym} below short MA")

        if f.get("above_sma_long") is True:
            signals += 1
            reasons.append(f"{sym} above long MA")
        elif f.get("above_sma_long") is False:
            signals -= 1
            reasons.append(f"{sym} below long MA")

        ret = f.get("return_short")
        if ret is not None:
            if ret > 0.02:
                signals += 1
                reasons.append(f"{sym} short return +{ret:.1%}")
            elif ret < -0.02:
                signals -= 1
                reasons.append(f"{sym} short return {ret:.1%}")

    if count == 0:
        warnings.append("No equity data for regime classification")
        return {"signal": "unclear", "score": 0.0, "reason": "No equity data"}

    max_possible = count * 3  # 3 checks per symbol
    raw = signals / max(max_possible, 1)
    score = max(-1.0, min(1.0, raw))

    if score > 0.3:
        signal = "risk_on"
    elif score < -0.3:
        signal = "risk_off"
    else:
        signal = "mixed"

    return {"signal": signal, "score": round(score, 4), "reason": "; ".join(reasons) or "No signals"}


def _score_duration_component(features_by_sym: dict, warnings: list) -> dict:
    """Score duration/bonds from TLT."""
    f = features_by_sym.get("TLT")
    if f is None:
        warnings.append("TLT data missing — duration signal unavailable")
        return {"signal": "unclear", "score": 0.0, "reason": "TLT data missing"}

    signals = 0
    reasons = []

    # Rising TLT = flight to safety = risk_off signal
    if f.get("above_sma_short") is True:
        signals += 1
        reasons.append("TLT above short MA (bonds bid)")
    elif f.get("above_sma_short") is False:
        signals -= 1
        reasons.append("TLT below short MA (bonds offered)")

    if f.get("above_sma_long") is True:
        signals += 1
        reasons.append("TLT above long MA")
    elif f.get("above_sma_long") is False:
        signals -= 1
        reasons.append("TLT below long MA")

    ret = f.get("return_short")
    if ret is not None:
        if ret > 0.01:
            signals += 1
            reasons.append(f"TLT short return +{ret:.1%}")
        elif ret < -0.01:
            signals -= 1
            reasons.append(f"TLT short return {ret:.1%}")

    raw = signals / 3.0
    score = max(-1.0, min(1.0, raw))

    # For duration: positive = bonds rising = risk_off
    if score > 0.3:
        signal = "risk_off"
    elif score < -0.3:
        signal = "risk_on"
    else:
        signal = "mixed"

    return {"signal": signal, "score": round(score, 4), "reason": "; ".join(reasons) or "No signals"}


def _score_defensive_component(features_by_sym: dict, warnings: list) -> dict:
    """Score defensive/gold from GLD."""
    f = features_by_sym.get("GLD")
    if f is None:
        warnings.append("GLD data missing — defensive signal unavailable")
        return {"signal": "unclear", "score": 0.0, "reason": "GLD data missing"}

    signals = 0
    reasons = []

    # Rising GLD = defensive positioning = risk_off tilt
    if f.get("above_sma_short") is True:
        signals += 1
        reasons.append("GLD above short MA (gold bid)")
    elif f.get("above_sma_short") is False:
        signals -= 1
        reasons.append("GLD below short MA")

    if f.get("above_sma_long") is True:
        signals += 1
        reasons.append("GLD above long MA")
    elif f.get("above_sma_long") is False:
        signals -= 1
        reasons.append("GLD below long MA")

    ret = f.get("return_short")
    if ret is not None:
        if ret > 0.01:
            signals += 1
            reasons.append(f"GLD short return +{ret:.1%}")
        elif ret < -0.01:
            signals -= 1
            reasons.append(f"GLD short return {ret:.1%}")

    raw = signals / 3.0
    score = max(-1.0, min(1.0, raw))

    if score > 0.3:
        signal = "risk_off"
    elif score < -0.3:
        signal = "risk_on"
    else:
        signal = "mixed"

    return {"signal": signal, "score": round(score, 4), "reason": "; ".join(reasons) or "No signals"}


def _score_cross_asset(features_by_sym: dict, warnings: list) -> dict:
    """
    Cross-asset relative strength:
        - Equities up + bonds down + gold down → strong risk_on
        - Equities down + bonds up + gold up → strong risk_off
    """
    eq_ret = []
    for sym in ("SPY", "QQQ"):
        f = features_by_sym.get(sym)
        if f and f.get("return_short") is not None:
            eq_ret.append(f["return_short"])

    safe_ret = []
    for sym in ("TLT", "GLD"):
        f = features_by_sym.get(sym)
        if f and f.get("return_short") is not None:
            safe_ret.append(f["return_short"])

    if not eq_ret or not safe_ret:
        warnings.append("Insufficient data for cross-asset signal")
        return {"signal": "unclear", "score": 0.0, "reason": "Insufficient cross-asset data"}

    avg_eq = sum(eq_ret) / len(eq_ret)
    avg_safe = sum(safe_ret) / len(safe_ret)

    # Spread: equities outperforming safe havens = risk_on
    spread = avg_eq - avg_safe
    reasons = [f"Equity avg return {avg_eq:+.2%}, safe-haven avg {avg_safe:+.2%}, spread {spread:+.2%}"]

    # Normalize spread to a score
    score = max(-1.0, min(1.0, spread * 10.0))  # 10% spread → ±1.0

    if score > 0.3:
        signal = "risk_on"
    elif score < -0.3:
        signal = "risk_off"
    else:
        signal = "mixed"

    return {"signal": signal, "score": round(score, 4), "reason": "; ".join(reasons)}


# ---------------------------------------------------------------------------
# Component weights for regime composition
# ---------------------------------------------------------------------------
COMPONENT_WEIGHTS = {
    "equities": 0.40,
    "duration": 0.20,
    "defensive": 0.15,
    "cross_asset": 0.25,
}


def classify_regime(features_by_sym: dict, basket_status: dict) -> tuple:
    """
    Run all component scorers and produce a regime classification.

    Returns:
        (components_dict, regime_dict, conclusion_dict, warnings_list)
    """
    warnings = []

    equities = _score_equity_component(features_by_sym, warnings)
    duration = _score_duration_component(features_by_sym, warnings)
    defensive = _score_defensive_component(features_by_sym, warnings)
    cross_asset = _score_cross_asset(features_by_sym, warnings)

    components = {
        "equities": equities,
        "duration": duration,
        "defensive": defensive,
        "cross_asset": cross_asset,
    }

    # --- Composite regime score ---
    # Positive = risk_on, Negative = risk_off
    # Equities score is already positive=risk_on
    # Duration/defensive are inverted: their positive = risk_off
    score_map = {
        "equities": equities["score"],
        "duration": -duration["score"],   # Invert: bonds up = risk_off
        "defensive": -defensive["score"], # Invert: gold up = risk_off
        "cross_asset": cross_asset["score"],
    }

    weighted = 0.0
    weight_sum = 0.0
    for comp, weight in COMPONENT_WEIGHTS.items():
        s = score_map.get(comp, 0.0)
        if components[comp]["signal"] == "unclear":
            continue
        weighted += s * weight
        weight_sum += weight

    if weight_sum == 0:
        regime_score = 0.0
    else:
        regime_score = weighted / weight_sum

    # Map to probabilities
    abs_s = abs(regime_score)
    mixed_base = 0.40
    mixed_prob = max(0.10, mixed_base * (1.0 - abs_s))
    remaining = 1.0 - mixed_prob

    if regime_score > 0:
        risk_on_share = 0.5 + 0.5 * regime_score
    else:
        risk_on_share = 0.5 + 0.5 * regime_score

    risk_on_prob = remaining * risk_on_share
    risk_off_prob = remaining * (1.0 - risk_on_share)

    total = risk_on_prob + risk_off_prob + mixed_prob
    risk_on_prob /= total
    risk_off_prob /= total
    mixed_prob /= total

    # Confidence
    available_ratio = len(basket_status.get("available_symbols", [])) / max(len(basket_status.get("required_symbols", [])), 1)
    confidence = abs_s * 0.6 + available_ratio * 0.4
    confidence = min(1.0, confidence)

    if risk_on_prob > risk_off_prob and risk_on_prob > 0.45:
        label = "risk_on"
    elif risk_off_prob > risk_on_prob and risk_off_prob > 0.45:
        label = "risk_off"
    else:
        label = "mixed"

    if confidence < 0.3:
        label = "unclear"

    regime = {
        "label": label,
        "risk_on_probability": round(risk_on_prob, 4),
        "risk_off_probability": round(risk_off_prob, 4),
        "mixed_probability": round(mixed_prob, 4),
        "confidence": round(confidence, 4),
    }

    # --- Conclusion ---
    drivers = []
    for comp_name, comp_data in components.items():
        if comp_data["signal"] != "unclear" and abs(comp_data["score"]) >= 0.3:
            drivers.append(f"{comp_name}: {comp_data['signal']} ({comp_data['score']:+.2f})")

    caveats = []
    if basket_status.get("missing_symbols"):
        caveats.append(f"Missing: {', '.join(basket_status['missing_symbols'])}")
    if confidence < 0.5:
        caveats.append("Low confidence — limited data or mixed signals")

    summary_parts = [f"Market regime: {label}"]
    if drivers:
        summary_parts.append(f"Key: {'; '.join(drivers)}")
    summary_parts.append(f"(confidence: {confidence:.0%})")

    conclusion = {
        "summary": " | ".join(summary_parts),
        "drivers": drivers if drivers else ["No dominant signal"],
        "caveats": caveats if caveats else ["None"],
    }

    return components, regime, conclusion, warnings
