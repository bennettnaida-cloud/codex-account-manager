param(
    [string]$InstallPath,
    [string]$LogPath,
    [switch]$NoLaunch,
    [switch]$NoShortcuts,
    [switch]$NoRegistry,
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:InstallerLogPath = $null
$script:InstallerScriptPath = $PSCommandPath

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)

    $stream = $null
    $sha256 = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        if ($null -ne $sha256) {
            $sha256.Dispose()
        }
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Initialize-InstallerLog {
    $resolvedLogPath = $LogPath
    if ([string]::IsNullOrWhiteSpace($resolvedLogPath)) {
        $logRoot = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            Join-Path ([IO.Path]::GetTempPath()) 'CodexAccountManager\InstallerLogs'
        }
        else {
            Join-Path $env:LOCALAPPDATA 'CodexAccountManager\InstallerLogs'
        }
        $resolvedLogPath = Join-Path $logRoot (
            'Install-{0}-{1}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID)
    }

    $resolvedLogPath = [IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables($resolvedLogPath))
    $logParent = Split-Path -Parent $resolvedLogPath
    if ([string]::IsNullOrWhiteSpace($logParent)) {
        throw '安装日志路径无效。'
    }

    New-Item -ItemType Directory -Force -Path $logParent | Out-Null
    @(
        'Codex Account Manager 安装日志',
        ('开始时间：{0:yyyy-MM-dd HH:mm:ss zzz}' -f (Get-Date)),
        ('安装脚本：{0}' -f $script:InstallerScriptPath),
        ('Windows：{0}' -f [Environment]::OSVersion.VersionString),
        ('PowerShell：{0}' -f $PSVersionTable.PSVersion),
        ''
    ) | Set-Content -LiteralPath $resolvedLogPath -Encoding UTF8
    $script:InstallerLogPath = $resolvedLogPath
}

function Write-InstallerProgress {
    param(
        [Parameter(Mandatory)][string]$Status,
        [ValidateRange(0, 100)][int]$Percent
    )

    $line = '[{0:HH:mm:ss}] [{1,3}%] {2}' -f (Get-Date), $Percent, $Status
    if (-not [string]::IsNullOrWhiteSpace($script:InstallerLogPath)) {
        try {
            Add-Content -LiteralPath $script:InstallerLogPath -Value $line -Encoding UTF8
        }
        catch {
            # The visible console and result dialog remain available if logging is interrupted.
        }
    }
    if (-not $Quiet) {
        Write-Host $line -ForegroundColor Cyan
        Write-Progress -Activity 'Codex Account Manager 安装程序' -Status $Status -PercentComplete $Percent
    }
}

function Write-InstallerFailureDetail {
    param([Parameter(Mandatory)]$ErrorRecord)

    if ([string]::IsNullOrWhiteSpace($script:InstallerLogPath)) {
        return
    }
    try {
        @(
            '',
            '--- 错误详情 ---',
            ($ErrorRecord | Out-String),
            ('结束时间：{0:yyyy-MM-dd HH:mm:ss zzz}' -f (Get-Date))
        ) | Add-Content -LiteralPath $script:InstallerLogPath -Encoding UTF8
    }
    catch {
        # Do not hide the original installer error when the log cannot be appended.
    }
}

function Show-InstallerMessage {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('Information', 'Error')][string]$Kind = 'Information'
    )

    if ($Quiet) {
        return
    }

    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $icon = if ($Kind -eq 'Error') {
            [System.Windows.Forms.MessageBoxIcon]::Error
        }
        else {
            [System.Windows.Forms.MessageBoxIcon]::Information
        }
        [System.Windows.Forms.MessageBox]::Show(
            $Message,
            'Codex Account Manager 安装程序',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            $icon) | Out-Null
        return
    }
    catch {
        try {
            $popupIcon = if ($Kind -eq 'Error') { 16 } else { 64 }
            $shell = New-Object -ComObject WScript.Shell
            [void]$shell.Popup(
                $Message,
                0,
                'Codex Account Manager 安装程序',
                $popupIcon)
            return
        }
        catch {
            Write-Host $Message -ForegroundColor $(if ($Kind -eq 'Error') { 'Red' } else { 'Green' })
        }
    }
}

function Unblock-InstallerFiles {
    param([Parameter(Mandatory)][string]$PackageRoot)

    $unblockExtensions = @('.cmd', '.bat', '.ps1', '.exe', '.dll', '.node')
    foreach ($file in Get-ChildItem -LiteralPath $PackageRoot -Recurse -Force -File -ErrorAction SilentlyContinue) {
        if ($file.Extension -notin $unblockExtensions) {
            continue
        }
        try {
            Unblock-File -LiteralPath $file.FullName -ErrorAction Stop
        }
        catch {
            # An absent Zone.Identifier or a read-only source is harmless.
        }
    }
}

