using System.Text.Json.Serialization;

namespace CodexAccountManager;

public sealed class AccountRecord
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("codexHome")]
    public string CodexHome { get; set; } = "";

    [JsonPropertyName("authKind")]
    public string AuthKind { get; set; } = AccountAuthKind.AccessToken;

    [JsonPropertyName("apiProviderName")]
    public string ApiProviderName { get; set; } = "OpenAI";

    [JsonPropertyName("apiBaseUrl")]
    public string ApiBaseUrl { get; set; } = "";

    [JsonPropertyName("apiModel")]
    public string ApiModel { get; set; } = "gpt-5.5";

    [JsonPropertyName("apiWireApi")]
    public string ApiWireApi { get; set; } = "responses";

    [JsonPropertyName("quotaLimitType")]
    public string QuotaLimitType { get; set; } = AccountQuotaLimitType.Unknown;

    [JsonPropertyName("quotaPrimaryWindowMinutes")]
    public long? QuotaPrimaryWindowMinutes { get; set; }

    [JsonPropertyName("quotaSecondaryWindowMinutes")]
    public long? QuotaSecondaryWindowMinutes { get; set; }

    [JsonPropertyName("quotaLimitObservedAtUtc")]
    public string? QuotaLimitObservedAtUtc { get; set; }

    public bool IsOfficialOAuth =>
        AuthKind.Equals(AccountAuthKind.OfficialOAuth, StringComparison.OrdinalIgnoreCase);

    public bool IsCompatibleApi =>
        AuthKind.Equals(AccountAuthKind.CompatibleApi, StringComparison.OrdinalIgnoreCase);

    // Unknown legacy values historically followed the Access Token path. Keep that
    // fallback while giving official OAuth an explicit branch of its own.
    public bool IsAccessToken => !IsOfficialOAuth && !IsCompatibleApi;

    public string AuthKindLabel => IsOfficialOAuth
        ? "通过 ChatGPT 登录（官方）"
        : IsCompatibleApi
            ? "兼容 API"
            : "Access Token";
}

public static class AccountAuthKind
{
    public const string AccessToken = "access_token";
    public const string OfficialOAuth = "official_oauth";
    public const string CompatibleApi = "compatible_api";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AccessToken;
        }

        var candidate = value.Trim();
        if (candidate.Equals(AccessToken, StringComparison.OrdinalIgnoreCase))
        {
            return AccessToken;
        }
        if (candidate.Equals(OfficialOAuth, StringComparison.OrdinalIgnoreCase))
        {
            return OfficialOAuth;
        }
        if (candidate.Equals(CompatibleApi, StringComparison.OrdinalIgnoreCase))
        {
            return CompatibleApi;
        }

        return candidate;
    }

    internal static void ValidateAuthenticationKinds()
    {
        var legacy = new AccountRecord { AuthKind = "" };
        legacy.AuthKind = Normalize(legacy.AuthKind);
        var oauth = new AccountRecord { AuthKind = Normalize("OFFICIAL_OAUTH") };
        var api = new AccountRecord { AuthKind = Normalize("Compatible_Api") };

        if (!legacy.IsAccessToken || legacy.IsOfficialOAuth || legacy.IsCompatibleApi ||
            legacy.AuthKind != AccessToken || legacy.AuthKindLabel != "Access Token" ||
            !oauth.IsOfficialOAuth || oauth.IsAccessToken || oauth.IsCompatibleApi ||
            oauth.AuthKind != OfficialOAuth || oauth.AuthKindLabel != "通过 ChatGPT 登录（官方）" ||
            !api.IsCompatibleApi || api.IsAccessToken || api.IsOfficialOAuth ||
            api.AuthKind != CompatibleApi || api.AuthKindLabel != "兼容 API")
        {
            throw new InvalidOperationException("Account authentication-kind normalization failed.");
        }
    }
}

public static class AccountQuotaLimitType
{
    public const string Unknown = "unknown";
    public const string Monthly = "monthly";
    public const string FiveHourAndWeekly = "five_hour_and_weekly";
    public const string WeeklyOnly = "weekly_only";
    public const string FiveHourOnly = "five_hour_only";

    public static AccountQuotaWindowKind ClassifyWindow(long? windowMinutes)
    {
        return windowMinutes switch
        {
            >= 240 and <= 360 => AccountQuotaWindowKind.FiveHour,
            >= 9_000 and <= 11_000 => AccountQuotaWindowKind.Weekly,
            >= 40_000 and <= 47_000 => AccountQuotaWindowKind.Monthly,
            _ => AccountQuotaWindowKind.Unknown
        };
    }

    public static string Detect(long? primaryWindowMinutes, long? secondaryWindowMinutes)
    {
        var primaryKind = ClassifyWindow(primaryWindowMinutes);
        var secondaryKind = ClassifyWindow(secondaryWindowMinutes);
        var hasFiveHour = primaryKind == AccountQuotaWindowKind.FiveHour ||
                          secondaryKind == AccountQuotaWindowKind.FiveHour;
        var hasWeekly = primaryKind == AccountQuotaWindowKind.Weekly ||
                        secondaryKind == AccountQuotaWindowKind.Weekly;
        var hasMonthly = primaryKind == AccountQuotaWindowKind.Monthly ||
                         secondaryKind == AccountQuotaWindowKind.Monthly;

        if (hasMonthly)
        {
            return Monthly;
        }

        if (hasFiveHour && hasWeekly)
        {
            return FiveHourAndWeekly;
        }

        if (hasWeekly)
        {
            return WeeklyOnly;
        }

        if (hasFiveHour)
        {
            return FiveHourOnly;
        }

        return Unknown;
    }

    public static bool IsWeeklyCategory(string quotaLimitType) =>
        quotaLimitType is FiveHourAndWeekly or WeeklyOnly or FiveHourOnly;

    public static bool HasTwoOfficialWindows(string quotaLimitType) =>
        quotaLimitType == FiveHourAndWeekly;

    public static bool UsesTwoDetailLines(string quotaLimitType) =>
        quotaLimitType is FiveHourAndWeekly or FiveHourOnly;
}

public enum AccountQuotaWindowKind
{
    Unknown,
    FiveHour,
    Weekly,
    Monthly
}

public sealed record AccountQuotaWindowSnapshot(
    AccountQuotaWindowKind Kind,
    double? UsedPercent,
    long WindowMinutes,
    DateTimeOffset? ResetAtUtc,
    bool IsSecondary)
{
    public double? RemainingPercent => UsedPercent.HasValue
        ? Math.Max(0D, 100D - UsedPercent.Value)
        : null;
}

public sealed class TokenMetadata
{
    public string? UpdatedAtUtc { get; set; }
    public string? ExpiresAtUtc { get; set; }
}

public sealed class LoginStatus
{
    public int ExitCode { get; init; }
    public string Text { get; init; } = "";

    public string Badge
    {
        get
        {
            if (Text.Contains("personal access token", StringComparison.OrdinalIgnoreCase))
            {
                return "TOKEN";
            }
            if (Text.Contains("api key", StringComparison.OrdinalIgnoreCase))
            {
                return "API_KEY";
            }
            if (ExitCode == 0 && Text.Contains("chatgpt", StringComparison.OrdinalIgnoreCase))
            {
                return "OAUTH";
            }
            if (ExitCode == 0 && Text.Contains("logged in", StringComparison.OrdinalIgnoreCase))
            {
                return "LOGGED";
            }
            return ExitCode == 0 ? "UNKNOWN" : "FAILED";
        }
    }
}
