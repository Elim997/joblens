<#
.SYNOPSIS
    Launches the published JobLens one-shot run, captures its output to a per-run log, and
    returns its exit code unchanged.

.DESCRIPTION
    Milestone F7. This is the only thing the "JobLens Scheduled Run" task executes. Its entire
    job is deployment plumbing:

        resolve the published executable
        make sure the log directory exists
        launch JobLens.Api --run-once
        capture stdout and stderr
        record the final exit code
        return that same exit code

    It deliberately contains no pipeline, scoring, ingest, retry, or database logic, holds no
    secrets, and starts and stops nothing else. PostgreSQL, the WhatsApp bridge, OmniRoute, and
    Rezi are external dependencies with their own lifecycles; if one is down, JobLens's own
    preflight reports it and the run exits with a status that says so. Concurrency is likewise
    not this script's problem: RunLock inside JobLens is the authoritative cross-process
    protection, and it also covers the API's /ingest and /run endpoints, which Task Scheduler
    knows nothing about.

    JobLens writes its structured run report through the ordinary .NET console logger, so
    capturing the process's streams is all the persistence the deployment needs - no additional
    logging framework is introduced into the application for this.

    Each run produces "logs\joblens-<timestamp>.out.log". A "logs\joblens-<timestamp>.err.log"
    is produced too, but is removed again when it is empty, so the mere presence of an .err.log
    is itself the signal that something wrote to standard error. Nothing captured is discarded.

.PARAMETER DeployRoot
    Deployment root produced by publish-joblens.ps1. Defaults to "%LOCALAPPDATA%\JobLens" for
    the current user - resolved from the environment so no username is committed, and so this
    resolves to the correct per-user location under whichever identity the task runs as.

.PARAMETER RetainLogCount
    How many recent runs to keep logs for. Default 60, roughly twenty days at three runs a day.

.OUTPUTS
    Exit code. 0/1/2/3/4 are JobLens's own scheduled-run codes and are passed through untouched.
    64 means this launcher itself could not start JobLens (for example, nothing is published
    yet); it is deliberately outside JobLens's range so the two cannot be confused.
#>
[CmdletBinding()]
param(
    [string] $DeployRoot = (Join-Path $env:LOCALAPPDATA 'JobLens'),
    [int] $RetainLogCount = 60
)

$ErrorActionPreference = 'Stop'

$LauncherFailureExitCode = 64

$appDir = Join-Path $DeployRoot 'app'
$logDir = Join-Path $DeployRoot 'logs'
$exePath = Join-Path $appDir 'JobLens.Api.exe'

try {
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "No published JobLens at '$exePath'. Run scripts\publish-joblens.ps1 first."
    }

    New-Item -ItemType Directory -Path $logDir -Force | Out-Null

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outLog = Join-Path $logDir "joblens-$stamp.out.log"
    $errLog = Join-Path $logDir "joblens-$stamp.err.log"
    $suffix = 1
    while (Test-Path -LiteralPath $outLog) {
        $outLog = Join-Path $logDir "joblens-$stamp-$suffix.out.log"
        $errLog = Join-Path $logDir "joblens-$stamp-$suffix.err.log"
        $suffix++
    }

    # Start-Process rather than a PowerShell redirect: it quotes the executable path itself (the
    # deployment root sits under a profile path, which may contain spaces) and it never turns the
    # child's standard error into a terminating NativeCommandError under $ErrorActionPreference.
    # WorkingDirectory is set explicitly because Task Scheduler does not start a process in its
    # own directory, and the published appsettings.json lives beside the executable.
    $process = Start-Process -FilePath $exePath `
        -ArgumentList '--run-once' `
        -WorkingDirectory $appDir `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -NoNewWindow -Wait -PassThru
    $exitCode = $process.ExitCode

    Add-Content -LiteralPath $outLog -Value "[launcher] JobLens --run-once exited with code $exitCode at $(Get-Date -Format 'o')."

    if ((Test-Path -LiteralPath $errLog) -and (Get-Item -LiteralPath $errLog).Length -eq 0) {
        Remove-Item -LiteralPath $errLog -Force
    }

    # Keep the log directory bounded. Only this launcher's own files are considered, and pruning
    # is by run: a run's standard-error log is removed together with its standard-output log.
    $runs = @(Get-ChildItem -LiteralPath $logDir -Filter 'joblens-*.out.log' -File |
        Sort-Object -Property Name -Descending)
    if ($runs.Count -gt $RetainLogCount) {
        foreach ($old in $runs[$RetainLogCount..($runs.Count - 1)]) {
            $companion = $old.FullName -replace '\.out\.log$', '.err.log'
            Remove-Item -LiteralPath $old.FullName -Force
            if (Test-Path -LiteralPath $companion) {
                Remove-Item -LiteralPath $companion -Force
            }
        }
    }

    Write-Host "JobLens --run-once exited with code $exitCode. Log: $outLog"
    exit $exitCode
}
catch {
    # A launcher failure must still be visible somewhere durable, and must not masquerade as one
    # of JobLens's own outcomes.
    Write-Host "JobLens launcher failed: $($_.Exception.Message)"
    try {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        Add-Content -LiteralPath (Join-Path $logDir 'launcher-errors.log') `
            -Value "$(Get-Date -Format 'o') $($_.Exception.Message)"
    }
    catch {
        # Nothing further to do: if the log directory itself is unwritable, the exit code below
        # is the only channel left, and Task Scheduler records it.
    }
    exit $LauncherFailureExitCode
}
