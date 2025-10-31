#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <config-path> <artifact-dir> [snapshot-path]" >&2
  exit 1
fi

ORIG_CONFIG="$(realpath "$1")"
ARTIFACT_DIR="$2"
SNAPSHOT_PATH="${3:-}"

ARTIFACT_FULL="$(realpath -m "$ARTIFACT_DIR")"
mkdir -p "$ARTIFACT_FULL"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

CONFIG_DIR="$(dirname "$ORIG_CONFIG")"

resolve_path() {
  local candidate="$1"
  if [[ -z "$candidate" || "$candidate" == "null" ]]; then
    echo ""
    return
  fi
  if [[ "$candidate" = /* ]]; then
    echo "$candidate"
  else
    realpath -m "$CONFIG_DIR/$candidate"
  fi
}

BASE_TICKS_REL="$(jq -r '.adapter.settings.stream.replayTicksFile // .InputTicksFile // empty' "$ORIG_CONFIG")"
if [[ -z "$BASE_TICKS_REL" ]]; then
  echo "Config missing replay ticks file reference: $ORIG_CONFIG" >&2
  exit 1
fi

BASE_TICKS="$(resolve_path "$BASE_TICKS_REL")"
if [[ -z "$BASE_TICKS" || ! -f "$BASE_TICKS" ]]; then
  echo "Replay ticks file not found: ${BASE_TICKS:-<unset>}" >&2
  exit 1
fi

INSTRUMENT_REL="$(jq -r '.InstrumentFile // empty' "$ORIG_CONFIG")"
INSTRUMENT_PATH=""
if [[ -n "$INSTRUMENT_REL" ]]; then
  INSTRUMENT_PATH="$(resolve_path "$INSTRUMENT_REL")"
fi

NEWS_REL="$(jq -r 'try .risk.news_blackout.source_path // empty' "$ORIG_CONFIG")"
NEWS_BASE=""
NEWS_OUT=""
if [[ -n "$NEWS_REL" ]]; then
  NEWS_BASE="$(resolve_path "$NEWS_REL")"
  if [[ -f "$NEWS_BASE" ]]; then
    NEWS_OUT="$TMP_DIR/news-stub.json"
  else
    NEWS_BASE=""
  fi
fi

TMP_TICKS="$TMP_DIR/ticks.csv"
START_MINUTE="$(date -u +"%Y-%m-%dT%H:%M:00Z")"

export SHIFT_BASE_TICKS="$BASE_TICKS"
export SHIFT_OUT_TICKS="$TMP_TICKS"
export SHIFT_START_MINUTE="$START_MINUTE"
export SHIFT_NEWS_IN="${NEWS_BASE:-NONE}"
export SHIFT_NEWS_OUT="${NEWS_OUT:-NONE}"

python3 - <<'PY'
import json
import os
from datetime import datetime, timezone

def parse_utc(ts: str) -> datetime:
    if not ts:
        raise SystemExit("Timestamp missing while shifting ticks.")
    if ts.endswith("Z"):
        ts = ts[:-1] + "+00:00"
    return datetime.fromisoformat(ts).astimezone(timezone.utc)

base_ticks = os.environ["SHIFT_BASE_TICKS"]
out_ticks = os.environ["SHIFT_OUT_TICKS"]
start_minute = parse_utc(os.environ["SHIFT_START_MINUTE"])
news_in = os.environ.get("SHIFT_NEWS_IN", "NONE")
news_out = os.environ.get("SHIFT_NEWS_OUT", "NONE")

with open(base_ticks, "r", encoding="utf-8") as src:
    header = src.readline().strip()
    rows = [line.strip() for line in src if line.strip()]

if not rows:
    raise SystemExit(f"Base tick file has no data: {base_ticks}")

first_ts = rows[0].split(",", 1)[0]
anchor = parse_utc(first_ts)
delta = start_minute - anchor

with open(out_ticks, "w", encoding="utf-8") as dst:
    dst.write(header + "\n")
    for row in rows:
        ts_str, rest = row.split(",", 1)
        shifted = parse_utc(ts_str) + delta
        dst.write(shifted.strftime("%Y-%m-%dT%H:%M:%SZ") + "," + rest + "\n")

if news_in and news_in != "NONE" and news_out and news_out != "NONE":
    with open(news_in, "r", encoding="utf-8") as fh:
        try:
            events = json.load(fh)
        except json.JSONDecodeError as exc:
            raise SystemExit(f"Failed to parse news stub {news_in}: {exc}") from exc
    for ev in events:
        if "utc" in ev:
            ev["utc"] = (parse_utc(ev["utc"]) + delta).strftime("%Y-%m-%dT%H:%M:%SZ")
    with open(news_out, "w", encoding="utf-8") as fh:
        json.dump(events, fh, indent=2)
        fh.write("\n")
PY

unset SHIFT_BASE_TICKS SHIFT_OUT_TICKS SHIFT_START_MINUTE SHIFT_NEWS_IN SHIFT_NEWS_OUT

TMP_CONFIG="$TMP_DIR/config.json"
JOURNAL_ROOT="$TMP_DIR/journals"
mkdir -p "$JOURNAL_ROOT"

jq \
  --arg ticks "$TMP_TICKS" \
  --arg journal "$JOURNAL_ROOT" \
  --arg instrument "${INSTRUMENT_PATH:-}" \
  --arg news "${NEWS_OUT:-}" \
  '
  (if ($instrument != "") then .InstrumentFile = $instrument else . end)
  | .InputTicksFile = $ticks
  | .JournalRoot = $journal
  | .adapter.settings.stream.replayTicksFile = $ticks
  | (if ($news != "" and .risk? and .risk.news_blackout?) then (.risk.news_blackout.source_path = $news) else . end)
  ' "$ORIG_CONFIG" > "$TMP_CONFIG"

CONFIG_PATH="$TMP_CONFIG"
CONFIG_DIR="$(dirname "$CONFIG_PATH")"
NEWS_OUTPUT_PATH="${NEWS_OUT:-}"

JOURNAL_REL="$(jq -r '.JournalRoot // "journals"' "$CONFIG_PATH")"
if [[ "$JOURNAL_REL" = /* ]]; then
  JOURNALS_DIR="$JOURNAL_REL"
else
  JOURNALS_DIR="$(realpath -m "$CONFIG_DIR/$JOURNAL_REL")"
fi

rm -rf "$JOURNALS_DIR"
mkdir -p "$JOURNALS_DIR"

if [[ -n "$SNAPSHOT_PATH" ]]; then
  SNAPSHOT_FULL="$(realpath "$SNAPSHOT_PATH")"
  export ENGINE_HOST_SNAPSHOT_PATH="$SNAPSHOT_FULL"
else
  unset ENGINE_HOST_SNAPSHOT_PATH || true
fi

RUN_ID="$(jq -r '.run.runId // "RUN-RISK-PROOF"' "$CONFIG_PATH")"
HOST_LOG="$ARTIFACT_FULL/host.log"

dotnet run --project src/TiYf.Engine.Host/TiYf.Engine.Host.csproj -c Release -- --config "$CONFIG_PATH" >"$HOST_LOG" 2>&1 &
HOST_PID=$!

sleep 20

HEALTH_PATH="$ARTIFACT_FULL/health.json"
METRICS_PATH="$ARTIFACT_FULL/metrics.txt"

for attempt in 1 2 3; do
  if curl -fsS http://127.0.0.1:8080/health -o "$HEALTH_PATH"; then
    break
  fi
  sleep 5
done

curl -fsS http://127.0.0.1:8080/metrics -o "$METRICS_PATH" || true

sleep 20
kill "$HOST_PID" 2>/dev/null || true
wait "$HOST_PID" 2>/dev/null || true

RUN_DIR=$(ls -td "$JOURNALS_DIR"/*/* 2>/dev/null | head -n1 || true)
if [[ -z "$RUN_DIR" ]]; then
  {
    echo "Run directory not found for run id ${RUN_ID}" >&2
    echo "Available journals directories:" >&2
    ls -R "$JOURNALS_DIR" >&2 || true
    echo "Host log tail:" >&2
    tail -n 200 "$HOST_LOG" >&2 || true
  }
  echo "Run directory not found" >&2
  exit 1
fi

cp "$RUN_DIR/events.csv" "$ARTIFACT_FULL/events.csv"
cp "$RUN_DIR/trades.csv" "$ARTIFACT_FULL/trades.csv"
cp "$CONFIG_PATH" "$ARTIFACT_FULL/config.json"
cp "$TMP_TICKS" "$ARTIFACT_FULL/ticks.csv"

if [[ -n "${NEWS_OUTPUT_PATH}" && -f "${NEWS_OUTPUT_PATH}" ]]; then
  cp "${NEWS_OUTPUT_PATH}" "$ARTIFACT_FULL/news-stub.json"
fi

if [[ -n "${SNAPSHOT_FULL:-}" ]]; then
  cp "$SNAPSHOT_FULL" "$ARTIFACT_FULL/snapshot.json"
fi

grep 'ALERT_' "$ARTIFACT_FULL/events.csv" | head -n 20 >"$ARTIFACT_FULL/alert-snippet.txt" || true

echo "Artifacts written to $ARTIFACT_FULL"

unset ENGINE_HOST_SNAPSHOT_PATH || true
