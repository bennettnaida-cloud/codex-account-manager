using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal static class LocalPatGateway
{
    // Every release takes all gateway endpoints from one version-scoped source. 2.3.13
    // can therefore run beside 2.3.12 without probing, shutting down or adopting 8332.
    internal const int Port = ReleaseConfiguration.GatewayPort;
    internal const string ListenerPrefix = ReleaseConfiguration.GatewayListenerPrefix;
    internal const string ProviderBaseUrl = ReleaseConfiguration.GatewayProviderBaseUrl;
    internal const string ChatGptBaseUrl = ReleaseConfiguration.GatewayChatGptBaseUrl;
    internal const string RequestTimeoutHeader = "X-Codex-Account-Manager-Request-Timeout-Ms";
    // Requests sent by the manager's explicit quota-test button are intentionally
    // independent of the running Codex session.  Keeping this marker on the loopback
    // hop lets the gateway avoid changing global rotation/session state or the
    // user-visible "last successful model account" while a test is in progress.
    internal const string RequestPurposeHeader =
        "X-Codex-Account-Manager-Request-Purpose";
    internal const string QuotaTestRequestPurpose = "quota-test";
    internal static string QuotaTestProofPurpose(string bearer) =>
        QuotaTestRequestPurpose + "\n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer)));
    internal const string ProcessArgument = "--local-pat-gateway";
    internal const string RootArgument = "--manager-root";
    internal const string RotationArmPath = "__rotation/arm";
    internal const string RotationClearPath = "__rotation/clear";
    internal const string SessionAffinityInvalidatePath = "__affinity/invalidate-account";

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
    // account rings. v6 adds per-session/previous-response affinity and per-account
    // fingerprint convergence. v7 classifies 429 responses and only persists/rotates on
    // explicit quota-exhaustion evidence; ordinary concurrency/rate-limit 429 responses
    // are retried on the same account and never poison the durable exhausted-account set.
    // v8 additionally normalizes gzip/deflate/br model JSON before affinity inspection and
    // requires three same-account confirmations before a body-only quota marker can rotate.
    // v9 adds bounded zstd normalization, request-scoped confirmation for otherwise
    // ambiguous repeated 429 responses, and pre-output 503 failover for compatible API
    // relays (while keeping official-account infrastructure 503 responses fail-closed).
    // v10 adds a successful-request start marker so streamed usage is attributed at the
    // request boundary instead of the later EOF timestamp. v11 lets a compatible-API
    // transport enter a prepared primary route and atomically replaces an unactivated
    // target for the explicit force-switch action. Older gateways remain readable until a
    // safe hand-off.
    internal const string DurableRotationProtocolValue = "request-boundary-v4";
    internal const string TransparentRotationProtocolValue = "request-boundary-v5";
    internal const string FingerprintRotationProtocolValue = "request-boundary-v6";
    internal const string ConfirmedQuotaRotationProtocolValue = "request-boundary-v7";
    internal const string SafeContentEncodingRotationProtocolValue = "request-boundary-v8";
    internal const string SuccessfulActivityRotationProtocolValue = "request-boundary-v10";
    // v11 is required for backup compatible-API transports to honor a prepared primary
    // route and for a force switch to replace a pending target under the gateway lock.
    // v12 keeps those semantics and raises the upstream response-header grace period. v13
    // also recognizes a configured compatible-API key as a gateway transport, allowing a
    // directly projected backup API profile to return to the primary pool at a request boundary.
    internal const string LegacyV11RotationProtocolValue = "request-boundary-v11";
    internal const string LegacyV12RotationProtocolValue = "request-boundary-v12";
    internal const string RotationProtocolValue = "request-boundary-v13";
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
                "本地网关已在系统配置中关闭；Access Token、独立代理或轮换需要网关，请先在系统配置中开启网关。");
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

    /// <summary>
    /// Synchronously closes the gateway owned by this manager during the WinForms
    /// shutdown path. The async control call runs on a worker thread so a FormClosing
    /// handler never deadlocks waiting for an await continuation on the UI thread.
    /// </summary>
    internal static bool ShutdownOwnedGatewayForProcessExit(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromSeconds(3);
        }

        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            return Task.Run(() => ShutdownOwnedGatewayAsync(cancellation.Token))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            ManagerLifecycleDiagnostics.WriteException(
                "pat-gateway-shutdown-on-manager-exit-failed",
                ex);
            return false;
        }
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
        // This is an authenticated control-plane read used to choose the logical source
        // account for a route mutation.  A 700 ms health-probe timeout is too aggressive
        // under transient local disk/AV pressure: returning null there makes the Manager
        // fall back to a stale UI account and every subsequent arm request fails with 409.
        using var client = CreateLoopbackControlClient();
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

            var rotationProtocol = response.Headers.TryGetValues(
                    RotationProtocolHeader,
                    out var rotationProtocolValues)
                ? rotationProtocolValues.FirstOrDefault(IsCompatibleRotationProtocolValue)
                : null;

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
                    : null,
                rotationProtocol,
                ReadAccountKey(activity, "lastSuccessfulModelRequestAccountKey"),
                ReadUnixMilliseconds(activity, "lastSuccessfulModelRequestCompletedAtUnixMs"),
                ReadUnixMilliseconds(activity, "lastSuccessfulModelRequestStartedAtUnixMs"),
                ReadOptionalString(activity, "lastSuccessfulModelRequestProxyNodeId"),
                ReadOptionalNullableBoolean(activity, "lastSuccessfulModelRequestUsedGlobalProxy"));
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
        string? transportAccountKey = null,
        bool replaceExistingArmedTarget = false,
        CancellationToken cancellationToken = default)
    {
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(sourceAccountKey, out var source) ||
            !PatGatewayRotationStore.TryNormalizeAccountKey(targetAccountKey, out var target) ||
            source.Equals(target, StringComparison.Ordinal))
        {
            throw new ArgumentException("PAT rotation requires two different account hashes.");
        }
        string? transport = null;
        if (transportAccountKey != null &&
            !PatGatewayRotationStore.TryNormalizeAccountKey(transportAccountKey, out transport))
        {
            throw new ArgumentException("PAT rotation transport account hash is invalid.", nameof(transportAccountKey));
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
            targetAccountKey = target,
            transportAccountKey = transport,
            replaceExistingArmedTarget
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
            var controlError = await ReadGatewayControlErrorMessageAsync(
                response,
                cancellationToken);
            if (replaceExistingArmedTarget &&
                response.StatusCode == HttpStatusCode.Conflict &&
                controlError?.Contains(
                    "active logical account",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                // v3-v10 gateways ignore the new replacement flag. Fall back only when the
                // shared route still proves that the rejected operation is replacing the
                // same unactivated source. This keeps an old listener usable until its safe
                // in-place upgrade, without clearing a route for credential/config errors.
                var existingStore = new PatGatewayRotationStore(new AccountStore().RootPath);
                var existing = existingStore.Load();
                if (existing.Status == PatGatewayRotationStatus.Armed &&
                    string.Equals(existing.SourceAccountKey, source, StringComparison.Ordinal) &&
                    !string.Equals(existing.TargetAccountKey, target, StringComparison.Ordinal))
                {
                    // The v10 listener reads this same credential-free file at every real
                    // request boundary. Replace it with one same-directory atomic rename;
                    // never clear first, otherwise a request arriving between two control
                    // calls could briefly revive the old transport account.
                    return existingStore.Arm(
                        source,
                        target,
                        DateTimeOffset.UtcNow,
                        replaceExistingArmedTarget: true);
                }
            }
            throw new InvalidOperationException(
                $"本地 PAT 网关拒绝了轮换准备（HTTP {(int)response.StatusCode}）" +
                (string.IsNullOrWhiteSpace(controlError)
                    ? "。"
                    : $"：{controlError}"));
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

    internal static async Task<bool> InvalidateOrdinarySessionAffinityAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(accountKey, out var normalized))
        {
            throw new ArgumentException(
                "Session-affinity invalidation requires a valid account hash.",
                nameof(accountKey));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { accountKey = normalized });
        using var client = CreateLoopbackControlClient();
        try
        {
            var challenge = LocalPatGatewayControl.CreateChallenge();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                ListenerPrefix + SessionAffinityInvalidatePath);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(
                LocalPatGatewayControl.ChallengeHeader,
                challenge);
            request.Headers.TryAddWithoutValidation(
                LocalPatGatewayControl.ProofHeader,
                LocalPatGatewayControl.CreateProof(
                    LocalPatGatewayControl.LoadOrCreateSecret(),
                    challenge,
                    BuildSessionAffinityInvalidationPurpose(payload)));
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode &&
                HasExpectedMarker(response) &&
                HasExpectedRotationProtocol(response))
            {
                return true;
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or IOException or
                InvalidOperationException or UnauthorizedAccessException)
        {
            // Fall through to an offline cache update. v3-v5 gateways do not hold
            // session-affinity state; a temporarily unavailable v6 gateway will still
            // enforce current pool eligibility before it can use an old ordinary alias.
        }

        try
        {
            var store = new AccountStore();
            return new PatGatewaySessionAffinityStore(
                    store.RootPath,
                    LocalPatGatewayControl.LoadOrCreateSecret())
                .InvalidateOrdinaryBindingsForAccount(normalized)
                .Persisted;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or NotSupportedException or ArgumentException or
                JsonException)
        {
            return false;
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

    private static string? ReadOptionalString(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            return null;
        }
        return value.GetString()!.Trim();
    }

    private static bool ReadOptionalBoolean(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static bool? ReadOptionalNullableBoolean(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string BuildRotationArmPurpose(ReadOnlySpan<byte> payload) =>
        "rotation-arm\n" + Convert.ToHexString(SHA256.HashData(payload));

    private static string BuildSessionAffinityInvalidationPurpose(ReadOnlySpan<byte> payload) =>
        "session-affinity-invalidate\n" + Convert.ToHexString(SHA256.HashData(payload));

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

    private static async Task<string?> ReadGatewayControlErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = message.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Length <= 512
                    ? value
                    : value[..512];
        }
        catch (Exception ex) when (
            ex is IOException or JsonException or InvalidOperationException or
            TaskCanceledException)
        {
            return null;
        }
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
        string.Equals(value, LegacyV12RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, LegacyV11RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, SuccessfulActivityRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, "request-boundary-v9", StringComparison.Ordinal) ||
        string.Equals(value, SafeContentEncodingRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, ConfirmedQuotaRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, FingerprintRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, TransparentRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, DurableRotationProtocolValue, StringComparison.Ordinal) ||
               string.Equals(value, CompatibleRotationProtocolValue, StringComparison.Ordinal);

    // v8-v9 introduced the success-only activity markers consumed by the Manager. v10
    // extends that marker with the request start boundary; all remain readable while the
    // new executable waits for a request-boundary listener hand-off.
    internal static bool IsCurrentRotationProtocolValue(string? value) =>
        string.Equals(value, RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, LegacyV12RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, SuccessfulActivityRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, SafeContentEncodingRotationProtocolValue, StringComparison.Ordinal);

    internal static bool IsTransparentRotationProtocolValue(string? value) =>
        string.Equals(value, RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, LegacyV12RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, LegacyV11RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, SuccessfulActivityRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, "request-boundary-v9", StringComparison.Ordinal) ||
        string.Equals(value, SafeContentEncodingRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, ConfirmedQuotaRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, FingerprintRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, TransparentRotationProtocolValue, StringComparison.Ordinal);

    internal static bool IsConfirmedQuotaRotationProtocolValue(string? value) =>
        string.Equals(value, RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, LegacyV12RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, LegacyV11RotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, SuccessfulActivityRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, "request-boundary-v9", StringComparison.Ordinal) ||
        string.Equals(value, SafeContentEncodingRotationProtocolValue, StringComparison.Ordinal) ||
        string.Equals(value, ConfirmedQuotaRotationProtocolValue, StringComparison.Ordinal);

    internal static bool IsManagerPreparedRotationProtocolValue(string? value) =>
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
                        string.Equals(value, DurableRotationProtocolValue, StringComparison.Ordinal) ||
                        string.Equals(value, ConfirmedQuotaRotationProtocolValue, StringComparison.Ordinal) ||
                        string.Equals(value, FingerprintRotationProtocolValue, StringComparison.Ordinal) ||
                        string.Equals(value, TransparentRotationProtocolValue, StringComparison.Ordinal) ||
                        string.Equals(value, SafeContentEncodingRotationProtocolValue, StringComparison.Ordinal) ||
                        string.Equals(value, "request-boundary-v9", StringComparison.Ordinal) ||
                        string.Equals(value, LegacyV11RotationProtocolValue, StringComparison.Ordinal) ||
                        string.Equals(value, LegacyV12RotationProtocolValue, StringComparison.Ordinal) ||
                        string.Equals(value, RotationProtocolValue, StringComparison.Ordinal));
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
    long? LastQuotaLimitedSequence = null,
    string? RotationProtocol = null,
    string? LastSuccessfulModelRequestAccountKey = null,
    DateTimeOffset? LastSuccessfulModelRequestCompletedAtUtc = null,
    DateTimeOffset? LastSuccessfulModelRequestStartedAtUtc = null,
    string? LastSuccessfulModelRequestProxyNodeId = null,
    bool? LastSuccessfulModelRequestUsedGlobalProxy = null);

internal sealed partial class LocalPatGatewayHost
{
    private const int CompatibleApiRequestBodyMaxBytes = 128 * 1024 * 1024;
    private const int ReplayableModelRequestBodyMaxBytes = CompatibleApiRequestBodyMaxBytes;
    private const int SessionAffinityInspectableBodyMaxBytes = 4 * 1024 * 1024;
    private const int SessionAffinityMetadataMaxCharacters = 64 * 1024;
    private const int SessionAffinityIdentifierMaxCharacters = 512;
    private const int SessionAffinityValuesPerKind = 3;
    private const string MutexName =
        "Local\\CodexAccountManager.LocalPatGateway." +
        ReleaseConfiguration.GatewayPortText;
    private const string UpstreamOrigin = "https://chatgpt.com";
    private const string WhoAmIUrl =
        "https://auth.openai.com/api/accounts/v1/user-auth-credential/whoami";
    private const string DefaultOriginator = "codex_cli_rs";
    // gpt-6 class models reject the pre-0.153 client identity. Prefer the real incoming
    // desktop version, but keep this floor for old or incomplete third-party callers.
    private const string RequiredCodexVersion = "0.153.4";
    private const string DefaultUserAgent =
        "codex_cli_rs/0.153.4 (Windows 10.0.0; x86_64) codex-account-manager";
    private static readonly TimeSpan IdentityCacheLifetime = TimeSpan.FromMinutes(30);
    // A dead per-account node must not hold a whoami or response-header connection
    // for the default HttpClient lifetime. This is only the pre-header attempt bound;
    // once SSE headers arrive, the stream keeps using the caller's request deadline.
    private static readonly TimeSpan UpstreamAttemptHeadersTimeout = TimeSpan.FromSeconds(60);
    private const int MaxUpstreamErrorBodyBytes = 16 * 1024;
    private const int Transient429SameAccountRetryLimit = 2;
    // A request may walk the latest configured ring after a transient 429, but a
    // malformed or concurrently-mutated account store must never turn that walk into an
    // unbounded retry loop.  The attempted-account set is the primary guard; this cap is
    // a final bounded safety net for keys that change while the request is in flight.
    private const int Transient429CandidateFailoverLimit = 32;
    private const int StructuredQuota429ConfirmationCount = 3;
    private static readonly TimeSpan Transient429DefaultRetryDelay =
        TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Transient429MaximumRetryDelay =
        TimeSpan.FromSeconds(8);
    private static readonly TimeSpan QuotaResetUnknownFallbackCooldown =
        TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ArmedRotationMaximumLifetime =
        TimeSpan.FromMinutes(15);
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
    private static readonly HashSet<string> ClientCompatibilityHeaderAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        // These values carry client capability/version information, not a device or
        // account identity. Always preserve them so a newer Codex is not downgraded by
        // the gateway's privacy-oriented fingerprint mode.
        "originator",
        "user-agent",
        "version"
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
    private readonly PatGatewaySuccessfulActivityStore _successfulActivityStore;
    private readonly PatGatewayAccountIdentityStore _accountIdentityStore;
    private readonly PatGatewaySessionAffinityStore _sessionAffinityStore;
    private readonly AccountProxyResolver _proxyResolver;
    private readonly object _rotationGate = new();
    private readonly SemaphoreSlim _rotationActivationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IdentityCacheEntry> _identityCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, QuotaLimitObservation> _quotaLimitedAccounts =
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
    private string? _lastSuccessfulModelRequestAccountKey;
    private string? _lastSuccessfulModelRequestProxyNodeId;
    private bool? _lastSuccessfulModelRequestUsedGlobalProxy;
    private DateTimeOffset? _lastSuccessfulModelRequestStartedAtUtc;
    private DateTimeOffset? _lastSuccessfulModelRequestCompletedAtUtc;
    private DateTimeOffset? _lastQuotaLimitedAtUtc;
    private long? _lastQuotaLimitedSequence;

    private readonly int _listenerPort;

