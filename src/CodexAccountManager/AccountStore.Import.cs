using System.Text.Json;

namespace CodexAccountManager;

public sealed partial class AccountStore
{
    internal void UpdateWebSession(AccountRecord expected, string name, AccountImportEntry? entry)
    {
        var accounts = LoadAccounts();
        var current = accounts.SingleOrDefault(a => a.IsWebSession && a.Name == expected.Name &&
            PathsEqual(a.CodexHome, expected.CodexHome))
            ?? throw new InvalidDataException("Session 账号已变化，请重新打开账号编辑窗口。");
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl) ||
            accounts.Any(a => a != current && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("账号名称为空、过长或已被使用，请重新填写。");
        var path = Path.Combine(current.CodexHome, "auth.json");
        var previous = File.ReadAllText(path);
        if (entry != null)
        {
            using var doc = JsonDocument.Parse(entry.AuthJson);
            var replacement = WebSessionCredential.ReadStored(doc.RootElement);
            var original = WebSessionCredential.ReadStored(path, requireCurrent: false);
            if (entry.AuthKind != AccountAuthKind.WebSession || replacement.AccountId != original.AccountId)
                throw new InvalidDataException("新 Session 属于不同的 ChatGPT 工作区，请通过“新增账号”单独导入。");
        }
        try
        {
            if (entry != null) AtomicFilePersistence.WriteAllText(path, entry.AuthJson);
            current.Name = name;
            SaveAccounts(accounts);
        }
        catch
        {
            if (entry != null) AtomicFilePersistence.WriteAllText(path, previous);
            throw;
        }
    }

    internal (int Added, int Duplicates) ImportAccounts(IReadOnlyList<AccountImportEntry> entries)
    {
        var accounts = LoadAccounts();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(account.CodexHome, "auth.json")));
                var root = doc.RootElement;
                string identity;
                if (account.IsWebSession)
                    identity = WebSessionCredential.ReadStored(root, requireCurrent: false).Identity;
                else if (account.IsOfficialOAuth && root.TryGetProperty("tokens", out var tokens))
                    identity = AccountFileImport.Text(tokens, "refresh_token") + "\n" + AccountFileImport.Text(tokens, "account_id");
                else
                {
                    identity = AccountFileImport.Text(root, "personal_access_token", "OPENAI_API_KEY");
                    if (account.IsCompatibleApi) identity += "\n" + account.ApiBaseUrl.TrimEnd('/');
                }
                fingerprints.Add(AccountFileImport.Fingerprint(account.AuthKind, identity));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        }
        var created = new List<string>();
        var duplicates = 0;
        try
        {
            foreach (var entry in entries)
            {
                if (!fingerprints.Add(entry.Fingerprint)) { duplicates++; continue; }
                var name = entry.Name;
                var suffix = 2;
                while (accounts.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    name = entry.Name + " (" + suffix++ + ")";
                // Names and paths from the imported document never choose a filesystem location.
                var home = Path.Combine(RootPath, "accounts", "imported-" + Guid.NewGuid().ToString("N"));
                var account = new AccountRecord { Name = name, CodexHome = home, AuthKind = entry.AuthKind,
                    ApiBaseUrl = entry.BaseUrl, ApiModel = string.IsNullOrWhiteSpace(entry.Model) ? "gpt-5.5" : entry.Model,
                    DiscoverDefaultModelOnLaunch = entry.AuthKind == AccountAuthKind.CompatibleApi && string.IsNullOrWhiteSpace(entry.Model) };
                created.Add(home);
                using var credential = JsonDocument.Parse(entry.AuthJson);
                EnsureCodexHome(account, account.IsCompatibleApi ? AccountFileImport.Text(credential.RootElement, "OPENAI_API_KEY") : null);
                AtomicFilePersistence.WriteAllText(Path.Combine(home, "auth.json"), entry.AuthJson);
                accounts.Add(account);
            }
            if (created.Count > 0) SaveAccounts(accounts);
            return (created.Count, duplicates);
        }
        catch
        {
            // Only these newly created random directories belong to this transaction.
            foreach (var home in created)
                try { if (Directory.Exists(home)) Directory.Delete(home, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
