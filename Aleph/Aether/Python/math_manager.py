"""
math_manager.py — Router-facing adapter for the math domain.

Dispatches 'indicators' action to the quant/ analysis package.
Preserves the external contract expected by aether_router.py.
"""

import argparse
import json
import os
import sys


def _ensure_path():
    """Ensure the package directory is on sys.path for relative imports."""
    d = os.path.dirname(os.path.abspath(__file__))
    if d not in sys.path:
        sys.path.insert(0, d)


def handle_action(action, argv):
    if action != "indicators":
        return {"ok": False, "domain": "math", "action": action, "error": "Unknown math action."}

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--symbol", default="")
    parser.add_argument("--days", type=int, default=0)
    parser.add_argument("--timeframe", default="1d")
    args, _ = parser.parse_known_args(argv)

    symbol = (args.symbol or "").strip().upper()
    if not symbol:
        return {"ok": False, "domain": "math", "action": action, "error": "--symbol is required."}

    days = max(args.days, 0)
    timeframe = (args.timeframe or "1d").strip().lower()

    try:
        _ensure_path()
        from quant.analysis import run_indicators
        return run_indicators(symbol, timeframe, days)
    except Exception as exc:
        print(f"[math_manager] Error: {exc}", file=sys.stderr)
        return {
            "ok": False,
            "domain": "math",
            "action": action,
            "symbol": symbol,
            "error": str(exc),
        }


def main(argv=None):
    parser = argparse.ArgumentParser(description="Aether math manager")
    parser.add_argument("action")
    args, remaining = parser.parse_known_args(argv)

    payload = handle_action(args.action, remaining)
    print(json.dumps(payload, separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        print("math_manager error: {}".format(ex), file=sys.stderr)
        print(json.dumps({"ok": False, "domain": "math", "error": "manager_exception"}, separators=(",", ":")))
