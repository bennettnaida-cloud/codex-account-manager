using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    internal const string CodexOAuthClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    internal static bool HasRefreshableOfficialOAuthCredential(AccountRecord account)
    {
        if (!account.IsOfficialOAuth) return false;
        try
        {
            var path = Path.Combine(account.CodexHome, AuthFileName);
            _ = BuildCanonicalJsonSha256(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("tokens", out var tokens) &&
                   AccountFileImport.Text(tokens, "access_token").Length > 0 &&
                   AccountFileImport.Text(tokens, "refresh_token").Length > 0 &&
                   IsSafeChatGptAccountId(AccountFileImport.Text(tokens, "account_id")) &&
                   AccountFileImport.Text(tokens, "account_id").Length > 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
    private static string? OfficialOAuthModelCatalogPath(AccountRecord account)
    {
        if (!account.IsOfficialOAuth) return null;
        try
        {
            var path = ManagedAccessTokenModelCatalogPath(account);
            var identity = Convert.ToHexString(BuildCanonicalJsonSha256(Path.Combine(account.CodexHome, AuthFileName)));
            return ReadVerifiedAccessTokenCatalog(path, identity, out _) != null ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static async Task<JsonArray> FetchOfficialOAuthRemoteModelsAsync(HttpClient client,
        string token, string accountId, string version, CancellationToken cancellationToken)
    {
        // Existing Codex OAuth credentials use the native Codex endpoint, not SIWC/API-key auth.
        using var request = BuildAccessTokenCatalogRequest(
            "https://chatgpt.com/backend-api/codex/models?client_version=" + Uri.EscapeDataString(version), token, version);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await ReadAccessTokenCatalogResponseAsync(response, (int)ManagedAccessTokenModelCatalogMaxBytes, cancellationToken);
        if (!TryReadAccessTokenModelSource(body, out _, out var models))
            throw new InvalidDataException("官方账号返回的模型目录无效；保留旧目录并停止本次启动。");
        return models;
    }

    private static async Task EnsureOfficialOAuthLaunchCredentialAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        if (!account.IsOfficialOAuth) return;
        var gate = AccessTokenDiscoveryGates.GetOrAdd(Path.GetFullPath(account.CodexHome), _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            SyncStoredOfficialOAuthAuthToAccount(account);
            var path = Path.Combine(account.CodexHome, AuthFileName);
            var identity = BuildCanonicalJsonSha256(path);
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var tokens = root["tokens"] as JsonObject ?? throw new InvalidDataException("缺少 OAuth 凭据，请重新登录。");
            var access = tokens["access_token"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(tokens["id_token"]?.GetValue<string>()) &&
                !IsJwtExpiredOrExpiring(access, TimeSpan.FromMinutes(2))) return;
            var refresh = tokens["refresh_token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidDataException("OAuth 凭据无法刷新，请重新登录。");
            using var resolver = new AccountProxyResolver(new AccountStore().RootPath);
            var route = resolver.Resolve(AccountProxyResolver.AccountKeyFor(account));
            if (!route.Success) throw new InvalidOperationException(route.Error);
            using var client = ProxyHttpClientFactory.Create(route, resolver.GetNode(route.NodeId));
            await RefreshOfficialOAuthCredentialCoreAsync(root, client, cancellationToken);
            if (!identity.SequenceEqual(BuildCanonicalJsonSha256(path)))
                throw new InvalidOperationException("账号凭据已变化，未覆盖新的登录状态。");
            AtomicFilePersistence.WriteAllText(path, root.ToJsonString());
        }
        finally { gate.Release(); }
    }

    private static async Task RefreshOfficialOAuthCredentialCoreAsync(JsonObject root, HttpClient client,
        CancellationToken cancellationToken)
    {
            var tokens = root["tokens"]!.AsObject();
            var refresh = tokens["refresh_token"]!.GetValue<string>();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = CodexOAuthClientId })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"OAuth 刷新失败（HTTP {(int)response.StatusCode}），请重新登录该账号。");
            var bytes = await ReadResponseBodyUpToLimitAsync(response.Content, 128 * 1024, cancellationToken)
                        ?? throw new InvalidDataException("OAuth 刷新响应过大。");
            using var doc = JsonDocument.Parse(bytes);
            var newAccess = AccountFileImport.Text(doc.RootElement, "access_token");
            var newId = AccountFileImport.Text(doc.RootElement, "id_token");
            if (newAccess.Length == 0 || newId.Length == 0 && string.IsNullOrWhiteSpace(tokens["id_token"]?.GetValue<string>()))
                throw new InvalidDataException("OAuth 刷新响应缺少必要字段，请重新登录。");
            var returnedAccountId = AccountFileImport.Claim(newAccess, "chatgpt_account_id");
            var expectedAccountId = tokens["account_id"]?.GetValue<string>();
            if (returnedAccountId.Length > 0 && returnedAccountId != expectedAccountId)
                throw new InvalidDataException("OAuth 刷新返回了不同的账号身份，已停止启动。");
            tokens["access_token"] = newAccess;
            if (newId.Length > 0) tokens["id_token"] = newId;
            var newRefresh = AccountFileImport.Text(doc.RootElement, "refresh_token");
            if (newRefresh.Length > 0) tokens["refresh_token"] = newRefresh;
            root["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");
    }

    internal async Task PrepareLaunchModelCatalogAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        LaunchPhase("launch-model-discovery-start", "正在获取所选账号的最新模型，完成后启动…");
        if (account.IsCompatibleApi)
        {
            await RefreshOfficialCompatibleModelTemplatesAsync(cancellationToken);
            await EnsureCompatibleApiLaunchPreflightAsync(account, cancellationToken, requireRemoteCatalog: true);
            ProjectCompatibleApiConfig(Path.Combine(account.CodexHome, ConfigFileName), account);
        }
        else
        {
            await RefreshAccessTokenModelCatalogAsync(account, cancellationToken,
                requireConfiguredModel: false, forceRemoteRefresh: true);
            if (account.IsOfficialOAuth) EnsureOfficialOAuthAccountConfig(account);
            else ProjectAccessTokenSourceConfig(Path.Combine(account.CodexHome, ConfigFileName), account);
        }
        LaunchPhase("launch-model-discovery-complete", "最新模型已获取，正在启动 Codex…");
    }
}
