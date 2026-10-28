[CmdletBinding()]
param(
    [ValidateSet('All', 'Api', 'Worker', 'Dashboard')]
    [string]$Component = 'All',
    [switch]$SetupOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionRoot = Join-Path $repoRoot 'src/LeadsFinder'
$dashboardRoot = Join-Path $solutionRoot 'LeadsFinder.Dashboard'
$env:LEADSFINDER_DB = 'Host=127.0.0.1;Port=55432;Database=leadsfinder;Username=leadsfinder;Password=devpassword'

if ($Component -ne 'All') {
    if ($Component -eq 'Dashboard') {
        Set-Location $dashboardRoot
        & npm run dev -- --strictPort
    }
    else {
        Set-Location $solutionRoot
        if ($Component -eq 'Api') {
            & dotnet run --project LeadsFinder.Api --launch-profile http
        }
        else {
            & dotnet run --project LeadsFinder.Worker
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "$Component exited with code $LASTEXITCODE." }
    return
}

foreach ($name in @('docker', 'dotnet', 'npm')) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "'$name' was not found. Install the prerequisites listed in README.md."
    }
}

if (-not $SetupOnly) {
    foreach ($port in @(5088, 5173)) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            Write-Host "Port $port is already in use. Stop the existing app before running this launcher again." -ForegroundColor Red
            exit 1
        }
    }
}

$envFile = Join-Path $repoRoot '.env'
if (-not (Test-Path -LiteralPath $envFile)) {
    Copy-Item -LiteralPath (Join-Path $repoRoot '.env.example') -Destination $envFile
    Write-Host 'Created .env from .env.example. Add optional API keys there when needed.'
}

& docker info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) {
    $desktopPaths = @(
        (Join-Path $env:LOCALAPPDATA 'Programs/DockerDesktop/Docker Desktop.exe'),
        (Join-Path $env:ProgramFiles 'Docker/Docker/Docker Desktop.exe')
    )
    $desktop = $desktopPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $desktop) { throw 'Docker is unavailable. Start Docker Desktop and retry.' }
    Write-Host 'Starting Docker Desktop...'
    Start-Process -FilePath $desktop -WindowStyle Hidden
    $dockerReady = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Seconds 2
        & docker info --format '{{.ServerVersion}}' *> $null
        if ($LASTEXITCODE -eq 0) { $dockerReady = $true; break }
    }
    if (-not $dockerReady) { throw 'Docker Desktop did not become ready within two minutes.' }
}

$composeFile = Join-Path $solutionRoot 'compose.yaml'
Write-Host 'Starting the local PostGIS database...'
& docker compose -f $composeFile up -d db
if ($LASTEXITCODE -ne 0) { throw 'Could not start the local database.' }

$databaseReady = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    & docker compose -f $composeFile exec -T db pg_isready -U leadsfinder -d leadsfinder *> $null
    if ($LASTEXITCODE -eq 0) { $databaseReady = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $databaseReady) { throw 'The database did not become ready within one minute.' }

Push-Location $solutionRoot
try {
    Write-Host 'Restoring the EF tool and applying database migrations...'
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the EF tool.' }
    & dotnet ef database update --project LeadsFinder.Data --startup-project LeadsFinder.Data
    if ($LASTEXITCODE -ne 0) { throw 'Database migration failed.' }
}
finally { Pop-Location }

if (-not (Test-Path -LiteralPath (Join-Path $dashboardRoot 'node_modules/.bin/vite.cmd'))) {
    Push-Location $dashboardRoot
    try {
        Write-Host 'Installing dashboard dependencies...'
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw 'Dashboard dependency installation failed.' }
    }
    finally { Pop-Location }
}

if ($SetupOnly) {
    Write-Host 'Database and dependencies are ready.'
    return
}

$shellPath = (Get-Process -Id $PID).Path
foreach ($name in @('Api', 'Worker', 'Dashboard')) {
    $arguments = @('-NoExit', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Component', $name)
    Start-Process -FilePath $shellPath -ArgumentList $arguments -WorkingDirectory $solutionRoot -WindowStyle Normal | Out-Null
}

Write-Host 'API, worker, and dashboard are starting in separate PowerShell windows.'
Write-Host 'Open http://127.0.0.1:5173/ when Vite reports ready. Press Ctrl+C in each window to stop.'
