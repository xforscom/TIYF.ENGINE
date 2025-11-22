# TIYF.ENGINE Handbook (code‑verified)

> Source of truth is the current `main` branch: solution files, sample configs, workflows, and probe tests. Statements below reflect what is implemented now (demo posture), not future intent.

## 1) Overview
- **Purpose:** Automated FX engine with deterministic proofs and strong observability. Current runtime is demo OANDA only; no real‑money configs active.
- **Posture on main:** GVRS live gate (demo), risk rails live on demo, broker caps enforced on demo, promotion runtime shadow‑only, news blackout enabled, alerting to Discord/file/none by env, config SoT surfaced (config_id, hashes).
- **Milestones M0–M15 (current state):**
  - M0 determinism proof in place.
  - M6 execution hardening A (idempotency, retries) shipped.
  - M7 promotion telemetry shipped; runtime remains shadow.
  - M8-B reconcile + idempotency persistence + slippage presets shipped; telemetry‑only auto‑heal.
  - M9 news + config SoT + secrets hygiene shipped.
  - M10 GVRS live gate (entries blocked in Volatile) demo‑only.
  - M11-B risk rails live (broker/global/symbol/cooldown) demo‑only.
  - M12 promotion runtime shadow only.
  - M13 alerting pipeline (Discord/file/none) demo‑only.
  - M14 demo acceptance proof + window live on demo.
  - M15 real‑money cutover not implemented; roadmap only.

## 2) Architecture map
- **Solution layout:**  
  - Engine: `src/TiYf.Engine.Core`, `src/TiYf.Engine.Sim`, `src/TiYf.Engine.Host`.  
  - Tools/probes: `tools/*` (RiskRailsProbe, AlertProbe, DemoAcceptanceProbe, etc.).  
  - Tests: `tests/TiYf.Engine.Tests`, `tests/OpsDashboard.Tests`.  
  - Dashboard: `tools/OpsDashboard` (read‑only /health and /metrics viewer).  
- **Engine loop:** `src/TiYf.Engine.Host/EngineLoopService.cs` wires config/adapters; `TiYf.Engine.Sim/EngineLoop.cs` runs bars → decisions → adapters with gates (GVRS, risk rails, news blackout).  
- **Adapters:** OANDA demo (primary), cTrader demo, mock feeds; settings in `src/TiYf.Engine.Sim/OandaAdapterSettings.cs` and peers.  
- **Journalling/events:** Decisions and adapter results recorded via `TiYf.Engine.Sim` journal classes; proofs read events.csv from probes.  
- **Health/Metrics:** Produced in host via `EngineHostState` and formatted by `EngineMetricsFormatter`; exposed at `/health` (JSON) and `/metrics` (Prometheus text).

## 3) Blueprint v2.0 (GVRS‑aware)
- **Signal:** GVRS raw/ewma/bucket computed in sim layer; metrics `engine_gvrs_bucket{bucket="…"}`, health fields `gvrs_raw/ewma/bucket`.  
- **Gate:** Configured via risk config `global_volatility_gate` (live mode). Entries blocked when bucket exceeds `live_max_bucket`; exits always allowed. Alerts `ALERT_BLOCK_GVRS_GATE`, metrics `engine_gvrs_gate_blocks_total`, health `gvrs_gate.blocking_enabled/bucket/last_block_utc`.  
- **Relation to rails/promotion:** GVRS gate runs before risk rails; promotion remains shadow‑only and unaffected by GVRS blocks.

## 4) Risk & controls
- **Modes:** `risk_rails_mode` supports disabled/telemetry/shadow/live. Live blocks entries; shadow/telemetry emit SOFT alerts only; disabled bypasses rails.  
- **Gates:** Daily loss cap (account ccy), global max units, per‑symbol unit caps, cooldown. Broker guardrail (M11-B) duplicates broker caps and blocks in live mode.  
- **Alerts:** SOFT for telemetry, HARD for live blocks (`ALERT_RISK_*_HARD`, broker caps `ALERT_RISK_BROKER_CAP_HARD`).  
- **Config:** Risk config in sample configs (`sample-config.demo-oanda.json`) with live mode and broker caps; proof fixtures under `proof/`.  
- **Entries vs exits:** Exits never blocked; rails evaluate only risk‑increasing entries.  
- **Proofs/tests:** `tools/RiskRailsProbe`, workflows `m11-risk-rails-proof.yml`; unit tests in `tests/TiYf.Engine.Tests/RiskRails*` enforce blocking/telemetry semantics.

