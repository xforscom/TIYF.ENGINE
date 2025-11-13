# M8 Execution Hardening (Plan)

This document outlines the implementation plan for M8 – Execution Hardening (B) in Blueprint v2.0. It builds directly on M6 (Execution Hardening A) and assumes the current main branch:

- **Current state after M6:**
  - Idempotent order send/cancel caches (5000 entries, 24h TTL) with eviction warnings and metrics.
  - Kill-switch with exit-only allowance and alert throttling.
  - Size-limit validation + risk rails for exposure/run drawdown.
  - Slippage hook pluggable but defaulting to zero.
  - Telemetry for rejects / idempotency / slippage selection.
  - Proof suite: m6-execution-proof on main plus daily-monitor health with order_rejects_total and GVRS.

## Blueprint gaps for M8
- **Broker reconciliation:** canonical mapping between broker order IDs / fills and engine decision IDs, including restart recovery and evidence (Sec. 4.3.4).
- **Restart-safe idempotency:** surviving process restarts without double-sending (persist caches, maintain TTL/eviction, prove behaviour).
- **Realistic slippage profile:** configurable spread + instrument/time-of-day tables, metrics, and tests (Sec. 4.3.3).
- **Proofs:** deterministic harness replaying broker logs → reconciliation report + trades.csv match; metrics to show reconciliation coverage and restart-safe idempotency; slippage acceptance proof.

## Phase breakdown

### Phase M8-A: Reconciliation journal + offline tooling (telemetry-only)
- **Scope:**
  - Extend execution adapter and host to emit broker reconciliation rows (decision_id, broker_order_id, status, fills) into a dedicated journal and Prometheus gauges (counts, last seen).
  - Add an offline reconciliation tool (e.g., tools/BrokerReconciler) that ingests the journal plus broker export to flag mismatches.
  - **Non-scope:** No live blocking logic, no persistence changes, no slippage updates.
- **Touch points:** `TiYf.Engine.Host` (execution callbacks), `TiYf.Engine.Sim` (fixtures), `TiYf.Engine.Tools` (new reconciler), `docs/` for operator procedure.
- **Proofs/tests:**
  - Unit tests for reconciliation record formatting.
  - CI workflow (e.g., `m8-reconciliation-proof.yml`) running the tool against canned broker logs.
- **Risks:**
  - Journal bloat (use rolling files), deterministic ordering for proofs, ensure no impact on execution latency.
- **Status:** Reconciliation journals + Prometheus fields now live in `TiYf.Engine.Host`; the `tools/ReconciliationProbe` CLI ingests journal directories and emits summary/metrics/health artifacts, and the `m8-reconcile-proof` workflow runs this probe against canned fixtures on every dispatch.

### Phase M8-B: Restart-safe idempotency + reconciliation integration
- **Scope:**
  - Persist order/cancel idempotency keys (e.g., lightweight RocksDB / JSON snapshot) with TTL enforcement on reload.
  - On startup, replay persisted keys, reconcile with broker open orders, and emit alerts if divergence detected.
  - Update execution loop to consult reconciliation state (e.g., skip duplicate sends when broker already working order exists).
  - **Non-scope:** Slippage changes; kill-switch and risk rails remain as in M6.
- **Touch points:** `TiYf.Engine.Host/EngineLoopService`, `EngineHostState`, persistence helpers (new infra folder), `deploy/` scripts for state directory, tests in `TiYf.Engine.Tests` for restart scenarios.
- **Proofs/tests:**
  - Integration test: run sim, persist keys, restart process, assert no duplicate orders and reconciliation report clean.
  - Workflow `m8-idempotency-proof.yml` that simulates crash/restart with stub broker.
- **Risks:**
  - Corrupt persistence may block sends; need checksum + fallback.
  - Determinism in tests when reading persisted timestamps.

### Phase M8-C: Realistic slippage model + updated proofs
- **Scope:**
  - Implement instrument/time-of-day slippage tables (e.g., JSON config) plus spread-based adjustments.
  - Add metrics (`engine_slippage_applied_total`, per-instrument averages) and expose chosen profile via `/health`.
  - Update simulator + proof harnesses (m6/m7 + new m8 proof) to validate that expected slippage is applied (widened PnL, correct VWAP) while keeping deterministic results.
  - **Non-scope:** No production broker changes beyond applying slippage at intent time; reconciliation+idempotency logic remains as from phases A/B.
- **Touch points:** `TiYf.Engine.Core/Slippage`, `TiYf.Engine.Sim` (fixtures + tests), host telemetry, docs describing profile tuning.
- **Proofs/tests:**
  - Unit tests for slippage calculation per instrument/time bucket.
  - Extend `m6-execution-proof` (or new `m8-slippage-proof`) to compare expected vs actual fills under the realistic model.
- **Risks:**
  - Breaking deterministic proofs if randomness introduced; keep deterministic tables.
  - Impact on trading PnL / risk calculations; coordinate with Ops before enabling in live adapters.

## Evidence expectations
- Reconciliation proof: tool output must show 100% matched decisions for sample broker log, with artifacts uploaded.
- Restart-safe proof: simulated crash/restart run demonstrating no duplicate sends and persisted idempotency counts.
- Slippage proof: deterministic replay showing applied slippage matches table expectations and updated metrics.

This plan keeps changes sequential and reviewable, allowing Ops to gate each phase independently while keeping runtime risk rails stable.
