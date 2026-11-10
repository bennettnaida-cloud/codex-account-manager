using System.Text.Json;

namespace CodexAccountManager;

// Desktop's two null Page snapshots are context notifications, not tool results.
// HTTP relays reject them because they have no call_id. Never invent an identifier
// or discard a real/unknown tool result to make a request pass validation.
internal static class CodexEmptyCompanionSnapshots
{
    internal static byte[] Normalize(byte[] body, string? userAgent, out int removed)
    {
        removed = 0;
        if (userAgent?.StartsWith("Codex Desktop/", StringComparison.Ordinal) != true ||
            body.Length == 0)
            return body;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 256 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
                return body;
            var count = input.EnumerateArray().Count(IsEmptySnapshot);
            if (count == 0 || !HasUniqueMembers(root)) return body;

            using var output = new MemoryStream(body.Length);
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (!property.NameEquals("input"))
                    {
                        property.WriteTo(writer);
                        continue;
                    }
                    writer.WriteStartArray("input");
                    foreach (var item in input.EnumerateArray())
                        if (!IsEmptySnapshot(item)) item.WriteTo(writer);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            removed = count;
            return output.ToArray();
        }
        catch (JsonException)
        {
            // Preserve malformed/ambiguous requests for the upstream's normal validation.
            return body;
        }
    }

    private static bool IsEmptySnapshot(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            Text(item, "type") != "function_call_output" || Text(item, "namespace") != "agent")
            return false;
        // Real call associations, including malformed/null identifiers, remain untouched.
        if (item.TryGetProperty("call_id", out var callId) &&
            (callId.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(callId.GetString())))
            return false;
        foreach (var property in item.EnumerateObject())
            if (property.Name is not ("type" or "id" or "call_id" or "name" or "namespace" or
                "output" or "internal_chat_message_metadata_passthrough"))
                return false;
        var output = Text(item, "output");
        if (output == null || output.Length > 4096) return false;
        var selected = Text(item, "name") == "codex_apps_selected_page_part_1_of_1";
        if (selected)
        {
            const string prefix = "Companion Page snapshot part 1 of 1:\n";
            if (!output.StartsWith(prefix, StringComparison.Ordinal)) return false;
            output = output[prefix.Length..];
        }
        else if (Text(item, "name") != "codex_apps_open_page") return false;
        try
        {
            using var snapshot = JsonDocument.Parse(output);
            var value = snapshot.RootElement;
            return value.ValueKind == JsonValueKind.Object && HasUniqueMembers(value) &&
                value.EnumerateObject().Count() == (selected ? 2 : 1) &&
                value.TryGetProperty("page_id", out var page) && page.ValueKind == JsonValueKind.Null &&
                (!selected || value.TryGetProperty("document_view", out var view) && view.ValueKind == JsonValueKind.Null);
        }
        catch (JsonException) { return false; }
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static bool HasUniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !HasUniqueMembers(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (!HasUniqueMembers(item)) return false;
        return true;
    }
}
