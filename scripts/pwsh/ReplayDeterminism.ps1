param(
    [string]$WorkingDirectory = "scratch/replay-proof",
    [int]$RunSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path .
$workDir = Join-Path $root $WorkingDirectory
if (Test-Path $workDir) {
    Remove-Item -Recurse -Force $workDir
}
New-Item -ItemType Directory -Path $workDir | Out-Null

# Seed instrument and tick fixtures
$ticks = @'
utc_ts,price,volume
2025-01-01T00:00:00Z,1.10000,1
2025-01-01T00:01:00Z,1.10050,1
2025-01-01T00:02:00Z,1.09980,1
2025-01-01T00:03:00Z,1.10120,1
'@.Trim()
Set-Content -Path (Join-Path $workDir "ticks.csv") -Value $ticks -Encoding UTF8

Copy-Item (Join-Path $root "tests/fixtures/backtest_m0/instruments.csv") (Join-Path $workDir "instruments.csv")

$journalRelative = "out"
$journalRoot = Join-Path $workDir $journalRelative
New-Item -ItemType Directory -Path $journalRoot | Out-Null

# Build replay config with JournalRoot pointing to working directory
$configJson = @"
{
  "schemaVersion": "1.3.0",
  "run": {
    "runId": "REPLAY-PROOF"
  },
  "adapter": {
    "type": "oanda-demo",
    "settings": {
      "baseUrl": "https://api-fxpractice.oanda.com/v3/",
      "accessToken": "env:OANDA_REPLAY_TOKEN",
      "accountId": "env:OANDA_REPLAY_ACCOUNT",
      "maxOrderUnits": 100000,
      "requestTimeoutSeconds": 10,
      "retryInitialDelaySeconds": 0.2,
      "retryMaxDelaySeconds": 2,
      "retryMaxAttempts": 5,
      "handshakeEndpoint": "/accounts/{accountId}/summary",
      "orderEndpoint": "/accounts/{accountId}/orders",
      "useMock": true,
      "stream": {
        "enable": true,
        "baseUrl": "https://stream-fxpractice.oanda.com/v3/",
        "pricingEndpoint": "/accounts/{accountId}/pricing/stream",
        "heartbeatTimeoutSeconds": 15,
        "maxBackoffSeconds": 10,
        "feedMode": "replay",
        "replayTicksFile": "ticks.csv",
        "instruments": ["EUR_USD"]
      }
    }
  },
  "InstrumentFile": "instruments.csv",
  "InputTicksFile": "ticks.csv",
  "BrokerId": "oanda-practice",
  "AccountId": "env:OANDA_REPLAY_ACCOUNT",
  "universe": ["EURUSD"],
  "featureFlags": {
    "risk": "active"
  },
  "JournalRoot": "$journalRelative"
}
"@

[System.IO.File]::WriteAllText((Join-Path $workDir "config.json"), $configJson, (New-Object System.Text.UTF8Encoding($false)))

function Get-CrossPlatformHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $content = Get-Content -Path $Path -Raw
    $normalized = $content -replace "`r", ""
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalized)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($bytes)
        return ([BitConverter]::ToString($hashBytes)).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Invoke-ReplayRun {
    param(
        [string]$Label
    )

    $labelDir = Join-Path $workDir $Label
    if (Test-Path $labelDir) {
        Remove-Item -Recurse -Force $labelDir
    }
    New-Item -ItemType Directory -Path $labelDir | Out-Null

    if (Test-Path $journalRoot) {
        Remove-Item -Recurse -Force $journalRoot
    }
    New-Item -ItemType Directory -Path $journalRoot | Out-Null

    $env:OANDA_REPLAY_TOKEN = "dummy-token"
    $env:OANDA_REPLAY_ACCOUNT = "DUMMY-ACCOUNT"
    $env:ENGINE_HOST_PORT = "5005"

    $proc = Start-Process dotnet -ArgumentList @(
        "run",
        "--project", "src/TiYf.Engine.Host/TiYf.Engine.Host.csproj",
        "--",
        "--config", (Join-Path $WorkingDirectory "config.json")
    ) -WorkingDirectory $root -PassThru -NoNewWindow

    $deadline = (Get-Date).AddSeconds($RunSeconds)
    $eventsPath = $null
    while ((Get-Date) -lt $deadline) {
        $runFolder = Get-ChildItem -Path (Join-Path $journalRoot "oanda-demo") -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($runFolder) {
            $candidate = Join-Path $runFolder.FullName "events.csv"
            if (Test-Path $candidate) {
                $eventsPath = $candidate
                break
            }
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not $eventsPath) {
        throw "Replay run '$Label' did not produce events.csv within ${RunSeconds}s."
    }

    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
        Start-Sleep -Seconds 2
    }

    try { $proc.WaitForExit() } catch { }

    $exitCode = $proc.ExitCode
    # Exit code 137 (SIGKILL) is expected when the process is force-killed after we've
    # already captured the replay artifacts (events.csv, etc.). Replay runs intentionally
    # terminate the host once the journals are written, so 137 is not a failure here.
    if ($null -eq $exitCode) {
        # Some environments do not propagate the exit code immediately after Stop-Process; treat as forced kill.
        $exitCode = 137
    }
    # Exit code 137 indicates SIGKILL from the forced Stop-Process above after artifacts are collected.
    # Treat it as success because the host shuts down via kill once replay evidence is captured.
    if ($exitCode -ne 0 -and $exitCode -ne 137) {
        throw "Replay run '$Label' failed (exit code $exitCode)."
    }

    Copy-Item $eventsPath (Join-Path $labelDir "events.csv")

    $hash = Get-CrossPlatformHash -Path (Join-Path $labelDir "events.csv")
    return [pscustomobject]@{
        Label = $Label
        EventsPath = (Join-Path $labelDir "events.csv")
        Hash = $hash
    }
}

$results = @(
    Invoke-ReplayRun -Label "run1"
    Invoke-ReplayRun -Label "run2"
)

if ($results[0].Hash -ne $results[1].Hash) {
    throw "Replay determinism check failed. Hashes differ: $($results[0].Hash) vs $($results[1].Hash)"
}

$report = @{
    run1 = $results[0]
    run2 = $results[1]
    matchingHash = $results[0].Hash
}

$reportPath = Join-Path $workDir "replay-proof.json"
$reportJson = $report | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($reportPath, $reportJson, (New-Object System.Text.UTF8Encoding($false)))

Compress-Archive -Path (Join-Path $workDir "run1/*") -DestinationPath (Join-Path $workDir "run1.zip") -Force
Compress-Archive -Path (Join-Path $workDir "run2/*") -DestinationPath (Join-Path $workDir "run2.zip") -Force

Write-Host "Replay determinism hash: $($results[0].Hash)"
Write-Host "Artifacts:"
Write-Host "  Run1 events: $($results[0].EventsPath)"
Write-Host "  Run2 events: $($results[1].EventsPath)"
Write-Host "  Report: $reportPath"
