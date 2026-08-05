using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexAccountManager;

internal sealed class CustomCodexThemeDialog : Form
{
    private static readonly (string Label, string Id)[] CodeThemes =
    [
        ("Tokyo Night", "tokyo-night"),
        ("Everforest", "everforest"),
        ("Rose Pine", "rose-pine"),
        ("Gruvbox", "gruvbox"),
        ("Night Owl", "night-owl"),
        ("Matrix", "matrix"),
        ("VS Code+", "vscode-plus"),
        ("GitHub", "github")
    ];

    private readonly ThemePalette _palette;
    private readonly TextBox _nameBox = new();
    private readonly ThemedComboBox _modeBox = new();
    private readonly ThemedComboBox _codeThemeBox = new();
    private readonly NumericUpDown _contrastBox = new();
    private readonly TextBox _backgroundPathBox = new();
    private readonly PictureBox _backgroundThumbnail = new();
    private readonly ModernButton _chooseBackgroundButton = new();
    private readonly ModernButton _clearBackgroundButton = new();
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 30000, ShowAlways = true };
    private readonly Dictionary<string, Button> _colorButtons = new(StringComparer.Ordinal);
    private readonly CodexThemePreviewControl _preview = new();
    private string? _backgroundImagePath;

    public CustomCodexTheme Theme { get; private set; }

    public CustomCodexThemeDialog(CustomCodexTheme source, ThemePalette palette)
    {
        Theme = source.Clone();
        _palette = palette;
        BuildUi();
        LoadTheme();
        RefreshPreview();
    }

    private void BuildUi()
    {
        Text = "自定义 Codex 主题";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(1040, 790);
        MinimumSize = new Size(900, 720);
        Font = new Font("Microsoft YaHei UI", 9F);
        Padding = new Padding(24);
        ThemeStyler.ApplyDialog(this, _palette);

        var title = MakeLabel("自定义 Codex 主题", 0, 0, 992, 38, 14F, true);
        title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(title);
        var subtitle = MakeLabel("选择自己的照片，并调整界面颜色与代码配色；右侧会实时显示实际 Codex 界面效果。", 0, 40, 992, 32, 8.8F, false, true);
        subtitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(subtitle);

        var editor = new Panel
        {
            Left = 0,
            Top = 86,
            Width = 360,
            Height = 620,
            AutoScroll = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
            BackColor = Color.Transparent
        };
        Controls.Add(editor);

        AddFieldLabel(editor, "主题名称", 0);
        _nameBox.SetBounds(0, 30, 346, 38);
        _nameBox.Font = new Font(Font.FontFamily, 9.3F);
        _nameBox.TextChanged += (_, _) => RefreshPreview();
        ThemeStyler.ApplyInput(_nameBox, _palette);
        editor.Controls.Add(_nameBox);

        AddFieldLabel(editor, "界面模式", 82);
        _modeBox.SetBounds(0, 112, 346, 40);
        _modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeBox.Items.AddRange(["深色", "浅色"]);
        _modeBox.SelectedIndexChanged += (_, _) => RefreshPreview();
        ThemeStyler.ApplyComboBox(_modeBox, _palette);
        editor.Controls.Add(_modeBox);

        AddFieldLabel(editor, "代码配色", 166);
        _codeThemeBox.SetBounds(0, 196, 346, 40);
        _codeThemeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _codeThemeBox.Items.AddRange(CodeThemes.Select(theme => theme.Label).ToArray());
        _codeThemeBox.SelectedIndexChanged += (_, _) => RefreshPreview();
        ThemeStyler.ApplyComboBox(_codeThemeBox, _palette);
        editor.Controls.Add(_codeThemeBox);

        AddFieldLabel(editor, "背景照片", 250);
        _backgroundThumbnail.SetBounds(0, 280, 74, 74);
        _backgroundThumbnail.BorderStyle = BorderStyle.FixedSingle;
        _backgroundThumbnail.SizeMode = PictureBoxSizeMode.Zoom;
        _backgroundThumbnail.BackColor = UiDesign.Blend(_palette.InputBackColor, _palette.FormBackColor, 0.12F);
        _backgroundThumbnail.AccessibleName = "背景照片缩略图";
        editor.Controls.Add(_backgroundThumbnail);

        _backgroundPathBox.SetBounds(84, 280, 262, 34);
        _backgroundPathBox.ReadOnly = true;
        _backgroundPathBox.PlaceholderText = "尚未选择照片";
        ThemeStyler.ApplyInput(_backgroundPathBox, _palette);
        editor.Controls.Add(_backgroundPathBox);
        _chooseBackgroundButton.Text = "选择照片";
        _chooseBackgroundButton.SetBounds(84, 320, 126, 34);
        ThemeStyler.ApplySoftButton(_chooseBackgroundButton, _palette);
        _chooseBackgroundButton.Click += (_, _) => ChooseBackgroundImage();
        editor.Controls.Add(_chooseBackgroundButton);
        _clearBackgroundButton.Text = "清除";
        _clearBackgroundButton.SetBounds(220, 320, 126, 34);
        ThemeStyler.ApplySoftButton(_clearBackgroundButton, _palette);
        _clearBackgroundButton.Click += (_, _) => ClearBackgroundImage();
        editor.Controls.Add(_clearBackgroundButton);

        AddFieldLabel(editor, "对比度", 370);
        _contrastBox.SetBounds(0, 400, 346, 38);
        _contrastBox.Minimum = 70;
        _contrastBox.Maximum = 100;
        _contrastBox.Increment = 1;
        _contrastBox.ValueChanged += (_, _) => RefreshPreview();
        _contrastBox.BackColor = _palette.InputBackColor;
        _contrastBox.ForeColor = _palette.TextColor;
        editor.Controls.Add(_contrastBox);

        AddColorRow(editor, "强调色", "accent", 454);
        AddColorRow(editor, "背景色", "surface", 504);
        AddColorRow(editor, "文字色", "ink", 554);

        _preview.SetBounds(384, 90, 632, 356);
        Controls.Add(_preview);

        var cancel = new ModernButton
        {
            Text = "取消",
            Left = 774,
            Top = 720,
            Width = 108,
            Height = 42,
            DialogResult = DialogResult.Cancel
        };
        ThemeStyler.ApplySoftButton(cancel, _palette);
        Controls.Add(cancel);

        var save = new ModernButton
        {
            Text = "保存主题",
            Left = 892,
            Top = 720,
            Width = 118,
            Height = 42
        };
        ThemeStyler.ApplyPrimaryButton(save, _palette);
        save.Click += (_, _) => SaveTheme();
        Controls.Add(save);
        AcceptButton = save;
        CancelButton = cancel;
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

        void LayoutPreview()
        {
            var scaledGap = Math.Max(12, (int)Math.Round(24F * DeviceDpi / 96F));
            var previewLeft = editor.Right + scaledGap;
            var previewRight = ClientSize.Width - Math.Max(Padding.Right, scaledGap);
            var previewTop = editor.Top + Math.Max(2, scaledGap / 6);
            var previewBottom = ClientSize.Height - Math.Max(Padding.Bottom, scaledGap) - save.Height - scaledGap;
            var availableWidth = Math.Max(1, previewRight - previewLeft);
            var availableHeight = Math.Max(1, previewBottom - previewTop);
            var previewSize = CodexThemePreviewControl.FitSixteenByNine(availableWidth, availableHeight);
            _preview.SetBounds(
                previewLeft + (availableWidth - previewSize.Width) / 2,
                previewTop + (availableHeight - previewSize.Height) / 2,
                previewSize.Width,
                previewSize.Height);
        }

        Resize += (_, _) => LayoutPreview();
        LayoutPreview();
        FormClosed += (_, _) =>
        {
            SetBackgroundThumbnail(null);
            _toolTip.Dispose();
        };
    }

    private Label MakeLabel(
        string text,
        int left,
        int top,
        int width,
        int height,
        float size,
        bool bold,
        bool muted = false)
    {
        var label = new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Font = new Font(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(label, _palette, muted);
        return label;
    }

    private void AddFieldLabel(Control parent, string text, int top)
    {
        parent.Controls.Add(MakeLabel(text, 0, top, 346, 26, 8.7F, true, true));
    }

    private void AddColorRow(Control parent, string label, string key, int top)
    {
        parent.Controls.Add(MakeLabel(label, 0, top, 78, 38, 8.7F, true, true));
        var swatch = new Button
        {
            Left = 86,
            Top = top,
            Width = 260,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false
        };
        swatch.FlatAppearance.BorderSize = 1;
        swatch.Click += (_, _) => PickColor(key);
        _colorButtons[key] = swatch;
        parent.Controls.Add(swatch);
    }

    private void LoadTheme()
    {
        _nameBox.Text = Theme.Name;
        _modeBox.SelectedIndex = Theme.IsDark ? 0 : 1;
        var codeIndex = Array.FindIndex(CodeThemes, entry =>
            entry.Id.Equals(Theme.CodeThemeId, StringComparison.OrdinalIgnoreCase));
        _codeThemeBox.SelectedIndex = codeIndex < 0 ? 0 : codeIndex;
        _contrastBox.Value = Math.Clamp(Theme.Contrast, 70, 100);
        _backgroundImagePath = string.IsNullOrWhiteSpace(Theme.BackgroundImagePath)
            ? null
            : Theme.BackgroundImagePath;
        UpdateBackgroundSelectionUi();
        SetSwatch("accent", ParseColor(Theme.AccentColor, Color.FromArgb(77, 184, 146)));
        SetSwatch("surface", ParseColor(Theme.SurfaceColor, Color.FromArgb(13, 26, 22)));
        SetSwatch("ink", ParseColor(Theme.InkColor, Color.FromArgb(232, 245, 238)));
    }

    private void PickColor(string key)
    {
        using var dialog = new ColorDialog
        {
            Color = _colorButtons[key].BackColor,
            FullOpen = true,
            AnyColor = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetSwatch(key, dialog.Color);
        RefreshPreview();
    }

    private void ChooseBackgroundImage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 Codex 背景照片",
            Filter = "图片文件 (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(_backgroundImagePath) && File.Exists(_backgroundImagePath))
        {
            dialog.FileName = _backgroundImagePath;
        }
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!TryLoadValidatedBackground(dialog.FileName, out var thumbnail, out var validationError))
        {
            MessageBox.Show(
                this,
                validationError,
                "无法使用这张照片",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _backgroundImagePath = dialog.FileName;
        SetBackgroundThumbnail(thumbnail);
        UpdateBackgroundSelectionUi(loadThumbnail: false);
        RefreshPreview();
    }

    private void ClearBackgroundImage()
    {
        _backgroundImagePath = null;
        SetBackgroundThumbnail(null);
        UpdateBackgroundSelectionUi(loadThumbnail: false);
        RefreshPreview();
    }

    private void UpdateBackgroundSelectionUi(bool loadThumbnail = true)
    {
        var hasImage = !string.IsNullOrWhiteSpace(_backgroundImagePath) && File.Exists(_backgroundImagePath);
        _backgroundPathBox.Text = hasImage ? Path.GetFileName(_backgroundImagePath) : string.Empty;
        _backgroundPathBox.AccessibleName = hasImage
            ? $"已选择背景照片：{Path.GetFileName(_backgroundImagePath)}"
            : "尚未选择背景照片";
        _chooseBackgroundButton.Text = hasImage ? "替换照片" : "选择照片";
        _clearBackgroundButton.Enabled = hasImage;
        if (hasImage)
        {
            _toolTip.SetToolTip(_backgroundPathBox, _backgroundImagePath);
        }
        else
        {
            _toolTip.SetToolTip(_backgroundPathBox, null);
        }
        if (!loadThumbnail)
        {
            return;
        }

        if (hasImage && TryLoadValidatedBackground(_backgroundImagePath!, out var thumbnail, out _))
        {
            SetBackgroundThumbnail(thumbnail);
        }
        else
        {
            SetBackgroundThumbnail(null);
        }
    }

    private void SetBackgroundThumbnail(Image? image)
    {
        var previous = _backgroundThumbnail.Image;
        _backgroundThumbnail.Image = image;
        if (!ReferenceEquals(previous, image))
        {
            previous?.Dispose();
        }
    }

    private static bool TryLoadValidatedBackground(string path, out Bitmap? image, out string error)
    {
        image = null;
        error = string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "选择的背景照片已不存在。";
                return false;
            }
            if (info.Length is < 1 or > 16 * 1024 * 1024)
            {
                error = "背景照片大小必须在 16 MB 以内。";
                return false;
            }

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
            if (source.Width < 64 || source.Height < 64)
            {
                error = "背景照片至少需要 64 × 64 像素。";
                return false;
            }
            if (source.Width > 16384 || source.Height > 16384 ||
                (long)source.Width * source.Height > 50_000_000L)
            {
                error = "背景照片分辨率过大，请选择不超过 5000 万像素的图片。";
                return false;
            }

            image = new Bitmap(source);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException or UnauthorizedAccessException)
        {
            error = "文件不是可读取的 JPG 或 PNG 图片，或者文件已经损坏。";
            image?.Dispose();
            image = null;
            return false;
        }
    }

    private void SetSwatch(string key, Color color)
    {
        var button = _colorButtons[key];
        button.BackColor = color;
        button.ForeColor = color.GetBrightness() < 0.52F ? Color.White : Color.Black;
        button.FlatAppearance.BorderColor = UiDesign.Blend(color, button.ForeColor, 0.35F);
        button.Text = ToHex(color);
    }

    private void RefreshPreview()
    {
        if (!_colorButtons.ContainsKey("accent"))
        {
            return;
        }

        _preview.ThemeName = string.IsNullOrWhiteSpace(_nameBox.Text) ? "我的主题" : _nameBox.Text.Trim();
        _preview.AccentColor = _colorButtons["accent"].BackColor;
        _preview.SurfaceColor = _colorButtons["surface"].BackColor;
        _preview.InkColor = _colorButtons["ink"].BackColor;
        _preview.IsDark = _modeBox.SelectedIndex != 1;
        _preview.Contrast = (int)_contrastBox.Value;
        var codeIndex = Math.Clamp(_codeThemeBox.SelectedIndex, 0, CodeThemes.Length - 1);
        _preview.CodeThemeId = CodeThemes[codeIndex].Id;
        _preview.SetBackgroundImage(_backgroundImagePath);
        _preview.Invalidate();
    }

    private void SaveTheme()
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请填写主题名称。", "自定义主题", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _nameBox.Focus();
            return;
        }
        if (!string.IsNullOrWhiteSpace(_backgroundImagePath) && !File.Exists(_backgroundImagePath))
        {
            MessageBox.Show(this, "选择的背景照片已不存在，请重新选择。", "自定义主题", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_backgroundImagePath))
        {
            if (!TryLoadValidatedBackground(_backgroundImagePath, out var validatedImage, out var validationError))
            {
                MessageBox.Show(this, validationError, "无法使用这张照片", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            validatedImage?.Dispose();
        }

        var codeIndex = Math.Clamp(_codeThemeBox.SelectedIndex, 0, CodeThemes.Length - 1);
        Theme = new CustomCodexTheme
        {
            Name = name,
            IsDark = _modeBox.SelectedIndex != 1,
            CodeThemeId = CodeThemes[codeIndex].Id,
            AccentColor = ToHex(_colorButtons["accent"].BackColor),
            SurfaceColor = ToHex(_colorButtons["surface"].BackColor),
            InkColor = ToHex(_colorButtons["ink"].BackColor),
            BackgroundImagePath = _backgroundImagePath,
            Contrast = (int)_contrastBox.Value
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    internal static Color ParseColor(string? value, Color fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }
}

internal sealed class CodexThemePreviewControl : Control
{
    private Image? _backgroundImage;
    private string? _backgroundImagePath;
    private Image? _staticPreviewImage;
    private string? _staticPreviewImagePath;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ThemeName { get; set; } = "主题预览";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = Color.FromArgb(77, 184, 146);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SurfaceColor { get; set; } = Color.FromArgb(13, 26, 22);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color InkColor { get; set; } = Color.FromArgb(232, 245, 238);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsDark { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Contrast { get; set; } = 92;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string CodeThemeId { get; set; } = "tokyo-night";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float FocusX { get; set; } = 0.5F;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float FocusY { get; set; } = 0.5F;

    public CodexThemePreviewControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        AccessibleName = "Codex 界面预览";
    }

    internal static Size FitSixteenByNine(int maxWidth, int maxHeight)
    {
        maxWidth = Math.Max(1, maxWidth);
        maxHeight = Math.Max(1, maxHeight);
        var width = maxWidth;
        var height = Math.Max(1, (int)Math.Round(width * 9D / 16D));
        if (height > maxHeight)
        {
            height = maxHeight;
            width = Math.Max(1, Math.Min(maxWidth, (int)Math.Round(height * 16D / 9D)));
        }
        return new Size(width, height);
    }

    public void SetBackgroundImage(string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        if (_staticPreviewImage == null &&
            string.Equals(_backgroundImagePath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _staticPreviewImage?.Dispose();
        _staticPreviewImage = null;
        _staticPreviewImagePath = null;
        _backgroundImage?.Dispose();
        _backgroundImage = null;
        _backgroundImagePath = normalized;
        if (normalized != null && File.Exists(normalized))
        {
            try
            {
                using var stream = File.Open(normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var source = Image.FromStream(stream);
                _backgroundImage = new Bitmap(source);
            }
            catch
            {
                _backgroundImage = null;
            }
        }
        Invalidate();
    }

    public void SetStaticPreviewImage(string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        if (_backgroundImage == null &&
            string.Equals(_staticPreviewImagePath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _backgroundImage?.Dispose();
        _backgroundImage = null;
        _backgroundImagePath = null;
        _staticPreviewImage?.Dispose();
        _staticPreviewImage = null;
        _staticPreviewImagePath = normalized;
        if (normalized != null && File.Exists(normalized))
        {
            try
            {
                using var stream = File.Open(normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var source = Image.FromStream(stream);
                _staticPreviewImage = new Bitmap(source);
            }
            catch
            {
                _staticPreviewImage = null;
            }
        }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _backgroundImage?.Dispose();
            _backgroundImage = null;
            _staticPreviewImage?.Dispose();
            _staticPreviewImage = null;
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = ClientRectangle;
        if (bounds.Width < 20 || bounds.Height < 20)
        {
            return;
        }

        var viewport = Rectangle.Inflate(bounds, -1, -1);
        using var viewportPath = RoundedPath(viewport, 10);
        var clipState = g.Save();
        g.SetClip(viewportPath);
        if (_staticPreviewImage != null)
        {
            using var letterbox = new SolidBrush(UiDesign.Blend(
                SurfaceColor,
                IsDark ? Color.Black : Color.White,
                0.08F));
            g.FillRectangle(letterbox, viewport);
            DrawImageContain(g, _staticPreviewImage, viewport);
            g.Restore(clipState);
            using var staticBorder = new Pen(Color.FromArgb(160, AccentColor));
            g.DrawPath(staticBorder, viewportPath);
            return;
        }
        using (var surface = new SolidBrush(SurfaceColor))
        {
            g.FillRectangle(surface, viewport);
        }
        if (_backgroundImage != null)
        {
            DrawImageCover(g, _backgroundImage, viewport, FocusX, FocusY);
            var washAlpha = IsDark
                ? Math.Clamp(68 + (Contrast - 70) * 2, 68, 132)
                : Math.Clamp(102 + (Contrast - 70), 102, 140);
            using var wash = new SolidBrush(Color.FromArgb(washAlpha, SurfaceColor));
            g.FillRectangle(wash, viewport);
        }
        g.Restore(clipState);

        const float designWidth = 960F;
        const float designHeight = 540F;
        var scale = Math.Min(viewport.Width / designWidth, viewport.Height / designHeight);
        var offsetX = viewport.Left + (viewport.Width - designWidth * scale) / 2F;
        var offsetY = viewport.Top + (viewport.Height - designHeight * scale) / 2F;
        var designState = g.Save();
        g.SetClip(viewportPath);
        g.TranslateTransform(offsetX, offsetY);
        g.ScaleTransform(scale, scale);

        var panel = new Rectangle(0, 0, (int)designWidth, (int)designHeight);
        var chrome = UiDesign.Blend(SurfaceColor, IsDark ? Color.Black : Color.White, IsDark ? 0.2F : 0.35F);
        var sidebar = UiDesign.Blend(SurfaceColor, IsDark ? Color.Black : AccentColor, IsDark ? 0.28F : 0.10F);
        using (var chromeBrush = new SolidBrush(Color.FromArgb(_backgroundImage == null ? 255 : 224, chrome)))
        {
            g.FillRectangle(chromeBrush, panel.Left, panel.Top, panel.Width, 44);
        }
        using (var sidebarBrush = new SolidBrush(Color.FromArgb(_backgroundImage == null ? 255 : 224, sidebar)))
        {
            g.FillRectangle(sidebarBrush, panel.Left, panel.Top + 45, 210, panel.Height - 45);
        }

        using var accentBrush = new SolidBrush(AccentColor);
        using var codeAccentBrush = new SolidBrush(ResolveCodeAccent(CodeThemeId, AccentColor));
        using var inkBrush = new SolidBrush(InkColor);
        using var mutedBrush = new SolidBrush(Color.FromArgb(190, InkColor));
        using var titleFont = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular, GraphicsUnit.Pixel);
        using var smallFont = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular, GraphicsUnit.Pixel);
        g.FillEllipse(accentBrush, 18, 14, 16, 16);
        g.DrawString("Codex", titleFont, inkBrush, 44, 9);
        g.DrawString(ThemeName, bodyFont, mutedBrush, 230, 13);

        const int navY = 78;
        for (var i = 0; i < 7; i++)
        {
            var item = new Rectangle(18, navY + i * 42, 174, 30);
            if (i == 0)
            {
                using var selected = new SolidBrush(Color.FromArgb(72, AccentColor));
                g.FillRectangle(selected, item);
            }
            g.FillEllipse(i == 0 ? accentBrush : mutedBrush, item.Left + 8, item.Top + 9, 10, 10);
            g.FillRectangle(i == 0 ? accentBrush : mutedBrush, item.Left + 30, item.Top + 10, 112, 7);
        }

        const int contentLeft = 252;
        const int contentWidth = 660;
        g.DrawString("新建任务", titleFont, inkBrush, contentLeft, 70);
        g.DrawString("在 Codex 中继续你的工作", smallFont, mutedBrush, contentLeft, 98);
        var bubbleOne = new Rectangle(contentLeft, 130, 520, 76);
        var bubbleTwo = new Rectangle(contentLeft + 112, 232, 548, 112);
        using var bubbleOneBrush = new SolidBrush(Color.FromArgb(224, UiDesign.Blend(SurfaceColor, AccentColor, 0.10F)));
        using var bubbleTwoBrush = new SolidBrush(Color.FromArgb(224, UiDesign.Blend(SurfaceColor, InkColor, IsDark ? 0.08F : 0.04F)));
        g.FillRectangle(bubbleOneBrush, bubbleOne);
        g.FillRectangle(bubbleTwoBrush, bubbleTwo);
        DrawLines(g, mutedBrush, bubbleOne, 3);
        DrawLines(g, codeAccentBrush, bubbleTwo, 4);

        var composer = new Rectangle(contentLeft, panel.Bottom - 82, contentWidth, 58);
        using var composerBrush = new SolidBrush(Color.FromArgb(230, UiDesign.Blend(SurfaceColor, IsDark ? Color.White : Color.Black, IsDark ? 0.08F : 0.035F)));
        g.FillRectangle(composerBrush, composer);
        g.FillEllipse(accentBrush, composer.Right - 46, composer.Top + 10, 38, 38);
        g.DrawString("输入消息", bodyFont, mutedBrush, composer.Left + 18, composer.Top + 18);
        g.Restore(designState);

        using var border = new Pen(Color.FromArgb(140, AccentColor));
        g.DrawPath(border, viewportPath);
    }

    private static void DrawImageCover(
        Graphics graphics,
        Image image,
        Rectangle bounds,
        float focusX,
        float focusY)
    {
        var scale = Math.Max(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
        var width = image.Width * scale;
        var height = image.Height * scale;
        focusX = Math.Clamp(focusX, 0F, 1F);
        focusY = Math.Clamp(focusY, 0F, 1F);
        var destination = new RectangleF(
            bounds.Left - (width - bounds.Width) * focusX,
            bounds.Top - (height - bounds.Height) * focusY,
            width,
            height);
        graphics.DrawImage(image, destination);
    }

    private static void DrawImageContain(Graphics graphics, Image image, Rectangle bounds)
    {
        var scale = Math.Min(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
        var width = image.Width * scale;
        var height = image.Height * scale;
        var destination = new RectangleF(
            bounds.Left + (bounds.Width - width) / 2F,
            bounds.Top + (bounds.Height - height) / 2F,
            width,
            height);
        graphics.DrawImage(image, destination);
    }

    private static Color ResolveCodeAccent(string? codeThemeId, Color fallback) =>
        codeThemeId?.ToLowerInvariant() switch
        {
            "everforest" => Color.FromArgb(131, 192, 146),
            "rose-pine" => Color.FromArgb(235, 188, 186),
            "gruvbox" => Color.FromArgb(250, 189, 47),
            "night-owl" => Color.FromArgb(130, 170, 255),
            "matrix" => Color.FromArgb(61, 255, 126),
            "vscode-plus" => Color.FromArgb(78, 201, 176),
            "github" => Color.FromArgb(88, 166, 255),
            "tokyo-night" => Color.FromArgb(122, 162, 247),
            _ => fallback
        };

    private static void DrawLines(Graphics graphics, Brush brush, Rectangle bounds, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var width = Math.Max(40, bounds.Width - 28 - (i == count - 1 ? bounds.Width / 3 : 0));
            graphics.FillRectangle(brush, bounds.Left + 14, bounds.Top + 13 + i * 13, width, 4);
        }
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
