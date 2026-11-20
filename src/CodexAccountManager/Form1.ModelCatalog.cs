using System.Globalization;
using System.Reflection;

namespace CodexAccountManager;

public partial class Form1
{
    // Keep every pricing column visible in the clean 1038px first-run window. The
    // workspace leaves roughly 670px for this grid after the sidebar, card padding,
    // borders, and the vertical scrollbar are accounted for.
    private const int ModelPricingGridNarrowViewportWidth = 670;
    private const int ModelPricingGridChromeReserve = 24;
    private const int ModelColumnMinimumWidth = 122;
    private const int InputColumnMinimumWidth = 50;
    private const int CachedInputColumnMinimumWidth = 62;
    private const int OutputColumnMinimumWidth = 50;
    private const int CacheWriteColumnMinimumWidth = 66;
    private const int LongContextColumnMinimumWidth = 64;
    private const int ThresholdColumnMinimumWidth = 78;
    private const int LongInputColumnMinimumWidth = 70;
    private const int LongOutputColumnMinimumWidth = 70;

    private int _modelCatalogSourceMaxWidth;
    private int _modelCatalogSourceRightEdge;
    private bool _modelCatalogSourceRightAligned;

    private Control CreateModelCatalogPanel(int width)
    {
        var dpiScale = Math.Max(1F, DeviceDpi / 96F);
        int Scale(int logicalPx) => Math.Max(1, (int)Math.Round(logicalPx * dpiScale));

        var catalog = ModelCatalogService.CreateEditableCopy();
        var dark = ThemeStyler.IsDark(_palette);
        var panel = new RoundedPanel
        {
            Width = width,
            Height = Scale(760),
            Radius = Scale(16),
            BorderColor = GetFloatingCardBorderColor(_palette.SecondaryAccentColor),
            BackColor = _palette.CardColor,
            SurfaceOpacity = GetFloatingCardOpacity(),
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, _palette.SecondaryAccentColor, dark ? 0.045F : 0.028F),
            AccentColor = _palette.SecondaryAccentColor,
            AccentWidth = 3,
            ShadowColor = GetFloatingCardShadowColor(),
            // Keep the card inside the flow viewport.  The previous right margin
            // was counted as child width and caused the workspace's horizontal
            // scrollbar, hiding the lower edge of the pricing grid.
            Margin = new Padding(0, 0, 0, CardGap),
            Padding = new Padding(Scale(22))
        };

        var innerLeft = Scale(24);
        var innerWidth = Math.Max(Scale(360), width - (innerLeft * 2));
        var titleFont = UiFontCache.Get(Font.FontFamily, 11.2F, FontStyle.Bold);
        var subtitleFont = UiFontCache.Get(Font.FontFamily, 8.7F, FontStyle.Regular);
        var rowLabelFont = UiFontCache.Get(Font.FontFamily, 9F, FontStyle.Bold);
        var sourceBadgeFont = UiFontCache.Get(Font.FontFamily, 8.2F, FontStyle.Bold);
        var noteFont = UiFontCache.Get(Font.FontFamily, 8.3F, FontStyle.Regular);

