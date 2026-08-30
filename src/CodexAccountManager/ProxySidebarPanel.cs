using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace CodexAccountManager;

/// <summary>Persistent proxy-node workspace. Credentials are edited in transient
/// dialogs and only DPAPI ciphertext is written to the manager root.</summary>
internal sealed class ProxySidebarPanel : Panel
{
    private readonly ProxyNodeStore _store;
    private readonly AccountProxyResolver _resolver;
    private readonly Func<IReadOnlyList<AccountRecord>> _accounts;
    private readonly Action<string> _status;
    private readonly ThemedComboBox _account = new();
    private readonly ThemedComboBox _mode = new();
    private readonly ThemedComboBox _node = new();
    private readonly ThemedComboBox _fallback = new();
    private readonly ListView _nodes = new();
    private readonly Label _bindingHint = new();
    private readonly Label _metrics = new();
    private readonly ModernButton _save = new();
    private readonly ModernButton _add = new();
    private readonly ModernButton _edit = new();
    private readonly ModernButton _remove = new();
    private readonly ModernButton _toggle = new();
    private readonly ModernButton _import = new();
    private readonly ModernButton _test = new();
    private readonly ModernButton _continuous = new();
    private readonly ModernButton _testAll = new();
    private readonly RoundedPanel _bindingCard = new();
    private readonly RoundedPanel _nodesCard = new();
    private readonly RoundedPanel _metricsCard = new();
    private Label _bindingTitle = null!;
    private Label _bindingSubtitle = null!;
    private Label _accountLabel = null!;
    private Label _modeLabel = null!;
    private Label _nodeLabel = null!;
    private Label _fallbackLabel = null!;
    private Label _nodesTitle = null!;
    private Label _nodesSubtitle = null!;
    private ThemePalette _palette = null!;
    private IReadOnlyList<AccountRecord> _currentAccounts = [];
    private CancellationTokenSource? _testCancellation;
    private bool _testRunning;
    private bool _disposed;

