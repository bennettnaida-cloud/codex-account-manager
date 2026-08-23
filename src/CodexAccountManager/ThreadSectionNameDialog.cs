namespace CodexAccountManager;

internal sealed class ThreadSectionNameDialog : Form
{
    internal const int MaximumNameLength = 80;

    private readonly HashSet<string> _blockedNames;
    private readonly ThemePalette _palette;
    private readonly Label _heading = new();
    private readonly Label _description = new();
    private readonly RoundedPanel _card = new();
    private readonly Label _nameLabel = new();
    private readonly TextBox _nameBox = new();
    private readonly ModernInputShell _nameShell;
    private readonly Label _validationLabel = new();
    private readonly ModernButton _confirmButton = new();
    private readonly ModernButton _cancelButton = new();
    private string? _confirmedName;

    public ThreadSectionNameDialog(
        string title,
        string confirmButtonText,
        string? initialName,
        IEnumerable<string> existingNames,
        ThemePalette palette)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("对话框标题不能为空。", nameof(title));
        }
        if (string.IsNullOrWhiteSpace(confirmButtonText))
        {
            throw new ArgumentException("确认按钮文字不能为空。", nameof(confirmButtonText));
        }
        ArgumentNullException.ThrowIfNull(existingNames);
        ArgumentNullException.ThrowIfNull(palette);

        _palette = palette;
        var normalizedInitialName = NormalizeName(initialName);
        _blockedNames = BuildBlockedNames(existingNames, normalizedInitialName);

        Text = title.Trim();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 338);
        MinimumSize = new Size(656, 377);
        Font = new Font("Microsoft YaHei UI", 9.25F);
        DoubleBuffered = true;
        ThemeStyler.ApplyDialog(this, palette);

        _heading.Text = Text;
        _heading.SetBounds(28, 18, 584, 42);
        _heading.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _heading.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        _heading.TextAlign = ContentAlignment.MiddleLeft;
        _heading.AutoEllipsis = true;
        _heading.UseCompatibleTextRendering = true;
        _heading.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_heading, palette);
        Controls.Add(_heading);

        _description.Text = "目录名称会显示在 Codex 左侧栏，也会用于聊天记录页的折叠分组。";
        _description.SetBounds(28, 62, 584, 30);
        _description.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _description.Font = new Font(Font.FontFamily, 8.6F);
        _description.TextAlign = ContentAlignment.MiddleLeft;
        _description.AutoEllipsis = true;
        _description.UseCompatibleTextRendering = true;
        _description.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_description, palette, true);
        Controls.Add(_description);

        _card.SetBounds(20, 104, 600, 142);
        _card.Name = "ThreadSectionNameCard";
        _card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _card.Radius = 16;
        _card.BackColor = palette.SurfaceColor;
        _card.BorderColor = palette.BorderColor;
        _card.UseGradient = true;
        _card.GradientColor = UiDesign.Blend(palette.SurfaceColor, palette.PrimaryColor, 0.025F);
        _card.AccentColor = palette.AccentColor;
        _card.AccentWidth = 3;
        _card.ShadowColor = Color.FromArgb(22, palette.ShadowColor);
        Controls.Add(_card);

        _nameLabel.Text = "聊天目录名称";
        _nameLabel.SetBounds(20, 10, 560, 28);
        _nameLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _nameLabel.Font = new Font(Font.FontFamily, 8.8F, FontStyle.Bold);
        _nameLabel.TextAlign = ContentAlignment.MiddleLeft;
        _nameLabel.UseCompatibleTextRendering = true;
        _nameLabel.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_nameLabel, palette);
        _card.Controls.Add(_nameLabel);

        _nameBox.Text = normalizedInitialName;
        _nameBox.Font = new Font(Font.FontFamily, 9.5F);
        _nameBox.MaxLength = MaximumNameLength;
        _nameBox.PlaceholderText = "输入目录名称";
        _nameBox.AccessibleName = "聊天目录名称";
        _nameShell = new ModernInputShell(_nameBox)
        {
            Name = "ThreadSectionNameInputShell",
            Left = 20,
            Top = 42,
            Width = 560,
            Height = 46,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Radius = 9
        };
        _nameShell.ApplyPalette(palette);
        _card.Controls.Add(_nameShell);

        _validationLabel.SetBounds(20, 96, 560, 30);
        _validationLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _validationLabel.Font = new Font(Font.FontFamily, 8.2F);
        _validationLabel.TextAlign = ContentAlignment.MiddleLeft;
        _validationLabel.AutoEllipsis = true;
        _validationLabel.UseCompatibleTextRendering = true;
        _validationLabel.UseMnemonic = false;
        _card.Controls.Add(_validationLabel);

        _confirmButton.Text = confirmButtonText.Trim();
        _confirmButton.SetBounds(344, 274, 132, 44);
        _confirmButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _confirmButton.AccessibleName = _confirmButton.Text;
        ThemeStyler.ApplyPrimaryButton(_confirmButton, palette);
        _confirmButton.Click += (_, _) => ConfirmName();
        Controls.Add(_confirmButton);

        _cancelButton.Text = "取消";
        _cancelButton.SetBounds(488, 274, 124, 44);
        _cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _cancelButton.DialogResult = DialogResult.Cancel;
        ThemeStyler.ApplySoftButton(_cancelButton, palette);
        Controls.Add(_cancelButton);

        AcceptButton = _confirmButton;
        CancelButton = _cancelButton;
        _nameBox.TextChanged += (_, _) => ShowCharacterCount();
        ShowCharacterCount();
        Shown += (_, _) => BeginInvoke(() =>
        {
            _nameBox.Focus();
            _nameBox.SelectAll();
        });
    }

    public string SectionName => _confirmedName ?? NormalizeName(_nameBox.Text);

    private void ConfirmName()
    {
        if (!TryValidateName(_nameBox.Text, _blockedNames, out var normalizedName, out var error))
        {
            _validationLabel.Text = error;
            _validationLabel.ForeColor = _palette.DangerColor;
            _nameBox.Focus();
            _nameBox.SelectAll();
            return;
        }

        _confirmedName = normalizedName;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowCharacterCount()
    {
        var length = NormalizeName(_nameBox.Text).Length;
        _validationLabel.Text = $"最多 {MaximumNameLength} 个字符 · 当前 {length} 个";
        ThemeStyler.ApplyLabel(_validationLabel, _palette, true);
    }

    private static HashSet<string> BuildBlockedNames(
        IEnumerable<string> existingNames,
        string normalizedInitialName)
    {
        return existingNames
            .Select(NormalizeName)
            .Where(name => name.Length > 0)
            // Renaming a directory without changing its name must remain valid even when the
            // caller passes the complete section list, including the section being edited.
            .Where(name => !name.Equals(normalizedInitialName, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryValidateName(
        string? candidate,
        IReadOnlySet<string> blockedNames,
        out string normalizedName,
        out string error)
    {
        normalizedName = NormalizeName(candidate);
        if (normalizedName.Length == 0)
        {
            error = "目录名称不能为空。";
            return false;
        }
        if (normalizedName.Length > MaximumNameLength)
        {
            error = $"目录名称不能超过 {MaximumNameLength} 个字符。";
            return false;
        }
        if (blockedNames.Contains(normalizedName))
        {
            error = "已经存在同名目录，请换一个名称。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string NormalizeName(string? value) => value?.Trim() ?? string.Empty;

    internal static void ValidateValidation()
    {
        var blocked = BuildBlockedNames(
            new[] { "已有目录", "  Case Sensitive  ", "当前名称" },
            "当前名称");

        AssertInvalid(null, blocked, "null");
        AssertInvalid(string.Empty, blocked, "empty");
        AssertInvalid("   ", blocked, "whitespace");
        AssertInvalid("已有目录", blocked, "duplicate");
        AssertInvalid("  case sensitive ", blocked, "duplicate-case-insensitive");
        AssertInvalid(new string('长', MaximumNameLength + 1), blocked, "too-long");
        AssertValid("  新目录  ", blocked, "新目录", "trim");
        AssertValid(new string('好', MaximumNameLength), blocked, new string('好', MaximumNameLength), "max-length");
        AssertValid("当前名称", blocked, "当前名称", "unchanged-rename");

        static void AssertInvalid(
            string? value,
            IReadOnlySet<string> names,
            string scenario)
        {
            if (TryValidateName(value, names, out _, out var error) || string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(
                    $"Thread section name validation should reject scenario: {scenario}.");
            }
        }

        static void AssertValid(
            string? value,
            IReadOnlySet<string> names,
            string expected,
            string scenario)
        {
            if (!TryValidateName(value, names, out var normalized, out var error) ||
                !normalized.Equals(expected, StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException(
                    $"Thread section name validation should accept scenario: {scenario}.");
            }
        }
    }

    internal static void ValidateLayout()
    {
        var palette = new ThemeService(Path.GetTempPath()).GetPalette(ThemeMode.Light);
        foreach (var scale in new[] { 1F, 1.25F, 1.5F, 2F })
        {
            using var dialog = new ThreadSectionNameDialog(
                "重命名聊天目录",
                "保存名称",
                "一个用于布局验证的聊天目录",
                new[] { "已有目录", "另一个目录" },
                palette);
            if (scale > 1F)
            {
                dialog.Scale(new SizeF(scale, scale));
            }

            dialog.PerformLayout();
            dialog._card.PerformLayout();
            dialog._nameShell.PerformLayout();

            var failures = new List<string>();
            if (dialog._heading.Parent != dialog) failures.Add("heading-parent");
            if (dialog._description.Parent != dialog) failures.Add("description-parent");
            if (dialog._card.Parent != dialog) failures.Add("card-parent");
            if (dialog._nameLabel.Parent != dialog._card) failures.Add("name-label-parent");
            if (dialog._nameShell.Parent != dialog._card) failures.Add("name-shell-parent");
            if (dialog._nameBox.Parent != dialog._nameShell) failures.Add("name-box-parent");
            if (dialog._validationLabel.Parent != dialog._card) failures.Add("validation-parent");
            if (!dialog.ClientRectangle.Contains(dialog._heading.Bounds)) failures.Add("heading-outside-dialog");
            if (!dialog.ClientRectangle.Contains(dialog._description.Bounds)) failures.Add("description-outside-dialog");
            if (!dialog.ClientRectangle.Contains(dialog._card.Bounds)) failures.Add("card-outside-dialog");
            if (!dialog.ClientRectangle.Contains(dialog._confirmButton.Bounds)) failures.Add("confirm-outside-dialog");
            if (!dialog.ClientRectangle.Contains(dialog._cancelButton.Bounds)) failures.Add("cancel-outside-dialog");
            if (!dialog._card.ClientRectangle.Contains(dialog._nameLabel.Bounds)) failures.Add("name-label-outside-card");
            if (!dialog._card.ClientRectangle.Contains(dialog._nameShell.Bounds)) failures.Add("name-shell-outside-card");
            if (!dialog._card.ClientRectangle.Contains(dialog._validationLabel.Bounds)) failures.Add("validation-outside-card");
            if (dialog._nameLabel.Bounds.IntersectsWith(dialog._nameShell.Bounds)) failures.Add("label-input-overlap");
            if (dialog._nameShell.Bounds.IntersectsWith(dialog._validationLabel.Bounds)) failures.Add("input-validation-overlap");
            if (dialog._confirmButton.Bounds.IntersectsWith(dialog._cancelButton.Bounds)) failures.Add("button-overlap");
            if (!TextFits(dialog._heading)) failures.Add("heading-text-clipped");
            if (!TextFits(dialog._description)) failures.Add("description-text-clipped");
            if (!TextFits(dialog._nameLabel)) failures.Add("name-label-clipped");
            if (!TextFits(dialog._validationLabel)) failures.Add("validation-text-clipped");
            if (!ButtonTextFits(dialog._confirmButton)) failures.Add("confirm-text-clipped");
            if (!ButtonTextFits(dialog._cancelButton)) failures.Add("cancel-text-clipped");
            if (dialog.AcceptButton != dialog._confirmButton) failures.Add("accept-button");
            if (dialog.CancelButton != dialog._cancelButton) failures.Add("cancel-button");
            if (dialog._nameBox.MaxLength != MaximumNameLength) failures.Add("max-length");
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Thread section name dialog layout failed at scale {scale:0.##}: " +
                    string.Join(", ", failures) + ".");
            }
        }

        static bool TextFits(Label label)
        {
            var measured = TextRenderer.MeasureText(
                label.Text,
                label.Font,
                Size.Empty,
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix);
            return measured.Width <= label.ClientSize.Width && measured.Height <= label.ClientSize.Height;
        }

        static bool ButtonTextFits(Button button)
        {
            var measured = TextRenderer.MeasureText(
                button.Text,
                button.Font,
                Size.Empty,
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix);
            return measured.Width <= Math.Max(1, button.ClientSize.Width - 24) &&
                   measured.Height <= button.ClientSize.Height;
        }
    }
}
