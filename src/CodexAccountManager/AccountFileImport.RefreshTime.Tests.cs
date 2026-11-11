using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

internal static partial class AccountFileImportTests
{
    private static void ValidateOAuthRefreshTimes()
    {
        var now = DateTimeOffset.UtcNow;
        var issued = now.AddDays(-1).ToUnixTimeSeconds();
        var claims = new JsonObject { ["iat"] = issued, ["exp"] = now.AddDays(9).ToUnixTimeSeconds() };
        string Token() => "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
        var credentials = new JsonObject
        {
            ["access_token"] = Token(), ["refresh_token"] = "already-rotated-fixture-rt",
            ["id_token"] = "fixture-id-token", ["chatgpt_account_id"] = "fixture-account"
        };
        var item = new JsonObject { ["platform"] = "openai", ["type"] = "oauth", ["credentials"] = credentials };
        string? Read()
        {
            using var doc = JsonDocument.Parse(item.ToJsonString());
            return AccountFileImport.ReadOAuthRefreshTime(doc.RootElement, now);
        }
        var batch = AccountFileImport.Parse(new JsonObject
        {
            ["type"] = "sub2api-data", ["version"] = 1, ["accounts"] = new JsonArray(item.DeepClone())
        }.ToJsonString());
        Check(batch.Entries.Count == 1 && batch.Problems.Count == 0, "Sub2API timestamp fixture imports");
        var native = JsonNode.Parse(batch.Entries[0].AuthJson)!.AsObject();
        Check(DateTimeOffset.Parse(native["last_refresh"]!.GetValue<string>()).ToUnixTimeSeconds() == issued,
            "legacy Sub2API import retains actual token age instead of forcing native refresh");
        foreach (var key in new[] { "access_token", "refresh_token", "id_token" })
            Check(native["tokens"]![key]!.GetValue<string>() == credentials[key]!.GetValue<string>(), "timestamp repair preserves " + key);
        Check(native["tokens"]!["account_id"]!.GetValue<string>() == "fixture-account", "timestamp repair preserves workspace");

        var original = now.AddDays(-2).ToString("O");
        credentials["last_refresh"] = original;
        Check(Read() == original, "nested Sub2API timestamp is preserved");
        item["last_refresh"] = now.AddHours(-1).ToString("O");
        Check(Read() == item["last_refresh"]!.GetValue<string>(), "top-level timestamp remains authoritative");
        item["last_refresh"] = "invalid-date";
        Check(Read() == original, "invalid outer timestamp cannot hide valid nested timestamp");
        credentials.Remove("last_refresh");
        Check(DateTimeOffset.Parse(Read()!).ToUnixTimeSeconds() == issued, "invalid timestamp falls back to iat");
        using (var doc = JsonDocument.Parse(native.ToJsonString()))
            Check(AccountFileImport.ReadOAuthRefreshTime(doc.RootElement, now) == native["last_refresh"]!.GetValue<string>(),
                "native auth timestamp is stable on reimport");

        claims["exp"] = now.AddHours(-1).ToUnixTimeSeconds();
        credentials["access_token"] = Token();
        Check(DateTimeOffset.Parse(Read()!).ToUnixTimeSeconds() == issued, "expired tokens keep original age");
        foreach (var invalid in new JsonNode?[] { null, JsonValue.Create("yesterday"), JsonValue.Create(long.MaxValue),
                     JsonValue.Create(0), JsonValue.Create(now.AddDays(1).ToUnixTimeSeconds()) })
        {
            claims["iat"] = invalid;
            credentials["access_token"] = Token();
            Check(Read() == null, "invalid or future iat never becomes a fictitious successful refresh");
        }
        credentials["access_token"] = "not-a-jwt";
        Check(Read() == null, "opaque token without timestamp still requires real refresh");
        Console.WriteLine("PASS: Sub2API OAuth timestamps, token preservation, native roundtrip, invalid and expired metadata");
    }
}
