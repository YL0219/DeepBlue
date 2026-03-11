import argparse
import json
import sys


def handle_action(action, argv):
    if action != "regime":
        return {"ok": False, "domain": "macro", "action": action, "error": "Unknown macro action."}

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--region", default="global")
    args, _ = parser.parse_known_args(argv)

    return {
        "ok": True,
        "domain": "macro",
        "action": action,
        "status": "placeholder",
        "message": "Macro manager wired successfully.",
        "region": (args.region or "global").strip().lower() or "global",
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
