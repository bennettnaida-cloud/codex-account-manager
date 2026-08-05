@echo off
setlocal
set "CODEX_ACCOUNT_MANAGER_HOME=%~dp0"
set "CODEX_PROXY_HOST="
for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "$p=Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue; if($p -and [int]$p.ProxyEnable -ne 0 -and $p.ProxyServer){ $s=[string]$p.ProxyServer; if($s -match '(^|;)https?=([^;]+)'){ $Matches[2] } else { $s } }" 2^>nul`) do set "CODEX_PROXY_HOST=%%P"
if defined CODEX_PROXY_HOST (
  set "HTTP_PROXY=http://%CODEX_PROXY_HOST%"
  set "HTTPS_PROXY=http://%CODEX_PROXY_HOST%"
  set "ALL_PROXY=http://%CODEX_PROXY_HOST%"
  set "http_proxy=http://%CODEX_PROXY_HOST%"
  set "https_proxy=http://%CODEX_PROXY_HOST%"
  set "all_proxy=http://%CODEX_PROXY_HOST%"
)
set "LOCAL_CODEX=%~dp0.tools\codex-cli\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe"
if exist "%LOCAL_CODEX%" (
  set "CODEX_SWITCHER_CODEX_COMMAND=%LOCAL_CODEX%"
) else if exist "%USERPROFILE%\AppData\Roaming\npm\codex.cmd" (
  set "CODEX_SWITCHER_CODEX_COMMAND=%USERPROFILE%\AppData\Roaming\npm\codex.cmd"
)
set "STARTUP_SCRIPT=%~dp0Start-CodexAccountManager.ps1"
if not exist "%STARTUP_SCRIPT%" (
  echo Missing startup script: "%STARTUP_SCRIPT%"
  exit /b 11
)
start "" /D "%~dp0" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%STARTUP_SCRIPT%"
