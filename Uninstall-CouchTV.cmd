@echo off
setlocal
title CouchTV uninstaller
rem Double-click to remove CouchTV and get the normal Windows desktop back.
net session >nul 2>&1
if errorlevel 1 goto elevate
set "PS1=%~dp0uninstall.ps1"
if not exist "%PS1%" set "PS1=%~dp0scripts\uninstall.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
echo.
pause
exit /b

:elevate
echo Asking for administrator rights...
set "CTV_SELF=%~f0"
powershell -NoProfile -Command "Start-Process -FilePath $env:CTV_SELF -Verb RunAs"
exit /b
