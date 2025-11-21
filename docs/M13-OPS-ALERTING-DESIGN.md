Demo-only, Phase 1 alerting (minimal viable sink; no impact on real-money configs).

Scope
- Deliver a simple alert pipeline from engine host to an external sink (Discord/webhook or file) for demo only.
- Secrets are env-only; no tokens in JSON/configs/logs.
- No trade/risk behaviour changes; alerts are observability-only.

Alert model
- category: adapter | risk_rails | reconcile | system
- severity: info | warn | error
- summary: short string; details optional; occurred_utc is UTC only.

Sink configuration (env only)
- ALERT_SINK_TYPE: discord | file | none (default none/no-op).
- ALERT_DISCORD_WEBHOOK_URL: Discord webhook (required when type=discord).
- ALERT_FILE_PATH: file output (required when type=file; used in proof).
- Environment label is derived from config filename; no secrets logged.

Sources (Phase 1)
- Adapter disconnect or stale heartbeat.
- Risk rails hard blocks (including broker guardrail).
- Reconciliation status mismatch.
- (Proof only) simulated monitor failures.

Telemetry
- /metrics: engine_alerts_total and engine_alerts_total{category="…"}.
- /health: alerts_total and alerts_by_category map.
- Optional daily-monitor tail can report alerts_total if added; not required for Phase 1.

Proof (m13-alerting-proof)
- Uses AlertProbe with file sink to emit adapter/risk_rails/reconcile alerts.
- Artifacts: alerts.log, summary.txt (counters per category), metrics.txt, health.json.
- Deterministic: fixed timestamps, no external network calls.

Posture
- Demo OANDA only; real-money untouched.
- Sink is best-effort and can be disabled (ALERT_SINK_TYPE=none).
- Exits, risk rails, GVRS, promotion unchanged; alerts are side-channel only.
