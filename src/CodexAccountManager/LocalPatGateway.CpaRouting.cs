using System.Collections.Concurrent;
using System.Net;

namespace CodexAccountManager;

internal sealed partial class LocalPatGatewayHost
{
    private readonly object _cpaRoutingGate = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cpaCooldownUntil = new(StringComparer.Ordinal);
    private bool _cpaCursorsInitialized;
    private string? _cpaPrimaryCursor;
    private string? _cpaBackupCursor;

    private bool IsCpaRoutingTransport(GatewayCredential credential)
    {
        var settings = _themeService.LoadSettings();
        return AccountRotationConfiguration.IsEnabled(settings) &&
               AccountRotationConfiguration.UsesCpaRouting(settings) &&
               !string.IsNullOrWhiteSpace(credential.AccountKey) &&
               IsSessionAffinityAccountEligible(settings, credential.AccountKey);
    }

    private void RememberCpaSelection(GatewayCredential credential, AccountRotationPool? pool = null)
    {
        lock (_cpaRoutingGate)
        {
            if (!_cpaCursorsInitialized)
            {
                var settings = _themeService.LoadSettings();
                _cpaPrimaryCursor = settings.AccountRotationPrimaryCursorAccountKey;
                _cpaBackupCursor = settings.AccountRotationBackupCursorAccountKey;
                _cpaCursorsInitialized = true;
            }
            pool ??= AccountRotationConfiguration.GetPool(_themeService.LoadSettings(), credential.AccountKey!);
            if (pool == AccountRotationPool.Primary) _cpaPrimaryCursor = credential.AccountKey;
            if (pool == AccountRotationPool.Backup) _cpaBackupCursor = credential.AccountKey;
        }
    }

    private bool IsCpaAccountCooling(string? key, DateTimeOffset now) =>
        key != null && _cpaCooldownUntil.TryGetValue(key, out var until) && until > now;

    private static bool IsCpaRetryableStatus(HttpStatusCode status) =>
        (int)status is 401 or 403 or 408 or 429 or 500 or 502 or 503 or 504 or >= 520 and <= 526;

    private void CoolCpaAccount(string? key, HttpResponseMessage? failure = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var now = DateTimeOffset.UtcNow;
        var retryAfter = failure?.Headers.RetryAfter;
        var until = retryAfter?.Date ?? now.Add(retryAfter?.Delta ?? TimeSpan.FromSeconds(60));
        if (until < now.AddSeconds(60)) until = now.AddSeconds(60);
        // Account-wide, in-memory cooldown. No auth files or official quota evidence
        // are changed. Cooldown expiry makes the account eligible again.
        _cpaCooldownUntil.AddOrUpdate(key, until, (_, prior) => prior > until ? prior : until);
    }
}
