param([switch]$Quiet)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$installedExe = Join-Path $installRoot 'CodexAccountManager.exe'

function Show-UninstallMessage {
    param([Parameter(Mandatory)][string]$Message)
    if ($Quiet) {
        return
    }
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        $Message,
        'Codex Account Manager 卸载程序',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
}

try {
    $running = @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ExecutablePath -and
                [string]::Equals(
                    [IO.Path]::GetFullPath($_.ExecutablePath),
                    $installedExe,
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    foreach ($item in $running) {
        $process = Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            [void]$process.CloseMainWindow()
        }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $remaining = @(
            Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.ExecutablePath -and
                    [string]::Equals(
                        [IO.Path]::GetFullPath($_.ExecutablePath),
                        $installedExe,
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        if ($remaining.Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($remaining.Count -gt 0) {
        throw '软件仍在运行，请先关闭后再卸载。'
    }

    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Account Manager.lnk'
    $startMenuFolder = Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Account Manager'
    Remove-Item -LiteralPath $desktopShortcut -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $startMenuFolder) {
        Remove-Item -LiteralPath $startMenuFolder -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexAccountManager' -Recurse -Force -ErrorAction SilentlyContinue

    foreach ($directoryName in @('assets', '.tools', 'CodexDreamSkin')) {
        $directory = Join-Path $installRoot $directoryName
        if (Test-Path -LiteralPath $directory) {
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
    }
    foreach ($fileName in @(
        'CodexAccountManager.exe',
        'README.md',
        '安装说明.txt',
        'package-version.txt'
    )) {
        Remove-Item -LiteralPath (Join-Path $installRoot $fileName) -Force -ErrorAction SilentlyContinue
    }

    Show-UninstallMessage -Message (
        "程序和快捷方式已卸载。`r`n`r`n" +
        "为防止误删，账号列表、额度记录和设置仍保留在：`r`n$installRoot`r`n`r`n" +
        '确认不再需要后，可手动删除该目录。登录凭据目录不会由卸载程序删除。')
    exit 0
}
catch {
    if (-not $Quiet) {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            ("卸载失败：`r`n`r`n" + $_.Exception.Message),
            'Codex Account Manager 卸载程序',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
    Write-Error $_.Exception.Message
    exit 1
}
