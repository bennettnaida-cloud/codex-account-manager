using System.Globalization;
using System.Text;

namespace CodexAccountManager;

/// <summary>
/// Result states emitted by the passive quota analyzer. The analyzer never creates
/// model traffic: it only evaluates natural token_count events already present in
/// an <see cref="AccountUsageSummary"/>.
/// </summary>
public enum PassiveQuotaStatus
{
    Collecting,
    Normal,
    Abnormal,
    Indeterminate
}

public sealed record PassiveQuotaModelUsage(
    string Model,
    long TotalTokens,
    double ApiEquivalentCostUsd,
    int EventCount);

public sealed record PassiveQuotaTrendPoint(
    DateTimeOffset TimestampUtc,
    string AccountName,
    string QuotaKind,
    long? WindowMinutes,
    DateTimeOffset? ResetsAtUtc,
    double? UsedPercent,
    double? RemainingPercent,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens,
    double ApiEquivalentCostUsd,
    double CumulativeApiEquivalentCostUsd,
    string Source,
    string? Model,
    int EventCount)
{
    // Keep the model detail outside the positional record contract: the same immutable
    // trend rebuilt twice must remain value-equal even though its read-only list instance
    // is freshly allocated. UI refresh code compares this detail explicitly.
    public IReadOnlyList<PassiveQuotaModelUsage> ModelUsage { get; init; } = [];
    public long CacheWriteTokens { get; init; }
    public int CacheWriteKnownEvents { get; init; }
    public int CacheWriteUnknownEvents { get; init; }
}

public sealed record PassiveQuotaOfficialObservation(
    DateTimeOffset TimestampUtc,
    int UsedPercent,
    long WindowMinutes,
    DateTimeOffset? ResetsAtUtc,
    DateTimeOffset? ActivationEpochUtc);

/// <summary>
/// One completed, directly observed rolling measurement window.  These windows are
/// emitted by the same analyzer that produces the headline estimate, so the chart can
/// distinguish a genuinely low-capacity interval from an unrelated historical bucket.
/// </summary>
public sealed record PassiveQuotaAssessmentWindow(
    DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc,
    int FromUsedPercent,
    int ThroughUsedPercent,
    double EstimatedTotalUsd,
    double ThresholdUsd,
    PassiveQuotaStatus Status)
{
    public double FromRemainingPercent => 100D - FromUsedPercent;

    public double ThroughRemainingPercent => 100D - ThroughUsedPercent;
}

public sealed record PassiveQuotaEstimate(
    PassiveQuotaStatus Status,
    string QuotaKind,
    long? WindowMinutes,
    double ThresholdUsd,
    double? EstimatedTotalUsd,
    double? EstimatedTotalLowerUsd,
    double? EstimatedTotalUpperUsd,
    double? EstimatedRemainingUsd,
    double? EstimatedRemainingLowerUsd,
    double? EstimatedRemainingUpperUsd,
    double? LatestUsedPercent,
    double? LatestRemainingPercent,
    double ObservedPercentSpan,
    int DistinctPercentLevels,
    int SampleCount,
    int CycleCount,
    int QualifiedCycleCount,
    DateTimeOffset? UpdatedAtUtc,
    string Reason,
    IReadOnlyList<PassiveQuotaTrendPoint> Trend)
{
    public IReadOnlyList<PassiveQuotaAssessmentWindow> AssessmentWindows { get; init; } = [];

    public string StatusCode => Status switch
    {
        PassiveQuotaStatus.Normal => "normal",
        PassiveQuotaStatus.Abnormal => "abnormal",
        PassiveQuotaStatus.Indeterminate => "indeterminate",
        _ => "collecting"
    };
}

/// <summary>
/// Estimates an API-equivalent quota capacity from natural local usage and the
/// integer official percentage snapshots attached to those natural events.
/// This type deliberately depends only on already-parsed in-memory usage data.
/// </summary>
public static class PassiveQuotaMonitor
{
    private const int MinimumDistinctPercentLevels = 2;
    private const int MinimumNaturalSamples = 2;
    private const int MinimumObservedPercentSpan = 2;
    private const int RecentCompletedWindowLimit = 5;
    private const int MaximumAssessmentWindowCount = 256;
    private const double PricingUncertainty = 0.05D;
    private const double RecentRegimeRelativeTolerance = 0.25D;
    private const double RecentRegimeMinimumToleranceUsd = 1D;
    private const double MonthlySolThresholdUsd = 200D;
    private const double MonthlyTerraThresholdUsd = 100D;
    private const double MonthlyLunaThresholdUsd = 80D;
    private static readonly TimeSpan ResetTimestampTolerance = TimeSpan.FromMinutes(2);

    private sealed record QuotaProfile(
        string Kind,
        long MinimumWindowMinutes,
        long MaximumWindowMinutes,
        long NominalWindowMinutes,
        double ThresholdUsd,
        TimeSpan TrendLookback);

    private sealed record CalibrationSample(
        DateTimeOffset TimestampUtc,
        int UsedPercent,
        long WindowMinutes,
        DateTimeOffset? ResetsAtUtc,
        DateTimeOffset? ActivationEpochUtc,
        double CumulativeNaturalCostUsd);

    private sealed record EventQuotaWindow(
        double? UsedPercent,
        long WindowMinutes,
        DateTimeOffset? ResetsAtUtc);

    private sealed class CalibrationSegment
    {
        public List<CalibrationSample> Samples { get; } = [];
    }

    private sealed record RollingMeasurementSet(
        IReadOnlyList<CalibrationSegment> CompletedWindows,
        CalibrationSegment? ActiveWindow,
        bool HasStartingBoundary);

    private sealed record SegmentEstimate(
        double TotalUsd,
        double LowerUsd,
        double UpperUsd,
        int PercentSpan,
        int DistinctLevels,
        int SampleCount,
        DateTimeOffset FromUtc,
        DateTimeOffset ThroughUtc,
        int FromUsedPercent,
        int ThroughUsedPercent);

    private sealed record WeightedValue(double Value, double Weight);

