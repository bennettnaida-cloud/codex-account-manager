namespace CodexAccountManager;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var managerRootIndex = Array.FindIndex(
            args,
            argument => argument.Equals("--manager-root", StringComparison.OrdinalIgnoreCase));
        if (managerRootIndex >= 0)
        {
            if (managerRootIndex + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[managerRootIndex + 1]) ||
                args[managerRootIndex + 1].StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("--manager-root requires a directory path.");
                return 2;
            }

            try
            {
                Environment.SetEnvironmentVariable(
                    "CODEX_ACCOUNT_MANAGER_HOME",
                    Path.GetFullPath(args[managerRootIndex + 1]));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine("--manager-root is not a valid directory path.");
                return 2;
            }
        }

        if (args.Contains("--sync-compatible-model-catalogs", StringComparer.OrdinalIgnoreCase))
        {
            try { Console.WriteLine("Model catalogs updated: " + new CodexCliService().SyncCompatibleModelCatalogs()); return 0; }
            catch { Console.Error.WriteLine("Model catalog synchronization failed; credentials were not changed."); return 1; }
        }
        var refreshCompatibleCatalogIndex = Array.FindIndex(
            args,
            argument => argument.Equals(
                "--refresh-compatible-model-catalog",
                StringComparison.OrdinalIgnoreCase));
        if (refreshCompatibleCatalogIndex >= 0)
        {
            if (refreshCompatibleCatalogIndex + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[refreshCompatibleCatalogIndex + 1]))
            {
                Console.Error.WriteLine("--refresh-compatible-model-catalog requires an account name.");
                return 2;
            }
            try
            {
                var count = new CodexCliService()
                    .RefreshCompatibleModelCatalogAsync(args[refreshCompatibleCatalogIndex + 1])
                    .GetAwaiter()
                    .GetResult();
                Console.WriteLine("Compatible model catalog refreshed: " + count);
                return 0;
            }
            catch
            {
                Console.Error.WriteLine(
                    "Compatible model catalog refresh failed; credentials were not changed.");
                return 1;
            }
        }
        if (args.Contains("--sync-native-compaction", StringComparer.OrdinalIgnoreCase))
        {
            try { Console.WriteLine("Native compaction configs updated: " + new CodexCliService().SyncManagedCompactionSettings()); return 0; }
            catch { Console.Error.WriteLine("Compaction configuration migration failed; credentials were not changed."); return 1; }
        }
        if (args.Contains("--proxy-stdio", StringComparer.OrdinalIgnoreCase)) return ProxyStdioHost.Run(import: false);
        if (args.Contains("--proxy-import-stdio", StringComparer.OrdinalIgnoreCase)) return ProxyStdioHost.Run(import: true);
        if (args.Contains(LocalPatGateway.ProcessArgument, StringComparer.OrdinalIgnoreCase))
        {
            return LocalPatGateway.RunProcess(args);
        }
        if (args.Contains(CodexNativeFastBridge.ProcessArgument, StringComparer.OrdinalIgnoreCase))
        {
            return CodexNativeFastBridge.RunProcess(args);
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
        if (args.Contains("--prune-deleted-desktop-state", StringComparer.OrdinalIgnoreCase))
        {
            return RunDeletedDesktopStatePrune(
                args.Contains(
                    "--allow-live-codex",
                    StringComparer.OrdinalIgnoreCase));
        }
        if (args.Contains("--repair-rollout-history", StringComparer.OrdinalIgnoreCase))
        {
            return RunRolloutHistoryRepair(
                args.Contains("--allow-ordinal-rewrite", StringComparer.OrdinalIgnoreCase));
        }
        if (args.Contains("--dual-login-recovery-self-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunDualLoginRecoverySelfTest();
        }
        if (args.Contains("--gateway-quota-signal-self-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunGatewayQuotaSignalSelfTest();
        }
        if (args.Contains("--gateway-rotation-self-test", StringComparer.OrdinalIgnoreCase))
        {
            CodexCliService.ValidateOfficialOAuthProfileProjection();
            PatAutoRotationPolicy.Validate();
            LocalPatGatewayHost.ValidateRoutingAndCredentialClassification();
            LocalPatGatewayHost.ValidateCompatibleQuotaErrors();
            AccountRotationConfiguration.Validate();
            LocalPatGatewayHost.ValidateCompatibleQuotaFailoverAsync().GetAwaiter().GetResult();
            Console.WriteLine("Gateway rotation and compatible quota classification self-test passed.");
            return 0;
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
        if (args.Contains("--history-sync-probe", StringComparer.OrdinalIgnoreCase))
        {
            return RunHistorySyncProbe();
        }
        if (args.Contains("--normalize-thread-section-order", StringComparer.OrdinalIgnoreCase))
        {
            return RunThreadSectionOrderNormalization();
        }
        if (args.Contains("--sync-chat-sections", StringComparer.OrdinalIgnoreCase))
        {
            return RunChatSectionSynchronization();
        }
        var moveThreadSectionIndex = Array.FindIndex(
            args,
            argument => argument.Equals("--move-thread-section", StringComparison.OrdinalIgnoreCase));
        if (moveThreadSectionIndex >= 0)
        {
            return RunMoveThreadSection(args, moveThreadSectionIndex);
        }
        if (args.Contains("--reset-credits-read", StringComparer.OrdinalIgnoreCase))
        {
            return RunResetCreditsRead(args);
        }
        if (args.Contains("--repair-codex-plus-plus-task", StringComparer.OrdinalIgnoreCase))
        {
            return RunCodexPlusPlusTaskRepair();
        }

        // Only the GUI branch is single-instance. Gateway, helper and self-test
        // arguments have all returned above and must remain runnable as child processes.
        Mutex? guiMutex = null;
        var guiMutexAcquired = false;
        try
        {
            var managerRoot = new AccountStore().RootPath;
            var mutexName = "Local\\CodexAccountManager.Gui." +
                            ReleaseConfiguration.Version + "." +
                            Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(
                                    System.Text.Encoding.UTF8.GetBytes(managerRoot)))
                                [..16];
            guiMutex = new Mutex(false, mutexName);
            try
            {
                guiMutexAcquired = guiMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                guiMutexAcquired = true;
            }
            if (!guiMutexAcquired)
            {
                MessageBox.Show(
                    "同一版本、同一数据目录的 Codex Account Manager 已经在运行。\n请先关闭已有管理器窗口，再启动此版本。",
                    "Codex Account Manager",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }

        var preserveExistingPatGateway = args.Contains(
            AppUpdateService.PreserveExistingGatewayArgument,
            StringComparer.OrdinalIgnoreCase);
        var refreshNativeFastBridge = args.Contains(
            AppUpdateService.RefreshNativeFastBridgeArgument,
            StringComparer.OrdinalIgnoreCase);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnApplicationThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        // Refresh only the launcher files.  Do not repair/register the scheduled task here:
        // that operation can require elevation and must never stop an already running
        // Codex or gateway process.  The generated launcher validates PID, start time,
        // executable path, and the manager PID before it can stop a prior client.
        try
        {
            new CodexCliService().RefreshCodexPlusPlusTaskLauncherFiles();
            ManagerLifecycleDiagnostics.Write("codex-plus-plus-launcher-files-refreshed");
        }
        catch (Exception ex)
        {
            // A read-only/locked LocalAppData directory must not prevent the manager UI
            // from opening.  The next launch or an explicit repair can retry the refresh.
            ManagerLifecycleDiagnostics.WriteException(
                "codex-plus-plus-launcher-files-refresh-failed",
                ex);
        }
        ManagerLifecycleDiagnostics.Write(
            "manager-message-loop-started",
            $"preserve_gateway={preserveExistingPatGateway}; refresh_bridge={refreshNativeFastBridge}");
        try
        {
            var updated = new CodexCliService().SyncManagedCompactionSettings();
            ManagerLifecycleDiagnostics.Write("native-compaction-configs-migrated", $"updated={updated}");
        }
        catch (Exception ex)
        {
            ManagerLifecycleDiagnostics.WriteException("native-compaction-config-migration-failed", ex);
        }
        try
        {
            Application.Run(new Form1(preserveExistingPatGateway, refreshNativeFastBridge));
        }
        finally
        {
            ManagerLifecycleDiagnostics.Write("manager-message-loop-ended");
        }
        return 0;
        }
        finally
        {
            if (guiMutexAcquired)
            {
                try
                {
                    guiMutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // The process may be exiting after an abandoned mutex.
                }
            }
            guiMutex?.Dispose();
        }
    }

    private static void OnApplicationThreadException(
        object sender,
        ThreadExceptionEventArgs eventArgs)
    {
        ManagerLifecycleDiagnostics.WriteException(
            "ui-thread-exception-contained",
            eventArgs.Exception);
        try
        {
            MessageBox.Show(
                "本次操作发生异常，但 Account Manager 已保持运行。请查看状态提示后重试。\n\n" +
                $"诊断日志：{ManagerLifecycleDiagnostics.LogPath}",
                "Codex Account Manager",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch
        {
            // The original UI exception has already been contained and recorded.
        }
    }

    private static void OnCurrentDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            ManagerLifecycleDiagnostics.WriteException(
                "app-domain-unhandled-exception",
                exception,
                $"terminating={eventArgs.IsTerminating}");
        }
        else
        {
            ManagerLifecycleDiagnostics.Write(
                "app-domain-unhandled-non-exception",
                $"terminating={eventArgs.IsTerminating}");
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs eventArgs)
    {
        ManagerLifecycleDiagnostics.WriteException(
            "unobserved-task-exception-contained",
            eventArgs.Exception);
        eventArgs.SetObserved();
    }

    private static int RunDeletedDesktopStatePrune(bool allowWhileOfficialClientRunning)
    {
        try
        {
            var changed = new CodexCliService().TryPruneDeletedDesktopSidebarState(
                allowWhileOfficialClientRunning);
            Console.WriteLine(changed
                ? "Deleted Codex desktop sidebar entries were pruned."
                : allowWhileOfficialClientRunning
                    ? "Codex desktop sidebar was not changed (no stale entries were found or the cache is currently locked)."
                    : "Codex desktop sidebar was not changed (the client may still be running or no stale entries were found).");
            return changed ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(CodexCliService.MaskSensitiveText(ex.Message));
            return 1;
        }
    }

    private static int RunRolloutHistoryRepair(bool allowOrdinalRewrite)
    {
        try
        {
            var result = CodexRolloutHistoryRepairService.TryRepairLaggingPaginatedRollouts(
                CodexCliService.GetDefaultCodexHome(),
                allowOrdinalRewrite);
            Console.WriteLine(
                $"Codex rollout history repair: candidates={result.CandidateFiles}; " +
                $"scanned={result.ScannedFiles}; repaired_files={result.RepairedFiles}; " +
                $"repaired_records={result.RepairedRecords}; scanned_bytes={result.ScannedBytes}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(CodexCliService.MaskSensitiveText(ex.Message));
            return 1;
        }
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

    private static int RunHistorySyncProbe()
    {
        try
        {
            var threads = new CodexCliService()
                .ListThreadsFromCodexAsync(CodexCliService.GetDefaultCodexHome())
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(
                $"Codex history sync passed. Threads={threads.Count}; " +
                $"Active={threads.Count(thread => !thread.Archived)}; " +
                $"Archived={threads.Count(thread => thread.Archived)}");
            foreach (var thread in threads.Take(5))
            {
                Console.WriteLine($"{thread.Id}\t{thread.Name}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunThreadSectionOrderNormalization()
    {
        try
        {
            var changedSections = new CodexCliService()
                .NormalizeThreadSectionOrderAsync(CodexCliService.GetDefaultCodexHome())
                .GetAwaiter()
                .GetResult();
            Console.WriteLine($"Codex thread-section order normalized. Sections={changedSections}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunChatSectionSynchronization()
    {
        try
        {
            var store = new AccountStore();
            var accounts = store.LoadAccounts();
            var result = ChatSectionSynchronizationService.Synchronize(
                accounts,
                CodexCliService.GetDefaultCodexHome(),
                managerRoot: store.RootPath);
            Console.WriteLine(
                $"Chat sections synchronized. OAuthAccounts={result.OAuthAccounts}; " +
                $"Profiles={result.UpdatedProfiles}; Sections={result.AddedSections}; " +
                $"Items={result.AddedItems}; Changed={result.Changed}; " +
                $"Backup={result.BackupPath ?? "none"}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunMoveThreadSection(string[] args, int switchIndex)
    {
        if (switchIndex + 2 >= args.Length ||
            string.IsNullOrWhiteSpace(args[switchIndex + 1]) ||
            args[switchIndex + 1].StartsWith("-", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[switchIndex + 2]) ||
            args[switchIndex + 2].StartsWith("-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "--move-thread-section requires a thread ID and a section ID.\n" +
                "Example: --move-thread-section <thread-id> <section-id>");
            return 2;
        }

        try
        {
            var threadId = args[switchIndex + 1];
            var sectionId = args[switchIndex + 2];
            if (!Guid.TryParse(threadId, out _) || !Guid.TryParse(sectionId, out _))
            {
                Console.Error.WriteLine("thread ID and section ID must be GUIDs.");
                return 2;
            }

            var codexHome = CodexCliService.GetDefaultCodexHome();
            new CodexCliService()
                .MoveThreadToSectionAsync(threadId, sectionId, codexHome)
                .GetAwaiter()
                .GetResult();

            var moved = new SharedHistoryService()
                .Load(codexHome, 10000)
                .FirstOrDefault(thread => thread.Id.Equals(threadId, StringComparison.OrdinalIgnoreCase));
            if (moved == null || !moved.SectionId.Equals(sectionId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Codex 返回成功，但数据库中的对话归属仍为“{moved?.SectionName ?? "未分类"}”。");
            }

            Console.WriteLine($"Thread {threadId} moved to section {sectionId}.");
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
            ProxyNodeStore.Validate();
            ProxyCoreService.ValidateNativeNodeConfig();
            ProxyHttpClientFactory.ValidateSocksStreamLifetime();
            var accounts = store.LoadAccounts();
            foreach (var account in accounts)
            {
                if (string.IsNullOrWhiteSpace(account.Name) || string.IsNullOrWhiteSpace(account.CodexHome))
                {
                    Console.Error.WriteLine("Account name and CODEX_HOME are required.");
                    return 3;
                }
            }

            CodexCliService.ValidateConfigProjectionDefaults();
            CodexCliService.ValidateNativeCompactionSettings();
            CodexCliService.ValidateLocalPatConfigMigration();
            LocalPatGatewayHost.ValidateRoutingAndCredentialClassification();
            LocalPatGatewayHost.ValidateSessionAffinityRouting();
            LocalPatGatewayHost.ValidateCompatibleQuotaErrors();
            LocalPatGatewayHost.ValidatePatRejectionMessaging();
            Form1.ValidateGatewaySuccessfulActivityIsolation();
            PatAutoRotationPolicy.Validate();
            AccountRotationConfiguration.Validate();
            CodexFingerprintConvergence.Validate();
            OpenAIContentSessionSeed.Validate();
            PatGatewayRotationStore.Validate();
            PatGatewaySuccessfulActivityStore.Validate();
            PatGatewayQuotaSignalStore.Validate();
            PatGatewaySessionAffinityStore.Validate();
            OpenAIResponseIdObserver.Validate();
            CodexTaskBoundaryMonitor.Validate();
            CodexCliService.ValidateDesktopSidebarProjection();
            CodexCliService.ValidateSharedProfileProjection();
            CodexCliService.ValidateExplicitChatGptFeatureProjection();
            CodexCliService.ValidateDualLoginLaunchTransaction();
            CodexCliService.ValidateServiceTierAccountIsolation();
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
            CodexCliService.ValidateUsageLimitResetGatewaySafety();
            CodexCliService.ValidateMinimalQuotaTestParsing();
            QuotaSnapshotStore.ValidateAccountIsolation();
            ProbeUsageLedger.ValidateLedger();
            Sub2ApiUsageLedger.ValidateLedger();
            UsageTracker.ValidateProbeUsageMerge();
            UsageTracker.ValidateSwitchEventNormalization();
            UsageTracker.ValidateSessionAccountAttribution();
            UsageTracker.ValidateResumedOldSessionUsage();
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
            Form1.ValidateUnifiedHistoryOrdering();
            Form1.ValidateTokenRowGeometry();
            Form1.ValidateStableWorkspaceGutter();
            Form1.ValidateResponsiveAccountCardLayouts();
            Form1.ValidateCodexAppearanceLayouts();
            Form1.ValidateModelPricingGridLayout();
            AppUpdateService.ValidateUpdateHelperScript();
            AppUpdateService.ValidateResumableDownload();
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
            CodexCliService.ValidateThreadSectionMutationSafety();
            CodexCliService.ValidateThreadSectionOrderNormalization();
            CodexAppServerClient.ValidateAccountIdentityProtocol();
            CodexAppServerClient.ValidateThreadSectionProtocol();
            ThreadSectionNameDialog.ValidateValidation();
            ThreadSectionNameDialog.ValidateLayout();
            SharedThreadTranscriptService.ValidateReader();
            CodexRolloutHistoryRepairService.Validate();
            ThreadPreviewDialog.ValidateFormatting();
            BufferedFlowLayoutPanel.ValidateNestedViewportRedraw();
            NativeWindowTheme.ValidateRedrawPolicy();
            SharedHistoryMerger.ValidateHistoryFileMerge();
            SharedHistoryMerger.ValidateDeletedThreadTombstones();
            ChatSectionSynchronizationService.Validate();
            // GitHub's clean Windows runners do not have the Microsoft Store
            // Codex desktop package or its runtime logs installed. Keep the
            // integration and persisted-log readiness checks strict for normal
            // local self-tests, while allowing the release workflow to validate
            // the rest of the package without pretending that package is present.
            if (!IsWindowsClientSelfTestSkipped())
            {
                CodexCliService.ValidateWindowsClientResolution();
                CodexCliService.ValidateOfficialCodexLaunchRecovery();
            }
            else
            {
                Console.WriteLine("Windows Codex desktop client self-test skipped by build environment.");
            }
            CodexCliService.ValidateOfficialCodexActivation();
            CodexDreamSkinService.ValidateBundledRuntime();
            CodexNativeFastBridge.ValidatePatchContract();

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

    private static int RunDualLoginRecoverySelfTest()
    {
        try
        {
            CodexCliService.ValidateDualLoginLaunchTransaction();
            CodexCliService.ValidateOfficialCodexLaunchRecovery();
            Console.WriteLine("Dual-login recovery self-test passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunGatewayQuotaSignalSelfTest()
    {
        try
        {
            PatGatewayQuotaSignalStore.Validate();
            Console.WriteLine("Gateway quota-signal persistence self-test passed.");
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
            Form1.ValidateModelPricingGridLayout();
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
        await using var session = await new CodexCliService().OpenUsageLimitResetSessionAsync(
            account,
            preserveRunningGateway: true);
        var info = await session.ReadAsync();
        Console.WriteLine($"Account={account.Name}");
        Console.WriteLine($"AvailableCount={(info.AvailableCount?.ToString() ?? "unavailable")}");
        Console.WriteLine(
            $"ApplicableAvailableCount={(info.ApplicableAvailableCount?.ToString() ?? "not-provided")}");
        Console.WriteLine(
            $"EffectiveApplicableAvailableCount={(info.EffectiveApplicableAvailableCount?.ToString() ?? "unavailable")}");
        Console.WriteLine($"CanConsumeResetCredit={info.CanConsumeResetCredit}");
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
