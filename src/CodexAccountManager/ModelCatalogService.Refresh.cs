using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountManager;

internal static partial class ModelCatalogService
{
    internal const string PricingUrl = "https://developers.openai.com/api/docs/pricing";

    private sealed record StandardTableColumns(
        int ColumnCount,
        int InputIndex,
        int OutputIndex,
        int CachedInputIndex,
        int CacheWriteIndex,
        int LongInputIndex,
        int LongOutputIndex,
        int LongCachedInputIndex,
        int LongCacheWriteIndex);

    internal static void SetAutomaticPriceUpdates(bool enabled)
    {
        lock (Sync)
        {
            var catalog = Clone(Current);
            catalog.AutomaticPriceUpdatesEnabled = enabled;
            SaveOverride(catalog);
            _current = catalog;
        }
    }

    private static ModelCatalogDocument ParseStandardPricingCatalog(
        string source, ModelCatalogDocument previous)
    {
        // Only the Standard section is valid for this editor. Never pick Batch,
        // Flex, Fast, Ultrafast, or credit rates with similarly named models later in the page.
        // OpenAI's Standard section may contain both a long-context table (9 or 11 columns,
        // with 30m/12h cache-write columns) and a companion short-context-only 4-column table
        // (e.g. gpt-5.4-mini / gpt-5.4-nano). Parse columns by header name rather than fixed position.
        var table = Regex.Match(source,
            @"(?im)^### Standard pricing data\s*\r?\n(?<rows>(?:[ \t]*\r?\n|[ \t]*\|[^\r\n]*(?:\r?\n|$))+)");
        if (!table.Success)
            throw new InvalidDataException("官网标准价格表格式已改变，已保留原有价格。");

        var models = new Dictionary<string, ModelCatalogPrice>(StringComparer.OrdinalIgnoreCase);
        StandardTableColumns? currentColumns = null;
        var sawHeader = false;
        foreach (var rawLine in table.Groups["rows"].Value.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                currentColumns = null;
                continue;
            }
            if (!trimmed.StartsWith('|'))
            {
                continue;
            }

            var cells = trimmed.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length == 0 || IsMarkdownSeparatorRow(cells))
            {
                continue;
            }

            if (cells[0].Equals("Model", StringComparison.OrdinalIgnoreCase))
            {
                currentColumns = ParseStandardTableHeader(cells);
                sawHeader = true;
                continue;
            }

            if (currentColumns is null)
            {
                if (cells.Length == 9)
                {
                    // Legacy headerless fallback matching pre-2026-11-20 9-column layout.
                    currentColumns = new StandardTableColumns(9, 1, 4, 2, 3, 5, 8, 6, 7);
                }
                else
                {
                    continue;
                }
            }

            if (cells.Length != currentColumns.ColumnCount) continue;
            var idMatch = Regex.Match(cells[0], @"^(gpt-(\d+(?:\.\d+)*)(?:-[a-z0-9-]+)?)(?:\s|$)");
            if (!idMatch.Success) continue;
            var number = idMatch.Groups[2].Value;
            if (!Version.TryParse(number.Contains('.') ? number : number + ".0", out var version) ||
                version < new Version(5, 4)) continue;
            var id = idMatch.Groups[1].Value;
            var old = previous.Models.FirstOrDefault(model => model.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var input = ReadRate(cells[currentColumns.InputIndex]);
            var output = ReadRate(cells[currentColumns.OutputIndex]);
            var cached = currentColumns.CachedInputIndex >= 0
                ? ReadOptionalRate(cells[currentColumns.CachedInputIndex]) ?? input
                : input;
            var write = currentColumns.CacheWriteIndex >= 0
                ? ReadOptionalRate(cells[currentColumns.CacheWriteIndex]) ?? input
                : input;
            var longInput = currentColumns.LongInputIndex >= 0
                ? ReadOptionalRate(cells[currentColumns.LongInputIndex])
                : null;
            var longOutput = currentColumns.LongOutputIndex >= 0
                ? ReadOptionalRate(cells[currentColumns.LongOutputIndex])
                : null;
            var longCached = currentColumns.LongCachedInputIndex >= 0
                ? ReadOptionalRate(cells[currentColumns.LongCachedInputIndex])
                : null;
            var longWrite = currentColumns.LongCacheWriteIndex >= 0
                ? ReadOptionalRate(cells[currentColumns.LongCacheWriteIndex])
                : null;
            if (longInput.HasValue != longOutput.HasValue ||
                (longCached.HasValue && (!longInput.HasValue || Math.Abs(longCached.Value / cached - longInput.Value / input) > 0.001)) ||
                (longWrite.HasValue && (!longInput.HasValue || Math.Abs(longWrite.Value / write - longInput.Value / input) > 0.001)))
                throw new InvalidDataException($"{id} 的官网长上下文价格无法完整表示，已保留原有价格。");
            if (!models.TryAdd(id, new ModelCatalogPrice
                {
                    Id = id,
                    Aliases = old?.Aliases.ToList() ?? [],
                    InputUsdPerMillion = input,
                    CachedInputUsdPerMillion = cached,
                    OutputUsdPerMillion = output,
                    CacheWriteMultiplier = write / input,
                    UsesLongContextPricing = longInput.HasValue,
                    LongContextThreshold = 272_000,
                    LongInputMultiplier = longInput / input ?? 2D,
                    LongOutputMultiplier = longOutput / output ?? 1.5D
                }))
                throw new InvalidDataException($"官网标准价格表包含重复的 {id}，已保留原有价格。");
        }

