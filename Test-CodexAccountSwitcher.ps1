Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSDefaultParameterValues['Get-Content:Encoding'] = 'utf8'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appScript = Join-Path $root 'Start-CodexAccountSwitcher.ps1'
$desktopLauncher = Join-Path $root 'CodexAccountManager.cmd'
$selfContainedLauncher = Join-Path $root 'Start-CodexAccountManager.ps1'
$appIcon = Join-Path $root 'assets\CodexAccountManager.ico'
$defaultAppExe = Join-Path $root 'dist\CodexAccountManager\CodexAccountManager.exe'
$appExe = if ([string]::IsNullOrWhiteSpace($env:CODEX_ACCOUNT_MANAGER_APP_EXE)) {
    $defaultAppExe
}
else {
    [IO.Path]::GetFullPath($env:CODEX_ACCOUNT_MANAGER_APP_EXE)
}
$projectPath = Join-Path $root 'src\CodexAccountManager\CodexAccountManager.csproj'
$dotnetPath = Join-Path $root '.tools\dotnet\dotnet.exe'
$fakeJwt = 'eyJhbGciOiJub25lIn0.eyJleHAiOjE4OTM0NTYwMDB9.signature'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("codex-switcher-test-" + [guid]::NewGuid().ToString('N'))
$accountsFile = Join-Path $tempRoot 'accounts.json'
$oldAccountsFileOverride = $env:CODEX_SWITCHER_ACCOUNTS_FILE
$oldMetadataFileOverride = $env:CODEX_SWITCHER_TOKEN_METADATA_FILE
$oldAccountManagerHome = $env:CODEX_ACCOUNT_MANAGER_HOME
$oldCodexCommand = $env:CODEX_SWITCHER_CODEX_COMMAND
$oldSharedCodexHome = $env:CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME
$oldPatGatewayProxy = $env:CODEX_PAT_GATEWAY_PROXY
$oldSkipGatewayEnsure = $env:CODEX_SWITCHER_SKIP_GATEWAY_ENSURE

if (-not (Test-Path -LiteralPath $appScript)) {
    throw "Missing app script: $appScript"
}

if (-not (Test-Path -LiteralPath $desktopLauncher -PathType Leaf)) {
    throw "Missing Windows client launcher: $desktopLauncher"
}
if (-not (Test-Path -LiteralPath $selfContainedLauncher -PathType Leaf)) {
    throw "Missing self-contained Windows client launcher: $selfContainedLauncher"
}
if (-not (Test-Path -LiteralPath $appIcon -PathType Leaf)) {
    throw "Missing Windows client icon: $appIcon"
}
$iconBytes = [System.IO.File]::ReadAllBytes($appIcon)
$iconFrameCount = if ($iconBytes.Length -ge 6) { [BitConverter]::ToUInt16($iconBytes, 4) } else { 0 }
if ($iconFrameCount -lt 8) {
    throw 'Windows client icon must include a complete multi-size frame set for title bars, taskbars, and shortcuts.'
}
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
    throw "Missing Windows client executable: $appExe"
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Missing Windows client project: $projectPath"
}
if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw "Missing bundled dotnet host: $dotnetPath"
}

New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$tempHome1 = Join-Path $tempRoot 'acct-alpha'
$tempHome2 = Join-Path $tempRoot 'acct-beta'
$tempHome3 = Join-Path $tempRoot 'acct-gamma'
$sharedCodexHome = Join-Path $tempRoot 'shared-codex'
New-Item -ItemType Directory -Force -Path $tempHome1, $tempHome2, $tempHome3, $sharedCodexHome, (Join-Path $tempRoot 'assets') | Out-Null
'model = "gpt-5.6-terra"' | Set-Content -LiteralPath (Join-Path $tempHome1 'config.toml') -Encoding UTF8
'model = "gpt-5.6-terra"' | Set-Content -LiteralPath (Join-Path $tempHome2 'config.toml') -Encoding UTF8
'model = "gpt-5.6-terra"' | Set-Content -LiteralPath (Join-Path $tempHome3 'config.toml') -Encoding UTF8
Copy-Item -LiteralPath $appIcon -Destination (Join-Path $tempRoot 'assets\CodexAccountManager.ico') -Force
@(
    [pscustomobject]@{ name = 'alpha'; codexHome = $tempHome1 },
    [pscustomobject]@{ name = 'beta'; codexHome = $tempHome2 },
    [pscustomobject]@{ name = 'gamma'; codexHome = $tempHome3 }
) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $accountsFile -Encoding UTF8
$env:CODEX_SWITCHER_ACCOUNTS_FILE = $accountsFile
$env:CODEX_SWITCHER_TOKEN_METADATA_FILE = (Join-Path $tempRoot 'token-metadata.json')
$env:CODEX_ACCOUNT_MANAGER_HOME = $tempRoot
$env:CODEX_SWITCHER_CODEX_COMMAND = Join-Path $root '.tools\codex-cli\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe'
$env:CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME = $sharedCodexHome
$env:CODEX_SWITCHER_SKIP_GATEWAY_ENSURE = '1'
# The source-level script tests do not perform an upstream request. A syntactically valid
# loopback proxy keeps the gateway health check deterministic without changing the host proxy.
$env:CODEX_PAT_GATEWAY_PROXY = 'http://127.0.0.1:10809'

$accountDialogSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\AccountDialog.cs') -Raw -Encoding UTF8
$accountRecordSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\AccountRecord.cs') -Raw -Encoding UTF8
$appScriptSource = Get-Content -LiteralPath $appScript -Raw -Encoding UTF8
$desktopLauncherSource = Get-Content -LiteralPath $desktopLauncher -Raw -Encoding UTF8
$injectorSource = Get-Content -LiteralPath (Join-Path $root 'tools\CodexDreamSkin\scripts\injector.mjs') -Raw -Encoding UTF8
$rendererInjectSource = Get-Content -LiteralPath (Join-Path $root 'tools\CodexDreamSkin\assets\renderer-inject.js') -Raw -Encoding UTF8
$dreamSkinCssSource = Get-Content -LiteralPath (Join-Path $root 'tools\CodexDreamSkin\assets\dream-skin.css') -Raw -Encoding UTF8
$formSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\Form1.cs') -Raw -Encoding UTF8
$oauthLinkDialogSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\ChatGptOAuthLinkDialog.cs') -Raw -Encoding UTF8
$themeArtworkRendererSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\NebulaThemeArtworkRenderer.cs') -Raw -Encoding UTF8
$customCodexThemeDialogSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\CustomCodexThemeDialog.cs') -Raw -Encoding UTF8
$modernUiControlsSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\ModernUiControls.cs') -Raw -Encoding UTF8
$cliServiceSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\CodexCliService.cs') -Raw -Encoding UTF8
$localPatGatewaySource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\LocalPatGateway.cs') -Raw -Encoding UTF8
$localProxyDetectorSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\LocalProxyDetector.cs') -Raw -Encoding UTF8
$quotaSnapshotStoreSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\QuotaSnapshotStore.cs') -Raw -Encoding UTF8
$dreamSkinServiceSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\CodexDreamSkinService.cs') -Raw -Encoding UTF8
$programSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\Program.cs') -Raw -Encoding UTF8
$resetSessionSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\UsageLimitResetSession.cs') -Raw -Encoding UTF8
$probeUsageLedgerSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\ProbeUsageLedger.cs') -Raw -Encoding UTF8
$projectSource = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$usageTrackerSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\UsageTracker.cs') -Raw -Encoding UTF8
$passiveQuotaMonitorSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\PassiveQuotaMonitor.cs') -Raw -Encoding UTF8
$passiveQuotaMonitoringSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\PassiveQuotaMonitoringService.cs') -Raw -Encoding UTF8
$quotaDashboardControlsSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\QuotaDashboardControls.cs') -Raw -Encoding UTF8
$modelUsageDistributionSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\ModelUsageDistributionControl.cs') -Raw -Encoding UTF8
$settingsSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\AppSettings.cs') -Raw -Encoding UTF8
$accountStoreSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\AccountStore.cs') -Raw -Encoding UTF8
$historyServiceSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\SharedHistoryService.cs') -Raw -Encoding UTF8
$historyMergerSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\SharedHistoryMerger.cs') -Raw -Encoding UTF8
$threadTranscriptSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\SharedThreadTranscriptService.cs') -Raw -Encoding UTF8
$threadPreviewDialogSource = Get-Content -LiteralPath (Join-Path $root 'src\CodexAccountManager\ThreadPreviewDialog.cs') -Raw -Encoding UTF8
$buildScriptSource = Get-Content -LiteralPath (Join-Path $root 'Build-CodexAccountManager.ps1') -Raw -Encoding UTF8
$selfContainedLauncherSource = Get-Content -LiteralPath $selfContainedLauncher -Raw -Encoding UTF8
$installerDefaultsSource = Get-Content -LiteralPath (Join-Path $root 'packaging\defaults\appsettings.json') -Raw -Encoding UTF8

try {
    [xml]$projectXml = $projectSource
}
catch {
    throw "Windows client project is not valid XML: $($_.Exception.Message)"
}
$normalBuildUseAppHostNodes = @(
    $projectXml.SelectNodes('/Project/PropertyGroup/UseAppHost') |
        Where-Object {
            $_.InnerText.Trim().Equals('false', [StringComparison]::OrdinalIgnoreCase) -and
            $_.GetAttribute('Condition') -match '\$\(PublishSingleFile\)' -and
            $_.GetAttribute('Condition') -match '!=' -and
            $_.GetAttribute('Condition') -match '(?i)true'
        }
)
if ($normalBuildUseAppHostNodes.Count -ne 1) {
    throw 'Normal developer builds must set UseAppHost=false while leaving single-file publish able to create the real EXE.'
}

