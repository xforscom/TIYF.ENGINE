## M10 – GVRS Live Gate (Phase A: Telemetry Only)

### Scope

- Surface GVRS gate readiness in telemetry without affecting executions.
- Record would-be gate triggers (bucket = Volatile) as alerts and counters.
- Keep demo configs file-backed; no runtime blocking or provider wiring.

### Gate Conditions

- Reuses existing GVRS bucket computation.
- When config `gvrs_gate.enabled` is `true` **and** the bucket is `Volatile`, emit `ALERT_BLOCK_GVRS_GATE` plus metrics/health counters.
- Orders are still allowed during this phase; the alert is telemetry-only.

### Config Surface

```json
"gvrs_gate": {
  "enabled": false,
  "block_on_volatile": false
}
```

- `enabled` controls telemetry + alerting.
- `block_on_volatile` opts into live blocking; defaults to false, so existing telemetry-only deployments are unchanged.
- Demo/sample configs keep both values false.

### Telemetry Additions

- `/metrics`
  - `engine_gvrs_gate_blocks_total` – cumulative would-be blocks.
  - `engine_gvrs_gate_is_blocking{state="volatile"}` – `1` when bucket is volatile and gate enabled, else `0`.
- `/health`
  - `gvrs_gate` block: `{ bucket, blocking_enabled, last_block_utc }`.
- Risk alerts: `ALERT_BLOCK_GVRS_GATE` appended alongside existing risk rail alerts when bucket=Volatile.

### Proof (m10-gvrs-gate-proof)

- Runs `tools/GvrsGateProbe` with `proof/m10-gvrs-config.json`.
- Forces GVRS snapshot to `Volatile`, triggers the telemetry path, and captures:
  - `summary.txt` (human-readable gate report).
  - `metrics.txt` containing the new gauges.
  - `health.json` showing `gvrs_gate.last_block_utc`.
- Workflow location: `.github/workflows/m10-gvrs-gate-proof.yml`.

### Non-Scope

- No runtime blocking or kill-switch activation yet.
- No changes to GVRS EWMA logic, smoothing, or alert thresholds.
- No live-provider wiring (FMP, etc.) and no demo config flips.