        if (!sawHeader && models.Count == 0)
            throw new InvalidDataException("官网标准价格表格式已改变，已保留原有价格。");
        if (!models.ContainsKey("gpt-6-astra") ||
            previous.Models.Any(model => !models.ContainsKey(model.Id)))
            throw new InvalidDataException("官网价格表不完整，已保留原有价格。");
        var current = Clone(previous);
        current.Models = OrderModelsForDisplay(models.Values).ToList();
        current.CatalogSource = "official";
        current.VerifiedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        current.Sources = [PricingUrl];
        // Refreshing prices must not silently select the new flagship for every account.
        Validate(current);
        return current;
    }

    private static bool IsMarkdownSeparatorRow(string[] cells) =>
        cells.All(cell => Regex.IsMatch(cell, @"^:?-{2,}:?$"));

    private static StandardTableColumns ParseStandardTableHeader(string[] headerCells)
    {
        var inputIndex = -1;
        var outputIndex = -1;
        var cachedInputIndex = -1;
        var cacheWriteIndex = -1;
        var longInputIndex = -1;
        var longOutputIndex = -1;
        var longCachedInputIndex = -1;
        var longCacheWriteIndex = -1;

        for (var i = 1; i < headerCells.Length; i++)
        {
            var normalized = Regex.Replace(headerCells[i].Trim().ToLowerInvariant(), @"\s+", " ");
            switch (normalized)
            {
                case "short context input":
                case "input":
                    inputIndex = i;
                    break;
                case "short context output":
                case "output":
                    outputIndex = i;
                    break;
                case "short context cached input":
                case "cached input":
                    cachedInputIndex = i;
                    break;
                case "short context 30m write":
                case "short context cache writes":
                case "short context cache write":
                case "30m write":
                case "cache writes":
                case "cache write":
                    // Prefer 30m standard cache write (1.25x) when multiple retention columns exist.
                    if (cacheWriteIndex < 0 || normalized.Contains("30m", StringComparison.Ordinal))
                    {
                        cacheWriteIndex = i;
                    }
                    break;
                case "long context input":
                    longInputIndex = i;
                    break;
                case "long context output":
                    longOutputIndex = i;
                    break;
                case "long context cached input":
                    longCachedInputIndex = i;
                    break;
                case "long context 30m write":
                case "long context cache writes":
                case "long context cache write":
                    if (longCacheWriteIndex < 0 || normalized.Contains("30m", StringComparison.Ordinal))
                    {
                        longCacheWriteIndex = i;
                    }
                    break;
            }
        }

        if (inputIndex < 0 || outputIndex < 0)
        {
            throw new InvalidDataException("官网标准价格表列头已改变，已保留原有价格。");
        }

        return new StandardTableColumns(
            headerCells.Length,
            inputIndex,
            outputIndex,
            cachedInputIndex,
            cacheWriteIndex,
            longInputIndex,
            longOutputIndex,
            longCachedInputIndex,
            longCacheWriteIndex);
    }

    private static double? ReadOptionalRate(string text) => text == "-" ? null : ReadRate(text);

    private static double ReadRate(string text)
    {
        if (!Regex.IsMatch(text, @"^\$\d+(?:\.\d+)?$") ||
            !double.TryParse(text[1..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value <= 0)
            throw new InvalidDataException("官网价格字段无效，已保留原有价格。");
        return value;
    }

    private static void MergeMissingBundledModels(ModelCatalogDocument catalog, IEnumerable<string?> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var bundled = JsonSerializer.Deserialize<ModelCatalogDocument>(File.ReadAllText(path!), JsonOptions);
                if (bundled == null) continue;
                Validate(bundled);
                var known = catalog.Models.Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                catalog.Models.AddRange(bundled.Models.Where(model => known.Add(model.Id)));
                return;
            }
            catch
            {
                // Keep the valid override if a bundled fallback cannot be read.
            }
        }
    }

    private static void ValidateStandardPricingRefresh()
    {
        // 1. Legacy 9-column Standard table compatibility.
        const string legacyFixture = """
            ### Standard pricing data

            | Model | Short context input | Short context cached input | Short context cache writes | Short context output | Long context input | Long context cached input | Long context cache writes | Long context output |
            | --- | --- | --- | --- | --- | --- | --- | --- | --- |
            | gpt-6-astra | $10.00 | $1.00 | $12.50 | $50.00 | $20.00 | $2.00 | $25.00 | $75.00 |
            | gpt-5.6-sol | $4.00 | $0.40 | $5.00 | $20.00 | $8.00 | $0.80 | $10.00 | $30.00 |

            ### Batch pricing data
            | gpt-6-astra | $5.00 | $0.50 | $6.25 | $25.00 | $10.00 | $1.00 | $12.50 | $37.50 |
            """;
        var previous = new ModelCatalogDocument
        {
            DefaultModel = "gpt-5.6",
            Models = [new ModelCatalogPrice { Id = "gpt-5.6-sol", Aliases = ["gpt-5.6"] }]
        };
        var refreshed = ParseStandardPricingCatalog(legacyFixture, previous);
        var astra = refreshed.Models.Single(model => model.Id == "gpt-6-astra");
        if (astra.InputUsdPerMillion != 10 || astra.CachedInputUsdPerMillion != 1 ||
            astra.OutputUsdPerMillion != 50 || astra.CacheWriteMultiplier != 1.25 ||
            !astra.UsesLongContextPricing || astra.LongContextThreshold != 272_000 ||
            astra.LongInputMultiplier != 2 || astra.LongOutputMultiplier != 1.5 ||
            refreshed.DefaultModel != previous.DefaultModel || previous.Models.Count != 1)
            throw new InvalidOperationException("Legacy 9-column GPT-6 price/alias refresh self-test failed.");

        // 2. Current 2026-11-20 dual-table format (11-column with 30m/12h writes + 4-column short-context table).
        const string modernFixture = """
            ### Standard pricing data

            | Model | Short context input | Short context output | Short context cached input | Short context 30m write | Short context 12h write | Long context input | Long context output | Long context cached input | Long context 30m write | Long context 12h write |
            | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
            | gpt-6.2-astra | $8.00 | $40.00 | $0.40 | $10.00 | $20.00 | $16.00 | $60.00 | $0.80 | $20.00 | $40.00 |
            | gpt-6.2-sol | $2.00 | $10.00 | $0.10 | $2.50 | $5.00 | $4.00 | $15.00 | $0.20 | $5.00 | $10.00 |
            | gpt-6-astra | $10.00 | $50.00 | $1.00 | $12.50 | $25.00 | $20.00 | $75.00 | $2.00 | $25.00 | $50.00 |
            | gpt-6.1-sol | $2.00 | $10.00 | $0.10 | $2.50 | $5.00 | $4.00 | $15.00 | $0.20 | $5.00 | $10.00 |
            | gpt-6.1-terra | $1.00 | $5.00 | $0.05 | $1.25 | $2.50 | $2.00 | $7.50 | $0.10 | $2.50 | $5.00 |
            | gpt-5.6-sol | $4.00 | $20.00 | $0.40 | $5.00 | $10.00 | $8.00 | $30.00 | $0.80 | $10.00 | $20.00 |
            | gpt-5.5-pro (<272K context length) | $30.00 | $180.00 | - | - | - | $60.00 | $270.00 | - | - | - |

            | Model | Input | Output | Cached input |
            | --- | --- | --- | --- |
            | gpt-5.4-mini | $0.75 | $4.50 | $0.075 |
            | gpt-5.4-nano | $0.20 | $1.25 | $0.02 |
            | gpt-5.2 | $1.75 | $14.00 | $0.175 |

            Batch

            ### Batch pricing data

            | Model | Short context input | Short context output | Short context cached input | Short context 30m write | Short context 12h write | Long context input | Long context output | Long context cached input | Long context 30m write | Long context 12h write |
            | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
            | gpt-6.2-astra | $4.00 | $20.00 | $0.20 | $5.00 | $10.00 | $8.00 | $30.00 | $0.40 | $10.00 | $20.00 |
            """;
        var modernPrevious = new ModelCatalogDocument
        {
            DefaultModel = "gpt-6.1-sol",
            Models =
            [
                new ModelCatalogPrice { Id = "gpt-6.1-sol" },
                new ModelCatalogPrice { Id = "gpt-5.6-sol", Aliases = ["gpt-5.6"] },
                new ModelCatalogPrice { Id = "gpt-5.4-mini" }
            ]
        };
        var modernRefreshed = ParseStandardPricingCatalog(modernFixture, modernPrevious);
        var astra62 = modernRefreshed.Models.Single(model => model.Id == "gpt-6.2-astra");
        var sol62 = modernRefreshed.Models.Single(model => model.Id == "gpt-6.2-sol");
        var terra61 = modernRefreshed.Models.Single(model => model.Id == "gpt-6.1-terra");
        var pro55 = modernRefreshed.Models.Single(model => model.Id == "gpt-5.5-pro");
        var mini54 = modernRefreshed.Models.Single(model => model.Id == "gpt-5.4-mini");
        if (astra62.InputUsdPerMillion != 8 || astra62.CachedInputUsdPerMillion != 0.4 ||
            astra62.OutputUsdPerMillion != 40 || Math.Abs(astra62.CacheWriteMultiplier - 1.25) > 0.0001 ||
            !astra62.UsesLongContextPricing || astra62.LongInputMultiplier != 2 || astra62.LongOutputMultiplier != 1.5 ||
            sol62.InputUsdPerMillion != 2 || sol62.CachedInputUsdPerMillion != 0.1 || sol62.OutputUsdPerMillion != 10 ||
            terra61.InputUsdPerMillion != 1 || terra61.CachedInputUsdPerMillion != 0.05 || terra61.OutputUsdPerMillion != 5 ||
            pro55.CachedInputUsdPerMillion != 30 || pro55.CacheWriteMultiplier != 1 || !pro55.UsesLongContextPricing ||
            mini54.InputUsdPerMillion != 0.75 || mini54.CachedInputUsdPerMillion != 0.075 ||
            mini54.OutputUsdPerMillion != 4.5 || mini54.UsesLongContextPricing ||
            modernRefreshed.Models.Any(model => model.Id == "gpt-5.2") ||
            modernRefreshed.DefaultModel != modernPrevious.DefaultModel)
        {
            throw new InvalidOperationException("Modern 11-column + 4-column official pricing refresh self-test failed.");
        }

        foreach (var invalid in new[]
                 {
                     legacyFixture.Replace("### Standard pricing data", "### Unknown pricing data"),
                     legacyFixture.Replace("| $10.00 | $1.00 |", "| invalid | $1.00 |"),
                     legacyFixture.Replace("gpt-5.6-sol", "gpt-5.6-terra"),
                     legacyFixture.Replace("| $20.00 | $2.00 | $25.00 |", "| $20.00 | $3.00 | $25.00 |"),
                     modernFixture.Replace("Short context output", "Unknown metric"),
                     modernFixture.Replace("| $16.00 | $60.00 | $0.80 |", "| $16.00 | $60.00 | $1.20 |")
                 })
        {
            try
            {
                ParseStandardPricingCatalog(invalid, invalid.Contains("gpt-6.2-astra", StringComparison.Ordinal) ? modernPrevious : previous);
                throw new InvalidOperationException("Incomplete or invalid pricing refresh was accepted.");
            }
            catch (InvalidDataException) { }
        }
        var manual = Clone(Current);
        manual.Models.RemoveAll(model => model.Id == "gpt-6.2-astra");
        manual.Models[0].InputUsdPerMillion = 123;
        MergeMissingBundledModels(manual, [Path.Combine(AppContext.BaseDirectory, "assets", "model-catalog.json")]);
        if (!manual.Models.Any(model => model.Id == "gpt-6.2-astra") || manual.Models[0].InputUsdPerMillion != 123)
            throw new InvalidOperationException("Old price caches must gain GPT-6.2 while preserving existing manual rates.");
    }
}
