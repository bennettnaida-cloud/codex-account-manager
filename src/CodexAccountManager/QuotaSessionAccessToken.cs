using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

// Bind quota helpers to the selected account's valid bearer in memory. Imported
// OAuth files may lack last_refresh; native file authentication can then omit
// Authorization despite a usable access token. No refresh timestamp is invented.
internal sealed record QuotaSessionAccessToken(string AccessToken, string AccountId)
{
    internal static QuotaSessionAccessToken? Read(AccountRecord account, string path)
    {
        if (account.IsWebSession) throw new InvalidDataException(AccountAuthKind.WebSessionRemovedMessage);
        if (!account.IsOfficialOAuth) return null;
        if (new FileInfo(path).Length > AccountFileImport.MaxFileBytes)
            throw new InvalidDataException("OAuth 凭据文件过大。");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        AccountFileImport.RejectDuplicateKeys(document.RootElement);
        return ParseOAuth(document.RootElement, DateTimeOffset.UtcNow);
    }

    private static QuotaSessionAccessToken? ParseOAuth(JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            return null;
        var token = AccountFileImport.Text(tokens, "access_token");
        if (token.Length is < 64 or > 16384 ||
            token.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            return null;
        if (!DateTimeOffset.TryParse(CodexCliService.GetAccessTokenExpiryUtc(token),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt) ||
            expiresAt <= now.AddMinutes(1))
            return null; // Expired tokens retain the native OAuth refresh path.
        var claimId = AccountFileImport.Claim(token, "chatgpt_account_id");
        var accountId = AccountFileImport.Text(tokens, "account_id");
        if (claimId.Length > 0 && accountId.Length > 0 && claimId != accountId)
            throw new InvalidDataException("OAuth 凭据的工作区与 access token 不一致，已停止额度查询。");
        if (accountId.Length == 0) accountId = claimId;
        if (accountId.Length is < 1 or > 128 ||
            !accountId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new InvalidDataException("OAuth 凭据缺少有效的工作区标识。");
        return new(token, accountId);
    }

    internal static void ValidateImportedOAuth()
    {
        var now = DateTimeOffset.UtcNow;
        string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var claims = new JsonObject
        {
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "quota-a" }
        };
        var token = Encode("{\"alg\":\"RS256\"}") + "." + Encode(claims.ToJsonString()) + ".fixture";
        var auth = new JsonObject
        {
            ["auth_mode"] = "chatgpt", ["last_refresh"] = null,
            ["tokens"] = new JsonObject { ["access_token"] = token, ["account_id"] = "quota-a" }
        };
        QuotaSessionAccessToken? Parse(DateTimeOffset time)
        {
            using var document = JsonDocument.Parse(auth.ToJsonString());
            return ParseOAuth(document.RootElement, time);
        }
        var before = auth.ToJsonString();
        if (Parse(now) is not { AccountId: "quota-a" } credential || credential.AccessToken != token ||
            auth.ToJsonString() != before || Parse(now.AddHours(2)) != null)
            throw new InvalidOperationException("Imported OAuth quota authentication or expiry failed.");
        auth["tokens"]!["account_id"] = "quota-b";
        try { _ = Parse(now); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Quota authentication accepted a different workspace.");
    }
}
