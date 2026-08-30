using System.Collections.Specialized;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

/// <summary>
/// Controls how the local gateway treats Codex client identity metadata.
/// GatewayDefault preserves the historical Account Manager behavior, while
/// Passthrough corresponds to an unmodified client fingerprint.
/// </summary>
internal enum CodexFingerprintMode
{
    GatewayDefault = 0,
    Passthrough = 1,
    Device = 2,
    Session = 3,
    Full = 4
}

/// <summary>
/// Immutable identifiers for one upstream attempt. A single instance must be used for
/// both the HTTP headers and request body so the per-attempt turn identity stays coherent.
/// </summary>
internal sealed record CodexFingerprintPlan(
    string AccountKey,
    CodexFingerprintMode Mode,
    string? InstallationId,
    string? SessionId,
    string? ThreadId,
    string? TurnId,
    string? WindowId,
    long? TurnStartedAtUnixMs)
{
    internal bool ShouldForwardClientMetadata => Mode != CodexFingerprintMode.GatewayDefault;

    internal bool HasConvergedIdentifiers =>
        Mode is CodexFingerprintMode.Device or CodexFingerprintMode.Session or CodexFingerprintMode.Full &&
        !string.IsNullOrWhiteSpace(InstallationId);

    internal bool MatchesAccount(string? accountKey) =>
        !string.IsNullOrWhiteSpace(accountKey) &&
        AccountKey.Equals(accountKey.Trim(), StringComparison.Ordinal);
}

/// <summary>
/// Pure transformations for account-scoped Codex identity convergence. This class does
/// not select accounts, read settings, or decide which client headers are admitted by the
/// gateway; callers retain those policy responsibilities.
/// </summary>
internal static class CodexFingerprintConvergence
{
    internal const string GatewayDefaultValue = "gateway_default";
    internal const string PassthroughValue = "off";
    internal const string PassthroughAliasValue = "passthrough";
    internal const string DeviceValue = "device";
    internal const string SessionValue = "session";
    internal const string FullValue = "full";

    private const string InstallationDerivationDomain = "sub2api:codex-install-id:v2:";
    private const string SessionDerivationDomain = "sub2api:codex-session-id:v2:";
    private const string ThreadDerivationDomain = "sub2api:codex-thread-id:v2:";

