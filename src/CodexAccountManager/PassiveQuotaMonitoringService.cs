using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

public sealed record PassiveQuotaMonitoringState(
    string AccountKey,
    string AccountName,
    bool IsEnabled,
    string? EpochId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    PassiveQuotaEstimate? LastEstimate,
    DateTimeOffset? LastEstimateRecordedAtUtc,
    int? StartingUsedPercent,
    long? StartingWindowMinutes,
    double? DisplayCapacityUsd = null,
    string? DisplayQuotaKind = null,
    DateTimeOffset? DisplayResetAtUtc = null)
{
    public bool HasRetainedEstimate => !IsEnabled && LastEstimate != null;
}

public sealed record PassiveQuotaMonitoringResult(
    PassiveQuotaMonitoringState State,
    PassiveQuotaEstimate? Estimate,
    bool IsRetainedEstimate,
    string Message)
{
    public bool IsEnabled => State.IsEnabled;

    public string StatusCode => !State.IsEnabled
        ? "disabled"
        : Estimate?.StatusCode ?? "collecting";
}

/// <summary>
/// Per-account lifecycle and persistence for passive quota monitoring. Enabling always
/// creates a fresh epoch; disabling freezes the most recently persisted estimate.
/// Analysis is delegated exclusively to the local natural-usage analyzer.
/// </summary>
public sealed class PassiveQuotaMonitoringService
{
    internal const string FileName = "quota-monitor-settings.json";
    private const int CurrentSchemaVersion = 6;
    internal const int MaximumOfficialObservationCount = 256;
    private static readonly TimeSpan OfficialResetTimestampTolerance = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> FileGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly object _gate;

    public PassiveQuotaMonitoringService(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("额度监测状态目录不能为空。", nameof(rootPath));
        }

