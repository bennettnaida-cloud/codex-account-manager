using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

// Browser sessions are short-lived bearer credentials, not refreshable Codex OAuth grants.
internal sealed record WebSessionCredential(string AccessToken, string AccountId, DateTimeOffset ExpiresAt)
{
    internal const string ExpiredMessage = "网页版 Session 已过期或即将过期，请重新打开 api/auth/session 并导入最新 JSON。";
    internal const string ImportedMessage = "网页版 Session 已导入；导入不代表模型调用已获授权。若测试返回 401/403，请使用“通过 ChatGPT 登录（官方）”。";

    internal static string AccessRejectedMessage(int statusCode, string operation) =>
        $"网页版 Session 被官方{operation}拒绝（HTTP {statusCode}）。" +
        "导入成功、Token 未到期或额度查询成功，都不代表模型请求已获授权。" +
        "请在“新增账号 → 登录方式”选择“通过 ChatGPT 登录（官方）”，用同一账号完成 Codex 授权。" +
        "也可更新 Session 后重试；如果官方登录仍被拒绝，请检查工作区的 Codex 访问权限。";

    internal static AccountImportEntry ParseImport(JsonElement session, string fallbackName)
    {
        var token = AccountFileImport.Text(session, "accessToken");
        var accountId = "";
        if (session.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object)
            accountId = AccountFileImport.Text(account, "id", "account_id", "chatgpt_account_id");
        var credential = Validate(token, accountId, AccountFileImport.Text(session, "expires"), requireCurrent: true);
        var name = AccountFileImport.Text(session, "name", "email");
        if (name.Length == 0 && session.TryGetProperty("user", out var user))
            name = AccountFileImport.Text(user, "email", "name");
        if (name.Length == 0) name = fallbackName;
        name = new string(name.Where(ch => !char.IsControl(ch)).Take(80).ToArray()).Trim();
        return credential.ToImportEntry(name);
    }

    internal AccountImportEntry ToImportEntry(string name)
    {
        // The native client uses API-key transport to our local gateway, which forwards
        // the original JWT with the pinned workspace. No synthetic ID/refresh token.
        var auth = new JsonObject
        {
            ["auth_mode"] = "apikey", ["OPENAI_API_KEY"] = AccessToken,
            ["web_session"] = new JsonObject
            {
                ["account_id"] = AccountId,
                ["expires_at"] = ExpiresAt.ToString("O")
            }
        };
        return new(name, AccountAuthKind.WebSession, auth.ToJsonString(), "", "",
            AccountFileImport.Fingerprint(AccountAuthKind.WebSession, Identity));
    }

    internal string Identity => AccessToken + "\n" + AccountId;

    internal static WebSessionCredential ReadStored(string path, bool requireCurrent = true)
    {
        if (new FileInfo(path).Length > AccountFileImport.MaxFileBytes)
            throw new InvalidDataException("Session 凭据文件过大。");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        AccountFileImport.RejectDuplicateKeys(doc.RootElement);
        return ReadStored(doc.RootElement, requireCurrent);
    }

    internal static WebSessionCredential ReadStored(JsonElement root, bool requireCurrent = true)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("web_session", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            AccountFileImport.Text(root, "auth_mode") != "apikey")
            throw new InvalidDataException("缺少网页版 Session 凭据，请重新导入 JSON。");
        return Validate(AccountFileImport.Text(root, "OPENAI_API_KEY"),
            AccountFileImport.Text(metadata, "account_id"), AccountFileImport.Text(metadata, "expires_at"), requireCurrent);
    }

    private static WebSessionCredential Validate(string token, string accountId, string expiry, bool requireCurrent)
    {
        if (token.Length is < 64 or > 8192 || !token.StartsWith("eyJ", StringComparison.Ordinal) ||
            token.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new InvalidDataException("Session 缺少有效 accessToken；请粘贴网页返回的完整 JSON。");
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
                throw new InvalidDataException("Session accessToken 格式无效。");
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var jwt = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            if (jwt.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Session accessToken 内容无效。");
            AccountFileImport.RejectDuplicateKeys(jwt.RootElement);
            var claimId = AccountFileImport.Claim(token, "chatgpt_account_id");
            if (accountId.Length > 0 && claimId.Length > 0 && accountId != claimId)
                throw new InvalidDataException("Session 的账号标识与 accessToken 不一致，未导入。");
            if (accountId.Length == 0) accountId = claimId;
            if (accountId.Length is < 1 or > 128 || !accountId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
                throw new InvalidDataException("Session 缺少 ChatGPT 工作区 account_id；请复制完整 JSON，或使用官方网页登录。");
            if (!jwt.RootElement.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var unix))
                throw new InvalidDataException("Session accessToken 缺少有效期，无法安全使用。");
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (expiry.Length > 0)
            {
                if (!DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sessionExpiry))
                    throw new InvalidDataException("Session 的 expires 日期格式无效。");
                if (sessionExpiry < expiresAt) expiresAt = sessionExpiry;
            }
            if (requireCurrent && expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
                throw new InvalidDataException(ExpiredMessage);
            return new(token, accountId, expiresAt);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        { throw new InvalidDataException("Session accessToken 内容无效，请重新复制网页 JSON。"); }
    }
}
