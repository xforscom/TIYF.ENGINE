#!/usr/bin/env python3
"""Generate parity hash snapshot for nightly canary runs.

This helper reads the resolved events/trades CSV files and produces the
hash values consumed by the workflow parity summary step. The snapshot file
contains POSIX-compatible KEY=VALUE exports so the workflow can source it.
"""

from __future__ import annotations

import csv
import hashlib
import os
import sys
from pathlib import Path
from typing import Iterable


def _sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _read_events(path: Path) -> str:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as exc:  # pragma: no cover - surfaced in workflow logs
        raise SystemExit(f"unable to read events file: {path}: {exc}") from exc

    if len(lines) <= 2:
        # Preserve legacy behavior: drop the first two metadata lines.
        payload = "\n".join(lines[2:])
    else:
        payload = "\n".join(lines[2:])
    return f"{payload}\n" if payload else ""


def _normalize_trades(rows: Iterable[list[str]], idx: int) -> str:
    result: list[str] = []
    for row in rows:
        if not row:
            continue
        if 0 <= idx < len(row):
            row = row[:idx] + [""] + row[idx + 1 :]
        result.append(",".join(row))
    return f"{'\n'.join(result)}\n" if result else ""


def _read_trades(path: Path) -> str:
    if not path.exists():
        return ""

    try:
        with path.open("r", newline="", encoding="utf-8") as handle:
            reader = csv.reader(handle)
            header = next(reader, [])
            idx = header.index("config_hash") if "config_hash" in header else -1
            return _normalize_trades(reader, idx)
    except OSError as exc:  # pragma: no cover - surfaced in workflow logs
        raise SystemExit(f"unable to read trades file: {path}: {exc}") from exc


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: nightly-hash-snapshot.py <output-path> <events-path> [<trades-path>]", file=sys.stderr)
        return 2

    output_path = Path(sys.argv[1]).resolve()
    events_path = Path(sys.argv[2]).resolve()
    trades_path = Path(sys.argv[3]).resolve() if len(sys.argv) >= 4 else None

    if not events_path.exists():
        raise SystemExit("EVENTS path missing")

    events_payload = _read_events(events_path)
    trades_payload = _read_trades(trades_path) if trades_path else ""

    snapshot = [
        f"EVENTS_SHA={_sha256(events_payload)}",
        f"TRADES_SHA={_sha256(trades_payload)}",
    ]

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text("\n".join(snapshot) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
