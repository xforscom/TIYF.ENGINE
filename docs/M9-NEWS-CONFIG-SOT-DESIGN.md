# M9 – News, Config SoT, and Secrets (Design)

## Scope
- Demo OANDA only; no real-money configs change.
- Behavioural parity: GVRS, risk rails, promotion unchanged; news blackout remains the only news gate.
- Goals: expose config identity, tighten secret provenance (env/secret store only), keep news pipeline ready for file/HTTP backends with deterministic proofs.

## Config Source of Truth (SoT)
- `EngineConfig.ConfigId` parsed from `config_id|config_version|configId`; fallback to file name (`unknown` last resort).
- Surfaces:
  - Startup log: `engine_config_sot config_id=… risk_config_hash=… promotion_config_hash=…`.
  - `/health.config`: `{ path, hash, id }`.
  - `/metrics`: `engine_config_id{config_id="…"} 1`, `engine_config_hash`, `engine_risk_config_hash`.
  - Daily-monitor: appends `config_id=…` tail when present.
- Proof: `m9-config-sot-proof` boots host with demo config and asserts config_id + hash + secret provenance; fails if id/hash mismatch.

## Secrets Hygiene
- Credentials are env/secret-store only; JSON configs must not carry tokens.
- OANDA adapter:
  - `accessToken` must be `env:VAR`; plain values are ignored with provenance `config_ignored`.
  - Secret provenance tracked via `SecretProvenanceTracker` and logged once at startup (`secret_provenance integration=… sources=env`).
- Startup logs never include secret values; proofs grep for provider labels and ensure tokens do not appear in logs.

## News Provider Modes
- Config field `news_provider`: `file` (default) or `http` (compile-time ready).
- `NewsBlackoutConfig` still drives blackout windows; provider selection chooses feed backend:
  - File: path resolved relative to config directory or `/opt/tiyf/news-stub/today.json`.
  - HTTP: uses configured base_uri + headers/query, API key from env (recorded as provenance, value never logged).
- Metrics/health unchanged: `engine_news_events_fetched_total`, `engine_news_blackout_windows_total`, `engine_news_source{type="…"}`, health `news` block.
- Proof: `m9-news-proof` uses deterministic file fixture and asserts events + blackout counters.

## Future hooks (not in this phase)
- Real HTTP feed enablement remains opt-in via config; proofs stay file-backed.
- Any new gates (news-driven blocking) require separate Ops approval and proofs.
