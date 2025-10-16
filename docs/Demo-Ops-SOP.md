# Demo Operations SOP

Standard Operating Procedure for demo runs in TiYf Engine.

## When to Run What

- **Smoke (`demo-live-smoke-ctrader`)**: Manual check before production broker toggles. Provides `dryRun` toggle and optional hosted runner fallback for emergencies.
- **Daily (`demo-daily-ctrader`)**: Runs nightly at 00:00 UTC with `dryRun=false` to exercise broker credentials end-to-end.
- **Runner Health**: Weekly watchdog that raises the `Runner Health` issue/webhook when the self-hosted Windows runner is offline.

All demo jobs share the same concurrency group—queue new smoke runs instead of forcing overlap.

## Preflight Expectations

The workflow preflight writes `preflight.sanity.txt` to the artifact bundle. Confirm it reports:

- All secrets present (client ID/secret, OAuth + refresh token, broker code, numeric account id).
- `Account mode` flagged ✅ (RUN_ID begins with `DEMO`).
- Access token expiry days ≥ 1 (token parser surfaces negative/expired tokens as failures).

## Live Run Checklist

1. Dispatch the smoke workflow with `dryRun=false` when validating broker connectivity.
2. If the self-hosted runner is impaired, set `useHostedFallback=true` (hosted machine has .NET 8 installed by workflow).
3. Monitor the job summary for:
	- `STRICT_EXIT=0` and `PARITY_EXIT=0`.
	- `broker_dangling=false`.
	- Stable SHA pairs (compare to previous reference run if needed).
4. Download the artifact bundle if deeper inspection is required; the `checks.csv` file is a quick ingest into sheets.

## Failure Handling

The workflow will fail automatically when any of the following occur:

- DemoFeed exits non-zero after two attempts.
- `broker_dangling` ≠ `false` (indicates outstanding positions—stop trading until resolved).
- Strict/parity verifiers exit non-zero.

On failure the automation already:

- Comments on the `Demo Session` issue with the first failing step and the result line.
- Pushes commit statuses `ci:red` (and `ci:green` on recovery) so Sentry labels match CI state.
- Fires the `DEMO_ALERT_WEBHOOK` notification; ensure the on-call channel watches that integration.

### Human Follow-up

1. Acknowledge the webhook alert in the pager channel.
2. Review `demo-ctrader.log`, `strict.json`, `parity.json`, and `checks.csv` from the artifact bundle.
3. Update `Demo Session` issue with investigation notes and next steps.
4. If the self-hosted runner is offline, track progress in the `Runner Health` issue until the watchdog clears.

## Runbook Pointers

- [Demo Run Guide](DEMO-RUN.md): Details on artifacts, summary lines, and manual execution.
- [`docs/adr`](../docs/adr): Architecture decisions around broker integration and determinism expectations.
- [`scripts/`](../scripts): Handy maintenance scripts for rebuilding demo artifacts locally.

Keep the checks.csv export handy when compiling post-mortems—each line includes UTC timestamp, exit codes, hashes, and the runner name that executed the job.