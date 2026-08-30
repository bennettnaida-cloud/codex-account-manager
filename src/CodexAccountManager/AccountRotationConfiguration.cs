namespace CodexAccountManager;

internal enum AccountRotationPool
{
    None,
    Primary,
    Backup
}

internal static class AccountRotationConfiguration
{
    internal const int CurrentQuotaEvidenceVersion = 3;
    internal const string PrimaryValue = "primary";
    internal const string BackupValue = "backup";
    internal const string NoneValue = "none";
    internal const string FingerprintGatewayDefaultValue = "gateway_default";
    // sub2api calls its unmodified/pass-through convergence mode "off".  CAM keeps
    // gateway_default as a separate compatibility state so an old false toggle never
    // starts forwarding additional client metadata after an upgrade.
    internal const string FingerprintPassthroughValue = "off";
    internal const string FingerprintDeviceValue = "device";
    internal const string FingerprintSessionValue = "session";
    internal const string FingerprintFullValue = "full";
    internal static readonly TimeSpan PrimaryResetGracePeriod = TimeSpan.FromMinutes(1);
    private static readonly ISet<string> EmptyAccountKeySet =
        new HashSet<string>(StringComparer.Ordinal);

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
        if (settings.AccountRotationSessionAffinityTtlSeconds is < 60 or > 86_400)
        {
            settings.AccountRotationSessionAffinityTtlSeconds = 3600;
            changed = true;
        }

        // Versions before 2.2.9 could persist a body-only HTTP 429 as an account reset.
        // Those entries cannot be distinguished from a real quota reset and would keep
        // skipping healthy A/B accounts even after the gateway classifier is fixed.
        // Retire them exactly once; schema-3 confirmed gateway signals and fresh official
        // 100% observations are reconciled back into this map below the normal paths.
        if (settings.AccountRotationQuotaEvidenceVersion != CurrentQuotaEvidenceVersion)
        {
            settings.AccountRotationResetAtUtc?.Clear();
            settings.AccountRotationQuotaEvidenceVersion = CurrentQuotaEvidenceVersion;
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
        settings.CodexFingerprintModes = NormalizeDictionary(
            settings.CodexFingerprintModes,
            keys,
            NormalizeFingerprintModeValue,
            ref changed);
        settings.CodexFingerprintSeeds = NormalizeDictionary(
            settings.CodexFingerprintSeeds,
            keys,
            NormalizeFingerprintSeed,
            ref changed);
        foreach (var invalidSeedKey in settings.CodexFingerprintSeeds
                     .Where(pair => string.IsNullOrEmpty(pair.Value))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            settings.CodexFingerprintSeeds.Remove(invalidSeedKey);
            changed = true;
        }

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
        if (settings.AccountRotationPools?.TryGetValue(accountKey, out var value) != true)
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

    /// <summary>
    /// Returns the destination used by the compact per-account pool toggle.
    /// The toggle intentionally only switches between the two participating pools;
    /// <see cref="AccountRotationPool.None"/> is handled by its own explicit action.
    /// </summary>
    internal static AccountRotationPool GetInteractivePoolToggleTarget(AccountRotationPool current) =>
        current switch
        {
            AccountRotationPool.Primary => AccountRotationPool.Backup,
            AccountRotationPool.Backup => AccountRotationPool.Primary,
            _ => AccountRotationPool.Primary
        };

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
        return (order ?? [])
            .Where(byKey.ContainsKey)
            .Select(key => byKey[key])
            .Where(account => GetPool(settings, account) == pool)
            .ToList();
    }

    /// <summary>
    /// Builds the order used by the quota dashboard when rotation is enabled.
    ///
    /// This is deliberately a presentation-only projection.  It never changes a
    /// cursor, checks a credential, or probes a quota endpoint.  The currently
    /// serving account is placed at the head and the configured ring then wraps
    /// around it (for example 3,4,5,6,7,8,1,2).  Accounts in the other participating
    /// pool and accounts that are not assigned to a pool are retained after the
    /// configured ring so filtering/grouping cannot make an account disappear.
    /// </summary>
    internal static IReadOnlyList<AccountRecord> GetQuotaDisplayOrder(
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        string? currentAccountKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accounts);

        if (accounts.Count <= 1 || !IsEnabled(settings))
        {
            return accounts.ToList();
        }

