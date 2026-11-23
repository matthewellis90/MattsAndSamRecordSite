@echo off
setlocal DisableDelayedExpansion
set "RECORD_ROOM_UPDATE_SCRIPT=%~dp0deploy\Update-AndEnrich.ps1"
if not exist "%RECORD_ROOM_UPDATE_SCRIPT%" (
    echo Open this launcher from the update folder.
    pause
    exit /b 2
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -Command "try { Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -ArgumentList ('-NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File ' + [char]34 + $env:RECORD_ROOM_UPDATE_SCRIPT + [char]34 + ' -EstimateMissingPressings') -ErrorAction Stop } catch { Write-Host $_.Exception.Message; exit 1 }"
if errorlevel 1 pause
