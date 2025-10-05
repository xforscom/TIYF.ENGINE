# 0001 - Architecture Style
- Status: Accepted
- Date: 2025-10-05

## Context
Need a structure that maximizes determinism, testability, and extensibility for a trading engine MVP. Early phase: single-process, synchronous loop with deterministic replay and audit journaling. Future: optional distribution and scaling.

## Decision
Adopt a Modular Monolith with Hexagonal (Ports & Adapters) layering.

Layers:
- Core Domain (pure logic, no IO): time abstractions, bar builder, instrument model, risk interfaces.
- Application/Simulation (engine loop orchestration, use cases, config loader).
- Adapters (sidecar): file journaling, CSV readers, CLI harness, logging appenders.

No microservices, no message bus initially.

## Consequences
+ Fast iteration, minimal deployment friction.
+ Deterministic tests: core is pure and mockable.
+ Clear seam for future distribution: adapters can lift out.
- Single-process limits horizontal scalability until refactor.
- Must enforce boundaries via code reviews and assembly references discipline.

## Alternatives Considered
1. Microservices from day 0 – rejected (complexity, premature distribution).
2. Event Sourced architecture – deferred (overhead not justified yet).
3. Traditional layered (UI/Business/Data) – less explicit about IO boundaries.
