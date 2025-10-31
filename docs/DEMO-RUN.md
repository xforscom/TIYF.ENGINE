# Demo Run Operations

## Purpose
- Allow operators to switch between the stub broker and the cTrader demo adapter without modifying workflow logic.
- Guard live-touching runs with deterministic config hashes, disk headroom checks, and secret preflight gates.
- Produce predictable artifacts and summaries so incidents can be triaged and rolled back quickly.

## Workflows at a Glance
| Workflow | Trigger | Primary Use | Notes |
| --- | --- | --- | --- |
| `demo-live-smoke-ctrader` | `workflow_dispatch` | Manual smoke before demo trading | Accepts `adapter`, `dryRun`, `useHostedFallback`, `alertEnv`, `enableAlertPing`. Updates Demo Session issue, flips commit status, and pings the repo secret resolved from `ALERT_ENV` when the toggle is enabled for prod evidence. |
| `demo-live-smoke-oanda` | `workflow_dispatch` | Manual smoke against OANDA practice | Minimal harness that builds Release, runs the simulator with `sample-config.demo-oanda.json`, enforces `Connected to OANDA` handshake evidence, and uploads `vps-demo-artifacts-adapter-oanda-demo/`. |
| `demo-daily-ctrader` | Cron (00:00 UTC) + `workflow_dispatch` | Scheduled end-to-end guardrail | Shares the same inputs; scheduled runs force `dryRun=false`, reuse the alert routing resolver, and require `enableAlertPing=true` only when operators want a prod success-path proof. |
| `demo-session-manual` | `workflow_dispatch` | Operator-led session with issue transcript | Inputs: `adapter`, optional `run_id`, `broker_enabled` (auto-aligned with adapter). Generates a fresh Demo Session issue entry. |
| `demo-health-oanda` | Cron (6-hour) + `workflow_dispatch` | Capture current host /health snapshot | Fetches telemetry from the running daemon, writes the metrics summary to the job output, and uploads `health.json` without running the simulator. |

All jobs target the self-hosted runner labelled `[self-hosted, Linux, X64, tiyf-vps]`, install PowerShell 7, set execution policy to `RemoteSigned`, and provision .NET 8 before executing DemoFeed.

## Adapter Modes
- `stub` (default) disables broker integration and uses `sample-config.demo.json`.
- ctrader-demo enables broker calls, swaps to sample-config.demo-ctrader.json, and requires repository secrets CT_APP_ID, CT_APP_SECRET, CT_DEMO_OAUTH_TOKEN, CT_DEMO_REFRESH_TOKEN, CT_DEMO_ACCOUNT_ID, CT_DEMO_BROKER. Adapter settings support env:NAME placeholders so config files can reference secrets without inlining values (see sample-config.demo-ctrader.json).
- oanda-demo enables broker calls, swaps to sample-config.demo-oanda.json, and requires repository secrets OANDA_PRACTICE_TOKEN and OANDA_PRACTICE_ACCOUNT_ID. The adapter also honours `env:NAME` placeholders so secrets can be injected at runtime without committing values. The `adapter.settings.stream` stanza controls the OANDA v20 streaming feed (`enable=true` to subscribe live, `feedMode=replay` to ingest a local tick file for diagnostics).
- Rollback path is always "re-dispatch with `adapter=stub`"; the workflows echo this in their summaries for quick reference.

## Automatic Safeguards
- Disk headroom gate: run aborts if less than 20 GiB remain on the runner workspace volume.
- Config safety rails: risk limits must satisfy `perTradeRiskPct <= 0.05` and `realLeverageCap <= 2.0`; the eight-symbol universe must match `EURUSD, GBPUSD, USDJPY, USDCHF, USDCAD, AUDUSD, NZDUSD, XAUUSD` in both content and order.
- cTrader preflight: when `adapter=ctrader-demo`, secrets are validated and summarized to `preflight.sanity.txt`; missing, non-numeric, or expired values fail the run before DemoFeed starts.
- OANDA preflight: when `adapter=oanda-demo`, the workflow asserts that `OANDA_PRACTICE_TOKEN` and `OANDA_PRACTICE_ACCOUNT_ID` are present before wiring the host.
- Run identity guard: `RUN_ID` must contain `DEMO`; the manual workflow rewrites `broker_enabled` to the safe value for the selected adapter and logs a warning if it overrides the operator input.

## Execution Outline
1. Checkout repository and compute adapter context (config path, SHA256 hash, log name, artifact name).
2. Validate disk headroom and configuration safety rules; execute the cTrader secret checklist when required.
3. Restore and build `TiYf.Engine.sln` in Release, then run DemoFeed (smoke/daily retry automatically; the manual session executes once) while injecting `--broker-enabled` according to the adapter mode.
4. Capture verifier outputs, broker health (`broker_dangling` must be `false`), connectivity proof for cTrader runs, and hashes for generated journals.
5. Publish result summaries, step reports, `checks.csv`, and the `artifacts/` payload prior to uploading with 30-day retention.

