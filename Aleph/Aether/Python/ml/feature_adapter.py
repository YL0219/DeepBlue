"""
feature_adapter.py - Maps metabolic payload dict to a fixed-order feature vector.

The feature order is canonical and must be stable across model versions.
New features should be appended, never inserted, to maintain compatibility.
"""

import sys
import math

# Canonical feature order — append-only for backwards compatibility
FEATURE_NAMES = [
    "rsi_14",
    "macd_line",
    "macd_signal",
    "macd_histogram",
    "dist_sma_20",
    "dist_sma_50",
    "dist_sma_200",
    "atr_pct",
    "volatility_20",
    "bb_bandwidth",
    "factor_trend",
    "factor_momentum",
    "factor_volatility",
    "factor_participation",
    "composite_bullish",
    "composite_bearish",
    "composite_neutral",
    "composite_confidence",
]


def extract_features(payload: dict) -> list[float]:
    """
    Extract a fixed-order feature vector from a metabolic payload dict.
    Missing values are replaced with 0.0 (safe default for SGDClassifier).
    """
    vector = []
    for name in FEATURE_NAMES:
        val = payload.get(name)
        if val is None or (isinstance(val, float) and (math.isnan(val) or math.isinf(val))):
            vector.append(0.0)
        else:
            try:
                vector.append(float(val))
            except (ValueError, TypeError):
                vector.append(0.0)
    return vector


def feature_count() -> int:
    return len(FEATURE_NAMES)


def has_meaningful_features(payload: dict) -> bool:
    """Check if the payload has at least some non-null features for prediction."""
    meaningful = 0
    for name in FEATURE_NAMES:
        val = payload.get(name)
        if val is not None and not (isinstance(val, float) and math.isnan(val)):
            meaningful += 1
    return meaningful >= 3  # need at least a few features to be useful