    private sealed class TrendAccumulator
    {
        public required DateTimeOffset TimestampUtc { get; init; }
        public required string AccountName { get; init; }
        public required string QuotaKind { get; init; }
        public long? WindowMinutes { get; set; }
        public DateTimeOffset? ResetsAtUtc { get; set; }
        public double? UsedPercent { get; set; }
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public int CacheWriteKnownEvents { get; set; }
        public int CacheWriteUnknownEvents { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningOutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public double CostUsd { get; set; }
        public string? Source { get; set; }
        public string? Model { get; set; }
        public int EventCount { get; set; }
        public Dictionary<string, ModelTrendAccumulator> ModelUsage { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ModelTrendAccumulator
    {
        public required string Model { get; init; }
        public long TotalTokens { get; set; }
        public double CostUsd { get; set; }
        public int EventCount { get; set; }
    }

    private static readonly QuotaProfile FiveHourProfile = new(
        "five_hour",
        240,
        360,
        300,
        10D,
        TimeSpan.FromDays(7));

    private static readonly QuotaProfile WeeklyProfile = new(
        "weekly",
        9_000,
        11_000,
        10_080,
        90D,
        TimeSpan.FromDays(14));

    private static readonly QuotaProfile MonthlyProfile = new(
        "monthly",
        40_000,
        47_000,
        43_800,
        200D,
        TimeSpan.FromDays(35));
    private const double DisplayThresholdToleranceUsd = 0.005D;

    public static PassiveQuotaEstimate Analyze(
        AccountRecord account,
        AccountUsageSummary usage,
        Func<UsageEvent, double> estimateEventCostUsd,
        DateTimeOffset now)
    {
        return Analyze(
            account,
            usage,
            estimateEventCostUsd,
            now,
            naturalUsageNotBeforeUtc: null,
            startingUsedPercent: null,
            startingWindowMinutes: null,
            officialObservations: null);
    }

    public static PassiveQuotaEstimate Analyze(
        AccountRecord account,
        AccountUsageSummary usage,
        Func<UsageEvent, double> estimateEventCostUsd,
        DateTimeOffset now,
        DateTimeOffset? naturalUsageNotBeforeUtc,
        int? startingUsedPercent = null,
        long? startingWindowMinutes = null,
        IReadOnlyList<PassiveQuotaOfficialObservation>? officialObservations = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(estimateEventCostUsd);

        var epochStartUtc = naturalUsageNotBeforeUtc?.ToUniversalTime();
        var canonicalOfficialObservations =
            PassiveQuotaMonitoringService.NormalizeOfficialObservations(officialObservations);
        var observedBoundaryEvents = canonicalOfficialObservations
            .Select(item => new UsageEvent
            {
                AccountName = account.Name,
                TimestampUtc = item.TimestampUtc.ToUniversalTime(),
                Source = UsageEventSource.OfficialSnapshot,
                ActivationEpochUtc = item.ActivationEpochUtc?.ToUniversalTime(),
                RateLimitUsedPercent = item.UsedPercent,
                RateLimitWindowMinutes = item.WindowMinutes,
                RateLimitResetAtUtc = item.ResetsAtUtc?.ToUniversalTime()
            })
            .ToList();
        var persistedBoundaryEvents = new HashSet<UsageEvent>(
            observedBoundaryEvents,
            ReferenceEqualityComparer.Instance);
        var orderedEvents = usage.Timeline
            .Concat(observedBoundaryEvents)
            .Where(item => item.TimestampUtc > DateTimeOffset.MinValue &&
                           (!epochStartUtc.HasValue || item.TimestampUtc >= epochStartUtc.Value))
            .OrderBy(item => item.TimestampUtc)
            .ToList();
        var costCache = new Dictionary<UsageEvent, double>(ReferenceEqualityComparer.Instance);
        double CachedEventCost(UsageEvent item)
        {
            if (costCache.TryGetValue(item, out var cached))
            {
                return cached;
            }
            var calculated = SafeCost(item, estimateEventCostUsd);
            costCache[item] = calculated;
            return calculated;
        }

        var profile = ResolveProfile(account, usage, orderedEvents);
        if (profile == null)
        {
            return new PassiveQuotaEstimate(
                PassiveQuotaStatus.Collecting,
                "unknown",
                null,
                0D,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                0D,
                0,
                0,
                0,
                0,
                null,
                account.IsCompatibleApi
                    ? "兼容 API 账号没有套餐百分比窗口，无法进行套餐总额度推断。"
                    : "尚未识别到 5h、周或月额度窗口，正在等待自然用量日志。",
                BuildTrend(
                    account,
                    usage,
                    CachedEventCost,
                    Later(now.AddDays(-30), epochStartUtc),
                    TimeSpan.FromHours(1)));
        }

        var latestNaturalModel = ResolveLatestNaturalModel(orderedEvents);
        var latestModelThresholdUsd = ResolveStatusThresholdUsd(profile, latestNaturalModel);

        var trend = BuildTrend(
            account,
            usage,
            CachedEventCost,
            Later(now.ToUniversalTime() - profile.TrendLookback, epochStartUtc),
            profile.Kind == FiveHourProfile.Kind
                ? TimeSpan.FromMinutes(5)
                : profile.Kind == WeeklyProfile.Kind
                    ? TimeSpan.FromMinutes(15)
                    : TimeSpan.FromHours(1),
            officialObservations: canonicalOfficialObservations);
        var samples = new List<CalibrationSample>();
        var firstPersistedOfficialBoundaryUtc = canonicalOfficialObservations is { Count: > 0 }
            ? canonicalOfficialObservations.Min(item => item.TimestampUtc).ToUniversalTime()
            : (DateTimeOffset?)null;
        var cumulativeNaturalCostUsd = 0D;
        foreach (var item in orderedEvents)
        {
            if (item.Source == UsageEventSource.Natural)
            {
                cumulativeNaturalCostUsd += CachedEventCost(item);
            }
            var eventWindow = GetEventQuotaWindow(item, profile);
            var isPersistedBoundary = persistedBoundaryEvents.Contains(item);
            if (eventWindow != null &&
                MakeCalibrationSample(item, eventWindow, cumulativeNaturalCostUsd) is { } sample &&
                // Once the monitor has persisted its own official boundaries, prefer
                // those boundaries for the remainder of the epoch. Natural log entries
                // and raw OfficialSnapshot events still add their Token cost / remain in
                // history, but their interleaved activation epochs must not split the
                // persisted official 9 -> 10 -> 11 ... sequence and leave the visible
                // rolling progress stuck at an older completed window.  In particular,
                // raw snapshot rows can be duplicated by a separately opened Codex
                // session with a null or different activation epoch; after persistence,
                // only this monitor's canonical boundary row may calibrate capacity.
                (isPersistedBoundary ||
                 !firstPersistedOfficialBoundaryUtc.HasValue ||
                 item.TimestampUtc < firstPersistedOfficialBoundaryUtc.Value))
            {
                samples.Add(sample);
            }
        }

        if (epochStartUtc.HasValue &&
            startingUsedPercent.HasValue &&
            IsProfileWindow(startingWindowMinutes, profile))
        {
            var normalizedStartingPercent = Math.Clamp(startingUsedPercent.Value, 0, 100);
            var firstSample = samples.FirstOrDefault();
            if (firstSample == null || firstSample.UsedPercent >= normalizedStartingPercent)
            {
                samples.Insert(
                    0,
                    new CalibrationSample(
                        epochStartUtc.Value,
                        normalizedStartingPercent,
                        firstSample?.WindowMinutes ?? profile.NominalWindowMinutes,
                        firstSample?.ResetsAtUtc,
                        firstSample?.ActivationEpochUtc,
                        0D));
            }
        }

        var currentOfficialWindow = GetSummaryQuotaWindow(usage, profile);
        var currentOfficialUsedPercent = NormalizePercent(currentOfficialWindow?.UsedPercent);
        var currentOfficialObservedAtUtc = currentOfficialUsedPercent.HasValue
            ? usage.RateLimitObservedAtUtc?.ToUniversalTime()
            : null;

        if (samples.Count == 0)
        {
            return EmptyEstimate(
                profile,
                latestModelThresholdUsd,
                trend,
                "已识别额度类型，但自然用量日志中还没有可用的官方整数百分比样本。",
                currentOfficialUsedPercent,
                currentOfficialObservedAtUtc);
        }

        var rawSegments = BuildSegments(samples, profile);
        var rollingSegments = rawSegments
            .Select((segment, index) => BuildRollingMeasurementSet(
                segment,
                skipFirstBoundary: index == 0))
            .ToList();
        var latestRolling = rollingSegments.LastOrDefault();
        var rolling = new RollingMeasurementSet(
            rollingSegments
                .SelectMany(item => item.CompletedWindows)
                .ToList(),
            latestRolling?.ActiveWindow,
            latestRolling?.HasStartingBoundary ?? false);
        // Highest-reasoning models can move the official integer percentage by several
        // points in one response. Keep the real observed span for every window and combine
        // a small recent set instead of letting one fast/atypical response replace the whole
        // estimate. Five windows remains responsive while suppressing large single-task swings.
        var allSegmentEstimates = rolling.CompletedWindows
            .Select(TryEstimateSegment)
            .Where(item => item != null)
            .Cast<SegmentEstimate>()
            .ToList();
        var assessmentWindows = allSegmentEstimates
            .Select(item =>
            {
                var windowModel = ResolveDominantNaturalModel(
                    orderedEvents,
                    CachedEventCost,
                    item.FromUtc,
                    item.ThroughUtc) ?? latestNaturalModel;
                var windowThresholdUsd = ResolveStatusThresholdUsd(profile, windowModel);
                return new PassiveQuotaAssessmentWindow(
                    item.FromUtc,
                    item.ThroughUtc,
                    item.FromUsedPercent,
                    item.ThroughUsedPercent,
                    item.TotalUsd,
                    windowThresholdUsd,
                    ClassifyPointEstimateStatus(item.TotalUsd, windowThresholdUsd));
            })
            .TakeLast(MaximumAssessmentWindowCount)
            .ToArray();
        var segmentEstimates = allSegmentEstimates
            .TakeLast(RecentCompletedWindowLimit)
            .ToList();
        var latest = samples[^1];
        var latestUsedPercent = currentOfficialUsedPercent ?? latest.UsedPercent;
        var latestObservedAtUtc = currentOfficialObservedAtUtc is { } officialObserved &&
                                  officialObserved > latest.TimestampUtc
            ? officialObserved
            : latest.TimestampUtc;
        var distinctLevels = rolling.ActiveWindow?.Samples
            .Select(item => item.UsedPercent)
            .Distinct()
            .Count() ?? 0;
        var observedSpan = rolling.ActiveWindow == null
            ? 0
            : Math.Min(MinimumObservedPercentSpan, GetPercentSpan(rolling.ActiveWindow.Samples));

        if (segmentEstimates.Count == 0)
        {
            var hasCompletedRawWindow = rolling.CompletedWindows.Count > 0;
            return new PassiveQuotaEstimate(
                hasCompletedRawWindow ? PassiveQuotaStatus.Indeterminate : PassiveQuotaStatus.Collecting,
                profile.Kind,
                latest.WindowMinutes,
                latestModelThresholdUsd,
                null,
                null,
                null,
                null,
                null,
                null,
                latestUsedPercent,
                100D - latestUsedPercent,
                observedSpan,
                distinctLevels,
                samples.Count,
                rolling.CompletedWindows.Count,
                0,
                latestObservedAtUtc,
                hasCompletedRawWindow
                    ? "最近一个绝对 2 个百分点滑动窗口已完成，但本地 API 等值消耗不足或样本不一致，暂不判断正常或异常。"
                    : rolling.HasStartingBoundary
                        ? $"监测中：当前滑动窗口已跨越 {observedSpan}/2 个百分点；达到 2 后更新结果。"
                        : "正在等待官方整数百分比首次跳变；第一次跳变只用于锁定起测边界，不计入额度估算。",
                trend);
        }

        var robustSegments = SelectResponsiveRobustSegments(segmentEstimates);
        var totalUsd = RecencySmoothedValue(robustSegments, item => item.TotalUsd);
        var lowerUsd = RecencySmoothedValue(robustSegments, item => item.LowerUsd);
        var upperUsd = RecencySmoothedValue(robustSegments, item => item.UpperUsd);
        var statusModel = robustSegments.Count == 0
            ? latestNaturalModel
            : ResolveDominantNaturalModel(
                  orderedEvents,
                  CachedEventCost,
                  robustSegments[^1].FromUtc,
                  robustSegments[^1].ThroughUtc) ?? latestNaturalModel;
        var statusThresholdUsd = ResolveStatusThresholdUsd(profile, statusModel);

        // The official percentage is integer-quantized. Add a modest uncertainty
        // margin as well, because an API-equivalent dollar is not an official
        // cash balance.
        lowerUsd = Math.Max(0D, Math.Min(totalUsd, lowerUsd) * (1D - PricingUncertainty));
        upperUsd = Math.Max(totalUsd, upperUsd) * (1D + PricingUncertainty);
        var remainingFraction = Math.Clamp((100D - latestUsedPercent) / 100D, 0D, 1D);
        var remainingLowerFraction = Math.Clamp((100D - Math.Min(100D, latestUsedPercent + 1D)) / 100D, 0D, 1D);
        var remainingUpperFraction = Math.Clamp((100D - Math.Max(0D, latestUsedPercent - 1D)) / 100D, 0D, 1D);
        var remainingUsd = totalUsd * remainingFraction;
        var remainingLowerUsd = lowerUsd * remainingLowerFraction;
        var remainingUpperUsd = upperUsd * remainingUpperFraction;
        // The account-health badge and highlighted assessment windows deliberately use
        // a deterministic reference threshold against the displayed point estimate. Monthly
        // quotas follow the model represented by the latest accepted measurement window;
        // five-hour and weekly quotas retain their fixed profile thresholds.
        // The uncertainty interval remains available in the tooltip, but it must not
        // replace a deterministic threshold result with a status based on whether the
        // current official quota happens to be exhausted.
        var status = ClassifyPointEstimateStatus(totalUsd, statusThresholdUsd);
        var robustWindowLabel = robustSegments.Count == 1
            ? "最近 1 个有效窗口"
            : $"最近 {robustSegments.Count} 个有效窗口的平滑值";
        var reason = status switch
        {
            PassiveQuotaStatus.Normal =>
                $"{robustWindowLabel}不低于 {FormatUsd(statusThresholdUsd)}，推断额度正常。",
            PassiveQuotaStatus.Abnormal =>
                $"{robustWindowLabel}低于 {FormatUsd(statusThresholdUsd)}，推断额度异常。",
            _ =>
                $"{robustWindowLabel}跨过 {FormatUsd(statusThresholdUsd)} 阈值；监测会继续滚动更新。"
        };

        return new PassiveQuotaEstimate(
            status,
            profile.Kind,
            latest.WindowMinutes,
            statusThresholdUsd,
            totalUsd,
            lowerUsd,
            upperUsd,
            remainingUsd,
            remainingLowerUsd,
            remainingUpperUsd,
            latestUsedPercent,
            100D - latestUsedPercent,
            observedSpan,
            distinctLevels,
            samples.Count,
            rolling.CompletedWindows.Count,
            robustSegments.Count,
            latestObservedAtUtc,
            reason + " 所有金额均按每条事件实际模型的 sub2api 实际账单口径换算（基础价格档，不启用 >272K 长上下文加价），并非官方美元余额；同样 Token 数量下会保留 Sol、Terra、Luna 等模型的价格差异。",
            trend)
        {
            AssessmentWindows = assessmentWindows
        };
    }

    private static double ResolveStatusThresholdUsd(QuotaProfile profile, string? modelName)
    {
        if (profile.Kind != MonthlyProfile.Kind)
        {
            return profile.ThresholdUsd;
        }

        return NormalizeTrendModel(modelName) switch
        {
            "gpt-5.6-terra" => MonthlyTerraThresholdUsd,
            "gpt-5.6-luna" => MonthlyLunaThresholdUsd,
            "gpt-5.6-sol" => MonthlySolThresholdUsd,
            _ => profile.ThresholdUsd
        };
    }

    private static string? ResolveLatestNaturalModel(
        IReadOnlyList<UsageEvent> events,
        DateTimeOffset? throughUtc = null)
    {
        var normalizedThroughUtc = throughUtc?.ToUniversalTime();
        return events
            .Where(item =>
                item.Source == UsageEventSource.Natural &&
                !string.IsNullOrWhiteSpace(item.Model) &&
                (!normalizedThroughUtc.HasValue || item.TimestampUtc <= normalizedThroughUtc.Value))
            .OrderByDescending(item => item.TimestampUtc)
            .Select(item => item.Model!.Trim())
            .FirstOrDefault();
    }

    private static string? ResolveDominantNaturalModel(
        IReadOnlyList<UsageEvent> events,
        Func<UsageEvent, double> estimateEventCostUsd,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc)
    {
        var normalizedFromUtc = fromUtc.ToUniversalTime();
        var normalizedThroughUtc = throughUtc.ToUniversalTime();
        var dominant = events
            .Where(item =>
                item.Source == UsageEventSource.Natural &&
                item.TimestampUtc > normalizedFromUtc &&
                item.TimestampUtc <= normalizedThroughUtc &&
                !string.IsNullOrWhiteSpace(item.Model))
            .GroupBy(item => NormalizeTrendModel(item.Model), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Model = group.Key,
                CostUsd = group.Sum(item => SafeCost(item, estimateEventCostUsd)),
                EventCount = group.Count(),
                LatestTimestampUtc = group.Max(item => item.TimestampUtc)
            })
            .OrderByDescending(item => item.CostUsd)
            .ThenByDescending(item => item.EventCount)
            .ThenByDescending(item => item.LatestTimestampUtc)
            .FirstOrDefault();

        return dominant?.Model ?? ResolveLatestNaturalModel(events, normalizedThroughUtc);
    }

    private static PassiveQuotaStatus ClassifyCapacityRangeStatus(
        double lowerUsd,
        double upperUsd,
        double thresholdUsd)
    {
        if (!double.IsFinite(lowerUsd) ||
            !double.IsFinite(upperUsd) ||
            !double.IsFinite(thresholdUsd) ||
            lowerUsd < 0D ||
            upperUsd < lowerUsd ||
            thresholdUsd < 0D)
        {
            return PassiveQuotaStatus.Indeterminate;
        }

        return lowerUsd >= thresholdUsd
            ? PassiveQuotaStatus.Normal
            : upperUsd < thresholdUsd
                ? PassiveQuotaStatus.Abnormal
                : PassiveQuotaStatus.Indeterminate;
    }

    private static PassiveQuotaStatus ClassifyPointEstimateStatus(
        double estimatedTotalUsd,
        double thresholdUsd)
    {
        if (!double.IsFinite(estimatedTotalUsd) ||
            !double.IsFinite(thresholdUsd) ||
            estimatedTotalUsd < 0D ||
            thresholdUsd < 0D)
        {
            return PassiveQuotaStatus.Indeterminate;
        }

        // Estimates are presented to cents, so a sub-cent floating-point artifact at
        // exactly the configured threshold must not turn a normal capacity abnormal.
        return estimatedTotalUsd + DisplayThresholdToleranceUsd >= thresholdUsd
            ? PassiveQuotaStatus.Normal
            : PassiveQuotaStatus.Abnormal;
    }

    /// <summary>
    /// Deterministic synthetic validation. It performs no external operation and is
    /// safe to call from the application's self-test.
    /// </summary>
    public static void Validate()
    {
        var now = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;

        var weeklyNormal = Analyze(
            MakeSyntheticAccount("weekly-normal", AccountQuotaLimitType.FiveHourAndWeekly),
            MakeSyntheticUsage(now, 300, dollarsPerPercent: 0.20D, percentSpan: 10),
            SyntheticCost,
            now);
        var weeklyAbnormal = Analyze(
            MakeSyntheticAccount("weekly-abnormal", AccountQuotaLimitType.FiveHourAndWeekly),
            MakeSyntheticUsage(now, 300, dollarsPerPercent: 0.04D, percentSpan: 5),
            SyntheticCost,
            now);
        var weeklyWindowNormal = Analyze(
            MakeSyntheticAccount("weekly-window-normal", AccountQuotaLimitType.WeeklyOnly),
            MakeSyntheticUsage(now, 10_080, dollarsPerPercent: 1.60D, percentSpan: 10),
            SyntheticCost,
            now);
        var weeklyWindowAbnormal = Analyze(
            MakeSyntheticAccount("weekly-window-abnormal", AccountQuotaLimitType.WeeklyOnly),
            MakeSyntheticUsage(now, 10_080, dollarsPerPercent: 0.40D, percentSpan: 10),
            SyntheticCost,
            now);
        var weeklyWindowBoundary = Analyze(
            MakeSyntheticAccount("weekly-window-boundary", AccountQuotaLimitType.WeeklyOnly),
            MakeSyntheticUsage(now, 10_080, dollarsPerPercent: 0.90D, percentSpan: 10),
            SyntheticCost,
            now);
        var monthlyNormal = Analyze(
            MakeSyntheticAccount("monthly-normal", AccountQuotaLimitType.Monthly),
            MakeSyntheticUsage(now, 43_800, dollarsPerPercent: 3.40D, percentSpan: 10),
            SyntheticCost,
            now);
        var monthlyAbnormal = Analyze(
            MakeSyntheticAccount("monthly-abnormal", AccountQuotaLimitType.Monthly),
            MakeSyntheticUsage(now, 43_800, dollarsPerPercent: 0.80D, percentSpan: 20),
            SyntheticCost,
            now);
        var insufficient = Analyze(
            MakeSyntheticAccount("collecting", AccountQuotaLimitType.FiveHourAndWeekly),
            MakeSyntheticUsage(now, 300, dollarsPerPercent: 0.12D, percentSpan: 1),
            SyntheticCost,
            now);
        var thresholdCrossing = Analyze(
            MakeSyntheticAccount("indeterminate", AccountQuotaLimitType.FiveHourAndWeekly),
            MakeSyntheticUsage(now, 300, dollarsPerPercent: 0.10D, percentSpan: 10),
            SyntheticCost,
            now);
        var terraSwitchingRangeStatus = ClassifyCapacityRangeStatus(87.59D, 290.43D, 200D);

        if (weeklyNormal.Status != PassiveQuotaStatus.Normal ||
            weeklyNormal.EstimatedTotalUsd is not { } weeklyNormalTotal ||
            Math.Abs(weeklyNormalTotal - 20D) > 0.000_001D ||
            weeklyAbnormal.Status != PassiveQuotaStatus.Abnormal ||
            weeklyAbnormal.EstimatedTotalUsd is not { } weeklyAbnormalTotal ||
            Math.Abs(weeklyAbnormalTotal - 4D) > 0.000_001D ||
            weeklyWindowNormal.Status != PassiveQuotaStatus.Normal ||
            weeklyWindowNormal.ThresholdUsd != 90D ||
            weeklyWindowNormal.EstimatedTotalUsd is not { } weeklyWindowNormalTotal ||
            Math.Abs(weeklyWindowNormalTotal - 160D) > 0.000_001D ||
            weeklyWindowAbnormal.Status != PassiveQuotaStatus.Abnormal ||
            weeklyWindowAbnormal.ThresholdUsd != 90D ||
            weeklyWindowAbnormal.EstimatedTotalUsd is not { } weeklyWindowAbnormalTotal ||
            Math.Abs(weeklyWindowAbnormalTotal - 40D) > 0.000_001D ||
            weeklyWindowBoundary.Status != PassiveQuotaStatus.Normal ||
            weeklyWindowBoundary.EstimatedTotalUsd is not { } weeklyWindowBoundaryTotal ||
            Math.Abs(weeklyWindowBoundaryTotal - 90D) > 0.000_001D ||
            monthlyNormal.Status != PassiveQuotaStatus.Normal ||
            monthlyNormal.EstimatedTotalUsd is not { } monthlyNormalTotal ||
            Math.Abs(monthlyNormalTotal - 340D) > 0.000_001D ||
            monthlyAbnormal.Status != PassiveQuotaStatus.Abnormal ||
            monthlyAbnormal.EstimatedTotalUsd is not { } monthlyAbnormalTotal ||
            Math.Abs(monthlyAbnormalTotal - 80D) > 0.000_001D ||
            insufficient.Status != PassiveQuotaStatus.Collecting ||
            thresholdCrossing.Status != PassiveQuotaStatus.Normal ||
            thresholdCrossing.EstimatedTotalLowerUsd is not { } crossingLower ||
            thresholdCrossing.EstimatedTotalUpperUsd is not { } crossingUpper ||
            crossingLower >= thresholdCrossing.ThresholdUsd ||
            crossingUpper < thresholdCrossing.ThresholdUsd ||
            terraSwitchingRangeStatus != PassiveQuotaStatus.Indeterminate)
        {
            throw new InvalidOperationException(
                "Passive quota classification self-test failed: " +
                $"weeklyNormal={weeklyNormal.Status}/{weeklyNormal.EstimatedTotalUsd}, " +
                $"weeklyAbnormal={weeklyAbnormal.Status}/{weeklyAbnormal.EstimatedTotalUsd}, " +
                $"weeklyWindowNormal={weeklyWindowNormal.Status}/{weeklyWindowNormal.EstimatedTotalUsd}, " +
                $"weeklyWindowAbnormal={weeklyWindowAbnormal.Status}/{weeklyWindowAbnormal.EstimatedTotalUsd}, " +
                $"weeklyWindowBoundary={weeklyWindowBoundary.Status}/{weeklyWindowBoundary.EstimatedTotalUsd}, " +
                $"monthlyNormal={monthlyNormal.Status}/{monthlyNormal.EstimatedTotalUsd}, " +
                $"monthlyAbnormal={monthlyAbnormal.Status}/{monthlyAbnormal.EstimatedTotalUsd}, " +
                $"insufficient={insufficient.Status}, crossing={thresholdCrossing.Status}, " +
                $"terraSwitchingRange={terraSwitchingRangeStatus}.");
        }

        var csvAccount = MakeSyntheticAccount("comma,quote\"account", AccountQuotaLimitType.FiveHourAndWeekly);
        var csvUsage = MakeSyntheticUsage(now, 300, dollarsPerPercent: 0.12D, percentSpan: 3);
        var csvBytes = ExportCsv(BuildTrend(csvAccount, csvUsage, SyntheticCost, now.AddHours(-1)));
        var csvText = Encoding.UTF8.GetString(csvBytes);
        if (csvBytes.Length < 3 ||
            csvBytes[0] != 0xEF || csvBytes[1] != 0xBB || csvBytes[2] != 0xBF ||
            !csvText.Contains("\"comma,quote\"\"account\"", StringComparison.Ordinal) ||
            !csvText.Contains("cache_write_tokens", StringComparison.Ordinal) ||
            !csvText.Contains("\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Passive quota CSV BOM/RFC4180 self-test failed.");
        }

        ValidateRollingMeasurementWindows();
        ValidateEveryOnePercentRefreshesEstimate();
        ValidateRecentConsensusOverridesHistoricalRegime();
        ValidateMonthlyModelThresholds();
        ValidateOfficialModelEquivalentCapacity();
        ValidatePersistedOfficialBoundariesIgnoreInterleavedNaturalEpochs();
        ValidateHighVelocityMeasurementWindows();
        ValidateCompletedWindowsAcrossActivationSegments();
        ValidateBoundedTrend();
        ValidatePerModelTrendBreakdown();
        ValidateFlexibleQuotaWindows();
        ValidateOfficialWindowSequenceStability();

        PassiveQuotaMonitoringService.Validate();
    }

    private static void ValidateRollingMeasurementWindows()
    {
        var account = MakeSyntheticAccount("rolling-two-percent", AccountQuotaLimitType.FiveHourAndWeekly);
        var usage = new AccountUsageSummary { AccountName = account.Name };
        var startedAtUtc = new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero);
        var resetAtUtc = startedAtUtc.AddHours(5);
        static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;

        void AddBoundary(int minute, int usedPercent, double costUsd)
        {
            var tokens = checked((long)Math.Round(costUsd * 1_000_000D, MidpointRounding.AwayFromZero));
            usage.Timeline.Add(new UsageEvent
            {
                TimestampUtc = startedAtUtc.AddMinutes(minute),
                Source = UsageEventSource.Natural,
                ActivationEpochUtc = startedAtUtc,
                InputTokens = tokens,
                TotalTokens = tokens,
                Model = "synthetic",
                RateLimitUsedPercent = usedPercent,
                RateLimitWindowMinutes = 300,
                RateLimitResetAtUtc = resetAtUtc
            });
        }

        // The very expensive 10 -> 11 transition only establishes the starting
        // boundary and must not contaminate the first measured 11 -> 13 window.
        AddBoundary(-1, 10, 100D);
        AddBoundary(1, 11, 50D);
        AddBoundary(2, 12, 0.12D);
        AddBoundary(3, 13, 0.12D);
        var first = Analyze(account, usage, SyntheticCost, startedAtUtc.AddMinutes(3), startedAtUtc, 10, 300);

        // Adding the next one-percent boundary slides the window by one point:
        // 11 -> 13 is replaced by 12 -> 14, rather than starting at 13.
        AddBoundary(4, 14, 0.02D);
        var second = Analyze(account, usage, SyntheticCost, startedAtUtc.AddMinutes(4), startedAtUtc, 10, 300);
        AddBoundary(5, 15, 0.30D);
        var third = Analyze(account, usage, SyntheticCost, startedAtUtc.AddMinutes(5), startedAtUtc, 10, 300);

        if (first.Status != PassiveQuotaStatus.Normal ||
            first.EstimatedTotalUsd is not { } firstTotal ||
            Math.Abs(firstTotal - 12D) > 0.000_001D ||
            first.CycleCount != 1 ||
            first.AssessmentWindows is not
                [{ Status: PassiveQuotaStatus.Normal, EstimatedTotalUsd: { } firstWindowTotal }] ||
            Math.Abs(firstWindowTotal - 12D) > 0.000_001D ||
            first.AssessmentWindows[0].FromUsedPercent != 11 ||
            first.AssessmentWindows[0].ThroughUsedPercent != 13 ||
            second.Status != PassiveQuotaStatus.Abnormal ||
            second.EstimatedTotalUsd is not { } secondTotal ||
            Math.Abs(secondTotal - 9.5D) > 0.000_001D ||
            second.CycleCount != 2 ||
            second.AssessmentWindows.Count != 2 ||
            second.AssessmentWindows[1].Status != PassiveQuotaStatus.Abnormal ||
            Math.Abs(second.AssessmentWindows[1].EstimatedTotalUsd - 7D) > 0.000_001D ||
            second.AssessmentWindows[1].FromUtc != startedAtUtc.AddMinutes(2) ||
            second.AssessmentWindows[1].ThroughUtc != startedAtUtc.AddMinutes(4) ||
            third.Status != PassiveQuotaStatus.Normal ||
            third.EstimatedTotalUsd is not { } thirdTotal ||
            Math.Abs(thirdTotal - 12.75D) > 0.000_001D ||
            third.CycleCount != 3 ||
            third.AssessmentWindows.Count != 3 ||
            third.AssessmentWindows[2].Status != PassiveQuotaStatus.Normal ||
            Math.Abs(third.AssessmentWindows[2].EstimatedTotalUsd - 16D) > 0.000_001D)
        {
            throw new InvalidOperationException(
                "Passive quota rolling-window self-test failed: the first transition " +
                "must be excluded and each new one-percent boundary must slide the " +
                $"two-percent window. first={first.Status}/{first.EstimatedTotalUsd}/cycles={first.CycleCount}; " +
                $"second={second.Status}/{second.EstimatedTotalUsd}/cycles={second.CycleCount}; " +
                $"third={third.Status}/{third.EstimatedTotalUsd}/cycles={third.CycleCount}.");
        }
    }

