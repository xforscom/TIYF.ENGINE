# TiYf Engine

Modular monolith trading engine MVP (M0). Focus: deterministic replay, bar building, instrument catalog, atomic journaling.

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

## License

Proprietary – All rights reserved (placeholder). Not for external distribution.
