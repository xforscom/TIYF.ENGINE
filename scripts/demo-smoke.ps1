param(
    [string]$RunId = "DEMO-A",
    [string]$Config = "tests/fixtures/backtest_m0/config.backtest-m0.json"
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$simDll = Join-Path $repoRoot 'src/TiYf.Engine.Sim/bin/Release/net8.0/TiYf.Engine.Sim.dll'
$toolsDll = Join-Path $repoRoot 'src/TiYf.Engine.Tools/bin/Release/net8.0/TiYf.Engine.Tools.dll'

if (-not (Test-Path $simDll) -or -not (Test-Path $toolsDll)) {
    Write-Host "[DEMO SMOKE] Building Release binaries" -ForegroundColor Cyan
    dotnet restore TiYf.Engine.sln | Out-Null
    dotnet build TiYf.Engine.sln -c Release --no-restore --nologo | Out-Null
}

$runDir = Join-Path $repoRoot (Join-Path 'journals/M0' "M0-RUN-$RunId")
if (Test-Path $runDir) {
    Remove-Item -Recurse -Force $runDir
}

$tmpRoot = Join-Path $repoRoot 'tmp'
if (-not (Test-Path $tmpRoot)) {
    New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
}

$stageDir = Join-Path $tmpRoot "demo-smoke-$RunId"
if (Test-Path $stageDir) {
    Remove-Item -Recurse -Force $stageDir
}
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

$zipPath = Join-Path $tmpRoot "demo-smoke-$RunId.zip"
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

$simLog = Join-Path $stageDir 'sim.log'
dotnet exec $simDll --config $Config --run-id $RunId | Tee-Object -FilePath $simLog
$simExit = $LASTEXITCODE
if ($simExit -ne 0) {
    throw "Simulator exited with $simExit"
}

$eventsLine = Select-String -Path $simLog -Pattern '^JOURNAL_DIR_EVENTS='
$tradesLine = Select-String -Path $simLog -Pattern '^JOURNAL_DIR_TRADES='
if (-not $eventsLine) { throw 'Missing JOURNAL_DIR_EVENTS in simulator output' }
if (-not $tradesLine) { throw 'Missing JOURNAL_DIR_TRADES in simulator output' }
$eventsPath = [System.IO.Path]::GetFullPath((($eventsLine | Select-Object -First 1).Line.Split('=')[1]))
$tradesPath = [System.IO.Path]::GetFullPath((($tradesLine | Select-Object -First 1).Line.Split('=')[1]))

$strictJson = Join-Path $stageDir 'strict.json'
dotnet exec $toolsDll verify strict --events $eventsPath --trades $tradesPath --schema 1.3.0 --json | Tee-Object -FilePath $strictJson
$strictExit = $LASTEXITCODE
if ($strictExit -ne 0) {
    throw "verify strict exited with $strictExit"
}

$parityJson = Join-Path $stageDir 'parity.json'
dotnet exec $toolsDll verify parity --events-a $eventsPath --events-b $eventsPath --trades-a $tradesPath --trades-b $tradesPath --json | Tee-Object -FilePath $parityJson
$parityExit = $LASTEXITCODE
if ($parityExit -ne 0) {
    throw "verify parity exited with $parityExit"
}

Copy-Item -Path $eventsPath -Destination (Join-Path $stageDir 'events.csv') -Force
Copy-Item -Path $tradesPath -Destination (Join-Path $stageDir 'trades.csv') -Force

$alertMatches = Select-String -Path $eventsPath -Pattern '^ALERT_BLOCK_'
$alertCount = if ($alertMatches) { $alertMatches.Count } else { 0 }
$tradeRows = (Get-Content $tradesPath | Select-Object -Skip 1).Count

$parityData = Get-Content $parityJson | ConvertFrom-Json
$envSanityPath = Join-Path $stageDir 'env.sanity'
@(
    "run_id=$RunId",
    "config=$Config",
    "sim_exit=$simExit",
    "strict_exit=$strictExit",
    "parity_exit=$parityExit",
    "events_path=$eventsPath",
    "trades_path=$tradesPath",
    "trades_row_count=$tradeRows",
    "alert_block_count=$alertCount",
    "events_sha=$($parityData.events.hashA)",
    "trades_sha=$($parityData.trades.hashA)"
) | Set-Content -Encoding UTF8 $envSanityPath

Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -Force

Write-Host "[DEMO SMOKE] Completed" -ForegroundColor Green
Write-Host "[DEMO SMOKE] SIM_EXIT=$simExit STRICT_EXIT=$strictExit PARITY_EXIT=$parityExit" -ForegroundColor Green
Write-Host "[DEMO SMOKE] trades_row_count=$tradeRows alert_block_count=$alertCount" -ForegroundColor Green
Write-Host "[DEMO SMOKE] Artifacts: $stageDir" -ForegroundColor Cyan
Write-Host "[DEMO SMOKE] Zip: $zipPath" -ForegroundColor Cyan
