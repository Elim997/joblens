<#
.SYNOPSIS
    Registers (or updates) the Windows scheduled task that runs published JobLens three times a day.

.DESCRIPTION
    Milestone F7. Creates one task with three daily triggers rather than three separate tasks:
    the schedule is one policy, and keeping it in one place means changing a time, a setting, or
    the deployment path is a single edit with no risk of the three drifting apart.

    The task's only action is the launcher in the deployment root, which runs the published
    "JobLens.Api.exe --run-once". It never starts or stops PostgreSQL, Docker, the WhatsApp
    bridge, OmniRoute, Rezi, or a browser - those are external prerequisites with their own
    lifecycles, and JobLens reports on them through preflight instead of managing them.

    Nothing sensitive is placed in the task definition: the action carries a path and the single
    argument "--run-once", and every secret continues to come from the same per-user
    user-secrets store and environment the interactive app already uses.

    IDENTITY (this is the part that matters most). The task is registered to run as the account
    invoking this script, with logon type Interactive - "run only when the user is logged on".
    That is a deliberate technical requirement, not a default:

      * The Rezi token cache ("%LOCALAPPDATA%\JobLens\rezi-token.dat") is DPAPI-protected for the
        current user. Only a real interactive logon session of that same account can decrypt it.
      * "Run whether user is logged on or not" would need either a stored Windows password
        (which this repo will not ask for or keep) or an S4U logon, and an S4U token has no
        access to the user's DPAPI master key - Rezi authentication would break silently.
      * SYSTEM or any other account would resolve a different %LOCALAPPDATA%, a different
        user-secrets store, and a different RunLock path, and could not read the token cache.

    So: log in as yourself and leave the session logged on. Runs that fall in a logged-off or
    sleeping window are picked up afterwards by the missed-run setting below.

    SETTINGS, and why each was chosen:

      MultipleInstances = IgnoreNew
        If a run is still going when the next trigger fires, don't start a second one. RunLock
        would refuse the second run anyway (exit 2), so this only avoids launching a process
        whose entire life is to fail and log a confusing conflict. RunLock, not this setting,
        remains the authoritative concurrency protection - it also covers the API's /ingest and
        /run endpoints, which Task Scheduler cannot see.

      StartWhenAvailable = true  ("run as soon as possible after a missed start")
        Safe and useful here because the pipeline is idempotent and backlog-based: ingest
        de-duplicates by message id and reports repeats as AlreadyStored, and scoring drains the
        stored backlog rather than only whatever arrived this minute. A catch-up run after the
        machine was asleep at 12:00 therefore does real work and creates no duplicates.

      ExecutionTimeLimit = 2 hours
        Generous - a normal run is minutes, but a large first backlog with per-batch LLM scoring
        is legitimately slow, and killing a healthy run would be worse than a long one. It is
        bounded so a wedged process cannot hold RunLock indefinitely and starve every later
        trigger. Termination is safe: RunLock is an open file handle, which Windows releases when
        the process dies, so no stale lock survives.

      Batteries: allowed to start, not stopped on switching to battery
        This is a desktop development machine. Even on a laptop, suppressing a scheduled ingest
        to save power would cost more than it saves.

      Network: no network-availability condition
        The run does need the network, but Task Scheduler's condition would silently skip the
        run. JobLens's own preflight instead reports an unreachable dependency explicitly, with
        an exit code and a log line - a diagnosable failure beats an invisible non-run.

      WakeToRun = false (default)
        Waking a sleeping machine three times a day is not worth it for a backlog-based pipeline;
        StartWhenAvailable already catches the run up once the machine is back.

.PARAMETER TaskName
    Scheduled task name. Default 'JobLens Scheduled Run'.

.PARAMETER DeployRoot
    Deployment root produced by publish-joblens.ps1. Defaults to "%LOCALAPPDATA%\JobLens" for the
    current user, resolved from the environment so no username is committed.

