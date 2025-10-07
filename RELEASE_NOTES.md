# Release Notes

## v0.4.0-m1-promotion (M1 Promotion & Rollback)

Highlights:

- Promote CLI (accept / reject) with deterministic exit codes (0 accept, 2 reject, 1 error)
- A/B parity enforcement via canonical CSV + SHA-256 hashes (events & trades)
- Culture-invariant, atomic journaling (no wall-clock usage in promotion journal)
- CI workflow (`m1-promotion.yml`) executes accept + reject scenarios and uploads artifacts
- Deterministic promotion journal: PROMOTION_BEGIN_V1, PROMOTION_GATES_V1, PROMOTION_ACCEPTED_V1 | PROMOTION_REJECTED_V1 (+ ROLLBACK_* on reject)
- Decision file (`promotion_decision.json`) containing acceptance outcome and hashes

Quality Gates:

- Build & Tests: PASS
- Determinism: PASS (culture en-US vs de-DE identical promotion.events.csv hash)
- Safety: PASS (M0 fixture emits zero ALERT_BLOCK_* events)

Usage snippet:

```powershell
dotnet run --project src/TiYf.Engine.Tools -- promote run `
  --config tests/fixtures/backtest_m0/promotion.json `
  --output artifacts/m1_promo
```

See README section "Promotion (M1)" for details.
