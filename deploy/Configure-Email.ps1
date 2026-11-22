[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:ProgramData 'MattsAndSamRecords'),
    [switch]$RecordRoomMailbox
)

#Requires -RunAsAdministrator
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Restart-RecordRoom([string]$ListenUrl) {
    $taskName = 'MattsAndSamRecords'
    Stop-ScheduledTask -TaskName $taskName -TaskPath '\'
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    while ((Get-ScheduledTask -TaskName $taskName -TaskPath '\').State -eq 'Running') {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'The website task did not stop.' }
        Start-Sleep -Milliseconds 300
    }
    Start-ScheduledTask -TaskName $taskName -TaskPath '\'
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            if ((Invoke-WebRequest -UseBasicParsing -Uri "$ListenUrl/health/ready" -TimeoutSec 3).StatusCode -eq 200) { return }
        }
        catch { Start-Sleep -Seconds 1 }
    }
    throw 'The website did not become ready after restart.'
}

$port = 587
if ($RecordRoomMailbox) {
    $smtpHost = 'mail.privateemail.com'
    $from = (Read-Host 'Namecheap Private Email mailbox address').Trim()
    $username = $from
}
else {
    $smtpHost = (Read-Host 'SMTP hostname').Trim()
    if ($smtpHost -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$') { throw 'Enter a valid SMTP hostname.' }
    $portText = (Read-Host 'SMTP port [587]').Trim()
    if ($portText -and (![int]::TryParse($portText, [ref]$port) -or $port -lt 1 -or $port -gt 65535)) {
        throw 'Enter a port from 1 to 65535.'
    }
    $from = (Read-Host 'From email address').Trim()
    $username = (Read-Host 'SMTP username (leave blank for an IP-authorized relay)').Trim()
}
if (!$from -or $from.Length -gt 254 -or [Net.Mail.MailAddress]::new($from).Address -cne $from) {
    throw 'Enter a single From email address.'
}
$ciphertext = ''
if ($username) {
    Add-Type -AssemblyName System.Security
    $secret = Read-Host 'SMTP password or app password' -AsSecureString
    if ($secret.Length -eq 0) { throw 'Enter the SMTP password.' }
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
    $plain = $null
    try {
        $plain = [Text.Encoding]::UTF8.GetBytes([Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr))
        $ciphertext = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect($plain, $null, 'LocalMachine'))
    }
    finally {
        if ($plain) { [Array]::Clear($plain, 0, $plain.Length) }
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        $secret.Dispose()
    }
}

$deploymentLock = [Threading.Mutex]::new($false, 'Global\MattsAndSamRecordsDeployment')
$lockAcquired = $false
try {
    try { $lockAcquired = $deploymentLock.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $lockAcquired = $true }
    if (!$lockAcquired) { throw 'A website deployment is running. Try again once it finishes.' }
    $root = (Resolve-Path -LiteralPath $InstallDirectory).Path
    $release = (Get-Content -LiteralPath (Join-Path $root 'current-release.txt') -Raw).Trim()
    if ($release -notmatch '^[0-9]{14}-[a-f0-9]{8}$') { throw 'The current release ID is invalid.' }
    $activeDirectory = Join-Path (Join-Path $root 'releases') $release
    $activeLauncher = Get-Content -LiteralPath (Join-Path $activeDirectory 'Start-RecordSite.ps1') -Raw
    if (!$activeLauncher.Contains('AccountMail__Host')) { throw 'Install the email recovery website update first.' }
    $entry = Get-Content -LiteralPath (Join-Path $activeDirectory 'run-settings.json') -Raw | ConvertFrom-Json
    $settingsPath = Join-Path $root 'mail-settings.json'
    $original = if (Test-Path -LiteralPath $settingsPath -PathType Leaf) { [IO.File]::ReadAllText($settingsPath) }
    $settings = @{ Host = $smtpHost; Port = $port; From = $from; Username = $username; ProtectedPassword = $ciphertext } | ConvertTo-Json
    [IO.File]::WriteAllText($settingsPath, $settings)
    try {
        Restart-RecordRoom $entry.ListenUrl
    }
    catch {
        $failure = $_
        if ($null -ne $original) { [IO.File]::WriteAllText($settingsPath, $original) }
        else { Remove-Item -LiteralPath $settingsPath }
        try { Restart-RecordRoom $entry.ListenUrl }
        catch { Write-Warning 'The website did not restart after restoring the previous settings.' }
        throw $failure
    }
    Write-Output 'Ready. SMTP settings saved. Send a verification email from Manage > Account settings to check delivery.'
}
finally {
    if ($lockAcquired) { $deploymentLock.ReleaseMutex() }
    $deploymentLock.Dispose()
}
