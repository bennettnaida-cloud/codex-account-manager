using System.Diagnostics;

namespace CodexAccountManager;

// Shared by the new-account form and the existing Session editor.
internal sealed class WebSessionInput : TableLayoutPanel
{
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
            Text = "先在浏览器登录所需 ChatGPT 账号，打开下面的网页，将完整 JSON 粘贴到此处。Session 到期后需重新导入；启动前会在线获取模型。"
        };
        var link = new LinkLabel
        {
            AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false,
            Text = "打开 ChatGPT Session 网页", Margin = new Padding(3, 8, 3, 8)
        };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://chatgpt.com/api/auth/session") { UseShellExecute = true }); }
            catch { MessageBox.Show(FindForm(), "请在已登录 ChatGPT 的浏览器中打开 https://chatgpt.com/api/auth/session。"); }
        };
        var caption = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false,
            Text = editing ? "粘贴新的 JSON；留空仅保存名称，只能更新同一工作区。" : "粘贴完整 JSON（不需要复制 Cookie）"
        };
        JsonBox.PlaceholderText = "{ \"user\": { ... }, \"expires\": \"...\", \"accessToken\": \"...\" }";
        Controls.Add(note, 0, 0);
        Controls.Add(link, 0, 1);
        Controls.Add(caption, 0, 2);
        Controls.Add(JsonBox, 0, 3);
        SizeChanged += (_, _) =>
        {
            // Constrain wrapping so preferred text widths cannot widen the table.
            var width = Math.Max(1, ClientSize.Width - Padding.Horizontal - 6);
            note.MaximumSize = caption.MaximumSize = link.MaximumSize = new Size(width, 0);
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