    public ProxySidebarPanel(string rootPath, Func<IReadOnlyList<AccountRecord>> accounts, Action<string> status)
    {
        _store = new ProxyNodeStore(rootPath); _resolver = new AccountProxyResolver(rootPath); _accounts = accounts; _status = status;
        Name = "ProxySidebarPanel";
        Dock = DockStyle.None;
        AutoScroll = true;
        Padding = new Padding(2, 4, 2, 8);
        Build();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            try { _testCancellation?.Cancel(); } catch { }
            _testCancellation?.Dispose();
            _testCancellation = null;
            _resolver.Dispose();
        }
        base.Dispose(disposing);
    }

    private bool IsUsable
    {
        get
        {
            var form = FindForm();
            return !_disposed && !IsDisposed && !Disposing &&
                (form == null || (!form.IsDisposed && !form.Disposing));
        }
    }

    private void ReportStatus(string message)
    {
        if (!IsUsable) return;
        try { _status(message); } catch (Exception) { /* status reporting must not fault a probe */ }
    }

    private void SetMetrics(string text)
    {
        if (!IsUsable) return;
        try { _metrics.Text = text; } catch (Exception) { /* metrics reporting must not fault a probe */ }
    }

    private void Build()
    {
        ConfigureCard(_bindingCard, "AccountProxyBindingCard");
        ConfigureCard(_nodesCard, "ProxyNodeLibraryCard");
        ConfigureCard(_metricsCard, "ProxyMetricsCard");
        Controls.Add(_bindingCard);
        Controls.Add(_nodesCard);

        _bindingTitle = MakeLabel("账号代理绑定", 14F, strong: true);
        _bindingSubtitle = MakeLabel(
            "账号与节点按稳定 AccountKey 关联；自动轮换时跟随实际候选账号切换。",
            10F,
            muted: true);
        _accountLabel = MakeLabel("当前账号", 10F, strong: true, muted: true);
        _modeLabel = MakeLabel("代理策略", 10F, strong: true, muted: true);
        _nodeLabel = MakeLabel("绑定节点", 10F, strong: true, muted: true);
        _fallbackLabel = MakeLabel("故障回退", 10F, strong: true, muted: true);
        _bindingCard.Controls.AddRange([
            _bindingTitle,
            _bindingSubtitle,
            _accountLabel,
            _modeLabel,
            _nodeLabel,
            _fallbackLabel]);
        _account.DropDownStyle = ComboBoxStyle.DropDownList;
        _account.SelectedIndexChanged += (_, _) => LoadBinding();
        _bindingCard.Controls.Add(_account);
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.Items.AddRange(["继承全局代理", "固定绑定节点", "禁用代理（安全失败）"]);
        _mode.SelectedIndexChanged += (_, _) => UpdateBindingEditor();
        _bindingCard.Controls.Add(_mode);
        _node.DropDownStyle = ComboBoxStyle.DropDownList;
        _node.SelectedIndexChanged += (_, _) => UpdateBindingEditor();
        _bindingCard.Controls.Add(_node);
        _fallback.DropDownStyle = ComboBoxStyle.DropDownList;
        _fallback.Items.AddRange(["故障时拒绝请求", "故障时继承全局代理"]);
        _bindingCard.Controls.Add(_fallback);
        ConfigureButton(_save, "保存账号绑定", primary: true);
        _save.Click += (_, _) => SaveBinding();
        _bindingCard.Controls.Add(_save);
        _bindingHint.AutoSize = false;
        _bindingHint.AutoEllipsis = false;
        _bindingHint.UseCompatibleTextRendering = true;
        _bindingHint.Tag = "muted";
        _bindingCard.Controls.Add(_bindingHint);

        _nodesTitle = MakeLabel("代理节点库", 14F, strong: true);
        _nodesSubtitle = MakeLabel(
            "HTTP / HTTPS / SOCKS5 / VLESS / VMess / Trojan / SS · 凭据使用当前 Windows 用户的 DPAPI 加密保存",
            10F,
            muted: true);
        _nodesCard.Controls.AddRange([_nodesTitle, _nodesSubtitle]);
        _nodes.View = View.Details;
        _nodes.FullRowSelect = true;
        _nodes.MultiSelect = false;
        _nodes.HideSelection = false;
        _nodes.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _nodes.BorderStyle = BorderStyle.FixedSingle;
        _nodes.GridLines = true;
        _nodes.Font = new Font("Microsoft YaHei UI", 10F);
        _nodes.Columns.Add("节点名称");
        _nodes.Columns.Add("协议");
        _nodes.Columns.Add("地址");
        _nodes.Columns.Add("延迟");
        _nodes.Columns.Add("出口 IP");
        _nodes.SelectedIndexChanged += (_, _) => UpdateNodeMetrics();
        _nodesCard.Controls.Add(_nodes);

        ConfigureButton(_add, "添加节点");
        ConfigureButton(_edit, "编辑");
        ConfigureButton(_remove, "删除");
        ConfigureButton(_toggle, "启用 / 禁用");
        ConfigureButton(_import, "批量导入 / 订阅", primary: true);
        ConfigureButton(_test, "测试 ChatGPT");
        ConfigureButton(_continuous, "连续测速 P50 / P90");
        ConfigureButton(_testAll, "一键测速全部", primary: true);
        _add.Click += (_, _) => EditNode(null);
        _edit.Click += (_, _) => EditNode(SelectedNode());
        _remove.Click += (_, _) => RemoveNode();
        _toggle.Click += (_, _) => ToggleNode();
        _import.Click += async (_, _) => await ImportNodesAsync();
        _test.Click += async (_, _) => await RunTestGuardedAsync(TestSelectedAsync, "正在测试 ChatGPT…", TimeSpan.FromSeconds(8));
        _continuous.Click += async (_, _) => await RunTestGuardedAsync(ContinuousTestAsync, "正在连续测速…", TimeSpan.FromSeconds(20));
        _testAll.Click += async (_, _) => await RunTestGuardedAsync(TestAllAsync, "正在一键测速全部节点…", TimeSpan.FromSeconds(90));
        _nodesCard.Controls.AddRange([_add, _edit, _remove, _toggle, _import, _test, _continuous, _testAll]);

        _metrics.AutoSize = false;
        _metrics.UseCompatibleTextRendering = true;
        _metrics.Tag = "muted";
        _metricsCard.Controls.Add(_metrics);
        _nodesCard.Controls.Add(_metricsCard);

        _bindingCard.Resize += (_, _) => LayoutBindingCard();
        _nodesCard.Resize += (_, _) => LayoutNodesCard();
        Resize += (_, _) => LayoutWorkspace();
        LayoutWorkspace();
    }

    private static void ConfigureCard(RoundedPanel card, string name)
    {
        card.Name = name;
        card.Radius = 16;
        card.UseGradient = true;
        card.Elevation = 1;
        card.Padding = Padding.Empty;
        card.Margin = Padding.Empty;
    }

    private static Label MakeLabel(string text, float size, bool strong = false, bool muted = false) => new()
    {
        Text = text,
        AutoSize = false,
        AutoEllipsis = true,
        UseMnemonic = false,
        UseCompatibleTextRendering = true,
        Font = new Font(SystemFonts.DefaultFont.FontFamily, size, strong ? FontStyle.Bold : FontStyle.Regular),
        Tag = muted ? "muted" : "label"
    };

    private static void ConfigureButton(ModernButton button, string text, bool primary = false)
    {
        button.Text = text;
        button.Height = 44;
        button.Radius = 11;
        button.Font = new Font("Microsoft YaHei UI", 10F, primary ? FontStyle.Bold : FontStyle.Regular);
        button.Tag = primary ? "proxy-primary" : "proxy-soft";
        button.ShowIconTile = false;
        button.IconWidth = 0;
    }

    private void LayoutWorkspace()
    {
        var availableWidth = Math.Max(320, ClientSize.Width - Padding.Horizontal);
        var left = Padding.Left;
        var top = Padding.Top;
        const int gap = 16;
        if (availableWidth >= 980)
        {
            var bindingWidth = Math.Clamp((int)Math.Round(availableWidth * 0.36D), 390, 520);
            var nodesWidth = Math.Max(420, availableWidth - bindingWidth - gap);
            // Leave enough room for the node table, action rows and the metrics
            // card even on a compact window.  The parent workspace can scroll
            // vertically; clipping the metrics card is much harder to diagnose.
            var cardHeight = Math.Max(760, ClientSize.Height - Padding.Vertical - 4);
            _bindingCard.SetBounds(left, top, bindingWidth, cardHeight);
            _nodesCard.SetBounds(left + bindingWidth + gap, top, nodesWidth, cardHeight);
            AutoScrollMinSize = new Size(0, cardHeight + Padding.Vertical);
        }
        else
        {
            var bindingHeight = 640;
            var nodesHeight = 760;
            _bindingCard.SetBounds(left, top, availableWidth, bindingHeight);
            _nodesCard.SetBounds(left, top + bindingHeight + gap, availableWidth, nodesHeight);
            AutoScrollMinSize = new Size(0, bindingHeight + nodesHeight + gap + Padding.Vertical);
        }

        LayoutBindingCard();
        LayoutNodesCard();
    }

    private void LayoutBindingCard()
    {
        if (_bindingTitle == null)
        {
            return;
        }

        var width = Math.Max(220, _bindingCard.ClientSize.Width - 48);
        _bindingTitle.SetBounds(24, 20, width, 38);
        _bindingSubtitle.SetBounds(24, 60, width, 52);
        _accountLabel.SetBounds(24, 120, width, 28);
        _account.SetBounds(24, 150, width, 42);
        _modeLabel.SetBounds(24, 204, width, 28);
        _mode.SetBounds(24, 234, width, 42);
        _nodeLabel.SetBounds(24, 288, width, 28);
        _node.SetBounds(24, 318, width, 42);
        _fallbackLabel.SetBounds(24, 372, width, 28);
        _fallback.SetBounds(24, 402, width, 42);
        _save.SetBounds(24, 458, width, 46);
        _bindingHint.SetBounds(24, 520, width, Math.Max(70, _bindingCard.ClientSize.Height - 544));
    }

    private void LayoutNodesCard()
    {
        if (_nodesTitle == null)
        {
            return;
        }

        var width = Math.Max(320, _nodesCard.ClientSize.Width - 48);
        _nodesTitle.SetBounds(24, 20, width, 38);
        _nodesSubtitle.SetBounds(24, 60, width, 36);
        var listHeight = Math.Max(210, _nodesCard.ClientSize.Height - 372);
        _nodes.SetBounds(24, 108, width, listHeight);

        var firstTop = _nodes.Bottom + 14;
        var firstGap = 8;
        var firstButtonWidth = Math.Max(74, (width - firstGap * 3) / 4);
        _add.SetBounds(24, firstTop, firstButtonWidth, 44);
        _edit.SetBounds(_add.Right + firstGap, firstTop, firstButtonWidth, 44);
        _remove.SetBounds(_edit.Right + firstGap, firstTop, firstButtonWidth, 44);
        _toggle.SetBounds(_remove.Right + firstGap, firstTop, Math.Max(74, width - firstButtonWidth * 3 - firstGap * 3), 44);

        var secondTop = firstTop + 50;
        var importWidth = Math.Max(140, (int)Math.Round(width * 0.28D));
        var testWidth = Math.Max(130, (int)Math.Round(width * 0.21D));
        var continuousWidth = Math.Max(150, (int)Math.Round(width * 0.27D));
        var allWidth = Math.Max(130, width - importWidth - testWidth - continuousWidth - firstGap * 3);
        _import.SetBounds(24, secondTop, importWidth, 44);
        _test.SetBounds(_import.Right + firstGap, secondTop, testWidth, 44);
        _continuous.SetBounds(_test.Right + firstGap, secondTop, continuousWidth, 44);
        _testAll.SetBounds(_continuous.Right + firstGap, secondTop, allWidth, 44);

        var metricsTop = secondTop + 58;
        var metricsHeight = Math.Max(128, _nodesCard.ClientSize.Height - metricsTop - 20);
        _metricsCard.SetBounds(24, metricsTop, width, metricsHeight);
        _metrics.SetBounds(
            16,
            10,
            Math.Max(120, _metricsCard.ClientSize.Width - 32),
            Math.Max(88, _metricsCard.ClientSize.Height - 20));

        // Keep the human-readable node name prominent and make the technical endpoint
        // compact.  Calculate against the actual ListView client width so the sum of
        // columns never creates the horizontal scrollbar that used to appear on the
        // right side of this workspace.
        // Reserve the native vertical-scrollbar slot even before it becomes visible;
        // otherwise adding the final rows can shrink ClientSize and reintroduce a
        // horizontal scrollbar after the columns were measured.
        var columnWidth = Math.Max(320, _nodes.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        var protocolWidth = 72;
        var latencyWidth = 92;
        var exitIpWidth = 132;
        var addressWidth = Math.Clamp((int)Math.Round(width * 0.16D), 140, 200);
        var nameWidth = columnWidth - protocolWidth - latencyWidth - exitIpWidth - addressWidth - 4;
        if (nameWidth < 260)
        {
            nameWidth = 260;
            addressWidth = Math.Max(120, columnWidth - protocolWidth - latencyWidth - exitIpWidth - nameWidth - 4);
        }
        _nodes.Columns[0].Width = nameWidth;
        _nodes.Columns[1].Width = protocolWidth;
        _nodes.Columns[3].Width = latencyWidth;
        _nodes.Columns[4].Width = exitIpWidth;
        _nodes.Columns[2].Width = addressWidth;
    }

    public void ApplyPalette(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.FormBackColor;
        ForeColor = palette.TextColor;
        foreach (var card in new[] { _bindingCard, _nodesCard })
        {
            card.BackColor = palette.CardColor;
            card.BorderColor = palette.BorderColor;
            card.GradientColor = UiDesign.Blend(palette.CardColor, palette.PrimaryColor, 0.025F);
            card.AccentColor = palette.TertiaryAccentColor;
            card.AccentWidth = 3;
            card.ShadowColor = Color.FromArgb(26, palette.ShadowColor);
        }
        _metricsCard.BackColor = palette.SurfaceAltColor;
        _metricsCard.BorderColor = palette.BorderColor;
        _metricsCard.GradientColor = UiDesign.Blend(palette.SurfaceAltColor, palette.PrimaryColor, 0.02F);
        _metricsCard.ShadowColor = Color.Transparent;
        _metricsCard.AccentWidth = 0;

        foreach (var child in Descendants(this))
        {
            child.ForeColor = palette.TextColor;
            if (child is Label label)
            {
                ThemeStyler.ApplyLabel(label, palette, Equals(label.Tag, "muted"));
            }
            if (child is ComboBox combo)
            {
                ThemeStyler.ApplyComboBox(combo, palette);
            }
            if (child is Button button)
            {
                if (Equals(button.Tag, "proxy-primary")) ThemeStyler.ApplyPrimaryButton(button, palette);
                else ThemeStyler.ApplySoftButton(button, palette);
            }
        }
        _nodes.BackColor = palette.InputBackColor;
        _nodes.ForeColor = palette.TextColor;
        _nodes.Invalidate();
        UpdateBoundNodeHighlight();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    public void SetAccounts(IReadOnlyList<AccountRecord> accounts, string? selectedName)
    {
        if (!IsUsable) return;
        _currentAccounts = accounts; var key = selectedName;
        _account.Items.Clear(); foreach (var account in accounts) _account.Items.Add(account.Name);
        if (key != null) _account.SelectedItem = key; if (_account.SelectedIndex < 0 && _account.Items.Count > 0) _account.SelectedIndex = 0;
        RefreshNodes(); LoadBinding();
    }
    public void RefreshNodes()
    {
        if (!IsUsable) return;
        var selectedNodeId = SelectedNode()?.NodeId;
        var nodes = _store.LoadNodes();
        _nodes.BeginUpdate();
        _nodes.Items.Clear();
        foreach (var node in nodes)
        {
            var latency = node.Enabled && string.IsNullOrWhiteSpace(node.LastHealthError) && node.LastFirstResponseMilliseconds.HasValue
                ? $"{node.LastFirstResponseMilliseconds.Value:0} ms"
                : "-1";
            var exitIp = FormatExitIpForList(node);
            var item = new ListViewItem(node.Name) { Tag = node };
            item.SubItems.Add(CompactProtocol(node.Scheme));
            item.SubItems.Add(node.DisplayAddress);
            item.SubItems.Add(latency);
            item.SubItems.Add(exitIp);
            _nodes.Items.Add(item);
            if (selectedNodeId != null && node.NodeId.Equals(selectedNodeId, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
        }
        _nodes.EndUpdate();
        LayoutNodesCard();
        _node.Items.Clear();
        _node.Items.Add("（选择节点）");
        foreach (var node in nodes) _node.Items.Add(new NodeItem(node));
        if (_node.SelectedIndex < 0) _node.SelectedIndex = 0;
        UpdateNodeMetrics();
        UpdateBoundNodeHighlight();
    }
    private static string CompactProtocol(string scheme) => scheme.ToLowerInvariant() switch
    {
        "https" => "TLS",
        "socks5" => "S5",
        "vless" => "VL",
        "vmess" => "VM",
        "trojan" => "TR",
        "ss" or "shadowsocks" => "SS",
        _ => "HTTP"
    };
    private AccountRecord? SelectedAccount() => _currentAccounts.FirstOrDefault(a => a.Name.Equals(_account.SelectedItem?.ToString(), StringComparison.OrdinalIgnoreCase));
    private ProxyNodeRecord? SelectedNode() => _nodes.SelectedItems.Count == 0 ? null : _nodes.SelectedItems[0].Tag as ProxyNodeRecord;
    private void LoadBinding()
    {
        if (!IsUsable) return;
        var account = SelectedAccount(); var binding = account == null ? null : _store.GetBinding(AccountProxyResolver.AccountKeyFor(account));
        _mode.SelectedIndex = binding?.Mode switch { ProxyBindingMode.FixedNode => 1, ProxyBindingMode.Disabled => 2, _ => 0 };
        _fallback.SelectedIndex = binding?.FallbackPolicy == ProxyFallbackPolicy.InheritGlobal ? 1 : 0;
        var idx = binding?.NodeId == null ? 0 : Enumerable.Range(1, Math.Max(0, _node.Items.Count - 1)).FirstOrDefault(i => (_node.Items[i] as NodeItem)?.Node.NodeId.Equals(binding.NodeId, StringComparison.OrdinalIgnoreCase) == true, 0);
        _node.SelectedIndex = _node.Items.Count == 0 ? -1 : Math.Clamp(idx, 0, _node.Items.Count - 1);
        SelectBoundNode(binding?.Mode == ProxyBindingMode.FixedNode ? binding.NodeId : null);
        UpdateBindingEditor();
    }

    private void SelectBoundNode(string? nodeId)
    {
        if (!IsUsable) return;
        _nodes.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _nodes.Items)
            {
                var matches = nodeId != null && item.Tag is ProxyNodeRecord node &&
                              node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase);
                item.Selected = matches;
                if (matches) item.EnsureVisible();
            }
        }
        finally
        {
            _nodes.EndUpdate();
        }
        UpdateBoundNodeHighlight();
    }

    private void UpdateBoundNodeHighlight()
    {
        if (!IsUsable) return;
        var account = SelectedAccount();
        var binding = account == null ? null : _store.GetBinding(AccountProxyResolver.AccountKeyFor(account));
        var boundNodeId = binding?.Mode == ProxyBindingMode.FixedNode ? binding.NodeId : null;
        var highlightBack = _palette == null
            ? Color.FromArgb(220, 237, 255)
            : Color.FromArgb(218, _palette.PrimaryColor.R, _palette.PrimaryColor.G, _palette.PrimaryColor.B);
        var highlightText = _palette == null ? Color.FromArgb(24, 83, 160) : _palette.PrimaryColor;
        foreach (ListViewItem item in _nodes.Items)
        {
            var matches = boundNodeId != null && item.Tag is ProxyNodeRecord node &&
                          node.NodeId.Equals(boundNodeId, StringComparison.OrdinalIgnoreCase);
            item.BackColor = matches ? highlightBack : _nodes.BackColor;
            item.ForeColor = matches ? highlightText : _nodes.ForeColor;
            item.ToolTipText = matches && account != null
                ? $"当前账号绑定节点：{account.Name}"
                : string.Empty;
        }
        _nodes.Invalidate();
    }

    private static string FormatExitIpForList(ProxyNodeRecord node)
    {
        if (string.IsNullOrWhiteSpace(node.LastExitIp)) return "未检测";
        return HasCurrentExitIp(node) ? node.LastExitIp : $"上次 {node.LastExitIp}";
    }

    private static bool HasCurrentExitIp(ProxyNodeRecord node) =>
        node.LastHealthError == null &&
        node.LastExitIpObservedAtUtc.HasValue &&
        node.LastHealthTestAtUtc.HasValue &&
        node.LastExitIpObservedAtUtc.Value >= node.LastHealthTestAtUtc.Value;
    private void UpdateBindingEditor()
    {
        if (!IsUsable) return;
        _node.Enabled = _mode.SelectedIndex == 1; _fallback.Enabled = _mode.SelectedIndex == 1;
        var account = SelectedAccount(); var binding = account == null ? null : _store.GetBinding(AccountProxyResolver.AccountKeyFor(account));
        _bindingHint.Text = _mode.SelectedIndex switch { 0 => "未配置账号级策略，将使用系统配置中的全局代理。", 1 => "自动轮换时会与实际候选账号一起切换代理。", _ => "已禁用直连与代理回退，网关将安全失败。" };
    }
    private void UpdateNodeMetrics()
    {
        if (!IsUsable) return;
        var node = SelectedNode();
        if (node == null) { SetMetrics("选择节点查看测速与出口 IP 状态。\n\n选中节点后可测试 TCP、TLS、HTTPS 首响应和出口 IP。"); return; }
        var exit = string.IsNullOrWhiteSpace(node.LastExitIp)
            ? "未检测"
            : HasCurrentExitIp(node)
                ? node.LastExitIp + (node.ExitIpChanged ? "（已变化）" : "")
                : $"未确认（上次 {node.LastExitIp}）";
        var bound = _store.LoadBindings().Count(b => b.Mode == ProxyBindingMode.FixedNode && b.NodeId?.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase) == true);
        var names = _currentAccounts.Where(account => _store.GetBinding(AccountProxyResolver.AccountKeyFor(account)) is { Mode: ProxyBindingMode.FixedNode } binding && binding.NodeId?.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase) == true).Select(account => account.Name).ToArray();
        SetMetrics($"{node.DisplayUrl}\nTCP：{Format(node.LastTcpMilliseconds)} · TLS：{Format(node.LastTlsMilliseconds)} · 首响应：{Format(node.LastFirstResponseMilliseconds)}\n出口 IP：{exit} · 已绑定账号：{bound}\n{(names.Length == 0 ? "暂无账号绑定" : string.Join("、", names))}");
        static string Format(double? value) => value.HasValue ? $"{value.Value:0} ms" : "-1";
    }
    private void SaveBinding()
    {
        var account = SelectedAccount(); if (account == null) return; var key = AccountProxyResolver.AccountKeyFor(account);
        var node = _node.SelectedItem as NodeItem;
        _store.SetBinding(new AccountProxyBinding { AccountKey = key, Mode = _mode.SelectedIndex switch { 1 => ProxyBindingMode.FixedNode, 2 => ProxyBindingMode.Disabled, _ => ProxyBindingMode.InheritGlobal }, NodeId = node?.Node.NodeId, FallbackPolicy = _fallback.SelectedIndex == 1 ? ProxyFallbackPolicy.InheritGlobal : ProxyFallbackPolicy.FailClosed });
        ReportStatus($"已保存账号“{account.Name}”的代理绑定（凭据不会显示）。");
    }
    private void EditNode(ProxyNodeRecord? existing)
    {
        using var dialog = new ProxyNodeEditDialog(_palette, existing); if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Node == null) return;
        _store.SetNode(dialog.Node); RefreshNodes(); ReportStatus("代理节点已保存；旧连接池将在下一次请求按节点键淘汰。");
    }
    private void RemoveNode()
    {
        var node = SelectedNode(); if (node == null) return; if (MessageBox.Show($"删除节点“{node.Name}”？已绑定账号将恢复为继承全局。", "代理节点", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        _store.RemoveNode(node.NodeId); RefreshNodes(); LoadBinding();
    }
    private void ToggleNode() { var node = SelectedNode(); if (node == null) return; node.Enabled = !node.Enabled; _store.SetNode(node); RefreshNodes(); ReportStatus(node.Enabled ? "节点已启用。" : "节点已禁用；绑定账号会安全失败。"); }
    private async Task ImportNodesAsync()
    {
        using var dialog = new ProxyImportDialog(_palette); if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        var text = dialog.TextValue.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var subscription) &&
            subscription.Scheme is ("http" or "https") &&
            string.IsNullOrWhiteSpace(subscription.UserInfo) &&
            (subscription.AbsolutePath is not ("" or "/") ||
             !string.IsNullOrWhiteSpace(subscription.Query) ||
             subscription.IsDefaultPort))
        {
            try
            {
                using var client = CreateSubscriptionClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexAccountManager/2026.8.30");
                using var response = await client.GetAsync(subscription, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                text = DecodeSubscription(await response.Content.ReadAsStringAsync());
            }
            catch (TaskCanceledException)
            {
                MessageBox.Show("订阅读取超时（90 秒）。请确认订阅地址有效，并检查系统代理或本地 Xray 是否可用。", "订阅导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (HttpRequestException ex)
            {
                // Do not echo the subscription URL (it may contain a bearer token).
                var detail = ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode.Value}" : "网络连接失败";
                MessageBox.Show("订阅读取失败：" + detail + "。请检查订阅地址、代理和网络连接。", "订阅导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (Exception)
            {
                MessageBox.Show("订阅读取失败：响应内容无法读取。请确认订阅服务返回的是文本订阅。", "订阅导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        // Night Sha Yun/v2rayN subscriptions are often pasted as one Base64
        // blob rather than one URL per line. Decode it before previewing;
        // ordinary URL lists are returned unchanged by DecodeSubscription.
        text = DecodeSubscription(text);
        var preview = _store.PreviewImport(text);
        var nativeCount = preview.Nodes.Count(node => node.IsNativeProtocol);
        if (preview.InvalidEntries.Count > 0 || preview.DuplicateCount > 0 || nativeCount > 0)
        {
            MessageBox.Show(
                $"导入预览：有效 {preview.Nodes.Count}，重复 {preview.DuplicateCount}，无效 {preview.InvalidEntries.Count}。\n" +
                (nativeCount > 0 ? $"其中 {nativeCount} 个 VLESS/VMess/Trojan/SS 节点将通过本机 Xray/sing-box 转换为 127.0.0.1 回环代理。" : "所有节点均为 HTTP/HTTPS/SOCKS5。"),
                "导入预览",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        var added = _store.ImportText(text); RefreshNodes(); ReportStatus($"已导入 {added} 个代理节点；原生订阅协议使用本机 Xray/sing-box 回环转换。");
    }
    private static string DecodeSubscription(string body)
    {
        var trimmed = body.Trim();
        try
        {
            var encoded = trimmed.Replace("\r", "").Replace("\n", "").Replace("-", "+").Replace("_", "/");
            var bytes = Convert.FromBase64String(encoded);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            if (decoded.Contains("://", StringComparison.Ordinal)) return decoded;
        }
        catch { }
        return trimmed;
    }

    private HttpClient CreateSubscriptionClient()
    {
        var settings = new ThemeService(_store.RootPath).LoadSettings();
        var configured = Environment.GetEnvironmentVariable("CODEX_PAT_GATEWAY_PROXY");
        configured = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : (!string.IsNullOrWhiteSpace(settings.PatGatewayProxy)
                ? settings.PatGatewayProxy
                : CodexCliService.BuildPatGatewayProxyUri(settings));
        configured = string.IsNullOrWhiteSpace(configured) ? null : CodexCliService.NormalizeProxyServer(configured);

        if (Uri.TryCreate(configured, UriKind.Absolute, out var proxyUri) &&
            proxyUri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) &&
            proxyUri.Port is > 0 and <= 65535)
        {
            var node = new ProxyNodeRecord { Scheme = "socks5", Address = proxyUri.Host, Port = proxyUri.Port };
            if (!string.IsNullOrWhiteSpace(proxyUri.UserInfo))
            {
                var credentials = Uri.UnescapeDataString(proxyUri.UserInfo).Split(':', 2);
                node.Username = credentials[0];
                if (credentials.Length == 2) node.SetPassword(credentials[1]);
            }
            var resolution = new ProxyResolution(true, proxyUri, null, "subscription:socks5", "");
            var socksClient = ProxyHttpClientFactory.Create(resolution, node);
            socksClient.Timeout = TimeSpan.FromSeconds(90);
            return socksClient;
        }

        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        if (Uri.TryCreate(configured, UriKind.Absolute, out proxyUri) &&
            proxyUri.Scheme is ("http" or "https") && proxyUri.Port is > 0 and <= 65535)
        {
            var webProxy = new WebProxy(proxyUri) { BypassProxyOnLocal = false };
            if (!string.IsNullOrWhiteSpace(proxyUri.UserInfo))
            {
                var credentials = Uri.UnescapeDataString(proxyUri.UserInfo).Split(':', 2);
                webProxy.Credentials = new NetworkCredential(credentials[0], credentials.Length == 2 ? credentials[1] : string.Empty);
            }
            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
    }
    private async Task RunTestGuardedAsync(Func<CancellationToken, Task> operation, string startingText, TimeSpan timeout)
    {
        if (!IsUsable) return;
        if (_testRunning)
        {
            SetMetrics("已有测试正在进行，请等待当前结果。\n\n按钮会在测试完成或超时后恢复。");
            return;
        }

        _testRunning = true;
        _testCancellation?.Dispose();
        _testCancellation = new CancellationTokenSource(timeout);
        var runCancellation = _testCancellation;
        var token = runCancellation.Token;
        _test.Enabled = false;
        _continuous.Enabled = false;
        _testAll.Enabled = false;
        var waitingHint = timeout.TotalSeconds >= 60
            ? "正在并发连接已启用节点，请稍候（会显示实时进度）…"
            : "正在连接节点，请稍候（单节点最长约 8 秒）…";
        SetMetrics(startingText + "\n\n" + waitingHint);
        ReportStatus(startingText);
        try
        {
            await operation(token);
        }
        catch (OperationCanceledException)
        {
            SetMetrics("测试已超时或被取消。\n请检查节点是否启用、核心是否运行，以及远端是否可达。");
            ReportStatus("节点测试已结束：超时或取消；未影响账号轮换。");
        }
        catch (ObjectDisposedException)
        {
            // A page can be rebuilt while an await is in flight. Treat that as a
            // cancelled UI probe instead of surfacing a generic application error.
            if (IsUsable)
            {
                SetMetrics("测试已取消：页面已切换，节点和网关未受影响。");
                ReportStatus("节点测试已取消（页面已切换）。");
            }
        }
        catch (Exception ex)
        {
            if (IsUsable)
            {
                var reason = DescribeTestFailure(ex);
                if (SelectedNode() is { } failedNode) RecordTestFailure(failedNode, reason);
                SetMetrics("测试异常：" + reason);
                ReportStatus("节点测试已结束：" + reason);
            }
        }
        finally
        {
            _testRunning = false;
            if (ReferenceEquals(_testCancellation, runCancellation))
            {
                runCancellation.Dispose();
                _testCancellation = null;
            }
            if (IsUsable)
            {
                _test.Enabled = true;
                _continuous.Enabled = true;
                _testAll.Enabled = true;
            }
        }
    }

    private static string DescribeTestFailure(Exception exception)
    {
        var root = exception;
        while (root.InnerException != null) root = root.InnerException;
        return root switch
        {
            SocketException => "TCP 连接失败：节点拒绝连接或远端主动关闭。",
            HttpRequestException http when http.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || http.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                => "HTTPS/TLS 握手失败：请检查节点 SNI、传输类型和本地核心。",
            HttpRequestException => "HTTPS 请求失败：节点可能不可用或未允许访问目标站点。",
            InvalidOperationException invalid => invalid.Message,
            IOException => "网络数据传输失败：远端提前关闭连接。",
            _ => "节点不可用或测试过程中断。"
        };
    }

    private void RecordTestFailure(ProxyNodeRecord node, string? reason = null)
    {
        try
        {
            node.LastHealthTestAtUtc = DateTimeOffset.UtcNow;
            node.LastTcpMilliseconds = null;
            node.LastTlsMilliseconds = null;
            node.LastFirstResponseMilliseconds = null;
            node.LastHealthError = string.IsNullOrWhiteSpace(reason) ? "最近一次测试失败。" : reason;
            _store.SetNode(node);
            SetListStatus(node);
        }
        catch { /* a failed probe must never become a second UI error */ }
    }

    private void SetListStatus(ProxyNodeRecord node)
    {
        if (!IsUsable) return;
        try
        {
            foreach (ListViewItem item in _nodes.Items)
            {
                if (item.Tag is ProxyNodeRecord listed && listed.NodeId.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase))
                {
                    // The fourth column is latency, not a free-form status field. Keep
                    // failures as -1 and show the diagnostic in the metrics card so a
                    // stale value such as “节点异常” cannot be mistaken for latency.
                    if (item.SubItems.Count > 3) item.SubItems[3].Text = "-1";
                    if (item.SubItems.Count > 4) item.SubItems[4].Text = FormatExitIpForList(node);
                    break;
                }
            }
        }
        catch (Exception) { }
    }

    private async Task TestSelectedAsync(CancellationToken cancellationToken)
    {
        var node = SelectedNode();
        if (node == null)
        {
            SetMetrics("请先在右侧列表选择一个代理节点。\n\n点击节点后，测试按钮会显示 TCP、TLS、首响应和出口 IP。");
            ReportStatus("未选择代理节点。");
            return;
        }
        var started = Stopwatch.StartNew();
        SetMetrics($"正在测试：{node.Name}\nTCP：连接中… · TLS：等待中… · 首响应：等待中…\n出口 IP：检测中…");
        try
        {
            // Native protocol startup invokes a local core and waits for its loopback
            // port. Keep that blocking step off the WinForms UI thread so the button
            // immediately paints “测试中…”, even when the node is dead.
            var resolution = await Task.Run(() => _resolver.ResolveByNode(node), cancellationToken);
            if (!resolution.Success || resolution.ProxyUri == null)
                throw new InvalidOperationException(resolution.Error);

            // Measure the actual route used by HttpClient. Native nodes use the
            // per-node loopback Xray port; ordinary nodes use their proxy port.
            var tcpWatch = Stopwatch.StartNew();
            using (var tcp = new TcpClient()) await tcp.ConnectAsync(resolution.ProxyUri.Host, resolution.ProxyUri.Port, cancellationToken);
            var tcpMs = tcpWatch.Elapsed.TotalMilliseconds;
            SetMetrics($"正在测试：{node.Name}\nTCP：{tcpMs:0} ms · TLS：握手中… · 首响应：等待中…\n出口 IP：检测中…");

            var firstWatch = Stopwatch.StartNew();
            using var client = ProxyHttpClientFactory.Create(resolution, node);
            client.Timeout = TimeSpan.FromSeconds(4);
            using var response = await client.GetAsync("https://chatgpt.com/backend-api/models", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var firstResponseMs = firstWatch.Elapsed.TotalMilliseconds;
            var bodyWatch = Stopwatch.StartNew();
            await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var totalMs = firstResponseMs + bodyWatch.Elapsed.TotalMilliseconds;
            SetMetrics($"正在测试：{node.Name}\nTCP：{tcpMs:0} ms · TLS：{Math.Max(0, firstResponseMs - tcpMs):0} ms · 首响应：{firstResponseMs:0} ms\n出口 IP：检测中… · HTTP {(int)response.StatusCode}");

            string? exitIp = null;
            try
            {
                using var ipResponse = await client.GetAsync("https://api.ipify.org", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var candidate = (await ipResponse.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (IPAddress.TryParse(candidate, out _)) exitIp = candidate;
            }
            catch { /* IP detection is informational and must not invalidate a node. */ }

            node.LastTcpMilliseconds = tcpMs;
            node.LastFirstResponseMilliseconds = firstResponseMs;
            node.LastTlsMilliseconds = Math.Max(0, firstResponseMs - tcpMs);
            node.LastHealthTestAtUtc = DateTimeOffset.UtcNow;
            if (exitIp != null)
            {
                node.ExitIpChanged = node.LastExitIp != null && !node.LastExitIp.Equals(exitIp, StringComparison.Ordinal);
                node.LastExitIp = exitIp;
                node.LastExitIpObservedAtUtc = node.LastHealthTestAtUtc;
            }
            node.LastHealthError = null;
            _store.SetNode(node);
            UpdateNodeMetrics();
            SetMetrics(_metrics.Text + $"\n本次总耗时（含响应体）：{totalMs:0} ms · HTTP {(int)response.StatusCode}\n测试耗时：{started.ElapsedMilliseconds} ms");
            ReportStatus("节点 TCP、TLS、HTTPS 首响应测试完成；出口 IP 检测失败不会误判节点不可用。");
        }
        catch (OperationCanceledException)
        {
            RecordTestFailure(node, "测试超时或已取消");
            SetMetrics($"测试超时或已取消：{node.Name}\n已等待 {started.ElapsedMilliseconds} ms。\n节点可能不可用；账号绑定仍保持安全失败策略。");
            ReportStatus("节点测试已结束：超时或取消；未影响账号轮换。");
        }
        catch (Exception ex)
        {
            var reason = DescribeTestFailure(ex);
            RecordTestFailure(node, reason);
            SetMetrics($"测试失败：{node.Name}\n{reason}\n已耗时 {started.ElapsedMilliseconds} ms。\n账号绑定仍保持安全失败策略。");
            ReportStatus("代理节点测试失败：" + reason);
        }
    }

    private async Task ContinuousTestAsync(CancellationToken cancellationToken)
    {
        const int sampleCount = 3;
        var node = SelectedNode();
        if (node == null)
        {
            SetMetrics($"请先在右侧列表选择一个代理节点。\n\n连续测速会采集 {sampleCount} 次真实 HTTPS 首响应并计算 P50/P90。");
            ReportStatus("未选择代理节点。");
            return;
        }
        var started = Stopwatch.StartNew();
        SetMetrics($"正在连续测速：{node.Name}\n已完成 0/{sampleCount} · TCP、HTTPS 首响应采样中…");
        var resolution = await Task.Run(() => _resolver.ResolveByNode(node), cancellationToken);
        if (!resolution.Success || resolution.ProxyUri == null)
        {
            RecordTestFailure(node, resolution.Error);
            SetMetrics("连续测速无法开始：" + resolution.Error + "\n账号绑定仍保持安全失败策略。");
            ReportStatus("连续测速无法开始：" + resolution.Error);
            return;
        }

        var samples = new List<double>();
        var tcpSamples = new List<double>();
        using var client = ProxyHttpClientFactory.Create(resolution, node);
        client.Timeout = TimeSpan.FromSeconds(3);
        for (var i = 0; i < sampleCount; i++)
        {
            try
            {
                var tcpWatch = Stopwatch.StartNew();
                using (var tcp = new TcpClient()) await tcp.ConnectAsync(resolution.ProxyUri.Host, resolution.ProxyUri.Port, cancellationToken);
                tcpSamples.Add(tcpWatch.Elapsed.TotalMilliseconds);
                var firstWatch = Stopwatch.StartNew();
                using var response = await client.GetAsync("https://chatgpt.com/backend-api/models", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                samples.Add(firstWatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* retain successful samples when one probe is transiently unavailable */ }
            SetMetrics($"正在连续测速：{node.Name}\n已完成 {i + 1}/{sampleCount} · 成功 HTTPS 样本 {samples.Count} · TCP 样本 {tcpSamples.Count}…");
            await Task.Delay(80, cancellationToken);
        }
        if (samples.Count == 0)
        {
            RecordTestFailure(node);
            SetMetrics($"连续测速失败：{sampleCount} 次均未获得 HTTPS 首响应。\n已耗时 {started.ElapsedMilliseconds} ms。\n请检查节点是否可用或先更换节点。");
            ReportStatus("连续测速完成：没有成功样本；未影响账号轮换。");
            return;
        }
        samples.Sort(); tcpSamples.Sort();
        double Percentile(IReadOnlyList<double> values, double p) => values[(int)Math.Clamp(Math.Ceiling((values.Count - 1) * p), 0, values.Count - 1)];
        node.LastFirstResponseMilliseconds = Percentile(samples, .50);
        node.LastTcpMilliseconds = tcpSamples.Count == 0 ? null : Percentile(tcpSamples, .50);
        node.LastTlsMilliseconds = node.LastTcpMilliseconds.HasValue ? Math.Max(0, node.LastFirstResponseMilliseconds.Value - node.LastTcpMilliseconds.Value) : null;
        node.LastHealthTestAtUtc = DateTimeOffset.UtcNow;
        node.LastHealthError = null;
        _store.SetNode(node);
        UpdateNodeMetrics();
        SetMetrics(_metrics.Text + $"\n连续 HTTPS 首响应：{samples.Count}/{sampleCount}\nP50：{Percentile(samples, .50):0} ms · P90：{Percentile(samples, .90):0} ms\nTCP P50：{(tcpSamples.Count == 0 ? "-1" : Percentile(tcpSamples, .50).ToString("0") + " ms")} · 总耗时：{started.ElapsedMilliseconds} ms\n测试目标：chatgpt.com/backend-api/models");
        ReportStatus("连续测速完成；P50/P90 现在统计真实 HTTPS 首响应，不再只是 TCP 粗测。");
    }

    private sealed record NodeProbeResult(
        ProxyNodeRecord Node,
        bool Success,
        double? TcpMilliseconds,
        double? TlsMilliseconds,
        double? FirstResponseMilliseconds,
        string? ExitIp,
        int? HttpStatus,
        string Error);

    private async Task<NodeProbeResult> ProbeNodeAsync(
        ProxyNodeRecord node,
        CancellationToken cancellationToken,
        AccountProxyResolver? probeResolver = null)
    {
        try
        {
            // Keep native-core startup away from the UI thread. The cancellation
            // token still bounds the await even if a third-party core is slow to
            // exit internally.
            var resolver = probeResolver ?? _resolver;
            var resolutionTask = Task.Run(() => resolver.ResolveByNode(node));
            var resolution = await resolutionTask.WaitAsync(cancellationToken);
            if (!resolution.Success || resolution.ProxyUri == null)
                return new(node, false, null, null, null, null, null, resolution.Error);

            var tcpWatch = Stopwatch.StartNew();
            using (var tcp = new TcpClient())
                await tcp.ConnectAsync(resolution.ProxyUri.Host, resolution.ProxyUri.Port, cancellationToken);
            var tcpMs = tcpWatch.Elapsed.TotalMilliseconds;

            using var client = ProxyHttpClientFactory.Create(resolution, node);
            client.Timeout = TimeSpan.FromSeconds(4);
            var firstWatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                "https://chatgpt.com/backend-api/models",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var firstMs = firstWatch.Elapsed.TotalMilliseconds;

            // Exit-IP lookup is informational. A failure here must not turn a
            // reachable proxy into a failed node.
            string? exitIp = null;
            try
            {
                using var ipResponse = await client.GetAsync(
                    "https://api.ipify.org",
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                var candidate = (await ipResponse.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (IPAddress.TryParse(candidate, out _)) exitIp = candidate;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch { }

            return new(
                node,
                true,
                tcpMs,
                Math.Max(0, firstMs - tcpMs),
                firstMs,
                exitIp,
                (int)response.StatusCode,
                string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(node, false, null, null, null, null, null, DescribeTestFailure(ex));
        }
    }

    private async Task TestAllAsync(CancellationToken cancellationToken)
    {
        var nodes = _store.LoadNodes().Where(node => node.Enabled).ToList();
        if (nodes.Count == 0)
        {
            SetMetrics("没有可测试的已启用节点。\n\n请先导入或添加节点，并确认节点处于启用状态。");
            ReportStatus("一键测速未开始：没有可用的已启用节点。");
            return;
        }

        var completed = 0;
        var successful = 0;
        using var slots = new SemaphoreSlim(4, 4);
        var progress = new Progress<string>(SetMetrics);
        var tasks = nodes.Select(async node =>
        {
            var acquired = false;
            try
            {
                await slots.WaitAsync(cancellationToken);
                acquired = true;
                // Each bulk-probe worker owns its resolver/core instance. Native
                // subscription startup is otherwise serialized by the shared
                // resolver lock and a long list would appear to hang.
                using var probeResolver = new AccountProxyResolver(_store.RootPath);
                var result = await ProbeNodeAsync(node, cancellationToken, probeResolver);
                if (result.Success) Interlocked.Increment(ref successful);
                var done = Interlocked.Increment(ref completed);
                ((IProgress<string>)progress).Report($"正在一键测速全部节点…\n已完成 {done}/{nodes.Count} · 成功 {successful} · 失败 {done - successful}\n最近完成：{node.Name}");
                return result;
            }
            finally
            {
                if (acquired) slots.Release();
            }
        }).ToList();

        NodeProbeResult[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        finally
        {
            // Do not leave one native core process per subscription node after a
            // bulk probe. This resolver belongs only to the sidebar; the gateway
            // has an independent resolver and is never touched here.
            try { _resolver.ResetNativeCores(); } catch { }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var result in results)
        {
            var node = result.Node;
            node.LastHealthTestAtUtc = now;
            if (!result.Success)
            {
                node.LastTcpMilliseconds = null;
                node.LastTlsMilliseconds = null;
                node.LastFirstResponseMilliseconds = null;
                node.LastHealthError = string.IsNullOrWhiteSpace(result.Error) ? "节点不可用。" : result.Error;
                continue;
            }

            node.LastTcpMilliseconds = result.TcpMilliseconds;
            node.LastTlsMilliseconds = result.TlsMilliseconds;
            node.LastFirstResponseMilliseconds = result.FirstResponseMilliseconds;
            node.LastHealthError = null;
            if (result.ExitIp != null)
            {
                node.ExitIpChanged = node.LastExitIp != null &&
                    !node.LastExitIp.Equals(result.ExitIp, StringComparison.Ordinal);
                node.LastExitIp = result.ExitIp;
                node.LastExitIpObservedAtUtc = now;
            }
        }
        _store.SaveNodes(nodes);
        RefreshNodes();

        var failed = results.Length - results.Count(result => result.Success);
        var successfulResponses = results.Where(result => result.Success && result.FirstResponseMilliseconds.HasValue)
            .Select(result => result.FirstResponseMilliseconds!.Value).ToList();
        var average = successfulResponses.Count == 0 ? "—" : $"{successfulResponses.Average():0} ms";
        var fastest = results.Where(result => result.Success && result.FirstResponseMilliseconds.HasValue)
            .OrderBy(result => result.FirstResponseMilliseconds)
            .Select(result => result.Node.Name)
            .FirstOrDefault();
        SetMetrics(fastest == null
            ? $"一键测速完成：成功 0/{results.Length}，失败 {failed}。\n所有节点均未获得 ChatGPT HTTPS 首响应；节点状态已标记为‘节点异常’。\n账号绑定和自动轮换未受影响。"
            : $"一键测速完成：成功 {results.Length - failed}/{results.Length}，失败 {failed}。\n平均 HTTPS 首响应：{average}\n最快节点：{fastest}\n出口 IP 检测失败不会误判节点不可用。\n账号绑定和自动轮换未受影响。");
        ReportStatus($"一键测速完成：成功 {results.Length - failed}，失败 {failed}；未影响账号轮换。");
    }

    private sealed record NodeItem(ProxyNodeRecord Node) { public override string ToString() => $"{Node.Name} · {(Node.Enabled ? "启用" : "禁用")}"; }
}

internal sealed class ProxyNodeEditDialog : Form
{
    public ProxyNodeRecord? Node { get; private set; }
    private readonly TextBox _name = new(), _scheme = new(), _address = new(), _port = new(), _user = new(), _password = new();
    public ProxyNodeEditDialog(ThemePalette palette, ProxyNodeRecord? existing)
    {
        Text = existing == null ? "添加代理节点" : "编辑代理节点";
        ClientSize = new Size(620, 430);
        MinimumSize = new Size(540, 390);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = palette.SurfaceColor;
        var fields = new[] { ("名称", _name), ("协议", _scheme), ("地址", _address), ("端口", _port), ("用户名", _user), ("密码", _password) };
        for (var i = 0; i < fields.Length; i++)
        {
            var label = new Label
            {
                Text = fields[i].Item1,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = palette.TextColor,
                BackColor = Color.Transparent,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9F, FontStyle.Bold)
            };
            label.SetBounds(28, 26 + i * 48, 82, 34);
            Controls.Add(label);
            var input = fields[i].Item2;
            input.SetBounds(122, 24 + i * 48, ClientSize.Width - 150, 38);
            input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            input.BackColor = palette.InputBackColor;
            input.ForeColor = palette.TextColor;
            input.Font = new Font(SystemFonts.DefaultFont.FontFamily, 9F);
            Controls.Add(input);
        }
        _password.UseSystemPasswordChar = true;
        _name.Text = existing?.Name ?? "新节点";
        _scheme.Text = existing?.Scheme ?? "http";
        _address.Text = existing?.Address ?? "";
        _port.Text = existing?.Port.ToString() ?? "8080";
        _user.Text = existing?.Username ?? "";
        var save = new ModernButton
        {
            Text = "保存节点",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Radius = 11
        };
        save.SetBounds(ClientSize.Width - 178, ClientSize.Height - 58, 150, 42);
        ThemeStyler.ApplyPrimaryButton(save, palette);
        save.Click += (_, _) =>
        {
            if (!int.TryParse(_port.Text, out var port)) { DialogResult = DialogResult.None; return; }
            var node = existing ?? new ProxyNodeRecord();
            node.Name = _name.Text.Trim();
            node.Scheme = _scheme.Text.Trim().ToLowerInvariant();
            node.Address = _address.Text.Trim();
            node.Port = port;
            node.Username = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();
            if (!string.IsNullOrEmpty(_password.Text)) node.SetPassword(_password.Text);
            if (!node.TryValidate(out var validationError))
            {
                MessageBox.Show(validationError, "节点配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            Node = node;
        };
        Controls.Add(save);
        AcceptButton = save;
    }
}

internal sealed class ProxyImportDialog : Form
{
    private readonly TextBox _text = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    public string TextValue => _text.Text;
    public ProxyImportDialog(ThemePalette palette)
    {
        Text = "批量导入代理 URL";
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(620, 440);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = palette.SurfaceColor;
        _text.Multiline = true;
        _text.ScrollBars = ScrollBars.Both;
        _text.AcceptsTab = true;
        _text.WordWrap = false;
        _text.Font = new Font(SystemFonts.DefaultFont.FontFamily, 9.5F);
        _text.SetBounds(24, 24, ClientSize.Width - 48, ClientSize.Height - 142);
        _text.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _text.BackColor = palette.InputBackColor;
        _text.ForeColor = palette.TextColor;
        Controls.Add(_text);
        var hint = new Label
        {
            Text = "每行一个：http://、https://、socks5://；支持批量粘贴或订阅 URL。导入预览会隐藏密码。",
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = palette.MutedTextColor,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 8.7F)
        };
        hint.SetBounds(24, ClientSize.Height - 106, ClientSize.Width - 48, 30);
        Controls.Add(hint);
        var ok = new ModernButton
        {
            Text = "预览并导入",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Radius = 11
        };
        ok.SetBounds(ClientSize.Width - 184, ClientSize.Height - 62, 160, 42);
        ThemeStyler.ApplyPrimaryButton(ok, palette);
        Controls.Add(ok);
        AcceptButton = ok;
    }
}
