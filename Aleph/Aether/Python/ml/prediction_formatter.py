"""
prediction_formatter.py - Normalized output contract for the Python brain.

All predictions from ml_cortex.py are formatted through this module to ensure
a stable JSON contract with the C# MlCortexService consumer.
"""


def format_prediction(
    predicted_class: str,
    probabilities: dict,
    confidence: float,
    action_tendency: float,
    model_state: str,
    model_version: str,
    trained_samples: int,
    pending_sample_stored: bool = False,
    training_occurred: bool = False,
    warnings: list[str] | None = None,
) -> dict:
    """Build the normalized prediction output dict."""
    return {
        "ok": True,
        "domain": "ml",
        "action": "cortex_predict",
        "predicted_class": predicted_class,
        "probabilities": {
            "bullish": round(probabilities.get("bullish", 0.333), 4),
            "neutral": round(probabilities.get("neutral", 0.334), 4),
            "bearish": round(probabilities.get("bearish", 0.333), 4),
        },
        "confidence": round(confidence, 4),
        "action_tendency": round(action_tendency, 4),
        "model_state": model_state,
        "model_version": model_version,
        "trained_samples": trained_samples,
        "pending_sample_stored": pending_sample_stored,
        "training_occurred": training_occurred,
        "warnings": warnings or [],
    }


def format_status(
    symbol: str,
    horizon: str,
    model_state: str,
    model_version: str,
    trained_samples: int,
    pending_count: int,
    resolved_count: int,
) -> dict:
    """Build the normalized status output dict."""
    return {
        "ok": True,
        "domain": "ml",
        "action": "cortex_status",
        "symbol": symbol,
        "horizon": horizon,
        "model_state": model_state,
        "model_version": model_version,
        "trained_samples": trained_samples,
        "pending_count": pending_count,
        "resolved_count": resolved_count,
    }


def format_train_result(
    symbol: str,
    horizon: str,
    samples_fitted: int,
    model_state: str,
    model_version: str,
    trained_samples: int,
) -> dict:
    """Build the normalized training result output dict."""
    return {
        "ok": True,
        "domain": "ml",
        "action": "cortex_train",
        "symbol": symbol,
        "horizon": horizon,
        "samples_fitted": samples_fitted,
        "model_state": model_state,
        "model_version": model_version,
        "trained_samples": trained_samples,
    }
