# M3 Plan (v0.6.0-m3-active)

Status: Draft (scaffold) – derived from accepted planning packet.

## Scope

- Data QA: shadow -> active gating
- Sentiment Guard: shadow -> active (clamp applied)
- Risk Guardrails: instrument/notional/net exposure + drawdown alignment
- Promotion CLI: `--verify` integration
- Nightly deterministic canary workflow
- Journal schema versioning roadmap (1.2.0 bump)
- Documentation & release notes updates

## Sequencing

1. Schema bump & verifier allow-list scaffold
2. Data QA active mode (abort event + tolerance profile hash)
3. Sentiment active mode (applied event + clamp influence)
4. Risk guardrail extensions (new alerts, abort on drawdown breach)
5. Promotion `--verify` flag integration
6. Nightly canary workflow (cron + scenario matrix + hash matrix artifact)
7. Docs (README, INTERNAL, RELEASE_NOTES draft)
8. Acceptance artifact capture & tag

## Data QA (Active Mode)

- Config: `featureFlags.dataQa: shadow|active|off`
- Tolerance block hashed -> `tolerance_profile_hash`
- New abort event: `DATA_QA_ABORT_V1`
- Promotion rejects on abort or `passed=false`

## Sentiment Active

- Config: `featureFlags.sentiment: shadow|active|off`
- New event: `INFO_SENTIMENT_APPLIED_V1`
- Clamp modifies downstream signal only in active mode

## Risk Extensions

- Config sections: `instrumentCaps[]`, `netExposure`, `drawdown`
- New alerts: `ALERT_BLOCK_INSTRUMENT_NOTIONAL`, `ALERT_BLOCK_NET_EXPOSURE`, `ALERT_BLOCK_DRAWDOWN`

## Promotion `--verify`

- Pre-gate journal & trades verification for baseline + candidate A/B
- Extend decision JSON with verification statuses

## Nightly Canary

- Cron 02:00 UTC
- Matrix: scenario list file
- Hash matrix output + issue on diff

## Schema Versioning

- Bump to 1.2.0 (additive events only)
- Policy: additive -> minor; breaking -> major

## Guardrails (Reaffirmed)

- UTC timestamps, invariant culture formatting, atomic writes, deterministic A/B, stable trades formatting

## Acceptance Artifacts

- CI links (unit + canary)
- Verify pass/fail samples
- Data QA abort excerpt
- Sentiment clamp excerpt
- Risk guardrail alert excerpt
- Promotion decision with verification block
- Tagged release `v0.6.0-m3-active`

---
Document created: 2025-10-07
