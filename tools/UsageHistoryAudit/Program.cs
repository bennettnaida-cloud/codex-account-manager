using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using CodexAccountManager;

// Recompute real usage with disposable caches. Never change accounts, authentication,
// session logs, the live manager's cache, or gateway configuration.
if (args.Length != 3)
    throw new ArgumentException("Usage: UsageHistoryAudit <manager-root> <account-name> <yyyy-MM-dd>");
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
var source = Path.GetFullPath(args[0]);
var day = DateTime.ParseExact(args[2], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
var accounts = JsonSerializer.Deserialize<List<AccountRecord>>(File.ReadAllText(Path.Combine(source, "accounts.json")))!;
var account = accounts.Single(a => a.Name == args[1]);
var temporary = Path.Combine(Path.GetTempPath(), "codex-usage-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var history = "usage-account-switches.json";
    File.Copy(Path.Combine(source, history), Path.Combine(temporary, history));
    var timer = Stopwatch.StartNew();
    var tracker = new UsageTracker(temporary);
    var report = tracker.BuildReport(accounts);
    var summary = report.Accounts.Single(a => a.AccountName == account.Name);
    var events = ReadTimeline(summary).Where(e => e.TimestampUtc.ToLocalTime().Date == day && e.TotalTokens > 0).ToArray();
    var result = events.GroupBy(e => e.Model).Select(g => new {
        Model = g.Key, Events = g.Count(),
        InputTokens = g.Sum(e => e.InputTokens),
        CachedInputTokens = g.Sum(e => e.CachedInputTokens),
        OutputTokens = g.Sum(e => e.OutputTokens),
        TotalTokens = g.Sum(e => e.TotalTokens),
        First = g.Min(e => e.TimestampUtc).ToLocalTime(),
        Last = g.Max(e => e.TimestampUtc).ToLocalTime()
    }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { Day = args[2], Models = result, ScanMs = timer.ElapsedMilliseconds }));
    timer.Restart();
    var again = tracker.BuildReport(accounts).Accounts.Single(a => a.AccountName == account.Name);
    var againEvents = ReadTimeline(again).Where(e => e.TimestampUtc.ToLocalTime().Date == day && e.TotalTokens > 0).ToArray();
    if (againEvents.Length != events.Length || againEvents.Sum(e => e.TotalTokens) != events.Sum(e => e.TotalTokens))
        throw new InvalidOperationException("Warm-cache results changed for the historical day.");
    Console.WriteLine($"Historical warm-cache consistency passed ({timer.ElapsedMilliseconds} ms).");
}
finally
{
    Directory.Delete(temporary, recursive: true);
}

static IEnumerable<UsageEvent> ReadTimeline(AccountUsageSummary summary) =>
    (IEnumerable<UsageEvent>)typeof(AccountUsageSummary)
        .GetProperty("Timeline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(summary)!;
