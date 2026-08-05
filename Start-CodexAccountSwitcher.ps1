param(
    [switch]$ValidateOnly,
    [switch]$PrintLaunchCommand,
    [switch]$PrintTokenLoginCommand,
    [switch]$CheckLoginStatus,
    [switch]$ReadTokenExpiry,
    [switch]$LoginWithAccessToken,
    [switch]$SaveAccount,
    [switch]$DeleteAccount,
    [string]$AccountName,
    [string]$OriginalAccountName,
    [string]$CodexHome,
    [string]$ProjectPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:ManagerHome = if (-not [string]::IsNullOrWhiteSpace($env:CODEX_ACCOUNT_MANAGER_HOME)) {
    [System.IO.Path]::GetFullPath($env:CODEX_ACCOUNT_MANAGER_HOME)
} elseif (-not [string]::IsNullOrWhiteSpace($env:CODEX_SWITCHER_ACCOUNTS_FILE)) {
    $accountsOverridePath = [System.IO.Path]::GetFullPath($env:CODEX_SWITCHER_ACCOUNTS_FILE)
    $accountsOverrideParent = Split-Path -Parent $accountsOverridePath
    if ([string]::IsNullOrWhiteSpace($accountsOverrideParent)) {
        $script:Root
    } else {
        $accountsOverrideParent
    }
} else {
    $script:Root
}
$script:AccountsFile = if ([string]::IsNullOrWhiteSpace($env:CODEX_SWITCHER_ACCOUNTS_FILE)) {
    Join-Path -Path $script:ManagerHome -ChildPath 'accounts.json'
} else {
    $env:CODEX_SWITCHER_ACCOUNTS_FILE
}
$script:TokenMetadataFile = if ([string]::IsNullOrWhiteSpace($env:CODEX_SWITCHER_TOKEN_METADATA_FILE)) {
    Join-Path -Path $script:ManagerHome -ChildPath 'token-metadata.json'
} else {
    $env:CODEX_SWITCHER_TOKEN_METADATA_FILE
}
$script:AppIconFile = Join-Path -Path $script:Root -ChildPath 'assets\CodexAccountManager.ico'
$script:AppSettingsFile = Join-Path -Path $script:ManagerHome -ChildPath 'appsettings.json'

function Get-SharedCodexHome {
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME)) {
        return [System.IO.Path]::GetFullPath($env:CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME)
    }

    return (Join-Path -Path $env:USERPROFILE -ChildPath '.codex')
}

function Read-AccountConfig {
    if (-not (Test-Path -LiteralPath $script:AccountsFile -PathType Leaf)) {
        return @()
    }

    $accounts = Get-Content -LiteralPath $script:AccountsFile -Raw | ConvertFrom-Json
    $accountList = @($accounts)
    if ($null -eq $accounts -or $accountList.Count -eq 0) {
        throw 'Accounts file is empty.'
    }

    return $accountList
}

function Write-AccountConfig {
    param([Parameter(Mandatory)][object[]]$Accounts)

    $parent = Split-Path -Parent $script:AccountsFile
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    @($Accounts) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $script:AccountsFile -Encoding UTF8
}

function Get-DefaultCodexConfig {
    return @'
model = "gpt-5.6-terra"
review_model = "gpt-5.6-terra"
model_reasoning_effort = "medium"
disable_response_storage = true
model_provider = "codex_account_manager"
service_tier = "default"

[model_providers.codex_account_manager]
name = "OpenAI Token HTTP"
base_url = "http://127.0.0.1:8317/backend-api/codex"
wire_api = "responses"
requires_openai_auth = true
supports_websockets = false
stream_max_retries = 0
request_max_retries = 1

approval_policy = "never"
sandbox_mode = "danger-full-access"

[windows]
sandbox = "elevated"
'@
}

function Ensure-CodexHomeReady {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }

    $config = Join-Path -Path $Path -ChildPath 'config.toml'
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) {
        Get-DefaultCodexConfig | Set-Content -LiteralPath $config -Encoding UTF8
    }
}

function Merge-SharedMcpServerConfig {
    param(
        [Parameter(Mandatory)][string]$SharedConfigText,
        [Parameter(Mandatory)][string]$ProjectedConfigText
    )

    $preserved = [System.Collections.Generic.List[string]]::new()
    $inMcpServerSection = $false
    foreach ($line in (($SharedConfigText -replace "`r`n", "`n" -replace "`r", "`n") -split "`n")) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[\[?.+\]\]?$') {
            $inMcpServerSection = $trimmed -match '^\[\[?\s*mcp_servers(?:\.|\s*\])'
        }
        if ($inMcpServerSection) {
            $preserved.Add($line)
        }
    }

    while ($preserved.Count -gt 0 -and [string]::IsNullOrWhiteSpace($preserved[$preserved.Count - 1])) {
        $preserved.RemoveAt($preserved.Count - 1)
    }
    if ($preserved.Count -eq 0) {
        return $ProjectedConfigText
    }

    $output = [System.Collections.Generic.List[string]]::new()
    $inMcpServerSection = $false
    foreach ($line in (($ProjectedConfigText -replace "`r`n", "`n" -replace "`r", "`n") -split "`n")) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[\[?.+\]\]?$') {
            $inMcpServerSection = $trimmed -match '^\[\[?\s*mcp_servers(?:\.|\s*\])'
        }
        if (-not $inMcpServerSection) {
            $output.Add($line)
        }
    }

    while ($output.Count -gt 0 -and [string]::IsNullOrWhiteSpace($output[$output.Count - 1])) {
        $output.RemoveAt($output.Count - 1)
    }
    $output.Add('')
    $output.AddRange([string[]]$preserved)
    return (($output -join [Environment]::NewLine).TrimEnd() + [Environment]::NewLine)
}

