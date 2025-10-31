#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <config-path> <artifact-dir> [snapshot-path]" >&2
  exit 1
fi

CONFIG_PATH="$(realpath "$1")"
ARTIFACT_DIR="$2"
SNAPSHOT_PATH="${3:-}"

ARTIFACT_FULL="$(realpath -m "$ARTIFACT_DIR")"
mkdir -p "$ARTIFACT_FULL"

CONFIG_DIR="$(dirname "$CONFIG_PATH")"
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

if [[ -n "${SNAPSHOT_FULL:-}" ]]; then
  cp "$SNAPSHOT_FULL" "$ARTIFACT_FULL/snapshot.json"
fi

grep 'ALERT_' "$ARTIFACT_FULL/events.csv" | head -n 20 >"$ARTIFACT_FULL/alert-snippet.txt" || true

echo "Artifacts written to $ARTIFACT_FULL"

unset ENGINE_HOST_SNAPSHOT_PATH || true