    private static void ValidateEveryOnePercentRefreshesEstimate()
    {
        var segment = new CalibrationSegment();
        var start = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        var cumulativeCosts = new[] { 0D, 2D, 4D, 6D, 8D, 12D, 14.4D };
        for (var index = 0; index < cumulativeCosts.Length; index++)
        {
            segment.Samples.Add(new CalibrationSample(
                start.AddMinutes(index),
                index,
                43_800,
                start.AddDays(30),
                start,
                cumulativeCosts[index]));
        }

        var rolling = BuildRollingMeasurementSet(segment, skipFirstBoundary: false);
        var estimates = rolling.CompletedWindows
            .Select(TryEstimateSegment)
            .Where(item => item != null)
            .Cast<SegmentEstimate>()
            .ToList();
        var before = RecencySmoothedValue(estimates.Take(4).ToList(), item => item.TotalUsd);
        var after = RecencySmoothedValue(estimates.Take(5).ToList(), item => item.TotalUsd);
        if (estimates.Count != 5 ||
            Math.Abs(before - 250D) > 0.000_001D ||
            Math.Abs(after - 285D) > 0.000_001D ||
            Math.Abs(after - before) < 0.000_001D)
        {
            throw new InvalidOperationException(
                "Every new official one-percent boundary must visibly refresh the responsive capacity estimate.");
        }
    }

