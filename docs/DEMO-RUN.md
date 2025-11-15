## Demo Runbook Highlights

- Streaming mode runs against the `sample-config.demo-oanda.json` / `sample-config.demo-ctrader.json` configs.
- Promotion telemetry stays enabled (shadow candidates declared in the risk block).
- GVRS now runs in live mode on demo: `risk.global_volatility_gate.enabled_mode` is set to `"live"` with `live_max_bucket: "moderate"`. When the bucket escalates to Volatile, new entries are blocked and `ALERT_BLOCK_GVRS_GATE` is recorded, but exits always flow.