        var y = Scale(14);
        var titleTextSize = TextRenderer.MeasureText(
            "模型与价格",
            titleFont,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var titleHeight = Math.Max(Scale(24), titleTextSize.Height + Scale(4));
        var titleWidth = Math.Min(innerWidth, Math.Max(Scale(120), titleTextSize.Width + Scale(12)));
        var title = new Label
        {
            Text = "模型与价格",
            Left = innerLeft,
            Top = y,
            Width = titleWidth,
            Height = titleHeight,
            Font = titleFont,
            AutoEllipsis = false,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var initialSourceText = GetModelCatalogSourceText(catalog);
        if (!string.IsNullOrWhiteSpace(_modelCatalogAutoRefreshNote))
        {
            initialSourceText += " · " + _modelCatalogAutoRefreshNote;
        }
        var sourceBadgeHeight = Math.Max(Scale(25), TextRenderer.MeasureText(
            initialSourceText,
            sourceBadgeFont,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height + Scale(8));
        var sourceBadgeWidth = MeasurePillLabelWidth(initialSourceText, sourceBadgeFont, sourceBadgeHeight, showDot: true);
        var sourceRightEdge = innerLeft + innerWidth;
        var sourceInlineMaxWidth = innerWidth - titleWidth - Scale(14);
        var sourceInline = sourceBadgeWidth <= sourceInlineMaxWidth;
        var sourcePill = new PillLabel
        {
            Text = initialSourceText,
            Font = sourceBadgeFont,
            Height = sourceBadgeHeight,
            TextAlign = ContentAlignment.MiddleCenter,
            ShowDot = true,
            UseMnemonic = false
        };
        ApplyModelCatalogSourcePillStyle(sourcePill, catalog.CatalogSource);
        if (sourceInline)
        {
            sourcePill.SetBounds(
                sourceRightEdge - sourceBadgeWidth,
                y + Math.Max(0, (titleHeight - sourceBadgeHeight) / 2),
                sourceBadgeWidth,
                sourceBadgeHeight);
            _modelCatalogSourceMaxWidth = sourceInlineMaxWidth;
            _modelCatalogSourceRightEdge = sourceRightEdge;
            _modelCatalogSourceRightAligned = true;
            y = Math.Max(title.Bottom, sourcePill.Bottom) + Scale(2);
        }
        else
        {
            var clampedWidth = Math.Min(innerWidth, sourceBadgeWidth);
            sourcePill.SetBounds(innerLeft, title.Bottom + Scale(4), clampedWidth, sourceBadgeHeight);
            _modelCatalogSourceMaxWidth = innerWidth;
            _modelCatalogSourceRightEdge = sourceRightEdge;
            _modelCatalogSourceRightAligned = false;
            y = sourcePill.Bottom + Scale(4);
        }
        _toolTip.SetToolTip(sourcePill, initialSourceText);
        panel.Controls.Add(sourcePill);
        _modelCatalogSourceLabel = sourcePill;
        _modelCatalogPanel = panel;
        _modelCatalogEditorDirty = false;

        const string subtitleText = "价格单位为美元 / 百万 token；可手动微调，官网检查成功后以最新官方标准价目为准。";
        var subtitle = new Label
        {
            Text = subtitleText,
            Left = innerLeft,
            Top = y,
            Width = innerWidth,
            Height = MeasureWrappedLabelHeight(subtitleText, subtitleFont, innerWidth, Scale(18)),
            Font = subtitleFont,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(subtitle, _palette, true);
        panel.Controls.Add(subtitle);
        y = subtitle.Bottom + Scale(8);

        panel.Controls.Add(CreateSystemConfigDivider(innerLeft, y, innerWidth));
        y += Scale(9);

        // Default model row: label aligned with System Config labels + ModernInputShell-wrapped ThemedComboBox
        var measuredLabelWidth = TextRenderer.MeasureText(
            "项目根目录",
            rowLabelFont,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        var labelWidth = Math.Max(Scale(106), measuredLabelWidth + Scale(22));
        var fieldLeft = innerLeft + labelWidth + Scale(10);

        var defaultModel = new ThemedComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiFontCache.Get(Font.FontFamily, 9.0F, FontStyle.Regular),
            IntegralHeight = false,
            DropDownHeight = Scale(260)
        };
        foreach (var choice in catalog.Models
                     .SelectMany(model => model.Aliases.Append(model.Id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            defaultModel.Items.Add(choice);
        }
        defaultModel.SelectedItem = defaultModel.Items.Cast<string>().FirstOrDefault(item =>
            item.Equals(catalog.DefaultModel, StringComparison.OrdinalIgnoreCase));
        defaultModel.SelectedIndex = defaultModel.SelectedIndex >= 0
            ? defaultModel.SelectedIndex
            : defaultModel.Items.Count > 0 ? 0 : -1;

        var defaultModelShellHeight = Math.Max(Scale(36), defaultModel.PreferredHeight + Scale(4));
        var defaultModelShellWidth = Math.Min(Scale(300), Math.Max(Scale(200), innerLeft + innerWidth - fieldLeft));
        var defaultModelShell = new ModernInputShell(defaultModel)
        {
            Left = fieldLeft,
            Top = y,
            Width = defaultModelShellWidth,
            Height = defaultModelShellHeight,
            Radius = Scale(10),
            Padding = new Padding(Scale(10), Scale(2), Scale(6), Scale(2))
        };
        ThemeStyler.ApplyInputShell(defaultModelShell, _palette);

        var defaultLabel = new Label
        {
            Text = "默认模型",
            Left = innerLeft,
            Top = y,
            Width = labelWidth,
            Height = defaultModelShellHeight,
            Font = rowLabelFont,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(defaultLabel, _palette, true);
        panel.Controls.Add(defaultLabel);
        panel.Controls.Add(defaultModelShell);

        var defaultHintLeft = defaultModelShell.Right + Scale(14);
        var defaultHintWidth = (innerLeft + innerWidth) - defaultHintLeft;
        if (defaultHintWidth >= Scale(160))
        {
            var defaultHint = new Label
            {
                Text = "作为默认计费参考与新账号初始模型",
                Left = defaultHintLeft,
                Top = y,
                Width = defaultHintWidth,
                Height = defaultModelShellHeight,
                Font = noteFont,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseMnemonic = false
            };
            ThemeStyler.ApplyLabel(defaultHint, _palette, true);
            panel.Controls.Add(defaultHint);
        }
        y = defaultModelShell.Bottom + Scale(9);

        // Pricing grid wrapped in a crisp modern rounded container (replaces harsh raw FixedSingle border)
        var rowHeight = Scale(30);
        var headerHeight = Scale(34);
        var visibleRows = Math.Min(Math.Max(6, catalog.Models.Count), 6);
        var gridContainerHeight = headerHeight + (rowHeight * visibleRows) + Scale(6);
        var gridBackColor = dark
            ? UiDesign.Blend(_palette.CardColor, _palette.InputBackColor, 0.78F)
            : _palette.InputBackColor;
        var gridFrame = new RoundedPanel
        {
            Left = innerLeft,
            Top = y,
            Width = innerWidth,
            Height = gridContainerHeight,
            Radius = Scale(10),
            BorderColor = dark
                ? Color.FromArgb(148, UiDesign.Blend(_palette.BorderColor, _palette.SecondaryAccentColor, 0.36F))
                : UiDesign.Blend(_palette.BorderColor, _palette.SecondaryAccentColor, 0.30F),
            BackColor = gridBackColor,
            SurfaceOpacity = byte.MaxValue,
            UseGradient = false,
            ShadowColor = Color.Transparent,
            Padding = new Padding(Scale(2))
        };

        var grid = CreateModelPricingGrid(
            catalog,
            Scale(2),
            Scale(2),
            Math.Max(Scale(120), innerWidth - Scale(4)),
            Math.Max(Scale(120), gridContainerHeight - Scale(4)),
            rowHeight,
            headerHeight,
            gridBackColor);
        grid.Dock = DockStyle.Fill;
        grid.CellValueChanged += (_, _) => _modelCatalogEditorDirty = true;
        grid.CurrentCellDirtyStateChanged += (_, _) => _modelCatalogEditorDirty = true;
        defaultModel.SelectedIndexChanged += (_, _) => _modelCatalogEditorDirty = true;
        gridFrame.Controls.Add(grid);
        panel.Controls.Add(gridFrame);
        y = gridFrame.Bottom + Scale(7);

        const string gridNoteText = "长上下文阈值按 token 计；取消「长上下文」后，阈值与两个长上下文倍数不参与计费。";
        var note = new Label
        {
            Text = gridNoteText,
            Left = innerLeft,
            Top = y,
            Width = innerWidth,
            Height = MeasureWrappedLabelHeight(gridNoteText, noteFont, innerWidth, Scale(18)),
            Font = noteFont,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(note, _palette, true);
        panel.Controls.Add(note);
        y = note.Bottom + Scale(8);

        // Pricing action bar: dynamically measured buttons (never truncated) cleanly aligned on the right
        var actionGap = Scale(10);
        var buttonHeight = Scale(34);
        var restoreWidth = MeasureActionButtonWidth("恢复内置价格", Scale(138));
        var saveWidth = MeasureActionButtonWidth("保存手动设置", Scale(138));
        var officialWidth = MeasureActionButtonWidth("通过代理检查官网", Scale(162));
        var totalButtonsWidth = restoreWidth + saveWidth + officialWidth + (actionGap * 2);

        Button restore;
        Button save;
        Button official;
        if (totalButtonsWidth <= innerWidth)
        {
            var startLeft = innerLeft + innerWidth - totalButtonsWidth;
            restore = MakeActionButton("恢复内置价格", startLeft, y, restoreWidth, false);
            save = MakeActionButton("保存手动设置", startLeft + restoreWidth + actionGap, y, saveWidth, false);
            official = MakeActionButton("通过代理检查官网", startLeft + restoreWidth + saveWidth + (actionGap * 2), y, officialWidth, true);
            y += buttonHeight + Scale(10);
        }
        else
        {
            var evenWidth = Math.Max(Scale(116), (innerWidth - (actionGap * 2)) / 3);
            restore = MakeActionButton("恢复内置价格", innerLeft, y, evenWidth, false);
            save = MakeActionButton("保存手动设置", innerLeft + evenWidth + actionGap, y, evenWidth, false);
            official = MakeActionButton("通过代理检查官网", innerLeft + ((evenWidth + actionGap) * 2), y, innerWidth - ((evenWidth + actionGap) * 2), true);
            y += buttonHeight + Scale(10);
        }
        restore.Height = buttonHeight;
        restore.AccessibleName = "恢复内置模型与价格";
        restore.Click += (_, _) => RestoreBundledModelCatalog();
        panel.Controls.Add(restore);

        save.Height = buttonHeight;
        save.AccessibleName = "保存手动模型与价格";
        save.Click += (_, _) => SaveManualModelCatalog(grid, defaultModel);
        panel.Controls.Add(save);

        official.Height = buttonHeight;
        official.AccessibleName = "通过当前代理检查 OpenAI 官网模型与价格";
        official.Click += async (_, _) => await CheckOfficialModelCatalogAsync(official);
        _toolTip.SetToolTip(official, "使用上方已检测或手动填写的 HTTP 代理访问 OpenAI 官方模型页面。检查成功后覆盖手动设置。");
        panel.Controls.Add(official);

        panel.Controls.Add(CreateSystemConfigDivider(innerLeft, y, innerWidth));
        y += Scale(8);

        // Auto-update toggle row: crisp label + subtext on left, sleek ModernToggleSwitch on right
        var toggleWidth = Scale(58);
        var toggleHeight = Scale(30);
        var autoTitleFont = UiFontCache.Get(Font.FontFamily, 8.8F, FontStyle.Bold);
        var autoTextWidth = Math.Max(Scale(160), innerWidth - toggleWidth - Scale(14));
        var autoTitleHeight = Math.Max(Scale(18), TextRenderer.MeasureText(
            "自动更新官网价格",
            autoTitleFont,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height + Scale(2));
        var autoTitle = new Label
        {
            Text = "自动更新官网价格",
            Left = innerLeft,
            Top = y,
            Width = autoTextWidth,
            Height = autoTitleHeight,
            Font = autoTitleFont,
            AutoEllipsis = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(autoTitle, _palette);
        panel.Controls.Add(autoTitle);

        const string autoDescText = "启动时及每 6 小时自动通过代理核验官网价格；失败后 15 分钟重试，保留上次有效目录。";
        var autoDesc = new Label
        {
            Text = autoDescText,
            Left = innerLeft,
            Top = autoTitle.Bottom + Scale(1),
            Width = autoTextWidth,
            Height = MeasureWrappedLabelHeight(autoDescText, noteFont, autoTextWidth, Scale(17)),
            Font = noteFont,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(autoDesc, _palette, true);
        panel.Controls.Add(autoDesc);

        var autoBlockHeight = autoDesc.Bottom - y;
        var automatic = new ModernToggleSwitch
        {
            Text = string.Empty,
            Left = innerLeft + innerWidth - toggleWidth,
            Top = y + Math.Max(0, (autoBlockHeight - toggleHeight) / 2),
            Width = toggleWidth,
            Height = toggleHeight,
            Checked = catalog.AutomaticPriceUpdatesEnabled,
            OnTrackColor = _palette.PrimaryColor,
            OffTrackColor = UiDesign.Blend(_palette.DisabledColor, _palette.MutedTextColor, 0.20F),
            KnobColor = dark ? Color.White : _palette.CardColor,
            TextColor = _palette.TextColor,
            BorderColor = UiDesign.Blend(_palette.BorderColor, _palette.PrimaryColor, 0.30F),
            BackColor = Color.Transparent,
            AccessibleName = "自动更新官网价格（启动时及每 6 小时；失败后 15 分钟重试）"
        };
        automatic.CheckedChanged += (_, _) =>
        {
            try
            {
                ModelCatalogService.SetAutomaticPriceUpdates(automatic.Checked);
                _nextAutomaticPriceCheckUtc = DateTimeOffset.MinValue;
                _statusBox.Text = automatic.Checked ? "已启用后台价格更新。" : "已关闭自动价格更新，仍可手动检查官网。";
            }
            catch (Exception error)
            {
                _statusBox.Text = "自动价格更新设置未保存：" + error.Message;
            }
        };
        panel.Controls.Add(automatic);
        y = Math.Max(autoDesc.Bottom, automatic.Bottom) + Scale(8);

        panel.Controls.Add(CreateSystemConfigDivider(innerLeft, y, innerWidth));
        y += Scale(8);

        // Account model discovery row: dynamically measured full-width button (ZERO truncation!) + wrapped guidance + status
        var refreshModelsWidth = MeasureActionButtonWidth("同步账号可用模型", Scale(168));
        var refreshModels = MakeActionButton("同步账号可用模型", innerLeft, y, Math.Min(innerWidth, refreshModelsWidth), true);
        refreshModels.Height = buttonHeight;
        refreshModels.AccessibleName = "从各账号接口同步可用模型，与官网价格检查相互独立";
        refreshModels.Click += async (_, _) => await RefreshAccountModelsManuallyAsync(refreshModels);
        panel.Controls.Add(refreshModels);

        const string discoveryText =
            "AT / API 模型启动前及后台每 15 分钟同步，与官网价格无关；OAuth 使用官方目录，已打开的 Codex 需手动重启。";
        var discoveryGap = Scale(14);
        var discoveryInlineWidth = innerWidth - refreshModels.Width - discoveryGap;
        Label discovery;
        if (discoveryInlineWidth >= Scale(220))
        {
            var discoveryHeight = MeasureWrappedLabelHeight(discoveryText, noteFont, discoveryInlineWidth, buttonHeight);
            discovery = new Label
            {
                Text = discoveryText,
                Left = refreshModels.Right + discoveryGap,
                Top = y + Math.Max(0, (buttonHeight - discoveryHeight) / 2),
                Width = discoveryInlineWidth,
                Height = discoveryHeight,
                Font = noteFont,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = false,
                UseMnemonic = false
            };
        }
        else
        {
            discovery = new Label
            {
                Text = discoveryText,
                Left = innerLeft,
                Top = refreshModels.Bottom + Scale(5),
                Width = innerWidth,
                Height = MeasureWrappedLabelHeight(discoveryText, noteFont, innerWidth, Scale(20)),
                Font = noteFont,
                AutoEllipsis = false,
                UseMnemonic = false
            };
        }
        ThemeStyler.ApplyLabel(discovery, _palette, true);
        panel.Controls.Add(discovery);
        y = Math.Max(refreshModels.Bottom, discovery.Bottom) + Scale(6);

        var discoveryStatus = new Label
        {
            Text = _accountModelRefreshStatus,
            Left = innerLeft,
            Top = y,
            Width = innerWidth,
            Height = MeasureWrappedLabelHeight(_accountModelRefreshStatus, noteFont, innerWidth, Scale(18)),
            Font = noteFont,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(discoveryStatus, _palette, true);
        _accountModelRefreshStatusLabel = discoveryStatus;
        panel.Controls.Add(discoveryStatus);

        panel.Height = discoveryStatus.Bottom + Scale(14);
        return panel;
    }

    private void ApplyModelCatalogSourcePillStyle(PillLabel pill, string catalogSource)
    {
        var dark = ThemeStyler.IsDark(_palette);
        var accent = catalogSource.ToLowerInvariant() switch
        {
            "official" => _palette.SuccessColor,
            "manual" => _palette.WarningColor,
            _ => _palette.SecondaryAccentColor
        };
        pill.FillColor = dark
            ? UiDesign.Blend(_palette.CardColor, accent, 0.18F)
            : UiDesign.Blend(_palette.SurfaceAltColor, accent, 0.11F);
        pill.StrokeColor = Color.FromArgb(dark ? 132 : 84, accent);
        pill.DotColor = dark
            ? UiDesign.Blend(accent, Color.White, 0.24F)
            : accent;
        pill.ForeColor = dark
            ? UiDesign.Blend(accent, Color.White, 0.36F)
            : UiDesign.Blend(_palette.TextColor, accent, 0.42F);
        pill.Invalidate();
    }

    private void UpdateModelCatalogSourceBadge(string text)
    {
        if (IsDisposed || _modelCatalogSourceLabel is not { IsDisposed: false } label)
        {
            return;
        }

        label.Text = text;
        _toolTip.SetToolTip(label, text);
        if (label is PillLabel pill && _modelCatalogSourceMaxWidth > 0)
        {
            var measuredWidth = MeasurePillLabelWidth(text, pill.Font, pill.Height, pill.ShowDot);
            var targetWidth = Math.Min(_modelCatalogSourceMaxWidth, measuredWidth);
            pill.Width = targetWidth;
            if (_modelCatalogSourceRightAligned)
            {
                pill.Left = _modelCatalogSourceRightEdge - targetWidth;
            }
            ApplyModelCatalogSourcePillStyle(pill, ModelCatalogService.Current.CatalogSource);
        }
    }

    private DataGridView CreateModelPricingGrid(
        ModelCatalogDocument catalog,
        int left,
        int top,
        int width,
        int height,
        int rowHeight,
        int headerHeight,
        Color gridBackColor)
    {
        var dark = ThemeStyler.IsDark(_palette);
        var altRowBack = dark
            ? UiDesign.Blend(gridBackColor, _palette.SecondaryAccentColor, 0.065F)
            : UiDesign.Blend(gridBackColor, _palette.PrimaryColor, 0.032F);
        var headerBack = dark
            ? UiDesign.Blend(_palette.SurfaceAltColor, _palette.PrimaryColor, 0.16F)
            : UiDesign.Blend(_palette.SurfaceAltColor, _palette.PrimaryColor, 0.075F);
        var headerFore = dark
            ? UiDesign.Blend(_palette.TextColor, Color.White, 0.12F)
            : UiDesign.Blend(_palette.TextColor, _palette.PrimaryColor, 0.22F);
        var gridDivider = dark
            ? Color.FromArgb(46, 62, 98)
            : UiDesign.Blend(_palette.DividerColor, _palette.BorderColor, 0.48F);

        var grid = new DataGridView
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = gridBackColor,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeight = headerHeight,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EnableHeadersVisualStyles = false,
            GridColor = gridDivider,
            RowHeadersVisible = false,
            RowTemplate = { Height = rowHeight },
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EditMode = DataGridViewEditMode.EditOnEnter,
            MultiSelect = false,
            ScrollBars = ScrollBars.Vertical
        };
        typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(grid, true, null);

        var cellFont = UiFontCache.Get(Font.FontFamily, 8.6F, FontStyle.Regular);
        var modelCellFont = UiFontCache.Get(Font.FontFamily, 8.6F, FontStyle.Bold);
        var headerFont = UiFontCache.Get(Font.FontFamily, 8.2F, FontStyle.Bold);
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = gridBackColor,
            ForeColor = _palette.TextColor,
            SelectionBackColor = UiDesign.Blend(gridBackColor, _palette.PrimaryColor, dark ? 0.34F : 0.20F),
            SelectionForeColor = _palette.TextColor,
            Font = cellFont,
            Padding = new Padding(8, 0, 8, 0),
            NullValue = ""
        };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = altRowBack,
            ForeColor = _palette.TextColor,
            SelectionBackColor = UiDesign.Blend(altRowBack, _palette.PrimaryColor, dark ? 0.34F : 0.20F),
            SelectionForeColor = _palette.TextColor,
            Padding = new Padding(8, 0, 8, 0),
            NullValue = ""
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = headerBack,
            ForeColor = headerFore,
            SelectionBackColor = headerBack,
            SelectionForeColor = headerFore,
            Font = headerFont,
            Padding = new Padding(8, 4, 8, 4),
            Alignment = DataGridViewContentAlignment.MiddleRight,
            WrapMode = DataGridViewTriState.True
        };

        AddModelPricingColumn(
            grid,
            "model",
            "模型",
            typeof(string),
            168F,
            ModelColumnMinimumWidth,
            readOnly: true,
            cellFont: modelCellFont);
        AddModelPricingColumn(grid, "input", "输入", typeof(double), 82F, InputColumnMinimumWidth);
        AddModelPricingColumn(
            grid,
            "cached",
            "缓存输入",
            typeof(double),
            92F,
            CachedInputColumnMinimumWidth);
        AddModelPricingColumn(grid, "output", "输出", typeof(double), 82F, OutputColumnMinimumWidth);
        AddModelPricingColumn(
            grid,
            "cacheWrite",
            "写入倍数",
            typeof(double),
            106F,
            CacheWriteColumnMinimumWidth);
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "usesLongContext",
            HeaderText = "长上下文",
            ToolTipText = "长上下文加价",
            ValueType = typeof(bool),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 96F,
            MinimumWidth = LongContextColumnMinimumWidth,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            FlatStyle = FlatStyle.Standard,
            HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });
        AddModelPricingColumn(
            grid,
            "threshold",
            "阈值 (token)",
            typeof(int),
            122F,
            ThresholdColumnMinimumWidth);
        AddModelPricingColumn(
            grid,
            "longInput",
            "长输入倍数",
            typeof(double),
            102F,
            LongInputColumnMinimumWidth);
        AddModelPricingColumn(
            grid,
            "longOutput",
            "长输出倍数",
            typeof(double),
            102F,
            LongOutputColumnMinimumWidth);

        foreach (var model in catalog.Models)
        {
            grid.Rows.Add(
                model.Id,
                model.InputUsdPerMillion,
                model.CachedInputUsdPerMillion,
                model.OutputUsdPerMillion,
                model.CacheWriteMultiplier,
                model.UsesLongContextPricing,
                model.LongContextThreshold,
                model.LongInputMultiplier,
                model.LongOutputMultiplier);
        }
        if (grid.Rows.Count > 0)
        {
            grid.FirstDisplayedScrollingRowIndex = 0;
        }
        grid.ClearSelection();
        grid.DataBindingComplete += (_, _) => grid.ClearSelection();
        foreach (Control child in grid.Controls)
        {
            if (child is ScrollBar scrollBar)
            {
                scrollBar.HandleCreated += (_, _) => NativeWindowTheme.ApplyScrollable(scrollBar, dark);
            }
        }
        grid.HandleCreated += (_, _) =>
        {
            NativeWindowTheme.ApplyScrollable(grid, dark);
            if (grid.Rows.Count > 0)
        {
            grid.FirstDisplayedScrollingRowIndex = 0;
        }
        grid.ClearSelection();
        };
        if (grid.IsHandleCreated)
        {
            NativeWindowTheme.ApplyScrollable(grid, dark);
            if (grid.Rows.Count > 0)
        {
            grid.FirstDisplayedScrollingRowIndex = 0;
        }
        grid.ClearSelection();
        }
        return grid;
    }

    private static void AddModelPricingColumn(
        DataGridView grid,
        string name,
        string header,
        Type valueType,
        float fillWeight,
        int minimumWidth,
        bool readOnly = false,
        Font? cellFont = null)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            ToolTipText = header,
            ValueType = valueType,
            ReadOnly = readOnly,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = fillWeight,
            MinimumWidth = minimumWidth,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Format = valueType == typeof(double) ? "0.####" : "N0",
                Font = cellFont,
                Alignment = readOnly
                    ? DataGridViewContentAlignment.MiddleLeft
                    : DataGridViewContentAlignment.MiddleRight
            }
        };
        column.HeaderCell.Style.Alignment = readOnly
            ? DataGridViewContentAlignment.MiddleLeft
            : DataGridViewContentAlignment.MiddleRight;
        grid.Columns.Add(column);
    }

    internal static void ValidateModelPricingGridLayout()
    {
        var minimumColumnsWidth =
            ModelColumnMinimumWidth +
            InputColumnMinimumWidth +
            CachedInputColumnMinimumWidth +
            OutputColumnMinimumWidth +
            CacheWriteColumnMinimumWidth +
            LongContextColumnMinimumWidth +
            ThresholdColumnMinimumWidth +
            LongInputColumnMinimumWidth +
            LongOutputColumnMinimumWidth;
        if (minimumColumnsWidth + ModelPricingGridChromeReserve > ModelPricingGridNarrowViewportWidth)
        {
            throw new InvalidOperationException(
                "Model-pricing columns no longer fit the default narrow viewport.");
        }
    }

    private void SaveManualModelCatalog(DataGridView grid, ComboBox defaultModel)
    {
        try
        {
            grid.EndEdit();
            var catalog = ModelCatalogService.CreateEditableCopy();
            catalog.DefaultModel = defaultModel.SelectedItem?.ToString()?.Trim() ?? string.Empty;
            var modelsById = catalog.Models.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in grid.Rows)
            {
                var id = Convert.ToString(row.Cells["model"].Value, CultureInfo.InvariantCulture)?.Trim() ?? "";
                if (!modelsById.TryGetValue(id, out var model))
                {
                    throw new InvalidDataException($"价格表中存在未知模型：{id}");
                }
                model.InputUsdPerMillion = ReadPositiveDouble(row, "input", id, "输入价格");
                model.CachedInputUsdPerMillion = ReadPositiveDouble(row, "cached", id, "缓存输入价格");
                model.OutputUsdPerMillion = ReadPositiveDouble(row, "output", id, "输出价格");
                model.CacheWriteMultiplier = ReadPositiveDouble(row, "cacheWrite", id, "缓存写入倍数");
                model.UsesLongContextPricing = Convert.ToBoolean(
                    row.Cells["usesLongContext"].Value,
                    CultureInfo.InvariantCulture);
                model.LongContextThreshold = ReadPositiveInt(row, "threshold", id, "长上下文阈值");
                model.LongInputMultiplier = ReadPositiveDouble(row, "longInput", id, "长输入倍数");
                model.LongOutputMultiplier = ReadPositiveDouble(row, "longOutput", id, "长输出倍数");
            }

            ModelCatalogService.SaveManual(catalog);
            ApplyModelCatalogToAccounts();
            _statusBox.Text = "模型与价格的手动设置已保存；下次官网检查成功后将以官网数据覆盖。";
            MessageBox.Show(
                this,
                "手动设置已保存。官网检查成功时会按官网数据覆盖这些设置。",
                "模型与价格",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            RenderCards();
        }
        catch (Exception error)
        {
            _statusBox.Text = "模型与价格未保存，请检查表格中的数值。";
            MessageBox.Show(this, error.Message, "无法保存模型与价格", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RestoreBundledModelCatalog()
    {
        var answer = MessageBox.Show(
            this,
            "将删除当前手动或官网缓存，恢复软件内置的官网价格快照。是否继续？",
            "恢复内置价格",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        try
        {
            ModelCatalogService.RestoreBundled();
            _modelCatalogAutoRefreshNote = string.Empty;
            ApplyModelCatalogToAccounts();
            _statusBox.Text = "已恢复软件内置的模型与价格快照。";
            RenderCards();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法恢复内置价格", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task CheckOfficialModelCatalogAsync(Button button)
    {
        if (_modelCatalogRefreshInProgress)
        {
            _statusBox.Text = "正在检查官网价格，请稍候。";
            return;
        }
        _modelCatalogRefreshInProgress = true;
        button.Enabled = false;
        string? proxyUri = null;
        try
        {
            if (!TryGetModelCatalogRefreshToken(out var cancellationToken)) return;
            proxyUri = GetModelCatalogProxyUri();
            _statusBox.Text = $"正在通过 {FormatProxyEndpoint(proxyUri)} 检查 OpenAI 官网模型与价格……";
            var result = await ModelCatalogService.CheckAndSaveOfficialAsync(proxyUri, cancellationToken);
            if (IsDisposed || Disposing) return;
            _nextAutomaticPriceCheckUtc = DateTimeOffset.UtcNow.AddHours(6);
            _modelCatalogAutoRefreshNote = string.Empty;
            ApplyModelCatalogToAccounts();
            var detail = result.Changes.Count == 0
                ? $"官网数据校验通过，当前默认模型为 {result.Current.DefaultModel}，价格没有变化。"
                : "已按官网更新：\r\n\r\n" + string.Join("\r\n", result.Changes);
            _statusBox.Text = result.Changes.Count == 0
                ? "官网模型与价格已核验，没有变化。"
                : "官网模型与价格已更新。";
            MessageBox.Show(this, detail, "官网模型与价格", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RenderCards();
        }
        catch (Exception error)
        {
            if (IsDisposed || Disposing) return;
            _nextAutomaticPriceCheckUtc = DateTimeOffset.UtcNow.AddMinutes(15);
            _statusBox.Text = "官网模型与价格检查失败，本地目录未修改。";
            var route = string.IsNullOrWhiteSpace(proxyUri)
                ? "未找到可用代理"
                : $"已尝试代理：{FormatProxyEndpoint(proxyUri)}";
            MessageBox.Show(
                this,
                $"{error.Message}\r\n\r\n{route}\r\n本地模型与价格没有被修改。",
                "无法自动检查官网",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _modelCatalogRefreshInProgress = false;
            if (!button.IsDisposed) button.Enabled = true;
        }
    }

    private string GetModelCatalogProxyUri()
    {
        if (SaveEditedPatGatewayProxy(updateStatus: false, markManual: false))
        {
            var configured = CodexCliService.BuildPatGatewayProxyUri(_appSettings);
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
        }

        var systemProxy = CodexCliService.GetWindowsProxyUri();
        if (CodexCliService.TryParseProxyEndpoint(
                systemProxy,
                out var address,
                out var port,
                out var scheme))
        {
            return $"{scheme}://{address}:{port}";
        }

        throw new InvalidOperationException("未检测到可用的 HTTP 代理。请先在上方点击“自动检测”，或填写代理地址和端口。");
    }

    private void ApplyModelCatalogToAccounts()
    {
        foreach (var account in _accounts.Where(account => account.IsAccessToken))
        {
            _codex.EnsureLocalPatAccountConfig(account);
        }
        InvalidateQuotaUsageCache(clearCachedData: true);
    }

    private static string GetModelCatalogSourceText(ModelCatalogDocument catalog)
    {
        var verified = DateTimeOffset.TryParse(
            catalog.VerifiedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : null;
        return catalog.CatalogSource.ToLowerInvariant() switch
        {
            "official" => verified is null
                ? "当前来源：官网校验"
                : $"当前来源：官网校验 · {verified}",
            "manual" => verified is null
                ? "当前来源：手动设置 · 官网检查成功后将覆盖"
                : $"当前来源：手动设置 · 上次官网校验 {verified} · 官网检查成功后将覆盖",
            _ => verified is null
                ? "当前来源：软件内置价格快照"
                : $"当前来源：软件内置官网快照 · {verified}"
        };
    }

    private static string FormatProxyEndpoint(string proxyUri)
    {
        return Uri.TryCreate(proxyUri, UriKind.Absolute, out var parsed)
            ? $"{parsed.Scheme}://{parsed.Host}:{parsed.Port}"
            : proxyUri;
    }

    private static double ReadPositiveDouble(
        DataGridViewRow row,
        string column,
        string model,
        string field)
    {
        var text = Convert.ToString(row.Cells[column].Value, CultureInfo.CurrentCulture)?.Trim() ?? "";
        if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
             !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) ||
            !double.IsFinite(value) || value <= 0)
        {
            throw new InvalidDataException($"{model} 的{field}必须是大于 0 的数字。");
        }
        return value;
    }

    private static int ReadPositiveInt(
        DataGridViewRow row,
        string column,
        string model,
        string field)
    {
        var text = Convert.ToString(row.Cells[column].Value, CultureInfo.CurrentCulture)?.Trim() ?? "";
        if (!int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var value) || value <= 0)
        {
            throw new InvalidDataException($"{model} 的{field}必须是大于 0 的整数。");
        }
        return value;
    }
}
