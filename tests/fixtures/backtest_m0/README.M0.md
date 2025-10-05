# M0 Backtest Fixture

Deterministic, minimal multi-instrument fixture for TiYf Engine M0 acceptance.

## Instruments

Defined in `instruments.csv` with explicit pip, tick, contract, and lot metadata.

## Ticks

- Cadence: 1 minute
- Window: 2025-01-02T00:00:00Z to 2025-01-02T01:59:00Z inclusive (120 rows per instrument)
- Deterministic linear increments with fixed spread; culture-invariant decimals

## Strategy Placeholder

`DeterministicScriptStrategy` (not yet implemented in this commit) will emit two proposals per instrument at +15m and +75m.

## Config

`config.backtest-m0.json` binds instruments, tick paths, disables sentiment & learning, enables atomic writes and canonical CSV.

## data_version

Will be computed as SHA-256 over (instruments.csv + all tick CSV + config) canonicalized bytes; stored in `data_version.txt` and injected into journals.

## Acceptance (for fixture packet)

- Files present with exact schemas
- No gaps/dups; strictly increasing timestamps
- Decimals padded appropriately
- Deterministic content (no randomness)
