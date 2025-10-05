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

## License
Proprietary – All rights reserved (placeholder). Not for external distribution.
