[CmdletBinding()]
param()

#Requires -RunAsAdministrator
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $published = Join-Path (Split-Path -Parent $PSScriptRoot) 'publish'
    & (Join-Path $PSScriptRoot 'Install-Windows.ps1') -PublishedDirectory $published
    & (Join-Path $PSScriptRoot 'Configure-Email.ps1') -RecordRoomMailbox
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    [void](Read-Host 'Press Enter to close')
}
