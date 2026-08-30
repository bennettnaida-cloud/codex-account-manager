using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace CodexAccountManager;

internal sealed class CodexDreamSkinApplyException : InvalidOperationException
{
    public CodexDreamSkinApplyException(
        string message,
        bool officialAppearanceRestored,
        bool officialClientRelaunched,
        Exception innerException)
        : base(message, innerException)
    {
        OfficialAppearanceRestored = officialAppearanceRestored;
        OfficialClientRelaunched = officialClientRelaunched;
    }

    public bool OfficialAppearanceRestored { get; }
    public bool OfficialClientRelaunched { get; }
}

internal sealed record MinimalQuotaTestResult(
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? TotalTokens);

public sealed partial class CodexCliService
{
    private readonly CodexAppServerClient _appServer = new();
    private readonly SharedHistoryService _threadSectionSafety = new();
    private readonly SemaphoreSlim _threadSectionMutationGate = new(1, 1);
    private static readonly Regex ApiKeyPattern = new("sk-[A-Za-z0-9_-]{8,}", RegexOptions.Compiled);
    private static readonly Regex PersonalAccessTokenPattern = new("at-[A-Za-z0-9_-]{8,}", RegexOptions.Compiled);
    private static readonly Regex NamedApiKeyPattern = new("(?i)(OPENAI_API_KEY\\s*[\"':=]+\\s*[\"']?)[^\"'\\s,}]+", RegexOptions.Compiled);
    private static readonly Regex JwtPattern = new("eyJ[A-Za-z0-9._-]{20,}", RegexOptions.Compiled);
    private static readonly Regex AnsiEscapePattern = new(
        "\\x1B\\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled);
    // Kept only for migration self-tests around releases that used device authorization.
    // The active login path below never invokes or exposes the device-code flow.
    private static readonly Regex DeviceAuthorizationUrlPattern = new(
        "https://[^\\s<>\"']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeviceAuthorizationCodePattern = new(
        "(?<![A-Z0-9])[A-Z0-9]{4}-[A-Z0-9]{4,8}(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string CodexPlusPlusLauncherFileName = "codex-plus-plus.exe";
    private const string CodexPlusPlusTaskName = "CodexAccountManagerCodexPlusPlus";
    private const string WindowsClientSwitchMutexName = "Local\\CodexAccountManager.WindowsClientSwitch";
    private const string CodexPlusPlusTaskHiddenWindowArgument = "-WindowStyle Hidden";
    private const string AuthFileName = "auth.json";
    private const string CockpitAuthFileName = ".cockpit_codex_auth.json";
    private const string ConfigFileName = "config.toml";
    private const string GlobalStateFileName = ".codex-global-state.json";
    private const string DesktopSelectionFileName = ".codex-account-manager-desktop-selection.json";
    // This sidecar identifies the account whose credentials currently own the shared
    // desktop profile.  It contains no credential material and is separate from the
    // OAuth-only selection file above so direct PAT/API projections cannot affect OAuth
    // restore semantics.
    private const string ActiveAccountStateFileName = ".codex-account-manager-active-account.json";
    private const string DesktopSelectionModeOfficialOAuth = "chatgpt-official-oauth";
    private const string DesktopSelectionModeChatGptAccessToken = "chatgpt-app-plus-personal-access-token";
    private const string DesktopSelectionModeChatGptCompatibleApi = "chatgpt-app-plus-compatible-api";
    private const string DesktopSelectionModeDirectAccessToken = "api-compatible-personal-access-token";
    private const string DesktopSelectionModeDirectCompatibleApi = "api-compatible-compatible-api";
    private const string DesktopAuthStoreDirectoryName = "account-switcher-desktop-auth";
    private const string DesktopGlobalAuthDirectoryName = "global";
    private const string DesktopAuthStoreFileName = "auth.json";
    private const string ChatGptAuthMode = "chatgpt";
    private const string ApiKeyAuthMode = "apikey";
    private const string MinimalQuotaTestModel = "gpt-5.6-luna";
    private const string CompatibleApiPreflightCacheFileName = ".codex-account-manager-api-preflight.json";
    private const string SitesPluginHeader = "[plugins.\"sites@openai-bundled\"]";
    private const int MaxPluginDefaultPromptLength = 128;
    private const uint PrintWindowClientOnly = 0x00000001;
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint InvalidGdiColor = 0xFFFFFFFF;
    private const string SharedCodexHomeOverrideVariable = "CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME";
    private const string OfficialOAuthAuthorizationHost = "auth.openai.com";
    private const string OfficialDeviceAuthorizationHost = "auth.openai.com";
    private const string DeviceAuthBrowserProfilePrefix = "codex-account-manager-device-auth-";
    private static readonly TimeSpan AccessTokenModelCacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan AccessTokenSwitchValidationCacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompatibleApiPreflightCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CompatibleApiLaunchPreflightTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CompatibleApiProxyConnectTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan MinimalQuotaTestTimeout = TimeSpan.FromSeconds(25);
    private const int MinimalQuotaTestResponseMaxBytes = 512 * 1024;
    private const int CompatibleApiModelCatalogMaxBytes = 2 * 1024 * 1024;
    // Dream Skin owns 9335-9435. Native Fast uses a disjoint range so either feature can
    // fail or recover independently without ever attaching to the other feature's browser.
    private const int OfficialNativeFastCdpPortBase = 19335;
    private const int OfficialNativeFastCdpPortCount = 32;
    private const int DreamSkinCdpPortBase = 9335;
    private const int DreamSkinCdpPortCount = 101;
    private const int OfficialNativeFastCdpMaxResponseBytes = 512 * 1024;
    private static readonly TimeSpan OfficialNativeFastRendererReadyTimeout =
        TimeSpan.FromSeconds(40);
    private static readonly TimeSpan OfficialNativeFastBridgeStopTimeout =
        TimeSpan.FromSeconds(2);
    // The primary renderer can legitimately spend more than 45 seconds mounting routes on a
    // cold profile.  Do not reload it until the renderer-to-main ready handshake has completed.
    private static readonly TimeSpan OfficialCodexPrimaryPageReadyTimeout =
        TimeSpan.FromSeconds(75);
    private static readonly TimeSpan OfficialCodexPostPatchPageReadyTimeout =
        TimeSpan.FromSeconds(45);
    private static readonly TimeSpan OfficialCodexRecoveryReleaseTimeout =
        TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OfficialAccountDisplayTimeout =
        TimeSpan.FromSeconds(6);
    private static readonly TimeSpan OfficialCodexReadyStableDuration =
        TimeSpan.FromMilliseconds(500);
    private const uint ErrorInsufficientBuffer = 122;
    private const int AddressFamilyInterNetwork = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ToolhelpSnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidNativeHandle = new(-1);
    private static readonly string[] ProxyEnvironmentVariableNames =
    [
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy"
    ];
    private static readonly string[] ProxyBypassEnvironmentVariableNames = ["NO_PROXY", "no_proxy"];
    private static readonly string[] CredentialEnvironmentVariableNames =
    [
        "OPENAI_API_KEY",
        "OPENAI_ACCESS_TOKEN",
        "OPENAI_TOKEN",
        "CODEX_ACCESS_TOKEN",
        "CODEX_API_KEY",
        "AZURE_OPENAI_API_KEY"
    ];
    private const string CodexLoopbackProxyBypass = "127.0.0.1,localhost,::1";
    private static readonly object SqliteProviderLock = new();
    private static readonly object CompatibleApiPreflightCacheLock = new();
    private static readonly Dictionary<string, CompatibleApiPreflightCacheEntry> CompatibleApiPreflightCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object AccessTokenSwitchValidationCacheLock = new();
    private static readonly Dictionary<string, AccessTokenSwitchValidationCacheEntry> AccessTokenSwitchValidationCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CodexPlusPlusLaunchDiagnosticLock = new();
    private static readonly Regex CdpIdentityPattern = new(
        "^[A-Za-z0-9._-]{1,200}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim OfficialOAuthLoginLock = new(1, 1);
    private static long _windowsClientLaunchGeneration;
    private static long _officialAccountDisplayGeneration;
    private static readonly SemaphoreSlim OfficialAccountDisplayGate = new(1, 1);
    private static readonly SemaphoreSlim CodexPlusPlusTaskOperationLock = new(1, 1);
    private static readonly TimeSpan CodexPlusPlusOpenThreadTimeout = TimeSpan.FromSeconds(12);
    // Codex++ 1.2.x can spend close to a minute discovering the packaged Codex app and
    // bringing up its CDP/helper bridge.  A 20-second foreground deadline produced a false
    // failure even though the elevated launcher had been accepted and was still progressing.
    private static readonly TimeSpan CodexPlusPlusLaunchReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CodexPlusPlusLauncherExitProbeTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan CodexPlusPlusShutdownDrainTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WindowsClientGracefulShutdownTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WindowsClientForceShutdownTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WindowsClientRuntimeStableDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CodexPlusPlusTaskResultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CodexPlusPlusTaskRequestLifetime = TimeSpan.FromSeconds(45);
    private const int CodexPlusPlusLauncherGuardPortBase = 57320;
    private static bool _sqliteProviderInitialized;
    private static readonly string LocalCodexCliRelativePath = Path.Combine(
        ".tools",
        "codex-cli",
        "node_modules",
        "@openai",
        "codex-win32-x64",
        "vendor",
        "x86_64-pc-windows-msvc",
        "bin",
        "codex.exe");

    public bool HasOfficialChatGptLogin(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.IsOfficialOAuth &&
               IsChatGptDesktopAuthJson(Path.Combine(account.CodexHome, AuthFileName));
    }

    public bool HasVerifiableChatGptFeatureLogin(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.IsOfficialOAuth &&
               TryReadChatGptAuthAccountId(
                   Path.Combine(account.CodexHome, AuthFileName),
                   out _);
    }

    public string GetDesktopAccountBindingKey(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return GetDesktopAccountKey(account);
    }

    public string? GetChatGptFeatureIdentityBindingKey(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.IsOfficialOAuth ||
            !TryReadChatGptAuthAccountId(
                Path.Combine(account.CodexHome, AuthFileName),
                out var accountId))
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)));
    }

    internal static string MinimalQuotaTestModelId => MinimalQuotaTestModel;

    public bool IsSharedCredentialAlreadySelected(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return IsProjectedDesktopCredentialSelected(
            account,
            Path.Combine(GetDefaultCodexHome(), AuthFileName));
    }

    public bool IsSharedProfileAlreadySelected(
        AccountRecord account,
        bool routeOfficialOAuthThroughGateway = false)
    {
        ArgumentNullException.ThrowIfNull(account);
        PersistSharedServiceTierToSelectedAccount();
        return CanReuseSharedProfileWithoutNetwork(
            account,
            Path.Combine(account.CodexHome, ConfigFileName),
            AccessTokenSharedProfileMode.ApiCompatible,
            chatGptFeatureAccount: null,
            routeOfficialOAuthThroughGateway);
    }

    public bool IsSharedChatGptFeatureProfileAlreadySelected(
        AccountRecord account,
        AccountRecord chatGptFeatureAccount)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(chatGptFeatureAccount);
        PersistSharedServiceTierToSelectedAccount();
        return CanReuseChatGptFeatureProfileForLaunchWithoutNetwork(
            account,
            Path.Combine(account.CodexHome, ConfigFileName),
            chatGptFeatureAccount);
    }

    public bool IsOfficialWindowsClientRunning()
    {
        var clientPath = ResolveCodexWindowsClientPath();
        var packageRoot = string.IsNullOrWhiteSpace(clientPath)
            ? null
            : Path.GetDirectoryName(clientPath);
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited &&
                        TryClassifyCodexWindowsClientProcess(process, packageRoot, out var isOfficial) &&
                        isOfficial)
                    {
                        return true;
                    }
                }
                catch
                {
                    // A vanished or inaccessible process cannot prove that the official
                    // client is running, so continue looking for an immutable live match.
                }
            }
        }
        return false;
    }

    public void CaptureActiveServiceTier()
    {
        PersistSharedServiceTierToSelectedAccount();
    }

    public bool DeleteSharedCredentialIfSelected(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var switchMutex = new Mutex(false, WindowsClientSwitchMutexName);
        var mutexAcquired = false;
        try
        {
            try
            {
                mutexAcquired = switchMutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }
            if (!mutexAcquired)
            {
                throw new TimeoutException(
                    "另一个 Codex 账号切换仍在进行；为避免删除到一半，请稍后重试。");
            }

            PersistSharedServiceTierToSelectedAccount();
            var sharedHome = GetDefaultCodexHome();
            var accountKey = GetDesktopAccountKey(account);
            var hasActiveAccountState = TryReadActiveAccountState(
                sharedHome,
                out var activeAccountKey,
                out _,
                out _);
            var sharedCredentialSelected = hasActiveAccountState
                ? activeAccountKey.Equals(accountKey, StringComparison.OrdinalIgnoreCase)
                : IsSharedCredentialAlreadySelected(account);
            var sharedChatGptAuthSelected =
                IsDesktopAuthSelectionForAccount(sharedHome, account);
            var sharedSelectionAffected =
                sharedCredentialSelected || sharedChatGptAuthSelected;
            string? deletedModelSecret = null;
            if (!account.IsOfficialOAuth)
            {
                try
                {
                    deletedModelSecret = ReadAccessTokenCredential(
                        Path.Combine(account.CodexHome, AuthFileName));
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException or FormatException)
                {
                    // Ownership sidecars still provide the authoritative selected-account
                    // cleanup path when the source credential is already corrupt.
                }
            }

            DeleteMatchingGlobalDesktopAuth(sharedHome, account, sharedChatGptAuthSelected);
            ScrubAccountSwitcherBackups(sharedHome, account);

            if (sharedSelectionAffected)
            {
                foreach (var fileName in new[]
                         {
                             AuthFileName,
                             CockpitAuthFileName,
                             DesktopSelectionFileName,
                             ActiveAccountStateFileName
                         })
                {
                    var path = Path.Combine(sharedHome, fileName);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    ClearReadOnlyAttribute(path);
                    File.Delete(path);
                }

            }

            if (sharedSelectionAffected)
            {
                RemoveExperimentalBearerTokenLines(
                    Path.Combine(sharedHome, ConfigFileName),
                    matchingSecret: null);
            }
            else if (!string.IsNullOrWhiteSpace(deletedModelSecret))
            {
                RemoveExperimentalBearerTokenLines(
                    Path.Combine(sharedHome, ConfigFileName),
                    deletedModelSecret);
            }

            DeleteStoredDesktopAuth(sharedHome, account);
            return sharedSelectionAffected;
        }
        finally
        {
            if (mutexAcquired)
            {
                switchMutex.ReleaseMutex();
            }
        }
    }

    public async Task<LoginStatus> GetLoginStatusAsync(AccountRecord account)
    {
        if (account.IsCompatibleApi)
        {
            var configPath = Path.Combine(account.CodexHome, ConfigFileName);
            var authPath = Path.Combine(account.CodexHome, AuthFileName);
            var missing = new List<string>();
            if (!File.Exists(configPath))
            {
                missing.Add(ConfigFileName);
            }
            if (!File.Exists(authPath))
            {
                missing.Add(AuthFileName);
            }

            return new LoginStatus
            {
                ExitCode = missing.Count == 0 ? 0 : 1,
                Text = missing.Count == 0
                    ? $"兼容 API 已配置：{account.ApiProviderName} / {account.ApiModel}"
                    : $"兼容 API 配置不完整：缺少 {string.Join(", ", missing)}"
            };
        }

        if (account.IsOfficialOAuth)
        {
            EnsureOfficialOAuthAccountConfig(account);
            SyncStoredOfficialOAuthAuthToAccount(account);
        }
        else
        {
            EnsureLocalPatAccountConfig(account);
        }
        var result = await RunCodexAsync("login status", account.CodexHome, null);
        var text = string.Join(Environment.NewLine, new[] { result.StdOut, result.StdErr }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (account.IsOfficialOAuth &&
            result.ExitCode == 0 &&
            !IsChatGptDesktopAuthJson(Path.Combine(account.CodexHome, AuthFileName)))
        {
            return new LoginStatus
            {
                ExitCode = 1,
                Text = "当前凭据不是通过 ChatGPT 登录生成的官方 OAuth 会话。请重新点击“通过 ChatGPT 登录”。"
            };
        }
        return new LoginStatus
        {
            ExitCode = result.ExitCode,
            Text = string.IsNullOrWhiteSpace(text) ? "无状态输出。" : text.Trim()
        };
    }

    public async Task<LoginStatus> LoginWithAccessTokenAsync(AccountRecord account, string accessToken)
    {
        if (!account.IsAccessToken)
        {
            throw new InvalidOperationException(
                "只有 Access Token 账号使用 codex login --with-access-token；通过 ChatGPT 登录的账号请使用官方浏览器登录。"
            );
        }

        accessToken = NormalizeAccessTokenInput(accessToken);
        var validationError = GetAccessTokenInputError(accessToken);
        if (validationError != null)
        {
            throw new InvalidOperationException(validationError);
        }

        PersistSharedServiceTierToSelectedAccount();
        EnsureLocalPatAccountConfig(account);
        await LocalPatGateway.EnsureRunningAsync();
        var result = await RunCodexAsync("login --with-access-token", account.CodexHome, accessToken);
        if (result.ExitCode != 0)
        {
            var failure = $"{result.StdErr} {result.StdOut}".Trim();
            if (failure.Contains("invalid agent identity JWT format", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Codex 拒绝了这个 Access Token：CLI 认为它不是可用于 Codex Local 的 agent identity token。\n\n" +
                    "Business/Enterprise 的 Codex access token 可以不是三段 JWT，也可能是 at 开头；但它必须来自 ChatGPT 工作区的 Codex Access tokens 页面，并且工作区要允许 Codex Local。\n" +
                    "请确认它不是 API Key、Workspace Agent access token、refresh_token、id_token、session_token 或整段 JSON。");
            }
            if (IsPersonalAccessTokenMetadataRequestFailure(failure))
            {
                throw new InvalidOperationException(BuildPersonalAccessTokenNetworkMessage(failure));
            }

            throw new InvalidOperationException($"Codex 登录失败：{failure}");
        }

        ProjectAccessTokenSourceConfig(Path.Combine(account.CodexHome, ConfigFileName));
        var status = await GetLoginStatusAsync(account);
        if (status.ExitCode == 0)
        {
            CacheAccessTokenSwitchValidation(account, status);
        }
        return status;
    }

    public async Task<LoginStatus> LoginWithChatGptAsync(
        AccountRecord account,
        IProgress<ChatGptOAuthAuthorization>? authorizationProgress = null,
        CancellationToken cancellationToken = default)
    {
        PersistSharedServiceTierToSelectedAccount();
        if (!await OfficialOAuthLoginLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException(
                "已有一个 ChatGPT 官方网页登录正在进行。请先完成或取消它，再为其他账号登录。"
            );
        }

        try
        {
            return await LoginWithChatGptCoreAsync(
                account,
                authorizationProgress,
                persistSuccessfulLogin: true,
                cancellationToken);
        }
        finally
        {
            OfficialOAuthLoginLock.Release();
        }
    }

    public async Task<LoginStatus> LoginWithChatGptDraftAsync(
        string draftCodexHome,
        IProgress<ChatGptOAuthAuthorization>? authorizationProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draftCodexHome))
        {
            throw new ArgumentException("临时 OAuth 账号目录不能为空。", nameof(draftCodexHome));
        }
        if (!await OfficialOAuthLoginLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException(
                "已有一个 ChatGPT 官方网页登录正在进行。请先完成或取消它，再为其他账号登录。"
            );
        }

        try
        {
            var draftAccount = new AccountRecord
            {
                Name = "pending-chatgpt-login",
                CodexHome = Path.GetFullPath(draftCodexHome),
                AuthKind = AccountAuthKind.OfficialOAuth
            };
            return await LoginWithChatGptCoreAsync(
                draftAccount,
                authorizationProgress,
                persistSuccessfulLogin: false,
                cancellationToken);
        }
        finally
        {
            OfficialOAuthLoginLock.Release();
        }
    }

    private async Task<LoginStatus> LoginWithChatGptCoreAsync(
        AccountRecord account,
        IProgress<ChatGptOAuthAuthorization>? authorizationProgress,
        bool persistSuccessfulLogin,
        CancellationToken cancellationToken)
    {
        if (!account.IsOfficialOAuth)
        {
            throw new InvalidOperationException("只有“通过 ChatGPT 登录（官方）”账号可以启动浏览器登录。");
        }

        EnsureOfficialOAuthAccountConfig(account);
        SyncStoredOfficialOAuthAuthToAccount(account);
        var authPath = Path.Combine(account.CodexHome, AuthFileName);
        var backupPath = File.Exists(authPath)
            ? authPath + ".chatgpt-login-backup-" + Guid.NewGuid().ToString("N")
            : null;
        var preserveBackup = false;
        if (backupPath != null)
        {
            File.Copy(authPath, backupPath, overwrite: false);
        }

        try
        {
            if (File.Exists(authPath))
            {
                ClearReadOnlyAttribute(authPath);
                File.Delete(authPath);
            }

            var result = await RunOfficialBrowserAuthorizationAsync(
                account.CodexHome,
                authorizationProgress,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                var failure = string.Join(Environment.NewLine, new[] { result.StdErr, result.StdOut }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                throw new InvalidOperationException(
                    "通过 ChatGPT 官方网页登录未完成。旧登录凭据将自动恢复。\n\n" +
                    (string.IsNullOrWhiteSpace(failure) ? "官方登录服务没有返回详细信息。" : failure.Trim()));
            }

            if (!IsChatGptDesktopAuthJson(authPath))
            {
                throw new InvalidOperationException(
                    "官方登录流程已经结束，但没有在该账号的独立目录生成 ChatGPT 登录凭据。请重新登录。"
                );
            }
        }
        catch (Exception loginError)
        {
            try
            {
                RestoreOfficialOAuthLoginCredential(authPath, backupPath);
            }
            catch (Exception restoreError)
            {
                preserveBackup = backupPath != null && File.Exists(backupPath);
                var backupHint = preserveBackup
                    ? $" 原凭据备份仍保留在：{backupPath}"
                    : string.Empty;
                throw new InvalidOperationException(
                    "ChatGPT 登录失败，并且无法自动恢复原登录凭据。" + backupHint,
                    new AggregateException(loginError, restoreError));
            }
            throw;
        }
        finally
        {
            if (!preserveBackup && backupPath != null && File.Exists(backupPath))
            {
                ClearReadOnlyAttribute(backupPath);
                File.Delete(backupPath);
            }
        }

        if (!persistSuccessfulLogin)
        {
            return new LoginStatus
            {
                ExitCode = 0,
                Text = "ChatGPT 官方网页登录已完成"
            };
        }

        // Commit the new OAuth file to the exact account snapshot before asking the app server
        // for status. This must not hot-swap the running shared profile: a different account_id
        // is applied only by the normal mutex + clean-shutdown projection path.
        PersistSuccessfulOfficialOAuthLogin(account);
        return await GetLoginStatusAsync(account);
    }

    public async Task SetThreadArchivedAsync(string threadId, bool archived, string codexHome)
    {
        ValidateThreadId(threadId);
        try
        {
            await _appServer.SetThreadArchivedAsync(threadId, archived, codexHome);
            return;
        }
        catch (Exception appServerError) when (appServerError is not OperationCanceledException)
        {
            var command = archived ? "archive" : "unarchive";
            var result = await RunCodexAsync($"{command} {threadId}", codexHome, null);
            if (result.ExitCode == 0)
            {
                return;
            }

            var detail = string.Join(Environment.NewLine, new[] { result.StdErr, result.StdOut }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            throw new InvalidOperationException(
                $"无法{(archived ? "归档" : "取消归档")}任务：{detail.Trim()}",
                appServerError);
        }
    }

    public async Task DeleteThreadAsync(string threadId, string codexHome)
    {
        ValidateThreadId(threadId);
        Exception? appServerError = null;
        try
        {
            await _appServer.DeleteThreadAsync(threadId, codexHome);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            appServerError = ex;
        }

        // Deletion is idempotent. A record already absent from Codex should stay hidden instead
        // of being resurrected by the manager's SQLite compatibility reader.
        var exists = await TryThreadExistsInCodexAsync(threadId, codexHome);
        if (exists == false)
        {
            return;
        }

        var result = await RunCodexAsync($"delete --force {threadId}", codexHome, null);
        if (result.ExitCode == 0)
        {
            return;
        }

        exists = await TryThreadExistsInCodexAsync(threadId, codexHome);
        if (exists == false)
        {
            return;
        }

        var detail = string.Join(Environment.NewLine, new[] { result.StdErr, result.StdOut }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        throw new InvalidOperationException(
            $"无法删除任务：{detail.Trim()}",
            appServerError);
    }

    internal Task<IReadOnlyList<CodexThreadSummary>> ListThreadsFromCodexAsync(
        string codexHome,
        CancellationToken cancellationToken = default) =>
        _appServer.ListThreadsAsync(codexHome, cancellationToken);

    internal async Task<IReadOnlyList<CodexThreadSection>> ListThreadSectionsFromCodexAsync(
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _appServer.ListThreadSectionsAsync(codexHome, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "当前 Codex 运行时不支持读取聊天目录；请先更新 Codex 后再使用手动分类。",
                ex);
        }
    }

    internal Task<CodexThreadSection> CreateThreadSectionAsync(
        string name,
        string codexHome,
        CancellationToken cancellationToken = default) =>
        ExecuteThreadSectionMutationAsync(
            codexHome,
            () => _appServer.CreateThreadSectionAsync(name, codexHome, cancellationToken),
            cancellationToken);

    internal Task<CodexThreadSection> RenameThreadSectionAsync(
        string sectionId,
        string name,
        string codexHome,
        CancellationToken cancellationToken = default) =>
        ExecuteThreadSectionMutationAsync(
            codexHome,
            () => _appServer.RenameThreadSectionAsync(
                sectionId,
                name,
                codexHome,
                cancellationToken),
            cancellationToken);

    internal async Task DeleteThreadSectionAsync(
        string sectionId,
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        await ExecuteThreadSectionMutationAsync(
            codexHome,
            async () =>
            {
                // Re-read the authoritative state database while the mutation gate is held.
                // The UI cache may be stale and Codex would otherwise unclassify every active
                // and archived thread in a non-empty section when the section is deleted.
                _threadSectionSafety.EnsureThreadSectionEmpty(codexHome, sectionId);
                await _appServer.DeleteThreadSectionAsync(
                    sectionId,
                    codexHome,
                    cancellationToken);
                return true;
            },
            cancellationToken);
    }

    internal async Task MoveThreadToSectionAsync(
        string threadId,
        string? sectionId,
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        ValidateThreadId(threadId);
        await ExecuteThreadSectionMutationAsync(
            codexHome,
            async () =>
            {
                await _appServer.MoveThreadToSectionAsync(
                    threadId,
                    sectionId,
                    codexHome,
                    cancellationToken);
                return true;
            },
            cancellationToken);
    }

    private async Task<T> ExecuteThreadSectionMutationAsync<T>(
        string codexHome,
        Func<Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        return await ExecuteThreadSectionMutationCoreAsync(
            () => _ = _threadSectionSafety.CreateThreadSectionSafetyBackup(codexHome),
            () => _threadSectionSafety.VerifyThreadSectionDatabaseIntegrity(codexHome),
            mutation,
            cancellationToken);
    }

    private async Task<T> ExecuteThreadSectionMutationCoreAsync<T>(
        Action createSafetyBackup,
        Action verifyIntegrity,
        Func<Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createSafetyBackup);
        ArgumentNullException.ThrowIfNull(verifyIntegrity);
        ArgumentNullException.ThrowIfNull(mutation);
        await _threadSectionMutationGate.WaitAsync(cancellationToken);
        try
        {
            createSafetyBackup();
            T result = default!;
            Exception? mutationError = null;
            try
            {
                result = await mutation();
            }
            catch (Exception ex)
            {
                mutationError = ex;
            }

            Exception? integrityError = null;
            try
            {
                // This must run after success, ordinary failure, cancellation, and timeout.
                verifyIntegrity();
            }
            catch (Exception ex)
            {
                integrityError = ex;
            }

            if (mutationError is OperationCanceledException cancellationError)
            {
                if (integrityError != null)
                {
                    throw new OperationCanceledException(
                        "聊天目录操作已取消或超时，并且操作后的数据库完整性检查失败：" +
                        integrityError.Message,
                        new AggregateException(cancellationError, integrityError),
                        cancellationError.CancellationToken);
                }

                // Preserve the exact cancellation instance, token, and original stack.
                ExceptionDispatchInfo.Capture(cancellationError).Throw();
            }

            if (mutationError != null)
            {
                if (integrityError != null)
                {
                    throw new InvalidOperationException(
                        $"聊天目录修改失败，并且操作后的数据库完整性检查也失败：{integrityError.Message}\n\n" +
                        "操作前备份已保留，请从备份恢复数据库后再继续操作。",
                        new AggregateException(mutationError, integrityError));
                }

                throw new InvalidOperationException(
                    $"聊天目录修改失败：{mutationError.Message}\n\n" +
                    "操作前备份已保留，原聊天记录没有被删除。" +
                    "如果提示目录方法不存在，请先更新 Codex 后再试。",
                    mutationError);
            }

            if (integrityError != null)
            {
                throw new InvalidOperationException(
                    "聊天目录修改完成，但操作后的数据库完整性检查失败。" +
                    "操作前备份已保留，请从备份恢复数据库后再继续操作。",
                    integrityError);
            }

            return result;
        }
        finally
        {
            _threadSectionMutationGate.Release();
        }
    }

    internal static void ValidateThreadSectionMutationSafety()
    {
        var service = new CodexCliService();
        var backupCount = 0;
        var integrityCount = 0;
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var expectedCancellation = new OperationCanceledException(
            "thread-section cancellation self-test",
            cancellationSource.Token);

        OperationCanceledException? observedCancellation = null;
        try
        {
            _ = service.ExecuteThreadSectionMutationCoreAsync(
                    () => backupCount++,
                    () => integrityCount++,
                    () => Task.FromException<bool>(expectedCancellation),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException ex)
        {
            observedCancellation = ex;
        }

        if (!ReferenceEquals(observedCancellation, expectedCancellation) ||
            backupCount != 1 ||
            integrityCount != 1)
        {
            throw new InvalidOperationException(
                "聊天目录取消路径没有原样重抛取消异常并执行操作后完整性检查。");
        }

        var integrityFailure = new InvalidDataException("expected integrity failure");
        OperationCanceledException? combinedCancellation = null;
        try
        {
            _ = service.ExecuteThreadSectionMutationCoreAsync(
                    () => backupCount++,
                    () => throw integrityFailure,
                    () => Task.FromException<bool>(expectedCancellation),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException ex)
        {
            combinedCancellation = ex;
        }

        if (combinedCancellation?.InnerException is not AggregateException aggregate ||
            !aggregate.InnerExceptions.Contains(expectedCancellation) ||
            !aggregate.InnerExceptions.Contains(integrityFailure))
        {
            throw new InvalidOperationException(
                "聊天目录取消后的完整性故障没有保留取消语义和两项原始异常。");
        }
    }

    private async Task<bool?> TryThreadExistsInCodexAsync(string threadId, string codexHome)
    {
        try
        {
            var threads = await _appServer.ListThreadsAsync(codexHome);
            return threads.Any(thread =>
                thread.Id.Equals(threadId, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateThreadId(string threadId)
    {
        if (!Guid.TryParse(threadId, out _))
        {
            throw new ArgumentException("Codex 任务 ID 无效。", nameof(threadId));
        }
    }

    public async Task<UsageLimitResetSession> OpenUsageLimitResetSessionAsync(
        AccountRecord account,
        bool fastFail = false,
        bool preserveRunningGateway = true,
        CancellationToken cancellationToken = default)
    {
        if (account.IsCompatibleApi)
        {
            throw new InvalidOperationException("兼容 API/API Key 账号不支持 Codex 套餐用量重置次数。");
        }

        if (account.IsOfficialOAuth)
        {
            EnsureOfficialOAuthAccountConfig(account);
            SyncStoredOfficialOAuthAuthToAccount(account);
        }
        else
        {
            EnsureLocalPatAccountConfig(account);
            await EnsureUsageLimitResetGatewayAsync(
                preserveRunningGateway,
                cancellationToken);
        }

        var authPath = Path.Combine(account.CodexHome, AuthFileName);
        if (!File.Exists(authPath))
        {
            throw new FileNotFoundException(
                $"账号 {account.Name} 没有可供官方用量接口使用的登录凭据。",
                authPath);
        }

        var command = ResolveCodexCliCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException(
                "找不到可用的 Codex CLI，无法查询官方用量重置次数。");
        }

        var attemptCount = fastFail ? 1 : 2;
        for (var attempt = 0; attempt < attemptCount; attempt++)
        {
            var initializeTimeout = fastFail
                ? TimeSpan.FromSeconds(8)
                : attempt == 0
                    ? TimeSpan.FromSeconds(20)
                    : TimeSpan.FromSeconds(60);
            try
            {
                return await StartUsageLimitResetSessionAsync(
                    command,
                    account.CodexHome,
                    initializeTimeout,
                    cancellationToken);
            }
            catch (TimeoutException) when (!fastFail && attempt == 0)
            {
                // Codex CLI 0.144.1 can occasionally receive initialize and then stall
                // during cold startup. The failed helper has already been terminated;
                // a clean retry is safe because no reset-credit request was sent.
                await Task.Delay(400, cancellationToken);
            }
        }

        throw new TimeoutException("Codex 官方用量接口初始化重试后仍然超时。");
    }

    private static Task EnsureUsageLimitResetGatewayAsync(
        bool preserveRunningGateway,
        CancellationToken cancellationToken) =>
        EnsureUsageLimitResetGatewayAsync(
            preserveRunningGateway,
            LocalPatGateway.EnsureRunningForLightweightTestAsync,
            token => LocalPatGateway.EnsureRunningAsync(token),
            cancellationToken);

    private static Task EnsureUsageLimitResetGatewayAsync(
        bool preserveRunningGateway,
        Func<CancellationToken, Task> preserveGateway,
        Func<CancellationToken, Task> restartCapableGateway,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preserveGateway);
        ArgumentNullException.ThrowIfNull(restartCapableGateway);
        return preserveRunningGateway
            ? preserveGateway(cancellationToken)
            : restartCapableGateway(cancellationToken);
    }

    internal static void ValidateUsageLimitResetGatewaySafety()
    {
        var preserveCalls = 0;
        var restartCapableCalls = 0;
        EnsureUsageLimitResetGatewayAsync(
                preserveRunningGateway: true,
                _ =>
                {
                    preserveCalls++;
                    return Task.CompletedTask;
                },
                _ =>
                {
                    restartCapableCalls++;
                    return Task.CompletedTask;
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var preserveParameter = typeof(CodexCliService)
            .GetMethod(nameof(OpenUsageLimitResetSessionAsync))?
            .GetParameters()
            .SingleOrDefault(parameter =>
                parameter.Name == "preserveRunningGateway");
        if (preserveCalls != 1 ||
            restartCapableCalls != 0 ||
            preserveParameter is not { HasDefaultValue: true, DefaultValue: true })
        {
            throw new InvalidOperationException(
                "Usage-limit reset sessions must preserve an already-running PAT gateway by default.");
        }

        var accountHome = Path.Combine(Path.GetTempPath(), "codex-reset-auth-fixture");
        var startInfo = BuildUsageLimitResetProcessStartInfo("codex-fixture.exe", accountHome);
        if (!string.Equals(startInfo.FileName, "codex-fixture.exe", StringComparison.Ordinal) ||
            !startInfo.Arguments.Equals("app-server --stdio --disable plugins", StringComparison.Ordinal) ||
            startInfo.UseShellExecute ||
            !string.Equals(startInfo.Environment["CODEX_HOME"], accountHome, StringComparison.Ordinal) ||
            !string.Equals(startInfo.Environment["CODEX_SQLITE_HOME"], accountHome, StringComparison.Ordinal) ||
            CredentialEnvironmentVariableNames.Any(startInfo.Environment.ContainsKey))
        {
            throw new InvalidOperationException(
                "Usage-limit reset sessions must authenticate only from the selected account CODEX_HOME.");
        }
    }

    internal bool HasStoredQuotaTestCredential(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.IsCompatibleApi)
        {
            return false;
        }

        var authPath = Path.Combine(account.CodexHome, AuthFileName);
        if (account.IsOfficialOAuth)
        {
            return IsChatGptDesktopAuthJson(authPath);
        }

        try
        {
            return !string.IsNullOrWhiteSpace(ReadQuotaTestCredential(account, authPath).Token);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            return false;
        }
    }

    internal async Task<MinimalQuotaTestResult> SendMinimalQuotaTestAsync(
        AccountRecord account,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.IsCompatibleApi)
        {
            throw new InvalidOperationException(
                "兼容 API/API Key 账号不使用 ChatGPT/Codex 套餐额度测试。");
        }

        if (account.IsOfficialOAuth)
        {
            EnsureOfficialOAuthAccountConfig(account);
            SyncStoredOfficialOAuthAuthToAccount(account);
        }
        else
        {
            EnsureLocalPatAccountConfig(account);
        }

        var authPath = Path.Combine(account.CodexHome, AuthFileName);
        if (!File.Exists(authPath))
        {
            throw new FileNotFoundException(
                $"账号 {account.Name} 没有可用于额度测试的登录凭据。",
                authPath);
        }

        progress?.Report("正在检查本地网关…");
        await LocalPatGateway.EnsureRunningForLightweightTestAsync(cancellationToken);
        var credential = ReadQuotaTestCredential(account, authPath);
        progress?.Report("正在发送轻量测试请求…");
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return await SendMinimalQuotaTestRequestAsync(
                client,
                new Uri(LocalPatGateway.ProviderBaseUrl, UriKind.Absolute),
                account,
                credential.Token,
                credential.AccountId,
                cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"账号 {account.Name} 的轻量测试在 {MinimalQuotaTestTimeout.TotalSeconds:0} 秒内没有收到模型响应。" +
                "本地网关仍保持运行；请切换更稳定的代理节点后重试。",
                ex);
        }
    }

    private static string BuildMinimalQuotaTestPayload() =>
        JsonSerializer.Serialize(new
        {
            model = MinimalQuotaTestModel,
            instructions = "请使用简体中文自然回复用户的问候，并用一句话说明你可以提供什么帮助。不要调用工具，总共不超过三句话。",
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new[] { new { type = "input_text", text = "你好" } }
                }
            },
            reasoning = new { effort = "low" },
            store = false,
            stream = true
        });

    private static async Task<MinimalQuotaTestResult> SendMinimalQuotaTestRequestAsync(
        HttpClient client,
        Uri endpoint,
        AccountRecord account,
        string credential,
        string? accountId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MinimalQuotaTestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                BuildMinimalQuotaTestPayload(),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential);
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        request.Headers.TryAddWithoutValidation(
            LocalPatGateway.RequestTimeoutHeader,
            ((int)MinimalQuotaTestTimeout.TotalMilliseconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            LocalPatGateway.RequestPurposeHeader,
            LocalPatGateway.QuotaTestRequestPurpose);
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        }

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var bytes = await ReadResponseBodyUpToLimitAsync(
                response.Content,
                MinimalQuotaTestResponseMaxBytes,
                timeout.Token);
            var detail = bytes == null
                ? $"HTTP {(int)response.StatusCode}（错误内容过长）"
                : Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(BuildMinimalQuotaTestFailureMessage(
                account,
                detail));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var protocolOutput = new StringBuilder();
        var observedBytes = 0;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            observedBytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (observedBytes > MinimalQuotaTestResponseMaxBytes)
            {
                throw new InvalidDataException("轻量测试响应过长，已停止读取。");
            }

            var json = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? line["data:".Length..].Trim()
                : line.Trim();
            if (json.Length == 0 ||
                json.Equals("[DONE]", StringComparison.OrdinalIgnoreCase) ||
                !json.StartsWith('{'))
            {
                continue;
            }

            protocolOutput.AppendLine(json);
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            var type = message?["type"]?.GetValue<string>() ?? string.Empty;
            var status = message?["response"]?["status"]?.GetValue<string>() ??
                         message?["status"]?.GetValue<string>() ?? string.Empty;
            if (IsMinimalQuotaTestFailureEvent(type, status))
            {
                throw new InvalidOperationException(BuildMinimalQuotaTestFailureMessage(
                    account,
                    json));
            }
            if (IsMinimalQuotaTestCompletionEvent(type, status))
            {
                return ParseMinimalQuotaTestResult(protocolOutput.ToString());
            }
        }

        throw new InvalidDataException(
            protocolOutput.Length == 0
                ? "轻量测试没有返回可识别的响应。"
                : "轻量测试连接在模型明确完成响应前结束，请检查代理节点后重试。");
    }

    private static bool IsMinimalQuotaTestCompletionEvent(string type, string status) =>
        type.Equals("response.completed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsMinimalQuotaTestFailureEvent(string type, string status) =>
        type.Equals("error", StringComparison.OrdinalIgnoreCase) ||
        type.EndsWith(".failed", StringComparison.OrdinalIgnoreCase) ||
        type.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase) ||
        type.EndsWith(".cancelled", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("incomplete", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("cancelled", StringComparison.OrdinalIgnoreCase);

    private static string BuildMinimalQuotaTestFailureMessage(AccountRecord account, string detail)
    {
        var diagnostic = ExtractMinimalQuotaTestJsonError(detail) ?? detail.Trim();
        if (diagnostic.Contains(
                "model is not supported when using Codex with a ChatGPT account",
                StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains(
                "not supported when using Codex with a ChatGPT account",
                StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("model_not_supported", StringComparison.OrdinalIgnoreCase))
        {
            return $"账号 {account.Name} 当前没有测试模型 {MinimalQuotaTestModel} 的使用权限。" +
                   "这是该账号或工作区的模型权限结果；未发送第二次测试请求。";
        }

        if ((diagnostic.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) &&
             diagnostic.Contains("local_pat_gateway_error", StringComparison.OrdinalIgnoreCase)) ||
            diagnostic.Contains("没有携带可用的 Codex PAT", StringComparison.OrdinalIgnoreCase))
        {
            return $"账号 {account.Name} 的测试进程没有加载到可识别的 PAT/OAuth 凭据。" +
                   "请在“状态与凭据”中重新登录或更新 Token 后重试。";
        }

        if (IsPersonalAccessTokenMetadataRequestFailure(detail))
        {
            return BuildPersonalAccessTokenNetworkMessage(diagnostic);
        }

        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            diagnostic = "Codex CLI 未返回失败原因。";
        }
        if (diagnostic.Length > 900)
        {
            diagnostic = diagnostic[..900] + "…";
        }
        return $"账号 {account.Name} 的小额测试请求失败：{diagnostic}";
    }

    private static string? ExtractMinimalQuotaTestJsonError(string output)
    {
        var trimmedOutput = (output ?? string.Empty).Trim();
        if (trimmedOutput.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(trimmedOutput) is JsonObject wholeMessage)
                {
                    var wholeExtracted = ReadNestedErrorText(wholeMessage["error"]) ??
                                         ReadNestedErrorText(wholeMessage["response"]?["error"]) ??
                                         ReadNestedErrorText(wholeMessage["message"]);
                    if (!string.IsNullOrWhiteSpace(wholeExtracted))
                    {
                        return wholeExtracted.Trim();
                    }
                }
            }
            catch (JsonException)
            {
                // Streaming responses are handled one JSON event per line below.
            }
        }

        foreach (var line in (output ?? string.Empty)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Reverse())
        {
            try
            {
                if (JsonNode.Parse(line.Trim()) is not JsonObject message)
                {
                    continue;
                }

                var type = message["type"]?.GetValue<string>() ?? string.Empty;
                if (!type.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                    !type.EndsWith(".failed", StringComparison.OrdinalIgnoreCase) &&
                    message["error"] == null &&
                    message["response"]?["error"] == null)
                {
                    continue;
                }

                var extracted = ReadNestedErrorText(message["error"]) ??
                                ReadNestedErrorText(message["response"]?["error"]) ??
                                ReadNestedErrorText(message["message"]) ??
                                ReadNestedErrorText(message["item"]);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    return extracted.Trim();
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Non-protocol diagnostic output is handled by the compact fallback above.
            }
        }
        return null;
    }

    private static string? ReadNestedErrorText(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                return ReadNestedErrorText(JsonNode.Parse(text)) ?? text;
            }
            catch (JsonException)
            {
                return text;
            }
        }

        if (node is not JsonObject error)
        {
            return null;
        }
        return ReadNestedErrorText(error["detail"]) ??
               ReadNestedErrorText(error["message"]) ??
               ReadNestedErrorText(error["error"]);
    }

    internal static MinimalQuotaTestResult ParseMinimalQuotaTestResult(string output)
    {
        long? inputTokens = null;
        long? cachedInputTokens = null;
        long? outputTokens = null;
        long? totalTokens = null;
        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonObject? root;
            try
            {
                root = JsonNode.Parse(line.Trim()) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            var usage = root?["usage"] as JsonObject ??
                        root?["response"]?["usage"] as JsonObject ??
                        root?["item"]?["usage"] as JsonObject ??
                        root?["event"]?["usage"] as JsonObject;
            if (usage == null)
            {
                continue;
            }

            inputTokens = ReadQuotaTestLong(usage["input_tokens"]) ??
                          ReadQuotaTestLong(usage["inputTokens"]) ??
                          inputTokens;
            cachedInputTokens = ReadQuotaTestLong(usage["cached_input_tokens"]) ??
                                ReadQuotaTestLong(usage["input_tokens_details"]?["cached_tokens"]) ??
                                ReadQuotaTestLong(usage["cachedInputTokens"]) ??
                                cachedInputTokens;
            outputTokens = ReadQuotaTestLong(usage["output_tokens"]) ??
                           ReadQuotaTestLong(usage["outputTokens"]) ??
                           outputTokens;
            totalTokens = ReadQuotaTestLong(usage["total_tokens"]) ??
                          ReadQuotaTestLong(usage["totalTokens"]) ??
                          totalTokens;
        }

        totalTokens ??= inputTokens.HasValue || outputTokens.HasValue
            ? Math.Max(0L, inputTokens ?? 0L) + Math.Max(0L, outputTokens ?? 0L)
            : null;
        return new MinimalQuotaTestResult(
            inputTokens,
            cachedInputTokens,
            outputTokens,
            totalTokens);
    }

    private static long? ReadQuotaTestLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }
        return value.TryGetValue<int>(out var intValue) ? intValue : null;
    }

    internal static void ValidateMinimalQuotaTestParsing()
    {
        var parsed = ParseMinimalQuotaTestResult(
            "{\"type\":\"thread.started\",\"thread_id\":\"fixture\"}\n" +
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":120,\"cached_input_tokens\":80,\"output_tokens\":2}}\n");
        if (parsed.InputTokens != 120 ||
            parsed.CachedInputTokens != 80 ||
            parsed.OutputTokens != 2 ||
            parsed.TotalTokens != 122)
        {
            throw new InvalidOperationException("Minimal quota-test usage parsing self-test failed.");
        }

        var payload = JsonNode.Parse(BuildMinimalQuotaTestPayload()) as JsonObject;
        if (payload?["model"]?.GetValue<string>() != "gpt-5.6-luna" ||
            payload["stream"]?.GetValue<bool>() != true ||
            payload["store"]?.GetValue<bool>() != false ||
            payload["max_output_tokens"] != null ||
            payload["instructions"]?.GetValue<string>() !=
                "请使用简体中文自然回复用户的问候，并用一句话说明你可以提供什么帮助。不要调用工具，总共不超过三句话。" ||
            payload["input"]?[0]?["content"]?[0]?["text"]?.GetValue<string>() != "你好" ||
            payload["reasoning"]?["effort"]?.GetValue<string>() != "low" ||
            payload["reasoning"]?["summary"] != null)
        {
            throw new InvalidOperationException("Minimal quota-test payload self-test failed.");
        }

        var directParsed = ParseMinimalQuotaTestResult(
            "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":12,\"input_tokens_details\":{\"cached_tokens\":3},\"output_tokens\":2,\"total_tokens\":14}}}\n");
        if (directParsed.InputTokens != 12 ||
            directParsed.CachedInputTokens != 3 ||
            directParsed.OutputTokens != 2 ||
            directParsed.TotalTokens != 14)
        {
            throw new InvalidOperationException("Minimal quota-test direct response parsing self-test failed.");
        }
        if (!IsMinimalQuotaTestCompletionEvent("response.completed", "") ||
            IsMinimalQuotaTestCompletionEvent("response.created", "in_progress") ||
            !IsMinimalQuotaTestFailureEvent("error", "") ||
            !IsMinimalQuotaTestFailureEvent("response.incomplete", "") ||
            !IsMinimalQuotaTestFailureEvent("", "cancelled"))
        {
            throw new InvalidOperationException("Minimal quota-test terminal event self-test failed.");
        }

        var unsupportedModel = BuildMinimalQuotaTestFailureMessage(
            new AccountRecord { Name = "fixture" },
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"{\\\"detail\\\":\\\"The model is not supported when using Codex with a ChatGPT account.\\\"}\"}}");
        if (!unsupportedModel.Contains("gpt-5.6-luna", StringComparison.Ordinal) ||
            unsupportedModel.Contains("turn.failed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Minimal quota-test error diagnostics self-test failed.");
        }
        var prettyJsonError = BuildMinimalQuotaTestFailureMessage(
            new AccountRecord { Name = "fixture" },
            "{\n  \"error\": {\n    \"message\": \"Invalid value: none\",\n    \"type\": \"invalid_request_error\"\n  }\n}");
        if (!prettyJsonError.Contains("Invalid value: none", StringComparison.Ordinal) ||
            prettyJsonError.Contains("\"error\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Minimal quota-test pretty JSON error self-test failed.");
        }
    }

    internal void EnsureLocalPatAccountConfig(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.IsAccessToken)
        {
            return;
        }

        ProjectAccessTokenSourceConfig(Path.Combine(account.CodexHome, ConfigFileName));
    }

    internal void EnsureOfficialOAuthAccountConfig(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.IsOfficialOAuth)
        {
            return;
        }

        Directory.CreateDirectory(account.CodexHome);
        var configPath = Path.Combine(account.CodexHome, ConfigFileName);
        var current = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        var projected = ProjectOfficialOAuthConfigText(current);
        if (!string.Equals(current, projected, StringComparison.Ordinal))
        {
            WriteTextAtomically(configPath, projected);
        }
    }

    private static async Task<UsageLimitResetSession> StartUsageLimitResetSessionAsync(
        string command,
        string codexHome,
        TimeSpan initializeTimeout,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildUsageLimitResetProcessStartInfo(command, codexHome);

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        var session = new UsageLimitResetSession(process, MaskSensitive);
        try
        {
            await session.InitializeAsync(initializeTimeout, cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static ProcessStartInfo BuildUsageLimitResetProcessStartInfo(
        string command,
        string codexHome)
    {
        var startInfo = new ProcessStartInfo(command, "app-server --stdio --disable plugins")
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // JSONL over stdio is explicitly BOM-free for compatibility with the Rust reader.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["CODEX_SQLITE_HOME"] = codexHome;
        foreach (var variableName in CredentialEnvironmentVariableNames)
        {
            startInfo.Environment.Remove(variableName);
        }
        ApplyProxyEnvironment(startInfo);
        return startInfo;
    }

    public async Task<WindowsClientAccountProjection> PrepareWindowsClientAccountAsync(AccountRecord account)
    {
        return await Task.Run(() =>
        {
            using var switchMutex = new Mutex(false, WindowsClientSwitchMutexName);
            var mutexAcquired = false;
            try
            {
                try
                {
                    mutexAcquired = switchMutex.WaitOne(TimeSpan.FromSeconds(15));
                }
                catch (AbandonedMutexException)
                {
                    mutexAcquired = true;
                }
                if (!mutexAcquired)
                {
                    throw new TimeoutException(
                        "Another Codex account switch is still running. Please retry shortly.");
                }

                PersistSharedServiceTierToSelectedAccount();
                var status = ValidateWindowsClientAccountAsync(
                        account,
                        accessTokenMode: AccessTokenSharedProfileMode.ApiCompatible)
                    .GetAwaiter()
                    .GetResult();

                // CLI preparation writes the same shared profile used by the desktop client.
                // Serialize it with desktop switches and re-check after taking the lock so a CLI
                // click cannot rewrite credentials under an in-flight renderer launch/reload.
                if (CanReuseSharedProfileWithoutNetwork(
                        account,
                        Path.Combine(account.CodexHome, ConfigFileName),
                        AccessTokenSharedProfileMode.ApiCompatible))
                {
                    return CreateReusedSharedProfileProjection(
                        account,
                        status,
                        AccessTokenSharedProfileMode.ApiCompatible);
                }

                var launchGeneration = BeginWindowsClientLaunchGeneration();
                var shutdownTargets = CaptureWindowsClientProcessSnapshots();
                var shutdownNativeFastPorts = CaptureOfficialNativeFastPortsOwnedBy(shutdownTargets);
                StopWindowsClientProcesses(shutdownTargets);
                if (!WaitForWindowsClientProcessAndPortRelease(
                        shutdownTargets,
                        shutdownNativeFastPorts,
                        launchGeneration,
                        OfficialCodexRecoveryReleaseTimeout) ||
                    !WaitForOfficialCodexSwitchQuiescence(
                        launchGeneration,
                        OfficialCodexRecoveryReleaseTimeout))
                {
                    throw new TimeoutException(
                        "Codex did not remain fully closed before CLI credential preparation. " +
                        "Shared credentials were not changed; close Codex completely and retry.");
                }

                return ProjectWindowsClientAccount(
                    account,
                    status,
                    AccessTokenSharedProfileMode.ApiCompatible);
            }
            finally
            {
                if (mutexAcquired)
                {
                    switchMutex.ReleaseMutex();
                }
            }
        });
    }

    public Task<WindowsClientAccountProjection> SwitchWindowsClientAccountAsync(
        AccountRecord account,
        string projectPath)
    {
        return SwitchWindowsClientAccountAsync(
            account,
            projectPath,
            WindowsClientMode.CodexPlusPlus);
    }

    public async Task<WindowsClientAccountProjection> SwitchWindowsClientAccountAsync(
        AccountRecord account,
        string projectPath,
        WindowsClientMode mode)
    {
        return await SwitchWindowsClientAccountAsync(
            account,
            projectPath,
            mode,
            useDreamSkin: false,
            appearanceMode: ThemeMode.System);
    }

    public async Task<WindowsClientAccountProjection> SwitchWindowsClientAccountAsync(
        AccountRecord account,
        string projectPath,
        WindowsClientMode mode,
        bool useDreamSkin)
    {
        return await SwitchWindowsClientAccountAsync(
            account,
            projectPath,
            mode,
            useDreamSkin,
            ThemeMode.System);
    }

    public async Task<WindowsClientAccountProjection> SwitchWindowsClientAccountAsync(
        AccountRecord account,
        string projectPath,
        WindowsClientMode mode,
        bool useDreamSkin,
        ThemeMode appearanceMode,
        string appearancePresetId = "manager",
        string? appearanceLabel = null,
        bool routeOfficialOAuthThroughGateway = false,
        bool forceClientRestart = false)
    {
        return await SwitchWindowsClientAccountCoreAsync(
            account,
            projectPath,
            mode,
            useDreamSkin,
            appearanceMode,
            appearancePresetId,
            appearanceLabel,
            AccessTokenSharedProfileMode.ApiCompatible,
            chatGptFeatureAccount: null,
            routeOfficialOAuthThroughGateway,
            forceClientRestart);
    }

    public async Task<WindowsClientAccountProjection> SwitchWindowsClientAccountWithChatGptFeaturesAsync(
        AccountRecord account,
        AccountRecord chatGptFeatureAccount,
        string projectPath,
        bool useDreamSkin,
        ThemeMode appearanceMode,
        string appearancePresetId = "manager",
        string? appearanceLabel = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(chatGptFeatureAccount);
        if (account.IsOfficialOAuth)
        {
            throw new InvalidOperationException(
                "官方 ChatGPT 账号请直接使用 Codex 启动；双登录入口只适用于 Access Token 或兼容 API 账号。");
        }

        return await SwitchWindowsClientAccountCoreAsync(
            account,
            projectPath,
            WindowsClientMode.OfficialCodex,
            useDreamSkin,
            appearanceMode,
            appearancePresetId,
            appearanceLabel,
            AccessTokenSharedProfileMode.ChatGptDesktop,
            chatGptFeatureAccount,
            routeOfficialOAuthThroughGateway: false,
            forceClientRestart: false);
    }

    private async Task<WindowsClientAccountProjection> SwitchWindowsClientAccountCoreAsync(
        AccountRecord account,
        string projectPath,
        WindowsClientMode mode,
        bool useDreamSkin,
        ThemeMode appearanceMode,
        string appearancePresetId,
        string? appearanceLabel,
        AccessTokenSharedProfileMode accessTokenMode,
        AccountRecord? chatGptFeatureAccount,
        bool routeOfficialOAuthThroughGateway,
        bool forceClientRestart)
    {
        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException($"Project path does not exist: {projectPath}");
        }
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Windows client mode.");
        }

        // Capture the native picker value before the validation/reuse decision.  The shared
        // config belongs to the account that is currently running, not necessarily the account
        // represented by this click.
        PersistSharedServiceTierToSelectedAccount();

        if (accessTokenMode == AccessTokenSharedProfileMode.ChatGptDesktop)
        {
            PrepareChatGptFeatureAccount(account, chatGptFeatureAccount);
        }

        // A normal desktop switch only validates local files. Running login status, debug
        // models, or a minimal model request here could consume quota and block this click for
        // one or more 120-second CLI timeouts. Codex++ performs the online validation when the
        // user opens it; explicit status/login actions remain available separately.
        var status = await ValidateWindowsClientAccountAsync(
            account,
            localOnly: true,
            accessTokenMode: accessTokenMode,
            chatGptFeatureAccount: chatGptFeatureAccount);
        if (account.IsCompatibleApi)
        {
            // A compatible-API switch is destructive to the currently open client once the
            // switch lock is taken.  Verify the configured proxy, endpoint and model catalog
            // first so a stopped v2rayN/Clash process or a mistyped model cannot leave the user
            // looking at a blank Codex window after the old profile has already been replaced.
            await EnsureCompatibleApiLaunchPreflightAsync(account);
        }
        long successfulLaunchGeneration = 0;
        var projectionResult = await Task.Run(() =>
        {
            using var switchMutex = new Mutex(false, WindowsClientSwitchMutexName);
            var mutexAcquired = false;
            try
            {
                try
                {
                    mutexAcquired = switchMutex.WaitOne(TimeSpan.FromSeconds(15));
                }
                catch (AbandonedMutexException)
                {
                    mutexAcquired = true;
                }
                if (!mutexAcquired)
                {
                    throw new TimeoutException("Another Codex account switch is still running. Please retry shortly.");
                }

                // Invalidate every delayed deep-link task from the previous launch before
                // stopping or rewriting the shared profile. Otherwise an older task can wake
                // during this switch and reactivate the package against half-written state.
                var launchGeneration = BeginWindowsClientLaunchGeneration();

                // Re-check after taking the cross-process switch lock. Another installed/copy of
                // Account Manager may have changed the shared profile while this click was waiting.
                var exactSharedProfileAlreadySelected = CanReuseSharedProfileWithoutNetwork(
                    account,
                    Path.Combine(account.CodexHome, ConfigFileName),
                    accessTokenMode,
                    chatGptFeatureAccount,
                    routeOfficialOAuthThroughGateway);
                var sharedProfileAlreadySelected = exactSharedProfileAlreadySelected ||
                    accessTokenMode == AccessTokenSharedProfileMode.ChatGptDesktop &&
                    chatGptFeatureAccount != null &&
                    CanIdentifySharedChatGptFeatureProfileWithoutNetwork(
                        account,
                        chatGptFeatureAccount);
                if (sharedProfileAlreadySelected && !exactSharedProfileAlreadySelected)
                {
                    // Codex can persist harmless runtime preferences while it is open. The
                    // immutable model bearer, endpoint, OAuth owner, selection sidecar and
                    // active-account state still prove this is the same dual-login profile, so
                    // preserve the healthy process instead of closing it merely to canonicalize
                    // unrelated config text.
                    WriteCodexPlusPlusLaunchDiagnostic(
                        "official-dual-profile-reused-with-runtime-drift",
                        "the verified model/OAuth owners were unchanged; existing Codex was preserved");
                }
                // The visible "Codex 启动" action is also the user's recovery switch.  When
                // requested, close the exact operation-start Codex process set even if the
                // projected account already matches, then reopen it from the verified profile.
                // Automatic/request-boundary rotation keeps the reuse behavior unchanged.
                var switchRequired = forceClientRestart ||
                                     RequiresWindowsClientShutdown(sharedProfileAlreadySelected);
                var shutdownTargets = switchRequired
                    ? CaptureWindowsClientProcessSnapshots()
                    : Array.Empty<WindowsClientProcessSnapshot>();
                var shutdownNativeFastPorts = switchRequired
                    ? CaptureOfficialNativeFastPortsOwnedBy(shutdownTargets)
                    : Array.Empty<int>();
                WindowsClientSharedProfileSnapshot? previousSharedProfileSnapshot = null;
            WindowsClientAccountProjection projection;
            if (switchRequired)
            {
                // Work only from the operation-start snapshot so a delayed shutdown can never
                // terminate a newer Codex process started by the user during this switch.
                StopWindowsClientProcesses(shutdownTargets);
                if (!WaitForWindowsClientProcessAndPortRelease(
                        shutdownTargets,
                        shutdownNativeFastPorts,
                        launchGeneration,
                        OfficialCodexRecoveryReleaseTimeout) ||
                    !WaitForOfficialCodexSwitchQuiescence(
                        launchGeneration,
                        OfficialCodexRecoveryReleaseTimeout))
                {
                    throw new TimeoutException(
                        "Codex did not remain fully closed, or a native bridge port did not release cleanly. " +
                        "Account credentials were not changed; close Codex completely and retry.");
                }
            }
            if (sharedProfileAlreadySelected)
            {
                // The profile itself can still be reused after a forced recovery restart; no
                // credential rewrite is needed when the account identity already matches.
                projection = CreateReusedSharedProfileProjection(
                    account,
                    status,
                    accessTokenMode,
                    chatGptFeatureAccount);
            }
            else
            {
                if (IsExplicitChatGptFeatureSwitch(
                        mode,
                        accessTokenMode,
                        chatGptFeatureAccount))
                {
                    // Keep an operation-owned, in-memory copy of only the five managed shared
                    // profile files. ProjectWindowsClientAccount also creates durable backups,
                    // but this snapshot lets a projection failure restore the pre-switch profile
                    // even when it throws before it can return its backup metadata.
                    previousSharedProfileSnapshot = CaptureWindowsClientSharedProfileSnapshot();
                }
                WindowsClientAccountProjection? pendingProjection = null;
                try
                {
                    pendingProjection = ProjectWindowsClientAccount(
                        account,
                        status,
                        accessTokenMode,
                        chatGptFeatureAccount,
                        routeOfficialOAuthThroughGateway);
                    NormalizeDesktopSidebarState(pendingProjection);
                    AlignDesktopProfileModelState(account, pendingProjection);
                    SanitizeProjectModelOverrides(projectPath, pendingProjection);
                    pendingProjection.ProfileChanged = true;
                    projection = pendingProjection;
                }
                catch (Exception projectionError)
                {
                    Exception? restoreError = null;
                    var profileRestored = false;
                    if (previousSharedProfileSnapshot != null)
                    {
                        try
                        {
                            RestoreWindowsClientSwitchProjection(
                                pendingProjection,
                                previousSharedProfileSnapshot);
                            profileRestored = WindowsClientSharedProfileMatchesSnapshot(
                                previousSharedProfileSnapshot);
                        }
                        catch (Exception ex)
                        {
                            restoreError = ex;
                        }
                    }
                    else if (pendingProjection != null)
                    {
                        try
                        {
                            RestoreWindowsClientSwitchProjection(
                                pendingProjection,
                                previousSharedProfileSnapshot: null);
                        }
                        catch
                        {
                            // Preserve the original behavior for non-dual-login switches: backup
                            // paths remain available for manual recovery if restoration itself fails.
                        }
                    }

                    if (previousSharedProfileSnapshot != null)
                    {
                        var previousClientRelaunchStarted = profileRestored &&
                            TryLaunchOfficialCodexFallback(projectPath, launchGeneration);
                        WriteCodexPlusPlusLaunchDiagnostic(
                            profileRestored
                                ? "official-dual-projection-rolled-back"
                                : "official-dual-projection-rollback-failed",
                            $"previous_client_relaunch_started={previousClientRelaunchStarted}; " +
                            $"restore_error={restoreError?.GetType().Name ?? "none"}");
                        throw new InvalidOperationException(
                            "双登录配置投放失败。" +
                            (profileRestored
                                ? previousClientRelaunchStarted
                                    ? "切换前的凭据已恢复，并已自动请求重新打开原 Codex。"
                                    : "切换前的凭据已恢复，但原 Codex 未能自动重新打开。"
                                : "切换前的凭据未能完整恢复；为避免继续写入，本次没有再次启动 Codex。"),
                            restoreError == null
                                ? projectionError
                                : new AggregateException(projectionError, restoreError));
                    }

                    throw;
                }
            }

            projection.ClientMode = mode;
            try
            {
                projection.ClientLaunchStarted = LaunchWindowsClient(
                    account,
                    projectPath,
                    projection.DefaultCodexHome,
                    mode,
                    shutdownTargets,
                    switchRequired,
                    useDreamSkin,
                    appearanceMode,
                    appearancePresetId,
                    appearanceLabel,
                    allowOfficialRendererPatch: ShouldApplyOfficialRendererPatch(
                        account,
                        accessTokenMode,
                        chatGptFeatureAccount),
                    expectedLaunchGeneration: launchGeneration);
                if (projection.ClientLaunchStarted)
                {
                    successfulLaunchGeneration = launchGeneration;
                }
                projection.CodexPlusPlusLaunchStarted =
                    mode == WindowsClientMode.CodexPlusPlus && projection.ClientLaunchStarted;
            }
            catch (CodexDreamSkinApplyException ex)
            {
                projection.ClientLaunchStarted = ex.OfficialClientRelaunched;
                projection.CodexPlusPlusLaunchStarted = false;
                projection.ClientLaunchError = MaskSensitive(ex.Message);
                projection.CodexDreamSkinFailed = true;
                projection.CodexOfficialAppearanceRestored = ex.OfficialAppearanceRestored;
            }
            catch (Exception ex)
            {
                projection.ClientLaunchStarted = false;
                projection.CodexPlusPlusLaunchStarted = false;
                projection.ClientLaunchError = MaskSensitive(ex.Message);
            }

            if (ShouldRollbackFailedExplicitChatGptFeatureSwitch(
                    projection,
                    mode,
                    accessTokenMode,
                    chatGptFeatureAccount,
                    switchRequired) &&
                previousSharedProfileSnapshot != null)
            {
                RecoverFailedExplicitChatGptFeatureSwitch(
                    projection,
                    previousSharedProfileSnapshot,
                    projectPath,
                    launchGeneration);
            }

            return projection;
            }
            finally
            {
                if (mutexAcquired)
                {
                    switchMutex.ReleaseMutex();
                }
            }
        });

        if (projectionResult.ClientLaunchStarted && mode == WindowsClientMode.OfficialCodex)
        {
            var displayAccount = chatGptFeatureAccount ?? (account.IsOfficialOAuth ? account : null);
            if (displayAccount != null)
            {
                QueueOfficialAccountDisplay(
                    account,
                    displayAccount,
                    isDualLogin: chatGptFeatureAccount != null,
                    successfulLaunchGeneration);
            }
        }
        return projectionResult;
    }

    private static bool RequiresWindowsClientShutdown(bool sharedProfileAlreadySelected)
    {
        return !sharedProfileAlreadySelected;
    }

    private static bool ShouldApplyOfficialRendererPatch(
        AccountRecord modelAccount,
        AccessTokenSharedProfileMode accessTokenMode,
        AccountRecord? chatGptFeatureAccount)
    {
        ArgumentNullException.ThrowIfNull(modelAccount);
        return accessTokenMode == AccessTokenSharedProfileMode.ApiCompatible &&
               chatGptFeatureAccount == null &&
               (modelAccount.IsAccessToken || modelAccount.IsCompatibleApi);
    }

    private static bool IsExplicitChatGptFeatureSwitch(
        WindowsClientMode mode,
        AccessTokenSharedProfileMode accessTokenMode,
        AccountRecord? chatGptFeatureAccount)
    {
        return mode == WindowsClientMode.OfficialCodex &&
               accessTokenMode == AccessTokenSharedProfileMode.ChatGptDesktop &&
               chatGptFeatureAccount != null;
    }

    private static bool ShouldRollbackFailedExplicitChatGptFeatureSwitch(
        WindowsClientAccountProjection projection,
        WindowsClientMode mode,
        AccessTokenSharedProfileMode accessTokenMode,
        AccountRecord? chatGptFeatureAccount,
        bool switchRequired)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return IsExplicitChatGptFeatureSwitch(
                   mode,
                   accessTokenMode,
                   chatGptFeatureAccount) &&
               switchRequired &&
               projection.ProfileChanged &&
               !projection.ClientLaunchStarted;
    }

    private void RecoverFailedExplicitChatGptFeatureSwitch(
        WindowsClientAccountProjection projection,
        WindowsClientSharedProfileSnapshot previousSharedProfileSnapshot,
        string projectPath,
        long launchGeneration)
    {
        var targetLaunchError = projection.ClientLaunchError ??
                                "未返回可验证的启动结果";
        if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
        {
            projection.FailedLaunchRecoveryError =
                "另一个账号启动已接管当前操作，旧凭据没有被并发回写。";
            return;
        }

        var failedLaunchTargets = CaptureWindowsClientProcessSnapshots();
        var failedLaunchPorts = CaptureOfficialNativeFastPortsOwnedBy(failedLaunchTargets);
        // LaunchOfficialCodex owns cleanup of every PID/start-time identity that it
        // deliberately created.  Do not broaden a rollback into killing every Codex
        // process visible at this later instant: the user may have manually reopened a
        // client while the launch was failing.  Restore credentials only after passive
        // proof that no official client/owned CDP listener remains.
        if (!WaitForWindowsClientProcessAndPortRelease(
                failedLaunchTargets,
                failedLaunchPorts,
                launchGeneration,
                OfficialCodexRecoveryReleaseTimeout) ||
            !WaitForOfficialCodexSwitchQuiescence(
                launchGeneration,
                OfficialCodexRecoveryReleaseTimeout))
        {
            projection.FailedLaunchRecoveryError =
                "失败的 Codex 进程或端口未完全释放，未在运行中的客户端下回写旧凭据。";
            projection.ClientLaunchError = targetLaunchError + "；" +
                                           projection.FailedLaunchRecoveryError;
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-dual-launch-rollback-quiescence-failed",
                $"process_count={failedLaunchTargets.Count}; data_preserved=true");
            return;
        }

        try
        {
            RestoreWindowsClientSwitchProjection(
                projection,
                previousSharedProfileSnapshot);
            if (!WindowsClientSharedProfileMatchesSnapshot(previousSharedProfileSnapshot))
            {
                throw new InvalidOperationException(
                    "恢复后的共享凭据与切换前快照不一致。");
            }

            projection.FailedLaunchProfileRestored = true;
            projection.ProfileChanged = false;
            projection.PreviousClientRelaunchStarted =
                TryLaunchOfficialCodexFallback(projectPath, launchGeneration);
            projection.ClientLaunchError = targetLaunchError + "；" +
                (projection.PreviousClientRelaunchStarted
                    ? "已恢复切换前凭据，并自动请求重新打开原 Codex"
                    : "已恢复切换前凭据，但原 Codex 未能自动重新打开");
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-dual-launch-rolled-back",
                $"previous_client_relaunch_started={projection.PreviousClientRelaunchStarted}; " +
                "shared_profile_verified=true");
        }
        catch (Exception ex)
        {
            projection.FailedLaunchRecoveryError = MaskSensitive(ex.Message);
            projection.ClientLaunchError = targetLaunchError +
                "；切换前凭据恢复失败：" + projection.FailedLaunchRecoveryError;
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-dual-launch-rollback-failed",
                $"error_type={ex.GetType().Name}; data_preserved=true");
        }
    }

    private void PrepareChatGptFeatureAccount(
        AccountRecord modelAccount,
        AccountRecord? chatGptFeatureAccount)
    {
        ArgumentNullException.ThrowIfNull(modelAccount);
        if (chatGptFeatureAccount == null || !chatGptFeatureAccount.IsOfficialOAuth)
        {
            throw new InvalidOperationException(
                "语音与手机连接需要绑定一个“通过 ChatGPT 登录（官方）”账号；" +
                "PAT 或兼容 API 仍只负责普通模型请求。");
        }
        if (PathsEqual(modelAccount.CodexHome, chatGptFeatureAccount.CodexHome))
        {
            throw new InvalidOperationException(
                "模型账号与 ChatGPT 功能账号必须使用不同的独立凭据目录。");
        }

        EnsureOfficialOAuthAccountConfig(chatGptFeatureAccount);
        var authPath = Path.Combine(chatGptFeatureAccount.CodexHome, AuthFileName);
        if (!IsChatGptDesktopAuthJson(authPath) ||
            !TryReadChatGptAuthAccountId(authPath, out _))
        {
            throw new InvalidOperationException(
                $"账号 {chatGptFeatureAccount.Name} 没有可验证身份的 ChatGPT 官方登录态。\n\n" +
                "请先在该账号上完成“通过 ChatGPT 登录”，再使用语音/手机功能启动。");
        }
    }

    private void QueueOfficialAccountDisplay(
        AccountRecord modelAccount,
        AccountRecord chatGptAccount,
        bool isDualLogin,
        long launchGeneration,
        AccountRecord? projectedModelAccount = null,
        long? displayGeneration = null)
    {
        var modelSnapshot = SnapshotAccountForDisplay(modelAccount);
        var chatGptSnapshot = SnapshotAccountForDisplay(chatGptAccount);
        var projectedModelSnapshot = projectedModelAccount == null
            ? null
            : SnapshotAccountForDisplay(projectedModelAccount);
        var currentDisplayGeneration = displayGeneration ??
                                       Interlocked.Increment(
                                           ref _officialAccountDisplayGeneration);
        _ = Task.Run(() => TryApplyOfficialAccountDisplayAsync(
            modelSnapshot,
            chatGptSnapshot,
            isDualLogin,
            launchGeneration,
            currentDisplayGeneration,
            projectedModelSnapshot));
    }

    public void QueueCurrentOfficialAccountDisplay(IReadOnlyList<AccountRecord> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var snapshots = accounts.Select(SnapshotAccountForDisplay).ToArray();
        var launchGeneration = Volatile.Read(ref _windowsClientLaunchGeneration);
        var displayGeneration = Interlocked.Increment(
            ref _officialAccountDisplayGeneration);
        _ = Task.Run(() =>
        {
            try
            {
                var oauthAccounts = snapshots.Where(account => account.IsOfficialOAuth).ToArray();
                var dualSelections = new List<(AccountRecord Model, AccountRecord ChatGpt)>();
                foreach (var modelAccount in snapshots.Where(account => !account.IsOfficialOAuth))
                {
                    foreach (var chatGptAccount in oauthAccounts)
                    {
                        if (CanIdentifySharedChatGptFeatureProfileWithoutNetwork(
                                modelAccount,
                                chatGptAccount))
                        {
                            dualSelections.Add((modelAccount, chatGptAccount));
                        }
                    }
                }

                var oauthSelections = oauthAccounts
                    .Where(account => CanReuseOfficialOAuthSharedProfile(account))
                    .ToArray();
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-account-display-discovery",
                    $"oauth_count={oauthAccounts.Length}; " +
                    $"dual_selection_count={dualSelections.Count}; " +
                    $"pure_oauth_selection_count={oauthSelections.Length}");

                if (dualSelections.Count == 1)
                {
                    var selected = dualSelections[0];
                    QueueOfficialAccountDisplay(
                        selected.Model,
                        selected.ChatGpt,
                        isDualLogin: true,
                        launchGeneration,
                        displayGeneration: displayGeneration);
                    return;
                }
                if (dualSelections.Count != 0)
                {
                    return;
                }

                if (oauthSelections.Length == 1)
                {
                    QueueOfficialAccountDisplay(
                        oauthSelections[0],
                        oauthSelections[0],
                        isDualLogin: false,
                        launchGeneration,
                        displayGeneration: displayGeneration);
                }
            }
            catch (Exception ex)
            {
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-account-display-discovery-unavailable",
                    "stage failed with " + ex.GetType().Name);
            }
        });
    }

    public void QueueHotRotatedOfficialAccountDisplay(
        AccountRecord modelAccount,
        IReadOnlyList<AccountRecord> accounts)
    {
        ArgumentNullException.ThrowIfNull(modelAccount);
        ArgumentNullException.ThrowIfNull(accounts);
        if (modelAccount.IsOfficialOAuth)
        {
            return;
        }

        var targetKey = QuotaAccountIdentity.CreateKey(modelAccount);
        var snapshots = accounts.Select(SnapshotAccountForDisplay).ToArray();
        var targetSnapshot = snapshots.FirstOrDefault(account =>
            QuotaAccountIdentity.CreateKey(account).Equals(
                targetKey,
                StringComparison.Ordinal));
        if (targetSnapshot == null)
        {
            return;
        }

        var launchGeneration = Volatile.Read(ref _windowsClientLaunchGeneration);
        var displayGeneration = Interlocked.Increment(
            ref _officialAccountDisplayGeneration);
        _ = Task.Run(() =>
        {
            try
            {
                if (!IsCurrentOfficialAccountDisplayGeneration(displayGeneration))
                {
                    return;
                }

                var oauthAccounts = snapshots.Where(account => account.IsOfficialOAuth).ToArray();
                var selectedProfiles = new List<(AccountRecord Model, AccountRecord ChatGpt)>();
                foreach (var projectedModel in snapshots.Where(account => !account.IsOfficialOAuth))
                {
                    foreach (var chatGptAccount in oauthAccounts)
                    {
                        if (CanIdentifySharedChatGptFeatureProfileWithoutNetwork(
                                projectedModel,
                                chatGptAccount))
                        {
                            selectedProfiles.Add((projectedModel, chatGptAccount));
                        }
                    }
                }

                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-account-display-hot-rotation",
                    $"selected_profile_count={selectedProfiles.Count}; " +
                    $"target_kind={(targetSnapshot.IsCompatibleApi ? "api" : "pat")}");
                if (selectedProfiles.Count != 1 ||
                    !IsCurrentOfficialAccountDisplayGeneration(displayGeneration))
                {
                    return;
                }

                var selected = selectedProfiles[0];
                QueueOfficialAccountDisplay(
                    targetSnapshot,
                    selected.ChatGpt,
                    isDualLogin: true,
                    launchGeneration,
                    projectedModelAccount: selected.Model,
                    displayGeneration: displayGeneration);
            }
            catch (Exception ex)
            {
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-account-display-hot-rotation-unavailable",
                    "stage failed with " + ex.GetType().Name);
            }
        });
    }

    private async Task TryApplyOfficialAccountDisplayAsync(
        AccountRecord modelAccount,
        AccountRecord chatGptAccount,
        bool isDualLogin,
        long launchGeneration,
        long displayGeneration,
        AccountRecord? projectedModelAccount)
    {
        await OfficialAccountDisplayGate.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(OfficialAccountDisplayTimeout);
            if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration) ||
                !IsCurrentOfficialAccountDisplayGeneration(displayGeneration))
            {
                return;
            }

            var identityBefore = GetChatGptFeatureIdentityBindingKey(chatGptAccount);
            if (identityBefore == null)
            {
                return;
            }

            var identity = await _appServer.ReadAccountIdentityAsync(
                chatGptAccount.CodexHome,
                timeout.Token);
            var identityAfter = GetChatGptFeatureIdentityBindingKey(chatGptAccount);
            if (identity == null ||
                identityAfter == null ||
                !identityBefore.Equals(identityAfter, StringComparison.Ordinal))
            {
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-account-display-skipped",
                    "the OAuth account/read identity did not remain bound to one account_id");
                return;
            }

            using var switchMutex = new Mutex(false, WindowsClientSwitchMutexName);
            var mutexAcquired = false;
            try
            {
                try
                {
                    // Identity decoration is optional. If any Account Manager is switching the
                    // shared profile, abandon this stale display attempt instead of waiting.
                    mutexAcquired = switchMutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    mutexAcquired = true;
                }
                if (!mutexAcquired)
                {
                    WriteCodexPlusPlusLaunchDiagnostic(
                        "official-account-display-deferred",
                        "an account switch owns the identity display gate");
                    return;
                }

                var profileModelAccount = projectedModelAccount ?? modelAccount;
                var selected = isDualLogin
                    ? CanIdentifySharedChatGptFeatureProfileWithoutNetwork(
                        profileModelAccount,
                        chatGptAccount)
                    : modelAccount.IsOfficialOAuth &&
                      PathsEqual(modelAccount.CodexHome, chatGptAccount.CodexHome) &&
                      CanReuseOfficialOAuthSharedProfile(chatGptAccount);
                var finalIdentityBinding = GetChatGptFeatureIdentityBindingKey(chatGptAccount);
                if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration) ||
                    !IsCurrentOfficialAccountDisplayGeneration(displayGeneration) ||
                    !selected ||
                    finalIdentityBinding == null ||
                    !identityAfter.Equals(finalIdentityBinding, StringComparison.Ordinal))
                {
                    WriteCodexPlusPlusLaunchDiagnostic(
                        "official-account-display-skipped",
                        "the shared model/OAuth projection changed before display injection");
                    return;
                }

                var endpoints = new List<(int Port, string BrowserId, WindowsClientActivationIdentity Owner)>();
                foreach (var port in OfficialNativeFastCdpPortCandidates())
                {
                    if (TryCaptureOfficialNativeFastEndpoint(
                            port,
                            out var browserId,
                            out var listenerIdentity))
                    {
                        endpoints.Add((port, browserId, listenerIdentity));
                    }
                }
                if (endpoints.Count != 1)
                {
                    WriteCodexPlusPlusLaunchDiagnostic(
                        "official-account-display-deferred",
                        $"verified_endpoint_count={endpoints.Count}; exactly one official owner is required");
                    return;
                }

                var ownerRoot = GetCodexWindowsClientAppDirectory();
                if (ownerRoot == null)
                {
                    return;
                }
                var display = new CodexAccountDisplay(
                    isDualLogin ? NormalizeModelAccountDisplayLabel(modelAccount.Name) : null,
                    identity.Email)
                {
                    ModelAccountTypeLabel = isDualLogin
                        ? modelAccount.IsCompatibleApi
                            ? "API 模型账号"
                            : "PAT 模型账号"
                        : null
                };
                var endpoint = endpoints[0];
                var applied = CodexNativeFastBridge.TryApplyAccountDisplayAsync(
                        endpoint.Port,
                        endpoint.BrowserId,
                        endpoint.Owner.ProcessId,
                        endpoint.Owner.StartTimeUtcTicks ?? 0,
                        ownerRoot,
                        display,
                        timeout.Token)
                    .GetAwaiter()
                    .GetResult();
                if (!applied)
                {
                    WriteCodexPlusPlusLaunchDiagnostic(
                        "official-account-display-deferred",
                        "the unique verified primary Codex renderer rejected the optional identity display");
                }
            }
            finally
            {
                if (mutexAcquired)
                {
                    switchMutex.ReleaseMutex();
                }
            }
        }
        catch (OperationCanceledException)
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-account-display-unavailable",
                "optional identity display timed out or was superseded");
        }
        catch (Exception ex)
        {
            // Identity decoration is optional. It must never turn an otherwise healthy launch
            // into a failure or expose an email/token in diagnostics.
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-account-display-unavailable",
                "stage failed with " + ex.GetType().Name);
        }
        finally
        {
            OfficialAccountDisplayGate.Release();
        }
    }

    private static bool IsCurrentOfficialAccountDisplayGeneration(long generation) =>
        Volatile.Read(ref _officialAccountDisplayGeneration) == generation;

    private static AccountRecord SnapshotAccountForDisplay(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new AccountRecord
        {
            Name = account.Name,
            CodexHome = account.CodexHome,
            AuthKind = account.AuthKind,
            ApiProviderName = account.ApiProviderName,
            ApiBaseUrl = account.ApiBaseUrl,
            ApiModel = account.ApiModel,
            ApiWireApi = account.ApiWireApi,
            QuotaLimitType = account.QuotaLimitType,
            QuotaPrimaryWindowMinutes = account.QuotaPrimaryWindowMinutes,
            QuotaSecondaryWindowMinutes = account.QuotaSecondaryWindowMinutes,
            QuotaLimitObservedAtUtc = account.QuotaLimitObservedAtUtc
        };
    }

    private static string NormalizeModelAccountDisplayLabel(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > 180)
        {
            normalized = normalized[..180];
        }
        return normalized;
    }

    private async Task<LoginStatus> ValidateWindowsClientAccountAsync(
        AccountRecord account,
        bool localOnly = false,
        AccessTokenSharedProfileMode accessTokenMode = AccessTokenSharedProfileMode.ChatGptDesktop,
        AccountRecord? chatGptFeatureAccount = null)
    {
        if (account.IsAccessToken)
        {
            await LocalPatGateway.EnsureRunningAsync();
        }
        else if (account.IsOfficialOAuth)
        {
            EnsureOfficialOAuthAccountConfig(account);
            SyncStoredOfficialOAuthAuthToAccount(account);
        }
        var sourceAuthPath = Path.Combine(account.CodexHome, AuthFileName);
        var sourceConfigPath = Path.Combine(account.CodexHome, ConfigFileName);
        if (!File.Exists(sourceAuthPath))
        {
            var message = account.IsCompatibleApi
                ? $"Account {account.Name} is missing {AuthFileName} and cannot be used by the Codex Windows client."
                : account.IsOfficialOAuth
                    ? $"账号 {account.Name} 还没有 ChatGPT 登录凭据。\n\n请先点击账号卡片里的“通过 ChatGPT 登录”，在 OpenAI 官方网页完成登录后再启动。"
                    : $"账号 {account.Name} 还没有保存 Codex 登录凭据。\n\n缺少文件：{sourceAuthPath}\n\n请点击这个账号卡片里的“Token”或“编辑”，填入新的 Access Token 后再启动。";
            throw new FileNotFoundException(message, sourceAuthPath);
        }

        if (account.IsCompatibleApi && !File.Exists(sourceConfigPath))
        {
            throw new FileNotFoundException(
                $"Account {account.Name} is missing {ConfigFileName} and cannot be used by the Codex Windows client.",
                sourceConfigPath);
        }
        if (localOnly &&
            (account.IsOfficialOAuth
                ? !IsChatGptDesktopAuthJson(sourceAuthPath)
                : !IsLocallyUsableAuthJson(sourceAuthPath)))
        {
            throw new InvalidDataException(
                account.IsOfficialOAuth
                    ? $"账号 {account.Name} 的 auth.json 不是通过 ChatGPT 登录生成的官方凭据；没有切换共享凭据。"
                    : $"账号 {account.Name} 的 auth.json 不是可用的本地凭据文件；没有切换共享凭据。",
                new JsonException("auth.json must be a non-empty JSON object without ambiguous duplicate keys."));
        }

        if (account.IsAccessToken)
        {
            _ = ReadAccessTokenCredential(sourceAuthPath);
        }

        if (account.IsCompatibleApi)
        {
            ProjectCompatibleApiConfig(sourceConfigPath, account);
        }
        else if (account.IsOfficialOAuth)
        {
            EnsureOfficialOAuthAccountConfig(account);
        }
        else
        {
            ProjectAccessTokenSourceConfig(sourceConfigPath);
        }

        var sharedProfileCanBeReused =
            accessTokenMode == AccessTokenSharedProfileMode.ChatGptDesktop &&
            chatGptFeatureAccount != null
                ? CanReuseChatGptFeatureProfileForLaunchWithoutNetwork(
                    account,
                    sourceConfigPath,
                    chatGptFeatureAccount)
                : CanReuseSharedProfileWithoutNetwork(
                    account,
                    sourceConfigPath,
                    accessTokenMode,
                    chatGptFeatureAccount);
        var emptySharedProfileCanBeInitialized =
            CanInitializeEmptySharedProfileWithoutNetwork(account, sourceAuthPath, sourceConfigPath);
        var localProjectionIsSufficient =
            sharedProfileCanBeReused || emptySharedProfileCanBeInitialized;
        var status = localOnly
            ? new LoginStatus
            {
                ExitCode = 0,
                Text = "本地凭据结构校验通过；启动切号未发送登录、模型或额度探测请求。"
            }
            : localProjectionIsSufficient
            ? new LoginStatus
            {
                ExitCode = 0,
                Text = sharedProfileCanBeReused
                    ? $"共享 Codex 已在使用 {account.Name} 的同一份凭据，已跳过重复网络预检。"
                    : $"共享 Codex 尚未登录，已使用 {account.Name} 的本地凭据直接准备 Codex++。"
            }
            : account.IsCompatibleApi || account.IsOfficialOAuth
                ? await GetLoginStatusAsync(account)
                : await GetAccessTokenSwitchStatusAsync(account);
        if (status.ExitCode != 0)
        {
            var message = account.IsCompatibleApi
                ? $"Account {account.Name} failed compatible API preflight. The shared Codex profile was not changed.\n\n{status.Text}"
                : account.IsOfficialOAuth
                    ? $"账号 {account.Name} 的 ChatGPT 登录状态不可用，默认 Codex 凭据没有被修改。\n\n请点击“通过 ChatGPT 登录”重新完成官方登录。\n\n{status.Text}"
                : IsPersonalAccessTokenMetadataRequestFailure(status.Text)
                    ? BuildPersonalAccessTokenNetworkMessage(status.Text)
                    : $"账号 {account.Name} 的 Codex 登录预检失败，默认 Codex 凭据没有被修改。\n\n这通常表示该账号的 Access Token 已失效，或账号目录不是已登录状态。请点击这个账号卡片里的“Token”或“编辑”，重新填入新的 Access Token。\n\n{status.Text}";
            throw new InvalidOperationException(message);
        }

        if (!localOnly && !localProjectionIsSufficient && !account.IsOfficialOAuth)
        {
            await EnsureAccountModelAvailableAsync(account);
        }
        return status;
    }

    private static bool CanReuseSharedProfileWithoutNetwork(
        AccountRecord account,
        string sourceConfigPath,
        AccessTokenSharedProfileMode accessTokenMode = AccessTokenSharedProfileMode.ChatGptDesktop,
        AccountRecord? chatGptFeatureAccount = null,
        bool routeOfficialOAuthThroughGateway = false)
    {
        if (account.IsOfficialOAuth)
        {
            return CanReuseOfficialOAuthSharedProfile(
                account,
                routeOfficialOAuthThroughGateway);
        }

        var sharedHome = GetDefaultCodexHome();
        var sharedAuthPath = Path.Combine(sharedHome, AuthFileName);
        if (File.Exists(Path.Combine(sharedHome, CockpitAuthFileName)) ||
            accessTokenMode == AccessTokenSharedProfileMode.ApiCompatible &&
            File.Exists(Path.Combine(sharedHome, DesktopSelectionFileName)))
        {
            return false;
        }
        if (accessTokenMode == AccessTokenSharedProfileMode.ChatGptDesktop)
        {
            if (!IsAccessTokenDesktopSessionSelected(account, sharedHome, sharedAuthPath))
            {
                return false;
            }
            if (chatGptFeatureAccount != null &&
                (!chatGptFeatureAccount.IsOfficialOAuth ||
                 !IsDesktopAuthSelectionForAccount(sharedHome, chatGptFeatureAccount)))
            {
                return false;
            }
            if (chatGptFeatureAccount != null)
            {
                var featureAuthPath = Path.Combine(
                    chatGptFeatureAccount.CodexHome,
                    AuthFileName);
                var storedFeatureAuthPath = GetStoredDesktopAuthPath(
                    sharedHome,
                    chatGptFeatureAccount);
                if (!ChatGptAuthAccountsMatch(sharedAuthPath, featureAuthPath) &&
                    !ChatGptAuthAccountsMatch(sharedAuthPath, storedFeatureAuthPath))
                {
                    return false;
                }
            }
        }
        else if (account.IsCompatibleApi)
        {
            if (!IsAccessTokenDesktopAuthSelected(
                    Path.Combine(account.CodexHome, AuthFileName),
                    sharedAuthPath))
            {
                return false;
            }
        }
        else if (!IsAccessTokenDesktopAuthSelected(
                     Path.Combine(account.CodexHome, AuthFileName),
                     sharedAuthPath))
        {
            return false;
        }

        var sharedConfigPath = Path.Combine(sharedHome, ConfigFileName);
        if (!File.Exists(sourceConfigPath) || !File.Exists(sharedConfigPath))
        {
            return false;
        }

        try
        {
            var targetServiceTier = ReadDesktopServiceTier(File.ReadAllText(sourceConfigPath));
            var sharedConfig = File.ReadAllText(sharedConfigPath);
            var projectedSharedConfig = accessTokenMode == AccessTokenSharedProfileMode.ChatGptDesktop
                ? account.IsCompatibleApi
                    ? ProjectCompatibleApiConfigText(
                        sharedConfig,
                        account,
                        requiresOpenAiAuth: true,
                            providerBearerToken: ReadAccessTokenCredential(
                                Path.Combine(account.CodexHome, AuthFileName)),
                        forceFileAuthStore: true,
                        serviceTier: targetServiceTier)
                    : ProjectWindowsClientConfigText(
                        sharedConfig,
                        requiresOpenAiAuth: true,
                        desktopProviderName: account.Name,
                        providerBearerToken: ReadAccessTokenCredential(
                            Path.Combine(account.CodexHome, AuthFileName)),
                        forceFileAuthStore: true,
                        serviceTier: targetServiceTier)
                : account.IsCompatibleApi
                    ? ProjectCompatibleApiConfigText(
                        sharedConfig,
                        account,
                        requiresOpenAiAuth: true,
                        forceFileAuthStore: true,
                        serviceTier: targetServiceTier)
                    : ProjectWindowsClientConfigText(
                        sharedConfig,
                        requiresOpenAiAuth: true,
                        desktopProviderName: account.Name,
                        forceFileAuthStore: true,
                        serviceTier: targetServiceTier);
            projectedSharedConfig = PreserveSharedMcpServerSections(
                sharedConfig,
                projectedSharedConfig);
            var currentFingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(
                NormalizeTextForFingerprint(sharedConfig)));
            var projectedFingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(
                NormalizeTextForFingerprint(projectedSharedConfig)));
            return CryptographicOperations.FixedTimeEquals(currentFingerprint, projectedFingerprint);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or FormatException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool CanIdentifySharedChatGptFeatureProfileWithoutNetwork(
        AccountRecord modelAccount,
        AccountRecord chatGptFeatureAccount)
    {
        ArgumentNullException.ThrowIfNull(modelAccount);
        ArgumentNullException.ThrowIfNull(chatGptFeatureAccount);
        if (modelAccount.IsOfficialOAuth ||
            !chatGptFeatureAccount.IsOfficialOAuth ||
            PathsEqual(modelAccount.CodexHome, chatGptFeatureAccount.CodexHome))
        {
            return false;
        }

        var sharedHome = Path.GetFullPath(GetDefaultCodexHome());
        var sharedAuthPath = Path.Combine(sharedHome, AuthFileName);
        if (File.Exists(Path.Combine(sharedHome, CockpitAuthFileName)) ||
            !TryReadDesktopSelectionKeys(
                sharedHome,
                out var selectedModelKey,
                out var selectedAuthKey,
                out var selectedAuthHome) ||
            !selectedModelKey.Equals(
                GetDesktopAccountKey(modelAccount),
                StringComparison.OrdinalIgnoreCase) ||
            !selectedAuthKey.Equals(
                GetDesktopAccountKey(chatGptFeatureAccount),
                StringComparison.OrdinalIgnoreCase) ||
            !PathsEqual(selectedAuthHome, chatGptFeatureAccount.CodexHome) ||
            !TryReadActiveAccountState(
                sharedHome,
                out var activeAccountKey,
                out var activeAccountHome,
                out var activeMode) ||
            !activeAccountKey.Equals(selectedModelKey, StringComparison.OrdinalIgnoreCase) ||
            !PathsEqual(activeAccountHome, modelAccount.CodexHome) ||
            !activeMode.Equals(
                GetActiveAccountMode(
                    modelAccount,
                    AccessTokenSharedProfileMode.ChatGptDesktop),
                StringComparison.Ordinal) ||
            !IsChatGptDesktopAuthJson(sharedAuthPath))
        {
            return false;
        }

        var featureAuthPath = Path.Combine(chatGptFeatureAccount.CodexHome, AuthFileName);
        var storedFeatureAuthPath = GetStoredDesktopAuthPath(
            sharedHome,
            chatGptFeatureAccount);
        if (!ChatGptAuthAccountsMatch(sharedAuthPath, featureAuthPath) &&
            !ChatGptAuthAccountsMatch(sharedAuthPath, storedFeatureAuthPath))
        {
            return false;
        }

        return IsManagedDesktopModelCredentialBoundToAccount(
            modelAccount,
            Path.Combine(sharedHome, ConfigFileName));
    }

    private static bool CanReuseChatGptFeatureProfileForLaunchWithoutNetwork(
        AccountRecord modelAccount,
        string sourceConfigPath,
        AccountRecord chatGptFeatureAccount)
    {
        ArgumentNullException.ThrowIfNull(modelAccount);
        ArgumentNullException.ThrowIfNull(chatGptFeatureAccount);
        return CanReuseSharedProfileWithoutNetwork(
                   modelAccount,
                   sourceConfigPath,
                   AccessTokenSharedProfileMode.ChatGptDesktop,
                   chatGptFeatureAccount) ||
               CanIdentifySharedChatGptFeatureProfileWithoutNetwork(
                   modelAccount,
                   chatGptFeatureAccount);
    }

    private static bool IsManagedDesktopModelCredentialBoundToAccount(
        AccountRecord account,
        string sharedConfigPath)
    {
        try
        {
            if (account.IsOfficialOAuth || !File.Exists(sharedConfigPath))
            {
                return false;
            }

            var configLines = File.ReadAllText(sharedConfigPath)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
            var providerId = account.IsCompatibleApi
                ? AccountStore.CompatibleApiProviderId
                : AccountStore.AccessTokenProviderId;
            if (!TomlTopLevelRawValueMatches(
                    configLines,
                    "model_provider",
                    TomlString(providerId)) ||
                !TomlTopLevelRawValueMatches(
                    configLines,
                    "cli_auth_credentials_store",
                    "\"file\""))
            {
                return false;
            }

            var providerHeaders = new[]
            {
                "[model_providers." + providerId + "]",
                "[model_providers." + TomlString(providerId) + "]"
            };
            if (!TryFindUniqueTomlSection(
                    configLines,
                    providerHeaders,
                    out var sectionStart,
                    out var sectionEnd))
            {
                return false;
            }

            var sourceToken = ReadAccessTokenCredential(
                Path.Combine(account.CodexHome, AuthFileName));
            var expectedBaseUrl = account.IsCompatibleApi
                ? account.ApiBaseUrl.TrimEnd('/')
                : AccountStore.AccessTokenBaseUrl;
            var expectedWireApi = account.IsCompatibleApi
                ? account.ApiWireApi
                : "responses";
            return TomlSectionRawValueMatches(
                       configLines,
                       sectionStart,
                       sectionEnd,
                       "base_url",
                       TomlString(expectedBaseUrl)) &&
                   TomlSectionRawValueMatches(
                       configLines,
                       sectionStart,
                       sectionEnd,
                       "wire_api",
                       TomlString(expectedWireApi)) &&
                   TomlSectionRawValueMatches(
                       configLines,
                       sectionStart,
                       sectionEnd,
                       "requires_openai_auth",
                       "true") &&
                   TomlSectionRawValueMatches(
                       configLines,
                       sectionStart,
                       sectionEnd,
                       "supports_websockets",
                       "false") &&
                   TomlSectionRawValueMatches(
                       configLines,
                       sectionStart,
                       sectionEnd,
                       "experimental_bearer_token",
                       TomlString(sourceToken),
                       sensitive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or FormatException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TomlTopLevelRawValueMatches(
        IReadOnlyList<string> lines,
        string key,
        string expectedValue)
    {
        var matches = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (IsTomlTableHeader(trimmed))
            {
                break;
            }
            if (!TomlKeyEquals(trimmed, key))
            {
                continue;
            }

            matches++;
            if (!TomlRawValueEquals(trimmed, expectedValue, sensitive: false))
            {
                return false;
            }
        }

        return matches == 1;
    }

    private static bool TryFindUniqueTomlSection(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> expectedHeaders,
        out int sectionStart,
        out int sectionEnd)
    {
        sectionStart = -1;
        sectionEnd = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (!expectedHeaders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (sectionStart >= 0)
            {
                return false;
            }

            sectionStart = index + 1;
        }
        if (sectionStart < 0)
        {
            return false;
        }

        sectionEnd = lines.Count;
        for (var index = sectionStart; index < lines.Count; index++)
        {
            if (IsTomlTableHeader(lines[index].Trim()))
            {
                sectionEnd = index;
                break;
            }
        }
        return true;
    }

    private static bool TomlSectionRawValueMatches(
        IReadOnlyList<string> lines,
        int sectionStart,
        int sectionEnd,
        string key,
        string expectedValue,
        bool sensitive = false)
    {
        var matches = 0;
        for (var index = sectionStart; index < sectionEnd; index++)
        {
            var trimmed = lines[index].Trim();
            if (!TomlKeyEquals(trimmed, key))
            {
                continue;
            }

            matches++;
            if (!TomlRawValueEquals(trimmed, expectedValue, sensitive))
            {
                return false;
            }
        }
        return matches == 1;
    }

    private static bool TomlRawValueEquals(
        string trimmedLine,
        string expectedValue,
        bool sensitive)
    {
        var equalsIndex = trimmedLine.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return false;
        }

        var actualValue = trimmedLine[(equalsIndex + 1)..].Trim();
        return sensitive
            ? SecretValuesEqual(actualValue, expectedValue)
            : actualValue.Equals(expectedValue, StringComparison.Ordinal);
    }

    private static bool CanInitializeEmptySharedProfileWithoutNetwork(
        AccountRecord account,
        string sourceAuthPath,
        string sourceConfigPath)
    {
        var sharedAuthPath = Path.Combine(GetDefaultCodexHome(), AuthFileName);
        if (File.Exists(sharedAuthPath) || !IsLocallyUsableAuthJson(sourceAuthPath))
        {
            return false;
        }

        if (account.IsOfficialOAuth)
        {
            return IsChatGptDesktopAuthJson(sourceAuthPath) && File.Exists(sourceConfigPath);
        }

        return !account.IsCompatibleApi || File.Exists(sourceConfigPath);
    }

    private static bool IsLocallyUsableAuthJson(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.EnumerateObject().Any())
            {
                return false;
            }

            // Canonicalization also rejects ambiguous duplicate object keys.
            _ = BuildCanonicalJsonSha256(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string NormalizeTextForFingerprint(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static async Task<LoginStatus> GetAccessTokenSwitchStatusAsync(AccountRecord account)
    {
        var fingerprint = BuildAccountValidationFingerprint(account);
        lock (AccessTokenSwitchValidationCacheLock)
        {
            if (AccessTokenSwitchValidationCache.TryGetValue(account.CodexHome, out var cached) &&
                cached.Fingerprint.Equals(fingerprint, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - cached.CompletedAtUtc <= AccessTokenSwitchValidationCacheLifetime)
            {
                return cached.Status;
            }
        }

        var status = await new CodexCliService().GetLoginStatusAsync(account);
        if (status.ExitCode == 0)
        {
            CacheAccessTokenSwitchValidation(account, status, fingerprint);
        }
        return status;
    }

    private static void CacheAccessTokenSwitchValidation(
        AccountRecord account,
        LoginStatus status,
        string? fingerprint = null)
    {
        lock (AccessTokenSwitchValidationCacheLock)
        {
            AccessTokenSwitchValidationCache[account.CodexHome] =
                new AccessTokenSwitchValidationCacheEntry(
                    fingerprint ?? BuildAccountValidationFingerprint(account),
                    DateTimeOffset.UtcNow,
                    status);
        }
    }

    private static WindowsClientAccountProjection ProjectWindowsClientAccount(
        AccountRecord account,
        LoginStatus status,
        AccessTokenSharedProfileMode accessTokenMode,
        AccountRecord? chatGptFeatureAccount = null,
        bool routeOfficialOAuthThroughGateway = false)
    {
        return account.IsOfficialOAuth
            ? ProjectOfficialOAuthAccount(account, status, routeOfficialOAuthThroughGateway)
            : account.IsCompatibleApi
            ? ProjectCompatibleApiAccount(account, status, accessTokenMode, chatGptFeatureAccount)
            : ProjectAccessTokenAccount(account, status, accessTokenMode, chatGptFeatureAccount);
    }

    private static WindowsClientAccountProjection ProjectAccessTokenAccount(
        AccountRecord account,
        LoginStatus status,
        AccessTokenSharedProfileMode mode,
        AccountRecord? chatGptFeatureAccount = null)
    {
        return ProjectSharedAccountProfile(account, status, mode, chatGptFeatureAccount);
    }

    private static WindowsClientAccountProjection ProjectCompatibleApiAccount(
        AccountRecord account,
        LoginStatus status,
        AccessTokenSharedProfileMode mode = AccessTokenSharedProfileMode.ApiCompatible,
        AccountRecord? chatGptFeatureAccount = null)
    {
        return ProjectSharedAccountProfile(account, status, mode, chatGptFeatureAccount);
    }

    private static bool CanReuseOfficialOAuthSharedProfile(
        AccountRecord account,
        bool routeThroughGateway = false)
    {
        var profileHome = Path.GetFullPath(GetDefaultCodexHome());
        var targetServiceTier = ReadAccountServiceTier(account.CodexHome);
        var sharedAuthPath = Path.Combine(profileHome, AuthFileName);
        var accountAuthPath = Path.Combine(account.CodexHome, AuthFileName);
        var storedAuthPath = GetStoredDesktopAuthPath(profileHome, account);
        var sharedConfigPath = Path.Combine(profileHome, ConfigFileName);
        if (!IsDesktopSelectionForAccount(profileHome, account) ||
            !IsDesktopAuthSelectionForAccount(profileHome, account) ||
            !IsChatGptDesktopAuthJson(sharedAuthPath) ||
            (!ChatGptAuthAccountsMatch(sharedAuthPath, accountAuthPath) &&
             !ChatGptAuthAccountsMatch(sharedAuthPath, storedAuthPath)) ||
            !File.Exists(sharedConfigPath) ||
            File.Exists(Path.Combine(profileHome, CockpitAuthFileName)))
        {
            return false;
        }

        try
        {
            var current = File.ReadAllText(sharedConfigPath);
            return string.Equals(
                current,
                ProjectOfficialOAuthConfigText(
                    current,
                    targetServiceTier,
                    routeThroughGateway),
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or FormatException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static WindowsClientAccountProjection ProjectOfficialOAuthAccount(
        AccountRecord account,
        LoginStatus status,
        bool routeThroughGateway = false)
    {
        var accountHome = Path.GetFullPath(account.CodexHome);
        var profileHome = Path.GetFullPath(GetDefaultCodexHome());
        Directory.CreateDirectory(accountHome);
        Directory.CreateDirectory(profileHome);

        var accountAuthPath = Path.Combine(accountHome, AuthFileName);
        var sharedAuthPath = Path.Combine(profileHome, AuthFileName);
        var cockpitAuthPath = Path.Combine(profileHome, CockpitAuthFileName);
        var sharedConfigPath = Path.Combine(profileHome, ConfigFileName);
        var selectionPath = Path.Combine(profileHome, DesktopSelectionFileName);
        var activeAccountStatePath = Path.Combine(profileHome, ActiveAccountStateFileName);

        // Before replacing the shared profile, preserve a refresh-token rotation made by
        // the official App for the account that is currently selected. The snapshot is
        // keyed only by that account's CODEX_HOME hash; OAuth accounts never use a global
        // or "newest available" fallback because that could cross account boundaries.
        if (!IsDesktopAuthSelectionForAccount(profileHome, account))
        {
            PersistSelectedDesktopChatGptAuth(profileHome, sharedAuthPath);
        }
        SyncStoredOfficialOAuthAuthToAccount(account);
        var sourceAuthPath = GetPreferredOfficialOAuthAuthPath(profileHome, account);
        var authFileReused = IsDesktopSelectionForAccount(profileHome, account) &&
                             IsDesktopAuthSelectionForAccount(profileHome, account) &&
                             IsChatGptDesktopAuthJson(sharedAuthPath);
        var sharedProfileReused = CanReuseOfficialOAuthSharedProfile(
            account,
            routeThroughGateway);
        var backupDirectory = CreateBackupDirectory(profileHome);
        var authExisted = File.Exists(sharedAuthPath);
        var cockpitAuthExisted = File.Exists(cockpitAuthPath);
        var configExisted = File.Exists(sharedConfigPath);
        var selectionExisted = File.Exists(selectionPath);
        var activeAccountStateExisted = File.Exists(activeAccountStatePath);
        var authBackupPath = authFileReused
            ? null
            : BackupFileIfPresent(sharedAuthPath, backupDirectory);
        var cockpitBackupPath = BackupFileIfPresent(cockpitAuthPath, backupDirectory);
        var configBackupPath = BackupFileIfPresent(sharedConfigPath, backupDirectory);
        var selectionBackupPath = BackupFileIfPresent(selectionPath, backupDirectory);
        var activeAccountStateBackupPath = BackupFileIfPresent(
            activeAccountStatePath,
            backupDirectory);

        try
        {
            var currentConfig = File.Exists(sharedConfigPath)
                ? File.ReadAllText(sharedConfigPath)
                : "";
            var projectedConfig = ProjectOfficialOAuthConfigText(
                currentConfig,
                ReadAccountServiceTier(accountHome),
                routeThroughGateway);
            if (!string.Equals(currentConfig, projectedConfig, StringComparison.Ordinal))
            {
                WriteTextAtomically(sharedConfigPath, projectedConfig);
            }

            if (!authFileReused)
            {
                if (PathsEqual(sourceAuthPath, sharedAuthPath))
                {
                    throw new InvalidOperationException(
                        "ChatGPT 登录账号目录不能与共享 Codex 目录相同；请为每个账号使用独立目录。"
                    );
                }
                CopyFileAtomically(sourceAuthPath, sharedAuthPath);
            }

            if (!IsChatGptDesktopAuthJson(sharedAuthPath))
            {
                throw new InvalidDataException(
                    $"账号 {account.Name} 没有可投放到 ChatGPT App 的官方登录凭据。"
                );
            }

            if (File.Exists(cockpitAuthPath))
            {
                ClearReadOnlyAttribute(cockpitAuthPath);
                File.Delete(cockpitAuthPath);
            }

            WriteDesktopSelection(profileHome, account);
            WriteActiveAccountState(
                profileHome,
                account,
                AccessTokenSharedProfileMode.ChatGptDesktop);
        }
        catch
        {
            RestoreFile(sharedAuthPath, authBackupPath, authExisted);
            RestoreFile(cockpitAuthPath, cockpitBackupPath, cockpitAuthExisted);
            RestoreFile(sharedConfigPath, configBackupPath, configExisted);
            RestoreFile(selectionPath, selectionBackupPath, selectionExisted);
            RestoreFile(
                activeAccountStatePath,
                activeAccountStateBackupPath,
                activeAccountStateExisted);
            throw;
        }

        return new WindowsClientAccountProjection
        {
            Status = status,
            DefaultCodexHome = profileHome,
            AccountCodexHome = accountHome,
            BackupDirectory = backupDirectory,
            AuthBackupPath = authBackupPath,
            CockpitAuthBackupPath = cockpitBackupPath,
            ConfigBackupPath = configBackupPath,
            DesktopSelectionBackupPath = selectionBackupPath,
            ActiveAccountStateBackupPath = activeAccountStateBackupPath,
            AuthExisted = authExisted,
            CockpitAuthExisted = cockpitAuthExisted,
            ConfigExisted = configExisted,
            DesktopSelectionExisted = selectionExisted,
            ActiveAccountStateExisted = activeAccountStateExisted,
            SharedCredentialsReused = sharedProfileReused,
            ProfileChanged = !sharedProfileReused,
            DesktopLoginRequired = false
        };
    }

    private static WindowsClientAccountProjection CreateReusedSharedProfileProjection(
        AccountRecord account,
        LoginStatus status,
        AccessTokenSharedProfileMode mode = AccessTokenSharedProfileMode.ChatGptDesktop,
        AccountRecord? chatGptFeatureAccount = null)
    {
        var accountHome = Path.GetFullPath(account.CodexHome);
        var profileHome = Path.GetFullPath(GetDefaultCodexHome());
        if (account.IsOfficialOAuth)
        {
            SyncStoredOfficialOAuthAuthToAccount(account);
        }
        else if (mode == AccessTokenSharedProfileMode.ChatGptDesktop)
        {
            var authPath = Path.Combine(profileHome, AuthFileName);
            // A login can complete inside the official App after the previous switch. Keep
            // the snapshot fresh even when the next launch reuses the same projected profile.
            PersistSelectedDesktopChatGptAuth(profileHome, authPath);
            if (chatGptFeatureAccount != null)
            {
                SyncStoredOfficialOAuthAuthToAccount(chatGptFeatureAccount);
            }
            PersistGlobalDesktopChatGptAuth(profileHome, authPath);
        }
        WriteActiveAccountState(profileHome, account, mode);
        return new WindowsClientAccountProjection
        {
            Status = status,
            DefaultCodexHome = profileHome,
            AccountCodexHome = accountHome,
            BackupDirectory = "",
            AuthExisted = File.Exists(Path.Combine(profileHome, AuthFileName)),
            CockpitAuthExisted = File.Exists(Path.Combine(profileHome, CockpitAuthFileName)),
            ConfigExisted = File.Exists(Path.Combine(profileHome, ConfigFileName)),
            DesktopSelectionExisted = File.Exists(Path.Combine(profileHome, DesktopSelectionFileName)),
            SharedCredentialsReused = true,
            ProfileChanged = false,
            DesktopLoginRequired =
                !account.IsOfficialOAuth &&
                mode == AccessTokenSharedProfileMode.ChatGptDesktop &&
                chatGptFeatureAccount == null &&
                !IsChatGptDesktopAuthJson(Path.Combine(profileHome, AuthFileName))
        };
    }

    private static WindowsClientAccountProjection ProjectSharedAccountProfile(
        AccountRecord account,
        LoginStatus status,
        AccessTokenSharedProfileMode accessTokenMode,
        AccountRecord? chatGptFeatureAccount = null)
    {
        var accountHome = Path.GetFullPath(account.CodexHome);
        var profileHome = Path.GetFullPath(GetDefaultCodexHome());
        Directory.CreateDirectory(accountHome);
        Directory.CreateDirectory(profileHome);

        var sourceAuthPath = Path.Combine(accountHome, AuthFileName);
        var sourceConfigPath = Path.Combine(accountHome, ConfigFileName);
        var authPath = Path.Combine(profileHome, AuthFileName);
        var cockpitAuthPath = Path.Combine(profileHome, CockpitAuthFileName);
        var configPath = Path.Combine(profileHome, ConfigFileName);
        var selectionPath = Path.Combine(profileHome, DesktopSelectionFileName);
        var activeAccountStatePath = Path.Combine(profileHome, ActiveAccountStateFileName);
        var useChatGptDesktopAuth =
            accessTokenMode == AccessTokenSharedProfileMode.ChatGptDesktop;
        if (useChatGptDesktopAuth &&
            chatGptFeatureAccount != null &&
            !chatGptFeatureAccount.IsOfficialOAuth)
        {
            throw new InvalidOperationException(
                "ChatGPT 功能身份必须来自“通过 ChatGPT 登录（官方）”账号。");
        }

        PersistSelectedDesktopChatGptAuth(profileHome, authPath);
        PersistGlobalDesktopChatGptAuth(profileHome, authPath);
        if (chatGptFeatureAccount != null)
        {
            SyncStoredOfficialOAuthAuthToAccount(chatGptFeatureAccount);
        }
        var explicitFeatureAuthPath = chatGptFeatureAccount == null
            ? null
            : Path.Combine(chatGptFeatureAccount.CodexHome, AuthFileName);
        var currentDesktopAuthAvailable =
            useChatGptDesktopAuth &&
            IsChatGptDesktopAuthJson(authPath) &&
            (chatGptFeatureAccount == null ||
             IsDesktopAuthSelectionForAccount(profileHome, chatGptFeatureAccount) &&
             explicitFeatureAuthPath != null &&
             ChatGptAuthAccountsMatch(authPath, explicitFeatureAuthPath));
        var authFileReused = useChatGptDesktopAuth
            ? IsAccessTokenDesktopSessionSelected(account, profileHome, authPath) &&
              (chatGptFeatureAccount == null ||
               IsDesktopAuthSelectionForAccount(profileHome, chatGptFeatureAccount) &&
               explicitFeatureAuthPath != null &&
               ChatGptAuthAccountsMatch(authPath, explicitFeatureAuthPath))
            : IsAccessTokenDesktopAuthSelected(sourceAuthPath, authPath);
        var sharedProfileReused = CanReuseSharedProfileWithoutNetwork(
            account,
            sourceConfigPath,
            accessTokenMode,
            chatGptFeatureAccount);
        var backupDirectory = CreateBackupDirectory(profileHome);
        var authExisted = File.Exists(authPath);
        var cockpitAuthExisted = File.Exists(cockpitAuthPath);
        var configExisted = File.Exists(configPath);
        var selectionExisted = File.Exists(selectionPath);
        var activeAccountStateExisted = File.Exists(activeAccountStatePath);
        var authBackupPath = authFileReused
            ? null
            : BackupFileIfPresent(authPath, backupDirectory);
        var cockpitBackupPath = BackupFileIfPresent(cockpitAuthPath, backupDirectory);
        var configBackupPath = BackupFileIfPresent(configPath, backupDirectory);
        var selectionBackupPath = BackupFileIfPresent(selectionPath, backupDirectory);
        var activeAccountStateBackupPath = BackupFileIfPresent(
            activeAccountStatePath,
            backupDirectory);

        try
        {
            var sourceConfig = File.Exists(sourceConfigPath)
                ? File.ReadAllText(sourceConfigPath)
                : "";
            if (account.IsCompatibleApi)
            {
                ProjectCompatibleApiConfig(sourceConfigPath, account);
                sourceConfig = File.ReadAllText(sourceConfigPath);
                var projectedConfig = ProjectCompatibleApiConfigText(
                    sourceConfig,
                    account,
                    requiresOpenAiAuth: true,
                    providerBearerToken: useChatGptDesktopAuth
                        ? ReadAccessTokenCredential(sourceAuthPath)
                        : null,
                    forceFileAuthStore: true,
                    serviceTier: ReadDesktopServiceTier(sourceConfig));
                var currentConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
                projectedConfig = PreserveSharedMcpServerSections(currentConfig, projectedConfig);
                if (!string.Equals(currentConfig, projectedConfig, StringComparison.Ordinal))
                {
                    WriteTextAtomically(configPath, projectedConfig);
                }
            }
            else
            {
                ProjectAccessTokenSourceConfig(sourceConfigPath);
                sourceConfig = File.ReadAllText(sourceConfigPath);
                var projectedConfig = useChatGptDesktopAuth
                    ? ProjectWindowsClientConfigText(
                        sourceConfig,
                        requiresOpenAiAuth: true,
                        desktopProviderName: account.Name,
                        providerBearerToken: ReadAccessTokenCredential(sourceAuthPath),
                        forceFileAuthStore: true,
                        serviceTier: ReadDesktopServiceTier(sourceConfig))
                    : ProjectWindowsClientConfigText(
                        sourceConfig,
                        requiresOpenAiAuth: true,
                        desktopProviderName: account.Name,
                        forceFileAuthStore: true,
                        serviceTier: ReadDesktopServiceTier(sourceConfig));
                var currentConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
                projectedConfig = PreserveSharedMcpServerSections(currentConfig, projectedConfig);
                if (!string.Equals(currentConfig, projectedConfig, StringComparison.Ordinal))
                {
                    WriteTextAtomically(configPath, projectedConfig);
                }
            }

            if (!authFileReused && useChatGptDesktopAuth)
            {
                var storedDesktopAuthPath = chatGptFeatureAccount != null
                    ? GetPreferredOfficialOAuthAuthPath(profileHome, chatGptFeatureAccount)
                    : FindRestorableDesktopAuthPath(
                        profileHome,
                        account,
                        authPath,
                        currentDesktopAuthAvailable);
                if (storedDesktopAuthPath != null &&
                    !PathsEqual(storedDesktopAuthPath, authPath))
                {
                    CopyFileAtomically(storedDesktopAuthPath, authPath);
                }
                else if (storedDesktopAuthPath == null)
                {
                    ClearReadOnlyAttribute(authPath);
                    if (File.Exists(authPath))
                    {
                        File.Delete(authPath);
                    }
                }

                if (chatGptFeatureAccount != null && !IsChatGptDesktopAuthJson(authPath))
                {
                    throw new InvalidDataException(
                        $"账号 {chatGptFeatureAccount.Name} 的 ChatGPT 官方登录态无法投放到 Codex。");
                }
            }
            else if (!authFileReused && account.IsCompatibleApi && !PathsEqual(sourceAuthPath, authPath))
            {
                WriteTextAtomically(authPath, BuildAccessTokenDesktopAuthText(sourceAuthPath));
            }
            else if (!authFileReused && account.IsAccessToken)
            {
                if (PathsEqual(sourceAuthPath, authPath))
                {
                    throw new InvalidOperationException(
                        "Access Token 账号目录不能与共享 Codex 目录相同，否则无法为桌面端创建隔离凭据投影。");
                }

                WriteTextAtomically(authPath, BuildAccessTokenDesktopAuthText(sourceAuthPath));
            }

            if (File.Exists(cockpitAuthPath))
            {
                ClearReadOnlyAttribute(cockpitAuthPath);
                File.Delete(cockpitAuthPath);
            }

            if (useChatGptDesktopAuth)
            {
                WriteDesktopSelection(profileHome, account, chatGptFeatureAccount);
            }
            else if (File.Exists(selectionPath))
            {
                ClearReadOnlyAttribute(selectionPath);
                File.Delete(selectionPath);
            }

            WriteActiveAccountState(profileHome, account, accessTokenMode);
        }
        catch
        {
            RestoreFile(authPath, authBackupPath, authExisted);
            RestoreFile(cockpitAuthPath, cockpitBackupPath, cockpitAuthExisted);
            RestoreFile(configPath, configBackupPath, configExisted);
            RestoreFile(selectionPath, selectionBackupPath, selectionExisted);
            RestoreFile(
                activeAccountStatePath,
                activeAccountStateBackupPath,
                activeAccountStateExisted);
            throw;
        }

        return new WindowsClientAccountProjection
        {
            Status = status,
            DefaultCodexHome = profileHome,
            AccountCodexHome = accountHome,
            BackupDirectory = backupDirectory,
            AuthBackupPath = authBackupPath,
            CockpitAuthBackupPath = cockpitBackupPath,
            ConfigBackupPath = configBackupPath,
            DesktopSelectionBackupPath = selectionBackupPath,
            ActiveAccountStateBackupPath = activeAccountStateBackupPath,
            AuthExisted = authExisted,
            CockpitAuthExisted = cockpitAuthExisted,
            ConfigExisted = configExisted,
            DesktopSelectionExisted = selectionExisted,
            ActiveAccountStateExisted = activeAccountStateExisted,
            SharedCredentialsReused = sharedProfileReused,
            ProfileChanged = !sharedProfileReused,
            DesktopLoginRequired = useChatGptDesktopAuth &&
                                   chatGptFeatureAccount == null &&
                                   !IsChatGptDesktopAuthJson(authPath)
        };
    }

    public void LaunchPowerShell(AccountRecord account, string projectPath, string codexHome)
    {
        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException($"项目目录不存在：{projectPath}");
        }
        if (account.IsAccessToken)
        {
            LocalPatGateway.EnsureRunning();
        }

        var scriptLines = new List<string>
        {
            "$env:CODEX_HOME = " + ToSingleQuoted(codexHome),
            "$env:CODEX_SQLITE_HOME = " + ToSingleQuoted(codexHome),
            "Set-Location -LiteralPath " + ToSingleQuoted(projectPath),
            "$Host.UI.RawUI.WindowTitle = 'Codex CLI - ' + $env:CODEX_HOME",
            "Write-Host ''",
            "Write-Host ('CODEX_HOME = ' + $env:CODEX_HOME) -ForegroundColor Green",
            "Write-Host ('Current folder = ' + (Get-Location).Path)",
            "Write-Host ''",
            "Write-Host " + ToSingleQuoted($"账号 {account.Name} 已切换到共享聊天库。需要启动时输入：codex -C .")
        };

        var proxyUri = GetConfiguredProxyUri();
        if (!string.IsNullOrWhiteSpace(proxyUri))
        {
            scriptLines.Insert(1, "$env:HTTP_PROXY = " + ToSingleQuoted(proxyUri));
            scriptLines.Insert(2, "$env:HTTPS_PROXY = " + ToSingleQuoted(proxyUri));
            scriptLines.Insert(3, "$env:ALL_PROXY = " + ToSingleQuoted(proxyUri));
            scriptLines.Insert(4, "$env:http_proxy = " + ToSingleQuoted(proxyUri));
            scriptLines.Insert(5, "$env:https_proxy = " + ToSingleQuoted(proxyUri));
            scriptLines.Insert(6, "$env:all_proxy = " + ToSingleQuoted(proxyUri));
            scriptLines.Insert(7, "$env:NO_PROXY = '127.0.0.1,localhost,::1'");
            scriptLines.Insert(8, "$env:no_proxy = '127.0.0.1,localhost,::1'");
        }

        var script = string.Join("; ", scriptLines);

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = projectPath,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("-NoExit");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        Process.Start(startInfo);
    }

    public SharedHistoryMergeResult MergeSharedHistory(IEnumerable<AccountRecord> accounts)
    {
        return SharedHistoryMerger.Merge(
            accounts.Select(account => account.CodexHome),
            GetDefaultCodexHome());
    }

    public bool LaunchWindowsClient(AccountRecord account, string projectPath, string codexHome)
    {
        return LaunchWindowsClient(
            account,
            projectPath,
            codexHome,
            WindowsClientMode.CodexPlusPlus);
    }

    public bool LaunchWindowsClient(
        AccountRecord account,
        string projectPath,
        string codexHome,
        WindowsClientMode mode)
    {
        return LaunchWindowsClient(
            account,
            projectPath,
            codexHome,
            mode,
            Array.Empty<WindowsClientProcessSnapshot>(),
            switchRequired: false,
            useDreamSkin: false,
            appearanceMode: ThemeMode.System,
            appearancePresetId: "manager",
            appearanceLabel: null,
            allowOfficialRendererPatch: true);
    }

    private bool LaunchWindowsClient(
        AccountRecord account,
        string projectPath,
        string codexHome,
        WindowsClientMode mode,
        IReadOnlyList<WindowsClientProcessSnapshot> shutdownTargets,
        bool switchRequired,
        bool useDreamSkin,
        ThemeMode appearanceMode,
        string appearancePresetId,
        string? appearanceLabel,
        bool allowOfficialRendererPatch,
        long? expectedLaunchGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException($"Project path does not exist: {projectPath}");
        }

        codexHome = Path.GetFullPath(codexHome);
        if (!Directory.Exists(codexHome))
        {
            throw new DirectoryNotFoundException($"Shared CODEX_HOME does not exist: {codexHome}");
        }

        SanitizeCuratedPluginManifests(codexHome);
        var launchGeneration = expectedLaunchGeneration ?? BeginWindowsClientLaunchGeneration();
        var hasExistingOfficialWindow =
            mode == WindowsClientMode.OfficialCodex &&
            HasWindowsClientMainWindowSince(DateTime.MinValue);
        if (ShouldPreserveExistingOfficialWindow(
                switchRequired,
                mode,
                useDreamSkin,
                hasExistingOfficialWindow,
                allowOfficialRendererPatch))
        {
            // A same-profile dual-login/OAuth click is an activation request, never a
            // recovery transaction. Runtime health can be transiently false while a turn
            // is busy, IPC is back-pressured, or the app-server is rotating state. Closing
            // that visible verified Codex window would kill the in-flight task. Preserve it
            // unconditionally; a best-effort deep link may focus/open the requested project
            // but its failure must not mutate the existing process tree.
            try
            {
                Process.Start(new ProcessStartInfo(BuildNewThreadDeepLink(projectPath))
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-project-deep-link-unavailable",
                    MaskSensitive(ex.Message));
            }
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-same-profile-window-preserved",
                "renderer patch and runtime-health shutdown were disabled for the existing dual-login/OAuth window");
            return true;
        }

        if (!switchRequired &&
            mode == WindowsClientMode.OfficialCodex &&
            !useDreamSkin &&
            hasExistingOfficialWindow)
        {
            var existingOfficialClientHealthy =
                IsWindowsClientRuntimeHealthySince(DateTime.MinValue);
            if (existingOfficialClientHealthy &&
                TryAttachNativeFastBridgeToExistingOfficialCodex())
            {
                Process.Start(new ProcessStartInfo(BuildNewThreadDeepLink(projectPath))
                {
                    UseShellExecute = true
                });
                return true;
            }

            // This branch is reached only from an explicit "start account" operation. A
            // healthy same-account client is preserved when its listener, owning packaged
            // process, browser identity and reviewed app://codex target all validate live.
            // Otherwise Electron cannot enable CDP after startup, so one controlled restart is
            // required to make the native Standard/Fast picker available. A merely visible or
            // spoofed shell is never reused.
            var sameAccountShutdownTargets = CaptureWindowsClientProcessSnapshots();
            var sameAccountNativeFastPorts = CaptureOfficialNativeFastPortsOwnedBy(
                sameAccountShutdownTargets);
            StopWindowsClientProcesses(sameAccountShutdownTargets);
            if (!WaitForWindowsClientProcessAndPortRelease(
                    sameAccountShutdownTargets,
                    sameAccountNativeFastPorts,
                    launchGeneration,
                    OfficialCodexRecoveryReleaseTimeout) ||
                !WaitForOfficialCodexSwitchQuiescence(
                    launchGeneration,
                    OfficialCodexRecoveryReleaseTimeout))
            {
                throw new TimeoutException(
                    "The existing Codex process did not remain globally closed, or its native bridge port did not release cleanly; " +
                    "a replacement official client was not activated.");
            }
        }

        return mode switch
        {
            WindowsClientMode.CodexPlusPlus => LaunchCodexPlusPlus(
                projectPath,
                codexHome,
                shutdownTargets,
                switchRequired,
                launchGeneration),
            WindowsClientMode.OfficialCodex => LaunchOfficialCodex(
                projectPath,
                useDreamSkin,
                appearanceMode,
                appearancePresetId,
                appearanceLabel,
                allowOfficialRendererPatch,
                launchGeneration),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Windows client mode.")
        };
    }

    private static bool ShouldPreserveExistingOfficialWindow(
        bool switchRequired,
        WindowsClientMode mode,
        bool useDreamSkin,
        bool hasExistingOfficialWindow,
        bool allowOfficialRendererPatch)
    {
        return !switchRequired &&
               mode == WindowsClientMode.OfficialCodex &&
               !useDreamSkin &&
               hasExistingOfficialWindow &&
               !allowOfficialRendererPatch;
    }

    private static OfficialCodexStartupReadinessPolicy SelectOfficialCodexStartupReadinessPolicy(
        bool allowRendererPatch)
    {
        // A controlled renderer reload needs a synchronous routes + IPC contract because the
        // manager itself caused that reload. Official OAuth and explicit dual-login launches do
        // not patch/reload the renderer; a delayed log line there must never become permission to
        // kill a visible client and restart it. Their readiness is observed in the background.
        return allowRendererPatch
            ? OfficialCodexStartupReadinessPolicy.VerifySynchronouslyForRendererPatch
            : OfficialCodexStartupReadinessPolicy.PreserveActivationAndObserveInBackground;
    }

    private bool LaunchCodexPlusPlus(
        string projectPath,
        string codexHome,
        IReadOnlyList<WindowsClientProcessSnapshot> shutdownTargets,
        bool switchRequired,
        long launchGeneration)
    {
        // Apply the Codex++ service-tier control preference before the ready fast path too.
        // Existing Codex++ processes otherwise skip the settings sync and can keep the picker
        // hidden after an older manager build wrote `codexAppServiceTierControls=false`.
        var clientPathForInjection = ResolveCodexWindowsClientPath();
        var clientAppDir = string.IsNullOrWhiteSpace(clientPathForInjection)
            ? null
            : Path.GetDirectoryName(clientPathForInjection);
        var serviceTierControlsChanged = EnsureCodexPlusPlusSafeSettings(clientAppDir);

        if (IsCodexPlusPlusReady() && !serviceTierControlsChanged)
        {
            Process.Start(new ProcessStartInfo(BuildNewThreadDeepLink(projectPath))
            {
                UseShellExecute = true
            });
            return true;
        }
        if (serviceTierControlsChanged && IsCodexPlusPlusReady())
        {
            // Codex++ loads backend settings only during startup. This one-time restart is
            // required when upgrading from an older manager that persisted the control as off.
            StopWindowsClientProcesses(CaptureWindowsClientProcessSnapshots());
        }

        var codexPlusPlusPath = ResolveCodexPlusPlusLauncherPath();
        if (!string.IsNullOrWhiteSpace(codexPlusPlusPath))
        {
            // Codex++ 1.2.34 does not reliably forward the launcher's --app-path to its
            // manager.  Persisting the already resolved package app directory avoids a slow
            // Get-AppxPackage discovery path that has taken 52-66 seconds on this machine.
            var launchStartedUtc = DateTime.UtcNow;
            if (TryLaunchCodexPlusPlusViaScheduledTask(
                    codexPlusPlusPath,
                    clientAppDir,
                    projectPath,
                    codexHome,
                    shutdownTargets,
                    switchRequired))
            {
                // The elevated helper has accepted the launch and already captured immediate
                // failures.  Bridge injection can take much longer than opening the app; keep
                // that readiness/deep-link step in the background instead of blocking this
                // button for one or two minutes.
                OpenNewTaskAfterCodexPlusPlusLaunchInBackground(
                    projectPath,
                    launchStartedUtc,
                    launchGeneration);
                return true;
            }

            var startInfo = new ProcessStartInfo(codexPlusPlusPath)
            {
                WorkingDirectory = projectPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!string.IsNullOrWhiteSpace(clientAppDir))
            {
                startInfo.ArgumentList.Add("--app-path");
                startInfo.ArgumentList.Add(clientAppDir);
            }

            startInfo.Environment["CODEX_HOME"] = codexHome;
            startInfo.Environment["CODEX_SQLITE_HOME"] = codexHome;
            startInfo.Environment["CODEX_PROJECT_PATH"] = projectPath;
            ApplyProxyEnvironment(startInfo);
            try
            {
                using var launcherProcess = Process.Start(startInfo) ??
                    throw new InvalidOperationException("Codex++ 启动器没有返回进程句柄。");
                _ = launcherProcess.StandardOutput.ReadToEndAsync();
                var launcherStandardError = launcherProcess.StandardError.ReadToEndAsync();
                var exitedDuringProbe = launcherProcess.WaitForExit(
                    (int)CodexPlusPlusLauncherExitProbeTimeout.TotalMilliseconds);
                if (exitedDuringProbe && launcherProcess.ExitCode != 0)
                {
                    var error = launcherStandardError.IsCompletedSuccessfully
                        ? MaskSensitive(launcherStandardError.Result).Trim()
                        : "";
                    throw new InvalidOperationException(
                        $"Codex++ 启动失败（启动器退出代码 {launcherProcess.ExitCode}）。" +
                        (string.IsNullOrWhiteSpace(error) ? "" : "\n" + error));
                }
                OpenNewTaskAfterCodexPlusPlusLaunchInBackground(
                    projectPath,
                    launchStartedUtc,
                    launchGeneration);
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                throw new InvalidOperationException(
                    "Codex++ 需要管理员权限，但普通账号启动不会隐式打开管理员 PowerShell。" +
                    "请先显式执行一次“修复 Codex++ 隐藏启动任务”，或把启动方式改为官方 Codex。",
                    ex);
            }
        }

        throw new InvalidOperationException(
            "找不到已安装的 Codex++。为确保聊天记录管理增强功能可用，本次没有绕过 Codex++ 直接启动 Codex。");
    }

    private static bool LaunchOfficialCodex(
        string projectPath,
        bool useDreamSkin,
        ThemeMode appearanceMode,
        string appearancePresetId,
        string? appearanceLabel,
        bool allowRendererPatch,
        long launchGeneration)
    {
        var clientPath = ResolveCodexWindowsClientPath();
        if (string.IsNullOrWhiteSpace(clientPath) || !File.Exists(clientPath))
        {
            throw new FileNotFoundException("找不到已安装的官方 Codex Windows 客户端。", clientPath);
        }

        if (useDreamSkin)
        {
            ApplyAndStartDreamSkinOrRestore(
                appearanceMode,
                appearancePresetId,
                appearanceLabel,
                projectPath);
            if (!TryAttachNativeFastBridgeToExistingOfficialCodex(
                    DreamSkinCdpPortCandidates(),
                    out var dreamSkinFastReady) ||
                !dreamSkinFastReady)
            {
                WriteCodexPlusPlusLaunchDiagnostic(
                    "dream-skin-native-fast-unavailable",
                    "no verified Dream Skin Codex renderer accepted the native Fast bridge");
            }
            // Start() has already opened a verified Codex process. The deep link only selects
            // the requested project/task, so its failure must not report the successful theme
            // activation as a failed client launch.
            TryLaunchOfficialCodexFallback(projectPath, launchGeneration);
            return true;
        }

        // The native Fast bridge performs one controlled renderer reload.  The unmodified
        // primary page must finish its renderer-to-main ready handshake before that reload, and
        // the patched page must complete the same handshake again afterwards.  Treating the
        // bridge's script-verification event as application readiness can strand Electron on a
        // white shell while persisted atoms are still synchronizing.
        var launchStartedUtc = DateTime.UtcNow;
        var launchLogBaseline = CaptureOfficialCodexLaunchLogBaseline();
        WindowsClientActivationIdentity activationIdentity;
        int? nativeFastPort = null;
        try
        {
            try
            {
                nativeFastPort = SelectOfficialNativeFastCdpPort();
                activationIdentity = ActivateOfficialCodexPackage(nativeFastPort.Value);
            }
            catch (OfficialCodexActivationIdentityException)
            {
                // COM returned a PID but Windows never exposed an immutable start time, and the
                // exact activation cannot participate in PID/start-time recovery. Even if its
                // held root handle was terminated, helpers are not proven globally quiescent, so
                // a second package activation would risk attaching to an orphaned shell.
                throw;
            }
            catch (Exception nativeFastError)
            {
                // Native Fast is additive.  If CDP activation itself is unavailable, open the
                // signed client without debugging and still require its primary ready handshake.
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-native-fast-unavailable",
                    MaskSensitive(nativeFastError.Message));
                nativeFastPort = null;
                activationIdentity = ActivateOfficialCodexPackage();
            }
        }
        catch (Exception packageActivationError)
        {
            // A codex:// launch does not return an immutable PID/start-time identity and delivers
            // the project deep link before the primary renderer is ready. Reporting that shell
            // hand-off as success would bypass both readiness and clean recovery. Fail closed so
            // the UI reports the activation error without leaving an untracked white shell.
            throw new InvalidOperationException(
                "Official Codex package activation failed before a verifiable client process was created.",
                packageActivationError);
        }

        if (SelectOfficialCodexStartupReadinessPolicy(allowRendererPatch) ==
            OfficialCodexStartupReadinessPolicy.PreserveActivationAndObserveInBackground)
        {
            // The COM activation already returned an immutable PID/start-time identity. For
            // OAuth/dual-login this is the commit point: do not reinterpret a slow renderer log
            // or busy IPC queue as a crash and destructively recycle the process. The existing
            // background observer will deliver the optional project deep link only after the
            // runtime becomes healthy, and will merely log a timeout without stopping Codex.
            if (!IsWindowsClientActivationIdentityAlive(activationIdentity))
            {
                throw new InvalidOperationException(
                    "Official Codex exited immediately after package activation.");
            }

            OpenNewTaskAfterOfficialCodexLaunchInBackground(
                projectPath,
                launchStartedUtc,
                activationIdentity,
                launchGeneration);
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-non-destructive-readiness-observer-started",
                $"pid={activationIdentity.ProcessId}; patch=false; restart_on_ready_timeout=false");
            return true;
        }

        var observedFirstAttemptProcessTree = new List<WindowsClientProcessSnapshot>();
        RememberWindowsClientProcessTree(
            activationIdentity,
            observedFirstAttemptProcessTree);
        var firstAttempt = CompleteOfficialCodexLaunchAttempt(
            projectPath,
            launchLogBaseline,
            activationIdentity,
            nativeFastPort,
            allowRendererPatch,
            launchGeneration,
            observedFirstAttemptProcessTree);
        if (firstAttempt is OfficialCodexLaunchAttemptOutcome.Ready or
            OfficialCodexLaunchAttemptOutcome.ReadyWithoutRendererPatch)
        {
            return true;
        }
        if (firstAttempt == OfficialCodexLaunchAttemptOutcome.Superseded)
        {
            return false;
        }

        if (DecideOfficialCodexRecovery(firstAttempt, recoveryAlreadyAttempted: false) !=
            OfficialCodexRecoveryDecision.RestartWithoutRendererPatch ||
            !TryRecoverOfficialCodexLaunchWithoutRendererPatch(
                projectPath,
                activationIdentity,
                nativeFastPort,
                launchGeneration,
                observedFirstAttemptProcessTree))
        {
            throw new TimeoutException(
                "Official Codex did not finish mounting its primary page. " +
                "One clean restart without the renderer patch was attempted or could not be performed safely; " +
                "the account credentials and existing Codex data were preserved.");
        }

        return true;
    }

    private static OfficialCodexLaunchAttemptOutcome CompleteOfficialCodexLaunchAttempt(
        string projectPath,
        OfficialCodexLogBaseline launchLogBaseline,
        WindowsClientActivationIdentity activationIdentity,
        int? nativeFastPort,
        bool allowRendererPatch,
        long launchGeneration,
        ICollection<WindowsClientProcessSnapshot>? observedProcessTree = null)
    {
        var initialReadiness = WaitForOfficialCodexMainPageReady(
            launchLogBaseline,
            activationIdentity,
            OfficialCodexPrimaryPageReadyTimeout,
            launchGeneration,
            OfficialCodexLogReadinessStage.InitialLaunch,
            observedProcessTree);
        if (initialReadiness == OfficialCodexMainPageWaitOutcome.Superseded)
        {
            return OfficialCodexLaunchAttemptOutcome.Superseded;
        }
        if (initialReadiness != OfficialCodexMainPageWaitOutcome.Ready)
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-primary-page-not-ready",
                $"stage=before-patch; outcome={initialReadiness}; pid={activationIdentity.ProcessId}");
            return OfficialCodexLaunchAttemptOutcome.RecoverableFailure;
        }

        if (!nativeFastPort.HasValue || !allowRendererPatch)
        {
            if (nativeFastPort.HasValue && !allowRendererPatch)
            {
                // ChatGPT OAuth already supplies the feature identity used by the dual-login
                // entry. Keep the verified CDP owner for optional account-label injection, but
                // do not reload a healthy renderer merely to apply the PAT/API Fast picker.
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-renderer-patch-not-required",
                    $"pid={activationIdentity.ProcessId}; port={nativeFastPort.Value}; primary-ready=true");
            }
            if (!IsWindowsClientActivationIdentityAlive(activationIdentity))
            {
                return OfficialCodexLaunchAttemptOutcome.RecoverableFailure;
            }
            TryOpenOfficialCodexProject(projectPath, activationIdentity, launchGeneration);
            return OfficialCodexLaunchAttemptOutcome.ReadyWithoutRendererPatch;
        }

        // Capture a second cursor only after the unmodified page has acknowledged ready.  A
        // successful controlled reload must produce a fresh routes + ready IPC sequence.
        var postPatchBaseline = CaptureOfficialCodexLaunchLogBaseline();
        if (!postPatchBaseline.IsUsable)
        {
            // Without a complete cursor, pre-patch bytes could be mistaken for post-reload
            // readiness. Keep the already-ready unmodified page and do not start the bridge.
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-post-patch-baseline-unavailable",
                "the fresh log cursor was incomplete; renderer patch was skipped before reload");
            if (!IsWindowsClientActivationIdentityAlive(activationIdentity))
            {
                return OfficialCodexLaunchAttemptOutcome.RecoverableFailure;
            }
            TryOpenOfficialCodexProject(projectPath, activationIdentity, launchGeneration);
            return OfficialCodexLaunchAttemptOutcome.ReadyWithoutRendererPatch;
        }
        var nativeFastAttachOutcome = AttachNativeFastBridgeWhenOfficialCodexIsReady(
                nativeFastPort.Value,
                activationIdentity,
                launchGeneration)
            .GetAwaiter()
            .GetResult();
        if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
        {
            return OfficialCodexLaunchAttemptOutcome.Superseded;
        }

        if (nativeFastAttachOutcome == OfficialCodexNativeFastAttachOutcome.AlreadyPatched)
        {
            // The immutable owner-scoped bridge event was already signaled before this attach
            // call. The primary routes/IPC sequence observed above therefore belongs to the
            // patched renderer and does not need a second reload-generation marker.
            if (!IsVerifiedOfficialNativeFastEndpointOwner(nativeFastPort.Value, activationIdentity))
            {
                return OfficialCodexLaunchAttemptOutcome.RecoverableFailure;
            }
            TryOpenOfficialCodexProject(projectPath, activationIdentity, launchGeneration);
            return OfficialCodexLaunchAttemptOutcome.Ready;
        }

        if (nativeFastAttachOutcome == OfficialCodexNativeFastAttachOutcome.NoReload)
        {
            // No bridge process reached the controlled-reload phase. Keep the primary page that
            // already completed routes + IPC instead of restarting a healthy unsupported build.
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-native-fast-unpatched",
                "the primary page is ready, but the optional reviewed renderer patch was unavailable");
            if (!IsWindowsClientActivationIdentityAlive(activationIdentity))
            {
                return OfficialCodexLaunchAttemptOutcome.RecoverableFailure;
            }
            TryOpenOfficialCodexProject(projectPath, activationIdentity, launchGeneration);
            return OfficialCodexLaunchAttemptOutcome.ReadyWithoutRendererPatch;
        }

        var postPatchReadiness = WaitForOfficialCodexMainPageReady(
            postPatchBaseline,
            activationIdentity,
            OfficialCodexPostPatchPageReadyTimeout,
            launchGeneration,
            OfficialCodexLogReadinessStage.RendererReload,
            observedProcessTree);
        if (postPatchReadiness == OfficialCodexMainPageWaitOutcome.Superseded)
        {
            return OfficialCodexLaunchAttemptOutcome.Superseded;
        }
        if (postPatchReadiness != OfficialCodexMainPageWaitOutcome.Ready)
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-post-patch-page-not-ready",
                $"outcome={postPatchReadiness}; attach={nativeFastAttachOutcome}; " +
                $"pid={activationIdentity.ProcessId}; port={nativeFastPort.Value}");
            return OfficialCodexLaunchAttemptOutcome.RecoverableFailure;
        }

        if (!IsVerifiedOfficialNativeFastEndpointOwner(nativeFastPort.Value, activationIdentity))
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-post-patch-owner-lost",
                $"pid={activationIdentity.ProcessId}; port={nativeFastPort.Value}");
            return OfficialCodexLaunchAttemptOutcome.RecoverableFailure;
        }

        TryOpenOfficialCodexProject(projectPath, activationIdentity, launchGeneration);
        return nativeFastAttachOutcome == OfficialCodexNativeFastAttachOutcome.PatchedAfterThisCall
            ? OfficialCodexLaunchAttemptOutcome.Ready
            : OfficialCodexLaunchAttemptOutcome.ReadyWithoutRendererPatch;
    }

    private static OfficialCodexMainPageWaitOutcome WaitForOfficialCodexMainPageReady(
        OfficialCodexLogBaseline baseline,
        WindowsClientActivationIdentity activationIdentity,
        TimeSpan timeout,
        long launchGeneration,
        OfficialCodexLogReadinessStage stage,
        ICollection<WindowsClientProcessSnapshot>? observedProcessTree = null)
    {
        var probe = baseline.CreateProbe(
            activationIdentity.ProcessId,
            activationIdentity.StartTimeUtcTicks,
            stage);
        if (!probe.IsAvailable)
        {
            return OfficialCodexMainPageWaitOutcome.ProbeUnavailable;
        }

        var deadline = DateTime.UtcNow + timeout;
        DateTime? readySinceUtc = null;
        while (DateTime.UtcNow < deadline)
        {
            RememberWindowsClientProcessTree(activationIdentity, observedProcessTree);
            if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
            {
                return OfficialCodexMainPageWaitOutcome.Superseded;
            }

            var logState = probe.Poll();
            if (logState == OfficialCodexLogReadinessState.PersistedAtomSyncFailed)
            {
                return OfficialCodexMainPageWaitOutcome.PersistedAtomSyncFailed;
            }
            if (!IsWindowsClientActivationIdentityAlive(activationIdentity))
            {
                return OfficialCodexMainPageWaitOutcome.ProcessExited;
            }
            if (logState == OfficialCodexLogReadinessState.Ready)
            {
                readySinceUtc ??= DateTime.UtcNow;
                if (DateTime.UtcNow - readySinceUtc.Value >= OfficialCodexReadyStableDuration)
                {
                    return OfficialCodexMainPageWaitOutcome.Ready;
                }
            }
            else
            {
                readySinceUtc = null;
            }

            Thread.Sleep(200);
        }

        return OfficialCodexMainPageWaitOutcome.TimedOut;
    }

    private static bool TryRecoverOfficialCodexLaunchWithoutRendererPatch(
        string projectPath,
        WindowsClientActivationIdentity failedActivationIdentity,
        int? failedNativeFastPort,
        long launchGeneration,
        ICollection<WindowsClientProcessSnapshot> observedFailedProcessTree)
    {
        if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration) ||
            !failedActivationIdentity.StartTimeUtcTicks.HasValue)
        {
            return false;
        }

        // Resolve only the exact activation root and its captured descendants. A time-window
        // filter can accidentally include a newer manual activation or another manager copy.
        RememberWindowsClientProcessTree(
            failedActivationIdentity,
            observedFailedProcessTree);
        var shutdownTargets = observedFailedProcessTree.ToArray();
        var failedIdentityStillAlive = IsWindowsClientActivationIdentityAlive(
            failedActivationIdentity);
        if (failedIdentityStillAlive &&
            !shutdownTargets.Any(snapshot =>
                snapshot.ProcessId == failedActivationIdentity.ProcessId &&
                snapshot.StartTimeUtcTicks == failedActivationIdentity.StartTimeUtcTicks.Value))
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-clean-restart-refused",
                "the failed activation PID/start-time identity was not present in the exact shutdown snapshot");
            return false;
        }
        var releasePorts = CaptureOfficialNativeFastPortsOwnedBy(shutdownTargets).ToList();
        if (failedNativeFastPort.HasValue && !releasePorts.Contains(failedNativeFastPort.Value))
        {
            // This manager selected the port while it was free. Require it to be free again even
            // if the owner vanished between the endpoint snapshot and recovery capture.
            releasePorts.Add(failedNativeFastPort.Value);
        }
        if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
        {
            return false;
        }

        WriteCodexPlusPlusLaunchDiagnostic(
            "official-clean-restart-started",
            $"pid={failedActivationIdentity.ProcessId}; port={failedNativeFastPort?.ToString() ?? "none"}; patch=disabled-on-retry");
        StopWindowsClientProcesses(shutdownTargets);
        if (!WaitForWindowsClientProcessAndPortRelease(
                shutdownTargets,
                releasePorts,
                launchGeneration,
                OfficialCodexRecoveryReleaseTimeout) ||
            !WaitForOfficialCodexSwitchQuiescence(
                launchGeneration,
                OfficialCodexRecoveryReleaseTimeout))
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-clean-restart-release-timeout",
                "the failed process tree did not become globally quiescent, or its CDP listener did not release cleanly; retry activation was suppressed");
            return false;
        }
        if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
        {
            return false;
        }

        var retryLogBaseline = CaptureOfficialCodexLaunchLogBaseline();
        WindowsClientActivationIdentity retryIdentity;
        try
        {
            // Recovery deliberately has no CDP argument, bridge process, or controlled reload.
            retryIdentity = ActivateOfficialCodexPackage();
        }
        catch (Exception ex)
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-clean-restart-activation-failed",
                MaskSensitive(ex.Message));
            return false;
        }

        var observedRetryProcessTree = new List<WindowsClientProcessSnapshot>();
        RememberWindowsClientProcessTree(retryIdentity, observedRetryProcessTree);
        var retryOutcome = CompleteOfficialCodexLaunchAttempt(
            projectPath,
            retryLogBaseline,
            retryIdentity,
            nativeFastPort: null,
            allowRendererPatch: false,
            launchGeneration,
            observedRetryProcessTree);
        var succeeded = retryOutcome is OfficialCodexLaunchAttemptOutcome.Ready or
            OfficialCodexLaunchAttemptOutcome.ReadyWithoutRendererPatch;
        WriteCodexPlusPlusLaunchDiagnostic(
            succeeded ? "official-clean-restart-ready" : "official-clean-restart-not-ready",
            $"outcome={retryOutcome}; pid={retryIdentity.ProcessId}; retryCount=1");
        if (!succeeded &&
            retryOutcome != OfficialCodexLaunchAttemptOutcome.Superseded)
        {
            CleanupFailedOfficialCodexRetry(
                retryIdentity,
                observedRetryProcessTree,
                launchGeneration);
        }
        return succeeded;
    }

    private static void RememberWindowsClientProcessTree(
        WindowsClientActivationIdentity activationIdentity,
        ICollection<WindowsClientProcessSnapshot>? observedProcessTree)
    {
        if (observedProcessTree == null)
        {
            return;
        }

        foreach (var snapshot in CaptureWindowsClientProcessTreeSnapshots(activationIdentity))
        {
            if (!observedProcessTree.Any(existing =>
                    existing.ProcessId == snapshot.ProcessId &&
                    existing.StartTimeUtcTicks == snapshot.StartTimeUtcTicks))
            {
                observedProcessTree.Add(snapshot);
            }
        }
    }

    private static void CleanupFailedOfficialCodexRetry(
        WindowsClientActivationIdentity retryIdentity,
        ICollection<WindowsClientProcessSnapshot> observedProcessTree,
        long launchGeneration)
    {
        if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration) ||
            !retryIdentity.StartTimeUtcTicks.HasValue)
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-clean-restart-cleanup-refused",
                "the retry activation no longer owned the launch generation or lacked a PID/start-time identity");
            return;
        }

        RememberWindowsClientProcessTree(retryIdentity, observedProcessTree);
        var shutdownTargets = observedProcessTree.ToArray();
        var retryIdentityStillAlive = IsWindowsClientActivationIdentityAlive(retryIdentity);
        if (retryIdentityStillAlive &&
            !shutdownTargets.Any(snapshot =>
                snapshot.ProcessId == retryIdentity.ProcessId &&
                snapshot.StartTimeUtcTicks == retryIdentity.StartTimeUtcTicks.Value))
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-clean-restart-cleanup-refused",
                "the retry PID/start-time identity was not present in its exact process-tree snapshot");
            return;
        }

        var releasePorts = CaptureOfficialNativeFastPortsOwnedBy(shutdownTargets);
        StopWindowsClientProcesses(shutdownTargets);
        var released = WaitForWindowsClientProcessAndPortRelease(
            shutdownTargets,
            releasePorts,
            launchGeneration,
            OfficialCodexRecoveryReleaseTimeout);
        WriteCodexPlusPlusLaunchDiagnostic(
            released
                ? "official-clean-restart-cleanup-complete"
                : "official-clean-restart-cleanup-timeout",
            $"pid={retryIdentity.ProcessId}; processCount={shutdownTargets.Length}; dataPreserved=true");
    }

    private static bool WaitForWindowsClientProcessAndPortRelease(
        IReadOnlyList<WindowsClientProcessSnapshot> shutdownTargets,
        IReadOnlyCollection<int> nativeFastPorts,
        long launchGeneration,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var stableReleasedSamples = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
            {
                return false;
            }

            var processesReleased = AreWindowsClientProcessSnapshotsReleased(shutdownTargets);
            var portsReleased = nativeFastPorts.All(IsOfficialNativeFastCdpPortAvailable);
            if (processesReleased && portsReleased)
            {
                stableReleasedSamples++;
                if (stableReleasedSamples >= 2)
                {
                    return true;
                }
            }
            else
            {
                stableReleasedSamples = 0;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    private static bool WaitForOfficialCodexSwitchQuiescence(
        long launchGeneration,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var stableQuiescentSamples = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
            {
                return false;
            }

            var processScanAvailable = TryAreOfficialCodexProcessesAbsent(out var processesAbsent);
            var managerOwnedCdpOwnerAbsent = !OfficialNativeFastCdpPortCandidates().Any(
                port => TryGetOfficialCodexLoopbackListenerIdentity(port, out _));
            if (processScanAvailable && processesAbsent && managerOwnedCdpOwnerAbsent)
            {
                stableQuiescentSamples++;
                if (stableQuiescentSamples >= 2)
                {
                    return true;
                }
            }
            else
            {
                stableQuiescentSamples = 0;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    private static bool TryAreOfficialCodexProcessesAbsent(out bool processesAbsent)
    {
        processesAbsent = false;
        var clientPath = ResolveCodexWindowsClientPath();
        var packageRoot = string.IsNullOrWhiteSpace(clientPath)
            ? null
            : Path.GetDirectoryName(clientPath);
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return false;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch
                {
                    // Process.GetProcesses can include protected system processes whose names are
                    // unavailable. They cannot be confused with a successfully named Codex image.
                    continue;
                }

                if (!processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) &&
                    !processName.Equals("Codex", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }
                }
                catch
                {
                    return false;
                }

                if (!TryClassifyCodexWindowsClientProcess(process, packageRoot, out var isOfficial))
                {
                    // A named candidate whose immutable executable path cannot be inspected makes
                    // absence unprovable, so credential projection must fail closed.
                    return false;
                }
                if (isOfficial)
                {
                    return true;
                }
            }

            processesAbsent = true;
            return true;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool AreWindowsClientProcessSnapshotsReleased(
        IReadOnlyList<WindowsClientProcessSnapshot> snapshots)
    {
        var clientPath = ResolveCodexWindowsClientPath();
        var packageRoot = string.IsNullOrWhiteSpace(clientPath)
            ? null
            : Path.GetDirectoryName(clientPath);
        foreach (var snapshot in snapshots)
        {
            if (!TryOpenWindowsClientSnapshot(snapshot, packageRoot, out var process))
            {
                continue;
            }

            process!.Dispose();
            return false;
        }

        return true;
    }

    private static bool IsWindowsClientActivationIdentityAlive(
        WindowsClientActivationIdentity activationIdentity)
    {
        try
        {
            using var process = Process.GetProcessById(activationIdentity.ProcessId);
            var clientPath = ResolveCodexWindowsClientPath();
            var packageRoot = string.IsNullOrWhiteSpace(clientPath)
                ? null
                : Path.GetDirectoryName(clientPath);
            return !process.HasExited &&
                   IsCodexWindowsClientProcess(process, packageRoot) &&
                   MatchesWindowsClientActivationIdentity(process, activationIdentity);
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryOpenOfficialCodexProject(
        string projectPath,
        WindowsClientActivationIdentity activationIdentity,
        long launchGeneration)
    {
        if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration) ||
            !IsWindowsClientActivationIdentityAlive(activationIdentity))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(BuildNewThreadDeepLink(projectPath))
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // The application-ready official client remains usable if the optional project
            // selection activation cannot be delivered.
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-project-deep-link-unavailable",
                MaskSensitive(ex.Message));
        }
    }

    private static OfficialCodexLogBaseline CaptureOfficialCodexLaunchLogBaseline()
    {
        return OfficialCodexLogBaseline.Capture(ResolveOfficialCodexLogDirectory());
    }

    private static string? ResolveOfficialCodexLogDirectory()
    {
        var appUserModelId = ResolveCodexWindowsClientAppUserModelId();
        var separator = appUserModelId?.IndexOf('!') ?? -1;
        if (separator <= 0)
        {
            return null;
        }

        var packageFamilyName = appUserModelId![..separator];
        if (!Regex.IsMatch(
                packageFamilyName,
                "^[A-Za-z0-9._-]{1,200}$",
                RegexOptions.CultureInvariant))
        {
            return null;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            packageFamilyName,
            "LocalCache",
            "Local",
            "Codex",
            "Logs");
    }

    public string GetCodexDreamSkinStatus() => CodexDreamSkinService.GetStatusText();

    public Task<bool> ApplyCodexDreamSkinAsync(
        ThemeMode appearanceMode,
        string presetId,
        string appearanceLabel,
        string projectPath)
    {
        return Task.Run(() => RunWithWindowsClientSwitchMutex(launchGeneration =>
        {
            StopWindowsClientForAppearanceChange(launchGeneration);
            ApplyAndStartDreamSkinOrRestore(
                appearanceMode,
                presetId,
                appearanceLabel,
                projectPath);
            // Dream Skin Start has already reopened Codex; return whether the optional project
            // deep link was accepted so the UI can show a non-fatal hint when it was not.
            return TryLaunchOfficialCodexFallback(projectPath, launchGeneration);
        }));
    }

    private static void ApplyAndStartDreamSkinOrRestore(
        ThemeMode appearanceMode,
        string presetId,
        string? appearanceLabel,
        string projectPath)
    {
        try
        {
            // Refresh the installed engine before every opt-in launch. This prevents an older
            // %LOCALAPPDATA% copy from running against a newer bundled theme contract.
            CodexDreamSkinService.Install();
            CodexDreamSkinService.ApplyAppearance(appearanceMode, presetId, appearanceLabel);
            CodexDreamSkinService.Start();
        }
        catch (Exception applyError)
        {
            Exception? restoreError = null;
            try
            {
                CodexDreamSkinService.RestoreOfficialAppearance();
            }
            catch (Exception ex)
            {
                restoreError = ex;
            }

            // Both install and start can close the current Codex process. Always make a best-
            // effort attempt to return the user to the official client after a failed apply.
            var fallbackStarted = TryLaunchOfficialCodexFallback(projectPath);
            if (restoreError != null)
            {
                throw new CodexDreamSkinApplyException(
                    "Codex 图片主题应用失败，自动恢复官方外观也未完成。" +
                    $"应用错误：{applyError.Message} 恢复错误：{restoreError.Message}" +
                    (fallbackStarted ? string.Empty : " 官方客户端也未能自动重新打开。"),
                    officialAppearanceRestored: false,
                    officialClientRelaunched: fallbackStarted,
                    innerException: new AggregateException(applyError, restoreError));
            }

            throw new CodexDreamSkinApplyException(
                "Codex 图片主题应用失败，已自动恢复官方外观" +
                (fallbackStarted ? "并重新打开 Codex" : "，但 Codex 未能自动重新打开") +
                $"：{applyError.Message}",
                officialAppearanceRestored: true,
                officialClientRelaunched: fallbackStarted,
                innerException: applyError);
        }
    }

    public Task<bool> RestoreOfficialCodexAppearanceAsync(string projectPath)
    {
        return Task.Run(() => RunWithWindowsClientSwitchMutex(launchGeneration =>
        {
            StopWindowsClientForAppearanceChange(launchGeneration);
            Exception? restoreError = null;
            try
            {
                CodexDreamSkinService.RestoreOfficialAppearance();
            }
            catch (Exception ex)
            {
                restoreError = ex;
            }

            var relaunched = TryLaunchOfficialCodexFallback(projectPath, launchGeneration);
            if (restoreError != null)
            {
                throw new InvalidOperationException(
                    "恢复 Codex 官方外观失败" +
                    (relaunched ? "，已重新打开 Codex" : "，Codex 也未能自动重新打开") +
                    $"：{restoreError.Message}",
                    restoreError);
            }
            // A failed activation must not turn a successful appearance restore into a failed
            // operation: the caller still needs to persist UseCodexDreamSkin=false. Returning the
            // activation result lets the UI show a separate "please start Codex manually" hint.
            return relaunched;
        }));
    }

    private static T RunWithWindowsClientSwitchMutex<T>(Func<long, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var switchMutex = new Mutex(false, WindowsClientSwitchMutexName);
        var mutexAcquired = false;
        try
        {
            try
            {
                mutexAcquired = switchMutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }
            if (!mutexAcquired)
            {
                throw new TimeoutException(
                    "Another Codex account switch is still running. Please retry shortly.");
            }

            return operation(BeginWindowsClientLaunchGeneration());
        }
        finally
        {
            if (mutexAcquired)
            {
                switchMutex.ReleaseMutex();
            }
        }
    }

    private static void StopWindowsClientForAppearanceChange(long launchGeneration)
    {
        var shutdownTargets = CaptureWindowsClientProcessSnapshots();
        var nativeFastPorts = CaptureOfficialNativeFastPortsOwnedBy(shutdownTargets);
        StopWindowsClientProcesses(shutdownTargets);
        if (!WaitForWindowsClientProcessAndPortRelease(
                shutdownTargets,
                nativeFastPorts,
                launchGeneration,
                OfficialCodexRecoveryReleaseTimeout) ||
            !WaitForOfficialCodexSwitchQuiescence(
                launchGeneration,
                OfficialCodexRecoveryReleaseTimeout))
        {
            throw new TimeoutException(
                "Codex did not remain fully closed before the appearance change; no new client was started.");
        }
    }

    private static bool TryLaunchOfficialCodexFallback(
        string projectPath,
        long? expectedLaunchGeneration = null)
    {
        var launchGeneration = expectedLaunchGeneration ?? BeginWindowsClientLaunchGeneration();
        try
        {
            var launchStartedUtc = DateTime.UtcNow;
            var activationIdentity = ActivateOfficialCodexPackage();
            OpenNewTaskAfterOfficialCodexLaunchInBackground(
                projectPath,
                launchStartedUtc,
                activationIdentity,
                launchGeneration);
            return true;
        }
        catch (OfficialCodexActivationIdentityException)
        {
            // COM created a PID but its immutable identity could not be proven. The activation
            // helper has already failed closed; a protocol launch here would create or attach a
            // second untracked shell and bypass the readiness/recovery contract.
            return false;
        }
        catch
        {
            try
            {
                Process.Start(BuildOfficialCodexActivationStartInfo(projectPath));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void OpenNewTaskAfterOfficialCodexLaunchInBackground(
        string projectPath,
        DateTime launchStartedUtc,
        WindowsClientActivationIdentity activationIdentity,
        long launchGeneration,
        Task<bool>? nativeFastReadyTask = null)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
                {
                    return;
                }

                if (!WaitForWindowsClientRuntimeHealthy(
                        launchStartedUtc,
                        TimeSpan.FromSeconds(45),
                        activationIdentity,
                        launchGeneration))
                {
                    if (IsCurrentWindowsClientLaunchGeneration(launchGeneration))
                    {
                        WriteCodexPlusPlusLaunchDiagnostic(
                            "official-runtime-not-ready",
                            "renderer/app-server did not become stable before the deep-link deadline");
                    }
                    return;
                }

                if (nativeFastReadyTask != null)
                {
                    try
                    {
                        // The deep link mounts a fresh native composer. Deliver it only after the
                        // reviewed renderer response substitution is ready, so PAT/API users see Standard/Fast
                        // without focus/visibility events that could trigger network refetches.
                        _ = nativeFastReadyTask.Wait(OfficialNativeFastRendererReadyTimeout);
                    }
                    catch
                    {
                        // The bridge is additive and records its own local diagnostic.
                    }
                }

                // Runtime health has already remained stable for several seconds. Give the
                // first renderer frame one final short turn before delivering the deep link.
                Thread.Sleep(TimeSpan.FromSeconds(1));
                if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration) ||
                    !IsWindowsClientRuntimeHealthySince(
                        launchStartedUtc.AddSeconds(-2),
                        activationIdentity))
                {
                    return;
                }

                Process.Start(new ProcessStartInfo(BuildNewThreadDeepLink(projectPath))
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // The official client itself remains usable if the optional project deep link
                // cannot be delivered after activation.
            }
        });
    }

    private static WindowsClientActivationIdentity ActivateOfficialCodexPackage(
        int? nativeFastCdpPort = null)
    {
        var appUserModelId = ResolveCodexWindowsClientAppUserModelId();
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            throw new InvalidOperationException(
                "The installed OpenAI.Codex package identity could not be resolved.");
        }

        var activationManager =
            (IApplicationActivationManager)(object)new ApplicationActivationManager();
        try
        {
            var result = activationManager.ActivateApplication(
                appUserModelId,
                nativeFastCdpPort.HasValue
                    ? BuildOfficialNativeFastActivationArguments(nativeFastCdpPort.Value)
                    : string.Empty,
                ApplicationActivationOptions.None,
                out var processId);
            Marshal.ThrowExceptionForHR(result);
            if (processId == 0)
            {
                throw new InvalidOperationException(
                    "Windows did not return a process ID after official Codex activation.");
            }

            return CaptureWindowsClientActivationIdentity(processId);
        }
        finally
        {
            if (Marshal.IsComObject(activationManager))
            {
                Marshal.FinalReleaseComObject(activationManager);
            }
        }
    }

    private static WindowsClientActivationIdentity CaptureWindowsClientActivationIdentity(uint processId)
    {
        if (processId > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Windows returned an unsupported Codex process ID: {processId}.");
        }

        var managedProcessId = (int)processId;
        Process? activationProcess = null;
        var handleDeadline = DateTime.UtcNow.AddSeconds(2);
        do
        {
            Process? candidate = null;
            try
            {
                candidate = Process.GetProcessById(managedProcessId);
                // Force a native process handle to be opened immediately. Keeping this exact
                // handle alive prevents a later PID reuse from retargeting identity cleanup.
                _ = candidate.Handle;
                activationProcess = candidate;
                candidate = null;
                break;
            }
            catch (Exception ex) when (
                ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                candidate?.Dispose();
                activationProcess?.Dispose();
                activationProcess = null;
            }

            Thread.Sleep(50);
        } while (DateTime.UtcNow < handleDeadline);

        if (activationProcess == null)
        {
            // No stable handle means the COM-returned PID cannot be distinguished from a later
            // reused PID. Never reopen and terminate it by number alone.
            throw new OfficialCodexActivationIdentityException(
                managedProcessId,
                cleanupSucceeded: false);
        }

        using (activationProcess)
        {
            var identityDeadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                try
                {
                    if (activationProcess.HasExited)
                    {
                        throw new OfficialCodexActivationIdentityException(
                            managedProcessId,
                            cleanupSucceeded: true);
                    }

                    return new WindowsClientActivationIdentity(
                        managedProcessId,
                        activationProcess.StartTime.ToUniversalTime().Ticks);
                }
                catch (OfficialCodexActivationIdentityException)
                {
                    throw;
                }
                catch (Exception ex) when (
                    ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // The handle is immutable even while packaged-process metadata is delayed.
                }

                Thread.Sleep(50);
            } while (DateTime.UtcNow < identityDeadline);

            var cleanupSucceeded = TryStopOfficialCodexActivationWithoutStartTime(
                activationProcess);
            throw new OfficialCodexActivationIdentityException(
                managedProcessId,
                cleanupSucceeded);
        }
    }

    private static bool TryStopOfficialCodexActivationWithoutStartTime(Process activationProcess)
    {
        try
        {
            if (activationProcess.HasExited)
            {
                return true;
            }

            var clientPath = ResolveCodexWindowsClientPath();
            var packageRoot = string.IsNullOrWhiteSpace(clientPath)
                ? null
                : Path.GetDirectoryName(clientPath);
            if (!IsCodexWindowsClientProcess(activationProcess, packageRoot))
            {
                return false;
            }

            // The overload without entireProcessTree terminates through the immutable handle that
            // was opened immediately after COM activation. Tree enumeration would reopen the root
            // by its now-reusable PID and defeat the purpose of holding that handle.
            activationProcess.Kill();
            return activationProcess.WaitForExit(
                       (int)OfficialCodexRecoveryReleaseTimeout.TotalMilliseconds) ||
                   activationProcess.HasExited;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    internal static string BuildOfficialNativeFastActivationArguments(int port)
    {
        if (port < OfficialNativeFastCdpPortBase ||
            port >= OfficialNativeFastCdpPortBase + OfficialNativeFastCdpPortCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                port,
                "Official Codex native Fast CDP port is outside the manager-owned range.");
        }

        return "--remote-debugging-address=127.0.0.1 " +
               "--remote-debugging-port=" +
               port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<int> OfficialNativeFastCdpPortCandidates()
    {
        return Enumerable.Range(OfficialNativeFastCdpPortBase, OfficialNativeFastCdpPortCount).ToArray();
    }

    private static IReadOnlyList<int> DreamSkinCdpPortCandidates()
    {
        var candidates = Enumerable.Range(DreamSkinCdpPortBase, DreamSkinCdpPortCount).ToList();
        if (CodexDreamSkinService.TryGetRecordedCdpPort(out var recordedPort) &&
            !candidates.Contains(recordedPort))
        {
            candidates.Insert(0, recordedPort);
        }
        return candidates;
    }

    private static int SelectOfficialNativeFastCdpPort()
    {
        return SelectOfficialNativeFastCdpPort(IsOfficialNativeFastCdpPortAvailable);
    }

    private static int SelectOfficialNativeFastCdpPort(Func<int, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        foreach (var port in OfficialNativeFastCdpPortCandidates())
        {
            if (isAvailable(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException(
            $"No free loopback port is available for native Fast between " +
            $"{OfficialNativeFastCdpPortBase} and " +
            $"{OfficialNativeFastCdpPortBase + OfficialNativeFastCdpPortCount - 1}.");
    }

    private static bool IsOfficialNativeFastCdpPortAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            // Another listener owns this candidate. Never attach the bridge to it; try the
            // next manager-owned port and verify the eventual listener owner before attaching.
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static Task<OfficialCodexNativeFastAttachOutcome> AttachNativeFastBridgeWhenOfficialCodexIsReady(
        int port,
        WindowsClientActivationIdentity activationIdentity,
        long launchGeneration)
    {
        return Task.Run(() =>
        {
            Process? bridgeProcess = null;
            string? bridgeBrowserId = null;
            var bridgeOwnerPid = 0;
            long bridgeOwnerStartTicks = 0;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline &&
                       IsCurrentWindowsClientLaunchGeneration(launchGeneration))
                {
                    if (!IsWindowsClientActivationIdentityAlive(activationIdentity))
                    {
                        return OfficialCodexNativeFastAttachOutcome.NoReload;
                    }
                    if (TryCaptureOfficialNativeFastEndpoint(
                            port,
                            out var browserId,
                            out var listenerIdentity) &&
                        listenerIdentity.ProcessId == activationIdentity.ProcessId &&
                        (!activationIdentity.StartTimeUtcTicks.HasValue ||
                         listenerIdentity.StartTimeUtcTicks == activationIdentity.StartTimeUtcTicks))
                    {
                        if (CodexNativeFastBridge.WaitForRendererPatch(
                                port,
                                browserId,
                                listenerIdentity.ProcessId,
                                listenerIdentity.StartTimeUtcTicks ?? 0,
                                TimeSpan.Zero))
                        {
                            return OfficialCodexNativeFastAttachOutcome.AlreadyPatched;
                        }
                        bridgeBrowserId = browserId;
                        bridgeOwnerPid = listenerIdentity.ProcessId;
                        bridgeOwnerStartTicks = listenerIdentity.StartTimeUtcTicks ?? 0;
                        bridgeProcess = CodexNativeFastBridge.StartDetached(
                            port,
                            browserId,
                            bridgeOwnerPid,
                            bridgeOwnerStartTicks,
                            GetCodexWindowsClientAppDirectory() ??
                            throw new InvalidOperationException(
                                "The installed Codex application directory could not be verified."),
                            allowRendererReload: true);
                        var patchOutcome = CodexNativeFastBridge.WaitForRendererPatchOutcome(
                            port,
                            browserId,
                            bridgeOwnerPid,
                            bridgeOwnerStartTicks,
                            OfficialNativeFastRendererReadyTimeout);
                        if (patchOutcome == NativeFastPatchWaitOutcome.Patched)
                        {
                            return OfficialCodexNativeFastAttachOutcome.PatchedAfterThisCall;
                        }
                        if (patchOutcome is
                            NativeFastPatchWaitOutcome.SkippedWithoutReload or
                            NativeFastPatchWaitOutcome.TimedOutWithoutReload)
                        {
                            WriteCodexPlusPlusLaunchDiagnostic(
                                patchOutcome == NativeFastPatchWaitOutcome.SkippedWithoutReload
                                    ? "official-native-fast-renderer-skipped"
                                    : "official-native-fast-renderer-timeout-no-reload",
                                patchOutcome == NativeFastPatchWaitOutcome.SkippedWithoutReload
                                    ? $"verified renderer preflight rejected the patch on port {port}; reload=false"
                                    : $"verified bridge did not finish preflight on port {port}; reload=false");
                            // Stop the exact helper and then re-read the owner-scoped events. A
                            // timeout/skip is only a snapshot; a Page.reload signal can arrive at
                            // that boundary and must permanently forbid a NoReload result.
                            return StopOwnedNativeFastBridgeAndResolveNoReload(
                                bridgeProcess,
                                port,
                                browserId,
                                bridgeOwnerPid,
                                bridgeOwnerStartTicks);
                        }
                        if (patchOutcome != NativeFastPatchWaitOutcome.TimedOutAfterReloadAttempt)
                        {
                            throw new InvalidOperationException(
                                $"Unsupported native Fast patch outcome: {patchOutcome}.");
                        }
                        WriteCodexPlusPlusLaunchDiagnostic(
                            "official-native-fast-renderer-timeout",
                            $"verified bridge attempted renderer reload but did not report readiness on port {port}");
                        // Only the owner-scoped ReloadAttempted event permits this recovery path.
                        // Starting a helper process alone says nothing about whether Page.reload
                        // ran and must never cause a healthy Codex process to be restarted.
                        return OfficialCodexNativeFastAttachOutcome.ReloadMayHaveStarted;
                    }

                    Thread.Sleep(250);
                }
                if (IsCurrentWindowsClientLaunchGeneration(launchGeneration))
                {
                    WriteCodexPlusPlusLaunchDiagnostic(
                        "official-native-fast-endpoint-timeout",
                        $"no verified official Codex app page CDP endpoint appeared on port {port}");
                }
                return OfficialCodexNativeFastAttachOutcome.NoReload;
            }
            catch (Exception ex)
            {
                // Fast remains additive. A validation or bridge-process failure must not affect
                // the already running signed official client.
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-native-fast-attach-unavailable",
                    MaskSensitive(ex.Message));
                var reloadMayHaveStarted = false;
                if (bridgeBrowserId != null && bridgeOwnerPid > 0 && bridgeOwnerStartTicks > 0)
                {
                    try
                    {
                        var finalOutcome = CodexNativeFastBridge.WaitForRendererPatchOutcome(
                                port,
                                bridgeBrowserId,
                                bridgeOwnerPid,
                                bridgeOwnerStartTicks,
                                TimeSpan.Zero);
                        reloadMayHaveStarted = NativeFastPatchOutcomeRequiresReloadReadiness(
                            finalOutcome);
                        if (!reloadMayHaveStarted)
                        {
                            reloadMayHaveStarted =
                                StopOwnedNativeFastBridgeAndResolveNoReload(
                                    bridgeProcess,
                                    port,
                                    bridgeBrowserId,
                                    bridgeOwnerPid,
                                    bridgeOwnerStartTicks) ==
                                OfficialCodexNativeFastAttachOutcome.ReloadMayHaveStarted;
                        }
                    }
                    catch
                    {
                        // If the exact helper cannot be finalized against its owner-scoped
                        // events, do not claim that a late reload was impossible even when the
                        // process handle itself can be stopped.
                        _ = StopOwnedNativeFastBridgeProcess(bridgeProcess);
                        reloadMayHaveStarted = true;
                    }
                }
                else
                {
                    reloadMayHaveStarted = !StopOwnedNativeFastBridgeProcess(bridgeProcess);
                }
                return reloadMayHaveStarted
                    ? OfficialCodexNativeFastAttachOutcome.ReloadMayHaveStarted
                    : OfficialCodexNativeFastAttachOutcome.NoReload;
            }
            finally
            {
                bridgeProcess?.Dispose();
            }
        });
    }

    private static bool NativeFastPatchOutcomeRequiresReloadReadiness(
        NativeFastPatchWaitOutcome outcome)
    {
        return outcome is
            NativeFastPatchWaitOutcome.Patched or
            NativeFastPatchWaitOutcome.TimedOutAfterReloadAttempt;
    }

    private static OfficialCodexNativeFastAttachOutcome StopOwnedNativeFastBridgeAndResolveNoReload(
        Process? bridgeProcess,
        int port,
        string browserId,
        int ownerPid,
        long ownerStartTicks)
    {
        var helperStopped = StopOwnedNativeFastBridgeProcess(bridgeProcess);
        var finalOutcome = CodexNativeFastBridge.WaitForRendererPatchOutcome(
            port,
            browserId,
            ownerPid,
            ownerStartTicks,
            TimeSpan.Zero);
        return helperStopped &&
               !NativeFastPatchOutcomeRequiresReloadReadiness(finalOutcome)
            ? OfficialCodexNativeFastAttachOutcome.NoReload
            : OfficialCodexNativeFastAttachOutcome.ReloadMayHaveStarted;
    }

    private static bool StopOwnedNativeFastBridgeProcess(Process? bridgeProcess)
    {
        if (bridgeProcess == null)
        {
            return true;
        }

        try
        {
            if (bridgeProcess.HasExited)
            {
                return true;
            }

            // This Process object owns the exact helper handle returned by StartDetached;
            // no process-name or PID-only lookup is used here.
            bridgeProcess.Kill();
            if (!bridgeProcess.WaitForExit(
                    (int)OfficialNativeFastBridgeStopTimeout.TotalMilliseconds))
            {
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-native-fast-helper-stop-timeout",
                    "the exact helper did not confirm exit before final reload-state inspection");
                return false;
            }
            return bridgeProcess.HasExited;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-native-fast-helper-stop-unavailable",
                ex.GetType().Name);
            return false;
        }
    }

    private static bool TryAttachNativeFastBridgeToExistingOfficialCodex()
    {
        return TryAttachNativeFastBridgeToExistingOfficialCodex(
            OfficialNativeFastCdpPortCandidates(),
            out _);
    }

    internal static bool TryRefreshNativeFastBridgeAfterUpdate()
    {
        using var switchMutex = new Mutex(false, WindowsClientSwitchMutexName);
        var mutexAcquired = false;
        try
        {
            try
            {
                // A post-update refresh is optional. Never let it race account switching, and do
                // not reload an already healthy renderer here: this maintenance path has no fresh
                // routes + ready IPC probe or clean-recovery transaction. The next normal official
                // launch can apply the reviewed patch through the fully gated launch path.
                mutexAcquired = switchMutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }
            if (!mutexAcquired)
            {
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-native-fast-update-refresh-deferred",
                    "an account switch owns the renderer readiness gate");
                return false;
            }

            return TryAttachNativeFastBridgeToExistingOfficialCodex(
                       OfficialNativeFastCdpPortCandidates(),
                       out var rendererReady,
                       allowRendererReload: false) &&
                   rendererReady;
        }
        catch (Exception ex)
        {
            WriteCodexPlusPlusLaunchDiagnostic(
                "official-native-fast-update-refresh-unavailable",
                MaskSensitive(ex.Message));
            return false;
        }
        finally
        {
            if (mutexAcquired)
            {
                switchMutex.ReleaseMutex();
            }
        }
    }

    private static bool TryAttachNativeFastBridgeToExistingOfficialCodex(
        IEnumerable<int> portCandidates,
        out bool rendererReady,
        bool allowRendererReload = false)
    {
        ArgumentNullException.ThrowIfNull(portCandidates);
        rendererReady = false;
        foreach (var port in portCandidates)
        {
            if (!TryCaptureOfficialNativeFastEndpoint(
                    port,
                    out var browserId,
                    out var listenerIdentity))
            {
                continue;
            }

            try
            {
                if (CodexNativeFastBridge.WaitForRendererPatch(
                        port,
                        browserId,
                        listenerIdentity.ProcessId,
                        listenerIdentity.StartTimeUtcTicks ?? 0,
                        TimeSpan.Zero))
                {
                    rendererReady = true;
                    return true;
                }
                using var bridgeProcess = CodexNativeFastBridge.StartDetached(
                    port,
                    browserId,
                    listenerIdentity.ProcessId,
                    listenerIdentity.StartTimeUtcTicks ?? 0,
                    GetCodexWindowsClientAppDirectory() ??
                    throw new InvalidOperationException(
                        "The installed Codex application directory could not be verified."),
                    allowRendererReload: allowRendererReload);
                rendererReady = CodexNativeFastBridge.WaitForRendererPatch(
                        port,
                        browserId,
                        listenerIdentity.ProcessId,
                        listenerIdentity.StartTimeUtcTicks ?? 0,
                        TimeSpan.FromSeconds(5));
                if (!rendererReady)
                {
                    WriteCodexPlusPlusLaunchDiagnostic(
                        "official-native-fast-existing-renderer-timeout",
                        $"verified bridge did not report a patched renderer on port {port}");
                }
            }
            catch (Exception ex)
            {
                // A healthy CDP-enabled Codex is still preferable to restarting a same-account
                // client merely because the optional bridge child could not be created. A later
                // explicit start operation will retry the idempotent, identity-scoped bridge.
                WriteCodexPlusPlusLaunchDiagnostic(
                    "official-native-fast-existing-attach-unavailable",
                    MaskSensitive(ex.Message));
            }
            // The endpoint and owning Codex process were verified even when the optional
            // renderer patch was unavailable. Preserve that healthy same-account client;
            // restarting it cannot make an unknown renderer version satisfy the patch contract.
            return true;
        }

        return false;
    }

    private static bool TryCaptureOfficialNativeFastEndpoint(
        int port,
        out string browserId,
        out WindowsClientActivationIdentity listenerIdentity)
    {
        browserId = "";
        listenerIdentity = null!;
        if (!TryGetOfficialCodexLoopbackListenerIdentity(port, out var before) ||
            !TryReadOfficialCodexCdpIdentity(port, expectedBrowserId: null, out browserId) ||
            !TryGetOfficialCodexLoopbackListenerIdentity(port, out var after) ||
            before.ProcessId != after.ProcessId ||
            before.StartTimeUtcTicks != after.StartTimeUtcTicks)
        {
            browserId = "";
            return false;
        }

        listenerIdentity = after;
        return true;
    }

    private static bool IsVerifiedOfficialNativeFastEndpointOwner(
        int port,
        WindowsClientActivationIdentity expectedIdentity)
    {
        return TryCaptureOfficialNativeFastEndpoint(
                   port,
                   out _,
                   out var actualIdentity) &&
               actualIdentity.ProcessId == expectedIdentity.ProcessId &&
               (!expectedIdentity.StartTimeUtcTicks.HasValue ||
                actualIdentity.StartTimeUtcTicks == expectedIdentity.StartTimeUtcTicks);
    }

    private static bool TryGetOfficialCodexLoopbackListenerIdentity(
        int port,
        out WindowsClientActivationIdentity identity)
    {
        identity = null!;
        var bufferSize = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferSize,
            order: false,
            AddressFamilyInterNetwork,
            TcpTableOwnerPidListener,
            reserved: 0);
        if (result != ErrorInsufficientBuffer || bufferSize <= sizeof(uint))
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref bufferSize,
                order: false,
                AddressFamilyInterNetwork,
                TcpTableOwnerPidListener,
                reserved: 0);
            if (result != 0)
            {
                return false;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            if (rowCount is < 0 or > 65535)
            {
                return false;
            }

            var rowSize = Marshal.SizeOf<NativeTcpRowOwnerPid>();
            var rowPointer = IntPtr.Add(buffer, sizeof(uint));
            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<NativeTcpRowOwnerPid>(rowPointer);
                rowPointer = IntPtr.Add(rowPointer, rowSize);
                var localPort = unchecked((ushort)IPAddress.NetworkToHostOrder(
                    unchecked((short)row.LocalPort)));
                if (localPort != port ||
                    !new IPAddress(BitConverter.GetBytes(row.LocalAddress)).Equals(IPAddress.Loopback) ||
                    row.OwningProcessId is 0 or > int.MaxValue)
                {
                    continue;
                }

                try
                {
                    using var process = Process.GetProcessById((int)row.OwningProcessId);
                    var clientPath = ResolveCodexWindowsClientPath();
                    var packageRoot = string.IsNullOrWhiteSpace(clientPath)
                        ? null
                        : Path.GetDirectoryName(clientPath);
                    if (process.HasExited || !IsCodexWindowsClientProcess(process, packageRoot))
                    {
                        return false;
                    }

                    identity = new WindowsClientActivationIdentity(
                        process.Id,
                        process.StartTime.ToUniversalTime().Ticks);
                    return true;
                }
                catch (Exception ex) when (
                    ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    return false;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadOfficialCodexCdpIdentity(
        int port,
        string? expectedBrowserId,
        out string browserId)
    {
        browserId = "";
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromMilliseconds(400)
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(900),
                MaxResponseContentBufferSize = OfficialNativeFastCdpMaxResponseBytes
            };
            using var version = ReadOfficialCodexCdpJson(client, port, "/json/version");
            var versionSocket = version.RootElement.TryGetProperty(
                "webSocketDebuggerUrl",
                out var versionSocketValue)
                ? versionSocketValue.GetString()
                : null;
            if (!TryValidateOfficialCodexCdpSocket(
                    versionSocket,
                    port,
                    "browser",
                    expectedId: expectedBrowserId,
                    out browserId))
            {
                return false;
            }

            using var targets = ReadOfficialCodexCdpJson(client, port, "/json/list");
            if (targets.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var target in targets.RootElement.EnumerateArray())
            {
                var type = target.TryGetProperty("type", out var typeValue)
                    ? typeValue.GetString()
                    : null;
                var pageUrl = target.TryGetProperty("url", out var urlValue)
                    ? urlValue.GetString()
                    : null;
                var targetId = target.TryGetProperty("id", out var idValue)
                    ? idValue.GetString()
                    : null;
                var targetSocket = target.TryGetProperty("webSocketDebuggerUrl", out var socketValue)
                    ? socketValue.GetString()
                    : null;
                if (string.Equals(type, "page", StringComparison.Ordinal) &&
                    IsReviewedOfficialCodexPageUrl(pageUrl) &&
                    !string.IsNullOrWhiteSpace(targetId) &&
                    CdpIdentityPattern.IsMatch(targetId) &&
                    TryValidateOfficialCodexCdpSocket(
                        targetSocket,
                        port,
                        "page",
                        targetId,
                        out _))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or IOException or
            JsonException or InvalidOperationException or NotSupportedException)
        {
            browserId = "";
            return false;
        }
    }

    private static bool IsReviewedOfficialCodexPageUrl(string? value)
    {
        return CodexNativeFastBridge.IsReviewedOfficialCodexPageUrl(value);
    }

    private static JsonDocument ReadOfficialCodexCdpJson(
        HttpClient client,
        int port,
        string resource)
    {
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        var readToken = readTimeout.Token;
        using var response = client.GetAsync(
                new Uri($"http://127.0.0.1:{port}{resource}", UriKind.Absolute),
                HttpCompletionOption.ResponseHeadersRead,
                readToken)
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > OfficialNativeFastCdpMaxResponseBytes)
        {
            throw new InvalidOperationException("Codex CDP response exceeded the safety limit.");
        }

        using var stream = response.Content.ReadAsStreamAsync(readToken)
            .GetAwaiter()
            .GetResult();
        using var bounded = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = stream.ReadAsync(buffer, 0, buffer.Length, readToken)
                .GetAwaiter()
                .GetResult();
            if (read == 0)
            {
                break;
            }
            if (bounded.Length + read > OfficialNativeFastCdpMaxResponseBytes)
            {
                throw new InvalidOperationException("Codex CDP response exceeded the safety limit.");
            }
            bounded.Write(buffer, 0, read);
        }
        bounded.Position = 0;
        return JsonDocument.Parse(bounded);
    }

    private static bool TryValidateOfficialCodexCdpSocket(
        string? value,
        int port,
        string targetKind,
        string? expectedId,
        out string actualId)
    {
        actualId = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != port ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var prefix = "/devtools/" + targetKind + "/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        actualId = uri.AbsolutePath[prefix.Length..];
        return CdpIdentityPattern.IsMatch(actualId) &&
               (expectedId == null || actualId.Equals(expectedId, StringComparison.Ordinal));
    }

    private static long BeginWindowsClientLaunchGeneration()
    {
        return Interlocked.Increment(ref _windowsClientLaunchGeneration);
    }

    private static bool IsCurrentWindowsClientLaunchGeneration(long generation)
    {
        return Volatile.Read(ref _windowsClientLaunchGeneration) == generation;
    }

    private static OfficialCodexRecoveryDecision DecideOfficialCodexRecovery(
        OfficialCodexLaunchAttemptOutcome outcome,
        bool recoveryAlreadyAttempted)
    {
        return outcome switch
        {
            OfficialCodexLaunchAttemptOutcome.Ready or
            OfficialCodexLaunchAttemptOutcome.ReadyWithoutRendererPatch =>
                OfficialCodexRecoveryDecision.Complete,
            OfficialCodexLaunchAttemptOutcome.RecoverableFailure when !recoveryAlreadyAttempted =>
                OfficialCodexRecoveryDecision.RestartWithoutRendererPatch,
            _ => OfficialCodexRecoveryDecision.Stop
        };
    }

    internal static ProcessStartInfo BuildOfficialCodexActivationStartInfo(string projectPath)
    {
        return new ProcessStartInfo(BuildNewThreadDeepLink(projectPath))
        {
            UseShellExecute = true
        };
    }

    public void RepairCodexPlusPlusScheduledTask()
    {
        var launcherPath = ResolveCodexPlusPlusLauncherPath();
        if (string.IsNullOrWhiteSpace(launcherPath))
        {
            throw new FileNotFoundException("找不到已安装的 Codex++，无法修复隐藏启动任务。");
        }

        EnsureCodexPlusPlusTaskFiles();
        if (!ScheduledTaskUsesHiddenPowerShell(CodexPlusPlusTaskName, out _))
        {
            InstallCodexPlusPlusScheduledTaskElevated();
        }
    }

    /// <summary>
    /// Refreshes the PowerShell launcher and installer scripts used by the existing
    /// Codex++ scheduled task without touching the task registration or any process.
    ///
    /// Older installations can retain a launcher generated by a previous version.
    /// Keeping this operation separate from <see cref="RepairCodexPlusPlusScheduledTask"/>
    /// lets the normal UI startup update the script in place without prompting for
    /// elevation or restarting Codex/Account Manager.
    /// </summary>
    public void RefreshCodexPlusPlusTaskLauncherFiles()
    {
        EnsureCodexPlusPlusTaskFiles();
    }

    internal static string BuildThreadDeepLink(string threadId)
    {
        if (!Guid.TryParse(threadId, out var parsedThreadId))
        {
            throw new ArgumentException("Codex task ID is invalid.", nameof(threadId));
        }

        return "codex://threads/" + parsedThreadId.ToString("D");
    }

    internal static string BuildNewThreadDeepLink(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Codex project path is required.", nameof(projectPath));
        }

        return "codex://threads/new?path=" +
               Uri.EscapeDataString(Path.GetFullPath(projectPath));
    }

    internal static void ValidateOfficialCodexActivation()
    {
        var projectPath = Path.Combine("C:\\", "Users", "Example User", "Demo");
        var startInfo = BuildOfficialCodexActivationStartInfo(projectPath);
        if (!startInfo.UseShellExecute ||
            startInfo.FileName != BuildNewThreadDeepLink(projectPath) ||
            !startInfo.FileName.StartsWith("codex://threads/new?path=", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(startInfo.WorkingDirectory))
        {
            throw new InvalidOperationException(
                "Official Codex must be activated through the registered codex:// protocol.");
        }

        var nativeFastCandidates = OfficialNativeFastCdpPortCandidates();
        var firstNativeFastPort = nativeFastCandidates[0];
        var lastNativeFastPort = nativeFastCandidates[^1];
        var nativeFastArguments = BuildOfficialNativeFastActivationArguments(firstNativeFastPort);
        var dreamSkinOwnedPorts = Enumerable.Range(DreamSkinCdpPortBase, DreamSkinCdpPortCount).ToArray();
        var dreamSkinCandidates = DreamSkinCdpPortCandidates();
        var dreamSkinLastPort = DreamSkinCdpPortBase + DreamSkinCdpPortCount - 1;
        var selectedAfterTwoOccupiedPorts = SelectOfficialNativeFastCdpPort(
            port => port == firstNativeFastPort + 2);
        if (nativeFastCandidates.Count != OfficialNativeFastCdpPortCount ||
            nativeFastCandidates.Distinct().Count() != nativeFastCandidates.Count ||
            dreamSkinOwnedPorts[0] != DreamSkinCdpPortBase ||
            dreamSkinOwnedPorts[^1] != dreamSkinLastPort ||
            dreamSkinOwnedPorts.Except(dreamSkinCandidates).Any() ||
            dreamSkinCandidates.Distinct().Count() != dreamSkinCandidates.Count ||
            dreamSkinOwnedPorts.Intersect(nativeFastCandidates).Any() ||
            firstNativeFastPort != OfficialNativeFastCdpPortBase ||
            lastNativeFastPort != OfficialNativeFastCdpPortBase + OfficialNativeFastCdpPortCount - 1 ||
            firstNativeFastPort <= dreamSkinLastPort ||
            selectedAfterTwoOccupiedPorts != firstNativeFastPort + 2 ||
            nativeFastArguments !=
                $"--remote-debugging-address=127.0.0.1 --remote-debugging-port={firstNativeFastPort}" ||
            !IsReviewedOfficialCodexPageUrl("app://codex/") ||
            !IsReviewedOfficialCodexPageUrl("app://-/index.html") ||
            !IsReviewedOfficialCodexPageUrl(
                "app://-/index.html?initialRoute=%2Favatar-overlay") ||
            IsReviewedOfficialCodexPageUrl(null) ||
            IsReviewedOfficialCodexPageUrl("") ||
            IsReviewedOfficialCodexPageUrl(" app://-/index.html") ||
            IsReviewedOfficialCodexPageUrl("APP://-/index.html") ||
            IsReviewedOfficialCodexPageUrl("app://fs/") ||
            IsReviewedOfficialCodexPageUrl("app://codex/settings") ||
            IsReviewedOfficialCodexPageUrl("app://codex/?changed=1") ||
            IsReviewedOfficialCodexPageUrl("app://user@codex/") ||
            IsReviewedOfficialCodexPageUrl("app://codex/#fragment") ||
            IsReviewedOfficialCodexPageUrl("app://codex/%2e") ||
            IsReviewedOfficialCodexPageUrl("app://user@-/index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-:19335/index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html/") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html/extra") ||
            IsReviewedOfficialCodexPageUrl("app://-/./index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/foo/../index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/%69ndex.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/index%2Ehtml") ||
            IsReviewedOfficialCodexPageUrl("app://-:0/index.html") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html#fragment") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html?changed=1") ||
            IsReviewedOfficialCodexPageUrl(
                "app://-/index.html?initialRoute=/avatar-overlay") ||
            IsReviewedOfficialCodexPageUrl("app://-/index.html?initialRoute=%2Fsettings") ||
            IsReviewedOfficialCodexPageUrl(
                "app://-/index.html?initialRoute=%2favatar-overlay") ||
            IsReviewedOfficialCodexPageUrl(
                "app://-/index.html?initialRoute=%2Favatar-overlay&changed=1") ||
            IsReviewedOfficialCodexPageUrl("http://-/index.html") ||
            IsReviewedOfficialCodexPageUrl("https://codex/") ||
            !TryValidateOfficialCodexCdpSocket(
                $"ws://127.0.0.1:{firstNativeFastPort}/devtools/browser/test-browser",
                firstNativeFastPort,
                "browser",
                "test-browser",
                out var validatedBrowserId) ||
            validatedBrowserId != "test-browser" ||
            TryValidateOfficialCodexCdpSocket(
                $"ws://localhost:{firstNativeFastPort}/devtools/browser/test-browser",
                firstNativeFastPort,
                "browser",
                "test-browser",
                out _))
        {
            throw new InvalidOperationException(
                "Official native Fast activation ports or loopback CDP validation are unsafe.");
        }

        try
        {
            _ = BuildOfficialNativeFastActivationArguments(OfficialNativeFastCdpPortBase - 1);
            throw new InvalidOperationException(
                "Official native Fast activation accepted a port outside its owned range.");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected: package activation may only receive a manager-selected port.
        }

        var supersededGeneration = BeginWindowsClientLaunchGeneration();
        var currentGeneration = BeginWindowsClientLaunchGeneration();
        if (IsCurrentWindowsClientLaunchGeneration(supersededGeneration) ||
            !IsCurrentWindowsClientLaunchGeneration(currentGeneration) ||
            WaitForWindowsClientRuntimeHealthy(
                DateTime.UtcNow,
                TimeSpan.FromSeconds(1),
                activationIdentity: null,
                launchGeneration: supersededGeneration))
        {
            throw new InvalidOperationException(
                "Only the newest Windows client launch may deliver its delayed deep link.");
        }

        ValidateWindowsClientSurfaceAnalysis();
    }

    internal static void ValidateOfficialCodexLaunchRecovery()
    {
        if (OfficialCodexPrimaryPageReadyTimeout < TimeSpan.FromSeconds(60) ||
            OfficialCodexPostPatchPageReadyTimeout < TimeSpan.FromSeconds(30) ||
            OfficialCodexReadyStableDuration < TimeSpan.FromMilliseconds(400) ||
            SelectOfficialCodexStartupReadinessPolicy(allowRendererPatch: true) !=
                OfficialCodexStartupReadinessPolicy.VerifySynchronouslyForRendererPatch ||
            SelectOfficialCodexStartupReadinessPolicy(allowRendererPatch: false) !=
                OfficialCodexStartupReadinessPolicy.PreserveActivationAndObserveInBackground ||
            DecideOfficialCodexRecovery(
                OfficialCodexLaunchAttemptOutcome.Ready,
                recoveryAlreadyAttempted: false) != OfficialCodexRecoveryDecision.Complete ||
            DecideOfficialCodexRecovery(
                OfficialCodexLaunchAttemptOutcome.ReadyWithoutRendererPatch,
                recoveryAlreadyAttempted: false) != OfficialCodexRecoveryDecision.Complete ||
            DecideOfficialCodexRecovery(
                OfficialCodexLaunchAttemptOutcome.RecoverableFailure,
                recoveryAlreadyAttempted: false) !=
                OfficialCodexRecoveryDecision.RestartWithoutRendererPatch ||
            DecideOfficialCodexRecovery(
                OfficialCodexLaunchAttemptOutcome.RecoverableFailure,
                recoveryAlreadyAttempted: true) != OfficialCodexRecoveryDecision.Stop ||
            DecideOfficialCodexRecovery(
                OfficialCodexLaunchAttemptOutcome.Superseded,
                recoveryAlreadyAttempted: false) != OfficialCodexRecoveryDecision.Stop)
        {
            throw new InvalidOperationException(
                "Official Codex launch recovery must preserve a ready/degraded client, " +
                "restart a failed first attempt once, and never recurse after recovery.");
        }

        var patAccount = new AccountRecord { AuthKind = AccountAuthKind.AccessToken };
        var apiAccount = new AccountRecord { AuthKind = AccountAuthKind.CompatibleApi };
        var oauthAccount = new AccountRecord { AuthKind = AccountAuthKind.OfficialOAuth };
        if (!ShouldApplyOfficialRendererPatch(
                patAccount,
                AccessTokenSharedProfileMode.ApiCompatible,
                chatGptFeatureAccount: null) ||
            !ShouldApplyOfficialRendererPatch(
                apiAccount,
                AccessTokenSharedProfileMode.ApiCompatible,
                chatGptFeatureAccount: null) ||
            ShouldApplyOfficialRendererPatch(
                patAccount,
                AccessTokenSharedProfileMode.ChatGptDesktop,
                oauthAccount) ||
            ShouldApplyOfficialRendererPatch(
                oauthAccount,
                AccessTokenSharedProfileMode.ApiCompatible,
                chatGptFeatureAccount: null))
        {
            throw new InvalidOperationException(
                "Dual-login and official OAuth launches must preserve the primary renderer, " +
                "while pure PAT/API launches may still request the optional Fast patch.");
        }
        if (!ShouldPreserveExistingOfficialWindow(
                switchRequired: false,
                mode: WindowsClientMode.OfficialCodex,
                useDreamSkin: false,
                hasExistingOfficialWindow: true,
                allowOfficialRendererPatch: false) ||
            ShouldPreserveExistingOfficialWindow(
                switchRequired: true,
                mode: WindowsClientMode.OfficialCodex,
                useDreamSkin: false,
                hasExistingOfficialWindow: true,
                allowOfficialRendererPatch: false) ||
            ShouldPreserveExistingOfficialWindow(
                switchRequired: false,
                mode: WindowsClientMode.CodexPlusPlus,
                useDreamSkin: false,
                hasExistingOfficialWindow: true,
                allowOfficialRendererPatch: false) ||
            ShouldPreserveExistingOfficialWindow(
                switchRequired: false,
                mode: WindowsClientMode.OfficialCodex,
                useDreamSkin: false,
                hasExistingOfficialWindow: false,
                allowOfficialRendererPatch: false) ||
            ShouldPreserveExistingOfficialWindow(
                switchRequired: false,
                mode: WindowsClientMode.OfficialCodex,
                useDreamSkin: false,
                hasExistingOfficialWindow: true,
                allowOfficialRendererPatch: true))
        {
            throw new InvalidOperationException(
                "Only an existing same-profile dual-login/OAuth official window may bypass runtime-health replacement.");
        }
        if (!NativeFastPatchOutcomeRequiresReloadReadiness(
                NativeFastPatchWaitOutcome.Patched) ||
            !NativeFastPatchOutcomeRequiresReloadReadiness(
                NativeFastPatchWaitOutcome.TimedOutAfterReloadAttempt) ||
            NativeFastPatchOutcomeRequiresReloadReadiness(
                NativeFastPatchWaitOutcome.SkippedWithoutReload) ||
            NativeFastPatchOutcomeRequiresReloadReadiness(
                NativeFastPatchWaitOutcome.TimedOutWithoutReload))
        {
            throw new InvalidOperationException(
                "Native Fast finalization must never classify a patched or reload-attempted owner as NoReload.");
        }

        var syntheticSnapshots = new[]
        {
            new WindowsClientProcessSnapshot(10, 100, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\ChatGPT.exe", 0, 0),
            new WindowsClientProcessSnapshot(11, 110, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\ChatGPT.exe", 1, 10),
            new WindowsClientProcessSnapshot(12, 120, "Codex", @"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\Codex.exe", 2, 11),
            new WindowsClientProcessSnapshot(20, 200, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\ChatGPT.exe", 0, 0),
            new WindowsClientProcessSnapshot(21, 210, "Codex", @"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\Codex.exe", 2, 20)
        };
        var selectedTree = SelectWindowsClientProcessTreeSnapshots(
            syntheticSnapshots,
            new WindowsClientActivationIdentity(10, 100));
        if (!selectedTree.Select(snapshot => snapshot.ProcessId).Order().SequenceEqual(
                new[] { 10, 11, 12 }))
        {
            throw new InvalidOperationException(
                "Official Codex clean recovery selected processes outside the exact activation tree.");
        }

        var protectedShutdownNames = new[]
        {
            "CodexAccountManager",
            "CodexAccountManager.exe",
            "WindowsTerminal",
            "powershell",
            "pwsh",
            "cmd",
            "conhost",
            "OpenConsole"
        };
        if (protectedShutdownNames.Any(name =>
                !IsProtectedWindowsClientShutdownProcessName(name)) ||
            IsProtectedWindowsClientShutdownProcessName("ChatGPT") ||
            IsProtectedWindowsClientShutdownProcessName("Codex") ||
            IsProtectedWindowsClientShutdownProcessName("codex-plus-plus-manager"))
        {
            throw new InvalidOperationException(
                "Exact Codex shutdown must protect Account Manager and terminal hosts without excluding reviewed Codex executables.");
        }

        using var currentProcess = Process.GetCurrentProcess();
        if (!CaptureProcessParentIds().ContainsKey(currentProcess.Id))
        {
            throw new InvalidOperationException(
                "Windows process-parent capture could not identify the current self-test process.");
        }
        var protectedIds = SelectProtectedWindowsClientShutdownProcessIds(
            new Dictionary<int, int>
            {
                [100] = 50,
                [50] = 1,
                [200] = 100,
                [300] = 50
            },
            managerProcessId: 100);
        if (!protectedIds.SetEquals(new[] { 1, 50, 100 }) ||
            protectedIds.Contains(200) ||
            protectedIds.Contains(300))
        {
            throw new InvalidOperationException(
                "Exact Codex shutdown must protect the Manager's ancestor chain without excluding its separately verified desktop child.");
        }

        OfficialCodexLogReadiness.Validate();
    }

    internal static void ValidateDualLoginLaunchTransaction()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-dual-login-transaction-" + Guid.NewGuid().ToString("N"));
        var sharedHome = Path.Combine(root, "shared");
        var projectHome = Path.Combine(root, "project");
        var backupHome = Path.Combine(root, "backups");
        var oldSharedHome = Environment.GetEnvironmentVariable(SharedCodexHomeOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, sharedHome);
            Directory.CreateDirectory(sharedHome);
            Directory.CreateDirectory(projectHome);
            Directory.CreateDirectory(backupHome);

            var originalManagedFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.Combine(sharedHome, AuthFileName)] =
                    System.Text.Encoding.UTF8.GetBytes("{\"auth\":\"before\"}"),
                [Path.Combine(sharedHome, ConfigFileName)] =
                    System.Text.Encoding.UTF8.GetBytes("model = \"before\"\n"),
                [Path.Combine(sharedHome, DesktopSelectionFileName)] =
                    System.Text.Encoding.UTF8.GetBytes("{\"selection\":\"before\"}"),
                [Path.Combine(sharedHome, ActiveAccountStateFileName)] =
                    System.Text.Encoding.UTF8.GetBytes("{\"active\":\"before\"}")
            };
            foreach (var (path, contents) in originalManagedFiles)
            {
                File.WriteAllBytes(path, contents);
            }

            // Cockpit auth intentionally starts absent; rollback must remove a newly created one.
            var snapshot = CaptureWindowsClientSharedProfileSnapshot();
            foreach (var file in snapshot.Files)
            {
                File.WriteAllText(file.Path, "changed");
            }

            var globalStatePath = Path.Combine(sharedHome, GlobalStateFileName);
            var globalStateBackupPath = Path.Combine(backupHome, "global-state.json");
            File.WriteAllText(globalStateBackupPath, "{\"sidebar\":\"before\"}");
            File.WriteAllText(globalStatePath, "{\"sidebar\":\"changed\"}");
            var projectConfigPath = Path.Combine(projectHome, ConfigFileName);
            var projectConfigBackupPath = Path.Combine(backupHome, "project-config.toml");
            File.WriteAllText(projectConfigBackupPath, "model = \"project-before\"\n");
            File.WriteAllText(projectConfigPath, "model = \"project-changed\"\n");
            var modelCachePath = Path.Combine(sharedHome, "models_cache.json");
            var modelCacheBackupPath = Path.Combine(backupHome, "models-cache.json");
            File.WriteAllText(modelCacheBackupPath, "{\"models\":\"before\"}");
            File.WriteAllText(modelCachePath, "{\"models\":\"changed\"}");

            var projection = new WindowsClientAccountProjection
            {
                DefaultCodexHome = sharedHome,
                ProfileChanged = true,
                GlobalStatePath = globalStatePath,
                GlobalStateBackupPath = globalStateBackupPath,
                GlobalStateExisted = true,
                ModelCacheBackupPath = modelCacheBackupPath,
                ModelCacheExisted = true,
                ProjectConfigPath = projectConfigPath,
                ProjectConfigBackupPath = projectConfigBackupPath,
                ProjectConfigExisted = true
            };
            new CodexCliService().RestoreWindowsClientSwitchProjection(projection, snapshot);
            if (!WindowsClientSharedProfileMatchesSnapshot(snapshot) ||
                File.ReadAllText(globalStatePath) != "{\"sidebar\":\"before\"}" ||
                File.ReadAllText(projectConfigPath) != "model = \"project-before\"\n" ||
                File.ReadAllText(modelCachePath) != "{\"models\":\"before\"}")
            {
                throw new InvalidOperationException(
                    "A failed dual-login launch did not restore every switch-owned file.");
            }

            var oauthAccount = new AccountRecord
            {
                Name = "oauth-fixture",
                CodexHome = Path.Combine(root, "oauth"),
                AuthKind = AccountAuthKind.OfficialOAuth
            };
            if (!ShouldRollbackFailedExplicitChatGptFeatureSwitch(
                    projection,
                    WindowsClientMode.OfficialCodex,
                    AccessTokenSharedProfileMode.ChatGptDesktop,
                    oauthAccount,
                    switchRequired: true) ||
                ShouldRollbackFailedExplicitChatGptFeatureSwitch(
                    projection,
                    WindowsClientMode.OfficialCodex,
                    AccessTokenSharedProfileMode.ApiCompatible,
                    oauthAccount,
                    switchRequired: true) ||
                ShouldRollbackFailedExplicitChatGptFeatureSwitch(
                    projection,
                    WindowsClientMode.OfficialCodex,
                    AccessTokenSharedProfileMode.ChatGptDesktop,
                    oauthAccount,
                    switchRequired: false))
            {
                throw new InvalidOperationException(
                    "Dual-login rollback policy did not stay limited to a failed committed profile switch.");
            }
            projection.ClientLaunchStarted = true;
            if (ShouldRollbackFailedExplicitChatGptFeatureSwitch(
                    projection,
                    WindowsClientMode.OfficialCodex,
                    AccessTokenSharedProfileMode.ChatGptDesktop,
                    oauthAccount,
                    switchRequired: true))
            {
                throw new InvalidOperationException(
                    "A successful dual-login launch was incorrectly selected for rollback.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, oldSharedHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public Task OpenWindowsClientThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var threadUrl = BuildThreadDeepLink(threadId);
        if (!IsCodexPlusPlusReady())
        {
            throw new InvalidOperationException(
                "Codex++ 增强桥接尚未就绪。请先手动打开 Codex++，再打开聊天记录。不会请求管理员 PowerShell。");
        }

        Process.Start(new ProcessStartInfo(threadUrl)
        {
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public Task OpenOfficialCodexThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var threadUrl = BuildThreadDeepLink(threadId);
        Process.Start(new ProcessStartInfo(threadUrl)
        {
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public void OpenWindowsClientThread(string threadId)
    {
        OpenWindowsClientThreadAsync(threadId).GetAwaiter().GetResult();
    }

    private static void OpenNewTaskAfterWindowsClientLaunch(
        string projectPath,
        DateTime launchStartedUtc,
        bool requireCodexPlusPlusEnhancements)
    {
        if (!WaitForWindowsClientMainWindow(launchStartedUtc, TimeSpan.FromSeconds(45)))
        {
            throw new TimeoutException(
                "Codex++ started, but the new Codex window did not become ready within 45 seconds.");
        }

        if (requireCodexPlusPlusEnhancements)
        {
            WaitForCodexPlusPlusEnhancements(launchStartedUtc, TimeSpan.FromSeconds(55));
        }

        var startInfo = new ProcessStartInfo(BuildNewThreadDeepLink(projectPath))
        {
            UseShellExecute = true
        };
        Process.Start(startInfo);
    }

    private void OpenNewTaskAfterCodexPlusPlusLaunchInBackground(
        string projectPath,
        DateTime launchStartedUtc,
        long launchGeneration)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
                {
                    return;
                }

                WaitForCodexPlusPlusLaunchReady(
                    launchStartedUtc,
                    CodexPlusPlusLaunchReadyTimeout,
                    launcherProcess: null,
                    launcherStandardError: null,
                    launchGeneration);
                if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
                {
                    return;
                }

                Process.Start(new ProcessStartInfo(BuildNewThreadDeepLink(projectPath))
                {
                    UseShellExecute = true
                });
                WriteCodexPlusPlusLaunchDiagnostic(
                    "background-ready",
                    $"elapsed={(DateTime.UtcNow - launchStartedUtc).TotalSeconds:0.0}s");
            }
            catch (OperationCanceledException)
            {
                WriteCodexPlusPlusLaunchDiagnostic(
                    "background-superseded",
                    "a newer Windows client launch replaced this delayed deep-link request");
            }
            catch (Exception ex)
            {
                // Credential projection and the accepted Codex++ launch remain valid.  A
                // delayed bridge/deep-link failure is diagnostic only and must never freeze
                // the Account Manager UI or close the newly launched Codex process.
                WriteCodexPlusPlusLaunchDiagnostic(
                    "background-ready-failed",
                    MaskSensitive(ex.Message));
            }
        });
    }

    private static void OpenNewTaskAfterWindowsClientLaunchInBackground(
        string projectPath,
        DateTime launchStartedUtc,
        bool requireCodexPlusPlusEnhancements)
    {
        _ = Task.Run(() =>
        {
            try
            {
                OpenNewTaskAfterWindowsClientLaunch(
                    projectPath,
                    launchStartedUtc,
                    requireCodexPlusPlusEnhancements);
            }
            catch
            {
                // Codex++ itself has already been launched. A delayed deep-link
                // failure must not keep Account Manager blocked for up to 100 s.
            }
        });
    }

    private static void WaitForCodexPlusPlusEnhancements(DateTime launchStartedUtc, TimeSpan timeout)
    {
        var statusPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex-session-delete",
            "latest-status.json");
        var earliestStartMs = new DateTimeOffset(launchStartedUtc.AddSeconds(-2)).ToUnixTimeMilliseconds();
        var deadline = launchStartedUtc + timeout;
        var lastStatus = "waiting for Codex++ status";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var status = JsonNode.Parse(File.ReadAllText(statusPath)) as JsonObject;
                var startedAtMs = status?["started_at_ms"]?.GetValue<long>() ?? 0;
                var state = status?["status"]?.GetValue<string>() ?? "unknown";
                var message = status?["message"]?.GetValue<string>() ?? "";
                lastStatus = state + ": " + message;
                if (startedAtMs >= earliestStartMs && state.Equals("running", StringComparison.Ordinal))
                {
                    var helperPort = status?["helper_port"]?.GetValue<int>() ?? 0;
                    if (helperPort > 0)
                    {
                        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
                        using var response = client.PostAsync(
                                $"http://127.0.0.1:{helperPort}/backend/status",
                                body)
                            .GetAwaiter()
                            .GetResult();
                        if (response.IsSuccessStatusCode)
                        {
                            return;
                        }

                        lastStatus = $"helper returned HTTP {(int)response.StatusCode}";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException or TaskCanceledException)
            {
                lastStatus = ex.Message;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Codex++ page enhancements did not become ready: " + lastStatus);
    }

    private void WaitForCodexPlusPlusLaunchReady(
        DateTime launchStartedUtc,
        TimeSpan timeout,
        Process? launcherProcess,
        Task<string>? launcherStandardError,
        long launchGeneration)
    {
        var earliestStartUtc = launchStartedUtc.AddSeconds(-2);
        var deadline = DateTime.UtcNow + timeout;
        var windowReady = false;
        var bridgeReady = false;
        int? launcherExitCode = null;
        var bridgeStatus = "等待 Codex++ 生成新的增强桥接状态";
        while (DateTime.UtcNow < deadline)
        {
            if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
            {
                throw new OperationCanceledException(
                    "A newer Windows client launch superseded this readiness wait.");
            }

            windowReady = windowReady || HasWindowsClientMainWindowSince(earliestStartUtc);
            bridgeReady = bridgeReady || IsCodexPlusPlusReadySince(earliestStartUtc, out bridgeStatus);
            if (windowReady && bridgeReady)
            {
                return;
            }

            if (launcherProcess != null && launcherExitCode == null)
            {
                try
                {
                    if (launcherProcess.HasExited)
                    {
                        launcherExitCode = launcherProcess.ExitCode;
                        if (launcherExitCode != 0)
                        {
                            var error = launcherStandardError?.IsCompletedSuccessfully == true
                                ? MaskSensitive(launcherStandardError.Result).Trim()
                                : "";
                            WriteCodexPlusPlusLaunchDiagnostic(
                                "launcher-exit",
                                $"exitCode={launcherExitCode}; stderr={error}");
                            throw new InvalidOperationException(
                                $"Codex++ 启动失败（启动器退出代码 {launcherExitCode}）。\n" +
                                "账号凭据已保留，无需重新登录。\n" +
                                "请稍后重试；详细原因已写入本地启动诊断日志。");
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
                {
                    bridgeStatus = "无法读取 Codex++ 启动器退出状态：" + MaskSensitive(ex.Message);
                }
            }

            Thread.Sleep(200);
        }

        var windowText = windowReady ? "已就绪" : "未就绪";
        var bridgeText = bridgeReady ? "已就绪" : "未就绪";
        var launcherText = launcherExitCode.HasValue
            ? $"exited({launcherExitCode.Value})"
            : launcherProcess == null ? "scheduled-task-accepted" : "running";
        WriteCodexPlusPlusLaunchDiagnostic(
            "readiness-timeout",
            $"timeout={timeout.TotalSeconds:0}s; window={windowText}; bridge={bridgeText}; " +
            $"launcher={launcherText}; bridgeDetail={MaskSensitive(bridgeStatus)}");
        throw new TimeoutException(
            $"Codex++ 尚未完全启动（窗口：{windowText}；增强桥接：{bridgeText}）。\n" +
            "账号凭据已保留，无需重新登录。\n" +
            "请稍后重试；详细原因已写入本地启动诊断日志。" );
    }

    private static bool HasWindowsClientMainWindowSince(DateTime earliestStartUtc)
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited &&
                        process.MainWindowHandle != IntPtr.Zero &&
                        process.StartTime.ToUniversalTime() >= earliestStartUtc)
                    {
                        return true;
                    }
                }
                catch
                {
                    // A process may exit while Codex++ is replacing the desktop client.
                }
            }
        }

        return false;
    }

    private static bool IsWindowsClientRuntimeHealthySince(DateTime earliestStartUtc)
    {
        return IsWindowsClientRuntimeHealthySince(earliestStartUtc, activationIdentity: null);
    }

    private static bool IsWindowsClientRuntimeHealthySince(
        DateTime earliestStartUtc,
        WindowsClientActivationIdentity? activationIdentity)
    {
        var clientPath = ResolveCodexWindowsClientPath();
        var packageRoot = string.IsNullOrWhiteSpace(clientPath)
            ? null
            : Path.GetDirectoryName(clientPath);
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return false;
        }

        var mainWindowReady = false;
        var packagedAppServerReady = false;
        var helperProcessCount = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.HasExited ||
                        !IsCodexWindowsClientProcess(process, packageRoot) ||
                        process.StartTime.ToUniversalTime() < earliestStartUtc)
                    {
                        continue;
                    }

                    if (process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            if (MatchesWindowsClientActivationIdentity(process, activationIdentity) &&
                                process.Responding &&
                                GetWindowsClientSurfaceState(process.MainWindowHandle) !=
                                    WindowsClientSurfaceState.Blank)
                            {
                                mainWindowReady = true;
                            }
                        }
                        else
                        {
                            // Electron does not expose renderer command lines through Process.
                            // Requiring multiple packaged helpers avoids treating a lone crashpad
                            // process as a usable renderer runtime.
                            helperProcessCount++;
                        }
                    }
                    else if (process.ProcessName.Equals("Codex", StringComparison.OrdinalIgnoreCase))
                    {
                        packagedAppServerReady = true;
                    }
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // A process may exit or become inaccessible while the runtime is sampled.
                }
            }
        }

        return mainWindowReady && packagedAppServerReady && helperProcessCount >= 2;
    }

    private static bool MatchesWindowsClientActivationIdentity(
        Process process,
        WindowsClientActivationIdentity? activationIdentity)
    {
        if (activationIdentity == null)
        {
            return true;
        }

        if (process.Id != activationIdentity.ProcessId)
        {
            return false;
        }

        return !activationIdentity.StartTimeUtcTicks.HasValue ||
               process.StartTime.ToUniversalTime().Ticks == activationIdentity.StartTimeUtcTicks.Value;
    }

    private static WindowsClientSurfaceState GetWindowsClientSurfaceState(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero ||
            !GetClientRect(windowHandle, out var clientRect))
        {
            return WindowsClientSurfaceState.Unknown;
        }

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width < 320 || height < 240)
        {
            return WindowsClientSurfaceState.Unknown;
        }

        var windowDc = GetDC(windowHandle);
        if (windowDc == IntPtr.Zero)
        {
            return WindowsClientSurfaceState.Unknown;
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(windowDc);
            bitmap = CreateCompatibleBitmap(windowDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return WindowsClientSurfaceState.Unknown;
            }

            previousObject = SelectObject(memoryDc, bitmap);
            if (previousObject == IntPtr.Zero || previousObject == new IntPtr(-1))
            {
                return WindowsClientSurfaceState.Unknown;
            }

            var printCaptured = PrintWindow(
                windowHandle,
                memoryDc,
                PrintWindowClientOnly | PrintWindowRenderFullContent);
            var printState = printCaptured
                ? AnalyzeCapturedClientSurface(memoryDc, width, height)
                : WindowsClientSurfaceState.Unknown;
            if (printState != WindowsClientSurfaceState.Unknown)
            {
                return printState;
            }

            // Hardware-accelerated Electron windows can return a uniformly black bitmap from
            // PrintWindow even while they are healthy. A client-DC copy is a safe fallback;
            // if both methods are unavailable the visual result remains Unknown and the
            // process/app-server checks decide health instead of forcing a restart loop.
            if (!BitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    windowDc,
                    0,
                    0,
                    SourceCopy | CaptureBlt))
            {
                return WindowsClientSurfaceState.Unknown;
            }

            return AnalyzeCapturedClientSurface(memoryDc, width, height);
        }
        finally
        {
            if (previousObject != IntPtr.Zero && previousObject != new IntPtr(-1) && memoryDc != IntPtr.Zero)
            {
                _ = SelectObject(memoryDc, previousObject);
            }
            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }
            if (memoryDc != IntPtr.Zero)
            {
                _ = DeleteDC(memoryDc);
            }
            _ = ReleaseDC(windowHandle, windowDc);
        }
    }

    private static WindowsClientSurfaceState AnalyzeCapturedClientSurface(
        IntPtr deviceContext,
        int width,
        int height)
    {
        const int columnCount = 17;
        const int rowCount = 11;
        var colors = new List<(int Red, int Green, int Blue)>(columnCount * rowCount);

        for (var row = 0; row < rowCount; row++)
        {
            var y = Math.Clamp(
                (int)Math.Round((row + 0.5) * height / rowCount),
                0,
                height - 1);
            for (var column = 0; column < columnCount; column++)
            {
                var x = Math.Clamp(
                    (int)Math.Round((column + 0.5) * width / columnCount),
                    0,
                    width - 1);
                var color = GetPixel(deviceContext, x, y);
                if (color == InvalidGdiColor)
                {
                    continue;
                }

                var red = (int)(color & 0xFF);
                var green = (int)((color >> 8) & 0xFF);
                var blue = (int)((color >> 16) & 0xFF);
                colors.Add((red, green, blue));
            }
        }

        if (colors.Count < columnCount * rowCount * 3 / 4)
        {
            return WindowsClientSurfaceState.Unknown;
        }

        return ClassifyCapturedClientSurface(colors);
    }

    private static WindowsClientSurfaceState ClassifyCapturedClientSurface(
        IReadOnlyList<(int Red, int Green, int Blue)> colors)
    {
        if (colors.Count == 0)
        {
            return WindowsClientSurfaceState.Unknown;
        }

        var minimumRed = colors.Min(color => color.Red);
        var minimumGreen = colors.Min(color => color.Green);
        var minimumBlue = colors.Min(color => color.Blue);
        var maximumRed = colors.Max(color => color.Red);
        var maximumGreen = colors.Max(color => color.Green);
        var maximumBlue = colors.Max(color => color.Blue);
        var redTotal = colors.Sum(color => (long)color.Red);
        var greenTotal = colors.Sum(color => (long)color.Green);
        var blueTotal = colors.Sum(color => (long)color.Blue);

        // A uniformly black capture is the common failure mode for protected or fully GPU-
        // composed windows. Treat it as unknown rather than declaring a healthy dark theme blank.
        if (maximumRed <= 4 && maximumGreen <= 4 && maximumBlue <= 4)
        {
            return WindowsClientSurfaceState.Unknown;
        }

        var meanRed = (double)redTotal / colors.Count;
        var meanGreen = (double)greenTotal / colors.Count;
        var meanBlue = (double)blueTotal / colors.Count;
        var contrastingSamples = 0;
        foreach (var color in colors)
        {
            var redDistance = color.Red - meanRed;
            var greenDistance = color.Green - meanGreen;
            var blueDistance = color.Blue - meanBlue;
            if (redDistance * redDistance +
                greenDistance * greenDistance +
                blueDistance * blueDistance >= 625)
            {
                contrastingSamples++;
            }
        }

        var maximumChannelRange = Math.Max(
            maximumRed - minimumRed,
            Math.Max(maximumGreen - minimumGreen, maximumBlue - minimumBlue));
        return maximumChannelRange >= 24 && contrastingSamples >= 3 ||
               maximumChannelRange >= 16 && contrastingSamples >= 8
            ? WindowsClientSurfaceState.Rendered
            : WindowsClientSurfaceState.Blank;
    }

    private static void ValidateWindowsClientSurfaceAnalysis()
    {
        var blank = Enumerable.Repeat((Red: 249, Green: 247, Blue: 245), 187).ToArray();
        if (ClassifyCapturedClientSurface(blank) != WindowsClientSurfaceState.Blank)
        {
            throw new InvalidOperationException(
                "A uniformly blank Windows client surface was not detected.");
        }

        var structured = new List<(int Red, int Green, int Blue)>();
        structured.AddRange(Enumerable.Repeat((12, 9, 24), 44));
        structured.AddRange(Enumerable.Repeat((68, 35, 112), 48));
        structured.AddRange(Enumerable.Repeat((249, 247, 245), 95));
        var structuredState = ClassifyCapturedClientSurface(structured);
        if (structuredState != WindowsClientSurfaceState.Rendered)
        {
            throw new InvalidOperationException(
                $"A structured Windows client surface was incorrectly classified as {structuredState}.");
        }
    }

    private static bool WaitForWindowsClientRuntimeHealthy(
        DateTime launchStartedUtc,
        TimeSpan timeout)
    {
        return WaitForWindowsClientRuntimeHealthy(
            launchStartedUtc,
            timeout,
            activationIdentity: null,
            launchGeneration: null);
    }

    private static bool WaitForWindowsClientRuntimeHealthy(
        DateTime launchStartedUtc,
        TimeSpan timeout,
        WindowsClientActivationIdentity? activationIdentity,
        long? launchGeneration)
    {
        var earliestStartUtc = launchStartedUtc <= DateTime.MinValue.AddSeconds(2)
            ? DateTime.MinValue
            : launchStartedUtc.AddSeconds(-2);
        var deadline = DateTime.UtcNow + timeout;
        DateTime? healthySinceUtc = null;
        while (DateTime.UtcNow < deadline)
        {
            if (launchGeneration.HasValue &&
                !IsCurrentWindowsClientLaunchGeneration(launchGeneration.Value))
            {
                return false;
            }

            if (IsWindowsClientRuntimeHealthySince(earliestStartUtc, activationIdentity))
            {
                healthySinceUtc ??= DateTime.UtcNow;
                if (DateTime.UtcNow - healthySinceUtc.Value >= WindowsClientRuntimeStableDuration)
                {
                    return true;
                }
            }
            else
            {
                healthySinceUtc = null;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    private static bool WaitForWindowsClientMainWindow(DateTime launchStartedUtc, TimeSpan timeout)
    {
        var earliestStartUtc = launchStartedUtc.AddSeconds(-2);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited &&
                            process.MainWindowHandle != IntPtr.Zero &&
                            process.StartTime.ToUniversalTime() >= earliestStartUtc)
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // A process may exit or become inaccessible while the new window starts.
                    }
                }
            }

            Thread.Sleep(250);
        }

        return false;
    }

    private static bool TryLaunchCodexPlusPlusViaScheduledTask(
        string codexPlusPlusPath,
        string? clientAppDir,
        string projectPath,
        string codexHome,
        IReadOnlyList<WindowsClientProcessSnapshot> shutdownTargets,
        bool switchRequired)
    {
        CodexPlusPlusTaskOperationLock.Wait();
        try
        {
            EnsureCodexPlusPlusTaskFiles();
            var taskUsesHiddenPowerShell = ScheduledTaskUsesHiddenPowerShell(
                CodexPlusPlusTaskName,
                out var taskExists);
            if (!taskExists)
            {
                return false;
            }

            // Never repair or elevate implicitly from an account-switch click. An old task that
            // can show a console is treated as unavailable until the user explicitly repairs it.
            if (!taskUsesHiddenPowerShell)
            {
                return false;
            }

            var requestId = WriteCodexPlusPlusLaunchRequest(
                codexPlusPlusPath,
                clientAppDir,
                projectPath,
                codexHome,
                shutdownTargets,
                switchRequired);

            var result = RunProcess(
                "schtasks.exe",
                "/Run /TN " + QuoteProcessArgument(CodexPlusPlusTaskName),
                TimeSpan.FromSeconds(3));
            if (result.ExitCode != 0)
            {
                return false;
            }

            if (WaitForCodexPlusPlusTaskResult(requestId, CodexPlusPlusTaskResultTimeout))
            {
                return true;
            }

            WriteCodexPlusPlusLaunchDiagnostic(
                "scheduled-result-timeout",
                $"requestId={requestId}; timeout={CodexPlusPlusTaskResultTimeout.TotalSeconds:0}s");
            throw new TimeoutException(
                "Codex++ 启动任务未及时确认。\n" +
                "账号凭据已保留，无需重新登录。\n" +
                "请稍后重试；详细原因已写入本地启动诊断日志。");
        }
        finally
        {
            CodexPlusPlusTaskOperationLock.Release();
        }
    }

    private static bool WaitForCodexPlusPlusTaskResult(string requestId, TimeSpan timeout)
    {
        var resultPath = CodexPlusPlusTaskResultPath();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(resultPath))
            {
                try
                {
                    var result = JsonNode.Parse(File.ReadAllText(resultPath)) as JsonObject;
                    if (!string.Equals(
                            result?["requestId"]?.GetValue<string>(),
                            requestId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            result?["operation"]?.GetValue<string>(),
                            "launch",
                            StringComparison.Ordinal))
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    var succeeded = result?["succeeded"]?.GetValue<bool>() == true;
                    var enhancementsReady = result?["enhancementsReady"]?.GetValue<bool>() == true;
                    var launchAccepted = result?["launchAccepted"]?.GetValue<bool>() == true;
                    var processId = result?["processId"]?.GetValue<int>() ?? 0;
                    var launcherProcessId = result?["launcherProcessId"]?.GetValue<int>() ?? 0;
                    var launcherExited = result?["launcherExited"]?.GetValue<bool>() == true;
                    var launcherExitCode = result?["launcherExitCode"]?.GetValue<int?>();
                    var launcherStandardError = MaskSensitive(
                        result?["launcherStandardError"]?.GetValue<string>() ?? "").Trim();
                    if (!succeeded)
                    {
                        var error = MaskSensitive(result?["error"]?.GetValue<string>() ?? "").Trim();
                        var detail = string.IsNullOrWhiteSpace(error)
                            ? launcherStandardError
                            : error;
                        WriteCodexPlusPlusLaunchDiagnostic(
                            "scheduled-launch-failed",
                            $"exitCode={launcherExitCode?.ToString() ?? "unknown"}; detail={detail}");
                        throw new InvalidOperationException(
                            "Codex++ 启动失败（隐藏启动任务未能完成）。\n" +
                            "账号凭据已保留，无需重新登录。\n" +
                            "请稍后重试；详细原因已写入本地启动诊断日志。");
                    }
                    if (launcherExited && launcherExitCode is not null and not 0)
                    {
                        WriteCodexPlusPlusLaunchDiagnostic(
                            "scheduled-launcher-exit",
                            $"exitCode={launcherExitCode.Value}; stderr={launcherStandardError}");
                        throw new InvalidOperationException(
                            $"Codex++ 启动失败（启动器退出代码 {launcherExitCode.Value}）。\n" +
                            "账号凭据已保留，无需重新登录。\n" +
                            "请稍后重试；详细原因已写入本地启动诊断日志。");
                    }
                    if (!launchAccepted && !enhancementsReady)
                    {
                        throw new InvalidOperationException(
                            "Codex++ 隐藏启动任务返回了不完整的本次启动结果。");
                    }

                    if (launchAccepted)
                    {
                        // The elevated helper has already spawned Codex++. Window and enhancement
                        // readiness continue inside that helper and are intentionally not a UI gate.
                        return true;
                    }

                    var liveProcessId = processId > 0 ? processId : launcherProcessId;
                    if (liveProcessId > 0)
                    {
                        using var process = Process.GetProcessById(liveProcessId);
                        return !process.HasExited;
                    }
                }
                catch (ArgumentException)
                {
                    return false;
                }
                catch (JsonException)
                {
                    // The elevated task may still be completing its atomic result write.
                }
                catch (IOException)
                {
                    // The elevated task may still have the result file open.
                }
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static void InstallCodexPlusPlusScheduledTaskElevated()
    {
        EnsureCodexPlusPlusTaskFiles();
        var installerPath = CodexPlusPlusTaskInstallerPath();
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments =
                "-NoProfile -NoLogo -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File " +
                QuoteProcessArgument(installerPath)
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process != null && !process.WaitForExit(15000))
            {
                throw new TimeoutException("Codex++ 隐藏启动任务安装超过 15 秒，已停止前台等待。");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("需要安装一次 Codex++ 免 UAC 启动任务，但你取消了 Windows UAC 提示。默认 Codex 凭据已经切换完成。", ex);
        }

        if (!ScheduledTaskUsesHiddenPowerShell(CodexPlusPlusTaskName, out _))
        {
            throw new InvalidOperationException("Codex++ 免 UAC 隐藏启动任务没有安装成功。默认 Codex 凭据已经切换完成，可以稍后重试启动。");
        }
    }

    private static void EnsureCodexPlusPlusTaskFiles()
    {
        var dir = CodexPlusPlusTaskDirectory();
        Directory.CreateDirectory(dir);
        WriteTextAtomically(CodexPlusPlusTaskLauncherScriptPath(), BuildCodexPlusPlusTaskLauncherScript());
        WriteTextAtomically(CodexPlusPlusTaskInstallerPath(), BuildCodexPlusPlusTaskInstallerScript());
    }

    private static string WriteCodexPlusPlusLaunchRequest(
        string codexPlusPlusPath,
        string? clientAppDir,
        string projectPath,
        string codexHome,
        IReadOnlyList<WindowsClientProcessSnapshot> shutdownTargets,
        bool switchRequired)
    {
        var resultPath = CodexPlusPlusTaskResultPath();
        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
        }

        var requestId = Guid.NewGuid().ToString("N");
        var createdAtUtc = DateTimeOffset.UtcNow;
        var serializedShutdownTargets = new JsonArray();
        if (switchRequired)
        {
            foreach (var target in shutdownTargets)
            {
                serializedShutdownTargets.Add(new JsonObject
                {
                    ["processId"] = target.ProcessId,
                    ["startTimeUtcTicks"] = target.StartTimeUtcTicks,
                    ["processName"] = target.ProcessName,
                    ["executablePath"] = target.ExecutablePath
                });
            }
        }
        var request = new JsonObject
        {
            ["operation"] = "launch",
            ["requestId"] = requestId,
            ["createdAtUtc"] = createdAtUtc.ToString("O"),
            ["expiresAtUtc"] = createdAtUtc.Add(CodexPlusPlusTaskRequestLifetime).ToString("O"),
            ["managerProcessId"] = Environment.ProcessId,
            ["switchRequired"] = switchRequired,
            ["shutdownTargets"] = serializedShutdownTargets,
            ["codexPlusPlusPath"] = codexPlusPlusPath,
            ["codexAppDir"] = clientAppDir ?? "",
            ["projectPath"] = projectPath,
            ["codexHome"] = codexHome,
            ["codexSqliteHome"] = codexHome,
            ["proxyUri"] = GetConfiguredProxyUri() ?? "",
            ["newThreadUrl"] = BuildNewThreadDeepLink(projectPath)
        };
        WriteTextAtomically(
            CodexPlusPlusTaskRequestPath(),
            request.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return requestId;
    }

    private static void WriteCodexPlusPlusOpenThreadRequest(string requestId, string threadUrl)
    {
        var resultPath = CodexPlusPlusTaskResultPath();
        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
        }

        var request = new JsonObject
        {
            ["operation"] = "openThread",
            ["requestId"] = requestId,
            ["threadUrl"] = threadUrl
        };
        WriteTextAtomically(
            CodexPlusPlusTaskRequestPath(),
            request.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task WaitForCodexPlusPlusOpenThreadResultAsync(
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var resultPath = CodexPlusPlusTaskResultPath();
        var deadline = DateTime.UtcNow + timeout;
        string? lastReadError = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(resultPath))
            {
                try
                {
                    var result = JsonNode.Parse(File.ReadAllText(resultPath)) as JsonObject;
                    if (string.Equals(
                            result?["requestId"]?.GetValue<string>(),
                            requestId,
                            StringComparison.Ordinal))
                    {
                        var succeeded = result?["succeeded"]?.GetValue<bool>() == true;
                        var threadOpened = result?["threadOpened"]?.GetValue<bool>() == true;
                        var operation = result?["operation"]?.GetValue<string>() ?? "";
                        if (succeeded &&
                            threadOpened &&
                            operation.Equals("openThread", StringComparison.Ordinal))
                        {
                            return;
                        }

                        var error = result?["error"]?.GetValue<string>();
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(error)
                                ? "Codex++ 最高权限任务没有确认聊天记录已打开。"
                                : "Codex++ 无法打开聊天记录：" + error);
                    }
                }
                catch (JsonException ex)
                {
                    lastReadError = ex.Message;
                }
                catch (IOException ex)
                {
                    lastReadError = ex.Message;
                }
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(
            "等待 Codex++ 打开聊天记录超时。" +
            (string.IsNullOrWhiteSpace(lastReadError) ? "" : " 最后读取错误：" + lastReadError));
    }

    private static bool ScheduledTaskUsesHiddenPowerShell(string taskName, out bool taskExists)
    {
        var result = RunProcess(
            "schtasks.exe",
            "/Query /TN " + QuoteProcessArgument(taskName) + " /XML",
            TimeSpan.FromSeconds(3));
        taskExists = result.ExitCode == 0;
        if (!taskExists)
        {
            return false;
        }

        return result.StdOut.Contains("<Command>powershell.exe</Command>", StringComparison.OrdinalIgnoreCase) &&
               result.StdOut.Contains(CodexPlusPlusTaskHiddenWindowArgument, StringComparison.OrdinalIgnoreCase) &&
               result.StdOut.Contains("<RunLevel>HighestAvailable</RunLevel>", StringComparison.OrdinalIgnoreCase) &&
               result.StdOut.Contains(
                   Path.GetFileName(CodexPlusPlusTaskLauncherScriptPath()),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessRunResult RunProcess(string fileName, string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动进程：{fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
            catch
            {
                // Process may already have exited.
            }
            return new ProcessRunResult(
                -1,
                GetCompletedProcessOutput(stdoutTask),
                GetCompletedProcessOutput(stderrTask));
        }

        return new ProcessRunResult(
            process.ExitCode,
            GetCompletedProcessOutput(stdoutTask),
            GetCompletedProcessOutput(stderrTask));
    }

    private static string GetCompletedProcessOutput(Task<string> outputTask)
    {
        try
        {
            return outputTask.Wait(TimeSpan.FromSeconds(1)) ? outputTask.Result : "";
        }
        catch
        {
            return "";
        }
    }

    private static string BuildCodexPlusPlusTaskInstallerScript()
    {
        return $$"""
$ErrorActionPreference = 'Stop'
$taskName = '{{CodexPlusPlusTaskName}}'
$launcherPath = '{{CodexPlusPlusTaskLauncherScriptPath().Replace("'", "''")}}'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -NoLogo -NonInteractive {{CodexPlusPlusTaskHiddenWindowArgument}} -ExecutionPolicy Bypass -File "' + $launcherPath + '"')
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
$trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddYears(10))
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Trigger $trigger -Force | Out-Null
""";
    }

    private static string BuildCodexPlusPlusTaskLauncherScript()
    {
        return $$"""
$ErrorActionPreference = 'Stop'
$requestPath = '{{CodexPlusPlusTaskRequestPath().Replace("'", "''")}}'
$resultPath = '{{CodexPlusPlusTaskResultPath().Replace("'", "''")}}'
$launchStartedAt = $null
$process = $null
$window = $null
$enhancementsReady = $false
$launcherExited = $false
$launcherExitCode = $null
$launcherStandardError = ''
$requestId = ''
$operation = 'launch'
$windowHiderAvailable = $false
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CodexAccountManagerWindowHider
{
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@ -ErrorAction Stop
    $windowHiderAvailable = $true
}
catch {
    $windowHiderAvailable = $false
}
function Hide-CodexPlusPlusManagerWindows {
    if (-not $windowHiderAvailable) {
        return
    }
    Get-Process -Name 'codex-plus-plus-manager' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.MainWindowHandle -ne [IntPtr]::Zero) {
                [void][CodexAccountManagerWindowHider]::ShowWindow($_.MainWindowHandle, 0)
            }
        }
        catch {
        }
    }
}
function Test-LoopbackPortAvailable([int]$Port) {
    $listener = $null
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $listener) {
            $listener.Stop()
        }
    }
}
function Get-CurrentLaunchRequest {
    if (-not (Test-Path -LiteralPath $requestPath -PathType Leaf)) {
        throw "Codex++ launch request disappeared."
    }
    return Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
}
function Assert-LaunchRequestCurrent([string]$ExpectedRequestId, [DateTimeOffset]$ExpiresAtUtc) {
    if ([DateTimeOffset]::UtcNow -gt $ExpiresAtUtc) {
        throw "Codex++ launch request expired before execution."
    }
    $currentRequest = Get-CurrentLaunchRequest
    if ([string]$currentRequest.requestId -ne $ExpectedRequestId -or [string]$currentRequest.operation -ne 'launch') {
        throw "Codex++ launch request was superseded by a newer generation."
    }
}
function Get-SnapshotProcess($Target) {
    try {
        $candidate = [System.Diagnostics.Process]::GetProcessById([int]$Target.processId)
        $protectedNames = @('CodexAccountManager', 'WindowsTerminal', 'powershell', 'pwsh', 'cmd', 'conhost', 'OpenConsole')
        $candidateName = [string]$candidate.ProcessName
        $expectedPath = [string]$Target.executablePath
        $actualPath = [string]$candidate.MainModule.FileName
        if ($candidate.HasExited -or
            [int]$candidate.Id -eq [int]$request.managerProcessId -or
            $protectedNames -icontains $candidateName -or
            $candidateName -ine [string]$Target.processName -or
            $candidate.StartTime.ToUniversalTime().Ticks -ne [long]$Target.startTimeUtcTicks -or
            [string]::IsNullOrWhiteSpace($expectedPath) -or
            [string]::IsNullOrWhiteSpace($actualPath) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath($actualPath),
                [System.IO.Path]::GetFullPath($expectedPath),
                [StringComparison]::OrdinalIgnoreCase)) {
            $candidate.Dispose()
            return $null
        }
        return $candidate
    }
    catch {
        return $null
    }
}
try {
    if (-not (Test-Path -LiteralPath $requestPath -PathType Leaf)) {
        throw "Missing Codex++ launch request: $requestPath"
    }
    $request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
    $requestId = [string]$request.requestId
    $operation = [string]$request.operation
    if ([string]::IsNullOrWhiteSpace($operation)) {
        $operation = 'launch'
    }
    # OPEN_THREAD_OPERATION_BEGIN
    if ($operation -eq 'openThread') {
        $threadUrl = [string]$request.threadUrl
        $threadPrefix = 'codex://threads/'
        if ([string]::IsNullOrWhiteSpace($requestId)) {
            throw "Missing open-thread request id."
        }
        if ([string]::IsNullOrWhiteSpace($threadUrl) -or -not $threadUrl.StartsWith($threadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Invalid Codex task deep link."
        }
        $threadId = $threadUrl.Substring($threadPrefix.Length)
        $parsedThreadId = [Guid]::Empty
        if (-not [Guid]::TryParseExact($threadId, 'D', [ref]$parsedThreadId) -or $threadUrl -ne ($threadPrefix + $threadId)) {
            throw "Invalid Codex task id."
        }
        $threadStartInfo = New-Object System.Diagnostics.ProcessStartInfo
        $threadStartInfo.FileName = $threadUrl
        $threadStartInfo.UseShellExecute = $true
        [void][System.Diagnostics.Process]::Start($threadStartInfo)
        @{ succeeded = $true; operation = $operation; requestId = $requestId; threadOpened = $true; processId = 0; launcherProcessId = 0; enhancementsReady = $false; newThreadOpened = $false; error = ''; completedAtUtc = [DateTime]::UtcNow.ToString('o') } |
            ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
        exit 0
    }
    # OPEN_THREAD_OPERATION_END
    if ($operation -ne 'launch') {
        throw "Unsupported Codex++ task operation: $operation"
    }
    if ([string]::IsNullOrWhiteSpace($requestId)) {
        throw "Missing Codex++ launch request id."
    }
    $expiresAtUtc = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$request.expiresAtUtc, [ref]$expiresAtUtc)) {
        throw "Missing or invalid Codex++ launch request expiry."
    }
    Assert-LaunchRequestCurrent $requestId $expiresAtUtc
    $exe = [string]$request.codexPlusPlusPath
    if ([string]::IsNullOrWhiteSpace($exe) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Missing Codex++ launcher: $exe"
    }
    $projectPath = [string]$request.projectPath
    if ([string]::IsNullOrWhiteSpace($projectPath) -or -not (Test-Path -LiteralPath $projectPath -PathType Container)) {
        $projectPath = Split-Path -Parent $exe
    }
    $newThreadUrl = [string]$request.newThreadUrl
    if ([string]::IsNullOrWhiteSpace($newThreadUrl) -or -not $newThreadUrl.StartsWith('codex://threads/new?path=')) {
        throw "Missing Codex new-task deep link."
    }
    $codexHome = [string]$request.codexHome
    if ([string]::IsNullOrWhiteSpace($codexHome) -or -not (Test-Path -LiteralPath $codexHome -PathType Container)) {
        throw "Missing account CODEX_HOME: $codexHome"
    }
    $codexSqliteHome = [string]$request.codexSqliteHome
    if ([string]::IsNullOrWhiteSpace($codexSqliteHome)) {
        $codexSqliteHome = $codexHome
    }
    if (-not (Test-Path -LiteralPath $codexSqliteHome -PathType Container)) {
        throw "Missing shared CODEX_SQLITE_HOME: $codexSqliteHome"
    }
    # Only a real credential change may close the previous client. Targets are immutable
    # PID/start-time snapshots captured before projection; never enumerate by process name
    # here, because a delayed task must not terminate a newer Codex generation.
    $switchRequired = [bool]$request.switchRequired
    $shutdownTargets = @($request.shutdownTargets)
    $launcherGuardHash = [uint16]0
    foreach ($usernameByte in [System.Text.Encoding]::UTF8.GetBytes([string]$env:USERNAME)) {
        $launcherGuardHash = [uint16](($launcherGuardHash + [uint16]$usernameByte) % 65536)
    }
    $launcherGuardPort = {{CodexPlusPlusLauncherGuardPortBase}} + ($launcherGuardHash % 1000)
    $shutdownDeadline = (Get-Date).AddMilliseconds({{(int)CodexPlusPlusShutdownDrainTimeout.TotalMilliseconds}})
    $remainingTargets = @()
    $remainingTargetCount = 0
    $guardAvailable = $false
    do {
        Assert-LaunchRequestCurrent $requestId $expiresAtUtc
        $remainingTargets = @()
        if ($switchRequired) {
            foreach ($target in $shutdownTargets) {
                $targetProcess = Get-SnapshotProcess $target
                if ($null -ne $targetProcess) {
                    $remainingTargets += $targetProcess
                }
            }
            if ($remainingTargets.Count -gt 0) {
                $remainingTargets | Stop-Process -Force -ErrorAction SilentlyContinue
                $remainingTargets | ForEach-Object { $_.Dispose() }
                Start-Sleep -Milliseconds 100
            }
        }
        $remainingTargets = @()
        if ($switchRequired) {
            foreach ($target in $shutdownTargets) {
                $targetProcess = Get-SnapshotProcess $target
                if ($null -ne $targetProcess) {
                    $remainingTargets += $targetProcess
                }
            }
        }
        $guardAvailable = Test-LoopbackPortAvailable $launcherGuardPort
        $remainingTargetCount = $remainingTargets.Count
        $remainingTargets | ForEach-Object { $_.Dispose() }
        $remainingTargets = @()
        if ($remainingTargetCount -eq 0 -and $guardAvailable) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $shutdownDeadline)
    if ($remainingTargetCount -ne 0 -or -not $guardAvailable) {
        throw "Previous snapshotted Codex/Codex++ processes or launcher guard port $launcherGuardPort did not release before restart."
    }
    Assert-LaunchRequestCurrent $requestId $expiresAtUtc
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exe
    $startInfo.WorkingDirectory = $projectPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $codexAppDir = [string]$request.codexAppDir
    if (-not [string]::IsNullOrWhiteSpace($codexAppDir)) {
        $startInfo.Arguments = '--app-path "' + $codexAppDir.Replace('"', '\"') + '"'
    }
    $proxyUri = [string]$request.proxyUri
    if (-not [string]::IsNullOrWhiteSpace($proxyUri)) {
        $startInfo.Environment['HTTP_PROXY'] = $proxyUri
        $startInfo.Environment['HTTPS_PROXY'] = $proxyUri
        $startInfo.Environment['ALL_PROXY'] = $proxyUri
        $startInfo.Environment['http_proxy'] = $proxyUri
        $startInfo.Environment['https_proxy'] = $proxyUri
        $startInfo.Environment['all_proxy'] = $proxyUri
        foreach ($bypassName in @('NO_PROXY','no_proxy')) {
            $bypassEntries = @(([string]$startInfo.Environment[$bypassName]).Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
            foreach ($loopback in '{{CodexLoopbackProxyBypass}}'.Split(',')) {
                if ($bypassEntries -notcontains $loopback) {
                    $bypassEntries += $loopback
                }
            }
            $startInfo.Environment[$bypassName] = $bypassEntries -join ','
        }
    }
    $startInfo.Environment['CODEX_HOME'] = $codexHome
    $startInfo.Environment['CODEX_SQLITE_HOME'] = $codexSqliteHome
    $startInfo.Environment['CODEX_PROJECT_PATH'] = $projectPath
    $launchStartedAt = Get-Date
    $launchStartedAtMs = ([DateTimeOffset]$launchStartedAt).ToUnixTimeMilliseconds()
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $launcherExited = $process.WaitForExit({{(int)CodexPlusPlusLauncherExitProbeTimeout.TotalMilliseconds}})
    if ($launcherExited) {
        $launcherExitCode = [int]$process.ExitCode
        [void]$standardOutputTask.Result
        $launcherStandardError = [string]$standardErrorTask.Result
        if ($launcherExitCode -ne 0) {
            $detail = $launcherStandardError.Trim()
            if ([string]::IsNullOrWhiteSpace($detail)) {
                throw "Codex++ launcher exited with code $launcherExitCode."
            }
            throw "Codex++ launcher exited with code $launcherExitCode`: $detail"
        }
    }
    @{ succeeded = $true; operation = $operation; requestId = $requestId; launchAccepted = $true; threadOpened = $false; processId = 0; launcherProcessId = $process.Id; launcherExited = $launcherExited; launcherExitCode = $launcherExitCode; launcherStandardError = $launcherStandardError; enhancementsReady = $false; newThreadOpened = $false; error = ''; completedAtUtc = [DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    # The result is already visible to Account Manager.  Keep this hidden helper alive briefly
    # only to suppress Codex++'s manager window while the launcher continues in the background;
    # this loop does not gate the account-switch button or the Codex window.
    $hideDeadline = (Get-Date).AddSeconds(14)
    while ((Get-Date) -lt $hideDeadline) {
        Hide-CodexPlusPlusManagerWindows
        Start-Sleep -Milliseconds 100
    }
    exit 0
}
catch {
    @{ succeeded = $false; operation = $operation; requestId = $requestId; threadOpened = $false; processId = 0; launcherProcessId = 0; launcherExited = $launcherExited; launcherExitCode = $launcherExitCode; launcherStandardError = $launcherStandardError; enhancementsReady = $false; newThreadOpened = $false; error = $_.Exception.Message; completedAtUtc = [DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    exit 1
}
""";
    }

    internal static void ValidateCodexPlusPlusTaskLauncherScript()
    {
        var script = BuildCodexPlusPlusTaskLauncherScript();
        var installerScript = BuildCodexPlusPlusTaskInstallerScript();
        if (RequiresWindowsClientShutdown(sharedProfileAlreadySelected: true) ||
            !RequiresWindowsClientShutdown(sharedProfileAlreadySelected: false))
        {
            throw new InvalidOperationException(
                "Reusing the selected account must not stop Codex; only a real profile switch may stop it.");
        }
        if (CodexPlusPlusLaunchReadyTimeout < TimeSpan.FromSeconds(90) ||
            CodexPlusPlusLauncherExitProbeTimeout < TimeSpan.FromSeconds(1) ||
            CodexPlusPlusLauncherExitProbeTimeout > TimeSpan.FromSeconds(12) ||
            CodexPlusPlusShutdownDrainTimeout > TimeSpan.FromSeconds(12) ||
            CodexPlusPlusTaskRequestLifetime < TimeSpan.FromSeconds(30) ||
            CodexPlusPlusTaskRequestLifetime > TimeSpan.FromMinutes(2) ||
            CalculateCodexPlusPlusLauncherGuardPort("安") != 57860)
        {
            throw new InvalidOperationException(
                "Codex++ launch timing must allow slow packaged-app discovery while keeping the launcher exit probe bounded.");
        }
        if (script.Contains("ArgumentList.Add", StringComparison.Ordinal) ||
            !script.Contains("$startInfo.Arguments", StringComparison.Ordinal) ||
            !script.Contains("$process = [System.Diagnostics.Process]::Start($startInfo)", StringComparison.Ordinal) ||
            !script.Contains("launchAccepted = $true", StringComparison.Ordinal) ||
            !script.Contains("launcherProcessId = $process.Id", StringComparison.Ordinal) ||
            !script.Contains("$startInfo.RedirectStandardError = $true", StringComparison.Ordinal) ||
            !script.Contains("$launcherExited = $process.WaitForExit(1500)", StringComparison.Ordinal) ||
            !script.Contains("launcherExitCode = $launcherExitCode", StringComparison.Ordinal) ||
            !script.Contains("[System.Text.Encoding]::UTF8.GetBytes([string]$env:USERNAME)", StringComparison.Ordinal) ||
            !script.Contains("Test-LoopbackPortAvailable $launcherGuardPort", StringComparison.Ordinal) ||
            !script.Contains("$remainingTargetCount -eq 0 -and $guardAvailable", StringComparison.Ordinal) ||
            !script.Contains("Assert-LaunchRequestCurrent $requestId $expiresAtUtc", StringComparison.Ordinal) ||
            !script.Contains("launch request expired before execution", StringComparison.Ordinal) ||
            !script.Contains("launch request was superseded by a newer generation", StringComparison.Ordinal) ||
            !script.Contains("[System.Diagnostics.Process]::GetProcessById([int]$Target.processId)", StringComparison.Ordinal) ||
            !script.Contains("$candidate.StartTime.ToUniversalTime().Ticks -ne [long]$Target.startTimeUtcTicks", StringComparison.Ordinal) ||
            !script.Contains("$protectedNames -icontains $candidateName", StringComparison.Ordinal) ||
            !script.Contains("[int]$candidate.Id -eq [int]$request.managerProcessId", StringComparison.Ordinal) ||
            !script.Contains("[System.IO.Path]::GetFullPath($expectedPath)", StringComparison.Ordinal) ||
            !script.Contains("$switchRequired = [bool]$request.switchRequired", StringComparison.Ordinal) ||
            !script.Contains("$shutdownTargets = @($request.shutdownTargets)", StringComparison.Ordinal) ||
            script.Contains("Start-Sleep -Milliseconds 180", StringComparison.Ordinal) ||
            !script.Contains("$startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden", StringComparison.Ordinal) ||
            !script.Contains("Hide-CodexPlusPlusManagerWindows", StringComparison.Ordinal) ||
            !script.Contains("ShowWindow($_.MainWindowHandle, 0)", StringComparison.Ordinal) ||
            !script.Contains("this loop does not gate the account-switch button", StringComparison.Ordinal) ||
            script.Contains("$enhancementDeadline", StringComparison.Ordinal) ||
            script.Contains("Codex++ page enhancements did not become ready", StringComparison.Ordinal) ||
            script.Contains("$uriStartInfo", StringComparison.Ordinal) ||
            !script.Contains("Stop-Process -Force", StringComparison.Ordinal) ||
            !script.Contains("$launchStartedAt = $null", StringComparison.Ordinal) ||
            !script.Contains("succeeded = $false", StringComparison.Ordinal) ||
            script.Contains("Environment.Remove", StringComparison.Ordinal) ||
            ProxyEnvironmentVariableNames.Any(name =>
                !script.Contains("$startInfo.Environment['" + name + "'] = $proxyUri", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Codex++ scheduled-task launcher is not compatible with Windows PowerShell 5.1, cannot report failures, or does not preserve all proxy aliases.");
        }

        const string openThreadStartMarker = "# OPEN_THREAD_OPERATION_BEGIN";
        const string openThreadEndMarker = "# OPEN_THREAD_OPERATION_END";
        var openThreadStart = script.IndexOf(openThreadStartMarker, StringComparison.Ordinal);
        var openThreadEnd = script.IndexOf(openThreadEndMarker, StringComparison.Ordinal);
        if (openThreadStart < 0 || openThreadEnd <= openThreadStart)
        {
            throw new InvalidOperationException(
                "Codex++ scheduled-task launcher is missing the isolated open-thread operation.");
        }

        var openThreadBlock = script[openThreadStart..(openThreadEnd + openThreadEndMarker.Length)];
        var forbiddenOpenThreadOperations = new[]
        {
            "Stop-Process",
            "CODEX_HOME",
            "CODEX_SQLITE_HOME",
            "codexPlusPlusPath",
            "codexHome",
            "projectPath",
            "$exe",
            "auth.json",
            "config.toml"
        };
        if (!openThreadBlock.Contains("$operation -eq 'openThread'", StringComparison.Ordinal) ||
            !openThreadBlock.Contains("[Guid]::TryParseExact", StringComparison.Ordinal) ||
            !openThreadBlock.Contains("$threadStartInfo.UseShellExecute = $true", StringComparison.Ordinal) ||
            !openThreadBlock.Contains("threadOpened = $true", StringComparison.Ordinal) ||
            !openThreadBlock.Contains("requestId = $requestId", StringComparison.Ordinal) ||
            forbiddenOpenThreadOperations.Any(forbidden =>
                openThreadBlock.Contains(forbidden, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Codex++ open-thread operation must only validate a GUID deep link, use ShellExecute, and report its result without switching accounts or stopping processes.");
        }

        if (!installerScript.Contains("New-ScheduledTaskAction", StringComparison.Ordinal) ||
            !installerScript.Contains(CodexPlusPlusTaskHiddenWindowArgument, StringComparison.Ordinal) ||
            !installerScript.Contains("-NonInteractive", StringComparison.Ordinal) ||
            !installerScript.Contains("-RunLevel Highest", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Codex++ scheduled task must launch Windows PowerShell at highest privilege, non-interactively, and with a hidden window.");
        }

        ValidatePowerShellScriptSyntax(script);
        ValidatePowerShellScriptSyntax(installerScript);
    }

    internal static int CalculateCodexPlusPlusLauncherGuardPort(string userName)
    {
        ushort hash = 0;
        foreach (var value in Encoding.UTF8.GetBytes(userName ?? ""))
        {
            hash = unchecked((ushort)(hash + value));
        }

        return CodexPlusPlusLauncherGuardPortBase + hash % 1000;
    }

    private static void ValidatePowerShellScriptSyntax(string script)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-script-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(tempPath, script, new UTF8Encoding(false));
            var parserCommand =
                "$ErrorActionPreference = 'Stop'\n" +
                "$scriptText = [IO.File]::ReadAllText(" + ToSingleQuoted(tempPath) + ")\n" +
                "[void][ScriptBlock]::Create($scriptText)\n";
            var encodedParserCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(parserCommand));
            var result = RunProcess(
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encodedParserCommand,
                TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Codex++ scheduled-task launcher is not valid Windows PowerShell 5.1 syntax.\n" +
                    result.StdErr);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    internal static void ValidateProxyEnvironmentProjection()
    {
        const string proxyUri = "http://127.0.0.1:10808";
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false
        };
        SetProxyEnvironment(startInfo, proxyUri);
        if (ProxyEnvironmentVariableNames.Any(name =>
                !startInfo.Environment.TryGetValue(name, out var value) ||
                !string.Equals(value, proxyUri, StringComparison.Ordinal)) ||
            ProxyBypassEnvironmentVariableNames.Any(name =>
                !startInfo.Environment.TryGetValue(name, out var value) ||
                CodexLoopbackProxyBypass.Split(',').Any(loopback =>
                    !(value ?? "").Split(',').Contains(loopback, StringComparer.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                "Codex process proxy projection did not set all aliases or preserve direct loopback bridge traffic.");
        }

        var settings = new AppSettings
        {
            PatGatewayProxyAddress = "127.0.0.1",
            PatGatewayProxyPort = 10808,
            PatGatewayProxyAutoDetect = true,
            PatGatewayProxyScheme = "http"
        };
        if (!string.Equals(BuildPatGatewayProxyUri(settings), proxyUri, StringComparison.Ordinal) ||
            !TryParseProxyEndpoint(proxyUri, out var address, out var port, out var scheme) ||
            address != "127.0.0.1" ||
            port != 10808 ||
            scheme != "http" ||
            !IsLoopbackProxyUri(proxyUri) ||
            !IsLoopbackProxyUri("http://localhost.:8317") ||
            !IsLoopbackProxyUri("http://[::1]:8317") ||
            !IsLoopbackProxyUri("http://[::ffff:127.0.0.1]:8317") ||
            IsLoopbackProxyUri("http://192.0.2.1:10808") ||
            LocalProxyDetector.BuildCandidatePorts(LocalPatGateway.Port)
                .Contains(LocalPatGateway.Port))
        {
            throw new InvalidOperationException(
                "Structured proxy settings or loopback-only detector candidates are invalid.");
        }
    }

    private static string CodexPlusPlusTaskDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexAccountManager");
    }

    private static string CodexPlusPlusTaskLauncherScriptPath()
    {
        return Path.Combine(CodexPlusPlusTaskDirectory(), "Start-CodexPlusPlusLauncher.ps1");
    }

    private static string CodexPlusPlusTaskInstallerPath()
    {
        return Path.Combine(CodexPlusPlusTaskDirectory(), "Install-CodexPlusPlusLauncherTask.ps1");
    }

    private static string CodexPlusPlusTaskRequestPath()
    {
        return Path.Combine(CodexPlusPlusTaskDirectory(), "codex-plus-plus-launch.json");
    }

    private static string CodexPlusPlusTaskResultPath()
    {
        return Path.Combine(CodexPlusPlusTaskDirectory(), "codex-plus-plus-launch-result.json");
    }

    private static string CodexPlusPlusLaunchDiagnosticPath()
    {
        return Path.Combine(CodexPlusPlusTaskDirectory(), "codex-plus-plus-launch-diagnostics.log");
    }

    private static void WriteCodexPlusPlusLaunchDiagnostic(string eventName, string detail)
    {
        try
        {
            var safeEventName = Regex.Replace(eventName ?? "launch", "[^A-Za-z0-9_.-]", "_");
            var safeDetail = MaskSensitive(detail ?? "")
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            if (safeDetail.Length > 4000)
            {
                safeDetail = safeDetail[..4000] + "…";
            }

            lock (CodexPlusPlusLaunchDiagnosticLock)
            {
                Directory.CreateDirectory(CodexPlusPlusTaskDirectory());
                File.AppendAllText(
                    CodexPlusPlusLaunchDiagnosticPath(),
                    $"{DateTime.UtcNow:O}\t{safeEventName}\t{safeDetail}{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics must never replace the original launch result.
        }
    }

    public static string GetDefaultCodexHome()
    {
        var overrideHome = Environment.GetEnvironmentVariable(SharedCodexHomeOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideHome))
        {
            return Path.GetFullPath(overrideHome);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public static string? ResolveCodexWindowsClientPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_WINDOWS_CLIENT_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var registeredCandidates = new List<string>();
        try
        {
            const string packagesKeyPath =
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            using var packagesKey = Registry.CurrentUser.OpenSubKey(packagesKeyPath);
            if (packagesKey != null)
            {
                foreach (var packageName in packagesKey.GetSubKeyNames()
                             .Where(name => name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)))
                {
                    using var packageKey = packagesKey.OpenSubKey(packageName);
                    var packageRoot = packageKey?.GetValue("PackageRootFolder") as string;
                    if (string.IsNullOrWhiteSpace(packageRoot))
                    {
                        continue;
                    }

                    var candidate = Path.Combine(packageRoot, "app", "ChatGPT.exe");
                    if (File.Exists(candidate))
                    {
                        registeredCandidates.Add(candidate);
                    }
                }
            }
        }
        catch
        {
            // Fall through to the legacy directory probe below.
        }

        var registeredClient = registeredCandidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(registeredClient))
        {
            return registeredClient;
        }

        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        if (!Directory.Exists(windowsApps))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateDirectories(windowsApps, "OpenAI.Codex_*")
                .Select(dir => Path.Combine(dir, "app", "ChatGPT.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetCodexWindowsClientAppDirectory()
    {
        var clientPath = ResolveCodexWindowsClientPath();
        if (string.IsNullOrWhiteSpace(clientPath) || !File.Exists(clientPath))
        {
            return null;
        }

        try
        {
            var appDirectory = Path.GetDirectoryName(Path.GetFullPath(clientPath));
            if (string.IsNullOrWhiteSpace(appDirectory) ||
                !Directory.Exists(appDirectory))
            {
                return null;
            }

            var fileName = Path.GetFileName(clientPath);
            return fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)
                ? appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : null;
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? ResolveCodexWindowsClientAppUserModelId()
    {
        var clientPath = ResolveCodexWindowsClientPath();
        if (string.IsNullOrWhiteSpace(clientPath) || !File.Exists(clientPath))
        {
            return null;
        }

        try
        {
            var appDirectory = Path.GetDirectoryName(clientPath);
            var packageRoot = string.IsNullOrWhiteSpace(appDirectory)
                ? null
                : Directory.GetParent(appDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                return null;
            }

            var manifestPath = Path.Combine(packageRoot, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var manifest = XDocument.Load(manifestPath, LoadOptions.None);
            var identityName = manifest.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Identity")?
                .Attribute("Name")?
                .Value;
            var applicationId = manifest.Descendants()
                .Where(element => element.Name.LocalName == "Application")
                .FirstOrDefault(element =>
                {
                    var executable = element.Attribute("Executable")?.Value;
                    if (string.IsNullOrWhiteSpace(executable))
                    {
                        return false;
                    }

                    var candidate = Path.GetFullPath(Path.Combine(
                        packageRoot,
                        executable.Replace('/', Path.DirectorySeparatorChar)));
                    return PathsEqual(candidate, clientPath);
                })?
                .Attribute("Id")?
                .Value;
            var packageFolderName = Path.GetFileName(packageRoot);
            var publisherSeparator = packageFolderName.LastIndexOf("__", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(identityName) ||
                string.IsNullOrWhiteSpace(applicationId) ||
                publisherSeparator < 0 ||
                publisherSeparator + 2 >= packageFolderName.Length)
            {
                return null;
            }

            var publisherId = packageFolderName[(publisherSeparator + 2)..];
            var appUserModelId = identityName + "_" + publisherId + "!" + applicationId;
            return Regex.IsMatch(
                appUserModelId,
                "^[A-Za-z0-9._-]{1,128}![A-Za-z0-9._-]{1,64}$",
                RegexOptions.CultureInvariant)
                ? appUserModelId
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    internal static void ValidateWindowsClientResolution()
    {
        var clientPath = ResolveCodexWindowsClientPath();
        if (string.IsNullOrWhiteSpace(clientPath) ||
            !File.Exists(clientPath) ||
            !Path.GetFileName(clientPath).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The installed Codex ChatGPT.exe could not be resolved.");
        }

        var appUserModelId = ResolveCodexWindowsClientAppUserModelId();
        if (string.IsNullOrWhiteSpace(appUserModelId) ||
            !appUserModelId.EndsWith("!App", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The installed Codex package AppUserModelId could not be resolved.");
        }
    }

    public static string? ResolveCodexPlusPlusLauncherPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_PLUS_PLUS_LAUNCHER_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Codex++",
                CodexPlusPlusLauncherFileName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool EnsureCodexPlusPlusSafeSettings(string? resolvedCodexAppDirectory)
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex-session-delete",
            "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var root = File.Exists(settingsPath)
            ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var changed = false;
        var serviceTierControlsChanged = root["codexAppServiceTierControls"]?.GetValue<bool>() != true;
        void SetBoolean(string key, bool value)
        {
            if (root[key]?.GetValue<bool>() == value)
            {
                return;
            }

            root[key] = value;
            changed = true;
        }
        void SetString(string key, string value)
        {
            if (string.Equals(
                    root[key]?.GetValue<string>(),
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            root[key] = value;
            changed = true;
        }

        // The manager already rewrites the shared SQLite provider safely. Enabling Codex++
        // provider sync here would rescan every rollout on every launch (hundreds of MB).
        SetBoolean("providerSyncEnabled", false);
        SetBoolean("enhancementsEnabled", true);
        SetBoolean("codexAppSessionDelete", true);
        SetBoolean("codexAppMarkdownExport", true);
        SetBoolean("codexAppProjectMove", true);
        SetBoolean("codexAppFastStartup", true);
        SetBoolean("codexAppModelWhitelistUnlock", false);
        SetBoolean("codexAppPluginMarketplaceUnlock", false);
        SetBoolean("codexAppPluginAutoExpand", false);
        // Codex++ already implements its own Standard/Fast UI and priority request rewrite.
        // Keep that native page control enabled; the CDP response bridge is reserved for the
        // unmodified official client and is not launched on the Codex++ path.
        SetBoolean("codexAppServiceTierControls", true);
        SetBoolean("codexAppStepwiseEnabled", false);
        if (!string.IsNullOrWhiteSpace(resolvedCodexAppDirectory) &&
            Directory.Exists(resolvedCodexAppDirectory))
        {
            SetString("codexAppPath", Path.GetFullPath(resolvedCodexAppDirectory));
        }
        if (!changed)
        {
            return serviceTierControlsChanged;
        }

        WriteTextAtomically(
            settingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return serviceTierControlsChanged;
    }

    private static void SanitizeCuratedPluginManifests(string codexHome)
    {
        var pluginsRoot = Path.Combine(codexHome, ".tmp", "plugins", "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            return;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(
                     pluginsRoot,
                     "plugin.json",
                     SearchOption.AllDirectories))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
                var interfaceObject = root?["interface"] as JsonObject;
                if (root == null || interfaceObject?["defaultPrompt"] is not JsonArray prompts)
                {
                    continue;
                }

                var changed = false;
                for (var index = 0; index < prompts.Count; index++)
                {
                    if (prompts[index] is not JsonValue promptValue)
                    {
                        continue;
                    }

                    string prompt;
                    try
                    {
                        prompt = promptValue.GetValue<string>();
                    }
                    catch
                    {
                        continue;
                    }

                    if (prompt.Length <= MaxPluginDefaultPromptLength)
                    {
                        continue;
                    }

                    prompts[index] = prompt[..MaxPluginDefaultPromptLength].TrimEnd();
                    changed = true;
                }

                if (changed)
                {
                    WriteTextAtomically(
                        manifestPath,
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch
            {
                // Invalid marketplace entries are ignored by Codex as well.
            }
        }
    }

    public static string? ResolveCodexCliCommand()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_SWITCHER_CODEX_COMMAND");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var appCliRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");
        if (Directory.Exists(appCliRoot))
        {
            try
            {
                var appCli = Directory
                    .EnumerateFiles(appCliRoot, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(appCli))
                {
                    return appCli;
                }
            }
            catch
            {
                // Fall through to the npm shim if the app cache cannot be enumerated.
            }
        }

        // Keep the manager aligned with the installed desktop protocol. The packaged CLI is a
        // compatibility fallback for machines where Codex has not populated its app cache yet.
        foreach (var root in GetCandidateManagerRoots())
        {
            var localCli = Path.Combine(root, LocalCodexCliRelativePath);
            if (File.Exists(localCli))
            {
                return localCli;
            }
        }

        var npmShim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd");
        return File.Exists(npmShim) ? npmShim : null;
    }

    internal static IReadOnlyList<string> ResolveThreadSectionCodexCliCandidates()
    {
        // Thread-section RPCs are newer than several desktop app-server builds. Always try the
        // release-pinned runtime shipped with Account Manager before any desktop cache entry.
        var packagedCandidates = GetThreadSectionPackagedManagerRoots()
            .Select(root => Path.Combine(root, LocalCodexCliRelativePath));

        var desktopCandidates = new List<string>();
        var appCliRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");
        if (Directory.Exists(appCliRoot))
        {
            try
            {
                desktopCandidates.AddRange(Directory
                    .EnumerateFiles(appCliRoot, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                // Keep the packaged and compatibility candidates usable if the cache is busy.
            }
        }

        var npmShim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd");
        var compatibilityCandidates = new[]
        {
            Environment.GetEnvironmentVariable("CODEX_SWITCHER_CODEX_COMMAND"),
            npmShim
        };

        return MergeThreadSectionCodexCliCandidates(
            File.Exists,
            packagedCandidates,
            desktopCandidates,
            compatibilityCandidates);
    }

    private static IReadOnlyList<string> GetThreadSectionPackagedManagerRoots() =>
        BuildThreadSectionPackagedManagerRoots(AppContext.BaseDirectory);

    private static IReadOnlyList<string> BuildThreadSectionPackagedManagerRoots(
        string appBaseDirectory)
    {
        // Never search the current working directory or a configurable manager home here. The
        // app may be launched while an untrusted repository is the CWD. Six fixed levels cover
        // both an installed payload and the development bin/<configuration>/<tfm> layout.
        var roots = new List<string>();
        string? current;
        try
        {
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appBaseDirectory));
        }
        catch
        {
            return roots;
        }

        for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            roots.Add(current);
            current = Directory.GetParent(current)?.FullName;
        }
        return roots;
    }

    private static IReadOnlyList<string> MergeThreadSectionCodexCliCandidates(
        Func<string, bool> candidateExists,
        params IEnumerable<string?>[] candidateGroups)
    {
        ArgumentNullException.ThrowIfNull(candidateExists);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidateGroups)
        {
            foreach (var candidate in group)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(candidate);
                }
                catch
                {
                    continue;
                }

                if (candidateExists(fullPath) && seen.Add(fullPath))
                {
                    result.Add(fullPath);
                }
            }
        }
        return result;
    }

    internal static void ValidateThreadSectionCliResolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "thread-section-cli-resolution");
        var appBase = Path.Combine(
            root,
            "repository",
            "src",
            "CodexAccountManager",
            "bin",
            "Debug",
            "net10.0-windows");
        var packagedRoots = BuildThreadSectionPackagedManagerRoots(appBase);
        if (packagedRoots.Count != 6 ||
            !Path.GetFullPath(packagedRoots[0]).Equals(
                Path.GetFullPath(appBase),
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(packagedRoots[^1]).Equals(
                Path.Combine(root, "repository"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "聊天目录随包 CLI 根目录没有严格限制在应用目录及固定深度父目录。");
        }

        var packaged = Path.Combine(root, "packaged", "codex.exe");
        var desktopNewest = Path.Combine(root, "desktop-new", "codex.exe");
        var desktopOlder = Path.Combine(root, "desktop-old", "codex.exe");
        var compatibility = Path.Combine(root, "override", "codex.exe");
        var ordered = MergeThreadSectionCodexCliCandidates(
            _ => true,
            new[] { packaged },
            new[] { desktopNewest, packaged, desktopOlder },
            new[] { compatibility, desktopNewest });
        var expected = new[]
        {
            packaged,
            desktopNewest,
            desktopOlder,
            compatibility
        }.Select(Path.GetFullPath);
        if (!ordered.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "聊天目录 CLI 候选没有保持随包优先、桌面逐项回退和路径去重顺序。");
        }
    }

    private static IEnumerable<string> GetCandidateManagerRoots()
    {
        var candidates = new List<string?>();
        candidates.Add(Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME"));
        candidates.Add(Directory.GetCurrentDirectory());
        candidates.Add(AppContext.BaseDirectory);

        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 4 && !string.IsNullOrWhiteSpace(current); i++)
        {
            candidates.Add(current);
            current = Directory.GetParent(current)?.FullName;
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public void RestoreWindowsClientAccountProjection(WindowsClientAccountProjection projection)
    {
        RestoreFile(
            Path.Combine(projection.DefaultCodexHome, AuthFileName),
            projection.AuthBackupPath,
            projection.AuthExisted);
        RestoreFile(
            Path.Combine(projection.DefaultCodexHome, CockpitAuthFileName),
            projection.CockpitAuthBackupPath,
            projection.CockpitAuthExisted);
        RestoreFile(
            Path.Combine(projection.DefaultCodexHome, ConfigFileName),
            projection.ConfigBackupPath,
            projection.ConfigExisted);
        RestoreFile(
            Path.Combine(projection.DefaultCodexHome, DesktopSelectionFileName),
            projection.DesktopSelectionBackupPath,
            projection.DesktopSelectionExisted);
        RestoreFile(
            Path.Combine(projection.DefaultCodexHome, ActiveAccountStateFileName),
            projection.ActiveAccountStateBackupPath,
            projection.ActiveAccountStateExisted);
    }

    private void RestoreWindowsClientSwitchProjection(
        WindowsClientAccountProjection? projection,
        WindowsClientSharedProfileSnapshot? previousSharedProfileSnapshot)
    {
        var failures = new List<Exception>();

        static void Attempt(List<Exception> failures, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (projection != null)
        {
            // Reverse the order used by the switch transaction. Each category is attempted even
            // if another restore fails so one locked optional file cannot prevent auth/config
            // recovery. The operation-owned profile snapshot below is the final exactness guard.
            Attempt(failures, () => RestoreProjectModelOverrides(projection));
            Attempt(failures, () => RestoreDesktopProfileModelState(projection));
            Attempt(failures, () => RestoreDesktopSidebarState(projection));
            Attempt(failures, () => RestoreWindowsClientAccountProjection(projection));
        }

        if (previousSharedProfileSnapshot != null)
        {
            Attempt(
                failures,
                () => RestoreWindowsClientSharedProfileSnapshot(
                    previousSharedProfileSnapshot));
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more files could not be restored after the failed Codex switch.",
                failures);
        }
    }

    private static WindowsClientSharedProfileSnapshot CaptureWindowsClientSharedProfileSnapshot()
    {
        var profileHome = Path.GetFullPath(GetDefaultCodexHome());
        Directory.CreateDirectory(profileHome);
        var managedPaths = new[]
        {
            Path.Combine(profileHome, AuthFileName),
            Path.Combine(profileHome, CockpitAuthFileName),
            Path.Combine(profileHome, ConfigFileName),
            Path.Combine(profileHome, DesktopSelectionFileName),
            Path.Combine(profileHome, ActiveAccountStateFileName)
        };
        return new WindowsClientSharedProfileSnapshot(
            managedPaths.Select(CaptureWindowsClientSharedProfileFile).ToArray());
    }

    private static WindowsClientSharedProfileFileSnapshot CaptureWindowsClientSharedProfileFile(
        string path)
    {
        if (!File.Exists(path))
        {
            return new WindowsClientSharedProfileFileSnapshot(path, false, Array.Empty<byte>());
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new WindowsClientSharedProfileFileSnapshot(path, true, memory.ToArray());
    }

    private static void RestoreWindowsClientSharedProfileSnapshot(
        WindowsClientSharedProfileSnapshot snapshot)
    {
        var failures = new List<Exception>();
        foreach (var file in snapshot.Files)
        {
            try
            {
                if (file.Existed)
                {
                    WriteBytesAtomically(file.Path, file.Contents);
                }
                else if (File.Exists(file.Path))
                {
                    ClearReadOnlyAttribute(file.Path);
                    File.Delete(file.Path);
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "The managed shared profile could not be restored exactly.",
                failures);
        }
    }

    private static bool WindowsClientSharedProfileMatchesSnapshot(
        WindowsClientSharedProfileSnapshot snapshot)
    {
        foreach (var expected in snapshot.Files)
        {
            if (File.Exists(expected.Path) != expected.Existed)
            {
                return false;
            }
            if (!expected.Existed)
            {
                continue;
            }

            try
            {
                var actual = CaptureWindowsClientSharedProfileFile(expected.Path);
                if (!actual.Existed || !actual.Contents.AsSpan().SequenceEqual(expected.Contents))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private static void NormalizeDesktopSidebarState(WindowsClientAccountProjection projection)
    {
        var statePath = Path.Combine(projection.DefaultCodexHome, GlobalStateFileName);
        projection.GlobalStatePath = statePath;
        projection.GlobalStateExisted = File.Exists(statePath);
        var current = projection.GlobalStateExisted ? File.ReadAllText(statePath) : "{}";
        var projected = ProjectDesktopSidebarStateText(current);
        if (string.Equals(current, projected, StringComparison.Ordinal))
        {
            return;
        }

        projection.GlobalStateBackupPath = BackupFileIfPresent(statePath, projection.BackupDirectory);
        WriteTextAtomically(statePath, projected);
        projection.SidebarStateWasNormalized = true;
    }

    internal static string ProjectDesktopSidebarStateText(string current)
    {
        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(current) ? "{}" : current) as JsonObject
            ?? throw new InvalidOperationException("Invalid Codex desktop state JSON.");

        var persisted = GetOrCreateObject(root, "electron-persisted-atom-state");
        var collapsed = GetOrCreateObject(persisted, "sidebar-collapsed-sections-v1");
        collapsed["chats"] = false;
        collapsed["pinned"] = false;
        collapsed["threads"] = false;

        // The current desktop client uses `list` + `updated_at` for the flat,
        // chronological task view. This keeps tasks from every project/account
        // in one visible list instead of hiding them inside project groups.
        var preferences = GetOrCreateObject(persisted, "flat-project-sidebar-preferences-v1");
        preferences["chatSortMode"] = "updated_at";
        preferences["projectSortMode"] = "updated_at";
        preferences["mode"] = "list";
        preferences["initialized"] = true;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void RestoreDesktopSidebarState(WindowsClientAccountProjection projection)
    {
        if (string.IsNullOrWhiteSpace(projection.GlobalStatePath))
        {
            return;
        }

        RestoreFile(
            projection.GlobalStatePath,
            projection.GlobalStateBackupPath,
            projection.GlobalStateExisted);
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static void AlignDesktopProfileModelState(
        AccountRecord account,
        WindowsClientAccountProjection projection)
    {
        // Preserve every historical model and reasoning level while moving each thread onto the
        // provider selected for the active account. Official OAuth uses a dedicated HTTPS-only
        // alias because the built-in OpenAI provider cannot be overridden to disable WebSockets.
        var stateDatabasePath = Path.Combine(projection.DefaultCodexHome, "state_5.sqlite");
        projection.StateDatabaseExisted = File.Exists(stateDatabasePath);
        projection.ThreadRowsUpdated = 0;
        if (projection.StateDatabaseExisted)
        {
            EnsureSqliteProvider();
            var needsProviderMigration = false;
            var primarySourceProvider = account.IsOfficialOAuth
                ? AccountStore.ManagedProviderId
                : "openai";
            var targetProvider = account.IsOfficialOAuth
                ? AccountStore.OfficialOAuthProviderId
                : AccountStore.ManagedProviderId;
            using (var inspect = new SqliteConnection("Data Source=" + stateDatabasePath))
            {
                inspect.Open();
                using var count = inspect.CreateCommand();
                count.CommandText =
                    "SELECT COUNT(*) FROM threads " +
                    "WHERE lower(model_provider) IN " +
                    "($primarySource, $builtInOpenAi, $officialHttps, $legacyToken, $legacyApi) " +
                    "AND lower(model_provider) <> $target;";
                count.Parameters.AddWithValue("$primarySource", primarySourceProvider);
                count.Parameters.AddWithValue("$builtInOpenAi", "openai");
                count.Parameters.AddWithValue("$officialHttps", AccountStore.OfficialOAuthProviderId);
                count.Parameters.AddWithValue("$legacyToken", AccountStore.LegacyAccessTokenProviderId);
                count.Parameters.AddWithValue("$legacyApi", AccountStore.LegacyCompatibleApiProviderId);
                count.Parameters.AddWithValue("$target", targetProvider);
                needsProviderMigration = Convert.ToInt64(count.ExecuteScalar()) > 0;
            }

            if (needsProviderMigration)
            {
                projection.StateDatabaseBackupPath = BackupSqliteDatabase(
                    stateDatabasePath,
                    projection.BackupDirectory);
                using var connection = new SqliteConnection("Data Source=" + stateDatabasePath);
                connection.Open();
                using var update = connection.CreateCommand();
                update.CommandText =
                    "UPDATE threads SET model_provider = $target " +
                    "WHERE lower(model_provider) IN " +
                    "($primarySource, $builtInOpenAi, $officialHttps, $legacyToken, $legacyApi) " +
                    "AND lower(model_provider) <> $target;";
                update.Parameters.AddWithValue("$target", targetProvider);
                update.Parameters.AddWithValue("$primarySource", primarySourceProvider);
                update.Parameters.AddWithValue("$builtInOpenAi", "openai");
                update.Parameters.AddWithValue("$officialHttps", AccountStore.OfficialOAuthProviderId);
                update.Parameters.AddWithValue("$legacyToken", AccountStore.LegacyAccessTokenProviderId);
                update.Parameters.AddWithValue("$legacyApi", AccountStore.LegacyCompatibleApiProviderId);
                projection.ThreadRowsUpdated = update.ExecuteNonQuery();
            }
        }

        var modelCachePath = Path.Combine(projection.DefaultCodexHome, "models_cache.json");
        projection.ModelCacheExisted = File.Exists(modelCachePath);
        if (projection.SharedCredentialsReused)
        {
            // The catalog belongs to the credential already active in the shared profile.
            // Preserving it avoids a redundant model-catalog fetch on every restart.
            return;
        }

        projection.ModelCacheBackupPath = BackupFileIfPresent(modelCachePath, projection.BackupDirectory);
        if (projection.ModelCacheExisted)
        {
            ClearReadOnlyAttribute(modelCachePath);
            File.Delete(modelCachePath);
        }
    }

    private static void RestoreDesktopProfileModelState(WindowsClientAccountProjection projection)
    {
        RestoreSqliteDatabase(
            Path.Combine(projection.DefaultCodexHome, "state_5.sqlite"),
            projection.StateDatabaseBackupPath,
            projection.StateDatabaseExisted);
        RestoreFile(
            Path.Combine(projection.DefaultCodexHome, "models_cache.json"),
            projection.ModelCacheBackupPath,
            projection.ModelCacheExisted);
    }

    private static (string Model, string ReasoningEffort) GetAccountModelSettings(AccountRecord account)
    {
        if (!account.IsCompatibleApi)
        {
            return (AccessTokenModel, AccessTokenReasoningEffort);
        }

        var model = string.IsNullOrWhiteSpace(account.ApiModel)
            ? CompatibleApiDefaultModel
            : account.ApiModel.Trim();
        return (model, CompatibleApiReasoningEffort);
    }

    private static void SanitizeProjectModelOverrides(
        string projectPath,
        WindowsClientAccountProjection projection)
    {
        var configPath = Path.Combine(projectPath, ".codex", ConfigFileName);
        projection.ProjectConfigPath = configPath;
        projection.ProjectConfigExisted = File.Exists(configPath);
        if (!projection.ProjectConfigExisted)
        {
            return;
        }

        var current = File.ReadAllText(configPath);
        var sanitized = RemoveProjectModelOverrides(current);
        if (string.Equals(current, sanitized, StringComparison.Ordinal))
        {
            return;
        }

        var backupPath = Path.Combine(projection.BackupDirectory, "project-config.toml");
        File.Copy(configPath, backupPath, true);
        projection.ProjectConfigBackupPath = backupPath;
        WriteTextAtomically(configPath, sanitized);
        projection.ProjectConfigWasSanitized = true;
    }

    private static void RestoreProjectModelOverrides(WindowsClientAccountProjection projection)
    {
        if (string.IsNullOrWhiteSpace(projection.ProjectConfigPath))
        {
            return;
        }

        RestoreFile(
            projection.ProjectConfigPath,
            projection.ProjectConfigBackupPath,
            projection.ProjectConfigExisted);
    }

    internal static string RemoveProjectModelOverrides(string config)
    {
        var accountKeys = new[]
        {
            "model_provider",
            "model",
            "review_model",
            "model_reasoning_effort",
            "model_context_window",
            "model_auto_compact_token_limit",
            "model_auto_compact_token_limit_scope"
        };
        var accountFeatureKeys = new[]
        {
            "remote_compaction_v2",
            "responses_websockets",
            "responses_websockets_v2"
        };
        var normalized = config.Replace("\r\n", "\n").Replace('\r', '\n');
        var output = new List<string>();
        string? currentSection = null;
        var skipSection = false;

        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                currentSection = trimmed;
                skipSection = trimmed.StartsWith("[model_providers.", StringComparison.OrdinalIgnoreCase);
            }

            if (skipSection)
            {
                continue;
            }

            if (currentSection == null && accountKeys.Any(key => TomlKeyEquals(trimmed, key)))
            {
                continue;
            }

            if (currentSection?.Equals("[features]", StringComparison.OrdinalIgnoreCase) == true &&
                accountFeatureKeys.Any(key => TomlKeyEquals(trimmed, key)))
            {
                continue;
            }

            output.Add(line);
        }

        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[0]))
        {
            output.RemoveAt(0);
        }
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }

        return output.Count == 0
            ? ""
            : string.Join(Environment.NewLine, output) + Environment.NewLine;
    }

    internal static void ValidateDesktopStateRewrite()
    {
        EnsureSqliteProvider();
        var root = Path.Combine(Path.GetTempPath(), "codex-account-manager-state-" + Guid.NewGuid().ToString("N"));
        var backupDirectory = Path.Combine(root, "backups");
        Directory.CreateDirectory(backupDirectory);
        try
        {
            var databasePath = Path.Combine(root, "state_5.sqlite");
            using (var connection = new SqliteConnection("Data Source=" + databasePath))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE threads (id TEXT PRIMARY KEY, model_provider TEXT, model TEXT, reasoning_effort TEXT);" +
                                     "INSERT INTO threads VALUES ('one', 'openai', 'gpt-5.5', 'xhigh');" +
                                     "INSERT INTO threads VALUES ('two', 'openai', 'gpt-5.6-sol', 'ultra');";
                create.ExecuteNonQuery();
            }
            File.WriteAllText(Path.Combine(root, "models_cache.json"), "{}");

            var projection = new WindowsClientAccountProjection
            {
                DefaultCodexHome = root,
                BackupDirectory = backupDirectory
            };
            AlignDesktopProfileModelState(new AccountRecord(), projection);

            using (var connection = new SqliteConnection("Data Source=" + databasePath))
            {
                connection.Open();
                using var verify = connection.CreateCommand();
                verify.CommandText = "SELECT COUNT(*) FROM threads " +
                                     "WHERE model_provider = $provider AND " +
                                     "((model = 'gpt-5.5' AND reasoning_effort = 'xhigh') " +
                                     "OR (model = 'gpt-5.6-sol' AND reasoning_effort = 'ultra'));";
                verify.Parameters.AddWithValue("$provider", AccountStore.AccessTokenProviderId);
                if (Convert.ToInt32(verify.ExecuteScalar()) != 2)
                {
                    throw new InvalidOperationException(
                        "Historical thread models changed or did not migrate to the HTTP provider.");
                }
            }
            if (File.Exists(Path.Combine(root, "models_cache.json")) ||
                projection.ThreadRowsUpdated != 2)
            {
                throw new InvalidOperationException("Desktop model cache reset self-test failed.");
            }

            File.WriteAllText(Path.Combine(root, "models_cache.json"), "{\"reused\":true}");
            var reusedProjection = new WindowsClientAccountProjection
            {
                DefaultCodexHome = root,
                BackupDirectory = backupDirectory,
                SharedCredentialsReused = true
            };
            AlignDesktopProfileModelState(new AccountRecord(), reusedProjection);
            if (!File.Exists(Path.Combine(root, "models_cache.json")) ||
                reusedProjection.ModelCacheBackupPath != null ||
                reusedProjection.StateDatabaseBackupPath != null ||
                reusedProjection.ThreadRowsUpdated != 0)
            {
                throw new InvalidOperationException(
                    "Reused desktop credentials unnecessarily reset the model cache or database.");
            }

            RestoreDesktopProfileModelState(projection);
            if (!File.Exists(Path.Combine(root, "models_cache.json")))
            {
                throw new InvalidOperationException("Desktop model state restore self-test failed.");
            }
            using (var connection = new SqliteConnection("Data Source=" + databasePath))
            {
                connection.Open();
                using var verifyRestore = connection.CreateCommand();
                verifyRestore.CommandText = "SELECT COUNT(*) FROM threads WHERE model_provider = 'openai';";
                if (Convert.ToInt32(verifyRestore.ExecuteScalar()) != 2)
                {
                    throw new InvalidOperationException("Desktop provider migration backup was not restored.");
                }
            }

            var oauthBackupDirectory = Path.Combine(root, "oauth-backups");
            Directory.CreateDirectory(oauthBackupDirectory);
            File.WriteAllText(Path.Combine(root, "models_cache.json"), "{\"oauth\":true}");
            var oauthProjection = new WindowsClientAccountProjection
            {
                DefaultCodexHome = root,
                BackupDirectory = oauthBackupDirectory
            };
            AlignDesktopProfileModelState(
                new AccountRecord { AuthKind = AccountAuthKind.OfficialOAuth },
                oauthProjection);
            using (var connection = new SqliteConnection("Data Source=" + databasePath))
            {
                connection.Open();
                using var verifyOfficial = connection.CreateCommand();
                verifyOfficial.CommandText =
                    "SELECT COUNT(*) FROM threads WHERE model_provider = $provider;";
                verifyOfficial.Parameters.AddWithValue(
                    "$provider",
                    AccountStore.OfficialOAuthProviderId);
                if (Convert.ToInt32(verifyOfficial.ExecuteScalar()) != 2 ||
                    oauthProjection.ThreadRowsUpdated != 2)
                {
                    throw new InvalidOperationException(
                        "Official OAuth threads did not migrate to the HTTPS-only provider.");
                }
            }

            var roundTripBackupDirectory = Path.Combine(root, "round-trip-backups");
            Directory.CreateDirectory(roundTripBackupDirectory);
            File.WriteAllText(Path.Combine(root, "models_cache.json"), "{\"roundTrip\":true}");
            var roundTripProjection = new WindowsClientAccountProjection
            {
                DefaultCodexHome = root,
                BackupDirectory = roundTripBackupDirectory
            };
            AlignDesktopProfileModelState(new AccountRecord(), roundTripProjection);
            using (var connection = new SqliteConnection("Data Source=" + databasePath))
            {
                connection.Open();
                using var verifyRoundTrip = connection.CreateCommand();
                verifyRoundTrip.CommandText =
                    "SELECT COUNT(*) FROM threads WHERE model_provider = $provider;";
                verifyRoundTrip.Parameters.AddWithValue(
                    "$provider",
                    AccountStore.AccessTokenProviderId);
                if (Convert.ToInt32(verifyRoundTrip.ExecuteScalar()) != 2 ||
                    roundTripProjection.ThreadRowsUpdated != 2)
                {
                    throw new InvalidOperationException(
                        "HTTPS-only official threads did not migrate back to the managed provider.");
                }
            }

        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // Temporary self-test files can be removed by the OS later if still in use.
            }
        }
    }

    internal static void EnsureSqliteProvider()
    {
        lock (SqliteProviderLock)
        {
            if (_sqliteProviderInitialized)
            {
                return;
            }

            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            SQLitePCL.raw.FreezeProvider();
            _sqliteProviderInitialized = true;
        }
    }

    private static async Task EnsureAccountModelAvailableAsync(AccountRecord account)
    {
        if (account.IsCompatibleApi)
        {
            await EnsureAccountCanRunMinimalRequestAsync(account);
            return;
        }

        if (HasFreshAccessTokenModelCache(account))
        {
            return;
        }

        var result = await RunCodexAsync("debug models", account.CodexHome, null);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Account {account.Name} model catalog could not be loaded. The shared Codex profile was not changed.\n\n" +
                string.Join(Environment.NewLine, new[] { result.StdOut, result.StdErr }
                    .Where(s => !string.IsNullOrWhiteSpace(s))));
        }

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            if (CatalogSupportsAccessTokenDefaults(document.RootElement))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Account {account.Name} returned an unreadable Codex model catalog.",
                ex);
        }

        throw new InvalidOperationException(
            $"Account {account.Name} does not currently offer {AccessTokenModel} / {AccessTokenReasoningEffort}. " +
            "The shared Codex profile was not changed.");
    }

    private static bool HasFreshAccessTokenModelCache(AccountRecord account)
    {
        var cachePath = Path.Combine(account.CodexHome, "models_cache.json");
        var authPath = Path.Combine(account.CodexHome, AuthFileName);
        if (!File.Exists(cachePath) || !File.Exists(authPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(cachePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("fetched_at", out var fetchedAtValue) ||
                !DateTimeOffset.TryParse(fetchedAtValue.GetString(), out var fetchedAtUtc))
            {
                return false;
            }

            fetchedAtUtc = fetchedAtUtc.ToUniversalTime();
            var age = DateTimeOffset.UtcNow - fetchedAtUtc;
            if (age < TimeSpan.FromMinutes(-5) || age > AccessTokenModelCacheLifetime)
            {
                return false;
            }

            var authModifiedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(authPath), TimeSpan.Zero);
            if (authModifiedUtc > fetchedAtUtc.AddSeconds(2))
            {
                return false;
            }

            return CatalogSupportsAccessTokenDefaults(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool CatalogSupportsAccessTokenDefaults(JsonElement root)
    {
        JsonElement models;
        if (root.ValueKind == JsonValueKind.Array)
        {
            models = root;
        }
        else if (!root.TryGetProperty("models", out models) || models.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var model in models.EnumerateArray())
        {
            if (!model.TryGetProperty("slug", out var slug) ||
                !string.Equals(slug.GetString(), AccessTokenModel, StringComparison.Ordinal))
            {
                continue;
            }

            return !model.TryGetProperty("supported_reasoning_levels", out var levels) ||
                   levels.EnumerateArray().Any(level =>
                       level.TryGetProperty("effort", out var effort) &&
                       string.Equals(effort.GetString(), AccessTokenReasoningEffort, StringComparison.Ordinal));
        }

        return false;
    }

    private static async Task EnsureAccessTokenWebSocketAvailableAsync(AccountRecord account)
    {
        var result = await RunCodexAsync("doctor --json", account.CodexHome, null);
        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var check = document.RootElement
                .GetProperty("checks")
                .GetProperty("network.websocket_reachability");
            var status = check.GetProperty("status").GetString();
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var summary = check.TryGetProperty("summary", out var summaryElement)
                ? summaryElement.GetString()
                : "WebSocket check failed";
            throw new InvalidOperationException(summary);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"账号 {account.Name} 的 Token WebSocket 预检没有通过，Codex 未切换。" +
                "请保持 Windows 系统代理和 127.0.0.1:10808 可用，避免进入 5 次重连。\n\n" +
                ex.Message,
                ex);
        }
    }

    internal static async Task EnsureCompatibleApiRotationPreflightAsync(
        AccountRecord account,
        CancellationToken cancellationToken = default)
    {
        if (GetCompatibleApiRotationBaseUrlValidationError(account.ApiBaseUrl) is { } urlError)
        {
            throw BuildCompatibleApiLaunchPreflightError(account, urlError);
        }

        await EnsureCompatibleApiLaunchPreflightAsync(account, cancellationToken);
    }

    private static string? GetCompatibleApiRotationBaseUrlValidationError(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(baseUri.Host) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            return "API 轮换地址必须是没有内嵌账号密码、查询参数或片段的完整 http/https 地址。";
        }
        if (baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !LocalProxyDetector.IsLoopbackHost(baseUri.Host))
        {
            return "为避免 API Key 明文泄露，远程 API 轮换地址必须使用 HTTPS；HTTP 仅允许本机回环地址。";
        }

        return null;
    }

    private static async Task EnsureCompatibleApiLaunchPreflightAsync(
        AccountRecord account,
        CancellationToken cancellationToken = default)
    {
        var modelError = GetCompatibleApiModelIdValidationError(account.ApiModel);
        if (modelError != null)
        {
            throw BuildCompatibleApiLaunchPreflightError(account, modelError);
        }

        if (!Uri.TryCreate(account.ApiBaseUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw BuildCompatibleApiLaunchPreflightError(
                account,
                "API 地址必须是没有内嵌账号密码的完整 http/https 地址。");
        }

        var configuredModel = account.ApiModel.Trim();
        var apiEndpointIsLoopback = LocalProxyDetector.IsLoopbackHost(baseUri.Host);
        // A loopback API endpoint is reached directly by the child Codex process. An unrelated
        // stopped or malformed system proxy must not block that local-only account.
        var configuredProxyText = apiEndpointIsLoopback ? null : GetConfiguredProxyUri();
        Uri? configuredProxy = null;
        if (!string.IsNullOrWhiteSpace(configuredProxyText))
        {
            if (!Uri.TryCreate(configuredProxyText, UriKind.Absolute, out var parsedProxy))
            {
                throw BuildCompatibleApiLaunchPreflightError(
                    account,
                    "当前代理配置无效：无法解析代理地址。请在设置中修正代理地址和端口后重试。");
            }
            if (GetCompatibleApiProxyValidationError(parsedProxy) is { } proxyError)
            {
                throw BuildCompatibleApiLaunchPreflightError(
                    account,
                    $"当前代理配置无效：{proxyError}。请在设置中修正代理地址和端口后重试。");
            }

            configuredProxy = parsedProxy;
        }

        if (configuredProxy != null && LocalProxyDetector.IsLoopbackHost(configuredProxy.Host))
        {
            try
            {
                using var proxyProbe = new TcpClient();
                await proxyProbe
                    .ConnectAsync(configuredProxy.Host, configuredProxy.Port, cancellationToken)
                    .AsTask()
                    .WaitAsync(CompatibleApiProxyConnectTimeout, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw BuildCompatibleApiLaunchPreflightError(
                    account,
                    $"本机代理 {BuildSafeAuthorityLabel(configuredProxy)} 连接超时。请先启动 v2rayN、Clash 或 Mihomo，并确认代理端口已监听。");
            }
            catch (Exception ex) when (
                ex is SocketException or TimeoutException or IOException or ObjectDisposedException)
            {
                throw BuildCompatibleApiLaunchPreflightError(
                    account,
                    $"本机代理 {BuildSafeAuthorityLabel(configuredProxy)} 当前不可连接。请先启动 v2rayN、Clash 或 Mihomo，并确认代理端口已监听。",
                    ex);
            }
        }

        var modelsUri = BuildCompatibleApiModelsUri(baseUri);
        using var handler = new HttpClientHandler();
        if (configuredProxy != null)
        {
            handler.Proxy = new WebProxy(configuredProxy);
            handler.UseProxy = true;
        }
        else if (apiEndpointIsLoopback)
        {
            handler.UseProxy = false;
        }

        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, modelsUri);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "Bearer " + ReadAccessTokenCredential(Path.Combine(account.CodexHome, AuthFileName)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CompatibleApiLaunchPreflightTimeout);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw BuildCompatibleApiLaunchPreflightError(
                account,
                $"通过当前网络访问 {BuildSafeAuthorityLabel(baseUri)} 超过 {CompatibleApiLaunchPreflightTimeout.TotalSeconds:0} 秒仍未响应。" +
                BuildCompatibleApiProxyHint(configuredProxy));
        }
        catch (HttpRequestException ex)
        {
            throw BuildCompatibleApiLaunchPreflightError(
                account,
                $"无法连接兼容 API {BuildSafeAuthorityLabel(baseUri)}。" +
                BuildCompatibleApiProxyHint(configuredProxy),
                ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw BuildCompatibleApiLaunchPreflightError(
                    account,
                    $"兼容 API {BuildSafeAuthorityLabel(baseUri)} 拒绝了当前 API Key（HTTP {(int)response.StatusCode}）。请编辑账号并更新 Key。");
            }

            // Some OpenAI-compatible services do not expose a model catalog. A clear 404/405
            // still proves that DNS, proxy, TLS and the endpoint itself are reachable; local
            // model-ID validation above remains in force for those services.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw BuildCompatibleApiLaunchPreflightError(
                    account,
                    $"兼容 API {BuildSafeAuthorityLabel(baseUri)} 暂时不可用（HTTP {(int)response.StatusCode}）。请稍后重试。");
            }

            if (response.Content.Headers.ContentLength is > CompatibleApiModelCatalogMaxBytes)
            {
                return;
            }

            byte[]? body;
            try
            {
                body = await ReadResponseBodyUpToLimitAsync(
                    response.Content,
                    CompatibleApiModelCatalogMaxBytes,
                    timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw BuildCompatibleApiLaunchPreflightError(
                    account,
                    $"兼容 API {BuildSafeAuthorityLabel(baseUri)} 已响应，但读取模型目录超过 " +
                    $"{CompatibleApiLaunchPreflightTimeout.TotalSeconds:0} 秒。" +
                    BuildCompatibleApiProxyHint(configuredProxy));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
            {
                throw BuildCompatibleApiLaunchPreflightError(
                    account,
                    $"兼容 API {BuildSafeAuthorityLabel(baseUri)} 已响应，但读取模型目录失败。" +
                    BuildCompatibleApiProxyHint(configuredProxy),
                    ex);
            }

            if (body == null || !TryReadCompatibleApiModelIds(body, out var modelIds))
            {
                return;
            }

            if (modelIds.Contains(configuredModel, StringComparer.Ordinal))
            {
                return;
            }

            var suggestions = modelIds
                .Where(IsSafeCompatibleApiModelId)
                .OrderByDescending(model => GetCompatibleApiModelFamily(configuredModel).Length > 0 &&
                                            model.StartsWith(
                                                GetCompatibleApiModelFamily(configuredModel),
                                                StringComparison.OrdinalIgnoreCase))
                .ThenBy(model => model, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            var suggestionText = suggestions.Length == 0
                ? string.Empty
                : $"\n接口当前公布的可选模型包括：{string.Join("、", suggestions)}";
            throw BuildCompatibleApiLaunchPreflightError(
                account,
                $"模型 ID“{configuredModel}”不在接口 /models 返回的列表中。请在“编辑 API”里改成准确的模型 ID。{suggestionText}");
        }
    }

    internal static string? GetCompatibleApiModelIdValidationError(string? model)
    {
        var value = model?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return "兼容 API 的模型 ID 不能为空。";
        }
        if (value.Length > 200)
        {
            return "兼容 API 的模型 ID 过长。";
        }
        if (value.Any(char.IsWhiteSpace))
        {
            return $"模型 ID“{value}”包含空格或换行。模型 ID 必须与接口公布的名称完全一致，例如 gpt-5.6-sol。";
        }
        if (value.Any(char.IsControl) || value.Contains('"') || value.Contains('\''))
        {
            return "兼容 API 的模型 ID 包含不允许的字符。";
        }

        return null;
    }

    internal static Uri BuildCompatibleApiModelsUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return new Uri(baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/models", UriKind.Absolute);
    }

    private static string? GetCompatibleApiProxyValidationError(Uri proxy)
    {
        if (!proxy.IsAbsoluteUri || string.IsNullOrWhiteSpace(proxy.Host))
        {
            return "代理地址必须是完整地址";
        }
        if (proxy.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        {
            return $"不支持代理协议 {proxy.Scheme}";
        }
        if (proxy.Port is <= 0 or > 65535)
        {
            return "代理端口必须在 1 到 65535 之间";
        }
        if (proxy.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(proxy.Query) ||
            !string.IsNullOrEmpty(proxy.Fragment))
        {
            return "代理地址不能包含路径、查询参数或片段";
        }

        return null;
    }

    private static async Task<byte[]?> ReadResponseBodyUpToLimitAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maxBytes, 80 * 1024));
        var buffer = new byte[80 * 1024];
        var total = 0;
        while (true)
        {
            var remaining = maxBytes - total;
            var requested = Math.Min(buffer.Length, remaining + 1);
            var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }
    }

    internal static bool TryReadCompatibleApiModelIds(
        ReadOnlyMemory<byte> json,
        out HashSet<string> modelIds)
    {
        modelIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            JsonElement rows;
            if (root.ValueKind == JsonValueKind.Array)
            {
                rows = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("data", out var data) &&
                     data.ValueKind == JsonValueKind.Array)
            {
                rows = data;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("models", out var models) &&
                     models.ValueKind == JsonValueKind.Array)
            {
                rows = models;
            }
            else
            {
                return false;
            }

            foreach (var row in rows.EnumerateArray())
            {
                string? id = row.ValueKind == JsonValueKind.String
                    ? row.GetString()
                    : row.ValueKind == JsonValueKind.Object &&
                      row.TryGetProperty("id", out var idValue) &&
                      idValue.ValueKind == JsonValueKind.String
                        ? idValue.GetString()
                        : row.ValueKind == JsonValueKind.Object &&
                          row.TryGetProperty("slug", out var slugValue) &&
                          slugValue.ValueKind == JsonValueKind.String
                            ? slugValue.GetString()
                            : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    modelIds.Add(id.Trim());
                }
            }

            return modelIds.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSafeCompatibleApiModelId(string value)
    {
        return value.Length is > 0 and <= 100 &&
               !value.Any(char.IsWhiteSpace) &&
               !value.Any(char.IsControl);
    }

    private static string GetCompatibleApiModelFamily(string model)
    {
        var separator = model.LastIndexOf('-');
        return separator > 0 ? model[..(separator + 1)] : string.Empty;
    }

    internal static void ValidateCompatibleApiLaunchPreflight()
    {
        if (GetCompatibleApiModelIdValidationError("gpt-5.6-sol") != null ||
            GetCompatibleApiModelIdValidationError("gpt-5.6 sol") is not { } whitespaceError ||
            !whitespaceError.Contains("空格", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Compatible API model-ID validation self-test failed.");
        }

        var modelsUri = BuildCompatibleApiModelsUri(
            new Uri("https://example.invalid/openai/v1/?ignored=true"));
        if (modelsUri.AbsoluteUri != "https://example.invalid/openai/v1/models")
        {
            throw new InvalidOperationException("Compatible API model-catalog URI self-test failed.");
        }

        if (GetCompatibleApiRotationBaseUrlValidationError("https://example.invalid/openai/v1") != null ||
            GetCompatibleApiRotationBaseUrlValidationError("http://127.0.0.1:8080/v1") != null ||
            GetCompatibleApiRotationBaseUrlValidationError("http://example.invalid/v1") is not { } httpError ||
            !httpError.Contains("HTTPS", StringComparison.Ordinal) ||
            GetCompatibleApiRotationBaseUrlValidationError("https://example.invalid/v1?key=unsafe") == null)
        {
            throw new InvalidOperationException("Compatible API rotation URL safety self-test failed.");
        }

        var catalog = Encoding.UTF8.GetBytes(
            "{\"data\":[{\"id\":123},{\"slug\":false},{\"id\":\"gpt-5.6-sol\"}]}");
        if (!TryReadCompatibleApiModelIds(catalog, out var modelIds) ||
            modelIds.Count != 1 ||
            !modelIds.Contains("gpt-5.6-sol"))
        {
            throw new InvalidOperationException("Compatible API model-catalog parsing self-test failed.");
        }

        if (GetCompatibleApiProxyValidationError(new Uri("http://127.0.0.1:10808")) != null ||
            GetCompatibleApiProxyValidationError(new Uri("socks5://127.0.0.1")) is not { } portError ||
            !portError.Contains("端口", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Compatible API proxy validation self-test failed.");
        }

        using var exactContent = new ByteArrayContent([1, 2, 3, 4]);
        var exact = ReadResponseBodyUpToLimitAsync(exactContent, 4, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        using var oversizedContent = new ByteArrayContent([1, 2, 3, 4, 5]);
        var oversized = ReadResponseBodyUpToLimitAsync(oversizedContent, 4, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (exact == null || !exact.SequenceEqual(new byte[] { 1, 2, 3, 4 }) || oversized != null)
        {
            throw new InvalidOperationException("Compatible API bounded-response self-test failed.");
        }

        var safeMessage = BuildCompatibleApiLaunchPreflightError(
            new AccountRecord { Name = "monthly-pool" },
            "proxy unavailable").Message;
        if (!safeMessage.Contains("当前 Codex 未关闭，共享凭据也未切换", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Compatible API non-destructive error-message self-test failed.");
        }
    }

    private static string BuildSafeAuthorityLabel(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.Host}]" : uri.Host;
        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    private static string BuildCompatibleApiProxyHint(Uri? configuredProxy)
    {
        return configuredProxy == null
            ? " 请检查网络和 Windows 系统代理。"
            : $" 当前配置的代理是 {BuildSafeAuthorityLabel(configuredProxy)}；请确认代理软件和节点均可用。";
    }

    private static InvalidOperationException BuildCompatibleApiLaunchPreflightError(
        AccountRecord account,
        string detail,
        Exception? innerException = null)
    {
        var message =
            $"兼容 API 账号“{account.Name}”启动前检查未通过。当前 Codex 未关闭，共享凭据也未切换。\n\n{detail}";
        return innerException == null
            ? new InvalidOperationException(message)
            : new InvalidOperationException(message, innerException);
    }

    private static async Task EnsureAccountCanRunMinimalRequestAsync(AccountRecord account)
    {
        var fingerprint = BuildAccountValidationFingerprint(account);
        if (HasFreshCompatibleApiPreflight(account, fingerprint))
        {
            return;
        }

        var result = await RunCodexAsync(
            "exec --skip-git-repo-check --ephemeral --dangerously-bypass-approvals-and-sandbox \"Reply exactly OK.\"",
            account.CodexHome,
            null);
        var text = string.Join(Environment.NewLine, new[] { result.StdOut, result.StdErr }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (result.ExitCode != 0)
        {
            var message = IsPersonalAccessTokenMetadataRequestFailure(text)
                ? BuildPersonalAccessTokenNetworkMessage(text)
                : $"Account {account.Name} could not complete a minimal Codex request. The shared Codex profile was not changed.\n\n{text}";
            throw new InvalidOperationException(message);
        }

        CacheCompatibleApiPreflight(account, fingerprint, DateTimeOffset.UtcNow);
    }

    private static bool HasFreshCompatibleApiPreflight(AccountRecord account, string fingerprint)
    {
        lock (CompatibleApiPreflightCacheLock)
        {
            if (CompatibleApiPreflightCache.TryGetValue(account.CodexHome, out var cached) &&
                cached.Fingerprint.Equals(fingerprint, StringComparison.Ordinal) &&
                IsFreshCompatibleApiPreflight(cached.CompletedAtUtc))
            {
                return true;
            }
        }

        var cachePath = Path.Combine(account.CodexHome, CompatibleApiPreflightCacheFileName);
        try
        {
            if (!File.Exists(cachePath))
            {
                return false;
            }

            var root = JsonNode.Parse(File.ReadAllText(cachePath)) as JsonObject;
            var cachedFingerprint = root?["fingerprint"]?.GetValue<string>();
            var completedAtText = root?["completedAtUtc"]?.GetValue<string>();
            if (!string.Equals(cachedFingerprint, fingerprint, StringComparison.Ordinal) ||
                !DateTimeOffset.TryParse(completedAtText, out var completedAtUtc) ||
                !IsFreshCompatibleApiPreflight(completedAtUtc))
            {
                return false;
            }

            lock (CompatibleApiPreflightCacheLock)
            {
                CompatibleApiPreflightCache[account.CodexHome] =
                    new CompatibleApiPreflightCacheEntry(fingerprint, completedAtUtc);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsFreshCompatibleApiPreflight(DateTimeOffset completedAtUtc)
    {
        var age = DateTimeOffset.UtcNow - completedAtUtc.ToUniversalTime();
        return age >= TimeSpan.FromMinutes(-1) && age <= CompatibleApiPreflightCacheLifetime;
    }

    private static void CacheCompatibleApiPreflight(
        AccountRecord account,
        string fingerprint,
        DateTimeOffset completedAtUtc)
    {
        lock (CompatibleApiPreflightCacheLock)
        {
            CompatibleApiPreflightCache[account.CodexHome] =
                new CompatibleApiPreflightCacheEntry(fingerprint, completedAtUtc);
        }

        var cache = new JsonObject
        {
            ["fingerprint"] = fingerprint,
            ["completedAtUtc"] = completedAtUtc.ToUniversalTime().ToString("O")
        };
        WriteTextAtomically(
            Path.Combine(account.CodexHome, CompatibleApiPreflightCacheFileName),
            cache.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string BuildAccountValidationFingerprint(AccountRecord account)
    {
        static string FileStamp(string path)
        {
            if (!File.Exists(path))
            {
                return "missing";
            }

            var info = new FileInfo(path);
            return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        }

        var authPath = Path.Combine(account.CodexHome, AuthFileName);
        var configPath = Path.Combine(account.CodexHome, ConfigFileName);
        var rawFingerprint = string.Join(
            "|",
            Path.GetFullPath(account.CodexHome),
            FileStamp(authPath),
            FileStamp(configPath),
            account.ApiBaseUrl,
            account.ApiModel,
            account.ApiWireApi);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawFingerprint)));
    }

    private static string ReadAccountServiceTier(string accountHome)
    {
        try
        {
            var configPath = Path.Combine(Path.GetFullPath(accountHome), ConfigFileName);
            return File.Exists(configPath)
                ? ReadDesktopServiceTier(File.ReadAllText(configPath))
                : DesktopServiceTier;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return DesktopServiceTier;
        }
    }

    private static string GetActiveAccountStatePath(string profileHome) =>
        Path.Combine(profileHome, ActiveAccountStateFileName);

    private static string GetActiveAccountMode(
        AccountRecord account,
        AccessTokenSharedProfileMode mode)
    {
        if (account.IsOfficialOAuth)
        {
            return DesktopSelectionModeOfficialOAuth;
        }

        if (mode == AccessTokenSharedProfileMode.ChatGptDesktop)
        {
            return account.IsCompatibleApi
                ? DesktopSelectionModeChatGptCompatibleApi
                : DesktopSelectionModeChatGptAccessToken;
        }

        return account.IsCompatibleApi
            ? DesktopSelectionModeDirectCompatibleApi
            : DesktopSelectionModeDirectAccessToken;
    }

    private static bool IsActiveAccountMode(string mode)
    {
        return mode is DesktopSelectionModeOfficialOAuth or
            DesktopSelectionModeChatGptAccessToken or
            DesktopSelectionModeChatGptCompatibleApi or
            DesktopSelectionModeDirectAccessToken or
            DesktopSelectionModeDirectCompatibleApi;
    }

    private static void WriteActiveAccountState(
        string profileHome,
        AccountRecord account,
        AccessTokenSharedProfileMode mode)
    {
        ArgumentNullException.ThrowIfNull(account);
        var normalizedProfileHome = Path.GetFullPath(profileHome);
        var accountHome = Path.GetFullPath(account.CodexHome);
        if (PathsEqual(normalizedProfileHome, accountHome))
        {
            throw new InvalidOperationException(
                "The active desktop account cannot use the shared CODEX_HOME.");
        }

        var contents = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                accountKey = GetDesktopAccountKey(account),
                accountHome,
                mode = GetActiveAccountMode(account, mode)
            },
            new JsonSerializerOptions { WriteIndented = true });
        WriteTextAtomically(GetActiveAccountStatePath(normalizedProfileHome), contents);
    }

    private static bool TryReadActiveAccountState(
        string profileHome,
        out string accountKey,
        out string accountHome,
        out string mode)
    {
        accountKey = "";
        accountHome = "";
        mode = "";
        var path = GetActiveAccountStatePath(Path.GetFullPath(profileHome));
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            _ = BuildCanonicalJsonSha256(path);
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var version) ||
                version != 1 ||
                !TryReadJsonString(root, "accountKey", out var key) ||
                !IsDesktopAccountKey(key) ||
                !TryReadJsonString(root, "accountHome", out var home) ||
                !TryReadJsonString(root, "mode", out var selectedMode) ||
                !IsActiveAccountMode(selectedMode))
            {
                return false;
            }

            var normalizedHome = Path.GetFullPath(home);
            var normalizedProfileHome = Path.GetFullPath(profileHome);
            if (PathsEqual(normalizedHome, normalizedProfileHome) ||
                !GetDesktopAccountKeyForHome(normalizedHome).Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            accountKey = key;
            accountHome = normalizedHome;
            mode = selectedMode;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsActiveAccountCredentialCurrent(
        string profileHome,
        string accountKey,
        string accountHome,
        string mode)
    {
        var sharedAuthPath = Path.Combine(profileHome, AuthFileName);
        var accountAuthPath = Path.Combine(accountHome, AuthFileName);
        if (mode is DesktopSelectionModeOfficialOAuth or
            DesktopSelectionModeChatGptAccessToken or
            DesktopSelectionModeChatGptCompatibleApi)
        {
            return TryReadDesktopSelectionKey(profileHome, out var selectedKey) &&
                   selectedKey.Equals(accountKey, StringComparison.OrdinalIgnoreCase) &&
                   IsChatGptDesktopAuthJson(sharedAuthPath);
        }

        if (mode is not DesktopSelectionModeDirectAccessToken and
            not DesktopSelectionModeDirectCompatibleApi)
        {
            return false;
        }

        return IsAccessTokenDesktopAuthSelected(accountAuthPath, sharedAuthPath);
    }

    // The native picker writes the selected tier to the shared CODEX_HOME.  Capture that value
    // before projecting another account, using the independent active-account marker to avoid
    // attributing it to an arbitrary account when the marker is missing or stale.
    private static void PersistSharedServiceTierToSelectedAccount()
    {
        try
        {
            var profileHome = Path.GetFullPath(GetDefaultCodexHome());
            var sharedConfigPath = Path.Combine(profileHome, ConfigFileName);
            if (!File.Exists(sharedConfigPath) ||
                !TryReadDesktopServiceTier(File.ReadAllText(sharedConfigPath), out var serviceTier) ||
                !TryReadActiveAccountState(
                    profileHome,
                    out var accountKey,
                    out var accountHome,
                    out var mode) ||
                !IsActiveAccountCredentialCurrent(profileHome, accountKey, accountHome, mode))
            {
                return;
            }

            var accountConfigPath = Path.Combine(accountHome, ConfigFileName);
            if (!File.Exists(accountConfigPath))
            {
                return;
            }

            var current = File.ReadAllText(accountConfigPath);
            var projected = UpsertDesktopServiceTier(current, serviceTier);
            if (!string.Equals(current, projected, StringComparison.Ordinal))
            {
                WriteTextAtomically(accountConfigPath, projected);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException)
        {
            // A stale/corrupt marker must never prevent an account switch.  The next successful
            // projection replaces it with a fresh marker and the target account's own tier.
        }
    }

    internal static string NormalizeDesktopServiceTier(string? value)
    {
        var candidate = value?.Trim();
        if (candidate?.Equals("default", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "default";
        }
        if (candidate?.Equals("priority", StringComparison.OrdinalIgnoreCase) == true ||
            candidate?.Equals("fast", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Older manager builds could persist the UI label "fast". Codex's native row and
            // request contract use the canonical service-tier value "priority".
            return "priority";
        }

        return DesktopServiceTier;
    }

    internal static string ReadDesktopServiceTier(string? config)
    {
        return TryReadDesktopServiceTier(config, out var serviceTier)
            ? serviceTier
            : DesktopServiceTier;
    }

    private static bool TryReadDesktopServiceTier(string? config, out string serviceTier)
    {
        serviceTier = DesktopServiceTier;
        if (string.IsNullOrWhiteSpace(config))
        {
            return false;
        }

        foreach (var line in config.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                break;
            }
            if (!IsTopLevelServiceTierLine(trimmed))
            {
                continue;
            }

            var equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }
            var rawValue = trimmed[(equalsIndex + 1)..].Trim();
            var match = Regex.Match(
                rawValue,
                "^(?:\\\"(?<quoted>default|priority|fast)\\\"|(?<bare>default|priority|fast))(?:\\s*#.*)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                continue;
            }

            serviceTier = NormalizeDesktopServiceTier(
                match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["bare"].Value);
            return true;
        }

        return false;
    }

    private static string UpsertDesktopServiceTier(string currentConfig, string? serviceTier)
    {
        var normalizedServiceTier = NormalizeDesktopServiceTier(serviceTier);
        var lines = currentConfig
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var output = new List<string>();
        var inTopLevel = true;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                inTopLevel = false;
            }
            if (inTopLevel && IsTopLevelServiceTierLine(trimmed))
            {
                continue;
            }

            output.Add(line);
        }

        var insertIndex = output.FindIndex(line =>
        {
            var trimmed = line.Trim();
            return trimmed.StartsWith("[", StringComparison.Ordinal) &&
                   trimmed.EndsWith("]", StringComparison.Ordinal);
        });
        if (insertIndex < 0)
        {
            insertIndex = output.Count;
        }
        while (insertIndex > 0 && string.IsNullOrWhiteSpace(output[insertIndex - 1]))
        {
            insertIndex--;
        }
        output.Insert(
            insertIndex,
            "service_tier = " + TomlString(normalizedServiceTier));
        return string.Join(Environment.NewLine, output).TrimEnd() + Environment.NewLine;
    }

    private static void ProjectAccessTokenSourceConfig(string targetConfigPath)
    {
        var currentConfig = File.Exists(targetConfigPath)
            ? File.ReadAllText(targetConfigPath)
            : "";
        var projected = ProjectWindowsClientConfigText(currentConfig, requiresOpenAiAuth: true);
        if (!string.Equals(currentConfig, projected, StringComparison.Ordinal))
        {
            WriteTextAtomically(targetConfigPath, projected);
        }
    }

    private static void ProjectCompatibleApiConfig(string targetConfigPath, AccountRecord account)
    {
        var currentConfig = File.Exists(targetConfigPath)
            ? File.ReadAllText(targetConfigPath)
            : "";
        var projected = ProjectCompatibleApiConfigText(currentConfig, account);
        if (!string.Equals(currentConfig, projected, StringComparison.Ordinal))
        {
            WriteTextAtomically(targetConfigPath, projected);
        }
    }

    internal static void ValidateConfigProjectionDefaults()
    {
        var migratedFastAlias = UpsertDesktopServiceTier(
            "model = \"test\"\n\n[features]\njs_repl = false\n",
            "fast");
        if (!NormalizeDesktopServiceTier("fast").Equals("priority", StringComparison.Ordinal) ||
            !ReadDesktopServiceTier("service_tier = \"fast\"\n").Equals("priority", StringComparison.Ordinal) ||
            !migratedFastAlias.Contains("service_tier = \"priority\"", StringComparison.Ordinal) ||
            migratedFastAlias.Contains("service_tier = \"fast\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The legacy Fast service-tier alias was not migrated to Codex's canonical priority value.");
        }

        const string existingConfig =
            "model_provider = \"stale\"\n" +
            "model = \"stale-model\"\n" +
            "review_model = \"stale-model\"\n" +
            "model_reasoning_effort = \"xhigh\"\n" +
            "service_tier = \"priority\"\n" +
            "model_auto_compact_token_limit = 100\n\n" +
            "[plugins.\"sites@openai-bundled\"]\n" +
            "enabled = true\n\n" +
            "[features]\n" +
            "js_repl = false\n\n" +
            "[model_providers.OpenAI]\n" +
            "name = \"OpenAI\"\n" +
            "base_url = \"https://stale.invalid\"\n" +
            "wire_api = \"responses\"\n" +
            "requires_openai_auth = false\n" +
            "supports_websockets = false\n\n" +
            "[model_providers.codex_compatible_api]\n" +
            "base_url = \"https://also-stale.invalid\"\n\n" +
            "[model_providers.codex_token_http]\n" +
            "base_url = \"https://stale-token.invalid\"\n\n" +
            "[model_providers.keep_me]\n" +
            "base_url = \"https://keep.invalid\"\n";

        var tokenConfig = ProjectWindowsClientConfigText(existingConfig);
        AssertProjectedConfig(
            tokenConfig,
            AccountStore.AccessTokenProviderId,
            AccessTokenModel,
            AccessTokenReasoningEffort,
            false,
            pluginsEnabled: false,
            expectedServiceTier: "priority");
        AssertManagedProviderSection(tokenConfig, requiresOpenAiAuth: false);
        AssertAccessTokenHttpProvider(tokenConfig, requiresOpenAiAuth: false);

        var namedDesktopTokenConfig = ProjectWindowsClientConfigText(
            existingConfig,
            desktopProviderName: "token-test");
        if (!namedDesktopTokenConfig.Contains("name = \"token-test\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Access Token desktop projection did not expose the selected account name.");
        }

        const string desktopBearerToken = "virtual-desktop-bearer";
        var dualLoginDesktopConfig = ProjectWindowsClientConfigText(
            existingConfig,
            requiresOpenAiAuth: true,
            desktopProviderName: "token-test",
            providerBearerToken: desktopBearerToken,
            forceFileAuthStore: true);
        AssertProjectedConfig(
            dualLoginDesktopConfig,
            AccountStore.AccessTokenProviderId,
            AccessTokenModel,
            AccessTokenReasoningEffort,
            false,
            pluginsEnabled: false,
            expectedServiceTier: "priority");
        AssertManagedProviderSection(dualLoginDesktopConfig, requiresOpenAiAuth: true);
        AssertAccessTokenHttpProvider(dualLoginDesktopConfig, requiresOpenAiAuth: true);
        var dualLoginTopLevelLines = dualLoginDesktopConfig
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .TakeWhile(line => !line.TrimStart().StartsWith("[", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();
        if (dualLoginTopLevelLines.Count(IsCliAuthCredentialsStoreLine) != 1 ||
            !dualLoginTopLevelLines.Contains(
                "cli_auth_credentials_store = \"file\"",
                StringComparer.Ordinal) ||
            dualLoginDesktopConfig.Split('\n').Count(line =>
                IsExperimentalBearerTokenLine(line.Trim())) != 1 ||
            !dualLoginDesktopConfig.Contains(
                "experimental_bearer_token = " + TomlString(desktopBearerToken),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Dual-login desktop projection did not separate ChatGPT OAuth from the model bearer token.");
        }

        var sourceTokenConfig = ProjectWindowsClientConfigText(
            existingConfig,
            requiresOpenAiAuth: true);
        AssertManagedProviderSection(sourceTokenConfig, requiresOpenAiAuth: true);
        AssertAccessTokenHttpProvider(sourceTokenConfig, requiresOpenAiAuth: true);
        if (sourceTokenConfig.Contains("experimental_bearer_token", StringComparison.Ordinal) ||
            sourceTokenConfig.Contains("cli_auth_credentials_store", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CLI source config unexpectedly received desktop-only OAuth projection settings.");
        }
        if (!sourceTokenConfig.Contains(
                "chatgpt_base_url = " + TomlString(LocalPatGateway.ChatGptBaseUrl),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PAT config must route app-server account APIs through the local gateway.");
        }

        var standaloneTokenConfig = AccountStore.BuildAccessTokenConfig();
        AssertProjectedConfig(
            standaloneTokenConfig,
            AccountStore.AccessTokenProviderId,
            AccessTokenModel,
            AccessTokenReasoningEffort,
            false,
            pluginsEnabled: false);
        AssertAccessTokenHttpProvider(standaloneTokenConfig, requiresOpenAiAuth: true);
        if (!standaloneTokenConfig.Contains(
                "chatgpt_base_url = " + TomlString(LocalPatGateway.ChatGptBaseUrl),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Standalone PAT config must route app-server account APIs through the local gateway.");
        }

        var apiAccount = new AccountRecord
        {
            ApiProviderName = "OpenAI",
            ApiModel = "",
            ApiBaseUrl = "https://example.invalid",
            ApiWireApi = "responses"
        };
        var apiConfig = ProjectCompatibleApiConfigText(existingConfig, apiAccount);
        AssertProjectedConfig(
            apiConfig,
            AccountStore.CompatibleApiProviderId,
            CompatibleApiDefaultModel,
            CompatibleApiReasoningEffort,
            false,
            expectedServiceTier: "priority");
        if (!apiConfig.Contains("base_url = \"https://example.invalid\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Compatible API projection did not preserve the selected endpoint.");
        }
        AssertManagedProviderSection(apiConfig, requiresOpenAiAuth: false);
        if (!apiConfig.Contains("name = \"OpenAI\"", StringComparison.Ordinal) ||
            !apiConfig.Contains("[model_providers.keep_me]", StringComparison.Ordinal) ||
            !apiConfig.Contains("stream_max_retries = 0", StringComparison.Ordinal) ||
            !apiConfig.Contains("request_max_retries = 1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Compatible API projection did not preserve its display name, unrelated providers, or fail-fast retry policy.");
        }

        var officialPriorityConfig = ProjectOfficialOAuthConfigText(apiConfig);
        var tokenPriorityRoundTrip = ProjectWindowsClientConfigText(officialPriorityConfig);
        var apiPriorityRoundTrip = ProjectCompatibleApiConfigText(tokenPriorityRoundTrip, apiAccount);
        if (!ReadDesktopServiceTier(officialPriorityConfig).Equals("priority", StringComparison.Ordinal) ||
            !ReadDesktopServiceTier(tokenPriorityRoundTrip).Equals("priority", StringComparison.Ordinal) ||
            !ReadDesktopServiceTier(apiPriorityRoundTrip).Equals("priority", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected Fast service tier was not preserved across API, official OAuth, PAT, and API projections.");
        }

        var providerNameCollisionConfig = ProjectCompatibleApiConfigText(existingConfig, new AccountRecord
        {
            ApiProviderName = "keep_me",
            ApiModel = CompatibleApiDefaultModel,
            ApiBaseUrl = "https://example.invalid",
            ApiWireApi = "responses"
        });
        var normalizedProviderNameCollisionConfig =
            providerNameCollisionConfig.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalizedProviderNameCollisionConfig.Contains(
                "[model_providers.keep_me]\nbase_url = \"https://keep.invalid\"",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Compatible API projection removed an unrelated provider whose id matched the display name.");
        }

        var standaloneApiConfig = AccountStore.BuildCompatibleApiConfig(new AccountRecord
        {
            ApiProviderName = "OpenAI",
            ApiModel = CompatibleApiDefaultModel,
            ApiBaseUrl = "https://example.invalid",
            ApiWireApi = "responses"
        });
        AssertManagedProviderSection(standaloneApiConfig, requiresOpenAiAuth: false);
        if (!standaloneApiConfig.Contains(
                "model_provider = " + TomlString(AccountStore.CompatibleApiProviderId),
                StringComparison.Ordinal) ||
            !standaloneApiConfig.Contains("model = " + TomlString(CompatibleApiDefaultModel), StringComparison.Ordinal) ||
            !standaloneApiConfig.Contains("name = \"OpenAI\"", StringComparison.Ordinal) ||
            !standaloneApiConfig.Contains("stream_max_retries = 0", StringComparison.Ordinal) ||
            !standaloneApiConfig.Contains("request_max_retries = 1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Standalone compatible API config is inconsistent with desktop projection.");
        }

        const string projectConfig =
            "model_provider = \"OpenAI\"\n" +
            "model = \"gpt-5.5\"\n" +
            "model_reasoning_effort = \"xhigh\"\n" +
            "network_access = \"enabled\"\n\n" +
            "[model_providers.OpenAI]\n" +
            "base_url = \"http://127.0.0.1:8317\"\n";
        var sanitizedProjectConfig = RemoveProjectModelOverrides(projectConfig);
        if (sanitizedProjectConfig.Contains("gpt-5.5", StringComparison.Ordinal) ||
            sanitizedProjectConfig.Contains("model_providers", StringComparison.Ordinal) ||
            !sanitizedProjectConfig.Contains("network_access = \"enabled\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Project-level model override sanitization failed.");
        }
    }

    internal static void ValidateLocalPatConfigMigration()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-local-pat-config-migration-" + Guid.NewGuid().ToString("N"));
        var accountHome = Path.Combine(root, "account");
        Directory.CreateDirectory(accountHome);
        try
        {
            var configPath = Path.Combine(accountHome, ConfigFileName);
            var authPath = Path.Combine(accountHome, AuthFileName);
            File.WriteAllText(
                configPath,
                "model_provider = \"codex_account_manager\"\n" +
                "model = \"gpt-5.6-terra\"\n\n" +
                "[model_providers.codex_account_manager]\n" +
                "base_url = \"https://chatgpt.com/backend-api/codex\"\n" +
                "wire_api = \"responses\"\n" +
                "requires_openai_auth = true\n");
            File.WriteAllText(
                authPath,
                "{\"personal_access_token\":\"at-test-only-not-a-real-token\"}");
            var authBefore = File.ReadAllBytes(authPath);
            var account = new AccountRecord
            {
                Name = "migration-test",
                CodexHome = accountHome,
                AuthKind = AccountAuthKind.AccessToken
            };
            var service = new CodexCliService();
            service.EnsureLocalPatAccountConfig(account);
            var migrated = File.ReadAllText(configPath);
            service.EnsureLocalPatAccountConfig(account);
            var migratedAgain = File.ReadAllText(configPath);
            var hasLocalBaseUrl = migrated.Contains(
                "base_url = " + TomlString(AccountStore.AccessTokenBaseUrl),
                StringComparison.Ordinal);
            var hasRequiredAuth = migrated.Contains(
                "requires_openai_auth = true",
                StringComparison.Ordinal);
            var isIdempotent = string.Equals(migrated, migratedAgain, StringComparison.Ordinal);
            var authUnchanged = authBefore.SequenceEqual(File.ReadAllBytes(authPath));
            if (!hasLocalBaseUrl || !hasRequiredAuth || !isIdempotent || !authUnchanged)
            {
                throw new InvalidOperationException(
                    "Local PAT config migration must be idempotent and must not modify auth.json.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertAccessTokenHttpProvider(
        string config,
        bool requiresOpenAiAuth)
    {
        var tokenProviderHeader = "[model_providers." + AccountStore.AccessTokenProviderId + "]";
        if (config.Split('\n').Count(line =>
                line.Trim().Equals(tokenProviderHeader, StringComparison.OrdinalIgnoreCase)) != 1 ||
            !config.Contains("base_url = " + TomlString(AccountStore.AccessTokenBaseUrl), StringComparison.Ordinal) ||
            !config.Contains(
                "requires_openai_auth = " + requiresOpenAiAuth.ToString().ToLowerInvariant(),
                StringComparison.Ordinal) ||
            !config.Contains("plugins = false", StringComparison.Ordinal) ||
            !config.Contains("supports_websockets = false", StringComparison.Ordinal) ||
            !config.Contains("stream_max_retries = 0", StringComparison.Ordinal) ||
            !config.Contains("request_max_retries = 1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Access Token projection did not force the fail-fast HTTP provider.");
        }
    }

    private static void AssertManagedProviderSection(string config, bool requiresOpenAiAuth)
    {
        var normalized = config.Replace("\r\n", "\n").Replace('\r', '\n');
        var managedHeader = "[model_providers." + AccountStore.ManagedProviderId + "]";
        var managedCount = normalized.Split('\n')
            .Count(line => line.Trim().Equals(managedHeader, StringComparison.OrdinalIgnoreCase));
        var expectedAuthLine = "requires_openai_auth = " + requiresOpenAiAuth.ToString().ToLowerInvariant();
        if (managedCount != 1 ||
            !normalized.Contains(managedHeader + "\n", StringComparison.Ordinal) ||
            !normalized.Contains(expectedAuthLine, StringComparison.Ordinal) ||
            normalized.Split('\n').Any(line =>
                line.Trim().Equals("[model_providers.OpenAI]", StringComparison.OrdinalIgnoreCase) ||
                line.Trim().Equals("[model_providers.\"OpenAI\"]", StringComparison.OrdinalIgnoreCase) ||
                line.Trim().Equals(
                    "[model_providers." + AccountStore.LegacyAccessTokenProviderId + "]",
                    StringComparison.OrdinalIgnoreCase) ||
                line.Trim().Equals(
                    "[model_providers." + AccountStore.LegacyCompatibleApiProviderId + "]",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Managed provider projection left a conflicting or duplicate provider section.");
        }
    }

    internal static void ValidateDesktopSidebarProjection()
    {
        const string current = """
            {
              "keep": "value",
              "electron-persisted-atom-state": {
                "sidebar-collapsed-sections-v1": {
                  "chats": true,
                  "cloud": false,
                  "pinned": true
                },
                "flat-project-sidebar-preferences-v1": {
                  "chatSortMode": "priority",
                  "initialized": true,
                  "mode": "project",
                  "projectSortMode": "priority"
                }
              }
            }
            """;

        var projected = JsonNode.Parse(ProjectDesktopSidebarStateText(current))!.AsObject();
        var persisted = projected["electron-persisted-atom-state"]!.AsObject();
        var collapsed = persisted["sidebar-collapsed-sections-v1"]!.AsObject();
        var preferences = persisted["flat-project-sidebar-preferences-v1"]!.AsObject();
        if (projected["keep"]?.GetValue<string>() != "value" ||
            collapsed["chats"]?.GetValue<bool>() != false ||
            collapsed["pinned"]?.GetValue<bool>() != false ||
            preferences["mode"]?.GetValue<string>() != "list" ||
            preferences["chatSortMode"]?.GetValue<string>() != "updated_at" ||
            preferences["projectSortMode"]?.GetValue<string>() != "updated_at" ||
            preferences["initialized"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("Codex desktop sidebar projection failed.");
        }

        const string threadId = "019f4be7-aa6e-72b2-84bf-4e35b9c5f25f";
        if (BuildThreadDeepLink(threadId) != "codex://threads/" + threadId)
        {
            throw new InvalidOperationException("Codex task deep link projection failed.");
        }

        var projectPath = Path.Combine("C:\\", "Users", "Example User", "Demo");
        var expectedNewThreadLink =
            "codex://threads/new?path=" + Uri.EscapeDataString(Path.GetFullPath(projectPath));
        if (BuildNewThreadDeepLink(projectPath) != expectedNewThreadLink)
        {
            throw new InvalidOperationException("Codex new-task deep link projection failed.");
        }
    }

    internal static void ValidateSharedProfileProjection()
    {
        static void AssertSharedMcpServersPreserved(string config)
        {
            config = config.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (!config.Contains(
                    "[mcp_servers.openaiDeveloperDocs]\nurl = \"https://developers.openai.com/mcp\"",
                    StringComparison.Ordinal) ||
                !config.Contains(
                    "[mcp_servers.\"local tools\"]\ncommand = \"local-mcp.exe\"",
                    StringComparison.Ordinal) ||
                !config.Contains(
                    "[mcp_servers.\"local tools\".env]\nLOCAL_MCP_TEST = \"preserved\"",
                    StringComparison.Ordinal) ||
                config.Split(
                    "[mcp_servers.openaiDeveloperDocs]",
                    StringSplitOptions.None).Length != 2)
            {
                throw new InvalidOperationException(
                    "Shared MCP server configuration was lost or duplicated during account projection.");
            }
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-account-profile-test-" + Guid.NewGuid().ToString("N"));
        var tokenHome = Path.Combine(root, "token");
        var apiHome = Path.Combine(root, "api");
        var differentHome = Path.Combine(root, "different");
        var sharedHome = Path.Combine(root, "shared");
        var oldSharedHome = Environment.GetEnvironmentVariable(SharedCodexHomeOverrideVariable);

        try
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, sharedHome);
            Directory.CreateDirectory(tokenHome);
            Directory.CreateDirectory(apiHome);
            Directory.CreateDirectory(differentHome);
            Directory.CreateDirectory(Path.Combine(tokenHome, "sessions"));
            File.WriteAllText(Path.Combine(tokenHome, "sessions", "token-thread.jsonl"), "{}");
            File.WriteAllText(
                Path.Combine(tokenHome, AuthFileName),
                "{\"OPENAI_API_KEY\":\"virtual-a\",\"personal_access_token\":\"virtual-a\"}");
            File.WriteAllText(
                Path.Combine(apiHome, AuthFileName),
                "{\n  \"OPENAI_API_KEY\": \"virtual-api\"\n}");
            File.WriteAllText(
                Path.Combine(differentHome, AuthFileName),
                "{\"OPENAI_API_KEY\":\"virtual-b\",\"personal_access_token\":\"virtual-b\"}");
            File.WriteAllText(
                Path.Combine(tokenHome, ConfigFileName),
                "service_tier = \"priority\"\n\n[features]\njs_repl = false\n");
            File.WriteAllText(
                Path.Combine(apiHome, ConfigFileName),
                "model = \"stale\"\n\n[features]\njs_repl = false\n");
            File.WriteAllText(
                Path.Combine(differentHome, ConfigFileName),
                "service_tier = \"default\"\n\n[features]\njs_repl = false\n");

            var tokenAccount = new AccountRecord
            {
                Name = "token-test",
                CodexHome = tokenHome
            };
            var apiAccount = new AccountRecord
            {
                Name = "api-test",
                AuthKind = AccountAuthKind.CompatibleApi,
                CodexHome = apiHome,
                ApiProviderName = "OpenAI",
                ApiBaseUrl = "https://example.invalid",
                ApiModel = CompatibleApiDefaultModel,
                ApiWireApi = "responses"
            };
            var differentAccount = new AccountRecord
            {
                Name = "different-test",
                CodexHome = differentHome
            };

            if (!CanInitializeEmptySharedProfileWithoutNetwork(
                    tokenAccount,
                    Path.Combine(tokenHome, AuthFileName),
                    Path.Combine(tokenHome, ConfigFileName)))
            {
                throw new InvalidOperationException(
                    "A valid local credential could not initialize an empty shared profile without network login.");
            }
            Directory.CreateDirectory(sharedHome);
            File.WriteAllText(
                Path.Combine(sharedHome, ConfigFileName),
                "[mcp_servers.openaiDeveloperDocs]\n" +
                "url = \"https://developers.openai.com/mcp\"\n\n" +
                "[mcp_servers.\"local tools\"]\n" +
                "command = \"local-mcp.exe\"\n\n" +
                "[mcp_servers.\"local tools\".env]\n" +
                "LOCAL_MCP_TEST = \"preserved\"\n");
            SharedHistoryMerger.Merge([tokenHome], sharedHome);
            var tokenProjection = ProjectAccessTokenAccount(
                tokenAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop);
            var sharedAuthPath = Path.Combine(sharedHome, AuthFileName);
            var sharedConfigPath = Path.Combine(sharedHome, ConfigFileName);
            var selectionPath = Path.Combine(sharedHome, DesktopSelectionFileName);
            var sharedTokenConfig = File.ReadAllText(Path.Combine(sharedHome, ConfigFileName));
            var sourceTokenConfigText = File.ReadAllText(Path.Combine(tokenHome, ConfigFileName));
            AssertSharedMcpServersPreserved(sharedTokenConfig);
            if (tokenProjection.SharedCredentialsReused ||
                !tokenProjection.DesktopLoginRequired ||
                File.Exists(sharedAuthPath) ||
                !IsDesktopSelectionForAccount(sharedHome, tokenAccount) ||
                !sharedTokenConfig.Contains(
                    "requires_openai_auth = true",
                    StringComparison.Ordinal) ||
                !sharedTokenConfig.Contains(
                    "name = \"token-test\"",
                    StringComparison.Ordinal) ||
                !sharedTokenConfig.Contains(
                    "cli_auth_credentials_store = \"file\"",
                    StringComparison.Ordinal) ||
                !sharedTokenConfig.Contains(
                    "experimental_bearer_token = \"virtual-a\"",
                    StringComparison.Ordinal) ||
                !sourceTokenConfigText.Contains(
                    "requires_openai_auth = true",
                    StringComparison.Ordinal) ||
                sourceTokenConfigText.Contains(
                    "experimental_bearer_token",
                    StringComparison.Ordinal) ||
                sourceTokenConfigText.Contains(
                    "cli_auth_credentials_store",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "First Access Token desktop projection did not prepare the separated ChatGPT login flow.");
            }
            var service = new CodexCliService();
            if (!service.IsSharedCredentialAlreadySelected(tokenAccount) ||
                !CanReuseSharedProfileWithoutNetwork(
                    tokenAccount,
                    Path.Combine(tokenHome, ConfigFileName),
                    AccessTokenSharedProfileMode.ChatGptDesktop))
            {
                throw new InvalidOperationException(
                    "A first-login desktop selection could not be reused while waiting for ChatGPT OAuth.");
            }

            const string desktopOAuth = """
                {
                  "auth_mode": "chatgpt",
                  "tokens": {
                    "id_token": "virtual-id",
                    "access_token": "virtual-access",
                    "refresh_token": "virtual-refresh"
                  }
                }
                """;
            File.WriteAllText(sharedAuthPath, desktopOAuth);
            var reusedTokenProjection = CreateReusedSharedProfileProjection(
                tokenAccount,
                new LoginStatus());
            var globalDesktopAuthPath = GetGlobalStoredDesktopAuthPath(sharedHome);
            if (reusedTokenProjection.DesktopLoginRequired ||
                !IsChatGptDesktopAuthJson(sharedAuthPath) ||
                !IsChatGptDesktopAuthJson(globalDesktopAuthPath) ||
                File.ReadAllText(globalDesktopAuthPath) != desktopOAuth)
            {
                throw new InvalidOperationException(
                    "A completed ChatGPT desktop login was not recognized and saved as the reusable global OAuth snapshot.");
            }

            var cockpitAuthPath = Path.Combine(sharedHome, CockpitAuthFileName);
            File.WriteAllText(cockpitAuthPath, "virtual-cockpit-marker");
            var apiProjection = ProjectCompatibleApiAccount(
                apiAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop);
            var storedTokenAuthPath = GetStoredDesktopAuthPath(sharedHome, tokenAccount);
            if (!PathsEqual(tokenProjection.DefaultCodexHome, sharedHome) ||
                !PathsEqual(apiProjection.DefaultCodexHome, sharedHome) ||
                !PathsEqual(tokenProjection.AccountCodexHome, tokenHome) ||
                !PathsEqual(apiProjection.AccountCodexHome, apiHome) ||
                !File.Exists(Path.Combine(sharedHome, "sessions", "token-thread.jsonl")))
            {
                throw new InvalidOperationException("Desktop account profiles are not sharing the default chat history home.");
            }

            if (apiProjection.SharedCredentialsReused ||
                string.IsNullOrWhiteSpace(apiProjection.AuthBackupPath) ||
                !File.Exists(apiProjection.AuthBackupPath) ||
                string.IsNullOrWhiteSpace(apiProjection.CockpitAuthBackupPath) ||
                !File.Exists(apiProjection.CockpitAuthBackupPath) ||
                string.IsNullOrWhiteSpace(apiProjection.DesktopSelectionBackupPath) ||
                !File.Exists(apiProjection.DesktopSelectionBackupPath) ||
                !IsChatGptDesktopAuthJson(storedTokenAuthPath) ||
                File.ReadAllText(storedTokenAuthPath) != desktopOAuth ||
                File.Exists(cockpitAuthPath) ||
                !IsDesktopSelectionForAccount(sharedHome, apiAccount) ||
                apiProjection.DesktopLoginRequired)
            {
                throw new InvalidOperationException(
                    "Compatible API switch did not preserve the selected account's ChatGPT OAuth before replacing shared auth.");
            }

            AssertProjectedConfig(
                File.ReadAllText(Path.Combine(sharedHome, ConfigFileName)),
                AccountStore.CompatibleApiProviderId,
                CompatibleApiDefaultModel,
                CompatibleApiReasoningEffort,
                false,
                expectedServiceTier: "default");
            if (!IsChatGptDesktopAuthJson(sharedAuthPath) ||
                File.ReadAllText(sharedAuthPath) != desktopOAuth)
            {
                throw new InvalidOperationException(
                    "Compatible API desktop projection did not preserve the ChatGPT UI login.");
            }
            var sharedApiConfig = File.ReadAllText(sharedConfigPath);
            AssertSharedMcpServersPreserved(sharedApiConfig);
            if (!sharedApiConfig.Contains(
                    "experimental_bearer_token = \"virtual-api\"",
                    StringComparison.Ordinal) ||
                !sharedApiConfig.Contains(
                    "requires_openai_auth = true",
                    StringComparison.Ordinal) ||
                !sharedApiConfig.Contains(
                    "cli_auth_credentials_store = \"file\"",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Compatible API desktop projection did not separate model and UI credentials.");
            }
            if (!service.IsSharedCredentialAlreadySelected(apiAccount))
            {
                throw new InvalidOperationException(
                    "The public local credential-selection check did not recognize semantic JSON equality.");
            }
            if (!IsAccessTokenDesktopSessionSelected(apiAccount, sharedHome, sharedAuthPath))
            {
                throw new InvalidOperationException(
                    "Compatible API desktop selection did not retain a usable ChatGPT session.");
            }
            var reprojectedSharedApiConfig = PreserveSharedMcpServerSections(
                sharedApiConfig,
                ProjectCompatibleApiConfigText(
                    sharedApiConfig,
                    apiAccount,
                    requiresOpenAiAuth: true,
                    providerBearerToken: "virtual-api",
                    forceFileAuthStore: true));
            if (!string.Equals(
                    NormalizeTextForFingerprint(sharedApiConfig),
                    NormalizeTextForFingerprint(reprojectedSharedApiConfig),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Compatible API desktop config projection was not idempotent.");
            }
            if (!CanReuseSharedProfileWithoutNetwork(
                    apiAccount,
                    Path.Combine(apiHome, ConfigFileName)))
            {
                throw new InvalidOperationException(
                    "An unchanged shared credential/config pair did not qualify for local startup reuse.");
            }

            var restoredTokenProjection = ProjectAccessTokenAccount(
                tokenAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop);
            var restoredTokenConfig = File.ReadAllText(sharedConfigPath);
            AssertSharedMcpServersPreserved(restoredTokenConfig);
            if (restoredTokenProjection.SharedCredentialsReused ||
                restoredTokenProjection.DesktopLoginRequired ||
                !IsDesktopSelectionForAccount(sharedHome, tokenAccount) ||
                !IsChatGptDesktopAuthJson(sharedAuthPath) ||
                File.ReadAllText(sharedAuthPath) != desktopOAuth ||
                !restoredTokenConfig.Contains(
                    "experimental_bearer_token = \"virtual-a\"",
                    StringComparison.Ordinal) ||
                !restoredTokenConfig.Contains(
                    "requires_openai_auth = true",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Returning to an authenticated Access Token account did not restore its ChatGPT OAuth session.");
            }

            if (service.IsSharedCredentialAlreadySelected(differentAccount))
            {
                throw new InvalidOperationException(
                    "The public local credential-selection check accepted a different credential.");
            }
            if (CanReuseSharedProfileWithoutNetwork(
                    differentAccount,
                    Path.Combine(differentHome, ConfigFileName),
                    AccessTokenSharedProfileMode.ChatGptDesktop) ||
                CanInitializeEmptySharedProfileWithoutNetwork(
                    differentAccount,
                    Path.Combine(differentHome, AuthFileName),
                    Path.Combine(differentHome, ConfigFileName)))
            {
                throw new InvalidOperationException(
                    "A different credential incorrectly bypassed the network preflight.");
            }
            var differentProjection = ProjectAccessTokenAccount(
                differentAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop);
            var differentConfig = File.ReadAllText(sharedConfigPath);
            if (differentProjection.SharedCredentialsReused ||
                differentProjection.DesktopLoginRequired ||
                string.IsNullOrWhiteSpace(differentProjection.AuthBackupPath) ||
                !File.Exists(differentProjection.AuthBackupPath) ||
                string.IsNullOrWhiteSpace(differentProjection.DesktopSelectionBackupPath) ||
                !File.Exists(differentProjection.DesktopSelectionBackupPath) ||
                !IsChatGptDesktopAuthJson(sharedAuthPath) ||
                File.ReadAllText(sharedAuthPath) != desktopOAuth ||
                !IsDesktopSelectionForAccount(sharedHome, differentAccount) ||
                !differentConfig.Contains(
                    "experimental_bearer_token = \"virtual-b\"",
                    StringComparison.Ordinal) ||
                !ReadDesktopServiceTier(differentConfig).Equals(
                    "default",
                    StringComparison.Ordinal) ||
                differentConfig.Contains(
                    "experimental_bearer_token = \"virtual-a\"",
                    StringComparison.Ordinal) ||
                !service.IsSharedCredentialAlreadySelected(differentAccount))
            {
                throw new InvalidOperationException(
                    "An Access Token account without its own OAuth snapshot did not reuse the stable global ChatGPT login.");
            }

            service.RestoreWindowsClientAccountProjection(differentProjection);
            var rolledBackTokenConfig = File.ReadAllText(sharedConfigPath);
            if (!IsDesktopSelectionForAccount(sharedHome, tokenAccount) ||
                !IsChatGptDesktopAuthJson(sharedAuthPath) ||
                File.ReadAllText(sharedAuthPath) != desktopOAuth ||
                !rolledBackTokenConfig.Contains(
                    "experimental_bearer_token = \"virtual-a\"",
                    StringComparison.Ordinal) ||
                !ReadDesktopServiceTier(rolledBackTokenConfig).Equals(
                    "priority",
                    StringComparison.Ordinal) ||
                rolledBackTokenConfig.Contains(
                    "experimental_bearer_token = \"virtual-b\"",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Dual-login credential and selection backups were not restorable.");
            }

            // Exercise the real first direct-App migration: the shared config is clean, a
            // legacy selection remains, and Codex++ left its cockpit credential behind.
            File.WriteAllText(sharedConfigPath, "");
            File.WriteAllText(cockpitAuthPath, "stale-cockpit-credential");
            var directTokenProjection = ProjectAccessTokenAccount(
                differentAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ApiCompatible);
            var directTokenConfig = File.ReadAllText(sharedConfigPath);
            var directTokenTopLevelLines = directTokenConfig
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .TakeWhile(line => !line.TrimStart().StartsWith("[", StringComparison.Ordinal))
                .Select(line => line.Trim())
                .ToList();
            using (var directTokenAuth = JsonDocument.Parse(File.ReadAllText(sharedAuthPath)))
            {
                if (directTokenProjection.DesktopLoginRequired ||
                    File.Exists(selectionPath) ||
                    File.Exists(cockpitAuthPath) ||
                    string.IsNullOrWhiteSpace(directTokenProjection.CockpitAuthBackupPath) ||
                    !File.Exists(directTokenProjection.CockpitAuthBackupPath) ||
                    directTokenTopLevelLines.Count(IsCliAuthCredentialsStoreLine) != 1 ||
                    !directTokenTopLevelLines.Contains(
                        "cli_auth_credentials_store = \"file\"",
                        StringComparer.Ordinal) ||
                    !directTokenConfig.Contains(
                        "requires_openai_auth = true",
                        StringComparison.Ordinal) ||
                    directTokenConfig.Contains(
                        "experimental_bearer_token",
                        StringComparison.Ordinal) ||
                    !TryReadJsonString(directTokenAuth.RootElement, "auth_mode", out var directAuthMode) ||
                    !directAuthMode.Equals(ApiKeyAuthMode, StringComparison.OrdinalIgnoreCase) ||
                    !IsAccessTokenDesktopAuthSelected(
                        Path.Combine(differentHome, AuthFileName),
                        sharedAuthPath) ||
                    !service.IsSharedProfileAlreadySelected(differentAccount))
                {
                    throw new InvalidOperationException(
                        "One-click Access Token projection did not write a reusable official-App API-key login.");
                }
            }

            // Repeat the direct-App migration from a deliberately dirty shared profile for an
            // API-key account as well.  This protects the second one-click path from silently
            // retaining legacy bearer-token or desktop-selection state.
            File.WriteAllText(
                sharedConfigPath,
                "cli_auth_credentials_store = \"keyring\"\n" +
                "cli_auth_credentials_store = \"legacy\"\n" +
                "experimental_bearer_token = \"stale-shared-bearer\"\n");
            File.WriteAllText(cockpitAuthPath, "stale-api-cockpit-credential");
            WriteDesktopSelection(sharedHome, differentAccount);
            var directApiProjection = ProjectCompatibleApiAccount(
                apiAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ApiCompatible);
            var directApiConfig = File.ReadAllText(sharedConfigPath);
            var directApiSourceConfig = File.ReadAllText(Path.Combine(apiHome, ConfigFileName));
            var directApiTopLevelLines = directApiConfig
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .TakeWhile(line => !line.TrimStart().StartsWith("[", StringComparison.Ordinal))
                .Select(line => line.Trim())
                .ToList();
            using (var directApiAuth = JsonDocument.Parse(File.ReadAllText(sharedAuthPath)))
            {
                if (directApiProjection.DesktopLoginRequired ||
                    File.Exists(selectionPath) ||
                    File.Exists(cockpitAuthPath) ||
                    string.IsNullOrWhiteSpace(directApiProjection.CockpitAuthBackupPath) ||
                    !File.Exists(directApiProjection.CockpitAuthBackupPath) ||
                    string.IsNullOrWhiteSpace(directApiProjection.DesktopSelectionBackupPath) ||
                    !File.Exists(directApiProjection.DesktopSelectionBackupPath) ||
                    directApiTopLevelLines.Count(IsCliAuthCredentialsStoreLine) != 1 ||
                    !directApiTopLevelLines.Contains(
                        "cli_auth_credentials_store = \"file\"",
                        StringComparer.Ordinal) ||
                    !directApiConfig.Contains(
                        "requires_openai_auth = true",
                        StringComparison.Ordinal) ||
                    directApiConfig.Contains(
                        "experimental_bearer_token",
                        StringComparison.Ordinal) ||
                    directApiSourceConfig.Contains(
                        "cli_auth_credentials_store",
                        StringComparison.Ordinal) ||
                    directApiSourceConfig.Contains(
                        "experimental_bearer_token",
                        StringComparison.Ordinal) ||
                    !TryReadJsonString(directApiAuth.RootElement, "auth_mode", out var directApiAuthMode) ||
                    !directApiAuthMode.Equals(ApiKeyAuthMode, StringComparison.OrdinalIgnoreCase) ||
                    !IsAccessTokenDesktopAuthSelected(
                        Path.Combine(apiHome, AuthFileName),
                        sharedAuthPath) ||
                    !service.IsSharedProfileAlreadySelected(apiAccount))
                {
                    throw new InvalidOperationException(
                        "One-click compatible-API projection did not write a reusable official-App API-key login.");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, oldSharedHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    internal static void ValidateExplicitChatGptFeatureProjection()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-explicit-chatgpt-feature-" + Guid.NewGuid().ToString("N"));
        var tokenHome = Path.Combine(root, "token");
        var apiHome = Path.Combine(root, "api");
        var oauthAHome = Path.Combine(root, "oauth-a");
        var oauthBHome = Path.Combine(root, "oauth-b");
        var invalidOAuthHome = Path.Combine(root, "oauth-invalid");
        var sharedHome = Path.Combine(root, "shared");
        var oldSharedHome = Environment.GetEnvironmentVariable(SharedCodexHomeOverrideVariable);

        try
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, sharedHome);
            foreach (var path in new[]
                     {
                         tokenHome,
                         apiHome,
                         oauthAHome,
                         oauthBHome,
                         invalidOAuthHome,
                         sharedHome
                     })
            {
                Directory.CreateDirectory(path);
            }

            File.WriteAllText(
                Path.Combine(tokenHome, AuthFileName),
                "{\"OPENAI_API_KEY\":\"virtual-pat\",\"personal_access_token\":\"virtual-pat\"}");
            File.WriteAllText(
                Path.Combine(tokenHome, ConfigFileName),
                "service_tier = \"priority\"\n");
            File.WriteAllText(
                Path.Combine(apiHome, AuthFileName),
                "{\"OPENAI_API_KEY\":\"virtual-api\"}");
            File.WriteAllText(
                Path.Combine(apiHome, ConfigFileName),
                "service_tier = \"default\"\n");
            File.WriteAllText(
                Path.Combine(oauthAHome, AuthFileName),
                BuildTestOAuthAuth("a-original", "account-A"));
            File.WriteAllText(
                Path.Combine(oauthBHome, AuthFileName),
                BuildTestOAuthAuth("b-original", "account-B"));
            File.WriteAllText(
                Path.Combine(invalidOAuthHome, AuthFileName),
                BuildTestOAuthAuth("missing-account-id"));
            File.WriteAllText(
                Path.Combine(oauthAHome, ConfigFileName),
                AccountStore.BuildOfficialOAuthConfig());
            File.WriteAllText(
                Path.Combine(oauthBHome, ConfigFileName),
                AccountStore.BuildOfficialOAuthConfig());
            File.WriteAllText(
                Path.Combine(invalidOAuthHome, ConfigFileName),
                AccountStore.BuildOfficialOAuthConfig());

            var tokenAccount = new AccountRecord
            {
                Name = "feature-token",
                CodexHome = tokenHome,
                AuthKind = AccountAuthKind.AccessToken
            };
            var apiAccount = new AccountRecord
            {
                Name = "feature-api",
                CodexHome = apiHome,
                AuthKind = AccountAuthKind.CompatibleApi,
                ApiProviderName = "OpenAI",
                ApiBaseUrl = "https://example.invalid",
                ApiModel = CompatibleApiDefaultModel,
                ApiWireApi = "responses"
            };
            var oauthA = new AccountRecord
            {
                Name = "feature-oauth-a",
                CodexHome = oauthAHome,
                AuthKind = AccountAuthKind.OfficialOAuth
            };
            var oauthB = new AccountRecord
            {
                Name = "feature-oauth-b",
                CodexHome = oauthBHome,
                AuthKind = AccountAuthKind.OfficialOAuth
            };
            var invalidOAuth = new AccountRecord
            {
                Name = "feature-oauth-invalid",
                CodexHome = invalidOAuthHome,
                AuthKind = AccountAuthKind.OfficialOAuth
            };
            var sameHomeOAuth = new AccountRecord
            {
                Name = "feature-oauth-same-home",
                CodexHome = tokenHome,
                AuthKind = AccountAuthKind.OfficialOAuth
            };
            var service = new CodexCliService();

            static string SnapshotSharedProfile(string path)
            {
                return string.Join(
                    "|",
                    Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                        .Select(file =>
                            Path.GetRelativePath(path, file) + ":" +
                            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
            }

            static void ExpectInvalidFeatureBinding(Action action, string sharedProfileHome)
            {
                var before = SnapshotSharedProfile(sharedProfileHome);
                try
                {
                    action();
                }
                catch (InvalidOperationException)
                {
                    if (SnapshotSharedProfile(sharedProfileHome) != before)
                    {
                        throw new InvalidOperationException(
                            "An invalid ChatGPT feature binding modified the shared profile.");
                    }
                    return;
                }

                throw new InvalidOperationException(
                    "An invalid explicit ChatGPT feature binding was accepted.");
            }

            ExpectInvalidFeatureBinding(() =>
                service.PrepareChatGptFeatureAccount(tokenAccount, null), sharedHome);
            ExpectInvalidFeatureBinding(() =>
                service.PrepareChatGptFeatureAccount(tokenAccount, apiAccount), sharedHome);
            ExpectInvalidFeatureBinding(() =>
                service.PrepareChatGptFeatureAccount(tokenAccount, sameHomeOAuth), sharedHome);
            ExpectInvalidFeatureBinding(() =>
                service.PrepareChatGptFeatureAccount(tokenAccount, invalidOAuth), sharedHome);
            ExpectInvalidFeatureBinding(
                () => service.SwitchWindowsClientAccountWithChatGptFeaturesAsync(
                        oauthA,
                        oauthB,
                        Path.Combine(root, "missing-project-path"),
                        useDreamSkin: false,
                        ThemeMode.System)
                    .GetAwaiter()
                    .GetResult(),
                sharedHome);

            var globalAuthPath = GetGlobalStoredDesktopAuthPath(sharedHome);
            Directory.CreateDirectory(Path.GetDirectoryName(globalAuthPath)!);
            File.WriteAllText(
                globalAuthPath,
                BuildTestOAuthAuth("b-newest-global", "account-B"));
            File.SetLastWriteTimeUtc(globalAuthPath, DateTime.UtcNow.AddMinutes(1));

            service.PrepareChatGptFeatureAccount(tokenAccount, oauthA);
            var tokenProjection = ProjectAccessTokenAccount(
                tokenAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop,
                oauthA);
            var sharedAuthPath = Path.Combine(sharedHome, AuthFileName);
            var sharedConfigPath = Path.Combine(sharedHome, ConfigFileName);
            var selectionPath = Path.Combine(sharedHome, DesktopSelectionFileName);
            var storedAPath = GetStoredDesktopAuthPath(sharedHome, oauthA);
            var storedBPath = GetStoredDesktopAuthPath(sharedHome, oauthB);
            var storedTokenPath = GetStoredDesktopAuthPath(sharedHome, tokenAccount);
            var storedApiPath = GetStoredDesktopAuthPath(sharedHome, apiAccount);
            var tokenConfig = File.ReadAllText(sharedConfigPath);
            using var selectionDocument = JsonDocument.Parse(File.ReadAllText(selectionPath));
            var selectionRoot = selectionDocument.RootElement;
            if (tokenProjection.DesktopLoginRequired ||
                !TryReadChatGptAuthAccountId(sharedAuthPath, out var projectedAccountId) ||
                projectedAccountId != "account-A" ||
                tokenConfig.Split("experimental_bearer_token", StringSplitOptions.None).Length - 1 != 1 ||
                !tokenConfig.Contains(
                    "experimental_bearer_token = \"virtual-pat\"",
                    StringComparison.Ordinal) ||
                !tokenConfig.Contains("requires_openai_auth = true", StringComparison.Ordinal) ||
                !tokenConfig.Contains(
                    "cli_auth_credentials_store = \"file\"",
                    StringComparison.Ordinal) ||
                File.ReadAllText(Path.Combine(tokenHome, ConfigFileName)).Contains(
                    "experimental_bearer_token",
                    StringComparison.Ordinal) ||
                !TryReadDesktopSelectionKeys(
                    sharedHome,
                    out var selectedModelKey,
                    out var selectedAuthKey,
                    out var selectedAuthHome) ||
                selectedModelKey != GetDesktopAccountKey(tokenAccount) ||
                selectedAuthKey != GetDesktopAccountKey(oauthA) ||
                !PathsEqual(selectedAuthHome, oauthAHome) ||
                !selectionRoot.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.GetInt32() != 2 ||
                 !TryReadJsonString(selectionRoot, "mode", out var selectionMode) ||
                 selectionMode != DesktopSelectionModeChatGptAccessToken ||
                 !CanIdentifySharedChatGptFeatureProfileWithoutNetwork(tokenAccount, oauthA) ||
                 CanIdentifySharedChatGptFeatureProfileWithoutNetwork(tokenAccount, oauthB) ||
                 CanIdentifySharedChatGptFeatureProfileWithoutNetwork(apiAccount, oauthA) ||
                 !CanReuseSharedProfileWithoutNetwork(
                    tokenAccount,
                    Path.Combine(tokenHome, ConfigFileName),
                    AccessTokenSharedProfileMode.ChatGptDesktop,
                    oauthA) ||
                 !CanReuseChatGptFeatureProfileForLaunchWithoutNetwork(
                    tokenAccount,
                    Path.Combine(tokenHome, ConfigFileName),
                    oauthA) ||
                CanReuseSharedProfileWithoutNetwork(
                    tokenAccount,
                    Path.Combine(tokenHome, ConfigFileName),
                    AccessTokenSharedProfileMode.ChatGptDesktop,
                    oauthB))
            {
                throw new InvalidOperationException(
                    "Explicit PAT + ChatGPT feature projection did not keep model and OAuth owners separate.");
            }

            // Identity discovery is deliberately narrower than full config reuse. Codex may
            // update a non-identity model preference while it is running; that must not hide the
            // already-selected model/OAuth owners. A changed bearer must still fail closed.
            var runtimePreferenceConfig = Regex.Replace(
                tokenConfig,
                "(?m)^model_reasoning_effort\\s*=.*$",
                "model_reasoning_effort = \"low\"");
            if (runtimePreferenceConfig.Equals(tokenConfig, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The dual-login display discovery fixture had no mutable runtime preference.");
            }
            File.WriteAllText(sharedConfigPath, runtimePreferenceConfig);
            if (CanReuseSharedProfileWithoutNetwork(
                    tokenAccount,
                    Path.Combine(tokenHome, ConfigFileName),
                    AccessTokenSharedProfileMode.ChatGptDesktop,
                    oauthA) ||
                !CanIdentifySharedChatGptFeatureProfileWithoutNetwork(tokenAccount, oauthA) ||
                !CanReuseChatGptFeatureProfileForLaunchWithoutNetwork(
                    tokenAccount,
                    Path.Combine(tokenHome, ConfigFileName),
                    oauthA))
            {
                throw new InvalidOperationException(
                    "A harmless runtime preference change would restart the selected dual-login profile.");
            }

            var mismatchedBearerConfig = tokenConfig.Replace(
                "experimental_bearer_token = \"virtual-pat\"",
                "experimental_bearer_token = \"other-account-secret\"",
                StringComparison.Ordinal);
            File.WriteAllText(sharedConfigPath, mismatchedBearerConfig);
            if (CanIdentifySharedChatGptFeatureProfileWithoutNetwork(tokenAccount, oauthA) ||
                CanReuseChatGptFeatureProfileForLaunchWithoutNetwork(
                    tokenAccount,
                    Path.Combine(tokenHome, ConfigFileName),
                    oauthA))
            {
                throw new InvalidOperationException(
                    "Dual-login display discovery accepted a mismatched model bearer.");
            }
            File.WriteAllText(sharedConfigPath, tokenConfig);

            // With no live/global auth, a newer snapshot belonging to B must still never be
            // selected for an explicit A binding.
            foreach (var fileName in new[]
                     {
                         AuthFileName,
                         CockpitAuthFileName,
                         ConfigFileName,
                         DesktopSelectionFileName,
                         ActiveAccountStateFileName
                     })
            {
                DeleteFileIfPresent(Path.Combine(sharedHome, fileName));
            }
            DeleteFileIfPresent(globalAuthPath);
            Directory.CreateDirectory(Path.GetDirectoryName(storedAPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(storedBPath)!);
            File.WriteAllText(storedAPath, BuildTestOAuthAuth("a-stored", "account-A"));
            File.WriteAllText(storedBPath, BuildTestOAuthAuth("b-newest-store", "account-B"));
            File.SetLastWriteTimeUtc(storedBPath, DateTime.UtcNow.AddMinutes(2));
            _ = ProjectAccessTokenAccount(
                tokenAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop,
                oauthA);
            if (!TryReadChatGptAuthAccountId(sharedAuthPath, out projectedAccountId) ||
                projectedAccountId != "account-A")
            {
                throw new InvalidOperationException(
                    "An explicit OAuth binding fell back to another account's newest snapshot.");
            }

            // A manual login to B must not be accepted merely because the sidecar still claims A.
            var manualBAuth = BuildTestOAuthAuth("b-manual-login", "account-B");
            File.WriteAllText(
                sharedAuthPath,
                manualBAuth);
             service.PrepareChatGptFeatureAccount(tokenAccount, oauthA);
             if (File.ReadAllText(sharedAuthPath) != manualBAuth ||
                 CanIdentifySharedChatGptFeatureProfileWithoutNetwork(tokenAccount, oauthA) ||
                 CanReuseSharedProfileWithoutNetwork(
                    tokenAccount,
                    Path.Combine(tokenHome, ConfigFileName),
                    AccessTokenSharedProfileMode.ChatGptDesktop,
                    oauthA))
            {
                throw new InvalidOperationException(
                    "Prepare mutated or accepted a mismatched live ChatGPT identity before shutdown.");
            }

            _ = ProjectAccessTokenAccount(
                tokenAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop,
                oauthA);
            if (!TryReadChatGptAuthAccountId(sharedAuthPath, out projectedAccountId) ||
                projectedAccountId != "account-A" ||
                !TryReadChatGptAuthAccountId(storedAPath, out var storedAccountId) ||
                storedAccountId != "account-A" ||
                File.Exists(storedTokenPath))
            {
                throw new InvalidOperationException(
                    "A mismatched live OAuth session polluted the explicit owner or model account store.");
            }

            // A normal refresh for A must rotate the OAuth owner's snapshot, never the PAT key.
            Thread.Sleep(15);
            var storedBBeforeRefresh = File.ReadAllText(storedBPath);
            File.WriteAllText(
                sharedAuthPath,
                BuildTestOAuthAuth("a-refreshed", "account-A"));
            _ = CreateReusedSharedProfileProjection(
                tokenAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop,
                oauthA);
            if (!AuthJsonFilesSemanticallyEqual(sharedAuthPath, storedAPath) ||
                !AuthJsonFilesSemanticallyEqual(
                    sharedAuthPath,
                    Path.Combine(oauthAHome, AuthFileName)) ||
                !AuthJsonFilesSemanticallyEqual(sharedAuthPath, globalAuthPath) ||
                File.ReadAllText(storedBPath) != storedBBeforeRefresh ||
                File.Exists(storedTokenPath) ||
                File.Exists(storedApiPath))
            {
                throw new InvalidOperationException(
                    "OAuth refresh rotation was not saved exclusively under the explicit owner during reuse.");
            }

            service.PrepareChatGptFeatureAccount(apiAccount, oauthA);
            var apiProjection = ProjectCompatibleApiAccount(
                apiAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop,
                oauthA);
            var apiConfig = File.ReadAllText(sharedConfigPath);
            if (apiProjection.DesktopLoginRequired ||
                !apiConfig.Contains(
                    "experimental_bearer_token = \"virtual-api\"",
                    StringComparison.Ordinal) ||
                !TryReadChatGptAuthAccountId(sharedAuthPath, out projectedAccountId) ||
                projectedAccountId != "account-A" ||
                !TryReadDesktopSelectionKeys(
                    sharedHome,
                    out selectedModelKey,
                    out selectedAuthKey,
                    out selectedAuthHome) ||
                 selectedModelKey != GetDesktopAccountKey(apiAccount) ||
                 selectedAuthKey != GetDesktopAccountKey(oauthA) ||
                 !PathsEqual(selectedAuthHome, oauthAHome) ||
                 !CanIdentifySharedChatGptFeatureProfileWithoutNetwork(apiAccount, oauthA) ||
                 CanIdentifySharedChatGptFeatureProfileWithoutNetwork(apiAccount, oauthB) ||
                 CanIdentifySharedChatGptFeatureProfileWithoutNetwork(tokenAccount, oauthA) ||
                 !CanReuseSharedProfileWithoutNetwork(
                    apiAccount,
                    Path.Combine(apiHome, ConfigFileName),
                    AccessTokenSharedProfileMode.ChatGptDesktop,
                    oauthA) ||
                CanReuseSharedProfileWithoutNetwork(
                    apiAccount,
                    Path.Combine(apiHome, ConfigFileName),
                    AccessTokenSharedProfileMode.ChatGptDesktop,
                    oauthB))
            {
                throw new InvalidOperationException(
                    "Explicit compatible API + ChatGPT feature projection lost either credential owner.");
            }

            // Deleting a selected model account must scrub its bearer from both the live
            // config and manager-created backups without deleting the OAuth owner's auth.
            var apiBackupDirectory = Path.Combine(
                sharedHome,
                "account-switcher-backups",
                "delete-api-fixture");
            Directory.CreateDirectory(apiBackupDirectory);
            File.WriteAllText(
                Path.Combine(apiBackupDirectory, AuthFileName),
                File.ReadAllText(sharedAuthPath));
            File.WriteAllText(
                Path.Combine(apiBackupDirectory, ConfigFileName),
                "experimental_bearer_token = \"virtual-api\"\n");
            WriteDesktopSelection(apiBackupDirectory, apiAccount, oauthA);
            WriteActiveAccountState(
                apiBackupDirectory,
                apiAccount,
                AccessTokenSharedProfileMode.ChatGptDesktop);
            var apiSourceBeforeDelete = File.ReadAllText(Path.Combine(apiHome, AuthFileName));
            if (!service.DeleteSharedCredentialIfSelected(apiAccount) ||
                File.Exists(sharedAuthPath) ||
                File.Exists(selectionPath) ||
                File.Exists(GetActiveAccountStatePath(sharedHome)) ||
                File.ReadAllText(sharedConfigPath).Contains(
                    "experimental_bearer_token",
                    StringComparison.Ordinal) ||
                File.ReadAllText(Path.Combine(apiBackupDirectory, ConfigFileName)).Contains(
                    "virtual-api",
                    StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(apiBackupDirectory, AuthFileName)) ||
                File.ReadAllText(Path.Combine(apiHome, AuthFileName)) != apiSourceBeforeDelete ||
                !File.Exists(globalAuthPath) ||
                !File.Exists(storedAPath))
            {
                throw new InvalidOperationException(
                    "Deleting a selected API model left a bearer behind or deleted the OAuth owner.");
            }

            _ = ProjectCompatibleApiAccount(
                apiAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop,
                oauthA);
            var oauthBackupDirectory = Path.Combine(
                sharedHome,
                "account-switcher-backups",
                "delete-oauth-fixture");
            Directory.CreateDirectory(oauthBackupDirectory);
            File.WriteAllText(
                Path.Combine(oauthBackupDirectory, AuthFileName),
                File.ReadAllText(sharedAuthPath));
            File.WriteAllText(
                Path.Combine(oauthBackupDirectory, ConfigFileName),
                "experimental_bearer_token = \"virtual-api\"\n");
            WriteDesktopSelection(oauthBackupDirectory, apiAccount, oauthA);
            WriteActiveAccountState(
                oauthBackupDirectory,
                apiAccount,
                AccessTokenSharedProfileMode.ChatGptDesktop);
            var tokenSourceBeforeDelete = File.ReadAllText(Path.Combine(tokenHome, AuthFileName));
            if (!service.DeleteSharedCredentialIfSelected(oauthA) ||
                File.Exists(sharedAuthPath) ||
                File.Exists(selectionPath) ||
                File.Exists(GetActiveAccountStatePath(sharedHome)) ||
                File.Exists(storedAPath) ||
                File.Exists(globalAuthPath) ||
                File.Exists(Path.Combine(oauthBackupDirectory, AuthFileName)) ||
                !File.ReadAllText(Path.Combine(oauthBackupDirectory, ConfigFileName)).Contains(
                    "virtual-api",
                    StringComparison.Ordinal) ||
                File.ReadAllText(sharedConfigPath).Contains(
                    "experimental_bearer_token",
                    StringComparison.Ordinal) ||
                File.ReadAllText(storedBPath) != storedBBeforeRefresh ||
                File.ReadAllText(Path.Combine(tokenHome, AuthFileName)) != tokenSourceBeforeDelete ||
                File.ReadAllText(Path.Combine(apiHome, AuthFileName)) != apiSourceBeforeDelete)
            {
                throw new InvalidOperationException(
                    "Deleting the OAuth owner left live/keyed/global auth behind or damaged another account.");
            }

            // PAT uses the same hybrid bearer channel and must receive the same deletion scrub.
            _ = ProjectAccessTokenAccount(
                tokenAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop,
                oauthB);
            _ = CreateReusedSharedProfileProjection(
                tokenAccount,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ChatGptDesktop,
                oauthB);
            if (!File.ReadAllText(sharedConfigPath).Contains(
                    "experimental_bearer_token = \"virtual-pat\"",
                    StringComparison.Ordinal) ||
                !service.DeleteSharedCredentialIfSelected(tokenAccount) ||
                File.ReadAllText(sharedConfigPath).Contains(
                    "experimental_bearer_token",
                    StringComparison.Ordinal) ||
                File.Exists(sharedAuthPath) ||
                File.ReadAllText(Path.Combine(tokenHome, AuthFileName)) != tokenSourceBeforeDelete ||
                !TryReadChatGptAuthAccountId(globalAuthPath, out var remainingGlobalAccountId) ||
                remainingGlobalAccountId != "account-B")
            {
                throw new InvalidOperationException(
                    "Deleting a selected PAT model left its bearer behind or damaged the OAuth owner.");
            }

            // Corrupt/missing ownership sidecars must not leave the deleted model's exact
            // secret in a stale live config, nor scrub a different account's value.
            File.WriteAllText(
                sharedConfigPath,
                "experimental_bearer_token = \"virtual-api\"\n" +
                "experimental_bearer_token = \"other-account-secret\"\n");
            if (service.DeleteSharedCredentialIfSelected(apiAccount) ||
                File.ReadAllText(sharedConfigPath).Contains(
                    "virtual-api",
                    StringComparison.Ordinal) ||
                !File.ReadAllText(sharedConfigPath).Contains(
                    "other-account-secret",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Deleting an unselected model did not precisely scrub its stale live bearer.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, oldSharedHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    internal static void ValidateServiceTierAccountIsolation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-service-tier-isolation-" + Guid.NewGuid().ToString("N"));
        var accountAHome = Path.Combine(root, "account-a");
        var accountBHome = Path.Combine(root, "account-b");
        var sharedHome = Path.Combine(root, "shared");
        var oldSharedHome = Environment.GetEnvironmentVariable(SharedCodexHomeOverrideVariable);

        try
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, sharedHome);
            Directory.CreateDirectory(accountAHome);
            Directory.CreateDirectory(accountBHome);
            Directory.CreateDirectory(sharedHome);

            var accountA = new AccountRecord
            {
                Name = "tier-a",
                CodexHome = accountAHome,
                AuthKind = AccountAuthKind.AccessToken
            };
            var accountB = new AccountRecord
            {
                Name = "tier-b",
                CodexHome = accountBHome,
                AuthKind = AccountAuthKind.AccessToken
            };
            File.WriteAllText(
                Path.Combine(accountAHome, AuthFileName),
                "{\"personal_access_token\":\"tier-a-token\"}");
            File.WriteAllText(
                Path.Combine(accountBHome, AuthFileName),
                "{\"personal_access_token\":\"tier-b-token\"}");
            File.WriteAllText(
                Path.Combine(accountAHome, ConfigFileName),
                "service_tier = \"priority\"\n\n[features]\njs_repl = false\n");
            File.WriteAllText(
                Path.Combine(accountBHome, ConfigFileName),
                "service_tier = \"default\"\n\n[features]\njs_repl = false\n");

            _ = ProjectAccessTokenAccount(
                accountA,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ApiCompatible);
            var sharedConfigPath = Path.Combine(sharedHome, ConfigFileName);
            if (!ReadDesktopServiceTier(File.ReadAllText(sharedConfigPath)).Equals(
                    "priority",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The first account's Fast selection was not projected to the shared Codex profile.");
            }

            // Simulate the native Codex picker changing A back to Standard, then capture it
            // before the next account is projected.
            File.WriteAllText(
                sharedConfigPath,
                UpsertDesktopServiceTier(File.ReadAllText(sharedConfigPath), "default"));
            PersistSharedServiceTierToSelectedAccount();
            if (!ReadAccountServiceTier(accountAHome).Equals("default", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The shared Standard selection was not written back to account A.");
            }

            _ = ProjectAccessTokenAccount(
                accountB,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ApiCompatible);
            if (!ReadDesktopServiceTier(File.ReadAllText(sharedConfigPath)).Equals(
                    "default",
                    StringComparison.Ordinal) ||
                !ReadAccountServiceTier(accountAHome).Equals("default", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Account B inherited account A's service tier or changed account A's stored choice.");
            }

            // A later Fast choice belongs to B only. Returning to A must project A's own
            // Standard value instead of carrying B's shared value across the switch.
            File.WriteAllText(
                sharedConfigPath,
                UpsertDesktopServiceTier(File.ReadAllText(sharedConfigPath), "priority"));
            PersistSharedServiceTierToSelectedAccount();
            if (!ReadAccountServiceTier(accountBHome).Equals("priority", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The shared Fast selection was not written back to account B.");
            }

            _ = ProjectAccessTokenAccount(
                accountA,
                new LoginStatus(),
                AccessTokenSharedProfileMode.ApiCompatible);
            if (!ReadDesktopServiceTier(File.ReadAllText(sharedConfigPath)).Equals(
                    "default",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Returning to account A incorrectly inherited account B's Fast selection.");
            }

            // Credential equality is not account identity. If two account directories happen
            // to contain the same token, deleting the inactive one must leave A's shared
            // projection and active marker untouched.
            File.Copy(
                Path.Combine(accountAHome, AuthFileName),
                Path.Combine(accountBHome, AuthFileName),
                overwrite: true);
            var service = new CodexCliService();
            if (service.DeleteSharedCredentialIfSelected(accountB) ||
                !File.Exists(Path.Combine(sharedHome, AuthFileName)) ||
                !File.Exists(GetActiveAccountStatePath(sharedHome)))
            {
                throw new InvalidOperationException(
                    "Deleting an inactive account with the same credential cleared A's shared projection.");
            }
            if (!service.DeleteSharedCredentialIfSelected(accountA) ||
                File.Exists(Path.Combine(sharedHome, AuthFileName)) ||
                File.Exists(GetActiveAccountStatePath(sharedHome)))
            {
                throw new InvalidOperationException(
                    "Deleting the active account did not clear its shared projection and marker.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, oldSharedHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    internal static void ValidateOfficialDeviceAuthorization()
    {
        const string verificationUrl = "https://auth.openai.com/codex/device";
        const string userCode = "A1B2-C3D4E";
        var measuredOutput =
            "Welcome to Codex [v\u001b[90m0.144.1\u001b[0m]\n" +
            "Follow these steps to sign in with ChatGPT using device code authorization:\n" +
            "1. Open this link in your browser and sign in to your account\n" +
            "   \u001b[94m" + verificationUrl + "\u001b[0m\n" +
            "2. Enter this one-time code (expires in 15 minutes)\n" +
            "   \u001b[94m" + userCode + "\u001b[0m\n";
        if (!TryParseOfficialDeviceAuthorization(
                measuredOutput,
                out var parsedUrl,
                out var parsedCode) ||
            !parsedUrl.Equals(verificationUrl, StringComparison.Ordinal) ||
            !parsedCode.Equals(userCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Codex 0.144.1 device-auth output was not parsed correctly.");
        }

        foreach (var rejectedOutput in new[]
                 {
                     measuredOutput.Replace(
                         verificationUrl,
                         "http://auth.openai.com/codex/device",
                         StringComparison.Ordinal),
                     measuredOutput.Replace(
                         verificationUrl,
                         "https://auth.openai.com.evil.invalid/codex/device",
                         StringComparison.Ordinal),
                     measuredOutput.Replace(
                         verificationUrl,
                         "https://chatgpt.com/codex/device",
                         StringComparison.Ordinal)
                 })
        {
            if (TryParseOfficialDeviceAuthorization(rejectedOutput, out _, out _))
            {
                throw new InvalidOperationException(
                    "A non-official device authorization URL was accepted.");
            }
        }

        var maskedOutput = MaskSensitive(measuredOutput);
        if (maskedOutput.Contains(userCode, StringComparison.OrdinalIgnoreCase) ||
            maskedOutput.Contains("\u001b[", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Device authorization output was not redacted before diagnostics.");
        }

        var profileRoot = GetDeviceAuthBrowserTempRoot();
        var ownedProfile = Path.Combine(
            profileRoot,
            DeviceAuthBrowserProfilePrefix + "validation-" + Guid.NewGuid().ToString("N"));
        var edgeStart = BuildDeviceAuthBrowserStartInfo(
            Path.Combine("C:\\Program Files (x86)", "Microsoft", "Edge", "Application", "msedge.exe"),
            DeviceAuthBrowserKind.Edge,
            ownedProfile,
            verificationUrl);
        var edgeArguments = edgeStart.ArgumentList.ToArray();
        if (edgeStart.UseShellExecute ||
            !edgeArguments.Contains("--inprivate", StringComparer.Ordinal) ||
            !edgeArguments.Contains("--no-first-run", StringComparer.Ordinal) ||
            !edgeArguments.Contains("--new-window", StringComparer.Ordinal) ||
            !edgeArguments.Contains(verificationUrl, StringComparer.Ordinal) ||
            !edgeArguments.Any(value => value.StartsWith("--user-data-dir=", StringComparison.Ordinal)) ||
            edgeArguments.Any(value => value.Contains("--profile-directory", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Edge device authorization did not use a one-time InPrivate profile.");
        }

        var chromeStart = BuildDeviceAuthBrowserStartInfo(
            Path.Combine("C:\\Program Files", "Google", "Chrome", "Application", "chrome.exe"),
            DeviceAuthBrowserKind.Chrome,
            ownedProfile,
            verificationUrl);
        if (!chromeStart.ArgumentList.Contains("--incognito"))
        {
            throw new InvalidOperationException(
                "Chrome device authorization did not use an incognito profile.");
        }

        var outsideProfile = Path.Combine(
            Path.GetTempPath(),
            DeviceAuthBrowserProfilePrefix + "outside");
        if (IsOwnedDeviceAuthBrowserProfile(outsideProfile))
        {
            throw new InvalidOperationException(
                "A browser profile outside the dedicated temp root was treated as owned.");
        }

        Directory.CreateDirectory(ownedProfile);
        File.WriteAllText(Path.Combine(ownedProfile, "cleanup-test.txt"), "temporary");
        TryDeleteOwnedDeviceAuthBrowserProfile(ownedProfile);
        if (Directory.Exists(ownedProfile))
        {
            throw new InvalidOperationException(
                "The owned one-time browser profile was not deleted.");
        }
    }

    internal static void ValidateOfficialOAuthProfileProjection()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-official-oauth-profile-" + Guid.NewGuid().ToString("N"));
        var accountAHome = Path.Combine(root, "oauth-a");
        var accountBHome = Path.Combine(root, "oauth-b");
        var sharedHome = Path.Combine(root, "shared");
        var oldSharedHome = Environment.GetEnvironmentVariable(SharedCodexHomeOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, sharedHome);
            Directory.CreateDirectory(accountAHome);
            Directory.CreateDirectory(accountBHome);
            Directory.CreateDirectory(sharedHome);
            File.WriteAllText(
                Path.Combine(sharedHome, ConfigFileName),
                "model_provider = \"unmanaged\"\n" +
                "chatgpt_base_url = \"https://stale.invalid\"\n\n" +
                "[model_providers.unmanaged]\n" +
                "base_url = \"https://stale.invalid\"\n" +
                "wire_api = \"responses\"\n");
            var accountA = new AccountRecord
            {
                Name = "oauth-a",
                CodexHome = accountAHome,
                AuthKind = AccountAuthKind.OfficialOAuth
            };
            var accountB = new AccountRecord
            {
                Name = "oauth-b",
                CodexHome = accountBHome,
                AuthKind = AccountAuthKind.OfficialOAuth
            };
            var authAPath = Path.Combine(accountAHome, AuthFileName);
            var authBPath = Path.Combine(accountBHome, AuthFileName);
            File.WriteAllText(authAPath, BuildTestOAuthAuth("a-original", "official-account-A"));
            File.WriteAllText(authBPath, BuildTestOAuthAuth("b-original", "official-account-B"));
            File.WriteAllText(
                Path.Combine(accountAHome, ConfigFileName),
                AccountStore.BuildOfficialOAuthConfig());
            File.WriteAllText(
                Path.Combine(accountBHome, ConfigFileName),
                AccountStore.BuildOfficialOAuthConfig());

            _ = ProjectOfficialOAuthAccount(accountA, new LoginStatus());
            var sharedAuthPath = Path.Combine(sharedHome, AuthFileName);
            var sharedConfigPath = Path.Combine(sharedHome, ConfigFileName);
            if (!File.ReadAllText(sharedAuthPath).Contains("a-original", StringComparison.Ordinal) ||
                !IsDesktopSelectionForAccount(sharedHome, accountA))
            {
                throw new InvalidOperationException("The first ChatGPT account was not projected independently.");
            }

            // Simulate the official App rotating A's refresh token while A is selected.
            Thread.Sleep(15);
            File.WriteAllText(
                sharedAuthPath,
                BuildTestOAuthAuth("a-refreshed", "official-account-A"));
            _ = ProjectOfficialOAuthAccount(accountB, new LoginStatus());
            if (!File.ReadAllText(sharedAuthPath).Contains("b-original", StringComparison.Ordinal) ||
                File.ReadAllText(authBPath).Contains("a-refreshed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Switching to OAuth B leaked OAuth A credentials.");
            }

            Thread.Sleep(15);
            _ = ProjectOfficialOAuthAccount(accountA, new LoginStatus());
            var restoredA = File.ReadAllText(sharedAuthPath);
            if (!restoredA.Contains("a-refreshed", StringComparison.Ordinal) ||
                !File.ReadAllText(authAPath).Contains("a-refreshed", StringComparison.Ordinal) ||
                restoredA.Contains("b-original", StringComparison.Ordinal) ||
                !CanReuseOfficialOAuthSharedProfile(accountA))
            {
                throw new InvalidOperationException(
                    "An OAuth refresh-token rotation was not restored to the exact originating account."
                );
            }

            // A successful browser re-login updates the exact owner snapshot, but must never
            // hot-swap auth.json underneath a running official App.
            var liveSharedBeforeRelogin = File.ReadAllText(sharedAuthPath);
            File.WriteAllText(
                authAPath,
                BuildTestOAuthAuth("a-relogin", "official-account-A"));
            PersistSuccessfulOfficialOAuthLogin(accountA);
            var storedAPath = GetStoredDesktopAuthPath(sharedHome, accountA);
            if (File.ReadAllText(sharedAuthPath) != liveSharedBeforeRelogin ||
                !File.ReadAllText(storedAPath).Contains("a-relogin", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A successful OAuth re-login was not isolated from the running shared credential.");
            }

            // Reusing the same local OAuth record for a different ChatGPT account must invalidate
            // reuse; applying the new account requires the normal clean restart path.
            File.WriteAllText(
                authAPath,
                BuildTestOAuthAuth("a-rebound", "official-account-C"));
            PersistSuccessfulOfficialOAuthLogin(accountA);
            if (File.ReadAllText(sharedAuthPath) != liveSharedBeforeRelogin ||
                !File.ReadAllText(storedAPath).Contains("a-rebound", StringComparison.Ordinal) ||
                CanReuseOfficialOAuthSharedProfile(accountA))
            {
                throw new InvalidOperationException(
                    "A different ChatGPT identity hot-replaced or reused the running OAuth session.");
            }

            File.WriteAllText(
                authAPath,
                BuildTestOAuthAuth("a-relogin", "official-account-A"));
            PersistSuccessfulOfficialOAuthLogin(accountA);

            // A failed login must restore the prior account auth.json from its retained backup.
            var restoreBackupPath = authAPath + ".restore-test";
            File.Copy(authAPath, restoreBackupPath, overwrite: false);
            File.WriteAllText(authAPath, "{}");
            RestoreOfficialOAuthLoginCredential(authAPath, restoreBackupPath);
            if (!File.ReadAllText(authAPath).Contains("a-relogin", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A failed OAuth login did not restore the previous account credential.");
            }
            File.Delete(restoreBackupPath);

            var projectedConfig = File.ReadAllText(sharedConfigPath);
            const string staleOfficialPreferences = """
                localeOverride = "en-US"

                [features]
                responses_websockets = true
                responses_websockets = true
                responses_websockets_v2 = true

                [desktop]
                localeOverride = "en-US"
                localeOverride = "en-GB"
                """;
            var repairedPreferences = ProjectOfficialOAuthConfigText(staleOfficialPreferences);
            var emptyProjectedConfig = ProjectOfficialOAuthConfigText("");
            var routedProjectedConfig = ProjectOfficialOAuthConfigText(
                projectedConfig,
                routeThroughGateway: true);
            var officialProviderHeader =
                "[model_providers." + AccountStore.OfficialOAuthProviderId + "]";
            var forbidden = new[]
            {
                LocalPatGateway.ChatGptBaseUrl,
                AccountStore.AccessTokenBaseUrl,
                "experimental_bearer_token",
                "responses_websockets"
            };
            if (!projectedConfig.Contains("cli_auth_credentials_store = \"file\"", StringComparison.Ordinal) ||
                !projectedConfig.Contains("forced_login_method = \"chatgpt\"", StringComparison.Ordinal) ||
                !projectedConfig.Contains(
                    "model_provider = " + TomlString(AccountStore.OfficialOAuthProviderId),
                    StringComparison.Ordinal) ||
                !projectedConfig.Contains(
                    officialProviderHeader,
                    StringComparison.Ordinal) ||
                projectedConfig.IndexOf(officialProviderHeader, StringComparison.Ordinal) !=
                    projectedConfig.LastIndexOf(officialProviderHeader, StringComparison.Ordinal) ||
                !projectedConfig.Contains(
                    "base_url = " + TomlString(AccountStore.OfficialOAuthBaseUrl),
                    StringComparison.Ordinal) ||
                !projectedConfig.Contains("wire_api = \"responses\"", StringComparison.Ordinal) ||
                !projectedConfig.Contains("requires_openai_auth = true", StringComparison.Ordinal) ||
                !projectedConfig.Contains("supports_websockets = false", StringComparison.Ordinal) ||
                !TomlSectionStringValueMatches(
                    projectedConfig,
                    "desktop",
                    "localeOverride",
                    AccountStore.OfficialOAuthDesktopLocale) ||
                !repairedPreferences.Contains(
                    "model_provider = " + TomlString(AccountStore.OfficialOAuthProviderId),
                    StringComparison.Ordinal) ||
                !repairedPreferences.Contains(
                    officialProviderHeader,
                    StringComparison.Ordinal) ||
                repairedPreferences.IndexOf(officialProviderHeader, StringComparison.Ordinal) !=
                    repairedPreferences.LastIndexOf(officialProviderHeader, StringComparison.Ordinal) ||
                !repairedPreferences.Contains("wire_api = \"responses\"", StringComparison.Ordinal) ||
                !repairedPreferences.Contains("supports_websockets = false", StringComparison.Ordinal) ||
                !TomlSectionStringValueMatches(
                    repairedPreferences,
                    "desktop",
                    "localeOverride",
                    AccountStore.OfficialOAuthDesktopLocale) ||
                forbidden.Any(value => projectedConfig.Contains(value, StringComparison.OrdinalIgnoreCase)) ||
                forbidden.Any(value => repairedPreferences.Contains(value, StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(
                    projectedConfig,
                    ProjectOfficialOAuthConfigText(projectedConfig),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    emptyProjectedConfig,
                    ProjectOfficialOAuthConfigText(emptyProjectedConfig),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Official ChatGPT projection retained PAT/API routing or was not idempotent."
                );
            }
            if (!routedProjectedConfig.Contains(
                    "base_url = " + TomlString(LocalPatGateway.ProviderBaseUrl),
                    StringComparison.Ordinal) ||
                routedProjectedConfig.Contains(
                    "base_url = " + TomlString(AccountStore.OfficialOAuthBaseUrl),
                    StringComparison.Ordinal) ||
                !routedProjectedConfig.Contains("requires_openai_auth = true", StringComparison.Ordinal) ||
                !string.Equals(
                    routedProjectedConfig,
                    ProjectOfficialOAuthConfigText(
                        routedProjectedConfig,
                        routeThroughGateway: true),
                    StringComparison.Ordinal) ||
                !ProjectOfficialOAuthConfigText(routedProjectedConfig).Contains(
                    "base_url = " + TomlString(AccountStore.OfficialOAuthBaseUrl),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Official ChatGPT rotation routing did not stay conditional, idempotent, and reversible."
                );
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SharedCodexHomeOverrideVariable, oldSharedHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string BuildTestOAuthAuth(string marker, string? accountId = null)
    {
        return JsonSerializer.Serialize(
            new
            {
                auth_mode = ChatGptAuthMode,
                tokens = new
                {
                    id_token = "id-" + marker,
                    access_token = "access-" + marker,
                    refresh_token = "refresh-" + marker,
                    account_id = accountId
                }
            },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AssertProjectedConfig(
        string config,
        string provider,
        string model,
        string reasoningEffort,
        bool remoteCompactionEnabled,
        bool? pluginsEnabled = null,
        string? expectedServiceTier = null)
    {
        var normalized = config.Replace("\r\n", "\n").Replace('\r', '\n');
        var topLevelLines = normalized.Split('\n')
            .TakeWhile(line => !line.TrimStart().StartsWith("[", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();
        var expected = new Dictionary<string, string>
        {
            ["model_provider"] = provider,
            ["model"] = model,
            ["review_model"] = model,
            ["model_reasoning_effort"] = reasoningEffort,
            ["service_tier"] = NormalizeDesktopServiceTier(
                expectedServiceTier ?? DesktopServiceTier)
        };

        foreach (var pair in expected)
        {
            var expectedLine = pair.Key + " = " + TomlString(pair.Value);
            if (topLevelLines.Count(line => TomlKeyEquals(line, pair.Key)) != 1 ||
                !topLevelLines.Contains(expectedLine, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Config projection produced an invalid {pair.Key} value.");
            }
        }

        var expectedAutoCompactLine =
            "model_auto_compact_token_limit = " + DesktopAutoCompactTokenLimit;
        if (topLevelLines.Count(line => TomlKeyEquals(line, "model_auto_compact_token_limit")) != 1 ||
            !topLevelLines.Contains(expectedAutoCompactLine, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Config projection did not disable automatic compaction.");
        }

        if (normalized.Split(SitesPluginHeader, StringSplitOptions.None).Length != 2 ||
            !normalized.Contains(SitesPluginHeader + "\nenabled = false", StringComparison.Ordinal) ||
            !normalized.Contains("[features]\njs_repl = false", StringComparison.Ordinal) ||
            !FeatureFlagMatches(normalized, "remote_compaction_v2", remoteCompactionEnabled) ||
            !FeatureFlagMatches(normalized, "remote_plugin", false) ||
            (pluginsEnabled.HasValue &&
             !FeatureFlagMatches(normalized, "plugins", pluginsEnabled.Value)))
        {
            throw new InvalidOperationException("Config projection did not preserve account-specific features safely.");
        }
    }

    private static bool FeatureFlagMatches(string config, string key, bool expectedValue)
    {
        var inFeatures = false;
        var matches = 0;
        var expectedLine = key + " = " + expectedValue.ToString().ToLowerInvariant();
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                inFeatures = trimmed.Equals("[features]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inFeatures && TomlKeyEquals(trimmed, key))
            {
                matches++;
                if (!trimmed.Equals(expectedLine, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return matches == 1;
    }

    internal static string ProjectOfficialOAuthConfigText(
        string currentConfig,
        string? serviceTier = null,
        bool routeThroughGateway = false)
    {
        var desktopServiceTier = serviceTier == null
            ? ReadDesktopServiceTier(currentConfig)
            : NormalizeDesktopServiceTier(serviceTier);
        var normalized = currentConfig.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var output = new List<string>
        {
            "model_provider = " + TomlString(AccountStore.OfficialOAuthProviderId),
            "cli_auth_credentials_store = \"file\"",
            "forced_login_method = \"chatgpt\"",
            "service_tier = " + TomlString(desktopServiceTier)
        };
        string? currentSection = null;
        var skipSection = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                currentSection = trimmed;
                skipSection =
                    trimmed.StartsWith("[model_providers.", StringComparison.OrdinalIgnoreCase) ||
                    IsSitesPluginSection(trimmed);
            }

            if (skipSection)
            {
                continue;
            }

            if (currentSection == null &&
                (IsTopLevelModelProviderLine(trimmed) ||
                 IsTopLevelModelLine(trimmed) ||
                 IsTopLevelReviewModelLine(trimmed) ||
                 IsTopLevelModelReasoningEffortLine(trimmed) ||
                 IsTopLevelChatGptBaseUrlLine(trimmed) ||
                 IsTopLevelServiceTierLine(trimmed) ||
                 IsTopLevelModelAutoCompactTokenLimitLine(trimmed) ||
                 IsTopLevelModelAutoCompactTokenLimitScopeLine(trimmed) ||
                 IsCliAuthCredentialsStoreLine(trimmed) ||
                 IsForcedLoginMethodLine(trimmed) ||
                 TomlKeyEquals(trimmed, "localeOverride") ||
                 IsExperimentalBearerTokenLine(trimmed)))
            {
                continue;
            }

            if (currentSection?.Equals("[features]", StringComparison.OrdinalIgnoreCase) == true &&
                new[]
                {
                    "js_repl",
                    "remote_compaction_v2",
                    "remote_plugin",
                    "plugins",
                    "responses_websockets",
                    "responses_websockets_v2"
                }
                    .Any(key => TomlKeyEquals(trimmed, key)))
            {
                continue;
            }

            if (currentSection?.Equals("[desktop]", StringComparison.OrdinalIgnoreCase) == true &&
                TomlKeyEquals(trimmed, "localeOverride"))
            {
                continue;
            }

            output.Add(line);
        }

        while (output.Count > 4 && string.IsNullOrWhiteSpace(output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }

        var baseProjection = UpsertTomlSectionStringValue(
            string.Join(Environment.NewLine, output) + Environment.NewLine,
            "desktop",
            "localeOverride",
            AccountStore.OfficialOAuthDesktopLocale);
        output = baseProjection
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }

        output.Add("");
        output.Add("[model_providers." + AccountStore.OfficialOAuthProviderId + "]");
        output.Add("name = " + TomlString(AccountStore.OfficialOAuthProviderName));
        output.Add("base_url = " + TomlString(
            routeThroughGateway
                ? LocalPatGateway.ProviderBaseUrl
                : AccountStore.OfficialOAuthBaseUrl));
        output.Add("wire_api = \"responses\"");
        output.Add("requires_openai_auth = true");
        output.Add("supports_websockets = false");
        return string.Join(Environment.NewLine, output) + Environment.NewLine;
    }

    private static string PreserveSharedMcpServerSections(
        string currentSharedConfig,
        string projectedConfig)
    {
        var sharedLines = currentSharedConfig
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var preserved = new List<string>();
        var inMcpServerSection = false;
        foreach (var line in sharedLines)
        {
            var trimmed = line.Trim();
            if (IsTomlTableHeader(trimmed))
            {
                inMcpServerSection = IsMcpServerSection(trimmed);
            }

            if (inMcpServerSection)
            {
                preserved.Add(line);
            }
        }

        while (preserved.Count > 0 && string.IsNullOrWhiteSpace(preserved[^1]))
        {
            preserved.RemoveAt(preserved.Count - 1);
        }
        if (preserved.Count == 0)
        {
            return projectedConfig;
        }

        var projectedLines = projectedConfig
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var output = new List<string>();
        inMcpServerSection = false;
        foreach (var line in projectedLines)
        {
            var trimmed = line.Trim();
            if (IsTomlTableHeader(trimmed))
            {
                inMcpServerSection = IsMcpServerSection(trimmed);
            }

            if (!inMcpServerSection)
            {
                output.Add(line);
            }
        }

        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }
        output.Add("");
        output.AddRange(preserved);
        return string.Join(Environment.NewLine, output).TrimEnd() + Environment.NewLine;
    }

    private static bool IsTomlTableHeader(string trimmedLine)
    {
        return trimmedLine.Length >= 3 &&
               trimmedLine.StartsWith("[", StringComparison.Ordinal) &&
               trimmedLine.EndsWith("]", StringComparison.Ordinal);
    }

    private static bool IsMcpServerSection(string trimmedHeader)
    {
        var tableName = trimmedHeader.TrimStart('[').TrimEnd(']').Trim();
        return tableName.Equals("mcp_servers", StringComparison.OrdinalIgnoreCase) ||
               tableName.StartsWith("mcp_servers.", StringComparison.OrdinalIgnoreCase);
    }

    private static string ProjectWindowsClientConfigText(
        string currentConfig,
        bool requiresOpenAiAuth = false,
        string? desktopProviderName = null,
        string? providerBearerToken = null,
        bool forceFileAuthStore = false,
        string? serviceTier = null)
    {
        var desktopServiceTier = serviceTier == null
            ? ReadDesktopServiceTier(currentConfig)
            : NormalizeDesktopServiceTier(serviceTier);
        // Materialize the features section before rebuilding the provider section. Without
        // this stable anchor, a config that started without [features] would alternate the
        // section order on every migration pass.
        var normalized = ApplyDesktopFeatureDefaults(currentConfig, disablePlugins: true)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var output = new List<string>
        {
            "model_provider = " + TomlString(AccountStore.AccessTokenProviderId),
            "model = " + TomlString(AccessTokenModel),
            "review_model = " + TomlString(AccessTokenModel),
            "model_reasoning_effort = " + TomlString(AccessTokenReasoningEffort),
            "chatgpt_base_url = " + TomlString(LocalPatGateway.ChatGptBaseUrl),
            "model_auto_compact_token_limit = " + DesktopAutoCompactTokenLimit
        };
        if (forceFileAuthStore)
        {
            output.Add("cli_auth_credentials_store = \"file\"");
        }
        // Keep the tier as the final managed top-level preference. UpsertDesktopServiceTier
        // uses the same canonical position when carrying the shared Codex choice across accounts.
        output.Add("service_tier = " + TomlString(desktopServiceTier));
        string? currentSection = null;
        var skipSection = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                currentSection = trimmed;
                skipSection =
                    trimmed.Equals("[model_providers.codex_local_access]", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(
                        "[model_providers." + AccountStore.AccessTokenProviderId + "]",
                        StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(
                        "[model_providers." + TomlString(AccountStore.AccessTokenProviderId) + "]",
                        StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(
                        "[model_providers." + AccountStore.LegacyAccessTokenProviderId + "]",
                        StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(
                        "[model_providers." + AccountStore.LegacyCompatibleApiProviderId + "]",
                        StringComparison.OrdinalIgnoreCase) ||
                    IsManagedLegacyCompatibleApiProviderSection(lines, index, trimmed, "OpenAI") ||
                    trimmed.Equals(
                        "[model_providers." + AccountStore.CompatibleApiProviderId + "]",
                        StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(
                        "[model_providers." + TomlString(AccountStore.CompatibleApiProviderId) + "]",
                        StringComparison.OrdinalIgnoreCase) ||
                    IsSitesPluginSection(trimmed);
            }

            if (skipSection)
            {
                continue;
            }

            if (currentSection == null &&
                (IsTopLevelModelProviderLine(trimmed) ||
                 IsTopLevelModelLine(trimmed) ||
                 IsTopLevelReviewModelLine(trimmed) ||
                 IsTopLevelModelReasoningEffortLine(trimmed) ||
                 IsTopLevelChatGptBaseUrlLine(trimmed) ||
                 IsTopLevelServiceTierLine(trimmed) ||
                 IsTopLevelModelAutoCompactTokenLimitLine(trimmed) ||
                 IsTopLevelModelAutoCompactTokenLimitScopeLine(trimmed) ||
                 (forceFileAuthStore && IsCliAuthCredentialsStoreLine(trimmed)) ||
                 IsExperimentalBearerTokenLine(trimmed)))
            {
                continue;
            }

            output.Add(line);
        }

        while (output.Count > 1 && string.IsNullOrWhiteSpace(output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }

        output.Add("");
        output.Add("[model_providers." + AccountStore.AccessTokenProviderId + "]");
        output.Add("name = " + TomlString(
            string.IsNullOrWhiteSpace(desktopProviderName)
                ? AccountStore.AccessTokenProviderName
                : desktopProviderName.Trim()));
        output.Add("base_url = " + TomlString(AccountStore.AccessTokenBaseUrl));
        output.Add("wire_api = \"responses\"");
        if (!string.IsNullOrWhiteSpace(providerBearerToken))
        {
            output.Add("experimental_bearer_token = " + TomlString(providerBearerToken.Trim()));
        }
        output.Add("requires_openai_auth = " + requiresOpenAiAuth.ToString().ToLowerInvariant());
        output.Add("supports_websockets = false");
        output.Add("stream_max_retries = 0");
        output.Add("request_max_retries = 1");
        AppendDisabledSitesPlugin(output);
        return ApplyDesktopFeatureDefaults(
            string.Join(Environment.NewLine, output) + Environment.NewLine,
            disablePlugins: true);
    }

    private static bool IsTopLevelModelProviderLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "model_provider");
    }

    private static bool IsCliAuthCredentialsStoreLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "cli_auth_credentials_store");
    }

    private static bool IsForcedLoginMethodLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "forced_login_method");
    }

    private static string ProjectCompatibleApiConfigText(
        string currentConfig,
        AccountRecord account,
        bool requiresOpenAiAuth = false,
        string? providerBearerToken = null,
        bool forceFileAuthStore = false,
        string? serviceTier = null)
    {
        var desktopServiceTier = serviceTier == null
            ? ReadDesktopServiceTier(currentConfig)
            : NormalizeDesktopServiceTier(serviceTier);
        var normalized = currentConfig.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var providerName = string.IsNullOrWhiteSpace(account.ApiProviderName) ? "OpenAI" : account.ApiProviderName.Trim();
        var providerHeader = "[model_providers." + AccountStore.CompatibleApiProviderId + "]";
        var providerHeaderQuoted = "[model_providers." + TomlString(AccountStore.CompatibleApiProviderId) + "]";
        var model = string.IsNullOrWhiteSpace(account.ApiModel)
            ? CompatibleApiDefaultModel
            : account.ApiModel.Trim();
        var output = new List<string>
        {
            "model_provider = " + TomlString(AccountStore.CompatibleApiProviderId),
            "model = " + TomlString(model),
            "review_model = " + TomlString(model),
            "model_reasoning_effort = " + TomlString(CompatibleApiReasoningEffort),
            "model_auto_compact_token_limit = " + DesktopAutoCompactTokenLimit
        };
        if (forceFileAuthStore)
        {
            output.Add("cli_auth_credentials_store = \"file\"");
        }
        // Match UpsertDesktopServiceTier's canonical ordering so repeated shared-profile
        // projections are byte-for-byte idempotent after a Standard/Fast selection is carried.
        output.Add("service_tier = " + TomlString(desktopServiceTier));

        string? currentSection = null;
        var skipSection = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                currentSection = trimmed;
                skipSection =
                    trimmed.Equals("[model_providers.codex_local_access]", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(providerHeader, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(providerHeaderQuoted, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(
                        "[model_providers." + AccountStore.LegacyAccessTokenProviderId + "]",
                        StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(
                        "[model_providers." + AccountStore.LegacyCompatibleApiProviderId + "]",
                        StringComparison.OrdinalIgnoreCase) ||
                    IsManagedLegacyCompatibleApiProviderSection(lines, index, trimmed, providerName) ||
                    IsManagedLegacyCompatibleApiProviderSection(lines, index, trimmed, "OpenAI") ||
                    IsSitesPluginSection(trimmed);
            }

            if (skipSection)
            {
                while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
                {
                    output.RemoveAt(output.Count - 1);
                }
                continue;
            }

            if (currentSection == null &&
                (IsTopLevelModelProviderLine(trimmed) ||
                 IsTopLevelModelLine(trimmed) ||
                 IsTopLevelReviewModelLine(trimmed) ||
                 IsTopLevelModelReasoningEffortLine(trimmed) ||
                 IsTopLevelChatGptBaseUrlLine(trimmed) ||
                 IsTopLevelServiceTierLine(trimmed) ||
                 IsTopLevelModelAutoCompactTokenLimitLine(trimmed) ||
                 IsTopLevelModelAutoCompactTokenLimitScopeLine(trimmed) ||
                 (forceFileAuthStore && IsCliAuthCredentialsStoreLine(trimmed)) ||
                 IsExperimentalBearerTokenLine(trimmed)))
            {
                continue;
            }

            output.Add(line);
        }

        while (output.Count > 1 && string.IsNullOrWhiteSpace(output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }

        output.Add(providerHeader);
        output.Add("name = " + TomlString(providerName));
        output.Add("base_url = " + TomlString(account.ApiBaseUrl.TrimEnd('/')));
        output.Add("wire_api = " + TomlString(account.ApiWireApi));
        if (!string.IsNullOrWhiteSpace(providerBearerToken))
        {
            output.Add("experimental_bearer_token = " + TomlString(providerBearerToken.Trim()));
        }
        output.Add("requires_openai_auth = " + requiresOpenAiAuth.ToString().ToLowerInvariant());
        output.Add("supports_websockets = false");
        output.Add("stream_max_retries = 0");
        output.Add("request_max_retries = 1");
        AppendDisabledSitesPlugin(output);
        return ApplyDesktopFeatureDefaults(
            string.Join(Environment.NewLine, output) + Environment.NewLine,
            disablePlugins: forceFileAuthStore);
    }

    private static bool IsManagedLegacyCompatibleApiProviderSection(
        IReadOnlyList<string> lines,
        int headerIndex,
        string trimmedHeader,
        string legacyProviderName)
    {
        var bareHeader = "[model_providers." + TomlBareKey(legacyProviderName) + "]";
        var quotedHeader = "[model_providers." + TomlString(legacyProviderName) + "]";
        if (!trimmedHeader.Equals(bareHeader, StringComparison.OrdinalIgnoreCase) &&
            !trimmedHeader.Equals(quotedHeader, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasName = false;
        var hasBaseUrl = false;
        var hasWireApi = false;
        var doesNotRequireOpenAiAuth = false;
        var disablesWebSockets = false;
        for (var index = headerIndex + 1; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                break;
            }

            hasName |= TomlKeyEquals(trimmed, "name");
            hasBaseUrl |= TomlKeyEquals(trimmed, "base_url");
            hasWireApi |= TomlKeyEquals(trimmed, "wire_api");
            doesNotRequireOpenAiAuth |= TomlValueEquals(trimmed, "requires_openai_auth", "false");
            disablesWebSockets |= TomlValueEquals(trimmed, "supports_websockets", "false");
        }

        return hasName &&
               hasBaseUrl &&
               hasWireApi &&
               doesNotRequireOpenAiAuth &&
               disablesWebSockets;
    }

    private static bool TomlValueEquals(string trimmedLine, string key, string expectedValue)
    {
        var equalsIndex = trimmedLine.IndexOf('=');
        return equalsIndex > 0 &&
               trimmedLine[..equalsIndex].Trim().Equals(key, StringComparison.OrdinalIgnoreCase) &&
               trimmedLine[(equalsIndex + 1)..].Trim().Equals(expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTopLevelModelLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "model");
    }

    private static bool IsTopLevelReviewModelLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "review_model");
    }

    private static bool IsTopLevelModelReasoningEffortLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "model_reasoning_effort");
    }

    private static bool IsTopLevelChatGptBaseUrlLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "chatgpt_base_url");
    }

    private static bool IsTopLevelServiceTierLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "service_tier");
    }

    private static bool IsTopLevelModelAutoCompactTokenLimitLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "model_auto_compact_token_limit");
    }

    private static bool IsTopLevelModelAutoCompactTokenLimitScopeLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "model_auto_compact_token_limit_scope");
    }

    private static bool IsSitesPluginSection(string trimmedLine)
    {
        return trimmedLine.Equals(SitesPluginHeader, StringComparison.OrdinalIgnoreCase) ||
               trimmedLine.Equals("[plugins.sites@openai-bundled]", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendDisabledSitesPlugin(List<string> output)
    {
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }

        output.Add("");
        output.Add(SitesPluginHeader);
        output.Add("enabled = false");
    }

    private static string UpsertFeatureFlag(string config, string key, bool enabled)
    {
        var normalized = config.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var featuresHeaderIndex = lines.FindIndex(line =>
            line.Trim().Equals("[features]", StringComparison.OrdinalIgnoreCase));
        var valueLine = key + " = " + enabled.ToString().ToLowerInvariant();

        if (featuresHeaderIndex < 0)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            lines.Add("");
            lines.Add("[features]");
            lines.Add(valueLine);
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        var sectionEnd = lines.FindIndex(
            featuresHeaderIndex + 1,
            line => line.TrimStart().StartsWith("[", StringComparison.Ordinal));
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        for (var index = featuresHeaderIndex + 1; index < sectionEnd; index++)
        {
            if (TomlKeyEquals(lines[index].Trim(), key))
            {
                lines[index] = valueLine;
                return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
            }
        }

        lines.Insert(sectionEnd, valueLine);
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    private static string UpsertTomlSectionStringValue(
        string config,
        string section,
        string key,
        string value)
    {
        var normalized = config.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var sectionHeader = "[" + section + "]";
        var sectionHeaderIndex = lines.FindIndex(line =>
            line.Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase));
        var valueLine = key + " = " + TomlString(value);

        if (sectionHeaderIndex < 0)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            lines.Add("");
            lines.Add(sectionHeader);
            lines.Add(valueLine);
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        var sectionEnd = lines.FindIndex(
            sectionHeaderIndex + 1,
            line => line.TrimStart().StartsWith("[", StringComparison.Ordinal));
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        for (var index = sectionHeaderIndex + 1; index < sectionEnd; index++)
        {
            if (TomlKeyEquals(lines[index].Trim(), key))
            {
                lines[index] = valueLine;
                return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
            }
        }

        var insertIndex = sectionEnd;
        while (insertIndex > sectionHeaderIndex + 1 &&
               string.IsNullOrWhiteSpace(lines[insertIndex - 1]))
        {
            insertIndex--;
        }

        lines.Insert(insertIndex, valueLine);
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    private static bool TomlSectionStringValueMatches(
        string config,
        string section,
        string key,
        string expectedValue)
    {
        var expectedHeader = "[" + section + "]";
        var expectedLine = key + " = " + TomlString(expectedValue);
        var inSection = false;
        var matches = 0;
        foreach (var line in config.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = trimmed.Equals(expectedHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!TomlKeyEquals(trimmed, key))
            {
                continue;
            }

            matches++;
            if (!inSection || !trimmed.Equals(expectedLine, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return matches == 1;
    }

    private static string ApplyDesktopFeatureDefaults(string config, bool disablePlugins = false)
    {
        var projected = UpsertFeatureFlag(config, "remote_compaction_v2", false);
        projected = UpsertFeatureFlag(projected, "remote_plugin", false);
        return disablePlugins
            ? UpsertFeatureFlag(projected, "plugins", false)
            : projected;
    }

    private static bool IsExperimentalBearerTokenLine(string trimmedLine)
    {
        return TomlKeyEquals(trimmedLine, "experimental_bearer_token");
    }

    private static bool TomlKeyEquals(string trimmedLine, string key)
    {
        var equalsIndex = trimmedLine.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return false;
        }

        return trimmedLine[..equalsIndex].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    private static string TomlBareKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "OpenAI";
        }

        return value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? value
            : TomlString(value);
    }

    private static string TomlString(string value)
    {
        return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static IReadOnlyList<WindowsClientProcessSnapshot> CaptureWindowsClientProcessSnapshots()
    {
        var clientPath = ResolveCodexWindowsClientPath();
        var packageRoot = string.IsNullOrWhiteSpace(clientPath)
            ? null
            : Path.GetDirectoryName(clientPath);
        var parentProcessIds = CaptureProcessParentIds();
        var protectedProcessIds = SelectProtectedWindowsClientShutdownProcessIds(
            parentProcessIds,
            Environment.ProcessId);

        var snapshots = new List<WindowsClientProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var processName = process.ProcessName;
                    if (protectedProcessIds.Contains(process.Id) ||
                        IsProtectedWindowsClientShutdownProcessName(processName) ||
                        (!IsCodexWindowsClientProcess(process, packageRoot) &&
                         !IsCodexPlusPlusLauncherProcess(process)) ||
                        process.HasExited ||
                        !TryGetProcessExecutablePath(process, out var executablePath))
                    {
                        continue;
                    }

                    // A PID without an immutable start-time identity is unsafe to terminate
                    // later because Windows may recycle it for an unrelated/new Codex process.
                    snapshots.Add(new WindowsClientProcessSnapshot(
                        process.Id,
                        process.StartTime.ToUniversalTime().Ticks,
                        processName,
                        executablePath,
                        GetShutdownPriority(process, packageRoot),
                        parentProcessIds.TryGetValue(process.Id, out var parentProcessId)
                            ? parentProcessId
                            : 0));
                }
                catch
                {
                    // Inaccessible/vanished processes are deliberately omitted instead of
                    // weakening snapshot identity to a broad process-name match.
                }
            }
        }

        return snapshots
            .OrderBy(snapshot => snapshot.ShutdownPriority)
            .ThenBy(snapshot => snapshot.StartTimeUtcTicks)
            .ToArray();
    }

    private static IReadOnlyList<WindowsClientProcessSnapshot> CaptureWindowsClientProcessTreeSnapshots(
        WindowsClientActivationIdentity rootIdentity)
    {
        return SelectWindowsClientProcessTreeSnapshots(
            CaptureWindowsClientProcessSnapshots(),
            rootIdentity);
    }

    private static IReadOnlyList<WindowsClientProcessSnapshot> SelectWindowsClientProcessTreeSnapshots(
        IReadOnlyList<WindowsClientProcessSnapshot> snapshots,
        WindowsClientActivationIdentity rootIdentity)
    {
        var root = snapshots.FirstOrDefault(snapshot =>
            snapshot.ProcessId == rootIdentity.ProcessId &&
            (!rootIdentity.StartTimeUtcTicks.HasValue ||
             snapshot.StartTimeUtcTicks == rootIdentity.StartTimeUtcTicks.Value));
        if (root == null)
        {
            return Array.Empty<WindowsClientProcessSnapshot>();
        }

        var ownedProcessIds = new HashSet<int> { root.ProcessId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var snapshot in snapshots)
            {
                if (!ownedProcessIds.Contains(snapshot.ProcessId) &&
                    ownedProcessIds.Contains(snapshot.ParentProcessId))
                {
                    ownedProcessIds.Add(snapshot.ProcessId);
                    changed = true;
                }
            }
        }

        return snapshots
            .Where(snapshot => ownedProcessIds.Contains(snapshot.ProcessId))
            .ToArray();
    }

    private static IReadOnlyList<int> CaptureOfficialNativeFastPortsOwnedBy(
        IReadOnlyList<WindowsClientProcessSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return Array.Empty<int>();
        }

        var identities = snapshots.ToDictionary(
            snapshot => snapshot.ProcessId,
            snapshot => snapshot.StartTimeUtcTicks);
        var ports = new List<int>();
        foreach (var port in OfficialNativeFastCdpPortCandidates())
        {
            if (TryGetOfficialCodexLoopbackListenerIdentity(port, out var identity) &&
                identities.TryGetValue(identity.ProcessId, out var expectedStartTicks) &&
                identity.StartTimeUtcTicks == expectedStartTicks)
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    private static IReadOnlyDictionary<int, int> CaptureProcessParentIds()
    {
        var parentProcessIds = new Dictionary<int, int>();
        var snapshotHandle = CreateToolhelp32Snapshot(ToolhelpSnapshotProcesses, 0);
        if (snapshotHandle == InvalidNativeHandle)
        {
            return parentProcessIds;
        }

        try
        {
            var entry = new NativeProcessEntry
            {
                Size = checked((uint)Marshal.SizeOf<NativeProcessEntry>())
            };
            if (!Process32First(snapshotHandle, ref entry))
            {
                return parentProcessIds;
            }

            do
            {
                if (entry.ProcessId is > 0 and <= int.MaxValue &&
                    entry.ParentProcessId <= int.MaxValue)
                {
                    parentProcessIds[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                }
                entry.Size = checked((uint)Marshal.SizeOf<NativeProcessEntry>());
            } while (Process32Next(snapshotHandle, ref entry));
        }
        finally
        {
            _ = CloseHandle(snapshotHandle);
        }

        return parentProcessIds;
    }

    private static IReadOnlySet<int> SelectProtectedWindowsClientShutdownProcessIds(
        IReadOnlyDictionary<int, int> parentProcessIds,
        int managerProcessId)
    {
        var protectedIds = new HashSet<int>();
        if (managerProcessId <= 0)
        {
            return protectedIds;
        }

        // The Manager may itself have been launched from a Codex task. Protect its ancestor
        // chain so closing that outer client cannot indirectly tear down the Manager. Direct
        // Manager/gateway descendants are independently protected by executable name. A
        // ChatGPT instance intentionally launched by this Manager remains a valid exact-PID
        // shutdown target; killing that single child never uses process-tree semantics.
        protectedIds.Add(managerProcessId);
        var cursor = managerProcessId;
        while (parentProcessIds.TryGetValue(cursor, out var parentId) &&
               parentId > 0 &&
               protectedIds.Add(parentId))
        {
            cursor = parentId;
        }

        return protectedIds;
    }

    private static void StopWindowsClientProcesses(
        IReadOnlyList<WindowsClientProcessSnapshot> shutdownTargets)
    {
        if (shutdownTargets.Count == 0)
        {
            return;
        }

        var clientPath = ResolveCodexWindowsClientPath();
        var packageRoot = string.IsNullOrWhiteSpace(clientPath)
            ? null
            : Path.GetDirectoryName(clientPath);
        var processes = new List<Process>();
        var rejectedTargetCount = 0;
        var protectedProcessIds = SelectProtectedWindowsClientShutdownProcessIds(
            CaptureProcessParentIds(),
            Environment.ProcessId);
        foreach (var target in shutdownTargets)
        {
            if (protectedProcessIds.Contains(target.ProcessId))
            {
                rejectedTargetCount++;
                continue;
            }

            if (TryOpenWindowsClientSnapshot(target, packageRoot, out var process))
            {
                processes.Add(process!);
                WriteCodexPlusPlusLaunchDiagnostic(
                    "windows-client-exact-shutdown-target-verified",
                    $"pid={target.ProcessId}; name={target.ProcessName}; " +
                    $"start_ticks={target.StartTimeUtcTicks}; priority={target.ShutdownPriority}");
            }
            else
            {
                rejectedTargetCount++;
            }
        }

        WriteCodexPlusPlusLaunchDiagnostic(
            "windows-client-exact-shutdown-plan",
            $"requested_count={shutdownTargets.Count}; verified_count={processes.Count}; " +
            $"rejected_count={rejectedTargetCount}; manager_pid_excluded={processes.All(process => process.Id != Environment.ProcessId)}; " +
            "force_kill_scope=single-process");

        try
        {
            var gracefulTargets = new List<Process>();
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    if (process.MainWindowHandle != IntPtr.Zero &&
                        process.CloseMainWindow())
                    {
                        gracefulTargets.Add(process);
                    }
                    // Renderer/helper processes are deliberately left alive until the main
                    // window has had time to flush its profile and close its app-server. A
                    // child-tree kill here can terminate the parent first and leave the next
                    // activation on a blank/white shell.
                }
                catch
                {
                    // Process may have exited or become inaccessible during restart.
                }
            }

            WaitForProcessesToExit(gracefulTargets, WindowsClientGracefulShutdownTimeout);

            // Use one shared force-stop budget instead of adding a timeout for every
            // renderer/helper process. This bounds shutdown even when Codex has many children.
            for (var index = processes.Count - 1; index >= 0; index--)
            {
                var process = processes[index];
                try
                {
                    if (!process.HasExited)
                    {
                        // Never terminate a ChatGPT process tree here. Account Manager or a
                        // terminal can legitimately be an indirect descendant when Codex
                        // launched the task that started this switch. Every helper/root that
                        // belongs to the captured Codex instance is already represented by its
                        // own PID + start time + name + executable-path snapshot and is stopped
                        // individually.
                        process.Kill();
                    }
                }
                catch
                {
                    // A graceful close or an individually stopped parent may already have
                    // removed this exact process.
                }
            }
            WaitForProcessesToExit(processes, WindowsClientForceShutdownTimeout);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        // Give the MSIX package a short hand-off window before activating the replacement.
        Thread.Sleep(300);
    }

    private static bool TryOpenWindowsClientSnapshot(
        WindowsClientProcessSnapshot snapshot,
        string? packageRoot,
        out Process? process)
    {
        process = null;
        try
        {
            var candidate = Process.GetProcessById(snapshot.ProcessId);
            var candidateProcessName = candidate.ProcessName;
            if (candidate.Id == Environment.ProcessId ||
                candidate.HasExited ||
                IsProtectedWindowsClientShutdownProcessName(candidateProcessName) ||
                !candidateProcessName.Equals(snapshot.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartTime.ToUniversalTime().Ticks != snapshot.StartTimeUtcTicks ||
                !TryGetProcessExecutablePath(candidate, out var executablePath) ||
                !PathsEqual(executablePath, snapshot.ExecutablePath) ||
                (!IsCodexWindowsClientProcess(candidate, packageRoot) &&
                 !IsCodexPlusPlusLauncherProcess(candidate)))
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return true;
        }
        catch
        {
            process?.Dispose();
            process = null;
            return false;
        }
    }

    private static bool TryGetProcessExecutablePath(Process process, out string executablePath)
    {
        executablePath = "";
        try
        {
            var candidate = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            executablePath = Path.GetFullPath(candidate);
            return true;
        }
        catch
        {
            executablePath = "";
            return false;
        }
    }

    private static void WaitForProcessesToExit(
        IReadOnlyCollection<Process> processes,
        TimeSpan timeout)
    {
        if (processes.Count == 0)
        {
            return;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var allExited = true;
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        allExited = false;
                        break;
                    }
                }
                catch
                {
                    // Inaccessible/disposed processes are no longer actionable here.
                }
            }

            if (allExited)
            {
                return;
            }

            Thread.Sleep(50);
        }
    }

    private static int GetShutdownPriority(Process process, string? packageRoot)
    {
        if (IsCodexPlusPlusLauncherProcess(process))
        {
            return 3;
        }

        try
        {
            if (IsCodexWindowsClientProcess(process, packageRoot) &&
                process.MainWindowHandle != IntPtr.Zero)
            {
                return 0;
            }

            if (process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
        }
        catch
        {
            // The process may exit while shutdown ordering is computed.
        }

        return 2;
    }

    private static DateTime TryGetProcessStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return DateTime.MaxValue;
        }
    }

    private static bool IsCodexPlusPlusLauncherProcess(Process process)
    {
        try
        {
            return process.ProcessName.Equals("codex-plus-plus", StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessName.Equals("codex-plus-plus-manager", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProtectedWindowsClientShutdownProcessName(string processName)
    {
        var normalized = (processName ?? "").Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.Equals("CodexAccountManager", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("conhost", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodexWindowsClientProcess(Process process, string? packageRoot)
    {
        return TryClassifyCodexWindowsClientProcess(process, packageRoot, out var isOfficial) &&
               isOfficial;
    }

    private static bool TryClassifyCodexWindowsClientProcess(
        Process process,
        string? packageRoot,
        out bool isOfficial)
    {
        isOfficial = false;
        try
        {
            if (!process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) &&
                !process.ProcessName.Equals("Codex", StringComparison.OrdinalIgnoreCase) &&
                !process.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                return false;
            }

            var fileName = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            var fullFileName = Path.GetFullPath(fileName);
            var executableName = Path.GetFileName(fullFileName);
            if (!executableName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                !executableName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) &&
                !executableName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var root = Path.GetFullPath(packageRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsPathWithinDirectory(fullFileName, root))
            {
                isOfficial = true;
                return true;
            }

            // A Store update can register a new package version while processes from the old
            // version are still shutting down. Trust sibling versions only when both paths are
            // below the real WindowsApps directory and have the exact same package identity and
            // publisher ID; an arbitrary CODEX_WINDOWS_CLIENT_PATH override remains exact-root
            // only and cannot broaden process termination to another directory.
            if (TryGetWindowsAppsPackageFamily(
                    root,
                    out var trustedWindowsAppsRoot,
                    out var trustedIdentityName,
                    out var trustedPublisherId) &&
                trustedIdentityName.Equals("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) &&
                TryGetWindowsAppsPackageFamily(
                    fullFileName,
                    out var candidateWindowsAppsRoot,
                    out var candidateIdentityName,
                    out var candidatePublisherId) &&
                PathsEqual(trustedWindowsAppsRoot, candidateWindowsAppsRoot) &&
                trustedIdentityName.Equals(candidateIdentityName, StringComparison.OrdinalIgnoreCase) &&
                trustedPublisherId.Equals(candidatePublisherId, StringComparison.OrdinalIgnoreCase))
            {
                isOfficial = true;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathWithinDirectory(string candidatePath, string directoryPath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var directory = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.StartsWith(
            directory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetWindowsAppsPackageFamily(
        string path,
        out string windowsAppsRoot,
        out string identityName,
        out string publisherId)
    {
        windowsAppsRoot = "";
        identityName = "";
        publisherId = "";
        try
        {
            var candidateWindowsAppsRoot = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WindowsApps"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (!IsPathWithinDirectory(fullPath, candidateWindowsAppsRoot))
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(candidateWindowsAppsRoot, fullPath);
            var separatorIndex = relativePath.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var packageFolderName = separatorIndex < 0
                ? relativePath
                : relativePath[..separatorIndex];
            var identitySeparator = packageFolderName.IndexOf('_');
            var publisherSeparator = packageFolderName.LastIndexOf("__", StringComparison.Ordinal);
            if (identitySeparator <= 0 ||
                publisherSeparator <= identitySeparator + 1 ||
                publisherSeparator + 2 >= packageFolderName.Length)
            {
                return false;
            }

            var parsedIdentityName = packageFolderName[..identitySeparator];
            var parsedPublisherId = packageFolderName[(publisherSeparator + 2)..];
            if (!Regex.IsMatch(
                    parsedIdentityName,
                    "^[A-Za-z0-9.-]{1,128}$",
                    RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(
                    parsedPublisherId,
                    "^[A-Za-z0-9]{1,64}$",
                    RegexOptions.CultureInvariant))
            {
                return false;
            }

            windowsAppsRoot = candidateWindowsAppsRoot;
            identityName = parsedIdentityName;
            publisherId = parsedPublisherId;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? GetAccessTokenExpiryUtc(string accessToken)
    {
        accessToken = NormalizeAccessTokenInput(accessToken);
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = DecodeBase64Url(parts[1]);
            using var json = JsonDocument.Parse(payload);
            if (!json.RootElement.TryGetProperty("exp", out var exp))
            {
                return null;
            }

            var unix = exp.GetInt64();
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }
        catch
        {
            return null;
        }
    }

    public static string NormalizeAccessTokenInput(string accessToken)
    {
        var token = (accessToken ?? "").Trim();
        token = token.Trim('"', '\'');

        if (token.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
        {
            var bearerIndex = token.IndexOf("Bearer ", StringComparison.OrdinalIgnoreCase);
            if (bearerIndex >= 0)
            {
                token = token[(bearerIndex + "Bearer ".Length)..].Trim();
            }
        }
        else if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = token["Bearer ".Length..].Trim();
        }

        return token.Trim('"', '\'').Trim();
    }

    public static string? GetAccessTokenInputError(string accessToken)
    {
        var token = NormalizeAccessTokenInput(accessToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return "请先粘贴 Codex Access Token。";
        }

        if (token.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
        {
            return "你粘贴的是 API Key，不是 Codex Access Token。\n\nAPI Key 请放到“兼容 API”账号里；普通 Access Token 账号需要 Codex 登录令牌。";
        }

        if (!token.StartsWith("at-", StringComparison.Ordinal))
        {
            return "本地 PAT 网关只接受 at- 开头的 Codex Personal Access Token。";
        }

        if (token.StartsWith("{", StringComparison.Ordinal) ||
            token.Contains("\"access_token\"", StringComparison.OrdinalIgnoreCase) ||
            token.Contains("\"refresh_token\"", StringComparison.OrdinalIgnoreCase) ||
            token.Contains("\"id_token\"", StringComparison.OrdinalIgnoreCase))
        {
            return "你粘贴的像是一整段 JSON。这里只能填写 Codex Access Token 本体，不要粘贴 auth.json 的完整内容。";
        }

        if (token.Any(char.IsWhiteSpace))
        {
            return "Access Token 里不能包含空格或换行。请只粘贴 token 本体。";
        }

        return null;
    }

    public bool IsCodexPlusPlusReady()
    {
        return IsCodexPlusPlusReadySince(DateTime.MinValue, out _);
    }

    private bool IsCodexPlusPlusReadySince(DateTime earliestStartUtc, out string statusDetail)
    {
        try
        {
            var statusPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex-session-delete",
                "latest-status.json");
            if (!File.Exists(statusPath))
            {
                statusDetail = "尚未生成 latest-status.json";
                return false;
            }

            var status = JsonNode.Parse(File.ReadAllText(statusPath)) as JsonObject;
            var state = status?["status"]?.GetValue<string>() ?? "unknown";
            var message = status?["message"]?.GetValue<string>() ?? "";
            var startedAtMs = status?["started_at_ms"]?.GetValue<long>() ?? 0;
            var earliestStartMs = earliestStartUtc == DateTime.MinValue
                ? long.MinValue
                : new DateTimeOffset(earliestStartUtc).ToUnixTimeMilliseconds();
            if (startedAtMs < earliestStartMs)
            {
                statusDetail = "检测到的桥接状态属于上一次启动";
                return false;
            }
            if (!state.Equals("running", StringComparison.Ordinal))
            {
                statusDetail = string.IsNullOrWhiteSpace(message)
                    ? $"桥接状态为 {state}"
                    : $"桥接状态为 {state}：{message}";
                return false;
            }

            var helperPort = status?["helper_port"]?.GetValue<int>() ?? 0;
            if (helperPort is <= 0 or > 65535)
            {
                statusDetail = "桥接没有提供有效的本地端口";
                return false;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(750) };
            using var body = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = client.PostAsync(
                    $"http://127.0.0.1:{helperPort}/backend/status",
                    body)
                .GetAwaiter()
                .GetResult();
            if (!response.IsSuccessStatusCode)
            {
                statusDetail = $"桥接端口 {helperPort} 返回 HTTP {(int)response.StatusCode}";
                return false;
            }

            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var responseStatus = JsonNode.Parse(responseBody) as JsonObject;
            var ready = string.Equals(
                responseStatus?["status"]?.GetValue<string>(),
                "ok",
                StringComparison.OrdinalIgnoreCase);
            statusDetail = ready
                ? $"桥接端口 {helperPort} 已响应"
                : $"桥接端口 {helperPort} 未返回 ok";
            return ready;
        }
        catch (Exception ex)
        {
            statusDetail = MaskSensitive(ex.Message);
            return false;
        }
    }

    public bool IsCodexPlusPlusRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!IsCodexPlusPlusLauncherProcess(process))
                {
                    continue;
                }

                try
                {
                    if (!process.HasExited)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Elevated launchers can become inaccessible while they start or exit.
                }
            }
        }

        return false;
    }

    private static async Task<CommandResult> RunOfficialBrowserAuthorizationAsync(
        string codexHome,
        IProgress<ChatGptOAuthAuthorization>? progress,
        CancellationToken cancellationToken)
    {
        var command = ResolveBundledCodexCliCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException(
                "找不到项目内置的官方登录组件。请修复软件安装后重试；不会改用全局 Codex。"
            );
        }

        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        startInfo.ArgumentList.Add("--disable");
        startInfo.ArgumentList.Add("plugins");
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["CODEX_SQLITE_HOME"] = codexHome;
        foreach (var variableName in new[]
                 {
                     "OPENAI_API_KEY",
                     "OPENAI_ACCESS_TOKEN",
                     "OPENAI_TOKEN",
                     "CODEX_ACCESS_TOKEN",
                     "CODEX_API_KEY",
                     "AZURE_OPENAI_API_KEY"
                 })
        {
            startInfo.Environment.Remove(variableName);
        }
        ApplyProxyEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };
        Task<string>? stderrTask = null;
        string? loginId = null;
        var loginCompleted = false;
        try
        {
            process.Start();
            stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await WriteAppServerMessageAsync(
                process,
                new JsonObject
                {
                    ["id"] = 1,
                    ["method"] = "initialize",
                    ["params"] = new JsonObject
                    {
                        ["clientInfo"] = new JsonObject
                        {
                            ["name"] = "codex-account-manager",
                            ["title"] = "Codex Account Manager",
                            ["version"] = "1.0.0"
                        },
                        ["capabilities"] = new JsonObject
                        {
                            ["experimentalApi"] = true
                        }
                    }
                },
                cancellationToken);
            _ = await ReadAppServerResponseAsync(
                process,
                requestId: 1,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            await WriteAppServerMessageAsync(
                process,
                new JsonObject { ["method"] = "initialized" },
                cancellationToken);

            await WriteAppServerMessageAsync(
                process,
                new JsonObject
                {
                    ["id"] = 2,
                    ["method"] = "account/login/start",
                    ["params"] = new JsonObject { ["type"] = "chatgpt" }
                },
                cancellationToken);
            var loginResponse = await ReadAppServerResponseAsync(
                process,
                requestId: 2,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            if (!TryReadJsonString(loginResponse, "type", out var responseType) ||
                !responseType.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) ||
                !TryReadJsonString(loginResponse, "loginId", out loginId) ||
                !TryReadJsonString(loginResponse, "authUrl", out var authUrl) ||
                !IsAllowedOfficialOAuthAuthorizationUri(authUrl))
            {
                throw new InvalidOperationException(
                    "官方登录服务没有返回可验证的 OpenAI HTTPS 登录链接，已拒绝继续。"
                );
            }

            // The URL is intentionally reported only to the in-memory UI callback. It is never
            // written to logs, account manifests, settings, or command-line arguments.
            progress?.Report(new ChatGptOAuthAuthorization(authUrl));

            using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loginTimeout.CancelAfter(TimeSpan.FromMinutes(15));
            while (true)
            {
                var message = await ReadAppServerMessageAsync(
                    process.StandardOutput,
                    TimeSpan.FromMinutes(15),
                    loginTimeout.Token);
                if (!TryReadJsonString(message, "method", out var method) ||
                    !method.Equals("account/login/completed", StringComparison.Ordinal) ||
                    message["params"] is not JsonObject parameters ||
                    !TryReadJsonString(parameters, "loginId", out var completedLoginId) ||
                    !completedLoginId.Equals(loginId, StringComparison.Ordinal))
                {
                    continue;
                }

                var success = parameters["success"] is JsonValue successValue &&
                              successValue.TryGetValue<bool>(out var succeeded) &&
                              succeeded;
                if (!success)
                {
                    var detail = TryReadJsonString(parameters, "error", out var error)
                        ? MaskSensitive(error)
                        : "OpenAI 官方页面没有完成授权。";
                    throw new InvalidOperationException("ChatGPT 官方网页登录失败：" + detail);
                }

                loginCompleted = true;
                break;
            }

            var authPath = Path.Combine(codexHome, AuthFileName);
            var credentialDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!IsChatGptDesktopAuthJson(authPath) && DateTime.UtcNow < credentialDeadline)
            {
                await Task.Delay(100, cancellationToken);
            }
            if (!IsChatGptDesktopAuthJson(authPath))
            {
                throw new InvalidOperationException(
                    "网页登录已返回成功，但没有生成完整的 ChatGPT OAuth 凭据。"
                );
            }

            return new CommandResult(0, "ChatGPT official browser login completed.", string.Empty);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "已取消 ChatGPT 官方网页登录；旧登录凭据将自动恢复。",
                ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(
                "ChatGPT 登录链接已过期，或未在 15 分钟内完成登录；旧登录凭据将自动恢复。",
                ex);
        }
        finally
        {
            if (!loginCompleted && !string.IsNullOrWhiteSpace(loginId) && !HasProcessExited(process))
            {
                try
                {
                    var cancelRequest = new JsonObject
                    {
                        ["id"] = 3,
                        ["method"] = "account/login/cancel",
                        ["params"] = new JsonObject { ["loginId"] = loginId }
                    };
                    await process.StandardInput.WriteLineAsync(cancelRequest.ToJsonString());
                    await process.StandardInput.FlushAsync();
                }
                catch
                {
                    // The exact app-server process is terminated below even if graceful cancel fails.
                }
            }

            if (!HasProcessExited(process))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The exact child may have exited between the check and kill request.
                }
            }
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Closing the child pipes is sufficient if the process already disappeared.
            }
            if (stderrTask != null)
            {
                try
                {
                    _ = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Stderr is diagnostic only and must never delay login cleanup.
                }
            }
        }
    }

    private static async Task WriteAppServerMessageAsync(
        Process process,
        JsonObject message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasProcessExited(process))
        {
            throw new InvalidOperationException("官方登录服务已提前退出。");
        }
        await process.StandardInput.WriteLineAsync(message.ToJsonString());
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonObject> ReadAppServerResponseAsync(
        Process process,
        int requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReadAppServerMessageAsync(
                process.StandardOutput,
                timeout,
                cancellationToken);
            if (message["id"] is not JsonValue idValue ||
                !idValue.TryGetValue<int>(out var id) ||
                id != requestId)
            {
                continue;
            }
            if (message["error"] is JsonObject error)
            {
                var detail = TryReadJsonString(error, "message", out var messageText)
                    ? MaskSensitive(messageText)
                    : "未知错误";
                throw new InvalidOperationException("官方登录服务请求失败：" + detail);
            }
            if (message["result"] is JsonObject result)
            {
                return result;
            }
            throw new InvalidOperationException("官方登录服务返回了不完整的响应。");
        }
    }

    private static async Task<JsonObject> ReadAppServerMessageAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(timeout, cancellationToken);
            if (line == null)
            {
                throw new InvalidOperationException("官方登录服务在完成网页登录前退出。");
            }
            try
            {
                if (JsonNode.Parse(line) is JsonObject message)
                {
                    return message;
                }
            }
            catch (JsonException)
            {
                // Ignore non-protocol diagnostic lines; sensitive content is never surfaced.
            }
        }
    }

    internal static bool IsAllowedOfficialOAuthAuthorizationUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (!uri.IsDefaultPort && uri.Port != 443))
        {
            return false;
        }

        return uri.Host.Equals(OfficialOAuthAuthorizationHost, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.Equals("/codex/desktop-auth", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateOfficialOAuthBrowserFlow()
    {
        if (!IsAllowedOfficialOAuthAuthorizationUri(
                "https://auth.openai.com/oauth/authorize?client_id=test&state=test") ||
            !IsAllowedOfficialOAuthAuthorizationUri(
                "https://chatgpt.com/codex/desktop-auth?authorize_url=test") ||
            IsAllowedOfficialOAuthAuthorizationUri(
                "http://auth.openai.com/oauth/authorize?client_id=test") ||
            IsAllowedOfficialOAuthAuthorizationUri(
                "https://auth.openai.com.evil.example/oauth/authorize") ||
            IsAllowedOfficialOAuthAuthorizationUri(
                "https://x@auth.openai.com/oauth/authorize") ||
            IsAllowedOfficialOAuthAuthorizationUri(
                "https://chatgpt.com/not-codex-login"))
        {
            throw new InvalidOperationException(
                "Official ChatGPT browser login URL validation must accept only the intended OpenAI HTTPS endpoints."
            );
        }

        var loginRequest = new JsonObject
        {
            ["id"] = 2,
            ["method"] = "account/login/start",
            ["params"] = new JsonObject { ["type"] = "chatgpt" }
        };
        if (!TryReadJsonString(loginRequest, "method", out var method) ||
            !method.Equals("account/login/start", StringComparison.Ordinal) ||
            loginRequest["params"] is not JsonObject parameters ||
            !TryReadJsonString(parameters, "type", out var loginType) ||
            !loginType.Equals("chatgpt", StringComparison.Ordinal) ||
            loginRequest.ToJsonString().Contains("device", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Official ChatGPT login must use app-server account/login/start with the browser OAuth type."
            );
        }
    }

    private static async Task<CommandResult> RunOfficialDeviceAuthorizationAsync(
        string codexHome,
        IProgress<ChatGptDeviceAuthorization>? progress,
        CancellationToken cancellationToken)
    {
        var command = ResolveBundledCodexCliCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException(
                "找不到项目内置的 Codex CLI，无法启动官方设备授权。请修复软件安装后重试；不会改用全局 Codex。"
            );
        }

        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("login");
        startInfo.ArgumentList.Add("--device-auth");
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["CODEX_SQLITE_HOME"] = codexHome;
        foreach (var variableName in new[]
                 {
                     "OPENAI_API_KEY",
                     "OPENAI_ACCESS_TOKEN",
                     "OPENAI_TOKEN",
                     "CODEX_ACCESS_TOKEN",
                     "CODEX_API_KEY",
                     "AZURE_OPENAI_API_KEY"
                 })
        {
            startInfo.Environment.Remove(variableName);
        }
        ApplyProxyEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };
        var output = new DeviceAuthorizationOutputCollector();
        Task? stdoutTask = null;
        Task? stderrTask = null;
        DeviceAuthBrowserSession? browserSession = null;
        try
        {
            process.Start();
            process.StandardInput.Close();
            stdoutTask = PumpDeviceAuthorizationOutputAsync(process.StandardOutput, output, isError: false);
            stderrTask = PumpDeviceAuthorizationOutputAsync(process.StandardError, output, isError: true);
            var exitTask = process.WaitForExitAsync();

            try
            {
                _ = await Task.WhenAny(output.Authorization, exitTask)
                    .WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    "Codex 官方设备授权未在 45 秒内返回登录网址和设备码；旧登录凭据将自动恢复。",
                    ex);
            }

            if (!output.Authorization.IsCompletedSuccessfully)
            {
                await Task.WhenAll(stdoutTask, stderrTask);
                var failure = output.GetSanitizedCombinedOutput();
                throw new InvalidOperationException(
                    "Codex 官方设备授权在显示设备码前结束。" +
                    (string.IsNullOrWhiteSpace(failure) ? string.Empty : "\n\n" + failure));
            }

            var authorization = await output.Authorization;
            browserSession = StartIsolatedDeviceAuthorizationBrowser(authorization.VerificationUrl);
            progress?.Report(new ChatGptDeviceAuthorization(
                authorization.VerificationUrl,
                authorization.UserCode,
                browserSession.Started,
                browserSession.BrowserDisplayName,
                browserSession.StartNotice));

            try
            {
                await exitTask.WaitAsync(TimeSpan.FromMinutes(15), cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    "ChatGPT 设备码已过期或授权未在 15 分钟内完成；旧登录凭据将自动恢复。",
                    ex);
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            return new CommandResult(
                process.ExitCode,
                output.GetSanitizedStdOut(),
                output.GetSanitizedStdErr());
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "已取消 ChatGPT 设备授权；旧登录凭据将自动恢复。",
                ex);
        }
        finally
        {
            if (!HasProcessExited(process))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The exact CLI process may have exited between the check and kill.
                }
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Stream pumps below also finish when the process handles close.
            }

            if (stdoutTask != null && stderrTask != null)
            {
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Never surface a cleanup failure in place of the login result.
                }
            }

            if (browserSession != null)
            {
                await browserSession.DisposeAsync();
            }
        }
    }

    private static async Task PumpDeviceAuthorizationOutputAsync(
        StreamReader reader,
        DeviceAuthorizationOutputCollector output,
        bool isError)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            output.Append(line, isError);
        }
    }

    internal static bool TryParseOfficialDeviceAuthorization(
        string output,
        out string verificationUrl,
        out string userCode)
    {
        verificationUrl = string.Empty;
        userCode = string.Empty;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var plainText = AnsiEscapePattern.Replace(output, string.Empty);
        if (!plainText.Contains("one-time code", StringComparison.OrdinalIgnoreCase) &&
            !plainText.Contains("device code", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Uri? officialUri = null;
        foreach (Match match in DeviceAuthorizationUrlPattern.Matches(plainText))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '}');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                IsAllowedOfficialDeviceAuthorizationUri(uri))
            {
                officialUri = uri;
                break;
            }
        }

        var codeMatch = DeviceAuthorizationCodePattern.Match(plainText);
        if (officialUri == null || !codeMatch.Success)
        {
            return false;
        }

        verificationUrl = officialUri.AbsoluteUri;
        userCode = codeMatch.Value.ToUpperInvariant();
        return true;
    }

    private static bool IsAllowedOfficialDeviceAuthorizationUri(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               (uri.IsDefaultPort || uri.Port == 443) &&
               uri.Host.Equals(OfficialDeviceAuthorizationHost, StringComparison.OrdinalIgnoreCase) &&
               (uri.AbsolutePath.Equals("/codex/device", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.Equals("/codex/device/", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveBundledCodexCliCommand()
    {
        foreach (var root in GetCandidateManagerRoots())
        {
            var localCli = Path.Combine(root, LocalCodexCliRelativePath);
            if (File.Exists(localCli))
            {
                return localCli;
            }
        }

        return null;
    }

    private static DeviceAuthBrowserSession StartIsolatedDeviceAuthorizationBrowser(
        string verificationUrl)
    {
        if (!Uri.TryCreate(verificationUrl, UriKind.Absolute, out var uri) ||
            !IsAllowedOfficialDeviceAuthorizationUri(uri))
        {
            throw new InvalidOperationException(
                "Codex CLI 返回了非 OpenAI 官方 HTTPS 设备授权地址，已拒绝打开浏览器。"
            );
        }

        var browser = ResolveIsolatedDeviceAuthBrowser();
        if (browser == null)
        {
            return DeviceAuthBrowserSession.NotStarted(
                "未找到 Google Chrome 或 Microsoft Edge。请仅使用窗口中显示的官方网址和设备码手动继续。"
            );
        }

        var profileRoot = GetDeviceAuthBrowserTempRoot();
        Directory.CreateDirectory(profileRoot);
        var profileDirectory = Path.Combine(
            profileRoot,
            DeviceAuthBrowserProfilePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profileDirectory);
        var startInfo = BuildDeviceAuthBrowserStartInfo(
            browser.ExecutablePath,
            browser.Kind,
            profileDirectory,
            uri.AbsoluteUri);
        try
        {
            var process = Process.Start(startInfo);
            if (process == null)
            {
                TryDeleteOwnedDeviceAuthBrowserProfile(profileDirectory);
                return DeviceAuthBrowserSession.NotStarted(
                    $"{browser.DisplayName} 未能启动。请仅使用窗口中显示的官方网址和设备码手动继续。"
                );
            }

            return DeviceAuthBrowserSession.StartedSession(
                process,
                profileDirectory,
                browser.DisplayName,
                $"已在 {browser.DisplayName} 的全新无 Cookie 临时会话中打开官方登录页。"
            );
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            TryDeleteOwnedDeviceAuthBrowserProfile(profileDirectory);
            return DeviceAuthBrowserSession.NotStarted(
                $"{browser.DisplayName} 启动失败。请仅使用窗口中显示的官方网址和设备码手动继续。"
            );
        }
    }

    internal static ProcessStartInfo BuildDeviceAuthBrowserStartInfo(
        string browserExecutablePath,
        DeviceAuthBrowserKind browserKind,
        string profileDirectory,
        string verificationUrl)
    {
        if (!Uri.TryCreate(verificationUrl, UriKind.Absolute, out var uri) ||
            !IsAllowedOfficialDeviceAuthorizationUri(uri))
        {
            throw new ArgumentException(
                "Only the official OpenAI HTTPS device authorization URL is allowed.",
                nameof(verificationUrl));
        }
        if (!IsOwnedDeviceAuthBrowserProfile(profileDirectory))
        {
            throw new ArgumentException(
                "The browser profile must be an owned one-time directory under the system temp directory.",
                nameof(profileDirectory));
        }

        var startInfo = new ProcessStartInfo(browserExecutablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(browserExecutablePath) ?? Path.GetTempPath(),
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--user-data-dir=" + Path.GetFullPath(profileDirectory));
        startInfo.ArgumentList.Add(browserKind == DeviceAuthBrowserKind.Edge ? "--inprivate" : "--incognito");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-background-mode");
        startInfo.ArgumentList.Add("--disable-sync");
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(uri.AbsoluteUri);
        return startInfo;
    }

    private static DeviceAuthBrowserExecutable? ResolveIsolatedDeviceAuthBrowser()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // The user explicitly chose Chrome for account selection. Keep every Chrome
        // installation location ahead of Edge while retaining Edge as a safe fallback.
        var candidates = new[]
        {
            new DeviceAuthBrowserExecutable(
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                "Google Chrome",
                DeviceAuthBrowserKind.Chrome),
            new DeviceAuthBrowserExecutable(
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                "Google Chrome",
                DeviceAuthBrowserKind.Chrome),
            new DeviceAuthBrowserExecutable(
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
                "Google Chrome",
                DeviceAuthBrowserKind.Chrome),
            new DeviceAuthBrowserExecutable(
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                "Microsoft Edge",
                DeviceAuthBrowserKind.Edge),
            new DeviceAuthBrowserExecutable(
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                "Microsoft Edge",
                DeviceAuthBrowserKind.Edge),
            new DeviceAuthBrowserExecutable(
                Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"),
                "Microsoft Edge",
                DeviceAuthBrowserKind.Edge)
        };
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ExecutablePath))
            .DistinctBy(candidate => candidate.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate => File.Exists(candidate.ExecutablePath));
    }

    private static bool IsOwnedDeviceAuthBrowserProfile(string profileDirectory)
    {
        try
        {
            var fullPath = Path.GetFullPath(profileDirectory);
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullPath));
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
            var profileRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(GetDeviceAuthBrowserTempRoot()));
            return parent != null &&
                   Path.TrimEndingDirectorySeparator(parent).Equals(
                       profileRoot,
                       StringComparison.OrdinalIgnoreCase) &&
                   fullPath.StartsWith(
                       profileRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase) &&
                   name.StartsWith(DeviceAuthBrowserProfilePrefix, StringComparison.OrdinalIgnoreCase) &&
                   name.Length > DeviceAuthBrowserProfilePrefix.Length;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string GetDeviceAuthBrowserTempRoot()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "CodexAccountManager",
            "device-auth-browser");
    }

    private static void TryDeleteOwnedDeviceAuthBrowserProfile(string? profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory) ||
            !IsOwnedDeviceAuthBrowserProfile(profileDirectory) ||
            !Directory.Exists(profileDirectory))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(profileDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 4)
                {
                    Thread.Sleep(120);
                }
            }
        }
    }

    private static bool HasProcessExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task<CommandResult> RunCodexAsync(
        string arguments,
        string codexHome,
        string? stdin,
        TimeSpan? timeout = null,
        bool clearCredentialEnvironment = false)
    {
        if (UsesLocalPatGateway(codexHome))
        {
            await LocalPatGateway.EnsureRunningAsync();
        }
        var command = ResolveCodexCliCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException(
                "找不到可用的 Codex CLI。请从 CodexAccountManager.cmd 启动，或确认项目内置的 .tools\\codex-cli 已安装。");
        }

        var startInfo = new ProcessStartInfo(command, arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["CODEX_SQLITE_HOME"] = codexHome;
        if (clearCredentialEnvironment)
        {
            foreach (var variableName in new[]
                     {
                         "OPENAI_API_KEY",
                         "OPENAI_ACCESS_TOKEN",
                         "OPENAI_TOKEN",
                         "CODEX_ACCESS_TOKEN",
                         "CODEX_API_KEY",
                         "AZURE_OPENAI_API_KEY"
                     })
            {
                startInfo.Environment.Remove(variableName);
            }
        }
        ApplyProxyEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        if (stdin != null)
        {
            await process.StandardInput.WriteLineAsync(stdin);
        }
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var waitTimeout = timeout ?? TimeSpan.FromMinutes(2);
        var timeoutMilliseconds = (int)Math.Clamp(
            waitTimeout.TotalMilliseconds,
            1D,
            int.MaxValue);
        var exited = await Task.Run(() => process.WaitForExit(timeoutMilliseconds));
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may already have exited between the timeout and kill attempt.
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Timeout cleanup is best-effort; the original timeout remains the useful error.
            }
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Child-pipe cleanup must not delay reporting the command timeout.
            }

            throw new TimeoutException(
                arguments.Equals("login", StringComparison.Ordinal)
                    ? "等待 ChatGPT 官方登录超时；账号尚未被标记为登录成功，请重新点击登录。"
                    : "Codex 命令超时。");
        }

        return new CommandResult(
            process.ExitCode,
            MaskSensitive(await stdoutTask),
            MaskSensitive(await stderrTask));
    }

    private static bool UsesLocalPatGateway(string codexHome)
    {
        try
        {
            var configPath = Path.Combine(codexHome, ConfigFileName);
            return File.Exists(configPath) &&
                   File.ReadAllText(configPath).Contains(
                       AccountStore.AccessTokenBaseUrl,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ApplyProxyEnvironment(ProcessStartInfo startInfo)
    {
        var proxyUri = GetConfiguredProxyUri();
        if (string.IsNullOrWhiteSpace(proxyUri))
        {
            return;
        }

        SetProxyEnvironment(startInfo, proxyUri);
    }

    internal static void ConfigureChildCodexProcessEnvironment(
        ProcessStartInfo startInfo,
        string codexHome)
    {
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["CODEX_SQLITE_HOME"] = codexHome;
        ApplyProxyEnvironment(startInfo);
    }

    private static void SetProxyEnvironment(ProcessStartInfo startInfo, string proxyUri)
    {
        foreach (var variableName in ProxyEnvironmentVariableNames)
        {
            startInfo.Environment[variableName] = proxyUri;
        }

        foreach (var variableName in ProxyBypassEnvironmentVariableNames)
        {
            startInfo.Environment.TryGetValue(variableName, out var existing);
            var entries = (existing ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            foreach (var loopback in CodexLoopbackProxyBypass.Split(','))
            {
                if (!entries.Contains(loopback, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(loopback);
                }
            }
            startInfo.Environment[variableName] = string.Join(',', entries);
        }
    }

    private static bool IsPersonalAccessTokenMetadataRequestFailure(string text)
    {
        return text.Contains("personal access token metadata", StringComparison.OrdinalIgnoreCase) &&
               (text.Contains("error sending request", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("whoami", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildPersonalAccessTokenNetworkMessage(string detail)
    {
        var proxy = GetConfiguredProxyUri() ?? "未检测到系统代理";
        var diagnostic = ExtractMinimalQuotaTestJsonError(detail) ?? detail.Trim();
        if (diagnostic.Length > 600)
        {
            diagnostic = diagnostic[..600] + "…";
        }
        return
            "Codex 无法请求 Access Token 元数据，但这不等于令牌已失效。\n\n" +
            "网页版显示有效时，通常是本机 Codex CLI 访问 auth.openai.com 的网络或代理不稳定。请确认本地 HTTP 代理（如 v2rayN、Clash 或 Mihomo）正在运行，然后重试。\n\n" +
            $"当前检测到的系统代理：{proxy}\n\n" +
            diagnostic;
    }

    internal static string? GetConfiguredProxyUri()
    {
        var explicitProxy = Environment.GetEnvironmentVariable("CODEX_PAT_GATEWAY_PROXY");
        if (!string.IsNullOrWhiteSpace(explicitProxy))
        {
            return NormalizeProxyServer(explicitProxy);
        }

        // The value entered in the Account Manager is the stable per-installation source of
        // truth. This keeps a stale HTTP_PROXY inherited from a launcher from silently
        // overriding a v2rayN port selected in the UI.
        var loopbackOnly = false;
        try
        {
            var settings = new ThemeService(new AccountStore().RootPath).LoadSettings();
            loopbackOnly = settings.PatGatewayProxyAutoDetect;
            var configured = BuildPatGatewayProxyUri(settings);
            if (!string.IsNullOrWhiteSpace(configured) &&
                (!loopbackOnly || IsLoopbackProxyUri(configured)))
            {
                return configured;
            }

            configured = settings.PatGatewayProxy;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var normalized = NormalizeProxyServer(configured);
                if (!loopbackOnly || IsLoopbackProxyUri(normalized))
                {
                    return normalized;
                }
            }
        }
        catch
        {
            // Fall through to environment and Windows proxy discovery.
        }

        foreach (var variableName in new[]
                 {
                     "HTTPS_PROXY",
                     "https_proxy",
                     "HTTP_PROXY",
                     "http_proxy",
                     "ALL_PROXY",
                     "all_proxy"
                 })
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                var normalized = NormalizeProxyServer(value);
                if (!loopbackOnly || IsLoopbackProxyUri(normalized))
                {
                    return normalized;
                }
            }
        }

        var windowsProxy = GetWindowsProxyUri();
        return !loopbackOnly || IsLoopbackProxyUri(windowsProxy)
            ? windowsProxy
            : null;
    }

    internal static bool IsLoopbackProxyUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return LocalProxyDetector.IsLoopbackHost(uri.Host);
    }

    internal static string? BuildPatGatewayProxyUri(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var address = settings.PatGatewayProxyAddress?.Trim() ?? "";
        var port = settings.PatGatewayProxyPort;
        var scheme = settings.PatGatewayProxyScheme?.Trim().ToLowerInvariant() ?? "http";
        if (string.IsNullOrWhiteSpace(address) ||
            port is not (> 0 and <= 65535) ||
            scheme is not ("http" or "https") ||
            address.Contains('/') ||
            address.Contains('\\') ||
            address.Any(char.IsWhiteSpace))
        {
            return null;
        }

        try
        {
            var uri = new UriBuilder(scheme, address, port.Value).Uri;
            return uri.GetLeftPart(UriPartial.Authority);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    internal static bool TryParseProxyEndpoint(
        string? value,
        out string address,
        out int port,
        out string scheme)
    {
        address = "";
        port = 0;
        scheme = "http";
        var normalized = string.IsNullOrWhiteSpace(value) ? null : NormalizeProxyServer(value);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.Port is <= 0 or > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        address = uri.Host;
        port = uri.Port;
        scheme = uri.Scheme.ToLowerInvariant();
        return true;
    }

    internal static string? GetWindowsProxyUri()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null)
            {
                return null;
            }

            var enabled = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0) != 0;
            var proxyServer = key.GetValue("ProxyServer") as string;
            if (!enabled || string.IsNullOrWhiteSpace(proxyServer))
            {
                return null;
            }

            return NormalizeProxyServer(proxyServer);
        }
        catch
        {
            return null;
        }
    }

    internal static string? NormalizeProxyServer(string proxyServer)
    {
        var selected = proxyServer.Trim();
        var scheme = "http";
        if (selected.Contains(';'))
        {
            foreach (var preferred in new[] { "https", "http", "socks" })
            {
                var prefix = preferred + "=";
                var part = selected
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (part == null)
                {
                    continue;
                }

                selected = part[prefix.Length..].Trim();
                scheme = preferred.Equals("socks", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }

        return selected.Contains("://", StringComparison.Ordinal)
            ? selected
            : scheme + "://" + selected;
    }

    private static string MaskSensitive(string value)
    {
        var masked = AnsiEscapePattern.Replace(value, string.Empty);
        masked = DeviceAuthorizationCodePattern.Replace(masked, "<redacted-device-code>");
        masked = ApiKeyPattern.Replace(masked, "sk-***");
        masked = PersonalAccessTokenPattern.Replace(masked, "at-***");
        masked = NamedApiKeyPattern.Replace(masked, "$1<redacted>");
        masked = JwtPattern.Replace(masked, "<redacted-token>");
        return masked.Trim();
    }

    internal static string MaskSensitiveText(string value) => MaskSensitive(value);

    private static string DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private static string CreateBackupDirectory(string profileHome)
    {
        var backupDirectory = Path.Combine(
            profileHome,
            "account-switcher-backups",
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(backupDirectory);
        return backupDirectory;
    }

    private static string? BackupFileIfPresent(string path, string backupDirectory)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var backupPath = Path.Combine(backupDirectory, Path.GetFileName(path));
        File.Copy(path, backupPath, true);
        return backupPath;
    }

    private static string? BackupSqliteDatabase(string path, string backupDirectory)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        EnsureSqliteProvider();
        var backupPath = Path.Combine(backupDirectory, Path.GetFileName(path));
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        using var source = new SqliteConnection("Data Source=" + path);
        using var destination = new SqliteConnection("Data Source=" + backupPath);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        return backupPath;
    }

    private static bool IsProjectedDesktopCredentialSelected(
        AccountRecord account,
        string targetAuthPath)
    {
        var sourceAuthPath = Path.Combine(account.CodexHome, AuthFileName);
        var profileHome = Path.GetDirectoryName(Path.GetFullPath(targetAuthPath));
        if (!string.IsNullOrWhiteSpace(profileHome) &&
            IsAccessTokenDesktopSessionSelected(account, profileHome, targetAuthPath))
        {
            return true;
        }

        return IsAccessTokenDesktopAuthSelected(sourceAuthPath, targetAuthPath);
    }

    private static bool IsAccessTokenDesktopSessionSelected(
        AccountRecord account,
        string profileHome,
        string targetAuthPath)
    {
        if (!IsDesktopSelectionForAccount(profileHome, account))
        {
            return false;
        }

        return !File.Exists(targetAuthPath) || IsChatGptDesktopAuthJson(targetAuthPath);
    }

    private static bool IsDesktopSelectionForAccount(string profileHome, AccountRecord account)
    {
        return TryReadDesktopSelectionKey(profileHome, out var selectedKey) &&
               selectedKey.Equals(GetDesktopAccountKey(account), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDesktopAuthSelectionForAccount(string profileHome, AccountRecord account)
    {
        return TryReadDesktopSelectionKeys(
                   profileHome,
                   out _,
                   out var selectedAuthKey) &&
               selectedAuthKey.Equals(
                   GetDesktopAccountKey(account),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadDesktopSelectionKey(string profileHome, out string accountKey)
    {
        return TryReadDesktopSelectionKeys(profileHome, out accountKey, out _);
    }

    private static bool TryReadDesktopSelectionKeys(
        string profileHome,
        out string accountKey,
        out string authAccountKey)
    {
        return TryReadDesktopSelectionKeys(
            profileHome,
            out accountKey,
            out authAccountKey,
            out _);
    }

    private static bool TryReadDesktopSelectionKeys(
        string profileHome,
        out string accountKey,
        out string authAccountKey,
        out string authAccountHome)
    {
        accountKey = "";
        authAccountKey = "";
        authAccountHome = "";
        var path = Path.Combine(profileHome, DesktopSelectionFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            _ = BuildCanonicalJsonSha256(path);
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var version) ||
                version is not (1 or 2) ||
                !TryReadJsonString(root, "accountKey", out var value) ||
                !IsDesktopAccountKey(value))
            {
                return false;
            }

            var authValue = value;
            var authHomeValue = "";
            if (version == 2 &&
                (!TryReadJsonString(root, "authAccountKey", out authValue) ||
                 !IsDesktopAccountKey(authValue) ||
                 !TryReadJsonString(root, "authAccountHome", out authHomeValue)))
            {
                return false;
            }
            if (version == 2)
            {
                var normalizedAuthHome = Path.GetFullPath(authHomeValue);
                if (PathsEqual(normalizedAuthHome, profileHome) ||
                    !GetDesktopAccountKeyForHome(normalizedAuthHome).Equals(
                        authValue,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                authHomeValue = normalizedAuthHome;
            }

            accountKey = value;
            authAccountKey = authValue;
            authAccountHome = authHomeValue;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void WriteDesktopSelection(
        string profileHome,
        AccountRecord account,
        AccountRecord? chatGptFeatureAccount = null)
    {
        var authAccount = chatGptFeatureAccount ?? account;
        if (chatGptFeatureAccount != null && !authAccount.IsOfficialOAuth)
        {
            throw new InvalidOperationException(
                "Desktop ChatGPT auth selection must reference an official OAuth account.");
        }

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var contents = chatGptFeatureAccount == null && !account.IsOfficialOAuth
            ? JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    accountKey = GetDesktopAccountKey(account),
                    accountName = account.Name,
                    mode = account.IsCompatibleApi
                        ? "chatgpt-app-plus-compatible-api"
                        : "chatgpt-app-plus-personal-access-token"
                },
                jsonOptions)
            : JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 2,
                    accountKey = GetDesktopAccountKey(account),
                    accountName = account.Name,
                    authAccountKey = GetDesktopAccountKey(authAccount),
                    authAccountName = authAccount.Name,
                    authAccountHome = Path.GetFullPath(authAccount.CodexHome),
                    mode = account.IsOfficialOAuth
                        ? "chatgpt-official-oauth"
                        : account.IsCompatibleApi
                            ? "chatgpt-app-plus-compatible-api"
                            : "chatgpt-app-plus-personal-access-token"
                },
                jsonOptions);
        WriteTextAtomically(Path.Combine(profileHome, DesktopSelectionFileName), contents);
    }

    private static string GetDesktopAccountKey(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return GetDesktopAccountKeyForHome(account.CodexHome);
    }

    private static string GetDesktopAccountKeyForHome(string accountHome)
    {
        var canonicalHome = Path.GetFullPath(accountHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalHome)));
    }

    private static bool IsDesktopAccountKey(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static string GetStoredDesktopAuthPath(string profileHome, AccountRecord account)
    {
        return GetStoredDesktopAuthPath(profileHome, GetDesktopAccountKey(account));
    }

    private static string GetStoredDesktopAuthPath(string profileHome, string accountKey)
    {
        if (!IsDesktopAccountKey(accountKey))
        {
            throw new InvalidDataException("Invalid stored desktop account key.");
        }

        return Path.Combine(
            profileHome,
            DesktopAuthStoreDirectoryName,
            accountKey,
            DesktopAuthStoreFileName);
    }

    private static string GetGlobalStoredDesktopAuthPath(string profileHome)
    {
        return Path.Combine(
            profileHome,
            DesktopAuthStoreDirectoryName,
            DesktopGlobalAuthDirectoryName,
            DesktopAuthStoreFileName);
    }

    private static string GetPreferredOfficialOAuthAuthPath(
        string profileHome,
        AccountRecord account)
    {
        var accountPath = Path.Combine(account.CodexHome, AuthFileName);
        var storedPath = GetStoredDesktopAuthPath(profileHome, account);
        var identityReferencePath = TryReadChatGptAuthAccountId(accountPath, out _)
            ? accountPath
            : TryReadChatGptAuthAccountId(storedPath, out _)
                ? storedPath
                : null;
        var candidates = new[] { accountPath, storedPath }
            .Where(IsChatGptDesktopAuthJson)
            .Where(path => identityReferencePath == null ||
                           ChatGptAuthAccountsMatch(path, identityReferencePath))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => PathsEqual(path, accountPath) ? 0 : 1)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new FileNotFoundException(
                $"账号 {account.Name} 没有可用的 ChatGPT 官方登录凭据。",
                accountPath);
        }

        var preferred = candidates[0];
        if (!PathsEqual(preferred, accountPath))
        {
            CopyFileAtomically(preferred, accountPath);
        }
        return accountPath;
    }

    private static void RestoreOfficialOAuthLoginCredential(
        string authPath,
        string? backupPath)
    {
        if (File.Exists(authPath))
        {
            ClearReadOnlyAttribute(authPath);
            File.Delete(authPath);
        }

        if (backupPath != null && File.Exists(backupPath))
        {
            CopyFileAtomically(backupPath, authPath);
        }
    }

    private static void PersistSuccessfulOfficialOAuthLogin(AccountRecord account)
    {
        var accountPath = Path.Combine(account.CodexHome, AuthFileName);
        if (!IsChatGptDesktopAuthJson(accountPath))
        {
            throw new InvalidDataException(
                $"账号 {account.Name} 的官方 ChatGPT 登录凭据无效，无法保存账号快照。");
        }

        using var switchMutex = new Mutex(false, WindowsClientSwitchMutexName);
        var mutexAcquired = false;
        try
        {
            try
            {
                mutexAcquired = switchMutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }
            if (!mutexAcquired)
            {
                throw new TimeoutException(
                    "另一个 Codex 账号切换仍在进行，新的 ChatGPT 登录已保留在账号目录；请稍后重试启动。");
            }

            var profileHome = Path.GetFullPath(GetDefaultCodexHome());
            var storedPath = GetStoredDesktopAuthPath(profileHome, account);
            Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
            CopyFileAtomically(accountPath, storedPath);
        }
        finally
        {
            if (mutexAcquired)
            {
                switchMutex.ReleaseMutex();
            }
        }
    }

    private static void SyncStoredOfficialOAuthAuthToAccount(AccountRecord account)
    {
        if (!account.IsOfficialOAuth)
        {
            return;
        }

        var profileHome = Path.GetFullPath(GetDefaultCodexHome());
        var accountPath = Path.Combine(account.CodexHome, AuthFileName);
        var storedPath = GetStoredDesktopAuthPath(profileHome, account);
        var sharedPath = Path.Combine(profileHome, AuthFileName);
        var sharedSelected = IsDesktopAuthSelectionForAccount(profileHome, account);
        var identityReferencePath = TryReadChatGptAuthAccountId(accountPath, out _)
            ? accountPath
            : TryReadChatGptAuthAccountId(storedPath, out _)
                ? storedPath
                : null;
        bool IsIdentitySafeCandidate(string path) =>
            identityReferencePath == null ||
            ChatGptAuthAccountsMatch(path, identityReferencePath);
        var candidates = new List<(string Path, int TieBreak)>();
        if (IsChatGptDesktopAuthJson(accountPath))
        {
            candidates.Add((accountPath, 0));
        }
        if (sharedSelected &&
            IsChatGptDesktopAuthJson(sharedPath) &&
            IsIdentitySafeCandidate(sharedPath))
        {
            candidates.Add((sharedPath, 1));
        }
        if (IsChatGptDesktopAuthJson(storedPath) &&
            IsIdentitySafeCandidate(storedPath))
        {
            candidates.Add((storedPath, 2));
        }
        var preferred = candidates
            .OrderByDescending(candidate => File.GetLastWriteTimeUtc(candidate.Path))
            .ThenBy(candidate => candidate.TieBreak)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
        if (preferred == null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
        if (!PathsEqual(preferred, storedPath))
        {
            CopyFileAtomically(preferred, storedPath);
        }

        Directory.CreateDirectory(account.CodexHome);
        if (!PathsEqual(preferred, accountPath))
        {
            CopyFileAtomically(preferred, accountPath);
        }
        // Never rewrite the live shared auth from a status/login check. Projection into the
        // official App is allowed only after the switch mutex is held and the old renderer,
        // native bridge and CDP owner have reached quiescence.
    }

    private static void PersistSelectedDesktopChatGptAuth(string profileHome, string sharedAuthPath)
    {
        if (!TryReadDesktopSelectionKeys(
                profileHome,
                out _,
                out var selectedAuthKey,
                out var selectedAuthHome) ||
            !IsChatGptDesktopAuthJson(sharedAuthPath))
        {
            return;
        }

        var storedAuthPath = GetStoredDesktopAuthPath(profileHome, selectedAuthKey);
        if (!string.IsNullOrWhiteSpace(selectedAuthHome))
        {
            var accountAuthPath = Path.Combine(selectedAuthHome, AuthFileName);
            if (!ChatGptAuthAccountsMatch(sharedAuthPath, accountAuthPath) &&
                !ChatGptAuthAccountsMatch(sharedAuthPath, storedAuthPath))
            {
                // The user may have signed into a different ChatGPT account inside Codex.
                // Never save that live token under the bound OAuth owner's key.
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(storedAuthPath)!);
        CopyFileAtomically(sharedAuthPath, storedAuthPath);
    }

    private static void PersistGlobalDesktopChatGptAuth(string profileHome, string sharedAuthPath)
    {
        if (!IsChatGptDesktopAuthJson(sharedAuthPath))
        {
            return;
        }

        var storedAuthPath = GetGlobalStoredDesktopAuthPath(profileHome);
        Directory.CreateDirectory(Path.GetDirectoryName(storedAuthPath)!);
        CopyFileAtomically(sharedAuthPath, storedAuthPath);
    }

    private static string? FindRestorableDesktopAuthPath(
        string profileHome,
        AccountRecord account,
        string sharedAuthPath,
        bool currentDesktopAuthAvailable)
    {
        var accountPath = GetStoredDesktopAuthPath(profileHome, account);
        if (IsChatGptDesktopAuthJson(accountPath))
        {
            return accountPath;
        }

        // Keep a valid live OAuth file if the user has just completed the browser login;
        // the global copy below is a fallback for a restart or a previous account switch.
        if (currentDesktopAuthAvailable && IsChatGptDesktopAuthJson(sharedAuthPath))
        {
            return sharedAuthPath;
        }

        var globalPath = GetGlobalStoredDesktopAuthPath(profileHome);
        if (IsChatGptDesktopAuthJson(globalPath))
        {
            return globalPath;
        }

        // Migrate/repair older per-account stores and select a deterministic newest valid
        // snapshot.  Only direct account directories are considered; arbitrary files under
        // the profile are never treated as credentials.
        var storeRoot = Path.Combine(profileHome, DesktopAuthStoreDirectoryName);
        if (!Directory.Exists(storeRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(storeRoot)
                .Where(path => !Path.GetFileName(path).Equals(
                    DesktopGlobalAuthDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.Combine(path, DesktopAuthStoreFileName))
                .Where(IsChatGptDesktopAuthJson)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DeleteMatchingGlobalDesktopAuth(
        string profileHome,
        AccountRecord account,
        bool sharedAuthSelected)
    {
        if (!account.IsOfficialOAuth)
        {
            return;
        }

        var globalAuthPath = GetGlobalStoredDesktopAuthPath(profileHome);
        if (!IsChatGptDesktopAuthJson(globalAuthPath))
        {
            return;
        }

        var identityCandidates = new List<string>
        {
            Path.Combine(account.CodexHome, AuthFileName),
            GetStoredDesktopAuthPath(profileHome, account)
        };
        if (sharedAuthSelected)
        {
            identityCandidates.Add(Path.Combine(profileHome, AuthFileName));
        }

        if (!identityCandidates.Any(path =>
                ChatGptAuthAccountsMatch(globalAuthPath, path)))
        {
            return;
        }

        ClearReadOnlyAttribute(globalAuthPath);
        File.Delete(globalAuthPath);
        DeleteDirectoryIfEmpty(Path.GetDirectoryName(globalAuthPath)!);
    }

    private static void ScrubAccountSwitcherBackups(
        string profileHome,
        AccountRecord account)
    {
        var backupRoot = Path.Combine(profileHome, "account-switcher-backups");
        if (!Directory.Exists(backupRoot))
        {
            return;
        }

        var accountKey = GetDesktopAccountKey(account);
        var sourceAuthPath = Path.Combine(account.CodexHome, AuthFileName);
        var storedAuthPath = GetStoredDesktopAuthPath(profileHome, account);
        string? modelSecret = null;
        if (!account.IsOfficialOAuth)
        {
            try
            {
                modelSecret = ReadAccessTokenCredential(sourceAuthPath);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or FormatException)
            {
                // Metadata ownership below still removes manager-generated backups. The
                // credential comparison fallback is unavailable for an already-corrupt source.
            }
        }

        foreach (var backupDirectory in Directory.EnumerateDirectories(backupRoot))
        {
            var hasSelection = TryReadDesktopSelectionKeys(
                backupDirectory,
                out var selectedModelKey,
                out var selectedAuthKey,
                out _);
            var hasActiveState = TryReadActiveAccountState(
                backupDirectory,
                out var activeAccountKey,
                out _,
                out _);
            var modelOwned =
                (hasSelection && selectedModelKey.Equals(
                    accountKey,
                    StringComparison.OrdinalIgnoreCase)) ||
                (hasActiveState && activeAccountKey.Equals(
                    accountKey,
                    StringComparison.OrdinalIgnoreCase));
            var authOwned = hasSelection
                ? selectedAuthKey.Equals(accountKey, StringComparison.OrdinalIgnoreCase)
                : modelOwned;

            var backupAuthPath = Path.Combine(backupDirectory, AuthFileName);
            var authMatches = File.Exists(backupAuthPath) &&
                              (authOwned ||
                               (account.IsOfficialOAuth
                                   ? ChatGptAuthAccountsMatch(backupAuthPath, sourceAuthPath) ||
                                     ChatGptAuthAccountsMatch(backupAuthPath, storedAuthPath)
                                   : !string.IsNullOrWhiteSpace(modelSecret) &&
                                     IsAccessTokenDesktopAuthSelected(
                                       sourceAuthPath,
                                       backupAuthPath)));
            if (authMatches)
            {
                ClearReadOnlyAttribute(backupAuthPath);
                File.Delete(backupAuthPath);
            }

            var backupConfigPath = Path.Combine(backupDirectory, ConfigFileName);
            if (modelOwned)
            {
                RemoveExperimentalBearerTokenLines(
                    backupConfigPath,
                    matchingSecret: null);
            }
            else if (!string.IsNullOrWhiteSpace(modelSecret))
            {
                RemoveExperimentalBearerTokenLines(backupConfigPath, modelSecret);
            }

            if (hasSelection && (modelOwned || authOwned))
            {
                DeleteFileIfPresent(Path.Combine(
                    backupDirectory,
                    DesktopSelectionFileName));
            }
            if (hasActiveState && modelOwned)
            {
                DeleteFileIfPresent(Path.Combine(
                    backupDirectory,
                    ActiveAccountStateFileName));
            }

            DeleteDirectoryIfEmpty(backupDirectory);
        }

        DeleteDirectoryIfEmpty(backupRoot);
    }

    private static void RemoveExperimentalBearerTokenLines(
        string configPath,
        string? matchingSecret)
    {
        if (!File.Exists(configPath))
        {
            return;
        }

        var original = File.ReadAllText(configPath);
        var normalized = original.Replace("\r\n", "\n").Replace('\r', '\n');
        var endsWithNewLine = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (endsWithNewLine && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var expectedValue = string.IsNullOrWhiteSpace(matchingSecret)
            ? null
            : TomlString(matchingSecret.Trim());
        var changed = false;
        var filtered = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!IsExperimentalBearerTokenLine(trimmed))
            {
                filtered.Add(line);
                continue;
            }

            var equalsIndex = trimmed.IndexOf('=');
            var value = equalsIndex >= 0
                ? trimmed[(equalsIndex + 1)..].Trim()
                : "";
            if (expectedValue != null &&
                !value.Equals(expectedValue, StringComparison.Ordinal))
            {
                filtered.Add(line);
                continue;
            }

            changed = true;
        }

        if (!changed)
        {
            return;
        }

        var newLine = original.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
        var updated = string.Join(newLine, filtered);
        if (endsWithNewLine && filtered.Count > 0)
        {
            updated += newLine;
        }
        WriteTextAtomically(configPath, updated);
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        ClearReadOnlyAttribute(path);
        File.Delete(path);
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static void DeleteStoredDesktopAuth(string profileHome, AccountRecord account)
    {
        var storedAuthPath = GetStoredDesktopAuthPath(profileHome, account);
        if (File.Exists(storedAuthPath))
        {
            ClearReadOnlyAttribute(storedAuthPath);
            File.Delete(storedAuthPath);
        }

        var accountDirectory = Path.GetDirectoryName(storedAuthPath)!;
        DeleteDirectoryIfEmpty(accountDirectory);
    }

    internal static bool IsOfficialOAuthCredentialFile(string path)
    {
        return IsChatGptDesktopAuthJson(path);
    }

    private static bool IsChatGptDesktopAuthJson(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            _ = BuildCanonicalJsonSha256(path);
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object ||
                !TryReadJsonString(tokens, "id_token", out _) ||
                !TryReadJsonString(tokens, "access_token", out _) ||
                !TryReadJsonString(tokens, "refresh_token", out _))
            {
                return false;
            }

            return !root.TryGetProperty("auth_mode", out var authMode) ||
                   authMode.ValueKind == JsonValueKind.Null ||
                   authMode.ValueKind == JsonValueKind.String &&
                   string.Equals(authMode.GetString(), ChatGptAuthMode, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool TryReadChatGptAuthAccountId(string path, out string accountId)
    {
        accountId = "";
        if (!IsChatGptDesktopAuthJson(path))
        {
            return false;
        }

        try
        {
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object ||
                !TryReadJsonString(tokens, "account_id", out var value) ||
                !IsSafeChatGptAccountId(value))
            {
                return false;
            }

            accountId = value;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool ChatGptAuthAccountsMatch(string firstPath, string secondPath)
    {
        return TryReadChatGptAuthAccountId(firstPath, out var firstAccountId) &&
               TryReadChatGptAuthAccountId(secondPath, out var secondAccountId) &&
               firstAccountId.Equals(secondAccountId, StringComparison.Ordinal);
    }

    private static bool IsAccessTokenDesktopAuthSelected(
        string sourceAuthPath,
        string targetAuthPath)
    {
        if (!File.Exists(sourceAuthPath) || !File.Exists(targetAuthPath))
        {
            return false;
        }

        try
        {
            var sourceToken = ReadAccessTokenCredential(sourceAuthPath);
            _ = BuildCanonicalJsonSha256(targetAuthPath);
            using var input = new FileStream(
                targetAuthPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryReadJsonString(root, "OPENAI_API_KEY", out var desktopApiKey))
            {
                return false;
            }

            // Never treat a ChatGPT-shaped auth file as the API-key projection.  The official
            // App writes auth_mode=apikey after a successful manual key login, so accept that
            // canonical marker (or a legacy missing marker) while still rejecting OAuth/PAT
            // source-only fields in the shared desktop credential.
            foreach (var propertyName in new[] { "personal_access_token", "tokens" })
            {
                if (root.TryGetProperty(propertyName, out var property) &&
                    property.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }

            if (root.TryGetProperty("auth_mode", out var authMode) &&
                authMode.ValueKind != JsonValueKind.Null &&
                (authMode.ValueKind != JsonValueKind.String ||
                 !string.Equals(
                     authMode.GetString(),
                     ApiKeyAuthMode,
                     StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return SecretValuesEqual(sourceToken, desktopApiKey);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildAccessTokenDesktopAuthText(string sourceAuthPath)
    {
        var auth = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_mode"] = ApiKeyAuthMode,
            ["OPENAI_API_KEY"] = ReadAccessTokenCredential(sourceAuthPath)
        };
        return JsonSerializer.Serialize(auth, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static string ReadAccessTokenCredential(string authPath)
    {
        if (!File.Exists(authPath))
        {
            throw new FileNotFoundException("Access Token auth.json was not found.", authPath);
        }

        // Canonicalization also rejects duplicate JSON keys before any credential is used.
        _ = BuildCanonicalJsonSha256(authPath);
        using var input = new FileStream(
            authPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Access Token auth.json must contain a JSON object.");
        }

        if (TryReadJsonString(root, "personal_access_token", out var personalAccessToken))
        {
            return personalAccessToken;
        }

        if (root.TryGetProperty("tokens", out var tokens) &&
            tokens.ValueKind == JsonValueKind.Object &&
            TryReadJsonString(tokens, "access_token", out var nestedAccessToken))
        {
            return nestedAccessToken;
        }

        if (TryReadJsonString(root, "OPENAI_API_KEY", out var apiKey))
        {
            return apiKey;
        }

        throw new InvalidDataException(
            "Access Token auth.json does not contain a usable access token.");
    }

    private static QuotaTestCredential ReadQuotaTestCredential(
        AccountRecord account,
        string authPath)
    {
        ArgumentNullException.ThrowIfNull(account);
        _ = BuildCanonicalJsonSha256(authPath);
        using var input = new FileStream(
            authPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("账号 auth.json 必须包含 JSON 对象。");
        }

        if (account.IsOfficialOAuth)
        {
            if (!IsChatGptDesktopAuthJson(authPath) ||
                !root.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object ||
                !TryReadJsonString(tokens, "access_token", out var oauthToken) ||
                !TryReadJsonString(tokens, "account_id", out var accountId) ||
                !IsSafeChatGptAccountId(accountId))
            {
                throw new InvalidDataException(
                    $"账号 {account.Name} 没有完整的官方 OAuth 凭据或工作区标识，请重新登录该账号。");
            }
            if (IsJwtExpiredOrExpiring(oauthToken, TimeSpan.FromMinutes(1)))
            {
                throw new InvalidDataException(
                    $"账号 {account.Name} 的 OAuth access token 已过期或即将过期。" +
                    "为避免误用其他账号凭据，本次未发送测试；请在“状态与凭据”中重新登录该账号后再试。");
            }
            return new QuotaTestCredential(oauthToken, accountId);
        }

        if (!account.IsAccessToken || root.TryGetProperty("tokens", out _))
        {
            throw new InvalidDataException(
                $"账号 {account.Name} 的凭据类型与 Access Token 账号不匹配，本次未发送测试。");
        }
        var hasPersonalAccessToken =
            TryReadJsonString(root, "personal_access_token", out var pat);
        if (!hasPersonalAccessToken)
        {
            _ = TryReadJsonString(root, "OPENAI_API_KEY", out pat);
        }
        if (GetAccessTokenInputError(pat) is { } patError)
        {
            throw new InvalidDataException($"账号 {account.Name} 的 PAT 无效：{patError}");
        }
        return new QuotaTestCredential(pat, null);
    }

    private static bool IsSafeChatGptAccountId(string value) =>
        value.Length <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static bool IsJwtExpiredOrExpiring(string token, TimeSpan margin)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }
            var payload = DecodeBase64Url(parts[1]);
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("exp", out var exp) &&
                   exp.TryGetInt64(out var unix) &&
                   DateTimeOffset.FromUnixTimeSeconds(unix) <= DateTimeOffset.UtcNow + margin;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryReadJsonString(
        JsonElement objectElement,
        string propertyName,
        out string value)
    {
        value = "";
        if (!objectElement.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadJsonString(
        JsonObject objectNode,
        string propertyName,
        out string value)
    {
        value = "";
        if (objectNode[propertyName] is not JsonValue property ||
            !property.TryGetValue<string>(out var text))
        {
            return false;
        }

        value = text.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool SecretValuesEqual(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static bool AuthJsonFilesSemanticallyEqual(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath) || !File.Exists(targetPath))
        {
            return false;
        }

        try
        {
            var sourceFingerprint = BuildCanonicalJsonSha256(sourcePath);
            var targetFingerprint = BuildCanonicalJsonSha256(targetPath);
            return sourceFingerprint.Length == targetFingerprint.Length &&
                   CryptographicOperations.FixedTimeEquals(sourceFingerprint, targetFingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Invalid or unreadable auth JSON must use the existing guarded backup/copy path.
            return false;
        }
    }

    private static byte[] BuildCanonicalJsonSha256(string path)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(input);
        using var normalized = new MemoryStream();
        using (var writer = new Utf8JsonWriter(normalized))
        {
            WriteCanonicalJson(writer, document.RootElement);
            writer.Flush();
        }

        return SHA256.HashData(normalized.GetBuffer().AsSpan(0, checked((int)normalized.Length)));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                writer.WriteStartObject();
                var properties = element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                for (var index = 1; index < properties.Length; index++)
                {
                    if (properties[index - 1].Name.Equals(properties[index].Name, StringComparison.Ordinal))
                    {
                        throw new JsonException(
                            $"Duplicate JSON property is not allowed in an auth file: {properties[index].Name}");
                    }
                }

                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                return;
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return;
            case JsonValueKind.Number:
                WriteCanonicalJsonNumber(writer, element);
                return;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;
            default:
                throw new JsonException($"Unsupported JSON value kind in auth file: {element.ValueKind}");
        }
    }

    private static void WriteCanonicalJsonNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetInt64(out var signedInteger))
        {
            writer.WriteNumberValue(signedInteger);
            return;
        }

        if (element.TryGetUInt64(out var unsignedInteger))
        {
            writer.WriteNumberValue(unsignedInteger);
            return;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            writer.WriteRawValue(
                decimalValue.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                skipInputValidation: true);
            return;
        }

        if (element.TryGetDouble(out var doubleValue) && double.IsFinite(doubleValue))
        {
            writer.WriteNumberValue(doubleValue);
            return;
        }

        throw new JsonException("Auth JSON contains a number that cannot be normalized safely.");
    }

    private static void CopyFileAtomically(string sourcePath, string targetPath)
    {
        var tempTarget = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(sourcePath, tempTarget, true);
            ClearReadOnlyAttribute(targetPath);
            File.Move(tempTarget, targetPath, true);
        }
        finally
        {
            if (File.Exists(tempTarget))
            {
                ClearReadOnlyAttribute(tempTarget);
                File.Delete(tempTarget);
            }
        }
    }

    private static void WriteTextAtomically(string targetPath, string contents)
    {
        var tempTarget = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempTarget, contents);
            ClearReadOnlyAttribute(targetPath);
            File.Move(tempTarget, targetPath, true);
        }
        finally
        {
            if (File.Exists(tempTarget))
            {
                ClearReadOnlyAttribute(tempTarget);
                File.Delete(tempTarget);
            }
        }
    }

    private static void WriteBytesAtomically(string targetPath, ReadOnlySpan<byte> contents)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempTarget = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempTarget, contents);
            ClearReadOnlyAttribute(targetPath);
            File.Move(tempTarget, targetPath, true);
        }
        finally
        {
            if (File.Exists(tempTarget))
            {
                ClearReadOnlyAttribute(tempTarget);
                File.Delete(tempTarget);
            }
        }
    }

    private static void RestoreFile(string targetPath, string? backupPath, bool existedBefore)
    {
        if (existedBefore && !string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
        {
            ClearReadOnlyAttribute(targetPath);
            File.Copy(backupPath, targetPath, true);
            return;
        }

        if (!existedBefore && File.Exists(targetPath))
        {
            ClearReadOnlyAttribute(targetPath);
            File.Delete(targetPath);
        }
    }

    private static void RestoreSqliteDatabase(
        string targetPath,
        string? backupPath,
        bool existedBefore)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = targetPath + suffix;
            if (File.Exists(sidecar))
            {
                ClearReadOnlyAttribute(sidecar);
                File.Delete(sidecar);
            }
        }

        RestoreFile(targetPath, backupPath, existedBefore);
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) == 0)
        {
            return;
        }

        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ToSingleQuoted(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private enum AccessTokenSharedProfileMode
    {
        ApiCompatible,
        ChatGptDesktop
    }

    private enum WindowsClientSurfaceState
    {
        Unknown,
        Blank,
        Rendered
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessEntry
    {
        public uint Size;
        public uint UsageCount;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int outputBufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ipVersion,
        int tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out NativeRect clientRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(
        IntPtr windowHandle,
        IntPtr targetDeviceContext,
        uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(
        IntPtr deviceContext,
        int width,
        int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destinationDeviceContext,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr sourceDeviceContext,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr deviceContext, int x, int y);

    [Flags]
    private enum ApplicationActivationOptions : uint
    {
        None = 0
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ApplicationActivationOptions options,
            out uint processId);
    }

    [ComImport]
    [Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")]
    private sealed class ApplicationActivationManager
    {
    }

    private sealed class DeviceAuthorizationOutputCollector
    {
        private readonly object _gate = new();
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly TaskCompletionSource<ParsedDeviceAuthorization> _authorization =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ParsedDeviceAuthorization> Authorization => _authorization.Task;

        public void Append(string line, bool isError)
        {
            lock (_gate)
            {
                var target = isError ? _stderr : _stdout;
                target.AppendLine(line);
                if (_authorization.Task.IsCompleted)
                {
                    return;
                }

                var combined = _stdout.ToString() + Environment.NewLine + _stderr;
                if (TryParseOfficialDeviceAuthorization(
                        combined,
                        out var verificationUrl,
                        out var userCode))
                {
                    _authorization.TrySetResult(new ParsedDeviceAuthorization(
                        verificationUrl,
                        userCode));
                }
            }
        }

        public string GetSanitizedStdOut()
        {
            lock (_gate)
            {
                return MaskSensitive(_stdout.ToString());
            }
        }

        public string GetSanitizedStdErr()
        {
            lock (_gate)
            {
                return MaskSensitive(_stderr.ToString());
            }
        }

        public string GetSanitizedCombinedOutput()
        {
            lock (_gate)
            {
                return MaskSensitive(
                    _stdout.ToString() + Environment.NewLine + _stderr);
            }
        }
    }

    private sealed class DeviceAuthBrowserSession : IAsyncDisposable
    {
        private readonly Process? _process;
        private readonly string? _profileDirectory;

        private DeviceAuthBrowserSession(
            Process? process,
            string? profileDirectory,
            bool started,
            string browserDisplayName,
            string startNotice)
        {
            _process = process;
            _profileDirectory = profileDirectory;
            Started = started;
            BrowserDisplayName = browserDisplayName;
            StartNotice = startNotice;
        }

        public bool Started { get; }
        public string BrowserDisplayName { get; }
        public string StartNotice { get; }

        public static DeviceAuthBrowserSession NotStarted(string notice)
        {
            return new DeviceAuthBrowserSession(
                null,
                null,
                false,
                "未自动打开浏览器",
                notice);
        }

        public static DeviceAuthBrowserSession StartedSession(
            Process process,
            string profileDirectory,
            string browserDisplayName,
            string notice)
        {
            return new DeviceAuthBrowserSession(
                process,
                profileDirectory,
                true,
                browserDisplayName,
                notice);
        }

        public async ValueTask DisposeAsync()
        {
            if (_process != null)
            {
                try
                {
                    if (!HasProcessExited(_process))
                    {
                        try
                        {
                            _ = _process.CloseMainWindow();
                            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                        }
                        catch
                        {
                            // The one-time browser may not expose a closeable top-level window.
                        }
                    }

                    if (!HasProcessExited(_process))
                    {
                        // The browser was launched with a unique user-data directory, so this
                        // exact process tree belongs only to this authorization attempt.
                        _process.Kill(entireProcessTree: true);
                    }

                    if (!HasProcessExited(_process))
                    {
                        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                catch
                {
                    // Profile deletion below is still attempted without enumerating browsers.
                }
                finally
                {
                    _process.Dispose();
                }
            }

            TryDeleteOwnedDeviceAuthBrowserProfile(_profileDirectory);
        }
    }

    private sealed record ParsedDeviceAuthorization(
        string VerificationUrl,
        string UserCode);
    private sealed record DeviceAuthBrowserExecutable(
        string ExecutablePath,
        string DisplayName,
        DeviceAuthBrowserKind Kind);

    private sealed record CommandResult(int ExitCode, string StdOut, string StdErr);
    private sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);
    private enum OfficialCodexLaunchAttemptOutcome
    {
        Ready,
        ReadyWithoutRendererPatch,
        RecoverableFailure,
        Superseded
    }

    private enum OfficialCodexRecoveryDecision
    {
        Complete,
        RestartWithoutRendererPatch,
        Stop
    }

    private enum OfficialCodexStartupReadinessPolicy
    {
        VerifySynchronouslyForRendererPatch,
        PreserveActivationAndObserveInBackground
    }

    private enum OfficialCodexNativeFastAttachOutcome
    {
        AlreadyPatched,
        PatchedAfterThisCall,
        NoReload,
        ReloadMayHaveStarted
    }

    private enum OfficialCodexMainPageWaitOutcome
    {
        Ready,
        PersistedAtomSyncFailed,
        ProcessExited,
        TimedOut,
        Superseded,
        ProbeUnavailable
    }

    private sealed record WindowsClientProcessSnapshot(
        int ProcessId,
        long StartTimeUtcTicks,
        string ProcessName,
        string ExecutablePath,
        int ShutdownPriority,
        int ParentProcessId);
    private sealed record WindowsClientSharedProfileSnapshot(
        IReadOnlyList<WindowsClientSharedProfileFileSnapshot> Files);
    private sealed record WindowsClientSharedProfileFileSnapshot(
        string Path,
        bool Existed,
        byte[] Contents);
    private sealed record WindowsClientActivationIdentity(
        int ProcessId,
        long? StartTimeUtcTicks);
    private sealed class OfficialCodexActivationIdentityException : InvalidOperationException
    {
        internal OfficialCodexActivationIdentityException(int processId, bool cleanupSucceeded)
            : base(
                $"Windows activated official Codex as PID {processId}, but its immutable start-time " +
                "identity could not be captured within 5 seconds. " +
                (cleanupSucceeded
                    ? "The untracked packaged process was removed before launch recovery continued."
                    : "The untracked packaged process could not be removed safely; no second activation was attempted."))
        {
            CleanupSucceeded = cleanupSucceeded;
        }

        internal bool CleanupSucceeded { get; }
    }
    private sealed record CompatibleApiPreflightCacheEntry(
        string Fingerprint,
        DateTimeOffset CompletedAtUtc);
    private sealed record AccessTokenSwitchValidationCacheEntry(
        string Fingerprint,
        DateTimeOffset CompletedAtUtc,
        LoginStatus Status);
    private sealed record QuotaTestCredential(string Token, string? AccountId);
}

public sealed record ChatGptDeviceAuthorization(
    string VerificationUrl,
    string UserCode,
    bool BrowserOpened,
    string BrowserName,
    string BrowserNotice);

public sealed record ChatGptOAuthAuthorization(string LoginUrl);

internal enum DeviceAuthBrowserKind
{
    Edge,
    Chrome
}

public sealed class WindowsClientAccountProjection
{
    public LoginStatus Status { get; init; } = new();
    public string DefaultCodexHome { get; init; } = "";
    public string AccountCodexHome { get; init; } = "";
    public string BackupDirectory { get; init; } = "";
    public string? AuthBackupPath { get; init; }
    public string? CockpitAuthBackupPath { get; init; }
    public string? ConfigBackupPath { get; init; }
    public string? DesktopSelectionBackupPath { get; init; }
    public string? ActiveAccountStateBackupPath { get; init; }
    public bool AuthExisted { get; init; }
    public bool CockpitAuthExisted { get; init; }
    public bool ConfigExisted { get; init; }
    public bool DesktopSelectionExisted { get; init; }
    public bool ActiveAccountStateExisted { get; init; }
    public bool SharedCredentialsReused { get; init; }
    public bool ProfileChanged { get; set; }
    public bool DesktopLoginRequired { get; init; }
    public WindowsClientMode ClientMode { get; set; } = WindowsClientMode.CodexPlusPlus;
    public bool ClientLaunchStarted { get; set; }
    public string? ClientLaunchError { get; set; }
    public bool FailedLaunchProfileRestored { get; set; }
    public bool PreviousClientRelaunchStarted { get; set; }
    public string? FailedLaunchRecoveryError { get; set; }
    public bool CodexPlusPlusLaunchStarted { get; set; }
    public bool CodexDreamSkinFailed { get; set; }
    public bool CodexOfficialAppearanceRestored { get; set; }
    public string? GlobalStatePath { get; set; }
    public string? GlobalStateBackupPath { get; set; }
    public bool GlobalStateExisted { get; set; }
    public bool SidebarStateWasNormalized { get; set; }
    public string? StateDatabaseBackupPath { get; set; }
    public string? ModelCacheBackupPath { get; set; }
    public bool StateDatabaseExisted { get; set; }
    public bool ModelCacheExisted { get; set; }
    public int ThreadRowsUpdated { get; set; }
    public string? ProjectConfigPath { get; set; }
    public string? ProjectConfigBackupPath { get; set; }
    public bool ProjectConfigExisted { get; set; }
    public bool ProjectConfigWasSanitized { get; set; }
}
