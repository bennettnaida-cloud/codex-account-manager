using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

internal static class CodexEmptyCompanionSnapshotsTests
{
    internal const string DesktopUserAgent = "Codex Desktop/0.167.0-alpha.3.2 (Windows; x86_64)";

    internal static JsonObject EmptyOpen() => new()
    {
        ["type"] = "function_call_output", ["id"] = "fco-fixture-open",
        ["namespace"] = "agent", ["name"] = "codex_apps_open_page",
        ["output"] = "{\"page_id\":null}"
    };

    internal static JsonObject EmptySelected() => new()
    {
        ["type"] = "function_call_output", ["namespace"] = "agent",
        ["name"] = "codex_apps_selected_page_part_1_of_1",
        ["output"] = "Companion Page snapshot part 1 of 1:\n{\"page_id\":null,\"document_view\":null}"
    };

    internal static byte[] Request(params JsonNode[] items) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["model"] = "gpt-6-astra", ["input"] = new JsonArray(items), ["stream"] = true,
        ["metadata"] = new JsonObject { ["large"] = 9007199254740993L }
    }.ToJsonString());

    internal static void Validate()
    {
        var body = Request(new JsonObject { ["role"] = "user", ["content"] = "fixture" },
            EmptyOpen(), EmptySelected(),
            new JsonObject { ["type"] = "function_call", ["call_id"] = "call-real", ["name"] = "check", ["arguments"] = "{}" },
            new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call-real", ["output"] = "42" });
        var normalized = CodexEmptyCompanionSnapshots.Normalize(body, DesktopUserAgent, out var removed);
        using (var document = JsonDocument.Parse(normalized))
        {
            var input = document.RootElement.GetProperty("input");
            Require(removed == 2 && input.GetArrayLength() == 3 &&
                input[1].GetProperty("call_id").GetString() == "call-real" &&
                input[2].GetProperty("output").GetString() == "42" &&
                document.RootElement.GetProperty("metadata").GetProperty("large").GetInt64() == 9007199254740993L,
                "Bootstrap normalization changed real tool pairs or metadata.");
        }
        Require(ReferenceEquals(normalized, CodexEmptyCompanionSnapshots.Normalize(normalized, DesktopUserAgent, out removed)) && removed == 0,
            "Repeated normalization must be idempotent.");
        foreach (var userAgent in new[] { null, "another-client", "codex_cli_rs/0.163.0" })
            Require(ReferenceEquals(body, CodexEmptyCompanionSnapshots.Normalize(body, userAgent, out removed)) && removed == 0,
                "Other clients must remain unchanged.");

        var protectedItems = new List<JsonObject>();
        foreach (var pair in new (string Key, JsonNode? Value)[]
        {
            ("call_id", JsonValue.Create("call-real")), ("call_id", null), ("call_id", JsonValue.Create(123)),
            ("namespace", JsonValue.Create("functions")), ("name", JsonValue.Create("other_tool")),
            ("output", JsonValue.Create("{\"page_id\":\"real-page\"}")),
            ("output", JsonValue.Create("{\"page_id\":null,\"content\":\"keep\"}")),
            ("output", JsonValue.Create("{\"page_id\":\"real\",\"page_id\":null}")),
            ("output", new JsonObject { ["page_id"] = null }),
            ("content", JsonValue.Create("unknown field: retain"))
        })
        {
            var item = EmptyOpen(); item[pair.Key] = pair.Value; protectedItems.Add(item);
        }
        var selected = EmptySelected();
        selected["output"] = "Companion Page snapshot part 1 of 1:\n{\"page_id\":null,\"document_view\":{\"text\":\"keep\"}}";
        protectedItems.Add(selected);
        selected = EmptySelected(); selected["name"] = "codex_apps_selected_page_part_1_of_2";
        protectedItems.Add(selected);
        foreach (var item in protectedItems)
        {
            var original = Request(item);
            Require(ReferenceEquals(original, CodexEmptyCompanionSnapshots.Normalize(original, DesktopUserAgent, out removed)) && removed == 0,
                "A real, unknown or invalid result was discarded.");
        }
        foreach (var malformed in new[]
        {
            "{", "null", "{\"input\":\"text\"}",
            "{\"input\":[" + EmptyOpen().ToJsonString() + "],\"model\":\"first\",\"model\":\"second\"}",
            "{\"input\":[" + EmptyOpen().ToJsonString() + "],\"metadata\":{\"x\":1,\"x\":2}}"
        })
        {
            var original = Encoding.UTF8.GetBytes(malformed);
            Require(ReferenceEquals(original, CodexEmptyCompanionSnapshots.Normalize(original, DesktopUserAgent, out removed)) && removed == 0,
                "Ambiguous JSON must not be rewritten.");
        }
        var continuation = JsonNode.Parse(Encoding.UTF8.GetString(body))!.AsObject();
        continuation["previous_response_id"] = "resp-original";
        using var continued = JsonDocument.Parse(CodexEmptyCompanionSnapshots.Normalize(
            Encoding.UTF8.GetBytes(continuation.ToJsonString()), DesktopUserAgent, out removed));
        Require(removed == 2 && continued.RootElement.GetProperty("previous_response_id").GetString() == "resp-original",
            "The compatibility layer must preserve response lineage.");
        Console.WriteLine("PASS: empty Page bootstrap, real tool pairs, real Page content, unknown clients, duplicate JSON and continuation preservation");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
