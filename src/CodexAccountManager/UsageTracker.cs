using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace CodexAccountManager;

public sealed class UsageTracker
{
    private const int PersistentUsageCacheSchemaVersion = 5;
    private const int PersistentCacheWriteIndexSchemaVersion = 2;
    private const int UsageReadBufferSize = 64 * 1024;
    private const int UsageTailFingerprintBytes = 96;
    private const double WebSearchEquivalentCostUsd = 0.01D;
    private static readonly TimeSpan SessionClockRollbackTolerance = TimeSpan.FromMinutes(5);
    // This cache accelerates the next launch; it is never the source of usage truth.  Writing
    // the complete event index after every live token_count update can otherwise serialize and
    // force-flush a multi-megabyte file several times per second.
    private static readonly TimeSpan PersistentUsageCachePersistInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PersistentCacheWriteIndexPersistInterval = TimeSpan.FromSeconds(10);
    // This SQLite log is several gigabytes on long-lived Codex installations. JSONL events
    // still refresh through the watcher promptly; this supplemental cache-write reconciliation
    // can safely lag a little without reopening the database every couple of seconds.
    private static readonly TimeSpan CacheWriteDatabaseRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly string _rootPath;
    private readonly string _managerScopeKey;
    private readonly object _usageFileCacheGate = new();
    private readonly Dictionary<string, CachedUsageFile> _usageFileCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ProbeUsageLedger _probeUsageLedger;
    private readonly Sub2ApiUsageLedger _sub2ApiUsageLedger;
    private readonly object _cacheWriteIndexGate = new();
    private readonly Dictionary<string, CacheWriteResponseRecord> _cacheWriteResponses =
        new(StringComparer.Ordinal);
    private bool _persistentUsageCacheLoaded;
    private bool _usageFileCacheDirty;
    private DateTimeOffset _lastUsageFileCachePersistAttemptUtc;
    private bool _persistentCacheWriteIndexLoaded;
    private string? _cacheWriteDatabasePath;
    private long _cacheWriteLastScannedLogId;
    private DateTimeOffset? _cacheWriteLoadedFromUtc;
    private bool _cacheWriteIndexNeedsNullableRescan;
    private bool _cacheWriteIndexDirty;
    private DateTimeOffset _lastCacheWriteIndexPersistAttemptUtc;
    private DateTimeOffset _lastCacheWriteDatabaseRefreshUtc;

    public UsageTracker(string rootPath)
    {
        _rootPath = rootPath;
        _managerScopeKey = QuotaAccountIdentity.CreateManagerScopeKey(rootPath);
        _probeUsageLedger = new ProbeUsageLedger(rootPath);
        _sub2ApiUsageLedger = new Sub2ApiUsageLedger(rootPath);
    }

    internal string ProbeUsagePath => _probeUsageLedger.Path;
    internal string Sub2ApiUsagePath => _sub2ApiUsageLedger.Path;
    internal string PersistentUsageCachePath =>
        Path.Combine(_rootPath, ".cache", "usage-file-index-v1.json");
    internal string PersistentCacheWriteIndexPath =>
        Path.Combine(_rootPath, ".cache", "cache-write-response-index-v1.json");

