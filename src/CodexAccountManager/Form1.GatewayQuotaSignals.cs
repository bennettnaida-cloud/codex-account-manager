namespace CodexAccountManager;

public partial class Form1
{
    private static readonly TimeSpan GatewayQuotaSignalPollInterval =
        TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GatewayQuotaSignalRetryInterval =
        TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GatewayRecoveryRetryInterval =
        TimeSpan.FromSeconds(5);

    private readonly PatGatewayQuotaSignalAttemptTracker _patGatewayQuotaSignalAttempts = new();
    private PatGatewayQuotaSignalStore? _patGatewayQuotaSignalStore;
    private DateTimeOffset? _patGatewayQuotaSignalCheckedAtUtc;
    private DateTimeOffset? _patGatewayActivitySignalCheckedAtUtc;
    private DateTimeOffset? _patGatewayRecoveryAttemptedAtUtc;
    private bool _patGatewayActivitySignalPollRunning;

    /// <summary>
    /// Carries the last in-memory 429 across a v3-to-v4 gateway hand-off.  This is
    /// especially important during an in-place Manager update: the old gateway can
    /// already know that the current account is exhausted even though it predates the
    /// durable signal file.
    /// </summary>
    private void PersistPatGatewayQuotaSignalSnapshotBestEffort(
        LocalPatGatewayActivitySnapshot? activity)
    {
        if (activity?.LastQuotaLimitedAtUtc is not { } observedAtUtc ||
            !PatGatewayRotationStore.TryNormalizeAccountKey(
                activity.LastQuotaLimitedAccountKey,
                out var accountKey))
        {
            return;
        }

        try
        {
            _patGatewayQuotaSignalStore ??=
                new PatGatewayQuotaSignalStore(_store.RootPath);
            _ = _patGatewayQuotaSignalStore.Record(accountKey, observedAtUtc);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or NotSupportedException or ArgumentException or
            OverflowException or System.Text.Json.JsonException)
        {
            // The old gateway keeps serving its current request.  A migration failure
            // must not turn a safe protocol hand-off into a Codex interruption.
        }
    }

    /// <summary>
    /// Polls the durable gateway signal independently of the official quota endpoint.
    /// This method runs on the existing UI timer and deliberately performs only one small
    /// local cache-file read; network preflight and route activation remain asynchronous.
    /// </summary>
    private void RefreshPersistedPatGatewayQuotaSignalIfNeeded()
    {
        QueuePatGatewayActivitySignalRefreshIfNeeded();

        if (_formClosed || IsDisposed ||
            !AccountRotationConfiguration.IsEnabled(_appSettings) ||
            !_patAutoRotationGatewayTransportActive ||
            _patAutoRotationLaunchContext?.ClientMode != WindowsClientMode.OfficialCodex ||
            _patAutoRotationBoundaryCancellation != null ||
            GetCurrentAccountRecord() is not { } current ||
            current.IsCompatibleApi ||
            AccountRotationConfiguration.GetPool(_appSettings, current) ==
                AccountRotationPool.None)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_patGatewayQuotaSignalCheckedAtUtc is { } lastChecked &&
            now - lastChecked < GatewayQuotaSignalPollInterval)
        {
            return;
        }
        _patGatewayQuotaSignalCheckedAtUtc = now;

        if (_patAutoRotationCooldownUntilUtc is { } cooldownUntil && cooldownUntil > now)
        {
            return;
        }

