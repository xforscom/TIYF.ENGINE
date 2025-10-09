# TiYf Engine

> Private Repository – Internal Use Only

Modular monolith trading engine MVP (M0). Focus: deterministic replay, bar building, instrument catalog, atomic journaling. As of v0.5.0 (post‑M2 shadow instrumentation) this repository is PRIVATE; distribution outside the approved organization list is prohibited. See `docs/INTERNAL.md` for collaboration, branching, and promotion gate policies.

## Structure

- `src/TiYf.Engine.Core` – Pure domain abstractions (clock, bars, instruments, risk interfaces)
- `src/TiYf.Engine.Sim` – Engine loop & simulation harness
- `src/TiYf.Engine.Sidecar` – File adapters (CSV ingestion, journaling, config hash)
- `tests/TiYf.Engine.Tests` – Unit & early integration tests
- `docs/adr` – Architecture decision records

## Run (Prereq: .NET 8 SDK)

```powershell
# Build & test
 dotnet build
 dotnet test

# (After engine entrypoint added)
 dotnet run --project src/TiYf.Engine.Sim -- --config .\sample-config.json
```

## Goals (M0)

1. Deterministic clock (test seam)  
2. Bar builder skeleton (O/H/L/C + volume)  
3. Instrument catalog (lookup + validate)  
4. Risk rails skeleton (interface only)  
5. Atomic journaling (append-only, schema_version, config_hash)

## Determinism Principles

- UTC-only timestamps
- Pure functions in Core
- No ambient static state; injectable services
- Replay = identical outputs (bars + journals) given same inputs

## Journaling Format (initial)

- Path: `journals/{run_id}/events.csv`
- Header includes: `schema_version,config_hash,sequence,utc_ts,event_type,payload_json`
- Atomic write via temp file + move or fsync flush (platform dependent)

## Verify CLI

Integrity checker for journal files.

Usage (PowerShell):

```powershell
dotnet run --project src/TiYf.Engine.Tools -- verify --file journals/RUN/events.csv
```

Options:

- `--json` emit structured JSON result
- `--max-errors N` cap reported validation issues (default 50)
- `--report-duplicates` include duplicate composite key findings

Exit codes:

- `0` OK
- `1` Validation issues found
- `2` Fatal error (I/O, malformed meta/header, etc.)

Example diff usage (for regression comparison):

```powershell
dotnet run --project src/TiYf.Engine.Tools -- diff --a journals/BASE/events.csv --b journals/NEW/events.csv --report-duplicates
```

## Risk Enforcement Events

When risk enforcement is enabled, additional alert/scaling events are journaled. These are whitelisted by the Verify CLI (only basic structural checks applied beyond UTC timestamp + valid JSON).

Event types & payload semantics (all numeric values culture-invariant decimals; timestamps ISO-8601 UTC):

| Event Type | Meaning | Observed Field | Cap Field |
|------------|---------|----------------|-----------|
| `ALERT_BLOCK_LEVERAGE` | Proposed trade blocked: projected leverage exceeded cap | `Observed` = projected leverage | `Cap` = leverage cap |
| `ALERT_BLOCK_MARGIN` | Proposed trade blocked: projected margin usage % exceeded cap | `Observed` = projected margin usage % | `Cap` = margin usage cap % |
| `ALERT_BLOCK_RISK_CAP` | Proposed trade blocked: per-position risk % exceeded cap | `Observed` = per-position risk % | `Cap` = per-position risk cap % |
| `ALERT_BLOCK_BASKET` | Proposed trade blocked: basket risk % exceeded cap | `Observed` = basket risk % | `Cap` = basket risk cap % (currently same as per-position cap) |
| `INFO_SCALE_TO_FIT` | Order scaled down to fit all caps (Allowed=true) | (Original leverage in `Observed`) | `Cap` = leverage cap |

Alert payload base fields:

```text
DecisionId, InstrumentId, Reason, Observed, Cap, Equity, NotionalValue, UsedMarginAfterOrder, SchemaVersion, ConfigHash
```

