# M4 Risk Guardrails – Acceptance Bundle

> Populate CI links & SHAs after first green run of `m4-risk-matrix` and `verify-strict` workflows on default integration branch.

## CI Runs

Populate after green CI:

- Risk Matrix Workflow: [m4-risk-matrix.yml](https://github.com/xforscom/TIYF.ENGINE/actions/workflows/m4-risk-matrix.yml)
  - Latest green run: [Actions • M4 Risk Matrix](https://github.com/xforscom/TIYF.ENGINE/actions/workflows/m4-risk-matrix.yml)
- Strict Verify Workflow: [verify-strict.yml](https://github.com/xforscom/TIYF.ENGINE/actions/workflows/verify-strict.yml)
  - Latest green run: [Actions • Verify Strict](https://github.com/xforscom/TIYF.ENGINE/actions/workflows/verify-strict.yml)
- Commit (docs + workflow): `83a302de00000000000000000000000000000000`

## Event Excerpts

Evaluation + exposure alert pair (from active_with_breach run):

```text
2,2025-01-02T00:01:00.0000000Z,INFO_RISK_EVAL_V1,{"symbol":"EURUSD","ts":"2025-01-02T00:01:00Z","net_exposure":0,"run_drawdown":0}
3,2025-01-02T00:01:00.0000000Z,ALERT_BLOCK_NET_EXPOSURE,{"symbol":"EURUSD","ts":"2025-01-02T00:01:00Z","limit":0,"value":0,"reason":"net_exposure_cap"}
```

(No drawdown alert in this scenario; exposure block triggers.)

## Promotion Decision Snippets

Accepted (active vs active parity):

```jsonc
"risk": {
  "baseline_mode": "active",
  "candidate_mode": "active",
  "baseline": { "eval_count": 120, "alert_count": 0 },
  "candidate": { "eval_count": 120, "alert_count": 0 },
  "parity": true,
  "reason": "parity",
  "diff_hint": ""
}
```

Rejected (downgrade active → shadow):

```jsonc
"risk": {
  "baseline_mode": "active",
  "candidate_mode": "shadow",
  "parity": false,
  "reason": "risk_mode_downgrade",
  "diff_hint": "mode downgrade"
}
```

Rejected (shadow → active with zero-cap gating):

```jsonc
"risk": {
  "baseline_mode": "shadow",
  "candidate_mode": "active",
  "parity": false,
  "reason": "risk_mismatch",
  "diff_hint": "introduced zero-cap EURUSD"
}
```

  Diagnostics (on failure):

  ```text
  PROMOTE_FACTS riskBase=shadow riskCand=active candZeroCap=true baseRows=6 candRows=0 baseAlerts=0 candAlerts=2 qaPassed=true
  PROMOTE_DECISION finalReason=risk_mismatch accepted=false
  ```

## Parity Artifact Hashes

Collected via Python collector (normalized rules: events skip meta+header; trades drop header & remove config_hash; LF normalization):

```text
off:                  events_sha=F1F816308C601785DA7E42C9E0250E0C35CF84144131AB9C172838435F138EC8 trades_sha=6B2DE3E5109B2562E4B3D30E93615614F6AD752311E1DFF46400E00565EE5FA1
shadow:               events_sha=2488E1AB5737C77BCDAF66B8E173DAAC8F2E8728EAB70D3312FF1A06CBD0F75A trades_sha=6B2DE3E5109B2562E4B3D30E93615614F6AD752311E1DFF46400E00565EE5FA1
active_no_breach:     events_sha=2488E1AB5737C77BCDAF66B8E173DAAC8F2E8728EAB70D3312FF1A06CBD0F75A trades_sha=6B2DE3E5109B2562E4B3D30E93615614F6AD752311E1DFF46400E00565EE5FA1
active_with_breach:   events_sha=ED9217EE3E8DD6890877BE970D177309BC44CDA4CC3F4D88C87C78E49B13BBCD trades_sha=E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855
```

Parity assertions:

```text
off.trades_sha == shadow.trades_sha
shadow.trades_sha == active_no_breach.trades_sha
active_with_breach.trades_sha != shadow.trades_sha (blocking alert triggered)
```

## Test Summary

All risk & promotion gating tests green:

- `RiskGuardrailEngineTests.*` (parity, exposure block, drawdown block, ordering)
- `PromotionCliRiskParityTests.*` (benign upgrade accept, downgrade reject, zero-cap reject)

## Determinism Notes

- Off ↔ Shadow trade hash parity enforced in CI.
- Active divergence only with blocking alert; zero-cap edge covered.
- Drawdown determinism via `forceDrawdownAfterEvals` (test-only hook) verified.
- Hash normalization excludes meta + headers; trades `config_hash` column stripped by name.

## Commit / Provenance

- Workflow commit SHA: `83a302de00000000000000000000000000000000`
- Docs commit SHA: `83a302de00000000000000000000000000000000`
- Tag (planned): `v0.8.0-m4-risk`

## Sign-off Checklist

- [ ] CI risk matrix green
- [ ] Strict verifier green
- [x] Event excerpts captured
- [x] Promotion decision JSONs captured
- [x] Parity artifact hashes recorded
- [x] Release notes updated (v0.8.0-m4-risk)
- [ ] Tag pushed & annotated

---
(Replace placeholders before final archival.)
