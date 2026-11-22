@echo off
setlocal DisableDelayedExpansion
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0deploy\Configure-Email.ps1"
set "result=%errorlevel%"
echo.
if not "%result%"=="0" echo Email setup exited with code %result%. Review the message above.
pause
exit /b %result%
