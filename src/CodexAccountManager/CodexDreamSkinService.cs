using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountManager;

// Keeps the historical type name so published builds remain source-compatible. Native
// appearance settings provide readable colors while the vendored Dream Skin runtime owns
// the opt-in image layer and its verified loopback CDP lifecycle.
internal static class CodexDreamSkinService
{
    private static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RuntimeTimeout = TimeSpan.FromSeconds(120);
    private static readonly Regex SecretPattern = new(
        "sk-[A-Za-z0-9_-]{8,}|eyJ[A-Za-z0-9._-]{20,}",
        RegexOptions.Compiled);

    private static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexDreamSkin");

    private static string BundleRoot
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(
                "CODEX_ACCOUNT_MANAGER_DREAM_SKIN_ROOT");
            return Path.GetFullPath(string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(AppContext.BaseDirectory, "CodexDreamSkin")
                : overridePath);
        }
    }

    private static string AppearanceScript => Path.Combine(
        BundleRoot,
        "scripts",
        "set-account-manager-codex-appearance.ps1");

    private static string DreamThemeScript => Path.Combine(
        BundleRoot,
        "scripts",
        "set-account-manager-dream-theme.ps1");

    private static string InstallScript => Path.Combine(
        BundleRoot,
        "scripts",
        "install-account-manager-theme.ps1");

    private static string BundledStartScript => Path.Combine(
        BundleRoot,
        "scripts",
        "start-dream-skin.ps1");

    private static string BundledRestoreScript => Path.Combine(
        BundleRoot,
        "scripts",
        "restore-dream-skin.ps1");

    private static string InstalledEngineRoot => Path.Combine(StateRoot, "engine");
    private static string InstalledStartScript => Path.Combine(
        InstalledEngineRoot,
        "scripts",
        "start-dream-skin.ps1");
    private static string InstalledRestoreScript => Path.Combine(
        InstalledEngineRoot,
        "scripts",
        "restore-dream-skin.ps1");
    private static string DreamSkinStatePath => Path.Combine(StateRoot, "state.json");
    private static string DreamSkinConfigBackupPath => Path.Combine(
        StateRoot,
        "config.before-dream-skin.toml");
    private static string AppearanceConfigBackupPath => Path.Combine(
        StateRoot,
        "config.before-account-manager-appearance.toml");

    private static string AppearanceMarkerPath => Path.Combine(
        StateRoot,
        "account-manager-codex-appearance.json");

    private static string CustomAppearancePath => Path.Combine(
        StateRoot,
        "custom-codex-appearance.json");

    public static string? GetPreviewAssetPath(string? assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName) ||
            assetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            assetName.Contains(Path.DirectorySeparatorChar) ||
            assetName.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        var presetPath = Path.Combine(BundleRoot, "assets", "presets", assetName);
        if (File.Exists(presetPath))
        {
            return presetPath;
        }

        // Account Manager's four palette-matched wallpapers live at the asset root instead of
        // inside the upstream public-preset directory.
        var rootAssetPath = Path.Combine(BundleRoot, "assets", assetName);
        return File.Exists(rootAssetPath) ? rootAssetPath : null;
    }

    public static string GetStatusText()
    {
        if (!HasBundledRuntime())
        {
            return "外观组件缺失";
        }

        try
        {
            if (!File.Exists(AppearanceMarkerPath))
            {
                return File.Exists(DreamSkinStatePath)
                    ? "图片背景运行中"
                    : "未启用（使用 Codex 默认外观）";
            }

            using var document = JsonDocument.Parse(File.ReadAllText(AppearanceMarkerPath));
            var label = document.RootElement.TryGetProperty("label", out var labelValue)
                ? labelValue.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(label))
            {
                return File.Exists(DreamSkinStatePath)
                    ? "图片背景已启用：" + label
                    : "已配置：" + label + "（等待启动）";
            }
            var mode = document.RootElement.TryGetProperty("mode", out var value)
                ? value.GetString()
                : null;
            return Enum.TryParse<ThemeMode>(mode, out var parsed)
                ? "已配置：" + GetDisplayName(parsed)
                : "已配置到 Codex";
        }
        catch
        {
            return File.Exists(DreamSkinStatePath)
                ? "图片背景运行中"
                : "未启用（使用 Codex 默认外观）";
        }
    }

    /// <summary>
    /// Reports the actual persisted Codex appearance state. The manager's startup-sync
    /// preference is intentionally separate: disabling future synchronization must not make
    /// an already-running Dream Skin look like the official appearance in the theme library.
    /// </summary>
    public static bool IsOfficialAppearanceActive()
    {
        try
        {
            return !File.Exists(AppearanceMarkerPath) &&
                   !File.Exists(DreamSkinStatePath);
        }
        catch
        {
            return false;
        }
    }

    public static void Install()
    {
        ValidateBundledRuntime();
        RunPowerShellScript(InstallScript, [], RuntimeTimeout);
    }

    public static void ApplyAppearance(
        ThemeMode mode,
        string presetId = "manager",
        string? label = null,
        CustomCodexTheme? customTheme = null)
    {
        ValidateBundledRuntime();
        var arguments = new List<string> { "-Mode", mode.ToString(), "-PresetId", presetId };
        if (presetId.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            if (customTheme != null)
            {
                SaveCustomAppearance(customTheme);
            }
            if (!File.Exists(CustomAppearancePath))
            {
                throw new InvalidOperationException("请先在主题设置中保存自定义 Codex 主题。");
            }
            arguments.Add("-CustomThemePath");
            arguments.Add(CustomAppearancePath);
        }

        // The four Account Manager cards intentionally share the native "manager" preset so
        // their explicit ThemeMode selects the matching Codex chrome settings. The image layer
        // still needs a stable, mode-specific ID so it can use the Account Manager artwork and
        // palette instead of silently falling back to the public Midnight Aurora preset.
        var dreamPresetId = presetId.Equals("manager", StringComparison.OrdinalIgnoreCase)
            ? mode switch
            {
                ThemeMode.Light => "manager-light",
                ThemeMode.PorcelainLight => "manager-porcelain-light",
                ThemeMode.Dark => "manager-dark",
                ThemeMode.NebulaDark => "manager-nebula-dark",
                _ => "manager-nebula-dark"
            }
            : presetId;
        var dreamArguments = new List<string> { "-PresetId", dreamPresetId };
        if (dreamPresetId.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            dreamArguments.Add("-CustomThemePath");
            dreamArguments.Add(CustomAppearancePath);
        }

        RunPowerShellScript(
            DreamThemeScript,
            dreamArguments,
            ConfigTimeout);
        RunPowerShellScript(
            AppearanceScript,
            arguments,
            ConfigTimeout);
        Directory.CreateDirectory(StateRoot);
        var marker = JsonSerializer.Serialize(new
        {
            mode = mode.ToString(),
            presetId,
            label = label ?? GetDisplayName(mode),
            appliedAt = DateTimeOffset.UtcNow
        });
        File.WriteAllText(AppearanceMarkerPath, marker);
    }

    public static void Start()
    {
        ValidateBundledRuntime();
        var startScript = File.Exists(InstalledStartScript)
            ? InstalledStartScript
            : BundledStartScript;
        RunPowerShellScript(
            startScript,
            ["-RestartExisting"],
            RuntimeTimeout);
    }

    public static void SaveCustomAppearance(CustomCodexTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Directory.CreateDirectory(StateRoot);
        theme.BackgroundImagePath = SaveManagedCustomBackground(theme.BackgroundImagePath);
        File.WriteAllText(
            CustomAppearancePath,
            JsonSerializer.Serialize(theme, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? SaveManagedCustomBackground(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("选择的 Codex 背景照片已不存在。", source);
        }
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not ".jpg" and not ".jpeg" and not ".png" and not ".webp")
        {
            throw new InvalidOperationException("Codex 背景照片仅支持 JPG、PNG 或 WebP。 ");
        }
        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length is < 1 or > 16 * 1024 * 1024)
        {
            throw new InvalidOperationException("Codex 背景照片大小必须在 1 字节到 16 MB 之间。");
        }

        var assetRoot = Path.Combine(StateRoot, "custom-assets");
        Directory.CreateDirectory(assetRoot);
        var target = Path.Combine(assetRoot, "custom-background" + extension);
        if (Path.GetFullPath(target).Equals(source, StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return target;
    }

    public static void RestoreOfficialAppearance()
    {
        ValidateBundledRuntime();
        var restoreScript = File.Exists(InstalledRestoreScript)
            ? InstalledRestoreScript
            : BundledRestoreScript;

        // Restore in the reverse order of installation. The Account Manager appearance backup
        // is captured after Dream Skin installs its base chrome (O -> B -> A). Restoring Dream
        // Skin first and that backup second would incorrectly end at B and leave a tinted base
        // theme behind. A -> B must happen before B -> O.
        RunPowerShellScript(AppearanceScript, ["-Restore"], ConfigTimeout);
        // Retire the A -> B backup before attempting B -> O. If archiving is blocked, both the
        // marker and Dream Skin's base backup are still present and a retry remains safe. Once
        // archived, a failed B -> O retry simply captures the current B state again.
        ArchiveCompletedAppearanceBackup();
        if (File.Exists(DreamSkinStatePath) || File.Exists(DreamSkinConfigBackupPath))
        {
            var restoreArguments = new List<string>();
            if (File.Exists(DreamSkinConfigBackupPath))
            {
                restoreArguments.Add("-RestoreBaseTheme");
            }
            restoreArguments.Add("-ForceRestart");
            restoreArguments.Add("-NoRelaunch");
            RunPowerShellScript(restoreScript, restoreArguments, RuntimeTimeout);
        }
        if (File.Exists(AppearanceMarkerPath))
        {
            File.Delete(AppearanceMarkerPath);
        }
    }

    private static void ArchiveCompletedAppearanceBackup()
    {
        if (!File.Exists(AppearanceConfigBackupPath))
        {
            return;
        }

        var archiveName = $"config.restored-account-manager-appearance-" +
                          $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-" +
                          $"{Guid.NewGuid():N}.toml";
        File.Move(
            AppearanceConfigBackupPath,
            Path.Combine(StateRoot, archiveName));
    }

    internal static void ValidateBundledRuntime()
    {
        var required = new[]
        {
            Path.Combine(BundleRoot, "bundle-version.txt"),
            AppearanceScript,
            DreamThemeScript,
            InstallScript,
            BundledStartScript,
            BundledRestoreScript,
            Path.Combine(BundleRoot, "scripts", "config-utf8.ps1"),
            Path.Combine(BundleRoot, "scripts", "theme-windows.ps1"),
            Path.Combine(BundleRoot, "scripts", "injector.mjs"),
            Path.Combine(BundleRoot, "assets", "renderer-inject.js"),
            Path.Combine(BundleRoot, "assets", "dream-skin.css"),
            Path.Combine(BundleRoot, "assets", "account-manager-nebula.jpg"),
            Path.Combine(BundleRoot, "assets", "account-manager-aurora-light.jpg"),
            Path.Combine(BundleRoot, "assets", "account-manager-porcelain-light.jpg"),
            Path.Combine(BundleRoot, "assets", "account-manager-deep-sea.jpg"),
            Path.Combine(BundleRoot, "assets", "account-manager-nebula-orbit.jpg"),
            Path.Combine(BundleRoot, "assets", "account-manager-nebula-theme.json"),
            Path.Combine(BundleRoot, "assets", "UPSTREAM-PRESETS-NOTICE.md"),
            Path.Combine(BundleRoot, "assets", "PRESET-PROVENANCE.md"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-arina-hashimoto.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-arina-hashimoto-preview.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-arina-hashimoto", "background.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-arina-hashimoto", "theme.json"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-gothic-void-crusade.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-gothic-void-crusade-preview.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-gothic-void-crusade", "background.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-gothic-void-crusade", "theme.json"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-midnight-aurora.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-midnight-aurora", "background.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-midnight-aurora", "theme.json"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-sakura-dawn.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-sakura-dawn", "background.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-sakura-dawn", "theme.json"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-amber-dusk.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-amber-dusk", "background.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-amber-dusk", "theme.json"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-forest-mist.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-forest-mist", "background.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-forest-mist", "theme.json"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-cyber-neon.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-cyber-neon", "background.jpg"),
            Path.Combine(BundleRoot, "assets", "presets", "preset-cyber-neon", "theme.json")
        };
        var missing = required.FirstOrDefault(path => !File.Exists(path));
        if (missing != null)
        {
            throw new FileNotFoundException("Codex 外观同步组件不完整。", missing);
        }
    }

    private static bool HasBundledRuntime()
    {
        try
        {
            ValidateBundledRuntime();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDisplayName(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "极光浅色",
        ThemeMode.PorcelainLight => "青瓷浅色",
        ThemeMode.Dark => "深海夜色",
        ThemeMode.NebulaDark => "星云夜色",
        _ => "跟随系统"
    };

    private static void RunPowerShellScript(
        string scriptPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("无法启动 Codex 外观配置进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            throw new TimeoutException("Codex 外观配置超时，请稍后重试。");
        }

        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5));
        if (process.ExitCode == 0)
        {
            return;
        }

        var detail = string.Join(
            Environment.NewLine,
            new[] { stderr.Result, stdout.Result }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
        detail = SecretPattern.Replace(detail, "<redacted>");
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"Codex 外观配置失败，退出代码 {process.ExitCode}。"
            : detail);
    }
}
