using System.Diagnostics;
using System.IO.Compression;
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

    internal async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var releaseDocument = await GetJsonAsync(ReleaseApiUrl, cancellationToken).ConfigureAwait(false);
        if (releaseDocument is null)
        {
            return null;
        }

        var release = releaseDocument.RootElement;
        var releaseUrl = ReadString(release, "html_url") ?? "https://github.com/" + Repository + "/releases";
        var releaseAssets = ReadAssets(release);
        var manifestAsset = releaseAssets.FirstOrDefault(asset =>
            string.Equals(asset.Name, "update-manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestAsset is null)
        {
            return null;
        }

        using var manifestDocument = await GetJsonAsync(manifestAsset.Url, cancellationToken).ConfigureAwait(false);
        if (manifestDocument is null)
        {
            return null;
        }

        var manifest = manifestDocument.RootElement;
        var remoteVersion = NormalizeVersion(ReadString(manifest, "version"));
        if (remoteVersion is null || !IsNewer(remoteVersion, CurrentVersion))
        {
            return null;
        }

        var commit = ReadString(manifest, "commit") ?? string.Empty;
        var platformName = "windows";
        if (!manifest.TryGetProperty("assets", out var manifestAssets) ||
            !manifestAssets.TryGetProperty(platformName, out var platformAsset))
        {
            return null;
        }

        var assetName = ReadString(platformAsset, "name");
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return null;
        }

        var releaseAsset = releaseAssets.FirstOrDefault(asset =>
            string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (releaseAsset is null)
        {
            return null;
        }

        var sha256 = NormalizeSha256(ReadString(platformAsset, "sha256"));
        if (sha256 is null)
        {
            sha256 = NormalizeSha256(releaseAsset.Digest?.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase));
        }
        if (sha256 is null)
        {
            return null;
        }

        return new AppUpdateInfo(
            remoteVersion,
            commit,
            releaseUrl,
            releaseAsset.Name,
            releaseAsset.Url,
            sha256);
    }

    internal async Task ScheduleInstallAsync(
        AppUpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        var updateRoot = Path.Combine(
            Path.GetTempPath(),
            "CodexAccountManager-update",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateRoot);
        var zipPath = Path.Combine(updateRoot, Path.GetFileName(update.AssetName));
        var extractRoot = Path.Combine(updateRoot, "extracted");

        try
        {
            await DownloadAndVerifyAsync(update.AssetUrl, zipPath, update.Sha256, cancellationToken)
                .ConfigureAwait(false);
            ExtractZipSafely(zipPath, extractRoot);
            var installerPath = Directory
                .GetFiles(extractRoot, "Install-CodexAccountManager.ps1", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (installerPath is null)
            {
                throw new InvalidOperationException("更新包缺少 Windows 安装脚本。");
            }

            await LocalPatGateway.ShutdownIfRunningAsync().ConfigureAwait(false);
            var helperPath = Path.Combine(updateRoot, "apply-update.ps1");
            await File.WriteAllTextAsync(helperPath, BuildHelperScript(), new UTF8Encoding(false), cancellationToken)
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
            startInfo.ArgumentList.Add(Path.GetFullPath(AppContext.BaseDirectory));
            startInfo.ArgumentList.Add("-WorkingDirectory");
            startInfo.ArgumentList.Add(Directory.Exists(Environment.CurrentDirectory)
                ? Environment.CurrentDirectory
                : AppContext.BaseDirectory);

            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("无法启动更新安装程序。");
            }
        }
        catch
        {
            try { Directory.Delete(updateRoot, recursive: true); } catch { }
            throw;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodexAccountManager", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task DownloadAndVerifyAsync(
        string url,
        string destination,
        string expectedSha256,
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
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新包 SHA256 校验失败，已拒绝安装。");
        }
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
    [Parameter(Mandatory = $true)][string]$WorkingDirectory
)
$ErrorActionPreference = 'Stop'
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        throw 'Codex Account Manager 未能在规定时间内退出。'
    }
    & $InstallerPath -Quiet -NoLaunch -NoShortcuts -InstallPath $InstallPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $installedExe = Join-Path $InstallPath 'CodexAccountManager.exe'
    Start-Process -FilePath $installedExe -WorkingDirectory $WorkingDirectory | Out-Null
    Start-Sleep -Seconds 2
}
finally {
    Remove-Item -LiteralPath $CleanupRoot -Recurse -Force -ErrorAction SilentlyContinue
}
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
}
