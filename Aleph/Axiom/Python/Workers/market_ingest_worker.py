"""
market_ingest_worker.py — Thin backward-compatibility wrapper.

All ingestion logic now lives in market_ingest.py (importable module).
This wrapper preserves the original CLI interface for direct invocation.

Usage (unchanged):
  python market_ingest_worker.py --symbols AMD,AAPL,TSLA --interval 1d \
      --lookbackDays 365 --outRoot data_lake/market/ohlcv
"""

import multiprocessing

if __name__ == "__main__":
    # Required on Windows where multiprocessing uses "spawn"
    multiprocessing.freeze_support()

    from market_ingest import main
    main()
