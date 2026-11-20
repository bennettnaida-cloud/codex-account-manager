namespace CodexAccountManager;

public partial class Form1
{
    // Two explicit actions (pool toggle + None) and the order arrows need a little
    // more room than the old single three-state button.  Below this width the row
    // stacks its actions instead of clipping the account identity.
    private const int AccountRotationHorizontalMinWidth = 900;

    private void RenderAccountRotationWorkspace(string query, int workspaceWidth)
    {
        _accountRotationDetailLabels.Clear();
        _accountRotationRows.Clear();
        // The quota workspace and this page must render the same effective report.  The
        // old rotation page read the persisted snapshot directly, while the quota page
        // merged that snapshot with the current local usage report.  During a refresh
        // cycle this made one account show (for example) 50% here and 0% on the quota
        // page.  Build the keyed view from the already-merged report so both pages share
        // the same window, percentage and reset boundary.
        var quotaUsage = GetAccountRotationQuotaUsage();
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
            _appSettings.AccountRotationPrimaryCursorAccountKey,
            quotaUsage);
        AddAccountRotationSection(
            "备用轮换池",
            AccountRotationPool.Backup,
            backupAccounts,
            query,
            workspaceWidth,
            _appSettings.AccountRotationBackupCursorAccountKey,
            quotaUsage);
        AddAccountRotationSection(
            "未参与账号",
            AccountRotationPool.None,
            unassignedAccounts,
            query,
            workspaceWidth,
            null,
            quotaUsage);
    }

    private Control CreateAccountRotationSummary(int width)
    {
        width = Math.Max(240, width);
        var enabled = AccountRotationConfiguration.IsEnabled(_appSettings);
        var primaryCount = _accounts.Count(account =>
            AccountRotationConfiguration.GetPool(_appSettings, account) == AccountRotationPool.Primary);
        var backupCount = _accounts.Count(account =>
            AccountRotationConfiguration.GetPool(_appSettings, account) == AccountRotationPool.Backup);
        var noneCount = Math.Max(0, _accounts.Count - primaryCount - backupCount);
        var accent = enabled ? _palette.SuccessColor : _palette.MutedTextColor;
        const int toggleWidth = 56;
        const int leftPadding = 24;
        const int rightPadding = 24;
        const int labelToggleGap = 12;
        var stateMeasurementFont = UiFontCache.Get(Font.FontFamily, 9F, FontStyle.Bold);
        var affinityMeasurementFont = UiFontCache.Get(Font.FontFamily, 8.7F, FontStyle.Bold);
        var titleMeasurementFont = UiFontCache.Get(Font.FontFamily, 10.4F, FontStyle.Bold);
        static int MeasureFixedLabel(string text, Font font) =>
            TextRenderer.MeasureText(
                text,
                font,
                Size.Empty,
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix).Width + 18;
        var stateWidth = Math.Max(
            76,
            Math.Max(
                MeasureFixedLabel("开启", stateMeasurementFont),
                MeasureFixedLabel("关闭", stateMeasurementFont)));
        var affinityWidth = Math.Max(
            100,
            Math.Max(
                MeasureFixedLabel("会话粘性", affinityMeasurementFont),
                MeasureFixedLabel("粘性关闭", affinityMeasurementFont)));
        var rightLabelWidth = Math.Max(stateWidth, affinityWidth);
        var rightColumnWidth = rightLabelWidth + labelToggleGap + toggleWidth;
        var titleWidth = MeasureFixedLabel("轮换状态", titleMeasurementFont);
        // A title and two fixed labels must never compete for the same pixels.  Use the
        // two-column layout only when the left column can physically fit the title; at
        // smaller widths the controls move below the text instead of being ellipsized.
        var horizontal = width >= leftPadding + titleWidth + 28 + rightColumnWidth + rightPadding;
        var availableControlsWidth = Math.Max(toggleWidth + labelToggleGap + 48, width - leftPadding - rightPadding);
        var effectiveRightLabelWidth = Math.Min(
            rightLabelWidth,
            Math.Max(48, availableControlsWidth - toggleWidth - labelToggleGap));
        var switchLeft = horizontal
            ? Math.Max(leftPadding, width - rightPadding - toggleWidth)
            : Math.Min(
                Math.Max(leftPadding, width - rightPadding - toggleWidth),
                leftPadding + effectiveRightLabelWidth + labelToggleGap);
        var rightLabelLeft = horizontal
            ? Math.Max(leftPadding, switchLeft - labelToggleGap - effectiveRightLabelWidth)
            : leftPadding;
        var summaryTextWidth = horizontal
            ? Math.Max(titleWidth, rightLabelLeft - leftPadding - 16)
            : Math.Max(titleWidth, width - leftPadding - rightPadding);
        var countsText = $"使用 {primaryCount}  ·  备用 {backupCount}  ·  未参与 {noneCount}";
        var countsMeasurementFont = UiFontCache.Get(Font.FontFamily, 8.9F);
        const int titleTop = 12;
        const int titleHeight = 34;
        const int countsTop = 52;
        var countsHeight = MeasureAccountRotationWrappedTextHeight(
            countsText,
            countsMeasurementFont,
            summaryTextWidth,
            27);
        var cursorSummaryTop = countsTop + countsHeight + 4;
        var cursorSummaryText = BuildAccountRotationCursorSummary();
        var cursorSummaryMeasurementFont = UiFontCache.Get(Font.FontFamily, 8.5F);
        var cursorSummaryHeight = MeasureAccountRotationWrappedTextHeight(
            cursorSummaryText,
            cursorSummaryMeasurementFont,
            summaryTextWidth,
            26);
        var switchTop = horizontal ? 25 : cursorSummaryTop + cursorSummaryHeight + 10;
        var affinityTop = switchTop + 38;
        var panelHeight = horizontal
            ? Math.Max(122, Math.Max(cursorSummaryTop + cursorSummaryHeight + 18, affinityTop + 42))
            : affinityTop + 42;
        var panel = new RoundedPanel
        {
            Width = width,
            Height = panelHeight,
            Radius = 16,
            BorderColor = GetFloatingCardBorderColor(accent, emphasized: true),
            BackColor = _palette.CardColor,
            SurfaceOpacity = GetFloatingCardOpacity(),
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, accent, 0.035F),
            AccentColor = accent,
            AccentWidth = 4,
            ShadowColor = GetFloatingCardShadowColor(),
            Margin = new Padding(0, 0, 0, 12),
            AccessibleName = enabled ? "账号轮换已开启" : "账号轮换已关闭"
        };

        var title = new Label
        {
            Text = "轮换状态",
            Left = leftPadding,
            Top = titleTop,
            Width = summaryTextWidth,
            Height = titleHeight,
            MinimumSize = new Size(titleWidth, titleHeight),
            Font = UiFontCache.Get(Font.FontFamily, 10.4F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var counts = new Label
        {
            Text = countsText,
            Left = leftPadding,
            Top = countsTop,
            Width = summaryTextWidth,
            Height = countsHeight,
            Font = UiFontCache.Get(Font.FontFamily, 8.9F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(counts, _palette, true);
        panel.Controls.Add(counts);

        var cursorSummary = new Label
        {
            Text = cursorSummaryText,
            Left = leftPadding,
            Top = cursorSummaryTop,
            Width = summaryTextWidth,
            Height = cursorSummaryHeight,
            Font = UiFontCache.Get(Font.FontFamily, 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(cursorSummary, _palette, true);
        panel.Controls.Add(cursorSummary);

        var effectiveStateWidth = Math.Min(stateWidth, effectiveRightLabelWidth);
        var state = new Label
        {
            Text = enabled ? "开启" : "关闭",
            Left = horizontal
                ? Math.Max(leftPadding, switchLeft - labelToggleGap - effectiveStateWidth)
                : rightLabelLeft,
            Top = switchTop,
            Width = effectiveStateWidth,
            Height = 34,
            MinimumSize = new Size(effectiveStateWidth, 34),
            Font = UiFontCache.Get(Font.FontFamily, 9F, FontStyle.Bold),
            TextAlign = horizontal ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(state, _palette, !enabled);
        panel.Controls.Add(state);

        var toggle = new ModernToggleSwitch
        {
            Left = switchLeft,
            Top = switchTop,
            Width = toggleWidth,
            Height = 34,
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

        if (AccountRotationConfiguration.UsesCpaRouting(_appSettings))
        {
            var mode = new Label
            {
                Text = AccountRotationConfiguration.UsesCpaRoundRobin(_appSettings) ? "CPA 轮询" : "CPA 优先",
                Left = rightLabelLeft,
                Top = affinityTop,
                Width = Math.Min(rightColumnWidth, Math.Max(120, width - rightLabelLeft - rightPadding)),
                Height = 34,
                Font = UiFontCache.Get(Font.FontFamily, 8.7F, FontStyle.Bold),
                TextAlign = horizontal ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
                UseMnemonic = false
            };
            ThemeStyler.ApplyLabel(mode, _palette);
            _toolTip.SetToolTip(mode, (AccountRotationConfiguration.UsesCpaRoundRobin(_appSettings)
                ? "普通模型请求逐次轮询主池；" : "持续使用主池顺序中第一个可用账号；手动选择的账号在同池内优先。") +
                "主池不可用才用备用池。" +
                "临时失败账号冷却至少 60 秒。previous_response_id 续聊始终使用原账号。");
            panel.Controls.Add(mode);
            return panel;
        }
        var affinityEnabled = _appSettings.AccountRotationSessionAffinityEnabled;
        var effectiveAffinityWidth = Math.Min(affinityWidth, effectiveRightLabelWidth);
        var affinityState = new Label
        {
            Text = affinityEnabled ? "会话粘性" : "粘性关闭",
            Left = horizontal
                ? Math.Max(leftPadding, switchLeft - labelToggleGap - effectiveAffinityWidth)
                : rightLabelLeft,
            Top = affinityTop,
            Width = effectiveAffinityWidth,
            Height = 34,
            MinimumSize = new Size(effectiveAffinityWidth, 34),
            Font = UiFontCache.Get(Font.FontFamily, 8.7F, FontStyle.Bold),
            TextAlign = horizontal ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(affinityState, _palette, !affinityEnabled);
        panel.Controls.Add(affinityState);

        var affinityToggle = new ModernToggleSwitch
        {
            Left = switchLeft,
            Top = affinityTop,
            Width = toggleWidth,
            Height = 34,
            Text = string.Empty,
            Checked = affinityEnabled,
            OnTrackColor = _palette.PrimaryColor,
            OffTrackColor = UiDesign.Blend(_palette.DisabledColor, _palette.MutedTextColor, 0.16F),
            KnobColor = _palette.CardColor,
            TextColor = _palette.TextColor,
            BorderColor = UiDesign.Blend(_palette.BorderColor, _palette.PrimaryColor, 0.30F),
            BackColor = panel.BackColor,
            AccessibleName = "开启会话账号粘性"
        };
        _toolTip.SetToolTip(
            affinityToggle,
            "每个请求按优先级从 session/conversation 请求头、prompt_cache_key、" +
            "turn metadata 或稳定内容摘要中选择一个普通会话标识；" +
            "previous_response_id 使用独立的已确认账号绑定，不会跨账号续聊。" +
            "可安全重放的普通请求遇到 429 时按你的轮换顺序换号，" +
            "并仅在完整成功后改绑。默认滑动有效期 60 分钟。");
        affinityToggle.CheckedChanged += (_, _) =>
        {
            var previous = _appSettings.AccountRotationSessionAffinityEnabled;
            if (affinityToggle.Checked == previous)
            {
                return;
            }
            _appSettings.AccountRotationSessionAffinityEnabled = affinityToggle.Checked;
            if (!TrySaveAppSettings(out var error))
            {
                _appSettings.AccountRotationSessionAffinityEnabled = previous;
                affinityToggle.Checked = previous;
                _statusBox.Text = "会话粘性设置未保存：" + error;
                return;
            }
            affinityState.Text = affinityToggle.Checked ? "会话粘性" : "粘性关闭";
            ThemeStyler.ApplyLabel(affinityState, _palette, !affinityToggle.Checked);
            _statusBox.Text = affinityToggle.Checked
                ? "已开启会话账号粘性；普通会话未命中时仍使用当前自定义轮换顺序，续聊响应 ID 严格留在已确认账号。"
                : "已关闭普通会话粘性；无续聊请求只使用全局轮换路由，已确认 previous_response_id 仍严格回到原账号。";
        };
        panel.Controls.Add(affinityToggle);

        return panel;
    }

    private string BuildAccountRotationCursorSummary()
    {
        var primary = ResolveAccountRotationCursorName(
            _appSettings.AccountRotationPrimaryCursorAccountKey);
        var backup = ResolveAccountRotationCursorName(
            _appSettings.AccountRotationBackupCursorAccountKey);
        // Keep each pool on its own line.  A single combined line silently clipped the
        // backup cursor when either account name was long or Windows text scaling was high.
        var modeText = AccountRotationConfiguration.UsesCpaRouting(_appSettings)
            ? (AccountRotationConfiguration.UsesCpaRoundRobin(_appSettings) ? "CPA：逐请求轮询" : "CPA：优先同一可用账号") +
              " · 主池优先 · 故障冷却\r\n" : "";
        return modeText + $"暂停并换号：5h 剩余 ≤10% / 仅周额度剩余 ≤3%\r\n使用轮换池：{primary}\r\n备用轮换池：{backup}";
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
        string? cursorAccountKey,
        IReadOnlyDictionary<string, AccountUsageSummary> quotaUsage)
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
                width,
                quotaUsage.GetValueOrDefault(QuotaAccountIdentity.CreateKey(account))));
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
            Height = 46,
            Radius = 11,
            BorderColor = GetFloatingCardBorderColor(accent, emphasized: true),
            BackColor = _palette.CardColor,
            SurfaceOpacity = GetFloatingCardOpacity(),
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceColor, accent, 0.028F),
            AccentColor = accent,
            AccentWidth = 3,
            ShadowColor = GetFloatingCardShadowColor(),
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
            Font = UiFontCache.Get(Font.FontFamily, 9.6F, FontStyle.Bold),
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
                Font = UiFontCache.Get(Font.FontFamily, 8.4F),
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
            SurfaceOpacity = GetFloatingCardOpacity(),
            Margin = new Padding(0, 0, 0, 12)
        };
        var label = new Label
        {
            Text = text,
            Left = 22,
            Top = 10,
            Width = Math.Max(160, width - 44),
            Height = 40,
            Font = UiFontCache.Get(Font.FontFamily, 8.8F),
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
        int width,
        AccountUsageSummary? quotaUsage)
    {
        var horizontal = width >= AccountRotationHorizontalMinWidth;
        var accent = GetAccountRotationPoolColor(pool);

        var row = new RoundedPanel
        {
            Width = width,
            Height = horizontal ? 94 : 188,
            Radius = 14,
            BorderColor = GetFloatingCardBorderColor(
                IsCurrentAccount(account) ? _palette.SuccessColor : accent,
                emphasized: IsCurrentAccount(account)),
            BackColor = _palette.CardColor,
            SurfaceOpacity = GetFloatingCardOpacity(emphasized: IsCurrentAccount(account)),
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, accent, 0.018F),
            AccentColor = accent,
            AccentWidth = 3,
            ShadowColor = GetFloatingCardShadowColor(emphasized: IsCurrentAccount(account)),
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

        var arrowWidth = 42;
        var gap = 8;
        var noneButtonWidth = 92;
        // Keep the primary action visually distinct from pool/order maintenance.  It
        // arms the authenticated request-boundary route and never calls the destructive
        // desktop profile switch used by the ordinary "启动" button.
        var activeButtonWidth = 124;
        var measuredPoolWidth = MeasureAccountRotationPoolActionWidth();
        var poolButtonWidth = horizontal
            ? Math.Max(176, measuredPoolWidth)
            : Math.Max(150, measuredPoolWidth);
        var controlsWidth = activeButtonWidth + poolButtonWidth + noneButtonWidth +
                            (arrowWidth * 2) + (gap * 4);
        var maxControlsWidth = Math.Max(220, width - 44);
        if (!horizontal && controlsWidth > maxControlsWidth)
        {
            var fixedOther = activeButtonWidth + noneButtonWidth + (arrowWidth * 2) + (gap * 4);
            if (maxControlsWidth - fixedOther >= 116)
            {
                poolButtonWidth = maxControlsWidth - fixedOther;
            }
            else
            {
                gap = 6;
                var scaleFactor = (double)(maxControlsWidth - (gap * 4)) /
                                  Math.Max(1, activeButtonWidth + poolButtonWidth + noneButtonWidth + (arrowWidth * 2));
                arrowWidth = Math.Max(30, (int)Math.Floor(arrowWidth * scaleFactor));
                noneButtonWidth = Math.Max(64, (int)Math.Floor(noneButtonWidth * scaleFactor));
                activeButtonWidth = Math.Max(82, (int)Math.Floor(activeButtonWidth * scaleFactor));
                poolButtonWidth = Math.Max(
                    88,
                    maxControlsWidth - activeButtonWidth - noneButtonWidth - (arrowWidth * 2) - (gap * 4));
            }
            controlsWidth = activeButtonWidth + poolButtonWidth + noneButtonWidth +
                            (arrowWidth * 2) + (gap * 4);
        }
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
            Font = UiFontCache.Get(Font.FontFamily, 9.7F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(name, _palette);
        row.Controls.Add(name);

        var detail = new Label
        {
            Text = BuildAccountRotationAccountDetail(account, quotaUsage),
            Left = 78,
            Top = 44,
            Width = summaryWidth,
            Height = 42,
            Font = UiFontCache.Get(Font.FontFamily, 8.4F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(detail, _palette, true);
        _toolTip.SetToolTip(detail, detail.Text);
        row.Controls.Add(detail);
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        _accountRotationDetailLabels[accountKey] = detail;
        _accountRotationRows[accountKey] = row;

        var controlsLeft = horizontal
            ? width - 22 - controlsWidth
            : Math.Max(22, (width - controlsWidth) / 2);
        var controlsTop = horizontal
            ? Math.Max(26, (row.Height - 42) / 2)
            : 132;

        var activeButton = MakeActionButton(
            "主动切换",
            controlsLeft,
            controlsTop,
            activeButtonWidth,
            primary: IsCurrentAccount(account));
        activeButton.Height = 42;
        var canActivelyRotate = !IsCurrentAccount(account) &&
                                pool != AccountRotationPool.None &&
                                AccountRotationConfiguration.IsEnabled(_appSettings) &&
                                _appSettings.PatGatewayEnabled;
        activeButton.Enabled = canActivelyRotate;
        activeButton.AccessibleName = canActivelyRotate
            ? $"主动切换到 {account.Name}"
            : IsCurrentAccount(account)
                ? $"{account.Name} 当前正在使用"
                : $"暂不能主动切换到 {account.Name}";
        _toolTip.SetToolTip(
            activeButton,
            canActivelyRotate
                ? "在网关请求边界切换到此账号；正在执行的任务继续输出，不关闭 Codex 或网关。"
                : IsCurrentAccount(account)
                    ? "当前账号无需再次切换。"
                    : "请先开启账号轮换、网关，并将账号加入使用轮换池或备用轮换池。");
        activeButton.Click += async (_, _) =>
            await StartManualAccountRotationFromUiAsync(account);
        row.Controls.Add(activeButton);

        var poolControlsLeft = controlsLeft + activeButtonWidth + gap;

        var poolToggleTarget =
            AccountRotationConfiguration.GetInteractivePoolToggleTarget(pool);
        var poolButtonLabel = GetAccountRotationPoolButtonLabel(pool);
        var poolButton = MakeActionButton(
            poolButtonLabel,
            poolControlsLeft,
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
        poolButton.Click += async (_, _) =>
            await ToggleAccountRotationPoolAsync(account);
        row.Controls.Add(poolButton);

        var noneButton = MakeActionButton(
            "不参与",
            poolControlsLeft + poolButtonWidth + gap,
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
        noneButton.Click += async (_, _) =>
            await SetAccountRotationPoolFromUiAsync(
                account,
                AccountRotationPool.None);
        row.Controls.Add(noneButton);

        var arrowLeft = poolControlsLeft + poolButtonWidth + noneButtonWidth + gap * 2;

        var canMove = pool != AccountRotationPool.None;
        var up = CreateAccountRotationMoveButton(
            "↑",
            arrowLeft,
            controlsTop,
            arrowWidth,
            "上移",
            canMove && index > 0);
        up.Click += (_, _) => MoveAccountRotationAccount(account, -1);
        row.Controls.Add(up);

        var down = CreateAccountRotationMoveButton(
            "↓",
            arrowLeft + arrowWidth + gap,
            controlsTop,
            arrowWidth,
            "下移",
            canMove && index >= 0 && index < count - 1);
        down.Click += (_, _) => MoveAccountRotationAccount(account, 1);
        row.Controls.Add(down);

        // Measure the actual Label after theme/DPI font changes. Quota refreshes
        // can add a reset line or wrap the summary and must resize the existing row.
        var fitting = false;
        void FitRow()
        {
            if (fitting || row.IsDisposed || detail.IsDisposed) return;
            fitting = true;
            row.SuspendLayout();
            try
            {
                name.Height = Math.Max(34, name.GetPreferredSize(new Size(name.Width, 0)).Height);
                detail.Top = name.Bottom;
                detail.Height = Math.Max(42, detail.GetPreferredSize(new Size(detail.Width, 0)).Height + 8);
                row.Height = horizontal ? Math.Max(94, detail.Bottom + 12) : Math.Max(188, detail.Bottom + 62);
                var top = horizontal ? (row.Height - 42) / 2 : detail.Bottom + 10;
                foreach (var button in row.Controls.OfType<Button>()) button.Top = top;
            }
            finally
            {
                row.ResumeLayout();
                fitting = false;
            }
        }
        detail.TextChanged += (_, _) => FitRow();
        detail.FontChanged += (_, _) => FitRow();
        detail.SizeChanged += (_, _) => FitRow();
        name.FontChanged += (_, _) => FitRow();
        FitRow();

        return row;
    }

    private Button CreateAccountRotationMoveButton(
        string symbol,
        int left,
        int top,
        int width,
        string accessibleName,
        bool enabled)
    {
        var button = MakeActionButton(symbol, left, top, width, primary: false);
        button.Height = 42;
        button.Padding = Padding.Empty;
        button.Font = UiFontCache.Get(Font.FontFamily, 12F, FontStyle.Bold);
        button.Enabled = enabled;
        button.AccessibleName = accessibleName;
        _toolTip.SetToolTip(button, accessibleName);
        return button;
    }

    private string BuildAccountRotationAccountDetail(
        AccountRecord account,
        AccountUsageSummary? quotaUsage)
    {
        var summary = new List<string> { account.AuthKindLabel };
        var resets = new List<string>();
        var fiveHour = quotaUsage?.GetQuotaWindow(AccountQuotaWindowKind.FiveHour);
        var weekly = quotaUsage?.GetQuotaWindow(AccountQuotaWindowKind.Weekly);
        var monthly = quotaUsage?.GetQuotaWindow(AccountQuotaWindowKind.Monthly);

        AppendRotationQuotaWindow(summary, resets, fiveHour, "5h", PatAutoRotationPolicy.FiveHourRemainingReserve);
        AppendRotationQuotaWindow(summary, resets, weekly, "周", fiveHour == null ? PatAutoRotationPolicy.WeeklyOnlyRemainingReserve : null);
        AppendRotationQuotaWindow(summary, resets, monthly, "月", reserve: null);
        if (summary.Count == 1)
        {
            summary.Add("额度待查询");
        }
        if (resets.Count == 0)
        {
            resets.Add("重置时间待查询");
        }

        return string.Join("  ·  ", summary) + Environment.NewLine +
               string.Join("  ·  ", resets);
    }

    private static void AppendRotationQuotaWindow(
        ICollection<string> summary,
        ICollection<string> resets,
        AccountQuotaWindowSnapshot? window,
        string label,
        double? reserve)
    {
        if (window == null)
        {
            return;
        }

        var remaining = window.RemainingPercent;
        summary.Add(remaining is { } value && double.IsFinite(value)
            ? $"{label}剩余 {Math.Clamp(value, 0D, 100D):0.#}%"
            : $"{label}剩余 —");
        if (reserve is { } threshold &&
            remaining is { } remainingValue &&
            remainingValue <= threshold &&
            (window.ResetAtUtc == null || window.ResetAtUtc > DateTimeOffset.UtcNow))
        {
            summary.Add("已达保留线");
        }

        resets.Add(window.ResetAtUtc is { } resetAtUtc
            ? $"{label}重置 {resetAtUtc.ToLocalTime():MM-dd HH:mm}"
            : $"{label}重置 —");
    }

    private bool TryUpdateAccountRotationRowsInPlace()
    {
        if (_activeView != WorkspaceView.AccountRotation ||
            _accountRotationDetailLabels.Count == 0)
        {
            return false;
        }

        var usageByKey = GetAccountRotationQuotaUsage();
        foreach (var account in _accounts)
        {
            var accountKey = QuotaAccountIdentity.CreateKey(account);
            if (!_accountRotationDetailLabels.TryGetValue(accountKey, out var detail) ||
                detail.IsDisposed)
            {
                continue;
            }

            usageByKey.TryGetValue(accountKey, out var usage);
            detail.Text = BuildAccountRotationAccountDetail(account, usage);
            _toolTip.SetToolTip(detail, detail.Text);
            if (_accountRotationRows.TryGetValue(accountKey, out var row) && !row.IsDisposed)
            {
                var pool = AccountRotationConfiguration.GetPool(_appSettings, account);
                var accent = GetAccountRotationPoolColor(pool);
                var border = IsCurrentAccount(account)
                    ? _palette.SuccessColor
                    : UiDesign.Blend(_palette.BorderColor, accent, 0.14F);
                if (row.BorderColor != border)
                {
                    row.BorderColor = border;
                    row.Invalidate();
                }
            }
        }

        return true;
    }

    private IReadOnlyDictionary<string, AccountUsageSummary> GetAccountRotationQuotaUsage()
    {
        var report = _quotaUsageCache;
        if (report == null)
        {
            return new Dictionary<string, AccountUsageSummary>(StringComparer.Ordinal);
        }

        // Keep this call in the same path used by RenderCards for the quota page.  It is
        // idempotent and ensures a just-arrived official response is visible here even
        // when the user navigates to rotation between two timer ticks.
        ApplyLiveRateLimitSnapshots(report);
        UpdateQuotaLimitProfilesFromReport(report);

        var usageByKey = new Dictionary<string, AccountUsageSummary>(StringComparer.Ordinal);
        foreach (var account in _accounts)
        {
            var usage = FindUsageForAccount(report, account);
            if (usage != null)
            {
                usageByKey[QuotaAccountIdentity.CreateKey(account)] = usage;
            }
        }

        return usageByKey;
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
        var buttonFont = UiFontCache.Get(Font.FontFamily, 8.9F);
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

    private async Task ToggleAccountRotationPoolAsync(AccountRecord account)
    {
        var current = AccountRotationConfiguration.GetPool(_appSettings, account);
        var target = AccountRotationConfiguration.GetInteractivePoolToggleTarget(current);
        await SetAccountRotationPoolFromUiAsync(account, target);
    }

    private async Task SetAccountRotationPoolFromUiAsync(
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
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        var primaryOccurrences = _appSettings.AccountRotationPrimaryOrder.Count(value =>
            value.Equals(accountKey, StringComparison.Ordinal));
        var backupOccurrences = _appSettings.AccountRotationBackupOrder.Count(value =>
            value.Equals(accountKey, StringComparison.Ordinal));
        var poolTransitionValid =
            AccountRotationConfiguration.GetPool(_appSettings, account) == target &&
            target switch
            {
                AccountRotationPool.Primary => primaryOccurrences == 1 && backupOccurrences == 0,
                AccountRotationPool.Backup => primaryOccurrences == 0 && backupOccurrences == 1,
                _ => primaryOccurrences == 0 && backupOccurrences == 0
            };
        if (!poolTransitionValid)
        {
            _appSettings.AccountRotationPools = pools;
            _appSettings.AccountRotationPrimaryOrder = primaryOrder;
            _appSettings.AccountRotationBackupOrder = backupOrder;
            _appSettings.AccountRotationPrimaryCursorAccountKey = primaryCursor;
            _appSettings.AccountRotationBackupCursorAccountKey = backupCursor;
            ShowAccountRotationSaveError("轮换池顺序未能完成原子迁移，请重试。");
            RerenderAccountRotationWorkspacePreservingScroll();
            return;
        }
        if (target != AccountRotationPool.None && IsCurrentAccount(account))
        {
            // Moving the serving backup account into the primary pool must move its live
            // cursor too; otherwise the header can keep reporting a backup position even
            // though the per-account row already says “使用轮换池”.
            AccountRotationConfiguration.MarkUsed(_appSettings, account);
        }
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

        // Re-evaluate backup -> primary return immediately after any pool change.  This is
        // especially important when the user moves an available account from backup/none into
        // the primary pool while another backup account is currently serving requests.
        _accountRotationPrimaryReturnCheckedAtUtc = null;

        // A pool membership transition involving None invalidates ordinary session aliases
        // for this account. Otherwise a stale session-id/conversation binding could bring
        // the account back as soon as it is re-added to a rotation pool. Strict
        // previous_response_id bindings are intentionally retained by the gateway because
        // they cannot be safely replayed on a different account.
        if (previousPool == AccountRotationPool.None || target == AccountRotationPool.None)
        {
            try
            {
                var invalidated = await LocalPatGateway.InvalidateOrdinarySessionAffinityAsync(
                    QuotaAccountIdentity.CreateKey(account));
                if (!invalidated)
                {
                    _statusBox.Text += "（普通会话粘性清理将在网关下次可用时重试）";
                }
            }
            catch (Exception ex) when (
                ex is IOException or HttpRequestException or InvalidOperationException or
                UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                _statusBox.Text += "（普通会话粘性清理暂未完成）";
            }
        }
        RerenderAccountRotationWorkspacePreservingScroll();

        // Moving an account into the primary/“使用” pool is an explicit signal that a
        // backup session may return now.  The 250 ms quota timer is intentionally
        // lightweight and can be throttled while the rotation page is being rebuilt, so
        // perform one immediate local-evidence check as well.  This never probes the
        // provider by itself; PreparePrimaryPoolReturnAsync still requires the same
        // persisted quota evidence and request-boundary gateway route as the timer path.
        if (target == AccountRotationPool.Primary)
        {
            RefreshAccountRotationPrimaryReturnIfNeeded();
        }
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
        _cardsPanel.InvalidateVisibleViewport(flush: true);
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
