using System.Net;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal sealed partial class LocalPatGatewayHost
{
    private static bool IsCompatibleQuotaStatus(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.PaymentRequired or
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    private static Upstream429Classification? ClassifyQuotaFailure(
        bool compatibleApi,
        HttpResponseMessage response,
        byte[] body,
        DateTimeOffset observedAtUtc)
    {
        // Relay billing errors often have neither HTTP 429 nor an OpenAI reset header.
        // Only explicit errors in a failed response qualify; never scan successful
        // model output, arbitrary HTML, or echoed request content for quota words.
        if (compatibleApi && IsCompatibleQuotaStatus(response.StatusCode) &&
            HasCompatibleQuotaError(body))
        {
            _ = TryReadStructuredQuotaExhaustion(body, observedAtUtc, out var resetAtUtc);
            return new Upstream429Classification(true, "compatible-api-quota-exhausted", resetAtUtc);
        }

        return response.StatusCode == HttpStatusCode.TooManyRequests
            ? ClassifyUpstream429(response, body, observedAtUtc)
            : null;
    }

    private static bool HasCompatibleQuotaError(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement;
            if (error.ValueKind != JsonValueKind.Object)
                return false;
            if (error.TryGetProperty("error", out var nested))
                error = nested;

            if (error.ValueKind == JsonValueKind.String)
                return IsQuotaMessage(error.GetString());
            if (error.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var name in new[] { "code", "type", "error_code", "error_type" })
            {
                if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var code = value.GetString()?.Trim().ToLowerInvariant();
                    if (IsExplicitQuotaExhaustionMarker(code) || code is
                        "insufficient_balance" or "balance_exhausted" or "insufficient_user_quota" or
                        "insufficient_account_quota" or "insufficient_token_quota" or
                        "user_quota_exhausted" or "account_quota_exhausted")
                        return true;
                }
            }
            foreach (var name in new[] { "message", "detail" })
            {
                if (error.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String && IsQuotaMessage(value.GetString()))
                    return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static bool IsQuotaMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return new[]
        {
            "insufficient balance", "insufficient quota", "quota exhausted", "quota is exhausted",
            "quota has been exhausted", "user quota is not enough", "account balance is exhausted",
            "余额不足", "额度不足", "额度已用尽", "额度已耗尽", "配额已耗尽"
        }.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static void ValidateCompatibleQuotaErrors()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var status in new[] { 400, 402, 403, 429 })
        {
            foreach (var body in new[]
            {
                "{\"error\":{\"code\":\"INSUFFICIENT_BALANCE\",\"message\":\"Insufficient balance\"}}",
                "{\"error\":{\"type\":\"insufficient_quota\"}}",
                "{\"error\":{\"code\":\"insufficient_user_quota\"}}",
                "{\"error\":{\"message\":\"账户额度已耗尽，请充值\"}}",
                "{\"message\":\"Insufficient balance\"}"
            })
            {
                using var response = new HttpResponseMessage((HttpStatusCode)status);
                var result = ClassifyQuotaFailure(true, response, Encoding.UTF8.GetBytes(body), now);
                if (result is not { IsQuotaExhausted: true, RequiresSameAccountConfirmation: false })
                    throw new InvalidOperationException($"Relay billing error HTTP {status} must rotate without requiring a reset header.");
            }
        }

        foreach (var (status, body) in new[]
        {
            (401, "{\"error\":{\"code\":\"insufficient_quota\"}}"),
            (403, "{\"error\":{\"code\":\"permission_denied\",\"message\":\"Model not available for this group\"}}"),
            (403, "{\"error\":{\"message\":\"API key expired\"}}"),
            (402, "{\"request\":{\"message\":\"insufficient balance\"}}"),
            (402, "<html>insufficient balance</html>"),
            (429, "{\"error\":{\"code\":\"rate_limit_exceeded\"}}"),
            (500, "{\"error\":{\"message\":\"Internal server error\"}}"),
            (200, "{\"error\":{\"code\":\"insufficient_quota\"}}")
        })
        {
            using var response = new HttpResponseMessage((HttpStatusCode)status);
            if (ClassifyQuotaFailure(true, response, Encoding.UTF8.GetBytes(body), now)?.IsQuotaExhausted == true)
                throw new InvalidOperationException($"HTTP {status} without reliable quota evidence must not mark an account exhausted.");
        }
        using var official = new HttpResponseMessage(HttpStatusCode.Forbidden);
        if (ClassifyQuotaFailure(false, official,
                Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"insufficient_quota\"}}"), now) != null)
            throw new InvalidOperationException("Relay billing classification must not override official account authorization errors.");
    }
}
