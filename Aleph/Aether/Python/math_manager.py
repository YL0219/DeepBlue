import argparse
import json
import sys


def _placeholder(action, symbol, days, timeframe):
    return {
        "ok": True,
        "domain": "math",
        "action": action,
        "status": "placeholder",
        "message": "Math manager wired successfully.",
        "symbol": symbol,
        "days": days,
        "timeframe": timeframe,
    }


def handle_action(action, argv):
    if action != "indicators":
        return {"ok": False, "domain": "math", "action": action, "error": "Unknown math action."}

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--symbol", default="")
    parser.add_argument("--days", type=int, default=30)
    parser.add_argument("--timeframe", default="1d")
    args, _ = parser.parse_known_args(argv)

    symbol = (args.symbol or "").strip().upper()
    if not symbol:
        return {"ok": False, "domain": "math", "action": action, "error": "--symbol is required."}

    return _placeholder(action, symbol, max(args.days, 1), (args.timeframe or "1d").strip().lower())


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