Scale event (INFO_SCALE_TO_FIT) additionally implies computed scale ratios (present in message `Reason`) and post-round projected metrics internally tracked; future versions may surface `ScaledVolumeBeforeRound`, `ScaledVolumeRounded`, `PostRoundProjectedLeverage`, `PostRoundProjectedMarginUsagePct` explicitly in the payload.

### Verify CLI Allow-List

The verifier enforces strict structural checks for `BAR_V1` and `RISK_PROBE_V1`. It allows (does not error on) these risk events: `ALERT_BLOCK_LEVERAGE`, `ALERT_BLOCK_MARGIN`, `ALERT_BLOCK_RISK_CAP`, `ALERT_BLOCK_BASKET`, `INFO_SCALE_TO_FIT`.

### Diff Keys Guidance

Suggested composite keys:

- Bars: `instrumentId,intervalSeconds,openTimeUtc,eventType`
- Risk probes / alerts / scale events: `instrumentId,eventType,utc_ts`

This balances uniqueness with readability while tolerating absence of bar interval fields in non-bar events.

## Smoke Script (Developer Convenience)

The PowerShell script at `scripts/smoke.ps1` provides a fast local determinism & integrity check before pushing:

Steps executed:

1. Build (no restore) and test.
2. Run simulator twice with the same config producing `journals/SMOKE_A/events.csv` and `journals/SMOKE_B/events.csv`.
3. Verify journal A with the Verify CLI (exit code must be 0).
4. Diff journal A vs B (must report zero differences and exit 0).

PASS Criteria (all must hold):

- Tests pass.
- Verify on A returns exit code 0.
- Diff A vs B returns exit code 0 (bit‑exact deterministic replay for event stream).

Usage:

```powershell
pwsh ./scripts/smoke.ps1            # or powershell on Windows
pwsh ./scripts/smoke.ps1 -Config sample-config.json
```

On success it prints: `PASS (tests+verify+diff deterministic)`.

If any stage fails, the script exits non-zero with a descriptive bracketed tag (e.g. `[SMOKE] Diff FAILED`).

## Promotion (M1)

Deterministic promotion gating between a baseline config and a candidate.

Runs baseline once and candidate twice (A/B) enforcing:

- A/B determinism (events.csv + trades.csv SHA-256 parity)
- Safety: zero ALERT_BLOCK_* events (if required)
- Performance gates: pnl_cand >= pnl_base - epsilon; maxDD_cand <= maxDD_base + epsilon
- Trade row count invariant for fixture (6 rows)

### Command

```powershell
dotnet run --project src/TiYf.Engine.Tools -- promote run \
  --config tests/fixtures/backtest_m0/promotion.json \
  --output artifacts/m1_promo
```

Add `--culture de-DE` to assert culture invariance.

### Exit Codes

0 = accepted | 2 = rejected | 1 = error (unexpected)

### Artifacts (atomic, culture-invariant)

- `promotion.events.csv` (schema_version=1.1.0,promotion_journal=1)
  - PROMOTION_BEGIN_V1
  - PROMOTION_GATES_V1
  - PROMOTION_ACCEPTED_V1 or PROMOTION_REJECTED_V1
  - ROLLBACK_* (only if rejected)
- `promotion_decision.json` (accepted, reason, baseline & candidate hashes)

Fixture: `tests/fixtures/backtest_m0/promotion.json`

### Determinism Guarantees

- Timestamp derived from first baseline trade (no wall clock)
- Atomic temp -> move writes
- Invariant numeric formatting (InvariantCulture)
- SHA-256 parity checks ensure candidate A/B identity

## Shadow Instrumentation (M2)

Introduces two shadow-only capabilities that do not alter trading outcomes:

1. Data QA Early Tolerance Pipeline
   - Order: Analyze (pure) -> ApplyTolerance (drop/suppress issues) -> Journal -> Gate.
   - If all issues filtered, emits DATA_QA_BEGIN_V1 / zero DATA_QA_ISSUE_V1 rows / DATA_QA_SUMMARY_V1 with `"passed":true` and NO DATA_QA_ABORT_V1.
   - Tolerance parameters (example high thresholds) neutralize known fixture gaps:
     - `maxMissingBarsPerInstrument >= 999`
     - `allowDuplicates = true`
     - `spikeZ >= 50` (or `dropSpikes=false`)
   - Analyzer stays pure (no embedded suppression logic) ensuring reproducible scan semantics.

