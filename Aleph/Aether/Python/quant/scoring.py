"""
scoring.py — Factor scoring and composite probability generation.

Factors:
    trend       — MA alignment and price position
    momentum    — RSI + MACD signals
    volatility  — ATR% and Bollinger bandwidth regime
    participation — Volume relative to its MA

Each factor returns a dict: { score: float[-1..1], label: str, reason: str }
Score >0 = bullish lean, <0 = bearish lean, ~0 = neutral.
"""

import numpy as np


def _clamp(v, lo=-1.0, hi=1.0):
    return max(lo, min(hi, v))


# ---------------------------------------------------------------------------
# Individual factor scorers
# ---------------------------------------------------------------------------

def score_trend(row: dict, warnings: list) -> dict:
    """Trend score based on SMA alignment and price distance from MAs."""
    signals = 0
    reasons = []

    sma20 = row.get("sma_20")
    sma50 = row.get("sma_50")
    sma200 = row.get("sma_200")
    price = row.get("close")

    has_long = sma200 is not None and not np.isnan(sma200)

    # Price vs. SMAs
    if sma20 is not None and not np.isnan(sma20):
        if price > sma20:
            signals += 1
            reasons.append("price > SMA20")
        else:
            signals -= 1
            reasons.append("price < SMA20")

    if sma50 is not None and not np.isnan(sma50):
        if price > sma50:
            signals += 1
            reasons.append("price > SMA50")
        else:
            signals -= 1
            reasons.append("price < SMA50")

    if has_long:
        if price > sma200:
            signals += 1
            reasons.append("price > SMA200")
        else:
            signals -= 1
            reasons.append("price < SMA200")

        # Golden/Death cross
        if sma50 is not None and not np.isnan(sma50):
            if sma50 > sma200:
                signals += 1
                reasons.append("SMA50 > SMA200 (golden cross zone)")
            else:
                signals -= 1
                reasons.append("SMA50 < SMA200 (death cross zone)")
    else:
        warnings.append("SMA200 unavailable — trend confidence reduced")

    max_signals = 4 if has_long else 2
    raw = signals / max(max_signals, 1)
    score = _clamp(raw)

    if score > 0.25:
        label = "bullish"
    elif score < -0.25:
        label = "bearish"
    else:
        label = "neutral"

    return {"score": round(score, 4), "label": label, "reason": "; ".join(reasons)}


def score_momentum(row: dict, warnings: list) -> dict:
    """Momentum score from RSI and MACD."""
    signals = 0
    reasons = []

    rsi = row.get("rsi_14")
    macd_hist = row.get("macd_histogram")
    macd_line = row.get("macd_line")
    macd_signal = row.get("macd_signal")

    if rsi is not None and not np.isnan(rsi):
        if rsi > 70:
            signals -= 1
            reasons.append(f"RSI {rsi:.1f} overbought")
        elif rsi > 55:
            signals += 1
            reasons.append(f"RSI {rsi:.1f} bullish")
        elif rsi < 30:
            signals += 1
            reasons.append(f"RSI {rsi:.1f} oversold (contrarian bullish)")
        elif rsi < 45:
            signals -= 1
            reasons.append(f"RSI {rsi:.1f} bearish")
        else:
            reasons.append(f"RSI {rsi:.1f} neutral")
    else:
        warnings.append("RSI unavailable")

    if macd_hist is not None and not np.isnan(macd_hist):
        if macd_hist > 0:
            signals += 1
            reasons.append("MACD histogram positive")
        else:
            signals -= 1
            reasons.append("MACD histogram negative")

    if macd_line is not None and macd_signal is not None:
        if not np.isnan(macd_line) and not np.isnan(macd_signal):
            if macd_line > macd_signal:
                signals += 1
                reasons.append("MACD line > signal")
            else:
                signals -= 1
                reasons.append("MACD line < signal")

    max_signals = 3
    raw = signals / max_signals
    score = _clamp(raw)

    if score > 0.2:
        label = "bullish"
    elif score < -0.2:
        label = "bearish"
    else:
        label = "neutral"

    return {"score": round(score, 4), "label": label, "reason": "; ".join(reasons)}


