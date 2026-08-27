namespace CodexAccountManager;

public partial class Form1
{
    private const int AccountRotationHorizontalMinWidth = 720;

    private void RenderAccountRotationWorkspace(string query, int workspaceWidth)
    {
        var primaryAccounts = AccountRotationConfiguration.GetOrderedAccounts(
            _appSettings,
            _accounts,
            AccountRotationPool.Primary);
        var backupAccounts = AccountRotationConfiguration.GetOrderedAccounts(
            _appSettings,
            _accounts,
            AccountRotationPool.Backup);
        var unassignedAccounts = _accounts
            .Where(account =>
                AccountRotationConfiguration.GetPool(_appSettings, account) == AccountRotationPool.None)
            .ToList();

        _cardsPanel.Controls.Add(CreateAccountRotationSummary(workspaceWidth));
        AddAccountRotationSection(
            "使用轮换池",
            AccountRotationPool.Primary,
            primaryAccounts,
            query,
            workspaceWidth,
            _appSettings.AccountRotationPrimaryCursorAccountKey);
        AddAccountRotationSection(
            "备用轮换池",
            AccountRotationPool.Backup,
            backupAccounts,
            query,
            workspaceWidth,
            _appSettings.AccountRotationBackupCursorAccountKey);
        AddAccountRotationSection(
            "未参与账号",
            AccountRotationPool.None,
            unassignedAccounts,
            query,
            workspaceWidth,
            null);
    }

    private Control CreateAccountRotationSummary(int width)
    {
        var horizontal = width >= 620;
        var enabled = AccountRotationConfiguration.IsEnabled(_appSettings);
        var primaryCount = _accounts.Count(account =>
            AccountRotationConfiguration.GetPool(_appSettings, account) == AccountRotationPool.Primary);
        var backupCount = _accounts.Count(account =>
            AccountRotationConfiguration.GetPool(_appSettings, account) == AccountRotationPool.Backup);
        var noneCount = Math.Max(0, _accounts.Count - primaryCount - backupCount);
        var accent = enabled ? _palette.SuccessColor : _palette.MutedTextColor;
        var panel = new RoundedPanel
        {
            Width = width,
            Height = horizontal ? 122 : 158,
            Radius = 16,
            BorderColor = UiDesign.Blend(_palette.BorderColor, accent, 0.22F),
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, accent, 0.035F),
            AccentColor = accent,
            AccentWidth = 4,
            ShadowColor = Color.FromArgb(18, _palette.ShadowColor),
            Margin = new Padding(0, 0, 0, 16),
            AccessibleName = enabled ? "账号轮换已开启" : "账号轮换已关闭"
        };

