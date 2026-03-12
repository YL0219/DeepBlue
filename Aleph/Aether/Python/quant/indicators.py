"""
indicators.py — Compute technical indicators on an OHLCV DataFrame.

Strategy:
    1. Try pandas-ta for each indicator.
    2. Fall back to manual calculation if pandas-ta is unavailable or errors.

All functions accept a DataFrame with columns: open, high, low, close, volume
and return the DataFrame with new columns appended.
"""

import sys
import pandas as pd
import numpy as np

_HAS_PANDAS_TA = False
try:
    import pandas_ta as ta  # type: ignore
    _HAS_PANDAS_TA = True
    print("[math/indicators] pandas-ta available", file=sys.stderr)
except ImportError:
    print("[math/indicators] pandas-ta not found, using manual fallback", file=sys.stderr)


# ---------------------------------------------------------------------------
# Manual fallback implementations
# ---------------------------------------------------------------------------

def _sma(series: pd.Series, period: int) -> pd.Series:
    return series.rolling(window=period, min_periods=period).mean()


def _ema(series: pd.Series, period: int) -> pd.Series:
    return series.ewm(span=period, adjust=False, min_periods=period).mean()


def _rsi(series: pd.Series, period: int = 14) -> pd.Series:
    delta = series.diff()
    gain = delta.where(delta > 0, 0.0)
    loss = (-delta).where(delta < 0, 0.0)
    avg_gain = gain.ewm(alpha=1.0 / period, min_periods=period, adjust=False).mean()
    avg_loss = loss.ewm(alpha=1.0 / period, min_periods=period, adjust=False).mean()
    rs = avg_gain / avg_loss.replace(0, np.nan)
    return 100.0 - (100.0 / (1.0 + rs))


def _macd(series: pd.Series, fast: int = 12, slow: int = 26, signal: int = 9):
    ema_fast = _ema(series, fast)
    ema_slow = _ema(series, slow)
    macd_line = ema_fast - ema_slow
    macd_signal = _ema(macd_line, signal)
    macd_hist = macd_line - macd_signal
    return macd_line, macd_signal, macd_hist


def _bollinger(series: pd.Series, period: int = 20, std_dev: float = 2.0):
    mid = _sma(series, period)
    rolling_std = series.rolling(window=period, min_periods=period).std()
    upper = mid + std_dev * rolling_std
    lower = mid - std_dev * rolling_std
    return mid, upper, lower


def _atr(high: pd.Series, low: pd.Series, close: pd.Series, period: int = 14) -> pd.Series:
    prev_close = close.shift(1)
    tr = pd.concat([
        (high - low),
        (high - prev_close).abs(),
        (low - prev_close).abs()
    ], axis=1).max(axis=1)
    return tr.ewm(alpha=1.0 / period, min_periods=period, adjust=False).mean()


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def compute_all(df: pd.DataFrame) -> tuple:
    """
    Compute all sprint indicators on *df* (must have ohlcv columns).
    Returns (df_with_indicators, warnings).
    """
    warnings = []
    df = df.copy()
    close = df["close"]
    high = df["high"]
    low = df["low"]
    volume = df["volume"].astype(float)

    # --- SMA ---
    for p in (20, 50, 200):
        col = f"sma_{p}"
        if _HAS_PANDAS_TA:
            try:
                df[col] = ta.sma(close, length=p)
            except Exception:
                df[col] = _sma(close, p)
        else:
            df[col] = _sma(close, p)
        if df[col].isna().all():
            warnings.append(f"Not enough data for SMA {p}")

    # --- EMA ---
    for p in (12, 26):
        col = f"ema_{p}"
        if _HAS_PANDAS_TA:
            try:
                df[col] = ta.ema(close, length=p)
            except Exception:
                df[col] = _ema(close, p)
        else:
            df[col] = _ema(close, p)

    # --- RSI ---
    if _HAS_PANDAS_TA:
        try:
            df["rsi_14"] = ta.rsi(close, length=14)
        except Exception:
            df["rsi_14"] = _rsi(close, 14)
    else:
        df["rsi_14"] = _rsi(close, 14)

    # --- MACD ---
    if _HAS_PANDAS_TA:
        try:
            macd_df = ta.macd(close, fast=12, slow=26, signal=9)
            if macd_df is not None and not macd_df.empty:
                df["macd_line"] = macd_df.iloc[:, 0]
                df["macd_signal"] = macd_df.iloc[:, 1]
                df["macd_histogram"] = macd_df.iloc[:, 2]
            else:
                raise ValueError("empty macd")
        except Exception:
            ml, ms, mh = _macd(close)
            df["macd_line"], df["macd_signal"], df["macd_histogram"] = ml, ms, mh
    else:
        ml, ms, mh = _macd(close)
        df["macd_line"], df["macd_signal"], df["macd_histogram"] = ml, ms, mh

    # --- Bollinger Bands ---
    if _HAS_PANDAS_TA:
        try:
            bb = ta.bbands(close, length=20, std=2.0)
            if bb is not None and not bb.empty:
                cols = bb.columns.tolist()
                df["bb_lower"] = bb[cols[0]]
                df["bb_mid"] = bb[cols[1]]
                df["bb_upper"] = bb[cols[2]]
            else:
                raise ValueError("empty bbands")
        except Exception:
            mid, upper, lower = _bollinger(close)
            df["bb_mid"], df["bb_upper"], df["bb_lower"] = mid, upper, lower
    else:
        mid, upper, lower = _bollinger(close)
        df["bb_mid"], df["bb_upper"], df["bb_lower"] = mid, upper, lower

    # Bandwidth
    df["bb_bandwidth"] = (df["bb_upper"] - df["bb_lower"]) / df["bb_mid"].replace(0, np.nan)

    # --- ATR ---
    if _HAS_PANDAS_TA:
        try:
            df["atr_14"] = ta.atr(high, low, close, length=14)
        except Exception:
            df["atr_14"] = _atr(high, low, close, 14)
    else:
        df["atr_14"] = _atr(high, low, close, 14)

    df["atr_pct"] = df["atr_14"] / close.replace(0, np.nan)

    # --- Rolling volatility (20-day log-return std annualized) ---
    log_ret = np.log(close / close.shift(1))
    df["volatility_20"] = log_ret.rolling(window=20, min_periods=20).std() * np.sqrt(252)

    # --- Volume SMA ---
    df["volume_sma_20"] = _sma(volume, 20)

    # --- Price distance from MAs ---
    for p in (20, 50, 200):
        sma_col = f"sma_{p}"
        df[f"dist_sma_{p}"] = (close - df[sma_col]) / df[sma_col].replace(0, np.nan)

    return df, warnings
