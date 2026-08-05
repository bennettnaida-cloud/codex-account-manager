using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Text;

namespace CodexAccountManager;

public partial class Form1 : Form
{
    private enum WorkspaceView
    {
        AccountSwitch,
        UnifiedHistory,
        StatusCheck,
        QuotaUsage,
        ThemeSettings,
        SystemConfig
    }

    private enum QuotaTrendScope
    {
        Realtime,
        Monitoring
    }

    private enum UsagePricingPolicy
    {
        AccessTokenSub2ApiParity,
        CompatibleApiProvider
    }

    private sealed record QuotaUsageLoadResult(
        UsageReport? Report,
        DateTime LatestWriteTimeUtc,
        DateTime LoadedAtUtc,
        int InvalidationVersion);

    private sealed record UnifiedHistoryLoadResult(
        IReadOnlyList<UnifiedThreadRecord> Threads,
        int InvalidationVersion);

    private sealed record UnifiedHistoryContentIndexResult(
        IReadOnlyDictionary<string, string> SearchTextByThreadId,
        int InvalidationVersion);

    private sealed record QuotaTrendDisplayData(
        IReadOnlyList<PassiveQuotaTrendPoint> Points,
        string EmptyText,
        DateTimeOffset FromUtc,
        DateTimeOffset ThroughUtc,
        IReadOnlyList<PassiveQuotaAssessmentWindow>? AssessmentWindows = null);

    private sealed record AccountGroupSection(
        string Key,
        string Title,
        IReadOnlyList<AccountRecord> Accounts);

    private sealed record WorkspaceViewCacheEntry(
        int WorkspaceWidth,
        Control[] Controls);

    private readonly record struct TokenRowGeometry(
        int Height,
        Rectangle Name,
        Rectangle AuthKind,
        Rectangle State,
        Rectangle Detail,
        Rectangle Badge,
        Rectangle Update);

    private readonly record struct CodexAppearanceRowActionGeometry(
        Rectangle Detail,
        Rectangle Launch,
        int RowHeight);

    private readonly record struct StatusTokenRowGeometry(
        int Height,
        Rectangle Name,
        Rectangle AuthKind,
        Rectangle State,
        Rectangle Detail,
        Rectangle StatusBadge,
        Rectangle TokenBadge,
        Rectangle Check,
        Rectangle Update);

    private readonly record struct QuotaUsageHorizontalGeometry(
        int NameWidth,
        int RightWidth,
        int MiddleLeft,
        int MiddleWidth,
        int MetricWidth);

    private sealed class UsageMetricBinding
    {
        public required Label Cost { get; init; }
        public required Label Tokens { get; init; }
    }

    private sealed class QuotaUsageTableRowBinding
    {
        public Label? RegularInput { get; init; }
        public Label? CachedInput { get; init; }
        public Label? CacheWrite { get; init; }
        public Label? Output { get; init; }
        public required Label Total { get; init; }
        public Label? CompactSplit { get; init; }
    }

    private sealed class QuotaUsageTableBinding
    {
        public required QuotaUsageTableRowBinding[] Rows { get; init; }
    }

    private sealed class QuotaUsageRowBinding
    {
        public required string AccountName { get; init; }
        public required string QuotaLimitType { get; init; }
        public required Label Kind { get; init; }
        public required UsageMetricBinding[] Metrics { get; init; }
        public required Label Detail { get; init; }
        public Label? SecondaryDetail { get; init; }
        public PillLabel? CapacityStatus { get; set; }
        public Label? CapacitySummary { get; set; }
        public PillLabel? PrimaryQuota { get; set; }
        public PillLabel? SecondaryQuota { get; set; }
        public QuotaProgressBar? PrimaryProgress { get; set; }
        public QuotaProgressBar? SecondaryProgress { get; set; }
    }

    private sealed class PassiveQuotaMonitorBinding
    {
        public required PassiveQuotaGauge Gauge { get; init; }
        public required Label Status { get; init; }
        public required Label Summary { get; init; }
        public required Label Progress { get; init; }
        public required UsageMetricBinding[] UsageMetrics { get; init; }
        public QuotaProgressBar? MeasurementProgress { get; init; }
        public PillLabel? OfficialQuota { get; init; }
        public PillLabel? ResetCount { get; init; }
        public Button? ResetAction { get; init; }
    }

    private sealed class QuotaUsageDetailBinding
    {
        public required string AccountName { get; init; }
        public required string QuotaLimitType { get; init; }
        public required Label Subtitle { get; init; }
        public required Label Meta { get; init; }
        public required UsageMetricBinding[] Metrics { get; init; }
        public required PassiveQuotaMonitorBinding Monitor { get; init; }
        public required QuotaTrendChart Chart { get; init; }
        public required Button ExportButton { get; init; }
        public required ModelUsageDistributionControl ModelDistribution { get; init; }
        public required QuotaUsageTableBinding UsageTable { get; init; }
        public required bool ShowsCacheWriteColumn { get; init; }
    }

    private const int AccountListWidth = 430;
    private const int AccountSummaryMinWidth = 360;
    private const int AccountSummaryHeight = 126;
    private const int AccountRowMinWidth = 560;
    private const int AccountSwitchHorizontalMinWidth = 920;
    private const int QuotaUsageHorizontalMinWidth = 900;
    private const int WorkspaceHeroHeight = 160;
    private const int WorkspaceScrollbarEdgeInset = 2;
    private const int CardGap = 22;
    private const int UnifiedHistoryPageSize = 8;
    private const int UnifiedHistorySearchMaxCharactersPerThread = 384 * 1024;
    private static readonly (string Label, ThemeMode Mode)[] ThemeOptions =
    [
        ("跟随系统", ThemeMode.System),
        ("极光浅色", ThemeMode.Light),
        ("青瓷浅色", ThemeMode.PorcelainLight),
        ("深海夜色", ThemeMode.Dark),
        ("星云夜色", ThemeMode.NebulaDark)
    ];
    private static readonly CodexAppearanceOption[] CodexAppearanceOptions =
    [
        new("manager-light", "极光浅色", "account-manager-aurora-light.jpg", "近白工作台与轻柔蓝紫极光，清爽但不偏蓝。", false, "github", "#4D8DFF", "#FFFFFF", "#172033", 86, 0.76F, 0.435F, ThemeMode.Light, "manager"),
        new("manager-porcelain-light", "青瓷浅色", "account-manager-porcelain-light.jpg", "Account Manager 的低饱和青瓷绿色浅色风格。", false, "everforest", "#4E8F84", "#F5FAF8", "#183C39", 88, 0.76F, 0.435F, ThemeMode.PorcelainLight, "manager"),
        new("manager-dark", "深海夜色", "account-manager-deep-sea.jpg", "Account Manager 的深海蓝黑高可读夜间风格。", true, "tokyo-night", "#60A5FA", "#091526", "#F1F6FF", 92, 0.76F, 0.435F, ThemeMode.Dark, "manager"),
        new("manager-nebula-dark", "星云夜色", "account-manager-nebula-orbit.jpg", "融合额度玻璃星球与模型轨道的紫色星云夜间风格。", true, "night-owl", "#B49AFF", "#171229", "#FCFAFF", 94, 0.76F, 0.435F, ThemeMode.NebulaDark, "manager"),
        new("official-default", "Codex 官方默认", null, "移除 Account Manager 写入的图片背景与注入配置，恢复 Codex 官方界面。", false, "Codex 默认", "#5B8CFF", "#F5F6F8", "#20242C", 86, 0.50F, 0.50F),
        new("preset-gothic-void-crusade", "哥特虚空远征", "preset-gothic-void-crusade.jpg", "哥特科幻背景，左侧留白承载 Codex 原生内容。", true, "tokyo-night", "#C8A55A", "#171513", "#F3EAD7", 94, 0.76F, 0.45F, ThemeMode.Dark, StaticPreviewAssetName: "preset-gothic-void-crusade-preview.jpg"),
        new("preset-arina-hashimoto", "桥本有菜", "preset-arina-hashimoto.jpg", "玫瑰浅色背景，左侧留白承载 Codex 原生内容。", false, "rose-pine", "#D86A83", "#FFF7F8", "#402C33", 88, 0.72F, 0.45F, ThemeMode.Light, StaticPreviewAssetName: "preset-arina-hashimoto-preview.jpg"),
        new("preset-midnight-aurora", "午夜极光", "preset-midnight-aurora.jpg", "深蓝夜幕里流动的极光，安静又有张力。", true, "tokyo-night", "#2DE1C2", "#0A0E1A", "#EAF4FF", 94, 0.72F, 0.38F),
        new("preset-sakura-dawn", "樱粉晨曦", "preset-sakura-dawn.jpg", "把喜欢的粉色调进工作台，温柔但不刺眼。", false, "rose-pine", "#F0607A", "#FDF3F5", "#3A2A30", 88, 0.68F, 0.40F),
        new("preset-amber-dusk", "琥珀黄昏", "preset-amber-dusk.jpg", "暖金色的黄昏光，适合长时间的深夜编码。", true, "gruvbox", "#FFB347", "#17110C", "#FFF3E6", 94, 0.74F, 0.42F),
        new("preset-forest-mist", "森野薄雾", "preset-forest-mist.jpg", "墨绿与晨雾，给屏幕一点自然的呼吸。", true, "everforest", "#7FD1B9", "#0D1A16", "#E8F5EE", 94, 0.70F, 0.40F),
        new("preset-cyber-neon", "赛博霓虹", "preset-cyber-neon.jpg", "近黑底色上的品红与青，高对比的赛博感。", true, "matrix", "#16E0FF", "#07070D", "#EAFCFF", 96, 0.72F, 0.38F),
        new("custom", "我的自定义主题", null, "使用本地照片，并自定义颜色、对比度与代码配色。", true, "tokyo-night", "#4DB892", "#0D1A16", "#E8F5EE", 92, 0.50F, 0.50F)
    ];
    private static readonly TimeSpan QuotaMinimumRefreshInterval = TimeSpan.FromMilliseconds(250);
    // Live logs can append several related records for one turn.  A sub-second visual update is
    // not useful if it repeatedly walks the session tree and rebuilds an already-current report.
    private static readonly TimeSpan QuotaLogRefreshMinimumInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QuotaRollingWindowRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OfficialQuotaFocusedRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OfficialQuotaBackgroundRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly AccountStore _store = new();
    private readonly CodexCliService _codex = new();
    private readonly SharedHistoryService _sharedHistory = new();
    private readonly SharedThreadTranscriptService _threadTranscript = new();
    private readonly Dictionary<string, LoginStatus> _statusCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BufferedTableLayoutPanel _accountLayout = new();
    private readonly BufferedFlowLayoutPanel _cardsPanel = new();
    private readonly Panel _detailPanel = new();
    private readonly TextBox _searchBox = new();
    private readonly TextBox _projectPathBox = new();
    private readonly TextBox _patGatewayProxyAddressBox = new();
    private readonly TextBox _patGatewayProxyPortBox = new();
    private readonly Label _patGatewayProxyDetectionLabel = new();
    private readonly Label _statusBox = new();
    private readonly ThemePicker _themeModePicker = new();
    private readonly ModernButton _addAccountNavButton = new();
    private readonly ModernButton _accountSwitchNavButton = new();
    private readonly ModernButton _unifiedHistoryNavButton = new();
    private readonly ModernButton _statusCheckNavButton = new();
    private readonly ModernButton _quotaUsageNavButton = new();
    private readonly ModernButton _themeSettingsNavButton = new();
    private readonly ModernButton _systemConfigNavButton = new();
    private readonly ModernButton _checkUpdatesButton = new();
    private readonly AppUpdateService _updateService = new();
    private readonly Label _headerTitle = new();
    private readonly Label _headerSubtitle = new();
    private readonly ToolTip _toolTip = new()
    {
        AutoPopDelay = 30000,
        InitialDelay = 450,
        ReshowDelay = 150,
        ShowAlways = true
    };
    private readonly System.Windows.Forms.Timer _quotaRefreshTimer = new();
    private readonly System.Windows.Forms.Timer _layoutRefreshTimer = new() { Interval = 120 };
    private FileSystemWatcher? _usageLogWatcher;
    private int _usageLogDirty = 1;
    private bool _usageLogWatcherReady;
    private readonly ThemeService _themeService;
    private readonly UsageTracker _usageTracker;
    private readonly PassiveQuotaMonitoringService _passiveQuotaMonitoring;
    private readonly QuotaSnapshotStore _quotaSnapshotStore;
    private readonly AppSettings _appSettings;
    private readonly Dictionary<string, ResetCreditViewState> _resetCreditState =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LiveRateLimitSnapshot> _liveRateLimitCache =
        new(StringComparer.OrdinalIgnoreCase);
    // A full passive-capacity replay walks every event in an account's monitoring epoch.
    // Keep it event-driven: the report can still refresh its rolling UI every few seconds,
    // but an unchanged token/official-quota snapshot must not repeatedly re-analyse the
    // same long history on the UI refresh path.
    private readonly Dictionary<string, string> _passiveQuotaMonitoringInputSignatures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _officialQuotaRefreshedAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _officialQuotaRefreshAttemptedAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _officialQuotaRefreshInProgress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _quotaRuntimeStateGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedAccountGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WorkspaceView, WorkspaceViewCacheEntry> _workspaceViewCache = [];
    private ThemePalette _palette;
    private List<AccountRecord> _accounts = [];
    private WorkspaceView _activeView = WorkspaceView.AccountSwitch;
    private string? _selectedAccountName;
    private string? _currentAccountName;
    private bool _showAccountDetail;
    private bool _showCodexAppearanceDetail;
    private string _selectedCodexAppearanceId = "preset-midnight-aurora";
    private bool _renderingCards;
    private bool _cardsAnimationRefreshQueued;
    private long _lastHeaderAnimationPumpTimestamp;
    private int _lastCardsPanelOuterWidth = -1;
    private int _renderedWorkspaceWidth = -1;
    private bool _suppressSearchRender;
    private bool _openingUnifiedThread;
    private bool _windowWasMinimized;
    private DateTime _lastQuotaUsageLogWriteTimeUtc;
    private DateTime _quotaUsageLoadedAtUtc;
    private UsageReport? _quotaUsageCache;
    private IReadOnlyList<UnifiedThreadRecord>? _unifiedHistoryCache;
    private IReadOnlyDictionary<string, string> _unifiedHistoryContentIndex =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _unifiedThreadDeleteGate = new(1, 1);
    private Task<QuotaUsageLoadResult>? _quotaUsageLoadTask;
    private Task<UnifiedHistoryLoadResult>? _unifiedHistoryLoadTask;
    private Task<UnifiedHistoryContentIndexResult>? _unifiedHistoryContentIndexTask;
    private CancellationTokenSource? _unifiedHistoryContentIndexCancellation;
    private Exception? _quotaUsageLoadError;
    private Exception? _unifiedHistoryLoadError;
    private Exception? _unifiedHistoryContentIndexError;
    private int _quotaUsageInvalidationVersion;
    private int _unifiedHistoryInvalidationVersion;
    private int _quotaUsageCacheVersion = -1;
    private int _unifiedHistoryCacheVersion = -1;
    private int _unifiedHistoryContentIndexVersion = -1;
    private int _unifiedHistoryContentIndexRequestedVersion = -1;
    private int _workspaceLoadGeneration;
    private int _quotaUsageRequestedGeneration;
    private int _unifiedHistoryVisibleLimit = UnifiedHistoryPageSize;
    private TimeSpan _quotaTrendRange = TimeSpan.FromHours(24);
    private QuotaTrendMetric _quotaTrendMetric = QuotaTrendMetric.Tokens;
    private readonly Dictionary<string, QuotaTrendScope> _quotaTrendScopes =
        new(StringComparer.Ordinal);
    private bool _formClosed;
    private bool _updateCheckRunning;
    private CancellationTokenSource? _proxyDetectionCancellation;
    private ModernInputShell? _searchShell;
    private ModernInputShell? _projectPathShell;
    private ModernInputShell? _patGatewayProxyAddressShell;
    private ModernInputShell? _patGatewayProxyPortShell;
    private RoundedPanel? _headerPanel;
    private BufferedTableLayoutPanel? _contentLayout;
    private BufferedFlowLayoutPanel? _controlsRow;

    public Form1()
    {
        _themeService = new ThemeService(_store.RootPath);
        _usageTracker = new UsageTracker(_store.RootPath);
        _passiveQuotaMonitoring = new PassiveQuotaMonitoringService(_store.RootPath);
        _quotaSnapshotStore = new QuotaSnapshotStore(_store.RootPath);
        _appSettings = _themeService.LoadSettings();
        _appSettings.CustomCodexTheme ??= new CustomCodexTheme();
        if (string.Equals(_appSettings.CodexAppearancePresetId, "manager", StringComparison.OrdinalIgnoreCase))
        {
            _appSettings.CodexAppearancePresetId = _appSettings.ThemeMode switch
            {
                ThemeMode.Light => "manager-light",
                ThemeMode.PorcelainLight => "manager-porcelain-light",
                ThemeMode.Dark => "manager-dark",
                ThemeMode.NebulaDark => "manager-nebula-dark",
                _ => "manager-light"
            };
        }
        else if (FindCodexAppearanceOptionIndex(_appSettings.CodexAppearancePresetId) < 0)
        {
            _appSettings.CodexAppearancePresetId = "preset-midnight-aurora";
        }
        _selectedCodexAppearanceId = _appSettings.CodexAppearancePresetId;
        _palette = _themeService.GetPalette(_appSettings.ThemeMode);
        _currentAccountName = _appSettings.CurrentAccountName;

        BuildUi();
        ApplyTheme();
        LoadAccounts();
        ConfigureQuotaAutoRefresh();
        Shown += (_, _) =>
        {
            UpdateStatusBarLayout();
            QueueCardsAnimationVisibilityRefresh();
            RefreshOfficialQuotaIfNeeded();
            // Recalibrate active passive monitors after every normal application start.
            // Previously this only happened once the user navigated to the quota page,
            // which could leave a pre-fix model-priced estimate visible after restart.
            if (_accounts.Any(account =>
                    !account.IsCompatibleApi &&
                    _passiveQuotaMonitoring.GetState(account).IsEnabled))
            {
                _ = RefreshQuotaUsageAsync(force: false, _workspaceLoadGeneration);
            }
            if (_appSettings.PatGatewayProxyAutoDetect)
            {
                _ = DetectLocalPatGatewayProxyAsync(updateStatus: false);
            }
            _ = CheckForUpdatesAsync(manual: false);
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyInitialWindowBounds();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Windows can request a top-level background frame before every child HWND has
        // repainted after a taskbar restore.  Painting the resolved theme here prevents
        // that short interval from falling back to the default white Form brush.
        using var background = new SolidBrush(_palette.FormBackColor);
        e.Graphics.FillRectangle(background, e.ClipRectangle);
    }

    private void ApplyInitialWindowBounds()
    {
        const int logicalWorkingAreaMargin = 96;
        const int logicalInitialWidth = 920;
        const int logicalInitialHeight = 620;
        const int logicalMinimumWidth = 800;
        const int logicalMinimumHeight = 600;
        var dpiScale = Math.Max(1F, DeviceDpi / 96F);
        var hasSavedSize =
            _appSettings.WindowWidth is >= logicalMinimumWidth &&
            _appSettings.WindowHeight is >= logicalMinimumHeight;
        var hasSavedLocation = hasSavedSize &&
                               _appSettings.WindowLeft.HasValue &&
                               _appSettings.WindowTop.HasValue;
        var savedBounds = hasSavedSize
            ? new Rectangle(
                hasSavedLocation
                    ? (int)Math.Round(_appSettings.WindowLeft!.Value * dpiScale)
                    : 0,
                hasSavedLocation
                    ? (int)Math.Round(_appSettings.WindowTop!.Value * dpiScale)
                    : 0,
                (int)Math.Round(_appSettings.WindowWidth!.Value * dpiScale),
                (int)Math.Round(_appSettings.WindowHeight!.Value * dpiScale))
            : Rectangle.Empty;
        var workingArea = hasSavedLocation
            ? Screen.FromRectangle(savedBounds).WorkingArea
            : Screen.FromControl(this).WorkingArea;
        var workingAreaMargin = Math.Max(
            48,
            (int)Math.Round(logicalWorkingAreaMargin * dpiScale));
        var availableWidth = Math.Max(
            1,
            workingArea.Width - (hasSavedLocation ? 0 : workingAreaMargin));
        var availableHeight = Math.Max(
            1,
            workingArea.Height - (hasSavedLocation ? 0 : workingAreaMargin));
        var targetWidth = (int)Math.Round(logicalInitialWidth * dpiScale);
        var targetHeight = (int)Math.Round(logicalInitialHeight * dpiScale);
        var minimumWidth = Math.Min(
            (int)Math.Round(logicalMinimumWidth * dpiScale),
            availableWidth);
        var minimumHeight = Math.Min(
            (int)Math.Round(logicalMinimumHeight * dpiScale),
            availableHeight);
        var initialWidth = hasSavedSize
            ? Math.Clamp(savedBounds.Width, minimumWidth, availableWidth)
            : Math.Min(targetWidth, availableWidth);
        var initialHeight = hasSavedSize
            ? Math.Clamp(savedBounds.Height, minimumHeight, availableHeight)
            : Math.Min(targetHeight, availableHeight);
        var initialLeft = hasSavedLocation
            ? Math.Clamp(
                savedBounds.Left,
                workingArea.Left,
                Math.Max(workingArea.Left, workingArea.Right - initialWidth))
            : workingArea.Left + Math.Max(0, (workingArea.Width - initialWidth) / 2);
        var initialTop = hasSavedLocation
            ? Math.Clamp(
                savedBounds.Top,
                workingArea.Top,
                Math.Max(workingArea.Top, workingArea.Bottom - initialHeight))
            : workingArea.Top + Math.Max(0, (workingArea.Height - initialHeight) / 2);

        MinimumSize = new Size(minimumWidth, minimumHeight);
        Size = new Size(initialWidth, initialHeight);
        Location = new Point(initialLeft, initialTop);
    }

    private void SaveWindowBounds()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var dpiScale = Math.Max(1F, DeviceDpi / 96F);
        _appSettings.WindowLeft = (int)Math.Round(bounds.Left / dpiScale);
        _appSettings.WindowTop = (int)Math.Round(bounds.Top / dpiScale);
        _appSettings.WindowWidth = (int)Math.Round(bounds.Width / dpiScale);
        _appSettings.WindowHeight = (int)Math.Round(bounds.Height / dpiScale);
        _themeService.SaveSettings(_appSettings);
    }

    private void BuildUi()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = "";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        MaximizeBox = true;
        MinimumSize = new Size(800, 600);
        Size = new Size(920, 620);
        WindowState = FormWindowState.Normal;
        Font = new Font("Microsoft YaHei UI", 9.25F);
        BackColor = _palette.FormBackColor;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        UpdateStyles();
        HandleCreated += (_, _) => NativeWindowTheme.Apply(this, ThemeStyler.IsDark(_palette));

        var iconPath = Path.Combine(_store.RootPath, "assets", "CodexAccountManager.ico");
        if (File.Exists(iconPath))
        {
            Icon = new Icon(iconPath);
        }

        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = _palette.FormBackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 20, 16, 18),
            Name = "Sidebar",
            BackColor = _palette.SidebarColor
        };
        layout.Controls.Add(sidebar, 0, 0);

        var logo = new PictureBox
        {
            Width = 36,
            Height = 36,
            SizeMode = PictureBoxSizeMode.Zoom,
            Left = 18,
            Top = 12,
            AccessibleName = "Codex Account Manager"
        };
        if (File.Exists(iconPath))
        {
            using var icon = new Icon(iconPath);
            logo.Image = icon.ToBitmap();
        }
        sidebar.Controls.Add(logo);

        ConfigureSidebarCommandButton(_addAccountNavButton, "新增账号", 58);
        _addAccountNavButton.Click += (_, _) => AddAccount();
        ConfigureSidebarNavButton(_accountSwitchNavButton, "账号切换", 118, WorkspaceView.AccountSwitch);
        ConfigureSidebarNavButton(_unifiedHistoryNavButton, "聊天记录", 170, WorkspaceView.UnifiedHistory);
        ConfigureSidebarNavButton(_statusCheckNavButton, "状态与凭据", 222, WorkspaceView.StatusCheck);
        ConfigureSidebarNavButton(_quotaUsageNavButton, "额度显示", 274, WorkspaceView.QuotaUsage);
        ConfigureSidebarNavButton(_themeSettingsNavButton, "Codex 主题", 326, WorkspaceView.ThemeSettings);
        ConfigureSidebarNavButton(_systemConfigNavButton, "系统配置", 378, WorkspaceView.SystemConfig);
        sidebar.Controls.Add(_addAccountNavButton);
        sidebar.Controls.Add(_accountSwitchNavButton);
        sidebar.Controls.Add(_unifiedHistoryNavButton);
        sidebar.Controls.Add(_statusCheckNavButton);
        sidebar.Controls.Add(_quotaUsageNavButton);
        sidebar.Controls.Add(_themeSettingsNavButton);
        sidebar.Controls.Add(_systemConfigNavButton);

        _themeModePicker.SetBounds(0, 0, 360, 46);
        _themeModePicker.Font = new Font(Font.FontFamily, 9F);
        _themeModePicker.SetItems(ThemeOptions.Select(option => option.Label));
        _themeModePicker.SelectedIndex = Array.FindIndex(
            ThemeOptions,
            option => option.Mode == _appSettings.ThemeMode);
        if (_themeModePicker.SelectedIndex < 0)
        {
            _themeModePicker.SelectedIndex = 0;
        }
        _themeModePicker.SelectedIndexChanged += (_, _) => ChangeThemeMode();
        _themeModePicker.AccessibleName = "管理器外观选择";
        _themeModePicker.ApplyPalette(_palette);

        // Account Manager owns its own appearance independently from the Codex theme
        // library. Keep the picker in a persistent sidebar footer so changing pages or
        // rebuilding a long, scrollable theme list never moves or disposes it.
        var managerAppearanceFooter = new Panel
        {
            Left = 16,
            Top = 440,
            Width = 228,
            Height = 92,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Name = "SidebarFooter",
            BackColor = Color.Transparent,
            TabStop = false
        };
        var managerAppearanceLabel = new Label
        {
            Text = "管理器外观",
            Left = 2,
            Top = 0,
            Width = 224,
            Height = 30,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Name = "ThemeLabel",
            UseMnemonic = false
        };
        _themeModePicker.SetBounds(0, 34, 228, 46);
        _themeModePicker.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        managerAppearanceFooter.Resize += (_, _) =>
        {
            managerAppearanceLabel.Width = Math.Max(120, managerAppearanceFooter.ClientSize.Width - 4);
            _themeModePicker.Width = Math.Max(160, managerAppearanceFooter.ClientSize.Width);
            _themeModePicker.Top = Math.Max(32, managerAppearanceFooter.ClientSize.Height - _themeModePicker.Height - 8);
        };
        managerAppearanceFooter.Controls.Add(managerAppearanceLabel);
        managerAppearanceFooter.Controls.Add(_themeModePicker);
        sidebar.Controls.Add(managerAppearanceFooter);
        void UpdateManagerAppearanceFooterLayout()
        {
            // Reuse the already DPI-scaled navigation geometry. Scaling these constants a
            // second time made the footer narrower than the buttons at 200% DPI and turned
            // short names such as “深海夜色” into an unnecessary ellipsis.
            var sideInset = _systemConfigNavButton.Left;
            var bottomInset = Math.Max(8, sidebar.Padding.Bottom);
            var navGap = Math.Max(8, sidebar.Padding.Bottom * 2 / 3);
            var footerWidth = Math.Max(160, _systemConfigNavButton.Width);
            var footerTop = Math.Max(
                _systemConfigNavButton.Bottom + navGap,
                sidebar.ClientSize.Height - managerAppearanceFooter.Height - bottomInset);
            managerAppearanceFooter.SetBounds(
                sideInset,
                footerTop,
                footerWidth,
                managerAppearanceFooter.Height);
            managerAppearanceFooter.BringToFront();
        }
        sidebar.Resize += (_, _) => UpdateManagerAppearanceFooterLayout();
        UpdateManagerAppearanceFooterLayout();
        _toolTip.SetToolTip(managerAppearanceLabel, "只切换 Account Manager 的四套内置外观，也可跟随系统。");
        _toolTip.SetToolTip(_themeModePicker, "管理器外观与 Codex 主题相互独立，选择后立即保存并应用。");

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 18, 20, 12),
            Name = "Content",
            BackColor = _palette.FormBackColor
        };
        layout.Controls.Add(content, 1, 0);

        var contentLayout = _contentLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = _palette.FormBackColor
        };
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, WorkspaceHeroHeight));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.Controls.Add(contentLayout);

        var header = _headerPanel = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Height = WorkspaceHeroHeight,
            Radius = 18,
            Padding = Padding.Empty,
            UseGradient = true,
            ShowTechDecoration = false,
            ShowStarfield = true,
            Elevation = 2,
            Name = "HeaderPanel"
        };
        contentLayout.Controls.Add(header, 0, 0);

        _headerTitle.Text = "账号切换";
        _headerTitle.Left = 32;
        _headerTitle.Top = 4;
        _headerTitle.Width = 520;
        _headerTitle.Height = 62;
        _headerTitle.Font = new Font(Font.FontFamily, 14.6F, FontStyle.Bold);
        _headerTitle.TextAlign = ContentAlignment.MiddleLeft;
        _headerTitle.UseMnemonic = false;
        _headerTitle.UseCompatibleTextRendering = true;
        _headerTitle.Name = "HeaderTitle";
        header.Controls.Add(_headerTitle);

        _headerSubtitle.Text = "选择账号并用 Codex++、Codex 或 CLI 启动。";
        _headerSubtitle.Left = 32;
        _headerSubtitle.Top = 66;
        _headerSubtitle.Width = 1040;
        _headerSubtitle.Height = 30;
        _headerSubtitle.Font = new Font(Font.FontFamily, 8.8F);
        _headerSubtitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _headerSubtitle.AutoEllipsis = false;
        _headerSubtitle.UseMnemonic = false;
        _headerSubtitle.UseCompatibleTextRendering = true;
        _headerSubtitle.TextAlign = ContentAlignment.MiddleLeft;
        _headerSubtitle.Name = "HeaderSubtitle";
        header.Controls.Add(_headerSubtitle);

        header.Resize += (_, _) =>
        {
            // Transparent WinForms labels repaint their own rectangular parent background.
            // Letting them span the whole hero therefore hides the starfield drawn by the
            // parent panel even though the labels contain text only on the left. Reserve the
            // right side for the meteor scene while keeping every workspace title comfortably
            // inside a responsive text column.
            var headerTextWidth = Math.Clamp(
                (int)MathF.Round(header.ClientSize.Width * 0.52F),
                320,
                680);
            _headerTitle.Width = Math.Max(
                240,
                Math.Min(headerTextWidth, header.ClientSize.Width - _headerTitle.Left - 20));
            _headerSubtitle.Width = Math.Max(
                260,
                Math.Min(headerTextWidth, header.ClientSize.Width - _headerSubtitle.Left - 20));
            UpdateHeaderControlLayout();
        };

        var controlsRow = _controlsRow = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            WrapContents = false,
            AutoScroll = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(32, 4, 32, 8),
            Margin = Padding.Empty,
            BackColor = Color.Transparent,
            Name = "ControlsRow"
        };
        header.Controls.Add(controlsRow);

        _searchBox.PlaceholderText = "搜索账号";
        _searchBox.Left = 0;
        _searchBox.Top = 0;
        _searchBox.Width = 450;
        _searchBox.AutoSize = true;
        _searchBox.Font = new Font(Font.FontFamily, 9.5F);
        _searchBox.Margin = Padding.Empty;
        _searchBox.TextChanged += (_, _) =>
        {
            if (_suppressSearchRender)
            {
                return;
            }

            _showAccountDetail = false;
            if (_activeView == WorkspaceView.UnifiedHistory)
            {
                _unifiedHistoryVisibleLimit = UnifiedHistoryPageSize;
            }
            RenderCards();
        };
        _searchShell = new ModernInputShell(_searchBox, showSearchGlyph: true)
        {
            Width = 500,
            Height = 44,
            Margin = new Padding(0, 0, 12, 0),
            Radius = 10
        };
        controlsRow.Controls.Add(_searchShell);

        _checkUpdatesButton.Text = "检查更新";
        _checkUpdatesButton.Width = 112;
        _checkUpdatesButton.Height = 42;
        _checkUpdatesButton.Margin = new Padding(0, 1, 0, 0);
        _checkUpdatesButton.Tag = "app-update";
        _checkUpdatesButton.Radius = 11;
        _checkUpdatesButton.Padding = new Padding(10, 0, 10, 0);
        _checkUpdatesButton.Font = new Font(Font.FontFamily, 8.9F);
        _checkUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        controlsRow.Controls.Add(_checkUpdatesButton);

        controlsRow.SizeChanged += (_, _) => UpdateHeaderControlLayout();

        _projectPathBox.Text = ResolveInitialProjectPath();
        _projectPathBox.Validated += (_, _) => SaveEditedProjectPath();
        _projectPathBox.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter)
            {
                return;
            }

            eventArgs.SuppressKeyPress = true;
            SaveEditedProjectPath();
        };
        InitializePatGatewayProxyEditors();
        _patGatewayProxyAddressBox.Validated += (_, _) => SaveEditedPatGatewayProxy();
        _patGatewayProxyAddressBox.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter)
            {
                return;
            }

            eventArgs.SuppressKeyPress = true;
            SaveEditedPatGatewayProxy();
        };
        _patGatewayProxyPortBox.Validated += (_, _) => SaveEditedPatGatewayProxy();
        _patGatewayProxyPortBox.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter)
            {
                return;
            }

            eventArgs.SuppressKeyPress = true;
            SaveEditedPatGatewayProxy();
        };
        FormClosing += (_, _) =>
        {
            SaveEditedProjectPath(updateStatus: false);
            SaveEditedPatGatewayProxy(updateStatus: false, markManual: false);
            SaveWindowBounds();
        };
        FormClosed += (_, _) =>
        {
            _formClosed = true;
            _workspaceLoadGeneration++;
            _proxyDetectionCancellation?.Cancel();
            _proxyDetectionCancellation?.Dispose();
            _proxyDetectionCancellation = null;
            _unifiedHistoryContentIndexCancellation?.Cancel();
            _quotaRefreshTimer.Dispose();
            _layoutRefreshTimer.Dispose();
            _usageLogWatcher?.Dispose();
            _toolTip.Dispose();
            ClearWorkspaceViewCache();
        };
        SaveProjectPath(_projectPathBox.Text);
        _projectPathShell = new ModernInputShell(_projectPathBox)
        {
            Height = 42,
            Radius = 9
        };
        _patGatewayProxyAddressShell = new ModernInputShell(_patGatewayProxyAddressBox)
        {
            Height = 42,
            Radius = 9
        };
        _patGatewayProxyPortShell = new ModernInputShell(_patGatewayProxyPortBox)
        {
            Height = 42,
            Radius = 9
        };

        _statusBox.Dock = DockStyle.Fill;
        _statusBox.Height = 52;
        _statusBox.AutoEllipsis = false;
        _statusBox.UseCompatibleTextRendering = true;
        _statusBox.Padding = new Padding(24, 4, 24, 8);
        _statusBox.TextAlign = ContentAlignment.MiddleLeft;
        _statusBox.Font = new Font(Font.FontFamily, 8.8F);
        _statusBox.Text = "就绪";
        _statusBox.TextChanged += (_, _) => UpdateStatusBarLayout();
        contentLayout.Controls.Add(_statusBox, 0, 2);

        _accountLayout.Dock = DockStyle.Fill;
        _accountLayout.ColumnCount = 1;
        _accountLayout.RowCount = 1;
        _accountLayout.Padding = new Padding(2, 12, 0, 4);
        _accountLayout.Name = "AccountWorkspace";
        _accountLayout.BackColor = _palette.FormBackColor;
        _accountLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _accountLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        contentLayout.Controls.Add(_accountLayout, 0, 1);

        _cardsPanel.Dock = DockStyle.Fill;
        _cardsPanel.AutoScroll = true;
        _cardsPanel.WrapContents = false;
        _cardsPanel.FlowDirection = FlowDirection.TopDown;
        // Keep the native scrollbar at the far-right edge. Card widths reserve one
        // stable system scrollbar slot themselves, so extra panel padding would only
        // create a second, visually heavy strip on the right.
        _cardsPanel.Padding = Padding.Empty;
        _cardsPanel.BackColor = _palette.FormBackColor;
        _cardsPanel.ViewportChanged += (_, _) => RefreshCardsAnimationVisibility();
        _layoutRefreshTimer.Tick += (_, _) =>
        {
            _layoutRefreshTimer.Stop();
            RenderCards();
        };
        _cardsPanel.SizeChanged += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                _windowWasMinimized = true;
                _layoutRefreshTimer.Stop();
                return;
            }

            // A native AutoScroll bar changes ClientSize while a group is folded. It
            // must not trigger a full card rebuild (and a white flash). Card geometry
            // depends on the outer width only, not the viewport height or scrollbar
            // visibility.
            var outerWidth = _cardsPanel.Width;
            if (outerWidth <= 0 || outerWidth == _lastCardsPanelOuterWidth)
            {
                return;
            }

            _lastCardsPanelOuterWidth = outerWidth;
            _layoutRefreshTimer.Stop();
            _layoutRefreshTimer.Start();
        };
        _cardsPanel.Name = "WorkspaceListPanel";
        _cardsPanel.HandleCreated += (_, _) =>
            NativeWindowTheme.ApplyScrollable(_cardsPanel, ThemeStyler.IsDark(_palette));
        _accountLayout.Controls.Add(_cardsPanel, 0, 0);

        SizeChanged += (_, _) => RestoreWorkspaceAfterMinimize();

        DpiChanged += (_, _) => BeginInvoke(() =>
        {
            UpdateStatusBarLayout();
            RenderCards();
        });

        UpdateWorkspaceChrome();
    }

    private void RestoreWorkspaceAfterMinimize()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            _windowWasMinimized = true;
            _layoutRefreshTimer.Stop();
            return;
        }

        if (!_windowWasMinimized || _formClosed || _cardsPanel.IsDisposed)
        {
            return;
        }

        _windowWasMinimized = false;
        _layoutRefreshTimer.Stop();
        _contentLayout?.PerformLayout();
        _accountLayout.PerformLayout();
        _cardsPanel.PerformLayout();
        _layoutRefreshTimer.Stop();

        var restoredWidth = _cardsPanel.Width;
        if (restoredWidth <= 0)
        {
            return;
        }

        _lastCardsPanelOuterWidth = restoredWidth;
        var restoredWorkspaceWidth = GetWorkspaceWidth();

        // Minimizing does not invalidate the existing page model or its controls.  A full
        // RenderCards call here used to dispose every row immediately after Windows made
        // the Form visible again.  While that synchronous rebuild was running, DWM could
        // briefly expose the empty parent background.  Keep the completed control tree,
        // update only its responsive widths, and paint the already-themed frame now.
        Invalidate(invalidateChildren: true);
        Update();
        if (_renderedWorkspaceWidth > 0 &&
            restoredWorkspaceWidth != _renderedWorkspaceWidth)
        {
            // Child geometry is calculated when each dynamic card is built. Merely widening
            // the outer card leaves its buttons/badges at the pre-minimize coordinates, which
            // produces the clipped-left/empty-right layout. Paint the existing themed frame
            // first, then rebuild once at the restored width on the next UI turn.
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (_formClosed || IsDisposed)
                    {
                        return;
                    }
                    RenderCards();
                    ResetCardsScrollPosition();
                }));
            }
            catch (InvalidOperationException)
            {
            }
            return;
        }

        ResizeExistingWorkspaceCards();
    }

    private void ResizeExistingWorkspaceCards()
    {
        if (_cardsPanel.IsDisposed || _cardsPanel.Controls.Count == 0)
        {
            return;
        }

        var workspaceWidth = GetWorkspaceWidth();
        if (workspaceWidth <= 0)
        {
            return;
        }

        _cardsPanel.SuspendLayout();
        try
        {
            foreach (Control card in _cardsPanel.Controls)
            {
                if (card.Dock == DockStyle.None && card.Width != workspaceWidth)
                {
                    card.Width = workspaceWidth;
                }
            }
        }
        finally
        {
            _cardsPanel.ResumeLayout(performLayout: true);
        }
    }

    private void RefreshCardsAnimationVisibility()
    {
        if (_formClosed || _cardsPanel.IsDisposed)
        {
            return;
        }

        foreach (var control in EnumerateDescendantControls(_cardsPanel))
        {
            switch (control)
            {
                case ModelUsageDistributionControl modelDistribution:
                    modelDistribution.RefreshAnimationStateForViewport();
                    break;
                case PassiveQuotaGauge quotaGauge:
                    quotaGauge.RefreshAnimationStateForViewport();
                    break;
            }
        }
    }

    private void QueueCardsAnimationVisibilityRefresh()
    {
        if (_cardsAnimationRefreshQueued ||
            _formClosed ||
            IsDisposed ||
            Disposing ||
            !IsHandleCreated ||
            _cardsPanel.IsDisposed)
        {
            return;
        }

        // Dynamic detail cards are initially attached while layout/redraw is suspended.
        // Their first visibility check can therefore see the pre-layout position and stop
        // the animation timer. Recheck once the current UI message has completed so the
        // final AutoScroll viewport and every descendant screen rectangle are authoritative.
        _cardsAnimationRefreshQueued = true;
        try
        {
            BeginInvoke((MethodInvoker)(() =>
            {
                _cardsAnimationRefreshQueued = false;
                RefreshCardsAnimationVisibility();
            }));
        }
        catch (InvalidOperationException)
        {
            _cardsAnimationRefreshQueued = false;
        }
    }

    private static IEnumerable<Control> EnumerateDescendantControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateDescendantControls(child))
            {
                yield return descendant;
            }
        }
    }

    private void ApplyTheme()
    {
        _palette = _themeService.GetPalette(_appSettings.ThemeMode);
        BackColor = _palette.FormBackColor;
        NativeWindowTheme.Apply(this, ThemeStyler.IsDark(_palette));
        NativeWindowTheme.ApplyScrollable(_cardsPanel, ThemeStyler.IsDark(_palette));

        ApplyThemeRecursive(this);
        ThemeStyler.ApplyInput(_searchBox, _palette);
        if (_searchShell != null)
        {
            ThemeStyler.ApplyInputShell(_searchShell, _palette);
        }
        ThemeStyler.ApplyInput(_projectPathBox, _palette);
        if (_projectPathShell != null)
        {
            ThemeStyler.ApplyInputShell(_projectPathShell, _palette);
        }
        _themeModePicker.ApplyPalette(_palette);
        _statusBox.BackColor = _palette.StatusBackColor;
        _statusBox.ForeColor = _palette.MutedTextColor;
        UpdateWorkspaceChrome();
        ApplySidebarNavButtons();
        RenderCards();
        Invalidate(true);
    }

    private void ApplyThemeRecursive(Control control)
    {
        switch (control)
        {
            case Form form:
                form.BackColor = _palette.FormBackColor;
                form.ForeColor = _palette.TextColor;
                break;
            case RoundedPanel roundedPanel:
                if (roundedPanel.Name == "HeaderPanel")
                {
                    roundedPanel.BackColor = _palette.HeroStartColor;
                    roundedPanel.GradientColor = _palette.HeroEndColor;
                    roundedPanel.DecorationColor = Color.FromArgb(48, _palette.SecondaryAccentColor);
                    roundedPanel.BorderColor = Color.FromArgb(82, _palette.TertiaryAccentColor);
                    roundedPanel.ShadowColor = Color.FromArgb(70, _palette.ShadowColor);
                    break;
                }
                roundedPanel.BorderColor = _palette.BorderColor;
                roundedPanel.ShadowColor = Color.FromArgb(28, _palette.ShadowColor);
                break;
            case FlowLayoutPanel flowLayoutPanel:
                flowLayoutPanel.BackColor = flowLayoutPanel.Name == "ControlsRow"
                    ? Color.Transparent
                    : _palette.FormBackColor;
                break;
            case TableLayoutPanel tableLayoutPanel:
                tableLayoutPanel.BackColor = _palette.FormBackColor;
                break;
            case ModernInputShell inputShell:
                ThemeStyler.ApplyInputShell(inputShell, _palette);
                break;
            case Panel panel:
                panel.BackColor = panel.Name switch
                {
                    "Sidebar" => _palette.SidebarColor,
                    "SidebarFooter" => Color.Transparent,
                    "Content" => _palette.FormBackColor,
                    "AccountDetailPanel" => _palette.FormBackColor,
                    _ => panel.Parent == null ? _palette.FormBackColor : panel.BackColor
                };
                break;
            case PillLabel pill:
                pill.BackColor = Color.Transparent;
                break;
            case Label label:
                if (IsInsideNamedControl(label, "HeaderPanel"))
                {
                    label.BackColor = Color.Transparent;
                    label.ForeColor = label.Name == "HeaderSubtitle"
                        ? _palette.HeroMutedTextColor
                        : _palette.HeroTextColor;
                }
                else if (IsInsideNamedControl(label, "Sidebar"))
                {
                    label.BackColor = Color.Transparent;
                    label.ForeColor = label.Name is "SidebarSubtitle" or "ThemeLabel"
                        ? _palette.SidebarMutedTextColor
                        : label.Name == "SidebarEnvironment"
                            ? _palette.AccentColor
                            : _palette.SidebarTextColor;
                }
                else
                {
                    var muted = label.Name is "PathLabel";
                    ThemeStyler.ApplyLabel(label, _palette, muted);
                }
                break;
            case ThemePicker themePicker:
                themePicker.ApplyPalette(_palette);
                break;
            case ComboBox comboBox:
                ThemeStyler.ApplyComboBox(comboBox, _palette);
                break;
            case Button button:
                if (button.Tag is WorkspaceView view)
                {
                    ApplySidebarNavButton(button, view == _activeView);
                }
                else if (button is CircleIconButton iconButton)
                {
                    ApplyBackIconButtonTheme(iconButton);
                }
                else if (Equals(button.Tag, "history-tonal"))
                {
                    ApplyHistoryActionButtonStyle(button, danger: false);
                }
                else if (Equals(button.Tag, "history-danger"))
                {
                    ApplyHistoryActionButtonStyle(button, danger: true);
                }
                else if (Equals(button.Tag, "status-check"))
                {
                    ApplyStatusCheckButtonStyle(button);
                }
                else if (Equals(button.Tag, "group-toggle"))
                {
                    ApplyAccountGroupToggleButtonStyle(button);
                }
                else if (Equals(button.Tag, "launch-primary"))
                {
                    ApplyLaunchActionButtonStyle(button);
                }
                else if (Equals(button.Tag, "launch-tonal"))
                {
                    ApplyLaunchTonalButtonStyle(button);
                }
                else if (Equals(button.Tag, "token-update"))
                {
                    ApplyTokenUpdateButtonStyle(button);
                }
                else if (Equals(button.Tag, "primary"))
                {
                    ThemeStyler.ApplyPrimaryButton(button, _palette);
                }
                else
                {
                    ThemeStyler.ApplySoftButton(button, _palette);
                }
                break;

        }

        foreach (Control child in control.Controls)
        {
            ApplyThemeRecursive(child);
        }
    }

    private static bool IsInsideNamedControl(Control control, string name)
    {
        for (var current = control.Parent; current != null; current = current.Parent)
        {
            if (current.Name.Equals(name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void ConfigureSidebarNavButton(Button button, string text, int top, WorkspaceView view)
    {
        button.Text = text;
        button.Left = 16;
        button.Top = top;
        button.Width = 228;
        button.Height = 44;
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Padding = new Padding(12, 0, 10, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.Tag = view;
        button.Name = $"SidebarNav{view}";
        if (button is ModernButton modern)
        {
            modern.IconText = view switch
            {
                WorkspaceView.AccountSwitch => "⇄",
                WorkspaceView.UnifiedHistory => "◷",
                WorkspaceView.StatusCheck => "✓",
                WorkspaceView.QuotaUsage => "▥",
                WorkspaceView.ThemeSettings => "◐",
                WorkspaceView.SystemConfig => "⚙",
                _ => "•"
            };
            modern.IconWidth = 24;
            modern.Radius = 12;
            modern.ShowIconTile = true;
        }
        button.Click += (_, _) => ChangeWorkspaceView(view);
        ApplySidebarNavButton(button, view == _activeView);
    }

    private void ConfigureSidebarCommandButton(Button button, string text, int top)
    {
        button.Text = text;
        button.Left = 16;
        button.Top = top;
        button.Width = 228;
        button.Height = 44;
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Padding = new Padding(12, 0, 10, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.Tag = "primary";
        button.Name = $"SidebarCommand{text}";
        button.Cursor = Cursors.Hand;
        if (button is ModernButton modern)
        {
            modern.IconText = "+";
            modern.IconWidth = 24;
            modern.Radius = 12;
            modern.ShowIconTile = true;
        }
        ThemeStyler.ApplyPrimaryButton(button, _palette);
    }

    private void ChangeWorkspaceView(WorkspaceView view)
    {
        if (_activeView == view)
        {
            return;
        }

        // WM_SETREDRAW on a top-level Form temporarily clears WS_VISIBLE on Windows,
        // which makes its taskbar button disappear and reappear. Freeze only the
        // buffered child workspace while the page contents are replaced.
        CacheActiveWorkspaceView();
        using var redraw = NativeWindowTheme.SuspendRedraw(_accountLayout);
        SuspendLayout();
        try
        {
            _activeView = view;
            var loadGeneration = ++_workspaceLoadGeneration;
            _showAccountDetail = false;
            _showCodexAppearanceDetail = false;
            if (_activeView == WorkspaceView.UnifiedHistory)
            {
                _unifiedHistoryVisibleLimit = UnifiedHistoryPageSize;
            }

            _suppressSearchRender = true;
            try
            {
                _searchBox.Clear();
            }
            finally
            {
                _suppressSearchRender = false;
            }

            if (_activeView == WorkspaceView.QuotaUsage)
            {
                _usageTracker.EnsureCurrentAccountTracking(GetCurrentAccountRecord());
            }

            UpdateWorkspaceChrome();
            ApplySidebarNavButtons();
            if (!TryRestoreWorkspaceView(view))
            {
                RenderCards();
            }
            // All workspace views share the same native AutoScroll host.  Without resetting
            // it, entering the compact account-switch list after a long quota/detail page can
            // retain a stale vertical offset: the first rows are clipped and the exposed area
            // looks like an empty, frozen card until another repaint arrives.
            ResetCardsScrollPosition();
            _statusBox.Text = $"已切换到：{GetWorkspaceTitle(view)}";
            if (_activeView == WorkspaceView.QuotaUsage)
            {
                _ = RefreshQuotaUsageAsync(force: false, loadGeneration);
            }
            else if (_activeView == WorkspaceView.UnifiedHistory)
            {
                _ = RefreshUnifiedHistoryAsync(force: false, loadGeneration);
            }
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    private static bool IsWorkspaceViewCacheable(WorkspaceView view) =>
        view is WorkspaceView.AccountSwitch or
            WorkspaceView.StatusCheck or
            WorkspaceView.QuotaUsage;

    private void CacheActiveWorkspaceView()
    {
        if (_cardsPanel.IsDisposed ||
            _cardsPanel.Controls.Count == 0 ||
            _showAccountDetail ||
            !string.IsNullOrWhiteSpace(_searchBox.Text) ||
            !IsWorkspaceViewCacheable(_activeView))
        {
            return;
        }

        var currentWorkspaceWidth = GetWorkspaceWidth();
        var cacheWorkspaceWidth = _renderedWorkspaceWidth > 0
            ? _renderedWorkspaceWidth
            : currentWorkspaceWidth;
        if (cacheWorkspaceWidth != currentWorkspaceWidth)
        {
            // A resize rebuild is still queued. These controls contain child geometry for
            // the previous width, so caching them under the new width would resurrect the
            // clipped-left/empty-right layout later.
            return;
        }

        if (_workspaceViewCache.Remove(_activeView, out var previous))
        {
            foreach (var control in previous.Controls)
            {
                control.Dispose();
            }
        }

        var controls = _cardsPanel.Controls.Cast<Control>().ToArray();
        _cardsPanel.SuspendLayout();
        try
        {
            _cardsPanel.Controls.Clear();
        }
        finally
        {
            _cardsPanel.ResumeLayout(performLayout: false);
        }
        _workspaceViewCache[_activeView] = new WorkspaceViewCacheEntry(
            cacheWorkspaceWidth,
            controls);
    }

    private bool TryRestoreWorkspaceView(WorkspaceView view)
    {
        if (!_workspaceViewCache.Remove(view, out var entry))
        {
            return false;
        }

        var currentWorkspaceWidth = GetWorkspaceWidth();
        if (entry.WorkspaceWidth != currentWorkspaceWidth)
        {
            foreach (var control in entry.Controls)
            {
                control.Dispose();
            }
            return false;
        }

        _cardsPanel.SuspendLayout();
        try
        {
            // The system-config inputs are owned by the form and reused whenever that
            // page is rebuilt.  Detach them before disposing the transient page panel;
            // otherwise returning from SystemConfig destroys the project-path textbox
            // and the next account launch sees an empty directory.
            DetachPersistentSystemConfigControls();
            foreach (var current in _cardsPanel.Controls.Cast<Control>().ToArray())
            {
                current.Dispose();
            }
            _cardsPanel.Controls.Clear();
            _cardsPanel.Controls.AddRange(entry.Controls);
        }
        finally
        {
            _cardsPanel.ResumeLayout(performLayout: true);
        }

        _renderedWorkspaceWidth = entry.WorkspaceWidth;
        QueueCardsAnimationVisibilityRefresh();
        return true;
    }

    private void ClearWorkspaceViewCache()
    {
        foreach (var entry in _workspaceViewCache.Values)
        {
            foreach (var control in entry.Controls)
            {
                control.Dispose();
            }
        }
        _workspaceViewCache.Clear();
    }

    private void ResetCardsScrollPosition()
    {
        if (_cardsPanel.IsDisposed || !_cardsPanel.IsHandleCreated)
        {
            return;
        }

        void Reset()
        {
            if (_cardsPanel.IsDisposed || !_cardsPanel.IsHandleCreated)
            {
                return;
            }

            // The panel is rebuilt while layout is suspended.  Setting the offset once
            // during that window is racy: WinForms can restore the old offset during its
            // next layout pass, leaving the first account row clipped.  Re-apply it after
            // layout has settled and repaint the viewport in the same UI turn.
            _cardsPanel.PerformLayout();
            _cardsPanel.AutoScrollPosition = Point.Empty;
            if (_cardsPanel.Controls.Count > 0)
            {
                _cardsPanel.ScrollControlIntoView(_cardsPanel.Controls[0]);
                _cardsPanel.AutoScrollPosition = Point.Empty;
            }
            _cardsPanel.InvalidateVisibleViewport(flush: true);
        }

        Reset();
        try
        {
            BeginInvoke((Action)(() =>
            {
                Reset();
                try
                {
                    BeginInvoke((Action)Reset);
                }
                catch (InvalidOperationException)
                {
                    // The form may close between the two layout turns.
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // A closing form no longer has a UI queue; the synchronous reset above is enough.
        }
    }

    private void ConfigureQuotaAutoRefresh()
    {
        TryConfigureUsageLogWatcher();
        _quotaRefreshTimer.Interval = 250;
        _quotaRefreshTimer.Tick += (_, _) =>
        {
            RefreshQuotaUsageIfNeeded();
            RefreshOfficialQuotaIfNeeded();
        };
        _quotaRefreshTimer.Start();
    }

    private void RefreshOfficialQuotaIfNeeded()
    {
        if (_formClosed || IsDisposed)
        {
            return;
        }

        // Refresh each ChatGPT-backed account independently.  The app's selected account is
        // only a UI focus; it must never decide which PAT receives the quota request.
        foreach (var account in _accounts.Where(candidate => !candidate.IsCompatibleApi))
        {
            StartOfficialQuotaRefreshAfterLaunch(account);
        }
    }

    private void RefreshQuotaUsageIfNeeded()
    {
        if (_cardsPanel.IsDisposed || _formClosed || _activeView != WorkspaceView.QuotaUsage)
        {
            return;
        }
        if (_quotaUsageLoadTask != null)
        {
            // Keep the watcher dirty flag intact. Consuming it while an older snapshot is
            // still loading can postpone a just-appended token_count event until the next
            // rolling-window refresh.
            return;
        }

        var cacheAge = _quotaUsageLoadedAtUtc == default
            ? TimeSpan.MaxValue
            : DateTime.UtcNow - _quotaUsageLoadedAtUtc;
        var logDirty = Volatile.Read(ref _usageLogDirty) != 0;
        if (_usageLogWatcherReady)
        {
            if (!logDirty && cacheAge < QuotaRollingWindowRefreshInterval)
            {
                return;
            }

            // Keep the dirty signal until the debounce window has elapsed.  Clearing it early
            // could hide a one-off log append until the ten-second rolling refresh.
            if (logDirty && cacheAge < QuotaLogRefreshMinimumInterval)
            {
                return;
            }

            if (logDirty)
            {
                Interlocked.Exchange(ref _usageLogDirty, 0);
            }
        }
        else if (cacheAge < QuotaMinimumRefreshInterval)
        {
            return;
        }

        _ = RefreshQuotaUsageAsync(force: _usageLogWatcherReady && logDirty, _workspaceLoadGeneration);
    }

    private void TryConfigureUsageLogWatcher()
    {
        var sessionsRoot = Path.Combine(CodexCliService.GetDefaultCodexHome(), "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return;
        }

        try
        {
            _usageLogWatcher = new FileSystemWatcher(sessionsRoot, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.CreationTime |
                               NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler markDirty = (_, _) => Interlocked.Exchange(ref _usageLogDirty, 1);
            RenamedEventHandler markRenamed = (_, _) => Interlocked.Exchange(ref _usageLogDirty, 1);
            _usageLogWatcher.Changed += markDirty;
            _usageLogWatcher.Created += markDirty;
            _usageLogWatcher.Deleted += markDirty;
            _usageLogWatcher.Renamed += markRenamed;
            _usageLogWatcher.Error += (_, _) =>
            {
                _usageLogWatcherReady = false;
                Interlocked.Exchange(ref _usageLogDirty, 1);
            };
            _usageLogWatcherReady = true;
        }
        catch
        {
            _usageLogWatcher?.Dispose();
            _usageLogWatcher = null;
            _usageLogWatcherReady = false;
        }
    }
    private DateTime GetLatestUsageLogWriteTimeUtc()
    {
        var roots = new[]
        {
            Path.Combine(CodexCliService.GetDefaultCodexHome(), "sessions")
        };

        var latest = DateTime.MinValue;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    var writeTime = File.GetLastWriteTimeUtc(file);
                    if (writeTime > latest)
                    {
                        latest = writeTime;
                    }
                }
            }
            catch
            {
                // The sessions folder can be written by Codex while we scan it.
            }
        }

        if (File.Exists(_usageTracker.ProbeUsagePath))
        {
            var probeWriteTime = File.GetLastWriteTimeUtc(_usageTracker.ProbeUsagePath);
            if (probeWriteTime > latest)
            {
                latest = probeWriteTime;
            }
        }

        return latest;
    }

    private async Task RefreshQuotaUsageAsync(bool force, int loadGeneration)
    {
        if (_formClosed || IsDisposed)
        {
            return;
        }

        _quotaUsageRequestedGeneration = loadGeneration;
        if (_quotaUsageLoadTask != null)
        {
            SetQuotaRefreshIndicator(updating: true);
            return;
        }

        var invalidationVersion = _quotaUsageInvalidationVersion;
        var cacheVersion = _quotaUsageCacheVersion;
        var hasCachedReport = _quotaUsageCache != null;
        var cachedLogWriteTimeUtc = _lastQuotaUsageLogWriteTimeUtc;
        var cachedAtUtc = _quotaUsageLoadedAtUtc;
        var usageLogWatcherReady = _usageLogWatcherReady;
        var accountSnapshot = _accounts
            .Select(account => new AccountRecord
            {
                Name = account.Name,
                CodexHome = account.CodexHome,
                AuthKind = account.AuthKind
            })
            .ToList();

        var loadTask = Task.Run(() =>
        {
            // A healthy FileSystemWatcher already tells us when a JSONL changes. Avoid a
            // second all-history directory walk before every incremental report; if the
            // watcher errors, retain the full-scan fallback for correctness.
            var latestWriteTimeUtc = usageLogWatcherReady
                ? cachedLogWriteTimeUtc
                : GetLatestUsageLogWriteTimeUtc();
            var cacheAge = cachedAtUtc == default
                ? TimeSpan.MaxValue
                : DateTime.UtcNow - cachedAtUtc;
            var cacheInvalid = !hasCachedReport || cacheVersion != invalidationVersion;
            var logChanged = force || latestWriteTimeUtc > cachedLogWriteTimeUtc;
            var shouldRefreshForLog = logChanged && cacheAge >= QuotaMinimumRefreshInterval;
            var shouldRefreshRollingWindows = cacheAge >= QuotaRollingWindowRefreshInterval;
            if (!force && !cacheInvalid && !shouldRefreshForLog && !shouldRefreshRollingWindows)
            {
                return new QuotaUsageLoadResult(
                    null,
                    latestWriteTimeUtc,
                    DateTime.UtcNow,
                    invalidationVersion);
            }

            var report = _usageTracker.BuildReport(accountSnapshot);
            return new QuotaUsageLoadResult(
                report,
                usageLogWatcherReady ? DateTime.UtcNow : GetLatestUsageLogWriteTimeUtc(),
                DateTime.UtcNow,
                invalidationVersion);
        });
        _quotaUsageLoadTask = loadTask;
        SetQuotaRefreshIndicator(updating: true);

        try
        {
            var result = await loadTask;
            var resultIsCurrent = result.InvalidationVersion == _quotaUsageInvalidationVersion;
            if (result.Report != null && resultIsCurrent)
            {
                _quotaUsageCache = result.Report;
                _lastQuotaUsageLogWriteTimeUtc = result.LatestWriteTimeUtc;
                _quotaUsageLoadedAtUtc = result.LoadedAtUtc;
                _quotaUsageCacheVersion = result.InvalidationVersion;
                _quotaUsageLoadError = null;
            }

            if (!_formClosed &&
                !IsDisposed &&
                _quotaUsageRequestedGeneration == _workspaceLoadGeneration &&
                result.Report != null &&
                resultIsCurrent)
            {
                ApplyLiveRateLimitSnapshots(result.Report);
                UpdateQuotaLimitProfilesFromReport(result.Report);

                // The official percentage is rendered from the freshly merged report on
                // every workspace.  The passive model-equivalent estimate must replay the
                // very same report before either the list or a detail card is updated as
                // well.  Previously the quota workspace skipped this call: a row could
                // therefore show a new official percentage while its persisted capacity
                // and assessment windows remained stuck at an older boundary until the
                // user happened to open that account's detail page.
                RefreshActivePassiveQuotaMonitoring(result.Report);

                // The first result still creates the page. Subsequent background refreshes
                // update the existing labels and progress bars in place so the list never
                // disappears into a white frame between timer ticks.
                if (_activeView == WorkspaceView.QuotaUsage &&
                    (!hasCachedReport ||
                     (_showAccountDetail
                         ? !TryUpdateQuotaDetailInPlace(result.Report)
                         : !TryUpdateQuotaUsageInPlace(result.Report))))
                {
                    RenderCards();
                }
            }
        }
        catch (Exception ex)
        {
            _quotaUsageLoadError = ex;
            if (!_formClosed &&
                !IsDisposed &&
                _quotaUsageRequestedGeneration == _workspaceLoadGeneration &&
                _activeView == WorkspaceView.QuotaUsage)
            {
                RenderCards();
                _statusBox.Text = $"读取本地用量失败：{ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_quotaUsageLoadTask, loadTask))
            {
                _quotaUsageLoadTask = null;
                SetQuotaRefreshIndicator(updating: false);
            }
        }

        if (!_formClosed &&
            !IsDisposed &&
            _quotaUsageRequestedGeneration == _workspaceLoadGeneration &&
            _activeView == WorkspaceView.QuotaUsage &&
            _quotaUsageLoadError == null &&
            _quotaUsageCacheVersion != _quotaUsageInvalidationVersion)
        {
            _ = RefreshQuotaUsageAsync(force: true, _quotaUsageRequestedGeneration);
        }
    }

    private void SetQuotaRefreshIndicator(bool updating)
    {
        const string suffix = " · 更新中";
        if (_formClosed || IsDisposed || _activeView != WorkspaceView.QuotaUsage)
        {
            return;
        }

        if (updating)
        {
            if (!_headerSubtitle.Text.EndsWith(suffix, StringComparison.Ordinal))
            {
                _headerSubtitle.Text += suffix;
            }
        }
        else if (_headerSubtitle.Text.EndsWith(suffix, StringComparison.Ordinal))
        {
            _headerSubtitle.Text = _headerSubtitle.Text[..^suffix.Length];
        }
    }

    private bool TryUpdateQuotaUsageInPlace(UsageReport report)
    {
        var query = _searchBox.Text.Trim();
        var visibleAccounts = _accounts.Where(account =>
            string.IsNullOrWhiteSpace(query) ||
            account.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            account.CodexHome.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var expectedGroups = BuildAccountGroups(visibleAccounts, report);
        var renderedGroups = _cardsPanel.Controls
            .Cast<Control>()
            .Select(control => control.Tag as AccountGroupSection)
            .Where(group => group != null)
            .Cast<AccountGroupSection>()
            .ToList();
        if (expectedGroups.Count != renderedGroups.Count ||
            expectedGroups.Where((group, index) =>
                !group.Key.Equals(renderedGroups[index].Key, StringComparison.OrdinalIgnoreCase) ||
                group.Accounts.Count != renderedGroups[index].Accounts.Count ||
                !group.Accounts.Select(account => account.Name).SequenceEqual(
                    renderedGroups[index].Accounts.Select(account => account.Name),
                    StringComparer.OrdinalIgnoreCase)).Any())
        {
            return false;
        }

        var bindings = _cardsPanel.Controls
            .Cast<Control>()
            .Select(control => control.Tag as QuotaUsageRowBinding)
            .Where(binding => binding != null)
            .Cast<QuotaUsageRowBinding>()
            .ToList();
        if (bindings.Count == 0)
        {
            return renderedGroups.Count > 0;
        }

        var updates = new List<(QuotaUsageRowBinding Binding, AccountRecord Account, AccountUsageSummary Usage)>();
        foreach (var binding in bindings)
        {
            var account = _accounts.FirstOrDefault(candidate =>
                candidate.Name.Equals(binding.AccountName, StringComparison.OrdinalIgnoreCase));
            var usage = report.Accounts.FirstOrDefault(candidate =>
                candidate.AccountName.Equals(binding.AccountName, StringComparison.OrdinalIgnoreCase));
            if (account == null || usage == null)
            {
                return false;
            }

            var quotaLimitType = ResolveQuotaLimitType(account, usage);
            if (!account.IsCompatibleApi && quotaLimitType != binding.QuotaLimitType)
            {
                // A newly detected quota type changes the row structure (one monthly bar vs.
                // the 5h + weekly pair), so allow one structural render for that transition.
                return false;
            }

            updates.Add((binding, account, usage));
        }

        _cardsPanel.SuspendLayout();
        try
        {
            foreach (var (binding, account, usage) in updates)
            {
                UpdateQuotaUsageRow(binding, account, usage);
            }
        }
        finally
        {
            _cardsPanel.ResumeLayout(performLayout: false);
        }

        return true;
    }

    private bool TryUpdateQuotaDetailInPlace(UsageReport report)
    {
        if (string.IsNullOrWhiteSpace(_selectedAccountName) || _cardsPanel.Controls.Count != 1)
        {
            return false;
        }

        var account = _accounts.FirstOrDefault(candidate =>
            candidate.Name.Equals(_selectedAccountName, StringComparison.OrdinalIgnoreCase));
        var usage = report.Accounts.FirstOrDefault(candidate =>
            candidate.AccountName.Equals(_selectedAccountName, StringComparison.OrdinalIgnoreCase));
        if (account == null || usage == null ||
            _cardsPanel.Controls[0].Tag is not QuotaUsageDetailBinding binding ||
            !binding.AccountName.Equals(account.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var quotaLimitType = ResolveQuotaLimitType(account, usage);
        if (!account.IsCompatibleApi &&
            !binding.QuotaLimitType.Equals(quotaLimitType, StringComparison.OrdinalIgnoreCase))
        {
            // A genuine quota-type transition changes the detail structure. This is the
            // only background transition that is allowed to request one full render.
            return false;
        }

        var card = _cardsPanel.Controls[0];
        var priceProfile = GetUsagePriceProfile(account);
        var metrics = GetQuotaUsageMetrics(account, quotaLimitType, usage);
        if (binding.ShowsCacheWriteColumn != ShouldShowCacheWriteColumn(usage))
        {
            // The wide table changes between a balanced two-column input layout and a
            // three-column layout only when cache-write tokens actually appear or disappear.
            return false;
        }
        var monitoring = GetPassiveQuotaMonitoringResult(account, usage);
        var trendData = GetQuotaTrendData(account, usage, priceProfile, monitoring);
        var modelDistributionItems = BuildModelUsageDistribution(trendData.Points);
        var preferredModelHeight = ModelUsageDistributionControl.GetPreferredHeight(
            binding.ModelDistribution.Width,
            modelDistributionItems.Count,
            Math.Max(1F, DeviceDpi / 96F));
        if (preferredModelHeight != binding.ModelDistribution.Height)
        {
            // A model row was added or removed. Rebuild once so the model table and the
            // usage table below it receive their new non-overlapping geometry.
            return false;
        }
        card.SuspendLayout();
        try
        {
            for (var index = 0; index < Math.Min(binding.Metrics.Length, metrics.Length); index++)
            {
                SetLabelText(
                    binding.Metrics[index].Cost,
                    FormatEstimatedCost(metrics[index].Bucket, priceProfile));
                SetLabelText(
                    binding.Metrics[index].Tokens,
                    $"{FormatTokens(metrics[index].Bucket.TotalTokens)} token");
            }

            var usageSummary = account.IsCompatibleApi
                ? "按 API 账单计费"
                : "仅显示官方额度百分比";
            var officialSummary = account.IsCompatibleApi ? null : GetOfficialFinancialSummary(usage);
            var subtitleText = string.IsNullOrWhiteSpace(officialSummary)
                ? $"{account.AuthKindLabel} · {usageSummary}"
                : $"{account.AuthKindLabel} · {usageSummary} · {officialSummary}";
            SetLabelText(binding.Subtitle, subtitleText);

            var observed = usage.RateLimitObservedAtUtc.HasValue
                ? $"更新 {usage.RateLimitObservedAtUtc.Value.ToLocalTime():MM-dd HH:mm}"
                : "更新 暂无";
            var resetSummary = account.IsCompatibleApi
                ? string.Empty
                : GetQuotaResetSummary(quotaLimitType, usage);
            var metaText = account.IsCompatibleApi
                ? "余额请到服务商账单查看"
                : $"{resetSummary} · {observed}";
            SetLabelText(binding.Meta, metaText);

            UpdatePassiveQuotaMonitor(binding.Monitor, account, usage, monitoring);

            var chartSamples = BuildQuotaChartSamples(
                trendData,
                GetQuotaTrendBucketSize(_quotaTrendRange),
                ShouldTrimLeadingQuotaTrendBuckets(_quotaTrendRange),
                GetQuotaTrendLeadingContextDuration(_quotaTrendRange));
            if (!QuotaChartSamplesEqual(binding.Chart.Samples, chartSamples))
            {
                binding.Chart.Samples = chartSamples;
            }
            binding.Chart.EmptyText = trendData.EmptyText;
            binding.Chart.AssessmentWindows = trendData.AssessmentWindows ?? [];
            binding.ExportButton.Tag = trendData.Points;
            binding.ModelDistribution.RangeLabel = GetModelDistributionRangeLabel(_quotaTrendRange);
            binding.ModelDistribution.Items = modelDistributionItems;
            UpdateQuotaUsageTable(binding.UsageTable, usage, priceProfile);
        }
        finally
        {
            card.ResumeLayout(performLayout: false);
        }

        return true;
    }

    private static void SetLabelText(Label label, string text)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal))
        {
            label.Text = text;
        }
    }

    private static bool QuotaChartSamplesEqual(
        IReadOnlyList<QuotaChartSample> left,
        IReadOnlyList<QuotaChartSample> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Timestamp != second.Timestamp ||
                first.IncrementalCostUsd != second.IncrementalCostUsd ||
                first.RemainingPercent != second.RemainingPercent ||
                first.TotalTokens != second.TotalTokens ||
                first.BucketDuration != second.BucketDuration ||
                !QuotaChartModelUsageEqual(first.ModelUsage, second.ModelUsage))
            {
                return false;
            }
        }
        return true;
    }

    private static bool QuotaChartModelUsageEqual(
        IReadOnlyList<QuotaChartModelUsage>? left,
        IReadOnlyList<QuotaChartModelUsage>? right)
    {
        var first = left ?? [];
        var second = right ?? [];
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (first[index] != second[index])
            {
                return false;
            }
        }
        return true;
    }

    private void UpdateQuotaUsageRow(
        QuotaUsageRowBinding binding,
        AccountRecord account,
        AccountUsageSummary usage)
    {
        var priceProfile = GetUsagePriceProfile(account);
        var quotaLimitType = ResolveQuotaLimitType(account, usage);
        var metrics = GetQuotaListUsageMetrics(usage);
        for (var index = 0; index < Math.Min(binding.Metrics.Length, metrics.Length); index++)
        {
            binding.Metrics[index].Cost.Text = FormatEstimatedCost(metrics[index].Bucket, priceProfile);
            binding.Metrics[index].Tokens.Text = $"{FormatTokens(metrics[index].Bucket.TotalTokens)} token";
        }

        var kindText = account.IsCompatibleApi
            ? "兼容 API"
            : GetQuotaLimitTypeLabel(quotaLimitType);
        var officialFinancial = account.IsCompatibleApi ? null : GetOfficialFinancialSummary(usage);
        binding.Kind.Text = string.IsNullOrWhiteSpace(officialFinancial)
            ? kindText
            : $"{kindText} · {officialFinancial}";
        _toolTip.SetToolTip(binding.Kind, binding.Kind.Text);
        if (!account.IsCompatibleApi)
        {
            UpdatePassiveQuotaStatus(
                binding.CapacityStatus,
                binding.CapacitySummary,
                GetPassiveQuotaMonitoringResult(account, usage),
                GetPrimaryDisplayedQuotaWindow(quotaLimitType, usage)?.RemainingPercent);
        }

        if (account.IsCompatibleApi)
        {
            UpdateQuotaPill(
                binding.PrimaryQuota,
                $"本月 {FormatEstimatedCost(usage.Month, priceProfile)}",
                _palette.PrimaryColor);
        }
        else if (quotaLimitType == AccountQuotaLimitType.FiveHourAndWeekly)
        {
            var fiveHourWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour);
            var weeklyWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly);
            var fiveHourColor = GetQuotaColor(fiveHourWindow?.RemainingPercent);
            var weeklyColor = GetQuotaColor(weeklyWindow?.RemainingPercent);
            UpdateQuotaPill(
                binding.PrimaryQuota,
                FormatQuotaRemaining(fiveHourWindow, "5h"),
                fiveHourColor);
            UpdateQuotaPill(
                binding.SecondaryQuota,
                FormatQuotaRemaining(weeklyWindow, "周"),
                weeklyColor);
            if (binding.PrimaryQuota != null)
            {
                _toolTip.SetToolTip(binding.PrimaryQuota, GetOfficialQuotaToolTip(fiveHourWindow, "5h"));
            }
            if (binding.SecondaryQuota != null)
            {
                _toolTip.SetToolTip(binding.SecondaryQuota, GetOfficialQuotaToolTip(weeklyWindow, "周"));
            }
            UpdateQuotaProgress(binding.PrimaryProgress, fiveHourWindow?.RemainingPercent, fiveHourColor);
            UpdateQuotaProgress(binding.SecondaryProgress, weeklyWindow?.RemainingPercent, weeklyColor);
        }
        else if (quotaLimitType == AccountQuotaLimitType.WeeklyOnly)
        {
            var weeklyWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly);
            var weeklyColor = GetQuotaColor(weeklyWindow?.RemainingPercent);
            UpdateQuotaPill(
                binding.PrimaryQuota,
                FormatQuotaRemaining(weeklyWindow, "周"),
                weeklyColor);
            UpdateQuotaProgress(binding.PrimaryProgress, weeklyWindow?.RemainingPercent, weeklyColor);
            UpdateQuotaPill(binding.SecondaryQuota, "无 5h 限额", _palette.MutedTextColor);
            if (binding.PrimaryQuota != null)
            {
                _toolTip.SetToolTip(binding.PrimaryQuota, GetOfficialQuotaToolTip(weeklyWindow, "周"));
            }
            if (binding.SecondaryQuota != null)
            {
                _toolTip.SetToolTip(binding.SecondaryQuota, "官方当前未返回 5h 额度窗口。");
            }
        }
        else if (quotaLimitType is AccountQuotaLimitType.Monthly or
                 AccountQuotaLimitType.FiveHourOnly)
        {
            var windowKind = quotaLimitType switch
            {
                AccountQuotaLimitType.Monthly => AccountQuotaWindowKind.Monthly,
                _ => AccountQuotaWindowKind.FiveHour
            };
            var windowLabel = windowKind switch
            {
                AccountQuotaWindowKind.Monthly => "月",
                _ => "5h"
            };
            var window = usage.GetQuotaWindow(windowKind);
            var remaining = window?.RemainingPercent;
            var quotaColor = GetQuotaColor(remaining);
            var quotaText = FormatQuotaRemaining(window, windowLabel);
            UpdateQuotaPill(binding.PrimaryQuota, quotaText, quotaColor);
            UpdateQuotaProgress(binding.PrimaryProgress, remaining, quotaColor);
            if (binding.PrimaryQuota != null)
            {
                _toolTip.SetToolTip(binding.PrimaryQuota, GetOfficialQuotaToolTip(window, windowLabel));
            }
        }
        else
        {
            UpdateQuotaPill(binding.PrimaryQuota, "待识别", _palette.MutedTextColor);
            UpdateQuotaProgress(binding.PrimaryProgress, null, _palette.MutedTextColor);
        }

        var (primaryDetail, secondaryDetail) = GetQuotaRowDetailLines(
            account.IsCompatibleApi,
            quotaLimitType,
            usage);
        binding.Detail.Text = primaryDetail;
        if (binding.SecondaryDetail != null)
        {
            binding.SecondaryDetail.Text = secondaryDetail ?? string.Empty;
            binding.SecondaryDetail.Visible = !string.IsNullOrWhiteSpace(secondaryDetail);
        }
        _toolTip.SetToolTip(
            binding.Detail,
            account.IsCompatibleApi
                ? "基于本地 Token 日志和当前模型单价估算。"
                : "仅显示官方额度百分比与重置时间；不会调用模型。"
        );
    }

    private static void UpdateQuotaPill(PillLabel? pill, string text, Color color)
    {
        if (pill == null)
        {
            return;
        }

        pill.Text = text;
        pill.FillColor = Color.FromArgb(44, color);
        pill.StrokeColor = Color.FromArgb(92, color);
        pill.ForeColor = color;
        pill.Invalidate();
    }

    private static void UpdateQuotaProgress(QuotaProgressBar? progress, double? value, Color color)
    {
        if (progress == null)
        {
            return;
        }

        progress.FillColor = color;
        progress.Value = value ?? 0D;
    }

    private async Task RefreshUnifiedHistoryAsync(bool force, int loadGeneration)
    {
        if (_formClosed || IsDisposed)
        {
            return;
        }

        var loadTask = _unifiedHistoryLoadTask;
        if (loadTask == null)
        {
            var invalidationVersion = _unifiedHistoryInvalidationVersion;
            var sharedHome = CodexCliService.GetDefaultCodexHome();
            loadTask = Task.Run(() => new UnifiedHistoryLoadResult(
                _sharedHistory.Load(sharedHome),
                invalidationVersion));
            _unifiedHistoryLoadTask = loadTask;
        }

        try
        {
            var result = await loadTask;
            var resultIsCurrent = result.InvalidationVersion == _unifiedHistoryInvalidationVersion;
            if (resultIsCurrent)
            {
                _unifiedHistoryCache = result.Threads;
                _unifiedHistoryCacheVersion = result.InvalidationVersion;
                _unifiedHistoryLoadError = null;
            }

            if (!_formClosed &&
                !IsDisposed &&
                loadGeneration == _workspaceLoadGeneration &&
                _activeView == WorkspaceView.UnifiedHistory &&
                resultIsCurrent)
            {
                RenderCards();
            }
        }
        catch (Exception ex)
        {
            _unifiedHistoryLoadError = ex;
            if (!_formClosed &&
                !IsDisposed &&
                loadGeneration == _workspaceLoadGeneration &&
                _activeView == WorkspaceView.UnifiedHistory)
            {
                RenderCards();
                _statusBox.Text = $"读取共享聊天记录失败：{ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_unifiedHistoryLoadTask, loadTask))
            {
                _unifiedHistoryLoadTask = null;
            }
        }

        if (!_formClosed &&
            !IsDisposed &&
            loadGeneration == _workspaceLoadGeneration &&
            _activeView == WorkspaceView.UnifiedHistory &&
            _unifiedHistoryLoadError == null &&
            _unifiedHistoryCacheVersion != _unifiedHistoryInvalidationVersion)
        {
            await RefreshUnifiedHistoryAsync(force: true, loadGeneration);
        }
    }

    private void EnsureUnifiedHistoryContentIndex(
        IReadOnlyList<UnifiedThreadRecord> threads,
        int invalidationVersion)
    {
        if (invalidationVersion < 0 ||
            _unifiedHistoryContentIndexVersion == invalidationVersion ||
            (_unifiedHistoryContentIndexTask != null &&
             _unifiedHistoryContentIndexRequestedVersion == invalidationVersion))
        {
            return;
        }

        _unifiedHistoryContentIndexCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var sharedHome = CodexCliService.GetDefaultCodexHome();
        var snapshot = threads.ToList();
        var task = Task.Run(() =>
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var thread in snapshot)
            {
                token.ThrowIfCancellationRequested();
                var transcript = _threadTranscript.Load(
                    sharedHome,
                    thread,
                    maxMessages: 160,
                    maxMessageCharacters: 6000);
                index[thread.Id] = BuildUnifiedHistorySearchText(transcript);
            }

            return new UnifiedHistoryContentIndexResult(index, invalidationVersion);
        }, token);

        _unifiedHistoryContentIndexCancellation = cancellation;
        _unifiedHistoryContentIndexTask = task;
        _unifiedHistoryContentIndexRequestedVersion = invalidationVersion;
        _unifiedHistoryContentIndexError = null;
        _ = CompleteUnifiedHistoryContentIndexAsync(task, cancellation);
    }

    private async Task CompleteUnifiedHistoryContentIndexAsync(
        Task<UnifiedHistoryContentIndexResult> task,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await task;
            if (cancellation.IsCancellationRequested ||
                result.InvalidationVersion != _unifiedHistoryInvalidationVersion ||
                !ReferenceEquals(_unifiedHistoryContentIndexTask, task))
            {
                return;
            }

            _unifiedHistoryContentIndex = result.SearchTextByThreadId;
            _unifiedHistoryContentIndexVersion = result.InvalidationVersion;
            _unifiedHistoryContentIndexError = null;
            if (!_formClosed && !IsDisposed && _activeView == WorkspaceView.UnifiedHistory)
            {
                BeginInvoke(new Action(RenderCards));
            }
        }
        catch (OperationCanceledException)
        {
            // A refresh or view shutdown superseded this content index.
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested &&
                ReferenceEquals(_unifiedHistoryContentIndexTask, task))
            {
                _unifiedHistoryContentIndexError = ex;
                if (!_formClosed && !IsDisposed && _activeView == WorkspaceView.UnifiedHistory)
                {
                    BeginInvoke(new Action(RenderCards));
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_unifiedHistoryContentIndexTask, task))
            {
                _unifiedHistoryContentIndexTask = null;
            }
            if (ReferenceEquals(_unifiedHistoryContentIndexCancellation, cancellation))
            {
                _unifiedHistoryContentIndexCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private static string BuildUnifiedHistorySearchText(UnifiedThreadTranscript transcript)
    {
        var builder = new StringBuilder(Math.Min(
            UnifiedHistorySearchMaxCharactersPerThread,
            Math.Max(256, transcript.Messages.Count * 256)));
        // Recent messages are the most useful and are indexed first. The cap prevents one
        // unusually large task from making the chat list heavy or unresponsive.
        foreach (var message in transcript.Messages.Reverse())
        {
            if (builder.Length >= UnifiedHistorySearchMaxCharactersPerThread)
            {
                break;
            }

            var remaining = UnifiedHistorySearchMaxCharactersPerThread - builder.Length;
            var text = message.Text.Trim();
            if (text.Length > remaining)
            {
                text = text[..remaining];
            }
            builder.AppendLine(text);
        }
        return builder.ToString();
    }

    private static string CreateUnifiedHistorySearchSnippet(string searchText, string query)
    {
        var index = searchText.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        const int contextCharacters = 72;
        var start = Math.Max(0, index - contextCharacters);
        var end = Math.Min(searchText.Length, index + query.Length + contextCharacters);
        var snippet = searchText[start..end]
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        while (snippet.Contains("  ", StringComparison.Ordinal))
        {
            snippet = snippet.Replace("  ", " ", StringComparison.Ordinal);
        }
        return (start > 0 ? "…" : "") + snippet + (end < searchText.Length ? "…" : "");
    }

    internal static void ValidateUnifiedHistorySearch()
    {
        var transcript = new UnifiedThreadTranscript(
            UnifiedThreadTranscriptStatus.Available,
            [
                new UnifiedThreadMessage(UnifiedThreadMessageRole.User, "first searchable question", null),
                new UnifiedThreadMessage(UnifiedThreadMessageRole.Assistant, "second searchable answer", null)
            ],
            IsTruncated: false,
            IgnoredMalformedLines: 0,
            IgnoredOversizedLines: 0,
            Notice: "");
        var index = BuildUnifiedHistorySearchText(transcript);
        var snippet = CreateUnifiedHistorySearchSnippet(index, "searchable answer");
        if (!index.Contains("searchable question", StringComparison.Ordinal) ||
            !index.Contains("searchable answer", StringComparison.Ordinal) ||
            !snippet.Contains("searchable answer", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unified history full-text search validation failed.");
        }
    }

    internal static void ValidateTokenRowGeometry()
    {
        foreach (var width in new[] { AccountRowMinWidth, 720, 960, 980, 1_280, 1_600 })
        {
            var geometry = CalculateTokenRowGeometry(width);
            var combinedHeight = 104 + 8 + geometry.Height;
            var expectedCombinedHeight = width < 980 ? 290 : 224;
            var rowBounds = new Rectangle(0, 0, width, geometry.Height);
            var controls = new[]
            {
                geometry.Name,
                geometry.AuthKind,
                geometry.State,
                geometry.Detail,
                geometry.Badge,
                geometry.Update
            };
            if (controls.Any(bounds =>
                    bounds.Width <= 0 ||
                    bounds.Height <= 0 ||
                    !rowBounds.Contains(bounds)) ||
                geometry.Name.IntersectsWith(geometry.Badge) ||
                geometry.AuthKind.IntersectsWith(geometry.Badge) ||
                geometry.State.IntersectsWith(geometry.Update) ||
                geometry.Detail.IntersectsWith(geometry.Update) ||
                combinedHeight != expectedCombinedHeight)
            {
                throw new InvalidOperationException(
                    $"Token row geometry overlaps or clips at width {width}.");
            }
        }

        // Runtime-created WinForms controls keep physical bounds while GDI text grows with
        // monitor DPI. Exercise representative 100%-250% text widths, including the 130 px
        // measurement observed for "更新 Token" at 200% scaling.
        foreach (var measuredTextWidth in new[] { 66, 98, 130, 164, 182 })
        {
            var actionWidth = CalculateStatusTokenActionWidth(measuredTextWidth);
            foreach (var width in new[] { AccountRowMinWidth, 720, 899, 900, 928, 1_280, 1_600 })
            {
                var geometry = CalculateStatusTokenRowGeometry(width, actionWidth);
                var rowBounds = new Rectangle(0, 0, width, geometry.Height);
                var controls = new[]
                {
                    geometry.Name,
                    geometry.AuthKind,
                    geometry.State,
                    geometry.Detail,
                    geometry.StatusBadge,
                    geometry.TokenBadge,
                    geometry.Check,
                    geometry.Update
                };
                if (controls.Any(bounds =>
                        bounds.Width <= 0 ||
                        bounds.Height <= 0 ||
                        !rowBounds.Contains(bounds)) ||
                    geometry.Name.IntersectsWith(geometry.State) ||
                    geometry.AuthKind.IntersectsWith(geometry.State) ||
                    geometry.State.IntersectsWith(geometry.StatusBadge) ||
                    geometry.Detail.IntersectsWith(geometry.StatusBadge) ||
                    geometry.StatusBadge.IntersectsWith(geometry.TokenBadge) ||
                    geometry.Check.IntersectsWith(geometry.Update) ||
                    geometry.Update.Width - 24 < measuredTextWidth)
                {
                    throw new InvalidOperationException(
                        $"Combined status/token row geometry overlaps or clips at width {width} " +
                        $"with a {measuredTextWidth}px credential label.");
                }
            }
        }
    }

    private void InvalidateQuotaUsageCache(bool clearCachedData)
    {
        _quotaUsageInvalidationVersion++;
        _quotaUsageLoadError = null;
        _passiveQuotaMonitoringInputSignatures.Clear();
        if (!clearCachedData)
        {
            return;
        }

        _quotaUsageCache = null;
        _quotaUsageCacheVersion = -1;
        _quotaUsageLoadedAtUtc = default;
        _lastQuotaUsageLogWriteTimeUtc = default;
    }

    private void InvalidateUnifiedHistoryCache(bool clearCachedData)
    {
        _unifiedHistoryInvalidationVersion++;
        _unifiedHistoryLoadError = null;
        _unifiedHistoryContentIndexCancellation?.Cancel();
        _unifiedHistoryContentIndexCancellation = null;
        _unifiedHistoryContentIndexTask = null;
        _unifiedHistoryContentIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _unifiedHistoryContentIndexVersion = -1;
        _unifiedHistoryContentIndexRequestedVersion = -1;
        _unifiedHistoryContentIndexError = null;
        if (!clearCachedData)
        {
            return;
        }

        _unifiedHistoryCache = null;
        _unifiedHistoryCacheVersion = -1;
    }

    private void UpdateWorkspaceChrome()
    {
        _headerTitle.Text = GetWorkspaceTitle(_activeView);
        _headerSubtitle.Text = _activeView switch
        {
            WorkspaceView.AccountSwitch => "选择账号并用 Codex++、Codex 或 CLI 启动。",
            WorkspaceView.UnifiedHistory => "查看、全文搜索和管理本地聊天。",
            WorkspaceView.StatusCheck => "检查登录状态并管理账号凭据。",
            WorkspaceView.QuotaUsage => "本地用量估算与额度窗口。",
            WorkspaceView.ThemeSettings => "选择、预览并应用 Codex 外观。",
            WorkspaceView.SystemConfig => "启动目录与项目设置。",
            _ => ""
        };
        var subtitleDetail = _activeView switch
        {
            WorkspaceView.AccountSwitch => "账号配置和登录状态相互隔离；聊天记录集中保存在默认 .codex。",
            WorkspaceView.UnifiedHistory => "按标题和对话正文搜索；本地只读，不启动或登录 Codex++。",
            WorkspaceView.StatusCheck => "状态检查按账号单独执行；ChatGPT 登录、Access Token 与 API Key 均按账号目录隔离。",
            WorkspaceView.QuotaUsage => "按官方返回显示 5h、周或月额度窗口；不会在后台轮流登录账号。",
            WorkspaceView.ThemeSettings => "Codex 主题可以独立启用、应用或恢复，不影响账号与聊天记录。",
            WorkspaceView.SystemConfig => "项目根目录保存本地配置，启动目录用于打开 Codex。",
            _ => _headerSubtitle.Text
        };
        _toolTip.SetToolTip(_headerSubtitle, subtitleDetail);
        if (_controlsRow != null)
        {
            _controlsRow.Visible = _activeView is not WorkspaceView.ThemeSettings and
                                   not WorkspaceView.SystemConfig;
        }
        UpdateHeaderControlLayout();
        _searchBox.PlaceholderText = _activeView == WorkspaceView.UnifiedHistory
            ? "搜索标题或对话内容"
            : "搜索账号";

        if (_headerPanel != null)
        {
            // A theme owns one workspace background. Switching pages must not silently
            // recolor the hero surface, otherwise the same selected theme looks like a
            // different theme on every page.
            _headerPanel.BackColor = _palette.HeroStartColor;
            _headerPanel.GradientColor = _palette.HeroEndColor;
            _headerPanel.DecorationColor = Color.FromArgb(48, _palette.SecondaryAccentColor);
            _headerPanel.BorderColor = Color.FromArgb(82, _palette.TertiaryAccentColor);
            _headerPanel.Invalidate();
        }
    }

    private static string GetWorkspaceTitle(WorkspaceView view)
    {
        return view switch
        {
            WorkspaceView.AccountSwitch => "账号切换",
            WorkspaceView.UnifiedHistory => "聊天记录",
            WorkspaceView.StatusCheck => "状态与凭据",
            WorkspaceView.QuotaUsage => "额度显示",
            WorkspaceView.ThemeSettings => "Codex 主题",
            WorkspaceView.SystemConfig => "系统配置",
            _ => "账号工作台"
        };
    }

    private void ApplySidebarNavButtons()
    {
        ApplySidebarNavButton(_accountSwitchNavButton, _activeView == WorkspaceView.AccountSwitch);
        ApplySidebarNavButton(_unifiedHistoryNavButton, _activeView == WorkspaceView.UnifiedHistory);
        ApplySidebarNavButton(_statusCheckNavButton, _activeView == WorkspaceView.StatusCheck);
        ApplySidebarNavButton(_quotaUsageNavButton, _activeView == WorkspaceView.QuotaUsage);
        ApplySidebarNavButton(_themeSettingsNavButton, _activeView == WorkspaceView.ThemeSettings);
        ApplySidebarNavButton(_systemConfigNavButton, _activeView == WorkspaceView.SystemConfig);
    }

    private void ApplySidebarNavButton(Button button, bool selected)
    {
        var accent = button switch
        {
            _ when ReferenceEquals(button, _unifiedHistoryNavButton) => _palette.SecondaryAccentColor,
            _ when ReferenceEquals(button, _statusCheckNavButton) => _palette.SuccessColor,
            _ when ReferenceEquals(button, _themeSettingsNavButton) => _palette.AccentColor,
            _ when ReferenceEquals(button, _systemConfigNavButton) => _palette.SecondaryAccentColor,
            _ => _palette.AccentColor
        };
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.BorderColor = _palette.SidebarColor;
        button.BackColor = selected ? _palette.SidebarSelectedColor : _palette.SidebarColor;
        button.ForeColor = selected ? _palette.SidebarTextColor : _palette.SidebarMutedTextColor;
        button.Cursor = Cursors.Hand;
        button.Font = new Font(Font.FontFamily, 9.5F, selected ? FontStyle.Bold : FontStyle.Regular);
        if (button is ModernButton modern)
        {
            modern.BaseBackColor = selected ? _palette.SidebarSelectedColor : _palette.SidebarColor;
            modern.HoverBackColor = selected
                ? UiDesign.Blend(_palette.SidebarSelectedColor, accent, 0.16F)
                : _palette.SidebarHoverColor;
            modern.PressedBackColor = UiDesign.Blend(_palette.SidebarHoverColor, accent, 0.20F);
            modern.BorderColor = selected ? Color.FromArgb(82, accent) : Color.Transparent;
            modern.GradientBackColor = selected
                ? Color.FromArgb(46, accent)
                : Color.Transparent;
            modern.ShadowColor = Color.Transparent;
            modern.IconTileColor = selected
                ? Color.FromArgb(66, accent)
                : Color.FromArgb(28, accent);
            modern.IconTileBorderColor = selected
                ? Color.FromArgb(104, accent)
                : Color.FromArgb(46, accent);
            modern.UseSurfaceSheen = false;
            modern.TextColor = selected ? _palette.SidebarTextColor : _palette.SidebarMutedTextColor;
            modern.DisabledBackColor = _palette.SidebarColor;
            modern.DisabledTextColor = _palette.SidebarMutedTextColor;
            modern.FocusColor = accent;
            modern.AccentColor = accent;
            modern.ShowAccent = selected;
            modern.Invalidate();
        }
    }

    private void ChangeThemeMode()
    {
        ClearWorkspaceViewCache();
        var selectedIndex = Math.Clamp(_themeModePicker.SelectedIndex, 0, ThemeOptions.Length - 1);
        _appSettings.ThemeMode = ThemeOptions[selectedIndex].Mode;
        _themeService.SaveSettings(_appSettings);
        ApplyTheme();
        _statusBox.Text = $"已切换外观模式：{_themeModePicker.Text}";
    }

    private int FindCodexAppearanceOptionIndex(string? id)
    {
        return Array.FindIndex(
            CodexAppearanceOptions,
            option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private CodexAppearanceOption GetSelectedCodexAppearanceOption()
    {
        var index = FindCodexAppearanceOptionIndex(_selectedCodexAppearanceId);
        index = index < 0 ? 0 : index;
        return CodexAppearanceOptions[index];
    }

    private CodexAppearanceOption GetCodexAppearanceOptionById(string? id)
    {
        var index = FindCodexAppearanceOptionIndex(id);
        return CodexAppearanceOptions[index < 0 ? 0 : index];
    }

    private ThemeMode GetCodexAppearanceRuntimeMode(CodexAppearanceOption appearance) =>
        appearance.RuntimeMode ?? _appSettings.ThemeMode;

    private static string GetCodexAppearanceRuntimePresetId(CodexAppearanceOption appearance) =>
        string.IsNullOrWhiteSpace(appearance.RuntimePresetId)
            ? appearance.Id
            : appearance.RuntimePresetId;

    private string GetSelectedCodexAppearanceLabel()
    {
        var option = GetSelectedCodexAppearanceOption();
        return option.Id == "custom" ? _appSettings.CustomCodexTheme.Name : option.Label;
    }

    private string GetCodexAppearanceLabelById(string? id)
    {
        var index = FindCodexAppearanceOptionIndex(id);
        if (index < 0)
        {
            return CodexAppearanceOptions[0].Label;
        }
        return CodexAppearanceOptions[index].Id == "custom"
            ? _appSettings.CustomCodexTheme.Name
            : CodexAppearanceOptions[index].Label;
    }

    private void ShowCodexAppearanceDetail(string id)
    {
        if (FindCodexAppearanceOptionIndex(id) < 0)
        {
            return;
        }

        _selectedCodexAppearanceId = id;
        _showCodexAppearanceDetail = true;
        _statusBox.Text = $"正在预览：{GetSelectedCodexAppearanceLabel()}";
        RenderCards();
        ResetCardsScrollPosition();
    }

    private void ShowCodexAppearanceLibrary()
    {
        _showCodexAppearanceDetail = false;
        _statusBox.Text = "已返回 Codex 主题库。";
        RenderCards();
        ResetCardsScrollPosition();
    }

    private static bool IsOfficialCodexAppearance(CodexAppearanceOption appearance) =>
        string.Equals(appearance.Id, "official-default", StringComparison.OrdinalIgnoreCase);

    private bool IsCodexAppearanceStartupSelected(CodexAppearanceOption appearance)
    {
        if (IsOfficialCodexAppearance(appearance))
        {
            // Official appearance is a restore action, never a Dream Skin startup preset.
            return false;
        }

        var idMatches = string.Equals(
            _appSettings.CodexAppearancePresetId,
            appearance.Id,
            StringComparison.OrdinalIgnoreCase);
        return _appSettings.UseCodexDreamSkin && idMatches;
    }

    private bool TryEnableCodexStartupTheme(CodexAppearanceOption appearance)
    {
        if (IsOfficialCodexAppearance(appearance))
        {
            return false;
        }

        try
        {
            if (appearance.Id == "custom")
            {
                CodexDreamSkinService.SaveCustomAppearance(_appSettings.CustomCodexTheme);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return false;
        }

        _selectedCodexAppearanceId = appearance.Id;
        _appSettings.CodexAppearancePresetId = appearance.Id;
        _appSettings.UseCodexDreamSkin = true;
        _themeService.SaveSettings(_appSettings);
        return true;
    }

    private Task StartCodexAppearanceAsync(CodexAppearanceOption appearance)
    {
        return ApplyCodexDreamSkinAsync(appearance);
    }

    private void ToggleCodexDreamSkinPreference()
    {
        var selected = GetSelectedCodexAppearanceOption();
        if (IsOfficialCodexAppearance(selected))
        {
            return;
        }
        var enabledForSelected = _appSettings.UseCodexDreamSkin &&
                                 string.Equals(
                                     _appSettings.CodexAppearancePresetId,
                                     selected.Id,
                                     StringComparison.OrdinalIgnoreCase);
        var enable = !enabledForSelected;
        if (enable)
        {
            if (!TryEnableCodexStartupTheme(selected))
            {
                return;
            }
        }
        else
        {
            _appSettings.UseCodexDreamSkin = false;
        }
        _themeService.SaveSettings(_appSettings);
        _statusBox.Text = _appSettings.UseCodexDreamSkin
            ? $"Codex 启动时将同步“{GetCodexAppearanceLabelById(_appSettings.CodexAppearancePresetId)}”。"
            : "已关闭 Codex 启动同步；当前 Codex 外观不会被改动。";
        RenderCards();
    }

    private async Task ApplyCodexDreamSkinAsync(CodexAppearanceOption appearance)
    {
        _selectedCodexAppearanceId = appearance.Id;
        if (IsOfficialCodexAppearance(appearance))
        {
            await RestoreOfficialCodexAppearanceAsync();
            return;
        }
        var appearanceLabel = GetCodexAppearanceLabel(appearance);
        var projectPath = GetProjectPathForCodexAppearance();
        var confirmation = MessageBox.Show(
            this,
            $"应用“{appearanceLabel}”会关闭当前 Codex 一次，安装图片背景并以安全的本地注入模式重新打开。账号、聊天记录和插件不会被修改。",
            "应用 Codex 外观",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.OK)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            _statusBox.Text = $"正在将“{appearanceLabel}”同步到 Codex…";
            if (appearance.Id == "custom")
            {
                CodexDreamSkinService.SaveCustomAppearance(_appSettings.CustomCodexTheme);
            }
            try
            {
                var projectOpened = await _codex.ApplyCodexDreamSkinAsync(
                    GetCodexAppearanceRuntimeMode(appearance),
                    GetCodexAppearanceRuntimePresetId(appearance),
                    appearanceLabel,
                    projectPath);
                _statusBox.Text = projectOpened
                    ? $"Codex 已同步为“{appearanceLabel}”。"
                    : $"Codex 已同步为“{appearanceLabel}”，但项目链接未自动打开。";
            }
            catch (CodexDreamSkinApplyException ex) when (ex.OfficialAppearanceRestored)
            {
                _appSettings.UseCodexDreamSkin = false;
                _themeService.SaveSettings(_appSettings);
                _statusBox.Text = "主题应用失败，已自动恢复 Codex 官方外观。";
                RenderCards();
                throw;
            }
            _appSettings.UseCodexDreamSkin = true;
            _appSettings.CodexAppearancePresetId = appearance.Id;
            _themeService.SaveSettings(_appSettings);
            RenderCards();
        });
    }

    private async Task RestoreOfficialCodexAppearanceAsync()
    {
        var projectPath = GetProjectPathForCodexAppearance();
        var confirmation = MessageBox.Show(
            this,
            "恢复会关闭当前 Codex 一次，移除 Account Manager 写入的外观配置后重新打开官方客户端。",
            "恢复 Codex 官方外观",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.OK)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            _statusBox.Text = "正在恢复 Codex 官方外观…";
            var reopened = await _codex.RestoreOfficialCodexAppearanceAsync(projectPath);
            _appSettings.UseCodexDreamSkin = false;
            _selectedCodexAppearanceId = "official-default";
            _themeService.SaveSettings(_appSettings);
            _statusBox.Text = reopened
                ? "Codex 已恢复官方外观。"
                : "Codex 外观已恢复，请从开始菜单手动启动 Codex。";
            RenderCards();
        });
    }

    private void UpdateHeaderControlLayout()
    {
        if (_controlsRow == null || _searchShell == null)
        {
            return;
        }

        var available = Math.Max(260, _controlsRow.ClientSize.Width - _controlsRow.Padding.Horizontal);
        var updateButtonWidth = _checkUpdatesButton.Visible ? _checkUpdatesButton.Width + _checkUpdatesButton.Margin.Horizontal : 0;
        _searchShell.Width = Math.Max(260, Math.Min(500, available - updateButtonWidth));
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_formClosed || _updateCheckRunning)
        {
            return;
        }

        _updateCheckRunning = true;
        if (!_checkUpdatesButton.IsDisposed)
        {
            _checkUpdatesButton.Enabled = false;
        }

        try
        {
            var update = await _updateService.CheckAsync();
            if (update is null)
            {
                if (manual && !_formClosed)
                {
                    MessageBox.Show(
                        this,
                        "当前已经是最新版本，或暂时无法连接 GitHub。",
                        "检查更新",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return;
            }

            var answer = MessageBox.Show(
                this,
                $"发现新版本 {update.Version}。\r\n\r\n现在下载并安装吗？安装会保留已有账号和本地配置。",
                "Codex Account Manager 更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            _statusBox.Text = "正在下载并校验更新包，请稍候……";
            await _updateService.ScheduleInstallAsync(update);
            _statusBox.Text = "更新包已准备完成，程序即将关闭并安装新版本。";
            await Task.Delay(350);
            Application.Exit();
        }
        catch (Exception ex)
        {
            if (!_formClosed)
            {
                MessageBox.Show(
                    this,
                    $"更新失败，当前版本未被修改。\r\n\r\n{ex.Message}",
                    "Codex Account Manager 更新",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _updateCheckRunning = false;
            if (!_formClosed && !_checkUpdatesButton.IsDisposed)
            {
                _checkUpdatesButton.Enabled = true;
            }
        }
    }

    private void UpdateStatusBarLayout()
    {
        if (_contentLayout == null ||
            _statusBox.IsDisposed ||
            _contentLayout.RowStyles.Count < 3)
        {
            return;
        }

        var width = _statusBox.ClientSize.Width - _statusBox.Padding.Horizontal;
        if (width <= 0)
        {
            width = _contentLayout.ClientSize.Width - _statusBox.Padding.Horizontal;
        }

        var measured = TextRenderer.MeasureText(
            string.IsNullOrWhiteSpace(_statusBox.Text) ? "国Ag" : _statusBox.Text,
            _statusBox.Font,
            new Size(Math.Max(320, width), 4096),
            TextFormatFlags.WordBreak |
            TextFormatFlags.NoPrefix |
            TextFormatFlags.TextBoxControl);
        var scale = Math.Max(1F, DeviceDpi / 96F);
        // The footer is a real layout row, not an overlay. Reserve a little more space
        // as DPI rises so the descenders and the final line never touch the client edge.
        var dpiMinimum = 52 + (int)Math.Ceiling(Math.Max(0F, scale - 1F) * 12F);
        var desiredHeight = Math.Max(
            dpiMinimum,
            measured.Height + _statusBox.Padding.Vertical + (int)Math.Ceiling(8F * scale));
        _contentLayout.RowStyles[2].Height = Math.Min(128, desiredHeight);
    }

    private static string GetWindowsClientDisplayName(WindowsClientMode mode) => mode switch
    {
        WindowsClientMode.OfficialCodex => "官方 Codex",
        _ => "Codex++"
    };

    private void LoadAccounts()
    {
        ClearWorkspaceViewCache();
        _accounts = _store.LoadAccounts();
        var accountConfigFailures = new List<string>();
        foreach (var account in _accounts.Where(candidate => candidate.IsAccessToken))
        {
            try
            {
                // Existing accounts may still point directly at chatgpt.com. Migrate their
                // managed provider locally without touching auth.json or making a request.
                _codex.EnsureLocalPatAccountConfig(account);
            }
            catch
            {
                accountConfigFailures.Add(account.Name);
            }
        }
        foreach (var account in _accounts.Where(candidate => candidate.IsOfficialOAuth))
        {
            try
            {
                _codex.EnsureOfficialOAuthAccountConfig(account);
            }
            catch
            {
                accountConfigFailures.Add(account.Name);
            }
        }
        InvalidateQuotaUsageCache(clearCachedData: true);
        HydratePersistedQuotaSnapshots();
        try
        {
            // Hydrate the last parsed local-only index synchronously. This contains no
            // credential and performs no network request, so the quota page can render its
            // real previous values immediately while the watcher reconciles appended JSONL.
            _quotaUsageCache = _usageTracker.TryBuildCachedReport(_accounts);
            if (_quotaUsageCache != null)
            {
                _quotaUsageCacheVersion = _quotaUsageInvalidationVersion;
            }
        }
        catch
        {
            // A damaged/stale index is repaired by the normal background scan.
            _quotaUsageCache = null;
        }
        SyncCurrentAccountSelection();
        _usageTracker.EnsureCurrentAccountTracking(GetCurrentAccountRecord());
        RenderCards();
        if (_activeView == WorkspaceView.QuotaUsage)
        {
            _ = RefreshQuotaUsageAsync(force: true, _workspaceLoadGeneration);
        }
        else
        {
            // Warm the local usage cache while the user is on another page so opening
            // the quota view does not have to wait for the first full log aggregation.
            _ = RefreshQuotaUsageAsync(force: false, _workspaceLoadGeneration);
        }
        if (accountConfigFailures.Count > 0)
        {
            _statusBox.Text =
                "以下账号未能更新本地登录配置，请检查其 config.toml 写入权限：" +
                string.Join("、", accountConfigFailures);
        }
    }

    private void HydratePersistedQuotaSnapshots()
    {
        IReadOnlyDictionary<string, PersistedQuotaSnapshot> snapshots;
        try
        {
            snapshots = _quotaSnapshotStore.LoadForAccounts(_accounts);
        }
        catch
        {
            // The snapshot is an optional accelerator. A damaged or locked cache must not
            // prevent the account list from loading; the normal official refresh will refill it.
            return;
        }

        foreach (var account in _accounts)
        {
            var accountKey = QuotaAccountIdentity.CreateKey(account);
            if (!snapshots.TryGetValue(accountKey, out var snapshot))
            {
                continue;
            }

            if (snapshot.AvailableCount.HasValue)
            {
                SetResetCreditState(
                    account,
                    ResetCreditStatus.Known,
                    snapshot.AvailableCount.Value);
            }
            else
            {
                SetResetCreditState(account, ResetCreditStatus.Unavailable);
            }

            if (snapshot.Primary == null &&
                snapshot.Secondary == null &&
                snapshot.CreditBalance == null &&
                snapshot.IndividualLimit == null &&
                string.IsNullOrWhiteSpace(snapshot.PlanType))
            {
                continue;
            }

            _liveRateLimitCache[accountKey] = new LiveRateLimitSnapshot(
                snapshot.Primary?.UsedPercent,
                snapshot.Primary?.WindowMinutes,
                snapshot.Primary?.ResetsAtUtc,
                snapshot.Secondary?.UsedPercent,
                snapshot.Secondary?.WindowMinutes,
                snapshot.Secondary?.ResetsAtUtc,
                snapshot.CreditBalance,
                snapshot.IndividualLimit,
                snapshot.PlanType,
                snapshot.ObservedAtUtc);
        }
    }

    private void SyncCurrentAccountSelection()
    {
        // appsettings is only a hint after a reboot or an external login. The shared
        // profile on disk is authoritative and can be compared without any network call.
        var matchingProfiles = _accounts
            .Where(account =>
            {
                try
                {
                    return _codex.IsSharedCredentialAlreadySelected(account);
                }
                catch
                {
                    return false;
                }
            })
            .ToList();
        var resolved = matchingProfiles.FirstOrDefault(account =>
                           account.Name.Equals(_currentAccountName, StringComparison.OrdinalIgnoreCase)) ??
                       matchingProfiles.FirstOrDefault();
        if (resolved != null)
        {
            SetCurrentAccount(resolved.Name, false);
            return;
        }

        // If the shared profile cannot be identified (for example immediately after a
        // reboot, while the client is closed, or for a compatible API profile), retain
        // the last account explicitly launched by this manager. This keeps the current
        // account marker and current-first ordering stable without any network request.
        var remembered = _accounts.FirstOrDefault(account =>
            account.Name.Equals(_currentAccountName, StringComparison.OrdinalIgnoreCase));
        SetCurrentAccount(remembered?.Name, false);
    }

    private void RenderCards()
    {
        if (_cardsPanel.IsDisposed || _renderingCards)
        {
            return;
        }

        _renderingCards = true;
        try
        {
            RenderCardsCore();
        }
        finally
        {
            _renderingCards = false;
            QueueCardsAnimationVisibilityRefresh();
        }
    }

    private void RenderCardsCore()
    {
        using var redraw = NativeWindowTheme.SuspendRedraw(_accountLayout);
        ApplyDetailViewportMode();
        _cardsPanel.SuspendLayout();
        DetachPersistentSystemConfigControls();
        foreach (var oldControl in _cardsPanel.Controls.Cast<Control>().ToArray())
        {
            oldControl.Dispose();
        }
        _cardsPanel.Controls.Clear();
        UpdateWorkspaceChrome();
        var workspaceWidth = GetWorkspaceWidth();
        _renderedWorkspaceWidth = workspaceWidth;

        if (_activeView == WorkspaceView.ThemeSettings)
        {
            if (_showCodexAppearanceDetail)
            {
                _cardsPanel.Controls.Add(CreateCodexAppearanceDetailPanel(workspaceWidth));
            }
            else
            {
                RenderThemeSettingsPanel(workspaceWidth);
            }
            _cardsPanel.ResumeLayout();
            return;
        }

        var query = _searchBox.Text.Trim();
        if (_activeView == WorkspaceView.UnifiedHistory)
        {
            if (_unifiedHistoryCache == null)
            {
                _cardsPanel.Controls.Add(_unifiedHistoryLoadError == null
                    ? CreateWorkspaceLoadingState(
                        workspaceWidth,
                        "正在读取聊天记录",
                        "正在后台加载…")
                    : CreateUnifiedHistoryErrorState(workspaceWidth, _unifiedHistoryLoadError.Message));
            }
            else
            {
                RenderUnifiedHistory(query, workspaceWidth, _unifiedHistoryCache);
            }
            _cardsPanel.ResumeLayout();
            return;
        }

        var visible = _accounts.Where(a =>
            string.IsNullOrWhiteSpace(query) ||
            a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            a.CodexHome.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (visible.Count == 0)
        {
            _selectedAccountName = null;
            _showAccountDetail = false;
            _cardsPanel.Controls.Add(CreateEmptyState(GetWorkspaceWidth()));
            _cardsPanel.ResumeLayout();
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedAccountName) ||
            visible.All(account => !account.Name.Equals(_selectedAccountName, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedAccountName = visible.FirstOrDefault(IsCurrentAccount)?.Name ?? visible[0].Name;
        }

        if (_activeView == WorkspaceView.SystemConfig)
        {
            _cardsPanel.Controls.Add(CreateSystemConfigPanel(workspaceWidth));
            _cardsPanel.ResumeLayout();
            return;
        }

        var selectedAccount = visible.First(account => account.Name.Equals(_selectedAccountName, StringComparison.OrdinalIgnoreCase));
        if (_activeView == WorkspaceView.AccountSwitch && _showAccountDetail)
        {
            _cardsPanel.Controls.Add(CreateAccountCard(selectedAccount, workspaceWidth));
            _cardsPanel.ResumeLayout();
            return;
        }

        UsageReport? usageReport = null;
        if (_activeView == WorkspaceView.QuotaUsage)
        {
            if (_quotaUsageCache == null)
            {
                _cardsPanel.Controls.Add(_quotaUsageLoadError == null
                    ? CreateQuotaUsageLoadingHint(workspaceWidth)
                    : CreateUnifiedHistoryErrorState(workspaceWidth, _quotaUsageLoadError.Message));
                _cardsPanel.ResumeLayout();
                return;
            }

            usageReport = _quotaUsageCache;
        }
        if (usageReport != null)
        {
            ApplyLiveRateLimitSnapshots(usageReport);
            UpdateQuotaLimitProfilesFromReport(usageReport);
        }

        if (_activeView == WorkspaceView.QuotaUsage && _showAccountDetail)
        {
            var usage = usageReport!.Accounts.First(summary => summary.AccountName.Equals(selectedAccount.Name, StringComparison.OrdinalIgnoreCase));
            _cardsPanel.Controls.Add(CreateQuotaUsageDetailCard(selectedAccount, usage, workspaceWidth));
            _cardsPanel.ResumeLayout();
            return;
        }

        foreach (var group in BuildAccountGroups(visible, usageReport))
        {
            var groupRows = new List<Control>(group.Accounts.Count);
            var collapsed = IsAccountGroupCollapsed(group.Key);
            _cardsPanel.Controls.Add(CreateAccountGroupHeader(group, workspaceWidth, groupRows));
            PumpHeaderAnimationFrame();

            foreach (var account in group.Accounts)
            {
                Control row = _activeView switch
                {
                    WorkspaceView.StatusCheck => CreateStatusTokenRow(account, workspaceWidth),
                    WorkspaceView.QuotaUsage => CreateQuotaUsageRow(
                        account,
                        usageReport!.Accounts.First(summary => summary.AccountName.Equals(account.Name, StringComparison.OrdinalIgnoreCase)),
                        workspaceWidth),
                    _ => CreateAccountSwitchRow(account, workspaceWidth)
                };
                row.Visible = !collapsed;
                groupRows.Add(row);
                _cardsPanel.Controls.Add(row);
                PumpHeaderAnimationFrame();
            }
        }

        _cardsPanel.ResumeLayout();
    }

    private void DetachPersistentSystemConfigControls()
    {
        DetachPersistentControl(_projectPathShell);
        DetachPersistentControl(_patGatewayProxyAddressShell);
        DetachPersistentControl(_patGatewayProxyPortShell);
        DetachPersistentControl(_patGatewayProxyDetectionLabel);
    }

    private static void DetachPersistentControl(Control? control)
    {
        if (control is null || control.IsDisposed)
        {
            return;
        }

        control.Parent?.Controls.Remove(control);
    }

    private void PumpHeaderAnimationFrame()
    {
        if (_headerPanel is not { ShowStarfield: true } header ||
            header.IsDisposed ||
            !header.IsHandleCreated)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var minimumTicks = Math.Max(1L, Stopwatch.Frequency / 30L);
        if (_lastHeaderAnimationPumpTimestamp != 0L &&
            now - _lastHeaderAnimationPumpTimestamp < minimumTicks)
        {
            return;
        }

        _lastHeaderAnimationPumpTimestamp = now;
        header.RenderStarfieldFrameNow();
    }

    private void ApplyDetailViewportMode()
    {
        if (_contentLayout == null || _controlsRow == null || _contentLayout.RowStyles.Count == 0)
        {
            return;
        }

        var compact = (_showAccountDetail &&
                       _activeView is WorkspaceView.AccountSwitch or WorkspaceView.QuotaUsage) ||
                      (_showCodexAppearanceDetail && _activeView == WorkspaceView.ThemeSettings) ||
                      _activeView == WorkspaceView.SystemConfig;
        // Every workspace uses one stable hero footprint. Hiding the search row on a
        // detail page must not resize the starfield banner or shift the page below it.
        _contentLayout.RowStyles[0].Height = WorkspaceHeroHeight;
        _controlsRow.Visible = !compact;
        _accountLayout.Padding = compact
            ? new Padding(2, 4, 0, 0)
            : new Padding(2, 12, 0, 4);
    }

    private IReadOnlyList<AccountGroupSection> BuildAccountGroups(
        IReadOnlyList<AccountRecord> accounts,
        UsageReport? usageReport = null)
    {
        List<AccountRecord> CurrentFirst(IEnumerable<AccountRecord> source) =>
            source.OrderByDescending(IsCurrentAccount).ToList();

        string EffectiveQuotaLimitType(AccountRecord account)
        {
            var usage = usageReport?.Accounts.FirstOrDefault(summary =>
                summary.AccountName.Equals(account.Name, StringComparison.OrdinalIgnoreCase));
            return usage == null
                ? account.QuotaLimitType
                : ResolveQuotaLimitType(account, usage);
        }

        var apiAccounts = CurrentFirst(accounts.Where(account => account.IsCompatibleApi));
        var weeklyAccounts = CurrentFirst(accounts.Where(account =>
            !account.IsCompatibleApi &&
            AccountQuotaLimitType.IsWeeklyCategory(EffectiveQuotaLimitType(account))));
        var monthlyAccounts = CurrentFirst(accounts.Where(account =>
            !account.IsCompatibleApi &&
            EffectiveQuotaLimitType(account) == AccountQuotaLimitType.Monthly));
        var pendingAccounts = CurrentFirst(accounts.Where(account =>
            !account.IsCompatibleApi &&
            EffectiveQuotaLimitType(account) == AccountQuotaLimitType.Unknown));

        var groups = new List<AccountGroupSection>(4);
        if (apiAccounts.Count > 0)
        {
            groups.Add(new AccountGroupSection("api", "API 账号", apiAccounts));
        }
        if (weeklyAccounts.Count > 0)
        {
            groups.Add(new AccountGroupSection("weekly", "周额度账号", weeklyAccounts));
        }
        if (monthlyAccounts.Count > 0)
        {
            groups.Add(new AccountGroupSection("monthly", "月额度账号", monthlyAccounts));
        }
        if (pendingAccounts.Count > 0)
        {
            groups.Add(new AccountGroupSection("pending", "待识别账号", pendingAccounts));
        }

        return groups
            .OrderByDescending(group => group.Accounts.Any(IsCurrentAccount))
            .ToList();
    }

    private string GetAccountGroupStateKey(string groupKey) => $"{_activeView}:{groupKey}";

    private bool IsAccountGroupCollapsed(string groupKey) =>
        _collapsedAccountGroups.Contains(GetAccountGroupStateKey(groupKey));

    private Color GetAccountGroupAccent(string groupKey)
    {
        return groupKey switch
        {
            "api" => _palette.SecondaryAccentColor,
            "weekly" => _palette.SuccessColor,
            "monthly" => _palette.TertiaryAccentColor,
            "pending" => _palette.WarningColor,
            _ => _palette.PrimaryColor
        };
    }

    private Control CreateAccountGroupHeader(
        AccountGroupSection group,
        int width,
        IReadOnlyList<Control> groupRows)
    {
        var collapsed = IsAccountGroupCollapsed(group.Key);
        var accent = GetAccountGroupAccent(group.Key);
        const int rightPadding = 12;
        const int toggleWidth = 86;
        const int countWidth = 72;
        const int headerGap = 8;
        var toggleLeft = width - rightPadding - toggleWidth;
        var countLeft = toggleLeft - headerGap - countWidth;
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 50,
            Radius = 12,
            BorderColor = Color.FromArgb(50, accent),
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceColor, accent, 0.025F),
            AccentColor = accent,
            AccentWidth = 3,
            Margin = new Padding(0, 2, 0, 8),
            Cursor = Cursors.Hand,
            AccessibleName = $"{group.Title}，{group.Accounts.Count} 个账号",
            Tag = group
        };

        var title = new Label
        {
            Text = group.Title,
            Left = 20,
            Top = 4,
            Width = Math.Max(160, countLeft - 32),
            Height = 40,
            Font = new Font(Font.FontFamily, 9.6F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var count = MakeBadge(
            $"{group.Accounts.Count} 个",
            countLeft,
            11,
            Color.FromArgb(24, accent),
            accent);
        count.Width = countWidth;
        count.Height = 28;
        count.Cursor = Cursors.Hand;
        panel.Controls.Add(count);

        var toggle = MakeAccountGroupToggleButton(collapsed, toggleLeft, 7);
        toggle.AccessibleName = collapsed ? "展开分组" : "收起分组";
        _toolTip.SetToolTip(toggle, collapsed ? "展开" : "收起");
        panel.Controls.Add(toggle);

        EventHandler toggleGroup = (_, _) =>
        {
            var stateKey = GetAccountGroupStateKey(group.Key);
            var collapseRows = _collapsedAccountGroups.Add(stateKey);
            if (!collapseRows)
            {
                _collapsedAccountGroups.Remove(stateKey);
            }

            // Folding can remove the native vertical scrollbar. Suspend native redraw
            // across the one layout pass so Windows never exposes an intermediate blank
            // viewport, while the fixed gutter keeps every row at the same width.
            using (NativeWindowTheme.SuspendRedraw(_cardsPanel))
            {
                _cardsPanel.SuspendLayout();
                try
                {
                    foreach (var row in groupRows)
                    {
                        row.Visible = !collapseRows;
                    }

                    toggle.Text = collapseRows ? "展开" : "收起";
                    toggle.AccessibleName = collapseRows ? "展开分组" : "收起分组";
                    _toolTip.SetToolTip(toggle, collapseRows ? "展开" : "收起");
                }
                finally
                {
                    _cardsPanel.ResumeLayout(performLayout: false);
                }

                _cardsPanel.PerformLayout();
            }
        };
        panel.Click += toggleGroup;
        title.Click += toggleGroup;
        count.Click += toggleGroup;
        toggle.Click += toggleGroup;
        return panel;
    }

    private void RenderUnifiedHistory(
        string query,
        int width,
        IReadOnlyList<UnifiedThreadRecord> allThreads)
    {
        var sharedHome = CodexCliService.GetDefaultCodexHome();
        var contentIndexReady =
            _unifiedHistoryContentIndexVersion == _unifiedHistoryCacheVersion &&
            _unifiedHistoryCacheVersion >= 0;
        if (!string.IsNullOrWhiteSpace(query) &&
            !contentIndexReady &&
            _unifiedHistoryContentIndexTask == null &&
            _unifiedHistoryContentIndexError == null)
        {
            EnsureUnifiedHistoryContentIndex(allThreads, _unifiedHistoryCacheVersion);
        }
        var contentIndexLoading =
            !string.IsNullOrWhiteSpace(query) &&
            !contentIndexReady &&
            _unifiedHistoryContentIndexTask != null;
        var contentSearchStatus = string.IsNullOrWhiteSpace(query)
            ? null
            : contentIndexReady
                ? "标题与正文搜索已就绪"
                : _unifiedHistoryContentIndexError != null
                    ? "正文索引失败，仅搜索标题"
                    : "正在建立正文索引…";
        var visibleThreads = allThreads.Where(thread =>
                string.IsNullOrWhiteSpace(query) ||
                thread.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                thread.Preview.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                thread.WorkingDirectory.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                thread.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (contentIndexReady &&
                 _unifiedHistoryContentIndex.TryGetValue(thread.Id, out var searchText) &&
                 searchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        var renderedThreads = visibleThreads
            .Take(Math.Max(UnifiedHistoryPageSize, _unifiedHistoryVisibleLimit))
            .ToList();

        _cardsPanel.Controls.Add(CreateUnifiedHistorySummary(
            width,
            sharedHome,
            allThreads.Count,
            allThreads.Count(thread => thread.Archived),
            visibleThreads.Count,
            renderedThreads.Count,
            contentSearchStatus));
        PumpHeaderAnimationFrame();

        if (visibleThreads.Count == 0)
        {
            _cardsPanel.Controls.Add(contentIndexLoading
                ? CreateUnifiedHistoryMessagePanel(
                    width,
                    "正在搜索对话正文",
                    "首次全文搜索正在建立本地索引，完成后会自动显示匹配结果。",
                    _palette.PrimaryColor)
                : CreateUnifiedHistoryEmptyState(width, query));
        }
        else
        {
            foreach (var thread in renderedThreads)
            {
                string? matchSnippet = null;
                if (!string.IsNullOrWhiteSpace(query) &&
                    contentIndexReady &&
                    _unifiedHistoryContentIndex.TryGetValue(thread.Id, out var searchText) &&
                    searchText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    matchSnippet = CreateUnifiedHistorySearchSnippet(searchText, query);
                }
                _cardsPanel.Controls.Add(CreateUnifiedHistoryRow(thread, width, matchSnippet));
                PumpHeaderAnimationFrame();
            }
        }

        _statusBox.Text =
            contentIndexLoading
                ? $"已显示 {renderedThreads.Count}/{visibleThreads.Count} 条标题结果；正在索引对话正文…"
                : $"已显示 {renderedThreads.Count}/{visibleThreads.Count} 条聊天；本地只读对话，不启动或登录 Codex++。";
        _toolTip.SetToolTip(
            _statusBox,
            $"聊天目录：{sharedHome}；总计 {allThreads.Count} 条。可搜索标题与对话正文；系统信息和工具日志已过滤。");
    }

    private Control CreateUnifiedHistorySummary(
        int width,
        string sharedHome,
        int total,
        int archived,
        int visible,
        int rendered,
        string? contentSearchStatus)
    {
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 82,
            Radius = 12,
            BorderColor = UiDesign.Blend(_palette.BorderColor, _palette.PrimaryColor, 0.24F),
            BackColor = _palette.SurfaceColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceColor, _palette.PrimaryColor, 0.035F),
            ShadowColor = Color.FromArgb(12, _palette.ShadowColor),
            Elevation = 1,
            Margin = new Padding(0, 0, 0, 14)
        };

        var title = new Label
        {
            Text = $"聊天库 · {total} 条",
            Left = 20,
            Top = 6,
            Width = Math.Max(320, width - (visible > rendered ? 390 : 250)),
            Height = 32,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var path = new Label
        {
            Text = $"活动 {total - archived} · 归档 {archived} · 显示 {rendered}/{visible}" +
                   (string.IsNullOrWhiteSpace(contentSearchStatus) ? "" : $" · {contentSearchStatus}"),
            Left = 20,
            Top = 40,
            Width = Math.Max(320, width - (visible > rendered ? 390 : 250)),
            Height = 26,
            Font = new Font(Font.FontFamily, 8.5F),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(path, _palette, true);
        _toolTip.SetToolTip(
            path,
            $"聊天目录：{sharedHome}；筛选结果 {visible} 条，当前显示 {rendered} 条。" +
            "全文索引只读取本地对话，不启动或登录 Codex++。");
        panel.Controls.Add(path);

        if (visible > rendered)
        {
            var loadMore = MakeHistoryActionButton("加载更多", width - 314, 21, 160, iconText: "＋");
            loadMore.Click += (_, _) =>
            {
                _unifiedHistoryVisibleLimit += UnifiedHistoryPageSize;
                RenderCards();
            };
            panel.Controls.Add(loadMore);
        }

        var refresh = MakeHistoryActionButton("刷新", width - 142, 21, 108, iconText: "↻");
        refresh.Click += async (_, _) =>
        {
            InvalidateUnifiedHistoryCache(clearCachedData: false);
            await RefreshUnifiedHistoryAsync(force: true, _workspaceLoadGeneration);
        };
        panel.Controls.Add(refresh);
        return panel;
    }

    private Control CreateUnifiedHistoryRow(
        UnifiedThreadRecord thread,
        int width,
        string? matchSnippet = null)
    {
        const int rowHeight = 104;
        const int actionsWidth = 450;
        var contentWidth = Math.Max(320, width - actionsWidth - 44);
        var row = new RoundedPanel
        {
            Width = width,
            Height = rowHeight,
            Radius = 14,
            BorderColor = thread.Archived ? _palette.WarningColor : _palette.BorderColor,
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(
                _palette.CardColor,
                thread.Archived ? _palette.WarningColor : _palette.PrimaryColor,
                thread.Archived ? 0.035F : 0.022F),
            AccentColor = thread.Archived
                ? _palette.WarningColor
                : UiDesign.Blend(_palette.PrimaryColor, _palette.SecondaryAccentColor, 0.34F),
            AccentWidth = 2,
            ShadowColor = Color.FromArgb(12, _palette.ShadowColor),
            Elevation = 1,
            Margin = new Padding(0, 0, 0, 12),
            Cursor = Cursors.Hand,
            AccessibleName = $"阅读本地聊天：{thread.Title}",
            AccessibleDescription = thread.Id
        };

        var title = new Label
        {
            Text = thread.Title,
            Left = 20,
            Top = 14,
            Width = contentWidth,
            Height = 34,
            Font = new Font(Font.FontFamily, 9.8F, FontStyle.Bold),
            AutoEllipsis = true,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(title, _palette);
        _toolTip.SetToolTip(title, thread.Title);
        row.Controls.Add(title);

        var updated = thread.UpdatedAt == DateTimeOffset.MinValue
            ? "时间未知"
            : thread.UpdatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        var project = string.IsNullOrWhiteSpace(thread.WorkingDirectory)
            ? "项目未知"
            : thread.WorkingDirectory;
        var model = string.Join(" / ", new[] { thread.Provider, thread.Model }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var meta = new Label
        {
            Text = string.IsNullOrWhiteSpace(matchSnippet)
                ? $"{updated}    {project}{(string.IsNullOrWhiteSpace(model) ? "" : "    " + model)}"
                : $"正文匹配：{matchSnippet}",
            Left = 20,
            Top = 58,
            Width = contentWidth,
            Height = 28,
            Font = new Font(Font.FontFamily, 8.3F),
            AutoEllipsis = true,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(meta, _palette, true);
        _toolTip.SetToolTip(
            meta,
            string.IsNullOrWhiteSpace(matchSnippet)
                ? meta.Text
                : $"{updated} · {project}{(string.IsNullOrWhiteSpace(model) ? "" : " · " + model)}\n{matchSnippet}");
        row.Controls.Add(meta);

        var badge = MakeBadge(
            thread.Archived ? "已归档" : "活动",
            width - 420,
            14,
            thread.Archived ? Color.FromArgb(40, _palette.WarningColor) : Color.FromArgb(44, _palette.SuccessColor),
            thread.Archived ? _palette.WarningColor : _palette.SuccessColor);
        badge.Width = 132;
        badge.Height = 34;
        badge.Cursor = Cursors.Hand;
        row.Controls.Add(badge);

        var openHint = new Label
        {
            Text = "阅读  ›",
            Left = width - 420,
            Top = 58,
            Width = 140,
            Height = 28,
            Font = new Font(Font.FontFamily, 8F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(openHint, _palette);
        openHint.ForeColor = _palette.PrimaryColor;
        row.Controls.Add(openHint);

        var archive = MakeHistoryActionButton(
            thread.Archived ? "取消归档" : "归档",
            width - 260,
            31,
            112);
        archive.Click += async (_, _) => await ToggleUnifiedThreadArchiveAsync(thread);
        row.Controls.Add(archive);

        var delete = MakeHistoryActionButton("删除", width - 136, 31, 104, danger: true);
        delete.Click += async (_, _) => await DeleteUnifiedThreadAsync(thread);
        row.Controls.Add(delete);

        EventHandler openThread = async (_, _) => await OpenUnifiedThreadAsync(thread);
        row.Click += openThread;
        title.Click += openThread;
        meta.Click += openThread;
        badge.Click += openThread;
        openHint.Click += openThread;
        return row;
    }

    private Control CreateUnifiedHistoryEmptyState(int width, string query)
    {
        var message = string.IsNullOrWhiteSpace(query)
            ? "共享 .codex 中还没有可显示的本地任务。"
            : $"没有找到与“{query}”匹配的聊天记录。";
        return CreateUnifiedHistoryMessagePanel(width, "没有聊天记录", message, _palette.BorderColor);
    }

    private Control CreateUnifiedHistoryErrorState(int width, string message)
    {
        return CreateUnifiedHistoryMessagePanel(width, "读取失败", message, _palette.WarningColor);
    }

    private Control CreateWorkspaceLoadingState(int width, string title, string message)
    {
        return CreateUnifiedHistoryMessagePanel(
            width,
            title,
            message,
            _palette.PrimaryColor,
            centered: true);
    }

    private Control CreateQuotaUsageLoadingHint(int width)
    {
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 58,
            Radius = 14,
            BorderColor = Color.FromArgb(72, _palette.PrimaryColor),
            BackColor = _palette.CardColor,
            Margin = new Padding(0, 0, 0, 12)
        };
        var hint = new Label
        {
            Text = "正在读取最近的本地用量…",
            Left = 20,
            Top = 9,
            Width = width - 40,
            Height = 38,
            Font = new Font(Font.FontFamily, 8.9F, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleCenter,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(hint, _palette, true);
        panel.Controls.Add(hint);
        return panel;
    }

    private Control CreateUnifiedHistoryMessagePanel(
        int width,
        string titleText,
        string message,
        Color borderColor,
        bool centered = false)
    {
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 150,
            Radius = 14,
            BorderColor = borderColor,
            BackColor = _palette.CardColor,
            Margin = new Padding(0, 0, 0, 12)
        };
        var title = new Label
        {
            Text = titleText,
            Left = centered ? 36 : 22,
            Top = centered ? 30 : 24,
            Width = width - (centered ? 72 : 44),
            Height = centered ? 40 : 30,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = centered ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);
        var text = new Label
        {
            Text = message,
            Left = centered ? 36 : 22,
            Top = centered ? 74 : 66,
            Width = width - (centered ? 72 : 44),
            Height = centered ? 44 : 58,
            Font = new Font(Font.FontFamily, 8.8F),
            AutoEllipsis = !centered,
            TextAlign = centered ? ContentAlignment.MiddleCenter : ContentAlignment.TopLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(text, _palette, true);
        panel.Controls.Add(text);
        return panel;
    }

    private async Task OpenUnifiedThreadAsync(UnifiedThreadRecord thread)
    {
        if (_openingUnifiedThread)
        {
            return;
        }

        try
        {
            _openingUnifiedThread = true;
            _statusBox.Text = $"正在读取本地聊天简版：{thread.Title}";
            var sharedHome = CodexCliService.GetDefaultCodexHome();
            var transcript = await Task.Run(() => _threadTranscript.Load(sharedHome, thread));
            if (_formClosed || IsDisposed)
            {
                return;
            }

            using var dialog = new ThreadPreviewDialog(thread, transcript, _palette);
            _statusBox.Text = $"正在阅读本地聊天简版：{thread.Title}";
            dialog.ShowDialog(this);
            _statusBox.Text = $"已关闭本地聊天简版：{thread.Title}；未启动或登录 Codex++。";
        }
        catch (Exception ex)
        {
            ShowError($"无法读取本地聊天简版：{ex.Message}");
        }
        finally
        {
            _openingUnifiedThread = false;
        }
    }

    private async Task ToggleUnifiedThreadArchiveAsync(UnifiedThreadRecord thread)
    {
        var sharedHome = CodexCliService.GetDefaultCodexHome();
        await RunBusyAsync(async () =>
        {
            await _codex.SetThreadArchivedAsync(thread.Id, !thread.Archived, sharedHome);
            InvalidateUnifiedHistoryCache(clearCachedData: false);
            await RefreshUnifiedHistoryAsync(force: true, _workspaceLoadGeneration);
            _statusBox.Text = thread.Archived
                ? $"已取消归档：{thread.Title}"
                : $"已归档：{thread.Title}";
        });
    }

    private async Task DeleteUnifiedThreadAsync(UnifiedThreadRecord thread)
    {
        var confirm = MessageBox.Show(
            this,
            $"将永久删除这条聊天记录及其本地会话文件：\n\n{thread.Title}\n\n此操作不可撤销，是否继续？",
            "永久删除聊天记录",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        var sharedHome = CodexCliService.GetDefaultCodexHome();
        var sourceHomes = _accounts
            .Select(account => Path.GetFullPath(account.CodexHome))
            .Where(home => !home.Equals(Path.GetFullPath(sharedHome), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        RemoveUnifiedThreadFromCachedView(thread.Id);
        _statusBox.Text = $"正在后台永久删除聊天记录：{thread.Title}";

        try
        {
            // Record the tombstone before yielding so an account switch cannot merge a legacy
            // source copy back into the shared library while the CLI deletion is still queued.
            _sharedHistory.RecordDeletedThread(sharedHome, thread.Id);
            await _unifiedThreadDeleteGate.WaitAsync();
            try
            {
                var sourceHomesWithThread = await Task.Run(() => sourceHomes
                    .Where(sourceHome => _sharedHistory.ContainsThread(sourceHome, thread.Id))
                    .ToList());

            // Remove legacy source copies first so the next account switch cannot merge the
            // deleted task back into the shared library.
                foreach (var sourceHome in sourceHomesWithThread)
                {
                    await _codex.DeleteThreadAsync(thread.Id, sourceHome);
                }

                await _codex.DeleteThreadAsync(thread.Id, sharedHome);
            }
            finally
            {
                _unifiedThreadDeleteGate.Release();
            }

            if (!_formClosed && !IsDisposed)
            {
                _statusBox.Text = $"已永久删除聊天记录：{thread.Title}";
            }
        }
        catch (Exception ex)
        {
            _sharedHistory.RemoveDeletedThreadRecord(sharedHome, thread.Id);
            InvalidateUnifiedHistoryCache(clearCachedData: false);
            await RefreshUnifiedHistoryAsync(force: true, _workspaceLoadGeneration);
            ShowError($"无法删除聊天记录：{ex.Message}");
        }
    }

    private void RemoveUnifiedThreadFromCachedView(string threadId)
    {
        if (_unifiedHistoryCache == null)
        {
            return;
        }

        var remaining = _unifiedHistoryCache
            .Where(item => !item.Id.Equals(threadId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (remaining.Count == _unifiedHistoryCache.Count)
        {
            return;
        }

        InvalidateUnifiedHistoryCache(clearCachedData: false);
        _unifiedHistoryCache = remaining;
        _unifiedHistoryCacheVersion = _unifiedHistoryInvalidationVersion;
        if (!_formClosed && !IsDisposed && _activeView == WorkspaceView.UnifiedHistory)
        {
            RenderCards();
        }
    }

    private int GetWorkspaceWidth()
    {
        // Use the outer panel width rather than ClientSize. Native AutoScroll removes the
        // scrollbar width from ClientSize only while it is visible; basing rows on that
        // value made every fold/unfold change card width. One exact system scrollbar slot
        // now stays reserved at all times, while the scrollbar itself remains native and
        // hidden when it is unnecessary.
        return CalculateStableWorkspaceWidth(
            _cardsPanel.Width,
            _cardsPanel.Padding.Horizontal,
            SystemInformation.VerticalScrollBarWidth);
    }

    private static bool UsesHorizontalAccountSwitchLayout(int width) =>
        width >= AccountSwitchHorizontalMinWidth;

    private static bool UsesHorizontalQuotaUsageLayout(int width) =>
        width >= QuotaUsageHorizontalMinWidth;

    private static QuotaUsageHorizontalGeometry CalculateQuotaUsageHorizontalGeometry(int width)
    {
        var nameWidth = Math.Clamp((int)Math.Round(width * 0.24D), 210, 480);
        var rightWidth = Math.Clamp((int)Math.Round(width * 0.26D), 234, 320);
        var middleLeft = 34 + nameWidth;
        var middleWidth = Math.Max(320, width - middleLeft - rightWidth - 52);
        var metricWidth = (middleWidth - 20) / 3;
        return new QuotaUsageHorizontalGeometry(
            nameWidth,
            rightWidth,
            middleLeft,
            middleWidth,
            metricWidth);
    }

    internal static void ValidateResponsiveAccountCardLayouts()
    {
        if (UsesHorizontalAccountSwitchLayout(AccountSwitchHorizontalMinWidth - 1) ||
            !UsesHorizontalAccountSwitchLayout(AccountSwitchHorizontalMinWidth) ||
            UsesHorizontalQuotaUsageLayout(QuotaUsageHorizontalMinWidth - 1) ||
            !UsesHorizontalQuotaUsageLayout(QuotaUsageHorizontalMinWidth))
        {
            throw new InvalidOperationException("Responsive account-card breakpoints are inconsistent.");
        }

        // 935-970px is the actual card viewport on a common 1280x768 desktop after
        // the sidebar and scrollbar gutter. It must keep the same compact horizontal
        // composition as a larger monitor instead of reverting to the old tall cards.
        foreach (var width in new[] { 935, 970 })
        {
            var geometry = CalculateQuotaUsageHorizontalGeometry(width);
            var accountInfo = new Rectangle(18, 49, geometry.NameWidth - 26, 136);
            var usageMetrics = new Rectangle(
                geometry.MiddleLeft,
                50,
                geometry.MiddleWidth,
                134);
            var quotaColumn = new Rectangle(
                width - geometry.RightWidth + 14,
                16,
                geometry.RightWidth - 40,
                168);
            var quotaActions = new Rectangle(width - 316, 186, 290, 34);

            const int compactActionTotalWidth = 210 + 180 + 74 + 86 + (10 * 3);
            var compactActionLeft = Math.Max(18, (width - compactActionTotalWidth) / 2);
            var switchName = new Rectangle(18, 4, width - 120 - 20 - 36, 30);
            var switchState = new Rectangle(width - 120 - 20, 10, 120, 36);
            var switchActions = new Rectangle(compactActionLeft, 62, compactActionTotalWidth, 42);
            if (!UsesHorizontalAccountSwitchLayout(width) ||
                !UsesHorizontalQuotaUsageLayout(width) ||
                geometry.NameWidth < 210 ||
                geometry.RightWidth < 234 ||
                geometry.MetricWidth < 112 ||
                geometry.MiddleLeft + geometry.MiddleWidth + geometry.RightWidth + 52 > width ||
                accountInfo.Right >= usageMetrics.Left ||
                usageMetrics.Right >= quotaColumn.Left ||
                usageMetrics.IntersectsWith(quotaActions) ||
                quotaColumn.IntersectsWith(quotaActions) ||
                switchName.IntersectsWith(switchState) ||
                switchName.IntersectsWith(switchActions) ||
                switchState.IntersectsWith(switchActions) ||
                switchActions.Bottom > 112)
            {
                throw new InvalidOperationException(
                    $"Compact account-card layout is invalid at {width}px: {geometry}.");
            }
        }
    }

    internal static void ValidateCodexAppearanceLayouts()
    {
        var managerAppearances = CodexAppearanceOptions
            .Where(option => option.Id.StartsWith("manager-", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expectedManagerModes = new HashSet<ThemeMode>
        {
            ThemeMode.Light,
            ThemeMode.PorcelainLight,
            ThemeMode.Dark,
            ThemeMode.NebulaDark
        };
        var actualManagerModes = managerAppearances
            .Where(option => option.RuntimeMode.HasValue)
            .Select(option => option.RuntimeMode!.Value)
            .ToHashSet();
        var official = CodexAppearanceOptions
            .SingleOrDefault(option => string.Equals(
                option.Id,
                "official-default",
                StringComparison.OrdinalIgnoreCase));
        if (managerAppearances.Length != 4 ||
            !actualManagerModes.SetEquals(expectedManagerModes) ||
            official == null ||
            official.PreviewAssetName != null ||
            official.StaticPreviewAssetName != null)
        {
            throw new InvalidOperationException(
                "Codex appearance catalog must contain four manager themes and one image-free official theme.");
        }

        foreach (var scale in new[] { 1F, 1.5F, 2F })
        {
            using var actionFont = new Font("Microsoft YaHei UI", 9F * scale, FontStyle.Bold);
            var detailTextWidth = TextRenderer.MeasureText(
                "查看详情",
                actionFont,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            var launchTextWidth = Math.Max(
                TextRenderer.MeasureText(
                    "启动主题 ✓",
                    actionFont,
                    Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width,
                TextRenderer.MeasureText(
                    "恢复官方外观",
                    actionFont,
                    Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
            var horizontalPadding = (int)Math.Ceiling(44 * scale);
            var detailWidth = Math.Max(112, detailTextWidth + horizontalPadding);
            var launchWidth = Math.Max(136, launchTextWidth + horizontalPadding);
            var gap = Math.Max(10, (int)Math.Round(10F * scale));
            var actionHeight = Math.Max(42, (int)Math.Ceiling(42F * scale));
            var bodyBottom = Math.Max(136, (int)Math.Ceiling(142F * scale));

            foreach (var logicalWidth in new[] { 560, 720, 819, 820, 960, 1600 })
            {
                var width = Math.Max(AccountRowMinWidth, (int)Math.Round(logicalWidth * scale));
                var geometry = CalculateCodexAppearanceRowActionGeometry(
                    width,
                    bodyBottom,
                    actionHeight,
                    detailWidth,
                    launchWidth,
                    gap);
                var rowBounds = new Rectangle(0, 0, width, geometry.RowHeight);
                if (!rowBounds.Contains(geometry.Detail) ||
                    !rowBounds.Contains(geometry.Launch) ||
                    geometry.Detail.IntersectsWith(geometry.Launch) ||
                    geometry.Detail.Width < 112 ||
                    geometry.Launch.Width < 136)
                {
                    throw new InvalidOperationException(
                        $"Codex theme-card actions overlap or clip at {logicalWidth}px/{scale:P0} DPI.");
                }
            }

            using var pickerFont = new Font("Microsoft YaHei UI", 9F * scale);
            var pickerTextWidth = ThemeOptions.Max(option => TextRenderer.MeasureText(
                option.Label,
                pickerFont,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
            var pickerAvailableWidth = Math.Max(1, 228 - (int)Math.Ceiling(52F * scale));
            var sidebarFooterTop = (int)Math.Round((560 - 18 - 92) * scale);
            var lastNavBottom = (int)Math.Round((378 + 44) * scale);
            if (pickerTextWidth > pickerAvailableWidth || sidebarFooterTop <= lastNavBottom)
            {
                throw new InvalidOperationException(
                    $"Sidebar manager-appearance picker clips at {scale:P0} DPI.");
            }
        }

        foreach (var innerWidth in new[] { 320, 720, 939, 940, 1299, 1300, 1600 })
        {
            var preview = CalculateCodexAppearanceDetailPreviewSize(innerWidth);
            var expectedHeight = (int)Math.Round(preview.Width * 9D / 16D);
            var detailReserve = innerWidth >= 1300 ? innerWidth - preview.Width - 28 : 0;
            if (preview.Width <= 0 ||
                preview.Height <= 0 ||
                preview.Width > innerWidth ||
                Math.Abs(preview.Height - expectedHeight) > 1 ||
                (innerWidth >= 1300 && detailReserve < 360) ||
                (innerWidth is >= 1300 and <= 1600 && preview.Width < innerWidth * 0.68D))
            {
                throw new InvalidOperationException(
                    $"Codex theme-detail preview is not responsive at {innerWidth}px.");
            }
        }
    }

    internal static int CalculateStableWorkspaceWidth(
        int panelOuterWidth,
        int horizontalPadding,
        int verticalScrollbarWidth)
    {
        var safeOuterWidth = Math.Max(0, panelOuterWidth);
        var safePadding = Math.Max(0, horizontalPadding);
        var stableScrollbarSlot = Math.Max(0, verticalScrollbarWidth);
        var viewportWidth = safeOuterWidth - safePadding - stableScrollbarSlot - WorkspaceScrollbarEdgeInset;
        return Math.Max(AccountRowMinWidth, viewportWidth);
    }

    internal static void ValidateStableWorkspaceGutter()
    {
        foreach (var panelWidth in new[] { 780, 960, 1280, 1600 })
        {
            foreach (var scrollbarWidth in new[] { 14, 17, 22, 28 })
            {
                var expanded = CalculateStableWorkspaceWidth(panelWidth, 0, scrollbarWidth);
                var collapsed = CalculateStableWorkspaceWidth(panelWidth, 0, scrollbarWidth);
                if (expanded != collapsed ||
                    expanded > panelWidth - scrollbarWidth ||
                    panelWidth - expanded != scrollbarWidth + WorkspaceScrollbarEdgeInset)
                {
                    throw new InvalidOperationException(
                        $"Workspace scrollbar gutter is not stable at {panelWidth}px / {scrollbarWidth}px.");
                }
            }
        }
    }

    private int GetAccountSummaryWidth()
    {
        var viewportWidth = _cardsPanel.ClientSize.Width - _cardsPanel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4;
        return Math.Max(AccountSummaryMinWidth, viewportWidth);
    }

    private int GetAccountDetailWidth()
    {
        var viewportWidth = _detailPanel.ClientSize.Width - _detailPanel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4;
        return Math.Max(650, viewportWidth);
    }

    private Control CreateEmptyState(int width)
    {
        var panel = new RoundedPanel
        {
            Width = Math.Max(320, width),
            Height = 180,
            Radius = 18,
            BorderColor = _palette.BorderColor,
            BackColor = _palette.SurfaceColor,
            Margin = new Padding(0, 0, 20, 20),
            Padding = new Padding(24)
        };

        var title = new Label
        {
            Text = "没有匹配的账号",
            Left = 0,
            Top = 8,
            Width = 200,
            Height = 28,
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold)
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var text = new Label
        {
            Text = "你可以清空搜索条件，或者先新增一个账号。",
            Left = 0,
            Top = 48,
            Width = 320,
            Height = 22
        };
        ThemeStyler.ApplyLabel(text, _palette, true);
        panel.Controls.Add(text);

        return panel;
    }

    private void RenderThemeSettingsPanel(int width)
    {
        var libraryTitleFont = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        var librarySubtitleFont = new Font(Font.FontFamily, 8.8F);
        var librarySubtitleText =
            $"可用 Codex 主题 {CodexAppearanceOptions.Length} 套 · 可直接设为启动主题，或进入详情预览与应用";
        var libraryTitleHeight = MeasureThemeTextHeight(
            "Codex 主题库",
            libraryTitleFont,
            Math.Max(1, width - 250),
            minimumHeight: 32,
            verticalPadding: 8,
            wrap: false);
        var librarySubtitleTop = 2 + libraryTitleHeight + 2;
        var librarySubtitleHeight = MeasureThemeTextHeight(
            librarySubtitleText,
            librarySubtitleFont,
            Math.Max(1, width - 250),
            minimumHeight: 28,
            verticalPadding: 6,
            wrap: true);
        var libraryHeaderHeight = Math.Max(68, librarySubtitleTop + librarySubtitleHeight + 6);
        var libraryHeader = new Panel
        {
            Width = width,
            Height = libraryHeaderHeight,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8)
        };
        var libraryTitle = new Label
        {
            Text = "Codex 主题库",
            Left = 4,
            Top = 2,
            Width = Math.Max(220, width - 250),
            Height = libraryTitleHeight,
            Font = libraryTitleFont,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(libraryTitle, _palette);
        libraryHeader.Controls.Add(libraryTitle);
        var librarySubtitle = new Label
        {
            Text = librarySubtitleText,
            Left = 4,
            Top = librarySubtitleTop,
            Width = Math.Max(260, width - 250),
            Height = librarySubtitleHeight,
            Font = librarySubtitleFont,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true
        };
        ThemeStyler.ApplyLabel(librarySubtitle, _palette, true);
        libraryHeader.Controls.Add(librarySubtitle);
        var editCustom = MakeActionButton(
            "自定义主题",
            Math.Max(4, width - 190),
            Math.Max(4, (libraryHeaderHeight - 42) / 2),
            176,
            false);
        editCustom.Height = 42;
        editCustom.Click += (_, _) => EditCustomCodexTheme();
        libraryHeader.Controls.Add(editCustom);
        _cardsPanel.Controls.Add(libraryHeader);

        var previousAppearanceGroup = string.Empty;
        foreach (var appearance in CodexAppearanceOptions)
        {
            var appearanceGroup = GetCodexAppearanceGroupKey(appearance);
            if (!string.Equals(previousAppearanceGroup, appearanceGroup, StringComparison.Ordinal))
            {
                var groupHeader = GetCodexAppearanceGroupHeader(appearanceGroup);
                _cardsPanel.Controls.Add(CreateThemeSectionHeader(
                    groupHeader.Title,
                    groupHeader.Subtitle,
                    width));
                previousAppearanceGroup = appearanceGroup;
            }
            _cardsPanel.Controls.Add(CreateCodexAppearanceLibraryRow(appearance, width));
        }

        // Keep the final card clear of the fixed status row when the native scrollbar
        // reaches the end of the list. Without a tail, the last line can stop directly
        // against the status surface and look visually clipped even though it is scrollable.
        _cardsPanel.Controls.Add(new Panel
        {
            Width = Math.Max(1, width),
            Height = 16,
            Margin = Padding.Empty,
            BackColor = Color.Transparent,
            TabStop = false
        });

    }

    private Control CreateThemeSectionHeader(string titleText, string subtitleText, int width)
    {
        var titleFont = new Font(Font.FontFamily, 9.6F, FontStyle.Bold);
        var subtitleFont = new Font(Font.FontFamily, 8.2F);
        var innerWidth = Math.Max(180, width - 12);
        var titleHeight = MeasureThemeTextHeight(
            titleText,
            titleFont,
            innerWidth,
            minimumHeight: 28,
            verticalPadding: 6,
            wrap: false);
        var subtitleHeight = MeasureThemeTextHeight(
            subtitleText,
            subtitleFont,
            innerWidth,
            minimumHeight: 26,
            verticalPadding: 6,
            wrap: true);
        var header = new Panel
        {
            Width = width,
            Height = titleHeight + subtitleHeight + 8,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 8),
            TabStop = false
        };
        var title = new Label
        {
            Text = titleText,
            Left = 4,
            Top = 0,
            Width = innerWidth,
            Height = titleHeight,
            Font = titleFont,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true
        };
        ThemeStyler.ApplyLabel(title, _palette);
        header.Controls.Add(title);
        var subtitle = new Label
        {
            Text = subtitleText,
            Left = 4,
            Top = titleHeight,
            Width = innerWidth,
            Height = subtitleHeight,
            Font = subtitleFont,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true
        };
        ThemeStyler.ApplyLabel(subtitle, _palette, true);
        header.Controls.Add(subtitle);
        return header;
    }

    private static CodexAppearanceRowActionGeometry CalculateCodexAppearanceRowActionGeometry(
        int width,
        int bodyBottom,
        int actionHeight,
        int detailWidth = 112,
        int launchWidth = 136,
        int gap = 10)
    {
        width = Math.Max(AccountRowMinWidth, width);
        bodyBottom = Math.Max(1, bodyBottom);
        actionHeight = Math.Max(42, actionHeight);
        detailWidth = Math.Max(112, detailWidth);
        launchWidth = Math.Max(136, launchWidth);
        gap = Math.Max(8, gap);
        const int horizontalInset = 18;
        if (width < 820)
        {
            var buttonWidth = Math.Max(112, (width - (horizontalInset * 2) - gap) / 2);
            var actionTop = bodyBottom + 8;
            return new CodexAppearanceRowActionGeometry(
                new Rectangle(horizontalInset, actionTop, buttonWidth, actionHeight),
                new Rectangle(horizontalInset + buttonWidth + gap, actionTop, buttonWidth, actionHeight),
                actionTop + actionHeight + 12);
        }

        const int rightInset = 20;
        var totalWidth = detailWidth + gap + launchWidth;
        var rowHeight = Math.Max(Math.Max(148, bodyBottom), actionHeight + 24);
        var left = width - rightInset - totalWidth;
        var top = (rowHeight - actionHeight) / 2;
        return new CodexAppearanceRowActionGeometry(
            new Rectangle(left, top, detailWidth, actionHeight),
            new Rectangle(left + detailWidth + gap, top, launchWidth, actionHeight),
            rowHeight);
    }

    private Control CreateCodexAppearanceLibraryRow(CodexAppearanceOption appearance, int width)
    {
        var compact = width < 820;
        var startupSelected = IsCodexAppearanceStartupSelected(appearance);
        var isOfficial = IsOfficialCodexAppearance(appearance);
        var officialActive = isOfficial && CodexDreamSkinService.IsOfficialAppearanceActive();
        var appearanceHighlighted = startupSelected || officialActive;
        var nameFont = new Font(Font.FontFamily, 10.4F, FontStyle.Bold);
        var descriptionFont = new Font(Font.FontFamily, 8.7F);
        var detailActionWidth = MeasureActionButtonWidth("查看详情", 112);
        var launchActionWidth = Math.Max(
            MeasureActionButtonWidth("启动主题 ✓", 136),
            MeasureActionButtonWidth("恢复官方外观", 136));
        var actionGap = Math.Max(10, (int)Math.Round(10F * DeviceDpi / 96F));
        var desiredPreviewWidth = compact ? 150 : Math.Min(238, Math.Max(190, width / 5));
        var previewSize = CodexThemePreviewControl.FitSixteenByNine(desiredPreviewWidth, 10000);
        var contentLeft = 18 + previewSize.Width + (compact ? 16 : 20);
        var actionTotalWidth = detailActionWidth + actionGap + launchActionWidth;
        var actionLeft = compact ? 18 : width - actionTotalWidth - 20;
        var textWidth = compact
            ? Math.Max(180, width - contentLeft - 18)
            : Math.Max(180, actionLeft - contentLeft - 16);
        var nameHeight = MeasureThemeTextHeight(
            GetCodexAppearanceLabel(appearance),
            nameFont,
            textWidth,
            minimumHeight: 34,
            verticalPadding: 8,
            wrap: false);
        var descriptionHeight = MeasureThemeTextHeight(
            GetCodexAppearanceDescription(appearance),
            descriptionFont,
            textWidth,
            minimumHeight: compact ? 54 : 40,
            verticalPadding: 8,
            wrap: true);
        var badgeFont = new Font(Font.FontFamily, 8.5F, FontStyle.Bold);
        const int appliedBadgeWidth = 124;
        var selectedBadgeText = IsOfficialCodexAppearance(appearance) ? "官方默认" : "启动已选择";
        var modeText = GetCodexAppearanceModeText(appearance);
        const int modeBadgeWidth = 72;
        var badgeHeight = MeasureThemeTextHeight(
            modeText,
            badgeFont,
            modeBadgeWidth,
            minimumHeight: 26,
            verticalPadding: 8,
            wrap: false);
        var appliedBadgeHeight = MeasureThemeTextHeight(
            selectedBadgeText,
            badgeFont,
            appliedBadgeWidth,
            minimumHeight: 26,
            verticalPadding: 8,
            wrap: false);
        var detailHeight = Math.Max(
            42,
            MeasureThemeTextHeight(
                "查看详情",
                Font,
                compact ? Math.Max(112, (width - 46) / 2) : detailActionWidth,
                minimumHeight: 42,
                verticalPadding: 12,
                wrap: false));
        var launchButtonText = isOfficial
            ? "恢复官方外观"
            : startupSelected ? "启动主题 ✓" : "启动主题";
        var launchHeight = Math.Max(
            42,
            MeasureThemeTextHeight(
                launchButtonText,
                Font,
                compact ? Math.Max(112, (width - 46) / 2) : launchActionWidth,
                minimumHeight: 42,
                verticalPadding: 12,
                wrap: false));
        var actionHeight = Math.Max(detailHeight, launchHeight);
        var contentTop = 18;
        var descriptionTop = contentTop + nameHeight + 2;
        var contentBottom = descriptionTop + descriptionHeight;
        var badgeTop = contentBottom + 8;
        var bodyBottom = Math.Max(previewSize.Height + 24, badgeTop + badgeHeight + 12);
        var actionGeometry = CalculateCodexAppearanceRowActionGeometry(
            width,
            bodyBottom,
            actionHeight,
            detailActionWidth,
            launchActionWidth,
            actionGap);
        var height = actionGeometry.RowHeight;
        var row = new RoundedPanel
        {
            Width = width,
            Height = height,
            Radius = 12,
            BorderColor = appearanceHighlighted
                ? UiDesign.Blend(_palette.BorderColor, _palette.AccentColor, 0.58F)
                : _palette.BorderColor,
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, _palette.SecondaryAccentColor, 0.022F),
            AccentColor = appearance.Id == "custom" ? _palette.TertiaryAccentColor : _palette.AccentColor,
            AccentWidth = appearanceHighlighted ? 4 : 2,
            ShadowColor = Color.FromArgb(22, _palette.ShadowColor),
            Margin = new Padding(0, 0, 0, 12),
            Cursor = Cursors.Hand,
            AccessibleName = $"{GetCodexAppearanceLabel(appearance)}主题卡片"
        };

        previewSize = CodexThemePreviewControl.FitSixteenByNine(desiredPreviewWidth, height - 24);
        var preview = CreateCodexAppearancePreview(appearance, previewSize.Width, previewSize.Height);
        preview.SetBounds(18, Math.Max(12, (bodyBottom - previewSize.Height) / 2), previewSize.Width, previewSize.Height);
        preview.Cursor = Cursors.Hand;
        row.Controls.Add(preview);

        var name = new Label
        {
            Text = GetCodexAppearanceLabel(appearance),
            Left = contentLeft,
            Top = contentTop,
            Width = textWidth,
            Height = nameHeight,
            Font = nameFont,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(name, _palette);
        row.Controls.Add(name);

        var description = new Label
        {
            Text = GetCodexAppearanceDescription(appearance),
            Left = contentLeft,
            Top = descriptionTop,
            Width = textWidth,
            Height = descriptionHeight,
            Font = descriptionFont,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(description, _palette, true);
        row.Controls.Add(description);

        var badge = MakeBadge(
            modeText,
            contentLeft,
            badgeTop,
            Color.FromArgb(38, _palette.SecondaryAccentColor),
            _palette.SecondaryAccentColor);
        badge.Width = modeBadgeWidth;
        badge.Font = badgeFont;
        badge.Height = badgeHeight;
        row.Controls.Add(badge);
        if (appearanceHighlighted &&
            contentLeft + modeBadgeWidth + 10 + appliedBadgeWidth <= width - 18)
        {
            var applied = MakeBadge(
                selectedBadgeText,
                contentLeft + modeBadgeWidth + 10,
                badgeTop,
                Color.FromArgb(38, _palette.SuccessColor),
                _palette.SuccessColor);
            applied.Width = appliedBadgeWidth;
            applied.Font = badgeFont;
            applied.Height = appliedBadgeHeight;
            row.Controls.Add(applied);
        }

        var detail = MakeActionButton(
            "查看详情",
            actionGeometry.Detail.Left,
            actionGeometry.Detail.Top,
            actionGeometry.Detail.Width,
            false);
        detail.Height = actionGeometry.Detail.Height;
        detail.Click += (_, _) => ShowCodexAppearanceDetail(appearance.Id);
        row.Controls.Add(detail);

        var launch = MakeActionButton(
            launchButtonText,
            actionGeometry.Launch.Left,
            actionGeometry.Launch.Top,
            actionGeometry.Launch.Width,
            true);
        launch.Height = actionGeometry.Launch.Height;
        // Keep the selected startup theme actionable so it can be applied again when the
        // current Codex window has not yet picked up the saved startup preference.
        launch.Enabled = true;
        launch.AccessibleName = isOfficial
            ? "恢复 Codex 官方外观"
            : $"启动主题：{GetCodexAppearanceLabel(appearance)}";
        launch.Click += async (_, _) => await StartCodexAppearanceAsync(appearance);
        row.Controls.Add(launch);
        _toolTip.SetToolTip(
            launch,
            isOfficial
                ? "移除注入外观并恢复 Codex 官方默认界面。"
                : "设为 Codex 启动主题并立即应用；应用时会关闭并重新打开一次 Codex。");

        EventHandler open = (_, _) => ShowCodexAppearanceDetail(appearance.Id);
        row.Click += open;
        name.Click += open;
        description.Click += open;
        preview.Click += open;
        foreach (Control child in preview.Controls)
        {
            child.Cursor = Cursors.Hand;
            child.Click += open;
        }
        return row;
    }

    private Control CreateCodexAppearanceDetailPanel(int width)
    {
        var appearance = GetSelectedCodexAppearanceOption();
        var label = GetCodexAppearanceLabel(appearance);
        var descriptionText = GetCodexAppearanceDescription(appearance);
        var innerLeft = 24;
        var innerWidth = Math.Max(320, width - 48);
        var wide = innerWidth >= 1300;
        var descriptionWidth = Math.Max(220, innerWidth - 62);
        var descriptionFont = new Font(Font.FontFamily, 9F);
        var measuredDescription = TextRenderer.MeasureText(
            descriptionText,
            descriptionFont,
            new Size(descriptionWidth, 1000),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        var descriptionHeight = Math.Max(32, measuredDescription.Height + 8);
        var previewTop = 62 + descriptionHeight + 16;
        // Give the visual preview clear priority on the detail page while reserving a
        // DPI-safe information/action column. The former 60%/540px cap made the preview
        // look like another library thumbnail on large monitors.
        var previewSize = CalculateCodexAppearanceDetailPreviewSize(innerWidth);
        var previewWidth = previewSize.Width;
        var previewHeight = previewSize.Height;
        var detailLeft = wide ? innerLeft + previewWidth + 28 : innerLeft;
        var detailWidth = wide ? innerWidth - previewWidth - 28 : innerWidth;
        var detailsTop = wide ? previewTop : previewTop + previewHeight + 28;
        var panelHeight = previewTop + previewHeight + 28;
        var panel = new RoundedPanel
        {
            Width = width,
            Height = panelHeight,
            Radius = 14,
            BorderColor = _palette.BorderColor,
            BackColor = _palette.CardColor,
            AccentColor = appearance.Id == "custom" ? _palette.TertiaryAccentColor : _palette.AccentColor,
            AccentWidth = 3,
            ShadowColor = Color.FromArgb(24, _palette.ShadowColor),
            Margin = new Padding(0, 0, 0, 18)
        };

        var back = MakeBackIconButton(innerLeft, 18);
        back.Click += (_, _) => ShowCodexAppearanceLibrary();
        panel.Controls.Add(back);
        var title = new Label
        {
            Text = label,
            Left = innerLeft + 62,
            Top = 16,
            Width = Math.Max(220, innerWidth - 62),
            Height = 42,
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);
        var description = new Label
        {
            Text = descriptionText,
            Left = innerLeft + 62,
            Top = 62,
            Width = descriptionWidth,
            Height = descriptionHeight,
            Font = descriptionFont,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true
        };
        ThemeStyler.ApplyLabel(description, _palette, true);
        panel.Controls.Add(description);

        var preview = CreateCodexAppearancePreview(appearance, previewWidth, previewHeight);
        preview.SetBounds(innerLeft, previewTop, previewWidth, previewHeight);
        panel.Controls.Add(preview);

        var mode = GetCodexAppearanceModeText(appearance);
        var codeTheme = appearance.Id == "custom"
                ? _appSettings.CustomCodexTheme.CodeThemeId
                : appearance.CodeThemeId;
        var detailCursor = detailsTop;
        var sourceDetail = MakeDetailLabel(
            "来源",
            GetCodexAppearanceSourceLabel(appearance),
            detailLeft,
            detailCursor,
            detailWidth);
        panel.Controls.Add(sourceDetail);
        detailCursor = sourceDetail.Bottom + 8;
        var modeDetail = MakeDetailLabel("界面模式", mode, detailLeft, detailCursor, detailWidth);
        panel.Controls.Add(modeDetail);
        detailCursor = modeDetail.Bottom + 8;
        var codeDetail = MakeDetailLabel("代码配色", codeTheme, detailLeft, detailCursor, detailWidth);
        panel.Controls.Add(codeDetail);
        detailCursor = codeDetail.Bottom + 8;
        var statusDetail = MakeDetailLabel(
            "当前状态",
            _codex.GetCodexDreamSkinStatus(),
            detailLeft,
            detailCursor,
            detailWidth);
        panel.Controls.Add(statusDetail);
        detailCursor = statusDetail.Bottom + 14;

        Control lastAction;
        if (IsOfficialCodexAppearance(appearance))
        {
            var restoreOfficial = MakeActionButton(
                "恢复并启动 Codex 官方外观",
                detailLeft,
                detailCursor,
                detailWidth,
                true);
            restoreOfficial.Height = 46;
            restoreOfficial.Click += async (_, _) => await RestoreOfficialCodexAppearanceAsync();
            panel.Controls.Add(restoreOfficial);
            lastAction = restoreOfficial;
        }
        else
        {
            var syncEnabledForAppearance = IsCodexAppearanceStartupSelected(appearance);
            var toggle = MakeActionButton(
                syncEnabledForAppearance ? "启动同步：已开启" : "设为启动同步主题",
                detailLeft,
                detailCursor,
                detailWidth,
                syncEnabledForAppearance);
            toggle.Height = 42;
            toggle.Click += (_, _) => ToggleCodexDreamSkinPreference();
            panel.Controls.Add(toggle);

            var actionTop = toggle.Bottom + 12;
            if (appearance.Id == "custom")
            {
                var edit = MakeActionButton("编辑自定义主题", detailLeft, actionTop, detailWidth, false);
                edit.Height = 42;
                edit.Click += (_, _) => EditCustomCodexTheme();
                panel.Controls.Add(edit);
                actionTop += 54;
            }
            var apply = MakeActionButton("应用到 Codex", detailLeft, actionTop, detailWidth, true);
            apply.Height = 44;
            apply.Click += async (_, _) => await ApplyCodexDreamSkinAsync(appearance);
            panel.Controls.Add(apply);
            lastAction = apply;
        }
        panel.Height = Math.Max(preview.Bottom + 28, lastAction.Bottom + 28);
        return panel;
    }

    private static Size CalculateCodexAppearanceDetailPreviewSize(int innerWidth)
    {
        innerWidth = Math.Max(320, innerWidth);
        var wide = innerWidth >= 1300;
        var maximumWidePreviewWidth = Math.Max(520, innerWidth - 388);
        var previewWidthLimit = wide
            ? Math.Min(Math.Max(520, (int)Math.Round(innerWidth * 0.70D)), maximumWidePreviewWidth)
            : innerWidth;
        return CodexThemePreviewControl.FitSixteenByNine(previewWidthLimit, 680);
    }

    private Control CreateCodexAppearancePreview(CodexAppearanceOption appearance, int width, int height)
    {
        var isCustom = appearance.Id == "custom";
        var preview = new CodexThemePreviewControl
        {
            Width = width,
            Height = height,
            ThemeName = GetCodexAppearanceLabel(appearance),
            IsDark = isCustom ? _appSettings.CustomCodexTheme.IsDark : appearance.IsDark,
            CodeThemeId = isCustom ? _appSettings.CustomCodexTheme.CodeThemeId : appearance.CodeThemeId,
            Contrast = isCustom ? _appSettings.CustomCodexTheme.Contrast : appearance.Contrast,
            AccentColor = CustomCodexThemeDialog.ParseColor(
                isCustom ? _appSettings.CustomCodexTheme.AccentColor : appearance.AccentColor,
                _palette.AccentColor),
            SurfaceColor = CustomCodexThemeDialog.ParseColor(
                isCustom ? _appSettings.CustomCodexTheme.SurfaceColor : appearance.SurfaceColor,
                _palette.SurfaceColor),
            InkColor = CustomCodexThemeDialog.ParseColor(
                isCustom ? _appSettings.CustomCodexTheme.InkColor : appearance.InkColor,
                _palette.TextColor),
            FocusX = isCustom ? 0.5F : appearance.FocusX,
            FocusY = isCustom ? 0.5F : appearance.FocusY,
            AccessibleName = $"{GetCodexAppearanceLabel(appearance)} Codex 界面预览"
        };

        if (IsOfficialCodexAppearance(appearance))
        {
            // The official card represents the native Codex surface, not a bundled
            // wallpaper. A null image lets CodexThemePreviewControl render its neutral
            // built-in chrome instead of falling back to the midnight-aurora artwork.
            preview.SetBackgroundImage(null);
            return preview;
        }

        var imagePath = isCustom
            ? _appSettings.CustomCodexTheme.BackgroundImagePath
            : ResolveCodexAppearancePreviewImagePath(appearance);
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            imagePath = CodexDreamSkinService.GetPreviewAssetPath("preset-midnight-aurora.jpg");
        }
        var staticPreviewPath = isCustom
            ? null
            : ResolveCodexAppearanceStaticPreviewImagePath(appearance);
        if (!string.IsNullOrWhiteSpace(staticPreviewPath) && File.Exists(staticPreviewPath))
        {
            preview.SetStaticPreviewImage(staticPreviewPath);
        }
        else
        {
            preview.SetBackgroundImage(imagePath);
        }
        return preview;
    }

    private static string? ResolveCodexAppearanceStaticPreviewImagePath(CodexAppearanceOption appearance) =>
        CodexDreamSkinService.GetPreviewAssetPath(appearance.StaticPreviewAssetName);

    private static string? ResolveCodexAppearancePreviewImagePath(CodexAppearanceOption appearance)
    {
        if (!appearance.Id.StartsWith("manager-", StringComparison.OrdinalIgnoreCase))
        {
            return CodexDreamSkinService.GetPreviewAssetPath(appearance.PreviewAssetName);
        }

        var presetProbe = CodexDreamSkinService.GetPreviewAssetPath("preset-midnight-aurora.jpg");
        var presetDirectory = string.IsNullOrWhiteSpace(presetProbe)
            ? null
            : Path.GetDirectoryName(presetProbe);
        var assetRoot = string.IsNullOrWhiteSpace(presetDirectory)
            ? null
            : Directory.GetParent(presetDirectory)?.FullName;
        var managerImage = string.IsNullOrWhiteSpace(assetRoot) ||
                           string.IsNullOrWhiteSpace(appearance.PreviewAssetName)
            ? null
            : Path.Combine(assetRoot, appearance.PreviewAssetName);
        return !string.IsNullOrWhiteSpace(managerImage) && File.Exists(managerImage)
            ? managerImage
            : presetProbe;
    }

    private Control MakeDetailLabel(string caption, string value, int left, int top, int width)
    {
        var displayValue = string.IsNullOrWhiteSpace(value) ? "—" : value;
        var captionFont = new Font(Font.FontFamily, 8.4F, FontStyle.Bold);
        var valueFont = new Font(Font.FontFamily, 9F);
        var textFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        var measuredCaption = TextRenderer.MeasureText(
            caption,
            captionFont,
            new Size(Math.Max(1, width), 1000),
            textFlags);
        var measuredValue = TextRenderer.MeasureText(
            displayValue,
            valueFont,
            new Size(Math.Max(1, width), 2000),
            textFlags);
        var captionHeight = Math.Max(24, measuredCaption.Height + 8);
        var contentGap = Math.Max(4, (int)Math.Round(4F * DeviceDpi / 96F));
        var valueTop = captionHeight + contentGap;
        var valueHeight = Math.Max(30, measuredValue.Height + 8);
        var host = new Panel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = valueTop + valueHeight,
            BackColor = Color.Transparent
        };
        var captionLabel = new Label
        {
            Text = caption,
            Left = 0,
            Top = 0,
            Width = width,
            Height = captionHeight,
            Font = captionFont,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(captionLabel, _palette, true);
        host.Controls.Add(captionLabel);
        var valueLabel = new Label
        {
            Text = displayValue,
            Left = 0,
            Top = valueTop,
            Width = width,
            Height = valueHeight,
            Font = valueFont,
            AutoEllipsis = false,
            TextAlign = ContentAlignment.TopLeft,
            UseCompatibleTextRendering = true
        };
        ThemeStyler.ApplyLabel(valueLabel, _palette);
        host.Controls.Add(valueLabel);
        return host;
    }

    private string GetCodexAppearanceLabel(CodexAppearanceOption appearance) =>
        appearance.Id == "custom" ? _appSettings.CustomCodexTheme.Name : appearance.Label;

    private string GetCodexAppearanceDescription(CodexAppearanceOption appearance) =>
        appearance.Id == "custom"
            ? "选择本地照片作为真实背景，并自定义强调色、文字色、对比度与代码配色。"
            : $"{GetCodexAppearanceSourceLabel(appearance)} · {appearance.Description}";

    private static string GetCodexAppearanceSourceLabel(CodexAppearanceOption appearance) =>
        appearance.Id switch
        {
            "custom" => "用户自定义",
            "official-default" => "Codex 官方",
            _ when appearance.Id.StartsWith("manager-", StringComparison.OrdinalIgnoreCase) =>
                "Account Manager 内置",
            "preset-arina-hashimoto" or "preset-gothic-void-crusade" =>
                "GitHub 当前实测预设",
            _ => "本地精选"
        };

    private static string GetCodexAppearanceGroupKey(CodexAppearanceOption appearance) =>
        appearance switch
        {
            { Id: "custom" } => "custom",
            { Id: "official-default" } => "official",
            _ when appearance.Id.StartsWith("manager-", StringComparison.OrdinalIgnoreCase) => "manager",
            { Id: "preset-arina-hashimoto" or "preset-gothic-void-crusade" } => "github",
            _ => "local"
        };

    private static (string Title, string Subtitle) GetCodexAppearanceGroupHeader(string groupKey) =>
        groupKey switch
        {
            "manager" => (
                $"Account Manager 内置（{CodexAppearanceOptions.Count(option => option.Id.StartsWith("manager-", StringComparison.OrdinalIgnoreCase))} 套）",
                "四套管理器风格可以独立同步到 Codex。"),
            "official" => (
                "Codex 官方外观（1 套）",
                "作为独立主题恢复官方界面，不写入启动同步预设。"),
            "github" => (
                "GitHub 当前实测预设（2 套，可应用）",
                "使用上游提供的纯背景和主题包，可直接应用到 Codex。"),
            "local" => (
                $"本地精选（{CodexAppearanceOptions.Count(option => option.Id.StartsWith("preset-", StringComparison.OrdinalIgnoreCase) && option.Id is not "preset-arina-hashimoto" and not "preset-gothic-void-crusade")} 套）",
                "本地主题资源可直接应用，并与上游预设分开管理。"),
            _ => (
                "自定义主题",
                "选择本地照片和配色，创建一套可以真正应用到 Codex 的主题。")
        };

    private string GetCodexAppearanceModeText(CodexAppearanceOption appearance) =>
        appearance.Id switch
        {
            "official-default" => "官方",
            "custom" => _appSettings.CustomCodexTheme.IsDark ? "深色" : "浅色",
            _ => appearance.IsDark ? "深色" : "浅色"
        };

    private void EditCustomCodexTheme()
    {
        using var dialog = new CustomCodexThemeDialog(_appSettings.CustomCodexTheme, _palette);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        CodexDreamSkinService.SaveCustomAppearance(dialog.Theme);
        _appSettings.CustomCodexTheme = dialog.Theme;
        _themeService.SaveSettings(_appSettings);
        _selectedCodexAppearanceId = "custom";
        _showCodexAppearanceDetail = true;
        _statusBox.Text = $"已保存自定义主题：{dialog.Theme.Name}。";
        RenderCards();
        ResetCardsScrollPosition();
    }

    private static Image? LoadImageWithoutFileLock(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    private Control CreateSystemConfigPanel(int width)
    {
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 466,
            Radius = 16,
            BorderColor = _palette.BorderColor,
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, _palette.PrimaryColor, 0.025F),
            AccentColor = _palette.AccentColor,
            AccentWidth = 3,
            ShadowColor = Color.FromArgb(26, _palette.ShadowColor),
            Margin = new Padding(0, 0, CardGap, CardGap),
            Padding = new Padding(22)
        };

        var innerLeft = 24;
        var innerWidth = width - 48;
        var title = new Label
        {
            Text = "目录与代理设置",
            Left = innerLeft,
            Top = 22,
            Width = 240,
            Height = 38,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var subtitle = new Label
        {
            Text = "项目配置、Codex 启动位置与 PAT 网关上游代理。",
            Left = innerLeft,
            Top = 62,
            Width = innerWidth,
            Height = 28,
            Font = new Font(Font.FontFamily, 8.8F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(subtitle, _palette, true);
        panel.Controls.Add(subtitle);

        var rootLabel = new Label
        {
            Text = "项目根目录",
            Left = innerLeft,
            Top = 112,
            Width = 150,
            Height = 38,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            AutoEllipsis = false,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(rootLabel, _palette, true);
        panel.Controls.Add(rootLabel);

        const int configActionWidth = 196;
        var configActionLeft = width - 244;
        var rootValue = new Label
        {
            Text = _store.RootPath,
            Left = innerLeft + 164,
            Top = 112,
            Width = innerWidth - 400,
            Height = 38,
            Font = new Font(Font.FontFamily, 9F),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(rootValue, _palette);
        panel.Controls.Add(rootValue);

        var openRoot = MakeActionButton("打开根目录", configActionLeft, 104, configActionWidth, false);
        openRoot.Click += (_, _) => OpenRootFolder();
        panel.Controls.Add(openRoot);

        var launchLabel = new Label
        {
            Text = "启动目录",
            Left = innerLeft,
            Top = 172,
            Width = 150,
            Height = 42,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            AutoEllipsis = false,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(launchLabel, _palette, true);
        panel.Controls.Add(launchLabel);

        _projectPathShell!.Parent?.Controls.Remove(_projectPathShell);
        _projectPathShell.SetBounds(innerLeft + 164, 164, innerWidth - 400, 46);
        _projectPathShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _projectPathBox.Font = new Font(Font.FontFamily, 9.5F);
        ThemeStyler.ApplyInput(_projectPathBox, _palette);
        ThemeStyler.ApplyInputShell(_projectPathShell, _palette);
        panel.Controls.Add(_projectPathShell);

        var browse = MakeActionButton("选择目录", configActionLeft, 167, configActionWidth, false);
        browse.Click += (_, _) => BrowseProjectPath();
        panel.Controls.Add(browse);

        var proxyAddressLabel = new Label
        {
            Text = "代理地址",
            Left = innerLeft,
            Top = 232,
            Width = 150,
            Height = 42,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            AutoEllipsis = false,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(proxyAddressLabel, _palette, true);
        panel.Controls.Add(proxyAddressLabel);

        _patGatewayProxyAddressShell!.Parent?.Controls.Remove(_patGatewayProxyAddressShell);
        _patGatewayProxyAddressShell.SetBounds(innerLeft + 164, 226, innerWidth - 400, 46);
        _patGatewayProxyAddressShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _patGatewayProxyAddressBox.Font = new Font(Font.FontFamily, 9.5F);
        ThemeStyler.ApplyInput(_patGatewayProxyAddressBox, _palette);
        ThemeStyler.ApplyInputShell(_patGatewayProxyAddressShell, _palette);
        panel.Controls.Add(_patGatewayProxyAddressShell);

        var autoProxy = MakeActionButton("自动检测", configActionLeft, 229, configActionWidth, false);
        autoProxy.Click += async (_, _) =>
        {
            autoProxy.Enabled = false;
            try
            {
                await DetectLocalPatGatewayProxyAsync(updateStatus: true);
            }
            finally
            {
                if (!autoProxy.IsDisposed)
                {
                    autoProxy.Enabled = true;
                }
            }
        };
        panel.Controls.Add(autoProxy);

        var proxyPortLabel = new Label
        {
            Text = "代理端口",
            Left = innerLeft,
            Top = 292,
            Width = 150,
            Height = 42,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            AutoEllipsis = false,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(proxyPortLabel, _palette, true);
        panel.Controls.Add(proxyPortLabel);

        _patGatewayProxyPortShell!.Parent?.Controls.Remove(_patGatewayProxyPortShell);
        _patGatewayProxyPortShell.SetBounds(innerLeft + 164, 286, innerWidth - 400, 46);
        _patGatewayProxyPortShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _patGatewayProxyPortBox.Font = new Font(Font.FontFamily, 9.5F);
        ThemeStyler.ApplyInput(_patGatewayProxyPortBox, _palette);
        ThemeStyler.ApplyInputShell(_patGatewayProxyPortShell, _palette);
        panel.Controls.Add(_patGatewayProxyPortShell);

        _patGatewayProxyDetectionLabel.Parent?.Controls.Remove(_patGatewayProxyDetectionLabel);
        _patGatewayProxyDetectionLabel.SetBounds(configActionLeft, 286, configActionWidth, 46);
        _patGatewayProxyDetectionLabel.Font = new Font(Font.FontFamily, 8.4F, FontStyle.Bold);
        _patGatewayProxyDetectionLabel.TextAlign = ContentAlignment.MiddleCenter;
        _patGatewayProxyDetectionLabel.AutoEllipsis = true;
        _patGatewayProxyDetectionLabel.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_patGatewayProxyDetectionLabel, _palette, true);
        panel.Controls.Add(_patGatewayProxyDetectionLabel);

        var note = new Label
        {
            Text = "自动检测只探测本机回环地址；地址默认 127.0.0.1，端口会显示并回填检测结果。手动编辑任一项后使用手动设置。",
            Left = innerLeft,
            Top = 354,
            Width = innerWidth,
            Height = 70,
            Font = new Font(Font.FontFamily, 8.5F),
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(note, _palette, true);
        _toolTip.SetToolTip(
            note,
            "检测只连接 127.0.0.1 的本地监听端口并执行 HTTP 代理握手，不访问外网；本地 PAT 网关端口 8317 会被排除。");
        panel.Controls.Add(note);

        return panel;
    }

    private Control CreateAccountSwitchRow(AccountRecord account, int width)
    {
        const int cliButtonWidth = 74;
        const int detailButtonWidth = 86;
        const int actionGap = 10;
        const int wideBadgeWidth = 150;
        const int compactBadgeWidth = 120;
        var horizontal = UsesHorizontalAccountSwitchLayout(width);
        var roomyHorizontal = width >= 1280;
        var measuredCodexPlusPlusWidth = MeasureActionButtonWidth("Codex++ 启动", 210);
        var measuredCodexWidth = MeasureActionButtonWidth("Codex 启动", 180);
        var splitActionWidth = Math.Max(160, (width - 46) / 2);
        var singleRowActionsFit = width - 36 >=
                                  measuredCodexPlusPlusWidth + measuredCodexWidth +
                                  cliButtonWidth + detailButtonWidth + (actionGap * 3);
        var twoActionRows = !singleRowActionsFit;
        var codexPlusPlusButtonWidth = twoActionRows ? splitActionWidth : measuredCodexPlusPlusWidth;
        var codexButtonWidth = twoActionRows ? splitActionWidth : measuredCodexWidth;
        var actionTotalWidth = measuredCodexPlusPlusWidth + measuredCodexWidth +
                               cliButtonWidth + detailButtonWidth + (actionGap * 3);
        var row = new RoundedPanel
        {
            Width = width,
            Height = twoActionRows ? 168 : horizontal ? 112 : 118,
            Radius = 16,
            BorderColor = UiDesign.Blend(
                _palette.BorderColor,
                GetAccountStateColor(account),
                IsCurrentAccount(account) ? 0.22F : 0.10F),
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(
                _palette.CardColor,
                GetAccountStateColor(account),
                IsCurrentAccount(account) ? 0.055F : 0.025F),
            AccentColor = GetAccountStateColor(account),
            AccentWidth = IsCurrentAccount(account) ? 4 : 2,
            ShadowColor = Color.FromArgb(24, _palette.ShadowColor),
            Elevation = IsCurrentAccount(account) ? 3 : 1,
            ShowTechDecoration = IsCurrentAccount(account),
            DecorationColor = Color.FromArgb(42, _palette.SecondaryAccentColor),
            Margin = new Padding(0, 0, 0, 12),
            Cursor = Cursors.Hand
        };

        var badgeWidth = roomyHorizontal ? wideBadgeWidth : compactBadgeWidth;
        var actionLeft = horizontal
            ? roomyHorizontal
                ? width - actionTotalWidth - 18
                : Math.Max(18, (width - actionTotalWidth) / 2)
            : Math.Max(18, (width - actionTotalWidth) / 2);
        if (twoActionRows)
        {
            actionLeft = 18;
        }
        var badgeLeft = horizontal
            ? roomyHorizontal
                ? actionLeft - badgeWidth - 12
                : width - badgeWidth - 20
            : width - badgeWidth - 20;
        var nameWidth = Math.Max(220, badgeLeft - 36);

        var name = new Label
        {
            Text = account.Name,
            Left = 18,
            Top = roomyHorizontal ? 14 : horizontal ? 4 : 6,
            Width = nameWidth,
            Height = roomyHorizontal ? 38 : 30,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(name, _palette);
        _toolTip.SetToolTip(name, account.Name);
        row.Controls.Add(name);

        var kind = new Label
        {
            Text = account.AuthKindLabel,
            Left = 18,
            Top = roomyHorizontal ? 58 : horizontal ? 34 : 36,
            Width = nameWidth,
            Height = roomyHorizontal ? 32 : 24,
            Font = new Font(Font.FontFamily, 8.7F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(kind, _palette, true);
        row.Controls.Add(kind);

        var stateBadge = MakeAccountStateBadge(
            account,
            badgeLeft,
            roomyHorizontal ? 39 : 10);
        stateBadge.Width = badgeWidth;
        stateBadge.Height = 36;
        stateBadge.UseMnemonic = false;
        stateBadge.Cursor = Cursors.Hand;
        row.Controls.Add(stateBadge);

        var actionTop = roomyHorizontal ? 35 : horizontal ? 62 : 66;
        var codexPlusPlus = MakeLaunchActionButton(
            "Codex++ 启动",
            actionLeft,
            actionTop,
            codexPlusPlusButtonWidth);
        codexPlusPlus.Click += async (_, _) => await LaunchAccountAsync(account, WindowsClientMode.CodexPlusPlus);
        _toolTip.SetToolTip(codexPlusPlus, "使用 Codex++ 启动此账号");
        row.Controls.Add(codexPlusPlus);

        var codexLeft = actionLeft + codexPlusPlusButtonWidth + actionGap;
        var codex = MakeLaunchActionButton(
            "Codex 启动",
            codexLeft,
            actionTop,
            codexButtonWidth);
        codex.Click += async (_, _) => await LaunchAccountAsync(account, WindowsClientMode.OfficialCodex);
        _toolTip.SetToolTip(codex, "使用官方 Codex 启动此账号");
        row.Controls.Add(codex);

        var secondaryActionTop = twoActionRows ? actionTop + 50 : actionTop;
        var cliLeft = twoActionRows ? 18 : codexLeft + codexButtonWidth + actionGap;
        var effectiveCliWidth = twoActionRows ? splitActionWidth : cliButtonWidth;
        var cli = MakeLaunchTonalButton("CLI", cliLeft, secondaryActionTop, effectiveCliWidth);
        cli.Click += async (_, _) => await LaunchCliAccountAsync(account);
        row.Controls.Add(cli);

        var detailLeft = twoActionRows
            ? cliLeft + splitActionWidth + actionGap
            : cliLeft + cliButtonWidth + actionGap;
        var effectiveDetailWidth = twoActionRows ? splitActionWidth : detailButtonWidth;
        var detail = MakeLaunchTonalButton("详情", detailLeft, secondaryActionTop, effectiveDetailWidth);
        detail.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(detail);

        AttachAccountSelection(row, account.Name);
        return row;
    }

    private Control CreateAccountSummary(AccountRecord account, int width, bool selected)
    {
        var status = _statusCache.GetValueOrDefault(account.Name);
        var configReady = Directory.Exists(account.CodexHome) && File.Exists(Path.Combine(account.CodexHome, "config.toml"));
        var directoryReady = Directory.Exists(account.CodexHome);
        var authReady = HasUsableAccountCredential(account);
        var secretState = GetCredentialStateText(account, authReady);
        var isCurrent = IsCurrentAccount(account);

        var card = new RoundedPanel
        {
            Width = width,
            Height = AccountSummaryHeight,
            Radius = 14,
            BorderColor = selected || isCurrent ? _palette.PrimaryColor : _palette.BorderColor,
            BackColor = selected ? _palette.SurfaceAltColor : _palette.CardColor,
            AccentColor = selected || isCurrent ? _palette.AccentColor : Color.Transparent,
            AccentWidth = selected || isCurrent ? 3 : 0,
            ShadowColor = Color.FromArgb(22, _palette.ShadowColor),
            Margin = new Padding(0, 0, 0, 12),
            Cursor = Cursors.Hand
        };

        var name = new Label
        {
            Text = account.Name,
            Left = 16,
            Top = 12,
            Width = Math.Max(120, width - (isCurrent ? 210 : 150)),
            Height = 30,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(name, _palette);
        card.Controls.Add(name);

        if (isCurrent)
        {
            var currentBadge = MakeBadge("当前", width - 186, 14, Color.FromArgb(44, _palette.PrimaryColor), _palette.PrimaryColor);
            currentBadge.Width = 56;
            currentBadge.Height = 28;
            currentBadge.Cursor = Cursors.Hand;
            card.Controls.Add(currentBadge);
        }

        var badge = MakeBadge(GetStatusBadgeText(status), width - 124, 14, GetStatusBackColor(status), GetStatusForeColor(status));
        badge.Width = 100;
        badge.Height = 28;
        badge.Cursor = Cursors.Hand;
        card.Controls.Add(badge);

        var kind = new Label
        {
            Text = account.AuthKindLabel,
            Left = 16,
            Top = 48,
            Width = 210,
            Height = 24,
            Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold),
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(kind, _palette, true);
        card.Controls.Add(kind);

        var home = new Label
        {
            Text = account.CodexHome,
            Left = 232,
            Top = 48,
            Width = Math.Max(140, width - 256),
            Height = 24,
            Font = new Font(Font.FontFamily, 8.5F),
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(home, _palette, true);
        card.Controls.Add(home);

        var metrics = new Label
        {
            Text = $"配置 {(configReady ? "就绪" : "缺失")}    目录 {(directoryReady ? "存在" : "缺失")}    {secretState}",
            Left = 16,
            Top = 86,
            Width = Math.Max(160, width - 32),
            Height = 24,
            Font = new Font(Font.FontFamily, 8.5F),
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(metrics, _palette);
        card.Controls.Add(metrics);

        AttachAccountSelection(card, account.Name);
        return card;
    }

    private bool HasUsableAccountCredential(AccountRecord account)
    {
        return account.IsOfficialOAuth
            ? _codex.HasOfficialChatGptLogin(account)
            : File.Exists(Path.Combine(account.CodexHome, "auth.json"));
    }

    private string GetCredentialStateText(AccountRecord account, bool authReady)
    {
        if (account.IsOfficialOAuth)
        {
            return authReady ? "ChatGPT 已登录" : "等待 ChatGPT 登录";
        }
        if (account.IsCompatibleApi)
        {
            return authReady ? "API Key 已保存" : "API Key 缺失";
        }

        var expiry = _store.GetExpiryLabel(account.Name);
        return expiry == "Unknown" ? "Token 到期未知" : $"Token 到期：{expiry}";
    }

    private static string GetCredentialInfoLabel(AccountRecord account) => account.IsOfficialOAuth
        ? "ChatGPT 会话"
        : account.IsCompatibleApi
            ? "API 地址"
            : "Token 到期";

    private string GetCredentialInfoValue(AccountRecord account, bool authReady) => account.IsOfficialOAuth
        ? authReady ? "已登录 · 使用中自动续期" : "尚未登录"
        : account.IsCompatibleApi
            ? account.ApiBaseUrl
            : _store.GetExpiryLabel(account.Name);

    private static string GetCredentialBadgeText(AccountRecord account) => account.IsOfficialOAuth
        ? "CHATGPT"
        : account.IsCompatibleApi
            ? "API KEY"
            : "TOKEN";

    private static string GetCredentialActionText(AccountRecord account) => account.IsOfficialOAuth
        ? "通过 ChatGPT 登录"
        : account.IsCompatibleApi
            ? "编辑 API"
            : "更新 Token";

    private static string GetCredentialMetricLabel(AccountRecord account) => account.IsOfficialOAuth
        ? "ChatGPT"
        : account.IsCompatibleApi
            ? "API Key"
            : "Token";

    private string GetCredentialMetricValue(AccountRecord account, bool authReady)
    {
        if (account.IsOfficialOAuth)
        {
            return authReady ? "已登录" : "未登录";
        }
        if (account.IsCompatibleApi)
        {
            return authReady ? "已保存" : "缺失";
        }

        return _store.GetExpiryLabel(account.Name) == "Unknown" ? "未知" : "已记录";
    }

    private static string GetCredentialModelText(AccountRecord account) => account.IsOfficialOAuth
        ? "模型：Codex 官方默认"
        : account.IsCompatibleApi
            ? $"模型：{account.ApiModel} / xhigh"
            : "模型：gpt-5.6-terra / medium";

    private void AttachAccountSelection(Control control, string accountName)
    {
        if (control is Button)
        {
            return;
        }

        control.Click += (_, _) => SelectAccount(accountName);
        foreach (Control child in control.Controls)
        {
            AttachAccountSelection(child, accountName);
        }
    }

    private void SelectAccount(string accountName)
    {
        var detailView = _activeView is WorkspaceView.AccountSwitch or WorkspaceView.QuotaUsage;
        if (accountName.Equals(_selectedAccountName, StringComparison.OrdinalIgnoreCase) &&
            (!detailView || _showAccountDetail))
        {
            return;
        }

        _selectedAccountName = accountName;
        if (detailView)
        {
            _showAccountDetail = true;
        }
        RenderCards();
        ResetCardsScrollPosition();
        _statusBox.Text = _activeView switch
        {
            WorkspaceView.AccountSwitch => $"正在查看账号详情：{accountName}",
            WorkspaceView.QuotaUsage => $"正在查看额度详情：{accountName}",
            _ => $"已选中账号：{accountName}"
        };
    }

    private bool IsCurrentAccount(AccountRecord account)
    {
        return !string.IsNullOrWhiteSpace(_currentAccountName) &&
            account.Name.Equals(_currentAccountName, StringComparison.OrdinalIgnoreCase);
    }

    private AccountRecord? GetCurrentAccountRecord()
    {
        return string.IsNullOrWhiteSpace(_currentAccountName)
            ? null
            : _accounts.FirstOrDefault(account => account.Name.Equals(
                _currentAccountName,
                StringComparison.OrdinalIgnoreCase));
    }

    private void SetCurrentAccount(string? accountName, bool render = true, bool recordUsageSwitch = false)
    {
        var normalizedAccountName = string.IsNullOrWhiteSpace(accountName) ? null : accountName;
        var accountChanged = !string.Equals(
            _currentAccountName,
            normalizedAccountName,
            StringComparison.OrdinalIgnoreCase);
        if (accountChanged)
        {
            ClearWorkspaceViewCache();
        }
        _currentAccountName = normalizedAccountName;
        _appSettings.CurrentAccountName = _currentAccountName;
        _themeService.SaveSettings(_appSettings);
        if (recordUsageSwitch && accountChanged)
        {
            _usageTracker.RecordSwitch(GetCurrentAccountRecord());
            InvalidateQuotaUsageCache(clearCachedData: false);
        }

        if (render)
        {
            RenderCards();
            ResetCardsScrollPosition();
        }
    }

    private Control CreateAccountCard(AccountRecord account, int cardWidth)
    {
        var status = _statusCache.GetValueOrDefault(account.Name);
        var configReady = Directory.Exists(account.CodexHome) && File.Exists(Path.Combine(account.CodexHome, "config.toml"));
        var authReady = HasUsableAccountCredential(account);
        var directoryReady = Directory.Exists(account.CodexHome);

        var innerLeft = 22;
        var innerWidth = cardWidth - 44;
        var card = new RoundedPanel
        {
            Width = cardWidth,
            Height = 688,
            Radius = 16,
            BorderColor = _palette.BorderColor,
            BackColor = _palette.CardColor,
            AccentColor = GetAccountStateColor(account),
            AccentWidth = 4,
            ShadowColor = Color.FromArgb(28, _palette.ShadowColor),
            Margin = Padding.Empty,
            Padding = new Padding(22)
        };

        var back = MakeBackIconButton(innerLeft, 18);
        back.Click += (_, _) =>
        {
            _showAccountDetail = false;
            RenderCards();
            ResetCardsScrollPosition();
            _statusBox.Text = "已返回账号列表。";
        };
        card.Controls.Add(back);

        var name = new Label
        {
            Text = account.Name,
            Left = innerLeft + 62,
            Top = 18,
            Width = innerWidth - 78,
            Height = 40,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(name, _palette);
        card.Controls.Add(name);

        card.Controls.Add(MakeInfoRow("CODEX_HOME", account.CodexHome, innerLeft, 82, innerWidth));

        var codexHomeNote = new Label
        {
            Text = "保存凭据与配置；聊天统一存入默认 .codex。",
            Left = innerLeft,
            Top = 164,
            Width = innerWidth,
            Height = 30,
            Font = new Font(Font.FontFamily, 8.6F),
            AutoEllipsis = true,
            BackColor = Color.Transparent
        };
        ThemeStyler.ApplyLabel(codexHomeNote, _palette, true);
        card.Controls.Add(codexHomeNote);

        card.Controls.Add(MakeInfoRow("登录状态", status?.Text ?? account.AuthKindLabel, innerLeft, 212, innerWidth));
        card.Controls.Add(MakeInfoRow(
            GetCredentialInfoLabel(account),
            GetCredentialInfoValue(account, authReady),
            innerLeft,
            306,
            innerWidth));

        var metricWidth = (innerWidth - 20) / 3;
        card.Controls.Add(MakeMetric("配置", configReady ? "就绪" : "缺失", innerLeft, 420, metricWidth));
        card.Controls.Add(MakeMetric("目录", directoryReady ? "存在" : "缺失", innerLeft + metricWidth + 10, 420, metricWidth));
        card.Controls.Add(MakeMetric(
            GetCredentialMetricLabel(account),
            GetCredentialMetricValue(account, authReady),
            innerLeft + ((metricWidth + 10) * 2),
            420,
            metricWidth));

        var actionGap = 6;
        var launchActionWidth = (innerWidth - actionGap) / 2;
        var actionTop = 532;
        var codexPlusPlus = MakeLaunchActionButton(
            "Codex++ 启动",
            innerLeft,
            actionTop,
            launchActionWidth);
        codexPlusPlus.Click += async (_, _) => await LaunchAccountAsync(account, WindowsClientMode.CodexPlusPlus);
        _toolTip.SetToolTip(codexPlusPlus, "使用 Codex++ 启动此账号");
        card.Controls.Add(codexPlusPlus);

        var codex = MakeLaunchActionButton(
            "Codex 启动",
            innerLeft + launchActionWidth + actionGap,
            actionTop,
            launchActionWidth);
        codex.Click += async (_, _) => await LaunchAccountAsync(account, WindowsClientMode.OfficialCodex);
        _toolTip.SetToolTip(codex, "使用官方 Codex 启动此账号");
        card.Controls.Add(codex);

        var utilityActionTop = actionTop + 46;
        var utilityActionWidth = (innerWidth - actionGap) / 2;
        var cli = MakeActionButton(
            "CLI",
            innerLeft,
            utilityActionTop,
            utilityActionWidth,
            false);
        cli.Click += async (_, _) => await LaunchCliAccountAsync(account);
        card.Controls.Add(cli);

        var statusButton = MakeActionButton(
            "状态",
            innerLeft + utilityActionWidth + actionGap,
            utilityActionTop,
            utilityActionWidth,
            false);
        statusButton.Click += async (_, _) => await CheckStatusAsync(account);
        card.Controls.Add(statusButton);

        var secondActionTop = actionTop + 92;
        var secondaryActionWidth = (innerWidth - (actionGap * 2)) / 3;
        var token = MakeActionButton(
            account.IsOfficialOAuth ? "ChatGPT 登录" : account.IsCompatibleApi ? "API Key" : "Token",
            innerLeft,
            secondActionTop,
            secondaryActionWidth,
            false);
        token.Click += async (_, _) => await UpdateTokenAsync(account);
        card.Controls.Add(token);

        var edit = MakeActionButton("编辑", innerLeft + secondaryActionWidth + actionGap, secondActionTop, secondaryActionWidth, false);
        edit.Click += async (_, _) => await EditAccountAsync(account);
        card.Controls.Add(edit);

        var delete = MakeActionButton("删除", innerLeft + ((secondaryActionWidth + actionGap) * 2), secondActionTop, secondaryActionWidth, false);
        delete.Click += (_, _) => DeleteAccount(account);
        card.Controls.Add(delete);

        return card;
    }

    private Button MakeBackIconButton(int left, int top)
    {
        var button = new CircleIconButton
        {
            Left = left,
            Top = top,
            Width = 44,
            Height = 44,
            Cursor = Cursors.Hand,
            AccessibleName = "返回账号列表"
        };
        ApplyBackIconButtonTheme(button);
        return button;
    }

    private void ApplyBackIconButtonTheme(CircleIconButton button)
    {
        button.BaseBackColor = _palette.SurfaceAltColor;
        button.HoverBackColor = _palette.SurfaceColor;
        button.PressedBackColor = _palette.SoftButtonHoverColor;
        button.BorderColor = _palette.BorderColor;
        button.GlyphColor = _palette.PrimaryColor;
        button.Invalidate();
    }

    private static void MakeButtonCircular(Button button)
    {
        void ApplyRegion()
        {
            button.Region?.Dispose();
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, button.Width - 1, button.Height - 1);
            button.Region = new Region(path);
        }

        button.Width = Math.Max(button.Width, button.Height);
        button.Height = button.Width;
        button.FlatAppearance.BorderSize = 1;
        ApplyRegion();
        button.SizeChanged += (_, _) => ApplyRegion();
    }

    private Control CreateStatusDashboard(IReadOnlyList<AccountRecord> accounts, int cardWidth)
    {
        var rowWidth = Math.Max(620, cardWidth - SystemInformation.VerticalScrollBarWidth - 8);
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = false,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(0, 0, 8, 0),
            BackColor = _palette.FormBackColor
        };

        panel.Controls.Add(CreateDashboardHeader(
            "状态总览",
            "逐个检查账号状态。",
            rowWidth));

        foreach (var account in accounts)
        {
            panel.Controls.Add(CreateStatusRow(account, rowWidth));
        }

        return panel;
    }

    private Control CreateTokenDashboard(IReadOnlyList<AccountRecord> accounts, int cardWidth)
    {
        var rowWidth = Math.Max(620, cardWidth - SystemInformation.VerticalScrollBarWidth - 8);
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = false,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(0, 0, 8, 0),
            BackColor = _palette.FormBackColor
        };

        panel.Controls.Add(CreateDashboardHeader(
            "凭据总览",
            "各账号凭据独立保存。",
            rowWidth));

        foreach (var account in accounts)
        {
            panel.Controls.Add(CreateTokenRow(account, rowWidth));
        }

        return panel;
    }

    private Control CreateDashboardHeader(string titleText, string subtitleText, int width, string? actionText = null, Func<Task>? action = null)
    {
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 104,
            Radius = 16,
            BorderColor = _palette.BorderColor,
            BackColor = _palette.SurfaceColor,
            Margin = new Padding(0, 0, 0, 14),
            Padding = new Padding(22)
        };

        var title = new Label
        {
            Text = titleText,
            Left = 22,
            Top = 18,
            Width = action == null ? width - 44 : width - 210,
            Height = 32,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var subtitle = new Label
        {
            Text = subtitleText,
            Left = 22,
            Top = 56,
            Width = action == null ? width - 44 : width - 210,
            Height = 28,
            Font = new Font(Font.FontFamily, 8.8F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(subtitle, _palette, true);
        panel.Controls.Add(subtitle);

        if (action != null && actionText != null)
        {
            var button = MakeActionButton(actionText, width - 166, 32, 132, true);
            button.Click += async (_, _) => await action();
            panel.Controls.Add(button);
        }

        return panel;
    }

    private Control CreateStatusRow(AccountRecord account, int width)
    {
        var status = _statusCache.GetValueOrDefault(account.Name);

        var row = new RoundedPanel
        {
            Width = width,
            Height = 104,
            Radius = 16,
            BorderColor = UiDesign.Blend(_palette.BorderColor, _palette.PrimaryColor, 0.10F),
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, _palette.PrimaryColor, 0.022F),
            AccentColor = GetAccountStateColor(account),
            AccentWidth = IsCurrentAccount(account) ? 4 : 2,
            ShadowColor = Color.FromArgb(20, _palette.ShadowColor),
            Margin = new Padding(0, 0, 0, 12),
            Cursor = Cursors.Hand
        };
        row.Click += (_, _) => SelectAccount(account.Name);

        const int actionWidth = 122;
        const int badgeWidth = 124;
        const int actionGap = 12;
        const int rightMargin = 20;
        var checkLeft = width - rightMargin - actionWidth;
        var badgeLeft = checkLeft - actionGap - badgeWidth;
        var identityWidth = Math.Max(180, badgeLeft - 36);

        var name = new Label
        {
            Text = account.Name,
            Left = 18,
            Top = 14,
            Width = identityWidth,
            Height = 34,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(name, _palette);
        _toolTip.SetToolTip(name, account.Name);
        name.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(name);

        var kind = new Label
        {
            Text = account.AuthKindLabel,
            Left = 18,
            Top = 54,
            Width = identityWidth,
            Height = 28,
            Font = new Font(Font.FontFamily, 8.7F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(kind, _palette, true);
        kind.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(kind);

        var badge = MakeBadge(
            $"{(status == null ? "○" : "●")}  {GetStatusBadgeText(status)}",
            badgeLeft,
            36,
            GetStatusBackColor(status),
            GetStatusForeColor(status));
        badge.Width = badgeWidth;
        badge.Height = 32;
        badge.UseMnemonic = false;
        _toolTip.SetToolTip(
            badge,
            status == null ? "尚未检查登录状态" : status.Text);
        row.Controls.Add(badge);

        var check = MakeStatusCheckButton(checkLeft, 30, actionWidth);
        check.Click += async (_, _) => await CheckStatusAsync(account);
        row.Controls.Add(check);

        return row;
    }

    private Control CreateStatusTokenRow(AccountRecord account, int width)
    {
        var status = _statusCache.GetValueOrDefault(account.Name);
        var authReady = HasUsableAccountCredential(account);
        var stateText = GetCredentialStateText(account, authReady);
        var detailText = GetCredentialModelText(account);
        var detailTip = account.IsOfficialOAuth
            ? $"账号目录：{account.CodexHome}；由官方 Codex 自动续期 ChatGPT 登录"
            : account.IsCompatibleApi
            ? $"API 地址：{account.ApiBaseUrl}；模型：{account.ApiModel} / xhigh"
            : $"账号目录：{account.CodexHome}；模型：gpt-5.6-terra / medium";
        var geometry = CalculateStatusTokenRowGeometry(width);
        var row = new RoundedPanel
        {
            Width = width,
            Height = geometry.Height,
            Radius = 16,
            BorderColor = UiDesign.Blend(_palette.BorderColor, _palette.PrimaryColor, 0.10F),
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, _palette.PrimaryColor, 0.022F),
            AccentColor = GetAccountStateColor(account),
            AccentWidth = IsCurrentAccount(account) ? 4 : 2,
            ShadowColor = Color.FromArgb(20, _palette.ShadowColor),
            Margin = new Padding(0, 0, 0, 12),
            Cursor = Cursors.Hand,
            Name = "StatusTokenRow"
        };
        row.Click += (_, _) => SelectAccount(account.Name);

        var name = new Label
        {
            Text = account.Name,
            Bounds = geometry.Name,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(name, _palette);
        _toolTip.SetToolTip(name, account.Name);
        name.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(name);

        var authKind = new Label
        {
            Text = account.AuthKindLabel,
            Bounds = geometry.AuthKind,
            Font = new Font(Font.FontFamily, 8.7F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(authKind, _palette, true);
        authKind.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(authKind);

        var state = new Label
        {
            Text = stateText,
            Bounds = geometry.State,
            Font = new Font(Font.FontFamily, 9.2F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(state, _palette);
        state.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(state);

        var detail = new Label
        {
            Text = detailText,
            Bounds = geometry.Detail,
            Font = new Font(Font.FontFamily, 8.5F),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(detail, _palette, true);
        _toolTip.SetToolTip(detail, detailTip);
        detail.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(detail);

        var statusBadge = MakeBadge(
            $"{(status == null ? "○" : "●")}  {GetStatusBadgeText(status)}",
            geometry.StatusBadge.Left,
            geometry.StatusBadge.Top,
            GetStatusBackColor(status),
            GetStatusForeColor(status));
        statusBadge.Size = geometry.StatusBadge.Size;
        statusBadge.UseMnemonic = false;
        _toolTip.SetToolTip(statusBadge, status == null ? "尚未检查登录状态" : status.Text);
        row.Controls.Add(statusBadge);

        var tokenBadge = MakeBadge(
            GetCredentialBadgeText(account),
            geometry.TokenBadge.Left,
            geometry.TokenBadge.Top,
            authReady || account.IsAccessToken
                ? Color.FromArgb(48, _palette.SuccessColor)
                : Color.FromArgb(40, _palette.WarningColor),
            authReady || account.IsAccessToken ? _palette.SuccessColor : _palette.WarningColor);
        tokenBadge.Size = geometry.TokenBadge.Size;
        tokenBadge.UseMnemonic = false;
        row.Controls.Add(tokenBadge);

        var check = MakeStatusCheckButton(
            geometry.Check.Left,
            geometry.Check.Top,
            geometry.Check.Width);
        check.Height = geometry.Check.Height;
        check.Click += async (_, _) => await CheckStatusAsync(account);
        row.Controls.Add(check);

        var update = MakeTokenUpdateButton(
            GetCredentialActionText(account),
            geometry.Update.Left,
            geometry.Update.Top,
            geometry.Update.Width);
        update.Height = geometry.Update.Height;
        update.Click += async (_, _) => await UpdateTokenAsync(account);
        row.Controls.Add(update);

        return row;
    }

    private StatusTokenRowGeometry CalculateStatusTokenRowGeometry(int width) =>
        CalculateStatusTokenRowGeometry(width, MeasureStatusTokenActionWidth());

    private int MeasureStatusTokenActionWidth()
    {
        using var buttonFont = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        var measuredWidth = new[] { "更新 Token", "编辑 API", "通过 ChatGPT 登录" }
            .Select(text => TextRenderer.MeasureText(
                text,
                buttonFont,
                Size.Empty,
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix).Width)
            .Max();
        return CalculateStatusTokenActionWidth(measuredWidth);
    }

    private static int CalculateStatusTokenActionWidth(int measuredTextWidth) =>
        Math.Clamp(measuredTextWidth + 34, 164, 216);

    private static StatusTokenRowGeometry CalculateStatusTokenRowGeometry(
        int width,
        int measuredActionWidth)
    {
        const int side = 18;
        const int gap = 10;
        const int badgeWidth = 148;
        var actionWidth = Math.Clamp(measuredActionWidth, 164, 216);
        var badgeRowWidth = (badgeWidth * 2) + gap;
        var actionRowWidth = (actionWidth * 2) + gap;
        var rightWidth = Math.Max(badgeRowWidth, actionRowWidth);
        var minimumWideWidth = (side * 2) + 250 + 180 + (gap * 2) + rightWidth;
        var narrow = width < Math.Max(900, minimumWideWidth);
        if (narrow)
        {
            var halfWidth = Math.Max(120, (width - side * 2 - gap) / 2);
            return new StatusTokenRowGeometry(
                224,
                new Rectangle(side, 12, width - side * 2, 32),
                new Rectangle(side, 46, width - side * 2, 26),
                new Rectangle(side, 78, width - side * 2, 28),
                new Rectangle(side, 108, width - side * 2, 30),
                new Rectangle(side, 148, halfWidth, 30),
                new Rectangle(side + halfWidth + gap, 148, halfWidth, 30),
                new Rectangle(side, 184, halfWidth, 36),
                new Rectangle(side + halfWidth + gap, 184, halfWidth, 36));
        }

        var rightLeft = width - side - rightWidth;
        var badgeLeft = width - side - badgeRowWidth;
        var actionLeft = width - side - actionRowWidth;
        var middleLeft = Math.Max(250, Math.Min(340, width / 3));
        var middleWidth = Math.Max(180, rightLeft - middleLeft - gap);
        return new StatusTokenRowGeometry(
            126,
            new Rectangle(side, 14, Math.Max(180, middleLeft - side - gap), 32),
            new Rectangle(side, 52, Math.Max(180, middleLeft - side - gap), 26),
            new Rectangle(middleLeft, 18, middleWidth, 28),
            new Rectangle(middleLeft, 52, middleWidth, 30),
            new Rectangle(badgeLeft, 14, badgeWidth, 30),
            new Rectangle(badgeLeft + badgeWidth + gap, 14, badgeWidth, 30),
            new Rectangle(actionLeft, 72, actionWidth, 38),
            new Rectangle(actionLeft + actionWidth + gap, 72, actionWidth, 38));
    }

    private Control CreateTokenRow(AccountRecord account, int width)
    {
        var authReady = HasUsableAccountCredential(account);
        var stateText = GetCredentialStateText(account, authReady);
        var detailText = GetCredentialModelText(account);
        var detailTip = account.IsOfficialOAuth
            ? $"账号目录：{account.CodexHome}；由官方 Codex 自动续期 ChatGPT 登录"
            : account.IsCompatibleApi
            ? $"API 地址：{account.ApiBaseUrl}；模型：{account.ApiModel} / xhigh"
            : $"账号目录：{account.CodexHome}；模型：gpt-5.6-terra / medium";

        var geometry = CalculateTokenRowGeometry(width);
        var row = new RoundedPanel
        {
            Width = width,
            Height = geometry.Height,
            Radius = 14,
            BorderColor = _palette.BorderColor,
            BackColor = _palette.CardColor,
            AccentColor = GetAccountStateColor(account),
            AccentWidth = IsCurrentAccount(account) ? 4 : 2,
            ShadowColor = Color.FromArgb(24, _palette.ShadowColor),
            Margin = new Padding(0, 0, 0, 12),
            Cursor = Cursors.Hand
        };
        row.Click += (_, _) => SelectAccount(account.Name);

        var name = new Label
        {
            Text = account.Name,
            Bounds = geometry.Name,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(name, _palette);
        _toolTip.SetToolTip(name, account.Name);
        name.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(name);

        var authKind = new Label
        {
            Text = account.AuthKindLabel,
            Bounds = geometry.AuthKind,
            Font = new Font(Font.FontFamily, 8.7F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(authKind, _palette, true);
        authKind.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(authKind);

        var state = new Label
        {
            Text = stateText,
            Bounds = geometry.State,
            Font = new Font(Font.FontFamily, 9.2F, FontStyle.Bold),
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(state, _palette);
        state.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(state);

        var detail = new Label
        {
            Text = detailText,
            Bounds = geometry.Detail,
            Font = new Font(Font.FontFamily, 8.5F),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(detail, _palette, true);
        _toolTip.SetToolTip(detail, detailTip);
        detail.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(detail);

        var kind = MakeBadge(GetCredentialBadgeText(account), geometry.Badge.Left, geometry.Badge.Top,
            authReady || account.IsAccessToken ? Color.FromArgb(48, _palette.SuccessColor) : Color.FromArgb(40, _palette.WarningColor),
            authReady || account.IsAccessToken ? _palette.SuccessColor : _palette.WarningColor);
        kind.Size = geometry.Badge.Size;
        row.Controls.Add(kind);

        var update = MakeTokenUpdateButton(
            GetCredentialActionText(account),
            geometry.Update.Left,
            geometry.Update.Top,
            geometry.Update.Width);
        update.Height = geometry.Update.Height;
        update.Click += async (_, _) => await UpdateTokenAsync(account);
        row.Controls.Add(update);

        return row;
    }

    private static TokenRowGeometry CalculateTokenRowGeometry(int width)
    {
        const int side = 18;
        const int gap = 16;
        const int updateWidth = 196;
        const int badgeWidth = 142;
        var narrow = width < 980;
        if (narrow)
        {
            var updateLeft = width - updateWidth - 20;
            var badgeLeft = width - badgeWidth - side;
            var nameWidth = Math.Max(160, badgeLeft - side - gap);
            var infoWidth = Math.Max(160, updateLeft - side - gap);
            return new TokenRowGeometry(
                178,
                new Rectangle(side, 12, nameWidth, 34),
                new Rectangle(side, 48, nameWidth, 32),
                new Rectangle(side, 88, infoWidth, 30),
                new Rectangle(side, 122, infoWidth, 38),
                new Rectangle(badgeLeft, 28, badgeWidth, 32),
                new Rectangle(updateLeft, 112, updateWidth, 44));
        }

        var nameColumnWidth = Math.Max(300, Math.Min(440, width / 3));
        var actionLeft = width - updateWidth - 20;
        var badgeLeftWide = actionLeft - badgeWidth - gap;
        var middleLeft = 26 + nameColumnWidth;
        var middleWidth = Math.Max(180, badgeLeftWide - middleLeft - 12);
        return new TokenRowGeometry(
            112,
            new Rectangle(side, 18, nameColumnWidth - 26, 34),
            new Rectangle(side, 58, nameColumnWidth - 26, 40),
            new Rectangle(middleLeft, 20, middleWidth, 32),
            new Rectangle(middleLeft, 58, middleWidth, 40),
            new Rectangle(badgeLeftWide, 41, badgeWidth, 30),
            new Rectangle(actionLeft, 35, updateWidth, 42));
    }

    private Control CreateUsageUnassignedNotice(int width, UsageReport report)
    {
        var panel = new RoundedPanel
        {
            Width = width,
            Height = 72,
            Radius = 14,
            BorderColor = _palette.WarningColor,
            BackColor = _palette.SurfaceColor,
            Margin = new Padding(0, 0, 0, 12)
        };

        var title = new Label
        {
            Text = "未归属用量",
            Left = 18,
            Top = 12,
            Width = width - 36,
            Height = 28,
            Font = new Font(Font.FontFamily, 9.2F, FontStyle.Bold),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        var text = new Label
        {
            Text = $"今日 {FormatTokens(report.UnassignedToday.TotalTokens)} · 7天 {FormatTokens(report.UnassignedWeek.TotalTokens)} · 30天 {FormatTokens(report.UnassignedMonth.TotalTokens)}",
            Left = 18,
            Top = 40,
            Width = width - 36,
            Height = 24,
            Font = new Font(Font.FontFamily, 8.5F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(text, _palette, true);
        _toolTip.SetToolTip(text, "之后产生的本地用量会按当前账号自动归属。");
        panel.Controls.Add(text);

        return panel;
    }

    private void ApplyLiveRateLimitSnapshots(UsageReport report)
    {
        ApplyLiveRateLimitSnapshots(
            report,
            _accounts,
            _liveRateLimitCache,
            DateTimeOffset.UtcNow);
    }

    private static void ApplyLiveRateLimitSnapshots(
        UsageReport report,
        IReadOnlyList<AccountRecord> accounts,
        IReadOnlyDictionary<string, LiveRateLimitSnapshot> snapshots,
        DateTimeOffset nowUtc)
    {
        var accountKeysByName = accounts.ToDictionary(
            account => account.Name,
            QuotaAccountIdentity.CreateKey,
            StringComparer.OrdinalIgnoreCase);
        var cutoff = nowUtc.AddMinutes(-30);
        foreach (var usage in report.Accounts)
        {
            if (!accountKeysByName.TryGetValue(usage.AccountName, out var accountKey) ||
                !snapshots.TryGetValue(accountKey, out var snapshot) ||
                snapshot.ObservedAtUtc < cutoff)
            {
                continue;
            }

            ApplyLiveRateLimitSnapshot(usage, snapshot);
        }
    }

    private static void ApplyLiveRateLimitSnapshot(
        AccountUsageSummary usage,
        LiveRateLimitSnapshot snapshot)
    {
        var hasPrimarySnapshot = snapshot.UsedPercent.HasValue ||
                                 snapshot.WindowMinutes.HasValue ||
                                 snapshot.ResetsAtUtc.HasValue;
        var hasSecondarySnapshot = snapshot.SecondaryUsedPercent.HasValue ||
                                   snapshot.SecondaryWindowMinutes.HasValue ||
                                   snapshot.SecondaryResetsAtUtc.HasValue;
        var primaryApplied = hasPrimarySnapshot && !LiveQuotaWindowConflicts(
            usage,
            snapshot.ObservedAtUtc,
            snapshot.WindowMinutes,
            snapshot.ResetsAtUtc);
        var secondaryApplied = hasSecondarySnapshot && !LiveQuotaWindowConflicts(
            usage,
            snapshot.ObservedAtUtc,
            snapshot.SecondaryWindowMinutes,
            snapshot.SecondaryResetsAtUtc);
        if (primaryApplied)
        {
            usage.RateLimitUsedPercent = snapshot.UsedPercent ?? usage.RateLimitUsedPercent;
            usage.RateLimitWindowMinutes = snapshot.WindowMinutes ?? usage.RateLimitWindowMinutes;
            usage.RateLimitResetAtUtc = snapshot.ResetsAtUtc ?? usage.RateLimitResetAtUtc;
        }
        if (secondaryApplied)
        {
            usage.SecondaryRateLimitUsedPercent =
                snapshot.SecondaryUsedPercent ?? usage.SecondaryRateLimitUsedPercent;
            usage.SecondaryRateLimitWindowMinutes =
                snapshot.SecondaryWindowMinutes ?? usage.SecondaryRateLimitWindowMinutes;
            usage.SecondaryRateLimitResetAtUtc =
                snapshot.SecondaryResetsAtUtc ?? usage.SecondaryRateLimitResetAtUtc;
        }
        if (primaryApplied || secondaryApplied)
        {
            usage.RateLimitObservedAtUtc = snapshot.ObservedAtUtc;
        }

        usage.CreditBalance = snapshot.CreditBalance ?? usage.CreditBalance;
        usage.IndividualLimit = snapshot.IndividualLimit ?? usage.IndividualLimit;
        usage.PlanType = snapshot.PlanType ?? usage.PlanType;
    }

    private static bool LiveQuotaWindowConflicts(
        AccountUsageSummary usage,
        DateTimeOffset liveObservedAtUtc,
        long? liveWindowMinutes,
        DateTimeOffset? liveResetAtUtc)
    {
        if (usage.RateLimitObservedAtUtc.HasValue)
        {
            // Observation time is authoritative across reset-cycle transitions. A newer
            // official read must replace an older model-log percentage even when its reset
            // timestamp moved by days; an older cached read must never overwrite newer logs.
            return liveObservedAtUtc < usage.RateLimitObservedAtUtc.Value;
        }

        if (!liveWindowMinutes.HasValue && !liveResetAtUtc.HasValue)
        {
            return false;
        }

        var liveKind = AccountQuotaLimitType.ClassifyWindow(liveWindowMinutes);
        var currentWindow = liveKind == AccountQuotaWindowKind.Unknown
            ? null
            : usage.GetQuotaWindow(liveKind);
        var hasAnyCurrentWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour) != null ||
                                  usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly) != null ||
                                  usage.GetQuotaWindow(AccountQuotaWindowKind.Monthly) != null;
        if (currentWindow == null)
        {
            return hasAnyCurrentWindow;
        }

        return currentWindow.ResetAtUtc.HasValue &&
               liveResetAtUtc.HasValue &&
               (currentWindow.ResetAtUtc.Value - liveResetAtUtc.Value).Duration() >
               TimeSpan.FromMinutes(2);
    }

    private void UpdateQuotaLimitProfilesFromReport(UsageReport report)
    {
        var changed = false;
        foreach (var usage in report.Accounts)
        {
            var account = _accounts.FirstOrDefault(candidate =>
                candidate.Name.Equals(usage.AccountName, StringComparison.OrdinalIgnoreCase));
            if (account == null || account.IsCompatibleApi)
            {
                continue;
            }

            changed |= ApplyDetectedQuotaLimitProfile(
                account,
                usage.RateLimitWindowMinutes,
                usage.SecondaryRateLimitWindowMinutes,
                usage.RateLimitObservedAtUtc);
        }

        if (changed)
        {
            _store.SaveAccounts(_accounts);
        }
    }

    private void RefreshActivePassiveQuotaMonitoring(UsageReport report)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var account in _accounts)
        {
            var accountKey = QuotaAccountIdentity.CreateKey(account);
            var state = account.IsCompatibleApi
                ? null
                : _passiveQuotaMonitoring.GetState(account);
            if (account.IsCompatibleApi || state?.IsEnabled != true)
            {
                _passiveQuotaMonitoringInputSignatures.Remove(accountKey);
                continue;
            }

            var usage = report.Accounts.FirstOrDefault(summary =>
                summary.AccountName.Equals(account.Name, StringComparison.OrdinalIgnoreCase));
            if (usage == null)
            {
                continue;
            }

            var signature = BuildPassiveQuotaMonitoringInputSignature(state, usage);
            if (_passiveQuotaMonitoringInputSignatures.TryGetValue(accountKey, out var previousSignature) &&
                string.Equals(previousSignature, signature, StringComparison.Ordinal))
            {
                continue;
            }

            var priceProfile = GetUsagePriceProfile(account);
            // Convert every natural event with its actual model's official API price.
            // The account profile is only the fallback for legacy events without a model.
            _ = _passiveQuotaMonitoring.Analyze(
                account,
                usage,
                item => EstimateUsageEventCost(item, priceProfile),
                now);
            _passiveQuotaMonitoringInputSignatures[accountKey] = signature;
        }
    }

    private static string BuildPassiveQuotaMonitoringInputSignature(
        PassiveQuotaMonitoringState state,
        AccountUsageSummary usage)
    {
        var latestTimelineTimestamp = usage.Timeline.Count == 0
            ? DateTimeOffset.MinValue
            : usage.Timeline.Max(item => item.TimestampUtc).ToUniversalTime();
        return string.Join(
            "|",
            state.EpochId ?? string.Empty,
            state.StartedAtUtc?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ?? "",
            usage.Timeline.Count.ToString(CultureInfo.InvariantCulture),
            latestTimelineTimestamp.Ticks.ToString(CultureInfo.InvariantCulture),
            usage.Month.InputTokens.ToString(CultureInfo.InvariantCulture),
            usage.Month.CachedInputTokens.ToString(CultureInfo.InvariantCulture),
            usage.Month.CacheWriteTokens.ToString(CultureInfo.InvariantCulture),
            usage.Month.OutputTokens.ToString(CultureInfo.InvariantCulture),
            usage.Month.TotalTokens.ToString(CultureInfo.InvariantCulture),
            usage.RateLimitUsedPercent?.ToString("R", CultureInfo.InvariantCulture) ?? "",
            usage.RateLimitWindowMinutes?.ToString(CultureInfo.InvariantCulture) ?? "",
            usage.RateLimitResetAtUtc?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ?? "",
            usage.SecondaryRateLimitUsedPercent?.ToString("R", CultureInfo.InvariantCulture) ?? "",
            usage.SecondaryRateLimitWindowMinutes?.ToString(CultureInfo.InvariantCulture) ?? "",
            usage.SecondaryRateLimitResetAtUtc?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ?? "",
            usage.RateLimitObservedAtUtc?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    private static bool ApplyDetectedQuotaLimitProfile(
        AccountRecord account,
        long? primaryWindowMinutes,
        long? secondaryWindowMinutes,
        DateTimeOffset? observedAtUtc)
    {
        var detectedType = AccountQuotaLimitType.Detect(
            primaryWindowMinutes,
            secondaryWindowMinutes);
        if (detectedType == AccountQuotaLimitType.Unknown)
        {
            return false;
        }

        var incomingObservedAt = (observedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (DateTimeOffset.TryParse(account.QuotaLimitObservedAtUtc, out var storedObservedAt) &&
            incomingObservedAt < storedObservedAt.ToUniversalTime())
        {
            return false;
        }

        var incomingObservedText = incomingObservedAt.ToString("O");

        var changed = account.QuotaLimitType != detectedType ||
                      account.QuotaPrimaryWindowMinutes != primaryWindowMinutes ||
                      account.QuotaSecondaryWindowMinutes != secondaryWindowMinutes ||
                      !string.Equals(
                          account.QuotaLimitObservedAtUtc,
                          incomingObservedText,
                          StringComparison.Ordinal);
        if (!changed)
        {
            return false;
        }

        account.QuotaLimitType = detectedType;
        account.QuotaPrimaryWindowMinutes = primaryWindowMinutes;
        account.QuotaSecondaryWindowMinutes = secondaryWindowMinutes;
        account.QuotaLimitObservedAtUtc = incomingObservedText;
        return true;
    }

    private long GetAvailableResetCount(AccountRecord account)
    {
        return _resetCreditState.TryGetValue(QuotaAccountIdentity.CreateKey(account), out var state)
            ? Math.Max(0, state.Count)
            : 0;
    }

    private string GetResetButtonText(AccountRecord account)
    {
        return $"可重置 {GetAvailableResetCount(account)} 次";
    }

    private bool CanResetUsage(AccountRecord account)
    {
        return _resetCreditState.TryGetValue(QuotaAccountIdentity.CreateKey(account), out var state) &&
               state.Status == ResetCreditStatus.Known &&
               state.Count > 0;
    }

    private void SetResetCreditState(
        AccountRecord account,
        ResetCreditStatus status,
        long count = 0,
        string? error = null)
    {
        _resetCreditState[QuotaAccountIdentity.CreateKey(account)] = new ResetCreditViewState(
            status,
            Math.Max(0, count),
            DateTimeOffset.UtcNow,
            error);
    }

    private long GetQuotaRuntimeStateGeneration(string accountKey)
    {
        return _quotaRuntimeStateGenerations.TryGetValue(accountKey, out var generation)
            ? generation
            : 0L;
    }

    private bool IsQuotaRuntimeStateCurrent(string accountKey, long generation)
    {
        return GetQuotaRuntimeStateGeneration(accountKey) == generation;
    }

    private void InvalidateQuotaRuntimeState(AccountRecord account)
    {
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        _quotaRuntimeStateGenerations[accountKey] = GetQuotaRuntimeStateGeneration(accountKey) + 1L;
        _resetCreditState.Remove(accountKey);
        _liveRateLimitCache.Remove(accountKey);
        _passiveQuotaMonitoringInputSignatures.Remove(accountKey);
        _officialQuotaRefreshedAt.Remove(accountKey);
        _officialQuotaRefreshAttemptedAt.Remove(accountKey);
        _officialQuotaRefreshInProgress.Remove(accountKey);
        try
        {
            // A token edit reuses the same CODEX_HOME, so the identity key remains stable.
            // Drop the persisted response as well; otherwise the next PAT could briefly
            // inherit the previous PAT's percentage and reset-credit count after a reload.
            _quotaSnapshotStore.Remove(account);
        }
        catch
        {
            // The in-memory state is already invalidated; the next successful refresh can
            // replace an optional stale snapshot even if the cache file is temporarily locked.
        }
    }

    private void CacheUsageLimitResetInfo(AccountRecord account, UsageLimitResetInfo info)
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        SetResetCreditState(
            account,
            info.IsAvailable ? ResetCreditStatus.Known : ResetCreditStatus.Unavailable,
            info.AvailableCount ?? 0);
        try
        {
            // Keep the latest official response per credential directory so a restart does
            // not make every account appear to have the selected account's empty state.
            // The store contains no token and silently degrades if the cache is unavailable.
            _quotaSnapshotStore.Save(account, info, observedAtUtc);
        }
        catch
        {
            // Persistence is an accelerator; the in-memory snapshot remains authoritative
            // for this process and the next official refresh can rebuild the file.
        }
        if (info.Primary != null ||
            info.Secondary != null ||
            info.CreditBalance != null ||
            info.IndividualLimit != null ||
            !string.IsNullOrWhiteSpace(info.PlanType))
        {
            _liveRateLimitCache[QuotaAccountIdentity.CreateKey(account)] = new LiveRateLimitSnapshot(
                info.Primary?.UsedPercent,
                info.Primary?.WindowMinutes,
                info.Primary?.ResetsAtUtc,
                info.Secondary?.UsedPercent,
                info.Secondary?.WindowMinutes,
                info.Secondary?.ResetsAtUtc,
                info.CreditBalance,
                info.IndividualLimit,
                info.PlanType,
                observedAtUtc);

            var storedAccount = _accounts.FirstOrDefault(candidate =>
                candidate.Name.Equals(account.Name, StringComparison.OrdinalIgnoreCase));
            if (storedAccount != null &&
                ApplyDetectedQuotaLimitProfile(
                    storedAccount,
                    info.Primary?.WindowMinutes,
                    info.Secondary?.WindowMinutes,
                    observedAtUtc))
            {
                account.QuotaLimitType = storedAccount.QuotaLimitType;
                account.QuotaPrimaryWindowMinutes = storedAccount.QuotaPrimaryWindowMinutes;
                account.QuotaSecondaryWindowMinutes = storedAccount.QuotaSecondaryWindowMinutes;
                account.QuotaLimitObservedAtUtc = storedAccount.QuotaLimitObservedAtUtc;
                _store.SaveAccounts(_accounts);
            }

            // The manual read-only query is often the first place where the user sees a
            // changed official percentage.  RenderCards() used to run immediately after
            // this method while still reading the old UsageReport, so the planet could
            // show a freshly queried percentage in a dialog but the estimated remaining
            // amount stayed one refresh behind.  Merge the fresh snapshot before either
            // an in-place update or a full render consumes the cached report.
            if (_quotaUsageCache != null)
            {
                ApplyLiveRateLimitSnapshots(_quotaUsageCache);
            }
        }
    }

    private static string ResolveQuotaLimitType(AccountRecord account, AccountUsageSummary usage)
    {
        var detected = AccountQuotaLimitType.Detect(
            usage.RateLimitWindowMinutes,
            usage.SecondaryRateLimitWindowMinutes);
        if (detected != AccountQuotaLimitType.Unknown)
        {
            return detected;
        }

        var hasCurrentWindow = usage.RateLimitWindowMinutes.HasValue ||
                               usage.SecondaryRateLimitWindowMinutes.HasValue;
        return hasCurrentWindow ? AccountQuotaLimitType.Unknown : account.QuotaLimitType;
    }

    private static string GetQuotaLimitTypeLabel(string quotaLimitType)
    {
        return quotaLimitType switch
        {
            AccountQuotaLimitType.Monthly => "月额度",
            AccountQuotaLimitType.FiveHourAndWeekly => "周额度",
            AccountQuotaLimitType.WeeklyOnly => "周额度",
            AccountQuotaLimitType.FiveHourOnly => "5h额度",
            _ => "额度待识别"
        };
    }

    private static (string Caption, UsageBucket Bucket)[] GetQuotaUsageMetrics(
        AccountRecord account,
        string quotaLimitType,
        AccountUsageSummary usage)
    {
        return account.IsCompatibleApi || quotaLimitType == AccountQuotaLimitType.Monthly
            ?
            [
                ("今天", usage.Day),
                ("本周", usage.Week),
                ("本月", usage.Month)
            ]
            :
            [
                ("5h", usage.FiveHours),
                ("今天", usage.Day),
                ("本周", usage.Week)
            ];
    }

    private static (string Caption, UsageBucket Bucket)[] GetQuotaListUsageMetrics(
        AccountUsageSummary usage) =>
    [
        ("5h", usage.FiveHours),
        ("今天", usage.Day),
        ("本周", usage.Week)
    ];

    private static AccountQuotaWindowSnapshot? GetPrimaryDisplayedQuotaWindow(
        string quotaLimitType,
        AccountUsageSummary usage)
    {
        return quotaLimitType switch
        {
            AccountQuotaLimitType.FiveHourAndWeekly or AccountQuotaLimitType.FiveHourOnly =>
                usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour),
            AccountQuotaLimitType.WeeklyOnly =>
                usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly),
            AccountQuotaLimitType.Monthly =>
                usage.GetQuotaWindow(AccountQuotaWindowKind.Monthly),
            _ => null
        };
    }

    private PassiveQuotaMonitoringResult GetPassiveQuotaMonitoringResult(
        AccountRecord account,
        AccountUsageSummary usage)
    {
        var priceProfile = GetUsagePriceProfile(account);
        return _passiveQuotaMonitoring.Analyze(
            account,
            usage,
            item => EstimateUsageEventCost(item, priceProfile),
            DateTimeOffset.Now);
    }

    private Color GetPassiveQuotaStatusColor(PassiveQuotaStatus status) => status switch
    {
        PassiveQuotaStatus.Normal => _palette.SuccessColor,
        PassiveQuotaStatus.Abnormal => _palette.DangerColor,
        PassiveQuotaStatus.Indeterminate => _palette.WarningColor,
        _ => _palette.MutedTextColor
    };

    private static string GetPassiveQuotaStatusText(PassiveQuotaStatus status) => status switch
    {
        PassiveQuotaStatus.Normal => "额度正常",
        PassiveQuotaStatus.Abnormal => "额度异常",
        PassiveQuotaStatus.Indeterminate => "额度待确认",
        _ => "数据收集中"
    };

    // The API reports whole percentages. Treat a value that is rendered as 0.0%
    // as exhausted so an old capacity estimate can never be mistaken for an
    // official quota fault after the active window has actually run out.
    private static bool IsOfficialQuotaExhausted(double? remainingPercent) =>
        remainingPercent is { } value && value <= 0.05D;

    private static string GetPassiveQuotaPresentationText(
        PassiveQuotaStatus status,
        double? officialRemainingPercent) =>
        IsOfficialQuotaExhausted(officialRemainingPercent)
            ? "额度已用尽"
            : GetPassiveQuotaStatusText(status);

    private Color GetPassiveQuotaPresentationColor(
        PassiveQuotaStatus status,
        double? officialRemainingPercent) =>
        IsOfficialQuotaExhausted(officialRemainingPercent)
            ? _palette.DangerColor
            : GetPassiveQuotaStatusColor(status);

    private static string GetPassiveQuotaPresentationToolTip(
        PassiveQuotaMonitoringResult monitoring,
        double? officialRemainingPercent)
    {
        if (!IsOfficialQuotaExhausted(officialRemainingPercent))
        {
            return monitoring.Estimate == null
                ? monitoring.Message
                : monitoring.Message + Environment.NewLine +
                  GetPassiveQuotaEstimateSummary(monitoring, officialRemainingPercent);
        }

        var estimateNote = monitoring.Estimate?.Status == PassiveQuotaStatus.Abnormal
            ? "被动容量推测存在历史偏差：本地模型等值消耗低于参考阈值。这只反映估算与官方百分比之间的差异，不代表官方额度数据异常。"
            : "被动容量推测仅作辅助参考，不是官方美元余额。";
        return "官方主额度窗口当前显示 0%，本窗口额度已用尽。" +
               Environment.NewLine +
               estimateNote;
    }

    private static double? GetDisplayedQuotaCapacityUsd(PassiveQuotaMonitoringResult monitoring)
    {
        var capacity = monitoring.State.DisplayCapacityUsd ?? monitoring.Estimate?.EstimatedTotalUsd;
        return capacity is { } value && double.IsFinite(value) && value > 0D
            ? value
            : null;
    }

    private static double? GetDisplayedQuotaRemainingUsd(
        PassiveQuotaMonitoringResult monitoring,
        double? latestOfficialRemainingPercent)
    {
        var estimate = monitoring.Estimate;
        var effectiveRemainingPercent = monitoring.IsEnabled
            ? latestOfficialRemainingPercent ?? estimate?.LatestRemainingPercent
            : estimate?.LatestRemainingPercent ?? latestOfficialRemainingPercent;
        return PassiveQuotaMonitoringService.ProjectDisplayedRemainingUsd(
            GetDisplayedQuotaCapacityUsd(monitoring),
            effectiveRemainingPercent,
            estimate?.EstimatedRemainingUsd);
    }

    private static string GetPassiveQuotaSummaryText(
        PassiveQuotaMonitoringResult monitoring,
        double? latestOfficialRemainingPercent)
    {
        var capacity = GetDisplayedQuotaCapacityUsd(monitoring);
        var remaining = GetDisplayedQuotaRemainingUsd(monitoring, latestOfficialRemainingPercent);
        if (capacity is not { } totalUsd ||
            remaining is not { } remainingUsd ||
            !double.IsFinite(totalUsd) ||
            !double.IsFinite(remainingUsd) ||
            totalUsd <= 0D)
        {
            return "推测剩余  — / —";
        }

        return $"推测剩余  {FormatUsd(remainingUsd)} / {FormatUsd(totalUsd)}";
    }

    private static string GetPassiveQuotaEstimateSummary(
        PassiveQuotaMonitoringResult monitoring,
        double? latestOfficialRemainingPercent = null)
    {
        var estimate = monitoring.Estimate;
        if (estimate == null)
        {
            return monitoring.Message;
        }

        var displayCapacityUsd = GetDisplayedQuotaCapacityUsd(monitoring);
        if (!displayCapacityUsd.HasValue)
        {
            return estimate.Reason;
        }

        return $"{GetPassiveQuotaSummaryText(monitoring, latestOfficialRemainingPercent)}；" +
               $"本轮容量单点估算 {FormatUsd(estimate.EstimatedTotalUsd ?? displayCapacityUsd.Value)}。{estimate.Reason}";
    }

    private void UpdatePassiveQuotaStatus(
        PillLabel? badge,
        Label? summary,
        PassiveQuotaMonitoringResult monitoring,
        double? latestOfficialRemainingPercent)
    {
        if (badge == null)
        {
            if (summary != null)
            {
                summary.Visible = false;
            }
            return;
        }

        void FitBadgeToContent()
        {
            var measuredWidth = TextRenderer.MeasureText(
                badge.Text,
                badge.Font,
                Size.Empty,
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix).Width + 28;
            var maximumWidth = badge.MaximumSize.Width > 0
                ? badge.MaximumSize.Width
                : 270;
            badge.Width = Math.Clamp(measuredWidth, Math.Min(124, maximumWidth), maximumWidth);
        }

        var estimate = monitoring.Estimate;
        var hasStoppedEpoch = monitoring.State.StartedAtUtc.HasValue &&
                              monitoring.State.StoppedAtUtc.HasValue;
        var visible = monitoring.IsEnabled || (hasStoppedEpoch && estimate != null);
        badge.Visible = visible;
        if (summary != null)
        {
            summary.Visible = visible;
        }
        if (!visible)
        {
            if (summary != null)
            {
                summary.Text = string.Empty;
                _toolTip.SetToolTip(summary, null);
            }
            return;
        }

        const double requiredPercentSpan = 2D;
        if (IsOfficialQuotaExhausted(latestOfficialRemainingPercent))
        {
            UpdateQuotaPill(badge, "额度已用尽", _palette.DangerColor);
            FitBadgeToContent();
            if (summary != null)
            {
                summary.Text = string.Empty;
                summary.Visible = false;
                _toolTip.SetToolTip(summary, null);
            }
            _toolTip.SetToolTip(
                badge,
                GetPassiveQuotaPresentationToolTip(monitoring, latestOfficialRemainingPercent));
            return;
        }

        if (monitoring.IsEnabled &&
            (estimate == null || estimate.Status == PassiveQuotaStatus.Collecting))
        {
            var observedSpan = Math.Clamp(estimate?.ObservedPercentSpan ?? 0D, 0D, requiredPercentSpan);
            UpdateQuotaPill(badge, "监测中", _palette.PrimaryColor);
            FitBadgeToContent();
            if (summary != null)
            {
                summary.Text = $"首次估算 {observedSpan:0.#}/{requiredPercentSpan:0}%";
                summary.ForeColor = _palette.PrimaryColor;
                summary.Visible = true;
                _toolTip.SetToolTip(summary, summary.Text);
            }
            _toolTip.SetToolTip(
                badge,
                $"正在只读自然使用日志；当前滑动窗口已跨越 {observedSpan:0.#}%，达到 2% 后显示结果，之后会自动更新。" +
                (estimate == null ? string.Empty : Environment.NewLine + estimate.Reason));
            return;
        }

        var status = estimate?.Status ?? PassiveQuotaStatus.Collecting;
        var color = GetPassiveQuotaStatusColor(status);
        var frozenPrefix = monitoring.IsEnabled ? string.Empty : "上一轮";
        UpdateQuotaPill(
            badge,
            $"{frozenPrefix}{GetPassiveQuotaStatusText(status)}",
            color);
        FitBadgeToContent();
        if (summary != null)
        {
            if (GetDisplayedQuotaCapacityUsd(monitoring) is { } totalUsd)
            {
                summary.Text = GetPassiveQuotaSummaryText(
                    monitoring,
                    latestOfficialRemainingPercent);
                _toolTip.SetToolTip(
                    summary,
                    summary.Text +
                    Environment.NewLine +
                    "前项按“容量参考 × 当前官方剩余百分比”自动换算；容量参考只在新的完整 2% 自然使用校准窗口完成后更新，避免整数百分比的单点波动造成容量抖动。" +
                    Environment.NewLine +
                    "容量口径：每条用量按 sub2api 实际账单的基础价格档换算为 API 等值，不启用 >272K 长上下文加价；缓存写入未上报时按普通输入价作基础估算，不是官方美元余额。");
            }
            else
            {
                summary.Text = "数据不足";
            }

            summary.ForeColor = color;
            summary.Visible = true;
            if (estimate?.EstimatedTotalUsd is not { })
            {
                _toolTip.SetToolTip(summary, summary.Text);
            }
        }
        _toolTip.SetToolTip(
            badge,
            GetPassiveQuotaPresentationToolTip(monitoring, latestOfficialRemainingPercent));
    }

    private static string FormatRemainingValue(double? remainingPercent) =>
        remainingPercent.HasValue ? $"{remainingPercent.Value:0.#}%" : "待查询";

    private static string FormatQuotaRemaining(
        AccountQuotaWindowSnapshot? window,
        string windowLabel)
    {
        var remainingPercent = window?.RemainingPercent;
        return remainingPercent.HasValue
            ? $"{windowLabel} {remainingPercent.Value:0.#}%"
            : $"{windowLabel} 待查询";
    }

    private static string GetOfficialQuotaToolTip(
        AccountQuotaWindowSnapshot? window,
        string windowLabel)
    {
        var remainingPercent = window?.RemainingPercent;
        var resetAtUtc = window?.ResetAtUtc;
        var remainingText = remainingPercent.HasValue
            ? $"剩余 {remainingPercent.Value:0.#}%"
            : "剩余百分比待查询";
        var resetText = resetAtUtc.HasValue
            ? $"下次重置 {resetAtUtc.Value.ToLocalTime():MM-dd HH:mm}"
            : "重置时间待查询";
        return $"{windowLabel}官方额度：{remainingText}；{resetText}。只读查询不会调用模型。";
    }
    private static string? GetOfficialFinancialSummary(AccountUsageSummary usage)
    {
        var parts = new List<string>();
        if (usage.CreditBalance is { Unlimited: true })
        {
            parts.Add("官方 Credits 不限");
        }
        else if (usage.CreditBalance is { HasCredits: true, Balance: { Length: > 0 } balance })
        {
            parts.Add($"官方 Credits {balance}");
        }

        if (usage.IndividualLimit is { } individual)
        {
            if (!string.IsNullOrWhiteSpace(individual.Used) && !string.IsNullOrWhiteSpace(individual.Limit))
            {
                parts.Add($"个人限额 {individual.Used}/{individual.Limit}");
            }
            if (individual.RemainingPercent.HasValue)
            {
                parts.Add($"个人剩余 {individual.RemainingPercent.Value:0.#}%");
            }
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string GetQuotaResetSummary(string quotaLimitType, AccountUsageSummary usage)
    {
        static string Time(DateTimeOffset? value, string format) =>
            value.HasValue ? value.Value.ToLocalTime().ToString(format) : "待查询";

        var fiveHour = usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour);
        var weekly = usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly);
        var monthly = usage.GetQuotaWindow(AccountQuotaWindowKind.Monthly);

        return quotaLimitType switch
        {
            AccountQuotaLimitType.Monthly =>
                $"月重置：{Time(monthly?.ResetAtUtc, "MM-dd HH:mm")}",
            AccountQuotaLimitType.FiveHourAndWeekly =>
                $"5h重置：{Time(fiveHour?.ResetAtUtc, "MM-dd HH:mm")} · 周重置：{Time(weekly?.ResetAtUtc, "MM-dd HH:mm")}",
            AccountQuotaLimitType.WeeklyOnly =>
                $"周重置：{Time(weekly?.ResetAtUtc, "MM-dd HH:mm")}",
            AccountQuotaLimitType.FiveHourOnly =>
                $"5h重置：{Time(fiveHour?.ResetAtUtc, "MM-dd HH:mm")} · 无周限额",
            _ => "使用该账号后自动识别额度类型"
        };
    }

    private static string GetQuotaListResetSummary(string quotaLimitType, AccountUsageSummary usage)
    {
        var summary = GetQuotaResetSummary(quotaLimitType, usage);
        return AccountQuotaLimitType.UsesTwoDetailLines(quotaLimitType)
            ? summary.Replace(" · ", Environment.NewLine, StringComparison.Ordinal)
            : summary;
    }

    private static string GetQuotaRowDetailText(
        bool isCompatibleApi,
        string quotaLimitType,
        AccountUsageSummary usage)
    {
        if (isCompatibleApi)
        {
            return "本地估算";
        }

        return quotaLimitType is AccountQuotaLimitType.FiveHourAndWeekly or
            AccountQuotaLimitType.WeeklyOnly or
            AccountQuotaLimitType.FiveHourOnly or
            AccountQuotaLimitType.Monthly
            ? GetQuotaListResetSummary(quotaLimitType, usage)
            : "使用该账号后自动识别额度类型";
    }

    private static (string Primary, string? Secondary) GetQuotaRowDetailLines(
        bool isCompatibleApi,
        string quotaLimitType,
        AccountUsageSummary usage)
    {
        var text = GetQuotaRowDetailText(isCompatibleApi, quotaLimitType, usage);
        var separator = text.IndexOf(Environment.NewLine, StringComparison.Ordinal);
        return separator < 0
            ? (text, null)
            : (text[..separator], text[(separator + Environment.NewLine.Length)..]);
    }

    private static Size MeasureQuotaResetText(Font font) =>
        TextRenderer.MeasureText(
            "周重置：12-31 23:59",
            font,
            Size.Empty,
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix);

    private static int CenterQuotaRowContent(int rowHeight, int contentHeight) =>
        Math.Max(16, (rowHeight - contentHeight) / 2);

    private Control CreateQuotaUsageRow(AccountRecord account, AccountUsageSummary usage, int width)
    {
        var quotaLimitType = ResolveQuotaLimitType(account, usage);
        var hasTwoQuotaWindows = AccountQuotaLimitType.HasTwoOfficialWindows(quotaLimitType);
        var hasTwoQuotaSlots = hasTwoQuotaWindows || quotaLimitType == AccountQuotaLimitType.WeeklyOnly;
        var hasTwoDetailLines = AccountQuotaLimitType.UsesTwoDetailLines(quotaLimitType);
        var priceProfile = GetUsagePriceProfile(account);
        var monitoring = account.IsCompatibleApi
            ? null
            : GetPassiveQuotaMonitoringResult(account, usage);
        var showsCapacity = monitoring != null &&
            (monitoring.IsEnabled ||
             (monitoring.State.StartedAtUtc.HasValue &&
              monitoring.State.StoppedAtUtc.HasValue &&
              monitoring.Estimate != null));
        // The previous 1000/1120px breakpoints made a common 1280x768 desktop
        // (about 935-970px of actual workspace after the sidebar) use the tall
        // stacked card.  That produced hundreds of pixels of empty space compared
        // with the same page on a wider monitor.  The horizontal composition still
        // has enough room at 900px, so reserve stacking for genuinely narrow windows.
        var stacked = !UsesHorizontalQuotaUsageLayout(width);
        var compact = stacked;
        using var resetDetailMeasureFont = new Font(Font.FontFamily, 8F);
        var resetLineHeight = Math.Max(34, MeasureQuotaResetText(resetDetailMeasureFont).Height + 12);
        const int resetLineGap = 2;
        const int resetToActionGap = 10;
        const int cardBottomPadding = 14;
        var resetAreaTop = stacked ? 348 : 106;
        var resetAreaHeight = (resetLineHeight * 2) + resetLineGap;
        var actionHeight = stacked ? 36 : 34;
        var actionTop = resetAreaTop + resetAreaHeight + resetToActionGap;
        // Every account in the same layout mode gets the same measured height. A
        // single monthly/API detail is centered in the two-line reset area, while
        // weekly accounts use both independent lines.
        var rowHeight = actionTop + actionHeight + cardBottomPadding;
        var row = new RoundedPanel
        {
            Width = width,
            Height = rowHeight,
            Radius = 15,
            BorderColor = UiDesign.Blend(
                _palette.BorderColor,
                GetAccountStateColor(account),
                IsCurrentAccount(account) ? 0.18F : 0.08F),
            BackColor = _palette.CardColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(
                _palette.CardColor,
                IsCurrentAccount(account) ? _palette.SecondaryAccentColor : _palette.PrimaryColor,
                IsCurrentAccount(account) ? 0.045F : 0.025F),
            AccentColor = GetAccountStateColor(account),
            AccentWidth = IsCurrentAccount(account) ? 4 : 1,
            ShadowColor = Color.FromArgb(26, _palette.ShadowColor),
            Elevation = IsCurrentAccount(account) ? 3 : 2,
            // These cards already contain meaningful quota progress bars. The
            // decorative circuit lines previously crossed reset labels and made
            // them look like duplicated data separators.
            ShowTechDecoration = false,
            DecorationColor = Color.FromArgb(36, _palette.SecondaryAccentColor),
            Margin = new Padding(0, 0, 0, 12),
            Cursor = Cursors.Hand
        };
        row.Click += (_, _) => SelectAccount(account.Name);

        var horizontalGeometry = stacked
            ? default
            : CalculateQuotaUsageHorizontalGeometry(width);
        var nameWidth = stacked ? width - 36 : horizontalGeometry.NameWidth;
        var rightWidth = compact ? width : horizontalGeometry.RightWidth;
        var effectiveRightWidth = stacked ? width : rightWidth;
        var middleLeft = stacked ? 18 : horizontalGeometry.MiddleLeft;
        var middleWidth = stacked ? width - 36 : horizontalGeometry.MiddleWidth;
        var metricWidth = stacked ? (middleWidth - 20) / 3 : horizontalGeometry.MetricWidth;
        var accountInfoHeight = showsCapacity ? 136 : 68;
        const int usageMetricHeight = 134;
        var infoTop = stacked
            ? 12
            : CenterQuotaRowContent(rowHeight, accountInfoHeight);
        var metricTop = stacked
            ? 154
            : CenterQuotaRowContent(rowHeight, usageMetricHeight);

        var name = new Label
        {
            Text = account.Name,
            Left = 18,
            Top = infoTop,
            Width = nameWidth - 26,
            Height = 38,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(name, _palette);
        name.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(name);

        var kindText = account.IsCompatibleApi
            ? "兼容 API"
            : GetQuotaLimitTypeLabel(quotaLimitType);
        var officialFinancial = account.IsCompatibleApi ? null : GetOfficialFinancialSummary(usage);
        var kind = new Label
        {
            Text = string.IsNullOrWhiteSpace(officialFinancial)
                ? kindText
                : $"{kindText} · {officialFinancial}",
            Left = 18,
            Top = infoTop + 38,
            Width = nameWidth - 26,
            Height = 30,
            Font = new Font(Font.FontFamily, 8.7F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        ThemeStyler.ApplyLabel(kind, _palette, true);
        _toolTip.SetToolTip(kind, kind.Text);
        kind.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(kind);

        PillLabel? capacityStatus = null;
        Label? capacitySummary = null;
        if (!account.IsCompatibleApi)
        {
            var estimate = monitoring!.Estimate;
            var status = estimate?.Status ?? PassiveQuotaStatus.Collecting;
            var primaryRemainingPercent = GetPrimaryDisplayedQuotaWindow(quotaLimitType, usage)?.RemainingPercent;
            capacityStatus = MakeBadge(
                GetPassiveQuotaPresentationText(status, primaryRemainingPercent),
                18,
                infoTop + 72,
                Color.FromArgb(36, GetPassiveQuotaPresentationColor(status, primaryRemainingPercent)),
                GetPassiveQuotaPresentationColor(status, primaryRemainingPercent));
            var capacityStatusMaxWidth = Math.Min(220, Math.Max(132, nameWidth - 26));
            capacityStatus.Width = capacityStatusMaxWidth;
            capacityStatus.MaximumSize = new Size(capacityStatusMaxWidth, 0);
            capacityStatus.Height = 30;
            capacityStatus.Font = new Font(Font.FontFamily, 8F, FontStyle.Bold);
            capacityStatus.UseMnemonic = false;
            row.Controls.Add(capacityStatus);

            capacitySummary = new Label
            {
                Left = 18,
                Top = infoTop + 104,
                Width = nameWidth - 26,
                Height = 32,
                Font = new Font(Font.FontFamily, 8F, FontStyle.Bold),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                UseCompatibleTextRendering = true,
                UseMnemonic = false,
                Cursor = Cursors.Hand
            };
            ThemeStyler.ApplyLabel(capacitySummary, _palette, true);
            capacitySummary.Click += (_, _) => SelectAccount(account.Name);
            row.Controls.Add(capacitySummary);
            UpdatePassiveQuotaStatus(
                capacityStatus,
                capacitySummary,
                monitoring,
                primaryRemainingPercent);
        }
        var metrics = GetQuotaListUsageMetrics(usage);
        row.Controls.Add(MakeUsageMetric(metrics[0].Caption, metrics[0].Bucket, priceProfile, middleLeft, metricTop, metricWidth, out var firstMetric));
        row.Controls.Add(MakeUsageMetric(metrics[1].Caption, metrics[1].Bucket, priceProfile, middleLeft + metricWidth + 10, metricTop, metricWidth, out var secondMetric));
        row.Controls.Add(MakeUsageMetric(metrics[2].Caption, metrics[2].Bucket, priceProfile, middleLeft + ((metricWidth + 10) * 2), metricTop, metricWidth, out var thirdMetric));

        var quotaLeft = stacked ? 18 : width - effectiveRightWidth + 14;
        var quotaControlWidth = stacked
            ? hasTwoQuotaSlots
                ? Math.Max(180, (width - 54) / 2)
                : width - 36
            : effectiveRightWidth - 40;
        PillLabel? primaryQuota = null;
        PillLabel? secondaryQuota = null;
        QuotaProgressBar? primaryProgress = null;
        QuotaProgressBar? secondaryProgress = null;
        var quotaContentBottom = 0;
        var singleQuotaTop = stacked ? 294 : 22;
        var dualQuotaTop = stacked ? 294 : 16;
        if (account.IsCompatibleApi)
        {
            var apiQuota = MakeBadge(
                $"本月 {FormatEstimatedCost(usage.Month, priceProfile)}",
                quotaLeft,
                singleQuotaTop,
                Color.FromArgb(44, _palette.PrimaryColor),
                _palette.PrimaryColor);
            apiQuota.Width = quotaControlWidth;
            apiQuota.Height = stacked ? 36 : 34;
            row.Controls.Add(apiQuota);
            primaryQuota = apiQuota;
            quotaContentBottom = apiQuota.Bottom;
        }
        else if (quotaLimitType == AccountQuotaLimitType.FiveHourAndWeekly)
        {
            var fiveHourWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour);
            var weeklyWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly);
            var fiveHourColor = GetQuotaColor(fiveHourWindow?.RemainingPercent);
            var fiveHour = MakeBadge(
                FormatQuotaRemaining(fiveHourWindow, "5h"),
                quotaLeft,
                dualQuotaTop,
                Color.FromArgb(44, fiveHourColor),
                fiveHourColor);
            fiveHour.Width = quotaControlWidth;
            fiveHour.Height = stacked ? 32 : 30;
            row.Controls.Add(fiveHour);
            _toolTip.SetToolTip(fiveHour, GetOfficialQuotaToolTip(fiveHourWindow, "5h"));
            primaryQuota = fiveHour;
            primaryProgress = MakeQuotaProgressBar(
                fiveHourWindow?.RemainingPercent,
                quotaLeft + 8,
                stacked ? 332 : 50,
                quotaControlWidth - 16,
                fiveHourColor);
            row.Controls.Add(primaryProgress);
            quotaContentBottom = Math.Max(fiveHour.Bottom, primaryProgress.Bottom);

            var weeklyColor = GetQuotaColor(weeklyWindow?.RemainingPercent);
            var weekly = MakeBadge(
                FormatQuotaRemaining(weeklyWindow, "周"),
                stacked ? quotaLeft + quotaControlWidth + 18 : quotaLeft,
                stacked ? dualQuotaTop : 58,
                Color.FromArgb(44, weeklyColor),
                weeklyColor);
            weekly.Width = quotaControlWidth;
            weekly.Height = stacked ? 32 : 30;
            row.Controls.Add(weekly);
            _toolTip.SetToolTip(weekly, GetOfficialQuotaToolTip(weeklyWindow, "周"));
            secondaryQuota = weekly;
            secondaryProgress = MakeQuotaProgressBar(
                weeklyWindow?.RemainingPercent,
                (stacked ? quotaLeft + quotaControlWidth + 18 : quotaLeft) + 8,
                stacked ? 332 : 92,
                quotaControlWidth - 16,
                weeklyColor);
            row.Controls.Add(secondaryProgress);
            quotaContentBottom = Math.Max(quotaContentBottom, Math.Max(weekly.Bottom, secondaryProgress.Bottom));
        }
        else if (quotaLimitType == AccountQuotaLimitType.WeeklyOnly)
        {
            var weeklyWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly);
            var weeklyColor = GetQuotaColor(weeklyWindow?.RemainingPercent);
            var weekly = MakeBadge(
                FormatQuotaRemaining(weeklyWindow, "周"),
                quotaLeft,
                dualQuotaTop,
                Color.FromArgb(44, weeklyColor),
                weeklyColor);
            weekly.Width = quotaControlWidth;
            weekly.Height = stacked ? 32 : 30;
            row.Controls.Add(weekly);
            _toolTip.SetToolTip(weekly, GetOfficialQuotaToolTip(weeklyWindow, "周"));
            primaryQuota = weekly;
            primaryProgress = MakeQuotaProgressBar(
                weeklyWindow?.RemainingPercent,
                quotaLeft + 8,
                stacked ? 332 : 50,
                quotaControlWidth - 16,
                weeklyColor);
            row.Controls.Add(primaryProgress);

            var noFiveHourLimitLeft = stacked
                ? quotaLeft + quotaControlWidth + 18
                : quotaLeft;
            var noFiveHourLimit = MakeBadge(
                "无 5h 限额",
                noFiveHourLimitLeft,
                stacked ? dualQuotaTop : 58,
                Color.FromArgb(28, _palette.MutedTextColor),
                _palette.MutedTextColor);
            noFiveHourLimit.Name = "QuotaAvailabilitySecondary";
            noFiveHourLimit.Width = quotaControlWidth;
            noFiveHourLimit.Height = stacked ? 32 : 30;
            row.Controls.Add(noFiveHourLimit);
            _toolTip.SetToolTip(noFiveHourLimit, "官方当前未返回 5h 额度窗口。");
            secondaryQuota = noFiveHourLimit;
            quotaContentBottom = Math.Max(
                Math.Max(weekly.Bottom, primaryProgress.Bottom),
                noFiveHourLimit.Bottom);
        }
        else if (quotaLimitType is AccountQuotaLimitType.Monthly or
                 AccountQuotaLimitType.FiveHourOnly)
        {
            var windowKind = quotaLimitType switch
            {
                AccountQuotaLimitType.Monthly => AccountQuotaWindowKind.Monthly,
                _ => AccountQuotaWindowKind.FiveHour
            };
            var windowLabel = windowKind switch
            {
                AccountQuotaWindowKind.Monthly => "月",
                _ => "5h"
            };
            var window = usage.GetQuotaWindow(windowKind);
            var remaining = window?.RemainingPercent;
            var quotaColor = GetQuotaColor(remaining);
            var quota = MakeBadge(
                FormatQuotaRemaining(window, windowLabel),
                quotaLeft,
                singleQuotaTop,
                Color.FromArgb(44, quotaColor),
                quotaColor);
            quota.Width = quotaControlWidth;
            quota.Height = stacked ? 36 : 34;
            row.Controls.Add(quota);
            _toolTip.SetToolTip(quota, GetOfficialQuotaToolTip(window, windowLabel));
            primaryQuota = quota;
            primaryProgress = MakeQuotaProgressBar(
                remaining,
                quotaLeft + 8,
                stacked ? 336 : 60,
                quotaControlWidth - 16,
                quotaColor);
            row.Controls.Add(primaryProgress);
            quotaContentBottom = Math.Max(quota.Bottom, primaryProgress.Bottom);
        }
        else
        {
            var quota = MakeBadge(
                "待识别",
                quotaLeft,
                singleQuotaTop,
                Color.FromArgb(44, _palette.MutedTextColor),
                _palette.MutedTextColor);
            quota.Width = quotaControlWidth;
            quota.Height = stacked ? 36 : 34;
            row.Controls.Add(quota);
            primaryQuota = quota;
            quotaContentBottom = quota.Bottom;
        }

        var (primaryDetailText, secondaryDetailText) = GetQuotaRowDetailLines(
            account.IsCompatibleApi,
            quotaLimitType,
            usage);
        var singleDetailHeight = stacked ? 54 : 58;
        var detailLabelLeft = quotaLeft;
        var detailBlockHeight = hasTwoDetailLines ? resetAreaHeight : singleDetailHeight;
        var detailTop = quotaContentBottom + Math.Max(
            8,
            (actionTop - quotaContentBottom - detailBlockHeight) / 2);
        var detailLabelWidth = stacked
            ? width - 36
            : quotaControlWidth;
        var detail = new Label
        {
            Name = "QuotaResetPrimaryDetail",
            Text = primaryDetailText,
            Left = detailLabelLeft,
            Top = detailTop,
            Width = detailLabelWidth,
            Height = hasTwoDetailLines ? resetLineHeight : singleDetailHeight,
            Font = new Font(Font.FontFamily, hasTwoDetailLines ? 8F : 8.35F),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = false,
            UseMnemonic = false,
            UseCompatibleTextRendering = true
        };
        ThemeStyler.ApplyLabel(detail, _palette, true);
        _toolTip.SetToolTip(
            detail,
            account.IsCompatibleApi
                ? "基于本地 Token 日志和当前模型单价估算。"
                : "仅显示官方额度百分比与重置时间；不会调用模型。");
        row.Controls.Add(detail);

        Label? secondaryDetail = null;
        if (hasTwoDetailLines)
        {
            secondaryDetail = new Label
            {
                Name = "QuotaResetSecondaryDetail",
                Text = secondaryDetailText ?? string.Empty,
                Left = detailLabelLeft,
                Top = detail.Bottom + 2,
                Width = detailLabelWidth,
                Height = resetLineHeight,
                Font = new Font(Font.FontFamily, 8F),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = false,
                UseMnemonic = false,
                UseCompatibleTextRendering = true
            };
            ThemeStyler.ApplyLabel(secondaryDetail, _palette, true);
            _toolTip.SetToolTip(secondaryDetail, _toolTip.GetToolTip(detail));
            row.Controls.Add(secondaryDetail);
        }

        var rightContentEdge = stacked ? width - 18 : width - 26;
        var actionLeft = rightContentEdge - 290;
        if (!account.IsCompatibleApi)
        {
            var resetUsage = MakeActionButton(GetResetButtonText(account), actionLeft, actionTop, 180, false);
            resetUsage.Height = actionHeight;
            resetUsage.Name = "UsageResetAction";
            resetUsage.AccessibleDescription = account.Name;
            resetUsage.Enabled = CanResetUsage(account);
            resetUsage.Click += async (_, _) => await ResetUsageLimitAsync(account);
            _toolTip.SetToolTip(
                resetUsage,
                CanResetUsage(account)
                    ? "点击后确认并使用一次官方 Codex 用量重置"
                    : "可重置 0 次；请进入详情查询最新次数");
            row.Controls.Add(resetUsage);
        }

        var detailLeft = account.IsCompatibleApi ? rightContentEdge - 104 : actionLeft + 190;
        var detailWidth = account.IsCompatibleApi ? 104 : 100;
        var openDetail = MakeActionButton("详情", detailLeft, actionTop, detailWidth, false);
        openDetail.Height = actionHeight;
        openDetail.Click += (_, _) => SelectAccount(account.Name);
        row.Controls.Add(openDetail);

        row.Tag = new QuotaUsageRowBinding
        {
            AccountName = account.Name,
            QuotaLimitType = quotaLimitType,
            Kind = kind,
            Metrics = [firstMetric, secondMetric, thirdMetric],
            Detail = detail,
            SecondaryDetail = secondaryDetail,
            CapacityStatus = capacityStatus,
            CapacitySummary = capacitySummary,
            PrimaryQuota = primaryQuota,
            SecondaryQuota = secondaryQuota,
            PrimaryProgress = primaryProgress,
            SecondaryProgress = secondaryProgress
        };

        return row;
    }

    private QuotaProgressBar MakeQuotaProgressBar(double? remainingPercent, int left, int top, int width, Color color)
    {
        return new QuotaProgressBar
        {
            Left = left,
            Top = top,
            Width = width,
            Height = 5,
            Value = remainingPercent ?? 0D,
            TrackColor = _palette.ProgressTrackColor,
            FillColor = color,
            BackColor = Color.Transparent
        };
    }

    private Control CreateQuotaUsageDetailCard(AccountRecord account, AccountUsageSummary usage, int cardWidth)
    {
        var innerLeft = 22;
        var innerWidth = cardWidth - 44;
        var compact = cardWidth < 980;
        var card = new RoundedPanel
        {
            Width = cardWidth,
            Height = 1,
            Radius = 16,
            BorderColor = _palette.BorderColor,
            BackColor = _palette.CardColor,
            AccentColor = GetAccountStateColor(account),
            AccentWidth = 4,
            ShadowColor = Color.FromArgb(28, _palette.ShadowColor),
            Margin = Padding.Empty,
            Padding = new Padding(22)
        };

        var back = MakeBackIconButton(innerLeft, 22);
        back.Click += (_, _) =>
        {
            _showAccountDetail = false;
            RenderCards();
            ResetCardsScrollPosition();
            _statusBox.Text = "已返回额度列表。";
        };
        card.Controls.Add(back);

        var priceProfile = GetUsagePriceProfile(account);
        var quotaLimitType = ResolveQuotaLimitType(account, usage);
        var titleLeft = innerLeft + 58;
        var title = new Label
        {
            Text = account.Name,
            Left = titleLeft,
            Top = compact ? 14 : 18,
            Width = Math.Max(120, cardWidth - innerLeft - titleLeft),
            Height = 50,
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(title, _palette);
        card.Controls.Add(title);

        var subtitleLeft = innerLeft;
        var usageSummary = account.IsCompatibleApi
            ? "按 API 账单计费"
            : "仅显示官方额度百分比";
        var officialSummary = account.IsCompatibleApi ? null : GetOfficialFinancialSummary(usage);
        var subtitle = new Label
        {
            Text = string.IsNullOrWhiteSpace(officialSummary)
                ? $"{account.AuthKindLabel} · {usageSummary}"
                : $"{account.AuthKindLabel} · {usageSummary} · {officialSummary}",
            Left = subtitleLeft,
            Top = compact ? 68 : 66,
            Width = compact ? innerWidth : Math.Min(580, Math.Max(280, innerWidth / 2)),
            Height = 38,
            Font = new Font(Font.FontFamily, 8.7F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(subtitle, _palette, true);
        _toolTip.SetToolTip(
            subtitle,
            account.IsCompatibleApi
                ? "本地 Token 用量按 API 单价估算。"
                : "官方百分比与重置时间来自手动只读查询；不会调用模型。");
        card.Controls.Add(subtitle);

        var observed = usage.RateLimitObservedAtUtc.HasValue
            ? $"更新 {usage.RateLimitObservedAtUtc.Value.ToLocalTime():MM-dd HH:mm}"
            : "更新 暂无";
        var pricingLabel = GetUsagePricingLabel(usage.Month, priceProfile);
        var resetSummary = account.IsCompatibleApi
            ? ""
            : GetQuotaResetSummary(quotaLimitType, usage);
        var visibleMeta = account.IsCompatibleApi
            ? "余额请到服务商账单查看"
            : $"{resetSummary} · {observed}";
        var cacheWriteStatus = GetCacheWriteReportingLabel(usage.Month);
        var metaDetail = account.IsCompatibleApi
            ? $"兼容 API 按量计费，本软件只能从本地会话统计用量，无法读取服务商余额或重置时间；费用按 {pricingLabel} 估算；{cacheWriteStatus}"
            : $"额度类型：{GetQuotaLimitTypeLabel(quotaLimitType)}；{resetSummary}；{observed}；费用按 {pricingLabel} 估算；{cacheWriteStatus}";
        var meta = new Label
        {
            Text = visibleMeta,
            Left = compact ? innerLeft : innerLeft + Math.Min(610, innerWidth - 250),
            Top = compact ? 106 : 70,
            Width = compact ? innerWidth : Math.Max(240, innerWidth - Math.Min(610, innerWidth - 250)),
            Height = 28,
            Font = new Font(Font.FontFamily, 8.6F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(meta, _palette, true);
        _toolTip.SetToolTip(meta, metaDetail);
        card.Controls.Add(meta);

        // The 今天 / 本周 / 本月 summaries belong to the monitoring composition
        // itself. Keeping them inside the right information card removes the
        // detached duplicate strip and gives the card a coherent hierarchy.
        var monitorTop = compact ? 146 : 112;
        var passiveMonitoring = GetPassiveQuotaMonitoringResult(account, usage);
        var monitor = MakePassiveQuotaMonitor(
            account,
            usage,
            passiveMonitoring,
            innerLeft,
            monitorTop,
            innerWidth,
            compact: innerWidth < 760);
        card.Controls.Add(monitor);

        var trendToolbar = MakeQuotaTrendToolbar(
            account,
            usage,
            priceProfile,
            passiveMonitoring,
            innerLeft,
            monitor.Bottom + 16,
            innerWidth,
            out var trendPoints,
            out var trendEmptyText,
            out var trendFromUtc,
            out var trendThroughUtc,
            out var trendAssessmentWindows,
            out var exportButton);
        card.Controls.Add(trendToolbar);

        var chart = new QuotaTrendChart
        {
            Left = innerLeft,
            Top = trendToolbar.Bottom + 8,
            Width = innerWidth,
            Height = GetQuotaTrendChartHeight(innerWidth),
            Metric = _quotaTrendMetric,
            Samples = BuildQuotaChartSamples(
                new QuotaTrendDisplayData(
                    trendPoints,
                    trendEmptyText,
                    trendFromUtc,
                    trendThroughUtc),
                GetQuotaTrendBucketSize(_quotaTrendRange),
                ShouldTrimLeadingQuotaTrendBuckets(_quotaTrendRange),
                GetQuotaTrendLeadingContextDuration(_quotaTrendRange)),
            AssessmentWindows = trendAssessmentWindows,
            EmptyText = trendEmptyText,
            CostColor = _palette.PrimaryColor,
            CostFillColor = Color.FromArgb(72, _palette.PrimaryColor),
            ModelSecondaryColor = _palette.SecondaryAccentColor,
            ModelTertiaryColor = _palette.TertiaryAccentColor,
            ModelAccentColor = _palette.AccentColor,
            ModelOtherColor = _palette.MutedTextColor,
            RemainingColor = _palette.SuccessColor,
            AbnormalRemainingColor = _palette.DangerColor,
            GridColor = _palette.DividerColor,
            TextColor = _palette.TextColor,
            MutedColor = _palette.MutedTextColor,
            BackColor = _palette.SurfaceAltColor
        };
        card.Controls.Add(chart);

        // Model statistics share the trend toolbar above. Both the selected time range
        // and the 全部日志 / 本轮监测 scope therefore stay synchronized without a
        // second, duplicate set of range buttons.
        var modelDistributionItems = BuildModelUsageDistribution(trendPoints);
        var modelDistribution = new ModelUsageDistributionControl
        {
            Left = innerLeft,
            Top = chart.Bottom + 16,
            Width = innerWidth,
            Height = ModelUsageDistributionControl.GetPreferredHeight(
                innerWidth,
                modelDistributionItems.Count,
                Math.Max(1F, DeviceDpi / 96F)),
            RangeLabel = GetModelDistributionRangeLabel(_quotaTrendRange),
            Items = modelDistributionItems,
            SurfaceColor = _palette.SurfaceAltColor,
            BorderColor = _palette.BorderColor,
            TextColor = _palette.TextColor,
            MutedColor = _palette.MutedTextColor,
            PrimaryColor = _palette.PrimaryColor,
            SecondaryColor = _palette.SecondaryAccentColor,
            TertiaryColor = _palette.TertiaryAccentColor,
            AccentColor = _palette.AccentColor
        };
        card.Controls.Add(modelDistribution);

        var tableTitle = new Label
        {
            Text = "用量明细",
            Left = innerLeft,
            Top = modelDistribution.Bottom + 16,
            Width = 220,
            Height = 44,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(tableTitle, _palette);
        card.Controls.Add(tableTitle);

        var usageTable = innerWidth < 860
            ? MakeCompactQuotaUsageTable(innerLeft, tableTitle.Bottom + 10, innerWidth, usage, priceProfile)
            : MakeQuotaUsageTable(innerLeft, tableTitle.Bottom + 10, innerWidth, usage, priceProfile);
        card.Controls.Add(usageTable);
        card.Height = usageTable.Bottom + 22;
        var monitorBinding = (PassiveQuotaMonitorBinding)monitor.Tag!;
        card.Tag = new QuotaUsageDetailBinding
        {
            AccountName = account.Name,
            QuotaLimitType = quotaLimitType,
            Subtitle = subtitle,
            Meta = meta,
            Metrics = monitorBinding.UsageMetrics,
            Monitor = monitorBinding,
            Chart = chart,
            ExportButton = exportButton,
            ModelDistribution = modelDistribution,
            UsageTable = (QuotaUsageTableBinding)usageTable.Tag!,
            ShowsCacheWriteColumn = ShouldShowCacheWriteColumn(usage)
        };

        return card;
    }

    private int GetQuotaTrendChartHeight(int innerWidth)
    {
        // Detail controls are created dynamically after WinForms has applied form-level
        // scaling, while the owner-drawn chart still receives the real per-monitor DPI.
        // Preserve the same logical chart height at every scale so a multi-model hover
        // card and a wrapped legend cannot be clipped at 150% or 200% DPI.
        var dpiScale = Math.Max(1F, DeviceDpi / 96F);
        return CalculateQuotaTrendChartHeight(innerWidth, dpiScale);
    }

    private static int CalculateQuotaTrendChartHeight(int innerWidth, float dpiScale)
    {
        dpiScale = Math.Max(1F, dpiScale);
        var logicalWidth = innerWidth / dpiScale;
        var logicalHeight = logicalWidth < 760F ? 400F : 460F;
        return (int)Math.Ceiling(logicalHeight * dpiScale);
    }

    private Control MakePassiveQuotaMonitor(
        AccountRecord account,
        AccountUsageSummary usage,
        PassiveQuotaMonitoringResult monitoring,
        int left,
        int top,
        int width,
        bool compact)
    {
        var panelHeight = compact ? 620 : 540;
        var panel = new RoundedPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = panelHeight,
            Radius = 14,
            BackColor = _palette.SurfaceAltColor,
            BorderColor = _palette.BorderColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceAltColor, _palette.SecondaryAccentColor, 0.045F)
        };

        var monitorToggleText = monitoring.IsEnabled ? "关闭额度监测" : "开启额度监测";
        var monitorToggleWidth = account.IsCompatibleApi
            ? 0
            : Math.Min(
                Math.Max(180, width - 54),
                MeasureActionButtonWidth(monitorToggleText, 240));

        var title = new Label
        {
            Text = account.IsCompatibleApi ? "本地 API 用量" : "额度监测",
            Left = 18,
            Top = 10,
            Width = account.IsCompatibleApi
                ? width - 36
                : Math.Max(160, width - monitorToggleWidth - 54),
            Height = 38,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(title, _palette);
        panel.Controls.Add(title);

        if (!account.IsCompatibleApi)
        {
            var toggle = MakeActionButton(
                monitorToggleText,
                width - monitorToggleWidth - 18,
                6,
                monitorToggleWidth,
                !monitoring.IsEnabled);
            toggle.Height = 44;
            toggle.UseMnemonic = false;
            if (toggle is ModernButton modernToggle)
            {
                modernToggle.AutoShrinkText = false;
            }
            toggle.Click += (_, _) => TogglePassiveQuotaMonitoring(account, usage);
            _toolTip.SetToolTip(
                toggle,
                monitoring.IsEnabled
                    ? "关闭后停止更新并保留上一轮结果；不会发送任何模型请求。"
                    : "从点击时刻创建全新的监测周期；旧周期不会参与本轮正常/异常判断。");
            panel.Controls.Add(toggle);
        }

        var estimate = monitoring.Estimate;
        var quotaLimitType = ResolveQuotaLimitType(account, usage);
        var displayedQuotaWindow = GetPrimaryDisplayedQuotaWindow(quotaLimitType, usage);
        var presentationStatus = estimate?.Status ?? PassiveQuotaStatus.Collecting;
        var officialQuotaExhausted = !account.IsCompatibleApi &&
                                     IsOfficialQuotaExhausted(displayedQuotaWindow?.RemainingPercent);
        var hasStoppedMonitoringEpoch =
            monitoring.State.StartedAtUtc.HasValue && monitoring.State.StoppedAtUtc.HasValue;
        var statusText = account.IsCompatibleApi
            ? "本地统计，服务商余额不可读取"
            : officialQuotaExhausted
                ? "额度已用尽"
            : monitoring.IsEnabled
                ? estimate == null || estimate.Status == PassiveQuotaStatus.Collecting
                    ? $"监测 {Math.Clamp(estimate?.ObservedPercentSpan ?? 0D, 0D, 2D):0.#}/2%"
                    : GetPassiveQuotaStatusText(presentationStatus)
                : !hasStoppedMonitoringEpoch
                    ? "尚未开启"
                    : estimate == null || estimate.Status == PassiveQuotaStatus.Collecting
                    ? "上一轮数据不足"
                        : $"上一轮{GetPassiveQuotaStatusText(presentationStatus)}";
        var statusColor = account.IsCompatibleApi
            ? _palette.PrimaryColor
            : officialQuotaExhausted
                ? _palette.DangerColor
            : monitoring.IsEnabled && (estimate == null || estimate.Status == PassiveQuotaStatus.Collecting)
                ? _palette.PrimaryColor
                : estimate != null
                    ? GetPassiveQuotaStatusColor(presentationStatus)
                    : _palette.PrimaryColor;
        // At normal widths the gauge owns a dedicated left column. This keeps
        // the visual hierarchy stable and gives the liquid display roughly one
        // third of the monitor instead of tucking a small gauge at the right.
        var gaugeColumnWidth = compact
            ? width
            : Math.Clamp((int)Math.Round(width * 0.34D), 300, 470);
        var gaugeSize = compact
            ? Math.Min(250, Math.Max(220, width - 36))
            : Math.Min(420, Math.Max(292, gaugeColumnWidth - 44));
        var gaugeLeft = compact
            ? Math.Max(18, (width - gaugeSize) / 2)
            : 18 + Math.Max(0, (gaugeColumnWidth - gaugeSize) / 2);
        var gaugeTop = compact ? 58 : 64;
        var gaugeCaption = quotaLimitType switch
        {
            AccountQuotaLimitType.WeeklyOnly => "周剩余",
            AccountQuotaLimitType.Monthly => "月剩余",
            AccountQuotaLimitType.FiveHourAndWeekly or AccountQuotaLimitType.FiveHourOnly => "5h剩余",
            _ => "官方剩余"
        };
        var gauge = new PassiveQuotaGauge
        {
            Left = gaugeLeft,
            Top = gaugeTop,
            Width = gaugeSize,
            Height = gaugeSize,
            RemainingPercent = account.IsCompatibleApi ? null : displayedQuotaWindow?.RemainingPercent,
            // StatusText is retained for accessibility only. The gauge paints just
            // the percentage and caption; the visible health state lives below it.
            StatusText = statusText,
            Caption = account.IsCompatibleApi ? "本月 API 用量" : gaugeCaption,
            PlaceholderText = account.IsCompatibleApi ? "本地统计" : "采集中",
            AccentColor = UiDesign.Blend(_palette.PrimaryColor, _palette.SecondaryAccentColor, 0.52F),
            TrackColor = _palette.ProgressTrackColor,
            TextColor = _palette.TextColor,
            MutedColor = _palette.MutedTextColor,
            // Keep the animated gauge opaque.  This closely matches the parent gradient at
            // the gauge's position while avoiding a costly transparent-parent repaint each
            // time the liquid wave advances or the card is scrolled.
            BackColor = UiDesign.Blend(
                _palette.SurfaceAltColor,
                _palette.SecondaryAccentColor,
                0.022F),
            Font = new Font(Font.FontFamily, 9F)
        };
        panel.Controls.Add(gauge);

        var externalStatus = new Label
        {
            Name = "PassiveQuotaStatusLabel",
            Text = statusText,
            Left = gauge.Left,
            Top = gauge.Bottom + 2,
            Width = gauge.Width,
            Height = 32,
            Font = new Font(Font.FontFamily, 8.7F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleCenter,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(externalStatus, _palette, true);
        externalStatus.ForeColor = statusColor;
        panel.Controls.Add(externalStatus);

        var infoLeft = compact ? 18 : 18 + gaugeColumnWidth + 18;
        var infoWidth = compact ? width - 36 : Math.Max(360, width - infoLeft - 18);
        // The two balanced control rows are intentional: they prevent long
        // Chinese labels from shrinking and avoid a dense one-line toolbar.
        var stackedActions = !account.IsCompatibleApi;
        var infoBlockHeight = account.IsCompatibleApi
            ? compact ? 224 : 278
            : compact ? 330 : Math.Clamp(gaugeSize, 390, 420);
        var infoTop = compact
            ? externalStatus.Bottom + 12
            : gauge.Top + Math.Max(0, (gauge.Height - infoBlockHeight) / 2);

        var infoCard = new RoundedPanel
        {
            Left = infoLeft,
            Top = infoTop,
            Width = infoWidth,
            Height = infoBlockHeight,
            Radius = 12,
            BackColor = _palette.CardColor,
            BorderColor = UiDesign.Blend(_palette.BorderColor, _palette.SecondaryAccentColor, 0.14F),
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, _palette.SecondaryAccentColor, 0.055F),
            AccentColor = UiDesign.Blend(_palette.PrimaryColor, _palette.SecondaryAccentColor, 0.48F),
            AccentWidth = 3,
            ShadowColor = Color.FromArgb(16, _palette.ShadowColor),
            Elevation = 1,
            ShowTechDecoration = false,
            DecorationColor = Color.FromArgb(34, _palette.SecondaryAccentColor)
        };
        panel.Controls.Add(infoCard);

        var summaryText = account.IsCompatibleApi
            ? $"本月 {FormatEstimatedCost(usage.Month, GetUsagePriceProfile(account))}"
            : GetPassiveQuotaSummaryText(
                monitoring,
                displayedQuotaWindow?.RemainingPercent);
        var summary = new Label
        {
            Text = summaryText,
            Left = compact ? 14 : 22,
            Top = compact ? 8 : 20,
            Width = infoWidth - (compact ? 28 : 44),
            Height = compact ? 38 : 48,
            Font = new Font(Font.FontFamily, compact ? 10.5F : 11.4F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true
        };
        ThemeStyler.ApplyLabel(summary, _palette);
        infoCard.Controls.Add(summary);

        var observedSpan = Math.Clamp(estimate?.ObservedPercentSpan ?? 0D, 0D, 2D);
        var hasCompletedMonitoringWindow = (estimate?.CycleCount ?? 0) > 0;
        QuotaProgressBar? measurementProgress = null;
        if (!account.IsCompatibleApi)
        {
            measurementProgress = new QuotaProgressBar
            {
                Left = compact ? 14 : 22,
                Top = compact ? 48 : 78,
                Width = infoWidth - (compact ? 28 : 44),
                Height = compact ? 7 : 9,
                Value = hasCompletedMonitoringWindow ? 100D : observedSpan / 2D * 100D,
                TrackColor = _palette.ProgressTrackColor,
                FillColor = UiDesign.Blend(_palette.PrimaryColor, _palette.SecondaryAccentColor, 0.52F)
            };
            infoCard.Controls.Add(measurementProgress);
        }

        var progressText = GetPassiveQuotaProgressText(
            account.IsCompatibleApi,
            monitoring,
            observedSpan,
            hasStoppedMonitoringEpoch);
        var progress = new Label
        {
            Text = progressText,
            Left = compact ? 14 : 22,
            Top = compact ? 60 : 96,
            Width = infoWidth - (compact ? 28 : 44),
            Height = compact ? 34 : 42,
            Font = new Font(Font.FontFamily, compact ? 8.6F : 9.1F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true
        };
        progress.Visible = !string.IsNullOrWhiteSpace(progressText);
        ThemeStyler.ApplyLabel(progress, _palette, true);
        infoCard.Controls.Add(progress);

        var usageMetricTop = progress.Bottom + (compact ? 6 : 10);
        var usageMetricHeight = compact ? 90 : 104;
        var usageMetricInset = compact ? 14 : 22;
        var usageMetricGap = compact ? 6 : 10;
        var usageMetricWidth = Math.Max(
            88,
            (infoWidth - (usageMetricInset * 2) - (usageMetricGap * 2)) / 3);
        var usageMetrics = GetQuotaUsageMetrics(account, quotaLimitType, usage);
        var usageMetricBindings = new UsageMetricBinding[usageMetrics.Length];
        var monitorPriceProfile = GetUsagePriceProfile(account);
        for (var index = 0; index < usageMetrics.Length; index++)
        {
            var metric = usageMetrics[index];
            var metricPanel = MakePassiveQuotaUsageMetric(
                metric.Caption,
                metric.Bucket,
                monitorPriceProfile,
                usageMetricInset + ((usageMetricWidth + usageMetricGap) * index),
                usageMetricTop,
                usageMetricWidth,
                usageMetricHeight,
                out usageMetricBindings[index]);
            infoCard.Controls.Add(metricPanel);
        }

        PillLabel? officialQuota = null;
        PillLabel? resetCount = null;
        Button? resetAction = null;
        if (!account.IsCompatibleApi)
        {
            var actionGap = compact ? 8 : 12;
            var horizontalInset = compact ? 14 : 22;
            var rowTop = usageMetricTop + usageMetricHeight + (compact ? 12 : 16);
            var controlHeight = compact ? 42 : 54;
            var usableRowWidth = infoWidth - (horizontalInset * 2) - actionGap;
            var quotaWidth = stackedActions
                ? usableRowWidth / 2
                : Math.Min(210, Math.Max(164, infoWidth / 5));
            var countWidth = stackedActions
                ? usableRowWidth - quotaWidth
                : 150;
            var queryWidth = stackedActions
                ? quotaWidth
                : Math.Max(184, MeasureActionButtonWidth("查询重置次数", 184));
            var resetWidth = stackedActions ? countWidth : 126;

            officialQuota = MakeBadge(
                "官方剩余 待查询",
                horizontalInset,
                rowTop,
                Color.FromArgb(40, _palette.PrimaryColor),
                _palette.PrimaryColor);
            officialQuota.Width = quotaWidth;
            officialQuota.Height = controlHeight;
            officialQuota.Font = new Font(Font.FontFamily, compact ? 8.6F : 9.2F, FontStyle.Bold);
            infoCard.Controls.Add(officialQuota);

            resetCount = MakeBadge(
                "可重置 0 次",
                officialQuota.Right + actionGap,
                rowTop,
                Color.FromArgb(36, _palette.MutedTextColor),
                _palette.MutedTextColor);
            resetCount.Width = countWidth;
            resetCount.Height = controlHeight;
            resetCount.Font = new Font(Font.FontFamily, compact ? 8.6F : 9.2F, FontStyle.Bold);
            infoCard.Controls.Add(resetCount);

            var secondRowTop = stackedActions ? rowTop + controlHeight + (compact ? 8 : 14) : rowTop;
            var queryLeft = stackedActions ? horizontalInset : resetCount.Right + actionGap;
            var queryResetCount = MakeActionButton(
                "查询重置次数",
                queryLeft,
                secondRowTop,
                queryWidth,
                false);
            queryResetCount.Height = controlHeight;
            queryResetCount.Padding = Padding.Empty;
            queryResetCount.TextAlign = ContentAlignment.MiddleCenter;
            queryResetCount.UseMnemonic = false;
            queryResetCount.Font = new Font(Font.FontFamily, compact ? 8.9F : 9.4F, FontStyle.Bold);
            queryResetCount.Click += async (_, _) => await QueryUsageLimitResetAsync(account);
            _toolTip.SetToolTip(
                queryResetCount,
                "只读刷新官方百分比、Credits 和可重置次数；不发送提示、不调用模型、不消耗 Token");
            infoCard.Controls.Add(queryResetCount);

            resetAction = MakeActionButton(
                "立即重置",
                queryResetCount.Right + actionGap,
                secondRowTop,
                resetWidth,
                true);
            resetAction.Height = controlHeight;
            resetAction.Font = new Font(Font.FontFamily, compact ? 8.9F : 9.4F, FontStyle.Bold);
            resetAction.Name = "UsageResetAction";
            resetAction.AccessibleDescription = account.Name;
            resetAction.Click += async (_, _) => await ResetUsageLimitAsync(account);
            infoCard.Controls.Add(resetAction);
        }

        var binding = new PassiveQuotaMonitorBinding
        {
            Gauge = gauge,
            Status = externalStatus,
            Summary = summary,
            Progress = progress,
            UsageMetrics = usageMetricBindings,
            MeasurementProgress = measurementProgress,
            OfficialQuota = officialQuota,
            ResetCount = resetCount,
            ResetAction = resetAction
        };
        panel.Tag = binding;
        UpdatePassiveQuotaMonitor(binding, account, usage, monitoring);

        panel.Height = Math.Max(
            panelHeight,
            Math.Max(externalStatus.Bottom, infoCard.Bottom) + 18);

        return panel;
    }

    private static string GetPassiveQuotaProgressText(
        bool isCompatibleApi,
        PassiveQuotaMonitoringResult monitoring,
        double observedSpan,
        bool hasStoppedMonitoringEpoch)
    {
        if (isCompatibleApi)
        {
            return string.Empty;
        }

        if (!monitoring.IsEnabled)
        {
            return hasStoppedMonitoringEpoch
                ? "监测已停止"
                : string.Empty;
        }

        return (monitoring.Estimate?.CycleCount ?? 0) > 0
            ? string.Empty
            : $"首次估算 {observedSpan:0.#}/2%";
    }

    private void UpdatePassiveQuotaMonitor(
        PassiveQuotaMonitorBinding binding,
        AccountRecord account,
        AccountUsageSummary usage,
        PassiveQuotaMonitoringResult monitoring)
    {
        var estimate = monitoring.Estimate;
        var quotaLimitType = ResolveQuotaLimitType(account, usage);
        var displayedQuotaWindow = GetPrimaryDisplayedQuotaWindow(quotaLimitType, usage);
        var presentationStatus = estimate?.Status ?? PassiveQuotaStatus.Collecting;
        var officialQuotaExhausted = !account.IsCompatibleApi &&
                                     IsOfficialQuotaExhausted(displayedQuotaWindow?.RemainingPercent);
        var hasStoppedMonitoringEpoch =
            monitoring.State.StartedAtUtc.HasValue && monitoring.State.StoppedAtUtc.HasValue;
        var observedSpan = Math.Clamp(estimate?.ObservedPercentSpan ?? 0D, 0D, 2D);
        var statusText = account.IsCompatibleApi
            ? "本地统计，服务商余额不可读取"
            : officialQuotaExhausted
                ? "额度已用尽"
            : monitoring.IsEnabled
                ? estimate == null || estimate.Status == PassiveQuotaStatus.Collecting
                    ? $"监测 {observedSpan:0.#}/2%"
                    : GetPassiveQuotaStatusText(presentationStatus)
                : !hasStoppedMonitoringEpoch
                    ? "尚未开启"
                    : estimate == null || estimate.Status == PassiveQuotaStatus.Collecting
                    ? "上一轮数据不足"
                        : $"上一轮{GetPassiveQuotaStatusText(presentationStatus)}";
        var statusColor = account.IsCompatibleApi
            ? _palette.PrimaryColor
            : officialQuotaExhausted
                ? _palette.DangerColor
            : monitoring.IsEnabled && (estimate == null || estimate.Status == PassiveQuotaStatus.Collecting)
                ? _palette.PrimaryColor
                : estimate != null
                    ? GetPassiveQuotaStatusColor(presentationStatus)
                    : _palette.PrimaryColor;

        binding.Gauge.RemainingPercent = account.IsCompatibleApi
            ? null
            : displayedQuotaWindow?.RemainingPercent;
        binding.Gauge.StatusText = statusText;
        binding.Gauge.Caption = account.IsCompatibleApi
            ? "本月 API 用量"
            : quotaLimitType switch
            {
                AccountQuotaLimitType.WeeklyOnly => "周剩余",
                AccountQuotaLimitType.Monthly => "月剩余",
                AccountQuotaLimitType.FiveHourAndWeekly or AccountQuotaLimitType.FiveHourOnly => "5h剩余",
                _ => "官方剩余"
            };
        binding.Gauge.PlaceholderText = account.IsCompatibleApi ? "本地统计" : "采集中";
        SetLabelText(binding.Status, statusText);
        binding.Status.ForeColor = statusColor;
        _toolTip.SetToolTip(
            binding.Status,
            GetPassiveQuotaPresentationToolTip(monitoring, displayedQuotaWindow?.RemainingPercent));

        var priceProfile = GetUsagePriceProfile(account);
        var summaryText = account.IsCompatibleApi
            ? $"本月 {FormatEstimatedCost(usage.Month, priceProfile)}"
            : GetPassiveQuotaSummaryText(
                monitoring,
                displayedQuotaWindow?.RemainingPercent);
        SetLabelText(binding.Summary, summaryText);
        _toolTip.SetToolTip(
            binding.Summary,
            summaryText +
            Environment.NewLine +
            "前项按“容量参考 × 当前官方剩余百分比”自动换算；容量参考只在新的完整 2% 自然使用校准窗口完成后更新，避免整数百分比的单点波动造成容量抖动。" +
            Environment.NewLine +
            "容量口径：每条用量按 sub2api 实际账单的基础价格档换算为 API 等值，不启用 >272K 长上下文加价；缓存写入未上报时按普通输入价作基础估算，不是官方美元余额。" +
            (officialQuotaExhausted
                ? Environment.NewLine + GetPassiveQuotaPresentationToolTip(monitoring, displayedQuotaWindow?.RemainingPercent)
                : string.Empty));

        if (binding.MeasurementProgress != null)
        {
            binding.MeasurementProgress.Value = (estimate?.CycleCount ?? 0) > 0
                ? 100D
                : observedSpan / 2D * 100D;
        }
        var progressText = GetPassiveQuotaProgressText(
            account.IsCompatibleApi,
            monitoring,
            observedSpan,
            hasStoppedMonitoringEpoch);
        SetLabelText(binding.Progress, progressText);
        binding.Progress.Visible = !string.IsNullOrWhiteSpace(progressText);
        _toolTip.SetToolTip(
            binding.Progress,
            string.IsNullOrWhiteSpace(progressText)
                ? null
                : monitoring.Message);

        var usageMetrics = GetQuotaUsageMetrics(account, quotaLimitType, usage);
        for (var index = 0; index < Math.Min(binding.UsageMetrics.Length, usageMetrics.Length); index++)
        {
            SetLabelText(
                binding.UsageMetrics[index].Cost,
                FormatEstimatedCost(usageMetrics[index].Bucket, priceProfile));
            SetLabelText(
                binding.UsageMetrics[index].Tokens,
                $"{FormatTokens(usageMetrics[index].Bucket.TotalTokens)} token");
        }

        if (binding.OfficialQuota != null)
        {
            var fiveHourWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour);
            var weeklyWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly);
            var monthlyWindow = usage.GetQuotaWindow(AccountQuotaWindowKind.Monthly);
            var quotaText = quotaLimitType switch
            {
                AccountQuotaLimitType.FiveHourAndWeekly =>
                    $"{FormatQuotaRemaining(fiveHourWindow, "5h")} · {FormatQuotaRemaining(weeklyWindow, "周")}",
                AccountQuotaLimitType.WeeklyOnly =>
                    $"{FormatQuotaRemaining(weeklyWindow, "周")} · 无 5h 限额",
                AccountQuotaLimitType.FiveHourOnly =>
                    $"{FormatQuotaRemaining(fiveHourWindow, "5h")} · 无周限额",
                AccountQuotaLimitType.Monthly =>
                    FormatQuotaRemaining(monthlyWindow, "月"),
                _ => "官方剩余 待查询"
            };
            var quotaColor = quotaLimitType == AccountQuotaLimitType.Unknown
                ? _palette.MutedTextColor
                : GetQuotaColor(displayedQuotaWindow?.RemainingPercent);
            UpdateQuotaPill(binding.OfficialQuota, quotaText, quotaColor);
            _toolTip.SetToolTip(binding.OfficialQuota, quotaText);
        }

        if (binding.ResetCount != null)
        {
            var availableCount = GetAvailableResetCount(account);
            var countColor = availableCount > 0 ? _palette.SuccessColor : _palette.MutedTextColor;
            UpdateQuotaPill(binding.ResetCount, $"可重置 {availableCount} 次", countColor);
        }
        if (binding.ResetAction != null)
        {
            binding.ResetAction.Enabled = CanResetUsage(account);
            _toolTip.SetToolTip(
                binding.ResetAction,
                CanResetUsage(account)
                    ? "点击后确认并使用一次官方 Codex 用量重置"
                    : "可重置 0 次，不能执行重置；请先查询最新次数");
        }
    }

    private void TogglePassiveQuotaMonitoring(AccountRecord account, AccountUsageSummary usage)
    {
        if (account.IsCompatibleApi)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var state = _passiveQuotaMonitoring.GetState(account);
            if (state.IsEnabled)
            {
                var priceProfile = GetUsagePriceProfile(account);
                _passiveQuotaMonitoring.DisableAndCapture(
                    account,
                    usage,
                    item => EstimateUsageEventCost(item, priceProfile),
                    now);
                _statusBox.Text = $"已关闭 {account.Name} 的额度监测；上一轮结果已冻结，不会继续变化。";
            }
            else
            {
                var quotaLimitType = ResolveQuotaLimitType(account, usage);
                var startingWindow = quotaLimitType switch
                {
                    AccountQuotaLimitType.FiveHourAndWeekly or AccountQuotaLimitType.FiveHourOnly =>
                        usage.GetQuotaWindow(AccountQuotaWindowKind.FiveHour),
                    AccountQuotaLimitType.Monthly =>
                        usage.GetQuotaWindow(AccountQuotaWindowKind.Monthly),
                    AccountQuotaLimitType.WeeklyOnly =>
                        usage.GetQuotaWindow(AccountQuotaWindowKind.Weekly),
                    _ => null
                };
                var started = _passiveQuotaMonitoring.Enable(
                    account,
                    now,
                    startingWindow?.UsedPercent,
                    startingWindow?.WindowMinutes);
                _statusBox.Text = quotaLimitType == AccountQuotaLimitType.WeeklyOnly
                    ? $"已开启 {account.Name} 的周额度监测；推测总额度不低于 $90 时判定正常。"
                    : $"已开启 {account.Name} 的额度监测；本轮从 {started.StartedAtUtc?.ToLocalTime():MM-dd HH:mm:ss} 开始。";
            }

            RenderCards();
        }
        catch (Exception error)
        {
            ShowError("无法更新额度监测状态：" + error.Message);
        }
    }

    private Control MakeQuotaTrendToolbar(
        AccountRecord account,
        AccountUsageSummary usage,
        UsagePriceProfile priceProfile,
        PassiveQuotaMonitoringResult passiveMonitoring,
        int left,
        int top,
        int width,
        out IReadOnlyList<PassiveQuotaTrendPoint> trendPoints,
        out string trendEmptyText,
        out DateTimeOffset trendFromUtc,
        out DateTimeOffset trendThroughUtc,
        out IReadOnlyList<PassiveQuotaAssessmentWindow> trendAssessmentWindows,
        out Button exportButton)
    {
        var monitoringState = account.IsCompatibleApi
            ? null
            : _passiveQuotaMonitoring.GetState(account);
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        var hasMonitoringEpoch = monitoringState?.StartedAtUtc.HasValue == true;
        if (!_quotaTrendScopes.TryGetValue(accountKey, out var scope))
        {
            scope = QuotaTrendScope.Realtime;
            _quotaTrendScopes[accountKey] = scope;
        }
        if (account.IsCompatibleApi && scope == QuotaTrendScope.Monitoring)
        {
            scope = QuotaTrendScope.Realtime;
            _quotaTrendScopes[accountKey] = scope;
        }

        var realtimeWidth = MeasureActionButtonWidth("全部日志", 112);
        var monitoringWidth = MeasureActionButtonWidth("本轮监测", 124);
        var apiMetricWidth = MeasureActionButtonWidth("API 等值", 88);
        var tokenMetricWidth = MeasureActionButtonWidth("Token", 76);
        var exportWidth = MeasureActionButtonWidth("导出 CSV", 108);
        var ranges = new (string Label, TimeSpan Range)[]
        {
            ("1h", TimeSpan.FromHours(1)),
            ("5h", TimeSpan.FromHours(5)),
            ("今天", TimeSpan.FromHours(24)),
            ("本周", TimeSpan.FromDays(7)),
            ("本月", TimeSpan.FromDays(30))
        };
        var rangeWidths = ranges
            .Select(option => MeasureActionButtonWidth(option.Label, 64))
            .ToArray();
        const int rangeGap = 6;
        var rangeGroupWidth = rangeWidths.Sum() + (rangeGap * (ranges.Length - 1));
        const int metricGap = 6;
        var metricGroupWidth = apiMetricWidth + metricGap + tokenMetricWidth;
        var singleLineRequiredWidth =
            16 + realtimeWidth + 8 + monitoringWidth + 18 + rangeGroupWidth +
            18 + metricGroupWidth + 18 + exportWidth + 16;
        var compact = width < singleLineRequiredWidth;
        var veryCompact = compact &&
            width < 16 + rangeGroupWidth + 18 + metricGroupWidth + 16;
        var toolbar = new RoundedPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = veryCompact ? 140 : compact ? 98 : 54,
            Radius = 11,
            BackColor = _palette.CardColor,
            BorderColor = _palette.BorderColor
        };

        const int firstRowTop = 8;
        var realtime = MakeActionButton(
            "全部日志",
            16,
            firstRowTop,
            realtimeWidth,
            scope == QuotaTrendScope.Realtime);
        realtime.Height = 36;
        realtime.UseMnemonic = false;
        if (realtime is ModernButton realtimeButton)
        {
            realtimeButton.AutoShrinkText = false;
        }
        realtime.Click += (_, _) =>
        {
            _quotaTrendScopes[accountKey] = QuotaTrendScope.Realtime;
            RenderCards();
        };
        _toolTip.SetToolTip(
            realtime,
            "显示所选时间范围内的全部自然使用日志。");
        toolbar.Controls.Add(realtime);

        var monitoring = MakeActionButton(
            "本轮监测",
            realtime.Right + 8,
            firstRowTop,
            monitoringWidth,
            scope == QuotaTrendScope.Monitoring);
        monitoring.Height = 36;
        monitoring.UseMnemonic = false;
        monitoring.Enabled = hasMonitoringEpoch;
        if (monitoring is ModernButton monitoringButton)
        {
            monitoringButton.AutoShrinkText = false;
        }
        monitoring.Click += (_, _) =>
        {
            _quotaTrendScopes[accountKey] = QuotaTrendScope.Monitoring;
            RenderCards();
        };
        _toolTip.SetToolTip(
            monitoring,
            hasMonitoringEpoch
                ? monitoringState?.IsEnabled == true
                    ? "只显示本次开启额度监测之后的数据。"
                    : "显示上一轮已经关闭并冻结的监测数据。"
                : account.IsCompatibleApi
                    ? "API 账号不使用套餐额度监测。"
                    : "尚未建立监测周期；开启额度监测后即可查看本轮数据。");
        toolbar.Controls.Add(monitoring);

        var buttonTop = compact ? 50 : 8;
        var rangeLeft = compact ? 16 : monitoring.Right + 18;
        for (var index = 0; index < ranges.Length; index++)
        {
            var option = ranges[index];
            var selected = _quotaTrendRange == option.Range;
            var button = MakeActionButton(option.Label, rangeLeft, buttonTop, rangeWidths[index], selected);
            button.Height = 36;
            button.Font = new Font(Font.FontFamily, 8F, selected ? FontStyle.Bold : FontStyle.Regular);
            if (button is ModernButton rangeButton)
            {
                rangeButton.AutoShrinkText = false;
            }
            button.Click += (_, _) =>
            {
                _quotaTrendRange = option.Range;
                RenderCards();
            };
            toolbar.Controls.Add(button);
            rangeLeft += rangeWidths[index] + rangeGap;
        }

        var metricTop = veryCompact ? 92 : buttonTop;
        var metricLeft = veryCompact
            ? 16
            : rangeLeft - rangeGap + 18;
        var apiMetric = MakeActionButton(
            "API 等值",
            metricLeft,
            metricTop,
            apiMetricWidth,
            _quotaTrendMetric == QuotaTrendMetric.ApiEquivalent);
        apiMetric.Height = 36;
        apiMetric.UseMnemonic = false;
        if (apiMetric is ModernButton apiMetricButton)
        {
            apiMetricButton.AutoShrinkText = false;
        }
        apiMetric.Click += (_, _) =>
        {
            _quotaTrendMetric = QuotaTrendMetric.ApiEquivalent;
            RenderCards();
        };
        _toolTip.SetToolTip(apiMetric, "按各模型的 sub2api 实际账单口径显示每个时间段的美元等值。");
        toolbar.Controls.Add(apiMetric);

        var tokenMetric = MakeActionButton(
            "Token",
            apiMetric.Right + metricGap,
            metricTop,
            tokenMetricWidth,
            _quotaTrendMetric == QuotaTrendMetric.Tokens);
        tokenMetric.Height = 36;
        tokenMetric.UseMnemonic = false;
        if (tokenMetric is ModernButton tokenMetricButton)
        {
            tokenMetricButton.AutoShrinkText = false;
        }
        tokenMetric.Click += (_, _) =>
        {
            _quotaTrendMetric = QuotaTrendMetric.Tokens;
            RenderCards();
        };
        _toolTip.SetToolTip(tokenMetric, "按日志中的真实 Token 数显示每个时间段的用量。");
        toolbar.Controls.Add(tokenMetric);

        var trendData = GetQuotaTrendData(account, usage, priceProfile, passiveMonitoring);
        trendPoints = trendData.Points;
        trendEmptyText = trendData.EmptyText;
        trendFromUtc = trendData.FromUtc;
        trendThroughUtc = trendData.ThroughUtc;
        trendAssessmentWindows = trendData.AssessmentWindows ?? [];
        var export = MakeActionButton(
            "导出 CSV",
            width - exportWidth - 16,
            compact ? firstRowTop : buttonTop,
            exportWidth,
            false);
        export.Height = 36;
        export.UseMnemonic = false;
        if (export is ModernButton modernExportButton)
        {
            modernExportButton.AutoShrinkText = false;
        }
        export.Tag = trendPoints;
        export.Click += (_, _) => ExportQuotaTrendCsv(
            account,
            export.Tag as IReadOnlyList<PassiveQuotaTrendPoint> ?? []);
        _toolTip.SetToolTip(
            export,
            scope == QuotaTrendScope.Monitoring
                ? "导出当前显示的本轮监测数据；关闭后的上一轮数据保持冻结。"
                : $"导出当前显示的 {GetQuotaTrendRangeLabel(_quotaTrendRange)} 日志；不包含聊天内容或凭据。");
        toolbar.Controls.Add(export);
        exportButton = export;
        return toolbar;
    }

    private QuotaTrendDisplayData GetQuotaTrendData(
        AccountRecord account,
        AccountUsageSummary usage,
        UsagePriceProfile priceProfile,
        PassiveQuotaMonitoringResult? monitoringResult = null)
    {
        var monitoringState = account.IsCompatibleApi
            ? null
            : monitoringResult?.State ?? _passiveQuotaMonitoring.GetState(account);
        var accountKey = QuotaAccountIdentity.CreateKey(account);
        if (!_quotaTrendScopes.TryGetValue(accountKey, out var scope))
        {
            scope = QuotaTrendScope.Realtime;
            _quotaTrendScopes[accountKey] = scope;
        }
        if (account.IsCompatibleApi && scope == QuotaTrendScope.Monitoring)
        {
            scope = QuotaTrendScope.Realtime;
            _quotaTrendScopes[accountKey] = scope;
        }

        var hasMonitoringEpoch = monitoringState?.StartedAtUtc.HasValue == true;
        var nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset fromUtc;
        DateTimeOffset throughUtc;
        string emptyText;
        if (scope == QuotaTrendScope.Monitoring && monitoringState?.StartedAtUtc is { } monitoringStarted)
        {
            throughUtc = monitoringState.IsEnabled
                ? nowUtc
                : monitoringState.StoppedAtUtc ?? nowUtc;
            fromUtc = GetQuotaTrendStartUtc(_quotaTrendRange, throughUtc);
            if (monitoringStarted > fromUtc)
            {
                fromUtc = monitoringStarted;
            }
            emptyText = monitoringState.IsEnabled
                ? "本轮监测开始后还没有自然使用记录"
                : "上一轮监测时间段没有自然使用记录（已冻结）";
        }
        else
        {
            throughUtc = nowUtc;
            fromUtc = GetQuotaTrendStartUtc(_quotaTrendRange, throughUtc);
            emptyText = scope == QuotaTrendScope.Monitoring
                ? "尚未建立监测周期；请先开启额度监测"
                : "这个时间段还没有自然使用记录";
        }

        var officialObservations = account.IsCompatibleApi
            ? []
            : BuildQuotaTrendOfficialObservations(
                monitoringState,
                _passiveQuotaMonitoring.GetOfficialObservations(account),
                monitoringState?.IsEnabled == true
                    ? GetPrimaryDisplayedQuotaWindow(
                        ResolveQuotaLimitType(account, usage),
                        usage)?.ResetAtUtc
                    : null,
                fromUtc,
                throughUtc);
        var points = scope == QuotaTrendScope.Monitoring && !hasMonitoringEpoch
            ? []
            : PassiveQuotaMonitor.BuildTrend(
                account,
                usage,
                item => EstimateUsageEventCost(item, priceProfile),
                fromUtc,
                GetQuotaTrendBucketSize(_quotaTrendRange),
                throughUtc,
                officialObservations);
        var assessmentWindows = SelectQuotaTrendAssessmentWindows(
            scope,
            hasMonitoringEpoch,
            (monitoringResult?.Estimate ?? monitoringState?.LastEstimate)?.AssessmentWindows,
            fromUtc,
            throughUtc);
        return new QuotaTrendDisplayData(
            points,
            emptyText,
            fromUtc,
            throughUtc,
            assessmentWindows);
    }

    private static IReadOnlyList<PassiveQuotaOfficialObservation> BuildQuotaTrendOfficialObservations(
        PassiveQuotaMonitoringState? monitoringState,
        IReadOnlyList<PassiveQuotaOfficialObservation> capturedObservations,
        DateTimeOffset? preferredResetAtUtc,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc)
    {
        if (throughUtc <= fromUtc)
        {
            return [];
        }

        var from = fromUtc.ToUniversalTime();
        var through = throughUtc.ToUniversalTime();
        var ordered = PassiveQuotaMonitoringService.NormalizeOfficialObservations(
                capturedObservations,
                preferredResetAtUtc)
            .Where(item => item.TimestampUtc > DateTimeOffset.MinValue)
            .OrderBy(item => item.TimestampUtc)
            .ToList();
        var visible = ordered
            .Where(item => item.TimestampUtc >= from && item.TimestampUtc < through)
            .ToList();
        var carryForward = ordered.LastOrDefault(item => item.TimestampUtc < from);
        if (carryForward != null)
        {
            visible.Insert(0, carryForward with { TimestampUtc = from });
        }
        else if (monitoringState is
                 {
                     StartedAtUtc: { } startedAtUtc,
                     StartingUsedPercent: { } startingUsedPercent,
                     StartingWindowMinutes: { } startingWindowMinutes
                 } && startedAtUtc <= from)
        {
            visible.Insert(0, new PassiveQuotaOfficialObservation(
                from,
                startingUsedPercent,
                startingWindowMinutes,
                monitoringState.DisplayResetAtUtc,
                startedAtUtc));
        }

        var deduplicated = visible
            .OrderBy(item => item.TimestampUtc)
            .GroupBy(item => new
            {
                Timestamp = item.TimestampUtc.ToUniversalTime(),
                item.WindowMinutes,
                item.UsedPercent
            })
            .Select(group => group.Last())
            .ToArray();
        return PassiveQuotaMonitoringService.NormalizeOfficialObservations(
            deduplicated,
            preferredResetAtUtc);
    }

    private static IReadOnlyList<PassiveQuotaAssessmentWindow> SelectQuotaTrendAssessmentWindows(
        QuotaTrendScope scope,
        bool hasMonitoringEpoch,
        IReadOnlyList<PassiveQuotaAssessmentWindow>? windows,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc)
    {
        if (scope != QuotaTrendScope.Monitoring ||
            !hasMonitoringEpoch ||
            throughUtc <= fromUtc)
        {
            return [];
        }

        return (windows ?? [])
            .Where(item =>
                item.Status == PassiveQuotaStatus.Abnormal &&
                item.ThroughUtc > fromUtc &&
                item.FromUtc < throughUtc)
            .OrderBy(item => item.FromUtc)
            .ThenBy(item => item.ThroughUtc)
            .TakeLast(256)
            .ToArray();
    }

    private static DateTimeOffset GetQuotaTrendStartUtc(TimeSpan range, DateTimeOffset throughUtc)
    {
        var normalizedThroughUtc = throughUtc.ToUniversalTime();
        if (range <= TimeSpan.FromHours(1))
        {
            return normalizedThroughUtc - TimeSpan.FromHours(1);
        }
        if (range <= TimeSpan.FromHours(5))
        {
            return normalizedThroughUtc - TimeSpan.FromHours(5);
        }

        var throughLocal = normalizedThroughUtc.ToLocalTime();
        DateTime localStart;
        if (range <= TimeSpan.FromHours(24))
        {
            localStart = throughLocal.Date;
        }
        else if (range <= TimeSpan.FromDays(7))
        {
            var daysSinceMonday =
                ((int)throughLocal.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            localStart = throughLocal.Date.AddDays(-daysSinceMonday);
        }
        else
        {
            localStart = new DateTime(
                throughLocal.Year,
                throughLocal.Month,
                1,
                0,
                0,
                0,
                DateTimeKind.Unspecified);
        }

        var localOffset = TimeZoneInfo.Local.GetUtcOffset(localStart);
        return new DateTimeOffset(localStart, localOffset).ToUniversalTime();
    }

    private static IReadOnlyList<QuotaChartSample> BuildQuotaChartSamples(
        QuotaTrendDisplayData trendData,
        TimeSpan bucketDuration,
        bool trimLeadingUnusedBuckets = false,
        TimeSpan? leadingContextDuration = null)
    {
        var bucket = NormalizeTrendBucket(bucketDuration);
        var ordered = trendData.Points
            .OrderBy(point => point.TimestampUtc)
            .ToList();
        var stableFromUtc = trendData.FromUtc.ToUniversalTime();
        var normalizedThroughUtc = trendData.ThroughUtc.ToUniversalTime();
        var visibleTicks = Math.Max(0L, (normalizedThroughUtc - stableFromUtc).Ticks);
        var expectedCountLong = visibleTicks == 0L
            ? 1L
            : (visibleTicks + bucket.Ticks - 1L) / bucket.Ticks;
        var expectedCount = (int)Math.Clamp(expectedCountLong, 1L, 2_048L);
        var stableThroughUtc = stableFromUtc + TimeSpan.FromTicks(bucket.Ticks * expectedCount);

        var byBucket = ordered
            .Where(point => point.TimestampUtc >= stableFromUtc && point.TimestampUtc < stableThroughUtc)
            .GroupBy(point => AlignTrendBoundary(point.TimestampUtc, stableFromUtc, bucket))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var samples = new List<QuotaChartSample>(expectedCount);
        double? carriedRemainingPercent = null;
        for (var timestamp = stableFromUtc;
             timestamp < stableThroughUtc && samples.Count < expectedCount;
             timestamp += bucket)
        {
            if (!byBucket.TryGetValue(timestamp, out var points))
            {
                samples.Add(new QuotaChartSample(
                    timestamp,
                    0D,
                    carriedRemainingPercent,
                    0L,
                    bucket,
                    []));
                continue;
            }

            var remainingPercent = points
                .Where(point => point.RemainingPercent.HasValue)
                .OrderBy(point => point.TimestampUtc)
                .Select(point => point.RemainingPercent)
                .LastOrDefault();
            if (remainingPercent.HasValue)
            {
                carriedRemainingPercent = remainingPercent;
            }
            var totalCost = points.Sum(point => Math.Max(0D, point.ApiEquivalentCostUsd));
            var totalTokens = points.Sum(point => Math.Max(0L, point.TotalTokens));
            var modelUsage = points
                .SelectMany(point => point.ModelUsage ?? [])
                .GroupBy(
                    item => NormalizeModelDistributionName(item.Model),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new QuotaChartModelUsage(
                    group.Key,
                    group.Sum(item => Math.Max(0D, item.ApiEquivalentCostUsd)),
                    group.Sum(item => Math.Max(0L, item.TotalTokens)),
                    group.Sum(item => Math.Max(0, item.EventCount))))
                .OrderByDescending(item => item.ApiEquivalentCostUsd)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var classifiedCost = modelUsage.Sum(item => Math.Max(0D, item.ApiEquivalentCostUsd));
            if (classifiedCost + 0.000_000_001D < totalCost)
            {
                modelUsage.Add(new QuotaChartModelUsage(
                    "未识别模型",
                    totalCost - classifiedCost,
                    Math.Max(0L, totalTokens - modelUsage.Sum(item => Math.Max(0L, item.TotalTokens))),
                    points.Sum(point => Math.Max(0, point.EventCount))));
            }
            samples.Add(new QuotaChartSample(
                timestamp,
                totalCost,
                carriedRemainingPercent,
                totalTokens,
                bucket,
                modelUsage
                    .OrderByDescending(item => item.ApiEquivalentCostUsd)
                    .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
                    .ToArray()));
        }

        if (!trimLeadingUnusedBuckets)
        {
            return samples;
        }

        var firstDataIndex = samples.FindIndex(HasQuotaTrendUsageOrOfficialRemaining);
        if (firstDataIndex < 0)
        {
            // With no natural usage, an empty sample set lets the chart show its
            // explicit empty-state copy instead of drawing a full-width zero line.
            return [];
        }

        // Keep a short zero-use lead-in before the first real bucket.  Cropping all the way
        // to the first non-zero sample makes a new session look like a vertical wall at the
        // left edge, while keeping the whole selected range wastes most of the chart.  The
        // lead-in is proportional to each toolbar range through its bucket size: 5 minutes
        // for 1h, 30 minutes for 5h, 1 hour for today, 12 hours for this week and 2 days for
        // this month.
        var contextBucketCount = GetQuotaTrendLeadingContextBucketCount(
            bucket,
            leadingContextDuration ?? bucket);
        var visibleStartIndex = Math.Max(0, firstDataIndex - contextBucketCount);
        if (firstDataIndex >= contextBucketCount)
        {
            return visibleStartIndex == 0
                ? samples
                : samples.GetRange(visibleStartIndex, samples.Count - visibleStartIndex);
        }

        // A newly-started monitoring epoch can place its first real event in the very
        // first bucket. Extend the visual window backwards with zero-only context rather
        // than rendering that single point as an isolated bar. These synthetic buckets
        // carry no Token, cost, model, or official-percent data and never enter exports.
        var missingContextBuckets = contextBucketCount - firstDataIndex;
        var extended = new List<QuotaChartSample>(samples.Count + missingContextBuckets);
        var firstTimestamp = samples[0].Timestamp;
        for (var offset = missingContextBuckets; offset > 0; offset--)
        {
            extended.Add(new QuotaChartSample(
                firstTimestamp - TimeSpan.FromTicks(bucket.Ticks * offset),
                0D,
                null,
                0L,
                bucket,
                []));
        }
        extended.AddRange(samples);
        return extended;
    }

    private static bool HasQuotaTrendUsage(QuotaChartSample sample) =>
        sample.TotalTokens > 0L ||
        (double.IsFinite(sample.IncrementalCostUsd) && sample.IncrementalCostUsd > 0D) ||
        (sample.ModelUsage?.Any(item =>
            item.TotalTokens > 0L ||
            (double.IsFinite(item.ApiEquivalentCostUsd) && item.ApiEquivalentCostUsd > 0D) ||
            item.EventCount > 0) == true);

    private static bool HasQuotaTrendUsageOrOfficialRemaining(QuotaChartSample sample) =>
        HasQuotaTrendUsage(sample) ||
        (sample.RemainingPercent is { } remainingPercent && double.IsFinite(remainingPercent));

    private static bool ShouldTrimLeadingQuotaTrendBuckets(TimeSpan range) =>
        range > TimeSpan.Zero && range <= TimeSpan.FromDays(31);

    private static TimeSpan GetQuotaTrendLeadingContextDuration(TimeSpan range)
    {
        if (range <= TimeSpan.FromHours(1)) return TimeSpan.FromMinutes(5);
        if (range <= TimeSpan.FromHours(5)) return TimeSpan.FromMinutes(30);
        if (range <= TimeSpan.FromHours(24)) return TimeSpan.FromHours(1);
        if (range <= TimeSpan.FromDays(7)) return TimeSpan.FromHours(12);
        return TimeSpan.FromDays(2);
    }

    private static int GetQuotaTrendLeadingContextBucketCount(
        TimeSpan bucketDuration,
        TimeSpan leadingContextDuration)
    {
        var bucket = NormalizeTrendBucket(bucketDuration);
        var context = leadingContextDuration > TimeSpan.Zero
            ? leadingContextDuration
            : bucket;
        var count = (context.Ticks + bucket.Ticks - 1L) / bucket.Ticks;
        return (int)Math.Clamp(count, 1L, 2_048L);
    }

    private static DateTimeOffset AlignTrendBoundary(
        DateTimeOffset value,
        DateTimeOffset origin,
        TimeSpan bucketDuration)
    {
        var bucket = NormalizeTrendBucket(bucketDuration);
        var utc = value.ToUniversalTime();
        var normalizedOrigin = origin.ToUniversalTime();
        if (utc <= normalizedOrigin)
        {
            return normalizedOrigin;
        }

        var elapsedTicks = utc.UtcTicks - normalizedOrigin.UtcTicks;
        var bucketIndex = elapsedTicks / bucket.Ticks;
        return normalizedOrigin + TimeSpan.FromTicks(bucketIndex * bucket.Ticks);
    }

    private static TimeSpan NormalizeTrendBucket(TimeSpan value) =>
        value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(1);

    private static TimeSpan GetQuotaTrendBucketSize(TimeSpan range)
    {
        if (range <= TimeSpan.FromHours(1)) return TimeSpan.FromMinutes(5);
        if (range <= TimeSpan.FromHours(5)) return TimeSpan.FromMinutes(15);
        if (range <= TimeSpan.FromHours(24)) return TimeSpan.FromMinutes(15);
        if (range <= TimeSpan.FromDays(7)) return TimeSpan.FromHours(6);
        return TimeSpan.FromDays(1);
    }

    private static string GetQuotaTrendRangeLabel(TimeSpan range)
    {
        if (range <= TimeSpan.FromHours(1)) return "1 小时";
        if (range <= TimeSpan.FromHours(5)) return "5 小时";
        if (range <= TimeSpan.FromHours(24)) return "今天";
        if (range <= TimeSpan.FromDays(7)) return "本周";
        return "本月";
    }

    private static string GetModelDistributionRangeLabel(TimeSpan range)
    {
        if (range <= TimeSpan.FromHours(1)) return "1h";
        if (range <= TimeSpan.FromHours(5)) return "5h";
        if (range <= TimeSpan.FromHours(24)) return "今天";
        if (range <= TimeSpan.FromDays(7)) return "本周";
        return "本月";
    }

    private void ExportQuotaTrendCsv(
        AccountRecord account,
        IReadOnlyList<PassiveQuotaTrendPoint> trendPoints)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeAccountName = new string(account.Name
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        using var dialog = new SaveFileDialog
        {
            Title = "导出额度用量 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            DefaultExt = "csv",
            AddExtension = true,
            FileName = $"{safeAccountName}-usage-{DateTime.Now:yyyyMMdd-HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        File.WriteAllBytes(dialog.FileName, PassiveQuotaMonitor.ExportCsv(trendPoints));
        _statusBox.Text = $"已导出 {trendPoints.Count} 个时间段：{dialog.FileName}";
    }

    private Control MakeCompactQuotaUsageTable(
        int left,
        int top,
        int width,
        AccountUsageSummary usage,
        UsagePriceProfile priceProfile)
    {
        var items = new (string Label, UsageBucket Bucket)[]
        {
            ("1h", usage.Hour),
            ("5h", usage.FiveHours),
            ("今天", usage.Day),
            ("本周", usage.Week),
            ("本月", usage.Month)
        };
        const int rowHeight = 70;
        var table = new RoundedPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = 18 + (items.Length * rowHeight),
            Radius = 12,
            BackColor = _palette.SurfaceAltColor,
            BorderColor = _palette.BorderColor
        };
        var bindings = new List<QuotaUsageTableRowBinding>(items.Length);

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var row = new Panel
            {
                Left = 12,
                Top = 8 + (index * rowHeight),
                Width = width - 24,
                Height = rowHeight - 4,
                BackColor = Color.Transparent
            };
            table.Controls.Add(row);

            var window = MakeQuotaTableCell(item.Label, 0, 0, 54, row.Height, true, false);
            row.Controls.Add(window);
            var total = MakeQuotaTableCell(
                $"{FormatTokens(item.Bucket.TotalTokens)} token  ·  {FormatEstimatedCost(item.Bucket, priceProfile)}",
                62,
                2,
                row.Width - 68,
                30,
                true,
                false);
            row.Controls.Add(total);
            var split = MakeQuotaTableCell(
                FormatCompactQuotaUsageSplit(item.Bucket),
                62,
                31,
                row.Width - 68,
                28,
                false,
                true);
            row.Controls.Add(split);
            bindings.Add(new QuotaUsageTableRowBinding
            {
                Total = total,
                CompactSplit = split
            });
        }

        table.Tag = new QuotaUsageTableBinding { Rows = bindings.ToArray() };
        return table;
    }

    private static string FormatCompactQuotaUsageSplit(UsageBucket usage)
    {
        var parts = new List<string>(4)
        {
            $"输入 {FormatTokens(usage.InputTokens)}",
            $"读取 {FormatTokens(usage.CachedInputTokens)}"
        };
        if (usage.CacheWriteTokens > 0L)
        {
            parts.Add($"写入 {FormatTokens(usage.CacheWriteTokens)}");
        }
        parts.Add($"输出 {FormatTokens(usage.OutputTokens)}");
        return string.Join("  ·  ", parts);
    }

    private Control MakeQuotaSummaryTile(string caption, string value, string secondaryText, int left, int top, int width)
    {
        var panel = new RoundedPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = 98,
            Radius = 10,
            BackColor = _palette.SurfaceAltColor,
            BorderColor = _palette.BorderColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceAltColor, _palette.PrimaryColor, 0.04F)
        };

        var captionLabel = new Label
        {
            Text = caption,
            Left = 14,
            Top = 10,
            Width = width - 28,
            Height = 28,
            Font = new Font(Font.FontFamily, 9F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(captionLabel, _palette, true);
        panel.Controls.Add(captionLabel);

        var valueLabel = new Label
        {
            Text = value,
            Left = 14,
            Top = 40,
            Width = width - 28,
            Height = 34,
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(valueLabel, _palette);
        panel.Controls.Add(valueLabel);

        var secondary = new Label
        {
            Text = secondaryText,
            Left = 14,
            Top = 72,
            Width = width - 28,
            Height = 22,
            Font = new Font(Font.FontFamily, 7.4F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(secondary, _palette, true);
        panel.Controls.Add(secondary);
        panel.Tag = new UsageMetricBinding
        {
            Cost = valueLabel,
            Tokens = secondary
        };

        return panel;
    }

    private Control MakeQuotaUsageTable(int left, int top, int width, AccountUsageSummary usage, UsagePriceProfile priceProfile)
    {
        var rowHeight = 56;
        var headerHeight = 50;
        var table = new RoundedPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = headerHeight + (rowHeight * 5) + 22,
            Radius = 12,
            BackColor = _palette.SurfaceAltColor,
            BorderColor = _palette.BorderColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceAltColor, _palette.PrimaryColor, 0.018F)
        };

        var timeWidth = 110;
        var totalWidth = Math.Min(300, Math.Max(240, width / 4));
        var outputWidth = Math.Min(260, Math.Max(200, width / 5));
        var inputWidth = Math.Max(280, width - timeWidth - outputWidth - totalWidth - 44);
        var xTime = 16;
        var xInput = xTime + timeWidth;
        var xOutput = xInput + inputWidth;
        var xTotal = xOutput + outputWidth;
        var showCacheWrite = ShouldShowCacheWriteColumn(usage);

        table.Controls.Add(MakeQuotaTableCell("窗口", xTime, 14, timeWidth, 30, true, true));
        table.Controls.Add(MakeQuotaTableCell(
            showCacheWrite
                ? "输入：蓝=普通/待分，绿=读取，紫=写入"
                : "输入：蓝=普通/待分，绿=读取",
            xInput,
            14,
            inputWidth,
            30,
            true,
            true));
        const string costHeader = "估算金额";
        table.Controls.Add(MakeQuotaTableCell($"输出 / {costHeader}", xOutput, 14, outputWidth, 30, true, true));
        table.Controls.Add(MakeQuotaTableCell($"总 token / {costHeader}", xTotal, 14, totalWidth, 30, true, true));

        var bindings = new[]
        {
            AddQuotaUsageRow(table, "1h", usage.Hour, priceProfile, showCacheWrite, xTime, xInput, xOutput, xTotal, timeWidth, inputWidth, outputWidth, totalWidth, headerHeight, rowHeight),
            AddQuotaUsageRow(table, "5h", usage.FiveHours, priceProfile, showCacheWrite, xTime, xInput, xOutput, xTotal, timeWidth, inputWidth, outputWidth, totalWidth, headerHeight + rowHeight, rowHeight),
            AddQuotaUsageRow(table, "今天", usage.Day, priceProfile, showCacheWrite, xTime, xInput, xOutput, xTotal, timeWidth, inputWidth, outputWidth, totalWidth, headerHeight + (rowHeight * 2), rowHeight),
            AddQuotaUsageRow(table, "本周", usage.Week, priceProfile, showCacheWrite, xTime, xInput, xOutput, xTotal, timeWidth, inputWidth, outputWidth, totalWidth, headerHeight + (rowHeight * 3), rowHeight),
            AddQuotaUsageRow(table, "本月", usage.Month, priceProfile, showCacheWrite, xTime, xInput, xOutput, xTotal, timeWidth, inputWidth, outputWidth, totalWidth, headerHeight + (rowHeight * 4), rowHeight)
        };

        table.Tag = new QuotaUsageTableBinding { Rows = bindings };
        return table;
    }

    private QuotaUsageTableRowBinding AddQuotaUsageRow(
        Control table,
        string caption,
        UsageBucket usage,
        UsagePriceProfile priceProfile,
        bool showCacheWrite,
        int xTime,
        int xInput,
        int xOutput,
        int xTotal,
        int timeWidth,
        int inputWidth,
        int outputWidth,
        int totalWidth,
        int top,
        int height)
    {
        var row = new Panel
        {
            Left = 8,
            Top = top,
            Width = table.Width - 16,
            Height = height,
            BackColor = Color.Transparent
        };
        table.Controls.Add(row);

        row.Controls.Add(MakeQuotaTableCell(caption, xTime - row.Left, 0, timeWidth, height, true, false));
        var input = MakeQuotaInputCell(usage, priceProfile, showCacheWrite, xInput - row.Left, 0, inputWidth, height);
        row.Controls.Add(input);
        var output = MakeQuotaTableCell(FormatTokensWithUsd(usage.OutputTokens, EstimateOutputCost(usage, priceProfile)), xOutput - row.Left, 0, outputWidth, height, false, false);
        row.Controls.Add(output);
        var total = MakeQuotaTableCell($"{FormatTokens(usage.TotalTokens)}  {FormatEstimatedCost(usage, priceProfile)}", xTotal - row.Left, 0, totalWidth, height, true, false);
        row.Controls.Add(total);
        var inputLabels = (Label?[])input.Tag!;
        return new QuotaUsageTableRowBinding
        {
            RegularInput = inputLabels[0],
            CachedInput = inputLabels[1],
            CacheWrite = inputLabels[2],
            Output = output,
            Total = total
        };
    }

    private Control MakeQuotaInputCell(
        UsageBucket usage,
        UsagePriceProfile priceProfile,
        bool showCacheWrite,
        int left,
        int top,
        int width,
        int height)
    {
        var panel = new Panel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            BackColor = Color.Transparent
        };

        var normalizedInput = Math.Max(0L, usage.InputTokens);
        var cachedInput = Math.Clamp(usage.CachedInputTokens, 0L, normalizedInput);
        var cacheWrite = Math.Clamp(usage.CacheWriteTokens, 0L, normalizedInput - cachedInput);
        var regularInput = normalizedInput - cachedInput - cacheWrite;
        var gap = 6;
        var segmentCount = showCacheWrite ? 3 : 2;
        var segmentWidth = Math.Max(82, (width - (gap * (segmentCount - 1))) / segmentCount);
        var uncached = MakeQuotaSegmentLabel(
            $"{FormatTokens(regularInput)}  {FormatUsd(EstimateRegularInputCost(usage, priceProfile))}",
            0,
            0,
            segmentWidth,
            height,
            _palette.PrimaryColor,
            false);
        panel.Controls.Add(uncached);

        var cached = MakeQuotaSegmentLabel(
            $"{FormatTokens(cachedInput)}  {FormatUsd(EstimateCachedInputCost(usage, priceProfile))}",
            segmentWidth + gap,
            0,
            segmentWidth,
            height,
            _palette.SuccessColor,
            false);
        panel.Controls.Add(cached);

        Label? cacheWriteLabel = null;
        if (showCacheWrite)
        {
            var cacheWriteText = cacheWrite > 0L
                ? $"{FormatTokens(cacheWrite)}  {FormatUsd(EstimateCacheWriteCost(usage, priceProfile))}"
                : string.Empty;
            cacheWriteLabel = MakeQuotaSegmentLabel(
                cacheWriteText,
                (segmentWidth + gap) * 2,
                0,
                Math.Max(76, width - ((segmentWidth + gap) * 2)),
                height,
                _palette.SecondaryAccentColor,
                false);
            panel.Controls.Add(cacheWriteLabel);
            _toolTip.SetToolTip(cacheWriteLabel, GetCacheWriteStatusDescription(usage, priceProfile));
        }

        _toolTip.SetToolTip(uncached, "普通输入；缓存写入未上报的部分也暂按普通输入价计入基础估算。");
        _toolTip.SetToolTip(cached, "缓存读取（cache read / cached input）。");
        panel.Tag = new Label?[] { uncached, cached, cacheWriteLabel };

        return panel;
    }

    private Label MakeQuotaSegmentLabel(string text, int left, int top, int width, int height, Color color, bool bold)
    {
        var label = new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Font = new Font(Font.FontFamily, 8.8F, bold ? FontStyle.Bold : FontStyle.Regular),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            ForeColor = color
        };
        return label;
    }

    private Label MakeQuotaTableCell(string text, int left, int top, int width, int height, bool bold, bool muted)
    {
        var label = new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Font = new Font(Font.FontFamily, muted ? 8.3F : 8.9F, bold ? FontStyle.Bold : FontStyle.Regular),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };
        ThemeStyler.ApplyLabel(label, _palette, muted);
        return label;
    }

    private Color GetUsageMetricAccent(string caption) => caption switch
    {
        "5h" => _palette.PrimaryColor,
        "今天" => _palette.SecondaryAccentColor,
        "本周" => _palette.TertiaryAccentColor,
        "本月" => _palette.AccentColor,
        _ => UiDesign.Blend(_palette.PrimaryColor, _palette.SecondaryAccentColor, 0.45F)
    };

    private Control MakePassiveQuotaUsageMetric(
        string caption,
        UsageBucket usage,
        UsagePriceProfile priceProfile,
        int left,
        int top,
        int width,
        int height,
        out UsageMetricBinding binding)
    {
        var accent = GetUsageMetricAccent(caption);
        var panel = new RoundedPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Radius = 12,
            BackColor = _palette.SurfaceAltColor,
            BorderColor = UiDesign.Blend(_palette.BorderColor, accent, 0.22F),
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceAltColor, accent, 0.07F),
            AccentColor = accent,
            AccentWidth = 2,
            ShadowColor = Color.FromArgb(14, _palette.ShadowColor),
            Elevation = 1,
            ShowTechDecoration = false
        };

        var captionLabel = new PillLabel
        {
            Text = caption,
            Left = 10,
            Top = 8,
            Width = Math.Min(Math.Max(48, width - 20), caption.Length >= 3 ? 70 : 58),
            Height = 24,
            Font = new Font(Font.FontFamily, 7.7F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            FillColor = Color.FromArgb(30, accent),
            StrokeColor = Color.FromArgb(76, accent),
            ForeColor = accent
        };
        panel.Controls.Add(captionLabel);

        var costHeight = height >= 100 ? 38 : 34;
        var cost = new Label
        {
            Text = FormatEstimatedCost(usage, priceProfile),
            Left = 10,
            Top = 32,
            Width = width - 20,
            Height = costHeight,
            Font = new Font(Font.FontFamily, height >= 100 ? 10.8F : 10F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(cost, _palette);
        panel.Controls.Add(cost);

        var tokenTop = cost.Top + cost.Height;
        var tokens = new Label
        {
            Text = $"{FormatTokens(usage.TotalTokens)} token",
            Left = 10,
            Top = tokenTop,
            Width = width - 20,
            Height = Math.Max(18, height - tokenTop - 4),
            Font = new Font(Font.FontFamily, height >= 100 ? 7.8F : 7.3F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(tokens, _palette, true);
        panel.Controls.Add(tokens);

        binding = new UsageMetricBinding
        {
            Cost = cost,
            Tokens = tokens
        };
        return panel;
    }

    private Control MakeUsageMetric(
        string caption,
        UsageBucket usage,
        UsagePriceProfile priceProfile,
        int left,
        int top,
        int width,
        out UsageMetricBinding binding)
    {
        var accent = GetUsageMetricAccent(caption);
        var panel = new RoundedPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = 134,
            Radius = 14,
            BackColor = _palette.CardColor,
            BorderColor = UiDesign.Blend(_palette.BorderColor, accent, 0.24F),
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.CardColor, accent, 0.075F),
            AccentColor = accent,
            AccentWidth = 3,
            ShadowColor = Color.FromArgb(24, _palette.ShadowColor),
            Elevation = 2
        };

        var captionLabel = new PillLabel
        {
            Text = caption,
            Left = 12,
            Top = 10,
            Width = Math.Min(width - 24, caption.Length >= 3 ? 76 : 64),
            Height = 30,
            Font = new Font(Font.FontFamily, 8.7F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleCenter,
            UseCompatibleTextRendering = true,
            UseMnemonic = false,
            FillColor = Color.FromArgb(34, accent),
            StrokeColor = Color.FromArgb(86, accent),
            ForeColor = accent
        };
        panel.Controls.Add(captionLabel);

        var total = new Label
        {
            Text = FormatEstimatedCost(usage, priceProfile),
            Left = 12,
            Top = 43,
            Width = width - 24,
            Height = 44,
            Font = new Font(Font.FontFamily, 12.4F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(total, _palette);
        panel.Controls.Add(total);

        var token = new Label
        {
            Text = $"{FormatTokens(usage.TotalTokens)} token",
            Left = 12,
            Top = 92,
            Width = width - 24,
            Height = 30,
            Font = new Font(Font.FontFamily, 8F, FontStyle.Bold),
            AutoEllipsis = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(token, _palette, true);
        panel.Controls.Add(token);

        binding = new UsageMetricBinding
        {
            Cost = total,
            Tokens = token
        };

        return panel;
    }

    private Control MakeQuotaDetailMetric(string caption, UsageBucket usage, int left, int top, int width)
    {
        var panel = new RoundedPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = 134,
            Radius = 10,
            BackColor = _palette.SurfaceAltColor,
            BorderColor = _palette.BorderColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceAltColor, _palette.AccentColor, 0.032F)
        };

        var title = new Label
        {
            Text = caption,
            Left = 14,
            Top = 12,
            Width = width - 28,
            Height = 26,
            Font = new Font(Font.FontFamily, 9.2F, FontStyle.Bold),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(title, _palette, true);
        panel.Controls.Add(title);

        var total = new Label
        {
            Text = $"总 token：{FormatTokens(usage.TotalTokens)}",
            Left = 14,
            Top = 42,
            Width = width - 28,
            Height = 28,
            Font = new Font(Font.FontFamily, 9.8F, FontStyle.Bold),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(total, _palette);
        panel.Controls.Add(total);

        var input = new Label
        {
            Text = usage.CacheWriteTokens > 0L
                ? $"输入：{FormatTokens(usage.InputTokens)} · 写入：{FormatTokens(usage.CacheWriteTokens)}"
                : $"输入：{FormatTokens(usage.InputTokens)}",
            Left = 14,
            Top = 76,
            Width = width - 28,
            Height = 24,
            Font = new Font(Font.FontFamily, 8.6F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(input, _palette, true);
        panel.Controls.Add(input);

        var output = new Label
        {
            Text = $"输出：{FormatTokens(usage.OutputTokens)}",
            Left = 14,
            Top = 102,
            Width = width - 28,
            Height = 24,
            Font = new Font(Font.FontFamily, 8.6F),
            AutoEllipsis = true
        };
        ThemeStyler.ApplyLabel(output, _palette, true);
        panel.Controls.Add(output);

        return panel;
    }

    private Control MakeInfoRow(string caption, string value, int left, int top, int width)
    {
        var panel = new Panel { Left = left, Top = top, Width = width, Height = 88, BackColor = Color.Transparent };

        var captionLabel = new Label
        {
            Text = caption,
            Left = 0,
            Top = 0,
            Width = width - 20,
            Height = 36,
            Font = new Font(Font.FontFamily, 9.4F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(captionLabel, _palette, true);
        panel.Controls.Add(captionLabel);

        var valueLabel = new Label
        {
            Text = value,
            Left = 0,
            Top = 44,
            Width = width - 4,
            Height = 38,
            Font = new Font(Font.FontFamily, 9.4F),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(valueLabel, _palette);
        panel.Controls.Add(valueLabel);

        return panel;
    }

    private Control MakeMetric(string caption, string value, int left, int top, int width)
    {
        var panel = new RoundedPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = 96,
            Radius = 10,
            BackColor = _palette.SurfaceAltColor,
            BorderColor = _palette.BorderColor,
            UseGradient = true,
            GradientColor = UiDesign.Blend(_palette.SurfaceAltColor, _palette.PrimaryColor, 0.04F)
        };

        var captionLabel = new Label
        {
            Text = caption,
            Left = 10,
            Top = 12,
            Width = width - 20,
            Height = 36,
            Font = new Font(Font.FontFamily, 9F),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(captionLabel, _palette, true);
        panel.Controls.Add(captionLabel);

        var valueLabel = new Label
        {
            Text = value,
            Left = 10,
            Top = 56,
            Width = width - 20,
            Height = 34,
            Font = new Font(Font.FontFamily, 9.6F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        ThemeStyler.ApplyLabel(valueLabel, _palette);
        panel.Controls.Add(valueLabel);

        return panel;
    }

    private Color GetAccountStateColor(AccountRecord account)
    {
        if (IsCurrentAccount(account))
        {
            return _palette.SuccessColor;
        }

        if (_statusCache.TryGetValue(account.Name, out var status) && status.ExitCode != 0)
        {
            return _palette.WarningColor;
        }

        return UiDesign.Blend(_palette.MutedTextColor, _palette.CardColor, 0.42F);
    }

    private static void ValidateQuotaResetLayoutAtScale(float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0F)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        static int ScalePixels(int value, float factor) =>
            (int)Math.Ceiling(value * factor);

        using var font = new Font(SystemFonts.DefaultFont.FontFamily, 8F * scale);
        var textSize = MeasureQuotaResetText(font);
        var lineHeight = Math.Max(
            ScalePixels(34, scale),
            textSize.Height + ScalePixels(12, scale));
        var lineGap = ScalePixels(2, scale);
        var actionGap = ScalePixels(10, scale);
        var bottomPadding = ScalePixels(14, scale);
        var actionHeight = ScalePixels(34, scale);
        var resetAreaTop = ScalePixels(106, scale);
        var secondLineTop = resetAreaTop + lineHeight + lineGap;
        var secondLineBottom = secondLineTop + lineHeight;
        var actionTop = secondLineBottom + actionGap;
        var rowHeight = actionTop + actionHeight + bottomPadding;
        var wideLabelWidth = ScalePixels(280, scale);
        var stackedLabelWidth = ScalePixels(AccountRowMinWidth - 36, scale);
        var horizontalInset = ScalePixels(8, scale);

        if (textSize.Width + horizontalInset > Math.Min(wideLabelWidth, stackedLabelWidth) ||
            lineHeight < textSize.Height + ScalePixels(12, scale) ||
            secondLineTop < resetAreaTop + lineHeight ||
            secondLineBottom + actionGap > actionTop ||
            actionTop + actionHeight + bottomPadding > rowHeight)
        {
            throw new InvalidOperationException(
                $"Quota reset layout clips at {scale * 100F:0}% DPI: " +
                $"text={textSize}, line={lineHeight}, wide={wideLabelWidth}, " +
                $"stacked={stackedLabelWidth}, row={rowHeight}.");
        }
    }

    public static void ValidateUsagePricing()
    {
        var shortUsage = new UsageBucket();
        foreach (var model in new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" })
        {
            shortUsage.Add(new UsageEvent
            {
                Model = model,
                InputTokens = 200_000,
                CachedInputTokens = 40_000,
                CacheWriteTokens = 80_000,
                OutputTokens = 20_000,
                TotalTokens = 220_000
            });
        }

        var longUsage = new UsageBucket();
        foreach (var model in new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" })
        {
            longUsage.Add(new UsageEvent
            {
                Model = model,
                InputTokens = 1_000_000,
                CachedInputTokens = 200_000,
                CacheWriteTokens = 400_000,
                OutputTokens = 100_000,
                TotalTokens = 1_100_000
            });
        }

        var fallback = new UsagePriceProfile("fallback", 99D, 99D, 99D, 199D, 199D, 199D);
        var shortEstimate = EstimateTotalCost(shortUsage, fallback);
        var longEstimate = EstimateTotalCost(longUsage, fallback);
        var gpt55ShortUsage = new UsageBucket();
        gpt55ShortUsage.Add(new UsageEvent
        {
            Model = "gpt-5.5",
            InputTokens = 200_000,
            CachedInputTokens = 40_000,
            OutputTokens = 20_000,
            TotalTokens = 220_000
        });
        var gpt55LongUsage = new UsageBucket();
        gpt55LongUsage.Add(new UsageEvent
        {
            Model = "gpt-5.5",
            InputTokens = 1_000_000,
            CachedInputTokens = 200_000,
            OutputTokens = 100_000,
            TotalTokens = 1_100_000
        });
        var mixedContextUsage = new UsageBucket();
        mixedContextUsage.Add(new UsageEvent
        {
            Model = "gpt-5.6-terra",
            InputTokens = 200_000,
            CachedInputTokens = 40_000,
            OutputTokens = 20_000,
            TotalTokens = 220_000
        });
        mixedContextUsage.Add(new UsageEvent
        {
            Model = "gpt-5.6-terra",
            InputTokens = 1_000_000,
            CachedInputTokens = 200_000,
            OutputTokens = 100_000,
            TotalTokens = 1_100_000
        });
        var accessTokenFallback = GetUsagePriceProfile(new AccountRecord
        {
            AuthKind = AccountAuthKind.AccessToken
        });
        var compatibleApiFallback = GetUsagePriceProfile(new AccountRecord
        {
            AuthKind = AccountAuthKind.CompatibleApi,
            ApiModel = "gpt-5.6-sol"
        });
        var spacedCompatibleSolProfile = GetUsagePriceProfile(new AccountRecord
        {
            AuthKind = AccountAuthKind.CompatibleApi,
            ApiModel = "gpt-5.6 sol"
        });
        var unknownModelUsage = new UsageBucket();
        unknownModelUsage.Add(new UsageEvent
        {
            InputTokens = 200_000,
            CachedInputTokens = 40_000,
            OutputTokens = 20_000,
            TotalTokens = 220_000
        });
        var unknownCacheWriteUsage = new UsageBucket();
        unknownCacheWriteUsage.Add(new UsageEvent
        {
            Model = "gpt-5.6-terra",
            InputTokens = 200_000,
            CachedInputTokens = 40_000,
            OutputTokens = 20_000,
            TotalTokens = 220_000
        });
        var fixedCostUsage = new UsageBucket();
        var fixedCostEvent = new UsageEvent
        {
            Model = "gpt-5.6-sol",
            InputTokens = 100_000L,
            CachedInputTokens = 20_000L,
            CacheWriteTokens = 10_000L,
            OutputTokens = 5_000L,
            TotalTokens = 105_000L,
            EquivalentCostOverrideUsd = 0.01D
        };
        fixedCostUsage.Add(fixedCostEvent);
        var fixedCostDistribution = BuildModelUsageDistribution(fixedCostUsage, fallback);
        var mixedDistribution = BuildModelUsageDistribution(mixedContextUsage, fallback);
        static UsageEvent MakePricingFixture(string model, bool longContext) => new()
        {
            Model = model,
            InputTokens = longContext ? 1_000_000 : 200_000,
            CachedInputTokens = longContext ? 200_000 : 40_000,
            CacheWriteTokens = model.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase)
                ? longContext ? 400_000 : 80_000
                : null,
            OutputTokens = longContext ? 100_000 : 20_000,
            TotalTokens = longContext ? 1_100_000 : 220_000
        };
        var officialShortByModel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = EstimateUsageEventCost(MakePricingFixture("gpt-5.6-sol", false), fallback),
            ["gpt-5.6-terra"] = EstimateUsageEventCost(MakePricingFixture("gpt-5.6-terra", false), fallback),
            ["gpt-5.6-luna"] = EstimateUsageEventCost(MakePricingFixture("gpt-5.6-luna", false), fallback),
            ["gpt-5.5"] = EstimateUsageEventCost(MakePricingFixture("gpt-5.5", false), fallback)
        };
        var officialLongByModel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = EstimateUsageEventCost(MakePricingFixture("gpt-5.6-sol", true), fallback),
            ["gpt-5.6-terra"] = EstimateUsageEventCost(MakePricingFixture("gpt-5.6-terra", true), fallback),
            ["gpt-5.6-luna"] = EstimateUsageEventCost(MakePricingFixture("gpt-5.6-luna", true), fallback),
            ["gpt-5.5"] = EstimateUsageEventCost(MakePricingFixture("gpt-5.5", true), fallback)
        };
        var compatibleLongByModel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = EstimateUsageEventCost(
                MakePricingFixture("gpt-5.6-sol", true),
                compatibleApiFallback),
            ["gpt-5.6-terra"] = EstimateUsageEventCost(
                MakePricingFixture("gpt-5.6-terra", true),
                compatibleApiFallback),
            ["gpt-5.6-luna"] = EstimateUsageEventCost(
                MakePricingFixture("gpt-5.6-luna", true),
                compatibleApiFallback),
            ["gpt-5.5"] = EstimateUsageEventCost(
                MakePricingFixture("gpt-5.5", true),
                compatibleApiFallback)
        };
        var officialMiniCost = EstimateUsageEventCost(
            MakePricingFixture("gpt-5.4-mini", false),
            accessTokenFallback);
        var compatibleMiniCost = EstimateUsageEventCost(
            MakePricingFixture("gpt-5.4-mini", false),
            compatibleApiFallback);
        var sub2ApiCsvTerraFixture = new UsageEvent
        {
            Model = "gpt-5.6-terra",
            InputTokens = 309_953,
            CachedInputTokens = 301_312,
            CacheWriteTokens = 0,
            OutputTokens = 3_562,
            TotalTokens = 313_515
        };
        var officialCsvTerraCost = EstimateUsageEventCost(
            sub2ApiCsvTerraFixture,
            accessTokenFallback);
        var compatibleCsvTerraCost = EstimateUsageEventCost(
            sub2ApiCsvTerraFixture,
            compatibleApiFallback);
        const double expectedShort = 2.1888D;
        const double expectedLong = 13.628D;
        var resetUsage = new AccountUsageSummary
        {
            RateLimitUsedPercent = 10D,
            RateLimitWindowMinutes = 300,
            RateLimitResetAtUtc = new DateTimeOffset(2026, 7, 12, 9, 53, 0, TimeSpan.Zero),
            SecondaryRateLimitUsedPercent = 20D,
            SecondaryRateLimitWindowMinutes = 10_080,
            SecondaryRateLimitResetAtUtc = new DateTimeOffset(2026, 7, 19, 11, 10, 0, TimeSpan.Zero)
        };
        var weeklyRowDetail = GetQuotaRowDetailText(
            false,
            AccountQuotaLimitType.FiveHourAndWeekly,
            resetUsage);
        var (primaryResetLine, secondaryResetLine) = GetQuotaRowDetailLines(
            false,
            AccountQuotaLimitType.FiveHourAndWeekly,
            resetUsage);
        var weeklyOnlyUsage = new AccountUsageSummary
        {
            RateLimitUsedPercent = 49D,
            RateLimitWindowMinutes = 10_080,
            RateLimitResetAtUtc = new DateTimeOffset(2026, 7, 20, 11, 10, 0, TimeSpan.Zero)
        };
        var (weeklyOnlyPrimaryLine, weeklyOnlySecondaryLine) = GetQuotaRowDetailLines(
            false,
            AccountQuotaLimitType.WeeklyOnly,
            weeklyOnlyUsage);
        var monthlyAccount = new AccountRecord
        {
            AuthKind = AccountAuthKind.AccessToken,
            QuotaLimitType = AccountQuotaLimitType.Monthly
        };
        var weeklyAccount = new AccountRecord
        {
            AuthKind = AccountAuthKind.AccessToken,
            QuotaLimitType = AccountQuotaLimitType.WeeklyOnly
        };
        var apiAccount = new AccountRecord { AuthKind = AccountAuthKind.CompatibleApi };
        var monthlyMetrics = GetQuotaUsageMetrics(monthlyAccount, AccountQuotaLimitType.Monthly, resetUsage);
        var weeklyMetrics = GetQuotaUsageMetrics(weeklyAccount, AccountQuotaLimitType.WeeklyOnly, resetUsage);
        var apiMetrics = GetQuotaUsageMetrics(apiAccount, AccountQuotaLimitType.Unknown, resetUsage);
        var listMetrics = GetQuotaListUsageMetrics(resetUsage);
        var localFixture = new DateTime(2026, 7, 15, 14, 30, 0, DateTimeKind.Unspecified);
        var fixtureOffset = TimeZoneInfo.Local.GetUtcOffset(localFixture);
        var fixtureThroughUtc = new DateTimeOffset(localFixture, fixtureOffset).ToUniversalTime();
        PassiveQuotaAssessmentWindow[] assessmentScopeFixture =
        [
            new(
                fixtureThroughUtc.AddHours(-3),
                fixtureThroughUtc.AddHours(-2),
                20,
                22,
                160D,
                200D,
                PassiveQuotaStatus.Abnormal),
            new(
                fixtureThroughUtc.AddHours(-2),
                fixtureThroughUtc.AddHours(-1),
                22,
                24,
                240D,
                200D,
                PassiveQuotaStatus.Normal),
            new(
                fixtureThroughUtc.AddDays(-2),
                fixtureThroughUtc.AddDays(-2).AddHours(1),
                10,
                12,
                150D,
                200D,
                PassiveQuotaStatus.Abnormal)
        ];
        var monitoringAssessmentWindows = SelectQuotaTrendAssessmentWindows(
            QuotaTrendScope.Monitoring,
            hasMonitoringEpoch: true,
            assessmentScopeFixture,
            fixtureThroughUtc.AddHours(-4),
            fixtureThroughUtc);
        var realtimeAssessmentWindows = SelectQuotaTrendAssessmentWindows(
            QuotaTrendScope.Realtime,
            hasMonitoringEpoch: true,
            assessmentScopeFixture,
            fixtureThroughUtc.AddHours(-4),
            fixtureThroughUtc);
        var noEpochAssessmentWindows = SelectQuotaTrendAssessmentWindows(
            QuotaTrendScope.Monitoring,
            hasMonitoringEpoch: false,
            assessmentScopeFixture,
            fixtureThroughUtc.AddHours(-4),
            fixtureThroughUtc);
        var todayStartLocal = GetQuotaTrendStartUtc(
            TimeSpan.FromHours(24),
            fixtureThroughUtc).ToLocalTime();
        var weekStartLocal = GetQuotaTrendStartUtc(
            TimeSpan.FromDays(7),
            fixtureThroughUtc).ToLocalTime();
        var monthStartLocal = GetQuotaTrendStartUtc(
            TimeSpan.FromDays(30),
            fixtureThroughUtc).ToLocalTime();
        var emptyHourlySamples = BuildQuotaChartSamples(
            new QuotaTrendDisplayData(
                [],
                "",
                fixtureThroughUtc.AddHours(-3),
                fixtureThroughUtc),
            TimeSpan.FromHours(1));
        var todayHourlySamples = BuildQuotaChartSamples(
            new QuotaTrendDisplayData(
                [],
                "",
                GetQuotaTrendStartUtc(TimeSpan.FromHours(24), fixtureThroughUtc),
                fixtureThroughUtc),
            TimeSpan.FromMinutes(15));
        var todayStartUtc = GetQuotaTrendStartUtc(TimeSpan.FromHours(24), fixtureThroughUtc);
        var todayUsagePoints = new PassiveQuotaTrendPoint[]
        {
            new(
                todayStartUtc.AddHours(10).AddMinutes(18),
                "fixture",
                "monthly",
                null,
                null,
                20D,
                80D,
                900L,
                100L,
                200L,
                0L,
                1_100L,
                1.25D,
                1.25D,
                "natural",
                "gpt-5.6-sol",
                1),
            new(
                todayStartUtc.AddHours(12).AddMinutes(7),
                "fixture",
                "monthly",
                null,
                null,
                21D,
                79D,
                600L,
                50L,
                100L,
                0L,
                700L,
                0.75D,
                2D,
                "natural",
                "gpt-5.6-terra",
                1)
        };
        var trimmedTodaySamples = BuildQuotaChartSamples(
            new QuotaTrendDisplayData(
                todayUsagePoints,
                "",
                todayStartUtc,
                fixtureThroughUtc),
            TimeSpan.FromMinutes(15),
            trimLeadingUnusedBuckets: true,
            leadingContextDuration: TimeSpan.FromHours(1));
        var emptyTrimmedTodaySamples = BuildQuotaChartSamples(
            new QuotaTrendDisplayData(
                [],
                "这个时间段还没有自然使用记录",
                todayStartUtc,
                fixtureThroughUtc),
            TimeSpan.FromMinutes(15),
            trimLeadingUnusedBuckets: true,
            leadingContextDuration: TimeSpan.FromHours(1));
        // Regression: a newly-started monitoring epoch can put the first natural-usage
        // event in samples[0].  "今天" must still show one hour of visual context: four
        // synthetic 15-minute buckets with no cost, Token, model, or official percentage.
        // The synthetic samples belong to the chart only; CSV export continues to receive
        // the original PassiveQuotaTrendPoint collection.
        var firstBucketFromUtc = todayStartUtc.AddHours(10).AddMinutes(18);
        var firstBucketTrendPoints = new PassiveQuotaTrendPoint[]
        {
            todayUsagePoints[0] with
            {
                TimestampUtc = firstBucketFromUtc.AddMinutes(1),
                UsedPercent = 22D,
                RemainingPercent = 78D
            }
        };
        var firstBucketTrendData = new QuotaTrendDisplayData(
            firstBucketTrendPoints,
            "",
            firstBucketFromUtc,
            firstBucketFromUtc.AddMinutes(45));
        var firstBucketUntrimmedSamples = BuildQuotaChartSamples(
            firstBucketTrendData,
            TimeSpan.FromMinutes(15));
        var firstBucketTrimmedSamples = BuildQuotaChartSamples(
            firstBucketTrendData,
            TimeSpan.FromMinutes(15),
            trimLeadingUnusedBuckets: true,
            leadingContextDuration: TimeSpan.FromHours(1));
        var firstBucketExportCsv = Encoding.UTF8.GetString(
            PassiveQuotaMonitor.ExportCsv(firstBucketTrendPoints));
        var firstBucketExportRows = firstBucketExportCsv
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (firstBucketUntrimmedSamples.Count != 3 ||
            !HasQuotaTrendUsage(firstBucketUntrimmedSamples[0]) ||
            firstBucketTrimmedSamples.Count != firstBucketUntrimmedSamples.Count + 4 ||
            firstBucketTrimmedSamples.Take(4).Where((sample, index) =>
                sample.Timestamp != firstBucketFromUtc.AddMinutes(-60 + (index * 15)) ||
                sample.BucketDuration != TimeSpan.FromMinutes(15) ||
                sample.IncrementalCostUsd != 0D ||
                sample.TotalTokens != 0L ||
                sample.RemainingPercent.HasValue ||
                (sample.ModelUsage?.Count ?? 0) != 0).Any() ||
            firstBucketTrimmedSamples[4].Timestamp != firstBucketFromUtc ||
            !HasQuotaTrendUsage(firstBucketTrimmedSamples[4]) ||
            firstBucketTrimmedSamples[4].RemainingPercent != 78D ||
            firstBucketTrendPoints.Length != 1 ||
            firstBucketExportRows.Length != 2 ||
            !firstBucketExportRows[1].StartsWith(
                "\"" + firstBucketTrendPoints[0].TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "\",",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Quota trend leading-context validation failed: the first chart bucket must gain four visual-only 15-minute zero buckets without changing CSV export rows. " +
                $"Untrimmed={firstBucketUntrimmedSamples.Count}, trimmed={firstBucketTrimmedSamples.Count}, " +
                $"firstUntrimmed={(firstBucketUntrimmedSamples.Count > 0 ? firstBucketUntrimmedSamples[0].Timestamp.ToString("O", CultureInfo.InvariantCulture) : "none")}, " +
                $"firstTrimmed={(firstBucketTrimmedSamples.Count > 0 ? firstBucketTrimmedSamples[0].Timestamp.ToString("O", CultureInfo.InvariantCulture) : "none")}, " +
                $"realRemaining={(firstBucketTrimmedSamples.Count > 4 ? firstBucketTrimmedSamples[4].RemainingPercent?.ToString(CultureInfo.InvariantCulture) ?? "null" : "missing")}, " +
                $"csvRows={firstBucketExportRows.Length}.");
        }
        IReadOnlyList<QuotaChartSample> structurallyEqualFirst =
        [
            new QuotaChartSample(
                fixtureThroughUtc,
                1.25D,
                76D,
                123_456L,
                TimeSpan.FromHours(1),
                [new QuotaChartModelUsage("gpt-5.6-sol", 1.25D, 123_456L, 2)])
        ];
        IReadOnlyList<QuotaChartSample> structurallyEqualSecond =
        [
            new QuotaChartSample(
                fixtureThroughUtc,
                1.25D,
                76D,
                123_456L,
                TimeSpan.FromHours(1),
                [new QuotaChartModelUsage("gpt-5.6-sol", 1.25D, 123_456L, 2)])
        ];
        var projectionState = new PassiveQuotaMonitoringState(
            "fixture",
            "fixture",
            true,
            "fixture-epoch",
            fixtureThroughUtc.AddHours(-1),
            null,
            null,
            null,
            0,
            43_200,
            248.33D,
            "monthly",
            fixtureThroughUtc.AddDays(1));
        var projectionEstimate = new PassiveQuotaEstimate(
            PassiveQuotaStatus.Normal,
            "monthly",
            43_200,
            200D,
            248.33D,
            null,
            null,
            166.3811D,
            null,
            null,
            33D,
            67D,
            2D,
            2,
            2,
            1,
            1,
            fixtureThroughUtc,
            "fixture",
            []);
        var projectionMonitoring = new PassiveQuotaMonitoringResult(
            projectionState,
            projectionEstimate,
            false,
            "fixture");
        var projectionAt67 = GetDisplayedQuotaRemainingUsd(projectionMonitoring, 67D);
        var projectionAt69 = GetDisplayedQuotaRemainingUsd(projectionMonitoring, 69D);
        var projectionTextAt67 = GetPassiveQuotaSummaryText(projectionMonitoring, 67D);
        var projectionTextAt69 = GetPassiveQuotaSummaryText(projectionMonitoring, 69D);
        ValidateQuotaResetLayoutAtScale(1F);
        ValidateQuotaResetLayoutAtScale(1.5F);
        ValidateQuotaResetLayoutAtScale(2F);
        ModelUsageDistributionControl.ValidateResponsiveLayout();
        if (OfficialQuotaFocusedRefreshInterval != TimeSpan.FromSeconds(15) ||
            OfficialQuotaBackgroundRefreshInterval != TimeSpan.FromMinutes(1) ||
            shortUsage.ModelUsage.Count != 3 ||
            longUsage.ModelUsage.Count != 3 ||
            shortUsage.CacheWriteTokens != 240_000L ||
            shortUsage.CacheWriteKnownEvents != 3 ||
            shortUsage.CacheWriteUnknownEvents != 0 ||
            Math.Abs(shortEstimate - expectedShort) > 0.000_001D ||
            Math.Abs(longEstimate - expectedLong) > 0.000_001D ||
            Math.Abs(officialShortByModel["gpt-5.6-sol"] - 1.52D) > 0.000_001D ||
            Math.Abs(officialShortByModel["gpt-5.6-terra"] - 0.608D) > 0.000_001D ||
            Math.Abs(officialShortByModel["gpt-5.6-luna"] - 0.0608D) > 0.000_001D ||
            Math.Abs(officialShortByModel["gpt-5.5"] - 1.42D) > 0.000_001D ||
            Math.Abs(officialLongByModel["gpt-5.6-sol"] - 7.6D) > 0.000_001D ||
            Math.Abs(officialLongByModel["gpt-5.6-terra"] - 5.48D) > 0.000_001D ||
            Math.Abs(officialLongByModel["gpt-5.6-luna"] - 0.548D) > 0.000_001D ||
            Math.Abs(officialLongByModel["gpt-5.5"] - 7.1D) > 0.000_001D ||
            Math.Abs(compatibleLongByModel["gpt-5.6-sol"] - 7.6D) > 0.000_001D ||
            Math.Abs(compatibleLongByModel["gpt-5.6-terra"] - 5.48D) > 0.000_001D ||
            Math.Abs(compatibleLongByModel["gpt-5.6-luna"] - 0.548D) > 0.000_001D ||
            Math.Abs(compatibleLongByModel["gpt-5.5"] - 7.1D) > 0.000_001D ||
            Math.Abs(officialMiniCost - 1.42D) > 0.000_001D ||
            Math.Abs(compatibleMiniCost - 1.42D) > 0.000_001D ||
            Math.Abs(officialCsvTerraCost - 0.219_204_8D) > 0.000_001D ||
            Math.Abs(compatibleCsvTerraCost - 0.219_204_8D) > 0.000_001D ||
            Math.Abs((officialShortByModel["gpt-5.6-terra"] * 2.5D) - officialShortByModel["gpt-5.6-sol"]) > 0.000_001D ||
            Math.Abs((officialShortByModel["gpt-5.6-luna"] * 25D) - officialShortByModel["gpt-5.6-sol"]) > 0.000_001D ||
            Math.Abs(EstimateTotalCost(gpt55ShortUsage, fallback) - 1.42D) > 0.000_001D ||
            Math.Abs(EstimateTotalCost(gpt55LongUsage, fallback) - 7.1D) > 0.000_001D ||
            accessTokenFallback.DisplayName != "gpt-5.6-terra Access Token（sub2api 实际口径）" ||
            accessTokenFallback.PricingPolicy != UsagePricingPolicy.AccessTokenSub2ApiParity ||
            !accessTokenFallback.UsesLongContextPricing ||
            Math.Abs(accessTokenFallback.GetCacheWriteRate(false) - 2.5D) > 0.000_001D ||
            Math.Abs(accessTokenFallback.GetInputRate(true) - 4D) > 0.000_001D ||
            Math.Abs(accessTokenFallback.GetCachedInputRate(true) - 0.4D) > 0.000_001D ||
            Math.Abs(accessTokenFallback.GetOutputRate(true) - 18D) > 0.000_001D ||
            Math.Abs(accessTokenFallback.GetCacheWriteRate(true) - 5D) > 0.000_001D ||
            compatibleApiFallback.DisplayName != "gpt-5.6-sol 兼容 API 账单单价" ||
            compatibleApiFallback.PricingPolicy != UsagePricingPolicy.CompatibleApiProvider ||
            compatibleApiFallback.UsesLongContextPricing ||
            Math.Abs(compatibleApiFallback.GetInputRate(true) - 5D) > 0.000_001D ||
            Math.Abs(compatibleApiFallback.GetCachedInputRate(true) - 0.5D) > 0.000_001D ||
            Math.Abs(compatibleApiFallback.GetOutputRate(true) - 30D) > 0.000_001D ||
            Math.Abs(compatibleApiFallback.GetCacheWriteRate(true) - 6.25D) > 0.000_001D ||
            spacedCompatibleSolProfile.DisplayName != compatibleApiFallback.DisplayName ||
            spacedCompatibleSolProfile.PricingPolicy != UsagePricingPolicy.CompatibleApiProvider ||
            Math.Abs(EstimateTotalCost(unknownModelUsage, accessTokenFallback) - 0.568D) > 0.000_001D ||
            unknownCacheWriteUsage.CacheWriteKnownEvents != 0 ||
            unknownCacheWriteUsage.CacheWriteUnknownEvents != 1 ||
            unknownCacheWriteUsage.CacheWriteUnknownInputTokens != 160_000L ||
            Math.Abs(EstimateTotalCost(unknownCacheWriteUsage, fallback) - 0.568D) > 0.000_001D ||
            Math.Abs(EstimateMaximumCacheWriteUplift(unknownCacheWriteUsage, fallback) - 0.08D) > 0.000_001D ||
            Math.Abs(fixedCostUsage.EquivalentCostOverrideUsd - 0.01D) > 0.000_001D ||
            Math.Abs(EstimateUsageEventCost(fixedCostEvent, fallback) - 0.01D) > 0.000_001D ||
            Math.Abs(EstimateTotalCost(fixedCostUsage, fallback) - 0.01D) > 0.000_001D ||
            fixedCostDistribution.Count != 1 ||
            Math.Abs(fixedCostDistribution[0].EquivalentCostUsd - 0.01D) > 0.000_001D ||
            mixedDistribution.Count != 1 ||
            mixedDistribution[0].Model != "gpt-5.6-terra" ||
            mixedDistribution[0].Records != 2 ||
            mixedDistribution[0].TotalTokens != 1_320_000L ||
            Math.Abs(mixedDistribution[0].EquivalentCostUsd - 5.648D) > 0.000_001D ||
            monitoringAssessmentWindows.Count != 1 ||
            monitoringAssessmentWindows[0].Status != PassiveQuotaStatus.Abnormal ||
            monitoringAssessmentWindows[0].FromUtc != fixtureThroughUtc.AddHours(-3) ||
            realtimeAssessmentWindows.Count != 0 ||
            noEpochAssessmentWindows.Count != 0 ||
            !weeklyRowDetail.Contains("5h重置：", StringComparison.Ordinal) ||
            !weeklyRowDetail.Contains(Environment.NewLine + "周重置：", StringComparison.Ordinal) ||
            !primaryResetLine.StartsWith("5h重置：", StringComparison.Ordinal) ||
            secondaryResetLine == null ||
            !secondaryResetLine.StartsWith("周重置：", StringComparison.Ordinal) ||
            primaryResetLine.Contains(Environment.NewLine, StringComparison.Ordinal) ||
            secondaryResetLine.Contains(Environment.NewLine, StringComparison.Ordinal) ||
            !weeklyOnlyPrimaryLine.StartsWith("周重置：", StringComparison.Ordinal) ||
            weeklyOnlySecondaryLine != null ||
            AccountQuotaLimitType.UsesTwoDetailLines(AccountQuotaLimitType.WeeklyOnly) ||
            !AccountQuotaLimitType.UsesTwoDetailLines(AccountQuotaLimitType.FiveHourAndWeekly) ||
            !monthlyMetrics.Select(item => item.Caption).SequenceEqual(["今天", "本周", "本月"]) ||
            !apiMetrics.Select(item => item.Caption).SequenceEqual(["今天", "本周", "本月"]) ||
            !weeklyMetrics.Select(item => item.Caption).SequenceEqual(["5h", "今天", "本周"]) ||
            !listMetrics.Select(item => item.Caption).SequenceEqual(["5h", "今天", "本周"]) ||
            !ReferenceEquals(listMetrics[0].Bucket, resetUsage.FiveHours) ||
            !ReferenceEquals(listMetrics[1].Bucket, resetUsage.Day) ||
            !ReferenceEquals(listMetrics[2].Bucket, resetUsage.Week) ||
            !ReferenceEquals(monthlyMetrics[2].Bucket, resetUsage.Month) ||
            !ReferenceEquals(apiMetrics[2].Bucket, resetUsage.Month) ||
            !ReferenceEquals(weeklyMetrics[0].Bucket, resetUsage.FiveHours) ||
            todayStartLocal.Date != localFixture.Date ||
            todayStartLocal.TimeOfDay != TimeSpan.Zero ||
            weekStartLocal.Date != new DateTime(2026, 7, 13) ||
            weekStartLocal.TimeOfDay != TimeSpan.Zero ||
            monthStartLocal.Date != new DateTime(2026, 7, 1) ||
            monthStartLocal.TimeOfDay != TimeSpan.Zero ||
            fixtureThroughUtc - GetQuotaTrendStartUtc(TimeSpan.FromHours(1), fixtureThroughUtc) != TimeSpan.FromHours(1) ||
            fixtureThroughUtc - GetQuotaTrendStartUtc(TimeSpan.FromHours(5), fixtureThroughUtc) != TimeSpan.FromHours(5) ||
            GetQuotaTrendBucketSize(TimeSpan.FromHours(24)) != TimeSpan.FromMinutes(15) ||
            emptyHourlySamples.Count != 3 ||
            emptyHourlySamples.Any(sample =>
                sample.BucketDuration != TimeSpan.FromHours(1) ||
                sample.IncrementalCostUsd != 0D) ||
            emptyHourlySamples.Zip(emptyHourlySamples.Skip(1), (first, second) => second.Timestamp - first.Timestamp)
                .Any(gap => gap != TimeSpan.FromHours(1)) ||
            todayHourlySamples.Count != 58 ||
            todayHourlySamples[0].Timestamp.ToLocalTime().TimeOfDay != TimeSpan.Zero ||
            todayHourlySamples[^1].Timestamp.ToLocalTime().TimeOfDay != TimeSpan.FromHours(14) + TimeSpan.FromMinutes(15) ||
            todayHourlySamples.Any(sample => sample.BucketDuration != TimeSpan.FromMinutes(15)) ||
            trimmedTodaySamples.Count != 21 ||
            trimmedTodaySamples[0].Timestamp.ToLocalTime().TimeOfDay != TimeSpan.FromHours(9) + TimeSpan.FromMinutes(15) ||
            trimmedTodaySamples[0].IncrementalCostUsd != 0D ||
            trimmedTodaySamples[4].Timestamp.ToLocalTime().TimeOfDay != TimeSpan.FromHours(10) + TimeSpan.FromMinutes(15) ||
            trimmedTodaySamples[4].IncrementalCostUsd != 1.25D ||
            trimmedTodaySamples[5].IncrementalCostUsd != 0D ||
            trimmedTodaySamples[11].Timestamp.ToLocalTime().TimeOfDay != TimeSpan.FromHours(12) ||
            trimmedTodaySamples[11].IncrementalCostUsd != 0.75D ||
            trimmedTodaySamples[^1].Timestamp.ToLocalTime().TimeOfDay != TimeSpan.FromHours(14) + TimeSpan.FromMinutes(15) ||
            trimmedTodaySamples[^1].IncrementalCostUsd != 0D ||
            emptyTrimmedTodaySamples.Count != 0 ||
            !ShouldTrimLeadingQuotaTrendBuckets(TimeSpan.FromHours(24)) ||
            !ShouldTrimLeadingQuotaTrendBuckets(TimeSpan.FromHours(1)) ||
            !ShouldTrimLeadingQuotaTrendBuckets(TimeSpan.FromHours(5)) ||
            !ShouldTrimLeadingQuotaTrendBuckets(TimeSpan.FromDays(7)) ||
            !ShouldTrimLeadingQuotaTrendBuckets(TimeSpan.FromDays(30)) ||
            GetQuotaTrendLeadingContextDuration(TimeSpan.FromHours(1)) != TimeSpan.FromMinutes(5) ||
            GetQuotaTrendLeadingContextDuration(TimeSpan.FromHours(5)) != TimeSpan.FromMinutes(30) ||
            GetQuotaTrendLeadingContextDuration(TimeSpan.FromHours(24)) != TimeSpan.FromHours(1) ||
            GetQuotaTrendLeadingContextDuration(TimeSpan.FromDays(7)) != TimeSpan.FromHours(12) ||
            GetQuotaTrendLeadingContextDuration(TimeSpan.FromDays(30)) != TimeSpan.FromDays(2) ||
            GetQuotaTrendLeadingContextBucketCount(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)) != 1 ||
            GetQuotaTrendLeadingContextBucketCount(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)) != 2 ||
            GetQuotaTrendLeadingContextBucketCount(TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)) != 4 ||
            GetQuotaTrendLeadingContextBucketCount(TimeSpan.FromHours(6), TimeSpan.FromHours(12)) != 2 ||
            GetQuotaTrendLeadingContextBucketCount(TimeSpan.FromDays(1), TimeSpan.FromDays(2)) != 2 ||
            CalculateQuotaTrendChartHeight(800, 1F) != 460 ||
            CalculateQuotaTrendChartHeight(1_200, 1.5F) != 690 ||
            CalculateQuotaTrendChartHeight(1_600, 2F) != 920 ||
            Math.Abs((projectionAt67 ?? double.NaN) - 166.3811D) > 0.000_001D ||
            Math.Abs((projectionAt69 ?? double.NaN) - 171.3477D) > 0.000_001D ||
            projectionTextAt67 != "推测剩余  $166.38 / $248.33" ||
            projectionTextAt69 != "推测剩余  $171.35 / $248.33" ||
            projectionTextAt67 == projectionTextAt69 ||
            !QuotaChartSamplesEqual(structurallyEqualFirst, structurallyEqualSecond))
        {
            throw new InvalidOperationException(
                $"Usage price or weekly reset-row validation failed. Short={shortEstimate}, long={longEstimate}, weekly={weeklyRowDetail}.");
        }
    }

    internal static void ValidateOfficialQuotaSnapshotPriority()
    {
        var now = new DateTimeOffset(2026, 7, 19, 5, 32, 0, TimeSpan.Zero);
        var usage = new AccountUsageSummary
        {
            AccountName = "official-priority",
            RateLimitUsedPercent = 89D,
            RateLimitWindowMinutes = 43_800,
            RateLimitResetAtUtc = now.AddDays(30),
            RateLimitObservedAtUtc = now.AddSeconds(10)
        };
        var liveSnapshot = new LiveRateLimitSnapshot(
            0D,
            43_800,
            now.AddDays(31),
            null,
            null,
            null,
            null,
            null,
            "team",
            now);
        ApplyLiveRateLimitSnapshot(usage, liveSnapshot);
        if (usage.RateLimitUsedPercent != 89D ||
            usage.RateLimitWindowMinutes != 43_800 ||
            usage.RateLimitResetAtUtc != now.AddDays(30) ||
            usage.RateLimitObservedAtUtc != now.AddSeconds(10))
        {
            throw new InvalidOperationException(
                "A conflicting official quota window must not overwrite the account's current model-log window.");
        }

        ApplyLiveRateLimitSnapshot(
            usage,
            liveSnapshot with
            {
                UsedPercent = 90D,
                ResetsAtUtc = now.AddDays(31),
                ObservedAtUtc = now.AddSeconds(20)
            });
        if (usage.RateLimitUsedPercent != 90D ||
            usage.RateLimitResetAtUtc != now.AddDays(31) ||
            usage.RateLimitObservedAtUtc != now.AddSeconds(20))
        {
            throw new InvalidOperationException(
                "A newer official quota refresh must replace an older model-log snapshot across reset cycles.");
        }
    }

    internal static void ValidateQuotaRuntimeAccountIsolation()
    {
        var now = new DateTimeOffset(2026, 7, 19, 5, 32, 0, TimeSpan.Zero);
        var originalAccount = new AccountRecord
        {
            Name = "same-label",
            CodexHome = Path.Combine(Path.GetTempPath(), "quota-isolation-original")
        };
        var replacementAccount = new AccountRecord
        {
            Name = originalAccount.Name,
            CodexHome = Path.Combine(Path.GetTempPath(), "quota-isolation-replacement")
        };
        var originalKey = QuotaAccountIdentity.CreateKey(originalAccount);
        var replacementKey = QuotaAccountIdentity.CreateKey(replacementAccount);
        if (string.Equals(originalKey, replacementKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Different account credential directories must have different runtime quota keys.");
        }

        var report = new UsageReport
        {
            Accounts =
            [
                new AccountUsageSummary { AccountName = replacementAccount.Name }
            ]
        };
        var snapshots = new Dictionary<string, LiveRateLimitSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [originalKey] = new LiveRateLimitSnapshot(
                91D,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "team",
                now)
        };
        ApplyLiveRateLimitSnapshots(report, [replacementAccount], snapshots, now);
        if (report.Accounts[0].RateLimitUsedPercent.HasValue)
        {
            throw new InvalidOperationException(
                "A replaced account must not inherit the previous account's live quota snapshot.");
        }

        snapshots[replacementKey] = snapshots[originalKey] with { UsedPercent = 23D };
        ApplyLiveRateLimitSnapshots(report, [replacementAccount], snapshots, now);
        if (report.Accounts[0].RateLimitUsedPercent != 23D)
        {
            throw new InvalidOperationException(
                "The live quota snapshot must be read from the current account key.");
        }
    }

    private static UsagePriceProfile GetUsagePriceProfile(AccountRecord account)
    {
        var gpt56Terra = new UsagePriceProfile(
            "gpt-5.6-terra Access Token（sub2api 实际口径）",
            2D,
            0.2D,
            12D,
            4D,
            0.4D,
            18D,
            CacheWriteUsdPerMillion: 2.5D,
            LongCacheWriteUsdPerMillion: 5D,
            UsesLongContextPricing: true);
        if (!account.IsCompatibleApi)
        {
            // Access Token accounts are launched with gpt-5.6-terra by default. Most
            // token_count records carry their own model and override this fallback; this
            // value is only used for legacy/truncated records with no preceding turn_context.
            return gpt56Terra;
        }

        var gpt55 = new UsagePriceProfile(
            "gpt-5.5 兼容 API 账单单价",
            5D,
            0.5D,
            30D,
            10D,
            1D,
            45D,
            PricingPolicy: UsagePricingPolicy.CompatibleApiProvider);
        return GetUsagePriceProfile(account.ApiModel, gpt55);
    }

    private static UsagePriceProfile GetUsagePriceProfile(
        string? modelName,
        UsagePriceProfile fallback)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return fallback;
        }

        // Compatible API profiles are user-entered and older configurations accepted
        // display-style model names such as "gpt-5.6 sol". Normalize separators before
        // resolving the price table so those profiles do not silently fall back to gpt-5.5.
        var model = modelName.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        var compatibleApiPricing =
            fallback.PricingPolicy == UsagePricingPolicy.CompatibleApiProvider;
        if (model.Contains("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(
                compatibleApiPricing
                    ? "gpt-5.6-terra 兼容 API 账单单价"
                    : "gpt-5.6-terra Access Token（sub2api 实际口径）",
                2D,
                0.2D,
                12D,
                4D,
                0.4D,
                18D,
                CacheWriteUsdPerMillion: 2.5D,
                LongCacheWriteUsdPerMillion: 5D,
                UsesLongContextPricing: true,
                PricingPolicy: fallback.PricingPolicy);
        }

        if (model.Contains("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(
                compatibleApiPricing
                    ? "gpt-5.6-luna 兼容 API 账单单价"
                    : "gpt-5.6-luna Access Token（sub2api 实际口径）",
                0.2D,
                0.02D,
                1.2D,
                0.4D,
                0.04D,
                1.8D,
                CacheWriteUsdPerMillion: 0.25D,
                LongCacheWriteUsdPerMillion: 0.5D,
                UsesLongContextPricing: true,
                PricingPolicy: fallback.PricingPolicy);
        }

        if (model.Contains("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase) ||
            model.Equals("gpt-5.6", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(
                compatibleApiPricing
                    ? "gpt-5.6-sol 兼容 API 账单单价"
                    : "gpt-5.6-sol Access Token（sub2api 实际口径）",
                5D,
                0.5D,
                30D,
                10D,
                1D,
                45D,
                CacheWriteUsdPerMillion: 6.25D,
                LongCacheWriteUsdPerMillion: 12.5D,
                PricingPolicy: fallback.PricingPolicy);
        }

        if (model.Contains("chat-latest", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(
                compatibleApiPricing
                    ? "gpt-5.5 / chat-latest 兼容 API 账单单价"
                    : "gpt-5.5 / chat-latest Access Token（sub2api 实际口径）",
                5D,
                0.5D,
                30D,
                10D,
                1D,
                45D,
                PricingPolicy: fallback.PricingPolicy);
        }

        if (model.Contains("gpt-5.4-mini", StringComparison.OrdinalIgnoreCase))
        {
            // The supplied sub2api CSV bills this wire-model alias with the sol table.
            // Apply the same observed mapping to both compatible API and Access Token
            // histories so an identical usage tuple cannot receive two different prices.
            return new UsagePriceProfile(
                compatibleApiPricing
                    ? "gpt-5.4-mini 兼容 API 账单映射（按 sol）"
                    : "gpt-5.4-mini Access Token（sub2api 实测按 sol）",
                5D,
                0.5D,
                30D,
                PricingPolicy: fallback.PricingPolicy);
        }

        if (model.Contains("gpt-5.4-nano", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(
                compatibleApiPricing
                    ? "gpt-5.4-nano 兼容 API 账单单价"
                    : "gpt-5.4-nano Access Token（sub2api 平价口径）",
                0.2D,
                0.02D,
                1.25D,
                PricingPolicy: fallback.PricingPolicy);
        }

        if (model.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(
                compatibleApiPricing
                    ? "gpt-5.4 兼容 API 账单单价"
                    : "gpt-5.4 Access Token（sub2api 平价口径）",
                2.5D,
                0.25D,
                15D,
                5D,
                0.5D,
                22.5D,
                PricingPolicy: fallback.PricingPolicy);
        }

        if (model.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceProfile(
                compatibleApiPricing ? "Codex 兼容 API 账单单价" : "Codex Access Token（sub2api 平价口径）",
                1.75D,
                0.175D,
                14D,
                PricingPolicy: fallback.PricingPolicy);
        }

        return fallback;
    }

    private static string GetUsagePricingLabel(UsageBucket usage, UsagePriceProfile fallback)
    {
        var labels = usage.ModelUsage
            .Where(modelUsage => modelUsage.Events > 0)
            .Select(modelUsage =>
            {
                var profile = GetUsagePriceProfile(modelUsage.Model, fallback);
                return profile.DisplayName +
                    (modelUsage.IsLongContext && profile.UsesLongContextPricing
                        ? "（长上下文）"
                        : "");
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return labels.Count switch
        {
            0 => fallback.DisplayName,
            1 => labels[0],
            _ => fallback.PricingPolicy == UsagePricingPolicy.CompatibleApiProvider
                ? "日志实际模型的兼容 API 账单单价"
                : "日志实际模型的 Access Token 单价（sub2api 实际口径）"
        };
    }

    private static IReadOnlyList<ModelUsageDistributionItem> BuildModelUsageDistribution(
        UsageBucket usage,
        UsagePriceProfile fallback)
    {
        return usage.ModelUsage
            .Where(modelUsage => modelUsage.Events > 0)
            .GroupBy(
                modelUsage => NormalizeModelDistributionName(modelUsage.Model),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelUsageDistributionItem(
                group.Key,
                group.Sum(modelUsage => modelUsage.Events),
                group.Sum(modelUsage => modelUsage.TotalTokens),
                group.Sum(modelUsage => EstimateModelUsageCost(modelUsage, fallback))))
            .OrderByDescending(item => item.TotalTokens)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ModelUsageDistributionItem> BuildModelUsageDistribution(
        IReadOnlyList<PassiveQuotaTrendPoint> trendPoints)
    {
        return trendPoints
            .SelectMany(point => point.ModelUsage ?? [])
            .Where(modelUsage => modelUsage.EventCount > 0 || modelUsage.TotalTokens > 0L)
            .GroupBy(
                modelUsage => NormalizeModelDistributionName(modelUsage.Model),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelUsageDistributionItem(
                group.Key,
                group.Sum(modelUsage => Math.Max(0, modelUsage.EventCount)),
                group.Sum(modelUsage => Math.Max(0L, modelUsage.TotalTokens)),
                group.Sum(modelUsage => Math.Max(0D, modelUsage.ApiEquivalentCostUsd))))
            .OrderByDescending(item => item.TotalTokens)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeModelDistributionName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return "未识别模型（按默认价）";
        }

        var model = modelName.Trim();
        if (model.Contains("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase))
        {
            return "gpt-5.6-sol";
        }
        if (model.Contains("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase))
        {
            return "gpt-5.6-terra";
        }
        if (model.Contains("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase))
        {
            return "gpt-5.6-luna";
        }
        if (model.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase))
        {
            return "gpt-5.5";
        }
        return model;
    }

    private static double EstimateModelUsageCost(
        ModelUsageBucket usage,
        UsagePriceProfile fallback)
    {
        var price = GetUsagePriceProfile(usage.Model, fallback);
        var normalizedInput = Math.Max(0L, usage.PricedInputTokens);
        var cachedInput = Math.Clamp(usage.PricedCachedInputTokens, 0L, normalizedInput);
        var cacheWrite = Math.Clamp(
            usage.PricedCacheWriteTokens,
            0L,
            normalizedInput - cachedInput);
        var regularInput = normalizedInput - cachedInput - cacheWrite;
        return usage.EquivalentCostOverrideUsd +
            (((regularInput * price.GetInputRate(usage.IsLongContext)) +
              (cachedInput * price.GetCachedInputRate(usage.IsLongContext)) +
              (cacheWrite * price.GetCacheWriteRate(usage.IsLongContext)) +
              (usage.PricedOutputTokens * price.GetOutputRate(usage.IsLongContext))) /
             1_000_000D);
    }

    private static double EstimateInputCost(UsageBucket usage, UsagePriceProfile priceProfile)
    {
        return EstimateRegularInputCost(usage, priceProfile) +
            EstimateCachedInputCost(usage, priceProfile) +
            EstimateCacheWriteCost(usage, priceProfile);
    }

    private static double EstimateRegularInputCost(UsageBucket usage, UsagePriceProfile priceProfile)
    {
        return usage.ModelUsage.Sum(modelUsage =>
        {
            var modelPrice = GetUsagePriceProfile(modelUsage.Model, priceProfile);
            var normalizedInput = Math.Max(0L, modelUsage.PricedInputTokens);
            var cachedInput = Math.Clamp(
                modelUsage.PricedCachedInputTokens,
                0L,
                normalizedInput);
            var cacheWrite = Math.Clamp(
                modelUsage.PricedCacheWriteTokens,
                0L,
                normalizedInput - cachedInput);
            var regularInput = normalizedInput - cachedInput - cacheWrite;
            return regularInput * modelPrice.GetInputRate(modelUsage.IsLongContext) / 1_000_000D;
        });
    }

    private static double EstimateCachedInputCost(UsageBucket usage, UsagePriceProfile priceProfile)
    {
        return usage.ModelUsage.Sum(modelUsage =>
        {
            var modelPrice = GetUsagePriceProfile(modelUsage.Model, priceProfile);
            var cachedInput = Math.Clamp(
                modelUsage.PricedCachedInputTokens,
                0L,
                Math.Max(0L, modelUsage.PricedInputTokens));
            return cachedInput * modelPrice.GetCachedInputRate(modelUsage.IsLongContext) / 1_000_000D;
        });
    }

    private static double EstimateCacheWriteCost(UsageBucket usage, UsagePriceProfile priceProfile)
    {
        return usage.ModelUsage.Sum(modelUsage =>
        {
            var modelPrice = GetUsagePriceProfile(modelUsage.Model, priceProfile);
            var normalizedInput = Math.Max(0L, modelUsage.PricedInputTokens);
            var cachedInput = Math.Clamp(
                modelUsage.PricedCachedInputTokens,
                0L,
                normalizedInput);
            var cacheWrite = Math.Clamp(
                modelUsage.PricedCacheWriteTokens,
                0L,
                normalizedInput - cachedInput);
            return cacheWrite * modelPrice.GetCacheWriteRate(modelUsage.IsLongContext) / 1_000_000D;
        });
    }

    private static double EstimateMaximumCacheWriteUplift(
        UsageBucket usage,
        UsagePriceProfile priceProfile)
    {
        return usage.ModelUsage.Sum(modelUsage =>
        {
            var modelPrice = GetUsagePriceProfile(modelUsage.Model, priceProfile);
            var rateDifference = Math.Max(
                0D,
                modelPrice.GetCacheWriteRate(modelUsage.IsLongContext) -
                modelPrice.GetInputRate(modelUsage.IsLongContext));
            return Math.Max(0L, modelUsage.PricedCacheWriteUnknownInputTokens) *
                rateDifference / 1_000_000D;
        });
    }

    private static double EstimateOutputCost(UsageBucket usage, UsagePriceProfile priceProfile)
    {
        return usage.ModelUsage.Sum(modelUsage =>
        {
            var modelPrice = GetUsagePriceProfile(modelUsage.Model, priceProfile);
            return modelUsage.PricedOutputTokens *
                modelPrice.GetOutputRate(modelUsage.IsLongContext) /
                1_000_000D;
        });
    }

    private static double EstimateTotalCost(UsageBucket usage, UsagePriceProfile priceProfile)
    {
        return usage.EquivalentCostOverrideUsd +
            EstimateInputCost(usage, priceProfile) +
            EstimateOutputCost(usage, priceProfile);
    }

    private static double EstimateUsageEventCost(UsageEvent usage, UsagePriceProfile priceProfile)
    {
        if (usage.EquivalentCostOverrideUsd is double overrideCost && overrideCost >= 0D)
        {
            return overrideCost;
        }

        var bucket = new UsageBucket();
        bucket.Add(usage);
        return EstimateTotalCost(bucket, priceProfile);
    }

    private static string FormatTokensWithUsd(long tokens, double usd)
    {
        return $"{FormatTokens(tokens)}  {FormatUsd(usd)}";
    }

    private static string FormatEstimatedCost(UsageBucket usage, UsagePriceProfile priceProfile)
    {
        return FormatUsd(EstimateTotalCost(usage, priceProfile));
    }

    private static UsageBucket[] GetQuotaUsageBuckets(AccountUsageSummary usage) =>
    [
        usage.Hour,
        usage.FiveHours,
        usage.Day,
        usage.Week,
        usage.Month
    ];

    private static bool ShouldShowCacheWriteColumn(AccountUsageSummary usage) =>
        GetQuotaUsageBuckets(usage).Any(bucket => bucket.CacheWriteTokens > 0L);

    private void UpdateQuotaUsageTable(
        QuotaUsageTableBinding binding,
        AccountUsageSummary usage,
        UsagePriceProfile priceProfile)
    {
        var buckets = GetQuotaUsageBuckets(usage);
        if (binding.Rows.Length != buckets.Length)
        {
            return;
        }

        for (var index = 0; index < buckets.Length; index++)
        {
            var row = binding.Rows[index];
            var bucket = buckets[index];
            SetLabelText(
                row.Total,
                row.CompactSplit != null
                    ? $"{FormatTokens(bucket.TotalTokens)} token  ·  {FormatEstimatedCost(bucket, priceProfile)}"
                    : $"{FormatTokens(bucket.TotalTokens)}  {FormatEstimatedCost(bucket, priceProfile)}");

            if (row.CompactSplit != null)
            {
                SetLabelText(row.CompactSplit, FormatCompactQuotaUsageSplit(bucket));
                continue;
            }

            var normalizedInput = Math.Max(0L, bucket.InputTokens);
            var cachedInput = Math.Clamp(bucket.CachedInputTokens, 0L, normalizedInput);
            var cacheWrite = Math.Clamp(bucket.CacheWriteTokens, 0L, normalizedInput - cachedInput);
            var regularInput = normalizedInput - cachedInput - cacheWrite;
            if (row.RegularInput != null)
            {
                SetLabelText(
                    row.RegularInput,
                    $"{FormatTokens(regularInput)}  {FormatUsd(EstimateRegularInputCost(bucket, priceProfile))}");
            }
            if (row.CachedInput != null)
            {
                SetLabelText(
                    row.CachedInput,
                    $"{FormatTokens(cachedInput)}  {FormatUsd(EstimateCachedInputCost(bucket, priceProfile))}");
            }
            if (row.CacheWrite != null)
            {
                SetLabelText(
                    row.CacheWrite,
                    cacheWrite > 0L
                        ? $"{FormatTokens(cacheWrite)}  {FormatUsd(EstimateCacheWriteCost(bucket, priceProfile))}"
                        : string.Empty);
                _toolTip.SetToolTip(row.CacheWrite, GetCacheWriteStatusDescription(bucket, priceProfile));
            }
            if (row.Output != null)
            {
                SetLabelText(
                    row.Output,
                    FormatTokensWithUsd(bucket.OutputTokens, EstimateOutputCost(bucket, priceProfile)));
            }
        }
    }

    private static string GetCacheWriteReportingLabel(UsageBucket usage)
    {
        if (usage.Events == 0)
        {
            return "缓存写入：暂无用量";
        }
        if (usage.CacheWriteUnknownEvents == 0)
        {
            return $"缓存写入已上报（{FormatTokens(usage.CacheWriteTokens)} Token）";
        }
        if (usage.CacheWriteKnownEvents > 0)
        {
            return usage.CacheWriteTokens > 0L
                ? $"缓存写入仅部分日志上报（{FormatTokens(usage.CacheWriteTokens)} Token）"
                : "缓存写入仅部分日志上报";
        }
        return "缓存写入未由上游日志上报";
    }

    private static string GetCacheWriteStatusDescription(
        UsageBucket usage,
        UsagePriceProfile priceProfile)
    {
        var status = GetCacheWriteReportingLabel(usage);
        var baseCost = EstimateTotalCost(usage, priceProfile);
        var reconciliation = usage.ResponseUsageMatchedEvents > 0
            ? $"已用本机 response.completed.usage 核对 {usage.ResponseUsageMatchedEvents:N0} 条；" +
              $"其中 {usage.ResponseUsageDifferenceEvents:N0} 条存在 JSONL 字段缺失或不同，已按原始响应修正并写入审计缓存。"
            : "当前时间范围内没有可匹配的原始响应 usage，继续使用 JSONL 记录。";
        if (usage.CacheWriteUnknownEvents == 0)
        {
            return $"{status}。缓存写入已按独立单价计入；当前 API 等值成本：{FormatUsd(baseCost)}。{reconciliation}";
        }

        return $"{status}。当前 API 等值成本按普通输入价作单点基础估算：{FormatUsd(baseCost)}；待上游上报缓存写入 Token 后会按实际独立单价更新。{reconciliation}";
    }

    private static string FormatUsd(double value)
    {
        if (value <= 0)
        {
            return "$0.00";
        }

        if (value < 0.01D)
        {
            return "<$0.01";
        }

        return "$" + value.ToString("#,0.00", CultureInfo.InvariantCulture);
    }

    private Color GetQuotaColor(double? remainingPercent)
    {
        if (!remainingPercent.HasValue)
        {
            return _palette.WarningColor;
        }

        return remainingPercent.Value switch
        {
            < 15 => _palette.DangerColor,
            < 35 => _palette.WarningColor,
            _ => _palette.SuccessColor
        };
    }

    private static string FormatTokens(long value)
    {
        var abs = Math.Abs(value);
        if (abs >= 1_000_000)
        {
            return (value / 1_000_000D).ToString("0.##M");
        }

        if (abs >= 10_000)
        {
            return (value / 1_000D).ToString("0.#k");
        }

        return value.ToString("N0");
    }

    private Label MakeAccountStateBadge(AccountRecord account, int left, int top)
    {
        var current = IsCurrentAccount(account);
        var color = current ? _palette.SuccessColor : _palette.MutedTextColor;
        var badge = MakeBadge(
            current ? "当前使用" : "未启用",
            left,
            top,
            Color.FromArgb(46, color),
            color);
        badge.Width = 132;
        badge.Height = 34;
        return badge;
    }

    private int MeasureThemeTextHeight(
        string text,
        Font font,
        int width,
        int minimumHeight,
        int verticalPadding,
        bool wrap)
    {
        var flags = TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding |
                    (wrap
                        ? TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl
                        : TextFormatFlags.SingleLine);
        var measured = TextRenderer.MeasureText(
            string.IsNullOrEmpty(text) ? "国Ag" : text,
            font,
            new Size(Math.Max(1, width), 4096),
            flags);
        return Math.Max(
            minimumHeight,
            measured.Height + Math.Max(0, verticalPadding));
    }

    private PillLabel MakeBadge(string text, int left, int top, Color back, Color fore)
    {
        return new PillLabel
        {
            Text = text,
            Left = left,
            Top = top,
            Width = 120,
            Height = 32,
            TextAlign = ContentAlignment.MiddleCenter,
            FillColor = back,
            StrokeColor = Color.FromArgb(Math.Min(92, Math.Max(34, (int)fore.A)), fore),
            BackColor = Color.Transparent,
            ForeColor = fore,
            Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold)
        };
    }

    private static string GetStatusBadgeText(LoginStatus? status)
    {
        if (status == null)
        {
            return "未检查";
        }

        return status.Badge switch
        {
            "TOKEN" => "TOKEN",
            "API_KEY" => "API",
            "OAUTH" => "ChatGPT",
            "LOGGED" => "已登录",
            "FAILED" => "失败",
            "UNKNOWN" => "未知",
            _ => status.Badge
        };
    }

    private Button MakePrimaryButton(string text, int left, int top, int width)
    {
        var button = new ModernButton
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 46,
            Margin = new Padding(0, 6, 12, 10),
            Tag = "primary",
            Radius = 12,
            Padding = new Padding(12, 0, 12, 0),
            Font = new Font(Font.FontFamily, 9.1F, FontStyle.Bold),
            MinimumFontSize = 8F
        };
        ThemeStyler.ApplyPrimaryButton(button, _palette);
        return button;
    }

    private Button MakeSoftButton(string text, int left, int top, int width)
    {
        var button = new ModernButton
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 46,
            Margin = new Padding(0, 6, 12, 10),
            Tag = "soft",
            Radius = 12,
            Padding = new Padding(12, 0, 12, 0),
            Font = new Font(Font.FontFamily, 9F),
            MinimumFontSize = 8F
        };
        ThemeStyler.ApplySoftButton(button, _palette);
        return button;
    }

    private Button MakeActionButton(string text, int left, int top, int width, bool primary)
    {
        var button = new ModernButton
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 42,
            Tag = primary ? "primary" : "soft",
            Radius = 12,
            Padding = new Padding(12, 0, 12, 0),
            Font = new Font(
                Font.FontFamily,
                primary ? 9F : 8.9F,
                primary ? FontStyle.Bold : FontStyle.Regular),
            MinimumFontSize = 8F
        };
        if (primary)
        {
            ThemeStyler.ApplyPrimaryButton(button, _palette);
        }
        else
        {
            ThemeStyler.ApplySoftButton(button, _palette);
        }
        return button;
    }

    private Button MakeLaunchActionButton(
        string text,
        int left,
        int top,
        int width)
    {
        var button = new ModernButton
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 42,
            Tag = "launch-primary",
            Radius = 14,
            Padding = new Padding(16, 0, 16, 0),
            Font = new Font(Font.FontFamily, 9.1F, FontStyle.Bold),
            MinimumFontSize = 8F,
            IconText = string.Empty,
            IconWidth = 0,
            ShowIconTile = false,
            AutoShrinkText = false,
            UseMnemonic = false
        };
        ApplyLaunchActionButtonStyle(button);
        return button;
    }

    private void ApplyLaunchActionButtonStyle(Button button)
    {
        var dark = ThemeStyler.IsDark(_palette);
        var baseColor = UiDesign.Blend(_palette.PrimaryColor, _palette.HeroStartColor, dark ? 0.18F : 0.12F);
        var hoverColor = UiDesign.Blend(_palette.PrimaryHoverColor, _palette.SecondaryAccentColor, 0.22F);
        var pressedColor = UiDesign.Blend(_palette.PrimaryPressedColor, _palette.HeroStartColor, 0.14F);
        var borderColor = Color.FromArgb(
            dark ? 138 : 118,
            UiDesign.Blend(_palette.PrimaryColor, Color.White, 0.50F));

        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.MouseOverBackColor = hoverColor;
        button.FlatAppearance.MouseDownBackColor = pressedColor;
        button.BackColor = baseColor;
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
        button.UseMnemonic = false;

        if (button is ModernButton modern)
        {
            modern.BaseBackColor = baseColor;
            modern.HoverBackColor = hoverColor;
            modern.PressedBackColor = pressedColor;
            modern.BorderColor = borderColor;
            modern.GradientBackColor = Color.FromArgb(dark ? 132 : 156, _palette.SecondaryAccentColor);
            modern.ShadowColor = Color.FromArgb(
                dark ? 82 : 44,
                UiDesign.Blend(_palette.PrimaryColor, _palette.ShadowColor, 0.24F));
            modern.TextColor = Color.White;
            modern.UseSurfaceSheen = true;
            modern.ShowIconTile = false;
            modern.IconText = string.Empty;
            modern.IconWidth = 0;
            modern.IconTileColor = Color.Transparent;
            modern.IconTileBorderColor = Color.Transparent;
            modern.DisabledBackColor = UiDesign.Blend(_palette.DisabledColor, _palette.CardColor, 0.30F);
            modern.DisabledTextColor = UiDesign.Blend(_palette.MutedTextColor, _palette.CardColor, 0.34F);
            modern.FocusColor = Color.FromArgb(190, _palette.AccentColor);
            modern.Invalidate();
        }
    }

    private Button MakeLaunchTonalButton(string text, int left, int top, int width)
    {
        var button = new ModernButton
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 42,
            Tag = "launch-tonal",
            Radius = 13,
            Padding = new Padding(10, 0, 10, 0),
            Font = new Font(Font.FontFamily, 8.9F, FontStyle.Bold),
            MinimumFontSize = 8F,
            AutoShrinkText = false,
            UseMnemonic = false
        };
        ApplyLaunchTonalButtonStyle(button);
        return button;
    }

    private void ApplyLaunchTonalButtonStyle(Button button)
    {
        var dark = ThemeStyler.IsDark(_palette);
        var baseColor = UiDesign.Blend(_palette.CardColor, _palette.PrimaryColor, dark ? 0.16F : 0.065F);
        var hoverColor = UiDesign.Blend(_palette.CardColor, _palette.PrimaryColor, dark ? 0.24F : 0.12F);
        var pressedColor = UiDesign.Blend(hoverColor, _palette.SecondaryAccentColor, 0.14F);
        var borderColor = UiDesign.Blend(_palette.BorderColor, _palette.PrimaryColor, dark ? 0.42F : 0.32F);

        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.MouseOverBackColor = hoverColor;
        button.FlatAppearance.MouseDownBackColor = pressedColor;
        button.BackColor = baseColor;
        button.ForeColor = dark ? _palette.TextColor : _palette.PrimaryColor;
        button.Cursor = Cursors.Hand;
        button.UseMnemonic = false;

        if (button is ModernButton modern)
        {
            modern.BaseBackColor = baseColor;
            modern.HoverBackColor = hoverColor;
            modern.PressedBackColor = pressedColor;
            modern.BorderColor = borderColor;
            modern.GradientBackColor = Color.FromArgb(dark ? 34 : 42, _palette.SecondaryAccentColor);
            modern.ShadowColor = Color.FromArgb(
                dark ? 44 : 20,
                UiDesign.Blend(_palette.PrimaryColor, _palette.ShadowColor, 0.16F));
            modern.TextColor = dark ? _palette.TextColor : _palette.PrimaryColor;
            modern.UseSurfaceSheen = true;
            modern.ShowIconTile = false;
            modern.FocusColor = _palette.FocusColor;
            modern.Invalidate();
        }
    }

    private Button MakeTokenUpdateButton(string text, int left, int top, int width)
    {
        var button = new ModernButton
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 42,
            Tag = "token-update",
            Radius = 14,
            Padding = new Padding(12, 0, 12, 0),
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            MinimumFontSize = 8F,
            // Preserve the complete label if a user's fallback font is wider than the
            // measured Windows UI font. The wider layout above normally needs no shrink.
            AutoShrinkText = true,
            ShowIconTile = false,
            UseMnemonic = false
        };
        ApplyTokenUpdateButtonStyle(button);
        return button;
    }

    private void ApplyTokenUpdateButtonStyle(Button button)
    {
        var dark = ThemeStyler.IsDark(_palette);
        var baseColor = UiDesign.Blend(
            _palette.CardColor,
            _palette.PrimaryColor,
            dark ? 0.18F : 0.075F);
        var hoverColor = UiDesign.Blend(
            _palette.CardColor,
            _palette.SecondaryAccentColor,
            dark ? 0.26F : 0.14F);
        var pressedColor = UiDesign.Blend(hoverColor, _palette.PrimaryColor, 0.16F);
        var borderColor = UiDesign.Blend(
            _palette.BorderColor,
            _palette.PrimaryColor,
            dark ? 0.48F : 0.38F);
        var textColor = dark ? _palette.TextColor : _palette.PrimaryColor;

        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.MouseOverBackColor = hoverColor;
        button.FlatAppearance.MouseDownBackColor = pressedColor;
        button.BackColor = baseColor;
        button.ForeColor = textColor;
        button.Cursor = Cursors.Hand;
        button.UseMnemonic = false;

        if (button is ModernButton modern)
        {
            modern.BaseBackColor = baseColor;
            modern.HoverBackColor = hoverColor;
            modern.PressedBackColor = pressedColor;
            modern.BorderColor = borderColor;
            modern.GradientBackColor = Color.FromArgb(
                dark ? 48 : 58,
                _palette.SecondaryAccentColor);
            modern.ShadowColor = Color.FromArgb(
                dark ? 38 : 18,
                UiDesign.Blend(_palette.PrimaryColor, _palette.ShadowColor, 0.20F));
            modern.TextColor = textColor;
            modern.UseSurfaceSheen = true;
            modern.ShowIconTile = false;
            modern.IconText = string.Empty;
            modern.IconWidth = 0;
            modern.FocusColor = _palette.FocusColor;
            modern.Invalidate();
        }
    }

    private Button MakeStatusCheckButton(int left, int top, int width)
    {
        var button = new ModernButton
        {
            Text = "检查",
            Left = left,
            Top = top,
            Width = width,
            Height = 44,
            Tag = "status-check",
            Radius = 13,
            Padding = new Padding(10, 0, 10, 0),
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            IconText = string.Empty,
            IconWidth = 0,
            ShowIconTile = false,
            AutoShrinkText = false,
            UseMnemonic = false,
            AccessibleName = "检查账号状态"
        };
        ApplyStatusCheckButtonStyle(button);
        return button;
    }

    private void ApplyStatusCheckButtonStyle(Button button)
    {
        // Keep the status action in the same visual family as both launch actions:
        // one clean blue-purple surface, no decorative prefix tile.
        ApplyLaunchActionButtonStyle(button);
        if (button is ModernButton modern)
        {
            modern.ShowIconTile = false;
            modern.IconText = string.Empty;
            modern.IconWidth = 0;
            modern.IconTileColor = Color.Transparent;
            modern.IconTileBorderColor = Color.Transparent;
            modern.Invalidate();
        }
    }

    private Button MakeAccountGroupToggleButton(bool collapsed, int left, int top)
    {
        var button = new ModernButton
        {
            Text = collapsed ? "展开" : "收起",
            Left = left,
            Top = top,
            Width = 86,
            Height = 36,
            Tag = "group-toggle",
            Radius = 18,
            Padding = new Padding(12, 0, 12, 0),
            Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold),
            IconText = string.Empty,
            IconWidth = 0,
            ShowIconTile = false,
            AutoShrinkText = false,
            UseMnemonic = false
        };
        ApplyAccountGroupToggleButtonStyle(button);
        return button;
    }

    private void ApplyAccountGroupToggleButtonStyle(Button button)
    {
        var dark = ThemeStyler.IsDark(_palette);
        var baseColor = UiDesign.Blend(_palette.CardColor, _palette.SecondaryAccentColor, dark ? 0.13F : 0.07F);
        var hoverColor = UiDesign.Blend(_palette.CardColor, _palette.SecondaryAccentColor, dark ? 0.22F : 0.14F);
        var pressedColor = UiDesign.Blend(hoverColor, _palette.PrimaryColor, 0.16F);
        var borderColor = UiDesign.Blend(_palette.BorderColor, _palette.PrimaryColor, 0.34F);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = borderColor;
        button.BackColor = baseColor;
        button.ForeColor = _palette.PrimaryColor;
        button.Cursor = Cursors.Hand;
        button.UseMnemonic = false;
        if (button is ModernButton modern)
        {
            modern.BaseBackColor = baseColor;
            modern.HoverBackColor = hoverColor;
            modern.PressedBackColor = pressedColor;
            modern.BorderColor = borderColor;
            modern.GradientBackColor = Color.FromArgb(dark ? 38 : 28, _palette.AccentColor);
            modern.ShadowColor = Color.FromArgb(
                dark ? 38 : 18,
                UiDesign.Blend(_palette.PrimaryColor, _palette.ShadowColor, 0.12F));
            modern.TextColor = _palette.PrimaryColor;
            modern.UseSurfaceSheen = true;
            modern.FocusColor = Color.FromArgb(170, _palette.AccentColor);
            modern.Invalidate();
        }
    }

    private Button MakeHistoryActionButton(
        string text,
        int left,
        int top,
        int width,
        bool danger = false,
        string iconText = "")
    {
        var button = (ModernButton)MakeActionButton(text, left, top, width, primary: false);
        button.Tag = danger ? "history-danger" : "history-tonal";
        button.Radius = 11;
        button.Padding = new Padding(10, 0, 10, 0);
        button.IconText = iconText;
        button.IconWidth = string.IsNullOrEmpty(iconText) ? 0 : 20;
        button.AutoShrinkText = false;
        ApplyHistoryActionButtonStyle(button, danger);
        return button;
    }

    private void ApplyHistoryActionButtonStyle(Button button, bool danger)
    {
        var dark = ThemeStyler.IsDark(_palette);
        var accent = danger ? _palette.DangerColor : _palette.PrimaryColor;
        var baseColor = UiDesign.Blend(
            _palette.CardColor,
            accent,
            dark ? 0.13F : danger ? 0.052F : 0.042F);
        var hoverColor = UiDesign.Blend(
            _palette.CardColor,
            accent,
            dark ? 0.22F : danger ? 0.105F : 0.085F);
        var pressedColor = UiDesign.Blend(hoverColor, accent, dark ? 0.16F : 0.12F);
        var borderColor = UiDesign.Blend(_palette.BorderColor, accent, danger ? 0.34F : 0.26F);

        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.MouseOverBackColor = hoverColor;
        button.FlatAppearance.MouseDownBackColor = pressedColor;
        button.BackColor = baseColor;
        button.ForeColor = accent;
        button.Cursor = Cursors.Hand;
        button.UseMnemonic = false;

        if (button is ModernButton modern)
        {
            modern.BaseBackColor = baseColor;
            modern.HoverBackColor = hoverColor;
            modern.PressedBackColor = pressedColor;
            modern.BorderColor = borderColor;
            modern.GradientBackColor = Color.Transparent;
            modern.ShadowColor = Color.Transparent;
            modern.TextColor = accent;
            modern.UseSurfaceSheen = false;
            modern.ShowAccent = true;
            modern.AccentColor = accent;
            modern.DisabledBackColor = UiDesign.Blend(_palette.DisabledColor, _palette.CardColor, 0.32F);
            modern.DisabledTextColor = UiDesign.Blend(_palette.MutedTextColor, _palette.CardColor, 0.34F);
            modern.FocusColor = Color.FromArgb(180, accent);
            modern.Invalidate();
        }
    }

    private int MeasureActionButtonWidth(string text, int minimumWidth)
    {
        using var buttonFont = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        var measured = TextRenderer.MeasureText(
            text,
            buttonFont,
            Size.Empty,
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix);
        // ModernButton reserves at least 8 px on both sides. Add a wider DPI-safe
        // buffer so Latin plus signs and Chinese glyphs are never replaced by an
        // ellipsis on 125%/150% display scaling.
        var dpiScale = Math.Max(1F, DeviceDpi / 96F);
        var horizontalPadding = (int)Math.Ceiling(44 * dpiScale);
        return Math.Max(minimumWidth, measured.Width + horizontalPadding);
    }

    private void AddAccount()
    {
        using var dialog = new AccountDialog(
            null,
            _store.RootPath,
            _palette,
            _accounts.Select(account => account.CodexHome),
            _codex);
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var account = CreateAccountFromDialog(dialog);
            try
            {
                _store.SaveAccount(
                    account,
                    null,
                    dialog.ApiKeyValue,
                    dialog.OfficialOAuthCredentialSourcePath);
            }
            catch (InvalidOperationException ex)
            {
                ShowAccountSaveError(ex);
                continue;
            }

            _selectedAccountName = account.Name;
            LoadAccounts();
            _statusBox.Text = $"已添加{account.AuthKindLabel}账号：{account.Name}";

            if (account.IsOfficialOAuth)
            {
                _statusBox.Text = $"已保存并选中 ChatGPT 官方登录账号：{account.Name}（✓ 已登录）。";
            }
            else if (account.IsAccessToken && !string.IsNullOrWhiteSpace(dialog.AccessTokenValue))
            {
                _ = LoginWithTokenAsync(account, dialog.AccessTokenValue, "Token 已保存到官方 Codex 登录状态。");
            }

            return;
        }
    }

    private async Task EditAccountAsync(AccountRecord account)
    {
        using var dialog = new AccountDialog(
            account,
            _store.RootPath,
            _palette,
            _accounts
                .Where(candidate => !candidate.Name.Equals(account.Name, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.CodexHome),
            _codex);
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var updatedAccount = CreateAccountFromDialog(dialog, account);
            var token = dialog.AccessTokenValue;
            var authKindChanged = !account.AuthKind.Equals(
                updatedAccount.AuthKind,
                StringComparison.OrdinalIgnoreCase);
            try
            {
                _store.SaveAccount(
                    updatedAccount,
                    account.Name,
                    dialog.ApiKeyValue,
                    dialog.OfficialOAuthCredentialSourcePath);
            }
            catch (InvalidOperationException ex)
            {
                ShowAccountSaveError(ex);
                continue;
            }

            _selectedAccountName = updatedAccount.Name;
            _statusCache.Remove(account.Name);
            var accountKey = QuotaAccountIdentity.CreateKey(account);
            var updatedAccountKey = QuotaAccountIdentity.CreateKey(updatedAccount);
            var identityChanged = !accountKey.Equals(
                updatedAccountKey,
                StringComparison.OrdinalIgnoreCase);
            var credentialChanged = identityChanged ||
                                    authKindChanged ||
                                    !string.IsNullOrWhiteSpace(token) ||
                                    !string.IsNullOrWhiteSpace(dialog.ApiKeyValue);
            if (credentialChanged)
            {
                // A display-name-only edit keeps the identity key and therefore keeps every
                // account-local quota snapshot. Credential rotation or a changed CODEX_HOME
                // must invalidate both the old and new key before the next official refresh.
                InvalidateQuotaRuntimeState(account);
                if (identityChanged)
                {
                    InvalidateQuotaRuntimeState(updatedAccount);
                }
                try
                {
                    // A replacement credential can reuse the same CODEX_HOME. Clear the
                    // account-local official snapshot before the new credential is queried.
                    _quotaSnapshotStore.Remove(updatedAccount);
                }
                catch
                {
                    // The next successful official query will overwrite the optional cache.
                }
            }
            if (!account.Name.Equals(updatedAccount.Name, StringComparison.OrdinalIgnoreCase))
            {
                _statusCache.Remove(updatedAccount.Name);
                _usageTracker.RenameAccount(account.Name, updatedAccount.Name);
            }
            if (account.Name.Equals(_currentAccountName, StringComparison.OrdinalIgnoreCase))
            {
                SetCurrentAccount(updatedAccount.Name, false);
            }
            LoadAccounts();
            _statusBox.Text = $"已更新账号：{updatedAccount.Name}";

            if (updatedAccount.IsOfficialOAuth && authKindChanged)
            {
                _statusBox.Text = $"已保存并选中 ChatGPT 官方登录账号：{updatedAccount.Name}（✓ 已登录）。";
            }
            else if (updatedAccount.IsAccessToken && !string.IsNullOrWhiteSpace(token))
            {
                await LoginWithTokenAsync(updatedAccount, token, "密钥已更新。");
            }

            return;
        }
    }

    private void ShowAccountSaveError(InvalidOperationException error)
    {
        MessageBox.Show(
            this,
            error.Message,
            "无法保存账号",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void DeleteAccount(AccountRecord account)
    {
        var confirm = MessageBox.Show(
            $"将永久删除账号 {account.Name} 及其本地凭据目录：\n\n{account.CodexHome}\n\n" +
            "如果共享 .codex 正在使用同一份凭据，对应的共享 auth.json 也会被清理。\n" +
            "共享聊天记录不会删除。正在运行的 Codex 可能在退出前继续使用内存中的登录状态。\n\n" +
            "此操作不可撤销，是否继续？",
            "永久删除账号及凭据",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        var finalConfirm = MessageBox.Show(
            $"再次确认永久删除：\n\n账号：{account.Name}\n目录：{account.CodexHome}",
            "再次确认永久删除",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Error);
        if (finalConfirm != DialogResult.OK)
        {
            return;
        }

        try
        {
            _codex.DeleteSharedCredentialIfSelected(account);
            _store.DeleteAccount(account);
            try
            {
                _quotaSnapshotStore.Remove(account);
            }
            catch
            {
                // Deleting an optional quota cache must not turn a completed account
                // deletion into a reported failure.
            }
        }
        catch (Exception ex)
        {
            ShowError($"无法永久删除账号：{ex.Message}");
            return;
        }

        _statusCache.Remove(account.Name);
        InvalidateQuotaRuntimeState(account);
        if (account.Name.Equals(_currentAccountName, StringComparison.OrdinalIgnoreCase))
        {
            SetCurrentAccount(null, false);
        }
        if (account.Name.Equals(_selectedAccountName, StringComparison.OrdinalIgnoreCase))
        {
            _selectedAccountName = null;
        }
        LoadAccounts();
        _statusBox.Text = $"已永久删除账号及本地凭据：{account.Name}";
    }

    private async Task LaunchAccountAsync(AccountRecord account, WindowsClientMode mode)
    {
        if (!TryGetProjectPathForLaunch(out var projectPath))
        {
            return;
        }

        var modelSummary = account.IsOfficialOAuth
            ? "Codex 官方默认 / ChatGPT 登录"
            : account.IsCompatibleApi
                ? $"{account.ApiModel} / 极高"
                : "gpt-5.6-terra / 中等";
        if (_appSettings.WindowsClientMode != mode)
        {
            // Remember the last explicit button for future defaults without hiding either
            // launch option behind a global picker.
            _appSettings.WindowsClientMode = mode;
            _themeService.SaveSettings(_appSettings);
        }
        var clientName = GetWindowsClientDisplayName(mode);
        var profileAlreadySelected = _codex.IsSharedProfileAlreadySelected(account);
        _statusBox.Text = profileAlreadySelected
            ? $"正在使用现有凭据启动 {clientName}…"
            : $"正在切换到 {account.Name} 并启动 {clientName}…";
        _toolTip.SetToolTip(
            _statusBox,
            profileAlreadySelected
                ? "共享凭据已匹配：跳过退出、重复投放和历史全量合并，直接启动客户端。"
                : "仅校验本地凭据；真正切号时关闭旧客户端、原子投放新凭据，再启动所选客户端。");
        _statusBox.Refresh();
        WindowsClientAccountProjection? projection = null;
        await RunBusyAsync(async () =>
        {
            var startupAppearance = GetCodexAppearanceOptionById(_appSettings.CodexAppearancePresetId);
            var useDreamSkinAtStartup = _appSettings.UseCodexDreamSkin &&
                                        !IsOfficialCodexAppearance(startupAppearance);
            projection = await _codex.SwitchWindowsClientAccountAsync(
                account,
                projectPath,
                mode,
                useDreamSkinAtStartup,
                GetCodexAppearanceRuntimeMode(startupAppearance),
                GetCodexAppearanceRuntimePresetId(startupAppearance),
                GetCodexAppearanceLabelById(_appSettings.CodexAppearancePresetId));
            _statusCache[account.Name] = projection.Status;
            SetCurrentAccount(account.Name, false, recordUsageSwitch: true);
            if (projection.CodexDreamSkinFailed)
            {
                _appSettings.UseCodexDreamSkin = false;
                _themeService.SaveSettings(_appSettings);
            }
            var projectConfigStatus = projection.ProjectConfigWasSanitized
                ? "已移除项目级模型覆盖；"
                : "项目未发现模型覆盖；";
            _statusBox.Text = projection.CodexDreamSkinFailed
                ? projection.ClientLaunchStarted
                    ? projection.CodexOfficialAppearanceRestored
                        ? $"已切换到 {account.Name}；主题同步失败，已恢复官方外观并重新打开 {clientName}。"
                        : $"已切换到 {account.Name}；主题同步失败，{clientName} 已重新打开，但官方外观恢复未完成。"
                    : projection.CodexOfficialAppearanceRestored
                        ? $"{account.Name} 的凭据已生效；主题同步失败并已恢复官方外观，但 {clientName} 未能重新打开。"
                        : $"{account.Name} 的凭据已生效，但主题同步和官方外观恢复均失败。"
                : projection.ClientLaunchStarted
                    ? projection.ProfileChanged
                        ? $"已切换到 {account.Name}；凭据已自动写入，{clientName} 已开始启动。"
                        : $"已复用 {account.Name} 的现有凭据；{clientName} 已开始启动。"
                    : $"{account.Name} 的凭据已生效，但 {clientName} 启动失败。";
            var launchDiagnostic = string.IsNullOrWhiteSpace(projection.ClientLaunchError)
                ? string.Empty
                : $"\n启动诊断：{projection.ClientLaunchError}";
            var accessTokenDesktopHint = account.IsOfficialOAuth
                ? "\n一键凭据：已投放该账号独立保存的 ChatGPT 官方登录态；Codex 使用中会自动续期。"
                : account.IsCompatibleApi
                    ? "\n一键凭据：Codex App 的 API Key 登录由管理器自动写入，无需在客户端重复输入。"
                    : "\n一键凭据：Codex App 的 API Key 登录由管理器自动写入；模型请求经本地 PAT 网关使用该账号的月额度凭据。";
            _toolTip.SetToolTip(
                _statusBox,
                $"模型：{modelSummary}\n共享聊天目录：{projection.DefaultCodexHome}\n账号凭据目录：{projection.AccountCodexHome}\n{projectConfigStatus}已保留历史任务原模型。\n备份目录：{projection.BackupDirectory}{launchDiagnostic}{accessTokenDesktopHint}");
            RenderCards();
            ResetCardsScrollPosition();
            if (projection.ClientLaunchStarted)
            {
                StartOfficialQuotaRefreshAfterLaunch(account);
                return;
            }

            throw new InvalidOperationException(
                $"账号凭据已经切换并保留为 {account.Name}，不会回滚旧账号；" +
                $"但 {clientName} 启动失败：{projection.ClientLaunchError ?? "未返回启动结果"}");
        });
    }

    private async Task LaunchCliAccountAsync(AccountRecord account)
    {
        if (!TryGetProjectPathForLaunch(out var projectPath))
        {
            return;
        }

        WindowsClientAccountProjection? projection = null;

        await RunBusyAsync(async () =>
        {
            projection = await _codex.PrepareWindowsClientAccountAsync(account);
            _statusCache[account.Name] = projection.Status;
            SetCurrentAccount(account.Name, false, recordUsageSwitch: true);
            _statusBox.Text = $"已为 {account.Name} 准备 CLI。";
            _toolTip.SetToolTip(_statusBox, $"共享聊天目录：{projection.DefaultCodexHome}");
            RenderCards();
            ResetCardsScrollPosition();
        });

        if (projection == null)
        {
            return;
        }

        try
        {
            _codex.LaunchPowerShell(account, projectPath, projection.DefaultCodexHome);
            _statusBox.Text = $"已在 CLI 中打开 {account.Name}。";
            _toolTip.SetToolTip(_statusBox, "CLI 使用该账号凭据，聊天记录写入共享 .codex。");
            StartOfficialQuotaRefreshAfterLaunch(account);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void StartOfficialQuotaRefreshAfterLaunch(AccountRecord account)
    {
        if (account.IsCompatibleApi || _formClosed || IsDisposed)
        {
            return;
        }

        var accountKey = QuotaAccountIdentity.CreateKey(account);
        var now = DateTimeOffset.UtcNow;
        var focused = IsCurrentAccount(account) ||
                      (_activeView == WorkspaceView.QuotaUsage &&
                       string.Equals(
                           _selectedAccountName,
                           account.Name,
                           StringComparison.OrdinalIgnoreCase));
        var refreshInterval = focused
            ? OfficialQuotaFocusedRefreshInterval
            : OfficialQuotaBackgroundRefreshInterval;
        if (_officialQuotaRefreshAttemptedAt.TryGetValue(accountKey, out var lastAttempt) &&
            now - lastAttempt < refreshInterval)
        {
            return;
        }
        if (!_officialQuotaRefreshInProgress.Add(accountKey))
        {
            return;
        }

        _officialQuotaRefreshAttemptedAt[accountKey] = now;
        var generation = GetQuotaRuntimeStateGeneration(accountKey);
        _ = RefreshOfficialQuotaAfterLaunchAsync(account, accountKey, generation);
    }

    private async Task RefreshOfficialQuotaAfterLaunchAsync(
        AccountRecord account,
        string accountKey,
        long generation)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var session = await _codex.OpenUsageLimitResetSessionAsync(
                account,
                fastFail: true,
                cancellationToken: timeout.Token);
            var info = await session.ReadAsync(timeout.Token);
            if (!IsQuotaRuntimeStateCurrent(accountKey, generation))
            {
                return;
            }

            CacheUsageLimitResetInfo(account, info);
            _officialQuotaRefreshedAt[accountKey] = DateTimeOffset.UtcNow;

            if (_quotaUsageCache == null && !_formClosed && !IsDisposed)
            {
                await RefreshQuotaUsageAsync(force: true, _workspaceLoadGeneration);
            }
            if (_quotaUsageCache == null || _formClosed || IsDisposed)
            {
                return;
            }

            ApplyLiveRateLimitSnapshots(_quotaUsageCache);
            UpdateQuotaLimitProfilesFromReport(_quotaUsageCache);
            RefreshActivePassiveQuotaMonitoring(_quotaUsageCache);
            if (_activeView != WorkspaceView.QuotaUsage)
            {
                return;
            }

            var updatedInPlace = _showAccountDetail
                ? TryUpdateQuotaDetailInPlace(_quotaUsageCache)
                : TryUpdateQuotaUsageInPlace(_quotaUsageCache);
            if (!updatedInPlace)
            {
                RenderCards();
            }
        }
        catch (OperationCanceledException)
        {
            // Start-of-use refresh is best effort and never delays Codex++ or retries.
        }
        catch
        {
            // Keep the last local/cache snapshot. Manual querying remains available in details.
        }
        finally
        {
            if (IsQuotaRuntimeStateCurrent(accountKey, generation))
            {
                _officialQuotaRefreshInProgress.Remove(accountKey);
            }
        }
    }

    private async Task QueryUsageLimitResetAsync(AccountRecord account)
    {
        if (account.IsCompatibleApi)
        {
            MessageBox.Show(
                this,
                "兼容 API/API Key 账号按 API 账单计费，不适用 ChatGPT/Codex 套餐的用量重置次数。",
                "该账号不适用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SetResetCreditState(
            account,
            ResetCreditStatus.Querying,
            GetAvailableResetCount(account));
        await RunBusyAsync(async () =>
        {
            _statusBox.Text = $"正在只读刷新 {account.Name} 的官方额度……";
            await using var session = await _codex.OpenUsageLimitResetSessionAsync(account);
            var info = await session.ReadAsync();
            CacheUsageLimitResetInfo(account, info);

            if (!info.IsAvailable)
            {
                const string unavailable =
                    "官方接口没有为这个账号返回重置次数信息。账号可能暂不支持该功能，或当前登录凭据无权读取。";
                _statusBox.Text = $"账号：{account.Name}\r\n{unavailable}";
                MessageBox.Show(
                    this,
                    unavailable,
                    "重置次数不可用",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var availableCount = Math.Max(0, info.AvailableCount ?? 0);
            var primaryText = info.Primary?.UsedPercent is { } primaryUsed
                ? $"主额度剩余 {Math.Max(0D, 100D - primaryUsed):0.#}%"
                : "主额度待查询";
            var secondaryText = info.Secondary?.UsedPercent is { } secondaryUsed
                ? $"；次额度剩余 {Math.Max(0D, 100D - secondaryUsed):0.#}%"
                : "";
            var creditsText = info.CreditBalance switch
            {
                { Unlimited: true } => "\n官方 Credits：不限",
                { HasCredits: true, Balance: { Length: > 0 } balance } => $"\n官方 Credits：{balance}（协议未提供币种）",
                _ => ""
            };
            var availabilityText =
                $"账号 {account.Name}\n{primaryText}{secondaryText}\n可重置 {availableCount} 次。{creditsText}\n\n" +
                "本次仅调用只读额度接口，没有发送提示、调用模型或消耗 Token。";
            _statusBox.Text = availabilityText +
                              (availableCount == 0 ? " 立即重置按钮已禁用。" : " 可以点击“立即重置”使用一次。");
            MessageBox.Show(
                this,
                availabilityText,
                $"查询完成：可重置 {availableCount} 次",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });

        if (_resetCreditState.TryGetValue(QuotaAccountIdentity.CreateKey(account), out var state) &&
            state.Status == ResetCreditStatus.Querying)
        {
            SetResetCreditState(account, ResetCreditStatus.Failed, error: "查询失败，请重试。");
        }

        RenderCards();
    }

    private async Task ResetUsageLimitAsync(AccountRecord account)
    {
        if (!CanResetUsage(account))
        {
            _statusBox.Text = $"账号 {account.Name} 当前可重置 0 次，不能执行重置。请进入详情查询最新次数。";
            return;
        }

        SetResetCreditState(
            account,
            ResetCreditStatus.Resetting,
            GetAvailableResetCount(account));
        await RunBusyAsync(async () =>
        {
            _statusBox.Text = $"正在重新确认 {account.Name} 的官方可重置次数……";
            await using var session = await _codex.OpenUsageLimitResetSessionAsync(account);
            var info = await session.ReadAsync();
            CacheUsageLimitResetInfo(account, info);

            var availableCount = info.IsAvailable
                ? Math.Max(0, info.AvailableCount ?? 0)
                : 0;
            if (!info.IsAvailable || availableCount == 0)
            {
                var unavailableText = info.IsAvailable
                    ? $"账号 {account.Name} 当前可重置 0 次，未发送重置请求。"
                    : "官方接口没有返回可重置次数，未发送重置请求。";
                _statusBox.Text = unavailableText;
                MessageBox.Show(
                    this,
                    unavailableText,
                    "不能执行重置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var credit = info.Credits.FirstOrDefault();
            var expiryText = credit?.ExpiresAtUtc is { } expiresAt
                ? $"\n该次数到期时间：{expiresAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "";
            var confirmation = MessageBox.Show(
                this,
                $"账号：{account.Name}\n当前可重置：{availableCount} 次{expiryText}\n\n" +
                "这会消耗一次官方获得的 reset credit，并重置当前符合条件的 Codex 用量窗口。" +
                "该操作不可撤销，也不是清空本地 Token 统计或重连次数。\n\n确定现在使用一次吗？",
                "确认使用用量重置次数",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.OK)
            {
                _statusBox.Text = $"已取消；账号 {account.Name} 仍有 {availableCount} 次可用重置。";
                return;
            }

            var idempotencyKey = Guid.NewGuid().ToString();
            SetResetCreditState(account, ResetCreditStatus.Resetting, availableCount);
            _statusBox.Text = $"正在为 {account.Name} 使用一次官方用量重置……";
            var outcome = await session.ConsumeAsync(idempotencyKey);

            UsageLimitResetInfo? refreshed = null;
            string? refreshWarning = null;
            try
            {
                refreshed = await session.ReadAsync();
                CacheUsageLimitResetInfo(account, refreshed);
            }
            catch (Exception ex)
            {
                refreshWarning = "兑换结果已返回，但重新查询剩余次数失败：" + ex.Message;
            }

            var outcomeText = outcome switch
            {
                UsageLimitResetOutcome.Reset => "用量重置成功。",
                UsageLimitResetOutcome.AlreadyRedeemed => "同一次重置请求已经成功处理，没有重复扣除次数。",
                UsageLimitResetOutcome.NothingToReset => "当前没有符合条件的用量窗口需要重置，因此没有执行重置。",
                UsageLimitResetOutcome.NoCredit => "官方返回当前没有可用的重置次数。",
                _ => "用量重置请求已结束。"
            };
            var remainingText = refreshed?.AvailableCount is { } remaining
                ? $"剩余可用：{remaining} 次。"
                : "剩余次数暂时未知。";
            var displayNote = outcome is UsageLimitResetOutcome.Reset or UsageLimitResetOutcome.AlreadyRedeemed
                ? "额度卡已采用最新官方结果；本地费用/Token 历史不会被清空。"
                : "";
            var finalText = string.Join(
                Environment.NewLine,
                new[] { outcomeText, remainingText, displayNote, refreshWarning }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            _statusBox.Text = $"账号：{account.Name}\r\n{finalText}";
            MessageBox.Show(
                this,
                finalText,
                "用量重置结果",
                MessageBoxButtons.OK,
                outcome is UsageLimitResetOutcome.Reset or UsageLimitResetOutcome.AlreadyRedeemed
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
        });

        if (_resetCreditState.TryGetValue(QuotaAccountIdentity.CreateKey(account), out var state) &&
            state.Status == ResetCreditStatus.Resetting)
        {
            SetResetCreditState(account, ResetCreditStatus.Failed, error: "重置结果需要重新查询。");
        }

        RenderCards();
    }

    private async Task CheckStatusAsync(AccountRecord account)
    {
        await RunBusyAsync(async () =>
        {
            var status = await _codex.GetLoginStatusAsync(account);
            _statusCache[account.Name] = status;
            var conciseStatus = status.ExitCode == 0 ? "已登录" : "检查失败";
            _statusBox.Text = $"{account.Name} · {conciseStatus}";
            _toolTip.SetToolTip(
                _statusBox,
                account.IsCompatibleApi
                    ? $"状态：{status.Text}\nAPI 地址：{account.ApiBaseUrl}"
                    : account.IsOfficialOAuth
                        ? $"状态：{status.Text}\n登录方式：OpenAI 官方 ChatGPT 登录；使用中自动续期"
                        : $"状态：{status.Text}\nToken 到期：{_store.GetExpiryLabel(account.Name)}");
            RenderCards();
        });
    }

    private async Task UpdateTokenAsync(AccountRecord account)
    {
        if (account.IsOfficialOAuth)
        {
            await LoginWithChatGptAsync(account);
            return;
        }

        if (account.IsCompatibleApi)
        {
            await EditAccountAsync(account);
            return;
        }

        using var dialog = new TokenDialog(account.Name, account.CodexHome, _palette);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var token = dialog.AccessTokenValue;
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        await LoginWithTokenAsync(account, token, "Token 已更新。");
    }

    private async Task LoginWithChatGptAsync(AccountRecord requestedAccount)
    {
        var account = _accounts.FirstOrDefault(candidate =>
            candidate.IsOfficialOAuth &&
            candidate.Name.Equals(requestedAccount.Name, StringComparison.OrdinalIgnoreCase) &&
            PathsEqual(candidate.CodexHome, requestedAccount.CodexHome));
        if (account == null)
        {
            ShowError("请先把登录方式为“通过 ChatGPT 登录（官方）”的账号保存到本地，再启动官方登录。");
            return;
        }

        if (!account.Name.Equals(_selectedAccountName, StringComparison.OrdinalIgnoreCase))
        {
            SelectAccount(account.Name);
        }
        if (!account.Name.Equals(_selectedAccountName, StringComparison.OrdinalIgnoreCase))
        {
            ShowError("请先明确选中要绑定的本地官方 OAuth 账号，再启动登录。");
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"即将为已保存并选中的本地账号“{account.Name}”生成 OpenAI 官方网页登录链接。\n\n" +
            "软件只会把一次性链接复制到剪贴板，不会自动打开浏览器，也不会自动识别当前浏览器账号。" +
            "请自行粘贴链接并确认要绑定的 ChatGPT 账号。\n\n是否继续？",
            "确认 ChatGPT 登录账号",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.OK)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        ChatGptOAuthLinkDialog? linkDialog = null;
        var browserAuthorizationActive = true;
        var progress = new Progress<ChatGptOAuthAuthorization>(authorization =>
        {
            if (!browserAuthorizationActive ||
                cancellation.IsCancellationRequested ||
                _formClosed ||
                IsDisposed)
            {
                cancellation.Cancel();
                return;
            }

            if (linkDialog != null)
            {
                return;
            }

            linkDialog = new ChatGptOAuthLinkDialog(account.Name, authorization, _palette);
            linkDialog.CancellationRequested += (_, _) => cancellation.Cancel();
            linkDialog.Show(this);
            linkDialog.Activate();
            _statusBox.Text = $"{account.Name}：官方登录链接已复制，请自行在浏览器中完成登录。";
            _toolTip.SetToolTip(
                _statusBox,
                "一次性登录链接只保留在临时窗口和剪贴板中，不写入账号文件、设置或日志。"
            );
        });

        void CloseLinkDialog()
        {
            if (linkDialog == null)
            {
                return;
            }

            linkDialog.CompleteAndClose();
            linkDialog.Dispose();
            linkDialog = null;
        }

        await RunBusyAsync(async () =>
        {
            _statusBox.Text = $"正在为 {account.Name} 生成 OpenAI 官方登录链接……";
            _statusBox.Refresh();
            InvalidateQuotaRuntimeState(account);
            try
            {
                _quotaSnapshotStore.Remove(account);
            }
            catch
            {
                // A successful official quota read will rebuild the optional cache.
            }

            try
            {
                var status = await _codex.LoginWithChatGptAsync(
                    account,
                    progress,
                    cancellation.Token);
                CloseLinkDialog();
                _statusCache[account.Name] = status;
                var quotaStatus = await QueryQuotaOnceAfterExplicitLoginAsync(account);
                var quotaSummary = quotaStatus.Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal);
                _statusBox.Text = $"{account.Name}：已通过 ChatGPT 官方网页登录。 {quotaSummary}";
                _toolTip.SetToolTip(
                    _statusBox,
                    $"凭据目录：{account.CodexHome}\n状态：{status.Text}\n官方 Codex 会在使用过程中自动续期登录。"
                );
                RenderCards();
            }
            finally
            {
                browserAuthorizationActive = false;
                CloseLinkDialog();
            }
        });
    }

    private async Task LoginWithTokenAsync(AccountRecord account, string token, string successMessage)
    {
        token = CodexCliService.NormalizeAccessTokenInput(token);
        var validationError = CodexCliService.GetAccessTokenInputError(token);
        if (validationError != null)
        {
            ShowError(validationError);
            return;
        }

        await RunBusyAsync(async () =>
        {
            InvalidateQuotaRuntimeState(account);
            try
            {
                // The credential directory is stable across token rotation, so its identity
                // key alone cannot distinguish the old PAT from the new one.
                _quotaSnapshotStore.Remove(account);
            }
            catch
            {
                // The new official response will repopulate the cache when available.
            }
            var expiry = CodexCliService.GetAccessTokenExpiryUtc(token);
            var status = await _codex.LoginWithAccessTokenAsync(account, token);
            _store.WriteTokenMetadata(account.Name, expiry);
            _statusCache[account.Name] = status;
            var quotaStatus = await QueryQuotaOnceAfterExplicitLoginAsync(account);
            var quotaSummary = quotaStatus.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            _statusBox.Text = $"{account.Name}：{successMessage.Trim()} {quotaSummary}";
            _toolTip.SetToolTip(
                _statusBox,
                $"凭据目录：{account.CodexHome}\n状态：{status.Text}\nToken 到期：{_store.GetExpiryLabel(account.Name)}");
            RenderCards();
        });
    }

    private async Task<string> QueryQuotaOnceAfterExplicitLoginAsync(AccountRecord account)
    {
        try
        {
            await using var session = await _codex.OpenUsageLimitResetSessionAsync(account);
            var info = await session.ReadAsync();
            CacheUsageLimitResetInfo(account, info);
            var detectedType = AccountQuotaLimitType.Detect(
                info.Primary?.WindowMinutes,
                info.Secondary?.WindowMinutes);
            var typeText = detectedType == AccountQuotaLimitType.Unknown
                ? "待识别（官方本次未返回窗口时长，正常使用后会从本地日志继续识别）"
                : GetQuotaLimitTypeLabel(detectedType);
            var resetText = info.AvailableCount.HasValue
                ? $"；可重置 {Math.Max(0, info.AvailableCount.Value)} 次"
                : "；可重置次数暂不可用";
            return $"额度类型：{typeText}{resetText}（登录成功后仅自动查询本次）";
        }
        catch (Exception ex)
        {
            return "额度类型：待识别（登录成功，但本次只读额度查询失败；不会自动重试）\r\n" +
                   "查询说明：" + ex.Message;
        }
    }

    private static AccountRecord CreateAccountFromDialog(
        AccountDialog dialog,
        AccountRecord? existingAccount = null)
    {
        return new AccountRecord
        {
            Name = dialog.AccountNameValue,
            CodexHome = dialog.CodexHomeValue,
            AuthKind = dialog.AuthKindValue,
            ApiProviderName = dialog.ApiProviderNameValue,
            ApiBaseUrl = dialog.ApiBaseUrlValue,
            ApiModel = dialog.ApiModelValue,
            ApiWireApi = dialog.ApiWireApiValue,
            QuotaLimitType = existingAccount?.QuotaLimitType ?? AccountQuotaLimitType.Unknown,
            QuotaPrimaryWindowMinutes = existingAccount?.QuotaPrimaryWindowMinutes,
            QuotaSecondaryWindowMinutes = existingAccount?.QuotaSecondaryWindowMinutes,
            QuotaLimitObservedAtUtc = existingAccount?.QuotaLimitObservedAtUtc
        };
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            SetButtonsEnabled(false);
            await action();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetButtonsEnabled(true);
            Cursor = Cursors.Default;
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        foreach (Control control in Controls)
        {
            SetButtonsEnabledRecursive(control, enabled);
        }
    }

    private void SetButtonsEnabledRecursive(Control control, bool enabled)
    {
        if (control is Button button)
        {
            if (!enabled)
            {
                button.Enabled = false;
            }
            else if (button.Name.Equals("UsageResetAction", StringComparison.Ordinal))
            {
                var account = _accounts.FirstOrDefault(candidate =>
                    candidate.Name.Equals(
                        button.AccessibleDescription ?? "",
                        StringComparison.OrdinalIgnoreCase));
                button.Enabled = account != null && CanResetUsage(account);
            }
            else
            {
                button.Enabled = true;
            }
        }

        foreach (Control child in control.Controls)
        {
            SetButtonsEnabledRecursive(child, enabled);
        }
    }

    private string ResolveInitialProjectPath()
    {
        if (TryNormalizeExistingDirectory(_appSettings.ProjectPath, out var savedProjectPath) &&
            !IsAccountManagerRoot(savedProjectPath))
        {
            return savedProjectPath;
        }

        string? managerEnvironmentPath = null;
        if (TryNormalizeExistingDirectory(
                Environment.GetEnvironmentVariable("CODEX_PROJECT_PATH"),
                out var environmentProjectPath))
        {
            if (!IsAccountManagerRoot(environmentProjectPath))
            {
                return environmentProjectPath;
            }

            managerEnvironmentPath = environmentProjectPath;
        }

        if (TryNormalizeExistingDirectory(Environment.CurrentDirectory, out var currentDirectory) &&
            !IsAccountManagerRoot(currentDirectory))
        {
            return currentDirectory;
        }

        var commonWorkspace = FindMostRecentCommonWorkspace();
        if (commonWorkspace != null)
        {
            return commonWorkspace;
        }

        return managerEnvironmentPath ?? Path.GetFullPath(_store.RootPath);
    }

    private string? FindMostRecentCommonWorkspace()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var commonRoots = new[]
        {
            Path.Combine(userProfile, "PycharmProjects"),
            Path.Combine(userProfile, "Projects"),
            Path.Combine(userProfile, "source", "repos"),
            Path.Combine(userProfile, "repos"),
            Path.Combine(userProfile, "workspace"),
            Path.Combine(userProfile, "workspaces"),
            Path.Combine(documents, "GitHub"),
            Path.Combine(documents, "Projects")
        };

        var candidates = new List<(string Path, DateTime LastWriteTimeUtc)>();
        foreach (var root in commonRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    if (IsAccountManagerRoot(directory))
                    {
                        continue;
                    }

                    candidates.Add((
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                        Directory.GetLastWriteTimeUtc(directory)));
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private bool IsAccountManagerRoot(string path)
    {
        return PathsEqual(path, _store.RootPath);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            var normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            var normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryNormalizeExistingDirectory(string? value, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var expandedPath = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expandedPath));
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            path = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void SaveProjectPath(string projectPath)
    {
        if (string.Equals(_appSettings.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _appSettings.ProjectPath = projectPath;
        _themeService.SaveSettings(_appSettings);
    }

    private bool SaveEditedProjectPath(bool updateStatus = true)
    {
        var enteredPath = GetProjectPathInputText();
        if (!TryNormalizeExistingDirectory(enteredPath, out var projectPath))
        {
            if (updateStatus)
            {
                _statusBox.Text = $"启动目录未保存，目录不存在或不可用：{enteredPath.Trim()}";
            }

            return false;
        }

        _projectPathBox.Text = projectPath;
        SaveProjectPath(projectPath);
        if (updateStatus)
        {
            _statusBox.Text = $"已保存启动目录：{projectPath}";
        }

        return true;
    }

    private void InitializePatGatewayProxyEditors()
    {
        var address = _appSettings.PatGatewayProxyAddress?.Trim();
        var port = _appSettings.PatGatewayProxyPort;
        var scheme = _appSettings.PatGatewayProxyScheme?.Trim().ToLowerInvariant();
        var migratedLegacyValue = false;
        if (port is not (> 0 and <= 65535) &&
            CodexCliService.TryParseProxyEndpoint(
                _appSettings.PatGatewayProxy,
                out var legacyAddress,
                out var legacyPort,
                out var legacyScheme))
        {
            address = legacyAddress;
            port = legacyPort;
            scheme = legacyScheme;
            _appSettings.PatGatewayProxyAutoDetect = false;
            migratedLegacyValue = true;
        }

        address = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address;
        scheme = scheme is "http" or "https" ? scheme : "http";
        _appSettings.PatGatewayProxyAddress = address;
        _appSettings.PatGatewayProxyPort = port is > 0 and <= 65535 ? port : null;
        _appSettings.PatGatewayProxyScheme = scheme;
        _patGatewayProxyAddressBox.Text = address;
        _patGatewayProxyAddressBox.PlaceholderText = "127.0.0.1";
        _patGatewayProxyPortBox.Text = _appSettings.PatGatewayProxyPort?.ToString(
            CultureInfo.InvariantCulture) ?? "";
        _patGatewayProxyPortBox.PlaceholderText = "自动检测";
        UpdatePatGatewayProxyDetectionLabel();
        if (migratedLegacyValue)
        {
            _themeService.SaveSettings(_appSettings);
        }
    }

    private bool SaveEditedPatGatewayProxy(bool updateStatus = true, bool markManual = true)
    {
        var address = _patGatewayProxyAddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            address = "127.0.0.1";
        }
        if (address.Contains('/') ||
            address.Contains('\\') ||
            address.Any(char.IsWhiteSpace))
        {
            if (updateStatus)
            {
                _statusBox.Text = "代理设置未保存：地址只填写主机名或 IP，不要包含协议、路径或空格。";
            }
            return false;
        }

        var portText = _patGatewayProxyPortBox.Text.Trim();
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is <= 0 or > 65535)
        {
            if (!markManual && _appSettings.PatGatewayProxyAutoDetect && string.IsNullOrWhiteSpace(portText))
            {
                return true;
            }
            if (updateStatus)
            {
                _statusBox.Text = "代理设置未保存：端口必须是 1 到 65535 的整数；也可以点击“自动检测”。";
            }
            return false;
        }

        if (port == LocalPatGateway.Port && IsLoopbackProxyAddress(address))
        {
            if (updateStatus)
            {
                _statusBox.Text = $"代理设置未保存：{LocalPatGateway.Port} 是本地 PAT 网关端口，不能作为它自己的上游代理。";
            }
            return false;
        }

        var scheme = _appSettings.PatGatewayProxyScheme?.Trim().ToLowerInvariant();
        scheme = scheme is "http" or "https" ? scheme : "http";
        _appSettings.PatGatewayProxyAddress = address;
        _appSettings.PatGatewayProxyPort = port;
        _appSettings.PatGatewayProxyScheme = scheme;
        if (markManual)
        {
            _appSettings.PatGatewayProxyAutoDetect = false;
        }

        var canonical = CodexCliService.BuildPatGatewayProxyUri(_appSettings);
        if (string.IsNullOrWhiteSpace(canonical))
        {
            if (updateStatus)
            {
                _statusBox.Text = "代理设置未保存：地址或端口无法组成有效的 HTTP 代理。";
            }
            return false;
        }

        _appSettings.PatGatewayProxy = canonical;
        _patGatewayProxyAddressBox.Text = address;
        _patGatewayProxyPortBox.Text = port.ToString(CultureInfo.InvariantCulture);
        _themeService.SaveSettings(_appSettings);
        UpdatePatGatewayProxyDetectionLabel();
        if (updateStatus)
        {
            _statusBox.Text = _appSettings.PatGatewayProxyAutoDetect
                ? $"自动检测到本地代理：{address}:{port}"
                : $"PAT 网关上游代理已保存：{address}:{port}";
        }
        return true;
    }

    private async Task DetectLocalPatGatewayProxyAsync(bool updateStatus)
    {
        _proxyDetectionCancellation?.Cancel();
        _proxyDetectionCancellation?.Dispose();
        var cancellation = _proxyDetectionCancellation = new CancellationTokenSource();
        _patGatewayProxyDetectionLabel.Text = "正在检测本地端口…";
        if (updateStatus)
        {
            _statusBox.Text = "正在检测本机 HTTP 代理端口…";
        }

        int? configuredPort = int.TryParse(
            _patGatewayProxyPortBox.Text.Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsedConfiguredPort)
            ? parsedConfiguredPort
            : null;
        var systemProxyPort = (int?)null;
        if (CodexCliService.TryParseProxyEndpoint(
                CodexCliService.GetWindowsProxyUri(),
                out var systemProxyAddress,
                out var detectedSystemProxyPort,
                out var systemProxyScheme) &&
            systemProxyScheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            IsLoopbackProxyAddress(systemProxyAddress))
        {
            systemProxyPort = detectedSystemProxyPort;
        }

        // In automatic mode the Windows loopback proxy is authoritative.  A stale
        // textbox value can belong to an unrelated local service (for example QQ),
        // so it must not outrank the system proxy.  Manual mode still honors the
        // port the user entered as the first candidate.
        var preferredPort = _appSettings.PatGatewayProxyAutoDetect
            ? systemProxyPort
            : configuredPort;

        try
        {
            LocalProxyDetectionResult? result = null;
            if (systemProxyPort.HasValue && _appSettings.PatGatewayProxyAutoDetect)
            {
                // v2rayN may publish the system-proxy setting before its core has
                // finished binding the port.  Give that local endpoint a short
                // background retry window instead of falling back to an unrelated
                // listener during startup.
                for (var attempt = 0; attempt < 15 && result == null; attempt++)
                {
                    result = await LocalProxyDetector.DetectPortAsync(
                        systemProxyPort.Value,
                        cancellation.Token);
                    if (result == null && attempt < 14)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                    }
                }
            }

            result ??= await LocalProxyDetector.DetectAsync(preferredPort, cancellation.Token);
            if (!ReferenceEquals(_proxyDetectionCancellation, cancellation) ||
                cancellation.IsCancellationRequested ||
                _formClosed)
            {
                return;
            }

            if (result == null)
            {
                var retainedAddress = _appSettings.PatGatewayProxyAddress?.Trim();
                var retainedPort = _appSettings.PatGatewayProxyPort;
                var retainedEndpoint = !string.IsNullOrWhiteSpace(retainedAddress) && retainedPort.HasValue
                    ? $"{retainedAddress}:{retainedPort.Value}"
                    : null;
                _patGatewayProxyDetectionLabel.Text = retainedEndpoint == null
                    ? "当前未在线：未检测到本地 HTTP 代理"
                    : $"当前未在线（保留：{retainedEndpoint}）";
                _toolTip.SetToolTip(
                    _patGatewayProxyDetectionLabel,
                    "自动检测未发现正在监听且支持 HTTP CONNECT 的本地代理；已有设置未被修改。");
                if (updateStatus)
                {
                    _statusBox.Text = retainedEndpoint == null
                        ? "自动检测完成：当前未在线，本机未发现可用的 HTTP 代理端口。"
                        : $"自动检测完成：当前未在线，已保留代理设置 {retainedEndpoint}。";
                }
                return;
            }

            _appSettings.PatGatewayProxyAutoDetect = true;
            _appSettings.PatGatewayProxyScheme = result.Scheme;
            _patGatewayProxyAddressBox.Text = result.Address;
            _patGatewayProxyPortBox.Text = result.Port.ToString(CultureInfo.InvariantCulture);
            SaveEditedPatGatewayProxy(updateStatus: false, markManual: false);
            _patGatewayProxyDetectionLabel.Text = $"自动：{result.Address}:{result.Port}";
            _toolTip.SetToolTip(_patGatewayProxyDetectionLabel, result.Description);
            if (updateStatus)
            {
                _statusBox.Text = $"自动检测完成：{result.Address}:{result.Port}（仅本机）。";
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_proxyDetectionCancellation, cancellation))
            {
                _proxyDetectionCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void UpdatePatGatewayProxyDetectionLabel()
    {
        var address = _appSettings.PatGatewayProxyAddress ?? "127.0.0.1";
        _patGatewayProxyDetectionLabel.Text = _appSettings.PatGatewayProxyPort is int port
            ? _appSettings.PatGatewayProxyAutoDetect
                ? $"自动：{address}:{port}"
                : $"手动：{address}:{port}"
            : "自动：待检测";
    }

    private static bool IsLoopbackProxyAddress(string address)
        => LocalProxyDetector.IsLoopbackHost(address);

    private bool TryGetProjectPathForLaunch(out string projectPath)
    {
        var enteredPath = GetProjectPathInputText();
        if (TryNormalizeExistingDirectory(enteredPath, out projectPath))
        {
            if (!_projectPathBox.IsDisposed)
            {
                _projectPathBox.Text = projectPath;
            }
            SaveProjectPath(projectPath);
            return true;
        }

        // The input control can briefly be unavailable while a cached workspace view is
        // being restored.  Prefer the persisted, existing project directory before
        // showing an error; this also repairs a stale/empty textbox in older sessions.
        if (TryNormalizeExistingDirectory(_appSettings.ProjectPath, out var savedProjectPath) &&
            !IsAccountManagerRoot(savedProjectPath))
        {
            projectPath = savedProjectPath;
            if (!_projectPathBox.IsDisposed)
            {
                _projectPathBox.Text = savedProjectPath;
            }

            SaveProjectPath(savedProjectPath);
            return true;
        }

        projectPath = "";
        ShowError($"启动目录不存在或不可用：{enteredPath.Trim()}\n请在“系统配置”中选择有效的项目目录后重试。");
        return false;
    }

    private string GetProjectPathForCodexAppearance()
    {
        if (TryNormalizeExistingDirectory(GetProjectPathInputText(), out var projectPath))
        {
            return projectPath;
        }

        if (TryNormalizeExistingDirectory(_appSettings.ProjectPath, out projectPath) &&
            !IsAccountManagerRoot(projectPath))
        {
            return projectPath;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string GetProjectPathInputText()
    {
        return _projectPathBox.IsDisposed ? string.Empty : _projectPathBox.Text;
    }

    private void BrowseProjectPath()
    {
        var selectedPath = TryNormalizeExistingDirectory(GetProjectPathInputText(), out var currentProjectPath)
            ? currentProjectPath
            : ResolveInitialProjectPath();
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Codex CLI 启动目录",
            SelectedPath = selectedPath
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (!_projectPathBox.IsDisposed)
            {
                _projectPathBox.Text = dialog.SelectedPath;
            }
            SaveEditedProjectPath();
        }
    }

    private void OpenRootFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _store.RootPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        _statusBox.Text = message;
        MessageBox.Show(this, message, "Codex Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private Color GetStatusBackColor(LoginStatus? status)
    {
        if (status == null)
        {
            return Color.FromArgb(30, _palette.MutedTextColor);
        }

        return status.ExitCode == 0 ? Color.FromArgb(48, _palette.SuccessColor) : Color.FromArgb(40, _palette.WarningColor);
    }

    private Color GetStatusForeColor(LoginStatus? status)
    {
        if (status == null)
        {
            return _palette.MutedTextColor;
        }

        return status.ExitCode == 0 ? _palette.SuccessColor : _palette.WarningColor;
    }

    private static string Ellipsize(string value, int length)
    {
        if (value.Length <= length)
        {
            return value;
        }

        return value[..Math.Max(0, length - 3)] + "...";
    }

    private enum ResetCreditStatus
    {
        NotQueried,
        Querying,
        Known,
        Unavailable,
        Failed,
        Resetting
    }

    private sealed record ResetCreditViewState(
        ResetCreditStatus Status,
        long Count,
        DateTimeOffset CheckedAtUtc,
        string? Error);

    private sealed record LiveRateLimitSnapshot(
        double? UsedPercent,
        long? WindowMinutes,
        DateTimeOffset? ResetsAtUtc,
        double? SecondaryUsedPercent,
        long? SecondaryWindowMinutes,
        DateTimeOffset? SecondaryResetsAtUtc,
        UsageCreditsSnapshot? CreditBalance,
        UsageSpendControl? IndividualLimit,
        string? PlanType,
        DateTimeOffset ObservedAtUtc);

    private sealed record CodexAppearanceOption(
        string Id,
        string Label,
        string? PreviewAssetName,
        string Description,
        bool IsDark,
        string CodeThemeId,
        string AccentColor,
        string SurfaceColor,
        string InkColor,
        int Contrast,
        float FocusX,
        float FocusY,
        ThemeMode? RuntimeMode = null,
        string? RuntimePresetId = null,
        string? StaticPreviewAssetName = null);

    private sealed record UsagePriceProfile(
        string DisplayName,
        double InputUsdPerMillion,
        double CachedInputUsdPerMillion,
        double OutputUsdPerMillion,
        double? LongInputUsdPerMillion = null,
        double? LongCachedInputUsdPerMillion = null,
        double? LongOutputUsdPerMillion = null,
        double? CacheWriteUsdPerMillion = null,
        double? LongCacheWriteUsdPerMillion = null,
        UsagePricingPolicy PricingPolicy = UsagePricingPolicy.AccessTokenSub2ApiParity,
        bool UsesLongContextPricing = false)
    {
        public double GetInputRate(bool isLongContext) =>
            isLongContext && UsesLongContextPricing
                ? LongInputUsdPerMillion ?? InputUsdPerMillion
                : InputUsdPerMillion;

        public double GetCachedInputRate(bool isLongContext) =>
            isLongContext && UsesLongContextPricing
                ? LongCachedInputUsdPerMillion ?? CachedInputUsdPerMillion
                : CachedInputUsdPerMillion;

        public double GetCacheWriteRate(bool isLongContext) =>
            isLongContext && UsesLongContextPricing
                ? LongCacheWriteUsdPerMillion ?? CacheWriteUsdPerMillion ?? GetInputRate(true)
                : CacheWriteUsdPerMillion ?? InputUsdPerMillion;

        public double GetOutputRate(bool isLongContext) =>
            isLongContext && UsesLongContextPricing
                ? LongOutputUsdPerMillion ?? OutputUsdPerMillion
                : OutputUsdPerMillion;
    }
}

internal sealed class CircleIconButton : Button
{
    private bool _hovered;
    private bool _pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BaseBackColor { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverBackColor { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressedBackColor { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GlyphColor { get; set; }

    public CircleIconButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TabStop = false;
        Text = string.Empty;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Region?.Dispose();
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, Width - 1, Height - 1);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);

        var backColor = _pressed ? PressedBackColor : _hovered ? HoverBackColor : BaseBackColor;
        var rect = new RectangleF(1.5F, 1.5F, Width - 3F, Height - 3F);

        using (var brush = new SolidBrush(backColor))
        using (var pen = new Pen(BorderColor, 1.2F))
        {
            e.Graphics.FillEllipse(brush, rect);
            e.Graphics.DrawEllipse(pen, rect);
        }

        var glyphColor = Enabled ? GlyphColor : SystemColors.GrayText;
        using var glyphPen = new Pen(glyphColor, 2.8F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        var points = new[]
        {
            new PointF(Width * 0.58F, Height * 0.31F),
            new PointF(Width * 0.40F, Height * 0.50F),
            new PointF(Width * 0.58F, Height * 0.69F)
        };
        e.Graphics.DrawLines(glyphPen, points);
    }
}

internal sealed class RoundedPanel : Panel
{
    // The timer only schedules paints. Motion is based on Stopwatch elapsed time so a busy UI
    // cannot make the meteors slow down, speed up, or visibly jump after a dropped frame.
    // The static half of the scene is cached.  Keep the foreground motion deliberately modest:
    // this is a decorative hero, and repainting it faster competes with the rest of the UI.
    private const int ActiveStarfieldAnimationIntervalMilliseconds = 67;
    // Each visible tail is sampled on the real Bézier route.  Seven short segments are smooth
    // at banner size while keeping the animated overlay inexpensive on the UI thread.
    private const int CurvedMeteorTrailSegmentCount = 7;
    private int _radius = 12;
    private bool _showStarfield;
    private Color _decorationColor = Color.Transparent;
    private System.Windows.Forms.Timer? _starfieldAnimationTimer;
    private Form? _starfieldAnimationHost;
    private readonly Stopwatch _starfieldAnimationClock = new();
    private Bitmap? _starfieldStaticCache;
    private Size _starfieldStaticCacheSize;
    private int _starfieldStaticCacheDpi;
    private Color _starfieldStaticCacheDecorationColor;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius
    {
        get => _radius;
        set
        {
            _radius = Math.Max(0, value);
            UpdateRoundedRegion();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(220, 230, 240);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GradientColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UseGradient { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ShadowColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Elevation { get; set; } = 1;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int AccentWidth { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowTechDecoration { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowStarfield
    {
        get => _showStarfield;
        set
        {
            if (_showStarfield == value)
            {
                return;
            }

            _showStarfield = value;
            if (_showStarfield)
            {
                EnsureStarfieldAnimationTimer();
                AttachStarfieldAnimationHost();
            }
            else
            {
                StopStarfieldAnimation();
                DetachStarfieldAnimationHost();
                DisposeStarfieldStaticCache();
            }
            UpdateStarfieldAnimationState();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DecorationColor
    {
        get => _decorationColor;
        set
        {
            if (_decorationColor.ToArgb() == value.ToArgb())
            {
                return;
            }

            _decorationColor = value;
            DisposeStarfieldStaticCache();
            Invalidate();
        }
    }

    public RoundedPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_showStarfield)
        {
            EnsureStarfieldAnimationTimer();
            AttachStarfieldAnimationHost();
            UpdateStarfieldAnimationState();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopStarfieldAnimation();
        base.OnHandleDestroyed(e);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (_showStarfield)
        {
            AttachStarfieldAnimationHost();
            UpdateStarfieldAnimationState();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateStarfieldAnimationState();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        UpdateRoundedRegion();
        DisposeStarfieldStaticCache();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopStarfieldAnimation();
            DetachStarfieldAnimationHost();
            if (_starfieldAnimationTimer != null)
            {
                _starfieldAnimationTimer.Tick -= StarfieldAnimationTimerOnTick;
                _starfieldAnimationTimer.Dispose();
                _starfieldAnimationTimer = null;
            }
            DisposeStarfieldStaticCache();
        }
        base.Dispose(disposing);
    }

    private void EnsureStarfieldAnimationTimer()
    {
        if (_starfieldAnimationTimer != null)
        {
            return;
        }

        _starfieldAnimationTimer = new System.Windows.Forms.Timer
        {
            Interval = ActiveStarfieldAnimationIntervalMilliseconds
        };
        _starfieldAnimationTimer.Tick += StarfieldAnimationTimerOnTick;
    }

    private void AttachStarfieldAnimationHost()
    {
        var host = FindForm();
        if (ReferenceEquals(host, _starfieldAnimationHost))
        {
            return;
        }

        DetachStarfieldAnimationHost();
        _starfieldAnimationHost = host;
        if (_starfieldAnimationHost == null)
        {
            return;
        }

        _starfieldAnimationHost.Activated += StarfieldAnimationHostOnActivated;
        _starfieldAnimationHost.Deactivate += StarfieldAnimationHostOnDeactivated;
        _starfieldAnimationHost.Resize += StarfieldAnimationHostOnStateChanged;
        _starfieldAnimationHost.VisibleChanged += StarfieldAnimationHostOnStateChanged;
        _starfieldAnimationHost.FormClosed += StarfieldAnimationHostOnFormClosed;
    }

    private void DetachStarfieldAnimationHost()
    {
        if (_starfieldAnimationHost != null)
        {
            _starfieldAnimationHost.Activated -= StarfieldAnimationHostOnActivated;
            _starfieldAnimationHost.Deactivate -= StarfieldAnimationHostOnDeactivated;
            _starfieldAnimationHost.Resize -= StarfieldAnimationHostOnStateChanged;
            _starfieldAnimationHost.VisibleChanged -= StarfieldAnimationHostOnStateChanged;
            _starfieldAnimationHost.FormClosed -= StarfieldAnimationHostOnFormClosed;
            _starfieldAnimationHost = null;
        }
    }

    private void StarfieldAnimationHostOnStateChanged(object? sender, EventArgs e) =>
        UpdateStarfieldAnimationState();

    private void StarfieldAnimationHostOnActivated(object? sender, EventArgs e)
    {
        UpdateStarfieldAnimationState();
        if (CanAnimateStarfield())
        {
            // Do not leave the frozen background frame visible until the first timer tick.
            Invalidate(GetStarfieldAnimationBounds(), invalidateChildren: false);
        }
    }

    private void StarfieldAnimationHostOnDeactivated(object? sender, EventArgs e)
    {
        // Deactivate can be raised before Form.ActiveForm has finished changing.  Stop directly
        // instead of relying on that transient state, and never schedule a background repaint.
        StopStarfieldAnimation();
    }

    private void StarfieldAnimationHostOnFormClosed(object? sender, FormClosedEventArgs e)
    {
        StopStarfieldAnimation();
        DetachStarfieldAnimationHost();
    }

    private void StarfieldAnimationTimerOnTick(object? sender, EventArgs e)
    {
        if (!CanAnimateStarfield())
        {
            UpdateStarfieldAnimationState();
            return;
        }

        // Only the right-hand sky is animated. The title/search area and the rest of the page
        // stay untouched, which prevents needless text repaints and keeps CPU use predictable.
        Invalidate(GetStarfieldAnimationBounds(), invalidateChildren: false);
    }

    internal void RenderStarfieldFrameNow()
    {
        if (!CanAnimateStarfield())
        {
            return;
        }

        Invalidate(GetStarfieldAnimationBounds(), invalidateChildren: false);
        Update();
    }

    private bool CanAnimateStarfield() =>
        _showStarfield &&
        !IsDisposed &&
        IsHandleCreated &&
        Visible &&
        IsStarfieldAnimationHostActive();

    private bool IsStarfieldAnimationHostActive()
    {
        if (_starfieldAnimationHost is not
            {
                IsDisposed: false,
                Visible: true,
                WindowState: not FormWindowState.Minimized
            } host)
        {
            return false;
        }

        // Form.ActiveForm remains the host while native child popups (for example a combo-box
        // drop-down) are open, while ContainsFocus covers focus held by normal child controls.
        return ReferenceEquals(Form.ActiveForm, host) || host.ContainsFocus;
    }

    private void UpdateStarfieldAnimationState()
    {
        if (CanAnimateStarfield())
        {
            EnsureStarfieldAnimationTimer();
            if (!_starfieldAnimationClock.IsRunning)
            {
                _starfieldAnimationClock.Start();
            }
            if (_starfieldAnimationTimer?.Enabled == false)
            {
                _starfieldAnimationTimer.Start();
            }
            return;
        }

        StopStarfieldAnimation();
    }

    private void StopStarfieldAnimation()
    {
        if (_starfieldAnimationTimer?.Enabled == true)
        {
            _starfieldAnimationTimer.Stop();
        }

        if (_starfieldAnimationClock.IsRunning)
        {
            _starfieldAnimationClock.Stop();
        }
    }

    private static float WrapStarfieldPhase(float value)
    {
        value %= 1F;
        return value < 0F ? value + 1F : value;
    }

    private Rectangle GetStarfieldAnimationBounds()
    {
        // Keep timer-driven invalidation out of the title/search column. The static cache is
        // still painted behind transparent labels when Windows asks for a background repaint.
        var left = Math.Clamp((int)MathF.Floor(Width * 0.48F), 0, Math.Max(0, Width - 1));
        return new Rectangle(left, 0, Math.Max(1, Width - left), Math.Max(1, Height));
    }

    private void DisposeStarfieldStaticCache()
    {
        _starfieldStaticCache?.Dispose();
        _starfieldStaticCache = null;
        _starfieldStaticCacheSize = Size.Empty;
        _starfieldStaticCacheDpi = 0;
        _starfieldStaticCacheDecorationColor = Color.Empty;
    }

    private void UpdateRoundedRegion()
    {
        Region?.Dispose();
        if (Width <= 1 || Height <= 1)
        {
            return;
        }

        using var path = UiDesign.CreateRoundedPath(new RectangleF(0F, 0F, Width, Height), Radius);
        Region = new Region(path);
    }

    private RectangleF GetSurfaceBounds()
    {
        var shadowSpace = Math.Max(0, Elevation);
        return new RectangleF(
            0.75F,
            0.75F,
            Math.Max(1F, Width - 1.5F),
            Math.Max(1F, Height - shadowSpace - 1.25F));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);

        var surfaceBounds = GetSurfaceBounds();
        if (Elevation > 0 && ShadowColor.A > 0)
        {
            var shadowBounds = surfaceBounds;
            shadowBounds.Offset(0F, Elevation);
            using var shadowPath = UiDesign.CreateRoundedPath(shadowBounds, Radius);
            using var shadowBrush = new SolidBrush(ShadowColor);
            e.Graphics.FillPath(shadowBrush, shadowPath);
        }

        using var surfacePath = UiDesign.CreateRoundedPath(surfaceBounds, Radius);
        if (UseGradient && GradientColor.A > 0)
        {
            using var gradient = new LinearGradientBrush(
                surfaceBounds,
                BackColor,
                GradientColor,
                LinearGradientMode.Horizontal);
            e.Graphics.FillPath(gradient, surfacePath);
        }
        else
        {
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillPath(brush, surfacePath);
        }

        // The star scene is part of the hero background, not a foreground ornament.
        // Transparent WinForms children ask their parent to repaint OnPaintBackground into
        // their own surface; drawing here keeps the sky visible behind transparent title
        // labels instead of letting those labels replace it with a plain gradient rectangle.
        if (ShowStarfield)
        {
            DrawStarfield(e.Graphics, surfaceBounds);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var surfaceBounds = GetSurfaceBounds();

        if (ShowTechDecoration && DecorationColor.A > 0)
        {
            DrawTechDecoration(e.Graphics, surfaceBounds);
        }

        if (AccentWidth > 0 && AccentColor.A > 0)
        {
            var accentBounds = new RectangleF(
                2F,
                Math.Max(12F, Radius * 0.72F),
                AccentWidth,
                Math.Max(8F, surfaceBounds.Height - Math.Max(24F, Radius * 1.44F)));
            using var accentBrush = new LinearGradientBrush(
                accentBounds,
                AccentColor,
                UiDesign.Blend(AccentColor, Color.White, 0.26F),
                LinearGradientMode.Vertical);
            e.Graphics.FillRoundedRectangle(accentBrush, accentBounds, AccentWidth / 2F);
        }

        using var path = UiDesign.CreateRoundedPath(surfaceBounds, Radius);
        using var pen = new Pen(BorderColor, 1F);
        e.Graphics.DrawPath(pen, path);
    }

    private void DrawTechDecoration(Graphics graphics, RectangleF surfaceBounds)
    {
        using var linePen = new Pen(DecorationColor, 1F);
        using var dotBrush = new SolidBrush(UiDesign.Blend(DecorationColor, Color.White, 0.26F));
        var startX = surfaceBounds.Left + (surfaceBounds.Width * 0.63F);
        var endX = surfaceBounds.Right - 24F;
        var centerY = surfaceBounds.Top + (surfaceBounds.Height * 0.5F);

        graphics.DrawLine(linePen, startX, surfaceBounds.Top + 24F, endX - 46F, surfaceBounds.Top + 24F);
        graphics.DrawLine(linePen, endX - 46F, surfaceBounds.Top + 24F, endX - 22F, surfaceBounds.Top + 48F);
        graphics.DrawLine(linePen, endX - 22F, surfaceBounds.Top + 48F, endX, surfaceBounds.Top + 48F);
        graphics.DrawLine(linePen, startX + 58F, centerY + 22F, endX - 86F, centerY + 22F);
        graphics.DrawLine(linePen, endX - 86F, centerY + 22F, endX - 54F, centerY + 54F);
        graphics.DrawLine(linePen, endX - 54F, centerY + 54F, endX, centerY + 54F);

        foreach (var point in new[]
                 {
                     new PointF(startX, surfaceBounds.Top + 24F),
                     new PointF(endX, surfaceBounds.Top + 48F),
                     new PointF(startX + 58F, centerY + 22F),
                     new PointF(endX, centerY + 54F)
                 })
        {
            graphics.FillEllipse(dotBrush, point.X - 2.5F, point.Y - 2.5F, 5F, 5F);
        }

        using var haloPen = new Pen(Color.FromArgb(Math.Max(12, DecorationColor.A / 2), DecorationColor), 1F);
        graphics.DrawEllipse(haloPen, endX - 148F, centerY - 54F, 112F, 112F);
        graphics.DrawEllipse(haloPen, endX - 130F, centerY - 36F, 76F, 76F);
    }

    private void DrawStarfield(Graphics graphics, RectangleF surfaceBounds)
    {
        var scale = Math.Max(1F, DeviceDpi / 96F);
        var accent = DecorationColor.A > 0
            ? Color.FromArgb(255, DecorationColor)
            : Color.FromArgb(132, 114, 255);
        var starWhite = Color.FromArgb(244, 244, 251, 255);
        var starBlue = UiDesign.Blend(accent, Color.FromArgb(112, 214, 255), 0.44F);
        var starViolet = UiDesign.Blend(accent, Color.FromArgb(231, 139, 255), 0.38F);
        var starRose = UiDesign.Blend(starViolet, Color.FromArgb(255, 118, 206), 0.40F);

        // Nebulae, distant stars, constellations and quiet far-field streaks are all stable.
        // Rendering them once makes the timer tick mostly a handful of tiny moving objects.
        var staticLayer = EnsureStarfieldStaticCache(
            surfaceBounds,
            scale,
            accent,
            starWhite,
            starBlue,
            starViolet,
            starRose);
        graphics.DrawImageUnscaled(staticLayer, 0, 0);

        var seconds = (float)_starfieldAnimationClock.Elapsed.TotalSeconds;
        var breathePrimary = 0.78F + (0.22F * (0.5F + (0.5F * MathF.Sin(seconds * 1.62F))));
        var breatheSecondary = 0.76F + (0.24F * (0.5F + (0.5F * MathF.Sin((seconds * 1.18F) + 2.1F))));
        var breatheTertiary = 0.82F + (0.18F * (0.5F + (0.5F * MathF.Sin((seconds * 2.04F) + 4.3F))));
        DrawStarGlow(graphics, surfaceBounds, 0.70F, 0.18F, 1.9F * scale, starViolet, breathePrimary);
        DrawStarGlow(graphics, surfaceBounds, 0.82F, 0.31F, 3.2F * scale, starWhite, breatheTertiary);
        DrawStarGlow(graphics, surfaceBounds, 0.90F, 0.82F, 2.2F * scale, starRose, breatheSecondary);
        DrawStarGlow(graphics, surfaceBounds, 0.96F, 0.65F, 2.7F * scale, starBlue, breathePrimary);
        DrawMeteorShower(
            graphics,
            surfaceBounds,
            seconds,
            starWhite,
            starBlue,
            starViolet,
            starRose,
            scale);
    }

    private Bitmap EnsureStarfieldStaticCache(
        RectangleF surfaceBounds,
        float scale,
        Color accent,
        Color starWhite,
        Color starBlue,
        Color starViolet,
        Color starRose)
    {
        var cacheSize = ClientSize;
        if (cacheSize.Width <= 0 || cacheSize.Height <= 0)
        {
            cacheSize = new Size(Math.Max(1, Width), Math.Max(1, Height));
        }

        var cacheIsCurrent = _starfieldStaticCache != null &&
                             _starfieldStaticCacheSize == cacheSize &&
                             _starfieldStaticCacheDpi == DeviceDpi &&
                             _starfieldStaticCacheDecorationColor.ToArgb() == DecorationColor.ToArgb();
        if (cacheIsCurrent)
        {
            return _starfieldStaticCache!;
        }

        DisposeStarfieldStaticCache();
        var cache = new Bitmap(cacheSize.Width, cacheSize.Height);
        using (var cacheGraphics = Graphics.FromImage(cache))
        {
            cacheGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            cacheGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            cacheGraphics.CompositingQuality = CompositingQuality.HighQuality;
            cacheGraphics.Clear(Color.Transparent);
            DrawStaticStarfield(
                cacheGraphics,
                surfaceBounds,
                scale,
                accent,
                starWhite,
                starBlue,
                starViolet,
                starRose);
        }

        _starfieldStaticCache = cache;
        _starfieldStaticCacheSize = cacheSize;
        _starfieldStaticCacheDpi = DeviceDpi;
        _starfieldStaticCacheDecorationColor = DecorationColor;
        return cache;
    }

    private static void DrawStaticStarfield(
        Graphics graphics,
        RectangleF surfaceBounds,
        float scale,
        Color accent,
        Color starWhite,
        Color starBlue,
        Color starViolet,
        Color starRose)
    {
        // Layered, deliberately right-weighted nebulae make the banner feel fuller without
        // competing with the left-side title and search controls.
        DrawNebulaGlow(
            graphics,
            new RectangleF(
                surfaceBounds.Left + (surfaceBounds.Width * 0.53F),
                surfaceBounds.Top - (surfaceBounds.Height * 0.52F),
                surfaceBounds.Width * 0.31F,
                surfaceBounds.Height * 1.66F),
            starBlue,
            34);
        DrawNebulaGlow(
            graphics,
            new RectangleF(
                surfaceBounds.Right - (surfaceBounds.Width * 0.34F),
                surfaceBounds.Top - (surfaceBounds.Height * 0.40F),
                surfaceBounds.Width * 0.36F,
                surfaceBounds.Height * 1.62F),
            starViolet,
            52);
        DrawNebulaGlow(
            graphics,
            new RectangleF(
                surfaceBounds.Right - (surfaceBounds.Width * 0.20F),
                surfaceBounds.Top + (surfaceBounds.Height * 0.08F),
                surfaceBounds.Width * 0.24F,
                surfaceBounds.Height * 0.98F),
            starRose,
            30);
        DrawNebulaRibbon(graphics, surfaceBounds, 0.56F, 0.16F, 0.80F, 0.04F, 1.03F, 0.28F, accent, 20, scale);
        DrawNebulaRibbon(graphics, surfaceBounds, 0.60F, 0.88F, 0.82F, 0.66F, 1.04F, 0.76F, starViolet, 22, scale);

        // Fixed normalized coordinates keep the sky stable across paints and page
        // changes. The stars do not jump or shimmer when controls are rebuilt.
        ReadOnlySpan<(float X, float Y, float Radius, byte Alpha, byte Tone)> stars =
        [
            (0.43F, 0.20F, 1.0F, 130, 0), (0.48F, 0.72F, 0.8F, 108, 1),
            (0.53F, 0.34F, 1.3F, 188, 2), (0.57F, 0.81F, 0.8F, 96, 0),
            (0.61F, 0.18F, 0.7F, 118, 1), (0.64F, 0.56F, 1.0F, 156, 2),
            (0.68F, 0.28F, 0.8F, 124, 0), (0.71F, 0.74F, 1.2F, 178, 1),
            (0.74F, 0.46F, 0.7F, 112, 2), (0.77F, 0.14F, 1.0F, 152, 0),
            (0.80F, 0.66F, 0.7F, 106, 1), (0.83F, 0.32F, 1.4F, 202, 2),
            (0.86F, 0.82F, 0.8F, 126, 0), (0.89F, 0.54F, 1.0F, 166, 1),
            (0.92F, 0.20F, 0.7F, 112, 2), (0.95F, 0.70F, 1.2F, 186, 0),
            (0.97F, 0.38F, 0.8F, 132, 1), (0.58F, 0.48F, 0.6F, 92, 2),
            (0.66F, 0.88F, 0.7F, 102, 0), (0.73F, 0.22F, 0.6F, 94, 1),
            (0.87F, 0.12F, 0.7F, 116, 2), (0.94F, 0.47F, 0.6F, 88, 0),
            (0.45F, 0.50F, 0.55F, 74, 1), (0.51F, 0.86F, 0.65F, 92, 3),
            (0.55F, 0.12F, 0.50F, 82, 0), (0.60F, 0.68F, 0.60F, 104, 3),
            (0.63F, 0.37F, 0.50F, 88, 1), (0.69F, 0.56F, 0.60F, 110, 0),
            (0.76F, 0.84F, 0.55F, 96, 2), (0.79F, 0.42F, 0.65F, 116, 3),
            (0.85F, 0.62F, 0.50F, 86, 0), (0.90F, 0.28F, 0.60F, 102, 1),
            (0.93F, 0.88F, 0.55F, 90, 3), (0.98F, 0.59F, 0.50F, 76, 2),
            (0.47F, 0.32F, 0.48F, 82, 2), (0.50F, 0.62F, 0.56F, 94, 0),
            (0.57F, 0.28F, 0.45F, 78, 3), (0.62F, 0.84F, 0.52F, 88, 1),
            (0.67F, 0.45F, 0.44F, 80, 0), (0.72F, 0.92F, 0.50F, 74, 2),
            (0.75F, 0.60F, 0.46F, 92, 1), (0.81F, 0.20F, 0.50F, 84, 3),
            (0.84F, 0.74F, 0.44F, 76, 0), (0.88F, 0.42F, 0.52F, 96, 2),
            (0.91F, 0.16F, 0.46F, 82, 1), (0.96F, 0.90F, 0.48F, 78, 3)
        ];

        foreach (var star in stars)
        {
            var color = star.Tone switch
            {
                1 => starBlue,
                2 => starViolet,
                3 => starRose,
                _ => starWhite
            };
            var radius = star.Radius * scale * 1.26F;
            var x = surfaceBounds.Left + (surfaceBounds.Width * star.X);
            var y = surfaceBounds.Top + (surfaceBounds.Height * star.Y);
            using var brush = new SolidBrush(Color.FromArgb(Math.Min(244, star.Alpha + 42), color));
            graphics.FillEllipse(brush, x - radius, y - radius, radius * 2F, radius * 2F);
        }

        var constellation = new[]
        {
            new PointF(surfaceBounds.Left + surfaceBounds.Width * 0.68F, surfaceBounds.Top + surfaceBounds.Height * 0.28F),
            new PointF(surfaceBounds.Left + surfaceBounds.Width * 0.77F, surfaceBounds.Top + surfaceBounds.Height * 0.14F),
            new PointF(surfaceBounds.Left + surfaceBounds.Width * 0.83F, surfaceBounds.Top + surfaceBounds.Height * 0.32F),
            new PointF(surfaceBounds.Left + surfaceBounds.Width * 0.89F, surfaceBounds.Top + surfaceBounds.Height * 0.54F),
            new PointF(surfaceBounds.Left + surfaceBounds.Width * 0.95F, surfaceBounds.Top + surfaceBounds.Height * 0.70F)
        };
        using var constellationPen = new Pen(Color.FromArgb(38, starBlue), Math.Max(0.8F, scale * 0.75F));
        graphics.DrawLines(constellationPen, constellation);

        // Sparse orbital traces make the sky feel authored rather than randomly dotted.
        // Their low opacity keeps them behind labels and avoids visual noise.
        DrawMicroOrbit(graphics, surfaceBounds, 0.82F, 0.48F, 0.074F, 0.27F, -8F, 194F, 118F, starBlue, scale);
        DrawMicroOrbit(graphics, surfaceBounds, 0.91F, 0.51F, 0.046F, 0.19F, 14F, 24F, 112F, starViolet, scale);

        // Keep the cached sky free of straight meteor streaks. Every meteor in this scene is
        // animated below along a curved route, so a still frame never contradicts its motion.
    }

    private static void DrawMeteorShower(
        Graphics graphics,
        RectangleF surfaceBounds,
        float elapsedSeconds,
        Color starWhite,
        Color starBlue,
        Color starViolet,
        Color starRose,
        float scale)
    {
        // Tone: 0 white, 1 blue, 2 violet, 3 rose. Each meteor owns a duration, a path and a
        // tail span; this prevents the familiar "same four lines moving together" look.
        ReadOnlySpan<(
            float CycleSeconds,
            float PhaseOffset,
            float StartX,
            float StartY,
            float ControlX,
            float ControlY,
            float EndX,
            float EndY,
            float TrailSpan,
            float Length,
            float Intensity,
            byte Tone)> meteors =
        [
            (11.2F, 0.03F, 1.10F, 0.02F, 0.92F, 0.18F, 0.58F, 0.66F, 0.17F, 0.095F, 0.96F, 1),
            (14.8F, 0.26F, 1.08F, 0.78F, 0.93F, 0.53F, 0.64F, 0.16F, 0.18F, 0.078F, 0.84F, 2),
            (9.6F, 0.48F, 1.12F, -0.05F, 0.86F, 0.10F, 0.57F, 0.44F, 0.14F, 0.071F, 0.78F, 0),
            (17.4F, 0.67F, 1.12F, 0.37F, 0.90F, 0.78F, 0.61F, 0.82F, 0.21F, 0.086F, 0.88F, 3),
            (13.1F, 0.81F, 1.05F, 0.86F, 0.87F, 0.55F, 0.65F, 0.26F, 0.16F, 0.066F, 0.72F, 1),
            (20.0F, 0.44F, 1.06F, 0.19F, 0.91F, 0.42F, 0.68F, 0.93F, 0.19F, 0.061F, 0.68F, 2)
        ];

        foreach (var meteor in meteors)
        {
            var color = meteor.Tone switch
            {
                1 => starBlue,
                2 => starViolet,
                3 => starRose,
                _ => starWhite
            };
            DrawCurvedAnimatedMeteor(
                graphics,
                surfaceBounds,
                elapsedSeconds,
                meteor.CycleSeconds,
                meteor.PhaseOffset,
                meteor.StartX,
                meteor.StartY,
                meteor.ControlX,
                meteor.ControlY,
                meteor.EndX,
                meteor.EndY,
                meteor.TrailSpan,
                meteor.Length,
                color,
                scale,
                meteor.Intensity);
        }
    }

    private static void DrawCurvedAnimatedMeteor(
        Graphics graphics,
        RectangleF surfaceBounds,
        float elapsedSeconds,
        float cycleSeconds,
        float phaseOffset,
        float startX,
        float startY,
        float controlX,
        float controlY,
        float endX,
        float endY,
        float trailSpan,
        float normalizedLength,
        Color color,
        float scale,
        float baseIntensity)
    {
        var phase = WrapStarfieldPhase((elapsedSeconds / Math.Max(1F, cycleSeconds)) + phaseOffset);
        var fadeIn = SmoothStarfieldStep(0F, 0.09F, phase);
        var fadeOut = 1F - SmoothStarfieldStep(0.84F, 1F, phase);
        var opacity = fadeIn * fadeOut;
        if (opacity <= 0.004F)
        {
            return;
        }

        var start = new PointF(startX, startY);
        var control = new PointF(controlX, controlY);
        var end = new PointF(endX, endY);
        var intensity = baseIntensity * opacity;

        // Sample the exact history of the moving Bezier point in screen coordinates.  This is
        // deliberately not a tail-to-head chord: every short segment follows the curve and the
        // final segment is also the direction used by the meteor head.
        var tailPhase = Math.Max(0F, phase - trailSpan);
        var visibleSpan = Math.Max(0.0001F, phase - tailPhase);
        Span<PointF> history = stackalloc PointF[CurvedMeteorTrailSegmentCount + 1];
        for (var index = 0; index <= CurvedMeteorTrailSegmentCount; index++)
        {
            var samplePhase = tailPhase + (visibleSpan * index / CurvedMeteorTrailSegmentCount);
            history[index] = ToSurfacePoint(
                surfaceBounds,
                EvaluateQuadraticBezier(start, control, end, samplePhase));
        }

        // Length formerly described a straight-line tail.  Retain its authored variation as a
        // subtle size difference while the actual tail length is now the path history above.
        var visualScale = scale * Math.Clamp(normalizedLength / 0.075F, 0.76F, 1.28F);
        DrawSampledCurvedMeteorTrail(graphics, history, color, visualScale, intensity);
        DrawCurvedMeteorHead(
            graphics,
            history[CurvedMeteorTrailSegmentCount - 1],
            history[CurvedMeteorTrailSegmentCount],
            color,
            visualScale,
            intensity);
    }

    private static PointF EvaluateQuadraticBezier(PointF start, PointF control, PointF end, float phase)
    {
        var t = Math.Clamp(phase, 0F, 1F);
        var inverse = 1F - t;
        return new PointF(
            (inverse * inverse * start.X) + (2F * inverse * t * control.X) + (t * t * end.X),
            (inverse * inverse * start.Y) + (2F * inverse * t * control.Y) + (t * t * end.Y));
    }

    private static PointF ToSurfacePoint(RectangleF surfaceBounds, PointF normalizedPoint) =>
        new(
            surfaceBounds.Left + (surfaceBounds.Width * normalizedPoint.X),
            surfaceBounds.Top + (surfaceBounds.Height * normalizedPoint.Y));

    private static void DrawSampledCurvedMeteorTrail(
        Graphics graphics,
        ReadOnlySpan<PointF> history,
        Color color,
        float scale,
        float intensity)
    {
        if (history.Length < 2 || intensity <= 0.004F)
        {
            return;
        }

        var glowColor = UiDesign.Blend(color, Color.White, 0.20F);
        var ionColor = UiDesign.Blend(color, Color.White, 0.48F);
        var filamentColor = UiDesign.Blend(color, Color.White, 0.76F);
        using var glowPen = new Pen(Color.Transparent, 1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var ionPen = new Pen(Color.Transparent, 1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var filamentPen = new Pen(Color.Transparent, 1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        for (var index = 0; index < history.Length - 1; index++)
        {
            // The wake progresses from almost transparent at its old end to a crisp ion trail
            // immediately behind the head.  Reusing the three Pens avoids per-segment GDI work.
            var strength = MathF.Pow((index + 1F) / (history.Length - 1F), 1.34F);
            var segmentIntensity = intensity * (0.055F + (0.945F * strength));
            glowPen.Color = Color.FromArgb(ScaleMeteorAlpha(segmentIntensity, 58), glowColor);
            glowPen.Width = Math.Max(1.85F, 6.1F * scale * (0.30F + (0.70F * strength)));
            ionPen.Color = Color.FromArgb(ScaleMeteorAlpha(segmentIntensity, 162), ionColor);
            ionPen.Width = Math.Max(0.62F, 1.62F * scale * (0.55F + (0.45F * strength)));
            filamentPen.Color = Color.FromArgb(ScaleMeteorAlpha(segmentIntensity, 176), filamentColor);
            filamentPen.Width = Math.Max(0.42F, 0.64F * scale * (0.54F + (0.46F * strength)));
            graphics.DrawLine(glowPen, history[index], history[index + 1]);
            graphics.DrawLine(ionPen, history[index], history[index + 1]);
            graphics.DrawLine(filamentPen, history[index], history[index + 1]);
        }
    }

    private static float SmoothStarfieldStep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / Math.Max(0.0001F, edge1 - edge0), 0F, 1F);
        return t * t * (3F - (2F * t));
    }

    private static void DrawCurvedMeteorHead(
        Graphics graphics,
        PointF previousPoint,
        PointF head,
        Color color,
        float scale,
        float intensity)
    {
        intensity = Math.Clamp(intensity, 0F, 1F);
        if (intensity <= 0.004F)
        {
            return;
        }
        var directionX = head.X - previousPoint.X;
        var directionY = head.Y - previousPoint.Y;
        var pathLength = Math.Max(1F, MathF.Sqrt((directionX * directionX) + (directionY * directionY)));
        var directionAngle = MathF.Atan2(directionY / pathLength, directionX / pathLength) * 180F / MathF.PI;

        var haloRadius = Math.Max(3.2F, 6.2F * scale * (0.48F + (intensity * 0.52F)));
        var haloBounds = new RectangleF(
            head.X - haloRadius,
            head.Y - haloRadius,
            haloRadius * 2F,
            haloRadius * 2F);
        using (var haloPath = new GraphicsPath())
        {
            haloPath.AddEllipse(haloBounds);
            using var haloBrush = new PathGradientBrush(haloPath)
            {
                CenterColor = Color.FromArgb(ScaleMeteorAlpha(intensity, 82), color),
                SurroundColors = [Color.FromArgb(0, color)],
                FocusScales = new PointF(0.28F, 0.28F)
            };
            graphics.FillPath(haloBrush, haloPath);
        }

        // A small directional lens reads as a moving meteor nucleus without the old horizontal
        // plus vertical ray pair that looked like a cheap cross-shaped star.
        var nucleusLength = Math.Max(2.8F, 5.2F * scale * (0.72F + (intensity * 0.28F)));
        var nucleusWidth = Math.Max(1.4F, 2.15F * scale * (0.76F + (intensity * 0.24F)));
        var nucleusState = graphics.Save();
        graphics.TranslateTransform(head.X, head.Y);
        graphics.RotateTransform(directionAngle);
        using (var nucleus = new LinearGradientBrush(
                   new PointF(-nucleusLength * 0.58F, 0F),
                   new PointF(nucleusLength * 0.50F, 0F),
                   Color.FromArgb(ScaleMeteorAlpha(intensity, 94), color),
                   Color.FromArgb(ScaleMeteorAlpha(intensity, 244), UiDesign.Blend(color, Color.White, 0.78F))))
        {
            graphics.FillEllipse(
                nucleus,
                -nucleusLength * 0.58F,
                -nucleusWidth * 0.5F,
                nucleusLength,
                nucleusWidth);
        }
        graphics.Restore(nucleusState);

        var headRadius = Math.Max(0.9F, 1.3F * scale * (0.76F + (intensity * 0.24F)));
        using var headBrush = new SolidBrush(Color.FromArgb(ScaleMeteorAlpha(intensity, 252),
            UiDesign.Blend(color, Color.White, 0.76F)));
        graphics.FillEllipse(
            headBrush,
            head.X - headRadius,
            head.Y - headRadius,
            headRadius * 2F,
            headRadius * 2F);
    }

    private static int ScaleMeteorAlpha(float intensity, int alpha) =>
        Math.Clamp((int)MathF.Round(Math.Clamp(intensity, 0F, 1F) * alpha), 0, 255);

    private static void DrawNebulaGlow(
        Graphics graphics,
        RectangleF bounds,
        Color color,
        int centerAlpha)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(Math.Clamp(centerAlpha, 0, 255), color),
            SurroundColors = [Color.FromArgb(0, color)],
            FocusScales = new PointF(0.26F, 0.44F)
        };
        graphics.FillPath(brush, path);
    }

    private static void DrawNebulaRibbon(
        Graphics graphics,
        RectangleF surfaceBounds,
        float startX,
        float startY,
        float controlX,
        float controlY,
        float endX,
        float endY,
        Color color,
        int alpha,
        float scale)
    {
        var start = new PointF(
            surfaceBounds.Left + (surfaceBounds.Width * startX),
            surfaceBounds.Top + (surfaceBounds.Height * startY));
        var control = new PointF(
            surfaceBounds.Left + (surfaceBounds.Width * controlX),
            surfaceBounds.Top + (surfaceBounds.Height * controlY));
        var end = new PointF(
            surfaceBounds.Left + (surfaceBounds.Width * endX),
            surfaceBounds.Top + (surfaceBounds.Height * endY));
        var controlOne = new PointF(
            start.X + ((control.X - start.X) * 0.70F),
            start.Y + ((control.Y - start.Y) * 0.70F));
        var controlTwo = new PointF(
            end.X + ((control.X - end.X) * 0.70F),
            end.Y + ((control.Y - end.Y) * 0.70F));

        using var path = new GraphicsPath();
        path.AddBezier(start, controlOne, controlTwo, end);
        using (var glowPen = new Pen(
                   Color.FromArgb(Math.Clamp(alpha, 0, 255), color),
                   Math.Max(4F, 13F * scale))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round,
                   LineJoin = LineJoin.Round
               })
        {
            graphics.DrawPath(glowPen, path);
        }
        using var corePen = new Pen(
            Color.FromArgb(Math.Clamp(alpha + 18, 0, 255), UiDesign.Blend(color, Color.White, 0.28F)),
            Math.Max(0.7F, 1.15F * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawPath(corePen, path);
    }

    private static void DrawMicroOrbit(
        Graphics graphics,
        RectangleF surfaceBounds,
        float normalizedCenterX,
        float normalizedCenterY,
        float normalizedRadiusX,
        float normalizedRadiusY,
        float rotationDegrees,
        float startAngle,
        float sweepAngle,
        Color color,
        float scale)
    {
        var centerX = surfaceBounds.Left + (surfaceBounds.Width * normalizedCenterX);
        var centerY = surfaceBounds.Top + (surfaceBounds.Height * normalizedCenterY);
        var radiusX = Math.Max(10F * scale, surfaceBounds.Width * normalizedRadiusX);
        var radiusY = Math.Max(7F * scale, surfaceBounds.Height * normalizedRadiusY);
        var localBounds = new RectangleF(-radiusX, -radiusY, radiusX * 2F, radiusY * 2F);
        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(centerX, centerY);
            graphics.RotateTransform(rotationDegrees);
            using (var glowPen = new Pen(Color.FromArgb(13, color), Math.Max(1.7F, 2.8F * scale))
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round
                   })
            {
                graphics.DrawArc(glowPen, localBounds, startAngle, sweepAngle);
            }
            using (var corePen = new Pen(Color.FromArgb(46, color), Math.Max(0.58F, 0.78F * scale))
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round
                   })
            {
                graphics.DrawArc(corePen, localBounds, startAngle, sweepAngle);
            }

            foreach (var angle in new[]
                     {
                         startAngle + (sweepAngle * 0.42F),
                         startAngle + sweepAngle
                     })
            {
                var radians = angle * MathF.PI / 180F;
                var point = new PointF(MathF.Cos(radians) * radiusX, MathF.Sin(radians) * radiusY);
                var nodeRadius = Math.Max(0.65F, 0.92F * scale);
                using var nodeHalo = new SolidBrush(Color.FromArgb(24, color));
                graphics.FillEllipse(
                    nodeHalo,
                    point.X - (nodeRadius * 2.1F),
                    point.Y - (nodeRadius * 2.1F),
                    nodeRadius * 4.2F,
                    nodeRadius * 4.2F);
                using var nodeCore = new SolidBrush(Color.FromArgb(116, UiDesign.Blend(color, Color.White, 0.46F)));
                graphics.FillEllipse(
                    nodeCore,
                    point.X - nodeRadius,
                    point.Y - nodeRadius,
                    nodeRadius * 2F,
                    nodeRadius * 2F);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawStarGlow(
        Graphics graphics,
        RectangleF surfaceBounds,
        float normalizedX,
        float normalizedY,
        float radius,
        Color color,
        float intensity = 1F)
    {
        intensity = Math.Clamp(intensity, 0F, 1F);
        var x = surfaceBounds.Left + (surfaceBounds.Width * normalizedX);
        var y = surfaceBounds.Top + (surfaceBounds.Height * normalizedY);
        var haloRadius = radius * 2.25F;
        var haloBounds = new RectangleF(
            x - haloRadius,
            y - haloRadius,
            haloRadius * 2F,
            haloRadius * 2F);
        using (var haloPath = new GraphicsPath())
        {
            haloPath.AddEllipse(haloBounds);
            using var haloBrush = new PathGradientBrush(haloPath)
            {
                CenterColor = Color.FromArgb(ScaleMeteorAlpha(intensity, 58), color),
                SurroundColors = [Color.FromArgb(0, color)],
                FocusScales = new PointF(0.24F, 0.24F)
            };
            graphics.FillPath(haloBrush, haloPath);
        }

        using var rayPen = new Pen(
            Color.FromArgb(ScaleMeteorAlpha(intensity, 132), color),
            Math.Max(0.62F, radius * 0.20F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(rayPen, x - (radius * 1.95F), y, x + (radius * 1.95F), y);
        graphics.DrawLine(rayPen, x, y - (radius * 1.95F), x, y + (radius * 1.95F));
        graphics.DrawLine(
            rayPen,
            x - (radius * 0.72F),
            y - (radius * 0.72F),
            x + (radius * 0.72F),
            y + (radius * 0.72F));
        graphics.DrawLine(
            rayPen,
            x - (radius * 0.72F),
            y + (radius * 0.72F),
            x + (radius * 0.72F),
            y - (radius * 0.72F));

        var coreRadius = Math.Max(0.8F, radius * 0.29F);
        using var coreBrush = new SolidBrush(Color.FromArgb(
            ScaleMeteorAlpha(intensity, 236),
            UiDesign.Blend(color, Color.White, 0.66F)));
        graphics.FillEllipse(
            coreBrush,
            x - coreRadius,
            y - coreRadius,
            coreRadius * 2F,
            coreRadius * 2F);
    }
}
