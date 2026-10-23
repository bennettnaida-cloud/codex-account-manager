namespace CodexAccountManager;

public partial class Form1
{
    private readonly System.Windows.Forms.Timer _modelCatalogRefreshTimer = new() { Interval = 60_000 };
    private readonly CancellationTokenSource _modelCatalogRefreshCancellation = new();
    private bool _modelCatalogRefreshInProgress;
    private bool _compatibleModelRefreshInProgress;
    private bool _accessTokenModelRefreshInProgress;
    private bool _modelCatalogEditorDirty;
    private DateTimeOffset _nextAutomaticPriceCheckUtc;
    private DateTimeOffset _nextAutomaticCompatibleModelCheckUtc;
    private DateTimeOffset _nextAutomaticAccessTokenModelCheckUtc;
    private Control? _modelCatalogPanel;
    private Label? _modelCatalogSourceLabel;
    private Label? _accountModelRefreshStatusLabel;
    private string _accountModelRefreshStatus = "账号模型独立同步；官网价格页失败不会关闭模型更新。";
    private string _automaticCompatibleModelStatus = "API：尚未检查";
    private string _automaticAccessTokenModelStatus = "AT：尚未检查";

    private void SetAutomaticAccountModelStatus(bool accessToken, bool success)
    {
        var status = (accessToken ? "AT：" : "API：") + (success ? "同步完成" : "有同步失败，旧目录保留");
        if (accessToken) _automaticAccessTokenModelStatus = status;
        else _automaticCompatibleModelStatus = status;
        SetAccountModelRefreshStatus(_automaticAccessTokenModelStatus + "；" + _automaticCompatibleModelStatus + "。可点击同步查看逐账号结果。");
    }

    private void SetAccountModelRefreshStatus(string status)
    {
        _accountModelRefreshStatus = status;
        if (!IsDisposed && _accountModelRefreshStatusLabel is { IsDisposed: false } label)
            label.Text = status;
    }

    private async Task RefreshAccountModelsManuallyAsync(Button button)
    {
        if (_compatibleModelRefreshInProgress || _accessTokenModelRefreshInProgress)
        {
            _statusBox.Text = "账号模型正在后台同步，请等待本次完成后再检查。";
            return;
        }
        _compatibleModelRefreshInProgress = _accessTokenModelRefreshInProgress = true;
        button.Enabled = false;
        SetAccountModelRefreshStatus("正在使用每个账号自己的凭据与绑定节点获取模型，不发送聊天请求…");
        try
        {
            var cancellationToken = _modelCatalogRefreshCancellation.Token;
            var results = await Task.Run(async () =>
            {
                var lines = new List<string>();
                foreach (var account in new AccountStore().LoadAccounts().Where(a => a.IsAccessToken || a.IsCompatibleApi))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var count = account.IsAccessToken
                            ? await _codex.RefreshAccessTokenModelCatalogAsync(account.Name, cancellationToken)
                            : await _codex.RefreshCompatibleModelCatalogAsync(account.Name, cancellationToken);
                        var config = File.ReadAllText(Path.Combine(account.CodexHome, "config.toml"));
                        var custom = config.Replace("\r", "").Split('\n').TakeWhile(line => !line.TrimStart().StartsWith('['))
                            .Any(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*model_catalog_json\s*=") &&
                                         !line.Contains("# codex-account-manager-model-catalog", StringComparison.Ordinal));
                        lines.Add($"{account.Name}：已获取 {count} 条模型记录" +
                            (custom ? "；但当前使用自定义 model_catalog_json，未覆盖它，请自行检查该文件。" : "。"));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { lines.Add($"{account.Name}：同步失败，保留旧目录。{ex.Message}"); }
                }
                return lines;
            }, cancellationToken);
            if (IsDisposed || Disposing) return;
            SetAccountModelRefreshStatus($"{DateTime.Now:HH:mm:ss} 已检查 {results.Count} 个 AT/API 账号；查看弹窗中的逐账号结果。");
            _statusBox.Text = "模型检查完成；未切换账号或关闭 Codex。";
            MessageBox.Show(this, string.Join("\r\n\r\n", results) +
                "\r\n\r\nOAuth 模型由官方 Codex 获取；AT 保留上游隐藏状态。API 的内置完整目录或自定义目录不代表服务端授权。" +
                "\r\n已打开的 Codex 可能仍持有旧目录，请自行完全退出并重新启动以加载新目录。官网价格更新与此操作无关。",
                "账号可用模型同步结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing) SetAccountModelRefreshStatus("模型同步未完成：" + ex.Message);
        }
        finally
        {
            _compatibleModelRefreshInProgress = _accessTokenModelRefreshInProgress = false;
            if (!button.IsDisposed) button.Enabled = true;
        }
    }

