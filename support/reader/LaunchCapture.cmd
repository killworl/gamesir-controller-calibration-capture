@echo off
rem Made by u/peoplehurt6666 - Reddit
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Capture.ps1" %*
set "captureExit=%errorlevel%"
if not "%captureExit%"=="0" pause
exit /b %captureExit%
