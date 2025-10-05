# ADR-0005: Deprecate BAR_V0; BAR_V1 Canonical (schema_version=1.1.0)

Status: Accepted  
Date: 2025-10-05

## Context

Early prototype journal rows emitted BAR entries without an `IntervalSeconds` field ("BAR_V0"). These legacy rows created ambiguity in:

- Deterministic replay keying (composite keys could collide across intervals).
- Diff tooling (default key inference had to branch on presence/absence of interval metadata).
- Future multi-interval / multi-instrument extensions (difficult to disambiguate overlapping bars).

## Decision

Adopt a single canonical bar event format `BAR_V1` under `schema_version=1.1.0`.

The canonical logical column semantics are:

```text
timestampUtc,eventType,instrumentId,intervalSeconds,openTimeUtc,closeTimeUtc,open,high,low,close,volume,schema_version,config_hash
```

Implementation detail: The persisted CSV still stores a `payload_json` column; the structured JSON for `BAR_V1` MUST contain:

```jsonc
InstrumentId.Value
IntervalSeconds
StartUtc
EndUtc
Open
High
Low
Close
Volume
```

All journals MUST write only `BAR_V1` for bar data. No other BAR variants are permitted starting with this version.

## Consequences

- Diff/verification default composite key is now: `instrumentId,intervalSeconds,openTimeUtc,eventType`.
- Legacy journals lacking `IntervalSeconds` are considered pre-1.1.0 and must be migrated (e.g., inject `IntervalSeconds` based on inferred interval) before being diffed against 1.1.0 journals.
- Replay tooling can assume interval uniqueness without scanning future rows.
- Risk tooling and snapshot persistence can rely on stable key shapes.

## Migration Notes

1. Identify legacy rows: payload lacks `IntervalSeconds` AND `event_type=BAR_V1` or older `BAR`.
2. Compute interval by `(EndUtc - StartUtc).TotalSeconds` or configured intervals used at run time.
3. Re-emit or transform JSON payload to include `IntervalSeconds`.
4. Set file header to `schema_version=1.1.0` once all rows are upgraded.

## Related

- ADR-0004 (Journaling Format) – this ADR narrows acceptable BAR variants.
- Diff Tool (Packet C) – now leverages intervalSeconds in default key inference.
