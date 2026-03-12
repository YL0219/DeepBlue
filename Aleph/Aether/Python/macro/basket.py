"""
basket.py — Macro regime basket definitions and per-symbol feature extraction.
"""

import numpy as np
import pandas as pd

# Foundational regime basket
DEFAULT_BASKET = ["SPY", "QQQ", "TLT", "GLD"]

# Role mapping for regime interpretation
SYMBOL_ROLES = {
    "SPY": "equities",
    "QQQ": "equities",
    "TLT": "duration",
    "GLD": "defensive",
}


def compute_features(df: pd.DataFrame, lookback_short: int = 20, lookback_long: int = 50) -> dict:
    """
    Extract regime-relevant features from a single-symbol OHLCV DataFrame.

    Returns dict with trend, momentum, and relative-strength metrics.
    """
    close = df["close"]
    n = len(close)

    features = {}

    # Current price
    features["price"] = float(close.iloc[-1])

    # SMA trend
    if n >= lookback_long:
        sma_short = close.rolling(lookback_short).mean().iloc[-1]
        sma_long = close.rolling(lookback_long).mean().iloc[-1]
        features["sma_short"] = float(sma_short)
        features["sma_long"] = float(sma_long)
        features["above_sma_short"] = bool(close.iloc[-1] > sma_short)
        features["above_sma_long"] = bool(close.iloc[-1] > sma_long)
        features["sma_cross"] = float(sma_short - sma_long)
    elif n >= lookback_short:
        sma_short = close.rolling(lookback_short).mean().iloc[-1]
        features["sma_short"] = float(sma_short)
        features["sma_long"] = None
        features["above_sma_short"] = bool(close.iloc[-1] > sma_short)
        features["above_sma_long"] = None
        features["sma_cross"] = None
    else:
        features["sma_short"] = None
        features["sma_long"] = None
        features["above_sma_short"] = None
        features["above_sma_long"] = None
        features["sma_cross"] = None

    # Returns
    if n >= lookback_short + 1:
        ret_short = float((close.iloc[-1] / close.iloc[-lookback_short - 1]) - 1.0)
        features["return_short"] = round(ret_short, 6)
    else:
        features["return_short"] = None

    if n >= lookback_long + 1:
        ret_long = float((close.iloc[-1] / close.iloc[-lookback_long - 1]) - 1.0)
        features["return_long"] = round(ret_long, 6)
    else:
        features["return_long"] = None

    # Rolling volatility
    if n >= lookback_short + 1:
        log_ret = np.log(close / close.shift(1)).dropna()
        vol = float(log_ret.tail(lookback_short).std() * np.sqrt(252))
        features["volatility"] = round(vol, 4)
    else:
        features["volatility"] = None

    return features
