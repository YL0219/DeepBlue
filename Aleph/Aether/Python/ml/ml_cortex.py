"""
ml_cortex.py - Main entrypoint for the ML Cortex Python brain.

Called by ml_manager.py for cortex_predict, cortex_train, cortex_status actions.
Routes to the appropriate brain subsystem and returns a normalized JSON dict.

All stdout output = exactly one JSON object.
All logs go to stderr.
"""

import json
import sys

from .feature_adapter import extract_features, has_meaningful_features
from .brain_state import load_model, save_model
from .pending_memory import (
    store_pending_sample,
    load_resolved_samples,
    pending_count as get_pending_count,
)
from .prediction_formatter import format_prediction, format_status, format_train_result


def cortex_predict(symbol: str, interval: str, horizon: str, asof_utc: str, payload: dict) -> dict:
    """
    Real-time prediction path. Always available, even cold start.

    1. Load model (or cold-start)
    2. Extract features from metabolic payload
    3. Predict
    4. Store pending sample
    5. Return formatted prediction
    """
    warnings = []

    # Load model state
    model = load_model(symbol, horizon)
    print(f"[MlCortex] Predict {symbol}/{horizon} — state={model.model_state}, samples={model.trained_samples}", file=sys.stderr)

    # Check feature quality
    if not has_meaningful_features(payload):
        warnings.append("insufficient_features")

    # Extract features
    features = extract_features(payload)

    # Predict
    result = model.predict(features)

    if model.model_state == "cold_start":
        warnings.append("cold_start_prediction")

    # Store pending sample for future label resolution
    price = payload.get("price")
    source_event_id = payload.get("source_event_id")
    pending_stored = store_pending_sample(
        symbol=symbol,
        horizon=horizon,
        features=features,
        predicted_class=result["predicted_class"],
        asof_utc=asof_utc,
        price=price,
        source_event_id=source_event_id,
    )

    return format_prediction(
        predicted_class=result["predicted_class"],
        probabilities=result["probabilities"],
        confidence=result["confidence"],
        action_tendency=result["action_tendency"],
        model_state=model.model_state,
        model_version=model.model_version,
        trained_samples=model.trained_samples,
        pending_sample_stored=pending_stored,
        training_occurred=False,
        warnings=warnings,
    )


def cortex_train(symbol: str, horizon: str, max_samples: int = 100) -> dict:
    """
    Incremental training path. Uses resolved samples to partial_fit the model.
    Should only be called during Calm/DeepWork windows.
    """
    model = load_model(symbol, horizon)
    print(f"[MlCortex] Train {symbol}/{horizon} — loading resolved samples", file=sys.stderr)

    # Load resolved (labeled) samples
    resolved = load_resolved_samples(symbol, horizon, max_samples=max_samples)

    if not resolved:
        print(f"[MlCortex] No resolved samples for {symbol}/{horizon}", file=sys.stderr)
        return format_train_result(
            symbol=symbol,
            horizon=horizon,
            samples_fitted=0,
            model_state=model.model_state,
            model_version=model.model_version,
            trained_samples=model.trained_samples,
        )

    # Extract features and labels from resolved samples
    features_batch = [s["features"] for s in resolved if "features" in s and "label" in s]
    labels = [s["label"] for s in resolved if "features" in s and "label" in s]

    if not features_batch:
        return format_train_result(
            symbol=symbol,
            horizon=horizon,
            samples_fitted=0,
            model_state=model.model_state,
            model_version=model.model_version,
            trained_samples=model.trained_samples,
        )

    # Partial fit
    fitted = model.partial_fit(features_batch, labels)
    print(f"[MlCortex] Fitted {fitted} samples for {symbol}/{horizon}", file=sys.stderr)

    # Save updated model
    save_model(symbol, horizon, model)

    return format_train_result(
        symbol=symbol,
        horizon=horizon,
        samples_fitted=fitted,
        model_state=model.model_state,
        model_version=model.model_version,
        trained_samples=model.trained_samples,
    )


def cortex_status(symbol: str, horizon: str) -> dict:
    """Get Cortex model status."""
    model = load_model(symbol, horizon)
    pc = get_pending_count(symbol, horizon)
    resolved = load_resolved_samples(symbol, horizon, max_samples=1)
    rc = len(resolved)  # lightweight — just check if any exist

    # Get a more accurate resolved count
    from .pending_memory import _resolved_path
    rp = _resolved_path(symbol, horizon)
    if rp.exists():
        try:
            with open(rp, "r") as f:
                rc = sum(1 for line in f if line.strip())
        except Exception:
            pass

    return format_status(
        symbol=symbol,
        horizon=horizon,
        model_state=model.model_state,
        model_version=model.model_version,
        trained_samples=model.trained_samples,
        pending_count=pc,
        resolved_count=rc,
    )
