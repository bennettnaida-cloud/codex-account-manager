using System.Text.Json;

namespace CodexAccountManager;

internal static partial class AccountFileImportTests
{
    internal const string RetiredSessionToken = "eyJhbGciOiJSUzI1NiJ9.eyJleHAiOjQxMDI0NDQ4MDAsImZpeHR1cmUiOnRydWV9.fixture-signature";
    internal static string RetiredSessionAuth => JsonSerializer.Serialize(new
    {
        auth_mode = "apikey", OPENAI_API_KEY = RetiredSessionToken,
        web_session = new { account_id = "retired-workspace", expires_at = "2100-01-01T00:00:00Z" }
    });

    internal static AccountRecord WriteRetiredSessionFixture(AccountStore store)
    {
        var account = new AccountRecord { Name = "retired-session", AuthKind = AccountAuthKind.WebSession,
            CodexHome = Path.Combine(store.RootPath, "legacy-session") };
        Directory.CreateDirectory(account.CodexHome);
        File.WriteAllText(Path.Combine(account.CodexHome, "auth.json"), RetiredSessionAuth);
        store.SaveAccounts([account]);
        return account;
    }

    private static void ValidateRetiredWebSession(string root)
    {
        var store = new AccountStore(Path.Combine(root, "retired"));
        var account = WriteRetiredSessionFixture(store);
        var manifest = File.ReadAllText(store.AccountsPath);
        var browserJson = JsonSerializer.Serialize(new { accessToken = RetiredSessionToken,
            expires = "2100-01-01T00:00:00Z", account = new { id = "retired-workspace" } });
        foreach (var json in new[] { browserJson, RetiredSessionAuth,
            "{\"accounts\":[" + browserJson + "]}", "{\"credentials\":" + RetiredSessionAuth + "}",
            "{\"authKind\":\"web_session\",\"personal_access_token\":\"at-fixture-retired-123456789\"}" })
        {
            var batch = AccountFileImport.Parse(json);
            Check(batch.Entries.Count == 0 && batch.Problems.Count == 1 &&
                batch.Problems[0].Reason.Contains("功能已移除") && !batch.Problems[0].Reason.Contains(RetiredSessionToken),
                "retired Session imports are rejected without echoing credentials");
        }
        var valid = AccountFileImport.Parse("{\"personal_access_token\":\"at-fixture-import-retired-123456789\"}").Entries.Single();
        var invalid = new AccountImportEntry("retired", AccountAuthKind.WebSession, RetiredSessionAuth, "", "", "retired");
        try { store.ImportAccounts([valid, invalid]); throw new Exception("retired import accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(store.AccountsPath) == manifest && !Directory.Exists(Path.Combine(store.RootPath, "accounts")),
            "rejected batch must not partially create accounts");
        var loaded = store.LoadAccounts().Single();
        Check(loaded.IsWebSession && !loaded.IsAccessToken && !loaded.IsOfficialOAuth && !loaded.IsCompatibleApi,
            "retired credentials must not be reclassified as supported authentication");
        var settings = new AppSettings();
        AccountRotationConfiguration.SetPool(settings, [loaded], loaded, AccountRotationPool.Primary);
        Check(AccountRotationConfiguration.GetPool(settings, loaded) == AccountRotationPool.None &&
            AccountRotationConfiguration.GetOrderedAccounts(settings, [loaded], AccountRotationPool.Primary).Count == 0,
            "retired Session records cannot participate in rotation pools");
        var cli = new CodexCliService();
        var status = cli.GetLoginStatusAsync(loaded).GetAwaiter().GetResult();
        Check(status.ExitCode != 0 && status.Text.Contains("通过 ChatGPT 登录（官方）") && !cli.HasStoredQuotaTestCredential(loaded),
            "retired status is disabled with official login guidance");
        ExpectDisabled(() => cli.PrepareWindowsClientAccountAsync(loaded).GetAwaiter().GetResult());
        ExpectDisabled(() => cli.PrepareLaunchModelCatalogAsync(loaded, CancellationToken.None).GetAwaiter().GetResult());
        ExpectDisabled(() => cli.OpenUsageLimitResetSessionAsync(loaded).GetAwaiter().GetResult());
        ExpectDisabled(() => cli.SendMinimalQuotaTestAsync(loaded).GetAwaiter().GetResult());
        store.ImportAccounts([valid]);
        Check(store.LoadAccounts().Any(a => a.IsWebSession && a.CodexHome == account.CodexHome) &&
            File.ReadAllText(Path.Combine(account.CodexHome, "auth.json")) == RetiredSessionAuth &&
            !File.Exists(Path.Combine(account.CodexHome, "config.toml")),
            "normal imports and rejected operations preserve legacy credentials without rewriting configuration");
        Console.WriteLine("PASS: retired Session import rejection, launch/quota blocking and existing-data preservation");
    }

    private static void ExpectDisabled(Action action)
    {
        try { action(); }
        catch (InvalidDataException ex) when (ex.Message == AccountAuthKind.WebSessionRemovedMessage) { return; }
        throw new InvalidOperationException("Retired Session operation was not rejected before side effects.");
    }
}

internal sealed partial class LocalPatGatewayHost
{
    internal static void ValidateRetiredWebSessionRouting()
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-retired-session-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
        var previousShared = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME");
        LocalPatGatewayHost? host = null;
        try
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", root);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", Path.Combine(root, "shared"));
            var store = new AccountStore(root);
            var account = AccountFileImportTests.WriteRetiredSessionFixture(store);
            new ThemeService(root).SaveSettings(new AppSettings { PatGatewayProxyAutoDetect = false });
            host = new LocalPatGatewayHost("X-Retired-Session-Fixture", "1");
            ExpectRejected(() => host.ResolveRotationCredential(QuotaAccountIdentity.CreateKey(account)));
            ExpectRejected(() => host.BindConfiguredAccountKey(ParseBearerCredential("Bearer " + AccountFileImportTests.RetiredSessionToken)!));
            // Duplicate records must not let the retired token escape identity binding.
            store.SaveAccounts([account, new AccountRecord { Name = "duplicate", CodexHome = account.CodexHome }]);
            ExpectRejected(() => host.BindConfiguredAccountKey(ParseBearerCredential("Bearer " + AccountFileImportTests.RetiredSessionToken)!));
            Console.WriteLine("PASS: retired Session gateway bearer and rotation rejection");
        }
        finally
        {
            host?._proxyResolver.Dispose();
            if (host != null) foreach (var client in host._clients.Values) client.Dispose();
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", previousRoot);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", previousShared);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        static void ExpectRejected(Func<GatewayCredential> action)
        {
            try { action(); }
            catch (InvalidDataException ex) when (ex.Message == AccountAuthKind.WebSessionRemovedMessage) { return; }
            throw new InvalidOperationException("Retired Session reached a gateway route.");
        }
    }
}