    internal LocalPatGatewayHost(string markerHeader, string markerValue, int listenerPort = LocalPatGateway.Port)
    {
        _listenerPort = listenerPort;
        _markerHeader = markerHeader;
        _markerValue = markerValue;
        _controlSecret = LocalPatGatewayControl.LoadOrCreateSecret();
        _accountStore = new AccountStore();
        _themeService = new ThemeService(_accountStore.RootPath);
        _rotationStore = new PatGatewayRotationStore(_accountStore.RootPath);
        _quotaSignalStore = new PatGatewayQuotaSignalStore(_accountStore.RootPath);
        _successfulActivityStore = new PatGatewaySuccessfulActivityStore(
            _accountStore.RootPath);
        _accountIdentityStore = new PatGatewayAccountIdentityStore(_accountStore.RootPath);
        var affinitySettings = _themeService.LoadSettings();
        _ = AccountRotationConfiguration.Normalize(affinitySettings, _accountStore.LoadAccounts());
        var affinityTtl = TimeSpan.FromSeconds(
            Math.Clamp(affinitySettings.AccountRotationSessionAffinityTtlSeconds, 60, 86_400));
        _sessionAffinityStore = new PatGatewaySessionAffinityStore(
            _accountStore.RootPath,
            _controlSecret,
            bindingLifetime: affinityTtl);
        _proxyResolver = new AccountProxyResolver(_accountStore.RootPath);
        var recoveredQuotaSignals = _quotaSignalStore.ReadLatestPerAccount(DateTimeOffset.UtcNow);
        foreach (var recoveredQuotaSignal in recoveredQuotaSignals)
        {
            _quotaLimitedAccounts[recoveredQuotaSignal.AccountKey] = new QuotaLimitObservation(
                recoveredQuotaSignal.ObservedAtUtc,
                recoveredQuotaSignal.ResetAtUtc);
        }
        if (_successfulActivityStore.ReadLatest() is { } recoveredActivity)
        {
            _lastSuccessfulModelRequestAccountKey = recoveredActivity.AccountKey;
            _lastSuccessfulModelRequestStartedAtUtc = recoveredActivity.StartedAtUtc;
            _lastSuccessfulModelRequestCompletedAtUtc = recoveredActivity.CompletedAtUtc;
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
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v6") ||
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v7") ||
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v8") ||
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v9") ||
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v10") ||
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v11") ||
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v12") ||
            !LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v13") ||
            !LocalPatGateway.IsManagerPreparedRotationProtocolValue("request-boundary-v3") ||
            !LocalPatGateway.IsManagerPreparedRotationProtocolValue("request-boundary-v4") ||
            LocalPatGateway.IsManagerPreparedRotationProtocolValue("request-boundary-v5") ||
            LocalPatGateway.IsManagerPreparedRotationProtocolValue("request-boundary-v6") ||
            LocalPatGateway.IsManagerPreparedRotationProtocolValue(null) ||
            LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v3") ||
            LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v4") ||
            !LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v5") ||
            !LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v6") ||
            !LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v7") ||
            !LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v8") ||
            !LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v9") ||
            !LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v10") ||
            !LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v11") ||
            !LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v12") ||
            !LocalPatGateway.IsTransparentRotationProtocolValue("request-boundary-v13") ||
            LocalPatGateway.IsConfirmedQuotaRotationProtocolValue("request-boundary-v6") ||
            !LocalPatGateway.IsConfirmedQuotaRotationProtocolValue("request-boundary-v7") ||
            !LocalPatGateway.IsConfirmedQuotaRotationProtocolValue("request-boundary-v8") ||
            !LocalPatGateway.IsConfirmedQuotaRotationProtocolValue("request-boundary-v9") ||
            !LocalPatGateway.IsConfirmedQuotaRotationProtocolValue("request-boundary-v10") ||
            !LocalPatGateway.IsConfirmedQuotaRotationProtocolValue("request-boundary-v11") ||
            !LocalPatGateway.IsConfirmedQuotaRotationProtocolValue("request-boundary-v12") ||
            !LocalPatGateway.IsConfirmedQuotaRotationProtocolValue("request-boundary-v13") ||
            LocalPatGateway.IsCurrentRotationProtocolValue("request-boundary-v7") ||
            !LocalPatGateway.IsCurrentRotationProtocolValue("request-boundary-v8") ||
            LocalPatGateway.IsCurrentRotationProtocolValue("request-boundary-v9") ||
            !LocalPatGateway.IsCurrentRotationProtocolValue("request-boundary-v10") ||
            LocalPatGateway.IsTransparentRotationProtocolValue(null) ||
            LocalPatGateway.IsCompatibleRotationProtocolValue("request-boundary-v2") ||
            LocalPatGateway.IsCompatibleRotationProtocolValue(null))
        {
            throw new InvalidOperationException(
                "Gateway routing compatibility must accept only request-boundary v3-v13, " +
                "with v7-v13 confirmed quota signals and v8/v10/v12-v13 success-only activity markers.");
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
            !ShouldForwardRequestHeader("originator") ||
            !ShouldForwardRequestHeader("user-agent") ||
            !ShouldForwardRequestHeader("version") ||
            ShouldForwardRequestHeader("x-codex-installation-id") ||
            !ShouldForwardRequestHeader("x-codex-installation-id", true) ||
            ShouldForwardRequestHeader("authorization", true) ||
            ShouldForwardRequestHeader("cookie", true) ||
            ShouldForwardRequestHeader("chatgpt-account-id", true) ||
            ShouldForwardRequestHeader("x-openai-workspace-id", true) ||
            ShouldForwardRequestHeader("x-openai-future-client-metadata", true) ||
            ShouldForwardRequestHeader(LocalPatGatewayControl.ChallengeHeader, true) ||
            !IsVersionAtLeast("0.153.4", RequiredCodexVersion) ||
            IsVersionAtLeast("0.153.3", RequiredCodexVersion) ||
            !IsCodexUserAgentAtLeast(
                "codex_cli_rs/0.153.4 (Windows 10.0.0; x86_64)",
                RequiredCodexVersion) ||
            IsCodexUserAgentAtLeast(
                "codex_cli_rs/0.144.4 (Windows 10.0.0; x86_64)",
                RequiredCodexVersion))
        {
            throw new InvalidOperationException(
                "Gateway routing must stay open within fixed ChatGPT prefixes and reject escapes.");
        }

        if (!ShouldRejectSharedOAuthModelRequest(true, false, true, false) ||
            ShouldRejectSharedOAuthModelRequest(true, false, true, true) ||
            ShouldRejectSharedOAuthModelRequest(true, true, true, false) ||
            ShouldRejectSharedOAuthModelRequest(false, false, true, false) ||
            ShouldRejectSharedOAuthModelRequest(true, false, false, false))
            throw new InvalidOperationException("Explicit authenticated OAuth tests must not be rejected as dual-login traffic.");

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
            Encoding.UTF8.GetBytes("{\"model\":\"old\",\"stream\":true,\"input\":[{\"role\":\"user\",\"content\":\"keep\"},{\"type\":\"compaction_trigger\"}]}"),
            "gpt-api-test");
        using var rewrittenDocument = JsonDocument.Parse(rewritten);
        var allowedCompatibleModels = new HashSet<string>(
            new[] { "gpt-api-test", "gpt-api-secondary" },
            StringComparer.Ordinal);
        var selectedModelBody = RewriteCompatibleApiRequestBody(
            Encoding.UTF8.GetBytes("{\"model\":\"gpt-api-secondary\",\"input\":\"keep\"}"),
            "gpt-api-test",
            allowedCompatibleModels);
        using var selectedModelDocument = JsonDocument.Parse(selectedModelBody);
        var defaultModelBody = RewriteCompatibleApiRequestBody(
            Encoding.UTF8.GetBytes("{\"input\":\"keep\"}"),
            "gpt-api-test",
            allowedCompatibleModels);
        using var defaultModelDocument = JsonDocument.Parse(defaultModelBody);
        var unavailableModelRejected = false;
        try
        {
            _ = RewriteCompatibleApiRequestBody(
                Encoding.UTF8.GetBytes("{\"model\":\"gpt-api-unavailable\",\"input\":\"keep\"}"),
                "gpt-api-test",
                allowedCompatibleModels);
        }
        catch (InvalidDataException)
        {
            unavailableModelRejected = true;
        }
        var compatibleUriOk = TryBuildCompatibleApiUpstreamUri(
            new Uri("https://api.example.invalid/v1"),
            new Uri(LocalPatGateway.ListenerPrefix + "backend-api/codex/responses?stream=true"),
            out var compatibleUri);
        var compatibleCompactUriOk = TryBuildCompatibleApiUpstreamUri(
            new Uri("https://api.example.invalid/v1"),
            new Uri(LocalPatGateway.ListenerPrefix + "backend-api/codex/responses/compact?stream=false"),
            out var compatibleCompactUri);
        var compatibleExtraPathRejected = !TryBuildCompatibleApiUpstreamUri(
            new Uri("https://api.example.invalid/v1"),
            new Uri(LocalPatGateway.ListenerPrefix + "backend-api/codex/responses/compact/extra"),
            out _);
        var compatibleTraversalRejected = !TryBuildCompatibleApiUpstreamUri(
            new Uri("https://api.example.invalid/v1"),
            new Uri(LocalPatGateway.ListenerPrefix + "backend-api/codex/responses/%252e%252e/secret"),
            out _);
        if (!activated ||
            !ShouldApplyRotationAtRequestBoundary(
                isModelRequest: true,
                isIndependentAccountProbe: false) ||
            ShouldApplyRotationAtRequestBoundary(
                isModelRequest: true,
                isIndependentAccountProbe: true) ||
            selected.Token != "at-target-test-only" ||
            unrelated.Token != "at-unrelated-test-only" ||
            !apiSelected.IsCompatibleApi ||
            apiSelected.CompatibleApiModel != "gpt-api-test" ||
            oauthSelected.ChatGptAccountId != "account-target" ||
            oauthSelected.AllowIncomingChatGptIdentity ||
            SelectChatGptAccountId(oauthSelected, null, "account-old") != "account-target" ||
            !compatibleUriOk ||
            compatibleUri.AbsoluteUri != "https://api.example.invalid/v1/responses?stream=true" ||
            !compatibleCompactUriOk ||
            compatibleCompactUri.AbsoluteUri != "https://api.example.invalid/v1/responses/compact?stream=false" ||
            !compatibleExtraPathRejected ||
            !compatibleTraversalRejected ||
            rewrittenDocument.RootElement.GetProperty("model").GetString() != "gpt-api-test" ||
            selectedModelDocument.RootElement.GetProperty("model").GetString() != "gpt-api-secondary" ||
            defaultModelDocument.RootElement.GetProperty("model").GetString() != "gpt-api-test" ||
            !unavailableModelRejected ||
            rewrittenDocument.RootElement.GetProperty("input")[0].GetProperty("content").GetString() != "keep" ||
            rewrittenDocument.RootElement.GetProperty("input")[1].GetProperty("type").GetString() != "compaction_trigger" ||
            !rewrittenDocument.RootElement.GetProperty("stream").GetBoolean() ||
            !ShouldForwardRequestHeader("x-codex-beta-features") ||
            Encoding.UTF8.GetString(rewritten).Contains("sk-api-test-only", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Gateway request-boundary rotation selected or transformed the wrong PAT/API target.");
        }

        using var remainingQuota429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                "{\"error\":{\"type\":\"rate_limit_error\",\"code\":\"rate_limit_exceeded\"}}")
        };
        remainingQuota429.Headers.TryAddWithoutValidation(
            "x-codex-primary-used-percent",
            "98");
        remainingQuota429.Headers.TryAddWithoutValidation(
            "x-codex-primary-reset-after-seconds",
            "3600");
        var remainingDecision = ClassifyUpstream429(
            remainingQuota429,
            Encoding.UTF8.GetBytes(
                "{\"error\":{\"type\":\"rate_limit_error\",\"code\":\"rate_limit_exceeded\"}}"),
            DateTimeOffset.UtcNow);
        using var exhausted429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}")
        };
        exhausted429.Headers.TryAddWithoutValidation(
            "x-codex-secondary-used-percent",
            "100");
        exhausted429.Headers.TryAddWithoutValidation(
            "x-codex-secondary-reset-after-seconds",
            "1800");
        var exhaustedDecision = ClassifyUpstream429(
            exhausted429,
            Encoding.UTF8.GetBytes("{}"),
            DateTimeOffset.UtcNow);
        using var structured429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}")
        };
        var structuredDecision = ClassifyUpstream429(
            structured429,
            Encoding.UTF8.GetBytes(
                "{\"error\":{\"type\":\"usage_limit_reached\",\"resets_in_seconds\":120}}"),
            DateTimeOffset.UtcNow);
        var ambiguousUsageDecision = ClassifyUpstream429(
            structured429,
            Encoding.UTF8.GetBytes(
                "{\"error\":{\"type\":\"usage_limit_reached\",\"message\":\"limit reached\"}}"),
            DateTimeOffset.UtcNow);
        if (remainingDecision.IsQuotaExhausted ||
            remainingDecision.Reason != "unconfirmed-rate-limit" ||
            !exhaustedDecision.IsQuotaExhausted ||
            exhaustedDecision.ResetAtUtc == null ||
            structuredDecision.IsQuotaExhausted ||
            !structuredDecision.RequiresSameAccountConfirmation ||
            structuredDecision.ResetAtUtc == null ||
            ambiguousUsageDecision.IsQuotaExhausted ||
            ambiguousUsageDecision.RequiresSameAccountConfirmation)
        {
            throw new InvalidOperationException(
                "429 classification must keep remaining/ambiguous quota on the same account, " +
                "require repeated review for a structured reset, and rotate immediately only on explicit 100% evidence.");
        }

        // An unconfirmed 429 may cross accounts only after the bounded same-account
        // review, and only when replay is safe.  This policy is request-scoped: it must
        // reject strict response affinity, opaque bodies, disabled cross-account replay,
        // and the hard candidate cap before the live selector is consulted.
        if (!CanReplayUnconfirmed429AcrossAccounts(
                requestHasEntityBody: true,
                hasReplayableBody: true,
                strictResponseAffinity: false,
                allowCrossAccountReplay: true,
                candidateFailoverCount: 0) ||
            CanReplayUnconfirmed429AcrossAccounts(
                requestHasEntityBody: true,
                hasReplayableBody: false,
                strictResponseAffinity: false,
                allowCrossAccountReplay: true,
                candidateFailoverCount: 0) ||
            CanReplayUnconfirmed429AcrossAccounts(
                requestHasEntityBody: true,
                hasReplayableBody: true,
                strictResponseAffinity: true,
                allowCrossAccountReplay: true,
                candidateFailoverCount: 0) ||
            CanReplayUnconfirmed429AcrossAccounts(
                requestHasEntityBody: true,
                hasReplayableBody: true,
                strictResponseAffinity: false,
                allowCrossAccountReplay: false,
                candidateFailoverCount: 0) ||
            CanReplayUnconfirmed429AcrossAccounts(
                requestHasEntityBody: true,
                hasReplayableBody: true,
                strictResponseAffinity: false,
                allowCrossAccountReplay: true,
                candidateFailoverCount: Transient429CandidateFailoverLimit))
        {
            throw new InvalidOperationException(
                "An unconfirmed 429 must fail over only for safely replayable requests within the bounded request scope.");
        }

        // Retry-After is deliberately excluded from the confirmation signature because
        // providers often decrement or regenerate it for each identical 429 response.
        using var signature429A = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"error\":\"rate_limit\"}"))
        };
        using var signature429B = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"error\":\"rate_limit\"}"))
        };
        signature429A.Headers.TryAddWithoutValidation("Retry-After", "1");
        signature429B.Headers.TryAddWithoutValidation("Retry-After", "2");
        if (!string.Equals(
                Build429ConfirmationSignature(
                    signature429A,
                    Encoding.UTF8.GetBytes("{\"error\":\"rate_limit\"}")),
                Build429ConfirmationSignature(
                    signature429B,
                    Encoding.UTF8.GetBytes("{\"error\":\"rate_limit\"}")),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "429 confirmation signatures must remain stable when Retry-After changes.");
        }

        var compressedRequestFixture = Encoding.UTF8.GetBytes(
            "{\"model\":\"gpt-test\",\"previous_response_id\":null,\"input\":\"压缩请求\"}");
        static byte[] CompressFixture(
            byte[] source,
            Func<Stream, Stream> createCompressor)
        {
            using var output = new MemoryStream();
            using (var compressor = createCompressor(output))
            {
                compressor.Write(source, 0, source.Length);
            }
            return output.ToArray();
        }
        var gzipFixture = CompressFixture(
            compressedRequestFixture,
            stream => new GZipStream(
                stream,
                CompressionLevel.SmallestSize,
                leaveOpen: true));
        var deflateFixture = CompressFixture(
            compressedRequestFixture,
            stream => new DeflateStream(
                stream,
                CompressionLevel.SmallestSize,
                leaveOpen: true));
        var brotliFixture = CompressFixture(
            compressedRequestFixture,
            stream => new BrotliStream(
                stream,
                CompressionLevel.SmallestSize,
                leaveOpen: true));
        var gzipThenBrotliFixture = CompressFixture(
            gzipFixture,
            stream => new BrotliStream(
                stream,
                CompressionLevel.SmallestSize,
                leaveOpen: true));
        byte[] zstdFixture;
        using (var compressor = new ZstdSharp.Compressor())
        {
            zstdFixture = compressor.Wrap(compressedRequestFixture).ToArray();
        }
        if (!DecodeRequestBody(gzipFixture, "gzip").AsSpan().SequenceEqual(compressedRequestFixture) ||
            !DecodeRequestBody(deflateFixture, "deflate").AsSpan().SequenceEqual(compressedRequestFixture) ||
            !DecodeRequestBody(brotliFixture, "br").AsSpan().SequenceEqual(compressedRequestFixture) ||
            !DecodeRequestBody(gzipThenBrotliFixture, "gzip, br").AsSpan()
                .SequenceEqual(compressedRequestFixture) ||
            !DecodeRequestBody(zstdFixture, "zstd").AsSpan()
                .SequenceEqual(compressedRequestFixture))
        {
            throw new InvalidOperationException(
                "Model request Content-Encoding normalization did not preserve inspectable JSON bytes " +
                "for gzip, deflate, br, or zstd.");
        }

        var quotaObservationNow = DateTimeOffset.UtcNow;
        if (!IsQuotaLimitObservationActive(
                new QuotaLimitObservation(
                    quotaObservationNow,
                    quotaObservationNow.AddMinutes(5)),
                quotaObservationNow.AddMinutes(1)) ||
            IsQuotaLimitObservationActive(
                new QuotaLimitObservation(
                    quotaObservationNow.AddSeconds(-6),
                    null),
                quotaObservationNow) ||
            !IsQuotaLimitObservationActive(
                new QuotaLimitObservation(
                    quotaObservationNow.AddSeconds(-4),
                    null),
                quotaObservationNow))
        {
            throw new InvalidOperationException(
                "Confirmed quota cooldown must honor reset times and use only a short fallback when reset metadata is absent.");
        }

        var routeRoot = Path.Combine(
            Path.GetTempPath(),
            "gateway-route-freshness-test");
        var routeSource = new AccountRecord
        {
            Name = "route-source",
            CodexHome = Path.Combine(routeRoot, "source")
        };
        var routeTarget = new AccountRecord
        {
            Name = "route-target",
            CodexHome = Path.Combine(routeRoot, "target")
        };
        var routeAccounts = new[] { routeSource, routeTarget };
        var routeSettings = new AppSettings
        {
            AccountRotationEnabled = true,
            AccountRotationQuotaEvidenceVersion =
                AccountRotationConfiguration.CurrentQuotaEvidenceVersion
        };
        _ = AccountRotationConfiguration.Normalize(routeSettings, routeAccounts);
        var routeSourceKey = QuotaAccountIdentity.CreateKey(routeSource);
        var routeTargetKey = QuotaAccountIdentity.CreateKey(routeTarget);
        var freshArmedRoute = new PatGatewayRotationSnapshot(
            PatGatewayRotationStatus.Armed,
            routeSourceKey,
            routeSourceKey,
            routeTargetKey,
            quotaObservationNow.AddMinutes(-1),
            null);
        if (!IsRotationRouteEligible(
                freshArmedRoute,
                routeSettings,
                routeAccounts,
                quotaObservationNow) ||
            IsRotationRouteEligible(
                freshArmedRoute with
                {
                    ArmedAtUtc = quotaObservationNow - ArmedRotationMaximumLifetime -
                                 TimeSpan.FromSeconds(1)
                },
                routeSettings,
                routeAccounts,
                quotaObservationNow))
        {
            throw new InvalidOperationException(
                "A request-boundary route must expire instead of activating hours after it was prepared.");
        }
        routeSettings.AccountRotationResetAtUtc[routeSourceKey] =
            quotaObservationNow.AddMinutes(-2);
        var routeArmedBeforeReset = freshArmedRoute with
        {
            ArmedAtUtc = quotaObservationNow.AddMinutes(-3)
        };
        if (!IsRotationRouteEligible(
                freshArmedRoute,
                routeSettings,
                routeAccounts,
                quotaObservationNow) ||
            IsRotationRouteEligible(
                routeArmedBeforeReset,
                routeSettings,
                routeAccounts,
                quotaObservationNow))
        {
            throw new InvalidOperationException(
                "A route armed after a quota reset must remain eligible, while a route from the previous window must expire.");
        }

        ValidateOfficialOAuthRotationCredential();
    }

    internal static void ValidateSessionAffinityRouting()
    {
        var headers = new System.Collections.Specialized.NameValueCollection
        {
            ["session-id"] = "session-header",
            ["thread-id"] = "thread-header",
            ["conversation-id"] = "conversation-header"
        };
        var request = ExtractSessionAffinityRequest(
            headers,
            Encoding.UTF8.GetBytes(
                "{\"previous_response_id\":\"resp_affinity_fixture\"," +
                "\"prompt_cache_key\":\"prompt-fixture\"}"),
            contentEncoding: null);
        var expectedKinds = new[]
        {
            PatGatewaySessionAffinityKeyKind.Response,
            PatGatewaySessionAffinityKeyKind.Session
        };
        if (request.PreviousResponseId != "resp_affinity_fixture" ||
            !request.AllowCrossAccountReplay ||
            !request.RequiresOriginalAccount ||
            !request.Keys.Select(key => key.Kind).SequenceEqual(expectedKinds) ||
            !request.Keys.Select(key => key.Value).SequenceEqual(
                [
                    "resp_affinity_fixture",
                    "session-header"
                ]))
        {
            throw new InvalidOperationException(
                "Session affinity did not preserve response priority and single-seed header selection.");
        }

        var malformed = ExtractSessionAffinityRequest(
            headers,
            Encoding.UTF8.GetBytes("{\"previous_response_id\":"),
            contentEncoding: null);
        var compressed = ExtractSessionAffinityRequest(
            headers,
            Encoding.UTF8.GetBytes("{}"),
            contentEncoding: "gzip");
        var ambiguous = ExtractSessionAffinityRequest(
            headers,
            Encoding.UTF8.GetBytes(
                "{\"previous_response_id\":\"resp_first_fixture\"," +
                "\"previous_response_id\":\"resp_second_fixture\"}"),
            contentEncoding: null);
        var invalidPrevious = ExtractSessionAffinityRequest(
            headers,
            Encoding.UTF8.GetBytes("{\"previous_response_id\":\"msg_not_response\"}"),
            contentEncoding: null);
        var nullPrevious = ExtractSessionAffinityRequest(
            headers,
            Encoding.UTF8.GetBytes("{\"previous_response_id\":null}"),
            contentEncoding: null);
        var emptyPrevious = ExtractSessionAffinityRequest(
            headers,
            Encoding.UTF8.GetBytes("{\"previous_response_id\":\"\"}"),
            contentEncoding: null);
        if (malformed.AllowCrossAccountReplay ||
            compressed.AllowCrossAccountReplay ||
            ambiguous.AllowCrossAccountReplay ||
            !malformed.RequiresOriginalAccount ||
            !compressed.RequiresOriginalAccount ||
            !ambiguous.RequiresOriginalAccount ||
            ambiguous.PreviousResponseId != null ||
            ambiguous.Keys.Any(key => key.Kind == PatGatewaySessionAffinityKeyKind.Response) ||
            invalidPrevious.AllowCrossAccountReplay ||
            !invalidPrevious.RequiresOriginalAccount ||
            invalidPrevious.PreviousResponseId != null ||
            !nullPrevious.AllowCrossAccountReplay ||
            nullPrevious.RequiresOriginalAccount ||
            !emptyPrevious.AllowCrossAccountReplay ||
            emptyPrevious.RequiresOriginalAccount ||
            malformed.Keys.All(key => key.Kind != PatGatewaySessionAffinityKeyKind.Session))
        {
            throw new InvalidOperationException(
                "Opaque or ambiguous continuation bodies must disable cross-account replay without discarding safe header aliases.");
        }

        var largeBody = Encoding.UTF8.GetBytes(
            "{\"instructions\":\"" +
            new string('x', SessionAffinityInspectableBodyMaxBytes + 1) +
            "\",\"previous_response_id\":\"resp_large_affinity_fixture\"}");
        var largeRequest = ExtractSessionAffinityRequest(
            new System.Collections.Specialized.NameValueCollection(),
            largeBody,
            contentEncoding: "identity");
        if (largeRequest.OpaqueBody ||
            largeRequest.UnsafeContinuation ||
            !largeRequest.AllowCrossAccountReplay ||
            largeRequest.PreviousResponseId != "resp_large_affinity_fixture" ||
            largeRequest.Keys.Count != 1 ||
            largeRequest.Keys[0].Kind != PatGatewaySessionAffinityKeyKind.Response)
        {
            throw new InvalidOperationException(
                "Large identity-encoded JSON did not receive bounded continuation inspection.");
        }

        var metadataHeaders = new System.Collections.Specialized.NameValueCollection
        {
            ["x-codex-turn-metadata"] =
                "{\"session_id\":\"metadata-session\",\"thread_id\":\"metadata-thread\"}"
        };
        var metadata = ExtractSessionAffinityRequest(
            metadataHeaders,
            Encoding.UTF8.GetBytes("{}"),
            contentEncoding: "identity");
        if (!metadata.AllowCrossAccountReplay ||
            metadata.RequiresOriginalAccount ||
            !metadata.Keys.Select(key => key.Value).SequenceEqual(
                ["metadata-session"]))
        {
            throw new InvalidOperationException(
                "Codex turn metadata did not produce a safe single session affinity seed.");
        }

        var contentFallback = ExtractSessionAffinityRequest(
            new System.Collections.Specialized.NameValueCollection(),
            Encoding.UTF8.GetBytes(
                "{\"model\":\"gpt-test\",\"messages\":[" +
                "{\"role\":\"system\",\"content\":\"rules\"}," +
                "{\"role\":\"user\",\"content\":\"hello\"}]}"),
            contentEncoding: "identity");
        if (contentFallback.Keys.Count != 1 ||
            contentFallback.Keys[0].Kind != PatGatewaySessionAffinityKeyKind.Session ||
            !contentFallback.Keys[0].Value.StartsWith(
                OpenAIContentSessionSeed.Prefix,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "OpenAI content fallback did not produce one deterministic session seed.");
        }
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
            _proxyResolver.Dispose();
        }
    }

    private async Task HandleAsync(
        HttpListenerContext context,
        HttpListener listener,
        CancellationTokenSource shutdown)
    {
        var response = context.Response;
        var affinityLeases = new List<PatGatewaySessionAffinityLease>();
        // These buffered upstream responses may survive several in-request candidate
        // attempts. Keep the references outside the try block so the unconditional finally
        // can dispose them even when request handling exits through an early error.
        HttpResponseMessage? lastTransient429Response = null;
        HttpResponseMessage? lastQuotaResponse = null;
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
            if (path.Equals(
                    "/" + LocalPatGateway.SessionAffinityInvalidatePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                await HandleSessionAffinityInvalidateAsync(context.Request, response);
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
                    "请求没有携带可用的 Codex PAT、ChatGPT OAuth 或已配置兼容 API Bearer。");
                return;
            }
            credential = BindConfiguredAccountKey(credential);

            var isModelRequest = IsModelRequest(context.Request, upstreamUri);
            var isIndependentAccountProbe = IsIndependentAccountProbe(context.Request);
            var authenticatedAccountProbe = isIndependentAccountProbe &&
                !string.IsNullOrWhiteSpace(credential.AccountKey) &&
                LocalPatGatewayControl.ValidateRequest(
                    context.Request, _controlSecret, LocalPatGateway.QuotaTestProofPurpose(credential.Token));
            if (ShouldRejectSharedOAuthModelRequest(
                    isModelRequest,
                    credential.IsPersonalAccessToken || credential.IsCompatibleApi,
                    CodexCliService.IsSharedDualLoginModeConfigured(),
                    authenticatedAccountProbe))
            {
                await WriteErrorAsync(
                    response,
                    HttpStatusCode.Conflict,
                    "本地双登录配置错误：模型请求携带的是 ChatGPT OAuth，而不是所选 PAT/API Bearer。" +
                    "Account Manager 已阻止本次请求，以免错误消耗 OAuth 模型额度；请重新投放该账号后再试。");
                return;
            }
            var rotationBoundaryApplied = false;
            if (ShouldApplyRotationAtRequestBoundary(
                    isModelRequest,
                    isIndependentAccountProbe))
            {
                var routeBeforeBoundary = _rotationStore.Load();
                rotationBoundaryApplied = routeBeforeBoundary.Status != PatGatewayRotationStatus.None &&
                    IncomingMatchesRotationTransport(credential, routeBeforeBoundary);
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
                catch (UnsupportedContentEncodingException ex)
                {
                    await WriteErrorAsync(
                        response,
                        HttpStatusCode.UnsupportedMediaType,
                        "模型请求使用了网关无法安全解码的 Content-Encoding（" +
                        SanitizeNetworkError(ex.Message) +
                        "）。请改用 gzip、deflate、br、zstd 或 identity 后重试。");
                    return;
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
            var globalRouteCredential = credential;
            var globalRouteSourceAccountKey = credential.AccountKey;
            SessionAffinityRequest? affinityRequest = null;
            PatGatewaySessionAffinityLease? activeAffinityLease = null;
            var strictResponseAffinity = false;
            if (isModelRequest)
            {
                // ReadReplayableModelRequestBodyAsync normalizes a supported request
                // Content-Encoding (gzip/deflate/br/zstd) to identity before this point.
                // Inspect the normalized bytes so the desktop Codex client can keep its
                // normal compression behaviour without making the account-rotation
                // boundary reject the request with a synthetic 415.
                affinityRequest = ExtractSessionAffinityRequest(
                    context.Request.Headers,
                    replayableBody?.Bytes,
                    contentEncoding: null);
            }
            if (isModelRequest &&
                !isIndependentAccountProbe &&
                !rotationBoundaryApplied &&
                PatGatewayRotationStore.TryNormalizeAccountKey(
                    globalRouteSourceAccountKey,
                    out var normalizedGlobalAccountKey))
            {
                var affinitySettings = _themeService.LoadSettings();
                if (AccountRotationConfiguration.IsEnabled(affinitySettings) &&
                    IsSessionAffinityAccountEligible(
                        affinitySettings,
                        normalizedGlobalAccountKey))
                {
                    if (affinityRequest is { OpaqueBody: true })
                    {
                        // A compressed body can hide previous_response_id from the
                        // account-affinity parser. Forwarding it through the current
                        // global route could therefore replay an existing continuation
                        // on the wrong credential after a rotation. Standard Codex and
                        // Responses clients send identity-encoded JSON; reject only the
                        // uninspectable form while automatic account rotation is enabled.
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.UnsupportedMediaType,
                            "开启账号轮换时，模型请求体必须是可检查的未压缩 JSON；" +
                            "请移除 Content-Encoding 后重试。关闭账号轮换可恢复原样转发。");
                        return;
                    }
                    if (affinityRequest is { UnsafeContinuation: true })
                    {
                        // A malformed, conflicting, or invalid continuation field is known
                        // unsafe. Do not let a lower-priority alias turn it into a
                        // cross-account replay, and do not forward it.
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.BadRequest,
                            "模型请求体包含无法安全确认的 previous_response_id；" +
                            "请修正续聊字段，或开始一个不携带该字段的新会话。");
                        return;
                    }

                    // Response-account affinity is an independent safety layer and stays
                    // active while ordinary session stickiness is switched off. This
                    // prevents the session toggle from turning a confirmed continuation
                    // into a cross-account replay.
                    strictResponseAffinity = affinityRequest?.PreviousResponseId != null;
                    IReadOnlyList<PatGatewaySessionAffinityKey> routingKeys = affinityRequest == null
                        ? []
                        : affinitySettings.AccountRotationSessionAffinityEnabled
                            ? affinityRequest.Keys
                            : affinityRequest.Keys
                                .Where(key => key.Kind == PatGatewaySessionAffinityKeyKind.Response)
                                .ToArray();
                    if (routingKeys.Count > 0)
                    {
                        activeAffinityLease = _sessionAffinityStore.ResolveOrClaim(
                            routingKeys,
                            normalizedGlobalAccountKey);
                        affinityLeases.Add(activeAffinityLease);
                        // Even an unknown previous_response_id may refer to state created
                        // before this cache existed. A lower-priority ordinary session seed
                        // must never be used as a route around an unconfirmed continuation.
                        if (affinityRequest?.PreviousResponseId != null &&
                            (activeAffinityLease.MatchedKind != PatGatewaySessionAffinityKeyKind.Response ||
                             !activeAffinityLease.MatchedConfirmed))
                        {
                            await WriteErrorAsync(
                                response,
                                HttpStatusCode.Conflict,
                                "previous_response_id 尚未在本地网关建立可信账号绑定；" +
                                "为避免跨账号重放，网关没有发送本次续聊请求。" +
                                "请先在同一网关创建新会话，或恢复原账号后重试。");
                            return;
                        }

                        if (!activeAffinityLease.AccountKey.Equals(
                                normalizedGlobalAccountKey,
                                StringComparison.Ordinal))
                        {
                            var stickyAccountAvailable = IsSessionAffinityAccountEligible(
                                affinitySettings,
                                activeAffinityLease.AccountKey);
                            if (stickyAccountAvailable)
                            {
                                try
                                {
                                    credential = ResolveRotationCredential(
                                        activeAffinityLease.AccountKey);
                                }
                                catch (Exception ex) when (
                                    ex is IOException or UnauthorizedAccessException or
                                    JsonException or InvalidDataException or
                                    InvalidOperationException or NotSupportedException or
                                    ArgumentException or FormatException)
                                {
                                    stickyAccountAvailable = false;
                                }
                            }

                            if (!stickyAccountAvailable)
                            {
                                if (strictResponseAffinity)
                                {
                                    await WriteErrorAsync(
                                        response,
                                        HttpStatusCode.Conflict,
                                        "previous_response_id 绑定的原账号当前不可用；" +
                                        "为避免跨账号重放续聊请求，网关没有自动换号。" +
                                        "请恢复原账号，或开始一个不携带 previous_response_id 的新会话。");
                                    return;
                                }

                                // Ordinary session aliases may escape an account that was
                                // removed, disabled, or whose local credential is unreadable.
                                // The provisional move is confirmed only after a complete
                                // successful response; otherwise the durable old route returns.
                                var movedToGlobal = _sessionAffinityStore.Move(
                                    activeAffinityLease,
                                    normalizedGlobalAccountKey);
                                if (movedToGlobal != null)
                                {
                                    activeAffinityLease = movedToGlobal;
                                    affinityLeases.Add(movedToGlobal);
                                }
                                credential = globalRouteCredential;
                            }
                        }
                    }
                }
            }
            using var modelRequestActivity = isModelRequest && !isIndependentAccountProbe
                ? BeginModelRequest(credential.AccountKey)
                : null;
            var attemptedAccountKeys = new HashSet<string>(StringComparer.Ordinal);
            var runtimeGlobalProxyFallbackAccounts = new HashSet<string>(StringComparer.Ordinal);
            var allowCompatibleApiRetry = CanRewriteCompatibleApiRequestBody(
                context.Request,
                replayableBody);
            var transparentRetryCount = 0;
            var transient429RetryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var structured429Confirmations = new Dictionary<
                string,
                (int Count, DateTimeOffset? ResetAtUtc)>(StringComparer.Ordinal);
            var unconfirmed429Confirmations = new Dictionary<
                string,
                (int Count, string Signature)>(StringComparer.Ordinal);
            GatewayCredential? lastQuotaCredential = null;
            var globalRouteQuotaSeen = false;
            HttpResponseMessage? finalUpstreamResponse = null;
            var hadSuccessfulTransparentRetry = false;
            var transient429CandidateFailoverCount = 0;
            var preserveAffinityBindingOnSuccess = false;
            string? successfulProxyNodeId = null;
            var successfulRequestUsedGlobalProxy = false;

            while (finalUpstreamResponse == null)
            {
                modelRequestActivity?.UpdateAccountKey(credential.AccountKey);
                upstreamUri = chatGptUpstreamUri;
                if (credential.IsCompatibleApi &&
                    !TryBuildCompatibleApiUpstreamUri(
                        credential.CompatibleApiBaseUri!,
                        context.Request.Url,
                        out upstreamUri,
                        _listenerPort))
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
                    if (TrySelectGlobalCredentialAfterAffinityFailure(
                            credential,
                            globalRouteCredential,
                            strictResponseAffinity,
                            activeAffinityLease,
                            out var globalAfterMappingFailure))
                    {
                        if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                        {
                            attemptedAccountKeys.Add(credential.AccountKey);
                        }
                        credential = globalAfterMappingFailure!;
                        WriteTransparentRotationDiagnostic(
                            "affinity-fallback",
                            Math.Max(1, attemptedAccountKeys.Count),
                            "unsafe-api-mapping");
                        continue;
                    }
                    await WriteErrorAsync(
                        response,
                        HttpStatusCode.BadGateway,
                        "兼容 API 的模型请求地址无法安全映射。");
                    return;
                }

                var useDirectConnection = credential.IsCompatibleApi &&
                                          LocalProxyDetector.IsLoopbackHost(upstreamUri.Host);
                var runtimeFallbackAccountKey = NormalizeOptionalAccountKey(credential.AccountKey);
                var proxyResolution = useDirectConnection
                    ? new ProxyResolution(true, null, null, "direct", "")
                    : runtimeFallbackAccountKey != null &&
                      runtimeGlobalProxyFallbackAccounts.Contains(runtimeFallbackAccountKey)
                        ? ResolveGlobalProxy()
                        : ResolveProxyForCredential(credential);
                var proxyUri = proxyResolution.ProxyUri;
                if (!useDirectConnection && !proxyResolution.Success)
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
                        proxyResolution.Error.Length == 0
                            ? "未检测到可用的本地代理；为防止意外直连，上游请求已停止。"
                            : proxyResolution.Error);
                    return;
                }
                var clientKey = useDirectConnection ? "direct" : proxyResolution.PoolKey;
                if (isModelRequest && !isIndependentAccountProbe)
                {
                    // Keep the route that actually reaches the successful response.
                    // A retry can overwrite this from node A to node B or to global;
                    // the health endpoint must never report the first failed attempt.
                    successfulProxyNodeId = proxyResolution.NodeId;
                    successfulRequestUsedGlobalProxy = !useDirectConnection &&
                        proxyResolution.NodeId == null &&
                        proxyResolution.PoolKey.StartsWith("global:", StringComparison.Ordinal);
                }
                var proxyNode = _proxyResolver.GetNode(proxyResolution.NodeId);
                var client = _clients.GetOrAdd(
                    clientKey,
                    _ => CreateUpstreamClient(proxyResolution, proxyNode));
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
                            clientKey,
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
                        if (TrySelectGlobalCredentialAfterAffinityFailure(
                                credential,
                                globalRouteCredential,
                                strictResponseAffinity,
                                activeAffinityLease,
                                out var globalAfterPatRejection))
                        {
                            if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                            {
                                attemptedAccountKeys.Add(credential.AccountKey);
                            }
                            credential = globalAfterPatRejection!;
                            WriteTransparentRotationDiagnostic(
                                "affinity-fallback",
                                Math.Max(1, attemptedAccountKeys.Count),
                                "credential-rejected");
                            continue;
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
                        if (!requestCancellationToken.IsCancellationRequested &&
                            (ex is HttpRequestException or TaskCanceledException) &&
                            TryActivateRuntimeGlobalProxyFallback(
                                credential,
                                proxyResolution,
                                runtimeGlobalProxyFallbackAccounts,
                                "identity-network-failure"))
                        {
                            continue;
                        }
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
                        if (TrySelectGlobalCredentialAfterAffinityFailure(
                                credential,
                                globalRouteCredential,
                                strictResponseAffinity,
                                activeAffinityLease,
                                out var globalAfterIdentityFailure))
                        {
                            if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                            {
                                attemptedAccountKeys.Add(credential.AccountKey);
                            }
                            // Identity lookup failures are generally transient network
                            // errors. Let this request escape the sticky account, but do
                            // not make that temporary escape the new durable session route.
                            preserveAffinityBindingOnSuccess = true;
                            credential = globalAfterIdentityFailure!;
                            WriteTransparentRotationDiagnostic(
                                "affinity-fallback",
                                Math.Max(1, attemptedAccountKeys.Count),
                                "identity-network-failure");
                            continue;
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
                // A decoded body must be sent as identity-encoded bytes. Treat the
                // normalization as a rewrite for header/content-length handling; this
                // also makes retries deterministic because every attempt uses the same
                // replay buffer.
                var requestBodyWasRewritten = replayableBody?.WasContentDecoded == true;
                var fingerprintPlan = ResolveCodexFingerprintPlan(credential, context.Request);
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
                            credential.CompatibleApiModel!,
                            credential.CompatibleApiModels);
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
                        if (TrySelectGlobalCredentialAfterAffinityFailure(
                                credential,
                                globalRouteCredential,
                                strictResponseAffinity,
                                activeAffinityLease,
                                out var globalAfterBodyFailure))
                        {
                            if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                            {
                                attemptedAccountKeys.Add(credential.AccountKey);
                            }
                            credential = globalAfterBodyFailure!;
                            WriteTransparentRotationDiagnostic(
                                "affinity-fallback",
                                Math.Max(1, attemptedAccountKeys.Count),
                                "api-body-conversion");
                            continue;
                        }
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.BadRequest,
                            "兼容 API 请求无法安全转换：" + SanitizeNetworkError(ex.Message));
                        return;
                    }
                }
                if (fingerprintPlan.HasConvergedIdentifiers && requestBody != null)
                {
                    var rewritten = CodexFingerprintConvergence.RewriteRequestBody(
                        requestBody,
                        fingerprintPlan,
                        out var fingerprintBodyModified);
                    if (requestBody.Length > 0 && !fingerprintBodyModified)
                    {
                        if (lastQuotaResponse != null &&
                            TrySelectNextTransparentRotationCredential(
                                credential,
                                attemptedAccountKeys,
                                allowCompatibleApiRetry,
                                out var nextAfterFingerprintFailure,
                                out var skippedOrdinal,
                                out _))
                        {
                            WriteTransparentRotationDiagnostic(
                                "candidate-skipped",
                                skippedOrdinal,
                                "fingerprint-body-convergence");
                            credential = nextAfterFingerprintFailure!;
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
                        if (TrySelectGlobalCredentialAfterAffinityFailure(
                                credential,
                                globalRouteCredential,
                                strictResponseAffinity,
                                activeAffinityLease,
                                out var globalAfterFingerprintFailure))
                        {
                            if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                            {
                                attemptedAccountKeys.Add(credential.AccountKey);
                            }
                            credential = globalAfterFingerprintFailure!;
                            WriteTransparentRotationDiagnostic(
                                "affinity-fallback",
                                Math.Max(1, attemptedAccountKeys.Count),
                                "fingerprint-body-convergence");
                            continue;
                        }
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.BadRequest,
                            "模型请求无法安全应用所选的 Codex 指纹收敛模式。");
                        return;
                    }
                    requestBody = rewritten;
                    requestBodyWasRewritten |= fingerprintBodyModified;
                }

                if (activeAffinityLease != null &&
                    PatGatewayRotationStore.TryNormalizeAccountKey(
                        credential.AccountKey,
                        out var attemptAccountKey) &&
                    !activeAffinityLease.AccountKey.Equals(
                        attemptAccountKey,
                        StringComparison.Ordinal))
                {
                    if (strictResponseAffinity)
                    {
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.Conflict,
                            affinityRequest?.PreviousResponseId != null
                                ? "previous_response_id 的续聊请求不能跨账号重放。"
                                : "请求体无法安全确认续聊状态，不能跨账号重放。");
                        return;
                    }

                    // Publish the candidate only as an in-memory provisional route just
                    // before the model request is sent. Concurrent turns can follow it,
                    // while any failed attempt still rolls back to the durable account.
                    var movedForAttempt = _sessionAffinityStore.Move(
                        activeAffinityLease,
                        attemptAccountKey);
                    if (movedForAttempt != null)
                    {
                        activeAffinityLease = movedForAttempt;
                        affinityLeases.Add(movedForAttempt);
                    }
                    else if (lastQuotaResponse != null)
                    {
                        // A concurrent request changed this binding, or the request only
                        // carries an unproven previous_response_id (response aliases are
                        // intentionally non-movable). Preserve the original 429 instead of
                        // replaying a continuation through an account we cannot bind.
                        finalUpstreamResponse = lastQuotaResponse;
                        lastQuotaResponse = null;
                        credential = lastQuotaCredential!;
                        break;
                    }
                    else
                    {
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.Conflict,
                            "会话账号绑定刚刚被另一个请求更新；" +
                            "为避免跨账号重放，本次请求未继续发送，请重试。");
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
                    fingerprintPlan);
                HttpResponseMessage attemptResponse;
                try
                {
                    using var attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                        requestCancellationToken);
                    attemptDeadline.CancelAfter(UpstreamAttemptHeadersTimeout);
                    attemptResponse = await client.SendAsync(
                        upstreamRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        attemptDeadline.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    var requestWasCanceled = requestCancellationToken.IsCancellationRequested;
                    ManagerLifecycleDiagnostics.WriteException(
                        "pat-gateway-upstream-send-failed",
                        ex,
                        $"account={NormalizeOptionalAccountKey(credential.AccountKey) ?? "unbound"}; " +
                        $"request_canceled={requestWasCanceled}; " +
                        $"header_timeout_ms={(int)UpstreamAttemptHeadersTimeout.TotalMilliseconds}");
                    if (!requestCancellationToken.IsCancellationRequested &&
                        TryActivateRuntimeGlobalProxyFallback(
                            credential,
                            proxyResolution,
                            runtimeGlobalProxyFallbackAccounts,
                            "model-network-failure"))
                    {
                        continue;
                    }
                    if (lastQuotaResponse != null)
                    {
                        // SendAsync does not prove that the request was not accepted by
                        // the upstream before the connection failed. Never issue the
                        // same non-idempotent request to a third account in that
                        // indeterminate window; return the original quota response and
                        // let the client decide whether to retry.
                        lastQuotaResponse.Dispose();
                        lastQuotaResponse = null;
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.BadGateway,
                            "轮换账号的上游请求状态不确定，网关未继续换号；请由客户端决定是否重试。" +
                            SanitizeNetworkError(ex.Message));
                        return;
                    }
                    await WriteErrorAsync(
                        response,
                        HttpStatusCode.BadGateway,
                        requestWasCanceled
                            ? "通过本地代理请求 ChatGPT Codex 上游失败：" +
                              SanitizeNetworkError(ex.Message)
                            : "通过本地代理请求 ChatGPT Codex 上游失败：上游在 " +
                              $"{UpstreamAttemptHeadersTimeout.TotalSeconds:0} 秒内未返回响应头，" +
                              "可能是代理节点响应过慢；请稍后重试或更换代理节点。" +
                              "（" + SanitizeNetworkError(ex.Message) + "）");
                    return;
                }

                byte[]? quotaErrorBody = null;
                Upstream429Classification? quotaClassification = null;
                if (modelRequestActivity != null &&
                    (attemptResponse.StatusCode == HttpStatusCode.TooManyRequests ||
                     (credential.IsCompatibleApi && IsCompatibleQuotaStatus(attemptResponse.StatusCode))))
                {
                    quotaErrorBody = await BufferUpstreamErrorResponseAsync(
                        attemptResponse, requestCancellationToken);
                    quotaClassification = ClassifyQuotaFailure(
                        credential.IsCompatibleApi, attemptResponse, quotaErrorBody, DateTimeOffset.UtcNow);
                }

                if (quotaClassification?.IsQuotaExhausted != true && credential.IsCompatibleApi &&
                    IsCompatibleCompactRequest(context.Request.Url) &&
                    !attemptResponse.IsSuccessStatusCode)
                {
                    var compactErrorBody = await BufferUpstreamErrorResponseAsync(
                        attemptResponse,
                        requestCancellationToken);
                    if (IsCompatibleCompactUnsupportedResponse(
                            attemptResponse,
                            compactErrorBody))
                    {
                        attemptResponse.Dispose();
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.BadGateway,
                            "兼容上游不支持 /responses/compact。该请求没有回退到 ChatGPT OAuth，也没有记为账号额度耗尽；" +
                            "请改用原生支持 Responses Compact 的兼容上游。");
                        return;
                    }
                    if ((int)attemptResponse.StatusCode >= 500)
                    {
                        var upstreamStatus = (int)attemptResponse.StatusCode;
                        attemptResponse.Dispose();
                        await WriteErrorAsync(
                            response,
                            HttpStatusCode.BadGateway,
                            $"兼容上游已接收 /responses/compact，但返回 HTTP {upstreamStatus}，" +
                            "当前上游的 compact 能力不可用。这不是账号额度耗尽，且请求没有回退到 ChatGPT OAuth。");
                        return;
                    }
                }

                if (quotaClassification != null)
                {
                    var buffered429Body = quotaErrorBody!;
                    var classification = quotaClassification;
                    var transientKey = NormalizeOptionalAccountKey(credential.AccountKey) ??
                                       "unbound";
                    if (!classification.IsQuotaExhausted &&
                        classification.RequiresSameAccountConfirmation &&
                        replayableBody != null)
                    {
                        var prior = structured429Confirmations.GetValueOrDefault(transientKey);
                        var sameReset = prior.Count == 0 ||
                                        (!prior.ResetAtUtc.HasValue &&
                                         !classification.ResetAtUtc.HasValue) ||
                                        (prior.ResetAtUtc.HasValue &&
                                         classification.ResetAtUtc.HasValue &&
                                         (prior.ResetAtUtc.Value - classification.ResetAtUtc.Value)
                                         .Duration() <= TimeSpan.FromMinutes(2));
                        var confirmedCount = sameReset ? prior.Count + 1 : 1;
                        structured429Confirmations[transientKey] =
                            (confirmedCount, classification.ResetAtUtc);
                        if (confirmedCount >= StructuredQuota429ConfirmationCount)
                        {
                            classification = classification with
                            {
                                IsQuotaExhausted = true,
                                RequiresSameAccountConfirmation = false,
                                Reason = "structured-usage-limit-confirmed-three-times"
                            };
                        }
                    }
                    if (!classification.IsQuotaExhausted &&
                        classification.Reason == "unconfirmed-rate-limit")
                    {
                        // A single ambiguous 429 is deliberately not treated as an
                        // exhausted five-hour window.  When the same account returns the
                        // same signal three times in this one request, however, the
                        // upstream has supplied enough fresh evidence to try the next
                        // configured account.  The signature prevents unrelated/transient
                        // 429s from being combined, and the observation remains scoped to
                        // this request (no durable poison marker is written until the
                        // three-confirmation threshold is reached).
                        var signature = Build429ConfirmationSignature(
                            attemptResponse,
                            buffered429Body);
                        var prior = unconfirmed429Confirmations.GetValueOrDefault(transientKey);
                        var confirmedCount = prior.Count > 0 &&
                                              string.Equals(
                                                  prior.Signature,
                                                  signature,
                                                  StringComparison.Ordinal)
                            ? prior.Count + 1
                            : 1;
                        unconfirmed429Confirmations[transientKey] =
                            (confirmedCount, signature);
                        if (confirmedCount >= StructuredQuota429ConfirmationCount)
                        {
                            classification = classification with
                            {
                                IsQuotaExhausted = true,
                                Reason = "unconfirmed-rate-limit-confirmed-three-times"
                            };
                            ManagerLifecycleDiagnostics.Write(
                                "pat-gateway-http-429-confirmed",
                                $"account={transientKey}; confirmations={confirmedCount}; " +
                                "reason=unconfirmed-rate-limit; downstream_bytes=0");
                        }
                    }
                    if (!classification.IsQuotaExhausted)
                    {
                        var retryCount = transient429RetryCounts.GetValueOrDefault(transientKey);
                        var canRetrySameAccount =
                            (!context.Request.HasEntityBody || replayableBody != null) &&
                            retryCount < Transient429SameAccountRetryLimit;
                        if (canRetrySameAccount)
                        {
                            transient429RetryCounts[transientKey] = retryCount + 1;
                            var retryDelay = SelectTransient429RetryDelay(
                                attemptResponse,
                                retryCount);
                            ManagerLifecycleDiagnostics.Write(
                                "pat-gateway-http-429-transient-retry",
                                $"same_account_attempt={retryCount + 2}; " +
                                $"retry_delay_ms={(int)retryDelay.TotalMilliseconds}; " +
                                $"reason={classification.Reason}; downstream_bytes=0");
                            attemptResponse.Dispose();
                            if (retryDelay > TimeSpan.Zero)
                            {
                                await Task.Delay(retryDelay, requestCancellationToken);
                            }
                            continue;
                        }

                        // The same-account review is deliberately bounded.  Only after it
                        // is exhausted may a replayable request make a request-scoped
                        // candidate hop.  This is intentionally separate from the confirmed
                        // quota path below: no durable exhausted marker, route activation,
                        // or global cursor update is allowed for an unconfirmed 429.
                        var canReplayUnconfirmed429 = CanReplayUnconfirmed429AcrossAccounts(
                            requestHasEntityBody: context.Request.HasEntityBody,
                            hasReplayableBody: replayableBody != null,
                            strictResponseAffinity: strictResponseAffinity,
                            allowCrossAccountReplay: affinityRequest?.AllowCrossAccountReplay ?? true,
                            candidateFailoverCount: transient429CandidateFailoverCount);
                        if (canReplayUnconfirmed429 &&
                            !string.IsNullOrWhiteSpace(credential.AccountKey))
                        {
                            // The selector reads accounts.json and the latest settings on
                            // every call.  Marking this account only in the per-request
                            // hard-exclusion set prevents a reordered/mutated ring from
                            // selecting it again during this request.
                            attemptedAccountKeys.Add(credential.AccountKey);
                        }
                        if (canReplayUnconfirmed429 &&
                            TrySelectNextTransparentRotationCredential(
                                credential,
                                attemptedAccountKeys,
                                allowCompatibleApiRetry,
                                out var nextTransientCredential,
                                out var transientRetryOrdinal,
                                out var transientRetryPool,
                                allowBackupPool: false))
                        {
                            lastTransient429Response?.Dispose();
                            lastTransient429Response = attemptResponse;
                            WriteTransparentRotationDiagnostic(
                                "retry-selected",
                                transientRetryOrdinal,
                                transientRetryPool == AccountRotationPool.Primary
                                    ? "unconfirmed-429-primary"
                                    : "unconfirmed-429-backup");
                            ManagerLifecycleDiagnostics.Write(
                                "pat-gateway-http-429-transient-failover",
                                $"source_account={transientKey}; " +
                                $"candidate_ordinal={transientRetryOrdinal}; " +
                                $"same_account_retries={retryCount}; " +
                                "durable_quota_marker=false; global_cursor_advance=false; " +
                                "downstream_bytes=0");
                            credential = nextTransientCredential!;
                            transient429CandidateFailoverCount++;
                            transparentRetryCount++;
                            // A transient candidate hop is request-scoped.  Do not persist
                            // an ordinary session-affinity move merely because this turn
                            // happened to succeed on the fallback account.
                            preserveAffinityBindingOnSuccess = true;
                            continue;
                        }

                        // If every latest-ring candidate also returned an unconfirmed 429,
                        // return the newest real upstream response.  Discard remembered
                        // confirmed/temporary responses so they cannot leak or mask the
                        // final 429.  A subsequent request gets a fresh candidate review.
                        lastTransient429Response?.Dispose();
                        lastTransient429Response = null;
                        lastQuotaResponse?.Dispose();
                        lastQuotaResponse = null;
                        finalUpstreamResponse = attemptResponse;
                        ManagerLifecycleDiagnostics.Write(
                            "pat-gateway-http-429-transient-returned",
                            $"same_account_retries={retryCount}; " +
                            $"candidate_failovers={transient429CandidateFailoverCount}; " +
                            $"replayable={canReplayUnconfirmed429}; " +
                            $"reason={classification.Reason}; downstream_bytes=0");
                        break;
                    }

                    // A prior temporary 429 is no longer needed once this account has
                    // supplied explicit exhaustion evidence.  The confirmed path below
                    // owns the response remembered for transparent quota failover.
                    lastTransient429Response?.Dispose();
                    lastTransient429Response = null;
                    RecordQuotaLimited(
                        credential.AccountKey,
                        classification.Reason,
                        classification.ResetAtUtc);
                    if (PatGatewayRotationStore.TryNormalizeAccountKey(
                            credential.AccountKey,
                            out var quotaAccountKey) &&
                        PatGatewayRotationStore.TryNormalizeAccountKey(
                            globalRouteSourceAccountKey,
                            out var quotaGlobalSourceKey) &&
                        quotaAccountKey.Equals(quotaGlobalSourceKey, StringComparison.Ordinal))
                    {
                        globalRouteQuotaSeen = true;
                    }
                    if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                    {
                        attemptedAccountKeys.Add(credential.AccountKey);
                    }

                    var canReplay = !strictResponseAffinity &&
                                    (affinityRequest?.AllowCrossAccountReplay ?? true) &&
                                    (!context.Request.HasEntityBody || replayableBody != null);
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
                        // A quota response is an explicit safe-replay signal. If this
                        // request later succeeds on the next account, its ordinary
                        // session route may be durably rebound as in sub2api.
                        preserveAffinityBindingOnSuccess = false;
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

                // A trusted compatible-API relay commonly reports an exhausted shared
                // pool as HTTP 503 instead of the official 429 envelope.  Treat that
                // response as a bounded, request-scoped failover signal: replay only
                // before any downstream byte is written, walk the latest configured
                // ring once, and never persist the account as exhausted merely because
                // a provider returned a generic 503.  Official OAuth/PAT accounts stay
                // conservative and require an explicit quota/capacity marker so a
                // transient ChatGPT outage cannot unexpectedly move a live session.
                if (modelRequestActivity != null &&
                    attemptResponse.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    var buffered503Body = await BufferUpstreamErrorResponseAsync(
                        attemptResponse,
                        requestCancellationToken);
                    var retryable503 = credential.IsCompatibleApi
                        ? true
                        : IsQuotaOrCapacityUnavailable503(attemptResponse, buffered503Body);
                    if (retryable503)
                    {
                        if (PatGatewayRotationStore.TryNormalizeAccountKey(
                                credential.AccountKey,
                                out var unavailable503AccountKey) &&
                            PatGatewayRotationStore.TryNormalizeAccountKey(
                                globalRouteSourceAccountKey,
                                out var global503SourceKey) &&
                            unavailable503AccountKey.Equals(
                                global503SourceKey,
                                StringComparison.Ordinal))
                        {
                            // Reuse the existing commit gate for transparent failover:
                            // a successful retry must advance the durable cursor even
                            // when the relay used 503 rather than an official 429.
                            globalRouteQuotaSeen = true;
                        }
                        if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                        {
                            attemptedAccountKeys.Add(credential.AccountKey);
                        }

                        var canReplay503 = !strictResponseAffinity &&
                                           (affinityRequest?.AllowCrossAccountReplay ?? true) &&
                                           (!context.Request.HasEntityBody || replayableBody != null);
                        if (canReplay503 &&
                            TrySelectNextTransparentRotationCredential(
                                credential,
                                attemptedAccountKeys,
                                allowCompatibleApiRetry,
                                out var next503Credential,
                                out var retry503Ordinal,
                                out var retry503Pool))
                        {
                            lastQuotaResponse?.Dispose();
                            lastQuotaResponse = attemptResponse;
                            lastQuotaCredential = credential;
                            WriteTransparentRotationDiagnostic(
                                "retry-selected",
                                retry503Ordinal,
                                retry503Pool == AccountRotationPool.Primary ?
                                    "primary-503" : "backup-503");
                            ManagerLifecycleDiagnostics.Write(
                                "pat-gateway-http-503-failover",
                                $"account={NormalizeOptionalAccountKey(credential.AccountKey) ?? "unbound"}; " +
                                $"compatible_api={credential.IsCompatibleApi}; " +
                                $"candidate_ordinal={retry503Ordinal}; downstream_bytes=0");
                            credential = next503Credential!;
                            transparentRetryCount++;
                            continue;
                        }
                    }

                    // Keep the actual upstream 503 (including its body and Retry-After)
                    // when there is no safe candidate.  This is preferable to turning a
                    // provider outage into a synthetic 502 and lets Codex perform its
                    // normal bounded retry policy.
                    if (lastQuotaResponse != null)
                    {
                        lastQuotaResponse.Dispose();
                        lastQuotaResponse = null;
                    }
                    finalUpstreamResponse = attemptResponse;
                    ManagerLifecycleDiagnostics.Write(
                        "pat-gateway-http-503-returned",
                        $"compatible_api={credential.IsCompatibleApi}; " +
                        $"retryable={retryable503}; downstream_bytes=0");
                    break;
                }

                if (lastQuotaResponse == null &&
                    attemptResponse.StatusCode is HttpStatusCode.Unauthorized or
                        HttpStatusCode.Forbidden &&
                    TrySelectGlobalCredentialAfterAffinityFailure(
                        credential,
                        globalRouteCredential,
                        strictResponseAffinity,
                        activeAffinityLease,
                        out var globalAfterRejectedStickyResponse))
                {
                    attemptResponse.Dispose();
                    if (!string.IsNullOrWhiteSpace(credential.AccountKey))
                    {
                        attemptedAccountKeys.Add(credential.AccountKey);
                    }
                    credential = globalAfterRejectedStickyResponse!;
                    WriteTransparentRotationDiagnostic(
                        "affinity-fallback",
                        Math.Max(1, attemptedAccountKeys.Count),
                        "credential-response-rejected");
                    continue;
                }

                if (lastQuotaResponse != null &&
                    (attemptResponse.StatusCode is HttpStatusCode.Unauthorized or
                        HttpStatusCode.Forbidden))
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
                if (lastTransient429Response != null)
                {
                    lastTransient429Response.Dispose();
                    lastTransient429Response = null;
                    ManagerLifecycleDiagnostics.Write(
                        "pat-gateway-http-429-transient-failover-succeeded",
                        $"candidate_failovers={transient429CandidateFailoverCount}; " +
                        "durable_quota_marker=false; global_cursor_advance=false; " +
                        "downstream_bytes=0");
                }
                hadSuccessfulTransparentRetry = lastQuotaResponse != null;
                if (lastQuotaResponse != null)
                {
                    lastQuotaResponse.Dispose();
                    lastQuotaResponse = null;
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
                var responseIdObservation = await CopyUpstreamResponseAsync(
                    upstreamResponse,
                    response,
                    observeResponseId: isModelRequest &&
                                       !isIndependentAccountProbe &&
                                       upstreamResponse.IsSuccessStatusCode,
                    cancellationToken: requestCancellationToken);

                // The manager uses this successful, completed account marker to keep its
                // current-account label and official quota refresh aligned with the
                // credential that actually produced the response.  It is updated only
                // after the response has been copied through to EOF, and explicit quota
                // probes are excluded so a test never steals the running session's label.
                // A trusted compatible relay is allowed to omit the Responses terminal/id
                // envelope (some relays return a valid 2xx stream with a non-standard
                // content type).  Requiring CanConfirmSession here made every transparent
                // retry succeed for the user but left the global cursor and UI on the
                // exhausted source.  An explicit failure/ambiguous observer result still
                // blocks the marker; otherwise a completed HTTP 2xx is the account that
                // served this turn.
                var responseCompletedSuccessfully =
                    upstreamResponse.IsSuccessStatusCode &&
                    responseIdObservation is not
                    {
                        SawTerminalFailure: true
                    } &&
                    responseIdObservation is not
                    {
                        IsAmbiguous: true
                    };
                if (isModelRequest &&
                    !isIndependentAccountProbe &&
                    responseCompletedSuccessfully)
                {
                    // A 2xx envelope can still carry response.failed, an incomplete
                    // stream, or malformed JSON.  The observer rejects an explicit failure
                    // or conflicting response id; a relay that does not expose those
                    // fields is still represented by the completed HTTP status above.
                    RecordSuccessfulModelRequest(
                        credential.AccountKey,
                        modelRequestActivity?.StartedAtUtc,
                        successfulProxyNodeId,
                        successfulRequestUsedGlobalProxy);
                }

                if (isModelRequest && !isIndependentAccountProbe && affinityRequest != null &&
                    upstreamResponse.IsSuccessStatusCode &&
                    responseIdObservation is
                        { CanConfirmSession: true } &&
                    PatGatewayRotationStore.TryNormalizeAccountKey(
                        credential.AccountKey,
                        out var successfulAccountKey))
                {
                    PatGatewaySessionAffinityLease? leaseToConfirm =
                        preserveAffinityBindingOnSuccess ? null : activeAffinityLease;
                    if (!preserveAffinityBindingOnSuccess &&
                        activeAffinityLease != null &&
                        !activeAffinityLease.AccountKey.Equals(
                            successfulAccountKey,
                            StringComparison.Ordinal) &&
                        !strictResponseAffinity)
                    {
                        var movedToWinner = _sessionAffinityStore.Move(
                            activeAffinityLease,
                            successfulAccountKey);
                        if (movedToWinner != null)
                        {
                            activeAffinityLease = movedToWinner;
                            affinityLeases.Add(movedToWinner);
                            leaseToConfirm = movedToWinner;
                        }
                    }

                    // A temporary identity-network escape may serve this turn through
                    // the global account, but it must not overwrite the old durable
                    // session route. Release that provisional move while still binding
                    // any newly observed response ID to the account that produced it.
                    // For a durable move, clear superseded provisional versions before
                    // binding an input previous_response_id that was unknown when
                    // routing began. Never release the current winner before aliases
                    // are confirmed.
                    for (var index = affinityLeases.Count - 1; index >= 0; index--)
                    {
                        var historical = affinityLeases[index];
                        if (leaseToConfirm != null &&
                            historical.Version == leaseToConfirm.Version &&
                            historical.AccountKey.Equals(
                                leaseToConfirm.AccountKey,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }
                        _ = _sessionAffinityStore.ReleaseFailed(historical);
                    }

                    var responseIds = responseIdObservation is
                        { CanConfirm: true, ResponseId: { } successfulResponseId }
                        ? new[] { successfulResponseId }
                        : Array.Empty<string>();
                    var affinityMutation = _sessionAffinityStore.ConfirmAndBindResponses(
                        leaseToConfirm,
                        responseIds,
                        successfulAccountKey);
                    if (!affinityMutation.Persisted)
                    {
                        ManagerLifecycleDiagnostics.Write(
                            "pat-gateway-session-affinity-persist-failed",
                            "response_completed=true; downstream_complete=true");
                    }
                }

                // Advance the user-visible global rotation cursor only after the retry
                // response has been copied to EOF and passed the Responses observer. A
                // 200 response carrying response.failed, malformed JSON, or a truncated
                // SSE stream must never commit a quota rotation. Use the credential that
                // actually produced the remembered 429 (rather than the first sticky
                // attempt), while still requiring it to be the current global route so a
                // sticky-only account cannot move the global ring.
                if (hadSuccessfulTransparentRetry &&
                    responseCompletedSuccessfully &&
                    globalRouteQuotaSeen &&
                    PatGatewayRotationStore.TryNormalizeAccountKey(
                        globalRouteSourceAccountKey,
                        out var expectedGlobalSourceAccountKey) &&
                    PatGatewayRotationStore.TryNormalizeAccountKey(
                        credential.AccountKey,
                        out var retryTargetAccountKey) &&
                    !expectedGlobalSourceAccountKey.Equals(
                        retryTargetAccountKey,
                        StringComparison.Ordinal))
                {
                    await CommitTransparentRotationAsync(
                        expectedGlobalSourceAccountKey,
                        retryTargetAccountKey,
                        requestCancellationToken);
                }
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
            // This is intentionally unconditional. Confirmed leases no longer have a
            // provisional owner, so ReleaseFailed is a no-op for successful requests;
            // failed or superseded attempts restore their last durable binding. Keeping
            // every version in the list also cleans an unmoved, unknown response-id alias.
            for (var index = affinityLeases.Count - 1; index >= 0; index--)
            {
                _ = _sessionAffinityStore.ReleaseFailed(affinityLeases[index]);
            }
            // Buffered quota/error responses are never sent directly by the downstream
            // response path.  Dispose any that remain after an early error, cancellation,
            // or a successful request so a long-lived gateway cannot accumulate them.
            lastTransient429Response?.Dispose();
            lastQuotaResponse?.Dispose();
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
        var proxy = ResolveProxyForCredential(null).ProxyUri;
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
                    lastSuccessfulModelRequestAccountKey =
                        activity.LastSuccessfulModelRequestAccountKey,
                    lastQuotaLimitedAccountKey = activity.LastQuotaLimitedAccountKey,
                    lastQuotaLimitedSequence = activity.LastQuotaLimitedSequence,
                    lastModelRequestStartedAtUnixMs = activity.LastModelRequestStartedAtUtc?.ToUnixTimeMilliseconds(),
                    lastModelRequestCompletedAtUnixMs = activity.LastModelRequestCompletedAtUtc?.ToUnixTimeMilliseconds(),
                    lastSuccessfulModelRequestCompletedAtUnixMs =
                        activity.LastSuccessfulModelRequestCompletedAtUtc?.ToUnixTimeMilliseconds(),
                    lastSuccessfulModelRequestStartedAtUnixMs =
                        activity.LastSuccessfulModelRequestStartedAtUtc?.ToUnixTimeMilliseconds(),
                    lastSuccessfulModelRequestProxyNodeId =
                        activity.LastSuccessfulModelRequestProxyNodeId,
                    lastSuccessfulModelRequestUsedGlobalProxy =
                        activity.LastSuccessfulModelRequestUsedGlobalProxy,
                    lastQuotaLimitedAtUnixMs = activity.LastQuotaLimitedAtUtc?.ToUnixTimeMilliseconds()
                },
                rotation = BuildRotationResponse(rotation)
            });
    }

    private async Task HandleRotationArmAsync(
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        // Control responses must advertise the same rotation contract as /healthz.
        // ArmRotationAsync validates this header before accepting the returned route;
        // without it the gateway can persist a valid route while the Manager reports a
        // false "incompatible protocol" failure to the user.
        response.Headers[LocalPatGateway.RotationProtocolHeader] =
            LocalPatGateway.RotationProtocolValue;

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

        string? transport = null;
        if (command.TransportAccountKey != null &&
            !PatGatewayRotationStore.TryNormalizeAccountKey(
                command.TransportAccountKey,
                out transport))
        {
            await WriteErrorAsync(response, HttpStatusCode.BadRequest, "PAT 轮换传输账号哈希无效。");
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
                if (transport != null)
                {
                    _ = ResolveRotationCredential(transport);
                }
                rotation = _rotationStore.Arm(
                    source,
                    target,
                    DateTimeOffset.UtcNow,
                    command.ReplaceExistingArmedTarget,
                    transport);
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
        response.Headers[LocalPatGateway.RotationProtocolHeader] =
            LocalPatGateway.RotationProtocolValue;

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

    private async Task HandleSessionAffinityInvalidateAsync(
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        response.Headers[LocalPatGateway.RotationProtocolHeader] =
            LocalPatGateway.RotationProtocolValue;

        if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.MethodNotAllowed,
                "请使用 POST 清理账号的普通会话粘性。");
            return;
        }

        byte[] payload;
        try
        {
            payload = await ReadBoundedRequestBodyAsync(request, maximumBytes: 4096);
        }
        catch (InvalidDataException)
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.BadRequest,
                "会话粘性清理请求体无效。");
            return;
        }
        if (!LocalPatGatewayControl.ValidateRequest(
                request,
                _controlSecret,
                BuildSessionAffinityInvalidationPurpose(payload)))
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.Unauthorized,
                "Gateway control request was not authenticated.");
            return;
        }

        SessionAffinityInvalidateRequest? command;
        try
        {
            command = JsonSerializer.Deserialize<SessionAffinityInvalidateRequest>(payload);
        }
        catch (JsonException)
        {
            command = null;
        }
        if (command == null ||
            !PatGatewayRotationStore.TryNormalizeAccountKey(
                command.AccountKey,
                out var accountKey))
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.BadRequest,
                "会话粘性清理账号哈希无效。");
            return;
        }

        var mutation = _sessionAffinityStore.InvalidateOrdinaryBindingsForAccount(accountKey);
        if (!mutation.Persisted)
        {
            await WriteErrorAsync(
                response,
                HttpStatusCode.InternalServerError,
                "普通会话粘性已从内存移除，但持久化清理失败。");
            return;
        }
        await WriteJsonAsync(
            response,
            HttpStatusCode.OK,
            new
            {
                status = "invalidated",
                changed = mutation.Applied,
                strictResponseBindingsPreserved = true
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
                    var route = DiscardStaleRotationRoute(_rotationStore.Load());
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
                        route = DiscardStaleRotationRoute(_rotationStore.Load());
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

    private PatGatewayRotationSnapshot DiscardStaleRotationRoute(
        PatGatewayRotationSnapshot route)
    {
        if (route.Status == PatGatewayRotationStatus.None ||
            string.IsNullOrWhiteSpace(route.TargetAccountKey))
        {
            return route;
        }

        List<AccountRecord> accounts;
        try
        {
            accounts = _accountStore.LoadAccounts();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or InvalidOperationException or NotSupportedException or
            ArgumentException)
        {
            ManagerLifecycleDiagnostics.WriteException(
                "pat-gateway-route-revalidation-deferred",
                ex,
                $"read=accounts; attempts={AtomicFilePersistence.DefaultAttempts}; " +
                $"status={route.Status}; transport={route.TransportAccountKey}; " +
                $"source={route.SourceAccountKey}; target={route.TargetAccountKey}; " +
                "route_preserved=true; downstream_bytes=0");
            return route;
        }

        if (!_themeService.TryLoadSettings(out var settings, out var settingsError))
        {
            ManagerLifecycleDiagnostics.WriteException(
                "pat-gateway-route-revalidation-deferred",
                settingsError ?? new IOException("Settings read failed after bounded retries."),
                $"read=settings; attempts={AtomicFilePersistence.DefaultAttempts}; " +
                $"status={route.Status}; transport={route.TransportAccountKey}; " +
                $"source={route.SourceAccountKey}; target={route.TargetAccountKey}; " +
                "route_preserved=true; downstream_bytes=0");
            return route;
        }

        string invalidReason;
        try
        {
            _ = AccountRotationConfiguration.Normalize(settings, accounts);
            invalidReason = GetRotationRouteIneligibilityReason(
                                route,
                                settings,
                                accounts,
                                DateTimeOffset.UtcNow) ??
                            string.Empty;
            if (invalidReason.Length == 0)
            {
                return route;
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or InvalidOperationException or NotSupportedException or
            ArgumentException)
        {
            ManagerLifecycleDiagnostics.WriteException(
                "pat-gateway-route-revalidation-deferred",
                ex,
                $"read=normalized-configuration; attempts=1; status={route.Status}; " +
                $"transport={route.TransportAccountKey}; source={route.SourceAccountKey}; " +
                $"target={route.TargetAccountKey}; route_preserved=true; downstream_bytes=0");
            return route;
        }

        _rotationStore.Clear();
        ManagerLifecycleDiagnostics.Write(
            "pat-gateway-stale-route-cleared",
            $"reason={invalidReason}; status={route.Status}; " +
            $"transport={route.TransportAccountKey}; source={route.SourceAccountKey}; " +
            $"target={route.TargetAccountKey}; downstream_bytes=0");
        return PatGatewayRotationSnapshot.Empty;
    }

    private static bool IsRotationRouteEligible(
        PatGatewayRotationSnapshot route,
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        DateTimeOffset nowUtc) =>
        GetRotationRouteIneligibilityReason(route, settings, accounts, nowUtc) == null;

    private static string? GetRotationRouteIneligibilityReason(
        PatGatewayRotationSnapshot route,
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        DateTimeOffset nowUtc)
    {
        if (route.Status == PatGatewayRotationStatus.None)
        {
            return null;
        }
        if (!AccountRotationConfiguration.IsEnabled(settings))
        {
            return "rotation_disabled";
        }
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(
                route.SourceAccountKey,
                out var sourceKey))
        {
            return "source_key_invalid";
        }
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(
                route.TargetAccountKey,
                out var targetKey))
        {
            return "target_key_invalid";
        }

        var source = accounts.FirstOrDefault(account =>
            QuotaAccountIdentity.CreateKey(account).Equals(sourceKey, StringComparison.Ordinal));
        var target = accounts.FirstOrDefault(account =>
            QuotaAccountIdentity.CreateKey(account).Equals(targetKey, StringComparison.Ordinal));
        if (source == null)
        {
            return "source_account_missing";
        }
        if (target == null)
        {
            return "target_account_missing";
        }
        if (AccountRotationConfiguration.GetPool(settings, source) ==
            AccountRotationPool.None)
        {
            return "source_not_in_rotation_pool";
        }
        if (AccountRotationConfiguration.GetPool(settings, target) ==
            AccountRotationPool.None)
        {
            return "target_not_in_rotation_pool";
        }

        if (route.Status != PatGatewayRotationStatus.Armed)
        {
            return null;
        }
        if (route.ArmedAtUtc is not { } armedAtUtc ||
            armedAtUtc > nowUtc.AddSeconds(5))
        {
            return "armed_timestamp_invalid";
        }
        if (nowUtc - armedAtUtc > ArmedRotationMaximumLifetime)
        {
            return "armed_route_timed_out";
        }

        // A manager-prepared route belongs to the quota window that armed it.  If that
        // window already reset while Codex was idle, activating the route hours later is
        // precisely the stale source-to-target route failure this guard prevents. A manual
        // route armed after that reset belongs to the new window and must remain eligible;
        // otherwise an old persisted reset marker clears every force-switch at its first
        // live request boundary.
        if (settings.AccountRotationResetAtUtc.TryGetValue(sourceKey, out var resetAtUtc))
        {
            var usableAtUtc = resetAtUtc +
                              AccountRotationConfiguration.PrimaryResetGracePeriod;
            if (usableAtUtc <= nowUtc && armedAtUtc < usableAtUtc)
            {
                return "source_quota_window_reset_after_arm";
            }
        }
        return null;
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

    private static bool ShouldApplyRotationAtRequestBoundary(
        bool isModelRequest,
        bool isIndependentAccountProbe) =>
        isModelRequest && !isIndependentAccountProbe;

    private static bool ShouldRejectSharedOAuthModelRequest(
        bool isModelRequest, bool isPatOrApi, bool sharedDualLogin, bool authenticatedAccountProbe) =>
        isModelRequest && !isPatOrApi && sharedDualLogin && !authenticatedAccountProbe;

    private bool TrySelectNextTransparentRotationCredential(
        GatewayCredential current,
        ISet<string> attemptedAccountKeys,
        bool allowCompatibleApi,
        out GatewayCredential? credential,
        out int candidateOrdinal,
        out AccountRotationPool pool,
        bool allowBackupPool = true)
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
            // AccountRotationConfiguration migrates pre-2.2.8 unconfirmed markers once.
            // Keep the remaining reset times: they now represent confirmed evidence and,
            // like sub2api's rate_limit_reset_at, must keep an exhausted B out of the ring
            // until its reset instead of probing B and C on every later request.
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
        foreach (var pair in _quotaLimitedAccounts.ToArray())
        {
            if (!IsQuotaLimitObservationActive(pair.Value, now))
            {
                _quotaLimitedAccounts.TryRemove(pair.Key, out _);
                continue;
            }
            unavailable.Add(pair.Key);
            if (pair.Value.ResetAtUtc is { } resetAtUtc)
            {
                settings.AccountRotationResetAtUtc[pair.Key] = resetAtUtc;
            }
        }

        while (true)
        {
            if (!AccountRotationConfiguration.TrySelectNextCandidate(
                    settings,
                    accounts,
                    currentAccount,
                    unavailable,
                    now,
                    hasUsableCredential: static _ => true,
                    out var candidate,
                    out pool,
                    canSelectAccount: account => allowCompatibleApi || !account.IsCompatibleApi,
                    hardUnavailableAccountKeys: attemptedAccountKeys))
            {
                return false;
            }

            var selectedCandidate = candidate!;
            var candidateKey = QuotaAccountIdentity.CreateKey(selectedCandidate);
            pool = AccountRotationConfiguration.GetPool(settings, selectedCandidate);
            if (!allowBackupPool && pool == AccountRotationPool.Backup)
            {
                // A bounded failover for an ambiguous/transient 429 may inspect other primary
                // accounts, but it must never consume the emergency pool. Backup becomes
                // eligible only after confirmed exhaustion/capacity handling reaches this path.
                return false;
            }
            unavailable.Add(candidateKey);
            attemptedAccountKeys.Add(candidateKey);
            candidateOrdinal = Math.Max(1, attemptedAccountKeys.Count);
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

    private static bool TrySelectGlobalCredentialAfterAffinityFailure(
        GatewayCredential current,
        GatewayCredential globalRouteCredential,
        bool strictResponseAffinity,
        PatGatewaySessionAffinityLease? affinityLease,
        out GatewayCredential? fallback)
    {
        fallback = null;
        if (strictResponseAffinity || affinityLease == null ||
            !PatGatewayRotationStore.TryNormalizeAccountKey(
                current.AccountKey,
                out var currentAccountKey) ||
            !PatGatewayRotationStore.TryNormalizeAccountKey(
                globalRouteCredential.AccountKey,
                out var globalAccountKey) ||
            currentAccountKey.Equals(globalAccountKey, StringComparison.Ordinal))
        {
            return false;
        }

        fallback = globalRouteCredential;
        return true;
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
                AllowIncomingChatGptIdentity: false,
                CompatibleApiModels: CodexCliService.GetCompatibleApiAllowedModels(account));
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

    private CodexFingerprintPlan ResolveCodexFingerprintPlan(
        GatewayCredential credential,
        HttpListenerRequest incoming)
    {
        if (string.IsNullOrWhiteSpace(credential.AccountKey))
        {
            return CodexFingerprintConvergence.CreatePlan(
                credential.AccountKey,
                seed: null,
                clientSessionId: null,
                mode: CodexFingerprintMode.GatewayDefault);
        }

        var settings = _themeService.LoadSettings();
        var mode = AccountRotationConfiguration.GetFingerprintMode(
            settings,
            credential.AccountKey);
        var contentEncoding = incoming.Headers["Content-Encoding"]?.Trim();
        if (CodexFingerprintConvergence.RequiresSeed(mode) &&
            !string.IsNullOrWhiteSpace(contentEncoding) &&
            !contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            // Rewriting only the headers of a compressed body would expose two different
            // identities. Fail closed to the historical gateway behavior for this attempt.
            mode = CodexFingerprintMode.GatewayDefault;
        }
        var plan = CodexFingerprintConvergence.CreatePlan(
            credential.AccountKey,
            AccountRotationConfiguration.GetFingerprintSeed(settings, credential.AccountKey),
            CodexFingerprintConvergence.ExtractClientSessionId(incoming.Headers),
            mode);
        if (CodexFingerprintConvergence.RequiresSeed(mode) && !plan.HasConvergedIdentifiers)
        {
            // Never silently turn a requested convergence mode into raw pass-through when
            // its manager-owned seed is missing or invalid.
            return CodexFingerprintConvergence.CreatePlan(
                credential.AccountKey,
                seed: null,
                clientSessionId: null,
                mode: CodexFingerprintMode.GatewayDefault);
        }
        return plan;
    }

    private static bool TokenHashesEqual(string first, string second)
    {
        var firstHash = SHA256.HashData(Encoding.UTF8.GetBytes(first));
        var secondHash = SHA256.HashData(Encoding.UTF8.GetBytes(second));
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static string BuildRotationArmPurpose(ReadOnlySpan<byte> payload) =>
        "rotation-arm\n" + Convert.ToHexString(SHA256.HashData(payload));

    private static string BuildSessionAffinityInvalidationPurpose(ReadOnlySpan<byte> payload) =>
        "session-affinity-invalidate\n" + Convert.ToHexString(SHA256.HashData(payload));

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

        var path = upstreamUri.AbsolutePath;
        return path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/responses/compact", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompatibleCompactRequest(Uri? incoming) =>
        incoming?.AbsolutePath.Equals(
            "/backend-api/codex/responses/compact",
            StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCompatibleCompactUnsupportedResponse(
        HttpResponseMessage response,
        ReadOnlySpan<byte> body)
    {
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or
            HttpStatusCode.NotImplemented)
        {
            return true;
        }

        if (body.IsEmpty)
        {
            return false;
        }
        var text = Encoding.UTF8.GetString(body);
        return text.Contains("not_supported", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unsupported", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("compact", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIndependentAccountProbe(HttpListenerRequest request)
    {
        var purpose = request.Headers[LocalPatGateway.RequestPurposeHeader]?.Trim();
        return string.Equals(
            purpose,
            LocalPatGateway.QuotaTestRequestPurpose,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> BufferUpstreamErrorResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var originalContent = response.Content;
        var contentHeaders = originalContent.Headers
            .Select(pair => new KeyValuePair<string, string[]>(pair.Key, pair.Value.ToArray()))
            .ToArray();
        var body = await originalContent.ReadAsByteArrayAsync(cancellationToken);
        var replacement = new ByteArrayContent(body);
        foreach (var pair in contentHeaders)
        {
            _ = replacement.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
        response.Content = replacement;
        originalContent.Dispose();
        return body.Length <= MaxUpstreamErrorBodyBytes
            ? body
            : body[..MaxUpstreamErrorBodyBytes];
    }

    private static Upstream429Classification ClassifyUpstream429(
        HttpResponseMessage response,
        byte[] responseBody,
        DateTimeOffset observedAtUtc)
    {
        var hasExplicitRemainingQuota = false;
        foreach (var prefix in new[] { "primary", "secondary" })
        {
            if (!TryReadFiniteHeaderDouble(
                    response,
                    $"x-codex-{prefix}-used-percent",
                    out var usedPercent))
            {
                continue;
            }
            if (usedPercent < 100D)
            {
                hasExplicitRemainingQuota = true;
                continue;
            }

            DateTimeOffset? resetAtUtc = null;
            if (TryReadFiniteHeaderDouble(
                    response,
                    $"x-codex-{prefix}-reset-after-seconds",
                    out var resetSeconds) &&
                resetSeconds > 0D)
            {
                resetAtUtc = observedAtUtc.AddSeconds(
                    Math.Min(resetSeconds, TimeSpan.FromDays(31).TotalSeconds));
            }
            return new Upstream429Classification(
                true,
                $"codex-{prefix}-100-percent",
                resetAtUtc);
        }

        if (TryReadStructuredQuotaExhaustion(responseBody, observedAtUtc, out var bodyResetAtUtc))
        {
            return new Upstream429Classification(
                false,
                hasExplicitRemainingQuota
                    ? "structured-usage-limit-with-explicit-remaining-quota"
                    : "structured-usage-limit-pending-confirmation",
                bodyResetAtUtc,
                RequiresSameAccountConfirmation: !hasExplicitRemainingQuota);
        }

        return new Upstream429Classification(false, "unconfirmed-rate-limit", null);
    }

    private static bool IsQuotaOrCapacityUnavailable503(
        HttpResponseMessage response,
        byte[] responseBody)
    {
        // Do not rotate an official account on every infrastructure 503.  Only an
        // explicit quota/capacity indication is strong enough for that path; compatible
        // API credentials are handled by the caller as trusted relay pool failures.
        foreach (var headerName in new[]
                 {
                     "x-codex-primary-used-percent",
                     "x-codex-secondary-used-percent"
                 })
        {
            if (TryReadFiniteHeaderDouble(response, headerName, out var usedPercent) &&
                usedPercent >= 100D)
            {
                return true;
            }
        }

        if (response.Headers.RetryAfter != null && responseBody.Length == 0)
        {
            // A provider that supplies Retry-After with an empty 503 body is commonly
            // signalling a saturated route.  Keep this as a single request-scoped hint;
            // no durable exhausted marker is written.
            return true;
        }

        var text = Encoding.UTF8.GetString(responseBody).ToLowerInvariant();
        return text.Contains("quota", StringComparison.Ordinal) ||
               text.Contains("rate_limit", StringComparison.Ordinal) ||
               text.Contains("rate limit", StringComparison.Ordinal) ||
               text.Contains("usage_limit", StringComparison.Ordinal) ||
               text.Contains("usage limit", StringComparison.Ordinal) ||
               text.Contains("capacity", StringComparison.Ordinal) ||
               text.Contains("overloaded", StringComparison.Ordinal) ||
               text.Contains("exhaust", StringComparison.Ordinal) ||
               text.Contains("insufficient", StringComparison.Ordinal) ||
               text.Contains("暂时不可用", StringComparison.Ordinal) ||
               text.Contains("额度", StringComparison.Ordinal);
    }

    private static string Build429ConfirmationSignature(
        HttpResponseMessage response,
        ReadOnlySpan<byte> responseBody)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(responseBody));
        var primary = response.Headers.TryGetValues(
                "x-codex-primary-used-percent",
                out var primaryValues)
            ? primaryValues.FirstOrDefault() ?? string.Empty
            : string.Empty;
        var secondary = response.Headers.TryGetValues(
                "x-codex-secondary-used-percent",
                out var secondaryValues)
            ? secondaryValues.FirstOrDefault() ?? string.Empty
            : string.Empty;
        // Retry-After commonly counts down or is regenerated between otherwise identical
        // retries. It must not split one persistent 429 into unrelated confirmations.
        return bodyHash + "|" + primary + "|" + secondary;
    }

    private static bool CanReplayUnconfirmed429AcrossAccounts(
        bool requestHasEntityBody,
        bool hasReplayableBody,
        bool strictResponseAffinity,
        bool allowCrossAccountReplay,
        int candidateFailoverCount) =>
        !strictResponseAffinity &&
        allowCrossAccountReplay &&
        (!requestHasEntityBody || hasReplayableBody) &&
        candidateFailoverCount < Transient429CandidateFailoverLimit;

    private static bool TryReadFiniteHeaderDouble(
        HttpResponseMessage response,
        string name,
        out double value)
    {
        value = 0D;
        if (!response.Headers.TryGetValues(name, out var values) &&
            !response.Content.Headers.TryGetValues(name, out values))
        {
            return false;
        }
        return double.TryParse(
                   values.FirstOrDefault()?.Trim(),
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value) &&
               double.IsFinite(value);
    }

    private static bool TryReadStructuredQuotaExhaustion(
        byte[] body,
        DateTimeOffset observedAtUtc,
        out DateTimeOffset? resetAtUtc)
    {
        resetAtUtc = null;
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var hasQuotaMarker = false;
            DateTimeOffset? parsedResetAtUtc = null;
            Visit(document.RootElement);
            resetAtUtc = parsedResetAtUtc;
            return hasQuotaMarker && parsedResetAtUtc.HasValue;

            void Visit(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        var normalizedName = property.Name.Trim().ToLowerInvariant();
                        if ((normalizedName is "type" or "code" or "error_type" or "error_code") &&
                            property.Value.ValueKind == JsonValueKind.String &&
                            IsExplicitQuotaExhaustionMarker(property.Value.GetString()))
                        {
                            hasQuotaMarker = true;
                        }
                        else if (normalizedName is "resets_at" or "reset_at" or
                                 "rate_limit_reset_at" or "resets_in_seconds" or
                                 "reset_after_seconds" or "retry_after_seconds")
                        {
                            var parsed = Parse429ResetValue(
                                normalizedName,
                                property.Value,
                                observedAtUtc);
                            if (parsed.HasValue &&
                                (!parsedResetAtUtc.HasValue ||
                                 parsed.Value > parsedResetAtUtc.Value))
                            {
                                parsedResetAtUtc = parsed;
                            }
                        }
                        Visit(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        Visit(item);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsExplicitQuotaExhaustionMarker(string? value) =>
        value?.Trim().ToLowerInvariant() is
            "usage_limit_reached" or
            "usage_limit_exceeded" or
            "quota_exhausted" or
            "quota_limit_reached" or
            "insufficient_quota" or
            "billing_hard_limit_reached";

    private static DateTimeOffset? Parse429ResetValue(
        string propertyName,
        JsonElement value,
        DateTimeOffset observedAtUtc)
    {
        var raw = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (propertyName is "resets_in_seconds" or "reset_after_seconds" or
            "retry_after_seconds")
        {
            return double.TryParse(
                       raw,
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var seconds) &&
                   double.IsFinite(seconds) && seconds > 0D
                ? observedAtUtc.AddSeconds(
                    Math.Min(seconds, TimeSpan.FromDays(31).TotalSeconds))
                : null;
        }

        if (long.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static TimeSpan SelectTransient429RetryDelay(
        HttpResponseMessage response,
        int retryCount)
    {
        var delay = TimeSpan.FromMilliseconds(
            Transient429DefaultRetryDelay.TotalMilliseconds * Math.Pow(2D, retryCount));
        var now = DateTimeOffset.UtcNow;
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            delay = delta;
        }
        else if (response.Headers.RetryAfter?.Date is { } retryAt && retryAt > now)
        {
            delay = retryAt - now;
        }
        return delay > Transient429MaximumRetryDelay
            ? Transient429MaximumRetryDelay
            : delay < TimeSpan.Zero
                ? TimeSpan.Zero
                : delay;
    }

    private static bool IsQuotaLimitObservationActive(
        QuotaLimitObservation observation,
        DateTimeOffset nowUtc) =>
        observation.ResetAtUtc is { } resetAtUtc
            ? resetAtUtc + AccountRotationConfiguration.PrimaryResetGracePeriod > nowUtc
            : observation.ObservedAtUtc + QuotaResetUnknownFallbackCooldown > nowUtc;

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

    private void RecordSuccessfulModelRequest(
        string? accountKey,
        DateTimeOffset? startedAtUtc,
        string? proxyNodeId,
        bool usedGlobalProxy)
    {
        var normalized = NormalizeOptionalAccountKey(accountKey);
        if (normalized == null)
        {
            return;
        }

        var completedAtUtc = DateTimeOffset.UtcNow;
        lock (_activityGate)
        {
            _lastSuccessfulModelRequestAccountKey = normalized;
            _lastSuccessfulModelRequestProxyNodeId = string.IsNullOrWhiteSpace(proxyNodeId)
                ? null
                : proxyNodeId;
            _lastSuccessfulModelRequestUsedGlobalProxy = usedGlobalProxy;
            _lastSuccessfulModelRequestStartedAtUtc = startedAtUtc;
            _lastSuccessfulModelRequestCompletedAtUtc = completedAtUtc;
        }
        try
        {
            _ = _successfulActivityStore.Record(
                normalized,
                startedAtUtc,
                completedAtUtc);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or NotSupportedException or ArgumentException or
                JsonException)
        {
            // The authenticated in-memory health marker remains available. A cache write
            // failure must never turn a complete model response into a downstream error.
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

    private void RecordQuotaLimited(
        string? accountKey,
        string reason,
        DateTimeOffset? resetAtUtc)
    {
        var normalizedAccountKey = NormalizeOptionalAccountKey(accountKey);
        var observedAtUtc = DateTimeOffset.UtcNow;
        PatGatewayQuotaSignal? durableSignal = null;
        if (normalizedAccountKey != null)
        {
            _quotaLimitedAccounts[normalizedAccountKey] = new QuotaLimitObservation(
                observedAtUtc,
                resetAtUtc);
            try
            {
                // Commit before forwarding the 429 response. If the gateway or Manager
                // exits immediately afterwards, the next Manager process can still arm a
                // route for the next model-request boundary. A persistence failure never
                // replaces the real upstream 429 with a local gateway error.
                durableSignal = _quotaSignalStore.Record(
                    normalizedAccountKey,
                    observedAtUtc,
                    resetAtUtc);
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
            "pat-gateway-quota-exhaustion-confirmed",
            $"reason={reason}; reset_known={resetAtUtc.HasValue}; downstream_bytes=0");
    }


    private sealed record QuotaLimitObservation(
        DateTimeOffset ObservedAtUtc,
        DateTimeOffset? ResetAtUtc);

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
                _lastQuotaLimitedSequence,
                RotationProtocol: null,
                LastSuccessfulModelRequestAccountKey: _lastSuccessfulModelRequestAccountKey,
                LastSuccessfulModelRequestCompletedAtUtc: _lastSuccessfulModelRequestCompletedAtUtc,
                LastSuccessfulModelRequestStartedAtUtc: _lastSuccessfulModelRequestStartedAtUtc,
                LastSuccessfulModelRequestProxyNodeId: _lastSuccessfulModelRequestProxyNodeId,
                LastSuccessfulModelRequestUsedGlobalProxy: _lastSuccessfulModelRequestUsedGlobalProxy);
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
        internal DateTimeOffset StartedAtUtc { get; }

        internal ModelRequestActivity(LocalPatGatewayHost owner, string? accountKey)
        {
            _owner = owner;
            _accountKey = accountKey;
            StartedAtUtc = DateTimeOffset.UtcNow;
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
        string proxyPoolKey,
        CancellationToken cancellationToken = default)
    {
        // Identity is an observation made through a particular egress. Never reuse a
        // whoami result obtained through another account's fixed proxy node.
        var key = HashToken(token) + "|" + proxyPoolKey;
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
        // Keep identity probing bounded so a dead fixed node can fall back to the
        // configured global proxy promptly instead of waiting the old 20-second limit.
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
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
        var accountKey = ResolveAccountKeyForPatToken(token);
        if (accountKey != null)
        {
            _ = _accountIdentityStore.Record(accountKey, accountId);
        }
        return identity;
    }

    private string? ResolveAccountKeyForPatToken(string token)
    {
        try
        {
            var matches = _accountStore.LoadAccounts()
                .Where(account => account.IsAccessToken && !account.IsCompatibleApi)
                .Where(account =>
                {
                    try
                    {
                        var configured = CodexCliService.ReadAccessTokenCredential(
                            Path.Combine(account.CodexHome, "auth.json"));
                        return TokenHashesEqual(token, configured);
                    }
                    catch (Exception ex) when (
                        ex is IOException or UnauthorizedAccessException or JsonException or
                        InvalidDataException or FormatException or ArgumentException or
                        NotSupportedException)
                    {
                        return false;
                    }
                })
                .Take(2)
                .ToList();
            return matches.Count == 1
                ? QuotaAccountIdentity.CreateKey(matches[0])
                : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or InvalidOperationException or NotSupportedException or
            ArgumentException)
        {
            ManagerLifecycleDiagnostics.WriteException(
                "pat-gateway-account-identity-binding-deferred",
                ex,
                "credentials_persisted=false");
            return null;
        }
    }

    private bool IsSessionAffinityAccountEligible(
        AppSettings settings,
        string accountKey)
    {
        var accounts = _accountStore.LoadAccounts();
        _ = AccountRotationConfiguration.Normalize(settings, accounts);
        var matches = accounts
            .Where(account => QuotaAccountIdentity.CreateKey(account).Equals(
                accountKey,
                StringComparison.Ordinal))
            .Take(2)
            .ToList();
        var eligible = matches.Count == 1 &&
                       AccountRotationConfiguration.GetPool(settings, matches[0]) !=
                       AccountRotationPool.None;
        if (!eligible)
        {
            // The Manager normally asks the v6 control endpoint to remove ordinary
            // aliases when an account leaves a pool. If that request races a transient
            // gateway failure, the Manager's disk fallback cannot mutate this process's
            // in-memory store. Clean the stale ordinary aliases lazily at the same
            // eligibility gate so a failed provisional move cannot restore the removed
            // account as its DurableFallback. Response-id bindings remain untouched by
            // InvalidateOrdinaryBindingsForAccount and therefore retain strict safety.
            try
            {
                _ = _sessionAffinityStore.InvalidateOrdinaryBindingsForAccount(accountKey);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or NotSupportedException or ArgumentException or
                JsonException)
            {
                // Affinity is an optimization. Eligibility remains fail-closed for this
                // account even when best-effort cache cleanup cannot be persisted.
            }
        }
        return eligible;
    }

    private static SessionAffinityRequest ExtractSessionAffinityRequest(
        System.Collections.Specialized.NameValueCollection headers,
        byte[]? requestBody,
        string? contentEncoding)
    {
        ArgumentNullException.ThrowIfNull(headers);

        static string? NormalizeValue(string? value)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ||
                   normalized.Length > SessionAffinityMetadataMaxCharacters ||
                   normalized.Any(char.IsControl)
                ? null
                : normalized;
        }

        static string? FirstHeaderValue(
            System.Collections.Specialized.NameValueCollection source,
            params string[] names)
        {
            foreach (var name in names)
            {
                var values = source.GetValues(name);
                if (values == null)
                {
                    continue;
                }
                foreach (var value in values)
                {
                    if (NormalizeValue(value) is { } normalized)
                    {
                        return normalized;
                    }
                }
            }
            return null;
        }

        static string? MetadataSessionValue(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                raw.Length > SessionAffinityMetadataMaxCharacters)
            {
                return null;
            }
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                foreach (var name in new[]
                         { "session_id", "session", "conversation_id", "conversation" })
                {
                    if (document.RootElement.TryGetProperty(name, out var value) &&
                        value.ValueKind == JsonValueKind.String &&
                        NormalizeValue(value.GetString()) is { } normalized)
                    {
                        return normalized;
                    }
                }
            }
            catch (JsonException)
            {
                // Optional metadata is advisory. A malformed metadata header does not
                // change the request body or create a routing alias.
            }
            return null;
        }

        var bodyPresent = requestBody is { Length: > 0 };
        var normalizedEncoding = contentEncoding?.Trim() ?? "";
        var bodyInspectable = bodyPresent &&
                               (normalizedEncoding.Length == 0 ||
                                normalizedEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase));
        var allowCrossAccountReplay = !bodyPresent;
        var unsafeContinuation = false;
        var opaqueBody = false;
        string? previousResponseId = null;
        string? bodyPromptCacheKey = null;
        string? bodyContentSeed = null;

        if (bodyPresent && bodyInspectable)
        {
            // Scan only top-level routing fields. Utf8JsonReader validates the whole
            // document without materializing a second DOM, so large image/tool payloads
            // remain inspectable within the existing 128 MiB replay buffer.
            allowCrossAccountReplay = true;
            try
            {
                var signals = InspectSessionAffinityJson(requestBody!);
                previousResponseId = signals.PreviousResponseId;
                bodyPromptCacheKey = signals.PromptCacheKey;
                unsafeContinuation = signals.UnsafeContinuation;
                allowCrossAccountReplay = !unsafeContinuation;

                // Match sub2api's ordinary OpenAI fallback. Keep the more expensive
                // content projection bounded; large bodies still get strict
                // previous_response_id inspection and explicit session/cache routing.
                if (requestBody!.Length <= SessionAffinityInspectableBodyMaxBytes)
                {
                    bodyContentSeed = OpenAIContentSessionSeed.Derive(requestBody);
                }
            }
            catch (JsonException)
            {
                allowCrossAccountReplay = false;
                unsafeContinuation = true;
            }
        }
        else if (bodyPresent)
        {
            // Content-Encoding prevents inspection of a continuation field. The request
            // boundary rejects this form when affinity is enabled; with affinity disabled
            // the original compressed bytes are still forwarded unchanged.
            allowCrossAccountReplay = false;
            opaqueBody = true;
        }

        // Exact sub2api-compatible explicit-header priority. Only the first non-empty
        // signal is used; thread/turn metadata is never bulk-claimed as parallel aliases.
        static string? CompactSeed(string? raw)
        {
            var normalized = raw?.Trim();
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Any(char.IsControl))
            {
                return null;
            }
            if (normalized.Length <= SessionAffinityIdentifierMaxCharacters)
            {
                return normalized;
            }
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
                .ToLowerInvariant();
            return "seed_sha256_" + digest;
        }

        var sessionSeed = FirstHeaderValue(
            headers,
            "session-id",
            "session_id",
            "conversation_id",
            "X-Session-Affinity",
            "X-Session-Id",
            "X-OpenCode-Session",
            "X-Conversation-ID",
            // Legacy CAM/Codex spellings remain a final compatibility fallback.
            "x-codex-session-id");
        sessionSeed = CompactSeed(sessionSeed);
        sessionSeed ??= CompactSeed(bodyPromptCacheKey);
        if (sessionSeed == null)
        {
            sessionSeed = MetadataSessionValue(headers["x-codex-turn-metadata"]);
        }
        sessionSeed ??= CompactSeed(bodyContentSeed);

        var keys = new List<PatGatewaySessionAffinityKey>(2);
        if (previousResponseId != null)
        {
            // previous_response_id is an independent response-account layer. It is not
            // folded into the ordinary session hash, matching sub2api's scheduler model.
            keys.Add(PatGatewaySessionAffinityKey.Response(previousResponseId));
        }
        if (sessionSeed != null)
        {
            keys.Add(PatGatewaySessionAffinityKey.Session(sessionSeed));
        }
        return new SessionAffinityRequest(
            keys,
            previousResponseId,
            allowCrossAccountReplay,
            unsafeContinuation,
            opaqueBody);
    }

    private static SessionAffinityBodySignals InspectSessionAffinityJson(
        ReadOnlySpan<byte> requestBody)
    {
        var reader = new Utf8JsonReader(
            requestBody,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
        if (!reader.Read())
        {
            throw new JsonException("The model request body is empty.");
        }
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            if (reader.TokenType is JsonTokenType.StartArray)
            {
                reader.Skip();
            }
            if (reader.Read())
            {
                throw new JsonException("The model request body contains trailing JSON.");
            }
            return new SessionAffinityBodySignals(null, null, UnsafeContinuation: false);
        }

        var sawPreviousResponse = false;
        var sawPromptCacheKey = false;
        var unsafeContinuation = false;
        string? previousResponseId = null;
        string? promptCacheKey = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (reader.Read())
                {
                    throw new JsonException("The model request body contains trailing JSON.");
                }
                return new SessionAffinityBodySignals(
                    previousResponseId,
                    promptCacheKey,
                    unsafeContinuation);
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("The model request body is not a JSON object.");
            }

            var isPreviousResponse = reader.ValueTextEquals("previous_response_id"u8);
            var isPromptCacheKey = reader.ValueTextEquals("prompt_cache_key"u8);
            if (!reader.Read())
            {
                throw new JsonException("The model request body is truncated.");
            }

            if (isPreviousResponse)
            {
                if (sawPreviousResponse)
                {
                    // Duplicate continuation fields are ambiguous even when their
                    // textual values happen to match.
                    unsafeContinuation = true;
                    previousResponseId = null;
                }
                else
                {
                    sawPreviousResponse = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        previousResponseId = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        var candidate = reader.GetString()?.Trim() ?? "";
                        if (candidate.Length == 0)
                        {
                            previousResponseId = null;
                        }
                        else if (OpenAIResponseIdObserver.IsValidResponseId(candidate))
                        {
                            previousResponseId = candidate;
                        }
                        else
                        {
                            unsafeContinuation = true;
                            previousResponseId = null;
                        }
                    }
                    else
                    {
                        unsafeContinuation = true;
                        previousResponseId = null;
                    }
                }
            }
            else if (isPromptCacheKey &&
                     !sawPromptCacheKey &&
                     reader.TokenType == JsonTokenType.String)
            {
                sawPromptCacheKey = true;
                promptCacheKey = reader.GetString()?.Trim();
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        throw new JsonException("The model request body is truncated.");
    }

    // Retained temporarily as a migration reference for older portable configurations;
    // all callers use the single-seed implementation above.
    private static SessionAffinityRequest ExtractSessionAffinityRequestLegacy(
        System.Collections.Specialized.NameValueCollection headers,
        byte[]? requestBody,
        string? contentEncoding)
    {
        var responses = new List<string>();
        var sessions = new List<string>();
        var threads = new List<string>();
        var conversations = new List<string>();
        var promptCaches = new List<string>();
        var allowCrossAccountReplay = requestBody is null or { Length: 0 };

        static void AddValue(List<string> target, string? value)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.Length > SessionAffinityIdentifierMaxCharacters ||
                target.Count >= SessionAffinityValuesPerKind ||
                target.Contains(normalized, StringComparer.Ordinal))
            {
                return;
            }
            target.Add(normalized);
        }

        static void AddHeaderValues(
            System.Collections.Specialized.NameValueCollection source,
            List<string> target,
            params string[] names)
        {
            foreach (var name in names)
            {
                var values = source.GetValues(name);
                if (values == null)
                {
                    continue;
                }
                foreach (var value in values)
                {
                    AddValue(target, value);
                }
            }
        }

        static void ReadMetadataObject(
            JsonElement metadata,
            List<string> sessionValues,
            List<string> threadValues,
            List<string> conversationValues,
            List<string> promptCacheValues)
        {
            if (metadata.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var value = property.Value.GetString();
                switch (property.Name.ToLowerInvariant())
                {
                    case "session":
                    case "session_id":
                    case "session-id":
                        AddValue(sessionValues, value);
                        break;
                    case "thread":
                    case "thread_id":
                    case "thread-id":
                        AddValue(threadValues, value);
                        break;
                    case "conversation":
                    case "conversation_id":
                    case "conversation-id":
                        AddValue(conversationValues, value);
                        break;
                    case "prompt_cache_key":
                        AddValue(promptCacheValues, value);
                        break;
                }
            }
        }

        AddHeaderValues(
            headers,
            sessions,
            "session-id",
            "session_id",
            "x-codex-session-id");
        AddHeaderValues(
            headers,
            threads,
            "thread-id",
            "thread_id",
            "x-codex-thread-id",
            "x-codex-parent-thread-id");
        AddHeaderValues(
            headers,
            conversations,
            "conversation-id",
            "conversation_id");
        AddHeaderValues(
            headers,
            promptCaches,
            "prompt-cache-key",
            "prompt_cache_key");

        var turnMetadataValues = headers.GetValues("x-codex-turn-metadata");
        if (turnMetadataValues != null)
        {
            foreach (var rawMetadata in turnMetadataValues)
            {
                if (string.IsNullOrWhiteSpace(rawMetadata) ||
                    rawMetadata.Length > SessionAffinityMetadataMaxCharacters)
                {
                    continue;
                }
                try
                {
                    using var metadataDocument = JsonDocument.Parse(rawMetadata);
                    ReadMetadataObject(
                        metadataDocument.RootElement,
                        sessions,
                        threads,
                        conversations,
                        promptCaches);
                }
                catch (JsonException)
                {
                    // Malformed optional metadata is not an affinity key. The original
                    // request remains eligible for normal gateway forwarding.
                }
            }
        }

        var bodyCanBeInspected = requestBody is { Length: > 0 } &&
                                 requestBody.Length <= SessionAffinityInspectableBodyMaxBytes &&
                                 (string.IsNullOrWhiteSpace(contentEncoding) ||
                                  contentEncoding.Trim().Equals(
                                      "identity",
                                      StringComparison.OrdinalIgnoreCase));
        if (bodyCanBeInspected)
        {
            try
            {
                using var bodyDocument = JsonDocument.Parse(
                    requestBody,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 128
                    });
                if (bodyDocument.RootElement.ValueKind == JsonValueKind.Object)
                {
                    allowCrossAccountReplay = true;
                    foreach (var property in bodyDocument.RootElement.EnumerateObject())
                    {
                        var name = property.Name.ToLowerInvariant();
                        if (name == "previous_response_id" &&
                            (property.Value.ValueKind != JsonValueKind.String ||
                             !OpenAIResponseIdObserver.IsValidResponseId(
                                 property.Value.GetString())))
                        {
                            allowCrossAccountReplay = false;
                        }
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var value = property.Value.GetString();
                            switch (name)
                            {
                                case "previous_response_id":
                                    if (OpenAIResponseIdObserver.IsValidResponseId(value))
                                    {
                                        AddValue(responses, value);
                                    }
                                    break;
                                case "session":
                                case "session_id":
                                    AddValue(sessions, value);
                                    break;
                                case "thread":
                                case "thread_id":
                                    AddValue(threads, value);
                                    break;
                                case "conversation":
                                case "conversation_id":
                                    AddValue(conversations, value);
                                    break;
                                case "prompt_cache_key":
                                    AddValue(promptCaches, value);
                                    break;
                                case "client_metadata":
                                case "metadata":
                                    if (value is { Length: <= SessionAffinityMetadataMaxCharacters })
                                    {
                                        try
                                        {
                                            using var embeddedDocument = JsonDocument.Parse(value);
                                            ReadMetadataObject(
                                                embeddedDocument.RootElement,
                                                sessions,
                                                threads,
                                                conversations,
                                                promptCaches);
                                        }
                                        catch (JsonException)
                                        {
                                        }
                                    }
                                    break;
                            }
                        }
                        else if (name is "client_metadata" or "metadata")
                        {
                            ReadMetadataObject(
                                property.Value,
                                sessions,
                                threads,
                                conversations,
                                promptCaches);
                        }
                    }
                }

            }
            catch (JsonException)
            {
                // Body parsing is advisory. The upstream still owns validation of the
                // original request, while header aliases can continue to provide affinity.
            }
        }

        var keys = new List<PatGatewaySessionAffinityKey>(
            responses.Count + sessions.Count + threads.Count +
            conversations.Count + promptCaches.Count);
        // A duplicated/conflicting previous_response_id is ambiguous. Do not let either
        // value acquire strict priority; lower-priority session aliases remain usable.
        string? previousResponseId = null;
        if (responses.Count == 1)
        {
            previousResponseId = responses[0];
            keys.Add(PatGatewaySessionAffinityKey.Response(previousResponseId));
        }
        else if (responses.Count > 1)
        {
            allowCrossAccountReplay = false;
        }
        keys.AddRange(sessions.Select(PatGatewaySessionAffinityKey.Session));
        keys.AddRange(threads.Select(PatGatewaySessionAffinityKey.Thread));
        keys.AddRange(conversations.Select(PatGatewaySessionAffinityKey.Conversation));
        keys.AddRange(promptCaches.Select(PatGatewaySessionAffinityKey.PromptCache));
        return new SessionAffinityRequest(
            keys,
            previousResponseId,
            allowCrossAccountReplay,
            UnsafeContinuation: !allowCrossAccountReplay,
            OpaqueBody: false);
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
        var encoded = buffer.ToArray();
        var contentEncoding = incoming.Headers["Content-Encoding"]?.Trim();
        if (string.IsNullOrWhiteSpace(contentEncoding) ||
            contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            return new ReplayableModelRequestBody(encoded);
        }

        var decoded = DecodeRequestBody(encoded, contentEncoding);
        ManagerLifecycleDiagnostics.Write(
            "pat-gateway-request-body-decoded",
            $"content_encoding={contentEncoding}; encoded_bytes={encoded.Length}; " +
            $"decoded_bytes={decoded.Length}");
        return new ReplayableModelRequestBody(
            decoded,
            WasContentDecoded: true,
            OriginalContentEncoding: contentEncoding);
    }

    private static byte[] DecodeRequestBody(byte[] encoded, string contentEncoding)
    {
        var codings = contentEncoding
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split(';', 2)[0].Trim().ToLowerInvariant())
            .Where(value => value.Length > 0 && !value.Equals("identity", StringComparison.Ordinal))
            .ToArray();
        if (codings.Length == 0)
        {
            return encoded;
        }

        var current = encoded;
        // Content codings are applied from left to right and therefore decoded in
        // reverse order (RFC 9110, section 8.4).
        for (var index = codings.Length - 1; index >= 0; index--)
        {
            current = DecodeSingleContentCoding(current, codings[index]);
        }
        return current;
    }

    private static byte[] DecodeSingleContentCoding(byte[] encoded, string coding)
    {
        if (coding == "zstd")
        {
            return DecodeZstdContentCoding(encoded);
        }
        if (coding is not ("gzip" or "x-gzip" or "deflate" or "br"))
        {
            throw new UnsupportedContentEncodingException(coding);
        }

        using var input = new MemoryStream(encoded, writable: false);
        using Stream decoder = coding switch
        {
            "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw new UnsupportedContentEncodingException(coding)
        };
        using var output = new MemoryStream(Math.Min(encoded.Length * 2, 1024 * 1024));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = decoder.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            if (output.Length > ReplayableModelRequestBodyMaxBytes - read)
            {
                throw new InvalidDataException("解压后的请求体超过 128 MiB 内存缓冲上限。");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] DecodeZstdContentCoding(byte[] encoded)
    {
        try
        {
            using var decompressor = new ZstdSharp.Decompressor();
            // The frame may omit its content size.  Unwrap still accepts a bounded
            // destination in that case; the managed port throws when the output would
            // exceed the supplied limit, so a malicious compressed body cannot bypass
            // the same 128 MiB replay-buffer cap used by gzip/deflate/br.
            var decoded = decompressor.Unwrap(
                encoded,
                ReplayableModelRequestBodyMaxBytes);
            return decoded.ToArray();
        }
        catch (ZstdSharp.ZstdException ex)
        {
            throw new InvalidDataException(
                "zstd 请求体无法解码：" + ex.Message,
                ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                "zstd 请求体超过 128 MiB 解压上限。",
                ex);
        }
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
        return body.WasContentDecoded ||
               string.IsNullOrWhiteSpace(contentEncoding) ||
               contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] RewriteCompatibleApiRequestBody(
        ReadOnlySpan<byte> body,
        string model,
        IReadOnlySet<string>? allowedModels = null)
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
                string? selectedModel = null;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("model"))
                    {
                        modelCount++;
                        if (modelCount > 1)
                        {
                            throw new InvalidDataException("请求体包含重复的 model 字段。");
                        }
                        if (allowedModels == null)
                        {
                            selectedModel = model.Trim();
                        }
                        else if (property.Value.ValueKind != JsonValueKind.String ||
                                 string.IsNullOrWhiteSpace(property.Value.GetString()))
                        {
                            throw new InvalidDataException("请求体中的 model 必须是非空字符串。");
                        }
                        else
                        {
                            selectedModel = property.Value.GetString()!.Trim();
                            if (CodexCliService.GetCompatibleApiModelIdValidationError(selectedModel) != null ||
                                !allowedModels.Contains(selectedModel))
                            {
                                var available = allowedModels
                                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                                    .Take(8)
                                    .ToArray();
                                throw new InvalidDataException(
                                    $"模型“{selectedModel}”不在当前兼容 API 账号的已同步目录中。" +
                                    (available.Length == 0
                                        ? string.Empty
                                        : $"可选模型：{string.Join("、", available)}。"));
                            }
                        }
                        writer.WriteString("model", selectedModel);
                        continue;
                    }
                    property.WriteTo(writer);
                }
                if (modelCount == 0)
                {
                    selectedModel = model.Trim();
                    if (allowedModels != null && !allowedModels.Contains(selectedModel))
                    {
                        throw new InvalidDataException(
                            $"默认模型“{selectedModel}”不在当前兼容 API 账号的已同步目录中。");
                    }
                    writer.WriteString("model", selectedModel);
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
        CodexFingerprintPlan? fingerprintPlan = null)
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
                 !ShouldForwardRequestHeader(
                     headerName,
                     fingerprintPlan?.ShouldForwardClientMetadata == true))
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

        _ = CodexFingerprintConvergence.ApplyHeaders(request, fingerprintPlan);

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
        if (!IsCodexUserAgentAtLeast(userAgent, RequiredCodexVersion))
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

        return ClientCompatibilityHeaderAllowList.Contains(headerName) ||
               ProtocolRequestHeaderAllowList.Contains(headerName) ||
               forwardClientMetadata && ClientMetadataHeaderAllowList.Contains(headerName);
    }

    private static bool IsCodexUserAgentAtLeast(string? value, string minimum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        const string marker = "codex_cli_rs/";
        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var versionStart = markerIndex + marker.Length;
        var versionEnd = value.IndexOfAny([' ', '\t', '(', ';'], versionStart);
        var version = versionEnd < 0
            ? value[versionStart..]
            : value[versionStart..versionEnd];
        return IsVersionAtLeast(version, minimum);
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

    private static async Task<OpenAIResponseIdObservation?> CopyUpstreamResponseAsync(
        HttpResponseMessage upstream,
        HttpListenerResponse downstream,
        bool observeResponseId = false,
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

        OpenAIResponseIdObserver? observer = observeResponseId
            ? new OpenAIResponseIdObserver(contentType)
            : null;
        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(
            cancellationToken);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await upstreamStream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (observer != null)
            {
                try
                {
                    observer.Observe(buffer.AsSpan(0, read));
                }
                catch
                {
                    // Observation is advisory. It must never interrupt byte-for-byte
                    // response forwarding, even if a future parser implementation fails.
                    observer = null;
                }
            }
            await downstream.OutputStream.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
        await downstream.OutputStream.FlushAsync(cancellationToken);

        if (observer == null)
        {
            return null;
        }
        try
        {
            return observer.Complete();
        }
        catch
        {
            return null;
        }
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

    private HttpClient CreateUpstreamClient(ProxyResolution resolution, ProxyNodeRecord? node)
    {
        if (resolution.ProxyUri == null)
        {
            return new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            }) { Timeout = Timeout.InfiniteTimeSpan };
        }
        return ProxyHttpClientFactory.Create(resolution, node);
    }

    private ProxyResolution ResolveProxyForCredential(GatewayCredential? credential)
    {
        var accountKey = credential?.AccountKey;
        return RejectGatewayLoop(_proxyResolver.Resolve(accountKey));
    }

    private ProxyResolution ResolveGlobalProxy()
    {
        return RejectGatewayLoop(_proxyResolver.ResolveGlobal());
    }

    private static ProxyResolution RejectGatewayLoop(ProxyResolution resolution)
    {
        if (resolution.Success && resolution.ProxyUri is { } uri &&
            LocalProxyDetector.IsLoopbackHost(uri.Host) && uri.Port == LocalPatGateway.Port)
        {
            return ProxyResolution.Fail("代理配置指向本地 PAT 网关自身；为防止循环转发，请修改代理节点。");
        }
        return resolution;
    }

    private bool TryActivateRuntimeGlobalProxyFallback(
        GatewayCredential credential,
        ProxyResolution failedResolution,
        HashSet<string> fallbackAccounts,
        string reason)
    {
        var accountKey = NormalizeOptionalAccountKey(credential.AccountKey);
        if (accountKey == null || failedResolution.NodeId == null ||
            fallbackAccounts.Contains(accountKey) ||
            !_proxyResolver.AllowsRuntimeGlobalFallback(accountKey, failedResolution.NodeId))
        {
            return false;
        }

        var globalResolution = ResolveGlobalProxy();
        if (!globalResolution.Success)
        {
            return false;
        }

        fallbackAccounts.Add(accountKey);
        _proxyResolver.MarkRuntimeFailure(
            failedResolution.NodeId,
            "运行时请求失败，已回退全局代理；旧出口 IP 仅作历史记录。");
        ManagerLifecycleDiagnostics.Write(
            "pat-gateway-account-proxy-fallback-activated",
            $"reason={reason}; proxy=fixed-to-global; downstream_bytes=0");
        return true;
    }

    private GatewayCredential? ReadBearerCredential(HttpListenerRequest request)
    {
        var authorization = request.Headers["Authorization"];
        if (!TryReadRawBearerToken(authorization, out var token))
        {
            return null;
        }

        // A compatible endpoint may issue keys whose spelling resembles a PAT or JWT.
        // Prefer an exact, locally configured API-key owner before generic syntax
        // classification so transparent rotation and prepared routes retain its account key.
        return ResolveConfiguredCompatibleApiBearer(token) ??
               ParseBearerCredential(authorization);
    }

    private static GatewayCredential? ParseBearerCredential(string? authorization)
    {
        if (!TryReadRawBearerToken(authorization, out var token))
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

    private static bool TryReadRawBearerToken(
        string? authorization,
        out string token)
    {
        authorization = authorization?.Trim();
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = authorization["Bearer ".Length..].Trim();
        return token.Length is >= 8 and <= 8192 &&
               !token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
    }

    private GatewayCredential? ResolveConfiguredCompatibleApiBearer(string token)
    {
        try
        {
            var matches = new List<(AccountRecord Account, string AccountKey)>();
            foreach (var account in _accountStore.LoadAccounts().Where(account => account.IsCompatibleApi))
            {
                try
                {
                    var configuredToken = CodexCliService.ReadAccessTokenCredential(
                        Path.Combine(account.CodexHome, "auth.json"));
                    if (TokenHashesEqual(token, configuredToken))
                    {
                        matches.Add((account, QuotaAccountIdentity.CreateKey(account)));
                    }
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException or FormatException or ArgumentException or
                    NotSupportedException)
                {
                    // A broken unrelated API record cannot hide the exact active key.
                }
            }
            if (matches.Count == 0)
            {
                return null;
            }

            var route = _rotationStore.Load();
            var selected = matches.FirstOrDefault(candidate =>
                PatGatewayRotationStore.TryNormalizeAccountKey(
                    route.TransportAccountKey,
                    out var routeTransport) &&
                candidate.AccountKey.Equals(routeTransport, StringComparison.Ordinal));
            if (selected.Account == null)
            {
                var currentName = _themeService.LoadSettings().CurrentAccountName?.Trim();
                var currentMatches = matches
                    .Where(candidate => candidate.Account.Name.Equals(
                        currentName,
                        StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToList();
                selected = currentMatches.Count == 1
                    ? currentMatches[0]
                    : matches.Count == 1
                        ? matches[0]
                        : default;
            }

            return selected.Account == null
                ? null
                : ResolveRotationCredential(selected.AccountKey);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or FormatException or ArgumentException or
            NotSupportedException or InvalidOperationException)
        {
            // Unknown or ambiguous API keys are rejected by the caller. They are never
            // forwarded to chatgpt.com as if they were an OAuth token.
            return null;
        }
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
        out Uri upstream,
        int listenerPort = LocalPatGateway.Port)
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
            !incoming.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            !LocalProxyDetector.IsLoopbackHost(incoming.Host) ||
            incoming.Port != listenerPort ||
            !string.IsNullOrEmpty(incoming.UserInfo) ||
            !string.IsNullOrEmpty(incoming.Fragment) ||
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
        if (!suffix.Equals("/responses", StringComparison.OrdinalIgnoreCase) &&
            !suffix.Equals("/responses/compact", StringComparison.OrdinalIgnoreCase))
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
    private sealed record ReplayableModelRequestBody(
        byte[] Bytes,
        bool WasContentDecoded = false,
        string? OriginalContentEncoding = null);
    private sealed record Upstream429Classification(
        bool IsQuotaExhausted,
        string Reason,
        DateTimeOffset? ResetAtUtc,
        bool RequiresSameAccountConfirmation = false);
    private sealed record SessionAffinityBodySignals(
        string? PreviousResponseId,
        string? PromptCacheKey,
        bool UnsafeContinuation);
    private sealed record SessionAffinityRequest(
        IReadOnlyList<PatGatewaySessionAffinityKey> Keys,
        string? PreviousResponseId,
        bool AllowCrossAccountReplay,
        bool UnsafeContinuation,
        bool OpaqueBody)
    {
        internal bool RequiresOriginalAccount =>
            PreviousResponseId != null ||
            UnsafeContinuation ||
            OpaqueBody && Keys.Count > 0;
    }
    private sealed record GatewayCredential(
        string Token,
        bool IsPersonalAccessToken,
        Uri? CompatibleApiBaseUri = null,
        string? CompatibleApiModel = null,
        string? AccountKey = null,
        string? ChatGptAccountId = null,
        bool AllowIncomingChatGptIdentity = true,
        IReadOnlySet<string>? CompatibleApiModels = null)
    {
        internal bool IsCompatibleApi =>
            CompatibleApiBaseUri != null && !string.IsNullOrWhiteSpace(CompatibleApiModel);
    }
    private sealed record RotationArmRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("sourceAccountKey")]
        string? SourceAccountKey,
        [property: System.Text.Json.Serialization.JsonPropertyName("targetAccountKey")]
        string? TargetAccountKey,
        [property: System.Text.Json.Serialization.JsonPropertyName("transportAccountKey")]
        string? TransportAccountKey,
        [property: System.Text.Json.Serialization.JsonPropertyName("replaceExistingArmedTarget")]
        bool ReplaceExistingArmedTarget = false);
    private sealed record SessionAffinityInvalidateRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("accountKey")]
        string? AccountKey);
    private sealed record PatRejectionDetails(HttpStatusCode StatusCode, string Message);

    private sealed class PatRejectedException(
        HttpStatusCode statusCode,
        bool isInactiveWorkspaceMember) : Exception
    {
        internal HttpStatusCode StatusCode { get; } = statusCode;
        internal bool IsInactiveWorkspaceMember { get; } = isInactiveWorkspaceMember;
    }

    private sealed class UnsupportedContentEncodingException(string coding)
        : Exception("unsupported " + coding)
    {
    }
}