function Use-SharedCodexHomeForAccount {
    param([Parameter(Mandatory)][pscustomobject]$Account)

    $accountHome = [System.IO.Path]::GetFullPath([string]$Account.codexHome)
    $sharedHome = Get-SharedCodexHome
    Ensure-CodexHomeReady -Path $sharedHome

    $sourceAuth = Join-Path -Path $accountHome -ChildPath 'auth.json'
    $sourceConfig = Join-Path -Path $accountHome -ChildPath 'config.toml'
    if (-not (Test-Path -LiteralPath $sourceAuth -PathType Leaf)) {
        throw "Account $($Account.name) is missing auth.json: $sourceAuth"
    }
    if (-not (Test-Path -LiteralPath $sourceConfig -PathType Leaf)) {
        throw "Account $($Account.name) is missing config.toml: $sourceConfig"
    }

    $backupDir = Join-Path -Path $sharedHome -ChildPath ('account-switcher-backups\' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff'))
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    foreach ($fileName in @('auth.json', '.cockpit_codex_auth.json', 'config.toml')) {
        $target = Join-Path -Path $sharedHome -ChildPath $fileName
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            Copy-Item -LiteralPath $target -Destination (Join-Path -Path $backupDir -ChildPath $fileName) -Force
        }
    }

    Copy-Item -LiteralPath $sourceAuth -Destination (Join-Path -Path $sharedHome -ChildPath 'auth.json') -Force
    $sharedConfig = Join-Path -Path $sharedHome -ChildPath 'config.toml'
    $sharedConfigText = if (Test-Path -LiteralPath $sharedConfig -PathType Leaf) {
        Get-Content -LiteralPath $sharedConfig -Raw -Encoding UTF8
    } else {
        ''
    }
    $sourceConfigText = Get-Content -LiteralPath $sourceConfig -Raw -Encoding UTF8
    $mergedConfigText = Merge-SharedMcpServerConfig -SharedConfigText $sharedConfigText -ProjectedConfigText $sourceConfigText
    [System.IO.File]::WriteAllText(
        $sharedConfig,
        $mergedConfigText,
        [System.Text.UTF8Encoding]::new($false))
    $cockpitAuth = Join-Path -Path $sharedHome -ChildPath '.cockpit_codex_auth.json'
    if (Test-Path -LiteralPath $cockpitAuth -PathType Leaf) {
        Remove-Item -LiteralPath $cockpitAuth -Force
    }

    return $sharedHome
}

function Save-CodexAccount {
    param(
        [Parameter(Mandatory)][object[]]$Accounts,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()][string]$OriginalName
    )

    $trimmedName = $Name.Trim()
    $trimmedPath = $Path.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmedName)) {
        throw 'Account name cannot be empty.'
    }
    if ([string]::IsNullOrWhiteSpace($trimmedPath)) {
        throw 'CODEX_HOME cannot be empty.'
    }

    Ensure-CodexHomeReady -Path $trimmedPath
    $targetName = if ([string]::IsNullOrWhiteSpace($OriginalName)) { $trimmedName } else { $OriginalName.Trim() }
    $keptAccounts = @($Accounts | Where-Object { $_.name -ne $targetName -and $_.name -ne $trimmedName })
    $newAccount = [pscustomobject]@{
        name = $trimmedName
        codexHome = $trimmedPath
    }

    return @($keptAccounts + $newAccount | Sort-Object name)
}

function Remove-CodexAccount {
    param(
        [Parameter(Mandatory)][object[]]$Accounts,
        [Parameter(Mandatory)][string]$Name
    )

    $trimmedName = $Name.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmedName)) {
        throw 'Account name cannot be empty.'
    }

    return @($Accounts | Where-Object { $_.name -ne $trimmedName })
}

function Get-AccountState {
    param([Parameter(Mandatory)][pscustomobject]$Account)

    $codexHome = [string]$Account.codexHome
    $configPath = Join-Path -Path $codexHome -ChildPath 'config.toml'
    $homeExists = Test-Path -LiteralPath $codexHome -PathType Container
    $configExists = Test-Path -LiteralPath $configPath -PathType Leaf

    if (-not $homeExists) {
        return 'Missing folder'
    }
    if (-not $configExists) {
        return 'Missing config'
    }
    return 'Ready'
}

