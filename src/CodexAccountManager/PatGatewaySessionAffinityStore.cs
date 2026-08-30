using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountManager;

internal enum PatGatewaySessionAffinityKeyKind
{
    Session,
    Thread,
    Conversation,
    PromptCache,
    Response
}

internal readonly record struct PatGatewaySessionAffinityKey(
    PatGatewaySessionAffinityKeyKind Kind,
    string Value)
{
    internal static PatGatewaySessionAffinityKey Session(string value) =>
        new(PatGatewaySessionAffinityKeyKind.Session, value);

    internal static PatGatewaySessionAffinityKey Thread(string value) =>
        new(PatGatewaySessionAffinityKeyKind.Thread, value);

    internal static PatGatewaySessionAffinityKey Conversation(string value) =>
        new(PatGatewaySessionAffinityKeyKind.Conversation, value);

    internal static PatGatewaySessionAffinityKey PromptCache(string value) =>
        new(PatGatewaySessionAffinityKeyKind.PromptCache, value);

    internal static PatGatewaySessionAffinityKey Response(string value) =>
        new(PatGatewaySessionAffinityKeyKind.Response, value);
}

internal readonly record struct PatGatewaySessionAffinityMutationResult(
    bool Applied,
    bool Persisted);

/// <summary>
/// A compare-and-swap lease over the affinity aliases observed on one request.
/// The lease contains only keyed digests; raw client identifiers never leave the
/// call that creates it and are never persisted.
/// </summary>
internal sealed class PatGatewaySessionAffinityLease
{
    internal PatGatewaySessionAffinityLease(
        Guid leaseId,
        string accountKey,
        long version,
        PatGatewaySessionAffinityKeyKind? matchedKind,
        bool matchedConfirmed,
        bool claimed,
        IReadOnlyList<Slot> slots)
    {
        LeaseId = leaseId;
        AccountKey = accountKey;
        Version = version;
        MatchedKind = matchedKind;
        MatchedConfirmed = matchedConfirmed;
        Claimed = claimed;
        Slots = slots;
    }

    internal Guid LeaseId { get; }
    internal string AccountKey { get; }
    internal long Version { get; }
    internal PatGatewaySessionAffinityKeyKind? MatchedKind { get; }
    internal bool MatchedConfirmed { get; }
    internal bool Claimed { get; }
    internal bool IsTracked => Slots.Count > 0;
    internal IReadOnlyList<Slot> Slots { get; }

    internal sealed record Slot(
        PatGatewaySessionAffinityKeyKind Kind,
        string StorageKey,
        long ExpectedVersion,
        bool CanMoveUnowned);
}

/// <summary>
/// Maintains request-session and response-id affinity for the local PAT gateway.
/// Confirmed bindings use a one-hour sliding TTL and are atomically persisted as
/// HMAC digests plus stable account hashes. Provisional claims and moves remain
/// memory-only until an upstream attempt succeeds.
/// </summary>
internal sealed class PatGatewaySessionAffinityStore
{
    internal const string FileName = "pat-gateway-session-affinity-v1.json";
    internal static readonly TimeSpan BindingLifetime = TimeSpan.FromHours(1);
    internal const int DefaultMaximumEntries = 65_536;

    private const int SchemaVersion = 1;
    private const int MaximumRequestAliases = 16;
    private const int MaximumRawIdentifierCharacters = 512;
    private const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan ProvisionalLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumLoadedFutureLifetime = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly object _persistenceGate = new();
    private readonly Dictionary<string, BindingEntry> _entries = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly byte[] _hmacKey;
    private readonly string _keyId;
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumEntries;
    private readonly TimeSpan _bindingLifetime;
    private long _nextVersion = 1;

    internal PatGatewaySessionAffinityStore(
        string managerRoot,
        string hmacSecret,
        TimeProvider? timeProvider = null,
        int maximumEntries = DefaultMaximumEntries,
        TimeSpan? bindingLifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managerRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(hmacSecret);
        if (maximumEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }
        var configuredBindingLifetime = bindingLifetime ?? BindingLifetime;
        if (configuredBindingLifetime < TimeSpan.FromMinutes(1) ||
            configuredBindingLifetime > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(bindingLifetime));
        }

