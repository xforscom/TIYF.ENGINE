# Strategy Promotion Framework (Phase 1)

Blueprint v2.0 §7.1 introduces the **strategy promotion framework**. Phase 1 wires the
promotion configuration into the engine so runtime components can surface the
configured policy.

> **Note**: This is distinct from the M1 "config promotion" feature (documented in
> README.md § Promotion (M1)), which handles A/B testing and deterministic gating
> of configuration changes. The strategy promotion framework manages runtime
> selection and performance-based promotion/demotion of trading strategies.

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

### How it works

The strategy promotion framework enables the engine to:

1. **Shadow mode**: Run candidate strategies alongside the active strategy without
   executing their trades (observability only).
2. **Performance tracking**: Accumulate success metrics for each candidate during
   the probation period.
3. **Promotion**: Automatically promote a candidate to active status when it meets
   the configured success threshold.
4. **Demotion**: Automatically demote an active strategy back to shadow when its
   performance falls below the demotion threshold.

Field semantics:

- `enabled`: toggles promotion tracking (default: `false`).
- `shadow_candidates`: list of strategy identifiers enrolled in shadow evaluation.
  These strategies run in parallel with the active strategy, but their trade
  decisions are not executed (shadow mode).
- `probation_days`: minimum days a candidate must remain in shadow mode before
  promotion eligibility (default: 30). This ensures sufficient observation time
  before trusting a strategy with live capital.
- `min_trades`: minimum completed trades before a candidate is evaluated for
  promotion (default: 50). Prevents promotion based on statistically insignificant
  sample sizes.
- `promotion_threshold`: success ratio (0.0 to 1.0) that must be met or exceeded
  for a shadow candidate to be promoted to active status (default: 0.6). For
  example, 0.6 means the candidate must achieve at least a 60% success rate.
- `demotion_threshold`: success ratio (0.0 to 1.0) below which an active strategy
  is demoted back to shadow mode (default: 0.4). This protects against sustained
  underperformance. For example, 0.4 means an active strategy will be demoted if
  its success rate falls below 40%.

### Threshold interpretation

- **Success ratio**: Defined as profitable trades divided by total trades, or
  similar performance metric (exact definition implemented in Phase 2).
- **Promotion threshold** (e.g., 0.6): Acts as a quality bar for new strategies.
  Higher values (closer to 1.0) require stronger evidence of profitability before
  promotion.
- **Demotion threshold** (e.g., 0.4): Acts as a floor for active strategies.
  Lower values (closer to 0.0) allow more tolerance for losses before demotion.
- **Hysteresis gap**: The gap between promotion and demotion thresholds (e.g.,
  0.6 vs 0.4) prevents oscillation when a strategy hovers near a single boundary.

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
