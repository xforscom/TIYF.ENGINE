# Promotion Framework (Phase 1)

Blueprint v2.0 §7.1 introduces the strategy promotion framework. Phase 1 wires the
promotion configuration into the engine so runtime components can surface the
configured policy.

## Configuration block

The `risk` section in engine configs now accepts an optional `promotion`
object:

```json
"promotion": {
  "enabled": true,
  "shadow_candidates": ["strategyA", "strategyB"],
  "probation_days": 30,
  "min_trades": 50,
  "promotion_threshold": 0.6,
  "demotion_threshold": 0.4
}
```

Field semantics:

- `enabled`: toggles promotion tracking.
- `shadow_candidates`: list of strategy identifiers enrolled in A/B evaluation.
- `probation_days`: minimum days a candidate must remain in shadow mode before
  promotion eligibility.
- `min_trades`: minimum completed trades before a candidate is evaluated.
- `promotion_threshold`: success ratio (0-1) that must be met or exceeded for
  promotion.
- `demotion_threshold`: success ratio (0-1) below which an active strategy is
  demoted back to shadow.

Missing fields fall back to defaults (disabled, empty candidate list, 30 days,
50 trades, thresholds 0.6/0.4).

Sample configs ship with `promotion.enabled` set to `true` purely to surface telemetry in Phase 1; no runtime promotion decisions execute yet.

## Telemetry

- `promotion_config_hash` is emitted via `/health` payloads and
  Prometheus metric `engine_promotion_config_hash{hash="…"} 1`.
- Hashes use the canonical representation of the promotion config to guarantee
  deterministic values across whitespace/property-order differences.

## Next steps

Phase 2 (per Blueprint §7.1) will implement runtime accounting, trade
aggregation, and promotion/demotion decisions. Phase 1 intentionally makes no
changes to execution or journaling paths.

## Phase 2 — Telemetry surfaces

Phase 2 keeps promotion logic in telemetry-only mode but surfaces the config everywhere Ops needs it:

- `/health` exposes `promotion_config_hash` plus a nested `promotion` object (candidates, probation_days, min_trades, promotion_threshold, demotion_threshold). When the block is absent the property is `null`.
- `/metrics` adds gauges `engine_promotion_candidates_total`, `engine_promotion_probation_days`, `engine_promotion_min_trades`, `engine_promotion_threshold`, `engine_demotion_threshold`, and keeps the `engine_promotion_config_hash{hash="…"}` one-hot.
- `daily-monitor` appends `promotion_candidates`, `promotion_probation_days`, and `promotion_min_trades` to the verdict line and archives the same block in `daily-monitor-health/health.json`.
- `tools/PromotionProbe` plus `m7-promotion-proof.yml` provide deterministic evidence by loading the sample configs, emitting `/health`, `/metrics`, and summary artifacts, and grepping for the new fields.
- Demo configs continue to ship with `promotion.enabled=true` purely so telemetry stays populated; no runtime promotion/demotion takes place until Phase 3.

## Interaction with GVRS Live Gate

- Demo configs now flip `risk.global_volatility_gate.enabled_mode` to `"live"` with a conservative `live_max_bucket`. When GVRS deems the regime “Volatile”, new entries are temporarily blocked (exits continue). Promotion telemetry remains unaffected—the GVRS gate simply reduces order flow during extreme volatility before promotions graduate to the next phase.
