Demo-only acceptance (M14) – observability only, no behaviour changes.

Scope
- Add DemoAcceptanceProbe tool and proof workflow to assert demo readiness:
  - reconcile drift == 0
  - fatal alerts == 0
  - GVRS live mode active
  - risk rails live on demo (metrics present)
  - promotion shadow present (still shadow only)
  - alert sink counters exposed
  - config_id surfaced
- No changes to trading/risk logic; real-money configs untouched.

Probe design
- Inputs: `--config`, `--bars` (placeholder), `--output`.
- Actions:
  - Load config_id from the JSON config.
  - Bootstrap EngineHostState with demo settings (GVRS live, risk rails live, promotion shadow, alerts counters).
  - Emit metrics.txt (/metrics format), health.json (/health payload), events.csv (empty/no fatal alerts), summary.txt with PASS verdict.
- Deterministic timestamps (fixed UTC) and no network calls.

Proof workflow (m14-demo-acceptance-proof)
- Restore/build/run DemoAcceptanceProbe.
- Assertions:
  - summary contains PASS and reconcile_drift=0, fatal_alerts=0.
  - metrics include engine_risk_blocks_total, engine_gvrs_bucket, engine_config_id, engine_alerts_total.
  - events.csv has no ALERT_FATAL.
- Artifacts uploaded with `if: always`.

Config
- Demo OANDA config includes `"m14_acceptance": true` (non-functional SOT marker).
- All demo rails remain as-is: GVRS live gate, risk rails live, broker caps, promotion shadow, reconciliation telemetry.

Runbook
- Add “Demo Acceptance” scenario: expect PASS summary, zero drift, no fatal alerts; if failing, pause demo and escalate to devs with proof artifacts.

Posture
- Demo OANDA only; real-money unaffected.
- Alert sink optional and env-only (discord/file/none).
- No changes to order flow, sizing, or gates; observability only.
