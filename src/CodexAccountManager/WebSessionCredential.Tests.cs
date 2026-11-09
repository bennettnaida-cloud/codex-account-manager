using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

internal static partial class AccountFileImportTests
{
    internal static string SessionFixture(string accountId = "session-workspace", int minutes = 60, string nonce = "first")
    {
        string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = Encode("{\"alg\":\"RS256\"}") + "." + Encode(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = accountId },
            ["nonce"] = nonce
        })) + ".fixture-signature";
        return JsonSerializer.Serialize(new { user = new { email = "session@example.invalid", id = "user-must-not-be-workspace" },
            accessToken = token, expires = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
            account = new { id = accountId }, cookie = "must-never-be-stored" });
    }

    private static void ValidateWebSessionImport(string root)
    {
        var json = SessionFixture();
        var batch = AccountFileImport.Parse(json);
        Check(batch.Entries.Count == 1 && batch.Problems.Count == 0, "browser session recognized");
        var entry = batch.Entries.Single();
        Check(entry.Name == "session@example.invalid" && entry.AuthKind == AccountAuthKind.WebSession, "session email/name and type");
        Check(!entry.AuthJson.Contains("cookie") && !entry.AuthJson.Contains("must-never-be-stored") &&
              !entry.AuthJson.Contains("refresh_token") && !entry.AuthJson.Contains("id_token"), "no cookie or invented OAuth grant");
        var store = new AccountStore(Path.Combine(root, "web-sessions"));
        Check(store.ImportAccounts([entry, entry]) == (1, 1), "session duplicate import");
        var account = store.LoadAccounts().Single();
        Check(account.IsWebSession && account.IsAccessToken && !account.IsOfficialOAuth && !account.IsCompatibleApi,
            "session uses bearer gateway transport, not PAT identity or refreshable OAuth");
        var authPath = Path.Combine(account.CodexHome, "auth.json");
        var session = WebSessionCredential.ReadStored(authPath);
        Check(session.AccountId == "session-workspace" && session.ExpiresAt < DateTimeOffset.UtcNow.AddHours(2), "JWT expiry beats browser cookie expiry");
        Check(store.ImportAccounts([entry]) == (0, 1), "session duplicate against stored account");
        Check(!File.ReadAllText(store.AccountsPath).Contains(session.AccessToken), "manifest contains no token");
        Check(AccountFileImport.Parse(File.ReadAllText(authPath)).Entries.Single().Fingerprint == entry.Fingerprint, "native Session credential round trip");
        var before = File.ReadAllText(authPath);
        var wrong = AccountFileImport.Parse(SessionFixture("other-workspace")).Entries.Single();
        try { store.UpdateWebSession(account, account.Name, wrong); throw new Exception("cross-workspace update accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(authPath) == before, "wrong-workspace update preserves credential");
        var replacement = AccountFileImport.Parse(SessionFixture(nonce: "new")).Entries.Single();
        store.UpdateWebSession(account, "renamed-session", replacement);
        var updated = store.LoadAccounts().Single();
        Check(updated.CodexHome == account.CodexHome && updated.Name == "renamed-session" &&
            updated.AuthKind == AccountAuthKind.WebSession && File.ReadAllText(authPath) == replacement.AuthJson,
            "updating session preserves home and authentication kind");
        store.UpdateWebSession(updated, "renamed-only", null);
        Check(File.ReadAllText(authPath) == replacement.AuthJson, "name-only edit preserves session");
        foreach (var invalid in new[] { "{}", "<html>sign in</html>", SessionFixture(minutes: -1),
            json.Replace("\"id\":\"session-workspace\"", "\"id\":\"other-workspace\""),
            json.Replace("accessToken", "sessionToken"), "{\"accessToken\":\"REDACTED\"}" })
        {
            var parsed = AccountFileImport.Parse(invalid);
            Check(parsed.Entries.Count == 0 && parsed.Problems.Count > 0, "reject expired/incomplete/mismatched session");
            Check(parsed.Problems.All(p => !p.Reason.Contains(session.AccessToken)), "session errors hide secrets");
        }
        var expiredStored = JsonNode.Parse(replacement.AuthJson)!.AsObject();
        expiredStored["web_session"]!["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        File.WriteAllText(authPath, expiredStored.ToJsonString());
        Check(new CodexCliService().GetLoginStatusAsync(store.LoadAccounts().Single()).GetAwaiter().GetResult().ExitCode != 0,
            "expired session is not reported as signed in");
        Check(AccountFileImport.Parse(expiredStored.ToJsonString()).Entries.Count == 0, "expired native session rejected");
        Console.WriteLine("PASS: browser Session import, expiry, identity pinning, duplicate isolation, update and secret minimization");
    }
}
