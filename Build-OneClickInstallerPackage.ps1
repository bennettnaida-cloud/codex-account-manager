param([string]$OutputPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$defaultAppExe = Join-Path $root 'dist\CodexAccountManager\CodexAccountManager.exe'
$appExe = if ([string]::IsNullOrWhiteSpace($env:CODEX_ACCOUNT_MANAGER_APP_EXE)) {
    $defaultAppExe
}
else {
    [IO.Path]::GetFullPath($env:CODEX_ACCOUNT_MANAGER_APP_EXE)
}
$appRuntimeRoot = Split-Path -Parent $appExe
$dreamSkinRuntime = if ([string]::IsNullOrWhiteSpace($env:CODEX_ACCOUNT_MANAGER_DREAM_SKIN_RUNTIME)) {
    Join-Path $appRuntimeRoot 'CodexDreamSkin'
}
else {
    [IO.Path]::GetFullPath($env:CODEX_ACCOUNT_MANAGER_DREAM_SKIN_RUNTIME)
}
$assetsRoot = Join-Path $root 'assets'
$defaultsRoot = Join-Path $root 'packaging\defaults'
$installerRoot = Join-Path $root 'packaging\installer'
$codexRuntime = if ([string]::IsNullOrWhiteSpace($env:CODEX_ACCOUNT_MANAGER_CODEX_RUNTIME)) {
    Join-Path $root '.tools\codex-cli\node_modules'
}
else {
    [IO.Path]::GetFullPath($env:CODEX_ACCOUNT_MANAGER_CODEX_RUNTIME)
}
$stamp = Get-Date -Format 'yyyyMMdd'
$displayVersion = if ([string]::IsNullOrWhiteSpace($env:CAM_VERSION)) {
    Get-Date -Format 'yyyy.MM.dd'
}
else {
    $env:CAM_VERSION.Trim()
}
$useDefaultOutputPath = [string]::IsNullOrWhiteSpace($OutputPath)
if ($useDefaultOutputPath) {
    $OutputPath = Join-Path (Split-Path -Parent $root) "CodexAccountManager-一键安装版-$stamp.zip"
}
$companionReadmePath = if ($useDefaultOutputPath) {
    Join-Path (Split-Path -Parent $root) 'CodexAccountManager-安装与使用说明.md'
}
else {
    $null
}
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("cami-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$packageRoot = Join-Path $stagingRoot 'CodexAccountManager-Setup'
$testInstallRoot = Join-Path $stagingRoot '隔离 安装 测试'
$testInstallerLog = Join-Path $stagingRoot '安装 日志\isolated-install.log'
$temporaryArchivePath = $null
$replacementBackupPath = $null

function Assert-CleanAppSettingsContent {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Context
    )

    try {
        $settings = $Content | ConvertFrom-Json
    }
    catch {
        throw "$Context is not valid JSON: $($_.Exception.Message)"
    }

    $requiredSettingNames = @(
        'ThemeMode',
        'CurrentAccountName',
        'ProjectPath',
        'PatGatewayProxy',
        'PatGatewayProxyAddress',
        'PatGatewayProxyPort',
        'PatGatewayProxyAutoDetect',
        'PatGatewayProxyScheme',
        'WindowWidth',
        'WindowHeight',
        'UseCodexDreamSkin',
        'CodexAppearancePresetId',
        'CustomCodexTheme'
    )
    $allowedSettingNames = @($requiredSettingNames + 'WindowsClientMode')
    $settingNames = @($settings.PSObject.Properties.Name)
    $missingSettingNames = @($requiredSettingNames | Where-Object { $_ -notin $settingNames })
    $unexpectedSettingNames = @($settingNames | Where-Object { $_ -notin $allowedSettingNames })
    if ($missingSettingNames.Count -gt 0 -or $unexpectedSettingNames.Count -gt 0) {
        throw ("{0} has a non-clean schema. Missing=[{1}] Unexpected=[{2}]" -f `
            $Context,
            ($missingSettingNames -join ', '),
            ($unexpectedSettingNames -join ', '))
    }

    $currentAccountName = $settings.PSObject.Properties['CurrentAccountName'].Value
    $projectPath = [string]$settings.PSObject.Properties['ProjectPath'].Value
    $patGatewayProxy = [string]$settings.PSObject.Properties['PatGatewayProxy'].Value
    $patGatewayProxyAddress = [string]$settings.PSObject.Properties['PatGatewayProxyAddress'].Value
    $patGatewayProxyPort = $settings.PSObject.Properties['PatGatewayProxyPort'].Value
    $patGatewayProxyAutoDetect = [bool]$settings.PSObject.Properties['PatGatewayProxyAutoDetect'].Value
    $patGatewayProxyScheme = [string]$settings.PSObject.Properties['PatGatewayProxyScheme'].Value
    $windowWidth = [int]$settings.PSObject.Properties['WindowWidth'].Value
    $windowHeight = [int]$settings.PSObject.Properties['WindowHeight'].Value
    $useCodexDreamSkin = [bool]$settings.PSObject.Properties['UseCodexDreamSkin'].Value
    $codexAppearancePresetId = [string]$settings.PSObject.Properties['CodexAppearancePresetId'].Value
    if ($null -ne $currentAccountName -or
        -not [string]::IsNullOrWhiteSpace($projectPath) -or
        -not [string]::IsNullOrWhiteSpace($patGatewayProxy) -or
        $patGatewayProxyAddress -ne '127.0.0.1' -or
        $null -ne $patGatewayProxyPort -or
        -not $patGatewayProxyAutoDetect -or
        $patGatewayProxyScheme -ne 'http' -or
        $windowWidth -ne 1038 -or
        $windowHeight -ne 615 -or
        $useCodexDreamSkin -or
        $codexAppearancePresetId -ne 'preset-midnight-aurora') {
        throw "$Context must be account-free, path-free, use loopback-only automatic proxy detection, use the clean 1038x615 first-run window, and keep the Codex theme disabled."
    }
}

function Assert-CleanAppSettingsFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Context
    )

    Assert-CleanAppSettingsContent `
        -Content ([IO.File]::ReadAllText($Path)) `
        -Context $Context
}

function Test-IsDeterminableTextFile {
    param([Parameter(Mandatory = $true)][IO.FileInfo]$File)

    $knownTextExtensions = @(
        '.json', '.md', '.txt', '.cmd', '.bat', '.ps1', '.psm1', '.psd1',
        '.js', '.mjs', '.cjs', '.jsx', '.ts', '.tsx', '.map', '.css', '.scss',
        '.html', '.htm', '.xml', '.toml', '.yaml', '.yml', '.ini', '.conf',
        '.config', '.properties', '.lock', '.sh', '.zsh', '.fish', '.sql',
        '.csv', '.svg', '.cs', '.csproj', '.props', '.targets', '.resx'
    )
    if ($File.Extension -in $knownTextExtensions -or $File.Name -match '^(?:LICENSE|NOTICE|README|CHANGELOG)(?:\..*)?$') {
        return $true
    }

    $knownBinaryExtensions = @(
        '.exe', '.dll', '.node', '.wasm', '.pdb', '.zip', '.gz', '.br', '.7z',
        '.png', '.ico', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.woff',
        '.woff2', '.ttf', '.otf', '.eot', '.pdf', '.sqlite', '.db', '.dat', '.bin'
    )
    if ($File.Extension -in $knownBinaryExtensions) {
        return $false
    }
    if ($File.Length -eq 0) {
        return $true
    }

    $stream = $null
    try {
        $stream = [IO.File]::Open($File.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $sampleLength = [int][Math]::Min(8192L, $File.Length)
        $buffer = New-Object byte[] $sampleLength
        $read = $stream.Read($buffer, 0, $sampleLength)
        if ($read -ge 2 -and
            (($buffer[0] -eq 0xFF -and $buffer[1] -eq 0xFE) -or
             ($buffer[0] -eq 0xFE -and $buffer[1] -eq 0xFF))) {
            return $true
        }
        if ($read -ge 3 -and $buffer[0] -eq 0xEF -and $buffer[1] -eq 0xBB -and $buffer[2] -eq 0xBF) {
            return $true
        }

        $controlCount = 0
        for ($index = 0; $index -lt $read; $index++) {
            $value = $buffer[$index]
            if ($value -eq 0) {
                return $false
            }
            if (($value -lt 9) -or ($value -gt 13 -and $value -lt 32)) {
                $controlCount++
            }
        }
        return $read -eq 0 -or (($controlCount / [double]$read) -le 0.02)
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Test-IsAllowedInstallerAbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Value)

    $normalized = $Value.Replace('\\', '\').Replace('/', '\').Trim()
    $normalized = $normalized.TrimEnd([char[]]@('.', ',', ';', ':', ')', ']', '}'))
    if ($normalized -match '(?i)^[A-Z]:\\Users\\(?!Public(?:\\|$)|Default(?:\\|$)|Default User(?:\\|$)|All Users(?:\\|$))[^\\]+(?:\\|$)') {
        return $false
    }
    if ($normalized -match '(?i)^[A-Z]:\\(?:Windows|Program Files(?: \(x86\))?|ProgramData|Temp)(?:\\|$)' -or
        $normalized -match '(?i)^[A-Z]:\\Users\\(?:Public|Default|Default User|All Users)(?:\\|$)' -or
        $normalized -match '(?i)^[A-Z]:\\(?:Apps\\)?CodexAccountManager(?:\\|$)' -or
        $normalized -match '(?i)^[A-Z]:\\(?:[^\\\r\n]+\\)*CodexAccountManager(?:\\|$)' -or
        $normalized -match '(?i)^[A-Z]:\\(?:path\\to|example|sample|tests?|testdata|foo|bar)(?:\\|$)' -or
        $normalized -match '(?i)%[A-Z0-9_]+%' -or
        $normalized -match '(?i)^[A-Z]:\\Users\\?$') {
        return $true
    }
    return $false
}

function Assert-CleanApplicationBinaryStrings {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Context
    )

    # The general text scan intentionally skips PE files. Scan only our application
    # executable here (not the signed third-party CLI/runtime binaries) for the two
    # private values that can realistically be baked into a local build: a personal
    # email address or an absolute Windows user-profile path. Restrict email suffixes
    # to common public domains so random PE byte sequences such as "x@Type.Object"
    # cannot become false positives.
    $personalEmailPattern = [regex]::new(
        '(?i)(?<![A-Z0-9._%+\-])[A-Z0-9._%+\-]{2,64}@(?<domain>[A-Z0-9\-]+(?:\.[A-Z0-9\-]+)*\.(?:com|net|org|cn|edu|gov|io|ai|me|co|xyz|top|tech|dev|app|info|biz|pro|club|cc|tv|us|uk|de|fr|jp|kr|au|ca|ru|in|br|nl|it|es|se|no|fi|pl|ch|at|be|nz|sg|hk|tw))(?![A-Z0-9\-])',
        [Text.RegularExpressions.RegexOptions]::Compiled)
    $userProfilePathPattern = [regex]::new(
        '(?i)(?<![A-Z0-9_])(?<path>[A-Z]:\\Users\\(?<profile>[^\\\x00\r\n\t"''<>|?*]{1,80})(?:\\[^\\\x00\r\n\t"''<>|?*]{1,160})?)',
        [Text.RegularExpressions.RegexOptions]::Compiled)
    $utf16LeAsciiRunPattern = [regex]::new(
        '(?:[\x20-\x7E]\x00){4,}',
        [Text.RegularExpressions.RegexOptions]::Compiled)
    $allowedSystemProfiles = @(
        'Public',
        'Default',
        'Default User',
        'All Users',
        # High-DPI dialog validation embeds this synthetic, non-user fixture.
        'layout'
    )
    $allowedBinaryEmailDomains = @(
        'example.com',
        'example.net',
        'example.org',
        'microsoft.com',
        'dot.net',
        'github.com',
        'nuget.org',
        'sqlite.org',
        'xamarin.com',
        'openai.com'
    )
    $inspectText = {
        param([string]$Text)

        foreach ($emailMatch in $personalEmailPattern.Matches($Text)) {
            $emailDomain = $emailMatch.Groups['domain'].Value.ToLowerInvariant()
            if ($emailDomain -notin $allowedBinaryEmailDomains) {
                throw "$Context contains a possible personal email address in the application binary."
            }
        }
        foreach ($pathMatch in $userProfilePathPattern.Matches($Text)) {
            $profileName = $pathMatch.Groups['profile'].Value.TrimEnd(
                [char[]]@('.', ',', ';', ':', ')', ']', '}'))
            if ($profileName -notin $allowedSystemProfiles) {
                throw "$Context contains a non-system absolute Windows user-profile path in the application binary."
            }
        }
    }

    $stream = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $buffer = New-Object byte[] (1024 * 1024)
        $tail = New-Object byte[] 0
        $latin1 = [Text.Encoding]::GetEncoding(28591)
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $combined = New-Object byte[] ($tail.Length + $read)
            if ($tail.Length -gt 0) {
                [Array]::Copy($tail, 0, $combined, 0, $tail.Length)
            }
            [Array]::Copy($buffer, 0, $combined, $tail.Length, $read)

            $asciiText = $latin1.GetString($combined)
            & $inspectText $asciiText
            foreach ($utf16Match in $utf16LeAsciiRunPattern.Matches($asciiText)) {
                & $inspectText ([Text.Encoding]::Unicode.GetString(
                    $combined,
                    $utf16Match.Index,
                    $utf16Match.Length))
            }

            # Preserve enough overlap for a long email/path split across chunks. The
            # even byte count also keeps UTF-16LE decoding aligned with the PE origin.
            $tailLength = [Math]::Min(4096, $combined.Length)
            $tail = New-Object byte[] $tailLength
            [Array]::Copy($combined, $combined.Length - $tailLength, $tail, 0, $tailLength)
        }
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

foreach ($required in @(
    $appExe,
    $dreamSkinRuntime,
    (Join-Path $assetsRoot 'CodexAccountManager.ico'),
    (Join-Path $assetsRoot 'CodexAccountManager.png'),
    (Join-Path $defaultsRoot 'accounts.json'),
    (Join-Path $defaultsRoot 'appsettings.json'),
    (Join-Path $defaultsRoot 'token-metadata.json'),
    (Join-Path $defaultsRoot 'usage-account-switches.json'),
    (Join-Path $installerRoot 'Install-CodexAccountManager.ps1'),
    (Join-Path $installerRoot 'Uninstall-CodexAccountManager.ps1'),
    (Join-Path $installerRoot '一键安装 Codex Account Manager.cmd'),
    (Join-Path $installerRoot '卸载 Codex Account Manager.cmd'),
    (Join-Path $installerRoot 'README.md'),
    (Join-Path $installerRoot '安装说明.txt'),
    $codexRuntime
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Missing one-click installer input: $required"
    }
}

$sourceFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Force -File |
        Where-Object {
            $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' -and
            $_.Extension -in @('.cs', '.csproj', '.resx', '.props', '.targets')
        }
    Get-ChildItem -LiteralPath (Join-Path $root 'tools\CodexDreamSkin') -Recurse -Force -File
)
if ($sourceFiles.Count -eq 0) {
    throw 'No application source files were found for the distribution freshness check.'
}
$newestSource = $sourceFiles | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$distributionExe = Get-Item -LiteralPath $appExe
if ($distributionExe.LastWriteTimeUtc -lt $newestSource.LastWriteTimeUtc) {
    throw ("Distribution is stale. Rebuild dist before packaging. Dist={0:o}; NewestSource={1:o} ({2})" -f `
        $distributionExe.LastWriteTimeUtc,
        $newestSource.LastWriteTimeUtc,
        $newestSource.FullName)
}
$distributionHashAtStart = (Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash

try {
    $payloadRoot = Join-Path $packageRoot 'payload'
    $packageAssets = Join-Path $payloadRoot 'assets'
    $packageDefaults = Join-Path $packageRoot 'defaults'
    $portableCliRoot = Join-Path $payloadRoot '.tools\codex-cli'
    New-Item -ItemType Directory -Force -Path $packageAssets, $packageDefaults, $portableCliRoot | Out-Null

    Copy-Item -LiteralPath $appExe -Destination (Join-Path $payloadRoot 'CodexAccountManager.exe') -Force
    Copy-Item -LiteralPath $dreamSkinRuntime -Destination (Join-Path $payloadRoot 'CodexDreamSkin') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $assetsRoot 'CodexAccountManager.ico') -Destination $packageAssets -Force
    Copy-Item -LiteralPath (Join-Path $assetsRoot 'CodexAccountManager.png') -Destination $packageAssets -Force
    Copy-Item -LiteralPath $codexRuntime -Destination $portableCliRoot -Recurse -Force

    foreach ($defaultName in @(
        'accounts.json',
        'appsettings.json',
        'token-metadata.json',
        'usage-account-switches.json'
    )) {
        Copy-Item -LiteralPath (Join-Path $defaultsRoot $defaultName) -Destination $packageDefaults -Force
    }
    foreach ($installerName in @(
        'Install-CodexAccountManager.ps1',
        'Uninstall-CodexAccountManager.ps1',
        '一键安装 Codex Account Manager.cmd',
        '卸载 Codex Account Manager.cmd',
        'README.md',
        '安装说明.txt'
    )) {
        Copy-Item -LiteralPath (Join-Path $installerRoot $installerName) -Destination $packageRoot -Force
    }
    [IO.File]::WriteAllText(
        (Join-Path $packageRoot 'package-version.txt'),
        $displayVersion,
        [Text.UTF8Encoding]::new($false))
    $packageAppRelativePath = 'payload\CodexAccountManager.exe'
    $packageCliRelativePath = 'payload\.tools\codex-cli\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe'
    $packageDreamSkinRelativePath = 'payload\CodexDreamSkin\bundle-version.txt'
    $packageAppHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot $packageAppRelativePath) -Algorithm SHA256).Hash
    if (-not $packageAppHash.Equals($distributionHashAtStart, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staged application hash differs from the distribution that passed the freshness check.'
    }
    Assert-CleanApplicationBinaryStrings `
        -Path (Join-Path $packageRoot $packageAppRelativePath) `
        -Context 'Staged CodexAccountManager.exe'
    $hashLines = @(
        ('{0}  {1}' -f $packageAppHash, $packageAppRelativePath),
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $packageRoot $packageCliRelativePath) -Algorithm SHA256).Hash, $packageCliRelativePath),
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $packageRoot $packageDreamSkinRelativePath) -Algorithm SHA256).Hash, $packageDreamSkinRelativePath),
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $packageRoot 'payload\CodexDreamSkin\assets\renderer-inject.js') -Algorithm SHA256).Hash, 'payload\CodexDreamSkin\assets\renderer-inject.js'),
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $packageRoot 'payload\CodexDreamSkin\assets\dream-skin.css') -Algorithm SHA256).Hash, 'payload\CodexDreamSkin\assets\dream-skin.css'),
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $packageRoot 'payload\CodexDreamSkin\assets\account-manager-aurora-light.jpg') -Algorithm SHA256).Hash, 'payload\CodexDreamSkin\assets\account-manager-aurora-light.jpg'),
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $packageRoot 'payload\CodexDreamSkin\assets\account-manager-porcelain-light.jpg') -Algorithm SHA256).Hash, 'payload\CodexDreamSkin\assets\account-manager-porcelain-light.jpg'),
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $packageRoot 'payload\CodexDreamSkin\assets\account-manager-deep-sea.jpg') -Algorithm SHA256).Hash, 'payload\CodexDreamSkin\assets\account-manager-deep-sea.jpg'),
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $packageRoot 'payload\CodexDreamSkin\assets\account-manager-nebula-orbit.jpg') -Algorithm SHA256).Hash, 'payload\CodexDreamSkin\assets\account-manager-nebula-orbit.jpg'),
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $packageRoot 'payload\CodexDreamSkin\scripts\set-account-manager-dream-theme.ps1') -Algorithm SHA256).Hash, 'payload\CodexDreamSkin\scripts\set-account-manager-dream-theme.ps1')
    )
    [IO.File]::WriteAllLines(
        (Join-Path $packageRoot 'SHA256SUMS.txt'),
        $hashLines,
        [Text.UTF8Encoding]::new($false))

    $forbiddenFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File |
        Where-Object {
            $_.Name -match '^(auth\.json|\.cockpit_codex_auth\.json|config\.toml|history\.jsonl|quota-capacity-measurements\.json|quota-probe-usage\.json|quota-monitor-settings\.json|quota-dollar-calibration\.json|usage-file-index-v1\.json|\.codex-global-state\.json|\.codex-account-manager-api-preflight\.json|models_cache\.json|codex-account-manager-usage-switches\.json|account-manager-deleted-threads\.json|shared-history-merge\.json|backup-manifest\.json|latest-status\.json|codex-plus-plus-launch(?:-result)?\.json|codex-plus-plus-launch-diagnostics\.log|state_.*\.sqlite.*)$' -or
            $_.Name -match '^(?:\.env(?:\..*)?|credentials?\.json|cookies?\.json)$' -or
            $_.Name -match '\.sqlite(?:$|[-.])' -or
            $_.Extension -eq '.jsonl' -or
            $_.Extension -in @('.pem', '.key') -or
            $_.Extension -in @('.pdb', '.log', '.tmp', '.bak', '.lnk')
        }
    if ($forbiddenFiles) {
        throw "Installer contains private/history/debug files: $($forbiddenFiles.FullName -join ', ')"
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
        throw "Installer contains chat-history directories: $($forbiddenDirectories.FullName -join ', ')"
    }

    if ((Get-Content -LiteralPath (Join-Path $packageDefaults 'accounts.json') -Raw).Trim() -ne '[]' -or
        (Get-Content -LiteralPath (Join-Path $packageDefaults 'token-metadata.json') -Raw).Trim() -ne '{}' -or
        (Get-Content -LiteralPath (Join-Path $packageDefaults 'usage-account-switches.json') -Raw).Trim() -ne '[]') {
        throw 'Installer default account and usage files are not empty.'
    }
    Assert-CleanAppSettingsFile `
        -Path (Join-Path $packageDefaults 'appsettings.json') `
        -Context 'Installer default appsettings.json'

    # Credential signatures are deliberately scanned in every determinable text file,
    # including the bundled node_modules runtime. Do not exempt third-party files here.
    $credentialPattern = [regex]::new(
        '(?i)(?:\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_\-]{20,}\b|\bBearer[ \t]+[A-Za-z0-9._~+\/\-]{8,}={0,2}\b|\beyJ[A-Za-z0-9_\-]{5,}\.eyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{8,}\b|-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----)')
    $quotedSecretFieldPattern = [regex]::new(
        '(?i)["'']?(?:access_token|refresh_token|id_token|api_key)["'']?\s*[:=]\s*(?:"(?<double>(?:\\.|[^"\\])*)"|''(?<single>(?:\\.|[^''\\])*)'')')
    $unquotedSecretFieldPattern = [regex]::new(
        '(?im)^\s*["'']?(?:access_token|refresh_token|id_token|api_key)["'']?\s*[:=]\s*(?<value>[^"''\s#;,}\]]+)')
    $absoluteDrivePathPattern = [regex]::new(
        '(?i)(?<![A-Z0-9_])(?<path>[A-Z]:(?:\\{1,2}|\/)[^\r\n\t"''<>|?*]+)')
    $emailPattern = [regex]::new(
        '(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b')
    $knownPrivateFragments = @(
        [regex]::Escape([IO.Path]::GetFullPath($root)),
        '(?i:PycharmProjects|wxid_|xwechat)'
    )
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $knownPrivateFragments += [regex]::Escape([IO.Path]::GetFullPath($env:USERPROFILE))
    }
    $knownPrivatePattern = [regex]::new('(?i)(?:' + ($knownPrivateFragments -join '|') + ')')

    $textFiles = @(
        Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File |
            Where-Object { Test-IsDeterminableTextFile -File $_ }
    )
    foreach ($file in $textFiles) {
        try {
            $content = [IO.File]::ReadAllText($file.FullName)
        }
        catch {
            throw "Unable to complete privacy scan for determinable text file $($file.FullName): $($_.Exception.Message)"
        }

        if ($credentialPattern.IsMatch($content)) {
            throw "Installer contains a JWT, sk- token, Bearer credential, or private key: $($file.FullName)"
        }

        foreach ($quotedSecretFieldMatch in $quotedSecretFieldPattern.Matches($content)) {
            $secretFieldValue = if ($quotedSecretFieldMatch.Groups['double'].Success) {
                $quotedSecretFieldMatch.Groups['double'].Value
            }
            else {
                $quotedSecretFieldMatch.Groups['single'].Value
            }
            if (-not [string]::IsNullOrWhiteSpace($secretFieldValue)) {
                throw "Installer contains a non-empty access_token/refresh_token/id_token/api_key field: $($file.FullName)"
            }
        }

        $isStructuredSettingsFile =
            $file.Extension -in @('.json', '.yaml', '.yml', '.toml', '.ini', '.conf', '.config', '.properties') -or
            $file.Name -match '^\.env(?:\..*)?$'
        if ($isStructuredSettingsFile) {
            foreach ($unquotedSecretFieldMatch in $unquotedSecretFieldPattern.Matches($content)) {
                $unquotedSecretFieldValue = $unquotedSecretFieldMatch.Groups['value'].Value.Trim()
                if ($unquotedSecretFieldValue -notin @('null', 'none', '~', '$null')) {
                    throw "Installer contains a non-empty unquoted credential field: $($file.FullName)"
                }
            }
        }

        foreach ($pathMatch in $absoluteDrivePathPattern.Matches($content)) {
            $absolutePath = $pathMatch.Groups['path'].Value
            if (-not (Test-IsAllowedInstallerAbsolutePath -Value $absolutePath)) {
                throw "Installer contains a non-system absolute local path: $($file.FullName)"
            }
        }

        if ($knownPrivatePattern.IsMatch($content)) {
            throw "Installer contains a private local identifier: $($file.FullName)"
        }
        if (-not $file.FullName.StartsWith($portableCliRoot, [StringComparison]::OrdinalIgnoreCase) -and
            $emailPattern.IsMatch($content)) {
            throw "Installer contains a possible personal email address: $($file.FullName)"
        }
    }

    $installerEntryPath = Join-Path $packageRoot '一键安装 Codex Account Manager.cmd'
    $oldSetupNoWait = $env:CAM_SETUP_NO_WAIT
    try {
        $env:CAM_SETUP_NO_WAIT = '1'
        $installerCommandLine = (
            '"{0}" -InstallPath "{1}" -LogPath "{2}" -NoLaunch -NoShortcuts -NoRegistry -Quiet' -f
            $installerEntryPath,
            $testInstallRoot,
            $testInstallerLog)
        $installerTest = Start-Process `
            -FilePath $env:ComSpec `
            -ArgumentList @('/d', '/s', '/c', ('"' + $installerCommandLine + '"')) `
            -WindowStyle Hidden `
            -Wait `
            -PassThru
        if ($installerTest.ExitCode -ne 0) {
            throw "Isolated one-click installer test failed with exit code $($installerTest.ExitCode)"
        }
    }
    finally {
        $env:CAM_SETUP_NO_WAIT = $oldSetupNoWait
    }
    if (-not (Test-Path -LiteralPath $testInstallerLog -PathType Leaf)) {
        throw "Isolated one-click installer did not preserve its log: $testInstallerLog"
    }
    $isolatedInstallerLogContent = Get-Content -LiteralPath $testInstallerLog -Raw
    if ($isolatedInstallerLogContent -notmatch '\[\s*100%\].*安装完成') {
        throw 'Isolated one-click installer log did not record successful completion.'
    }
    foreach ($installedRequired in @(
        (Join-Path $testInstallRoot 'CodexAccountManager.exe'),
        (Join-Path $testInstallRoot 'assets\CodexAccountManager.ico'),
        (Join-Path $testInstallRoot '.tools\codex-cli\node_modules'),
        (Join-Path $testInstallRoot 'CodexDreamSkin\bundle-version.txt'),
        (Join-Path $testInstallRoot 'CodexDreamSkin\assets\renderer-inject.js'),
        (Join-Path $testInstallRoot 'CodexDreamSkin\assets\dream-skin.css'),
        (Join-Path $testInstallRoot 'CodexDreamSkin\assets\account-manager-aurora-light.jpg'),
        (Join-Path $testInstallRoot 'CodexDreamSkin\assets\account-manager-porcelain-light.jpg'),
        (Join-Path $testInstallRoot 'CodexDreamSkin\assets\account-manager-deep-sea.jpg'),
        (Join-Path $testInstallRoot 'CodexDreamSkin\assets\account-manager-nebula-orbit.jpg'),
        (Join-Path $testInstallRoot 'CodexDreamSkin\scripts\renderer-motion-self-test.mjs'),
        (Join-Path $testInstallRoot 'CodexDreamSkin\scripts\renderer-motion-browser-self-test.mjs'),
        (Join-Path $testInstallRoot 'accounts.json'),
        (Join-Path $testInstallRoot 'appsettings.json'),
        (Join-Path $testInstallRoot 'README.md')
    )) {
        if (-not (Test-Path -LiteralPath $installedRequired)) {
            throw "Isolated installer did not create: $installedRequired"
        }
    }
    if ((Get-Content -LiteralPath (Join-Path $testInstallRoot 'accounts.json') -Raw).Trim() -ne '[]') {
        throw 'Isolated installer did not preserve clean account defaults.'
    }
    Assert-CleanAppSettingsFile `
        -Path (Join-Path $testInstallRoot 'appsettings.json') `
        -Context 'Isolated installed appsettings.json'

    $selfTestHome = Join-Path $stagingRoot 'self-test-account'
    New-Item -ItemType Directory -Force -Path $selfTestHome | Out-Null
    $testAccounts = @(
        [pscustomobject]@{ name = 'installer-self-test'; codexHome = $selfTestHome }
    )
    [IO.File]::WriteAllText(
        (Join-Path $testInstallRoot 'accounts.json'),
        (ConvertTo-Json -InputObject $testAccounts -Depth 5),
        [Text.UTF8Encoding]::new($false))
    $oldManagerHome = $env:CODEX_ACCOUNT_MANAGER_HOME
    $stdout = Join-Path $stagingRoot 'self-test.stdout.txt'
    $stderr = Join-Path $stagingRoot 'self-test.stderr.txt'
    try {
        $env:CODEX_ACCOUNT_MANAGER_HOME = $testInstallRoot
        $selfTest = Start-Process `
            -FilePath (Join-Path $testInstallRoot 'CodexAccountManager.exe') `
            -ArgumentList '--self-test' `
            -WindowStyle Hidden `
            -Wait `
            -PassThru `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr
        $selfTestOutput =
            (Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue) +
            (Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue)
        if ($selfTest.ExitCode -ne 0 -or $selfTestOutput -notmatch 'Self test passed') {
            throw "Installed application self-test failed: $selfTestOutput"
        }
    }
    finally {
        $env:CODEX_ACCOUNT_MANAGER_HOME = $oldManagerHome
    }

    $sourceFilesAtPublish = @(
        Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Force -File |
            Where-Object {
                $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' -and
                $_.Extension -in @('.cs', '.csproj', '.resx', '.props', '.targets')
            }
        Get-ChildItem -LiteralPath (Join-Path $root 'tools\CodexDreamSkin') -Recurse -Force -File
    )
    if ($sourceFilesAtPublish.Count -eq 0) {
        throw 'Application source disappeared during installer assembly.'
    }
    $newestSourceAtPublish = $sourceFilesAtPublish | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $distributionAtPublish = Get-Item -LiteralPath $appExe
    if ($distributionAtPublish.LastWriteTimeUtc -lt $newestSourceAtPublish.LastWriteTimeUtc) {
        throw 'Application source changed after the initial distribution freshness check. Rebuild and package again.'
    }
    $distributionHashAtPublish = (Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash
    if (-not $distributionHashAtPublish.Equals($distributionHashAtStart, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Distribution changed while the installer was being assembled. Package again from one stable build.'
    }

    $outputDirectory = Split-Path -Parent $outputFullPath
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $temporaryArchivePath = Join-Path $outputDirectory (
        '.{0}.{1}.tmp.zip' -f
        [IO.Path]::GetFileNameWithoutExtension($outputFullPath),
        [guid]::NewGuid().ToString('N'))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $packageRoot,
        $temporaryArchivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $true)

    $temporaryArchive = Get-Item -LiteralPath $temporaryArchivePath
    if ($temporaryArchive.Length -le 0) {
        throw 'Temporary installer ZIP is empty.'
    }

    $archivePrefix = [IO.Path]::GetFileName($packageRoot.TrimEnd('\')) -replace '\\', '/'
    $requiredArchiveEntries = @(
        "$archivePrefix/payload/CodexAccountManager.exe",
        "$archivePrefix/payload/assets/CodexAccountManager.ico",
        "$archivePrefix/payload/.tools/codex-cli/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe",
        "$archivePrefix/payload/CodexDreamSkin/bundle-version.txt",
        "$archivePrefix/payload/CodexDreamSkin/assets/account-manager-nebula.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/account-manager-aurora-light.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/account-manager-porcelain-light.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/account-manager-deep-sea.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/account-manager-nebula-orbit.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/renderer-inject.js",
        "$archivePrefix/payload/CodexDreamSkin/assets/dream-skin.css",
        "$archivePrefix/payload/CodexDreamSkin/scripts/renderer-motion-self-test.mjs",
        "$archivePrefix/payload/CodexDreamSkin/scripts/renderer-motion-browser-self-test.mjs",
        "$archivePrefix/payload/CodexDreamSkin/assets/UPSTREAM-PRESETS-NOTICE.md",
        "$archivePrefix/payload/CodexDreamSkin/assets/PRESET-PROVENANCE.md",
        "$archivePrefix/payload/CodexDreamSkin/scripts/set-account-manager-dream-theme.ps1",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-arina-hashimoto.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-arina-hashimoto-preview.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-arina-hashimoto/background.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-arina-hashimoto/theme.json",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-gothic-void-crusade.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-gothic-void-crusade-preview.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-gothic-void-crusade/background.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-gothic-void-crusade/theme.json",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-midnight-aurora/background.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-midnight-aurora/theme.json",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-sakura-dawn/background.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-sakura-dawn/theme.json",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-amber-dusk/background.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-amber-dusk/theme.json",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-forest-mist/background.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-forest-mist/theme.json",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-cyber-neon/background.jpg",
        "$archivePrefix/payload/CodexDreamSkin/assets/presets/preset-cyber-neon/theme.json",
        "$archivePrefix/defaults/accounts.json",
        "$archivePrefix/defaults/appsettings.json",
        "$archivePrefix/defaults/token-metadata.json",
        "$archivePrefix/defaults/usage-account-switches.json",
        "$archivePrefix/Install-CodexAccountManager.ps1",
        "$archivePrefix/README.md"
    )
    $zip = $null
    try {
        $zip = [IO.Compression.ZipFile]::OpenRead($temporaryArchivePath)
        if ($zip.Entries.Count -eq 0) {
            throw 'Temporary installer ZIP has no entries.'
        }
        $entriesByNormalizedPath = @{}
        foreach ($entry in $zip.Entries) {
            $entryPath = $entry.FullName -replace '\\', '/'
            if ($entryPath.StartsWith('/', [StringComparison]::Ordinal) -or
                $entryPath -match '(^|/)\.\.(/|$)' -or
                $entryPath -match '^[A-Z]:') {
                throw "Temporary installer ZIP contains an unsafe entry path: $entryPath"
            }
            if ($entriesByNormalizedPath.ContainsKey($entryPath)) {
                throw "Temporary installer ZIP contains duplicate normalized entry paths: $entryPath"
            }
            $entriesByNormalizedPath[$entryPath] = $entry
        }
        foreach ($requiredEntryName in $requiredArchiveEntries) {
            if (-not $entriesByNormalizedPath.ContainsKey($requiredEntryName)) {
                throw "Temporary installer ZIP is missing: $requiredEntryName"
            }
        }

        $archiveSettingsEntry = $entriesByNormalizedPath["$archivePrefix/defaults/appsettings.json"]
        $archiveSettingsStream = $null
        $archiveSettingsReader = $null
        try {
            $archiveSettingsStream = $archiveSettingsEntry.Open()
            $archiveSettingsReader = [IO.StreamReader]::new($archiveSettingsStream, [Text.Encoding]::UTF8, $true)
            Assert-CleanAppSettingsContent `
                -Content $archiveSettingsReader.ReadToEnd() `
                -Context 'Temporary ZIP appsettings.json'
        }
        finally {
            if ($null -ne $archiveSettingsReader) {
                $archiveSettingsReader.Dispose()
            }
            elseif ($null -ne $archiveSettingsStream) {
                $archiveSettingsStream.Dispose()
            }
        }

        $archiveAccountsEntry = $entriesByNormalizedPath["$archivePrefix/defaults/accounts.json"]
        $archiveAccountsStream = $null
        $archiveAccountsReader = $null
        try {
            $archiveAccountsStream = $archiveAccountsEntry.Open()
            $archiveAccountsReader = [IO.StreamReader]::new($archiveAccountsStream, [Text.Encoding]::UTF8, $true)
            if ($archiveAccountsReader.ReadToEnd().Trim() -ne '[]') {
                throw 'Temporary installer ZIP does not contain clean account defaults.'
            }
        }
        finally {
            if ($null -ne $archiveAccountsReader) {
                $archiveAccountsReader.Dispose()
            }
            elseif ($null -ne $archiveAccountsStream) {
                $archiveAccountsStream.Dispose()
            }
        }

        foreach ($emptyDefault in @(
            [pscustomobject]@{
                Path = "$archivePrefix/defaults/token-metadata.json"
                Expected = '{}'
            },
            [pscustomobject]@{
                Path = "$archivePrefix/defaults/usage-account-switches.json"
                Expected = '[]'
            }
        )) {
            $emptyDefaultEntry = $entriesByNormalizedPath[$emptyDefault.Path]
            $emptyDefaultStream = $null
            $emptyDefaultReader = $null
            try {
                $emptyDefaultStream = $emptyDefaultEntry.Open()
                $emptyDefaultReader = [IO.StreamReader]::new($emptyDefaultStream, [Text.Encoding]::UTF8, $true)
                if ($emptyDefaultReader.ReadToEnd().Trim() -ne $emptyDefault.Expected) {
                    throw "Temporary installer ZIP does not contain clean $($emptyDefault.Path)."
                }
            }
            finally {
                if ($null -ne $emptyDefaultReader) {
                    $emptyDefaultReader.Dispose()
                }
                elseif ($null -ne $emptyDefaultStream) {
                    $emptyDefaultStream.Dispose()
                }
            }
        }

        $archiveAppEntry = $entriesByNormalizedPath["$archivePrefix/payload/CodexAccountManager.exe"]
        $archiveAppStream = $null
        $sha256 = $null
        try {
            $archiveAppStream = $archiveAppEntry.Open()
            $sha256 = [Security.Cryptography.SHA256]::Create()
            $archiveAppHash = [BitConverter]::ToString($sha256.ComputeHash($archiveAppStream)).Replace('-', '')
            $expectedAppHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot $packageAppRelativePath) -Algorithm SHA256).Hash
            if (-not $archiveAppHash.Equals($expectedAppHash, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Temporary installer ZIP application hash does not match the staged payload.'
            }
        }
        finally {
            if ($null -ne $sha256) {
                $sha256.Dispose()
            }
            if ($null -ne $archiveAppStream) {
                $archiveAppStream.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $zip) {
            $zip.Dispose()
        }
    }

    $temporaryArchiveHash = (Get-FileHash -LiteralPath $temporaryArchivePath -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath $outputFullPath) {
        $replacementBackupPath = Join-Path $outputDirectory (
            '.{0}.{1}.replace-backup' -f
            [IO.Path]::GetFileName($outputFullPath),
            [guid]::NewGuid().ToString('N'))
        [IO.File]::Replace($temporaryArchivePath, $outputFullPath, $replacementBackupPath, $true)
        Remove-Item -LiteralPath $replacementBackupPath -Force
        $replacementBackupPath = $null
    }
    else {
        [IO.File]::Move($temporaryArchivePath, $outputFullPath)
    }
    $temporaryArchivePath = $null

    $publishedArchiveHash = (Get-FileHash -LiteralPath $outputFullPath -Algorithm SHA256).Hash
    if (-not $publishedArchiveHash.Equals($temporaryArchiveHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published installer ZIP hash changed during atomic replacement.'
    }

    if ($useDefaultOutputPath) {
        $defaultOutputDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $root)).TrimEnd('\')
        $actualOutputDirectory = [IO.Path]::GetFullPath($outputDirectory).TrimEnd('\')
        if (-not $actualOutputDirectory.Equals($defaultOutputDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean old default installer ZIPs outside the project parent directory.'
        }
        $oldInstallerArchives = @(
            Get-ChildItem -LiteralPath $defaultOutputDirectory -Force -File -Filter '*.zip' |
                Where-Object {
                    ($_.Name -match '(?i)^CodexAccountManager-(?!macOS-).*(?:一键安装|安装包|安装版|installer|setup).*\.zip$' -or
                     $_.Name -match '(?i)^CodexAccountManager-portable-clean-\d{8}\.zip$') -and
                    -not $_.FullName.Equals($outputFullPath, [StringComparison]::OrdinalIgnoreCase)
                }
        )
        foreach ($oldInstallerArchive in $oldInstallerArchives) {
            $oldArchiveDirectory = [IO.Path]::GetFullPath($oldInstallerArchive.DirectoryName).TrimEnd('\')
            if (-not $oldArchiveDirectory.Equals($defaultOutputDirectory, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove an old installer outside the default output directory: $($oldInstallerArchive.FullName)"
            }
            Remove-Item -LiteralPath $oldInstallerArchive.FullName -Force
        }

        # Keep one readable copy next to the ZIP for the owner to inspect or send
        # separately.  It is sourced from the exact README staged into this build,
        # so the external instructions cannot silently lag behind the installer.
        Copy-Item -LiteralPath (Join-Path $installerRoot 'README.md') `
            -Destination $companionReadmePath -Force
    }

    $archive = Get-Item -LiteralPath $outputFullPath
    $hash = $publishedArchiveHash
    Write-Output ("InstallerPackage={0}" -f $archive.FullName)
    Write-Output ("InstallerPackageBytes={0}" -f $archive.Length)
    Write-Output ("InstallerPackageSHA256={0}" -f $hash)
    if (-not [string]::IsNullOrWhiteSpace($companionReadmePath)) {
        Write-Output ("InstallerCompanionReadme={0}" -f $companionReadmePath)
    }
    Write-Output 'InstallerPrivacyScan=Passed'
    Write-Output 'InstallerIsolatedInstallTest=Passed'
    Write-Output 'InstallerOneClickEntryTest=Passed'
    Write-Output 'InstallerProgressLogTest=Passed'
    Write-Output 'InstalledApplicationSelfTest=Passed'
    Write-Output 'InstallerTemporaryZipValidation=Passed'
    Write-Output 'InstallerAtomicPublish=Passed'
}
finally {
    if (-not [string]::IsNullOrWhiteSpace([string]$temporaryArchivePath) -and
        (Test-Path -LiteralPath $temporaryArchivePath)) {
        Remove-Item -LiteralPath $temporaryArchivePath -Force -ErrorAction SilentlyContinue
    }
    $tempFull = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $stagingFull = [IO.Path]::GetFullPath($stagingRoot)
    if ($stagingFull.StartsWith($tempFull, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $stagingFull)) {
        Remove-Item -LiteralPath $stagingFull -Recurse -Force -ErrorAction SilentlyContinue
    }
}
