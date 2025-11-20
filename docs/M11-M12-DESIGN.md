# M11-B & M12 Design Notes

_Purpose: capture the next enforcement steps so the following orchestrator can implement them deterministically without re-discovering context._

## Guiding Principles
- Reuse the existing telemetry and proof stacks (`RiskRailTelemetrySnapshot`, `RiskRailsProbe`, `daily-monitor`, `m0-determinism`); do not fork new schemas unless strictly required.
- Determinism first: every new gate must consume clock inputs via `IClock` and must be reproducible in proof workflows.
- Entries may be blocked; exits must always be allowed so the host can flatten safely.
- Demo-first activation. Real-money configs flip only after Ops creates a tracking issue, evidence set, and updated runbooks.

---

## M11-B – Risk Rails Live Gateway

### Rails Moving to Blocking Mode
| Rail | Behaviour in M11-A | Behaviour in M11-B |
| --- | --- | --- |
| Broker daily loss cap (`broker_daily_loss_cap_ccy`) | Telemetry-only counter | Block new entry orders once used > cap; emit `ALERT_RISK_BROKER_DAILY_CAP_HARD` |
| Global max position units (`max_position_units`) | Telemetry only | Block entries that would exceed cap; alert `ALERT_RISK_MAX_POSITION_HARD` |
| Per-symbol unit caps (`symbol_unit_caps`) | Telemetry only | Block entries per symbol beyond cap; alert `ALERT_RISK_SYMBOL_CAP_HARD` |
| Cooldown guard (`cooldown_enabled`) | Telemetry only | When triggered, block entries until cooldown expires; alert `ALERT_RISK_COOLDOWN_HARD` |
| Broker/DD rails from M4 | Already blocking | No change (still enforced upstream) |

Telemetry-only rails that remain non-blocking: promotion counters, secret provenance, GVRS telemetry (shadow mode), and any optional observational metrics.

### Entry vs Exit Semantics
- **Entries:** Evaluate M11 rails before submitting orders. If any blocking condition triggers, suppress the order, emit the relevant risk alert, increment host counters, and record the event in journal/Probe artifacts.
- **Exits / reductions:** Always permitted; rails should log telemetry if the exit occurs during a block, but **must not** prevent protective unwinds.
- **Cooldown:** Start timer when trigger condition met; only entry suppression occurs during the cooldown window.

### Telemetry Reuse
- Continue to populate `RiskRailTelemetrySnapshot` and push into `EngineHostState`. Add booleans/values (`*.blocking_active`) instead of new structures; broker guardrail counters surface via the same snapshot.
- `/metrics` already exposes `engine_risk_*` gauges; add `engine_broker_cap_blocks_total{gate="..."}` for broker guardrail hits alongside existing `engine_risk_blocks_total`.
- `/health.risk_rails` should add a concise `blocking` flag per rail (e.g. `"max_position": {"limit": 100000, "used": 95000, "blocking": false}`) and a `broker_cap_blocks_total`/`broker_cap_blocks_by_gate` section for Ops grep.

### Alerts & Counters
- Alerts (`RiskRailAlert`) should have deterministic event types:  
  - `ALERT_RISK_BROKER_DAILY_CAP_HARD`  
  - `ALERT_RISK_MAX_POSITION_HARD`  
  - `ALERT_RISK_SYMBOL_CAP_HARD` (include `symbol` in payload)  
  - `ALERT_RISK_COOLDOWN_HARD`
- Counters: extend `engine_risk_blocks_total{rail="..."};` and `engine_broker_cap_blocks_total{gate="daily_loss|global_units|symbol_units:<sym>"}`.
- Daily-monitor: append `risk_blocks_total` (existing) and, when non-zero, a tail such as `risk_blocks_breakdown=broker:1,symbol:2,cooldown:0`.

