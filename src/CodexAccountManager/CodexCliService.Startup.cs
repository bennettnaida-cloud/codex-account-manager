using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    private static readonly AsyncLocal<LaunchDiagnostics?> CurrentLaunch = new();
    private static int _desktopLaunchInProgress;
    private static WindowsClientActivationIdentity? _pendingOfficialStartup;
    private static long _navigationIssuedGeneration = -1;

    private static bool CanSubmitStartupNavigation(long generation, bool identityAlive, bool windowVisible) =>
        identityAlive && !windowVisible && IsCurrentWindowsClientLaunchGeneration(generation) &&
        Interlocked.Exchange(ref _navigationIssuedGeneration, generation) != generation;

    private static bool HasPendingOfficialStartup() =>
        Volatile.Read(ref _pendingOfficialStartup) is { } identity &&
        ObserveStartupIdentity(identity) != StartupProcessState.Exited;

    internal sealed class LaunchDiagnostics : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly LaunchDiagnostics? _previous;
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string ProgressText = "正在检查配置与网关…";
        internal long ElapsedSeconds => _clock.ElapsedMilliseconds / 1000;
        internal long ElapsedMilliseconds => _clock.ElapsedMilliseconds;
        internal TaskCompletionSource<string>? PageObservation { get; private set; }
        internal void StartPageObservation() => PageObservation ??=
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void FinishPageObservation(string message) => PageObservation?.TrySetResult(message);
        internal LaunchDiagnostics(LaunchDiagnostics? previous) => _previous = previous;
        public void Dispose() => CurrentLaunch.Value = _previous;
    }

    internal static LaunchDiagnostics BeginLaunchDiagnostics(string entry)
    {
        var trace = new LaunchDiagnostics(CurrentLaunch.Value);
        CurrentLaunch.Value = trace;
        WriteCodexPlusPlusLaunchDiagnostic("launch-click", $"entry={entry}");
        return trace;
    }

    private static void LaunchPhase(string phase, string progress, string detail = "")
    {
        if (CurrentLaunch.Value is { } trace) trace.ProgressText = progress;
        WriteCodexPlusPlusLaunchDiagnostic(phase, detail);
    }

    internal static void ReportLaunchProgress(string phase, string progress) => LaunchPhase(phase, progress);

    private sealed class DesktopLaunchLease : IDisposable
    {
        public DesktopLaunchLease()
        {
            if (Interlocked.CompareExchange(ref _desktopLaunchInProgress, 1, 0) != 0)
                throw new InvalidOperationException("已有 Codex 启动正在进行；本次点击未排队，请等待完成。");
        }
        public void Dispose() => Volatile.Write(ref _desktopLaunchInProgress, 0);
    }

    // Readiness is deliberately separate from the process termination classifier.
    private enum StartupProcessState { Alive, Exited, Unobservable }
    private enum StartupObservation { Ready, Exited, DetectionUnavailable, Initializing, InitializationError }

    private static StartupObservation EvaluateStartupObservation(
        StartupProcessState process, OfficialCodexLogReadinessState log, bool logAvailable) =>
        process == StartupProcessState.Exited ? StartupObservation.Exited :
        process == StartupProcessState.Unobservable ? StartupObservation.DetectionUnavailable :
        log == OfficialCodexLogReadinessState.PersistedAtomSyncFailed ? StartupObservation.InitializationError :
        logAvailable && log == OfficialCodexLogReadinessState.Ready ? StartupObservation.Ready :
        !logAvailable ? StartupObservation.DetectionUnavailable : StartupObservation.Initializing;

    private static StartupProcessState ObserveStartupIdentity(WindowsClientActivationIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited) return StartupProcessState.Exited;
            if (!identity.StartTimeUtcTicks.HasValue) return StartupProcessState.Unobservable;
            if (process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks.Value)
                return StartupProcessState.Exited; // PID reuse, not this activation.
            var root = Path.GetDirectoryName(ResolveCodexWindowsClientPath());
            return IsCodexWindowsClientProcess(process, root)
                ? StartupProcessState.Alive : StartupProcessState.Unobservable;
        }
        catch (ArgumentException) { return StartupProcessState.Exited; }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception or NotSupportedException)
        { return StartupProcessState.Unobservable; }
    }

    private static bool IsTrustedExternalAppServer(
        Process process, WindowsClientActivationIdentity root, IReadOnlyDictionary<int, int> parents)
    {
        if (!root.StartTimeUtcTicks.HasValue ||
            ObserveStartupIdentity(root) != StartupProcessState.Alive ||
            !process.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
            parents.GetValueOrDefault(process.Id) != root.ProcessId ||
            process.StartTime.ToUniversalTime().Ticks < root.StartTimeUtcTicks.Value)
            return false;
        var file = process.MainModule?.FileName;
        return IsExternalAppServerPath(file) && SignedOpenAiExecutable.IsTrusted(file!);
    }

    private static bool IsExternalAppServerPath(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return false;
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");
        var relative = Path.GetRelativePath(bin, file).Replace('\\', '/');
        // Exactly one version directory; no traversal, arbitrary CLI home, or prefix match.
        return System.Text.RegularExpressions.Regex.IsMatch(relative,
            "\\A[A-Za-z0-9_-]{6,80}/codex\\.exe\\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static WindowsClientActivationIdentity? FindExistingOfficialWindow()
    {
        var packageRoot = Path.GetDirectoryName(ResolveCodexWindowsClientPath());
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || !IsCodexWindowsClientProcess(process, packageRoot)) continue;
                    var identity = new WindowsClientActivationIdentity(
                        process.Id, process.StartTime.ToUniversalTime().Ticks);
                    if (FindOfficialMainWindow(identity) != null) return identity;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return null;
    }

    private static WindowsClientActivationIdentity? FindExistingOfficialProcess()
    {
        return SelectExistingOfficialProcess(CaptureOfficialStartupSamples());
    }

    private static WindowsClientActivationIdentity? SelectExistingOfficialProcess(
        IReadOnlyList<OfficialStartupSample> samples)
    {
        var roots = samples.Where(sample => sample.VerifiedPackage && sample.StartTicks > 0 &&
            !samples.Any(parent => parent.ProcessId == sample.ParentProcessId)).ToArray();
        return roots.Length == 1 ? new(roots[0].ProcessId, roots[0].StartTicks) : null;
    }

    private sealed record OfficialDesktopWindow(
        IntPtr Handle, int ProcessId, string ClassName, bool Owned, bool ToolWindow,
        bool Visible, bool Minimized);

    private static OfficialDesktopWindow? SelectOfficialMainWindow(
        int processId, IntPtr preferredHandle, IReadOnlyList<OfficialDesktopWindow> windows)
    {
        var candidates = windows.Where(window => window.Handle != IntPtr.Zero &&
            window.ProcessId == processId && !window.Owned && !window.ToolWindow &&
            window.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.Ordinal)).ToArray();
        var preferred = candidates.FirstOrDefault(window => window.Handle == preferredHandle);
        if (preferred != null) return preferred;
        var visible = candidates.Where(window => window.Visible).ToArray();
        // Multiple hidden Electron windows are ambiguous; let official activation choose.
        return visible.Length == 1 ? visible[0] : candidates.Length == 1 ? candidates[0] : null;
    }

    private static OfficialDesktopWindow? FindOfficialMainWindow(WindowsClientActivationIdentity identity)
    {
        if (ObserveStartupIdentity(identity) != StartupProcessState.Alive) return null;
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            var selected = SelectOfficialMainWindow(identity.ProcessId, process.MainWindowHandle,
                EnumerateOfficialDesktopWindows(identity.ProcessId));
            return ObserveStartupIdentity(identity) == StartupProcessState.Alive ? selected : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }

    private static IReadOnlyList<OfficialDesktopWindow> EnumerateOfficialDesktopWindows(int processId)
    {
        var windows = new List<OfficialDesktopWindow>();
        EnumDesktopWindowCallback callback = (window, state) =>
        {
            _ = GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != processId) return true;
            var className = new StringBuilder(256);
            if (GetClassName(window, className, className.Capacity) == 0) return true;
            windows.Add(new OfficialDesktopWindow(window, processId, className.ToString(),
                GetWindow(window, 4) != IntPtr.Zero, (GetWindowLong(window, -20) & 0x80) != 0,
                IsWindowVisible(window), IsIconic(window)));
            return true;
        };
        _ = EnumWindows(callback, IntPtr.Zero);
        return windows;
    }

    private static int? GetOfficialWindowRestoreCommand(bool visible, bool minimized) =>
        minimized ? 9 : !visible ? 5 : null;

    private static bool ShouldPreservePendingOfficialStartup(bool managedPending, bool hasMainWindow) =>
        managedPending && !hasMainWindow;

    private static bool FocusExistingOfficialWindow(WindowsClientActivationIdentity identity)
    {
        var window = FindOfficialMainWindow(identity);
        if (window == null) return false;
        _ = GetWindowThreadProcessId(window.Handle, out var ownerProcessId);
        if (ownerProcessId != identity.ProcessId ||
            ObserveStartupIdentity(identity) != StartupProcessState.Alive) return false;
        var command = GetOfficialWindowRestoreCommand(window.Visible, window.Minimized);
        if (command.HasValue && !ShowWindowAsync(window.Handle, command.Value)) return false;
        var focused = SetForegroundWindow(window.Handle); // Explicit click, never an observer.
        LaunchPhase("official-window-reused", "已唤回现有 Codex 窗口",
            $"pid={identity.ProcessId}; start_ticks={identity.StartTimeUtcTicks}; " +
            $"was_visible={window.Visible}; was_minimized={window.Minimized}; " +
            $"restore_command={command}; foreground_accepted={focused}; navigation=false; restart=false");
        CurrentLaunch.Value?.StartPageObservation();
        CurrentLaunch.Value?.FinishPageObservation("已唤回现有 Codex 窗口；未重启或切换聊天。");
        return true;
    }

    private static bool TryReuseExistingOfficialClient()
    {
        var existingWindow = FindExistingOfficialWindow();
        if (existingWindow != null && FocusExistingOfficialWindow(existingWindow)) return true;
        if (ShouldPreservePendingOfficialStartup(HasPendingOfficialStartup(), existingWindow != null))
        {
            LaunchPhase("official-pending-startup-preserved", "Codex 本次启动仍在初始化，未重复启动…",
                "managed_observer=true; reactivated=false; navigation=false; retry=false");
            CurrentLaunch.Value?.StartPageObservation();
            CurrentLaunch.Value?.FinishPageObservation("Codex 本次启动仍在初始化；未重复启动或切换聊天。");
            return true;
        }
        var existingProcess = existingWindow ?? FindExistingOfficialProcess();
        if (existingProcess == null || ObserveStartupIdentity(existingProcess) != StartupProcessState.Alive)
            return false;
        LaunchPhase("official-existing-instance-activation-start", "正在请求 Windows 唤回现有 Codex…",
            $"pid={existingProcess.ProcessId}; start_ticks={existingProcess.StartTimeUtcTicks}; " +
            "arguments=empty; navigation=false; restart=false");
        // No deep link or renderer flags: this explicit click only restores the official instance.
        var activated = ActivateOfficialCodexPackage(cleanupUnverifiableIdentity: false);
        LaunchPhase("official-existing-instance-activation-accepted", "已请求 Windows 唤回现有 Codex",
            $"existing_pid={existingProcess.ProcessId}; activation_pid={activated.ProcessId}; " +
            "arguments=empty; navigation=false; restart=false");
        CurrentLaunch.Value?.StartPageObservation();
        CurrentLaunch.Value?.FinishPageObservation("已请求 Windows 唤回现有 Codex；未重启或切换聊天。");
        return true;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    private delegate bool EnumDesktopWindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumDesktopWindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);

    private static string BuildOfficialStartupArguments(int? debugPort, string? projectPath, Uri? desktopProxy = null)
    {
        var arguments = debugPort.HasValue ? BuildOfficialNativeFastActivationArguments(debugPort.Value) : "";
        if (desktopProxy != null)
        {
            var proxy = ValidateDesktopProxyUri(desktopProxy);
            arguments += " --proxy-server=" + proxy.GetLeftPart(UriPartial.Authority) +
                " --proxy-bypass-list=localhost;127.0.0.1;[::1]";
        }
        // Codex queues initialArgv deep links itself until its primary route is ready.
        // Submit once with activation, never as a second shell activation after page-ready.
        return string.IsNullOrWhiteSpace(projectPath) ? arguments.Trim() :
            (arguments + " " + BuildNewThreadDeepLink(projectPath)).Trim();
    }

    private static bool LaunchOfficialCodex(string projectPath, bool useDreamSkin,
        ThemeMode appearanceMode, string appearancePresetId, string? appearanceLabel,
        bool allowRendererPatch, long launchGeneration, Uri? desktopProxy = null)
    {
        if (useDreamSkin)
            return LaunchOfficialCodexWithLegacyRendererReload(projectPath, true,
                appearanceMode, appearancePresetId, appearanceLabel, allowRendererPatch, launchGeneration);

        var clientPath = ResolveCodexWindowsClientPath();
        if (string.IsNullOrWhiteSpace(clientPath) || !File.Exists(clientPath))
            throw new FileNotFoundException("找不到已安装的官方 Codex Windows 客户端。", clientPath);
        LaunchPhase("official-log-baseline-start", "正在准备启动诊断，随后打开 Codex…");
        var baseline = CaptureOfficialCodexLaunchLogBaseline();
        LaunchPhase("official-log-baseline-complete", "启动诊断已准备，正在请求 Windows 打开 Codex…",
            $"log_probe_available={baseline.IsUsable}; log_failure_does_not_block_activation=true");
        var launchStartedUtc = DateTime.UtcNow;
        int? port = null;
        if (allowRendererPatch)
        {
            try { port = SelectOfficialNativeFastCdpPort(); }
            catch (Exception ex)
            {
                WriteCodexPlusPlusLaunchDiagnostic("official-optional-debug-unavailable",
                    $"error_type={ex.GetType().Name}; base_launch_continues=true");
            }
        }
        if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration)) return false;
        LaunchPhase("official-system-activation-start", "正在请求 Windows 打开 Codex…",
            "navigation=initial-argv; reload_permitted=false; retry=false");
        // One COM activation. A timeout/metadata error is not permission to activate again.
        var identity = ActivateOfficialCodexPackage(port, cleanupUnverifiableIdentity: false, projectPath, desktopProxy);
        Interlocked.Exchange(ref _navigationIssuedGeneration, launchGeneration);
        Volatile.Write(ref _pendingOfficialStartup, identity);
        LaunchPhase("official-system-activation-accepted", "Windows 已接受启动，Codex 正在初始化…",
            $"pid={identity.ProcessId}; start_ticks={identity.StartTimeUtcTicks}; " +
            "project_navigation=queued-once-with-activation; reload=false");
        CurrentLaunch.Value?.StartPageObservation();
        ObserveOfficialStartupInBackground(baseline, identity, launchStartedUtc,
            launchGeneration, port, allowRendererPatch);
        return true;
    }

    private static void ObserveOfficialStartupInBackground(
        OfficialCodexLogBaseline baseline, WindowsClientActivationIdentity identity,
        DateTime originalLaunchStartedUtc, long generation, int? nativeFastPort, bool optionalFast)
    {
        var trace = CurrentLaunch.Value;
        _ = Task.Run(() =>
        {
            try
            {
                var probe = baseline.CreateProbe(identity.ProcessId, identity.StartTimeUtcTicks,
                    OfficialCodexLogReadinessStage.InitialLaunch);
                var clock = Stopwatch.StartNew();
                var visibleRecorded = false;
                var runtimeRecorded = false;
                while (clock.Elapsed < OfficialCodexPrimaryPageReadyTimeout &&
                       IsCurrentWindowsClientLaunchGeneration(generation))
                {
                    var state = ObserveStartupIdentity(identity);
                    if (state == StartupProcessState.Exited)
                    {
                        var handoff = SelectOfficialStartupIdentity(identity, CaptureOfficialStartupSamples());
                        if (handoff != null)
                        {
                            LaunchPhase("official-activation-handoff-verified", "Codex 正在完成启动交接…",
                                $"old_pid={identity.ProcessId}; pid={handoff.ProcessId}; start_ticks={handoff.StartTimeUtcTicks}");
                            identity = handoff;
                            Volatile.Write(ref _pendingOfficialStartup, identity);
                            probe = baseline.CreateProbe(identity.ProcessId, identity.StartTimeUtcTicks,
                                OfficialCodexLogReadinessStage.InitialLaunch);
                            continue;
                        }
                        LaunchPhase("official-process-exited", "Codex 进程已退出，请查看启动诊断",
                            $"pid={identity.ProcessId}; retry=false");
                        trace?.FinishPageObservation("Codex 启动进程已退出，请查看启动诊断。");
                        return;
                    }
                    if (state == StartupProcessState.Alive)
                    {
                        using var process = Process.GetProcessById(identity.ProcessId);
                        if (!visibleRecorded && process.MainWindowHandle != IntPtr.Zero &&
                            IsWindowVisible(process.MainWindowHandle))
                        {
                            visibleRecorded = true;
                            LaunchPhase("official-first-window-visible", "Codex 窗口已出现，正在初始化…",
                                $"pid={identity.ProcessId}; start_ticks={identity.StartTimeUtcTicks}");
                        }
                    }
                    var logState = probe.Poll();
                    var observation = EvaluateStartupObservation(state, logState, probe.IsAvailable);
                    if (observation == StartupObservation.Ready)
                    {
                        Interlocked.CompareExchange(ref _pendingOfficialStartup, null, identity);
                        LaunchPhase("official-main-page-interactive", "Codex 主页面已就绪",
                            $"pid={identity.ProcessId}; start_ticks={identity.StartTimeUtcTicks}; evidence=routes-and-ready; reload=false; " +
                            $"app_server_utc={probe.AppServerConnectedAtUtc:O}; routes_utc={probe.RoutesMountedAtUtc:O}; ready_utc={probe.ReadyAtUtc:O}");
                        if (optionalFast && nativeFastPort.HasValue)
                            StartOptionalFastWithoutReload(nativeFastPort.Value, identity, generation);
                        trace?.FinishPageObservation("Codex 主页面已就绪。");
                        return;
                    }
                    if (observation == StartupObservation.InitializationError)
                    {
                        LaunchPhase("official-initialization-error-preserved", "Codex 报告初始化错误，未自动重启",
                            $"pid={identity.ProcessId}; reason=persisted-atom-sync; retry=false");
                        trace?.FinishPageObservation("Codex 报告初始化错误；已保留窗口和账号，请查看启动诊断。");
                        return;
                    }
                    if (!runtimeRecorded && IsWindowsClientRuntimeHealthySince(
                            originalLaunchStartedUtc.AddSeconds(-2), identity))
                    {
                        runtimeRecorded = true;
                        LaunchPhase("official-runtime-observed", "Codex 窗口和后台进程已运行…",
                            $"pid={identity.ProcessId}; log_probe_available={probe.IsAvailable}; interactive=not-yet-verified");
                    }
                    Thread.Sleep(200); // Sampling, not a mandatory startup delay.
                }
                if (IsCurrentWindowsClientLaunchGeneration(generation))
                    LaunchPhase("official-readiness-observation-ended", "Codex 已启动，页面就绪尚未确认",
                        $"pid={identity.ProcessId}; visible={visibleRecorded}; runtime={runtimeRecorded}; " +
                        $"log_probe_available={probe.IsAvailable}; process_state={ObserveStartupIdentity(identity)}; " +
                        "restart=false; navigation=false; observation_timeout_not_window_latency=true");
            }
            catch (Exception ex)
            {
                WriteCodexPlusPlusLaunchDiagnostic("official-observation-unavailable",
                    $"pid={identity.ProcessId}; error_type={ex.GetType().Name}; restart=false; navigation=false");
            }
            finally
            {
                Interlocked.CompareExchange(ref _pendingOfficialStartup, null, identity);
                trace?.FinishPageObservation(IsCurrentWindowsClientLaunchGeneration(generation)
                    ? "Codex 窗口已启动，但尚未确认主页面就绪。若仍停在标志页，请检查所选账号节点对官方登录和功能服务的连接。"
                    : "本次启动观察已被新的启动取代。");
            }
        });
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

    private static void StartOptionalFastWithoutReload(int port, WindowsClientActivationIdentity identity, long generation)
    {
        // Optional work never delays completion and never navigates or focuses a window.
        _ = Task.Run(() =>
        {
            try
            {
                if (!IsCurrentWindowsClientLaunchGeneration(generation) ||
                    !IsVerifiedOfficialNativeFastEndpointOwner(port, identity) ||
                    !TryReadOfficialCodexCdpIdentity(port, expectedBrowserId: null, out var browserId)) return;
                using var helper = CodexNativeFastBridge.StartDetached(port, browserId,
                    identity.ProcessId, identity.StartTimeUtcTicks ?? 0,
                    GetCodexWindowsClientAppDirectory()!, allowRendererReload: false);
                WriteCodexPlusPlusLaunchDiagnostic("official-optional-fast-passive",
                    $"pid={identity.ProcessId}; reload_permitted=false; startup_wait=false");
            }
            catch (Exception ex)
            {
                WriteCodexPlusPlusLaunchDiagnostic("official-optional-fast-skipped",
                    $"error_type={ex.GetType().Name}; reload=false; startup_affected=false");
            }
        });
    }
    private static int? SelectOfficialStartupDebugPort(bool allowRendererPatch, Func<int> selectPort)
        => allowRendererPatch ? selectPort() : null;

    private sealed record OfficialStartupSample(
        int ProcessId, long StartTicks, int ParentProcessId,
        bool VerifiedPackage, bool HasWindow);

    private static WindowsClientActivationIdentity? SelectOfficialStartupIdentity(
        WindowsClientActivationIdentity original,
        IReadOnlyList<OfficialStartupSample> samples)
    {
        if (!original.StartTimeUtcTicks.HasValue)
        {
            return null;
        }

        var samePid = samples.FirstOrDefault(sample => sample.ProcessId == original.ProcessId);
        if (samePid != null)
        {
            // A reused PID or unreadable package identity is not evidence of success.
            return samePid.VerifiedPackage && samePid.StartTicks == original.StartTimeUtcTicks
                ? original
                : null;
        }

        // An MSIX/bootstrap handoff can retire the activation process. Only adopt one
        // visible, verified direct child born after that exact activation. Never adopt
        // an unrelated existing window, a helper without a window, or an ambiguous pair.
        var children = samples.Where(sample =>
            sample.ParentProcessId == original.ProcessId &&
            sample.VerifiedPackage && sample.HasWindow &&
            sample.StartTicks >= original.StartTimeUtcTicks.Value).ToArray();
        return children.Length == 1
            ? new WindowsClientActivationIdentity(children[0].ProcessId, children[0].StartTicks)
            : null;
    }

    private static IReadOnlyList<OfficialStartupSample> CaptureOfficialStartupSamples()
    {
        var samples = new List<OfficialStartupSample>();
        var parents = CaptureProcessParentIds();
        var clientPath = ResolveCodexWindowsClientPath();
        var packageRoot = string.IsNullOrWhiteSpace(clientPath) ? null : Path.GetDirectoryName(clientPath);
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    samples.Add(new OfficialStartupSample(
                        process.Id, process.StartTime.ToUniversalTime().Ticks,
                        parents.GetValueOrDefault(process.Id),
                        IsCodexWindowsClientProcess(process, packageRoot),
                        process.MainWindowHandle != IntPtr.Zero));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Metadata can be unavailable on a cold packaged-process launch.
                    // Keep its PID present: it must not be mistaken for an exited parent.
                    samples.Add(new OfficialStartupSample(process.Id, 0, 0, false, false));
                }
            }
        }
        return samples;
    }

    private static WindowsClientActivationIdentity WaitForOfficialCodexActivationStartup(
        WindowsClientActivationIdentity original, long launchGeneration)
    {
        var clock = Stopwatch.StartNew();
        WindowsClientActivationIdentity? last = null;
        var consecutive = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (!IsCurrentWindowsClientLaunchGeneration(launchGeneration))
            {
                throw new OperationCanceledException("Codex 启动已被后续切换操作替代。");
            }
            var observed = SelectOfficialStartupIdentity(original, CaptureOfficialStartupSamples());
            consecutive = observed != null && observed == last ? consecutive + 1 : 1;
            last = observed;
            if (observed != null && consecutive >= 3)
            {
                WriteCodexPlusPlusLaunchDiagnostic(
                    observed == original ? "official-activation-verified" : "official-activation-handoff-verified",
                    $"activation_pid={original.ProcessId}; window_pid={observed.ProcessId}; " +
                    $"elapsed_ms={clock.ElapsedMilliseconds}; reactivated=false");
                return observed;
            }
            Thread.Sleep(100);
        }

        WriteCodexPlusPlusLaunchDiagnostic("official-activation-verification-timeout",
            $"activation_pid={original.ProcessId}; elapsed_ms={clock.ElapsedMilliseconds}; " +
            "reactivated=false; terminated=false; process-exit-not-assumed=true");
        if (ObserveStartupIdentity(original) != StartupProcessState.Exited)
            return original; // Metadata timeout is not a failed or repeatable activation.
        throw new InvalidOperationException("本次 Codex 进程已退出；没有自动重复启动，请查看启动诊断。");
    }

    private static void ValidateOfficialCodexStartupObservation()
    {
        LocalPatGateway.ValidateDesktopStartupPreparation();
        ValidateNonDestructiveStartupPolicy();
        var selectedPorts = 0;
        int SelectPort() { selectedPorts++; return 19335; }
        if (SelectOfficialStartupDebugPort(false, SelectPort) != null || selectedPorts != 0 ||
            SelectOfficialStartupDebugPort(true, SelectPort) != 19335 || selectedPorts != 1)
        {
            throw new InvalidOperationException("Ordinary Codex must not initialize the renderer debug bridge.");
        }
        var root = new WindowsClientActivationIdentity(100, 1000);
        var alive = new OfficialStartupSample(100, 1000, 1, true, false);
        var child = new OfficialStartupSample(101, 1100, 100, true, true);
        if (SelectOfficialStartupIdentity(root, new[] { alive }) != root ||
            SelectOfficialStartupIdentity(root, Array.Empty<OfficialStartupSample>()) != null ||
            SelectOfficialStartupIdentity(root, new[] { alive with { VerifiedPackage = false } }) != null ||
            SelectOfficialStartupIdentity(root, new[] { alive with { StartTicks = 2000 }, child }) != null ||
            SelectOfficialStartupIdentity(root, new[] { child }) != new WindowsClientActivationIdentity(101, 1100) ||
            SelectOfficialStartupIdentity(root, new[] { child with { ParentProcessId = 9 } }) != null ||
            SelectOfficialStartupIdentity(root, new[] { child with { StartTicks = 999 } }) != null ||
            SelectOfficialStartupIdentity(root, new[] { child with { VerifiedPackage = false } }) != null ||
            SelectOfficialStartupIdentity(root, new[] { child with { HasWindow = false } }) != null ||
            SelectOfficialStartupIdentity(root, new[] { child, child with { ProcessId = 102 } }) != null ||
            SelectOfficialStartupIdentity(new WindowsClientActivationIdentity(100, null), new[] { child }) != null)
        {
            throw new InvalidOperationException("Official startup identity/handoff verification regression.");
        }
    }

    private static void ValidateNonDestructiveStartupPolicy()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Startup regression: " + message);
        }
        var project = @"C:\Projects\project with spaces\学习";
        foreach (var dual in new[] { false, true })
        {
            var hidden = new OfficialDesktopWindow((IntPtr)11, 100, "Chrome_WidgetWin_1",
                false, false, false, false);
            Require(SelectOfficialMainWindow(100, IntPtr.Zero, [hidden]) == hidden,
                "both entries must find a hidden main window even when MainWindowHandle is zero");
            Require(GetOfficialWindowRestoreCommand(false, false) == 5 &&
                GetOfficialWindowRestoreCommand(true, true) == 9 &&
                GetOfficialWindowRestoreCommand(false, true) == 9 &&
                GetOfficialWindowRestoreCommand(true, false) == null,
                "hidden windows are shown, minimized windows restored, visible windows not shown again");
            foreach (var rejected in new[]
                { hidden with { ProcessId = 9 }, hidden with { Owned = true },
                  hidden with { ToolWindow = true }, hidden with { ClassName = "Chrome_MessageWindow" },
                  hidden with { Handle = IntPtr.Zero } })
                Require(SelectOfficialMainWindow(100, IntPtr.Zero, [rejected]) == null,
                    "foreign, message, owned and utility windows cannot be restored as the primary window");
            var second = hidden with { Handle = (IntPtr)12 };
            Require(SelectOfficialMainWindow(100, IntPtr.Zero, [hidden, second]) == null &&
                SelectOfficialMainWindow(100, second.Handle, [hidden, second]) == second &&
                SelectOfficialMainWindow(100, IntPtr.Zero, [hidden, second with { Visible = true }])?.Handle == second.Handle,
                "ambiguous hidden windows use official activation rather than selecting an arbitrary window");
            Require(ShouldPreservePendingOfficialStartup(true, false) &&
                !ShouldPreservePendingOfficialStartup(false, false) &&
                !ShouldPreservePendingOfficialStartup(true, true),
                "only a managed active observer can classify a no-window process as initializing");
            Require(BuildOfficialStartupArguments(null, null) == "",
                "existing-instance activation has no renderer flags, project navigation or proxy override");
            var arguments = BuildOfficialStartupArguments(dual ? 19335 : null, project,
                new Uri("http://127.0.0.1:12805"));
            Require(arguments.Contains("--proxy-server=http://127.0.0.1:12805", StringComparison.Ordinal) &&
                arguments.Contains("--proxy-bypass-list=localhost;127.0.0.1;[::1]", StringComparison.Ordinal),
                "both desktop entries use the selected proxy and preserve local gateway access");
            Require(arguments.Split("codex://", StringSplitOptions.None).Length == 2,
                "each cold launch must queue exactly one project link in initial argv");
            Require(arguments.Contains(BuildNewThreadDeepLink(project), StringComparison.Ordinal), "project encoding");
            Require(arguments.Contains("remote-debugging-port", StringComparison.Ordinal) == dual, "ordinary launch must not start CDP");
            Require(ShouldPreserveExistingOfficialWindow(false, WindowsClientMode.OfficialCodex, false, true, dual),
                "same-account window reuse for both entries, including Manager opened after Codex");
            Require(!ShouldPreserveExistingOfficialWindow(true, WindowsClientMode.OfficialCodex, false, true, dual),
                "actual account switch still requires profile replacement");
        }
        foreach (var unsafeProxy in new[] { "http://user:password@127.0.0.1:8080", "file:///test", "http://example.test/path?secret=1" })
        {
            var rejected = false;
            try { _ = BuildOfficialStartupArguments(null, project, new Uri(unsafeProxy)); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "proxy credentials/invalid values cannot leak into process arguments");
        }
        using (var fixture = new Form { Text = "CAM hidden-window fixture", ShowInTaskbar = false })
        {
            var handle = fixture.Handle;
            var hidden = EnumerateOfficialDesktopWindows(Environment.ProcessId)
                .SingleOrDefault(window => window.Handle == handle);
            Require(hidden != null && !hidden.Visible,
                "native enumeration must include a real hidden top-level HWND, not only Process.MainWindowHandle");
            Require(EnumerateOfficialDesktopWindows(-1).Count == 0,
                "native enumeration must not borrow another process's windows");
        }
        using (var trace = new LaunchDiagnostics(null))
        {
            Require(trace.PageObservation == null, "window reuse does not introduce a wait");
            trace.StartPageObservation();
            Require(!trace.PageObservation!.Task.IsCompleted, "activation is not page-ready");
            trace.FinishPageObservation("ready");
            trace.FinishPageObservation("timeout");
            Require(trace.PageObservation.Task.Result == "ready", "late observer cleanup cannot overwrite readiness");
        }
        ValidateDesktopNativePluginPreservation();
        using (var first = new DesktopLaunchLease())
        {
            var rejected = false;
            try { using var second = new DesktopLaunchLease(); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "repeated clicks must not queue");
        }
        using (var afterRelease = new DesktopLaunchLease()) { }
        var generation = BeginWindowsClientLaunchGeneration();
        Require(!CanSubmitStartupNavigation(generation, true, true), "visible page cannot be replaced by a delayed link");
        Require(!CanSubmitStartupNavigation(generation, false, false), "dead/reused process cannot navigate");
        Require(CanSubmitStartupNavigation(generation, true, false), "initial navigation reservation");
        Require(!CanSubmitStartupNavigation(generation, true, false), "navigation is at most once");
        _ = BeginWindowsClientLaunchGeneration();
        Require(!CanSubmitStartupNavigation(generation, true, false), "superseded navigation is cancelled");
        var completedStartup = new WindowsClientActivationIdentity(100, 1000);
        Volatile.Write(ref _pendingOfficialStartup, completedStartup);
        Interlocked.CompareExchange(ref _pendingOfficialStartup, null, completedStartup);
        Require(Volatile.Read(ref _pendingOfficialStartup) == null,
            "a completed or timed-out observer cannot keep a process pending forever");
        var nextStartup = new WindowsClientActivationIdentity(101, 2000);
        Volatile.Write(ref _pendingOfficialStartup, nextStartup);
        Interlocked.CompareExchange(ref _pendingOfficialStartup, null, completedStartup);
        Require(ReferenceEquals(Volatile.Read(ref _pendingOfficialStartup), nextStartup),
            "an old observer cannot clear a newer startup identity");
        Interlocked.CompareExchange(ref _pendingOfficialStartup, null, nextStartup);
        var existingRoot = new OfficialStartupSample(100, 1000, 1, true, false);
        Require(SelectExistingOfficialProcess([existingRoot]) == completedStartup &&
            SelectExistingOfficialProcess([existingRoot, existingRoot with { ProcessId = 101, ParentProcessId = 100 }]) == completedStartup &&
            SelectExistingOfficialProcess([existingRoot with { VerifiedPackage = false }]) == null &&
            SelectExistingOfficialProcess([existingRoot, existingRoot with { ProcessId = 102 }]) == null,
            "existing-instance activation is restricted to one verified package root, not helpers or ambiguous instances");
        Require(EvaluateStartupObservation(StartupProcessState.Alive, OfficialCodexLogReadinessState.Pending, true)
                == StartupObservation.Initializing, "slow initialization is not a crash");
        Require(EvaluateStartupObservation(StartupProcessState.Alive, OfficialCodexLogReadinessState.Ready, true)
                == StartupObservation.Ready, "slow initialization can become ready without reactivation");
        Require(EvaluateStartupObservation(StartupProcessState.Alive, OfficialCodexLogReadinessState.Pending, false)
                == StartupObservation.DetectionUnavailable, "missing logs must not imply exit");
        Require(EvaluateStartupObservation(StartupProcessState.Unobservable, OfficialCodexLogReadinessState.Ready, true)
                == StartupObservation.DetectionUnavailable, "unverifiable PID cannot borrow old ready logs");
        Require(EvaluateStartupObservation(StartupProcessState.Exited, OfficialCodexLogReadinessState.Ready, true)
                == StartupObservation.Exited, "exit must remain distinguishable");
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        Require(IsExternalAppServerPath(Path.Combine(bin, "faa963e871dd422c", "codex.exe")), "new external app-server path");
        Require(!IsExternalAppServerPath(Path.Combine(bin, "..", "other", "codex.exe")) &&
                !IsExternalAppServerPath(Path.Combine(bin, "version123", "nested", "codex.exe")) &&
                !IsExternalAppServerPath(@"C:\standalone-cli\codex.exe"), "untrusted/external CLI paths excluded");
        Require(CodexNativeFastBridge.HasSplitServiceTierContract(
            "import './app-shared-abcdef123.js';serviceTierForRequest:x"), "split bundles rejected as an incomplete contract");
        Require(!CodexNativeFastBridge.HasSplitServiceTierContract(
            "serviceTierForRequest:x;p=`priority`,f=`fast`,"), "old complete resource is not confused with split layout");
    }

    private static void ValidateDesktopNativePluginPreservation()
    {
        var current = "[plugins.\"documents@openai-primary-runtime\"]\nenabled = true\n" +
            "[plugins.\"browser@openai-bundled\"]\nenabled = false\n" +
            "[marketplaces.openai-bundled]\nsource = 'C:/runtime/plugins'\n" +
            "[model_providers.old]\nexperimental_bearer_token = 'must-not-copy'\n";
        var projected = "model = 'test-model'\n[plugins.\"documents@openai-primary-runtime\"]\nenabled = false\n" +
            "[model_providers.new]\nexperimental_bearer_token = 'selected-model-key'\n";
        var preserved = PreserveSharedDesktopRuntimeSections(current, projected);
        if (!preserved.Contains("source = 'C:/runtime/plugins'", StringComparison.Ordinal) ||
            !preserved.Contains("selected-model-key", StringComparison.Ordinal) ||
            preserved.Contains("must-not-copy", StringComparison.Ordinal) ||
            !preserved.Contains("browser@openai-bundled\"]\r\nenabled = false", StringComparison.Ordinal) ||
            preserved.Split("documents@openai-primary-runtime").Length != 2 ||
            PreserveSharedDesktopRuntimeSections(preserved, preserved) != preserved)
            throw new InvalidOperationException("Desktop native plugin preservation/credential isolation regression.");
        var api = new AccountRecord { Name = "fixture-api", AuthKind = "compatible_api",
            ApiBaseUrl = "https://example.invalid", ApiModel = "fixture-model" };
        foreach (var ordinary in new[] { false, true })
        {
            var apiConfig = ProjectCompatibleApiConfigText("[features]\nplugins = false\n", api,
                requiresOpenAiAuth: true, forceFileAuthStore: true,
                providerBearerToken: ordinary ? null : "fixture-key");
            var patConfig = ProjectWindowsClientConfigText("[features]\nplugins = false\n",
                requiresOpenAiAuth: true, forceFileAuthStore: true,
                providerBearerToken: ordinary ? null : "fixture-key");
            var expected = "plugins = " + (ordinary ? "false" : "true");
            if (!apiConfig.Contains(expected, StringComparison.Ordinal) || !patConfig.Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Native plugins must be available for both PAT/API OAuth feature launches.");
        }
    }

    internal static object AuditWindowsStartupRuntime(int processId)
    {
        using var main = Process.GetProcessById(processId);
        var identity = new WindowsClientActivationIdentity(processId, main.StartTime.ToUniversalTime().Ticks);
        var parents = CaptureProcessParentIds();
        var root = Path.GetDirectoryName(ResolveCodexWindowsClientPath());
        var children = new List<object>();
        foreach (var child in Process.GetProcessesByName("codex"))
        {
            using (child)
            {
                if (parents.GetValueOrDefault(child.Id) != processId) continue;
                children.Add(new { pid = child.Id, parentPid = processId,
                    externalRuntimeTrusted = IsTrustedExternalAppServer(child, identity, parents),
                    acceptedByUnchangedKillClassifier = IsCodexWindowsClientProcess(child, root) });
            }
        }
        // Explicit diagnostic replay, never used by a live startup readiness decision.
        var replay = new OfficialCodexLogProbe(ResolveOfficialCodexLogDirectory() ?? "",
            main.StartTime.ToUniversalTime().AddSeconds(-2),
            new Dictionary<string, OfficialCodexLogFileSnapshot>(), true,
            processId, identity.StartTimeUtcTicks, OfficialCodexLogReadinessStage.InitialLaunch);
        var replayedReadiness = replay.Poll();
        var window = FindOfficialMainWindow(identity);
        return new { pid = processId, startTimeUtc = main.StartTime.ToUniversalTime(),
            processState = ObserveStartupIdentity(identity).ToString(),
            runtimeHealthy = IsWindowsClientRuntimeHealthySince(main.StartTime.ToUniversalTime().AddSeconds(-2), identity),
            observationOnly = true, children, replayedOfficialLogReadiness = replayedReadiness.ToString(),
            standardMainWindowFound = main.MainWindowHandle != IntPtr.Zero,
            windowFound = window != null, windowVisible = window?.Visible,
            windowMinimized = window?.Minimized, windowClassName = window?.ClassName,
            replay.AppServerConnectedAtUtc, replay.RoutesMountedAtUtc, replay.ReadyAtUtc };
    }

    internal static void ValidateWindowsStartupSafety()
    {
        ValidateOfficialCodexStartupObservation();
        OfficialCodexLogReadiness.Validate();
        CodexNativeFastBridge.ValidatePatchContract();
        ChatSectionSynchronizationService.Validate();
    }
}
