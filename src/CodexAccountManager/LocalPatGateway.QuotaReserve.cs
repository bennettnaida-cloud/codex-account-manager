namespace CodexAccountManager;

internal sealed partial class LocalPatGatewayHost
{
    private HashSet<string> GetQuotaReservePausedAccountKeys(
        IReadOnlyCollection<AccountRecord> accounts, DateTimeOffset nowUtc)
    {
        var snapshots = new QuotaSnapshotStore(_accountStore.RootPath).LoadForAccounts(accounts);
        return accounts.Where(account => !account.IsCompatibleApi)
            .Select(QuotaAccountIdentity.CreateKey)
            .Where(key => snapshots.TryGetValue(key, out var snapshot) &&
                          PatAutoRotationPolicy.IsReservePaused(snapshot, nowUtc))
            .ToHashSet(StringComparer.Ordinal);
    }

    private bool IsQuotaReservePausedForRotation(GatewayCredential credential)
    {
        if (credential.IsCompatibleApi || string.IsNullOrWhiteSpace(credential.AccountKey))
            return false;
        var settings = _themeService.LoadSettings();
        var accounts = _accountStore.LoadAccounts();
        _ = AccountRotationConfiguration.Normalize(settings, accounts);
        return AccountRotationConfiguration.IsEnabled(settings) &&
               accounts.Any(account => QuotaAccountIdentity.CreateKey(account) == credential.AccountKey &&
                   AccountRotationConfiguration.GetPool(settings, account) != AccountRotationPool.None) &&
               GetQuotaReservePausedAccountKeys(accounts, DateTimeOffset.UtcNow).Contains(credential.AccountKey);
    }
}
