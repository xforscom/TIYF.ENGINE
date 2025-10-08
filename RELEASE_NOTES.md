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
