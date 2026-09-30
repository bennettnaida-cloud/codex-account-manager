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
        await RefreshOfficialCompatibleModelTemplatesAsync(cancellationToken);
        await EnsureCompatibleApiLaunchPreflightAsync(account, cancellationToken, validateConfiguredModel: false);
        SyncCompatibleModelCatalogs();
        return GetCompatibleApiAllowedModels(account).Count;
    }

    internal async Task RefreshAllCompatibleModelCatalogsAsync(CancellationToken cancellationToken)
    {
        var accounts = new AccountStore().LoadAccounts().Where(a => a.IsCompatibleApi).ToArray();
        if (accounts.Length > 0) await RefreshOfficialCompatibleModelTemplatesAsync(cancellationToken);
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await EnsureCompatibleApiLaunchPreflightAsync(
                    account, cancellationToken, validateConfiguredModel: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One offline account cannot prevent other providers from receiving new models.
                // Never log credentials, provider response bodies or account names.
                ManagerLifecycleDiagnostics.WriteException("compatible-model-auto-refresh-failed", ex);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var updated = SyncCompatibleModelCatalogs();
        ManagerLifecycleDiagnostics.Write("compatible-model-auto-refresh-completed",
            $"accounts={accounts.Length}; configs_updated={updated}");
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
                RefreshManagedCompatibleModelCatalog(account, cachedModels);
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
        IEnumerable<string> upstreamModelIds,
        Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(upstreamModelIds);

        var defaultModel = account.ApiModel?.Trim() ?? string.Empty;
        var packagedModels = GetBundledCompatibleModelIds();
        var upstreamModels = upstreamModelIds.Where(IsSafeCompatibleApiModelId)
            .Where(IsUserSelectableCompatibleApiModelId).ToArray();
        if (upstreamModels.Length == 0 && TryReadManagedCompatibleModelIds(
                ManagedCompatibleModelCatalogPath(account.CodexHome), out var existingModels) &&
            existingModels.Count > 0 && (!account.UseBundledCompatibleApiModelCatalog ||
                                       IsCompleteBundledCompatibleModelCatalog(existingModels)))
            return false;
        var requestedModels = (account.UseBundledCompatibleApiModelCatalog
                ? upstreamModels.Concat(packagedModels)
                : upstreamModels)
            .Where(IsSafeCompatibleApiModelId)
            .Where(IsUserSelectableCompatibleApiModelId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(model => model.Equals(defaultModel, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var officialTemplates = ReadOfficialCompatibleModelTemplates();
        var models = new JsonArray();
        foreach (var model in requestedModels)
        {
            var source = officialTemplates.GetValueOrDefault(model) ??
                         ReadPackagedCompatibleModelTemplate(model) ??
                         ReadPackagedCompatibleModelTemplate("fallback");
            if (source == null) continue;
            var selectable = source.DeepClone().AsObject();
            var isFallback = selectable["slug"]?.GetValue<string>() == "fallback";
            selectable["slug"] = model;
            if (isFallback) selectable["display_name"] = model;
            selectable["visibility"] = "list";
            models.Add(selectable);
        }

        // Empty/unsupported provider responses must not erase the last usable catalog.
        if (models.Count == 0) return false;

        var output = new JsonObject { ["models"] = models }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        var path = ManagedCompatibleModelCatalogPath(account.CodexHome);
        lock (CompatibleModelCatalogLock)
        {
            // The account/key may have changed while /models or official metadata was
            // in flight. Never publish that old response into the new account settings.
            if (isCurrent != null && !isCurrent()) return false;
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
            ManagerLifecycleDiagnostics.Write("compatible-model-catalog-updated", $"models={models.Count}");
            return true;
        }
    }

    private static string CompatibleModelCatalogIdentity(AccountRecord account, string credential) =>
        BuildVerifiedCompatibleApiModelFingerprint(account.ApiBaseUrl, account.ApiWireApi,
            account.ApiModel + ":" + account.UseBundledCompatibleApiModelCatalog, credential);

    private static bool IsCompatibleModelCatalogIdentityCurrent(AccountRecord account, string identity)
    {
        var current = new AccountStore().LoadAccounts().FirstOrDefault(candidate =>
            candidate.IsCompatibleApi && candidate.CodexHome.Equals(account.CodexHome, StringComparison.OrdinalIgnoreCase));
        return current != null && CompatibleModelCatalogIdentity(current,
            ReadAccessTokenCredential(Path.Combine(current.CodexHome, AuthFileName))) == identity;
    }

    private static bool IsUserSelectableCompatibleApiModelId(string model) =>
        // Require a filename-safe coding/reasoning family. /models can also advertise
        // image, speech, realtime and internal models that cannot run a Codex session.
        Regex.IsMatch(model, @"^gpt-(?:[5-9]|[1-9][0-9]+)(?:\.[0-9]+)*(?:-[a-z0-9]+)*$") &&
        !Regex.IsMatch(model, @"-(?:image|audio|realtime|transcribe|tts|embedding|search|chat|moderation)(?:-|$)");

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
                   .IsSupersetOf(bundledModels);
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
                "gpt-5.5", "gpt-5.6-luna", "gpt-5.6-terra", "gpt-6-astra", "gpt-6-luna", "gpt-6-sol", "gpt-6.1-sol"
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
                new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-6-sol", "unknown-upstream-model" });
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
                    new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-6-sol" },
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

            // A future upstream model must be usable without rebuilding this application.
            var future = "gpt-99.123-sol";
            RefreshManagedCompatibleModelCatalog(providerAccount, new[]
            {
                "gpt-5.6-sol", future, future, "gpt-99-audio", "gpt-image-1", "gpt-99/../../secret"
            });
            var futurePath = ManagedCompatibleModelCatalogPath(providerAccount.CodexHome);
            var lastGood = File.ReadAllText(futurePath);
            var futureNode = JsonNode.Parse(lastGood)?["models"]?.AsArray()
                .OfType<JsonObject>().Single(m => m["slug"]?.GetValue<string>() == future);
            if (!GetCompatibleApiAllowedModels(providerAccount).SetEquals(new[] { "gpt-5.6-sol", future }) ||
                futureNode?["context_window"]?.GetValue<int>() != 32768 ||
                futureNode["supported_reasoning_levels"]?.AsArray().Count != 0 ||
                futureNode["service_tiers"]?.AsArray().Count != 0 ||
                futureNode["input_modalities"]?.ToJsonString() != "[\"text\"]" ||
                RefreshManagedCompatibleModelCatalog(providerAccount, Array.Empty<string>()) ||
                RefreshManagedCompatibleModelCatalog(providerAccount, new[] { "gpt-image-1" }) ||
                RefreshManagedCompatibleModelCatalog(providerAccount, new[] { "gpt-6-sol" }, () => false) ||
                File.ReadAllText(futurePath) != lastGood)
                throw new InvalidOperationException("Future model fallback/last-good/stale refresh regression.");

            providerAccount.UseBundledCompatibleApiModelCatalog = true;
            _ = ManagedCompatibleModelCatalogPath(providerAccount);
            if (!GetCompatibleApiAllowedModels(providerAccount).Contains(future))
                throw new InvalidOperationException("Bundled catalog repair discarded a newly discovered model.");
            _ = ManagedCompatibleModelCatalogPath(providerAccount);
            if (!GetCompatibleApiAllowedModels(providerAccount).Contains(future))
                throw new InvalidOperationException("Bundled catalog completeness discarded an upstream model.");
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
