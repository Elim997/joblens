<#
.SYNOPSIS
    Removes the JobLens scheduled task, stopping all future automatic runs.

.DESCRIPTION
    Milestone F7. Unregisters exactly one task, by name, and touches nothing else. This is a
    scheduler uninstall, not an application uninstall: it deliberately does NOT remove

        the published application in "<DeployRoot>\app"
        the run logs in "<DeployRoot>\logs"
        the run lock or the DPAPI-protected Rezi token cache
        user-secrets
        the PostgreSQL database or its Docker container
        the WhatsApp bridge session or messages.db

    Removing any of those would destroy state that is expensive or impossible to recreate, and
    none of it is owned by the scheduler. Delete the published binaries and logs by hand if you
    want the disk space back; both live under the deployment root and neither is needed to run
    JobLens interactively from the repository again.

    After this runs, JobLens can still be started manually and the API still works exactly as
    before - the only thing removed is the automatic schedule.

.PARAMETER TaskName
    Scheduled task name to remove. Default 'JobLens Scheduled Run'. Only this task is removed;
    if it does not exist the script says so and exits without error.

.EXAMPLE
    .\scripts\unregister-joblens-task.ps1 -WhatIf

.EXAMPLE
    .\scripts\unregister-joblens-task.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $TaskName = 'JobLens Scheduled Run'
)

$ErrorActionPreference = 'Stop'

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "No scheduled task named '$TaskName' is registered. Nothing to do." -ForegroundColor Yellow
    return
}

Write-Host "Scheduled task to remove: $TaskName" -ForegroundColor Cyan
Write-Host "  state   : $($existing.State)"
Write-Host "  runs as : $($existing.Principal.UserId)"
Write-Host '  Published binaries, logs, the Rezi token cache, user-secrets, PostgreSQL and the'
Write-Host '  WhatsApp database are all left untouched.'
Write-Host ''

if ($PSCmdlet.ShouldProcess($TaskName, 'Unregister scheduled task')) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed '$TaskName'. JobLens will no longer run automatically." -ForegroundColor Green
}
