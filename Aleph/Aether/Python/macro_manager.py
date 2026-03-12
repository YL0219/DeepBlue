"""
macro_manager.py — Router-facing adapter for the macro domain.

Dispatches 'regime' action to the macro/ analysis package.
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
    if action != "regime":
        return {"ok": False, "domain": "macro", "action": action, "error": "Unknown macro action."}

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--region", default="us")
    args, _ = parser.parse_known_args(argv)

    region = (args.region or "us").strip().lower() or "us"

    try:
        _ensure_path()
        from macro.analysis import run_regime
        return run_regime(region)
    except Exception as exc:
        print(f"[macro_manager] Error: {exc}", file=sys.stderr)
        return {
            "ok": False,
            "domain": "macro",
            "action": "regime",
            "error": str(exc),
        }


def main(argv=None):
    parser = argparse.ArgumentParser(description="Aether macro manager")
    parser.add_argument("action")
    args, remaining = parser.parse_known_args(argv)

    payload = handle_action(args.action, remaining)
    print(json.dumps(payload, separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        print("macro_manager error: {}".format(ex), file=sys.stderr)
        print(json.dumps({"ok": False, "domain": "macro", "error": "manager_exception"}, separators=(",", ":")))