function Test-AccountConfig {
    param([Parameter(Mandatory)][object[]]$Accounts)

    foreach ($account in $Accounts) {
        $name = [string]$account.name
        $codexHome = [string]$account.codexHome

        if ([string]::IsNullOrWhiteSpace($name)) {
            throw 'Account name cannot be empty.'
        }
        if ([string]::IsNullOrWhiteSpace($codexHome)) {
            throw "Account $name has empty CODEX_HOME."
        }
        if (-not (Test-Path -LiteralPath $codexHome -PathType Container)) {
            throw "Account $name folder does not exist: $codexHome"
        }

        $config = Join-Path -Path $codexHome -ChildPath 'config.toml'
        if (-not (Test-Path -LiteralPath $config -PathType Leaf)) {
            throw "Account $name is missing config.toml: $config"
        }
    }
}

function ConvertTo-SingleQuotedLiteral {
    param([Parameter(Mandatory)][string]$Value)
    return "'" + ($Value -replace "'", "''") + "'"
}

function Get-CodexProxyUri {
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_PAT_GATEWAY_PROXY)) {
        return [string]$env:CODEX_PAT_GATEWAY_PROXY
    }

    if (Test-Path -LiteralPath $script:AppSettingsFile -PathType Leaf) {
        try {
            $settings = Get-Content -LiteralPath $script:AppSettingsFile -Raw | ConvertFrom-Json
            if (-not [string]::IsNullOrWhiteSpace([string]$settings.PatGatewayProxy)) {
                return [string]$settings.PatGatewayProxy
            }
        }
        catch {
            # Fall through to environment and Windows proxy discovery.
        }
    }

    foreach ($candidate in @(
        $env:HTTPS_PROXY,
        $env:HTTP_PROXY,
        $env:ALL_PROXY,
        $env:https_proxy,
        $env:http_proxy,
        $env:all_proxy
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            return [string]$candidate
        }
    }

    try {
        $settings = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
        if ([int]$settings.ProxyEnable -eq 0 -or [string]::IsNullOrWhiteSpace([string]$settings.ProxyServer)) {
            return $null
        }
        $server = [string]$settings.ProxyServer
        if ($server -match '(^|;)https?=([^;]+)') {
            $server = $Matches[2]
        }
        if ($server -notmatch '^[a-z][a-z0-9+.-]*://') {
            $server = 'http://' + $server
        }
        return $server
    }
    catch {
        return $null
    }
}

function Set-CodexProxyEnvironment {
    param(
        [Parameter(Mandatory)][System.Diagnostics.ProcessStartInfo]$StartInfo
    )

    $proxyUri = Get-CodexProxyUri
    if ([string]::IsNullOrWhiteSpace($proxyUri)) {
        return
    }
    foreach ($proxyName in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy')) {
        $StartInfo.EnvironmentVariables[$proxyName] = $proxyUri
    }
    foreach ($bypassName in @('NO_PROXY', 'no_proxy')) {
        $StartInfo.EnvironmentVariables[$bypassName] = '127.0.0.1,localhost,::1'
    }
}

