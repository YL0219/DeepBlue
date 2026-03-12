"""
analysis.py — Orchestrate indicator computation, scoring, and output assembly.
"""

import sys
from datetime import datetime, timezone

import numpy as np
import pandas as pd

from . import parquet_loader
from . import indicators
from . import scoring


def _safe_float(v):
    """Convert numpy/pandas scalar to Python float; return None for NaN."""
    if v is None:
        return None
    if isinstance(v, (float, int)):
        return None if np.isnan(v) else round(float(v), 4)
    try:
        f = float(v)
        return None if np.isnan(f) else round(f, 4)
    except (TypeError, ValueError):
        return None


def _safe_list(series, n=20):
    """Last *n* values as a JSON-safe list."""
    if series is None:
        return []
    tail = series.tail(n)
    out = []
    for v in tail:
        f = _safe_float(v)
        out.append(f)
    return out


def run_indicators(symbol: str, timeframe: str = "1d", days: int = 0) -> dict:
    """
    Full math-indicators pipeline:
        load → compute → score → assemble output contract.
    """
    all_warnings = []

    # --- Load data ---
    df, load_warns = parquet_loader.load_ohlcv(symbol, timeframe, days)
    all_warnings.extend(load_warns)

    if df is None or df.empty:
        return {
            "ok": False,
            "domain": "math",
            "action": "indicators",
            "symbol": symbol,
            "timeframe": timeframe,
            "error": "No data available.",
            "warnings": all_warnings,
        }

    rows = len(df)

    # --- Compute indicators ---
    df, ind_warns = indicators.compute_all(df)
    all_warnings.extend(ind_warns)

    # --- Data quality ---
    data_quality = {
        "rows": rows,
        "enough_for_long_trend": rows >= 200,
        "missing_columns": [],
        "warnings": list(all_warnings),
    }
    if rows < 200:
        all_warnings.append(f"Only {rows} rows — SMA200 and long-trend signals may be unreliable.")

    # --- Snapshot (latest row) ---
    last = df.iloc[-1]
    row = {col: last[col] for col in df.columns}

    snapshot = {
        "price": _safe_float(row.get("close")),
        "sma_20": _safe_float(row.get("sma_20")),
        "sma_50": _safe_float(row.get("sma_50")),
        "sma_200": _safe_float(row.get("sma_200")),
        "ema_12": _safe_float(row.get("ema_12")),
        "ema_26": _safe_float(row.get("ema_26")),
        "rsi_14": _safe_float(row.get("rsi_14")),
        "macd": {
            "line": _safe_float(row.get("macd_line")),
            "signal": _safe_float(row.get("macd_signal")),
            "histogram": _safe_float(row.get("macd_histogram")),
        },
        "bollinger": {
            "mid": _safe_float(row.get("bb_mid")),
            "upper": _safe_float(row.get("bb_upper")),
            "lower": _safe_float(row.get("bb_lower")),
            "bandwidth": _safe_float(row.get("bb_bandwidth")),
        },
        "atr_14": _safe_float(row.get("atr_14")),
        "atr_pct": _safe_float(row.get("atr_pct")),
        "volatility_20": _safe_float(row.get("volatility_20")),
        "volume_sma_20": _safe_float(row.get("volume_sma_20")),
    }

    # --- Recent windows ---
    recent_windows = {
        "close": _safe_list(df["close"]),
        "rsi_14": _safe_list(df.get("rsi_14")),
        "macd_histogram": _safe_list(df.get("macd_histogram")),
        "atr_pct": _safe_list(df.get("atr_pct")),
    }

    # --- Factor scoring ---
    factor_warns = []
    trend = scoring.score_trend(row, factor_warns)
    momentum = scoring.score_momentum(row, factor_warns)
    volatility = scoring.score_volatility(row, factor_warns)
    participation = scoring.score_participation(row, factor_warns)
    all_warnings.extend(factor_warns)

    factor_scores = {
        "trend": trend,
        "momentum": momentum,
        "volatility": volatility,
        "participation": participation,
    }

    # --- Composite ---
    composite = scoring.compute_composite(factor_scores, all_warnings)

    # --- Conclusion ---
    bp = composite["bullish_probability"]
    bep = composite["bearish_probability"]

    if bp > bep and bp > 0.45:
        bias = "bullish"
    elif bep > bp and bep > 0.45:
        bias = "bearish"
    elif composite["confidence"] < 0.3:
        bias = "unclear"
    else:
        bias = "neutral"

    drivers = []
    for name in ("trend", "momentum", "volatility", "participation"):
        fs = factor_scores[name]
        if abs(fs["score"]) >= 0.3:
            drivers.append(f"{name}: {fs['label']} ({fs['score']:+.2f})")

    risks = []
    if not data_quality["enough_for_long_trend"]:
        risks.append("Insufficient history for long-term trend signals")
    if volatility["label"] == "high_vol":
        risks.append("Elevated volatility increases uncertainty")
    if participation["label"] == "weak":
        risks.append("Low volume participation may signal unreliable price moves")

    summary_parts = [f"{symbol} shows {bias} bias"]
    if drivers:
        summary_parts.append(f"driven by {', '.join(drivers)}")
    summary_parts.append(f"(confidence: {composite['confidence']:.0%})")

    conclusion = {
        "bias": bias,
        "summary": " ".join(summary_parts),
        "key_drivers": drivers if drivers else ["No dominant factor"],
        "risks": risks if risks else ["None identified"],
    }

    return {
        "ok": True,
        "domain": "math",
        "action": "indicators",
        "symbol": symbol,
        "timeframe": timeframe,
        "asof_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "data_quality": data_quality,
        "snapshot": snapshot,
        "recent_windows": recent_windows,
        "factor_scores": factor_scores,
        "composite": composite,
        "conclusion": conclusion,
        "warnings": all_warnings,
    }
