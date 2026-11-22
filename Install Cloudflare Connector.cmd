@echo off
setlocal DisableDelayedExpansion
set "installer=%~dp0deploy\Install-CloudflareConnector.ps1"
set "result=2"
if not exist "%installer%" (
    echo The connector script is missing. Open this launcher from the extracted MattsRecords-Windows folder.
    goto finish
)

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%installer%"
set "result=%errorlevel%"

:finish
echo.
if not "%result%"=="0" echo Installer exited with code %result%. Review the message above.
pause
exit /b %result%