function Invoke-ManagerLauncherCommand {
    param(
        [Parameter(Mandatory)][string]$Launcher,
        [Parameter(Mandatory)][string]$Argument,
        [int]$TimeoutMilliseconds = 30000,
        [bool]$CaptureOutput = $true
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Launcher
    $startInfo.Arguments = $Argument
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $CaptureOutput
    $startInfo.RedirectStandardError = $CaptureOutput
    $startInfo.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        if ($CaptureOutput) {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
        }
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try {
                $process.Kill()
            }
            catch {
            }
            throw "Manager command timed out: $Argument"
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = if ($CaptureOutput) {
                (($stdoutTask.Result, $stderrTask.Result) |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
            }
            else {
                ""
            }
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-LocalPatConfigMigration {
    param(
        [Parameter(Mandatory)][string]$Launcher,
        [Parameter(Mandatory)][string]$Dotnet,
        [Parameter(Mandatory)][string]$Assembly
    )

    if ((Test-Path -LiteralPath $Dotnet -PathType Leaf) -and
        (Test-Path -LiteralPath $Assembly -PathType Leaf)) {
        $output = @(& $Dotnet $Assembly '--migrate-local-pat-configs' 2>&1)
        $exitCode = $LASTEXITCODE
    }
    elseif (Test-Path -LiteralPath $Launcher -PathType Leaf) {
        $result = Invoke-ManagerLauncherCommand -Launcher $Launcher -Argument '--migrate-local-pat-configs'
        $output = @($result.Output)
        $exitCode = $result.ExitCode
    }
    else {
        throw 'Local PAT gateway launcher is missing. Build the Codex Account Manager first.'
    }

    if ($exitCode -ne 0) {
        throw (($output | Out-String).Trim())
    }
}

function Ensure-LocalPatGateway {
    # The verification suite supplies a fake/isolated Codex command and may run while the
    # owner's real gateway is already serving 8317.  In that explicit test-only mode, do not
    # restart or replace the live gateway just to exercise status-output formatting.
    if ($env:CODEX_SWITCHER_SKIP_GATEWAY_ENSURE -eq '1' -and
        -not [string]::IsNullOrWhiteSpace($env:CODEX_SWITCHER_CODEX_COMMAND)) {
        return
    }

    $launcher = Join-Path $script:Root 'dist\CodexAccountManager\CodexAccountManager.exe'
    $dotnet = Join-Path $script:Root '.tools\dotnet\dotnet.exe'
    $assembly = Join-Path $script:Root 'src\CodexAccountManager\bin\Release\net10.0-windows\CodexAccountManager.dll'
    $oldManagerHome = $env:CODEX_ACCOUNT_MANAGER_HOME
    if ([string]::IsNullOrWhiteSpace($oldManagerHome)) {
        $env:CODEX_ACCOUNT_MANAGER_HOME = $script:ManagerHome
    }

    try {
        # Upgrade existing PAT profiles before status/login so the script entry point
        # follows the same local-gateway path as the WinForms manager.
        Invoke-LocalPatConfigMigration -Launcher $launcher -Dotnet $dotnet -Assembly $assembly
        if (Test-Path -LiteralPath $launcher -PathType Leaf) {
            $result = Invoke-ManagerLauncherCommand -Launcher $launcher -Argument '--ensure-local-pat-gateway' -CaptureOutput $false
            $output = @($result.Output)
            $exitCode = $result.ExitCode
        }
        elseif ((Test-Path -LiteralPath $dotnet -PathType Leaf) -and
                (Test-Path -LiteralPath $assembly -PathType Leaf)) {
            # The gateway is a long-lived child of this framework-dependent host.
            # Do not capture its inherited stdout/stderr handles here: PowerShell can
            # otherwise wait for the child gateway to close the redirected pipes.
            & $dotnet $assembly '--ensure-local-pat-gateway'
            $output = @()
            $exitCode = $LASTEXITCODE
        }
        else {
            throw 'Local PAT gateway launcher is missing. Build the Codex Account Manager first.'
        }

        if ($exitCode -ne 0) {
            $detail = ($output | Out-String).Trim()
            $message = if ([string]::IsNullOrWhiteSpace($detail)) {
                'Local PAT gateway could not be started.'
            }
            else {
                $detail
            }
            throw $message
        }
    }
    finally {
        if ([string]::IsNullOrWhiteSpace($oldManagerHome)) {
            Remove-Item Env:CODEX_ACCOUNT_MANAGER_HOME -ErrorAction SilentlyContinue
        }
        else {
            $env:CODEX_ACCOUNT_MANAGER_HOME = $oldManagerHome
        }
    }
}

function Get-AccountByName {
    param(
        [Parameter(Mandatory)][object[]]$Accounts,
        [Parameter(Mandatory)][string]$Name
    )

    $selected = $Accounts | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if ($null -eq $selected) {
        throw "Unknown account: $Name"
    }
    return [pscustomobject]$selected
}

function Protect-SensitiveText {
    param([AllowNull()][string]$Text)

    if ($null -eq $Text) {
        return ''
    }

    $safe = $Text
    $safe = $safe -replace 'sk-[A-Za-z0-9_-]{8,}', 'sk-***'
    $safe = $safe -replace 'eyJ[A-Za-z0-9._-]{20,}', '<redacted-token>'
    $safe = $safe -replace '(?i)(access[_ -]?token\s*[:=]\s*)\S+', '${1}<redacted>'
    return $safe.Trim()
}

function New-CodexProcessStartInfo {
    param(
        [Parameter(Mandatory)][string]$Arguments,
        [Parameter(Mandatory)][string]$CodexHome
    )

    $command = if ([string]::IsNullOrWhiteSpace($env:CODEX_SWITCHER_CODEX_COMMAND)) {
        'codex'
    } else {
        $env:CODEX_SWITCHER_CODEX_COMMAND
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $command
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['CODEX_HOME'] = $CodexHome
    $psi.EnvironmentVariables['CODEX_SQLITE_HOME'] = $CodexHome
    Set-CodexProxyEnvironment -StartInfo $psi
    return $psi
}

function Invoke-CodexProcess {
    param(
        [Parameter(Mandatory)][string]$Arguments,
        [Parameter(Mandatory)][string]$CodexHome,
        [AllowNull()][string]$StandardInputText
    )

    $psi = New-CodexProcessStartInfo -Arguments $Arguments -CodexHome $CodexHome
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    [void]$process.Start()
    if ($null -ne $StandardInputText) {
        $process.StandardInput.WriteLine($StandardInputText)
    }
    $process.StandardInput.Close()

    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    if (-not $process.WaitForExit(120000)) {
        try {
            $process.Kill()
        } catch {
        }
        throw 'Codex command timed out.'
    }

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = Protect-SensitiveText $stdout
        StdErr = Protect-SensitiveText $stderr
    }
}

function Invoke-CodexLoginStatus {
    param([Parameter(Mandatory)][pscustomobject]$Account)

    Ensure-LocalPatGateway
    $codexHome = [string]$Account.codexHome
    $result = Invoke-CodexProcess -Arguments 'login status' -CodexHome $codexHome -StandardInputText $null
    $statusText = (($result.StdOut, $result.StdErr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($statusText)) {
        $statusText = 'No status output.'
    }

    return [pscustomobject]@{
        Account = [string]$Account.name
        CodexHome = $codexHome
        ExitCode = $result.ExitCode
        Text = $statusText
    }
}

function Format-LoginStatus {
    param([Parameter(Mandatory)][pscustomobject]$Status)

    return @(
        "Account: $($Status.Account)"
        "CODEX_HOME: $($Status.CodexHome)"
        "Exit code: $($Status.ExitCode)"
        "Codex status: $($Status.Text)"
    ) -join "`n"
}

function Invoke-CodexTokenLogin {
    param(
        [Parameter(Mandatory)][pscustomobject]$Account,
        [Parameter(Mandatory)][string]$AccessToken
    )

    Ensure-LocalPatGateway
    $codexHome = [string]$Account.codexHome
    return Invoke-CodexProcess -Arguments 'login --with-access-token' -CodexHome $codexHome -StandardInputText $AccessToken
}

function New-TokenLoginCommandSummary {
    param([Parameter(Mandatory)][pscustomobject]$Account)

    return "CODEX_HOME=$($Account.codexHome); codex login --with-access-token; token is read from hidden input/stdin"
}

function ConvertFrom-Base64Url {
    param([Parameter(Mandatory)][string]$Value)

    $base64 = $Value.Replace('-', '+').Replace('_', '/')
    switch ($base64.Length % 4) {
        2 { $base64 += '==' }
        3 { $base64 += '=' }
        1 { return $null }
    }

    try {
        return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($base64))
    } catch {
        return $null
    }
}

function Get-AccessTokenExpiryUtc {
    param([Parameter(Mandatory)][string]$AccessToken)

    $parts = $AccessToken.Split('.')
    if ($parts.Count -lt 2) {
        return $null
    }

    $payloadJson = ConvertFrom-Base64Url -Value $parts[1]
    if ([string]::IsNullOrWhiteSpace($payloadJson)) {
        return $null
    }

    try {
        $payload = $payloadJson | ConvertFrom-Json
        if ($null -eq $payload.exp) {
            return $null
        }
        return [DateTimeOffset]::FromUnixTimeSeconds([int64]$payload.exp).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
    } catch {
        return $null
    }
}

function Read-TokenMetadata {
    if (-not (Test-Path -LiteralPath $script:TokenMetadataFile -PathType Leaf)) {
        return @{}
    }

    try {
        $raw = Get-Content -LiteralPath $script:TokenMetadataFile -Raw
        $data = $raw | ConvertFrom-Json
        $metadata = @{}
        foreach ($property in $data.PSObject.Properties) {
            $metadata[$property.Name] = $property.Value
        }
        return $metadata
    } catch {
        return @{}
    }
}

function Write-TokenMetadata {
    param(
        [Parameter(Mandatory)][string]$AccountName,
        [AllowNull()][string]$ExpiresAtUtc
    )

    $metadata = Read-TokenMetadata
    $metadata[$AccountName] = [pscustomobject]@{
        updatedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        expiresAtUtc = $ExpiresAtUtc
    }
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $script:TokenMetadataFile -Encoding UTF8
}

function Get-TokenExpiryLabel {
    param([Parameter(Mandatory)][string]$AccountName)

    $metadata = Read-TokenMetadata
    if (-not $metadata.ContainsKey($AccountName)) {
        return 'Unknown'
    }

    $entry = $metadata[$AccountName]
    if ($null -eq $entry.expiresAtUtc -or [string]::IsNullOrWhiteSpace([string]$entry.expiresAtUtc)) {
        return 'Unknown'
    }

    return [string]$entry.expiresAtUtc
}

function Read-SecretInputText {
    param([AllowNull()][object[]]$PipelineInput)

    $pipelineText = @($PipelineInput) -join "`n"
    if (-not [string]::IsNullOrWhiteSpace($pipelineText)) {
        return $pipelineText.TrimEnd("`r", "`n")
    }

    return [Console]::In.ReadToEnd().TrimEnd("`r", "`n")
}

function New-CodexLaunchCommand {
    param(
        [Parameter(Mandatory)][string]$CodexHome,
        [Parameter(Mandatory)][string]$ProjectPath
    )

    $homeLiteral = ConvertTo-SingleQuotedLiteral $CodexHome
    $projectLiteral = ConvertTo-SingleQuotedLiteral $ProjectPath

    $parts = @(
        ('$env:CODEX_HOME = ' + $homeLiteral)
        ('$env:CODEX_SQLITE_HOME = ' + $homeLiteral)
        ('Set-Location -LiteralPath ' + $projectLiteral)
        '$Host.UI.RawUI.WindowTitle = ''Codex CLI - '' + $env:CODEX_HOME'
        'Write-Host '''''
        'Write-Host (''CODEX_HOME = '' + $env:CODEX_HOME) -ForegroundColor Green'
        'Write-Host (''Current folder = '' + (Get-Location).Path)'
        'Write-Host '''''
        'Write-Host ''Account switched. Run: codex -C .'''
    )

    $proxyUri = Get-CodexProxyUri
    if (-not [string]::IsNullOrWhiteSpace($proxyUri)) {
        $proxyLiteral = ConvertTo-SingleQuotedLiteral $proxyUri
        $proxyParts = foreach ($proxyName in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy')) {
            ('$env:' + $proxyName + ' = ' + $proxyLiteral)
        }
        $proxyParts += @(
            '$env:NO_PROXY = ''127.0.0.1,localhost,::1'''
            '$env:no_proxy = ''127.0.0.1,localhost,::1'''
        )
        $parts = @($parts[0], $parts[1]) + @($proxyParts) + @($parts[2..($parts.Count - 1)])
    }

    return ($parts -join '; ')
}

function Start-CodexPowerShell {
    param(
        [Parameter(Mandatory)][pscustomobject]$Account,
        [Parameter(Mandatory)][string]$ProjectPath
    )

    if ((Get-AccountState -Account $Account) -ne 'Ready') {
        throw "Account $($Account.name) is not ready. Check its folder and config.toml."
    }
    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Container)) {
        throw "Project folder does not exist: $ProjectPath"
    }

    Ensure-LocalPatGateway
    $codexHome = Use-SharedCodexHomeForAccount -Account $Account
    $command = New-CodexLaunchCommand -CodexHome $codexHome -ProjectPath $ProjectPath
    Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoExit',
        '-ExecutionPolicy',
        'Bypass',
        '-Command',
        $command
    ) -WorkingDirectory $ProjectPath
}

