# M11 – Risk Rails Finalization (Phase A)

## Scope (Phase A – telemetry + proof only)
- Broker-level daily loss cap (account ccy) – telemetry/alerts only.
- Global max open position units – telemetry/alerts only.
- Per-symbol unit caps – telemetry/alerts only.
- Optional cooldown guard (consecutive losses/time window) – telemetry/alerts only.

## Non-scope (Phase A)
- No new order blocking/resizing yet; enforcement flip deferred to M11-B.
- No changes to existing M4/M5 rails.
- No broker API pushes.

## Touch points
- Risk config parser/model, RiskRails evaluation, EngineHostState/metrics, EngineLoopService wiring, proof harness.

## Proof expectations
- Deterministic probe + workflow that drives all new rails and records metrics/health evidence.
