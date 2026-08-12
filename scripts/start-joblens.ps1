<#
.SYNOPSIS
    One-command JobLens dev startup: Postgres, WhatsApp bridge, then the API.

.DESCRIPTION
    1. Checks the 'joblens-pg' Docker container: starts it if stopped, leaves it if
       already running, never creates/recreates it.
    2. Checks whether the WhatsApp bridge is already up (by this script's own PID
       record, then by port); if not, starts `go run main.go` in the bridge dir in
       its own console window so a first-run QR prompt is visible. Never touches
       the bridge's session files or messages.db.
    3. Runs `dotnet run` for JobLens.Api in this window (Ctrl+C stops it).
    4. On exit, stops only the bridge window this script itself started - Postgres
       and the WhatsApp session are always left alone.

    Requires scripts/dev-config.ps1 (copy dev-config.ps1.example and fill in your
    paths - see SETUP.md "One-command dev startup").
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# --- Load machine-specific config ---------------------------------------------
$configPath = Join-Path $scriptDir 'dev-config.ps1'
if (-not (Test-Path $configPath)) {
    Write-Error "Missing $configPath. Copy dev-config.ps1.example to dev-config.ps1 and fill in your paths."
    exit 1
}
. $configPath

foreach ($name in 'BridgeDir', 'ApiProjectPath', 'PostgresContainerName') {
    if (-not (Get-Variable -Name $name -ErrorAction SilentlyContinue).Value) {
        Write-Error "dev-config.ps1 is missing `$$name - see dev-config.ps1.example."
        exit 1
    }
}
if (-not $BridgePort) { $BridgePort = 8080 }

$runDir = Join-Path $scriptDir '.run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$bridgePidFile = Join-Path $runDir 'bridge.pid'

$bridgeStartedByThisRun = $null

function Test-PortOpen {
    param([int]$Port)
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $connectTask = $client.ConnectAsync('127.0.0.1', $Port)
        $completed = $connectTask.Wait(300)
        $result = $completed -and $client.Connected
        $client.Close()
        return $result
    } catch {
        return $false
    }
}

function Get-BridgePidFromFile {
    if (-not (Test-Path $bridgePidFile)) { return $null }
    $bridgeProcessId = Get-Content $bridgePidFile -ErrorAction SilentlyContinue
    if (-not $bridgeProcessId) { return $null }
    $proc = Get-Process -Id $bridgeProcessId -ErrorAction SilentlyContinue
    if ($proc) { return $proc }
    # Stale file from a previous run that didn't shut down cleanly.
    Remove-Item $bridgePidFile -ErrorAction SilentlyContinue
    return $null
}

# --- 1. Postgres ----------------------------------------------------------------
# Native commands' stderr must not be redirected with PowerShell's own >/2>/*>
# operators here: under $ErrorActionPreference = 'Stop' that can turn into a
# terminating NativeCommandError instead of just setting $LASTEXITCODE, aborting
# the script (confirmed while testing this script). cmd.exe's own >nul 2>&1
# suppresses it at the OS level instead, so PowerShell never sees it as an error.
Write-Host "==> Postgres ('$PostgresContainerName')" -ForegroundColor Cyan
cmd /c "docker info >nul 2>&1"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker doesn't seem to be running. Start Docker Desktop and re-run this script."
    exit 1
}

$pgState = docker inspect -f '{{.State.Status}}' $PostgresContainerName
if ($LASTEXITCODE -ne 0) {
    Write-Error "Container '$PostgresContainerName' doesn't exist. Create it once per SETUP.md step 3, then re-run this script. (This script never creates it.)"
    exit 1
}

if ($pgState -eq 'running') {
    Write-Host "    already running - left alone." -ForegroundColor Green
} else {
    Write-Host "    state is '$pgState' - starting it (not recreating)..." -ForegroundColor Yellow
    docker start $PostgresContainerName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "    started." -ForegroundColor Green
}

# --- 2. WhatsApp bridge -----------------------------------------------------------
Write-Host "==> WhatsApp bridge" -ForegroundColor Cyan
$existing = Get-BridgePidFromFile
if ($existing) {
    Write-Host "    already running (PID $($existing.Id), started by an earlier run of this script) - left alone." -ForegroundColor Green
} elseif (Test-PortOpen -Port $BridgePort) {
    Write-Host "    already running (something is listening on port $BridgePort) - left alone. This script did not start it, so it won't stop it on exit." -ForegroundColor Green
} else {
    Write-Host "    not running - starting 'go run main.go' in $BridgeDir, in its own window..." -ForegroundColor Yellow
    Write-Host "    First run only: a QR code will appear in that window - scan it with the phone in the job group. Existing sessions log in silently." -ForegroundColor Yellow

    $title = "WhatsApp Bridge (JobLens dev)"
    $proc = Start-Process -FilePath 'cmd.exe' `
        -ArgumentList '/c', "title $title && go run main.go" `
        -WorkingDirectory $BridgeDir `
        -PassThru
    Set-Content -Path $bridgePidFile -Value $proc.Id
    $bridgeStartedByThisRun = $proc

    Write-Host "    waiting for it to come up on port $BridgePort (up to 2 minutes; longer on first run if the QR isn't scanned yet)..." -ForegroundColor Yellow
    $waitedSeconds = 0
    while (-not (Test-PortOpen -Port $BridgePort) -and $waitedSeconds -lt 120) {
        Start-Sleep -Seconds 2
        $waitedSeconds += 2
    }
    if (Test-PortOpen -Port $BridgePort) {
        Write-Host "    up." -ForegroundColor Green
    } else {
        Write-Host "    still not up after ${waitedSeconds}s - check its window (it may be waiting on a QR scan). Continuing anyway." -ForegroundColor Yellow
    }
}

# --- 3. JobLens API (foreground; Ctrl+C stops it) --------------------------------
Write-Host "==> Starting JobLens API - Ctrl+C to stop everything this script started" -ForegroundColor Cyan
try {
    dotnet run --project $ApiProjectPath
} finally {
    # --- 4. Cleanup: only what this run started ------------------------------------
    if ($bridgeStartedByThisRun) {
        Write-Host "`n==> Stopping the WhatsApp bridge window this script started (PID $($bridgeStartedByThisRun.Id))..." -ForegroundColor Cyan
        # taskkill /T kills the whole process tree: cmd.exe -> go.exe -> the compiled
        # bridge binary go run spawns as a separate child. Stop-Process alone would
        # only kill cmd.exe/go.exe and leave the actual bridge process (and port 8080)
        # running as an orphan. Routed through cmd.exe's own >nul 2>&1 (not PowerShell's
        # redirection - see the note above) so a harmless "already exited" race doesn't
        # abort this finally block before Postgres/session cleanup messaging below.
        cmd /c "taskkill /PID $($bridgeStartedByThisRun.Id) /T /F >nul 2>&1"
        Remove-Item $bridgePidFile -ErrorAction SilentlyContinue
    }
    Write-Host "==> Postgres and the WhatsApp session were left untouched." -ForegroundColor Cyan
}