$accounts = Read-AccountConfig

if ($ValidateOnly) {
    Test-AccountConfig -Accounts $accounts
    Write-Output 'OK'
    return
}

if ($SaveAccount) {
    if ([string]::IsNullOrWhiteSpace($AccountName)) {
        throw 'Provide -AccountName with -SaveAccount.'
    }
    if ([string]::IsNullOrWhiteSpace($CodexHome)) {
        throw 'Provide -CodexHome with -SaveAccount.'
    }

    $accounts = Save-CodexAccount -Accounts $accounts -Name $AccountName -Path $CodexHome -OriginalName $OriginalAccountName
    Write-AccountConfig -Accounts $accounts
    Write-Output "Saved account: $AccountName"
    return
}

if ($DeleteAccount) {
    if ([string]::IsNullOrWhiteSpace($AccountName)) {
        throw 'Provide -AccountName with -DeleteAccount.'
    }

    $accounts = Remove-CodexAccount -Accounts $accounts -Name $AccountName
    Write-AccountConfig -Accounts $accounts
    Write-Output "Deleted account from list: $AccountName"
    return
}

if ($ReadTokenExpiry) {
    $tokenInput = Read-SecretInputText -PipelineInput @($input)
    $expiry = if ([string]::IsNullOrWhiteSpace($tokenInput)) {
        $null
    } else {
        Get-AccessTokenExpiryUtc -AccessToken $tokenInput
    }

    if ([string]::IsNullOrWhiteSpace($expiry)) {
        Write-Output 'Expires at (UTC): unknown'
    } else {
        Write-Output "Expires at (UTC): $expiry"
    }
    Remove-Variable tokenInput -ErrorAction SilentlyContinue
    return
}