    public static void ValidateSubagentReplayFiltering()
    {
        if (AccountQuotaLimitType.Detect(43_800, null) != AccountQuotaLimitType.Monthly ||
            AccountQuotaLimitType.Detect(300, 10_080) != AccountQuotaLimitType.FiveHourAndWeekly ||
            AccountQuotaLimitType.Detect(10_080, 300) != AccountQuotaLimitType.FiveHourAndWeekly ||
            AccountQuotaLimitType.Detect(10_080, null) != AccountQuotaLimitType.WeeklyOnly ||
            AccountQuotaLimitType.Detect(null, 10_080) != AccountQuotaLimitType.WeeklyOnly ||
            AccountQuotaLimitType.Detect(300, null) != AccountQuotaLimitType.FiveHourOnly ||
            AccountQuotaLimitType.Detect(null, null) != AccountQuotaLimitType.Unknown)
        {
            throw new InvalidOperationException("Quota window type detection is not stable.");
        }

        var reversedWindows = new AccountUsageSummary
        {
            RateLimitUsedPercent = 49D,
            RateLimitWindowMinutes = 10_080,
            RateLimitResetAtUtc = DateTimeOffset.Parse("2026-07-20T12:00:00Z"),
            SecondaryRateLimitUsedPercent = 12D,
            SecondaryRateLimitWindowMinutes = 300,
            SecondaryRateLimitResetAtUtc = DateTimeOffset.Parse("2026-07-13T17:00:00Z")
        };
        var reversedFiveHour = reversedWindows.GetQuotaWindow(AccountQuotaWindowKind.FiveHour);
        var reversedWeekly = reversedWindows.GetQuotaWindow(AccountQuotaWindowKind.Weekly);
        if (reversedFiveHour is not { IsSecondary: true, RemainingPercent: 88D, WindowMinutes: 300 } ||
            reversedWeekly is not { IsSecondary: false, RemainingPercent: 51D, WindowMinutes: 10_080 })
        {
            throw new InvalidOperationException(
                "Quota window snapshots must be selected by duration rather than primary/secondary position.");
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-usage-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var sessionStartedAt = DateTimeOffset.Parse("2026-07-10T12:44:05.613Z");
            var subagentPath = Path.Combine(tempRoot, "subagent.jsonl");
            File.WriteAllLines(
                subagentPath,
                [
                    JsonSerializer.Serialize(new
                    {
                        timestamp = sessionStartedAt.ToString("O"),
                        type = "session_meta",
                        payload = new
                        {
                            id = "019f4c0e-7c6c-7663-9507-f142d09c558f",
                            forked_from_id = "019f4be7-aa6e-72b2-84bf-4e35b9c5f25f",
                            timestamp = sessionStartedAt.ToString("O"),
                            thread_source = "subagent",
                            source = new { subagent = new { } }
                        }
                    }),
                    JsonSerializer.Serialize(new
                    {
                        timestamp = sessionStartedAt.AddMilliseconds(5).ToString("O"),
                        type = "session_meta",
                        payload = new
                        {
                            id = "019f4be7-aa6e-72b2-84bf-4e35b9c5f25f",
                            timestamp = sessionStartedAt.AddMinutes(-40).ToString("O"),
                            thread_source = "app"
                        }
                    }),
                    MakeTokenCountFixture(sessionStartedAt.AddMilliseconds(10), 1_000, 99),
                    MakeTurnContextFixture(sessionStartedAt.AddMilliseconds(15), "gpt-5.6-sol"),
                    JsonSerializer.Serialize(new
                    {
                        timestamp = sessionStartedAt.AddMilliseconds(20).ToString("O"),
                        type = "inter_agent_communication_metadata",
                        payload = new { trigger_turn = true }
                    }),
                    MakeTokenCountFixture(sessionStartedAt.AddSeconds(5), 200, 7)
                ]);

            var subagentEvents = EnumerateUsageEventsFromFile(subagentPath, "fixture").ToList();
            if (subagentEvents.Count != 1 ||
                subagentEvents[0].TotalTokens != 200 ||
                subagentEvents[0].RateLimitUsedPercent != 7 ||
                subagentEvents[0].RateLimitWindowMinutes != 300 ||
                subagentEvents[0].SecondaryRateLimitUsedPercent != 8 ||
                subagentEvents[0].SecondaryRateLimitWindowMinutes != 10_080 ||
                subagentEvents[0].CreditBalance is not { HasCredits: true, Unlimited: false, Balance: "3.25" } ||
                subagentEvents[0].IndividualLimit is not { Limit: "20.00", Used: "4.00", RemainingPercent: 80 } ||
                subagentEvents[0].PlanType != "business" ||
                !string.Equals(subagentEvents[0].Model, "gpt-5.6-sol", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Subagent usage replay filter must discard inherited history and keep live usage/model metadata.");
            }

            var rootPath = Path.Combine(tempRoot, "root.jsonl");
            File.WriteAllLines(
                rootPath,
                [
                    JsonSerializer.Serialize(new
                    {
                        timestamp = sessionStartedAt.ToString("O"),
                        type = "session_meta",
                        payload = new
                        {
                            id = "019f4c0e-7c6c-7663-9507-f142d09c5580",
                            timestamp = sessionStartedAt.ToString("O"),
                            thread_source = "cli"
                        }
                    }),
                    MakeTurnContextFixture(sessionStartedAt.AddMilliseconds(10), "gpt-5.6-terra"),
                    MakeTokenCountFixture(sessionStartedAt.AddSeconds(1), 300, 12)
                ]);

            var rootEvents = EnumerateUsageEventsFromFile(rootPath, "fixture").ToList();
            if (rootEvents.Count != 1 ||
                rootEvents[0].TotalTokens != 300 ||
                !string.Equals(rootEvents[0].Model, "gpt-5.6-terra", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Regular usage logs must keep token_count events and their active model metadata.");
            }

            var searchRootPath = Path.Combine(tempRoot, "web-search-root.jsonl");
            File.WriteAllLines(
                searchRootPath,
                [
                    JsonSerializer.Serialize(new
                    {
                        timestamp = sessionStartedAt.ToString("O"),
                        type = "session_meta",
                        payload = new
                        {
                            id = "019f4c0e-7c6c-7663-9507-f142d09c5590",
                            timestamp = sessionStartedAt.ToString("O"),
                            thread_source = "app"
                        }
                    }),
                    MakeTurnContextFixture(sessionStartedAt.AddMilliseconds(10), "gpt-5.6-sol"),
                    MakeWebSearchEndFixture(
                        sessionStartedAt.AddSeconds(1),
                        "exec-replayed-search",
                        "open_page"),
                    MakeWebSearchEndFixture(
                        sessionStartedAt.AddSeconds(2),
                        "exec-root-search",
                        "search")
                ]);
            var rootSearchEvents = EnumerateUsageEventsFromFile(searchRootPath, "fixture").ToList();
            var rootSearchBucket = new UsageBucket();
            foreach (var usage in rootSearchEvents)
            {
                rootSearchBucket.Add(usage);
            }
            if (rootSearchEvents.Count != 2 ||
                rootSearchEvents.Any(usage =>
                    usage.Source != UsageEventSource.Natural ||
                    usage.EquivalentCostOverrideUsd != WebSearchEquivalentCostUsd ||
                    usage.CacheWriteTokens != 0L ||
                    usage.TotalTokens != 0L ||
                    !string.Equals(usage.Model, "gpt-5.6-sol", StringComparison.Ordinal)) ||
                Math.Abs(rootSearchBucket.EquivalentCostOverrideUsd - 0.02D) > 0.000_000_001D)
            {
                throw new InvalidOperationException(
                    "Each completed web-search action must become one zero-token $0.01 API-equivalent usage event.");
            }

            var searchSubagentPath = Path.Combine(tempRoot, "web-search-subagent.jsonl");
            File.WriteAllLines(
                searchSubagentPath,
                [
                    JsonSerializer.Serialize(new
                    {
                        timestamp = sessionStartedAt.AddSeconds(3).ToString("O"),
                        type = "session_meta",
                        payload = new
                        {
                            id = "019f4c0e-7c6c-7663-9507-f142d09c5591",
                            forked_from_id = "019f4c0e-7c6c-7663-9507-f142d09c5590",
                            timestamp = sessionStartedAt.AddSeconds(3).ToString("O"),
                            thread_source = "subagent",
                            source = new { subagent = new { } }
                        }
                    }),
                    MakeTurnContextFixture(sessionStartedAt.AddSeconds(3.1D), "gpt-5.6-sol"),
                    MakeWebSearchEndFixture(
                        sessionStartedAt.AddSeconds(3.2D),
                        "exec-replayed-search",
                        "open_page"),
                    JsonSerializer.Serialize(new
                    {
                        timestamp = sessionStartedAt.AddSeconds(4).ToString("O"),
                        type = "inter_agent_communication_metadata",
                        payload = new { trigger_turn = true }
                    }),
                    MakeTurnContextFixture(sessionStartedAt.AddSeconds(4.1D), "gpt-5.6-luna"),
                    MakeWebSearchEndFixture(
                        sessionStartedAt.AddSeconds(5),
                        "exec-live-subagent-search",
                        "search")
                ]);
            var subagentSearchEvents = EnumerateUsageEventsFromFile(
                searchSubagentPath,
                "fixture").ToList();
            if (subagentSearchEvents.Count != 1 ||
                subagentSearchEvents[0].TimestampUtc != sessionStartedAt.AddSeconds(5) ||
                subagentSearchEvents[0].EquivalentCostOverrideUsd != WebSearchEquivalentCostUsd ||
                !string.Equals(
                    subagentSearchEvents[0].Model,
                    "gpt-5.6-luna",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Subagent web-search replay must stay suppressed while live post-boundary searches remain billable.");
            }

            var zeroTokenResponse = new CacheWriteResponseRecord
            {
                ResponseId = "resp_zero_token_fixture",
                TimestampUtc = rootSearchEvents[0].TimestampUtc,
                Model = "gpt-5.6-sol",
                InputTokens = 0,
                CachedInputTokens = 0,
                CacheWriteTokens = 0,
                OutputTokens = 0,
                ReasoningOutputTokens = 0,
                TotalTokens = 0
            };
            var searchResponseReconciliation = ApplyResponseUsageMatches(
                [rootSearchEvents[0]],
                [zeroTokenResponse]);
            if (searchResponseReconciliation.MatchedCount != 0 ||
                searchResponseReconciliation.UnmatchedResponses.Count != 1 ||
                rootSearchEvents[0].ResponseUsageMatched)
            {
                throw new InvalidOperationException(
                    "Fixed-cost web-search events must not consume response.completed usage records during SQLite reconciliation.");
            }

            var duplicatePath = Path.Combine(tempRoot, "duplicate-cumulative.jsonl");
            File.WriteAllLines(
                duplicatePath,
                [
                    MakeTurnContextFixture(sessionStartedAt, "gpt-5.6-sol"),
                    MakeTokenCountFixture(sessionStartedAt.AddSeconds(1), 300, 10, 300),
                    MakeTokenCountFixture(sessionStartedAt.AddSeconds(2), 300, 11, 300),
                    MakeTokenCountFixture(sessionStartedAt.AddSeconds(3), 200, 12, 500)
                ]);
            var duplicateEvents = EnumerateUsageEventsFromFile(duplicatePath, "fixture").ToList();
            var duplicateSummary = new AccountUsageSummary { AccountName = "fixture" };
            var duplicateReport = new UsageReport();
            var duplicateSummaries = new Dictionary<string, AccountUsageSummary>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["fixture"] = duplicateSummary
            };
            foreach (var usage in duplicateEvents)
            {
                AddUsage(
                    duplicateReport,
                    duplicateSummaries,
                    usage,
                    sessionStartedAt.AddDays(-1),
                    sessionStartedAt.AddDays(-1),
                    sessionStartedAt.AddDays(-1),
                    sessionStartedAt.AddDays(-1),
                    sessionStartedAt.AddDays(-1),
                    sessionStartedAt.AddDays(-1));
            }
            if (duplicateEvents.Count != 3 ||
                duplicateEvents[0].Source != UsageEventSource.Natural ||
                duplicateEvents[1].Source != UsageEventSource.OfficialSnapshot ||
                duplicateEvents[1].TotalTokens != 0 ||
                duplicateEvents[1].RateLimitUsedPercent != 11 ||
                duplicateEvents[2].Source != UsageEventSource.Natural ||
                duplicateSummary.Month.TotalTokens != 500 ||
                duplicateSummary.Month.Events != 2 ||
                duplicateSummary.Timeline.Count != 3 ||
                duplicateSummary.RateLimitUsedPercent != 12)
            {
                throw new InvalidOperationException(
                    "Repeated cumulative usage snapshots must remain as zero-cost quota boundaries without inflating token totals.");
            }

            var totalOnlyPath = Path.Combine(tempRoot, "total-only-cumulative.jsonl");
            File.WriteAllLines(
                totalOnlyPath,
                [
                    MakeTokenCountFixture(sessionStartedAt.AddSeconds(1), 300, 10, 300),
                    MakeTokenCountFixture(sessionStartedAt.AddSeconds(2), 0, 11, 500, includeLastUsage: false),
                    MakeTokenCountFixture(sessionStartedAt.AddSeconds(3), 0, 12, 100, includeLastUsage: false)
                ]);
            var totalOnlyEvents = EnumerateUsageEventsFromFile(totalOnlyPath, "fixture").ToList();
            if (totalOnlyEvents.Count != 3 ||
                totalOnlyEvents[0].TotalTokens != 300 ||
                totalOnlyEvents[1].Source != UsageEventSource.Natural ||
                totalOnlyEvents[1].TotalTokens != 200 ||
                totalOnlyEvents[2].Source != UsageEventSource.OfficialSnapshot ||
                totalOnlyEvents[2].TotalTokens != 0 ||
                totalOnlyEvents[2].RateLimitUsedPercent != 12)
            {
                throw new InvalidOperationException(
                    "Total-only cumulative records must use non-negative deltas and retain reset boundaries without a usage spike.");
            }

            var cacheWritePath = Path.Combine(tempRoot, "cache-write.jsonl");
            File.WriteAllLines(
                cacheWritePath,
                [
                    MakeCacheWriteTokenCountFixture(
                        sessionStartedAt.AddSeconds(1), 100, 10, 100, 20, 20),
                    MakeCacheWriteTokenCountFixture(
                        sessionStartedAt.AddSeconds(2), 150, 11, 250, 30, 50,
                        "input_tokens_details"),
                    MakeCacheWriteTokenCountFixture(
                        sessionStartedAt.AddSeconds(3), 200, 12, 450, 40, 90,
                        "prompt_tokens_details"),
                    MakeCacheWriteTokenCountFixture(
                        sessionStartedAt.AddSeconds(4), 0, 13, 550, 0, 120,
                        includeLastUsage: false),
                    MakeTokenCountFixture(
                        sessionStartedAt.AddSeconds(5), 0, 14, 650, includeLastUsage: false),
                    MakeCacheWriteTokenCountFixture(
                        sessionStartedAt.AddSeconds(6), 50, 15, 700, 0, 120),
                    MakeCacheWriteTokenCountFixture(
                        sessionStartedAt.AddSeconds(7), 50, 16, 750, 60, 180)
                ]);
            var cacheWriteEvents = EnumerateUsageEventsFromFile(cacheWritePath, "fixture").ToList();
            if (cacheWriteEvents.Count != 7 ||
                cacheWriteEvents[0].CacheWriteTokens != 20 ||
                cacheWriteEvents[1].CacheWriteTokens != 30 ||
                cacheWriteEvents[2].CacheWriteTokens != 40 ||
                cacheWriteEvents[3].InputTokens != 100 ||
                cacheWriteEvents[3].CacheWriteTokens != 30 ||
                cacheWriteEvents[4].CacheWriteTokens.HasValue ||
                cacheWriteEvents[5].CacheWriteTokens != 0 ||
                cacheWriteEvents[6].CacheWriteTokens.HasValue)
            {
                throw new InvalidOperationException(
                    "Cache-write parsing must support flat, Responses, Chat, cumulative-delta, explicit-zero, missing, and invalid values.");
            }

            var matchedUsage = new UsageEvent
            {
                TimestampUtc = sessionStartedAt.AddSeconds(7),
                Model = "gpt-5.6-sol",
                InputTokens = 1_000,
                CachedInputTokens = 600,
                OutputTokens = 200,
                ReasoningOutputTokens = 50,
                TotalTokens = 1_200
            };
            var duplicateUsage = CloneUsageEvent(matchedUsage);
            duplicateUsage.TimestampUtc = matchedUsage.TimestampUtc.AddSeconds(1);
            var rawResponse = new CacheWriteResponseRecord
            {
                ResponseId = "resp_fixture",
                TimestampUtc = sessionStartedAt,
                Model = "gpt-5.6-sol",
                InputTokens = 1_000,
                CachedInputTokens = 600,
                CacheWriteTokens = 100,
                OutputTokens = 200,
                ReasoningOutputTokens = 50,
                TotalTokens = 1_200
            };
            var exactReconciliation = ApplyResponseUsageMatches(
                [matchedUsage, duplicateUsage],
                [rawResponse]);
            if (exactReconciliation.MatchedCount != 1 ||
                exactReconciliation.DifferenceCount != 1 ||
                exactReconciliation.UnmatchedResponses.Count != 0 ||
                matchedUsage.CacheWriteTokens != 100 ||
                !matchedUsage.ResponseUsageMatched ||
                matchedUsage.ResponseUsageDifferenceFields is not ["cache_write_tokens_missing"] ||
                duplicateUsage.CacheWriteTokens.HasValue)
            {
                throw new InvalidOperationException(
                    "Raw response usage correlation must reconcile every field, record missing cache-write, and consume each response id once.");
            }

            var mismatchedUsage = new UsageEvent
            {
                TimestampUtc = sessionStartedAt.AddSeconds(10),
                Model = "gpt-5.6-sol",
                InputTokens = 995,
                CachedInputTokens = 600,
                CacheWriteTokens = 90,
                OutputTokens = 200,
                ReasoningOutputTokens = 50,
                TotalTokens = 1_195
            };
            var authoritativeResponse = new CacheWriteResponseRecord
            {
                ResponseId = "resp_mismatch_fixture",
                TimestampUtc = sessionStartedAt.AddSeconds(9),
                Model = "gpt-5.6-sol",
                InputTokens = 1_000,
                CachedInputTokens = 600,
                CacheWriteTokens = 100,
                OutputTokens = 200,
                ReasoningOutputTokens = 50,
                TotalTokens = 1_200
            };
            var mismatchReconciliation = ApplyResponseUsageMatches(
                [mismatchedUsage],
                [authoritativeResponse]);
            if (mismatchReconciliation.MatchedCount != 1 ||
                mismatchReconciliation.DifferenceCount != 1 ||
                mismatchedUsage.InputTokens != 1_000 ||
                mismatchedUsage.CacheWriteTokens != 100 ||
                mismatchedUsage.TotalTokens != 1_200 ||
                mismatchedUsage.ResponseUsageDifferenceFields is not
                    ["input_tokens", "cache_write_tokens", "total_tokens"] ||
                authoritativeResponse.Reconciliation is not { MatchKind: "tolerant" })
            {
                throw new InvalidOperationException(
                    "A close response-usage match must replace the full JSONL tuple and persist every differing field.");
            }

            var knownCacheUsage = new UsageEvent
            {
                TimestampUtc = sessionStartedAt.AddSeconds(20),
                Model = "gpt-5.6-sol",
                InputTokens = 800,
                CachedInputTokens = 500,
                CacheWriteTokens = 75,
                OutputTokens = 100,
                ReasoningOutputTokens = 25,
                TotalTokens = 900
            };
            var responseWithoutCacheWrite = new CacheWriteResponseRecord
            {
                ResponseId = "resp_missing_cache_write_fixture",
                TimestampUtc = sessionStartedAt.AddSeconds(19),
                Model = "gpt-5.6-sol",
                InputTokens = 800,
                CachedInputTokens = 500,
                CacheWriteTokens = null,
                OutputTokens = 100,
                ReasoningOutputTokens = 25,
                TotalTokens = 900
            };
            var missingCacheReconciliation = ApplyResponseUsageMatches(
                [knownCacheUsage],
                [responseWithoutCacheWrite]);
            if (missingCacheReconciliation.MatchedCount != 1 ||
                missingCacheReconciliation.DifferenceCount != 0 ||
                missingCacheReconciliation.UnmatchedResponses.Count != 0 ||
                knownCacheUsage.CacheWriteTokens != 75 ||
                knownCacheUsage.ResponseUsageDifferenceFields.Length != 0)
            {
                throw new InvalidOperationException(
                    "A response without cache-write metadata must still match and must not erase a known JSONL value.");
            }

            CodexCliService.EnsureSqliteProvider();
            using (var responseDatabase = new SqliteConnection("Data Source=:memory:"))
            {
                responseDatabase.Open();
                using (var schema = responseDatabase.CreateCommand())
                {
                    schema.CommandText = """
                        CREATE TABLE logs (
                            id INTEGER PRIMARY KEY,
                            ts INTEGER NOT NULL,
                            ts_nanos INTEGER NOT NULL,
                            target TEXT NOT NULL,
                            feedback_log_body TEXT NOT NULL
                        );
                        """;
                    schema.ExecuteNonQuery();
                }
                using (var insert = responseDatabase.CreateCommand())
                {
                    var responseJson = JsonSerializer.Serialize(new
                    {
                        type = "response.completed",
                        response = new
                        {
                            id = "resp_sql_missing_cache_write_fixture",
                            model = "gpt-5.6-sol",
                            usage = new
                            {
                                input_tokens = 800,
                                input_tokens_details = new { cached_tokens = 500 },
                                output_tokens = 100,
                                output_tokens_details = new { reasoning_tokens = 25 },
                                total_tokens = 900
                            }
                        }
                    });
                    insert.CommandText = """
                        INSERT INTO logs (id, ts, ts_nanos, target, feedback_log_body)
                        VALUES (1, $ts, 0, 'codex_api::sse::responses', $body);
                        """;
                    insert.Parameters.AddWithValue("$ts", sessionStartedAt.ToUnixTimeSeconds());
                    insert.Parameters.AddWithValue("$body", "SSE event: " + responseJson);
                    insert.ExecuteNonQuery();
                }

                var sqliteResponses = ReadCacheWriteResponses(
                    responseDatabase,
                    sessionStartedAt.AddMinutes(-1),
                    afterId: 0,
                    throughId: 1,
                    fullScan: false);
                if (sqliteResponses is not [{
                        ResponseId: "resp_sql_missing_cache_write_fixture",
                        CacheWriteTokens: null,
                        TotalTokens: 900
                    }])
                {
                    throw new InvalidOperationException(
                        "SQLite response parsing must retain otherwise-valid rows with missing cache-write metadata.");
                }
            }

            var responseOnly = new CacheWriteResponseRecord
            {
                LogId = 10,
                ResponseId = "resp_response_only_fixture",
                TimestampUtc = sessionStartedAt.AddSeconds(30),
                Model = "gpt-5.6-sol",
                InputTokens = 700,
                CachedInputTokens = 400,
                CacheWriteTokens = null,
                OutputTokens = 80,
                ReasoningOutputTokens = 20,
                TotalTokens = 780
            };
            var duplicateResponseOnly = new CacheWriteResponseRecord
            {
                LogId = 11,
                ResponseId = responseOnly.ResponseId,
                TimestampUtc = responseOnly.TimestampUtc,
                Model = responseOnly.Model,
                InputTokens = responseOnly.InputTokens,
                CachedInputTokens = responseOnly.CachedInputTokens,
                CacheWriteTokens = responseOnly.CacheWriteTokens,
                OutputTokens = responseOnly.OutputTokens,
                ReasoningOutputTokens = responseOnly.ReasoningOutputTokens,
                TotalTokens = responseOnly.TotalTokens
            };
            var responseOnlyReconciliation = ApplyResponseUsageMatches(
                [],
                [responseOnly, duplicateResponseOnly]);
            var responseOnlyEvents = CreateResponseOnlyUsageEvents(
                responseOnlyReconciliation.UnmatchedResponses,
                sessionStartedAt);
            if (responseOnlyReconciliation.MatchedCount != 0 ||
                responseOnlyReconciliation.UnmatchedResponses.Count != 1 ||
                responseOnlyEvents is not [{
                    Source: UsageEventSource.Natural,
                    CacheWriteTokens: null,
                    ResponseUsageMatched: true,
                    ResponseUsageMatchKind: "response-only"
                }] ||
                responseOnlyEvents[0].ResponseUsageDifferenceFields is not ["jsonl_usage_missing"] ||
                responseOnlyEvents[0].TotalTokens != 780)
            {
                throw new InvalidOperationException(
                    "Unmatched provider responses must become one de-duplicated response-only usage event.");
            }

            var partialQuotaSummary = new AccountUsageSummary
            {
                AccountName = "fixture",
                SecondaryRateLimitUsedPercent = 25,
                SecondaryRateLimitWindowMinutes = 10_080,
                SecondaryRateLimitResetAtUtc = sessionStartedAt.AddDays(7),
                RateLimitObservedAtUtc = sessionStartedAt
            };
            UpdateLatestQuotaSnapshot(
                partialQuotaSummary,
                new UsageEvent
                {
                    TimestampUtc = sessionStartedAt.AddMinutes(1),
                    RateLimitUsedPercent = 15,
                    RateLimitWindowMinutes = 300,
                    RateLimitResetAtUtc = sessionStartedAt.AddHours(5)
                });
            if (partialQuotaSummary.RateLimitUsedPercent != 15 ||
                partialQuotaSummary.SecondaryRateLimitUsedPercent != 25 ||
                partialQuotaSummary.SecondaryRateLimitWindowMinutes != 10_080 ||
                partialQuotaSummary.SecondaryRateLimitResetAtUtc != sessionStartedAt.AddDays(7))
            {
                throw new InvalidOperationException(
                    "A partial quota snapshot must not erase the other valid quota window.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // A locked temp fixture should not hide the validation result.
            }
        }
    }

    public static void ValidatePersistentIncrementalCache()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-usage-cache-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        try
        {
            var timestamp = DateTimeOffset.UtcNow.AddMinutes(-2);
            var session = Path.Combine(sessions, "usage.jsonl");
            File.WriteAllLines(
                session,
                [
                    MakeTurnContextFixture(timestamp, "gpt-5.6-terra"),
                    MakeTokenCountFixture(timestamp.AddSeconds(1), 300, 12)
                ]);

            var firstTracker = new UsageTracker(root);
            var first = firstTracker.BuildReport([], sessions, DateTimeOffset.Now);
            if (first.UnassignedMonth.TotalTokens != 300 ||
                !File.Exists(firstTracker.PersistentUsageCachePath))
            {
                throw new InvalidOperationException(
                    "Persistent usage cache must be created after the first local scan.");
            }

            // A fresh tracker represents an application restart. It must hydrate the parsed
            // file index, then preserve parser model state while consuming only appended bytes.
            var restartedTracker = new UsageTracker(root);
            var restarted = restartedTracker.BuildReport([], sessions, DateTimeOffset.Now);
            File.AppendAllText(
                session,
                MakeTokenCountFixture(timestamp.AddSeconds(2), 300, 13, 300) + Environment.NewLine);
            var repeated = restartedTracker.BuildReport([], sessions, DateTimeOffset.Now);
            var persistedRepeatedEvents = restartedTracker
                .GetPersistedUsageEvents(sessions, timestamp.AddMinutes(-1))?
                .OrderBy(item => item.TimestampUtc)
                .ToList();
            File.AppendAllText(
                session,
                MakeTokenCountFixture(timestamp.AddSeconds(3), 200, 14, 500) + Environment.NewLine);
            var appended = restartedTracker.BuildReport([], sessions, DateTimeOffset.Now);
            var terraUsage = appended.UnassignedMonth.ModelUsage.SingleOrDefault(model =>
                string.Equals(model.Model, "gpt-5.6-terra", StringComparison.Ordinal));
            if (restarted.UnassignedMonth.TotalTokens != 300 ||
                repeated.UnassignedMonth.TotalTokens != 300 ||
                persistedRepeatedEvents is not { Count: 2 } ||
                persistedRepeatedEvents[1].Source != UsageEventSource.OfficialSnapshot ||
                persistedRepeatedEvents[1].RateLimitUsedPercent != 13 ||
                appended.UnassignedMonth.TotalTokens != 500 ||
                terraUsage?.TotalTokens != 500)
            {
                throw new InvalidOperationException(
                    "Incremental usage parsing must persist cumulative de-duplication state, quota boundaries, and model context.");
            }

            var finalLine = MakeTokenCountFixture(timestamp.AddSeconds(4), 250, 15, 750);
            var split = finalLine.Length / 2;
            File.AppendAllText(session, finalLine[..split]);
            var partial = restartedTracker.BuildReport([], sessions, DateTimeOffset.Now);
            File.AppendAllText(session, finalLine[split..] + Environment.NewLine);
            var completed = restartedTracker.BuildReport([], sessions, DateTimeOffset.Now);
            if (partial.UnassignedMonth.TotalTokens != 500 ||
                completed.UnassignedMonth.TotalTokens != 750)
            {
                throw new InvalidOperationException(
                    "A partially-written JSONL line must remain pending until it is complete.");
            }

            File.WriteAllLines(
                session,
                [
                    MakeTurnContextFixture(timestamp.AddSeconds(4), "gpt-5.6-luna"),
                    MakeTokenCountFixture(timestamp.AddSeconds(5), 111, 16, 111)
                ]);
            var rewritten = restartedTracker.BuildReport([], sessions, DateTimeOffset.Now);
            if (rewritten.UnassignedMonth.TotalTokens != 111 ||
                rewritten.UnassignedMonth.ModelUsage.Single().Model != "gpt-5.6-luna")
            {
                throw new InvalidOperationException(
                    "A truncated or replaced usage file must invalidate its incremental entry.");
            }

            File.WriteAllText(restartedTracker.PersistentUsageCachePath, "{broken");
            var recovered = new UsageTracker(root).BuildReport([], sessions, DateTimeOffset.Now);
            if (recovered.UnassignedMonth.TotalTokens != 111)
            {
                throw new InvalidOperationException(
                    "A damaged persistent usage cache must fall back to a clean local scan.");
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
                // A locked temp fixture should not hide the validation result.
            }
        }
    }

    public static void ValidatePersistentCacheWriteIndex()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-response-index-" + Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "logs_2.sqlite");
        Directory.CreateDirectory(root);

        static string MakeCompletedResponse(string responseId, long totalTokens)
        {
            return "SSE event: " + JsonSerializer.Serialize(new
            {
                type = "response.completed",
                response = new
                {
                    id = responseId,
                    model = "gpt-5.6-sol",
                    usage = new
                    {
                        input_tokens = totalTokens - 80,
                        input_tokens_details = new
                        {
                            cached_tokens = Math.Max(0L, totalTokens - 380),
                            cache_write_tokens = 0
                        },
                        output_tokens = 80,
                        output_tokens_details = new { reasoning_tokens = 20 },
                        total_tokens = totalTokens
                    }
                }
            });
        }

        static void InsertResponse(
            SqliteConnection connection,
            long id,
            DateTimeOffset timestamp,
            string responseId,
            long totalTokens)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO logs (id, ts, ts_nanos, target, feedback_log_body)
                VALUES ($id, $ts, 0, 'codex_api::sse::responses', $body);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$ts", timestamp.ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("$body", MakeCompletedResponse(responseId, totalTokens));
            insert.ExecuteNonQuery();
        }

        try
        {
            CodexCliService.EnsureSqliteProvider();
            var oldTimestamp = DateTimeOffset.FromUnixTimeSeconds(
                DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds());
            var newTimestamp = oldTimestamp.AddMinutes(1);
            var firstFromUtc = oldTimestamp.AddHours(-1);

            using (var connection = new SqliteConnection(
                       new SqliteConnectionStringBuilder
                       {
                           DataSource = databasePath,
                           Mode = SqliteOpenMode.ReadWriteCreate,
                           Pooling = false
                       }.ToString()))
            {
                connection.Open();
                using (var schema = connection.CreateCommand())
                {
                    schema.CommandText = """
                        CREATE TABLE logs (
                            id INTEGER PRIMARY KEY,
                            ts INTEGER NOT NULL,
                            ts_nanos INTEGER NOT NULL,
                            target TEXT NOT NULL,
                            feedback_log_body TEXT NOT NULL
                        );
                        CREATE INDEX idx_logs_ts ON logs(ts);
                        """;
                    schema.ExecuteNonQuery();
                }
                InsertResponse(connection, 100, oldTimestamp, "resp_retained_before_rewind", 780);
            }

            var tracker = new UsageTracker(root);
            var initial = tracker.LoadCacheWriteResponses(databasePath, firstFromUtc);
            if (initial is not [{ ResponseId: "resp_retained_before_rewind" }] ||
                !File.Exists(tracker.PersistentCacheWriteIndexPath))
            {
                throw new InvalidOperationException(
                    "The initial response scan must create a durable response index.");
            }

            using (var connection = new SqliteConnection(
                       new SqliteConnectionStringBuilder
                       {
                           DataSource = databasePath,
                           Mode = SqliteOpenMode.ReadWrite,
                           Pooling = false
                       }.ToString()))
            {
                connection.Open();
                using (var delete = connection.CreateCommand())
                {
                    delete.CommandText = "DELETE FROM logs;";
                    delete.ExecuteNonQuery();
                }
                InsertResponse(connection, 1, newTimestamp, "resp_after_id_rewind", 910);
            }

            // Bypass the production refresh throttle so this fixture observes the MAX(id)
            // rewind immediately. A same-path full scan must merge rather than replace.
            tracker._lastCacheWriteDatabaseRefreshUtc = default;
            var rescanned = tracker.LoadCacheWriteResponses(
                databasePath,
                oldTimestamp.AddMinutes(-30));
            var responseOnlyEvents = CreateResponseOnlyUsageEvents(
                ApplyResponseUsageMatches([], rescanned).UnmatchedResponses,
                firstFromUtc);
            if (rescanned.Count != 2 ||
                responseOnlyEvents.Select(item => item.TotalTokens).ToArray() is not [780, 910] ||
                tracker._cacheWriteLoadedFromUtc != firstFromUtc)
            {
                throw new InvalidOperationException(
                    "A same-path MAX(id) rewind must retain disappeared responses and the earliest scan boundary.");
            }

            var restarted = new UsageTracker(root);
            var persisted = restarted.LoadPersistedCacheWriteResponsesOnly(databasePath, firstFromUtc);
            if (persisted.Select(item => item.ResponseId).Order(StringComparer.Ordinal).ToArray() is not
                ["resp_after_id_rewind", "resp_retained_before_rewind"])
            {
                throw new InvalidOperationException(
                    "Responses retained across a database rewind must survive an application restart.");
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
                // A locked temp fixture should not hide the validation result.
            }
        }
    }

