using System.Diagnostics;

namespace CodexAccountManager;

public sealed partial class CodexCliService
{
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
        throw new InvalidOperationException(
            "Windows 已接受 Codex 启动，但 5 秒内未能确认原进程或其启动交接窗口。" +
            "管理器没有关闭或重复启动 Codex；凭据已保留。若窗口已打开，请勿重复点击启动。");
    }

    private static void ValidateOfficialCodexStartupObservation()
    {
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
}
