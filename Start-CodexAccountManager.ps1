param(
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$currentVersion = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$versionedRoot = Join-Path $root 'dist'
$discoveredAppPaths = @(
    Get-ChildItem -LiteralPath $versionedRoot -Directory -Filter ("CodexAccountManager-" + $currentVersion + "*") -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'CodexAccountManager.exe') -PathType Leaf } |
        Sort-Object LastWriteTime -Descending |
        ForEach-Object { Join-Path $_.FullName 'CodexAccountManager.exe' }
)
$preferredAppPaths = @(
    $discoveredAppPaths
    (Join-Path $root 'dist\CodexAccountManager-2.3.2-hotfix\CodexAccountManager.exe')
    (Join-Path $root 'dist\CodexAccountManager-2.3.2-fixed\CodexAccountManager.exe')
    (Join-Path $root 'dist\CodexAccountManager-2.2.25-fallback\CodexAccountManager.exe')
)
$appExe = $preferredAppPaths |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($appExe)) {
    # Keep the startup error deterministic when both the primary and fallback package
    # are missing instead of silently selecting an unrelated historical build.
    $appExe = $preferredAppPaths[0]
}
$dataRoot = Join-Path (Split-Path -Parent $root) 'codex-account-manager'
if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'accounts.json') -PathType Leaf)) {
    $dataRoot = $root
}
$env:CODEX_ACCOUNT_MANAGER_HOME = $dataRoot

function Show-StartupError {
    param([Parameter(Mandatory)][string]$Message)

    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        $Message,
        'Codex Account Manager',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    ) | Out-Null
}

if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
    $message = "The self-contained application was not found:`n$appExe`n`nRun Build-CodexAccountManager.ps1 first. No runtime download page will be opened."
    if ($CheckOnly) {
        Write-Output "MISSING: self-contained win-x64 application [$appExe]"
    }
    else {
        Show-StartupError -Message $message
    }
    exit 11
}

if ($CheckOnly) {
    Write-Output "OK: self-contained win-x64 application [$appExe]"
    exit 0
}

# The supported dist executable is published with --self-contained true. Launch
# it directly: checking or installing a machine-wide Desktop Runtime here would
# be both unnecessary and the source of repeated browser download prompts.
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $appExe
$startInfo.WorkingDirectory = $dataRoot
$startInfo.UseShellExecute = $true
[void][System.Diagnostics.Process]::Start($startInfo)
