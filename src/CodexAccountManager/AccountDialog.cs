namespace CodexAccountManager;

public sealed class AccountDialog : Form
{
    private const int AccessTokenAuthIndex = 0;
    private const int CompatibleApiAuthIndex = 1;
    private const int OfficialOAuthAuthIndex = 2;

    private readonly TextBox _nameBox = new();
    private readonly TextBox _homeBox = new();
    private readonly ThemedComboBox _authKindBox = new();
    private readonly TextBox _secretBox = new();
    private readonly ModernButton _updateTokenButton = new();
    private readonly TextBox _providerBox = new();
    private readonly TextBox _baseUrlBox = new();
    private readonly TextBox _modelBox = new();
    private readonly ThemedComboBox _wireApiBox = new();
    private readonly Label _secretLabel = new();
    private readonly Label _secretNote = new();
    private readonly Label _apiNote = new();
    private readonly RoundedPanel _heroCard = new();
    private readonly RoundedPanel _formCard = new();
    private readonly RoundedPanel _secretNoteCard = new();
    private readonly RoundedPanel _apiNoteCard = new();
    private readonly PillLabel _credentialSectionBadge = new();
    private readonly ModernButton _browseButton = new();
    private readonly ModernButton _generateLoginLinkButton = new();
    private readonly ModernButton _saveButton = new();
    private readonly ModernButton _cancelButton = new();
    private readonly Panel _footerDivider = new();
    private readonly ModernInputShell _nameShell;
    private readonly ModernInputShell _homeShell;
    private readonly ModernInputShell _authKindShell;
    private readonly ModernInputShell _secretShell;
    private readonly ModernInputShell _providerShell;
    private readonly ModernInputShell _wireApiShell;
    private readonly ModernInputShell _baseUrlShell;
    private readonly ModernInputShell _modelShell;
    private readonly PillLabel _oauthStatusBadge = new();
    private readonly Control[] _apiControls;
    private readonly ThemePalette _palette;
    private readonly ToolTip _pathToolTip = new()
    {
        AutoPopDelay = 15000,
        InitialDelay = 350,
        ReshowDelay = 100,
        ShowAlways = true
    };
    private readonly bool _isNewAccount;
    private readonly HashSet<string> _reservedHomes;
    private readonly CodexCliService? _codex;
    private readonly AccountRecord? _originalAccount;
    private readonly string _originalHome = "";
    private readonly bool _originalOAuthVerified;
    private string _autoSuggestedHome = "";
    private string _pendingAccessToken = "";
    private string _oauthDraftRoot = "";
    private string _oauthLoginUrl = "";
    private bool _oauthDraftVerified;
    private bool _oauthLoginBusy;
    private CancellationTokenSource? _oauthCancellation;
    private Task<LoginStatus>? _oauthLoginTask;