    private static void ValidateRecentConsensusOverridesHistoricalRegime()
    {
        var start = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        SegmentEstimate Segment(double value, int index) => new(
            value,
            value,
            value,
            2,
            3,
            3,
            start.AddMinutes(index),
            start.AddMinutes(index + 1),
            10 + index,
            12 + index);

        var singleNewValue = SelectResponsiveRobustSegments(
        [
            Segment(87D, 0),
            Segment(90D, 1),
            Segment(92D, 2),
            Segment(202D, 3)
        ]);
        var singleTotal = RecencySmoothedValue(singleNewValue, item => item.TotalUsd);

        var confirmedNewRegime = SelectResponsiveRobustSegments(
        [
            Segment(87D, 0),
            Segment(90D, 1),
            Segment(92D, 2),
            Segment(202D, 3),
            Segment(204D, 4)
        ]);
        var confirmedTotal = RecencySmoothedValue(confirmedNewRegime, item => item.TotalUsd);

        if (singleNewValue.Count != 3 ||
            Math.Abs(singleTotal - 90.25D) > 0.000_001D ||
            confirmedNewRegime.Count != 2 ||
            Math.Abs(confirmedTotal - 203D) > 0.000_001D)
        {
            throw new InvalidOperationException(
                "A single outlier must remain suppressed, while two agreeing recent " +
                "windows must establish a new quota-capacity regime.");
        }
    }

    private static void ValidatePersistedOfficialBoundariesIgnoreInterleavedNaturalEpochs()
    {
        var account = MakeSyntheticAccount(
            "persisted-official-boundaries",
            AccountQuotaLimitType.Monthly);
        var startedAtUtc = new DateTimeOffset(2026, 7, 13, 1, 0, 0, TimeSpan.Zero);
        var resetAtUtc = startedAtUtc.AddDays(30);
        var usage = new AccountUsageSummary
        {
            AccountName = account.Name,
            RateLimitUsedPercent = 14D,
            RateLimitWindowMinutes = 43_800,
            RateLimitResetAtUtc = resetAtUtc,
            RateLimitObservedAtUtc = startedAtUtc.AddMinutes(6)
        };
        static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;

        // Natural events keep contributing their monetary/Token cost, but their alternating
        // activation epochs must not split the persisted official 9 -> ... -> 14 sequence.
        // Six official boundaries create four completed overlapping 2% windows and leave
        // the next 13 -> 14 window visibly at 1/2%.
        for (var transition = 0; transition < 5; transition++)
        {
            var tokens = 2_400_000L;
            usage.Timeline.Add(new UsageEvent
            {
                AccountName = account.Name,
                TimestampUtc = startedAtUtc.AddMinutes(1 + transition).AddSeconds(30),
                Source = UsageEventSource.Natural,
                ActivationEpochUtc = startedAtUtc.AddHours(transition % 2),
                InputTokens = tokens,
                TotalTokens = tokens,
                Model = "synthetic-interleaved",
                RateLimitUsedPercent = 9 + transition,
                RateLimitWindowMinutes = 43_800,
                RateLimitResetAtUtc = resetAtUtc
            });
            // Session-log official snapshots are zero-cost duplicates, but may carry a
            // different activation epoch from the monitor-owned persisted boundary.
            // They must not fragment the canonical persisted sequence below.
            usage.Timeline.Add(new UsageEvent
            {
                AccountName = account.Name,
                TimestampUtc = startedAtUtc.AddMinutes(1 + transition).AddSeconds(15),
                Source = UsageEventSource.OfficialSnapshot,
                ActivationEpochUtc = startedAtUtc.AddHours(transition + 1),
                RateLimitUsedPercent = 9 + transition,
                RateLimitWindowMinutes = 43_800,
                RateLimitResetAtUtc = resetAtUtc
            });
        }

        var officialObservations = Enumerable.Range(0, 6)
            .Select(index => new PassiveQuotaOfficialObservation(
                startedAtUtc.AddMinutes(1 + index),
                9 + index,
                43_800,
                resetAtUtc,
                startedAtUtc))
            .ToList();
        var estimate = Analyze(
            account,
            usage,
            SyntheticCost,
            startedAtUtc.AddMinutes(6),
            startedAtUtc,
            startingUsedPercent: 0,
            startingWindowMinutes: 43_800,
            officialObservations);

        if (estimate.Status != PassiveQuotaStatus.Normal ||
            estimate.EstimatedTotalUsd is not { } totalUsd ||
            Math.Abs(totalUsd - 240D) > 0.000_001D ||
            estimate.ObservedPercentSpan != 1D ||
            estimate.CycleCount != 4 ||
            estimate.LatestUsedPercent != 14D ||
            estimate.LatestRemainingPercent != 86D)
        {
            throw new InvalidOperationException(
                "Persisted official quota boundaries must stay contiguous when natural " +
                "activation epochs interleave. " +
                $"status={estimate.Status}, total={estimate.EstimatedTotalUsd}, " +
                $"span={estimate.ObservedPercentSpan}, cycles={estimate.CycleCount}, " +
                $"used={estimate.LatestUsedPercent}, remaining={estimate.LatestRemainingPercent}.");
        }
    }

    private static void ValidateHighVelocityMeasurementWindows()
    {
        var account = MakeSyntheticAccount("high-velocity-five-hour", AccountQuotaLimitType.FiveHourAndWeekly);
        var usage = new AccountUsageSummary { AccountName = account.Name };
        var startedAtUtc = new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero);
        var resetAtUtc = startedAtUtc.AddHours(5);
        static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;

        void AddBoundary(int minute, int usedPercent, double costUsd)
        {
            var tokens = checked((long)Math.Round(costUsd * 1_000_000D, MidpointRounding.AwayFromZero));
            usage.Timeline.Add(new UsageEvent
            {
                TimestampUtc = startedAtUtc.AddMinutes(minute),
                Source = UsageEventSource.Natural,
                ActivationEpochUtc = startedAtUtc,
                InputTokens = tokens,
                TotalTokens = tokens,
                Model = "synthetic-high",
                RateLimitUsedPercent = usedPercent,
                RateLimitWindowMinutes = 300,
                RateLimitResetAtUtc = resetAtUtc
            });
        }

        // Each response crosses four official integer points. One response is a very
        // small outlier; the estimator must divide by the actual four-point span and
        // retain the stable recent capacity instead of treating every jump as exactly 2%.
        AddBoundary(0, 10, 0D);
        AddBoundary(1, 12, 0.24D);
        AddBoundary(2, 16, 0.48D);
        AddBoundary(3, 20, 0.48D);
        AddBoundary(4, 24, 0.04D);
        AddBoundary(5, 28, 0.48D);

        var estimate = Analyze(account, usage, SyntheticCost, startedAtUtc.AddMinutes(5));
        if (estimate.Status != PassiveQuotaStatus.Normal ||
            estimate.EstimatedTotalUsd is not { } totalUsd ||
            Math.Abs(totalUsd - 12D) > 0.000_001D ||
            estimate.CycleCount != 4 ||
            estimate.QualifiedCycleCount < 3)
        {
            throw new InvalidOperationException(
                "Passive quota high-velocity self-test failed: multi-point jumps must use " +
                $"their real span and recent robust windows. status={estimate.Status}, " +
                $"total={estimate.EstimatedTotalUsd}, cycles={estimate.CycleCount}, " +
                $"qualified={estimate.QualifiedCycleCount}.");
        }
    }

    private static void ValidateCompletedWindowsAcrossActivationSegments()
    {
        var account = MakeSyntheticAccount("multi-activation-monthly", AccountQuotaLimitType.Monthly);
        var usage = new AccountUsageSummary { AccountName = account.Name };
        var startedAtUtc = new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero);
        var firstActivation = startedAtUtc;
        var secondActivation = startedAtUtc.AddHours(1);
        var resetAtUtc = startedAtUtc.AddDays(30);
        static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;

        void AddBoundary(int minute, int usedPercent, double costUsd, DateTimeOffset activation)
        {
            var tokens = checked((long)Math.Round(costUsd * 1_000_000D, MidpointRounding.AwayFromZero));
            usage.Timeline.Add(new UsageEvent
            {
                TimestampUtc = startedAtUtc.AddMinutes(minute),
                Source = UsageEventSource.Natural,
                ActivationEpochUtc = activation,
                InputTokens = tokens,
                TotalTokens = tokens,
                Model = "synthetic-multi-activation",
                RateLimitUsedPercent = usedPercent,
                RateLimitWindowMinutes = 43_800,
                RateLimitResetAtUtc = resetAtUtc
            });
        }

        AddBoundary(0, 0, 0D, firstActivation);
        AddBoundary(1, 1, 2.10D, firstActivation);
        AddBoundary(2, 3, 4.20D, firstActivation);
        AddBoundary(60, 4, 0D, secondActivation);
        AddBoundary(61, 5, 1D, secondActivation);

