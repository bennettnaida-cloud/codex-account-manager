using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountManager;

internal enum PatGatewayRotationStatus
{
    None,
    Armed,
    Active
}

internal sealed record PatGatewayRotationSnapshot(
    PatGatewayRotationStatus Status,
    string? TransportAccountKey,
    string? SourceAccountKey,
    string? TargetAccountKey,
    DateTimeOffset? ArmedAtUtc,
    DateTimeOffset? ActivatedAtUtc)
{
    internal static readonly PatGatewayRotationSnapshot Empty = new(
        PatGatewayRotationStatus.None,
        null,
        null,
        null,
        null,
        null);
}

/// <summary>
/// Persists only stable account hashes and rotation timestamps. Credentials and provider
/// details are always resolved from the target account inside the gateway process and are
/// never written to this route file or sent through the control API.
/// </summary>
internal sealed class PatGatewayRotationStore
{
    // Every release uses a port-scoped route file. The 2.2.25/2.3.9 gateways can remain installed
    // (and, during an interrupted update, even remain alive on 8317) without being
    // able to arm/clear the 2.3.12 route behind our back.
    internal const string FileName =
        "account-auto-rotation-route-v2-" + ReleaseConfiguration.GatewayPortText + ".json";
    private const string LegacyFileName =
        "pat-auto-rotation-route-v1-" + ReleaseConfiguration.GatewayPortText + ".json";
    private const int SchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly string _legacyPath;
    private readonly object _gate = new();
    private PatGatewayRotationSnapshot _lastKnownSnapshot = PatGatewayRotationSnapshot.Empty;
    private bool _hasLastKnownSnapshot;
    private int _consecutiveLoadFailures;

