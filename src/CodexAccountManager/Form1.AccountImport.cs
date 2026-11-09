namespace CodexAccountManager;

public partial class Form1
{
    private void ImportWebSession(AccountRecord? existing = null)
    {
        using var dialog = new WebSessionImportDialog(_palette, existing);
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                if (existing != null)
                {
                    _store.UpdateWebSession(existing, dialog.AccountName, dialog.Entry);
                    _statusCache.Remove(existing.Name);
                    if (dialog.Entry != null) InvalidateQuotaRuntimeState(existing);
                    _selectedAccountName = dialog.AccountName;
                    _statusBox.Text = "网页版 Session 账号已保存；下次启动先在线获取模型，Session 到期后需重新导入。";
                }
                else
                {
                    var result = _store.ImportAccounts([dialog.Entry!]);
                    _statusBox.Text = result.Added == 0 ? "此 Session 已导入，已跳过重复凭据。"
                        : "网页版 Session 已导入；启动前会在线获取模型，Session 到期后需重新导入。";
                }
                LoadAccounts();
                return;
            }
            catch (InvalidDataException ex)
            { MessageBox.Show(this, ex.Message, "Session 未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            { MessageBox.Show(this, "Session 未保存。请检查账号名称、目录权限与磁盘空间。", "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    private void ImportAccountFiles()
    {
        using var picker = new OpenFileDialog
        {
            Title = "导入 sub2api / CPA / Codex 账号文件",
            Filter = "账号导出文件 (*.json;*.zip)|*.json;*.zip|JSON 文件 (*.json)|*.json|ZIP 压缩包 (*.zip)|*.zip",
            Multiselect = true, CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var batch = AccountFileImport.ReadFiles(picker.FileNames);
            using var preview = new Form
            {
                Text = "文件导入预览", Width = 840, Height = 570, MinimumSize = new Size(640, 420),
                StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false
            };
            ThemeStyler.ApplyDialog(preview, _palette);
            var note = new Label { Dock = DockStyle.Top, Height = 74, Padding = new Padding(14),
                Text = $"识别到 {batch.Entries.Count} 个账号，{batch.Problems.Count} 项无法导入。重复凭据将跳过，同名账号自动重命名。\n支持 sub2api、CPA Codex、auth.json 和网页版 Session；Session 到期需重新导入。" };
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                GridLines = true, HideSelection = false };
            list.Columns.Add("账号 / 来源", 290);
            list.Columns.Add("识别结果", 480);
            foreach (var entry in batch.Entries)
                list.Items.Add(new ListViewItem(new[] { entry.Name, entry.AuthKind switch
                {
                    AccountAuthKind.OfficialOAuth => "ChatGPT OAuth（启动时验证 / 刷新）",
                    AccountAuthKind.WebSession => "网页版 Session（到期需重新导入）",
                    AccountAuthKind.CompatibleApi => "兼容 API", _ => "PAT"
                } }));
            foreach (var problem in batch.Problems)
                list.Items.Add(new ListViewItem(new[] { problem.Source, "跳过：" + problem.Reason }));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62,
                FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 100, Height = 34 };
            var import = new Button { Text = "导入有效账号", DialogResult = DialogResult.OK, Width = 140, Height = 34,
                Enabled = batch.Entries.Count > 0 };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(import);
            preview.Controls.Add(list);
            preview.Controls.Add(note);
            preview.Controls.Add(buttons);
            preview.AcceptButton = import;
            preview.CancelButton = cancel;
            if (preview.ShowDialog(this) != DialogResult.OK) return;
            var result = _store.ImportAccounts(batch.Entries);
            LoadAccounts();
            _statusBox.Text = $"文件导入完成：新增 {result.Added} 个，重复 {result.Duplicates} 个，跳过无效记录 {batch.Problems.Count} 项。启动时会先查询账号最新模型。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Never display credential JSON or include provider response bodies in diagnostics.
            MessageBox.Show(this, "导入未完成。请检查文件格式、账号数据目录权限与磁盘空间；现有账号未被覆盖。",
                "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
