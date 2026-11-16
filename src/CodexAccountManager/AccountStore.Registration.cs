namespace CodexAccountManager;

public sealed partial class AccountStore
{
    private static void InitializeNewRegistration(AccountRecord account)
    {
        account.RegistrationId = Guid.NewGuid().ToString("N");
        account.RegisteredAtUtc = DateTimeOffset.UtcNow;
        account.QuotaLimitType = AccountQuotaLimitType.Unknown;
        account.QuotaPrimaryWindowMinutes = null;
        account.QuotaSecondaryWindowMinutes = null;
        account.QuotaLimitObservedAtUtc = null;
    }

    private void RemoveDeletedAccountState(AccountRecord account)
    {
        // Cleanup is owned by the store, not only by one UI button. A delayed reader
        // may still write the old ID after deletion, but it cannot reach a new registration.
        Action[] cleanup = [
            () => new QuotaSnapshotStore(RootPath).Remove(account),
            () => new PassiveQuotaMonitoringService(RootPath).Remove(account),
            () => new PatGatewayQuotaSignalStore(RootPath).RemoveForAccount(QuotaAccountIdentity.CreateKey(account))
        ];
        foreach (var remove in cleanup)
        {
            try { remove(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidOperationException or System.Text.Json.JsonException)
            {
                ManagerLifecycleDiagnostics.WriteException("deleted-account-cache-cleanup-deferred", ex);
            }
        }
    }
}
