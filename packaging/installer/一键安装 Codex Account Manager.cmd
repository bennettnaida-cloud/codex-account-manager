@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul 2>&1
title Codex Account Manager Setup
color 0B

set "SETUP_SCRIPT=%~dp0Install-CodexAccountManager.ps1"
set "CAM_SETUP_ROOT=%~dp0"
set "WINDOWS_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

cls
echo ================================================================
echo            Codex Account Manager - One-click Setup
echo ================================================================
echo.
echo [1/3] The installer has started. Please keep this window open.

if not exist "%SETUP_SCRIPT%" goto :missing_script
if not exist "%WINDOWS_POWERSHELL%" goto :missing_powershell

echo [2/3] Preparing files and removing download blocking marks...
"%WINDOWS_POWERSHELL%" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "try { Get-ChildItem -LiteralPath $env:CAM_SETUP_ROOT -Force -File -ErrorAction Stop ^| Unblock-File -ErrorAction SilentlyContinue; exit 0 } catch { exit 0 }" >nul 2>&1

echo [3/3] Installing. A Chinese result dialog will appear when finished.
echo.
"%WINDOWS_POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SETUP_SCRIPT%" %*
set "SETUP_EXIT=%ERRORLEVEL%"
echo.

if not "%SETUP_EXIT%"=="0" goto :failed

color 0A
echo Installation completed successfully.
echo The result dialog contains the install path and log path.
if defined CAM_SETUP_NO_WAIT exit /b 0
echo This window will close in 8 seconds.
timeout /t 8 /nobreak >nul
exit /b 0

:missing_script
color 0C
echo ERROR: Install-CodexAccountManager.ps1 is missing.
echo Please extract the complete ZIP before running this file.
goto :hold_failure

:missing_powershell
color 0C
echo ERROR: Windows PowerShell was not found.
echo Windows 10 or Windows 11 x64 is required.
goto :hold_failure

:failed
color 0C
echo Installation failed with exit code %SETUP_EXIT%.
echo Please read the Chinese error dialog and the installer log path above.

:hold_failure
echo.
if defined CAM_SETUP_NO_WAIT exit /b 1
echo Press any key to keep this error visible and close the installer.
pause >nul
exit /b 1
