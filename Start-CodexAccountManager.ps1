param(
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appExe = Join-Path $root 'dist\CodexAccountManager\CodexAccountManager.exe'

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
$startInfo.WorkingDirectory = $root
$startInfo.UseShellExecute = $true
[void][System.Diagnostics.Process]::Start($startInfo)
