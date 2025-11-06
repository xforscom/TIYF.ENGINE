# Strategy Promotion Framework (Phase 1)

Blueprint v2.0 §7.1 introduces the strategy promotion framework. Phase 1 wires the
promotion configuration into the engine so runtime components can surface the
configured policy.

## Distinction from M1 Config Promotion

This feature is **distinct** from the M1 "Promotion" CLI tool (documented in README.md under "Promotion (M1)"):

- **M1 Config Promotion** (existing): A/B testing tool that compares two different engine configurations (baseline vs candidate) by running simulations and validating determinism, safety gates, and performance metrics. Used for gating config changes before deployment.

- **M7 Strategy Promotion Framework** (this feature): Runtime framework for managing strategy lifecycle transitions. Tracks shadow strategies alongside active strategies, evaluates their performance over time, and provides the foundation for automatic promotion/demotion decisions based on success ratios. Phase 1 is telemetry-only; Phase 2 will implement runtime promotion logic.

## What does this feature do?

The strategy promotion framework enables **dynamic strategy lifecycle management**:

1. **Shadow Evaluation**: Candidate strategies run in "shadow mode" alongside active strategies, executing trades in a non-impactful way to gather performance data.

2. **Probation Period**: Strategies must meet minimum criteria (time in probation, minimum trades executed) before being eligible for promotion evaluation.

3. **Automatic Promotion/Demotion** (Phase 2): Based on success ratios compared to thresholds, strategies can be automatically promoted to active trading or demoted back to shadow mode.

Phase 1 (current) focuses on configuration parsing, hash tracking, and telemetry emission. No runtime promotion decisions are made yet.

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
  promotion. For example, `0.6` means a strategy must have a 60% success rate
  (winning trades / total trades) to be promoted from shadow to active.
- `demotion_threshold`: success ratio (0-1) below which an active strategy is
  demoted back to shadow. For example, `0.4` means an active strategy with less
  than 40% success rate will be demoted. The gap between promotion and demotion
  thresholds creates hysteresis to prevent rapid flip-flopping.

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
