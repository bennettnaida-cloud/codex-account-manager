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
$buildVersion = if ([string]::IsNullOrWhiteSpace($env:CAM_VERSION)) {
    (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
}
else {
    $env:CAM_VERSION.Trim()
}
$out = Join-Path $root ("dist\CodexAccountManager-" + $buildVersion)
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

# Publishing is staged beside the live package. Never stop the optional PAT gateway:
# the user's 2.2.25 installation may still own port 8317, while this 2.3.x build
# uses its dedicated versioned listener. The running gateway must remain available.
$existingLauncher = Join-Path $out 'CodexAccountManager.exe'

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

# Keep the portable publish folder self-describing as well as the installed copy.
# Older builds could leave a stale package-version.txt beside a newly published EXE,
# which made the direct dist entry point report the previous release number.
$buildVersion | Set-Content -LiteralPath (Join-Path $out 'package-version.txt') -Encoding ASCII

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

# Synchronize all existing launch entries after publish AND self-test succeed.
# Taskbar pins are separate .lnk files; updating only Desktop leaves old versions pinned.
$dataRoot = Join-Path (Split-Path -Parent $root) 'codex-account-manager'
if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'accounts.json') -PathType Leaf)) {
    $dataRoot = $root
}
. (Join-Path $root 'packaging\installer\Sync-ManagerShortcuts.ps1')
& (Join-Path $root 'tools\Test-ManagerShortcuts.ps1')
Sync-ManagerShortcuts -ExecutablePath $appExe -ManagerRoot $dataRoot -SourceRoot $root |
    ForEach-Object { Write-Output "Updated shortcut: $_" }
