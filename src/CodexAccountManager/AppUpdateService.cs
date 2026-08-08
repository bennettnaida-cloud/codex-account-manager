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
    private const string Repository = "bennettnaida-cloud/codex-account-manager";
    private const string ReleaseApiUrl = "https://api.github.com/repos/" + Repository + "/releases/tags/latest";
    private const long MaximumDownloadBytes = 800L * 1024L * 1024L;
    private static readonly HttpClient Http = CreateHttpClient();

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
            using var releaseDocument = await GetJsonAsync(
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
        var updateRoot = Path.Combine(
            Path.GetTempPath(),
            "CAM-update",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateRoot);
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
            await DownloadAndVerifyAsync(
                    update.AssetUrl,
                    zipPath,
                    update.Sha256,
                    progress,
                    cancellationToken)
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
            startInfo.ArgumentList.Add(GetCurrentInstallPath());
            startInfo.ArgumentList.Add("-WorkingDirectory");
            startInfo.ArgumentList.Add(managerRoot);
            startInfo.ArgumentList.Add("-ManagerRoot");
            startInfo.ArgumentList.Add(managerRoot);
            startInfo.ArgumentList.Add("-CurrentExecutablePath");
            startInfo.ArgumentList.Add(Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "CodexAccountManager.exe"));
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
        if (!script.Contains("--shutdown-local-pat-gateway", StringComparison.Ordinal)) failures.Add("gateway-shutdown");
        if (!script.Contains("Start-Process -FilePath $installedExe", StringComparison.Ordinal)) failures.Add("restart");
        if (!script.Contains("$env:CODEX_ACCOUNT_MANAGER_HOME = $managerRoot", StringComparison.Ordinal)) failures.Add("manager-root-environment");
        if (!script.Contains("Copy-Item -LiteralPath $rollbackExecutablePath", StringComparison.Ordinal)) failures.Add("rollback-restore");
        if (!GetCurrentInstallPath().Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("install-path");
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
            var executionInfo = new ProcessStartInfo
            {
                FileName = startInfo.FileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            executionInfo.ArgumentList.Add("-NoLogo");
            executionInfo.ArgumentList.Add("-NoProfile");
            executionInfo.ArgumentList.Add("-NonInteractive");
            executionInfo.ArgumentList.Add("-ExecutionPolicy");
            executionInfo.ArgumentList.Add("Bypass");
            executionInfo.ArgumentList.Add("-File");
            executionInfo.ArgumentList.Add(probePath);
            executionInfo.ArgumentList.Add("-ProcessId");
            executionInfo.ArgumentList.Add(int.MaxValue.ToString());
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

            using var execution = Process.Start(executionInfo)
                ?? throw new InvalidOperationException("Updater execution probe could not start PowerShell.");
            if (!execution.WaitForExit(30_000))
            {
                try { execution.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("Updater execution probe timed out.");
            }
            var executionOutput = execution.StandardOutput.ReadToEnd();
            var executionError = execution.StandardError.ReadToEnd();
            var installerMarker = Path.Combine(installPath, "installer-ran.txt");
            var expectedManagerRoot = Path.GetFullPath(probeRoot);
            var inheritedManagerRoot = File.Exists(Path.Combine(installPath, "manager-root.txt"))
                ? File.ReadAllText(Path.Combine(installPath, "manager-root.txt")).Trim()
                : string.Empty;
            var installerManagerRoot = File.Exists(Path.Combine(installPath, "manager-working-directory.txt"))
                ? File.ReadAllText(Path.Combine(installPath, "manager-working-directory.txt")).Trim()
                : string.Empty;
            if (execution.ExitCode != 0 ||
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
        }
        finally
        {
            try { Directory.Delete(probeRoot, recursive: true); } catch { }
        }
    }

    private static string GetCurrentInstallPath() =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodexAccountManager", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken cancellationToken,
        AppUpdateCheckStatus networkFailureStatus,
        AppUpdateCheckStatus httpFailureStatus,
        AppUpdateCheckStatus invalidJsonStatus)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
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
        string url,
        string destination,
        string expectedSha256,
        IProgress<AppUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
        {
            throw new InvalidOperationException("更新包体积超过安全限制。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 128];
        long total = 0;
        var contentLength = response.Content.Headers.ContentLength;
        var lastReportedPercent = -1;
        long lastReportedBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumDownloadBytes)
            {
                throw new InvalidOperationException("更新包体积超过安全限制。");
            }
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);

            var percent = contentLength is > 0
                ? (int)Math.Min(100, total * 100 / contentLength.Value)
                : -1;
            if (percent != lastReportedPercent || total - lastReportedBytes >= 4L * 1024L * 1024L)
            {
                lastReportedPercent = percent;
                lastReportedBytes = total;
                progress?.Report(new AppUpdateProgress(FormatDownloadProgress(total, contentLength, percent)));
            }
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新包 SHA256 校验失败，已拒绝安装。");
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
    [Parameter(Mandatory = $true)][string]$FailureMarkerPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exitCode = 0
$managerRoot = [IO.Path]::GetFullPath($ManagerRoot)
$env:CODEX_ACCOUNT_MANAGER_HOME = $managerRoot
$rollbackExecutablePath = Join-Path $CleanupRoot 'rollback-CodexAccountManager.exe'

function Write-UpdaterLog {
    param([Parameter(Mandatory = $true)][string]$Message)
    $line = '[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $Message
    Add-Content -LiteralPath $UpdaterLogPath -Value $line -Encoding UTF8
}

try {
    $logRoot = Split-Path -Parent $UpdaterLogPath
    New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
    Write-UpdaterLog 'Updater started.'
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        throw 'Codex Account Manager did not exit within 45 seconds.'
    }

    Write-UpdaterLog 'Previous process exited. Starting package installer.'
    if (Test-Path -LiteralPath $CurrentExecutablePath -PathType Leaf) {
        Copy-Item -LiteralPath $CurrentExecutablePath -Destination $rollbackExecutablePath -Force
        Write-UpdaterLog 'Saved the previous executable for rollback.'
        try {
            $shutdownProcess = Start-Process -FilePath $CurrentExecutablePath `
                -ArgumentList '--shutdown-local-pat-gateway' -WorkingDirectory $WorkingDirectory `
                -Wait -PassThru
            Write-UpdaterLog ('Gateway shutdown command exited with code {0}.' -f $shutdownProcess.ExitCode)
        }
        catch {
            Write-UpdaterLog ('Gateway shutdown command failed: ' + $_.Exception.Message)
        }
    }

    $processPath = [IO.Path]::GetFullPath($CurrentExecutablePath)
    $remaining = @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ExecutablePath -and
                [string]::Equals(
                    [IO.Path]::GetFullPath($_.ExecutablePath),
                    $processPath,
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    foreach ($item in $remaining) {
        Write-UpdaterLog ('Stopping remaining process {0} before installation.' -f $item.ProcessId)
        Stop-Process -Id $item.ProcessId -Force -ErrorAction Stop
    }

    $childPowerShell = Join-Path $PSHOME 'powershell.exe'
    & $childPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $InstallerPath `
        -Quiet -NoLaunch -InstallPath $InstallPath `
        -ManagerWorkingDirectory $managerRoot -LogPath $InstallerLogPath
    $installerExitCode = $LASTEXITCODE
    if ($installerExitCode -ne 0) {
        throw ('Package installer exited with code {0}.' -f $installerExitCode)
    }

    $installedExe = Join-Path $InstallPath 'CodexAccountManager.exe'
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw 'The updated executable was not found after installation.'
    }
    Remove-Item -LiteralPath $FailureMarkerPath -Force -ErrorAction SilentlyContinue
    Write-UpdaterLog 'Installation completed. Restarting the updated application.'
    Start-Process -FilePath $installedExe -WorkingDirectory $WorkingDirectory | Out-Null
    Start-Sleep -Seconds 2
}
catch {
    $exitCode = 1
    $failure = $_.Exception.Message
    try { Set-Content -LiteralPath $FailureMarkerPath -Value $failure -Encoding UTF8 } catch { }
    try { Write-UpdaterLog ('Update failed: ' + $failure) } catch { }
    $fallbackExecutablePath = $null
    if (Test-Path -LiteralPath $rollbackExecutablePath -PathType Leaf) {
        try {
            Copy-Item -LiteralPath $rollbackExecutablePath -Destination $CurrentExecutablePath -Force
            $fallbackExecutablePath = $CurrentExecutablePath
            Write-UpdaterLog 'Restored the previous executable after the update failure.'
        }
        catch {
            $fallbackExecutablePath = $rollbackExecutablePath
            try { Write-UpdaterLog ('Could not restore the previous executable in place: ' + $_.Exception.Message) } catch { }
        }
    }
    elseif (Test-Path -LiteralPath $CurrentExecutablePath -PathType Leaf) {
        $fallbackExecutablePath = $CurrentExecutablePath
    }
    if ($fallbackExecutablePath) {
        try {
            Start-Process -FilePath $fallbackExecutablePath -WorkingDirectory $WorkingDirectory | Out-Null
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

    private sealed class UpdateCheckException(
        AppUpdateCheckStatus status,
        string message,
        Exception? innerException = null) : Exception(message, innerException)
    {
        internal AppUpdateCheckStatus Status { get; } = status;
    }
}
