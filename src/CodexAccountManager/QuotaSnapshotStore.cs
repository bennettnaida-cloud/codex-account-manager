using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

/// <summary>
/// Persists the latest official quota response without storing credentials.  The
/// account identity (auth kind + credential directory) is the only ownership key;
/// account display names are deliberately not used so renames remain safe.
/// </summary>
internal sealed class QuotaSnapshotStore
{
    internal const string FileName = "quota-snapshots-v1.json";
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private static readonly ConcurrentDictionary<string, object> FileGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly object _gate;

    public QuotaSnapshotStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Quota snapshot root is required.", nameof(rootPath));
        }

        var normalizedRoot = System.IO.Path.GetFullPath(rootPath);
        _path = System.IO.Path.Combine(normalizedRoot, ".cache", FileName);
        _gate = FileGates.GetOrAdd(_path, static _ => new object());
    }

    internal string Path => _path;

    internal IReadOnlyDictionary<string, PersistedQuotaSnapshot> LoadForAccounts(
        IReadOnlyCollection<AccountRecord> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var knownKeys = accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.CodexHome))
            .Select(QuotaAccountIdentity.CreateKey)
            .ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            return LoadUnsafe()
                .Accounts
                .Where(item => knownKeys.Contains(item.AccountKey))
                .GroupBy(item => item.AccountKey, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.ObservedAtUtc).First())
                .ToDictionary(item => item.AccountKey, StringComparer.Ordinal);
        }
    }

    internal void Save(AccountRecord account, UsageLimitResetInfo info, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(info);

        var snapshot = new PersistedQuotaSnapshot(
            QuotaAccountIdentity.CreateKey(account),
            observedAtUtc.ToUniversalTime(),
            info.AvailableCount,
            info.Primary,
            info.Secondary,
            info.CreditBalance,
            info.IndividualLimit,
            info.PlanType);
        lock (_gate)
        {
            var file = LoadUnsafe();
            var existing = file.Accounts.FirstOrDefault(item =>
                item.AccountKey.Equals(snapshot.AccountKey, StringComparison.Ordinal));
            if (existing != null && existing.ObservedAtUtc > snapshot.ObservedAtUtc)
            {
                return;
            }

            file.Accounts.RemoveAll(item =>
                item.AccountKey.Equals(snapshot.AccountKey, StringComparison.Ordinal));
            file.Accounts.Add(snapshot);
            SaveUnsafe(file);
        }
    }

    internal void Remove(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        lock (_gate)
        {
            var file = LoadUnsafe();
            if (file.Accounts.RemoveAll(item =>
                    item.AccountKey.Equals(accountKey, StringComparison.Ordinal)) > 0)
            {
                SaveUnsafe(file);
            }
        }
    }

    private SnapshotFile LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new SnapshotFile();
            }

            var file = JsonSerializer.Deserialize<SnapshotFile>(
                           File.ReadAllText(_path),
                           JsonOptions) ??
                       new SnapshotFile();
            file.Accounts ??= [];
            file.Accounts = file.Accounts
                .Where(item => !string.IsNullOrWhiteSpace(item.AccountKey) &&
                               item.ObservedAtUtc > DateTimeOffset.MinValue)
                .GroupBy(item => item.AccountKey, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.ObservedAtUtc).First())
                .ToList();
            file.SchemaVersion = CurrentSchemaVersion;
            return file;
        }
        catch
        {
            // This cache is optional. A damaged file must never prevent account switching
            // or force a network request before the normal refresh path runs.
            return new SnapshotFile();
        }
    }

    private void SaveUnsafe(SnapshotFile file)
    {
        file.SchemaVersion = CurrentSchemaVersion;
        file.Accounts = file.Accounts
            .OrderBy(item => item.AccountKey, StringComparer.Ordinal)
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
                // A leftover temporary file is harmless.
            }
        }
    }

    internal static void ValidateAccountIsolation()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "codex-quota-snapshot-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = new AccountRecord
            {
                Name = "quota-a",
                CodexHome = System.IO.Path.Combine(root, "account-a"),
                AuthKind = AccountAuthKind.AccessToken
            };
            var second = new AccountRecord
            {
                Name = "quota-b",
                CodexHome = System.IO.Path.Combine(root, "account-b"),
                AuthKind = AccountAuthKind.AccessToken
            };
            var store = new QuotaSnapshotStore(root);
            var observedAt = DateTimeOffset.UtcNow;
            store.Save(
                first,
                new UsageLimitResetInfo(
                    2,
                    [],
                    new UsageRateLimitWindow(11, 300, observedAt.AddHours(5)),
                    null,
                    new UsageCreditsSnapshot(true, false, "a"),
                    null,
                    "plan-a"),
                observedAt);
            store.Save(
                second,
                new UsageLimitResetInfo(
                    7,
                    [],
                    new UsageRateLimitWindow(73, 10_080, observedAt.AddDays(7)),
                    null,
                    new UsageCreditsSnapshot(false, true, null),
                    null,
                    "plan-b"),
                observedAt);

            var loaded = store.LoadForAccounts([first, second]);
            var firstKey = QuotaAccountIdentity.CreateKey(first);
            var secondKey = QuotaAccountIdentity.CreateKey(second);
            if (loaded.Count != 2 ||
                !loaded.TryGetValue(firstKey, out var firstSnapshot) ||
                !loaded.TryGetValue(secondKey, out var secondSnapshot) ||
                firstSnapshot.Primary?.UsedPercent != 11 ||
                firstSnapshot.AvailableCount != 2 ||
                secondSnapshot.Primary?.UsedPercent != 73 ||
                secondSnapshot.AvailableCount != 7)
            {
                throw new InvalidOperationException(
                    "Persisted quota snapshots must remain isolated by account identity.");
            }

            var firstOnly = store.LoadForAccounts([first]);
            if (firstOnly.Count != 1 ||
                !firstOnly.ContainsKey(firstKey) ||
                firstOnly.ContainsKey(secondKey))
            {
                throw new InvalidOperationException(
                    "Loading one account must not expose another account's quota snapshot.");
            }

            var renamedFirst = new AccountRecord
            {
                Name = "quota-a-renamed",
                CodexHome = first.CodexHome,
                AuthKind = first.AuthKind
            };
            var renamedKey = QuotaAccountIdentity.CreateKey(renamedFirst);
            var renamedLoad = store.LoadForAccounts([renamedFirst]);
            if (!string.Equals(renamedKey, firstKey, StringComparison.Ordinal) ||
                renamedLoad.Count != 1 ||
                renamedLoad[renamedKey].AvailableCount != 2)
            {
                throw new InvalidOperationException(
                    "Renaming an account must retain its identity-keyed quota snapshot.");
            }

            var durableObservedAt = observedAt.AddMinutes(1);
            var durablePrimaryReset = durableObservedAt.AddDays(7);
            var durableSecondaryReset = durableObservedAt.AddHours(5);
            var durableSpendReset = durableObservedAt.AddDays(30);
            store.Save(
                first,
                new UsageLimitResetInfo(
                    5,
                    [],
                    new UsageRateLimitWindow(13, 10_080, durablePrimaryReset),
                    new UsageRateLimitWindow(17, 300, durableSecondaryReset),
                    new UsageCreditsSnapshot(true, false, "12.34"),
                    new UsageSpendControl("100", "25", 75D, durableSpendReset),
                    "durable-team"),
                durableObservedAt);

            void AssertDurableSnapshot(QuotaSnapshotStore restartedStore, string restartLabel)
            {
                var restartedLoad = restartedStore.LoadForAccounts([first]);
                if (!restartedLoad.TryGetValue(firstKey, out var restartedSnapshot) ||
                    restartedSnapshot.ObservedAtUtc != durableObservedAt.ToUniversalTime() ||
                    restartedSnapshot.AvailableCount != 5 ||
                    restartedSnapshot.Primary?.UsedPercent != 13 ||
                    restartedSnapshot.Primary?.WindowMinutes != 10_080 ||
                    restartedSnapshot.Primary?.ResetsAtUtc != durablePrimaryReset ||
                    restartedSnapshot.Secondary?.UsedPercent != 17 ||
                    restartedSnapshot.Secondary?.WindowMinutes != 300 ||
                    restartedSnapshot.Secondary?.ResetsAtUtc != durableSecondaryReset ||
                    restartedSnapshot.CreditBalance?.Balance != "12.34" ||
                    restartedSnapshot.IndividualLimit?.RemainingPercent != 75D ||
                    restartedSnapshot.IndividualLimit?.ResetsAtUtc != durableSpendReset ||
                    !string.Equals(restartedSnapshot.PlanType, "durable-team", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The official quota snapshot must survive {restartLabel} with every field intact.");
                }
            }

            AssertDurableSnapshot(new QuotaSnapshotStore(root), "the next process start");
            AssertDurableSnapshot(new QuotaSnapshotStore(root), "a later process start");

            // Older writes must never replace a newer account-local observation.
            new QuotaSnapshotStore(root).Save(
                first,
                new UsageLimitResetInfo(
                    99,
                    [],
                    new UsageRateLimitWindow(99, 300, observedAt.AddHours(5)),
                    null,
                    null,
                    null,
                    "stale"),
                observedAt.AddMinutes(-1));
            var afterStaleWrite = new QuotaSnapshotStore(root).LoadForAccounts([first]);
            if (afterStaleWrite[firstKey].Primary?.UsedPercent != 13 ||
                afterStaleWrite[firstKey].ObservedAtUtc != durableObservedAt.ToUniversalTime())
            {
                throw new InvalidOperationException(
                    "An older quota observation must not overwrite the current account snapshot.");
            }

            store.Remove(first);
            if (store.LoadForAccounts([first]).Count != 0 ||
                store.LoadForAccounts([second]).Count != 1)
            {
                throw new InvalidOperationException(
                    "Removing one account's quota snapshot must not remove another account's snapshot.");
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

    private sealed class SnapshotFile
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<PersistedQuotaSnapshot> Accounts { get; set; } = [];
    }
}

internal sealed record PersistedQuotaSnapshot(
    string AccountKey,
    DateTimeOffset ObservedAtUtc,
    long? AvailableCount,
    UsageRateLimitWindow? Primary,
    UsageRateLimitWindow? Secondary,
    UsageCreditsSnapshot? CreditBalance,
    UsageSpendControl? IndividualLimit,
    string? PlanType);
