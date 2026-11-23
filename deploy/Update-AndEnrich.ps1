[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{2,31}$')][string]$Username = 'matt',
    [switch]$SkipWebsiteUpdate,
    [switch]$EstimateMissingPressings
)

#Requires -RunAsAdministrator
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $package = Split-Path -Parent $PSScriptRoot
    $install = Join-Path $env:ProgramData 'MattsAndSamRecords'
    if (!$SkipWebsiteUpdate) {
        & (Join-Path $PSScriptRoot 'Install-Windows.ps1') -PublishedDirectory (Join-Path $package 'publish')
    }

    $release = (Get-Content -LiteralPath (Join-Path $install 'current-release.txt') -Raw).Trim()
    if ($release -notmatch '^[0-9]{14}-[a-f0-9]{8}$') { throw 'The active Record Room release ID is invalid.' }
    $active = Join-Path (Join-Path $install 'releases') $release
    $settings = Get-Content -LiteralPath (Join-Path $active 'run-settings.json') -Raw | ConvertFrom-Json
    $env:ConnectionStrings__DefaultConnection = $settings.ConnectionString
    $reportName = if ($EstimateMissingPressings) { 'Pressing estimates' } else { 'Pressing review' }
    $report = Join-Path $package ("$reportName - {0:yyyyMMdd-HHmmss}.txt" -f (Get-Date))

    Write-Host "Checking the personal collection for $Username. This can take several minutes."
    $app = Join-Path $active 'MattsRecords.exe'
    if ($EstimateMissingPressings) {
        & $app --estimate-pressings $Username $report (Split-Path -Parent $package)
    }
    else {
        & $app --enrich-pressings $Username $report
    }
    if ($LASTEXITCODE -ne 0) { throw 'The pressing update did not finish. Check the output above.' }
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    [void](Read-Host 'Press Enter to close')
}
