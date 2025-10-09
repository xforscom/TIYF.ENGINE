# Release Notes

## v0.4.0-m1-promotion (M1 Promotion & Rollback)

Highlights:

- Promote CLI (accept / reject) with deterministic exit codes (0 accept, 2 reject, 1 error)
- A/B parity enforcement via canonical CSV + SHA-256 hashes (events & trades)
- Culture-invariant, atomic journaling (no wall-clock usage in promotion journal)
- CI workflow (`m1-promotion.yml`) executes accept + reject scenarios and uploads artifacts
- Deterministic promotion journal: PROMOTION_BEGIN_V1, PROMOTION_GATES_V1, PROMOTION_ACCEPTED_V1 | PROMOTION_REJECTED_V1 (+ ROLLBACK_* on reject)
- Decision file (`promotion_decision.json`) containing acceptance outcome and hashes

Quality Gates:

- Build & Tests: PASS
- Determinism: PASS (culture en-US vs de-DE identical promotion.events.csv hash)
- Safety: PASS (M0 fixture emits zero ALERT_BLOCK_* events)

Usage snippet:

```powershell
dotnet run --project src/TiYf.Engine.Tools -- promote run `
  --config tests/fixtures/backtest_m0/promotion.json `
  --output artifacts/m1_promo
```

See README section "Promotion (M1)" for details.

## v0.5.0-m2-shadow (M2 Shadow Instrumentation)

Highlights:

- Early Data QA tolerance pipeline (Analyze -> ApplyTolerance -> Journal -> Gate) with pure analyzer and clean pass path (no DATA_QA_ABORT_V1 when all issues filtered)
- Sentiment volatility guard (shadow mode) emitting INFO_SENTIMENT_Z_V1 and conditional INFO_SENTIMENT_CLAMP_V1 (observability only)
- Deterministic ordering: BAR_V1 precedes sentiment events (Z then optional CLAMP same timestamp)
- Formatting invariants codified via NotionalScaleFormattingTests (pnl two decimals, integer volumes, stable per-symbol price precision, no scientific notation)
- Sentiment event numeric formatting enforced (no scientific notation, invariant decimal separator)
- Non-impact guarantee: trades economic content unchanged between baseline (sentiment disabled) and shadow (normalized hash parity ignoring config_hash)
- CI workflow m2-shadow.yml validating Data QA pass + presence of sentiment events + clamp scenario

Quality Gates:

- Build & Tests: PASS
- Determinism: PASS (shadow A/B events hash and trades hash parity)
- Data QA: PASS (fixture yields DATA_QA_SUMMARY_V1 passed=true)
- Sentiment Clamp: PASS (ultra-low sigma produces at least one INFO_SENTIMENT_CLAMP_V1)

Enable Shadow Example:

```json
{
  "featureFlags": { "sentiment": "shadow" },
  "sentimentConfig": { "window": 10, "volGuardSigma": 0.20 }
}
```

Sample Lines:

```text
DATA_QA_SUMMARY_V1,{"issues_total":0,"passed":true,...}
INFO_SENTIMENT_Z_V1,{"symbol":"EURUSD","s_raw":0.000123,"z":0.00,"sigma":0.00005,"ts":"2025-01-02T00:15:00Z"}
INFO_SENTIMENT_CLAMP_V1,{"symbol":"EURUSD","reason":"volatility_guard","ts":"2025-01-02T00:15:00Z"}
```

## v0.6.0-m3-active (Sentiment Active Mode & Extended Gating) – 2025-10-08

Highlights:

- Sentiment Active Mode introducing impactful scaling with deterministic rule `adjusted_units = max(1, floor(original_units * 0.5))` on clamped bars.
- New event `INFO_SENTIMENT_APPLIED_V1` emitted only in active mode when a clamp triggers and scaling occurs.
- Promotion decision JSON extended with `sentiment` block:
  - `baseline_mode`, `candidate_mode`, `parity`, `reason`, `diff_hint`.
- Gating logic:
  - active vs active (identical applied count & events) → accept
  - shadow → active (no clamps) → accept
  - active vs off|shadow → reject (`sentiment_mismatch`)
  - active vs active (APPLIED count or event sequence mismatch) → reject (`sentiment_mismatch`)
- Data QA active enhancements retained (summary / pass vs abort). Sentiment gating occurs after data QA failure short‑circuit.
- Determinism improvements consolidated:
  - Ordered builders / deterministic risk probe IDs
  - Culture invariance (numeric formatting invariant; test coverage under `de-DE`)
  - Atomic journaling & hash parity for candidate A/B runs
- New tests:
  - Engine: Active influence, shadow vs off parity, active vs shadow controlled diff, active determinism, formatting invariance
  - Promotion CLI: four sentiment gating scenarios + culture path
- CI (planned) `m3-sentiment-matrix.yml` validating cross-mode invariants (off|shadow|active).

Example promotion sentiment block:

```json
"sentiment": {
  "baseline_mode": "active",
  "candidate_mode": "active",
  "parity": true,
  "reason": "ok",
  "diff_hint": ""
}
```

Status: Final.

### Bug fixes (post-release patch)

- fix(dataqa): apply per-symbol missing_bar tolerance deterministically (drop up to K by ts)

## v0.7.0-m4-parity (Strict Verifier, Penalty Scaffold, Parity Artifacts) – 2025-10-08

Highlights:

- Strict Verifier CLI (`verify strict`) with deterministic exit codes:
  - `0` success (no violations)
  - `2` validation failures (structured JSON detail)
  - `1` reserved for runtime / internal errors
