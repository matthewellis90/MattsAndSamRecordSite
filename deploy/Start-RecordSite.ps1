[CmdletBinding()]
param([Parameter(Mandatory)][string]$SettingsPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$applicationDirectory = Split-Path -Parent $SettingsPath
$application = Join-Path $applicationDirectory 'MattsRecords.exe'
$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json

$env:DOTNET_ENVIRONMENT = 'Production'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:Urls = $settings.ListenUrl
$env:AllowedHosts = $settings.AllowedHosts
$env:ConnectionStrings__DefaultConnection = $settings.ConnectionString
$env:Admin__Username = $settings.AdminUsername
$env:Admin__PasswordHash = $settings.AdminPasswordHash
$env:DataProtection__KeysPath = $settings.KeysDirectory

$mailSettingsPath = Join-Path (Split-Path -Parent (Split-Path -Parent $applicationDirectory)) 'mail-settings.json'
if (Test-Path -LiteralPath $mailSettingsPath -PathType Leaf) {
    Add-Type -AssemblyName System.Security
    $mail = Get-Content -LiteralPath $mailSettingsPath -Raw | ConvertFrom-Json
    $env:AccountMail__Host = $mail.Host
    $env:AccountMail__Port = $mail.Port.ToString()
    $env:AccountMail__From = $mail.From
    $env:AccountMail__Username = $mail.Username
    $env:AccountMail__PublicOrigin = 'https://' + ($settings.AllowedHosts -split ';')[0]
    if ($mail.ProtectedPassword) {
        $protected = [Convert]::FromBase64String($mail.ProtectedPassword)
        $plain = [Security.Cryptography.ProtectedData]::Unprotect($protected, $null, 'LocalMachine')
        try {
            $env:AccountMail__Password = [Text.Encoding]::UTF8.GetString($plain)
        }
        finally {
            [Array]::Clear($plain, 0, $plain.Length)
        }
    }
}

Set-Location -LiteralPath $applicationDirectory
$log = Join-Path $settings.LogDirectory 'record-site.log'
$previousLog = Join-Path $settings.LogDirectory 'record-site.previous.log'
if (Test-Path -LiteralPath $log) {
    Move-Item -LiteralPath $log -Destination $previousLog -Force
}

# Keep the server in the task's process tree and propagate its exit code for recovery.
$ErrorActionPreference = 'Continue'
& $application *> $log
exit $LASTEXITCODE
