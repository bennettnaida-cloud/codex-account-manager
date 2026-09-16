using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal enum PatAutoRotationState
{
    Idle,
    PendingQuotaExhaustion,
    WaitingForRequestBoundary,
    Switching,
    FallbackApi,
    Cooldown
}

internal enum PatRotationCandidateQuotaStatus
{
    Unknown,
    Available,
    Exhausted
}

internal sealed record PatAutoRotationLaunchContext(
    WindowsClientMode ClientMode,
    string? ChatGptFeatureAccountName,
    DateTimeOffset LaunchedAtUtc);

internal sealed record CodexTaskBoundarySnapshot(
    bool HasActiveTask,
    int InspectedSessionCount,
    DateTimeOffset? LatestSessionWriteAtUtc);

internal sealed record QuotaRotationDecision(
    bool ShouldRotate,
    bool IsImmediatelyExhausted,
    double? UsedPercent,
    double? RemainingPercent,
    double SafetyMarginPercent,
    double EstimatedRecentRequestPercent,
    string Reason);

internal sealed class QuotaSafetyMarginTracker
{
    private readonly Dictionary<string, Observation> _observations =
        new(StringComparer.Ordinal);

    internal double? Observe(
        string accountKey,
        UsageRateLimitWindow? window,
        long? completedModelRequests,
        DateTimeOffset observedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        if (window?.UsedPercent is not { } currentUsed || !double.IsFinite(currentUsed))
        {
            return _observations.TryGetValue(accountKey.Trim().ToUpperInvariant(), out var existing)
                ? existing.EstimatedRequestPercent
                : null;
        }

        var normalizedKey = accountKey.Trim().ToUpperInvariant();
        var normalizedUsed = Math.Clamp(currentUsed, 0D, 100D);
        var normalizedCount = completedModelRequests is >= 0
            ? completedModelRequests
            : null;
        double? estimate = null;
        if (_observations.TryGetValue(normalizedKey, out var previous) &&
            AreSameQuotaWindow(previous.ResetsAtUtc, window.ResetsAtUtc) &&
            normalizedUsed >= previous.UsedPercent)
        {
            var usedDelta = normalizedUsed - previous.UsedPercent;
            var requestDelta = normalizedCount.HasValue && previous.CompletedModelRequests.HasValue &&
                               normalizedCount.Value >= previous.CompletedModelRequests.Value
                ? normalizedCount.Value - previous.CompletedModelRequests.Value
                : 0L;
            if (usedDelta > 0D)
            {
                var observedPerRequest = requestDelta > 0
                    ? usedDelta / requestDelta
                    : usedDelta;
                observedPerRequest = Math.Clamp(observedPerRequest, 0.05D, 25D);
                estimate = previous.EstimatedRequestPercent.HasValue
                    ? Math.Max(observedPerRequest, previous.EstimatedRequestPercent.Value * 0.75D)
                    : observedPerRequest;
            }
            else
            {
                estimate = previous.EstimatedRequestPercent;
            }
        }

        _observations[normalizedKey] = new Observation(
            normalizedUsed,
            window.ResetsAtUtc?.ToUniversalTime(),
            normalizedCount,
            estimate,
            observedAtUtc.ToUniversalTime());
        return estimate;
    }

    internal void Reset(string accountKey)
    {
        if (!string.IsNullOrWhiteSpace(accountKey))
        {
            _observations.Remove(accountKey.Trim().ToUpperInvariant());
        }
    }

    private static bool AreSameQuotaWindow(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (!first.HasValue || !second.HasValue)
        {
            return first.HasValue == second.HasValue;
        }
        return (first.Value - second.Value).Duration() <= TimeSpan.FromMinutes(2);
    }

    private sealed record Observation(
        double UsedPercent,
        DateTimeOffset? ResetsAtUtc,
        long? CompletedModelRequests,
        double? EstimatedRequestPercent,
        DateTimeOffset ObservedAtUtc);
}

