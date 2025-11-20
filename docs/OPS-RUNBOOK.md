# Ops Runbook (Demo OANDA Host)

_Environment assumptions:_ OANDA practice account (`demo-oanda`), VPS `tiyf-vps-uk-01`, timeframes H1/H4, GVRS live gate enabled for entries only on demo configs, risk rails Phase‑A telemetry only, promotion telemetry only, news feed file-backed by default, no real-money trading.

## General Checklist
1. VPN/SSH into the VPS as `ubuntu`.
2. All commands run from `/opt/tiyf/current` unless stated.
3. Primary service: `tiyf-engine-demo.service`.
4. Health endpoints:
   - `curl -s http://127.0.0.1:8080/health`
   - `curl -s http://127.0.0.1:8080/metrics`
5. Daily-monitor workflow: https://github.com/xforscom/TIYF.ENGINE/actions/workflows/daily-monitor.yml

## Scenario 1 – Adapter / Feed Issues

### How to Detect
- `/health` fields:
  - `connected` false.
  - `bar_lag_ms` >> 120000 (value in ms).
  - `stream_connected` = 0 or `stream_heartbeat_age` > 10s.
  - `last_heartbeat_utc` older than 60s.
- Daily-monitor summary showing `stream_connected=0` or large `heartbeat_age`.

### What to Do
1. `curl -s http://127.0.0.1:8080/health | jq '{connected, bar_lag_ms, stream_connected, stream_heartbeat_age, last_heartbeat_utc}'`.
2. If stream is disconnected:
   - Check logs: `sudo journalctl -u tiyf-engine-demo.service -n 200 --no-pager`.
   - Restart feed if needed (engine restart, see Scenario 6).
3. Confirm broker/login credentials are valid (env vars). No change typically needed for demo.
4. After restart, verify `/health.stream_connected==1` and `bar_lag_ms` back to expected (<= few seconds).

### When to Escalate
- After two restart attempts, if `stream_connected` remains 0 or `bar_lag_ms` keeps increasing, stop the service and escalate to devs with logs.
- Do not flip configs or kill-switch without approval.

## Scenario 2 – GVRS Gate Behaviour

### How to Detect
- `/health.gvrs_gate`: `bucket`, `blocking_enabled`, `last_block_utc`.
- `/metrics`: `engine_gvrs_gate_blocks_total`, `engine_gvrs_gate_is_blocking{state="volatile"}`.
- Daily-monitor tail: `gvrs_gate_blocks=...` shown only when >0.

### What to Do
1. `curl -s http://127.0.0.1:8080/metrics | rg 'engine_gvrs_gate'`.
2. If `gvrs_bucket=Volatile` and blocks_total increasing:
   - This is expected: entries are blocked until bucket returns to <= Moderate.
   - Monitor `gvrs_raw` and `gvrs_ewma`; no manual action required.
3. If bucket stays volatile > 2h, notify Ops lead; do **not** disable the gate.

### When to Escalate
- If `gvrs_gate_blocks_total` climbs while `gvrs_bucket=Moderate` or `blocking_enabled=false`, raise incident (should not block in that case).
- If `/health.gvrs_gate` missing, escalate (telemetry regression).

## Scenario 3 – Risk Rails Telemetry Spikes

### How to Detect
- `/metrics`: `engine_risk_events_total`, `engine_risk_blocks_total`, `engine_risk_throttles_total`.
- `/health.risk_rails`: config + usage fields.
- Daily-monitor summary: `risk_events_total`, `risk_blocks_total`, `risk_throttles_total`.

### What to Do
1. If counters increase modestly (e.g., news blackout), log in Ops channel; telemetry-only.
2. Check `curl -s http://127.0.0.1:8080/metrics | rg 'engine_risk'`.
3. If values skyrocket unexpectedly:
   - Review logs for `ALERT_RISK_*`.
   - Confirm kill-switch status (Scenario 4).
4. For demo, no action unless accompanied by kill-switch or order rejects.

### When to Escalate
- Counters grow rapidly (>100/min) or `risk_blocks_total>0` despite telemetry-only rails → escalate (indicates future M11-B code landed accidentally).

## Scenario 4 – Kill-Switch Operations

### How to Detect
- `/health.kill_switch` block (if present) or `engine_kill_switch` metric (0/1).
- Logs: `KILL_SWITCH set to ON/OFF`.

### What to Do
1. Check status: `curl -s http://127.0.0.1:8080/health | jq '.kill_switch'`.
2. To enable (stop trading):
   - `sudo systemctl stop tiyf-engine-demo.service` (if manual stop desired), or use CLI kill command if implemented.
   - Alternatively set env/flag and restart (per existing SOP).
3. To disable:
   - Ensure reconciliation is clean (Scenario 6 verification).
   - Edit config / env as needed (per M4 instructions).
   - Restart service.

### When to Escalate
- If kill-switch toggles itself (logs) or remains ON after restart without clear reason.
- If kill-switch fails to block decisions (orders still sent) – stop engine and alert devs.

## Scenario 5 – Risk Rails Block Orders (Demo Live Mode)

