using System.IO.Compression;
using System.Text.Json;

namespace CodexAccountManager;

internal static partial class AccountFileImportTests
{
    internal static void Validate()
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var native = """{"auth_mode":"chatgpt","tokens":{"id_token":"fixture-id-token","access_token":"fixture-access-token","refresh_token":"fixture-refresh-token","account_id":"fixture-account"}}""";
            var cpa = """{"type":"codex","email":"fixture@example.invalid","access_token":"fixture-access-token","refresh_token":"fixture-refresh-token","id_token":"fixture-id-token","account_id":"fixture-account"}""";
            var sub = """{"type":"sub2api-data","version":1,"accounts":[{"name":"fixture","platform":"openai","type":"oauth","credentials":{"access_token":"fixture-access-token","refresh_token":"fixture-refresh-token-2","chatgpt_account_id":"fixture-account-2"}},{"name":"fixture","platform":"openai","type":"apikey","credentials":{"api_key":"sk-fixture-only-123456","base_url":"https://example.invalid/v1","model":"gpt-99-test"}},{"name":"ignored","platform":"anthropic","credentials":{"api_key":"sk-fixture-other"}}]}""";
            var parsed = AccountFileImport.Parse(sub);
            Check(parsed.Entries.Count == 2 && parsed.Problems.Count == 1, "sub2api mixed accounts");
            Check(AccountFileImport.Parse(native).Entries.Count == 1 && AccountFileImport.Parse(cpa).Entries.Count == 1, "native and CPA");
            var store = new AccountStore(root);
            var entries = parsed.Entries.Concat(AccountFileImport.Parse(cpa).Entries).Concat(AccountFileImport.Parse(native).Entries).ToArray();
            var result = store.ImportAccounts(entries);
            Check(result == (3, 1), "duplicate credentials within batch");
            var manifest = File.ReadAllText(store.AccountsPath);
            var imported = store.LoadAccounts();
            Check(imported.Select(a => a.CodexHome).Distinct().Count() == 3 && imported.Select(a => a.Name).Distinct().Count() == 3, "isolated homes and duplicate names");
            Check(imported.Where(a => a.IsOfficialOAuth).All(CodexCliService.HasRefreshableOfficialOAuthCredential), "missing id token remains launchable via refresh");
            Check(store.ImportAccounts(entries) == (0, 4) && File.ReadAllText(store.AccountsPath) == manifest, "reimport never overwrites existing accounts");
            foreach (var invalid in new[]
            {
                "{bad json", "{\"access_token\":\"a\",\"access_token\":\"b\"}",
                "{\"type\":\"gemini\",\"access_token\":\"fixture-token\"}",
                "{\"api_key\":\"sk-fixture-only\"}",
                "{\"api_key\":\"sk-fixture-only\",\"base_url\":\"http://example.invalid/v1\"}",
                "{\"api_key\":\"REDACTED\",\"base_url\":\"https://example.invalid/v1\"}",
                "{\"access_token\":\"fixture-token\",\"refresh_token\":\"fixture-refresh\"}"
            }) Check(AccountFileImport.Parse(invalid).Entries.Count == 0, "invalid credential rejection");
            var pat = AccountFileImport.Parse("{\"personal_access_token\":\"at-fixture-pat-import-only-123456789\",\"name\":\"../outside\"}");
            Check(pat.Entries.Count == 1 && pat.Entries[0].AuthKind == AccountAuthKind.AccessToken, "PAT classification");
            store.ImportAccounts(pat.Entries);
            Check(store.LoadAccounts().Single(a => a.Name == "../outside").CodexHome.StartsWith(Path.Combine(root, "accounts") + Path.DirectorySeparatorChar), "imported names cannot choose paths");
            var zipPath = Path.Combine(root, "CPA.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("../../auth.json").Open());
                writer.Write(cpa);
            }
            Check(AccountFileImport.ReadFiles([zipPath]).Entries.Count == 1 && !File.Exists(Path.Combine(root, "auth.json")), "ZIP is parsed without extraction");
            var fullPath = Path.Combine(root, "too-large.json");
            using (var stream = File.Create(fullPath)) stream.SetLength(AccountFileImport.MaxFileBytes + 1);
            Check(AccountFileImport.ReadFiles([fullPath]).Entries.Count == 0, "file size limit");
            Console.WriteLine("PASS: sub2api/CPA/native/PAT imports, duplicate isolation, invalid inputs, ZIP paths, size limits");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Account import regression: " + message); }
}