internal static class PatAutoRotationPolicy
{
    // Retained only for loading pre-2.2.4 settings. 2.2.8 never rotates early at 98%
    // or on a historical 429: only an official 100% observation is exhaustion here.
    internal const double DefaultUsedPercentThreshold = 98D;
    internal const int RequiredConsecutiveOfficialObservations = 2;
    internal static readonly TimeSpan MinimumOfficialObservationSpacing = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan GatewayQuietPeriod = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan QuotaLimitedSignalLifetime =
        PatGatewayQuotaSignalStore.SignalLifetime;
    internal const int RequiredConsecutiveSafeBoundaryChecks = 3;
    internal const double AssumedRecentRequestPercent = 1D;
    internal const double MinimumSafetyMarginPercent = 1.5D;
    internal const double MaximumSafetyMarginPercent = 12.5D;
    internal const double RecentRequestSafetyMultiplier = 1.5D;
    internal const double FixedMeasurementReservePercent = 0.5D;

    internal static UsageRateLimitWindow? SelectFiveHourWindow(UsageLimitResetInfo info)
    {
        if (AccountQuotaLimitType.ClassifyWindow(info.Primary?.WindowMinutes) ==
            AccountQuotaWindowKind.FiveHour)
        {
            return info.Primary;
        }
        if (AccountQuotaLimitType.ClassifyWindow(info.Secondary?.WindowMinutes) ==
            AccountQuotaWindowKind.FiveHour)
        {
            return info.Secondary;
        }
        return null;
    }

    internal static bool CanRetryCompatiblePrimary(
        DateTimeOffset resetAtUtc, PatGatewayQuotaSignal? exhaustion, DateTimeOffset now)
    {
        if (resetAtUtc != default && resetAtUtc + AccountRotationConfiguration.PrimaryResetGracePeriod > now)
            return false;
        if (exhaustion == null) return true;
        return exhaustion.ResetAtUtc is { } reset
            ? reset + AccountRotationConfiguration.PrimaryResetGracePeriod <= now
            : exhaustion.ObservedAtUtc + QuotaLimitedSignalLifetime <= now;
    }

    internal static bool HasLocallyAvailableFiveHourQuota(
        PersistedQuotaSnapshot snapshot,
        PatGatewayQuotaSignal? latestConfirmedExhaustion,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = nowUtc.ToUniversalTime();
        var window = new[] { snapshot.Primary, snapshot.Secondary }
            .FirstOrDefault(candidate =>
                AccountQuotaLimitType.ClassifyWindow(candidate?.WindowMinutes) ==
                AccountQuotaWindowKind.FiveHour);
        if (window == null)
        {
            return false;
        }

        if (latestConfirmedExhaustion?.ResetAtUtc is { } confirmedResetAtUtc &&
            confirmedResetAtUtc.ToUniversalTime() +
                AccountRotationConfiguration.PrimaryResetGracePeriod <= now)
        {
            return true;
        }
        if (window.ResetsAtUtc is { } resetAtUtc &&
            resetAtUtc.ToUniversalTime() +
                AccountRotationConfiguration.PrimaryResetGracePeriod <= now)
        {
            return true;
        }
        if (window.UsedPercent is not { } usedPercent ||
            !double.IsFinite(usedPercent) ||
            usedPercent >= 100D)
        {
            return false;
        }

        // A gateway-confirmed exhaustion observed after this local official snapshot wins.
        // An account-local snapshot taken later proves that a new/current window is usable.
        return latestConfirmedExhaustion == null ||
               latestConfirmedExhaustion.ObservedAtUtc.ToUniversalTime() <
               snapshot.ObservedAtUtc.ToUniversalTime();
    }

    internal static bool IsThresholdReached(
        UsageRateLimitWindow? window,
        double usedPercentThreshold,
        DateTimeOffset nowUtc)
    {
        _ = usedPercentThreshold;
        return EvaluateQuotaSafety(
            window,
            recentRequestUsedPercent: null,
            lastQuotaLimitedAtUtc: null,
            nowUtc).ShouldRotate;
    }

