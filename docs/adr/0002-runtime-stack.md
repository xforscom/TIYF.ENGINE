# 0002 - Runtime & Stack
- Status: Accepted
- Date: 2025-10-05

## Context
Need high-performance, strongly typed, mainstream ecosystem with first-class tooling for deterministic logic.

## Decision
Use .NET 8 / C# 12. Single-process console sim (no ASP.NET hosting). xUnit for testing. No external DB yet. File-based persistence only (CSV + JSON). Hashing via SHA256 from BCL.

## Consequences
+ Rapid development, value types & spans available if we need perf later.
+ Cross-platform (Windows/Linux) with single code base.
+ Low operational overhead initially.
- Need eventual migration path for journaling into PostgreSQL.
- Must self-implement minimal atomic file semantics.

## Alternatives Considered
1. Go – simpler deploy but less rich generics maturity for advanced domain patterns.
2. Rust – high perf, but slower iteration for early domain modeling.
3. Python – fast prototyping, weaker at sustained deterministic perf under load.
