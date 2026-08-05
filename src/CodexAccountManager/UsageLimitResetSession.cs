using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

public sealed record UsageRateLimitWindow(
    int? UsedPercent,
    long? WindowMinutes,
    DateTimeOffset? ResetsAtUtc);

public sealed record UsageLimitResetInfo(
    long? AvailableCount,
    IReadOnlyList<UsageLimitResetCredit> Credits,
    UsageRateLimitWindow? Primary,
    UsageRateLimitWindow? Secondary,
    UsageCreditsSnapshot? CreditBalance,
    UsageSpendControl? IndividualLimit,
    string? PlanType)
{
    public bool IsAvailable => AvailableCount.HasValue;
    public int? UsedPercent => Primary?.UsedPercent;
    public DateTimeOffset? ResetsAtUtc => Primary?.ResetsAtUtc;
}

public sealed record UsageCreditsSnapshot(
    bool HasCredits,
    bool Unlimited,
    string? Balance);

public sealed record UsageSpendControl(
    string Limit,
    string Used,
    double? RemainingPercent,
    DateTimeOffset? ResetsAtUtc);

public sealed record UsageLimitResetCredit(
    string Id,
    string ResetType,
    string Status,
    DateTimeOffset? GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string Title,
    string Description);

public enum UsageLimitResetOutcome
{
    Reset,
    NothingToReset,
    NoCredit,
    AlreadyRedeemed
}

