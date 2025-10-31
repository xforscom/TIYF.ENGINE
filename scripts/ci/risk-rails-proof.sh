#!/usr/bin/env bash
set -euo pipefail

ROOT="$(pwd)"
OUT_DIR="$ROOT/proof"
CONFIG_PATH="$ROOT/risk-proof.json"
NEWS_PATH="$ROOT/configs/news-proof.json"

mkdir -p "$OUT_DIR"
mkdir -p "$(dirname "$NEWS_PATH")"
mkdir -p "$ROOT/journals"

python - <<'PY'
import datetime as dt
import json
from pathlib import Path

root = Path(".").resolve()
news_path = root / "configs" / "news-proof.json"
config_path = root / "risk-proof.json"

event_ts = (dt.datetime.utcnow() + dt.timedelta(minutes=10)).replace(microsecond=0).isoformat() + "Z"
news_path.parent.mkdir(parents=True, exist_ok=True)
with news_path.open("w", encoding="utf-8") as fh:
    json.dump([{"utc": event_ts, "impact": "high", "tags": ["USD", "EUR"]}], fh, indent=2)

config = {
    "schemaVersion": "1.3.0",
    "run": {"runId": "RUN-RISK-PROOF"},
    "adapter": {
        "type": "oanda-demo",
        "settings": {
            "accessToken": "stub-token",
            "accountId": "stub-account",
            "baseUrl": "https://api-fxpractice.oanda.com/v3/",
            "stream": {
                "enable": True,
                "feedMode": "replay",
                "replayTicksFile": "tests/fixtures/backtest_m0/ticks_EURUSD.csv",
                "heartbeatTimeoutSeconds": 5,
                "maxBackoffSeconds": 1,
                "pricingEndpoint": "/accounts/{accountId}/pricing/stream",
                "instruments": ["EUR_USD"]
            }
        }
    },
    "InstrumentFile": "tests/fixtures/backtest_m0/instruments.csv",
    "InputTicksFile": "tests/fixtures/backtest_m0/ticks_EURUSD.csv",
    "universe": ["EURUSD"],
    "risk": {
        "perTradeRiskPct": 0.01,
        "realLeverageCap": 1.0,
        "sessionWindow": {"startUtc": "07:00", "endUtc": "08:00"},
        "dailyCap": {"loss": 0, "gain": 0.25, "actionOnBreach": "block"},
        "globalDrawdown": {"maxDd": -0.25},
        "newsBlackout": {"enabled": True, "minutesBefore": 30, "minutesAfter": 30, "sourcePath": "configs/news-proof.json"},
        "forceDrawdownAfterEvals": {"EURUSD": 1},
        "maxRunDrawdownCCY": 0.01,
        "blockOnBreach": True
    },
    "JournalRoot": "journals",
    "featureFlags": {
        "risk": "active",
        "riskProbe": "disabled",
        "sentiment": "disabled"
    }
}

with config_path.open("w", encoding="utf-8") as fh:
    json.dump(config, fh, indent=2)
PY

HOST_LOG="$OUT_DIR/host-risk-proof.log"
dotnet run --project src/TiYf.Engine.Host/TiYf.Engine.Host.csproj -c Release -- --config "$CONFIG_PATH" >"$HOST_LOG" 2>&1 &
HOST_PID=$!

sleep 15

HEALTH_PATH="$OUT_DIR/health.json"
METRICS_PATH="$OUT_DIR/metrics.txt"

for attempt in 1 2 3; do
  if curl -fsS http://127.0.0.1:8080/health -o "$HEALTH_PATH"; then
    break
  fi
  sleep 20
done

curl -fsS http://127.0.0.1:8080/metrics -o "$METRICS_PATH" || true

sleep 20
kill "$HOST_PID" 2>/dev/null || true
wait "$HOST_PID" 2>/dev/null || true

RUN_DIR=$(ls -td journals/*/* 2>/dev/null | head -n1 || true)
if [ -z "$RUN_DIR" ]; then
  echo "Available journals directory contents:"
  ls -R journals || true
  echo "Host log:"
  cat "$HOST_LOG" || true
  echo "Run directory not found" >&2
  exit 1
fi
cp "$RUN_DIR/events.csv" "$OUT_DIR/events.csv"
cp "$RUN_DIR/trades.csv" "$OUT_DIR/trades.csv"

grep 'ALERT_' "$OUT_DIR/events.csv" | head -n 20 > "$OUT_DIR/alert-snippet.txt" || true

{
  echo "## Risk Gate Alerts"
  cat "$OUT_DIR/alert-snippet.txt"
  echo
  echo "## /health"
  cat "$HEALTH_PATH"
  echo
  echo "## Metrics (risk counters)"
  grep -E 'engine_risk_(blocks|throttles)_total' "$METRICS_PATH" || true
} > "$OUT_DIR/proof-summary.txt"
