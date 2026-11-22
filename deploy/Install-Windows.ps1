[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:ProgramData 'MattsAndSamRecords'),
    [string]$PublishedDirectory,
    [string]$SqlServer = "lpc:$env:COMPUTERNAME",
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]{0,63}$')][string]$DatabaseName = 'MattVinyl',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]+[A-Za-z0-9]$')][string]$PublicHost,
    [ValidateRange(1024, 65535)][int]$Port = 5782,
    [ValidateLength(1, 64)][string]$AdminUsername,
    [securestring]$AdminPassword
)

#Requires -RunAsAdministrator
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Set-PrivateDirectoryPermissions {
    param([string]$Path, [System.Security.AccessControl.FileSystemRights]$ServiceRights)

    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in $acl.Access) {
        $acl.RemoveAccessRuleSpecific($rule)
    }

    $inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-19')) {
        $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
        if ($sid -eq 'S-1-5-19') {
            $rights = $ServiceRights
        }
        $identity = [System.Security.Principal.SecurityIdentifier]::new($sid)
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            $identity, $rights, $inheritance, 'None', 'Allow'
        ))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Invoke-Database {
    param([string]$Phase, [string]$Database, [string[]]$InputArguments)

    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & $sqlcmd.Source -S $SqlServer -E -C -I -l 10 -b -r 1 -d $Database @InputArguments 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        $details = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "SQL Server failed during $Phase for '$Database' (sqlcmd exit code $code).`n$details"
    }
    $output
}

function Set-DatabaseAccess {
    $escapedAccount = $serviceAccount.Replace(']', ']]')
    $literalAccount = $serviceAccount.Replace("'", "''")
    Invoke-Database -Phase 'database and service login setup' -Database 'master' -InputArguments @('-Q', @"
SET NOCOUNT ON;
IF DB_ID(N'$DatabaseName') IS NULL CREATE DATABASE [$DatabaseName];
IF SUSER_ID(N'$literalAccount') IS NULL CREATE LOGIN [$escapedAccount] FROM WINDOWS;
"@)
    Invoke-Database -Phase 'catalog schema and legacy import' -Database $DatabaseName -InputArguments @('-i', $schema)
    Invoke-Database -Phase 'service role assignment' -Database $DatabaseName -InputArguments @('-Q', @"
SET NOCOUNT ON;
IF DATABASE_PRINCIPAL_ID(N'$literalAccount') IS NULL
    CREATE USER [$escapedAccount] FOR LOGIN [$escapedAccount];
IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_role_members
    WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'record_site_runtime')
      AND member_principal_id = DATABASE_PRINCIPAL_ID(N'$literalAccount')
)
    ALTER ROLE [record_site_runtime] ADD MEMBER [$escapedAccount];
"@)
}

function Set-PersonalCollectionAccess {
    $workerIdentifier = $provisionerAccount.Replace(']', ']]')
    $workerLiteral = $provisionerAccount.Replace("'", "''")
    Invoke-Database -Phase 'personal account schema' -Database $DatabaseName -InputArguments @('-i', $accountsSchema)
    Invoke-Database -Phase 'provisioner login' -Database 'master' -InputArguments @('-Q', @"
IF SUSER_ID(N'$workerLiteral') IS NULL CREATE LOGIN [$workerIdentifier] FROM WINDOWS;
GRANT CREATE ANY DATABASE TO [$workerIdentifier];
"@)
    Invoke-Database -Phase 'provisioner account access' -Database $DatabaseName -InputArguments @('-Q', @"
IF USER_ID(N'$workerLiteral') IS NULL CREATE USER [$workerIdentifier] FOR LOGIN [$workerIdentifier];
GRANT SELECT ON OBJECT::dbo.RecordSiteAccount (id, state, created_utc, attempts, last_attempt_utc) TO [$workerIdentifier];
GRANT UPDATE ON OBJECT::dbo.RecordSiteAccount (state, attempts, last_attempt_utc) TO [$workerIdentifier];
"@)
}

function Update-PersonalCollectionSchemas {
    $accounts = Invoke-Database -Phase 'list personal collections' -Database $DatabaseName -InputArguments @('-h', '-1', '-W', '-Q', @'
SET NOCOUNT ON;
SELECT CONVERT(varchar(36), id) FROM dbo.RecordSiteAccount WHERE state = 1;
'@)
    foreach ($line in $accounts) {
        $id = [Guid]::Empty
        if (![Guid]::TryParseExact($line.ToString().Trim(), 'D', [ref]$id)) { continue }
        $database = 'MattsRecords_User_' + $id.ToString('N')
        Invoke-Database -Phase 'personal catalog schema upgrade' -Database $database -InputArguments @('-i', $schema)
    }
}

function Get-AdminPasswordHash {
    param([string]$Application, [securestring]$Password)

    $passwordBuffer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    $process = $null
    $inputBytes = $null
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Application, '--hash-admin-password')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    try {
        $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordBuffer)
        if ($value.Contains([char]13) -or $value.Contains([char]10)) {
            throw 'The administrator password must fit on one line.'
        }
        $inputBytes = [System.Text.Encoding]::UTF8.GetBytes($value + [Environment]::NewLine)
        $value = $null
        $process = [System.Diagnostics.Process]::Start($startInfo)
        $inputStream = $process.StandardInput.BaseStream
        $inputStream.Write($inputBytes, 0, $inputBytes.Length)
        $inputStream.Close()
        $hash = $process.StandardOutput.ReadToEnd().Trim()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($hash)) {
            throw 'The application rejected the administrator password.'
        }
        return $hash
    }
    finally {
        if ($inputBytes) {
            [Array]::Clear($inputBytes, 0, $inputBytes.Length)
        }
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordBuffer)
        if ($process) {
            $process.Dispose()
        }
    }
}