2. Sentiment Volatility Guard (shadow mode)
   - Enable via `featureFlags.sentiment = "shadow"` plus optional `sentimentConfig` block:

```json
{
  "featureFlags": { "sentiment": "shadow" },
  "sentimentConfig": { "window": 10, "volGuardSigma": 0.20 }
}
```

- Emits per bar:
  - `INFO_SENTIMENT_Z_V1` { symbol, s_raw, z, sigma, ts }
  - `INFO_SENTIMENT_CLAMP_V1` { symbol, reason="volatility_guard", ts } when `sigma > volGuardSigma`.
- Ordering: BAR_V1 -> INFO_SENTIMENT_Z_V1 -> (optional) INFO_SENTIMENT_CLAMP_V1 using same bar timestamp for temporal continuity.
- Pure observability: trading logic & trades.csv unchanged (non-impact proven by normalized trades hash parity baseline vs shadow).

### Formatting & Determinism Invariants (Extended)

- `trades.csv` shape-only formatting tests enforce:
  - `pnl_ccy` exactly two decimals (`^-?\d+\.\d{2}$`).
  - Integer `volume_units` (no decimal, parseable as long).
  - Stable price precision per symbol (captured from first occurrence).
  - No scientific notation, no thousands separators (InvariantCulture only).
- Sentiment event numeric payloads: no scientific notation; decimal point = `.`.
- Shadow runs: deterministic SHA-256 parity on events (including sentiment) and trades (economic content) for identical configs.

### Sample Event Lines

```text
DATA_QA_SUMMARY_V1,{"issues_total":0,"passed":true,...}
INFO_SENTIMENT_Z_V1,{"symbol":"EURUSD","s_raw":0.000123,"z":0.00,"sigma":0.00005,"ts":"2025-01-02T00:15:00Z"}
INFO_SENTIMENT_CLAMP_V1,{"symbol":"EURUSD","reason":"volatility_guard","ts":"2025-01-02T00:15:00Z"}
```

### CI (m2-shadow.yml)

- Builds & runs full test suite.
- Executes two shadow simulations: normal sigma (presence of Z events) and ultra-low sigma (ensures at least one CLAMP event).
- Asserts clean Data QA pass (`"passed":true`).

See `RELEASE_NOTES.md` v0.5.0 entry for release highlights.

## Sentiment Active Mode (M3)

M3 elevates the previously shadow‑only sentiment volatility guard into an optionally impactful feature with deterministic scaling on clamped bars. Schema version: 1.2.0.

### Feature Flag States

| Config Value | Normalized Mode | Impact on Trades | Events Emitted |
|--------------|-----------------|------------------|----------------|
| `off` / `disabled` / `none` | off | None | (no sentiment events) |
| `shadow` | shadow | None | `INFO_SENTIMENT_Z_V1`, optional `INFO_SENTIMENT_CLAMP_V1` |
| `active` | active | Yes (on clamped bars only) | `INFO_SENTIMENT_Z_V1`, optional `INFO_SENTIMENT_CLAMP_V1`, `INFO_SENTIMENT_APPLIED_V1` (only when a clamp triggers and scaling occurs) |

The engine normalizes `disabled` and `none` to `off`. Default when omitted: `shadow` (observability without impact).

### Event Ordering (Per Bar)

```text
BAR_V1 → INFO_SENTIMENT_Z_V1 → [INFO_SENTIMENT_CLAMP_V1] → [INFO_SENTIMENT_APPLIED_V1 (active only)]
```

`INFO_SENTIMENT_APPLIED_V1` is emitted only in active mode and only when a clamp is in effect for that bar (i.e. the volatility guard threshold was exceeded and scaling is performed).

### Deterministic Scaling Rule

When in `active` mode and a clamp triggers, open‑side unit sizing is deterministically scaled:

```text
adjusted_units = max(1, floor(original_units * 0.5))
```

Applied only for that bar’s affected opens; existing positions are not retroactively altered. The scaling formula is purely arithmetic (no randomness), guaranteeing A/B determinism.

