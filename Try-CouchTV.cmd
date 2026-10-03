@echo off
setlocal
title CouchTV preview
rem Opens CouchTV full screen on any Windows PC without installing anything. Alt+F4 closes it.
set "OUT=%TEMP%\CouchTV-preview"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" -OutDir "%OUT%" >nul
if errorlevel 1 goto failed
copy /y "%~dp0couchtv.ini" "%OUT%\couchtv.ini" >nul
echo CouchTV is opening full screen. Press Alt+F4 to close it.
start "" "%OUT%\CouchTV.exe"
exit /b

:failed
echo Build failed - run scripts\build.ps1 in PowerShell to see why.
pause