        var byKey = accounts
            .GroupBy(QuotaAccountIdentity.CreateKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var primary = GetOrderedAccounts(settings, accounts, AccountRotationPool.Primary);
        var backup = GetOrderedAccounts(settings, accounts, AccountRotationPool.Backup);
        var normalizedCurrentKey = string.IsNullOrWhiteSpace(currentAccountKey)
            ? null
            : currentAccountKey.Trim().ToUpperInvariant();
        var currentPool = string.IsNullOrWhiteSpace(normalizedCurrentKey)
            ? AccountRotationPool.None
            : GetPool(settings, normalizedCurrentKey);
        var currentAccountExists = normalizedCurrentKey != null &&
            byKey.ContainsKey(normalizedCurrentKey);

        // Keep the active pool's ring contiguous.  If the current primary account is
        // #3, the primary cards must read 3,4,5,6,7,8,1,2; inserting backup accounts
        // between 8 and 1 would make the visual order disagree with the configured
        // primary rotation.  The other pool is appended only after that complete ring.
        var activePool = currentPool == AccountRotationPool.Backup
            ? AccountRotationPool.Backup
            : AccountRotationPool.Primary;
        var activeConfigured = activePool == AccountRotationPool.Backup ? backup : primary;
        var otherConfigured = activePool == AccountRotationPool.Backup ? primary : backup;
        var activeRing = new List<AccountRecord>(activeConfigured.Count);
        var otherRing = new List<AccountRecord>(otherConfigured.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AppendTo(
            List<AccountRecord> destination,
            IEnumerable<AccountRecord> source)
        {
            foreach (var account in source)
            {
                var key = QuotaAccountIdentity.CreateKey(account);
                if (byKey.ContainsKey(key) && seen.Add(key))
                {
                    destination.Add(byKey[key]);
                }
            }
        }

        AppendTo(activeRing, activeConfigured);
        AppendTo(otherRing, otherConfigured);

        // Normalize() normally makes these lists complete.  The defensive pass is
        // useful while an older settings file is being upgraded or while another
        // process is editing it; it keeps the dashboard deterministic without
        // silently dropping an account.
        AppendTo(activeRing, accounts.Where(account =>
            GetPool(settings, account) == activePool));
        AppendTo(otherRing, accounts.Where(account =>
            GetPool(settings, account) is AccountRotationPool.Primary or AccountRotationPool.Backup));
        var unassigned = accounts
            .Where(account => GetPool(settings, account) == AccountRotationPool.None)
            .ToList();

        if (activeRing.Count == 0 && otherRing.Count == 0 && unassigned.Count == 0)
        {
            return accounts.ToList();
        }

        var anchor = -1;
        if (!string.IsNullOrWhiteSpace(normalizedCurrentKey))
        {
            anchor = activeRing.FindIndex(account =>
                QuotaAccountIdentity.CreateKey(account).Equals(
                    normalizedCurrentKey,
                    StringComparison.Ordinal));
        }

        if (anchor < 0 && !currentAccountExists)
        {
            // After a restart the shared credential may not be observable yet.  A
            // persisted cursor is only a display fallback in that case; it does not
            // participate in request selection.
            var cursor = currentPool == AccountRotationPool.Backup
                ? settings.AccountRotationBackupCursorAccountKey
                : settings.AccountRotationPrimaryCursorAccountKey;
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                cursor = cursor.Trim().ToUpperInvariant();
                anchor = activeRing.FindIndex(account =>
                    QuotaAccountIdentity.CreateKey(account).Equals(
                        cursor,
                        StringComparison.Ordinal));
            }
        }

        // The pool-building pass uses its own de-duplication set.  Reset it before
        // composing the final list so the already collected pool members are not
        // mistaken for entries that have already been appended to the result.
        seen.Clear();
        var result = new List<AccountRecord>(accounts.Count);
        if (anchor >= 0)
        {
            AppendTo(result, activeRing.Skip(anchor));
            AppendTo(result, activeRing.Take(anchor));
        }
        else
        {
            // If the current account is deliberately outside both participating
            // pools, still keep that visible account first; the configured rings then
            // retain their normal primary/backup order.
            if (!string.IsNullOrWhiteSpace(normalizedCurrentKey) &&
                currentPool == AccountRotationPool.None)
            {
                AppendTo(result, accounts.Where(account =>
                    QuotaAccountIdentity.CreateKey(account).Equals(
                        normalizedCurrentKey,
                        StringComparison.Ordinal)));
            }
            AppendTo(result, activeRing);
        }

        AppendTo(result, otherRing);
        AppendTo(result, unassigned);
        AppendTo(result, accounts);
        return result;
    }

