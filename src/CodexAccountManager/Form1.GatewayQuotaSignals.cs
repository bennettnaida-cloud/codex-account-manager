namespace CodexAccountManager;

public partial class Form1
{
    private static readonly TimeSpan GatewayQuotaSignalPollInterval =
        TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GatewayQuotaSignalRetryInterval =
        TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GatewayRecoveryRetryInterval =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GatewaySuccessfulActivityPollInterval =
        TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LegacyTransparentInferenceWindow =
        TimeSpan.FromMinutes(2);

    private readonly PatGatewayQuotaSignalAttemptTracker _patGatewayQuotaSignalAttempts = new();
    private readonly HashSet<long> _patGatewayReconciledQuotaSignalSequences = [];
    private PatGatewayQuotaSignalStore? _patGatewayQuotaSignalStore;
    private DateTimeOffset? _patGatewayQuotaSignalCheckedAtUtc;
    private DateTimeOffset? _patGatewayActivitySignalCheckedAtUtc;
    private DateTimeOffset? _patGatewaySuccessfulActivityCheckedAtUtc;
    private DateTimeOffset? _patGatewayRecoveryAttemptedAtUtc;
    private bool _patGatewayActivitySignalPollRunning;
    private string? _patGatewayObservedRotationProtocol;
    private bool _legacyPatGatewayRouteRetired;
    private bool _legacyPatGatewayRouteRetirementRunning;
    // The health document is polled more often than the UI is rebuilt.  Remember the
    // completion marker that has already been reflected in the Manager so a sticky
    // session cannot repeatedly rewrite the current-account setting or the usage ledger.
    private DateTimeOffset? _patGatewayLastObservedSuccessfulCompletionAtUtc;
    private long? _patGatewayLastObservedSuccessfulRequestCount;
    private string? _patGatewayLastObservedSuccessfulAccountKey;
    private bool _patGatewayActivityBaselineCaptured;
    private readonly DateTimeOffset _patGatewayActivityMonitorStartedAtUtc =
        DateTimeOffset.UtcNow;
    private PatGatewaySuccessfulActivityStore? _patGatewaySuccessfulActivityStore;

