"""
brain_state.py - Load/save model, scaler, and metadata to the file-based data lake.

Storage layout:
  data_lake/cortex/models/{symbol}/{horizon}/model.pkl
  data_lake/cortex/models/{symbol}/{horizon}/scaler.pkl
  data_lake/cortex/state/{symbol}/{horizon}/metadata.json

All heavy blobs live in files, not SQLite.
"""

import json
import os
import pickle
import sys
from pathlib import Path

from .incremental_model import IncrementalCortexModel


def _cortex_root() -> Path:
    """Resolve the cortex data lake root relative to the Aleph content root."""
    # Walk up from this file to find the Aleph project root
    # This file is at: Aleph/Aether/Python/ml/brain_state.py
    # Content root is: Aleph/
    ml_dir = Path(__file__).parent
    python_dir = ml_dir.parent
    aether_dir = python_dir.parent
    content_root = aether_dir.parent
    return content_root / "data_lake" / "cortex"


def _model_dir(symbol: str, horizon: str) -> Path:
    return _cortex_root() / "models" / symbol.upper() / horizon


def _state_dir(symbol: str, horizon: str) -> Path:
    return _cortex_root() / "state" / symbol.upper() / horizon


def load_model(symbol: str, horizon: str) -> IncrementalCortexModel:
    """Load persisted model + scaler, or return a fresh cold-start model."""
    model = IncrementalCortexModel()

    model_path = _model_dir(symbol, horizon) / "model.pkl"
    scaler_path = _model_dir(symbol, horizon) / "scaler.pkl"
    meta_path = _state_dir(symbol, horizon) / "metadata.json"

    if model_path.exists():
        try:
            with open(model_path, "rb") as f:
                model.model = pickle.load(f)
            model._fitted = True
            print(f"[MlCortex] Loaded model from {model_path}", file=sys.stderr)
        except Exception as ex:
            print(f"[MlCortex] Failed to load model: {ex}", file=sys.stderr)

    if scaler_path.exists():
        try:
            with open(scaler_path, "rb") as f:
                model.scaler = pickle.load(f)
            model._scaler_fitted = True
        except Exception as ex:
            print(f"[MlCortex] Failed to load scaler: {ex}", file=sys.stderr)

    if meta_path.exists():
        try:
            with open(meta_path, "r") as f:
                meta = json.load(f)
            model.trained_samples = meta.get("trained_samples", 0)
            model.model_version = meta.get("model_version", "v1.0.0")
        except Exception as ex:
            print(f"[MlCortex] Failed to load metadata: {ex}", file=sys.stderr)

    return model


def save_model(symbol: str, horizon: str, model: IncrementalCortexModel) -> None:
    """Persist model, scaler, and metadata to the data lake."""
    model_dir = _model_dir(symbol, horizon)
    state_dir = _state_dir(symbol, horizon)

    model_dir.mkdir(parents=True, exist_ok=True)
    state_dir.mkdir(parents=True, exist_ok=True)

    # Save model weights
    if model._fitted:
        try:
            with open(model_dir / "model.pkl", "wb") as f:
                pickle.dump(model.model, f)
        except Exception as ex:
            print(f"[MlCortex] Failed to save model: {ex}", file=sys.stderr)

    # Save scaler
    if model._scaler_fitted:
        try:
            with open(model_dir / "scaler.pkl", "wb") as f:
                pickle.dump(model.scaler, f)
        except Exception as ex:
            print(f"[MlCortex] Failed to save scaler: {ex}", file=sys.stderr)

    # Save metadata
    try:
        meta = model.get_state_dict()
        with open(state_dir / "metadata.json", "w") as f:
            json.dump(meta, f, indent=2)
    except Exception as ex:
        print(f"[MlCortex] Failed to save metadata: {ex}", file=sys.stderr)
