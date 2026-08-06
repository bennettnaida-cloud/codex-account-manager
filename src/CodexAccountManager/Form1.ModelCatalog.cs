using System.Globalization;

namespace CodexAccountManager;

public partial class Form1
{
    private Control CreateModelCatalogPanel(int width)
    {
        var catalog = ModelCatalogService.CreateEditableCopy();
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 500,
            Radius = 16,
            BorderColor = _palette.BorderColor,
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, _palette.SecondaryAccentColor, 0.025F),
            AccentColor = _palette.SecondaryAccentColor,
            AccentWidth = 3,
            ShadowColor = Color.FromArgb(26, _palette.ShadowColor),
            Margin = new Padding(0, 0, CardGap, CardGap),
            Padding = new Padding(22)
        };

        const int innerLeft = 24;
        var innerWidth = width - 48;
        var title = new Label
        {
            Text = "模型与价格",
            Left = innerLeft,
            Top = 22,
            Width = 260,
            Height = 36,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var subtitle = new Label
        {
            Text = "价格单位为美元 / 百万 token；可以手动调整，官网检查成功后以官网结果为准。",
            Left = innerLeft,
            Top = 60,
            Width = innerWidth,
            Height = 28,
            Font = new Font(Font.FontFamily, 8.8F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(subtitle, _palette, true);
        panel.Controls.Add(subtitle);

        var source = new Label
        {
            Text = GetModelCatalogSourceText(catalog),
            Left = innerLeft,
            Top = 91,
            Width = innerWidth,
            Height = 26,
            Font = new Font(Font.FontFamily, 8.4F, FontStyle.Bold),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(source, _palette, true);
        panel.Controls.Add(source);

        var defaultLabel = new Label
        {
            Text = "默认模型",
            Left = innerLeft,
            Top = 125,
            Width = 112,
            Height = 38,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(defaultLabel, _palette, true);
        panel.Controls.Add(defaultLabel);

        var defaultModel = new ThemedComboBox
        {
            Left = innerLeft + 116,
            Top = 124,
            Width = Math.Min(360, Math.Max(220, innerWidth - 116)),
            Height = 38,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font(Font.FontFamily, 9F),
            IntegralHeight = false,
            DropDownHeight = 240
        };
        foreach (var choice in catalog.Models
                     .SelectMany(model => model.Aliases.Append(model.Id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            defaultModel.Items.Add(choice);
        }
        defaultModel.SelectedItem = defaultModel.Items.Cast<string>().FirstOrDefault(item =>
            item.Equals(catalog.DefaultModel, StringComparison.OrdinalIgnoreCase));
        defaultModel.SelectedIndex = defaultModel.SelectedIndex >= 0 ? defaultModel.SelectedIndex : 0;
        ThemeStyler.ApplyComboBox(defaultModel, _palette);
        panel.Controls.Add(defaultModel);

        var grid = CreateModelPricingGrid(catalog, innerLeft, 174, innerWidth, 210);
        panel.Controls.Add(grid);

        var note = new Label
        {
            Text = "长上下文阈值按 token 计；取消“长上下文加价”后，阈值与两个长上下文倍数不参与计费。",
            Left = innerLeft,
            Top = 393,
            Width = innerWidth,
            Height = 28,
            Font = new Font(Font.FontFamily, 8.3F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(note, _palette, true);
        panel.Controls.Add(note);

        const int actionGap = 10;
        var actionWidth = Math.Clamp((innerWidth - (actionGap * 2)) / 3, 154, 210);
        var actionsWidth = (actionWidth * 3) + (actionGap * 2);
        var actionLeft = innerLeft + innerWidth - actionsWidth;
        var restore = MakeActionButton("恢复内置价格", actionLeft, 434, actionWidth, false);
        restore.AccessibleName = "恢复内置模型与价格";
        restore.Click += (_, _) => RestoreBundledModelCatalog();
        panel.Controls.Add(restore);

        var save = MakeActionButton("保存手动设置", actionLeft + actionWidth + actionGap, 434, actionWidth, false);
        save.AccessibleName = "保存手动模型与价格";
        save.Click += (_, _) => SaveManualModelCatalog(grid, defaultModel);
        panel.Controls.Add(save);

        var official = MakeActionButton(
            "通过代理检查官网",
            actionLeft + ((actionWidth + actionGap) * 2),
            434,
            actionWidth,
            true);
        official.AccessibleName = "通过当前代理检查 OpenAI 官网模型与价格";
        official.Click += async (_, _) => await CheckOfficialModelCatalogAsync(official);
        _toolTip.SetToolTip(official, "使用上方已检测或手动填写的 HTTP 代理访问 OpenAI 官方模型页面。检查成功后覆盖手动设置。");
        panel.Controls.Add(official);

        return panel;
    }

    private DataGridView CreateModelPricingGrid(
        ModelCatalogDocument catalog,
        int left,
        int top,
        int width,
        int height)
    {
        var grid = new DataGridView
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = _palette.InputBackColor,
            BorderStyle = BorderStyle.FixedSingle,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeight = 46,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EnableHeadersVisualStyles = false,
            GridColor = _palette.DividerColor,
            RowHeadersVisible = false,
            RowTemplate = { Height = 42 },
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EditMode = DataGridViewEditMode.EditOnEnter,
            MultiSelect = false,
            ScrollBars = ScrollBars.Both
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = _palette.InputBackColor,
            ForeColor = _palette.TextColor,
            SelectionBackColor = UiDesign.Blend(_palette.InputBackColor, _palette.PrimaryColor, 0.24F),
            SelectionForeColor = _palette.TextColor,
            Font = new Font(Font.FontFamily, 8.5F),
            Padding = new Padding(7, 0, 7, 0),
            NullValue = ""
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = _palette.SurfaceAltColor,
            ForeColor = _palette.TextColor,
            SelectionBackColor = _palette.SurfaceAltColor,
            SelectionForeColor = _palette.TextColor,
            Font = new Font(Font.FontFamily, 8.1F, FontStyle.Bold),
            Padding = new Padding(6, 0, 6, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            WrapMode = DataGridViewTriState.False
        };

        AddModelPricingColumn(grid, "model", "模型", typeof(string), 165F, 170, readOnly: true);
        AddModelPricingColumn(grid, "input", "输入", typeof(double), 84F, 82);
        AddModelPricingColumn(grid, "cached", "缓存输入", typeof(double), 92F, 92);
        AddModelPricingColumn(grid, "output", "输出", typeof(double), 84F, 82);
        AddModelPricingColumn(grid, "cacheWrite", "写入倍数", typeof(double), 112F, 105);
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "usesLongContext",
            HeaderText = "长上下文",
            ValueType = typeof(bool),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 104F,
            MinimumWidth = 100,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            FlatStyle = FlatStyle.Standard
        });
        AddModelPricingColumn(grid, "threshold", "阈值 (token)", typeof(int), 122F, 116);
        AddModelPricingColumn(grid, "longInput", "长输入倍数", typeof(double), 104F, 98);
        AddModelPricingColumn(grid, "longOutput", "长输出倍数", typeof(double), 104F, 98);

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
        grid.ClearSelection();
        return grid;
    }

    private static void AddModelPricingColumn(
        DataGridView grid,
        string name,
        string header,
        Type valueType,
        float fillWeight,
        int minimumWidth,
        bool readOnly = false)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            ValueType = valueType,
            ReadOnly = readOnly,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = fillWeight,
            MinimumWidth = minimumWidth,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Format = valueType == typeof(double) ? "0.####" : "0",
                Alignment = readOnly
                    ? DataGridViewContentAlignment.MiddleLeft
                    : DataGridViewContentAlignment.MiddleRight
            }
        });
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
        button.Enabled = false;
        string? proxyUri = null;
        try
        {
            proxyUri = GetModelCatalogProxyUri();
            _statusBox.Text = $"正在通过 {FormatProxyEndpoint(proxyUri)} 检查 OpenAI 官网模型与价格……";
            var result = await ModelCatalogService.CheckAndSaveOfficialAsync(proxyUri);
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
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value <= 0)
        {
            throw new InvalidDataException($"{model} 的{field}必须是大于 0 的整数。");
        }
        return value;
    }
}
