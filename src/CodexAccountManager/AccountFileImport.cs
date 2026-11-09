using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

internal sealed record AccountImportEntry(string Name, string AuthKind, string AuthJson,
    string BaseUrl, string Model, string Fingerprint);
internal sealed record AccountImportProblem(string Source, string Reason);
internal sealed record AccountImportBatch(IReadOnlyList<AccountImportEntry> Entries,
    IReadOnlyList<AccountImportProblem> Problems);

internal static class AccountFileImport
{
    internal const int MaxFileBytes = 16 * 1024 * 1024;
    internal const int MaxTotalBytes = 64 * 1024 * 1024;
    internal const int MaxEntries = 1000;

    internal static AccountImportBatch ReadFiles(IEnumerable<string> paths)
    {
        var entries = new List<AccountImportEntry>();
        var problems = new List<AccountImportProblem>();
        long total = 0;
        var count = 0;
        foreach (var path in paths)
        {
            var source = Path.GetFileName(path);
            try
            {
                using var file = File.OpenRead(path);
                if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var archive = new ZipArchive(file, ZipArchiveMode.Read);
                    if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("压缩包文件过多（最多 1000 项）。");
                    foreach (var item in archive.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                    {
                        using var input = item.Open(); // Never extract archive-controlled paths.
                        Read(input, source + " / " + item.Name);
                    }
                }
                else Read(file, source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { problems.Add(new(source, "文件无法读取、损坏或超过导入限制。")); }
        }
        return new(entries, problems);

        void Read(Stream input, string source)
        {
            if (++count > MaxEntries) throw new InvalidDataException("文件过多。");
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                total += read;
                if (output.Length + read > MaxFileBytes || total > MaxTotalBytes)
                    throw new InvalidDataException("文件过大。");
                output.Write(buffer, 0, read);
            }
            var batch = Parse(Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF'), source);
            if (entries.Count + batch.Entries.Count > MaxEntries)
                throw new InvalidDataException("账号过多。");
            entries.AddRange(batch.Entries);
            problems.AddRange(batch.Problems);
        }
    }

    internal static AccountImportBatch Parse(string json, string source = "JSON")
    {
        var entries = new List<AccountImportEntry>();
        var problems = new List<AccountImportProblem>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateKeys(document.RootElement);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
                data.ValueKind is JsonValueKind.Object or JsonValueKind.Array) root = data;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("accounts", out var accounts)) root = accounts;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root };
            if (items.Length > MaxEntries) throw new InvalidDataException("单次最多导入 1000 个账号。");
            for (var index = 0; index < items.Length; index++)
            {
                try { entries.Add(ParseEntry(items[index], Path.GetFileNameWithoutExtension(source), index)); }
                catch (InvalidDataException ex) { problems.Add(new(source + " #" + (index + 1), ex.Message)); }
            }
        }
        catch (JsonException) { problems.Add(new(source, "JSON 格式无效或嵌套过深。")); }
        catch (InvalidDataException ex) { problems.Add(new(source, ex.Message)); }
        return new(entries, problems);
    }

    private static AccountImportEntry ParseEntry(JsonElement item, string fallbackName, int index)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("账号记录必须是 JSON 对象。");
        var platform = Text(item, "platform").ToLowerInvariant();
        var type = Text(item, "type").ToLowerInvariant();
        if (platform.Length > 0 && platform is not ("openai" or "codex") ||
            platform.Length == 0 && type.Length > 0 && type is not ("codex" or "oauth" or "apikey" or "api_key" or "api-key" or "pat" or "access_token"))
            throw new InvalidDataException("仅支持 OpenAI/Codex 账号，已跳过其它平台。");
        var credentials = item.TryGetProperty("credentials", out var c) && c.ValueKind == JsonValueKind.Object ? c : item;
        var tokens = credentials.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object ? t : credentials;
        var name = Text(item, "name", "email");
        if (name.Length == 0) name = Text(credentials, "email");
        if (name.Length == 0) name = fallbackName + "-" + (index + 1);
        name = new string(name.Where(ch => !char.IsControl(ch)).Take(80).ToArray()).Trim();
        var access = Text(tokens, "access_token");
        var refresh = Text(tokens, "refresh_token");
        var idToken = Text(tokens, "id_token");
        var pat = Text(credentials, "personal_access_token", "pat");
        var apiKey = Text(credentials, "api_key", "OPENAI_API_KEY");
        if (pat.Length == 0 && access.StartsWith("at-", StringComparison.Ordinal)) pat = access;
        if (pat.Length == 0 && apiKey.StartsWith("at-", StringComparison.Ordinal)) pat = apiKey;
        if (pat.Length > 0)
        {
            if (CodexCliService.GetAccessTokenInputError(pat) != null)
                throw new InvalidDataException("PAT 格式无效或导出内容已脱敏。");
            return Entry(AccountAuthKind.AccessToken, new JsonObject { ["auth_mode"] = "personalAccessToken", ["personal_access_token"] = pat }, "", "", pat);
        }
        if (refresh.Length > 0 || access.Length > 0 || credentials.TryGetProperty("tokens", out _))
        {
            ValidateSecret(access, "缺少有效 OAuth access_token。");
            ValidateSecret(refresh, "缺少有效 OAuth refresh_token。");
            var accountId = Text(tokens, "chatgpt_account_id", "account_id");
            if (accountId.Length == 0) accountId = Claim(idToken, "chatgpt_account_id");
            if (accountId.Length == 0) accountId = Claim(access, "chatgpt_account_id");
            if (accountId.Length == 0 || accountId.Length > 128 || !accountId.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
                throw new InvalidDataException("缺少有效的 ChatGPT account_id，无法确定所属账号。");
            var clientId = Text(credentials, "client_id");
            if (clientId.Length > 0 && clientId != CodexCliService.CodexOAuthClientId)
                throw new InvalidDataException("该 OAuth 凭据属于其它授权客户端，需使用 Codex OAuth 导出或重新登录。");
            return Entry(AccountAuthKind.OfficialOAuth, new JsonObject
            {
                ["auth_mode"] = "chatgpt", ["OPENAI_API_KEY"] = null,
                ["tokens"] = new JsonObject { ["access_token"] = access, ["refresh_token"] = refresh,
                    ["id_token"] = idToken, ["account_id"] = accountId },
                ["last_refresh"] = Text(item, "last_refresh") is { Length: > 0 } last ? last : null
            }, "", "", refresh + "\n" + accountId);
        }
        ValidateSecret(apiKey, "缺少可导入的凭据，或文件仅包含账号列表而非完整导出。");
        var baseUrl = Text(credentials, "base_url", "api_base_url", "api_base");
        if (baseUrl.Length == 0) baseUrl = Text(item, "base_url", "api_base_url");
        if (baseUrl.Length == 0) throw new InvalidDataException("API Key 记录缺少 base_url；请补充中转站地址后导入。");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("API 地址无效：远程地址须为 HTTPS，且不能包含密码、查询参数或片段。");
        var model = Text(credentials, "model", "api_model");
        if (model.Length == 0) model = Text(item, "model", "api_model");
        // An empty selection is resolved from /models on first launch.
        if (model.Length > 0 && CodexCliService.GetCompatibleApiModelIdValidationError(model) != null)
            throw new InvalidDataException("API 默认模型标识无效。");
        return Entry(AccountAuthKind.CompatibleApi, new JsonObject { ["OPENAI_API_KEY"] = apiKey },
            uri.AbsoluteUri.TrimEnd('/'), model, apiKey + "\n" + uri.AbsoluteUri.TrimEnd('/'));

        AccountImportEntry Entry(string kind, JsonObject auth, string url, string model, string identity) =>
            new(name, kind, auth.ToJsonString(), url, model, Fingerprint(kind, identity));
    }

    internal static string Fingerprint(string kind, string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + identity)));
    private static void ValidateSecret(string value, string error)
    {
        if (value.Length < 8 || value.Length > 32768 || value.Any(char.IsWhiteSpace) || value.Contains('*') ||
            value.Contains('…') || value.Contains("REDACTED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(error);
    }
    internal static string Text(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
        return "";
    }
    internal static string Claim(string jwt, string key)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return "";
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            return doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth) ? Text(auth, key) : "";
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return ""; }
    }
    private static void RejectDuplicateKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("JSON 包含重复字段，未导入有歧义的凭据。");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) RejectDuplicateKeys(child);
    }
}
