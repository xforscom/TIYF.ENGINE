# M4 Plan (Seed Packet)

Status: Seed Draft

Baseline Tag: v0.6.0-m3-active  
Branch: feat/m4-seed

## 1. Objectives & Themes

- Schema hardening & forward/backward compatibility discipline.
- Deterministic penalty instrumentation (PENALTY_APPLIED_V1) and promotion gating impact.
- Deep verification layer (verify CLI + promotion --verify integration upgrades).
- Expanded nightly canary surface for early drift detection.
- Risk refinement: drawdown & exposure caps enforced deterministically with new alert taxonomy.

## 2. Schema Hardening & Versioning Policy

- Current journal schema version: 1.2.0 (additive sentiment events).
- Version bump rules:
  - Additive (new optional event types / payload fields): minor (1.2.x -> 1.3.0).
  - Structural / mandatory field additions or semantic change: major (2.0.0).
  - Field removal or meaning change is disallowed without major bump & migration note.
- Validation tier:
  - Strict parse: reject malformed JSON payloads early.
  - Field presence: required set per event type (BAR_V1, INFO_SENTIMENT_Z_V1, INFO_SENTIMENT_CLAMP_V1, INFO_SENTIMENT_APPLIED_V1, RISK_PROBE_V1, future penalty).
  - Numeric normalization: enforce invariant-culture decimal formatting (no scientific notation) in tests.
- Deprecation process:
  - Mark event type as DEPRECATED in RELEASE_NOTES; maintain for two minor versions before removal (requires major bump).

## 3. PENALTY_APPLIED_V1 Event

- Purpose: capture deterministic application of penalty adjustments (e.g., scaling, risk adjustments) influencing economic output.
- Emission Conditions:
  - Triggered immediately after an economic-altering penalty decision (e.g., size reduction beyond sentiment clamp or drawdown throttle) but before trade execution commit.
- Payload (draft):
  - symbol (string)
  - ts (UTC ISO 8601)
  - penalty_kind (enum: sentiment_scale, drawdown_throttle, exposure_cap, custom)
  - original_units (long)
  - adjusted_units (long)
  - rationale (string, deterministic tokens; no free-form stack traces)
  - decision_id (string) — ties to trade or risk action
  - schema_version, config_hash (shadowed for promotion diff hashing; maybe omitted if redundant — finalize after hashing tests)
- Determinism Rules:
  - Sorting of multi‑penalty scenarios deterministic via symbol + decision_id order.
  - No randomness; scaling formulas must be pure.
- Promotion Gate Impact:
  - Parity check counts and payload stable across A/B.
  - Candidate introducing new penalty types must not degrade PnL beyond documented tolerance.
- Tests:
  - Unit: serialization invariants, formatting.
  - Integration: penalty emission scenario with forced throttle.
  - Promotion: reject mismatch in applied penalty counts between candidate A and B.

## 4. Verify CLI Enhancements

- New deep checks (enable via `--strict` or `--deep`):
  - Ordering: strictly increasing sequence; monotonic non‑decreasing timestamps.
  - Gaps / Duplicates: detect missing sequence numbers or timestamp regressions.
  - Symbol Set Consistency: For multi-instrument runs, ensure consistent symbol list across BAR emissions.
  - Event Type Constraints: CLAMP/APPLIED must follow a preceding Z within same bar timestamp; PENALTY_APPLIED_V1 must follow clamp or risk evaluation event.
  - Culture: parse sample numeric fields under alternate cultures to assert invariant formatting.
- JSON Report Output:
  - `{"summary": {"passed": true, "issues": N}, "issues": [{"seq":123,"type":"ordering_gap","detail":"..."}]}`
- Deterministic Exit Codes:
  - 0 = pass; 1 = soft warnings (non-economic instrumentation drift); 2 = hard failure (economic / determinism violation).
- Promotion `--verify` integration:
  - Baseline + candidate A/B journals all verified pre-gate.
  - Decision object extended with `verification`: { baseline: {...}, candidateA: {...}, candidateB: {...} }.

## 5. Canary Expansion

- Nightly Cron: `02:15 UTC` workflow `m4-nightly-canary.yml`.
- Scenario Matrix: list file `canary/scenarios.json` enumerating config paths + feature flag permutations (sentiment modes, future penalty toggles).
- Steps:
  1. Restore + single build artifact.
  2. Run matrix scenarios collecting events/trades.
  3. Hash (excluding meta line) -> store in `canary-hash-matrix.json` artifact.
  4. Compare against prior successful run’s baseline hash file (download via artifact API).
  5. On drift: open/append GitHub Issue with diff summary & attach offending journals subset.
- Future: integrate minimal HTML diff report (phase 2).

## 6. Risk Refinements

- Drawdown Cap Enforcement:
  - Track running equity curve (from trades). If peak-to-trough > configured max drawdown, enter throttled mode.
  - Emit `ALERT_BLOCK_DRAWDOWN` when blocking an order; optionally `PENALTY_APPLIED_V1` if size partially reduced instead of full block.
- Exposure Caps:
  - Instrument Notional Cap: `ALERT_BLOCK_INSTRUMENT_NOTIONAL`.
  - Net Exposure Cap (sum across basket): `ALERT_BLOCK_NET_EXPOSURE`.
- Determinism:
  - All risk calculations pure functions; no floating rounding surprises (use decimal; consistent ordering by instrument id).
  - Add tests ensuring two identical runs produce identical risk alert hashes.
- Config Additions:
  - `risk.instrumentCaps[]` { symbol, notionalCap }
  - `risk.netExposureCap`
  - `risk.maxDrawdownPct`

## 7. Documentation & CI Updates

- README: add section “Determinism Verification & Penalties”.
- RELEASE_NOTES: outline new events & schema roadmap.
- INTERNAL: update promotion gate to add verify deep checks + penalty parity conditions.
- CI Additions:
  - `verify-deep` job: runs enhanced verify on sample journals.
  - Penalty scenario test job using synthetic config forcing penalty emission.
  - Clean tree guard reused for new jobs.

## 8. Acceptance Criteria (M4 Completion)

- All new events validated by verify strict mode.
- Promotion CLI enforces penalty parity & deep verification.
- Nightly canary stable 7 consecutive nights (no unintended hash drift).
- Risk alert taxonomy documented + tests green.
- No regression in existing 55+ tests; new tests added (target total > 65).

## 9. Milestone Exit Artifact Bundle

- Promotion decision JSON sample with verification & penalty sections.
- Canary hash matrix diff (empty) proving stability.
- Verify deep JSON output example.
- Risk penalty test journal excerpt.
- Release tag `v0.7.0-m4-seed` (or `-alpha`) created after acceptance.

## 10. Timeline (Indicative)

- Week 1: Schema validation layer + PENALTY_APPLIED_V1 skeleton + deep verify scaffolding.
- Week 2: Risk refinements & penalty integration tests.
- Week 3: Canary expansion & CI wiring + promotion integration.
- Week 4: Hardening, documentation, release candidate tagging.

---
Document created: 2025-10-08
