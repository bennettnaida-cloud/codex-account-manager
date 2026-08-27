namespace CodexAccountManager;

internal enum AccountRotationPool
{
    None,
    Primary,
    Backup
}

internal static class AccountRotationConfiguration
{
    internal const string PrimaryValue = "primary";
    internal const string BackupValue = "backup";
    internal const string NoneValue = "none";
    internal static readonly TimeSpan PrimaryResetGracePeriod = TimeSpan.FromMinutes(1);

    internal static bool IsEnabled(AppSettings settings) =>
        settings.AccountRotationEnabled ?? settings.PatAutoRotationEnabled;

    internal static void SetEnabled(AppSettings settings, bool enabled)
    {
        settings.AccountRotationEnabled = enabled;
        // Keep older portable copies and downgrade installs from silently re-enabling the
        // retired PAT-only switch.
        settings.PatAutoRotationEnabled = enabled;
    }

    internal static bool Normalize(
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accounts);
        var changed = false;

        if (!settings.AccountRotationEnabled.HasValue)
        {
            settings.AccountRotationEnabled = settings.PatAutoRotationEnabled;
            changed = true;
        }

        var keys = accounts
            .Select(QuotaAccountIdentity.CreateKey)
            .ToHashSet(StringComparer.Ordinal);
        settings.AccountRotationPools = NormalizeDictionary(
            settings.AccountRotationPools,
            keys,
            NormalizePoolValue,
            ref changed);
        settings.AccountRotationResetAtUtc = NormalizeDictionary(
            settings.AccountRotationResetAtUtc,
            keys,
            value => value.ToUniversalTime(),
            ref changed);
        settings.CodexFingerprintForwarding = NormalizeDictionary(
            settings.CodexFingerprintForwarding,
            keys,
            value => value,
            ref changed);

        // A pre-2.2.4 configuration had no explicit pools. Preserve its established policy:
        // native/PAT accounts are the normal ring and compatible APIs are the last-resort ring.
        if (settings.AccountRotationPools.Count == 0 && accounts.Count > 0)
        {
            foreach (var account in accounts)
            {
                settings.AccountRotationPools[QuotaAccountIdentity.CreateKey(account)] =
                    account.IsCompatibleApi ? BackupValue : PrimaryValue;
            }
            changed = true;
        }
        else
        {
            foreach (var account in accounts)
            {
                var key = QuotaAccountIdentity.CreateKey(account);
                if (settings.AccountRotationPools.TryAdd(key, NoneValue))
                {
                    changed = true;
                }
            }
        }

        settings.AccountRotationPrimaryOrder = NormalizeOrder(
            settings.AccountRotationPrimaryOrder,
            accounts,
            settings,
            AccountRotationPool.Primary,
            ref changed);
        settings.AccountRotationBackupOrder = NormalizeOrder(
            settings.AccountRotationBackupOrder,
            accounts,
            settings,
            AccountRotationPool.Backup,
            ref changed);

        if (!IsCursorValid(
                settings.AccountRotationPrimaryCursorAccountKey,
                settings.AccountRotationPrimaryOrder))
        {
            if (!string.IsNullOrWhiteSpace(settings.AccountRotationPrimaryCursorAccountKey))
            {
                changed = true;
            }
            settings.AccountRotationPrimaryCursorAccountKey = null;
        }
        if (!IsCursorValid(
                settings.AccountRotationBackupCursorAccountKey,
                settings.AccountRotationBackupOrder))
        {
            if (!string.IsNullOrWhiteSpace(settings.AccountRotationBackupCursorAccountKey))
            {
                changed = true;
            }
            settings.AccountRotationBackupCursorAccountKey = null;
        }