        var estimate = Analyze(account, usage, SyntheticCost, startedAtUtc.AddMinutes(61));
        if (estimate.Status != PassiveQuotaStatus.Normal ||
            estimate.EstimatedTotalUsd is not { } totalUsd ||
            Math.Abs(totalUsd - 210D) > 0.000_001D ||
            estimate.CycleCount != 1)
        {
            throw new InvalidOperationException(
                "Completed passive quota windows must remain visible while a later account activation " +
                $"collects its next window. status={estimate.Status}, total={estimate.EstimatedTotalUsd}, " +
                $"cycles={estimate.CycleCount}.");
        }
    }

    private static void ValidateBoundedTrend()
    {
        var account = MakeSyntheticAccount("bounded-trend", AccountQuotaLimitType.FiveHourAndWeekly);
        var usage = new AccountUsageSummary { AccountName = account.Name };
        var fromUtc = new DateTimeOffset(2026, 7, 12, 11, 43, 23, TimeSpan.Zero);
        var throughUtc = fromUtc.AddMinutes(12);
        static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;

        void AddEvent(DateTimeOffset timestampUtc)
        {
            usage.Timeline.Add(new UsageEvent
            {
                TimestampUtc = timestampUtc,
                InputTokens = 1_000_000,
                TotalTokens = 1_000_000,
                Model = "synthetic"
            });
        }

        AddEvent(fromUtc.AddTicks(-1));
        AddEvent(fromUtc);
        AddEvent(fromUtc.AddMinutes(3));
        AddEvent(throughUtc);

        var frozenAtStop = BuildTrend(
            account,
            usage,
            SyntheticCost,
            fromUtc,
            TimeSpan.FromMinutes(10),
            throughUtc);

        // Natural history can continue growing after monitoring is stopped. A fixed
        // upper bound must keep the stopped monitoring epoch unchanged as "now" moves.
        var advancedNow = throughUtc.AddDays(1);
        AddEvent(advancedNow);
        var frozenAfterNowAdvanced = BuildTrend(
            account,
            usage,
            SyntheticCost,
            fromUtc,
            TimeSpan.FromMinutes(10),
            throughUtc);

        if (frozenAtStop.Count == 0 ||
            frozenAtStop[0].TimestampUtc != fromUtc ||
            frozenAtStop.Sum(item => item.EventCount) != 2 ||
            frozenAtStop.Sum(item => item.TotalTokens) != 2_000_000 ||
            Math.Abs(frozenAtStop.Sum(item => item.ApiEquivalentCostUsd) - 2D) > 0.000_001D ||
            !TrendPointsEqual(frozenAtStop, frozenAfterNowAdvanced))
        {
            throw new InvalidOperationException(
                "Passive quota bounded-trend self-test failed: events outside [from, through) " +
                "must be excluded, the first bucket must not predate from, and stopped trends " +
                "must remain frozen when now advances.");
        }
    }

    private static void ValidatePerModelTrendBreakdown()
    {
        var account = MakeSyntheticAccount("mixed-model-trend", AccountQuotaLimitType.FiveHourAndWeekly);
        var usage = new AccountUsageSummary { AccountName = account.Name };
        var now = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        usage.Timeline.Add(new UsageEvent
        {
            TimestampUtc = now.AddMinutes(-10),
            Source = UsageEventSource.Natural,
            Model = "gpt-5.6-sol",
            InputTokens = 1_000_000,
            TotalTokens = 1_000_000
        });
        usage.Timeline.Add(new UsageEvent
        {
            TimestampUtc = now.AddMinutes(-9),
            Source = UsageEventSource.Natural,
            Model = "gpt-5.6-terra",
            InputTokens = 2_000_000,
            TotalTokens = 2_000_000
        });
        usage.Timeline.Add(new UsageEvent
        {
            // The trend's upper boundary is exclusive; this event belongs to the next view.
            TimestampUtc = now,
            Source = UsageEventSource.Natural,
            Model = "gpt-5.6-luna",
            InputTokens = 4_000_000,
            TotalTokens = 4_000_000
        });
        static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;

        var trend = BuildTrend(
            account,
            usage,
            SyntheticCost,
            now.AddHours(-1),
            TimeSpan.FromMinutes(15),
            now);
        if (trend.Count != 1 ||
            trend[0].Model != "mixed" ||
            trend[0].ModelUsage.Count != 2 ||
            trend[0].ModelUsage.Sum(item => item.TotalTokens) != 3_000_000L ||
            Math.Abs(trend[0].ModelUsage.Sum(item => item.ApiEquivalentCostUsd) -
                     trend[0].ApiEquivalentCostUsd) > 0.000_001D ||
            !trend[0].ModelUsage.Any(item => item.Model == "gpt-5.6-sol") ||
            !trend[0].ModelUsage.Any(item => item.Model == "gpt-5.6-terra"))
        {
            throw new InvalidOperationException(
                "Passive quota per-model trend self-test failed: mixed model buckets must retain exact model costs.");
        }
    }

    private static bool TrendPointsEqual(
        IReadOnlyList<PassiveQuotaTrendPoint> left,
        IReadOnlyList<PassiveQuotaTrendPoint> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.TimestampUtc != second.TimestampUtc ||
                first.AccountName != second.AccountName ||
                first.QuotaKind != second.QuotaKind ||
                first.WindowMinutes != second.WindowMinutes ||
                first.ResetsAtUtc != second.ResetsAtUtc ||
                first.UsedPercent != second.UsedPercent ||
                first.RemainingPercent != second.RemainingPercent ||
                first.InputTokens != second.InputTokens ||
                first.CachedInputTokens != second.CachedInputTokens ||
                first.CacheWriteTokens != second.CacheWriteTokens ||
                first.CacheWriteKnownEvents != second.CacheWriteKnownEvents ||
                first.CacheWriteUnknownEvents != second.CacheWriteUnknownEvents ||
                first.OutputTokens != second.OutputTokens ||
                first.ReasoningOutputTokens != second.ReasoningOutputTokens ||
                first.TotalTokens != second.TotalTokens ||
                first.ApiEquivalentCostUsd != second.ApiEquivalentCostUsd ||
                first.CumulativeApiEquivalentCostUsd != second.CumulativeApiEquivalentCostUsd ||
                first.Source != second.Source ||
                first.Model != second.Model ||
                first.EventCount != second.EventCount ||
                !first.ModelUsage.SequenceEqual(second.ModelUsage))
            {
                return false;
            }
        }
        return true;
    }

    private static void ValidateFlexibleQuotaWindows()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;

        var staleDualAccount = MakeSyntheticAccount(
            "weekly-only-current",
            AccountQuotaLimitType.FiveHourAndWeekly);
        var weeklyOnlyUsage = new AccountUsageSummary
        {
            AccountName = staleDualAccount.Name,
            RateLimitUsedPercent = 49D,
            RateLimitWindowMinutes = 10_080,
            RateLimitResetAtUtc = now.AddDays(7),
            RateLimitObservedAtUtc = now
        };
        weeklyOnlyUsage.Timeline.Add(new UsageEvent
        {
            TimestampUtc = now,
            Source = UsageEventSource.Natural,
            InputTokens = 100_000,
            TotalTokens = 100_000,
            RateLimitUsedPercent = 49D,
            RateLimitWindowMinutes = 10_080,
            RateLimitResetAtUtc = now.AddDays(7)
        });
        var weeklyOnlyEstimate = Analyze(
            staleDualAccount,
            weeklyOnlyUsage,
            SyntheticCost,
            now.AddMinutes(1));
        var weeklyOnlyTrend = BuildTrend(
            staleDualAccount,
            weeklyOnlyUsage,
            SyntheticCost,
            now.AddMinutes(-1));

        var reversedUsage = new AccountUsageSummary
        {
            AccountName = "reversed-dual",
            RateLimitUsedPercent = 49D,
            RateLimitWindowMinutes = 10_080,
            RateLimitResetAtUtc = now.AddDays(7),
            SecondaryRateLimitUsedPercent = 20D,
            SecondaryRateLimitWindowMinutes = 300,
            SecondaryRateLimitResetAtUtc = now.AddHours(5),
            RateLimitObservedAtUtc = now
        };
        reversedUsage.Timeline.Add(new UsageEvent
        {
            TimestampUtc = now,
            Source = UsageEventSource.Natural,
            InputTokens = 100_000,
            TotalTokens = 100_000,
            RateLimitUsedPercent = 49D,
            RateLimitWindowMinutes = 10_080,
            RateLimitResetAtUtc = now.AddDays(7),
            SecondaryRateLimitUsedPercent = 20D,
            SecondaryRateLimitWindowMinutes = 300,
            SecondaryRateLimitResetAtUtc = now.AddHours(5)
        });
        var reversedTrend = BuildTrend(
            MakeSyntheticAccount("reversed-dual", AccountQuotaLimitType.FiveHourAndWeekly),
            reversedUsage,
            SyntheticCost,
            now.AddMinutes(-1));

        if (weeklyOnlyEstimate.Status != PassiveQuotaStatus.Collecting ||
            weeklyOnlyEstimate.EstimatedTotalUsd.HasValue ||
            weeklyOnlyEstimate.QuotaKind != "weekly" ||
            weeklyOnlyEstimate.ThresholdUsd != 90D ||
            weeklyOnlyTrend.Count != 1 ||
            weeklyOnlyTrend[0] is not { QuotaKind: "weekly", WindowMinutes: 10_080, RemainingPercent: 51D } ||
            reversedTrend.Count != 1 ||
            reversedTrend[0] is not { QuotaKind: "five_hour", WindowMinutes: 300, RemainingPercent: 80D })
        {
            throw new InvalidOperationException(
                "Passive quota flexible-window self-test failed: weekly-only windows must stay weekly, " +
                "and reversed dual windows must select 5h by duration rather than field position.");
        }
    }

    private static void ValidateOfficialWindowSequenceStability()
    {
        var start = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        var authoritativeReset = start.AddDays(31);
        var staleReset = start.AddDays(30);
        var observations = new[]
        {
            new PassiveQuotaOfficialObservation(start, 0, 43_800, authoritativeReset, start),
            new PassiveQuotaOfficialObservation(start.AddMinutes(1), 89, 43_800, staleReset, start),
            new PassiveQuotaOfficialObservation(start.AddMinutes(2), 0, 43_800, authoritativeReset, start),
            new PassiveQuotaOfficialObservation(start.AddMinutes(3), 89, 43_800, staleReset, start)
        };
        var normalized = PassiveQuotaMonitoringService.NormalizeOfficialObservations(observations);
        if (normalized.Count != 1 ||
            normalized[0].UsedPercent != 0 ||
            normalized[0].ResetsAtUtc != authoritativeReset)
        {
            throw new InvalidOperationException(
                "Official quota observations must reject an older reset cycle after a newer cycle is active.");
        }

        var rebased = PassiveQuotaMonitoringService.NormalizeOfficialObservations(
            observations,
            staleReset);
        if (rebased.Count != 1 ||
            rebased[0].UsedPercent != 89 ||
            rebased[0].ResetsAtUtc != staleReset)
        {
            throw new InvalidOperationException(
                "Official quota observations must be able to rebase to the current model-log cycle.");
        }

        var account = MakeSyntheticAccount("official-window-stability", AccountQuotaLimitType.Monthly);
        var usage = new AccountUsageSummary
        {
            AccountName = account.Name,
            RateLimitUsedPercent = 0D,
            RateLimitWindowMinutes = 43_800,
            RateLimitResetAtUtc = authoritativeReset,
            RateLimitObservedAtUtc = start.AddMinutes(2)
        };
        usage.Timeline.Add(new UsageEvent
        {
            AccountName = account.Name,
            TimestampUtc = start.AddMinutes(1),
            Source = UsageEventSource.Natural,
            InputTokens = 1_000_000,
            TotalTokens = 1_000_000,
            RateLimitUsedPercent = 89D,
            RateLimitWindowMinutes = 43_800,
            RateLimitResetAtUtc = staleReset
        });
        usage.Timeline.Add(new UsageEvent
        {
            AccountName = account.Name,
            TimestampUtc = start.AddMinutes(3),
            Source = UsageEventSource.Natural,
            InputTokens = 1_000_000,
            TotalTokens = 1_000_000,
            RateLimitUsedPercent = 89D,
            RateLimitWindowMinutes = 43_800,
            RateLimitResetAtUtc = staleReset
        });
        var trend = BuildTrend(
            account,
            usage,
            item => item.InputTokens / 1_000_000D,
            start,
            TimeSpan.FromMinutes(1),
            start.AddMinutes(5),
            observations);
        if (trend.Where(item => item.RemainingPercent.HasValue)
                .Any(item => item.RemainingPercent != 100D))
        {
            throw new InvalidOperationException(
                "A stale natural-log quota window must not overwrite the canonical official trend.");
        }
    }

    public static IReadOnlyList<PassiveQuotaTrendPoint> BuildTrend(
        AccountRecord account,
        AccountUsageSummary usage,
        Func<UsageEvent, double> estimateEventCostUsd,
        DateTimeOffset fromUtc,
        TimeSpan? bucketSize = null,
        DateTimeOffset? throughUtc = null,
        IReadOnlyList<PassiveQuotaOfficialObservation>? officialObservations = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(estimateEventCostUsd);

        var normalizedFromUtc = fromUtc.ToUniversalTime();
        var normalizedThroughUtc = throughUtc?.ToUniversalTime();
        var canonicalOfficialObservations =
            PassiveQuotaMonitoringService.NormalizeOfficialObservations(officialObservations);
        var officialEvents = canonicalOfficialObservations
            .Select(item => new UsageEvent
            {
                AccountName = account.Name,
                TimestampUtc = item.TimestampUtc.ToUniversalTime(),
                Source = UsageEventSource.OfficialSnapshot,
                ActivationEpochUtc = item.ActivationEpochUtc?.ToUniversalTime(),
                RateLimitUsedPercent = item.UsedPercent,
                RateLimitWindowMinutes = item.WindowMinutes,
                RateLimitResetAtUtc = item.ResetsAtUtc?.ToUniversalTime()
            })
            .ToList();
        var persistedOfficialEvents = new HashSet<UsageEvent>(
            officialEvents,
            ReferenceEqualityComparer.Instance);
        var allEvents = usage.Timeline
            .Concat(officialEvents)
            .OrderBy(item => item.TimestampUtc)
            .ToList();
        // Trend ranges are half-open: [from, through). Keeping the upper bound exclusive
        // prevents an event exactly on a bucket boundary from being counted in both the
        // previous view and the next one.
        var events = allEvents
            .Where(item => item.TimestampUtc >= normalizedFromUtc &&
                           (!normalizedThroughUtc.HasValue || item.TimestampUtc < normalizedThroughUtc.Value))
            .ToList();
        var profile = ResolveProfile(account, usage, allEvents);
        var trendWindowKind = profile == FiveHourProfile
            ? AccountQuotaWindowKind.FiveHour
            : profile == WeeklyProfile
                ? AccountQuotaWindowKind.Weekly
                : profile == MonthlyProfile
                ? AccountQuotaWindowKind.Monthly
                : AccountQuotaWindowKind.Unknown;
        var trendQuotaKind = trendWindowKind switch
        {
            AccountQuotaWindowKind.FiveHour => "five_hour",
            AccountQuotaWindowKind.Weekly => "weekly",
            AccountQuotaWindowKind.Monthly => "monthly",
            _ => "unknown"
        };
        var buckets = new SortedDictionary<long, TrendAccumulator>();
        var effectiveBucket = bucketSize is { Ticks: > 0 } value ? value : TimeSpan.Zero;

        foreach (var item in events)
        {
            var timestamp = item.TimestampUtc.ToUniversalTime();
            // Anchor buckets to the visible range, rather than to the Unix/UTC clock.
            // A rolling one-hour view with five-minute buckets consequently has exactly
            // twelve equal buckets even when it starts at (for example) 11:03.
            var bucketTicks = effectiveBucket == TimeSpan.Zero
                ? timestamp.UtcTicks
                : normalizedFromUtc.UtcTicks +
                  (((timestamp.UtcTicks - normalizedFromUtc.UtcTicks) / effectiveBucket.Ticks) *
                   effectiveBucket.Ticks);
            if (!buckets.TryGetValue(bucketTicks, out var accumulator))
            {
                accumulator = new TrendAccumulator
                {
                    TimestampUtc = new DateTimeOffset(bucketTicks, TimeSpan.Zero),
                    AccountName = account.Name,
                    QuotaKind = trendQuotaKind
                };
                buckets[bucketTicks] = accumulator;
            }

            // Official percentage snapshots are zero-cost calibration boundaries.  They supply
            // the green remaining-quota path but must never create a fake model record, token
            // count, or API-equivalent spend inside the usage series.
            if (item.Source == UsageEventSource.OfficialSnapshot)
            {
                accumulator.Source = MergeSource(accumulator.Source, GetSourceCode(item.Source));
            }
            else
            {
                accumulator.InputTokens += item.InputTokens;
                accumulator.CachedInputTokens += item.CachedInputTokens;
                var normalizedInput = Math.Max(0L, item.InputTokens);
                var normalizedCached = Math.Clamp(item.CachedInputTokens, 0L, normalizedInput);
                if (item.CacheWriteTokens is long cacheWrite &&
                    cacheWrite >= 0L &&
                    cacheWrite <= normalizedInput - normalizedCached)
                {
                    accumulator.CacheWriteTokens += cacheWrite;
                    accumulator.CacheWriteKnownEvents++;
                }
                else
                {
                    accumulator.CacheWriteUnknownEvents++;
                }
                accumulator.OutputTokens += item.OutputTokens;
                accumulator.ReasoningOutputTokens += item.ReasoningOutputTokens;
                accumulator.TotalTokens += item.TotalTokens;
                var eventCostUsd = SafeCost(item, estimateEventCostUsd);
                accumulator.CostUsd += eventCostUsd;
                accumulator.EventCount++;
                accumulator.Source = MergeSource(accumulator.Source, GetSourceCode(item.Source));
                accumulator.Model = MergeModel(accumulator.Model, item.Model);
                var modelKey = NormalizeTrendModel(item.Model);
                if (!accumulator.ModelUsage.TryGetValue(modelKey, out var modelUsage))
                {
                    modelUsage = new ModelTrendAccumulator { Model = modelKey };
                    accumulator.ModelUsage[modelKey] = modelUsage;
                }
                modelUsage.TotalTokens += Math.Max(0L, item.TotalTokens);
                modelUsage.CostUsd += eventCostUsd;
                modelUsage.EventCount++;
            }

            var eventWindow = GetEventQuotaWindow(item, trendWindowKind);
            if (!persistedOfficialEvents.Contains(item) &&
                canonicalOfficialObservations.Count > 0 &&
                item.TimestampUtc >= canonicalOfficialObservations[0].TimestampUtc &&
                eventWindow is { } rawWindow &&
                !canonicalOfficialObservations.Any(observation =>
                    Math.Abs(observation.WindowMinutes - rawWindow.WindowMinutes) <= 5 &&
                    SameResetCycle(observation.ResetsAtUtc, rawWindow.ResetsAtUtc)))
            {
                eventWindow = null;
            }
            if (eventWindow != null &&
                NormalizePercent(eventWindow.UsedPercent) is { } usedPercent)
            {
                accumulator.WindowMinutes = eventWindow.WindowMinutes;
                accumulator.ResetsAtUtc = eventWindow.ResetsAtUtc;
                accumulator.UsedPercent = usedPercent;
            }
        }

        var result = new List<PassiveQuotaTrendPoint>(buckets.Count);
        var cumulative = 0D;
        foreach (var bucket in buckets.Values)
        {
            cumulative += bucket.CostUsd;
            result.Add(new PassiveQuotaTrendPoint(
                bucket.TimestampUtc,
                bucket.AccountName,
                bucket.QuotaKind,
                bucket.WindowMinutes,
                bucket.ResetsAtUtc,
                bucket.UsedPercent,
                bucket.UsedPercent.HasValue ? 100D - bucket.UsedPercent.Value : null,
                bucket.InputTokens,
                bucket.CachedInputTokens,
                bucket.OutputTokens,
                bucket.ReasoningOutputTokens,
                bucket.TotalTokens,
                bucket.CostUsd,
                cumulative,
                bucket.Source ?? "natural",
                bucket.Model,
                bucket.EventCount)
            {
                CacheWriteTokens = bucket.CacheWriteTokens,
                CacheWriteKnownEvents = bucket.CacheWriteKnownEvents,
                CacheWriteUnknownEvents = bucket.CacheWriteUnknownEvents,
                ModelUsage = bucket.ModelUsage.Values
                    .OrderByDescending(item => item.CostUsd)
                    .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new PassiveQuotaModelUsage(
                        item.Model,
                        item.TotalTokens,
                        item.CostUsd,
                        item.EventCount))
                    .ToArray()
            });
        }

        return result;
    }

    /// <summary>
    /// Produces RFC 4180 CSV bytes with a UTF-8 BOM. The caller decides where to
    /// save the returned bytes, which keeps the monitor independent of UI and IO policy.
    /// </summary>
    public static byte[] ExportCsv(IEnumerable<PassiveQuotaTrendPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var builder = new StringBuilder();
        AppendCsvRow(
            builder,
            "timestamp_utc",
            "account",
            "quota_kind",
            "window_minutes",
            "reset_at_utc",
            "used_percent",
            "remaining_percent",
            "input_tokens",
            "cached_input_tokens",
            "cache_write_tokens",
            "cache_write_known_events",
            "cache_write_unknown_events",
            "output_tokens",
            "reasoning_output_tokens",
            "total_tokens",
            "api_equivalent_usd",
            "cumulative_api_equivalent_usd",
            "source",
            "model",
            "event_count");

        foreach (var point in points.OrderBy(item => item.TimestampUtc))
        {
            AppendCsvRow(
                builder,
                point.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                point.AccountName,
                point.QuotaKind,
                FormatNullable(point.WindowMinutes),
                point.ResetsAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "",
                FormatNullable(point.UsedPercent),
                FormatNullable(point.RemainingPercent),
                point.InputTokens.ToString(CultureInfo.InvariantCulture),
                point.CachedInputTokens.ToString(CultureInfo.InvariantCulture),
                point.CacheWriteTokens.ToString(CultureInfo.InvariantCulture),
                point.CacheWriteKnownEvents.ToString(CultureInfo.InvariantCulture),
                point.CacheWriteUnknownEvents.ToString(CultureInfo.InvariantCulture),
                point.OutputTokens.ToString(CultureInfo.InvariantCulture),
                point.ReasoningOutputTokens.ToString(CultureInfo.InvariantCulture),
                point.TotalTokens.ToString(CultureInfo.InvariantCulture),
                point.ApiEquivalentCostUsd.ToString("0.########", CultureInfo.InvariantCulture),
                point.CumulativeApiEquivalentCostUsd.ToString("0.########", CultureInfo.InvariantCulture),
                point.Source,
                point.Model ?? "",
                point.EventCount.ToString(CultureInfo.InvariantCulture));
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(builder.ToString());
        var output = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, output, preamble.Length, body.Length);
        return output;
    }

    private static PassiveQuotaEstimate EmptyEstimate(
        QuotaProfile profile,
        double thresholdUsd,
        IReadOnlyList<PassiveQuotaTrendPoint> trend,
        string reason,
        double? latestUsedPercent,
        DateTimeOffset? updatedAtUtc)
    {
        return new PassiveQuotaEstimate(
            PassiveQuotaStatus.Collecting,
            profile.Kind,
            profile.NominalWindowMinutes,
            thresholdUsd,
            null,
            null,
            null,
            null,
            null,
            null,
            latestUsedPercent,
            latestUsedPercent.HasValue ? 100D - latestUsedPercent.Value : null,
            0D,
            0,
            0,
            0,
            0,
            updatedAtUtc,
            reason,
            trend);
    }

    private static QuotaProfile? ResolveProfile(
        AccountRecord account,
        AccountUsageSummary usage,
        IReadOnlyList<UsageEvent> events)
    {
        if (account.IsCompatibleApi)
        {
            return null;
        }

        if (usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour) != null)
        {
            return FiveHourProfile;
        }
        if (usage.GetQuotaWindow(AccountQuotaWindowKind.Monthly) != null)
        {
            return MonthlyProfile;
        }
        if (usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly) != null)
        {
            return WeeklyProfile;
        }

        var latestEvent = events
            .Where(item =>
                AccountQuotaLimitType.ClassifyWindow(item.RateLimitWindowMinutes) != AccountQuotaWindowKind.Unknown ||
                AccountQuotaLimitType.ClassifyWindow(item.SecondaryRateLimitWindowMinutes) != AccountQuotaWindowKind.Unknown)
            .OrderByDescending(item => item.TimestampUtc)
            .FirstOrDefault();
        if (latestEvent != null)
        {
            if (GetEventQuotaWindow(latestEvent, AccountQuotaWindowKind.FiveHour) != null)
            {
                return FiveHourProfile;
            }
            if (GetEventQuotaWindow(latestEvent, AccountQuotaWindowKind.Monthly) != null)
            {
                return MonthlyProfile;
            }
            if (GetEventQuotaWindow(latestEvent, AccountQuotaWindowKind.Weekly) != null)
            {
                return WeeklyProfile;
            }
        }

        if (account.QuotaLimitType is AccountQuotaLimitType.FiveHourAndWeekly or
            AccountQuotaLimitType.FiveHourOnly)
        {
            return FiveHourProfile;
        }
        if (account.QuotaLimitType == AccountQuotaLimitType.WeeklyOnly)
        {
            return WeeklyProfile;
        }
        if (account.QuotaLimitType == AccountQuotaLimitType.Monthly)
        {
            return MonthlyProfile;
        }

        return null;
    }

    private static AccountQuotaWindowSnapshot? GetSummaryQuotaWindow(
        AccountUsageSummary usage,
        QuotaProfile profile)
    {
        return profile == FiveHourProfile
            ? usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour)
            : profile == WeeklyProfile
                ? usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly)
                : usage.GetQuotaWindow(AccountQuotaWindowKind.Monthly);
    }

    private static EventQuotaWindow? GetEventQuotaWindow(UsageEvent item, QuotaProfile profile)
    {
        return GetEventQuotaWindow(
            item,
            profile == FiveHourProfile
                ? AccountQuotaWindowKind.FiveHour
                : profile == WeeklyProfile
                    ? AccountQuotaWindowKind.Weekly
                    : AccountQuotaWindowKind.Monthly);
    }

    private static EventQuotaWindow? GetEventQuotaWindow(
        UsageEvent item,
        AccountQuotaWindowKind kind)
    {
        if (kind == AccountQuotaWindowKind.Unknown)
        {
            return null;
        }

        if (AccountQuotaLimitType.ClassifyWindow(item.RateLimitWindowMinutes) == kind &&
            item.RateLimitWindowMinutes.HasValue)
        {
            return new EventQuotaWindow(
                item.RateLimitUsedPercent,
                item.RateLimitWindowMinutes.Value,
                item.RateLimitResetAtUtc);
        }

        if (AccountQuotaLimitType.ClassifyWindow(item.SecondaryRateLimitWindowMinutes) == kind &&
            item.SecondaryRateLimitWindowMinutes.HasValue)
        {
            return new EventQuotaWindow(
                item.SecondaryRateLimitUsedPercent,
                item.SecondaryRateLimitWindowMinutes.Value,
                item.SecondaryRateLimitResetAtUtc);
        }

        return null;
    }

    private static CalibrationSample? MakeCalibrationSample(
        UsageEvent item,
        EventQuotaWindow window,
        double cumulativeNaturalCostUsd)
    {
        if (item.Source is not (UsageEventSource.Natural or UsageEventSource.OfficialSnapshot))
        {
            // Retired active-probe history remains visible in the local usage graph,
            // but is never allowed to influence a new passive capacity estimate.
            return null;
        }

        // OfficialSnapshot is a zero-cost, read-only calibration boundary captured from
        // the percentage already shown by Codex. Natural Token cost is accumulated before
        // the snapshot is converted, so it can close a 99% -> 97% window immediately
        // without sending a model request or attributing any cost to the snapshot itself.

        var percent = NormalizePercent(window.UsedPercent);
        if (!percent.HasValue)
        {
            // Historical retired entries have no official percentage. They stay in
            // the usage graph, but they can never become calibration anchors.
            return null;
        }

        return new CalibrationSample(
            item.TimestampUtc.ToUniversalTime(),
            percent.Value,
            window.WindowMinutes,
            window.ResetsAtUtc?.ToUniversalTime(),
            item.ActivationEpochUtc?.ToUniversalTime(),
            cumulativeNaturalCostUsd);
    }

    private static List<CalibrationSegment> BuildSegments(
        IReadOnlyList<CalibrationSample> samples,
        QuotaProfile profile)
    {
        var segments = new List<CalibrationSegment>();
        CalibrationSegment? current = null;
        CalibrationSample? previous = null;
        foreach (var sample in samples)
        {
            var sameActivationEpoch = previous == null ||
                                      sample.ActivationEpochUtc == previous.ActivationEpochUtc;
            var sameWindow = previous == null ||
                             Math.Abs(sample.WindowMinutes - previous.WindowMinutes) <= 5;
            var sameResetCycle = previous == null ||
                                 SameResetCycle(previous.ResetsAtUtc, sample.ResetsAtUtc);
            var withinWindowGap = previous == null ||
                                  sample.TimestampUtc - previous.TimestampUtc <=
                                  TimeSpan.FromMinutes(profile.MaximumWindowMinutes + 15);

            if (previous != null &&
                sameActivationEpoch &&
                sameWindow &&
                sameResetCycle &&
                withinWindowGap &&
                sample.UsedPercent < previous.UsedPercent)
            {
                // Parallel Codex sessions can flush an older integer snapshot after a
                // newer one. With the same activation/reset epoch this is stale ordering,
                // not evidence that capacity reset; omit it instead of fragmenting the fit.
                continue;
            }

            var startsNewSegment = previous == null ||
                                   !sameActivationEpoch ||
                                   !sameWindow ||
                                   !sameResetCycle ||
                                   !withinWindowGap;
            if (startsNewSegment)
            {
                current = new CalibrationSegment();
                segments.Add(current);
            }

            current!.Samples.Add(sample);
            previous = sample;
        }

        return segments.Where(segment => segment.Samples.Count > 0).ToList();
    }

    private static RollingMeasurementSet BuildRollingMeasurementSet(
        CalibrationSegment? rawSegment,
        bool skipFirstBoundary = true)
    {
        if (rawSegment == null || rawSegment.Samples.Count == 0)
        {
            return new RollingMeasurementSet([], null, false);
        }

        // Keep the first sample that reached each new integer used-percent boundary.
        // If monitoring starts at 44% remaining, the first 44 -> 43 transition only
        // establishes the 43% starting boundary; its cost is deliberately excluded.
        var boundaries = new List<CalibrationSample>();
        foreach (var sample in rawSegment.Samples)
        {
            if (boundaries.Count == 0 || sample.UsedPercent > boundaries[^1].UsedPercent)
            {
                boundaries.Add(sample);
            }
        }
        if (boundaries.Count < 2)
        {
            return new RollingMeasurementSet([], null, false);
        }

        // Only the first activation segment contains the synthetic monitoring-start
        // anchor, so only that segment discards its first transition. Later activation
        // segments already begin on a real official boundary; retaining that boundary
        // lets 91% -> 89% close a new 2% window after switching away and back.
        var measurementBoundaries = (skipFirstBoundary ? boundaries.Skip(1) : boundaries).ToList();
        var completed = new List<CalibrationSegment>();
        CalibrationSegment? active = null;
        for (var start = 0; start < measurementBoundaries.Count; start++)
        {
            var end = -1;
            for (var candidate = start + 1; candidate < measurementBoundaries.Count; candidate++)
            {
                if (measurementBoundaries[candidate].UsedPercent -
                    measurementBoundaries[start].UsedPercent >= MinimumObservedPercentSpan)
                {
                    end = candidate;
                    break;
                }
            }

            if (end >= 0)
            {
                var completedWindow = new CalibrationSegment();
                completedWindow.Samples.AddRange(
                    measurementBoundaries.Skip(start).Take(end - start + 1));
                completed.Add(completedWindow);
                continue;
            }

            // The earliest unfinished start is the next overlapping window. With
            // remaining-percent boundaries 43, 42, 41, 40 this yields 43 -> 41,
            // then 42 -> 40, so every new one-percent boundary refreshes the result.
            active = new CalibrationSegment();
            active.Samples.AddRange(measurementBoundaries.Skip(start));
            break;
        }

        return new RollingMeasurementSet(completed, active, true);
    }

    private static bool SameResetCycle(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }
        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }
        return (left.Value - right.Value).Duration() <= ResetTimestampTolerance;
    }

    private static SegmentEstimate? TryEstimateSegment(CalibrationSegment segment)
    {
        if (segment.Samples.Count < MinimumNaturalSamples)
        {
            return null;
        }

        var levels = segment.Samples.Select(item => item.UsedPercent).Distinct().Count();
        var span = GetPercentSpan(segment.Samples);
        if (levels < MinimumDistinctPercentLevels || span < MinimumObservedPercentSpan)
        {
            return null;
        }

        var pointValues = new List<WeightedValue>();
        var lowerValues = new List<WeightedValue>();
        var upperValues = new List<WeightedValue>();
        var boundarySpan = 0;
        for (var start = 0; start < segment.Samples.Count - 1; start++)
        {
            for (var end = start + 1; end < segment.Samples.Count; end++)
            {
                var percentDelta = segment.Samples[end].UsedPercent - segment.Samples[start].UsedPercent;
                if (percentDelta < MinimumObservedPercentSpan)
                {
                    continue;
                }

                var costDelta = segment.Samples[end].CumulativeNaturalCostUsd -
                                segment.Samples[start].CumulativeNaturalCostUsd;
                if (!double.IsFinite(costDelta) || costDelta <= 0D)
                {
                    continue;
                }

                var weight = percentDelta;
                pointValues.Add(new WeightedValue(100D * costDelta / percentDelta, weight));
                if (percentDelta > boundarySpan)
                {
                    boundarySpan = percentDelta;
                    lowerValues.Clear();
                    upperValues.Clear();
                }
                if (percentDelta == boundarySpan)
                {
                    lowerValues.Add(new WeightedValue(100D * costDelta / (percentDelta + 1D), weight));
                    upperValues.Add(new WeightedValue(100D * costDelta / (percentDelta - 1D), weight));
                }
            }
        }

        if (pointValues.Count == 0)
        {
            return null;
        }

        var point = WeightedMedian(pointValues);
        var lower = Math.Min(point, WeightedMedian(lowerValues));
        var upper = Math.Max(point, WeightedMedian(upperValues));
        var first = segment.Samples[0];
        var last = segment.Samples[^1];
        return new SegmentEstimate(
            point,
            lower,
            upper,
            span,
            levels,
            segment.Samples.Count,
            first.TimestampUtc,
            last.TimestampUtc,
            first.UsedPercent,
            last.UsedPercent);
    }

    private static AccountRecord MakeSyntheticAccount(string name, string quotaLimitType)
    {
        return new AccountRecord
        {
            Name = name,
            CodexHome = System.IO.Path.Combine("synthetic", name),
            AuthKind = AccountAuthKind.AccessToken,
            QuotaLimitType = quotaLimitType,
            QuotaPrimaryWindowMinutes = quotaLimitType switch
            {
                AccountQuotaLimitType.Monthly => 43_800,
                AccountQuotaLimitType.WeeklyOnly => 10_080,
                _ => 300
            },
            QuotaSecondaryWindowMinutes = quotaLimitType == AccountQuotaLimitType.FiveHourAndWeekly ? 10_080 : null
        };
    }

    private static AccountUsageSummary MakeSyntheticUsage(
        DateTimeOffset now,
        long windowMinutes,
        double dollarsPerPercent,
        int percentSpan)
    {
        var usage = new AccountUsageSummary { AccountName = "synthetic" };
        var firstTimestamp = now.AddMinutes(-(percentSpan + 1));
        var resetsAt = now.AddMinutes(windowMinutes);
        var eventCostTokens = checked((long)Math.Round(
            dollarsPerPercent * 1_000_000D,
            MidpointRounding.AwayFromZero));
        const int startingPercent = 10;
        for (var offset = 0; offset <= percentSpan; offset++)
        {
            usage.Timeline.Add(new UsageEvent
            {
                TimestampUtc = firstTimestamp.AddMinutes(offset),
                InputTokens = eventCostTokens,
                TotalTokens = eventCostTokens,
                Model = "synthetic",
                RateLimitUsedPercent = startingPercent + offset,
                RateLimitWindowMinutes = windowMinutes,
                RateLimitResetAtUtc = resetsAt
            });
        }
        return usage;
    }

    private static void ValidateMonthlyModelThresholds()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        static double SyntheticCost(UsageEvent item) => item.InputTokens / 1_000_000D;

        static AccountUsageSummary MakeModelUsage(
            DateTimeOffset now,
            string model,
            double dollarsPerPercent)
        {
            var usage = MakeSyntheticUsage(
                now,
                43_800,
                dollarsPerPercent,
                percentSpan: 10);
            foreach (var item in usage.Timeline)
            {
                item.Model = model;
            }
            return usage;
        }

        var sol = Analyze(
            MakeSyntheticAccount("monthly-sol-threshold", AccountQuotaLimitType.Monthly),
            MakeModelUsage(now, "gpt-5.6-sol", 1.50D),
            SyntheticCost,
            now);
        var terra = Analyze(
            MakeSyntheticAccount("monthly-terra-threshold", AccountQuotaLimitType.Monthly),
            MakeModelUsage(now, "gpt-5.6-terra", 1.50D),
            SyntheticCost,
            now);
        var luna = Analyze(
            MakeSyntheticAccount("monthly-luna-threshold", AccountQuotaLimitType.Monthly),
            MakeModelUsage(now, "gpt-5.6-luna", 0.90D),
            SyntheticCost,
            now);
        var lunaAbnormal = Analyze(
            MakeSyntheticAccount("monthly-luna-abnormal", AccountQuotaLimitType.Monthly),
            MakeModelUsage(now, "gpt-5.6-luna", 0.70D),
            SyntheticCost,
            now);

        if (sol.ThresholdUsd != MonthlySolThresholdUsd ||
            sol.Status != PassiveQuotaStatus.Abnormal ||
            terra.ThresholdUsd != MonthlyTerraThresholdUsd ||
            terra.Status != PassiveQuotaStatus.Normal ||
            luna.ThresholdUsd != MonthlyLunaThresholdUsd ||
            luna.Status != PassiveQuotaStatus.Normal ||
            lunaAbnormal.ThresholdUsd != MonthlyLunaThresholdUsd ||
            lunaAbnormal.Status != PassiveQuotaStatus.Abnormal ||
            sol.AssessmentWindows.Count == 0 ||
            terra.AssessmentWindows.Count == 0 ||
            luna.AssessmentWindows.Count == 0 ||
            lunaAbnormal.AssessmentWindows.Count == 0 ||
            sol.AssessmentWindows.Any(item => item.ThresholdUsd != MonthlySolThresholdUsd) ||
            terra.AssessmentWindows.Any(item => item.ThresholdUsd != MonthlyTerraThresholdUsd) ||
            luna.AssessmentWindows.Any(item => item.ThresholdUsd != MonthlyLunaThresholdUsd) ||
            lunaAbnormal.AssessmentWindows.Any(item => item.ThresholdUsd != MonthlyLunaThresholdUsd))
        {
            throw new InvalidOperationException(
                "Monthly passive quota thresholds must follow the model represented by each accepted window: " +
                $"sol={sol.Status}/{sol.ThresholdUsd}, terra={terra.Status}/{terra.ThresholdUsd}, " +
                $"luna={luna.Status}/{luna.ThresholdUsd}, lunaAbnormal={lunaAbnormal.Status}/{lunaAbnormal.ThresholdUsd}.");
        }
    }

    private static void ValidateOfficialModelEquivalentCapacity()
    {
        // This guards the user-facing invariant directly: when four models consume the
        // same official API-dollar value for every percentage step, the passive estimator
        // must reconstruct one and the same total capacity.  It covers normal input,
        // cached input, output, and long-context input separately.
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var models = new[]
        {
            "gpt-5.6-sol",
            "gpt-5.6-terra",
            "gpt-5.6-luna",
            "gpt-5.5"
        };

        double OfficialCost(UsageEvent item)
        {
            var isLongContext = item.InputTokens > UsageBucket.LongContextInputThreshold;
            var (inputRate, cachedInputRate, outputRate) = (item.Model, isLongContext) switch
            {
                ("gpt-5.6-sol", false) or ("gpt-5.5", false) => (5D, 0.5D, 30D),
                ("gpt-5.6-terra", false) => (2D, 0.2D, 12D),
                ("gpt-5.6-luna", false) => (0.2D, 0.02D, 1.2D),
                ("gpt-5.6-sol", true) or ("gpt-5.5", true) => (10D, 1D, 45D),
                ("gpt-5.6-terra", true) => (4D, 0.4D, 18D),
                ("gpt-5.6-luna", true) => (0.4D, 0.04D, 1.8D),
                _ => throw new InvalidOperationException("Equivalent-capacity fixture has an unknown model.")
            };
            var cachedInput = Math.Min(item.InputTokens, item.CachedInputTokens);
            return ((item.InputTokens - cachedInput) * inputRate +
                    (cachedInput * cachedInputRate) +
                    (item.OutputTokens * outputRate)) / 1_000_000D;
        }

        double EstimateCapacity(string model, long inputTokens, long cachedInputTokens, long outputTokens)
        {
            var usage = new AccountUsageSummary { AccountName = model };
            var resetAtUtc = now.AddDays(30);
            for (var step = 0; step <= 6; step++)
            {
                usage.Timeline.Add(new UsageEvent
                {
                    AccountName = model,
                    TimestampUtc = now.AddMinutes(-6 + step),
                    InputTokens = inputTokens,
                    CachedInputTokens = cachedInputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = inputTokens + outputTokens,
                    Model = model,
                    RateLimitUsedPercent = 10 + step,
                    RateLimitWindowMinutes = 43_800,
                    RateLimitResetAtUtc = resetAtUtc
                });
            }

            return Analyze(
                MakeSyntheticAccount(model, AccountQuotaLimitType.Monthly),
                usage,
                OfficialCost,
                now).EstimatedTotalUsd ?? double.NaN;
        }

        void AssertEquivalent(string label, double expectedTotalUsd, IReadOnlyList<double> values)
        {
            if (values.Count != models.Length ||
                values.Any(value => !double.IsFinite(value)) ||
                values.Any(value => Math.Abs(value - expectedTotalUsd) > 0.000_001D) ||
                values.Max() - values.Min() > 0.000_001D)
            {
                throw new InvalidOperationException(
                    $"Official model-equivalent capacity self-test failed for {label}: " +
                    string.Join(", ", models.Zip(values, (model, value) => $"{model}={value}")));
            }
        }

        AssertEquivalent(
            "short regular input",
            5D,
            [
                EstimateCapacity(models[0], 10_000, 0, 0),
                EstimateCapacity(models[1], 25_000, 0, 0),
                EstimateCapacity(models[2], 250_000, 0, 0),
                EstimateCapacity(models[3], 10_000, 0, 0)
            ]);
        AssertEquivalent(
            "short cached input",
            0.5D,
            [
                EstimateCapacity(models[0], 10_000, 10_000, 0),
                EstimateCapacity(models[1], 25_000, 25_000, 0),
                EstimateCapacity(models[2], 250_000, 250_000, 0),
                EstimateCapacity(models[3], 10_000, 10_000, 0)
            ]);
        AssertEquivalent(
            "short output",
            300D,
            [
                EstimateCapacity(models[0], 0, 0, 100_000),
                EstimateCapacity(models[1], 0, 0, 250_000),
                EstimateCapacity(models[2], 0, 0, 2_500_000),
                EstimateCapacity(models[3], 0, 0, 100_000)
            ]);
        AssertEquivalent(
            "long input",
            300D,
            [
                EstimateCapacity(models[0], 300_000, 0, 0),
                EstimateCapacity(models[1], 750_000, 0, 0),
                EstimateCapacity(models[2], 7_500_000, 0, 0),
                EstimateCapacity(models[3], 300_000, 0, 0)
            ]);
    }

    private static List<SegmentEstimate> RejectOutlierSegments(IReadOnlyList<SegmentEstimate> values)
    {
        if (values.Count < 3)
        {
            return values.ToList();
        }

        var median = Median(values.Select(item => item.TotalUsd));
        var mad = Median(values.Select(item => Math.Abs(item.TotalUsd - median)));
        if (!double.IsFinite(mad))
        {
            return values.ToList();
        }

        // Repeated equal windows make MAD exactly zero. Do not disable outlier
        // protection in that common case: retain a wide 75% band so legitimate
        // one-percent recalibration still moves the display, while a near-zero/high-
        // velocity anomaly cannot drag the responsive smoother below the threshold.
        var maximumDeviation = mad <= 0D
            ? Math.Max(1D, Math.Abs(median) * 0.75D)
            : Math.Max(3D * mad, Math.Max(1D, Math.Abs(median) * 0.20D));
        var filtered = values
            .Where(item => Math.Abs(item.TotalUsd - median) <= maximumDeviation)
            .ToList();
        return filtered.Count == 0 ? values.ToList() : filtered;
    }

    private static List<SegmentEstimate> SelectResponsiveRobustSegments(
        IReadOnlyList<SegmentEstimate> values)
    {
        var robust = RejectOutlierSegments(values);
        if (values.Count < 2)
        {
            return robust;
        }

        var recent = values.TakeLast(2).ToList();
        var scale = Math.Max(
            RecentRegimeMinimumToleranceUsd,
            recent.Max(item => Math.Abs(item.TotalUsd)));
        var agrees = recent.All(item => double.IsFinite(item.TotalUsd) && item.TotalUsd >= 0D) &&
                     Math.Abs(recent[0].TotalUsd - recent[1].TotalUsd) <=
                     scale * RecentRegimeRelativeTolerance;
        var newlyRejectedEvidence = recent.Any(item =>
            !robust.Any(candidate => ReferenceEquals(candidate, item)));

        // One unusual task stays suppressed. Two consecutive, mutually consistent
        // completed windows establish a new capacity regime so the headline cannot
        // remain anchored indefinitely to an older model/price mix.
        return agrees && newlyRejectedEvidence ? recent : robust;
    }

    private static double RecencySmoothedValue(
        IReadOnlyList<SegmentEstimate> values,
        Func<SegmentEstimate, double> selector)
    {
        var finite = values
            .Select(selector)
            .Where(value => double.IsFinite(value) && value >= 0D)
            .ToList();
        if (finite.Count == 0)
        {
            return 0D;
        }

        // A 50% exponential blend gives every newly completed overlapping 2% window
        // immediate visible influence. Because those windows advance at each official
        // integer-percent boundary, the display responds every 1% while the previous
        // windows still damp one unusually fast/high-reasoning turn.
        var smoothed = finite[0];
        for (var index = 1; index < finite.Count; index++)
        {
            smoothed = (smoothed + finite[index]) / 2D;
        }
        return smoothed;
    }

    private static int GetPercentSpan(IReadOnlyList<CalibrationSample> samples)
    {
        return samples.Count == 0
            ? 0
            : samples.Max(item => item.UsedPercent) - samples.Min(item => item.UsedPercent);
    }

    private static int? NormalizePercent(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value) || value.Value < 0D || value.Value > 100D)
        {
            return null;
        }

        // The app-server schema promises an integer percentage. Normalizing here keeps
        // local JSONL and read-only snapshots under the same one-percent uncertainty.
        return Math.Clamp(
            (int)Math.Round(value.Value, MidpointRounding.AwayFromZero),
            0,
            100);
    }

    private static bool IsProfileWindow(long? windowMinutes, QuotaProfile profile)
    {
        return windowMinutes is { } value &&
               value >= profile.MinimumWindowMinutes &&
               value <= profile.MaximumWindowMinutes;
    }

    private static double SafeCost(UsageEvent item, Func<UsageEvent, double> estimateEventCostUsd)
    {
        try
        {
            var value = estimateEventCostUsd(item);
            return double.IsFinite(value) && value > 0D ? value : 0D;
        }
        catch
        {
            // One unknown model must not break the local usage page or fabricate cost.
            return 0D;
        }
    }

    private static double WeightedMedian(IEnumerable<WeightedValue> values)
    {
        var ordered = values
            .Where(item => double.IsFinite(item.Value) && double.IsFinite(item.Weight) && item.Weight > 0D)
            .OrderBy(item => item.Value)
            .ToList();
        if (ordered.Count == 0)
        {
            return 0D;
        }

        var target = ordered.Sum(item => item.Weight) / 2D;
        var cumulative = 0D;
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            cumulative += item.Weight;
            if (Math.Abs(cumulative - target) <= 0.000_000_001D && index + 1 < ordered.Count)
            {
                return (item.Value + ordered[index + 1].Value) / 2D;
            }
            if (cumulative > target)
            {
                return item.Value;
            }
        }
        return ordered[^1].Value;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(double.IsFinite).OrderBy(item => item).ToList();
        if (ordered.Count == 0)
        {
            return 0D;
        }
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2D
            : ordered[middle];
    }

    private static string? MergeModel(string? current, string? next)
    {
        if (string.IsNullOrWhiteSpace(next))
        {
            return current;
        }
        if (string.IsNullOrWhiteSpace(current))
        {
            return next.Trim();
        }
        return current.Equals(next.Trim(), StringComparison.OrdinalIgnoreCase) ? current : "mixed";
    }

    private static string NormalizeTrendModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return "未识别模型";
        }

        var model = modelName.Trim();
        if (model.Contains("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase) ||
            model.Equals("gpt-5.6", StringComparison.OrdinalIgnoreCase))
        {
            return "gpt-5.6-sol";
        }
        if (model.Contains("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase))
        {
            return "gpt-5.6-terra";
        }
        if (model.Contains("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase))
        {
            return "gpt-5.6-luna";
        }
        if (model.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase))
        {
            return "gpt-5.5";
        }
        return model;
    }

    private static string GetSourceCode(UsageEventSource source) => source switch
    {
        UsageEventSource.OfficialSnapshot => "official_snapshot",
        UsageEventSource.LegacyProbe => "legacy_probe",
        _ => "natural"
    };

    private static string MergeSource(string? current, string next) =>
        string.IsNullOrWhiteSpace(current)
            ? next
            : current.Equals(next, StringComparison.Ordinal) ? current : "mixed";

    private static string FormatUsd(double value) =>
        "$" + value.ToString("#,0.00", CultureInfo.InvariantCulture);

    private static DateTimeOffset Later(DateTimeOffset candidate, DateTimeOffset? minimumUtc) =>
        minimumUtc.HasValue && minimumUtc.Value > candidate.ToUniversalTime()
            ? minimumUtc.Value
            : candidate.ToUniversalTime();

    private static string FormatNullable(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string FormatNullable(double? value) =>
        value?.ToString("0.########", CultureInfo.InvariantCulture) ?? "";

    private static void AppendCsvRow(StringBuilder builder, params string[] cells)
    {
        for (var index = 0; index < cells.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }
            builder.Append('"');
            builder.Append((cells[index] ?? "").Replace("\"", "\"\"", StringComparison.Ordinal));
            builder.Append('"');
        }
        builder.Append("\r\n");
    }
}
