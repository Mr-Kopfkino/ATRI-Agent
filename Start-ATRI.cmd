@echo off
set "ATRI_ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-ATRI.ps1"
if errorlevel 1 pause
