using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal sealed record ProbeUsageRecord(
    string EventId,
    string AccountKey,
    string AccountName,
    DateTimeOffset CompletedAtUtc,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long TotalTokens,
    double EquivalentCostUsd,
    string Model)
{
    public UsageEvent ToUsageEvent(string resolvedAccountName) => new()
    {
        AccountName = resolvedAccountName,
        Model = Model,
        TimestampUtc = CompletedAtUtc.ToUniversalTime(),
        Source = UsageEventSource.LegacyProbe,
        EquivalentCostOverrideUsd = EquivalentCostUsd,
        InputTokens = InputTokens,
        CachedInputTokens = CachedInputTokens,
        OutputTokens = OutputTokens,
        TotalTokens = TotalTokens
    };
}

internal static class QuotaAccountIdentity
{
    public static string CreateKey(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var normalizedHome = Path.GetFullPath(account.CodexHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var bytes = Encoding.UTF8.GetBytes(account.AuthKind + "\n" + normalizedHome);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string CreateManagerScopeKey(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Manager root path is required.", nameof(rootPath));
        }

        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot)));
    }
}

/// <summary>
/// Read-only compatibility for usage created by retired quota probes. These historical
/// Token charges remain part of local usage totals, but the application no longer creates
/// or appends probe events.
/// </summary>
internal sealed class ProbeUsageLedger
{
    internal const string FileName = "quota-probe-usage.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;

    public ProbeUsageLedger(string rootPath)
    {
        _path = System.IO.Path.Combine(rootPath, FileName);
    }

    public string Path => _path;

    public IReadOnlyList<ProbeUsageRecord> LoadSince(DateTimeOffset sinceUtc)
    {
        lock (_gate)
        {
            return LoadUnsafe(throwOnDamage: false)
                .Where(record => record.CompletedAtUtc >= sinceUtc.ToUniversalTime())
                .OrderBy(record => record.CompletedAtUtc)
                .ThenBy(record => record.EventId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public void RenameAccount(string originalName, string newName)
    {
        if (string.IsNullOrWhiteSpace(originalName) || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        lock (_gate)
        {
            var records = LoadUnsafe(throwOnDamage: true);
            var changed = false;
            for (var index = 0; index < records.Count; index++)
            {
                if (!records[index].AccountName.Equals(
                        originalName.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                records[index] = records[index] with { AccountName = newName.Trim() };
                changed = true;
            }
            if (changed)
            {
                SaveUnsafe(records);
            }
        }
    }

    private List<ProbeUsageRecord> LoadUnsafe(bool throwOnDamage)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }
            var file = JsonSerializer.Deserialize<ProbeUsageFile>(File.ReadAllText(_path), JsonOptions) ??
                       throw new InvalidDataException("Probe usage ledger root is empty.");
            return file.Events ?? [];
        }
        catch (Exception ex) when (throwOnDamage)
        {
            throw new InvalidOperationException(
                "历史探针用量账本已损坏，无法安全更新账号名称。",
                ex);
        }
        catch
        {
            return [];
        }
    }

    private void SaveUnsafe(IReadOnlyCollection<ProbeUsageRecord> records)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(new ProbeUsageFile { Events = records.ToList() }, JsonOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    internal static void ValidateLedger()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "codex-probe-ledger-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ledger = new ProbeUsageLedger(root);
            var completedAtUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(
                ledger.Path,
                JsonSerializer.Serialize(
                    new ProbeUsageFile
                    {
                        Events =
                        [
                            new ProbeUsageRecord(
                                "legacy:1",
                                "account-key",
                                "example",
                                completedAtUtc,
                                9_500,
                                500,
                                86,
                                9_586,
                                0.024D,
                                "gpt-5.6-terra")
                        ]
                    },
                    JsonOptions));
            var loaded = ledger.LoadSince(DateTimeOffset.UtcNow.AddMinutes(-1));
            if (loaded.Count != 1 || loaded[0].TotalTokens != 9_586)
            {
                throw new InvalidOperationException("Historical probe usage compatibility self-test failed.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ProbeUsageFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ProbeUsageRecord> Events { get; set; } = [];
    }
}
