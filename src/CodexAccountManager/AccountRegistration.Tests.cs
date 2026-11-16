using System.Text.Json;

namespace CodexAccountManager;

public sealed partial class AccountStore
{
    internal static void ValidateRegistrationIsolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AccountStore(root);
            var original = new AccountRecord { Name = "same@example.invalid", CodexHome = Path.Combine(root, "same-home") };
            var untouched = new AccountRecord { Name = "other@example.invalid", CodexHome = Path.Combine(root, "other-home") };
            // Simulate pre-upgrade accounts. Reading/saving legacy entries must not move keys.
            store.SaveAccounts([original, untouched]);
            var oldKey = QuotaAccountIdentity.CreateKey(original);
            store.SaveAccount(original, original.Name, null);
            if (QuotaAccountIdentity.CreateKey(original) != oldKey || original.RegistrationId != null)
                throw new InvalidOperationException("Upgrade/edit changed a legacy account identity.");
            var snapshots = new QuotaSnapshotStore(root);
            var monitor = new PassiveQuotaMonitoringService(root);
            var info = new UsageLimitResetInfo(null, [], null,
                new UsageRateLimitWindow(98, 10080, DateTimeOffset.UtcNow.AddDays(2)), null, null, null);
            snapshots.Save(original, info, DateTimeOffset.UtcNow);
            snapshots.Save(untouched, info, DateTimeOffset.UtcNow);
            monitor.Enable(original, DateTimeOffset.UtcNow, 98, 10080);
            monitor.Enable(untouched, DateTimeOffset.UtcNow, 98, 10080);
            var signals = new PatGatewayQuotaSignalStore(root);
            signals.Record(oldKey, DateTimeOffset.UtcNow);
            store.DeleteAccount(original);
            if (snapshots.LoadForAccounts([original, untouched]).ContainsKey(oldKey) ||
                monitor.GetState(original).IsEnabled ||
                !snapshots.LoadForAccounts([untouched]).ContainsKey(QuotaAccountIdentity.CreateKey(untouched)) ||
                !monitor.GetState(untouched).IsEnabled)
                throw new InvalidOperationException("Deletion did not isolate cache cleanup to the deleted account.");
            if (signals.ReadLatestForAccount(oldKey, DateTimeOffset.UtcNow) != null)
                throw new InvalidOperationException("Deleted quota-exhaustion signal was retained.");

