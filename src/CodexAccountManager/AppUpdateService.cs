using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal sealed record AppUpdateInfo(
    string Version,
    string Commit,
    string ReleaseUrl,
    string AssetName,
    string AssetUrl,
    string Sha256);

internal enum AppUpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    NetworkUnavailable,
    ReleaseUnavailable,
    ManifestMissing,
    ManifestInvalid,
    PlatformAssetMissing
}

internal sealed record AppUpdateCheckResult(
    AppUpdateInfo? Update,
    AppUpdateCheckStatus Status);

internal sealed record AppUpdateProgress(string Message);

/// <summary>
/// Reads the rolling "latest" GitHub Release and schedules the trusted package
/// installer after the current process has exited. The release workflow publishes
/// update-manifest.json alongside platform-specific ZIP files.
/// </summary>
internal sealed class AppUpdateService
{
    internal const string PreserveExistingGatewayArgument = "--preserve-existing-pat-gateway";
    internal const string RefreshNativeFastBridgeArgument = "--refresh-native-fast-bridge-after-update";
    private const string Repository = "bennettnaida-cloud/codex-account-manager";
    private const string ReleaseApiUrl = "https://api.github.com/repos/" + Repository + "/releases/tags/latest";
    private const long MaximumDownloadBytes = 800L * 1024L * 1024L;
    private const string UpdateSessionMarkerFileName = ".codex-account-manager-update-session";
    private const string UpdateSessionMarkerContent = "CodexAccountManager update session v1";
    private static readonly TimeSpan StaleUpdateSessionAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(90);

    internal static string CurrentVersion =>
        NormalizeVersion(Assembly.GetEntryAssembly()?.GetName().Version?.ToString()) ?? "0.0.0.0";

    internal static string DisplayVersion
    {
        get
        {
            if (!Version.TryParse(CurrentVersion, out var version))
            {
                return CurrentVersion;
            }

            return version.Build >= 0
                ? $"{version.Major}.{version.Minor}.{version.Build}" +
                  (version.Revision > 0 ? $".{version.Revision}" : string.Empty)
                : $"{version.Major}.{version.Minor}";
        }
    }

    internal async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = CreateHttpClient();
            using var releaseDocument = await GetJsonAsync(
                http,
                ReleaseApiUrl,
                cancellationToken,
                networkFailureStatus: AppUpdateCheckStatus.NetworkUnavailable,
                httpFailureStatus: AppUpdateCheckStatus.ReleaseUnavailable,
                invalidJsonStatus: AppUpdateCheckStatus.ReleaseUnavailable).ConfigureAwait(false);