if ($PrintTokenLoginCommand) {
    if ([string]::IsNullOrWhiteSpace($AccountName)) {
        throw 'Provide -AccountName with -PrintTokenLoginCommand.'
    }

    $selected = Get-AccountByName -Accounts $accounts -Name $AccountName
    New-TokenLoginCommandSummary -Account $selected
    return
}

if ($CheckLoginStatus) {
    if ([string]::IsNullOrWhiteSpace($AccountName)) {
        throw 'Provide -AccountName with -CheckLoginStatus.'
    }

    $selected = Get-AccountByName -Accounts $accounts -Name $AccountName
    $status = Invoke-CodexLoginStatus -Account $selected
    Format-LoginStatus -Status $status
    if ($status.ExitCode -ne 0) {
        exit [int]$status.ExitCode
    }
    return
}

if ($LoginWithAccessToken) {
    if ([string]::IsNullOrWhiteSpace($AccountName)) {
        throw 'Provide -AccountName with -LoginWithAccessToken.'
    }

    $selected = Get-AccountByName -Accounts $accounts -Name $AccountName
    $tokenInput = Read-SecretInputText -PipelineInput @($input)
    if ([string]::IsNullOrWhiteSpace($tokenInput)) {
        throw 'Access token input is empty.'
    }

    $expiry = Get-AccessTokenExpiryUtc -AccessToken $tokenInput
    try {
        $loginResult = Invoke-CodexTokenLogin -Account $selected -AccessToken $tokenInput
    } finally {
        Remove-Variable tokenInput -ErrorAction SilentlyContinue
    }

    if ($loginResult.ExitCode -ne 0) {
        throw "Codex login failed: $($loginResult.StdErr) $($loginResult.StdOut)"
    }

    Write-TokenMetadata -AccountName ([string]$selected.name) -ExpiresAtUtc $expiry
    $status = Invoke-CodexLoginStatus -Account $selected
    Format-LoginStatus -Status $status
    if ($status.ExitCode -ne 0) {
        exit [int]$status.ExitCode
    }
    return
}