### Promotion Gating (Sentiment Parity)

The Promote CLI now emits a `sentiment` block in the decision JSON:

```jsonc
"sentiment": {
  "baseline_mode": "active|shadow|off",
  "candidate_mode": "active|shadow|off",
  "parity": true,
  "reason": "ok|sentiment_mismatch",
  "diff_hint": "A:<line>|B:<line> | applied_count base=.. cand=.."
}
```

Acceptance matrix:

| Scenario | Result | Notes |
|----------|--------|-------|
| active vs active (identical applied count & events) | accept | `parity=true` |
| shadow → active (no clamps so no APPLIED events) | accept | Benign upgrade (no trade impact) |
| active vs off or active vs shadow | reject (sentiment_mismatch) | Impactful vs non‑impactful divergence |
| active vs active (different `INFO_SENTIMENT_APPLIED_V1` count or differing sentiment event sequence) | reject (sentiment_mismatch) | `diff_hint` indicates first difference or applied count mismatch |

`diff_hint` truncates long lines and includes either the divergent pair or an applied count summary.

### Data QA (Recap with Active Mode)

Active Data QA continues to journal `DATA_QA_SUMMARY_V1` (with `passed` and tolerance metrics) and may emit `DATA_QA_ABORT_V1` when failing hard gates. The promotion decision continues to gate on `data_qa_failed` before sentiment parity is evaluated.

### Determinism Guarantees (Extended)

- Candidate A/B runs must produce identical `events.csv` & `trades.csv` hashes (including sentiment events).
- Culture invariant (`--culture de-DE` yields same hashes and parse success).
- Atomic journaling preserved (temp file then move).
- No run‑id, machine path, or locale leakage inside JSON payload fields (only deterministic domain data).
- Sentiment scaling formula ensures that repeated runs with identical inputs produce identical adjusted unit counts.

### Enabling Active Mode

```json
{
  "featureFlags": { "sentiment": "active", "riskProbe": "disabled" },
  "sentimentConfig": { "window": 5, "volGuardSigma": 0.0000001 }
}
```

For a benign upgrade test (no clamps): use a very large `volGuardSigma` (e.g. `99999`).

### Sample Applied Event

```text
INFO_SENTIMENT_APPLIED_V1,{"symbol":"EURUSD","reason":"volatility_guard","scaled_from":200,"scaled_to":100,"ts":"2025-01-02T00:20:00Z"}
```

(`scaled_from` / `scaled_to` illustrative – actual field names may evolve; deterministic numeric formatting applies.)

### CI Matrix (m3-sentiment-matrix.yml)

Lightweight workflow runs the M0 fixture under modes `off`, `shadow`, `active` asserting:

- off vs shadow: `trades.csv` hash parity; shadow has Z/CLAMP events
- active vs shadow: first divergence at clamp (presence of APPLIED + unit change)

Artifacts are uploaded on failure for triage.

---

## Parity Diagnostics (Artifacts) (M4)

Deterministic post‑run parity hashes are emitted (artifact‑only, no journal events) to enable CI and promotion invariant checks without polluting the event stream.

Path pattern:

```text
artifacts/parity/<run-id>/hashes.txt
```

Fields:

- `events_sha` – SHA‑256 of normalized `events.csv`
- `trades_sha` – SHA‑256 of normalized `trades.csv` with the `config_hash` column removed
- `applied_count` – Count of `INFO_SENTIMENT_APPLIED_V1` lines (active mode scaling occurrences)
- `penalty_count` – Count of `PENALTY_APPLIED_V1` lines (penalty scaffold occurrences)

Normalization rules (mirrors engine implementation exactly):

1. Skip the meta line and header line (first two lines of `events.csv`; first line header only in `trades.csv`).
2. Force LF line endings (CRLF stripped).
3. Preserve original column ordering (stable deterministic ordering already enforced by writers).
4. Remove only the `config_hash` column from `trades.csv` (other columns untouched).

No additional whitespace trimming or JSON canonicalization is applied beyond what the engine already produces. Hashes are hex uppercase SHA‑256.

