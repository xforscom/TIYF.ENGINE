#!/usr/bin/env python3

"""Resolve raw journal paths to absolute locations and emit shell-friendly exports."""

import os
import pathlib
import shlex
import sys


def resolve(raw):
    if not raw:
        return None
    path = pathlib.Path(raw)
    if not path.is_absolute():
        path = (pathlib.Path.cwd() / path).resolve()
    return path


def emit(key, value):
    return f"{key}={shlex.quote(str(value))}" if value else f"{key}=''"


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: nightly-path-resolve.py <output>")

    output_path = pathlib.Path(sys.argv[1])

    events = resolve(os.environ.get("RAW_EVENTS"))
    trades = resolve(os.environ.get("RAW_TRADES"))
    run_dir = events.parent if events else pathlib.Path.cwd()

    lines = [
        emit("EVENTS_RESOLVED", events),
        emit("TRADES_RESOLVED", trades),
        emit("RUN_DIR_RESOLVED", run_dir),
    ]

    output_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
