namespace CodexAccountManager;

public partial class Form1
{
    private async Task ImportAccountFilesAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = "导入 sub2api / CPA / Codex 账号文件",
            Filter = "账号导出文件 (*.json;*.zip)|*.json;*.zip|JSON 文件 (*.json)|*.json|ZIP 压缩包 (*.zip)|*.zip",
            Multiselect = true, CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var fileNames = picker.FileNames;
        try
        {
            AccountImportBatch batch;
            Cursor = Cursors.WaitCursor;
            SetButtonsEnabled(false);
            try
            {
                _statusBox.Text = "正在解析导入文件，请稍候…";
                batch = await Task.Run(() => AccountFileImport.ReadFiles(fileNames));
            }
            finally
            {
                if (!_formClosed && !IsDisposed)
                {
                    SetButtonsEnabled(true);
                    Cursor = Cursors.Default;
                }
            }
            if (_formClosed || IsDisposed) return;

            var dark = ThemeStyler.IsDark(_palette);
            using var preview = new Form
            {
                Text = "文件导入预览", Width = 840, Height = 570, MinimumSize = new Size(640, 420),
                StartPosition = FormStartPosition.CenterParent, Font = Font,
                AutoScaleMode = AutoScaleMode.Dpi, MinimizeBox = false, MaximizeBox = false
            };
            var note = new Label { Dock = DockStyle.Top, Height = 74, Padding = new Padding(14),
                Text = $"识别到 {batch.Entries.Count} 个账号，{batch.Problems.Count} 项无法导入。重复凭据将跳过，同名账号自动重命名。\n支持 sub2api、CPA Codex、官方 OAuth auth.json、PAT 和兼容 API。" };
            ThemeStyler.ApplyLabel(note, _palette);
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                GridLines = true, HideSelection = false, BackColor = _palette.InputBackColor, ForeColor = _palette.TextColor };
            list.HandleCreated += (_, _) => NativeWindowTheme.ApplyScrollable(list, dark);
            list.Columns.Add("账号 / 来源", 290);
            list.Columns.Add("识别结果", 480);
            foreach (var entry in batch.Entries)
                list.Items.Add(new ListViewItem(new[] { entry.Name, entry.AuthKind switch
                {
                    AccountAuthKind.OfficialOAuth => "ChatGPT OAuth（启动时验证 / 刷新）",
                    AccountAuthKind.CompatibleApi => "兼容 API", _ => "PAT"
                } }));
            foreach (var problem in batch.Problems)
                list.Items.Add(new ListViewItem(new[] { problem.Source, "跳过：" + problem.Reason }));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62,
                FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12), BackColor = _palette.SurfaceColor };
            var cancel = new ModernButton { Text = "取消", DialogResult = DialogResult.Cancel, Width = 104, Height = 38 };
            ThemeStyler.ApplySoftButton(cancel, _palette);
            var import = new ModernButton { Text = "导入有效账号", DialogResult = DialogResult.OK, Width = 148, Height = 38,
                Enabled = batch.Entries.Count > 0 };
            ThemeStyler.ApplyPrimaryButton(import, _palette);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(import);
            preview.Controls.Add(list);
            preview.Controls.Add(note);
            preview.Controls.Add(buttons);
            preview.AcceptButton = import;
            preview.CancelButton = cancel;
            ThemeStyler.ApplyDialog(preview, _palette);
            if (preview.ShowDialog(this) != DialogResult.OK) return;
            if (_formClosed || IsDisposed) return;

            (int Added, int Duplicates) result;
            Cursor = Cursors.WaitCursor;
            SetButtonsEnabled(false);
            try
            {
                _statusBox.Text = "正在导入账号凭据，请稍候…";
                result = await Task.Run(() => _store.ImportAccounts(batch.Entries));
            }
            finally
            {
                if (!_formClosed && !IsDisposed)
                {
                    SetButtonsEnabled(true);
                    Cursor = Cursors.Default;
                }
            }
            if (_formClosed || IsDisposed) return;

            LoadAccounts();
            _statusBox.Text = $"文件导入完成：新增 {result.Added} 个，重复 {result.Duplicates} 个，跳过无效记录 {batch.Problems.Count} 项。启动时会先查询账号最新模型。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (_formClosed || IsDisposed) return;
            // Never display credential JSON or include provider response bodies in diagnostics.
            MessageBox.Show(this, "导入未完成。请检查文件格式、账号数据目录权限与磁盘空间；现有账号未被覆盖。",
                "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
