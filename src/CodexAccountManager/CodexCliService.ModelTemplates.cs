using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    // Public model metadata only. No account key, token or provider URL is sent here.
    private const string OfficialCompatibleModelTemplatesUrl =
        "https://raw.githubusercontent.com/openai/codex/main/codex-rs/models-manager/models.json";
    private const string OfficialCompatibleModelTemplatesFile = "compatible-model-templates.official.json";
    private static readonly SemaphoreSlim OfficialCompatibleModelTemplatesGate = new(1, 1);
    private static DateTimeOffset _nextOfficialModelTemplateAttemptUtc;

    private static string OfficialCompatibleModelTemplatesPath =>
        Path.Combine(new AccountStore().RootPath, OfficialCompatibleModelTemplatesFile);

    private static async Task RefreshOfficialCompatibleModelTemplatesAsync(CancellationToken cancellationToken)
    {
        await OfficialCompatibleModelTemplatesGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextOfficialModelTemplateAttemptUtc) return;
            _nextOfficialModelTemplateAttemptUtc = now.AddMinutes(5);
            var path = OfficialCompatibleModelTemplatesPath;
            var modified = File.GetLastWriteTimeUtc(path);
            if (modified <= now.UtcDateTime && modified > now.UtcDateTime.AddMinutes(-15) &&
                ReadOfficialCompatibleModelTemplates().Count > 0) return;

            var settings = new ThemeService(new AccountStore().RootPath).LoadSettings();
            var proxy = BuildPatGatewayProxyUri(settings);
            if (string.IsNullOrWhiteSpace(proxy)) proxy = GetWindowsProxyUri();
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false
            };
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await client.GetAsync(
                OfficialCompatibleModelTemplatesUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > ManagedCompatibleModelCatalogMaxBytes)
                throw new InvalidDataException("Official model metadata exceeds the size limit.");
            var body = await ReadResponseBodyUpToLimitAsync(
                response.Content, (int)ManagedCompatibleModelCatalogMaxBytes, timeout.Token);
            if (body == null) throw new InvalidDataException("Official model metadata exceeds the size limit.");
            var json = Encoding.UTF8.GetString(body);
            if (ParseOfficialCompatibleModelTemplates(json).Count == 0)
                throw new InvalidDataException("Official model metadata has no usable entries.");
            // Validate before replacing; malformed data and transient failures keep the last good copy.
            AtomicFilePersistence.WriteAllText(path, json);
            _nextOfficialModelTemplateAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(15);
            ManagerLifecycleDiagnostics.Write("compatible-model-templates-refreshed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ManagerLifecycleDiagnostics.WriteException("compatible-model-templates-refresh-failed", ex);
        }
        finally
        {
            OfficialCompatibleModelTemplatesGate.Release();
        }
    }

    private static Dictionary<string, JsonObject> ReadOfficialCompatibleModelTemplates()
    {
        try
        {
            var path = OfficialCompatibleModelTemplatesPath;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > ManagedCompatibleModelCatalogMaxBytes) return new();
            return ParseOfficialCompatibleModelTemplates(AtomicFilePersistence.ReadAllTextWithRetry(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                       InvalidOperationException or FormatException)
        {
            return new();
        }
    }

    private static Dictionary<string, JsonObject> ParseOfficialCompatibleModelTemplates(string json)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var root = JsonNode.Parse(json) as JsonObject;
        if (root?["models"] is not JsonArray models) return result;
        foreach (var node in models.OfType<JsonObject>())
        {
            if (node["slug"] is not JsonValue slugValue || !slugValue.TryGetValue<string>(out var slug) ||
                !IsSafeCompatibleApiModelId(slug) || !IsUserSelectableCompatibleApiModelId(slug) ||
                node["display_name"] is not JsonValue display || !display.TryGetValue<string>(out _) ||
                node["supported_reasoning_levels"] is not JsonArray ||
                node["model_messages"] is not JsonObject ||
                node["shell_type"]?.ToString() is not ("unified_exec" or "shell_command") ||
                node["context_window"] is not JsonValue window ||
                !window.TryGetValue<long>(out var tokens) || tokens <= 0)
                continue;
            result.TryAdd(slug, node);
        }
        return result;
    }

    private static JsonObject? ReadPackagedCompatibleModelTemplate(string model)
    {
        var path = PackagedCompatibleModelCatalogPath(model);
        if (path == null) return null;
        try
        {
            var template = JsonNode.Parse(AtomicFilePersistence.ReadAllTextWithRetry(path)) as JsonObject;
            return (template?["models"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(candidate => candidate["slug"]?.GetValue<string>() == model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                       InvalidOperationException or FormatException)
        {
            // A broken template does not invalidate other entries in the account's catalog.
            return null;
        }
    }
}