    /// <summary>
    /// Carries only a v8 in-memory confirmed 429 across an in-place gateway hand-off.
    /// Earlier protocols used a weaker classifier, so copying their last observation into
    /// the schema-3 file would reintroduce a retired false-positive after migration.
    /// </summary>
    private void PersistPatGatewayQuotaSignalSnapshotBestEffort(
        LocalPatGatewayActivitySnapshot? activity)
    {
        if (activity?.RotationProtocol != null)
        {
            _patGatewayObservedRotationProtocol = activity.RotationProtocol;
        }
        if (!LocalPatGateway.IsCurrentRotationProtocolValue(
                activity?.RotationProtocol))
        {
            // v3-v7 used weaker 429 evidence. Never upgrade that in-memory snapshot into
            // the v8/schema-3 confirmed-signal store.
            return;
        }
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
        RefreshPersistedPatGatewaySuccessfulActivityIfNeeded();
        QueuePatGatewayActivitySignalRefreshIfNeeded();

        if (_formClosed || IsDisposed ||
            !AccountRotationConfiguration.IsEnabled(_appSettings))
        {
            return;
        }
        ReconcileConfirmedGatewayQuotaResetSignals();

        if (
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

        var rotationProtocol = _patGatewayObservedRotationProtocol;
        var transparentRetryAvailable =
            LocalPatGateway.IsTransparentRotationProtocolValue(rotationProtocol);
        if (!transparentRetryAvailable &&
            !LocalPatGateway.IsManagerPreparedRotationProtocolValue(rotationProtocol))
        {
            // Capability is deliberately fail-closed. Until the authenticated health
            // snapshot proves v3/v4, never pre-arm an account from a cached 429. A v5/v6
            // gateway owns retry selection inside the real request and commits only its
            // successful winner.
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
            if (IsGatewayQuotaSignalSuperseded(
                    signal,
                    accountKey,
                    fiveHourWindow,
                    now))
            {
                _patGatewayReconciledQuotaSignalSequences.Add(signal.Sequence);
                RecordPatRotationAccountAvailable(current);
                return;
            }
            var confirmedByGateway =
                LocalPatGateway.IsConfirmedQuotaRotationProtocolValue(rotationProtocol);
            var decision = confirmedByGateway
                ? new QuotaRotationDecision(
                    true,
                    true,
                    fiveHourWindow?.UsedPercent,
                    fiveHourWindow?.UsedPercent is { } used
                        ? Math.Max(0D, 100D - used)
                        : null,
                    0D,
                    0D,
                    "gateway-confirmed-quota-exhaustion")
                : PatAutoRotationPolicy.EvaluateQuotaSafety(
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
                signal.ResetAtUtc ?? fiveHourWindow?.ResetsAtUtc,
                now);
            _patAutoRotationObservedAccountKey = accountKey;
            _patAutoRotationObservedResetAtUtc =
                signal.ResetAtUtc ?? fiveHourWindow?.ResetsAtUtc;
            _patAutoRotationLastObservationAtUtc = now;
            _patAutoRotationConsecutiveObservations = 1;
            _patAutoRotationState = PatAutoRotationState.PendingQuotaExhaustion;

            if (transparentRetryAvailable)
            {
                _statusBox.Text =
                    $"网关已从 {current.Name} 的 429 响应中确认额度耗尽；" +
                    "已记录重置窗口。下一次真实模型请求仍由透明网关按轮换顺序试号，" +
                    "只有完整成功的候选才会成为当前账号。";
                UpdatePatAutoRotationControls();
                return;
            }

            var cancellation = new CancellationTokenSource();
            _patAutoRotationBoundaryCancellation = cancellation;
            _statusBox.Text =
                $"网关已确认 {current.Name} 的额度耗尽；" +
                "正在准备下一个轮换账号；v5 网关会在响应输出前透明重试同一请求，旧网关则在下一次请求边界切换。";
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

    private void RefreshPersistedPatGatewaySuccessfulActivityIfNeeded()
    {
        if (_formClosed || IsDisposed ||
            !AccountRotationConfiguration.IsEnabled(_appSettings) ||
            !_appSettings.PatGatewayEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_patGatewaySuccessfulActivityCheckedAtUtc is { } lastChecked &&
            now - lastChecked < GatewaySuccessfulActivityPollInterval)
        {
            return;
        }
        _patGatewaySuccessfulActivityCheckedAtUtc = now;

        try
        {
            _patGatewaySuccessfulActivityStore ??=
                new PatGatewaySuccessfulActivityStore(_store.RootPath);
            var successful = _patGatewaySuccessfulActivityStore.ReadLatest();
            if (successful == null)
            {
                return;
            }

            var activity = CreatePersistedSuccessfulActivitySnapshot(
                successful,
                new PatGatewayRotationStore(_store.RootPath).Load());
            if (_patGatewayActivityBaselineCaptured)
            {
                ReconcilePatGatewaySuccessfulAccount(activity);
            }
            else if (ShouldReconcileInitialSuccessfulActivity(
                         activity,
                         _patGatewayActivityMonitorStartedAtUtc,
                         _usageTracker.GetLatestAccountSwitchAtUtc()))
            {
                _patGatewayActivityBaselineCaptured = true;
                ReconcilePatGatewaySuccessfulAccount(activity);
            }
            else
            {
                CapturePatGatewayActivityBaseline(activity);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or NotSupportedException or ArgumentException or
            System.Text.Json.JsonException)
        {
            ManagerLifecycleDiagnostics.Write(
                "pat-gateway-persisted-activity-monitor-error",
                $"type={ex.GetType().Name}");
        }
    }

    private static LocalPatGatewayActivitySnapshot CreatePersistedSuccessfulActivitySnapshot(
        PatGatewaySuccessfulActivity successful,
        PatGatewayRotationSnapshot rotation) =>
        new(
            ActiveModelRequests: 0,
            LastModelRequestStartedAtUtc: successful.StartedAtUtc,
            LastModelRequestCompletedAtUtc: successful.CompletedAtUtc,
            LastQuotaLimitedAtUtc: null,
            Rotation: rotation,
            CompletedModelRequests: successful.Sequence,
            LastModelRequestAccountKey: successful.AccountKey,
            LastQuotaLimitedAccountKey: null,
            LastQuotaLimitedSequence: null,
            RotationProtocol: LocalPatGateway.RotationProtocolValue,
            LastSuccessfulModelRequestAccountKey: successful.AccountKey,
            LastSuccessfulModelRequestCompletedAtUtc: successful.CompletedAtUtc,
            LastSuccessfulModelRequestStartedAtUtc: successful.StartedAtUtc);

    private void ReconcileConfirmedGatewayQuotaResetSignals()
    {
        try
        {
            _patGatewayQuotaSignalStore ??=
                new PatGatewayQuotaSignalStore(_store.RootPath);
            var now = DateTimeOffset.UtcNow;
            var changed = false;
            foreach (var signal in _patGatewayQuotaSignalStore.ReadLatestPerAccount(now))
            {
                if (_patGatewayReconciledQuotaSignalSequences.Contains(signal.Sequence) ||
                    signal.ResetAtUtc is not { } resetAtUtc ||
                    resetAtUtc + AccountRotationConfiguration.PrimaryResetGracePeriod <= now ||
                    FindRotationAccount(signal.AccountKey) is not { IsCompatibleApi: false } account)
                {
                    continue;
                }

                _patAutoRotationUnavailableAccountKeys.Add(signal.AccountKey);
                _patAutoRotationUnknownResetRetryAtUtc.Remove(signal.AccountKey);
                if (!_appSettings.AccountRotationResetAtUtc.TryGetValue(
                        signal.AccountKey,
                        out var existingReset) ||
                    (existingReset - resetAtUtc).Duration() > TimeSpan.FromSeconds(1))
                {
                    AccountRotationConfiguration.RecordExhaustedReset(
                        _appSettings,
                        account,
                        resetAtUtc);
                    changed = true;
                }
                _patGatewayReconciledQuotaSignalSequences.Add(signal.Sequence);
            }
            if (changed)
            {
                _themeService.SaveSettings(_appSettings);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or NotSupportedException or ArgumentException or
            System.Text.Json.JsonException)
        {
            // Confirmed reset metadata is an optimization. The active request and normal
            // official-quota refresh remain authoritative if this optional cache is locked.
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
        if (!ShouldPollPatGatewayActivity(
                _formClosed,
                IsDisposed,
                _patGatewayActivitySignalPollRunning,
                AccountRotationConfiguration.IsEnabled(_appSettings),
                _appSettings.PatGatewayEnabled))
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

    private static bool ShouldPollPatGatewayActivity(
        bool formClosed,
        bool formDisposed,
        bool pollRunning,
        bool rotationEnabled,
        bool gatewayEnabled) =>
        !formClosed &&
        !formDisposed &&
        !pollRunning &&
        rotationEnabled &&
        gatewayEnabled;

    private static bool ShouldRecoverMissingPatGateway(
        bool officialWindowsClientRunning,
        DateTimeOffset now,
        DateTimeOffset? lastRecoveryAttempt) =>
        officialWindowsClientRunning &&
        (lastRecoveryAttempt is not { } lastRecovery ||
         now - lastRecovery >= GatewayRecoveryRetryInterval);

    private async Task RefreshPatGatewayActivitySignalAsync()
    {
        try
        {
            var activity = await LocalPatGateway.ReadActivitySnapshotAsync();
            var now = DateTimeOffset.UtcNow;
            if (activity == null &&
                ShouldRecoverMissingPatGateway(
                    _codex.IsOfficialWindowsClientRunning(),
                    now,
                    _patGatewayRecoveryAttemptedAtUtc))
            {
                // A gateway process can disappear independently of the Manager (for
                // example, an old build used to throw while releasing its named mutex).
                // Recreate only the loopback gateway; never close or relaunch Codex.
                _patGatewayRecoveryAttemptedAtUtc = now;
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

            _patGatewayObservedRotationProtocol = activity.RotationProtocol;
            PersistPatGatewayQuotaSignalSnapshotBestEffort(activity);
            if (await RetireUnconfirmedLegacyGatewayRouteAsync(activity))
            {
                return;
            }
            ReconcileTransparentPatGatewayRotation(activity);
            if (_patGatewayActivityBaselineCaptured)
            {
                // A v10 success marker is committed only after the complete response has
                // been forwarded. Reconcile it even if another independent request is
                // already active; waiting for a globally quiet poll makes continuous use
                // permanently hide the actual account and leaves its quota card stale.
                ReconcilePatGatewaySuccessfulAccount(activity);
            }
            else if (ShouldReconcileInitialSuccessfulActivity(
                         activity,
                         _patGatewayActivityMonitorStartedAtUtc,
                         _usageTracker.GetLatestAccountSwitchAtUtc()))
            {
                // The first poll may run only after a long model response has completed.
                // That response is not historical when it started/finished after this
                // Manager instance was created, so reconcile it immediately instead of
                // freezing the pre-launch account label until a second request arrives.
                _patGatewayActivityBaselineCaptured = true;
                ReconcilePatGatewaySuccessfulAccount(activity);
            }
            else
            {
                CapturePatGatewayActivityBaseline(activity);
            }
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
            ManagerLifecycleDiagnostics.Write(
                "pat-gateway-activity-monitor-error",
                $"type={ex.GetType().Name}");
        }
        finally
        {
            _patGatewayActivitySignalPollRunning = false;
        }
    }

    private void CapturePatGatewayActivityBaseline(
        LocalPatGatewayActivitySnapshot activity)
    {
        _patGatewayActivityBaselineCaptured = true;
        var (accountKey, completedAtUtc, _) = SelectSuccessfulActivityMarker(activity);
        _patGatewayLastObservedSuccessfulAccountKey = accountKey;
        _patGatewayLastObservedSuccessfulCompletionAtUtc = completedAtUtc;
        _patGatewayLastObservedSuccessfulRequestCount = activity.CompletedModelRequests;
    }

    private static bool ShouldReconcileInitialSuccessfulActivity(
        LocalPatGatewayActivitySnapshot activity,
        DateTimeOffset monitorStartedAtUtc,
        DateTimeOffset? latestAccountSwitchAtUtc) =>
        SelectSuccessfulActivityMarker(activity).CompletedAtUtc is { } completedAtUtc &&
        (completedAtUtc >= monitorStartedAtUtc ||
         latestAccountSwitchAtUtc is not { } latestSwitch ||
         completedAtUtc > latestSwitch);

    private static (
        string? AccountKey,
        DateTimeOffset? CompletedAtUtc,
        DateTimeOffset? StartedAtUtc)
        SelectSuccessfulActivityMarker(LocalPatGatewayActivitySnapshot activity)
    {
        // v10 exposes a marker that is written after a complete successful response.
        // v3-v7's LastModelRequestAccountKey is a *started/attempted* credential.  It can
        // point at an exhausted source even when transparent retry later succeeds on a
        // different account.  Treating that legacy field as a success marker is precisely
        // what made the quota page jump back to the previous account after a 158 switch.
        // Fail closed until the authenticated health response proves the success-only v8
        // protocol; the explicit armed/active route remains the only legacy recovery path.
        if (LocalPatGateway.IsCurrentRotationProtocolValue(activity.RotationProtocol) &&
            PatGatewayRotationStore.TryNormalizeAccountKey(
                    activity.LastSuccessfulModelRequestAccountKey,
                    out var successfulKey) &&
            activity.LastSuccessfulModelRequestCompletedAtUtc is { } successfulAt)
        {
            return (
                successfulKey,
                successfulAt,
                activity.LastSuccessfulModelRequestStartedAtUtc);
        }

        // A v9 gateway has no success-only field and cannot be upgraded while Codex is
        // continuously issuing requests.  A v10 gateway normally does expose that field,
        // but a compatible relay may complete a valid 2xx stream without a parseable
        // Responses terminal/id envelope.  Both cases still expose enough bounded
        // evidence for a transparent retry: a quota-limited source followed within two
        // minutes by a completed request through a different candidate.  Use this narrow
        // fallback only for that retry shape; never treat an isolated last-attempt marker
        // as a successful account (the original source of the card misalignment).
        if ((string.Equals(activity.RotationProtocol, "request-boundary-v9", StringComparison.Ordinal) ||
             LocalPatGateway.IsCurrentRotationProtocolValue(activity.RotationProtocol)) &&
            activity.ActiveModelRequests == 0 &&
            activity.LastModelRequestCompletedAtUtc is { } completedAt &&
            activity.LastQuotaLimitedAtUtc is { } quotaAt &&
            completedAt >= quotaAt &&
            completedAt - quotaAt <= LegacyTransparentInferenceWindow &&
            PatGatewayRotationStore.TryNormalizeAccountKey(
                activity.LastModelRequestAccountKey,
                out var inferredKey) &&
            PatGatewayRotationStore.TryNormalizeAccountKey(
                activity.LastQuotaLimitedAccountKey,
                out var limitedKey) &&
            !string.Equals(inferredKey, limitedKey, StringComparison.Ordinal))
        {
            return (
                inferredKey,
                completedAt,
                activity.LastModelRequestStartedAtUtc);
        }

        return (null, null, null);
    }

    /// <summary>
    /// Regression coverage for the v7-to-v8 hand-off.  A legacy health snapshot may have a
    /// perfectly plausible last-started account and completion timestamp, but it is not
    /// evidence that that account produced the response.  Only the v8 success-only pair is
    /// allowed to identify the account shown by the quota/current-account UI.
    /// </summary>
    internal static void ValidateGatewaySuccessfulActivityIsolation()
    {
        var now = DateTimeOffset.UtcNow;
        if (!ShouldPollPatGatewayActivity(
                formClosed: false,
                formDisposed: false,
                pollRunning: false,
                rotationEnabled: true,
                gatewayEnabled: true) ||
            ShouldPollPatGatewayActivity(
                formClosed: false,
                formDisposed: false,
                pollRunning: false,
                rotationEnabled: true,
                gatewayEnabled: false) ||
            ShouldRecoverMissingPatGateway(
                officialWindowsClientRunning: false,
                now,
                lastRecoveryAttempt: null) ||
            !ShouldRecoverMissingPatGateway(
                officialWindowsClientRunning: true,
                now,
                lastRecoveryAttempt: null))
        {
            throw new InvalidOperationException(
                "Gateway activity polling must remain independent of desktop-process detection, " +
                "while missing-gateway recovery stays limited to a live official client.");
        }

        var attemptedKey = new string('A', 64);
        var successfulKey = new string('B', 64);
        var legacy = new LocalPatGatewayActivitySnapshot(
            ActiveModelRequests: 0,
            LastModelRequestStartedAtUtc: now.AddSeconds(-3),
            LastModelRequestCompletedAtUtc: now.AddSeconds(-2),
            LastQuotaLimitedAtUtc: now.AddSeconds(-1),
            Rotation: null,
            CompletedModelRequests: 12,
            LastModelRequestAccountKey: attemptedKey,
            LastQuotaLimitedAccountKey: attemptedKey,
            RotationProtocol: LocalPatGateway.ConfirmedQuotaRotationProtocolValue,
            LastSuccessfulModelRequestAccountKey: null,
            LastSuccessfulModelRequestCompletedAtUtc: null,
            LastSuccessfulModelRequestStartedAtUtc: null);
        var legacyMarker = SelectSuccessfulActivityMarker(legacy);
        if (legacyMarker.AccountKey != null || legacyMarker.CompletedAtUtc != null)
        {
            throw new InvalidOperationException(
                "A pre-v8 gateway attempt marker must not become the successful account.");
        }

        var current = new LocalPatGatewayActivitySnapshot(
            ActiveModelRequests: 2,
            LastModelRequestStartedAtUtc: now.AddSeconds(-3),
            LastModelRequestCompletedAtUtc: now.AddSeconds(-2),
            LastQuotaLimitedAtUtc: null,
            Rotation: null,
            CompletedModelRequests: 13,
            LastModelRequestAccountKey: attemptedKey,
            LastQuotaLimitedAccountKey: null,
            RotationProtocol: LocalPatGateway.RotationProtocolValue,
            LastSuccessfulModelRequestAccountKey: successfulKey,
            LastSuccessfulModelRequestCompletedAtUtc: now.AddSeconds(-1),
            LastSuccessfulModelRequestStartedAtUtc: now.AddSeconds(-3));
        var currentMarker = SelectSuccessfulActivityMarker(current);
        if (!string.Equals(currentMarker.AccountKey, successfulKey, StringComparison.Ordinal) ||
            currentMarker.CompletedAtUtc != current.LastSuccessfulModelRequestCompletedAtUtc ||
            currentMarker.StartedAtUtc != current.LastSuccessfulModelRequestStartedAtUtc ||
            !ShouldReconcileInitialSuccessfulActivity(
                current,
                now.AddSeconds(-2),
                latestAccountSwitchAtUtc: now.AddSeconds(-4)) ||
            ShouldReconcileInitialSuccessfulActivity(
                current,
                now,
                latestAccountSwitchAtUtc: now))
        {
            throw new InvalidOperationException(
                "The active v10 success-only account marker was not selected for immediate reconciliation.");
        }

        var persisted = new PatGatewaySuccessfulActivity(
            Sequence: 15,
            AccountKey: successfulKey,
            StartedAtUtc: now.AddSeconds(-3),
            CompletedAtUtc: now.AddSeconds(-1));
        var persistedSnapshot = CreatePersistedSuccessfulActivitySnapshot(
            persisted,
            PatGatewayRotationSnapshot.Empty);
        var persistedMarker = SelectSuccessfulActivityMarker(persistedSnapshot);
        if (!string.Equals(persistedMarker.AccountKey, successfulKey, StringComparison.Ordinal) ||
            persistedMarker.StartedAtUtc != persisted.StartedAtUtc ||
            persistedMarker.CompletedAtUtc != persisted.CompletedAtUtc)
        {
            throw new InvalidOperationException(
                "The durable successful-activity record was not projected into the unified account marker.");
        }

        if (!ShouldReconcileInitialSuccessfulActivity(
                current,
                monitorStartedAtUtc: now.AddMinutes(1),
                latestAccountSwitchAtUtc: now.AddSeconds(-2)) ||
            ShouldReconcileInitialSuccessfulActivity(
                current,
                monitorStartedAtUtc: now.AddMinutes(1),
                latestAccountSwitchAtUtc: now))
        {
            throw new InvalidOperationException(
                "A Manager restart must adopt a newer completed gateway success, but must " +
                "preserve an account selection made after that response.");
        }

        var legacyTransparent = new LocalPatGatewayActivitySnapshot(
            ActiveModelRequests: 0,
            LastModelRequestStartedAtUtc: now.AddSeconds(-4),
            LastModelRequestCompletedAtUtc: now.AddSeconds(-1),
            LastQuotaLimitedAtUtc: now.AddSeconds(-3),
            Rotation: null,
            CompletedModelRequests: 14,
            LastModelRequestAccountKey: successfulKey,
            LastQuotaLimitedAccountKey: attemptedKey,
            LastQuotaLimitedSequence: 99,
            RotationProtocol: "request-boundary-v9",
            LastSuccessfulModelRequestAccountKey: null,
            LastSuccessfulModelRequestCompletedAtUtc: null);
        var inferredMarker = SelectSuccessfulActivityMarker(legacyTransparent);
        if (inferredMarker.AccountKey != successfulKey ||
            inferredMarker.CompletedAtUtc != legacyTransparent.LastModelRequestCompletedAtUtc)
        {
            throw new InvalidOperationException(
                "The bounded v9 transparent-retry fallback was not selected.");
        }
    }

    private void ReconcilePatGatewaySuccessfulAccount(
        LocalPatGatewayActivitySnapshot activity)
    {
        // A pre-v8 gateway has no success-only marker.  While its armed/active route is
        // still visible, LastModelRequestAccountKey can describe the exhausted source
        // even though the route has already committed the target.  Let the explicit route
        // reconciler own that boundary; otherwise the compatibility fallback below could
        // immediately switch the UI back to the failed source.
        if (activity.LastSuccessfulModelRequestAccountKey == null &&
            activity.Rotation?.Status is PatGatewayRotationStatus.Armed or
                PatGatewayRotationStatus.Active)
        {
            return;
        }

        var (accountKey, completedAtUtc, startedAtUtc) =
            SelectSuccessfulActivityMarker(activity);
        if (accountKey == null || completedAtUtc is not { } completedAt)
        {
            return;
        }

        if (activity.Rotation is
                {
                    Status: PatGatewayRotationStatus.Active,
                    ActivatedAtUtc: { } activatedAtUtc
                } activeRoute &&
            PatGatewayRotationStore.TryNormalizeAccountKey(
                activeRoute.TargetAccountKey,
                out var activeTargetKey) &&
            !string.Equals(accountKey, activeTargetKey, StringComparison.Ordinal) &&
            completedAt < activatedAtUtc)
        {
            // Route activation is an account boundary. Until the first target response
            // completes, the health document can still carry the source's previous success.
            // Do not move the UI backwards across that boundary.
            return;
        }

        var requestCount = activity.CompletedModelRequests;
        var isNew = false;
        if (_patGatewayLastObservedSuccessfulCompletionAtUtc is not { } previousAt)
        {
            isNew = true;
        }
        else if (completedAt > previousAt)
        {
            isNew = true;
        }
        else if (requestCount is { } currentCount &&
                 _patGatewayLastObservedSuccessfulRequestCount is { } previousCount &&
                 currentCount > previousCount)
        {
            isNew = true;
        }
        else if (completedAt == previousAt &&
                 !string.Equals(
                     accountKey,
                     _patGatewayLastObservedSuccessfulAccountKey,
                     StringComparison.Ordinal))
        {
            isNew = true;
        }
        if (!isNew)
        {
            return;
        }

        // Advance the marker before any UI/network work.  A transient account lookup or
        // refresh failure must not cause the same completed request to be treated as a new
        // switch on every 500 ms health poll.
        _patGatewayLastObservedSuccessfulAccountKey = accountKey;
        _patGatewayLastObservedSuccessfulCompletionAtUtc = completedAt;
        _patGatewayLastObservedSuccessfulRequestCount = requestCount;

        var observed = FindRotationAccount(accountKey);
        if (observed == null)
        {
            return;
        }

        var currentKey = GetCurrentAccountRecord() is { } current
            ? QuotaAccountIdentity.CreateKey(current)
            : null;
        var currentChanged = !string.Equals(
            currentKey,
            accountKey,
            StringComparison.Ordinal);
        if (currentChanged)
        {
            // This is a real completed model response, so it is safe to create the usage
            // attribution boundary.  It also covers sticky-session winners that never
            // created a global rotation record.
            // Usage events are emitted while the response is streaming, before the
            // gateway reaches EOF.  Use the request start as the attribution boundary
            // so token_count snapshots from this turn cannot remain attached to the
            // previous account merely because completion was observed a few seconds later.
            var attributionBoundary = startedAtUtc is { } started && started <= completedAt
                ? started
                : completedAt;
            _usageTracker.RecordSwitch(
                observed,
                "pat-gateway-observed",
                attributionBoundary);
            SetCurrentAccount(observed.Name, false, persistSettings: true);
            _codex.QueueHotRotatedOfficialAccountDisplay(observed, _accounts);
            ManagerLifecycleDiagnostics.Write(
                "pat-gateway-successful-account-synchronized",
                $"account={observed.Name}; account_key={accountKey}; " +
                $"completed_at_utc={completedAt:o}");
        }

        var shouldRetargetOfficialRefresh =
            !observed.IsCompatibleApi &&
            !string.Equals(
                _launchedOfficialQuotaAccountKey,
                accountKey,
                StringComparison.Ordinal);
        _launchedOfficialQuotaAccountKey = observed.IsCompatibleApi ? null : accountKey;
        if (shouldRetargetOfficialRefresh && !observed.IsCompatibleApi)
        {
            _officialQuotaRefreshAttemptedAt.Remove(accountKey);
        }

        if (currentChanged || shouldRetargetOfficialRefresh || isNew)
        {
            _statusBox.Text = observed.IsCompatibleApi
                ? $"当前实际模型请求已由 API 账号 {observed.Name} 完成；额度页面已同步。"
                : $"当前实际模型请求已由 {observed.Name} 完成；正在刷新该账号官方额度…";
            if (currentChanged)
            {
                RenderCards();
                ResetCardsScrollPosition();
            }
            if (shouldRetargetOfficialRefresh && !observed.IsCompatibleApi)
            {
                StartOfficialQuotaRefresh(observed);
            }
        }
    }

    /// <summary>
    /// 2.2.8 and earlier could persist an active/armed route from body-only 429 evidence.
    /// On the first quiet observation of a pre-v8 gateway, retire that route in place. This
    /// edits only the authenticated route/ordinary-affinity caches: the listener process is
    /// neither stopped nor restarted, so an updater can safely adopt a live gateway.
    /// </summary>
    private async Task<bool> RetireUnconfirmedLegacyGatewayRouteAsync(
        LocalPatGatewayActivitySnapshot activity)
    {
        if (_legacyPatGatewayRouteRetired ||
            LocalPatGateway.IsCurrentRotationProtocolValue(
                activity.RotationProtocol))
        {
            _legacyPatGatewayRouteRetired = true;
            return false;
        }
        if (!LocalPatGateway.IsCompatibleRotationProtocolValue(
                activity.RotationProtocol))
        {
            return false;
        }

        var route = activity.Rotation;
        if (route?.Status is not (PatGatewayRotationStatus.Armed or
                PatGatewayRotationStatus.Active))
        {
            _legacyPatGatewayRouteRetired = true;
            return false;
        }
        if (_legacyPatGatewayRouteRetirementRunning ||
            !PatAutoRotationPolicy.IsGatewayQuiet(activity, DateTimeOffset.UtcNow))
        {
            // Do not let the old snapshot reconcile its unconfirmed target while waiting
            // for the current request to finish; the 500 ms monitor will retry at quiet.
            return true;
        }

        _legacyPatGatewayRouteRetirementRunning = true;
        try
        {
            var transport = FindRotationAccount(route.TransportAccountKey);
            var cleared = await LocalPatGateway.ClearRotationAsync();
            if (!cleared)
            {
                return true;
            }

            if (PatGatewayRotationStore.TryNormalizeAccountKey(
                    route.TargetAccountKey,
                    out var oldTargetKey))
            {
                _ = await LocalPatGateway.InvalidateOrdinarySessionAffinityAsync(
                    oldTargetKey);
            }

            _legacyPatGatewayRouteRetired = true;
            CancelPendingPatAutoRotation(resetState: true);
            if (transport != null &&
                AccountRotationConfiguration.GetPool(_appSettings, transport) !=
                    AccountRotationPool.None)
            {
                AccountRotationConfiguration.MarkUsed(_appSettings, transport);
                SetCurrentAccount(transport.Name, false, persistSettings: true);
            }
            _statusBox.Text =
                "已在不中断网关的情况下清除 2.2.8 及更早版本留下的未确认轮换路由；" +
                "后续只接受 v8 明确额度证据。";
            ManagerLifecycleDiagnostics.Write(
                "legacy-unconfirmed-gateway-route-retired",
                "gateway_restarted=false; route_cleared=true; ordinary_affinity_invalidated=true");
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or HttpRequestException or InvalidDataException or
                UnauthorizedAccessException or InvalidOperationException or
                NotSupportedException or ArgumentException or
                System.Text.Json.JsonException)
        {
            return true;
        }
        finally
        {
            _legacyPatGatewayRouteRetirementRunning = false;
        }
    }

    private bool IsGatewayQuotaSignalSuperseded(
        PatGatewayQuotaSignal signal,
        string accountKey,
        UsageRateLimitWindow? fiveHourWindow,
        DateTimeOffset nowUtc)
    {
        if (signal.ResetAtUtc is { } resetAtUtc &&
            resetAtUtc + AccountRotationConfiguration.PrimaryResetGracePeriod <= nowUtc)
        {
            return true;
        }
        if (!_liveRateLimitCache.TryGetValue(accountKey, out var liveSnapshot) ||
            liveSnapshot.ObservedAtUtc <= signal.ObservedAtUtc)
        {
            return false;
        }

        // A newer official quota read showing a live, sub-100% five-hour window is stronger
        // evidence than an older gateway event.  This is the switch-time re-review that
        // prevents one historical 429 from continuing to eject a recovered account.
        return fiveHourWindow?.UsedPercent is { } usedPercent &&
               usedPercent < 100D &&
               (fiveHourWindow.ResetsAtUtc == null ||
                fiveHourWindow.ResetsAtUtc > nowUtc);
    }

    private void ReconcileTransparentPatGatewayRotation(
        LocalPatGatewayActivitySnapshot activity)
    {
        var rotation = activity.Rotation;
        if (rotation?.Status != PatGatewayRotationStatus.Active ||
            rotation.ActivatedAtUtc is not { } activatedAtUtc ||
            FindRotationAccount(rotation.TargetAccountKey) is not { } target)
        {
            return;
        }

        // v5 can commit the active route before the 500 ms UI poll sees the durable
        // source 429. Preserve that exhaustion first so the circular ring cannot select
        // the source again on the following request.  This must also run after a Manager
        // restart where CurrentAccountName may already have been reconciled to the target.
        if (FindRotationAccount(rotation.SourceAccountKey) is { } source)
        {
            try
            {
                _patGatewayQuotaSignalStore ??=
                    new PatGatewayQuotaSignalStore(_store.RootPath);
                var sourceKey = QuotaAccountIdentity.CreateKey(source);
                var signal = _patGatewayQuotaSignalStore.ReadLatestForAccount(
                    sourceKey,
                    DateTimeOffset.UtcNow);
                var sourceWindow = GetCachedFiveHourWindowForGatewaySignal(sourceKey);
                var sourceOfficiallyExhausted =
                    sourceWindow?.UsedPercent is >= 100;
                var protocolConfirmsExhaustion =
                    LocalPatGateway.IsConfirmedQuotaRotationProtocolValue(
                        activity.RotationProtocol);
                if (signal != null &&
                    signal.ObservedAtUtc >=
                        activatedAtUtc - PatGatewayQuotaSignalStore.SignalLifetime &&
                    signal.ObservedAtUtc <= activatedAtUtc.AddSeconds(5) &&
                    !_patGatewayReconciledQuotaSignalSequences.Contains(signal.Sequence) &&
                    (protocolConfirmsExhaustion || sourceOfficiallyExhausted))
                {
                    RecordPatRotationAccountExhausted(
                        source,
                        sourceWindow?.ResetsAtUtc,
                        signal.ObservedAtUtc);
                    _patGatewayReconciledQuotaSignalSequences.Add(signal.Sequence);
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or NotSupportedException or ArgumentException or
                System.Text.Json.JsonException)
            {
                // Route reconciliation is still authoritative. A later durable-file poll
                // can retry the optional reset metadata without undoing the active route.
            }
        }

        var targetKey = QuotaAccountIdentity.CreateKey(target);
        if (GetCurrentAccountRecord() is { } current &&
            QuotaAccountIdentity.CreateKey(current).Equals(
                targetKey,
                StringComparison.Ordinal))
        {
            return;
        }

        CancelPendingPatAutoRotation(resetState: false);
        CompletePatGatewayRotation(target, activatedAtUtc);
        ManagerLifecycleDiagnostics.Write(
            "pat-gateway-transparent-route-reconciled",
            "active_target_changed=true");
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