        var normalizedRoot = System.IO.Path.GetFullPath(rootPath);
        _path = System.IO.Path.Combine(normalizedRoot, FileName);
        _gate = FileGates.GetOrAdd(_path, static _ => new object());
    }

    public string Path => _path;

    public PassiveQuotaMonitoringState GetState(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        lock (_gate)
        {
            var file = LoadUnsafe();
            var stored = FindAccount(file, accountKey);
            return stored == null
                ? MakeDisabledState(accountKey, account.Name)
                : ToPublicState(stored, account.Name);
        }
    }

    /// <summary>
    /// Returns the read-only official percentage boundaries captured for the current retained
    /// monitoring epoch.  These snapshots carry no model cost; callers use them to keep the
    /// official remaining-quota graph continuous even between natural token events.
    /// </summary>
    public IReadOnlyList<PassiveQuotaOfficialObservation> GetOfficialObservations(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        lock (_gate)
        {
            var stored = FindAccount(LoadUnsafe(), accountKey);
            if (stored?.StartedAtUtc is not { } startedAtUtc)
            {
                return [];
            }

            var observations = (stored.OfficialObservations ?? [])
                .Where(item => item.TimestampUtc >= startedAtUtc.ToUniversalTime())
                .Select(item => new PassiveQuotaOfficialObservation(
                    item.TimestampUtc,
                    item.UsedPercent,
                    item.WindowMinutes,
                    item.ResetsAtUtc,
                    startedAtUtc.ToUniversalTime()))
                .ToArray();
            return NormalizeOfficialObservations(observations);
        }
    }

    internal static IReadOnlyList<PassiveQuotaOfficialObservation> NormalizeOfficialObservations(
        IEnumerable<PassiveQuotaOfficialObservation>? observations,
        DateTimeOffset? preferredResetAtUtc = null)
    {
        var normalized = new List<PassiveQuotaOfficialObservation>();
        DateTimeOffset? activeResetAtUtc = null;
        long? activeWindowMinutes = null;
        int? activeMaximumUsedPercent = null;
        var preferredReset = preferredResetAtUtc?.ToUniversalTime();
        foreach (var source in (observations ?? [])
                     .Where(item => item.TimestampUtc > DateTimeOffset.MinValue)
                     .OrderBy(item => item.TimestampUtc))
        {
            var item = source with
            {
                TimestampUtc = source.TimestampUtc.ToUniversalTime(),
                ResetsAtUtc = source.ResetsAtUtc?.ToUniversalTime(),
                ActivationEpochUtc = source.ActivationEpochUtc?.ToUniversalTime()
            };
            if (preferredReset.HasValue &&
                item.ResetsAtUtc is { } sourceReset &&
                sourceReset > preferredReset.Value + OfficialResetTimestampTolerance)
            {
                continue;
            }

            var startsNewCycle = activeWindowMinutes.HasValue &&
                                 Math.Abs(activeWindowMinutes.Value - item.WindowMinutes) > 5;
            if (activeResetAtUtc is { } activeReset)
            {
                if (item.ResetsAtUtc is { } candidateReset)
                {
                    var difference = candidateReset - activeReset;
                    if (difference < -OfficialResetTimestampTolerance)
                    {
                        continue;
                    }

                    item = item with
                    {
                        ResetsAtUtc = difference.Duration() <= OfficialResetTimestampTolerance
                            ? activeReset
                            : candidateReset
                    };
                    startsNewCycle |= difference > OfficialResetTimestampTolerance;
                    activeResetAtUtc = item.ResetsAtUtc;
                }
                else
                {
                    item = item with { ResetsAtUtc = activeReset };
                }
            }
            else if (item.ResetsAtUtc is { } firstReset)
            {
                activeResetAtUtc = firstReset;
                startsNewCycle = true;
            }

            if (startsNewCycle)
            {
                activeMaximumUsedPercent = null;
            }
            if (activeMaximumUsedPercent.HasValue &&
                item.UsedPercent < activeMaximumUsedPercent.Value)
            {
                continue;
            }

            if (normalized.LastOrDefault() is { } previous &&
                previous.UsedPercent == item.UsedPercent &&
                previous.WindowMinutes == item.WindowMinutes &&
                SameResetTimestamp(previous.ResetsAtUtc, item.ResetsAtUtc))
            {
                continue;
            }

            normalized.Add(item);
            activeWindowMinutes = item.WindowMinutes;
            activeMaximumUsedPercent = Math.Max(
                activeMaximumUsedPercent ?? item.UsedPercent,
                item.UsedPercent);
        }

        return normalized.TakeLast(MaximumOfficialObservationCount).ToArray();
    }

    /// <summary>
    /// Starts a new monitoring epoch at <paramref name="startedAt"/>. Calling this
    /// while an older epoch is enabled intentionally replaces it and clears its active
    /// estimate so a changed subscription state cannot inherit historical calibration.
    /// </summary>
    public PassiveQuotaMonitoringState Enable(
        AccountRecord account,
        DateTimeOffset startedAt,
        double? startingUsedPercent = null,
        long? startingWindowMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        var startedAtUtc = startedAt.ToUniversalTime();
        lock (_gate)
        {
            var file = LoadUnsafe();
            var stored = FindAccount(file, accountKey) ?? AddAccount(file, accountKey, account.Name);
            stored.AccountName = account.Name;
            stored.IsEnabled = true;
            stored.EpochId = Guid.NewGuid().ToString("N");
            stored.StartedAtUtc = startedAtUtc;
            stored.StoppedAtUtc = null;
            stored.LastEstimate = null;
            stored.LastEstimateRecordedAtUtc = null;
            stored.StartingUsedPercent = NormalizeStartingUsedPercent(startingUsedPercent);
            stored.StartingWindowMinutes = NormalizeStartingWindowMinutes(startingWindowMinutes);
            stored.DisplayCapacityUsd = null;
            stored.DisplayQuotaKind = null;
            stored.DisplayResetAtUtc = null;
            stored.OfficialObservations = [];
            SaveUnsafe(file);
            return ToPublicState(stored, account.Name);
        }
    }

    /// <summary>
    /// Stops the active epoch. The latest estimate is retained as a frozen reference
    /// and subsequent Analyze calls do not update it until Enable creates a new epoch.
    /// </summary>
    public PassiveQuotaMonitoringState Disable(AccountRecord account, DateTimeOffset stoppedAt)
    {
        ArgumentNullException.ThrowIfNull(account);
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        var stoppedAtUtc = stoppedAt.ToUniversalTime();
        lock (_gate)
        {
            var file = LoadUnsafe();
            var stored = FindAccount(file, accountKey) ?? AddAccount(file, accountKey, account.Name);
            stored.AccountName = account.Name;
            if (stored.IsEnabled)
            {
                stored.IsEnabled = false;
                stored.StoppedAtUtc = stoppedAtUtc;
                SaveUnsafe(file);
            }
            return ToPublicState(stored, account.Name);
        }
    }

    /// <summary>
    /// Captures the newest natural-log estimate and then freezes the epoch. This is the
    /// preferred UI close action when an up-to-date final result is desired.
    /// </summary>
    public PassiveQuotaMonitoringState DisableAndCapture(
        AccountRecord account,
        AccountUsageSummary usage,
        Func<UsageEvent, double> estimateEventCostUsd,
        DateTimeOffset stoppedAt)
    {
        _ = Analyze(account, usage, estimateEventCostUsd, stoppedAt);
        return Disable(account, stoppedAt);
    }

    public PassiveQuotaMonitoringResult Analyze(
        AccountRecord account,
        AccountUsageSummary usage,
        Func<UsageEvent, double> estimateEventCostUsd,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(estimateEventCostUsd);

        var state = GetState(account);
        if (!state.IsEnabled || string.IsNullOrWhiteSpace(state.EpochId) || !state.StartedAtUtc.HasValue)
        {
            return MakeInactiveResult(state, usage);
        }

        var epochId = state.EpochId;
        var officialObservations = CaptureOfficialObservationsIfCurrent(
            account,
            epochId,
            state.StartedAtUtc.Value,
            state.StartingWindowMinutes,
            usage);
        var estimate = PassiveQuotaMonitor.Analyze(
            account,
            usage,
            estimateEventCostUsd,
            now,
            state.StartedAtUtc.Value,
            state.StartingUsedPercent,
            state.StartingWindowMinutes,
            officialObservations);
        var currentState = RecordEstimateIfCurrent(account, epochId, estimate, usage, now);
        if (!currentState.IsEnabled ||
            !string.Equals(currentState.EpochId, epochId, StringComparison.Ordinal))
        {
            // The user disabled or restarted monitoring while analysis was running.
            // Never let a completed calculation leak into the newer lifecycle state.
            return MakeInactiveResult(currentState, usage);
        }

        return new PassiveQuotaMonitoringResult(
            currentState,
            estimate,
            false,
            $"额度监测已开启；本轮只使用 {currentState.StartedAtUtc!.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} 之后的自然使用日志。" );
    }

    private IReadOnlyList<PassiveQuotaOfficialObservation> CaptureOfficialObservationsIfCurrent(
        AccountRecord account,
        string epochId,
        DateTimeOffset startedAtUtc,
        long? startingWindowMinutes,
        AccountUsageSummary usage)
    {
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        lock (_gate)
        {
            var file = LoadUnsafe();
            var stored = FindAccount(file, accountKey);
            if (stored == null ||
                !stored.IsEnabled ||
                !string.Equals(stored.EpochId, epochId, StringComparison.Ordinal))
            {
                return [];
            }

            stored.OfficialObservations ??= [];
            var startedAt = startedAtUtc.ToUniversalTime();
            var preferredWindow = SelectPreferredQuotaWindow(usage, startingWindowMinutes);
            var preferredKind = preferredWindow?.Kind ??
                                AccountQuotaLimitType.ClassifyWindow(startingWindowMinutes);
            var preferredResetAtUtc = preferredWindow?.ResetAtUtc?.ToUniversalTime();
            var existingObservations = NormalizeOfficialObservations(
                    stored.OfficialObservations
                        .Where(item => item.TimestampUtc >= startedAt)
                        .Select(item => new PassiveQuotaOfficialObservation(
                            item.TimestampUtc,
                            item.UsedPercent,
                            item.WindowMinutes,
                            item.ResetsAtUtc,
                            startedAt))
                        .Concat(BuildTimelineOfficialObservations(
                            usage,
                            preferredKind,
                            startedAt)),
                    preferredResetAtUtc)
                .ToList();
            var observedAtUtc = usage.RateLimitObservedAtUtc?.ToUniversalTime();
            var usedPercent = NormalizeStartingUsedPercent(preferredWindow?.UsedPercent);
            if (preferredWindow != null &&
                observedAtUtc.HasValue &&
                observedAtUtc.Value >= startedAtUtc.ToUniversalTime() &&
                usedPercent.HasValue)
            {
                existingObservations = NormalizeOfficialObservations(
                        existingObservations.Append(new PassiveQuotaOfficialObservation(
                            observedAtUtc.Value,
                            usedPercent.Value,
                            preferredWindow.WindowMinutes,
                            preferredWindow.ResetAtUtc,
                            startedAt)),
                        preferredResetAtUtc)
                    .ToList();
            }

            var normalizedStored = existingObservations
                .Select(item => new StoredOfficialObservation
                {
                    TimestampUtc = item.TimestampUtc,
                    UsedPercent = item.UsedPercent,
                    WindowMinutes = item.WindowMinutes,
                    ResetsAtUtc = item.ResetsAtUtc
                })
                .ToList();
            if (!StoredOfficialObservationsEqual(stored.OfficialObservations, normalizedStored))
            {
                stored.OfficialObservations = normalizedStored;
                SaveUnsafe(file);
            }

            return existingObservations;
        }
    }

    private static IEnumerable<PassiveQuotaOfficialObservation> BuildTimelineOfficialObservations(
        AccountUsageSummary usage,
        AccountQuotaWindowKind preferredKind,
        DateTimeOffset startedAtUtc)
    {
        if (preferredKind == AccountQuotaWindowKind.Unknown)
        {
            yield break;
        }

        foreach (var item in usage.Timeline.Where(item =>
                     item.TimestampUtc >= startedAtUtc &&
                     item.Source is UsageEventSource.Natural or UsageEventSource.OfficialSnapshot))
        {
            double? usedPercent = null;
            long? windowMinutes = null;
            DateTimeOffset? resetAtUtc = null;
            if (AccountQuotaLimitType.ClassifyWindow(item.RateLimitWindowMinutes) == preferredKind)
            {
                usedPercent = item.RateLimitUsedPercent;
                windowMinutes = item.RateLimitWindowMinutes;
                resetAtUtc = item.RateLimitResetAtUtc;
            }
            else if (AccountQuotaLimitType.ClassifyWindow(item.SecondaryRateLimitWindowMinutes) == preferredKind)
            {
                usedPercent = item.SecondaryRateLimitUsedPercent;
                windowMinutes = item.SecondaryRateLimitWindowMinutes;
                resetAtUtc = item.SecondaryRateLimitResetAtUtc;
            }

            var normalizedPercent = NormalizeStartingUsedPercent(usedPercent);
            if (!windowMinutes.HasValue || !normalizedPercent.HasValue)
            {
                continue;
            }

            yield return new PassiveQuotaOfficialObservation(
                item.TimestampUtc.ToUniversalTime(),
                normalizedPercent.Value,
                windowMinutes.Value,
                resetAtUtc?.ToUniversalTime(),
                startedAtUtc);
        }
    }

    private static bool StoredOfficialObservationsEqual(
        IReadOnlyList<StoredOfficialObservation>? left,
        IReadOnlyList<StoredOfficialObservation> right)
    {
        left ??= [];
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].TimestampUtc != right[index].TimestampUtc ||
                left[index].UsedPercent != right[index].UsedPercent ||
                left[index].WindowMinutes != right[index].WindowMinutes ||
                left[index].ResetsAtUtc != right[index].ResetsAtUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static AccountQuotaWindowSnapshot? SelectPreferredQuotaWindow(
        AccountUsageSummary usage,
        long? startingWindowMinutes)
    {
        var preferredKind = AccountQuotaLimitType.ClassifyWindow(startingWindowMinutes);
        if (preferredKind != AccountQuotaWindowKind.Unknown &&
            usage.GetQuotaWindow(preferredKind) is { } preferred)
        {
            return preferred;
        }

        return usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour) ??
               usage.GetQuotaWindow(AccountQuotaWindowKind.Monthly) ??
               usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly);
    }

    private static bool SameResetTimestamp(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }
        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }
        return (left.Value - right.Value).Duration() <= OfficialResetTimestampTolerance;
    }

    private static DisplayCapacityAnchor ResolveDisplayCapacityAnchor(
        double? currentCapacityUsd,
        string? currentQuotaKind,
        DateTimeOffset? currentResetAtUtc,
        double? previousRemainingPercent,
        double? nextCapacityUsd,
        string nextQuotaKind,
        double? nextRemainingPercent,
        DateTimeOffset? nextResetAtUtc)
    {
        if (nextCapacityUsd is not { } nextCapacity ||
            !double.IsFinite(nextCapacity) ||
            nextCapacity <= 0D)
        {
            return new DisplayCapacityAnchor(
                currentCapacityUsd,
                currentQuotaKind,
                currentResetAtUtc ?? nextResetAtUtc);
        }

        var sameResetWithClockJitter = currentResetAtUtc.HasValue &&
                                       nextResetAtUtc.HasValue &&
                                       SameResetTimestamp(currentResetAtUtc, nextResetAtUtc);
        var effectiveResetAtUtc = sameResetWithClockJitter
            ? currentResetAtUtc
            : nextResetAtUtc ?? currentResetAtUtc;

        // Every new official integer-percent boundary produces a new natural-usage
        // calibration. Publish that newest total immediately; keeping the first total as
        // a window-long anchor made the official percentage move while the displayed
        // capacity stayed frozen. The canonical reset timestamp is still retained across
        // harmless sub-minute API clock jitter.
        return new DisplayCapacityAnchor(
            nextCapacity,
            string.IsNullOrWhiteSpace(nextQuotaKind) ? currentQuotaKind : nextQuotaKind,
            effectiveResetAtUtc);
    }

    internal static double? ProjectDisplayedRemainingUsd(
        double? displayCapacityUsd,
        double? latestOfficialRemainingPercent,
        double? fallbackRemainingUsd = null)
    {
        if (displayCapacityUsd is { } capacity &&
            double.IsFinite(capacity) &&
            capacity > 0D &&
            latestOfficialRemainingPercent is { } remainingPercent &&
            double.IsFinite(remainingPercent))
        {
            return capacity * Math.Clamp(remainingPercent, 0D, 100D) / 100D;
        }

        return fallbackRemainingUsd is { } fallback && double.IsFinite(fallback)
            ? fallback
            : null;
    }

    private PassiveQuotaMonitoringState RecordEstimateIfCurrent(
        AccountRecord account,
        string epochId,
        PassiveQuotaEstimate estimate,
        AccountUsageSummary usage,
        DateTimeOffset recordedAt)
    {
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        lock (_gate)
        {
            var file = LoadUnsafe();
            var stored = FindAccount(file, accountKey);
            if (stored == null ||
                !stored.IsEnabled ||
                !string.Equals(stored.EpochId, epochId, StringComparison.Ordinal))
            {
                return stored == null
                    ? MakeDisabledState(accountKey, account.Name)
                    : ToPublicState(stored, account.Name);
            }

            stored.AccountName = account.Name;
            var displayedWindow = SelectPreferredQuotaWindow(usage, stored.StartingWindowMinutes);
            var displayAnchor = ResolveDisplayCapacityAnchor(
                stored.DisplayCapacityUsd,
                stored.DisplayQuotaKind,
                stored.DisplayResetAtUtc,
                stored.LastEstimate?.LatestRemainingPercent,
                estimate.EstimatedTotalUsd,
                estimate.QuotaKind,
                estimate.LatestRemainingPercent,
                displayedWindow?.ResetAtUtc?.ToUniversalTime());
            var displayAnchorChanged = stored.DisplayCapacityUsd != displayAnchor.CapacityUsd ||
                                       !string.Equals(
                                           stored.DisplayQuotaKind,
                                           displayAnchor.QuotaKind,
                                           StringComparison.Ordinal) ||
                                       stored.DisplayResetAtUtc != displayAnchor.ResetAtUtc;
            stored.DisplayCapacityUsd = displayAnchor.CapacityUsd;
            stored.DisplayQuotaKind = displayAnchor.QuotaKind;
            stored.DisplayResetAtUtc = displayAnchor.ResetAtUtc;
            var snapshot = StoredEstimate.FromEstimate(estimate);
            var assessmentWindowsChanged = !StoredAssessmentWindowsEqual(
                stored.LastEstimate?.AssessmentWindows,
                snapshot.AssessmentWindows);
            var comparableSnapshot = stored.LastEstimate == null
                ? snapshot
                : snapshot with { AssessmentWindows = stored.LastEstimate.AssessmentWindows };
            if (stored.LastEstimate != comparableSnapshot ||
                assessmentWindowsChanged ||
                displayAnchorChanged)
            {
                stored.LastEstimate = snapshot;
                stored.LastEstimateRecordedAtUtc = recordedAt.ToUniversalTime();
                SaveUnsafe(file);
            }
            return ToPublicState(stored, account.Name, estimate);
        }
    }

    private static PassiveQuotaMonitoringResult MakeInactiveResult(
        PassiveQuotaMonitoringState state,
        AccountUsageSummary usage)
    {
        if (state.LastEstimate != null && IsEstimateApplicable(state.LastEstimate, usage))
        {
            var recordedAt = state.LastEstimateRecordedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知时间";
            return new PassiveQuotaMonitoringResult(
                state,
                state.LastEstimate,
                true,
                $"额度监测未开启；显示的是上一轮截至 {recordedAt} 的冻结结果，新日志不会继续更新该推断。");
        }

        return new PassiveQuotaMonitoringResult(
            state,
            null,
            false,
            state.LastEstimate == null
                ? "额度监测未开启；点击开启后会从当前时刻建立新的独立监测周期。"
                : "上一轮结果对应的官方额度窗口已经变化，因此仅保留历史数据，不再作为当前额度结论显示。" );
    }

    private static bool IsEstimateApplicable(
        PassiveQuotaEstimate estimate,
        AccountUsageSummary usage)
    {
        var hasCurrentWindow = usage.RateLimitWindowMinutes.HasValue ||
                               usage.SecondaryRateLimitWindowMinutes.HasValue;
        if (!hasCurrentWindow)
        {
            return true;
        }

        var preferredQuotaKind = usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour) != null
            ? "five_hour"
            : usage.GetQuotaWindow(AccountQuotaWindowKind.Monthly) != null
                ? "monthly"
                : usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly) != null
                    ? "weekly"
                    : null;
        return preferredQuotaKind != null && estimate.QuotaKind == preferredQuotaKind;
    }

    private static bool StoredAssessmentWindowsEqual(
        IReadOnlyList<StoredAssessmentWindow>? left,
        IReadOnlyList<StoredAssessmentWindow>? right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }
        return true;
    }

    private MonitoringFile LoadUnsafe()
    {
        if (!File.Exists(_path))
        {
            return new MonitoringFile();
        }

        try
        {
            var file = JsonSerializer.Deserialize<MonitoringFile>(File.ReadAllText(_path), JsonOptions) ??
                       new MonitoringFile();
            file.Accounts ??= [];
            foreach (var account in file.Accounts)
            {
                account.OfficialObservations ??= [];
                if (account.LastEstimate != null)
                {
                    account.LastEstimate.AssessmentWindows ??= [];
                }
            }
            file.Accounts = file.Accounts
                .Where(item => !string.IsNullOrWhiteSpace(item.AccountKey))
                .GroupBy(item => item.AccountKey, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();
            file.SchemaVersion = CurrentSchemaVersion;
            return file;
        }
        catch
        {
            // A damaged optional monitoring cache must not prevent account switching.
            return new MonitoringFile();
        }
    }

    private void SaveUnsafe(MonitoringFile file)
    {
        file.SchemaVersion = CurrentSchemaVersion;
        file.Accounts = file.Accounts
            .OrderBy(item => item.AccountName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.AccountKey, StringComparer.Ordinal)
            .ToList();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(file, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A leftover temporary cache is harmless and must not mask the save result.
            }
        }
    }

    private static StoredAccountState? FindAccount(MonitoringFile file, string accountKey) =>
        file.Accounts.FirstOrDefault(item => item.AccountKey.Equals(accountKey, StringComparison.Ordinal));

    private static StoredAccountState AddAccount(MonitoringFile file, string accountKey, string accountName)
    {
        var stored = new StoredAccountState
        {
            AccountKey = accountKey,
            AccountName = accountName
        };
        file.Accounts.Add(stored);
        return stored;
    }

    private static PassiveQuotaMonitoringState MakeDisabledState(string accountKey, string accountName) =>
        new(accountKey, accountName, false, null, null, null, null, null, null, null);

    private static PassiveQuotaMonitoringState ToPublicState(
        StoredAccountState stored,
        string currentAccountName,
        PassiveQuotaEstimate? liveEstimate = null)
    {
        return new PassiveQuotaMonitoringState(
            stored.AccountKey,
            currentAccountName,
            stored.IsEnabled,
            stored.EpochId,
            stored.StartedAtUtc,
            stored.StoppedAtUtc,
            liveEstimate ?? stored.LastEstimate?.ToEstimate(),
            stored.LastEstimateRecordedAtUtc,
            stored.StartingUsedPercent,
            stored.StartingWindowMinutes,
            stored.DisplayCapacityUsd,
            stored.DisplayQuotaKind,
            stored.DisplayResetAtUtc);
    }

    private static int? NormalizeStartingUsedPercent(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value) || value.Value < 0D || value.Value > 100D)
        {
            return null;
        }

        return Math.Clamp(
            (int)Math.Round(value.Value, MidpointRounding.AwayFromZero),
            0,
            100);
    }

    private static long? NormalizeStartingWindowMinutes(long? value)
    {
        return AccountQuotaLimitType.ClassifyWindow(value) is
            AccountQuotaWindowKind.FiveHour or
            AccountQuotaWindowKind.Weekly or
            AccountQuotaWindowKind.Monthly
            ? value
            : null;
    }

    private static void ValidateStableDisplayCapacityProjection()
    {
        var resetAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var first = ResolveDisplayCapacityAnchor(
            null,
            null,
            null,
            null,
            247.03D,
            "monthly",
            79D,
            resetAt);
        var second = ResolveDisplayCapacityAnchor(
            first.CapacityUsd,
            first.QuotaKind,
            first.ResetAtUtc,
            79D,
            250.52D,
            "monthly",
            78D,
            resetAt.AddSeconds(30));
        var third = ResolveDisplayCapacityAnchor(
            second.CapacityUsd,
            second.QuotaKind,
            second.ResetAtUtc,
            78D,
            255D,
            "monthly",
            77D,
            resetAt);
        var at79 = ProjectDisplayedRemainingUsd(first.CapacityUsd, 79D);
        var at78 = ProjectDisplayedRemainingUsd(second.CapacityUsd, 78D);
        var at77 = ProjectDisplayedRemainingUsd(third.CapacityUsd, 77D);
        if (first.CapacityUsd is not { } firstCapacity ||
            second.CapacityUsd is not { } secondCapacity ||
            third.CapacityUsd is not { } thirdCapacity ||
            Math.Abs(firstCapacity - 247.03D) > 0.000_001D ||
            Math.Abs(secondCapacity - 250.52D) > 0.000_001D ||
            Math.Abs(thirdCapacity - 255D) > 0.000_001D ||
            second.ResetAtUtc != resetAt ||
            at79 is not { } remainingAt79 ||
            at78 is not { } remainingAt78 ||
            at77 is not { } remainingAt77 ||
            Math.Abs(remainingAt79 - 195.1537D) > 0.000_001D ||
            Math.Abs(remainingAt78 - 195.4056D) > 0.000_001D ||
            Math.Abs(remainingAt77 - 196.35D) > 0.000_001D)
        {
            throw new InvalidOperationException(
                "Passive quota display capacity must publish every new official-percent recalibration.");
        }

        var reset = ResolveDisplayCapacityAnchor(
            third.CapacityUsd,
            third.QuotaKind,
            third.ResetAtUtc,
            77D,
            260D,
            "monthly",
            99D,
            resetAt.AddMonths(1));
        if (reset.CapacityUsd is not { } resetCapacity ||
            Math.Abs(resetCapacity - 260D) > 0.000_001D ||
            reset.ResetAtUtc != resetAt.AddMonths(1))
        {
            throw new InvalidOperationException(
                "Passive quota display-capacity anchor must rebase after an official quota reset.");
        }
    }

    internal static void Validate()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "codex-passive-monitor-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new PassiveQuotaMonitoringService(root);
            var epochStart = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
            var account = new AccountRecord
            {
                Name = "monitor-a",
                CodexHome = System.IO.Path.Combine(root, "account-a"),
                AuthKind = AccountAuthKind.AccessToken,
                QuotaLimitType = AccountQuotaLimitType.FiveHourAndWeekly,
                QuotaPrimaryWindowMinutes = 300,
                QuotaSecondaryWindowMinutes = 10_080
            };
            var otherAccount = new AccountRecord
            {
                Name = "monitor-b",
                CodexHome = System.IO.Path.Combine(root, "account-b"),
                AuthKind = AccountAuthKind.AccessToken,
                QuotaLimitType = AccountQuotaLimitType.Monthly,
                QuotaPrimaryWindowMinutes = 43_800
            };
            var weeklyAccount = new AccountRecord
            {
                Name = "monitor-weekly",
                CodexHome = System.IO.Path.Combine(root, "account-weekly"),
                AuthKind = AccountAuthKind.AccessToken,
                QuotaLimitType = AccountQuotaLimitType.WeeklyOnly,
                QuotaPrimaryWindowMinutes = 10_080
            };
            var enabled = service.Enable(
                account,
                epochStart,
                startingUsedPercent: 10D,
                startingWindowMinutes: 300);
            if (!enabled.IsEnabled ||
                !enabled.StartedAtUtc.HasValue ||
                enabled.StartedAtUtc.Value != epochStart ||
                enabled.StartingUsedPercent != 10 ||
                enabled.StartingWindowMinutes != 300 ||
                string.IsNullOrWhiteSpace(enabled.EpochId) ||
                enabled.LastEstimate != null ||
                service.GetState(otherAccount).IsEnabled)
            {
                throw new InvalidOperationException("Passive quota per-account enable state self-test failed.");
            }

            var usage = MakeValidationUsage(epochStart);
            static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;
            var activeResult = service.Analyze(account, usage, SyntheticCost, epochStart.AddMinutes(20));
            if (!activeResult.IsEnabled ||
                activeResult.IsRetainedEstimate ||
                activeResult.Estimate is not { Status: PassiveQuotaStatus.Normal, EstimatedTotalUsd: { } total } ||
                Math.Abs(total - 12D) > 0.000_001D ||
                activeResult.Estimate.AssessmentWindows.Count == 0 ||
                activeResult.Estimate.AssessmentWindows.Any(item =>
                    item.Status != PassiveQuotaStatus.Normal ||
                    Math.Abs(item.EstimatedTotalUsd - 12D) > 0.000_001D))
            {
                throw new InvalidOperationException("Passive quota epoch cutoff self-test failed.");
            }

            var reloaded = new PassiveQuotaMonitoringService(root).GetState(account);
            if (!reloaded.IsEnabled ||
                reloaded.EpochId != enabled.EpochId ||
                reloaded.StartingUsedPercent != 10 ||
                reloaded.StartingWindowMinutes != 300 ||
                reloaded.LastEstimate?.EstimatedTotalUsd is not { } reloadedTotal ||
                Math.Abs(reloadedTotal - 12D) > 0.000_001D ||
                reloaded.LastEstimate.AssessmentWindows.Count !=
                    activeResult.Estimate.AssessmentWindows.Count ||
                !reloaded.LastEstimate.AssessmentWindows.SequenceEqual(
                    activeResult.Estimate.AssessmentWindows) ||
                reloaded.DisplayCapacityUsd is not { } reloadedDisplayCapacity ||
                Math.Abs(reloadedDisplayCapacity - 12D) > 0.000_001D)
            {
                throw new InvalidOperationException("Passive quota persisted state self-test failed.");
            }

            ValidateLegacyAssessmentSchema(root);

            ValidateStableDisplayCapacityProjection();

            var disabled = service.Disable(account, epochStart.AddMinutes(21));
            AddValidationEvent(
                usage,
                epochStart.AddMinutes(22),
                99,
                100D,
                UsageEventSource.Natural,
                epochStart,
                epochStart.AddHours(5));
            var frozen = service.Analyze(account, usage, SyntheticCost, epochStart.AddMinutes(23));
            if (disabled.IsEnabled ||
                !frozen.IsRetainedEstimate ||
                frozen.StatusCode != "disabled" ||
                frozen.Estimate?.EstimatedTotalUsd is not { } frozenTotal ||
                Math.Abs(frozenTotal - 12D) > 0.000_001D)
            {
                throw new InvalidOperationException("Passive quota frozen-result self-test failed.");
            }

            var weeklyOnlyUsage = new AccountUsageSummary
            {
                AccountName = account.Name,
                RateLimitUsedPercent = 49D,
                RateLimitWindowMinutes = 10_080,
                RateLimitResetAtUtc = epochStart.AddDays(7),
                RateLimitObservedAtUtc = epochStart.AddMinutes(24)
            };
            var staleFrozen = service.Analyze(
                account,
                weeklyOnlyUsage,
                SyntheticCost,
                epochStart.AddMinutes(24));
            if (staleFrozen.IsRetainedEstimate ||
                staleFrozen.Estimate != null ||
                !staleFrozen.Message.Contains("窗口已经变化", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Passive quota stale-window self-test failed: an old 5h estimate must not be shown for a weekly-only window.");
            }

            var restarted = service.Enable(account, epochStart.AddMinutes(30));
            var collecting = service.Analyze(account, usage, SyntheticCost, epochStart.AddMinutes(31));
            if (!restarted.IsEnabled ||
                restarted.EpochId == enabled.EpochId ||
                restarted.LastEstimate != null ||
                collecting.Estimate?.Status != PassiveQuotaStatus.Collecting ||
                collecting.Estimate.SampleCount != 0)
            {
                throw new InvalidOperationException("Passive quota new-epoch isolation self-test failed.");
            }

            var weeklyEnabled = service.Enable(
                weeklyAccount,
                epochStart.AddMinutes(40),
                startingUsedPercent: 49D,
                startingWindowMinutes: 10_080);
            var weeklyReloaded = new PassiveQuotaMonitoringService(root).GetState(weeklyAccount);
            if (!weeklyEnabled.IsEnabled ||
                weeklyEnabled.StartingUsedPercent != 49 ||
                weeklyEnabled.StartingWindowMinutes != 10_080 ||
                weeklyReloaded.StartingUsedPercent != 49 ||
                weeklyReloaded.StartingWindowMinutes != 10_080)
            {
                throw new InvalidOperationException(
                    "Passive weekly quota start-window persistence self-test failed.");
            }

            var observedAccount = new AccountRecord
            {
                Name = "monitor-official-boundaries",
                CodexHome = System.IO.Path.Combine(root, "account-observed"),
                AuthKind = AccountAuthKind.AccessToken,
                QuotaLimitType = AccountQuotaLimitType.Monthly,
                QuotaPrimaryWindowMinutes = 43_800
            };
            var observedUsage = new AccountUsageSummary
            {
                AccountName = observedAccount.Name,
                RateLimitUsedPercent = 0D,
                RateLimitWindowMinutes = 43_800,
                RateLimitResetAtUtc = epochStart.AddDays(30),
                RateLimitObservedAtUtc = epochStart
            };
            service.Enable(
                observedAccount,
                epochStart,
                startingUsedPercent: 0D,
                startingWindowMinutes: 43_800);
            _ = service.Analyze(observedAccount, observedUsage, SyntheticCost, epochStart);
            observedUsage.Timeline.Add(new UsageEvent
            {
                AccountName = observedAccount.Name,
                TimestampUtc = epochStart.AddMinutes(1),
                Source = UsageEventSource.Natural,
                ActivationEpochUtc = epochStart,
                InputTokens = 2_100_000,
                TotalTokens = 2_100_000,
                Model = "synthetic"
            });
            observedUsage.RateLimitUsedPercent = 1D;
            observedUsage.RateLimitObservedAtUtc = epochStart.AddMinutes(2);
            _ = service.Analyze(observedAccount, observedUsage, SyntheticCost, epochStart.AddMinutes(2));
            observedUsage.Timeline.Add(new UsageEvent
            {
                AccountName = observedAccount.Name,
                TimestampUtc = epochStart.AddMinutes(3),
                Source = UsageEventSource.Natural,
                ActivationEpochUtc = epochStart,
                InputTokens = 4_200_000,
                TotalTokens = 4_200_000,
                Model = "synthetic"
            });
            observedUsage.RateLimitUsedPercent = 3D;
            observedUsage.RateLimitObservedAtUtc = epochStart.AddMinutes(4);
            var observedResult = service.Analyze(
                observedAccount,
                observedUsage,
                SyntheticCost,
                epochStart.AddMinutes(4));
            var observedReloaded = new PassiveQuotaMonitoringService(root).Analyze(
                observedAccount,
                observedUsage,
                SyntheticCost,
                epochStart.AddMinutes(5));
            if (observedResult.Estimate is not
                    { Status: PassiveQuotaStatus.Normal, EstimatedTotalUsd: { } observedTotal, CycleCount: 1 } ||
                Math.Abs(observedTotal - 210D) > 0.000_001D ||
                observedReloaded.Estimate is not
                    { Status: PassiveQuotaStatus.Normal, EstimatedTotalUsd: { } reloadedObservedTotal } ||
                Math.Abs(reloadedObservedTotal - 210D) > 0.000_001D)
            {
                throw new InvalidOperationException(
                    "Persisted official percentage boundaries must complete the 99% to 97% monitoring window.");
            }

            // A fresh official percentage must update the displayed remaining estimate
            // immediately, even when no new natural event has completed another 2%
            // calibration window.  The calibrated capacity intentionally remains stable
            // here: re-estimating it from a single integer-percent move would turn the
            // UI into a noisy readout of rounding error.
            observedUsage.RateLimitUsedPercent = 4D;
            observedUsage.RateLimitObservedAtUtc = epochStart.AddMinutes(6);
            var livePercentageResult = service.Analyze(
                observedAccount,
                observedUsage,
                SyntheticCost,
                epochStart.AddMinutes(6));
            if (livePercentageResult.Estimate is not
                    { EstimatedTotalUsd: { } liveTotal, EstimatedRemainingUsd: { } liveRemaining } ||
                Math.Abs(liveTotal - 210D) > 0.000_001D ||
                Math.Abs(liveRemaining - 201.6D) > 0.000_001D ||
                livePercentageResult.State.DisplayCapacityUsd is not { } displayCapacity ||
                Math.Abs(displayCapacity - 210D) > 0.000_001D ||
                ProjectDisplayedRemainingUsd(displayCapacity, 96D) is not { } projectedRemaining ||
                Math.Abs(projectedRemaining - 201.6D) > 0.000_001D)
            {
                throw new InvalidOperationException(
                    "A new official percentage must refresh remaining quota without changing an uncalibrated capacity.");
            }

            // Re-open the persisted monitor, append more natural cost and official
            // boundaries, then make sure the later windows are actually replayed.  This
            // protects the quota-list refresh path: it used to render an incoming official
            // percentage without invoking Analyze(), leaving LastEstimate frozen at the
            // last window that happened to be calculated while an account detail was open.
            // The exact capacity may legitimately change with the later real workload;
            // what matters here is that no persisted boundary is silently ignored.
            var completedWindowCount = livePercentageResult.Estimate.AssessmentWindows.Count;
            observedUsage.Timeline.Add(new UsageEvent
            {
                AccountName = observedAccount.Name,
                TimestampUtc = epochStart.AddMinutes(7),
                Source = UsageEventSource.Natural,
                ActivationEpochUtc = epochStart,
                InputTokens = 5_000_000,
                TotalTokens = 5_000_000,
                Model = "synthetic"
            });
            observedUsage.RateLimitUsedPercent = 5D;
            observedUsage.RateLimitObservedAtUtc = epochStart.AddMinutes(8);
            _ = new PassiveQuotaMonitoringService(root).Analyze(
                observedAccount,
                observedUsage,
                SyntheticCost,
                epochStart.AddMinutes(8));
            observedUsage.Timeline.Add(new UsageEvent
            {
                AccountName = observedAccount.Name,
                TimestampUtc = epochStart.AddMinutes(9),
                Source = UsageEventSource.Natural,
                ActivationEpochUtc = epochStart,
                InputTokens = 10_000_000,
                TotalTokens = 10_000_000,
                Model = "synthetic"
            });
            observedUsage.RateLimitUsedPercent = 7D;
            observedUsage.RateLimitObservedAtUtc = epochStart.AddMinutes(10);
            var appendedBoundaryResult = new PassiveQuotaMonitoringService(root).Analyze(
                observedAccount,
                observedUsage,
                SyntheticCost,
                epochStart.AddMinutes(10));
            var appendedReloaded = new PassiveQuotaMonitoringService(root).GetState(observedAccount);
            if (appendedBoundaryResult.Estimate is not { LatestUsedPercent: 7D } appendedEstimate ||
                appendedEstimate.AssessmentWindows.Count <= completedWindowCount ||
                !appendedEstimate.AssessmentWindows.Any(window =>
                    window.ThroughUtc == epochStart.AddMinutes(10) &&
                    window.ThroughUsedPercent == 7) ||
                appendedReloaded.LastEstimate is not { LatestUsedPercent: 7D } reloadedEstimate ||
                reloadedEstimate.AssessmentWindows.Count != appendedEstimate.AssessmentWindows.Count ||
                appendedReloaded.DisplayCapacityUsd is not { } reloadedCapacity ||
                appendedEstimate.EstimatedTotalUsd is not { } appendedCapacity ||
                Math.Abs(reloadedCapacity - appendedCapacity) > 0.000_001D)
            {
                throw new InvalidOperationException(
                    "Persisted official boundaries appended after an earlier estimate must refresh the latest assessment and display capacity.");
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
                // A locked synthetic cache must not hide the validation result.
            }
        }
    }

    private static void ValidateLegacyAssessmentSchema(string validationRoot)
    {
        var legacyRoot = System.IO.Path.Combine(validationRoot, "legacy-schema");
        Directory.CreateDirectory(legacyRoot);
        var account = new AccountRecord
        {
            Name = "legacy-monitor",
            CodexHome = System.IO.Path.Combine(legacyRoot, "account"),
            AuthKind = AccountAuthKind.AccessToken,
            QuotaLimitType = AccountQuotaLimitType.FiveHourOnly,
            QuotaPrimaryWindowMinutes = 300
        };
        var accountKeyJson = JsonSerializer.Serialize(QuotaAccountIdentity.CreateKey(account));
        var legacyJson = $$"""
        {
          "SchemaVersion": 5,
          "Accounts": [
            {
              "AccountKey": {{accountKeyJson}},
              "AccountName": "legacy-monitor",
              "IsEnabled": false,
              "LastEstimate": {
                "Status": 1,
                "QuotaKind": "five_hour",
                "WindowMinutes": 300,
                "ThresholdUsd": 10,
                "EstimatedTotalUsd": 12,
                "Reason": "legacy state without assessment windows"
              }
            }
          ]
        }
        """;
        File.WriteAllText(
            System.IO.Path.Combine(legacyRoot, FileName),
            legacyJson,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var reloaded = new PassiveQuotaMonitoringService(legacyRoot).GetState(account);
        if (reloaded.LastEstimate is not { EstimatedTotalUsd: 12D } legacyEstimate ||
            legacyEstimate.AssessmentWindows.Count != 0)
        {
            throw new InvalidOperationException(
                "Passive quota schema-5 state without assessment windows must remain readable.");
        }
    }

    private static AccountUsageSummary MakeValidationUsage(DateTimeOffset epochStart)
    {
        var usage = new AccountUsageSummary { AccountName = "monitor-a" };
        var resetAt = epochStart.AddHours(5);
        AddValidationEvent(
            usage,
            epochStart.AddMinutes(-1),
            1,
            50D,
            UsageEventSource.Natural,
            epochStart.AddHours(-1),
            resetAt);
        for (var offset = 0; offset <= 10; offset++)
        {
            AddValidationEvent(
                usage,
                epochStart.AddMinutes(offset + 1),
                10 + offset,
                0.12D,
                UsageEventSource.Natural,
                epochStart,
                resetAt);
            if (offset == 5)
            {
                AddValidationEvent(
                    usage,
                    epochStart.AddMinutes(offset + 1).AddSeconds(10),
                    90,
                    100D,
                    UsageEventSource.LegacyProbe,
                    epochStart,
                    resetAt);
            }
        }
        return usage;
    }

    private static void AddValidationEvent(
        AccountUsageSummary usage,
        DateTimeOffset timestamp,
        double usedPercent,
        double costUsd,
        UsageEventSource source,
        DateTimeOffset activationEpoch,
        DateTimeOffset resetAt)
    {
        var tokens = checked((long)Math.Round(costUsd * 1_000_000D, MidpointRounding.AwayFromZero));
        usage.Timeline.Add(new UsageEvent
        {
            TimestampUtc = timestamp,
            Source = source,
            ActivationEpochUtc = activationEpoch,
            InputTokens = tokens,
            TotalTokens = tokens,
            Model = "synthetic",
            RateLimitUsedPercent = usedPercent,
            RateLimitWindowMinutes = 300,
            RateLimitResetAtUtc = resetAt
        });
    }

    private sealed class MonitoringFile
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<StoredAccountState> Accounts { get; set; } = [];
    }

    private sealed class StoredAccountState
    {
        public string AccountKey { get; set; } = "";
        public string AccountName { get; set; } = "";
        public bool IsEnabled { get; set; }
        public string? EpochId { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? StoppedAtUtc { get; set; }
        public StoredEstimate? LastEstimate { get; set; }
        public DateTimeOffset? LastEstimateRecordedAtUtc { get; set; }
        public int? StartingUsedPercent { get; set; }
        public long? StartingWindowMinutes { get; set; }
        public double? DisplayCapacityUsd { get; set; }
        public string? DisplayQuotaKind { get; set; }
        public DateTimeOffset? DisplayResetAtUtc { get; set; }
        public List<StoredOfficialObservation> OfficialObservations { get; set; } = [];
    }

    private sealed record DisplayCapacityAnchor(
        double? CapacityUsd,
        string? QuotaKind,
        DateTimeOffset? ResetAtUtc);

    private sealed class StoredOfficialObservation
    {
        public DateTimeOffset TimestampUtc { get; set; }
        public int UsedPercent { get; set; }
        public long WindowMinutes { get; set; }
        public DateTimeOffset? ResetsAtUtc { get; set; }
    }

    private sealed record StoredAssessmentWindow
    {
        public DateTimeOffset FromUtc { get; init; }
        public DateTimeOffset ThroughUtc { get; init; }
        public int FromUsedPercent { get; init; }
        public int ThroughUsedPercent { get; init; }
        public double EstimatedTotalUsd { get; init; }
        public double ThresholdUsd { get; init; }
        public PassiveQuotaStatus Status { get; init; }

        public static StoredAssessmentWindow FromWindow(PassiveQuotaAssessmentWindow window) => new()
        {
            FromUtc = window.FromUtc,
            ThroughUtc = window.ThroughUtc,
            FromUsedPercent = window.FromUsedPercent,
            ThroughUsedPercent = window.ThroughUsedPercent,
            EstimatedTotalUsd = window.EstimatedTotalUsd,
            ThresholdUsd = window.ThresholdUsd,
            Status = window.Status
        };

        public PassiveQuotaAssessmentWindow ToWindow() => new(
            FromUtc,
            ThroughUtc,
            FromUsedPercent,
            ThroughUsedPercent,
            EstimatedTotalUsd,
            ThresholdUsd,
            Status);
    }

    private sealed record StoredEstimate
    {
        public PassiveQuotaStatus Status { get; init; }
        public string QuotaKind { get; init; } = "unknown";
        public long? WindowMinutes { get; init; }
        public double ThresholdUsd { get; init; }
        public double? EstimatedTotalUsd { get; init; }
        public double? EstimatedTotalLowerUsd { get; init; }
        public double? EstimatedTotalUpperUsd { get; init; }
        public double? EstimatedRemainingUsd { get; init; }
        public double? EstimatedRemainingLowerUsd { get; init; }
        public double? EstimatedRemainingUpperUsd { get; init; }
        public double? LatestUsedPercent { get; init; }
        public double? LatestRemainingPercent { get; init; }
        public double ObservedPercentSpan { get; init; }
        public int DistinctPercentLevels { get; init; }
        public int SampleCount { get; init; }
        public int CycleCount { get; init; }
        public int QualifiedCycleCount { get; init; }
        public DateTimeOffset? UpdatedAtUtc { get; init; }
        public string Reason { get; init; } = "";
        public List<StoredAssessmentWindow> AssessmentWindows { get; set; } = [];

        public static StoredEstimate FromEstimate(PassiveQuotaEstimate estimate) => new()
        {
            Status = estimate.Status,
            QuotaKind = estimate.QuotaKind,
            WindowMinutes = estimate.WindowMinutes,
            ThresholdUsd = estimate.ThresholdUsd,
            EstimatedTotalUsd = estimate.EstimatedTotalUsd,
            EstimatedTotalLowerUsd = estimate.EstimatedTotalLowerUsd,
            EstimatedTotalUpperUsd = estimate.EstimatedTotalUpperUsd,
            EstimatedRemainingUsd = estimate.EstimatedRemainingUsd,
            EstimatedRemainingLowerUsd = estimate.EstimatedRemainingLowerUsd,
            EstimatedRemainingUpperUsd = estimate.EstimatedRemainingUpperUsd,
            LatestUsedPercent = estimate.LatestUsedPercent,
            LatestRemainingPercent = estimate.LatestRemainingPercent,
            ObservedPercentSpan = estimate.ObservedPercentSpan,
            DistinctPercentLevels = estimate.DistinctPercentLevels,
            SampleCount = estimate.SampleCount,
            CycleCount = estimate.CycleCount,
            QualifiedCycleCount = estimate.QualifiedCycleCount,
            UpdatedAtUtc = estimate.UpdatedAtUtc,
            Reason = estimate.Reason,
            AssessmentWindows = estimate.AssessmentWindows
                .OrderBy(item => item.FromUtc)
                .ThenBy(item => item.ThroughUtc)
                .Select(StoredAssessmentWindow.FromWindow)
                .ToList()
        };

        public PassiveQuotaEstimate ToEstimate() => new(
            Status,
            QuotaKind,
            WindowMinutes,
            ThresholdUsd,
            EstimatedTotalUsd,
            EstimatedTotalLowerUsd,
            EstimatedTotalUpperUsd,
            EstimatedRemainingUsd,
            EstimatedRemainingLowerUsd,
            EstimatedRemainingUpperUsd,
            LatestUsedPercent,
            LatestRemainingPercent,
            ObservedPercentSpan,
            DistinctPercentLevels,
            SampleCount,
            CycleCount,
            QualifiedCycleCount,
            UpdatedAtUtc,
            Reason,
            [])
        {
            AssessmentWindows = (AssessmentWindows ?? [])
                .OrderBy(item => item.FromUtc)
                .ThenBy(item => item.ThroughUtc)
                .Select(item => item.ToWindow())
                .ToArray()
        };
    }
}