    private static string MakeTurnContextFixture(DateTimeOffset timestamp, string model)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "turn_context",
            payload = new
            {
                turn_id = "019f4c0e-7d9e-7153-bbb0-09100a0d15f4",
                model
            }
        });
    }

    private static string MakeWebSearchEndFixture(
        DateTimeOffset timestamp,
        string callId,
        string actionType)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "web_search_end",
                call_id = callId,
                query = "fixture query",
                action = new { type = actionType }
            }
        });
    }

    private static string MakeTokenCountFixture(
        DateTimeOffset timestamp,
        long totalTokens,
        double usedPercent,
        long? cumulativeTotalTokens = null,
        bool includeLastUsage = true)
    {
        var cumulativeTokens = cumulativeTotalTokens ?? totalTokens;
        var lastTokenUsage = includeLastUsage
            ? new
            {
                input_tokens = totalTokens,
                cached_input_tokens = 0,
                output_tokens = 0,
                reasoning_output_tokens = 0,
                total_tokens = totalTokens
            }
            : null;
        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = lastTokenUsage,
                    total_token_usage = new
                    {
                        input_tokens = cumulativeTokens,
                        cached_input_tokens = 0,
                        output_tokens = 0,
                        reasoning_output_tokens = 0,
                        total_tokens = cumulativeTokens
                    }
                },
                rate_limits = new
                {
                    primary = new
                    {
                        used_percent = usedPercent,
                        window_minutes = 300,
                        resets_at = timestamp.AddHours(5).ToUnixTimeSeconds()
                    },
                    secondary = new
                    {
                        used_percent = usedPercent + 1,
                        window_minutes = 10_080,
                        resets_at = timestamp.AddDays(7).ToUnixTimeSeconds()
                    },
                    credits = new
                    {
                        has_credits = true,
                        unlimited = false,
                        balance = "3.25"
                    },
                    individual_limit = new
                    {
                        limit = "20.00",
                        used = "4.00",
                        remaining_percent = 80,
                        resets_at = timestamp.AddDays(30).ToUnixTimeSeconds()
                    },
                    plan_type = "business"
                }
            }
        });
    }

    private static string MakeCacheWriteTokenCountFixture(
        DateTimeOffset timestamp,
        long totalTokens,
        double usedPercent,
        long cumulativeTotalTokens,
        long lastCacheWriteTokens,
        long cumulativeCacheWriteTokens,
        string? detailsProperty = null,
        bool includeLastUsage = true)
    {
        var root = JsonNode.Parse(MakeTokenCountFixture(
            timestamp,
            totalTokens,
            usedPercent,
            cumulativeTotalTokens,
            includeLastUsage))!.AsObject();
        var info = root["payload"]!["info"]!.AsObject();
        if (includeLastUsage && info["last_token_usage"] is JsonObject lastUsage)
        {
            SetCacheWriteFixtureValue(lastUsage, lastCacheWriteTokens, detailsProperty);
        }
        if (info["total_token_usage"] is JsonObject totalUsage)
        {
            SetCacheWriteFixtureValue(totalUsage, cumulativeCacheWriteTokens, detailsProperty);
        }
        return root.ToJsonString();
    }

    private static void SetCacheWriteFixtureValue(
        JsonObject usage,
        long cacheWriteTokens,
        string? detailsProperty)
    {
        if (string.IsNullOrWhiteSpace(detailsProperty))
        {
            usage["cache_write_tokens"] = cacheWriteTokens;
            return;
        }

        usage[detailsProperty] = new JsonObject
        {
            ["cache_write_tokens"] = cacheWriteTokens
        };
    }

    private string SwitchHistoryPath => Path.Combine(_rootPath, "usage-account-switches.json");
    private string StableSwitchHistoryPath => Path.Combine(CodexCliService.GetDefaultCodexHome(), "codex-account-manager-usage-switches.json");

    private IEnumerable<string> GetSwitchHistoryPaths()
    {
        return new[] { SwitchHistoryPath, StableSwitchHistoryPath }
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public void EnsureCurrentAccountTracking(AccountRecord? account)
    {
        if (account == null)
        {
            return;
        }

        EnsureCurrentAccountTracking(account.Name, QuotaAccountIdentity.CreateKey(account));
    }

    public void EnsureCurrentAccountTracking(string? accountName)
    {
        EnsureCurrentAccountTracking(accountName, accountKey: null);
    }

    private void EnsureCurrentAccountTracking(string? accountName, string? accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        var events = LoadSwitchEvents();
        var last = events.LastOrDefault(candidate =>
            string.Equals(candidate.ManagerScopeKey, _managerScopeKey, StringComparison.Ordinal));
        if (last != null && SwitchTargetsAccount(last, accountName, accountKey))
        {
            SaveSwitchEvents(events);
            return;
        }

        RecordSwitch(accountName, accountKey, "detected");
    }

    public void RecordSwitch(AccountRecord? account, string source = "switch")
    {
        if (account == null)
        {
            return;
        }

        RecordSwitch(account.Name, QuotaAccountIdentity.CreateKey(account), source);
    }

    public void RecordSwitch(string accountName, string source = "switch")
    {
        RecordSwitch(accountName, accountKey: null, source: source);
    }

    private void RecordSwitch(string accountName, string? accountKey, string source)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        var events = LoadSwitchEvents();
        var normalizedAccountName = accountName.Trim();
        var last = events.LastOrDefault(candidate =>
            string.Equals(candidate.ManagerScopeKey, _managerScopeKey, StringComparison.Ordinal));
        if (last != null && SwitchTargetsAccount(last, normalizedAccountName, accountKey))
        {
            SaveSwitchEvents(events);
            return;
        }
        events.Add(new UsageSwitchEvent
        {
            AccountName = normalizedAccountName,
            AccountKey = accountKey,
            ManagerScopeKey = _managerScopeKey,
            SwitchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Source = source
        });

        SaveSwitchEvents(events);
    }

    private static bool SwitchTargetsAccount(
        UsageSwitchEvent switchEvent,
        string accountName,
        string? accountKey)
    {
        if (!string.IsNullOrWhiteSpace(accountKey) || !string.IsNullOrWhiteSpace(switchEvent.AccountKey))
        {
            return !string.IsNullOrWhiteSpace(accountKey) &&
                string.Equals(switchEvent.AccountKey, accountKey, StringComparison.Ordinal);
        }

        return switchEvent.AccountName.Equals(accountName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public void RenameAccount(string originalName, string newName)
    {
        if (string.IsNullOrWhiteSpace(originalName) ||
            string.IsNullOrWhiteSpace(newName) ||
            originalName.Trim().Equals(newName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var events = LoadSwitchEvents();
        foreach (var switchEvent in events.Where(candidate =>
                     candidate.AccountName.Equals(originalName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            switchEvent.AccountName = newName.Trim();
        }
        SaveSwitchEvents(events);
        _probeUsageLedger.RenameAccount(originalName, newName);
    }

    public UsageReport BuildReport(IReadOnlyList<AccountRecord> accounts) =>
        BuildReport(
            accounts,
            Path.Combine(CodexCliService.GetDefaultCodexHome(), "sessions"),
            DateTimeOffset.Now);

    public UsageReport? TryBuildCachedReport(IReadOnlyList<AccountRecord> accounts)
    {
        var now = DateTimeOffset.Now;
        var sessionsRoot = Path.Combine(CodexCliService.GetDefaultCodexHome(), "sessions");
        var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset);
        var timelineStart = now.AddDays(-35) < monthStart ? now.AddDays(-35) : monthStart;
        var events = GetPersistedUsageEvents(sessionsRoot, timelineStart);
        return events == null
            ? null
            : BuildReportFromEvents(
                accounts,
                now,
                timelineStart,
                events,
                sessionsRoot,
                refreshCacheWriteDatabase: false);
    }

    internal UsageReport BuildReport(
        IReadOnlyList<AccountRecord> accounts,
        string sessionsRoot,
        DateTimeOffset now)
    {
        var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset);
        // The dashboard offers current-month and passive-monitor lookbacks. Keep a small
        // safety margin so month-boundary sessions remain available to the passive quota
        // estimator while month/week/day buckets use their calendar cutoffs.
        var timelineStart = now.AddDays(-35) < monthStart ? now.AddDays(-35) : monthStart;
        return BuildReportFromEvents(
            accounts,
            now,
            timelineStart,
            GetUsageEvents(sessionsRoot, timelineStart),
            sessionsRoot,
            refreshCacheWriteDatabase: true);
    }

    internal IReadOnlyList<UsageEvent> GetReconciledUsageEventsForImport(
        string sessionsRoot,
        DateTimeOffset sinceUtc)
    {
        var normalizedSinceUtc = sinceUtc.ToUniversalTime();
        return EnrichCacheWriteTokens(
            GetUsageEvents(sessionsRoot, normalizedSinceUtc),
            sessionsRoot,
            normalizedSinceUtc,
            refreshDatabase: true);
    }

    private UsageReport BuildReportFromEvents(
        IReadOnlyList<AccountRecord> accounts,
        DateTimeOffset now,
        DateTimeOffset timelineStart,
        IReadOnlyList<UsageEvent> usageEvents,
        string sessionsRoot,
        bool refreshCacheWriteDatabase)
    {
        var reportUsageEvents = EnrichCacheWriteTokens(
            usageEvents,
            sessionsRoot,
            timelineStart,
            refreshCacheWriteDatabase);
        var todayStart = new DateTimeOffset(now.Date, now.Offset);
        var daysSinceMonday = ((int)now.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var oneHourStart = now.AddHours(-1);
        var fiveHoursStart = now.AddHours(-5);
        var dayStart = todayStart;
        var weekStart = new DateTimeOffset(now.Date.AddDays(-daysSinceMonday), now.Offset);
        var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset);
        var switchEvents = FilterTrustedSwitchEvents(LoadSwitchEvents(), accounts)
            .OrderBy(e => e.GetSwitchedAtUtc())
            .ToList();
        var switchTimeline = switchEvents
            .Select(entry => new UsageSwitchPoint(entry, entry.GetSwitchedAtUtc()))
            .ToArray();
        var report = new UsageReport
        {
            GeneratedAt = now,
            SwitchEventCount = switchEvents.Count
        };

        var summaries = accounts.ToDictionary(
            account => account.Name,
            account => new AccountUsageSummary { AccountName = account.Name },
            StringComparer.OrdinalIgnoreCase);

        foreach (var usage in reportUsageEvents)
        {
            AssignUsageToActiveSwitch(usage, switchTimeline, accounts);
            AddUsage(report, summaries, usage, todayStart, oneHourStart, fiveHoursStart, dayStart, weekStart, monthStart);
        }

        foreach (var usage in _sub2ApiUsageLedger.LoadMissingUsageEvents(
                     accounts,
                     timelineStart.ToUniversalTime(),
                     reportUsageEvents))
        {
            // Ledger records carry a stable account identity and must never be reassigned by
            // an unrelated switch boundary. If a natural copy exists, the loader omits this
            // recovery row before it reaches the report.
            AddUsage(report, summaries, usage, todayStart, oneHourStart, fiveHoursStart, dayStart, weekStart, monthStart);
        }

        var accountsByKey = accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.CodexHome))
            .GroupBy(QuotaAccountIdentity.CreateKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var probe in _probeUsageLedger.LoadSince(timelineStart.ToUniversalTime()))
        {
            var accountName = accountsByKey.TryGetValue(probe.AccountKey, out var matchedAccount)
                ? matchedAccount.Name
                : ResolveCurrentAccountName(probe.AccountName, accounts) ?? probe.AccountName;
            var usage = probe.ToUsageEvent(accountName);
            AddUsage(report, summaries, usage, todayStart, oneHourStart, fiveHoursStart, dayStart, weekStart, monthStart);
        }

        report.Accounts = accounts
            .Select(account => summaries[account.Name])
            .ToList();

        return report;
    }

    internal static void ValidateProbeUsageMerge()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-probe-usage-merge-" + Guid.NewGuid().ToString("N"));
        var emptySessions = Path.Combine(root, "sessions");
        var accountHome = Path.Combine(root, "account-home");
        Directory.CreateDirectory(emptySessions);
        Directory.CreateDirectory(accountHome);
        try
        {
            var tracker = new UsageTracker(root);
            var oldAccount = new AccountRecord
            {
                Name = "old-name",
                CodexHome = accountHome,
                AuthKind = AccountAuthKind.AccessToken
            };
            var completedAtUtc = DateTimeOffset.UtcNow;
            var legacyLedger = new
            {
                SchemaVersion = 1,
                Events = new[]
                {
                    new
                    {
                        EventId = "legacy-probe:1",
                        AccountKey = QuotaAccountIdentity.CreateKey(oldAccount),
                        AccountName = oldAccount.Name,
                        CompletedAtUtc = completedAtUtc,
                        InputTokens = 9_500,
                        CachedInputTokens = 500,
                        OutputTokens = 86,
                        TotalTokens = 9_586,
                        EquivalentCostUsd = 0.024D,
                        Model = "gpt-5.6-terra"
                    }
                }
            };
            File.WriteAllText(
                Path.Combine(root, ProbeUsageLedger.FileName),
                JsonSerializer.Serialize(legacyLedger, JsonOptions));

            var renamedAccount = new AccountRecord
            {
                Name = "new-name",
                CodexHome = accountHome,
                AuthKind = AccountAuthKind.AccessToken
            };
            var report = tracker.BuildReport([renamedAccount], emptySessions, DateTimeOffset.Now);
            var summary = report.Accounts.Single();
            if (summary.AccountName != "new-name" ||
                summary.Hour.TotalTokens != 9_586 ||
                summary.FiveHours.TotalTokens != 9_586 ||
                summary.Day.TotalTokens != 9_586 ||
                summary.Week.TotalTokens != 9_586 ||
                summary.Month.TotalTokens != 9_586)
            {
                throw new InvalidOperationException("Probe usage report merge self-test failed.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void ValidateSwitchEventNormalization()
    {
        static UsageSwitchEvent Make(string accountName, int minute) => new()
        {
            AccountName = accountName,
            SwitchedAtUtc = new DateTimeOffset(2026, 7, 13, 3, minute, 0, TimeSpan.Zero).ToString("O"),
            Source = "fixture"
        };

        var normalized = NormalizeSwitchEvents(
        [
            Make("A", 0),
            Make("A", 1),
            Make("B", 2),
            Make("B", 3),
            Make("A", 4)
        ]);

        if (normalized.Count != 3 ||
            normalized[0].AccountName != "A" ||
            normalized[0].GetSwitchedAtUtc().Minute != 0 ||
            normalized[1].AccountName != "B" ||
            normalized[1].GetSwitchedAtUtc().Minute != 2 ||
            normalized[2].AccountName != "A" ||
            normalized[2].GetSwitchedAtUtc().Minute != 4)
        {
            throw new InvalidOperationException(
                "Switch-event normalization must collapse consecutive duplicates while retaining A → B → A epochs.");
        }

        var knownAccount = new AccountRecord
        {
            Name = "monthly-pool",
            CodexHome = Path.Combine(Path.GetTempPath(), "codex-switch-known"),
            AuthKind = AccountAuthKind.CompatibleApi
        };
        var knownAccountKey = QuotaAccountIdentity.CreateKey(knownAccount);
        var currentScope = QuotaAccountIdentity.CreateManagerScopeKey(
            Path.Combine(Path.GetTempPath(), "codex-manager-current"));
        var foreignScope = QuotaAccountIdentity.CreateManagerScopeKey(
            Path.Combine(Path.GetTempPath(), "codex-manager-foreign"));
        var start = DateTimeOffset.Parse("2026-07-17T08:00:00Z");
        var trustFixtures = new List<UsageSwitchEvent>
        {
            new()
            {
                AccountName = knownAccount.Name,
                AccountKey = knownAccountKey,
                ManagerScopeKey = currentScope,
                SwitchedAtUtc = start.ToString("O"),
                Source = "switch"
            },
            new()
            {
                AccountName = "example",
                SwitchedAtUtc = start.AddMinutes(1).ToString("O"),
                Source = "detected"
            },
            new()
            {
                AccountName = knownAccount.Name,
                AccountKey = "STALE-IDENTITY",
                ManagerScopeKey = foreignScope,
                SwitchedAtUtc = start.AddMinutes(2).ToString("O"),
                Source = "detected"
            },
            new()
            {
                AccountName = "deleted-account",
                SwitchedAtUtc = start.AddMinutes(3).ToString("O"),
                Source = "switch"
            },
            new()
            {
                AccountName = "deleted-manual-account",
                SwitchedAtUtc = start.AddMinutes(4).ToString("O"),
                Source = "manual"
            },
            new()
            {
                AccountName = "old-known-name",
                AccountKey = knownAccountKey,
                ManagerScopeKey = foreignScope,
                SwitchedAtUtc = start.AddMinutes(5).ToString("O"),
                Source = "detected"
            }
        };
        var trusted = FilterTrustedSwitchEvents(trustFixtures, [knownAccount])
            .OrderBy(entry => entry.GetSwitchedAtUtc())
            .ToList();
        if (trusted.Count != 5 ||
            trusted.Any(entry => entry.AccountName == "example") ||
            !trusted.Any(entry => entry.AccountKey == "STALE-IDENTITY") ||
            !trusted.Any(entry => entry.AccountName == "deleted-account") ||
            !trusted.Any(entry => entry.AccountName == "deleted-manual-account"))
        {
            throw new InvalidOperationException(
                "Unknown detected switches must be ignored while current-name fallback and explicit deleted-account history are retained.");
        }

        var trustedTimeline = trusted
            .Select(entry => new UsageSwitchPoint(entry, entry.GetSwitchedAtUtc()))
            .ToArray();
        var responseOnly = new UsageEvent
        {
            TimestampUtc = start.AddMinutes(1).AddSeconds(30),
            Source = UsageEventSource.Natural,
            ResponseUsageMatched = true,
            ResponseUsageMatchKind = "response-only"
        };
        AssignUsageToActiveSwitch(responseOnly, trustedTimeline, [knownAccount]);
        if (responseOnly.AccountName != knownAccount.Name ||
            responseOnly.ActivationEpochUtc != start)
        {
            throw new InvalidOperationException(
                "Response-only usage must inherit the last trusted account switch rather than an unknown detected boundary.");
        }

        var afterDeletedSwitch = new UsageEvent
        {
            TimestampUtc = start.AddMinutes(3).AddSeconds(1),
            InputTokens = 1,
            TotalTokens = 1
        };
        var afterDeletedManual = new UsageEvent
        {
            TimestampUtc = start.AddMinutes(4).AddSeconds(1),
            InputTokens = 1,
            TotalTokens = 1
        };
        AssignUsageToActiveSwitch(afterDeletedSwitch, trustedTimeline, [knownAccount]);
        AssignUsageToActiveSwitch(afterDeletedManual, trustedTimeline, [knownAccount]);
        if (afterDeletedSwitch.AccountName != "deleted-account" ||
            afterDeletedManual.AccountName != "deleted-manual-account")
        {
            throw new InvalidOperationException(
                "Explicit manual/switch history for a deleted account must remain intact instead of being rewritten.");
        }

        var unassignedReport = new UsageReport();
        var knownSummaries = new Dictionary<string, AccountUsageSummary>(StringComparer.OrdinalIgnoreCase)
        {
            [knownAccount.Name] = new() { AccountName = knownAccount.Name }
        };
        var beforeFixtures = start.AddDays(-1);
        AddUsage(
            unassignedReport,
            knownSummaries,
            afterDeletedSwitch,
            beforeFixtures,
            beforeFixtures,
            beforeFixtures,
            beforeFixtures,
            beforeFixtures,
            beforeFixtures);
        AddUsage(
            unassignedReport,
            knownSummaries,
            afterDeletedManual,
            beforeFixtures,
            beforeFixtures,
            beforeFixtures,
            beforeFixtures,
            beforeFixtures,
            beforeFixtures);
        if (unassignedReport.UnassignedMonth.Events != 2 ||
            knownSummaries[knownAccount.Name].Month.Events != 0)
        {
            throw new InvalidOperationException(
                "Deleted manual/switch account history must flow into Unassigned usage buckets.");
        }

        var afterKnownIdentity = new UsageEvent { TimestampUtc = start.AddMinutes(5).AddSeconds(1) };
        AssignUsageToActiveSwitch(afterKnownIdentity, trustedTimeline, [knownAccount]);
        if (afterKnownIdentity.AccountName != knownAccount.Name)
        {
            throw new InvalidOperationException(
                "Stable account identity must survive manager scope and account-name changes.");
        }

        ValidateJuly17UnknownDetectedRegression();
    }

    internal static void ValidateSessionAccountAttribution()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-session-account-attribution-" + Guid.NewGuid().ToString("N"));
        var sessionsRoot = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        var now = DateTimeOffset.UtcNow;
        var firstAccount = new AccountRecord
        {
            Name = "clock-account-a",
            CodexHome = Path.Combine(root, "account-a"),
            AuthKind = AccountAuthKind.AccessToken
        };
        var secondAccount = new AccountRecord
        {
            Name = "clock-account-b",
            CodexHome = Path.Combine(root, "account-b"),
            AuthKind = AccountAuthKind.AccessToken
        };
        var switchEvents = new UsageSwitchEvent[]
        {
            new()
            {
                AccountName = firstAccount.Name,
                AccountKey = QuotaAccountIdentity.CreateKey(firstAccount),
                SwitchedAtUtc = now.AddMinutes(-120).ToString("O"),
                Source = "switch"
            },
            new()
            {
                AccountName = secondAccount.Name,
                AccountKey = QuotaAccountIdentity.CreateKey(secondAccount),
                SwitchedAtUtc = now.AddMinutes(-60).ToString("O"),
                Source = "switch"
            }
        };

        var sessionStartedAt = now.AddMinutes(-50);
        var eventTimestamp = now.AddMinutes(-110);
        var sessionPath = Path.Combine(sessionsRoot, "rollout-clock-adjusted.jsonl");
        File.WriteAllLines(
            sessionPath,
            [
                JsonSerializer.Serialize(new
                {
                    timestamp = sessionStartedAt.ToString("O"),
                    type = "session_meta",
                    payload = new
                    {
                        id = Guid.NewGuid().ToString(),
                        timestamp = sessionStartedAt.ToString("O"),
                        thread_source = "app"
                    }
                }),
                MakeTokenCountFixture(eventTimestamp, 300, 12)
            ]);

        try
        {
            var parsedEvent = EnumerateUsageEventsFromFile(sessionPath, null).Single();
            if (parsedEvent.SessionStartedAtUtc != sessionStartedAt)
            {
                throw new InvalidOperationException(
                    $"Session metadata parsing failed: expected {sessionStartedAt:O}, got {parsedEvent.SessionStartedAtUtc:O}.");
            }

            var timeline = switchEvents
                .Select(entry => new UsageSwitchPoint(entry, entry.GetSwitchedAtUtc()))
                .ToArray();
            AssignUsageToActiveSwitch(parsedEvent, timeline, [firstAccount, secondAccount]);
            if (parsedEvent.AccountName != secondAccount.Name ||
                parsedEvent.ActivationEpochUtc != switchEvents[1].GetSwitchedAtUtc())
            {
                throw new InvalidOperationException(
                    "An event timestamp that moved backward by more than five minutes must follow its session account.");
            }

            var toleranceSwitches = new UsageSwitchEvent[]
            {
                new()
                {
                    AccountName = firstAccount.Name,
                    AccountKey = QuotaAccountIdentity.CreateKey(firstAccount),
                    SwitchedAtUtc = now.AddMinutes(-70).ToString("O"),
                    Source = "switch"
                },
                new()
                {
                    AccountName = secondAccount.Name,
                    AccountKey = QuotaAccountIdentity.CreateKey(secondAccount),
                    SwitchedAtUtc = now.AddMinutes(-57).ToString("O"),
                    Source = "switch"
                }
            };
            var toleranceTimeline = toleranceSwitches
                .Select(entry => new UsageSwitchPoint(entry, entry.GetSwitchedAtUtc()))
                .ToArray();
            var minorRollback = new UsageEvent
            {
                SessionStartedAtUtc = now.AddMinutes(-55),
                TimestampUtc = now.AddMinutes(-59),
                InputTokens = 1,
                TotalTokens = 1
            };
            var significantRollback = new UsageEvent
            {
                SessionStartedAtUtc = now.AddMinutes(-55),
                TimestampUtc = now.AddMinutes(-61),
                InputTokens = 1,
                TotalTokens = 1
            };
            AssignUsageToActiveSwitch(minorRollback, toleranceTimeline, [firstAccount, secondAccount]);
            AssignUsageToActiveSwitch(significantRollback, toleranceTimeline, [firstAccount, secondAccount]);
            if (minorRollback.AccountName != firstAccount.Name ||
                minorRollback.ActivationEpochUtc != toleranceSwitches[0].GetSwitchedAtUtc() ||
                significantRollback.AccountName != secondAccount.Name ||
                significantRollback.ActivationEpochUtc != toleranceSwitches[1].GetSwitchedAtUtc())
            {
                throw new InvalidOperationException(
                    "Only a session event clock rollback beyond the five-minute tolerance may use the session-start boundary.");
            }

            ValidateCachedSessionSwitchAttribution(
                root,
                firstAccount,
                secondAccount,
                new DateTimeOffset(2099, 7, 15, 12, 0, 0, TimeSpan.Zero));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void ValidateCachedSessionSwitchAttribution(
        string root,
        AccountRecord firstAccount,
        AccountRecord secondAccount,
        DateTimeOffset start)
    {
        var sessionsRoot = Path.Combine(root, "cached-sessions");
        Directory.CreateDirectory(sessionsRoot);
        var sessionPath = Path.Combine(sessionsRoot, "rollout-cached-a-b-a.jsonl");
        File.WriteAllText(sessionPath, "{}" + Environment.NewLine);
        File.SetLastWriteTimeUtc(sessionPath, start.AddMinutes(40).UtcDateTime);
        if (!TryGetUsageFileIdentity(sessionPath, out var identity))
        {
            throw new InvalidOperationException("Could not create the cached session attribution fixture.");
        }

        var switchEvents = new UsageSwitchEvent[]
        {
            new()
            {
                AccountName = firstAccount.Name,
                AccountKey = QuotaAccountIdentity.CreateKey(firstAccount),
                SwitchedAtUtc = start.ToString("O"),
                Source = "switch"
            },
            new()
            {
                AccountName = secondAccount.Name,
                AccountKey = QuotaAccountIdentity.CreateKey(secondAccount),
                SwitchedAtUtc = start.AddMinutes(10).ToString("O"),
                Source = "switch"
            },
            new()
            {
                AccountName = firstAccount.Name,
                AccountKey = QuotaAccountIdentity.CreateKey(firstAccount),
                SwitchedAtUtc = start.AddMinutes(20).ToString("O"),
                Source = "switch"
            }
        };
        File.WriteAllText(
            Path.Combine(root, "usage-account-switches.json"),
            JsonSerializer.Serialize(switchEvents, JsonOptions));

        var sessionStartedAt = start.AddMinutes(1);
        var staleCachedEvents = new List<UsageEvent>
        {
            new()
            {
                AccountName = firstAccount.Name,
                SessionStartedAtUtc = sessionStartedAt,
                TimestampUtc = start.AddMinutes(5),
                InputTokens = 10,
                TotalTokens = 10
            },
            new()
            {
                AccountName = firstAccount.Name,
                SessionStartedAtUtc = sessionStartedAt,
                TimestampUtc = start.AddMinutes(15),
                InputTokens = 20,
                TotalTokens = 20
            },
            new()
            {
                AccountName = firstAccount.Name,
                SessionStartedAtUtc = sessionStartedAt,
                TimestampUtc = start.AddMinutes(25),
                InputTokens = 30,
                TotalTokens = 30
            }
        };
        var cacheDocument = new PersistentUsageCacheDocument
        {
            SchemaVersion = PersistentUsageCacheSchemaVersion,
            Files =
            [
                new PersistentUsageCacheEntry
                {
                    SessionsRoot = Path.GetFullPath(sessionsRoot),
                    FullPath = identity.FullPath,
                    Length = identity.Length,
                    LastWriteTimeUtc = identity.LastWriteTimeUtc,
                    ParsedLength = identity.Length,
                    TailFingerprint = ComputeTailFingerprint(identity.FullPath, identity.Length),
                    ParserState = UsageParserState.Empty,
                    Events = staleCachedEvents
                }
            ]
        };
        var cachePath = Path.Combine(root, ".cache", "usage-file-index-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cacheDocument, CacheJsonOptions));

        var restartedTracker = new UsageTracker(root);
        var report = restartedTracker.BuildReport(
            [firstAccount, secondAccount],
            sessionsRoot,
            start.AddMinutes(40));
        var firstSummary = report.Accounts.Single(summary => summary.AccountName == firstAccount.Name);
        var secondSummary = report.Accounts.Single(summary => summary.AccountName == secondAccount.Name);
        if (firstSummary.Month.Events != 2 ||
            firstSummary.Month.TotalTokens != 40 ||
            secondSummary.Month.Events != 1 ||
            secondSummary.Month.TotalTokens != 20 ||
            firstSummary.Timeline.Any(usage => usage.AccountName != firstAccount.Name) ||
            secondSummary.Timeline.Any(usage => usage.AccountName != secondAccount.Name) ||
            !File.Exists(sessionPath))
        {
            throw new InvalidOperationException(
                "A stale persisted cache must be re-attributed across A -> B -> A switches during BuildReport without deleting the session log.");
        }
    }

    private static void ValidateJuly17UnknownDetectedRegression()
    {
        const string monthlyPoolName = "月额度共用号池";
        var monthlyPool = new AccountRecord
        {
            Name = monthlyPoolName,
            CodexHome = Path.Combine(Path.GetTempPath(), "codex-july17-monthly-pool"),
            AuthKind = AccountAuthKind.CompatibleApi
        };
        var monthlyPoolEpoch = DateTimeOffset.Parse("2026-07-17T06:15:16.5258715Z");
        var exampleDetection = DateTimeOffset.Parse("2026-07-17T08:18:42.0972915Z");
        var monthlyPoolRedetection = DateTimeOffset.Parse("2026-07-17T08:30:05.9633819Z");
        var realSwitchEvents = NormalizeSwitchEvents(
        [
            new()
            {
                AccountName = monthlyPoolName,
                SwitchedAtUtc = monthlyPoolEpoch.ToString("O"),
                Source = "switch"
            },
            new()
            {
                AccountName = "example",
                SwitchedAtUtc = exampleDetection.ToString("O"),
                Source = "detected"
            },
            new()
            {
                AccountName = monthlyPoolName,
                SwitchedAtUtc = monthlyPoolRedetection.ToString("O"),
                Source = "detected"
            }
        ]);
        var trusted = FilterTrustedSwitchEvents(realSwitchEvents, [monthlyPool])
            .OrderBy(entry => entry.GetSwitchedAtUtc())
            .ToList();
        if (trusted.Count != 2 || trusted.Any(entry => entry.AccountName == "example"))
        {
            throw new InvalidOperationException(
                "The real 2026-07-17 unknown example detection must not become an account boundary.");
        }

        string[] realUsageTimestamps =
        [
            "2026-07-17T08:18:42.491Z",
            "2026-07-17T08:19:23.418Z",
            "2026-07-17T08:19:53.188Z",
            "2026-07-17T08:20:21.534Z",
            "2026-07-17T08:20:42.767Z",
            "2026-07-17T08:21:48.099Z",
            "2026-07-17T08:22:16.576Z",
            "2026-07-17T08:22:38.625Z",
            "2026-07-17T08:23:09.742Z",
            "2026-07-17T08:23:37.221Z",
            "2026-07-17T08:23:58.828Z",
            "2026-07-17T08:24:37.426Z",
            "2026-07-17T08:24:56.465Z",
            "2026-07-17T08:25:32.082Z",
            "2026-07-17T08:25:59.542Z",
            "2026-07-17T08:26:54.251Z",
            "2026-07-17T08:27:19.302Z",
            "2026-07-17T08:27:42.141Z",
            "2026-07-17T08:28:33.028Z",
            "2026-07-17T08:29:03.840Z",
            "2026-07-17T08:29:24.107Z",
            "2026-07-17T08:29:47.032Z"
        ];
        var trustedTimeline = trusted
            .Select(entry => new UsageSwitchPoint(entry, entry.GetSwitchedAtUtc()))
            .ToArray();
        var actualEvents = realUsageTimestamps
            .Select(timestamp => new UsageEvent { TimestampUtc = DateTimeOffset.Parse(timestamp) })
            .ToArray();
        foreach (var usage in actualEvents)
        {
            AssignUsageToActiveSwitch(usage, trustedTimeline, [monthlyPool]);
        }

        var chinaOffset = TimeSpan.FromHours(8);
        var expectedFirstLocal = DateTimeOffset.Parse("2026-07-17T16:18:42.491+08:00");
        var expectedLastLocal = DateTimeOffset.Parse("2026-07-17T16:29:47.032+08:00");
        if (actualEvents.Length != 22 ||
            actualEvents[0].TimestampUtc.ToOffset(chinaOffset) != expectedFirstLocal ||
            actualEvents[^1].TimestampUtc.ToOffset(chinaOffset) != expectedLastLocal ||
            actualEvents.Any(usage => usage.AccountName != monthlyPoolName) ||
            actualEvents.Any(usage => usage.ActivationEpochUtc != monthlyPoolEpoch))
        {
            throw new InvalidOperationException(
                "All 22 real usage records from 2026-07-17 16:18:42 through 16:29:47 China time must remain attributed to 月额度共用号池.");
        }

        var responseOnly = new UsageEvent
        {
            TimestampUtc = actualEvents[^1].TimestampUtc,
            Source = UsageEventSource.Natural,
            ResponseUsageMatched = true,
            ResponseUsageMatchKind = "response-only"
        };
        AssignUsageToActiveSwitch(responseOnly, trustedTimeline, [monthlyPool]);
        if (responseOnly.AccountName != monthlyPoolName ||
            responseOnly.ActivationEpochUtc != monthlyPoolEpoch)
        {
            throw new InvalidOperationException(
                "Response-only synthetic usage must use the same trusted 2026-07-17 attribution timeline.");
        }
    }

    private static void AddUsage(
        UsageReport report,
        Dictionary<string, AccountUsageSummary> summaries,
        UsageEvent usage,
        DateTimeOffset todayStart,
        DateTimeOffset oneHourStart,
        DateTimeOffset fiveHoursStart,
        DateTimeOffset dayStart,
        DateTimeOffset weekStart,
        DateTimeOffset monthStart)
    {
        var target = !string.IsNullOrWhiteSpace(usage.AccountName) &&
            summaries.TryGetValue(usage.AccountName, out var summary)
            ? summary
            : null;
        if (target != null)
        {
            target.Timeline.Add(usage);
        }

        // Codex can emit the same cumulative token snapshot more than once while updating
        // its official rate-limit percentage. Keep that boundary in the account timeline,
        // but never let the zero-cost snapshot inflate any token or model bucket.
        if (usage.Source == UsageEventSource.OfficialSnapshot)
        {
            UpdateLatestQuotaSnapshot(target, usage);
            return;
        }

        if (usage.TimestampUtc >= monthStart.ToUniversalTime())
        {
            if (target == null)
            {
                report.UnassignedMonth.Add(usage);
            }
            else
            {
                target.Month.Add(usage);
            }
        }

        if (usage.TimestampUtc >= weekStart.ToUniversalTime())
        {
            if (target == null)
            {
                report.UnassignedWeek.Add(usage);
            }
            else
            {
                target.Week.Add(usage);
            }
        }

        if (usage.TimestampUtc >= dayStart.ToUniversalTime())
        {
            if (target == null)
            {
                report.UnassignedDay.Add(usage);
            }
            else
            {
                target.Day.Add(usage);
            }
        }

        if (usage.TimestampUtc >= fiveHoursStart.ToUniversalTime())
        {
            if (target == null)
            {
                report.UnassignedFiveHours.Add(usage);
            }
            else
            {
                target.FiveHours.Add(usage);
            }
        }

        if (usage.TimestampUtc >= oneHourStart.ToUniversalTime())
        {
            if (target == null)
            {
                report.UnassignedHour.Add(usage);
            }
            else
            {
                target.Hour.Add(usage);
            }
        }

        if (usage.TimestampUtc.ToLocalTime() >= todayStart)
        {
            if (target == null)
            {
                report.UnassignedToday.Add(usage);
            }
            else
            {
                target.Today.Add(usage);
            }
        }

        UpdateLatestQuotaSnapshot(target, usage);
    }

    private static void UpdateLatestQuotaSnapshot(AccountUsageSummary? target, UsageEvent usage)
    {
        if (target != null &&
            (usage.RateLimitUsedPercent.HasValue ||
             usage.RateLimitWindowMinutes.HasValue ||
             usage.RateLimitResetAtUtc.HasValue ||
             usage.SecondaryRateLimitUsedPercent.HasValue ||
             usage.SecondaryRateLimitWindowMinutes.HasValue ||
             usage.SecondaryRateLimitResetAtUtc.HasValue ||
             usage.CreditBalance != null ||
             usage.IndividualLimit != null ||
             !string.IsNullOrWhiteSpace(usage.PlanType)) &&
            (!target.RateLimitObservedAtUtc.HasValue || usage.TimestampUtc >= target.RateLimitObservedAtUtc.Value))
        {
            // Some token_count messages contain only one quota window. Update only the
            // values actually present so a newer primary-only message cannot erase the
            // last valid weekly/monthly green line (and vice versa).
            target.RateLimitUsedPercent = usage.RateLimitUsedPercent ?? target.RateLimitUsedPercent;
            target.RateLimitWindowMinutes = usage.RateLimitWindowMinutes ?? target.RateLimitWindowMinutes;
            target.RateLimitResetAtUtc = usage.RateLimitResetAtUtc ?? target.RateLimitResetAtUtc;
            target.SecondaryRateLimitUsedPercent =
                usage.SecondaryRateLimitUsedPercent ?? target.SecondaryRateLimitUsedPercent;
            target.SecondaryRateLimitWindowMinutes =
                usage.SecondaryRateLimitWindowMinutes ?? target.SecondaryRateLimitWindowMinutes;
            target.SecondaryRateLimitResetAtUtc =
                usage.SecondaryRateLimitResetAtUtc ?? target.SecondaryRateLimitResetAtUtc;
            target.CreditBalance = usage.CreditBalance ?? target.CreditBalance;
            target.IndividualLimit = usage.IndividualLimit ?? target.IndividualLimit;
            if (!string.IsNullOrWhiteSpace(usage.PlanType))
            {
                target.PlanType = usage.PlanType;
            }
            target.RateLimitObservedAtUtc = usage.TimestampUtc;
        }
    }

    private IReadOnlyList<UsageEvent> EnrichCacheWriteTokens(
        IReadOnlyList<UsageEvent> usageEvents,
        string sessionsRoot,
        DateTimeOffset timelineStart,
        bool refreshDatabase)
    {
        string databasePath;
        try
        {
            var normalizedSessionsRoot = Path.GetFullPath(sessionsRoot);
            var codexHome = Directory.GetParent(normalizedSessionsRoot)?.FullName;
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                return usageEvents;
            }
            databasePath = Path.Combine(codexHome, "logs_2.sqlite");
        }
        catch
        {
            return usageEvents;
        }

        if (!File.Exists(databasePath))
        {
            return usageEvents;
        }

        var retentionFloor = DateTimeOffset.UtcNow.AddDays(-45);
        var requiredFromUtc = timelineStart.ToUniversalTime().AddMinutes(-5);
        if (requiredFromUtc < retentionFloor)
        {
            requiredFromUtc = retentionFloor;
        }

        if (!refreshDatabase)
        {
            return ApplyAndPersistResponseUsageMatches(
                usageEvents,
                LoadPersistedCacheWriteResponsesOnly(databasePath, requiredFromUtc),
                timelineStart.ToUniversalTime());
        }

        var responses = LoadCacheWriteResponses(databasePath, requiredFromUtc);
        return ApplyAndPersistResponseUsageMatches(
            usageEvents,
            responses,
            timelineStart.ToUniversalTime());
    }

    private IReadOnlyList<UsageEvent> ApplyAndPersistResponseUsageMatches(
        IReadOnlyList<UsageEvent> usageEvents,
        IReadOnlyList<CacheWriteResponseRecord> responses,
        DateTimeOffset syntheticFromUtc)
    {
        lock (_cacheWriteIndexGate)
        {
            var result = ApplyResponseUsageMatches(usageEvents, responses);
            if (result.AuditRecordChangeCount > 0)
            {
                _cacheWriteIndexDirty = true;
                // A recorded mismatch is audit data rather than a disposable performance cache.
                // Flush it immediately; ordinary exact/missing-cache-write matches keep the normal
                // ten-second batching behavior.
                PersistCacheWriteIndexIfDue(force: result.DifferenceCount > 0);
            }

            var syntheticEvents = CreateResponseOnlyUsageEvents(
                result.UnmatchedResponses,
                syntheticFromUtc);
            if (syntheticEvents.Count == 0)
            {
                return usageEvents;
            }

            return usageEvents.Concat(syntheticEvents).ToArray();
        }
    }

    private IReadOnlyList<CacheWriteResponseRecord> LoadPersistedCacheWriteResponsesOnly(
        string databasePath,
        DateTimeOffset requiredFromUtc)
    {
        lock (_cacheWriteIndexGate)
        {
            EnsurePersistentCacheWriteIndexLoaded();
            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(databasePath);
            }
            catch
            {
                return [];
            }

            if (!string.Equals(
                    _cacheWriteDatabasePath,
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            return _cacheWriteResponses.Values
                .Where(item => item.TimestampUtc >= requiredFromUtc)
                .OrderBy(item => item.TimestampUtc)
                .ToArray();
        }
    }

    private IReadOnlyList<CacheWriteResponseRecord> LoadCacheWriteResponses(
        string databasePath,
        DateTimeOffset requiredFromUtc)
    {
        lock (_cacheWriteIndexGate)
        {
            EnsurePersistentCacheWriteIndexLoaded();
            var normalizedPath = Path.GetFullPath(databasePath);
            var pathChanged = !string.Equals(
                _cacheWriteDatabasePath,
                normalizedPath,
                StringComparison.OrdinalIgnoreCase);
            var requiresFullScan = _cacheWriteIndexNeedsNullableRescan ||
                pathChanged ||
                !_cacheWriteLoadedFromUtc.HasValue ||
                _cacheWriteLoadedFromUtc.Value > requiredFromUtc;

            var now = DateTimeOffset.UtcNow;
            if (!requiresFullScan &&
                _lastCacheWriteDatabaseRefreshUtc != default &&
                now - _lastCacheWriteDatabaseRefreshUtc < CacheWriteDatabaseRefreshInterval)
            {
                return _cacheWriteResponses.Values
                    .Where(item => item.TimestampUtc >= requiredFromUtc)
                    .OrderBy(item => item.TimestampUtc)
                    .ToArray();
            }
            _lastCacheWriteDatabaseRefreshUtc = now;

            try
            {
                CodexCliService.EnsureSqliteProvider();
                using var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = normalizedPath,
                        Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Private,
                        Pooling = false
                    }.ToString());
                connection.Open();

                var throughId = ReadMaximumLogId(connection);
                if (!requiresFullScan && throughId < _cacheWriteLastScannedLogId)
                {
                    requiresFullScan = true;
                }

                if (requiresFullScan || throughId > _cacheWriteLastScannedLogId)
                {
                    var loadedFromUtc = requiresFullScan
                        ? requiredFromUtc
                        : _cacheWriteLoadedFromUtc ?? requiredFromUtc;
                    var records = ReadCacheWriteResponses(
                        connection,
                        loadedFromUtc,
                        requiresFullScan ? 0L : _cacheWriteLastScannedLogId,
                        throughId,
                        requiresFullScan);

                    var reconciliations = pathChanged
                        ? new Dictionary<string, UsageResponseReconciliationAudit?>(StringComparer.Ordinal)
                        : _cacheWriteResponses.Values
                            .Where(item => item.Reconciliation != null)
                            .ToDictionary(
                                item => item.ResponseId,
                                item => item.Reconciliation,
                                StringComparer.Ordinal);

                    if (requiresFullScan)
                    {
                        // Codex may compact or replace rows inside the same WAL database, causing
                        // MAX(id) to move backwards. Persisted responses are durable usage evidence:
                        // merge every same-path rescan and clear only for a genuinely different DB.
                        if (pathChanged)
                        {
                            _cacheWriteResponses.Clear();
                            _cacheWriteLoadedFromUtc = loadedFromUtc;
                        }
                        else if (!_cacheWriteLoadedFromUtc.HasValue ||
                                 loadedFromUtc < _cacheWriteLoadedFromUtc.Value)
                        {
                            _cacheWriteLoadedFromUtc = loadedFromUtc;
                        }
                    }
                    foreach (var record in records)
                    {
                        if (reconciliations.TryGetValue(record.ResponseId, out var reconciliation))
                        {
                            record.Reconciliation = reconciliation;
                        }
                        _cacheWriteResponses[record.ResponseId] = record;
                    }

                    _cacheWriteDatabasePath = normalizedPath;
                    _cacheWriteLastScannedLogId = throughId;
                    _cacheWriteIndexNeedsNullableRescan = false;
                    // Report lookbacks vary by view. They must not delete durable responses or
                    // move LoadedFromUtc forward; pruning uses one independent retention policy.
                    PruneCacheWriteResponses(now.AddDays(-45));
                    _cacheWriteIndexDirty = true;
                    PersistCacheWriteIndexIfDue(force: requiresFullScan);
                }
                PersistCacheWriteIndexIfDue(force: false);
            }
            catch
            {
                // The Codex process continuously writes this WAL database. A transient read
                // failure must never block the regular JSONL usage report; the next refresh
                // retries and any already-persisted response metadata remains usable.
            }

            return _cacheWriteResponses.Values
                .Where(item => item.TimestampUtc >= requiredFromUtc)
                .OrderBy(item => item.TimestampUtc)
                .ToArray();
        }
    }

    private static long ReadMaximumLogId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM logs;";
        command.CommandTimeout = 10;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<CacheWriteResponseRecord> ReadCacheWriteResponses(
        SqliteConnection connection,
        DateTimeOffset fromUtc,
        long afterId,
        long throughId,
        bool fullScan)
    {
        if (throughId <= 0L || (!fullScan && throughId <= afterId))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        var sourceRange = fullScan
            ? "ts >= $from AND id <= $through"
            : "id > $after AND id <= $through";
        var indexHint = fullScan ? " INDEXED BY idx_logs_ts" : string.Empty;
        command.CommandText = $"""
            WITH candidates AS (
                SELECT id, ts, ts_nanos, substr(feedback_log_body, 12) AS event_json
                FROM logs{indexHint}
                WHERE {sourceRange}
                  AND target = 'codex_api::sse::responses'
                  AND substr(feedback_log_body, 1, 11) = 'SSE event: '
                  AND instr(substr(feedback_log_body, 12, 96), '"response.completed"') > 0
                  AND json_valid(substr(feedback_log_body, 12))
            )
            SELECT id,
                   ts,
                   ts_nanos,
                   json_extract(event_json, '$.response.id'),
                   json_extract(event_json, '$.response.model'),
                   json_extract(event_json, '$.response.usage.input_tokens'),
                   json_extract(event_json, '$.response.usage.input_tokens_details.cached_tokens'),
                   json_extract(event_json, '$.response.usage.input_tokens_details.cache_write_tokens'),
                   json_extract(event_json, '$.response.usage.output_tokens'),
                   json_extract(event_json, '$.response.usage.output_tokens_details.reasoning_tokens'),
                   json_extract(event_json, '$.response.usage.total_tokens')
            FROM candidates
            WHERE json_extract(event_json, '$.type') = 'response.completed';
            """;
        command.CommandTimeout = 45;
        command.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$after", afterId);
        command.Parameters.AddWithValue("$through", throughId);

        var result = new List<CacheWriteResponseRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var responseId = ReadNullableText(reader, 3);
            var model = ReadNullableText(reader, 4);
            var inputTokens = ReadNullableInt64(reader, 5);
            var cachedTokens = ReadNullableInt64(reader, 6);
            var cacheWriteTokens = ReadNullableInt64(reader, 7);
            var outputTokens = ReadNullableInt64(reader, 8);
            var reasoningTokens = ReadNullableInt64(reader, 9) ?? 0L;
            var totalTokens = ReadNullableInt64(reader, 10);
            if (string.IsNullOrWhiteSpace(responseId) ||
                inputTokens is not { } input || input < 0L ||
                cachedTokens is not { } cached || cached < 0L ||
                outputTokens is not { } output || output < 0L ||
                totalTokens is not { } total || total < 0L ||
                cached > input ||
                cacheWriteTokens is long cacheWrite &&
                (cacheWrite < 0L || cacheWrite > input - cached))
            {
                continue;
            }

            var unixSeconds = reader.GetInt64(1);
            var nanos = Math.Clamp(reader.GetInt64(2), 0L, 999_999_999L);
            result.Add(new CacheWriteResponseRecord
            {
                LogId = reader.GetInt64(0),
                ResponseId = responseId,
                TimestampUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).AddTicks(nanos / 100L),
                Model = model,
                InputTokens = input,
                CachedInputTokens = cached,
                CacheWriteTokens = cacheWriteTokens,
                OutputTokens = output,
                ReasoningOutputTokens = Math.Max(0L, reasoningTokens),
                TotalTokens = total
            });
        }
        return result;
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static ResponseUsageReconciliationResult ApplyResponseUsageMatches(
        IReadOnlyList<UsageEvent> usageEvents,
        IReadOnlyList<CacheWriteResponseRecord> responses)
    {
        var orderedResponses = responses
            .Where(item => !string.IsNullOrWhiteSpace(item.ResponseId))
            .GroupBy(item => item.ResponseId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.LogId)
                .ThenByDescending(item => item.TimestampUtc)
                .First())
            .OrderBy(item => item.TimestampUtc)
            .ToArray();
        var responsesByKey = orderedResponses
            .GroupBy(CreateResponseUsageMatchKey)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());
        var usedResponseIds = new HashSet<string>(StringComparer.Ordinal);
        var matched = 0;
        var differences = 0;
        var auditChanges = 0;
        var unmatchedUsages = new List<UsageEvent>();

        void Reconcile(
            UsageEvent usage,
            CacheWriteResponseRecord response,
            string matchKind)
        {
            var audit = BuildReconciliationAudit(usage, response, matchKind);
            usage.ResponseUsageMatched = true;
            usage.ResponseUsageMatchKind = matchKind;
            usage.ResponseUsageDifferenceFields = audit.DifferenceFields.ToArray();
            usage.ResponseUsageResponseTimestampUtc = response.TimestampUtc;

            // response.completed.usage is the provider-facing, per-response usage record.
            // Once correlation is strong enough, it is authoritative for the whole tuple;
            // JSONL remains the durable event/timeline source rather than a second billable copy.
            if (!string.IsNullOrWhiteSpace(response.Model))
            {
                usage.Model = response.Model;
            }
            usage.InputTokens = response.InputTokens;
            usage.CachedInputTokens = response.CachedInputTokens;
            // Older/alternate providers can omit cache_write_tokens. In that case the
            // response remains authoritative for every available counter, while an existing
            // JSONL cache-write value is more informative than replacing it with unknown.
            usage.CacheWriteTokens = response.CacheWriteTokens ?? usage.CacheWriteTokens;
            usage.OutputTokens = response.OutputTokens;
            usage.ReasoningOutputTokens = response.ReasoningOutputTokens;
            usage.TotalTokens = response.TotalTokens;

            if (!ReconciliationAuditEqual(response.Reconciliation, audit))
            {
                response.Reconciliation = audit;
                auditChanges++;
            }
            if (audit.DifferenceFields.Count > 0)
            {
                differences++;
            }
            usedResponseIds.Add(response.ResponseId);
            matched++;
        }

        var orderedUsages = usageEvents
            .Where(item =>
                item.Source != UsageEventSource.OfficialSnapshot &&
                !item.EquivalentCostOverrideUsd.HasValue)
            .OrderBy(item => item.TimestampUtc)
            .ToArray();

        // Reserve every exact tuple first. This prevents an earlier partially-corrupted event
        // from taking a response that has a later, unambiguous JSONL counterpart.
        foreach (var usage in orderedUsages)
        {
            var key = CreateResponseUsageMatchKey(usage);
            var exact = responsesByKey.TryGetValue(key, out var exactCandidates)
                ? SelectClosestResponse(usage, exactCandidates, usedResponseIds)
                : null;
            if (exact == null)
            {
                unmatchedUsages.Add(usage);
                continue;
            }
            Reconcile(usage, exact, "exact");
        }

        var responseStartIndex = 0;
        var responseEndIndex = 0;
        foreach (var usage in unmatchedUsages)
        {
            // A tolerant match can only be within the same -30s/+5min response window.
            // Keeping a sliding range avoids rechecking every historical response for every
            // unmatched token event once the local session archive grows large.
            var earliestResponseTimestamp = usage.TimestampUtc.AddSeconds(-300D);
            var latestResponseTimestamp = usage.TimestampUtc.AddSeconds(30D);
            while (responseStartIndex < orderedResponses.Length &&
                   orderedResponses[responseStartIndex].TimestampUtc < earliestResponseTimestamp)
            {
                responseStartIndex++;
            }
            if (responseEndIndex < responseStartIndex)
            {
                responseEndIndex = responseStartIndex;
            }
            while (responseEndIndex < orderedResponses.Length &&
                   orderedResponses[responseEndIndex].TimestampUtc <= latestResponseTimestamp)
            {
                responseEndIndex++;
            }

            var tolerant = SelectTolerantResponse(
                usage,
                orderedResponses,
                responseStartIndex,
                responseEndIndex,
                usedResponseIds);
            if (tolerant != null)
            {
                Reconcile(usage, tolerant, "tolerant");
            }
        }

        var unmatchedResponses = orderedResponses
            .Where(item => !usedResponseIds.Contains(item.ResponseId))
            .ToArray();
        return new ResponseUsageReconciliationResult(
            matched,
            differences,
            auditChanges,
            unmatchedResponses);
    }

    private static IReadOnlyList<UsageEvent> CreateResponseOnlyUsageEvents(
        IReadOnlyList<CacheWriteResponseRecord> responses,
        DateTimeOffset fromUtc)
    {
        return responses
            .Where(response =>
                !string.IsNullOrWhiteSpace(response.ResponseId) &&
                response.TimestampUtc >= fromUtc)
            .GroupBy(response => response.ResponseId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(response => response.LogId)
                .ThenByDescending(response => response.TimestampUtc)
                .First())
            .OrderBy(response => response.TimestampUtc)
            .Select(response => new UsageEvent
            {
                Model = response.Model,
                TimestampUtc = response.TimestampUtc,
                // A response-only record is still natural billable usage. Keep the source
                // compatible with passive quota estimation and expose its provenance through
                // ResponseUsageMatchKind instead of introducing a second cost-bearing source.
                Source = UsageEventSource.Natural,
                InputTokens = response.InputTokens,
                CachedInputTokens = response.CachedInputTokens,
                CacheWriteTokens = response.CacheWriteTokens,
                OutputTokens = response.OutputTokens,
                ReasoningOutputTokens = response.ReasoningOutputTokens,
                TotalTokens = response.TotalTokens,
                ResponseUsageMatched = true,
                ResponseUsageMatchKind = "response-only",
                ResponseUsageDifferenceFields = ["jsonl_usage_missing"],
                ResponseUsageResponseTimestampUtc = response.TimestampUtc
            })
            .ToArray();
    }

    private static CacheWriteResponseRecord? SelectClosestResponse(
        UsageEvent usage,
        IReadOnlyList<CacheWriteResponseRecord> candidates,
        ISet<string> usedResponseIds)
    {
        CacheWriteResponseRecord? best = null;
        var bestDistance = double.MaxValue;
        foreach (var candidate in candidates)
        {
            if (usedResponseIds.Contains(candidate.ResponseId) ||
                !IsWithinResponseUsageTimeWindow(usage, candidate, out var distance))
            {
                continue;
            }
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static CacheWriteResponseRecord? SelectTolerantResponse(
        UsageEvent usage,
        IReadOnlyList<CacheWriteResponseRecord> candidates,
        int startIndex,
        int endIndex,
        ISet<string> usedResponseIds)
    {
        var usageModel = NormalizeCacheWriteModel(usage.Model);
        var ranked = new List<(CacheWriteResponseRecord Response, int ExactFields, double Distance)>();
        for (var index = startIndex; index < endIndex; index++)
        {
            var candidate = candidates[index];
            if (usedResponseIds.Contains(candidate.ResponseId) ||
                !IsWithinResponseUsageTimeWindow(usage, candidate, out var distance))
            {
                continue;
            }

            var responseModel = NormalizeCacheWriteModel(candidate.Model);
            if (usageModel.Length > 0 && responseModel.Length > 0 &&
                !usageModel.Equals(responseModel, StringComparison.Ordinal))
            {
                continue;
            }

            var exactFields = CountEqualResponseUsageFields(usage, candidate);
            // Five compared counters are strongly correlated. Requiring three exact values
            // permits one base counter plus total_tokens to disagree while rejecting a mere
            // same-model/same-minute coincidence from another concurrent Codex session.
            if (exactFields < 3)
            {
                continue;
            }
            ranked.Add((candidate, exactFields, distance));
        }

        var ordered = ranked
            .OrderByDescending(item => item.ExactFields)
            .ThenBy(item => item.Distance)
            .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        if (ordered.Length > 1 &&
            ordered[0].ExactFields == ordered[1].ExactFields &&
            Math.Abs(ordered[0].Distance - ordered[1].Distance) < 0.5D)
        {
            // Ambiguous concurrent candidates are safer left unresolved than silently
            // assigning another response's authoritative usage to this JSONL event.
            return null;
        }
        return ordered[0].Response;
    }

    private static bool IsWithinResponseUsageTimeWindow(
        UsageEvent usage,
        CacheWriteResponseRecord response,
        out double distance)
    {
        var secondsAfterResponse = (usage.TimestampUtc - response.TimestampUtc).TotalSeconds;
        distance = Math.Abs(secondsAfterResponse);
        return secondsAfterResponse is >= -30D and <= 300D;
    }

    private static int CountEqualResponseUsageFields(
        UsageEvent usage,
        CacheWriteResponseRecord response)
    {
        var count = 0;
        if (usage.InputTokens == response.InputTokens) count++;
        if (usage.CachedInputTokens == response.CachedInputTokens) count++;
        if (usage.OutputTokens == response.OutputTokens) count++;
        if (usage.ReasoningOutputTokens == response.ReasoningOutputTokens) count++;
        if (usage.TotalTokens == response.TotalTokens) count++;
        return count;
    }

    private static UsageResponseReconciliationAudit BuildReconciliationAudit(
        UsageEvent usage,
        CacheWriteResponseRecord response,
        string matchKind)
    {
        var differences = new List<string>();
        var usageModel = NormalizeCacheWriteModel(usage.Model);
        var responseModel = NormalizeCacheWriteModel(response.Model);
        if (usageModel.Length > 0 && responseModel.Length > 0 &&
            !usageModel.Equals(responseModel, StringComparison.Ordinal))
        {
            differences.Add("model");
        }
        if (usage.InputTokens != response.InputTokens) differences.Add("input_tokens");
        if (usage.CachedInputTokens != response.CachedInputTokens) differences.Add("cached_input_tokens");
        if (response.CacheWriteTokens is long responseCacheWrite)
        {
            if (!usage.CacheWriteTokens.HasValue)
            {
                differences.Add("cache_write_tokens_missing");
            }
            else if (usage.CacheWriteTokens.Value != responseCacheWrite)
            {
                differences.Add("cache_write_tokens");
            }
        }
        if (usage.OutputTokens != response.OutputTokens) differences.Add("output_tokens");
        if (usage.ReasoningOutputTokens != response.ReasoningOutputTokens) differences.Add("reasoning_output_tokens");
        if (usage.TotalTokens != response.TotalTokens) differences.Add("total_tokens");

        return new UsageResponseReconciliationAudit
        {
            UsageTimestampUtc = usage.TimestampUtc,
            MatchKind = matchKind,
            JsonlModel = usage.Model,
            JsonlInputTokens = usage.InputTokens,
            JsonlCachedInputTokens = usage.CachedInputTokens,
            JsonlCacheWriteTokens = usage.CacheWriteTokens,
            JsonlOutputTokens = usage.OutputTokens,
            JsonlReasoningOutputTokens = usage.ReasoningOutputTokens,
            JsonlTotalTokens = usage.TotalTokens,
            DifferenceFields = differences
        };
    }

    private static bool ReconciliationAuditEqual(
        UsageResponseReconciliationAudit? left,
        UsageResponseReconciliationAudit right) =>
        left != null &&
        left.UsageTimestampUtc == right.UsageTimestampUtc &&
        string.Equals(left.MatchKind, right.MatchKind, StringComparison.Ordinal) &&
        string.Equals(left.JsonlModel, right.JsonlModel, StringComparison.Ordinal) &&
        left.JsonlInputTokens == right.JsonlInputTokens &&
        left.JsonlCachedInputTokens == right.JsonlCachedInputTokens &&
        left.JsonlCacheWriteTokens == right.JsonlCacheWriteTokens &&
        left.JsonlOutputTokens == right.JsonlOutputTokens &&
        left.JsonlReasoningOutputTokens == right.JsonlReasoningOutputTokens &&
        left.JsonlTotalTokens == right.JsonlTotalTokens &&
        left.DifferenceFields != null &&
        left.DifferenceFields.SequenceEqual(right.DifferenceFields, StringComparer.Ordinal);

    private static CacheWriteMatchKey CreateResponseUsageMatchKey(UsageEvent usage) => new(
        NormalizeCacheWriteModel(usage.Model),
        usage.InputTokens,
        usage.CachedInputTokens,
        usage.OutputTokens,
        usage.ReasoningOutputTokens,
        usage.TotalTokens);

    private static CacheWriteMatchKey CreateResponseUsageMatchKey(CacheWriteResponseRecord response) => new(
        NormalizeCacheWriteModel(response.Model),
        response.InputTokens,
        response.CachedInputTokens,
        response.OutputTokens,
        response.ReasoningOutputTokens,
        response.TotalTokens);

    private static string NormalizeCacheWriteModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var normalized = model.Trim().ToLowerInvariant();
        if (normalized.Equals("gpt-5.6", StringComparison.Ordinal))
        {
            return "gpt-5.6-sol";
        }
        if (normalized.Contains("chat-latest", StringComparison.Ordinal))
        {
            return "gpt-5.5";
        }
        foreach (var known in new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5" })
        {
            if (normalized.Contains(known, StringComparison.Ordinal))
            {
                return known;
            }
        }
        return normalized;
    }

    private void EnsurePersistentCacheWriteIndexLoaded()
    {
        if (_persistentCacheWriteIndexLoaded)
        {
            return;
        }
        _persistentCacheWriteIndexLoaded = true;

        try
        {
            if (!File.Exists(PersistentCacheWriteIndexPath))
            {
                return;
            }
            using var stream = new FileStream(
                PersistentCacheWriteIndexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var document = JsonSerializer.Deserialize<PersistentCacheWriteIndexDocument>(stream, CacheJsonOptions);
            if (document == null ||
                document.SchemaVersion is < 1 or > PersistentCacheWriteIndexSchemaVersion ||
                string.IsNullOrWhiteSpace(document.DatabasePath))
            {
                return;
            }

            _cacheWriteDatabasePath = Path.GetFullPath(document.DatabasePath);
            _cacheWriteLastScannedLogId = Math.Max(0L, document.LastScannedLogId);
            _cacheWriteLoadedFromUtc = document.LoadedFromUtc.ToUniversalTime();
            _cacheWriteIndexNeedsNullableRescan =
                document.SchemaVersion < PersistentCacheWriteIndexSchemaVersion;
            _lastCacheWriteIndexPersistAttemptUtc = DateTimeOffset.UtcNow;
            foreach (var response in document.Responses ?? [])
            {
                if (!string.IsNullOrWhiteSpace(response.ResponseId))
                {
                    _cacheWriteResponses[response.ResponseId] = response;
                }
            }
        }
        catch
        {
            _cacheWriteResponses.Clear();
            _cacheWriteDatabasePath = null;
            _cacheWriteLastScannedLogId = 0L;
            _cacheWriteLoadedFromUtc = null;
            _cacheWriteIndexNeedsNullableRescan = false;
        }
    }

    private void PruneCacheWriteResponses(DateTimeOffset fromUtc)
    {
        foreach (var key in _cacheWriteResponses
                     .Where(pair => pair.Value.TimestampUtc < fromUtc)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _cacheWriteResponses.Remove(key);
        }
    }

    private void PersistCacheWriteIndexIfDue(bool force)
    {
        if (!_cacheWriteIndexDirty)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force &&
            _lastCacheWriteIndexPersistAttemptUtc != default &&
            now - _lastCacheWriteIndexPersistAttemptUtc < PersistentCacheWriteIndexPersistInterval)
        {
            return;
        }

        _lastCacheWriteIndexPersistAttemptUtc = now;
        if (TryPersistCacheWriteIndex())
        {
            _cacheWriteIndexDirty = false;
        }
    }

    private bool TryPersistCacheWriteIndex()
    {
        var path = PersistentCacheWriteIndexPath;
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var document = new PersistentCacheWriteIndexDocument
            {
                SchemaVersion = PersistentCacheWriteIndexSchemaVersion,
                DatabasePath = _cacheWriteDatabasePath ?? string.Empty,
                LastScannedLogId = _cacheWriteLastScannedLogId,
                LoadedFromUtc = _cacheWriteLoadedFromUtc ?? DateTimeOffset.UtcNow,
                Responses = _cacheWriteResponses.Values
                    .OrderBy(item => item.TimestampUtc)
                    .ToList()
            };
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch
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
                // Best effort cleanup only.
            }
            return false;
        }
    }

    private IReadOnlyList<UsageEvent> GetUsageEvents(string sessionsRoot, DateTimeOffset timelineStart)
    {
        lock (_usageFileCacheGate)
        {
            return BuildUsageEventSnapshot(sessionsRoot, timelineStart);
        }
    }

    private IReadOnlyList<UsageEvent>? GetPersistedUsageEvents(
        string sessionsRoot,
        DateTimeOffset timelineStart)
    {
        lock (_usageFileCacheGate)
        {
            EnsurePersistentUsageCacheLoaded();
            string normalizedRoot;
            try
            {
                normalizedRoot = Path.GetFullPath(sessionsRoot);
            }
            catch
            {
                return null;
            }

            var matching = _usageFileCache.Values
                .Where(entry => entry.SessionsRoot.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matching.Count == 0)
            {
                return null;
            }

            var timelineStartUtc = timelineStart.ToUniversalTime();
            return matching
                .SelectMany(entry => entry.Events)
                .Where(usage => usage.TimestampUtc >= timelineStartUtc)
                .Select(CloneUsageEvent)
                .ToList();
        }
    }

    private IReadOnlyList<UsageEvent> BuildUsageEventSnapshot(
        string sessionsRoot,
        DateTimeOffset timelineStart)
    {
        EnsurePersistentUsageCacheLoaded();

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(sessionsRoot);
        }
        catch
        {
            return [];
        }

        if (!Directory.Exists(normalizedRoot))
        {
            RemoveCachedFilesForRoot(normalizedRoot, activePaths: null);
            return [];
        }

        var minWriteTime = timelineStart.LocalDateTime.AddDays(-2);
        List<string> files;
        try
        {
            files = EnumerateRecentUsageLogFiles(normalizedRoot, minWriteTime)
                .Where(path => File.GetLastWriteTime(path) >= minWriteTime)
                .Select(Path.GetFullPath)
                .ToList();
        }
        catch
        {
            return [];
        }

        var timelineStartUtc = timelineStart.ToUniversalTime();
        var snapshot = new List<UsageEvent>();
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!TryGetUsageFileIdentity(file, out var identity))
            {
                _usageFileCache.Remove(file);
                continue;
            }

            activePaths.Add(identity.FullPath);
            IReadOnlyList<UsageEvent> parsedEvents;
            if (_usageFileCache.TryGetValue(identity.FullPath, out var cached) &&
                cached.Matches(normalizedRoot, identity) &&
                cached.ParsedLength >= identity.Length)
            {
                parsedEvents = cached.Events;
            }
            else
            {
                ParsedUsageFile parsed;
                if (cached != null && cached.CanAppend(normalizedRoot, identity))
                {
                    // Codex JSONL files are append-only during a live task. Resume at the last
                    // complete JSON line with the saved model/replay state instead of rereading
                    // a multi-gigabyte task from byte zero for every token_count update.
                    parsed = ParseUsageFile(
                        identity.FullPath,
                        cached.ParsedLength,
                        cached.ParserState,
                        accountName: null);
                    parsedEvents = cached.Events.Concat(parsed.Events).ToList();
                }
                else
                {
                    parsed = ParseUsageFile(
                        identity.FullPath,
                        startOffset: 0,
                        UsageParserState.Empty,
                        accountName: null);
                    parsedEvents = parsed.Events;
                }

                if (TryGetUsageFileIdentity(identity.FullPath, out var identityAfterRead))
                {
                    var replacement = new CachedUsageFile(
                        normalizedRoot,
                        identityAfterRead,
                        parsed.ProcessedLength,
                        ComputeTailFingerprint(identity.FullPath, parsed.ProcessedLength),
                        parsed.ParserState,
                        parsedEvents);
                    _usageFileCache[identity.FullPath] = replacement;
                    _usageFileCacheDirty = true;
                }
            }

            foreach (var usage in parsedEvents)
            {
                if (usage.TimestampUtc >= timelineStartUtc)
                {
                    // BuildReport assigns accounts from the latest switch history. Never expose
                    // a cached mutable event, otherwise parallel reports could overwrite it.
                    snapshot.Add(CloneUsageEvent(usage));
                }
            }
        }

        RemoveCachedFilesForRoot(normalizedRoot, activePaths);
        PersistUsageFileCacheIfDirty();
        return snapshot;
    }

    private static IEnumerable<string> EnumerateRecentUsageLogFiles(
        string sessionsRoot,
        DateTime minWriteTime)
    {
        // Codex stores sessions in sessions\\yyyy\\MM\\dd. Walking only the days that can
        // contribute to this dashboard avoids repeatedly traversing years of immutable history.
        // Keep top-level files for fixtures and older flat layouts, and fall back to the legacy
        // recursive scan when the dated layout is not present at all.
        var hasDatedDirectory = false;
        var today = DateTime.Today;
        for (var date = minWriteTime.Date; date <= today; date = date.AddDays(1))
        {
            var dateDirectory = Path.Combine(
                sessionsRoot,
                date.Year.ToString("D4", CultureInfo.InvariantCulture),
                date.Month.ToString("D2", CultureInfo.InvariantCulture),
                date.Day.ToString("D2", CultureInfo.InvariantCulture));
            if (!Directory.Exists(dateDirectory))
            {
                continue;
            }

            hasDatedDirectory = true;
            foreach (var file in Directory.EnumerateFiles(dateDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }
        }

        if (!hasDatedDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                yield return file;
            }
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }
    }

    private void RemoveCachedFilesForRoot(string normalizedRoot, HashSet<string>? activePaths)
    {
        var stalePaths = _usageFileCache
            .Where(entry =>
                entry.Value.SessionsRoot.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                (activePaths == null || !activePaths.Contains(entry.Key)))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var stalePath in stalePaths)
        {
            _usageFileCache.Remove(stalePath);
            _usageFileCacheDirty = true;
        }
    }

    private void EnsurePersistentUsageCacheLoaded()
    {
        if (_persistentUsageCacheLoaded)
        {
            return;
        }

        _persistentUsageCacheLoaded = true;
        try
        {
            var path = PersistentUsageCachePath;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > 128L * 1024L * 1024L)
            {
                return;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                UsageReadBufferSize,
                FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<PersistentUsageCacheDocument>(stream, CacheJsonOptions);
            if (document == null || document.SchemaVersion != PersistentUsageCacheSchemaVersion)
            {
                return;
            }

            foreach (var entry in document.Files ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.FullPath) ||
                    string.IsNullOrWhiteSpace(entry.SessionsRoot) ||
                    entry.Length < 0 ||
                    entry.ParsedLength < 0 ||
                    entry.ParsedLength > entry.Length)
                {
                    continue;
                }

                string fullPath;
                string sessionsRoot;
                try
                {
                    fullPath = Path.GetFullPath(entry.FullPath);
                    sessionsRoot = Path.GetFullPath(entry.SessionsRoot);
                }
                catch
                {
                    continue;
                }

                _usageFileCache[fullPath] = new CachedUsageFile(
                    sessionsRoot,
                    new UsageFileIdentity(fullPath, entry.Length, entry.LastWriteTimeUtc),
                    entry.ParsedLength,
                    entry.TailFingerprint ?? string.Empty,
                    entry.ParserState ?? UsageParserState.Empty,
                    entry.Events ?? []);
            }
        }
        catch
        {
            // A partial/corrupt cache must never block the quota page. The next successful
            // local scan recreates it atomically without touching any Codex credential.
            _usageFileCache.Clear();
            _usageFileCacheDirty = true;
        }
    }

    private void PersistUsageFileCacheIfDirty(bool force = false)
    {
        if (!_usageFileCacheDirty)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force &&
            _lastUsageFileCachePersistAttemptUtc != default &&
            now - _lastUsageFileCachePersistAttemptUtc < PersistentUsageCachePersistInterval)
        {
            return;
        }

        // Rate-limit failed attempts too.  The in-memory incremental index remains complete,
        // and a later pass will retry atomically without turning a transient disk issue into a
        // 250ms CPU/disk loop.
        _lastUsageFileCachePersistAttemptUtc = now;

        var path = PersistentUsageCachePath;
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var document = new PersistentUsageCacheDocument
            {
                SchemaVersion = PersistentUsageCacheSchemaVersion,
                Files = _usageFileCache.Values
                    .OrderBy(entry => entry.Identity.FullPath, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new PersistentUsageCacheEntry
                    {
                        SessionsRoot = entry.SessionsRoot,
                        FullPath = entry.Identity.FullPath,
                        Length = entry.Identity.Length,
                        LastWriteTimeUtc = entry.Identity.LastWriteTimeUtc,
                        ParsedLength = entry.ParsedLength,
                        TailFingerprint = entry.TailFingerprint,
                        ParserState = entry.ParserState,
                        Events = entry.Events.ToList()
                    })
                    .ToList()
            };

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        UsageReadBufferSize,
                        FileOptions.None))
            {
                JsonSerializer.Serialize(stream, document, CacheJsonOptions);
            }

            File.Move(temporaryPath, path, overwrite: true);
            _usageFileCacheDirty = false;
        }
        catch
        {
            // The in-memory index remains usable. A later refresh retries persistence.
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static string ComputeTailFingerprint(string path, long parsedLength)
    {
        if (parsedLength <= 0)
        {
            return "EMPTY";
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                UsageTailFingerprintBytes,
                FileOptions.RandomAccess);
            if (parsedLength > stream.Length)
            {
                return string.Empty;
            }

            var byteCount = checked((int)Math.Min(UsageTailFingerprintBytes, parsedLength));
            var buffer = new byte[byteCount];
            stream.Position = parsedLength - byteCount;
            var totalRead = 0;
            while (totalRead < byteCount)
            {
                var read = stream.Read(buffer, totalRead, byteCount - totalRead);
                if (read <= 0)
                {
                    return string.Empty;
                }
                totalRead += read;
            }

            return Convert.ToHexString(SHA256.HashData(buffer));
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class PersistentUsageCacheDocument
    {
        public int SchemaVersion { get; set; }
        public List<PersistentUsageCacheEntry> Files { get; set; } = [];
    }

    private sealed class PersistentUsageCacheEntry
    {
        public string SessionsRoot { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public long ParsedLength { get; set; }
        public string? TailFingerprint { get; set; }
        public UsageParserState? ParserState { get; set; }
        public List<UsageEvent>? Events { get; set; }
    }

    private static bool TryGetUsageFileIdentity(string path, out UsageFileIdentity identity)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists)
            {
                identity = default;
                return false;
            }

            identity = new UsageFileIdentity(
                Path.GetFullPath(info.FullName),
                info.Length,
                info.LastWriteTimeUtc);
            return true;
        }
        catch
        {
            identity = default;
            return false;
        }
    }

    private static UsageEvent CloneUsageEvent(UsageEvent source)
    {
        return new UsageEvent
        {
            AccountName = source.AccountName,
            Model = source.Model,
            TimestampUtc = source.TimestampUtc,
            SessionStartedAtUtc = source.SessionStartedAtUtc,
            InputTokens = source.InputTokens,
            CachedInputTokens = source.CachedInputTokens,
            CacheWriteTokens = source.CacheWriteTokens,
            OutputTokens = source.OutputTokens,
            ReasoningOutputTokens = source.ReasoningOutputTokens,
            TotalTokens = source.TotalTokens,
            RateLimitUsedPercent = source.RateLimitUsedPercent,
            RateLimitWindowMinutes = source.RateLimitWindowMinutes,
            RateLimitResetAtUtc = source.RateLimitResetAtUtc,
            SecondaryRateLimitUsedPercent = source.SecondaryRateLimitUsedPercent,
            SecondaryRateLimitWindowMinutes = source.SecondaryRateLimitWindowMinutes,
            SecondaryRateLimitResetAtUtc = source.SecondaryRateLimitResetAtUtc,
            CreditBalance = source.CreditBalance,
            IndividualLimit = source.IndividualLimit,
            PlanType = source.PlanType,
            Source = source.Source,
            ActivationEpochUtc = source.ActivationEpochUtc,
            EquivalentCostOverrideUsd = source.EquivalentCostOverrideUsd,
            ResponseUsageMatched = source.ResponseUsageMatched,
            ResponseUsageMatchKind = source.ResponseUsageMatchKind,
            ResponseUsageDifferenceFields = source.ResponseUsageDifferenceFields?.ToArray() ?? [],
            ResponseUsageResponseTimestampUtc = source.ResponseUsageResponseTimestampUtc
        };
    }

    private readonly record struct UsageFileIdentity(
        string FullPath,
        long Length,
        DateTime LastWriteTimeUtc);

    private readonly record struct CacheWriteMatchKey(
        string Model,
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long ReasoningOutputTokens,
        long TotalTokens);

    private sealed class CacheWriteResponseRecord
    {
        public long LogId { get; set; }
        public string ResponseId { get; set; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; set; }
        public string? Model { get; set; }
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long? CacheWriteTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningOutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public UsageResponseReconciliationAudit? Reconciliation { get; set; }
    }

    private sealed class UsageResponseReconciliationAudit
    {
        public DateTimeOffset UsageTimestampUtc { get; set; }
        public string MatchKind { get; set; } = string.Empty;
        public string? JsonlModel { get; set; }
        public long JsonlInputTokens { get; set; }
        public long JsonlCachedInputTokens { get; set; }
        public long? JsonlCacheWriteTokens { get; set; }
        public long JsonlOutputTokens { get; set; }
        public long JsonlReasoningOutputTokens { get; set; }
        public long JsonlTotalTokens { get; set; }
        public List<string> DifferenceFields { get; set; } = [];
    }

    private readonly record struct ResponseUsageReconciliationResult(
        int MatchedCount,
        int DifferenceCount,
        int AuditRecordChangeCount,
        IReadOnlyList<CacheWriteResponseRecord> UnmatchedResponses);

    private sealed class PersistentCacheWriteIndexDocument
    {
        public int SchemaVersion { get; set; }
        public string DatabasePath { get; set; } = string.Empty;
        public long LastScannedLogId { get; set; }
        public DateTimeOffset LoadedFromUtc { get; set; }
        public List<CacheWriteResponseRecord> Responses { get; set; } = [];
    }

    private sealed class CachedUsageFile(
        string sessionsRoot,
        UsageFileIdentity identity,
        long parsedLength,
        string tailFingerprint,
        UsageParserState parserState,
        IReadOnlyList<UsageEvent> events)
    {
        public string SessionsRoot { get; } = sessionsRoot;
        public UsageFileIdentity Identity { get; } = identity;
        public long ParsedLength { get; } = parsedLength;
        public string TailFingerprint { get; } = tailFingerprint;
        public UsageParserState ParserState { get; } = parserState;
        public IReadOnlyList<UsageEvent> Events { get; } = events;

        public bool Matches(string expectedRoot, UsageFileIdentity candidate)
        {
            return SessionsRoot.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase) &&
                Identity.Equals(candidate);
        }

        public bool CanAppend(string expectedRoot, UsageFileIdentity candidate)
        {
            if (!SessionsRoot.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase) ||
                ParsedLength < 0 ||
                ParsedLength > candidate.Length ||
                candidate.Length < Identity.Length)
            {
                return false;
            }

            var hasPendingTail = Matches(expectedRoot, candidate) && ParsedLength < candidate.Length;
            if (candidate.Length == Identity.Length && !hasPendingTail)
            {
                return false;
            }

            return string.Equals(
                TailFingerprint,
                ComputeTailFingerprint(candidate.FullPath, ParsedLength),
                StringComparison.Ordinal);
        }
    }

    private sealed class UsageParserState
    {
        public static UsageParserState Empty => new();

        public string? CurrentModel { get; set; }
        public DateTimeOffset? SessionStartedAtUtc { get; set; }
        public string? LastCumulativeUsageSignature { get; set; }
        public CumulativeUsageSnapshot? LastCumulativeUsage { get; set; }
        public bool SessionMetadataSeen { get; set; }
        public bool SuppressForkReplay { get; set; }

        public UsageParserState Clone() => new()
        {
            CurrentModel = CurrentModel,
            SessionStartedAtUtc = SessionStartedAtUtc,
            LastCumulativeUsageSignature = LastCumulativeUsageSignature,
            LastCumulativeUsage = LastCumulativeUsage?.Clone(),
            SessionMetadataSeen = SessionMetadataSeen,
            SuppressForkReplay = SuppressForkReplay
        };
    }

    private sealed class CumulativeUsageSnapshot
    {
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long? CacheWriteTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningOutputTokens { get; set; }
        public long TotalTokens { get; set; }

        public CumulativeUsageSnapshot Clone() => new()
        {
            InputTokens = InputTokens,
            CachedInputTokens = CachedInputTokens,
            CacheWriteTokens = CacheWriteTokens,
            OutputTokens = OutputTokens,
            ReasoningOutputTokens = ReasoningOutputTokens,
            TotalTokens = TotalTokens
        };

        public string GetSignature() => string.Join(
            "|",
            InputTokens.ToString(CultureInfo.InvariantCulture),
            CachedInputTokens.ToString(CultureInfo.InvariantCulture),
            CacheWriteTokens?.ToString(CultureInfo.InvariantCulture) ?? "?",
            OutputTokens.ToString(CultureInfo.InvariantCulture),
            ReasoningOutputTokens.ToString(CultureInfo.InvariantCulture),
            TotalTokens.ToString(CultureInfo.InvariantCulture));
    }

    private sealed record ParsedUsageFile(
        IReadOnlyList<UsageEvent> Events,
        long ProcessedLength,
        UsageParserState ParserState);

    private static IEnumerable<UsageEvent> EnumerateUsageEventsFromFile(string file, string? accountName)
    {
        return ParseUsageFile(file, 0, UsageParserState.Empty, accountName).Events;
    }

    private static ParsedUsageFile ParseUsageFile(
        string file,
        long startOffset,
        UsageParserState initialState,
        string? accountName)
    {
        var events = new List<UsageEvent>();
        var state = initialState.Clone();
        state.SessionStartedAtUtc ??= TryGetSessionStartFromFileName(file);
        var replayFilter = new SubagentReplayFilter(state);
        var processedLength = Math.Max(0, startOffset);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                UsageReadBufferSize,
                FileOptions.SequentialScan);
            if (startOffset < 0 || startOffset > stream.Length)
            {
                return new ParsedUsageFile(events, 0, UsageParserState.Empty);
            }

            stream.Position = startOffset;
            using var lineBuffer = new MemoryStream();
            var readBuffer = new byte[UsageReadBufferSize];
            while (true)
            {
                var bufferStart = stream.Position;
                var read = stream.Read(readBuffer, 0, readBuffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var segmentStart = 0;
                for (var index = 0; index < read; index++)
                {
                    if (readBuffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    if (index > segmentStart)
                    {
                        lineBuffer.Write(readBuffer, segmentStart, index - segmentStart);
                    }

                    ProcessUsageLine(
                        lineBuffer.GetBuffer().AsSpan(0, checked((int)lineBuffer.Length)),
                        replayFilter,
                        state,
                        accountName,
                        events,
                        validateCompleteJson: false);
                    lineBuffer.SetLength(0);
                    processedLength = bufferStart + index + 1;
                    segmentStart = index + 1;
                }

                if (segmentStart < read)
                {
                    lineBuffer.Write(readBuffer, segmentStart, read - segmentStart);
                }
            }

            // A closed JSONL file does not always end with a newline. Consume its final
            // record only when it is complete JSON; a concurrently-written partial record
            // remains pending and will be retried from the same byte boundary next time.
            if (lineBuffer.Length > 0)
            {
                var stateBeforeTail = state.Clone();
                var eventCountBeforeTail = events.Count;
                var complete = ProcessUsageLine(
                    lineBuffer.GetBuffer().AsSpan(0, checked((int)lineBuffer.Length)),
                    replayFilter,
                    state,
                    accountName,
                    events,
                    validateCompleteJson: true);
                if (complete)
                {
                    processedLength = stream.Length;
                }
                else
                {
                    state = stateBeforeTail;
                    if (events.Count > eventCountBeforeTail)
                    {
                        events.RemoveRange(eventCountBeforeTail, events.Count - eventCountBeforeTail);
                    }
                }
            }
        }
        catch
        {
            // A task can be rotated or removed while the background index is reading it.
            // Return every complete line observed so far; the next watcher tick reconciles it.
        }
        finally
        {
            stream?.Dispose();
        }

        state.SessionMetadataSeen = replayFilter.SessionMetadataSeen;
        state.SuppressForkReplay = replayFilter.SuppressForkReplay;
        return new ParsedUsageFile(events, processedLength, state);
    }

    private static bool ProcessUsageLine(
        ReadOnlySpan<byte> utf8Line,
        SubagentReplayFilter replayFilter,
        UsageParserState state,
        string? accountName,
        List<UsageEvent> events,
        bool validateCompleteJson)
    {
        if (utf8Line.Length > 0 && utf8Line[^1] == (byte)'\r')
        {
            utf8Line = utf8Line[..^1];
        }
        if (utf8Line.IsEmpty)
        {
            return true;
        }

        string line;
        try
        {
            line = Encoding.UTF8.GetString(utf8Line);
            if (line.Length > 0 && line[0] == '\uFEFF')
            {
                line = line[1..];
            }
        }
        catch
        {
            return false;
        }

        if (!validateCompleteJson && !replayFilter.RequiresParsing(line))
        {
            return true;
        }

        try
        {
            var root = JsonNode.Parse(line)?.AsObject();
            if (root == null)
            {
                return false;
            }

            if (validateCompleteJson && !replayFilter.RequiresParsing(line))
            {
                return true;
            }

            var isFirstSessionMetadata = !state.SessionMetadataSeen && IsSessionMetadata(root);
            if (isFirstSessionMetadata)
            {
                CaptureSessionMetadata(root, state);
                state.SessionMetadataSeen = true;
            }

            var shouldInclude = replayFilter.ShouldInclude(root);
            var observedModel = TryGetTurnContextModel(root);
            if (!string.IsNullOrWhiteSpace(observedModel))
            {
                state.CurrentModel = observedModel;
            }

            if (shouldInclude && TryParseUsageEvent(root, accountName, state.CurrentModel) is { } usage)
            {
                usage.SessionStartedAtUtc = state.SessionStartedAtUtc;
                var hasLastUsage = HasLastTokenUsage(root);
                var cumulativeUsage = TryGetCumulativeUsageSnapshot(root);
                if (cumulativeUsage != null)
                {
                    var cumulativeSignature = cumulativeUsage.GetSignature();
                    if (string.Equals(
                            cumulativeSignature,
                            state.LastCumulativeUsageSignature,
                            StringComparison.Ordinal))
                    {
                        usage.Source = UsageEventSource.OfficialSnapshot;
                        usage.InputTokens = 0;
                        usage.CachedInputTokens = 0;
                        usage.CacheWriteTokens = cumulativeUsage.CacheWriteTokens.HasValue ? 0 : null;
                        usage.OutputTokens = 0;
                        usage.ReasoningOutputTokens = 0;
                        usage.TotalTokens = 0;
                    }
                    else
                    {
                        if (!hasLastUsage &&
                            !TryApplyCumulativeUsageDelta(
                                usage,
                                state.LastCumulativeUsage,
                                cumulativeUsage))
                        {
                            MakeZeroCostOfficialSnapshot(usage);
                        }
                        state.LastCumulativeUsageSignature = cumulativeSignature;
                        state.LastCumulativeUsage = cumulativeUsage;
                    }
                }
                else if (!hasLastUsage)
                {
                    // A percentage-only token_count record is still an official boundary.
                    MakeZeroCostOfficialSnapshot(usage);
                }
                events.Add(usage);
            }
            else if (shouldInclude &&
                     TryParseWebSearchUsageEvent(root, accountName, state.CurrentModel) is { } searchUsage)
            {
                searchUsage.SessionStartedAtUtc = state.SessionStartedAtUtc;
                events.Add(searchUsage);
            }

            return true;
        }
        catch
        {
            // Ignore malformed complete lines and retain a non-terminated partial line.
            return false;
        }
    }

    private static string? TryGetTurnContextModel(JsonObject root)
    {
        if (!string.Equals(GetString(root, "type"), "turn_context", StringComparison.Ordinal))
        {
            return null;
        }

        return root["payload"] is JsonObject payload
            ? GetString(payload, "model")
            : null;
    }

    private static bool HasLastTokenUsage(JsonObject root) =>
        root["payload"]?["info"]?["last_token_usage"] is JsonObject;

    private static bool IsSessionMetadata(JsonObject root)
    {
        return string.Equals(GetString(root, "type"), "session_meta", StringComparison.Ordinal);
    }

    private static void CaptureSessionMetadata(JsonObject root, UsageParserState state)
    {
        var payload = root["payload"] as JsonObject;
        var payloadTimestamp = payload == null ? null : GetString(payload, "timestamp");
        var rootTimestamp = GetString(root, "timestamp");
        state.SessionStartedAtUtc = TryParseTimestamp(payloadTimestamp) ??
                                    TryParseTimestamp(rootTimestamp) ??
                                    state.SessionStartedAtUtc;
    }

    private static DateTimeOffset? TryParseTimestamp(string? value)
    {
        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;
    }

    private static DateTimeOffset? TryGetSessionStartFromFileName(string file)
    {
        var name = Path.GetFileName(file);
        const string prefix = "rollout-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length < prefix.Length + 19)
        {
            return null;
        }

        var timestampText = name.Substring(prefix.Length, 19);
        if (!DateTime.TryParseExact(
                timestampText,
                "yyyy-MM-dd'T'HH-mm-ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTimestamp))
        {
            return null;
        }

        var offset = TimeZoneInfo.Local.GetUtcOffset(localTimestamp);
        return new DateTimeOffset(localTimestamp, offset).ToUniversalTime();
    }

    private static CumulativeUsageSnapshot? TryGetCumulativeUsageSnapshot(JsonObject root)
    {
        if (root["payload"]?["info"]?["total_token_usage"] is not JsonObject totalUsage)
        {
            return null;
        }

        return new CumulativeUsageSnapshot
        {
            InputTokens = GetUsageInputTokens(totalUsage),
            CachedInputTokens = GetUsageCachedInputTokens(totalUsage),
            CacheWriteTokens = TryGetCacheWriteTokens(totalUsage),
            OutputTokens = GetUsageOutputTokens(totalUsage),
            ReasoningOutputTokens = GetUsageReasoningOutputTokens(totalUsage),
            TotalTokens = GetLong(totalUsage, "total_tokens")
        };
    }

    private static bool TryApplyCumulativeUsageDelta(
        UsageEvent usage,
        CumulativeUsageSnapshot? previous,
        CumulativeUsageSnapshot current)
    {
        var previousInput = previous?.InputTokens ?? 0;
        var previousCachedInput = previous?.CachedInputTokens ?? 0;
        var previousOutput = previous?.OutputTokens ?? 0;
        var previousReasoningOutput = previous?.ReasoningOutputTokens ?? 0;
        var previousTotal = previous?.TotalTokens ?? 0;
        if (current.InputTokens < previousInput ||
            current.CachedInputTokens < previousCachedInput ||
            (previous?.CacheWriteTokens is long previousCacheWrite &&
             current.CacheWriteTokens is long currentCacheWrite &&
             currentCacheWrite < previousCacheWrite) ||
            current.OutputTokens < previousOutput ||
            current.ReasoningOutputTokens < previousReasoningOutput ||
            current.TotalTokens < previousTotal)
        {
            return false;
        }

        usage.InputTokens = current.InputTokens - previousInput;
        usage.CachedInputTokens = current.CachedInputTokens - previousCachedInput;
        usage.CacheWriteTokens = current.CacheWriteTokens.HasValue &&
            (previous == null || previous.CacheWriteTokens.HasValue)
            ? current.CacheWriteTokens.Value - (previous?.CacheWriteTokens ?? 0)
            : null;
        usage.OutputTokens = current.OutputTokens - previousOutput;
        usage.ReasoningOutputTokens = current.ReasoningOutputTokens - previousReasoningOutput;
        usage.TotalTokens = current.TotalTokens - previousTotal;
        return true;
    }

    private static void MakeZeroCostOfficialSnapshot(UsageEvent usage)
    {
        usage.Source = UsageEventSource.OfficialSnapshot;
        usage.InputTokens = 0;
        usage.CachedInputTokens = 0;
        usage.CacheWriteTokens = null;
        usage.OutputTokens = 0;
        usage.ReasoningOutputTokens = 0;
        usage.TotalTokens = 0;
    }

    private static UsageEvent? TryParseUsageEvent(JsonObject root, string? accountName, string? model)
    {
        var payload = root?["payload"]?.AsObject();
        if (!string.Equals(payload?["type"]?.GetValue<string>(), "token_count", StringComparison.Ordinal))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(root?["timestamp"]?.GetValue<string>(), out var timestamp))
        {
            return null;
        }

        var lastUsage = payload?["info"]?["last_token_usage"] as JsonObject;

        var usage = new UsageEvent
        {
            AccountName = accountName,
            Model = model,
            TimestampUtc = timestamp.ToUniversalTime(),
            Source = UsageEventSource.Natural,
            InputTokens = lastUsage == null ? 0 : GetUsageInputTokens(lastUsage),
            CachedInputTokens = lastUsage == null ? 0 : GetUsageCachedInputTokens(lastUsage),
            CacheWriteTokens = lastUsage == null ? null : TryGetCacheWriteTokens(lastUsage),
            OutputTokens = lastUsage == null ? 0 : GetUsageOutputTokens(lastUsage),
            ReasoningOutputTokens = lastUsage == null ? 0 : GetUsageReasoningOutputTokens(lastUsage),
            TotalTokens = lastUsage == null ? 0 : GetLong(lastUsage, "total_tokens")
        };

        var rateLimits = payload?["rate_limits"] as JsonObject;
        var primary = rateLimits?["primary"] as JsonObject;
        var secondary = rateLimits?["secondary"] as JsonObject;
        usage.RateLimitUsedPercent = GetNullableDouble(primary, "used_percent");
        usage.RateLimitWindowMinutes = GetNullableLong(primary, "window_minutes");
        var resetsAt = GetNullableLong(primary, "resets_at");
        if (resetsAt.HasValue)
        {
            usage.RateLimitResetAtUtc = DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value);
        }

        usage.SecondaryRateLimitUsedPercent = GetNullableDouble(secondary, "used_percent");
        usage.SecondaryRateLimitWindowMinutes = GetNullableLong(secondary, "window_minutes");
        var secondaryResetsAt = GetNullableLong(secondary, "resets_at");
        if (secondaryResetsAt.HasValue)
        {
            usage.SecondaryRateLimitResetAtUtc = DateTimeOffset.FromUnixTimeSeconds(secondaryResetsAt.Value);
        }

        var credits = rateLimits?["credits"] as JsonObject;
        if (credits != null)
        {
            usage.CreditBalance = new UsageCreditsSnapshot(
                GetNullableBoolean(credits, "has_credits") ?? false,
                GetNullableBoolean(credits, "unlimited") ?? false,
                GetString(credits, "balance"));
        }

        var individualLimit = rateLimits?["individual_limit"] as JsonObject;
        if (individualLimit != null)
        {
            var individualResetsAt = GetNullableLong(individualLimit, "resets_at");
            usage.IndividualLimit = new UsageSpendControl(
                GetString(individualLimit, "limit") ?? "",
                GetString(individualLimit, "used") ?? "",
                GetNullableDouble(individualLimit, "remaining_percent"),
                individualResetsAt.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(individualResetsAt.Value)
                    : null);
        }

        usage.PlanType = GetString(rateLimits ?? new JsonObject(), "plan_type");

        return usage;
    }

    private static UsageEvent? TryParseWebSearchUsageEvent(
        JsonObject root,
        string? accountName,
        string? model)
    {
        if (!string.Equals(GetString(root, "type"), "event_msg", StringComparison.Ordinal) ||
            root["payload"] is not JsonObject payload ||
            !string.Equals(GetString(payload, "type"), "web_search_end", StringComparison.Ordinal) ||
            !DateTimeOffset.TryParse(GetString(root, "timestamp"), out var timestamp))
        {
            return null;
        }

        return new UsageEvent
        {
            AccountName = accountName,
            Model = model,
            TimestampUtc = timestamp.ToUniversalTime(),
            Source = UsageEventSource.Natural,
            // sub2api exposes every completed browser action through /v1/alpha/search
            // and bills the request once at a fixed $0.01, independent of token usage.
            EquivalentCostOverrideUsd = WebSearchEquivalentCostUsd,
            CacheWriteTokens = 0L
        };
    }

    private sealed class SubagentReplayFilter
    {
        private bool _sessionMetadataSeen;
        private bool _suppressForkReplay;

        public SubagentReplayFilter()
        {
        }

        public SubagentReplayFilter(UsageParserState state)
        {
            _sessionMetadataSeen = state.SessionMetadataSeen;
            _suppressForkReplay = state.SuppressForkReplay;
        }

        public bool SessionMetadataSeen => _sessionMetadataSeen;
        public bool SuppressForkReplay => _suppressForkReplay;

        public bool RequiresParsing(string line)
        {
            if (!_sessionMetadataSeen)
            {
                return line.Contains("session_meta", StringComparison.Ordinal) ||
                    line.Contains("turn_context", StringComparison.Ordinal) ||
                    line.Contains("token_count", StringComparison.Ordinal) ||
                    line.Contains("web_search_end", StringComparison.Ordinal);
            }

            if (_suppressForkReplay)
            {
                return line.Contains("turn_context", StringComparison.Ordinal) ||
                    line.Contains("inter_agent_communication_metadata", StringComparison.Ordinal) ||
                    line.Contains("web_search_end", StringComparison.Ordinal);
            }

            return line.Contains("turn_context", StringComparison.Ordinal) ||
                line.Contains("token_count", StringComparison.Ordinal) ||
                line.Contains("web_search_end", StringComparison.Ordinal);
        }

        public bool ShouldInclude(JsonObject root)
        {
            if (!_sessionMetadataSeen && IsSessionMetadata(root))
            {
                _sessionMetadataSeen = true;
                var payload = root["payload"]?.AsObject();
                if (payload != null && IsSubagentSession(payload))
                {
                    _suppressForkReplay = true;
                }

                return true;
            }

            if (!_suppressForkReplay)
            {
                return true;
            }

            if (string.Equals(
                GetString(root, "type"),
                "inter_agent_communication_metadata",
                StringComparison.Ordinal))
            {
                _suppressForkReplay = false;
            }

            // The boundary contains no usage. Keep it out so only records
            // produced by the subagent's live turn are considered.
            return false;
        }

        private static bool IsSessionMetadata(JsonObject root)
        {
            return string.Equals(root["type"]?.GetValue<string>(), "session_meta", StringComparison.Ordinal);
        }

        private static bool IsSubagentSession(JsonObject payload)
        {
            if (string.Equals(GetString(payload, "thread_source"), "subagent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return payload["source"] is JsonObject source && source["subagent"] != null;
        }

    }

    private readonly record struct UsageSwitchPoint(
        UsageSwitchEvent Entry,
        DateTimeOffset SwitchedAtUtc);

    private static IReadOnlyList<UsageSwitchEvent> FilterTrustedSwitchEvents(
        IEnumerable<UsageSwitchEvent> switchEvents,
        IReadOnlyList<AccountRecord> accounts)
    {
        var accountsByKey = accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.CodexHome))
            .GroupBy(QuotaAccountIdentity.CreateKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var accountNames = accounts
            .Select(account => account.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return switchEvents
            .Where(switchEvent =>
            {
                // Explicit switches are durable history, including switches to an account
                // that was later deleted. Only passive detection needs a trust check: old
                // portable/debug manager copies used to share this file and could otherwise
                // inject an unknown account boundary into every running request timeline.
                if (!string.Equals(switchEvent.Source, "detected", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // A stable identity is preferred, but exact-name fallback also covers legacy
                // detected entries and a current account whose persisted identity became stale.
                // Only a detection that matches neither signal is allowed to disappear; explicit
                // manual/switch history is retained above even after that account is deleted.
                return (!string.IsNullOrWhiteSpace(switchEvent.AccountKey) &&
                        accountsByKey.ContainsKey(switchEvent.AccountKey)) ||
                    accountNames.Contains(switchEvent.AccountName);
            })
            .ToList();
    }

    private static void AssignUsageToActiveSwitch(
        UsageEvent usage,
        IReadOnlyList<UsageSwitchPoint> switchEvents,
        IReadOnlyList<AccountRecord> accounts)
    {
        // A conversation can continue across multiple account switches, so ordinary events
        // belong to the account active at the event time. Fall back to the session boundary only
        // for a clear client-clock rollback; small timestamp skew must not collapse the whole
        // conversation into the account that originally created it.
        var attributionTimestamp = usage.TimestampUtc;
        if (usage.SessionStartedAtUtc is { } sessionStartedAtUtc &&
            usage.TimestampUtc < sessionStartedAtUtc - SessionClockRollbackTolerance)
        {
            attributionTimestamp = sessionStartedAtUtc;
        }
        var activeSwitch = FindActiveSwitch(attributionTimestamp, switchEvents);
        usage.AccountName = ResolveCurrentAccountName(activeSwitch?.Entry, accounts);
        usage.ActivationEpochUtc = activeSwitch?.SwitchedAtUtc;
    }

    private static UsageSwitchPoint? FindActiveSwitch(
        DateTimeOffset timestampUtc,
        IReadOnlyList<UsageSwitchPoint> switchEvents)
    {
        var low = 0;
        var high = switchEvents.Count - 1;
        var match = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (switchEvents[middle].SwitchedAtUtc <= timestampUtc)
            {
                match = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return match >= 0 ? switchEvents[match] : null;
    }

    private static string? ResolveCurrentAccountName(
        UsageSwitchEvent? switchEvent,
        IReadOnlyList<AccountRecord> accounts)
    {
        if (!string.IsNullOrWhiteSpace(switchEvent?.AccountKey))
        {
            var identityMatch = accounts.FirstOrDefault(account =>
                !string.IsNullOrWhiteSpace(account.CodexHome) &&
                string.Equals(
                    QuotaAccountIdentity.CreateKey(account),
                    switchEvent.AccountKey,
                    StringComparison.Ordinal));
            if (identityMatch != null)
            {
                return identityMatch.Name;
            }
        }

        return ResolveCurrentAccountName(switchEvent?.AccountName, accounts);
    }

    private static string? ResolveCurrentAccountName(
        string? recordedAccountName,
        IReadOnlyList<AccountRecord> accounts)
    {
        if (string.IsNullOrWhiteSpace(recordedAccountName))
        {
            return null;
        }

        var exact = accounts.FirstOrDefault(account =>
            account.Name.Equals(recordedAccountName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact.Name;
        }

        var renamedMatches = accounts
            .Where(account => account.Name.Contains(recordedAccountName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return renamedMatches.Count == 1 ? renamedMatches[0].Name : recordedAccountName;
    }

    private List<UsageSwitchEvent> LoadSwitchEvents()
    {
        var events = new List<UsageSwitchEvent>();
        foreach (var path in GetSwitchHistoryPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                events.AddRange(JsonSerializer.Deserialize<List<UsageSwitchEvent>>(json) ?? []);
            }
            catch
            {
                // Ignore a damaged history file; the other copy may still be valid.
            }
        }

        return NormalizeSwitchEvents(events);
    }

    private void SaveSwitchEvents(List<UsageSwitchEvent> events)
    {
        var normalized = NormalizeSwitchEvents(events);
        foreach (var path in GetSwitchHistoryPaths())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(normalized, JsonOptions));
            }
            catch
            {
                // The usage page should still work if one mirror cannot be written.
            }
        }
    }

    private static List<UsageSwitchEvent> NormalizeSwitchEvents(IEnumerable<UsageSwitchEvent> events)
    {
        var ordered = events
            .Where(e => !string.IsNullOrWhiteSpace(e.AccountName) && e.GetSwitchedAtUtc() > DateTimeOffset.MinValue)
            .GroupBy(e => $"{e.AccountName.Trim().ToLowerInvariant()}|{e.GetSwitchedAtUtc():O}")
            .Select(group => group
                .OrderByDescending(GetSwitchEventMetadataScore)
                .First())
            .OrderBy(e => e.GetSwitchedAtUtc())
            .ToList();

        var normalized = new List<UsageSwitchEvent>(ordered.Count);
        foreach (var current in ordered)
        {
            if (normalized.Count > 0 &&
                AreEquivalentSwitchBoundaries(normalized[^1], current))
            {
                continue;
            }

            normalized.Add(current);
        }

        return normalized;
    }

    private static int GetSwitchEventMetadataScore(UsageSwitchEvent switchEvent)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(switchEvent.AccountKey))
        {
            score += 4;
        }
        if (!string.IsNullOrWhiteSpace(switchEvent.ManagerScopeKey))
        {
            score += 2;
        }
        if (!string.Equals(switchEvent.Source, "detected", StringComparison.OrdinalIgnoreCase))
        {
            score++;
        }
        return score;
    }

    private static bool AreEquivalentSwitchBoundaries(
        UsageSwitchEvent left,
        UsageSwitchEvent right)
    {
        return left.AccountName.Trim().Equals(right.AccountName.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.AccountKey, right.AccountKey, StringComparison.Ordinal) &&
            string.Equals(left.ManagerScopeKey, right.ManagerScopeKey, StringComparison.Ordinal) &&
            string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase);
    }

    private static long GetLong(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var value) && value != null
            ? value.GetValue<long>()
            : 0;
    }

    private static long? TryGetCacheWriteTokens(JsonObject usage)
    {
        // Responses API: usage.input_tokens_details.cache_write_tokens
        // Chat Completions: usage.prompt_tokens_details.cache_write_tokens
        // Codex may also flatten the value when forwarding it to token_count.
        foreach (var container in new JsonObject?[]
                 {
                     usage,
                     usage["input_tokens_details"] as JsonObject,
                     usage["prompt_tokens_details"] as JsonObject
                 })
        {
            if (TryGetNonNegativeLong(container, "cache_write_tokens") is { } value)
            {
                var inputTokens = Math.Max(0L, GetUsageInputTokens(usage));
                var cachedTokens = Math.Clamp(GetUsageCachedInputTokens(usage), 0L, inputTokens);
                return value <= inputTokens - cachedTokens ? value : null;
            }
        }

        return null;
    }

    private static long GetUsageInputTokens(JsonObject usage) =>
        TryGetNonNegativeLong(usage, "input_tokens") ??
        TryGetNonNegativeLong(usage, "prompt_tokens") ??
        0L;

    private static long GetUsageCachedInputTokens(JsonObject usage)
    {
        if (TryGetNonNegativeLong(usage, "cached_input_tokens") is { } flattened)
        {
            return flattened;
        }

        foreach (var details in new[]
                 {
                     usage["input_tokens_details"] as JsonObject,
                     usage["prompt_tokens_details"] as JsonObject
                 })
        {
            if (TryGetNonNegativeLong(details, "cached_tokens") is { } nested)
            {
                return nested;
            }
        }
        return 0L;
    }

    private static long GetUsageOutputTokens(JsonObject usage) =>
        TryGetNonNegativeLong(usage, "output_tokens") ??
        TryGetNonNegativeLong(usage, "completion_tokens") ??
        0L;

    private static long GetUsageReasoningOutputTokens(JsonObject usage) =>
        TryGetNonNegativeLong(usage, "reasoning_output_tokens") ??
        TryGetNonNegativeLong(usage["output_tokens_details"] as JsonObject, "reasoning_tokens") ??
        TryGetNonNegativeLong(usage["completion_tokens_details"] as JsonObject, "reasoning_tokens") ??
        0L;

    private static long? TryGetNonNegativeLong(JsonObject? obj, string key)
    {
        if (obj == null ||
            !obj.TryGetPropertyValue(key, out var value) ||
            value is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<long>(out var result) ||
            result < 0)
        {
            return null;
        }

        return result;
    }

    private static string? GetString(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var value) && value is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var result)
            ? result
            : null;
    }

    private static long? GetNullableLong(JsonObject? obj, string key)
    {
        return obj != null && obj.TryGetPropertyValue(key, out var value) && value != null
            ? value.GetValue<long>()
            : null;
    }

    private static double? GetNullableDouble(JsonObject? obj, string key)
    {
        return obj != null && obj.TryGetPropertyValue(key, out var value) && value != null
            ? value.GetValue<double>()
            : null;
    }

    private static bool? GetNullableBoolean(JsonObject? obj, string key)
    {
        return obj != null && obj.TryGetPropertyValue(key, out var value) &&
            value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var result)
            ? result
            : null;
    }
}

