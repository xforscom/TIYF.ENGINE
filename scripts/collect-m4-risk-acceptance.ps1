Param(
    [string]$BaseConfig = 'tests/fixtures/backtest_m0/config.backtest-m0.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Write-Host "[M4] Collecting risk acceptance artifacts..."

if (-not (Test-Path $BaseConfig)) { throw "Base config not found: $BaseConfig" }

Write-Host "[M4] Building Release binaries..."
dotnet build -c Release --no-restore | Out-Null

$sim = 'src/TiYf.Engine.Sim/bin/Release/net8.0/TiYf.Engine.Sim.dll'
$tools = 'src/TiYf.Engine.Tools/bin/Release/net8.0/TiYf.Engine.Tools.dll'
if (-not (Test-Path $sim)) { throw "Sim binary missing: $sim" }
if (-not (Test-Path $tools)) { throw "Tools binary missing: $tools" }

function New-RiskConfig {
    param(
        [string]$Mode,
        [string]$Path,
        [switch]$Breach
    )
    $obj = Get-Content $BaseConfig -Raw | ConvertFrom-Json
    if (-not $obj.featureFlags) { $obj | Add-Member -NotePropertyName featureFlags -NotePropertyValue ([pscustomobject]@{}) }
    if (-not ($obj.featureFlags | Get-Member -Name risk -ErrorAction SilentlyContinue)) { $obj.featureFlags | Add-Member -NotePropertyName risk -NotePropertyValue $Mode } else { $obj.featureFlags.risk = $Mode }
    if ($Mode -eq 'active') {
        if (-not ($obj.PSObject.Properties.Name -contains 'riskConfig')) { $obj | Add-Member -NotePropertyName riskConfig -NotePropertyValue ([pscustomobject]@{}) }
        if (-not ($obj.riskConfig | Get-Member -Name maxNetExposureBySymbol -ErrorAction SilentlyContinue)) { $obj.riskConfig | Add-Member -NotePropertyName maxNetExposureBySymbol -NotePropertyValue (@{}) }
    if ($Breach) { $obj.riskConfig.maxNetExposureBySymbol.EURUSD = 0 } else { $obj.riskConfig.maxNetExposureBySymbol.EURUSD = 10000000 }
    if (-not ($obj.riskConfig | Get-Member -Name maxRunDrawdownCCY -ErrorAction SilentlyContinue)) { $obj.riskConfig | Add-Member -NotePropertyName maxRunDrawdownCCY -NotePropertyValue 9999999 } else { $obj.riskConfig.maxRunDrawdownCCY = 9999999 }
    if (-not ($obj.riskConfig | Get-Member -Name emitEvaluations -ErrorAction SilentlyContinue)) { $obj.riskConfig | Add-Member -NotePropertyName emitEvaluations -NotePropertyValue $true } else { $obj.riskConfig.emitEvaluations = $true }
    if (-not ($obj.riskConfig | Get-Member -Name blockOnBreach -ErrorAction SilentlyContinue)) { $obj.riskConfig | Add-Member -NotePropertyName blockOnBreach -NotePropertyValue $true } else { $obj.riskConfig.blockOnBreach = $true }
    }
    $obj | ConvertTo-Json -Depth 40 | Set-Content $Path -Encoding UTF8
}

New-RiskConfig off    'acc.off.json'
New-RiskConfig shadow 'acc.shadow.json'
New-RiskConfig active 'acc.active_nb.json'
New-RiskConfig active 'acc.active_breach.json' -Breach

Write-Host "[M4] Running simulations..."
function Run-Sim { param($Cfg,$RunId) dotnet exec $sim --config $Cfg --quiet --run-id $RunId | Out-Null }
Run-Sim acc.off.json ACC-OFF
Run-Sim acc.shadow.json ACC-SHADOW
Run-Sim acc.active_nb.json ACC-ACTIVE-NB
Run-Sim acc.active_breach.json ACC-ACTIVE-BREACH

$parityRoot = Join-Path (Get-Location) 'artifacts/parity'
if (-not (Test-Path $parityRoot)) { throw "Parity artifacts missing: $parityRoot" }

Write-Host "[M4] Collecting parity hash summaries..."
$modes = 'ACC-OFF','ACC-SHADOW','ACC-ACTIVE-NB','ACC-ACTIVE-BREACH'
$hashSummary = @()
foreach ($m in $modes) {
    $h = Join-Path $parityRoot $m 'hashes.txt'
    if (-not (Test-Path $h)) { Write-Warning "Missing hashes for $m"; continue }
    $kv = @{ mode = $m }
    Get-Content $h | ForEach-Object { if ($_ -match '=') { $parts = $_.Split('='); $kv[$parts[0]] = $parts[1] } }
    $hashSummary += [pscustomobject]$kv
}
$hashSummary | Format-Table | Out-String | Write-Host

Write-Host "[M4] Extracting first evaluation + alert lines (breach run)..."
$breachEvents = 'journals/M0/M0-RUN-ACC-ACTIVE-BREACH/events.csv'
$evalLine = (Select-String -Path $breachEvents -Pattern 'INFO_RISK_EVAL_V1' | Select-Object -First 1).Line
$alertLine = (Select-String -Path $breachEvents -Pattern 'ALERT_BLOCK_' | Select-Object -First 1).Line
Set-Content acc.eval_alert.txt @($evalLine,$alertLine)

Write-Host "[M4] Promotion scenarios..."
Copy-Item acc.active_nb.json acc.active_nb.baseline.json -Force
Copy-Item acc.active_nb.json acc.active_nb.candidate.json -Force

# Accept: active vs active (identical)
$promAccept = dotnet exec $tools promote --baseline acc.active_nb.baseline.json --candidate acc.active_nb.candidate.json
Set-Content prom.accept.json $promAccept

# Downgrade reject: active -> shadow
$promDowngrade = dotnet exec $tools promote --baseline acc.active_nb.baseline.json --candidate acc.shadow.json
Set-Content prom.downgrade.json $promDowngrade

# Zero-cap reject: shadow -> active (breach)
$promZero = dotnet exec $tools promote --baseline acc.shadow.json --candidate acc.active_breach.json
Set-Content prom.zero.json $promZero

function Extract-RiskBlock { param($In,$Out) $json = Get-Content $In -Raw | ConvertFrom-Json; ($json.risk | ConvertTo-Json -Depth 20) | Set-Content $Out -Encoding UTF8 }
Extract-RiskBlock prom.accept.json risk.accept.json
Extract-RiskBlock prom.downgrade.json risk.downgrade.json
Extract-RiskBlock prom.zero.json risk.zero.json

git rev-parse HEAD > acc.commit.sha

Write-Host "[M4] Acceptance artifact generation COMPLETE." -ForegroundColor Green