    internal static IReadOnlyList<AccountRecord> BuildCandidates(
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        AccountRecord current,
        IReadOnlySet<string> unavailableAccountKeys,
        DateTimeOffset nowUtc,
        Func<AccountRecord, bool> hasUsableCredential,
        Func<AccountRecord, bool>? canSelectAccount = null,
        ISet<string>? hardUnavailableAccountKeys = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(unavailableAccountKeys);
        ArgumentNullException.ThrowIfNull(hasUsableCredential);
        canSelectAccount ??= static _ => true;
        hardUnavailableAccountKeys ??= EmptyAccountKeySet;

        var currentKey = QuotaAccountIdentity.CreateKey(current);
        var primary = BuildEligibleRing(
            settings,
            accounts,
            AccountRotationPool.Primary,
            currentKey,
            unavailableAccountKeys,
            nowUtc,
            hasUsableCredential,
            canSelectAccount,
            hardUnavailableAccountKeys);
        var backup = BuildEligibleRing(
            settings,
            accounts,
            AccountRotationPool.Backup,
            currentKey,
            unavailableAccountKeys,
            nowUtc,
            hasUsableCredential,
            canSelectAccount,
            hardUnavailableAccountKeys);

        // Never expose backup candidates while any primary candidate is still eligible.
        // This prevents a transient/unknown primary quota probe from skipping directly to
        // backup. Reset timestamps keep exhausted primary accounts out until reset + one
        // minute, after which the next request naturally returns to the primary ring.
        return primary.Count > 0
            ? primary
            : backup;
    }

