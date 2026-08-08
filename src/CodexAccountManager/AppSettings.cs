using System.Text.Json;

namespace CodexAccountManager;

public sealed class AppSettings
{
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;
    public WindowsClientMode WindowsClientMode { get; set; } =
        global::CodexAccountManager.WindowsClientMode.CodexPlusPlus;
    public bool UseCodexDreamSkin { get; set; }
    public string CodexAppearancePresetId { get; set; } = "preset-midnight-aurora";
    public CustomCodexTheme CustomCodexTheme { get; set; } = new();
    public string? CurrentAccountName { get; set; }
    public string? ProjectPath { get; set; }
    public string? PatGatewayProxy { get; set; }
    // Structured proxy fields supersede PatGatewayProxy while retaining it for older
    // portable configurations.  The address is local by default; a null port means that
    // the UI should perform a loopback-only detector pass before starting the gateway.
    public string? PatGatewayProxyAddress { get; set; } = "127.0.0.1";
    public int? PatGatewayProxyPort { get; set; }
    public bool PatGatewayProxyAutoDetect { get; set; } = true;
    public string? PatGatewayProxyScheme { get; set; } = "http";
    public bool PatGatewayEnabled { get; set; } = true;
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
}

public sealed class CustomCodexTheme
{
    public string Name { get; set; } = "我的主题";
    public bool IsDark { get; set; } = true;
    public string CodeThemeId { get; set; } = "tokyo-night";
    public string AccentColor { get; set; } = "#4DB892";
    public string SurfaceColor { get; set; } = "#0D1A16";
    public string InkColor { get; set; } = "#E8F5EE";
    public string? BackgroundImagePath { get; set; }
    public int Contrast { get; set; } = 92;

    public CustomCodexTheme Clone() => new()
    {
        Name = Name,
        IsDark = IsDark,
        CodeThemeId = CodeThemeId,
        AccentColor = AccentColor,
        SurfaceColor = SurfaceColor,
        InkColor = InkColor,
        BackgroundImagePath = BackgroundImagePath,
        Contrast = Contrast
    };
}

public enum WindowsClientMode
{
    CodexPlusPlus = 0,
    OfficialCodex = 1
}

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,
    PorcelainLight = 3,
    NebulaDark = 4
}

public sealed class ThemePalette
{
    public required Color FormBackColor { get; init; }
    public required Color SurfaceColor { get; init; }
    public required Color SurfaceAltColor { get; init; }
    public required Color SidebarColor { get; init; }
    public required Color CardColor { get; init; }
    public required Color BorderColor { get; init; }
    public required Color DividerColor { get; init; }
    public required Color PrimaryColor { get; init; }
    public required Color PrimaryHoverColor { get; init; }
    public required Color PrimaryPressedColor { get; init; }
    public required Color AccentColor { get; init; }
    public required Color SecondaryAccentColor { get; init; }
    public required Color TertiaryAccentColor { get; init; }
    public required Color FocusColor { get; init; }
    public required Color SoftButtonColor { get; init; }
    public required Color SoftButtonHoverColor { get; init; }
    public required Color InputBackColor { get; init; }
    public required Color TextColor { get; init; }
    public required Color MutedTextColor { get; init; }
    public required Color StatusBackColor { get; init; }
    public required Color SuccessColor { get; init; }
    public required Color WarningColor { get; init; }
    public required Color DangerColor { get; init; }
    public required Color SidebarTextColor { get; init; }
    public required Color SidebarMutedTextColor { get; init; }
    public required Color SidebarHoverColor { get; init; }
    public required Color SidebarSelectedColor { get; init; }
    public required Color SidebarBorderColor { get; init; }
    public required Color HeroStartColor { get; init; }
    public required Color HeroEndColor { get; init; }
    public required Color HeroTextColor { get; init; }
    public required Color HeroMutedTextColor { get; init; }
    public required Color ShadowColor { get; init; }
    public required Color DisabledColor { get; init; }
    public required Color ProgressTrackColor { get; init; }
}

