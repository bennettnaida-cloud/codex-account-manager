using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessTokenDiscoveryGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ModelDiscoveryCliVersions = new();
    private sealed record AccessTokenCatalogReceipt(string CredentialIdentity, string CatalogHash,
        DateTimeOffset FetchedAtUtc, string ClientVersion);

    private static async Task<bool> RefreshAccessTokenModelCatalogAsync(AccountRecord account,
        CancellationToken cancellationToken, bool requireConfiguredModel, bool forceRemoteRefresh)
    {
        var gate = AccessTokenDiscoveryGates.GetOrAdd(Path.GetFullPath(account.CodexHome), _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Read only this account's PAT. In particular, never borrow the shared
            // desktop's OAuth identity, environment API key or rotated gateway route.
            var authPath = Path.Combine(account.CodexHome, AuthFileName);
            var identity = Convert.ToHexString(BuildCanonicalJsonSha256(authPath));
            var token = ReadQuotaTestCredential(account, authPath).Token;
            bool IsCurrent()
            {
                try
                {
                    var matches = new AccountStore().LoadAccounts().Where(a =>
                        a.CodexHome.Equals(account.CodexHome, StringComparison.OrdinalIgnoreCase)).ToArray();
                    return matches.Length == 1 && matches[0].IsAccessToken &&
                           Convert.ToHexString(BuildCanonicalJsonSha256(authPath)) == identity;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                { return false; }
            }

            return await RefreshAccessTokenCatalogCoreAsync(account, identity, requireConfiguredModel,
                forceRemoteRefresh, IsCurrent, async ct =>
                {
                    var version = await GetModelDiscoveryCliVersionAsync(ct);
                    using var resolver = new AccountProxyResolver(new AccountStore().RootPath);
                    var route = resolver.Resolve(AccountProxyResolver.AccountKeyFor(account));
                    if (!route.Success) throw new InvalidOperationException(route.Error);
                    using var client = ProxyHttpClientFactory.Create(route, resolver.GetNode(route.NodeId));
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    try
                    {
                        return (await FetchAccessTokenRemoteModelsAsync(client, token, version, deadline.Token), version);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    { throw new TimeoutException("AT 在线模型查询超时；已保留上一份目录，未改动账号或默认模型。"); }
                }, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> RefreshAccessTokenCatalogCoreAsync(AccountRecord account, string identity,
        bool requireConfiguredModel, bool forceRemoteRefresh, Func<bool> isCurrent,
        Func<CancellationToken, Task<(JsonArray Models, string Version)>> fetch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrent()) throw new InvalidOperationException("AT 账号或凭据已变化；未发送模型查询。");
        var path = ManagedAccessTokenModelCatalogPath(account);
        var cached = ReadVerifiedAccessTokenCatalog(path, identity, out var fetchedAt);
        bool SupportsDefaults(JsonArray models) => CatalogSupportsAccessTokenDefaults(
            models, GetAccessTokenModel(account), AccessTokenReasoningEffort);
        var usable = cached != null && (!requireConfiguredModel || SupportsDefaults(cached));
        if (!forceRemoteRefresh && usable && DateTimeOffset.UtcNow - fetchedAt < TimeSpan.FromMinutes(15) && isCurrent())
            return false;

        (JsonArray Models, string Version) remote;
        try { remote = await fetch(cancellationToken); }
        catch (Exception ex) when (!forceRemoteRefresh && usable && isCurrent() &&
                                   IsTransientAccessTokenCatalogError(ex))
        {
            // Startup can use this exact credential's last verified catalog while offline.
            // A forced/background refresh must still report failure, not stale success.
            ManagerLifecycleDiagnostics.Write("access-token-model-refresh-stale", "last_verified_catalog_retained=true");
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (remote.Models.Count == 0) throw new InvalidDataException("AT 在线模型目录为空；未覆盖旧目录。");
        bool changed;
        lock (AccessTokenModelCatalogLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isCurrent()) throw new InvalidOperationException("AT 账号已删除或凭据已变化；已丢弃过期的模型查询结果。");
            changed = WriteAccessTokenModelCatalog(path, remote.Models);
            var receipt = new AccessTokenCatalogReceipt(identity, CatalogFileHash(path), DateTimeOffset.UtcNow, remote.Version);
            AtomicFilePersistence.WriteAllText(path + ".refresh.json", JsonSerializer.Serialize(receipt));
        }
        ManagerLifecycleDiagnostics.Write("access-token-model-remote-refresh-succeeded",
            $"models={remote.Models.Count}; client_version={remote.Version}; changed={changed}");
        // Discovery is still saved when a previous default was removed by the server.
        // Never silently change the user's selected model or claim it is authorized.
        if (requireConfiguredModel && !SupportsDefaults(remote.Models))
            throw new InvalidOperationException("AT 在线目录已更新，但不包含当前默认模型或推理档位；请重新选择。共享登录凭据未切换。");
        return changed;
    }

    private static bool IsTransientAccessTokenCatalogError(Exception ex) => ex is TimeoutException ||
        ex is HttpRequestException http && (http.StatusCode == null ||
            (int)http.StatusCode is 408 or 429 or >= 500);

    private static JsonArray? ReadVerifiedAccessTokenCatalog(string path, string identity, out DateTimeOffset fetchedAt)
    {
        fetchedAt = default;
        try
        {
            var file = new FileInfo(path);
            var receiptFile = new FileInfo(path + ".refresh.json");
            if (!file.Exists || file.Length is <= 0 or > ManagedAccessTokenModelCatalogMaxBytes ||
                !receiptFile.Exists || receiptFile.Length is <= 0 or > 4096) return null;
            var receipt = JsonSerializer.Deserialize<AccessTokenCatalogReceipt>(File.ReadAllText(receiptFile.FullName));
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > ManagedAccessTokenModelCatalogMaxBytes || receipt == null ||
                receipt.CredentialIdentity != identity || receipt.CatalogHash != Convert.ToHexString(SHA256.HashData(bytes)) ||
                receipt.FetchedAtUtc > DateTimeOffset.UtcNow || receipt.FetchedAtUtc == default) return null;
            if (!TryReadAccessTokenModelSource(Encoding.UTF8.GetString(bytes), out _, out var models)) return null;
            fetchedAt = receipt.FetchedAtUtc;
            return models;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string CatalogFileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<JsonArray> FetchAccessTokenRemoteModelsAsync(HttpClient client, string token,
        string version, CancellationToken cancellationToken)
    {
        // These fixed HTTPS destinations cannot be redirected by account TOML, a
        // model_catalog_json override or a gateway's active rotation target.
        using var identityRequest = BuildAccessTokenCatalogRequest(
            "https://auth.openai.com/api/accounts/v1/user-auth-credential/whoami", token, version);
        using var identityResponse = await client.SendAsync(identityRequest,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var identityBody = await ReadAccessTokenCatalogResponseAsync(identityResponse, 64 * 1024, cancellationToken);
        using var identityDocument = JsonDocument.Parse(identityBody);
        if (identityDocument.RootElement.ValueKind != JsonValueKind.Object ||
            !TryReadJsonString(identityDocument.RootElement, "chatgpt_account_id", out var accountId) ||
            !IsSafeChatGptAccountId(accountId))
            throw new InvalidDataException("AT 工作区身份响应无效；未更新模型目录。");

        using var request = BuildAccessTokenCatalogRequest(
            "https://chatgpt.com/backend-api/codex/models?client_version=" + Uri.EscapeDataString(version), token, version);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        if (identityDocument.RootElement.TryGetProperty("chatgpt_account_is_fedramp", out var fedramp) &&
            fedramp.ValueKind == JsonValueKind.True)
            request.Headers.TryAddWithoutValidation("x-openai-fedramp", "true");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await ReadAccessTokenCatalogResponseAsync(response, (int)ManagedAccessTokenModelCatalogMaxBytes, cancellationToken);
        if (!TryReadAccessTokenModelSource(body, out _, out var models))
            throw new InvalidDataException("AT 在线模型目录格式无效或为空；未覆盖旧目录。");
        return models;
    }

    private static HttpRequestMessage BuildAccessTokenCatalogRequest(string url, string token, string version)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        request.Headers.TryAddWithoutValidation("version", version);
        request.Headers.TryAddWithoutValidation("User-Agent", $"codex_cli_rs/{version} (Windows; x86_64) codex-account-manager");
        return request;
    }

    private static async Task<string> ReadAccessTokenCatalogResponseAsync(HttpResponseMessage response, int limit,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AT 在线模型查询返回 HTTP {(int)response.StatusCode}；已保留旧目录。",
                null, response.StatusCode);
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("AT 模型响应超过大小限制。");
        var bytes = await ReadResponseBodyUpToLimitAsync(response.Content, limit, cancellationToken);
        if (bytes == null) throw new InvalidDataException("AT 模型响应超过大小限制。");
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<string> GetModelDiscoveryCliVersionAsync(CancellationToken cancellationToken)
    {
        var command = ResolveCodexCliCommand();
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("找不到 Codex CLI，无法确定在线模型查询的客户端版本。");
        var key = command + "|" + File.GetLastWriteTimeUtc(command).Ticks;
        if (ModelDiscoveryCliVersions.TryGetValue(key, out var cached)) return cached;
        // --version loads neither the live account config nor a model catalog.
        using var process = new Process { StartInfo = new ProcessStartInfo(command, "--version")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var text = await stdout;
            _ = await stderr;
            var match = Regex.Match(text.Trim(), @"^codex-cli\s+(\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?)$");
            if (process.ExitCode != 0 || !match.Success)
                throw new InvalidDataException("无法读取 Codex CLI 版本；未更新模型目录。");
            return ModelDiscoveryCliVersions[key] = match.Groups[1].Value;
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        }
    }
}