    private static readonly JsonDocumentOptions JsonReadOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 256
    };

    internal static CodexFingerprintMode ParseMode(string? value)
    {
        var candidate = value?.Trim();
        return candidate switch
        {
            GatewayDefaultValue => CodexFingerprintMode.GatewayDefault,
            PassthroughValue or PassthroughAliasValue => CodexFingerprintMode.Passthrough,
            DeviceValue => CodexFingerprintMode.Device,
            SessionValue => CodexFingerprintMode.Session,
            FullValue => CodexFingerprintMode.Full,
            _ => CodexFingerprintMode.GatewayDefault
        };
    }

    internal static string ToSettingValue(CodexFingerprintMode mode) => mode switch
    {
        CodexFingerprintMode.Passthrough => PassthroughValue,
        CodexFingerprintMode.Device => DeviceValue,
        CodexFingerprintMode.Session => SessionValue,
        CodexFingerprintMode.Full => FullValue,
        _ => GatewayDefaultValue
    };

    internal static bool RequiresSeed(CodexFingerprintMode mode) =>
        mode is CodexFingerprintMode.Device or CodexFingerprintMode.Session or CodexFingerprintMode.Full;

    /// <summary>
    /// Accepts only the canonical lowercase D representation of a non-empty UUID. The
    /// UUID version is deliberately not restricted; newly-created seeds are UUIDv4, while
    /// valid imported seeds from another implementation remain usable.
    /// </summary>
    internal static bool TryNormalizeSeed(string? value, out string normalized)
    {
        normalized = "";
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Guid.TryParseExact(candidate, "D", out var parsed) ||
            parsed == Guid.Empty ||
            !candidate.Equals(parsed.ToString("D"), StringComparison.Ordinal))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    internal static string CreateSeed() => Guid.NewGuid().ToString("D");

    /// <summary>
    /// Derives a deterministic RFC 4122 UUIDv4 from arbitrary UTF-8 material.
    /// Formatting is explicitly network-order; Guid(byte[]) must not be used here because
    /// its default mixed-endian interpretation would produce different identifiers.
    /// </summary>
    internal static string DeriveStableUuid(string material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return hex[..8] + "-" +
               hex.Substring(8, 4) + "-" +
               hex.Substring(12, 4) + "-" +
               hex.Substring(16, 4) + "-" +
               hex.Substring(20, 12);
    }

    internal static string ExtractClientSessionId(NameValueCollection? headers)
    {
        if (headers == null)
        {
            return "";
        }

        var hyphenated = headers["session-id"]?.Trim();
        return !string.IsNullOrWhiteSpace(hyphenated)
            ? hyphenated
            : headers["session_id"]?.Trim() ?? "";
    }

    /// <summary>
    /// Creates a plan for one selected-account attempt. Missing account identity or an
    /// invalid seed degrades a convergence mode to a no-rewrite plan instead of inventing
    /// a transient seed in the request path.
    /// </summary>
    internal static CodexFingerprintPlan CreatePlan(
        string? accountKey,
        string? seed,
        string? clientSessionId,
        CodexFingerprintMode mode,
        string? preferredInstallationId = null) =>
        CreatePlan(
            accountKey,
            seed,
            clientSessionId,
            mode,
            preferredInstallationId,
            DateTimeOffset.UtcNow,
            static () => Guid.CreateVersion7().ToString("D"));

    private static CodexFingerprintPlan CreatePlan(
        string? accountKey,
        string? seed,
        string? clientSessionId,
        CodexFingerprintMode mode,
        string? preferredInstallationId,
        DateTimeOffset nowUtc,
        Func<string> createTurnId)
    {
        ArgumentNullException.ThrowIfNull(createTurnId);
        mode = NormalizeMode(mode);
        var normalizedAccountKey = accountKey?.Trim() ?? "";
        if (!RequiresSeed(mode) ||
            normalizedAccountKey.Length == 0 ||
            !TryNormalizeSeed(seed, out var normalizedSeed))
        {
            return EmptyPlan(normalizedAccountKey, mode);
        }

        var installationId = preferredInstallationId?.Trim();
        if (string.IsNullOrWhiteSpace(installationId))
        {
            installationId = DeriveStableUuid(InstallationDerivationDomain + normalizedSeed);
        }

        if (mode == CodexFingerprintMode.Device)
        {
            return new CodexFingerprintPlan(
                normalizedAccountKey,
                mode,
                installationId,
                SessionId: null,
                ThreadId: null,
                TurnId: null,
                WindowId: null,
                TurnStartedAtUnixMs: null);
        }

        var sessionId = DeriveStableUuid(SessionDerivationDomain + normalizedSeed);
        var normalizedClientSessionId = clientSessionId?.Trim() ?? "";
        var threadId = mode == CodexFingerprintMode.Full
            ? sessionId
            : normalizedClientSessionId.Length == 0
                ? sessionId
                : DeriveStableUuid(
                    ThreadDerivationDomain + normalizedSeed + ":" + normalizedClientSessionId);
        var turnId = createTurnId();
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new InvalidOperationException("The Codex fingerprint turn-id factory returned an empty value.");
        }

        return new CodexFingerprintPlan(
            normalizedAccountKey,
            mode,
            installationId,
            sessionId,
            threadId,
            turnId.Trim(),
            threadId + ":0",
            nowUtc.ToUnixTimeMilliseconds());
    }

    private static CodexFingerprintMode NormalizeMode(CodexFingerprintMode mode) => mode switch
    {
        CodexFingerprintMode.GatewayDefault or
        CodexFingerprintMode.Passthrough or
        CodexFingerprintMode.Device or
        CodexFingerprintMode.Session or
        CodexFingerprintMode.Full => mode,
        _ => CodexFingerprintMode.GatewayDefault
    };

    private static CodexFingerprintPlan EmptyPlan(string accountKey, CodexFingerprintMode mode) =>
        new(
            accountKey,
            mode,
            InstallationId: null,
            SessionId: null,
            ThreadId: null,
            TurnId: null,
            WindowId: null,
            TurnStartedAtUnixMs: null);

    /// <summary>
    /// Applies the converged fields after the gateway has copied its admitted incoming
    /// headers. Authentication and account-selection headers are intentionally untouched.
    /// </summary>
    internal static bool ApplyHeaders(HttpRequestMessage request, CodexFingerprintPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (plan is not { HasConvergedIdentifiers: true })
        {
            return false;
        }

        SetHeader(request.Headers, "x-codex-installation-id", plan.InstallationId!);
        if (plan.Mode == CodexFingerprintMode.Device)
        {
            RewriteTurnMetadataHeader(request.Headers, plan);
            return true;
        }

        SetHeader(request.Headers, "x-codex-window-id", plan.WindowId!);
        SetHeader(request.Headers, "x-client-request-id", plan.ThreadId!);
        SetHeader(request.Headers, "session-id", plan.SessionId!);
        SetHeader(request.Headers, "session_id", plan.SessionId!);
        SetHeader(request.Headers, "thread-id", plan.ThreadId!);
        RewriteTurnMetadataHeader(request.Headers, plan);
        return true;
    }

    private static void RewriteTurnMetadataHeader(
        HttpRequestHeaders headers,
        CodexFingerprintPlan plan)
    {
        if (!headers.TryGetValues("x-codex-turn-metadata", out var values))
        {
            return;
        }

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        SetHeader(headers, "x-codex-turn-metadata", RewriteEmbeddedTurnMetadata(raw, plan));
    }

    private static void SetHeader(HttpRequestHeaders headers, string name, string value)
    {
        headers.Remove(name);
        if (!headers.TryAddWithoutValidation(name, value))
        {
            throw new InvalidOperationException($"Unable to set the converged Codex header {name}.");
        }
    }

    /// <summary>
    /// Rewrites only a JSON object request. Malformed JSON and non-object roots are left
    /// untouched. The returned array is the original instance when no rewrite occurred.
    /// </summary>
    internal static byte[] RewriteRequestBody(
        byte[] body,
        CodexFingerprintPlan? plan,
        out bool modified)
    {
        ArgumentNullException.ThrowIfNull(body);
        modified = false;
        if (body.Length == 0 || plan is not { HasConvergedIdentifiers: true })
        {
            return body;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, JsonReadOptions);
        }
        catch (JsonException)
        {
            return body;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return body;
            }

            var originalBodySessionId = CaptureOriginalBodySessionId(document.RootElement);
            var hasClientMetadata = false;
            var initialCapacity = body.Length <= int.MaxValue - 1024
                ? body.Length + 1024
                : body.Length;
            using var output = new MemoryStream(initialCapacity);
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("client_metadata"))
                    {
                        hasClientMetadata = true;
                        writer.WritePropertyName(property.Name);
                        WriteClientMetadata(writer, property.Value, plan);
                        continue;
                    }

                    if (property.NameEquals("prompt_cache_key") &&
                        ShouldRewritePromptCacheKey(property.Value, originalBodySessionId, plan))
                    {
                        writer.WriteString(property.Name, plan.SessionId);
                        continue;
                    }

                    property.WriteTo(writer);
                }

                if (!hasClientMetadata)
                {
                    writer.WritePropertyName("client_metadata");
                    WriteClientMetadata(writer, existing: null, plan);
                }
                writer.WriteEndObject();
            }

            modified = true;
            return output.ToArray();
        }
    }

    private static string CaptureOriginalBodySessionId(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals("client_metadata"))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("session_id", out var session) &&
                session.ValueKind == JsonValueKind.String)
            {
                return session.GetString()?.Trim() ?? "";
            }
            return "";
        }
        return "";
    }

    private static bool ShouldRewritePromptCacheKey(
        JsonElement promptCacheKey,
        string originalBodySessionId,
        CodexFingerprintPlan plan)
    {
        if (plan.Mode is not (CodexFingerprintMode.Session or CodexFingerprintMode.Full) ||
            string.IsNullOrWhiteSpace(plan.SessionId) ||
            originalBodySessionId.Length == 0 ||
            promptCacheKey.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = promptCacheKey.GetString();
        return !string.IsNullOrWhiteSpace(value) &&
               value.Equals(originalBodySessionId, StringComparison.Ordinal);
    }

    private static void WriteClientMetadata(
        Utf8JsonWriter writer,
        JsonElement? existing,
        CodexFingerprintPlan plan)
    {
        writer.WriteStartObject();
        if (existing is { ValueKind: JsonValueKind.Object } objectElement)
        {
            foreach (var property in objectElement.EnumerateObject())
            {
                if (IsFlatClientMetadataOverride(property.Name, plan.Mode))
                {
                    continue;
                }

                if (property.NameEquals("x-codex-turn-metadata") &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { Length: > 0 } embedded)
                {
                    writer.WriteString(
                        property.Name,
                        RewriteEmbeddedTurnMetadata(embedded, plan));
                    continue;
                }

                property.WriteTo(writer);
            }
        }

        writer.WriteString("x-codex-installation-id", plan.InstallationId);
        if (plan.Mode is CodexFingerprintMode.Session or CodexFingerprintMode.Full)
        {
            writer.WriteString("session_id", plan.SessionId);
            writer.WriteString("thread_id", plan.ThreadId);
            writer.WriteString("turn_id", plan.TurnId);
            writer.WriteString("x-codex-window-id", plan.WindowId);
        }
        writer.WriteEndObject();
    }

    private static bool IsFlatClientMetadataOverride(string propertyName, CodexFingerprintMode mode)
    {
        if (propertyName.Equals("x-codex-installation-id", StringComparison.Ordinal))
        {
            return true;
        }

        return mode is CodexFingerprintMode.Session or CodexFingerprintMode.Full &&
               propertyName is "session_id" or "thread_id" or "turn_id" or "x-codex-window-id";
    }

    private static string RewriteEmbeddedTurnMetadata(
        string raw,
        CodexFingerprintPlan plan)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(raw, JsonReadOptions);
        }
        catch (JsonException)
        {
            // An invalid embedded value is rebuilt below from the converged fields.
        }

        try
        {
            using var output = new MemoryStream(Math.Max(128, raw.Length + 192));
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                if (document?.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (!IsEmbeddedTurnMetadataOverride(property.Name, plan.Mode))
                        {
                            property.WriteTo(writer);
                        }
                    }
                }

                writer.WriteString("installation_id", plan.InstallationId);
                if (plan.Mode is CodexFingerprintMode.Session or CodexFingerprintMode.Full)
                {
                    writer.WriteString("session_id", plan.SessionId);
                    writer.WriteString("thread_id", plan.ThreadId);
                    writer.WriteString("turn_id", plan.TurnId);
                    writer.WriteString("window_id", plan.WindowId);
                    writer.WriteNumber("turn_started_at_unix_ms", plan.TurnStartedAtUnixMs!.Value);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static bool IsEmbeddedTurnMetadataOverride(
        string propertyName,
        CodexFingerprintMode mode)
    {
        if (propertyName.Equals("installation_id", StringComparison.Ordinal))
        {
            return true;
        }

        return mode is CodexFingerprintMode.Session or CodexFingerprintMode.Full &&
               propertyName is
                   "session_id" or
                   "thread_id" or
                   "turn_id" or
                   "window_id" or
                   "turn_started_at_unix_ms";
    }

    internal static void Validate()
    {
        const string seed = "11111111-1111-4111-8111-111111111111";
        const string fixedTurnId = "0195d40b-8f8e-7a31-9d2d-28df78f73420";
        var fixedNow = DateTimeOffset.FromUnixTimeMilliseconds(1_772_345_678_901L);

        Require(ParseMode(null) == CodexFingerprintMode.GatewayDefault, "missing mode");
        Require(ParseMode("invalid") == CodexFingerprintMode.GatewayDefault, "invalid mode");
        Require(ParseMode("off") == CodexFingerprintMode.Passthrough, "off alias");
        Require(ParseMode(DeviceValue) == CodexFingerprintMode.Device, "device mode");
        Require(ParseMode(SessionValue) == CodexFingerprintMode.Session, "session mode");
        Require(ParseMode(FullValue) == CodexFingerprintMode.Full, "full mode");
        Require(ParseMode("SESSION") == CodexFingerprintMode.GatewayDefault, "mode casing");
        Require(ToSettingValue(CodexFingerprintMode.Passthrough) == PassthroughValue, "passthrough setting");

        Require(TryNormalizeSeed(" " + seed + " ", out var normalizedSeed) && normalizedSeed == seed, "canonical seed");
        Require(
            !TryNormalizeSeed("AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA", out _),
            "uppercase seed rejection");
        Require(!TryNormalizeSeed(Guid.Empty.ToString("D"), out _), "empty seed rejection");
        Require(Guid.TryParseExact(CreateSeed(), "D", out var createdSeed) && createdSeed != Guid.Empty, "seed creation");

        var installation = DeriveStableUuid(InstallationDerivationDomain + seed);
        var session = DeriveStableUuid(SessionDerivationDomain + seed);
        var thread = DeriveStableUuid(ThreadDerivationDomain + seed + ":client-session-abc");
        Require(installation == "3a479c33-60a4-4c91-8479-62da0fecf605", "installation derivation vector");
        Require(session == "4065c8ec-c2ce-48bd-9198-53c4c5e30d6e", "session derivation vector");
        Require(thread == "027e3d39-f748-434e-a3d0-40a6a48d2bea", "thread derivation vector");
        Require(installation[14] == '4' && installation[19] is '8' or '9' or 'a' or 'b', "stable UUID bits");

        var gatewayDefault = CreatePlan("account-a", seed, "client", CodexFingerprintMode.GatewayDefault);
        var passthrough = CreatePlan("account-a", seed, "client", CodexFingerprintMode.Passthrough);
        Require(!gatewayDefault.ShouldForwardClientMetadata && !gatewayDefault.HasConvergedIdentifiers, "gateway default plan");
        Require(passthrough.ShouldForwardClientMetadata && !passthrough.HasConvergedIdentifiers, "passthrough plan");
        var invalidSeedPlan = CreatePlan("account-a", "invalid", "client", CodexFingerprintMode.Session);
        Require(invalidSeedPlan.ShouldForwardClientMetadata && !invalidSeedPlan.HasConvergedIdentifiers, "invalid seed plan");

        var device = CreatePlan(
            "account-a",
            seed,
            "client-session-abc",
            CodexFingerprintMode.Device,
            preferredInstallationId: "real-device-id",
            fixedNow,
            static () => fixedTurnId);
        Require(device.InstallationId == "real-device-id" && device.SessionId == null, "device plan");

        var sessionPlan = CreatePlan(
            "account-a",
            seed,
            "client-session-abc",
            CodexFingerprintMode.Session,
            preferredInstallationId: null,
            fixedNow,
            static () => fixedTurnId);
        Require(sessionPlan.InstallationId == installation, "session installation");
        Require(sessionPlan.SessionId == session, "session identity");
        Require(sessionPlan.ThreadId == thread, "session thread");
        Require(sessionPlan.WindowId == thread + ":0", "session window");
        Require(sessionPlan.TurnId == fixedTurnId && sessionPlan.TurnStartedAtUnixMs == fixedNow.ToUnixTimeMilliseconds(), "session turn");
        Require(sessionPlan.MatchesAccount("account-a") && !sessionPlan.MatchesAccount("account-b"), "plan account binding");

        var otherClientPlan = CreatePlan(
            "account-a",
            seed,
            "client-session-other",
            CodexFingerprintMode.Session,
            preferredInstallationId: null,
            fixedNow,
            static () => fixedTurnId);
        Require(otherClientPlan.SessionId == sessionPlan.SessionId && otherClientPlan.ThreadId != sessionPlan.ThreadId, "per-client thread");
        var missingClientPlan = CreatePlan(
            "account-a",
            seed,
            "",
            CodexFingerprintMode.Session,
            preferredInstallationId: null,
            fixedNow,
            static () => fixedTurnId);
        Require(missingClientPlan.ThreadId == missingClientPlan.SessionId, "missing client session fallback");
        var fullPlan = CreatePlan(
            "account-a",
            seed,
            "ignored-client-session",
            CodexFingerprintMode.Full,
            preferredInstallationId: null,
            fixedNow,
            static () => fixedTurnId);
        Require(fullPlan.ThreadId == fullPlan.SessionId && fullPlan.WindowId == fullPlan.SessionId + ":0", "full plan");
        var liveTurnPlan = CreatePlan("account-a", seed, "client", CodexFingerprintMode.Session);
        Require(liveTurnPlan.TurnId is { Length: 36 } && liveTurnPlan.TurnId[14] == '7', "UUIDv7 turn");

        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/responses"))
        {
            request.Headers.TryAddWithoutValidation("x-codex-installation-id", "client-install");
            request.Headers.TryAddWithoutValidation("session-id", "client-session");
            request.Headers.TryAddWithoutValidation("x-codex-window-id", "client-window:0");
            request.Headers.TryAddWithoutValidation(
                "x-codex-turn-metadata",
                "{\"installation_id\":\"client-install\",\"session_id\":\"client-session\",\"sandbox\":\"keep\"}");
            Require(ApplyHeaders(request, sessionPlan), "session header rewrite");
            Require(ReadHeader(request.Headers, "x-codex-installation-id") == sessionPlan.InstallationId, "header installation");
            Require(ReadHeader(request.Headers, "session-id") == sessionPlan.SessionId, "hyphenated session header");
            Require(ReadHeader(request.Headers, "session_id") == sessionPlan.SessionId, "underscored session header");
            Require(ReadHeader(request.Headers, "thread-id") == sessionPlan.ThreadId, "thread header");
            Require(ReadHeader(request.Headers, "x-client-request-id") == sessionPlan.ThreadId, "client request header");
            Require(ReadHeader(request.Headers, "x-codex-window-id") == sessionPlan.WindowId, "window header");
            using var metadata = JsonDocument.Parse(ReadHeader(request.Headers, "x-codex-turn-metadata")!);
            Require(metadata.RootElement.GetProperty("turn_id").GetString() == sessionPlan.TurnId, "header turn");
            Require(metadata.RootElement.GetProperty("sandbox").GetString() == "keep", "header metadata preservation");
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/responses"))
        {
            request.Headers.TryAddWithoutValidation("session-id", "client-session");
            request.Headers.TryAddWithoutValidation("x-codex-window-id", "client-window:0");
            Require(ApplyHeaders(request, device), "device header rewrite");
            Require(ReadHeader(request.Headers, "session-id") == "client-session", "device keeps session");
            Require(ReadHeader(request.Headers, "x-codex-window-id") == "client-window:0", "device keeps window");
            Require(!request.Headers.Contains("x-codex-turn-metadata"), "missing header metadata remains missing");
        }

        var originalBody = Encoding.UTF8.GetBytes(
            "{\"model\":\"gpt-test\",\"prompt_cache_key\":\"body-session\",\"client_metadata\":{" +
            "\"x-codex-installation-id\":\"client-install\",\"session_id\":\" body-session \"," +
            "\"trace\":\"keep\",\"x-codex-turn-metadata\":\"{\\\"installation_id\\\":\\\"client-install\\\"," +
            "\\\"session_id\\\":\\\"body-session\\\",\\\"sandbox\\\":\\\"keep\\\"}\"},\"input\":[]}");
        var rewrittenBody = RewriteRequestBody(originalBody, sessionPlan, out var bodyModified);
        Require(bodyModified && !ReferenceEquals(originalBody, rewrittenBody), "body rewrite result");
        using (var bodyDocument = JsonDocument.Parse(rewrittenBody))
        {
            var root = bodyDocument.RootElement;
            var clientMetadata = root.GetProperty("client_metadata");
            Require(root.GetProperty("prompt_cache_key").GetString() == sessionPlan.SessionId, "default prompt cache key");
            Require(clientMetadata.GetProperty("x-codex-installation-id").GetString() == sessionPlan.InstallationId, "body installation");
            Require(clientMetadata.GetProperty("session_id").GetString() == sessionPlan.SessionId, "body session");
            Require(clientMetadata.GetProperty("thread_id").GetString() == sessionPlan.ThreadId, "body thread");
            Require(clientMetadata.GetProperty("turn_id").GetString() == sessionPlan.TurnId, "body turn");
            Require(clientMetadata.GetProperty("x-codex-window-id").GetString() == sessionPlan.WindowId, "body window");
            Require(clientMetadata.GetProperty("trace").GetString() == "keep", "body metadata preservation");
            using var embedded = JsonDocument.Parse(clientMetadata.GetProperty("x-codex-turn-metadata").GetString()!);
            Require(embedded.RootElement.GetProperty("turn_id").GetString() == sessionPlan.TurnId, "embedded turn consistency");
            Require(embedded.RootElement.GetProperty("turn_started_at_unix_ms").GetInt64() == sessionPlan.TurnStartedAtUnixMs, "embedded time consistency");
            Require(embedded.RootElement.GetProperty("sandbox").GetString() == "keep", "embedded metadata preservation");
        }

        var customCacheBody = Encoding.UTF8.GetBytes(
            "{\"prompt_cache_key\":\"explicit-cache\",\"client_metadata\":{\"session_id\":\"body-session\"}}");
        var customCacheRewritten = RewriteRequestBody(customCacheBody, sessionPlan, out var customModified);
        Require(customModified, "custom cache body rewrite");
        using (var customDocument = JsonDocument.Parse(customCacheRewritten))
        {
            Require(customDocument.RootElement.GetProperty("prompt_cache_key").GetString() == "explicit-cache", "custom prompt cache preservation");
        }

        var absentMetadataBody = Encoding.UTF8.GetBytes("{\"model\":\"gpt-test\",\"input\":[]}");
        var absentMetadataRewritten = RewriteRequestBody(absentMetadataBody, device, out var absentMetadataModified);
        Require(absentMetadataModified, "missing client metadata injection");
        using (var absentMetadataDocument = JsonDocument.Parse(absentMetadataRewritten))
        {
            var metadata = absentMetadataDocument.RootElement.GetProperty("client_metadata");
            Require(metadata.GetProperty("x-codex-installation-id").GetString() == device.InstallationId, "device body installation");
            Require(!metadata.TryGetProperty("session_id", out _), "device body keeps session absent");
            Require(!metadata.TryGetProperty("x-codex-turn-metadata", out _), "missing embedded metadata remains missing");
        }

        var malformedEmbeddedBody = Encoding.UTF8.GetBytes(
            "{\"client_metadata\":{\"session_id\":\"body-session\",\"x-codex-turn-metadata\":\"{broken\"}}");
        var malformedEmbeddedRewritten = RewriteRequestBody(malformedEmbeddedBody, sessionPlan, out var malformedEmbeddedModified);
        Require(malformedEmbeddedModified, "malformed embedded body rewrite");
        using (var malformedDocument = JsonDocument.Parse(malformedEmbeddedRewritten))
        {
            var embeddedRaw = malformedDocument.RootElement
                .GetProperty("client_metadata")
                .GetProperty("x-codex-turn-metadata")
                .GetString();
            using var rebuilt = JsonDocument.Parse(embeddedRaw!);
            Require(rebuilt.RootElement.GetProperty("turn_id").GetString() == sessionPlan.TurnId, "malformed metadata rebuild");
        }

        var scalarBody = Encoding.UTF8.GetBytes("[]");
        var scalarResult = RewriteRequestBody(scalarBody, sessionPlan, out var scalarModified);
        Require(!scalarModified && ReferenceEquals(scalarBody, scalarResult), "non-object body");
        var passthroughResult = RewriteRequestBody(originalBody, passthrough, out var passthroughModified);
        Require(!passthroughModified && ReferenceEquals(originalBody, passthroughResult), "passthrough body");

        const string otherSeed = "22222222-2222-4222-8222-222222222222";
        var retryPlan = CreatePlan(
            "account-b",
            otherSeed,
            "client-session-abc",
            CodexFingerprintMode.Full,
            preferredInstallationId: null,
            fixedNow,
            static () => fixedTurnId);
        var retryBody = RewriteRequestBody(originalBody, retryPlan, out var retryModified);
        var retryJson = Encoding.UTF8.GetString(retryBody);
        Require(retryModified && retryJson.Contains(retryPlan.InstallationId!, StringComparison.Ordinal), "retry account rewrite");
        Require(!retryJson.Contains(sessionPlan.InstallationId!, StringComparison.Ordinal), "retry account isolation");
    }

    private static string? ReadHeader(HttpRequestHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static void Require(bool condition, string subject)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Codex fingerprint convergence validation failed: " + subject + ".");
        }
    }
}
