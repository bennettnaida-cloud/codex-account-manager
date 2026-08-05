using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexAccountManager;

public sealed class ThreadPreviewDialog : Form
{
    private readonly RichTextBox _transcriptBox = new();
    private readonly TextBox _findBox = new();
    private readonly Label _findStatusLabel = new();
    private readonly Label _noticeLabel = new();
    private readonly string _copyText;

    public ThreadPreviewDialog(
        UnifiedThreadRecord thread,
        UnifiedThreadTranscript transcript,
        ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(transcript);

        Text = "阅读本地聊天";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(980, 700);
        MinimumSize = new Size(760, 540);
        Font = new Font("Microsoft YaHei UI", 9.25F);
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = false;
        ThemeStyler.ApplyDialog(this, palette);

        _copyText = BuildCopyText(thread, transcript);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = palette.FormBackColor,
            Padding = new Padding(22),
            ColumnCount = 1,
            RowCount = 4
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132F));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        root.Controls.Add(header, 0, 0);

        var title = new Label
        {
            Text = thread.Title,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            AccessibleName = "聊天标题"
        };
        ThemeStyler.ApplyLabel(title, palette);
        header.Controls.Add(title, 0, 0);

        var readOnlyBadge = new PillLabel
        {
            Text = "本地只读",
            Dock = DockStyle.Fill,
            Margin = new Padding(12, 7, 0, 7),
            Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            FillColor = Color.FromArgb(34, palette.AccentColor),
            StrokeColor = Color.FromArgb(94, palette.AccentColor),
            ForeColor = palette.PrimaryColor
        };
        header.Controls.Add(readOnlyBadge, 1, 0);

        var subtitle = new Label
        {
            Text = $"{transcript.Messages.Count} 条对话消息 · 已过滤系统信息与工具日志",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = new Font(Font.FontFamily, 8.8F),
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(subtitle, palette, true);
        header.Controls.Add(subtitle, 0, 1);
        header.SetColumnSpan(subtitle, 2);

        var searchRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 10)
        };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104F));
        searchRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.Controls.Add(searchRow, 0, 1);

        _findBox.PlaceholderText = "搜索当前对话（Ctrl+F）";
        _findBox.Dock = DockStyle.Fill;
        _findBox.Font = new Font(Font.FontFamily, 9F);
        _findBox.Margin = Padding.Empty;
        ThemeStyler.ApplyInput(_findBox, palette);
        var findShell = new ModernInputShell(_findBox, showSearchGlyph: true)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 10, 2),
            Radius = 9
        };
        ThemeStyler.ApplyInputShell(findShell, palette);
        searchRow.Controls.Add(findShell, 0, 0);

        var previous = new ModernButton
        {
            Text = "上一个",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 8, 2),
            AccessibleName = "查找上一个匹配"
        };
        ThemeStyler.ApplySoftButton(previous, palette);
        previous.Click += (_, _) => FindMatch(forward: false);
        searchRow.Controls.Add(previous, 1, 0);

        var next = new ModernButton
        {
            Text = "下一个",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 8, 2),
            AccessibleName = "查找下一个匹配"
        };
        ThemeStyler.ApplySoftButton(next, palette);
        next.Click += (_, _) => FindMatch(forward: true);
        searchRow.Controls.Add(next, 2, 0);

        _findStatusLabel.Text = "Ctrl+F";
        _findStatusLabel.Dock = DockStyle.Fill;
        _findStatusLabel.Font = new Font(Font.FontFamily, 8.2F, FontStyle.Bold);
        _findStatusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _findStatusLabel.UseCompatibleTextRendering = true;
        _findStatusLabel.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_findStatusLabel, palette, true);
        searchRow.Controls.Add(_findStatusLabel, 3, 0);

        _findBox.TextChanged += (_, _) => FindMatch(forward: true, restart: true);
        _findBox.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Enter)
            {
                FindMatch(forward: !eventArgs.Shift);
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
            }
            else if (eventArgs.KeyCode == Keys.Escape)
            {
                _findBox.Clear();
                _transcriptBox.Focus();
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
            }
        };

        var transcriptShell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = palette.BorderColor,
            Padding = new Padding(1),
            Margin = new Padding(0, 0, 0, 14)
        };
        root.Controls.Add(transcriptShell, 0, 2);

        var transcriptSurface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = palette.InputBackColor,
            Padding = new Padding(18, 16, 14, 16)
        };
        transcriptShell.Controls.Add(transcriptSurface);

        _transcriptBox.Dock = DockStyle.Fill;
        _transcriptBox.ReadOnly = true;
        _transcriptBox.BorderStyle = BorderStyle.None;
        _transcriptBox.BackColor = palette.InputBackColor;
        _transcriptBox.ForeColor = palette.TextColor;
        _transcriptBox.Font = new Font(Font.FontFamily, 9.5F);
        _transcriptBox.WordWrap = true;
        _transcriptBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        _transcriptBox.ShortcutsEnabled = true;
        _transcriptBox.HideSelection = false;
        _transcriptBox.DetectUrls = false;
        _transcriptBox.AccessibleName = "本地聊天简版正文";
        _transcriptBox.AccessibleDescription = "只读内容，可选择后按 Ctrl+C 复制。";
        transcriptSurface.Controls.Add(_transcriptBox);
        RenderTranscript(transcript, palette);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.Controls.Add(footer, 0, 3);

        _noticeLabel.Text = string.IsNullOrWhiteSpace(transcript.Notice)
            ? "仅显示你和 Codex 的对话；Ctrl+F 搜索，Ctrl+C 复制。"
            : transcript.Notice + "  已过滤系统信息；Ctrl+F 搜索。";
        _noticeLabel.Dock = DockStyle.Fill;
        _noticeLabel.Margin = new Padding(0, 0, 14, 0);
        _noticeLabel.AutoEllipsis = true;
        _noticeLabel.Font = new Font(Font.FontFamily, 8.3F);
        _noticeLabel.TextAlign = ContentAlignment.MiddleLeft;
        _noticeLabel.UseCompatibleTextRendering = true;
        _noticeLabel.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_noticeLabel, palette, true);
        footer.Controls.Add(_noticeLabel, 0, 0);

        var copy = new ModernButton
        {
            Text = "复制全部",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 14, 12, 14),
            AccessibleName = "复制全部聊天正文"
        };
        ThemeStyler.ApplySoftButton(copy, palette);
        copy.Click += (_, _) => CopyAll();
        footer.Controls.Add(copy, 1, 0);

        var close = new ModernButton
        {
            Text = "关闭",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 14, 0, 14),
            DialogResult = DialogResult.Cancel,
            AccessibleName = "关闭聊天阅读窗口"
        };
        ThemeStyler.ApplyPrimaryButton(close, palette);
        footer.Controls.Add(close, 2, 0);
        CancelButton = close;

        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Control && eventArgs.KeyCode == Keys.F)
            {
                _findBox.Focus();
                _findBox.SelectAll();
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
            }
            else if (eventArgs.KeyCode == Keys.F3)
            {
                FindMatch(forward: !eventArgs.Shift);
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
            }
        };

        Shown += (_, _) =>
        {
            _transcriptBox.SelectionStart = 0;
            _transcriptBox.SelectionLength = 0;
            _transcriptBox.ScrollToCaret();
            _transcriptBox.Focus();
        };
    }

    internal static void ValidateFormatting()
    {
        var startedAt = DateTimeOffset.Parse("2026-07-12T12:00:00Z", CultureInfo.InvariantCulture);
        var thread = new UnifiedThreadRecord(
            "019f5c10-7f43-7a84-89c6-b94ba0c82455",
            "Synthetic preview",
            "",
            "",
            "",
            "",
            startedAt,
            Archived: false,
            HasUserEvent: true);
        var transcript = new UnifiedThreadTranscript(
            UnifiedThreadTranscriptStatus.Available,
            [
                new UnifiedThreadMessage(UnifiedThreadMessageRole.User, "question fixture", startedAt),
                new UnifiedThreadMessage(UnifiedThreadMessageRole.Assistant, "answer fixture", startedAt.AddSeconds(1))
            ],
            IsTruncated: false,
            IgnoredMalformedLines: 0,
            IgnoredOversizedLines: 0,
            Notice: "fixture notice");
        var text = BuildCopyText(thread, transcript);
        var user = text.IndexOf("你 ·", StringComparison.Ordinal);
        var assistant = text.IndexOf("Codex ·", StringComparison.Ordinal);
        if (user < 0 || assistant <= user ||
            !text.Contains("question fixture", StringComparison.Ordinal) ||
            !text.Contains("answer fixture", StringComparison.Ordinal) ||
            text.Contains("fixture notice", StringComparison.Ordinal) ||
            CountMatches("alpha beta ALPHA", "alpha") != 2)
        {
            throw new InvalidOperationException("Thread preview dialog formatting validation failed.");
        }
    }

    private void RenderTranscript(UnifiedThreadTranscript transcript, ThemePalette palette)
    {
        _transcriptBox.Clear();
        using var authorFont = new Font(Font.FontFamily, 9.6F, FontStyle.Bold);
        using var bodyFont = new Font(Font.FontFamily, 9.5F, FontStyle.Regular);
        using var noticeFont = new Font(Font.FontFamily, 10F, FontStyle.Bold);

        if (transcript.Messages.Count == 0)
        {
            AppendStyled("当前没有可显示的正文", noticeFont, palette.MutedTextColor);
            AppendStyled(Environment.NewLine + Environment.NewLine + transcript.Notice, bodyFont, palette.MutedTextColor);
            return;
        }

        for (var i = 0; i < transcript.Messages.Count; i++)
        {
            var message = transcript.Messages[i];
            var author = message.Role == UnifiedThreadMessageRole.User ? "你" : "Codex";
            var authorColor = message.Role == UnifiedThreadMessageRole.User
                ? palette.PrimaryColor
                : palette.SuccessColor;
            var time = message.Timestamp.HasValue
                ? message.Timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
                : "时间未知";

            AppendStyled(author, authorFont, authorColor);
            AppendStyled("  ·  " + time + Environment.NewLine, bodyFont, palette.MutedTextColor);
            AppendStyled(message.Text.Trim(), bodyFont, palette.TextColor);
            if (i + 1 < transcript.Messages.Count)
            {
                AppendStyled(Environment.NewLine + Environment.NewLine + Environment.NewLine, bodyFont, palette.DividerColor);
            }
        }

        _transcriptBox.SelectionStart = 0;
        _transcriptBox.SelectionLength = 0;
    }

    private void AppendStyled(string text, Font font, Color color)
    {
        _transcriptBox.SelectionStart = _transcriptBox.TextLength;
        _transcriptBox.SelectionLength = 0;
        _transcriptBox.SelectionFont = font;
        _transcriptBox.SelectionColor = color;
        _transcriptBox.AppendText(text);
    }

    private void CopyAll()
    {
        if (string.IsNullOrEmpty(_copyText))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_copyText, TextDataFormat.UnicodeText);
            _noticeLabel.Text = "已复制全部简版聊天正文。";
        }
        catch (ExternalException)
        {
            _noticeLabel.Text = "剪贴板暂时被占用；可选中文字后按 Ctrl+C 重试。";
        }
    }

    private void FindMatch(bool forward, bool restart = false)
    {
        var query = _findBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _findStatusLabel.Text = "Ctrl+F";
            _transcriptBox.SelectionLength = 0;
            return;
        }

        var text = _transcriptBox.Text;
        var total = CountMatches(text, query);
        if (total == 0)
        {
            _findStatusLabel.Text = "无匹配";
            _transcriptBox.SelectionLength = 0;
            return;
        }

        int index;
        if (forward)
        {
            var start = restart
                ? 0
                : Math.Min(text.Length, _transcriptBox.SelectionStart + Math.Max(1, _transcriptBox.SelectionLength));
            index = text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
            {
                index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            }
        }
        else
        {
            var start = restart
                ? text.Length - 1
                : _transcriptBox.SelectionStart - 1;
            index = start >= 0
                ? text.LastIndexOf(
                    query,
                    Math.Min(start, text.Length - 1),
                    StringComparison.CurrentCultureIgnoreCase)
                : -1;
            if (index < 0)
            {
                index = text.LastIndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            }
        }

        if (index < 0)
        {
            _findStatusLabel.Text = "无匹配";
            return;
        }

        _transcriptBox.SelectionStart = index;
        _transcriptBox.SelectionLength = query.Length;
        _transcriptBox.ScrollToCaret();
        var ordinal = CountMatches(text[..index], query) + 1;
        _findStatusLabel.Text = $"{ordinal} / {total}";
    }

    private static int CountMatches(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return 0;
        }

        var count = 0;
        var start = 0;
        while (start <= text.Length - query.Length)
        {
            var index = text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
            {
                break;
            }
            count++;
            start = index + Math.Max(1, query.Length);
        }
        return count;
    }

    private static string BuildCopyText(
        UnifiedThreadRecord thread,
        UnifiedThreadTranscript transcript)
    {
        var builder = new StringBuilder();
        builder.AppendLine(thread.Title);
        builder.AppendLine("本地只读对话");
        builder.AppendLine();

        foreach (var message in transcript.Messages)
        {
            var author = message.Role == UnifiedThreadMessageRole.User ? "你" : "Codex";
            var time = message.Timestamp.HasValue
                ? message.Timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
                : "时间未知";
            builder.Append(author).Append(" · ").AppendLine(time);
            builder.AppendLine(message.Text.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
