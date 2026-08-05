using System.Runtime.InteropServices;

namespace CodexAccountManager;

internal sealed class ChatGptOAuthLinkDialog : Form
{
    private readonly string _loginUrl;
    private readonly TextBox _urlBox;
    private readonly Label _copyStatus;
    private bool _allowClose;
    private bool _cancellationRaised;

    public ChatGptOAuthLinkDialog(
        string accountName,
        ChatGptOAuthAuthorization authorization,
        ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _loginUrl = authorization.LoginUrl;

        Text = "ChatGPT 官方网页登录";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 430);
        MinimumSize = new Size(660, 410);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = palette.FormBackColor;
        ForeColor = palette.TextColor;
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 24, 28, 22),
            ColumnCount = 2,
            RowCount = 7,
            BackColor = palette.FormBackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

        var heading = new Label
        {
            Text = $"为“{accountName}”登录 ChatGPT",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(heading, palette);
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);

        var explanation = new Label
        {
            Text = "登录链接由官方桌面端同款 OAuth 流程生成。软件不会自动打开浏览器，也不会读取当前浏览器账号；" +
                   "请把已复制的链接粘贴到你选择的浏览器，登录并确认要绑定的 ChatGPT 账号。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(explanation, palette, true);
        layout.Controls.Add(explanation, 0, 1);
        layout.SetColumnSpan(explanation, 2);

        var urlLabel = new Label
        {
            Text = "OpenAI 官方登录链接（约 15 分钟内有效）",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        ThemeStyler.ApplyLabel(urlLabel, palette);
        layout.Controls.Add(urlLabel, 0, 2);
        layout.SetColumnSpan(urlLabel, 2);

        _urlBox = new TextBox
        {
            Text = _loginUrl,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            ShortcutsEnabled = true,
            TabStop = true
        };
        ThemeStyler.ApplyInput(_urlBox, palette);
        layout.Controls.Add(_urlBox, 0, 3);

        var copyUrl = new ModernButton
        {
            Text = "复制登录链接",
            Dock = DockStyle.Fill,
            Margin = new Padding(10, 0, 0, 0)
        };
        ThemeStyler.ApplyPrimaryButton(copyUrl, palette);
        copyUrl.Click += (_, _) => CopyLoginUrl(showFailure: true);
        layout.Controls.Add(copyUrl, 1, 3);

        _copyStatus = new Label
        {
            Text = "正在复制登录链接…",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(_copyStatus, palette, true);
        layout.Controls.Add(_copyStatus, 0, 4);
        layout.SetColumnSpan(_copyStatus, 2);

        var privacyNote = new Label
        {
            Text = "等待浏览器回调中。只有官方登录完成且本地 OAuth 凭据验证通过后，软件才会显示登录成功。\n" +
                   "安全提示：请确认浏览器地址属于 auth.openai.com 或 chatgpt.com。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = palette.WarningColor
        };
        layout.Controls.Add(privacyNote, 0, 5);
        layout.SetColumnSpan(privacyNote, 2);

        var cancel = new ModernButton
        {
            Text = "取消本次登录",
            Dock = DockStyle.Right,
            Width = 160
        };
        ThemeStyler.ApplySoftButton(cancel, palette);
        cancel.Click += (_, _) => RequestCancellation();
        layout.Controls.Add(cancel, 0, 6);
        layout.SetColumnSpan(cancel, 2);

        Controls.Add(layout);
        AcceptButton = copyUrl;
        CancelButton = cancel;
        Shown += (_, _) => CopyLoginUrl(showFailure: true);
    }

    public event EventHandler? CancellationRequested;

    public void CompleteAndClose()
    {
        if (IsDisposed)
        {
            return;
        }

        _allowClose = true;
        ClearEphemeralValues();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && !_cancellationRaised)
        {
            e.Cancel = true;
            if (e.CloseReason is CloseReason.ApplicationExitCall or CloseReason.FormOwnerClosing)
            {
                _cancellationRaised = true;
                _allowClose = true;
                ClearEphemeralValues();
                CancellationRequested?.Invoke(this, EventArgs.Empty);
                e.Cancel = false;
                base.OnFormClosing(e);
            }
            else
            {
                BeginInvoke(RequestCancellation);
            }
            return;
        }

        ClearEphemeralValues();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearEphemeralValues();
        }
        base.Dispose(disposing);
    }

    private void RequestCancellation()
    {
        if (_allowClose || _cancellationRaised || IsDisposed)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "取消后，本次官方网页登录会话会立即结束，原登录凭据会自动恢复。",
            "取消 ChatGPT 登录",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.OK)
        {
            return;
        }

        _cancellationRaised = true;
        _allowClose = true;
        ClearEphemeralValues();
        CancellationRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CopyLoginUrl(bool showFailure)
    {
        try
        {
            Clipboard.SetText(_loginUrl);
            _copyStatus.Text = "登录链接已复制。请自行粘贴到浏览器完成登录，软件正在等待结果…";
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            _copyStatus.Text = "剪贴板暂不可用，请手动选择并复制上方链接。";
            if (showFailure)
            {
                MessageBox.Show(
                    this,
                    $"无法复制登录链接，请手动选择文本：{ex.Message}",
                    "剪贴板不可用",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    private void ClearEphemeralValues()
    {
        if (!_urlBox.IsDisposed)
        {
            _urlBox.Clear();
        }
        try
        {
            if (Clipboard.ContainsText() &&
                Clipboard.GetText().Equals(_loginUrl, StringComparison.Ordinal))
            {
                Clipboard.Clear();
            }
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            // Clipboard cleanup is best effort and never persists the one-time login URL.
        }
    }
}
