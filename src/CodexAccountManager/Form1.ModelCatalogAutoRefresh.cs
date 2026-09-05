namespace CodexAccountManager;

public partial class Form1
{
    private readonly System.Windows.Forms.Timer _modelCatalogRefreshTimer = new() { Interval = 60_000 };
    private readonly CancellationTokenSource _modelCatalogRefreshCancellation = new();
    private bool _modelCatalogRefreshInProgress;
    private bool _modelCatalogEditorDirty;
    private DateTimeOffset _nextAutomaticPriceCheckUtc;
    private Control? _modelCatalogPanel;
    private Label? _modelCatalogSourceLabel;

    private void ConfigureAutomaticModelCatalogRefresh()
    {
        Shown += async (_, _) =>
        {
            _modelCatalogRefreshTimer.Start();
            await RefreshModelCatalogAutomaticallyAsync();
        };
        _modelCatalogRefreshTimer.Tick += async (_, _) => await RefreshModelCatalogAutomaticallyAsync();
        FormClosed += (_, _) =>
        {
            _modelCatalogRefreshTimer.Stop();
            _modelCatalogRefreshTimer.Dispose();
            _modelCatalogRefreshCancellation.Cancel();
            _modelCatalogRefreshCancellation.Dispose();
        };
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
}
