# Roadmap (Post-M0)

## M1
- Multi-instrument support (catalog ingestion from CSV)
- Configurable bar intervals (1s/5s/1m)
- Journal rotation + compression (gzip)
- Basic risk evaluators (notional limit, max position size stub)
- Deterministic random seed injection for strategy plug-ins

## M2
- Strategy plug-in interface (load from assemblies)
- Order simulation & fills model
- Latency simulation (fixed + distribution)
- Deterministic scenario scripting (JSON scenario files)
- Replay diff tool (compare two run journals)

## M3
- Pluggable messaging adapter (Kafka or Redis) behind port
- PostgreSQL persistence module (journals + bar store)
- Metrics (prometheus textfile exporter sidecar)
- Structured logging (JSON) + correlation ids

## M4
- Risk engine expansion (per-instrument throttles, kill-switch)
- Live ingestion adapter abstraction (websocket / FIX placeholder)
- Time travel debugger (step through journal sequentially)

## M5
- Containerization (Dockerfile multi-stage)
- K8s manifests (non-prod dev cluster)
- Basic observability: OpenTelemetry traces + logs + metrics

## M6
- Web UI prototype (read-only) for run inspection
- Strategy sandbox API
- Pluggable pricing models (mid, last, bid/ask synthetic)

## Principles
- Preserve determinism first; concurrency introduced with guardrails.
- Avoid premature optimization; profile prior to refactors.
- Maintain backward-compatible journal schema or version bump.
