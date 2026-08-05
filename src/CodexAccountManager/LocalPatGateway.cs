using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal static class LocalPatGateway
{
    internal const int Port = 8317;
    internal const string ListenerPrefix = "http://127.0.0.1:8317/";
    internal const string ProviderBaseUrl = "http://127.0.0.1:8317/backend-api/codex";
    internal const string ChatGptBaseUrl = "http://127.0.0.1:8317/backend-api";
    internal const string ProcessArgument = "--local-pat-gateway";

    private const string MarkerHeader = "X-Codex-Account-Manager-Gateway";
    private const string MarkerValue = "pat-v1";
    private const string ProxyKeyHeader = "X-Codex-Account-Manager-Proxy-Key";
    private static readonly SemaphoreSlim StartupLock = new(1, 1);

    internal static int RunProcess()
    {
        return new LocalPatGatewayHost(MarkerHeader, MarkerValue)
            .RunAsync()
            .GetAwaiter()
            .GetResult();
    }

    internal static void EnsureRunning()
    {
        EnsureRunningAsync().GetAwaiter().GetResult();
    }

    internal static async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        await StartupLock.WaitAsync(cancellationToken);
        try
        {
            var health = await ProbeAsync(cancellationToken);
            if (health == GatewayHealth.ProxyMismatch)
            {
                // A gateway is a long-lived child process, so it may have inherited an
                // older proxy choice. Restart it before any PAT-bearing request is sent.
                if (!await ShutdownIfRunningAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "本地 PAT 网关使用了旧的代理配置，且无法安全重启；请关闭后重试。");
                }

                await Task.Delay(120, cancellationToken);
                health = GatewayHealth.Unavailable;
            }
            if (health == GatewayHealth.Ready)
            {
                return;
            }
            if (health == GatewayHealth.ProxyMissing)
            {
                throw BuildProxyMissingException();
            }
            if (health == GatewayHealth.ForeignListener)
            {
                throw new InvalidOperationException(
                    $"本地端口 {Port} 已被其它程序占用。为避免把 PAT 发送给未知进程，本地 PAT 网关未启动。");
            }

            using var process = Process.Start(BuildGatewayStartInfo());
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(120, cancellationToken);
                health = await ProbeAsync(cancellationToken);
                if (health == GatewayHealth.Ready)
                {
                    return;
                }
                if (health == GatewayHealth.ProxyMissing)
                {
                    throw BuildProxyMissingException();
                }
                if (health == GatewayHealth.ForeignListener)
                {
                    throw new InvalidOperationException(
                        $"本地端口 {Port} 返回了未知服务。为避免泄露 PAT，已拒绝连接。");
                }
                if (process is { HasExited: true })
                {
                    break;
                }
            }

            throw new InvalidOperationException(
                $"本地 PAT 网关未能在 8 秒内启动（127.0.0.1:{Port}）。");
        }
        finally
        {
            StartupLock.Release();
        }
    }

    internal static async Task<bool> ShutdownIfRunningAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateLoopbackClient();
        try
        {
            var secret = LocalPatGatewayControl.LoadOrCreateSecret();
            var challenge = LocalPatGatewayControl.CreateChallenge();
            using var request = new HttpRequestMessage(HttpMethod.Post, ListenerPrefix + "__shutdown");
            request.Headers.TryAddWithoutValidation(
                LocalPatGatewayControl.ChallengeHeader,
                challenge);
            request.Headers.TryAddWithoutValidation(
                LocalPatGatewayControl.ProofHeader,
                LocalPatGatewayControl.CreateShutdownProof(secret, challenge));
            using var response = await client.SendAsync(request, cancellationToken);
            return HasExpectedMarker(response) && response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            TaskCanceledException or
            IOException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static InvalidOperationException BuildProxyMissingException()
    {
        return new InvalidOperationException(
            "本地 PAT 网关没有检测到可用的本地代理。请开启代理软件的 HTTP 代理，" +
            "或设置 CODEX_PAT_GATEWAY_PROXY/HTTPS_PROXY 后重试；网关不会静默改成直连。");
    }

    private static ProcessStartInfo BuildGatewayStartInfo()
    {
        _ = LocalPatGatewayControl.LoadOrCreateSecret();
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("无法解析当前程序路径，不能启动本地 PAT 网关。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = AppContext.BaseDirectory,
            // ShellExecute gives this long-lived child its own standard handles. A
            // redirected PowerShell caller can then finish after the launcher exits.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        if (fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Framework-dependent launches use dotnet.exe as ProcessPath. The project
            // assembly is beside it; Assembly.Location is empty for single-file bundles.
            var assemblyName = typeof(LocalPatGateway).Assembly.GetName().Name;
            var assemblyPath = string.IsNullOrWhiteSpace(assemblyName)
                ? null
                : Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("无法解析程序集路径，不能启动本地 PAT 网关。");
            }
            startInfo.ArgumentList.Add(assemblyPath);
        }
        startInfo.ArgumentList.Add(ProcessArgument);
        return startInfo;
    }

    private static async Task<GatewayHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        using var client = CreateLoopbackClient();
        try
        {
            var challenge = LocalPatGatewayControl.CreateChallenge();
            using var request = new HttpRequestMessage(HttpMethod.Get, ListenerPrefix + "healthz");
            request.Headers.TryAddWithoutValidation(
                LocalPatGatewayControl.ChallengeHeader,
                challenge);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!HasExpectedMarker(response))
            {
                return GatewayHealth.ForeignListener;
            }
            if (!HasExpectedControlProof(response, challenge))
            {
                return GatewayHealth.ForeignListener;
            }
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return GatewayHealth.ProxyMissing;
            }
            if (!response.IsSuccessStatusCode)
            {
                return GatewayHealth.Unavailable;
            }

            var expectedProxyKey = LocalPatGatewayControl.ComputeProxyKey(
                CodexCliService.GetConfiguredProxyUri());
            var actualProxyKey = response.Headers.TryGetValues(ProxyKeyHeader, out var values)
                ? values.FirstOrDefault() ?? string.Empty
                : string.Empty;
            return string.Equals(expectedProxyKey, actualProxyKey, StringComparison.Ordinal)
                ? GatewayHealth.Ready
                : GatewayHealth.ProxyMismatch;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return GatewayHealth.Unavailable;
        }
    }

    private static HttpClient CreateLoopbackClient()
    {
        return new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMilliseconds(700)
        };
    }

    private static bool HasExpectedMarker(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues(MarkerHeader, out var values) &&
               values.Contains(MarkerValue, StringComparer.Ordinal);
    }

    private static bool HasExpectedControlProof(
        HttpResponseMessage response,
        string challenge)
    {
        if (!response.Headers.TryGetValues(
                LocalPatGatewayControl.ProofHeader,
                out var values))
        {
            return false;
        }

        var secret = LocalPatGatewayControl.LoadOrCreateSecret();
        var expected = LocalPatGatewayControl.CreateHealthProof(secret, challenge);
        return values.Contains(expected, StringComparer.Ordinal);
    }

    private enum GatewayHealth
    {
        Unavailable,
        Ready,
        ProxyMissing,
        ProxyMismatch,
        ForeignListener
    }
}

