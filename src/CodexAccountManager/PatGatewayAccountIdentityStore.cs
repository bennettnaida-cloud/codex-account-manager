using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountManager;

/// <summary>
/// Maps a non-secret account hash to the ChatGPT account id returned by the PAT
/// whoami endpoint. This lets the local sidebar profile follow a pure PAT launch
/// without turning auth.json into OAuth or persisting the PAT itself.
/// </summary>
internal sealed class PatGatewayAccountIdentityStore
{
    internal const string FileName =
        "pat-gateway-account-identities-v1-" +
        ReleaseConfiguration.GatewayPortText +
        ".json";
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly string _path;
    private readonly object _gate = new();

    internal PatGatewayAccountIdentityStore(string managerRoot)
    {
        _path = Path.Combine(Path.GetFullPath(managerRoot), ".cache", FileName);
    }

    internal bool Record(string accountKey, string chatGptAccountId)
    {
        if (!PatGatewayRotationStore.TryNormalizeAccountKey(accountKey, out var normalizedKey) ||
            !Guid.TryParse(chatGptAccountId, out var parsedAccountId))
        {
            return false;
        }

        lock (_gate)
        {
            var document = LoadDocument();
            var normalizedAccountId = parsedAccountId.ToString("D");
            if (document.AccountIds.TryGetValue(normalizedKey, out var existing) &&
                existing.Equals(normalizedAccountId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            document.AccountIds[normalizedKey] = normalizedAccountId;
            AtomicFilePersistence.WriteAllText(
                _path,
                JsonSerializer.Serialize(document, JsonOptions));
            return true;
        }
    }

    internal IReadOnlyDictionary<string, string> Load()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(
                LoadDocument().AccountIds,
                StringComparer.Ordinal);
        }
    }

    private IdentityDocument LoadDocument()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new IdentityDocument();
            }
            var text = AtomicFilePersistence.ReadAllTextWithRetry(_path);
            var document = JsonSerializer.Deserialize<IdentityDocument>(text, JsonOptions);
            if (document?.SchemaVersion != SchemaVersion)
            {
                return new IdentityDocument();
            }
            document.AccountIds ??= new Dictionary<string, string>(StringComparer.Ordinal);
            document.AccountIds = document.AccountIds
                .Where(pair =>
                    PatGatewayRotationStore.TryNormalizeAccountKey(pair.Key, out _) &&
                    Guid.TryParse(pair.Value, out _))
                .ToDictionary(
                    pair => pair.Key.Trim().ToUpperInvariant(),
                    pair => Guid.Parse(pair.Value).ToString("D"),
                    StringComparer.Ordinal);
            return document;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException)
        {
            ManagerLifecycleDiagnostics.WriteException(
                "pat-gateway-account-identity-read-failed",
                ex,
                "credentials_persisted=false");
            return new IdentityDocument();
        }
    }

    private sealed class IdentityDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = PatGatewayAccountIdentityStore.SchemaVersion;

        [JsonPropertyName("accountIds")]
        public Dictionary<string, string> AccountIds { get; set; } =
            new(StringComparer.Ordinal);
    }
}