function Stop-InstalledManager {
    param([Parameter(Mandatory)][string]$ExecutablePath)

    $running = @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ExecutablePath -and
                [string]::Equals(
                    [IO.Path]::GetFullPath($_.ExecutablePath),
                    $ExecutablePath,
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
                        $ExecutablePath,
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'Codex Account Manager 仍在运行。请先关闭软件，然后重新双击安装。'
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function New-ManagerShortcut {
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    $parent = Split-Path -Parent $ShortcutPath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $ExecutablePath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.Arguments = ''
    $shortcut.IconLocation = $ExecutablePath + ',0'
    $shortcut.Description = '管理和切换 Codex 账号'
    $shortcut.Save()
}

try {
    Initialize-InstallerLog
    Write-InstallerProgress -Status '安装程序已启动，正在检查运行环境。' -Percent 2
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw '此安装包仅支持 Windows 10/11 x64。'
    }

    $packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    Write-InstallerProgress -Status '正在解除下载文件的 Windows 阻止标记。' -Percent 6
    Unblock-InstallerFiles -PackageRoot $packageRoot
    $payloadRoot = Join-Path $packageRoot 'payload'
    $defaultsRoot = Join-Path $packageRoot 'defaults'
    $versionPath = Join-Path $packageRoot 'package-version.txt'
    if ([string]::IsNullOrWhiteSpace($InstallPath)) {
        $InstallPath = Join-Path $env:LOCALAPPDATA 'Programs\CodexAccountManager'
    }
    $installRoot = [IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables($InstallPath))
    $installedExe = Join-Path $installRoot 'CodexAccountManager.exe'

    foreach ($required in @(
        (Join-Path $payloadRoot 'CodexAccountManager.exe'),
        (Join-Path $payloadRoot 'assets\CodexAccountManager.ico'),
        (Join-Path $payloadRoot '.tools\codex-cli\node_modules'),
        (Join-Path $payloadRoot 'CodexDreamSkin\bundle-version.txt'),
        (Join-Path $payloadRoot 'CodexDreamSkin\scripts\install-account-manager-theme.ps1'),
        (Join-Path $defaultsRoot 'accounts.json'),
        (Join-Path $defaultsRoot 'appsettings.json'),
        (Join-Path $defaultsRoot 'token-metadata.json'),
        (Join-Path $defaultsRoot 'usage-account-switches.json'),
        (Join-Path $packageRoot 'Uninstall-CodexAccountManager.ps1'),
        (Join-Path $packageRoot '卸载 Codex Account Manager.cmd'),
        (Join-Path $packageRoot 'SHA256SUMS.txt'),
        $versionPath
    )) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "安装包不完整，缺少：$required"
        }
    }

    Write-InstallerProgress -Status '安装包文件完整，正在校验关键程序。' -Percent 14
    foreach ($line in Get-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt')) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([0-9A-Fa-f]{64})\s{2}(.+)$') {
            throw '安装包校验清单格式无效。'
        }
        $relativePath = $Matches[2]
        $candidate = [IO.Path]::GetFullPath((Join-Path $packageRoot $relativePath))
        $packagePrefix = [IO.Path]::GetFullPath($packageRoot) + [IO.Path]::DirectorySeparatorChar
        if (-not $candidate.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "安装包校验文件不存在或路径无效：$relativePath"
        }
        $actualHash = Get-Sha256Hex -Path $candidate
        if (-not $actualHash.Equals($Matches[1], [StringComparison]::OrdinalIgnoreCase)) {
            throw "安装包文件校验失败：$relativePath"
        }
    }

    Write-InstallerProgress -Status '校验通过，正在准备安装目录。' -Percent 28
    Stop-InstalledManager -ExecutablePath $installedExe
    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
    Write-InstallerProgress -Status '正在复制主程序。' -Percent 38
    Copy-Item -LiteralPath (Join-Path $payloadRoot 'CodexAccountManager.exe') -Destination $installedExe -Force
    Write-InstallerProgress -Status '正在复制界面资源。' -Percent 46
    Copy-DirectoryContents -Source (Join-Path $payloadRoot 'assets') -Destination (Join-Path $installRoot 'assets')
    Write-InstallerProgress -Status '正在复制本地运行组件，请稍候。' -Percent 58
    Copy-DirectoryContents -Source (Join-Path $payloadRoot '.tools') -Destination (Join-Path $installRoot '.tools')
    Copy-DirectoryContents -Source (Join-Path $payloadRoot 'CodexDreamSkin') -Destination (Join-Path $installRoot 'CodexDreamSkin')
    Unblock-InstallerFiles -PackageRoot $installRoot

    Write-InstallerProgress -Status '正在初始化安全的空白配置。' -Percent 70
    foreach ($defaultName in @(
        'accounts.json',
        'appsettings.json',
        'token-metadata.json',
        'usage-account-switches.json'
    )) {
        $destination = Join-Path $installRoot $defaultName
        if (-not (Test-Path -LiteralPath $destination)) {
            Copy-Item -LiteralPath (Join-Path $defaultsRoot $defaultName) -Destination $destination -Force
        }
    }

    Copy-Item -LiteralPath (Join-Path $packageRoot 'Uninstall-CodexAccountManager.ps1') -Destination $installRoot -Force
    Copy-Item -LiteralPath (Join-Path $packageRoot '卸载 Codex Account Manager.cmd') -Destination $installRoot -Force
    Copy-Item -LiteralPath (Join-Path $packageRoot 'README.md') -Destination $installRoot -Force
    Copy-Item -LiteralPath (Join-Path $packageRoot '安装说明.txt') -Destination $installRoot -Force
    Copy-Item -LiteralPath $versionPath -Destination $installRoot -Force

    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Account Manager.lnk'
    $startMenuFolder = Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Account Manager'
    $startMenuShortcut = Join-Path $startMenuFolder 'Codex Account Manager.lnk'
    if (-not $NoShortcuts) {
        Write-InstallerProgress -Status '正在创建桌面和开始菜单快捷方式。' -Percent 80
        New-ManagerShortcut -ShortcutPath $desktopShortcut -ExecutablePath $installedExe -WorkingDirectory $installRoot
        New-ManagerShortcut -ShortcutPath $startMenuShortcut -ExecutablePath $installedExe -WorkingDirectory $installRoot
        Copy-Item -LiteralPath (Join-Path $installRoot '卸载 Codex Account Manager.cmd') -Destination $startMenuFolder -Force
    }

    if (-not $NoRegistry) {
        Write-InstallerProgress -Status '正在写入当前用户的卸载信息。' -Percent 88
        $version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
        $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexAccountManager'
        New-Item -Path $uninstallKey -Force | Out-Null
        $uninstallScript = Join-Path $installRoot 'Uninstall-CodexAccountManager.ps1'
        $uninstallCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $uninstallScript + '"'
        $estimatedSize = [int][Math]::Ceiling((
            Get-ChildItem -LiteralPath $installRoot -Recurse -Force -File |
                Measure-Object Length -Sum).Sum / 1KB)
        Set-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'Codex Account Manager'
        Set-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $version
        Set-ItemProperty -Path $uninstallKey -Name Publisher -Value 'Codex Account Manager Community Build'
        Set-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot
        Set-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value ($installedExe + ',0')
        Set-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand
        New-ItemProperty -Path $uninstallKey -Name EstimatedSize -Value $estimatedSize -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
    }

    if (-not $NoLaunch) {
        Write-InstallerProgress -Status '正在启动 Codex Account Manager。' -Percent 96
        Start-Process -FilePath $installedExe -WorkingDirectory $installRoot | Out-Null
    }

    Write-InstallerProgress -Status '安装完成。' -Percent 100
    if (-not $Quiet) {
        Write-Progress -Activity 'Codex Account Manager 安装程序' -Completed
    }
    $shortcutMessage = if ($NoShortcuts) {
        '本次测试未创建快捷方式。'
    }
    else {
        '已创建桌面和开始菜单快捷方式。'
    }
    Show-InstallerMessage -Message (
        "安装完成。`r`n`r`n安装目录：$installRoot`r`n" +
        "$shortcutMessage`r`n以后重复运行本安装包可升级，并保留已有账号配置。`r`n`r`n" +
        "安装日志：$script:InstallerLogPath")
    Write-Output "InstallPath=$installRoot"
    Write-Output "InstalledExe=$installedExe"
    Write-Output "InstallerLog=$script:InstallerLogPath"
    exit 0
}
catch {
    $message = $_.Exception.Message
    Write-InstallerFailureDetail -ErrorRecord $_
    if (-not $Quiet) {
        Write-Progress -Activity 'Codex Account Manager 安装程序' -Completed
    }
    $logHint = if ([string]::IsNullOrWhiteSpace($script:InstallerLogPath)) {
        '安装日志无法创建；请保留当前命令窗口中的错误信息。'
    }
    else {
        "安装日志：$script:InstallerLogPath"
    }
    Show-InstallerMessage -Message ("安装失败：`r`n`r`n$message`r`n`r`n$logHint") -Kind Error
    Write-Error "$message`r`n$logHint"
    exit 1
}