.PARAMETER At
    The daily run times. Default 12:00, 18:00 and 23:00, local machine time.

.EXAMPLE
    .\scripts\register-joblens-task.ps1 -WhatIf
    Prints exactly what would be registered and changes nothing.

.EXAMPLE
    .\scripts\register-joblens-task.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $TaskName = 'JobLens Scheduled Run',
    [string] $DeployRoot = (Join-Path $env:LOCALAPPDATA 'JobLens'),
    [string[]] $At = @('12:00', '18:00', '23:00')
)

$ErrorActionPreference = 'Stop'

$appDir = Join-Path $DeployRoot 'app'
$exePath = Join-Path $appDir 'JobLens.Api.exe'
$launcherPath = Join-Path $DeployRoot 'run-joblens-scheduled.ps1'
$logDir = Join-Path $DeployRoot 'logs'
$identity = "$env:USERDOMAIN\$env:USERNAME"

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "No published JobLens at '$exePath'. Run scripts\publish-joblens.ps1 before registering the task - registering a task that points at nothing would fail silently three times a day."
}
if (-not (Test-Path -LiteralPath $launcherPath)) {
    throw "No launcher at '$launcherPath'. Run scripts\publish-joblens.ps1, which copies it into the deployment root."
}

$powershellPath = Join-Path $PSHOME 'powershell.exe'
$argument = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$launcherPath`""

$triggers = foreach ($time in $At) {
    $parsed = [datetime]::ParseExact($time, 'HH\:mm', [cultureinfo]::InvariantCulture)
    New-ScheduledTaskTrigger -Daily -At (Get-Date -Hour $parsed.Hour -Minute $parsed.Minute -Second 0)
}

$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

$principal = New-ScheduledTaskPrincipal `
    -UserId $identity `
    -LogonType Interactive `
    -RunLevel Limited

$action = New-ScheduledTaskAction `
    -Execute $powershellPath `
    -Argument $argument `
    -WorkingDirectory $appDir

$description = 'Runs the published JobLens one-shot pipeline (preflight, ingest, backlog scoring) and exits. Registered by scripts/register-joblens-task.ps1. Starts and stops no external services.'

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

Write-Host 'Scheduled task to register' -ForegroundColor Cyan
Write-Host "  name          : $TaskName"
Write-Host "  action        : $powershellPath $argument"
Write-Host "  working dir   : $appDir"
Write-Host "  runs as       : $identity (logon type Interactive, not elevated)"
Write-Host "  triggers      : daily at $($At -join ', ') (local time)"
Write-Host '  instances     : IgnoreNew (RunLock remains the authoritative guard)'
Write-Host '  missed runs   : run as soon as possible afterwards'
Write-Host '  time limit    : 2 hours'
Write-Host '  power/network : may start on battery, not stopped on battery, no network condition, no wake'
Write-Host "  logs          : $logDir"
Write-Host "  existing task : $(if ($existing) { 'yes - it will be replaced' } else { 'no - it will be created' })"
Write-Host ''
Write-Host 'This task runs as YOU. That is required: the Rezi token cache is DPAPI-protected for' -ForegroundColor Yellow
Write-Host 'your account, and user-secrets, %LOCALAPPDATA% and the run lock all resolve per user.' -ForegroundColor Yellow
Write-Host ''

if ($PSCmdlet.ShouldProcess($TaskName, 'Register scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $triggers `
        -Settings $settings `
        -Principal $principal `
        -Description $description `
        -Force | Out-Null

    Write-Host "Registered '$TaskName'." -ForegroundColor Green
    Write-Host 'Verify with:'
    Write-Host "  Get-ScheduledTask -TaskName '$TaskName' | Get-ScheduledTaskInfo"
    Write-Host "  Start-ScheduledTask -TaskName '$TaskName'"
    Write-Host "Remove with scripts\unregister-joblens-task.ps1."
}
