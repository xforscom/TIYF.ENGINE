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

Stable SHA-256 fingerprint over ONLY the immutable market data layer:

Included files (in fixed lexical order):

1. `instruments.csv`
2. `ticks_EURUSD.csv`
3. `ticks_USDJPY.csv`
4. `ticks_XAUUSD.csv`

Excluded: `config.backtest-m0.json` (so configuration tweaks do not churn journal metadata) and any derived artifacts.

Canonicalization before hashing:

- UTF-8 (no BOM)
- Convert all line endings to `\n`
- Trim trailing spaces and tabs on each line (content before the trim is already normalized to avoid semantic alteration)
- No column reordering; numeric text left verbatim

Current expected value:

`C531EDAA1B2B3EB9286B3EDA98B6443DD365C1A8DFEA2AFB4B77FC7DDD1D6122`

This value is injected into the first metadata line of `events.csv` and `trades.csv` as `data_version=...`.

### Recompute (verification)

Using the Tools project (or a simple one-liner if the helper exposes a command). Example PowerShell sketch:

```powershell
$files = 'instruments.csv','ticks_EURUSD.csv','ticks_USDJPY.csv','ticks_XAUUSD.csv'
$canon = foreach($f in $files){
  (Get-Content $f -Raw).Replace("`r`n","`n").Replace("`r","`n") -split "`n" | ForEach-Object { $_.TrimEnd(' ',"`t") }
} | ForEach-Object { $_ } | Join-String "`n"
[System.BitConverter]::ToString((New-Object System.Security.Cryptography.SHA256Managed).ComputeHash([Text.Encoding]::UTF8.GetBytes($canon))).Replace('-','')
```

### Why exclude config?

The config may evolve (e.g., enlarging risk caps or toggling optional probes) without changing the underlying data payload. Excluding it prevents metadata churn and keeps historical reproducibility intact as long as the raw data layer is unchanged.

## Acceptance (fixture + determinism)

- All fixture files present with exact schemas
- No timestamp gaps/dups; strictly increasing per instrument
- Decimals padded per instrument precision
- Deterministic content (no randomness, no GUIDs)
- `data_version` equals expected constant above
- Risk rails enabled yet zero `ALERT_BLOCK_` rows for M0
- Two successive runs yield bit-exact (canonicalized) `events.csv` and `trades.csv`

## CI Determinism Check

Workflow: `.github/workflows/m0-determinism.yml`

Enforces:

1. Build & unit/integration tests
2. Run M0 twice (A/B) from the same commit
3. Canonicalize + hash journals
4. Assert hashes A == B for both `events.csv` and `trades.csv`
5. Assert `data_version` matches expected constant
6. Assert zero `ALERT_BLOCK_` rows
7. Assert trades row count == 6 (header excluded)
8. Upload artifacts (both raw and canonical) on failure with a compact diff

Artifacts on failure: `artifacts/m0_runA/*`, `artifacts/m0_runB/*`.

## Local Reproduction

```powershell
dotnet run --project src/TiYf.Engine.Sim -- --config tests/fixtures/backtest_m0/config.backtest-m0.json
Copy-Item journals/M0/M0-RUN/events.csv eventsA.csv
Copy-Item journals/M0/M0-RUN/trades.csv tradesA.csv
dotnet run --project src/TiYf.Engine.Sim -- --config tests/fixtures/backtest_m0/config.backtest-m0.json
Copy-Item journals/M0/M0-RUN/events.csv eventsB.csv
Copy-Item journals/M0/M0-RUN/trades.csv tradesB.csv

function Get-CanonHash($path){
  $raw = (Get-Content $path -Raw).Replace("`r`n","`n").Replace("`r","`n") -split "`n" | ForEach-Object { $_.TrimEnd(' ',"`t") }
  $canon = ($raw -join "`n")
  $bytes = [Text.Encoding]::UTF8.GetBytes($canon)
  $sha = [System.Security.Cryptography.SHA256]::Create()
  ($sha.ComputeHash($bytes) | ForEach-Object ToString x2) -join ''
}

Get-CanonHash eventsA.csv
Get-CanonHash eventsB.csv
Get-CanonHash tradesA.csv
Get-CanonHash tradesB.csv
```

All four hashes should produce two identical pairs (events, trades). Any divergence indicates nondeterminism.