Invariants enforced in CI (matrix + invariants job):

- `off ↔ shadow`: `trades_sha` must be identical (no economic impact from shadow instrumentation).
- `active` mode: `trades_sha` may diverge from off/shadow only when `applied_count > 0` OR `penalty_count > 0` (i.e., a genuine impactful clamp or forced penalty occurred). If both counts are zero, active must match shadow.

Usage in CI:

- Each sentiment matrix job uploads the parity artifact directory.
- The invariants job globs for `hashes.txt` (does not assume a particular run id) and prints contents prior to assertions.

Rationale:

- Keeps journals pure (no `INFO_PARITY_*` events) maintaining backward compatibility for analytical tooling.
- Enables fast promotion & regression gates without re‑reading large CSVs repeatedly (single canonical hash per file per run).
- Provides explicit guardrails for feature evolution (any new economic impact in shadow/off surfaces as an unexpected hash divergence).

Example `hashes.txt`:

```text
events_sha=F1F816308C601785DA7E42C9E0250E0C35CF84144131AB9C172838435F138EC8
trades_sha=6B2DE3E5109B2562E4B3D30E93615614F6AD752311E1DFF46400E00565EE5FA1
applied_count=0
penalty_count=0
```

## Risk Guardrails (M4)

Introduces deterministic risk guardrails with three modes: `off`, `shadow`, `active` governing whether evaluations and alerts are emitted and whether trades are blocked.

### Modes

| Config (`featureFlags.risk`) | Normalized Mode | Evaluations (`INFO_RISK_EVAL_V1`) | Alerts (`ALERT_BLOCK_*`) | Trade Blocking |
|------------------------------|-----------------|-----------------------------------|--------------------------|----------------|
| omitted / `off` / `disabled` | off             | No (suppressed)                   | No                       | No             |
| `shadow`                     | shadow          | Yes                               | Yes (observability only) | No             |
| `active`                     | active          | Yes                               | Yes                      | Yes (on breach)|

Deterministic trade parity MUST hold between `off` and `shadow` modes (identical `trades_sha`). Divergence in `active` is allowed only when a guardrail would block (exposure or drawdown) producing an alert.

### Event Ordering (Per Bar)

```
BAR_V1 → [Sentiment events (if enabled): INFO_SENTIMENT_Z_V1 → (INFO_SENTIMENT_CLAMP_V1) → (INFO_SENTIMENT_APPLIED_V1 in active sentiment)] → INFO_RISK_EVAL_V1 → [ALERT_BLOCK_NET_EXPOSURE] → [ALERT_BLOCK_DRAWDOWN] → (trade emitted OR suppressed)
```

Guarantee: Each alert for a given bar is always preceded by its evaluation line; multiple alerts (exposure + drawdown) maintain evaluation-first ordering.

### Configuration (`riskConfig`)

```jsonc
{
  "featureFlags": { "risk": "shadow" },
  "riskConfig": {
    "maxNetExposureBySymbol": { "EURUSD": 500000, "USDJPY": 250000 },
    "maxRunDrawdownCCY": 1500,
    "forceDrawdownAfterEvals": { "EURUSD": 3 }, // test hook: after N evaluations reduce equity to trigger synthetic drawdown
    "blockOnBreach": true,   // ignored in off/shadow (always false effect there)
    "emitEvaluations": true  // set false to suppress INFO_RISK_EVAL_V1 while still allowing blocking (active)
  }
}
```

Field Notes:

- `maxNetExposureBySymbol`: Per-symbol absolute notional cap; breach condition is `projectedExposure >= cap` (>= chosen for deterministic zero‑cap promotion tests).
- `maxRunDrawdownCCY`: Maximum peak-to-trough equity loss (currency units) since run start.
- `forceDrawdownAfterEvals`: Deterministic test hook triggering a synthetic equity reduction *before* drawdown evaluation on the specified evaluation count (per symbol) – never use in production configs.
- `blockOnBreach`: Master switch (defaults true) enabling suppression of trade emission when in `active` mode and a breach is detected.
- `emitEvaluations`: When false (active only) blocking still occurs but the informational evaluation lines are omitted.