public sealed class UsageSwitchEvent
{
    public string AccountName { get; set; } = "";
    public string? AccountKey { get; set; }
    public string? ManagerScopeKey { get; set; }
    public string SwitchedAtUtc { get; set; } = "";
    public string Source { get; set; } = "switch";

    public DateTimeOffset GetSwitchedAtUtc()
    {
        return DateTimeOffset.TryParse(SwitchedAtUtc, out var result)
            ? result.ToUniversalTime()
            : DateTimeOffset.MinValue;
    }
}

public sealed class UsageReport
{
    public DateTimeOffset GeneratedAt { get; set; }
    public int SwitchEventCount { get; set; }
    public List<AccountUsageSummary> Accounts { get; set; } = [];
    public UsageBucket UnassignedHour { get; } = new();
    public UsageBucket UnassignedFiveHours { get; } = new();
    public UsageBucket UnassignedDay { get; } = new();
    public UsageBucket UnassignedToday { get; } = new();
    public UsageBucket UnassignedWeek { get; } = new();
    public UsageBucket UnassignedMonth { get; } = new();
}

public sealed class AccountUsageSummary
{
    public string AccountName { get; set; } = "";
    public UsageBucket Hour { get; } = new();
    public UsageBucket FiveHours { get; } = new();
    public UsageBucket Day { get; } = new();
    public UsageBucket Today { get; } = new();
    public UsageBucket Week { get; } = new();
    public UsageBucket Month { get; } = new();
    internal List<UsageEvent> Timeline { get; } = [];
    public double? RateLimitUsedPercent { get; set; }
    public long? RateLimitWindowMinutes { get; set; }
    public DateTimeOffset? RateLimitResetAtUtc { get; set; }
    public double? SecondaryRateLimitUsedPercent { get; set; }
    public long? SecondaryRateLimitWindowMinutes { get; set; }
    public DateTimeOffset? SecondaryRateLimitResetAtUtc { get; set; }
    public UsageCreditsSnapshot? CreditBalance { get; set; }
    public UsageSpendControl? IndividualLimit { get; set; }
    public string? PlanType { get; set; }
    public DateTimeOffset? RateLimitObservedAtUtc { get; set; }

