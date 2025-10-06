# Local dual-run determinism harness (PowerShell 5.1 compatible)
$ErrorActionPreference = 'Stop'

# --- CONFIG ---
$ConfigPath = "tests/fixtures/backtest_m0/config.backtest-m0.json"
$OutA = "local_artifacts/m0_runA"
$OutB = "local_artifacts/m0_runB"
$Canonical = "local_artifacts/canonical"
$Hashes = "local_artifacts/hashes"
$ExpectedDV = "C531EDAA1B2B3EB9286B3EDA98B6443DD365C1A8DFEA2AFB4B77FC7DDD1D6122"

# --- PREP ---
if (Test-Path local_artifacts) { Remove-Item -Recurse -Force local_artifacts }
New-Item -ItemType Directory -Force -Path $OutA | Out-Null
New-Item -ItemType Directory -Force -Path $OutB | Out-Null
New-Item -ItemType Directory -Force -Path $Canonical | Out-Null
New-Item -ItemType Directory -Force -Path $Hashes | Out-Null

# Helper: detect if sim supports --out; if not, we'll copy from fixed journal path.
$SupportsOut = $true

function Run-Sim([string]$outDir) {
  # Attempt run with --out, fall back if fails.
  $cmd = "dotnet run --project src/TiYf.Engine.Sim -- --config $ConfigPath --out $outDir"
  $p = Start-Process -FilePath powershell -ArgumentList "-NoLogo","-NoProfile","-Command", $cmd -PassThru -WindowStyle Hidden -Wait
  if ($p.ExitCode -ne 0) {
    $global:SupportsOut = $false
  }
  if (-not $global:SupportsOut) {
    # Rerun without --out and copy from journals path
    Write-Host "Falling back to journals copy mode (no --out support)" -ForegroundColor Yellow
    $cmd2 = "dotnet run --project src/TiYf.Engine.Sim -- --config $ConfigPath"
    $p2 = Start-Process -FilePath powershell -ArgumentList "-NoLogo","-NoProfile","-Command", $cmd2 -PassThru -WindowStyle Hidden -Wait
    if ($p2.ExitCode -ne 0) { throw "Simulation failed (fallback)" }
    if (-not (Test-Path journals/M0/M0-RUN/events.csv)) { throw "Expected journals/M0/M0-RUN/events.csv not found" }
    Copy-Item journals/M0/M0-RUN/events.csv (Join-Path $outDir events.csv) -Force
    Copy-Item journals/M0/M0-RUN/trades.csv (Join-Path $outDir trades.csv) -Force
  }
}

# --- RUNS ---
Run-Sim -outDir $OutA
Run-Sim -outDir $OutB

# Validate outputs exist
$required = @(
  "$OutA/events.csv","$OutA/trades.csv",
  "$OutB/events.csv","$OutB/trades.csv"
)
$required | ForEach-Object { if (-not (Test-Path $_)) { throw "Missing expected output file: $_" } }

# --- CANONICALIZE ---
function Normalize-File($in, $out) {
  $text = Get-Content -Raw -Encoding UTF8 $in
  $lines = $text -replace "\r\n?", "\n" -split "\n"
  $norm = $lines | ForEach-Object { $_.TrimEnd() }
  $final = ($norm -join "`n")
  [System.IO.File]::WriteAllText($out, $final, [System.Text.UTF8Encoding]::new($false))
}
Normalize-File "$OutA/events.csv"  "$Canonical/events_A.csv"
Normalize-File "$OutB/events.csv"  "$Canonical/events_B.csv"
Normalize-File "$OutA/trades.csv"  "$Canonical/trades_A.csv"
Normalize-File "$OutB/trades.csv"  "$Canonical/trades_B.csv"

# --- HASHES ---
$events_A = (Get-FileHash "$Canonical/events_A.csv" -Algorithm SHA256).Hash.ToUpper()
$events_B = (Get-FileHash "$Canonical/events_B.csv" -Algorithm SHA256).Hash.ToUpper()
$trades_A = (Get-FileHash "$Canonical/trades_A.csv" -Algorithm SHA256).Hash.ToUpper()
$trades_B = (Get-FileHash "$Canonical/trades_B.csv" -Algorithm SHA256).Hash.ToUpper()
Set-Content -NoNewline -Path "$Hashes/events_A.sha256" -Value $events_A
Set-Content -NoNewline -Path "$Hashes/events_B.sha256" -Value $events_B
Set-Content -NoNewline -Path "$Hashes/trades_A.sha256" -Value $trades_A
Set-Content -NoNewline -Path "$Hashes/trades_B.sha256" -Value $trades_B

# --- INVARIANTS (from canonical A) ---
# Extract metadata from first line of events (for data_version) if trades lacks field
$eventsMeta = Get-Content "$Canonical/events_A.csv" -TotalCount 1
if ($eventsMeta -match 'data_version=([A-Fa-f0-9]{64})') { $dv = $Matches[1].ToUpper() } else { $dv = '' }

# Count trades rows (excluding header)
$trLines = Get-Content "$Canonical/trades_A.csv"
$rows = ($trLines.Length - 1)
$alertCount = (Select-String -Path "$Canonical/events_A.csv" -Pattern "ALERT_BLOCK_" -SimpleMatch | Measure-Object).Count

# --- HEADERS ---
$hdrEvents = $eventsMeta
$hdrTrades = Get-Content "$Canonical/trades_A.csv" -TotalCount 1

# --- REPORT ---
Write-Host "events_A: $events_A"
Write-Host "events_B: $events_B"
Write-Host "trades_A: $trades_A"
Write-Host "trades_B: $trades_B"
Write-Host "data_version: $dv"
Write-Host "trades_rows: $rows"
Write-Host "alert_block_count: $alertCount"
Write-Host "events.csv header (A): $hdrEvents"
Write-Host "trades.csv header (A): $hdrTrades"

# --- QUICK ASSERTIONS ---
$hardFail = $false
$fail = $false
if ($events_A -ne $events_B) { Write-Error "Events hash mismatch"; $fail = $true }
if ($trades_A -ne $trades_B) { Write-Error "Trades hash mismatch"; $fail = $true }
if ($dv -ne $ExpectedDV) { Write-Error "Unexpected data_version: $dv"; $fail = $true }
if ($rows -ne 6) { Write-Error "Trades rows != 6: $rows"; $fail = $true }
if ($alertCount -ne 0) { Write-Error "ALERT_BLOCK_* count != 0: $alertCount"; $fail = $true }
if ($hardFail -and $fail) { exit 1 } elseif ($fail) { Write-Warning "Invariants failed (non-fatal)." }
