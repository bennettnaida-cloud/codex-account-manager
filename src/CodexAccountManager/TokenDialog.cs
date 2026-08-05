namespace CodexAccountManager;

public sealed class TokenDialog : Form
{
    private readonly TextBox _tokenBox = new();
    private readonly RoundedPanel _card = new();
    private readonly Label _accountLabel = new();
    private readonly Label _title = new();
    private readonly PillLabel _safetyBadge = new();
    private readonly Label _tokenLabel = new();
    private readonly ModernInputShell _tokenShell;
    private readonly Label _note = new();

    public TokenDialog(string accountName, string codexHome, ThemePalette palette)
    {
        Text = "更新 Token";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(920, 500);
        MinimumSize = new Size(940, 520);
        Font = new Font("Microsoft YaHei UI", 9.25F);
        DoubleBuffered = true;
        ThemeStyler.ApplyDialog(this, palette);

        _accountLabel.Text = $"账号  ·  {accountName}";
        _accountLabel.SetBounds(28, 18, 630, 28);
        _accountLabel.Font = new Font(Font.FontFamily, 8.7F, FontStyle.Bold);
        _accountLabel.AutoEllipsis = false;
        _accountLabel.TextAlign = ContentAlignment.MiddleLeft;
        _accountLabel.UseCompatibleTextRendering = true;
        _accountLabel.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_accountLabel, palette, true);
        Controls.Add(_accountLabel);