public sealed class UsageLimitResetSession : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConsumeTimeout = TimeSpan.FromSeconds(35);
    private readonly Process _process;
    private readonly Func<string, string> _maskSensitive;
    private readonly Task<string> _stderrTask;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private int _nextRequestId;
    private bool _disposed;

    internal UsageLimitResetSession(Process process, Func<string, string> maskSensitive)
    {
        _process = process;
        _maskSensitive = maskSensitive;
        _stderrTask = process.StandardError.ReadToEndAsync();
    }

    internal async Task InitializeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "codex-account-manager",
                ["title"] = "Codex Account Manager",
                ["version"] = "1.0.0"
            },
            ["capabilities"] = new JsonObject
            {
                ["experimentalApi"] = true
            }
        };
        await RequestAsync("initialize", parameters, timeout, cancellationToken);
        await WriteJsonLineAsync(new JsonObject { ["method"] = "initialized" }, cancellationToken);
    }

    public async Task<UsageLimitResetInfo> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            "account/rateLimits/read",
            null,
            ReadTimeout,
            cancellationToken);
        return ParseRateLimits(result);
    }

    public async Task<UsageLimitResetOutcome> ConsumeAsync(
        string idempotencyKey,
        string? creditId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("用量重置的幂等键不能为空。", nameof(idempotencyKey));
        }

        JsonObject BuildParameters()
        {
            var parameters = new JsonObject { ["idempotencyKey"] = idempotencyKey };
            if (!string.IsNullOrWhiteSpace(creditId))
            {
                parameters["creditId"] = creditId;
            }
            return parameters;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await RequestAsync(
                    "account/rateLimitResetCredit/consume",
                    BuildParameters(),
                    ConsumeTimeout,
                    cancellationToken);
                return ParseOutcome(result);
            }
            catch (TimeoutException) when (attempt == 0 && !_process.HasExited)
            {
                // The backend may have completed the first request after the local timeout.
                // Retrying with the same idempotency key prevents consuming a second credit.
            }
        }
    }

    internal static JsonObject BuildRequest(int id, string method, JsonObject? parameters)
    {
        var request = new JsonObject
        {
            ["id"] = id,
            ["method"] = method
        };
        if (parameters != null)
        {
            request["params"] = parameters.DeepClone();
        }
        return request;
    }

    internal static UsageLimitResetInfo ParseRateLimits(JsonObject result)
    {
        long? availableCount = null;
        var credits = new List<UsageLimitResetCredit>();
        if (result["rateLimitResetCredits"] is JsonObject resetSummary)
        {
            availableCount = ReadLong(resetSummary["availableCount"]);
            if (resetSummary["credits"] is JsonArray creditRows)
            {
                foreach (var node in creditRows.OfType<JsonObject>())
                {
                    credits.Add(new UsageLimitResetCredit(
                        ReadString(node["id"]),
                        ReadString(node["resetType"]),
                        ReadString(node["status"]),
                        ReadDateTimeOffset(node["grantedAt"]),
                        ReadDateTimeOffset(node["expiresAt"]),
                        ReadString(node["title"]),
                        ReadString(node["description"])));
                }
            }
        }

        var rateLimits = SelectCodexRateLimitSnapshot(result);
        var primary = ParseWindow(rateLimits?["primary"] as JsonObject);
        var secondary = ParseWindow(rateLimits?["secondary"] as JsonObject);
        var creditBalance = ParseCreditsSnapshot(rateLimits?["credits"] as JsonObject);
        var individualLimit = ParseSpendControl(rateLimits?["individualLimit"] as JsonObject);
        var planType = ReadString(rateLimits?["planType"]);
        return new UsageLimitResetInfo(
            availableCount,
            credits,
            primary,
            secondary,
            creditBalance,
            individualLimit,
            string.IsNullOrWhiteSpace(planType) ? null : planType);
    }

    private static JsonObject? SelectCodexRateLimitSnapshot(JsonObject result)
    {
        if (result["rateLimitsByLimitId"] is JsonObject byLimitId)
        {
            if (byLimitId["codex"] is JsonObject codex)
            {
                return codex;
            }

            foreach (var entry in byLimitId)
            {
                if (entry.Value is not JsonObject candidate)
                {
                    continue;
                }

                var limitId = ReadString(candidate["limitId"]);
                if (entry.Key.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                    limitId.Equals("codex", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return result["rateLimits"] as JsonObject;
    }

    private static UsageCreditsSnapshot? ParseCreditsSnapshot(JsonObject? credits)
    {
        if (credits == null)
        {
            return null;
        }

        return new UsageCreditsSnapshot(
            ReadBoolean(credits["hasCredits"]),
            ReadBoolean(credits["unlimited"]),
            NullIfWhiteSpace(ReadString(credits["balance"])));
    }

    private static UsageSpendControl? ParseSpendControl(JsonObject? spendControl)
    {
        if (spendControl == null)
        {
            return null;
        }

        var resetsAt = ReadLong(spendControl["resetsAt"]);
        return new UsageSpendControl(
            ReadString(spendControl["limit"]),
            ReadString(spendControl["used"]),
            ReadDouble(spendControl["remainingPercent"]),
            ParseUnixSeconds(resetsAt));
    }

    private static UsageRateLimitWindow? ParseWindow(JsonObject? window)
    {
        if (window == null)
        {
            return null;
        }

        // The official app-server schema defines rate-limit usedPercent as int32.
        // Do not infer a decimal precision that the service does not promise.
        var usedPercent = ReadRateLimitPercent(window["usedPercent"]) ??
                          ReadRateLimitPercent(window["used_percent"]);
        var windowMinutes =
            ReadLong(window["windowMinutes"]) ??
            ReadLong(window["windowDurationMins"]) ??
            ReadLong(window["window_minutes"]);
        var resetsAt = ReadLong(window["resetsAt"]) ?? ReadLong(window["resets_at"]);
        DateTimeOffset? resetsAtUtc = null;
        if (resetsAt.HasValue)
        {
            try
            {
                resetsAtUtc = DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                resetsAtUtc = null;
            }
        }

        return new UsageRateLimitWindow(usedPercent, windowMinutes, resetsAtUtc);
    }

    internal static UsageLimitResetOutcome ParseOutcome(JsonObject result)
    {
        return ReadString(result["outcome"]) switch
        {
            "reset" => UsageLimitResetOutcome.Reset,
            "nothingToReset" => UsageLimitResetOutcome.NothingToReset,
            "noCredit" => UsageLimitResetOutcome.NoCredit,
            "alreadyRedeemed" => UsageLimitResetOutcome.AlreadyRedeemed,
            var value => throw new InvalidOperationException(
                "Codex 返回了无法识别的用量重置结果：" +
                (string.IsNullOrWhiteSpace(value) ? "<empty>" : value))
        };
    }

    internal static void ValidateProtocolParsing()
    {
        var sample = JsonNode.Parse(
            """
            {
              "rateLimits": {
                "primary": { "usedPercent": 99, "windowDurationMins": 1, "resetsAt": 1786000000 }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "planType": "business",
                  "primary": { "usedPercent": 12, "windowDurationMins": 300, "resetsAt": 1786366863 },
                  "secondary": { "usedPercent": 33, "windowDurationMins": 10080, "resetsAt": 1786800000 },
                  "credits": { "hasCredits": true, "unlimited": false, "balance": "12.50" },
                  "individualLimit": { "limit": "50.00", "used": "20.00", "remainingPercent": 60, "resetsAt": 1786800000 }
                }
              },
              "rateLimitResetCredits": {
                "availableCount": 2,
                "credits": [
                  {
                    "id": "credit-1",
                    "resetType": "codexRateLimits",
                    "status": "available",
                    "grantedAt": "2026-07-11T00:00:00Z",
                    "expiresAt": "2026-08-11T00:00:00Z",
                    "title": "Reset",
                    "description": "Reset current limits"
                  }
                ]
              }
            }
            """)!.AsObject();
        var parsed = ParseRateLimits(sample);
        if (parsed.AvailableCount != 2 ||
            parsed.Credits.Count != 1 ||
            parsed.UsedPercent != 12 ||
            parsed.Primary?.WindowMinutes != 300 ||
            parsed.Secondary?.UsedPercent != 33 ||
            parsed.Secondary?.WindowMinutes != 10_080 ||
            parsed.CreditBalance is not { HasCredits: true, Unlimited: false, Balance: "12.50" } ||
            parsed.IndividualLimit is not { Limit: "50.00", Used: "20.00", RemainingPercent: 60 } ||
            parsed.PlanType != "business" ||
            !parsed.ResetsAtUtc.HasValue)
        {
            throw new InvalidOperationException("Usage-limit reset response parser self-test failed.");
        }

        var unsupportedFraction = ParseRateLimits(JsonNode.Parse(
            """
            { "rateLimits": { "primary": { "usedPercent": 12.5, "windowDurationMins": 300 } } }
            """)!.AsObject());
        if (unsupportedFraction.Primary?.UsedPercent.HasValue == true)
        {
            throw new InvalidOperationException(
                "Usage-limit parser invented support for decimal percentages outside the official int32 schema.");
        }

        var unavailable = ParseRateLimits(new JsonObject { ["rateLimitResetCredits"] = null });
        if (unavailable.IsAvailable || unavailable.AvailableCount.HasValue)
        {
            throw new InvalidOperationException("Unavailable reset-credit state was treated as zero.");
        }

        var readRequest = BuildRequest(2, "account/rateLimits/read", null);
        var consumeRequest = BuildRequest(
            3,
            "account/rateLimitResetCredit/consume",
            new JsonObject { ["idempotencyKey"] = "stable-key" });
        if (readRequest.ContainsKey("params") ||
            consumeRequest["params"]?["idempotencyKey"]?.GetValue<string>() != "stable-key" ||
            ParseOutcome(new JsonObject { ["outcome"] = "reset" }) != UsageLimitResetOutcome.Reset ||
            ParseOutcome(new JsonObject { ["outcome"] = "nothingToReset" }) != UsageLimitResetOutcome.NothingToReset ||
            ParseOutcome(new JsonObject { ["outcome"] = "noCredit" }) != UsageLimitResetOutcome.NoCredit ||
            ParseOutcome(new JsonObject { ["outcome"] = "alreadyRedeemed" }) != UsageLimitResetOutcome.AlreadyRedeemed)
        {
            throw new InvalidOperationException("Usage-limit reset request/outcome self-test failed.");
        }
    }

    private async Task<JsonObject> RequestAsync(
        string method,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            var requestId = Interlocked.Increment(ref _nextRequestId);
            await WriteJsonLineAsync(BuildRequest(requestId, method, parameters), cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            while (true)
            {
                string? line;
                try
                {
                    line = await _process.StandardOutput.ReadLineAsync(timeoutSource.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Codex 官方用量接口 {method} 响应超时。");
                }

                if (line == null)
                {
                    throw new InvalidOperationException(BuildExitedMessage(method));
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    // Ignore non-protocol diagnostic lines. app-server notifications and
                    // matching JSON-RPC responses are handled below.
                    continue;
                }

                if (message == null || ReadLong(message["id"]) != requestId)
                {
                    continue;
                }

                if (message["error"] is JsonObject error)
                {
                    var detail = ReadString(error["message"]);
                    throw new InvalidOperationException(
                        $"Codex 官方用量接口 {method} 失败：" +
                        _maskSensitive(string.IsNullOrWhiteSpace(detail) ? error.ToJsonString() : detail));
                }

                if (message["result"] is JsonObject result)
                {
                    return result;
                }

                throw new InvalidOperationException($"Codex 官方用量接口 {method} 返回格式不完整。");
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task WriteJsonLineAsync(JsonObject message, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_process.HasExited)
        {
            throw new InvalidOperationException(BuildExitedMessage(ReadString(message["method"])));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _process.StandardInput.WriteLineAsync(message.ToJsonString());
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    private string BuildExitedMessage(string method)
    {
        var stderr = _stderrTask.IsCompletedSuccessfully ? _stderrTask.Result : "";
        var detail = string.IsNullOrWhiteSpace(stderr)
            ? "辅助进程已退出。"
            : _maskSensitive(stderr.Trim());
        return $"Codex 官方用量接口 {method} 无法继续：{detail}";
    }

    private static string ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text ?? ""
            : "";
    }

    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }
        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }
        return null;
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue;
        }
        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }
        return null;
    }

    private static int? ReadRateLimitPercent(JsonNode? node)
    {
        var value = ReadLong(node);
        return value is >= 0 and <= 100 ? checked((int)value.Value) : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonNode? node)
    {
        var unixSeconds = ReadLong(node);
        if (unixSeconds.HasValue)
        {
            return ParseUnixSeconds(unixSeconds);
        }

        var value = ReadString(node);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ParseUnixSeconds(long? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool ReadBoolean(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _process.StandardInput.Close();
        }
        catch
        {
            // The helper may already have exited after an RPC error.
        }

        if (!_process.HasExited)
        {
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await _process.WaitForExitAsync(exitTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may exit between the timeout and kill request.
                }
            }
        }

        try
        {
            await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Disposal must not replace the result of a completed reset operation.
        }

        _requestLock.Dispose();
        _process.Dispose();
    }
}