### How to Detect
- `/metrics`: `engine_risk_blocks_total{gate="..."}` and broker guardrail counters `engine_broker_cap_blocks_total{gate="daily_loss|global_units|symbol_units:..."}`
- `/health.risk_rails`: violation counters, cooldown block indicators, and `broker_cap_blocks_total`/`broker_cap_blocks_by_gate`.
- Daily-monitor: `risk_blocks_total` (optional breakdown if appended).
- Journal/events: `ALERT_RISK_*_HARD` entries.

### What to Do
1. `curl -s http://127.0.0.1:8080/metrics | rg 'engine_risk_blocks_total|engine_broker_cap_blocks_total'`.
2. Check `/health.risk_rails` for which gate triggered (broker_daily, global_units, symbol caps, cooldown) and whether `blocking_enabled` is true (demo only).
3. If blocks align with demo caps (expected), no action required; entries resume when rails clear. Exits are always allowed.
4. If blocks appear on non-demo configs (should not happen), stop engine and escalate.

### When to Escalate
- Blocks on real-money configs (not expected).
- Blocks persisting due to misconfigured caps; hash mismatch in `/health.config.hash` vs intended config.

## Scenario 6 – Demo Config Changes

### How to Detect
- `/health.config.hash` and daily-monitor `risk_config_hash` mismatch expected value.

### What to Do
1. Edit config in repo (`sample-config.demo-oanda.json`), run tests, tag release.
2. Deploy to VPS (`/opt/tiyf/current` update via rsync or release artifact).
3. Restart service (Scenario 6).
4. Verify:
   - `/health.config.hash` matches new hash.
   - Daily-monitor summary shows new hash.
5. Document change in Ops log (timestamp, tag, who deployed).

### When to Escalate
- Hash in `/health` never matches local config after restart → stop engine and alert devs.

## Scenario 7 – Engine Restart Procedure

### Steps
1. `sudo systemctl status tiyf-engine-demo.service --no-pager`.
2. `sudo systemctl stop tiyf-engine-demo.service`.
3. `sudo journalctl -u tiyf-engine-demo.service -n 200 --no-pager` (review last logs).
4. `sudo systemctl start tiyf-engine-demo.service`.
5. `sudo systemctl status tiyf-engine-demo.service --no-pager`.
6. Verify `/health` and `/metrics` (connected, loop iterations increasing, gvrs fields present).
7. Check daily-monitor run (trigger if needed) to ensure summary line updated.

### When to Escalate
- Service fails to start twice.
- `/health.connected` false or `stream_connected=0` after restart (ties back to Scenario 1).

## Scenario 8 – Daily-Monitor Looks Wrong

### Symptoms
- No new decisions (`decisions_total` not increasing).
- `last_decision_utc` older than expected.
- `risk_config_hash` mismatch.
- Missing `gvrs_*` or `promotion_*` tails.

### What to Do
1. Download latest artifact: `gh run download <run-id> -n daily-monitor-health`.
2. Compare fields vs `/health`.
3. If summary missing fields:
   - Re-run workflow manually (`gh workflow run daily-monitor.yml --ref main`).
4. If engine genuinely idle:
   - Check logs for `kill-switch`, `adapter` warnings.
   - Confirm timeframes still H1/H4.

### When to Escalate
- Discrepancies persist after manual workflow run.
- `/health` shows data but daily-monitor consistently omits it (indicates workflow bug).

## Scenario 9 – Adapter Feed Credentials / Secrets

## Scenario 10 – Reconciliation Drift

### How to Detect
- `/health.reconciliation` block: `mismatches_total`, `runs_total`, `last_status`, `last_reconcile_utc`.
- `/metrics`: `engine_reconcile_runs_total`, `engine_reconcile_mismatches_total`.
- Reconcile journal under `journals/<adapter>/reconcile/`.

### What to Do
1. `curl -s http://127.0.0.1:8080/health | jq '.reconciliation'`.
2. If mismatches > 0, inspect `reconcile.csv` in the latest journal run; confirm symbols and reasons.
3. If mismatches are expected test fixtures (proof/demo), no action. If unexpected, pause trading via kill-switch and escalate.

### When to Escalate
- `last_status` = `mismatch` on real broker live runs.
- Broker API unreachable during reconciliation.

## Scenario 11 – Idempotency Persistence After Restart

### How to Detect
- `/health.idempotency_persistence`: `last_load_utc`, `loaded_keys`, `expired_dropped`.
- `/metrics`: `engine_idempotency_persisted_loaded`, `engine_idempotency_persisted_expired_total`.

### What to Do
1. After restart, confirm `/health.idempotency_persistence.loaded_keys` > 0 for active demo runs.
2. If persistence failed (loaded_keys=0 unexpectedly), check logs for `ALERT_IDEMPOTENCY_PERSISTENCE_FAILED`.
3. Only wipe the persistence file as a last resort and with dev approval; doing so risks duplicate order sends on restart.

### How to Detect
- `/health.secrets` block (from M9-C) showing missing env.
- Logs: `SECRET_PROVENANCE missing` warnings.

### What to Do
1. Verify env vars in `/etc/systemd/system/tiyf-engine-demo.service.d/env.conf` (example).
2. Update secrets via SOP (never commit secrets).
3. Restart engine.

### When to Escalate
- If secrets fail to load even though env conf is correct.

---

_Remember: demo environment only. When in doubt, stop the engine, gather logs (`journalctl`, `/health`, daily-monitor run IDs), and alert the dev on-call._ 