    public double? RemainingPercent =>
        RateLimitUsedPercent.HasValue ? Math.Max(0, 100 - RateLimitUsedPercent.Value) : null;

    public double? SecondaryRemainingPercent =>
        SecondaryRateLimitUsedPercent.HasValue
            ? Math.Max(0, 100 - SecondaryRateLimitUsedPercent.Value)
            : null;

    public AccountQuotaWindowSnapshot? GetQuotaWindow(AccountQuotaWindowKind kind)
    {
        if (kind == AccountQuotaWindowKind.Unknown)
        {
            return null;
        }

        if (AccountQuotaLimitType.ClassifyWindow(RateLimitWindowMinutes) == kind &&
            RateLimitWindowMinutes.HasValue)
        {
            return new AccountQuotaWindowSnapshot(
                kind,
                RateLimitUsedPercent,
                RateLimitWindowMinutes.Value,
                RateLimitResetAtUtc,
                IsSecondary: false);
        }

        if (AccountQuotaLimitType.ClassifyWindow(SecondaryRateLimitWindowMinutes) == kind &&
            SecondaryRateLimitWindowMinutes.HasValue)
        {
            return new AccountQuotaWindowSnapshot(
                kind,
                SecondaryRateLimitUsedPercent,
                SecondaryRateLimitWindowMinutes.Value,
                SecondaryRateLimitResetAtUtc,
                IsSecondary: true);
        }

        return null;
    }
}

