import argparse
import json
import sys


def handle_action(action, argv):
    if action != "backtest":
        return {"ok": False, "domain": "sim", "action": action, "error": "Unknown sim action."}

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--symbol", default="")
    parser.add_argument("--days", type=int, default=180)
    parser.add_argument("--strategy", default="baseline")
    args, _ = parser.parse_known_args(argv)

    symbol = (args.symbol or "").strip().upper()
    if not symbol:
        return {"ok": False, "domain": "sim", "action": action, "error": "--symbol is required."}

    return {
        "ok": True,
        "domain": "sim",
        "action": action,
        "status": "placeholder",
        "message": "Simulation manager wired successfully.",
        "symbol": symbol,
        "days": max(args.days, 1),
        "strategy": (args.strategy or "baseline").strip().lower(),
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description="Aether simulation manager")
    parser.add_argument("action")
    args, remaining = parser.parse_known_args(argv)

    payload = handle_action(args.action, remaining)
    print(json.dumps(payload, separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        print("sim_manager error: {}".format(ex), file=sys.stderr)
        print(json.dumps({"ok": False, "domain": "sim", "error": "manager_exception"}, separators=(",", ":")))
