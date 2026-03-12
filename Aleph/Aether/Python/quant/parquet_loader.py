"""
parquet_loader.py — Load OHLCV data from the local Parquet data lake.

Data lake contract:
    data_lake/market/ohlcv/symbol=<SYM>/interval=<INTERVAL>/latest.parquet

Expected columns: time, open, high, low, close, volume
"""

import os
import sys
import pandas as pd


EXPECTED_COLUMNS = {"time", "open", "high", "low", "close", "volume"}


def _resolve_data_root():
    """Walk up from this file to find the Aleph project root containing data_lake/."""
    d = os.path.dirname(os.path.abspath(__file__))
    for _ in range(10):
        candidate = os.path.join(d, "data_lake", "market", "ohlcv")
        if os.path.isdir(candidate):
            return candidate
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    return None


def load_ohlcv(symbol: str, timeframe: str = "1d", days: int = 0) -> tuple:
    """
    Load OHLCV Parquet for *symbol* and return (df, warnings).

    Returns:
        (pd.DataFrame | None, list[str])
        DataFrame is sorted by time ascending and trimmed to *days* if > 0.
    """
    warnings = []

    data_root = _resolve_data_root()
    if data_root is None:
        return None, ["Cannot locate data_lake/market/ohlcv under project tree."]

    parquet_path = os.path.join(data_root, f"symbol={symbol}", f"interval={timeframe}", "latest.parquet")

    if not os.path.exists(parquet_path):
        return None, [f"No Parquet file for {symbol}/{timeframe} at {parquet_path}"]

    try:
        df = pd.read_parquet(parquet_path, engine="pyarrow")
    except Exception as exc:
        return None, [f"Failed to read Parquet: {exc}"]

    # Normalize column names to lower case
    df.columns = [c.lower().strip() for c in df.columns]

    missing = EXPECTED_COLUMNS - set(df.columns)
    if missing:
        return None, [f"Missing required columns: {sorted(missing)}"]

    # Coerce time column
    if not pd.api.types.is_datetime64_any_dtype(df["time"]):
        try:
            df["time"] = pd.to_datetime(df["time"], utc=True)
        except Exception:
            warnings.append("Could not parse 'time' column as datetime; sorting may be unreliable.")

    df = df.sort_values("time").reset_index(drop=True)

    # Trim to requested window
    if days > 0 and len(df) > days:
        df = df.tail(days).reset_index(drop=True)

    # Drop rows with NaN in critical columns
    before = len(df)
    df = df.dropna(subset=["open", "high", "low", "close"]).reset_index(drop=True)
    dropped = before - len(df)
    if dropped:
        warnings.append(f"Dropped {dropped} rows with NaN OHLC values.")

    if df.empty:
        return None, warnings + ["DataFrame is empty after cleaning."]

    print(f"[math/parquet_loader] Loaded {len(df)} rows for {symbol}/{timeframe}", file=sys.stderr)
    return df, warnings
