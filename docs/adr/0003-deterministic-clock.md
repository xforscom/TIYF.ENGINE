# 0003 - Deterministic Clock Strategy
- Status: Accepted
- Date: 2025-10-05

## Context
Clock determinism is foundational. Need a seam for simulated time vs. real UTC while ensuring pure functions never read ambient system time.

## Decision
Define `IClock` in Core with `UtcNow` and `Tick()` (for advancing in simulation). Provide:
- `SystemClock` (production / wall-clock)
- `DeterministicSequenceClock` (pre-seeded timestamps for replay)
- `FixedStepClock` (increment by fixed interval)

Clock instances injected into engine loop; never static.

## Consequences
+ Replay fidelity: same sequence => same outputs.
+ Test ease: unit tests inject synthetic time.
- Slight boilerplate for passing clock around.

## Alternatives Considered
1. Static `DateTime.UtcNow` usage – rejected (non-deterministic).
2. Async event loop time provider – premature complexity.
