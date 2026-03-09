"""
market_ingest.py — Modular OHLCV ingestion worker for Deep Blue.

Extracted from market_ingest_worker.py for router integration.
Contains all ingestion logic; the original file is now a thin wrapper.

Provider strategy:
  1. Attempt OpenBB (provider="yfinance") for ALL symbols in a child process
     with a warm-up-safe timeout (60s first run, 20s steady-state).
  2. For any symbols that failed or timed out, fall back to direct yfinance.
  3. Per-result providerUsed reflects which provider actually succeeded:
     "openbb" or "yfinance".

Output contract:
  - stdout: single JSON object matching IngestionReport schema (camelCase)
  - stderr: progress/debug logs (safe to ignore)
  - exit code 0: report printed (individual symbols may still have isSuccess=false)
  - exit code 1: fatal error before any processing
"""

import sys
import json
import argparse
import uuid
import os
import tempfile
import shutil
import multiprocessing
from datetime import datetime, timezone, timedelta


# ── Dependency checks (required) ────────────────────────────────────────────
# yfinance: required (fallback provider — always available)
# pandas + pyarrow: required for Parquet I/O
# openbb: optional (attempted lazily in child process)

try:
    import yfinance as yf
except ImportError:
    print(json.dumps({
        "schemaVersion": 1, "jobId": "", "startedAtUtc": "", "finishedAtUtc": "",
        "durationMs": 0, "request": {}, "results": [],
        "warnings": ["FATAL: yfinance is not installed. Run: pip install yfinance"]
    }))
    sys.exit(1)

try:
    import pandas as pd
except ImportError:
    print(json.dumps({
        "schemaVersion": 1, "jobId": "", "startedAtUtc": "", "finishedAtUtc": "",
        "durationMs": 0, "request": {}, "results": [],
        "warnings": ["FATAL: pandas is not installed. Run: pip install pandas"]
    }))
    sys.exit(1)

try:
    import pyarrow  # noqa: F401 — needed by pandas .to_parquet()
except ImportError:
    print(json.dumps({
        "schemaVersion": 1, "jobId": "", "startedAtUtc": "", "finishedAtUtc": "",
        "durationMs": 0, "request": {}, "results": [],
        "warnings": ["FATAL: pyarrow is not installed. Run: pip install pyarrow"]
    }))
    sys.exit(1)


# ── Constants ────────────────────────────────────────────────────────────────

OPENBB_WARMUP_TIMEOUT = 60   # seconds — first run after install / openbb-build
OPENBB_STEADY_TIMEOUT = 20   # seconds — normal steady-state
WARMUP_MARKER = "data_lake/market/.openbb_warmed"


# ── Shared utilities ─────────────────────────────────────────────────────────

def _write_parquet(df, symbol, interval, out_root):
    """Atomic Parquet write (tmp file -> rename). Returns the final path."""
    dir_path = os.path.join(out_root, f"symbol={symbol}", f"interval={interval}")
    os.makedirs(dir_path, exist_ok=True)
    final_path = os.path.join(dir_path, "latest.parquet")

    fd, tmp_path = tempfile.mkstemp(suffix=".parquet", dir=dir_path)
    os.close(fd)
    try:
        df.to_parquet(tmp_path, engine="pyarrow", index=False)
        shutil.move(tmp_path, final_path)
    except Exception:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)
        raise

    return final_path


def _make_success_result(symbol, interval, provider, parquet_path, rows, data_start, data_end):
    """Build a success result dict matching the IngestionResult schema."""
    return {
        "symbol": symbol,
        "interval": interval,
        "providerUsed": provider,
        "parquetPath": parquet_path.replace("\\", "/"),
        "rowsWritten": rows,
        "dataStartUtc": data_start,
        "dataEndUtc": data_end,
        "isSuccess": True,
        "error": None,
    }


def _make_error_result(symbol, interval, code, message, provider="yfinance"):
    """Build an error result dict matching the IngestionResult schema."""
    print(f"[Ingestion] {symbol}: ERROR — {message}", file=sys.stderr)
    return {
        "symbol": symbol,
        "interval": interval,
        "providerUsed": provider,
        "parquetPath": "",
        "rowsWritten": 0,
        "dataStartUtc": None,
        "dataEndUtc": None,
        "isSuccess": False,
        "error": {"code": code, "message": message, "provider": provider},
    }


