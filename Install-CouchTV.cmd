@echo off
setlocal
title CouchTV installer
rem Double-click to install. Asks for administrator rights first.
net session >nul 2>&1
if errorlevel 1 goto elevate
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
echo.
pause
exit /b

:elevate
echo Asking for administrator rights...
set "CTV_SELF=%~f0"
powershell -NoProfile -Command "Start-Process -FilePath $env:CTV_SELF -Verb RunAs"
exit /b