public sealed class UsageBucket
{
    public const long LongContextInputThreshold = 272_000;
    private const string UnknownModelKey = "<unknown>";
    private readonly Dictionary<string, ModelUsageBucket> _modelUsage =
        new(StringComparer.OrdinalIgnoreCase);

    public long InputTokens { get; private set; }
    public long CachedInputTokens { get; private set; }
    public long CacheWriteTokens { get; private set; }
    public int CacheWriteKnownEvents { get; private set; }
    public int CacheWriteUnknownEvents { get; private set; }
    public long CacheWriteUnknownInputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long ReasoningOutputTokens { get; private set; }
    public long TotalTokens { get; private set; }
    public double EquivalentCostOverrideUsd { get; private set; }
    public int Events { get; private set; }
    public int ResponseUsageMatchedEvents { get; private set; }
    public int ResponseUsageDifferenceEvents { get; private set; }
    public IReadOnlyCollection<ModelUsageBucket> ModelUsage => _modelUsage.Values;

    public void Add(UsageEvent usage)
    {
        InputTokens += usage.InputTokens;
        CachedInputTokens += usage.CachedInputTokens;
        var normalizedInput = Math.Max(0L, usage.InputTokens);
        var normalizedCached = Math.Clamp(usage.CachedInputTokens, 0L, normalizedInput);
        var possibleCacheWrite = normalizedInput - normalizedCached;
        if (usage.CacheWriteTokens is long cacheWrite &&
            cacheWrite >= 0L &&
            cacheWrite <= possibleCacheWrite)
        {
            CacheWriteTokens += cacheWrite;
            CacheWriteKnownEvents++;
        }
        else
        {
            CacheWriteUnknownEvents++;
            CacheWriteUnknownInputTokens += possibleCacheWrite;
        }
        OutputTokens += usage.OutputTokens;
        ReasoningOutputTokens += usage.ReasoningOutputTokens;
        TotalTokens += usage.TotalTokens;
        if (usage.EquivalentCostOverrideUsd is double overrideCost &&
            double.IsFinite(overrideCost) &&
            overrideCost >= 0D)
        {
            EquivalentCostOverrideUsd += overrideCost;
        }
        Events++;
        if (usage.ResponseUsageMatched)
        {
            ResponseUsageMatchedEvents++;
            if (usage.ResponseUsageDifferenceFields?.Length > 0)
            {
                ResponseUsageDifferenceEvents++;
            }
        }

        var model = string.IsNullOrWhiteSpace(usage.Model) ? null : usage.Model.Trim();
        var isLongContext = usage.InputTokens > LongContextInputThreshold;
        var key = $"{model ?? UnknownModelKey}|{(isLongContext ? "long" : "short")}";
        if (!_modelUsage.TryGetValue(key, out var modelUsage))
        {
            modelUsage = new ModelUsageBucket(model, isLongContext);
            _modelUsage[key] = modelUsage;
        }

        modelUsage.Add(usage);
    }
}

