[CmdletBinding()]
param([switch]$ReplaceToken)

#Requires -RunAsAdministrator
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Official Windows amd64 asset and checksum:
# https://github.com/cloudflare/cloudflared/releases/tag/2026.11.1
$version = '2026.11.1'
$sha256 = '341deda3db5ec498a0482254d6c38c0b7cf01a30c37bb5dd95d2216d08955e35'
$downloadUrl = "https://github.com/cloudflare/cloudflared/releases/download/$version/cloudflared-windows-amd64.exe"

$taskName = 'MattsRecordsCloudflareConnector'
$description = 'Dedicated Cloudflare Tunnel connector for the Matt and Sam record site'
$installDirectory = Join-Path $env:ProgramData $taskName
$binary = Join-Path $installDirectory "cloudflared-$version.exe"
$tokenPath = Join-Path $installDirectory 'tunnel-token'
$logDirectory = Join-Path $installDirectory 'logs'
$networkService = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-20')
$administrators = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')

function Assert-LocalItem {
    param([string]$Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "The connector path must not be a symbolic link or junction: '$Path'."
    }
}

function Set-ConnectorPermissions {
    param(
        [string]$Path,
        [System.Security.AccessControl.FileSystemRights]$ConnectorRights,
        [switch]$Directory
    )

    $acl = Get-Acl -LiteralPath $Path
    $acl.SetOwner($administrators)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in $acl.Access) {
        $acl.RemoveAccessRuleSpecific($rule)
    }

    $inheritance = [System.Security.AccessControl.InheritanceFlags]::None
    if ($Directory) {
        $inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    }
    foreach ($identity in @($administrators, $system, $networkService)) {
        $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
        if ($identity -eq $networkService) {
            $rights = $ConnectorRights
        }
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            $identity, $rights, $inheritance, 'None', 'Allow'
        ))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Save-TunnelToken {
    $secret = Read-Host 'Paste the new tunnel token or Windows connector command' -AsSecureString
    $buffer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
    $pending = Join-Path $installDirectory ('token-' + [Guid]::NewGuid().ToString('N'))
    try {
        $entry = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($buffer).Trim()
        $pattern = '^(?:(?:"[^"\r\n]+\\cloudflared(?:-windows-amd64)?(?:\.exe)?"|(?:[^\s"]*\\)?cloudflared(?:-windows-amd64)?(?:\.exe)?)\s+service\s+install\s+)?(?<token>[A-Za-z0-9+/]{40,}={0,2})$'
        $match = [regex]::Match($entry, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success) {
            throw 'Copy the Windows connector command or token for the dedicated tunnel from Cloudflare.'
        }

        [IO.File]::WriteAllText($pending, $match.Groups['token'].Value, [Text.UTF8Encoding]::new($false))
        Set-ConnectorPermissions -Path $pending -ConnectorRights Read
        if (Test-Path -LiteralPath $tokenPath -PathType Leaf) {
            [IO.File]::Replace($pending, $tokenPath, [NullString]::Value)
            Set-ConnectorPermissions -Path $tokenPath -ConnectorRights Read
        }
        else {
            [IO.File]::Move($pending, $tokenPath)
        }
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($buffer)
        $secret.Dispose()
        if (Test-Path -LiteralPath $pending) {
            Remove-Item -LiteralPath $pending -Force
        }
    }
}

$previousTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
if ($previousTask -and $previousTask.Description -ne $description) {
    throw "Another scheduled task already uses the name '$taskName'."
}

Assert-LocalItem -Path $installDirectory
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Set-ConnectorPermissions -Path $installDirectory -ConnectorRights ReadAndExecute -Directory
foreach ($path in @($binary, $tokenPath, $logDirectory)) {
    Assert-LocalItem -Path $path
}
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
Set-ConnectorPermissions -Path $logDirectory -ConnectorRights Modify -Directory

if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    $download = Join-Path $installDirectory ('download-' + [Guid]::NewGuid().ToString('N'))
    try {
        Write-Host "Downloading and verifying cloudflared $version."
        Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $download -TimeoutSec 60
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $sha256) {
            throw 'The cloudflared download did not match the official release checksum.'
        }
        [IO.File]::Move($download, $binary)
    }
    finally {
        if (Test-Path -LiteralPath $download) {
            Remove-Item -LiteralPath $download -Force
        }
    }
}
if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $sha256) {
    throw "The existing cloudflared executable at '$binary' failed checksum verification."
}
Set-ConnectorPermissions -Path $binary -ConnectorRights ReadAndExecute

if ($ReplaceToken -or -not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
    Save-TunnelToken
}
Set-ConnectorPermissions -Path $tokenPath -ConnectorRights Read

$serviceAccount = $networkService.Translate([System.Security.Principal.NTAccount]).Value
$arguments = 'tunnel --no-autoupdate --log-directory "{0}" run --token-file "{1}"' -f $logDirectory, $tokenPath
$action = New-ScheduledTaskAction -Execute $binary -Argument $arguments -WorkingDirectory $installDirectory
$principal = New-ScheduledTaskPrincipal -UserId $serviceAccount -LogonType ServiceAccount -RunLevel Limited
$trigger = New-ScheduledTaskTrigger -AtStartup
$taskOptions = @{
    MultipleInstances = 'IgnoreNew'
    StartWhenAvailable = $true
    ExecutionTimeLimit = [TimeSpan]::Zero
    RestartCount = 12
    RestartInterval = (New-TimeSpan -Minutes 1)
    AllowStartIfOnBatteries = $true
    DontStopIfGoingOnBatteries = $true
}
$registration = @{
    TaskName = $taskName
    TaskPath = '\'
    Description = $description
    Action = $action
    Principal = $principal
    Trigger = $trigger
    Settings = (New-ScheduledTaskSettingsSet @taskOptions)
}

if ($previousTask -and $previousTask.State -eq 'Running') {
    Stop-ScheduledTask -TaskName $taskName -TaskPath '\'
}
Register-ScheduledTask @registration -Force | Out-Null
Start-ScheduledTask -TaskName $taskName -TaskPath '\'

Write-Output "Started '$taskName'. Check the dedicated tunnel in Cloudflare for connection status."
Write-Output "Connector logs are in '$logDirectory'."