            var replacement = new AccountRecord { Name = original.Name, CodexHome = original.CodexHome };
            store.SaveAccount(replacement, null, null);
            var newKey = QuotaAccountIdentity.CreateKey(replacement);
            if (oldKey == newKey || replacement.RegisteredAtUtc == null || replacement.RegistrationId == null)
                throw new InvalidOperationException("Re-adding the same name and directory reused the old identity.");
            // Simulate a late callback from the deleted registration or an older process.
            snapshots.Save(original, info, DateTimeOffset.UtcNow.AddSeconds(1));
            monitor.Enable(original, DateTimeOffset.UtcNow.AddSeconds(1), 98, 10080);
            if (snapshots.LoadForAccounts([replacement]).Count != 0 || monitor.GetState(replacement).IsEnabled)
                throw new InvalidOperationException("A late old quota write reached the new account.");
            UsageTracker.ValidateRegistrationHistoryIsolation(original, replacement);
            UsageTracker.ValidateReaddedAccountReports(root, original, replacement);
            try
            {
                store.DeleteAccount(original);
                throw new InvalidOperationException("Stale UI account deleted the new registration.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("已重新添加", StringComparison.Ordinal)) { }

            var renamed = new AccountRecord { Name = "renamed@example.invalid", CodexHome = replacement.CodexHome };
            store.SaveAccount(renamed, replacement.Name, null);
            if (QuotaAccountIdentity.CreateKey(renamed) != newKey ||
                store.LoadAccounts().Single(x => x.Name == renamed.Name).RegistrationId != replacement.RegistrationId)
                throw new InvalidOperationException("Rename or reload lost registration identity.");

            store.DeleteAccount(renamed);
            store.ImportAccounts(AccountFileImport.Parse("{\"name\":\"same@example.invalid\",\"OPENAI_API_KEY\":\"at-fixture-registration-only\"}").Entries);
            var imported = store.LoadAccounts().Single(x => x.Name != untouched.Name);
            if (string.IsNullOrWhiteSpace(imported.RegistrationId))
                throw new InvalidOperationException("File import did not create a fresh registration.");
            var importKey = QuotaAccountIdentity.CreateKey(imported);
            store.DeleteAccount(imported);
            store.ImportAccounts(AccountFileImport.Parse("{\"name\":\"same@example.invalid\",\"OPENAI_API_KEY\":\"at-fixture-registration-only\"}").Entries);
            if (QuotaAccountIdentity.CreateKey(store.LoadAccounts().Single(x => x.Name != untouched.Name)) == importKey)
                throw new InvalidOperationException("Re-import reused a deleted registration.");
            Console.WriteLine("Account registration: same email/home, delayed quota writes, deletion cleanup, rename, reload and re-import passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public sealed partial class UsageTracker
{
    internal static void ValidateReaddedAccountReports(string root, AccountRecord deleted, AccountRecord added)
    {
        var previousShared = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME");
        var shared = Path.Combine(root, "shared");
        var sessions = Path.Combine(shared, "sessions");
        Directory.CreateDirectory(sessions);
        var registered = added.RegisteredAtUtc!.Value;
        var oldTime = registered.AddMinutes(-2);
        var newTime = registered.AddMinutes(2);
        try
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", shared);
            File.WriteAllText(Path.Combine(root, "usage-account-switches.json"), JsonSerializer.Serialize(new[] {
                new UsageSwitchEvent { AccountName = deleted.Name, AccountKey = QuotaAccountIdentity.CreateKey(deleted),
                    SwitchedAtUtc = oldTime.AddMinutes(-1).ToString("O"), Source = "switch" },
                new UsageSwitchEvent { AccountName = added.Name, AccountKey = QuotaAccountIdentity.CreateKey(added),
                    SwitchedAtUtc = registered.AddMinutes(1).ToString("O"), Source = "switch" }
            }));
            var oldLog = Path.Combine(sessions, "old.jsonl");
            File.WriteAllText(oldLog, MakeTokenCountFixture(oldTime, 10, 98));
            File.WriteAllText(Path.Combine(sessions, "new.jsonl"), MakeTokenCountFixture(newTime, 7, 5));
            File.WriteAllText(Path.Combine(root, ProbeUsageLedger.FileName), JsonSerializer.Serialize(new {
                SchemaVersion = 1, Events = new[] { new ProbeUsageRecord("old-probe", QuotaAccountIdentity.CreateKey(deleted),
                    deleted.Name, oldTime, 11, 0, 0, 11, 0.01, "fixture-probe") }
            }));
            File.WriteAllText(Path.Combine(root, Sub2ApiUsageLedger.FileName), JsonSerializer.Serialize(new {
                SchemaVersion = 1, Events = new[] { new Sub2ApiUsageRecord { EventId = "old-relay", AccountKey = QuotaAccountIdentity.CreateKey(deleted),
                    AccountName = deleted.Name, CompletedAtUtc = oldTime, TotalTokens = 13, InputTokens = 13, Model = "fixture-relay", BilledCostUsd = 0.02M } }
            }));
            var tracker = new UsageTracker(root);
            Check(tracker); Check(tracker); Check(new UsageTracker(root));
            if (!File.Exists(oldLog)) throw new InvalidOperationException("Historical chat log was deleted.");
            void Check(UsageTracker current)
            {
                var report = current.BuildReport([added], sessions, newTime.AddMinutes(1));
                var summary = report.Accounts.Single();
                if (summary.Timeline.Sum(x => x.TotalTokens) != 7 || summary.RateLimitUsedPercent != 5 ||
                    summary.Timeline.Any(x => x.AccountKey != QuotaAccountIdentity.CreateKey(added)))
                    throw new InvalidOperationException("Re-added account inherited old logs/ledger in a cold, warm or restarted usage report.");
            }
        }
        finally { Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", previousShared); }
    }

    internal static void ValidateRegistrationHistoryIsolation(AccountRecord deleted, AccountRecord added)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var started = added.RegisteredAtUtc!.Value;
        var newKey = QuotaAccountIdentity.CreateKey(added);
        var summary = new AccountUsageSummary { AccountName = added.Name, AccountKey = newKey, RegisteredAtUtc = started };
        var summaries = new Dictionary<string, AccountUsageSummary>(StringComparer.OrdinalIgnoreCase) { [added.Name] = summary };
        var report = new UsageReport();
        // Cached natural logs, probe ledger and recovered relay ledger all use this
        // aggregation path. A mismatched key must never fall back to the same email.
        foreach (var source in new[] { UsageEventSource.Natural, UsageEventSource.LegacyProbe, UsageEventSource.OfficialSnapshot })
            Add(new UsageEvent { AccountKey = QuotaAccountIdentity.CreateKey(deleted), AccountName = added.Name,
                TimestampUtc = now, TotalTokens = 10, RateLimitUsedPercent = 98, RateLimitWindowMinutes = 10080, Source = source });
        Add(new UsageEvent { AccountName = added.Name, TimestampUtc = started.AddSeconds(-1), TotalTokens = 10 });
        Add(new UsageEvent { AccountName = added.Name, TimestampUtc = now, ActivationEpochUtc = started.AddDays(-1), TotalTokens = 10 });
        if (summary.Timeline.Count != 0 || summary.Month.TotalTokens != 0 || summary.RateLimitUsedPercent != null)
            throw new InvalidOperationException("Old keyed or legacy-name usage leaked into a re-added account.");
        Add(new UsageEvent { AccountKey = newKey, AccountName = "old-display-name", TimestampUtc = now, TotalTokens = 7 });
        Add(new UsageEvent { AccountName = added.Name, TimestampUtc = now, ActivationEpochUtc = started.AddSeconds(1), TotalTokens = 8 });
        if (summary.Month.TotalTokens != 15 || summary.Timeline.Count != 2 || report.UnassignedMonth.TotalTokens != 40)
            throw new InvalidOperationException("Current usage was lost or deleted-account history was not retained separately.");
        void Add(UsageEvent usage) => AddUsage(report, summaries, usage,
            now.AddDays(-30), now.AddDays(-30), now.AddDays(-30), now.AddDays(-30), now.AddDays(-30), now.AddDays(-30));
    }
}
