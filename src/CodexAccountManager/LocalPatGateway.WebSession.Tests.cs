using System.Text.Json.Nodes;

namespace CodexAccountManager;

internal sealed partial class LocalPatGatewayHost
{
    internal static void ValidateWebSessionRouting()
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-session-routing-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
        var previousShared = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME");
        LocalPatGatewayHost? host = null;
        try
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", root);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", Path.Combine(root, "shared"));
            var store = new AccountStore(root);
            var entry = AccountFileImport.Parse(AccountFileImportTests.SessionFixture()).Entries.Single();
            store.ImportAccounts([entry]);
            new ThemeService(root).SaveSettings(new AppSettings { PatGatewayProxyAutoDetect = false });
            var account = store.LoadAccounts().Single();
            host = new LocalPatGatewayHost("X-Session-Fixture", "1");
            var key = QuotaAccountIdentity.CreateKey(account);
            var credential = host.ResolveRotationCredential(key);
            var bound = host.BindConfiguredAccountKey(ParseBearerCredential("Bearer " + credential.Token)!);
            foreach (var candidate in new[] { credential, bound })
                if (!candidate.IsWebSession || candidate.IsPersonalAccessToken || candidate.IsCompatibleApi ||
                    candidate.AccountKey != key || candidate.ChatGptAccountId != "session-workspace" ||
                    candidate.AllowIncomingChatGptIdentity || SelectChatGptAccountId(candidate, null, "wrong-incoming-workspace") != "session-workspace")
                    throw new InvalidOperationException("Session gateway identity was not pinned to its account.");
            if (ShouldRejectSharedOAuthModelRequest(true,
                bound.IsPersonalAccessToken || bound.IsCompatibleApi || bound.IsWebSession, true, false))
                throw new InvalidOperationException("Configured Session bearer was rejected as unrelated shared OAuth.");
            var path = Path.Combine(account.CodexHome, "auth.json");
            var expired = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            expired["web_session"]!["expires_at"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
            File.WriteAllText(path, expired.ToJsonString());
            ExpectExpired(() => host.ResolveRotationCredential(key));
            ExpectExpired(() => host.BindConfiguredAccountKey(ParseBearerCredential("Bearer " + credential.Token)!));
            Console.WriteLine("PASS: Session gateway binding, rotation target, incoming identity isolation and expiration");
        }
        finally
        {
            host?._proxyResolver.Dispose();
            if (host != null) foreach (var client in host._clients.Values) client.Dispose();
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", previousRoot);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", previousShared);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        static void ExpectExpired(Func<GatewayCredential> action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Expired Session reached a gateway route.");
        }
    }
}
