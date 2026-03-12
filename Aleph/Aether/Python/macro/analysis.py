"""
analysis.py — Orchestrate macro regime analysis: load basket → extract features → classify.
"""

import sys
from datetime import datetime, timezone

from . import parquet_loader
from .basket import DEFAULT_BASKET, SYMBOL_ROLES, compute_features
from .regime_rules import classify_regime


def run_regime(region: str = "us") -> dict:
    """
    Full macro-regime pipeline.

    Currently only 'us' region is supported (SPY/QQQ/TLT/GLD basket).
    The region parameter is kept for future extensibility.
    """
    all_warnings = []

    basket = list(DEFAULT_BASKET)

    # --- Load basket data ---
    frames, basket_status = parquet_loader.load_basket(basket)

    if not basket_status["enough_data"]:
        return {
            "ok": False,
            "domain": "macro",
            "action": "regime",
            "error": "Insufficient basket data for regime classification.",
            "basket_status": basket_status,
            "warnings": all_warnings + [f"Available: {basket_status['available_symbols']}, Missing: {basket_status['missing_symbols']}"],
        }

    # --- Extract features per symbol ---
    features_by_sym = {}
    for sym, df in frames.items():
        try:
            features_by_sym[sym] = compute_features(df)
            print(f"[macro/analysis] Features computed for {sym}", file=sys.stderr)
        except Exception as exc:
            all_warnings.append(f"Feature extraction failed for {sym}: {exc}")

    # --- Classify regime ---
    components, regime, conclusion, rule_warnings = classify_regime(features_by_sym, basket_status)
    all_warnings.extend(rule_warnings)

    return {
        "ok": True,
        "domain": "macro",
        "action": "regime",
        "asof_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "basket_status": basket_status,
        "components": components,
        "regime": regime,
        "conclusion": conclusion,
        "future_inputs": {
            "news_ready": False,
            "sentiment_ready": False,
        },
        "warnings": all_warnings,
    }
