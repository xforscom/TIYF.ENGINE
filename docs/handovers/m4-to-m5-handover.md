# M4 → M5 Handover (Context Pack)

This note captures the minimal, durable context to continue work in a fresh chat without losing fidelity.

## Baseline

- Tag (M4 release): `v0.8.0-m4-risk`
- Branch at tag: `feat/m4-seed`
- Head at time of tag: `23cc832` (short)
- Acceptance bundle: `docs/acceptance/m4-risk-acceptance.md`

## CI Workflows (authoritative)

- Risk Matrix: `.github/workflows/m4-risk-matrix.yml`
  - Modes: `off`, `shadow`, `active_no_breach`, `active_with_breach`
  - For `active_with_breach` the config forces `EURUSD` exposure cap = 0 to trigger gating.
  - Artifacts: `artifacts/parity/<mode>/hashes.txt` (+ `hashes-<mode>.txt`), fields include:
    - `events_sha`, `trades_sha`, `alert_count`, optional `gated_zero_cap`, `run_dir`
- Strict Verifier: `.github/workflows/verify-strict.yml`
  - Builds Release; runs Sim once with fixture; runs Tools `verify strict` on generated `events.csv`/`trades.csv` (schema 1.2.0)
  - Robust Tools path: Release fallback to Debug if Release DLL missing
  - Uploads `artifacts/verify-strict/` with the CSVs and JSON output
- Penalty scaffold: `.github/workflows/penalty-scaffold.yml`
  - Ensures a penalty artifact is produced when needed (used for CI parity visibility)
- Standard CI: `.github/workflows/ci.yml`

## Engine Conventions (stabilized in M4)

- Risk config canonical keys (Core):
  - `emitEvaluations`, `blockOnBreach`, `maxRunDrawdownCCY`, `maxNetExposureBySymbol`, `forceDrawdownAfterEvals`
- Modes and invariants:
  - `off` ≡ `shadow` for trade parity (same `trades_sha`)
  - `shadow` ≡ `active_no_breach` for trade parity
  - `active_with_breach` diverges; must have `alert_count>0` or `gated_zero_cap=true`
- Determinism:
  - M0 run-id policy in Sim; mutex around journal dir; typical IDs like `M0-RUN-STRICT-CI` or `M0-RUN-MATRIX-<mode>-<seed>`
- Diagnostics (log lines to grep):
  - RUN_ID: `RUN_ID_RESOLVED=...`
  - Journals: `JOURNAL_DIR_EVENTS=...`, `JOURNAL_DIR_TRADES=...`
  - Promotion: `PROMOTE_FACTS ...`, `PROMOTE_DECISION ...`
  - Matrix artifacts: `events_sha=...`, `trades_sha=...`, `alert_count=...`, `gated_zero_cap=true|false`
  - Verify strict captures JSON; non-zero exit means violation

## Promotion Behavior (risk-related)

- Fact-first resolver with explicit reasons
- Shadow → Active with zero-cap is rejected with reason `risk_mismatch`
- Decision diagnostics present in logs (see Acceptance doc examples)

## Parity Artifact Normalization

- Events: skip meta + header rows before hashing
- Trades: drop header and remove `config_hash` column by name before hashing
- Hashes are SHA-256 uppercase across LF-normalized content

## Fixtures & Paths

- Backtest fixture: `tests/fixtures/backtest_m0/config.backtest-m0.json`
- Journals during CI: `journals/M0/<run-id>/events.csv|trades.csv`

## Known Edge Cases (covered)

- Tools built in Debug under Release build: verify-strict falls back to Debug path
- Artifact layout differences (merged vs per-mode dirs): invariants handles both via `hashes.txt` and `hashes-<mode>.txt`

## Starting M5 (Proposed Scope)

- Schema bump to 1.3
  - Define deltas from 1.2.0; update Tools verifier and any CSV emit if needed
  - Keep backward compatibility in parser until rollout
- Penalty activation
  - Move from CI-only scaffold to controlled activation flag (env/config); ensure gating and tests
  - Extend parity artifacts to include penalty counts consistently
- Verify “deep” mode
  - Add richer checks in Tools (cross-file constraints, ordering rules, extended risk invariants)
  - JSON report with machine-readable counters and reasons

## Minimal Context to Paste in New Chat

- Base tag and SHA: `v0.8.0-m4-risk` at `23cc832`
- Pointer to acceptance doc: `docs/acceptance/m4-risk-acceptance.md`
- Workflows to watch/edit: `m4-risk-matrix.yml`, `verify-strict.yml`, `penalty-scaffold.yml`, `ci.yml`
- Goals for M5: schema 1.3, penalty activation, deep verify
- Any open decisions or constraints specific to your environment (add here if needed)

---
This file is the canonical handover. Update it if scope adjusts.