    internal static QuotaRotationDecision EvaluateQuotaSafety(
        UsageRateLimitWindow? window,
        double? recentRequestUsedPercent,
        DateTimeOffset? lastQuotaLimitedAtUtc,
        DateTimeOffset nowUtc)
    {
        var normalizedNow = nowUtc.ToUniversalTime();
        if (window?.ResetsAtUtc is { } resetAtUtc && resetAtUtc <= normalizedNow)
        {
            return new QuotaRotationDecision(
                false,
                false,
                window.UsedPercent,
                window.UsedPercent.HasValue ? Math.Max(0D, 100D - window.UsedPercent.Value) : null,
                0D,
                NormalizeRecentRequestEstimate(recentRequestUsedPercent),
                "reset-elapsed");
        }

        _ = lastQuotaLimitedAtUtc;
        var usedPercent = window?.UsedPercent is { } rawUsed && double.IsFinite(rawUsed)
            ? Math.Clamp(rawUsed, 0D, 100D)
            : (double?)null;
        var estimate = NormalizeRecentRequestEstimate(recentRequestUsedPercent);
        const double margin = 0D;
        var remaining = usedPercent.HasValue
            ? Math.Max(0D, 100D - usedPercent.Value)
            : (double?)null;

        if (usedPercent >= 100D)
        {
            return new QuotaRotationDecision(
                true,
                false,
                usedPercent,
                remaining,
                margin,
                estimate,
                "official-100-percent");
        }

        return new QuotaRotationDecision(
            false,
            false,
            usedPercent,
            remaining,
            margin,
            estimate,
            "available");
    }

