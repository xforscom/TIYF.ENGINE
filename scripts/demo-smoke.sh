#!/usr/bin/env bash
set -euo pipefail

RUN_ID=${RUN_ID:-DEMO-A}
CONFIG=${CONFIG:-tests/fixtures/backtest_m0/config.backtest-m0.json}
if [[ $# -ge 1 ]]; then
  RUN_ID="$1"
fi

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SIM_DLL="$ROOT/src/TiYf.Engine.Sim/bin/Release/net8.0/TiYf.Engine.Sim.dll"
TOOLS_DLL="$ROOT/src/TiYf.Engine.Tools/bin/Release/net8.0/TiYf.Engine.Tools.dll"

if [[ ! -f "$SIM_DLL" || ! -f "$TOOLS_DLL" ]]; then
  echo "[DEMO SMOKE] Building Release binaries" >&2
  dotnet restore "$ROOT/TiYf.Engine.sln" >/dev/null
  dotnet build "$ROOT/TiYf.Engine.sln" -c Release --no-restore --nologo >/dev/null
fi

RUN_DIR="$ROOT/journals/M0/M0-RUN-$RUN_ID"
rm -rf "$RUN_DIR"

TMP_ROOT="$ROOT/tmp"
mkdir -p "$TMP_ROOT"

STAGE="$TMP_ROOT/demo-smoke-$RUN_ID"
rm -rf "$STAGE"
mkdir -p "$STAGE"

ZIP_PATH="$TMP_ROOT/demo-smoke-$RUN_ID.zip"
rm -f "$ZIP_PATH"

SIM_LOG="$STAGE/sim.log"
set +e
DOTNET_OUT=$(dotnet exec "$SIM_DLL" --config "$CONFIG" --run-id "$RUN_ID" | tee "$SIM_LOG")
SIM_EXIT=${PIPESTATUS[0]}
set -e
if [[ $SIM_EXIT -ne 0 ]]; then
  echo "Simulator exited with $SIM_EXIT" >&2
  exit $SIM_EXIT
fi

EVENTS_PATH_RAW=$(grep -m1 '^JOURNAL_DIR_EVENTS=' "$SIM_LOG" | cut -d= -f2)
TRADES_PATH_RAW=$(grep -m1 '^JOURNAL_DIR_TRADES=' "$SIM_LOG" | cut -d= -f2)
if [[ -z "$EVENTS_PATH_RAW" || -z "$TRADES_PATH_RAW" ]]; then
  echo "Failed to resolve journal paths" >&2
  exit 2
fi
EVENTS_PATH=$(python - <<'PY' "$EVENTS_PATH_RAW"
import os, sys
print(os.path.abspath(sys.argv[1]))
PY
)
TRADES_PATH=$(python - <<'PY' "$TRADES_PATH_RAW"
import os, sys
print(os.path.abspath(sys.argv[1]))
PY
)

STRICT_JSON="$STAGE/strict.json"
set +e
dotnet exec "$TOOLS_DLL" verify strict --events "$EVENTS_PATH" --trades "$TRADES_PATH" --schema 1.3.0 --json | tee "$STRICT_JSON"
STRICT_EXIT=${PIPESTATUS[0]}
set -e
if [[ $STRICT_EXIT -ne 0 ]]; then
  echo "verify strict exited with $STRICT_EXIT" >&2
  exit $STRICT_EXIT
fi

PARITY_JSON="$STAGE/parity.json"
set +e
dotnet exec "$TOOLS_DLL" verify parity --events-a "$EVENTS_PATH" --events-b "$EVENTS_PATH" --trades-a "$TRADES_PATH" --trades-b "$TRADES_PATH" --json | tee "$PARITY_JSON"
PARITY_EXIT=${PIPESTATUS[0]}
set -e
if [[ $PARITY_EXIT -ne 0 ]]; then
  echo "verify parity exited with $PARITY_EXIT" >&2
  exit $PARITY_EXIT
fi

cp "$EVENTS_PATH" "$STAGE/events.csv"
cp "$TRADES_PATH" "$STAGE/trades.csv"

ALERT_COUNT=$(grep -c '^ALERT_BLOCK_' "$EVENTS_PATH" || true)
TRADES_ROWS=$(tail -n +2 "$TRADES_PATH" | wc -l | awk '{print $1}')
EVENTS_SHA=$(python - <<'PY' "$PARITY_JSON"
import json, sys
with open(sys.argv[1], 'r', encoding='utf-8') as fh:
    data = json.load(fh)
print(data['events']['hashA'])
PY
)
TRADES_SHA=$(python - <<'PY' "$PARITY_JSON"
import json, sys
with open(sys.argv[1], 'r', encoding='utf-8') as fh:
    data = json.load(fh)
print(data['trades']['hashA'])
PY
)

cat <<EOF > "$STAGE/env.sanity"
run_id=$RUN_ID
config=$CONFIG
sim_exit=$SIM_EXIT
strict_exit=$STRICT_EXIT
parity_exit=$PARITY_EXIT
events_path=$EVENTS_PATH
trades_path=$TRADES_PATH
trades_row_count=$TRADES_ROWS
alert_block_count=$ALERT_COUNT
events_sha=$EVENTS_SHA
trades_sha=$TRADES_SHA
EOF

zip -qr "$ZIP_PATH" -j "$STAGE"/*

echo "[DEMO SMOKE] Completed"
echo "[DEMO SMOKE] SIM_EXIT=$SIM_EXIT STRICT_EXIT=$STRICT_EXIT PARITY_EXIT=$PARITY_EXIT"
echo "[DEMO SMOKE] trades_row_count=$TRADES_ROWS alert_block_count=$ALERT_COUNT"
echo "[DEMO SMOKE] Artifacts: $STAGE"
echo "[DEMO SMOKE] Zip: $ZIP_PATH"