### Acceptance Criteria
1. **Alerts:** Each blocking scenario must emit one alert per decision with payload fields: `decision_id`, `rail`, `limit`, `used`, `config_hash`.
2. **/metrics:**  
   - `engine_broker_cap_blocks_total` and `engine_broker_cap_blocks_total{gate="..."}` increment on guardrail hits.  
   - `engine_risk_cooldown_active` (0/1) reflects cooldown state.  
   - `engine_risk_symbol_unit_cap_used{symbol="EURUSD"}` matches live exposure.
3. **/health:** `risk_rails` block shows config + used + `blocking` booleans; cooldown includes `active_until_utc`.
4. **Proof:** Extend `tools/RiskRailsProbe` to feed deterministic trade sequences forcing each rail to block at least once. Workflow (`m11-risk-rails-proof`) must:  
   - Inspect `summary.txt` for broker and rail blocks (e.g., `broker_cap_blocks=1 risk_blocks=...`).  
   - `metrics.txt` lines for every `engine_risk_blocks_total{rail=...}` label > 0 and `engine_broker_cap_blocks_total` > 0.  
   - `health.json` entries matching the new blocking flags.
5. **Daily-monitor:** Summaries include a tail `risk_blocks_total=X` and optional `risk_blocks_breakdown=...` when X>0.

---

## M12 – Promotion Runtime (Shadow-first)

### Runtime Behaviour
- **Shadow mode first:** Promotion engine evaluates strategies per config but does not alter live routing. Instead, it logs:
  - `promotion_shadow_decision.json` (per cycle) listing candidate id, metrics, decision (`remain_shadow`, `eligible`, `needs_probation`).
  - `/metrics` counters such as `engine_promotion_shadow_promotions_total`, `engine_promotion_shadow_demotions_total`.
- **Routing model:**  
  - Default baseline strategy remains in control.  
  - When promotion criteria satisfied (`min_trades`, `promotion_threshold`, `probation_days`), mark candidate as `shadow_ready`.  
  - Demotion thresholds (`demotion_threshold`) trigger alerts but not routing yet.
- **Probation tracking:** Mirror config values (probation days, minimum trades) in telemetry so Ops can confirm gating logic before live flips.

### Telemetry Integration
- Reuse existing promotion telemetry block in `/health` by adding `shadow_state` fields (`"promotion_shadow": {"candidates": 2, "probation_days": 30, "shadow_ready": ["strategy_A"]}`).
- `/metrics`:  
  - `engine_promotion_shadow_promotions_total{strategy="..."}`  
  - `engine_promotion_shadow_demotions_total{strategy="..."}`  
- Daily-monitor tail: add `promotion_shadow=promoted:0,demoted:0,probation:1`.

### Proof Expectations
- **New workflow:** `m12-promotion-runtime-proof.yml`  
  - Uses deterministic config + historical trade CSV to simulate promotion thresholds.  
  - Emits `summary.txt` (e.g. `promotion_shadow_summary promoted=1 demoted=0 probation=1`).  
  - Stores `shadow_decisions.json` artifact for audit.
- **Acceptance criteria:**  
  1. `/metrics` counters match the simulated decisions.  
  2. `/health.promotion` includes `shadow_state` block.  
  3. Daily-monitor summary includes the new `promotion_shadow` tail (non-zero only when triggered).  
  4. Alerts: `ALERT_PROMOTION_SHADOW_PROMOTED`, `ALERT_PROMOTION_SHADOW_DEMOTED` with payload referencing strategy id, `probation_days`, `min_trades`.

### Path to Full Enforcement
- Once Ops approves, switch from “shadow-only logging” to routing (promotion candidates control order generation). This step should:
  - Be guarded by config flags (`promotion_runtime.enabled`, `promotion_runtime.shadow_only`).  
  - Require fresh proofs + daily-monitor runs with real order modifications disabled until Ops issues a change request.

---

### Notes for the Next Orchestrator
- Use these docs as a baseline. You may refine implementations (e.g., add helper classes or restructure probes) as long as determinism, telemetry parity, and proof coverage remain intact.
- Every runtime flip must have: (1) Config defaulted to “off,” (2) deterministic proof artifacts, (3) Ops-traceable daily-monitor evidence, and (4) tracking issue updates referencing tags and workflow runs.
