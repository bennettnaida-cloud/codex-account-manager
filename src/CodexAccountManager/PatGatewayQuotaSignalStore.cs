using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountManager;

internal sealed record PatGatewayQuotaSignal(
    long Sequence,
    string AccountKey,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ResetAtUtc = null);

/// <summary>
/// Persists only the gateway's confirmed quota-exhaustion observations without ever storing a token,
/// account name, e-mail address, provider URL, or response body. The manager can read
/// this file independently of both the gateway health endpoint and official quota APIs.
/// </summary>
internal sealed class PatGatewayQuotaSignalStore
{
    internal const string FileName =
        "pat-gateway-quota-signals-v1-" + ReleaseConfiguration.GatewayPortText + ".json";
    // Without an official reset window, policy accepts a 429 for only this short
    // interval.  The file retains it longer so a restarted Manager can match it to a
    // known five-hour window and prove that the corresponding reset has not happened.
    internal static readonly TimeSpan SignalLifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan MaximumRetention = TimeSpan.FromHours(6);
    internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);

    // Schema 1 treated every HTTP 429 as exhaustion. Schema 2 could still persist a
    // body-only usage marker before the v8 same-account confirmation completed. Reject
    // both during the 2.2.9 migration so stale evidence cannot force another switch.
    private const int CurrentSchemaVersion = 3;
    private const int MaximumRetainedSignals = 32;
    private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly ConcurrentDictionary<string, object> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly object _gate;

    internal PatGatewayQuotaSignalStore(string managerRoot)
    {
        if (string.IsNullOrWhiteSpace(managerRoot))
        {
            throw new ArgumentException("Gateway quota-signal root is required.", nameof(managerRoot));
        }

        var cacheRoot = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(managerRoot),
            ".cache");
        _path = System.IO.Path.Combine(cacheRoot, FileName);
        _gate = FileGates.GetOrAdd(_path, static _ => new object());
    }

    internal string Path => _path;

    internal PatGatewayQuotaSignal Record(
        string accountKey,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? resetAtUtc = null)
    {
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(accountKey, out var normalizedKey))
        {
            throw new InvalidDataException("Gateway quota signal requires a valid account hash.");
        }

        var observed = observedAtUtc.ToUniversalTime();
        var reset = resetAtUtc?.ToUniversalTime();
        lock (_gate)
        {
            var file = LoadUnsafe();
            var latestForAccount = file.Signals
                .Where(signal => signal.AccountKey.Equals(normalizedKey, StringComparison.Ordinal))
                .OrderByDescending(signal => signal.ObservedAtUtc)
                .ThenByDescending(signal => signal.Sequence)
                .FirstOrDefault();

            // Multiple Codex retries can receive the same exhausted-window response almost
            // simultaneously. Keep one durable event for that burst, while a genuinely later
            // 429 still receives a new monotonically increasing sequence number.
            if (latestForAccount != null &&
                (observed <= latestForAccount.ObservedAtUtc ||
                 observed - latestForAccount.ObservedAtUtc <= DuplicateWindow))
            {
                if (latestForAccount.ResetAtUtc == null && reset != null)
                {
                    var enriched = latestForAccount with { ResetAtUtc = reset };
                    file.Signals.RemoveAll(signal => signal.Sequence == enriched.Sequence);
                    file.Signals.Add(enriched);
                    SaveUnsafe(file);
                    return enriched;
                }
                return latestForAccount;
            }

            var maximumSequence = file.Signals
                .Select(signal => signal.Sequence)
                .DefaultIfEmpty(0L)
                .Max();
            var nextSequence = Math.Max(file.NextSequence, maximumSequence + 1L);
            if (nextSequence <= 0L)
            {
                throw new InvalidDataException("Gateway quota-signal sequence is exhausted.");
            }

            var signal = new PatGatewayQuotaSignal(nextSequence, normalizedKey, observed, reset);
            file.NextSequence = checked(nextSequence + 1L);
            file.Signals.Add(signal);
            file.Signals = file.Signals
                .Where(item => item.ObservedAtUtc >= observed - MaximumRetention)
                .OrderByDescending(item => item.ObservedAtUtc)
                .ThenByDescending(item => item.Sequence)
                .Take(MaximumRetainedSignals)
                .OrderBy(item => item.Sequence)
                .ToList();
            SaveUnsafe(file);
            return signal;
        }
    }

    internal PatGatewayQuotaSignal? ReadLatestForAccount(
        string accountKey,
        DateTimeOffset nowUtc)
    {
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(accountKey, out var normalizedKey))
        {
            return null;
        }

        lock (_gate)
        {
            return SelectCurrentSignals(LoadUnsafe(), nowUtc)
                .Where(signal => signal.AccountKey.Equals(normalizedKey, StringComparison.Ordinal))
                .OrderByDescending(signal => signal.ObservedAtUtc)
                .ThenByDescending(signal => signal.Sequence)
                .FirstOrDefault();
        }
    }

    internal PatGatewayQuotaSignal? ReadLatest(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return SelectCurrentSignals(LoadUnsafe(), nowUtc)
                .OrderByDescending(signal => signal.ObservedAtUtc)
                .ThenByDescending(signal => signal.Sequence)
                .FirstOrDefault();
        }
    }

    internal IReadOnlyList<PatGatewayQuotaSignal> ReadLatestPerAccount(
        DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return SelectCurrentSignals(LoadUnsafe(), nowUtc)
                .GroupBy(signal => signal.AccountKey, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(signal => signal.ObservedAtUtc)
                    .ThenByDescending(signal => signal.Sequence)
                    .First())
                .OrderByDescending(signal => signal.ObservedAtUtc)
                .ThenByDescending(signal => signal.Sequence)
                .ToList();
        }
    }

    private static IEnumerable<PatGatewayQuotaSignal> SelectCurrentSignals(
        SignalFile file,
        DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        return file.Signals.Where(signal =>
            signal.ObservedAtUtc <= now + FutureClockTolerance &&
            signal.ObservedAtUtc >= now - MaximumRetention &&
            (signal.ResetAtUtc is { } resetAtUtc
                ? resetAtUtc + AccountRotationConfiguration.PrimaryResetGracePeriod > now
                : signal.ObservedAtUtc >= now - SignalLifetime));
    }

    private SignalFile LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new SignalFile();
            }

            var file = JsonSerializer.Deserialize<SignalFile>(
                AtomicFilePersistence.ReadAllTextWithRetry(_path),
                JsonOptions);
            if (file == null || file.SchemaVersion != CurrentSchemaVersion)
            {
                return new SignalFile();
            }

            file.Signals ??= [];
            file.Signals = file.Signals
                .Where(signal =>
                    signal.Sequence > 0L &&
                    PatGatewayRotationStore.TryNormalizeAccountKey(
                        signal.AccountKey,
                        out _) &&
                    signal.ObservedAtUtc > DateTimeOffset.MinValue)
                .Select(signal => signal with
                {
                    AccountKey = signal.AccountKey.Trim().ToUpperInvariant(),
                    ObservedAtUtc = signal.ObservedAtUtc.ToUniversalTime(),
                    ResetAtUtc = signal.ResetAtUtc?.ToUniversalTime()
                })
                .GroupBy(signal => signal.Sequence)
                .Select(group => group
                    .OrderByDescending(signal => signal.ObservedAtUtc)
                    .First())
                .OrderBy(signal => signal.Sequence)
                .TakeLast(MaximumRetainedSignals)
                .ToList();
            var maximumSequence = file.Signals
                .Select(signal => signal.Sequence)
                .DefaultIfEmpty(0L)
                .Max();
            file.NextSequence = Math.Max(file.NextSequence, maximumSequence + 1L);
            if (file.NextSequence <= 0L)
            {
                return new SignalFile();
            }
            return file;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException or OverflowException)
        {
            // This signal accelerates recovery but must never prevent the gateway from
            // returning the real upstream response or prevent the Manager from opening.
            return new SignalFile();
        }
    }

    private void SaveUnsafe(SignalFile file)
    {
        file.SchemaVersion = CurrentSchemaVersion;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        AtomicFilePersistence.WriteAllText(
            _path,
            JsonSerializer.Serialize(file, JsonOptions));
    }

    internal static void Validate()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pat-gateway-quota-signal-test-" + Guid.NewGuid().ToString("N"));
        var firstKey = new string('A', 64);
        var secondKey = new string('B', 64);
        var resetlessKey = new string('C', 64);
        var now = DateTimeOffset.UtcNow;
        try
        {
            var legacyStore = new PatGatewayQuotaSignalStore(
                System.IO.Path.Combine(root, "legacy-schema-2"));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(legacyStore.Path)!);
            File.WriteAllText(
                legacyStore.Path,
                "{\"schemaVersion\":2,\"nextSequence\":2,\"signals\":[{" +
                "\"sequence\":1,\"accountKey\":\"" + firstKey + "\"," +
                "\"observedAtUtc\":\"" + now.ToString("O") + "\"}]}");
            if (legacyStore.ReadLatest(now) != null)
            {
                throw new InvalidOperationException(
                    "Schema-2 body-only quota evidence must be retired during the 2.2.9 migration.");
            }

            var store = new PatGatewayQuotaSignalStore(root);
            var expectedReset = now.AddHours(2);
            var first = store.Record(firstKey, now.AddSeconds(-10), expectedReset);
            var duplicate = store.Record(firstKey, now.AddSeconds(-9));
            var second = store.Record(secondKey, now.AddSeconds(-8));
            var laterFirst = store.Record(firstKey, now.AddSeconds(-6));
            if (first.Sequence != duplicate.Sequence ||
                first.ResetAtUtc != expectedReset ||
                duplicate.ResetAtUtc != expectedReset ||
                second.Sequence != first.Sequence + 1L ||
                laterFirst.Sequence != second.Sequence + 1L ||
                store.ReadLatestForAccount(firstKey, now)?.Sequence != laterFirst.Sequence ||
                store.ReadLatestForAccount(secondKey, now)?.Sequence != second.Sequence)
            {
                throw new InvalidOperationException(
                    "Gateway quota signals must deduplicate retry bursts and remain account-isolated.");
            }

            // A new store instance models both gateway and Manager process restarts. The
            // unexpired event and its sequence must survive, and the next write must remain
            // monotonic rather than starting again from one.
            var restarted = new PatGatewayQuotaSignalStore(root);
            if (restarted.ReadLatestForAccount(firstKey, now)?.Sequence != laterFirst.Sequence)
            {
                throw new InvalidOperationException(
                    "Gateway quota signal did not survive a process restart.");
            }
            var recoveredAccounts = restarted.ReadLatestPerAccount(now);
            if (recoveredAccounts.Count != 2 ||
                recoveredAccounts.Single(signal => signal.AccountKey == firstKey).Sequence !=
                    laterFirst.Sequence ||
                recoveredAccounts.Single(signal => signal.AccountKey == secondKey).Sequence !=
                    second.Sequence)
            {
                throw new InvalidOperationException(
                    "Gateway restart must recover the latest quota signal for every account in the ring.");
            }
            var afterRestart = restarted.Record(secondKey, now.AddSeconds(-4));
            if (afterRestart.Sequence != laterFirst.Sequence + 1L)
            {
                throw new InvalidOperationException(
                    "Gateway quota-signal sequence did not remain monotonic after restart.");
            }

            _ = restarted.Record(resetlessKey, now.AddSeconds(-2));

            if (restarted.ReadLatestForAccount(
                    resetlessKey,
                    now + SignalLifetime + TimeSpan.FromSeconds(1)) != null)
            {
                throw new InvalidOperationException(
                    "A reset-less confirmed signal must not remain actionable indefinitely after Manager restart.");
            }

            var resetBacked = restarted.Record(
                firstKey,
                now.AddSeconds(-1),
                now.AddHours(2));
            if (restarted.ReadLatestForAccount(
                    firstKey,
                    now + SignalLifetime + TimeSpan.FromSeconds(1))?.Sequence !=
                resetBacked.Sequence)
            {
                throw new InvalidOperationException(
                    "A confirmed signal with a future reset must remain actionable until that reset window elapses.");
            }

            if (restarted.ReadLatestForAccount(
                    firstKey,
                    now + MaximumRetention + TimeSpan.FromSeconds(1)) != null)
            {
                throw new InvalidOperationException(
                    "Expired gateway quota signals must fail closed.");
            }

            var serialized = File.ReadAllText(restarted.Path);
            using var parsed = JsonDocument.Parse(serialized);
            if (serialized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                serialized.Contains("@", StringComparison.Ordinal) ||
                serialized.Contains("http", StringComparison.OrdinalIgnoreCase) ||
                Directory.EnumerateFiles(
                        System.IO.Path.GetDirectoryName(restarted.Path)!,
                        System.IO.Path.GetFileName(restarted.Path) + ".*.tmp")
                    .Any() ||
                parsed.RootElement.GetProperty("schemaVersion").GetInt32() != CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    "Gateway quota-signal persistence leaked identity data or was not atomically committed.");
            }

            var attempts = new PatGatewayQuotaSignalAttemptTracker();
            if (!attempts.ShouldAttempt(first, now, TimeSpan.FromMinutes(1)) ||
                attempts.ShouldAttempt(first, now.AddSeconds(30), TimeSpan.FromMinutes(1)) ||
                !attempts.ShouldAttempt(first, now.AddMinutes(1), TimeSpan.FromMinutes(1)) ||
                !attempts.ShouldAttempt(laterFirst, now.AddMinutes(1).AddSeconds(1), TimeSpan.FromMinutes(1)))
            {
                throw new InvalidOperationException(
                    "Manager quota-signal attempts must deduplicate a sequence while allowing bounded retry and newer events.");
            }

            // A fresh tracker models a Manager restart: the still-live durable signal must
            // be attempted again instead of being lost with the previous process memory.
            if (!new PatGatewayQuotaSignalAttemptTracker().ShouldAttempt(
                    laterFirst,
                    now,
                    TimeSpan.FromMinutes(1)))
            {
                throw new InvalidOperationException(
                    "Manager restart must recover an unexpired durable quota signal.");
            }

            File.WriteAllText(restarted.Path, "{broken-json");
            if (new PatGatewayQuotaSignalStore(root).ReadLatest(now) != null)
            {
                throw new InvalidOperationException(
                    "A corrupt gateway quota-signal file must fail closed.");
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

    private sealed class SignalFile
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("nextSequence")]
        public long NextSequence { get; set; } = 1L;

        [JsonPropertyName("signals")]
        public List<PatGatewayQuotaSignal> Signals { get; set; } = [];
    }
}

/// <summary>
/// Process-local delivery guard. A new durable sequence is immediate; the same sequence
/// can be retried only after the caller's cooldown, and a fresh Manager process can recover
/// the still-live event from disk.
/// </summary>
internal sealed class PatGatewayQuotaSignalAttemptTracker
{
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    internal bool ShouldAttempt(
        PatGatewayQuotaSignal signal,
        DateTimeOffset nowUtc,
        TimeSpan retryInterval)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var now = nowUtc.ToUniversalTime();
        if (_attempts.TryGetValue(signal.AccountKey, out var previous))
        {
            if (signal.Sequence < previous.Sequence ||
                (signal.Sequence == previous.Sequence &&
                 now - previous.AttemptedAtUtc < retryInterval))
            {
                return false;
            }
        }

        _attempts[signal.AccountKey] = new Attempt(signal.Sequence, now);
        return true;
    }

    private sealed record Attempt(long Sequence, DateTimeOffset AttemptedAtUtc);
}