if ($PrintLaunchCommand) {
    if ([string]::IsNullOrWhiteSpace($AccountName)) {
        throw 'Provide -AccountName with -PrintLaunchCommand.'
    }
    if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        $ProjectPath = $script:Root
    }

    $selected = Get-AccountByName -Accounts $accounts -Name $AccountName

    $sharedCodexHome = Get-SharedCodexHome
    Ensure-CodexHomeReady -Path $sharedCodexHome
    New-CodexLaunchCommand -CodexHome $sharedCodexHome -ProjectPath $ProjectPath
    return
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Codex Account Switcher'
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(860, 520)
$form.MinimumSize = New-Object System.Drawing.Size(820, 480)

$titleLabel = New-Object System.Windows.Forms.Label
$titleLabel.Text = 'Select an account, then open a new PowerShell window'
$titleLabel.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 11, [System.Drawing.FontStyle]::Bold)
$titleLabel.Location = New-Object System.Drawing.Point(18, 16)
$titleLabel.Size = New-Object System.Drawing.Size(600, 28)
$form.Controls.Add($titleLabel)

$listView = New-Object System.Windows.Forms.ListView
$listView.Location = New-Object System.Drawing.Point(20, 55)
$listView.Size = New-Object System.Drawing.Size(805, 210)
$listView.View = [System.Windows.Forms.View]::Details
$listView.FullRowSelect = $true
$listView.MultiSelect = $false
$listView.GridLines = $true
[void]$listView.Columns.Add('Account', 95)
[void]$listView.Columns.Add('Config', 105)
[void]$listView.Columns.Add('Token expiry', 185)
[void]$listView.Columns.Add('CODEX_HOME', 390)
$form.Controls.Add($listView)

$folderLabel = New-Object System.Windows.Forms.Label
$folderLabel.Text = 'PowerShell folder'
$folderLabel.Location = New-Object System.Drawing.Point(20, 282)
$folderLabel.Size = New-Object System.Drawing.Size(140, 22)
$form.Controls.Add($folderLabel)

$folderTextBox = New-Object System.Windows.Forms.TextBox
$folderTextBox.Location = New-Object System.Drawing.Point(175, 279)
$folderTextBox.Size = New-Object System.Drawing.Size(545, 26)
$folderTextBox.Text = if ([string]::IsNullOrWhiteSpace($ProjectPath)) { $script:Root } else { $ProjectPath }
$form.Controls.Add($folderTextBox)

$browseButton = New-Object System.Windows.Forms.Button
$browseButton.Text = 'Browse...'
$browseButton.Location = New-Object System.Drawing.Point(730, 277)
$browseButton.Size = New-Object System.Drawing.Size(85, 30)
$form.Controls.Add($browseButton)

$launchButton = New-Object System.Windows.Forms.Button
$launchButton.Text = 'Switch and Open PowerShell'
$launchButton.Location = New-Object System.Drawing.Point(20, 325)
$launchButton.Size = New-Object System.Drawing.Size(190, 34)
$form.Controls.Add($launchButton)

$statusButton = New-Object System.Windows.Forms.Button
$statusButton.Text = 'Check Status'
$statusButton.Location = New-Object System.Drawing.Point(220, 325)
$statusButton.Size = New-Object System.Drawing.Size(120, 34)
$form.Controls.Add($statusButton)

$tokenButton = New-Object System.Windows.Forms.Button
$tokenButton.Text = 'Update Token'
$tokenButton.Location = New-Object System.Drawing.Point(350, 325)
$tokenButton.Size = New-Object System.Drawing.Size(125, 34)
$form.Controls.Add($tokenButton)

$refreshButton = New-Object System.Windows.Forms.Button
$refreshButton.Text = 'Refresh'
$refreshButton.Location = New-Object System.Drawing.Point(485, 325)
$refreshButton.Size = New-Object System.Drawing.Size(95, 34)
$form.Controls.Add($refreshButton)

$closeButton = New-Object System.Windows.Forms.Button
$closeButton.Text = 'Close'
$closeButton.Location = New-Object System.Drawing.Point(590, 325)
$closeButton.Size = New-Object System.Drawing.Size(70, 34)
$form.Controls.Add($closeButton)

$statusTextBox = New-Object System.Windows.Forms.TextBox
$statusTextBox.Location = New-Object System.Drawing.Point(20, 378)
$statusTextBox.Size = New-Object System.Drawing.Size(805, 70)
$statusTextBox.Multiline = $true
$statusTextBox.ReadOnly = $true
$statusTextBox.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$form.Controls.Add($statusTextBox)

function Update-AccountList {
    $listView.Items.Clear()
    foreach ($account in $accounts) {
        $state = Get-AccountState -Account $account
        $item = New-Object System.Windows.Forms.ListViewItem([string]$account.name)
        [void]$item.SubItems.Add($state)
        [void]$item.SubItems.Add((Get-TokenExpiryLabel -AccountName ([string]$account.name)))
        [void]$item.SubItems.Add([string]$account.codexHome)
        $item.Tag = $account

        if ($state -eq 'Ready') {
            $item.ForeColor = [System.Drawing.Color]::FromArgb(20, 110, 60)
        } else {
            $item.ForeColor = [System.Drawing.Color]::FromArgb(180, 60, 40)
        }

        [void]$listView.Items.Add($item)
    }

    if ($listView.Items.Count -gt 0 -and $listView.SelectedItems.Count -eq 0) {
        $listView.Items[0].Selected = $true
    }
}