public sealed class ModelUsageBucket
{
    public ModelUsageBucket(string? model, bool isLongContext)
    {
        Model = model;
        IsLongContext = isLongContext;
    }

    public string? Model { get; }
    public bool IsLongContext { get; }
    public long InputTokens { get; private set; }
    public long CachedInputTokens { get; private set; }
    public long CacheWriteTokens { get; private set; }
    public int CacheWriteKnownEvents { get; private set; }
    public int CacheWriteUnknownEvents { get; private set; }
    public long CacheWriteUnknownInputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long ReasoningOutputTokens { get; private set; }
    public long TotalTokens { get; private set; }
    public double EquivalentCostOverrideUsd { get; private set; }
    public int Events { get; private set; }
    public int ResponseUsageMatchedEvents { get; private set; }
    public int ResponseUsageDifferenceEvents { get; private set; }
    // Exact-cost events (for example imported sub2api bill rows) remain visible in the
    // ordinary Token totals, but their tokens must not also be priced a second time.
    public long PricedInputTokens { get; private set; }
    public long PricedCachedInputTokens { get; private set; }
    public long PricedCacheWriteTokens { get; private set; }
    public long PricedCacheWriteUnknownInputTokens { get; private set; }
    public long PricedOutputTokens { get; private set; }

    internal void Add(UsageEvent usage)
    {
        InputTokens += usage.InputTokens;
        CachedInputTokens += usage.CachedInputTokens;
        var normalizedInput = Math.Max(0L, usage.InputTokens);
        var normalizedCached = Math.Clamp(usage.CachedInputTokens, 0L, normalizedInput);
        var possibleCacheWrite = normalizedInput - normalizedCached;
        if (usage.CacheWriteTokens is long cacheWrite &&
            cacheWrite >= 0L &&
            cacheWrite <= possibleCacheWrite)
        {
            CacheWriteTokens += cacheWrite;
            CacheWriteKnownEvents++;
        }
        else
        {
            CacheWriteUnknownEvents++;
            CacheWriteUnknownInputTokens += possibleCacheWrite;
        }
        OutputTokens += usage.OutputTokens;
        ReasoningOutputTokens += usage.ReasoningOutputTokens;
        TotalTokens += usage.TotalTokens;
        var overrideCost = usage.EquivalentCostOverrideUsd ?? -1D;
        var hasValidCostOverride =
            double.IsFinite(overrideCost) &&
            overrideCost >= 0D;
        if (hasValidCostOverride)
        {
            EquivalentCostOverrideUsd += overrideCost;
        }
        else
        {
            PricedInputTokens += usage.InputTokens;
            PricedCachedInputTokens += usage.CachedInputTokens;
            if (usage.CacheWriteTokens is long pricedCacheWrite &&
                pricedCacheWrite >= 0L &&
                pricedCacheWrite <= possibleCacheWrite)
            {
                PricedCacheWriteTokens += pricedCacheWrite;
            }
            else
            {
                PricedCacheWriteUnknownInputTokens += possibleCacheWrite;
            }
            PricedOutputTokens += usage.OutputTokens;
        }
        Events++;
        if (usage.ResponseUsageMatched)
        {
            ResponseUsageMatchedEvents++;
            if (usage.ResponseUsageDifferenceFields?.Length > 0)
            {
                ResponseUsageDifferenceEvents++;
            }
        }
    }
}