## Observability and Artifacts

- Log files begin with banner lines such as `Adapter = â€¦`, `adapter_id=â€¦`, `broker=â€¦`, `account_id=â€¦`, `Universe = â€¦`, and `Config = â€¦`. DemoFeed also emits `BROKER_MODE=<adapter> (stub=ON|OFF) accountId=â€¦` before any execution output. For cTrader runs the workflow searches `demo-ctrader.log` for `Connected to cTrader endpoint`, and for OANDA runs it searches `demo-oanda.log` for `Connected to OANDA endpoint`; any `OrderSend` lines whose `brokerOrderId` retains the `STUB-` prefix are rejected.
- Artifacts upload under `vps-demo-artifacts-adapter-<adapter>` and contain `events.csv`, `trades.csv`, strict/parity JSON, the DemoFeed log (`demo-ctrader.log`, `demo-oanda.log`, or `demo-stub.log`), and `preflight.sanity.txt` when generated. Placeholder files are written when a source artifact is absent so consumers see a consistent layout.
- Event payloads now include a `src_adapter` JSON field, and the trades journal encodes `src_adapter=<adapter>` in the `data_version` column so provenance survives downstream ingestion.
- `checks.csv` at the workspace root records UTC timestamp, strict/parity exit codes, `broker_dangling`, SHA hashes, and runner identity. The same data is summarised in the `RESULT_LINE` stored in the step summary.

## Host Service & Health

- The TiYf Engine Host runs under `tiyf-engine-demo.service` on the VPS and listens on `http://127.0.0.1:8080`.
- The service logs a startup banner (`host: adapter meta adapter=<id> broker=<broker> account=<account> mode=<mode>`) as soon as the DI container resolves the adapter, followed by one heartbeat per interval: `host: heartbeat t=<utc> adapter=<id> connected=<bool> last_h1_decision=<utc|none> pending_orders=<n> bar_lag_ms=<ms>`.
- Set `ENGINE_HOST_HEARTBEAT_SECONDS` in `/etc/tiyf/engine.env` to override the default 30s heartbeat cadence (the systemd unit also sources `/opt/tiyf/current/.env` when present).
- `/health` responds with a connectivity snapshot and is used by both CI and the deployment workflow. A typical response is:

```json
{
  "adapter": "oanda-demo",
  "connected": true,
  "last_h1_decision_utc": "2025-01-20T13:20:00Z",
  "last_decision_utc": "2025-01-20T13:20:00Z",
  "timeframes_active": ["H1", "H4"],
  "bar_lag_ms": 12.5,
  "pending_orders": 0,
  "feature_flags": ["dataQa=active", "riskProbe=enabled"],
  "loop_iterations_total": 42,
  "decisions_total": 42,
  "loop_last_success_utc": "2025-01-20T13:20:00Z",
  "loop_start_utc": "2025-01-20T09:00:00Z",
  "last_heartbeat_utc": "2025-01-20T13:24:30Z",
  "heartbeat_age_seconds": 2.1,
  "stream_connected": 1,
  "stream_heartbeat_age_seconds": 0.4,
  "open_positions": 1,
  "active_orders": 0,
  "risk_events_total": 5,
  "alerts_total": 0,
  "last_log": "Connected to OANDA (practice)"
}
```

## Deployment Playbook

- `deploy-demo-host` (`workflow_dispatch`) publishes the host with inputs `environment`, optional `releaseTag`, and `dryRun` (defaults to `true`). Dry runs run `rsync --dry-run` and execute the remote script in simulation mode; real runs flip `/opt/tiyf/current` and restart the unit.
- Release artefacts land in `/opt/tiyf/releases/<release-id>/` with `systemd/tiyf-engine-demo.service`, `scripts/remote-deploy.sh`, and binaries produced by `dotnet publish -c Release`.
- The systemd unit copies the service file into `/etc/systemd/system/`, performs `systemctl daemon-reload`, flips the symlink, restarts the service, and polls `/health` (5×, 5s back-off) on successful deployments.
- Service management quick reference:

```bash
sudo systemctl status tiyf-engine-demo.service
sudo systemctl stop tiyf-engine-demo.service
sudo systemctl start tiyf-engine-demo.service
```

- Rollback (choose the prior good release ID):

```bash
sudo systemctl stop tiyf-engine-demo.service
sudo ln -sfn /opt/tiyf/releases/<previous-id> /opt/tiyf/current
sudo systemctl daemon-reload
sudo systemctl start tiyf-engine-demo.service
```

- Environment overrides live in `/etc/tiyf/engine.env` (sample template: `deploy/systemd/engine.env.sample`). `ENGINE_HOST_HEARTBEAT_SECONDS` is respected without restart after the next reload/restart.