public sealed class ThemeService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public ThemeService(string rootPath)
    {
        _settingsPath = Path.Combine(rootPath, "appsettings.json");
    }

    public AppSettings LoadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(settings, JsonOptions),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A stale temporary settings file is ignored by readers.
            }
        }
    }

    public ThemePalette GetPalette(ThemeMode mode)
    {
        var resolved = mode == ThemeMode.System ? ResolveSystemTheme() : mode;
        return resolved switch
        {
            ThemeMode.Dark => CreateDarkPalette(),
            ThemeMode.PorcelainLight => CreatePorcelainLightPalette(),
            ThemeMode.NebulaDark => CreateNebulaDarkPalette(),
            _ => CreateLightPalette()
        };
    }

    private static ThemeMode ResolveSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int themeValue)
            {
                return themeValue == 0 ? ThemeMode.Dark : ThemeMode.Light;
            }
        }
        catch
        {
        }

        return ThemeMode.Light;
    }

    private static ThemePalette CreateLightPalette()
    {
        return new ThemePalette
        {
            FormBackColor = Color.FromArgb(245, 247, 251),
            SurfaceColor = Color.FromArgb(255, 255, 255),
            SurfaceAltColor = Color.FromArgb(246, 248, 252),
            SidebarColor = Color.FromArgb(11, 20, 36),
            CardColor = Color.White,
            BorderColor = Color.FromArgb(227, 232, 241),
            DividerColor = Color.FromArgb(237, 241, 246),
            PrimaryColor = Color.FromArgb(88, 105, 246),
            PrimaryHoverColor = Color.FromArgb(106, 121, 255),
            PrimaryPressedColor = Color.FromArgb(70, 86, 216),
            AccentColor = Color.FromArgb(77, 141, 255),
            SecondaryAccentColor = Color.FromArgb(139, 92, 246),
            TertiaryAccentColor = Color.FromArgb(34, 184, 207),
            FocusColor = Color.FromArgb(99, 102, 241),
            SoftButtonColor = Color.FromArgb(250, 251, 253),
            SoftButtonHoverColor = Color.FromArgb(238, 243, 255),
            InputBackColor = Color.White,
            TextColor = Color.FromArgb(16, 24, 40),
            MutedTextColor = Color.FromArgb(104, 117, 138),
            StatusBackColor = Color.FromArgb(249, 251, 254),
            SuccessColor = Color.FromArgb(15, 159, 118),
            WarningColor = Color.FromArgb(199, 122, 8),
            DangerColor = Color.FromArgb(223, 75, 91),
            SidebarTextColor = Color.FromArgb(240, 245, 255),
            SidebarMutedTextColor = Color.FromArgb(151, 164, 188),
            SidebarHoverColor = Color.FromArgb(19, 35, 59),
            SidebarSelectedColor = Color.FromArgb(27, 49, 85),
            SidebarBorderColor = Color.FromArgb(42, 70, 111),
            HeroStartColor = Color.FromArgb(24, 43, 82),
            HeroEndColor = Color.FromArgb(67, 59, 126),
            HeroTextColor = Color.FromArgb(248, 250, 255),
            HeroMutedTextColor = Color.FromArgb(191, 204, 229),
            ShadowColor = Color.FromArgb(30, 44, 73),
            DisabledColor = Color.FromArgb(221, 226, 236),
            ProgressTrackColor = Color.FromArgb(230, 235, 244)
        };
    }

    private static ThemePalette CreateDarkPalette()
    {
        return new ThemePalette
        {
            FormBackColor = Color.FromArgb(7, 16, 29),
            SurfaceColor = Color.FromArgb(13, 27, 46),
            SurfaceAltColor = Color.FromArgb(18, 36, 59),
            SidebarColor = Color.FromArgb(5, 13, 24),
            CardColor = Color.FromArgb(16, 32, 57),
            BorderColor = Color.FromArgb(32, 58, 94),
            DividerColor = Color.FromArgb(28, 49, 79),
            PrimaryColor = Color.FromArgb(47, 107, 255),
            PrimaryHoverColor = Color.FromArgb(74, 127, 255),
            PrimaryPressedColor = Color.FromArgb(37, 89, 212),
            AccentColor = Color.FromArgb(34, 211, 238),
            SecondaryAccentColor = Color.FromArgb(96, 165, 250),
            TertiaryAccentColor = Color.FromArgb(167, 139, 250),
            FocusColor = Color.FromArgb(103, 232, 249),
            SoftButtonColor = Color.FromArgb(19, 40, 65),
            SoftButtonHoverColor = Color.FromArgb(27, 53, 84),
            InputBackColor = Color.FromArgb(9, 24, 42),
            TextColor = Color.FromArgb(245, 247, 251),
            MutedTextColor = Color.FromArgb(166, 182, 204),
            StatusBackColor = Color.FromArgb(8, 22, 38),
            SuccessColor = Color.FromArgb(52, 211, 153),
            WarningColor = Color.FromArgb(245, 158, 11),
            DangerColor = Color.FromArgb(251, 113, 133),
            SidebarTextColor = Color.FromArgb(242, 246, 255),
            SidebarMutedTextColor = Color.FromArgb(139, 154, 179),
            SidebarHoverColor = Color.FromArgb(12, 31, 54),
            SidebarSelectedColor = Color.FromArgb(17, 48, 89),
            SidebarBorderColor = Color.FromArgb(27, 61, 101),
            HeroStartColor = Color.FromArgb(4, 40, 57),
            HeroEndColor = Color.FromArgb(57, 32, 115),
            HeroTextColor = Color.FromArgb(248, 250, 255),
            HeroMutedTextColor = Color.FromArgb(182, 196, 222),
            ShadowColor = Color.FromArgb(0, 0, 0),
            DisabledColor = Color.FromArgb(43, 52, 67),
            ProgressTrackColor = Color.FromArgb(36, 47, 66)
        };
    }

    private static ThemePalette CreatePorcelainLightPalette()
    {
        return new ThemePalette
        {
            FormBackColor = Color.FromArgb(242, 246, 245),
            SurfaceColor = Color.FromArgb(252, 253, 252),
            SurfaceAltColor = Color.FromArgb(234, 241, 239),
            SidebarColor = Color.FromArgb(17, 47, 45),
            CardColor = Color.FromArgb(255, 255, 255),
            BorderColor = Color.FromArgb(207, 222, 218),
            DividerColor = Color.FromArgb(226, 235, 232),
            PrimaryColor = Color.FromArgb(40, 107, 98),
            PrimaryHoverColor = Color.FromArgb(52, 126, 115),
            PrimaryPressedColor = Color.FromArgb(32, 86, 79),
            AccentColor = Color.FromArgb(78, 143, 132),
            SecondaryAccentColor = Color.FromArgb(115, 151, 164),
            TertiaryAccentColor = Color.FromArgb(194, 164, 104),
            FocusColor = Color.FromArgb(86, 142, 159),
            SoftButtonColor = Color.FromArgb(239, 244, 242),
            SoftButtonHoverColor = Color.FromArgb(227, 236, 233),
            InputBackColor = Color.FromArgb(252, 253, 252),
            TextColor = Color.FromArgb(24, 43, 41),
            MutedTextColor = Color.FromArgb(107, 125, 121),
            StatusBackColor = Color.FromArgb(246, 249, 248),
            SuccessColor = Color.FromArgb(47, 125, 104),
            WarningColor = Color.FromArgb(168, 117, 43),
            DangerColor = Color.FromArgb(185, 80, 76),
            SidebarTextColor = Color.FromArgb(244, 248, 247),
            SidebarMutedTextColor = Color.FromArgb(169, 190, 186),
            SidebarHoverColor = Color.FromArgb(25, 62, 58),
            SidebarSelectedColor = Color.FromArgb(36, 85, 79),
            SidebarBorderColor = Color.FromArgb(62, 110, 104),
            HeroStartColor = Color.FromArgb(23, 75, 70),
            HeroEndColor = Color.FromArgb(73, 111, 122),
            HeroTextColor = Color.FromArgb(248, 251, 250),
            HeroMutedTextColor = Color.FromArgb(207, 222, 221),
            ShadowColor = Color.FromArgb(41, 74, 70),
            DisabledColor = Color.FromArgb(217, 227, 224),
            ProgressTrackColor = Color.FromArgb(221, 231, 228)
        };
    }

    private static ThemePalette CreateNebulaDarkPalette()
    {
        return new ThemePalette
        {
            FormBackColor = Color.FromArgb(14, 10, 26),
            SurfaceColor = Color.FromArgb(23, 17, 41),
            SurfaceAltColor = Color.FromArgb(33, 24, 58),
            SidebarColor = Color.FromArgb(11, 7, 22),
            CardColor = Color.FromArgb(26, 19, 48),
            BorderColor = Color.FromArgb(58, 42, 91),
            DividerColor = Color.FromArgb(46, 35, 73),
            PrimaryColor = Color.FromArgb(124, 92, 252),
            PrimaryHoverColor = Color.FromArgb(146, 119, 255),
            PrimaryPressedColor = Color.FromArgb(104, 71, 222),
            AccentColor = Color.FromArgb(192, 132, 252),
            SecondaryAccentColor = Color.FromArgb(244, 114, 182),
            TertiaryAccentColor = Color.FromArgb(34, 211, 238),
            FocusColor = Color.FromArgb(232, 121, 249),
            SoftButtonColor = Color.FromArgb(37, 27, 64),
            SoftButtonHoverColor = Color.FromArgb(51, 35, 84),
            InputBackColor = Color.FromArgb(19, 14, 35),
            TextColor = Color.FromArgb(248, 245, 255),
            MutedTextColor = Color.FromArgb(185, 173, 206),
            StatusBackColor = Color.FromArgb(17, 12, 32),
            SuccessColor = Color.FromArgb(52, 211, 153),
            WarningColor = Color.FromArgb(251, 191, 36),
            DangerColor = Color.FromArgb(251, 113, 133),
            SidebarTextColor = Color.FromArgb(247, 242, 255),
            SidebarMutedTextColor = Color.FromArgb(170, 155, 188),
            SidebarHoverColor = Color.FromArgb(27, 18, 48),
            SidebarSelectedColor = Color.FromArgb(50, 32, 91),
            SidebarBorderColor = Color.FromArgb(75, 51, 123),
            HeroStartColor = Color.FromArgb(45, 20, 92),
            HeroEndColor = Color.FromArgb(111, 29, 91),
            HeroTextColor = Color.FromArgb(251, 248, 255),
            HeroMutedTextColor = Color.FromArgb(213, 200, 232),
            ShadowColor = Color.FromArgb(0, 0, 0),
            DisabledColor = Color.FromArgb(61, 53, 75),
            ProgressTrackColor = Color.FromArgb(50, 42, 67)
        };
    }
}