function Wait-RecordSite {
    $deadline = [DateTime]::UtcNow.AddSeconds(70)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "$listenUrl/health/ready" -TimeoutSec 3
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }
    throw "The site did not become ready. See the application log at '$logDirectory'."
}

function Install-RecordSite {
    $projectRoot = Split-Path -Parent $PSScriptRoot
    $schema = Join-Path $projectRoot 'database\schema.sql'
    $accountsSchema = Join-Path $projectRoot 'database\accounts.sql'
    $sqlcmd = Get-Command sqlcmd.exe -CommandType Application -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $schema -PathType Leaf)) {
        throw "The required database schema script is missing from '$schema'."
    }

    $taskName = 'MattsAndSamRecords'
    $provisionerTaskName = 'MattsAndSamRecords-PersonalCollections'
    $installRoot = [System.IO.Path]::GetFullPath($InstallDirectory)
    if ($installRoot.TrimEnd('\') -eq [System.IO.Path]::GetPathRoot($installRoot).TrimEnd('\')) {
        throw 'Use a dedicated installation directory.'
    }

    $serviceSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-19')
    $serviceAccount = $serviceSid.Translate([System.Security.Principal.NTAccount]).Value
    $provisionerAccount = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18').Translate([System.Security.Principal.NTAccount]).Value
    $releases = Join-Path $installRoot 'releases'
    $state = Join-Path $installRoot 'state'
    $keysDirectory = Join-Path $state 'keys'
    $logDirectory = Join-Path $state 'logs'
    $currentFile = Join-Path $installRoot 'current-release.txt'
    $previousTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
    $previousProvisioner = Get-ScheduledTask -TaskName $provisionerTaskName -TaskPath '\' -ErrorAction SilentlyContinue
    $previousProvisionerXml = if ($previousProvisioner) { Export-ScheduledTask -TaskName $provisionerTaskName -TaskPath '\' }
    $previousTaskXml = $null
    $previousTaskWasRunning = $false
    $previousSettings = $null
    if ($previousTask) {
        if (-not (Test-Path -LiteralPath $currentFile -PathType Leaf)) {
            throw "A task named '$taskName' already exists. Use the directory for the current installation."
        }
        $previousRelease = (Get-Content -LiteralPath $currentFile -Raw).Trim()
        if ($previousRelease -notmatch '^[0-9]{14}-[a-f0-9]{8}$') {
            throw "The current release ID is invalid in '$currentFile'."
        }
        $previousSettingsPath = Join-Path (Join-Path $releases $previousRelease) 'run-settings.json'
        $previousSettings = Get-Content -LiteralPath $previousSettingsPath -Raw | ConvertFrom-Json
        $previousTaskXml = Export-ScheduledTask -TaskName $taskName -TaskPath '\'
        $previousTaskWasRunning = $previousTask.State -eq 'Running'
    }
    elseif ((Test-Path -LiteralPath $installRoot) -and
            (Get-ChildItem -LiteralPath $installRoot -Force | Where-Object Name -notin @('releases', 'state', 'current-release.txt'))) {
        throw "Use a dedicated installation directory. '$installRoot' contains other files."
    }
    if (-not $PublicHost) {
        $PublicHost = if ($previousSettings) { ($previousSettings.AllowedHosts -split ';')[0] } else { 'localhost' }
    }

    $releaseId = '{0:yyyyMMddHHmmss}-{1}' -f [DateTime]::UtcNow, ([Guid]::NewGuid().ToString('N').Substring(0, 8))
    $release = Join-Path $releases $releaseId
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    Set-PrivateDirectoryPermissions -Path $installRoot -ServiceRights 'ReadAndExecute'
    New-Item -ItemType Directory -Path $state -Force | Out-Null
    Set-PrivateDirectoryPermissions -Path $state -ServiceRights 'Modify'
    New-Item -ItemType Directory -Path $release, $keysDirectory, $logDirectory -Force | Out-Null

    if ($PublishedDirectory) {
        $source = (Resolve-Path -LiteralPath $PublishedDirectory).ProviderPath
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $release -Recurse -Force
    }
    else {
        $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction Stop
        $project = Join-Path $projectRoot 'MattsRecords.csproj'
        & $dotnet.Source publish $project --configuration Release --runtime win-x64 --self-contained false --output $release
        if ($LASTEXITCODE -ne 0) {
            throw 'Publishing the application failed. Install the .NET SDK or provide -PublishedDirectory.'
        }
    }

    $application = Join-Path $release 'MattsRecords.exe'
    if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
        throw "The release at '$release' does not contain MattsRecords.exe."
    }
    $launcher = Join-Path $release 'Start-RecordSite.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-RecordSite.ps1') -Destination $launcher
    $provisioner = Join-Path $release 'Provision-PersonalCollections.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Provision-PersonalCollections.ps1') -Destination $provisioner
    Copy-Item -LiteralPath $schema -Destination (Join-Path $release 'catalog-schema.sql')
    $provisionerSettingsPath = Join-Path $release 'provision-settings.json'
    $provisionerSettings = @{
        Server = $SqlServer
        OwnerDatabase = $DatabaseName
        RuntimeAccount = $serviceAccount
        SqlCmd = $sqlcmd.Source
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText($provisionerSettingsPath, $provisionerSettings)

    $adminHash = ''
    if ($previousSettings) {
        $adminHash = $previousSettings.AdminPasswordHash
        if (-not $AdminUsername) {
            $AdminUsername = $previousSettings.AdminUsername
        }
    }
    if (-not $AdminUsername) {
        $AdminUsername = 'admin'
    }
    if ($AdminPassword) {
        $adminHash = Get-AdminPasswordHash -Application $application -Password $AdminPassword
    }

    Set-DatabaseAccess
    Set-PersonalCollectionAccess

    $listenUrl = "http://127.0.0.1:$Port"
    $connection = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $connection['Data Source'] = $SqlServer
    $connection['Initial Catalog'] = $DatabaseName
    $connection['Integrated Security'] = $true
    $connection['Encrypt'] = $true
    $connection['TrustServerCertificate'] = $true

    $settingsPath = Join-Path $release 'run-settings.json'
    $settings = @{
        ListenUrl = $listenUrl
        AllowedHosts = (@($PublicHost, 'localhost', '127.0.0.1') | Select-Object -Unique) -join ';'
        ConnectionString = $connection.ConnectionString
        AdminUsername = $AdminUsername
        AdminPasswordHash = $adminHash
        KeysDirectory = $keysDirectory
        LogDirectory = $logDirectory
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText($settingsPath, $settings)

    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action = New-ScheduledTaskAction -Execute $powershell -WorkingDirectory $release -Argument (
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -SettingsPath "{1}"' -f $launcher, $settingsPath
    )
    $principal = New-ScheduledTaskPrincipal -UserId $serviceAccount -LogonType ServiceAccount -RunLevel Limited
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $taskOptions = @{
        MultipleInstances = 'IgnoreNew'
        StartWhenAvailable = $true
        ExecutionTimeLimit = [TimeSpan]::Zero
        RestartCount = 8
        RestartInterval = New-TimeSpan -Minutes 1
        AllowStartIfOnBatteries = $true
        DontStopIfGoingOnBatteries = $true
    }
    $taskSettings = New-ScheduledTaskSettingsSet @taskOptions
    $provisionerAction = New-ScheduledTaskAction -Execute $powershell -WorkingDirectory $release -Argument (
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -SettingsPath "{1}"' -f $provisioner, $provisionerSettingsPath
    )
    $provisionerPrincipal = New-ScheduledTaskPrincipal -UserId $provisionerAccount -LogonType ServiceAccount -RunLevel Highest
    $provisionerTrigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1)) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $provisionerTaskOptions = @{
        MultipleInstances = 'IgnoreNew'
        StartWhenAvailable = $true
        ExecutionTimeLimit = New-TimeSpan -Minutes 3
        AllowStartIfOnBatteries = $true
        DontStopIfGoingOnBatteries = $true
    }
    $provisionerTaskSettings = New-ScheduledTaskSettingsSet @provisionerTaskOptions

    $registeredNewTask = $false
    $provisionerChanged = $false
    try {
        if ($previousProvisioner) {
            $provisionerChanged = $true
            Disable-ScheduledTask -TaskName $provisionerTaskName -TaskPath '\' | Out-Null
            if ($previousProvisioner.State -eq 'Running') {
                Stop-ScheduledTask -TaskName $provisionerTaskName -TaskPath '\'
            }
        }
        Update-PersonalCollectionSchemas

        if ($previousTaskWasRunning) {
            Stop-ScheduledTask -TaskName $taskName -TaskPath '\'
            $stopDeadline = [DateTime]::UtcNow.AddSeconds(15)
            while ((Get-ScheduledTask -TaskName $taskName -TaskPath '\').State -eq 'Running') {
                if ([DateTime]::UtcNow -gt $stopDeadline) {
                    throw "The previous deployment task '$taskName' did not stop."
                }
                Start-Sleep -Milliseconds 300
            }
        }

        $registration = @{
            TaskName = $taskName
            TaskPath = '\'
            Action = $action
            Trigger = $trigger
            Principal = $principal
            Settings = $taskSettings
            Description = 'The Record Room website'
            Force = $true
        }
        Register-ScheduledTask @registration | Out-Null
        $registeredNewTask = $true
        Start-ScheduledTask -TaskName $taskName -TaskPath '\'
        Wait-RecordSite
        $provisionerChanged = $true
        $provisionerRegistration = @{
            TaskName = $provisionerTaskName
            TaskPath = '\'
            Action = $provisionerAction
            Trigger = $provisionerTrigger
            Principal = $provisionerPrincipal
            Settings = $provisionerTaskSettings
            Description = 'Creates blank SQL catalogs for personal record collection accounts'
            Force = $true
        }
        Register-ScheduledTask @provisionerRegistration | Out-Null
        Start-ScheduledTask -TaskName $provisionerTaskName -TaskPath '\'
        $provisionerReady = Join-Path $release 'provisioner-ready.txt'
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (!(Test-Path -LiteralPath $provisionerReady)) {
            if ([DateTime]::UtcNow -gt $deadline) {
                $provisionerLog = Join-Path $release 'provisioning.log'
                throw "The personal collection task did not connect to SQL Server. See '$provisionerLog'."
            }
            Start-Sleep -Milliseconds 500
        }
        $pendingCurrentFile = Join-Path $release 'pending-release.txt'
        [System.IO.File]::WriteAllText($pendingCurrentFile, $releaseId)
        if (Test-Path -LiteralPath $currentFile -PathType Leaf) {
            [System.IO.File]::Replace($pendingCurrentFile, $currentFile, [NullString]::Value)
        }
        else {
            [System.IO.File]::Move($pendingCurrentFile, $currentFile)
        }
    }
    catch {
        $deploymentFailure = $_
        try {
            if ($provisionerChanged) {
                if (Get-ScheduledTask -TaskName $provisionerTaskName -TaskPath '\' -ErrorAction SilentlyContinue) {
                    Stop-ScheduledTask -TaskName $provisionerTaskName -TaskPath '\' -ErrorAction SilentlyContinue
                }
                if ($previousProvisionerXml) {
                    Register-ScheduledTask -TaskName $provisionerTaskName -TaskPath '\' -Xml $previousProvisionerXml -Force | Out-Null
                }
                else {
                    Unregister-ScheduledTask -TaskName $provisionerTaskName -TaskPath '\' -Confirm:$false -ErrorAction SilentlyContinue
                }
            }
            if ($registeredNewTask) {
                Stop-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
                if ($previousTaskXml) {
                    Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Xml $previousTaskXml -Force | Out-Null
                }
                else {
                    Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
                }
            }
            if ($previousTaskWasRunning) {
                Start-ScheduledTask -TaskName $taskName -TaskPath '\'
            }
        }
        catch {
            Write-Warning "The previous task could not be restored: $($_.Exception.Message)"
        }
        throw $deploymentFailure
    }

    Write-Output "Ready at $listenUrl. Cloudflare origin for $PublicHost is $listenUrl."
    Write-Output "Application logs are in '$logDirectory'."
    Write-Output "Personal collection provisioning runs under '$provisionerTaskName'."
    if (-not $adminHash) {
        Write-Output 'Administration is disabled until an administrator password is configured.'
    }
}

$deploymentLock = [Threading.Mutex]::new($false, 'Global\MattsAndSamRecordsDeployment')
$lockAcquired = $false
try {
    try {
        $lockAcquired = $deploymentLock.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $lockAcquired = $true
    }
    if (-not $lockAcquired) {
        throw 'Another record site deployment is in progress.'
    }
    Install-RecordSite
}
finally {
    if ($lockAcquired) {
        $deploymentLock.ReleaseMutex()
    }
    $deploymentLock.Dispose()
}
