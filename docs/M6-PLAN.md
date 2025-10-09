# M6 Plan (Seed)

## Scope

- Schema 1.3 bump plan & freeze window
- Penalty activation rules + promotion gating matrix
- Make `promote --verify deep` the default gate (document lenient override)
- Risk reporting refinements (exposure/drawdown deltas)
- Nightly dashboards: include `events_sha` deltas
- CI: expand canary summary to include `events_sha`; keep parity artifact rules

## Details

### Schema 1.3 bump & freeze

- Propose schema 1.3 finalization window (N days).
- Freeze period: no breaking changes; only additive fields guarded by version checks.
- Update Sim/Tools to emit schema 1.3 in headers and validators to enforce.

### Penalty activation & promotion gating

- Define activation rules (behind feature flag, controlled by config).
- Promotion gating matrix:
  - active vs active: require identical penalty event counts & sequence order.
  - shadow → active: allow if penalty inactive in baseline and candidate (no penalty events).
  - any mismatch in penalty event sequence/count → reject with reason `penalty_mismatch`.

### Promote default gate: verify deep

- Make deep verify the default in promotion CLI gates.
- Allow `--lenient` override to use strict verify for CI smoke scenarios.

### Risk reporting refinements

- Add exposure and drawdown delta series to INFO_RISK_EVAL_V1 payload.
- Summarize run-level deltas in a `RISK_SUMMARY_V1` at end of run.

### Nightly dashboards & CI

- Nightly: include events_sha deltas relative to last successful main build.
- Canary summary: show events_sha and trades_sha parity, counts of applied/clamps/penalties.

## Tasks

- [ ] Schema version bump plumbing and validators
- [ ] Penalty activation toggles + event sequencing invariants
- [ ] Promotion CLI default `--verify deep`; lenient path
- [ ] Risk reporting deltas + summary event
- [ ] Dashboards & CI summary expansion