        _title.Text = "更新 Access Token";
        _title.SetBounds(28, 48, 620, 48);
        _title.AutoEllipsis = false;
        _title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _title.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.UseCompatibleTextRendering = true;
        _title.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_title, palette);
        Controls.Add(_title);

        _safetyBadge.Text = "仅写入当前账号目录";
        _safetyBadge.SetBounds(696, 40, 196, 38);
        _safetyBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _safetyBadge.Font = new Font(Font.FontFamily, 8.2F, FontStyle.Bold);
        _safetyBadge.TextAlign = ContentAlignment.MiddleCenter;
        _safetyBadge.FillColor = Color.FromArgb(34, palette.AccentColor);
        _safetyBadge.StrokeColor = Color.FromArgb(90, palette.AccentColor);
        _safetyBadge.ForeColor = palette.PrimaryColor;
        Controls.Add(_safetyBadge);

        _card.SetBounds(16, 108, 888, 300);
        _card.Name = "TokenDialogCredentialCard";
        _card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _card.Radius = 18;
        _card.BackColor = palette.SurfaceColor;
        _card.BorderColor = palette.BorderColor;
        _card.UseGradient = true;
        _card.GradientColor = UiDesign.Blend(palette.SurfaceColor, palette.PrimaryColor, 0.025F);
        _card.AccentColor = palette.AccentColor;
        _card.AccentWidth = 3;
        _card.ShadowColor = Color.FromArgb(26, palette.ShadowColor);
        Controls.Add(_card);

        _tokenLabel.Text = "新的 Access Token";
        _tokenLabel.SetBounds(20, 12, 360, 32);
        _tokenLabel.Font = new Font(Font.FontFamily, 8.8F, FontStyle.Bold);
        _tokenLabel.TextAlign = ContentAlignment.MiddleLeft;
        _tokenLabel.UseCompatibleTextRendering = true;
        _tokenLabel.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_tokenLabel, palette);
        _card.Controls.Add(_tokenLabel);

        _tokenBox.UseSystemPasswordChar = true;
        _tokenBox.Font = new Font(Font.FontFamily, 9.5F);
        _tokenBox.PlaceholderText = "粘贴新的 Codex Access Token";
        _tokenBox.AccessibleName = "新的 Codex Access Token";
        _tokenShell = new ModernInputShell(_tokenBox)
        {
            Name = "TokenDialogTokenInputShell",
            Left = 20,
            Top = 48,
            Width = 848,
            Height = 48,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Radius = 9
        };
        _tokenShell.ApplyPalette(palette);
        _card.Controls.Add(_tokenShell);

        _note.Text = $"凭据目录\n{codexHome}\n\n只填写 Codex Access Token 本体；不要填写 sk- API Key、JSON、refresh_token 或 session_token。";
        _note.SetBounds(20, 112, 848, 164);
        _note.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _note.Font = new Font(Font.FontFamily, 8.6F);
        _note.TextAlign = ContentAlignment.TopLeft;
        _note.UseCompatibleTextRendering = true;
        _note.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_note, palette, true);
        _card.Controls.Add(_note);

        var ok = new ModernButton { Text = "确认更新", Left = 652, Top = 438, Width = 116, Height = 44, DialogResult = DialogResult.OK };
        ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        ThemeStyler.ApplyPrimaryButton(ok, palette);
        var cancel = new ModernButton { Text = "取消", Left = 780, Top = 438, Width = 112, Height = 44, DialogResult = DialogResult.Cancel };
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        ThemeStyler.ApplySoftButton(cancel, palette);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => BeginInvoke(() =>
        {
            _tokenBox.Focus();
            _tokenBox.Select(0, 0);
        });
    }

    public string AccessTokenValue => _tokenBox.Text.Trim();

    internal static void ValidateLayout()
    {
        var palette = new ThemeService(Path.GetTempPath()).GetPalette(ThemeMode.Light);
        foreach (var scale in new[] { 1F, 1.25F, 1.5F, 2F })
        {
            using var dialog = new TokenDialog(
                "layout@example.com",
                @"C:\Users\layout\.codex-accounts\layout@example.com",
                palette);
            if (scale > 1F)
            {
                dialog.Scale(new SizeF(scale, scale));
            }

            dialog.PerformLayout();
            dialog._card.PerformLayout();
            dialog._tokenShell.PerformLayout();

            var failures = new List<string>();
            if (dialog._card.Parent != dialog) failures.Add("card-parent");
            if (dialog._tokenLabel.Parent != dialog._card) failures.Add("label-parent");
            if (dialog._tokenShell.Parent != dialog._card) failures.Add("input-shell-parent");
            if (dialog._tokenBox.Parent != dialog._tokenShell) failures.Add("token-box-parent");
            if (dialog._note.Parent != dialog._card) failures.Add("note-parent");
            if (dialog.Controls.Contains(dialog._tokenShell)) failures.Add("input-shell-form-sibling");
            if (dialog.Controls.Contains(dialog._note)) failures.Add("note-form-sibling");
            if (!dialog._card.ClientRectangle.Contains(dialog._tokenLabel.Bounds)) failures.Add("label-outside-card");
            if (!dialog._card.ClientRectangle.Contains(dialog._tokenShell.Bounds)) failures.Add("input-outside-card");
            if (!dialog._card.ClientRectangle.Contains(dialog._note.Bounds)) failures.Add("note-outside-card");
            if (dialog._tokenShell.Bounds.IntersectsWith(dialog._note.Bounds)) failures.Add("input-note-overlap");
            if (string.IsNullOrWhiteSpace(dialog._tokenBox.PlaceholderText)) failures.Add("placeholder-missing");
            if (!TextFits(dialog._accountLabel)) failures.Add("account-text-clipped");
            if (!TextFits(dialog._title)) failures.Add("title-text-clipped");
            if (!TextFits(dialog._safetyBadge, horizontalPadding: 20)) failures.Add("safety-text-clipped");
            if (!TextFits(dialog._tokenLabel)) failures.Add("token-label-clipped");
            if (!MultilineTextFits(dialog._note, horizontalPadding: 6, verticalPadding: 6)) failures.Add("note-text-clipped");
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Token dialog layout failed at scale {scale:0.##}: {string.Join(", ", failures)}.");
            }
        }

        static bool TextFits(Label label, int horizontalPadding = 0)
        {
            var available = new Size(
                Math.Max(1, label.ClientSize.Width - horizontalPadding),
                Math.Max(1, label.ClientSize.Height));
            var measured = TextRenderer.MeasureText(
                label.Text,
                label.Font,
                Size.Empty,
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix);
            return measured.Width <= available.Width && measured.Height <= available.Height;
        }

        static bool MultilineTextFits(Label label, int horizontalPadding = 0, int verticalPadding = 0)
        {
            var available = new Size(
                Math.Max(1, label.ClientSize.Width - horizontalPadding),
                Math.Max(1, label.ClientSize.Height - verticalPadding));
            var measured = TextRenderer.MeasureText(
                label.Text,
                label.Font,
                new Size(available.Width, int.MaxValue),
                TextFormatFlags.WordBreak |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix);
            return measured.Width <= available.Width && measured.Height <= available.Height;
        }
    }
}
