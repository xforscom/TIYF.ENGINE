## M10 – GVRS Live Gate (Phase A: Demo Enforcement)

### Scope

- Drive GVRS decisions from `risk.global_volatility_gate` and allow demo configs to block new entries when the regime is too volatile.
- Exits always flow; only fresh entries are suppressed.
- Keep enforcement demo-only. Real-money configs remain on `enabled_mode="shadow"`.

### Gate Conditions

- GVRS is already seeded by the existing MarketContextService.
- When `global_volatility_gate.enabled_mode` is `"live"`:
  - Optional `live_max_bucket` constrains the highest bucket allowed (e.g. `"moderate"` blocks `"volatile"` regimes).
  - Optional `live_max_ewma` clamps the EWMA threshold.
  - If either threshold triggers, the engine emits `ALERT_BLOCK_GVRS_GATE` and skips the entry.
- If GVRS has not produced a snapshot (`HasValue=false`), the gate remains idle (no blocking).

### Config Surface (demo only)

```json
"risk": {
  "global_volatility_gate": {
    "enabled_mode": "live",
    "entry_threshold": 0.0,
    "ewma_alpha": 0.3,
    "live_max_bucket": "moderate",
    "components": [
      { "name": "fx_atr_percentile", "weight": 0.6 },
      { "name": "risk_proxy_z", "weight": 0.4 }
    ]
  }
}
```

- Demo configs (`sample-config.demo-oanda.json`, `sample-config.demo-ctrader.json`) ship with the snippet above.
- All other configs (sample, production, etc.) keep `enabled_mode` at `"shadow"` or `"disabled"`.

### Telemetry

- `/metrics`
  - `engine_gvrs_gate_blocks_total` – actual blocks witnessed during the run.
  - `engine_risk_blocks_total{gate="gvrs_live_gate"}` – risk rail roll-up.
- `/health`
  - `risk_rails.gvrs_gate_blocks_total` – cumulative count suitable for daily-monitor / proofs.
  - Existing `gvrs_gate` block still reports the last bucket + last block timestamp.
- Daily monitor: when GVRS blocks are non-zero, the summary line appends `gvrs_gate_blocks=<value>`.

### Proof (m10-gvrs-live-proof)

- Workflow: `.github/workflows/m10-gvrs-live-proof.yml`.
- Runs `tools/GvrsGateProbe` with `proof/m10-gvrs-config.json`, which seeds GVRS into `"Volatile"` and exercises the live gate.
- Verifies:
  - `events.csv` includes `ALERT_BLOCK_GVRS_GATE`.
  - `metrics.txt`/`health.json` carry the new counters.
  - `summary.txt` records the live mode posture.

### Non-Scope

- No UI, kill switch toggles, or size modulation.
- GVRS math (ATR/proxy smoothing) is unchanged.
- No real-money config changes until a future M-stage flips them intentionally.
