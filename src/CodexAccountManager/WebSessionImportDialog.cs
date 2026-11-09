using System.Diagnostics;

namespace CodexAccountManager;

internal sealed class WebSessionImportDialog : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, MaxLength = 80 };
    private readonly TextBox _json = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true, MaxLength = 128 * 1024
    };
    internal AccountImportEntry? Entry { get; private set; }
    internal string AccountName => _name.Text.Trim();

    internal WebSessionImportDialog(ThemePalette palette, AccountRecord? existing = null)
    {
        Text = existing == null ? "导入网页版 Session" : "编辑 / 更新网页版 Session";
        Font = new Font("Microsoft YaHei UI", 9.25F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 540);
        MinimumSize = new Size(620, 460);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ThemeStyler.ApplyDialog(this, palette);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        var note = new Label { AutoSize = true, Dock = DockStyle.Fill,
            Text = "先在浏览器登录所需 ChatGPT 账号，再打开下面的 Session 网页，复制完整 JSON 到此处。\nSession 到期后需重新导入；启动前会在线获取该账号的模型。" };
        var link = new LinkLabel { AutoSize = true, Text = "打开 https://chatgpt.com/api/auth/session", Margin = new Padding(3, 10, 3, 12) };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://chatgpt.com/api/auth/session") { UseShellExecute = true }); }
            catch { MessageBox.Show(this, "请在已登录 ChatGPT 的浏览器中手动打开 https://chatgpt.com/api/auth/session。"); }
        };
        var nameRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
        nameRow.ColumnStyles.Add(new(SizeType.AutoSize));
        nameRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        nameRow.Controls.Add(new Label { AutoSize = true, Text = "账号名称", Margin = new Padding(3, 6, 12, 3) });
        _name.Text = existing?.Name ?? "";
        _name.PlaceholderText = "留空时使用 Session 中的邮箱";
        nameRow.Controls.Add(_name);
        var caption = new Label { AutoSize = true, Text = existing == null
            ? "粘贴完整 JSON（不需要复制 Cookie）"
            : "粘贴新的 JSON；留空则仅保存账号名称。仅允许更新同一 ChatGPT 工作区。" };
        _json.PlaceholderText = "{ \"user\": { ... }, \"expires\": \"...\", \"accessToken\": \"...\" }";
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel, Height = 34 };
        var save = new Button { Text = existing == null ? "导入 Session" : "保存", AutoSize = true, Height = 34 };
        save.Click += (_, _) =>
        {
            Entry = null;
            if (existing != null && string.IsNullOrWhiteSpace(_json.Text))
            {
                if (AccountName.Length == 0) { MessageBox.Show(this, "账号名称不能为空。"); return; }
                DialogResult = DialogResult.OK;
                return;
            }
            var batch = AccountFileImport.Parse(_json.Text.Trim().TrimStart('\uFEFF'), "网页版 Session");
            if (batch.Problems.Count > 0 || batch.Entries.Count != 1 || batch.Entries[0].AuthKind != AccountAuthKind.WebSession)
            {
                MessageBox.Show(this, batch.Problems.FirstOrDefault()?.Reason ?? "请粘贴一个完整的 api/auth/session JSON 对象。",
                    "无法导入 Session", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Entry = batch.Entries[0];
            if (AccountName.Length > 0) Entry = Entry with { Name = AccountName };
            else _name.Text = Entry.Name;
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(note, 0, 0);
        layout.Controls.Add(link, 0, 1);
        layout.Controls.Add(nameRow, 0, 2);
        layout.Controls.Add(caption, 0, 3);
        layout.Controls.Add(_json, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        CancelButton = cancel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _json.Clear();
        base.Dispose(disposing);
    }
}
