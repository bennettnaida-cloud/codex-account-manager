using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountManager;

internal sealed class ModelCatalogDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string DefaultModel { get; set; } = "gpt-5.6";
    public string DefaultReasoningEffort { get; set; } = "medium";
    public string CatalogSource { get; set; } = string.Empty;
    public string VerifiedAtUtc { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = [];
    public List<ModelCatalogPrice> Models { get; set; } = [];
}

internal sealed class ModelCatalogPrice
{
    public string Id { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
    public double InputUsdPerMillion { get; set; }
    public double CachedInputUsdPerMillion { get; set; }
    public double OutputUsdPerMillion { get; set; }
    public double CacheWriteMultiplier { get; set; } = 1.25D;
    public bool UsesLongContextPricing { get; set; } = true;
    public int LongContextThreshold { get; set; } = 272_000;
    public double LongInputMultiplier { get; set; } = 2D;
    public double LongOutputMultiplier { get; set; } = 1.5D;
}

internal sealed record ModelCatalogCheckResult(
    ModelCatalogDocument Previous,
    ModelCatalogDocument Current,
    IReadOnlyList<string> Changes);

internal static partial class ModelCatalogService
{
    internal const string ModelsUrl = "https://developers.openai.com/api/docs/models";
    internal const string CompareUrl = "https://developers.openai.com/api/docs/models/compare";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static string? _overridePath;
    private static ModelCatalogDocument? _current;

    internal static string DefaultModel => Current.DefaultModel;
    internal static string CanonicalDefaultModel
    {
        get
        {
            var catalog = Current;
            var configured = catalog.DefaultModel;
            return catalog.Models.FirstOrDefault(model =>
                       model.Id.Equals(configured, StringComparison.OrdinalIgnoreCase) ||
                       model.Aliases.Contains(configured, StringComparer.OrdinalIgnoreCase))
                   ?.Id ?? configured;
        }
    }
    internal static string DefaultReasoningEffort => Current.DefaultReasoningEffort;

    internal static ModelCatalogDocument Current
    {
        get
        {
            lock (Sync)
            {
                return _current ??= LoadCatalog();
            }
        }
    }

    internal static void Initialize(string dataRoot)
    {
        lock (Sync)
        {
            _overridePath = Path.Combine(Path.GetFullPath(dataRoot), "model-catalog.official.json");
            _current = LoadCatalog();
        }
    }

    internal static ModelCatalogPrice? Resolve(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        var normalized = NormalizeModelName(modelName);
        return Current.Models
            .OrderByDescending(model => model.Id.Length)
            .FirstOrDefault(model =>
                normalized.Contains(NormalizeModelName(model.Id), StringComparison.OrdinalIgnoreCase) ||
                model.Aliases.Any(alias =>
                    normalized.Equals(NormalizeModelName(alias), StringComparison.OrdinalIgnoreCase)));
    }

    internal static async Task<ModelCatalogCheckResult> CheckAndSaveOfficialAsync(
        string? proxyUri,
        CancellationToken cancellationToken = default)
    {
        var previous = Clone(Current);
        using var client = CreateClient(proxyUri);
        var indexText = await DownloadOfficialTextAsync(client, ModelsUrl, cancellationToken)
            .ConfigureAwait(false);
        var models = new List<ModelCatalogPrice>();
        foreach (var modelId in DiscoverTrackedModelIds(indexText, previous))
        {
            var configured = previous.Models.FirstOrDefault(model =>
                                 model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)) ??
                             new ModelCatalogPrice
                             {
                                 Id = modelId,
                                 CacheWriteMultiplier = 1D,
                                 UsesLongContextPricing = false
                             };
            var pageUrl = $"{ModelsUrl}/{Uri.EscapeDataString(modelId)}";
            var pageText = await DownloadOfficialTextAsync(client, pageUrl, cancellationToken)
                .ConfigureAwait(false);
            models.Add(ParseOfficialPrice(pageText, configured));
        }

        var defaultId = DetectDefaultModelId(indexText, models) ??
            throw new InvalidOperationException("官网模型目录中未找到明确的 Default 模型，已拒绝更新本地目录。");
        var defaultPrice = models.FirstOrDefault(model => ModelMatches(model, defaultId));
        if (defaultPrice is null)
        {
            var pageUrl = $"{ModelsUrl}/{Uri.EscapeDataString(defaultId)}";
            var pageText = await DownloadOfficialTextAsync(client, pageUrl, cancellationToken)
                .ConfigureAwait(false);
            defaultPrice = ParseOfficialPrice(pageText, new ModelCatalogPrice { Id = defaultId });
            models.Add(defaultPrice);
        }

