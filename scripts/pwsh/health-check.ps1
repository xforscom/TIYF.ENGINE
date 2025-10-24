$ErrorActionPreference = 'Stop'

param(
    [string]$Uri = 'http://127.0.0.1:8080/health',
    [int]$TimeoutSec = 5
)

$healthLine = 'health connected=unknown last_heartbeat_utc=unknown'

try {
    $health = Invoke-RestMethod -Uri $Uri -TimeoutSec $TimeoutSec
    if ($null -ne $health) {
        $isConnected = [bool]$health.connected
        $heartbeat = $health.last_heartbeat_utc
        $healthLine = 'health connected={0} last_heartbeat_utc={1}' -f $isConnected, $heartbeat
    }
} catch {
    Write-Warning "Failed to capture /health for digest: $($_.Exception.Message)"
}

Write-Output $healthLine
