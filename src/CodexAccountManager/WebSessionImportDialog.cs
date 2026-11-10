namespace CodexAccountManager;

internal sealed class WebSessionImportDialog : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, MaxLength = 80 };
    internal AccountImportEntry? Entry { get; private set; }
    internal string AccountName => _name.Text.Trim();

    internal WebSessionImportDialog(ThemePalette palette, AccountRecord? existing = null)
    {
        Text = existing == null ? "导入网页版 Session" : "编辑 / 更新网页版 Session";
        Font = new Font("Microsoft YaHei UI", 9.25F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(620, 460);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ThemeStyler.ApplyDialog(this, palette);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        var nameRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
        nameRow.ColumnStyles.Add(new(SizeType.AutoSize));
        nameRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        nameRow.Controls.Add(new Label { AutoSize = true, Text = "账号名称", Margin = new Padding(3, 6, 12, 3) });
        _name.Text = existing?.Name ?? "";
        _name.PlaceholderText = "留空时使用 Session 中的邮箱";
        nameRow.Controls.Add(_name);
        var input = new WebSessionInput(existing != null) { Dock = DockStyle.Fill };
        // Keep actions outside the content table so wrapped labels cannot push them off-screen.
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Bottom, WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        var cancel = new Button { Name = "SessionCancel", Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel, MinimumSize = new Size(100, 40) };
        var save = new Button { Name = "SessionSave", Text = existing == null ? "导入 Session" : "保存", AutoSize = true, MinimumSize = new Size(150, 40) };
        ThemeStyler.ApplyPrimaryButton(save, palette);
        save.Click += (_, _) =>
        {
            Entry = null;
            if (existing != null && string.IsNullOrWhiteSpace(input.JsonBox.Text))
            {
                if (AccountName.Length == 0) { MessageBox.Show(this, "账号名称不能为空。"); return; }
                DialogResult = DialogResult.OK;
                return;
            }
            try { Entry = input.ParseEntry(AccountName); }
            catch (InvalidDataException ex)
            {
                MessageBox.Show(this, ex.Message, "无法导入 Session", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (AccountName.Length == 0) _name.Text = Entry.Name;
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(nameRow, 0, 0);
        layout.Controls.Add(input, 0, 1);
        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = cancel;
    }

    internal static void ValidateLayout(ThemePalette palette)
    {
        foreach (var scale in new[] { 1F, 1.5F, 2F })
        {
            using var dialog = new WebSessionImportDialog(palette);
            dialog.Scale(new SizeF(scale, scale));
            dialog.PerformLayout();
            var content = dialog.Controls.OfType<TableLayoutPanel>().Single();
            var footer = dialog.Controls.OfType<FlowLayoutPanel>().Single();
            content.PerformLayout();
            footer.PerformLayout();
            var input = content.Controls.OfType<WebSessionInput>().Single();
            input.PerformLayout();
            foreach (var button in footer.Controls.OfType<Button>())
            {
                if (!footer.ClientRectangle.Contains(button.Bounds) ||
                    !dialog.ClientRectangle.Contains(footer.Bounds) || content.Bottom > footer.Top)
                    throw new InvalidOperationException($"Session footer is outside the dialog at scale {scale}.");
            }
            if (input.JsonBox.Height < 80 || input.Right > content.ClientSize.Width)
                throw new InvalidOperationException($"Session JSON editor is clipped at scale {scale}.");
        }
    }
}