    internal PatGatewayRotationStore(string managerRoot)
    {
        var cacheRoot = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(managerRoot),
            ".cache");
        _path = System.IO.Path.Combine(cacheRoot, FileName);
        _legacyPath = System.IO.Path.Combine(cacheRoot, LegacyFileName);
    }

    internal string Path => _path;

    internal PatGatewayRotationSnapshot Load()
    {
        lock (_gate)
        {
            Exception? lastError = null;
            for (var attempt = 0; attempt < AtomicFilePersistence.DefaultAttempts; attempt++)
            {
                try
                {
                    var snapshot = LoadOnce();
                    _lastKnownSnapshot = snapshot;
                    _hasLastKnownSnapshot = true;
                    _consecutiveLoadFailures = 0;
                    return snapshot;
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or JsonException or
                    NotSupportedException)
                {
                    lastError = ex;
                    _consecutiveLoadFailures++;
                    if (attempt + 1 < AtomicFilePersistence.DefaultAttempts)
                    {
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }
            }

            ManagerLifecycleDiagnostics.WriteException(
                "pat-gateway-rotation-route-read-failed",
                lastError ?? new IOException("Unknown route read failure."),
                $"failures={_consecutiveLoadFailures}; cached={_hasLastKnownSnapshot}; " +
                $"status={_lastKnownSnapshot.Status}; " +
                $"transport={_lastKnownSnapshot.TransportAccountKey ?? "none"}; " +
                $"effective={_lastKnownSnapshot.TargetAccountKey ?? _lastKnownSnapshot.SourceAccountKey ?? "none"}");
            if (_hasLastKnownSnapshot)
            {
                return _lastKnownSnapshot;
            }

            throw new IOException(
                "PAT rotation route remained unreadable after bounded retries.",
                lastError);
        }
    }

    private PatGatewayRotationSnapshot LoadOnce()
    {
        if (File.Exists(_path))
        {
            return LoadDocument(_path, SchemaVersion);
        }
        if (!File.Exists(_legacyPath))
        {
            // A route is valid only for the listener/desktop-base-URL pair that
            // created it. Never import another port's route into this release.
            return PatGatewayRotationSnapshot.Empty;
        }
        return LoadLegacyAndMigrate();
    }

    private PatGatewayRotationSnapshot LoadLegacyAndMigrate()
    {
        var legacy = LoadDocument(_legacyPath, expectedSchemaVersion: 1);
        if (legacy.Status == PatGatewayRotationStatus.None)
        {
            return legacy;
        }

        // Preserve a live PAT chain across the gateway upgrade. The v2 document is
        // atomically committed before the legacy file is removed, so a failed migration
        // can never silently send the desktop's original transport PAT upstream again.
        try
        {
            Save(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The validated legacy route remains authoritative for this request. It is
            // safer to retry migration later than to fall back to the transport account.
            return legacy;
        }
        try
        {
            File.Delete(_legacyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The committed v2 file wins on the next load; a leftover v1 file is inert.
        }
        return legacy;
    }

    private static PatGatewayRotationSnapshot LoadDocument(
        string path,
        int expectedSchemaVersion)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var document = JsonSerializer.Deserialize<RouteDocument>(stream, JsonOptions);
        if (document == null ||
            document.SchemaVersion != expectedSchemaVersion ||
            !TryNormalizeAccountKey(document.TransportAccountKey, out var transport) ||
            !TryNormalizeAccountKey(document.SourceAccountKey, out var source) ||
            !TryNormalizeAccountKey(document.TargetAccountKey, out var target) ||
            source.Equals(target, StringComparison.Ordinal) ||
            document.ArmedAtUtc == default)
        {
            return PatGatewayRotationSnapshot.Empty;
        }

        var status = document.Status switch
        {
            "armed" => PatGatewayRotationStatus.Armed,
            "active" when document.ActivatedAtUtc.HasValue => PatGatewayRotationStatus.Active,
            _ => PatGatewayRotationStatus.None
        };
        return status == PatGatewayRotationStatus.None
            ? PatGatewayRotationSnapshot.Empty
            : new PatGatewayRotationSnapshot(
                status,
                transport,
                source,
                target,
                document.ArmedAtUtc.ToUniversalTime(),
                document.ActivatedAtUtc?.ToUniversalTime());
    }

    internal PatGatewayRotationSnapshot Arm(
        string sourceAccountKey,
        string targetAccountKey,
        DateTimeOffset armedAtUtc,
        bool replaceExistingArmedTarget = false,
        string? transportAccountKey = null)
    {
        if (!TryNormalizeAccountKey(sourceAccountKey, out var source) ||
            !TryNormalizeAccountKey(targetAccountKey, out var target) ||
            source.Equals(target, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Rotation account hashes are invalid.");
        }
        var hasExplicitTransport = TryNormalizeAccountKey(
            transportAccountKey,
            out var explicitTransport);

        var existing = Load();
        if (existing.Status == PatGatewayRotationStatus.Armed &&
            string.Equals(existing.SourceAccountKey, source, StringComparison.Ordinal) &&
            string.Equals(existing.TargetAccountKey, target, StringComparison.Ordinal) &&
            (!hasExplicitTransport ||
             string.Equals(
                 existing.TransportAccountKey,
                 explicitTransport,
                 StringComparison.Ordinal)))
        {
            return existing;
        }

        if (replaceExistingArmedTarget &&
            existing.Status == PatGatewayRotationStatus.Armed &&
            string.Equals(existing.SourceAccountKey, source, StringComparison.Ordinal) &&
            TryNormalizeAccountKey(existing.TransportAccountKey, out var armedTransport))
        {
            // A user-requested force switch may supersede an automatic/manual target that
            // has not crossed a request boundary yet. Preserve the stable desktop transport
            // and atomically replace only the logical target in the same route document.
            var replacementTransport = hasExplicitTransport
                ? explicitTransport
                : armedTransport;
            var replacement = new PatGatewayRotationSnapshot(
                PatGatewayRotationStatus.Armed,
                replacementTransport,
                source,
                target,
                armedAtUtc.ToUniversalTime(),
                null);
            Save(replacement);
            return replacement;
        }

        string transport;
        if (existing.Status == PatGatewayRotationStatus.Active &&
            string.Equals(existing.TargetAccountKey, source, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(existing.TransportAccountKey))
        {
            // The desktop client continues sending the first PAT in a chain. Keep that
            // transport identity while changing only the logical source and next target.
            transport = existing.TransportAccountKey;
        }
        else if (existing.Status == PatGatewayRotationStatus.None &&
                 hasExplicitTransport)
        {
            // A compatible-API account is a logical target, not the bearer that the
            // desktop Codex process sends to this listener.  The Manager supplies the
            // still-live OAuth/PAT transport identity when no active route can carry it.
            transport = explicitTransport;
        }
        else if (existing.Status == PatGatewayRotationStatus.None)
        {
            transport = source;
        }
        else
        {
            throw new InvalidOperationException(
                "Rotation source does not match the gateway's active logical account.");
        }

        var snapshot = new PatGatewayRotationSnapshot(
            PatGatewayRotationStatus.Armed,
            transport,
            source,
            target,
            armedAtUtc.ToUniversalTime(),
            null);
        Save(snapshot);
        return snapshot;
    }

    internal PatGatewayRotationSnapshot Activate(
        PatGatewayRotationSnapshot armed,
        DateTimeOffset activatedAtUtc)
    {
        if (armed.Status != PatGatewayRotationStatus.Armed ||
            !TryNormalizeAccountKey(armed.TransportAccountKey, out _) ||
            !TryNormalizeAccountKey(armed.SourceAccountKey, out _) ||
            !TryNormalizeAccountKey(armed.TargetAccountKey, out _))
        {
            throw new InvalidOperationException("Only a valid armed route can be activated.");
        }

        var snapshot = armed with
        {
            Status = PatGatewayRotationStatus.Active,
            ActivatedAtUtc = activatedAtUtc.ToUniversalTime()
        };
        Save(snapshot);
        return snapshot;
    }

    internal void Clear()
    {
        lock (_gate)
        {
            try
            {
                foreach (var path in new[] { _path, _legacyPath })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Unable to clear the PAT rotation route.", ex);
            }
            _lastKnownSnapshot = PatGatewayRotationSnapshot.Empty;
            _hasLastKnownSnapshot = true;
            _consecutiveLoadFailures = 0;
        }
    }

    internal static bool TryNormalizeAccountKey(string? value, out string normalized)
    {
        normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length == 64 && normalized.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
    }

    internal static void Validate()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pat-rotation-store-test-" + Guid.NewGuid().ToString("N"));
        var a = new string('A', 64);
        var b = new string('B', 64);
        var c = new string('C', 64);
        try
        {
            var store = new PatGatewayRotationStore(root);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(store.Path)!);
            File.WriteAllText(
                System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(store.Path)!,
                    "account-auto-rotation-route-v2.json"),
                JsonSerializer.Serialize(
                    new RouteDocument
                    {
                        SchemaVersion = SchemaVersion,
                        Status = "active",
                        TransportAccountKey = c,
                        SourceAccountKey = c,
                        TargetAccountKey = b,
                        ArmedAtUtc = DateTimeOffset.UtcNow,
                        ActivatedAtUtc = DateTimeOffset.UtcNow
                    },
                    JsonOptions),
                new UTF8Encoding(false));
            if (store.Load().Status != PatGatewayRotationStatus.None)
            {
                throw new InvalidOperationException(
                    "A new port-scoped gateway imported a stale shared rotation route.");
            }
            var armed = store.Arm(a, b, DateTimeOffset.UtcNow);
            var forced = store.Arm(
                a,
                c,
                DateTimeOffset.UtcNow.AddMilliseconds(500),
                replaceExistingArmedTarget: true);
            if (forced.Status != PatGatewayRotationStatus.Armed ||
                forced.TransportAccountKey != a ||
                forced.SourceAccountKey != a ||
                forced.TargetAccountKey != c)
            {
                throw new InvalidOperationException(
                    "A force switch did not atomically replace the pending target.");
            }
            var repairedTransport = store.Arm(
                a,
                c,
                DateTimeOffset.UtcNow.AddMilliseconds(600),
                replaceExistingArmedTarget: true,
                transportAccountKey: new string('D', 64));
            if (repairedTransport.TransportAccountKey != new string('D', 64) ||
                repairedTransport.SourceAccountKey != a ||
                repairedTransport.TargetAccountKey != c)
            {
                throw new InvalidOperationException(
                    "A force switch did not repair a stale pending transport identity.");
            }
            store.Clear();
            var explicitTransport = store.Arm(
                a,
                b,
                DateTimeOffset.UtcNow,
                transportAccountKey: new string('D', 64));
            if (explicitTransport.TransportAccountKey != new string('D', 64))
            {
                throw new InvalidOperationException(
                    "A compatible logical source did not preserve its explicit desktop transport.");
            }
            store.Clear();
            armed = store.Arm(
                a,
                b,
                DateTimeOffset.UtcNow.AddMilliseconds(750),
                replaceExistingArmedTarget: true);
            var active = store.Activate(armed, DateTimeOffset.UtcNow.AddSeconds(1));
            var chained = store.Arm(b, c, DateTimeOffset.UtcNow.AddSeconds(2));
            var serialized = File.ReadAllText(store.Path);
            if (active.Status != PatGatewayRotationStatus.Active ||
                chained.Status != PatGatewayRotationStatus.Armed ||
                chained.TransportAccountKey != a ||
                chained.SourceAccountKey != b ||
                chained.TargetAccountKey != c ||
                serialized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                serialized.Contains("@", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Gateway rotation persistence or chain semantics failed.");
            }

            using (var locked = new FileStream(
                       store.Path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                var cachedDuringTransientFailure = store.Load();
                if (cachedDuringTransientFailure.Status != PatGatewayRotationStatus.Armed ||
                    cachedDuringTransientFailure.TargetAccountKey != c)
                {
                    throw new InvalidOperationException(
                        "A transient route read failure discarded the last validated armed route.");
                }
            }

            store.Clear();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(store._legacyPath)!);
            File.WriteAllText(
                store._legacyPath,
                JsonSerializer.Serialize(
                    new RouteDocument
                    {
                        SchemaVersion = 1,
                        Status = "active",
                        TransportAccountKey = a,
                        SourceAccountKey = a,
                        TargetAccountKey = b,
                        ArmedAtUtc = DateTimeOffset.UtcNow,
                        ActivatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1)
                    },
                    JsonOptions),
                new UTF8Encoding(false));
            var migrated = store.Load();
            if (migrated.Status != PatGatewayRotationStatus.Active ||
                migrated.TransportAccountKey != a ||
                migrated.TargetAccountKey != b ||
                !File.Exists(store.Path) ||
                File.Exists(store._legacyPath))
            {
                throw new InvalidOperationException("Gateway v1 route migration failed.");
            }

            File.WriteAllText(store.Path, "{}", new UTF8Encoding(false));
            File.WriteAllText(
                store._legacyPath,
                JsonSerializer.Serialize(
                    new RouteDocument
                    {
                        SchemaVersion = 1,
                        Status = "active",
                        TransportAccountKey = a,
                        SourceAccountKey = a,
                        TargetAccountKey = b,
                        ArmedAtUtc = DateTimeOffset.UtcNow,
                        ActivatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1)
                    },
                    JsonOptions),
                new UTF8Encoding(false));
            if (store.Load().Status != PatGatewayRotationStatus.None ||
                !File.Exists(store._legacyPath))
            {
                throw new InvalidOperationException(
                    "A corrupt v2 route must not revive a stale legacy route.");
            }

            store.Clear();
            if (store.Load().Status != PatGatewayRotationStatus.None)
            {
                throw new InvalidOperationException("Gateway rotation clear failed.");
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

    private void Save(PatGatewayRotationSnapshot snapshot)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var document = new RouteDocument
            {
                SchemaVersion = SchemaVersion,
                Status = snapshot.Status == PatGatewayRotationStatus.Active ? "active" : "armed",
                TransportAccountKey = snapshot.TransportAccountKey,
                SourceAccountKey = snapshot.SourceAccountKey,
                TargetAccountKey = snapshot.TargetAccountKey,
                ArmedAtUtc = snapshot.ArmedAtUtc ?? DateTimeOffset.UtcNow,
                ActivatedAtUtc = snapshot.ActivatedAtUtc
            };
            AtomicFilePersistence.WriteAllText(
                _path,
                JsonSerializer.Serialize(document, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _lastKnownSnapshot = snapshot;
            _hasLastKnownSnapshot = true;
            _consecutiveLoadFailures = 0;
        }
    }

    private sealed class RouteDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("transportAccountKey")]
        public string? TransportAccountKey { get; set; }

        [JsonPropertyName("sourceAccountKey")]
        public string? SourceAccountKey { get; set; }

        [JsonPropertyName("targetAccountKey")]
        public string? TargetAccountKey { get; set; }

        [JsonPropertyName("armedAtUtc")]
        public DateTimeOffset ArmedAtUtc { get; set; }

        [JsonPropertyName("activatedAtUtc")]
        public DateTimeOffset? ActivatedAtUtc { get; set; }
    }
}