def score_volatility(row: dict, warnings: list) -> dict:
    """Volatility assessment from ATR% and Bollinger bandwidth."""
    reasons = []
    score = 0.0

    atr_pct = row.get("atr_pct")
    bb_bw = row.get("bb_bandwidth")
    vol20 = row.get("volatility_20")

    parts = 0

    if atr_pct is not None and not np.isnan(atr_pct):
        parts += 1
        if atr_pct > 0.04:
            score -= 0.3
            reasons.append(f"ATR% {atr_pct:.3f} elevated")
        elif atr_pct > 0.025:
            reasons.append(f"ATR% {atr_pct:.3f} moderate")
        else:
            score += 0.2
            reasons.append(f"ATR% {atr_pct:.3f} low")

    if bb_bw is not None and not np.isnan(bb_bw):
        parts += 1
        if bb_bw > 0.15:
            score -= 0.3
            reasons.append(f"BB bandwidth {bb_bw:.3f} wide")
        elif bb_bw < 0.05:
            score += 0.1
            reasons.append(f"BB bandwidth {bb_bw:.3f} tight (squeeze potential)")
        else:
            reasons.append(f"BB bandwidth {bb_bw:.3f} normal")

    if vol20 is not None and not np.isnan(vol20):
        parts += 1
        if vol20 > 0.40:
            score -= 0.3
            reasons.append(f"Annualized vol {vol20:.2f} high")
        elif vol20 < 0.15:
            score += 0.2
            reasons.append(f"Annualized vol {vol20:.2f} low")
        else:
            reasons.append(f"Annualized vol {vol20:.2f} moderate")

    if parts == 0:
        warnings.append("No volatility metrics available")

    score = _clamp(score)

    if score > 0.1:
        label = "low_vol"
    elif score < -0.1:
        label = "high_vol"
    else:
        label = "moderate"

    return {"score": round(score, 4), "label": label, "reason": "; ".join(reasons)}


def score_participation(row: dict, warnings: list) -> dict:
    """Volume participation score — is volume confirming the move?"""
    reasons = []
    score = 0.0

    volume = row.get("volume")
    vol_sma = row.get("volume_sma_20")
    price = row.get("close")
    sma20 = row.get("sma_20")

    if volume is None or vol_sma is None or np.isnan(volume) or np.isnan(vol_sma) or vol_sma == 0:
        warnings.append("Volume data unavailable for participation scoring")
        return {"score": 0.0, "label": "unknown", "reason": "Insufficient volume data"}

    vol_ratio = volume / vol_sma
    reasons.append(f"Volume ratio to 20-SMA: {vol_ratio:.2f}")

    if vol_ratio > 1.5:
        # High volume — direction matters
        if price is not None and sma20 is not None and not np.isnan(sma20):
            if price > sma20:
                score = 0.6
                reasons.append("High volume + price above SMA20 (strong buying)")
            else:
                score = -0.6
                reasons.append("High volume + price below SMA20 (distribution)")
        else:
            score = 0.2
            reasons.append("High volume, direction unclear")
    elif vol_ratio > 1.0:
        score = 0.1
        reasons.append("Volume slightly above average")
    elif vol_ratio > 0.5:
        score = -0.1
        reasons.append("Below-average volume")
    else:
        score = -0.3
        reasons.append("Very low volume — weak participation")

    score = _clamp(score)

    if score > 0.2:
        label = "strong"
    elif score < -0.2:
        label = "weak"
    else:
        label = "normal"

    return {"score": round(score, 4), "label": label, "reason": "; ".join(reasons)}


# ---------------------------------------------------------------------------
# Composite probability
# ---------------------------------------------------------------------------

FACTOR_WEIGHTS = {
    "trend": 0.35,
    "momentum": 0.30,
    "volatility": 0.15,
    "participation": 0.20,
}


def compute_composite(factor_scores: dict, warnings: list) -> dict:
    """
    Combine factor scores [-1..1] into a probability triple that sums to 1.0.

    Approach:
        weighted_score in [-1, 1] → map to (bullish, neutral, bearish) simplex.
    """
    weighted = 0.0
    weight_sum = 0.0

    for name, weight in FACTOR_WEIGHTS.items():
        fs = factor_scores.get(name)
        if fs is None:
            continue
        s = fs.get("score", 0.0)
        if s is None or np.isnan(s):
            continue
        weighted += s * weight
        weight_sum += weight

    if weight_sum == 0:
        return {
            "bullish_probability": 0.33,
            "bearish_probability": 0.33,
            "neutral_probability": 0.34,
            "confidence": 0.0,
        }

    composite = weighted / weight_sum  # in [-1, 1]

    # Map to simplex: score → (bull, neutral, bear)
    # At composite=+1: bull=0.80, neutral=0.15, bear=0.05
    # At composite=-1: bull=0.05, neutral=0.15, bear=0.80
    # At composite= 0: bull=0.30, neutral=0.40, bear=0.30
    abs_c = abs(composite)

    neutral_base = 0.40
    neutral_prob = max(0.10, neutral_base * (1.0 - abs_c))

    remaining = 1.0 - neutral_prob
    if composite > 0:
        bull_share = 0.5 + 0.5 * composite
    else:
        bull_share = 0.5 + 0.5 * composite  # composite is negative, so this < 0.5

    bull_prob = remaining * bull_share
    bear_prob = remaining * (1.0 - bull_share)

    # Normalize to exactly 1.0
    total = bull_prob + bear_prob + neutral_prob
    bull_prob /= total
    bear_prob /= total
    neutral_prob /= total

    # Confidence: based on how decisive the factors are + weight coverage
    coverage = weight_sum / sum(FACTOR_WEIGHTS.values())
    confidence = abs_c * 0.7 + coverage * 0.3
    confidence = min(1.0, confidence)

    return {
        "bullish_probability": round(bull_prob, 4),
        "bearish_probability": round(bear_prob, 4),
        "neutral_probability": round(neutral_prob, 4),
        "confidence": round(confidence, 4),
    }