        var defaultModel = defaultPrice.Aliases.FirstOrDefault(alias =>
            alias.Count(character => character == '-') == 1) ?? defaultPrice.Id;
        var current = new ModelCatalogDocument
        {
            SchemaVersion = 1,
            DefaultModel = defaultModel,
            DefaultReasoningEffort = previous.DefaultReasoningEffort,
            CatalogSource = "official",
            VerifiedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Sources = [ModelsUrl, CompareUrl],
            Models = OrderModelsForDisplay(models).ToList()
        };
        Validate(current);
        var changes = DescribeChanges(previous, current);
        SaveOverride(current);
        lock (Sync) _current = current;
        return new ModelCatalogCheckResult(previous, current, changes);
    }

    internal static void SaveManual(ModelCatalogDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var manual = Clone(catalog);
        manual.CatalogSource = "manual";
        Validate(manual);
        SaveOverride(manual);
        lock (Sync) _current = manual;
    }

    internal static void RestoreBundled()
    {
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(_overridePath) && File.Exists(_overridePath))
            {
                File.Delete(_overridePath);
            }
            _current = LoadCatalog();
        }
    }

    internal static ModelCatalogDocument CreateEditableCopy() => Clone(Current);

    internal static void ValidatePersistenceAndProxy()
    {
        string? originalOverridePath;
        ModelCatalogDocument originalCatalog;
        lock (Sync)
        {
            originalOverridePath = _overridePath;
            originalCatalog = Clone(_current ??= LoadCatalog());
        }

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-model-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            lock (Sync)
            {
                _overridePath = Path.Combine(temporaryRoot, "model-catalog.official.json");
                _current = Clone(originalCatalog);
            }

            var manual = CreateEditableCopy();
            manual.Models[0].InputUsdPerMillion += 0.125D;
            SaveManual(manual);
            var saved = JsonSerializer.Deserialize<ModelCatalogDocument>(
                File.ReadAllText(_overridePath!),
                JsonOptions);
            if (saved is null ||
                !saved.CatalogSource.Equals("manual", StringComparison.OrdinalIgnoreCase) ||
                saved.Models[0].InputUsdPerMillion != manual.Models[0].InputUsdPerMillion)
            {
                throw new InvalidOperationException("Manual model-catalog persistence self-test failed.");
            }

            RestoreBundled();
            if (File.Exists(_overridePath!) ||
                Current.CatalogSource.Equals("manual", StringComparison.OrdinalIgnoreCase) ||
                CanonicalDefaultModel != "gpt-5.6-sol" ||
                !Current.Models.Take(3).Select(model => model.Id).SequenceEqual(
                    ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"],
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Bundled model-catalog restore self-test failed.");
            }

            const string currentOfficialMarkdown = """
                # GPT-5.6 Sol
                Model ID: `gpt-5.6-sol`
                The `gpt-5.6` alias routes requests to GPT-5.6 Sol.
                ### Text tokens
                | Metric | Price | Unit |
                | --- | ---: | --- |
                | Input | $5 | 1M tokens |
                | Cached input | $0.5 | 1M tokens |
                | Output | $30 | 1M tokens |
                - Prompts with >272K input tokens are priced at 2x input and 1.5x output for the full request.
                - Cache writes are billed at 1.25x the uncached input token rate.
                """;
            var parsedOfficial = ParseOfficialPrice(currentOfficialMarkdown, new ModelCatalogPrice());
            if (parsedOfficial.Id != "gpt-5.6-sol" ||
                !parsedOfficial.Aliases.Contains("gpt-5.6", StringComparer.OrdinalIgnoreCase) ||
                parsedOfficial.InputUsdPerMillion != 5D ||
                parsedOfficial.CachedInputUsdPerMillion != 0.5D ||
                parsedOfficial.OutputUsdPerMillion != 30D ||
                parsedOfficial.CacheWriteMultiplier != 1.25D ||
                !parsedOfficial.UsesLongContextPricing ||
                parsedOfficial.LongContextThreshold != 272_000)
            {
                throw new InvalidOperationException("Current official model-page parser self-test failed.");
            }

            const string currentOfficialIndex =
                "If you're not sure where to start, use [GPT-5.6 Sol](/api/docs/models/gpt-5.6-sol), our flagship model.";
            if (DetectDefaultModelId(currentOfficialIndex, [parsedOfficial]) != "gpt-5.6-sol")
            {
                throw new InvalidOperationException("Current official default-model parser self-test failed.");
            }

            const string proWithoutCacheDiscountMarkdown = """
                # GPT-5.5 Pro
                Model ID: `gpt-5.5-pro`
                ### Text tokens
                | Metric | Price | Unit |
                | --- | ---: | --- |
                | Input | $30 | 1M tokens |
                | Output | $180 | 1M tokens |
                - GPT-5.5 Pro does not offer a cached input discount.
                """;
            var parsedPro = ParseOfficialPrice(
                proWithoutCacheDiscountMarkdown,
                new ModelCatalogPrice { Id = "gpt-5.5-pro" });
            if (parsedPro.InputUsdPerMillion != 30D ||
                parsedPro.CachedInputUsdPerMillion != 30D ||
                parsedPro.OutputUsdPerMillion != 180D ||
                parsedPro.UsesLongContextPricing)
            {
                throw new InvalidOperationException("Official no-cache-discount model parser self-test failed.");
            }

            const string discoveryIndex = """
                [GPT-5.6 Sol](/api/docs/models/gpt-5.6-sol.md)
                [GPT-5.5](/api/docs/models/gpt-5.5.md)
                [GPT-5.4 mini](/api/docs/models/gpt-5.4-mini.md)
                [GPT-5.3 Codex](/api/docs/models/gpt-5.3-codex.md)
                """;
            var discovered = DiscoverTrackedModelIds(discoveryIndex, new ModelCatalogDocument
            {
                Models = [parsedOfficial]
            });
            if (!discovered.Contains("gpt-5.6-sol", StringComparer.OrdinalIgnoreCase) ||
                !discovered.Contains("gpt-5.5", StringComparer.OrdinalIgnoreCase) ||
                !discovered.Contains("gpt-5.4-mini", StringComparer.OrdinalIgnoreCase) ||
                discovered.Contains("gpt-5.3-codex", StringComparer.OrdinalIgnoreCase) ||
                discovered.Contains("gpt-5.6", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Official tracked-model discovery self-test failed.");
            }

            using var validProxyClient = CreateClient("http://127.0.0.1:10808");
            try
            {
                using var invalidProxyClient = CreateClient("ftp://127.0.0.1:21");
                throw new InvalidOperationException("Invalid model-catalog proxy was accepted.");
            }
            catch (InvalidOperationException error) when (
                error.Message.Contains("代理地址无效", StringComparison.Ordinal))
            {
            }
        }
        finally
        {
            lock (Sync)
            {
                _overridePath = originalOverridePath;
                _current = originalCatalog;
            }
            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            catch
            {
                // The OS can remove a temporary self-test directory later if a scanner still holds it.
            }
        }
    }

    private static HttpClient CreateClient(string? proxyUri)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        if (!string.IsNullOrWhiteSpace(proxyUri))
        {
            if (!Uri.TryCreate(proxyUri, UriKind.Absolute, out var parsedProxy) ||
                parsedProxy.Scheme is not ("http" or "https") ||
                string.IsNullOrWhiteSpace(parsedProxy.Host) ||
                parsedProxy.Port is <= 0 or > 65535)
            {
                handler.Dispose();
                throw new InvalidOperationException("当前代理地址无效，无法用于官网检查。");
            }
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(parsedProxy);
        }

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 CodexAccountManager/1");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html", 0.8D));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    private static async Task<string> DownloadOfficialTextAsync(
        HttpClient client,
        string pageUrl,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var candidate in new[] { pageUrl + ".md", pageUrl })
        {
            try
            {
                using var response = await client.GetAsync(
                    candidate,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    lastError = new InvalidOperationException("OpenAI 官网拒绝了程序化请求（HTTP 403）。");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 2_000_000)
                {
                    throw new InvalidOperationException("官网返回内容超过安全大小限制。");
                }
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (text.Length is 0 or > 2_000_000)
                {
                    throw new InvalidOperationException("官网返回内容为空或超过安全大小限制。");
                }
                return text;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lastError = error;
            }
        }
        throw new InvalidOperationException(
            $"无法读取 OpenAI 官方页面 {pageUrl}：{lastError?.Message ?? "未知网络错误"}",
            lastError);
    }

    private static ModelCatalogPrice ParseOfficialPrice(string source, ModelCatalogPrice existing)
    {
        var text = NormalizeDocumentText(source);
        var match = OfficialMetricTablePriceRegex().Match(text);
        if (!match.Success)
        {
            match = OfficialPriceRegex().Match(text);
        }
        double inputPrice;
        double cachedInputPrice;
        double outputPrice;
        if (match.Success)
        {
            inputPrice = ParsePrice(match.Groups[1].Value);
            cachedInputPrice = ParsePrice(match.Groups[2].Value);
            outputPrice = ParsePrice(match.Groups[3].Value);
        }
        else
        {
            var noCacheDiscount = OfficialNoCacheDiscountPriceRegex().Match(text);
            if (!noCacheDiscount.Success)
            {
                throw new InvalidOperationException($"官网页面没有包含完整的 {existing.Id} 输入、缓存输入与输出价格。");
            }
            inputPrice = ParsePrice(noCacheDiscount.Groups[1].Value);
            cachedInputPrice = inputPrice;
            outputPrice = ParsePrice(noCacheDiscount.Groups[2].Value);
        }
        var idMatch = Regex.Match(
            text,
            @"(?i)Model ID:\s*`?(gpt-\d+(?:\.\d+)*(?:-[a-z0-9-]+)?)`?");
        if (!idMatch.Success)
        {
            idMatch = Regex.Match(text, @"(?i)\b(gpt-\d+(?:\.\d+)*(?:-[a-z0-9-]+)?)\b");
        }
        var id = string.IsNullOrWhiteSpace(existing.Id) && idMatch.Success
            ? idMatch.Groups[1].Value.ToLowerInvariant()
            : existing.Id;
        var aliases = existing.Aliases.ToList();
        var aliasMatch = Regex.Match(
            text,
            @"(?i)`?\b(gpt-\d+(?:\.\d+)*)\b`?\s+alias\s+routes\s+requests\s+to");
        if (aliasMatch.Success && !aliases.Contains(aliasMatch.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
        {
            aliases.Add(aliasMatch.Groups[1].Value.ToLowerInvariant());
        }
        return new ModelCatalogPrice
        {
            Id = id,
            Aliases = aliases,
            InputUsdPerMillion = inputPrice,
            CachedInputUsdPerMillion = cachedInputPrice,
            OutputUsdPerMillion = outputPrice,
            CacheWriteMultiplier = Regex.IsMatch(text, @"(?i)cache writes are billed at\s*1\.25x") ? 1.25D : 1D,
            UsesLongContextPricing = Regex.IsMatch(text, @"(?i)>\s*272K input tokens") &&
                                     Regex.IsMatch(text, @"(?i)2x input") &&
                                     Regex.IsMatch(text, @"(?i)1\.5x output"),
            LongContextThreshold = Regex.IsMatch(text, @"(?i)>\s*272K input tokens") ? 272_000 : existing.LongContextThreshold,
            LongInputMultiplier = Regex.IsMatch(text, @"(?i)2x input") ? 2D : existing.LongInputMultiplier,
            LongOutputMultiplier = Regex.IsMatch(text, @"(?i)1\.5x output") ? 1.5D : existing.LongOutputMultiplier
        };
    }

    private static IReadOnlyList<string> DiscoverTrackedModelIds(
        string indexSource,
        ModelCatalogDocument previous)
    {
        var discovered = previous.Models.Select(model => model.Id).ToList();
        foreach (Match match in Regex.Matches(
                     indexSource,
                     @"(?i)/api/docs/models/(gpt-(\d+(?:\.\d+)*)(?:-(?:sol|terra|luna|mini|nano|pro))?)(?:\.md)?(?=[)\s?#])"))
        {
            var versionText = match.Groups[2].Value.Contains('.', StringComparison.Ordinal)
                ? match.Groups[2].Value
                : match.Groups[2].Value + ".0";
            if (!Version.TryParse(versionText, out var version) ||
                version < new Version(5, 4))
            {
                continue;
            }
            var id = match.Groups[1].Value.ToLowerInvariant();
            if (!discovered.Contains(id, StringComparer.OrdinalIgnoreCase)) discovered.Add(id);
        }
        return discovered;
    }

    private static string? DetectDefaultModelId(string source, IReadOnlyList<ModelCatalogPrice> models)
    {
        var text = NormalizeDocumentText(source);
        foreach (var model in models)
        {
            var flexibleModelName = Regex.Escape(model.Id).Replace("-", "[- ]", StringComparison.Ordinal);
            if (Regex.IsMatch(
                    text,
                    $@"(?i)not sure where to start.{{0,120}}?{flexibleModelName}") ||
                Regex.IsMatch(
                    text,
                    $@"(?i){flexibleModelName}.{{0,100}}?Start here"))
            {
                return model.Id;
            }
        }
        foreach (var model in models)
        {
            var display = model.Id.Replace('-', ' ');
            if (text.Contains(display + " Default", StringComparison.OrdinalIgnoreCase)) return model.Id;
        }
        var match = Regex.Match(
            text,
            @"(?i)\b(gpt-\d+(?:\.\d+)*(?:-[a-z0-9-]+)?)\b.{0,100}?\bDefault\b");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string NormalizeDocumentText(string source)
    {
        var withoutTags = Regex.Replace(source, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(withoutTags), @"\s+", " ").Trim();
    }

    private static double ParsePrice(string value) =>
        double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static bool ModelMatches(ModelCatalogPrice model, string modelName) =>
        NormalizeModelName(model.Id).Equals(NormalizeModelName(modelName), StringComparison.OrdinalIgnoreCase) ||
        model.Aliases.Any(alias => NormalizeModelName(alias).Equals(
            NormalizeModelName(modelName), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeModelName(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

    private static ModelCatalogDocument LoadCatalog()
    {
        var candidates = new[]
        {
            _overridePath,
            Path.Combine(AppContext.BaseDirectory, "assets", "model-catalog.json"),
            Path.Combine(Environment.CurrentDirectory, "assets", "model-catalog.json")
        };
        foreach (var path in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var catalog = JsonSerializer.Deserialize<ModelCatalogDocument>(File.ReadAllText(path), JsonOptions);
                if (catalog is null) continue;
                if (string.IsNullOrWhiteSpace(catalog.CatalogSource))
                {
                    catalog.CatalogSource = path.Equals(_overridePath, StringComparison.OrdinalIgnoreCase)
                        ? "official"
                        : "bundled";
                }
                Validate(catalog);
                catalog.Models = OrderModelsForDisplay(catalog.Models).ToList();
                return catalog;
            }
            catch
            {
                // A corrupt user override must not prevent startup; the bundled catalog remains next.
            }
        }
        throw new InvalidOperationException("缺少有效的模型价格目录 assets/model-catalog.json。");
    }

    private static void Validate(ModelCatalogDocument catalog)
    {
        if (catalog.SchemaVersion != 1 || catalog.Models.Count == 0 ||
            string.IsNullOrWhiteSpace(catalog.DefaultModel) ||
            !catalog.Models.Any(model => ModelMatches(model, catalog.DefaultModel)))
        {
            throw new InvalidDataException("模型价格目录结构无效。");
        }
        foreach (var model in catalog.Models)
        {
            if (!Regex.IsMatch(model.Id, @"^gpt-[a-z0-9.-]+$", RegexOptions.IgnoreCase) ||
                model.InputUsdPerMillion <= 0 || model.InputUsdPerMillion > 1_000 ||
                model.CachedInputUsdPerMillion <= 0 || model.CachedInputUsdPerMillion > 1_000 ||
                model.OutputUsdPerMillion <= 0 || model.OutputUsdPerMillion > 1_000 ||
                model.CacheWriteMultiplier is < 1 or > 4 ||
                model.LongContextThreshold is < 1_000 or > 10_000_000 ||
                model.LongInputMultiplier is < 1 or > 4 ||
                model.LongOutputMultiplier is < 1 or > 4)
            {
                throw new InvalidDataException($"模型 {model.Id} 的价格目录数据无效。");
            }
        }
    }

    private static IReadOnlyList<string> DescribeChanges(
        ModelCatalogDocument previous,
        ModelCatalogDocument current)
    {
        var changes = new List<string>();
        if (!string.Equals(previous.DefaultModel, current.DefaultModel, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"默认模型：{previous.DefaultModel} -> {current.DefaultModel}");
        }
        foreach (var model in current.Models)
        {
            var old = previous.Models.FirstOrDefault(candidate =>
                candidate.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase));
            if (old is null)
            {
                changes.Add($"新增模型：{model.Id}");
                continue;
            }
            if (old.InputUsdPerMillion != model.InputUsdPerMillion ||
                old.CachedInputUsdPerMillion != model.CachedInputUsdPerMillion ||
                old.OutputUsdPerMillion != model.OutputUsdPerMillion)
            {
                changes.Add($"{model.Id}：输入 ${old.InputUsdPerMillion:g} -> ${model.InputUsdPerMillion:g}，" +
                    $"缓存 ${old.CachedInputUsdPerMillion:g} -> ${model.CachedInputUsdPerMillion:g}，" +
                    $"输出 ${old.OutputUsdPerMillion:g} -> ${model.OutputUsdPerMillion:g}");
            }
        }
        return changes;
    }

    private static void SaveOverride(ModelCatalogDocument catalog)
    {
        var path = _overridePath ?? Path.Combine(Environment.CurrentDirectory, "model-catalog.official.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(catalog, JsonOptions) + Environment.NewLine);
        File.Move(tempPath, path, overwrite: true);
    }

    private static ModelCatalogDocument Clone(ModelCatalogDocument catalog)
    {
        var json = JsonSerializer.Serialize(catalog, JsonOptions);
        return JsonSerializer.Deserialize<ModelCatalogDocument>(json, JsonOptions) ??
            throw new InvalidDataException("无法复制模型价格目录。");
    }

    private static IOrderedEnumerable<ModelCatalogPrice> OrderModelsForDisplay(
        IEnumerable<ModelCatalogPrice> models)
    {
        return models
            .OrderByDescending(GetModelVersion)
            .ThenBy(GetModelTierOrder)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static Version GetModelVersion(ModelCatalogPrice model)
    {
        var match = Regex.Match(model.Id, @"(?i)^gpt-(\d+(?:\.\d+)*)");
        if (!match.Success) return new Version(0, 0);
        var versionText = match.Groups[1].Value.Contains('.', StringComparison.Ordinal)
            ? match.Groups[1].Value
            : match.Groups[1].Value + ".0";
        return Version.TryParse(versionText, out var version) ? version : new Version(0, 0);
    }

    private static int GetModelTierOrder(ModelCatalogPrice model)
    {
        var id = model.Id.ToLowerInvariant();
        if (id.EndsWith("-sol", StringComparison.Ordinal) ||
            Regex.IsMatch(id, @"^gpt-\d+(?:\.\d+)*$")) return 0;
        if (id.EndsWith("-terra", StringComparison.Ordinal) ||
            id.EndsWith("-pro", StringComparison.Ordinal)) return 1;
        if (id.EndsWith("-luna", StringComparison.Ordinal) ||
            id.EndsWith("-mini", StringComparison.Ordinal)) return 2;
        if (id.EndsWith("-nano", StringComparison.Ordinal)) return 3;
        return 4;
    }

    [GeneratedRegex(@"(?i)Text tokens\s+Per 1M tokens\s+Input\s+\$([0-9]+(?:\.[0-9]+)?)\s+Cached input\s+\$([0-9]+(?:\.[0-9]+)?)\s+Output\s+\$([0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex OfficialPriceRegex();

    [GeneratedRegex(@"(?is)Text tokens.{0,600}?\|\s*Input\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|.{0,160}?\|\s*Cached input\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|.{0,160}?\|\s*Output\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|")]
    private static partial Regex OfficialMetricTablePriceRegex();

    [GeneratedRegex(@"(?is)Text tokens.{0,600}?\|\s*Input\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|.{0,220}?\|\s*Output\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|")]
    private static partial Regex OfficialNoCacheDiscountPriceRegex();
}

internal enum UsagePricingPolicy
{
    AccessTokenSub2ApiParity,
    CompatibleApiProvider
}

internal sealed record UsagePriceProfile(
    string DisplayName,
    double InputUsdPerMillion,
    double CachedInputUsdPerMillion,
    double OutputUsdPerMillion,
    double? LongInputUsdPerMillion = null,
    double? LongCachedInputUsdPerMillion = null,
    double? LongOutputUsdPerMillion = null,
    double? CacheWriteUsdPerMillion = null,
    double? LongCacheWriteUsdPerMillion = null,
    UsagePricingPolicy PricingPolicy = UsagePricingPolicy.AccessTokenSub2ApiParity,
    bool UsesLongContextPricing = false)
{
    public double GetInputRate(bool isLongContext) =>
        isLongContext && UsesLongContextPricing
            ? LongInputUsdPerMillion ?? InputUsdPerMillion
            : InputUsdPerMillion;

    public double GetCachedInputRate(bool isLongContext) =>
        isLongContext && UsesLongContextPricing
            ? LongCachedInputUsdPerMillion ?? CachedInputUsdPerMillion
            : CachedInputUsdPerMillion;

    public double GetCacheWriteRate(bool isLongContext) =>
        isLongContext && UsesLongContextPricing
            ? LongCacheWriteUsdPerMillion ?? CacheWriteUsdPerMillion ?? GetInputRate(true)
            : CacheWriteUsdPerMillion ?? InputUsdPerMillion;

    public double GetOutputRate(bool isLongContext) =>
        isLongContext && UsesLongContextPricing
            ? LongOutputUsdPerMillion ?? OutputUsdPerMillion
            : OutputUsdPerMillion;
}

internal static class UsagePricingCatalog
{
    internal static UsagePriceProfile Resolve(AccountRecord account)
    {
        if (!account.IsCompatibleApi)
        {
            return Resolve(ModelCatalogService.DefaultModel, LegacyGpt55(UsagePricingPolicy.AccessTokenSub2ApiParity));
        }
        return Resolve(account.ApiModel, LegacyGpt55(UsagePricingPolicy.CompatibleApiProvider));
    }

    internal static UsagePriceProfile Resolve(string? modelName, UsagePriceProfile fallback)
    {
        var official = ModelCatalogService.Resolve(modelName);
        if (official is not null) return FromOfficial(official, fallback.PricingPolicy);
        if (string.IsNullOrWhiteSpace(modelName)) return fallback;

        var model = modelName.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        var compatible = fallback.PricingPolicy == UsagePricingPolicy.CompatibleApiProvider;
        if (model.Contains("chat-latest", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyGpt55(fallback.PricingPolicy);
        }
        if (model.Contains("gpt-5.4-mini", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(compatible ? "gpt-5.4-mini 兼容 API 账单映射（按 sol）" : "gpt-5.4-mini Access Token（sub2api 实测按 sol）", 5D, 0.5D, 30D, PricingPolicy: fallback.PricingPolicy);
        }
        if (model.Contains("gpt-5.4-nano", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(compatible ? "gpt-5.4-nano 兼容 API 账单单价" : "gpt-5.4-nano Access Token（sub2api 平价口径）", 0.2D, 0.02D, 1.25D, PricingPolicy: fallback.PricingPolicy);
        }
        if (model.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(compatible ? "gpt-5.4 兼容 API 账单单价" : "gpt-5.4 Access Token（sub2api 平价口径）", 2.5D, 0.25D, 15D, 5D, 0.5D, 22.5D, PricingPolicy: fallback.PricingPolicy);
        }
        if (model.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(compatible ? "Codex 兼容 API 账单单价" : "Codex Access Token（sub2api 平价口径）", 1.75D, 0.175D, 14D, PricingPolicy: fallback.PricingPolicy);
        }
        return fallback;
    }

    private static UsagePriceProfile FromOfficial(ModelCatalogPrice price, UsagePricingPolicy policy)
    {
        var suffix = policy == UsagePricingPolicy.CompatibleApiProvider
            ? "兼容 API 官网单价"
            : "Access Token 官网单价";
        var cacheWrite = price.InputUsdPerMillion * price.CacheWriteMultiplier;
        return new UsagePriceProfile(
            $"{price.Id} {suffix}",
            price.InputUsdPerMillion,
            price.CachedInputUsdPerMillion,
            price.OutputUsdPerMillion,
            price.InputUsdPerMillion * price.LongInputMultiplier,
            price.CachedInputUsdPerMillion * price.LongInputMultiplier,
            price.OutputUsdPerMillion * price.LongOutputMultiplier,
            cacheWrite,
            cacheWrite * price.LongInputMultiplier,
            policy,
            UsesLongContextPricing: price.UsesLongContextPricing);
    }

    private static UsagePriceProfile LegacyGpt55(UsagePricingPolicy policy) =>
        new(
            policy == UsagePricingPolicy.CompatibleApiProvider
                ? "gpt-5.5 兼容 API 账单单价"
                : "gpt-5.5 Access Token 账单单价",
            5D,
            0.5D,
            30D,
            10D,
            1D,
            45D,
            PricingPolicy: policy);
}
