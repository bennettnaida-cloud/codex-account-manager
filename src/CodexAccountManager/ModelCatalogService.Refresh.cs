using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountManager;

internal static partial class ModelCatalogService
{
    internal const string PricingUrl = "https://developers.openai.com/api/docs/pricing";

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
        // Only the Standard table is valid for this editor. Never pick the Batch,
        // Flex, Fast, or credit rate with a similarly named model later in the page.
        var table = Regex.Match(source,
            @"(?im)^### Standard pricing data\s*\r?\n(?<rows>(?:[ \t]*\r?\n|[ \t]*\|[^\r\n]*(?:\r?\n|$))+)");
        if (!table.Success)
            throw new InvalidDataException("官网标准价格表格式已改变，已保留原有价格。");

        var models = new Dictionary<string, ModelCatalogPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in table.Groups["rows"].Value.Split('\n'))
        {
            var cells = line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length != 9) continue;
            var idMatch = Regex.Match(cells[0], @"^(gpt-(\d+(?:\.\d+)*)(?:-[a-z0-9-]+)?)(?:\s|$)");
            if (!idMatch.Success) continue;
            var number = idMatch.Groups[2].Value;
            if (!Version.TryParse(number.Contains('.') ? number : number + ".0", out var version) ||
                version < new Version(5, 4)) continue;
            var id = idMatch.Groups[1].Value;
            var old = previous.Models.FirstOrDefault(model => model.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var input = ReadRate(cells[1]);
            var cached = ReadOptionalRate(cells[2]) ?? input;
            var write = ReadOptionalRate(cells[3]) ?? input;
            var output = ReadRate(cells[4]);
            var longInput = ReadOptionalRate(cells[5]);
            var longCached = ReadOptionalRate(cells[6]);
            var longWrite = ReadOptionalRate(cells[7]);
            var longOutput = ReadOptionalRate(cells[8]);
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
        const string fixture = """
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
        var refreshed = ParseStandardPricingCatalog(fixture, previous);
        var astra = refreshed.Models.Single(model => model.Id == "gpt-6-astra");
        if (astra.InputUsdPerMillion != 10 || astra.CachedInputUsdPerMillion != 1 ||
            astra.OutputUsdPerMillion != 50 || astra.CacheWriteMultiplier != 1.25 ||
            !astra.UsesLongContextPricing || astra.LongContextThreshold != 272_000 ||
            astra.LongInputMultiplier != 2 || astra.LongOutputMultiplier != 1.5 ||
            refreshed.DefaultModel != previous.DefaultModel || previous.Models.Count != 1)
            throw new InvalidOperationException("Standard GPT-6 price/alias refresh self-test failed.");
        foreach (var invalid in new[]
                 {
                     fixture.Replace("### Standard pricing data", "### Unknown pricing data"),
                     fixture.Replace("| $10.00 | $1.00 |", "| invalid | $1.00 |"),
                     fixture.Replace("gpt-5.6-sol", "gpt-5.6-terra"),
                     fixture.Replace("| $20.00 | $2.00 | $25.00 |", "| $20.00 | $3.00 | $25.00 |")
                 })
        {
            try
            {
                ParseStandardPricingCatalog(invalid, previous);
                throw new InvalidOperationException("Incomplete or invalid pricing refresh was accepted.");
            }
            catch (InvalidDataException) { }
        }
        var manual = Clone(Current);
        manual.Models.RemoveAll(model => model.Id == "gpt-6-astra");
        manual.Models[0].InputUsdPerMillion = 123;
        MergeMissingBundledModels(manual, [Path.Combine(AppContext.BaseDirectory, "assets", "model-catalog.json")]);
        if (!manual.Models.Any(model => model.Id == "gpt-6-astra") || manual.Models[0].InputUsdPerMillion != 123)
            throw new InvalidOperationException("Old price caches must gain GPT-6 while preserving existing manual rates.");
    }
}