    /// <summary>
    /// Selects exactly one next account from the configured ring.  The caller is expected
    /// to add the returned key to <paramref name="hardUnavailableAccountKeys"/> before
    /// probing or sending a request, then call this method again only after that attempt
    /// fails.  Keeping this operation singular is important: building a complete candidate
    /// list encourages quota/API preflights to walk ahead of the first usable account and
    /// makes a later account observable before it is needed.
    ///
    /// Primary accounts always win over backup accounts.  The backup ring is considered
    /// only when no primary account is currently eligible under the supplied snapshot.
    /// No network or quota operation is performed here; <paramref name="hasUsableCredential"/>
    /// is the only caller-supplied predicate evaluated while scanning the ring.
    /// </summary>
    internal static bool TrySelectNextCandidate(
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        AccountRecord current,
        IReadOnlySet<string> unavailableAccountKeys,
        DateTimeOffset nowUtc,
        Func<AccountRecord, bool> hasUsableCredential,
        out AccountRecord? candidate,
        out AccountRotationPool pool,
        Func<AccountRecord, bool>? canSelectAccount = null,
        ISet<string>? hardUnavailableAccountKeys = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(unavailableAccountKeys);
        ArgumentNullException.ThrowIfNull(hasUsableCredential);
        canSelectAccount ??= static _ => true;
        hardUnavailableAccountKeys ??= EmptyAccountKeySet;

        var currentKey = QuotaAccountIdentity.CreateKey(current);
        candidate = FindFirstEligible(
            settings,
            accounts,
            AccountRotationPool.Primary,
            currentKey,
            unavailableAccountKeys,
            nowUtc,
            hasUsableCredential,
            canSelectAccount,
            hardUnavailableAccountKeys);
        if (candidate != null)
        {
            pool = AccountRotationPool.Primary;
            return true;
        }

        candidate = FindFirstEligible(
            settings,
            accounts,
            AccountRotationPool.Backup,
            currentKey,
            unavailableAccountKeys,
            nowUtc,
            hasUsableCredential,
            canSelectAccount,
            hardUnavailableAccountKeys);
        if (candidate != null)
        {
            pool = AccountRotationPool.Backup;
            return true;
        }

        pool = AccountRotationPool.None;
        return false;
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

    internal static CodexFingerprintMode GetFingerprintMode(
        AppSettings settings,
        AccountRecord account) =>
        GetFingerprintMode(settings, QuotaAccountIdentity.CreateKey(account));

    internal static CodexFingerprintMode GetFingerprintMode(
        AppSettings settings,
        string accountKey)
    {
        if (settings.CodexFingerprintModes?.TryGetValue(accountKey, out var configured) == true)
        {
            return ParseFingerprintMode(configured);
        }

        // Legacy true meant "forward the allow-listed client metadata unchanged",
        // which is the same observable behavior as sub2api's off mode. Legacy false
        // remains CAM's metadata-stripping gateway default.
        return settings.CodexFingerprintForwarding?.TryGetValue(accountKey, out var enabled) == true && enabled
            ? CodexFingerprintMode.Passthrough
            : CodexFingerprintMode.GatewayDefault;
    }

    internal static string? GetFingerprintSeed(AppSettings settings, string accountKey)
    {
        return settings.CodexFingerprintSeeds?.TryGetValue(accountKey, out var value) == true &&
               Guid.TryParseExact(value, "D", out var parsed) &&
               parsed != Guid.Empty
            ? parsed.ToString("D")
            : null;
    }

    internal static void SetFingerprintMode(
        AppSettings settings,
        AccountRecord account,
        CodexFingerprintMode mode)
    {
        var key = QuotaAccountIdentity.CreateKey(account);
        settings.CodexFingerprintModes ??= new Dictionary<string, string>(StringComparer.Ordinal);
        settings.CodexFingerprintForwarding ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        settings.CodexFingerprintSeeds ??= new Dictionary<string, string>(StringComparer.Ordinal);
        settings.CodexFingerprintModes[key] = ToFingerprintModeValue(mode);
        // Keep downgrade behavior unsurprising: convergence modes need the same
        // allow-list as pass-through before their identifiers can be rewritten.
        settings.CodexFingerprintForwarding[key] = mode != CodexFingerprintMode.GatewayDefault;
        if ((mode is CodexFingerprintMode.Device or
             CodexFingerprintMode.Session or
             CodexFingerprintMode.Full) &&
            GetFingerprintSeed(settings, key) == null)
        {
            settings.CodexFingerprintSeeds[key] = Guid.NewGuid().ToString("D");
        }
    }

    internal static void Validate()
    {
        if (GetInteractivePoolToggleTarget(AccountRotationPool.None) != AccountRotationPool.Primary ||
            GetInteractivePoolToggleTarget(AccountRotationPool.Primary) != AccountRotationPool.Backup ||
            GetInteractivePoolToggleTarget(AccountRotationPool.Backup) != AccountRotationPool.Primary)
        {
            throw new InvalidOperationException(
                "The account rotation pool toggle must switch participating pools without cycling through None.");
        }

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

        var poolMoveSettings = new AppSettings();
        _ = Normalize(poolMoveSettings, accounts);
        var poolMoveApiKey = QuotaAccountIdentity.CreateKey(api);
        poolMoveSettings.AccountRotationBackupCursorAccountKey = poolMoveApiKey;
        SetPool(poolMoveSettings, accounts, api, AccountRotationPool.Primary);
        if (GetPool(poolMoveSettings, api) != AccountRotationPool.Primary ||
            poolMoveSettings.AccountRotationPrimaryOrder.Count(value =>
                value.Equals(poolMoveApiKey, StringComparison.Ordinal)) != 1 ||
            poolMoveSettings.AccountRotationBackupOrder.Contains(poolMoveApiKey, StringComparer.Ordinal) ||
            poolMoveSettings.AccountRotationBackupCursorAccountKey != null)
        {
            throw new InvalidOperationException(
                "Moving an account from the backup pool to the primary pool did not migrate its order and cursor atomically.");
        }

        var legacyEvidenceSettings = new AppSettings
        {
            AccountRotationQuotaEvidenceVersion = 1,
            AccountRotationResetAtUtc = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
            {
                [QuotaAccountIdentity.CreateKey(a)] = DateTimeOffset.UtcNow.AddHours(1)
            }
        };
        if (!Normalize(legacyEvidenceSettings, accounts) ||
            legacyEvidenceSettings.AccountRotationQuotaEvidenceVersion !=
                CurrentQuotaEvidenceVersion ||
            legacyEvidenceSettings.AccountRotationResetAtUtc.Count != 0)
        {
            throw new InvalidOperationException(
                "Pre-2.2.9 unconfirmed 429 reset markers were not retired exactly once.");
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

        candidates = BuildCandidates(
            settings,
            accounts,
            a,
            unavailable,
            DateTimeOffset.UtcNow,
            _ => true,
            hardUnavailableAccountKeys: unavailable);
        if (candidates.Any(account => account.Name == "b") ||
            candidates.FirstOrDefault()?.Name != "oauth")
        {
            throw new InvalidOperationException(
                "A credential already attempted by one request must remain hard-excluded even after reset grace elapsed.");
        }

        // The request path must be strictly lazy: expose one configured account, and do
        // not make the next account visible until the caller records a failed attempt.
        var lazySettings = new AppSettings
        {
            AccountRotationEnabled = true,
            PatAutoRotationEnabled = true
        };
        Normalize(lazySettings, accounts);
        MarkUsed(lazySettings, a);
        var lazyAttempts = new HashSet<string>(StringComparer.Ordinal);
        var credentialPredicateCalls = new List<string>();
        if (!TrySelectNextCandidate(
                lazySettings,
                accounts,
                a,
                unavailableAccountKeys: new HashSet<string>(StringComparer.Ordinal),
                DateTimeOffset.UtcNow,
                account =>
                {
                    credentialPredicateCalls.Add(account.Name);
                    return true;
                },
                out var lazyFirst,
                out var lazyPool,
                hardUnavailableAccountKeys: lazyAttempts) ||
            lazyFirst?.Name != "b" ||
            lazyPool != AccountRotationPool.Primary ||
            !credentialPredicateCalls.SequenceEqual(["b"]))
        {
            throw new InvalidOperationException(
                "Lazy rotation selection did not expose only the first configured primary account.");
        }

        // The currently serving account, not a stale persisted cursor, anchors a live
        // decision after the user reorders the pool. This mirrors [b, oauth, a] with
        // current=a: the next candidate must wrap to b even if an old cursor says oauth.
        lazySettings.AccountRotationPrimaryOrder =
        [
            QuotaAccountIdentity.CreateKey(b),
            QuotaAccountIdentity.CreateKey(oauth),
            QuotaAccountIdentity.CreateKey(a)
        ];
        lazySettings.AccountRotationPrimaryCursorAccountKey =
            QuotaAccountIdentity.CreateKey(oauth);
        if (!TrySelectNextCandidate(
                lazySettings,
                accounts,
                a,
                unavailableAccountKeys: new HashSet<string>(StringComparer.Ordinal),
                DateTimeOffset.UtcNow,
                _ => true,
                out var reorderedFirst,
                out var reorderedPool) ||
            reorderedFirst?.Name != "b" ||
            reorderedPool != AccountRotationPool.Primary)
        {
            throw new InvalidOperationException(
                "A stale cursor overrode the latest pool order and current-account anchor.");
        }
        Normalize(lazySettings, accounts);
        lazySettings.AccountRotationPrimaryOrder =
        [
            QuotaAccountIdentity.CreateKey(a),
            QuotaAccountIdentity.CreateKey(b),
            QuotaAccountIdentity.CreateKey(oauth)
        ];
        MarkUsed(lazySettings, a);
        lazyAttempts.Add(QuotaAccountIdentity.CreateKey(lazyFirst!));
        credentialPredicateCalls.Clear();
        if (!TrySelectNextCandidate(
                lazySettings,
                accounts,
                a,
                unavailableAccountKeys: new HashSet<string>(StringComparer.Ordinal),
                DateTimeOffset.UtcNow,
                account =>
                {
                    credentialPredicateCalls.Add(account.Name);
                    return true;
                },
                out var lazySecond,
                out var lazySecondPool,
                hardUnavailableAccountKeys: lazyAttempts) ||
            lazySecond?.Name != "oauth" ||
            lazySecondPool != AccountRotationPool.Primary ||
            !credentialPredicateCalls.SequenceEqual(["oauth"]))
        {
            throw new InvalidOperationException(
                "Lazy rotation selection did not advance only after the first attempt failed.");
        }

        // Once every primary member is hard-excluded, and only then, the selector may
        // expose the first backup member.  This guards the primary-before-backup rule.
        lazyAttempts.Add(QuotaAccountIdentity.CreateKey(lazySecond!));
        lazyAttempts.Add(QuotaAccountIdentity.CreateKey(oauth));
        if (!TrySelectNextCandidate(
                lazySettings,
                accounts,
                a,
                unavailableAccountKeys: new HashSet<string>(StringComparer.Ordinal),
                DateTimeOffset.UtcNow,
                _ => true,
                out var lazyBackup,
                out var lazyBackupPool,
                hardUnavailableAccountKeys: lazyAttempts) ||
            lazyBackup?.Name != "api" ||
            lazyBackupPool != AccountRotationPool.Backup)
        {
            throw new InvalidOperationException(
                "Lazy rotation selection entered the backup pool before all primary attempts failed.");
        }

        // The quota dashboard uses the same live ring order as the request path.  A
        // current third account must be shown as 3,4,5,6,7,8,1,2 rather than being
        // promoted alone while the remaining cards stay in file order.
        var displayAccounts = Enumerable.Range(1, 8)
            .Select(index => new AccountRecord
            {
                Name = index.ToString(),
                CodexHome = Path.Combine(root, "display-" + index)
            })
            .ToArray();
        var displaySettings = new AppSettings
        {
            AccountRotationEnabled = true,
            PatAutoRotationEnabled = true
        };
        Normalize(displaySettings, displayAccounts);
        displaySettings.AccountRotationPrimaryOrder = displayAccounts
            .Select(QuotaAccountIdentity.CreateKey)
            .ToList();
        var displayOrder = GetQuotaDisplayOrder(
                displaySettings,
                displayAccounts,
                QuotaAccountIdentity.CreateKey(displayAccounts[2]))
            .Select(account => account.Name)
            .ToArray();
        if (!displayOrder.SequenceEqual(["3", "4", "5", "6", "7", "8", "1", "2"]))
        {
            throw new InvalidOperationException(
                "Quota display order did not wrap around the currently serving account.");
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

        SetFingerprintMode(settings, api, CodexFingerprintMode.Full);
        var apiKey = QuotaAccountIdentity.CreateKey(api);
        if (GetFingerprintMode(settings, api) != CodexFingerprintMode.Full ||
            GetFingerprintSeed(settings, apiKey) == null ||
            !settings.CodexFingerprintForwarding.GetValueOrDefault(apiKey))
        {
            throw new InvalidOperationException(
                "A trusted compatible API could not opt into full fingerprint convergence.");
        }
        SetFingerprintMode(settings, api, CodexFingerprintMode.Passthrough);
        if (GetFingerprintMode(settings, api) != CodexFingerprintMode.Passthrough ||
            settings.CodexFingerprintModes[apiKey] != FingerprintPassthroughValue)
        {
            throw new InvalidOperationException(
                "The sub2api-compatible off fingerprint mode did not round-trip.");
        }

        var legacyFingerprintSettings = new AppSettings();
        legacyFingerprintSettings.CodexFingerprintForwarding[apiKey] = true;
        if (GetFingerprintMode(legacyFingerprintSettings, apiKey) !=
            CodexFingerprintMode.Passthrough)
        {
            throw new InvalidOperationException(
                "Legacy fingerprint forwarding did not migrate to the off/pass-through mode.");
        }

        var explicitNullFingerprintSettings = new AppSettings
        {
            CodexFingerprintForwarding = null!,
            CodexFingerprintModes = null!,
            CodexFingerprintSeeds = null!
        };
        if (GetFingerprintMode(explicitNullFingerprintSettings, apiKey) !=
                CodexFingerprintMode.GatewayDefault ||
            GetFingerprintSeed(explicitNullFingerprintSettings, apiKey) != null)
        {
            throw new InvalidOperationException(
                "Explicit null fingerprint dictionaries were not handled as safe defaults.");
        }
        SetFingerprintMode(
            explicitNullFingerprintSettings,
            api,
            CodexFingerprintMode.Session);
        if (GetFingerprintMode(explicitNullFingerprintSettings, apiKey) !=
                CodexFingerprintMode.Session ||
            GetFingerprintSeed(explicitNullFingerprintSettings, apiKey) == null)
        {
            throw new InvalidOperationException(
                "Fingerprint configuration did not recover from explicit null dictionaries.");
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
        Func<AccountRecord, bool> canSelectAccount,
        ISet<string> hardUnavailableAccountKeys)
    {
        var ordered = GetOrderedAccounts(settings, accounts, pool).ToList();
        if (ordered.Count == 0)
        {
            return [];
        }
        var cursor = pool == AccountRotationPool.Primary
            ? settings.AccountRotationPrimaryCursorAccountKey
            : settings.AccountRotationBackupCursorAccountKey;
        // The account that is actually serving the request is the authoritative ring
        // position.  A persisted cursor is only a restart/fallback anchor when that
        // account is outside this pool.  Giving an old cursor priority could skip newly
        // reordered entries (for example, current=lkcau while 158/hjd were moved before
        // it), which made the UI order and the next real request disagree.
        var anchor = ordered.FindIndex(account =>
            QuotaAccountIdentity.CreateKey(account).Equals(currentKey, StringComparison.Ordinal));
        if (anchor < 0)
        {
            anchor = ordered.FindIndex(account =>
                QuotaAccountIdentity.CreateKey(account).Equals(cursor, StringComparison.Ordinal));
        }

        return Enumerable.Range(1, ordered.Count)
            .Select(offset => ordered[(anchor + offset + ordered.Count) % ordered.Count])
            .Where(account =>
            {
                var key = QuotaAccountIdentity.CreateKey(account);
                return !key.Equals(currentKey, StringComparison.Ordinal) &&
                       !hardUnavailableAccountKeys.Contains(key) &&
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

    private static AccountRecord? FindFirstEligible(
        AppSettings settings,
        IReadOnlyList<AccountRecord> accounts,
        AccountRotationPool pool,
        string currentKey,
        IReadOnlySet<string> unavailableAccountKeys,
        DateTimeOffset nowUtc,
        Func<AccountRecord, bool> hasUsableCredential,
        Func<AccountRecord, bool> canSelectAccount,
        ISet<string> hardUnavailableAccountKeys)
    {
        var ordered = GetOrderedAccounts(settings, accounts, pool);
        if (ordered.Count == 0)
        {
            return null;
        }

        var cursor = pool == AccountRotationPool.Primary
            ? settings.AccountRotationPrimaryCursorAccountKey
            : settings.AccountRotationBackupCursorAccountKey;
        var anchor = -1;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (QuotaAccountIdentity.CreateKey(ordered[index])
                .Equals(currentKey, StringComparison.Ordinal))
            {
                anchor = index;
                break;
            }
        }
        if (anchor < 0 && !string.IsNullOrWhiteSpace(cursor))
        {
            for (var index = 0; index < ordered.Count; index++)
            {
                if (QuotaAccountIdentity.CreateKey(ordered[index])
                    .Equals(cursor, StringComparison.Ordinal))
                {
                    anchor = index;
                    break;
                }
            }
        }

        for (var offset = 1; offset <= ordered.Count; offset++)
        {
            var account = ordered[(anchor + offset + ordered.Count) % ordered.Count];
            var key = QuotaAccountIdentity.CreateKey(account);
            if (key.Equals(currentKey, StringComparison.Ordinal) ||
                hardUnavailableAccountKeys.Contains(key) ||
                IsStillUnavailable(settings, key, unavailableAccountKeys, nowUtc) ||
                !IsResetGraceElapsed(settings, key, nowUtc) ||
                !hasUsableCredential(account) ||
                !canSelectAccount(account))
            {
                continue;
            }

            return account;
        }

        return null;
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

    private static string NormalizeFingerprintModeValue(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            FingerprintPassthroughValue => FingerprintPassthroughValue,
            FingerprintDeviceValue => FingerprintDeviceValue,
            FingerprintSessionValue => FingerprintSessionValue,
            FingerprintFullValue => FingerprintFullValue,
            _ => FingerprintGatewayDefaultValue
        };

    private static string NormalizeFingerprintSeed(string? value)
    {
        return Guid.TryParseExact(value?.Trim(), "D", out var parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : string.Empty;
    }

    private static CodexFingerprintMode ParseFingerprintMode(string? value) =>
        NormalizeFingerprintModeValue(value) switch
        {
            FingerprintPassthroughValue => CodexFingerprintMode.Passthrough,
            FingerprintDeviceValue => CodexFingerprintMode.Device,
            FingerprintSessionValue => CodexFingerprintMode.Session,
            FingerprintFullValue => CodexFingerprintMode.Full,
            _ => CodexFingerprintMode.GatewayDefault
        };

    private static string ToFingerprintModeValue(CodexFingerprintMode mode) => mode switch
    {
        CodexFingerprintMode.Passthrough => FingerprintPassthroughValue,
        CodexFingerprintMode.Device => FingerprintDeviceValue,
        CodexFingerprintMode.Session => FingerprintSessionValue,
        CodexFingerprintMode.Full => FingerprintFullValue,
        _ => FingerprintGatewayDefaultValue
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
