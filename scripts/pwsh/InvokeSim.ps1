[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SimDll,

    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [string[]]$ExtraArgs = @(),

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host ("PS Edition: {0} Version: {1}" -f $PSVersionTable.PSEdition, $PSVersionTable.PSVersion)
Write-Host ("PATH: {0}" -f $env:PATH)

if (-not (Test-Path -Path $SimDll)) {
    throw "Simulator DLL not found at '$SimDll'"
}
if (-not (Test-Path -Path $ConfigPath)) {
    throw "Config file not found at '$ConfigPath'"
}

$runArgs = @('--config', $ConfigPath, '--run-id', $RunId)
if ($ExtraArgs) {
    $runArgs += $ExtraArgs
}
if ($DryRun.IsPresent) {
    $runArgs += '--dry-run'
}

Write-Host ("Simulator: {0}" -f $SimDll)
Write-Host ("Run arguments: {0}" -f ($runArgs -join ' '))

$logDirectory = Split-Path -Parent $LogPath
if ($logDirectory -and -not (Test-Path -Path $logDirectory)) {
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
}

dotnet exec $SimDll @runArgs 2>&1 | Tee-Object -FilePath $LogPath
$simExit = $LASTEXITCODE

if ($simExit -ne 0) {
    Write-Warning ("Simulator exited with code {0}. Showing tail of {1}" -f $simExit, $LogPath)
    if (Test-Path -Path $LogPath) {
        Get-Content -Path $LogPath | Select-Object -Last 200
    }
    throw "Simulator exited with code $simExit"
}