    private void ConfigureAutomaticModelCatalogRefresh()
    {
        Shown += async (_, _) =>
        {
            _modelCatalogRefreshTimer.Start();
            await RefreshAutomaticCatalogsAsync();
        };
        _modelCatalogRefreshTimer.Tick += async (_, _) => await RefreshAutomaticCatalogsAsync();
        FormClosed += (_, _) =>
        {
            _modelCatalogRefreshTimer.Stop();
            _modelCatalogRefreshTimer.Dispose();
            _modelCatalogRefreshCancellation.Cancel();
            _modelCatalogRefreshCancellation.Dispose();
        };
    }

    private Task RefreshAutomaticCatalogsAsync() => Task.WhenAll(
        RefreshCompatibleModelsAutomaticallyAsync(),
        RefreshAccessTokenModelsAutomaticallyAsync(),
        RefreshModelCatalogAutomaticallyAsync());

    private async Task RefreshCompatibleModelsAutomaticallyAsync()
    {
        if (IsDisposed || Disposing || _compatibleModelRefreshInProgress ||
            DateTimeOffset.UtcNow < _nextAutomaticCompatibleModelCheckUtc) return;
        _compatibleModelRefreshInProgress = true;
        _nextAutomaticCompatibleModelCheckUtc = DateTimeOffset.UtcNow.AddMinutes(15);
        var cancellationToken = _modelCatalogRefreshCancellation.Token;
        try
        {
            // Discovery runs independently of the price update preference and off the UI
            // thread. Updating files never changes the selected model or restarts Codex.
            await Task.Run(() => _codex.RefreshAllCompatibleModelCatalogsAsync(cancellationToken), cancellationToken);
            if (!IsDisposed && !Disposing) SetAutomaticAccountModelStatus(false, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ManagerLifecycleDiagnostics.WriteException("compatible-model-auto-refresh-failed", ex);
            if (!IsDisposed && !Disposing) SetAutomaticAccountModelStatus(false, false);
        }
        finally
        {
            _compatibleModelRefreshInProgress = false;
        }
    }

    private async Task RefreshModelCatalogAutomaticallyAsync()
    {
        if (IsDisposed || Disposing || _modelCatalogRefreshInProgress ||
            !ModelCatalogService.Current.AutomaticPriceUpdatesEnabled ||
            DateTimeOffset.UtcNow < _nextAutomaticPriceCheckUtc) return;
        _modelCatalogRefreshInProgress = true;
        _nextAutomaticPriceCheckUtc = DateTimeOffset.UtcNow.AddMinutes(15);
        try
        {
            // Read the configured proxy without saving edited controls or changing accounts.
            var proxy = CodexCliService.BuildPatGatewayProxyUri(_appSettings);
            if (string.IsNullOrWhiteSpace(proxy)) proxy = CodexCliService.GetWindowsProxyUri();
            await ModelCatalogService.CheckAndSaveOfficialAsync(proxy, _modelCatalogRefreshCancellation.Token);
            if (IsDisposed || Disposing) return;
            _nextAutomaticPriceCheckUtc = DateTimeOffset.UtcNow.AddHours(6);
            InvalidateQuotaUsageCache(clearCachedData: true);
            if (_modelCatalogPanel is { IsDisposed: false, Visible: true } && !_modelCatalogEditorDirty)
                RenderCards();
            else if (_modelCatalogSourceLabel is { IsDisposed: false })
                _modelCatalogSourceLabel.Text = GetModelCatalogSourceText(ModelCatalogService.Current) + " · 官网价格已更新";
        }
        catch (OperationCanceledException) { }
        catch
        {
            // A price refresh must never display a startup error or discard the last good catalog.
            if (!IsDisposed && _modelCatalogSourceLabel is { IsDisposed: false })
                _modelCatalogSourceLabel.Text = GetModelCatalogSourceText(ModelCatalogService.Current) + " · 自动检查失败，稍后重试";
        }
        finally
        {
            _modelCatalogRefreshInProgress = false;
        }
    }

    private async Task RefreshAccessTokenModelsAutomaticallyAsync()
    {
        if (IsDisposed || Disposing || _accessTokenModelRefreshInProgress ||
            DateTimeOffset.UtcNow < _nextAutomaticAccessTokenModelCheckUtc) return;
        _accessTokenModelRefreshInProgress = true;
        _nextAutomaticAccessTokenModelCheckUtc = DateTimeOffset.UtcNow.AddMinutes(15);
        var cancellationToken = _modelCatalogRefreshCancellation.Token;
        try
        {
            // Query the account's remote catalog independently of the static desktop
            // catalog. No prompt is sent; failures retain the last good file and are
            // counted as failures, never reported as a successful online refresh.
            await Task.Run(
                () => _codex.RefreshAllAccessTokenModelCatalogsAsync(cancellationToken),
                cancellationToken);
            if (!IsDisposed && !Disposing) SetAutomaticAccountModelStatus(true, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ManagerLifecycleDiagnostics.WriteException("access-token-model-auto-refresh-failed", ex);
            if (!IsDisposed && !Disposing) SetAutomaticAccountModelStatus(true, false);
        }
        finally
        {
            _accessTokenModelRefreshInProgress = false;
        }
    }
}
