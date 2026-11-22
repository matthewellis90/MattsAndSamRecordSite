@echo off
setlocal DisableDelayedExpansion
set "installer=%~dp0deploy\Install-Windows.ps1"
set "published=%~dp0publish"
set "result=2"
if not exist "%installer%" (
    echo The installer is missing. Open this launcher from the extracted update folder.
    goto finish
)
if not exist "%published%\MattsRecords.exe" (
    echo The Windows build is missing from the publish folder.
    goto finish
)

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%installer%" -PublishedDirectory "%published%"
set "result=%errorlevel%"

:finish
echo.
if not "%result%"=="0" echo Installer exited with code %result%. Review the message above.
pause
exit /b %result%