internal sealed class LocalPatGatewayHost
{
    private const string MutexName = "Local\\CodexAccountManager.LocalPatGateway.8317";
    private const string UpstreamOrigin = "https://chatgpt.com";
    private const string WhoAmIUrl =
        "https://auth.openai.com/api/accounts/v1/user-auth-credential/whoami";
    private const string DefaultOriginator = "codex_cli_rs";
    private const string RequiredCodexVersion = "0.144.1";
    private const string DefaultUserAgent =
        "codex_cli_rs/0.144.1 (Windows 10.0.0; x86_64) codex-account-manager";
    private static readonly TimeSpan IdentityCacheLifetime = TimeSpan.FromMinutes(30);
    private const int MaxUpstreamErrorBodyBytes = 16 * 1024;
    private const string InactiveWorkspaceMemberMarker =
        "owner is not an active member of the selected workspace";
    private static readonly string[] InactiveWorkspaceMemberMarkers =
    {
        InactiveWorkspaceMemberMarker,
        // Keep compatibility with the shortened wording used by older upstream
        // responses and by local test fixtures.
        "owner not active member of selected workspace"
    };
    private static readonly HashSet<string> RequestHeaderAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept",
        "accept-language",
        "cache-control",
        "content-encoding",
        "content-language",
        "idempotency-key",
        "if-match",
        "if-modified-since",
        "if-none-match",
        "if-unmodified-since",
        "openai-beta",
        "originator",
        "pragma",
        "range",
        "user-agent",
        "version",
        "session-id",
        "thread-id",
        "conversation-id",
        "session_id",
        "conversation_id",
        "x-client-request-id",
        "x-codex-beta-features",
        "x-codex-installation-id",
        "x-codex-models-etag",
        "x-codex-seq",
        "x-codex-trace-id",
        "x-codex-turn-state",
        "x-codex-turn-metadata",
        "x-codex-window-id",
        "x-codex-parent-thread-id",
        "x-openai-subagent",
        "x-openai-memgen-request",
        "x-openai-internal-codex-responses-lite",
        "x-openai-internal-codex-residency",
        "x-oai-attestation",
        "x-responsesapi-include-timing-metrics",
        "traceparent",
        "tracestate"
    };
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade",
        "set-cookie"
    };

    private readonly string _markerHeader;
    private readonly string _markerValue;
    private readonly string _controlSecret;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IdentityCacheEntry> _identityCache = new(StringComparer.Ordinal);

    internal LocalPatGatewayHost(string markerHeader, string markerValue)
    {
        _markerHeader = markerHeader;
        _markerValue = markerValue;
        _controlSecret = LocalPatGatewayControl.LoadOrCreateSecret();
    }

    internal static void ValidateRoutingAndCredentialClassification()
    {
        var pat = ParseBearerCredential("Bearer at-test-only-not-a-real-token");
        var fakeOauth =
            "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ0ZXN0LW9ubHkifQ.signature-test-only-not-real";
        var oauth = ParseBearerCredential("Bearer " + fakeOauth);
        if (pat is not { IsPersonalAccessToken: true } ||
            oauth is not { IsPersonalAccessToken: false } ||
            ParseBearerCredential("Bearer sk-not-accepted") != null ||
            ParseBearerCredential("Basic ignored") != null)
        {
            throw new InvalidOperationException(
                "Gateway credential classification must separate PAT, OAuth, and API keys.");
        }

        if (!TryBuildUpstreamUri(
                new Uri(LocalPatGateway.ListenerPrefix + "backend-api/future/endpoint"),
                out var future) ||
            future.AbsoluteUri != "https://chatgpt.com/backend-api/future/endpoint" ||
            !TryBuildUpstreamUri(
                new Uri(LocalPatGateway.ListenerPrefix + "api/codex/future/endpoint"),
                out var legacy) ||
            legacy.AbsoluteUri != "https://chatgpt.com/api/codex/future/endpoint" ||
            !TryBuildUpstreamUri(new Uri(LocalPatGateway.ProviderBaseUrl), out var model) ||
            model.AbsoluteUri != "https://chatgpt.com/backend-api/codex/responses" ||
            TryBuildUpstreamUri(new Uri(LocalPatGateway.ListenerPrefix + "v1/models"), out _) ||
            TryBuildUpstreamUri(
                new Uri(LocalPatGateway.ListenerPrefix +
                        "backend-api/%25252e%25252e%25252f/v1/models"),
                out _) ||
            !ShouldForwardRequestHeader("content-encoding") ||
            ShouldForwardRequestHeader(LocalPatGatewayControl.ChallengeHeader))
        {
            throw new InvalidOperationException(
                "Gateway routing must stay open within fixed ChatGPT prefixes and reject escapes.");
        }
    }

    // Keep PAT rejection diagnostics deliberately small and non-sensitive. The
    // upstream response is inspected only for the known workspace-membership marker;
    // no upstream body or credential is ever copied into the gateway error.
    internal static void ValidatePatRejectionMessaging()
    {
        var unauthorized = ClassifyPatRejection(HttpStatusCode.Unauthorized, "invalid token");
        if (unauthorized.StatusCode != HttpStatusCode.Unauthorized ||
            !unauthorized.Message.Contains("状态无法确认", StringComparison.Ordinal) ||
            unauthorized.Message.Contains("PAT 无效、已过期或已被撤销", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PAT 401 diagnostics must remain uncertain instead of claiming expiry.");
        }

        var workspace = ClassifyPatRejection(
            HttpStatusCode.Forbidden,
            "{\"detail\":\"owner is not an active member of the selected workspace.\"}");
        if (workspace.StatusCode != HttpStatusCode.Forbidden ||
            !workspace.Message.Contains("工作区成员资格无效", StringComparison.Ordinal) ||
            !workspace.Message.Contains("请在该工作区重新生成或切换账号", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PAT workspace-membership 403 diagnostics must identify the workspace issue.");
        }

        var ordinaryForbidden = ClassifyPatRejection(HttpStatusCode.Forbidden, "access denied");
        if (ordinaryForbidden.StatusCode != HttpStatusCode.Forbidden ||
            !ordinaryForbidden.Message.Contains("PAT 未必过期", StringComparison.Ordinal) ||
            ordinaryForbidden.Message.Contains("工作区成员资格无效", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Other PAT 403 diagnostics must stay uncertain and generic.");
        }
    }

    internal async Task<int> RunAsync()
    {
        using var mutex = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
            {
                return 0;
            }

            using var listener = new HttpListener();
            listener.Prefixes.Add(LocalPatGateway.ListenerPrefix);
            listener.Start();

            using var shutdown = new CancellationTokenSource();
            while (!shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (
                    shutdown.IsCancellationRequested &&
                    ex is HttpListenerException or ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleAsync(context, listener, shutdown));
            }
            return 0;
        }
        catch (HttpListenerException)
        {
            return 2;
        }
        finally
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private async Task HandleAsync(
        HttpListenerContext context,
        HttpListener listener,
        CancellationTokenSource shutdown)
    {
        var response = context.Response;
        response.Headers[_markerHeader] = _markerValue;
        try
        {
            if (!IsLoopback(context.Request.RemoteEndPoint?.Address))
            {
                await WriteErrorAsync(response, HttpStatusCode.Forbidden, "只允许本机访问 PAT 网关。");
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHealthAsync(context.Request, response);
                return;
            }
            if (path.Equals("/__shutdown", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteErrorAsync(response, HttpStatusCode.MethodNotAllowed, "请使用 POST 关闭网关。");
                    return;
                }
                if (!LocalPatGatewayControl.ValidateRequest(
                        context.Request,
                        _controlSecret,
                        "shutdown"))
                {
                    await WriteErrorAsync(response, HttpStatusCode.Unauthorized, "Gateway control request was not authenticated.");
                    return;
                }
                await WriteJsonAsync(response, HttpStatusCode.OK, new { status = "stopping" });
                shutdown.Cancel();
                listener.Stop();
                return;
            }

            if (!TryBuildUpstreamUri(context.Request.Url, out var upstreamUri))
            {
                await WriteErrorAsync(response, HttpStatusCode.NotFound, "本地 PAT 网关不支持这个路径。");
                return;
            }
            if (context.Request.HttpMethod is not (
                    "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"))
            {
                await WriteErrorAsync(response, HttpStatusCode.MethodNotAllowed, "本地 PAT 网关不支持这个请求方法。");
                return;
            }

            var credential = ReadBearerCredential(context.Request);
            if (credential == null)
            {
                await WriteErrorAsync(
                    response,
                    HttpStatusCode.Unauthorized,
                    "请求没有携带可用的 Codex PAT 或 ChatGPT OAuth Bearer。");
                return;
            }

            var proxyUri = ResolveRequiredProxyUri();
            if (proxyUri == null)
            {
                await WriteErrorAsync(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "未检测到可用的本地代理；为防止意外直连，上游请求已停止。");
                return;
            }
            var client = _clients.GetOrAdd(proxyUri.AbsoluteUri, _ => CreateUpstreamClient(proxyUri));
            PatIdentity? identity = null;
            if (credential.IsPersonalAccessToken)
            {
                try
                {
                    identity = await GetIdentityAsync(client, credential.Token);
                }
                catch (PatRejectedException ex)
                {
                    await WritePatRejectionErrorAsync(response, ex.StatusCode, ex.IsInactiveWorkspaceMember);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
                {
                    await WriteErrorAsync(
                        response,
                        HttpStatusCode.BadGateway,
                        "通过本地代理请求 OpenAI PAT 元数据失败：" + SanitizeNetworkError(ex.Message));
                    return;
                }
            }

            using var upstreamRequest = BuildUpstreamRequest(
                context.Request,
                upstreamUri,
                credential.Token,
                identity);
            HttpResponseMessage upstreamResponse;
            try
            {
                upstreamResponse = await client.SendAsync(
                    upstreamRequest,
                    HttpCompletionOption.ResponseHeadersRead);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await WriteErrorAsync(
                    response,
                    HttpStatusCode.BadGateway,
                    "通过本地代理请求 ChatGPT Codex 上游失败：" + SanitizeNetworkError(ex.Message));
                return;
            }

            using (upstreamResponse)
            {
                if (upstreamResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    if (credential.IsPersonalAccessToken)
                    {
                        _identityCache.TryRemove(HashToken(credential.Token), out _);
                        var errorBody = await ReadUpstreamErrorBodyAsync(upstreamResponse);
                        await WritePatRejectionErrorAsync(
                            response,
                            upstreamResponse.StatusCode,
                            IsInactiveWorkspaceMemberError(errorBody));
                        return;
                    }
                }
                await CopyUpstreamResponseAsync(upstreamResponse, response);
            }
        }
        catch (Exception ex) when (
            ex is IOException or
            HttpListenerException or
            ObjectDisposedException or
            InvalidOperationException)
        {
            if (!response.OutputStream.CanWrite)
            {
                return;
            }
            try
            {
                await WriteErrorAsync(
                    response,
                    HttpStatusCode.BadGateway,
                    "本地 PAT 网关处理请求失败：" + SanitizeNetworkError(ex.Message));
            }
            catch
            {
                // The Codex client may disconnect while an SSE response is being copied.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
            }
        }
    }

    private async Task WriteHealthAsync(
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        var proxy = ResolveRequiredProxyUri();
        var challenge = request.Headers[LocalPatGatewayControl.ChallengeHeader]?.Trim();
        if (!string.IsNullOrWhiteSpace(challenge))
        {
            response.Headers[LocalPatGatewayControl.ProofHeader] =
                LocalPatGatewayControl.CreateHealthProof(_controlSecret, challenge);
        }
        if (proxy != null)
        {
            response.Headers["X-Codex-Account-Manager-Proxy-Key"] =
                LocalPatGatewayControl.ComputeProxyKey(proxy.AbsoluteUri);
        }
        await WriteJsonAsync(
            response,
            proxy == null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
            new
            {
                status = proxy == null ? "proxy_required" : "ready",
                proxyConfigured = proxy != null,
                listen = $"127.0.0.1:{LocalPatGateway.Port}"
            });
    }

    private async Task<PatIdentity> GetIdentityAsync(HttpClient client, string token)
    {
        var key = HashToken(token);
        if (_identityCache.TryGetValue(key, out var cached) &&
            cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return cached.Identity;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, WhoAmIUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("originator", DefaultOriginator);
        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var errorBody = await ReadUpstreamErrorBodyAsync(response, timeout.Token);
            throw new PatRejectedException(
                response.StatusCode,
                IsInactiveWorkspaceMemberError(errorBody));
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"whoami returned HTTP {(int)response.StatusCode}",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var root = document.RootElement;
        var accountId = ReadRequiredString(root, "chatgpt_account_id");
        var identity = new PatIdentity(
            accountId,
            ReadOptionalBoolean(root, "chatgpt_account_is_fedramp"));
        _identityCache[key] = new IdentityCacheEntry(
            identity,
            DateTimeOffset.UtcNow + IdentityCacheLifetime);
        return identity;
    }

    private static HttpRequestMessage BuildUpstreamRequest(
        HttpListenerRequest incoming,
        Uri upstreamUri,
        string token,
        PatIdentity? identity)
    {
        var request = new HttpRequestMessage(new HttpMethod(incoming.HttpMethod), upstreamUri);
        if (incoming.HasEntityBody)
        {
            request.Content = new StreamContent(incoming.InputStream);
            if (!string.IsNullOrWhiteSpace(incoming.ContentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", incoming.ContentType);
            }
            if (incoming.ContentLength64 >= 0)
            {
                request.Content.Headers.ContentLength = incoming.ContentLength64;
            }
        }

        foreach (var headerName in incoming.Headers.AllKeys)
        {
            if (headerName == null ||
                headerName.Equals("content-type", StringComparison.OrdinalIgnoreCase) ||
                !ShouldForwardRequestHeader(headerName))
            {
                continue;
            }
            var values = incoming.Headers.GetValues(headerName);
            if (values == null)
            {
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(headerName, values))
            {
                request.Content?.Headers.TryAddWithoutValidation(headerName, values);
            }
        }

        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.Remove("chatgpt-account-id");
        var accountId = identity?.AccountId ?? ReadSafeIncomingAccountId(incoming);
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        }
        if (identity != null)
        {
            request.Headers.Remove("x-openai-fedramp");
            if (identity.IsFedRamp)
            {
                request.Headers.TryAddWithoutValidation("x-openai-fedramp", "true");
            }
        }

        var originator = incoming.Headers["originator"];
        if (string.IsNullOrWhiteSpace(originator) ||
            !originator.StartsWith("codex_", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Remove("originator");
            request.Headers.TryAddWithoutValidation("originator", DefaultOriginator);
        }
        var userAgent = incoming.UserAgent;
        if (string.IsNullOrWhiteSpace(userAgent) ||
            !userAgent.StartsWith("codex", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        }

        var version = request.Headers.TryGetValues("version", out var versionValues)
            ? versionValues.FirstOrDefault()
            : null;
        if (!IsVersionAtLeast(version, RequiredCodexVersion))
        {
            request.Headers.Remove("version");
            request.Headers.TryAddWithoutValidation("version", RequiredCodexVersion);
        }

        if (!request.Headers.TryGetValues("OpenAI-Beta", out var betaValues) ||
            !betaValues.Any(value => value.Contains(
                "responses=experimental",
                StringComparison.OrdinalIgnoreCase)))
        {
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        }
        return request;
    }

    private static bool ShouldForwardRequestHeader(string headerName)
    {
        if (headerName.StartsWith(
                "x-codex-account-manager-",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return RequestHeaderAllowList.Contains(headerName) ||
               headerName.StartsWith("x-codex-", StringComparison.OrdinalIgnoreCase) ||
               headerName.StartsWith("x-openai-", StringComparison.OrdinalIgnoreCase) ||
               headerName.StartsWith("x-oai-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVersionAtLeast(string? value, string minimum)
    {
        if (!Version.TryParse(value?.Trim().TrimStart('v', 'V'), out var actual) ||
            !Version.TryParse(minimum, out var required))
        {
            return false;
        }

        return actual >= required;
    }

    private static async Task CopyUpstreamResponseAsync(
        HttpResponseMessage upstream,
        HttpListenerResponse downstream)
    {
        downstream.StatusCode = (int)upstream.StatusCode;
        // Content-Type is a restricted HttpListener response header. Set the typed
        // property explicitly so streamed Responses/SSE data keeps its media type.
        var contentType = upstream.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            downstream.ContentType = contentType;
        }
        CopyResponseHeaders(upstream.Headers, downstream);
        CopyResponseHeaders(upstream.Content.Headers, downstream);
        if (upstream.Content.Headers.ContentLength is { } contentLength)
        {
            downstream.ContentLength64 = contentLength;
        }
        else
        {
            downstream.SendChunked = true;
        }
        await upstream.Content.CopyToAsync(downstream.OutputStream);
        await downstream.OutputStream.FlushAsync();
    }

    private static void CopyResponseHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> source,
        HttpListenerResponse target)
    {
        foreach (var pair in source)
        {
            if (HopByHopHeaders.Contains(pair.Key) ||
                pair.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                target.Headers[pair.Key] = string.Join(", ", pair.Value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // HttpListener owns a few restricted response headers.
            }
        }
    }

    private static HttpClient CreateUpstreamClient(Uri proxyUri)
    {
        var proxy = new WebProxy(proxyUri);
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = proxy,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static Uri? ResolveRequiredProxyUri()
    {
        var explicitProxy = Environment.GetEnvironmentVariable("CODEX_PAT_GATEWAY_PROXY");
        var proxy = !string.IsNullOrWhiteSpace(explicitProxy)
            ? CodexCliService.NormalizeProxyServer(explicitProxy)
            : CodexCliService.GetConfiguredProxyUri();
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }
        if (LocalProxyDetector.IsLoopbackHost(uri.Host) &&
            uri.Port == LocalPatGateway.Port)
        {
            return null;
        }
        return uri;
    }

    private static GatewayCredential? ReadBearerCredential(HttpListenerRequest request)
    {
        return ParseBearerCredential(request.Headers["Authorization"]);
    }

    private static GatewayCredential? ParseBearerCredential(string? authorization)
    {
        authorization = authorization?.Trim();
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var token = authorization["Bearer ".Length..].Trim();
        if (token.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (token.StartsWith("at-", StringComparison.Ordinal))
        {
            return new GatewayCredential(token, IsPersonalAccessToken: true);
        }

        // ChatGPT desktop OAuth access tokens are JWTs. They are forwarded as-is;
        // the gateway neither creates nor refreshes this separate login state.
        var jwtSegments = token.Split('.');
        return token.StartsWith("eyJ", StringComparison.Ordinal) &&
               token.Length >= 64 &&
               jwtSegments.Length == 3 &&
               jwtSegments.All(segment => segment.Length > 0)
            ? new GatewayCredential(token, IsPersonalAccessToken: false)
            : null;
    }

    private static string? ReadSafeIncomingAccountId(HttpListenerRequest request)
    {
        var value = request.Headers["chatgpt-account-id"]?.Trim();
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 128 &&
               value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            ? value
            : null;
    }

    private static bool TryBuildUpstreamUri(Uri? incoming, out Uri upstream)
    {
        upstream = null!;
        if (incoming == null)
        {
            return false;
        }
        var rawPath = incoming.OriginalString.Split('?', 2)[0];
        if (ContainsDotSegments(rawPath))
        {
            return false;
        }
        var path = incoming.AbsolutePath;
        const string modelGatewayPrefix = "/backend-api/codex";
        const string backendGatewayPrefix = "/backend-api";
        const string legacyApiPrefix = "/api/codex";
        string upstreamPath;
        if (HasPathPrefix(path, modelGatewayPrefix))
        {
            var suffix = path[modelGatewayPrefix.Length..];
            if (suffix.Length == 0)
            {
                suffix = "/responses";
            }
            upstreamPath = modelGatewayPrefix + suffix;
        }
        else if (HasPathPrefix(path, backendGatewayPrefix))
        {
            var suffix = path[backendGatewayPrefix.Length..];
            upstreamPath = backendGatewayPrefix + suffix;
        }
        else if (HasPathPrefix(path, legacyApiPrefix))
        {
            var suffix = path[legacyApiPrefix.Length..];
            upstreamPath = legacyApiPrefix + suffix;
        }
        else
        {
            return false;
        }
        if (!Uri.TryCreate(
                UpstreamOrigin + upstreamPath + incoming.Query,
                UriKind.Absolute,
                out var resolved))
        {
            return false;
        }
        var canonicalPath = resolved.AbsolutePath;
        var canonicalAllowed = HasPathPrefix(canonicalPath, backendGatewayPrefix) ||
                               HasPathPrefix(canonicalPath, legacyApiPrefix);
        if (!canonicalAllowed)
        {
            return false;
        }
        upstream = resolved;
        return true;
    }

    private static bool HasPathPrefix(string path, string prefix)
    {
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               (path.Length == prefix.Length || path[prefix.Length] == '/');
    }

    private static bool ContainsDotSegments(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        foreach (var segment in rawPath.Split('/'))
        {
            var decoded = segment;
            var stabilized = false;
            try
            {
                for (var attempt = 0; attempt < 16; attempt++)
                {
                    var next = Uri.UnescapeDataString(decoded);
                    if (string.Equals(next, decoded, StringComparison.Ordinal))
                    {
                        stabilized = true;
                        break;
                    }
                    decoded = next;
                }
            }
            catch (UriFormatException)
            {
                return true;
            }

            if (!stabilized ||
                decoded is "." or ".." ||
                decoded.Contains('/') ||
                decoded.Contains('\\'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLoopback(IPAddress? address)
    {
        return address != null && IPAddress.IsLoopback(address);
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"whoami response omitted {propertyName}");
        }
        return value.GetString()!.Trim();
    }

    private static bool ReadOptionalBoolean(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static string SanitizeNetworkError(string message)
    {
        return CodexCliService.MaskSensitiveText(message)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static PatRejectionDetails ClassifyPatRejection(
        HttpStatusCode statusCode,
        string? upstreamBody)
    {
        if (statusCode == HttpStatusCode.Forbidden &&
            IsInactiveWorkspaceMemberError(upstreamBody))
        {
            return new PatRejectionDetails(
                statusCode,
                "PAT 未必过期，但当前 ChatGPT 工作区成员资格无效；请在该工作区重新生成或切换账号。");
        }

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return new PatRejectionDetails(
                statusCode,
                "PAT 状态无法确认：上游返回 HTTP 401，可能是凭据无效、已过期、权限不足或当前请求上下文不匹配。请在 ChatGPT 中重新生成 PAT 或切换账号。");
        }

        return new PatRejectionDetails(
            statusCode,
            "PAT 状态无法确认：上游拒绝了请求（HTTP 403），可能是工作区、权限或账号上下文限制；PAT 未必过期。请检查当前 ChatGPT 工作区后重试。");
    }

    private static bool IsInactiveWorkspaceMemberError(string? upstreamBody)
    {
        return !string.IsNullOrWhiteSpace(upstreamBody) &&
               InactiveWorkspaceMemberMarkers.Any(marker =>
                   upstreamBody.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static Task WritePatRejectionErrorAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        bool isInactiveWorkspaceMember)
    {
        var marker = isInactiveWorkspaceMember
            ? InactiveWorkspaceMemberMarker
            : null;
        var details = ClassifyPatRejection(statusCode, marker);
        return WriteErrorAsync(response, details.StatusCode, details.Message);
    }

    private static async Task<string> ReadUpstreamErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxUpstreamErrorBodyBytes];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }
            return Encoding.UTF8.GetString(buffer, 0, offset);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            return string.Empty;
        }
    }

    private static Task WriteErrorAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        string message)
    {
        return WriteJsonAsync(
            response,
            statusCode,
            new
            {
                error = new
                {
                    type = "local_pat_gateway_error",
                    message
                }
            });
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }

    private sealed record PatIdentity(string AccountId, bool IsFedRamp);
    private sealed record IdentityCacheEntry(PatIdentity Identity, DateTimeOffset ExpiresAtUtc);
    private sealed record GatewayCredential(string Token, bool IsPersonalAccessToken);
    private sealed record PatRejectionDetails(HttpStatusCode StatusCode, string Message);

    private sealed class PatRejectedException(
        HttpStatusCode statusCode,
        bool isInactiveWorkspaceMember) : Exception
    {
        internal HttpStatusCode StatusCode { get; } = statusCode;
        internal bool IsInactiveWorkspaceMember { get; } = isInactiveWorkspaceMember;
    }
}
