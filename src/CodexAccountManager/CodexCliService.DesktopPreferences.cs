namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    private const string DesktopReasoningEffortsKey = "enabled-reasoning-efforts";
    // Persistent is a separate native mode; the six normal levels include max and ultra.
    internal const string DefaultDesktopReasoningEffortsToml =
        "[\"low\", \"medium\", \"high\", \"xhigh\", \"max\", \"ultra\", \"persistent\"]";

    internal static string ApplyDesktopReasoningDefaults(string config)
    {
        var inDesktop = false;
        foreach (var line in config.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (IsTomlTableHeader(trimmed))
            {
                inDesktop = trimmed.Equals("[desktop]", StringComparison.OrdinalIgnoreCase);
            }
            else if (inDesktop &&
                     (TomlKeyEquals(trimmed, DesktopReasoningEffortsKey) ||
                      TomlKeyEquals(trimmed, "\"" + DesktopReasoningEffortsKey + "\"") ||
                      TomlKeyEquals(trimmed, "'" + DesktopReasoningEffortsKey + "'")))
            {
                // A user's explicit subset (including an empty/multiline array) wins.
                return config;
            }
        }

        return UpsertTomlSectionRawValue(
            config, "desktop", DesktopReasoningEffortsKey, DefaultDesktopReasoningEffortsToml);
    }

    private static void ValidateDesktopReasoningPreferences()
    {
        var apiAccount = new AccountRecord
        {
            ApiProviderName = "test",
            ApiBaseUrl = "https://example.invalid",
            ApiModel = CompatibleApiDefaultModel,
            ApiWireApi = "responses"
        };
        var defaultLine = DesktopReasoningEffortsKey + " = " + DefaultDesktopReasoningEffortsToml;
        var generated = new[]
        {
            AccountStore.BuildOfficialOAuthConfig(),
            AccountStore.BuildAccessTokenConfig(),
            AccountStore.BuildCompatibleApiConfig(apiAccount),
            ProjectOfficialOAuthConfigText(""),
            ProjectWindowsClientConfigText(""),
            ProjectCompatibleApiConfigText("", apiAccount)
        };
        foreach (var config in generated)
        {
            var lines = config.Replace("\r\n", "\n").Split('\n');
            var start = Array.FindIndex(lines, line => line.Trim() == "[desktop]");
            var end = start < 0 ? 0 : Array.FindIndex(lines, start + 1, line => IsTomlTableHeader(line.Trim()));
            if (end < 0) end = lines.Length;
            if (!TomlSectionRawValueMatches(
                    lines, start + 1, end, DesktopReasoningEffortsKey,
                    DefaultDesktopReasoningEffortsToml, sensitive: false) ||
                !ApplyDesktopReasoningDefaults(config).Equals(config, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Desktop reasoning defaults must be scoped and idempotent for every account kind.");
            }
        }

        foreach (var key in new[] { DesktopReasoningEffortsKey, "\"" + DesktopReasoningEffortsKey + "\"", "'" + DesktopReasoningEffortsKey + "'" })
        {
            foreach (var value in new[] { "[]", "[\"high\"]", "[\n  \"medium\", # custom selection\n  \"high\",\n]" })
            {
                var customConfig = "model_reasoning_effort = \"low\"\n\n[desktop]\n" + key + " = " + value + "\n";
                if (!ApplyDesktopReasoningDefaults(customConfig).Equals(customConfig, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Desktop reasoning defaults must not override an explicit selection or actual model effort.");
                }
            }
        }

        const string nativeDesktopPreferences = "[desktop]\n" +
            "enabled-reasoning-efforts = [\n  \"low\",\n  \"high\",\n]\n" +
            "future-native-preference = true\n";
        foreach (var accountConfig in generated.Take(3))
        {
            var preserved = PreserveSharedDesktopRuntimeSections(nativeDesktopPreferences, accountConfig);
            if (!preserved.Replace("\r\n", "\n").Contains(nativeDesktopPreferences, StringComparison.Ordinal) ||
                preserved.Contains(defaultLine, StringComparison.Ordinal) ||
                preserved.Split('\n').Count(line => line.Trim() == "[desktop]") != 1 ||
                !PreserveSharedDesktopRuntimeSections(preserved, accountConfig).Equals(preserved, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Switching accounts must retain native desktop preferences without duplicate tables.");
            }
        }

        var legacyDesktop = PreserveSharedDesktopRuntimeSections(
            "[desktop]\nlocaleOverride = \"zh-CN\"\n", generated[1]);
        if (!legacyDesktop.Contains(defaultLine, StringComparison.Ordinal) ||
            !legacyDesktop.Contains("localeOverride = \"zh-CN\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A legacy desktop table must acquire six reasoning levels without losing native preferences.");
        }
    }
}
