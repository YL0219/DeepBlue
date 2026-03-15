"""
pending_memory.py - Store/load pending unresolved samples for delayed labeling.

Pending samples are predictions that haven't been resolved yet (we don't know
if the prediction was correct). Once labels are available (e.g., price moved
in the predicted direction after N days), they become resolved training samples.

Storage layout:
  data_lake/cortex/pending/{symbol}/{horizon}/pending.jsonl
  data_lake/cortex/resolved/{symbol}/{horizon}/resolved.jsonl
"""

import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path


def _cortex_root() -> Path:
    ml_dir = Path(__file__).parent
    python_dir = ml_dir.parent
    aether_dir = python_dir.parent
    content_root = aether_dir.parent
    return content_root / "data_lake" / "cortex"


def _pending_path(symbol: str, horizon: str) -> Path:
    return _cortex_root() / "pending" / symbol.upper() / horizon / "pending.jsonl"


def _resolved_path(symbol: str, horizon: str) -> Path:
    return _cortex_root() / "resolved" / symbol.upper() / horizon / "resolved.jsonl"


def store_pending_sample(
    symbol: str,
    horizon: str,
    features: list[float],
    predicted_class: str,
    asof_utc: str,
    price: float | None = None,
    source_event_id: str | None = None,
) -> bool:
    """
    Store a pending sample for future label resolution.
    Returns True if stored successfully.
    """
    path = _pending_path(symbol, horizon)
    path.parent.mkdir(parents=True, exist_ok=True)

    sample = {
        "asof_utc": asof_utc,
        "stored_utc": datetime.now(timezone.utc).isoformat(),
        "symbol": symbol.upper(),
        "horizon": horizon,
        "features": features,
        "predicted_class": predicted_class,
        "price_at_prediction": price,
        "source_event_id": source_event_id,
        "resolved": False,
    }

    try:
        with open(path, "a") as f:
            f.write(json.dumps(sample, separators=(",", ":")) + "\n")
        return True
    except Exception as ex:
        print(f"[MlCortex] Failed to store pending sample: {ex}", file=sys.stderr)
        return False


def load_pending_samples(symbol: str, horizon: str, max_samples: int = 1000) -> list[dict]:
    """Load pending (unresolved) samples."""
    path = _pending_path(symbol, horizon)
    if not path.exists():
        return []

    samples = []
    try:
        with open(path, "r") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    sample = json.loads(line)
                    if not sample.get("resolved", False):
                        samples.append(sample)
                except json.JSONDecodeError:
                    continue
                if len(samples) >= max_samples:
                    break
    except Exception as ex:
        print(f"[MlCortex] Failed to load pending samples: {ex}", file=sys.stderr)

    return samples


def store_resolved_sample(
    symbol: str,
    horizon: str,
    features: list[float],
    label: str,
    asof_utc: str,
    resolution_utc: str | None = None,
) -> bool:
    """Store a resolved (labeled) sample for training."""
    path = _resolved_path(symbol, horizon)
    path.parent.mkdir(parents=True, exist_ok=True)

    sample = {
        "asof_utc": asof_utc,
        "resolution_utc": resolution_utc or datetime.now(timezone.utc).isoformat(),
        "symbol": symbol.upper(),
        "horizon": horizon,
        "features": features,
        "label": label,
    }

    try:
        with open(path, "a") as f:
            f.write(json.dumps(sample, separators=(",", ":")) + "\n")
        return True
    except Exception as ex:
        print(f"[MlCortex] Failed to store resolved sample: {ex}", file=sys.stderr)
        return False


def load_resolved_samples(symbol: str, horizon: str, max_samples: int = 10000) -> list[dict]:
    """Load resolved (labeled) samples for training."""
    path = _resolved_path(symbol, horizon)
    if not path.exists():
        return []

    samples = []
    try:
        with open(path, "r") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    samples.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
                if len(samples) >= max_samples:
                    break
    except Exception as ex:
        print(f"[MlCortex] Failed to load resolved samples: {ex}", file=sys.stderr)

    return samples


def pending_count(symbol: str, horizon: str) -> int:
    """Count pending samples without loading them all."""
    path = _pending_path(symbol, horizon)
    if not path.exists():
        return 0
    try:
        with open(path, "r") as f:
            return sum(1 for line in f if line.strip())
    except Exception:
        return 0
