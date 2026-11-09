[CmdletBinding()]
param(
    [ValidateSet('All', 'Api', 'Dashboard')]
    [string]$Component = 'All',
    [switch]$SetupOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionRoot = Join-Path $repoRoot 'src/LeadsFinder'
$dashboardRoot = Join-Path $solutionRoot 'LeadsFinder.Dashboard'

# This script runs in a dedicated PowerShell process (see run.cmd). The job
# owns its descendants, including npm/cmd/node and dotnet's application host.
if (-not $SetupOnly) {
    Add-Type -Path (Join-Path $PSScriptRoot 'LauncherLifetime.cs')
    [LauncherLifetime]::Attach()
}

if ($Component -eq 'Api') {
    Set-Location $solutionRoot
    & dotnet run --project LeadsFinder.Api --launch-profile http
    if ($LASTEXITCODE -ne 0) { throw "API exited with code $LASTEXITCODE." }
    return
}
if ($Component -eq 'Dashboard') {
    Set-Location $dashboardRoot
    & npm run dev -- --strictPort
    if ($LASTEXITCODE -ne 0) { throw "Dashboard exited with code $LASTEXITCODE." }
    return
}

foreach ($name in @('dotnet', 'npm')) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "'$name' was not found. Install the prerequisites listed in README.md."
    }
}
if (-not $SetupOnly) {
    foreach ($port in @(5088, 5173)) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            throw "Port $port is already in use. Stop the existing app before running the launcher again."
        }
    }
}

Push-Location $solutionRoot
try {
    & dotnet restore LeadsFinder.Api/LeadsFinder.Api.csproj
    if ($LASTEXITCODE -ne 0) { throw 'API dependency restore failed.' }
}
finally { Pop-Location }

if (-not (Test-Path -LiteralPath (Join-Path $dashboardRoot 'node_modules/.bin/vite.cmd'))) {
    Push-Location $dashboardRoot
    try {
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw 'Dashboard dependency installation failed.' }
    }
    finally { Pop-Location }
}
if ($SetupOnly) {
    Write-Host 'API and dashboard dependencies are ready. No database service or API keys are required.'
    return
}

$shellPath = (Get-Process -Id $PID).Path
$children = @()
try {
    foreach ($name in @('Api', 'Dashboard')) {
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Component', $name)
        $children += Start-Process -FilePath $shellPath -ArgumentList $arguments -WorkingDirectory $solutionRoot -NoNewWindow -PassThru
    }
    Write-Host 'LeadsFinder is starting at http://127.0.0.1:5173/ (API: http://127.0.0.1:5088).'
    Write-Host 'Press Ctrl+C to stop the API and dashboard together. Closing this terminal also stops them.'
    while ($true) {
        foreach ($child in $children) {
            if ($child.HasExited) {
                throw "A LeadsFinder component exited (code $($child.ExitCode)). Stopping the remaining processes."
            }
        }
        Start-Sleep -Milliseconds 250
    }
}
finally {
    foreach ($child in $children) {
        if (-not $child.HasExited) {
            & taskkill.exe /PID $child.Id /T /F 2>$null | Out-Null
        }
    }
}