internal static class ThemeStyler
{
    public static bool IsDark(ThemePalette palette) => palette.FormBackColor.GetBrightness() < 0.35F;

    public static void ApplyInput(TextBox textBox, ThemePalette palette)
    {
        textBox.BackColor = palette.InputBackColor;
        textBox.ForeColor = palette.TextColor;
        textBox.BorderStyle = textBox.Parent is ModernInputShell
            ? BorderStyle.None
            : BorderStyle.FixedSingle;
    }

    public static void ApplyInputShell(ModernInputShell shell, ThemePalette palette)
    {
        shell.ApplyPalette(palette);
    }

    public static void ApplyComboBox(ComboBox comboBox, ThemePalette palette)
    {
        comboBox.BackColor = palette.InputBackColor;
        comboBox.ForeColor = palette.TextColor;
        comboBox.FlatStyle = FlatStyle.Flat;
        if (comboBox is ThemedComboBox themed)
        {
            themed.ApplyPalette(palette);
        }
    }

    public static void ApplyLabel(Label label, ThemePalette palette, bool muted = false)
    {
        label.ForeColor = muted ? palette.MutedTextColor : palette.TextColor;
        label.BackColor = Color.Transparent;
    }

    public static void ApplyPrimaryButton(Button button, ThemePalette palette)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = palette.PrimaryColor;
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
        button.UseMnemonic = false;
        button.FlatAppearance.MouseOverBackColor = palette.PrimaryHoverColor;
        button.FlatAppearance.MouseDownBackColor = palette.PrimaryPressedColor;
        if (button is ModernButton modern)
        {
            modern.BaseBackColor = palette.PrimaryColor;
            modern.HoverBackColor = palette.PrimaryHoverColor;
            modern.PressedBackColor = palette.PrimaryPressedColor;
            modern.BorderColor = Color.FromArgb(
                112,
                UiDesign.Blend(palette.PrimaryColor, Color.White, 0.48F));
            modern.GradientBackColor = Color.FromArgb(118, palette.SecondaryAccentColor);
            modern.ShadowColor = Color.FromArgb(
                IsDark(palette) ? 86 : 42,
                UiDesign.Blend(palette.PrimaryColor, palette.ShadowColor, 0.22F));
            modern.TextColor = Color.White;
            modern.UseSurfaceSheen = true;
            modern.DisabledBackColor = UiDesign.Blend(palette.DisabledColor, palette.FormBackColor, 0.25F);
            modern.DisabledTextColor = UiDesign.Blend(palette.MutedTextColor, palette.FormBackColor, 0.35F);
            modern.FocusColor = Color.FromArgb(190, palette.AccentColor);
            modern.Invalidate();
        }
    }

    public static void ApplySoftButton(Button button, ThemePalette palette)
    {
        var dark = IsDark(palette);
        var softBase = UiDesign.Blend(
            palette.SoftButtonColor,
            palette.PrimaryColor,
            dark ? 0.08F : 0.035F);
        var softHover = UiDesign.Blend(
            palette.SoftButtonHoverColor,
            palette.PrimaryColor,
            dark ? 0.15F : 0.10F);
        var softPressed = UiDesign.Blend(softHover, palette.PrimaryColor, 0.16F);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = UiDesign.Blend(palette.BorderColor, palette.PrimaryColor, 0.26F);
        button.BackColor = softBase;
        button.ForeColor = palette.TextColor;
        button.Cursor = Cursors.Hand;
        button.UseMnemonic = false;
        button.FlatAppearance.MouseOverBackColor = softHover;
        button.FlatAppearance.MouseDownBackColor = softPressed;
        if (button is ModernButton modern)
        {
            modern.BaseBackColor = softBase;
            modern.HoverBackColor = softHover;
            modern.PressedBackColor = softPressed;
            modern.BorderColor = UiDesign.Blend(palette.BorderColor, palette.PrimaryColor, 0.28F);
            modern.GradientBackColor = Color.FromArgb(
                dark ? 34 : 44,
                palette.SecondaryAccentColor);
            modern.ShadowColor = Color.FromArgb(
                dark ? 50 : 24,
                UiDesign.Blend(palette.ShadowColor, palette.PrimaryColor, 0.14F));
            modern.TextColor = palette.TextColor;
            modern.UseSurfaceSheen = true;
            modern.DisabledBackColor = UiDesign.Blend(palette.DisabledColor, palette.FormBackColor, 0.3F);
            modern.DisabledTextColor = UiDesign.Blend(palette.MutedTextColor, palette.FormBackColor, 0.35F);
            modern.FocusColor = palette.FocusColor;
            modern.Invalidate();
        }
    }

    public static void ApplyDialog(Form form, ThemePalette palette)
    {
        form.BackColor = palette.SurfaceColor;
        form.ForeColor = palette.TextColor;
        form.HandleCreated += (_, _) => NativeWindowTheme.Apply(form, IsDark(palette));
        if (form.IsHandleCreated)
        {
            NativeWindowTheme.Apply(form, IsDark(palette));
        }
    }
}
