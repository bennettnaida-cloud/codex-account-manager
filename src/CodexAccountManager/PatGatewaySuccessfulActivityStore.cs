using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal sealed record PatGatewaySuccessfulActivity(
    long Sequence,
    string AccountKey,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Persists the final account chosen by each completely successful model response. The
/// document contains only a stable account hash and timestamps, never a credential or body.
/// </summary>
internal sealed class PatGatewaySuccessfulActivityStore
{
    internal const string FileName =
        "pat-gateway-successful-activity-v1-" + ReleaseConfiguration.GatewayPortText + ".json";
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly ConcurrentDictionary<string, object> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly object _gate;

    internal PatGatewaySuccessfulActivityStore(string managerRoot)
    {
        var root = System.IO.Path.GetFullPath(managerRoot);
        _path = System.IO.Path.Combine(root, ".cache", FileName);
        _gate = FileGates.GetOrAdd(_path, static _ => new object());
    }

    internal string Path => _path;

    internal PatGatewaySuccessfulActivity? ReadLatest()
    {
        lock (_gate)
        {
            return LoadUnsafe();
        }
    }

    internal PatGatewaySuccessfulActivity Record(
        string accountKey,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(accountKey, out var normalized))
        {
            throw new InvalidDataException("Successful gateway activity account hash is invalid.");
        }

        lock (_gate)
        {
            var existing = LoadUnsafe();
            var completed = completedAtUtc.ToUniversalTime();
            if (existing != null && existing.CompletedAtUtc > completed)
            {
                return existing;
            }

            var activity = new PatGatewaySuccessfulActivity(
                Math.Max(0L, existing?.Sequence ?? 0L) + 1L,
                normalized,
                startedAtUtc?.ToUniversalTime(),
                completed);
            SaveUnsafe(activity);
            return activity;
        }
    }

    private PatGatewaySuccessfulActivity? LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<ActivityDocument>(
                AtomicFilePersistence.ReadAllTextWithRetry(_path),
                JsonOptions);
            if (document == null ||
                document.SchemaVersion != SchemaVersion ||
                document.Sequence <= 0 ||
                !PatGatewayRotationStore.TryNormalizeAccountKey(
                    document.AccountKey,
                    out var accountKey) ||
                document.CompletedAtUtc == default)
            {
                return null;
            }

            return new PatGatewaySuccessfulActivity(
                document.Sequence,
                accountKey,
                document.StartedAtUtc?.ToUniversalTime(),
                document.CompletedAtUtc.ToUniversalTime());
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private void SaveUnsafe(PatGatewaySuccessfulActivity activity)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var document = new ActivityDocument
        {
            SchemaVersion = SchemaVersion,
            Sequence = activity.Sequence,
            AccountKey = activity.AccountKey,
            StartedAtUtc = activity.StartedAtUtc,
            CompletedAtUtc = activity.CompletedAtUtc
        };
        AtomicFilePersistence.WriteAllText(
            _path,
            JsonSerializer.Serialize(document, JsonOptions));
    }

    internal static void Validate()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pat-gateway-successful-activity-test-" + Guid.NewGuid().ToString("N"));
        var firstKey = new string('A', 64);
        var secondKey = new string('B', 64);
        var now = DateTimeOffset.UtcNow;
        try
        {
            var store = new PatGatewaySuccessfulActivityStore(root);
            var first = store.Record(firstKey, now.AddSeconds(-2), now.AddSeconds(-1));
            var second = store.Record(secondKey, now, now.AddSeconds(1));
            var restarted = new PatGatewaySuccessfulActivityStore(root).ReadLatest();
            if (first.Sequence != 1 || second.Sequence != 2 || restarted != second ||
                File.ReadAllText(store.Path).Contains("token", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Successful gateway activity was not persisted atomically by account key.");
            }

            var stale = store.Record(firstKey, now, now.AddSeconds(-3));
            if (stale != second || store.ReadLatest() != second)
            {
                throw new InvalidOperationException(
                    "An older completion overwrote the final successful gateway account.");
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

    private sealed class ActivityDocument
    {
        public int SchemaVersion { get; set; }
        public long Sequence { get; set; }
        public string? AccountKey { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset CompletedAtUtc { get; set; }
    }
}
