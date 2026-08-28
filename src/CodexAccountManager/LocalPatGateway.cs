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
    internal const string RequestTimeoutHeader = "X-Codex-Account-Manager-Request-Timeout-Ms";
    internal const string ProcessArgument = "--local-pat-gateway";
    internal const string RootArgument = "--manager-root";
    internal const string RotationArmPath = "__rotation/arm";
    internal const string RotationClearPath = "__rotation/clear";

    private const string MarkerHeader = "X-Codex-Account-Manager-Gateway";
    private const string MarkerValue = "pat-v1";
    private const string ProxyKeyHeader = "X-Codex-Account-Manager-Proxy-Key";
    internal const string RotationProtocolHeader = "X-Codex-Account-Manager-Rotation";
    // v3 already implements authenticated arm/clear control calls and activates a
    // prepared route at the next model-request boundary. Keep this legacy marker so an
    // in-flight gateway can finish naturally while a newer Manager waits for a safe
    // listener hand-off.
    internal const string CompatibleRotationProtocolValue = "request-boundary-v3";
    // v4 makes a quota-limited event durable and independently observable by the
    // Manager. v5 additionally buffers a model request in memory and, before any
    // downstream response byte is written, retries an upstream 429 through the ordered
    // account rings. Older gateways remain readable until the existing safe boundary.
    internal const string DurableRotationProtocolValue = "request-boundary-v4";
    internal const string RotationProtocolValue = "request-boundary-v5";
    private static readonly SemaphoreSlim StartupLock = new(1, 1);

    internal static int RunProcess(string[] args)
    {
        var rootIndex = Array.FindIndex(
            args,
            argument => argument.Equals(RootArgument, StringComparison.OrdinalIgnoreCase));
        if (rootIndex >= 0 && rootIndex + 1 < args.Length && !string.IsNullOrWhiteSpace(args[rootIndex + 1]))
        {
            Environment.SetEnvironmentVariable(
                "CODEX_ACCOUNT_MANAGER_HOME",
                Path.GetFullPath(args[rootIndex + 1]));
        }

        return new LocalPatGatewayHost(MarkerHeader, MarkerValue).Run();
    }

    internal static void EnsureRunning()
    {
        EnsureRunningAsync().GetAwaiter().GetResult();
    }

    internal static Task EnsureRunningForLightweightTestAsync(
        CancellationToken cancellationToken = default) =>
        EnsureRunningAsync(cancellationToken, restartOnProxyMismatch: false);

    internal static bool IsEnabledBySettings()
    {
        var store = new AccountStore();
        return new ThemeService(store.RootPath).LoadSettings().PatGatewayEnabled;
    }

    internal static async Task EnsureRunningAsync(
        CancellationToken cancellationToken = default,
        bool restartOnProxyMismatch = true)
    {
        if (!IsEnabledBySettings())
        {
            throw new InvalidOperationException(
                "本地 PAT 网关已在系统配置中关闭，请先打开网关再启动 Access Token 账号。");
        }

        await StartupLock.WaitAsync(cancellationToken);
        try
        {
            var health = await ProbeAsync(cancellationToken);
            if (health == GatewayHealth.LegacyRootMismatch)
            {
                if (!restartOnProxyMismatch)
                {
                    throw new InvalidOperationException(
                        "当前运行的是旧版本地 PAT 网关。为避免中断正在运行的账号，轻量测试没有重启网关；" +
                        "请保留网关运行并重新打开新版管理器后再试。");
                }
                if (!TryLoadLegacyControlSecret(out var legacySecret) ||
                    !await ShutdownWithSecretAsync(legacySecret, cancellationToken))
                {
                    throw new InvalidOperationException(
                        "检测到旧版本启动的本地 PAT 网关，但无法安全关闭；请退出旧版管理器后重试。");
                }

                await Task.Delay(120, cancellationToken);
                health = GatewayHealth.Unavailable;
            }
            if (health == GatewayHealth.ProxyMismatch)
            {
                if (!restartOnProxyMismatch)
                {
                    // A quota test must never interrupt an already-running account. The
                    // existing gateway can safely finish the request through its inherited
                    // proxy; the next explicit gateway start may apply the new proxy choice.
                    return;
                }
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
            if (health == GatewayHealth.UpgradeRequired)
            {
                if (!restartOnProxyMismatch)
                {
                    // Read-only quota probes may share an older gateway while a task is
                    // active. The next explicit PAT launch upgrades it at a safe boundary.
                    return;
                }
                if (!await ShutdownIfRunningAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "检测到不支持无缝轮换的旧版 PAT 网关，但无法安全升级；请退出旧版管理器后重试。");
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
        return await ShutdownWithSecretAsync(
            LocalPatGatewayControl.LoadOrCreateSecret(),
            cancellationToken);
    }

    internal static async Task<bool> ShutdownOwnedGatewayAsync(
        CancellationToken cancellationToken = default)
    {
        var health = await ProbeAsync(cancellationToken);
        if (health == GatewayHealth.Unavailable)
        {
            return true;
        }
        if (health is GatewayHealth.Ready or GatewayHealth.ProxyMissing or
            GatewayHealth.ProxyMismatch or GatewayHealth.UpgradeRequired)
        {
            return await ShutdownIfRunningAsync(cancellationToken);
        }
        if (health != GatewayHealth.LegacyRootMismatch ||
            !TryLoadLegacyControlSecret(out var legacySecret))
        {
            return false;
        }

        return await ShutdownWithSecretAsync(legacySecret, cancellationToken);
    }

    internal static Task<LocalPatGatewayActivitySnapshot?> ReadActivitySnapshotAsync(
        CancellationToken cancellationToken = default) =>
        ReadActivitySnapshotCoreAsync(requireCompatibleProtocol: true, cancellationToken);

    internal static Task<LocalPatGatewayActivitySnapshot?> ReadOwnedActivitySnapshotAsync(
        CancellationToken cancellationToken = default) =>
        ReadActivitySnapshotCoreAsync(requireCompatibleProtocol: false, cancellationToken);

    internal static async Task<bool> RequiresRotationProtocolUpgradeAsync(
        CancellationToken cancellationToken = default) =>
        await ProbeAsync(cancellationToken) == GatewayHealth.UpgradeRequired;

    private static async Task<LocalPatGatewayActivitySnapshot?> ReadActivitySnapshotCoreAsync(
        bool requireCompatibleProtocol,
        CancellationToken cancellationToken)
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
            if (!HasExpectedMarker(response) ||
                !HasExpectedControlProof(response, challenge) ||
                (requireCompatibleProtocol && !HasCompatibleRotationProtocol(response)) ||
                !response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("activity", out var activity) ||
                activity.ValueKind != JsonValueKind.Object ||
                !activity.TryGetProperty("activeModelRequests", out var activeValue) ||
                !activeValue.TryGetInt32(out var activeModelRequests))
            {
                // An older owned gateway is safe to keep serving traffic, but it cannot be
                // used as an automatic-switch boundary oracle.
                return null;
            }

            return new LocalPatGatewayActivitySnapshot(
                Math.Max(0, activeModelRequests),
                ReadUnixMilliseconds(activity, "lastModelRequestStartedAtUnixMs"),
                ReadUnixMilliseconds(activity, "lastModelRequestCompletedAtUnixMs"),
                ReadUnixMilliseconds(activity, "lastQuotaLimitedAtUnixMs"),
                ReadRotationSnapshot(root),
                activity.TryGetProperty("completedModelRequests", out var completedValue) &&
                completedValue.TryGetInt64(out var completedModelRequests)
                    ? Math.Max(0L, completedModelRequests)
                    : null,
                ReadAccountKey(activity, "lastModelRequestAccountKey"),
                ReadAccountKey(activity, "lastQuotaLimitedAccountKey"),
                activity.TryGetProperty("lastQuotaLimitedSequence", out var sequenceValue) &&
                sequenceValue.TryGetInt64(out var lastQuotaLimitedSequence) &&
                lastQuotaLimitedSequence > 0L
                    ? lastQuotaLimitedSequence
                    : null);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            TaskCanceledException or
            IOException or
            JsonException or
            InvalidOperationException)
        {
            return null;
        }
    }

    internal static async Task<PatGatewayRotationSnapshot> ArmRotationAsync(
        string sourceAccountKey,
        string targetAccountKey,
        CancellationToken cancellationToken = default)
    {
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(sourceAccountKey, out var source) ||
            !PatGatewayRotationStore.TryNormalizeAccountKey(targetAccountKey, out var target) ||
            source.Equals(target, StringComparison.Ordinal))
        {
            throw new ArgumentException("PAT rotation requires two different account hashes.");
        }

        await EnsureRunningAsync(cancellationToken, restartOnProxyMismatch: false);
        if (await ProbeAsync(cancellationToken) == GatewayHealth.UpgradeRequired &&
            !await HasCompatibleLegacyRotationProtocolAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "当前网关版本不支持 PAT/API 无缝轮换；已保留正在运行的请求，请等待安全升级完成。");
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sourceAccountKey = source,
            targetAccountKey = target
        });
        using var client = CreateLoopbackControlClient();
        var challenge = LocalPatGatewayControl.CreateChallenge();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ListenerPrefix + RotationArmPath);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/json");
        request.Headers.TryAddWithoutValidation(
            LocalPatGatewayControl.ChallengeHeader,
            challenge);
        request.Headers.TryAddWithoutValidation(
            LocalPatGatewayControl.ProofHeader,
            LocalPatGatewayControl.CreateProof(
                LocalPatGatewayControl.LoadOrCreateSecret(),
                challenge,
                BuildRotationArmPurpose(payload)));
        using var response = await client.SendAsync(request, cancellationToken);
        if (!HasExpectedMarker(response))
        {
            throw new InvalidOperationException("本地 PAT 网关没有返回可信的轮换协议标记。");
        }
        if (!HasCompatibleRotationProtocol(response))
        {
            throw new InvalidOperationException("本地 PAT 网关返回了不兼容的轮换协议标记。");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"本地 PAT 网关拒绝了轮换准备（HTTP {(int)response.StatusCode}）。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        return ReadRotationSnapshot(document.RootElement) is { } snapshot &&
               snapshot.Status != PatGatewayRotationStatus.None
            ? snapshot
            : throw new InvalidDataException("本地 PAT 网关没有返回有效的轮换状态。");
    }

    internal static async Task<bool> ClearRotationAsync(
        CancellationToken cancellationToken = default)
    {
        var store = new PatGatewayRotationStore(new AccountStore().RootPath);
        try
        {
            store.Clear();
        }
        catch (IOException)
        {
            return false;
        }

        using var client = CreateLoopbackControlClient();
        try
        {
            var challenge = LocalPatGatewayControl.CreateChallenge();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                ListenerPrefix + RotationClearPath);
            request.Headers.TryAddWithoutValidation(
                LocalPatGatewayControl.ChallengeHeader,
                challenge);
            request.Headers.TryAddWithoutValidation(
                LocalPatGatewayControl.ProofHeader,
                LocalPatGatewayControl.CreateProof(
                    LocalPatGatewayControl.LoadOrCreateSecret(),
                    challenge,
                    "rotation-clear"));
            using var response = await client.SendAsync(request, cancellationToken);
            if (!HasExpectedMarker(response))
            {
                return false;
            }
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // No listener means the already-deleted route is fully cleared.
            return true;
        }
    }

    internal static void ClearPersistedRotationRoute()
    {
        new PatGatewayRotationStore(new AccountStore().RootPath).Clear();
    }

    private static PatGatewayRotationSnapshot? ReadRotationSnapshot(JsonElement root)
    {
        if (!root.TryGetProperty("rotation", out var rotation) ||
            rotation.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var status = rotation.TryGetProperty("status", out var statusValue)
            ? statusValue.GetString() switch
            {
                "armed" => PatGatewayRotationStatus.Armed,
                "active" => PatGatewayRotationStatus.Active,
                _ => PatGatewayRotationStatus.None
            }
            : PatGatewayRotationStatus.None;
        return new PatGatewayRotationSnapshot(
            status,
            ReadAccountKey(rotation, "transportAccountKey"),
            ReadAccountKey(rotation, "sourceAccountKey"),
            ReadAccountKey(rotation, "targetAccountKey"),
            ReadUnixMilliseconds(rotation, "armedAtUnixMs"),
            ReadUnixMilliseconds(rotation, "activatedAtUnixMs"));
    }

    private static string? ReadAccountKey(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !PatGatewayRotationStore.TryNormalizeAccountKey(value.GetString(), out var normalized))
        {
            return null;
        }
        return normalized;
    }

    private static string BuildRotationArmPurpose(ReadOnlySpan<byte> payload) =>
        "rotation-arm\n" + Convert.ToHexString(SHA256.HashData(payload));

    private static DateTimeOffset? ReadUnixMilliseconds(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null ||
            !value.TryGetInt64(out var unixMilliseconds) ||
            unixMilliseconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static async Task<bool> ShutdownWithSecretAsync(
        string secret,
        CancellationToken cancellationToken)
    {
        using var client = CreateLoopbackClient();
        try
        {
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
        var managerRoot = new AccountStore().RootPath;
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("无法解析当前程序路径，不能启动本地 PAT 网关。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = managerRoot,
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
        startInfo.ArgumentList.Add(RootArgument);
        startInfo.ArgumentList.Add(managerRoot);
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
                return HasExpectedLegacyControlProof(response, challenge)
                    ? GatewayHealth.LegacyRootMismatch
                    : GatewayHealth.ForeignListener;
            }
            if (!response.Headers.TryGetValues(RotationProtocolHeader, out var rotationValues) ||
                !rotationValues.Contains(RotationProtocolValue, StringComparer.Ordinal))
            {
                return GatewayHealth.UpgradeRequired;
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

    private static HttpClient CreateLoopbackControlClient()
    {
        return new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private static bool HasExpectedMarker(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues(MarkerHeader, out var values) &&
               values.Contains(MarkerValue, StringComparer.Ordinal);
    }

    private static bool HasExpectedRotationProtocol(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues(RotationProtocolHeader, out var values) &&
               values.Contains(RotationProtocolValue, StringComparer.Ordinal);
    }

    private static bool HasCompatibleRotationProtocol(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues(RotationProtocolHeader, out var values) &&
               values.Any(IsCompatibleRotationProtocolValue);
    }

    internal static bool IsCompatibleRotationProtocolValue(string? value) =>
        string.Equals(value, RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, DurableRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, CompatibleRotationProtocolValue, StringComparison.Ordinal);

    private static async Task<bool> HasCompatibleLegacyRotationProtocolAsync(
        CancellationToken cancellationToken)
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
            return response.IsSuccessStatusCode &&
                   HasExpectedMarker(response) &&
                   HasExpectedControlProof(response, challenge) &&
                   response.Headers.TryGetValues(RotationProtocolHeader, out var values) &&
                   values.Any(value =>
                       string.Equals(value, CompatibleRotationProtocolValue, StringComparison.Ordinal) ||
                       string.Equals(value, DurableRotationProtocolValue, StringComparison.Ordinal));
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or IOException or
            InvalidOperationException)
        {
            return false;
        }
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

    private static bool HasExpectedLegacyControlProof(
        HttpResponseMessage response,
        string challenge)
    {
        if (!response.Headers.TryGetValues(
                LocalPatGatewayControl.ProofHeader,
                out var values) ||
            !TryLoadLegacyControlSecret(out var secret))
        {
            return false;
        }

        var expected = LocalPatGatewayControl.CreateHealthProof(secret, challenge);
        return values.Contains(expected, StringComparer.Ordinal);
    }

    private static bool TryLoadLegacyControlSecret(out string secret)
    {
        var currentRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(new AccountStore().RootPath));
        var legacyRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));
        if (currentRoot.Equals(legacyRoot, StringComparison.OrdinalIgnoreCase))
        {
            secret = string.Empty;
            return false;
        }

        return LocalPatGatewayControl.TryLoadSecretForRoot(legacyRoot, out secret);
    }

    private enum GatewayHealth
    {
        Unavailable,
        Ready,
        ProxyMissing,
        ProxyMismatch,
        UpgradeRequired,
        LegacyRootMismatch,
        ForeignListener
    }
}

