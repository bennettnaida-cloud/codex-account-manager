namespace CodexAccountManager;

public partial class Form1
{
    internal static void ValidateWorkspaceControlLifetime()
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-workspace-lifetime-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", root);
            foreach (var scenario in new[] { "invalidate", "resize", "replace", "restore" })
            {
                // No form is shown, credentials are absent, and no client/gateway is launched.
                using var form = new Form1(preserveExistingPatGatewayOnStartup: true);
                form._quotaRefreshTimer.Stop();
                var account = new AccountRecord
                {
                    Name = "lifetime@example.invalid", AuthKind = AccountAuthKind.OfficialOAuth,
                    CodexHome = Path.Combine(root, "account")
                };
                form._accounts = [account];
                var report = new UsageReport
                {
                    Accounts = [new AccountUsageSummary
                    {
                        AccountName = account.Name, AccountKey = QuotaAccountIdentity.CreateKey(account),
                        RateLimitUsedPercent = 25, RateLimitWindowMinutes = 10080,
                        RateLimitResetAtUtc = DateTimeOffset.UtcNow.AddDays(4)
                    }]
                };
                form._quotaUsageCache = report;
                form._activeView = WorkspaceView.QuotaUsage;
                form.RenderCards();
                var button = form._bulkQuotaTestButton;
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    form.CacheActiveWorkspaceView();
                    if (scenario == "invalidate")
                    {
                        // Gateway account changes invalidate the cached quota page.
                        form.SetCurrentAccount(iteration.ToString(), render: false, persistSettings: false);
                    }
                    else if (scenario == "resize")
                    {
                        var entry = form._workspaceViewCache[WorkspaceView.QuotaUsage];
                        form._workspaceViewCache[WorkspaceView.QuotaUsage] = entry with
                        {
                            WorkspaceWidth = entry.WorkspaceWidth + 100
                        };
                        if (form.TryRestoreWorkspaceView(WorkspaceView.QuotaUsage))
                            throw new InvalidOperationException("A stale-width page was restored.");
                    }
                    else if (scenario == "replace")
                    {
                        form._cardsPanel.Controls.Add(new Panel());
                        form.CacheActiveWorkspaceView();
                    }
                    else
                    {
                        if (!form.TryRestoreWorkspaceView(WorkspaceView.QuotaUsage))
                            throw new InvalidOperationException("The cached quota page did not restore.");
                        if (!ReferenceEquals(button.Parent?.Parent, form._cardsPanel))
                            throw new InvalidOperationException("The bulk action was lost during cache restore.");
                    }

                    if (button.IsDisposed)
                        throw new InvalidOperationException($"Quota bulk action was disposed during {scenario}.");
                    form._quotaUsageCache = report;
                    form.RenderCards();
                    if (form._cardsPanel.Controls.OfType<Control>().Count(c => c.Tag is QuotaUsageRowBinding) != 1)
                        throw new InvalidOperationException($"Quota rows disappeared after {scenario}.");
                    if (!form.TryUpdateQuotaUsageInPlace(report))
                        throw new InvalidOperationException($"Quota refresh failed after {scenario}.");
                    form.ClearWorkspaceViewCache();
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }
}
