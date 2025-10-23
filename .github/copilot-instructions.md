# Copilot instructions for TIYF.ENGINE

## Architecture and data flow
- Modular monolith with hexagonal seams: Core (pure) + Sim (engine loop) + Sidecar (journals) + Host (daemon HTTP) + Tools (verifiers).
- Tick source -> bars -> strategy + risk -> execution adapter -> append-only journals (events.csv, trades.csv) -> verify strict/parity/deep.

## Determinism, journals, schema
- Inject `IClock` and operate UTC-only; no `DateTime.Now`.
- Journal meta always includes: schema_version, config_hash, adapter_id, broker, account_id.
- CSV columns (fixed order):
  - events.csv = sequence,utc_ts,event_type,src_adapter,payload_json
  - trades.csv = sequence,utc_ts,trade_type,src_adapter,payload_json
- Formatting guardrails: integer `volume_units`, two-decimal `pnl_ccy`.
- When adding/removing/reordering columns bump `TiYf.Engine.Core.Infrastructure.Schema.Version` and update verifiers.

## Adapter handshake cues
- OANDA practice handshake line: `Connected to OANDA (practice)`
- Order evidence: `OrderSend ok decision=`
- Banner line: `BROKER_MODE=oanda-demo (stub=OFF) accountId=env:OANDA_PRACTICE_ACCOUNT_ID`

### PowerShell grep snippets
```powershell
# OANDA handshake
dotnetLog = 'scratch/oanda-sim.log'
Select-String -Path $dotnetLog -Pattern 'Connected to OANDA \(practice\)'

# Order evidence
Select-String -Path $dotnetLog -Pattern 'OrderSend ok decision='
```

## Host vs Simulator
- Host daemon (`src/TiYf.Engine.Host/*`) serves `/health` and `/metrics` on 127.0.0.1:8080; simulator binaries do not.
- `.github/workflows/demo-health-oanda.yml` captures `/health` snapshots with retry/backoff and archives `health.json` as an artifact.
- Systemd unit reads `/etc/tiyf/engine.env` (root owned, 0600); do not source it in ExecStart—use `EnvironmentFile`.

## Build, run, test
- Build: `dotnet build TiYf.Engine.sln -c Release`
- Unit/integration: `dotnet test --no-build`
- Simulator: `dotnet run --project src/TiYf.Engine.Sim -- --config sample-config.demo-oanda.json`
- Host: `dotnet run --project src/TiYf.Engine.Host -- --config sample-config.demo-oanda.json` then `curl http://127.0.0.1:8080/health`
- Verifiers: `dotnet exec src/TiYf.Engine.Tools/bin/Release/net8.0/TiYf.Engine.Tools.dll verify strict|parity|deep`

## GitHub Actions conventions (self-hosted VPS)
- Runner labels: `[self-hosted, Linux, X64, tiyf-vps]`
- Add `timeout-minutes`, `concurrency` (grouped by ref), and `permissions: { contents: read }`
- Pin actions by major (checkout@v4, setup-dotnet@v4, upload-artifact@v4, etc.)
- Write derived values to `$GITHUB_ENV`; do not redeclare them later via `env:` blocks.
- All simulator launches go through `scripts/pwsh/InvokeSim.ps1` (shared by smoke & daily). Pass adapter-specific flags via `-ExtraArgs` (e.g. `--broker-enabled`, `--quiet`) and record `LOG_PATH` after the call.
- Preflight every workflow with `dotnet exec $env:SIM_DLL -- --version` so CLI binding failures fail fast.
- Adapter inputs: `adapter = stub|ctrader-demo|oanda-demo`; resolve config inside the workflow with PowerShell.

### Safety rails (PowerShell JSON checks)
```powershell
$configPath = Join-Path $env:GITHUB_WORKSPACE 'sample-config.demo-oanda.json'
$cfg = Get-Content $configPath | ConvertFrom-Json
if ($cfg.perTradeRiskPct -gt 0.05) { throw 'perTradeRiskPct too high' }
if ($cfg.realLeverageCap -gt 2.0) { throw 'realLeverageCap too high' }
$expected = 'EURUSD','GBPUSD','USDJPY','USDCHF','USDCAD','AUDUSD','NZDUSD','XAUUSD'
if (@($cfg.universe | Sort-Object) -ne @($expected | Sort-Object)) { throw 'Universe drift detected' }
```

## Alerts, artifacts, summaries
- Success alert resolver: pick prod vs staging webhook from `DEMO_ALERT_WEBHOOK_PROD` / `_STAGING` using `ALERT_ENV` (repo var or input).
- Only send pings when `enableAlertPing=true`; log HTTP status and never echo the URL or payload.
- Artifacts: use adapter suffix (e.g. `vps-demo-artifacts-adapter-oanda-demo/`) and `if: always()`.
- Summary must include: commit SHA, adapter, config, dryRun flag, `STRICT_EXIT`, `PARITY_EXIT`, `broker_dangling`, `events_sha`, `trades_sha`, and connectivity verdict (`handshake` boolean, `order_evidence` boolean, note).

## Scheduling and weekend behaviour
- Default cron for demo OANDA runs: `10 2 * * 1-5` (UTC weekdays).
- Weekend/manual runs should succeed with a note such as `no trade criteria met (weekend/closed)`; only require `OrderSend` evidence during market hours or when `requireOrderEvidence=true`.

## Workflows (examples)
- `.github/workflows/demo-live-smoke-oanda.yml` – canonical adapter smoke flow
- `.github/workflows/demo-daily-oanda.yml` – scheduled daily OANDA simulation with parity/alerts
- `.github/workflows/demo-health-oanda.yml` – dedicated `/health` capture for the daemon
- `.github/workflows/deploy-demo-host.yml` – deployment + systemd refresh
- `.github/workflows/verify-deep.yml` / `.github/workflows/nightly-canary.yml` – determinism governance

## Pitfalls
- Do not query `/health` from simulator jobs; only the host serves it.
- Do not re-export values already written to `$GITHUB_ENV` via step `env:` blocks.
- Never log secrets, webhooks, tokens, or account IDs.
- Treat journals as append-only; update verifiers alongside schema changes.