    internal static DateTimeOffset? SelectQuotaLimitedSignal(
        LocalPatGatewayActivitySnapshot? snapshot,
        string accountKey)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(accountKey))
        {
            return null;
        }
        var normalizedKey = accountKey.Trim().ToUpperInvariant();
        return string.Equals(
            snapshot.LastQuotaLimitedAccountKey,
            normalizedKey,
            StringComparison.Ordinal)
            ? snapshot.LastQuotaLimitedAtUtc
            : null;
    }

    internal static long? SelectCompletedModelRequestCount(
        LocalPatGatewayActivitySnapshot? snapshot,
        string accountKey)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(accountKey))
        {
            return null;
        }
        var normalizedKey = accountKey.Trim().ToUpperInvariant();
        return string.Equals(
            snapshot.LastModelRequestAccountKey,
            normalizedKey,
            StringComparison.Ordinal)
            ? snapshot.CompletedModelRequests
            : null;
    }

    internal static double CalculateSafetyMargin(double? recentRequestUsedPercent)
    {
        var estimate = NormalizeRecentRequestEstimate(recentRequestUsedPercent);
        return Math.Clamp(
            (estimate * RecentRequestSafetyMultiplier) + FixedMeasurementReservePercent,
            MinimumSafetyMarginPercent,
            MaximumSafetyMarginPercent);
    }

    private static double NormalizeRecentRequestEstimate(double? value) =>
        value.HasValue && double.IsFinite(value.Value) && value.Value > 0D
            ? Math.Clamp(value.Value, 0.05D, 25D)
            : AssumedRecentRequestPercent;

    private static bool IsCurrentWindowQuotaLimitSignal(
        UsageRateLimitWindow? window,
        DateTimeOffset? lastQuotaLimitedAtUtc,
        DateTimeOffset nowUtc)
    {
        if (!lastQuotaLimitedAtUtc.HasValue || lastQuotaLimitedAtUtc.Value > nowUtc.AddSeconds(5))
        {
            return false;
        }

        var signal = lastQuotaLimitedAtUtc.Value.ToUniversalTime();
        if (window?.ResetsAtUtc is { } resetAtUtc && window.WindowMinutes is > 0)
        {
            var windowStartedAtUtc = resetAtUtc.ToUniversalTime() -
                                     TimeSpan.FromMinutes(window.WindowMinutes.Value);
            return signal >= windowStartedAtUtc;
        }
        return nowUtc - signal <= QuotaLimitedSignalLifetime;
    }

    internal static bool IsGatewayQuiet(
        LocalPatGatewayActivitySnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        if (snapshot.ActiveModelRequests != 0)
        {
            return false;
        }

        var latestActivity = new[]
            {
                snapshot.LastModelRequestStartedAtUtc,
                snapshot.LastModelRequestCompletedAtUtc
            }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        return latestActivity == DateTimeOffset.MinValue ||
               nowUtc - latestActivity >= GatewayQuietPeriod;
    }

    internal static IReadOnlyList<AccountRecord> BuildRotationCandidates(
        IReadOnlyList<AccountRecord> accounts,
        AccountRecord current,
        IReadOnlySet<string> unavailableAccountKeys,
        Func<AccountRecord, bool> hasUsableCredential,
        Func<AccountRecord, bool> hasAvailableFiveHourQuota)
    {
        if (accounts.Count == 0)
        {
            return [];
        }

        var currentIndex = accounts
            .Select((account, index) => (account, index))
            .FirstOrDefault(pair => pair.account.Name.Equals(
                current.Name,
                StringComparison.OrdinalIgnoreCase))
            .index;
        var ordered = Enumerable.Range(1, accounts.Count)
            .Select(offset => accounts[(currentIndex + offset) % accounts.Count])
            .ToList();
        var personalAccessTokens = ordered
            .Where(account =>
                account.IsAccessToken &&
                !account.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase) &&
                !unavailableAccountKeys.Contains(QuotaAccountIdentity.CreateKey(account)) &&
                hasUsableCredential(account) &&
                hasAvailableFiveHourQuota(account));
        var compatibleApis = ordered
            .Where(account =>
                account.IsCompatibleApi &&
                !unavailableAccountKeys.Contains(QuotaAccountIdentity.CreateKey(account)) &&
                hasUsableCredential(account));
        return personalAccessTokens.Concat(compatibleApis).ToList();
    }

    internal static void Validate()
    {
        var now = DateTimeOffset.UtcNow;
        var reversed = new UsageLimitResetInfo(
            null,
            [],
            new UsageRateLimitWindow(40, 10_080, now.AddDays(3)),
            new UsageRateLimitWindow(98, 300, now.AddHours(2)),
            null,
            null,
            null);
        var dynamicDecision = EvaluateQuotaSafety(
            new UsageRateLimitWindow(97, 300, now.AddHours(2)),
            recentRequestUsedPercent: 2D,
            lastQuotaLimitedAtUtc: null,
            now);
        var availableDecision = EvaluateQuotaSafety(
            new UsageRateLimitWindow(97, 300, now.AddHours(2)),
            recentRequestUsedPercent: 0.5D,
            lastQuotaLimitedAtUtc: null,
            now);
        var limitedDecision = EvaluateQuotaSafety(
            new UsageRateLimitWindow(20, 300, now.AddHours(2)),
            recentRequestUsedPercent: 0.5D,
            lastQuotaLimitedAtUtc: now.AddSeconds(-1),
            now);
        var retainedWindowDecision = EvaluateQuotaSafety(
            new UsageRateLimitWindow(20, 300, now.AddHours(1)),
            recentRequestUsedPercent: null,
            lastQuotaLimitedAtUtc: now.AddHours(-2),
            now);
        var retainedWithoutWindowDecision = EvaluateQuotaSafety(
            window: null,
            recentRequestUsedPercent: null,
            lastQuotaLimitedAtUtc: now.AddHours(-2),
            now);
        var exhaustedDecision = EvaluateQuotaSafety(
            new UsageRateLimitWindow(100, 300, now.AddHours(2)),
            recentRequestUsedPercent: 5D,
            lastQuotaLimitedAtUtc: null,
            now);
        var localSnapshotKey = new string('D', 64);
        var apiExhaustion = new PatGatewayQuotaSignal(1, localSnapshotKey, now);
        if (!CanRetryCompatiblePrimary(default, null, now) ||
            CanRetryCompatiblePrimary(default, apiExhaustion, now) ||
            !CanRetryCompatiblePrimary(default, apiExhaustion, now.AddMinutes(16)) ||
            CanRetryCompatiblePrimary(now.AddHours(1), null, now) ||
            CanRetryCompatiblePrimary(default, apiExhaustion with { ResetAtUtc = now.AddHours(2) }, now.AddMinutes(16)))
            throw new InvalidOperationException("API primary return must allow unknown quota but respect exhaustion cooldown/reset.");
        var localAvailableSnapshot = new PersistedQuotaSnapshot(
            localSnapshotKey,
            now,
            null,
            null,
            null,
            new UsageRateLimitWindow(0, 300, now.AddHours(5)),
            null,
            null,
            null,
            null);
        var newerConfirmedExhaustion = new PatGatewayQuotaSignal(
            1,
            localSnapshotKey,
            now.AddSeconds(1),
            now.AddHours(5));
        var elapsedLocalSnapshot = localAvailableSnapshot with
        {
            ObservedAtUtc = now.AddHours(-6),
            Primary = new UsageRateLimitWindow(100, 300, now.AddMinutes(-2))
        };
        if (SelectFiveHourWindow(reversed)?.UsedPercent != 98 ||
            IsThresholdReached(SelectFiveHourWindow(reversed), 98, now) ||
            !IsThresholdReached(
                new UsageRateLimitWindow(100, 300, now.AddHours(2)),
                98,
                now) ||
            IsThresholdReached(
                new UsageRateLimitWindow(100, 300, now.AddSeconds(-1)),
                98,
                now) ||
            dynamicDecision.ShouldRotate ||
            dynamicDecision.SafetyMarginPercent != 0D ||
            availableDecision.ShouldRotate ||
            limitedDecision.ShouldRotate ||
            limitedDecision.IsImmediatelyExhausted ||
            limitedDecision.Reason != "available" ||
            retainedWindowDecision.ShouldRotate ||
            retainedWindowDecision.IsImmediatelyExhausted ||
            retainedWindowDecision.Reason != "available" ||
            retainedWithoutWindowDecision.ShouldRotate ||
            !exhaustedDecision.ShouldRotate ||
            exhaustedDecision.IsImmediatelyExhausted ||
            exhaustedDecision.Reason != "official-100-percent" ||
            !HasLocallyAvailableFiveHourQuota(localAvailableSnapshot, null, now) ||
            HasLocallyAvailableFiveHourQuota(
                localAvailableSnapshot,
                newerConfirmedExhaustion,
                now) ||
            !HasLocallyAvailableFiveHourQuota(elapsedLocalSnapshot, null, now) ||
            IsGatewayQuiet(
                new LocalPatGatewayActivitySnapshot(1, now, null, null),
                now.AddSeconds(10)) ||
            !IsGatewayQuiet(
                new LocalPatGatewayActivitySnapshot(0, now.AddSeconds(-5), now.AddSeconds(-4), null),
                now))
        {
            throw new InvalidOperationException(
                "PAT automatic rotation quota-window or request-boundary policy failed.");
        }

        var tracker = new QuotaSafetyMarginTracker();
        var trackerKey = new string('A', 64);
        _ = tracker.Observe(
            trackerKey,
            new UsageRateLimitWindow(92, 300, now.AddHours(2)),
            completedModelRequests: 10,
            now.AddMinutes(-2));
        var estimate = tracker.Observe(
            trackerKey,
            new UsageRateLimitWindow(96, 300, now.AddHours(2)),
            completedModelRequests: 12,
            now);
        if (estimate != 2D)
        {
            throw new InvalidOperationException(
                "Dynamic quota safety margin did not estimate recent per-request consumption.");
        }

        var quotaAccountKey = new string('B', 64);
        var otherAccountKey = new string('C', 64);
        var activity = new LocalPatGatewayActivitySnapshot(
            0,
            now.AddSeconds(-3),
            now.AddSeconds(-2),
            now.AddSeconds(-1),
            Rotation: null,
            CompletedModelRequests: 4,
            LastModelRequestAccountKey: quotaAccountKey,
            LastQuotaLimitedAccountKey: quotaAccountKey);
        if (SelectQuotaLimitedSignal(activity, quotaAccountKey) != now.AddSeconds(-1) ||
            SelectQuotaLimitedSignal(activity, otherAccountKey).HasValue ||
            SelectCompletedModelRequestCount(activity, quotaAccountKey) != 4 ||
            SelectCompletedModelRequestCount(activity, otherAccountKey).HasValue)
        {
            throw new InvalidOperationException(
                "Gateway quota signals crossed logical account boundaries.");
        }

        var first = new AccountRecord { Name = "a", CodexHome = "a" };
        var second = new AccountRecord { Name = "b", CodexHome = "b" };
        var api = new AccountRecord
        {
            Name = "api",
            CodexHome = "api",
            AuthKind = AccountAuthKind.CompatibleApi
        };
        var candidates = BuildRotationCandidates(
            [first, second, api],
            first,
            new HashSet<string>(StringComparer.Ordinal),
            _ => true,
            _ => true);
        if (!candidates.Select(account => account.Name).SequenceEqual(["b", "api"]))
        {
            throw new InvalidOperationException(
                "PAT automatic rotation must prefer the next PAT and use compatible API last.");
        }
    }
}

