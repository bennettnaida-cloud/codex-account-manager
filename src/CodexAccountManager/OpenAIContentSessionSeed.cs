using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

/// <summary>
/// Derives the same bounded, content-based OpenAI session seed used by sub2api when a
/// caller does not send an explicit session/conversation identifier. Only fields that are
/// normally stable across turns are included; the entire turn history is never used as a
/// sticky key.
/// </summary>
internal static class OpenAIContentSessionSeed
{
    internal const string Prefix = "compat_cs_";
    private const int MaximumMaterialCharacters = 64 * 1024;
    private const int MaximumCanonicalValueCharacters = 24 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 128
    };

    internal static string Derive(byte[]? body) =>
        body is { Length: > 0 } ? Derive(body.AsSpan()) : string.Empty;

    internal static string Derive(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray(), JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                // Go's gjson view used by sub2api effectively resolves one root field;
                // retaining the first occurrence avoids duplicate-field route changes.
                fields.TryAdd(property.Name, property.Value);
            }

            var material = new StringBuilder();
            AppendString(material, "model", GetString(fields, "model"));
            AppendArray(material, "tools", fields, "tools");
            AppendArray(material, "functions", fields, "functions");
            AppendString(material, "instructions", GetString(fields, "instructions"));

            if (fields.TryGetValue("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                AppendChatMessages(material, messages);
            }
            else if (fields.TryGetValue("input", out var input))
            {
                AppendResponsesInput(material, input);
            }

            if (material.Length == 0)
            {
                return string.Empty;
            }

            // Session stores should not receive multi-megabyte keys when a caller sends a
            // very large tool schema or prompt. Preserve deterministic behavior by hashing
            // only after the same canonical material has been assembled.
            if (material.Length > MaximumMaterialCharacters)
            {
                var digest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
                    .ToLowerInvariant();
                return Prefix + "sha256_" + digest;
            }
            return Prefix + material;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static void AppendChatMessages(StringBuilder material, JsonElement messages)
    {
        var systemPrefixOpen = true;
        var firstUserCaptured = false;
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                systemPrefixOpen = false;
                continue;
            }

            var role = GetString(message, "role");
            if (role is "system" or "developer" && systemPrefixOpen)
            {
                AppendJson(material, "system", GetProperty(message, "content"));
                continue;
            }

            if (role == "user")
            {
                systemPrefixOpen = false;
                if (!firstUserCaptured)
                {
                    AppendJson(material, "first_user", GetProperty(message, "content"));
                    firstUserCaptured = true;
                }
                continue;
            }

            systemPrefixOpen = false;
        }
    }

    private static void AppendResponsesInput(StringBuilder material, JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            AppendString(material, "input", input.GetString());
            return;
        }
        if (input.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var firstUserCaptured = false;
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = GetString(item, "role");
            if (role is "system" or "developer")
            {
                AppendJson(material, "system", GetProperty(item, "content"));
                continue;
            }
            if (role == "user" && !firstUserCaptured)
            {
                AppendJson(material, "first_user", GetProperty(item, "content"));
                firstUserCaptured = true;
                continue;
            }
            if (!firstUserCaptured && GetString(item, "type") == "input_text")
            {
                AppendString(material, "first_user", GetString(item, "text"));
                firstUserCaptured = true;
            }
        }
    }

    private static void AppendArray(
        StringBuilder material,
        string label,
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() == 0)
        {
            return;
        }
        AppendJson(material, label, value);
    }

    private static void AppendJson(StringBuilder material, string label, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return;
        }
        var normalized = Canonicalize(value);
        if (string.IsNullOrEmpty(normalized) || normalized is "[]" or "{}" or "null")
        {
            return;
        }
        AppendRaw(material, label, normalized);
    }

    private static void AppendString(StringBuilder material, string label, string? value)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrEmpty(normalized))
        {
            AppendRaw(material, label, normalized);
        }
    }

    private static void AppendRaw(StringBuilder material, string label, string value)
    {
        if (value.Length > MaximumCanonicalValueCharacters)
        {
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();
            value = "sha256_" + digest;
        }
        if (material.Length > 0)
        {
            material.Append('|');
        }
        // Keep the compact sub2api-compatible representation for ordinary values, but
        // escape every character that has a structural meaning in the material grammar.
        // Without this, for example, model="a|instructions=b" collides with
        // model="a" + instructions="b" and unrelated requests share one affinity key.
        material.Append(label).Append('=').Append(EscapeComponent(value));
    }

    private static string EscapeComponent(string value)
    {
        if (value.IndexOf('\\') < 0 && value.IndexOf('|') < 0 && value.IndexOf('=') < 0)
        {
            return value;
        }

        var escaped = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (character is '\\' or '|' or '=')
            {
                escaped.Append('\\');
            }
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    private static JsonElement GetProperty(JsonElement objectElement, string name) =>
        objectElement.ValueKind == JsonValueKind.Object &&
        objectElement.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string? GetString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name) =>
        fields.TryGetValue(name, out var value) ? GetString(value) : null;

    private static string? GetString(JsonElement objectElement, string name) =>
        GetString(GetProperty(objectElement, name));

    private static string? GetString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    internal static void Validate()
    {
        var chat1 = Derive(Encoding.UTF8.GetBytes(
            "{\"model\":\"gpt-test\",\"messages\":[" +
            "{\"role\":\"system\",\"content\":\"rules\"}," +
            "{\"role\":\"user\",\"content\":\"first\"}," +
            "{\"role\":\"assistant\",\"content\":\"answer\"}]}"));
        var chat2 = Derive(Encoding.UTF8.GetBytes(
            "{\"model\":\"gpt-test\",\"messages\":[" +
            "{\"role\":\"system\",\"content\":\"rules\"}," +
            "{\"role\":\"user\",\"content\":\"first\"}," +
            "{\"role\":\"assistant\",\"content\":\"different\"}," +
            "{\"role\":\"user\",\"content\":\"next\"}]}"));
        if (chat1 != chat2 || !chat1.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Content session seed was not stable across turns.");
        }

        var reordered = Derive(Encoding.UTF8.GetBytes(
            "{\"tools\":[{\"b\":2,\"a\":1}],\"model\":\"gpt-test\"}"));
        var canonical = Derive(Encoding.UTF8.GetBytes(
            "{\"model\":\"gpt-test\",\"tools\":[{\"a\":1,\"b\":2}]}"));
        if (reordered != canonical || Derive(Encoding.UTF8.GetBytes("{}")) != string.Empty)
        {
            throw new InvalidOperationException("Content session seed canonicalization failed.");
        }

        var delimiterBearingValue = Derive(Encoding.UTF8.GetBytes(
            "{\"model\":\"gpt-test|instructions=rules\"}"));
        var separateFields = Derive(Encoding.UTF8.GetBytes(
            "{\"model\":\"gpt-test\",\"instructions\":\"rules\"}"));
        if (delimiterBearingValue == separateFields)
        {
            throw new InvalidOperationException(
                "Content session seed component escaping allowed a delimiter collision.");
        }

        var responsesInput = Derive(Encoding.UTF8.GetBytes(
            "{\"model\":\"gpt-test\",\"input\":[" +
            "{\"type\":\"input_text\",\"text\":\"hello\"}]}"));
        if (!responsesInput.Contains("first_user=hello", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Responses input content fallback failed.");
        }
    }
}