# ── OpenBB child-process worker ─────────────────────────────────────────────

def _openbb_batch_fetch(symbols, interval, start_date, end_date, result_queue):
    """
    Target for multiprocessing.Process. Attempts OpenBB fetch for ALL symbols.
    Communicates results back via the Queue as serializable dicts (not DataFrames).

    CRITICAL: stdout is redirected to stderr immediately to prevent any library
    output from corrupting the parent's single-JSON stdout contract.
    """
    # Redirect stdout -> stderr BEFORE any library import
    sys.stdout = sys.stderr

    try:
        from openbb import obb
    except ImportError:
        result_queue.put(("import_error", "openbb package not installed"))
        return
    except Exception as e:
        result_queue.put(("import_error", f"openbb import failed: {e}"))
        return

    results = {}
    for sym in symbols:
        try:
            print(f"[Ingestion/OpenBB] Fetching {sym}...", file=sys.stderr)
            data = obb.equity.price.historical(
                symbol=sym,
                start_date=start_date,
                end_date=end_date,
                provider="yfinance",
            )
            df = data.to_dataframe().reset_index()

            # Normalize column names to our stable schema
            col_map = {}
            for col in df.columns:
                lc = col.lower()
                if lc in ("date", "datetime", "timestamp", "time"):
                    col_map[col] = "time"
                elif lc == "open":
                    col_map[col] = "open"
                elif lc == "high":
                    col_map[col] = "high"
                elif lc == "low":
                    col_map[col] = "low"
                elif lc == "close":
                    col_map[col] = "close"
                elif lc == "volume":
                    col_map[col] = "volume"
            df = df.rename(columns=col_map)

            required = {"time", "open", "high", "low", "close", "volume"}
            missing = required - set(df.columns)
            if missing:
                results[sym] = {"error": f"Missing columns after normalization: {missing}"}
                continue

            df = df[["time", "open", "high", "low", "close", "volume"]]

            # UTC-normalize time column
            if hasattr(df["time"].dtype, "tz") and df["time"].dtype.tz is not None:
                df["time"] = df["time"].dt.tz_convert("UTC").dt.tz_localize(None)

            df["volume"] = df["volume"].astype("int64")

            if df.empty:
                results[sym] = {"error": f"No data returned for {sym}"}
                continue

            # Serialize to plain Python types for safe pickling through the Queue
            results[sym] = {
                "data": {
                    "time": [t.isoformat() for t in df["time"]],
                    "open": df["open"].tolist(),
                    "high": df["high"].tolist(),
                    "low": df["low"].tolist(),
                    "close": df["close"].tolist(),
                    "volume": df["volume"].tolist(),
                },
                "rows": len(df),
            }
            print(f"[Ingestion/OpenBB] {sym}: OK ({len(df)} rows)", file=sys.stderr)

        except Exception as e:
            results[sym] = {"error": str(e)}
            print(f"[Ingestion/OpenBB] {sym}: FAILED — {e}", file=sys.stderr)

    result_queue.put(("ok", results))


def _try_openbb_batch(symbols, interval, start_date, end_date):
    """
    Attempt OpenBB fetch for all symbols in a child process with timeout.
    Returns (results_dict_or_None, error_string_or_None).
    """
    is_warmup = not os.path.exists(WARMUP_MARKER)
    timeout = OPENBB_WARMUP_TIMEOUT if is_warmup else OPENBB_STEADY_TIMEOUT
    label = "warmup" if is_warmup else "steady"
    print(f"[Ingestion] OpenBB attempt ({label}, timeout={timeout}s) for: {', '.join(symbols)}",
          file=sys.stderr)

    q = multiprocessing.Queue()
    p = multiprocessing.Process(
        target=_openbb_batch_fetch,
        args=(symbols, interval, start_date, end_date, q),
    )
    p.start()

    import queue
    try:
        # FIX: The parent MUST read from the queue BEFORE joining, or else the OS
        # pipe fills up with data and the child process deadlocks trying to exit!
        status, payload = q.get(timeout=timeout)
        error_msg = None
    except queue.Empty:
        status, payload = None, None
        error_msg = f"OpenBB timed out after {timeout}s"
    except Exception as e:
        status, payload = None, None
        error_msg = f"Failed to read from queue: {e}"

    # Now that the queue is empty, the child can safely die
    p.join(5)
    if p.is_alive():
        print("[Ingestion] OpenBB child hung. Terminating...", file=sys.stderr)
        p.terminate()
        p.join(2)
        if p.is_alive():
            p.kill()

    if error_msg:
        return None, error_msg

    if status == "import_error":
        print(f"[Ingestion] OpenBB unavailable: {payload}", file=sys.stderr)
        return None, payload

    if status != "ok":
        return None, f"Unexpected OpenBB status: {status}"

    return payload, None


