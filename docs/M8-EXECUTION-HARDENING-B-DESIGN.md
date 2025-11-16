# M8 Execution Hardening – Phase B (Design)

_Scope: documentation only; no code/config changes._

## Scope

### Reconciliation Loop
- **Objective:** ensure engine state (orders, positions, fills) matches broker records after startup and on a scheduled cadence (e.g., every 5 minutes).
- **Design:**
  - On startup, load the last persisted decision log + order journal; enumerate expected open orders/positions.
  - Query broker API for live orders and positions.
  - Compare both sets:
    - Orders missing on broker but present locally → mark `stale_local_order`.
    - Orders present on broker but not locally → mark `orphan_broker_order`.
    - Position deltas beyond tolerance → flag `position_drift`.
  - Reconciliation runs as a dedicated loop that:
    1. Takes a snapshot (local vs broker).
    2. Persists the report (JSON + metrics).
    3. Emits alerts if thresholds breached.

### Cross-Process Idempotency
- **Objective:** survive restarts without duplicating orders or ignoring fills.
- **Design:**
  - Persist an **order id cache** (e.g., SQLite / JSON file) keyed by `broker_client_order_id`.
  - Each entry: `{decision_id, symbol, side, size, ttl_utc, status}`.
  - TTL: configurable (default 48h); expired entries purged on startup.
  - Before sending a new order, check cache:
    - If identical key exists and status ∈ {sent, pending_fill}, treat as duplicate and skip.
    - On fill/cancel, update status to `closed` + persist.
  - Cache persistence lives under `/var/lib/tiyf/order-id-cache.json` (example), synced at shutdown + after every status change.

### Slippage Presets
- **Objective:** represent realistic slippage per instrument/time-of-day while staying deterministic.
- **Design:**
  - Config surface: `slippage_presets`: array of `{symbol, session, max_slippage_pips}`.
  - Sessions: `asia`, `eu`, `us`, `overnight` (deterministic buckets by UTC hour).
  - Engine uses existing slippage calculator interface; presets feed into the tolerance calculations referenced by proofs and metrics (`engine_slippage_pips`).

### Broker Mirrors
- **Objective:** align broker-level daily loss caps & per-symbol size caps with internal rails.
- **Design:**
  - Reconciliation loop fetches broker risk limits (when available) and compares to engine config hash.
  - Mirror object stored in telemetry: `broker_limits: {daily_loss_cap, symbol_caps{EURUSD:100000}}`.
  - On mismatch, raise alert `ALERT_BROKER_LIMIT_MISMATCH`.
  - This remains vendor-neutral (no secrets); implementation reads environment-specific adapters.

## Behaviour

### Startup / Restart Flow
1. **Crash/Manual restart detected** (`engine_restart_reason` log entry).
2. Reconciliation loop kicks off immediately:
   - Runs full order/position comparison.
   - Persists `reconcile-report.json`.
   - Emits `info` log summarising counts.
3. **Healthy:** zero or expected diff counts (e.g., 0 stale orders, 0 orphans, position drift < 0.5 pip). Proceed to normal trading after recon finishes.
4. **Manual intervention required:** any of:
   - stale or orphan orders > 0.
   - position drift beyond tolerance.
   - cache load failure/CRC mismatch.
   - broker API unreachable.
   In these cases, engine sets `kill_switch=1`, raises alerts, and waits for Ops/dev intervention.

### Partial Fills & Cancels
- Reconciliation inspects each pending order:
  - If broker shows partial fill, engine updates local fill qty, logs `partial_fill_caught_by_reconcile`, and either re-queues remainder or marks closed per strategy.
  - Stale open orders older than TTL are cancelled via broker API (if permitted) and logged as `stale_order_cancelled`.

### Interaction with M6 Logic
- **Idempotency:** cache described above extends M6 restart-safe idempotency; the M6 per-process safe guards remain and now reference the shared cache.
- **Retries:** reconcile findings inform the retry manager (e.g., do not auto-retry if broker says order already filled).
- **Kill-switch:** triggered if reconciliation reports severe divergence, ensuring we don’t double-enter positions.
- **Size caps:** same config as M6; reconciliation verifies actual exposure matches caps before clearing host to trade.

## Telemetry & Alerts

### Metrics
- `engine_reconcile_runs_total`
- `engine_reconcile_last_duration_seconds`
- `engine_reconcile_stale_orders_total`
- `engine_reconcile_orphan_orders_total`
- `engine_reconcile_position_drift_pips`
- `engine_idempotency_cache_entries_total`
- `engine_slippage_preset_hits_total{symbol,session}`

### Alerts
- `ALERT_RECONCILE_DIVERGENCE` – raised when stale/orphan counts > 0 or drift above threshold.
- `ALERT_RECONCILE_FAILED` – reconciliation loop crashed or broker API unreachable.
- `ALERT_IDEMPOTENCY_PERSISTENCE_FAILED` – cache read/write error.
- `ALERT_SLIPPAGE_PRESET_MISSING` – encountering symbol/session without configured preset (fails safe).
- `ALERT_BROKER_LIMIT_MISMATCH` – engine vs broker risk limits differ.

All alerts include `decision_id` or `reconcile_id`, `timestamp`, and relevant counts.

## Proof Workflow

### Inputs
- Canned broker event log (JSONL) describing orders, fills, cancellations.
- Engine decision log / order journal snapshot.
- Config including idempotency cache, slippage presets.

### Steps
1. Replay engine decisions with loop disabled but reconciliation forced.
2. Feed broker events to reconciliation module.
3. Produce artifacts:
   - `trades.csv` showing reconciled fills.
   - `reconcile-report.json` with counts.
   - `summary.txt` (e.g., `reconcile_summary stale=0 orphan=0 drift=0.1 cache_ok=true`).
   - `metrics.txt` with the new `engine_reconcile_*` gauges.

### Pass/Fail Criteria
- Fail if any of:
  - stale/orphan counts non-zero when not expected.
  - position drift above fixed tolerance.
  - cache read/write failure log.
  - proof script `grep -F "reconcile_summary"` missing expected tokens.
  - metrics missing lines for `engine_reconcile_runs_total`.
- Pass when all greps succeed and `summary.txt` matches expectation. Workflow uploads artifacts for Ops review just like M10/M11 proofs.

## Out of Scope for M8
- Real-money trading or broker-specific limit flips.
- GVRS logic, promotion runtime, or news adapter changes.
- UI/dashboard work.
- Automated Ops tooling (systemd changes, auto restarts).
- Changes to demo configs or kill-switch defaults.
- Any secret management refactors (handled in M9/M13).

---

_Next steps: once Ops approves this design, implementation will introduce the reconcile loop, cache persistence, slippage presets, and proof workflow, keeping deterministic guarantees._ 
