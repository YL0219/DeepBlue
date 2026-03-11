"""
aether_router.py - Thin dispatcher for Aether domain workers.

Usage:
    python aether_router.py <domain> <action> [args...]
"""

import argparse
import json
import os
import sys


def _emit(payload):
    print(json.dumps(payload, separators=(",", ":")))


def _error(msg):
    print(msg, file=sys.stderr)
    _emit({"ok": False, "error": msg})
    sys.exit(1)


def _route_math(action, remaining):
    from math_manager import handle_action
    return handle_action(action, remaining)


def _route_ml(action, remaining):
    from ml_manager import handle_action
    return handle_action(action, remaining)


def _route_sim(action, remaining):
    from sim_manager import handle_action
    return handle_action(action, remaining)


def _route_macro(action, remaining):
    from macro_manager import handle_action
    return handle_action(action, remaining)


def main():
    parser = argparse.ArgumentParser(description="Aether Python router")
    parser.add_argument("domain")
    parser.add_argument("action")
    args, remaining = parser.parse_known_args()

    router_dir = os.path.dirname(os.path.abspath(__file__))
    if router_dir not in sys.path:
        sys.path.insert(0, router_dir)

    if args.domain == "math":
        payload = _route_math(args.action, remaining)
    elif args.domain == "ml":
        payload = _route_ml(args.action, remaining)
    elif args.domain == "sim":
        payload = _route_sim(args.action, remaining)
    elif args.domain == "macro":
        payload = _route_macro(args.action, remaining)
    else:
        _error("Unknown domain: '{}'. Valid: math, ml, sim, macro".format(args.domain))
        return

    _emit(payload)


if __name__ == "__main__":
    main()
