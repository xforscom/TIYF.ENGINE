#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <config-path> <artifact-dir>" >&2
  exit 1
fi

SOURCE_CONFIG="$(realpath "$1")"
ARTIFACT_DIR="$2"

ARTIFACT_FULL="$(realpath -m "$ARTIFACT_DIR")"
mkdir -p "$ARTIFACT_FULL"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

TMP_CONFIG="$TMP_DIR/config.json"
TMP_JOURNAL="$TMP_DIR/journals"
TMP_OUT="$TMP_DIR/out"
mkdir -p "$TMP_JOURNAL"

# Extract risk block
RISK_JSON="$TMP_DIR/risk.json"
jq '.risk' "$SOURCE_CONFIG" >"$RISK_JSON"

# Determine optional news stub path
NEWS_PATH=""
if jq -e '.risk.news_blackout?.source_path?' "$SOURCE_CONFIG" >/dev/null; then
  NEWS_REL=$(jq -r '.risk.news_blackout.source_path' "$SOURCE_CONFIG")
  if [[ -n "$NEWS_REL" && "$NEWS_REL" != "null" ]]; then
    CONFIG_DIR="$(dirname "$SOURCE_CONFIG")"
    if [[ "$NEWS_REL" = /* ]]; then
      NEWS_PATH="$NEWS_REL"
    else
      NEWS_PATH="$(realpath -m "$CONFIG_DIR/$NEWS_REL")"
      if [[ ! -f "$NEWS_PATH" ]]; then
        NEWS_PATH="$(realpath -m "$NEWS_REL")"
      fi
    fi
    if [[ ! -f "$NEWS_PATH" ]]; then
      echo "News blackout stub not found at $NEWS_PATH" >&2
      exit 1
    fi
  fi
fi

# Compose simulation config from M0 template
BASE_CONFIG="tests/fixtures/backtest_m0/config.backtest-m0.json"
if [[ ! -f "$BASE_CONFIG" ]]; then
  echo "Base M0 config missing at $BASE_CONFIG" >&2
  exit 1
fi

if [[ -n "$NEWS_PATH" ]]; then
  jq \
    --slurpfile risk "$RISK_JSON" \
    --arg journal "$TMP_JOURNAL" \
    --arg news "$NEWS_PATH" \
    '
    .risk = $risk[0]
    | .featureFlags.risk = "active"
    | .featureFlags.riskProbe = "enabled"
    | .output.journalDir = $journal
    | (.risk.news_blackout.source_path = $news)
    ' "$BASE_CONFIG" >"$TMP_CONFIG"
else
  jq \
    --slurpfile risk "$RISK_JSON" \
    --arg journal "$TMP_JOURNAL" \
    '
    .risk = $risk[0]
    | .featureFlags.risk = "active"
    | .featureFlags.riskProbe = "enabled"
    | .output.journalDir = $journal
    ' "$BASE_CONFIG" >"$TMP_CONFIG"
fi

# Run simulation (copy events via --out for convenience)
SIM_LOG="$TMP_DIR/sim.log"
dotnet run --project src/TiYf.Engine.Sim/TiYf.Engine.Sim.csproj -c Release -- --config "$TMP_CONFIG" --out "$TMP_OUT/events.csv" >"$SIM_LOG" 2>&1

RUN_DIR=$(ls -td "$TMP_JOURNAL"/*/* 2>/dev/null | head -n1 || true)
if [[ -z "$RUN_DIR" ]]; then
  echo "Simulation journal directory not found." >&2
  echo "Simulation log tail:" >&2
  tail -n 200 "$SIM_LOG" >&2 || true
  exit 1
fi

cp "$RUN_DIR/events.csv" "$ARTIFACT_FULL/events.csv"
if [[ -f "$RUN_DIR/trades.csv" ]]; then
  cp "$RUN_DIR/trades.csv" "$ARTIFACT_FULL/trades.csv"
else
  printf 'utc_ts_open,utc_ts_close,symbol,direction,entry_price,exit_price,volume_units,pnl_ccy,pnl_r,decision_id,schema_version,config_hash,src_adapter,data_version\n' >"$ARTIFACT_FULL/trades.csv"
fi

cp "$TMP_CONFIG" "$ARTIFACT_FULL/config.json"
cp "$SIM_LOG" "$ARTIFACT_FULL/sim.log"

if [[ -n "$NEWS_PATH" ]]; then
  cp "$NEWS_PATH" "$ARTIFACT_FULL/news-stub.json"
fi

# Build alert snippet and simple health/metrics
grep 'ALERT_' "$ARTIFACT_FULL/events.csv" | head -n 20 >"$ARTIFACT_FULL/alert-snippet.txt" || true

ALERT_COUNT=$(grep -c 'ALERT_' "$ARTIFACT_FULL/events.csv" || true)
BLOCKS_BY_GATE=$(grep -o 'ALERT_[A-Z_]*' "$ARTIFACT_FULL/events.csv" | sort | uniq -c | awk '{print $2":"$1}' | paste -sd',' - || true)

cat >"$ARTIFACT_FULL/health.json" <<JSON
{"connected":true,"risk_blocks_total":$ALERT_COUNT,"gates":"$BLOCKS_BY_GATE"}
JSON

cat >"$ARTIFACT_FULL/metrics.txt" <<METRICS
engine_risk_blocks_total $ALERT_COUNT
METRICS

echo "Artifacts written to $ARTIFACT_FULL"
