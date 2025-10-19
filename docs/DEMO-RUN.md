# Demo Run Operations

## Purpose
- Allow operators to switch between the stub broker and the cTrader demo adapter without modifying workflow logic.
- Guard live-touching runs with deterministic config hashes, disk headroom checks, and secret preflight gates.
- Produce predictable artifacts and summaries so incidents can be triaged and rolled back quickly.

## Workflows at a Glance
| Workflow | Trigger | Primary Use | Notes |
| --- | --- | --- | --- |
| `demo-live-smoke-ctrader` | `workflow_dispatch` | Manual smoke before demo trading | Accepts `adapter`, `dryRun`, `useHostedFallback`, `alertEnv`, `enableAlertPing`. Updates Demo Session issue, flips commit status, and pings the repo secret resolved from `ALERT_ENV` when the toggle is enabled for prod evidence. |
| `demo-daily-ctrader` | Cron (00:00 UTC) + `workflow_dispatch` | Scheduled end-to-end guardrail | Shares the same inputs; scheduled runs force `dryRun=false`, reuse the alert routing resolver, and require `enableAlertPing=true` only when operators want a prod success-path proof. |
| `demo-session-manual` | `workflow_dispatch` | Operator-led session with issue transcript | Inputs: `adapter`, optional `run_id`, `broker_enabled` (auto-aligned with adapter). Generates a fresh Demo Session issue entry. |

All jobs target the self-hosted runner labelled `[self-hosted, Linux, X64, tiyf-vps]`, install PowerShell 7, set execution policy to `RemoteSigned`, and provision .NET 8 before executing DemoFeed.

## Adapter Modes
- `stub` (default) disables broker integration and uses `sample-config.demo.json`.
- `ctrader-demo` enables broker calls, swaps to `sample-config.demo-ctrader.json`, and requires repository secrets `CT_APP_ID`, `CT_APP_SECRET`, `CT_DEMO_OAUTH_TOKEN`, `CT_DEMO_REFRESH_TOKEN`, `CT_DEMO_ACCOUNT_ID`, `CT_DEMO_BROKER`.
- Rollback path is always "re-dispatch with `adapter=stub`"; the workflows echo this in their summaries for quick reference.

## Automatic Safeguards
- Disk headroom gate: run aborts if less than 20 GiB remain on the runner workspace volume.
- Config safety rails: risk limits must satisfy `perTradeRiskPct <= 0.05` and `realLeverageCap <= 2.0`; the eight-symbol universe must match `EURUSD, GBPUSD, USDJPY, USDCHF, USDCAD, AUDUSD, NZDUSD, XAUUSD` in both content and order.
- cTrader preflight: when `adapter=ctrader-demo`, secrets are validated and summarized to `preflight.sanity.txt`; missing, non-numeric, or expired values fail the run before DemoFeed starts.
- Run identity guard: `RUN_ID` must contain `DEMO`; the manual workflow rewrites `broker_enabled` to the safe value for the selected adapter and logs a warning if it overrides the operator input.

## Execution Outline
1. Checkout repository and compute adapter context (config path, SHA256 hash, log name, artifact name).
2. Validate disk headroom and configuration safety rules; execute the cTrader secret checklist when required.
3. Restore and build `TiYf.Engine.sln` in Release, then run DemoFeed (smoke/daily retry automatically; the manual session executes once) while injecting `--broker-enabled` according to the adapter mode.
4. Capture verifier outputs, broker health (`broker_dangling` must be `false`), connectivity proof for cTrader runs, and hashes for generated journals.
5. Publish result summaries, step reports, `checks.csv`, and the `artifacts/` payload prior to uploading with 30-day retention.

## Observability and Artifacts
- Log files begin with banner lines such as `Adapter = …`, `Universe = …`, and `Config = …`. For cTrader runs the workflow searches `demo-ctrader.log` for `Adapter = CTrader OpenAPI (demo)|Connected to cTrader endpoint|OrderSend`; missing evidence fails the job.
- Artifacts upload under `vps-demo-artifacts-adapter-<adapter>` and contain `events.csv`, `trades.csv`, strict/parity JSON, the DemoFeed log (`demo-ctrader.log` or `demo-stub.log`), and `preflight.sanity.txt` when generated. Placeholder files are written when a source artifact is absent so consumers see a consistent layout.
- `checks.csv` at the workspace root records UTC timestamp, strict/parity exit codes, `broker_dangling`, SHA hashes, and runner identity. The same data is summarised in the `RESULT_LINE` stored in the step summary.

## Alert Routing
- `ALERT_ENV` input (or repository variable fallback) accepts `prod` or `staging`; the resolver defaults to `prod` when not supplied.
- Webhooks live in repository secrets `DEMO_ALERT_WEBHOOK_PROD` and `DEMO_ALERT_WEBHOOK_STAGING`. The selected secret name is logged (masked) before the ping is attempted.
- Success pings (`204 No Content`) are emitted only when `ALERT_ENV` resolves to `prod` **and** the `enableAlertPing` input is set to `true`; the HTTP status code is recorded in the run summary for downstream evidence. Non-2xx responses bubble up and fail the workflow to prevent silent alert outage when a ping is attempted.
- The environment-level `DEMO_ALERT_WEBHOOK` secret is no longer referenced. Clean it up (or repoint it) to avoid drift with the repository-scoped configuration.
- cTrader credentials (`CT_*`) remain bound to the `ctrader-demo` environment so the adapter preflight keeps guarding runs without leaking secret data.

## Failure Handling
- Smoke and daily workflows set GitHub commit statuses (`ci:red` or `ci:green`), comment on the "Demo Session" issue with the first failing step, and optionally post to the ops webhook.
- The manual session workflow appends a transcript comment to its Demo Session issue, embedding the summary line and artifact link for later audit.
- All workflows retain artifacts and summaries on failure, enabling operators to diff hashes across runs without rerunning immediately.

## Operator Checklist
- Choose the adapter: prefer `stub` after risky changes; escalate to `ctrader-demo` only after confirming secrets and desired trading window.
- Verify the `tiyf-vps` runner is online and has the required disk space before dispatching.
- For cTrader runs, review repository secrets ahead of time; the preflight gate will halt the run, but proactive checks reduce noise.
- Capture artifact URLs and the posted `RESULT_LINE` when filing incident notes; include `preflight.sanity.txt` for compliance evidence.
- If a cTrader run misbehaves, immediately re-run with `adapter=stub` to restore nightly coverage while the incident is investigated.