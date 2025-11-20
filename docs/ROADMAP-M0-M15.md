# TIYf Engine Roadmap (M0–M15)

## Current Position (Nov 2025)
- The VPS host (`tiyf-vps-uk-01`) runs the demo OANDA service (H1/H4) with GVRS live gating enabled only for **entries**; exits always proceed.
- News feeds remain file-backed by default; HTTP adapters exist but are disabled unless explicitly configured.
- M11 risk rails: Phase‑A telemetry is complete; Phase‑B live gating is enabled on demo configs only (broker/global/symbol/cooldown), with exits always allowed and real-money configs still telemetry-only.
- Promotion framework Phases 1–2 (config hashing, telemetry, NewsProbe integration) are active, yet promotion decisions are not enforced; they remain observability-only.
- No real-money trading is running; all enforcement is scoped to the demo environment, and any live blocking requires an explicit config flip plus proof.

## Milestone Status

> **Legend:** Status = Done / In progress / Not started / Deferred.  
> Each evidence block lists the canonical tag, representative PR(s), a concrete proof workflow run (when available), and the tracking issue or blueprint reference.

### M0 — Determinism Harness & Tooling
**Status:** Done – determinism gate, CLI harness, and hermetic proofs in place since the v0.x era.

**Summary:** Introduced the `m0-determinism` workflow, run harness, and reproducible build/test gates to ensure every probe/host run can be replayed bit-for-bit.

**Evidence**
- **Tag(s):** `v0.3.1-m0-determinism`
- **Key PR(s):** Foundational determinism merge (pre-public history, see release notes for the tag)
- **Proof workflow(s):** `m0-determinism` – [run 19394242142](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19394242142)
- **Tracking issue(s):** Blueprint v2.0, Milestone M0 (pre-GitHub issue tracker)

### M1 — Telemetry & Promotion Framework (Phase 1)
**Status:** Done – telemetry surface, promotion config hashing, and `/metrics` exposures live.

**Summary:** Established the baseline telemetry schema (engine, risk, promotion) plus config hashing required for downstream promotion proofing.

**Evidence**
- **Tag(s):** `v1.1.0-m1-telemetry`
- **Key PR(s):** Promotion telemetry introduction (legacy PR sequence, see release notes)
- **Proof workflow(s):** `verify-strict` (telemetry schema checks) – [run 19394242134](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19394242134)
- **Tracking issue(s):** Blueprint v2.0 M1 entry

### M2 — Streaming / Market Data Reliability
**Status:** Done – streaming connectors and lag telemetry hardened.

**Summary:** Moved proof fixtures and demo hosts to streaming-backed feeds with lag monitoring (`stream_connected`, `stream_heartbeat_age`) in `/health` & daily-monitor.

**Evidence**
- **Tag(s):** `v1.2.0-m2-streaming`
- **Key PR(s):** Streaming ingest enablement (legacy release)
- **Proof workflow(s):** `verify-deep` (streaming replay checks) – [run 19394242139](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19394242139)
- **Tracking issue(s):** Blueprint v2.0 M2 entry

### M3 — Engine Loop & Execution Core
**Status:** Done – deterministic loop, decision journal, and host surfaces stabilized.

**Summary:** Locked in the execution loop contract (decision ids, journal, retries) and instrumentation that every later milestone builds upon.

**Evidence**
- **Tag(s):** `v1.3.0-m3-loop`
- **Key PR(s):** EngineLoopService hardening (legacy PR)
- **Proof workflow(s):** `CI` build-test suite – [run 19394242147](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19394242147)
- **Tracking issue(s):** Blueprint v2.0 M3 entry

### M4 — Risk Rails Foundation
**Status:** Done – session limits, DD caps, blackout plumbing landed.

**Summary:** Delivered the first risk rails enforcement (session/day windows, drawdown, news blackout) and their observability hooks.

**Evidence**
- **Tag(s):** `v1.4.0-m4-risk`
- **Key PR(s):** Risk rails foundation PR set
- **Proof workflow(s):** `verify-deep` (risk scenarios) – [run 19394242139](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19394242139)
- **Tracking issue(s):** Blueprint v2.0 M4 entry

### M5 — GVRS Shadow Gate
**Status:** Done – GVRS sourced from proofs and surfaced to `/metrics`, but only as a shadow alert.

**Summary:** Added GVRS metrics/alerts (`engine_gvrs_*`) and blackout-to-news coordination while keeping order flow unaffected.

**Evidence**
- **Tag(s):** `v1.5.0-m5-gvrs-shadow`
- **Key PR(s):** GVRS shadow telemetry PR
- **Proof workflow(s):** `m0-determinism` + GVRS unit proofs – [run 19394242142](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19394242142)
- **Tracking issue(s):** Blueprint v2.0 M5 entry

### M6 — Execution Hardening (Phase A)
**Status:** Done – reconciliation host, retry/idempotency, and slippage telemetry delivered.

**Summary:** Ensured fills/reconciliation proofs stay deterministic; added slippage telemetry and host safeguards (idempotent order ids, restart-safe loops).

**Evidence**
- **Tag(s):** `v1.6.0-m6-exec-a`
- **Key PR(s):** Execution hardening suite
- **Proof workflow(s):** `reconciliation-proof` (part of verify-deep CI) – [run 19394242139](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19394242139)
- **Tracking issue(s):** Blueprint v2.0 M6 entry

### M7 — Promotion Telemetry Phase 1 & 2
**Status:** Done – runtime promotion hashes, candidates/probation telemetry (`engine_promotion_*`) live.

**Summary:** Completed promotion SoT (Phase 1) and telemetry fan-out (Phase 2) powering daily-monitor tails and proof artifacts.

