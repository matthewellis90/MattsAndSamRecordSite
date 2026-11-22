@echo off
setlocal DisableDelayedExpansion
set "RECORD_ROOM_MAIL_INSTALL=%~dp0deploy\Install-RecordRoomEmail.ps1"
if not exist "%RECORD_ROOM_MAIL_INSTALL%" (
    echo Open this launcher from the update folder.
    pause
    exit /b 2
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -Command "try { Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -ArgumentList ('-NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File ' + [char]34 + $env:RECORD_ROOM_MAIL_INSTALL + [char]34) -ErrorAction Stop } catch { Write-Host $_.Exception.Message; exit 1 }"
if errorlevel 1 pause