    public AccountDialog(
        AccountRecord? account,
        string rootPath,
        ThemePalette palette,
        IEnumerable<string>? reservedHomes = null,
        CodexCliService? codex = null)
    {
        _palette = palette;
        _isNewAccount = account == null;
        _codex = codex;
        _originalAccount = account;
        _originalHome = account?.CodexHome ?? "";
        _originalOAuthVerified = account?.IsOfficialOAuth == true &&
                                 codex?.HasOfficialChatGptLogin(account) == true;
        _reservedHomes = new HashSet<string>(
            (reservedHomes ?? []).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        Text = account == null ? "新增账号" : "编辑账号";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(960, 770);
        MinimumSize = new Size(980, 460);
        Font = new Font("Microsoft YaHei UI", 9.25F);
        DoubleBuffered = true;
        ThemeStyler.ApplyDialog(this, palette);

        _heroCard.SetBounds(16, 14, 928, 86);
        _heroCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _heroCard.Radius = 20;
        _heroCard.BackColor = palette.HeroStartColor;
        _heroCard.UseGradient = true;
        _heroCard.GradientColor = palette.HeroEndColor;
        _heroCard.BorderColor = Color.FromArgb(74, palette.AccentColor);
        _heroCard.ShadowColor = Color.FromArgb(48, palette.ShadowColor);
        _heroCard.Elevation = 2;
        _heroCard.ShowTechDecoration = true;
        _heroCard.DecorationColor = Color.FromArgb(54, palette.HeroMutedTextColor);
        Controls.Add(_heroCard);

        var eyebrow = new Label
        {
            Name = "AccountDialogHeroSubtitle",
            Text = "ACCOUNT WORKSPACE",
            Left = 26,
            Top = 9,
            Width = 520,
            Height = 20,
            Font = new Font(Font.FontFamily, 7.5F, FontStyle.Bold),
            ForeColor = palette.HeroMutedTextColor,
            BackColor = Color.Transparent,
            UseMnemonic = false
        };
        _heroCard.Controls.Add(eyebrow);

        var heading = new Label
        {
            Name = "AccountDialogHeroTitle",
            Text = account == null ? "新增账号" : "编辑账号",
            Left = 24,
            Top = 35,
            Width = 520,
            Height = 38,
            Font = new Font(Font.FontFamily, 13.5F, FontStyle.Bold),
            ForeColor = palette.HeroTextColor,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = true,
            UseMnemonic = false
        };
        _heroCard.Controls.Add(heading);

        var safetyBadge = new PillLabel
        {
            Name = "AccountDialogIsolationBadge",
            Text = "●  凭据隔离",
            Left = 736,
            Top = 27,
            Width = 164,
            Height = 34,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Font = new Font(Font.FontFamily, 8.3F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            FillColor = Color.FromArgb(38, Color.White),
            StrokeColor = Color.FromArgb(78, Color.White),
            ForeColor = palette.HeroTextColor,
            UseMnemonic = false
        };
        _heroCard.Controls.Add(safetyBadge);

        _formCard.SetBounds(16, 112, 928, 582);
        _formCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formCard.Radius = 20;
        _formCard.BackColor = palette.SurfaceColor;
        _formCard.BorderColor = UiDesign.Blend(palette.BorderColor, palette.PrimaryColor, 0.12F);
        _formCard.UseGradient = true;
        _formCard.GradientColor = UiDesign.Blend(palette.SurfaceColor, palette.PrimaryColor, 0.018F);
        _formCard.ShadowColor = Color.FromArgb(34, palette.ShadowColor);
        _formCard.Elevation = 2;
        _formCard.ShowTechDecoration = false;
        Controls.Add(_formCard);

        var profileBadge = MakeSectionBadge("账号资料", 20, 14, 108);
        _formCard.Controls.Add(profileBadge);

        _credentialSectionBadge.Text = "登录凭据";
        _credentialSectionBadge.SetBounds(20, 158, 108, 28);
        ApplySectionBadgeStyle(_credentialSectionBadge);
        _formCard.Controls.Add(_credentialSectionBadge);

        var sectionDivider = new Panel
        {
            Name = "AccountDialogSectionDivider",
            Left = 20,
            Top = 145,
            Width = 888,
            Height = 1,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = UiDesign.Blend(palette.DividerColor, palette.PrimaryColor, 0.16F)
        };
        _formCard.Controls.Add(sectionDivider);

        var nameLabel = MakeLabel("账号名称", 22, 47, 124);
        _formCard.Controls.Add(nameLabel);

        _nameBox.Text = account?.Name ?? "";
        _nameBox.TextChanged += (_, _) => UpdateSuggestedHomeForName();
        _nameShell = MakeInputShell(_nameBox, 164, 40, 744);
        _nameShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formCard.Controls.Add(_nameShell);

        var homeLabel = MakeLabel("账号目录", 22, 99, 130);
        _formCard.Controls.Add(homeLabel);

        _homeBox.Text = account?.CodexHome ?? SuggestNewCodexHome(_nameBox.Text);
        _autoSuggestedHome = _homeBox.Text;
        _homeBox.TextChanged += (_, _) =>
        {
            UpdateHomePathToolTip();
            UpdateAuthMode();
        };
        _homeShell = MakeInputShell(_homeBox, 164, 92, 568);
        _homeShell.Name = "CodexHomeInputShell";
        _homeShell.Padding = new Padding(18, 9, 12, 8);
        _homeShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formCard.Controls.Add(_homeShell);
        _homeShell.PerformLayout();
        UpdateHomePathToolTip();

        _browseButton.Text = "选择目录";
        _browseButton.SetBounds(744, 92, 164, 44);
        _browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        ConfigureDialogTonalButton(_browseButton, "⌁");
        _browseButton.Click += (_, _) =>
        {
            using var folder = new FolderBrowserDialog
            {
                Description = "选择或创建账号目录",
                SelectedPath = Directory.Exists(_homeBox.Text) ? _homeBox.Text : rootPath
            };
            if (folder.ShowDialog(this) == DialogResult.OK)
            {
                _homeBox.Text = folder.SelectedPath;
            }
        };
        _formCard.Controls.Add(_browseButton);

        var authLabel = MakeLabel("登录方式", 22, 195, 124);
        _formCard.Controls.Add(authLabel);

        _authKindBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _authKindBox.Items.AddRange(["Access Token", "兼容 API", "通过 ChatGPT 登录（官方）"]);
        _authKindBox.SelectedIndex = account?.IsOfficialOAuth == true
            ? OfficialOAuthAuthIndex
            : account?.IsCompatibleApi == true
                ? CompatibleApiAuthIndex
                : AccessTokenAuthIndex;
        _authKindBox.SelectedIndexChanged += (_, _) =>
        {
            HandleOAuthSelectionChanged();
            UpdateAuthMode();
        };
        _authKindShell = MakeInputShell(_authKindBox, 164, 188, 500);
        _authKindShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formCard.Controls.Add(_authKindShell);

        _generateLoginLinkButton.Text = "生成登录链接";
        _generateLoginLinkButton.SetBounds(676, 188, 232, 44);
        _generateLoginLinkButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _generateLoginLinkButton.Radius = 12;
        _generateLoginLinkButton.Padding = new Padding(12, 0, 12, 0);
        _generateLoginLinkButton.AutoShrinkText = false;
        _generateLoginLinkButton.UseMnemonic = false;
        ThemeStyler.ApplyPrimaryButton(_generateLoginLinkButton, palette);
        _generateLoginLinkButton.Click += async (_, _) => await GenerateOfficialOAuthLinkAsync();
        _formCard.Controls.Add(_generateLoginLinkButton);

        _oauthStatusBadge.SetBounds(164, 240, 744, 44);
        _oauthStatusBadge.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _oauthStatusBadge.TextAlign = ContentAlignment.MiddleLeft;
        _oauthStatusBadge.Padding = new Padding(16, 0, 16, 0);
        _oauthStatusBadge.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        _oauthStatusBadge.FillColor = UiDesign.Blend(palette.SurfaceAltColor, palette.PrimaryColor, 0.05F);
        _oauthStatusBadge.StrokeColor = UiDesign.Blend(palette.BorderColor, palette.PrimaryColor, 0.24F);
        _oauthStatusBadge.ForeColor = palette.MutedTextColor;
        _oauthStatusBadge.UseMnemonic = false;
        _formCard.Controls.Add(_oauthStatusBadge);

        _secretLabel.SetBounds(22, 247, 130, 38);
        _secretLabel.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        _secretLabel.TextAlign = ContentAlignment.MiddleLeft;
        ThemeStyler.ApplyLabel(_secretLabel, palette);
        _formCard.Controls.Add(_secretLabel);

        _secretBox.UseSystemPasswordChar = true;
        _secretShell = MakeInputShell(_secretBox, 164, 240, 744);
        _secretShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formCard.Controls.Add(_secretShell);

        _updateTokenButton.SetBounds(164, 240, 240, 44);
        _updateTokenButton.Text = "更新 Token";
        _updateTokenButton.UseMnemonic = false;
        _updateTokenButton.AutoShrinkText = false;
        _updateTokenButton.Radius = 12;
        _updateTokenButton.Padding = new Padding(14, 0, 14, 0);
        ThemeStyler.ApplyPrimaryButton(_updateTokenButton, palette);
        _updateTokenButton.Click += (_, _) =>
        {
            using var tokenDialog = new TokenDialog(
                string.IsNullOrWhiteSpace(_nameBox.Text) ? "当前账号" : _nameBox.Text.Trim(),
                _homeBox.Text.Trim(),
                _palette);
            if (tokenDialog.ShowDialog(this) != DialogResult.OK ||
                string.IsNullOrWhiteSpace(tokenDialog.AccessTokenValue))
            {
                return;
            }

            _pendingAccessToken = tokenDialog.AccessTokenValue;
            _updateTokenButton.Text = "已选择新 Token";
            UpdateAuthMode();
        };
        _formCard.Controls.Add(_updateTokenButton);

        ConfigureInfoCard(_secretNoteCard, 164, 292, 744);
        _formCard.Controls.Add(_secretNoteCard);

        _secretNote.Dock = DockStyle.Fill;
        _secretNote.Padding = new Padding(14, 10, 14, 10);
        _secretNote.Font = new Font(Font.FontFamily, 8.5F);
        _secretNote.AutoEllipsis = false;
        _secretNote.TextAlign = ContentAlignment.MiddleLeft;
        _secretNote.UseCompatibleTextRendering = true;
        _secretNote.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_secretNote, palette, true);
        _secretNoteCard.Controls.Add(_secretNote);

        var providerLabel = MakeLabel("Provider", 22, 371, 124);
        _formCard.Controls.Add(providerLabel);
        _providerBox.Text = string.IsNullOrWhiteSpace(account?.ApiProviderName) ? "OpenAI" : account.ApiProviderName;
        _providerShell = MakeInputShell(_providerBox, 164, 364, 270);
        _formCard.Controls.Add(_providerShell);

        var wireApiLabel = MakeLabel("Wire API", 464, 371, 84);
        _formCard.Controls.Add(wireApiLabel);
        _wireApiBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _wireApiBox.Items.AddRange(["responses", "chat"]);
        _wireApiBox.SelectedItem = string.IsNullOrWhiteSpace(account?.ApiWireApi) ? "responses" : account.ApiWireApi;
        if (_wireApiBox.SelectedIndex < 0)
        {
            _wireApiBox.SelectedIndex = 0;
        }
        _wireApiShell = MakeInputShell(_wireApiBox, 558, 364, 350);
        _wireApiShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formCard.Controls.Add(_wireApiShell);

        var baseUrlLabel = MakeLabel("Base URL", 22, 423, 124);
        _formCard.Controls.Add(baseUrlLabel);
        _baseUrlBox.Text = account?.ApiBaseUrl ?? "";
        _baseUrlShell = MakeInputShell(_baseUrlBox, 164, 416, 744);
        _baseUrlShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formCard.Controls.Add(_baseUrlShell);

        var modelLabel = MakeLabel("Model", 22, 475, 124);
        _formCard.Controls.Add(modelLabel);
        _modelBox.Text = string.IsNullOrWhiteSpace(account?.ApiModel) ? "gpt-5.5" : account.ApiModel;
        _modelShell = MakeInputShell(_modelBox, 164, 468, 744);
        _modelShell.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formCard.Controls.Add(_modelShell);

        ConfigureInfoCard(_apiNoteCard, 164, 520, 744);
        _formCard.Controls.Add(_apiNoteCard);
        _apiNote.Text = "兼容 API 会写入该账号目录的 config.toml，并把 API Key 保存到该目录的 auth.json。";
        _apiNote.Dock = DockStyle.Fill;
        _apiNote.Padding = new Padding(14, 9, 14, 9);
        _apiNote.Font = new Font(Font.FontFamily, 8.5F);
        _apiNote.AutoEllipsis = false;
        _apiNote.TextAlign = ContentAlignment.MiddleLeft;
        _apiNote.UseCompatibleTextRendering = true;
        _apiNote.UseMnemonic = false;
        ThemeStyler.ApplyLabel(_apiNote, palette, true);
        _apiNoteCard.Controls.Add(_apiNote);

        _apiControls =
        [
            providerLabel, _providerShell, wireApiLabel, _wireApiShell,
            baseUrlLabel, _baseUrlShell, modelLabel, _modelShell, _apiNoteCard
        ];

        _footerDivider.SetBounds(16, 704, 928, 1);
        _footerDivider.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _footerDivider.BackColor = UiDesign.Blend(palette.DividerColor, palette.PrimaryColor, 0.10F);
        Controls.Add(_footerDivider);

        _saveButton.Text = "保存账号";
        _saveButton.SetBounds(672, 720, 140, 44);
        _saveButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _saveButton.DialogResult = DialogResult.OK;
        _saveButton.Radius = 12;
        _saveButton.Padding = new Padding(14, 0, 14, 0);
        _saveButton.AutoShrinkText = false;
        ThemeStyler.ApplyPrimaryButton(_saveButton, palette);

        _cancelButton.Text = "取消";
        _cancelButton.SetBounds(824, 720, 120, 44);
        _cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _cancelButton.DialogResult = DialogResult.Cancel;
        ConfigureDialogTonalButton(_cancelButton);
        Controls.Add(_saveButton);
        Controls.Add(_cancelButton);
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;

        UpdateAuthMode();
        Shown += (_, _) => BeginInvoke(() =>
        {
            // A long suggested CODEX_HOME can otherwise retain an end-of-line scroll offset
            // on high-DPI displays and hide the drive letter when the dialog first appears.
            _homeBox.Focus();
            ResetHomePathViewport();
            _nameBox.Focus();
            BeginInvoke(() =>
            {
                _homeShell.PerformLayout();
                ResetHomePathViewport();
            });
        });
    }

    public string AccountNameValue => _nameBox.Text.Trim();
    public string CodexHomeValue => _homeBox.Text.Trim();
    public string AuthKindValue => _authKindBox.SelectedIndex switch
    {
        CompatibleApiAuthIndex => AccountAuthKind.CompatibleApi,
        OfficialOAuthAuthIndex => AccountAuthKind.OfficialOAuth,
        _ => AccountAuthKind.AccessToken
    };
    public string AccessTokenValue => IsAccessTokenSelected
        ? (_isNewAccount ? _secretBox.Text : _pendingAccessToken).Trim()
        : "";
    public string ApiKeyValue => IsCompatibleApiSelected ? _secretBox.Text.Trim() : "";
    public string ApiProviderNameValue => _providerBox.Text.Trim();
    public string ApiBaseUrlValue => _baseUrlBox.Text.Trim();
    public string ApiModelValue => _modelBox.Text.Trim();
    public string ApiWireApiValue => (_wireApiBox.SelectedItem as string ?? "responses").Trim();
    public string? OfficialOAuthCredentialSourcePath =>
        IsOfficialOAuthSelected && _oauthDraftVerified && !string.IsNullOrWhiteSpace(_oauthDraftRoot)
            ? Path.Combine(_oauthDraftRoot, "auth.json")
            : null;

    private bool IsAccessTokenSelected => _authKindBox.SelectedIndex == AccessTokenAuthIndex;
    private bool IsCompatibleApiSelected => _authKindBox.SelectedIndex == CompatibleApiAuthIndex;
    private bool IsOfficialOAuthSelected => _authKindBox.SelectedIndex == OfficialOAuthAuthIndex;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _oauthCancellation?.Cancel();
            ClearOAuthLoginClipboard();
            ScheduleOAuthDraftCleanup();
            _pathToolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    internal static void ValidateExistingTokenEditLayout()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-account-dialog-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var longHome = Path.Combine(
                root,
                "long-account-directory-for-complete-codex-home-path-visibility");
            var account = new AccountRecord
            {
                Name = "layout@example.com",
                CodexHome = longHome,
                AuthKind = AccountAuthKind.AccessToken
            };
            var palette = new ThemeService(root).GetPalette(ThemeMode.Light);
            using var dialog = new AccountDialog(
                account,
                root,
                palette);
            var homeLabel = dialog._formCard.Controls
                .OfType<Label>()
                .Single(label => label.Text == "账号目录");
            var measured = TextRenderer.MeasureText(
                homeLabel.Text,
                homeLabel.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (!dialog._updateTokenButton.Enabled ||
                dialog._updateTokenButton.Text != "更新 Token" ||
                dialog._secretBox.Enabled ||
                !string.IsNullOrEmpty(dialog.AccessTokenValue) ||
                homeLabel.Width < measured.Width + 12 ||
                dialog.ClientSize.Width < 960 ||
                dialog._homeShell.Width < 568 ||
                dialog._pathToolTip.GetToolTip(dialog._homeBox) != longHome ||
                dialog._pathToolTip.GetToolTip(dialog._homeShell) != longHome ||
                dialog._browseButton.Left - dialog._homeShell.Right < 12 ||
                dialog._heroCard.Parent != dialog ||
                dialog._formCard.Parent != dialog ||
                dialog._nameShell.Parent != dialog._formCard ||
                dialog._homeShell.Parent != dialog._formCard ||
                dialog._secretShell.Parent != dialog._formCard ||
                dialog._secretNoteCard.Parent != dialog._formCard ||
                dialog._secretNote.Parent != dialog._secretNoteCard ||
                dialog._heroCard.Controls.Find("AccountDialogHeroTitle", false).Length != 1 ||
                dialog._heroCard.Controls.Find("AccountDialogIsolationBadge", false).Length != 1)
            {
                throw new InvalidOperationException(
                    "The account editor must preserve existing credentials and keep the hero/form hierarchy intact.");
            }
            ValidateDialogGeometry(dialog, AccountAuthKind.AccessToken);

            using var newDialog = new AccountDialog(
                null,
                root,
                palette);
            if (!newDialog._secretBox.Enabled ||
                newDialog._secretBox.Parent != newDialog._secretShell ||
                newDialog._secretShell.Parent != newDialog._formCard ||
                newDialog._secretNote.Parent != newDialog._secretNoteCard ||
                !string.IsNullOrEmpty(newDialog._pathToolTip.GetToolTip(newDialog._secretNote)))
            {
                throw new InvalidOperationException(
                    "The new-account token form must keep every editable field inside the real form hierarchy.");
            }
            ValidateDialogGeometry(newDialog, AccountAuthKind.AccessToken);

            newDialog._authKindBox.SelectedIndex = CompatibleApiAuthIndex;
            ValidateDialogGeometry(newDialog, AccountAuthKind.CompatibleApi);
            newDialog._authKindBox.SelectedIndex = OfficialOAuthAuthIndex;
            if (newDialog.AuthKindValue != AccountAuthKind.OfficialOAuth ||
                !string.IsNullOrEmpty(newDialog.AccessTokenValue) ||
                !string.IsNullOrEmpty(newDialog.ApiKeyValue) ||
                newDialog._secretLabel.Visible ||
                newDialog._secretShell.Visible ||
                newDialog._secretBox.Visible ||
                newDialog._updateTokenButton.Visible ||
                newDialog._generateLoginLinkButton.Parent != newDialog._formCard ||
                newDialog._oauthStatusBadge.Parent != newDialog._formCard ||
                newDialog._saveButton.Enabled ||
                newDialog._generateLoginLinkButton.Left - newDialog._authKindShell.Right < 8 ||
                newDialog._apiControls.Any(control => control.Visible || control.Enabled))
            {
                throw new InvalidOperationException(
                    "Official OAuth mode must expose link generation, hide pasted secrets, and block saving before login.");
            }
            newDialog._oauthDraftRoot = Path.Combine(
                Path.GetTempPath(),
                "codex-account-manager-oauth-draft-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(newDialog._oauthDraftRoot);
            newDialog._oauthDraftVerified = true;
            newDialog.UpdateAuthMode();
            if (!newDialog._saveButton.Enabled ||
                newDialog._oauthStatusBadge.Text != "✓ 已登录" ||
                string.IsNullOrWhiteSpace(newDialog.OfficialOAuthCredentialSourcePath))
            {
                throw new InvalidOperationException(
                    "Official OAuth accounts must become saveable only after the verified-login state is set.");
            }
            ValidateDialogGeometry(newDialog, AccountAuthKind.OfficialOAuth);
            ValidateRuntimeScaledModeSwitch(root, palette, 1.5F);
            ValidateRuntimeScaledModeSwitch(root, palette, 2F);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // A temporary UI handle should not hide the validation result.
            }
        }
    }

