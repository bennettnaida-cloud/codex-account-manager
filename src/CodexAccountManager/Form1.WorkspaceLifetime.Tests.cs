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

                // Verify that remaining-USD estimation stays visible in a calm theme color
                // while all passive anomaly/normality verdict labels remain absent on both
                // the quota list card and the detail card (even for low estimated capacity).
                if (scenario == "restore")
                {
                    var epochStart = DateTimeOffset.UtcNow.AddMinutes(-20);
                    form._passiveQuotaMonitoring.Enable(account, epochStart, 20D, 10_080);
                    var estimatedReport = new UsageReport
                    {
                        Accounts = [new AccountUsageSummary
                        {
                            AccountName = account.Name,
                            AccountKey = QuotaAccountIdentity.CreateKey(account),
                            RateLimitUsedPercent = 25,
                            RateLimitWindowMinutes = 10_080,
                            RateLimitResetAtUtc = DateTimeOffset.UtcNow.AddDays(4),
                            RateLimitObservedAtUtc = epochStart.AddMinutes(10),
                            Timeline =
                            {
                                new UsageEvent
                                {
                                    AccountName = account.Name,
                                    TimestampUtc = epochStart.AddMinutes(2),
                                    Source = UsageEventSource.Natural,
                                    ActivationEpochUtc = epochStart,
                                    InputTokens = 10_000,
                                    TotalTokens = 10_000,
                                    Model = "gpt-5.4",
                                    RateLimitUsedPercent = 21,
                                    RateLimitWindowMinutes = 10_080,
                                    RateLimitResetAtUtc = DateTimeOffset.UtcNow.AddDays(4)
                                },
                                new UsageEvent
                                {
                                    AccountName = account.Name,
                                    TimestampUtc = epochStart.AddMinutes(4),
                                    Source = UsageEventSource.Natural,
                                    ActivationEpochUtc = epochStart,
                                    InputTokens = 20_000,
                                    TotalTokens = 20_000,
                                    Model = "gpt-5.4",
                                    RateLimitUsedPercent = 23,
                                    RateLimitWindowMinutes = 10_080,
                                    RateLimitResetAtUtc = DateTimeOffset.UtcNow.AddDays(4)
                                },
                                new UsageEvent
                                {
                                    AccountName = account.Name,
                                    TimestampUtc = epochStart.AddMinutes(6),
                                    Source = UsageEventSource.Natural,
                                    ActivationEpochUtc = epochStart,
                                    InputTokens = 20_000,
                                    TotalTokens = 20_000,
                                    Model = "gpt-5.4",
                                    RateLimitUsedPercent = 25,
                                    RateLimitWindowMinutes = 10_080,
                                    RateLimitResetAtUtc = DateTimeOffset.UtcNow.AddDays(4)
                                }
                            }
                        }]
                    };
                    form._quotaUsageCache = estimatedReport;
                    form.RefreshActivePassiveQuotaMonitoring(estimatedReport);
                    form._showAccountDetail = false;
                    form.RenderCards();
                    ValidateNoAnomalyVerdictLabels(form._cardsPanel, form._palette.DangerColor, requireEstimatedSummary: true);

                    form._selectedAccountName = account.Name;
                    form._showAccountDetail = true;
                    form.RenderCards();
                    ValidateNoAnomalyVerdictLabels(form._cardsPanel, form._palette.DangerColor, requireEstimatedSummary: true);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ValidateNoAnomalyVerdictLabels(
        Control root,
        Color dangerColor,
        bool requireEstimatedSummary)
    {
        string[] forbiddenVerdicts = ["额度异常", "额度正常", "额度待确认", "推测异常", "推测正常", "容量推测偏差段"];
        var sawEstimatedRemaining = false;
        var stack = new Stack<Control>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (Control child in current.Controls)
            {
                stack.Push(child);
            }

            var text = current.Text ?? string.Empty;
            foreach (var verdict in forbiddenVerdicts)
            {
                if (text.Contains(verdict, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Removed anomaly/normality verdict text '{verdict}' appeared on control '{current.Name}' ('{text}').");
                }
            }

            if (text.Contains("推测剩余", StringComparison.Ordinal) &&
                text.Contains('$', StringComparison.Ordinal))
            {
                sawEstimatedRemaining = true;
                if (current.ForeColor.ToArgb() == dangerColor.ToArgb())
                {
                    throw new InvalidOperationException(
                        $"Remaining-USD estimate label '{text}' must use a calm theme color instead of red DangerColor.");
                }
            }
        }

        if (requireEstimatedSummary && !sawEstimatedRemaining)
        {
            throw new InvalidOperationException(
                "Quota cards must retain the visible '推测剩余 $... / $...' remaining-USD estimate.");
        }
    }
}
