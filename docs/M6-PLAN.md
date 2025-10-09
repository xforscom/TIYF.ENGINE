# M6 Plan (Draft)

Date: 2025-10-09

Scope

- Schema 1.3 stabilization
- Penalty: move from scaffold → active with deterministic impact and promotion gating
- Promote integration: `promote --verify deep` required gate for candidates (exit 2 on fail)
- Risk extensions: exposure/reporting refinements (per-symbol net exposure tracking, refined drawdown metrics)
- Canary dashboards / summaries: richer nightly summaries and optional dashboard artifact (e.g., markdown tables + sparkline JSON)

Goals

1. Deep Verify Everywhere

- Make `--verify deep` the default candidate gate in Promote CLI (opt-out only in exceptional cases)
- Extend deep checks with optional sampling for large journals (keep exit-code semantics 0/2/1)

1. Penalty Activation

- Replace `ciPenaltyScaffold` with real, deterministic penalty emission in active mode
- Promotion gating: reject if candidate introduces penalties not present in baseline unless justified (config flag)
- Invariants: nightly enforces penalty-active has penalty_count ≥ 1 and preserves off/shadow/active parity where applicable

1. Risk Extensions

- Add exposure snapshots per bar to support audit
- Clarify and log max drawdown calculation with explicit timestamps
- Update strict verifier allow-list accordingly

1. CI & Reporting

- Nightly job summary includes: events/trades counts, applied_count, penalty_count, and first-diff hint when parity mismatches
- Optional artifact: `artifacts/reports/nightly-summary.md` with aggregated table across modes

1. Docs & Samples

- README: document `verify deep` (usage + exit codes), promotion deep gate, nightly invariants
- RELEASE_NOTES: add M6 highlights once shipped

Milestones

- M6a: wire promote `--verify deep` as default gate; extend verifier for size-efficient modes
- M6b: activate penalty (deterministic), integrate promotion gating and nightly invariant
- M6c: risk reporting refinements + docs
- M6d: CI dashboards and final polish

Acceptance

- CI green: verify-deep + nightly-canary
- Promotion with deep gate accepts happy path; rejects when deep verify fails (exit 2, reason=verify_failed)
- Parity invariants hold; penalty-active produces ≥1 penalty event
- Docs reflect new gates and invariants
