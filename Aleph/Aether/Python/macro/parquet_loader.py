"""
parquet_loader.py — Load OHLCV data for macro basket symbols from the local data lake.

Reuses the same data lake layout as math:
    data_lake/market/ohlcv/symbol=<SYM>/interval=1d/latest.parquet
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


def load_basket(symbols: list, timeframe: str = "1d", min_rows: int = 50) -> tuple:
    """
    Load OHLCV for each symbol in the basket.

    Returns:
        (dict[symbol → DataFrame], basket_status_dict)
    """
    data_root = _resolve_data_root()
    available = []
    missing = []
    frames = {}
    warnings = []

    if data_root is None:
        return {}, {
            "required_symbols": symbols,
            "available_symbols": [],
            "missing_symbols": symbols,
            "enough_data": False,
        }

    for sym in symbols:
        path = os.path.join(data_root, f"symbol={sym}", f"interval={timeframe}", "latest.parquet")
        if not os.path.exists(path):
            missing.append(sym)
            continue

        try:
            df = pd.read_parquet(path, engine="pyarrow")
            df.columns = [c.lower().strip() for c in df.columns]

            col_missing = EXPECTED_COLUMNS - set(df.columns)
            if col_missing:
                warnings.append(f"{sym}: missing columns {sorted(col_missing)}")
                missing.append(sym)
                continue

            if not pd.api.types.is_datetime64_any_dtype(df["time"]):
                df["time"] = pd.to_datetime(df["time"], utc=True)

            df = df.sort_values("time").reset_index(drop=True)

            if len(df) < min_rows:
                warnings.append(f"{sym}: only {len(df)} rows (need {min_rows})")
                missing.append(sym)
                continue

            frames[sym] = df
            available.append(sym)
            print(f"[macro/parquet_loader] Loaded {len(df)} rows for {sym}", file=sys.stderr)

        except Exception as exc:
            warnings.append(f"{sym}: load error — {exc}")
            missing.append(sym)

    # Need at least 2 symbols for any meaningful cross-asset signal
    enough = len(available) >= 2

    status = {
        "required_symbols": symbols,
        "available_symbols": available,
        "missing_symbols": missing,
        "enough_data": enough,
    }

    return frames, status