## Alert Routing

- `ALERT_ENV` input (or repository variable fallback) accepts `prod` or `staging`; the resolver defaults to `prod` when not supplied.
- Webhooks live in repository secrets `DEMO_ALERT_WEBHOOK_PROD` and `DEMO_ALERT_WEBHOOK_STAGING`. The selected secret name is logged (masked) before the ping is attempted.
- Success pings (`204 No Content`) are emitted only when `ALERT_ENV` resolves to `prod` **and** the `enableAlertPing` input is set to `true`; the HTTP status code is recorded in the run summary for downstream evidence. Non-2xx responses bubble up and fail the workflow to prevent silent alert outage when a ping is attempted.
- The environment-level `DEMO_ALERT_WEBHOOK` secret is no longer referenced. Clean it up (or repoint it) to avoid drift with the repository-scoped configuration.
- Broker credentials remain scoped to their environments: cTrader secrets (`CT_*`) stay bound to `ctrader-demo`, and OANDA practice secrets (`OANDA_PRACTICE_*`) stay bound to `oanda-demo` so preflight gates can validate without leaking values.

## Failure Handling

- Smoke and daily workflows set GitHub commit statuses (`ci:red` or `ci:green`), comment on the "Demo Session" issue with the first failing step, and optionally post to the ops webhook.
- The manual session workflow appends a transcript comment to its Demo Session issue, embedding the summary line and artifact link for later audit.
- All workflows retain artifacts and summaries on failure, enabling operators to diff hashes across runs without rerunning immediately.

## Operator Checklist

- Choose the adapter: prefer `stub` after risky changes; escalate to `ctrader-demo` or `oanda-demo` only after confirming secrets and desired trading window.
- Verify the `tiyf-vps` runner is online and has the required disk space before dispatching.
- For external broker runs, review repository secrets ahead of time; the preflight gate will halt the run, but proactive checks reduce noise.
- Capture artifact URLs and the posted `RESULT_LINE` when filing incident notes; include `preflight.sanity.txt` for compliance evidence.
- If a broker-integrated run misbehaves, immediately re-run with `adapter=stub` to restore nightly coverage while the incident is investigated.

## Scheduled automation

| Workflow | Cadence | Purpose | Evidence |
| --- | --- | --- | --- |
| `daily-monitor` | Weekdays 02:15 UTC (cron) + manual dispatch | Polls the deployed host `/health` endpoint with five-attempt retry/back-off and fails fast unless `connected` is `true`. | Uploads `daily-monitor-health/health.json` and writes `adapter/connected/last_decision_utc/timeframes_active` alongside heartbeat stats to the job summary. |
| `friday-proof` | Fridays 10:00 UTC (cron) + manual dispatch | Runs the OANDA simulator via `InvokeSim.ps1` with `enableAlertPing=true` and `ALERT_ENV=prod` to validate broker handshake plus alert routing. | Enforces handshake evidence, strict/parity gates, `broker_dangling=false`, and asserts `Alert ping HTTP status: 204` in the summary. |
| `weekly-digest` | Sundays 09:00 UTC (cron) + manual dispatch | Aggregates the last five successful `demo-daily-oanda` runs on `main`, extracts `events_sha`/`trades_sha`, and posts a digest to Discord. | Archives `weekly-digest.txt`, logs the webhook status code, and includes the digest table in the run summary without exposing the webhook URL. |
## Host Telemetry
- The engine host exposes two loopback-only endpoints: http://127.0.0.1:8080/health (JSON) and http://127.0.0.1:8080/metrics (Prometheus text). Both respond even when trading is idle, falling back to zero/unknown values when data is unavailable.
- /health now includes heartbeat_age_seconds, open_positions, active_orders, risk_events_total, alerts_total, last_decision_utc, timeframes_active, decisions_total, and loop_iterations_total alongside the existing adapter status. Daily monitor runs quote these fields in the summary: daily-monitor: adapter=<name> connected=<bool> heartbeat_age=<xs> stream_connected=<n> stream_heartbeat_age=<xs> bar_lag_ms=<ms> open_positions=<n> active_orders=<n> risk_events_total=<n> alerts_total=<n> last_decision_utc=<iso> timeframes_active=<csv> decisions_total=<n> loop_iterations_total=<n>.
- /metrics publishes the same values as gauges/counters (engine_heartbeat_age_seconds, engine_bar_lag_ms, engine_pending_orders, engine_open_positions, engine_active_orders, engine_risk_events_total, engine_alerts_total, engine_stream_connected, engine_stream_heartbeat_age_seconds, engine_loop_uptime_seconds, engine_loop_iterations_total, engine_decisions_total, engine_loop_last_success_ts) so Prometheus-compatible scrapers can ingest them without extra formatting.







