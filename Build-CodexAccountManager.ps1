Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bundledDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (-not [string]::IsNullOrWhiteSpace($env:CODEX_ACCOUNT_MANAGER_DOTNET)) {
    [IO.Path]::GetFullPath($env:CODEX_ACCOUNT_MANAGER_DOTNET)
}
elseif (Test-Path -LiteralPath $bundledDotnet -PathType Leaf) {
    $bundledDotnet
}
else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $command) { $bundledDotnet } else { $command.Source }
}
$usingBundledDotnet = [string]::Equals(
    [IO.Path]::GetFullPath($dotnet),
    [IO.Path]::GetFullPath($bundledDotnet),
    [StringComparison]::OrdinalIgnoreCase)
$project = Join-Path $root 'src\CodexAccountManager\CodexAccountManager.csproj'
$out = Join-Path $root 'dist\CodexAccountManager'
$buildVersion = if ([string]::IsNullOrWhiteSpace($env:CAM_VERSION)) {
    Get-Date -Format 'yyyy.MM.dd'
}
else {
    $env:CAM_VERSION.Trim()
}
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("codex-account-manager-build-" + [guid]::NewGuid().ToString('N'))
$oldAccountManagerHome = $env:CODEX_ACCOUNT_MANAGER_HOME
$oldDotnetRoot = $env:DOTNET_ROOT
$oldDotnetRootX64 = $env:DOTNET_ROOT_X64
$localDotnetRoot = Split-Path -Parent $dotnet

function Get-CurrentProjectGatewayProcesses {
    try {
        return @(Get-CimInstance Win32_Process | Where-Object {
            $_.CommandLine -match '(?i)(?:^|\s)--local-pat-gateway(?:\s|$)' -and
            $_.CommandLine -match [regex]::Escape($root)
        })
    }
    catch {
        return @()
    }
}

# Release the optional local PAT gateway before publishing. A running single-file
# process can keep the previous executable locked on Windows. Ask the existing
# manager to authenticate the shutdown request; this also works with the previous
# build, whose shutdown endpoint did not require authentication.
$existingLauncher = Join-Path $out 'CodexAccountManager.exe'
$previousHomeForGateway = $env:CODEX_ACCOUNT_MANAGER_HOME
$env:CODEX_ACCOUNT_MANAGER_HOME = $root
try {
    if (Test-Path -LiteralPath $existingLauncher -PathType Leaf) {
        try {
            & $existingLauncher '--shutdown-local-pat-gateway' 2>$null | Out-Null
        }
        catch {
            # The gateway is optional and may not be running.
        }
    }
}
finally {
    $env:CODEX_ACCOUNT_MANAGER_HOME = $previousHomeForGateway
}

$gatewayDeadline = [DateTime]::UtcNow.AddSeconds(5)
while ([DateTime]::UtcNow -lt $gatewayDeadline) {
    if (@(Get-CurrentProjectGatewayProcesses).Count -eq 0) {
        break
    }
    Start-Sleep -Milliseconds 150
}
if (@(Get-CurrentProjectGatewayProcesses).Count -gt 0) {
    # A gateway started with a different CODEX_ACCOUNT_MANAGER_HOME has a different
    # control-secret path and does not lock this source tree's output. Stop only the
    # exact current-project gateway before replacing its executable.
    foreach ($gatewayProcess in @(Get-CurrentProjectGatewayProcesses)) {
        Stop-Process -Id ([int]$gatewayProcess.ProcessId) -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 250
}
if (@(Get-CurrentProjectGatewayProcesses).Count -gt 0) {
    throw 'The current project local PAT gateway is still running. Close it before publishing so the executable can be replaced safely.'
}

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Missing dotnet SDK: $dotnet"
}

$dotnetInfo = (& $dotnet --info 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or
    ($dotnetInfo -notmatch '(?im)^\s*Architecture:\s*x64\s*$' -and
     $dotnetInfo -notmatch '(?im)^\s*RID:\s*win-x64\s*$')) {
    throw "The local dotnet SDK is not an x64 installation: $dotnet"
}