internal sealed record LocalPatGatewayActivitySnapshot(
    int ActiveModelRequests,
    DateTimeOffset? LastModelRequestStartedAtUtc,
    DateTimeOffset? LastModelRequestCompletedAtUtc,
    DateTimeOffset? LastQuotaLimitedAtUtc,
    PatGatewayRotationSnapshot? Rotation = null,
    long? CompletedModelRequests = null,
    string? LastModelRequestAccountKey = null,
    string? LastQuotaLimitedAccountKey = null,
    long? LastQuotaLimitedSequence = null);

internal sealed class LocalPatGatewayHost
{
    private const int CompatibleApiRequestBodyMaxBytes = 128 * 1024 * 1024;
    private const int ReplayableModelRequestBodyMaxBytes = CompatibleApiRequestBodyMaxBytes;
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
    private static readonly HashSet<string> ProtocolRequestHeaderAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept",
        "cache-control",
        "content-encoding",
        "content-language",
        "idempotency-key",
        "if-match",
        "if-modified-since",
        "if-none-match",
        "if-unmodified-since",
        "openai-beta",
        "pragma",
        "range",
        "session-id",
        "thread-id",
        "conversation-id",
        "session_id",
        "conversation_id",
        "x-client-request-id",
        "x-codex-beta-features",
        "x-codex-models-etag",
        "x-codex-seq",
        "x-codex-trace-id",
        "x-codex-turn-state",
        "x-codex-turn-metadata",
        "x-codex-window-id",
        "x-codex-parent-thread-id",
        "x-openai-subagent",
        "x-openai-memgen-request"
    };
    private static readonly HashSet<string> ClientMetadataHeaderAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept-language",
        "originator",
        "user-agent",
        "version",
        "x-codex-installation-id",
        "x-openai-internal-codex-responses-lite",
        "x-openai-internal-codex-residency",
        "x-oai-attestation",
        "x-responsesapi-include-timing-metrics",
        "traceparent",
        "tracestate"
    };
    private static readonly HashSet<string> NeverForwardRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "cookie",
        "cookie2",
        "chatgpt-account-id",
        "openai-organization",
        "openai-project",
        "x-openai-account-id",
        "x-openai-fedramp",
        "x-openai-organization",
        "x-openai-project",
        "x-openai-user-id",
        "x-openai-workspace-id",
        "x-oai-account-id",
        "x-oai-organization-id",
        "x-oai-project-id",
        "x-oai-user-id",
        "x-oai-workspace-id"
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
    private readonly AccountStore _accountStore;
    private readonly ThemeService _themeService;
    private readonly PatGatewayRotationStore _rotationStore;
    private readonly PatGatewayQuotaSignalStore _quotaSignalStore;
    private readonly object _rotationGate = new();
    private readonly SemaphoreSlim _rotationActivationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IdentityCacheEntry> _identityCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _quotaLimitedAccountKeys =
        new(StringComparer.Ordinal);
    private readonly object _activityGate = new();
    private readonly Dictionary<string, long> _completedModelRequestsByAccount =
        new(StringComparer.Ordinal);
    private int _activeModelRequests;
    private long _completedModelRequests;
    private string? _lastModelRequestAccountKey;
    private string? _lastQuotaLimitedAccountKey;
    private DateTimeOffset? _lastModelRequestStartedAtUtc;
    private DateTimeOffset? _lastModelRequestCompletedAtUtc;
    private DateTimeOffset? _lastQuotaLimitedAtUtc;
    private long? _lastQuotaLimitedSequence;

    internal LocalPatGatewayHost(string markerHeader, string markerValue)
    {
        _markerHeader = markerHeader;
        _markerValue = markerValue;
        _controlSecret = LocalPatGatewayControl.LoadOrCreateSecret();
        _accountStore = new AccountStore();
        _themeService = new ThemeService(_accountStore.RootPath);
        _rotationStore = new PatGatewayRotationStore(_accountStore.RootPath);
        _quotaSignalStore = new PatGatewayQuotaSignalStore(_accountStore.RootPath);
        var recoveredQuotaSignals = _quotaSignalStore.ReadLatestPerAccount(DateTimeOffset.UtcNow);
        foreach (var recoveredQuotaSignal in recoveredQuotaSignals)
        {
            _quotaLimitedAccountKeys[recoveredQuotaSignal.AccountKey] =
                recoveredQuotaSignal.ObservedAtUtc;
        }
        if (recoveredQuotaSignals.FirstOrDefault() is { } latestRecoveredQuotaSignal)
        {
            // Rehydrate health diagnostics too. The Manager consumes the file directly,
            // but this keeps the authenticated health snapshot truthful after a gateway
            // process upgrade or crash recovery. The full per-account map above prevents
            // an earlier exhausted ring member from being retried after a gateway restart.
            _lastQuotaLimitedAccountKey = latestRecoveredQuotaSignal.AccountKey;
            _lastQuotaLimitedAtUtc = latestRecoveredQuotaSignal.ObservedAtUtc;
            _lastQuotaLimitedSequence = latestRecoveredQuotaSignal.Sequence;
        }
    }

    internal static void ValidateRoutingAndCredentialClassification()
    {
        if (!LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v3") ||
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v4") ||
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v5") ||
            LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v2") ||
            LocalPatGateway.IsCompatibleRotationProtocolValue(null))
        {
            throw new InvalidOperationException(
                "Gateway routing compatibility must accept only request-boundary v3/v4/v5.");
        }

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
            ShouldForwardRequestHeader("x-codex-installation-id") ||
            !ShouldForwardRequestHeader("x-codex-installation-id", true) ||
            ShouldForwardRequestHeader("authorization", true) ||
            ShouldForwardRequestHeader("cookie", true) ||
            ShouldForwardRequestHeader("chatgpt-account-id", true) ||
            ShouldForwardRequestHeader("x-openai-workspace-id", true) ||
            ShouldForwardRequestHeader("x-openai-future-client-metadata", true) ||
            ShouldForwardRequestHeader(LocalPatGatewayControl.ChallengeHeader, true))
        {
            throw new InvalidOperationException(
                "Gateway routing must stay open within fixed ChatGPT prefixes and reject escapes.");
        }

        var transportKey = new string('A', 64);
        var sourceKey = new string('B', 64);
        var targetKey = new string('C', 64);
        var apiKey = new string('D', 64);
        var oauthKey = new string('E', 64);
        var route = new PatGatewayRotationSnapshot(
            PatGatewayRotationStatus.Armed,
            transportKey,
            sourceKey,
            targetKey,
            DateTimeOffset.UtcNow,
            null);
        var activated = false;
        GatewayCredential ResolveFixture(string key) => key switch
        {
            var value when value == transportKey =>
                new GatewayCredential("at-transport-test-only", true),
            var value when value == targetKey =>
                new GatewayCredential("at-target-test-only", true),
            var value when value == apiKey =>
                new GatewayCredential(
                    "sk-api-test-only",
                    false,
                    new Uri("https://api.example.invalid/v1"),
                    "gpt-api-test"),
            var value when value == oauthKey =>
                new GatewayCredential(
                    fakeOauth,
                    IsPersonalAccessToken: false,
                    AccountKey: oauthKey,
                    ChatGptAccountId: "account-target",
                    AllowIncomingChatGptIdentity: false),
            _ => throw new InvalidDataException("Unexpected fixture account hash.")
        };
        var selected = SelectRotationCredentialAtRequestBoundary(
            new GatewayCredential("at-transport-test-only", true),
            route,
            ResolveFixture,
            () => activated = true);
        var unrelated = SelectRotationCredentialAtRequestBoundary(
            new GatewayCredential("at-unrelated-test-only", true),
            route,
            ResolveFixture,
            () => throw new InvalidOperationException("Unrelated request activated a route."));
        var apiSelected = SelectRotationCredentialAtRequestBoundary(
            new GatewayCredential("at-transport-test-only", true),
            route with { TargetAccountKey = apiKey },
            ResolveFixture,
            () => { });
        var oauthSelected = SelectRotationCredentialAtRequestBoundary(
            new GatewayCredential("at-transport-test-only", true),
            route with { TargetAccountKey = oauthKey },
            ResolveFixture,
            () => { });
        var rewritten = RewriteCompatibleApiRequestBody(
            Encoding.UTF8.GetBytes("{\"model\":\"old\",\"input\":[{\"role\":\"user\",\"content\":\"keep\"}]}"),
            "gpt-api-test");
        using var rewrittenDocument = JsonDocument.Parse(rewritten);
        var compatibleUriOk = TryBuildCompatibleApiUpstreamUri(
            new Uri("https://api.example.invalid/v1"),
            new Uri(LocalPatGateway.ListenerPrefix + "backend-api/codex/responses?stream=true"),
            out var compatibleUri);
        if (!activated ||
            selected.Token != "at-target-test-only" ||
            unrelated.Token != "at-unrelated-test-only" ||
            !apiSelected.IsCompatibleApi ||
            apiSelected.CompatibleApiModel != "gpt-api-test" ||
            oauthSelected.ChatGptAccountId != "account-target" ||
            oauthSelected.AllowIncomingChatGptIdentity ||
            SelectChatGptAccountId(oauthSelected, null, "account-old") != "account-target" ||
            !compatibleUriOk ||
            compatibleUri.AbsoluteUri != "https://api.example.invalid/v1/responses?stream=true" ||
            rewrittenDocument.RootElement.GetProperty("model").GetString() != "gpt-api-test" ||
            rewrittenDocument.RootElement.GetProperty("input")[0].GetProperty("content").GetString() != "keep" ||
            Encoding.UTF8.GetString(rewritten).Contains("sk-api-test-only", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Gateway request-boundary rotation selected or transformed the wrong PAT/API target.");
        }

        ValidateOfficialOAuthRotationCredential();
    }

    private static void ValidateOfficialOAuthRotationCredential()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "oauth-rotation-credential-test-" + Guid.NewGuid().ToString("N"));
        var authPath = Path.Combine(root, "auth.json");
        var accountKey = new string('A', 64);
        try
        {
            Directory.CreateDirectory(root);
            var futureToken = BuildTestJwt(DateTimeOffset.UtcNow.AddHours(1));
            File.WriteAllText(
                authPath,
                JsonSerializer.Serialize(
                    new
                    {
                        tokens = new
                        {
                            id_token = BuildTestJwt(DateTimeOffset.UtcNow.AddHours(1)),
                            access_token = futureToken,
                            refresh_token = "refresh-test-only",
                            account_id = "account-target"
                        }
                    }),
                new UTF8Encoding(false));
            var credential = ReadOfficialOAuthRotationCredential(
                authPath,
                accountKey,
                "fixture");
            if (credential.Token != futureToken ||
                credential.IsPersonalAccessToken ||
                credential.IsCompatibleApi ||
                credential.AccountKey != accountKey ||
                credential.ChatGptAccountId != "account-target" ||
                credential.AllowIncomingChatGptIdentity)
            {
                throw new InvalidOperationException(
                    "Official OAuth rotation did not bind the stored target identity.");
            }

            File.WriteAllText(
                authPath,
                JsonSerializer.Serialize(
                    new
                    {
                        tokens = new
                        {
                            id_token = BuildTestJwt(DateTimeOffset.UtcNow.AddHours(1)),
                            access_token = BuildTestJwt(DateTimeOffset.UtcNow.AddMinutes(-1)),
                            refresh_token = "refresh-test-only",
                            account_id = "account-target"
                        }
                    }),
                new UTF8Encoding(false));
            if (!string.Equals(
                    TryReadOfficialOAuthAccountId(authPath),
                    "account-target",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "OAuth account_id must remain readable even when an access token expires.");
            }
            try
            {
                _ = ReadOfficialOAuthRotationCredential(authPath, accountKey, "fixture");
                throw new InvalidOperationException(
                    "An expired OAuth token was accepted as a rotation target.");
            }
            catch (InvalidDataException)
            {
            }

            var storedCredential = new GatewayCredential(
                "stored-oauth-test-only",
                IsPersonalAccessToken: false,
                AccountKey: accountKey,
                ChatGptAccountId: "account-target",
                AllowIncomingChatGptIdentity: false);
            var refreshedCredential = storedCredential with
            {
                Token = "refreshed-oauth-test-only"
            };
            var unrelatedCredential = refreshedCredential with
            {
                ChatGptAccountId = "account-other"
            };
            if (!OfficialOAuthCredentialBelongsToAccount(
                    refreshedCredential,
                    storedCredential.ChatGptAccountId!) ||
                OfficialOAuthCredentialBelongsToAccount(
                    unrelatedCredential,
                    storedCredential.ChatGptAccountId!))
            {
                throw new InvalidOperationException(
                    "OAuth refresh selection must require exact account_id equality.");
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

    private static string BuildTestJwt(DateTimeOffset expiresAtUtc)
    {
        static string Base64Url(string value) => Convert
            .ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." +
               Base64Url("{\"exp\":" + expiresAtUtc.ToUnixTimeSeconds() + "}") + "." +
               "signature-test-only-not-real";
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

    internal int Run()
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

            // A Windows mutex is thread-affine: the same thread that calls WaitOne must
            // call ReleaseMutex. Keep acquisition/release in this synchronous wrapper and
            // block it on the async listener. Releasing from an async continuation caused
            // the old gateway to crash exactly when a safe upgrade/shutdown was requested.
            return RunListenerAsync().GetAwaiter().GetResult();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private async Task<int> RunListenerAsync()
    {
        try
        {
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
            if (path.Equals("/" + LocalPatGateway.RotationArmPath, StringComparison.OrdinalIgnoreCase))
            {
                await HandleRotationArmAsync(context.Request, response);
                return;
            }
            if (path.Equals("/" + LocalPatGateway.RotationClearPath, StringComparison.OrdinalIgnoreCase))
            {
                await HandleRotationClearAsync(context.Request, response);
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
            using var requestDeadline = CreateRequestDeadline(context.Request);
            var requestCancellationToken = requestDeadline?.Token ?? CancellationToken.None;

            var credential = ReadBearerCredential(context.Request);
            if (credential == null)
            {
                await WriteErrorAsync(
                    response,
                    HttpStatusCode.Unauthorized,
                    "请求没有携带可用的 Codex PAT 或 ChatGPT OAuth Bearer。");
                return;
            }
            credential = BindConfiguredAccountKey(credential);

            var isModelRequest = IsModelRequest(context.Request, upstreamUri);
            if (isModelRequest && !credential.IsCompatibleApi)
            {
                credential = await ApplyRotationAtRequestBoundaryAsync(
                    credential,
                    requestCancellationToken);
            }
            var chatGptUpstreamUri = upstreamUri;
            ReplayableModelRequestBody? replayableBody = null;
            if (isModelRequest && context.Request.HasEntityBody)
            {
                try
                {
                    replayableBody = await ReadReplayableModelRequestBodyAsync(
                        context.Request,
                        requestCancellationToken);
                }
                catch (InvalidDataException ex)
                {
                    await WriteErrorAsync(
                        response,
                        HttpStatusCode.RequestEntityTooLarge,
                        "模型请求无法安全缓冲：" + SanitizeNetworkError(ex.Message));
                    return;
                }
            }
            using var modelRequestActivity = isModelRequest
                ? BeginModelRequest(credential.AccountKey)
                : null;
            var attemptedAccountKeys = new HashSet<string>(StringComparer.Ordinal);
            var retrySourceAccountKey = credential.AccountKey;
            var allowCompatibleApiRetry = CanRewriteCompatibleApiRequestBody(
                context.Request,
                replayableBody);
            var transparentRetryCount = 0;
            HttpResponseMessage? lastQuotaResponse = null;
            GatewayCredential? lastQuotaCredential = null;
            HttpResponseMessage? finalUpstreamResponse = null;

            while (finalUpstreamResponse == null)
            {
                modelRequestActivity?.UpdateAccountKey(credential.AccountKey);
                upstreamUri = chatGptUpstreamUri;
                if (credential.IsCompatibleApi &&
                    !TryBuildCompatibleApiUpstreamUri(
                        credential.CompatibleApiBaseUri!,
                        context.Request.Url,
                        out upstreamUri))
                {
                    if (lastQuotaResponse != null &&
                        TrySelectNextTransparentRotationCredential(
                            credential,
                            attemptedAccountKeys,
                            allowCompatibleApiRetry,
                            out var nextAfterMappingFailure,
                            out var skippedOrdinal,
                            out _))
                    {
                        WriteTransparentRotationDiagnostic(
                            "candidate-skipped",
                            skippedOrdinal,
                            "unsafe-api-mapping");
                        credential = nextAfterMappingFailure!;
                        transparentRetryCount++;
                        continue;
                    }

                    if (lastQuotaResponse != null)
                    {
                        finalUpstreamResponse = lastQuotaResponse;
                        lastQuotaResponse = null;
                        credential = lastQuotaCredential!;
                        break;
                    }
                    await WriteErrorAsync(
                        response,
                        HttpStatusCode.BadGateway,
                        "兼容 API 的模型请求地址无法安全映射。");
                    return;
                }

                var useDirectConnection = credential.IsCompatibleApi &&
                                          LocalProxyDetector.IsLoopbackHost(upstreamUri.Host);
                var proxyUri = useDirectConnection ? null : ResolveRequiredProxyUri();
                if (!useDirectConnection && proxyUri == null)
                {
                    if (lastQuotaResponse != null)
                    {
                        finalUpstreamResponse = lastQuotaResponse;
                        lastQuotaResponse = null;
                        credential = lastQuotaCredential!;
                        break;
                    }
                    await WriteErrorAsync(
                        response,
                        HttpStatusCode.ServiceUnavailable,
                        "未检测到可用的本地代理；为防止意外直连，上游请求已停止。");
                    return;
                }
                var clientKey = useDirectConnection
                    ? "direct"
                    : "proxy:" + proxyUri!.AbsoluteUri;
                var client = _clients.GetOrAdd(
                    clientKey,
                    _ => CreateUpstreamClient(proxyUri));
                PatIdentity? identity = string.IsNullOrWhiteSpace(credential.ChatGptAccountId)
                    ? null
                    : new PatIdentity(credential.ChatGptAccountId, IsFedRamp: false);
                if (credential.IsPersonalAccessToken)
                {
                    try
                    {
                        identity = await GetIdentityAsync(
                            client,
                            credential.Token,
                            requestCancellationToken);
                    }
                    catch (PatRejectedException ex)
                    {
                        if (lastQuotaResponse != null &&
                            TrySelectNextTransparentRotationCredential(
                                credential,
                                attemptedAccountKeys,
                                allowCompatibleApiRetry,
                                out var nextAfterPatRejection,
                                out var skippedOrdinal,
                                out _))
                        {
                            WriteTransparentRotationDiagnostic(
                                "candidate-skipped",
                                skippedOrdinal,
                                "credential-rejected");
                            credential = nextAfterPatRejection!;
                            transparentRetryCount++;
                            continue;
                        }

                        if (lastQuotaResponse != null)
                        {
                            finalUpstreamResponse = lastQuotaResponse;
                            lastQuotaResponse = null;
                            credential = lastQuotaCredential!;
                            break;
                        }
                        await WritePatRejectionErrorAsync(
                            response,
                            ex.StatusCode,
                            ex.IsInactiveWorkspaceMember);
                        return;
                    }
                    catch (Exception ex) when (
                        ex is HttpRequestException or TaskCanceledException or JsonException or
                        InvalidDataException)
                    {
                        if (lastQuotaResponse != null)
                        {
                            if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                            {
                                attemptedAccountKeys.Add(credential.AccountKey);
                            }
                            if (TrySelectNextTransparentRotationCredential(
                                    credential,
                                    attemptedAccountKeys,
                                    allowCompatibleApiRetry,
                                    out var nextAfterIdentityFailure,
                                    out var skippedOrdinal,
                                    out _))
                            {
                                WriteTransparentRotationDiagnostic(
                                    "candidate-skipped",
                                    skippedOrdinal,
                                    "identity-network-failure");
                                credential = nextAfterIdentityFailure!;
                                transparentRetryCount++;
                                continue;
                            }

                            finalUpstreamResponse = lastQuotaResponse;
                            lastQuotaResponse = null;
                            credential = lastQuotaCredential!;
                            break;
                        }
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.BadGateway,
                            "通过本地代理请求 OpenAI PAT 元数据失败：" +
                            SanitizeNetworkError(ex.Message));
                        return;
                    }
                }

                byte[]? requestBody = replayableBody?.Bytes;
                var requestBodyWasRewritten = false;
                if (credential.IsCompatibleApi)
                {
                    try
                    {
                        if (!allowCompatibleApiRetry || replayableBody == null)
                        {
                            throw new InvalidDataException("请求体不能转换到兼容 API。");
                        }
                        requestBody = RewriteCompatibleApiRequestBody(
                            replayableBody.Bytes,
                            credential.CompatibleApiModel!);
                        requestBodyWasRewritten = true;
                    }
                    catch (InvalidDataException ex)
                    {
                        if (lastQuotaResponse != null &&
                            TrySelectNextTransparentRotationCredential(
                                credential,
                                attemptedAccountKeys,
                                allowCompatibleApiRetry,
                                out var nextAfterBodyFailure,
                                out var skippedOrdinal,
                                out _))
                        {
                            WriteTransparentRotationDiagnostic(
                                "candidate-skipped",
                                skippedOrdinal,
                                "api-body-conversion");
                            credential = nextAfterBodyFailure!;
                            transparentRetryCount++;
                            continue;
                        }

                        if (lastQuotaResponse != null)
                        {
                            finalUpstreamResponse = lastQuotaResponse;
                            lastQuotaResponse = null;
                            credential = lastQuotaCredential!;
                            break;
                        }
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.BadRequest,
                            "兼容 API 请求无法安全转换：" + SanitizeNetworkError(ex.Message));
                        return;
                    }
                }

                using var upstreamRequest = BuildUpstreamRequest(
                    context.Request,
                    upstreamUri,
                    credential,
                    identity,
                    requestBody,
                    requestBodyWasRewritten,
                    IsFingerprintForwardingEnabled(credential));
                HttpResponseMessage attemptResponse;
                try
                {
                    attemptResponse = await client.SendAsync(
                        upstreamRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        requestCancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    if (lastQuotaResponse != null)
                    {
                        if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                        {
                            attemptedAccountKeys.Add(credential.AccountKey);
                        }
                        if (TrySelectNextTransparentRotationCredential(
                                credential,
                                attemptedAccountKeys,
                                allowCompatibleApiRetry,
                                out var nextAfterSendFailure,
                                out var skippedOrdinal,
                                out _))
                        {
                            WriteTransparentRotationDiagnostic(
                                "candidate-skipped",
                                skippedOrdinal,
                                "upstream-network-failure");
                            credential = nextAfterSendFailure!;
                            transparentRetryCount++;
                            continue;
                        }

                        finalUpstreamResponse = lastQuotaResponse;
                        lastQuotaResponse = null;
                        credential = lastQuotaCredential!;
                        break;
                    }
                    await WriteErrorAsync(
                        response,
                        HttpStatusCode.BadGateway,
                        "通过本地代理请求 ChatGPT Codex 上游失败：" +
                        SanitizeNetworkError(ex.Message));
                    return;
                }

                if (modelRequestActivity != null &&
                    attemptResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    RecordQuotaLimited(credential.AccountKey);
                    if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                    {
                        attemptedAccountKeys.Add(credential.AccountKey);
                    }

                    var canReplay = !context.Request.HasEntityBody || replayableBody != null;
                    if (canReplay &&
                        TrySelectNextTransparentRotationCredential(
                            credential,
                            attemptedAccountKeys,
                            allowCompatibleApiRetry,
                            out var nextCredential,
                            out var retryOrdinal,
                            out var retryPool))
                    {
                        lastQuotaResponse?.Dispose();
                        lastQuotaResponse = attemptResponse;
                        lastQuotaCredential = credential;
                        WriteTransparentRotationDiagnostic(
                            "retry-selected",
                            retryOrdinal,
                            retryPool == AccountRotationPool.Primary ? "primary" : "backup");
                        credential = nextCredential!;
                        transparentRetryCount++;
                        continue;
                    }

                    if (lastQuotaResponse != null)
                    {
                        lastQuotaResponse.Dispose();
                        lastQuotaResponse = null;
                    }
                    finalUpstreamResponse = attemptResponse;
                    ManagerLifecycleDiagnostics.Write(
                        "pat-gateway-transparent-retry-exhausted",
                        $"attempt_count={Math.Max(1, transparentRetryCount + 1)}; " +
                        $"replayable={canReplay}; downstream_bytes=0");
                    break;
                }

                if (lastQuotaResponse != null &&
                    (attemptResponse.StatusCode is HttpStatusCode.Unauthorized or
                        HttpStatusCode.Forbidden ||
                     credential.IsCompatibleApi && !attemptResponse.IsSuccessStatusCode))
                {
                    attemptResponse.Dispose();
                    if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                    {
                        attemptedAccountKeys.Add(credential.AccountKey);
                    }
                    if (TrySelectNextTransparentRotationCredential(
                            credential,
                            attemptedAccountKeys,
                            allowCompatibleApiRetry,
                            out var nextAfterRejectedResponse,
                            out var skippedOrdinal,
                            out _))
                    {
                        WriteTransparentRotationDiagnostic(
                            "candidate-skipped",
                            skippedOrdinal,
                            "non-success-response");
                        credential = nextAfterRejectedResponse!;
                        transparentRetryCount++;
                        continue;
                    }

                    finalUpstreamResponse = lastQuotaResponse;
                    lastQuotaResponse = null;
                    credential = lastQuotaCredential!;
                    break;
                }

                finalUpstreamResponse = attemptResponse;
                if (lastQuotaResponse != null)
                {
                    lastQuotaResponse.Dispose();
                    lastQuotaResponse = null;
                    if (attemptResponse.IsSuccessStatusCode &&
                        PatGatewayRotationStore.TryNormalizeAccountKey(
                            retrySourceAccountKey,
                            out var sourceAccountKey) &&
                        PatGatewayRotationStore.TryNormalizeAccountKey(
                            credential.AccountKey,
                            out var targetAccountKey) &&
                        !sourceAccountKey.Equals(targetAccountKey, StringComparison.Ordinal))
                    {
                        await CommitTransparentRotationAsync(
                            sourceAccountKey,
                            targetAccountKey,
                            requestCancellationToken);
                    }
                    ManagerLifecycleDiagnostics.Write(
                        "pat-gateway-transparent-retry-succeeded",
                        $"attempt_count={transparentRetryCount + 1}; downstream_bytes=0");
                }
            }

            modelRequestActivity?.UpdateAccountKey(credential.AccountKey);
            using (var upstreamResponse = finalUpstreamResponse ??
                                          throw new InvalidOperationException(
                                              "Upstream response selection ended without a response."))
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
                await CopyUpstreamResponseAsync(
                    upstreamResponse,
                    response,
                    requestCancellationToken);
            }
        }
        catch (Exception ex) when (
            ex is IOException or
            HttpListenerException or
            ObjectDisposedException or
            InvalidOperationException or
            InvalidDataException or
            JsonException)
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
        response.Headers[LocalPatGateway.RotationProtocolHeader] =
            LocalPatGateway.RotationProtocolValue;
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
        var activity = GetActivitySnapshot();
        PatGatewayRotationSnapshot rotation;
        lock (_rotationGate)
        {
            rotation = _rotationStore.Load();
        }
        await WriteJsonAsync(
            response,
            proxy == null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
            new
            {
                status = proxy == null ? "proxy_required" : "ready",
                proxyConfigured = proxy != null,
                listen = $"127.0.0.1:{LocalPatGateway.Port}",
                activity = new
                {
                    activeModelRequests = activity.ActiveModelRequests,
                    completedModelRequests = activity.CompletedModelRequests,
                    lastModelRequestAccountKey = activity.LastModelRequestAccountKey,
                    lastQuotaLimitedAccountKey = activity.LastQuotaLimitedAccountKey,
                    lastQuotaLimitedSequence = activity.LastQuotaLimitedSequence,
                    lastModelRequestStartedAtUnixMs = activity.LastModelRequestStartedAtUtc?.ToUnixTimeMilliseconds(),
                    lastModelRequestCompletedAtUnixMs = activity.LastModelRequestCompletedAtUtc?.ToUnixTimeMilliseconds(),
                    lastQuotaLimitedAtUnixMs = activity.LastQuotaLimitedAtUtc?.ToUnixTimeMilliseconds()
                },
                rotation = BuildRotationResponse(rotation)
            });
    }

    private async Task HandleRotationArmAsync(
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.MethodNotAllowed,
                "请使用 POST 准备 PAT 轮换。");
            return;
        }

        byte[] payload;
        try
        {
            payload = await ReadBoundedRequestBodyAsync(request, 4096);
        }
        catch (InvalidDataException)
        {
            await WriteErrorAsync(response, HttpStatusCode.BadRequest, "PAT 轮换请求格式无效。");
            return;
        }
        if (!LocalPatGatewayControl.ValidateRequest(
                request,
                _controlSecret,
                BuildRotationArmPurpose(payload)))
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.Unauthorized,
                "Gateway control request was not authenticated.");
            return;
        }

        RotationArmRequest? command;
        try
        {
            command = JsonSerializer.Deserialize<RotationArmRequest>(payload);
        }
        catch (JsonException)
        {
            command = null;
        }
        if (command == null ||
            !PatGatewayRotationStore.TryNormalizeAccountKey(
                command.SourceAccountKey,
                out var source) ||
            !PatGatewayRotationStore.TryNormalizeAccountKey(
                command.TargetAccountKey,
                out var target) ||
            source.Equals(target, StringComparison.Ordinal))
        {
            await WriteErrorAsync(response, HttpStatusCode.BadRequest, "PAT 轮换账号哈希无效。");
            return;
        }

        PatGatewayRotationSnapshot rotation;
        try
        {
            lock (_rotationGate)
            {
                // Resolve both sides from accounts.json/auth.json before persisting an armed
                // route. Neither credential crosses the HTTP control plane.
                _ = ResolveRotationCredential(source);
                _ = ResolveRotationCredential(target);
                rotation = _rotationStore.Arm(source, target, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or InvalidOperationException)
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.Conflict,
                "PAT 轮换路由无法准备：" + SanitizeNetworkError(ex.Message));
            return;
        }

        await WriteJsonAsync(
            response,
            HttpStatusCode.OK,
            new
            {
                status = "armed",
                rotation = BuildRotationResponse(rotation)
            });
    }

    private async Task HandleRotationClearAsync(
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.MethodNotAllowed,
                "请使用 POST 清理 PAT 轮换。");
            return;
        }
        if (!LocalPatGatewayControl.ValidateRequest(
                request,
                _controlSecret,
                "rotation-clear"))
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.Unauthorized,
                "Gateway control request was not authenticated.");
            return;
        }

        lock (_rotationGate)
        {
            _rotationStore.Clear();
        }
        await WriteJsonAsync(
            response,
            HttpStatusCode.OK,
            new
            {
                status = "cleared",
                rotation = BuildRotationResponse(PatGatewayRotationSnapshot.Empty)
            });
    }

    private async Task<GatewayCredential> ApplyRotationAtRequestBoundaryAsync(
        GatewayCredential incoming,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var waitForQuietBoundary = false;
            await _rotationActivationGate.WaitAsync(cancellationToken);
            try
            {
                lock (_rotationGate)
                {
                    var route = _rotationStore.Load();
                    if (route.Status != PatGatewayRotationStatus.Armed ||
                        !IncomingMatchesRotationTransport(incoming, route))
                    {
                        return SelectRotationCredentialAtRequestBoundary(
                            incoming,
                            route,
                            ResolveRotationCredential,
                            () => _rotationStore.Activate(route, DateTimeOffset.UtcNow));
                    }

                    // This request is deliberately not counted as active yet.  Do not hold
                    // the activation gate while older requests drain: a transparent retry
                    // from one of those requests must be able to acquire the same gate and
                    // commit the winner, otherwise the two paths can deadlock indefinitely.
                    if (GetActivitySnapshot().ActiveModelRequests == 0)
                    {
                        route = _rotationStore.Load();
                        if (route.Status != PatGatewayRotationStatus.Armed ||
                            !IncomingMatchesRotationTransport(incoming, route))
                        {
                            return SelectRotationCredentialAtRequestBoundary(
                                incoming,
                                route,
                                ResolveRotationCredential,
                                () => _rotationStore.Activate(route, DateTimeOffset.UtcNow));
                        }

                        // Re-read the activity counter immediately before activation.  The
                        // request-boundary candidate is not counted until this method returns,
                        // so a newly arriving request will observe the activated route and use
                        // the same target rather than reviving the source account.
                        if (GetActivitySnapshot().ActiveModelRequests == 0)
                        {
                            return SelectRotationCredentialAtRequestBoundary(
                                incoming,
                                route,
                                ResolveRotationCredential,
                                () => _rotationStore.Activate(route, DateTimeOffset.UtcNow));
                        }
                    }

                    waitForQuietBoundary = true;
                }
            }
            finally
            {
                _rotationActivationGate.Release();
            }

            if (waitForQuietBoundary)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }

    private bool IncomingMatchesRotationTransport(
        GatewayCredential incoming,
        PatGatewayRotationSnapshot route)
    {
        if (string.IsNullOrWhiteSpace(route.TransportAccountKey))
        {
            return false;
        }
        var transport = ResolveRotationCredential(route.TransportAccountKey);
        return TokenHashesEqual(incoming.Token, transport.Token);
    }

    private static GatewayCredential SelectRotationCredentialAtRequestBoundary(
        GatewayCredential incoming,
        PatGatewayRotationSnapshot route,
        Func<string, GatewayCredential> resolveCredential,
        Action activate)
    {
        if (route.Status == PatGatewayRotationStatus.None ||
            string.IsNullOrWhiteSpace(route.TransportAccountKey) ||
            string.IsNullOrWhiteSpace(route.TargetAccountKey))
        {
            return incoming;
        }

        var transport = resolveCredential(route.TransportAccountKey);
        if (!TokenHashesEqual(incoming.Token, transport.Token))
        {
            // Quota reads, account tests, and a manually reconfigured desktop request
            // must never be silently redirected by a stale route.
            return incoming;
        }

        var target = resolveCredential(route.TargetAccountKey);
        if (route.Status == PatGatewayRotationStatus.Armed)
        {
            activate();
        }
        return target;
    }

    private bool TrySelectNextTransparentRotationCredential(
        GatewayCredential current,
        ISet<string> attemptedAccountKeys,
        bool allowCompatibleApi,
        out GatewayCredential? credential,
        out int candidateOrdinal,
        out AccountRotationPool pool)
    {
        credential = null;
        candidateOrdinal = 0;
        pool = AccountRotationPool.None;
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(
                current.AccountKey,
                out var currentAccountKey))
        {
            return false;
        }

        IReadOnlyList<AccountRecord> accounts;
        AppSettings settings;
        try
        {
            accounts = _accountStore.LoadAccounts();
            settings = _themeService.LoadSettings();
            _ = AccountRotationConfiguration.Normalize(settings, accounts);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or InvalidOperationException or NotSupportedException or
            ArgumentException)
        {
            return false;
        }
        if (!AccountRotationConfiguration.IsEnabled(settings))
        {
            return false;
        }

        // A duplicated local record must not abort the gateway's 429 recovery path.
        // Normalization will repair ordering/configuration later; for this request, use
        // the first matching credential and keep the transparent retry chain alive.
        var currentAccount = accounts.FirstOrDefault(account =>
            QuotaAccountIdentity.CreateKey(account).Equals(
                currentAccountKey,
                StringComparison.Ordinal));
        if (currentAccount == null ||
            AccountRotationConfiguration.GetPool(settings, currentAccount) ==
                AccountRotationPool.None)
        {
            return false;
        }

        // The route itself is the authoritative current cursor. Mark it only in this
        // in-memory settings snapshot so a Manager write racing this request cannot be
        // overwritten by the gateway. The Manager reconciles the committed active route
        // and durably persists the same cursor after a successful retry.
        AccountRotationConfiguration.MarkUsed(settings, currentAccount);
        var now = DateTimeOffset.UtcNow;
        var unavailable = new HashSet<string>(attemptedAccountKeys, StringComparer.Ordinal);
        foreach (var observed in _quotaLimitedAccountKeys)
        {
            var graceElapsed = settings.AccountRotationResetAtUtc.TryGetValue(
                                   observed.Key,
                                   out var resetAtUtc) &&
                               resetAtUtc + AccountRotationConfiguration.PrimaryResetGracePeriod <= now;
            if (!graceElapsed &&
                now - observed.Value <= PatGatewayQuotaSignalStore.SignalLifetime)
            {
                unavailable.Add(observed.Key);
            }
            else
            {
                _quotaLimitedAccountKeys.TryRemove(observed.Key, out _);
            }
        }

        while (true)
        {
            var candidates = AccountRotationConfiguration.BuildCandidates(
                settings,
                accounts,
                currentAccount,
                unavailable,
                now,
                hasUsableCredential: static _ => true,
                canSelectAccount: account => allowCompatibleApi || !account.IsCompatibleApi,
                hardUnavailableAccountKeys: attemptedAccountKeys);
            if (candidates.Count == 0)
            {
                return false;
            }

            var candidate = candidates[0];
            var candidateKey = QuotaAccountIdentity.CreateKey(candidate);
            unavailable.Add(candidateKey);
            attemptedAccountKeys.Add(candidateKey);
            candidateOrdinal = Math.Max(1, attemptedAccountKeys.Count);
            pool = AccountRotationConfiguration.GetPool(settings, candidate);
            try
            {
                credential = ResolveRotationCredential(candidateKey);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or NotSupportedException or
                ArgumentException or FormatException)
            {
                WriteTransparentRotationDiagnostic(
                    "candidate-skipped",
                    candidateOrdinal,
                    "local-credential-unavailable");
            }
        }
    }

    private async Task CommitTransparentRotationAsync(
        string sourceAccountKey,
        string targetAccountKey,
        CancellationToken cancellationToken)
    {
        await _rotationActivationGate.WaitAsync(cancellationToken);
        try
        {
            lock (_rotationGate)
            {
                var route = _rotationStore.Load();
                if (route.Status == PatGatewayRotationStatus.Active &&
                    string.Equals(
                        route.TargetAccountKey,
                        targetAccountKey,
                        StringComparison.Ordinal))
                {
                    // A concurrent request already committed the same winner.
                    return;
                }

                var logicalAccountKey = route.Status switch
                {
                    PatGatewayRotationStatus.Active => route.TargetAccountKey,
                    PatGatewayRotationStatus.Armed => route.SourceAccountKey,
                    _ => sourceAccountKey
                };
                if (!string.Equals(
                        logicalAccountKey,
                        sourceAccountKey,
                        StringComparison.Ordinal))
                {
                    // Never move an active route backwards when another request won a
                    // concurrent quota race first. Its response may still complete using
                    // this target, while all later requests follow the newer active route.
                    ManagerLifecycleDiagnostics.Write(
                        "pat-gateway-transparent-route-kept-concurrent-winner",
                        "downstream_bytes=0");
                    return;
                }

                PatGatewayRotationSnapshot armed;
                if (route.Status == PatGatewayRotationStatus.Armed &&
                    string.Equals(
                        route.SourceAccountKey,
                        sourceAccountKey,
                        StringComparison.Ordinal))
                {
                    if (string.Equals(
                            route.TargetAccountKey,
                            targetAccountKey,
                            StringComparison.Ordinal))
                    {
                        armed = route;
                    }
                    else
                    {
                        // A Manager-side quota poll can arm a slower preflight route while
                        // v5 is already retrying the same 429. The successful in-request
                        // winner is newer and must replace that still-unactivated route.
                        _rotationStore.Clear();
                        armed = _rotationStore.Arm(
                            sourceAccountKey,
                            targetAccountKey,
                            DateTimeOffset.UtcNow);
                    }
                }
                else
                {
                    armed = _rotationStore.Arm(
                        sourceAccountKey,
                        targetAccountKey,
                        DateTimeOffset.UtcNow);
                }
                _ = _rotationStore.Activate(armed, DateTimeOffset.UtcNow);
                ManagerLifecycleDiagnostics.Write(
                    "pat-gateway-transparent-route-activated",
                    "downstream_bytes=0");
            }
        }
        finally
        {
            _rotationActivationGate.Release();
        }
    }

    private static void WriteTransparentRotationDiagnostic(
        string eventSuffix,
        int candidateOrdinal,
        string reason)
    {
        ManagerLifecycleDiagnostics.Write(
            "pat-gateway-transparent-" + eventSuffix,
            $"candidate_ordinal={Math.Max(1, candidateOrdinal)}; reason={reason}; downstream_bytes=0");
    }

    private GatewayCredential ResolveRotationCredential(string accountKey)
    {
        var matches = _accountStore.LoadAccounts()
            .Where(candidate => QuotaAccountIdentity.CreateKey(candidate).Equals(
                accountKey,
                StringComparison.Ordinal))
            .Take(2)
            .ToList();
        if (matches.Count == 0)
        {
            throw new InvalidDataException("Rotation account is missing or has an unsupported authentication kind.");
        }
        if (matches.Count > 1)
        {
            // An ambiguous key must fail closed.  Silently selecting the first record
            // could send one account's request through another account's credential.
            throw new InvalidDataException("Rotation account key is duplicated in the local account store.");
        }
        var account = matches[0];

        if (account.IsOfficialOAuth)
        {
            // Resolve OAuth before reading the generic access-token field. The account
            // snapshot may be expired while the live shared profile already contains a
            // valid refresh; forcing the stale read first would prevent that safe match.
            return ResolveOfficialOAuthRotationCredential(
                account,
                accountKey);
        }

        var token = CodexCliService.ReadAccessTokenCredential(
            Path.Combine(account.CodexHome, "auth.json"));
        if (account.IsCompatibleApi)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            {
                throw new InvalidDataException("Compatible API rotation account does not contain a usable API credential.");
            }
            if (!account.ApiWireApi.Equals("responses", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Compatible API rotation requires the Responses wire API.");
            }
            if (CodexCliService.GetCompatibleApiModelIdValidationError(account.ApiModel) != null)
            {
                throw new InvalidDataException("Compatible API rotation account has an invalid model identifier.");
            }
            if (!Uri.TryCreate(account.ApiBaseUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme is not ("http" or "https") ||
                (baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                 !LocalProxyDetector.IsLoopbackHost(baseUri.Host)) ||
                string.IsNullOrWhiteSpace(baseUri.Host) ||
                !string.IsNullOrEmpty(baseUri.UserInfo) ||
                !string.IsNullOrEmpty(baseUri.Query) ||
                !string.IsNullOrEmpty(baseUri.Fragment))
            {
                throw new InvalidDataException("Compatible API rotation account has an invalid base URL.");
            }
            return new GatewayCredential(
                token,
                IsPersonalAccessToken: false,
                CompatibleApiBaseUri: baseUri,
                CompatibleApiModel: account.ApiModel.Trim(),
                AccountKey: accountKey,
                AllowIncomingChatGptIdentity: false);
        }

        var credential = ParseBearerCredential("Bearer " + token);
        if (credential is not { IsPersonalAccessToken: true })
        {
            throw new InvalidDataException("PAT rotation account does not contain a usable PAT credential.");
        }
        return credential with
        {
            AccountKey = accountKey,
            AllowIncomingChatGptIdentity = false
        };
    }

    private GatewayCredential ResolveOfficialOAuthRotationCredential(
        AccountRecord account,
        string accountKey)
    {
        var accountAuthPath = Path.Combine(account.CodexHome, "auth.json");
        var expectedAccountId = TryReadOfficialOAuthAccountId(accountAuthPath);
        if (!string.IsNullOrWhiteSpace(expectedAccountId))
        {
            var sharedAuthPath = Path.Combine(
                CodexCliService.GetDefaultCodexHome(),
                "auth.json");
            if (!PathsReferToSameFile(accountAuthPath, sharedAuthPath) &&
                TryReadOfficialOAuthRotationCredential(
                    sharedAuthPath,
                    accountKey,
                    account.Name) is { } sharedCredential &&
                OfficialOAuthCredentialBelongsToAccount(
                    sharedCredential,
                    expectedAccountId))
            {
                return sharedCredential;
            }
        }

        // If the shared file is absent, expired, malformed, or belongs to another
        // account_id, use only the exact account snapshot. ReadOfficial... performs the
        // normal expiry and canonical-auth validation and fails closed when that snapshot
        // cannot safely authenticate the request.
        return ReadOfficialOAuthRotationCredential(
            accountAuthPath,
            accountKey,
            account.Name);
    }

    private static GatewayCredential? TryReadOfficialOAuthRotationCredential(
        string authPath,
        string accountKey,
        string displayName)
    {
        try
        {
            return ReadOfficialOAuthRotationCredential(authPath, accountKey, displayName);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or FormatException or ArgumentException or
            NotSupportedException)
        {
            // A shared profile is only an optional refreshed-token source. Any read or
            // validation failure leaves the exact per-account snapshot as the sole
            // candidate; it never makes another account's token eligible.
            return null;
        }
    }

    private static bool OfficialOAuthCredentialBelongsToAccount(
        GatewayCredential credential,
        string expectedAccountId) =>
        !string.IsNullOrWhiteSpace(credential.ChatGptAccountId) &&
        credential.ChatGptAccountId.Equals(expectedAccountId, StringComparison.Ordinal);

    private static string? TryReadOfficialOAuthAccountId(string authPath)
    {
        if (!CodexCliService.IsOfficialOAuthCredentialFile(authPath))
        {
            return null;
        }

        try
        {
            using var input = new FileStream(
                authPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object ||
                !tokens.TryGetProperty("account_id", out var accountIdElement) ||
                accountIdElement.ValueKind != JsonValueKind.String ||
                !IsSafeChatGptAccountId(accountIdElement.GetString(), out var accountId))
            {
                return null;
            }

            return accountId;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or FormatException or ArgumentException or
            NotSupportedException)
        {
            return null;
        }
    }

    private static bool PathsReferToSameFile(string first, string second)
    {
        try
        {
            var firstFull = Path.GetFullPath(first)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var secondFull = Path.GetFullPath(second)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return firstFull.Equals(secondFull, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return first.Equals(second, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static GatewayCredential ReadOfficialOAuthRotationCredential(
        string authPath,
        string accountKey,
        string displayName)
    {
        if (!CodexCliService.IsOfficialOAuthCredentialFile(authPath))
        {
            throw new InvalidDataException(
                $"Official OAuth rotation account {displayName} does not contain a complete OAuth login.");
        }

        // This read also runs the repository's canonical JSON validation, which rejects
        // duplicate credential keys before any secret can be selected.
        var token = CodexCliService.ReadAccessTokenCredential(authPath);
        var parsed = ParseBearerCredential("Bearer " + token);
        if (parsed is null or { IsPersonalAccessToken: true } ||
            IsJwtExpiredOrExpiring(token, TimeSpan.FromMinutes(2)))
        {
            throw new InvalidDataException(
                $"Official OAuth rotation account {displayName} has an expired or invalid access token.");
        }

        using var input = new FileStream(
            authPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        if (!root.TryGetProperty("tokens", out var tokens) ||
            tokens.ValueKind != JsonValueKind.Object ||
            !tokens.TryGetProperty("account_id", out var accountIdElement) ||
            accountIdElement.ValueKind != JsonValueKind.String ||
            !IsSafeChatGptAccountId(accountIdElement.GetString(), out var accountId))
        {
            throw new InvalidDataException(
                $"Official OAuth rotation account {displayName} is missing a safe ChatGPT account id.");
        }

        return parsed with
        {
            AccountKey = accountKey,
            ChatGptAccountId = accountId,
            AllowIncomingChatGptIdentity = false
        };
    }

    private GatewayCredential BindConfiguredAccountKey(GatewayCredential credential)
    {
        if (!string.IsNullOrWhiteSpace(credential.AccountKey))
        {
            return credential;
        }

        var accounts = _accountStore.LoadAccounts();
        var matchingAccounts = new List<AccountRecord>();
        foreach (var account in accounts)
        {
            if (account.IsCompatibleApi)
            {
                continue;
            }
            try
            {
                var configuredToken = CodexCliService.ReadAccessTokenCredential(
                    Path.Combine(account.CodexHome, "auth.json"));
                if (TokenHashesEqual(credential.Token, configuredToken))
                {
                    matchingAccounts.Add(account);
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or FormatException or ArgumentException or
                NotSupportedException)
            {
                // A damaged unrelated account cannot make an otherwise valid gateway
                // request fail. It simply cannot opt into metadata forwarding.
            }
        }

        // OAuth access tokens can be refreshed in the live shared profile without
        // rewriting the account snapshot immediately. Bind such a token by exact
        // token + account_id equality, and only when one local OAuth record owns that
        // identity. A different account_id (or duplicate records) stays unbound.
        if (matchingAccounts.Count == 0)
        {
            var sharedAuthPath = Path.Combine(
                CodexCliService.GetDefaultCodexHome(),
                "auth.json");
            var sharedCredential = TryReadOfficialOAuthRotationCredential(
                sharedAuthPath,
                accountKey: "",
                displayName: "共享官方 OAuth");
            if (sharedCredential != null &&
                TokenHashesEqual(credential.Token, sharedCredential.Token) &&
                !string.IsNullOrWhiteSpace(sharedCredential.ChatGptAccountId))
            {
                var sharedIdentityMatches = accounts
                    .Where(candidate => candidate.IsOfficialOAuth)
                    .Where(candidate =>
                        TryReadOfficialOAuthAccountId(
                            Path.Combine(candidate.CodexHome, "auth.json")) is { } accountId &&
                        accountId.Equals(
                            sharedCredential.ChatGptAccountId,
                            StringComparison.Ordinal))
                    .ToList();
                if (sharedIdentityMatches.Count == 1)
                {
                    return sharedCredential with
                    {
                        AccountKey = QuotaAccountIdentity.CreateKey(sharedIdentityMatches[0]),
                        AllowIncomingChatGptIdentity = false
                    };
                }
            }

            return credential;
        }

        // Duplicate local records for one credential are intentionally treated as
        // ambiguous. A per-account opt-in must never inherit another record's setting.
        if (matchingAccounts.Count != 1)
        {
            return credential;
        }

        var matched = matchingAccounts[0];
        var accountKey = QuotaAccountIdentity.CreateKey(matched);
        return matched.IsOfficialOAuth
            ? credential with
            {
                // The incoming token was compared byte-for-byte with this account's
                // snapshot above. Preserve that exact token and bind only the account
                // key; resolving a possibly newer shared refresh here could otherwise
                // turn a quota/status request into a different token.
                AccountKey = accountKey,
                ChatGptAccountId = TryReadOfficialOAuthAccountId(
                    Path.Combine(matched.CodexHome, "auth.json")),
                AllowIncomingChatGptIdentity = false
            }
            : credential with
            {
                AccountKey = accountKey,
                AllowIncomingChatGptIdentity = false
            };
    }

    private bool IsFingerprintForwardingEnabled(GatewayCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.AccountKey))
        {
            return false;
        }

        var settings = _themeService.LoadSettings();
        return settings.CodexFingerprintForwarding?.TryGetValue(
                   credential.AccountKey,
                   out var enabled) == true && enabled;
    }

    private static bool TokenHashesEqual(string first, string second)
    {
        var firstHash = SHA256.HashData(Encoding.UTF8.GetBytes(first));
        var secondHash = SHA256.HashData(Encoding.UTF8.GetBytes(second));
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static string BuildRotationArmPurpose(ReadOnlySpan<byte> payload) =>
        "rotation-arm\n" + Convert.ToHexString(SHA256.HashData(payload));

    private static async Task<byte[]> ReadBoundedRequestBodyAsync(
        HttpListenerRequest request,
        int maximumBytes)
    {
        if (!request.HasEntityBody ||
            request.ContentLength64 < 0 ||
            request.ContentLength64 > maximumBytes)
        {
            throw new InvalidDataException("Control request body length is invalid.");
        }

        var expectedLength = checked((int)request.ContentLength64);
        var payload = new byte[expectedLength];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await request.InputStream.ReadAsync(payload.AsMemory(offset));
            if (read == 0)
            {
                break;
            }
            offset += read;
        }
        if (offset != payload.Length)
        {
            throw new InvalidDataException("Control request body was truncated.");
        }
        return payload;
    }

    private static object BuildRotationResponse(PatGatewayRotationSnapshot rotation) => new
    {
        status = rotation.Status switch
        {
            PatGatewayRotationStatus.Armed => "armed",
            PatGatewayRotationStatus.Active => "active",
            _ => "none"
        },
        transportAccountKey = rotation.TransportAccountKey,
        sourceAccountKey = rotation.SourceAccountKey,
        targetAccountKey = rotation.TargetAccountKey,
        armedAtUnixMs = rotation.ArmedAtUtc?.ToUnixTimeMilliseconds(),
        activatedAtUnixMs = rotation.ActivatedAtUtc?.ToUnixTimeMilliseconds()
    };

    private static bool IsModelRequest(HttpListenerRequest incoming, Uri upstreamUri)
    {
        if (!incoming.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = upstreamUri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase);
    }

    private ModelRequestActivity BeginModelRequest(string? accountKey)
    {
        lock (_activityGate)
        {
            _activeModelRequests++;
            _lastModelRequestAccountKey = NormalizeOptionalAccountKey(accountKey);
            _lastModelRequestStartedAtUtc = DateTimeOffset.UtcNow;
        }
        return new ModelRequestActivity(this, NormalizeOptionalAccountKey(accountKey));
    }

    private void UpdateModelRequestAccountKey(string? accountKey)
    {
        lock (_activityGate)
        {
            _lastModelRequestAccountKey = NormalizeOptionalAccountKey(accountKey);
        }
    }

    private void EndModelRequest(string? accountKey)
    {
        lock (_activityGate)
        {
            _activeModelRequests = Math.Max(0, _activeModelRequests - 1);
            _completedModelRequests++;
            if (!string.IsNullOrWhiteSpace(accountKey))
            {
                _completedModelRequestsByAccount[accountKey] =
                    _completedModelRequestsByAccount.GetValueOrDefault(accountKey) + 1L;
                _lastModelRequestAccountKey = accountKey;
            }
            _lastModelRequestCompletedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void RecordQuotaLimited(string? accountKey)
    {
        var normalizedAccountKey = NormalizeOptionalAccountKey(accountKey);
        var observedAtUtc = DateTimeOffset.UtcNow;
        PatGatewayQuotaSignal? durableSignal = null;
        if (normalizedAccountKey != null)
        {
            _quotaLimitedAccountKeys[normalizedAccountKey] = observedAtUtc;
            try
            {
                // Commit before forwarding the 429 response. If the gateway or Manager
                // exits immediately afterwards, the next Manager process can still arm a
                // route for the next model-request boundary. A persistence failure never
                // replaces the real upstream 429 with a local gateway error.
                durableSignal = _quotaSignalStore.Record(
                    normalizedAccountKey,
                    observedAtUtc);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException or OverflowException)
            {
                // The authenticated in-memory health signal remains available as a
                // best-effort fallback for this gateway process.
            }
        }

        lock (_activityGate)
        {
            _lastQuotaLimitedAccountKey = normalizedAccountKey;
            _lastQuotaLimitedAtUtc = durableSignal?.ObservedAtUtc ?? observedAtUtc;
            _lastQuotaLimitedSequence = durableSignal?.Sequence;
        }
        ManagerLifecycleDiagnostics.Write(
            "pat-gateway-http-429-detected",
            "downstream_bytes=0");
    }

    private LocalPatGatewayActivitySnapshot GetActivitySnapshot()
    {
        lock (_activityGate)
        {
            var completedForLastAccount = !string.IsNullOrWhiteSpace(_lastModelRequestAccountKey)
                ? _completedModelRequestsByAccount.GetValueOrDefault(_lastModelRequestAccountKey)
                : _completedModelRequests;
            return new LocalPatGatewayActivitySnapshot(
                _activeModelRequests,
                _lastModelRequestStartedAtUtc,
                _lastModelRequestCompletedAtUtc,
                _lastQuotaLimitedAtUtc,
                Rotation: null,
                completedForLastAccount,
                _lastModelRequestAccountKey,
                _lastQuotaLimitedAccountKey,
                _lastQuotaLimitedSequence);
        }
    }

    private static string? NormalizeOptionalAccountKey(string? value) =>
        PatGatewayRotationStore.TryNormalizeAccountKey(value, out var normalized)
            ? normalized
            : null;

    private sealed class ModelRequestActivity : IDisposable
    {
        private LocalPatGatewayHost? _owner;
        private string? _accountKey;

        internal ModelRequestActivity(LocalPatGatewayHost owner, string? accountKey)
        {
            _owner = owner;
            _accountKey = accountKey;
        }

        internal void UpdateAccountKey(string? accountKey)
        {
            var normalized = NormalizeOptionalAccountKey(accountKey);
            _accountKey = normalized;
            _owner?.UpdateModelRequestAccountKey(normalized);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndModelRequest(_accountKey);
        }
    }

    private async Task<PatIdentity> GetIdentityAsync(
        HttpClient client,
        string token,
        CancellationToken cancellationToken = default)
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
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

    private static async Task<ReplayableModelRequestBody> ReadReplayableModelRequestBodyAsync(
        HttpListenerRequest incoming,
        CancellationToken cancellationToken)
    {
        if (!incoming.HasEntityBody)
        {
            return new ReplayableModelRequestBody([]);
        }
        if (incoming.ContentLength64 > ReplayableModelRequestBodyMaxBytes)
        {
            throw new InvalidDataException("请求体超过 128 MiB 内存缓冲上限。");
        }

        var initialCapacity = incoming.ContentLength64 is >= 0 and <= int.MaxValue
            ? (int)incoming.ContentLength64
            : 0;
        using var buffer = new MemoryStream(initialCapacity);
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await incoming.InputStream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > ReplayableModelRequestBodyMaxBytes)
            {
                throw new InvalidDataException("请求体超过 128 MiB 内存缓冲上限。");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return new ReplayableModelRequestBody(buffer.ToArray());
    }

    private static bool CanRewriteCompatibleApiRequestBody(
        HttpListenerRequest incoming,
        ReplayableModelRequestBody? body)
    {
        if (!incoming.HasEntityBody || body == null)
        {
            return false;
        }
        var contentEncoding = incoming.Headers["Content-Encoding"]?.Trim();
        return string.IsNullOrWhiteSpace(contentEncoding) ||
               contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] RewriteCompatibleApiRequestBody(
        ReadOnlySpan<byte> body,
        string model)
    {
        if (string.IsNullOrWhiteSpace(model) ||
            CodexCliService.GetCompatibleApiModelIdValidationError(model) != null)
        {
            throw new InvalidDataException("目标兼容 API 模型无效。");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 256
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("POST /responses 请求体必须是 JSON 对象。");
            }

            using var output = new MemoryStream(body.Length + Math.Min(model.Length + 32, 512));
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                var modelCount = 0;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("model"))
                    {
                        modelCount++;
                        if (modelCount > 1)
                        {
                            throw new InvalidDataException("请求体包含重复的 model 字段。");
                        }
                        writer.WriteString("model", model.Trim());
                        continue;
                    }
                    property.WriteTo(writer);
                }
                if (modelCount == 0)
                {
                    writer.WriteString("model", model.Trim());
                }
                writer.WriteEndObject();
            }
            return output.ToArray();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("请求体不是有效 JSON。");
        }
    }

    private static HttpRequestMessage BuildUpstreamRequest(
        HttpListenerRequest incoming,
        Uri upstreamUri,
        GatewayCredential credential,
        PatIdentity? identity,
        byte[]? requestBody = null,
        bool requestBodyWasRewritten = false,
        bool forwardClientMetadata = false)
    {
        var request = new HttpRequestMessage(new HttpMethod(incoming.HttpMethod), upstreamUri);
        if (incoming.HasEntityBody)
        {
            request.Content = requestBody == null
                ? new StreamContent(incoming.InputStream)
                : new ByteArrayContent(requestBody);
            var contentType = !requestBodyWasRewritten
                ? incoming.ContentType
                : "application/json; charset=utf-8";
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            if (requestBody == null && incoming.ContentLength64 >= 0)
            {
                request.Content.Headers.ContentLength = incoming.ContentLength64;
            }
        }

        foreach (var headerName in incoming.Headers.AllKeys)
        {
            if (headerName == null ||
                headerName.Equals("content-type", StringComparison.OrdinalIgnoreCase) ||
                (requestBodyWasRewritten &&
                 headerName.Equals("content-encoding", StringComparison.OrdinalIgnoreCase)) ||
                !ShouldForwardRequestHeader(headerName, forwardClientMetadata))
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
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential.Token);
        request.Headers.Remove("chatgpt-account-id");
        request.Headers.Remove("x-openai-fedramp");
        if (credential.IsCompatibleApi)
        {
            request.Headers.Remove("x-oai-attestation");
            request.Headers.Remove("x-openai-internal-codex-responses-lite");
            request.Headers.Remove("x-openai-internal-codex-residency");
        }
        else
        {
            var accountId = SelectChatGptAccountId(
                credential,
                identity,
                ReadSafeIncomingAccountId(incoming));
            if (!string.IsNullOrWhiteSpace(accountId))
            {
                request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
            }
            if (identity?.IsFedRamp == true)
            {
                request.Headers.TryAddWithoutValidation("x-openai-fedramp", "true");
            }
            else if (credential.AllowIncomingChatGptIdentity &&
                     string.Equals(
                         incoming.Headers["x-openai-fedramp"]?.Trim(),
                         "true",
                         StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("x-openai-fedramp", "true");
            }
        }

        var originator = request.Headers.TryGetValues("originator", out var originatorValues)
            ? originatorValues.FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(originator) ||
            !originator.StartsWith("codex_", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Remove("originator");
            request.Headers.TryAddWithoutValidation("originator", DefaultOriginator);
        }
        var userAgent = request.Headers.TryGetValues("User-Agent", out var userAgentValues)
            ? userAgentValues.FirstOrDefault()
            : null;
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

    private static string? SelectChatGptAccountId(
        GatewayCredential credential,
        PatIdentity? identity,
        string? incomingAccountId) =>
        identity?.AccountId ??
        credential.ChatGptAccountId ??
        (credential.AllowIncomingChatGptIdentity ? incomingAccountId : null);

    private static bool ShouldForwardRequestHeader(
        string headerName,
        bool forwardClientMetadata = false)
    {
        if (headerName.StartsWith(
                "x-codex-account-manager-",
                StringComparison.OrdinalIgnoreCase) ||
            NeverForwardRequestHeaders.Contains(headerName))
        {
            return false;
        }

        return ProtocolRequestHeaderAllowList.Contains(headerName) ||
               forwardClientMetadata && ClientMetadataHeaderAllowList.Contains(headerName);
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
        HttpListenerResponse downstream,
        CancellationToken cancellationToken = default)
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
        await upstream.Content.CopyToAsync(downstream.OutputStream, cancellationToken);
        await downstream.OutputStream.FlushAsync(cancellationToken);
    }

    private static CancellationTokenSource? CreateRequestDeadline(HttpListenerRequest request)
    {
        var raw = request.Headers[LocalPatGateway.RequestTimeoutHeader]?.Trim();
        if (!int.TryParse(raw, out var milliseconds) || milliseconds is < 1_000 or > 120_000)
        {
            return null;
        }
        return new CancellationTokenSource(TimeSpan.FromMilliseconds(milliseconds));
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

    private static HttpClient CreateUpstreamClient(Uri? proxyUri)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = proxyUri != null,
            Proxy = proxyUri == null ? null : new WebProxy(proxyUri),
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

    private static bool IsSafeChatGptAccountId(string? value, out string accountId)
    {
        accountId = value?.Trim() ?? string.Empty;
        return accountId.Length is > 0 and <= 128 &&
               accountId.All(character =>
                   char.IsLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsJwtExpiredOrExpiring(string token, TimeSpan margin)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return true;
            }
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return !document.RootElement.TryGetProperty("exp", out var expiration) ||
                   !expiration.TryGetInt64(out var expirationUnixSeconds) ||
                   DateTimeOffset.FromUnixTimeSeconds(expirationUnixSeconds) <=
                   DateTimeOffset.UtcNow + margin;
        }
        catch (Exception ex) when (
            ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return true;
        }
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

    private static bool TryBuildCompatibleApiUpstreamUri(
        Uri baseUri,
        Uri? incoming,
        out Uri upstream)
    {
        upstream = null!;
        if (!baseUri.IsAbsoluteUri ||
            baseUri.Scheme is not ("http" or "https") ||
            (baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
             !LocalProxyDetector.IsLoopbackHost(baseUri.Host)) ||
            string.IsNullOrWhiteSpace(baseUri.Host) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment) ||
            incoming == null ||
            ContainsDotSegments(incoming.OriginalString.Split('?', 2)[0]))
        {
            return false;
        }

        const string modelGatewayPrefix = "/backend-api/codex";
        if (!HasPathPrefix(incoming.AbsolutePath, modelGatewayPrefix))
        {
            return false;
        }
        var suffix = incoming.AbsolutePath[modelGatewayPrefix.Length..];
        if (suffix.Length == 0)
        {
            suffix = "/responses";
        }
        if (!suffix.Equals("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var baseText = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (!Uri.TryCreate(baseText + suffix + incoming.Query, UriKind.Absolute, out var resolved) ||
            !resolved.Scheme.Equals(baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !resolved.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            resolved.Port != baseUri.Port)
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
    private sealed record ReplayableModelRequestBody(byte[] Bytes);
    private sealed record GatewayCredential(
        string Token,
        bool IsPersonalAccessToken,
        Uri? CompatibleApiBaseUri = null,
        string? CompatibleApiModel = null,
        string? AccountKey = null,
        string? ChatGptAccountId = null,
        bool AllowIncomingChatGptIdentity = true)
    {
        internal bool IsCompatibleApi =>
            CompatibleApiBaseUri != null && !string.IsNullOrWhiteSpace(CompatibleApiModel);
    }
    private sealed record RotationArmRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("sourceAccountKey")]
        string? SourceAccountKey,
        [property: System.Text.Json.Serialization.JsonPropertyName("targetAccountKey")]
        string? TargetAccountKey);
    private sealed record PatRejectionDetails(HttpStatusCode StatusCode, string Message);

    private sealed class PatRejectedException(
        HttpStatusCode statusCode,
        bool isInactiveWorkspaceMember) : Exception
    {
        internal HttpStatusCode StatusCode { get; } = statusCode;
        internal bool IsInactiveWorkspaceMember { get; } = isInactiveWorkspaceMember;
    }
}
