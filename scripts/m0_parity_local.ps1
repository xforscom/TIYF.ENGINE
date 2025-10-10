Param(
    [string]$Configuration = "Release",
    [string]$RunIdA = "LOCAL-A",
    [string]$RunIdB = "LOCAL-B",
    [string]$ConfigPath = "tests/fixtures/backtest_m0/config.backtest-m0.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-PathRelative([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return (Resolve-Path -LiteralPath $Path).ProviderPath
    }
    $root = Split-Path -Parent $PSCommandPath
    $repoRoot = Resolve-Path -LiteralPath (Join-Path $root "..")
    return (Resolve-Path -LiteralPath (Join-Path $repoRoot $Path)).ProviderPath
}

$repoRoot = Resolve-PathRelative "."
Set-Location $repoRoot

Write-Host "[Step] Building solution ($Configuration)" -ForegroundColor Cyan
& dotnet build "TiYf.Engine.sln" -c $Configuration --nologo | Write-Host

$simPath = Resolve-PathRelative "src/TiYf.Engine.Sim/bin/$Configuration/net8.0/TiYf.Engine.Sim.dll"
$toolsPath = Resolve-PathRelative "src/TiYf.Engine.Tools/bin/$Configuration/net8.0/TiYf.Engine.Tools.dll"
$configPath = Resolve-PathRelative $ConfigPath

if (-not (Test-Path $simPath)) { throw "Sim DLL not found at $simPath" }
if (-not (Test-Path $toolsPath)) { throw "Tools DLL not found at $toolsPath" }
if (-not (Test-Path $configPath)) { throw "Config not found at $configPath" }

$journalRoot = Join-Path $repoRoot "journals"
if (Test-Path $journalRoot) {
    Remove-Item $journalRoot -Recurse -Force
}

function Invoke-Run([string]$RunId, [string]$LogPath) {
    Write-Host "[Step] Running sim (RunId=$RunId)" -ForegroundColor Cyan
    & dotnet exec $simPath --config $configPath --run-id $RunId --quiet *>&1 |
        Tee-Object -FilePath $LogPath | Out-Null

    $log = Get-Content $LogPath
    $eventsDir = ($log | Where-Object { $_ -like 'JOURNAL_DIR_EVENTS=*' } | Select-Object -Last 1).Split('=')[-1]
    $tradesDir = ($log | Where-Object { $_ -like 'JOURNAL_DIR_TRADES=*' } | Select-Object -Last 1).Split('=')[-1]
    if (-not $eventsDir -or -not (Test-Path $eventsDir)) {
        throw "Failed to resolve events directory for RunId=$RunId"
    }
    if (-not $tradesDir -or -not (Test-Path $tradesDir)) {
        throw "Failed to resolve trades directory for RunId=$RunId"
    }
    return [PSCustomObject]@{
        Events = Join-Path $eventsDir "events.csv"
        Trades = Join-Path $tradesDir "trades.csv"
    }
}

$logA = Join-Path $repoRoot ("run-$RunIdA.log")
$logB = Join-Path $repoRoot ("run-$RunIdB.log")
$resultA = Invoke-Run -RunId $RunIdA -LogPath $logA
$resultB = Invoke-Run -RunId $RunIdB -LogPath $logB

Write-Host "[Step] Verifying parity" -ForegroundColor Cyan
$parityJson = Join-Path $repoRoot "parity.local.json"
$verifyExit = & dotnet exec $toolsPath verify parity `
    --events-a $($resultA.Events) `
    --events-b $($resultB.Events) `
    --trades-a $($resultA.Trades) `
    --trades-b $($resultB.Trades) `
    --json 2>&1 | Tee-Object -FilePath $parityJson

if ($LASTEXITCODE -ne 0) {
    Write-Host "Parity FAILED. Inspect $parityJson for details." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Parity verified successfully." -ForegroundColor Green
Write-Host "Summary saved to $parityJson"
```}