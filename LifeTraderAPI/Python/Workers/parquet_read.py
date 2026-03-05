"""
parquet_read.py — Read local Parquet OHLCV data for Deep Blue.

Reads from the data lake and returns a JSON summary + candle array.
Candle format matches MarketController output (time as unix seconds + OHLCV).

Usage (via router):
    python python_router.py market parquet-read --symbol AMD --days 7

Output contract:
    stdout: single JSON object:
    {
        "ok": true,
        "symbol": "AMD",
        "interval": "1d",
        "rows": <int>,
        "startDate": "YYYY-MM-DD",
        "endDate": "YYYY-MM-DD",
        "summary": { "high": <float>, "low": <float>, "avgVolume": <int>, "pctChange": <float> },
        "candles": [ { "time": <unix_seconds>, "open": ..., "high": ..., "low": ..., "close": ..., "volume": ... }, ... ]
    }
    stderr: logs only
"""

import sys
import json
import argparse
import os
from datetime import datetime, timezone, timedelta


def _error_json(msg):
    """Print a JSON error to stdout and exit."""
    print(json.dumps({"ok": False, "error": msg}))
    sys.exit(1)


def main(argv=None):
    """
    Entry point for parquet-read. Called by python_router.py or directly.
    argv: list of CLI args (without script name). None = use sys.argv[1:].
    """
    parser = argparse.ArgumentParser(description="Deep Blue Parquet Reader")
    parser.add_argument("--symbol", required=True, help="Ticker symbol (e.g., AMD)")
    parser.add_argument("--days", type=int, default=0,
                        help="Filter to last N days (0 = all data, default: 0)")
    parser.add_argument("--dataRoot", default="data_lake/market/ohlcv",
                        help="Root directory for Parquet data lake")
    args = parser.parse_args(argv)

    symbol = args.symbol.strip().upper()
    if not symbol:
        _error_json("Symbol is required.")

    # Resolve Parquet path: data_lake/market/ohlcv/symbol=<SYM>/interval=1d/latest.parquet
    parquet_path = os.path.join(args.dataRoot, f"symbol={symbol}", "interval=1d", "latest.parquet")

    if not os.path.exists(parquet_path):
        _error_json(f"No Parquet data found for {symbol} at {parquet_path}")

    try:
        import pandas as pd
    except ImportError:
        _error_json("pandas is not installed. Run: pip install pandas")

    try:
        import pyarrow  # noqa: F401
    except ImportError:
        _error_json("pyarrow is not installed. Run: pip install pyarrow")

    try:
        df = pd.read_parquet(parquet_path, engine="pyarrow")
    except Exception as e:
        _error_json(f"Failed to read Parquet file: {e}")

    if df.empty:
        _error_json(f"Parquet file is empty for {symbol}")

    # Ensure expected columns
    required = {"time", "open", "high", "low", "close", "volume"}
    missing = required - set(df.columns)
    if missing:
        _error_json(f"Parquet missing columns: {missing}")

    # Ensure time column is datetime
    df["time"] = pd.to_datetime(df["time"])

    # Sort by time ascending
    df = df.sort_values("time").reset_index(drop=True)

    # Filter to last N days if requested
    if args.days > 0:
        cutoff = datetime.now(timezone.utc).replace(tzinfo=None) - timedelta(days=args.days)
        df = df[df["time"] >= cutoff].reset_index(drop=True)
        if df.empty:
            _error_json(f"No data in last {args.days} days for {symbol}")

    # Build candles array (time as unix seconds, matching MarketController format)
    candles = []
    for _, row in df.iterrows():
        ts = int(row["time"].timestamp())
        candles.append({
            "time": ts,
            "open": round(float(row["open"]), 4),
            "high": round(float(row["high"]), 4),
            "low": round(float(row["low"]), 4),
            "close": round(float(row["close"]), 4),
            "volume": int(row["volume"]),
        })

    # Build summary
    high = round(float(df["high"].max()), 4)
    low = round(float(df["low"].min()), 4)
    avg_volume = int(df["volume"].mean())

    first_close = float(df["close"].iloc[0])
    last_close = float(df["close"].iloc[-1])
    pct_change = round(((last_close - first_close) / first_close) * 100, 4) if first_close != 0 else 0.0

    start_date = df["time"].min().strftime("%Y-%m-%d")
    end_date = df["time"].max().strftime("%Y-%m-%d")

    result = {
        "ok": True,
        "symbol": symbol,
        "interval": "1d",
        "rows": len(df),
        "startDate": start_date,
        "endDate": end_date,
        "summary": {
            "high": high,
            "low": low,
            "avgVolume": avg_volume,
            "pctChange": pct_change,
        },
        "candles": candles,
    }

    # Exactly ONE JSON object to stdout
    print(json.dumps(result))
