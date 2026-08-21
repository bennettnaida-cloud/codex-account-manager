using System.Security.Cryptography;
using System.Text.Json;

namespace CodexAccountManager;

public sealed class AccountStore
{
    internal const string ManagedProviderId = "codex_account_manager";
    internal const string CompatibleApiProviderId = ManagedProviderId;
    internal const string AccessTokenProviderId = ManagedProviderId;
    internal const string LegacyCompatibleApiProviderId = "codex_compatible_api";
    internal const string LegacyAccessTokenProviderId = "codex_token_http";
    internal const string AccessTokenProviderName = "OpenAI Token HTTP";
    internal const string AccessTokenBaseUrl = LocalPatGateway.ProviderBaseUrl;
    internal const string OfficialOAuthProviderId = "codex_official_https";
    internal const string OfficialOAuthProviderName = "OpenAI";
    internal const string OfficialOAuthBaseUrl = "https://chatgpt.com/backend-api/codex";
    internal const string OfficialOAuthDesktopLocale = "zh-CN";

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public AccountStore()
        : this(LocateRootPath())
    {
    }

    internal AccountStore(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        AccountsPath = Path.Combine(RootPath, "accounts.json");
        TokenMetadataPath = Path.Combine(RootPath, "token-metadata.json");
    }

    public string RootPath { get; }
    public string AccountsPath { get; }
    public string TokenMetadataPath { get; }

    public List<AccountRecord> LoadAccounts()
    {
        if (!File.Exists(AccountsPath))
        {
            return [];
        }

        var json = File.ReadAllText(AccountsPath);
        var accounts = JsonSerializer.Deserialize<List<AccountRecord>>(json) ?? [];
        foreach (var account in accounts)
        {
            NormalizeAccount(account);
        }

        return accounts;
    }

