<#
.SYNOPSIS
    Publishes JobLens.Api to a stable, user-owned deployment directory for scheduled operation.

.DESCRIPTION
    Milestone F7. Windows Task Scheduler must launch a published build from a fixed path, not
    "dotnet run" against the source tree - a scheduled run has to keep working when no shell,
    IDE, or Claude Code session is open, and when the repository isn't the working directory.

    Layout this script produces, all under one user-owned root (default
    "%LOCALAPPDATA%\JobLens", which already holds JobLens's other per-user runtime state):

        <DeployRoot>\app\                    published binaries (replaced on every publish)
        <DeployRoot>\run-joblens-scheduled.ps1   the launcher the scheduled task invokes
        <DeployRoot>\logs\                   per-run logs written by that launcher
        <DeployRoot>\run.lock                RunLock's file        (pre-existing, never touched)
        <DeployRoot>\rezi-token.dat          DPAPI token cache     (pre-existing, never touched)

    Publishing is FRAMEWORK-DEPENDENT on purpose. The deployment target is the same machine that
    builds this repository, so the .NET 10 runtime is guaranteed present; a self-contained publish
    would add roughly 70 MB of duplicated runtime per publish and a runtime-identifier pin to
    maintain, buying nothing here. JobLens.Api.csproj deliberately pins no RuntimeIdentifier.

    Safe to re-run: this is also the update procedure. Only "app\" is replaced. Logs, the run
    lock, the Rezi token cache, user-secrets, the WhatsApp database, and PostgreSQL are all
    outside "app\" and are never read, moved, or deleted here. No secret is read or written by
    this script - the published app resolves its own configuration at startup exactly as the
    interactive app does (appsettings.json + per-user user-secrets + environment variables).

.PARAMETER DeployRoot
    Deployment root. Defaults to "%LOCALAPPDATA%\JobLens" for the CURRENT user - deliberately
    resolved from the environment rather than written out, so no username is committed and so
    the published app lands beside the state only that same user can decrypt.

.PARAMETER Force
    Replace "app\" even if its contents don't look like a previous JobLens publish. Without this,
    a non-empty, unrecognized "app\" aborts the publish rather than deleting someone's files.

.EXAMPLE
    .\scripts\publish-joblens.ps1

.EXAMPLE
    .\scripts\publish-joblens.ps1 -DeployRoot 'D:\JobLensTest'
#>
[CmdletBinding()]
param(
    [string] $DeployRoot = (Join-Path $env:LOCALAPPDATA 'JobLens'),
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectPath = Join-Path $repoRoot 'src\JobLens.Api\JobLens.Api.csproj'
$launcherSource = Join-Path $scriptDir 'run-joblens-scheduled.ps1'

$appDir = Join-Path $DeployRoot 'app'
$logDir = Join-Path $DeployRoot 'logs'
$publishedExe = Join-Path $appDir 'JobLens.Api.exe'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Cannot find JobLens.Api.csproj at '$projectPath'. Run this script from a clone of the JobLens repository."
}
if (-not (Test-Path -LiteralPath $launcherSource)) {
    throw "Cannot find the scheduled launcher at '$launcherSource'. It ships beside this script and is required."
}

Write-Host 'JobLens publish' -ForegroundColor Cyan
Write-Host "  project     : $projectPath"
Write-Host "  deploy root : $DeployRoot"
Write-Host "  app dir     : $appDir"
Write-Host '  publish kind: Release, framework-dependent (no RuntimeIdentifier)'
Write-Host ''

# Replace app\ rather than publishing over it: "dotnet publish" never removes output left behind
# by a previous publish, so a package or file dropped from the project would linger indefinitely.
# Only app\ is in scope - the guard below is what keeps a mistyped -DeployRoot from deleting an
# unrelated directory.
if (Test-Path -LiteralPath $appDir) {
    $existing = @(Get-ChildItem -LiteralPath $appDir -Force)
    $looksLikeJobLens = Test-Path -LiteralPath (Join-Path $appDir 'JobLens.Api.dll')
    if ($existing.Count -gt 0 -and -not $looksLikeJobLens -and -not $Force) {
        throw "'$appDir' is not empty and does not look like a previous JobLens publish (no JobLens.Api.dll). Refusing to delete it. Re-run with -Force if this really is the deployment directory."
    }
    Write-Host "Clearing previous publish output in '$appDir'..." -ForegroundColor Yellow
    Remove-Item -LiteralPath $appDir -Recurse -Force
}

New-Item -ItemType Directory -Path $appDir -Force | Out-Null
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

Write-Host 'Running dotnet publish...' -ForegroundColor Cyan
# No PowerShell stream redirection on this native command: under $ErrorActionPreference = 'Stop'
# that can surface as a terminating NativeCommandError instead of just setting $LASTEXITCODE.
# The SDK's own output stays visible, which is what you want when a publish fails.
dotnet publish $projectPath --configuration Release --output $appDir --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. The deployment directory may now be incomplete - fix the build and re-run this script."
}

if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "dotnet publish reported success but '$publishedExe' does not exist. Refusing to report a working deployment."
}

# The scheduled task points at the launcher in the deployment root, never at a path inside this
# repository, so the task keeps working if the clone is moved or removed - and so updating the
# launcher is part of the ordinary publish/update step rather than a separate task re-registration.
Copy-Item -LiteralPath $launcherSource -Destination (Join-Path $DeployRoot 'run-joblens-scheduled.ps1') -Force

Write-Host ''
Write-Host 'Publish complete.' -ForegroundColor Green
Write-Host "  executable  : $publishedExe"
Write-Host "  launcher    : $(Join-Path $DeployRoot 'run-joblens-scheduled.ps1')"
Write-Host "  log dir     : $logDir"
Write-Host ''
Write-Host 'Next: verify the deployment under your own account before scheduling anything:' -ForegroundColor Cyan
Write-Host "  & '$publishedExe' --preflight"
Write-Host "  & '$publishedExe' --run-once"
Write-Host 'Then register the scheduled task with scripts\register-joblens-task.ps1.'