$ordinaryBuildOutput = Join-Path $tempRoot 'ordinary-build'
try {
    New-Item -ItemType Directory -Force -Path $ordinaryBuildOutput | Out-Null
    $ordinaryBuildLog = @(
        & $dotnetPath build $projectPath -c Release --no-restore --nologo -o $ordinaryBuildOutput 2>&1
    )
    $ordinaryBuildExitCode = $LASTEXITCODE
    if ($ordinaryBuildExitCode -ne 0) {
        throw "Normal developer build failed with exit code ${ordinaryBuildExitCode}: $($ordinaryBuildLog -join [Environment]::NewLine)"
    }

    $ordinaryBuildDll = Join-Path $ordinaryBuildOutput 'CodexAccountManager.dll'
    $ordinaryBuildExe = Join-Path $ordinaryBuildOutput 'CodexAccountManager.exe'
    if (-not (Test-Path -LiteralPath $ordinaryBuildDll -PathType Leaf) -or
        (Get-Item -LiteralPath $ordinaryBuildDll).Length -le 0) {
        throw 'Normal developer build must produce a non-empty CodexAccountManager.dll.'
    }
    if (Test-Path -LiteralPath $ordinaryBuildExe -PathType Leaf) {
        throw 'Normal developer build must not produce a misleading apphost CodexAccountManager.exe.'
    }
}
finally {
    if (Test-Path -LiteralPath $ordinaryBuildOutput) {
        $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot).TrimEnd('\')
        $resolvedBuildOutput = [IO.Path]::GetFullPath($ordinaryBuildOutput)
        if (-not $resolvedBuildOutput.StartsWith(
                $resolvedTempRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove ordinary-build output outside the test root: $resolvedBuildOutput"
        }
        Remove-Item -LiteralPath $resolvedBuildOutput -Recurse -Force
    }
}
if ($accountDialogSource -notmatch '_secretBox' -or
    $accountDialogSource -notmatch '_updateTokenButton' -or
    $accountDialogSource -notmatch 'new TokenDialog' -or
    $accountDialogSource -notmatch 'TextRenderer\.MeasureText' -or
    $accountDialogSource -notmatch '_heroCard\.Controls\.Add\(heading\)' -or
    $accountDialogSource -notmatch '_formCard\.Controls\.Add\(_nameShell\)' -or
    $accountDialogSource -notmatch '_secretNoteCard\.Controls\.Add\(_secretNote\)' -or
    $accountDialogSource -notmatch 'ValidateRuntimeScaledModeSwitch' -or
    $accountDialogSource -match 'ClientSize\s*=\s*new Size\(960,\s*isApi' -or
    $accountDialogSource -notmatch 'useTokenUpdateButton' -or
    $accountDialogSource -notmatch 'AccessTokenValue' -or
    $accountDialogSource -notmatch 'ApiKeyValue' -or
    $accountDialogSource -notmatch 'AccountAuthKind\.CompatibleApi' -or
    $accountDialogSource -notmatch 'UseSystemPasswordChar\s*=\s*true') {
    throw 'Edit account dialog must expose masked access token and compatible API fields.'
}
if ($accountRecordSource -notmatch 'OfficialOAuth\s*=\s*"official_oauth"' -or
    $accountRecordSource -notmatch 'IsOfficialOAuth' -or
    $accountRecordSource -notmatch '通过 ChatGPT 登录（官方）' -or
    $accountDialogSource -notmatch '通过 ChatGPT 登录（官方）' -or
    $accountDialogSource -notmatch '生成登录链接' -or
    $accountDialogSource -notmatch '不会自动打开浏览器' -or
    $accountDialogSource -notmatch '✓ 已登录' -or
    $accountDialogSource -notmatch 'OfficialOAuthCredentialSourcePath' -or
    $accountDialogSource -notmatch 'LoginWithChatGptDraftAsync' -or
    $accountDialogSource -notmatch '_saveButton\.Enabled\s*=\s*verified\s*&&\s*!_oauthLoginBusy' -or
    $accountDialogSource -notmatch 'IsOfficialOAuthSelected' -or
    $accountDialogSource -notmatch '_secretShell\.Visible\s*=\s*!isOAuth' -or
    $accountStoreSource -notmatch 'BuildOfficialOAuthConfig' -or
    $accountStoreSource -notmatch 'cli_auth_credentials_store\s*=\s*"file"' -or
    $accountStoreSource -notmatch 'forced_login_method\s*=\s*"chatgpt"' -or
    $accountStoreSource -notmatch 'officialOAuthCredentialSourcePath' -or
    $accountStoreSource -notmatch 'IsOfficialOAuthCredentialFile' -or
    $cliServiceSource -notmatch 'LoginWithChatGptAsync' -or
    $cliServiceSource -notmatch 'LoginWithChatGptDraftAsync' -or
    $cliServiceSource -notmatch 'ProjectOfficialOAuthAccount' -or
    $cliServiceSource -notmatch 'SyncStoredOfficialOAuthAuthToAccount' -or
    $cliServiceSource -notmatch 'startInfo\.Environment\.Remove\(variableName\)' -or
    $cliServiceSource -notmatch 'ArgumentList\.Add\("app-server"\)' -or
    $cliServiceSource -notmatch '"account/login/start"' -or
    $cliServiceSource -notmatch '"account/login/completed"' -or
    $cliServiceSource -notmatch '"account/login/cancel"' -or
    $cliServiceSource -notmatch '\["type"\]\s*=\s*"chatgpt"' -or
    $cliServiceSource -notmatch 'ResolveBundledCodexCliCommand' -or
    $cliServiceSource -notmatch 'OfficialOAuthLoginLock' -or
    $cliServiceSource -notmatch 'OfficialOAuthAuthorizationHost\s*=\s*"auth\.openai\.com"' -or
    $cliServiceSource -notmatch 'Kill\(entireProcessTree:\s*true\)' -or
    $cliServiceSource -match 'RunCodexAsync\(\s*"login"\s*,' -or
    $formSource -notmatch '通过 ChatGPT 登录' -or
    $formSource -notmatch 'LoginWithChatGptAsync' -or
    $formSource -notmatch '确认 ChatGPT 登录账号' -or
    $formSource -notmatch 'ChatGptOAuthLinkDialog' -or
    $formSource -notmatch 'Progress<ChatGptOAuthAuthorization>' -or
    $oauthLinkDialogSource -notmatch 'Clipboard\.SetText\(_loginUrl\)' -or
    $oauthLinkDialogSource -notmatch 'Clipboard\.Clear\(\)' -or
    $oauthLinkDialogSource -notmatch 'CancellationRequested' -or
    $oauthLinkDialogSource -notmatch '不会自动打开浏览器' -or
    $programSource -notmatch 'ValidateOfficialOAuthBrowserFlow\(\)' -or
    $programSource -notmatch 'ValidateOfficialOAuthProfileProjection\(\)') {
    throw 'Official ChatGPT login must copy an app-server OAuth URL, verify the callback, and block saving until the isolated credential is valid.'
}
$officialBrowserLoginFlow = [regex]::Match(
    $cliServiceSource,
    '(?s)private static async Task<CommandResult> RunOfficialBrowserAuthorizationAsync\(.*?(?=\r?\n\s*private static async Task WriteAppServerMessageAsync)')
if (-not $officialBrowserLoginFlow.Success -or
    $officialBrowserLoginFlow.Value -match 'Process\.Start\([^\)]*authUrl' -or
    $officialBrowserLoginFlow.Value -match 'StartIsolatedDeviceAuthorizationBrowser' -or
    $officialBrowserLoginFlow.Value -match 'ArgumentList\.Add\("--device-auth"\)' -or
    $officialBrowserLoginFlow.Value -notmatch 'progress\?\.Report\(new ChatGptOAuthAuthorization\(authUrl\)\)') {
    throw 'Official browser OAuth must return the URL to the UI without automatically opening a browser or falling back to device code.'
}
$addAccountFlow = [regex]::Match(
    $formSource,
    '(?s)private void AddAccount\(\).*?(?=\r?\n\s*private async Task EditAccountAsync)')
$editAccountFlow = [regex]::Match(
    $formSource,
    '(?s)private async Task EditAccountAsync\(AccountRecord account\).*?(?=\r?\n\s*private void ShowAccountSaveError)')
if (-not $addAccountFlow.Success -or
    -not $editAccountFlow.Success -or
    $addAccountFlow.Value -match 'LoginWithChatGptAsync\(' -or
    $editAccountFlow.Value -match 'LoginWithChatGptAsync\(' -or
    $addAccountFlow.Value -notmatch 'dialog\.OfficialOAuthCredentialSourcePath' -or
    $editAccountFlow.Value -notmatch 'dialog\.OfficialOAuthCredentialSourcePath' -or
    $addAccountFlow.Value -notmatch '✓ 已登录' -or
    $editAccountFlow.Value -notmatch '✓ 已登录') {
    throw 'New or converted OAuth accounts must commit only the credential verified inside the account dialog.'
}
if ($formSource -notmatch 'dialog\.AccessTokenValue' -or
    $formSource -notmatch 'LoginWithAccessTokenAsync') {
    throw 'Edit account flow must update the token when a new token is provided.'
}
if ($formSource -notmatch 'AccountRowMinWidth' -or
    $formSource -notmatch 'WorkspaceView\.AccountSwitch' -or
    $formSource -notmatch 'WorkspaceView\.StatusCheck' -or
    $formSource -match 'WorkspaceView\.TokenManagement' -or
    $formSource -notmatch 'WorkspaceView\.QuotaUsage' -or
    $formSource -notmatch '_accountLayout\.ColumnCount\s*=\s*1' -or
    $formSource -notmatch 'CreateAccountSwitchRow\(account,\s*workspaceWidth\)' -or
    $formSource -notmatch 'CreateAccountCard\(selectedAccount,\s*workspaceWidth\)' -or
    $formSource -notmatch 'CreateStatusTokenRow\(account,\s*workspaceWidth\)' -or
    $formSource -match '_tokenManageNavButton' -or
    $formSource -notmatch '"状态与凭据",\s*222,\s*WorkspaceView\.StatusCheck' -or
    $formSource -notmatch '"额度显示",\s*274,\s*WorkspaceView\.QuotaUsage' -or
    $formSource -notmatch '_showAccountDetail\s*=\s*true' -or
    $formSource -notmatch 'MakeBackIconButton' -or
    $formSource -notmatch 'SelectAccount\(string accountName\)') {
    throw 'Account workspace must use full-width row views, with account details opened only after selecting an account.'
}
if ($formSource -match '_cardsPanel\.BringToFront\(\)') {
    throw 'Cards panel must not cover the bottom status area.'
}
$accountSwitchRowLayout = [regex]::Match(
    $formSource,
    '(?s)private Control CreateAccountSwitchRow\(AccountRecord account,\s*int width\).*?(?=\r?\n\s*private Control CreateAccountSummary)')
if (-not $accountSwitchRowLayout.Success -or
    $accountSwitchRowLayout.Value -match 'account\.CodexHome|configReady|directoryReady|credentialState|API Key 已保存|Token 已记录|配置 \{|目录 \{' -or
    $accountSwitchRowLayout.Value -notmatch '"Codex\+\+ 启动"' -or
    $accountSwitchRowLayout.Value -notmatch '"Codex 启动"' -or
    $accountSwitchRowLayout.Value -notmatch 'MakeAccountStateBadge' -or
    $accountSwitchRowLayout.Value -notmatch 'Height\s*=\s*twoActionRows \? 168 : horizontal \? 112 : 118' -or
    $accountSwitchRowLayout.Value -notmatch 'UsesHorizontalAccountSwitchLayout\(width\)' -or
    $accountSwitchRowLayout.Value -notmatch 'UseCompatibleTextRendering\s*=\s*true' -or
    $accountSwitchRowLayout.Value -notmatch 'UseMnemonic\s*=\s*false') {
    throw 'Account-switch rows must show only identity, current state, and launch actions, without directory/configuration/credential metadata or clipped high-DPI text.'
}
$statusRowLayout = [regex]::Match(
    $formSource,
    '(?s)private Control CreateStatusRow\(AccountRecord account,\s*int width\).*?(?=\r?\n\s*private Control CreateStatusTokenRow)')
if (-not $statusRowLayout.Success -or
    $statusRowLayout.Value -match 'configReady|directoryReady|authReady|secretText|statusTextValue|var meta\s*=|配置 \{|目录 \{|Token 未知|Token 已记录' -or
    $statusRowLayout.Value -notmatch 'Height\s*=\s*104' -or
    $statusRowLayout.Value -notmatch 'GetStatusBadgeText\(status\)' -or
    $statusRowLayout.Value -notmatch 'status == null \? "尚未检查登录状态" : status\.Text' -or
    $statusRowLayout.Value -notmatch 'MakeStatusCheckButton\(' -or
    $statusRowLayout.Value -notmatch 'UseCompatibleTextRendering\s*=\s*true' -or
    $statusRowLayout.Value -notmatch 'UseMnemonic\s*=\s*false') {
    throw 'Status rows must show only account identity, one concise login-state badge, and the check action; local config/directory/token metadata must stay out of the list.'
}
$statusTokenRowLayout = [regex]::Match(
    $formSource,
    '(?s)private Control CreateStatusTokenRow\(AccountRecord account,\s*int width\).*?(?=\r?\n\s*private Control CreateTokenRow)')
if (-not $statusTokenRowLayout.Success -or
    $statusTokenRowLayout.Value -notmatch 'new RoundedPanel' -or
    $statusTokenRowLayout.Value -notmatch 'CalculateStatusTokenRowGeometry\(width\)' -or
    $statusTokenRowLayout.Value -notmatch 'MakeStatusCheckButton\(' -or
    $statusTokenRowLayout.Value -notmatch 'MakeTokenUpdateButton\(' -or
    $statusTokenRowLayout.Value -notmatch 'CheckStatusAsync\(account\)' -or
    $statusTokenRowLayout.Value -notmatch 'UpdateTokenAsync\(account\)' -or
    $statusTokenRowLayout.Value -match 'CreateStatusRow\(account,\s*width\)|CreateTokenRow\(account,\s*width\)|statusRow\.Dock|tokenRow\.Dock') {
    throw 'Status and token management must be rendered as one account-scoped card with both actions.'
}
if ($formSource -notmatch 'Tag\s*=\s*"status-check"' -or
    $formSource -notmatch 'ApplyStatusCheckButtonStyle' -or
    $formSource -notmatch 'Tag\s*=\s*"group-toggle"' -or
    $formSource -notmatch 'ApplyAccountGroupToggleButtonStyle' -or
    $formSource -notmatch 'UseSurfaceSheen\s*=\s*false') {
    throw 'Status and account-group actions must keep their dedicated flat tonal styling across theme changes.'
}
if ($formSource -notmatch 'private static string FormatEstimatedCost\(' -or
    $formSource -notmatch 'const string costHeader\s*=\s*"估算金额"' -or
    $formSource -notmatch 'Text\s*=\s*FormatEstimatedCost\(usage,\s*priceProfile\)' -or
    $formSource -notmatch 'binding\.Metrics\[index\]\.Cost\.Text\s*=\s*FormatEstimatedCost' -or
    $formSource -match 'private static string FormatUsageCostDisplay\(' -or
    $formSource -match '"API 等值估算"|"API 费用估算"') {
    throw 'Quota metric cards must show a bare dollar amount, while the detail table labels the estimate once in its header.'
}
$statusCheckHandler = [regex]::Match(
    $formSource,
    '(?s)private async Task CheckStatusAsync\(AccountRecord account\).*?(?=\r?\n\s*private async Task UpdateTokenAsync)')
if (-not $statusCheckHandler.Success -or
    $statusCheckHandler.Value -notmatch 'var conciseStatus = status\.ExitCode == 0 \? "已登录" : "检查失败"' -or
    $statusCheckHandler.Value -notmatch '_statusBox\.Text = \$"\{account\.Name\} · \{conciseStatus\}"' -or
    $statusCheckHandler.Value -notmatch '_toolTip\.SetToolTip\(' -or
    $statusCheckHandler.Value -notmatch '状态：\{status\.Text\}' -or
    $statusCheckHandler.Value -match '_statusBox\.Text = account\.IsCompatibleApi|_statusBox\.Text = .*status\.Text|_statusBox\.Text = .*API 地址|_statusBox\.Text = .*Token 到期') {
    throw 'Status checks must leave one concise outcome in the visible footer and move raw login/API/token details to its tooltip.'
}
if ($formSource -notmatch 'var headerTextWidth\s*=\s*Math\.Clamp\(' -or
    $formSource -notmatch '_headerSubtitle\.Width\s*=\s*Math\.Max\(\s*260,\s*Math\.Min\(headerTextWidth,\s*header\.ClientSize\.Width\s*-\s*_headerSubtitle\.Left\s*-\s*20\)\)' -or
    $formSource -notmatch 'var controlsRow = _controlsRow = new (?:Buffered)?FlowLayoutPanel[\s\S]*?Height = 58' -or
    $formSource -notmatch 'MakeTokenUpdateButton\(\s*GetCredentialActionText\(account\),\s*geometry\.Update\.Left,\s*geometry\.Update\.Top,\s*geometry\.Update\.Width\)' -or
    $formSource -notmatch 'var rootLabel = new Label[\s\S]*?Width = 150' -or
    $formSource -notmatch 'var note = new Label[\s\S]*?Height = 36') {
    throw 'High-DPI UI must keep header, token actions, and system configuration labels fully visible.'
}
if ($formSource -match 'NativeWindowTheme\.SuspendRedraw\(this\)' -or
    $formSource -match 'RecreateHandle\(' -or
    $formSource -match 'ShowInTaskbar\s*=\s*false' -or
    $formSource -notmatch 'using var redraw = NativeWindowTheme\.SuspendRedraw\(_accountLayout\)' -or
    $formSource -notmatch 'private readonly BufferedFlowLayoutPanel _cardsPanel' -or
    $formSource -notmatch 'if \(_suppressSearchRender\)' -or
    $modernUiControlsSource -notmatch 'internal sealed class BufferedFlowLayoutPanel' -or
    $modernUiControlsSource -notmatch 'ShouldSuspendRedraw\(isTopLevelWindow\)' -or
    $modernUiControlsSource -notmatch 'ValidateRedrawPolicy' -or
    $programSource -notmatch 'NativeWindowTheme\.ValidateRedrawPolicy\(\)') {
    throw 'Workspace navigation must freeze only buffered child containers; top-level WM_SETREDRAW would hide the taskbar button.'
}
$headerTitleLayout = [regex]::Match(
    $formSource,
    '(?s)_headerTitle\.Text\s*=.*?header\.Controls\.Add\(_headerTitle\);')
if (-not $headerTitleLayout.Success -or
    $headerTitleLayout.Value -notmatch '_headerTitle\.Top\s*=\s*4' -or
    $headerTitleLayout.Value -notmatch '_headerTitle\.Height\s*=\s*62' -or
    $headerTitleLayout.Value -notmatch '_headerTitle\.TextAlign\s*=\s*ContentAlignment\.MiddleLeft' -or
    $headerTitleLayout.Value -notmatch '_headerTitle\.UseMnemonic\s*=\s*false' -or
    $headerTitleLayout.Value -notmatch '_headerTitle\.UseCompatibleTextRendering\s*=\s*true' -or
    $headerTitleLayout.Value -notmatch '_headerTitle\.Name\s*=\s*"HeaderTitle"' -or
    $formSource -notmatch 'AutoScaleMode\s*=\s*AutoScaleMode\.Dpi' -or
    $formSource -notmatch '_headerTitle\.Width\s*=\s*Math\.Max\(\s*240,\s*Math\.Min\(headerTextWidth,\s*header\.ClientSize\.Width\s*-\s*_headerTitle\.Left\s*-\s*20\)\)' -or
    $formSource -match '_workspaceBadge|HeaderWorkspaceBadge') {
    throw 'HeaderTitle must use the full badge-free width, reserve a vertically centered 62px logical box, and scale responsively at high DPI.'
}
if ($formSource -notmatch 'WorkspaceView\.ThemeSettings' -or
    $formSource -notmatch 'RenderThemeSettingsPanel' -or
    $formSource -notmatch 'private readonly ThemePicker _themeModePicker\s*=\s*new\(\)' -or
    $formSource -notmatch 'Name\s*=\s*"SidebarFooter"' -or
    $formSource -notmatch 'void UpdateManagerAppearanceFooterLayout\(\)' -or
    $formSource -notmatch 'managerAppearanceFooter\.SetBounds\(' -or
    $formSource -notmatch 'sidebar\.Resize\s*\+=' -or
    $formSource -notmatch 'Text\s*=\s*"管理器外观"' -or
    $formSource -notmatch '_themeModePicker\.SetBounds\(0,\s*34,\s*228,\s*46\)' -or
    $formSource -notmatch '_themeModePicker\.SetItems\(ThemeOptions\.Select\(option => option\.Label\)\)' -or
    $formSource -notmatch '_themeModePicker\.SelectedIndexChanged\s*\+=' -or
    $formSource -notmatch '_themeModePicker\.ApplyPalette\(_palette\)' -or
    $formSource -notmatch '管理器外观与 Codex 主题相互独立' -or
    $formSource -notmatch '"Codex 主题库"' -or
    $formSource -notmatch '"official-default"' -or
    $formSource -notmatch 'CodexDreamSkinService\.IsOfficialAppearanceActive\(\)' -or
    $dreamSkinServiceSource -notmatch 'public static bool IsOfficialAppearanceActive\(\)' -or
    $formSource -notmatch '"恢复官方外观"' -or
    $formSource -notmatch 'StartCodexAppearanceAsync' -or
    $formSource -notmatch 'CalculateCodexAppearanceDetailPreviewSize' -or
    $formSource -notmatch 'ValidateCodexAppearanceLayouts' -or
    $programSource -notmatch 'Form1\.ValidateCodexAppearanceLayouts\(\)' -or
    $formSource -notmatch 'CreateCodexAppearanceLibraryRow' -or
    $formSource -notmatch 'CreateCodexAppearanceDetailPanel' -or
    $formSource -notmatch 'ApplyCodexDreamSkinAsync' -or
    $formSource -notmatch 'RestoreOfficialCodexAppearanceAsync' -or
    $formSource -match '_themeModeBox') {
    throw 'Manager appearance must live in the sidebar footer while the Codex-only theme library provides startup, official-default, large-preview, and DPI-safe controls.'
}
$startCodexAppearanceMethod = [regex]::Match(
    $formSource,
    '(?s)private Task StartCodexAppearanceAsync\(CodexAppearanceOption appearance\).*?(?=\r?\n\s*private void ToggleCodexDreamSkinPreference\()')
$applyCodexAppearanceMethod = [regex]::Match(
    $formSource,
    '(?s)private async Task ApplyCodexDreamSkinAsync\(CodexAppearanceOption appearance\).*?(?=\r?\n\s*private async Task RestoreOfficialCodexAppearanceAsync\()')
$codexAppearanceLibraryRowMethod = [regex]::Match(
    $formSource,
    '(?s)private Control CreateCodexAppearanceLibraryRow\(CodexAppearanceOption appearance, int width\).*?(?=\r?\n\s*private Control CreateCodexAppearanceDetailPanel\()')
$codexAppearanceDetailMethod = [regex]::Match(
    $formSource,
    '(?s)private Control CreateCodexAppearanceDetailPanel\(int width\).*?(?=\r?\n\s*private static Size CalculateCodexAppearanceDetailPreviewSize\()')
if (-not $startCodexAppearanceMethod.Success -or
    $startCodexAppearanceMethod.Value -notmatch 'return ApplyCodexDreamSkinAsync\(appearance\)' -or
    $startCodexAppearanceMethod.Value -match 'TryEnableCodexStartupTheme|下次启动时自动同步|RenderCards\(' -or
    -not $applyCodexAppearanceMethod.Success -or
    $applyCodexAppearanceMethod.Value -notmatch '_selectedCodexAppearanceId\s*=\s*appearance\.Id' -or
    $applyCodexAppearanceMethod.Value -notmatch '_appSettings\.CodexAppearancePresetId\s*=\s*appearance\.Id' -or
    $applyCodexAppearanceMethod.Value -notmatch '_codex\.ApplyCodexDreamSkinAsync\(' -or
    -not $codexAppearanceLibraryRowMethod.Success -or
    $codexAppearanceLibraryRowMethod.Value -notmatch 'launch\.Enabled\s*=\s*true' -or
    $codexAppearanceLibraryRowMethod.Value -notmatch '设为 Codex 启动主题并立即应用' -or
    -not $codexAppearanceDetailMethod.Success -or
    $codexAppearanceDetailMethod.Value -notmatch 'await ApplyCodexDreamSkinAsync\(appearance\)') {
    throw 'Codex theme-card launch and detail apply actions must share the immediate apply path and persist the successful theme as the startup preference.'
}
$themeSettingsMethod = [regex]::Match(
    $formSource,
    '(?s)private void RenderThemeSettingsPanel\(int width\).*?(?=\r?\n\s*private Control CreateThemeSectionHeader\()')
if (-not $themeSettingsMethod.Success -or
    $themeSettingsMethod.Value -match '_themeModePicker|管理器外观') {
    throw 'The Codex theme workspace must not own or render Account Manager appearance controls.'
}
if ($modernUiControlsSource -notmatch 'internal sealed class ThemePicker\s*:\s*Control' -or
    $modernUiControlsSource -notmatch 'ControlStyles\.OptimizedDoubleBuffer' -or
    $modernUiControlsSource -notmatch 'ControlStyles\.SupportsTransparentBackColor' -or
    $modernUiControlsSource -notmatch 'ControlStyles\.UserPaint' -or
    $modernUiControlsSource -notmatch 'AccessibleRole\s*=\s*AccessibleRole\.ComboBox' -or
    $modernUiControlsSource -notmatch 'protected override void OnPaint\(PaintEventArgs e\)' -or
    $modernUiControlsSource -notmatch 'UiDesign\.CreateRoundedPath' -or
    $modernUiControlsSource -notmatch 'UiDesign\.CenterTextVertically' -or
    $modernUiControlsSource -notmatch 'Keys\.Enter or Keys\.Space or Keys\.F4' -or
    $modernUiControlsSource -notmatch 'Keys\.Up or Keys\.Down' -or
    $modernUiControlsSource -notmatch 'internal sealed class ThemePickerMenuRenderer' -or
    $modernUiControlsSource -notmatch '_menu\.Renderer\s*=\s*new ThemePickerMenuRenderer' -or
    $modernUiControlsSource -notmatch 'GetPopupConstraintBounds\(\)' -or
    $modernUiControlsSource -notmatch '!string\.Equals\(container\.Name,\s*"Sidebar",\s*StringComparison\.Ordinal\)' -or
    $modernUiControlsSource -notmatch 'CalculateMenuBounds\(popupConstraintBounds,\s*anchorBounds,\s*_menu\.Size\)' -or
    $modernUiControlsSource -notmatch 'var openAbove = spaceBelow < height && spaceAbove >= spaceBelow' -or
    $modernUiControlsSource -notmatch 'Math\.Clamp\(preferredY,\s*ownerClientBounds\.Top,\s*maxY\)' -or
    $modernUiControlsSource -notmatch '_menuBackColor\s*=\s*palette\.SidebarColor' -or
    $modernUiControlsSource -notmatch '_menuBackColorEnd\s*=\s*UiDesign\.Blend\(palette\.SidebarColor,\s*palette\.HeroEndColor') {
    throw 'ThemePicker must be fully owner-drawn, DPI-aware, keyboard accessible, theme-colored, and constrained to the sidebar.'
}
if ($formSource -match '_headerEyebrow') {
    throw 'The compact header must not restore the redundant English eyebrow.'
}
if ($formSource -notmatch '_headerPanel\.BackColor\s*=\s*_palette\.HeroStartColor' -or
    $formSource -notmatch '_headerPanel\.GradientColor\s*=\s*_palette\.HeroEndColor' -or
    $formSource -match '(?s)private void UpdateWorkspaceChrome\(\).*?_headerPanel\.BackColor\s*=\s*UiDesign\.Blend') {
    throw 'Every workspace in one selected theme must share the same hero background instead of changing color by page.'
}
if ($formSource -notmatch 'AccountRowMinWidth\s*=\s*560' -or
    $formSource -notmatch 'AccountSwitchHorizontalMinWidth\s*=\s*920' -or
    $formSource -notmatch 'QuotaUsageHorizontalMinWidth\s*=\s*900' -or
    $formSource -notmatch 'ValidateResponsiveAccountCardLayouts\(\)' -or
    $formSource -notmatch 'CalculateQuotaUsageHorizontalGeometry\(width\)' -or
    $formSource -notmatch 'CalculateStableWorkspaceWidth\(' -or
    $formSource -notmatch '_cardsPanel\.Width' -or
    $formSource -notmatch 'WorkspaceScrollbarEdgeInset' -or
    $formSource -match 'var scrollReserve = _cardsPanel\.VerticalScroll\.Visible' -or
    $formSource -match 'SystemInformation\.VerticalScrollBarWidth\s*-\s*CardGap\s*-\s*32') {
    throw 'Responsive rows must use compact breakpoints and reserve one stable native scrollbar gutter.'
}
if ($formSource -notmatch 'contentLayout\.RowStyles\.Add\(new RowStyle\(SizeType\.Absolute,\s*WorkspaceHeroHeight\)\)' -or
    $formSource -notmatch 'contentLayout\.Controls\.Add\(header,\s*0,\s*0\)' -or
    $formSource -notmatch 'contentLayout\.Controls\.Add\(_accountLayout,\s*0,\s*1\)' -or
    $formSource -notmatch '_accountLayout\.Controls\.Add\(_cardsPanel,\s*0,\s*0\)' -or
    $formSource -notmatch 'contentLayout\.Controls\.Add\(_statusBox,\s*0,\s*2\)') {
    throw 'Main content must keep header, account workspace, and status area in separate rows.'
}
if ($formSource -notmatch 'CreateQuotaUsageRow' -or
    $usageTrackerSource -notmatch 'token_count' -or
    $usageTrackerSource -notmatch 'last_token_usage' -or
    $usageTrackerSource -notmatch 'SecondaryRateLimitWindowMinutes' -or
    $usageTrackerSource -notmatch 'window_minutes' -or
     $usageTrackerSource -notmatch 'AssignUsageToActiveSwitch\(usage' -or
    $usageTrackerSource -notmatch 'total_token_usage' -or
    $usageTrackerSource -notmatch 'LastCumulativeUsageSignature' -or
    $usageTrackerSource -notmatch 'UsageEventSource\.OfficialSnapshot' -or
    $usageTrackerSource -notmatch 'usage-account-switches\.json' -or
    $usageTrackerSource -notmatch 'RateLimitUsedPercent') {
    throw 'Quota view must parse local Codex token_count logs, track account switches, and show per-account usage.'
}
$removedProbeFiles = @(
    'src\CodexAccountManager\CodexQuotaProbeRunner.cs',
    'src\CodexAccountManager\QuotaCapacityMeasurement.cs',
    'src\CodexAccountManager\QuotaMeasurementDialog.cs',
    'src\CodexAccountManager\QuotaDollarEstimator.cs'
)
foreach ($relativePath in $removedProbeFiles) {
    if (Test-Path -LiteralPath (Join-Path $root $relativePath)) {
        throw "Retired quota-probe source must be absent: $relativePath"
    }
}
$allCSharpSource = (Get-ChildItem -LiteralPath (Join-Path $root 'src\CodexAccountManager') -Filter '*.cs' -File |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$forbiddenProbePatterns = @(
    'CodexQuotaProbeRunner',
    'QuotaCapacityMeasurementService',
    'QuotaMeasurementAction',
    'MeasureQuotaCapacityAsync',
    'RunQuotaMeasurementLoopAsync',
    'CALIBRATION_PAYLOAD',
    'codex-quota-probe-',
    'RecordProbeBatch',
    'ScanNaturalSecondBoundary',
    'GetQuotaMeasurementPresentation'
)
foreach ($pattern in $forbiddenProbePatterns) {
    if ($allCSharpSource -match [regex]::Escape($pattern)) {
        throw "Retired active quota-probe code is still reachable: $pattern"
    }
}
if ($probeUsageLedgerSource -notmatch 'LoadSince\(' -or
    $probeUsageLedgerSource -notmatch 'ToUsageEvent\(' -or
    $probeUsageLedgerSource -match 'public void Record\(' -or
    $usageTrackerSource -notmatch '_probeUsageLedger\.LoadSince' -or
    $usageTrackerSource -notmatch 'legacyLedger' -or
    $programSource -notmatch 'ProbeUsageLedger\.ValidateLedger' -or
    $programSource -notmatch 'UsageTracker\.ValidateProbeUsageMerge' -or
    $resetSessionSource -notmatch 'int\? UsedPercent' -or
    $resetSessionSource -notmatch 'ReadRateLimitPercent') {
    throw 'Historical probe Token charges must remain read-only while all active quota probing is removed.'
}
if ($passiveQuotaMonitorSource -notmatch 'PassiveQuotaMonitor' -or
    $passiveQuotaMonitorSource -notmatch 'MinimumObservedPercentSpan\s*=\s*2' -or
    $passiveQuotaMonitorSource -notmatch '(?s)FiveHourProfile\s*=\s*new\(\s*"five_hour",\s*240,\s*360,\s*300,\s*10D,' -or
    $passiveQuotaMonitorSource -notmatch '(?s)WeeklyProfile\s*=\s*new\(\s*"weekly",\s*9_000,\s*11_000,\s*10_080,\s*90D,' -or
    $passiveQuotaMonitorSource -notmatch '(?s)MonthlyProfile\s*=\s*new\(\s*"monthly",\s*40_000,\s*47_000,\s*43_800,\s*200D,' -or
    $passiveQuotaMonitorSource -notmatch 'MonthlySolThresholdUsd\s*=\s*200D' -or
    $passiveQuotaMonitorSource -notmatch 'MonthlyTerraThresholdUsd\s*=\s*100D' -or
    $passiveQuotaMonitorSource -notmatch 'MonthlyLunaThresholdUsd\s*=\s*80D' -or
    $passiveQuotaMonitorSource -notmatch 'ResolveStatusThresholdUsd' -or
    $passiveQuotaMonitorSource -notmatch 'ResolveDominantNaturalModel' -or
    $passiveQuotaMonitorSource -notmatch 'ValidateMonthlyModelThresholds' -or
    $passiveQuotaMonitorSource -notmatch 'UsageEventSource\.Natural or UsageEventSource\.OfficialSnapshot' -or
    $passiveQuotaMonitoringSource -notmatch 'CaptureOfficialObservationsIfCurrent' -or
    $passiveQuotaMonitorSource -notmatch 'ActivationEpochUtc' -or
    $passiveQuotaMonitorSource -notmatch 'naturalUsageNotBeforeUtc' -or
    $passiveQuotaMonitorSource -notmatch 'ExportCsv' -or
    $passiveQuotaMonitorSource -notmatch 'PassiveQuotaAssessmentWindow' -or
    $passiveQuotaMonitorSource -notmatch 'MaximumAssessmentWindowCount\s*=\s*256' -or
    $passiveQuotaMonitoringSource -notmatch 'StoredAssessmentWindow' -or
    $passiveQuotaMonitoringSource -notmatch 'ValidateLegacyAssessmentSchema' -or
    $passiveQuotaMonitorSource -match 'CodexCliService|Process\.Start|thread/start|turn/start|CALIBRATION_PAYLOAD' -or
    $passiveQuotaMonitoringSource -notmatch 'class PassiveQuotaMonitoringService' -or
    $passiveQuotaMonitoringSource -notmatch 'quota-monitor-settings\.json' -or
    $passiveQuotaMonitoringSource -notmatch 'Guid\.NewGuid\(\)\.ToString\("N"\)' -or
    $passiveQuotaMonitoringSource -notmatch 'DisableAndCapture' -or
    $passiveQuotaMonitoringSource -match 'CodexCliService|Process\.Start|thread/start|turn/start|CALIBRATION_PAYLOAD' -or
    $quotaDashboardControlsSource -notmatch 'PassiveQuotaGauge' -or
    $quotaDashboardControlsSource -notmatch 'QuotaTrendChart' -or
    $quotaDashboardControlsSource -notmatch 'CreateWavePath' -or
    $quotaDashboardControlsSource -notmatch 'liquidBounds\.Bottom - \(liquidBounds\.Height \* percent / 100F\)' -or
    $quotaDashboardControlsSource -notmatch 'graphics\.SetClip\(spherePath, CombineMode\.Intersect\)' -or
    $quotaDashboardControlsSource -notmatch 'DrawCenteredPercentage' -or
    $quotaDashboardControlsSource -notmatch 'ValidateLiquidFill' -or
    $quotaDashboardControlsSource -notmatch 'ValidateGaugeCaptionAtHighDpi' -or
    $quotaDashboardControlsSource -notmatch 'SetDpiScaleForOfflineValidation\(2F\)' -or
    $quotaDashboardControlsSource -notmatch 'Caption = "周剩余"' -or
    $quotaDashboardControlsSource -notmatch 'allowWrap: false' -or
    $quotaDashboardControlsSource -match 'DrawTicks\(' -or
    $quotaDashboardControlsSource -notmatch 'BuildClampedMonotoneCurve' -or
    $quotaDashboardControlsSource -notmatch 'public Color CostColor' -or
    $quotaDashboardControlsSource -notmatch 'public Color CostFillColor' -or
    $quotaDashboardControlsSource -notmatch 'FormatUsdAxis\(cost, maximumCost\)' -or
    $quotaDashboardControlsSource -notmatch 'protected override void OnMouseMove\(MouseEventArgs e\)' -or
     $quotaDashboardControlsSource -notmatch 'GetRollingOneHourMetric\(' -or
    $quotaDashboardControlsSource -notmatch 'DrawAbnormalRemainingWindows\(' -or
     $quotaDashboardControlsSource -notmatch '容量推测偏差段' -or
    $quotaDashboardControlsSource -notmatch '使用速率' -or
    $quotaDashboardControlsSource -notmatch '近 1 小时' -or
    $formSource -notmatch 'MakePassiveQuotaMonitor' -or
    $formSource -notmatch '"开启额度监测"' -or
    $formSource -notmatch '"关闭额度监测"' -or
    $formSource -notmatch 'TogglePassiveQuotaMonitoring' -or
    $formSource -notmatch 'AccountQuotaLimitType\.WeeklyOnly\s*=>\s*\r?\n\s*usage\.GetQuotaWindow\(AccountQuotaWindowKind\.Weekly\)' -or
    $formSource -notmatch '_passiveQuotaMonitoring\.Analyze' -or
    $formSource -notmatch 'MakeQuotaTrendToolbar' -or
    $formSource -notmatch 'Height\s*=\s*GetQuotaTrendChartHeight\(innerWidth\)' -or
    $formSource -notmatch 'CalculateQuotaTrendChartHeight\(innerWidth, dpiScale\)' -or
    $quotaDashboardControlsSource -notmatch 'CalculateModelTooltipLayout' -or
    $quotaDashboardControlsSource -notmatch 'ValidateHighDpiTooltipLayout' -or
    $formSource -notmatch 'CostColor\s*=\s*_palette\.PrimaryColor' -or
    $formSource -notmatch '导出 CSV' -or
    $programSource -notmatch 'PassiveQuotaMonitor\.Validate\(\)' -or
    $programSource -notmatch 'QuotaDashboardControls\.Validate\(\)') {
    throw 'Passive quota monitoring must use only natural logs, render a level-controlled gauge plus a tall USD trend with hourly hover details, and export safe CSV.'
}
if ($modernUiControlsSource -notmatch 'new LinearGradientBrush\(bounds, topFill, fill, 90F\)' -or
    $modernUiControlsSource -notmatch 'content\.Offset\(0, 1\)' -or
    $modernUiControlsSource -notmatch 'DarkMode_Explorer' -or
    $formSource -notmatch 'NativeWindowTheme\.ApplyScrollable\(_cardsPanel' -or
    $formSource -notmatch 'Radius = 12,\s*\r?\n\s*Padding = new Padding\(12, 0, 12, 0\)' -or
    $formSource -notmatch 'MinimumFontSize = 8F') {
    throw 'Global action buttons must retain the polished gradient, pressed feedback, consistent radius, and DPI-safe text padding.'
}
$quotaTrendToolbarSizing = [regex]::Match(
    $formSource,
    '(?s)private Control MakeQuotaTrendToolbar\(.*?(?=\r?\n\s*private static TimeSpan GetQuotaTrendBucketSize)')
if (-not $quotaTrendToolbarSizing.Success -or
    $quotaTrendToolbarSizing.Value -notmatch 'rangeWidths\s*=\s*ranges[\s\S]*?MeasureActionButtonWidth\(option\.Label, 64\)' -or
    $quotaTrendToolbarSizing.Value -notmatch 'rangeGroupWidth = rangeWidths\.Sum\(\)' -or
    $quotaTrendToolbarSizing.Value -notmatch '\("今天", TimeSpan\.FromHours\(24\)\)' -or
    $quotaTrendToolbarSizing.Value -notmatch '\("本周", TimeSpan\.FromDays\(7\)\)' -or
    $quotaTrendToolbarSizing.Value -notmatch '\("本月", TimeSpan\.FromDays\(30\)\)' -or
    $quotaTrendToolbarSizing.Value -notmatch 'rangeButton\.AutoShrinkText = false') {
    throw 'Quota trend range buttons must show complete localized today, this-week, and this-month labels at high DPI.'
}
$tokenRowLayout = [regex]::Match(
    $formSource,
    '(?s)private Control CreateTokenRow\(AccountRecord account,\s*int width\).*?(?=\r?\n\s*private Control CreateUsageUnassignedNotice)')
$tokenAuthKindLayout = [regex]::Match(
    $tokenRowLayout.Value,
    '(?s)var authKind = new Label\s*\{.*?\};')
$tokenDetailLayout = [regex]::Match(
    $tokenRowLayout.Value,
    '(?s)var detail = new Label\s*\{.*?\};')
if (-not $tokenRowLayout.Success -or
    -not $tokenAuthKindLayout.Success -or
    $tokenAuthKindLayout.Value -notmatch 'Bounds\s*=\s*geometry\.AuthKind' -or
    $tokenAuthKindLayout.Value -notmatch 'TextAlign\s*=\s*ContentAlignment\.MiddleLeft' -or
    $tokenAuthKindLayout.Value -notmatch 'UseCompatibleTextRendering\s*=\s*true' -or
    -not $tokenDetailLayout.Success -or
    $tokenDetailLayout.Value -notmatch 'Bounds\s*=\s*geometry\.Detail' -or
    $tokenDetailLayout.Value -notmatch 'TextAlign\s*=\s*ContentAlignment\.MiddleLeft' -or
    $tokenDetailLayout.Value -notmatch 'UseCompatibleTextRendering\s*=\s*true' -or
    $formSource -notmatch 'internal static void ValidateTokenRowGeometry\(\)' -or
    $formSource -notmatch 'var narrow = width < 980' -or
    $programSource -notmatch 'Form1\.ValidateTokenRowGeometry\(\)') {
    throw 'Token rows must use validated narrow/wide geometry with complete middle-left compatible text and non-overlapping actions.'
}
$passiveQuotaMonitorLayout = [regex]::Match(
    $formSource,
    '(?s)private Control MakePassiveQuotaMonitor\(.*?(?=\r?\n\s*private void TogglePassiveQuotaMonitoring)')
if (-not $passiveQuotaMonitorLayout.Success -or
    $passiveQuotaMonitorLayout.Value -notmatch 'var monitorToggleText\s*=\s*monitoring\.IsEnabled\s*\?\s*"关闭额度监测"\s*:\s*"开启额度监测"' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'MeasureActionButtonWidth\(monitorToggleText,\s*240\)' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'MakeActionButton\([\s\S]*?monitorToggleText,[\s\S]*?monitorToggleWidth,[\s\S]*?!monitoring\.IsEnabled\)' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'toggle\.Height\s*=\s*44' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'modernToggle\.AutoShrinkText\s*=\s*false' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'Name\s*=\s*"PassiveQuotaStatusLabel"' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'externalStatus\.ForeColor\s*=\s*statusColor' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'AccountQuotaLimitType\.WeeklyOnly\s*=>\s*"周剩余"' -or
     $passiveQuotaMonitorLayout.Value -notmatch 'Caption\s*=\s*account\.IsCompatibleApi\s*\?\s*"[^"]+"\s*:\s*gaugeCaption' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'var panelHeight\s*=\s*compact \? 620 : 540' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'width \* 0\.34D' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'Math\.Min\(420,\s*Math\.Max\(292,\s*gaugeColumnWidth - 44\)\)' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'Left\s*=\s*gaugeLeft' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'var gaugeTop\s*=\s*compact \? 58 : 64' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'var infoLeft\s*=\s*compact \? 18 : 18 \+ gaugeColumnWidth \+ 18' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'ShowTechDecoration\s*=\s*false' -or
     $passiveQuotaMonitorLayout.Value -notmatch 'GetPassiveQuotaSummaryText' -or
    $passiveQuotaMonitorLayout.Value -match '推测总额') {
    throw 'Passive quota monitor must keep measured toggle text, show the health state outside a large left-column gauge, avoid decorative lines, and use the compact remaining/estimated-capacity summary.'
}
$quotaUsageRowLayout = [regex]::Match(
    $formSource,
    '(?s)private Control CreateQuotaUsageRow\(.*?(?=\r?\n\s*private QuotaProgressBar MakeQuotaProgressBar)')
if (-not $quotaUsageRowLayout.Success -or
    $quotaUsageRowLayout.Value -notmatch 'ShowTechDecoration\s*=\s*false' -or
    $quotaUsageRowLayout.Value -match 'ShowTechDecoration\s*=\s*IsCurrentAccount') {
    throw 'Quota list cards must not paint decorative circuit lines across quota percentages and reset details.'
}
$passiveQuotaEstimateSummary = [regex]::Match(
    $formSource,
    '(?s)private static double\? GetDisplayedQuotaCapacityUsd\(PassiveQuotaMonitoringResult monitoring\).*?(?=\r?\n\s*private void UpdatePassiveQuotaStatus)')
if (-not $passiveQuotaEstimateSummary.Success -or
    $passiveQuotaEstimateSummary.Value -notmatch 'monitoring\.State\.DisplayCapacityUsd \?\? monitoring\.Estimate\?\.EstimatedTotalUsd' -or
    $passiveQuotaEstimateSummary.Value -notmatch 'ProjectDisplayedRemainingUsd' -or
     $passiveQuotaEstimateSummary.Value -notmatch 'GetDisplayedQuotaRemainingUsd' -or
    $passiveQuotaEstimateSummary.Value -match '显示基准会在本轮额度窗口内保持稳定' -or
    $passiveQuotaMonitoringSource -notmatch 'Every new official integer-percent boundary produces a new natural-usage' -or
    $passiveQuotaMonitoringSource -notmatch 'return new DisplayCapacityAnchor\(\s*nextCapacity' -or
    $passiveQuotaEstimateSummary.Value -match '总额|总额度|推测总额') {
    throw 'Visible and tooltip quota estimates must publish every official one-percent recalibration and use the latest official percentage without total-capacity labels.'
}
$usageDetailTitleLayout = [regex]::Match(
    $formSource,
    '(?s)var tableTitle = new Label\s*\{.*?Text\s*=\s*"用量明细".*?\};')
if (-not $usageDetailTitleLayout.Success -or
    $usageDetailTitleLayout.Value -notmatch 'Height\s*=\s*44') {
    throw 'Quota usage detail title must reserve a 44px high-DPI text box.'
}
$quotaTrendScopeLayout = [regex]::Match(
    $formSource,
    '(?s)private enum QuotaTrendScope\s*\{.*?\}')
$quotaTrendToolbarLayout = [regex]::Match(
    $formSource,
    '(?s)private Control MakeQuotaTrendToolbar\(.*?(?=\r?\n\s*private static TimeSpan GetQuotaTrendBucketSize)')
if (-not $quotaTrendScopeLayout.Success -or
    $quotaTrendScopeLayout.Value -notmatch '\bRealtime\b' -or
    $quotaTrendScopeLayout.Value -notmatch '\bMonitoring\b' -or
    -not $quotaTrendToolbarLayout.Success -or
    $quotaTrendToolbarLayout.Value -notmatch 'out IReadOnlyList<PassiveQuotaTrendPoint> trendPoints,\s*out string trendEmptyText' -or
    $quotaTrendToolbarLayout.Value -notmatch '!_quotaTrendScopes\.TryGetValue\(accountKey,\s*out var scope\)[\s\S]*?scope\s*=\s*QuotaTrendScope\.Realtime' -or
     $quotaTrendToolbarLayout.Value -notmatch '"全部日志"' -or
    $quotaTrendToolbarLayout.Value -notmatch '"本轮监测"' -or
    $quotaTrendToolbarLayout.Value -notmatch '_quotaTrendScopes\[accountKey\]\s*=\s*QuotaTrendScope\.Realtime' -or
    $quotaTrendToolbarLayout.Value -notmatch '_quotaTrendScopes\[accountKey\]\s*=\s*QuotaTrendScope\.Monitoring' -or
    $quotaTrendToolbarLayout.Value -notmatch 'var trendData\s*=\s*GetQuotaTrendData\(account,\s*usage,\s*priceProfile,\s*passiveMonitoring\)' -or
    $quotaTrendToolbarLayout.Value -notmatch 'trendPoints\s*=\s*trendData\.Points' -or
    $quotaTrendToolbarLayout.Value -notmatch 'trendEmptyText\s*=\s*trendData\.EmptyText' -or
    $quotaTrendToolbarLayout.Value -notmatch 'trendAssessmentWindows\s*=\s*trendData\.AssessmentWindows \?\? \[\]' -or
    $quotaTrendToolbarLayout.Value -notmatch 'private QuotaTrendDisplayData GetQuotaTrendData' -or
    $quotaTrendToolbarLayout.Value -notmatch 'var points\s*=[\s\S]*?PassiveQuotaMonitor\.BuildTrend\([\s\S]*?throughUtc\)' -or
    $quotaTrendToolbarLayout.Value -notmatch 'export\.Tag\s*=\s*trendPoints' -or
    $quotaTrendToolbarLayout.Value -notmatch 'export\.Tag as IReadOnlyList<PassiveQuotaTrendPoint>' -or
    $formSource -notmatch 'out var trendPoints,\s*out var trendEmptyText,\s*out var trendFromUtc,\s*out var trendThroughUtc,\s*out var trendAssessmentWindows,\s*out var exportButton' -or
    $formSource -notmatch 'Samples\s*=\s*BuildQuotaChartSamples\(' -or
    $formSource -notmatch 'AssessmentWindows\s*=\s*trendAssessmentWindows' -or
    $formSource -notmatch 'SelectQuotaTrendAssessmentWindows\(' -or
    $formSource -notmatch 'scope != QuotaTrendScope\.Monitoring[\s\S]*?!hasMonitoringEpoch[\s\S]*?return \[\]' -or
    $formSource -notmatch 'item\.Status == PassiveQuotaStatus\.Abnormal' -or
    $formSource -notmatch 'EmptyText\s*=\s*trendEmptyText' -or
    $formSource -notmatch 'PassiveQuotaMonitor\.ExportCsv\(trendPoints\)') {
    throw 'Quota trend UI must default to Realtime, expose Realtime and Monitoring scopes, return its empty text, and export the exact points currently charted.'
}
$buildTrendLayout = [regex]::Match(
    $passiveQuotaMonitorSource,
    '(?s)public static IReadOnlyList<PassiveQuotaTrendPoint> BuildTrend\(.*?(?=\r?\n\s*public static byte\[\] ExportCsv)')
if (-not $buildTrendLayout.Success -or
    $buildTrendLayout.Value -notmatch 'DateTimeOffset\? throughUtc\s*=\s*null' -or
    $buildTrendLayout.Value -notmatch 'var normalizedThroughUtc\s*=\s*throughUtc\?\.ToUniversalTime\(\)' -or
    $buildTrendLayout.Value -notmatch 'item\.TimestampUtc\s*>=\s*normalizedFromUtc\s*&&\s*\(!normalizedThroughUtc\.HasValue\s*\|\|\s*item\.TimestampUtc\s*<\s*normalizedThroughUtc\.Value\)' -or
    $buildTrendLayout.Value -notmatch 'normalizedFromUtc\.UtcTicks\s*\+' -or
    $buildTrendLayout.Value -notmatch 'timestamp\.UtcTicks\s*-\s*normalizedFromUtc\.UtcTicks') {
    throw 'Passive quota trends must use an inclusive [fromUtc, throughUtc] interval and clamp the first bucket to fromUtc.'
}
$rollingMeasurementLayout = [regex]::Match(
    $passiveQuotaMonitorSource,
    '(?s)private static RollingMeasurementSet BuildRollingMeasurementSet\(.*?(?=\r?\n\s*private static bool SameResetCycle)')
$rollingMeasurementValidation = [regex]::Match(
    $passiveQuotaMonitorSource,
    '(?s)private static void ValidateRollingMeasurementWindows\(\).*?(?=\r?\n\s*private static void ValidateBoundedTrend)')
if (-not $rollingMeasurementLayout.Success -or
    $passiveQuotaMonitorSource -notmatch 'var rollingSegments\s*=\s*rawSegments[\s\S]*?\.Select\(\(segment, index\) => BuildRollingMeasurementSet\(' -or
    $passiveQuotaMonitorSource -notmatch 'SelectMany\(item => item\.CompletedWindows\)' -or
    $passiveQuotaMonitorSource -notmatch 'RecentCompletedWindowLimit\s*=\s*5' -or
    $passiveQuotaMonitorSource -notmatch 'rolling\.CompletedWindows[\s\S]*?\.TakeLast\(RecentCompletedWindowLimit\)' -or
    $passiveQuotaMonitorSource -notmatch 'RecencySmoothedValue\(robustSegments' -or
    $passiveQuotaMonitorSource -notmatch 'ValidateEveryOnePercentRefreshesEstimate\(\)' -or
    $rollingMeasurementLayout.Value -notmatch 'var measurementBoundaries\s*=\s*\(skipFirstBoundary \? boundaries\.Skip\(1\) : boundaries\)\.ToList\(\)' -or
    $rollingMeasurementLayout.Value -notmatch 'for \(var start = 0; start < measurementBoundaries\.Count; start\+\+\)' -or
    $rollingMeasurementLayout.Value -notmatch 'measurementBoundaries\[candidate\]\.UsedPercent\s*-\s*measurementBoundaries\[start\]\.UsedPercent\s*>=\s*MinimumObservedPercentSpan' -or
    $rollingMeasurementLayout.Value -notmatch 'measurementBoundaries\.Skip\(start\)\.Take\(end - start \+ 1\)' -or
    $rollingMeasurementLayout.Value -notmatch 'completed\.Add\(completedWindow\)' -or
    -not $rollingMeasurementValidation.Success -or
    $passiveQuotaMonitorSource -notmatch 'ValidateRollingMeasurementWindows\(\)' -or
    $rollingMeasurementValidation.Value -notmatch 'AddBoundary\(-1,\s*10,\s*100D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'startedAtUtc,\s*10,\s*300\)' -or
    $rollingMeasurementValidation.Value -notmatch 'AddBoundary\(1,\s*11,\s*50D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'AddBoundary\(2,\s*12,\s*0\.12D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'AddBoundary\(3,\s*13,\s*0\.12D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'AddBoundary\(4,\s*14,\s*0\.02D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'AddBoundary\(5,\s*15,\s*0\.30D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'Math\.Abs\(firstTotal - 12D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'first\.CycleCount != 1' -or
    $rollingMeasurementValidation.Value -notmatch 'Math\.Abs\(secondTotal - 9\.5D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'second\.CycleCount != 2' -or
    $rollingMeasurementValidation.Value -notmatch 'Math\.Abs\(thirdTotal - 12\.75D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'third\.CycleCount != 3' -or
    $passiveQuotaMonitorSource -notmatch 'ValidateHighVelocityMeasurementWindows\(\)' -or
    $passiveQuotaMonitorSource -notmatch 'ValidateCompletedWindowsAcrossActivationSegments\(\)' -or
    $rollingMeasurementValidation.Value -notmatch 'AddBoundary\(2,\s*16,\s*0\.48D\)' -or
    $rollingMeasurementValidation.Value -notmatch 'multi-point jumps must use') {
    throw 'Passive quota estimation must exclude the first transition, retain overlapping windows, and robustly normalize fast multi-point jumps.'
}
$passiveMonitoringAnalyzeLayout = [regex]::Match(
    $passiveQuotaMonitoringSource,
    '(?s)public PassiveQuotaMonitoringResult Analyze\(.*?(?=\r?\n\s*private PassiveQuotaMonitoringState RecordEstimateIfCurrent)')
$passiveMonitoringValidation = [regex]::Match(
    $passiveQuotaMonitoringSource,
    '(?s)internal static void Validate\(\).*?(?=\r?\n\s*private static AccountUsageSummary MakeValidationUsage)')
if (-not $passiveMonitoringAnalyzeLayout.Success -or
    $passiveMonitoringAnalyzeLayout.Value -notmatch 'var currentState\s*=\s*RecordEstimateIfCurrent\(' -or
    $passiveMonitoringAnalyzeLayout.Value -notmatch 'return new PassiveQuotaMonitoringResult\(\s*currentState,\s*estimate,\s*false,' -or
    $passiveMonitoringAnalyzeLayout.Value -match '\bDisable\(' -or
    $passiveQuotaMonitoringSource -notmatch 'StartingUsedPercent' -or
    $passiveQuotaMonitoringSource -notmatch 'StartingWindowMinutes' -or
    $formSource -notmatch 'startingWindow\?\.UsedPercent' -or
    -not $passiveMonitoringValidation.Success -or
    $passiveMonitoringValidation.Value -notmatch 'var activeResult\s*=\s*service\.Analyze\(' -or
    $passiveMonitoringValidation.Value -notmatch '!activeResult\.IsEnabled' -or
    $passiveMonitoringValidation.Value -notmatch 'activeResult\.Estimate is not \{ Status: PassiveQuotaStatus\.Normal, EstimatedTotalUsd: \{ \} total \}' -or
    $passiveMonitoringValidation.Value -notmatch '!reloaded\.IsEnabled') {
    throw 'A completed two-percent estimate must remain live and persisted while monitoring stays enabled; only the user may stop it.'
}
if ($passiveQuotaMonitorLayout.Value -notmatch 'var observedSpan\s*=\s*Math\.Clamp\(estimate\?\.ObservedPercentSpan \?\? 0D,\s*0D,\s*2D\)' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'GetPassiveQuotaProgressText\(' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'GetPassiveQuotaSummaryText\(' -or
    $passiveQuotaMonitorLayout.Value -notmatch 'UsageMetrics\s*=\s*usageMetricBindings' -or
    $formSource -notmatch 'Metrics\s*=\s*monitorBinding\.UsageMetrics' -or
    $passiveQuotaMonitorLayout.Value -notmatch '\(estimate\?\.CycleCount \?\? 0\) > 0\s*\? 100D' -or
    $passiveQuotaMonitorLayout.Value -notmatch '/2%' -or
    $formSource -match '0D,\s*3D|/3%') {
    throw 'Quota cards must show first-estimate progress only before calibration, then describe live overlapping updates without misleading current/completed rounds.'
}
if ($formSource -notmatch 'QueryUsageLimitResetAsync' -or
    $formSource -notmatch 'ResetUsageLimitAsync' -or
    $formSource -match 'ManageUsageLimitResetAsync' -or
    $formSource -notmatch 'GetResetButtonText' -or
    $formSource -notmatch 'ResetCreditStatus\.Known' -or
    $formSource -notmatch 'UsageResetAction' -or
    $formSource -notmatch 'rightWidth = compact \? width : horizontalGeometry\.RightWidth' -or
    $formSource -notmatch 'actionLeft, actionTop, 180' -or
    $formSource -notmatch 'MessageBoxDefaultButton\.Button2' -or
    $formSource -notmatch 'card\.Height\s*=\s*usageTable\.Bottom \+ 22' -or
    $formSource -match 'Height\s*=\s*740' -or
    $cliServiceSource -notmatch 'app-server --stdio --disable plugins' -or
    $cliServiceSource -notmatch 'StandardInputEncoding = new UTF8Encoding\(false\)' -or
    $cliServiceSource -notmatch 'OpenUsageLimitResetSessionAsync' -or
    $cliServiceSource -notmatch 'StartUsageLimitResetSessionAsync' -or
    $cliServiceSource -notmatch 'attempt < attemptCount' -or
    $resetSessionSource -notmatch 'account/rateLimits/read' -or
    $resetSessionSource -notmatch 'account/rateLimitResetCredit/consume' -or
    $resetSessionSource -notmatch 'idempotencyKey' -or
    $resetSessionSource -notmatch 'alreadyRedeemed' -or
    $resetSessionSource -notmatch 'rateLimitResetCredits') {
    throw 'Quota view must expose the official usage-limit reset-credit flow and size its detail card to real content.'
}
$modelTonalArcMethod = [regex]::Match(
    $modelUsageDistributionSource,
    'private static void DrawTonalArc\([\s\S]*?(?=\r?\n    private static void DrawOrbitalDepthAccents\()').Value
$modelPlanetDustMethod = [regex]::Match(
    $modelUsageDistributionSource,
    'private static void DrawPlanetDust\([\s\S]*?(?=\r?\n    private static void DrawUsageEnergyEdges\()').Value
$modelOrbitingHighlightMethod = [regex]::Match(
    $modelUsageDistributionSource,
    'private static void DrawOrbitingHighlight\([\s\S]*?(?=\r?\n    private static WrappedArcSegment\[\] CalculateWrappedArcSegments\()').Value
$modelOrbitingHighlightReferenceCount = [regex]::Matches(
    $modelUsageDistributionSource,
    'DrawOrbitingHighlight\(').Count
$modelUsageEnergyEdgeReferenceCount = [regex]::Matches(
    $modelUsageDistributionSource,
    'DrawUsageEnergyEdges\(').Count
if ($formSource -notmatch 'BuildModelUsageDistribution\(' -or
    $formSource -notmatch 'binding\.ModelDistribution' -or
    $formSource -notmatch 'RangeLabel\s*=\s*GetModelDistributionRangeLabel' -or
    $formSource -notmatch 'EstimateUsageEventCost' -or
    $modelUsageDistributionSource -notmatch 'ValidateResponsiveLayout\(\)' -or
    $programSource -notmatch 'ModelUsageDistributionControl\.ValidateResponsiveLayout\(\)' -or
    $modelUsageDistributionSource -notmatch 'internal sealed class ModelUsageDistributionControl' -or
    $modelUsageDistributionSource -notmatch '模型星系 · \{_rangeLabel\}' -or
    $modelUsageDistributionSource -notmatch '占比与 API 等值见右侧' -or
    $modelUsageDistributionSource -notmatch 'RingSweepAngle\s*=\s*360F' -or
    $modelUsageDistributionSource -notmatch 'RingStartAngle\s*=\s*-90F' -or
    $modelUsageDistributionSource -notmatch 'SolRingColor\s*=\s*Color\.FromArgb\(55,\s*124,\s*255\)' -or
    $modelUsageDistributionSource -notmatch 'LunaRingColor\s*=\s*Color\.FromArgb\(199,\s*92,\s*255\)' -or
    $modelUsageDistributionSource -notmatch 'MaxNamedRings\s*=\s*4' -or
    $modelUsageDistributionSource -notmatch 'RingHitTarget' -or
    $modelUsageDistributionSource -notmatch 'ActiveAnimationIntervalMilliseconds\s*=\s*33' -or
    $modelUsageDistributionSource -notmatch 'InactiveAnimationIntervalMilliseconds\s*=\s*64' -or
    $modelUsageDistributionSource -notmatch 'Stopwatch\s+_animationClock' -or
    $modelUsageDistributionSource -notmatch 'CalculateMaximumVisualHalfWidth' -or
    $modelUsageDistributionSource -notmatch 'WideLayoutThreshold\s*=\s*780' -or
    $modelUsageDistributionSource -notmatch 'var gap\s*=\s*Scale\(\s*safeCount\s*>=\s*5\s*\?\s*8\s*:\s*safeCount\s*==\s*4\s*\?\s*10\s*:\s*12,\s*dpi\s*\)' -or
    $modelUsageDistributionSource -notmatch 'CalculateVisualRingSweep' -or
    $modelUsageDistributionSource -notmatch 'tokens > 0L \? RingSweepAngle : 0F' -or
    $modelUsageDistributionSource -match 'CalculateUsageSweep' -or
    $modelUsageDistributionSource -notmatch 'DrawOrbitingHighlight' -or
    $modelUsageDistributionSource -notmatch 'CalculateWrappedArcSegments' -or
    $modelUsageDistributionSource -notmatch 'SampleRingTone' -or
    $modelUsageDistributionSource -notmatch 'DrawPlanetOrbit' -or
    $modelUsageDistributionSource -notmatch 'DrawTonalArc' -or
    $modelUsageDistributionSource -notmatch 'DrawOrbitalDepthAccents' -or
    $modelUsageDistributionSource -notmatch 'DrawWideModelCards' -or
    $modelUsageDistributionSource -notmatch 'CalculatePerspectiveOrbitBounds' -or
    $modelUsageDistributionSource -notmatch 'DrawNebulaCloud' -or
    $modelUsageDistributionSource -notmatch 'DrawDistantMeteor' -or
    $modelUsageDistributionSource -match 'DrawUsageEndpoints' -or
    $modelUsageDistributionSource -notmatch 'DrawPlanetDust' -or
    $modelUsageEnergyEdgeReferenceCount -ne 2 -or
    $modelUsageDistributionSource -notmatch 'ValidateArcPresentation\(\)' -or
    $modelUsageDistributionSource -notmatch 'FormatModelWithUsagePercent' -or
    $modelUsageDistributionSource -notmatch '模型 / 占比' -or
    $modelUsageDistributionSource -notmatch 'DrawFittedCenteredText' -or
    $modelUsageDistributionSource -notmatch 'StringTrimming\.None' -or
    $modelUsageDistributionSource -notmatch 'ControlStyles\.Opaque' -or
    $modelUsageDistributionSource -notmatch 'ControlStyles\.OptimizedDoubleBuffer' -or
    $modelUsageDistributionSource -notmatch 'GetPreferredHeight' -or
    $modelUsageDistributionSource -notmatch 'ValidateResponsiveLayout' -or
    $modelUsageDistributionSource -notmatch 'API 等值') {
    throw 'Quota detail must render a responsive left-planet/right-detail model galaxy with complete 360-degree star tracks, textual shares, fitted text, and API-equivalent cost.'
}
if ($modelUsageDistributionSource -notmatch 'NeutralOrbitColor\s*=\s*Color\.FromArgb\(96,\s*114,\s*146\)' -or
    $modelUsageDistributionSource -notmatch 'PlanetOrbitStrokeRatio\s*=\s*0\.065F' -or
    $modelUsageDistributionSource -notmatch 'PlanetUsageStrokeRatio\s*=\s*0\.68F' -or
    $modelUsageDistributionSource -notmatch 'PlanetUsageMinimumDpiWidth\s*=\s*7\.00F' -or
    $modelUsageDistributionSource -notmatch 'PlanetUsageMaximumDpiWidth\s*=\s*12\.00F' -or
    $modelUsageDistributionSource -notmatch 'CalculatePlanetUsageStroke' -or
    $modelUsageDistributionSource -notmatch 'graphics\.DrawEllipse\(halo,\s*bounds\)' -or
    $modelUsageDistributionSource -notmatch 'DrawCentralGlassPlanet' -or
    $modelUsageDistributionSource -match 'DrawTonalTrack' -or
    $modelUsageDistributionSource -notmatch 'var hueShift\s*=\s*-4F\s*\*\s*MathF\.Sin\(radians\)' -or
    $modelUsageDistributionSource -notmatch 'maximumHueDistance\s*>\s*5F' -or
    $modelUsageDistributionSource -notmatch 'MaximumPlanetDustCount\s*=\s*4' -or
    [string]::IsNullOrWhiteSpace($modelPlanetDustMethod) -or
    $modelPlanetDustMethod -notmatch '2\s*\+\s*\(int\)Math\.Floor\(sweep\s*/\s*150F\)' -or
    $modelPlanetDustMethod -notmatch 'GetEllipsePoint' -or
    $modelPlanetDustMethod -notmatch 'graphics\.FillEllipse' -or
    $modelPlanetDustMethod -match 'DashStyle|DashPattern' -or
    $modelUsageDistributionSource -match 'PlanetCloudBandCount|UpperCloudDashPattern|LowerCloudDashPattern|OrbitalGrainDashPattern|DashStyle\.Custom|DashPattern\s*=' -or
    $modelOrbitingHighlightReferenceCount -ne 3 -or
    [string]::IsNullOrWhiteSpace($modelOrbitingHighlightMethod) -or
    $modelOrbitingHighlightMethod -match 'new Pen\(|graphics\.DrawArc|\.Color\s*=' -or
    [regex]::Matches($modelOrbitingHighlightMethod, 'new SolidBrush\(').Count -ne 3 -or
    [regex]::Matches($modelOrbitingHighlightMethod, 'graphics\.FillEllipse\(').Count -lt 5 -or
    $modelOrbitingHighlightMethod -notmatch 'GetEllipsePoint' -or
    $modelUsageDistributionSource -notmatch 'const int runtimeWidth\s*=\s*825' -or
    $modelUsageDistributionSource -notmatch 'const int runtimeHeight\s*=\s*526' -or
    $modelUsageDistributionSource -notmatch 'const float runtimeDpi\s*=\s*2F' -or
    $modelUsageDistributionSource -notmatch '_validationDpiOverride\s*=\s*runtimeDpi' -or
    $modelUsageDistributionSource -notmatch 'runtimeControl\.OnPaint\(' -or
    [string]::IsNullOrWhiteSpace($modelTonalArcMethod) -or
    [regex]::Matches($modelTonalArcMethod, 'new Pen\(').Count -ne 2 -or
    $modelTonalArcMethod -notmatch 'segmentPen\.Color\s*=\s*color' -or
    $modelUsageDistributionSource -notmatch 'graphics\.IsVisible\(layout\.Title\)' -or
    $modelUsageDistributionSource -notmatch 'graphics\.IsVisible\(layout\.Donut\)' -or
    $modelUsageDistributionSource -notmatch 'graphics\.IsVisible\(layout\.Table\)' -or
    $modelUsageDistributionSource -notmatch 'private static bool IsDrawableBounds\(RectangleF bounds\)' -or
    $modelUsageDistributionSource -notmatch 'private static bool IsFinitePositive\(float value\)' -or
    $modelUsageDistributionSource -notmatch 'private static bool AreFinite\(float first, float second' -or
    [regex]::Matches($modelUsageDistributionSource, '!IsDrawableBounds\(').Count -lt 8 -or
    [regex]::Matches($modelUsageDistributionSource, '!AreFinite\(').Count -lt 6) {
    throw 'Model distribution must keep perspective full-circle planet orbits, a deep-space glass planet, same-family gradients, nebula/stars, one brush-only particle comet per ring, true clipped OnPaint tests, and finite GDI inputs.'
}
if ($passiveQuotaMonitorSource -notmatch 'PassiveQuotaModelUsage' -or
    $passiveQuotaMonitorSource -notmatch 'Dictionary<string, ModelTrendAccumulator>' -or
    $passiveQuotaMonitorSource -notmatch 'NormalizeTrendModel' -or
    $quotaDashboardControlsSource -notmatch 'QuotaChartModelUsage' -or
    $quotaDashboardControlsSource -notmatch 'DrawStackedModelSeries' -or
    $quotaDashboardControlsSource -notmatch 'BuildModelSeries' -or
    $quotaDashboardControlsSource -notmatch 'API 等值' -or
    $quotaDashboardControlsSource -notmatch '该时间段没有用量' -or
    $formSource -notmatch 'GetQuotaTrendBucketSize\(TimeSpan range\)[\s\S]*?TimeSpan\.FromHours\(24\)\) return TimeSpan\.FromMinutes\(15\)' -or
    $formSource -notmatch 'GetQuotaTrendLeadingContextDuration\(TimeSpan range\)' -or
    $formSource -notmatch 'TimeSpan\.FromHours\(24\)\) return TimeSpan\.FromHours\(1\)' -or
    $formSource -notmatch 'timestamp\s*<\s*stableThroughUtc\s*&&\s*samples\.Count\s*<\s*expectedCount' -or
    $quotaDashboardControlsSource -notmatch 'GetSampleCenter' -or
    $quotaDashboardControlsSource -notmatch 'GetSampleEnd' -or
    $quotaDashboardControlsSource -notmatch 'FindSampleIndexAtX' -or
    $formSource -notmatch 'QuotaChartModelUsageEqual') {
    throw 'Quota trend must use fixed time buckets, show non-cumulative per-model API-equivalent areas, retain an API-equivalent range total, and compare nested model data structurally.'
}
if ($resetSessionSource -notmatch 'public sealed record UsageCreditsSnapshot' -or
    $resetSessionSource -notmatch 'public sealed record UsageSpendControl' -or
    -not ($resetSessionSource.Contains('result["rateLimitsByLimitId"]')) -or
    -not ($resetSessionSource.Contains('byLimitId["codex"]')) -or
    $resetSessionSource -notmatch 'return result\["rateLimits"\] as JsonObject' -or
    -not ($resetSessionSource.Contains('rateLimits?["credits"]')) -or
    -not ($resetSessionSource.Contains('rateLimits?["individualLimit"]')) -or
    -not ($resetSessionSource.Contains('rateLimits?["planType"]')) -or
    $resetSessionSource -notmatch 'ParseCreditsSnapshot' -or
    $resetSessionSource -notmatch 'ParseSpendControl' -or
    $resetSessionSource -notmatch 'parsed\.CreditBalance is not \{ HasCredits: true, Unlimited: false, Balance: "12\.50" \}' -or
    $resetSessionSource -notmatch 'parsed\.IndividualLimit is not \{ Limit: "50\.00", Used: "20\.00", RemainingPercent: 60 \}' -or
    $resetSessionSource -notmatch 'parsed\.PlanType != "business"' -or
    $programSource -notmatch 'UsageLimitResetSession\.ValidateProtocolParsing\(\)') {
    throw 'Official quota parsing must prefer the codex rateLimitsByLimitId entry and retain Credits, individualLimit, and planType.'
}
if (-not ($usageTrackerSource.Contains('rateLimits?["credits"]')) -or
    -not ($usageTrackerSource.Contains('rateLimits?["individual_limit"]')) -or
    -not ($usageTrackerSource.Contains('"plan_type"')) -or
    $usageTrackerSource -notmatch 'CreditBalance is not \{ HasCredits: true, Unlimited: false, Balance: "3\.25" \}' -or
    $usageTrackerSource -notmatch 'IndividualLimit is not \{ Limit: "20\.00", Used: "4\.00", RemainingPercent: 80 \}') {
    throw 'Local token_count snapshots must retain official Credits and individual spend-control metadata.'
}
if ($formSource -notmatch 'QueryQuotaOnceAfterExplicitLoginAsync' -or
    $formSource -notmatch 'AccountQuotaLimitType\.FiveHourAndWeekly' -or
    $formSource -notmatch 'AccountQuotaLimitType\.WeeklyOnly' -or
    $formSource -notmatch 'AccountQuotaWindowKind\.Weekly' -or
    $formSource -notmatch 'AccountQuotaLimitType\.Monthly' -or
    $formSource -notmatch 'GetQuotaWindow\(' -or
    $resetSessionSource -notmatch 'windowDurationMins' -or
    $resetSessionSource -notmatch 'UsageRateLimitWindow' -or
    $accountStoreSource -notmatch 'QuotaPrimaryWindowMinutes') {
    throw 'Quota UI must auto-detect and display monthly versus simultaneous 5h/weekly official windows.'
}
if ($accountRecordSource -notmatch 'public const string WeeklyOnly = "weekly_only"' -or
    $accountRecordSource -notmatch 'public const string FiveHourOnly = "five_hour_only"' -or
    $accountRecordSource -notmatch '>= 9_000 and <= 11_000 => AccountQuotaWindowKind\.Weekly' -or
    $accountRecordSource -notmatch 'hasFiveHour && hasWeekly' -or
    $usageTrackerSource -notmatch 'GetQuotaWindow\(AccountQuotaWindowKind kind\)' -or
    $usageTrackerSource -notmatch 'IsSecondary: true' -or
    $formSource -notmatch 'hasCurrentWindow \? AccountQuotaLimitType\.Unknown : account\.QuotaLimitType') {
    throw 'Quota windows must be classified by duration on either primary/secondary field, including weekly-only and 5h-only responses.'
}
if ([regex]::Matches($formSource, 'GetQuotaUsageMetrics\(').Count -lt 4 -or
    [regex]::Matches($formSource, 'GetQuotaListUsageMetrics\(').Count -lt 3 -or
    $formSource -notmatch '\("今天", usage\.Day\)' -or
    $formSource -notmatch '\("本周", usage\.Week\)' -or
    $formSource -notmatch '\("本月", usage\.Month\)' -or
    $formSource -notmatch '\("5h", usage\.FiveHours\)' -or
    $formSource -notmatch 'GetQuotaListUsageMetrics\(usage\)' -or
    $formSource -match 'var buckets = new\[\] \{ usage\.FiveHours, usage\.Day, usage\.Week \}') {
    throw 'Quota home cards must use 5h-today-1week, while detail summaries localize 1d as today and retain the applicable longer window.'
}
$usageMetricLayout = [regex]::Match(
    $formSource,
    '(?s)private Control MakeUsageMetric\(.*?(?=\r?\n\s*private Control MakeQuotaDetailMetric)')
if (-not $usageMetricLayout.Success -or
    $formSource -notmatch 'var metricTop = stacked\s*\?\s*154\s*:\s*CenterQuotaRowContent\(rowHeight, usageMetricHeight\)' -or
    $formSource -notmatch 'const int usageMetricHeight\s*=\s*134' -or
    $usageMetricLayout.Value -notmatch 'var captionLabel = new PillLabel' -or
    $usageMetricLayout.Value -notmatch 'Radius\s*=\s*14' -or
    $usageMetricLayout.Value -notmatch 'Elevation\s*=\s*2' -or
    [regex]::Matches($usageMetricLayout.Value, 'TextAlign = ContentAlignment\.MiddleLeft').Count -lt 2 -or
    [regex]::Matches($usageMetricLayout.Value, 'UseCompatibleTextRendering = true').Count -lt 3 -or
    [regex]::Matches($usageMetricLayout.Value, 'AutoEllipsis = false').Count -lt 3) {
    throw 'Quota account text and 5h/day/week metric cards must share one vertical center and reserve complete high-DPI text rows.'
}
if ($formSource -notmatch 'CreateWorkspaceLoadingState\([\s\S]*?centered:\s*true' -or
    $formSource -notmatch 'TextAlign\s*=\s*centered \? ContentAlignment\.MiddleCenter : ContentAlignment\.MiddleLeft' -or
    $formSource -notmatch 'AutoEllipsis\s*=\s*!centered' -or
    $formSource -notmatch 'UseCompatibleTextRendering\s*=\s*true') {
    throw 'First-load workspace status text must remain complete and horizontally centered at high DPI.'
}
if ($accountDialogSource -notmatch '_homeShell\.Padding\s*=\s*new Padding\(18, 9, 12, 8\)' -or
    $accountDialogSource -notmatch '_secretNote\.Dock\s*=\s*DockStyle\.Fill' -or
    $accountDialogSource -notmatch '_secretNote\.UseCompatibleTextRendering\s*=\s*true' -or
    $accountDialogSource -notmatch 'MeasureInfoCardHeight' -or
    $accountDialogSource -notmatch 'ValidateRuntimeScaledModeSwitch' -or
    $accountDialogSource -match 'ClientSize\s*=\s*new Size\(960, isApi' -or
    $modernUiControlsSource -notmatch 'var margin\s*=\s*Math\.Max\(10, \(int\)Math\.Round\(DeviceDpi / 12D\)\)') {
    throw 'The add-account dialog must preserve the drive-letter glyph and all three Token guidance lines at high DPI.'
}
if ($passiveQuotaMonitoringSource -notmatch 'StartingWindowMinutes' -or
    $passiveQuotaMonitoringSource -notmatch 'IsEstimateApplicable' -or
    $passiveQuotaMonitoringSource -notmatch 'AccountQuotaWindowKind\.Weekly' -or
    $passiveQuotaMonitorSource -notmatch 'ResolveProfile\(account, usage, orderedEvents\)' -or
    $passiveQuotaMonitorSource -notmatch 'GetEventQuotaWindow\(item, trendWindowKind\)' -or
    $passiveQuotaMonitorSource -notmatch 'weeklyWindowNormal\.ThresholdUsd != 90D' -or
    $passiveQuotaMonitorSource -notmatch 'Math\.Abs\(weeklyWindowNormalTotal - 160D\)' -or
    $passiveQuotaMonitorSource -notmatch 'Math\.Abs\(weeklyWindowAbnormalTotal - 40D\)' -or
    $passiveQuotaMonitorSource -notmatch 'Math\.Abs\(weeklyWindowBoundaryTotal - 90D\)') {
    throw 'Passive quota monitoring must invalidate stale window estimates and classify weekly-only capacity with the natural-usage $90 threshold.'
}
if ($formSource -notmatch 'MeasureActionButtonWidth\("查询重置次数", 184\)' -or
    $formSource -notmatch 'var controlHeight\s*=\s*compact \? 42 : 54' -or
    $formSource -notmatch 'queryResetCount\.Height\s*=\s*controlHeight' -or
    $formSource -notmatch 'queryResetCount\.UseMnemonic\s*=\s*false') {
    throw 'Usage reset controls must size from the rendered text so high-DPI Chinese labels are not clipped.'
}
$switchWindowsClientMatch = [regex]::Match(
    $cliServiceSource,
    '(?s)public async Task<WindowsClientAccountProjection> SwitchWindowsClientAccountAsync\(.*?(?=\r?\n\s*private async Task<LoginStatus> ValidateWindowsClientAccountAsync)')
$launchWindowsClientMatch = [regex]::Match(
    $cliServiceSource,
    '(?s)public bool LaunchWindowsClient\(.*?(?=\r?\n\s*public void RepairCodexPlusPlusScheduledTask)')
$launchOfficialCodexMatch = [regex]::Match(
    $cliServiceSource,
    '(?s)private static bool LaunchOfficialCodex\(.*?(?=\r?\n\s*internal static ProcessStartInfo BuildOfficialCodexActivationStartInfo)')
$openWindowsClientThreadMatch = [regex]::Match(
    $cliServiceSource,
    '(?s)public Task OpenWindowsClientThreadAsync\(.*?(?=\r?\n\s*public void OpenWindowsClientThread)')
$taskLauncherScriptMatch = [regex]::Match(
    $cliServiceSource,
    '(?s)private static string BuildCodexPlusPlusTaskLauncherScript\(\).*?(?=\r?\n\s*internal static void ValidateCodexPlusPlusTaskLauncherScript)')
$launchAccountMatch = [regex]::Match(
    $formSource,
    '(?s)private async Task LaunchAccountAsync\(.*?(?=\r?\n\s*private async Task LaunchCliAccountAsync)')
if (-not $switchWindowsClientMatch.Success -or
    -not $launchWindowsClientMatch.Success -or
    -not $launchOfficialCodexMatch.Success -or
    -not $openWindowsClientThreadMatch.Success -or
    -not $taskLauncherScriptMatch.Success -or
    -not $launchAccountMatch.Success) {
    throw 'Could not isolate the desktop switch, launch, chat-open, and UI launch methods for safety assertions.'
}
$switchWindowsClientMethod = $switchWindowsClientMatch.Value
$launchWindowsClientMethod = $launchWindowsClientMatch.Value
$launchOfficialCodexMethod = $launchOfficialCodexMatch.Value
$openWindowsClientThreadMethod = $openWindowsClientThreadMatch.Value
$taskLauncherScriptMethod = $taskLauncherScriptMatch.Value
$launchAccountMethod = $launchAccountMatch.Value

if ($cliServiceSource -notmatch 'LaunchWindowsClient' -or
    $cliServiceSource -notmatch 'ResolveCodexPlusPlusLauncherPath' -or
    $cliServiceSource -notmatch 'CODEX_PLUS_PLUS_LAUNCHER_PATH' -or
    $cliServiceSource -notmatch 'codex-plus-plus\.exe' -or
    $cliServiceSource -notmatch 'CodexAccountManagerCodexPlusPlus' -or
    $cliServiceSource -notmatch 'TryLaunchCodexPlusPlusViaScheduledTask' -or
    $cliServiceSource -notmatch 'WaitForCodexPlusPlusTaskResult' -or
    $cliServiceSource -notmatch 'codex-plus-plus-launch-result\.json' -or
    $cliServiceSource -notmatch 'InstallCodexPlusPlusScheduledTaskElevated' -or
    $cliServiceSource -notmatch 'ScheduledTaskUsesHiddenPowerShell' -or
    $cliServiceSource -notmatch 'RepairCodexPlusPlusScheduledTask' -or
    $cliServiceSource -notmatch 'CodexPlusPlusTaskHiddenWindowArgument' -or
    $cliServiceSource -notmatch '-WindowStyle Hidden' -or
    $cliServiceSource -notmatch '-NonInteractive' -or
    $cliServiceSource -notmatch 'schtasks\.exe' -or
    $cliServiceSource -notmatch '--app-path' -or
    $cliServiceSource -notmatch 'Path\.Combine\(dir, "app", "ChatGPT\.exe"\)' -or
    $cliServiceSource -notmatch 'PackageRootFolder' -or
    $cliServiceSource -notmatch 'ValidateWindowsClientResolution' -or
    $formSource -notmatch 'LaunchCliAccount' -or
    $formSource -notmatch 'MakeLaunchTonalButton\("CLI"') {
    throw 'Codex++ discovery, explicit legacy task repair, official Codex resolution, and the CLI fallback must remain available.'
}
if ($cliServiceSource -notmatch 'AccessTokenSwitchValidationCacheLifetime' -or
    $cliServiceSource -notmatch 'AccessTokenSwitchValidationCacheEntry' -or
    $cliServiceSource -notmatch 'CompatibleApiPreflightCacheFileName' -or
    $cliServiceSource -notmatch 'HasFreshCompatibleApiPreflight' -or
    $cliServiceSource -notmatch 'BuildAccountValidationFingerprint' -or
    $cliServiceSource -notmatch 'return await Task\.Run' -or
    $programSource -notmatch '--repair-codex-plus-plus-task') {
    throw 'Explicit status checks must retain validation caches, switching must stay off the UI thread, and legacy task repair must remain explicit only.'
}
if ($switchWindowsClientMethod -notmatch 'ValidateWindowsClientAccountAsync\(\s*account,\s*localOnly:\s*true,\s*accessTokenMode:\s*AccessTokenSharedProfileMode\.ApiCompatible\)' -or
    $switchWindowsClientMethod -notmatch 'sharedProfileAlreadySelected' -or
    $switchWindowsClientMethod -notmatch 'CreateReusedSharedProfileProjection' -or
    $switchWindowsClientMethod -notmatch 'if\s*\(sharedProfileAlreadySelected\)' -or
    $switchWindowsClientMethod -notmatch 'RequiresWindowsClientShutdown\(sharedProfileAlreadySelected\)' -or
    $switchWindowsClientMethod -match 'if\s*\(sharedProfileAlreadySelected\)[\s\S]*?StopWindowsClientProcesses[\s\S]*?CreateReusedSharedProfileProjection' -or
    $switchWindowsClientMethod -notmatch 'else[\s\S]*?StopWindowsClientProcesses\(shutdownTargets\)' -or
    $switchWindowsClientMethod -notmatch 'projection\.ClientLaunchStarted\s*=\s*LaunchWindowsClient\(' -or
    $switchWindowsClientMethod -notmatch 'projection\.ClientLaunchError' -or
    $switchWindowsClientMethod -notmatch 'must not silently restore the old account' -or
    $switchWindowsClientMethod -match 'SharedHistoryMerger\.Merge') {
    throw 'Switching must use a same-profile fast path, project only on a real account change, and never roll credentials back after a launcher failure.'
}
if ($launchWindowsClientMethod -notmatch 'TryLaunchCodexPlusPlusViaScheduledTask' -or
    $launchWindowsClientMethod -notmatch 'LaunchOfficialCodex' -or
    $launchWindowsClientMethod -notmatch 'WindowsClientMode\.OfficialCodex' -or
     $launchWindowsClientMethod -notmatch 'OpenNewTaskAfterOfficialCodexLaunchInBackground' -or
    $launchWindowsClientMethod -notmatch 'OpenNewTaskAfterCodexPlusPlusLaunchInBackground' -or
    $launchWindowsClientMethod -notmatch 'RedirectStandardError\s*=\s*true' -or
    $launchWindowsClientMethod -notmatch 'WindowStyle\s*=\s*ProcessWindowStyle\.Hidden' -or
    $cliServiceSource -notmatch 'CodexPlusPlusLaunchReadyTimeout\s*=\s*TimeSpan\.FromSeconds\(120\)' -or
    $cliServiceSource -notmatch 'CodexPlusPlusLauncherExitProbeTimeout\s*=\s*TimeSpan\.FromMilliseconds\(1500\)' -or
    $cliServiceSource -notmatch 'SetBoolean\("codexAppFastStartup", true\)' -or
    $cliServiceSource -notmatch 'IsCodexPlusPlusReadySince\(earliestStartUtc' -or
    $launchWindowsClientMethod -match 'TimeSpan\.FromSeconds\(20\)' -or
    $launchWindowsClientMethod -notmatch 'catch\s*\(Win32Exception ex\)\s*when\s*\(ex\.NativeErrorCode\s*==\s*740\)' -or
    $launchWindowsClientMethod -notmatch '不会隐式打开管理员 PowerShell' -or
    $launchWindowsClientMethod -match 'StartCodexPlusPlusElevated|Verb\s*=\s*"runas"|new\s+ProcessStartInfo\("powershell\.exe"\)') {
    throw 'The normal Start path must accept Codex++ quickly, finish fresh-window/bridge readiness in the background, enable fast startup, capture launcher errors, and avoid an implicit elevated PowerShell fallback.'
}
if ($launchOfficialCodexMethod -notmatch 'BuildOfficialCodexActivationStartInfo\(projectPath\)' -or
    $cliServiceSource -notmatch 'new ProcessStartInfo\(BuildNewThreadDeepLink\(projectPath\)\)' -or
    $cliServiceSource -notmatch 'ValidateOfficialCodexActivation' -or
    $launchOfficialCodexMethod -match 'new ProcessStartInfo\(clientPath\)' -or
    $launchOfficialCodexMethod -match 'UseShellExecute\s*=\s*false' -or
    $launchOfficialCodexMethod -match 'startInfo\.Environment\[' -or
    $launchOfficialCodexMethod -match 'OpenNewTaskAfterWindowsClientLaunchInBackground') {
    throw 'The official MSIX Codex client must be activated through codex:// and never started directly from the protected WindowsApps executable.'
}
if ($settingsSource -notmatch 'bool UseCodexDreamSkin' -or
    $formSource -notmatch '_appSettings\.UseCodexDreamSkin' -or
    $cliServiceSource -notmatch 'CodexDreamSkinService\.Start\(\)' -or
    $cliServiceSource -notmatch 'ApplyCodexDreamSkinAsync' -or
    $cliServiceSource -notmatch 'RestoreOfficialCodexAppearanceAsync' -or
    $dreamSkinServiceSource -notmatch 'install-account-manager-theme\.ps1' -or
    $dreamSkinServiceSource -notmatch 'set-account-manager-dream-theme\.ps1' -or
    $dreamSkinServiceSource -notmatch 'start-dream-skin\.ps1' -or
    $dreamSkinServiceSource -notmatch 'restore-dream-skin\.ps1' -or
    $dreamSkinServiceSource -notmatch 'CreateNoWindow\s*=\s*true' -or
    $dreamSkinServiceSource -notmatch 'SecretPattern\.Replace' -or
    $programSource -notmatch 'CodexDreamSkinService\.ValidateBundledRuntime\(\)' -or
    $projectSource -notmatch 'tools\\CodexDreamSkin') {
    throw 'The optional Codex theme must be bundled, launch through hidden validated scripts, and support in-app apply/restore.'
}
if (-not $injectorSource.Contains('function verificationPassed(result)') -or
    -not $injectorSource.Contains('Boolean(result.shell)') -or
    -not $injectorSource.Contains('Boolean(result.main)') -or
    -not $injectorSource.Contains('result.pass = (${verificationPassed.toString()})(result);') -or
    $injectorSource -match "chromePointerEvents === 'none' && Boolean\(result\.composer\)") {
    throw 'Renderer verification must use the stable shell/main/sidebar skeleton and allow a temporarily absent composer during task rendering.'
}
if ($rendererInjectSource -notmatch 'managerMotionThemeIds' -or
    $rendererInjectSource -notmatch 'createManagerMotion' -or
    $rendererInjectSource -notmatch 'resolveArtCover' -or
    $rendererInjectSource -notmatch 'requestAnimationFrame' -or
    $rendererInjectSource -notmatch 'prefers-reduced-motion:\s*reduce' -or
    $rendererInjectSource -notmatch 'document\.hidden' -or
    $rendererInjectSource -notmatch 'window\.addEventListener\?\.\("blur"' -or
    $rendererInjectSource -notmatch 'previous\?\.motion\?\.destroy' -or
    $rendererInjectSource -notmatch 'radiusX:\s*520,\s*radiusY:\s*218' -or
    $dreamSkinCssSource -notmatch 'dream-manager-motion #codex-dream-skin-chrome' -or
    $dreamSkinCssSource -notmatch '#codex-dream-skin-motion' -or
    $dreamSkinCssSource -notmatch 'pointer-events:\s*none' -or
    $injectorSource -notmatch 'motionCanvasPointerEvents' -or
    $injectorSource -notmatch 'state\?\.motion\?\.snapshot') {
    throw 'Account Manager Codex themes must ship a non-interactive, palette-aware meteor/orbit Canvas with pause, reduced-motion, alignment, cleanup, and injector verification.'
}
if ($cliServiceSource -notmatch 'OfficialClientRelaunched' -or
    $cliServiceSource -notmatch 'CodexDreamSkinFailed' -or
    $cliServiceSource -notmatch 'catch\s*\(CodexDreamSkinApplyException ex\)' -or
    $formSource -notmatch 'projection\.CodexDreamSkinFailed' -or
    $formSource -notmatch '_appSettings\.UseCodexDreamSkin\s*=\s*false') {
    throw 'A theme launch failure must be recorded, disable repeated startup synchronization, and preserve a usable official-client fallback.'
}
$dreamSkinBundle = Join-Path $root 'tools\CodexDreamSkin'
$dreamThemeScript = Join-Path $dreamSkinBundle 'scripts\set-account-manager-dream-theme.ps1'
$appearanceScript = Join-Path $dreamSkinBundle 'scripts\set-account-manager-codex-appearance.ps1'
$dreamThemeScriptSource = Get-Content -LiteralPath $dreamThemeScript -Raw -Encoding UTF8
$appearanceScriptSource = Get-Content -LiteralPath $appearanceScript -Raw -Encoding UTF8
foreach ($dreamSkinRequired in @(
    (Join-Path $dreamSkinBundle 'bundle-version.txt'),
    (Join-Path $dreamSkinBundle 'assets\account-manager-nebula.jpg'),
    (Join-Path $dreamSkinBundle 'assets\account-manager-aurora-light.jpg'),
    (Join-Path $dreamSkinBundle 'assets\account-manager-porcelain-light.jpg'),
    (Join-Path $dreamSkinBundle 'assets\account-manager-deep-sea.jpg'),
    (Join-Path $dreamSkinBundle 'assets\account-manager-nebula-orbit.jpg'),
    (Join-Path $dreamSkinBundle 'assets\account-manager-nebula-theme.json'),
    (Join-Path $dreamSkinBundle 'assets\UPSTREAM-PRESETS-NOTICE.md'),
    (Join-Path $dreamSkinBundle 'assets\PRESET-PROVENANCE.md'),
    (Join-Path $dreamSkinBundle 'scripts\install-account-manager-theme.ps1'),
    $appearanceScript,
    $dreamThemeScript,
    (Join-Path $dreamSkinBundle 'scripts\start-dream-skin.ps1'),
    (Join-Path $dreamSkinBundle 'scripts\restore-dream-skin.ps1'),
    (Join-Path $dreamSkinBundle 'scripts\renderer-motion-self-test.mjs'),
    (Join-Path $dreamSkinBundle 'scripts\renderer-motion-browser-self-test.mjs')
)) {
    if (-not (Test-Path -LiteralPath $dreamSkinRequired -PathType Leaf)) {
        throw "Missing bundled Codex theme resource: $dreamSkinRequired"
    }
}
$managerArtworkByPreset = [ordered]@{
    'manager-light' = 'account-manager-aurora-light.jpg'
    'manager-porcelain-light' = 'account-manager-porcelain-light.jpg'
    'manager-dark' = 'account-manager-deep-sea.jpg'
    'manager-nebula-dark' = 'account-manager-nebula-orbit.jpg'
}
$managerArtworkHashes = @()
foreach ($entry in $managerArtworkByPreset.GetEnumerator()) {
    $mappingText = "'$($entry.Key)' { '$($entry.Value)' }"
    if (-not $dreamThemeScriptSource.Contains($mappingText)) {
        throw "Account Manager Codex theme does not map to its own wallpaper: $mappingText"
    }
    $managerArtworkHashes += (Get-FileHash `
        -LiteralPath (Join-Path $dreamSkinBundle ('assets\' + $entry.Value)) `
        -Algorithm SHA256).Hash
}
if (@($managerArtworkHashes | Select-Object -Unique).Count -ne $managerArtworkByPreset.Count) {
    throw 'The four Account Manager Codex themes must use four visually distinct wallpaper assets.'
}
if ($themeArtworkRendererSource -match 'DrawStarBurst' -or
    $themeArtworkRendererSource -notmatch 'DrawAtmosphericCloudTexture' -or
    $themeArtworkRendererSource -notmatch 'case\s+"manager-porcelain-light"[\s\S]*?ringFirstMoon\s*=\s*true' -or
    $formSource -match 'var\s+shortRay\s*=') {
    throw 'Bundled Account Manager planets must use distinct atmospheric materials and circular meteor particles without cross-shaped stars.'
}
if (-not $dreamThemeScriptSource.Contains("background = '#FBFCFF'; panel = '#FFFFFF'; panelAlt = '#F7F9FF'") -or
    $appearanceScriptSource -notmatch 'appearanceLightChromeTheme\s*=.*surface\s*=\s*"#FFFFFF"' -or
    $formSource -notmatch '"manager-light"[\s\S]*?"#FFFFFF"') {
    throw 'The Aurora Light Codex theme must keep a neutral white work surface instead of the old blue wash.'
}
$publicDreamSkinPresetIds = @(
    'preset-arina-hashimoto',
    'preset-gothic-void-crusade',
    'preset-midnight-aurora',
    'preset-sakura-dawn',
    'preset-amber-dusk',
    'preset-forest-mist',
    'preset-cyber-neon'
)
foreach ($presetId in $publicDreamSkinPresetIds) {
    $presetDirectory = Join-Path $dreamSkinBundle ("assets\presets\{0}" -f $presetId)
    $presetBackground = Join-Path $presetDirectory 'background.jpg'
    $presetThemePath = Join-Path $presetDirectory 'theme.json'
    $presetPreview = Join-Path $dreamSkinBundle ("assets\presets\{0}.jpg" -f $presetId)
    foreach ($presetResource in @($presetBackground, $presetThemePath, $presetPreview)) {
        if (-not (Test-Path -LiteralPath $presetResource -PathType Leaf) -or
            (Get-Item -LiteralPath $presetResource).Length -le 0) {
            throw "Bundled public Codex theme is incomplete: $presetResource"
        }
    }
    try {
        $presetTheme = [IO.File]::ReadAllText($presetThemePath) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Bundled public Codex theme metadata is invalid JSON: ${presetThemePath}: $($_.Exception.Message)"
    }
    if ([int]$presetTheme.schemaVersion -ne 1 -or
        [string]$presetTheme.id -cne $presetId -or
        [string]$presetTheme.image -cne 'background.jpg') {
        throw "Bundled public Codex theme metadata does not match its complete nested pack: $presetId"
    }
}
$githubDreamSkinPresetIds = @(
    'preset-arina-hashimoto',
    'preset-gothic-void-crusade'
)
foreach ($presetId in $githubDreamSkinPresetIds) {
    $runtimeBackground = Join-Path $dreamSkinBundle ("assets\presets\{0}.jpg" -f $presetId)
    $staticPreview = Join-Path $dreamSkinBundle ("assets\presets\{0}-preview.jpg" -f $presetId)
    if (-not (Test-Path -LiteralPath $staticPreview -PathType Leaf) -or
        (Get-Item -LiteralPath $staticPreview).Length -le 0 -or
        (Get-FileHash -LiteralPath $staticPreview -Algorithm SHA256).Hash -ceq
            (Get-FileHash -LiteralPath $runtimeBackground -Algorithm SHA256).Hash) {
        throw "GitHub preset must keep its runtime wallpaper and UI screenshot preview separate: $presetId"
    }
}
if ($dreamThemeScriptSource -notmatch "ValidateSet\('manager-light'.*'preset-arina-hashimoto'.*'preset-gothic-void-crusade'.*'preset-midnight-aurora'.*'preset-sakura-dawn'.*'preset-amber-dusk'.*'preset-forest-mist'.*'preset-cyber-neon'.*'custom'\)" -or
    $dreamThemeScriptSource -notmatch '\$custom\.BackgroundImagePath' -or
    $dreamThemeScriptSource -notmatch 'Join-Path \$assetRoot \(''presets\\'' \+ \$PresetId\)' -or
    $dreamThemeScriptSource -notmatch 'Read-DreamSkinTheme\s+-ThemeDirectory\s+\$themeDirectory' -or
    $dreamThemeScriptSource -notmatch 'Set-DreamSkinActiveTheme\s+-ImagePath\s+\$imagePath') {
    throw 'Dream Skin theme generation must accept every public pack plus a real custom background image and publish an active theme.'
}
if ($settingsSource -notmatch 'string\? BackgroundImagePath' -or
    $settingsSource -notmatch 'BackgroundImagePath\s*=\s*BackgroundImagePath' -or
    $customCodexThemeDialogSource -notmatch 'OpenFileDialog' -or
    $customCodexThemeDialogSource -notmatch '_chooseBackgroundButton\.Text\s*=' -or
    $customCodexThemeDialogSource -notmatch '_backgroundThumbnail' -or
    $customCodexThemeDialogSource -notmatch '16\s*\*\s*1024\s*\*\s*1024' -or
    $customCodexThemeDialogSource -notmatch 'SetBackgroundImage\(_backgroundImagePath\)' -or
    $formSource -notmatch '_appSettings\.CustomCodexTheme\.BackgroundImagePath' -or
    $dreamSkinServiceSource -notmatch 'SaveManagedCustomBackground' -or
    $dreamSkinServiceSource -notmatch '"custom-assets"' -or
    $dreamSkinServiceSource -notmatch 'theme\.BackgroundImagePath\s*=\s*SaveManagedCustomBackground' -or
    $installerDefaultsSource -notmatch '"BackgroundImagePath"\s*:\s*null') {
    throw 'Custom Codex themes must persist a validated local photo, preview it in a Codex frame, copy it to managed assets, and keep installer defaults private.'
}

$applyAndStartDreamSkinMethod = [regex]::Match(
    $cliServiceSource,
    '(?s)private static void ApplyAndStartDreamSkinOrRestore\(.*?(?=\r?\n\s*public Task<bool> RestoreOfficialCodexAppearanceAsync\()')
if (-not $applyAndStartDreamSkinMethod.Success -or
    $applyAndStartDreamSkinMethod.Value -notmatch 'CodexDreamSkinService\.Install\(\);[\s\S]*?CodexDreamSkinService\.ApplyAppearance\([\s\S]*?CodexDreamSkinService\.Start\(\);' -or
    $applyAndStartDreamSkinMethod.Value -notmatch 'catch\s*\(Exception applyError\)[\s\S]*?CodexDreamSkinService\.RestoreOfficialAppearance\(\)' -or
    $applyAndStartDreamSkinMethod.Value -notmatch 'if\s*\(restoreError\s*!=\s*null\)' -or
    $applyAndStartDreamSkinMethod.Value -notmatch 'innerException:\s*applyError') {
    throw 'Applying a picture theme must install, configure, and start in order, then attempt official-appearance rollback without hiding the original failure.'
}
$restoreOfficialAppearanceMethod = [regex]::Match(
    $dreamSkinServiceSource,
    '(?s)public static void RestoreOfficialAppearance\(\).*?(?=\r?\n\s*private static void ArchiveCompletedAppearanceBackup\()')
if (-not $restoreOfficialAppearanceMethod.Success -or
    $restoreOfficialAppearanceMethod.Value -notmatch 'RunPowerShellScript\(AppearanceScript,\s*\["-Restore"\][\s\S]*?ArchiveCompletedAppearanceBackup\(\)[\s\S]*?RunPowerShellScript\(restoreScript' -or
    $restoreOfficialAppearanceMethod.Value -notmatch 'RunPowerShellScript\(restoreScript[\s\S]*?File\.Delete\(AppearanceMarkerPath\)' -or
    $dreamSkinServiceSource -notmatch 'File\.Move\(\s*AppearanceConfigBackupPath') {
    throw 'Official appearance restore must unwind A to B before B to O, retire completed backups, and preserve its marker until restoration succeeds.'
}

# Exercise the two-layer config transaction entirely under the suite temp root. The real
# ~/.codex/config.toml and %LOCALAPPDATA% Dream Skin state must never be involved here.
. (Join-Path $dreamSkinBundle 'scripts\config-utf8.ps1')
$appearanceSemanticsRoot = Join-Path $tempRoot 'dream-skin-appearance-semantics'
$appearanceSemanticsState = Join-Path $appearanceSemanticsRoot 'state'
$appearanceSemanticsConfig = Join-Path $appearanceSemanticsRoot 'config.toml'
New-Item -ItemType Directory -Force -Path $appearanceSemanticsState | Out-Null

function Invoke-AccountManagerAppearanceRoundTrip {
    param(
        [Parameter(Mandatory = $true)][string]$RoundId,
        [Parameter(Mandatory = $true)][string]$OriginalContent,
        [Parameter(Mandatory = $true)][string]$ChineseSentinel,
        [Parameter(Mandatory = $true)][string]$PresetId,
        [switch]$WithBom
    )

    $dreamBackup = Join-Path $appearanceSemanticsState 'config.before-dream-skin.toml'
    $appearanceBackup = Join-Path $appearanceSemanticsState 'config.before-account-manager-appearance.toml'
    foreach ($activeBackup in @($dreamBackup, $appearanceBackup)) {
        if (Test-Path -LiteralPath $activeBackup) {
            throw "Round $RoundId began with a stale active backup: $activeBackup"
        }
    }

    $utf8NoBom = [Text.UTF8Encoding]::new($false, $true)
    [byte[]]$originalBytes = $utf8NoBom.GetBytes($OriginalContent)
    if ($WithBom) {
        $originalBytes = [byte[]]([Text.UTF8Encoding]::new($true, $true).GetPreamble() + $originalBytes)
    }
    [IO.File]::WriteAllBytes($appearanceSemanticsConfig, $originalBytes)

    Install-DreamSkinBaseTheme `
        -ConfigPath $appearanceSemanticsConfig `
        -BackupPath $dreamBackup
    $baseBytes = [IO.File]::ReadAllBytes($appearanceSemanticsConfig)
    $baseContent = ConvertFrom-DreamSkinUtf8Bytes -Bytes $baseBytes -Path $appearanceSemanticsConfig
    if (Test-DreamSkinBytesEqual -Left $originalBytes -Right $baseBytes) {
        throw "Round $RoundId did not transition O to the Dream Skin base B."
    }
    if ($baseContent.IndexOf($ChineseSentinel, [StringComparison]::Ordinal) -lt 0 -or
        [regex]::IsMatch($baseContent, '(?<!\r)\n') -or
        $baseContent -notmatch 'appearanceLightCodeThemeId\s*=\s*"codex"') {
        throw "Round $RoundId corrupted Chinese text/CRLF or did not install the Dream Skin base B."
    }
    if (-not (Test-DreamSkinBytesEqual -Left $originalBytes -Right ([IO.File]::ReadAllBytes($dreamBackup)))) {
        throw "Round $RoundId did not preserve the exact O bytes, including any UTF-8 BOM, in the Dream Skin backup."
    }

    # A failed first appearance update must leave B untouched and retain its newly captured
    # backup so a later valid apply/restore remains possible.
    $invalidCustomThemePath = Join-Path $appearanceSemanticsRoot ("invalid-custom-{0}.json" -f $RoundId)
    $invalidCustomTheme = [ordered]@{
        Name = "invalid-$RoundId"
        IsDark = $true
        CodeThemeId = 'tokyo-night'
        AccentColor = 'not-a-color'
        SurfaceColor = '#0D1A16'
        InkColor = '#E8F5EE'
        BackgroundImagePath = $null
        Contrast = 92
    } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($invalidCustomThemePath, $invalidCustomTheme, $utf8NoBom)
    $invalidApplyFailed = $false
    try {
        & $appearanceScript `
            -Mode System `
            -PresetId custom `
            -CustomThemePath $invalidCustomThemePath `
            -ConfigPath $appearanceSemanticsConfig `
            -StateRoot $appearanceSemanticsState | Out-Null
    }
    catch {
        $invalidApplyFailed = $true
    }
    if (-not $invalidApplyFailed -or
        -not (Test-DreamSkinBytesEqual -Left $baseBytes -Right ([IO.File]::ReadAllBytes($appearanceSemanticsConfig))) -or
        -not (Test-DreamSkinBytesEqual -Left $baseBytes -Right ([IO.File]::ReadAllBytes($appearanceBackup)))) {
        throw "Round $RoundId did not retain B and its appearance backup after a failed apply."
    }

    & $appearanceScript `
        -Mode System `
        -PresetId $PresetId `
        -ConfigPath $appearanceSemanticsConfig `
        -StateRoot $appearanceSemanticsState | Out-Null
    $appearanceBytes = [IO.File]::ReadAllBytes($appearanceSemanticsConfig)
    if (Test-DreamSkinBytesEqual -Left $baseBytes -Right $appearanceBytes) {
        throw "Round $RoundId did not transition B to Account Manager appearance A."
    }
    if (-not (Test-DreamSkinBytesEqual -Left $baseBytes -Right ([IO.File]::ReadAllBytes($appearanceBackup)))) {
        throw "Round $RoundId overwrote the original B backup while switching themes."
    }

    # A failed restore must preserve both A and the unusable backup for inspection/retry. The
    # fixture then repairs only its own temp backup and verifies the successful A -> B unwind.
    $savedAppearanceBackupBytes = [IO.File]::ReadAllBytes($appearanceBackup)
    [byte[]]$invalidBackupBytes = @(0xFF, 0xFE, 0x00, 0x81)
    [IO.File]::WriteAllBytes($appearanceBackup, $invalidBackupBytes)
    $invalidRestoreFailed = $false
    try {
        & $appearanceScript `
            -Restore `
            -ConfigPath $appearanceSemanticsConfig `
            -StateRoot $appearanceSemanticsState | Out-Null
    }
    catch {
        $invalidRestoreFailed = $true
    }
    if (-not $invalidRestoreFailed -or
        -not (Test-DreamSkinBytesEqual -Left $appearanceBytes -Right ([IO.File]::ReadAllBytes($appearanceSemanticsConfig))) -or
        -not (Test-DreamSkinBytesEqual -Left $invalidBackupBytes -Right ([IO.File]::ReadAllBytes($appearanceBackup)))) {
        throw "Round $RoundId did not preserve A and its failed restore backup."
    }
    [IO.File]::WriteAllBytes($appearanceBackup, $savedAppearanceBackupBytes)

    & $appearanceScript `
        -Restore `
        -ConfigPath $appearanceSemanticsConfig `
        -StateRoot $appearanceSemanticsState | Out-Null
    if (-not (Test-DreamSkinBytesEqual -Left $baseBytes -Right ([IO.File]::ReadAllBytes($appearanceSemanticsConfig)))) {
        throw "Round $RoundId did not restore A back to the exact B state."
    }

    $appearanceArchive = Join-Path $appearanceSemanticsState ("config.restored-account-manager-appearance-{0}.toml" -f $RoundId)
    [IO.File]::Move($appearanceBackup, $appearanceArchive)
    Restore-DreamSkinBaseTheme `
        -ConfigPath $appearanceSemanticsConfig `
        -BackupPath $dreamBackup
    $restoredBytes = [IO.File]::ReadAllBytes($appearanceSemanticsConfig)
    $restoredContent = ConvertFrom-DreamSkinUtf8Bytes -Bytes $restoredBytes -Path $appearanceSemanticsConfig
    $expectedContent = ConvertFrom-DreamSkinUtf8Bytes -Bytes $originalBytes -Path "expected-$RoundId"
    if ($restoredContent -cne $expectedContent -or
        $restoredContent.IndexOf($ChineseSentinel, [StringComparison]::Ordinal) -lt 0 -or
        [regex]::IsMatch($restoredContent, '(?<!\r)\n')) {
        throw "Round $RoundId did not restore B to semantic O while preserving Chinese text and CRLF."
    }

    $dreamArchive = Join-Path $appearanceSemanticsState ("config.restored-dream-skin-{0}.toml" -f $RoundId)
    Archive-DreamSkinConfigBackup -BackupPath $dreamBackup -ArchivePath $dreamArchive
    if ((Test-Path -LiteralPath $dreamBackup) -or
        (Test-Path -LiteralPath $appearanceBackup) -or
        -not (Test-Path -LiteralPath $dreamArchive -PathType Leaf) -or
        -not (Test-Path -LiteralPath $appearanceArchive -PathType Leaf)) {
        throw "Round $RoundId did not retire completed backups before the next install."
    }
}

$roundOneOriginal = @(
    'model = "gpt-5.6-terra"',
    '# 中文原始配置：第一轮 O',
    '[desktop]',
    'appearanceTheme = "system"',
    'appearanceLightCodeThemeId = "github"',
    'appearanceDarkCodeThemeId = "night-owl"',
    'appearanceLightChromeTheme = { accent = "#123456" }',
    'appearanceDarkChromeTheme = { accent = "#654321" }',
    'customLabel = "保留第一轮"',
    '[features]',
    'remote_compaction = true',
    ''
) -join "`r`n"
Invoke-AccountManagerAppearanceRoundTrip `
    -RoundId 'round-1' `
    -OriginalContent $roundOneOriginal `
    -ChineseSentinel '保留第一轮' `
    -PresetId 'preset-sakura-dawn' `
    -WithBom

$roundTwoOriginal = @(
    'model = "gpt-5.6-sol"',
    '# 第二轮使用新的原始配置 O2，不能复用第一轮 O',
    '[desktop]',
    'appearanceTheme = "dark"',
    'appearanceLightCodeThemeId = "everforest"',
    'appearanceDarkCodeThemeId = "matrix"',
    'appearanceLightChromeTheme = { accent = "#246810" }',
    'appearanceDarkChromeTheme = { accent = "#135790" }',
    'customLabel = "保留第二轮 O2"',
    '[features]',
    'remote_compaction = false',
    ''
) -join "`r`n"
Invoke-AccountManagerAppearanceRoundTrip `
    -RoundId 'round-2' `
    -OriginalContent $roundTwoOriginal `
    -ChineseSentinel '保留第二轮 O2' `
    -PresetId 'preset-forest-mist'

$generatedThemeState = Join-Path $tempRoot 'dream-skin-theme-generation'
foreach ($presetId in $publicDreamSkinPresetIds) {
    & $dreamThemeScript -PresetId $presetId -StateRoot $generatedThemeState | Out-Null
    $activeThemeDirectory = Join-Path $generatedThemeState 'active-theme'
    $activeThemeMetadataPath = Join-Path $activeThemeDirectory 'theme.json'
    $activeTheme = [IO.File]::ReadAllText($activeThemeMetadataPath) | ConvertFrom-Json -ErrorAction Stop
    $activeImageRelativePath = [string]$activeTheme.image
    $activeImagePath = Join-Path $activeThemeDirectory $activeImageRelativePath
    $sourceImagePath = Join-Path $dreamSkinBundle ("assets\presets\{0}\background.jpg" -f $presetId)
    $expectedAppearance = if ($presetId -in @('preset-arina-hashimoto', 'preset-sakura-dawn')) { 'light' } else { 'dark' }
    if ([string]$activeTheme.id -cne $presetId -or
        [string]$activeTheme.appearance -cne $expectedAppearance -or
        [double]$activeTheme.art.focusX -lt 0 -or [double]$activeTheme.art.focusX -gt 1 -or
        [double]$activeTheme.art.focusY -lt 0 -or [double]$activeTheme.art.focusY -gt 1 -or
        [string]$activeTheme.art.safeArea -cne 'left' -or
        [string]$activeTheme.art.taskMode -cne 'ambient' -or
        [string]::IsNullOrWhiteSpace($activeImageRelativePath) -or
        [IO.Path]::IsPathRooted($activeImageRelativePath) -or
        $activeImageRelativePath -match '(^|[\\/])\.\.([\\/]|$)' -or
        -not (Test-Path -LiteralPath $activeImagePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $activeImagePath -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $sourceImagePath -Algorithm SHA256).Hash) {
        throw "Generated active Dream Skin theme does not use the complete bundled image pack: $presetId"
    }
}

$customThemeFixturePath = Join-Path $appearanceSemanticsRoot 'custom-photo-theme.json'
$customPhotoSource = Join-Path $dreamSkinBundle 'assets\presets\preset-forest-mist\background.jpg'
$customThemeFixture = [ordered]@{
    Name = '本地照片回归主题'
    IsDark = $true
    CodeThemeId = 'everforest'
    AccentColor = '#4DB892'
    SurfaceColor = '#0D1A16'
    InkColor = '#E8F5EE'
    BackgroundImagePath = $customPhotoSource
    Contrast = 92
} | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($customThemeFixturePath, $customThemeFixture, [Text.UTF8Encoding]::new($false, $true))
& $dreamThemeScript `
    -PresetId custom `
    -CustomThemePath $customThemeFixturePath `
    -StateRoot $generatedThemeState | Out-Null
$customActiveThemeDirectory = Join-Path $generatedThemeState 'active-theme'
$customActiveTheme = [IO.File]::ReadAllText((Join-Path $customActiveThemeDirectory 'theme.json')) |
    ConvertFrom-Json -ErrorAction Stop
$customActiveImagePath = Join-Path $customActiveThemeDirectory ([string]$customActiveTheme.image)
if ([string]$customActiveTheme.id -cne 'custom' -or
    [string]$customActiveTheme.appearance -cne 'dark' -or
    [int]$customActiveTheme.contrast -ne 92 -or
    [string]$customActiveTheme.palette.accent -cne '#4DB892' -or
    [string]$customActiveTheme.palette.surface -cne '#0D1A16' -or
    [string]$customActiveTheme.palette.ink -cne '#E8F5EE' -or
    -not (Test-Path -LiteralPath $customActiveImagePath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $customActiveImagePath -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $customPhotoSource -Algorithm SHA256).Hash) {
    throw 'Generated custom Dream Skin theme did not publish the selected local photo.'
}
if ($taskLauncherScriptMethod -notmatch 'launchAccepted = \$true' -or
    $taskLauncherScriptMethod -notmatch 'RedirectStandardError = \$true' -or
    $taskLauncherScriptMethod -notmatch '\$process\.WaitForExit\(\{\{\(int\)CodexPlusPlusLauncherExitProbeTimeout\.TotalMilliseconds\}\}\)' -or
    $taskLauncherScriptMethod -notmatch 'launcherExitCode = \$launcherExitCode' -or
    $taskLauncherScriptMethod -notmatch '\[System\.Text\.Encoding\]::UTF8\.GetBytes\(\[string\]\$env:USERNAME\)' -or
    $taskLauncherScriptMethod -notmatch 'Test-LoopbackPortAvailable \$launcherGuardPort' -or
    $taskLauncherScriptMethod -notmatch '\$remainingTargetCount -eq 0 -and \$guardAvailable' -or
    $taskLauncherScriptMethod -notmatch 'Assert-LaunchRequestCurrent \$requestId \$expiresAtUtc' -or
    $taskLauncherScriptMethod -notmatch 'launch request was superseded by a newer generation' -or
    $taskLauncherScriptMethod -notmatch '\[System\.Diagnostics\.Process\]::GetProcessById\(\[int\]\$Target\.processId\)' -or
    $taskLauncherScriptMethod -notmatch 'StartTime\.ToUniversalTime\(\)\.Ticks -ne \[long\]\$Target\.startTimeUtcTicks' -or
    $taskLauncherScriptMethod -notmatch '\$switchRequired = \[bool\]\$request\.switchRequired' -or
    $taskLauncherScriptMethod -notmatch 'Hide-CodexPlusPlusManagerWindows' -or
    $taskLauncherScriptMethod -notmatch 'ShowWindow\(\$_\.MainWindowHandle, 0\)' -or
    $taskLauncherScriptMethod -notmatch 'WindowStyle = \[System\.Diagnostics\.ProcessWindowStyle\]::Hidden' -or
    $cliServiceSource -notmatch 'CalculateCodexPlusPlusLauncherGuardPort\("安"\) != 57860' -or
    $cliServiceSource -match 'CodexPlusPlusLauncherGuardPort\s*=\s*57383' -or
    $taskLauncherScriptMethod -notmatch 'this loop does not gate the account-switch button' -or
    $cliServiceSource -notmatch 'process\.ProcessName\.Equals\("codex-plus-plus-manager"' -or
    $cliServiceSource -notmatch 'codex-plus-plus-launch-diagnostics\.log' -or
    $cliServiceSource -notmatch '账号凭据已保留，无需重新登录' -or
    $cliServiceSource -match '20 秒内未同时检测到新的 Codex 窗口和可用的增强桥接' -or
    $taskLauncherScriptMethod -match '\$enhancementDeadline|Codex\+\+ page enhancements did not become ready|\$uriStartInfo') {
    throw 'The elevated Codex++ helper must capture immediate launcher failures, hide its manager window, leave real readiness to the background caller, log detailed diagnostics, and never kill a valid launch after a bridge timeout.'
}
if ($cliServiceSource -notmatch 'SwitchWindowsClientAccountAsync' -or
    $cliServiceSource -notmatch 'ProjectSharedAccountProfile' -or
    $cliServiceSource -notmatch 'ValidateSharedProfileProjection' -or
    $cliServiceSource -notmatch 'PreserveSharedMcpServerSections' -or
    $cliServiceSource -notmatch 'AssertSharedMcpServersPreserved' -or
    $cliServiceSource -notmatch 'profileHome = Path\.GetFullPath\(GetDefaultCodexHome\(\)\)' -or
    $cliServiceSource -notmatch 'AccountCodexHome' -or
    $cliServiceSource -notmatch 'SharedHistoryMerger\.Merge' -or
    $cliServiceSource -notmatch 'auth\.json' -or
    $cliServiceSource -notmatch '\.cockpit_codex_auth\.json' -or
    $cliServiceSource -notmatch 'config\.toml') {
    throw 'Windows client switch must validate the selected account, project credentials, and use shared chat history.'
}
if ($appScriptSource -notmatch 'function Merge-SharedMcpServerConfig' -or
    $appScriptSource -notmatch 'Merge-SharedMcpServerConfig -SharedConfigText \$sharedConfigText -ProjectedConfigText \$sourceConfigText') {
    throw 'Legacy account switching must preserve shared MCP server configuration.'
}
if ($settingsSource -notmatch 'WindowsClientMode WindowsClientMode' -or
    $settingsSource -notmatch 'OfficialCodex' -or
    ($cliServiceSource -notmatch '(?s)public bool IsSharedCredentialAlreadySelected\(AccountRecord account\)\s*\{.{0,500}?AuthJsonFilesSemanticallyEqual' -and
     ($cliServiceSource -notmatch '(?s)public bool IsSharedCredentialAlreadySelected\(AccountRecord account\)\s*\{.{0,500}?IsProjectedDesktopCredentialSelected' -or
      $cliServiceSource -notmatch '(?s)private static bool IsProjectedDesktopCredentialSelected\(.*?AuthJsonFilesSemanticallyEqual')) -or
    $cliServiceSource -match '(?s)public bool IsSharedCredentialAlreadySelected\(AccountRecord account\)\s*\{\s*return IsSharedProfileAlreadySelected' -or
    $formSource -match '_windowsClientModePicker' -or
    $formSource -notmatch '"Codex\+\+ 启动"' -or
    $formSource -notmatch '"Codex 启动"' -or
    $formSource -notmatch 'LaunchAccountAsync\(account, WindowsClientMode\.CodexPlusPlus\)' -or
    $formSource -notmatch 'LaunchAccountAsync\(account, WindowsClientMode\.OfficialCodex\)' -or
    $formSource -notmatch 'AutoShrinkText = false' -or
    $formSource -notmatch 'private Button MakeLaunchActionButton\(\s*string text,\s*int left' -or
    $formSource -notmatch 'IconText\s*=\s*string\.Empty' -or
    $formSource -notmatch 'ShowIconTile\s*=\s*false' -or
    $formSource -notmatch '(?s)private void ApplyStatusCheckButtonStyle\(Button button\).*?ApplyLaunchActionButtonStyle\(button\)' -or
    $formSource -notmatch 'MeasureActionButtonWidth\("Codex\+\+ 启动", 210\)' -or
    $formSource -notmatch 'MeasureActionButtonWidth\("Codex 启动", 180\)' -or
    $formSource -notmatch 'var horizontal = UsesHorizontalAccountSwitchLayout\(width\)' -or
    $formSource -notmatch 'Height = twoActionRows \? 168 : horizontal \? 112 : 118' -or
    $formSource -notmatch 'secondaryActionTop = twoActionRows \? actionTop \+ 50 : actionTop' -or
    $formSource -notmatch 'SwitchWindowsClientAccountAsync\(\s*account,\s*projectPath,\s*mode(?:,|\))' -or
    $cliServiceSource -notmatch 'WaitForCodexPlusPlusTaskResult\(requestId' -or
    $cliServiceSource -notmatch 'result\?\["requestId"\]' -or
    $cliServiceSource -match 'TryEndCodexPlusPlusScheduledTask\(\)') {
    throw 'Every account must expose complete icon-free premium Codex++ and Codex launch actions, with the status action in the same visual family.'
}
if ($cliServiceSource -notmatch 'startInfo\.Environment\["CODEX_HOME"\]\s*=\s*codexHome' -or
    $cliServiceSource -notmatch 'startInfo\.Environment\["CODEX_SQLITE_HOME"\]\s*=\s*codexHome' -or
    $cliServiceSource -notmatch '\["codexHome"\]\s*=\s*codexHome' -or
    $cliServiceSource -notmatch '\["codexSqliteHome"\]\s*=\s*codexHome' -or
    $cliServiceSource -notmatch '\$startInfo\.Environment\[''CODEX_HOME''\]\s*=\s*\$codexHome' -or
    $cliServiceSource -notmatch '\$startInfo\.Environment\[''CODEX_SQLITE_HOME''\]\s*=\s*\$codexSqliteHome' -or
    $cliServiceSource -notmatch 'codexHome = Path\.GetFullPath\(codexHome\)' -or
    $cliServiceSource -notmatch 'remote_compaction_v2' -or
    $cliServiceSource -notmatch 'remote_plugin' -or
    $cliServiceSource -notmatch 'service_tier' -or
    $cliServiceSource -notmatch 'model_auto_compact_token_limit' -or
    $cliServiceSource -notmatch 'ApplyProxyEnvironment\(startInfo\)' -or
    $cliServiceSource -notmatch 'supports_websockets = false') {
    throw 'Windows client launch must use shared CODEX_HOME and CODEX_SQLITE_HOME, preserve proxy settings through direct ChatGPT launches, disable Fast and automatic compaction, and keep compatible APIs on HTTP.'
}
$proxyAliases = @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy')
foreach ($proxyAlias in $proxyAliases) {
    $scheduledAssignment = '$startInfo.Environment[''' + $proxyAlias + '''] = $proxyUri'
    $shellAssignment = '$env:' + $proxyAlias + ' = '
    $cmdAssignment = 'set "' + $proxyAlias + '='
    if (-not $cliServiceSource.Contains('"' + $proxyAlias + '"') -or
        -not $cliServiceSource.Contains($scheduledAssignment) -or
        -not $cliServiceSource.Contains($shellAssignment) -or
        -not $appScriptSource.Contains("'$proxyAlias'") -or
        -not $desktopLauncherSource.Contains($cmdAssignment)) {
        throw "Codex launch paths must project proxy alias $proxyAlias."
    }
}
if ($cliServiceSource -match '(?m)^\s*\[void\]\$startInfo\.Environment\.Remove\(' -or
    $cliServiceSource -notmatch 'ValidateProxyEnvironmentProjection' -or
    $cliServiceSource -notmatch 'CodexLoopbackProxyBypass\s*=\s*"127\.0\.0\.1,localhost,::1"' -or
    $taskLauncherScriptMethod -notmatch "@\('NO_PROXY','no_proxy'\)" -or
    $appScriptSource -notmatch 'Invoke-LocalPatConfigMigration' -or
    $appScriptSource -notmatch "'NO_PROXY', 'no_proxy'") {
    throw 'Codex++ scheduled-task launch must preserve lowercase proxy aliases, bypass the proxy for its loopback CDP/helper bridge, and validate that projection.'
}
if ($cliServiceSource -notmatch 'await EnsureAccountModelAvailableAsync' -or
    $cliServiceSource -notmatch 'debug models' -or
    $cliServiceSource -notmatch 'HasFreshAccessTokenModelCache' -or
    $cliServiceSource -notmatch 'CompatibleApiPreflightCacheLifetime' -or
    $cliServiceSource -notmatch 'AccountStore\.AccessTokenProviderId' -or
    $cliServiceSource -notmatch 'supports_websockets = false' -or
    $cliServiceSource -notmatch 'await EnsureAccountCanRunMinimalRequestAsync') {
    throw 'Windows client switch must validate the target model and force Token accounts onto HTTP before stopping Codex.'
}
if ($cliServiceSource -notmatch 'ProjectWindowsClientConfig' -or
    $cliServiceSource -notmatch 'codex_local_access' -or
    $cliServiceSource -notmatch 'experimental_bearer_token' -or
    $accountStoreSource -notmatch 'ManagedProviderId\s*=\s*"codex_account_manager"' -or
    $accountStoreSource -notmatch 'AccessTokenProviderId\s*=\s*ManagedProviderId' -or
    $accountStoreSource -notmatch 'CompatibleApiProviderId\s*=\s*ManagedProviderId' -or
     $accountStoreSource -notmatch 'AccessTokenBaseUrl\s*=\s*LocalPatGateway\.ProviderBaseUrl' -or
    $accountStoreSource -notmatch 'requires_openai_auth\s*=\s*true' -or
    $accountStoreSource -notmatch 'plugins\s*=\s*false' -or
    $cliServiceSource -notmatch 'disablePlugins:\s*true' -or
     $accountStoreSource -notmatch 'supports_websockets\s*=\s*false') {
    throw 'Windows client account projection must remove stale local-access settings and use the fail-fast Token HTTP provider.'
}
if ($cliServiceSource -notmatch 'ApiKeyAuthMode\s*=\s*"apikey"' -or
    $cliServiceSource -notmatch '\["auth_mode"\]\s*=\s*ApiKeyAuthMode' -or
    $cliServiceSource -notmatch 'accessTokenMode:\s*AccessTokenSharedProfileMode\.ApiCompatible' -or
    $cliServiceSource -notmatch 'IsSharedProfileAlreadySelected\(AccountRecord account\)[\s\S]*?AccessTokenSharedProfileMode\.ApiCompatible' -or
    $cliServiceSource -notmatch 'One-click Access Token projection did not write a reusable official-App API-key login' -or
    $cliServiceSource -notmatch 'One-click compatible-API projection did not write a reusable official-App API-key login' -or
    $formSource -match '请在打开的 Codex App 完成一次 ChatGPT 登录' -or
    $formSource -notmatch '一键凭据：Codex App 的 API Key 登录由管理器自动写入') {
    throw 'Windows client launch must automatically project reusable auth_mode=apikey credentials for API and PAT accounts without another App login prompt.'
}
if ($localPatGatewaySource -notmatch 'ProviderBaseUrl\s*=\s*"http://127\.0\.0\.1:8317/backend-api/codex"' -or
    $localPatGatewaySource -notmatch 'ChatGptBaseUrl\s*=\s*"http://127\.0\.0\.1:8317/backend-api"' -or
    $localPatGatewaySource -notmatch 'chatgpt_account_id' -or
    $localPatGatewaySource -notmatch 'chatgpt-account-id' -or
    $localPatGatewaySource -notmatch 'UseCookies\s*=\s*false' -or
    $localPatGatewaySource -notmatch 'RequiredCodexVersion' -or
    $localPatGatewaySource -notmatch 'ResolveRequiredProxyUri' -or
    $localPatGatewaySource -notmatch 'proxyConfigured' -or
    $localPatGatewaySource -notmatch 'ReadBearerCredential' -or
    $localPatGatewaySource -notmatch 'IsPersonalAccessToken' -or
    $localPatGatewaySource -notmatch 'HasPathPrefix\(canonicalPath, backendGatewayPrefix\)' -or
    $localPatGatewaySource -notmatch 'UseShellExecute\s*=\s*true' -or
    $localPatGatewaySource -notmatch 'downstream\.ContentType\s*=\s*contentType' -or
    $localPatGatewaySource -notmatch '"session-id"' -or
    $localPatGatewaySource -notmatch '"thread-id"' -or
    $cliServiceSource -notmatch 'chatgpt_base_url' -or
    $formSource -notmatch 'PatGatewayProxyAddress' -or
    $formSource -notmatch 'PatGatewayProxyPort' -or
    $formSource -notmatch 'DetectLocalPatGatewayProxyAsync' -or
    $formSource -notmatch 'LocalProxyDetector\.DetectAsync' -or
    $formSource -notmatch 'IsLoopbackProxyAddress' -or
    $settingsSource -notmatch 'PatGatewayProxyAddress' -or
    $settingsSource -notmatch 'PatGatewayProxyPort' -or
    $settingsSource -notmatch 'PatGatewayProxyAutoDetect' -or
    $cliServiceSource -notmatch 'BuildPatGatewayProxyUri' -or
    $localPatGatewaySource -notmatch 'GetConfiguredProxyUri\(\)' -or
    $settingsSource -notmatch 'File\.Move\(temporaryPath, _settingsPath, overwrite:\s*true\)') {
    throw 'The local PAT gateway must resolve each token identity, inject the ChatGPT account id, and use the split address/port proxy settings with loopback detection.'
}
if ($localProxyDetectorSource -notmatch 'DetectPortAsync' -or
    $localProxyDetectorSource -notmatch 'TcpListener\(IPAddress\.Loopback,\s*0\)' -or
    $localProxyDetectorSource -notmatch 'ProbePayload' -or
    $localProxyDetectorSource -notmatch 'ReadHttpHeadersAsync' -or
    $localProxyDetectorSource -notmatch 'ReadExactlyAsync' -or
    $localProxyDetectorSource -notmatch 'statusCode\s*!=\s*200' -or
    $formSource -notmatch 'systemProxyPort' -or
    $formSource -notmatch 'DetectPortAsync') {
    throw 'Local proxy detection must verify a loopback CONNECT data round-trip and prefer the authoritative Windows proxy without touching the PAT gateway port.'
}
if ($quotaSnapshotStoreSource -notmatch 'QuotaAccountIdentity\.CreateKey' -or
    $quotaSnapshotStoreSource -notmatch 'LoadForAccounts' -or
    $quotaSnapshotStoreSource -notmatch 'ValidateAccountIsolation' -or
    $formSource -notmatch 'HydratePersistedQuotaSnapshots' -or
    $formSource -notmatch '_quotaSnapshotStore\.Save' -or
    $formSource -notmatch 'liveObservedAtUtc < usage\.RateLimitObservedAtUtc\.Value' -or
    $formSource -notmatch 'A newer official quota refresh must replace an older model-log snapshot across reset cycles' -or
    $programSource -notmatch 'Form1\.ValidateOfficialQuotaSnapshotPriority\(\)') {
    throw 'Official quota snapshots must be identity-scoped and newer official reset cycles must replace stale model-log percentages.'
}
if ($accountStoreSource -notmatch 'LegacyCompatibleApiProviderId\s*=\s*"codex_compatible_api"' -or
    $accountStoreSource -notmatch '\[model_providers\.\{CompatibleApiProviderId\}\]' -or
    $accountStoreSource -match 'model_provider\s*=\s*\{providerName\}' -or
    $accountStoreSource -notmatch 'stream_max_retries\s*=\s*0' -or
    $accountStoreSource -notmatch 'request_max_retries\s*=\s*1' -or
    $cliServiceSource -notmatch 'AccountStore\.CompatibleApiProviderId' -or
    $cliServiceSource -match '"model_provider = "\s*\+\s*TomlString\(providerName\)' -or
    $cliServiceSource -notmatch 'output\.Add\("stream_max_retries = 0"\)' -or
    $cliServiceSource -notmatch 'output\.Add\("request_max_retries = 1"\)' -or
    $cliServiceSource -notmatch 'AssertManagedProviderSection' -or
    $cliServiceSource -notmatch 'IsManagedLegacyCompatibleApiProviderSection' -or
    $cliServiceSource -notmatch 'removed an unrelated provider whose id matched the display name') {
    throw 'Compatible APIs must use the fixed codex_compatible_api provider id, retain only the display name, remove conflicting legacy sections, and fail fast on stream errors.'
}
if ($cliServiceSource -notmatch 'AccessTokenModel\s*=\s*"gpt-5\.6-terra"' -or
    $cliServiceSource -notmatch 'AccessTokenReasoningEffort\s*=\s*"medium"' -or
    $cliServiceSource -notmatch 'CompatibleApiDefaultModel\s*=\s*"gpt-5\.5"' -or
    $cliServiceSource -notmatch 'CompatibleApiReasoningEffort\s*=\s*"xhigh"' -or
    $cliServiceSource -notmatch 'sites@openai-bundled' -or
    $cliServiceSource -notmatch 'SetBoolean\("enhancementsEnabled", true\)' -or
    $cliServiceSource -notmatch 'SetBoolean\("providerSyncEnabled", false\)' -or
    $cliServiceSource -match 'SetBoolean\("providerSyncEnabled", true\)' -or
    $cliServiceSource -notmatch 'SetBoolean\("codexAppSessionDelete", true\)' -or
    $cliServiceSource -notmatch 'SetBoolean\("codexAppMarkdownExport", true\)' -or
    $cliServiceSource -notmatch 'SetBoolean\("codexAppProjectMove", true\)' -or
    $cliServiceSource -notmatch 'SetBoolean\("codexAppModelWhitelistUnlock", false\)' -or
    $cliServiceSource -notmatch 'SetString\("codexAppPath", Path\.GetFullPath\(resolvedCodexAppDirectory\)\)' -or
    $cliServiceSource -notmatch 'EnsureCodexPlusPlusSafeSettings\(clientAppDir\)' -or
    $cliServiceSource -notmatch 'codexAppPluginMarketplaceUnlock' -or
    $cliServiceSource -notmatch 'codexAppPluginAutoExpand') {
    throw 'Account switching must project the requested defaults and enable only safe Codex++ chat-management enhancements.'
}
if ($cliServiceSource -notmatch 'ProcessName\.Equals\("ChatGPT"' -or
    $cliServiceSource -notmatch 'MainWindowHandle\s*!=\s*IntPtr\.Zero' -or
    $cliServiceSource -notmatch 'GetShutdownPriority' -or
    $cliServiceSource -notmatch 'SanitizeCuratedPluginManifests' -or
    $cliServiceSource -notmatch 'MaxPluginDefaultPromptLength\s*=\s*128' -or
    $cliServiceSource -notmatch 'OpenNewTaskAfterWindowsClientLaunchInBackground' -or
    $cliServiceSource -notmatch 'WindowsClientGracefulShutdownTimeout\s*=\s*TimeSpan\.FromSeconds\(8\)' -or
    $cliServiceSource -notmatch 'WaitForProcessesToExit\(gracefulTargets, WindowsClientGracefulShutdownTimeout\)' -or
    $cliServiceSource -match 'process\.WaitForExit\(60000\)') {
    throw 'Windows client restart must close Codex with shared deadlines, sanitize the launch environment, and keep successful-launch readiness checks in the background.'
}
if ($launchAccountMethod -notmatch 'ClientLaunchStarted' -or
    $launchAccountMethod -notmatch 'ClientLaunchError' -or
    $launchAccountMethod -notmatch 'ProfileChanged' -or
    $launchAccountMethod -notmatch '(?s)if\s*\(projection\.ClientLaunchStarted\).*?StartOfficialQuotaRefreshAfterLaunch\(account\)' -or
    ([regex]::Matches($launchAccountMethod, 'StartOfficialQuotaRefreshAfterLaunch\(account\)')).Count -ne 1) {
    throw 'The Start UI must honor the selected client, retain committed credentials on launch errors, and refresh quota only after launch acceptance.'
}
if ($cliServiceSource -notmatch 'AlignDesktopProfileModelState' -or
    $cliServiceSource -notmatch 'state_5\.sqlite' -or
    $cliServiceSource -match 'UPDATE threads SET model\s*=' -or
    $cliServiceSource -notmatch 'UPDATE threads SET model_provider = \$target' -or
    $cliServiceSource -notmatch 'BackupSqliteDatabase' -or
    $cliServiceSource -notmatch 'BackupDatabase' -or
    $cliServiceSource -notmatch 'RestoreSqliteDatabase' -or
    $cliServiceSource -notmatch 'Historical thread models changed' -or
    $cliServiceSource -notmatch 'models_cache\.json' -or
    $cliServiceSource -notmatch 'NormalizeDesktopSidebarState' -or
    $cliServiceSource -notmatch 'sidebar-collapsed-sections-v1' -or
    $cliServiceSource -notmatch 'chatSortMode' -or
    $cliServiceSource -notmatch 'BuildThreadDeepLink' -or
    $cliServiceSource -notmatch 'SanitizeProjectModelOverrides' -or
    $cliServiceSource -notmatch 'RemoveProjectModelOverrides' -or
    $projectSource -notmatch 'Microsoft\.Data\.Sqlite') {
    throw 'Windows client switch must remove project overrides, preserve historical task models, reset the model cache, expand the flat chronological sidebar, and open existing tasks by id.'
}
if ($formSource -notmatch 'WorkspaceView\.UnifiedHistory' -or
    $formSource -notmatch 'RenderUnifiedHistory' -or
    $formSource -notmatch 'OpenUnifiedThreadAsync' -or
    $formSource -notmatch 'ToggleUnifiedThreadArchiveAsync' -or
    $formSource -notmatch 'DeleteUnifiedThreadAsync' -or
    $formSource -notmatch 'AutoScaleMode\s*=\s*AutoScaleMode\.Dpi' -or
    $historyServiceSource -notmatch 'state_5\.sqlite' -or
    $historyServiceSource -notmatch 'RecordDeletedThread' -or
    $historyServiceSource -notmatch 'ORDER BY updated_ms DESC' -or
    $historyServiceSource -notmatch 'BuildVisibleThreadFilter' -or
    $historyServiceSource -notmatch 'agent_path' -or
    $historyMergerSource -notmatch 'MergeThreadValues' -or
    $historyMergerSource -notmatch 'UPDATE threads SET' -or
    $historyMergerSource -notmatch 'ValidateHistoryFileMerge' -or
    $historyMergerSource -notmatch 'account-switcher-conflicts' -or
    $historyMergerSource -notmatch 'SourceIsPrefix' -or
    $historyMergerSource -notmatch 'deletedThreadIds' -or
    $settingsSource -notmatch 'ProjectPath' -or
    $formSource -notmatch 'ResolveInitialProjectPath' -or
    $formSource -notmatch '!IsAccountManagerRoot\(savedProjectPath\)' -or
    $formSource -notmatch 'DetachPersistentSystemConfigControls' -or
    $formSource -notmatch 'GetProjectPathInputText' -or
    $formSource -notmatch '_appSettings\.ProjectPath' -or
    $accountStoreSource -notmatch 'PathsEqual\(account\.CodexHome, CodexCliService\.GetDefaultCodexHome\(\)\)') {
    throw 'Account Manager must expose one unified .codex history view, safely upsert existing thread metadata, and persist the selected project path.'
}
$restoreWorkspaceLayout = [regex]::Match(
    $formSource,
    '(?s)private bool TryRestoreWorkspaceView\(WorkspaceView view\).*?(?=\r?\n\s*private void ClearWorkspaceViewCache)')
$persistentControlLayout = [regex]::Match(
    $formSource,
    '(?s)private void DetachPersistentSystemConfigControls\(\).*?(?=\r?\n\s*private static void DetachPersistentControl)')
if (-not $restoreWorkspaceLayout.Success -or
    $restoreWorkspaceLayout.Value -notmatch 'DetachPersistentSystemConfigControls\(\);[\s\S]*?foreach \(var current in _cardsPanel\.Controls\.Cast<Control>\(\)\.ToArray\(\)\)[\s\S]*?current\.Dispose\(\)' -or
    -not $persistentControlLayout.Success -or
    $persistentControlLayout.Value -notmatch '_projectPathShell' -or
    $persistentControlLayout.Value -notmatch '_patGatewayProxyAddressShell' -or
    $persistentControlLayout.Value -notmatch '_patGatewayProxyPortShell' -or
    $persistentControlLayout.Value -notmatch '_patGatewayProxyDetectionLabel') {
    throw 'Workspace view restoration must detach persistent system-config controls before disposing transient cards.'
}
$openUnifiedThreadLayout = [regex]::Match(
    $formSource,
    '(?s)private async Task OpenUnifiedThreadAsync\(UnifiedThreadRecord thread\).*?(?=\r?\n\s*private async Task ToggleUnifiedThreadArchiveAsync)')
if (-not $openUnifiedThreadLayout.Success -or
    $openUnifiedThreadLayout.Value -notmatch 'CodexCliService\.GetDefaultCodexHome\(\)' -or
    $openUnifiedThreadLayout.Value -notmatch 'Task\.Run\(\(\) => _threadTranscript\.Load\(sharedHome, thread\)\)' -or
    $openUnifiedThreadLayout.Value -notmatch 'new ThreadPreviewDialog\(thread, transcript, _palette\)' -or
    $openUnifiedThreadLayout.Value -notmatch 'dialog\.ShowDialog\(this\)' -or
    $openUnifiedThreadLayout.Value -match 'IsCodexPlusPlusReady|OpenWindowsClientThreadAsync|LaunchAccountAsync|LoginWith|SwitchWindowsClientAccountAsync' -or
    $formSource -notmatch 'Text\s*=\s*"阅读  ›"' -or
    $formSource -notmatch 'AccessibleName\s*=\s*\$"阅读本地聊天：\{thread\.Title\}"' -or
    $formSource -notmatch '本地只读对话，不启动或登录 Codex\+\+。') {
    throw 'Opening a unified chat must show the local read-only preview directly and must not require, launch, switch, or log in to Codex++.'
}
if ($threadTranscriptSource -notmatch 'public sealed class SharedThreadTranscriptService' -or
    $threadTranscriptSource -notmatch 'public UnifiedThreadTranscript Load\(' -or
    $threadTranscriptSource -notmatch 'UnifiedThreadTranscriptStatus' -or
    $threadTranscriptSource -notmatch 'IReadOnlyList<UnifiedThreadMessage> Messages' -or
    $threadTranscriptSource -notmatch 'maxMessages = Math\.Clamp\(maxMessages, 1, 200\)' -or
    $threadTranscriptSource -notmatch 'maxMessageCharacters = Math\.Clamp\(maxMessageCharacters, 80, 12_000\)' -or
    $threadTranscriptSource -notmatch 'FileAccess\.Read' -or
    $threadTranscriptSource -notmatch 'FileShare\.ReadWrite \| FileShare\.Delete' -or
    $threadTranscriptSource -notmatch 'IsInsideDirectory\(fullRolloutPath, home\)' -or
    $threadTranscriptSource -notmatch 'Path\.GetExtension\(fullRolloutPath\)\.Equals\("\.jsonl"' -or
    $threadTranscriptSource -notmatch 'ReadBoundedLines\(reader, maxJsonLineCharacters\)' -or
    $threadTranscriptSource -notmatch 'FilterConversationText\(role, text\)' -or
    $threadTranscriptSource -notmatch '# AGENTS\.md instructions' -or
    $threadTranscriptSource -notmatch 'environment_context' -or
    $threadTranscriptSource -notmatch '# Files mentioned by the user:' -or
    $threadTranscriptSource -notmatch 'internal static void ValidateReader\(\)' -or
    $programSource -notmatch 'SharedThreadTranscriptService\.ValidateReader\(\)') {
    throw 'The local transcript reader must be bounded, path-confined, read-only, tolerant of active JSONL files, and covered by offline validation.'
}
if ($threadPreviewDialogSource -notmatch 'public sealed class ThreadPreviewDialog\s*:\s*Form' -or
    $threadPreviewDialogSource -notmatch 'AutoScaleMode = AutoScaleMode\.Dpi' -or
    $threadPreviewDialogSource -notmatch 'FormBorderStyle = FormBorderStyle\.Sizable' -or
    $threadPreviewDialogSource -notmatch '_transcriptBox\.ReadOnly = true' -or
    $threadPreviewDialogSource -notmatch '_transcriptBox\.ShortcutsEnabled = true' -or
    $threadPreviewDialogSource -notmatch 'KeyPreview = true' -or
    $threadPreviewDialogSource -notmatch 'PlaceholderText = "搜索当前对话（Ctrl\+F）"' -or
    $threadPreviewDialogSource -notmatch 'eventArgs\.Control && eventArgs\.KeyCode == Keys\.F' -or
    $threadPreviewDialogSource -notmatch 'private void FindMatch\(bool forward, bool restart = false\)' -or
    $threadPreviewDialogSource -notmatch 'private static int CountMatches\(string text, string query\)' -or
    $threadPreviewDialogSource -notmatch 'Text = "复制全部"' -or
    $threadPreviewDialogSource -notmatch 'private void RenderTranscript\(UnifiedThreadTranscript transcript, ThemePalette palette\)' -or
    $threadPreviewDialogSource -notmatch 'internal static void ValidateFormatting\(\)' -or
    $threadPreviewDialogSource -notmatch 'BuildCopyText\(thread, transcript\)' -or
    $programSource -notmatch 'ThreadPreviewDialog\.ValidateFormatting\(\)') {
    throw 'The local chat preview dialog must be DPI-aware, resizable, read-only, copyable, role-formatted, and covered by offline formatting validation.'
}
if ($formSource -notmatch '搜索标题或对话内容' -or
    $formSource -notmatch 'EnsureUnifiedHistoryContentIndex' -or
    $formSource -notmatch 'BuildUnifiedHistorySearchText' -or
    $formSource -notmatch 'CreateUnifiedHistorySearchSnippet' -or
    $formSource -notmatch '正文匹配：\{matchSnippet\}' -or
    $formSource -notmatch 'Task\.Run\(\(\) =>' -or
    $formSource -notmatch 'ValidateUnifiedHistorySearch\(\)' -or
    $programSource -notmatch 'Form1\.ValidateUnifiedHistorySearch\(\)') {
    throw 'The unified chat view must search titles and filtered local conversation content in a background index.'
}
if ($formSource -notmatch 'Task\.Run\(\(\) =>[\s\S]*?_usageTracker\.BuildReport\(accountSnapshot\)' -or
    $formSource -notmatch 'Task\.Run\(\(\) => new UnifiedHistoryLoadResult' -or
    $formSource -notmatch '_quotaUsageCache' -or
    $formSource -notmatch '_unifiedHistoryCache' -or
    $formSource -notmatch 'CreateWorkspaceLoadingState' -or
    $formSource -notmatch 'UnifiedHistoryPageSize\s*=\s*8' -or
    $formSource -notmatch 'foreach \(var thread in renderedThreads\)' -or
    $formSource -notmatch 'TryUpdateQuotaUsageInPlace' -or
    $formSource -notmatch 'var loadMore = MakeHistoryActionButton\(' -or
    $formSource -notmatch 'var refresh = MakeHistoryActionButton\(' -or
    $formSource -notmatch 'var archive = MakeHistoryActionButton\(' -or
    $formSource -notmatch 'var delete = MakeHistoryActionButton\("删除",[\s\S]*?danger:\s*true\)' -or
    $formSource -notmatch 'button\.Tag = danger \? "history-danger" : "history-tonal"' -or
    $formSource -notmatch 'Equals\(button\.Tag, "history-tonal"\)[\s\S]*?ApplyHistoryActionButtonStyle\(button, danger:\s*false\)' -or
    $formSource -notmatch 'Equals\(button\.Tag, "history-danger"\)[\s\S]*?ApplyHistoryActionButtonStyle\(button, danger:\s*true\)' -or
    $formSource -notmatch 'var accent = danger \? _palette\.DangerColor : _palette\.PrimaryColor' -or
    $formSource -notmatch 'modern\.UseSurfaceSheen\s*=\s*false' -or
    $formSource -notmatch 'modern\.ShadowColor\s*=\s*Color\.Transparent' -or
    $formSource -match 'AppendUnifiedHistoryRowsAsync') {
    throw 'Quota and history views must keep cached in-place updates, the eight-item shortcut page, and white-background tonal/danger history actions.'
}
if ($programSource -notmatch 'AccountDialog\.ValidateExistingTokenEditLayout\(\)') {
    throw 'The high-DPI account-dialog layout regression test must remain wired into self-test mode.'
}
if ($formSource -notmatch 'QuotaMinimumRefreshInterval\s*=\s*TimeSpan\.FromMilliseconds\(250\)' -or
    $formSource -notmatch '_quotaRefreshTimer\.Interval\s*=\s*250' -or
    $formSource -notmatch 'Warm the local usage cache' -or
    $usageTrackerSource -notmatch '_usageFileCache' -or
    $usageTrackerSource -notmatch 'TryGetUsageFileIdentity' -or
    $usageTrackerSource -notmatch 'CachedUsageFile' -or
    $formSource -notmatch 'var openHint = new Label[\s\S]*?Width = 140' -or
    $settingsSource -notmatch 'SecondaryAccentColor' -or
    $settingsSource -notmatch 'TertiaryAccentColor') {
    throw 'Quota refresh, chat shortcuts, and theme palettes must keep their responsive high-DPI multi-accent design.'
}
$automaticQuotaRefresh = [regex]::Match(
    $formSource,
    '(?s)private void StartOfficialQuotaRefreshAfterLaunch\(AccountRecord account\).*?(?=\r?\n\s*private async Task QueryUsageLimitResetAsync)')
if (-not $automaticQuotaRefresh.Success -or
    $formSource -notmatch 'OfficialQuotaFocusedRefreshInterval\s*=\s*TimeSpan\.FromSeconds\(15\)' -or
    $formSource -notmatch 'OfficialQuotaBackgroundRefreshInterval\s*=\s*TimeSpan\.FromMinutes\(1\)' -or
    ([regex]::Matches($formSource, 'StartOfficialQuotaRefreshAfterLaunch\(account\)')).Count -lt 2 -or
    $automaticQuotaRefresh.Value -notmatch 'account\.IsCompatibleApi' -or
    $automaticQuotaRefresh.Value -notmatch 'var accountKey\s*=\s*QuotaAccountIdentity\.CreateKey\(account\)' -or
    $automaticQuotaRefresh.Value -notmatch '_officialQuotaRefreshAttemptedAt\.TryGetValue\(accountKey' -or
    $automaticQuotaRefresh.Value -notmatch 'var refreshInterval\s*=\s*focused' -or
    $automaticQuotaRefresh.Value -notmatch 'now - lastAttempt < refreshInterval' -or
    $automaticQuotaRefresh.Value -notmatch '_officialQuotaRefreshInProgress\.Add\(accountKey\)' -or
    $automaticQuotaRefresh.Value -notmatch 'CancellationTokenSource\(TimeSpan\.FromSeconds\(15\)\)' -or
    $automaticQuotaRefresh.Value -notmatch 'OpenUsageLimitResetSessionAsync\([\s\S]*?account,[\s\S]*?fastFail:\s*true' -or
    $automaticQuotaRefresh.Value -notmatch 'session\.ReadAsync\(timeout\.Token\)' -or
    $automaticQuotaRefresh.Value -notmatch 'CacheUsageLimitResetInfo\(account,\s*info\)' -or
    $automaticQuotaRefresh.Value -notmatch 'catch \(OperationCanceledException\)' -or
    $automaticQuotaRefresh.Value -match '_accounts|foreach\s*\(|ConsumeAsync\(|LoginWith|EnsureAccountCanRunMinimalRequest|thread/start|turn/start') {
    throw 'Official quota refresh must prioritize the focused account every 15 seconds, retain a background cooldown, and avoid model/reset-credit actions.'
}
$readOnlyQuotaRequest = [regex]::Match(
    $resetSessionSource,
    '(?s)public async Task<UsageLimitResetInfo> ReadAsync\(.*?(?=\r?\n\s*public async Task<UsageLimitResetOutcome> ConsumeAsync)')
if (-not $readOnlyQuotaRequest.Success -or
    $readOnlyQuotaRequest.Value -notmatch 'RequestAsync\(\s*"account/rateLimits/read",\s*null,' -or
    $readOnlyQuotaRequest.Value -match 'rateLimitResetCredit/consume|thread/start|turn/start|model|prompt' -or
    $cliServiceSource -notmatch 'var attemptCount\s*=\s*fastFail \? 1 : 2' -or
    $cliServiceSource -notmatch 'var initializeTimeout\s*=\s*fastFail\s*\? TimeSpan\.FromSeconds\(8\)' -or
    $cliServiceSource -notmatch 'catch \(TimeoutException\) when \(!fastFail && attempt == 0\)') {
    throw 'Automatic official quota reads must be zero-token read-only requests and fast-fail without retrying.'
}
if ($formSource -match 'GetManualDollarEstimate|QuotaMeasurementVisualState|GetPendingCheckpoint|FormatQuotaRemainingWithDollar|GetDollarEstimateToolTip' -or
    $formSource -notmatch 'FormatQuotaRemaining\(' -or
    $formSource -notmatch 'GetOfficialQuotaToolTip\(') {
    throw 'Quota cards must show only official percentages/reset times and local usage, without an active dollar-capacity probe.'
}
if ($formSource -notmatch 'BuildAccountGroups\(\s*visible(?:Accounts)?(?:,\s*(?:report|usageReport))?\s*\)' -or
    $formSource -notmatch 'new AccountGroupSection\("api"' -or
    $formSource -notmatch 'new AccountGroupSection\("weekly"' -or
    $formSource -notmatch 'new AccountGroupSection\("monthly"' -or
    $formSource -notmatch 'var stacked = !UsesHorizontalQuotaUsageLayout\(width\)' -or
    $formSource -notmatch 'var resetLineHeight = Math\.Max\(' -or
    $formSource -notmatch 'MeasureQuotaResetText\(resetDetailMeasureFont\)' -or
    $formSource -notmatch 'var resetAreaHeight = \(resetLineHeight \* 2\) \+ resetLineGap' -or
    $formSource -notmatch 'var actionTop = resetAreaTop \+ resetAreaHeight \+ resetToActionGap' -or
    $formSource -notmatch 'var rowHeight = actionTop \+ actionHeight \+ cardBottomPadding' -or
    $formSource -notmatch 'var accountInfoHeight = showsCapacity \? 136 : 68' -or
    $formSource -notmatch 'CenterQuotaRowContent\(rowHeight, accountInfoHeight\)' -or
    $formSource -notmatch 'CalculateQuotaUsageHorizontalGeometry\(width\)' -or
    $formSource -notmatch 'CapacitySummary' -or
    $formSource -notmatch '推测剩余  \{FormatUsd\(remainingUsd\)\} / \{FormatUsd\(totalUsd\)\}' -or
    $formSource -notmatch '首次估算 \{observedSpan:0\.#\}/\{requiredPercentSpan:0\}%' -or
    $formSource -notmatch 'var primaryRemainingPercent = GetPrimaryDisplayedQuotaWindow\(quotaLimitType, usage\)\?\.RemainingPercent;[\s\S]*?UpdatePassiveQuotaStatus\(\s*capacityStatus,\s*capacitySummary,\s*monitoring,\s*primaryRemainingPercent\)' -or
    $formSource -notmatch 'binding\.CapacitySummary' -or
    $formSource -notmatch 'void FitBadgeToContent\(\)' -or
    $formSource -notmatch 'badge\.Width = Math\.Clamp\(measuredWidth, Math\.Min\(124, maximumWidth\), maximumWidth\)' -or
    $formSource -notmatch 'Height = 56' -or
    $formSource -notmatch 'UseCompatibleTextRendering = true' -or
    $formSource -notmatch 'AccountQuotaLimitType\.UsesTwoDetailLines\(quotaLimitType\)' -or
    $formSource -notmatch 'FormatQuotaRemaining\(weeklyWindow, "周"\)' -or
    $formSource -notmatch '无 5h 限额' -or
    $formSource -notmatch 'Name = "QuotaAvailabilitySecondary"' -or
    $formSource -notmatch 'UpdateQuotaPill\(binding\.SecondaryQuota, "无 5h 限额", _palette\.MutedTextColor\)' -or
    $formSource -notmatch 'GetQuotaRowDetailLines\(' -or
    $formSource -notmatch 'binding\.SecondaryDetail\.Text = secondaryDetail \?\? string\.Empty' -or
    $formSource -notmatch 'AccountQuotaLimitType\.UsesTwoDetailLines\(quotaLimitType\)[\s\S]*?summary\.Replace' -or
    $formSource -notmatch 'Name = "QuotaResetPrimaryDetail"' -or
    $formSource -notmatch 'Name = "QuotaResetSecondaryDetail"' -or
    $formSource -notmatch 'TextRenderer\.MeasureText\(' -or
    $formSource -notmatch 'private static void ValidateQuotaResetLayoutAtScale\(float scale\)' -or
    $formSource -notmatch 'ValidateQuotaResetLayoutAtScale\(2F\)' -or
    $formSource -notmatch 'var wideLabelWidth = ScalePixels\(280, scale\)' -or
    $formSource -notmatch 'var stackedLabelWidth = ScalePixels\(AccountRowMinWidth - 36, scale\)' -or
    $programSource -notmatch 'Form1\.ValidateUsagePricing\(\)' -or
    $programSource -notmatch 'Form1\.ValidateStableWorkspaceGutter\(\)' -or
    $formSource -notmatch 'UseCompatibleTextRendering = true' -or
    $formSource -notmatch 'var detailTop = quotaContentBottom \+ Math\.Max\(' -or
    $formSource -notmatch 'hasTwoQuotaSlots[\s\S]*?Math\.Max\(180, \(width - 54\) / 2\)[\s\S]*?: width - 36' -or
    $formSource -match 'MeasurementAction' -or
    $formSource -notmatch 'BuildAccountGroups\(\s*visible(?:Accounts)?(?:,\s*(?:report|usageReport))?\s*\)' -or
    $formSource -notmatch 'OrderByDescending\(IsCurrentAccount\)' -or
    $formSource -notmatch 'group\.Accounts\.Any\(IsCurrentAccount\)' -or
    $formSource -notmatch 'visible\.FirstOrDefault\(IsCurrentAccount\)' -or
    $formSource -notmatch 'matchingProfiles' -or
    $formSource -notmatch '_codex\.IsSharedCredentialAlreadySelected\(account\)' -or
    $formSource -notmatch 'var remembered = _accounts\.FirstOrDefault' -or
    $formSource -notmatch 'SetCurrentAccount\(remembered\?\.Name, false\)' -or
    $formSource -notmatch 'var collapseRows = _collapsedAccountGroups\.Add\(stateKey\)' -or
    $formSource -notmatch 'row\.Visible = !collapseRows' -or
    $formSource -notmatch 'NativeWindowTheme\.SuspendRedraw\(_cardsPanel\)' -or
    $formSource -notmatch '_cardsPanel\.SuspendLayout\(\)' -or
    $formSource -notmatch '_cardsPanel\.ResumeLayout\(performLayout:\s*false\)' -or
    $formSource -notmatch 'SequenceEqual\(' -or
    $formSource -notmatch 'StringComparer\.OrdinalIgnoreCase') {
    throw 'Quota rows must group accounts, keep current ordering, and fold in place with stable redraw and scrollbar geometry.'
}
if ($formSource -notmatch 'StartPosition = FormStartPosition\.Manual' -or
    $formSource -notmatch 'WindowState = FormWindowState\.Normal' -or
    $formSource -match 'WindowState = FormWindowState\.Maximized' -or
    $formSource -notmatch 'FormBorderStyle = FormBorderStyle\.Sizable' -or
    $formSource -notmatch 'MinimumSize = new Size\(800, 600\)' -or
    $formSource -notmatch 'Size = new Size\(920, 620\)' -or
    $formSource -notmatch 'ApplyInitialWindowBounds\(\)' -or
    $formSource -notmatch 'const int logicalWorkingAreaMargin = 96' -or
    $formSource -notmatch 'DeviceDpi / 96F' -or
    $formSource -notmatch 'Screen\.FromControl\(this\)\.WorkingArea' -or
    $formSource -notmatch 'const int logicalInitialWidth = 920' -or
    $formSource -notmatch 'const int logicalInitialHeight = 620' -or
    $formSource -notmatch 'const int logicalMinimumWidth = 800' -or
    $formSource -notmatch 'const int logicalMinimumHeight = 600' -or
    $formSource -notmatch 'Screen\.FromRectangle\(savedBounds\)\.WorkingArea' -or
    $formSource -notmatch 'SaveWindowBounds\(\)' -or
    $formSource -notmatch 'WindowState == FormWindowState\.Normal \? Bounds : RestoreBounds' -or
    $formSource -notmatch '_appSettings\.WindowWidth = \(int\)Math\.Round\(bounds\.Width / dpiScale\)' -or
    $settingsSource -notmatch 'public int\? WindowLeft \{ get; set; \}' -or
    $settingsSource -notmatch 'public int\? WindowTop \{ get; set; \}' -or
    $settingsSource -notmatch 'public int\? WindowWidth \{ get; set; \}' -or
    $settingsSource -notmatch 'public int\? WindowHeight \{ get; set; \}' -or
    $formSource -notmatch 'targetWidth = \(int\)Math\.Round\(logicalInitialWidth \* dpiScale\)' -or
    $formSource -notmatch 'targetHeight = \(int\)Math\.Round\(logicalInitialHeight \* dpiScale\)' -or
    $formSource -notmatch 'Math\.Min\(targetWidth, availableWidth\)' -or
    $formSource -notmatch 'Math\.Min\(targetHeight, availableHeight\)' -or
    $formSource -notmatch 'Location = new Point\(' -or
    $formSource -notmatch 'var resetAreaTop = stacked \? 348 : 106' -or
    $formSource -notmatch 'Height = hasTwoDetailLines \? resetLineHeight : singleDetailHeight' -or
    $formSource -notmatch 'ApplyDetailViewportMode' -or
    $formSource -notmatch 'private const int WorkspaceHeroHeight = 160' -or
    $formSource -notmatch '_contentLayout\.RowStyles\[0\]\.Height = WorkspaceHeroHeight' -or
    $formSource -notmatch 'ShowStarfield = true' -or
    $formSource -notmatch 'private void DrawStarfield\(' -or
    $formSource -notmatch 'ControlStyles\.OptimizedDoubleBuffer' -or
    $formSource -notmatch 'ShowInTaskbar = true' -or
    $formSource -notmatch 'private void RestoreWorkspaceAfterMinimize\(\)' -or
    $formSource -notmatch 'WindowState == FormWindowState\.Minimized' -or
    $formSource -notmatch '_layoutRefreshTimer\.Stop\(\)' -or
    $formSource -notmatch '_lastCardsPanelOuterWidth = restoredWidth' -or
    $formSource -notmatch '_activeView == WorkspaceView\.SystemConfig' -or
    $formSource -notmatch '_controlsRow\.Visible = !compact' -or
    $accountDialogSource -notmatch '_updateTokenButton\.Text = "[^"]*Token";' -or
    $buildScriptSource -notmatch '\$desktopShortcut\.TargetPath = \$appExe' -or
    $buildScriptSource -notmatch '\$desktopShortcut\.IconLocation') {
    throw 'The desktop UI must restore a resizable visible window, keep quota reset spacing, use complete token labels, and expose a stable taskbar identity.'
}
$drawStarfieldMethod = [regex]::Match(
    $formSource,
    '(?s)private void DrawStarfield\(.*?(?=\r?\n\s*private static void DrawMeteorShower\()')
$meteorShowerMethod = [regex]::Match(
    $formSource,
    '(?s)private static void DrawMeteorShower\(.*?(?=\r?\n\s*private static void DrawCurvedAnimatedMeteor\()')
$roundedPanelSource = [regex]::Match(
    $formSource,
    '(?s)internal sealed class RoundedPanel : Panel.*$').Value
$roundedPanelBackgroundMethod = [regex]::Match(
    $roundedPanelSource,
    '(?s)protected override void OnPaintBackground\(PaintEventArgs e\).*?(?=\r?\n\s*protected override void OnPaint\(PaintEventArgs e\))')
$roundedPanelForegroundMethod = [regex]::Match(
    $roundedPanelSource,
    '(?s)protected override void OnPaint\(PaintEventArgs e\).*?(?=\r?\n\s*private void DrawTechDecoration\()')
$starfieldActivatedMethod = [regex]::Match(
    $roundedPanelSource,
    '(?s)private void StarfieldAnimationHostOnActivated\(.*?(?=\r?\n\s*private void StarfieldAnimationHostOnDeactivated\()')
$starfieldDeactivatedMethod = [regex]::Match(
    $roundedPanelSource,
    '(?s)private void StarfieldAnimationHostOnDeactivated\(.*?(?=\r?\n\s*private void StarfieldAnimationHostOnFormClosed\()')
if ($formSource -notmatch 'ActiveStarfieldAnimationIntervalMilliseconds = 67' -or
    $formSource -match 'InactiveStarfieldAnimationIntervalMilliseconds' -or
    $formSource -notmatch 'var headerTextWidth\s*=\s*Math\.Clamp\(' -or
    $formSource -notmatch 'header\.ClientSize\.Width \* 0\.52F' -or
    $formSource -notmatch 'private Rectangle GetStarfieldAnimationBounds\(\)' -or
    $formSource -notmatch 'Invalidate\(GetStarfieldAnimationBounds\(\), invalidateChildren: false\)' -or
    $formSource -notmatch 'private bool IsStarfieldAnimationHostActive\(\)' -or
    $formSource -notmatch 'ReferenceEquals\(Form\.ActiveForm, host\) \|\| host\.ContainsFocus' -or
    $formSource -notmatch 'WindowState: not FormWindowState\.Minimized' -or
    $formSource -notmatch 'private static void DrawMeteorShower\(' -or
    -not $drawStarfieldMethod.Success -or
    -not $meteorShowerMethod.Success -or
    -not $roundedPanelBackgroundMethod.Success -or
    -not $roundedPanelForegroundMethod.Success -or
    -not $starfieldActivatedMethod.Success -or
    -not $starfieldDeactivatedMethod.Success -or
    $starfieldActivatedMethod.Value -notmatch 'UpdateStarfieldAnimationState\(\)[\s\S]*?Invalidate\(GetStarfieldAnimationBounds\(\), invalidateChildren: false\)' -or
    $starfieldDeactivatedMethod.Value -notmatch 'StopStarfieldAnimation\(\)' -or
    $starfieldDeactivatedMethod.Value -match '\bInvalidate\(' -or
    ([regex]::Matches($roundedPanelSource, '\.Activated \+= StarfieldAnimationHostOnActivated').Count -ne 1) -or
    ([regex]::Matches($roundedPanelSource, '\.Activated -= StarfieldAnimationHostOnActivated').Count -ne 1) -or
    ([regex]::Matches($roundedPanelSource, '\.Deactivate \+= StarfieldAnimationHostOnDeactivated').Count -ne 1) -or
    ([regex]::Matches($roundedPanelSource, '\.Deactivate -= StarfieldAnimationHostOnDeactivated').Count -ne 1) -or
    $roundedPanelBackgroundMethod.Value -notmatch 'if \(ShowStarfield\)[\s\S]*?DrawStarfield\(e\.Graphics,\s*surfaceBounds\)' -or
    $roundedPanelForegroundMethod.Value -match 'DrawStarfield\(' -or
    ([regex]::Matches($drawStarfieldMethod.Value, '\bDrawMeteorShower\(').Count -ne 1) -or
    ([regex]::Matches($meteorShowerMethod.Value, '(?m)^\s*\(\d+\.\d+F,\s*0\.\d+F,\s*1\.\d+F,').Count -ne 6) -or
    ([regex]::Matches($meteorShowerMethod.Value, '\bDrawCurvedAnimatedMeteor\(').Count -ne 1) -or
    $meteorShowerMethod.Value -match '1\.0[469]F,\s*0\.(?:4[7-9]|[5-9]\d)F') {
    throw 'The starfield banner must keep six curved animated meteors in the uncovered right-hand sky, repaint at 15 FPS only while its host is active, and stop without invalidating while inactive or minimized.'
}
if ($cliServiceSource -notmatch 'BuildNewThreadDeepLink' -or
    $cliServiceSource -notmatch 'codex://threads/new\?path=' -or
    $cliServiceSource -notmatch 'OpenNewTaskAfterWindowsClientLaunchInBackground' -or
    $cliServiceSource -notmatch '/backend/status' -or
    $cliServiceSource -notmatch 'MainWindowHandle\s*!=\s*IntPtr\.Zero') {
    throw 'A successful direct Codex++ launch must still open a blank task only after the page bridge is ready.'
}
if ($openWindowsClientThreadMethod -notmatch 'var threadUrl\s*=\s*BuildThreadDeepLink\(threadId\)' -or
    $openWindowsClientThreadMethod -notmatch 'Process\.Start\(new ProcessStartInfo\(threadUrl\)' -or
    $openWindowsClientThreadMethod -notmatch 'UseShellExecute\s*=\s*true' -or
    $openWindowsClientThreadMethod -notmatch 'Task\.CompletedTask' -or
    $openWindowsClientThreadMethod -match 'CodexPlusPlusTaskOperationLock|EnsureCodexPlusPlusTaskFiles|ScheduledTaskUsesHiddenPowerShell|WriteCodexPlusPlusOpenThreadRequest|WaitForCodexPlusPlusOpenThreadResultAsync|schtasks\.exe|Verb\s*=\s*"runas"|powershell\.exe') {
    throw 'Opening an existing task must use the direct codex deep link and must never acquire the elevated task lock or invoke tasks, UAC, or PowerShell.'
}
if ($formSource -notmatch 'SwitchWindowsClientAccountAsync' -or
    $formSource -notmatch 'modelSummary' -or
    $launchAccountMethod -notmatch 'IsSharedProfileAlreadySelected' -or
    $launchAccountMethod -notmatch 'GetWindowsClientDisplayName' -or
    $launchAccountMethod -match 'MessageBox\.Show') {
    throw 'Start must use the local same-profile fast path and launch the selected client without a redundant confirmation dialog.'
}
$launchPowerShellMatch = [regex]::Match(
    $cliServiceSource,
    '(?s)public void LaunchPowerShell\(.*?(?=\r?\n\s*public SharedHistoryMergeResult)')
if (-not $launchPowerShellMatch.Success -or
    $launchPowerShellMatch.Value -match 'ApplyProxyEnvironment\(startInfo\)') {
    throw 'The visible CLI PowerShell must set proxy aliases inside its encoded script instead of mutating ProcessStartInfo.Environment with UseShellExecute enabled.'
}
if ($formSource -notmatch 'LaunchCliAccountAsync' -or
    $formSource -notmatch 'projection\.DefaultCodexHome' -or
    $formSource -notmatch 'projection\.AccountCodexHome' -or
    $cliServiceSource -notmatch 'LaunchPowerShell\(AccountRecord account, string projectPath, string codexHome\)' -or
    $cliServiceSource -notmatch '\$env:CODEX_HOME = \" \+ ToSingleQuoted\(codexHome\)') {
    throw 'CLI launch must use selected account credentials with shared CODEX_HOME chat history.'
}
if ($buildScriptSource -notmatch '\$LASTEXITCODE\s+-ne\s+0') {
    throw 'Build script must stop if dotnet publish fails, instead of self-testing a stale executable.'
}
if ($buildScriptSource -notmatch '--self-contained\s+true' -or
    $selfContainedLauncherSource -notmatch 'dist\\CodexAccountManager\\CodexAccountManager\.exe' -or
    $selfContainedLauncherSource -match 'dotnet\.microsoft\.com|aka\.ms|Start-Process.+https?://|Microsoft\.WindowsDesktop\.App') {
    throw 'The desktop launcher must start only the self-contained dist executable and must never open a runtime download page.'
}

$selfTestOut = [System.IO.Path]::GetTempFileName()
$selfTestErr = [System.IO.Path]::GetTempFileName()
try {
    $selfTestProcess = Start-Process -FilePath $appExe -ArgumentList @('--self-test') -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $selfTestOut -RedirectStandardError $selfTestErr
    $selfTestText = (Get-Content -LiteralPath $selfTestOut -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath $selfTestErr -Raw -ErrorAction SilentlyContinue)
    if ($selfTestProcess.ExitCode -ne 0) {
        throw "Windows client self-test failed: $selfTestText"
    }
    if ($selfTestText -notmatch 'Self test passed') {
        throw 'Windows client self-test did not report success'
    }
}
finally {
    Remove-Item -LiteralPath $selfTestOut, $selfTestErr -ErrorAction SilentlyContinue
}

$accounts = Get-Content -LiteralPath $accountsFile -Raw | ConvertFrom-Json
if ($accounts.Count -ne 3) {
    throw "Expected 3 accounts, found $($accounts.Count)"
}

foreach ($account in $accounts) {
    if ([string]::IsNullOrWhiteSpace($account.name)) {
        throw 'Account name cannot be empty'
    }
    if ([string]::IsNullOrWhiteSpace($account.codexHome)) {
        throw "Account $($account.name) has empty codexHome"
    }
    if (-not (Test-Path -LiteralPath $account.codexHome -PathType Container)) {
        throw "Account $($account.name) directory does not exist: $($account.codexHome)"
    }
    $config = Join-Path $account.codexHome 'config.toml'
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) {
        throw "Account $($account.name) is missing config.toml"
    }
}

& $appScript -ValidateOnly | Out-Null

try {
    & $appScript -SaveAccount -AccountName 'delta' -CodexHome $tempHome2 | Out-Null
    $afterAdd = Get-Content -LiteralPath $accountsFile -Raw | ConvertFrom-Json
    if (@($afterAdd | Where-Object { $_.name -eq 'delta' }).Count -ne 1) {
        throw 'SaveAccount did not add a new account'
    }

    & $appScript -SaveAccount -AccountName 'delta' -CodexHome $tempHome3 | Out-Null
    $afterEdit = Get-Content -LiteralPath $accountsFile -Raw | ConvertFrom-Json
    $edited = $afterEdit | Where-Object { $_.name -eq 'delta' } | Select-Object -First 1
    if ($null -eq $edited -or $edited.codexHome -ne $tempHome3) {
        throw 'SaveAccount did not edit an existing account'
    }

    & $appScript -DeleteAccount -AccountName 'delta' | Out-Null
    $afterDelete = Get-Content -LiteralPath $accountsFile -Raw | ConvertFrom-Json
    if (@($afterDelete | Where-Object { $_.name -eq 'delta' }).Count -ne 0) {
        throw 'DeleteAccount did not remove the selected account'
    }
}
catch {
    throw
}

$statusOutput = & $appScript -CheckLoginStatus -AccountName 'alpha'
if (($statusOutput -join "`n") -notmatch 'Account: alpha') {
    throw 'Login status output did not include the selected account name'
}
if (($statusOutput -join "`n") -match 'sk-[A-Za-z0-9_-]{8,}') {
    throw 'Login status output contains an unmasked API key'
}

$loginCommand = & $appScript -PrintTokenLoginCommand -AccountName 'alpha'
if ($loginCommand -notmatch [regex]::Escape($tempHome1)) {
    throw 'Token login command summary did not include the selected CODEX_HOME'
}
if ($loginCommand -notmatch 'codex login --with-access-token') {
    throw 'Token login command summary did not describe the expected login command'
}
if ($loginCommand -match [regex]::Escape($fakeJwt)) {
    throw 'Token login command summary leaked token input'
}

$expiryOutput = $fakeJwt | & $appScript -ReadTokenExpiry
if (($expiryOutput -join "`n") -notmatch '2030-01-01T00:00:00Z') {
    throw 'Token expiry parser did not report the expected UTC expiry'
}
if (($expiryOutput -join "`n") -match [regex]::Escape($fakeJwt)) {
    throw 'Token expiry parser leaked token input'
}

$sample = & $appScript -PrintLaunchCommand -AccountName 'beta' -ProjectPath $root
if ($sample -isnot [string]) {
    throw 'Launch command must be a single string, not multiple output records'
}
if ($sample -notmatch '; Set-Location') {
    throw 'Launch command must preserve command separators'
}
if ($sample -notmatch [regex]::Escape($sharedCodexHome)) {
    throw 'Launch command did not include the shared CODEX_HOME'
}
if ($sample -match '(access|token|auth)') {
    throw 'Launch command contains sensitive-looking text'
}

$launchOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $sample 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Generated launch command failed: $($launchOutput -join ' ')"
}
$launchText = $launchOutput -join "`n"
if ($launchText -notmatch [regex]::Escape("CODEX_HOME = $sharedCodexHome")) {
    throw 'Generated launch command did not set CODEX_HOME in a child PowerShell'
}
if ($launchText -match '(access|token|auth)') {
    throw 'Launch output contains sensitive-looking text'
}

$tempOut = [System.IO.Path]::GetTempFileName()
$tempErr = [System.IO.Path]::GetTempFileName()
try {
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-Command',
        $sample
    ) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $tempOut -RedirectStandardError $tempErr

    $startProcessOutput = Get-Content -LiteralPath $tempOut -Raw -ErrorAction SilentlyContinue
    $startProcessError = Get-Content -LiteralPath $tempErr -Raw -ErrorAction SilentlyContinue
    if ($process.ExitCode -ne 0) {
        throw "Start-Process launch command failed: $startProcessError"
    }
    if ($startProcessOutput -notmatch [regex]::Escape("CODEX_HOME = $sharedCodexHome")) {
        throw 'Start-Process launch command did not set CODEX_HOME'
    }
}
finally {
    Remove-Item -LiteralPath $tempOut, $tempErr -ErrorAction SilentlyContinue
}

$env:CODEX_SWITCHER_ACCOUNTS_FILE = $oldAccountsFileOverride
$env:CODEX_SWITCHER_TOKEN_METADATA_FILE = $oldMetadataFileOverride
$env:CODEX_ACCOUNT_MANAGER_HOME = $oldAccountManagerHome
$env:CODEX_SWITCHER_CODEX_COMMAND = $oldCodexCommand
$env:CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME = $oldSharedCodexHome
$env:CODEX_PAT_GATEWAY_PROXY = $oldPatGatewayProxy
$env:CODEX_SWITCHER_SKIP_GATEWAY_ENSURE = $oldSkipGatewayEnsure
Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Codex account switcher verification passed.'
