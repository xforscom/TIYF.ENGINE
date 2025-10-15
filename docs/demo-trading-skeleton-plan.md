# Demo Trading Skeleton Plan

## Goal

Establish a paper/demo trading harness that exercises the engine without touching live venues while preserving the existing determinism + verification guarantees.

## Components

- **Data feed adapter stub**
  - Deterministic clock surface (simulated wall clock, pause/resume hooks).
  - Bar ingestion stub that replays fixture bars into the engine loop.
  - Injectable source so backtests, demo mode, and future live adapters share the same surface area.
- **Broker simulator stub**
  - Accepts submit/confirm/cancel requests and returns deterministic fills.
  - Journals trades in the existing format so verify strict/parity work unchanged.
  - Supports configurable fill latency + slippage knobs for future experimentation.
- **Safety rails**
  - Reuse `verify strict` and `verify parity` on every demo journal before promoting runs.
  - Emit env.sanity snapshots + parity hashes for triage parity with CI harness.
- **Dry-run workflow**
  - Minimal GitHub Actions workflow that builds Release, runs the stubs, and uploads journals + sanity files.
  - Manual trigger only; no branch protection.

## Work Breakdown

1. Author interface contracts for feed adapter + broker simulator stubs (no live wiring yet).
2. Provide deterministic fixture data + configuration for demo mode.
3. Implement journaling glue so the stubs reuse existing writers.
4. Add CLI entrypoint or script to launch demo mode locally.
5. Create the dry-run workflow mirroring the local script; capture artifacts for parity checks.
6. Document the workflow + triage process in `README.md` / operations guides.

## Open Questions

- How much latency/slippage configurability do we need up front?
- Should demo mode share config schema with backtests or have a dedicated section?
- Is there value in journaling synthetic balance snapshots for demo reviewers?

## Definition of Done

- Draft PR `feat/demo-trading-skeleton` contains this plan doc only.
- Consensus on plan from reviewers (sign-off or tracked follow-ups).
- Follow-up issues filed for implementation tasks deriving from the plan.
