@echo off
chcp 65001 >nul
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-CodexAccountManager.ps1"
if errorlevel 1 pause
exit /b %errorlevel%