def _mark_openbb_warmed():
    """Create the warm-up marker after first successful OpenBB fetch."""
    try:
        marker_dir = os.path.dirname(WARMUP_MARKER)
        if marker_dir:
            os.makedirs(marker_dir, exist_ok=True)
        with open(WARMUP_MARKER, "w") as f:
            f.write(datetime.now(timezone.utc).isoformat())
    except Exception as e:
        print(f"[Ingestion] Could not write warm-up marker: {e}", file=sys.stderr)


# ── yfinance fallback (existing MVP logic) ───────────────────────────────────

def _ingest_yfinance(symbol, interval, start_date, end_date, out_root, openbb_error=None):
    """
    Fetch OHLCV for one symbol via yfinance, write Parquet, return result dict.
    If openbb_error is provided, it's prepended to any failure message.
    """
    try:
        print(f"[Ingestion] yfinance fallback: {symbol} {interval} {start_date}..{end_date}",
              file=sys.stderr)
        ticker = yf.Ticker(symbol)
        hist = ticker.history(start=start_date, end=end_date, interval=interval)

        if hist.empty:
            msg = f"No data returned for {symbol}"
            if openbb_error:
                msg = f"OpenBB failed first: {openbb_error}; yfinance failed: {msg}"
            return _make_error_result(symbol, interval, "NO_DATA", msg, "yfinance")

        # Normalize to stable schema
        idx = hist.index
        if idx.tz is not None:
            idx = idx.tz_convert("UTC").tz_localize(None)

        df = pd.DataFrame({
            "time":   idx,
            "open":   hist["Open"].values,
            "high":   hist["High"].values,
            "low":    hist["Low"].values,
            "close":  hist["Close"].values,
            "volume": hist["Volume"].values.astype("int64"),
        })

        final_path = _write_parquet(df, symbol, interval, out_root)
        data_start = df["time"].min().isoformat() + "Z"
        data_end = df["time"].max().isoformat() + "Z"

        print(f"[Ingestion] {symbol}: yfinance wrote {len(df)} rows -> {final_path}",
              file=sys.stderr)

        return _make_success_result(
            symbol, interval, "yfinance", final_path, len(df), data_start, data_end)

    except Exception as e:
        msg = str(e)
        if openbb_error:
            msg = f"OpenBB failed first: {openbb_error}; yfinance failed: {msg}"
        return _make_error_result(symbol, interval, "EXCEPTION", msg, "yfinance")


# ── Entry point ──────────────────────────────────────────────────────────────