        return changed;
    }

    internal static AccountRotationPool GetPool(AppSettings settings, AccountRecord account) =>
        GetPool(settings, QuotaAccountIdentity.CreateKey(account));

    internal static AccountRotationPool GetPool(AppSettings settings, string accountKey)
    {
        if (!settings.AccountRotationPools.TryGetValue(accountKey, out var value))
        {
            return AccountRotationPool.None;
        }
        return NormalizePoolValue(value) switch
        {
            PrimaryValue => AccountRotationPool.Primary,
            BackupValue => AccountRotationPool.Backup,
            _ => AccountRotationPool.None
        };
    }

    internal static void SetPool(
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        AccountRecord account,
        AccountRotationPool pool)
    {
        var key = QuotaAccountIdentity.CreateKey(account);
        settings.AccountRotationPools[key] = ToValue(pool);
        settings.AccountRotationPrimaryOrder.RemoveAll(value =>
            value.Equals(key, StringComparison.Ordinal));
        settings.AccountRotationBackupOrder.RemoveAll(value =>
            value.Equals(key, StringComparison.Ordinal));
        var order = GetMutableOrder(settings, pool);
        if (order != null)
        {
            order.Add(key);
        }
        Normalize(settings, accounts);
    }

    internal static void Move(
        AppSettings settings,
        AccountRecord account,
        int offset)
    {
        if (offset == 0)
        {
            return;
        }
        var pool = GetPool(settings, account);
        var order = GetMutableOrder(settings, pool);
        if (order == null)
        {
            return;
        }
        var key = QuotaAccountIdentity.CreateKey(account);
        var index = order.FindIndex(value => value.Equals(key, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }
        var target = Math.Clamp(index + Math.Sign(offset), 0, order.Count - 1);
        if (target == index)
        {
            return;
        }
        (order[index], order[target]) = (order[target], order[index]);
    }

    internal static IReadOnlyList<AccountRecord> GetOrderedAccounts(
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        AccountRotationPool pool)
    {
        var byKey = accounts
            .GroupBy(QuotaAccountIdentity.CreateKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var order = pool switch
        {
            AccountRotationPool.Primary => settings.AccountRotationPrimaryOrder,
            AccountRotationPool.Backup => settings.AccountRotationBackupOrder,
            _ => []
        };
        return order
            .Where(byKey.ContainsKey)
            .Select(key => byKey[key])
            .Where(account => GetPool(settings, account) == pool)
            .ToList();
    }

    internal static IReadOnlyList<AccountRecord> BuildCandidates(
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        AccountRecord current,
        IReadOnlySet<string> unavailableAccountKeys,
        DateTimeOffset nowUtc,
        Func<AccountRecord, bool> hasUsableCredential,
        Func<AccountRecord, bool>? canSelectAccount = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(unavailableAccountKeys);
        ArgumentNullException.ThrowIfNull(hasUsableCredential);
        canSelectAccount ??= static _ => true;

        var currentKey = QuotaAccountIdentity.CreateKey(current);
        var primary = BuildEligibleRing(
            settings,
            accounts,
            AccountRotationPool.Primary,
            currentKey,
            unavailableAccountKeys,
            nowUtc,
            hasUsableCredential,
            canSelectAccount);
        var backup = BuildEligibleRing(
            settings,
            accounts,
            AccountRotationPool.Backup,
            currentKey,
            unavailableAccountKeys,
            nowUtc,
            hasUsableCredential,
            canSelectAccount);

        // Never expose backup candidates while any primary candidate is still eligible.
        // This prevents a transient/unknown primary quota probe from skipping directly to
        // backup. Reset timestamps keep exhausted primary accounts out until reset + one
        // minute, after which the next request naturally returns to the primary ring.
        return primary.Count > 0
            ? primary
            : backup;
    }

    internal static void MarkUsed(
        AppSettings settings,
        AccountRecord account)
    {
        var key = QuotaAccountIdentity.CreateKey(account);
        switch (GetPool(settings, key))
        {
            case AccountRotationPool.Primary:
                settings.AccountRotationPrimaryCursorAccountKey = key;
                break;
            case AccountRotationPool.Backup:
                settings.AccountRotationBackupCursorAccountKey = key;
                break;
        }
    }

    internal static void RecordExhaustedReset(
        AppSettings settings,
        AccountRecord account,
        DateTimeOffset? resetAtUtc)
    {
        var key = QuotaAccountIdentity.CreateKey(account);
        if (resetAtUtc.HasValue)
        {
            settings.AccountRotationResetAtUtc[key] = resetAtUtc.Value.ToUniversalTime();
        }
        else
        {
            settings.AccountRotationResetAtUtc.Remove(key);
        }
    }

    internal static void RecordAvailable(
        AppSettings settings,
        AccountRecord account)
    {
        settings.AccountRotationResetAtUtc.Remove(QuotaAccountIdentity.CreateKey(account));
    }

    internal static bool IsResetGraceElapsed(
        AppSettings settings,
        string accountKey,
        DateTimeOffset nowUtc)
    {
        return !settings.AccountRotationResetAtUtc.TryGetValue(accountKey, out var resetAtUtc) ||
               resetAtUtc + PrimaryResetGracePeriod <= nowUtc;
    }

    internal static bool IsFingerprintForwardingEnabled(
        AppSettings settings,
        AccountRecord account) =>
        settings.CodexFingerprintForwarding.TryGetValue(
            QuotaAccountIdentity.CreateKey(account),
            out var enabled) && enabled;

    internal static void ToggleFingerprintForwarding(
        AppSettings settings,
        AccountRecord account)
    {
        var key = QuotaAccountIdentity.CreateKey(account);
        settings.CodexFingerprintForwarding[key] =
            !settings.CodexFingerprintForwarding.TryGetValue(key, out var enabled) || !enabled;
    }

    internal static void Validate()
    {
        var root = Path.Combine(Path.GetTempPath(), "account-rotation-plan-test");
        var a = new AccountRecord { Name = "a", CodexHome = Path.Combine(root, "a") };
        var b = new AccountRecord { Name = "b", CodexHome = Path.Combine(root, "b") };
        var oauth = new AccountRecord
        {
            Name = "oauth",
            CodexHome = Path.Combine(root, "oauth"),
            AuthKind = AccountAuthKind.OfficialOAuth
        };
        var api = new AccountRecord
        {
            Name = "api",
            CodexHome = Path.Combine(root, "api"),
            AuthKind = AccountAuthKind.CompatibleApi
        };
        var accounts = new[] { a, b, oauth, api };
        var settings = new AppSettings
        {
            PatAutoRotationEnabled = false,
            AccountRotationEnabled = null
        };
        if (!Normalize(settings, accounts) || IsEnabled(settings) ||
            !GetOrderedAccounts(settings, accounts, AccountRotationPool.Primary)
                .Select(account => account.Name)
                .SequenceEqual(["a", "b", "oauth"]) ||
            !GetOrderedAccounts(settings, accounts, AccountRotationPool.Backup)
                .Select(account => account.Name)
                .SequenceEqual(["api"]))
        {
            throw new InvalidOperationException("Account rotation migration or default pools failed.");
        }

        SetEnabled(settings, true);
        MarkUsed(settings, a);
        var bKey = QuotaAccountIdentity.CreateKey(b);
        RecordExhaustedReset(settings, b, DateTimeOffset.UtcNow.AddMinutes(5));
        var candidates = BuildCandidates(
            settings,
            accounts,
            a,
            new HashSet<string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow,
            _ => true);
        if (!candidates.Select(account => account.Name).SequenceEqual(["oauth"]))
        {
            throw new InvalidOperationException(
                "Primary selection did not preserve its circular cursor, reset grace, or backup isolation.");
        }

        settings.AccountRotationResetAtUtc[bKey] = DateTimeOffset.UtcNow.AddMinutes(-2);
        var unavailable = new HashSet<string>(StringComparer.Ordinal) { bKey };
        candidates = BuildCandidates(
            settings,
            accounts,
            a,
            unavailable,
            DateTimeOffset.UtcNow,
            _ => true);
        if (!candidates.Select(account => account.Name).Take(2).SequenceEqual(["b", "oauth"]))
        {
            throw new InvalidOperationException(
                "A reset primary account did not return after its one-minute grace period.");
        }


        SetPool(settings, accounts, oauth, AccountRotationPool.Primary);
        candidates = BuildCandidates(
            settings,
            accounts,
            a,
            new HashSet<string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow,
            _ => true,
            account => !account.IsOfficialOAuth);
        if (candidates.Any(account => account.IsOfficialOAuth))
        {
            throw new InvalidOperationException(
                "A runtime capability filter did not exclude its requested account kind.");
        }

        MarkUsed(settings, api);
        var afterRestart = BuildCandidates(
            settings,
            accounts,
            api,
            new HashSet<string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow,
            _ => true);
        if (afterRestart.Count == 0 || afterRestart[0].Name != "b")
        {
            throw new InvalidOperationException(
                "The persisted ring cursor did not resume with the next primary account.");
        }
    }

    private static List<AccountRecord> BuildEligibleRing(
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        AccountRotationPool pool,
        string currentKey,
        IReadOnlySet<string> unavailableAccountKeys,
        DateTimeOffset nowUtc,
        Func<AccountRecord, bool> hasUsableCredential,
        Func<AccountRecord, bool> canSelectAccount)
    {
        var ordered = GetOrderedAccounts(settings, accounts, pool).ToList();
        if (ordered.Count == 0)
        {
            return [];
        }
        var cursor = pool == AccountRotationPool.Primary
            ? settings.AccountRotationPrimaryCursorAccountKey
            : settings.AccountRotationBackupCursorAccountKey;
        var anchor = ordered.FindIndex(account =>
            QuotaAccountIdentity.CreateKey(account).Equals(cursor, StringComparison.Ordinal));
        if (anchor < 0)
        {
            anchor = ordered.FindIndex(account =>
                QuotaAccountIdentity.CreateKey(account).Equals(currentKey, StringComparison.Ordinal));
        }

        return Enumerable.Range(1, ordered.Count)
            .Select(offset => ordered[(anchor + offset + ordered.Count) % ordered.Count])
            .Where(account =>
            {
                var key = QuotaAccountIdentity.CreateKey(account);
                return !key.Equals(currentKey, StringComparison.Ordinal) &&
                       !IsStillUnavailable(
                           settings,
                           key,
                           unavailableAccountKeys,
                           nowUtc) &&
                       IsResetGraceElapsed(settings, key, nowUtc) &&
                       hasUsableCredential(account) &&
                       canSelectAccount(account);
            })
            .ToList();
    }

    private static bool IsStillUnavailable(
        AppSettings settings,
        string accountKey,
        IReadOnlySet<string> unavailableAccountKeys,
        DateTimeOffset nowUtc)
    {
        if (!unavailableAccountKeys.Contains(accountKey))
        {
            return false;
        }

        // The in-memory set is a fast signal, not permanent truth. Once an observed reset
        // plus the one-minute server-delay guard has elapsed, a stale set entry must not
        // prevent the primary ring from recovering after a manager restart or missed prune.
        return !settings.AccountRotationResetAtUtc.TryGetValue(accountKey, out var resetAtUtc) ||
               resetAtUtc + PrimaryResetGracePeriod > nowUtc;
    }

    private static Dictionary<string, T> NormalizeDictionary<T>(
        Dictionary<string, T>? source,
        IReadOnlySet<string> validKeys,
        Func<T, T> normalizeValue,
        ref bool changed)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var pair in source ?? [])
        {
            var key = pair.Key?.Trim().ToUpperInvariant();
            if (key == null || !validKeys.Contains(key))
            {
                changed = true;
                continue;
            }
            var value = normalizeValue(pair.Value);
            result[key] = value;
            if (!key.Equals(pair.Key, StringComparison.Ordinal) ||
                !EqualityComparer<T>.Default.Equals(value, pair.Value))
            {
                changed = true;
            }
        }
        if ((source?.Count ?? 0) != result.Count)
        {
            changed = true;
        }
        return result;
    }

    private static List<string> NormalizeOrder(
        List<string>? source,
        IReadOnlyList<AccountRecord> accounts,
        AppSettings settings,
        AccountRotationPool pool,
        ref bool changed)
    {
        var eligible = accounts
            .Where(account => GetPool(settings, account) == pool)
            .Select(QuotaAccountIdentity.CreateKey)
            .ToList();
        var eligibleSet = eligible.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in source ?? [])
        {
            var key = raw?.Trim().ToUpperInvariant();
            if (key == null || !eligibleSet.Contains(key) || !seen.Add(key))
            {
                changed = true;
                continue;
            }
            result.Add(key);
            if (!key.Equals(raw, StringComparison.Ordinal))
            {
                changed = true;
            }
        }
        foreach (var key in eligible)
        {
            if (seen.Add(key))
            {
                result.Add(key);
                changed = true;
            }
        }
        if ((source?.Count ?? 0) != result.Count)
        {
            changed = true;
        }
        return result;
    }

    private static string NormalizePoolValue(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            PrimaryValue => PrimaryValue,
            BackupValue => BackupValue,
            _ => NoneValue
        };

    private static string ToValue(AccountRotationPool pool) => pool switch
    {
        AccountRotationPool.Primary => PrimaryValue,
        AccountRotationPool.Backup => BackupValue,
        _ => NoneValue
    };

    private static List<string>? GetMutableOrder(
        AppSettings settings,
        AccountRotationPool pool) => pool switch
    {
        AccountRotationPool.Primary => settings.AccountRotationPrimaryOrder,
        AccountRotationPool.Backup => settings.AccountRotationBackupOrder,
        _ => null
    };

    private static bool IsCursorValid(string? cursor, IReadOnlyList<string> order) =>
        string.IsNullOrWhiteSpace(cursor) ||
        order.Contains(cursor.Trim().ToUpperInvariant(), StringComparer.Ordinal);
}