        var rightReserve = horizontal ? 174 : 24;
        var title = new Label
        {
            Text = "轮换状态",
            Left = 24,
            Top = 14,
            Width = Math.Max(180, width - 48 - rightReserve),
            Height = 30,
            Font = new Font(Font.FontFamily, 10.4F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var counts = new Label
        {
            Text = $"使用 {primaryCount}  ·  备用 {backupCount}  ·  未参与 {noneCount}",
            Left = 24,
            Top = 47,
            Width = Math.Max(220, width - 48 - (horizontal ? rightReserve : 0)),
            Height = 27,
            Font = new Font(Font.FontFamily, 8.9F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(counts, _palette, true);
        panel.Controls.Add(counts);

        var cursorSummary = new Label
        {
            Text = BuildAccountRotationCursorSummary(),
            Left = 24,
            Top = 77,
            Width = Math.Max(220, width - 48 - (horizontal ? rightReserve : 0)),
            Height = 26,
            Font = new Font(Font.FontFamily, 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(cursorSummary, _palette, true);
        _toolTip.SetToolTip(cursorSummary, cursorSummary.Text);
        panel.Controls.Add(cursorSummary);

        var switchTop = horizontal ? 25 : 110;
        var switchLeft = horizontal ? width - 80 : 24;
        var state = new Label
        {
            Text = enabled ? "开启" : "关闭",
            Left = horizontal ? width - 166 : 88,
            Top = switchTop,
            Width = 76,
            Height = 30,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            TextAlign = horizontal ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(state, _palette, !enabled);
        panel.Controls.Add(state);

        var toggle = new ModernToggleSwitch
        {
            Left = switchLeft,
            Top = switchTop,
            Width = 56,
            Height = 30,
            Text = string.Empty,
            Checked = enabled,
            OnTrackColor = _palette.SuccessColor,
            OffTrackColor = UiDesign.Blend(_palette.DisabledColor, _palette.MutedTextColor, 0.16F),
            KnobColor = _palette.CardColor,
            TextColor = _palette.TextColor,
            BorderColor = UiDesign.Blend(_palette.BorderColor, accent, 0.30F),
            BackColor = panel.BackColor,
            AccessibleName = "开启账号轮换"
        };
        _toolTip.SetToolTip(toggle, enabled ? "关闭账号轮换" : "开启账号轮换");
        toggle.CheckedChanged += async (_, _) =>
            await SetAccountRotationEnabledFromUiAsync(toggle.Checked);
        panel.Controls.Add(toggle);

        return panel;
    }

    private string BuildAccountRotationCursorSummary()
    {
        var primary = ResolveAccountRotationCursorName(
            _appSettings.AccountRotationPrimaryCursorAccountKey);
        var backup = ResolveAccountRotationCursorName(
            _appSettings.AccountRotationBackupCursorAccountKey);
        return $"上次位置：使用 {primary}  ·  备用 {backup}";
    }

    private string ResolveAccountRotationCursorName(string? accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            return "未开始";
        }

        return _accounts.FirstOrDefault(account =>
                   QuotaAccountIdentity.CreateKey(account).Equals(
                       accountKey,
                       StringComparison.Ordinal))?.Name ?? "未开始";
    }

    private void AddAccountRotationSection(
        string title,
        AccountRotationPool pool,
        IReadOnlyList<AccountRecord> allAccounts,
        string query,
        int width,
        string? cursorAccountKey)
    {
        var visibleAccounts = allAccounts
            .Where(account => MatchesAccountRotationSearch(account, query))
            .ToList();
        _cardsPanel.Controls.Add(CreateAccountRotationSectionHeader(
            title,
            pool,
            visibleAccounts.Count,
            allAccounts.Count,
            width,
            cursorAccountKey,
            !string.IsNullOrWhiteSpace(query)));

        if (visibleAccounts.Count == 0)
        {
            _cardsPanel.Controls.Add(CreateAccountRotationEmptyRow(
                width,
                string.IsNullOrWhiteSpace(query) ? "尚无账号" : "没有匹配账号"));
            return;
        }

        foreach (var account in visibleAccounts)
        {
            var globalIndex = IndexOfAccount(allAccounts, account);
            _cardsPanel.Controls.Add(CreateAccountRotationRow(
                account,
                pool,
                globalIndex,
                allAccounts.Count,
                width));
        }
    }

    private Control CreateAccountRotationSectionHeader(
        string title,
        AccountRotationPool pool,
        int visibleCount,
        int totalCount,
        int width,
        string? cursorAccountKey,
        bool filtered)
    {
        var accent = GetAccountRotationPoolColor(pool);
        var countWidth = filtered ? 84 : 70;
        var countLeft = Math.Max(210, width - countWidth - 14);
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 52,
            Radius = 12,
            BorderColor = Color.FromArgb(48, accent),
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceColor, accent, 0.028F),
            AccentColor = accent,
            AccentWidth = 3,
            Margin = new Padding(0, 2, 0, 8),
            AccessibleName = $"{title}，{totalCount} 个账号"
        };

        var titleLabel = new Label
        {
            Text = title,
            Left = 20,
            Top = 5,
            Width = Math.Max(140, Math.Min(230, countLeft - 32)),
            Height = 40,
            Font = new Font(Font.FontFamily, 9.6F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(titleLabel, _palette);
        panel.Controls.Add(titleLabel);

        if (pool != AccountRotationPool.None && width >= 620)
        {
            var cursorName = ResolveAccountRotationCursorName(cursorAccountKey);
            var cursor = new Label
            {
                Text = $"上次：{cursorName}",
                Left = 250,
                Top = 8,
                Width = Math.Max(100, countLeft - 262),
                Height = 34,
                Font = new Font(Font.FontFamily, 8.4F),
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true,
                UseMnemonic = false
            };
            ThemeStyler.ApplyLabel(cursor, _palette, true);
            _toolTip.SetToolTip(cursor, $"上次使用位置：{cursorName}");
            panel.Controls.Add(cursor);
        }

        var countText = filtered ? $"{visibleCount}/{totalCount}" : $"{totalCount} 个";
        var count = MakeBadge(
            countText,
            countLeft,
            11,
            Color.FromArgb(24, accent),
            accent);
        count.Width = countWidth;
        count.Height = 28;
        panel.Controls.Add(count);
        return panel;
    }

    private Control CreateAccountRotationEmptyRow(int width, string text)
    {
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 62,
            Radius = 12,
            BorderColor = _palette.BorderColor,
            BackColor = _palette.SurfaceAltColor,
            Margin = new Padding(0, 0, 0, 14)
        };
        var label = new Label
        {
            Text = text,
            Left = 22,
            Top = 10,
            Width = Math.Max(160, width - 44),
            Height = 40,
            Font = new Font(Font.FontFamily, 8.8F),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(label, _palette, true);
        panel.Controls.Add(label);
        return panel;
    }

    private Control CreateAccountRotationRow(
        AccountRecord account,
        AccountRotationPool pool,
        int index,
        int count,
        int width)
    {
        var horizontal = width >= AccountRotationHorizontalMinWidth;
        var accent = GetAccountRotationPoolColor(pool);
        var row = new RoundedPanel
        {
            Width = width,
            Height = horizontal ? 94 : 148,
            Radius = 14,
            BorderColor = UiDesign.Blend(_palette.BorderColor, accent, 0.14F),
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, accent, 0.018F),
            AccentColor = accent,
            AccentWidth = 3,
            ShadowColor = Color.FromArgb(14, _palette.ShadowColor),
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = $"{account.Name}，{GetAccountRotationPoolLabel(pool)}"
        };

        var orderBadge = MakeBadge(
            pool == AccountRotationPool.None ? "—" : $"#{index + 1}",
            20,
            15,
            Color.FromArgb(24, accent),
            accent);
        orderBadge.Width = 46;
        orderBadge.Height = 30;
        row.Controls.Add(orderBadge);

        var actionWidth = horizontal ? 230 : Math.Min(280, Math.Max(230, width - 44));
        var summaryWidth = horizontal
            ? Math.Max(180, width - 94 - actionWidth - 26)
            : Math.Max(160, width - 98);
        var name = new Label
        {
            Text = account.Name,
            Left = 78,
            Top = 10,
            Width = summaryWidth,
            Height = 34,
            Font = new Font(Font.FontFamily, 9.7F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(name, _palette);
        row.Controls.Add(name);

        var detail = new Label
        {
            Text = BuildAccountRotationAccountDetail(account),
            Left = 78,
            Top = 44,
            Width = horizontal ? summaryWidth : Math.Max(160, width - 102),
            Height = horizontal ? 31 : 38,
            Font = new Font(Font.FontFamily, 8.4F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(detail, _palette, true);
        _toolTip.SetToolTip(detail, detail.Text);
        row.Controls.Add(detail);

        var arrowWidth = 42;
        var gap = 8;
        var poolButtonWidth = horizontal
            ? 130
            : Math.Min(168, Math.Max(130, actionWidth - (arrowWidth * 2) - (gap * 2)));
        var controlsWidth = poolButtonWidth + (arrowWidth * 2) + (gap * 2);
        var controlsLeft = width - 22 - controlsWidth;
        var controlsTop = horizontal ? 26 : 94;

        var poolButton = MakeActionButton(
            GetAccountRotationPoolLabel(pool),
            controlsLeft,
            controlsTop,
            poolButtonWidth,
            primary: pool == AccountRotationPool.Primary);
        poolButton.Height = 42;
        poolButton.AccessibleName = $"设置 {account.Name} 的轮换池";
        _toolTip.SetToolTip(poolButton, "切换轮换池");
        poolButton.Click += (_, _) => CycleAccountRotationPool(account);
        row.Controls.Add(poolButton);

        var canMove = pool != AccountRotationPool.None;
        var up = CreateAccountRotationMoveButton(
            "↑",
            controlsLeft + poolButtonWidth + gap,
            controlsTop,
            "上移",
            canMove && index > 0);
        up.Click += (_, _) => MoveAccountRotationAccount(account, -1);
        row.Controls.Add(up);

        var down = CreateAccountRotationMoveButton(
            "↓",
            controlsLeft + poolButtonWidth + gap + arrowWidth + gap,
            controlsTop,
            "下移",
            canMove && index >= 0 && index < count - 1);
        down.Click += (_, _) => MoveAccountRotationAccount(account, 1);
        row.Controls.Add(down);

        return row;
    }

    private Button CreateAccountRotationMoveButton(
        string symbol,
        int left,
        int top,
        string accessibleName,
        bool enabled)
    {
        var button = MakeActionButton(symbol, left, top, 42, primary: false);
        button.Height = 42;
        button.Padding = Padding.Empty;
        button.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        button.Enabled = enabled;
        button.AccessibleName = accessibleName;
        _toolTip.SetToolTip(button, accessibleName);
        return button;
    }

    private string BuildAccountRotationAccountDetail(AccountRecord account)
    {
        var parts = new List<string> { account.AuthKindLabel };
        if (IsCurrentAccount(account))
        {
            parts.Add("当前使用");
        }

        var accountKey = QuotaAccountIdentity.CreateKey(account);
        if (_appSettings.AccountRotationResetAtUtc.TryGetValue(accountKey, out var resetAtUtc))
        {
            var availableAt = resetAtUtc + AccountRotationConfiguration.PrimaryResetGracePeriod;
            parts.Add(availableAt > DateTimeOffset.UtcNow
                ? $"恢复 {availableAt.ToLocalTime():MM-dd HH:mm}"
                : "已恢复");
        }

        return string.Join("  ·  ", parts);
    }

    private static int IndexOfAccount(
        IReadOnlyList<AccountRecord> accounts,
        AccountRecord account)
    {
        var key = QuotaAccountIdentity.CreateKey(account);
        for (var index = 0; index < accounts.Count; index++)
        {
            if (QuotaAccountIdentity.CreateKey(accounts[index]).Equals(key, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool MatchesAccountRotationSearch(AccountRecord account, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return account.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               account.CodexHome.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               account.AuthKindLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               account.ApiProviderName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               account.ApiModel.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private Color GetAccountRotationPoolColor(AccountRotationPool pool) => pool switch
    {
        AccountRotationPool.Primary => _palette.SuccessColor,
        AccountRotationPool.Backup => _palette.SecondaryAccentColor,
        _ => _palette.MutedTextColor
    };

    private static string GetAccountRotationPoolLabel(AccountRotationPool pool) => pool switch
    {
        AccountRotationPool.Primary => "使用轮换池",
        AccountRotationPool.Backup => "备用轮换池",
        _ => "不参与"
    };

    private async Task SetAccountRotationEnabledFromUiAsync(bool enabled)
    {
        var previousEnabled = _appSettings.AccountRotationEnabled;
        var previousLegacyEnabled = _appSettings.PatAutoRotationEnabled;
        AccountRotationConfiguration.SetEnabled(_appSettings, enabled);
        if (!TrySaveAppSettings(out var error))
        {
            _appSettings.AccountRotationEnabled = previousEnabled;
            _appSettings.PatAutoRotationEnabled = previousLegacyEnabled;
            ShowAccountRotationSaveError(error);
            RerenderAccountRotationWorkspacePreservingScroll();
            return;
        }

        if (!enabled)
        {
            CancelPendingPatAutoRotation(resetState: true);
            await CancelArmedPatGatewayRotationAsync();
        }
        UpdatePatAutoRotationControls();
        _statusBox.Text = enabled
            ? "账号轮换已开启。"
            : "账号轮换已关闭；保持当前账号。";
        RerenderAccountRotationWorkspacePreservingScroll();
    }

    private void CycleAccountRotationPool(AccountRecord account)
    {
        var pools = new Dictionary<string, string>(
            _appSettings.AccountRotationPools,
            StringComparer.Ordinal);
        var primaryOrder = _appSettings.AccountRotationPrimaryOrder.ToList();
        var backupOrder = _appSettings.AccountRotationBackupOrder.ToList();
        var primaryCursor = _appSettings.AccountRotationPrimaryCursorAccountKey;
        var backupCursor = _appSettings.AccountRotationBackupCursorAccountKey;
        var current = AccountRotationConfiguration.GetPool(_appSettings, account);
        var next = current switch
        {
            AccountRotationPool.None => AccountRotationPool.Primary,
            AccountRotationPool.Primary => AccountRotationPool.Backup,
            _ => AccountRotationPool.None
        };

        AccountRotationConfiguration.SetPool(_appSettings, _accounts, account, next);
        if (!TrySaveAppSettings(out var error))
        {
            _appSettings.AccountRotationPools = pools;
            _appSettings.AccountRotationPrimaryOrder = primaryOrder;
            _appSettings.AccountRotationBackupOrder = backupOrder;
            _appSettings.AccountRotationPrimaryCursorAccountKey = primaryCursor;
            _appSettings.AccountRotationBackupCursorAccountKey = backupCursor;
            ShowAccountRotationSaveError(error);
            RerenderAccountRotationWorkspacePreservingScroll();
            return;
        }

        _statusBox.Text = $"{account.Name} 已设为{GetAccountRotationPoolLabel(next)}。";
        RerenderAccountRotationWorkspacePreservingScroll();
    }

    private void MoveAccountRotationAccount(AccountRecord account, int offset)
    {
        var previousPrimary = _appSettings.AccountRotationPrimaryOrder.ToList();
        var previousBackup = _appSettings.AccountRotationBackupOrder.ToList();
        AccountRotationConfiguration.Move(_appSettings, account, offset);
        if (previousPrimary.SequenceEqual(
                _appSettings.AccountRotationPrimaryOrder,
                StringComparer.Ordinal) &&
            previousBackup.SequenceEqual(
                _appSettings.AccountRotationBackupOrder,
                StringComparer.Ordinal))
        {
            return;
        }

        if (!TrySaveAppSettings(out var error))
        {
            _appSettings.AccountRotationPrimaryOrder = previousPrimary;
            _appSettings.AccountRotationBackupOrder = previousBackup;
            ShowAccountRotationSaveError(error);
            RerenderAccountRotationWorkspacePreservingScroll();
            return;
        }

        _statusBox.Text = $"已调整 {account.Name} 的轮换顺序。";
        RerenderAccountRotationWorkspacePreservingScroll();
    }

    private void RerenderAccountRotationWorkspacePreservingScroll()
    {
        var verticalOffset = Math.Max(0, -_cardsPanel.AutoScrollPosition.Y);
        RenderCards();
        _cardsPanel.PerformLayout();
        _cardsPanel.AutoScrollPosition = new Point(0, verticalOffset);
    }

    private void ShowAccountRotationSaveError(string? error)
    {
        var message = "账号轮换设置未能保存。" +
                      (string.IsNullOrWhiteSpace(error) ? string.Empty : $"\r\n\r\n{error}");
        _statusBox.Text = "账号轮换设置保存失败。";
        MessageBox.Show(
            this,
            message,
            "账号轮换",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }
}
