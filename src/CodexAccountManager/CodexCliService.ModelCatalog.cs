using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    private const string ManagedModelCatalogMarker = " # codex-account-manager-model-catalog";
    private const string ManagedCompatibleModelCatalogFileName =
        ".codex-account-manager-compatible-models.json";
    private const long ManagedCompatibleModelCatalogMaxBytes = 16 * 1024 * 1024;
    private static readonly object CompatibleModelCatalogLock = new();

    internal async Task<int> RefreshCompatibleModelCatalogAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        var matches = new AccountStore().LoadAccounts()
            .Where(account => account.IsCompatibleApi &&
                              account.Name.Equals(accountName.Trim(), StringComparison.Ordinal))
            .Take(2)
            .ToList();
        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                "Compatible API account name is missing or ambiguous.");
        }

        var account = matches[0];
        await EnsureCompatibleApiLaunchPreflightAsync(account, cancellationToken);
        SyncCompatibleModelCatalogs();
        return GetCompatibleApiAllowedModels(account).Count;
    }

    internal int SyncCompatibleModelCatalogs()
    {
        var updated = 0;
        foreach (var account in new AccountStore().LoadAccounts().Where(a => a.IsCompatibleApi))
        {
            var catalog = ManagedCompatibleModelCatalogPath(account);
            if (catalog == null) continue;
            var homes = new List<string> { account.CodexHome };
            if (IsSharedActiveAccount(account)) homes.Add(GetDefaultCodexHome());
            foreach (var home in homes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.Combine(home, ConfigFileName);
                if (!File.Exists(path)) continue;
                var original = File.ReadAllText(path);
                var cleaned = RemoveManagedModelCatalog(original);
                if (Regex.IsMatch(cleaned, @"(?m)^\s*model_catalog_json\s*=")) continue;
                var projected = "model_catalog_json = " + TomlString(catalog) + ManagedModelCatalogMarker + Environment.NewLine + cleaned;
                if (projected == original) continue;
                var backup = Path.Combine(home, "backups", "model-catalog-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);
                BackupFileIfPresent(path, backup);
                WriteTextAtomically(path, projected);
                updated++;
            }
        }
        return updated;
    }

    private static string? ManagedCompatibleModelCatalogPath(AccountRecord account)
    {
        if (string.IsNullOrWhiteSpace(account.CodexHome))
        {
            return PackagedCompatibleModelCatalogPath(account.ApiModel);
        }

        var cachedPath = ManagedCompatibleModelCatalogPath(account.CodexHome);
        if (TryReadManagedCompatibleModelIds(cachedPath, out var cachedModels) &&
            cachedModels.Count > 0 &&
            (!account.UseBundledCompatibleApiModelCatalog ||
             IsCompleteBundledCompatibleModelCatalog(cachedModels)))
        {
            return cachedPath;
        }

        if (account.UseBundledCompatibleApiModelCatalog)
        {
            try
            {
                RefreshManagedCompatibleModelCatalog(account, Array.Empty<string>());
                if (TryReadManagedCompatibleModelIds(cachedPath, out cachedModels) &&
                    cachedModels.Count > 0 &&
                    IsCompleteBundledCompatibleModelCatalog(cachedModels))
                {
                    return cachedPath;
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // A read-only account home can still use the packaged single-model fallback.
            }
        }

        return PackagedCompatibleModelCatalogPath(account.ApiModel);
    }

    private static string ManagedCompatibleModelCatalogPath(string codexHome) =>
        Path.Combine(codexHome, ManagedCompatibleModelCatalogFileName);

    internal static IReadOnlySet<string> GetCompatibleApiAllowedModels(AccountRecord account)
    {
        var path = string.IsNullOrWhiteSpace(account.CodexHome)
            ? null
            : ManagedCompatibleModelCatalogPath(account.CodexHome);
        if (path != null &&
            TryReadManagedCompatibleModelIds(path, out var models) &&
            models.Count > 0)
        {
            if (!account.UseBundledCompatibleApiModelCatalog ||
                IsCompleteBundledCompatibleModelCatalog(models))
            {
                return new HashSet<string>(models, StringComparer.Ordinal);
            }
        }

        var resolvedPath = ManagedCompatibleModelCatalogPath(account);
        if (resolvedPath != null &&
            TryReadManagedCompatibleModelIds(resolvedPath, out models) &&
            models.Count > 0)
        {
            return new HashSet<string>(models, StringComparer.Ordinal);
        }

        var fallback = account.ApiModel?.Trim() ?? string.Empty;
        return GetCompatibleApiModelIdValidationError(fallback) == null
            ? new HashSet<string>(new[] { fallback }, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private static string? PackagedCompatibleModelCatalogPath(string? model)
    {
        var normalized = model?.Trim() ?? string.Empty;
        if (!Regex.IsMatch(normalized, "^[a-z0-9.-]+$")) return null;
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "codex-models",
            normalized + ".json");
        return File.Exists(path) ? path : null;
    }

    private static bool RefreshManagedCompatibleModelCatalog(
        AccountRecord account,
        IEnumerable<string> upstreamModelIds)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(upstreamModelIds);

        var defaultModel = account.ApiModel?.Trim() ?? string.Empty;
        var packagedModels = GetBundledCompatibleModelIds();
        var requestedModels = (account.UseBundledCompatibleApiModelCatalog
                ? upstreamModelIds.Concat(packagedModels)
                : upstreamModelIds)
            .Where(IsSafeCompatibleApiModelId)
            .Where(IsUserSelectableCompatibleApiModelId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(model => model.Equals(defaultModel, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var models = new JsonArray();
        foreach (var model in requestedModels)
        {
            var templatePath = Path.Combine(
                AppContext.BaseDirectory,
                "assets",
                "codex-models",
                model + ".json");
            if (!File.Exists(templatePath)) continue;

            try
            {
                var template = JsonNode.Parse(
                    AtomicFilePersistence.ReadAllTextWithRetry(templatePath)) as JsonObject;
                var source = template?["models"] is JsonArray templateModels
                    ? templateModels
                        .OfType<JsonObject>()
                        .FirstOrDefault(candidate =>
                            candidate["slug"]?.GetValue<string>()
                                .Equals(model, StringComparison.Ordinal) == true)
                    : null;
                if (source == null) continue;

                var selectable = source.DeepClone().AsObject();
                selectable["visibility"] = "list";
                models.Add(selectable);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidOperationException or FormatException)
            {
                // A broken local template cannot make the whole upstream catalog unusable.
            }
        }

        var output = new JsonObject { ["models"] = models }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        var path = ManagedCompatibleModelCatalogPath(account.CodexHome);
        lock (CompatibleModelCatalogLock)
        {
            try
            {
                if (File.Exists(path) &&
                    AtomicFilePersistence.ReadAllTextWithRetry(path).Equals(
                        output,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Atomic replacement below repairs an unreadable generated catalog.
            }

            AtomicFilePersistence.WriteAllText(path, output);
            return true;
        }
    }

    private static bool IsUserSelectableCompatibleApiModelId(string model) =>
        model.StartsWith("gpt-", StringComparison.Ordinal) &&
        !model.StartsWith("gpt-daybreak-", StringComparison.Ordinal) &&
        !model.Equals("codex-auto-review", StringComparison.Ordinal);

    private static IReadOnlyList<string> GetBundledCompatibleModelIds()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "assets", "codex-models");
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .Where(IsSafeCompatibleApiModelId)
            .Where(IsUserSelectableCompatibleApiModelId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsCompleteBundledCompatibleModelCatalog(
        IReadOnlyList<string> cachedModels)
    {
        var bundledModels = GetBundledCompatibleModelIds();
        return bundledModels.Count > 0 &&
               new HashSet<string>(cachedModels, StringComparer.Ordinal)
                   .SetEquals(bundledModels);
    }

    private static bool TryReadManagedCompatibleModelIds(
        string path,
        out IReadOnlyList<string> models)
    {
        models = Array.Empty<string>();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > ManagedCompatibleModelCatalogMaxBytes)
                return false;

            using var document = JsonDocument.Parse(
                AtomicFilePersistence.ReadAllTextWithRetry(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 256
                });
            if (!document.RootElement.TryGetProperty("models", out var catalogModels) ||
                catalogModels.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<string>();
            foreach (var item in catalogModels.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("slug", out var slugNode) ||
                    slugNode.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                var slug = slugNode.GetString();
                if (slug == null ||
                    !IsSafeCompatibleApiModelId(slug) ||
                    parsed.Contains(slug, StringComparer.Ordinal))
                {
                    return false;
                }
                parsed.Add(slug);
            }
            models = parsed;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    // Only remove our generated override; a user's custom catalog remains their choice.
    private static string RemoveManagedModelCatalog(string config) =>
        Regex.Replace(config, @"(?m)^[ \t]*model_catalog_json[ \t]*=[^\r\n]* # codex-account-manager-model-catalog[ \t]*\r?\n?", "");

    private static void ValidateCompatibleModelCatalogProjection()
    {
        var fixtureHome = Path.Combine(Path.GetTempPath(), "cam-model-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureHome);
        try
        {
            var account = new AccountRecord
            {
                AuthKind = AccountAuthKind.CompatibleApi,
                ApiModel = "gpt-5.6-sol",
                ApiBaseUrl = "https://example.test",
                CodexHome = fixtureHome,
                UseBundledCompatibleApiModelCatalog = true
            };
            var sixModels = new[]
            {
                "gpt-5.4", "gpt-5.4-mini", "gpt-5.5", "gpt-5.6-luna",
                "gpt-5.6-sol", "gpt-5.6-terra", "unknown-upstream-model"
            };
            var expectedModels = new[]
            {
                "gpt-5.6-sol", "gpt-5.2", "gpt-5.4", "gpt-5.4-mini",
                "gpt-5.5", "gpt-5.6-luna", "gpt-5.6-terra", "gpt-6-astra"
            };
            var firstWrite = RefreshManagedCompatibleModelCatalog(account, sixModels);
            var repeatedWrite = RefreshManagedCompatibleModelCatalog(account, sixModels);
            var sixRead = TryReadManagedCompatibleModelIds(
                ManagedCompatibleModelCatalogPath(account.CodexHome),
                out var initialModels);
            var catalogPath = ManagedCompatibleModelCatalogPath(account);
            var projected = ProjectCompatibleApiConfigText(
                ProjectCompatibleApiConfigText("", account), account);
            var reducedWrite = RefreshManagedCompatibleModelCatalog(
                account,
                new[] { "gpt-5.6-sol", "gpt-5.6-terra" });
            var reducedRead = TryReadManagedCompatibleModelIds(
                ManagedCompatibleModelCatalogPath(account.CodexHome),
                out var reducedModels);
            var providerHome = Path.Combine(fixtureHome, "provider-catalog");
            Directory.CreateDirectory(providerHome);
            var providerAccount = new AccountRecord
            {
                AuthKind = AccountAuthKind.CompatibleApi,
                ApiModel = "gpt-5.6-sol",
                ApiBaseUrl = "https://provider.example.test",
                CodexHome = providerHome
            };
            var providerWrite = RefreshManagedCompatibleModelCatalog(
                providerAccount,
                new[] { "gpt-5.6-sol", "gpt-5.6-terra", "unknown-upstream-model" });
            var providerRead = TryReadManagedCompatibleModelIds(
                ManagedCompatibleModelCatalogPath(providerAccount.CodexHome),
                out var providerModels);
            providerAccount.UseBundledCompatibleApiModelCatalog = true;
            var upgradedProviderPath = ManagedCompatibleModelCatalogPath(providerAccount);
            IReadOnlyList<string> upgradedProviderModels = Array.Empty<string>();
            var upgradedProviderRead = upgradedProviderPath != null &&
                TryReadManagedCompatibleModelIds(upgradedProviderPath, out upgradedProviderModels);
            providerAccount.UseBundledCompatibleApiModelCatalog = false;
            var downgradedProviderWrite = RefreshManagedCompatibleModelCatalog(
                providerAccount,
                new[] { "gpt-5.6-sol", "gpt-5.6-terra" });
            var downgradedProviderRead = TryReadManagedCompatibleModelIds(
                ManagedCompatibleModelCatalogPath(providerAccount.CodexHome),
                out var downgradedProviderModels);
            if (!firstWrite || repeatedWrite || !sixRead ||
                !initialModels.SequenceEqual(expectedModels, StringComparer.Ordinal) ||
                initialModels.Contains("unknown-upstream-model", StringComparer.Ordinal) ||
                catalogPath == null ||
                !projected.Contains("model_catalog_json = ", StringComparison.Ordinal) ||
                projected != ProjectCompatibleApiConfigText(projected, account) ||
                reducedWrite || !reducedRead ||
                !reducedModels.SequenceEqual(expectedModels, StringComparer.Ordinal) ||
                !providerWrite || !providerRead ||
                !providerModels.SequenceEqual(
                    new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
                    StringComparer.Ordinal) ||
                !upgradedProviderRead ||
                !upgradedProviderModels.SequenceEqual(expectedModels, StringComparer.Ordinal) ||
                !downgradedProviderWrite || !downgradedProviderRead ||
                !downgradedProviderModels.SequenceEqual(
                    new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
                    StringComparer.Ordinal) ||
                ProjectOfficialOAuthConfigText(projected).Contains("model_catalog_json", StringComparison.Ordinal) ||
                ProjectWindowsClientConfigText(projected).Contains("model_catalog_json", StringComparison.Ordinal) ||
                !ProjectCompatibleApiConfigText("model_catalog_json = \"C:/custom.json\"\n", account)
                    .Contains("model_catalog_json = \"C:/custom.json\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Account-scoped model catalog projection regression.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(fixtureHome, recursive: true);
            }
            catch
            {
                // A self-test cleanup failure must not hide the catalog assertions.
            }
        }
    }
}