        try
        {
            _patGatewayQuotaSignalStore ??=
                new PatGatewayQuotaSignalStore(_store.RootPath);
            var accountKey = QuotaAccountIdentity.CreateKey(current);
            var signal = _patGatewayQuotaSignalStore.ReadLatestForAccount(accountKey, now);
            if (signal == null)
            {
                return;
            }

            // A cached reset time prevents an event from the preceding five-hour window
            // from rotating an account after it has already refreshed. With no official
            // window available, the store's short lifetime is the conservative fallback.
            var fiveHourWindow = GetCachedFiveHourWindowForGatewaySignal(accountKey);
            var decision = PatAutoRotationPolicy.EvaluateQuotaSafety(
                fiveHourWindow,
                recentRequestUsedPercent: null,
                signal.ObservedAtUtc,
                now);
            _patAutoRotationLastDecision = decision;
            if (!decision.ShouldRotate ||
                !_patGatewayQuotaSignalAttempts.ShouldAttempt(
                    signal,
                    now,
                    GatewayQuotaSignalRetryInterval))
            {
                return;
            }

            // Record unavailability before candidate preparation. This survives a Manager
            // restart and prevents the exhausted source from immediately re-entering its
            // ring. A missing reset time is re-probed by the existing bounded retry path.
            RecordPatRotationAccountExhausted(
                current,
                fiveHourWindow?.ResetsAtUtc,
                now);
            _patAutoRotationObservedAccountKey = accountKey;
            _patAutoRotationObservedResetAtUtc = fiveHourWindow?.ResetsAtUtc;
            _patAutoRotationLastObservationAtUtc = now;
            _patAutoRotationConsecutiveObservations = 1;
            _patAutoRotationState = PatAutoRotationState.PendingQuotaExhaustion;

            var cancellation = new CancellationTokenSource();
            _patAutoRotationBoundaryCancellation = cancellation;
            _statusBox.Text =
                $"网关已确认 {current.Name} 的模型请求返回 HTTP 429；" +
                "正在独立准备下一个轮换账号。失败请求不会重放，下一次模型请求将在边界直接使用新账号。";
            UpdatePatAutoRotationControls();
            _ = PreparePatAutoRotationAsync(accountKey, now, cancellation);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or NotSupportedException or ArgumentException or
            System.Text.Json.JsonException)
        {
            // A locked/damaged optional cache must not terminate the WinForms timer. The
            // authenticated health signal and normal official refresh remain fallbacks,
            // and the next poll retries this read without touching Codex.
        }
    }

    /// <summary>
    /// Bridges quota events from an already-running v3 gateway while it waits for a
    /// genuinely quiet upgrade boundary.  v3 exposes an authenticated in-memory 429
    /// snapshot but cannot write the v4 durable signal file itself.  Polling this small
    /// loopback health document keeps rotation live without closing Codex or the gateway.
    /// </summary>
    private void QueuePatGatewayActivitySignalRefreshIfNeeded()
    {
        if (_formClosed || IsDisposed ||
            _patGatewayActivitySignalPollRunning ||
            !AccountRotationConfiguration.IsEnabled(_appSettings) ||
            !_appSettings.PatGatewayEnabled ||
            !_codex.IsOfficialWindowsClientRunning())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_patGatewayActivitySignalCheckedAtUtc is { } lastChecked &&
            now - lastChecked < GatewayQuotaSignalPollInterval)
        {
            return;
        }

        _patGatewayActivitySignalCheckedAtUtc = now;
        _patGatewayActivitySignalPollRunning = true;
        _ = RefreshPatGatewayActivitySignalAsync();
    }

    private async Task RefreshPatGatewayActivitySignalAsync()
    {
        try
        {
            var activity = await LocalPatGateway.ReadActivitySnapshotAsync();
            if (activity == null &&
                (_patGatewayRecoveryAttemptedAtUtc is not { } lastRecoveryAttempt ||
                 DateTimeOffset.UtcNow - lastRecoveryAttempt >= GatewayRecoveryRetryInterval))
            {
                // A gateway process can disappear independently of the Manager (for
                // example, an old build used to throw while releasing its named mutex).
                // Recreate only the loopback gateway; never close or relaunch Codex.
                _patGatewayRecoveryAttemptedAtUtc = DateTimeOffset.UtcNow;
                await LocalPatGateway.EnsureRunningAsync(restartOnProxyMismatch: false);
                activity = await LocalPatGateway.ReadActivitySnapshotAsync();
                if (activity != null)
                {
                    ManagerLifecycleDiagnostics.Write(
                        "pat-gateway-recovered-without-codex-restart",
                        "source=quota-signal-monitor");
                }
            }
            if (activity == null)
            {
                return;
            }

            PersistPatGatewayQuotaSignalSnapshotBestEffort(activity);
            if (!_patAutoRotationGatewayTransportActive ||
                _patAutoRotationLaunchContext == null)
            {
                await TryRecoverPatAutoRotationLaunchContextAsync(activity);
            }
        }
        catch (Exception ex) when (
            ex is IOException or HttpRequestException or InvalidDataException or
            UnauthorizedAccessException or InvalidOperationException or
            NotSupportedException or ArgumentException or
            System.Text.Json.JsonException)
        {
            // The durable file and official quota monitor remain independent fallbacks.
            // A transient loopback read must never escape the WinForms timer.
        }
        finally
        {
            _patGatewayActivitySignalPollRunning = false;
        }
    }

    private UsageRateLimitWindow? GetCachedFiveHourWindowForGatewaySignal(string accountKey)
    {
        if (!_liveRateLimitCache.TryGetValue(accountKey, out var snapshot))
        {
            return null;
        }

        if (AccountQuotaLimitType.ClassifyWindow(snapshot.WindowMinutes) ==
            AccountQuotaWindowKind.FiveHour)
        {
            return new UsageRateLimitWindow(
                NormalizeGatewaySignalUsedPercent(snapshot.UsedPercent),
                snapshot.WindowMinutes,
                snapshot.ResetsAtUtc);
        }
        if (AccountQuotaLimitType.ClassifyWindow(snapshot.SecondaryWindowMinutes) ==
            AccountQuotaWindowKind.FiveHour)
        {
            return new UsageRateLimitWindow(
                NormalizeGatewaySignalUsedPercent(snapshot.SecondaryUsedPercent),
                snapshot.SecondaryWindowMinutes,
                snapshot.SecondaryResetsAtUtc);
        }
        return null;
    }

    private static int? NormalizeGatewaySignalUsedPercent(double? value) =>
        value.HasValue && double.IsFinite(value.Value)
            ? (int)Math.Round(
                Math.Clamp(value.Value, 0D, 100D),
                MidpointRounding.AwayFromZero)
            : null;
}
