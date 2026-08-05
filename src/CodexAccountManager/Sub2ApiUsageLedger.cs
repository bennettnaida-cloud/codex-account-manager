using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal sealed class Sub2ApiUsageRecord
{
    public string EventId { get; set; } = string.Empty;
    public string AccountKey { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; set; }
    public string Model { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public decimal BilledCostUsd { get; set; }

    public bool IsSearch => Endpoint.Equals(
        "/v1/alpha/search",
        StringComparison.OrdinalIgnoreCase);

    public UsageEvent ToUsageEvent(string resolvedAccountName) => new()
    {
        AccountName = resolvedAccountName,
        Model = Model,
        TimestampUtc = CompletedAtUtc.ToUniversalTime(),
        // A CSV recovery row represents real user traffic, not a synthetic quota probe.
        // Keeping it Natural lets the passive monitor include the recovered spend.
        Source = UsageEventSource.Natural,
        EquivalentCostOverrideUsd = decimal.ToDouble(BilledCostUsd),
        InputTokens = InputTokens,
        CachedInputTokens = CachedInputTokens,
        CacheWriteTokens = CacheWriteTokens,
        OutputTokens = OutputTokens,
        TotalTokens = TotalTokens
    };
}

internal sealed record Sub2ApiUsageImportResult(
    int CsvRows,
    int AlreadyImportedRows,
    int MatchedLocalRows,
    int AddedRows,
    decimal AddedCostUsd,
    bool Persisted,
    string LedgerPath,
    IReadOnlyList<Sub2ApiUsageRecord> AddedRecords);

/// <summary>
/// Durable, idempotent recovery ledger for provider rows that no longer have a local
/// JSONL/SQLite counterpart. Natural usage always wins if it later becomes available.
/// </summary>
internal sealed class Sub2ApiUsageLedger
{
    internal const string FileName = "sub2api-usage-ledger.json";
    internal static readonly TimeSpan MatchTolerance = TimeSpan.FromSeconds(45);
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;

    public Sub2ApiUsageLedger(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Manager root path is required.", nameof(rootPath));
        }
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(rootPath), FileName);
    }

    public string Path => _path;

    public Sub2ApiUsageImportResult Import(
        string csvPath,
        AccountRecord account,
        IReadOnlyList<UsageEvent> reconciledLocalEvents,
        bool persist,
        DateTimeOffset? fromUtc = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(reconciledLocalEvents);
        if (string.IsNullOrWhiteSpace(account.Name) || string.IsNullOrWhiteSpace(account.CodexHome))
        {
            throw new ArgumentException("The target account must have a name and CODEX_HOME.", nameof(account));
        }

        var rows = FilterFrom(ReadCsv(csvPath), fromUtc);
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        var importedRows = rows
            .Select(row => row.ToRecord(accountKey, account.Name))
            .ToList();

        lock (_gate)
        {
            var existing = LoadUnsafe(throwOnDamage: true);
            var existingIds = existing
                .Select(item => item.EventId)
                .ToHashSet(StringComparer.Ordinal);
            var usedLocalIndices = new HashSet<int>();

            // Existing ledger entries reserve their natural counterpart first. This keeps
            // a single local event from suppressing a second, genuinely distinct CSV row.
            MatchAgainstLocal(existing, reconciledLocalEvents, usedLocalIndices);
            var matchedCsvIds = MatchAgainstLocal(
                importedRows,
                reconciledLocalEvents,
                usedLocalIndices);

            var alreadyImported = 0;
            var matchedLocal = 0;
            var additions = new List<Sub2ApiUsageRecord>();
            foreach (var row in importedRows)
            {
                if (existingIds.Contains(row.EventId))
                {
                    alreadyImported++;
                    continue;
                }
                if (matchedCsvIds.Contains(row.EventId))
                {
                    matchedLocal++;
                    continue;
                }
                additions.Add(row);
            }

            if (persist && additions.Count > 0)
            {
                existing.AddRange(additions);
                SaveUnsafe(existing);
            }

            return new Sub2ApiUsageImportResult(
                importedRows.Count,
                alreadyImported,
                matchedLocal,
                additions.Count,
                additions.Sum(item => item.BilledCostUsd),
                persist,
                _path,
                additions);
        }
    }

    public IReadOnlyList<UsageEvent> LoadMissingUsageEvents(
        IReadOnlyList<AccountRecord> accounts,
        DateTimeOffset sinceUtc,
        IReadOnlyList<UsageEvent> reconciledLocalEvents)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(reconciledLocalEvents);
        lock (_gate)
        {
            var records = LoadUnsafe(throwOnDamage: false)
                .Where(item => item.CompletedAtUtc >= sinceUtc.ToUniversalTime())
                .OrderBy(item => item.CompletedAtUtc)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToList();
            if (records.Count == 0)
            {
                return [];
            }

            // If JSONL/SQLite later recovers an imported event, the natural copy becomes
            // authoritative and the ledger copy disappears from this report automatically.
            var naturallyRecovered = MatchAgainstLocal(
                records,
                reconciledLocalEvents,
                new HashSet<int>());
            var accountsByKey = accounts
                .Where(account => !string.IsNullOrWhiteSpace(account.CodexHome))
                .GroupBy(QuotaAccountIdentity.CreateKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var accountsByName = accounts
                .Where(account => !string.IsNullOrWhiteSpace(account.Name))
                .GroupBy(account => account.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<UsageEvent>(records.Count - naturallyRecovered.Count);
            foreach (var record in records)
            {
                if (naturallyRecovered.Contains(record.EventId))
                {
                    continue;
                }
                var resolvedName = accountsByKey.TryGetValue(record.AccountKey, out var identityMatch)
                    ? identityMatch.Name
                    : accountsByName.TryGetValue(record.AccountName, out var nameMatch)
                        ? nameMatch.Name
                        : record.AccountName;
                result.Add(record.ToUsageEvent(resolvedName));
            }
            return result;
        }
    }

    internal static IReadOnlyList<Sub2ApiUsageRecord> ReadCsvForImport(
        string csvPath,
        AccountRecord account,
        DateTimeOffset? fromUtc = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        var key = QuotaAccountIdentity.CreateKey(account);
        return FilterFrom(ReadCsv(csvPath), fromUtc)
            .Select(row => row.ToRecord(key, account.Name))
            .ToList();
    }

    private static List<Sub2ApiCsvRow> FilterFrom(
        IReadOnlyList<Sub2ApiCsvRow> rows,
        DateTimeOffset? fromUtc)
    {
        if (!fromUtc.HasValue)
        {
            return rows.ToList();
        }
        var inclusiveFromUtc = fromUtc.Value.ToUniversalTime();
        return rows
            .Where(row => row.CompletedAtUtc >= inclusiveFromUtc)
            .ToList();
    }

    private static HashSet<string> MatchAgainstLocal(
        IReadOnlyList<Sub2ApiUsageRecord> records,
        IReadOnlyList<UsageEvent> localEvents,
        HashSet<int> usedLocalIndices)
    {
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records
                     .OrderBy(item => item.CompletedAtUtc)
                     .ThenBy(item => item.EventId, StringComparer.Ordinal))
        {
            var bestIndex = -1;
            var bestDifference = TimeSpan.MaxValue;
            for (var index = 0; index < localEvents.Count; index++)
            {
                if (usedLocalIndices.Contains(index) ||
                    !Matches(record, localEvents[index], out var timestampDifference) ||
                    timestampDifference >= bestDifference)
                {
                    continue;
                }
                bestIndex = index;
                bestDifference = timestampDifference;
            }
            if (bestIndex >= 0)
            {
                usedLocalIndices.Add(bestIndex);
                matched.Add(record.EventId);
            }
        }
        return matched;
    }

    private static bool Matches(
        Sub2ApiUsageRecord record,
        UsageEvent local,
        out TimeSpan timestampDifference)
    {
        var localTimestamp = local.ResponseUsageResponseTimestampUtc ?? local.TimestampUtc;
        timestampDifference = (record.CompletedAtUtc.ToUniversalTime() -
                               localTimestamp.ToUniversalTime()).Duration();
        if (timestampDifference > MatchTolerance ||
            local.Source == UsageEventSource.OfficialSnapshot ||
            !ModelsMatch(record.Model, local.Model))
        {
            return false;
        }

        if (record.IsSearch)
        {
            return record.TotalTokens == 0L &&
                local.TotalTokens == 0L &&
                local.InputTokens == 0L &&
                local.OutputTokens == 0L &&
                local.EquivalentCostOverrideUsd is double fixedCost &&
                Math.Abs(fixedCost - decimal.ToDouble(record.BilledCostUsd)) < 0.000_000_001D;
        }

        return local.InputTokens == record.InputTokens &&
            local.CachedInputTokens == record.CachedInputTokens &&
            // A missing local cache-write field can safely match a provider-reported zero.
            // A non-zero provider value must remain exact.
            (local.CacheWriteTokens == record.CacheWriteTokens ||
             (record.CacheWriteTokens == 0L && !local.CacheWriteTokens.HasValue)) &&
            local.OutputTokens == record.OutputTokens &&
            local.TotalTokens == record.TotalTokens;
    }

    private static bool ModelsMatch(string providerModel, string? localModel)
    {
        if (string.IsNullOrWhiteSpace(providerModel) || string.IsNullOrWhiteSpace(localModel))
        {
            return true;
        }
        return providerModel.Trim().Equals(localModel.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static List<Sub2ApiCsvRow> ReadCsv(string csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            throw new ArgumentException("CSV path is required.", nameof(csvPath));
        }
        var fullPath = System.IO.Path.GetFullPath(csvPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("sub2api usage CSV was not found.", fullPath);
        }

        using var reader = new StreamReader(
            new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var records = ReadRfc4180Records(reader).ToList();
        if (records.Count == 0)
        {
            throw new InvalidDataException("sub2api usage CSV is empty.");
        }

        var headers = records[0]
            .Select((name, index) => new
            {
                Name = name.Trim().TrimStart('\uFEFF'),
                Index = index
            })
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
        int Required(string name) => headers.TryGetValue(name, out var index)
            ? index
            : throw new InvalidDataException($"sub2api usage CSV is missing column '{name}'.");

        var timeColumn = Required("Time");
        var modelColumn = Required("Model");
        var endpointColumn = Required("Inbound Endpoint");
        var inputColumn = Required("Input Tokens");
        var outputColumn = Required("Output Tokens");
        var cacheReadColumn = Required("Cache Read Tokens");
        var cacheCreationColumn = Required("Cache Creation Tokens");
        var billedCostColumn = Required("Billed Cost");
        var duplicateOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<Sub2ApiCsvRow>(records.Count - 1);

        for (var rowIndex = 1; rowIndex < records.Count; rowIndex++)
        {
            var fields = records[rowIndex];
            if (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0]))
            {
                continue;
            }
            string Field(int index, string name)
            {
                if (index < 0)
                {
                    return string.Empty;
                }
                if (index >= fields.Count)
                {
                    throw new InvalidDataException(
                        $"sub2api usage CSV row {rowIndex + 1} is missing column '{name}'.");
                }
                return fields[index];
            }
            long NonNegativeLong(int index, string name)
            {
                var value = Field(index, name).Trim();
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed < 0L)
                {
                    throw new InvalidDataException(
                        $"sub2api usage CSV row {rowIndex + 1} has invalid {name}: '{value}'.");
                }
                return parsed;
            }

            var rawTime = Field(timeColumn, "Time").Trim();
            if (!DateTimeOffset.TryParse(
                    rawTime,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                    out var completedAt))
            {
                throw new InvalidDataException(
                    $"sub2api usage CSV row {rowIndex + 1} has invalid Time: '{rawTime}'.");
            }
            var model = Field(modelColumn, "Model").Trim();
            var endpoint = Field(endpointColumn, "Inbound Endpoint").Trim();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidDataException(
                    $"sub2api usage CSV row {rowIndex + 1} has an empty Inbound Endpoint.");
            }
            var regularInput = NonNegativeLong(inputColumn, "Input Tokens");
            var cachedInput = NonNegativeLong(cacheReadColumn, "Cache Read Tokens");
            var cacheWrite = NonNegativeLong(cacheCreationColumn, "Cache Creation Tokens");
            var output = NonNegativeLong(outputColumn, "Output Tokens");
            long totalInput;
            long totalTokens;
            try
            {
                totalInput = checked(regularInput + cachedInput + cacheWrite);
                totalTokens = checked(totalInput + output);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException(
                    $"sub2api usage CSV row {rowIndex + 1} token total overflows Int64.",
                    ex);
            }
            var rawCost = Field(billedCostColumn, "Billed Cost").Trim();
            if (!decimal.TryParse(rawCost, NumberStyles.Number, CultureInfo.InvariantCulture, out var billedCost) ||
                billedCost < 0M)
            {
                throw new InvalidDataException(
                    $"sub2api usage CSV row {rowIndex + 1} has invalid Billed Cost: '{rawCost}'.");
            }

            var canonical = string.Join("\n",
                completedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                model.ToLowerInvariant(),
                endpoint.ToLowerInvariant(),
                regularInput.ToString(CultureInfo.InvariantCulture),
                cachedInput.ToString(CultureInfo.InvariantCulture),
                cacheWrite.ToString(CultureInfo.InvariantCulture),
                output.ToString(CultureInfo.InvariantCulture),
                billedCost.ToString("G29", CultureInfo.InvariantCulture));
            var occurrence = duplicateOrdinals.TryGetValue(canonical, out var previous)
                ? previous + 1
                : 1;
            duplicateOrdinals[canonical] = occurrence;
            var eventId = "sub2api:v1:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "\noccurrence=" + occurrence)));
            result.Add(new Sub2ApiCsvRow(
                eventId,
                completedAt.ToUniversalTime(),
                model,
                endpoint,
                totalInput,
                cachedInput,
                cacheWrite,
                output,
                totalTokens,
                billedCost));
        }
        return result;
    }

    private static IEnumerable<IReadOnlyList<string>> ReadRfc4180Records(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                if (inQuotes)
                {
                    throw new InvalidDataException("sub2api usage CSV ends inside a quoted field.");
                }
                if (fieldStarted || field.Length > 0 || row.Count > 0)
                {
                    row.Add(field.ToString());
                    yield return row;
                }
                yield break;
            }

            var character = (char)next;
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(character);
                }
                fieldStarted = true;
                continue;
            }

            if (character == '"' && !fieldStarted && field.Length == 0)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }
                row.Add(field.ToString());
                yield return row;
                row = [];
                field.Clear();
                fieldStarted = false;
            }
            else
            {
                field.Append(character);
                fieldStarted = true;
            }
        }
    }

    private List<Sub2ApiUsageRecord> LoadUnsafe(bool throwOnDamage)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }
            var file = JsonSerializer.Deserialize<Sub2ApiUsageFile>(File.ReadAllText(_path), JsonOptions) ??
                       throw new InvalidDataException("sub2api usage ledger root is empty.");
            if (file.SchemaVersion != SchemaVersion || file.Events == null)
            {
                throw new InvalidDataException(
                    $"Unsupported sub2api usage ledger schema {file.SchemaVersion}.");
            }
            return file.Events
                .Where(item => !string.IsNullOrWhiteSpace(item.EventId))
                .GroupBy(item => item.EventId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }
        catch (Exception ex) when (throwOnDamage)
        {
            throw new InvalidOperationException(
                "sub2api 历史用量账本已损坏，已停止导入以避免覆盖原数据。",
                ex);
        }
        catch
        {
            return [];
        }
    }

    private void SaveUnsafe(IReadOnlyCollection<Sub2ApiUsageRecord> records)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    new Sub2ApiUsageFile
                    {
                        SchemaVersion = SchemaVersion,
                        Events = records
                            .OrderBy(item => item.CompletedAtUtc)
                            .ThenBy(item => item.EventId, StringComparer.Ordinal)
                            .ToList()
                    },
                    JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
            throw;
        }
    }

    internal static void ValidateLedger()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "codex-sub2api-ledger-" + Guid.NewGuid().ToString("N"));
        var sessions = System.IO.Path.Combine(root, "sessions");
        var accountHome = System.IO.Path.Combine(root, "account-home");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(accountHome);
        try
        {
            var timestamp = DateTimeOffset.UtcNow.AddMinutes(-2);
            var csvPath = System.IO.Path.Combine(root, "usage.csv");
            var csv =
                "Time,API Key Name,Model,Inbound Endpoint,IP Address,Type,Input Tokens,Output Tokens,Cache Read Tokens,Cache Creation Tokens,Billed Cost,Duration (ms)\r\n" +
                $"{timestamp:O},\"key, \"\"quoted\"\"\r\nline\",gpt-5.6-terra,/v1/responses,127.0.0.1,Stream,80,30,20,0,0.12345678,50\r\n" +
                $"{timestamp.AddSeconds(1):O},fixture,gpt-5.6-terra,/v1/alpha/search,127.0.0.1,Sync,0,0,0,0,0.01000000,20\r\n";
            File.WriteAllText(csvPath, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var oldAccount = new AccountRecord
            {
                Name = "old-name",
                CodexHome = accountHome,
                AuthKind = AccountAuthKind.CompatibleApi
            };
            var parsed = ReadCsvForImport(csvPath, oldAccount);
            if (parsed.Count != 2 ||
                parsed[0].InputTokens != 100L ||
                parsed[0].CachedInputTokens != 20L ||
                parsed[0].CacheWriteTokens != 0L ||
                parsed[0].OutputTokens != 30L ||
                parsed[0].TotalTokens != 130L ||
                parsed[0].BilledCostUsd != 0.12345678M ||
                !parsed[1].IsSearch)
            {
                throw new InvalidOperationException("sub2api RFC4180/token mapping self-test failed.");
            }

            var ledger = new Sub2ApiUsageLedger(root);
            var inclusiveFrom = timestamp.AddSeconds(1);
            var filtered = ReadCsvForImport(csvPath, oldAccount, inclusiveFrom);
            var filteredPreview = ledger.Import(
                csvPath,
                oldAccount,
                [],
                persist: false,
                fromUtc: inclusiveFrom);
            if (filtered.Count != 1 ||
                !filtered[0].IsSearch ||
                filteredPreview.CsvRows != 1 ||
                filteredPreview.AddedRows != 1 ||
                filteredPreview.AddedCostUsd != 0.01000000M)
            {
                throw new InvalidOperationException(
                    "sub2api inclusive --from filtering self-test failed.");
            }

            var croppedCsvPath = System.IO.Path.Combine(root, "cropped-usage.csv");
            var croppedCsv =
                "Time,API Key Name,Model,Inbound Endpoint,IP Address,Type,Input Tokens,Output Tokens,Cache Read Tokens,Cache Creation Tokens,Billed Cost,Duration (ms)\r\n" +
                $"{timestamp:O},different-key,gpt-5.6-terra,/v1/responses,203.0.113.9,Sync,80,30,20,0,0.12345678,999\r\n";
            File.WriteAllText(croppedCsvPath, croppedCsv, Encoding.UTF8);
            var cropped = ReadCsvForImport(croppedCsvPath, oldAccount);
            if (cropped.Count != 1 || cropped[0].EventId != parsed[0].EventId)
            {
                throw new InvalidOperationException(
                    "sub2api core billing fields must produce a stable EventId across CSV projections.");
            }

            var delayedNatural = parsed[0].ToUsageEvent(oldAccount.Name);
            delayedNatural.TimestampUtc = delayedNatural.TimestampUtc.AddSeconds(40);
            var latencyPreview = ledger.Import(
                csvPath,
                oldAccount,
                [delayedNatural],
                persist: false);
            if (latencyPreview.CsvRows != 2 ||
                latencyPreview.MatchedLocalRows != 1 ||
                latencyPreview.AddedRows != 1 ||
                latencyPreview.AddedCostUsd != 0.01000000M)
            {
                throw new InvalidOperationException(
                    "sub2api 45-second completion-latency matching self-test failed.");
            }

            var naturalSearch = parsed[1].ToUsageEvent(oldAccount.Name);
            var firstImport = ledger.Import(csvPath, oldAccount, [naturalSearch], persist: true);
            var repeatedImport = ledger.Import(csvPath, oldAccount, [naturalSearch], persist: true);
            if (firstImport.CsvRows != 2 ||
                firstImport.MatchedLocalRows != 1 ||
                firstImport.AddedRows != 1 ||
                firstImport.AddedCostUsd != 0.12345678M ||
                repeatedImport.AlreadyImportedRows != 1 ||
                repeatedImport.MatchedLocalRows != 1 ||
                repeatedImport.AddedRows != 0)
            {
                throw new InvalidOperationException("sub2api idempotent import self-test failed.");
            }

            var renamedAccount = new AccountRecord
            {
                Name = "new-name",
                CodexHome = accountHome,
                AuthKind = AccountAuthKind.CompatibleApi
            };
            var beforeRecovery = ledger.LoadMissingUsageEvents(
                [renamedAccount],
                timestamp.AddMinutes(-1),
                []);
            if (beforeRecovery.Count != 1 ||
                beforeRecovery[0].AccountName != renamedAccount.Name ||
                beforeRecovery[0].InputTokens != 100L ||
                beforeRecovery[0].EquivalentCostOverrideUsd != 0.12345678D)
            {
                throw new InvalidOperationException(
                    "sub2api stable account identity/rename self-test failed.");
            }

            var switchEvent = new UsageSwitchEvent
            {
                AccountName = renamedAccount.Name,
                AccountKey = QuotaAccountIdentity.CreateKey(renamedAccount),
                ManagerScopeKey = QuotaAccountIdentity.CreateManagerScopeKey(root),
                SwitchedAtUtc = timestamp.AddSeconds(-1).ToString("O"),
                Source = "fixture"
            };
            File.WriteAllText(
                System.IO.Path.Combine(root, "usage-account-switches.json"),
                JsonSerializer.Serialize(new[] { switchEvent }, JsonOptions));

            var tracker = new UsageTracker(root);
            var ledgerOnlyReport = tracker.BuildReport([renamedAccount], sessions, DateTimeOffset.Now);
            if (ledgerOnlyReport.Accounts.Single().Month.Events != 1 ||
                ledgerOnlyReport.Accounts.Single().Month.TotalTokens != 130L ||
                Math.Abs(ledgerOnlyReport.Accounts.Single().Month.EquivalentCostOverrideUsd - 0.12345678D) >
                0.000_000_001D)
            {
                throw new InvalidOperationException("sub2api BuildReport ledger merge self-test failed.");
            }

            var jsonl = System.IO.Path.Combine(sessions, "natural.jsonl");
            File.WriteAllLines(jsonl,
            [
                JsonSerializer.Serialize(new
                {
                    timestamp = timestamp.AddMilliseconds(-10).ToString("O"),
                    type = "turn_context",
                    payload = new { model = "gpt-5.6-terra" }
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = timestamp.ToString("O"),
                    type = "event_msg",
                    payload = new
                    {
                        type = "token_count",
                        info = new
                        {
                            last_token_usage = new
                            {
                                input_tokens = 100,
                                cached_input_tokens = 20,
                                cache_write_tokens = 0,
                                output_tokens = 30,
                                reasoning_output_tokens = 0,
                                total_tokens = 130
                            },
                            total_token_usage = new
                            {
                                input_tokens = 100,
                                cached_input_tokens = 20,
                                cache_write_tokens = 0,
                                output_tokens = 30,
                                reasoning_output_tokens = 0,
                                total_tokens = 130
                            }
                        }
                    }
                })
            ]);
            var recoveredReport = new UsageTracker(root).BuildReport(
                [renamedAccount],
                sessions,
                DateTimeOffset.Now);
            var recoveredSummary = recoveredReport.Accounts.Single();
            if (recoveredSummary.Month.Events != 1 ||
                recoveredSummary.Month.TotalTokens != 130L ||
                recoveredSummary.Month.EquivalentCostOverrideUsd != 0D)
            {
                throw new InvalidOperationException(
                    "A later natural event must replace, not duplicate, a sub2api ledger row.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Sub2ApiCsvRow(
        string EventId,
        DateTimeOffset CompletedAtUtc,
        string Model,
        string Endpoint,
        long InputTokens,
        long CachedInputTokens,
        long CacheWriteTokens,
        long OutputTokens,
        long TotalTokens,
        decimal BilledCostUsd)
    {
        public Sub2ApiUsageRecord ToRecord(string accountKey, string accountName) => new()
        {
            EventId = EventId,
            AccountKey = accountKey,
            AccountName = accountName.Trim(),
            CompletedAtUtc = CompletedAtUtc,
            Model = Model,
            Endpoint = Endpoint,
            InputTokens = InputTokens,
            CachedInputTokens = CachedInputTokens,
            CacheWriteTokens = CacheWriteTokens,
            OutputTokens = OutputTokens,
            TotalTokens = TotalTokens,
            BilledCostUsd = BilledCostUsd
        };
    }

    private sealed class Sub2ApiUsageFile
    {
        public int SchemaVersion { get; set; }
        public List<Sub2ApiUsageRecord>? Events { get; set; }
    }
}
