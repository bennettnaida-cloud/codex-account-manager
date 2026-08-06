param(
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appExe = Join-Path $root 'dist\CodexAccountManager\CodexAccountManager.exe'
$dreamSkinRuntime = Join-Path $root 'dist\CodexAccountManager\CodexDreamSkin'
$assetsRoot = Join-Path $root 'assets'
$defaultsRoot = Join-Path $root 'packaging\defaults'
$codexRuntime = Join-Path $root '.tools\codex-cli\node_modules'
$stamp = Get-Date -Format 'yyyyMMdd'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path -Parent $root) "CodexAccountManager-portable-clean-$stamp.zip"
}
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("codex-account-manager-package-" + [guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stagingRoot 'CodexAccountManager'

foreach ($required in @(
    $appExe,
    $dreamSkinRuntime,
    (Join-Path $assetsRoot 'CodexAccountManager.ico'),
    (Join-Path $assetsRoot 'CodexAccountManager.png'),
    (Join-Path $assetsRoot 'model-catalog.json'),
    (Join-Path $defaultsRoot 'accounts.json'),
    (Join-Path $defaultsRoot 'appsettings.json'),
    (Join-Path $defaultsRoot 'token-metadata.json'),
    (Join-Path $defaultsRoot 'usage-account-switches.json'),
    (Join-Path $defaultsRoot 'README.md'),
    $codexRuntime
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Missing portable package input: $required"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
    Copy-Item -LiteralPath $appExe -Destination (Join-Path $packageRoot 'CodexAccountManager.exe') -Force
    Copy-Item -LiteralPath $dreamSkinRuntime -Destination (Join-Path $packageRoot 'CodexDreamSkin') -Recurse -Force
    $packageAssets = Join-Path $packageRoot 'assets'
    New-Item -ItemType Directory -Force -Path $packageAssets | Out-Null
    Copy-Item -LiteralPath (Join-Path $assetsRoot 'CodexAccountManager.ico') -Destination $packageAssets -Force
    Copy-Item -LiteralPath (Join-Path $assetsRoot 'CodexAccountManager.png') -Destination $packageAssets -Force
    Copy-Item -LiteralPath (Join-Path $assetsRoot 'model-catalog.json') -Destination $packageAssets -Force
    Copy-Item -LiteralPath (Join-Path $defaultsRoot 'accounts.json') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $defaultsRoot 'appsettings.json') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $defaultsRoot 'token-metadata.json') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $defaultsRoot 'usage-account-switches.json') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $defaultsRoot 'README.md') -Destination $packageRoot -Force

    $portableCliRoot = Join-Path $packageRoot '.tools\codex-cli'
    New-Item -ItemType Directory -Force -Path $portableCliRoot | Out-Null
    Copy-Item -LiteralPath $codexRuntime -Destination $portableCliRoot -Recurse -Force

    $forbiddenFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File |
        Where-Object {
            $_.Name -match '^(auth\.json|\.cockpit_codex_auth\.json|config\.toml|history\.jsonl|quota-capacity-measurements\.json|quota-probe-usage\.json|quota-monitor-settings\.json|usage-file-index-v1\.json|codex-plus-plus-launch(?:-result)?\.json|state_.*\.sqlite.*)$' -or
            $_.Name -match '\.sqlite(?:$|[-.])' -or
            $_.Extension -eq '.jsonl' -or
            $_.Extension -in @('.pdb', '.log', '.tmp', '.bak', '.lnk')
        }
    if ($forbiddenFiles) {
        throw "Portable package contains private/history/debug files: $($forbiddenFiles.FullName -join ', ')"
    }

    $forbiddenDirectories = Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -Directory |
        Where-Object {
            $_.Name -in @(
                'sessions',
                'archived_sessions',
                'history-backups',
                '.cache',
                '.codex',
                '.codex-accounts')
        }
    if ($forbiddenDirectories) {
        throw "Portable package contains chat-history directories: $($forbiddenDirectories.FullName -join ', ')"
    }

    if ((Get-Content -LiteralPath (Join-Path $packageRoot 'accounts.json') -Raw).Trim() -ne '[]' -or
        (Get-Content -LiteralPath (Join-Path $packageRoot 'token-metadata.json') -Raw).Trim() -ne '{}' -or
        (Get-Content -LiteralPath (Join-Path $packageRoot 'usage-account-switches.json') -Raw).Trim() -ne '[]') {
        throw 'Portable package default account and usage files are not empty.'
    }

    $portableSettings = Get-Content -LiteralPath (Join-Path $packageRoot 'appsettings.json') -Raw |
        ConvertFrom-Json
    if ($null -ne $portableSettings.CurrentAccountName -or
        -not [string]::IsNullOrWhiteSpace([string]$portableSettings.ProjectPath) -or
        -not [string]::IsNullOrWhiteSpace([string]$portableSettings.PatGatewayProxy) -or
        [string]$portableSettings.PatGatewayProxyAddress -ne '127.0.0.1' -or
        $null -ne $portableSettings.PatGatewayProxyPort -or
        -not [bool]$portableSettings.PatGatewayProxyAutoDetect -or
        [string]$portableSettings.PatGatewayProxyScheme -ne 'http') {
        throw 'Portable package app settings still contain an account/path/proxy override or disable loopback-only automatic proxy detection.'
    }

    $privatePattern = '(@(?:126|163)\.com|C:\\Users\\[^\\]+|D:\\GPT|PycharmProjects|wxid_|xwechat|86152|mathcau|mathuk)'
    $packageTextFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File |
        Where-Object {
            $_.Extension -in @(
                '.json', '.md', '.txt', '.cmd', '.ps1', '.js', '.mjs', '.cjs',
                '.ts', '.toml', '.yaml', '.yml', '.xml')
        }
    foreach ($file in $packageTextFiles) {
        if ((Get-Content -LiteralPath $file.FullName -Raw) -match $privatePattern) {
            throw "Portable package contains a private local identifier: $($file.FullName)"
        }
    }

    $outputDirectory = Split-Path -Parent $outputFullPath
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    if (Test-Path -LiteralPath $outputFullPath) {
        Remove-Item -LiteralPath $outputFullPath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $packageRoot,
        $outputFullPath,
        [IO.Compression.CompressionLevel]::Optimal,
        $true)

    $archive = Get-Item -LiteralPath $outputFullPath
    Write-Output ("PortablePackage={0}" -f $archive.FullName)
    Write-Output ("PortablePackageBytes={0}" -f $archive.Length)
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
