[CmdletBinding()]
param([switch]$SkipBrowserInstall)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scraperRoot = Join-Path $repoRoot 'src/LeadsFinder/google-maps-scraper'

if (-not (Test-Path (Join-Path $scraperRoot 'go.mod'))) {
    & git -C $repoRoot submodule update --init src/LeadsFinder/google-maps-scraper
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the pinned scraper submodule.' }
}

Push-Location $scraperRoot
try {
    $buildArguments = @('build')
    if ($env:OS -eq 'Windows_NT') {
        # The pinned scrapemate dependency forces Linux-style single-process Chromium
        # flags that crash the Windows headless browser. Patch an isolated build copy;
        # leave both the pinned submodule and the shared Go module cache untouched.
        & go mod download github.com/gosom/scrapemate
        if ($LASTEXITCODE -ne 0) { throw 'Could not download the pinned scrapemate dependency.' }
        $moduleSource = (& go list -m -f '{{.Dir}}' github.com/gosom/scrapemate)
        if ($LASTEXITCODE -ne 0 -or -not $moduleSource) { throw 'Could not locate the scrapemate dependency. Run go mod download first.' }
        $buildRoot = Join-Path $repoRoot ('artifacts/google-scraper-build/' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
        $moduleCopy = Join-Path $buildRoot 'scrapemate'
        Copy-Item -LiteralPath $moduleSource.Trim() -Destination $moduleCopy -Recurse
        $browserSource = Join-Path $moduleCopy 'adapters/fetchers/jshttp/jshttp.go'
        (Get-Item -LiteralPath $browserSource).IsReadOnly = $false
        $browserText = [System.IO.File]::ReadAllText($browserSource)
        foreach ($flag in @('--single-process', '--no-zygote')) {
            $pattern = '(?m)^\s*`' + [regex]::Escape($flag) + '`,\r?\n'
            if (-not [regex]::IsMatch($browserText, $pattern)) { throw "Expected Chromium flag $flag was not found; review the pinned dependency before patching." }
            $browserText = [regex]::Replace($browserText, $pattern, '')
        }
        [System.IO.File]::WriteAllText($browserSource, $browserText)
        Copy-Item -LiteralPath (Join-Path $scraperRoot 'go.mod') -Destination (Join-Path $buildRoot 'go.mod')
        Copy-Item -LiteralPath (Join-Path $scraperRoot 'go.sum') -Destination (Join-Path $buildRoot 'go.sum')
        $modFile = Join-Path $buildRoot 'go.mod'
        & go mod edit "-modfile=$modFile" "-replace=github.com/gosom/scrapemate=$moduleCopy"
        if ($LASTEXITCODE -ne 0) { throw 'Could not configure the Windows build dependency.' }
        $buildArguments += "-modfile=$modFile"
    }
    $executable = if ($env:OS -eq 'Windows_NT') { 'bin/google-maps-scraper.exe' } else { 'bin/google-maps-scraper' }
    & go @buildArguments -o $executable .
    if ($LASTEXITCODE -ne 0) { throw 'Google Maps Scraper build failed.' }

    if (-not $SkipBrowserInstall) {
        $previousInstall = $env:PLAYWRIGHT_INSTALL_ONLY
        $previousBrowsers = $env:PLAYWRIGHT_BROWSERS_PATH
        $previousTelemetry = $env:DISABLE_TELEMETRY
        try {
            $env:PLAYWRIGHT_INSTALL_ONLY = '1'
            $env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $scraperRoot '.playwright'
            $env:DISABLE_TELEMETRY = '1'
            & (Join-Path $scraperRoot $executable)
            if ($LASTEXITCODE -ne 0) { throw 'Playwright Chromium installation failed.' }
        }
        finally {
            $env:PLAYWRIGHT_INSTALL_ONLY = $previousInstall
            $env:PLAYWRIGHT_BROWSERS_PATH = $previousBrowsers
            $env:DISABLE_TELEMETRY = $previousTelemetry
        }
    }

    $excludeFile = (& git rev-parse --git-path info/exclude)
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the local Git exclude file.' }
    if (-not (Select-String -LiteralPath $excludeFile -SimpleMatch '.playwright/' -Quiet -ErrorAction SilentlyContinue)) {
        Add-Content -LiteralPath $excludeFile -Value "`n.playwright/"
    }
    Write-Host 'Google Maps Scraper setup complete. The pinned source and shared Go cache are unchanged.'
}
finally { Pop-Location }
