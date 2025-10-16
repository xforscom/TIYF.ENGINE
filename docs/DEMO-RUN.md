dotnet build -c Release
dotnet exec src/TiYf.Engine.DemoFeed/bin/Release/net8.0/TiYf.Engine.DemoFeed.dll --run-id DEMO-TEST --broker-enabled true --broker-fill-mode ioc-market
dotnet exec src/TiYf.Engine.Tools/bin/Release/net8.0/TiYf.Engine.Tools.dll verify strict --events journals/DEMO/DEMO-TEST/events.csv --trades journals/DEMO/DEMO-TEST/trades.csv --schema 1.3.0
# Demo Run Guide

The demo workflows validate the TiYf Engine broker integration in a deterministic environment. Use this guide to understand what each workflow does, what artifacts to expect, and how to interpret the run summaries.

## Demo Configuration

- **Scenario**: `tests/fixtures/backtest_m0/config.backtest-m0.json` with schema 1.3.0.
- **Broker Mode**: Always enabled (`--broker-enabled true --broker-fill-mode ioc-market`).
- **Determinism Targets**:
  - 6 trades with stable ordering.
  - No `ALERT_BLOCK_*` events.
  - `broker_dangling=false` after every run.
- **Verifier Contracts**: `verify strict` and `verify parity` MUST exit with code `0` every time.

## Workflows

- `demo-live-smoke-ctrader.yml`: Manual smoke validation before live interventions. Includes workspace-aware preflight, retrying `DemoFeed`, result hashing, and resiliency around artifact capture.
- `demo-daily-ctrader.yml`: Scheduled midnight UTC run with the same guard rails as the smoke job. Default `dryRun=false` on the schedule so it exercises live broker credentials nightly.
- `demo-runner-health.yml`: Weekly watchdog that inspects the self-hosted Windows runner fleet and raises an issue/webhook alert when any demo runner is offline.

Both demo runs gate on a single concurrency slot to guarantee “one demo at a time”. They expose `useHostedFallback` for manual runs when the self-hosted runner is unavailable; set it to `true` in `workflow_dispatch` to land on `windows-latest`.

## Job Summary Format

Every run appends a deterministic summary line to the job summary:

```
STRICT_EXIT=0; PARITY_EXIT=0; broker_dangling=false; events_sha=<SHA256>; trades_sha=<SHA256>
Artifacts retained for 30 days (repository default).
```

- Any non-zero exit code or `broker_dangling` other than `false` causes an immediate failure.
- The SHA pairs allow quick parity comparisons across runs without downloading the CSVs.

## Artifacts

Artifacts are uploaded under `demo-*-ctrader-<RUN_ID>` with 30-day retention. On failure the collector still produces placeholder files so triage has a consistent structure:

| File | Purpose |
| --- | --- |
| `events.csv`, `trades.csv` | Primary journals copied from the runner workspace (placeholder if generation failed).
| `strict.json`, `parity.json` | Verifier outputs.
| `demo-ctrader.log` | Combined DemoFeed stdout.
| `preflight.sanity.txt` | Workspace-aware secret sanity checks.
| `checks.csv` | One line CSV containing UTC timestamp, strict/parity exits, broker flag, SHA hashes, and runner name for quick spreadsheet ingest.

## Failure Signaling

On red runs the workflows automatically:

- Comment (or open) the `Demo Session` issue with the first failing step and the summary line.
- Flip commit statuses `ci:red` / `ci:green` so Sentry release automation inherits the same signal.
- Invoke the configured ops webhook (set repository/environment secret `DEMO_ALERT_WEBHOOK`) with a short payload.

If the self-hosted runner fleet is down, the `Runner Health` issue receives an update from the watchdog job. The same webhook fires so the on-call channel sees the degraded state.

## Running Locally

```powershell
# Build release binaries
dotnet build -c Release

# Execute the demo feed (dry run by default)
dotnet exec src/TiYf.Engine.DemoFeed/bin/Release/net8.0/TiYf.Engine.DemoFeed.dll `
  --run-id DEMO-LOCAL `
  --broker-enabled true `
  --broker-fill-mode ioc-market

# Verify determinism
dotnet exec src/TiYf.Engine.Tools/bin/Release/net8.0/TiYf.Engine.Tools.dll verify strict `
  --events journals/DEMO/DEMO-LOCAL/events.csv `
  --trades journals/DEMO/DEMO-LOCAL/trades.csv `
  --schema 1.3.0
```

Expected output: `STRICT VERIFY: OK`, `PARITY VERIFY: OK`, and `broker_dangling=false` in the DemoFeed log.