[CmdletBinding()]
param([Parameter(Mandatory)][string]$SettingsPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
$schema = Join-Path $PSScriptRoot 'catalog-schema.sql'
$log = Join-Path $PSScriptRoot 'provisioning.log'
$ready = Join-Path $PSScriptRoot 'provisioner-ready.txt'

function Invoke-Sql {
    param([string]$Database, [string[]]$Arguments)
    $ErrorActionPreference = 'Continue'
    $result = & $config.SqlCmd -S $config.Server -d $Database -E -C -I -l 10 -b -x @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ($result -join [Environment]::NewLine)
    }
    $result
}

try {
    if (!(Test-Path -LiteralPath $ready)) {
        Invoke-Sql -Database 'master' -Arguments @('-Q', @'
IF COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE'), 0) <> 1
    THROW 50003, 'The provisioning login cannot create a personal database.', 1;
'@) | Out-Null
    }
    $pending = Invoke-Sql -Database $config.OwnerDatabase -Arguments @('-h', '-1', '-W', '-Q', @'
SET NOCOUNT ON;
SELECT TOP (3) CONVERT(varchar(36), id)
FROM dbo.RecordSiteAccount
WHERE state = 0 AND (last_attempt_utc IS NULL OR last_attempt_utc < DATEADD(minute, -2, SYSUTCDATETIME()))
ORDER BY created_utc, id;
'@)
    if (!(Test-Path -LiteralPath $ready)) { [IO.File]::WriteAllText($ready, 'Ready') }
}
catch {
    Add-Content -LiteralPath $log -Value "[$([DateTimeOffset]::UtcNow)] $($_.Exception.Message)"
    throw
}
$runtimeIdentifier = $config.RuntimeAccount.Replace(']', ']]')
$runtimeLiteral = $config.RuntimeAccount.Replace("'", "''")
foreach ($line in $pending) {
    $id = [Guid]::Empty
    if (![Guid]::TryParseExact($line.ToString().Trim(), 'D', [ref]$id)) { continue }
    $database = 'MattsRecords_User_' + $id.ToString('N')
    try {
        Invoke-Sql -Database 'master' -Arguments @('-Q', @"
IF DB_ID(N'$database') IS NULL EXEC(N'CREATE DATABASE [$database]');
"@) | Out-Null
        Invoke-Sql -Database $database -Arguments @('-i', $schema) | Out-Null
        Invoke-Sql -Database $database -Arguments @('-Q', @"
SET NOCOUNT ON;
IF USER_ID(N'$runtimeLiteral') IS NULL CREATE USER [$runtimeIdentifier] FOR LOGIN [$runtimeIdentifier];
IF IS_ROLEMEMBER(N'record_site_runtime', N'$runtimeLiteral') <> 1
    ALTER ROLE [record_site_runtime] ADD MEMBER [$runtimeIdentifier];

DECLARE @data sysname = (SELECT name FROM sys.database_files WHERE file_id = 1);
DECLARE @log sysname = (SELECT TOP (1) name FROM sys.database_files WHERE type = 1 ORDER BY file_id);
DECLARE @limits nvarchar(max) =
    N'ALTER DATABASE [$database] MODIFY FILE (NAME = ' + QUOTENAME(@data) + N', MAXSIZE = 256MB); ' +
    N'ALTER DATABASE [$database] MODIFY FILE (NAME = ' + QUOTENAME(@log) + N', MAXSIZE = 64MB);';
EXEC sys.sp_executesql @limits;
ALTER DATABASE [$database] SET RECOVERY SIMPLE;
"@) | Out-Null
        Invoke-Sql -Database $config.OwnerDatabase -Arguments @('-Q', "UPDATE dbo.RecordSiteAccount SET state = 1 WHERE id = '$id' AND state = 0;") | Out-Null
    }
    catch {
        try {
            Add-Content -LiteralPath $log -Value "[$([DateTimeOffset]::UtcNow)] $database $($_.Exception.Message)"
        }
        catch { Write-Warning "Could not write the provisioning log." }
        Invoke-Sql -Database $config.OwnerDatabase -Arguments @('-Q', @"
UPDATE dbo.RecordSiteAccount SET attempts = attempts + 1, last_attempt_utc = SYSUTCDATETIME(),
    state = CASE WHEN attempts >= 4 THEN 2 ELSE 0 END WHERE id = '$id' AND state = 0;
"@) | Out-Null
    }
}