            var release = releaseDocument.RootElement;
            var releaseUrl = ReadString(release, "html_url") ?? "https://github.com/" + Repository + "/releases";
            var releaseAssets = ReadAssets(release);
            var manifestAsset = releaseAssets.FirstOrDefault(asset =>
                string.Equals(asset.Name, "update-manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifestAsset is null)
            {
                return new AppUpdateCheckResult(null, AppUpdateCheckStatus.ManifestMissing);
            }

            using var manifestDocument = await GetJsonAsync(
                http,
                manifestAsset.Url,
                cancellationToken,
                networkFailureStatus: AppUpdateCheckStatus.NetworkUnavailable,
                httpFailureStatus: AppUpdateCheckStatus.ManifestMissing,
                invalidJsonStatus: AppUpdateCheckStatus.ManifestInvalid).ConfigureAwait(false);

            var manifest = manifestDocument.RootElement;
            var remoteVersion = NormalizeVersion(ReadString(manifest, "version"));
            if (remoteVersion is null)
            {
                return new AppUpdateCheckResult(null, AppUpdateCheckStatus.ManifestInvalid);
            }

            if (!IsNewer(remoteVersion, CurrentVersion))
            {
                return new AppUpdateCheckResult(null, AppUpdateCheckStatus.UpToDate);
            }

            var commit = ReadString(manifest, "commit") ?? string.Empty;
            var platformName = "windows";
            if (!manifest.TryGetProperty("assets", out var manifestAssets) ||
                manifestAssets.ValueKind != JsonValueKind.Object ||
                !manifestAssets.TryGetProperty(platformName, out var platformAsset) ||
                platformAsset.ValueKind != JsonValueKind.Object)
            {
                return new AppUpdateCheckResult(null, AppUpdateCheckStatus.PlatformAssetMissing);
            }

            var assetName = ReadString(platformAsset, "name");
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return new AppUpdateCheckResult(null, AppUpdateCheckStatus.PlatformAssetMissing);
            }

            var releaseAsset = releaseAssets.FirstOrDefault(asset =>
                string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (releaseAsset is null)
            {
                return new AppUpdateCheckResult(null, AppUpdateCheckStatus.PlatformAssetMissing);
            }

            var sha256 = NormalizeSha256(ReadString(platformAsset, "sha256"));
            if (sha256 is null)
            {
                sha256 = NormalizeSha256(releaseAsset.Digest?.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase));
            }
            if (sha256 is null)
            {
                return new AppUpdateCheckResult(null, AppUpdateCheckStatus.ManifestInvalid);
            }

            return new AppUpdateCheckResult(
                new AppUpdateInfo(
                    remoteVersion,
                    commit,
                    releaseUrl,
                    releaseAsset.Name,
                    releaseAsset.Url,
                    sha256),
                AppUpdateCheckStatus.UpdateAvailable);
        }
        catch (UpdateCheckException error)
        {
            return new AppUpdateCheckResult(null, error.Status);
        }
    }

    internal async Task ScheduleInstallAsync(
        AppUpdateInfo update,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var currentExecutablePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "CodexAccountManager.exe");
        EnsurePathIsOutsideUpdateStaging(
            Path.GetDirectoryName(currentExecutablePath) ?? AppContext.BaseDirectory,
            "The running application");
        var installPath = GetVersionedInstallPath(update.Version);
        EnsurePathIsOutsideUpdateStaging(installPath, "The update install target");

        var updateStagingRoot = GetUpdateStagingRoot();
        CleanupStaleUpdateSessions(updateStagingRoot);
        var updateRoot = Path.Combine(
            updateStagingRoot,
            "sessions",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateRoot);
        File.WriteAllText(
            Path.Combine(updateRoot, UpdateSessionMarkerFileName),
            UpdateSessionMarkerContent,
            Encoding.ASCII);
        var zipPath = Path.Combine(updateRoot, Path.GetFileName(update.AssetName));
        var extractRoot = Path.Combine(updateRoot, "extracted");
        var updateStateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexAccountManager",
            "UpdaterLogs");
        Directory.CreateDirectory(updateStateRoot);
        var logSuffix = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId;
        var updaterLogPath = Path.Combine(updateStateRoot, $"Update-{logSuffix}.log");
        var installerLogPath = Path.Combine(updateStateRoot, $"Install-{logSuffix}.log");
        var failureMarkerPath = Path.Combine(updateStateRoot, "last-update-error.txt");

        try
        {
            progress?.Report(new AppUpdateProgress("正在连接 GitHub 并下载更新包……"));
            using var downloadHttp = CreateDownloadHttpClient();
            await DownloadAndVerifyAsync(
                    downloadHttp,
                    update,
                    zipPath,
                    GetUpdateDownloadCacheRoot(),
                    progress,
                    cancellationToken,
                    DownloadIdleTimeout)
                .ConfigureAwait(false);
            progress?.Report(new AppUpdateProgress("下载与校验完成，正在解压更新包……"));
            ExtractZipSafely(zipPath, extractRoot);
            var installerPath = Directory
                .GetFiles(extractRoot, "Install-CodexAccountManager.ps1", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (installerPath is null)
            {
                throw new InvalidOperationException("更新包缺少 Windows 安装脚本。");
            }

            var helperPath = Path.Combine(updateRoot, "apply-update.ps1");
            // Windows PowerShell 5.1 treats BOM-less UTF-8 as the active ANSI code page.
            // A BOM keeps paths and localized text parseable on every supported Windows locale.
            await File.WriteAllTextAsync(helperPath, BuildHelperScript(), new UTF8Encoding(true), cancellationToken)
                .ConfigureAwait(false);

            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powershell))
            {
                powershell = "powershell.exe";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var managerRoot = Path.GetFullPath(new AccountStore().RootPath);
            startInfo.Environment["CODEX_ACCOUNT_MANAGER_HOME"] = managerRoot;
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(helperPath);
            startInfo.ArgumentList.Add("-ProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-InstallerPath");
            startInfo.ArgumentList.Add(installerPath);
            startInfo.ArgumentList.Add("-CleanupRoot");
            startInfo.ArgumentList.Add(updateRoot);
            startInfo.ArgumentList.Add("-InstallPath");
            startInfo.ArgumentList.Add(installPath);
            startInfo.ArgumentList.Add("-WorkingDirectory");
            startInfo.ArgumentList.Add(managerRoot);
            startInfo.ArgumentList.Add("-ManagerRoot");
            startInfo.ArgumentList.Add(managerRoot);
            startInfo.ArgumentList.Add("-CurrentExecutablePath");
            startInfo.ArgumentList.Add(currentExecutablePath);
            startInfo.ArgumentList.Add("-UpdaterLogPath");
            startInfo.ArgumentList.Add(updaterLogPath);
            startInfo.ArgumentList.Add("-InstallerLogPath");
            startInfo.ArgumentList.Add(installerLogPath);
            startInfo.ArgumentList.Add("-FailureMarkerPath");
            startInfo.ArgumentList.Add(failureMarkerPath);

            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("无法启动更新安装程序。");
            }
            progress?.Report(new AppUpdateProgress("更新包已准备完成，正在关闭旧版本并安装……"));
        }
        catch
        {
            try { Directory.Delete(updateRoot, recursive: true); } catch { }
            throw;
        }
    }

    internal static string? ConsumePendingFailure()
    {
        var markerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexAccountManager",
            "UpdaterLogs",
            "last-update-error.txt");
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            var message = File.ReadAllText(markerPath).Trim();
            File.Delete(markerPath);
            return string.IsNullOrWhiteSpace(message) ? "更新安装程序未能完成安装。" : message;
        }
        catch
        {
            return "更新安装程序未能完成安装，请查看更新日志。";
        }
    }

    internal static void ValidateUpdateHelperScript()
    {
        var script = BuildHelperScript();
        var failures = new List<string>();
        if (!Version.TryParse(DisplayVersion, out _) || DisplayVersion.Contains('+')) failures.Add("display-version");
        if (script.Any(character => character > 0x7f)) failures.Add("non-ascii-content");
        if (!script.Contains("powershell.exe", StringComparison.Ordinal)) failures.Add("child-powershell");
        if (!script.Contains("$InstallerLogPath", StringComparison.Ordinal)) failures.Add("installer-log");
        if (!script.Contains("function Wait-ProcessExit", StringComparison.Ordinal) ||
            !script.Contains("Start-Sleep -Milliseconds 250", StringComparison.Ordinal) ||
            script.Contains("Wait-Process -Id", StringComparison.Ordinal)) failures.Add("powershell-5-exit-wait");
        if (!script.Contains("$previousProcessExited = $false", StringComparison.Ordinal) ||
            !script.Contains("$previousProcessExited = $true", StringComparison.Ordinal) ||
            !script.Contains("$pathsValidated -and $previousProcessExited -and", StringComparison.Ordinal))
        {
            failures.Add("pre-exit-restart-guard");
        }
        if (script.Contains("--shutdown-local-pat-gateway", StringComparison.Ordinal)) failures.Add("gateway-interruption");
        if (!script.Contains("--preserve-existing-pat-gateway", StringComparison.Ordinal)) failures.Add("gateway-preservation");
        if (!script.Contains("--refresh-native-fast-bridge-after-update", StringComparison.Ordinal)) failures.Add("native-fast-refresh");
        if (!script.Contains("$nativeFastArgument", StringComparison.Ordinal) ||
            !script.Contains("$gatewayArgument", StringComparison.Ordinal) ||
            !script.Contains("function Get-ExactNativeFastBridgeProcess", StringComparison.Ordinal) ||
            !script.Contains("$candidate.CreationDate -ne $ExpectedCreationDate", StringComparison.Ordinal) ||
            !script.Contains("$_.CommandLine.IndexOf($gatewayArgument, [StringComparison]::OrdinalIgnoreCase) -lt 0", StringComparison.Ordinal) ||
            !script.Contains("Stop-Process -Id $bridge.ProcessId", StringComparison.Ordinal)) failures.Add("bridge-only-stop");
        if (!script.Contains("Start-Process -FilePath $installedExe", StringComparison.Ordinal)) failures.Add("restart");
        if (!script.Contains("$env:CODEX_ACCOUNT_MANAGER_HOME = $managerRoot", StringComparison.Ordinal)) failures.Add("manager-root-environment");
        if (script.Contains("rollbackExecutablePath", StringComparison.Ordinal)) failures.Add("temporary-rollback");
        var versionedProbePath = GetVersionedInstallPath("999.999.999");
        var expectedVersionRoot = GetVersionedInstallRoot();
        if (!IsSamePathOrDescendant(versionedProbePath, expectedVersionRoot) ||
            IsSamePathOrDescendant(versionedProbePath, GetUpdateStagingRoot()))
        {
            failures.Add("versioned-install-path");
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Updater helper script validation failed: " + string.Join(", ", failures));
        }

        var probeRoot = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-updater-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeRoot);
        try
        {
            var cleanupRoot = Path.Combine(probeRoot, "download");
            Directory.CreateDirectory(cleanupRoot);
            var probePath = Path.Combine(cleanupRoot, "apply-update.ps1");
            File.WriteAllText(probePath, script, new UTF8Encoding(true));
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = File.Exists(powershell) ? powershell : "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["CAM_UPDATE_HELPER_PROBE"] = probePath;
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$tokens=$null;$errors=$null;" +
                "[System.Management.Automation.Language.Parser]::ParseFile(" +
                "$env:CAM_UPDATE_HELPER_PROBE,[ref]$tokens,[ref]$errors)|Out-Null;" +
                "if($errors.Count -gt 0){$errors|ForEach-Object{$_.Message};exit 1}");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Updater parser probe could not start PowerShell.");
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("Updater parser probe timed out.");
            }
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Updater helper script does not parse in Windows PowerShell 5.1: " +
                    (string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError).Trim());
            }

            var fakeInstallerPath = Path.Combine(probeRoot, "fake-installer.ps1");
            File.WriteAllText(
                fakeInstallerPath,
                @"
param(
    [switch]$Quiet,
    [switch]$NoLaunch,
    [string]$InstallPath,
    [string]$ManagerWorkingDirectory,
    [string]$LogPath
)
$ErrorActionPreference = 'Stop'
if ($env:CAM_WAIT_PROBE_PID -and
    $null -ne (Get-Process -Id ([int]$env:CAM_WAIT_PROBE_PID) -ErrorAction SilentlyContinue)) {
    throw 'Updater started the installer before the previous process exited.'
}
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
$testExe = Join-Path $env:WINDIR 'System32\where.exe'
Copy-Item -LiteralPath $testExe -Destination (Join-Path $InstallPath 'CodexAccountManager.exe') -Force
Set-Content -LiteralPath (Join-Path $InstallPath 'installer-ran.txt') -Value 'ok' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $InstallPath 'manager-root.txt') -Value $env:CODEX_ACCOUNT_MANAGER_HOME -Encoding UTF8
Set-Content -LiteralPath (Join-Path $InstallPath 'manager-working-directory.txt') -Value $ManagerWorkingDirectory -Encoding UTF8
exit 0
",
                new UTF8Encoding(true));

            var installPath = Path.Combine(probeRoot, "installed");
            var updaterLogPath = Path.Combine(probeRoot, "logs", "update.log");
            var installerLogPath = Path.Combine(probeRoot, "logs", "install.log");
            var failureMarkerPath = Path.Combine(probeRoot, "logs", "failure.txt");
            var waitProbeInfo = new ProcessStartInfo
            {
                FileName = startInfo.FileName,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            waitProbeInfo.ArgumentList.Add("-NoLogo");
            waitProbeInfo.ArgumentList.Add("-NoProfile");
            waitProbeInfo.ArgumentList.Add("-NonInteractive");
            waitProbeInfo.ArgumentList.Add("-Command");
            waitProbeInfo.ArgumentList.Add("Start-Sleep -Milliseconds 1250");
            using var waitProbe = Process.Start(waitProbeInfo)
                ?? throw new InvalidOperationException("Updater process-wait probe could not start PowerShell.");
            var executionInfo = new ProcessStartInfo
            {
                FileName = startInfo.FileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            executionInfo.Environment["CAM_WAIT_PROBE_PID"] = waitProbe.Id.ToString();
            executionInfo.ArgumentList.Add("-NoLogo");
            executionInfo.ArgumentList.Add("-NoProfile");
            executionInfo.ArgumentList.Add("-NonInteractive");
            executionInfo.ArgumentList.Add("-ExecutionPolicy");
            executionInfo.ArgumentList.Add("Bypass");
            executionInfo.ArgumentList.Add("-File");
            executionInfo.ArgumentList.Add(probePath);
            executionInfo.ArgumentList.Add("-ProcessId");
            executionInfo.ArgumentList.Add(waitProbe.Id.ToString());
            executionInfo.ArgumentList.Add("-InstallerPath");
            executionInfo.ArgumentList.Add(fakeInstallerPath);
            executionInfo.ArgumentList.Add("-CleanupRoot");
            executionInfo.ArgumentList.Add(cleanupRoot);
            executionInfo.ArgumentList.Add("-InstallPath");
            executionInfo.ArgumentList.Add(installPath);
            executionInfo.ArgumentList.Add("-WorkingDirectory");
            executionInfo.ArgumentList.Add(probeRoot);
            executionInfo.ArgumentList.Add("-ManagerRoot");
            executionInfo.ArgumentList.Add(probeRoot);
            executionInfo.ArgumentList.Add("-CurrentExecutablePath");
            executionInfo.ArgumentList.Add(Path.Combine(probeRoot, "missing-current.exe"));
            executionInfo.ArgumentList.Add("-UpdaterLogPath");
            executionInfo.ArgumentList.Add(updaterLogPath);
            executionInfo.ArgumentList.Add("-InstallerLogPath");
            executionInfo.ArgumentList.Add(installerLogPath);
            executionInfo.ArgumentList.Add("-FailureMarkerPath");
            executionInfo.ArgumentList.Add(failureMarkerPath);

            var executionStopwatch = Stopwatch.StartNew();
            using var execution = Process.Start(executionInfo)
                ?? throw new InvalidOperationException("Updater execution probe could not start PowerShell.");
            if (!execution.WaitForExit(30_000))
            {
                try { execution.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("Updater execution probe timed out.");
            }
            var executionOutput = execution.StandardOutput.ReadToEnd();
            var executionError = execution.StandardError.ReadToEnd();
            executionStopwatch.Stop();
            waitProbe.Refresh();
            var installerMarker = Path.Combine(installPath, "installer-ran.txt");
            var expectedManagerRoot = Path.GetFullPath(probeRoot);
            var inheritedManagerRoot = File.Exists(Path.Combine(installPath, "manager-root.txt"))
                ? File.ReadAllText(Path.Combine(installPath, "manager-root.txt")).Trim()
                : string.Empty;
            var installerManagerRoot = File.Exists(Path.Combine(installPath, "manager-working-directory.txt"))
                ? File.ReadAllText(Path.Combine(installPath, "manager-working-directory.txt")).Trim()
                : string.Empty;
            if (execution.ExitCode != 0 ||
                !waitProbe.HasExited ||
                executionStopwatch.Elapsed < TimeSpan.FromMilliseconds(750) ||
                !File.Exists(installerMarker) ||
                File.Exists(failureMarkerPath) ||
                !File.Exists(updaterLogPath) ||
                !inheritedManagerRoot.Equals(expectedManagerRoot, StringComparison.OrdinalIgnoreCase) ||
                !installerManagerRoot.Equals(expectedManagerRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.ReadAllText(updaterLogPath).Contains(
                    "Installation completed. Restarting the updated application.",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Updater execution probe failed: " +
                    (string.IsNullOrWhiteSpace(executionError) ? executionOutput : executionError).Trim());
            }

            var timeoutCleanupRoot = Path.Combine(probeRoot, "timeout-download");
            Directory.CreateDirectory(timeoutCleanupRoot);
            var timeoutProbePath = Path.Combine(timeoutCleanupRoot, "apply-update.ps1");
            File.WriteAllText(timeoutProbePath, script, new UTF8Encoding(true));
            var timeoutInstallPath = Path.Combine(probeRoot, "timeout-installed");
            var timeoutUpdaterLogPath = Path.Combine(probeRoot, "logs", "timeout-update.log");
            var timeoutInstallerLogPath = Path.Combine(probeRoot, "logs", "timeout-install.log");
            var timeoutFailureMarkerPath = Path.Combine(probeRoot, "logs", "timeout-failure.txt");
            var timeoutWaitProbeInfo = new ProcessStartInfo
            {
                FileName = startInfo.FileName,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            timeoutWaitProbeInfo.ArgumentList.Add("-NoLogo");
            timeoutWaitProbeInfo.ArgumentList.Add("-NoProfile");
            timeoutWaitProbeInfo.ArgumentList.Add("-NonInteractive");
            timeoutWaitProbeInfo.ArgumentList.Add("-Command");
            timeoutWaitProbeInfo.ArgumentList.Add("Start-Sleep -Seconds 15");
            using var timeoutWaitProbe = Process.Start(timeoutWaitProbeInfo)
                ?? throw new InvalidOperationException("Updater timeout probe process could not start PowerShell.");
            try
            {
                var timeoutExecutionInfo = new ProcessStartInfo
                {
                    FileName = startInfo.FileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                timeoutExecutionInfo.ArgumentList.Add("-NoLogo");
                timeoutExecutionInfo.ArgumentList.Add("-NoProfile");
                timeoutExecutionInfo.ArgumentList.Add("-NonInteractive");
                timeoutExecutionInfo.ArgumentList.Add("-ExecutionPolicy");
                timeoutExecutionInfo.ArgumentList.Add("Bypass");
                timeoutExecutionInfo.ArgumentList.Add("-File");
                timeoutExecutionInfo.ArgumentList.Add(timeoutProbePath);
                timeoutExecutionInfo.ArgumentList.Add("-ProcessId");
                timeoutExecutionInfo.ArgumentList.Add(timeoutWaitProbe.Id.ToString());
                timeoutExecutionInfo.ArgumentList.Add("-InstallerPath");
                timeoutExecutionInfo.ArgumentList.Add(fakeInstallerPath);
                timeoutExecutionInfo.ArgumentList.Add("-CleanupRoot");
                timeoutExecutionInfo.ArgumentList.Add(timeoutCleanupRoot);
                timeoutExecutionInfo.ArgumentList.Add("-InstallPath");
                timeoutExecutionInfo.ArgumentList.Add(timeoutInstallPath);
                timeoutExecutionInfo.ArgumentList.Add("-WorkingDirectory");
                timeoutExecutionInfo.ArgumentList.Add(probeRoot);
                timeoutExecutionInfo.ArgumentList.Add("-ManagerRoot");
                timeoutExecutionInfo.ArgumentList.Add(probeRoot);
                timeoutExecutionInfo.ArgumentList.Add("-CurrentExecutablePath");
                timeoutExecutionInfo.ArgumentList.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32",
                    "where.exe"));
                timeoutExecutionInfo.ArgumentList.Add("-UpdaterLogPath");
                timeoutExecutionInfo.ArgumentList.Add(timeoutUpdaterLogPath);
                timeoutExecutionInfo.ArgumentList.Add("-InstallerLogPath");
                timeoutExecutionInfo.ArgumentList.Add(timeoutInstallerLogPath);
                timeoutExecutionInfo.ArgumentList.Add("-FailureMarkerPath");
                timeoutExecutionInfo.ArgumentList.Add(timeoutFailureMarkerPath);
                timeoutExecutionInfo.ArgumentList.Add("-PreviousProcessExitTimeoutSeconds");
                timeoutExecutionInfo.ArgumentList.Add("1");

                using var timeoutExecution = Process.Start(timeoutExecutionInfo)
                    ?? throw new InvalidOperationException("Updater timeout execution probe could not start PowerShell.");
                if (!timeoutExecution.WaitForExit(10_000))
                {
                    try { timeoutExecution.Kill(entireProcessTree: true); } catch { }
                    throw new InvalidOperationException("Updater timeout execution probe did not terminate.");
                }
                var timeoutOutput = timeoutExecution.StandardOutput.ReadToEnd();
                var timeoutError = timeoutExecution.StandardError.ReadToEnd();
                timeoutWaitProbe.Refresh();
                var timeoutLog = File.Exists(timeoutUpdaterLogPath)
                    ? File.ReadAllText(timeoutUpdaterLogPath)
                    : string.Empty;
                if (timeoutExecution.ExitCode != 1 ||
                    timeoutWaitProbe.HasExited ||
                    !File.Exists(timeoutFailureMarkerPath) ||
                    File.Exists(Path.Combine(timeoutInstallPath, "installer-ran.txt")) ||
                    !timeoutLog.Contains("did not exit within 1 seconds", StringComparison.Ordinal) ||
                    timeoutLog.Contains("Restarted the previous application", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Updater pre-exit timeout fallback probe failed: " +
                        (string.IsNullOrWhiteSpace(timeoutError) ? timeoutOutput : timeoutError).Trim());
                }
            }
            finally
            {
                timeoutWaitProbe.Refresh();
                if (!timeoutWaitProbe.HasExited)
                {
                    try { timeoutWaitProbe.Kill(entireProcessTree: true); } catch { }
                    try { timeoutWaitProbe.WaitForExit(5_000); } catch { }
                }
            }
        }
        finally
        {
            try { Directory.Delete(probeRoot, recursive: true); } catch { }
        }
    }

    internal static void ValidateResumableDownload() =>
        ValidateResumableDownloadAsync().GetAwaiter().GetResult();

    private static async Task ValidateResumableDownloadAsync()
    {
        var probeRoot = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-download-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeRoot);
        try
        {
            using (var configuredProxyHandler = CreateConfiguredProxyHandler("http://127.0.0.1:18080"))
            {
                var resolvedProxy = configuredProxyHandler.Proxy?.GetProxy(new Uri("https://github.com"));
                if (!configuredProxyHandler.UseProxy || resolvedProxy?.Port != 18080)
                {
                    throw new InvalidOperationException("The updater did not apply the software-configured proxy.");
                }
            }
            using (var systemProxyHandler = CreateConfiguredProxyHandler(configuredProxy: null))
            {
                if (!systemProxyHandler.UseProxy || systemProxyHandler.Proxy is not null)
                {
                    throw new InvalidOperationException("The updater did not preserve the system-default proxy path.");
                }
            }

            var payload = Enumerable.Range(0, 257).Select(index => (byte)(index % 251)).ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(payload));
            var update = new AppUpdateInfo(
                "999.999.999",
                "probe",
                "https://example.invalid/release",
                "CodexAccountManager-Windows-probe.zip",
                "https://example.invalid/package.zip",
                sha256);

            var resumeCache = Path.Combine(probeRoot, "resume-cache");
            var resumeDestination = Path.Combine(probeRoot, "resume.zip");
            using (var truncatedClient = new HttpClient(
                       new DownloadProbeHandler(payload, supportsRanges: true, truncateFreshAt: 73)))
            {
                try
                {
                    await DownloadAndVerifyAsync(
                        truncatedClient,
                        update,
                        resumeDestination,
                        resumeCache,
                        progress: null,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    throw new InvalidOperationException("Truncated download probe unexpectedly completed.");
                }
                catch (IOException error) when (error.Message.Contains("下载未完成", StringComparison.Ordinal))
                {
                }
            }

            var retainedPartials = Directory.GetFiles(resumeCache, "*.partial", SearchOption.TopDirectoryOnly);
            if (retainedPartials.Length != 1 || new FileInfo(retainedPartials[0]).Length != 73)
            {
                throw new InvalidOperationException("A failed update did not retain exactly one resumable partial file.");
            }

            var resumeHandler = new DownloadProbeHandler(payload, supportsRanges: true);
            using (var resumeClient = new HttpClient(resumeHandler))
            {
                await DownloadAndVerifyAsync(
                    resumeClient,
                    update,
                    resumeDestination,
                    resumeCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (resumeHandler.RequestedOffsets is not [73] ||
                resumeHandler.RequestedIfRanges is not ["\"download-probe\""] ||
                !File.ReadAllBytes(resumeDestination).SequenceEqual(payload) ||
                Directory.GetFiles(resumeCache, "*.partial", SearchOption.TopDirectoryOnly).Length != 0 ||
                !File.Exists(Path.Combine(resumeCache, "windows-update.verified.zip")))
            {
                throw new InvalidOperationException("A compatible partial update did not resume and promote atomically.");
            }

            File.Delete(resumeDestination);
            var reusedDestination = Path.Combine(probeRoot, "resume-reused.zip");
            var reuseHandler = new DownloadProbeHandler(payload, supportsRanges: true);
            using (var reuseClient = new HttpClient(reuseHandler))
            {
                await DownloadAndVerifyAsync(
                    reuseClient,
                    update,
                    reusedDestination,
                    resumeCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (reuseHandler.RequestedOffsets.Count != 0 ||
                !File.ReadAllBytes(reusedDestination).SequenceEqual(payload) ||
                !File.Exists(Path.Combine(resumeCache, "windows-update.verified.zip")))
            {
                throw new InvalidOperationException("A verified package was not reused after an installation session was discarded.");
            }

            var orphanVerifiedCache = Path.Combine(probeRoot, "orphan-verified-cache");
            Directory.CreateDirectory(orphanVerifiedCache);
            var orphanVerifiedPath = Path.Combine(orphanVerifiedCache, "windows-update.verified.zip");
            var orphanVerifiedMetadataPath = Path.Combine(orphanVerifiedCache, "windows-update.verified.json");
            File.WriteAllBytes(orphanVerifiedPath, payload);
            var orphanVerifiedDestination = Path.Combine(probeRoot, "orphan-verified.zip");
            var orphanVerifiedHandler = new DownloadProbeHandler(payload, supportsRanges: true);
            using (var orphanVerifiedClient = new HttpClient(orphanVerifiedHandler))
            {
                await DownloadAndVerifyAsync(
                    orphanVerifiedClient,
                    update,
                    orphanVerifiedDestination,
                    orphanVerifiedCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            var recoveredVerifiedMetadata = ReadResumeMetadata(orphanVerifiedMetadataPath);
            if (orphanVerifiedHandler.RequestedOffsets.Count != 0 ||
                !File.ReadAllBytes(orphanVerifiedDestination).SequenceEqual(payload) ||
                recoveredVerifiedMetadata is null ||
                !ResumeMetadataMatches(recoveredVerifiedMetadata, update) ||
                recoveredVerifiedMetadata.TotalLength != payload.LongLength)
            {
                throw new InvalidOperationException("An orphan verified package was not revalidated and recovered without downloading.");
            }

            var replacementPayload = payload.Select(value => (byte)(value ^ 0x3c)).ToArray();
            var replacementUpdate = update with
            {
                Version = "999.999.1000",
                AssetName = "CodexAccountManager-Windows-replacement.zip",
                AssetUrl = "https://example.invalid/replacement.zip",
                Sha256 = Convert.ToHexString(SHA256.HashData(replacementPayload))
            };
            var verifiedIdentityDestination = Path.Combine(probeRoot, "verified-identity.zip");
            var verifiedIdentityHandler = new DownloadProbeHandler(replacementPayload, supportsRanges: true);
            using (var verifiedIdentityClient = new HttpClient(verifiedIdentityHandler))
            {
                await DownloadAndVerifyAsync(
                    verifiedIdentityClient,
                    replacementUpdate,
                    verifiedIdentityDestination,
                    resumeCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (verifiedIdentityHandler.RequestedOffsets is not [null] ||
                !File.ReadAllBytes(verifiedIdentityDestination).SequenceEqual(replacementPayload))
            {
                throw new InvalidOperationException("A verified package with a different identity was not replaced safely.");
            }
            var corruptedVerified = replacementPayload.ToArray();
            corruptedVerified[0] ^= 0x7f;
            File.WriteAllBytes(
                Path.Combine(resumeCache, "windows-update.verified.zip"),
                corruptedVerified);
            var verifiedRepairDestination = Path.Combine(probeRoot, "verified-repair.zip");
            var verifiedRepairHandler = new DownloadProbeHandler(replacementPayload, supportsRanges: true);
            using (var verifiedRepairClient = new HttpClient(verifiedRepairHandler))
            {
                await DownloadAndVerifyAsync(
                    verifiedRepairClient,
                    replacementUpdate,
                    verifiedRepairDestination,
                    resumeCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (verifiedRepairHandler.RequestedOffsets is not [null] ||
                !File.ReadAllBytes(verifiedRepairDestination).SequenceEqual(replacementPayload))
            {
                throw new InvalidOperationException("A corrupt verified package was reused instead of being replaced.");
            }

            var identityCache = Path.Combine(probeRoot, "identity-cache");
            var identityDestination = Path.Combine(probeRoot, "identity.zip");
            using (var truncatedClient = new HttpClient(
                       new DownloadProbeHandler(payload, supportsRanges: true, truncateFreshAt: 29)))
            {
                try
                {
                    await DownloadAndVerifyAsync(
                        truncatedClient,
                        update,
                        identityDestination,
                        identityCache,
                        progress: null,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (IOException error) when (error.Message.Contains("下载未完成", StringComparison.Ordinal))
                {
                }
            }
            var identityHandler = new DownloadProbeHandler(replacementPayload, supportsRanges: true);
            using (var identityClient = new HttpClient(identityHandler))
            {
                await DownloadAndVerifyAsync(
                    identityClient,
                    replacementUpdate,
                    identityDestination,
                    identityCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (identityHandler.RequestedOffsets is not [null] ||
                !File.ReadAllBytes(identityDestination).SequenceEqual(replacementPayload))
            {
                throw new InvalidOperationException("A partial package with a different identity was not discarded before download.");
            }

            var contentRangeCache = Path.Combine(probeRoot, "content-range-cache");
            var contentRangeDestination = Path.Combine(probeRoot, "content-range.zip");
            await CreateTruncatedDownloadProbeAsync(
                update,
                payload,
                contentRangeCache,
                contentRangeDestination,
                67).ConfigureAwait(false);
            var contentRangeHandler = new DownloadProbeHandler(
                payload,
                supportsRanges: true,
                rangeStartAdjustment: 1);
            using (var contentRangeClient = new HttpClient(contentRangeHandler))
            {
                await DownloadAndVerifyAsync(
                    contentRangeClient,
                    update,
                    contentRangeDestination,
                    contentRangeCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (contentRangeHandler.RequestedOffsets is not [67, null])
            {
                throw new InvalidOperationException("An invalid Content-Range did not trigger a clean full download.");
            }

            var validatorCache = Path.Combine(probeRoot, "validator-cache");
            var validatorDestination = Path.Combine(probeRoot, "validator.zip");
            await CreateTruncatedDownloadProbeAsync(
                update,
                payload,
                validatorCache,
                validatorDestination,
                83).ConfigureAwait(false);
            var validatorHandler = new DownloadProbeHandler(
                payload,
                supportsRanges: true,
                responseETag: "\"changed-validator\"");
            using (var validatorClient = new HttpClient(validatorHandler))
            {
                await DownloadAndVerifyAsync(
                    validatorClient,
                    update,
                    validatorDestination,
                    validatorCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (validatorHandler.RequestedOffsets is not [83, null])
            {
                throw new InvalidOperationException("A changed download validator did not trigger a clean full download.");
            }

            var lastModifiedCache = Path.Combine(probeRoot, "last-modified-cache");
            var lastModifiedDestination = Path.Combine(probeRoot, "last-modified.zip");
            using (var lastModifiedSetupClient = new HttpClient(new DownloadProbeHandler(
                       payload,
                       supportsRanges: true,
                       truncateFreshAt: 71,
                       responseETag: null,
                       responseLastModified: DateTimeOffset.UnixEpoch)))
            {
                try
                {
                    await DownloadAndVerifyAsync(
                        lastModifiedSetupClient,
                        update,
                        lastModifiedDestination,
                        lastModifiedCache,
                        progress: null,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (IOException error) when (error.Message.Contains("下载未完成", StringComparison.Ordinal))
                {
                }
            }
            var lastModifiedHandler = new DownloadProbeHandler(
                payload,
                supportsRanges: true,
                responseETag: null,
                responseLastModified: DateTimeOffset.UnixEpoch.AddDays(1));
            using (var lastModifiedClient = new HttpClient(lastModifiedHandler))
            {
                await DownloadAndVerifyAsync(
                    lastModifiedClient,
                    update,
                    lastModifiedDestination,
                    lastModifiedCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (lastModifiedHandler.RequestedOffsets is not [71, null] ||
                lastModifiedHandler.RequestedIfRanges[0] is null)
            {
                throw new InvalidOperationException("A changed Last-Modified validator did not trigger a clean full download.");
            }

            var lockCache = Path.Combine(probeRoot, "lock-cache");
            Directory.CreateDirectory(lockCache);
            await using (var heldLock = new FileStream(
                             Path.Combine(lockCache, "windows-update.lock"),
                             FileMode.OpenOrCreate,
                             FileAccess.ReadWrite,
                             FileShare.None))
            using (var lockClient = new HttpClient(new DownloadProbeHandler(payload, supportsRanges: true)))
            {
                try
                {
                    await DownloadAndVerifyAsync(
                        lockClient,
                        update,
                        Path.Combine(probeRoot, "locked.zip"),
                        lockCache,
                        progress: null,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    throw new InvalidOperationException("Concurrent download lock probe unexpectedly succeeded.");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("另一个更新下载", StringComparison.Ordinal))
                {
                }
            }

            var idleCache = Path.Combine(probeRoot, "idle-cache");
            var idleDestination = Path.Combine(probeRoot, "idle.zip");
            using (var idleClient = new HttpClient(new IdleDownloadProbeHandler(payload, prefixLength: 31)))
            {
                try
                {
                    await DownloadAndVerifyAsync(
                        idleClient,
                        update,
                        idleDestination,
                        idleCache,
                        progress: null,
                        CancellationToken.None,
                        TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
                    throw new InvalidOperationException("Idle download probe unexpectedly completed.");
                }
                catch (IOException error) when (error.Message.Contains("空闲超时", StringComparison.Ordinal))
                {
                }
            }
            var idlePartials = Directory.GetFiles(idleCache, "*.partial", SearchOption.TopDirectoryOnly);
            if (idlePartials.Length != 1 || new FileInfo(idlePartials[0]).Length != 31)
            {
                throw new InvalidOperationException("An idle download did not retain exactly one resumable partial.");
            }

            var headerIdleCache = Path.Combine(probeRoot, "header-idle-cache");
            using (var headerIdleClient = new HttpClient(new BlockingSendProbeHandler()))
            {
                try
                {
                    await DownloadAndVerifyAsync(
                        headerIdleClient,
                        update,
                        Path.Combine(probeRoot, "header-idle.zip"),
                        headerIdleCache,
                        progress: null,
                        CancellationToken.None,
                        TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
                    throw new InvalidOperationException("Header idle timeout probe unexpectedly completed.");
                }
                catch (IOException error) when (error.Message.Contains("等待响应超时", StringComparison.Ordinal))
                {
                }
            }
            if (Directory.GetFiles(headerIdleCache, "*.partial", SearchOption.TopDirectoryOnly).Length != 0)
            {
                throw new InvalidOperationException("A response-header timeout created a useless partial package.");
            }

            using (var cancellation = new CancellationTokenSource())
            using (var cancellationClient = new HttpClient(new BlockingSendProbeHandler()))
            {
                cancellation.Cancel();
                try
                {
                    await DownloadAndVerifyAsync(
                        cancellationClient,
                        update,
                        Path.Combine(probeRoot, "cancelled.zip"),
                        Path.Combine(probeRoot, "cancelled-cache"),
                        progress: null,
                        cancellation.Token,
                        TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
                    throw new InvalidOperationException("External cancellation probe unexpectedly completed.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }

            var restartCache = Path.Combine(probeRoot, "restart-cache");
            var restartDestination = Path.Combine(probeRoot, "restart.zip");
            using (var truncatedClient = new HttpClient(
                       new DownloadProbeHandler(payload, supportsRanges: true, truncateFreshAt: 41)))
            {
                try
                {
                    await DownloadAndVerifyAsync(
                        truncatedClient,
                        update,
                        restartDestination,
                        restartCache,
                        progress: null,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (IOException error) when (error.Message.Contains("下载未完成", StringComparison.Ordinal))
                {
                }
            }

            var restartHandler = new DownloadProbeHandler(payload, supportsRanges: false);
            using (var restartClient = new HttpClient(restartHandler))
            {
                await DownloadAndVerifyAsync(
                    restartClient,
                    update,
                    restartDestination,
                    restartCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (restartHandler.RequestedOffsets is not [41, null] ||
                !File.ReadAllBytes(restartDestination).SequenceEqual(payload))
            {
                throw new InvalidOperationException("A server without Range support did not trigger one clean full download.");
            }

            var rangeCompleteCache = Path.Combine(probeRoot, "range-complete-cache");
            var rangeCompleteDestination = Path.Combine(probeRoot, "range-complete.zip");
            Directory.CreateDirectory(rangeCompleteCache);
            File.WriteAllBytes(Path.Combine(rangeCompleteCache, "windows-update.partial"), payload);
            await WriteResumeMetadataAsync(
                Path.Combine(rangeCompleteCache, "windows-update.partial.json"),
                new DownloadResumeMetadata(
                    update.Version,
                    update.AssetName,
                    update.AssetUrl,
                    update.Sha256,
                    TotalLength: null,
                    "\"download-probe\"",
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None).ConfigureAwait(false);
            var rangeCompleteHandler = new DownloadProbeHandler(
                payload,
                supportsRanges: true,
                rangeNotSatisfiableLength: payload.LongLength);
            using (var rangeCompleteClient = new HttpClient(rangeCompleteHandler))
            {
                await DownloadAndVerifyAsync(
                    rangeCompleteClient,
                    update,
                    rangeCompleteDestination,
                    rangeCompleteCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (rangeCompleteHandler.RequestedOffsets is not [257] ||
                !File.ReadAllBytes(rangeCompleteDestination).SequenceEqual(payload))
            {
                throw new InvalidOperationException("A complete partial was not accepted after a matching 416 length and SHA-256.");
            }

            var rangeMismatchCache = Path.Combine(probeRoot, "range-mismatch-cache");
            var rangeMismatchDestination = Path.Combine(probeRoot, "range-mismatch.zip");
            Directory.CreateDirectory(rangeMismatchCache);
            File.WriteAllBytes(Path.Combine(rangeMismatchCache, "windows-update.partial"), payload);
            await WriteResumeMetadataAsync(
                Path.Combine(rangeMismatchCache, "windows-update.partial.json"),
                new DownloadResumeMetadata(
                    update.Version,
                    update.AssetName,
                    update.AssetUrl,
                    update.Sha256,
                    TotalLength: null,
                    "\"download-probe\"",
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None).ConfigureAwait(false);
            var rangeMismatchHandler = new DownloadProbeHandler(
                payload,
                supportsRanges: true,
                rangeNotSatisfiableLength: payload.LongLength + 1);
            using (var rangeMismatchClient = new HttpClient(rangeMismatchHandler))
            {
                await DownloadAndVerifyAsync(
                    rangeMismatchClient,
                    update,
                    rangeMismatchDestination,
                    rangeMismatchCache,
                    progress: null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (rangeMismatchHandler.RequestedOffsets is not [257, null] ||
                !File.ReadAllBytes(rangeMismatchDestination).SequenceEqual(payload))
            {
                throw new InvalidOperationException("A mismatched 416 length did not discard the partial before a full download.");
            }

            var cleanupProbeRoot = Path.Combine(probeRoot, "session-cleanup");
            var cleanupSessionsRoot = Path.Combine(cleanupProbeRoot, "sessions");
            Directory.CreateDirectory(cleanupSessionsRoot);
            var staleOwnedSession = Path.Combine(cleanupSessionsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staleOwnedSession);
            File.WriteAllText(
                Path.Combine(staleOwnedSession, UpdateSessionMarkerFileName),
                UpdateSessionMarkerContent,
                Encoding.ASCII);
            Directory.SetLastWriteTimeUtc(staleOwnedSession, DateTime.UtcNow - TimeSpan.FromDays(2));
            var staleUnownedSession = Path.Combine(cleanupSessionsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staleUnownedSession);
            Directory.SetLastWriteTimeUtc(staleUnownedSession, DateTime.UtcNow - TimeSpan.FromDays(2));
            var staleWrongName = Path.Combine(cleanupSessionsRoot, "not-a-guid");
            Directory.CreateDirectory(staleWrongName);
            File.WriteAllText(
                Path.Combine(staleWrongName, UpdateSessionMarkerFileName),
                UpdateSessionMarkerContent,
                Encoding.ASCII);
            Directory.SetLastWriteTimeUtc(staleWrongName, DateTime.UtcNow - TimeSpan.FromDays(2));
            var recentOwnedSession = Path.Combine(cleanupSessionsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recentOwnedSession);
            File.WriteAllText(
                Path.Combine(recentOwnedSession, UpdateSessionMarkerFileName),
                UpdateSessionMarkerContent,
                Encoding.ASCII);
            CleanupStaleUpdateSessions(cleanupProbeRoot);
            if (Directory.Exists(staleOwnedSession) ||
                !Directory.Exists(staleUnownedSession) ||
                !Directory.Exists(staleWrongName) ||
                !Directory.Exists(recentOwnedSession))
            {
                throw new InvalidOperationException("Stale update-session cleanup ignored its GUID, age, or ownership boundary.");
            }

            var corruptCache = Path.Combine(probeRoot, "corrupt-cache");
            var corruptDestination = Path.Combine(probeRoot, "corrupt.zip");
            var corruptPayload = payload.ToArray();
            corruptPayload[^1] ^= 0x5a;
            using (var corruptClient = new HttpClient(new DownloadProbeHandler(corruptPayload, supportsRanges: true)))
            {
                try
                {
                    await DownloadAndVerifyAsync(
                        corruptClient,
                        update,
                        corruptDestination,
                        corruptCache,
                        progress: null,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    throw new InvalidOperationException("Corrupt download probe unexpectedly passed SHA-256 verification.");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("SHA256", StringComparison.Ordinal))
                {
                }
            }
            if (File.Exists(corruptDestination) ||
                Directory.GetFiles(corruptCache, "*.partial", SearchOption.TopDirectoryOnly).Length != 0)
            {
                throw new InvalidOperationException("A corrupt completed update was retained after SHA-256 rejection.");
            }
        }
        finally
        {
            try { Directory.Delete(probeRoot, recursive: true); } catch { }
        }
    }

    private static async Task CreateTruncatedDownloadProbeAsync(
        AppUpdateInfo update,
        byte[] payload,
        string cacheRoot,
        string destination,
        int retainedLength)
    {
        using var client = new HttpClient(
            new DownloadProbeHandler(payload, supportsRanges: true, truncateFreshAt: retainedLength));
        try
        {
            await DownloadAndVerifyAsync(
                client,
                update,
                destination,
                cacheRoot,
                progress: null,
                CancellationToken.None,
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            throw new InvalidOperationException("Truncated download setup unexpectedly completed.");
        }
        catch (IOException error) when (error.Message.Contains("下载未完成", StringComparison.Ordinal))
        {
        }
    }

    private static string GetVersionedInstallPath(string version)
    {
        var normalized = NormalizeVersion(version)
            ?? throw new InvalidOperationException("The update version is invalid.");
        return Path.Combine(GetVersionedInstallRoot(), normalized);
    }

    private static string GetVersionedInstallRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "CodexAccountManager",
        "versions");

    private static string GetUpdateStagingRoot() => Path.Combine(
        Path.GetTempPath(),
        "CAM-update");

    private static string GetUpdateDownloadCacheRoot() => Path.Combine(
        GetUpdateStagingRoot(),
        "download-cache");

    private static void CleanupStaleUpdateSessions(string stagingRoot)
    {
        try
        {
            var sessionsRoot = Path.Combine(stagingRoot, "sessions");
            if (!Directory.Exists(sessionsRoot))
            {
                return;
            }
            var sessionsDirectory = new DirectoryInfo(sessionsRoot);
            if ((sessionsDirectory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !IsSamePathOrDescendant(sessionsDirectory.FullName, stagingRoot))
            {
                return;
            }
            var cutoff = DateTime.UtcNow - StaleUpdateSessionAge;
            foreach (var directory in sessionsDirectory.EnumerateDirectories())
            {
                TryDeleteStaleSession(directory, cutoff, sessionsRoot);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteStaleSession(
        DirectoryInfo directory,
        DateTime cutoff,
        string sessionsRoot)
    {
        try
        {
            if (!Guid.TryParseExact(directory.Name, "N", out _) ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !IsSamePathOrDescendant(directory.FullName, sessionsRoot) ||
                directory.LastWriteTimeUtc > cutoff)
            {
                return;
            }

            var markerPath = Path.Combine(directory.FullName, UpdateSessionMarkerFileName);
            var marker = new FileInfo(markerPath);
            if (!marker.Exists ||
                (marker.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !File.ReadAllText(markerPath).Equals(UpdateSessionMarkerContent, StringComparison.Ordinal) ||
                SessionTreeContainsReparsePoint(directory))
            {
                return;
            }

            Directory.Delete(directory.FullName, recursive: true);
        }
        catch
        {
        }
    }

    private static bool SessionTreeContainsReparsePoint(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
            }
        }
        return false;
    }

    private static void EnsurePathIsOutsideUpdateStaging(string path, string description)
    {
        if (IsSamePathOrDescendant(path, GetUpdateStagingRoot()))
        {
            throw new InvalidOperationException(
                $"{description} is inside the temporary CAM-update directory. " +
                "Reinstall the application to a permanent directory before updating.");
        }
    }

    private static bool IsSamePathOrDescendant(string candidate, string root)
    {
        var candidatePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (candidatePath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidatePath.StartsWith(
            rootPath + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(CreateConfiguredProxyHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodexAccountManager", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static HttpClient CreateDownloadHttpClient()
    {
        var client = new HttpClient(CreateConfiguredProxyHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodexAccountManager", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }

    private static HttpClientHandler CreateConfiguredProxyHandler() =>
        CreateConfiguredProxyHandler(CodexCliService.GetConfiguredProxyUri());

    private static HttpClientHandler CreateConfiguredProxyHandler(string? configuredProxy)
    {
        var handler = new HttpClientHandler();
        if (Uri.TryCreate(configuredProxy, UriKind.Absolute, out var proxyUri))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxyUri);
        }
        return handler;
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken,
        AppUpdateCheckStatus networkFailureStatus,
        AppUpdateCheckStatus httpFailureStatus,
        AppUpdateCheckStatus invalidJsonStatus)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failureStatus = response.StatusCode == HttpStatusCode.NotFound
                    ? httpFailureStatus
                    : networkFailureStatus;
                throw new UpdateCheckException(failureStatus, response.StatusCode == HttpStatusCode.NotFound
                    ? "GitHub 未找到请求的 Release 资源。"
                    : $"GitHub 返回 HTTP {(int)response.StatusCode}。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException error)
            {
                throw new UpdateCheckException(invalidJsonStatus, "GitHub 返回的 JSON 格式无效。", error);
            }
        }
        catch (UpdateCheckException)
        {
            throw;
        }
        catch (HttpRequestException error)
        {
            throw new UpdateCheckException(networkFailureStatus, "无法连接 GitHub。", error);
        }
        catch (TaskCanceledException error)
        {
            throw new UpdateCheckException(networkFailureStatus, "连接 GitHub 超时。", error);
        }
    }

    private static async Task DownloadAndVerifyAsync(
        HttpClient client,
        AppUpdateInfo update,
        string destination,
        string cacheRoot,
        IProgress<AppUpdateProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan idleTimeout)
    {
        Directory.CreateDirectory(cacheRoot);
        var partialPath = Path.Combine(cacheRoot, "windows-update.partial");
        var partialMetadataPath = Path.Combine(cacheRoot, "windows-update.partial.json");
        var verifiedPath = Path.Combine(cacheRoot, "windows-update.verified.zip");
        var verifiedMetadataPath = Path.Combine(cacheRoot, "windows-update.verified.json");
        var lockPath = Path.Combine(cacheRoot, "windows-update.lock");
        using var downloadLock = AcquireDownloadLock(lockPath);

        var verifiedMetadata = ReadResumeMetadata(verifiedMetadataPath);
        if (File.Exists(verifiedPath) &&
            verifiedMetadata is not null &&
            ResumeMetadataMatches(verifiedMetadata, update))
        {
            progress?.Report(new AppUpdateProgress("发现已校验的更新包，正在确认完整性……"));
            if (await FileMatchesSha256Async(verifiedPath, update.Sha256, cancellationToken).ConfigureAwait(false))
            {
                DeleteCachedDownload(partialPath, partialMetadataPath);
                await CopyVerifiedDownloadToSessionAsync(
                    verifiedPath,
                    destination,
                    update.Sha256,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else if (File.Exists(verifiedPath) && verifiedMetadata is null)
        {
            // Promotion intentionally moves the package before publishing its metadata.
            // A crash in that narrow window leaves an orphan verified ZIP.  Its signed
            // release SHA is sufficient to bind it to the current asset identity, so
            // reconstruct the metadata instead of downloading the same package again.
            progress?.Report(new AppUpdateProgress("发现待恢复的完整更新包，正在校验……"));
            if (await FileMatchesSha256Async(verifiedPath, update.Sha256, cancellationToken).ConfigureAwait(false))
            {
                verifiedMetadata = new DownloadResumeMetadata(
                    update.Version,
                    update.AssetName,
                    update.AssetUrl,
                    update.Sha256,
                    new FileInfo(verifiedPath).Length,
                    ETag: null,
                    LastModifiedUtc: null);
                await WriteResumeMetadataAsync(
                    verifiedMetadataPath,
                    verifiedMetadata,
                    cancellationToken).ConfigureAwait(false);
                DeleteCachedDownload(partialPath, partialMetadataPath);
                await CopyVerifiedDownloadToSessionAsync(
                    verifiedPath,
                    destination,
                    update.Sha256,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        DeleteCachedDownload(verifiedPath, verifiedMetadataPath);

        var metadata = ReadResumeMetadata(partialMetadataPath);
        if (!File.Exists(partialPath) ||
            metadata is null ||
            !ResumeMetadataMatches(metadata, update))
        {
            DeleteCachedDownload(partialPath, partialMetadataPath);
            metadata = null;
        }

        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0L;
        if (existingLength <= 0 ||
            existingLength > MaximumDownloadBytes ||
            (metadata?.TotalLength is > 0 && existingLength > metadata.TotalLength.Value))
        {
            DeleteCachedDownload(partialPath, partialMetadataPath);
            metadata = null;
            existingLength = 0;
        }

        if (existingLength > 0 && metadata?.TotalLength == existingLength)
        {
            progress?.Report(new AppUpdateProgress("发现已完整下载的更新包，正在重新校验……"));
            if (await FileMatchesSha256Async(partialPath, update.Sha256, cancellationToken).ConfigureAwait(false))
            {
                await PublishAndCopyVerifiedDownloadAsync(
                    partialPath,
                    partialMetadataPath,
                    verifiedPath,
                    verifiedMetadataPath,
                    destination,
                    metadata,
                    update.Sha256,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            DeleteCachedDownload(partialPath, partialMetadataPath);
            metadata = null;
            existingLength = 0;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var resume = existingLength > 0 && metadata is not null;
            using var request = new HttpRequestMessage(HttpMethod.Get, update.AssetUrl);
            if (resume)
            {
                request.Headers.Range = new RangeHeaderValue(existingLength, null);
                if (!string.IsNullOrWhiteSpace(metadata!.ETag) &&
                    EntityTagHeaderValue.TryParse(metadata.ETag, out var entityTag) &&
                    !entityTag.IsWeak)
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
                }
                else if (metadata.LastModifiedUtc is not null)
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(metadata.LastModifiedUtc.Value);
                }
                progress?.Report(new AppUpdateProgress(
                    $"发现上次未完成的下载，正从 {existingLength / 1024d / 1024d:0.0} MB 继续……"));
            }

            using var response = await SendWithIdleTimeoutAsync(
                client,
                request,
                cancellationToken,
                idleTimeout).ConfigureAwait(false);

            if (resume && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                var declaredLength = response.Content.Headers.ContentRange?.Length ?? metadata!.TotalLength;
                if (declaredLength == existingLength &&
                    await FileMatchesSha256Async(partialPath, update.Sha256, cancellationToken).ConfigureAwait(false))
                {
                    await PublishAndCopyVerifiedDownloadAsync(
                        partialPath,
                        partialMetadataPath,
                        verifiedPath,
                        verifiedMetadataPath,
                        destination,
                        metadata!,
                        update.Sha256,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                DeleteCachedDownload(partialPath, partialMetadataPath);
                metadata = null;
                existingLength = 0;
                continue;
            }

            if (resume && response.StatusCode != HttpStatusCode.PartialContent)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    DeleteCachedDownload(partialPath, partialMetadataPath);
                    metadata = null;
                    existingLength = 0;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                throw new InvalidOperationException("更新服务器未返回有效的断点续传响应。");
            }

            response.EnsureSuccessStatusCode();
            if (!resume && response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException("更新服务器未返回完整的下载响应。");
            }

            var responseTotalLength = resume
                ? response.Content.Headers.ContentRange?.Length
                : response.Content.Headers.ContentLength;
            var responseLength = response.Content.Headers.ContentLength;
            if (resume)
            {
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.From != existingLength ||
                    contentRange.To is null ||
                    contentRange.To.Value < existingLength ||
                    (responseTotalLength is > 0 && contentRange.To.Value >= responseTotalLength.Value) ||
                    (responseLength is > 0 &&
                     contentRange.To.Value - existingLength + 1 != responseLength.Value) ||
                    (metadata!.TotalLength is > 0 &&
                     responseTotalLength is > 0 &&
                     metadata.TotalLength.Value != responseTotalLength.Value) ||
                    ResponseValidatorChanged(metadata, response))
                {
                    DeleteCachedDownload(partialPath, partialMetadataPath);
                    metadata = null;
                    existingLength = 0;
                    continue;
                }
            }

            var totalLength = responseTotalLength ?? metadata?.TotalLength;
            if (totalLength is > MaximumDownloadBytes ||
                (responseLength is > 0 && responseLength.Value > MaximumDownloadBytes - existingLength))
            {
                DeleteCachedDownload(partialPath, partialMetadataPath);
                throw new InvalidOperationException("更新包体积超过安全限制。");
            }

            metadata = new DownloadResumeMetadata(
                update.Version,
                update.AssetName,
                update.AssetUrl,
                update.Sha256,
                totalLength,
                response.Headers.ETag?.ToString() ?? metadata?.ETag,
                response.Content.Headers.LastModified ?? metadata?.LastModifiedUtc);
            await WriteResumeMetadataAsync(partialMetadataPath, metadata, cancellationToken).ConfigureAwait(false);

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(
                partialPath,
                resume ? FileMode.Append : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 128,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 128];
                var totalReceived = existingLength;
                var lastReportedPercent = -1;
                var lastReportedBytes = existingLength;
                var exceededMaximum = false;
                while (true)
                {
                    var read = await ReadWithIdleTimeoutAsync(
                        source,
                        buffer,
                        cancellationToken,
                        idleTimeout).ConfigureAwait(false);
                    if (read == 0) break;
                    totalReceived += read;
                    if (totalReceived > MaximumDownloadBytes)
                    {
                        exceededMaximum = true;
                        break;
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                    var percent = totalLength is > 0
                        ? (int)Math.Min(100, totalReceived * 100 / totalLength.Value)
                        : -1;
                    if (percent != lastReportedPercent || totalReceived - lastReportedBytes >= 4L * 1024L * 1024L)
                    {
                        lastReportedPercent = percent;
                        lastReportedBytes = totalReceived;
                        progress?.Report(new AppUpdateProgress(
                            FormatDownloadProgress(totalReceived, totalLength, percent)));
                    }
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (exceededMaximum)
                {
                    target.Close();
                    DeleteCachedDownload(partialPath, partialMetadataPath);
                    throw new InvalidOperationException("更新包体积超过安全限制。");
                }
            }

            var completedLength = new FileInfo(partialPath).Length;
            if (totalLength is > 0 && completedLength != totalLength.Value)
            {
                if (completedLength > totalLength.Value)
                {
                    DeleteCachedDownload(partialPath, partialMetadataPath);
                }
                throw new IOException("更新包下载未完成，已保留进度供下次继续。");
            }

            progress?.Report(new AppUpdateProgress("下载完成，正在校验更新包 SHA-256……"));
            if (!await FileMatchesSha256Async(partialPath, update.Sha256, cancellationToken).ConfigureAwait(false))
            {
                DeleteCachedDownload(partialPath, partialMetadataPath);
                throw new InvalidOperationException("更新包 SHA256 校验失败，已删除无效文件并拒绝安装。");
            }

            await PublishAndCopyVerifiedDownloadAsync(
                partialPath,
                partialMetadataPath,
                verifiedPath,
                verifiedMetadataPath,
                destination,
                metadata,
                update.Sha256,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("更新服务器不支持可靠的断点续传，请重新尝试下载。");
    }

    private static async Task<HttpResponseMessage> SendWithIdleTimeoutAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan idleTimeout)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idleTimeout);
        try
        {
            return await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("连接更新服务器等待响应超时，请重试。", error);
        }
    }

    private static async Task<int> ReadWithIdleTimeoutAsync(
        Stream source,
        byte[] buffer,
        CancellationToken cancellationToken,
        TimeSpan idleTimeout)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idleTimeout);
        try
        {
            return await source.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("更新包下载连接空闲超时，已保留进度供下次继续。", error);
        }
    }

    private static FileStream AcquireDownloadLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("另一个更新下载正在进行，请稍后重试。", error);
        }
    }

    private static DownloadResumeMetadata? ReadResumeMetadata(string metadataPath)
    {
        try
        {
            return File.Exists(metadataPath)
                ? JsonSerializer.Deserialize<DownloadResumeMetadata>(File.ReadAllText(metadataPath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ResumeMetadataMatches(DownloadResumeMetadata metadata, AppUpdateInfo update) =>
        string.Equals(metadata.Version, update.Version, StringComparison.Ordinal) &&
        string.Equals(metadata.AssetName, update.AssetName, StringComparison.Ordinal) &&
        string.Equals(metadata.AssetUrl, update.AssetUrl, StringComparison.Ordinal) &&
        string.Equals(metadata.Sha256, update.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool ResponseValidatorChanged(
        DownloadResumeMetadata metadata,
        HttpResponseMessage response)
    {
        var responseETag = response.Headers.ETag?.ToString();
        if (!string.IsNullOrWhiteSpace(metadata.ETag) &&
            !string.IsNullOrWhiteSpace(responseETag) &&
            !string.Equals(metadata.ETag, responseETag, StringComparison.Ordinal))
        {
            return true;
        }

        var responseLastModified = response.Content.Headers.LastModified;
        return metadata.LastModifiedUtc is not null &&
               responseLastModified is not null &&
               metadata.LastModifiedUtc.Value != responseLastModified.Value;
    }

    private static async Task WriteResumeMetadataAsync(
        string metadataPath,
        DownloadResumeMetadata metadata,
        CancellationToken cancellationToken)
    {
        var temporaryPath = metadataPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(metadata),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static async Task<bool> FileMatchesSha256Async(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task PublishAndCopyVerifiedDownloadAsync(
        string partialPath,
        string partialMetadataPath,
        string verifiedPath,
        string verifiedMetadataPath,
        string destination,
        DownloadResumeMetadata metadata,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        DeleteCachedDownload(verifiedPath, verifiedMetadataPath);
        File.Move(partialPath, verifiedPath, overwrite: false);
        await WriteResumeMetadataAsync(
            verifiedMetadataPath,
            metadata,
            cancellationToken).ConfigureAwait(false);
        DeleteCachedDownload(partialPath, partialMetadataPath);
        await CopyVerifiedDownloadToSessionAsync(
            verifiedPath,
            destination,
            expectedSha256,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyVerifiedDownloadToSessionAsync(
        string verifiedPath,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("更新包目标目录无效。"));
        var temporaryDestination = destination + ".copying";
        File.Delete(temporaryDestination);
        try
        {
            File.Copy(verifiedPath, temporaryDestination, overwrite: false);
            if (!await FileMatchesSha256Async(
                    temporaryDestination,
                    expectedSha256,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("已校验更新包复制失败，已拒绝安装。");
            }
            File.Move(temporaryDestination, destination, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporaryDestination); } catch { }
        }
    }

    private static void DeleteCachedDownload(string packagePath, string metadataPath)
    {
        File.Delete(packagePath);
        File.Delete(metadataPath);
        File.Delete(metadataPath + ".tmp");
        if (File.Exists(packagePath) || File.Exists(metadataPath) || File.Exists(metadataPath + ".tmp"))
        {
            throw new IOException("无法安全替换旧的更新下载缓存，请关闭占用该文件的程序后重试。");
        }
    }

    private static string FormatDownloadProgress(long received, long? total, int percent)
    {
        var receivedMb = received / 1024d / 1024d;
        if (total is > 0)
        {
            var totalMb = total.Value / 1024d / 1024d;
            return $"正在下载更新包：{receivedMb:0.0} / {totalMb:0.0} MB（{Math.Max(0, percent)}%）";
        }
        return $"正在下载更新包：已接收 {receivedMb:0.0} MB";
    }

    private static void ExtractZipSafely(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var destinationPrefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var candidate = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!candidate.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("更新包包含不安全的文件路径。");
            }
        }
        ZipFile.ExtractToDirectory(zipPath, destination);
    }

    private static string BuildHelperScript() => @"
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$CleanupRoot,
    [Parameter(Mandatory = $true)][string]$InstallPath,
    [Parameter(Mandatory = $true)][string]$WorkingDirectory,
    [Parameter(Mandatory = $true)][string]$ManagerRoot,
    [Parameter(Mandatory = $true)][string]$CurrentExecutablePath,
    [Parameter(Mandatory = $true)][string]$UpdaterLogPath,
    [Parameter(Mandatory = $true)][string]$InstallerLogPath,
    [Parameter(Mandatory = $true)][string]$FailureMarkerPath,
    [ValidateRange(1, 300)][int]$PreviousProcessExitTimeoutSeconds = 45
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exitCode = 0
$managerRoot = [IO.Path]::GetFullPath($ManagerRoot)
$env:CODEX_ACCOUNT_MANAGER_HOME = $managerRoot
$pathsValidated = $false
$previousProcessExited = $false

function Test-PathInside {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root
    )
    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    $rootFull = [IO.Path]::GetFullPath($Root)
    if ([string]::Equals($candidateFull, $rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    $separator = [string][IO.Path]::DirectorySeparatorChar
    if (-not $rootFull.EndsWith($separator, [StringComparison]::Ordinal)) {
        $rootFull += $separator
    }
    return $candidateFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)
}

function Write-UpdaterLog {
    param([Parameter(Mandatory = $true)][string]$Message)
    $line = '[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $Message
    Add-Content -LiteralPath $UpdaterLogPath -Value $line -Encoding UTF8
}

function Wait-ProcessExit {
    param(
        [Parameter(Mandatory = $true)][int]$TargetProcessId,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($null -ne (Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            return $false
        }
        Start-Sleep -Milliseconds 250
    }
    return $true
}

function Get-ExactNativeFastBridgeProcess {
    param(
        [Parameter(Mandatory = $true)][int]$TargetProcessId,
        [Parameter(Mandatory = $true)][string]$ExpectedExecutablePath,
        [Parameter(Mandatory = $true)]$ExpectedCreationDate,
        [Parameter(Mandatory = $true)][string]$NativeFastArgument,
        [Parameter(Mandatory = $true)][string]$GatewayArgument
    )
    $candidate = Get-CimInstance Win32_Process `
        -Filter ('ProcessId = {0}' -f $TargetProcessId) `
        -ErrorAction SilentlyContinue
    if ($null -eq $candidate -or
        -not $candidate.ExecutablePath -or
        -not $candidate.CommandLine -or
        $candidate.CreationDate -ne $ExpectedCreationDate -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($candidate.ExecutablePath),
            $ExpectedExecutablePath,
            [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.CommandLine.IndexOf($NativeFastArgument, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $candidate.CommandLine.IndexOf($GatewayArgument, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $null
    }
    return $candidate
}

function Wait-ExactNativeFastBridgeExit {
    param(
        [Parameter(Mandatory = $true)][int]$TargetProcessId,
        [Parameter(Mandatory = $true)][string]$ExpectedExecutablePath,
        [Parameter(Mandatory = $true)]$ExpectedCreationDate,
        [Parameter(Mandatory = $true)][string]$NativeFastArgument,
        [Parameter(Mandatory = $true)][string]$GatewayArgument,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($null -ne (Get-ExactNativeFastBridgeProcess `
        -TargetProcessId $TargetProcessId `
        -ExpectedExecutablePath $ExpectedExecutablePath `
        -ExpectedCreationDate $ExpectedCreationDate `
        -NativeFastArgument $NativeFastArgument `
        -GatewayArgument $GatewayArgument)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            return $false
        }
        Start-Sleep -Milliseconds 250
    }
    return $true
}

try {
    $logRoot = Split-Path -Parent $UpdaterLogPath
    New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
    Write-UpdaterLog 'Updater started.'

    $cleanupFull = [IO.Path]::GetFullPath($CleanupRoot)
    $installFull = [IO.Path]::GetFullPath($InstallPath)
    $currentExecutableFull = [IO.Path]::GetFullPath($CurrentExecutablePath)
    $currentDirectory = Split-Path -Parent $currentExecutableFull
    $temporaryUpdateRoot = Join-Path ([IO.Path]::GetTempPath()) 'CAM-update'
    if (Test-PathInside -Candidate $installFull -Root $temporaryUpdateRoot) {
        throw 'The install target is inside the temporary CAM-update directory.'
    }
    if (Test-PathInside -Candidate $installFull -Root $cleanupFull) {
        throw 'The install target is inside the update cleanup directory.'
    }
    if (Test-PathInside -Candidate $currentDirectory -Root $temporaryUpdateRoot) {
        throw 'The running application is inside the temporary CAM-update directory.'
    }
    $pathsValidated = $true

    if (-not (Wait-ProcessExit -TargetProcessId $ProcessId -TimeoutSeconds $PreviousProcessExitTimeoutSeconds)) {
        throw ('Codex Account Manager did not exit within {0} seconds.' -f $PreviousProcessExitTimeoutSeconds)
    }
    $previousProcessExited = $true

    Write-UpdaterLog 'Previous process exited. Starting package installer.'
    $childPowerShell = Join-Path $PSHOME 'powershell.exe'
    & $childPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $InstallerPath `
        -Quiet -NoLaunch -InstallPath $installFull `
        -ManagerWorkingDirectory $managerRoot -LogPath $InstallerLogPath
    $installerExitCode = $LASTEXITCODE
    if ($installerExitCode -ne 0) {
        throw ('Package installer exited with code {0}.' -f $installerExitCode)
    }

    $installedExe = Join-Path $installFull 'CodexAccountManager.exe'
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw 'The updated executable was not found after installation.'
    }

    $oldProcessPath = [IO.Path]::GetFullPath($CurrentExecutablePath)
    $nativeFastArgument = '--codex-native-fast-bridge'
    $gatewayArgument = '--local-pat-gateway'
    $bridges = @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ExecutablePath -and
                $_.CommandLine -and
                [string]::Equals(
                    [IO.Path]::GetFullPath($_.ExecutablePath),
                    $oldProcessPath,
                    [StringComparison]::OrdinalIgnoreCase) -and
                $_.CommandLine.IndexOf($nativeFastArgument, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $_.CommandLine.IndexOf($gatewayArgument, [StringComparison]::OrdinalIgnoreCase) -lt 0
            }
    )
    foreach ($bridge in $bridges) {
        $currentBridge = Get-ExactNativeFastBridgeProcess `
            -TargetProcessId $bridge.ProcessId `
            -ExpectedExecutablePath $oldProcessPath `
            -ExpectedCreationDate $bridge.CreationDate `
            -NativeFastArgument $nativeFastArgument `
            -GatewayArgument $gatewayArgument
        if ($null -ne $currentBridge) {
            Write-UpdaterLog ('Stopping native Fast bridge {0} for version handoff.' -f $bridge.ProcessId)
            try {
                Stop-Process -Id $bridge.ProcessId -Force -ErrorAction Stop
            }
            catch {
                if ($null -ne (Get-ExactNativeFastBridgeProcess `
                    -TargetProcessId $bridge.ProcessId `
                    -ExpectedExecutablePath $oldProcessPath `
                    -ExpectedCreationDate $bridge.CreationDate `
                    -NativeFastArgument $nativeFastArgument `
                    -GatewayArgument $gatewayArgument)) {
                    throw
                }
            }
        }
    }
    foreach ($bridge in $bridges) {
        if (-not (Wait-ExactNativeFastBridgeExit `
            -TargetProcessId $bridge.ProcessId `
            -ExpectedExecutablePath $oldProcessPath `
            -ExpectedCreationDate $bridge.CreationDate `
            -NativeFastArgument $nativeFastArgument `
            -GatewayArgument $gatewayArgument `
            -TimeoutSeconds 15)) {
            throw ('Native Fast bridge {0} did not exit within 15 seconds.' -f $bridge.ProcessId)
        }
    }

    Remove-Item -LiteralPath $FailureMarkerPath -Force -ErrorAction SilentlyContinue
    Write-UpdaterLog 'Installation completed. Restarting the updated application.'
    Start-Process -FilePath $installedExe -WorkingDirectory $WorkingDirectory `
        -ArgumentList @('--preserve-existing-pat-gateway', '--refresh-native-fast-bridge-after-update') | Out-Null
    Start-Sleep -Seconds 2
}
catch {
    $exitCode = 1
    $failure = $_.Exception.Message
    try { Set-Content -LiteralPath $FailureMarkerPath -Value $failure -Encoding UTF8 } catch { }
    try { Write-UpdaterLog ('Update failed: ' + $failure) } catch { }
    if ($pathsValidated -and $previousProcessExited -and (Test-Path -LiteralPath $CurrentExecutablePath -PathType Leaf)) {
        try {
            Start-Process -FilePath $CurrentExecutablePath -WorkingDirectory $WorkingDirectory `
                -ArgumentList @('--preserve-existing-pat-gateway', '--refresh-native-fast-bridge-after-update') | Out-Null
            Write-UpdaterLog 'Restarted the previous application after the update failure.'
        }
        catch {
            try { Write-UpdaterLog ('Could not restart the previous application: ' + $_.Exception.Message) } catch { }
        }
    }
}
finally {
    try { Remove-Item -LiteralPath $CleanupRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
exit $exitCode
";

    private static IReadOnlyList<ReleaseAsset> ReadAssets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ReleaseAsset>();
        }

        return assets.EnumerateArray()
            .Select(asset => new ReleaseAsset(
                ReadString(asset, "name") ?? string.Empty,
                ReadString(asset, "browser_download_url") ?? string.Empty,
                ReadString(asset, "digest")))
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Name) && !string.IsNullOrWhiteSpace(asset.Url))
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? NormalizeSha256(string? value)
    {
        var normalized = value?.Trim();
        return normalized is not null && normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : null;
    }

    private static string? NormalizeVersion(string? value)
    {
        var normalized = value?.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var parsed) ? parsed.ToString() : null;
    }

    private static bool IsNewer(string remote, string current)
    {
        return Version.TryParse(remote, out var remoteVersion) &&
               Version.TryParse(current, out var currentVersion) &&
               remoteVersion > currentVersion;
    }

    private sealed record ReleaseAsset(string Name, string Url, string? Digest);

    private sealed record DownloadResumeMetadata(
        string Version,
        string AssetName,
        string AssetUrl,
        string Sha256,
        long? TotalLength,
        string? ETag,
        DateTimeOffset? LastModifiedUtc);

    private sealed class DownloadProbeHandler(
        byte[] payload,
        bool supportsRanges,
        int? truncateFreshAt = null,
        long? rangeNotSatisfiableLength = null,
        long rangeStartAdjustment = 0,
        string? responseETag = "\"download-probe\"",
        DateTimeOffset? responseLastModified = null) : HttpMessageHandler
    {
        internal List<long?> RequestedOffsets { get; } = [];
        internal List<string?> RequestedIfRanges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestedOffset = request.Headers.Range?.Ranges.SingleOrDefault()?.From;
            RequestedOffsets.Add(requestedOffset);
            RequestedIfRanges.Add(request.Headers.IfRange?.ToString());
            if (requestedOffset is not null && rangeNotSatisfiableLength is not null)
            {
                var rangeFailure = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Content = new ByteArrayContent([])
                };
                rangeFailure.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    rangeNotSatisfiableLength.Value);
                return Task.FromResult(rangeFailure);
            }
            var useRange = supportsRanges && requestedOffset is >= 0 && requestedOffset.Value < payload.LongLength;
            byte[] responseBytes;
            HttpResponseMessage response;
            if (useRange)
            {
                var responseOffset = requestedOffset!.Value + rangeStartAdjustment;
                responseBytes = payload[(int)responseOffset..];
                response = new HttpResponseMessage(HttpStatusCode.PartialContent);
                response.Content = new ByteArrayContent(responseBytes);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    responseOffset,
                    payload.LongLength - 1,
                    payload.LongLength);
            }
            else
            {
                var length = truncateFreshAt is > 0
                    ? Math.Min(truncateFreshAt.Value, payload.Length)
                    : payload.Length;
                responseBytes = payload[..length];
                response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Content = new ByteArrayContent(responseBytes);
                response.Content.Headers.ContentLength = payload.LongLength;
            }

            if (!string.IsNullOrWhiteSpace(responseETag))
            {
                response.Headers.ETag = new EntityTagHeaderValue(responseETag);
            }
            response.Content.Headers.LastModified = responseLastModified ?? DateTimeOffset.UnixEpoch;
            return Task.FromResult(response);
        }
    }

    private sealed class IdleDownloadProbeHandler(byte[] payload, int prefixLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PrefixThenStallStream(payload[..prefixLength]))
            };
            response.Content.Headers.ContentLength = payload.LongLength;
            response.Headers.ETag = new EntityTagHeaderValue("\"download-probe\"");
            response.Content.Headers.LastModified = DateTimeOffset.UnixEpoch;
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingSendProbeHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class PrefixThenStallStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return ValueTask.FromResult(count);
            }
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UpdateCheckException(
        AppUpdateCheckStatus status,
        string message,
        Exception? innerException = null) : Exception(message, innerException)
    {
        internal AppUpdateCheckStatus Status { get; } = status;
    }
}