## 5) GVRS live gate
- **Computation:** In sim layer; bucket from GVRS metrics.  
- **Config knobs:** `global_volatility_gate.enabled_mode` (`live` for demo), `live_max_bucket` (e.g., Moderate), optional `live_max_ewma`.  
- **Signals:** Metrics `engine_gvrs_gate_blocks_total`, `engine_gvrs_gate_is_blocking{state="volatile"}`, health `gvrs_gate` block. Demo configs set live; real‑money must stay shadow/disabled.

## 6) Promotion runtime (shadow)
- **Config:** `promotion` block with `candidates`, `probation_days`, `min_trades`, `promotion_threshold`, `demotion_threshold`.  
- **Runtime:** `PromotionShadowRuntime` evaluates only recent trades within probation window; shadow counters only (no routing).  
- **Metrics/health:** `engine_promotion_*` metrics; health promotion block with shadow snapshot.  
- **Proof:** `m12-promotion-runtime-proof.yml` validates probation window, thresholds, and shadow counters.

## 7) News & blackout
- **Providers:** Config selects `news_provider` (file default) or `http`. Helper resolves provider from explicit field then blackout source type.  
- **Blackout:** News events trigger blackout windows; gates evaluated in EngineLoop before orders.  
- **Secrets:** HTTP provider uses env (e.g., FMP_API_KEY) only; provenance logged without values (`secrets` block in health, `engine_secret_provenance` metrics).  
- **Proof:** `m9-news-proof.yml` uses deterministic file fixture; checks events, blackout counts, metrics `engine_news_source`, `engine_news_events_fetched_total`.

## 8) Execution hardening (M8)
- **Reconciliation:** `ReconciliationRecordBuilder` compares broker snapshot vs engine positions; telemetry only. Metrics `engine_reconcile_*`, health `reconciliation` block.  
- **Idempotency:** Caches (order/cancel) persisted with TTL; metrics `engine_idempotency_cache_size`, `engine_idempotency_evictions_total`, health `idempotency_persistence`.  
- **Slippage presets:** Zero default; session_pips model available (`SessionPipSlippageModel`). Config selectable.  
- **Proof:** `m8-execution-hardening-proof.yml` via `tools/ReconcileProbe`/ExecutionProof asserts mismatch classification, idempotency behavior.

## 9) Config SoT & secrets (M9)
- **ConfigId:** Parsed from `configId` in configs; surfaced in health (`config.id`), metrics (`engine_config_id{config_id="…"}`), daily-monitor verdict tail.  
- **Hashes:** Metrics `engine_config_hash`, `engine_risk_config_hash`, promotion hash; health includes config.path/hash and risk/promotion hashes.  
- **Secrets:** Env-only; provenance tracked in health `secrets` block and metrics `engine_secret_provenance{integration,source}`.  
- **Proof:** `m9-config-sot-proof.yml` validates config_id/hash surfacing and secret provenance with dummy env.

## 10) Alerting (M13)
- **Model:** Alert categories (adapter, risk_rails, reconcile, system, etc.), severities; enqueued and sent via sinks.  
- **Sinks:** Env `ALERT_SINK_TYPE` (discord/file/none), with `ALERT_DISCORD_WEBHOOK_URL` for Discord; defaults to no-op if unset.  
- **Triggers:** Adapter disconnect, risk HARD blocks, reconcile mismatch, system alerts; metrics `engine_alerts_total{category=…}`; health `alerts_total/alerts_by_category`.  
- **Proof:** `m13-alerting-proof.yml` uses `AlertProbe` to emit deterministic alerts to file sink; asserts summary and alerts.log.

