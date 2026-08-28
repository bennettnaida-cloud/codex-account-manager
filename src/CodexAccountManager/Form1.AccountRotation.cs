namespace CodexAccountManager;

public partial class Form1
{
    // Two explicit actions (pool toggle + None) and the order arrows need a little
    // more room than the old single three-state button.  Below this width the row
    // stacks its actions instead of clipping the account identity.
    private const int AccountRotationHorizontalMinWidth = 900;

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
        var rightReserve = horizontal ? 174 : 0;
        var summaryTextWidth = Math.Max(120, width - 48 - rightReserve);
        var countsText = $"使用 {primaryCount}  ·  备用 {backupCount}  ·  未参与 {noneCount}";
        using var countsMeasurementFont = new Font(Font.FontFamily, 8.9F);
        var countsHeight = MeasureAccountRotationWrappedTextHeight(
            countsText,
            countsMeasurementFont,
            summaryTextWidth,
            27);
        var cursorSummaryTop = 47 + countsHeight + 3;
        var cursorSummaryText = BuildAccountRotationCursorSummary();
        using var cursorSummaryMeasurementFont = new Font(Font.FontFamily, 8.5F);
        var cursorSummaryHeight = MeasureAccountRotationWrappedTextHeight(
            cursorSummaryText,
            cursorSummaryMeasurementFont,
            summaryTextWidth,
            26);
        var switchTop = horizontal ? 25 : cursorSummaryTop + cursorSummaryHeight + 10;
        var panelHeight = horizontal
            ? Math.Max(122, cursorSummaryTop + cursorSummaryHeight + 18)
            : switchTop + 48;
        var panel = new RoundedPanel
        {
            Width = width,
            Height = panelHeight,
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

        var title = new Label
        {
            Text = "轮换状态",
            Left = 24,
            Top = 14,
            Width = summaryTextWidth,
            Height = 30,
            Font = new Font(Font.FontFamily, 10.4F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var counts = new Label
        {
            Text = countsText,
            Left = 24,
            Top = 47,
            Width = summaryTextWidth,
            Height = countsHeight,
            Font = new Font(Font.FontFamily, 8.9F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(counts, _palette, true);
        panel.Controls.Add(counts);

        var cursorSummary = new Label
        {
            Text = cursorSummaryText,
            Left = 24,
            Top = cursorSummaryTop,
            Width = summaryTextWidth,
            Height = cursorSummaryHeight,
            Font = new Font(Font.FontFamily, 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(cursorSummary, _palette, true);
        _toolTip.SetToolTip(cursorSummary, cursorSummary.Text);
        panel.Controls.Add(cursorSummary);

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
            Height = horizontal ? 94 : 188,
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

        const int arrowWidth = 42;
        const int gap = 8;
        const int noneButtonWidth = 92;
        var poolButtonWidth = horizontal
            ? Math.Max(176, MeasureAccountRotationPoolActionWidth())
            : Math.Min(
                Math.Max(150, MeasureAccountRotationPoolActionWidth()),
                Math.Max(
                    132,
                    Math.Min(360, Math.Max(300, width - 44)) -
                    noneButtonWidth -
                    (arrowWidth * 2) -
                    (gap * 3)));
        var controlsWidth = poolButtonWidth + noneButtonWidth + (arrowWidth * 2) + (gap * 3);
        var actionWidth = controlsWidth;
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

        var controlsLeft = horizontal
            ? width - 22 - controlsWidth
            : Math.Max(22, (width - controlsWidth) / 2);
        var controlsTop = horizontal ? 26 : 132;

        var poolToggleTarget =
            AccountRotationConfiguration.GetInteractivePoolToggleTarget(pool);
        var poolButtonLabel = GetAccountRotationPoolButtonLabel(pool);
        var poolButton = MakeActionButton(
            poolButtonLabel,
            controlsLeft,
            controlsTop,
            poolButtonWidth,
            primary: pool != AccountRotationPool.Backup);
        poolButton.Height = 42;
        poolButton.AccessibleName =
            $"将 {account.Name} 切换到{GetAccountRotationPoolLabel(poolToggleTarget)}";
        _toolTip.SetToolTip(poolButton, pool == AccountRotationPool.None
            ? "点击加入使用轮换池；使用轮换池与备用轮换池之间可直接互切。"
            : $"当前在{GetAccountRotationPoolLabel(pool)}；" +
              $"点击切换到{GetAccountRotationPoolLabel(poolToggleTarget)}。" +
              "不参与请使用右侧独立按钮。");
        poolButton.Click += (_, _) => ToggleAccountRotationPool(account);
        row.Controls.Add(poolButton);

        var noneButton = MakeActionButton(
            "不参与",
            controlsLeft + poolButtonWidth + gap,
            controlsTop,
            noneButtonWidth,
            primary: false);
        noneButton.Height = 42;
        noneButton.Enabled = pool != AccountRotationPool.None;
        noneButton.AccessibleName = pool == AccountRotationPool.None
            ? $"{account.Name} 当前不参与账号轮换"
            : $"将 {account.Name} 移出轮换池";
        _toolTip.SetToolTip(noneButton, pool == AccountRotationPool.None
            ? "当前账号未参与轮换。左侧按钮可加入使用轮换池。"
            : "点击将账号设为不参与；不会改变其他账号的轮换顺序。");
        noneButton.Click += (_, _) => SetAccountRotationPoolFromUi(
            account,
            AccountRotationPool.None);
        row.Controls.Add(noneButton);

        var arrowLeft = controlsLeft + poolButtonWidth + noneButtonWidth + gap * 2;

        var canMove = pool != AccountRotationPool.None;
        var up = CreateAccountRotationMoveButton(
            "↑",
            arrowLeft,
            controlsTop,
            "上移",
            canMove && index > 0);
        up.Click += (_, _) => MoveAccountRotationAccount(account, -1);
        row.Controls.Add(up);

        var down = CreateAccountRotationMoveButton(
            "↓",
            arrowLeft + arrowWidth + gap,
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

    private static string GetAccountRotationPoolButtonLabel(AccountRotationPool pool) => pool switch
    {
        AccountRotationPool.Primary => "放入备用轮换池",
        AccountRotationPool.Backup => "放入使用轮换池",
        _ => "放入使用轮换池"
    };

    private int MeasureAccountRotationPoolActionWidth()
    {
        using var buttonFont = new Font(Font.FontFamily, 8.9F);
        var measuredWidth = new[] { "放入备用轮换池", "放入使用轮换池" }
            .Select(text => TextRenderer.MeasureText(
                text,
                buttonFont,
                Size.Empty,
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix).Width)
            .Max();
        // Keep enough horizontal padding for the custom ModernButton renderer and
        // a little spare room at 125–200% DPI so labels never become ellipses.
        return Math.Max(202, measuredWidth + 58);
    }

    private static int MeasureAccountRotationWrappedTextHeight(
        string text,
        Font font,
        int width,
        int minimumHeight)
    {
        var measured = TextRenderer.MeasureText(
            text,
            font,
            new Size(Math.Max(1, width), int.MaxValue),
            TextFormatFlags.WordBreak |
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix);
        return Math.Max(minimumHeight, measured.Height + 4);
    }

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

    private void ToggleAccountRotationPool(AccountRecord account)
    {
        var current = AccountRotationConfiguration.GetPool(_appSettings, account);
        var target = AccountRotationConfiguration.GetInteractivePoolToggleTarget(current);
        SetAccountRotationPoolFromUi(account, target);
    }

    private void SetAccountRotationPoolFromUi(
        AccountRecord account,
        AccountRotationPool target)
    {
        var pools = new Dictionary<string, string>(
            _appSettings.AccountRotationPools,
            StringComparer.Ordinal);
        var primaryOrder = _appSettings.AccountRotationPrimaryOrder.ToList();
        var backupOrder = _appSettings.AccountRotationBackupOrder.ToList();
        var primaryCursor = _appSettings.AccountRotationPrimaryCursorAccountKey;
        var backupCursor = _appSettings.AccountRotationBackupCursorAccountKey;
        var previousPool = AccountRotationConfiguration.GetPool(_appSettings, account);
        AccountRotationConfiguration.SetPool(_appSettings, _accounts, account, target);
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

        _statusBox.Text = target == AccountRotationPool.None
            ? $"{account.Name} 已设为不参与轮换。"
            : previousPool == AccountRotationPool.None
                ? $"{account.Name} 已加入{GetAccountRotationPoolLabel(target)}。"
                : $"{account.Name} 已切换到{GetAccountRotationPoolLabel(target)}。";
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