if ($usingBundledDotnet) {
    $desktopRuntimeRoot = Join-Path $localDotnetRoot 'shared\Microsoft.WindowsDesktop.App'
    $compatibleDesktopRuntime = Get-ChildItem -LiteralPath $desktopRuntimeRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.\d+\.\d+(?:\.\d+)?$' } |
        Select-Object -First 1
    if ($null -eq $compatibleDesktopRuntime) {
        throw "Missing x64 Microsoft.WindowsDesktop.App 10.x under the local dotnet root: $localDotnetRoot."
    }
}

& $dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    "-p:Version=$buildVersion" `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $out
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$appExe = Join-Path $out 'CodexAccountManager.exe'
try {
    # The self-test and any framework-dependent build helper must resolve the
    # already bundled project-local x64 runtime instead of opening a download URL.
    $env:DOTNET_ROOT = $localDotnetRoot
    $env:DOTNET_ROOT_X64 = $localDotnetRoot
    $tempAssets = Join-Path $tempRoot 'assets'
    $tempHome1 = Join-Path $tempRoot 'acct-example-one'
    $tempHome2 = Join-Path $tempRoot 'acct-example-two'
    New-Item -ItemType Directory -Force -Path $tempAssets, $tempHome1, $tempHome2 | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'assets\CodexAccountManager.ico') -Destination (Join-Path $tempAssets 'CodexAccountManager.ico') -Force
    'model = "gpt-5.6-terra"' | Set-Content -LiteralPath (Join-Path $tempHome1 'config.toml') -Encoding UTF8
    'model = "gpt-5.6-terra"' | Set-Content -LiteralPath (Join-Path $tempHome2 'config.toml') -Encoding UTF8
    @(
        [pscustomobject]@{ name = 'example-one'; codexHome = $tempHome1 },
        [pscustomobject]@{ name = 'example-two'; codexHome = $tempHome2 }
    ) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $tempRoot 'accounts.json') -Encoding UTF8

    $env:CODEX_ACCOUNT_MANAGER_HOME = $tempRoot
    $selfTestOut = [System.IO.Path]::GetTempFileName()
    $selfTestErr = [System.IO.Path]::GetTempFileName()
    $selfTestProcess = Start-Process -FilePath $appExe -ArgumentList @('--self-test') -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $selfTestOut -RedirectStandardError $selfTestErr
    $selfTestOutput = (Get-Content -LiteralPath $selfTestOut -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath $selfTestErr -Raw -ErrorAction SilentlyContinue)
    Remove-Item -LiteralPath $selfTestOut, $selfTestErr -ErrorAction SilentlyContinue
    if ($selfTestProcess.ExitCode -ne 0) {
        throw "CodexAccountManager self-test failed with exit code $($selfTestProcess.ExitCode): $selfTestOutput"
    }
    if ($selfTestOutput -notmatch 'Self test passed') {
        throw "CodexAccountManager self-test did not report success: $selfTestOutput"
    }
}
finally {
    $env:CODEX_ACCOUNT_MANAGER_HOME = $oldAccountManagerHome
    $env:DOTNET_ROOT = $oldDotnetRoot
    $env:DOTNET_ROOT_X64 = $oldDotnetRootX64
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# Keep a source-tree development shortcut current without replacing a shortcut
# owned by an installed copy of the application.
$desktopPath = [Environment]::GetFolderPath('Desktop')
$desktopShortcutPath = Join-Path $desktopPath 'Codex Account Manager.lnk'
if (Test-Path -LiteralPath $desktopShortcutPath -PathType Leaf) {
    $shortcutShell = New-Object -ComObject WScript.Shell
    $desktopShortcut = $shortcutShell.CreateShortcut($desktopShortcutPath)
    $sourceRootPrefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $shortcutTarget = if ([string]::IsNullOrWhiteSpace($desktopShortcut.TargetPath)) {
        ''
    }
    else {
        [IO.Path]::GetFullPath($desktopShortcut.TargetPath)
    }
    if ($shortcutTarget.StartsWith($sourceRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $desktopShortcut.TargetPath = $appExe
        $desktopShortcut.WorkingDirectory = $root
        $desktopShortcut.Arguments = ''
        $desktopShortcut.IconLocation = $appExe + ',0'
        $desktopShortcut.Save()
    }
}
