# 0004 - Journaling Format (M0)
- Status: Accepted
- Date: 2025-10-05

## Context
Need immutable, append-only event record of engine outputs for replay validation & audit. Simplicity over optimization.

## Decision
Single CSV file per run: `journals/{run_id}/events.csv`
Companion JSON metadata: `journals/{run_id}/meta.json` containing schema_version, config_hash, start_utc, instrument_set_hash.

CSV Columns (ordered):
1. sequence (ulong)
2. utc_ts (ISO 8601 Z)
3. event_type (string)
4. payload_json (minified)

Atomicity: write to temp file and rename at close. During run: open with append + flush after each line. Optionally add fsync when performance later evaluated.

## Consequences
+ Human-inspectable.
+ Simple diffing across runs.
- Larger than binary.
- Need eventual rotation or compression for long sessions.

## Alternatives Considered
1. One-file-per-event JSON – too many small files.
2. SQLite WAL – adds dependency for M0.
3. Binary protobuf stream – less human friendly early.