    private static void ValidateRuntimeScaledModeSwitch(string root, ThemePalette palette, float scale)
    {
        using var dialog = new AccountDialog(null, root, palette);
        dialog.Scale(new SizeF(scale, scale));
        dialog.PerformLayout();
        dialog._authKindBox.SelectedIndex = CompatibleApiAuthIndex;
        ValidateDialogGeometry(dialog, AccountAuthKind.CompatibleApi);
        dialog._authKindBox.SelectedIndex = OfficialOAuthAuthIndex;
        ValidateDialogGeometry(dialog, AccountAuthKind.OfficialOAuth);
        dialog._authKindBox.SelectedIndex = AccessTokenAuthIndex;
        ValidateDialogGeometry(dialog, AccountAuthKind.AccessToken);
    }

    private static void ValidateDialogGeometry(AccountDialog dialog, string authKind)
    {
        var isApi = authKind == AccountAuthKind.CompatibleApi;
        var isOAuth = authKind == AccountAuthKind.OfficialOAuth;
        dialog.PerformLayout();
        dialog._formCard.PerformLayout();
        dialog._secretNoteCard.PerformLayout();
        dialog._apiNoteCard.PerformLayout();
        var scale = dialog.GetCurrentLayoutScale();
        var noteCard = isApi ? dialog._apiNoteCard : dialog._secretNoteCard;
        var note = isApi ? dialog._apiNote : dialog._secretNote;
        var availableWidth = Math.Max(
            120,
            noteCard.ClientSize.Width - note.Padding.Left - note.Padding.Right - 4);
        var measuredNote = TextRenderer.MeasureText(
            note.Text,
            note.Font,
            new Size(availableWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        var expectedNoteHeight = measuredNote.Height + note.Padding.Top + note.Padding.Bottom;
        var footerGap = dialog._saveButton.Top - dialog._formCard.Bottom;
        var bottomMargin = dialog.ClientSize.Height - dialog._saveButton.Bottom;
        var homeGap = dialog._browseButton.Left - dialog._homeShell.Right;
        var failures = new List<string>();
        if (noteCard.Height < expectedNoteHeight) failures.Add($"note-height={noteCard.Height}<{expectedNoteHeight}");
        if (noteCard.Bottom > dialog._formCard.ClientSize.Height - Math.Max(10, (int)Math.Floor(12 * scale))) failures.Add("note-outside-card");
        if (footerGap < Math.Max(10, (int)Math.Floor(12 * scale))) failures.Add($"footer-gap={footerGap}");
        if (bottomMargin < Math.Max(10, (int)Math.Floor(12 * scale))) failures.Add($"bottom-margin={bottomMargin}");
        if (homeGap < Math.Max(8, (int)Math.Floor(8 * scale))) failures.Add($"home-gap={homeGap}");
        if (dialog._footerDivider.Top <= dialog._formCard.Bottom) failures.Add("footer-divider-overlap");
        if (dialog._saveButton.Top != dialog._cancelButton.Top) failures.Add("footer-button-misalignment");
        if (dialog._saveButton.Right > dialog.ClientSize.Width) failures.Add($"save-right={dialog._saveButton.Right}>client={dialog.ClientSize.Width}");
        if (dialog._cancelButton.Right > dialog.ClientSize.Width) failures.Add($"cancel-right={dialog._cancelButton.Right}>client={dialog.ClientSize.Width}");
        if (!dialog._heroCard.UseGradient) failures.Add("hero-gradient-missing");
        if (dialog._formCard.ShowTechDecoration) failures.Add("body-decoration-enabled");
        if (dialog._browseButton.UseSurfaceSheen) failures.Add("browse-sheen-enabled");
        if (dialog._cancelButton.UseSurfaceSheen) failures.Add("cancel-sheen-enabled");
        if (!ButtonTextFits(dialog._browseButton)) failures.Add("browse-text-clipped");
        if (isOAuth && !ButtonTextFits(dialog._generateLoginLinkButton)) failures.Add("oauth-link-text-clipped");
        if (!ButtonTextFits(dialog._saveButton)) failures.Add("save-text-clipped");
        if (!ButtonTextFits(dialog._cancelButton)) failures.Add("cancel-text-clipped");
        if (isApi && (dialog._apiNote.Parent != dialog._apiNoteCard || !dialog._apiControls.Contains(dialog._apiNoteCard))) failures.Add("api-note-hierarchy");
        if (isOAuth &&
            (dialog._secretLabel.Visible ||
             dialog._secretShell.Visible ||
             dialog._secretBox.Visible ||
             dialog._updateTokenButton.Visible ||
             dialog._apiControls.Any(control => control.Visible || control.Enabled))) failures.Add("oauth-credential-fields-visible");
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Account dialog layout failed at scale {scale:0.##} in {authKind} mode: {string.Join(", ", failures)}.");
        }
    }

    private static bool ButtonTextFits(ModernButton button)
    {
        var measured = TextRenderer.MeasureText(
            button.Text,
            button.Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        var horizontalPadding = Math.Max(10, Math.Max(button.Padding.Left, button.Padding.Right));
        var iconSpace = string.IsNullOrEmpty(button.IconText) ? 0 : button.IconWidth + 8;
        return measured.Width <= button.ClientSize.Width - (horizontalPadding * 2) - iconSpace;
    }

    private void UpdateSuggestedHomeForName()
    {
        if (!_isNewAccount ||
            !string.Equals(_homeBox.Text, _autoSuggestedHome, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _autoSuggestedHome = SuggestNewCodexHome(_nameBox.Text);
        _homeBox.Text = _autoSuggestedHome;
    }

    private void UpdateHomePathToolTip()
    {
        var fullPath = _homeBox.Text.Trim();
        _pathToolTip.SetToolTip(_homeBox, fullPath);
        _pathToolTip.SetToolTip(_homeShell, fullPath);
    }

    private void ResetHomePathViewport()
    {
        if (!_homeBox.IsHandleCreated)
        {
            return;
        }

        _homeBox.Select(0, 0);
        SendMessage(_homeBox.Handle, EmSetSel, IntPtr.Zero, IntPtr.Zero);
        SendMessage(_homeBox.Handle, WmHScroll, (IntPtr)SbLeft, IntPtr.Zero);
        SendMessage(_homeBox.Handle, EmScrollCaret, IntPtr.Zero, IntPtr.Zero);
        _homeBox.ScrollToCaret();
        SendMessage(_homeBox.Handle, WmHScroll, (IntPtr)SbLeft, IntPtr.Zero);
    }

    private string SuggestNewCodexHome(string accountName)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex-accounts");
        var baseName = ToSafeDirectoryName(accountName);
        var candidate = Path.Combine(root, baseName);
        var sequence = 2;
        while (Directory.Exists(candidate) || _reservedHomes.Contains(NormalizePath(candidate)))
        {
            candidate = Path.Combine(root, $"{baseName}-{sequence}");
            sequence++;
        }

        return candidate;
    }

    private static string ToSafeDirectoryName(string accountName)
    {
        var safe = new string(accountName.Trim()
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')
            .ToArray())
            .Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "new-account" : safe;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private Label MakeLabel(string text, int left, int top, int width)
    {
        var measured = TextRenderer.MeasureText(
            text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        var label = new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = Math.Max(width, measured.Width + 18),
            Height = Math.Max(28, measured.Height + 8),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        ThemeStyler.ApplyLabel(label, _palette);
        return label;
    }

    private ModernInputShell MakeInputShell(Control input, int left, int top, int width)
    {
        input.Font = new Font(Font.FontFamily, 9.4F);
        var shell = new ModernInputShell(input)
        {
            Left = left,
            Top = top,
            Width = width,
            Height = 44,
            Radius = 11
        };
        shell.ApplyPalette(_palette);
        return shell;
    }

    private PillLabel MakeSectionBadge(string text, int left, int top, int width)
    {
        var badge = new PillLabel
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 28,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false
        };
        ApplySectionBadgeStyle(badge);
        return badge;
    }

    private void ApplySectionBadgeStyle(PillLabel badge)
    {
        badge.FillColor = UiDesign.Blend(_palette.SurfaceAltColor, _palette.PrimaryColor, 0.08F);
        badge.StrokeColor = Color.FromArgb(78, _palette.AccentColor);
        badge.ForeColor = _palette.PrimaryColor;
        badge.Font = new Font(Font.FontFamily, 8.2F, FontStyle.Bold);
        badge.BackColor = Color.Transparent;
    }

    private void ConfigureInfoCard(RoundedPanel card, int left, int top, int width)
    {
        card.SetBounds(left, top, width, 72);
        card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        card.Radius = 12;
        card.BackColor = UiDesign.Blend(_palette.SurfaceAltColor, _palette.PrimaryColor, 0.035F);
        card.BorderColor = UiDesign.Blend(_palette.BorderColor, _palette.PrimaryColor, 0.22F);
        card.UseGradient = true;
        card.GradientColor = UiDesign.Blend(_palette.SurfaceColor, _palette.SecondaryAccentColor, 0.045F);
        card.ShadowColor = Color.Transparent;
        card.Elevation = 0;
        card.AccentColor = _palette.AccentColor;
        card.AccentWidth = 3;
        card.ShowTechDecoration = false;
    }

    private void ConfigureDialogTonalButton(ModernButton button, string iconText = "")
    {
        var dark = ThemeStyler.IsDark(_palette);
        var baseColor = UiDesign.Blend(_palette.CardColor, _palette.PrimaryColor, dark ? 0.13F : 0.045F);
        var hoverColor = UiDesign.Blend(_palette.CardColor, _palette.PrimaryColor, dark ? 0.22F : 0.10F);
        var pressedColor = UiDesign.Blend(hoverColor, _palette.PrimaryColor, dark ? 0.18F : 0.14F);
        var textColor = dark
            ? UiDesign.Blend(_palette.AccentColor, Color.White, 0.28F)
            : _palette.PrimaryColor;
        button.Tag = "dialog-tonal";
        button.Radius = 12;
        button.Padding = new Padding(12, 0, 12, 0);
        button.Font = new Font(Font.FontFamily, 8.9F, FontStyle.Bold);
        button.UseMnemonic = false;
        button.AutoShrinkText = false;
        button.BaseBackColor = baseColor;
        button.HoverBackColor = hoverColor;
        button.PressedBackColor = pressedColor;
        button.BorderColor = UiDesign.Blend(_palette.BorderColor, _palette.PrimaryColor, 0.34F);
        button.TextColor = textColor;
        button.GradientBackColor = Color.FromArgb(dark ? 34 : 26, _palette.SecondaryAccentColor);
        button.ShadowColor = Color.Transparent;
        button.UseSurfaceSheen = false;
        button.FocusColor = Color.FromArgb(170, _palette.AccentColor);
        button.IconText = iconText;
        button.IconWidth = string.IsNullOrEmpty(iconText) ? 0 : 22;
        button.ShowIconTile = !string.IsNullOrEmpty(iconText);
        button.IconTileColor = Color.FromArgb(26, _palette.PrimaryColor);
        button.IconTileBorderColor = Color.FromArgb(54, _palette.AccentColor);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = button.BorderColor;
        button.BackColor = baseColor;
        button.ForeColor = textColor;
    }

    private int MeasureInfoCardHeight(RoundedPanel card, Label label, int logicalMinimumHeight)
    {
        var scale = GetCurrentLayoutScale();
        var horizontalPadding = Math.Max(12, (int)Math.Ceiling(14 * scale));
        var verticalPadding = Math.Max(8, (int)Math.Ceiling(9 * scale));
        label.Padding = new Padding(horizontalPadding, verticalPadding, horizontalPadding, verticalPadding);
        var availableWidth = Math.Max(120, card.ClientSize.Width - (horizontalPadding * 2) - 4);
        var measured = TextRenderer.MeasureText(
            label.Text,
            label.Font,
            new Size(availableWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        return Math.Max(
            (int)Math.Ceiling(logicalMinimumHeight * scale),
            measured.Height + (verticalPadding * 2) + (int)Math.Ceiling(4 * scale));
    }

    private float GetCurrentLayoutScale()
    {
        return Math.Max(1F, _nameShell.Height / 44F);
    }

    private async Task GenerateOfficialOAuthLinkAsync()
    {
        if (!IsOfficialOAuthSelected)
        {
            return;
        }
        if (_oauthLoginBusy)
        {
            CopyOAuthLoginUrl(showFailure: true);
            return;
        }
        if (_codex == null)
        {
            MessageBox.Show(
                this,
                "当前窗口没有连接到官方登录服务，请关闭后从主界面的“新增账号”重新打开。",
                "无法生成登录链接",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(AccountNameValue) || string.IsNullOrWhiteSpace(CodexHomeValue))
        {
            MessageBox.Show(
                this,
                "请先填写账号名称和账号目录，再生成登录链接。",
                "账号资料不完整",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (PathsEqual(CodexHomeValue, CodexCliService.GetDefaultCodexHome()))
        {
            MessageBox.Show(
                this,
                "账号目录不能使用共享的默认 .codex，请先选择一个独立目录。",
                "账号目录无效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        ScheduleOAuthDraftCleanup();
        _oauthDraftRoot = Path.Combine(
            Path.GetTempPath(),
            "codex-account-manager-oauth-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_oauthDraftRoot);
        _oauthDraftVerified = false;
        _oauthLoginUrl = "";
        _oauthCancellation?.Dispose();
        _oauthCancellation = new CancellationTokenSource();
        _oauthLoginBusy = true;
        SetOAuthInputsBusy(true);
        UpdateAuthMode();

        var progress = new Progress<ChatGptOAuthAuthorization>(authorization =>
        {
            if (IsDisposed || Disposing || !IsOfficialOAuthSelected)
            {
                _oauthCancellation?.Cancel();
                return;
            }

            _oauthLoginUrl = authorization.LoginUrl;
            _generateLoginLinkButton.Text = "复制登录链接";
            _oauthStatusBadge.Text = "登录链接已复制，正在等待浏览器登录…";
            _oauthStatusBadge.ForeColor = _palette.PrimaryColor;
            CopyOAuthLoginUrl(showFailure: true);
        });

        try
        {
            _oauthLoginTask = _codex.LoginWithChatGptDraftAsync(
                _oauthDraftRoot,
                progress,
                _oauthCancellation.Token);
            var status = await _oauthLoginTask;
            if (IsDisposed || Disposing || !IsOfficialOAuthSelected)
            {
                return;
            }
            if (status.ExitCode != 0 ||
                !File.Exists(Path.Combine(_oauthDraftRoot, "auth.json")))
            {
                throw new InvalidOperationException("官方网页登录完成，但没有生成可保存的 OAuth 凭据。");
            }

            _oauthDraftVerified = true;
            _oauthStatusBadge.Text = "✓ 已登录";
            _oauthStatusBadge.ForeColor = _palette.SuccessColor;
            _oauthStatusBadge.FillColor = UiDesign.Blend(
                _palette.SurfaceAltColor,
                _palette.SuccessColor,
                0.12F);
            _oauthStatusBadge.StrokeColor = Color.FromArgb(110, _palette.SuccessColor);
            _generateLoginLinkButton.Text = "重新生成登录链接";
            ClearOAuthLoginClipboard();
            _oauthLoginUrl = "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            if (!IsDisposed && !Disposing && IsOfficialOAuthSelected)
            {
                _oauthStatusBadge.Text = _oauthCancellation?.IsCancellationRequested == true
                    ? "登录已取消"
                    : "登录未完成，请重新生成链接";
                _oauthStatusBadge.ForeColor = _palette.WarningColor;
                if (_oauthCancellation?.IsCancellationRequested != true)
                {
                    MessageBox.Show(
                        this,
                        ex.Message,
                        "ChatGPT 登录未完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            ScheduleOAuthDraftCleanup();
        }
        finally
        {
            _oauthLoginBusy = false;
            SetOAuthInputsBusy(false);
            _oauthCancellation?.Dispose();
            _oauthCancellation = null;
            if (!IsDisposed && !Disposing)
            {
                UpdateAuthMode();
            }
        }
    }

    private void HandleOAuthSelectionChanged()
    {
        if (IsOfficialOAuthSelected)
        {
            return;
        }

        _oauthCancellation?.Cancel();
        ClearOAuthLoginClipboard();
        ScheduleOAuthDraftCleanup();
    }

    private bool HasVerifiedOAuthCredential()
    {
        if (_oauthDraftVerified)
        {
            return true;
        }
        return _originalOAuthVerified &&
               _originalAccount?.IsOfficialOAuth == true &&
               PathsEqual(_originalHome, CodexHomeValue);
    }

    private void SetOAuthInputsBusy(bool busy)
    {
        _nameBox.Enabled = !busy;
        _homeBox.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _authKindBox.Enabled = !busy;
        _authKindShell.Enabled = !busy;
    }

    private void CopyOAuthLoginUrl(bool showFailure)
    {
        if (string.IsNullOrWhiteSpace(_oauthLoginUrl))
        {
            return;
        }
        try
        {
            Clipboard.SetText(_oauthLoginUrl);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            _oauthStatusBadge.Text = "剪贴板暂不可用，请重试“复制登录链接”。";
            _oauthStatusBadge.ForeColor = _palette.WarningColor;
            if (showFailure)
            {
                MessageBox.Show(
                    this,
                    $"无法复制登录链接：{ex.Message}",
                    "剪贴板不可用",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    private void ClearOAuthLoginClipboard()
    {
        if (string.IsNullOrWhiteSpace(_oauthLoginUrl))
        {
            return;
        }
        try
        {
            if (Clipboard.ContainsText() &&
                Clipboard.GetText().Equals(_oauthLoginUrl, StringComparison.Ordinal))
            {
                Clipboard.Clear();
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            // Clipboard cleanup is best effort and never persists the one-time login URL.
        }
    }

    private void ScheduleOAuthDraftCleanup()
    {
        var draftRoot = _oauthDraftRoot;
        if (string.IsNullOrWhiteSpace(draftRoot))
        {
            _oauthDraftVerified = false;
            return;
        }

        _oauthDraftRoot = "";
        _oauthDraftVerified = false;
        var loginTask = _oauthLoginTask;
        if (loginTask != null && !loginTask.IsCompleted)
        {
            _ = loginTask.ContinueWith(
                _ => TryDeleteOAuthDraftRoot(draftRoot),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        TryDeleteOAuthDraftRoot(draftRoot);
    }

    private static void TryDeleteOAuthDraftRoot(string draftRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(draftRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                           Path.DirectorySeparatorChar;
            var name = Path.GetFileName(fullPath);
            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith("codex-account-manager-oauth-draft-", StringComparison.Ordinal) ||
                !Directory.Exists(fullPath))
            {
                return;
            }
            Directory.Delete(fullPath, recursive: true);
        }
        catch
        {
            // A later temp cleanup can remove an already-terminated draft directory.
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(right)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void UpdateAuthMode()
    {
        var isApi = IsCompatibleApiSelected;
        var isOAuth = IsOfficialOAuthSelected;
        var useTokenUpdateButton = IsAccessTokenSelected && !_isNewAccount;
        _credentialSectionBadge.Text = isApi
            ? "API 连接"
            : isOAuth
                ? "官方登录"
                : "登录凭据";
        _secretLabel.Text = isApi ? "API Key" : "访问令牌";
        _secretNote.Text = isOAuth
            ? $"点击登录方式右侧的“生成登录链接”。链接只会复制到剪贴板，不会自动打开浏览器；请自行粘贴并确认 ChatGPT 账号。只有官方回调完成并显示“✓ 已登录”后才能保存。\nOAuth 凭据只会保存在当前账号目录：{_homeBox.Text}"
            : isApi
                ? $"API Key 只会保存到当前账号目录：{_homeBox.Text}\n新增时必填；编辑时留空会保留该目录中的原 API Key。"
                : useTokenUpdateButton
                    ? string.IsNullOrWhiteSpace(_pendingAccessToken)
                        ? "当前 Token 保持不变。只有点击“更新 Token”并保存账号后，才会替换现有 Token。"
                        : "已选择新的 Token；点击“保存”后才会更新。若取消编辑，现有 Token 不会改变。"
                    : $"Access Token 只会保存到当前账号目录：{_homeBox.Text}\n只填 Token 本体；不要填 sk- API Key、JSON 或 Bearer 前缀。";
        _secretNoteCard.Height = MeasureInfoCardHeight(
            _secretNoteCard,
            _secretNote,
            isApi ? 70 : 82);
        _secretNoteCard.PerformLayout();

        _secretLabel.Visible = !isOAuth;
        _secretLabel.Enabled = !isOAuth;
        _secretShell.Visible = !isOAuth && !useTokenUpdateButton;
        _secretShell.Enabled = !isOAuth && !useTokenUpdateButton;
        _secretBox.Visible = !isOAuth && !useTokenUpdateButton;
        _secretBox.Enabled = !isOAuth && !useTokenUpdateButton;
        _updateTokenButton.Visible = useTokenUpdateButton;
        _updateTokenButton.Enabled = useTokenUpdateButton;
        _generateLoginLinkButton.Visible = isOAuth;
        _generateLoginLinkButton.Enabled = isOAuth &&
            _codex != null &&
            (!_oauthLoginBusy || !string.IsNullOrWhiteSpace(_oauthLoginUrl));
        _authKindShell.Width = isOAuth
            ? Math.Max(
                220,
                _generateLoginLinkButton.Left - _authKindShell.Left -
                Math.Max(8, (int)Math.Ceiling(12 * GetCurrentLayoutScale())))
            : _homeShell.Width;
        _oauthStatusBadge.Visible = isOAuth;
        _oauthStatusBadge.Enabled = isOAuth;

        if (isOAuth)
        {
            var verified = HasVerifiedOAuthCredential();
            if (_oauthLoginBusy)
            {
                _oauthStatusBadge.Text = string.IsNullOrWhiteSpace(_oauthLoginUrl)
                    ? "正在生成 OpenAI 官方登录链接…"
                    : "登录链接已复制，正在等待浏览器登录…";
                _oauthStatusBadge.ForeColor = _palette.PrimaryColor;
                _generateLoginLinkButton.Text = string.IsNullOrWhiteSpace(_oauthLoginUrl)
                    ? "正在生成…"
                    : "复制登录链接";
            }
            else if (verified)
            {
                _oauthStatusBadge.Text = "✓ 已登录";
                _oauthStatusBadge.ForeColor = _palette.SuccessColor;
                _oauthStatusBadge.FillColor = UiDesign.Blend(
                    _palette.SurfaceAltColor,
                    _palette.SuccessColor,
                    0.12F);
                _oauthStatusBadge.StrokeColor = Color.FromArgb(110, _palette.SuccessColor);
                _generateLoginLinkButton.Text = _oauthDraftVerified
                    ? "重新生成登录链接"
                    : "生成新登录链接";
            }
            else
            {
                _oauthStatusBadge.Text = "尚未登录；请先生成链接并在浏览器完成 ChatGPT 登录";
                _oauthStatusBadge.ForeColor = _palette.MutedTextColor;
                _oauthStatusBadge.FillColor = UiDesign.Blend(
                    _palette.SurfaceAltColor,
                    _palette.PrimaryColor,
                    0.05F);
                _oauthStatusBadge.StrokeColor = UiDesign.Blend(
                    _palette.BorderColor,
                    _palette.PrimaryColor,
                    0.24F);
                _generateLoginLinkButton.Text = "生成登录链接";
            }

            _saveButton.Enabled = verified && !_oauthLoginBusy;
        }
        else
        {
            _saveButton.Enabled = true;
        }

        foreach (var control in _apiControls)
        {
            control.Enabled = isApi;
            control.Visible = isApi;
        }

        if (isApi)
        {
            _apiNoteCard.Height = MeasureInfoCardHeight(_apiNoteCard, _apiNote, 54);
            _apiNoteCard.PerformLayout();
        }

        var scale = GetCurrentLayoutScale();
        var cardBottomPadding = Math.Max(14, (int)Math.Ceiling(18 * scale));
        var footerGap = Math.Max(12, (int)Math.Ceiling(16 * scale));
        var bottomMargin = Math.Max(14, (int)Math.Ceiling(18 * scale));
        var horizontalMargin = Math.Max(12, (int)Math.Ceiling(16 * scale));
        var footerButtonGap = Math.Max(10, (int)Math.Ceiling(12 * scale));
        var contentBottom = isApi ? _apiNoteCard.Bottom : _secretNoteCard.Bottom;
        _formCard.Height = contentBottom + cardBottomPadding;
        _footerDivider.Top = _formCard.Bottom + Math.Max(6, (int)Math.Ceiling(8 * scale));
        _saveButton.Top = _formCard.Bottom + footerGap;
        _cancelButton.Top = _saveButton.Top;
        var desiredClientWidth = Math.Max(ClientSize.Width, _formCard.Right + horizontalMargin);
        ClientSize = new Size(desiredClientWidth, _saveButton.Bottom + bottomMargin);
        _cancelButton.Left = ClientSize.Width - horizontalMargin - _cancelButton.Width;
        _saveButton.Left = _cancelButton.Left - footerButtonGap - _saveButton.Width;
    }

    private const uint EmSetSel = 0x00B1;
    private const uint EmScrollCaret = 0x00B7;
    private const uint WmHScroll = 0x0114;
    private const int SbLeft = 6;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