### Evaluation Payload Schema (`INFO_RISK_EVAL_V1`)

```jsonc
{
  "symbol": "EURUSD",
  "eval_index": 12,            // per-symbol monotonically increasing
  "net_exposure": 420000,      // absolute notional after projecting pending action
  "net_exposure_cap": 500000,
  "run_drawdown_ccy": 750,     // current realized + unrealized peak-to-trough
  "run_drawdown_cap": 1500,
  "will_block": false          // true only in active mode & imminent alert would suppress trade
}
```

### Alert Types

| Event | Condition | Primary Fields |
|-------|-----------|----------------|
| `ALERT_BLOCK_NET_EXPOSURE` | `projectedExposure >= cap` | `symbol, projected_exposure, cap` |
| `ALERT_BLOCK_DRAWDOWN` | `run_drawdown_ccy >= maxRunDrawdownCCY` | `symbol, run_drawdown_ccy, cap` |

Alerts are emitted in both `shadow` and `active`; only `active` suppresses the associated trade.

### Promotion Gating (Risk Parity)

Promotion decision JSON gains a `risk` block (conceptually analogous to `sentiment`). Scenarios:

| Baseline Mode | Candidate Mode | Alerts Introduced? | Result | Reason |
|---------------|----------------|--------------------|--------|--------|
| off/shadow | shadow | No new alerts | accept | parity |
| shadow | active | No alerts (benign) | accept | parity |
| active | shadow/off | (downgrade) | reject | risk_mode_downgrade |
| shadow | active | Alerts appear | reject | risk_mismatch |
| off | shadow | Alerts appear (should be none) | reject | risk_mismatch |

Zero-cap introduction heuristic: A candidate introducing a new per-symbol cap of `0` (not present in baseline) in a shadow→active upgrade is rejected (guarding against silent immediate blocking regression) even if runtime happened not to trigger an alert inside the limited test fixture window.

### Sample Promotion Decision Snippet

```jsonc
"risk": {
  "baseline_mode": "shadow",
  "candidate_mode": "active",
  "baseline": { "eval_count": 120, "alert_count": 0 },
  "candidate": { "eval_count": 120, "alert_count": 0 },
  "parity": true,
  "reason": "parity",
  "diff_hint": ""
}
```

If a mismatch occurs `parity=false` and `diff_hint` provides the first divergent alert/eval line or a summary (e.g., `alert_count base=0 cand=2`).

### Determinism & Parity Guarantees

- Off ↔ Shadow: identical `trades_sha` (no economic impact). Evaluations & alerts only differ in presence.
- Shadow ↔ Active (benign): identical trades when no breaches (alert_count = 0), proven by promotion/test invariants.
- Active breach determinism: Forceable via `forceDrawdownAfterEvals` or by configuring a low exposure cap; resulting alerts precede suppressed trade deterministically.
- All evaluation ordering & counts reproducible (per-symbol evaluation counter embedded in payload).

### Sample Lines

```text
INFO_RISK_EVAL_V1,{"symbol":"EURUSD","eval_index":1,"net_exposure":100000,"net_exposure_cap":500000,"run_drawdown_ccy":0,"run_drawdown_cap":1500,"will_block":false}
ALERT_BLOCK_NET_EXPOSURE,{"symbol":"EURUSD","projected_exposure":500000,"cap":500000,"ts":"2025-01-02T00:25:00Z"}
ALERT_BLOCK_DRAWDOWN,{"symbol":"EURUSD","run_drawdown_ccy":1500,"cap":1500,"ts":"2025-01-02T00:30:00Z"}
```

### Upgrade Notes

- No journal schema change required for existing consumers; risk events extend existing allow-list in strict verifier.
- Test hooks (`forceDrawdownAfterEvals`) must be stripped in production promotion proposals.
- Promotion gating leverages hash parity; risk mismatches are fast-fail before economic metrics comparison.

---

## License

Proprietary – All rights reserved. Internal evaluation and development only. No redistribution, sublicensing, or external publication without written authorization. See `LICENSE` for terms.

---
_Visibility switched to private on: 2025-10-07 (UTC). Public badges / publishing references have been removed or deprecated._
