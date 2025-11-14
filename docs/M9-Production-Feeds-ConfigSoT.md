# M9 – Production Feeds & Config Source of Truth

## Current State
- **News blackout**: Risk config parser and runtime accept a `news_blackout` block, but today it is exercised only through stub fixtures (e.g. `proof/news.json`, `RiskRailsProbe`). No live news adapter feeds the blackout gate, and telemetry merely reflects stubbed data.
- **Config hashes**: `risk_config_hash` and `promotion_config_hash` are emitted via `/health`, `/metrics`, daily-monitor summaries, and proof artifacts (risk/promotion probes). They reflect whatever risk/promotion config was parsed at boot, but there is no strong linkage to a Git-based source of truth beyond these hashes.
- **Secrets**: Broker/news credentials are injected via environment variables referenced in sample configs (`env:OANDA_PRACTICE_TOKEN`, `CT_*`). Tests mock these env vars, and workflows set them through GitHub secrets. There is no centralized “secret provenance” indicator beyond knowing which env var was read.

## Phase M9-A – Production News Feed & Blackout Telemetry
**In scope**
- Introduce/plug a real news adapter interface into existing `news_blackout` plumbing (no blocking yet).
- Surface telemetry showing live blackout state in `/health`, `/metrics`, daily-monitor, and proofs.
- Minimal alerting/logs when blackout events are received or time out.

**Explicitly not in scope**
- No trading strategy changes, kill-switch triggers, or risk-rail alterations.
- No broker execution gating yet; telemetry-only validation.

**Likely touch-points**
- `src/TiYf.Engine.Core/RiskConfigParser.cs` (adapter hooks).
- `src/TiYf.Engine.Host/EngineLoopService.cs` & `EngineHostState` (telemetry updates).
- News adapter interfaces (`src/TiYf.Engine.Sim/...`), proof fixtures under `proof/news.json`.

**Proof expectations**
- Extend an existing proof workflow (or add lightweight job) that replays sample news feed data and asserts the blackout telemetry toggles.
- Daily-monitor line includes explicit blackout status.

## Phase M9-B – Config Source of Truth (Git + Hashes)
**Status: ✅ implemented on main (`feat/m9b-live-news-adapter` / PR #109).**  
Engine startup now emits an explicit `engine_config_sot path=… hash=…` log, `/health` exposes `config { path, hash }`, and `/metrics` surfaces both `engine_config_hash{hash="…"}` and `engine_risk_config_hash{hash="…"}`. Daily fan-outs and proofs capture the same values so Ops can tie a running host back to the Git blob that produced it.

**In scope**
- Define how configs are stored as Git-tracked blobs (e.g., `/config/*.json`, versioned).
- Compute and emit a `config_sot_hash` at boot alongside risk/promotion hashes.
- Log/alert when expected hashes differ (telemetry + alert only, no blocking).
- Ensure hashes appear in `/health`, `/metrics`, proofs, and daily-monitor.

**Explicitly not in scope**
- No runtime halt/block when hashes mismatch (Phase B is detect-only).
- No new workflows; reuse existing fans/proofs for evidence.

**Likely touch-points**
- `EngineConfigLoader`, `EngineLoopService` (hash computation & telemetry).
- Daily-monitor formatter/scripts.
- Proof harnesses that read config hashes (risk/promotion probes).

**Proof expectations**
- `m9-config-sot-proof` workflow launches the host (loop disabled), scrapes `/health` + `/metrics`, and asserts the config path/hash plus secret provenance gauges (see M9-C below).
- Daily-monitor artifact shows the config hash alongside the existing risk/promotion hashes.

## Phase M9-C – Secrets Handling & Provenance
**Status: ✅ implemented on main (`feat/m9c-config-sot`).**  
Secret reads now update a `SecretProvenanceTracker`, `/health` exposes `secrets { integration: "env|missing|…" }`, `/metrics` outputs `engine_secret_provenance{integration=\"…\",source=\"…\"}`, and no secret values (or env var names) are logged or exported. Demo configs remain file-backed; HTTP news/API integrations stay opt-in via config + env secrets only.

**In scope**
- Document approved secret surfaces (env vars, secret store injection) and forbid secrets in JSON configs or committed artifacts.
- Audit adapters, config loader, and workflows to ensure secrets are only pulled from env/secret inputs.
- Introduce minimal telemetry/logging that indicates source (e.g., `source=env:OANDA_PRACTICE_TOKEN`) without exposing actual values.

**Explicitly not in scope**
- No change to credential rotation or vault integration beyond documenting/enforcing env usage.
- No runtime hash of secret values (avoid storing/deriving sensitive material).

**Likely touch-points**
- Adapter settings loaders (`OandaAdapterSettings`, `CTraderAdapterSettings`).
- Workflows that pass secrets to runs (`demo-*/nightly` jobs).
- Documentation in `docs/DEMO-RUN.md`, runbooks.

**Proof expectations**
- `m9-config-sot-proof` workflow verifies that `/health.secrets` includes `oanda_demo` and `/metrics` reports `engine_secret_provenance{integration=\"oanda_demo\",source=\"env\"} 1`, all with dummy env values (no secret data leaked).
- Daily-monitor lines carry the new config + secrets tail so Ops can audit provenance at a glance.
## Overall Notes
- Each phase delivers telemetry/proof evidence before any runtime gating.
- M9 is sequencing groundwork for eventual M10+ runtime enforcement.
