namespace CodexAccountManager;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains(LocalPatGateway.ProcessArgument, StringComparer.OrdinalIgnoreCase))
        {
            return LocalPatGateway.RunProcess();
        }
        if (args.Contains("--ensure-local-pat-gateway", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                LocalPatGateway.EnsureRunning();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
        if (args.Contains("--shutdown-local-pat-gateway", StringComparer.OrdinalIgnoreCase))
        {
            return LocalPatGateway.ShutdownIfRunningAsync().GetAwaiter().GetResult() ? 0 : 1;
        }
        if (args.Contains("--migrate-local-pat-configs", StringComparer.OrdinalIgnoreCase))
        {
            return RunLocalPatConfigMigration();
        }
        var sub2ApiImportIndex = Array.FindIndex(
            args,
            argument => argument.Equals(
                "--import-sub2api-usage",
                StringComparison.OrdinalIgnoreCase));
        if (sub2ApiImportIndex >= 0)
        {
            return RunSub2ApiUsageImport(args, sub2ApiImportIndex);
        }
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunSelfTest();
        }
        if (args.Contains("--oauth-link-probe", StringComparer.OrdinalIgnoreCase))
        {
            return RunOAuthLinkProbeAsync().GetAwaiter().GetResult();
        }
        if (args.Contains("--usage-debug", StringComparer.OrdinalIgnoreCase))
        {
            return RunUsageDebug();
        }
        if (args.Contains("--model-render-self-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunModelRenderSelfTest();
        }
        var nebulaArtworkIndex = Array.FindIndex(
            args,
            argument => argument.Equals("--render-nebula-theme", StringComparison.OrdinalIgnoreCase));
        if (nebulaArtworkIndex >= 0)
        {
            return RunNebulaThemeArtworkRender(
                nebulaArtworkIndex + 1 < args.Length ? args[nebulaArtworkIndex + 1] : null);
        }
        var modelPreviewIndex = Array.FindIndex(
            args,
            argument => argument.Equals("--model-render-preview", StringComparison.OrdinalIgnoreCase));
        if (modelPreviewIndex >= 0)
        {
            return RunModelRenderPreview(
                modelPreviewIndex + 1 < args.Length ? args[modelPreviewIndex + 1] : null);
        }
        if (args.Contains("--merge-shared-history", StringComparer.OrdinalIgnoreCase))
        {
            return RunMergeSharedHistory();
        }
        if (args.Contains("--reset-credits-read", StringComparer.OrdinalIgnoreCase))
        {
            return RunResetCreditsRead(args);
        }
        if (args.Contains("--repair-codex-plus-plus-task", StringComparer.OrdinalIgnoreCase))
        {
            return RunCodexPlusPlusTaskRepair();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
        return 0;
    }

    private static int RunLocalPatConfigMigration()
    {
        try
        {
            var store = new AccountStore();
            var service = new CodexCliService();
            var migrated = 0;
            foreach (var account in store.LoadAccounts().Where(account => account.IsAccessToken))
            {
                service.EnsureLocalPatAccountConfig(account);
                migrated++;
            }

            Console.WriteLine($"Local PAT account configs migrated: {migrated}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunCodexPlusPlusTaskRepair()
    {
        try
        {
            new CodexCliService().RepairCodexPlusPlusScheduledTask();
            Console.WriteLine("Codex++ hidden scheduled task is current.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunSelfTest()
    {
        try
        {
            var store = new AccountStore();
            var accounts = store.LoadAccounts();
            if (accounts.Count == 0)
            {
                Console.Error.WriteLine("No accounts configured.");
                return 2;
            }

            foreach (var account in accounts)
            {
                if (string.IsNullOrWhiteSpace(account.Name) || string.IsNullOrWhiteSpace(account.CodexHome))
                {
                    Console.Error.WriteLine("Account name and CODEX_HOME are required.");
                    return 3;
                }
            }

            CodexCliService.ValidateConfigProjectionDefaults();
            CodexCliService.ValidateLocalPatConfigMigration();
            LocalPatGatewayHost.ValidateRoutingAndCredentialClassification();
            LocalPatGatewayHost.ValidatePatRejectionMessaging();
            CodexCliService.ValidateDesktopSidebarProjection();
            CodexCliService.ValidateSharedProfileProjection();
            CodexCliService.ValidateOfficialOAuthBrowserFlow();
            CodexCliService.ValidateOfficialOAuthProfileProjection();
            CodexCliService.ValidateDesktopStateRewrite();
            CodexCliService.ValidateCompatibleApiLaunchPreflight();
            CodexCliService.ValidateCodexPlusPlusTaskLauncherScript();
            CodexCliService.ValidateProxyEnvironmentProjection();
            AccountAuthKind.ValidateAuthenticationKinds();
            AccountStore.ValidateOfficialOAuthAccountStorage();
            AccountStore.ValidatePermanentAccountDeletion();
            UsageLimitResetSession.ValidateProtocolParsing();
            QuotaSnapshotStore.ValidateAccountIsolation();
            ProbeUsageLedger.ValidateLedger();
            Sub2ApiUsageLedger.ValidateLedger();
            UsageTracker.ValidateProbeUsageMerge();
            UsageTracker.ValidateSwitchEventNormalization();
            UsageTracker.ValidateSessionAccountAttribution();
            UsageTracker.ValidateSubagentReplayFiltering();
            UsageTracker.ValidatePersistentIncrementalCache();
            UsageTracker.ValidatePersistentCacheWriteIndex();
            PassiveQuotaMonitor.Validate();
            QuotaDashboardControls.Validate();
            ModelUsageDistributionControl.ValidateResponsiveLayout();
            ModelUsageDistributionControl.ValidateOffscreenRendering();
            TokenDialog.ValidateLayout();
            AccountDialog.ValidateExistingTokenEditLayout();
            Form1.ValidateUsagePricing();
            Form1.ValidateOfficialQuotaSnapshotPriority();
            Form1.ValidateQuotaRuntimeAccountIsolation();
            Form1.ValidateUnifiedHistorySearch();
            Form1.ValidateTokenRowGeometry();
            Form1.ValidateStableWorkspaceGutter();
            Form1.ValidateResponsiveAccountCardLayouts();
            Form1.ValidateCodexAppearanceLayouts();
            ModelCatalogService.ValidatePersistenceAndProxy();
            var officialCatalogProxy = Environment.GetEnvironmentVariable(
                "CODEX_ACCOUNT_MANAGER_SELF_TEST_MODEL_CATALOG_PROXY");
            if (!string.IsNullOrWhiteSpace(officialCatalogProxy))
            {
                ModelCatalogService.Initialize(store.RootPath);
                var officialCatalog = ModelCatalogService
                    .CheckAndSaveOfficialAsync(officialCatalogProxy)
                    .GetAwaiter()
                    .GetResult()
                    .Current;
                if (officialCatalog.Models.Count < 9 ||
                    !officialCatalog.CatalogSource.Equals("official", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Official model-catalog network self-test returned an incomplete catalog.");
                }
                Console.WriteLine(
                    $"Official model catalog verified through proxy. Models={officialCatalog.Models.Count}; " +
                    $"Default={officialCatalog.DefaultModel}");
            }
            SharedHistoryService.ValidateReader();
            SharedThreadTranscriptService.ValidateReader();
            ThreadPreviewDialog.ValidateFormatting();
            BufferedFlowLayoutPanel.ValidateNestedViewportRedraw();
            NativeWindowTheme.ValidateRedrawPolicy();
            SharedHistoryMerger.ValidateHistoryFileMerge();
            SharedHistoryMerger.ValidateDeletedThreadTombstones();
            // GitHub's clean Windows runners do not have the Microsoft Store
            // Codex desktop package installed. Keep the integration check strict
            // for normal local self-tests, while allowing the release workflow to
            // validate the rest of the package without pretending that package is
            // present.
            if (!IsWindowsClientSelfTestSkipped())
            {
                CodexCliService.ValidateWindowsClientResolution();
            }
            else
            {
                Console.WriteLine("Windows Codex desktop client self-test skipped by build environment.");
            }
            CodexCliService.ValidateOfficialCodexActivation();
            CodexDreamSkinService.ValidateBundledRuntime();

            var iconPath = Path.Combine(store.RootPath, "assets", "CodexAccountManager.ico");
            if (!File.Exists(iconPath))
            {
                Console.Error.WriteLine("Missing application icon.");
                return 4;
            }

            Console.WriteLine($"Self test passed. Root={store.RootPath}; Accounts={accounts.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsWindowsClientSelfTestSkipped()
    {
        var value = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SKIP_WINDOWS_CLIENT_SELF_TEST");
        return string.Equals(value?.Trim(), "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> RunOAuthLinkProbeAsync()
    {
        var draftRoot = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-oauth-draft-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(draftRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var linkReceived = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Progress<ChatGptOAuthAuthorization>(authorization =>
        {
            if (CodexCliService.IsAllowedOfficialOAuthAuthorizationUri(authorization.LoginUrl) &&
                Uri.TryCreate(authorization.LoginUrl, UriKind.Absolute, out var uri))
            {
                linkReceived.TrySetResult(uri);
            }
            else
            {
                linkReceived.TrySetException(
                    new InvalidOperationException("Official login returned an invalid URL."));
            }
            cancellation.Cancel();
        });

        try
        {
            var loginTask = new CodexCliService().LoginWithChatGptDraftAsync(
                draftRoot,
                progress,
                cancellation.Token);
            var uri = await linkReceived.Task.WaitAsync(TimeSpan.FromSeconds(40));
            try
            {
                _ = await loginTask;
            }
            catch (InvalidOperationException) when (cancellation.IsCancellationRequested)
            {
                // Expected: this diagnostic only verifies link generation, then cancels before login.
            }
            Console.WriteLine(
                $"OAuth link probe passed. Scheme={uri.Scheme}; Host={uri.Host}; URL was not printed or persisted.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("OAuth link probe failed: " + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                var fullPath = Path.GetFullPath(draftRoot);
                var tempRoot = Path.GetFullPath(Path.GetTempPath());
                if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(fullPath).StartsWith(
                        "codex-account-manager-oauth-draft-probe-",
                        StringComparison.Ordinal) &&
                    Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                }
            }
            catch
            {
                // The exact app-server child has already been terminated; temp cleanup is best effort.
            }
        }
    }

    private static int RunSub2ApiUsageImport(string[] args, int optionIndex)
    {
        try
        {
            if (optionIndex + 2 >= args.Length)
            {
                Console.Error.WriteLine(
                    "Usage: --import-sub2api-usage <csv> <account-name> " +
                    "[--from <ISO8601>] [--dry-run] [--verbose]");
                return 2;
            }

            var csvPath = Path.GetFullPath(args[optionIndex + 1]);
            var accountName = args[optionIndex + 2];
            var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
            DateTimeOffset? fromUtc = null;
            var fromIndex = Array.FindIndex(
                args,
                argument => argument.Equals("--from", StringComparison.OrdinalIgnoreCase));
            if (fromIndex >= 0)
            {
                if (fromIndex + 1 >= args.Length ||
                    !DateTimeOffset.TryParse(
                        args[fromIndex + 1],
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AllowWhiteSpaces |
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var parsedFrom))
                {
                    Console.Error.WriteLine(
                        "--from requires a valid ISO8601 timestamp, for example " +
                        "2026-07-17T21:41:00+08:00.");
                    return 2;
                }
                fromUtc = parsedFrom.ToUniversalTime();
            }
            var store = new AccountStore();
            var account = store.LoadAccounts().FirstOrDefault(candidate =>
                candidate.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                Console.Error.WriteLine("Account not found: " + accountName);
                return 3;
            }

            var csvRows = Sub2ApiUsageLedger.ReadCsvForImport(csvPath, account, fromUtc);
            if (csvRows.Count == 0)
            {
                Console.Error.WriteLine("The sub2api usage CSV contains no data rows.");
                return 4;
            }
            var sinceUtc = csvRows.Min(item => item.CompletedAtUtc) -
                Sub2ApiUsageLedger.MatchTolerance;
            var sessionsRoot = Path.Combine(CodexCliService.GetDefaultCodexHome(), "sessions");
            var localEvents = new UsageTracker(store.RootPath)
                .GetReconciledUsageEventsForImport(sessionsRoot, sinceUtc);
            var result = new Sub2ApiUsageLedger(store.RootPath).Import(
                csvPath,
                account,
                localEvents,
                persist: !dryRun,
                fromUtc: fromUtc);

            Console.WriteLine($"Mode={(dryRun ? "dry-run" : "import")}");
            Console.WriteLine($"Account={account.Name}");
            if (fromUtc.HasValue)
            {
                Console.WriteLine($"FromUtc={fromUtc.Value:O}");
            }
            Console.WriteLine($"CsvRows={result.CsvRows}");
            Console.WriteLine($"AlreadyImportedRows={result.AlreadyImportedRows}");
            Console.WriteLine($"MatchedLocalRows={result.MatchedLocalRows}");
            Console.WriteLine($"{(dryRun ? "WouldAddRows" : "AddedRows")}={result.AddedRows}");
            Console.WriteLine(
                $"{(dryRun ? "WouldAddCostUsd" : "AddedCostUsd")}=" +
                result.AddedCostUsd.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine($"Ledger={result.LedgerPath}");
            if (args.Contains("--verbose", StringComparer.OrdinalIgnoreCase))
            {
                foreach (var record in result.AddedRecords)
                {
                    Console.WriteLine(
                        $"Missing={record.CompletedAtUtc:O}|{record.Endpoint}|{record.Model}|" +
                        $"{record.InputTokens}|{record.CachedInputTokens}|{record.CacheWriteTokens}|" +
                        $"{record.OutputTokens}|{record.BilledCostUsd.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunModelRenderSelfTest()
    {
        try
        {
            BufferedFlowLayoutPanel.ValidateNestedViewportRedraw();
            Form1.ValidateCodexAppearanceLayouts();
            ModelUsageDistributionControl.ValidateResponsiveLayout();
            ModelUsageDistributionControl.ValidateOffscreenRendering();
            Console.WriteLine("Model usage distribution rendering self test passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunModelRenderPreview(string? outputPath)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(Path.GetTempPath(), "codex-account-manager-model-preview.png")
                : Path.GetFullPath(outputPath);
            ModelUsageDistributionControl.RenderSyntheticPreview(path);
            Console.WriteLine($"ModelRenderPreview={path}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunNebulaThemeArtworkRender(string? outputPath)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(Path.GetTempPath(), "account-manager-nebula-orbit.jpg")
                : Path.GetFullPath(outputPath);
            path = NebulaThemeArtworkRenderer.Render(path);
            Console.WriteLine($"NebulaThemeArtwork={path}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunMergeSharedHistory()
    {
        try
        {
            var store = new AccountStore();
            var accounts = store.LoadAccounts();
            var result = new CodexCliService().MergeSharedHistory(accounts);
            Console.WriteLine($"SharedCodexHome={result.SharedHome}");
            Console.WriteLine($"BackupDirectory={result.BackupDirectory ?? ""}");
            Console.WriteLine($"CopiedSessionFiles={result.CopiedSessionFiles}");
            Console.WriteLine($"ImportedThreads={result.ImportedThreads}");
            Console.WriteLine($"TotalThreads={result.TotalThreads}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunUsageDebug()
    {
        try
        {
            var store = new AccountStore();
            var accounts = store.LoadAccounts();
            var tracker = new UsageTracker(store.RootPath);
            var currentAccountName = new ThemeService(store.RootPath).LoadSettings().CurrentAccountName;
            var currentAccount = accounts.FirstOrDefault(account => account.Name.Equals(
                currentAccountName,
                StringComparison.OrdinalIgnoreCase));
            tracker.EnsureCurrentAccountTracking(currentAccount);
            var coldBuild = System.Diagnostics.Stopwatch.StartNew();
            _ = tracker.BuildReport(accounts);
            coldBuild.Stop();
            var warmBuild = System.Diagnostics.Stopwatch.StartNew();
            var report = tracker.BuildReport(accounts);
            warmBuild.Stop();
            var cachedHydrate = System.Diagnostics.Stopwatch.StartNew();
            _ = new UsageTracker(store.RootPath).TryBuildCachedReport(accounts);
            cachedHydrate.Stop();
            Console.WriteLine($"Root={store.RootPath}");
            Console.WriteLine($"DefaultCodexHome={CodexCliService.GetDefaultCodexHome()}");
            Console.WriteLine($"ColdBuildMs={coldBuild.ElapsedMilliseconds}");
            Console.WriteLine($"WarmBuildMs={warmBuild.ElapsedMilliseconds}");
            Console.WriteLine($"CachedHydrateMs={cachedHydrate.ElapsedMilliseconds}");
            Console.WriteLine($"SwitchEvents={report.SwitchEventCount}");
            Console.WriteLine($"UnassignedToday={report.UnassignedToday.TotalTokens}");
            Console.WriteLine(
                $"UnassignedCacheWrite={report.UnassignedMonth.CacheWriteTokens}; " +
                $"known={report.UnassignedMonth.CacheWriteKnownEvents}; " +
                $"unknown={report.UnassignedMonth.CacheWriteUnknownEvents}");
            PrintUsageDebugSamples(store.RootPath);
            foreach (var account in report.Accounts)
            {
                Console.WriteLine(
                    $"{account.AccountName}: 1h={account.Hour.TotalTokens}, 5h={account.FiveHours.TotalTokens}, " +
                    $"day={account.Day.TotalTokens}, week={account.Week.TotalTokens}, month={account.Month.TotalTokens}, " +
                    $"cacheWrite={account.Month.CacheWriteTokens}, cacheWriteKnown={account.Month.CacheWriteKnownEvents}, " +
                    $"cacheWriteUnknown={account.Month.CacheWriteUnknownEvents}, " +
                    $"responseUsageMatched={account.Month.ResponseUsageMatchedEvents}, " +
                    $"responseUsageDifferences={account.Month.ResponseUsageDifferenceEvents}, " +
                    $"primary={account.RateLimitUsedPercent?.ToString("0.#") ?? "unknown"}%/{account.RateLimitWindowMinutes?.ToString() ?? "unknown"}m, " +
                    $"secondary={account.SecondaryRateLimitUsedPercent?.ToString("0.#") ?? "unknown"}%/{account.SecondaryRateLimitWindowMinutes?.ToString() ?? "unknown"}m");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunResetCreditsRead(string[] args)
    {
        try
        {
            var optionIndex = Array.FindIndex(
                args,
                value => value.Equals("--reset-credits-read", StringComparison.OrdinalIgnoreCase));
            if (optionIndex < 0 || optionIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("Usage: --reset-credits-read <account-name>");
                return 2;
            }

            var accountName = args[optionIndex + 1];
            var account = new AccountStore().LoadAccounts().FirstOrDefault(candidate =>
                candidate.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                Console.Error.WriteLine("Account not found: " + accountName);
                return 3;
            }

            return ReadResetCreditsAsync(account).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ReadResetCreditsAsync(AccountRecord account)
    {
        await using var session = await new CodexCliService().OpenUsageLimitResetSessionAsync(account);
        var info = await session.ReadAsync();
        Console.WriteLine($"Account={account.Name}");
        Console.WriteLine($"AvailableCount={(info.AvailableCount?.ToString() ?? "unavailable")}");
        Console.WriteLine($"UsedPercent={(info.UsedPercent?.ToString("0.##") ?? "unknown")}");
        Console.WriteLine($"PrimaryWindowMinutes={(info.Primary?.WindowMinutes?.ToString() ?? "unknown")}");
        Console.WriteLine($"SecondaryUsedPercent={(info.Secondary?.UsedPercent?.ToString("0.##") ?? "unknown")}");
        Console.WriteLine($"SecondaryWindowMinutes={(info.Secondary?.WindowMinutes?.ToString() ?? "unknown")}");
        return 0;
    }

    private static void PrintUsageDebugSamples(string rootPath)
    {
        var historyPaths = new[]
        {
            Path.Combine(rootPath, "usage-account-switches.json"),
            Path.Combine(CodexCliService.GetDefaultCodexHome(), "codex-account-manager-usage-switches.json")
        };
        foreach (var path in historyPaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var events = System.Text.Json.JsonSerializer.Deserialize<List<UsageSwitchEvent>>(File.ReadAllText(path)) ?? [];
            foreach (var switchEvent in events)
            {
                Console.WriteLine($"Switch[{Path.GetFileName(path)}]={switchEvent.AccountName} @ {switchEvent.GetSwitchedAtUtc():O}");
            }
        }

        var sessionRoot = Path.Combine(CodexCliService.GetDefaultCodexHome(), "sessions");
        if (!Directory.Exists(sessionRoot))
        {
            return;
        }

        var samples = Directory
            .EnumerateFiles(sessionRoot, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(ReadTokenCountSamples)
            .TakeLast(5)
            .ToList();
        foreach (var line in samples)
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(line)?.AsObject();
            var timestamp = root?["timestamp"]?.GetValue<string>();
            var total = root?["payload"]?["info"]?["last_token_usage"]?["total_tokens"]?.GetValue<long>() ?? 0;
            Console.WriteLine($"UsageSample={timestamp}, total={total}");
        }
    }

    private static IEnumerable<string> ReadTokenCountSamples(string file)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch
        {
            yield break;
        }

        var lines = new Queue<string>();
        using (stream)
        using (var reader = new StreamReader(stream))
        {
            while (true)
            {
                var line = reader.ReadLine();
                if (line == null)
                {
                    break;
                }

                if (!line.Contains("\"token_count\""))
                {
                    continue;
                }

                lines.Enqueue(line);
                while (lines.Count > 3)
                {
                    lines.Dequeue();
                }
            }
        }

        foreach (var line in lines)
        {
            yield return line;
        }
    }
}