internal sealed class CodexTaskBoundaryMonitor
{
    private const int TailByteLimit = 1024 * 1024;
    private static readonly TimeSpan PendingLookback = TimeSpan.FromSeconds(15);
    private readonly string _sessionsRoot;

    internal CodexTaskBoundaryMonitor(string codexHome)
    {
        _sessionsRoot = Path.Combine(Path.GetFullPath(codexHome), "sessions");
    }

    internal CodexTaskBoundarySnapshot Inspect(DateTimeOffset pendingSinceUtc)
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return new CodexTaskBoundarySnapshot(false, 0, null);
        }

        var thresholdUtc = pendingSinceUtc.UtcDateTime - PendingLookback;
        var candidates = Directory
            .EnumerateFiles(_sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.LastWriteTimeUtc >= thresholdUtc)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(128)
            .ToList();
        var hasActiveTask = false;
        foreach (var file in candidates)
        {
            var boundary = ReadLastTaskBoundary(file.FullName);
            if (boundary == TaskBoundary.Started ||
                (boundary == TaskBoundary.Unknown &&
                 file.LastWriteTimeUtc >= pendingSinceUtc.UtcDateTime))
            {
                hasActiveTask = true;
                break;
            }
        }

        return new CodexTaskBoundarySnapshot(
            hasActiveTask,
            candidates.Count,
            candidates.Count == 0
                ? null
                : new DateTimeOffset(candidates[0].LastWriteTimeUtc, TimeSpan.Zero));
    }

    internal static void Validate()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-task-boundary-test-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions", "fixture");
        var sessionPath = Path.Combine(sessions, "rollout.jsonl");
        try
        {
            Directory.CreateDirectory(sessions);
            File.WriteAllText(
                sessionPath,
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                new UTF8Encoding(false));
            var monitor = new CodexTaskBoundaryMonitor(root);
            var pendingSince = DateTimeOffset.UtcNow.AddSeconds(-1);
            if (!monitor.Inspect(pendingSince).HasActiveTask)
            {
                throw new InvalidOperationException(
                    "A started Codex task must block automatic account rotation.");
            }

            File.AppendAllText(
                sessionPath,
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                new UTF8Encoding(false));
            if (monitor.Inspect(pendingSince).HasActiveTask)
            {
                throw new InvalidOperationException(
                    "A completed Codex task must release the automatic rotation boundary.");
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

    private static TaskBoundary ReadLastTaskBoundary(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var tailLength = Math.Min((long)TailByteLimit, stream.Length);
            stream.Seek(-tailLength, SeekOrigin.End);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(false, false),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 16 * 1024,
                leaveOpen: false);
            if (tailLength < stream.Length)
            {
                _ = reader.ReadLine();
            }

            var latest = TaskBoundary.Unknown;
            while (reader.ReadLine() is { } line)
            {
                if (TryReadTaskBoundary(line) is { } boundary)
                {
                    latest = boundary;
                }
            }
            return latest;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            DecoderFallbackException)
        {
            // A file that is changing too quickly to inspect is conservatively considered
            // active. The next one-second boundary pass will retry it.
            return TaskBoundary.Started;
        }
    }

    private static TaskBoundary? TryReadTaskBoundary(string line)
    {
        if (!line.Contains("task_started", StringComparison.Ordinal) &&
            !line.Contains("task_complete", StringComparison.Ordinal) &&
            !line.Contains("turn_aborted", StringComparison.Ordinal))
        {
            return null;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var rootType) ||
            !rootType.ValueEquals("event_msg") ||
            !root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("type", out var payloadType))
        {
            return null;
        }

        return payloadType.GetString() switch
        {
            "task_started" => TaskBoundary.Started,
            "task_complete" => TaskBoundary.Completed,
            "turn_aborted" => TaskBoundary.Completed,
            _ => null
        };
    }

    private enum TaskBoundary
    {
        Unknown,
        Started,
        Completed
    }
}