function Get-SelectedAccountFromList {
    if ($listView.SelectedItems.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show('Select one account first.', 'Codex Account Switcher') | Out-Null
        return $null
    }

    return [pscustomobject]$listView.SelectedItems[0].Tag
}

function Invoke-LaunchSelected {
    $selected = Get-SelectedAccountFromList
    if ($null -eq $selected) {
        return
    }

    try {
        Start-CodexPowerShell -Account $selected -ProjectPath $folderTextBox.Text
    } catch {
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Cannot open PowerShell') | Out-Null
    }
}

function Invoke-CheckSelectedStatus {
    $selected = Get-SelectedAccountFromList
    if ($null -eq $selected) {
        return
    }

    try {
        $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        $status = Invoke-CodexLoginStatus -Account $selected
        $expiry = Get-TokenExpiryLabel -AccountName ([string]$selected.name)
        $statusTextBox.Text = (Format-LoginStatus -Status $status) + "`r`nToken expiry: $expiry"
    } catch {
        $statusTextBox.Text = $_.Exception.Message
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Status check failed') | Out-Null
    } finally {
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
    }
}

function Show-TokenInputDialog {
    param([Parameter(Mandatory)][pscustomobject]$Account)

    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = 'Update Codex Token'
    $dialog.StartPosition = 'CenterParent'
    $dialog.Size = New-Object System.Drawing.Size(560, 190)
    $dialog.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false

    $label = New-Object System.Windows.Forms.Label
    $label.Text = "Paste access token for account: $($Account.name)"
    $label.Location = New-Object System.Drawing.Point(16, 18)
    $label.Size = New-Object System.Drawing.Size(500, 24)
    $dialog.Controls.Add($label)

    $tokenBox = New-Object System.Windows.Forms.TextBox
    $tokenBox.Location = New-Object System.Drawing.Point(18, 54)
    $tokenBox.Size = New-Object System.Drawing.Size(505, 26)
    $tokenBox.UseSystemPasswordChar = $true
    $dialog.Controls.Add($tokenBox)

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = 'The token is sent to codex through stdin and is not saved by this tool.'
    $hint.Location = New-Object System.Drawing.Point(18, 88)
    $hint.Size = New-Object System.Drawing.Size(505, 22)
    $dialog.Controls.Add($hint)

    $okButton = New-Object System.Windows.Forms.Button
    $okButton.Text = 'Update'
    $okButton.Location = New-Object System.Drawing.Point(350, 118)
    $okButton.Size = New-Object System.Drawing.Size(80, 30)
    $okButton.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dialog.Controls.Add($okButton)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = 'Cancel'
    $cancelButton.Location = New-Object System.Drawing.Point(442, 118)
    $cancelButton.Size = New-Object System.Drawing.Size(80, 30)
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dialog.Controls.Add($cancelButton)

    $dialog.AcceptButton = $okButton
    $dialog.CancelButton = $cancelButton

    if ($dialog.ShowDialog($form) -ne [System.Windows.Forms.DialogResult]::OK) {
        return $null
    }

    $token = $tokenBox.Text.Trim()
    $tokenBox.Clear()
    return $token
}

function Invoke-UpdateSelectedToken {
    $selected = Get-SelectedAccountFromList
    if ($null -eq $selected) {
        return
    }

    $token = Show-TokenInputDialog -Account $selected
    if ([string]::IsNullOrWhiteSpace($token)) {
        return
    }

    try {
        $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        $expiry = Get-AccessTokenExpiryUtc -AccessToken $token
        $loginResult = Invoke-CodexTokenLogin -Account $selected -AccessToken $token
        if ($loginResult.ExitCode -ne 0) {
            throw "Codex login failed: $($loginResult.StdErr) $($loginResult.StdOut)"
        }

        Write-TokenMetadata -AccountName ([string]$selected.name) -ExpiresAtUtc $expiry
        $status = Invoke-CodexLoginStatus -Account $selected
        Update-AccountList
        $statusTextBox.Text = (Format-LoginStatus -Status $status) + "`r`nToken expiry: $(Get-TokenExpiryLabel -AccountName ([string]$selected.name))"
        [System.Windows.Forms.MessageBox]::Show('Token updated and login status refreshed.', 'Codex Account Switcher') | Out-Null
    } catch {
        $statusTextBox.Text = $_.Exception.Message
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Token update failed') | Out-Null
    } finally {
        Remove-Variable token -ErrorAction SilentlyContinue
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
    }
}

$browseButton.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the folder for the new PowerShell window'
    $dialog.SelectedPath = $folderTextBox.Text
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $folderTextBox.Text = $dialog.SelectedPath
    }
})

$launchButton.Add_Click({ Invoke-LaunchSelected })
$listView.Add_DoubleClick({ Invoke-LaunchSelected })
$statusButton.Add_Click({ Invoke-CheckSelectedStatus })
$tokenButton.Add_Click({ Invoke-UpdateSelectedToken })
$refreshButton.Add_Click({ Update-AccountList })
$closeButton.Add_Click({ $form.Close() })

Update-AccountList
[void]$form.ShowDialog()
