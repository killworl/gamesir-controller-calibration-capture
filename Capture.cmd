@echo off
rem Made by u/peoplehurt6666 - Reddit
setlocal
set "G7PRO_LAUNCH_DIR=%~dp0."
set "G7PRO_CAPTURE_OUTPUT=%~dp0captures"
call "%~dp0support\reader\LaunchCapture.cmd" %*
exit /b %errorlevel%