- Penalty Scaffold (`PENALTY_APPLIED_V1`) behind feature flag + optional `forcePenalty` config. Emits observability events without altering trade economics (hash parity preserved unless penalty explicitly counted for invariants gating of active mode differences).
- Parity Artifacts: post‑run artifact‐only parity hashes at `artifacts/parity/<run-id>/hashes.txt` containing:
  - `events_sha`, `trades_sha` (normalized; `config_hash` column removed in trades)
  - `applied_count` (sentiment active scaling occurrences)
  - `penalty_count` (penalty scaffold occurrences)
- CI Hardening:
  - Matrix & invariants workflows glob artifact hashes (no hard‑coded run IDs)
  - Invariants enforce: off vs shadow trade hash parity; active divergence only if `applied_count>0 || penalty_count>0`.
  - Diagnostic printing of discovered run directories & parity hash files.

Quality Gates:

- Build & Tests: PASS (all engine + tools tests green; determinism tests unchanged)
- Strict Verify: PASS on M0 fixture (exit 0) under Release build
- Parity: PASS (off ↔ shadow equality; controlled active divergence scenarios only)
- Penalty: PASS (≥1 `PENALTY_APPLIED_V1` when forced)

Upgrade Notes:

- Journals remain schema‑stable (no new parity event lines); tooling consuming event streams requires no change.
- Promotion / invariants consumers should prefer artifact parity hashes over ad‑hoc recomputation for speed and uniformity.

Status: Final.

## v0.8.0-m4-risk (Deterministic Risk Guardrails & Promotion Gating) – 2025-10-08

Highlights:

- Risk guardrails (net exposure & run drawdown) with tri‑state mode: off | shadow | active.
- Evaluation event `INFO_RISK_EVAL_V1` (per-symbol monotonic eval_index) always precedes any blocking alerts.
- Alert events: `ALERT_BLOCK_NET_EXPOSURE`, `ALERT_BLOCK_DRAWDOWN` emitted in shadow (observability) and active (enforcement) modes.
- Deterministic ordering: BAR / sentiment → risk evaluation → alerts → trade (or suppression in active).
- Deterministic drawdown breach via `forceDrawdownAfterEvals` test hook (synthetic equity reduction pre-eval) enabling reproducible alert tests.
- Exposure breach logic uses `>=` for deterministic zero-cap promotion gating scenarios.
- Promotion CLI extended with risk parity block (baseline/candidate eval & alert counts, modes, parity flag, diff_hint).
- Gating rules: reject downgrades (active→shadow/off), reject introduction of alerts in shadow→active upgrade, reject zero-cap introduction; accept benign shadow→active (no alerts) and parity off↔shadow.
- New tests:
  - Parity: off vs shadow trades hash identity.
  - Active exposure blocking (alert + suppression) reproducibility.
  - Deterministic forced drawdown alert.
  - Event ordering (INFO_RISK_EVAL precedes alerts) invariant.
  - Promotion gating scenarios (benign upgrade accept, downgrade reject, forced mismatch reject).
- CI `m4-risk-matrix.yml` workflow (build once → matrix modes: off, shadow, active_no_breach, active_with_breach) + invariants job asserting hash parity and conditional divergence only when alerts present.

Quality Gates:

- Build & Tests: PASS (all new risk tests green).
- Determinism: PASS (forced drawdown & exposure breach deterministic across runs).
- Promotion Parity: PASS (benign upgrade accepted, mismatches rejected with informative diff_hint).
- CI Matrix: Authored (pending first pipeline run outside local context).

Upgrade Notes:

- No breaking schema changes; risk events added to strict verifier allow‑list.
- `forceDrawdownAfterEvals` is a non-production test facility and should not appear in production configs.
- Off ↔ Shadow parity (trades) is a guarded invariant; any future change impacting economics in shadow will break CI invariants.
- Active divergence must be justified by a blocking alert; CI enforces this via parity artifacts (`applied_count` / `penalty_count` not used for risk but infrastructure reused).

Status: Final.

## v0.9.0-m5-penalty-verify (Penalty Activation & Deep Verify) – 2025-10-09

Highlights:

- Deep Verifier CLI: `verify deep --events <events.csv> --trades <trades.csv> [--json]` producing a structured JSON report.
- Promotion `--verify deep`: optional deep verification pass on candidate A; reject with `reason="verify_failed"` and exit 2 if it fails.
- Tools tests updated to read `TiYf.Engine.Core.Infrastructure.Schema.Version` instead of hardcoding schema strings; CRLF/LF tolerant reads; stdout-based journal path discovery retained.
- CI workflow `verify-deep.yml`: runs on push/PR across Linux + Windows, uses Release build with Debug fallback, uploads JSON report; includes a clean-tree guard.
- Nightly canary `nightly-canary.yml`: daily at 05:00 UTC and manual dispatch; matrix over off, shadow, active, penalty-active; publishes parity artifacts and a compact summary table; adds a job summary with the CSV.

Quality Gates:

- Build & Tests: PASS (Tools tests include promotion verify positive/negative cases).
- Deep Verify: PASS on M0 fixture (exit 0; JSON artifact uploaded by CI).
- Hygiene: Tests clean up generated run folders best-effort; docs updated.

Upgrade Notes:

- Consumers can adopt `verify deep` for stricter CI gates; `promote --verify deep` integrates this automatically for candidates.
- Tests and scripts should avoid hardcoded schema strings; use `Schema.Version` from Core.

Status: Final.
