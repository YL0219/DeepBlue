import argparse
import json
import sys


def _ok(action, symbol, extra=None):
    payload = {
        "ok": True,
        "domain": "ml",
        "action": action,
        "status": "placeholder",
        "message": "ML manager wired successfully.",
        "symbol": symbol,
    }
    if extra:
        payload.update(extra)
    return payload


def handle_action(action, argv):
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--symbol", default="")
    parser.add_argument("--horizonDays", type=int, default=5)
    parser.add_argument("--epochs", type=int, default=1)
    args, _ = parser.parse_known_args(argv)

    symbol = (args.symbol or "").strip().upper()

    if action == "status":
        return _ok(action, symbol or "GLOBAL")

    if not symbol:
        return {"ok": False, "domain": "ml", "action": action, "error": "--symbol is required."}

    if action == "predict":
        return _ok(action, symbol, {"horizonDays": max(args.horizonDays, 1)})

    if action == "train":
        return _ok(action, symbol, {"epochs": max(args.epochs, 1)})

    return {"ok": False, "domain": "ml", "action": action, "error": "Unknown ml action."}


def main(argv=None):
    parser = argparse.ArgumentParser(description="Aether ML manager")
    parser.add_argument("action")
    args, remaining = parser.parse_known_args(argv)

    payload = handle_action(args.action, remaining)
    print(json.dumps(payload, separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        print("ml_manager error: {}".format(ex), file=sys.stderr)
        print(json.dumps({"ok": False, "domain": "ml", "error": "manager_exception"}, separators=(",", ":")))
