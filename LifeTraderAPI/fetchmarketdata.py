"""
fetchmarketdata.py — CLI tool for Deep Blue market data.

Usage:
  python fetchmarketdata.py quote --symbol AMD
  python fetchmarketdata.py candles --symbol AMD --tf 1d --range 180d --limit 500 [--to <unix_ts>]

Output: JSON to stdout. No extra prints.
"""

import sys
import json
import argparse
from datetime import datetime, timezone, timedelta

import yfinance as yf

# ── Mappings ──────────────────────────────────────────────────────────────────

# Map our timeframe codes to yfinance interval strings
TF_TO_YF_INTERVAL = {
    "1m":  "1m",
    "5m":  "5m",
    "15m": "15m",
    "1h":  "1h",
    "1d":  "1d",
    "1w":  "1wk",
    "1mo": "1mo",
}

# Map our range codes to yfinance period strings (used when 'to' is not set)
RANGE_TO_YF_PERIOD = {
    "7d":   "7d",    # Note: intraday intervals limited to last 60 days on yfinance
    "30d":  "1mo",
    "90d":  "3mo",
    "180d": "6mo",
    "1y":   "1y",
    "2y":   "2y",
}

# Map range codes to approximate timedelta for 'to'-based fetches
RANGE_TO_TIMEDELTA = {
    "7d":   timedelta(days=7),
    "30d":  timedelta(days=30),
    "90d":  timedelta(days=90),
    "180d": timedelta(days=180),
    "1y":   timedelta(days=365),
    "2y":   timedelta(days=730),
}


def error_json(msg: str):
    """Print a JSON error to stdout and exit."""
    print(json.dumps({"ok": False, "error": msg}))
    sys.exit(1)


def cmd_quote(args):
    """Fetch the latest quote for a symbol."""
    symbol = args.symbol.upper()
    try:
        ticker = yf.Ticker(symbol)
        info = ticker.fast_info

        price = getattr(info, "last_price", None)
        if price is None:
            # Fallback: use last close from 1d history
            hist = ticker.history(period="1d")
            if hist.empty:
                error_json(f"No quote data found for {symbol}")
            price = float(hist["Close"].iloc[-1])

        result = {
            "ok": True,
            "symbol": symbol,
            "price": round(float(price), 4),
            "timestampUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        }
        print(json.dumps(result))

    except Exception as e:
        error_json(f"Quote fetch failed for {symbol}: {str(e)}")


def cmd_candles(args):
    """Fetch OHLCV candle data for a symbol."""
    symbol = args.symbol.upper()
    tf = args.tf
    range_code = args.range
    limit = min(int(args.limit), 2000)
    to_ts = args.to  # optional unix timestamp

    if tf not in TF_TO_YF_INTERVAL:
        error_json(f"Invalid timeframe '{tf}'. Allowed: {list(TF_TO_YF_INTERVAL.keys())}")
    if range_code not in RANGE_TO_YF_PERIOD:
        error_json(f"Invalid range '{range_code}'. Allowed: {list(RANGE_TO_YF_PERIOD.keys())}")

    yf_interval = TF_TO_YF_INTERVAL[tf]

    try:
        ticker = yf.Ticker(symbol)

        if to_ts:
            # Paging mode: fetch a window ending at 'to'
            to_unix = int(float(to_ts))
            end_dt = datetime.fromtimestamp(to_unix, tz=timezone.utc)
            delta = RANGE_TO_TIMEDELTA[range_code]
            start_dt = end_dt - delta
            hist = ticker.history(start=start_dt.strftime("%Y-%m-%d"),
                                  end=end_dt.strftime("%Y-%m-%d"),
                                  interval=yf_interval)
        else:
            # Standard mode: use period
            yf_period = RANGE_TO_YF_PERIOD[range_code]
            hist = ticker.history(period=yf_period, interval=yf_interval)

        if hist.empty:
            error_json(f"No candle data returned for {symbol} tf={tf} range={range_code}")

        # Trim to limit (keep most recent)
        if len(hist) > limit:
            hist = hist.iloc[-limit:]

        candles = []
        for idx, row in hist.iterrows():
            # Convert index to unix timestamp
            ts = int(idx.timestamp())
            candles.append({
                "time": ts,
                "open": round(float(row["Open"]), 4),
                "high": round(float(row["High"]), 4),
                "low": round(float(row["Low"]), 4),
                "close": round(float(row["Close"]), 4),
                "volume": int(row["Volume"]),
            })

        # nextTo hint: timestamp of the earliest candle for paging further back
        next_to = candles[0]["time"] if candles else None

        result = {
            "ok": True,
            "symbol": symbol,
            "tf": tf,
            "candles": candles,
            "nextTo": next_to,
        }
        print(json.dumps(result))

    except Exception as e:
        error_json(f"Candle fetch failed for {symbol}: {str(e)}")


# ── Entry Point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Deep Blue Market Data Fetcher")
    subparsers = parser.add_subparsers(dest="command")

    # quote subcommand
    quote_parser = subparsers.add_parser("quote")
    quote_parser.add_argument("--symbol", required=True)

    # candles subcommand
    candles_parser = subparsers.add_parser("candles")
    candles_parser.add_argument("--symbol", required=True)
    candles_parser.add_argument("--tf", required=True)
    candles_parser.add_argument("--range", required=True)
    candles_parser.add_argument("--limit", default="500")
    candles_parser.add_argument("--to", default=None)

    args = parser.parse_args()

    if args.command == "quote":
        cmd_quote(args)
    elif args.command == "candles":
        cmd_candles(args)
    else:
        error_json("Unknown command. Use 'quote' or 'candles'.")