def main(argv=None):
    """
    Entry point for market ingestion. Called by python_router.py or directly.
    argv: list of CLI args (without script name). None = use sys.argv[1:].
    """
    parser = argparse.ArgumentParser(description="Deep Blue OHLCV Ingestion Worker")
    parser.add_argument("--symbols", required=True, help="Comma-separated ticker symbols")
    parser.add_argument("--interval", default="1d", help="Candle interval (default: 1d)")
    parser.add_argument("--lookbackDays", type=int, default=365, help="Days of history (default: 365)")
    parser.add_argument("--outRoot", default="data_lake/market/ohlcv", help="Output root directory")
    args = parser.parse_args(argv)

    symbols = [s.strip().upper() for s in args.symbols.split(",") if s.strip()]
    if not symbols:
        print(json.dumps({
            "schemaVersion": 1, "jobId": "", "startedAtUtc": "", "finishedAtUtc": "",
            "durationMs": 0, "request": {}, "results": [],
            "warnings": ["No symbols provided."]
        }))
        sys.exit(0)

    job_id = str(uuid.uuid4())
    started = datetime.now(timezone.utc)

    end_date = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    start_date = (datetime.now(timezone.utc) - timedelta(days=args.lookbackDays)).strftime("%Y-%m-%d")

    results = []
    warnings = []
    openbb_errors = {}       # symbol -> error string (for composing dual-failure messages)
    openbb_succeeded = set()  # symbols that succeeded via OpenBB

    # ── Phase 1: Attempt OpenBB for the entire batch ──────────────────────
    openbb_results, batch_error = _try_openbb_batch(
        symbols, args.interval, start_date, end_date)

    if openbb_results is None:
        # Total OpenBB failure — fall back to yfinance for ALL symbols
        if batch_error:
            warnings.append(f"OpenBB batch failed: {batch_error}")
            for sym in symbols:
                openbb_errors[sym] = batch_error
    else:
        # Process per-symbol OpenBB results
        any_openbb_success = False
        for sym in symbols:
            if sym not in openbb_results:
                openbb_errors[sym] = "Symbol not in OpenBB response"
                continue

            entry = openbb_results[sym]
            if "error" in entry and "data" not in entry:
                openbb_errors[sym] = entry["error"]
                print(f"[Ingestion] {sym}: OpenBB per-symbol error: {entry['error']}",
                      file=sys.stderr)
                continue

            # OpenBB succeeded for this symbol — reconstruct DF and write Parquet
            try:
                data = entry["data"]
                df = pd.DataFrame({
                    "time":   pd.to_datetime(data["time"]),
                    "open":   data["open"],
                    "high":   data["high"],
                    "low":    data["low"],
                    "close":  data["close"],
                    "volume": data["volume"],
                })

                final_path = _write_parquet(df, sym, args.interval, args.outRoot)
                data_start = df["time"].min().isoformat() + "Z"
                data_end = df["time"].max().isoformat() + "Z"

                print(f"[Ingestion] {sym}: OpenBB wrote {len(df)} rows -> {final_path}",
                      file=sys.stderr)

                results.append(_make_success_result(
                    sym, args.interval, "openbb", final_path, len(df), data_start, data_end))
                openbb_succeeded.add(sym)
                any_openbb_success = True

            except Exception as e:
                openbb_errors[sym] = f"Parquet write after OpenBB failed: {e}"
                print(f"[Ingestion] {sym}: OpenBB data OK but Parquet write failed: {e}",
                      file=sys.stderr)

        # Create warm-up marker if at least one symbol succeeded via OpenBB
        if any_openbb_success:
            _mark_openbb_warmed()

    # ── Phase 2: yfinance fallback for remaining symbols ──────────────────
    fallback_symbols = [s for s in symbols if s not in openbb_succeeded]

    if fallback_symbols:
        print(f"[Ingestion] yfinance fallback for: {', '.join(fallback_symbols)}", file=sys.stderr)

    for sym in fallback_symbols:
        result = _ingest_yfinance(
            sym, args.interval, start_date, end_date, args.outRoot,
            openbb_errors.get(sym))
        results.append(result)

    # ── Build report ──────────────────────────────────────────────────────
    # Sort results to match input symbol order
    symbol_order = {s: i for i, s in enumerate(symbols)}
    results.sort(key=lambda r: symbol_order.get(r["symbol"], 999))

    finished = datetime.now(timezone.utc)
    duration_ms = int((finished - started).total_seconds() * 1000)

    # Request-level providerUsed: "openbb" if OpenBB was available, else "yfinance"
    request_provider = "openbb" if openbb_results is not None else "yfinance"

    report = {
        "schemaVersion": 1,
        "jobId": job_id,
        "startedAtUtc": started.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "finishedAtUtc": finished.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "durationMs": duration_ms,
        "request": {
            "symbols": symbols,
            "interval": args.interval,
            "startDate": start_date,
            "endDate": end_date,
            "outRoot": args.outRoot,
            "providerUsed": request_provider,
        },
        "results": results,
        "warnings": warnings,
    }

    # Exactly ONE JSON object to stdout — this is the contract with C#
    print(json.dumps(report))
