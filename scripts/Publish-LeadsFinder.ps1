[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/publish'))

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dashboardRoot = Join-Path $repoRoot 'src/LeadsFinder/LeadsFinder.Dashboard'
$publishRoot = [System.IO.Path]::GetFullPath($OutputPath)

Push-Location $dashboardRoot
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard dependency installation failed.' }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed.' }
}
finally { Pop-Location }

& dotnet publish (Join-Path $repoRoot 'src/LeadsFinder/LeadsFinder.Api/LeadsFinder.Api.csproj') -c Release -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }
New-Item -ItemType Directory -Path (Join-Path $publishRoot 'wwwroot') -Force | Out-Null
Copy-Item -Path (Join-Path $dashboardRoot 'dist/*') -Destination (Join-Path $publishRoot 'wwwroot') -Recurse -Force
Write-Host "Published to $publishRoot. Run dotnet LeadsFinder.Api.dll from that directory."
