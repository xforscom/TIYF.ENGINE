param(
    [string]$Config = "sample-config.json"
)

$ErrorActionPreference = 'Stop'

Write-Host "[SMOKE] Build + Test" -ForegroundColor Cyan

dotnet build --no-restore | Out-Null
$testResult = dotnet test --no-build --verbosity quiet
if($LASTEXITCODE -ne 0){ Write-Host "[SMOKE] Tests FAILED" -ForegroundColor Red; exit 1 }

# Run two simulations
$runA = "journals/SMOKE_A"
$runB = "journals/SMOKE_B"
if(Test-Path $runA){ Remove-Item -Recurse -Force $runA }
if(Test-Path $runB){ Remove-Item -Recurse -Force $runB }

Write-Host "[SMOKE] Sim Run A" -ForegroundColor Cyan
dotnet run --project src/TiYf.Engine.Sim -- --config $Config --out $runA
if($LASTEXITCODE -ne 0){ Write-Host "[SMOKE] Sim A FAILED" -ForegroundColor Red; exit 2 }

Write-Host "[SMOKE] Sim Run B" -ForegroundColor Cyan
dotnet run --project src/TiYf.Engine.Sim -- --config $Config --out $runB
if($LASTEXITCODE -ne 0){ Write-Host "[SMOKE] Sim B FAILED" -ForegroundColor Red; exit 3 }

# Verify
Write-Host "[SMOKE] Verify Run A" -ForegroundColor Cyan
dotnet run --project src/TiYf.Engine.Tools -- verify --file "$runA/events.csv"
if($LASTEXITCODE -ne 0){ Write-Host "[SMOKE] Verify FAILED" -ForegroundColor Red; exit 4 }

# Diff
Write-Host "[SMOKE] Diff A vs B" -ForegroundColor Cyan
dotnet run --project src/TiYf.Engine.Tools -- diff --left "$runA/events.csv" --right "$runB/events.csv"
if($LASTEXITCODE -ne 0){ Write-Host "[SMOKE] Diff FAILED" -ForegroundColor Red; exit 5 }

Write-Host "[SMOKE] PASS (tests+verify+diff deterministic)" -ForegroundColor Green