        var cacheRoot = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(managerRoot),
            ".cache");
        _path = System.IO.Path.Combine(cacheRoot, FileName);
        _hmacKey = Encoding.UTF8.GetBytes(hmacSecret);
        if (_hmacKey.Length < 16)
        {
            throw new ArgumentException(
                "The affinity HMAC secret must contain at least 16 UTF-8 bytes.",
                nameof(hmacSecret));
        }
        _keyId = Convert.ToHexString(HMACSHA256.HashData(
            _hmacKey,
            Encoding.UTF8.GetBytes("cam-affinity-key-id-v1")))[..16];
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumEntries = maximumEntries;
        _bindingLifetime = configuredBindingLifetime;
        Load();
    }

    internal string Path => _path;

    /// <summary>
    /// Resolves the first live alias in caller-supplied priority order. If none is
    /// known, atomically claims the aliases for <paramref name="defaultAccountKey"/>.
    /// Missing aliases accompanying a hit are provisionally attached to the same
    /// account and become durable only after <see cref="Confirm"/>.
    /// </summary>
    internal PatGatewaySessionAffinityLease ResolveOrClaim(
        IEnumerable<PatGatewaySessionAffinityKey> keys,
        string defaultAccountKey)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var accountKey = NormalizeRequiredAccountKey(defaultAccountKey);
        var digests = HashKeys(keys);
        var now = UtcNow();
        var leaseId = Guid.NewGuid();
        if (digests.Count == 0)
        {
            return new PatGatewaySessionAffinityLease(
                leaseId,
                accountKey,
                version: 0,
                matchedKind: null,
                matchedConfirmed: false,
                claimed: false,
                slots: []);
        }

        lock (_gate)
        {
            PruneExpiredLocked(now);
            BindingEntry? selected = null;
            HashedKey? selectedKey = null;
            foreach (var key in digests)
            {
                if (_entries.TryGetValue(key.StorageKey, out var candidate))
                {
                    selected = candidate;
                    selectedKey = key;
                    break;
                }
            }

            var claimed = selected == null;
            var selectedAccountKey = selected?.AccountKey ?? accountKey;
            var selectedVersion = selected?.Version ?? NextVersionLocked();
            var slots = new List<PatGatewaySessionAffinityLease.Slot>(digests.Count);
            foreach (var key in digests)
            {
                if (_entries.TryGetValue(key.StorageKey, out var existing))
                {
                    if (existing.AccountKey.Equals(selectedAccountKey, StringComparison.Ordinal))
                    {
                        slots.Add(new PatGatewaySessionAffinityLease.Slot(
                            key.Kind,
                            key.StorageKey,
                            existing.Version,
                            CanMoveUnowned: existing.Confirmed && existing.ProvisionalOwner == null));
                    }
                    continue;
                }

                if (!EnsureCapacityLocked(1, now, digests.Select(value => value.StorageKey)))
                {
                    continue;
                }
                var provisional = new BindingEntry
                {
                    Kind = key.Kind,
                    Digest = key.Digest,
                    AccountKey = selectedAccountKey,
                    Version = selectedVersion,
                    Confirmed = false,
                    ProvisionalOwner = leaseId,
                    ExpiresAtUtc = now + ProvisionalLifetime,
                    UpdatedAtUtc = now
                };
                _entries[key.StorageKey] = provisional;
                slots.Add(new PatGatewaySessionAffinityLease.Slot(
                    key.Kind,
                    key.StorageKey,
                    selectedVersion,
                    CanMoveUnowned: false));
            }

            return new PatGatewaySessionAffinityLease(
                leaseId,
                selectedAccountKey,
                selectedVersion,
                selectedKey?.Kind,
                selected?.Confirmed == true && !selected.Ambiguous,
                claimed,
                slots);
        }
    }

    /// <summary>
    /// Confirms every alias that still matches this lease. A stale concurrent lease
    /// cannot overwrite a newer move because both account hash and version must match.
    /// A successful request may confirm another request's identical provisional claim;
    /// this intentionally prevents its later failure cleanup from removing a proven route.
    /// </summary>
    internal PatGatewaySessionAffinityMutationResult Confirm(
        PatGatewaySessionAffinityLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return ConfirmAndBindResponses(lease, [], lease.AccountKey);
    }

    /// <summary>
    /// Provisionally moves non-response aliases to another account. The returned
    /// lease must be confirmed after a successful retry or released after the retry
    /// chain fails. Response-id ownership is deliberately immutable here.
    /// </summary>
    internal PatGatewaySessionAffinityLease? Move(
        PatGatewaySessionAffinityLease lease,
        string targetAccountKey)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var target = NormalizeRequiredAccountKey(targetAccountKey);
        if (target.Equals(lease.AccountKey, StringComparison.Ordinal))
        {
            return lease;
        }

        var now = UtcNow();
        lock (_gate)
        {
            PruneExpiredLocked(now);
            var movedSlots = new List<PatGatewaySessionAffinityLease.Slot>(lease.Slots.Count);
            long targetVersion = 0;
            foreach (var slot in lease.Slots)
            {
                if (slot.Kind == PatGatewaySessionAffinityKeyKind.Response ||
                    !_entries.TryGetValue(slot.StorageKey, out var current) ||
                    current.Version != slot.ExpectedVersion ||
                    !current.AccountKey.Equals(lease.AccountKey, StringComparison.Ordinal))
                {
                    continue;
                }

                var ownsProvisional = current.ProvisionalOwner == lease.LeaseId;
                if (!ownsProvisional &&
                    !(slot.CanMoveUnowned && current.ProvisionalOwner == null && current.Confirmed))
                {
                    continue;
                }

                if (targetVersion == 0)
                {
                    targetVersion = NextVersionLocked();
                }
                if (current.Confirmed)
                {
                    current.DurableFallback = current.ToDurableSnapshot();
                }
                current.AccountKey = target;
                current.Version = targetVersion;
                current.Confirmed = false;
                current.ProvisionalOwner = lease.LeaseId;
                current.ExpiresAtUtc = now + ProvisionalLifetime;
                current.UpdatedAtUtc = now;
                movedSlots.Add(new PatGatewaySessionAffinityLease.Slot(
                    slot.Kind,
                    slot.StorageKey,
                    targetVersion,
                    CanMoveUnowned: false));
            }

            return movedSlots.Count == 0
                ? null
                : new PatGatewaySessionAffinityLease(
                    lease.LeaseId,
                    target,
                    targetVersion,
                    lease.MatchedKind,
                    lease.MatchedConfirmed,
                    claimed: false,
                    movedSlots);
        }
    }

    /// <summary>
    /// Binds one successful Responses API id to the actual account that produced it.
    /// A conflicting existing owner is retained rather than silently reassigned.
    /// </summary>
    internal PatGatewaySessionAffinityMutationResult BindResponse(
        string responseId,
        string accountKey)
    {
        return ConfirmAndBindResponses(null, [responseId], accountKey);
    }

    /// <summary>
    /// Atomically confirms a successful request lease and binds every observed
    /// Responses API id to the account that actually produced the response. Raw
    /// response ids are reduced to HMAC digests before the store lock is acquired.
    /// Live response ownership conflicts are retained as an ambiguous tombstone;
    /// neither account may subsequently route a continuation through that id.
    /// Non-conflicting mutations are committed with at most one persistence attempt.
    /// </summary>
    internal PatGatewaySessionAffinityMutationResult ConfirmAndBindResponses(
        PatGatewaySessionAffinityLease? lease,
        IEnumerable<string> responseIds,
        string accountKey)
    {
        ArgumentNullException.ThrowIfNull(responseIds);
        var target = NormalizeRequiredAccountKey(accountKey);
        var responseKeys = HashKeys(responseIds.Select(responseId =>
            PatGatewaySessionAffinityKey.Response(responseId)));
        var confirmLease = lease != null &&
                           lease.AccountKey.Equals(target, StringComparison.Ordinal);
        var protectedStorageKeys = responseKeys
            .Select(key => key.StorageKey)
            .Concat(confirmLease
                ? lease!.Slots.Select(slot => slot.StorageKey)
                : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var now = UtcNow();
        var applied = false;
        lock (_gate)
        {
            PruneExpiredLocked(now);
            if (confirmLease)
            {
                foreach (var slot in lease!.Slots)
                {
                    if (!_entries.TryGetValue(slot.StorageKey, out var current) ||
                        current.Version != slot.ExpectedVersion ||
                        !current.AccountKey.Equals(target, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (current.Ambiguous)
                    {
                        // A poisoned response id must never be made usable again by
                        // a later request that happens to use the old owner.
                        continue;
                    }
                    current.Confirmed = true;
                    current.ProvisionalOwner = null;
                    current.DurableFallback = null;
                    current.ExpiresAtUtc = now + _bindingLifetime;
                    current.UpdatedAtUtc = now;
                    applied = true;
                }
            }

            foreach (var key in responseKeys)
            {
                if (_entries.TryGetValue(key.StorageKey, out var existing))
                {
                    if (existing.Ambiguous)
                    {
                        // Keep the tombstone alive, but never choose either account as
                        // the owner of a response id observed from multiple accounts.
                        existing.ExpiresAtUtc = now + _bindingLifetime;
                        existing.UpdatedAtUtc = now;
                        applied = true;
                        continue;
                    }
                    if (!existing.AccountKey.Equals(target, StringComparison.Ordinal))
                    {
                        existing.Ambiguous = true;
                        existing.Confirmed = true;
                        existing.ProvisionalOwner = null;
                        existing.DurableFallback = null;
                        existing.Version = NextVersionLocked();
                        existing.ExpiresAtUtc = now + _bindingLifetime;
                        existing.UpdatedAtUtc = now;
                        applied = true;
                        continue;
                    }
                    existing.Confirmed = true;
                    existing.ProvisionalOwner = null;
                    existing.DurableFallback = null;
                    existing.ExpiresAtUtc = now + _bindingLifetime;
                    existing.UpdatedAtUtc = now;
                    applied = true;
                }
                else if (EnsureCapacityLocked(1, now, protectedStorageKeys))
                {
                    _entries[key.StorageKey] = new BindingEntry
                    {
                        Kind = PatGatewaySessionAffinityKeyKind.Response,
                        Digest = key.Digest,
                        AccountKey = target,
                        Version = NextVersionLocked(),
                        Confirmed = true,
                        Ambiguous = false,
                        ExpiresAtUtc = now + _bindingLifetime,
                        UpdatedAtUtc = now
                    };
                    applied = true;
                }
            }
        }
        return PersistMutation(applied);
    }

    /// <summary>
    /// Releases only provisional entries still owned by this lease. A provisional
    /// move restores its last durable binding; a first-use claim is removed. Entries
    /// confirmed or superseded by concurrent requests are left untouched.
    /// </summary>
    internal PatGatewaySessionAffinityMutationResult ReleaseFailed(
        PatGatewaySessionAffinityLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var now = UtcNow();
        var applied = false;
        lock (_gate)
        {
            foreach (var slot in lease.Slots)
            {
                if (!_entries.TryGetValue(slot.StorageKey, out var current) ||
                    current.Version != slot.ExpectedVersion ||
                    current.ProvisionalOwner != lease.LeaseId ||
                    !current.AccountKey.Equals(lease.AccountKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (current.DurableFallback is { } fallback &&
                    fallback.ExpiresAtUtc > now)
                {
                    _entries[slot.StorageKey] = BindingEntry.FromSnapshot(fallback);
                }
                else
                {
                    _entries.Remove(slot.StorageKey);
                }
                applied = true;
            }
            PruneExpiredLocked(now);
        }

        // Provisional state is never serialized. Its durable fallback was already on
        // disk, so cleanup is durably complete without another filesystem write.
        return new PatGatewaySessionAffinityMutationResult(applied, true);
    }

    /// <summary>
    /// Removes ordinary session aliases owned by one account when its pool participation
    /// changes. Strict response-id bindings are deliberately retained: an existing
    /// previous_response_id cannot be made safe by silently moving it to another account.
    /// Provisional moves are rolled back or detached without allowing a later failed lease
    /// to resurrect the invalidated durable account.
    /// </summary>
    internal PatGatewaySessionAffinityMutationResult InvalidateOrdinaryBindingsForAccount(
        string accountKey)
    {
        var target = NormalizeRequiredAccountKey(accountKey);
        var applied = false;
        lock (_gate)
        {
            foreach (var pair in _entries.ToArray())
            {
                var current = pair.Value;
                if (current.Kind == PatGatewaySessionAffinityKeyKind.Response)
                {
                    continue;
                }

                if (current.AccountKey.Equals(target, StringComparison.Ordinal))
                {
                    if (!current.Confirmed &&
                        current.DurableFallback is { } fallback &&
                        !fallback.AccountKey.Equals(target, StringComparison.Ordinal) &&
                        fallback.ExpiresAtUtc > UtcNow())
                    {
                        _entries[pair.Key] = BindingEntry.FromSnapshot(fallback);
                    }
                    else
                    {
                        _entries.Remove(pair.Key);
                    }
                    applied = true;
                    continue;
                }

                if (!current.Confirmed &&
                    current.DurableFallback is { } staleFallback &&
                    staleFallback.AccountKey.Equals(target, StringComparison.Ordinal))
                {
                    current.DurableFallback = null;
                    applied = true;
                }
            }
            PruneExpiredLocked(UtcNow());
        }
        return PersistMutation(applied);
    }

    private PatGatewaySessionAffinityMutationResult PersistMutation(bool applied)
    {
        return new PatGatewaySessionAffinityMutationResult(
            applied,
            !applied || TryPersistLatest());
    }

    private IReadOnlyList<HashedKey> HashKeys(
        IEnumerable<PatGatewaySessionAffinityKey> keys)
    {
        var result = new List<HashedKey>(MaximumRequestAliases);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (result.Count >= MaximumRequestAliases)
            {
                break;
            }
            if (!TryHashKey(key, out var hashed) || !seen.Add(hashed.StorageKey))
            {
                continue;
            }
            result.Add(hashed);
        }
        return result;
    }

    private bool TryHashKey(
        PatGatewaySessionAffinityKey key,
        out HashedKey hashed)
    {
        hashed = default;
        if (!Enum.IsDefined(key.Kind) ||
            !TryNormalizeRawIdentifier(key.Kind, key.Value, out var normalized))
        {
            return false;
        }

        var payload = Encoding.UTF8.GetBytes(
            "cam-affinity-v1\0" + KindName(key.Kind) + "\0" + normalized);
        var digest = Convert.ToHexString(HMACSHA256.HashData(_hmacKey, payload));
        hashed = new HashedKey(
            key.Kind,
            digest,
            KindName(key.Kind) + ":" + digest);
        return true;
    }

    private static bool TryNormalizeRawIdentifier(
        PatGatewaySessionAffinityKeyKind kind,
        string? value,
        out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > MaximumRawIdentifierCharacters ||
            normalized.Any(char.IsControl))
        {
            return false;
        }
        return kind != PatGatewaySessionAffinityKeyKind.Response ||
               IsValidResponseId(normalized);
    }

    private static bool IsValidResponseId(string value)
    {
        if (value.Length is < 6 or > 256 ||
            !value.StartsWith("resp_", StringComparison.Ordinal))
        {
            return false;
        }
        return value.AsSpan(5).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-".AsSpan()) < 0;
    }

    private static string NormalizeRequiredAccountKey(string value)
    {
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(value, out var normalized))
        {
            throw new ArgumentException("The affinity account hash is invalid.", nameof(value));
        }
        return normalized;
    }

    private bool EnsureCapacityLocked(
        int additionalEntries,
        DateTimeOffset now,
        IEnumerable<string> protectedStorageKeys)
    {
        PruneExpiredLocked(now);
        if (_entries.Count + additionalEntries <= _maximumEntries)
        {
            return true;
        }

        var protectedKeys = new HashSet<string>(protectedStorageKeys, StringComparer.Ordinal);
        var removable = _entries
            .Where(pair =>
                pair.Value.ProvisionalOwner == null &&
                !protectedKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Value.Kind == PatGatewaySessionAffinityKeyKind.Response ? 0 : 1)
            .ThenBy(pair => pair.Value.UpdatedAtUtc)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in removable)
        {
            _entries.Remove(key);
            if (_entries.Count + additionalEntries <= _maximumEntries)
            {
                return true;
            }
        }
        return _entries.Count + additionalEntries <= _maximumEntries;
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        foreach (var pair in _entries.ToArray())
        {
            var current = pair.Value;
            if (current.ExpiresAtUtc > now)
            {
                continue;
            }
            if (!current.Confirmed &&
                current.DurableFallback is { } fallback &&
                fallback.ExpiresAtUtc > now)
            {
                _entries[pair.Key] = BindingEntry.FromSnapshot(fallback);
            }
            else
            {
                _entries.Remove(pair.Key);
            }
        }
    }

    private long NextVersionLocked()
    {
        if (_nextVersion == long.MaxValue)
        {
            var highest = _entries.Count == 0
                ? 0
                : _entries.Values.Max(entry => entry.Version);
            if (highest == long.MaxValue)
            {
                // Version wrap is practically unreachable. Re-numbering while holding
                // the store lock preserves CAS ordering without accepting stale leases.
                var version = 1L;
                foreach (var entry in _entries.Values.OrderBy(entry => entry.UpdatedAtUtc))
                {
                    entry.Version = version++;
                }
                _nextVersion = version;
            }
            else
            {
                _nextVersion = highest + 1;
            }
        }
        return _nextVersion++;
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumDocumentBytes)
            {
                return;
            }
            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var document = JsonSerializer.Deserialize<AffinityDocument>(stream, JsonOptions);
            if (document == null ||
                document.SchemaVersion != SchemaVersion ||
                !string.Equals(document.KeyId, _keyId, StringComparison.Ordinal))
            {
                return;
            }

            var now = UtcNow();
            lock (_gate)
            {
                foreach (var persisted in document.Entries ?? [])
                {
                    if (_entries.Count >= _maximumEntries ||
                        !TryParseKind(persisted.Kind, out var kind) ||
                        !IsDigest(persisted.Digest) ||
                        !PatGatewayRotationStore.TryNormalizeAccountKey(
                            persisted.AccountKey,
                            out var accountKey) ||
                        persisted.Version < 1 ||
                        persisted.ExpiresAtUtc <= now ||
                        persisted.ExpiresAtUtc > now + MaximumLoadedFutureLifetime)
                    {
                        continue;
                    }
                    var storageKey = KindName(kind) + ":" + persisted.Digest;
                    var updatedAt = persisted.UpdatedAtUtc == default ||
                                    persisted.UpdatedAtUtc > now + TimeSpan.FromMinutes(5)
                        ? now
                        : persisted.UpdatedAtUtc.ToUniversalTime();
                    var entry = new BindingEntry
                    {
                        Kind = kind,
                        Digest = persisted.Digest!,
                        AccountKey = accountKey,
                        Version = persisted.Version,
                        Confirmed = true,
                        Ambiguous = kind == PatGatewaySessionAffinityKeyKind.Response &&
                                    persisted.Ambiguous,
                        ExpiresAtUtc = persisted.ExpiresAtUtc.ToUniversalTime(),
                        UpdatedAtUtc = updatedAt
                    };
                    if (!_entries.TryGetValue(storageKey, out var existing) ||
                        entry.UpdatedAtUtc > existing.UpdatedAtUtc)
                    {
                        _entries[storageKey] = entry;
                    }
                    _nextVersion = entry.Version == long.MaxValue
                        ? long.MaxValue
                        : Math.Max(_nextVersion, entry.Version + 1);
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException or OverflowException)
        {
            // Affinity is an optimization. A damaged cache must fail open without
            // changing credential routing or making the gateway unavailable.
        }
    }

    private bool TryPersistLatest()
    {
        lock (_persistenceGate)
        {
            AffinityDocument document;
            lock (_gate)
            {
                var now = UtcNow();
                PruneExpiredLocked(now);
                var persistedEntries = new List<PersistedEntry>(_entries.Count);
                foreach (var entry in _entries.Values)
                {
                    var snapshot = entry.Confirmed
                        ? entry.ToDurableSnapshot()
                        : entry.DurableFallback;
                    if (snapshot == null || snapshot.ExpiresAtUtc <= now)
                    {
                        continue;
                    }
                    persistedEntries.Add(new PersistedEntry
                    {
                        Kind = KindName(snapshot.Kind),
                        Digest = snapshot.Digest,
                        AccountKey = snapshot.AccountKey,
                        Version = snapshot.Version,
                        ExpiresAtUtc = snapshot.ExpiresAtUtc,
                        UpdatedAtUtc = snapshot.UpdatedAtUtc,
                        Ambiguous = snapshot.Ambiguous
                    });
                }
                document = new AffinityDocument
                {
                    SchemaVersion = SchemaVersion,
                    KeyId = _keyId,
                    Entries = persistedEntries
                        .OrderBy(entry => entry.ExpiresAtUtc)
                        .ToList()
                };
            }

            var directory = System.IO.Path.GetDirectoryName(_path)!;
            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 16 * 1024,
                           FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, document, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
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
                    // A same-directory temporary contains only digests and hashes.
                }
            }
        }
    }

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string KindName(PatGatewaySessionAffinityKeyKind kind) => kind switch
    {
        PatGatewaySessionAffinityKeyKind.Session => "session",
        PatGatewaySessionAffinityKeyKind.Thread => "thread",
        PatGatewaySessionAffinityKeyKind.Conversation => "conversation",
        PatGatewaySessionAffinityKeyKind.PromptCache => "prompt-cache",
        PatGatewaySessionAffinityKeyKind.Response => "response",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static bool TryParseKind(
        string? value,
        out PatGatewaySessionAffinityKeyKind kind)
    {
        kind = value switch
        {
            "session" => PatGatewaySessionAffinityKeyKind.Session,
            "thread" => PatGatewaySessionAffinityKeyKind.Thread,
            "conversation" => PatGatewaySessionAffinityKeyKind.Conversation,
            "prompt-cache" => PatGatewaySessionAffinityKeyKind.PromptCache,
            "response" => PatGatewaySessionAffinityKeyKind.Response,
            _ => (PatGatewaySessionAffinityKeyKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    internal static void Validate()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pat-gateway-affinity-test-" + Guid.NewGuid().ToString("N"));
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var secret = "test-only-affinity-secret-32-bytes-minimum";
        var accountA = new string('A', 64);
        var accountB = new string('B', 64);
        var accountC = new string('C', 64);
        const string sessionId = "session-private-fixture";
        const string threadId = "thread-private-fixture";
        const string responseId = "resp_private_fixture_1";
        const string batchSessionId = "session-batch-private-fixture";
        const string batchResponseId1 = "resp_batch_private_fixture_1";
        const string batchResponseId2 = "resp_batch_private_fixture_2";
        try
        {
            var store = new PatGatewaySessionAffinityStore(root, secret, clock, maximumEntries: 32);
            var first = store.ResolveOrClaim(
                [
                    PatGatewaySessionAffinityKey.Session(sessionId),
                    PatGatewaySessionAffinityKey.Thread(threadId)
                ],
                accountA);
            var concurrent = store.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Session(sessionId)],
                accountB);
            if (!first.Claimed ||
                first.AccountKey != accountA ||
                concurrent.AccountKey != accountA ||
                store.Confirm(concurrent) is not { Applied: true, Persisted: true })
            {
                throw new InvalidOperationException(
                    "Concurrent first-use affinity claims did not converge on one account.");
            }

            var persisted = File.ReadAllText(store.Path);
            if (persisted.Contains(sessionId, StringComparison.Ordinal) ||
                persisted.Contains(threadId, StringComparison.Ordinal) ||
                persisted.Contains(responseId, StringComparison.Ordinal) ||
                persisted.Contains(secret, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Affinity persistence exposed a raw identifier or HMAC secret.");
            }

            var reloaded = new PatGatewaySessionAffinityStore(root, secret, clock, maximumEntries: 32);
            var hit = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Session(sessionId)],
                accountB);
            if (hit.AccountKey != accountA ||
                hit.MatchedKind != PatGatewaySessionAffinityKeyKind.Session)
            {
                throw new InvalidOperationException("A durable affinity binding was not reloaded.");
            }

            clock.Advance(TimeSpan.FromMinutes(50));
            if (!reloaded.Confirm(hit).Persisted)
            {
                throw new InvalidOperationException("Sliding affinity TTL was not persisted.");
            }
            clock.Advance(TimeSpan.FromMinutes(20));
            var sliding = new PatGatewaySessionAffinityStore(root, secret, clock, maximumEntries: 32)
                .ResolveOrClaim([PatGatewaySessionAffinityKey.Session(sessionId)], accountB);
            if (sliding.AccountKey != accountA)
            {
                throw new InvalidOperationException("Confirmed affinity did not slide its one-hour TTL.");
            }

            var staleLease = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Session(sessionId)],
                accountC);
            var movingLease = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Session(sessionId)],
                accountC);
            var moved = reloaded.Move(movingLease, accountB) ??
                        throw new InvalidOperationException("Affinity move CAS unexpectedly failed.");
            if (reloaded.Confirm(moved) is not { Applied: true, Persisted: true })
            {
                throw new InvalidOperationException("Moved affinity was not confirmed.");
            }
            _ = reloaded.Confirm(staleLease);
            var movedHit = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Session(sessionId)],
                accountA);
            if (movedHit.AccountKey != accountB)
            {
                throw new InvalidOperationException("A stale lease overwrote a newer affinity move.");
            }

            var rollbackSource = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Conversation("conversation-rollback")],
                accountA);
            _ = reloaded.Confirm(rollbackSource);
            var rollbackAttempt = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Conversation("conversation-rollback")],
                accountB);
            var rollbackMove = reloaded.Move(rollbackAttempt, accountC) ??
                               throw new InvalidOperationException("Rollback move was not staged.");
            _ = reloaded.ReleaseFailed(rollbackMove);
            var rollbackHit = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Conversation("conversation-rollback")],
                accountB);
            if (rollbackHit.AccountKey != accountA)
            {
                throw new InvalidOperationException("Failed affinity move did not restore its durable route.");
            }

            var failedClaim = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Thread("thread-failed-claim")],
                accountC);
            _ = reloaded.ReleaseFailed(failedClaim);
            var reclaimed = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Thread("thread-failed-claim")],
                accountB);
            if (reclaimed.AccountKey != accountB)
            {
                throw new InvalidOperationException("Failed provisional claim was not released.");
            }

            if (reloaded.BindResponse(responseId, accountB) is not { Applied: true, Persisted: true })
            {
                throw new InvalidOperationException("Response affinity was not persisted.");
            }
            var responseHit = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Response(responseId)],
                accountA);
            if (responseHit.AccountKey != accountB ||
                reloaded.Move(responseHit, accountC) != null)
            {
                throw new InvalidOperationException(
                    "Response affinity was not strict or was incorrectly movable.");
            }

            var invalidated = reloaded.InvalidateOrdinaryBindingsForAccount(accountB);
            var ordinaryAfterInvalidation = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Session(sessionId)],
                accountA);
            var responseAfterInvalidation = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Response(responseId)],
                accountA);
            if (invalidated is not { Applied: true, Persisted: true } ||
                ordinaryAfterInvalidation.AccountKey != accountA ||
                responseAfterInvalidation.AccountKey != accountB ||
                !responseAfterInvalidation.MatchedConfirmed)
            {
                throw new InvalidOperationException(
                    "Pool-membership invalidation did not clear ordinary affinity while preserving strict response bindings.");
            }

            var batchLease = reloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Session(batchSessionId)],
                accountC);
            if (reloaded.ConfirmAndBindResponses(
                    batchLease,
                    [batchResponseId1, batchResponseId2],
                    accountC) is not { Applied: true, Persisted: true })
            {
                throw new InvalidOperationException(
                    "Atomic affinity confirmation and response binding failed.");
            }

            var batchPersisted = File.ReadAllText(reloaded.Path);
            if (batchPersisted.Contains(batchSessionId, StringComparison.Ordinal) ||
                batchPersisted.Contains(batchResponseId1, StringComparison.Ordinal) ||
                batchPersisted.Contains(batchResponseId2, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Atomic affinity persistence exposed a raw identifier.");
            }

            var batchReloaded = new PatGatewaySessionAffinityStore(
                root,
                secret,
                clock,
                maximumEntries: 32);
            var batchSessionHit = batchReloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Session(batchSessionId)],
                accountA);
            var batchResponseHit1 = batchReloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Response(batchResponseId1)],
                accountA);
            var batchResponseHit2 = batchReloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Response(batchResponseId2)],
                accountA);
            var conflictingBatch = batchReloaded.ConfirmAndBindResponses(
                null,
                [batchResponseId1],
                accountA);
            var conflictHit = batchReloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Response(batchResponseId1)],
                accountA);
            if (batchSessionHit.AccountKey != accountC ||
                batchResponseHit1.AccountKey != accountC ||
                batchResponseHit2.AccountKey != accountC ||
                conflictingBatch is not { Applied: true, Persisted: true } ||
                conflictHit.AccountKey != accountC ||
                conflictHit.MatchedConfirmed)
            {
                throw new InvalidOperationException(
                    "Atomic affinity bindings did not reload or a conflicting response id was not poisoned.");
            }

            var stillPoisoned = batchReloaded.ConfirmAndBindResponses(
                null,
                [batchResponseId1],
                accountC);
            var poisonHit = batchReloaded.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Response(batchResponseId1)],
                accountB);
            if (stillPoisoned is not { Applied: true, Persisted: true } ||
                poisonHit.MatchedConfirmed)
            {
                throw new InvalidOperationException(
                    "A poisoned response id was incorrectly restored by a later owner.");
            }

            var boundedRoot = System.IO.Path.Combine(root, "bounded");
            var bounded = new PatGatewaySessionAffinityStore(
                boundedRoot,
                secret,
                clock,
                maximumEntries: 2);
            _ = bounded.BindResponse("resp_capacity_1", accountA);
            clock.Advance(TimeSpan.FromSeconds(1));
            _ = bounded.BindResponse("resp_capacity_2", accountA);
            clock.Advance(TimeSpan.FromSeconds(1));
            _ = bounded.BindResponse("resp_capacity_3", accountA);
            lock (bounded._gate)
            {
                if (bounded._entries.Count != 2)
                {
                    throw new InvalidOperationException("Affinity capacity was not enforced.");
                }
            }

            var differentSecret = new PatGatewaySessionAffinityStore(
                root,
                "different-test-only-affinity-secret-32-bytes",
                clock,
                maximumEntries: 32);
            var isolated = differentSecret.ResolveOrClaim(
                [PatGatewaySessionAffinityKey.Session(sessionId)],
                accountC);
            if (isolated.AccountKey != accountC)
            {
                throw new InvalidOperationException("Affinity HMAC key ids were not isolated.");
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

    private readonly record struct HashedKey(
        PatGatewaySessionAffinityKeyKind Kind,
        string Digest,
        string StorageKey);

    private sealed class BindingEntry
    {
        internal required PatGatewaySessionAffinityKeyKind Kind { get; init; }
        internal required string Digest { get; init; }
        internal required string AccountKey { get; set; }
        internal required long Version { get; set; }
        internal required bool Confirmed { get; set; }
        internal bool Ambiguous { get; set; }
        internal Guid? ProvisionalOwner { get; set; }
        internal required DateTimeOffset ExpiresAtUtc { get; set; }
        internal required DateTimeOffset UpdatedAtUtc { get; set; }
        internal DurableSnapshot? DurableFallback { get; set; }

        internal DurableSnapshot ToDurableSnapshot() => new(
            Kind,
            Digest,
            AccountKey,
            Version,
            ExpiresAtUtc,
            UpdatedAtUtc,
            Ambiguous);

        internal static BindingEntry FromSnapshot(DurableSnapshot snapshot) => new()
        {
            Kind = snapshot.Kind,
            Digest = snapshot.Digest,
            AccountKey = snapshot.AccountKey,
            Version = snapshot.Version,
            Confirmed = true,
            Ambiguous = snapshot.Ambiguous,
            ExpiresAtUtc = snapshot.ExpiresAtUtc,
            UpdatedAtUtc = snapshot.UpdatedAtUtc
        };
    }

    private sealed record DurableSnapshot(
        PatGatewaySessionAffinityKeyKind Kind,
        string Digest,
        string AccountKey,
        long Version,
        DateTimeOffset ExpiresAtUtc,
        DateTimeOffset UpdatedAtUtc,
        bool Ambiguous);

    private sealed class AffinityDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("keyId")]
        public string? KeyId { get; set; }

        [JsonPropertyName("entries")]
        public List<PersistedEntry>? Entries { get; set; }
    }

    private sealed class PersistedEntry
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }

        [JsonPropertyName("accountKey")]
        public string? AccountKey { get; set; }

        [JsonPropertyName("version")]
        public long Version { get; set; }

        [JsonPropertyName("expiresAtUtc")]
        public DateTimeOffset ExpiresAtUtc { get; set; }

        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; set; }

        [JsonPropertyName("ambiguous")]
        public bool Ambiguous { get; set; }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }
            _utcNow += duration;
        }
    }
}