    public void SaveAccounts(IEnumerable<AccountRecord> accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AccountsPath)!);
        var ordered = accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        File.WriteAllText(AccountsPath, JsonSerializer.Serialize(ordered, _jsonOptions));
    }

    public Dictionary<string, TokenMetadata> LoadTokenMetadata()
    {
        if (!File.Exists(TokenMetadataPath))
        {
            return new Dictionary<string, TokenMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(TokenMetadataPath);
            return JsonSerializer.Deserialize<Dictionary<string, TokenMetadata>>(json) ??
                   new Dictionary<string, TokenMetadata>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, TokenMetadata>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveTokenMetadata(Dictionary<string, TokenMetadata> metadata)
    {
        File.WriteAllText(TokenMetadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
    }

    public void SaveAccount(string name, string codexHome, string? originalName)
    {
        SaveAccount(new AccountRecord { Name = name, CodexHome = codexHome }, originalName, null);
    }

    public void SaveAccount(
        AccountRecord account,
        string? originalName,
        string? apiKey,
        string? officialOAuthCredentialSourcePath = null)
    {
        NormalizeAccount(account);
        account.Name = account.Name.Trim();
        account.CodexHome = account.CodexHome.Trim();

        if (string.IsNullOrWhiteSpace(account.Name))
        {
            throw new InvalidOperationException("账号名称不能为空。");
        }
        if (string.IsNullOrWhiteSpace(account.CodexHome))
        {
            throw new InvalidOperationException("CODEX_HOME 不能为空。");
        }
        if (account.IsCompatibleApi && string.IsNullOrWhiteSpace(account.ApiBaseUrl))
        {
            throw new InvalidOperationException("兼容 API 地址不能为空。");
        }
        if (account.IsCompatibleApi &&
            CodexCliService.GetCompatibleApiModelIdValidationError(account.ApiModel) is { } modelError)
        {
            throw new InvalidOperationException(modelError);
        }
        if (PathsEqual(account.CodexHome, CodexCliService.GetDefaultCodexHome()))
        {
            throw new InvalidOperationException(
                "账号凭据目录不能直接使用共享的默认 .codex。请为每个账号选择独立目录；聊天记录仍会统一保存在默认 .codex。");
        }

        var accounts = LoadAccounts();
        var target = string.IsNullOrWhiteSpace(originalName) ? account.Name : originalName.Trim();
        var conflictingName = accounts.FirstOrDefault(existing =>
            existing.Name.Equals(account.Name, StringComparison.OrdinalIgnoreCase) &&
            !existing.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (conflictingName != null)
        {
            throw new InvalidOperationException($"账号名称 {account.Name} 已存在。请使用不同的名称，或编辑现有账号。");
        }

        var duplicateHome = accounts.FirstOrDefault(existing =>
            !existing.Name.Equals(target, StringComparison.OrdinalIgnoreCase) &&
            PathsEqual(existing.CodexHome, account.CodexHome));
        if (duplicateHome != null)
        {
            throw new InvalidOperationException(
                $"账号 {duplicateHome.Name} 已使用这个 CODEX_HOME。每个账号必须使用独立凭据目录；聊天记录仍会统一写入默认 .codex。");
        }

        byte[]? previousOAuthCredential = null;
        string? committedOAuthPath = null;
        var previousOAuthCredentialExisted = false;
        try
        {
            EnsureCodexHome(account, apiKey);
            if (!string.IsNullOrWhiteSpace(officialOAuthCredentialSourcePath))
            {
                if (!account.IsOfficialOAuth ||
                    !CodexCliService.IsOfficialOAuthCredentialFile(officialOAuthCredentialSourcePath))
                {
                    throw new InvalidOperationException(
                        "只有已验证成功的 ChatGPT 官方 OAuth 凭据才能随账号一起保存。"
                    );
                }

                committedOAuthPath = Path.Combine(account.CodexHome, "auth.json");
                previousOAuthCredentialExisted = File.Exists(committedOAuthPath);
                if (previousOAuthCredentialExisted)
                {
                    previousOAuthCredential = File.ReadAllBytes(committedOAuthPath);
                }
                CopyFileAtomically(officialOAuthCredentialSourcePath, committedOAuthPath);
            }

            accounts.RemoveAll(a =>
                a.Name.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals(account.Name, StringComparison.OrdinalIgnoreCase));
            accounts.Add(account);
            SaveAccounts(accounts);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(committedOAuthPath))
            {
                try
                {
                    if (previousOAuthCredentialExisted && previousOAuthCredential != null)
                    {
                        WriteBytesAtomically(committedOAuthPath, previousOAuthCredential);
                    }
                    else if (File.Exists(committedOAuthPath))
                    {
                        File.Delete(committedOAuthPath);
                    }
                }
                catch
                {
                    // Preserve the original save error. The existing account manifest remains authoritative.
                }
            }
            throw;
        }
        finally
        {
            if (previousOAuthCredential != null)
            {
                CryptographicOperations.ZeroMemory(previousOAuthCredential);
            }
        }
        if (!string.IsNullOrWhiteSpace(originalName) &&
            !originalName.Trim().Equals(account.Name, StringComparison.OrdinalIgnoreCase))
        {
            RenameTokenMetadata(originalName.Trim(), account.Name);
        }
    }

    private static void CopyFileAtomically(string sourcePath, string targetPath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        try
        {
            WriteBytesAtomically(targetPath, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void WriteBytesAtomically(string targetPath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(targetPath) ??
                        throw new InvalidOperationException("OAuth 凭据目标目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            ".auth.json.pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void RenameTokenMetadata(string originalName, string newName)
    {
        var metadata = LoadTokenMetadata();
        if (!metadata.TryGetValue(originalName, out var entry))
        {
            return;
        }

        metadata.Remove(originalName);
        metadata[newName] = entry;
        SaveTokenMetadata(metadata);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(left))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(right))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void RemoveAccount(string name)
    {
        var accounts = LoadAccounts();
        accounts.RemoveAll(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        SaveAccounts(accounts);
    }

    public void DeleteAccount(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var accounts = LoadAccounts();
        var storedAccount = accounts.FirstOrDefault(candidate =>
            candidate.Name.Equals(account.Name, StringComparison.OrdinalIgnoreCase));
        if (storedAccount == null)
        {
            throw new InvalidOperationException($"账号 {account.Name} 不存在，未删除任何文件。");
        }

        var otherHomes = accounts
            .Where(candidate => !candidate.Name.Equals(storedAccount.Name, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.CodexHome)
            .ToList();
        DeleteCredentialDirectory(storedAccount.CodexHome, RootPath, otherHomes);

        accounts.RemoveAll(candidate =>
            candidate.Name.Equals(storedAccount.Name, StringComparison.OrdinalIgnoreCase));
        SaveAccounts(accounts);

        var metadata = LoadTokenMetadata();
        if (metadata.Remove(storedAccount.Name))
        {
            SaveTokenMetadata(metadata);
        }
    }

    private static void DeleteCredentialDirectory(
        string codexHome,
        string managerRoot,
        IReadOnlyCollection<string> otherAccountHomes)
    {
        var target = NormalizeDeletionPath(codexHome, "账号凭据目录");
        var protectedPaths = new[]
        {
            NormalizeDeletionPath(managerRoot, "Account Manager 数据目录"),
            NormalizeDeletionPath(CodexCliService.GetDefaultCodexHome(), "共享 .codex 目录"),
            NormalizeDeletionPath(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "用户主目录")
        };
        var volumeRoot = Path.GetPathRoot(target);
        if (string.IsNullOrWhiteSpace(volumeRoot) || PathsEqual(target, volumeRoot))
        {
            throw new InvalidOperationException($"拒绝删除磁盘根目录：{target}");
        }

        foreach (var protectedPath in protectedPaths)
        {
            if (IsSameOrAncestor(target, protectedPath))
            {
                throw new InvalidOperationException(
                    $"账号凭据目录指向或包含受保护目录，拒绝删除：{target}");
            }
        }

        foreach (var otherHome in otherAccountHomes)
        {
            var normalizedOtherHome = NormalizeDeletionPath(otherHome, "其它账号凭据目录");
            if (IsSameOrAncestor(target, normalizedOtherHome))
            {
                throw new InvalidOperationException(
                    $"账号凭据目录还包含其它账号目录，拒绝删除：{normalizedOtherHome}");
            }
        }

        if (!Directory.Exists(target))
        {
            return;
        }

        var attributes = File.GetAttributes(target);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"账号凭据目录是符号链接或目录联接，拒绝递归删除：{target}");
        }

        try
        {
            DeleteDirectoryTreeWithoutFollowingLinks(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"无法删除账号凭据目录，账号仍保留在列表中：{target}\n{ex.Message}",
                ex);
        }
    }

    private static void DeleteDirectoryTreeWithoutFollowingLinks(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
            if (isDirectory && !isReparsePoint)
            {
                DeleteDirectoryTreeWithoutFollowingLinks(entry);
                continue;
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }

            if (isDirectory)
            {
                Directory.Delete(entry, recursive: false);
            }
            else
            {
                File.Delete(entry);
            }
        }

        var directoryAttributes = File.GetAttributes(directory);
        if ((directoryAttributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(directory, directoryAttributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(directory, recursive: false);
    }

    private static string NormalizeDeletionPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{label}为空，拒绝删除。");
        }

        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root) && PathsEqual(fullPath, root)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{label}不是有效路径，拒绝删除：{path}", ex);
        }
    }

    private static bool IsSameOrAncestor(string candidate, string path)
    {
        if (PathsEqual(candidate, path))
        {
            return true;
        }

        var prefix = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidatePermanentAccountDeletion()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-delete-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccountStore(root);
            var accountHome = Path.Combine(root, "account-home");
            Directory.CreateDirectory(accountHome);
            File.WriteAllText(Path.Combine(accountHome, "auth.json"), "{\"token\":\"test-only\"}");
            File.WriteAllText(Path.Combine(accountHome, "config.toml"), "model = \"test\"");
            var account = new AccountRecord { Name = "delete-test", CodexHome = accountHome };
            store.SaveAccounts([account]);
            store.WriteTokenMetadata(account.Name, "2099-01-01T00:00:00Z");

            store.DeleteAccount(account);
            if (Directory.Exists(accountHome) ||
                store.LoadAccounts().Count != 0 ||
                store.LoadTokenMetadata().ContainsKey(account.Name))
            {
                throw new InvalidOperationException(
                    "Permanent account deletion must remove the credential directory, manifest entry, and token metadata.");
            }

            var protectedAccount = new AccountRecord { Name = "protected-test", CodexHome = root };
            store.SaveAccounts([protectedAccount]);
            try
            {
                store.DeleteAccount(protectedAccount);
                throw new InvalidOperationException(
                    "Permanent account deletion must reject the Account Manager data directory.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("受保护目录", StringComparison.Ordinal))
            {
            }
            if (store.LoadAccounts().Count != 1)
            {
                throw new InvalidOperationException(
                    "A rejected permanent deletion must keep the account manifest entry.");
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

    public void WriteTokenMetadata(string accountName, string? expiresAtUtc)
    {
        var metadata = LoadTokenMetadata();
        metadata[accountName] = new TokenMetadata
        {
            UpdatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ExpiresAtUtc = expiresAtUtc
        };
        SaveTokenMetadata(metadata);
    }

    public string GetExpiryLabel(string accountName)
    {
        var metadata = LoadTokenMetadata();
        if (!metadata.TryGetValue(accountName, out var entry) ||
            string.IsNullOrWhiteSpace(entry.ExpiresAtUtc))
        {
            return "Unknown";
        }

        return entry.ExpiresAtUtc!;
    }

    private static void NormalizeAccount(AccountRecord account)
    {
        // authKind was absent in early manifests. Normalize all known spellings while
        // retaining the historical Access Token fallback for a missing value.
        account.AuthKind = AccountAuthKind.Normalize(account.AuthKind);
        if (string.IsNullOrWhiteSpace(account.ApiProviderName))
        {
            account.ApiProviderName = "OpenAI";
        }
        if (string.IsNullOrWhiteSpace(account.ApiModel))
        {
            account.ApiModel = "gpt-5.5";
        }
        if (string.IsNullOrWhiteSpace(account.ApiWireApi))
        {
            account.ApiWireApi = "responses";
        }
        if (account.QuotaLimitType is not (
                AccountQuotaLimitType.Monthly or
                AccountQuotaLimitType.FiveHourAndWeekly or
                AccountQuotaLimitType.WeeklyOnly or
                AccountQuotaLimitType.FiveHourOnly))
        {
            account.QuotaLimitType = AccountQuotaLimitType.Detect(
                account.QuotaPrimaryWindowMinutes,
                account.QuotaSecondaryWindowMinutes);
        }
    }

    private static void EnsureCodexHome(AccountRecord account, string? apiKey)
    {
        if (account.IsOfficialOAuth)
        {
            EnsureOfficialOAuthHome(account.CodexHome);
            return;
        }

        if (account.IsCompatibleApi)
        {
            EnsureCompatibleApiHome(account, apiKey);
            return;
        }

        EnsureAccessTokenHome(account.CodexHome);
    }

    private static void EnsureAccessTokenHome(string codexHome)
    {
        Directory.CreateDirectory(codexHome);
        var configPath = Path.Combine(codexHome, "config.toml");
        if (File.Exists(configPath))
        {
            return;
        }

        File.WriteAllText(configPath, BuildAccessTokenConfig());
    }

    private static void EnsureOfficialOAuthHome(string codexHome)
    {
        Directory.CreateDirectory(codexHome);
        // This profile deliberately stays on Codex's native ChatGPT authentication path.
        // Rewriting the small managed config also removes stale PAT/API-provider routing if
        // an existing account directory is intentionally converted to official OAuth.
        var configPath = Path.Combine(codexHome, "config.toml");
        var existingConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        var serviceTier = CodexCliService.ReadDesktopServiceTier(existingConfig);
        File.WriteAllText(configPath, BuildOfficialOAuthConfig(serviceTier));
    }

    internal static string BuildOfficialOAuthConfig(string? serviceTier = null)
    {
        var normalizedServiceTier = CodexCliService.NormalizeDesktopServiceTier(serviceTier);
        return $"""
model_provider = {TomlString(OfficialOAuthProviderId)}
cli_auth_credentials_store = "file"
forced_login_method = "chatgpt"
service_tier = {TomlString(normalizedServiceTier)}
windows_wsl_setup_acknowledged = true

approval_policy = "never"
sandbox_mode = "danger-full-access"

[model_providers.{OfficialOAuthProviderId}]
name = {TomlString(OfficialOAuthProviderName)}
base_url = {TomlString(OfficialOAuthBaseUrl)}
wire_api = "responses"
requires_openai_auth = true
supports_websockets = false

[desktop]
localeOverride = {TomlString(OfficialOAuthDesktopLocale)}

[windows]
sandbox = "unelevated"
""";
    }

    internal static string BuildAccessTokenConfig(string? serviceTier = null)
    {
        var normalizedServiceTier = CodexCliService.NormalizeDesktopServiceTier(serviceTier);
        return $"""
model = {TomlString(ModelCatalogService.CanonicalDefaultModel)}
review_model = {TomlString(ModelCatalogService.CanonicalDefaultModel)}
model_reasoning_effort = {TomlString(ModelCatalogService.DefaultReasoningEffort)}
chatgpt_base_url = {TomlString(LocalPatGateway.ChatGptBaseUrl)}
disable_response_storage = true
model_provider = {TomlString(AccessTokenProviderId)}
service_tier = {TomlString(normalizedServiceTier)}
model_auto_compact_token_limit = 1000000000
windows_wsl_setup_acknowledged = true

approval_policy = "never"
sandbox_mode = "danger-full-access"

[features]
js_repl = false
remote_compaction_v2 = false
remote_plugin = false
plugins = false

[model_providers.{AccessTokenProviderId}]
name = {TomlString(AccessTokenProviderName)}
base_url = {TomlString(AccessTokenBaseUrl)}
wire_api = "responses"
requires_openai_auth = true
supports_websockets = false
stream_max_retries = 0
request_max_retries = 1

[plugins."sites@openai-bundled"]
enabled = false

[windows]
sandbox = "unelevated"
""";
    }

    private static void EnsureCompatibleApiHome(AccountRecord account, string? apiKey)
    {
        Directory.CreateDirectory(account.CodexHome);
        var configPath = Path.Combine(account.CodexHome, "config.toml");
        var existingConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        var serviceTier = CodexCliService.ReadDesktopServiceTier(existingConfig);
        File.WriteAllText(configPath, BuildCompatibleApiConfig(account, serviceTier));

        var authPath = Path.Combine(account.CodexHome, "auth.json");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var auth = new Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = apiKey.Trim()
            };
            File.WriteAllText(authPath, JsonSerializer.Serialize(auth, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (!File.Exists(authPath))
        {
            throw new InvalidOperationException("新增兼容 API 账号时必须填写 API Key。编辑时可留空以保留原 Key。");
        }
    }

    public static string BuildCompatibleApiConfig(AccountRecord account, string? serviceTier = null)
    {
        var providerDisplayName = string.IsNullOrWhiteSpace(account.ApiProviderName)
            ? "OpenAI"
            : account.ApiProviderName.Trim();
        var providerName = TomlString(providerDisplayName);
        var providerId = TomlString(CompatibleApiProviderId);
        var baseUrl = TomlString(account.ApiBaseUrl.TrimEnd('/'));
        var model = TomlString(account.ApiModel.Trim());
        var wireApi = TomlString(account.ApiWireApi.Trim());
        var normalizedServiceTier = CodexCliService.NormalizeDesktopServiceTier(serviceTier);

        return $"""
model_provider = {providerId}
model = {model}
review_model = {model}
model_reasoning_effort = "xhigh"
disable_response_storage = true
network_access = "enabled"
service_tier = {TomlString(normalizedServiceTier)}
model_auto_compact_token_limit = 1000000000
windows_wsl_setup_acknowledged = true

[features]
remote_compaction_v2 = false
remote_plugin = false

[model_providers.{CompatibleApiProviderId}]
name = {providerName}
base_url = {baseUrl}
wire_api = {wireApi}
requires_openai_auth = false
supports_websockets = false
stream_max_retries = 0
request_max_retries = 1

[windows]
sandbox = "unelevated"
""";
    }

    internal static void ValidateOfficialOAuthAccountStorage()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-account-oauth-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacyHome = Path.Combine(root, "legacy-home");
            var legacyManifest = new[]
            {
                new Dictionary<string, string>
                {
                    ["name"] = "legacy-token",
                    ["codexHome"] = legacyHome
                }
            };
            File.WriteAllText(
                Path.Combine(root, "accounts.json"),
                JsonSerializer.Serialize(legacyManifest));

            var store = new AccountStore(root);
            var legacy = store.LoadAccounts().Single();
            if (!legacy.IsAccessToken || legacy.AuthKind != AccountAuthKind.AccessToken)
            {
                throw new InvalidOperationException(
                    "An account manifest without authKind must remain an Access Token account.");
            }

            var oauthHome = Path.Combine(root, "oauth-home");
            var oauth = new AccountRecord
            {
                Name = "oauth-test",
                CodexHome = oauthHome,
                AuthKind = "OFFICIAL_OAUTH"
            };
            store.SaveAccount(oauth, null, null);

            var configPath = Path.Combine(oauthHome, "config.toml");
            var authPath = Path.Combine(oauthHome, "auth.json");
            var config = File.ReadAllText(configPath);
            var normalizedConfig = config.Replace("\r\n", "\n").Replace('\r', '\n');
            var officialProviderHeader = "[model_providers." + OfficialOAuthProviderId + "]";
            var localeLine = "localeOverride = \"" + OfficialOAuthDesktopLocale + "\"";
            var desktopLocaleBlock = "[desktop]\n" + localeLine;
            var localeIndex = normalizedConfig.IndexOf(localeLine, StringComparison.Ordinal);
            var desktopLocaleIndex = normalizedConfig.IndexOf(desktopLocaleBlock, StringComparison.Ordinal);
            var forbiddenFragments = new[]
            {
                "chatgpt_base_url",
                "experimental_bearer_token",
                "personal_access_token",
                "OPENAI_API_KEY",
                "responses_websockets"
            };
            var storedOAuth = store.LoadAccounts().Single(account => account.Name == oauth.Name);
            if (!storedOAuth.IsOfficialOAuth ||
                storedOAuth.AuthKind != AccountAuthKind.OfficialOAuth ||
                !config.Contains(
                    "model_provider = " + TomlString(OfficialOAuthProviderId),
                    StringComparison.Ordinal) ||
                !config.Contains("cli_auth_credentials_store = \"file\"", StringComparison.Ordinal) ||
                !config.Contains("forced_login_method = \"chatgpt\"", StringComparison.Ordinal) ||
                !config.Contains(
                    officialProviderHeader,
                    StringComparison.Ordinal) ||
                config.IndexOf(officialProviderHeader, StringComparison.Ordinal) !=
                    config.LastIndexOf(officialProviderHeader, StringComparison.Ordinal) ||
                !config.Contains(
                    "base_url = " + TomlString(OfficialOAuthBaseUrl),
                    StringComparison.Ordinal) ||
                !config.Contains("wire_api = \"responses\"", StringComparison.Ordinal) ||
                !config.Contains("requires_openai_auth = true", StringComparison.Ordinal) ||
                !config.Contains("supports_websockets = false", StringComparison.Ordinal) ||
                localeIndex < 0 ||
                localeIndex != normalizedConfig.LastIndexOf(localeLine, StringComparison.Ordinal) ||
                desktopLocaleIndex < 0 ||
                desktopLocaleIndex != normalizedConfig.LastIndexOf(desktopLocaleBlock, StringComparison.Ordinal) ||
                forbiddenFragments.Any(fragment => config.Contains(fragment, StringComparison.OrdinalIgnoreCase)) ||
                File.Exists(authPath))
            {
                throw new InvalidOperationException(
                    "Official OAuth account storage must use native ChatGPT file authentication without PAT/API routing or fabricated credentials.");
            }

            // Saving an OAuth account again must not reset the native Fast/Standard choice
            // already written to that account's own config.toml.
            File.WriteAllText(configPath, BuildOfficialOAuthConfig("priority"));
            store.SaveAccount(
                new AccountRecord
                {
                    Name = oauth.Name,
                    CodexHome = oauthHome,
                    AuthKind = AccountAuthKind.OfficialOAuth
                },
                null,
                null);
            var preservedConfig = File.ReadAllText(configPath);
            if (!preservedConfig.Contains(
                    "service_tier = \"priority\"",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Saving an existing official OAuth account reset its persisted service tier.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Temporary validation cleanup must not hide the actual test result.
            }
        }
    }

    private static string TomlString(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string LocateRootPath()
    {
        var envRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            return Path.GetFullPath(envRoot);
        }

        var current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "accounts.json")))
        {
            return current;
        }

        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "accounts.json")))
            {
                return dir.FullName;
            }
        }

        return current;
    }
}