## 11) Demo acceptance (M14)
- **Probe:** `tools/DemoAcceptanceProbe` (loop disabled) collects health/metrics/events and summary.  
- **Acceptance conditions:** reconcile_drift=0, fatal_alerts=0, rails active, GVRS live gate on demo, alert sink configured, config_id present.  
- **Proof:** `m14-demo-acceptance-proof.yml` asserts summary PASS, no fatal alerts, required metrics/health fields present.  
- **Acceptance window:** Demo engine runs with `configId=demo-oanda-v1`, GVRS/risk rails live, promotion shadow, alerting on.

## 12) Real‑money cutover outline (M15)
- **Not implemented:** No real-money configs, no production adapters active, no live promotion routing.  
- **Roadmap expectations:** Reuse rails/gates/promotion with stricter proofs, audited secrets, staged enablement; acceptance evidence required before any cutover.

## 13) CI / proofs / workflows
- **Key workflows (.github/workflows):**
  - `CI.yml`: build Release, run engine/tools/tests, verify sim DLL.
  - `verify-strict`, `verify-deep`: extended test suites.
  - `m0-determinism`: determinism checks.
  - `dataqa-tolerance`, `nightly-canary`, `penalty-scaffold`, `demo-journal-verify`: domain-specific validations.
  - Proofs: `m8-execution-hardening-proof`, `m9-config-sot-proof`, `m9-news-proof`, `m10-gvrs-gate-proof`, `m11-risk-rails-proof`, `m12-promotion-runtime-proof`, `m13-alerting-proof`, `m14-demo-acceptance-proof`.
  - `daily-monitor.yml`: hits `/health`, builds verdict line, uploads health.json.
  - `m14-demo-acceptance-proof` and others upload artifacts (summary/metrics/health/events) for audit.

## 14) Ops / runtime
- **Systemd (demo):** `tiyf-engine-demo.service` runs Host with `--config /opt/tiyf/current/sample-config.demo-oanda.json`, env `ENGINE_HOST_ENABLE_LOOP=true`, adapter creds, alert sink envs.  
- **Dashboard:** `tiyf-dashboard.service` runs `OpsDashboard.dll` with `ASPNETCORE_URLS=http://127.0.0.1:5010`, `DASHBOARD_Dashboard__EngineBaseUrl=http://127.0.0.1:8080`, reverse-proxied by nginx at https://ops.xfors.com.  
- **Redeploy pattern:** publish to `/opt/tiyf/releases/<sha>`, symlink `/opt/tiyf/current`, restart service, verify `/health` and `/metrics`.  
- **Daily-monitor:** Workflow_dispatch or cron; fetches `/health`, validates fields, emits verdict line (adapter, connected, heartbeats, risk hashes, blocks, promotion, gvrs, config_id tail).
- **Interpreting verdict:** Key fields—adapter/connected/heartbeat_age, stream_connected, bar_lag_ms, positions/orders, risk_events/alerts/orders rejects, risk_config_hash, risk_blocks/throttles, gvrs bucket, promotion params, config_id.

## 15) Dev process & orchestration
- **Branch/PR/tag:** Feature branches with full CI/proofs green before merge; squash-merge; tags per milestone (e.g., v1.14.x).  
- **Gate:** Ops “GO” required before merges affecting runtime.  
- **New agents:** Use this handbook + proofs + workflows + sample configs; verify changes via probes/tests; keep determinism; avoid touching runtime configs without explicit instruction.

## 16) Glossary & invariants
- **GVRS:** Global volatility regime signal (raw/ewma/bucket).  
- **Rails modes:** disabled (off), telemetry/shadow (SOFT alerts only), live (blocks entries).  
- **Broker guardrail:** Broker caps (daily loss/global/symbol) mirrored; live blocks entries.  
- **Config SoT:** configId + hashes in health/metrics/daily-monitor; sample demo uses `demo-oanda-v1`.  
- **Secrets:** Env-only; provenance tracked; never in JSON.  
- **Alerts:** Categorized counters; sinks set by env; no secrets logged.  
- **Exits:** Never blocked by GVRS or risk rails.  
- **Real-money:** Telemetry/disabled only; demo configs carry live gates/rails.  
- **Determinism:** Proofs run with fixed fixtures/timestamps; no wall-clock in proofs.  
- **Proof artifacts:** summary.txt/metrics.txt/health.json/events.csv for audit.  
- **Health/metrics:** Primary observability contract; dashboard/daily-monitor consume them.