**Evidence**
- **Tag(s):** `v1.7.0-m7-promotion-phase1`, `v1.7.1-m7-promotion-telemetry`
- **Key PR(s):** Promotion telemetry PR series
- **Proof workflow(s):** `daily-monitor` verdict w/ promotion tail – [run 19394246991](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19394246991)
- **Tracking issue(s):** Blueprint v2.0 M7 entry

### M8 — Execution Hardening (Phase B: reconciliation, idempotency, slippage)
**Status:** In progress – demo-only implementation underway; reconciliation proof + session slippage preset added; no real-money change.

**Summary:** Adding broker reconciliation telemetry on startup+interval, restart-safe idempotency persistence (24h TTL, bounded caches), and a session-based slippage preset. Observability-first; no auto-heal yet.

**Evidence**
- **Tag(s):** Pending (demo-only while in progress)
- **Key PR(s):** (current) feat/m8b-execution-hardening
- **Proof workflow(s):** `m8-execution-hardening-proof` (branch runs)
- **Tracking issue(s):** Blueprint v2.0 M8 entry

### M9 — Production Feeds & Config SoT
**Status:** Done – file + HTTP feeds, news telemetry, config SoT, secret provenance.

**Summary:** Delivered file-backed news feed + NewsProbe (M9-A), HTTP adapter + telemetry (M9-B), and config/secret observability (M9-C).

**Evidence**
- **Tag(s):** (rolled into `main`; future tag pending)
- **Key PR(s):** #109 (M9-B), #111 (SoT tuning)
- **Proof workflow(s):** `m9-news-proof` [run 19358047610](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19358047610), `m9-config-sot-proof` [run 19393394943](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19393394943)
- **Tracking issue(s):** #104 “M9 – Production Feeds & Config SoT”

### M10 — GVRS Live Gate (Demo Only)
**Status:** Done – live gate enabled on demo configs only; entries blocked in volatile regimes, exits untouched.

**Summary:** Extended the GVRS gate to support live blocking, wired telemetry/daily-monitor tails, and proved behavior via deterministic probe runs.

**Evidence**
- **Tag(s):** `v1.10.0-m10-gvrs-live-gate`
- **Key PR(s):** #118
- **Proof workflow(s):** `m10-gvrs-live-proof` – [run 19394246032](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19394246032)
- **Tracking issue(s):** #113 “M10 – GVRS live gate”

### M11 — Risk Rails Phase A (Telemetry & Proof)
**Status:** Done – telemetry-only risk rails (broker loss, units, symbol caps, cooldown) live with proof harness. Phase B (live gating) in progress (demo-only).

**Summary:** Added `RiskRailTelemetrySnapshot`, host/metrics fields, and the `m11-risk-rails-proof` workflow (tools/RiskRailsProbe).

**Evidence**
- **Tag(s):** `v1.11.0-m11-risk-rails-telemetry`
- **Key PR(s):** #116
- **Proof workflow(s):** `m11-risk-rails-proof` – [run 19393394943](https://github.com/xforscom/TIYF.ENGINE/actions/runs/19393394943)
- **Tracking issue(s):** (New) Roadmap doc + future Phase‑B tracking TBD

### M11-B — Risk Rails Live Blocking
**Status:** In progress — live broker/global/symbol/cooldown gates on demo configs; exits always allowed; real-money configs remain telemetry-only until Ops approves cutover.

**Summary:** Broker guardrail (daily loss/global/symbol caps) and existing rails block new entries in live mode, emitting `ALERT_RISK_*_HARD` while retaining Phase‑A telemetry. Demo configs carry live caps; real-money remains telemetry/disabled.

**Evidence**
- **Tag(s):** Pending (post-merge)
- **Key PR(s):** #122 + follow-ups (broker guardrail)
- **Proof workflow(s):** `m11-risk-rails-proof` (branch and main runs)
- **Tracking issue(s):** #117

### M12 — Promotion Runtime
**Status:** In progress — shadow promotion runtime + proof on branch (no live routing yet).

**Summary:** Converts promotion telemetry into a shadow runtime that evaluates promotion/demotion readiness and surfaces metrics/health; live routing remains disabled.

**Evidence**
- **Tag(s):** N/A (shadow-only stage)
- **Key PR(s):** #123 (open)
- **Proof workflow(s):** `m12-promotion-runtime-proof` (branch runs)
- **Tracking issue(s):** Upcoming

### M13 — Config Source of Truth / Secret Provenance hardening
**Status:** Deferred — partially covered in M9-C; remaining scope (centralized SoT + provenance audit) scheduled post M12.

**Summary:** Consolidate config/secret SoT, integrate with Ops approvals, and expose provenance metrics for all environments.

**Evidence**
- **Tag(s):** N/A
- **Key PR(s):** N/A
- **Proof workflow(s):** N/A
- **Tracking issue(s):** Blueprint future milestone

### M14 — Ops Readiness & Demo Acceptance
**Status:** Not started.

**Summary:** End-to-end demo acceptance tests, runbooks, and Ops dashboards required before real-money cutover.

**Evidence**
- **Tag(s):** N/A
- **Key PR(s):** N/A
- **Proof workflow(s):** Planned Ops acceptance proof
- **Tracking issue(s):** Future Ops issue

### M15 — Real-Money Cutover
**Status:** Not started.

**Summary:** Flip real-money configs, enable all live gates, and demonstrate deterministic proofs + daily-monitor evidence for regulatory sign-off.

**Evidence**
- **Tag(s):** N/A
- **Key PR(s):** N/A
- **Proof workflow(s):** Final go/no-go suite (TBD)
- **Tracking issue(s):** To be created when Ops schedules cutover
