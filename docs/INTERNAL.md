# INTERNAL POLICIES

> Visibility: Private (since 2025-10-07). Distribution outside authorized collaborators is prohibited.

## Access & Roles

- Maintainers: Merge to `main`, create release tags, approve promotions.
- Contributors: Feature branches only (prefix `feat/`, `fix/`, `chore/`, `exp/`).
- Observers: Read-only; may not run promotions or alter CI.

## Branching Model

- `main`: Stable, promotion‑gated. Every merge must pass: tests, smoke determinism, promotion (if feature alters engine output), and verifier.
- Feature branches: Short‑lived; rebase (preferred) or merge from `main` at least daily.
- Tags: `v<semver>-m<n>-<qualifier>` (e.g. `v0.5.0-m2-shadow`).

## Promotion Gate (Baseline → Candidate)

1. Run baseline once.
2. Run candidate twice (A/B); require SHA-256 parity of events & trades.
3. Gates:
   - PnL >= baseline - epsilon.
   - MaxDD <= baseline + epsilon.
   - No new ALERT_* (unless intentionally introducing enforcement feature; must be documented).
   - Trade row count invariant for canonical fixtures.
4. Decision journal must contain `PROMOTION_BEGIN_V1`, `PROMOTION_GATES_V1`, and terminal ACCEPT/REJECT event.

## Determinism Requirements

- UTC timestamps only.
- No ambient static state for business logic; favor injected abstractions.
- Hash normalization rules documented for any tolerated non-economic deltas.
- New event types require schema version bump or additive-safe extension.

## Data QA (Shadow → Enforced Roadmap)

- Current: Shadow mode logs issues and summary with `passed=true` when tolerated.
- Enforce Plan (M3 candidate): Abort run on un-tolerated severity; promotion rejects if Data QA fails.

## Sentiment Volatility Guard

- Shadow-only (M2). Emits `INFO_SENTIMENT_Z_V1` and conditional `INFO_SENTIMENT_CLAMP_V1`.
- Graduation Plan: Introduce enforcement flag; clamp modifies downstream signal after proving non-regression across 30 shadow canary runs.

## Risk Enforcement Events

Allowed events (verifier allow-list):
`ALERT_BLOCK_LEVERAGE`, `ALERT_BLOCK_MARGIN`, `ALERT_BLOCK_RISK_CAP`, `ALERT_BLOCK_BASKET`, `INFO_SCALE_TO_FIT`.

## CI Layers

1. Unit & integration tests.
2. Shadow instrumentation tests (sentiment, Data QA pass, clamp scenario).
3. Smoke determinism script (dual run diff + verify).
4. (Planned M3) Nightly canary: historical dataset re-run; hash regression dashboard.

## Code Review Checklist

- Determinism preserved? (dual run locally optional but encouraged)
- Journaling changes: schema impact considered? Backward compatibility?
- Numeric formatting invariant (InvariantCulture) preserved?
- New risk / alert path validated by verifier allow-list if applicable.
- Tests cover both happy path and structural failure (where feasible).

## Security & Confidentiality

- No external paste of source or proprietary journals.
- Secrets (if later introduced) reside only in secure CI store (never committed).
- Immediate rotation if accidental exposure occurs.

## Release Notes

- Update `RELEASE_NOTES.md` per milestone (summary + key instrumentation changes + gating evolution).

## Future Enhancements (Candidate for M3 Planning Doc)

- Data QA enforcement mode.
- Sentiment guard promotion.
- Risk exposure caps refinement (basket vs instrument granularity).
- Verify CLI `--strict` mode (enforce absence of allow-list alerts unless flagged).
- Canary historical dataset workflow.

---
Document version: 2025-10-07 initial draft.