public sealed class UsageEvent
{
    public string? AccountName { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public DateTimeOffset? SessionStartedAtUtc { get; set; }
    public UsageEventSource Source { get; set; } = UsageEventSource.Natural;
    public DateTimeOffset? ActivationEpochUtc { get; set; }
    public double? EquivalentCostOverrideUsd { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long? CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public double? RateLimitUsedPercent { get; set; }
    public long? RateLimitWindowMinutes { get; set; }
    public DateTimeOffset? RateLimitResetAtUtc { get; set; }
    public double? SecondaryRateLimitUsedPercent { get; set; }
    public long? SecondaryRateLimitWindowMinutes { get; set; }
    public DateTimeOffset? SecondaryRateLimitResetAtUtc { get; set; }
    public UsageCreditsSnapshot? CreditBalance { get; set; }
    public UsageSpendControl? IndividualLimit { get; set; }
    public string? PlanType { get; set; }
    public bool ResponseUsageMatched { get; set; }
    public string? ResponseUsageMatchKind { get; set; }
    public string[] ResponseUsageDifferenceFields { get; set; } = [];
    public DateTimeOffset? ResponseUsageResponseTimestampUtc { get; set; }
}

public enum UsageEventSource
{
    Natural = 0,
    LegacyProbe = 1,
    OfficialSnapshot = 2
}
