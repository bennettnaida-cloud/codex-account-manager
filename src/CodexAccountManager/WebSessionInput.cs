namespace CodexAccountManager;

// Shared by the new-account form and the existing Session editor.
internal sealed class WebSessionInput : TableLayoutPanel
{
    internal const string SessionUrl = "https://chatgpt.com/api/auth/session";
    internal TextBox JsonBox { get; } = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true, MaxLength = 128 * 1024,
        AccessibleName = "网页版 Session JSON"
    };

    internal WebSessionInput(bool editing = false)
    {
        ColumnCount = 1;
        RowCount = 4;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var note = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false,
            Text = "复制下方地址，在已登录所需 ChatGPT 账号的浏览器中打开，将完整 JSON 粘贴到此处。导入不代表模型授权通过；若测试返回 401/403，请改用官方 ChatGPT 登录。"
        };
        var address = new TextBox
        {
            Name = "SessionAddress", ReadOnly = true, Dock = DockStyle.Fill,
            Text = SessionUrl, AccessibleName = "Session 网页地址（可选中复制）",
            Margin = new Padding(3, 8, 3, 8)
        };
        address.Enter += (_, _) => address.SelectAll();
        address.Click += (_, _) => address.SelectAll();
        var caption = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false,
            Text = editing ? "粘贴新的 JSON；留空仅保存名称，只能更新同一工作区。" : "粘贴完整 JSON（不需要复制 Cookie）"
        };
        JsonBox.PlaceholderText = "{ \"user\": { ... }, \"expires\": \"...\", \"accessToken\": \"...\" }";
        Controls.Add(note, 0, 0);
        Controls.Add(address, 0, 1);
        Controls.Add(caption, 0, 2);
        Controls.Add(JsonBox, 0, 3);
        SizeChanged += (_, _) =>
        {
            // Constrain wrapping so preferred text widths cannot widen the table.
            var width = Math.Max(1, ClientSize.Width - Padding.Horizontal - 6);
            note.MaximumSize = caption.MaximumSize = new Size(width, 0);
        };
    }

    internal AccountImportEntry ParseEntry(string accountName)
    {
        var batch = AccountFileImport.Parse(JsonBox.Text.Trim().TrimStart('\uFEFF'), "网页版 Session");
        if (batch.Problems.Count > 0 || batch.Entries.Count != 1 || batch.Entries[0].AuthKind != AccountAuthKind.WebSession)
            throw new InvalidDataException(batch.Problems.FirstOrDefault()?.Reason ?? "请粘贴一个完整的 api/auth/session JSON 对象。");
        var entry = batch.Entries[0];
        return string.IsNullOrWhiteSpace(accountName) ? entry : entry with { Name = accountName.Trim() };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) JsonBox.Clear();
        base.Dispose(disposing);
    }
}
