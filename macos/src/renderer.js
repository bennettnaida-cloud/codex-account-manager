(() => {
  "use strict";

  const bridge = window.codexManager || {};
  const systemTheme = window.matchMedia("(prefers-color-scheme: dark)");
  const palette = ["#5f73f2", "#19b8cf", "#8b5cf6", "#ec6598", "#17aa78", "#e18e28"];
  const rangeLabels = { today: "今天", "7d": "最近 7 天", "30d": "最近 30 天", all: "全部时间" };

  const app = {
    accounts: [],
    currentAccountId: null,
    selectedAccountId: null,
    loginStatuses: {},
    theme: "system",
    range: "30d",
    metric: "value",
    activePage: "accounts",
    usageReport: emptyUsageReport(),
    usageAccountFilter: "all",
    usageRefreshSeconds: 15,
    usageRefreshTimer: null,
    usageInFlight: false,
    usage: emptyUsage(),
    historyThreads: [],
    historyLoaded: false,
    historyRequest: 0,
    threadRequest: 0,
    historySearchTimer: null,
    historyAccountFilter: "all",
    historyArchiveFilter: "all",
    selectedThreadId: null,
    selectedThread: null,
    threadDetail: null,
    quotaReport: { updatedAt: null, accounts: [] },
    quotaLoaded: false,
    quotaAccountFilter: "all",
    quotaRefreshSeconds: 30,
    quotaRefreshTimer: null,
    quotaInFlight: false,
    quotaRequest: 0,
    codexThemes: [],
    themesLoaded: false,
    systemSettings: null,
    settingsLoaded: false,
    editingAccountId: null,
    editingOriginalAuthKind: null,
    stateRequest: 0,
    usageRequest: 0,
    pendingConfirm: null,
    oauthDraftId: null,
    oauthVerified: false,
    oauthPending: false,
    oauthRequiresFreshLogin: false,
    oauthRequestVersion: 0,
    oauthButtonOperation: 0,
    copiedOAuthValues: new Set(),
    pendingOAuthCompletions: new Map(),
  };

  const el = {};

  document.addEventListener("DOMContentLoaded", initialize);

  async function initialize() {
    cacheElements();
    bindEvents();
    applyTheme("system");

    try {
      await loadState();
      await loadRefreshPreferences();
      await loadUsage(false);
      updateUsageRefreshTimer();
      subscribeToState();
      subscribeToOAuthDraft();
    } catch (error) {
      showToast("无法读取应用数据", friendlyError(error), "error");
      renderAll();
    } finally {
      el.appShell.setAttribute("aria-busy", "false");
    }
  }

  function cacheElements() {
    const ids = [
      "appShell", "versionLabel", "themeButton", "themeLabel", "accountsPage", "historyPage", "statusPage", "usagePage", "quotaPage", "themesPage", "settingsPage",
      "importButton", "addAccountButton", "currentInitial", "currentName", "currentAuth",
      "currentLoginStatus", "heroTokens", "heroValueLabel", "heroValue", "launchCurrentButton", "accountCount",
      "accountSearch", "accountList", "detailEmpty", "accountDetail", "detailAvatar", "detailName",
      "detailType", "detailStatus", "detailHome", "detailModel", "detailLastUsed", "detailLaunchButton", "detailAppLaunchButton",
      "setCurrentButton", "checkLoginButton", "loginButton", "editAccountButton", "deleteAccountButton",
      "historySearch", "historyAccountFilter", "historyArchiveFilter", "refreshHistoryButton", "historyCount",
      "historyList", "threadReader", "threadReaderEmpty", "threadReaderContent", "threadTitle", "threadMeta",
      "threadMessages", "archiveThreadButton", "deleteThreadButton", "copyThreadButton",
      "statusCheckAllButton", "statusAccountList", "usageAccountFilter", "usageRefreshButton", "usageRefreshInterval",
      "rangeControl", "metricSwitch", "valueMetricLabel", "primaryMetricLabel", "primaryMetricValue", "primaryMetricHint",
      "inputTokenValue", "cachedTokenValue", "cacheRateHint", "outputTokenValue", "chartTitle",
      "chartSubtitle", "chartTotal", "usageChart", "chartWrap", "chartTooltip", "chartEmpty",
      "modelRing", "modelCount", "modelLegend", "breakdownBars",
      "quotaAccountFilter", "refreshQuotaButton", "quotaUpdatedAt", "quotaGrid",
      "refreshThemesButton", "restoreThemeButton", "themeGrid", "customThemeForm", "customThemeName",
      "customThemeMode", "customThemeCodeTheme", "customThemeAccent", "customThemeSurface", "customThemeText",
      "customThemeBackground", "saveCustomThemeButton", "settingsForm", "projectRootInput", "launchDirectoryInput",
      "codexAppPathInput", "codexAppPathHint", "chooseCodexAppButton", "autoDetectCodexAppButton",
      "proxyHostInput", "proxyPortInput", "proxyAutoDetectInput", "detectProxyButton", "detectedProxyStatus",
      "openProjectRootButton", "openLaunchDirectoryButton", "saveSystemSettingsButton", "toastStack", "accountModal",
      "accountForm", "accountModalEyebrow", "accountModalTitle", "accountNameInput", "authKindInput",
      "modelField", "modelInput", "codexHomeInput", "apiFields", "oauthFields", "providerNameInput", "baseUrlInput", "wireApiInput",
      "oauthLoginButton", "oauthLoginStatus", "accountSecretField", "secretFieldLabel", "secretInput", "secretFieldHint",
      "revealSecretButton", "saveAccountButton", "loginModal", "loginForm", "loginDescription",
      "loginSecretField", "loginSecretLabel", "loginSecretInput",
      "revealLoginSecretButton", "confirmLoginButton", "confirmModal", "confirmTitle", "confirmMessage",
      "confirmActionButton",
    ];
    ids.forEach((id) => { el[id] = document.getElementById(id); });
  }

  function bindEvents() {
    document.querySelectorAll(".nav-item").forEach((button) => {
      button.addEventListener("click", () => showPage(button.dataset.page));
    });

    el.addAccountButton.addEventListener("click", () => openAccountModal());
    el.importButton.addEventListener("click", importAccounts);
    el.accountSearch.addEventListener("input", renderAccountList);
    el.accountList.addEventListener("click", handleAccountListClick);
    el.accountList.addEventListener("keydown", (event) => {
      if ((event.key === "Enter" || event.key === " ") && event.target.closest(".account-row")) {
        event.preventDefault();
        selectAccount(event.target.closest(".account-row").dataset.accountId);
      }
    });

    el.launchCurrentButton.addEventListener("click", () => launchAccount(app.currentAccountId));
    el.detailLaunchButton.addEventListener("click", () => launchAccount(app.selectedAccountId));
    el.detailAppLaunchButton.addEventListener("click", () => launchAccountInCodexApp(app.selectedAccountId));
    el.setCurrentButton.addEventListener("click", setSelectedAsCurrent);
    el.checkLoginButton.addEventListener("click", () => checkLogin(app.selectedAccountId, true));
    el.loginButton.addEventListener("click", openSelectedAccountLogin);
    el.editAccountButton.addEventListener("click", () => openAccountModal(selectedAccount()));
    el.deleteAccountButton.addEventListener("click", deleteSelectedAccount);

    el.historySearch.addEventListener("input", () => {
      window.clearTimeout(app.historySearchTimer);
      app.historySearchTimer = window.setTimeout(() => loadHistory(true), 260);
    });
    el.historyAccountFilter.addEventListener("change", () => {
      app.historyAccountFilter = el.historyAccountFilter.value;
      clearSelectedThread();
      loadHistory(true);
    });
    el.historyArchiveFilter.addEventListener("change", () => {
      app.historyArchiveFilter = el.historyArchiveFilter.value;
      renderHistoryList();
    });
    el.refreshHistoryButton.addEventListener("click", () => loadHistory(true));
    el.historyList.addEventListener("click", handleHistoryListClick);
    el.archiveThreadButton.addEventListener("click", toggleSelectedThreadArchive);
    el.deleteThreadButton.addEventListener("click", deleteSelectedThread);
    el.copyThreadButton.addEventListener("click", copySelectedThread);

    el.statusCheckAllButton.addEventListener("click", checkAllLoginStatuses);
    el.statusAccountList.addEventListener("click", handleStatusAccountAction);

    el.usageAccountFilter.addEventListener("change", () => {
      app.usageAccountFilter = el.usageAccountFilter.value;
      app.usage = selectUsageScope(app.usageReport, app.usageAccountFilter);
      renderUsage();
      renderCurrentCard();
    });
    el.usageRefreshButton.addEventListener("click", () => loadUsage(true));
    el.usageRefreshInterval.addEventListener("change", () => {
      app.usageRefreshSeconds = Math.max(0, Number(el.usageRefreshInterval.value) || 0);
      updateUsageRefreshTimer();
      persistRefreshPreferences();
    });

    el.rangeControl.addEventListener("click", (event) => {
      const button = event.target.closest("button[data-range]");
      if (!button || button.dataset.range === app.range) return;
      app.range = button.dataset.range;
      updateSegmentedControl(el.rangeControl, "range", app.range);
      loadUsage(true);
    });

    el.metricSwitch.addEventListener("click", (event) => {
      const button = event.target.closest("button[data-metric]");
      if (!button || button.dataset.metric === app.metric) return;
      app.metric = button.dataset.metric;
      updateSegmentedControl(el.metricSwitch, "metric", app.metric, true);
      renderUsage();
    });

    el.quotaAccountFilter.addEventListener("change", () => {
      app.quotaAccountFilter = el.quotaAccountFilter.value;
      renderQuota();
    });
    el.refreshQuotaButton.addEventListener("click", () => loadQuota(true));

    el.refreshThemesButton.addEventListener("click", () => loadCodexThemes(true));
    el.restoreThemeButton.addEventListener("click", restoreOfficialCodexTheme);
    el.themeGrid.addEventListener("click", handleThemeAction);
    el.customThemeForm.addEventListener("submit", saveCustomCodexTheme);

    el.settingsForm.addEventListener("submit", saveSettings);
    el.detectProxyButton.addEventListener("click", detectProxy);
    el.chooseCodexAppButton.addEventListener("click", chooseCodexApp);
    el.autoDetectCodexAppButton.addEventListener("click", useAutomaticCodexAppDetection);
    el.openProjectRootButton.addEventListener("click", () => openConfiguredPath(el.projectRootInput.value));
    el.openLaunchDirectoryButton.addEventListener("click", () => openConfiguredPath(el.launchDirectoryInput.value || el.projectRootInput.value));

    el.themeButton.addEventListener("click", cycleTheme);
    systemTheme.addEventListener("change", () => {
      if (app.theme === "system") applyTheme("system");
    });

    document.addEventListener("keydown", (event) => {
      if (event.metaKey && event.shiftKey && event.key.toLowerCase() === "l") {
        event.preventDefault();
        cycleTheme();
      }
      if (event.metaKey && event.key.toLowerCase() === "n" && !isDialogOpen()) {
        event.preventDefault();
        openAccountModal();
      }
    });

    document.querySelectorAll("[data-close-modal]").forEach((button) => {
      button.addEventListener("click", () => closeDialog(document.getElementById(button.dataset.closeModal)));
    });
    [el.accountModal, el.loginModal].forEach((dialog) => {
      dialog.addEventListener("click", (event) => {
        if (event.target === dialog) closeDialog(dialog);
      });
      dialog.addEventListener("cancel", (event) => {
        event.preventDefault();
        closeDialog(dialog);
      });
    });

    el.authKindInput.addEventListener("change", handleAuthKindChange);
    el.oauthLoginButton.addEventListener("click", startOAuthLogin);
    el.revealSecretButton.addEventListener("click", () => toggleSecret(el.secretInput, el.revealSecretButton));
    el.revealLoginSecretButton.addEventListener("click", () => toggleSecret(el.loginSecretInput, el.revealLoginSecretButton));
    el.accountForm.addEventListener("submit", saveAccount);
    el.loginForm.addEventListener("submit", loginAccount);

    window.addEventListener("unhandledrejection", (event) => {
      showToast("操作未完成", friendlyError(event.reason), "error");
    });
    window.addEventListener("beforeunload", () => {
      window.clearTimeout(app.historySearchTimer);
      clearUsageRefreshTimer();
      clearQuotaRefreshTimer();
    });
  }

  async function loadState() {
    const requestId = ++app.stateRequest;
    const raw = await invoke("getState");
    if (requestId !== app.stateRequest) return { stale: true, accountScopeChanged: false };
    invalidateUsageAndQuotaRequests();
    const previousAccountIds = new Set(app.accounts.map((account) => account.id));
    const previousCurrentAccountId = app.currentAccountId;
    app.accounts = Array.isArray(raw?.accounts) ? raw.accounts.map(normalizeAccount) : [];
    app.currentAccountId = idOf(raw?.currentAccountId) || idOf(app.accounts.find((item) => item.isCurrent)?.id);
    app.loginStatuses = normalizeStatuses(raw?.loginStatuses);
    app.accounts.forEach((account) => {
      if (account.loginStatus != null && !app.loginStatuses[account.id]) {
        app.loginStatuses[account.id] = normalizeStatus(account.loginStatus);
      }
    });
    app.theme = normalizeTheme(raw?.theme);
    applyTheme(app.theme);

    const accountScopeChanged = previousCurrentAccountId !== app.currentAccountId
      || previousAccountIds.size !== app.accounts.length
      || app.accounts.some((account) => !previousAccountIds.has(account.id));
    if (accountScopeChanged) invalidateHistoryAndThreadRequests();
    pruneDerivedStateToActiveAccounts();

    if (raw?.appVersion) el.versionLabel.textContent = `版本 ${raw.appVersion} · ${raw.platform || "macOS"}`;
    else el.versionLabel.textContent = raw?.platform || "macOS";

    const previousSelectionExists = app.accounts.some((item) => item.id === app.selectedAccountId);
    if (!previousSelectionExists) app.selectedAccountId = app.currentAccountId || app.accounts[0]?.id || null;
    updateAccountFilterOptions();
    renderAll();
    return { accountScopeChanged };
  }

  async function loadRefreshPreferences() {
    if (typeof bridge.getSystemSettings !== "function") return;
    try {
      const settings = await invoke("getSystemSettings");
      if (!settings || typeof settings !== "object") return;
      app.systemSettings = settings;
      app.settingsLoaded = true;
      const usageSeconds = Math.max(15, Number(settings.usageRefreshSeconds) || 15);
      app.usageRefreshSeconds = usageSeconds <= 15 ? 15 : usageSeconds <= 30 ? 30 : 60;
      app.quotaRefreshSeconds = Math.max(30, Math.min(300, Number(settings.quotaRefreshSeconds) || 30));
      el.usageRefreshInterval.value = String(app.usageRefreshSeconds);
      renderSettings();
    } catch {
      app.usageRefreshSeconds = 15;
      app.quotaRefreshSeconds = 30;
      el.usageRefreshInterval.value = "15";
    }
  }

  async function persistRefreshPreferences() {
    if (typeof bridge.saveSystemSettings !== "function") return;
    try {
      const settings = await invoke("saveSystemSettings", {
        usageRefreshSeconds: app.usageRefreshSeconds || 15,
        quotaRefreshSeconds: app.quotaRefreshSeconds,
      });
      if (settings && typeof settings === "object") app.systemSettings = settings;
    } catch (error) {
      showToast("刷新频率未能保存", friendlyError(error), "error");
    }
  }

  async function loadUsage(showLoading) {
    if (app.usageInFlight) return;
    app.usageInFlight = true;
    const requestId = ++app.usageRequest;
    if (showLoading) setUsageLoading(true);
    try {
      const raw = await invoke("getUsageStats", { range: app.range });
      if (requestId !== app.usageRequest) return;
      app.usageReport = restrictUsageReportToActiveAccounts(normalizeUsageReport(raw));
      if (!usageScopeExists(app.usageReport, app.usageAccountFilter)) app.usageAccountFilter = "all";
      app.usage = selectUsageScope(app.usageReport, app.usageAccountFilter);
      renderUsageAccountFilter();
      renderUsage();
      renderCurrentCard();
    } catch (error) {
      if (requestId === app.usageRequest) showToast("统计读取失败", friendlyError(error), "error");
    } finally {
      if (requestId === app.usageRequest) {
        app.usageInFlight = false;
        if (showLoading) setUsageLoading(false);
      }
    }
  }

  function subscribeToState() {
    if (typeof bridge.onStateChanged !== "function") return;
    bridge.onStateChanged(async () => {
      try {
        const refreshHistory = app.historyLoaded;
        const refreshQuota = app.quotaLoaded || app.quotaInFlight;
        const stateChange = await loadState();
        if (stateChange?.stale) return;
        await Promise.all([
          loadUsage(false),
          ...(refreshQuota || stateChange?.accountScopeChanged ? [loadQuota(false)] : []),
          ...(refreshHistory || stateChange?.accountScopeChanged ? [loadHistory(false)] : []),
        ]);
      } catch (error) {
        showToast("刷新失败", friendlyError(error), "error");
      }
    });
  }

  function subscribeToOAuthDraft() {
    if (typeof bridge.onOAuthDraftCompleted !== "function") return;
    bridge.onOAuthDraftCompleted((payload) => {
      const draftId = idOf(payload?.draftId);
      if (!draftId) return;
      if (draftId !== app.oauthDraftId) {
        if (el.accountModal.open && app.oauthPending && !app.oauthDraftId && el.authKindInput.value === "official_oauth") {
          app.pendingOAuthCompletions.set(draftId, {
            draftId,
            ok: payload?.ok === true,
            message: String(payload?.message || ""),
          });
          if (app.pendingOAuthCompletions.size > 8) {
            app.pendingOAuthCompletions.delete(app.pendingOAuthCompletions.keys().next().value);
          }
        }
        return;
      }
      completeOAuthDraft(payload);
    });
  }

  function renderAll() {
    renderAccountList();
    renderCurrentCard();
    renderAccountDetail();
    renderStatusAccounts();
    renderHistoryList();
    renderQuota();
    renderUsage();
    updateSegmentedControl(el.rangeControl, "range", app.range);
    updateSegmentedControl(el.metricSwitch, "metric", app.metric, true);
  }

  function renderAccountList() {
    const query = el.accountSearch.value.trim().toLocaleLowerCase("zh-CN");
    const rows = app.accounts.filter((account) => {
      if (!query) return true;
      return [account.name, account.apiProviderName, account.apiModel, account.codexHome]
        .some((value) => String(value || "").toLocaleLowerCase("zh-CN").includes(query));
    });
    el.accountCount.textContent = String(app.accounts.length);

    if (!rows.length) {
      el.accountList.innerHTML = query
        ? `<div class="list-empty"><div class="list-empty-icon"></div><h4>没有匹配的账号</h4><p>换一个关键词试试。</p></div>`
        : `<div class="list-empty"><div class="list-empty-icon"></div><h4>还没有账号</h4><p>添加账号后即可独立启动 Codex。</p><button class="mini-button" type="button" data-empty-add>添加第一个账号</button></div>`;
      return;
    }

    el.accountList.innerHTML = rows.map((account) => {
      const colors = avatarColors(account.id || account.name);
      const status = statusFor(account.id);
      const selected = account.id === app.selectedAccountId;
      const current = account.id === app.currentAccountId || account.isCurrent;
      return `<div class="account-row${selected ? " is-selected" : ""}" data-account-id="${escapeAttr(account.id)}" role="button" tabindex="0" aria-selected="${selected}">
        <div class="account-avatar" style="--avatar-a:${colors[0]};--avatar-b:${colors[1]}">${escapeHtml(initialOf(account.name))}</div>
        <div class="account-row-copy"><strong>${escapeHtml(account.name)}</strong><span><i class="mini-status ${status.loggedIn ? "online" : ""}"></i>${escapeHtml(authLabel(account))}</span></div>
        ${current ? `<span class="current-tag">当前</span>` : `<span class="row-chevron">›</span>`}
      </div>`;
    }).join("");
  }

  function handleAccountListClick(event) {
    if (event.target.closest("[data-empty-add]")) {
      openAccountModal();
      return;
    }
    const row = event.target.closest(".account-row");
    if (row) selectAccount(row.dataset.accountId);
  }

  function selectAccount(accountId) {
    if (!accountId || accountId === app.selectedAccountId) return;
    app.selectedAccountId = accountId;
    renderAccountList();
    renderAccountDetail();
  }

  function renderCurrentCard() {
    const current = accountById(app.currentAccountId);
    const hasCurrent = Boolean(current);
    const currentUsage = current ? usageForAccount(current.id) : emptyUsage();
    const valueLabel = usageValueLabel();
    el.launchCurrentButton.disabled = !hasCurrent;
    el.currentName.textContent = current?.name || "尚未选择账号";
    el.currentInitial.textContent = initialOf(current?.name || "Codex");
    el.currentAuth.textContent = current ? authLabel(current) : "添加账号后即可开始";
    const status = current ? statusFor(current.id) : { label: "未连接", loggedIn: false };
    el.currentLoginStatus.textContent = status.label;
    el.currentLoginStatus.style.color = status.loggedIn ? "#87e5d0" : "";
    el.heroTokens.textContent = currentUsage.totalTokens > 0 ? formatTokens(currentUsage.totalTokens) : "—";
    el.heroValueLabel.textContent = valueLabel;
    el.heroValue.textContent = currentUsage.apiEquivalentComplete
      ? (currentUsage.apiEquivalentUsd > 0 ? formatUsd(currentUsage.apiEquivalentUsd) : "—")
      : "不可确定";
  }

  function renderAccountDetail() {
    const account = selectedAccount();
    el.detailEmpty.classList.toggle("is-hidden", Boolean(account));
    el.accountDetail.classList.toggle("is-hidden", !account);
    if (!account) return;

    const colors = avatarColors(account.id || account.name);
    el.detailAvatar.textContent = initialOf(account.name);
    el.detailAvatar.style.setProperty("--avatar-a", colors[0]);
    el.detailAvatar.style.setProperty("--avatar-b", colors[1]);
    el.detailName.textContent = account.name;
    el.detailType.textContent = authLabel(account);
    el.detailHome.textContent = account.codexHome || "自动管理";
    el.detailHome.title = account.codexHome || "";
    el.detailModel.textContent = account.authKind === "compatible_api" ? (account.apiModel || "默认模型") : "由 Codex 决定";
    el.detailLastUsed.textContent = account.lastUsedAt ? formatDate(account.lastUsedAt) : "尚未使用";

    const status = statusFor(account.id);
    el.detailStatus.className = `status-pill${status.loggedIn ? " online" : status.error ? " error" : status.checking ? " checking" : ""}`;
    el.detailStatus.innerHTML = `<i></i>${escapeHtml(status.label)}`;
    const isCurrent = account.id === app.currentAccountId;
    el.setCurrentButton.disabled = false;
    el.detailAppLaunchButton.disabled = false;
    el.setCurrentButton.textContent = isCurrent ? "重新应用账号" : "切换账号";
    el.loginButton.textContent = account.authKind === "official_oauth"
      ? (status.loggedIn ? "重新通过 ChatGPT 登录" : "通过 ChatGPT 登录（官方）")
      : "登录 / 更新凭据";
  }

  function renderUsage() {
    const usage = app.usage;
    const valueMode = app.metric === "value";
    const valueLabel = usageValueLabel();
    el.valueMetricLabel.textContent = valueLabel;
    el.primaryMetricLabel.textContent = valueMode ? valueLabel : "Token 总量";
    el.primaryMetricValue.textContent = valueMode
      ? (usage.apiEquivalentComplete ? formatUsd(usage.apiEquivalentUsd) : "不可确定")
      : formatTokens(usage.totalTokens);
    el.primaryMetricHint.textContent = `${usageScopeLabel()} · ${rangeLabels[app.range]}`;
    el.inputTokenValue.textContent = formatTokens(usage.inputTokens);
    el.cachedTokenValue.textContent = formatTokens(usage.cachedInputTokens);
    el.outputTokenValue.textContent = formatTokens(usage.outputTokens);
    const cacheRate = usage.inputTokens > 0 ? usage.cachedInputTokens / usage.inputTokens * 100 : 0;
    el.cacheRateHint.textContent = `占输入 ${formatPercent(cacheRate)}`;

    el.chartTitle.textContent = valueMode ? `${valueLabel}趋势` : "Token 使用趋势";
    el.chartSubtitle.textContent = `${usageScopeLabel()} · ${rangeLabels[app.range]}`;
    el.chartTotal.textContent = valueMode
      ? (usage.apiEquivalentComplete ? formatUsd(usage.apiEquivalentUsd) : "不可确定")
      : formatTokens(usage.totalTokens);
    renderChart();
    renderModels();
    renderBreakdown();
  }

  function renderChart() {
    const valueLabel = usageValueLabel();
    const hasTimeline = app.usage.timeline.length > 0;
    const source = hasTimeline
      ? app.usage.timeline.map((item) => ({ label: item.label, value: app.metric === "value" ? item.apiEquivalentUsd : item.totalTokens }))
      : app.usage.models.slice().sort((a, b) => (app.metric === "value" ? (b.cost ?? -1) - (a.cost ?? -1) : b.tokens - a.tokens)).map((item) => ({ label: item.model, value: app.metric === "value" ? item.cost : item.tokens }));
    const points = source.filter((item) => Number.isFinite(item.value));
    if (!hasTimeline && points.length) {
      el.chartTitle.textContent = app.metric === "value" ? `各模型 ${valueLabel}` : "各模型 Token 使用";
      el.chartSubtitle.textContent = `${usageScopeLabel()} · ${rangeLabels[app.range]} · 模型汇总`;
    }

    el.usageChart.replaceChildren();
    el.chartTooltip.hidden = true;
    const hasData = points.length >= 1 && points.some((item) => item.value > 0);
    el.chartEmpty.classList.toggle("is-visible", !hasData);
    if (!hasData) return;

    const svg = el.usageChart;
    const width = 760;
    const height = 280;
    const pad = { left: 53, right: 18, top: 17, bottom: 31 };
    const innerWidth = width - pad.left - pad.right;
    const innerHeight = height - pad.top - pad.bottom;
    const maxValue = Math.max(...points.map((item) => item.value), 1);
    const paddedMax = niceMax(maxValue);
    const coords = points.map((item, index) => ({
      ...item,
      x: points.length === 1 ? pad.left + innerWidth / 2 : pad.left + innerWidth * index / (points.length - 1),
      y: pad.top + innerHeight * (1 - item.value / paddedMax),
    }));

    const defs = svgNode("defs");
    const areaGradient = svgNode("linearGradient", { id: "chartAreaGradient", x1: "0", y1: "0", x2: "0", y2: "1" });
    areaGradient.append(svgNode("stop", { offset: "0%", "stop-color": "#6578f4", "stop-opacity": ".24" }), svgNode("stop", { offset: "100%", "stop-color": "#6578f4", "stop-opacity": "0" }));
    const lineGradient = svgNode("linearGradient", { id: "chartLineGradient", x1: "0", y1: "0", x2: "1", y2: "0" });
    lineGradient.append(svgNode("stop", { offset: "0%", "stop-color": "#4a9cff" }), svgNode("stop", { offset: "52%", "stop-color": "#6775f2" }), svgNode("stop", { offset: "100%", "stop-color": "#9b67ed" }));
    defs.append(areaGradient, lineGradient);
    svg.append(defs);

    for (let index = 0; index <= 4; index++) {
      const y = pad.top + innerHeight * index / 4;
      const value = paddedMax * (1 - index / 4);
      svg.append(svgNode("line", { x1: pad.left, y1: y, x2: width - pad.right, y2: y, class: "chart-grid-line" }));
      const label = svgNode("text", { x: pad.left - 9, y: y + 3, class: "chart-axis-label", "text-anchor": "end" });
      label.textContent = app.metric === "value" ? compactUsd(value) : formatTokens(value);
      svg.append(label);
    }

    const xLabelIndexes = uniqueIndexes([0, Math.round((points.length - 1) / 2), points.length - 1]);
    xLabelIndexes.forEach((index) => {
      const label = svgNode("text", { x: coords[index].x, y: height - 7, class: "chart-axis-label", "text-anchor": index === 0 ? "start" : index === points.length - 1 ? "end" : "middle" });
      label.textContent = coords[index].label;
      svg.append(label);
    });

    if (coords.length > 1) {
      const linePath = smoothPath(coords);
      const areaPath = `${linePath} L ${coords.at(-1).x} ${height - pad.bottom} L ${coords[0].x} ${height - pad.bottom} Z`;
      svg.append(svgNode("path", { d: areaPath, class: "chart-area" }));
      svg.append(svgNode("path", { d: linePath, class: "chart-line" }));
    }
    const hoverLine = svgNode("line", { y1: pad.top, y2: height - pad.bottom, class: "chart-hover-line" });
    svg.append(hoverLine);

    coords.forEach((point) => svg.append(svgNode("circle", { cx: point.x, cy: point.y, r: coords.length === 1 ? 5 : 3.6, class: "chart-point", style: coords.length === 1 ? "opacity:1" : "" })));
    const hit = svgNode("rect", { x: pad.left, y: pad.top, width: innerWidth, height: innerHeight, class: "chart-hit" });
    hit.addEventListener("pointermove", (event) => showChartTooltip(event, coords, hoverLine));
    hit.addEventListener("pointerleave", () => hideChartTooltip(hoverLine));
    svg.append(hit);
  }

  function showChartTooltip(event, points, hoverLine) {
    const bounds = el.usageChart.getBoundingClientRect();
    const relativeX = (event.clientX - bounds.left) / bounds.width * 760;
    const point = points.reduce((best, item) => Math.abs(item.x - relativeX) < Math.abs(best.x - relativeX) ? item : best, points[0]);
    const visualX = point.x / 760 * bounds.width + 13;
    const visualY = point.y / 280 * bounds.height + 13;
    el.chartTooltip.innerHTML = `${escapeHtml(point.label)}<strong>${app.metric === "value" ? formatUsd(point.value) : formatFullTokens(point.value)}</strong>`;
    el.chartTooltip.style.left = `${visualX}px`;
    el.chartTooltip.style.top = `${Math.max(36, visualY)}px`;
    el.chartTooltip.hidden = false;
    hoverLine.setAttribute("x1", point.x);
    hoverLine.setAttribute("x2", point.x);
    hoverLine.style.opacity = "1";
    el.usageChart.querySelectorAll(".chart-point").forEach((node, index) => { node.style.opacity = points[index] === point ? "1" : "0"; });
  }

  function hideChartTooltip(hoverLine) {
    el.chartTooltip.hidden = true;
    hoverLine.style.opacity = "0";
    el.usageChart.querySelectorAll(".chart-point").forEach((node) => { node.style.opacity = "0"; });
  }

  function renderModels() {
    const valueMode = app.metric === "value";
    const models = app.usage.models.map((model, index) => ({
      ...model,
      color: validColor(model.color) || palette[index % palette.length],
      metricValue: valueMode ? (model.costKnown ? model.cost : 0) : model.tokens,
    })).sort((a, b) => b.metricValue - a.metricValue);
    const total = models.reduce((sum, model) => sum + Math.max(0, model.metricValue), 0);
    el.modelCount.textContent = String(models.length);

    if (!models.length) {
      el.modelRing.style.background = `conic-gradient(var(--line) 0 100%)`;
      el.modelLegend.innerHTML = `<div class="legend-empty">暂无模型数据</div>`;
      return;
    }

    if (total <= 0) {
      el.modelRing.style.background = `conic-gradient(var(--line) 0 100%)`;
      el.modelLegend.innerHTML = models.slice(0, 6).map((model) => `<div class="legend-row">
        <i class="legend-dot" style="--model-color:${model.color}"></i>
        <span class="legend-name" title="${escapeAttr(model.model)}">${escapeHtml(model.model)}</span>
        <span class="legend-value">${valueMode && !model.costKnown ? "不可确定" : valueMode ? formatUsd(model.cost) : formatTokens(model.tokens)}</span>
      </div>`).join("");
      return;
    }

    let cursor = 0;
    const segments = models.map((model) => {
      const start = cursor;
      cursor += model.metricValue / total * 100;
      return `${model.color} ${start.toFixed(2)}% ${cursor.toFixed(2)}%`;
    });
    el.modelRing.style.background = `conic-gradient(${segments.join(",")})`;
    el.modelLegend.innerHTML = models.slice(0, 6).map((model) => `<div class="legend-row">
      <i class="legend-dot" style="--model-color:${model.color}"></i>
      <span class="legend-name" title="${escapeAttr(model.model)}">${escapeHtml(model.model)}</span>
      <span class="legend-value">${valueMode && !model.costKnown ? "不可确定" : valueMode ? formatUsd(model.cost) : formatTokens(model.tokens)}</span>
    </div>`).join("");
  }

  function renderBreakdown() {
    const entries = [
      { label: "输入 Token", value: app.usage.inputTokens, color: "var(--blue)" },
      { label: "缓存读取", value: app.usage.cachedInputTokens, color: "var(--cyan)" },
      { label: "缓存写入", value: app.usage.cacheWriteTokens, color: "var(--violet)" },
      { label: "输出 Token", value: app.usage.outputTokens, color: "var(--pink)" },
    ];
    const max = Math.max(...entries.map((entry) => entry.value), 1);
    el.breakdownBars.innerHTML = entries.map((entry) => `<div class="breakdown-item">
      <div class="breakdown-copy"><span>${entry.label}</span><strong>${formatTokens(entry.value)}</strong></div>
      <div class="bar-track"><div class="bar-fill" style="--bar-color:${entry.color};width:${Math.max(0, entry.value / max * 100).toFixed(2)}%"></div></div>
    </div>`).join("");
  }

  function updateAccountFilterOptions() {
    const accountOptions = [
      { value: "all", label: "全部账号" },
      ...app.accounts.map((account) => ({ value: account.id, label: account.name })),
    ];
    const historyOptions = [accountOptions[0], { value: "shared", label: "共享 Codex 记录" }, ...accountOptions.slice(1)];
    app.historyAccountFilter = setSelectOptions(el.historyAccountFilter, historyOptions, app.historyAccountFilter);
    app.quotaAccountFilter = setSelectOptions(el.quotaAccountFilter, accountOptions, app.quotaAccountFilter);
    renderUsageAccountFilter();
  }

  function setSelectOptions(select, options, selectedValue) {
    if (!select) return selectedValue;
    const value = options.some((item) => item.value === selectedValue) ? selectedValue : options[0]?.value || "";
    select.innerHTML = options.map((item) => `<option value="${escapeAttr(item.value)}">${escapeHtml(item.label)}</option>`).join("");
    select.value = value;
    return value;
  }

  async function loadHistory(showLoading = false) {
    const requestId = ++app.historyRequest;
    const query = el.historySearch.value.trim();
    const account = accountById(app.historyAccountFilter);
    if (showLoading) {
      el.refreshHistoryButton.disabled = true;
      el.refreshHistoryButton.textContent = "正在读取…";
    }
    try {
      const options = {
        includeArchived: true,
        limit: 500,
        ...(query ? { query } : {}),
        ...(app.historyAccountFilter !== "all" ? { accountId: app.historyAccountFilter } : {}),
        ...(account?.codexHome ? { codexHome: account.codexHome } : {}),
      };
      const raw = await invoke(query ? "searchHistory" : "getHistory", options);
      if (requestId !== app.historyRequest) return;
      const threads = Array.isArray(raw?.threads) ? raw.threads : Array.isArray(raw) ? raw : [];
      app.historyThreads = restrictHistoryThreadsToActiveAccounts(
        threads.map((thread, index) => normalizeHistoryThread(thread, index, account?.codexHome)),
      );
      app.historyLoaded = true;
      if (app.selectedThreadId && !app.historyThreads.some((thread) => thread.id === app.selectedThreadId)) clearSelectedThread();
      renderHistoryList();
    } catch (error) {
      if (requestId === app.historyRequest) {
        app.historyLoaded = true;
        app.historyThreads = [];
        clearSelectedThread();
        renderHistoryList();
        showToast("聊天记录读取失败", friendlyError(error), "error");
      }
    } finally {
      if (requestId === app.historyRequest && showLoading) {
        el.refreshHistoryButton.disabled = false;
        el.refreshHistoryButton.textContent = "刷新记录";
      }
    }
  }

  function normalizeHistoryThread(raw, index, requestedCodexHome = "") {
    return {
      id: idOf(raw?.id || raw?.threadId) || `thread-${index}`,
      title: String(raw?.title || "未命名会话"),
      preview: String(raw?.preview || ""),
      workingDirectory: String(raw?.workingDirectory || raw?.cwd || ""),
      model: String(raw?.model || ""),
      provider: String(raw?.provider || ""),
      updatedAt: raw?.updatedAt || raw?.timestamp || null,
      archived: Boolean(raw?.archived),
      hasUserEvent: raw?.hasUserEvent !== false,
      messageCount: Math.max(0, Number(raw?.messageCount) || 0),
      accountId: idOf(raw?.accountId),
      codexHome: String(raw?.codexHome || requestedCodexHome || ""),
    };
  }

  function filteredHistoryThreads() {
    const currentThreads = restrictHistoryThreadsToActiveAccounts(app.historyThreads);
    if (app.historyArchiveFilter === "active") return currentThreads.filter((thread) => !thread.archived);
    if (app.historyArchiveFilter === "archived") return currentThreads.filter((thread) => thread.archived);
    return currentThreads;
  }

  function renderHistoryList() {
    if (!el.historyList) return;
    const threads = filteredHistoryThreads();
    el.historyCount.textContent = `${threads.length} 条记录`;
    if (!threads.length) {
      const searching = Boolean(el.historySearch.value.trim());
      el.historyList.innerHTML = `<div class="empty-state"><span class="empty-state-mark">⌁</span><h3>${searching ? "没有匹配的聊天记录" : "暂无聊天记录"}</h3><p>${searching ? "请更换关键词或账号筛选条件。" : "Codex 会话写入本地后会显示在这里。"}</p></div>`;
      return;
    }
    el.historyList.innerHTML = threads.map((thread) => `<button class="history-row${thread.id === app.selectedThreadId ? " is-selected" : ""}" type="button" data-thread-id="${escapeAttr(thread.id)}">
      <span class="history-row-heading"><strong>${escapeHtml(thread.title)}</strong>${thread.archived ? `<i class="archive-tag">已归档</i>` : ""}</span>
      <p>${escapeHtml(thread.preview || "暂无摘要")}</p>
      <span class="history-row-meta"><span>${escapeHtml(thread.model || thread.provider || "默认模型")}</span><span>·</span><span>${escapeHtml(formatDateTime(thread.updatedAt))}</span>${thread.messageCount ? `<span>· ${thread.messageCount} 条消息</span>` : ""}</span>
    </button>`).join("");
  }

  function handleHistoryListClick(event) {
    const row = event.target.closest("[data-thread-id]");
    if (!row) return;
    selectHistoryThread(row.dataset.threadId);
  }

  async function selectHistoryThread(threadId) {
    const thread = app.historyThreads.find((item) => item.id === threadId);
    if (!thread) return;
    app.selectedThreadId = thread.id;
    app.selectedThread = thread;
    app.threadDetail = null;
    renderHistoryList();
    renderThreadReader();
    const requestId = ++app.threadRequest;
    try {
      const raw = await invoke("readThread", {
        threadId: thread.id,
        maxMessages: 80,
        maxMessageCharacters: 4000,
        ...(thread.codexHome ? { codexHome: thread.codexHome } : {}),
      });
      if (requestId !== app.threadRequest || app.selectedThreadId !== thread.id) return;
      app.threadDetail = normalizeThreadDetail(raw);
    } catch (error) {
      if (requestId !== app.threadRequest || app.selectedThreadId !== thread.id) return;
      app.threadDetail = { status: "unavailable", messages: [], isTruncated: false, notice: friendlyError(error) };
    }
    renderThreadReader();
  }

  function normalizeThreadDetail(raw) {
    const allowedStatuses = ["available", "empty", "source_missing", "unavailable"];
    return {
      status: allowedStatuses.includes(raw?.status) ? raw.status : "available",
      messages: Array.isArray(raw?.messages) ? raw.messages.map((message) => ({
        role: message?.role === "user" ? "user" : "assistant",
        text: String(message?.text || ""),
        timestamp: message?.timestamp || null,
      })).filter((message) => message.text) : [],
      isTruncated: Boolean(raw?.isTruncated),
      notice: String(raw?.notice || ""),
    };
  }

  function renderThreadReader() {
    const thread = app.selectedThread;
    el.threadReaderEmpty.classList.toggle("is-hidden", Boolean(thread));
    el.threadReaderContent.classList.toggle("is-hidden", !thread);
    if (!thread) return;
    el.threadTitle.textContent = thread.title;
    el.threadMeta.textContent = [thread.workingDirectory, thread.model || thread.provider, formatDateTime(thread.updatedAt)].filter(Boolean).join(" · ") || "本地会话";
    el.archiveThreadButton.textContent = thread.archived ? "取消归档" : "归档";
    if (!app.threadDetail) {
      el.threadMessages.innerHTML = `<div class="empty-state"><span class="empty-state-mark">…</span><h3>正在读取消息</h3></div>`;
      return;
    }
    const detail = app.threadDetail;
    if (!detail.messages.length) {
      const notices = {
        empty: "该会话没有可显示的用户或助手消息。",
        source_missing: "原始会话文件已经不存在。",
        unavailable: "暂时无法读取这条会话。",
      };
      el.threadMessages.innerHTML = `<div class="empty-state"><span class="empty-state-mark">⌁</span><h3>没有可显示的消息</h3><p>${escapeHtml(detail.notice || notices[detail.status] || "本地会话内容为空。")}</p></div>`;
      return;
    }
    el.threadMessages.innerHTML = `${detail.isTruncated ? `<div class="quota-error">为保证界面流畅，仅显示部分消息。</div>` : ""}${detail.messages.map((message) => `<article class="thread-message ${message.role}">
      <div class="thread-message-header"><strong>${message.role === "user" ? "你" : "Codex"}</strong><span>${escapeHtml(formatDateTime(message.timestamp))}</span></div>
      <pre class="thread-message-body">${escapeHtml(message.text)}</pre>
    </article>`).join("")}`;
  }

  function clearSelectedThread() {
    app.threadRequest += 1;
    app.selectedThreadId = null;
    app.selectedThread = null;
    app.threadDetail = null;
    if (el.threadReaderEmpty) renderThreadReader();
  }

  async function toggleSelectedThreadArchive() {
    const thread = app.selectedThread;
    if (!thread) return;
    const archived = !thread.archived;
    await withButtonBusy(el.archiveThreadButton, archived ? "归档中…" : "恢复中…", async () => {
      await invoke("setThreadArchived", {
        threadId: thread.id,
        archived,
        ...(thread.codexHome ? { codexHome: thread.codexHome } : {}),
      });
      thread.archived = archived;
      renderHistoryList();
      renderThreadReader();
      showToast(archived ? "聊天记录已归档" : "聊天记录已恢复", thread.title);
    });
  }

  async function deleteSelectedThread() {
    const thread = app.selectedThread;
    if (!thread) return;
    const confirmed = await confirmAction("删除这条聊天记录？", `“${thread.title}”将从本地永久删除，此操作不可撤销。`, "永久删除");
    if (!confirmed) return;
    await withButtonBusy(el.deleteThreadButton, "删除中…", async () => {
      await invoke("deleteThread", {
        threadId: thread.id,
        ...(thread.codexHome ? { codexHome: thread.codexHome } : {}),
      });
      clearSelectedThread();
      await loadHistory(false);
      showToast("聊天记录已删除", thread.title);
    });
  }

  async function copySelectedThread() {
    const messages = app.threadDetail?.messages || [];
    if (!messages.length) return;
    const body = messages.map((message) => `${message.role === "user" ? "你" : "Codex"}\n${message.text}`).join("\n\n");
    try {
      await copyText(body);
      showToast("聊天正文已复制", app.selectedThread?.title || "");
    } catch (error) {
      showToast("复制失败", friendlyError(error), "error");
    }
  }

  function renderStatusAccounts() {
    if (!el.statusAccountList) return;
    if (!app.accounts.length) {
      el.statusAccountList.innerHTML = `<div class="empty-state"><span class="empty-state-mark">✓</span><h3>还没有账号</h3><p>请先在账号中心添加账号。</p></div>`;
      return;
    }
    el.statusAccountList.innerHTML = app.accounts.map((account) => {
      const colors = avatarColors(account.id || account.name);
      const status = statusFor(account.id);
      const statusClass = status.loggedIn ? " online" : status.error ? " error" : status.checking ? " checking" : "";
      const statusLabel = account.authKind === "compatible_api" && status.loggedIn ? "API Key 已配置" : status.label;
      return `<div class="status-account-row" data-account-id="${escapeAttr(account.id)}">
        <div class="status-account-identity"><div class="account-avatar" style="--avatar-a:${colors[0]};--avatar-b:${colors[1]}">${escapeHtml(initialOf(account.name))}</div><div><strong>${escapeHtml(account.name)}</strong><span>${escapeHtml(authLabel(account))}</span></div></div>
        <div class="status-account-meta"><strong title="${escapeAttr(account.codexHome)}">${escapeHtml(account.codexHome || "自动管理")}</strong><span>${escapeHtml(account.authKind === "compatible_api" ? (account.apiModel || "默认模型") : "模型由 Codex 决定")}</span></div>
        <span class="status-pill${statusClass}"><i></i>${escapeHtml(statusLabel)}</span>
        <div class="status-account-actions"><button class="mini-button" type="button" data-status-action="check">检查</button><button class="mini-button" type="button" data-status-action="credentials">更新凭据</button><button class="mini-button" type="button" data-status-action="edit">编辑</button></div>
      </div>`;
    }).join("");
  }

  function handleStatusAccountAction(event) {
    const button = event.target.closest("[data-status-action]");
    const row = button?.closest("[data-account-id]");
    if (!button || !row) return;
    const account = accountById(row.dataset.accountId);
    if (!account) return;
    app.selectedAccountId = account.id;
    if (button.dataset.statusAction === "check") checkLogin(account.id, true);
    else if (button.dataset.statusAction === "credentials") openSelectedAccountLogin();
    else if (button.dataset.statusAction === "edit") openAccountModal(account);
  }

  async function checkAllLoginStatuses() {
    await withButtonBusy(el.statusCheckAllButton, "检查中…", async () => {
      if (typeof bridge.getAllLoginStatuses === "function") {
        const result = await invoke("getAllLoginStatuses");
        app.loginStatuses = {
          ...app.loginStatuses,
          ...normalizeStatuses(result?.statuses || result?.accounts || result),
        };
        renderAccountList();
        renderAccountDetail();
        renderCurrentCard();
        renderStatusAccounts();
      } else {
        for (const account of app.accounts) await checkLogin(account.id, false);
      }
      showToast("状态检查完成", `已检查 ${app.accounts.length} 个账号。`);
    });
  }

  function renderUsageAccountFilter() {
    if (!el.usageAccountFilter) return;
    const options = [{ value: "all", label: "全部账号" }, { value: "unattributed", label: "未归属会话" }];
    for (const account of app.accounts) {
      options.push({ value: account.id, label: account.name });
    }
    app.usageAccountFilter = setSelectOptions(el.usageAccountFilter, options, app.usageAccountFilter);
  }

  function usageScopeExists(report, scope) {
    if (scope === "all" || scope === "unattributed") return true;
    return Boolean(accountById(scope)) && report.perAccount.some((item) => item.accountId === scope);
  }

  function selectUsageScope(report, scope) {
    if (scope === "unattributed") return report.unattributed;
    if (scope !== "all") {
      if (!accountById(scope)) return emptyUsage();
      return report.perAccount.find((item) => item.accountId === scope) || emptyUsage();
    }
    return report.aggregate;
  }

  function usageForAccount(accountId) {
    if (!accountById(accountId)) return emptyUsage();
    return app.usageReport.perAccount.find((item) => item.accountId === idOf(accountId)) || emptyUsage();
  }

  function activeAccountIds() {
    return new Set(app.accounts.map((account) => account.id));
  }

  function restrictUsageReportToActiveAccounts(report) {
    const activeIds = activeAccountIds();
    const perAccount = report.perAccount.filter((item) => activeIds.has(item.accountId));
    const removedAccountRows = perAccount.length !== report.perAccount.length;
    return {
      ...report,
      perAccount,
      aggregate: removedAccountRows
        ? combineUsageScopes([...perAccount, report.unattributed], report.aggregate?.range || app.range)
        : report.aggregate,
    };
  }

  function combineUsageScopes(scopes, range) {
    const combined = emptyUsage();
    combined.range = String(range || app.range);
    combined.apiEquivalentComplete = scopes.every((scope) => scope.apiEquivalentComplete !== false);
    combined.apiEquivalentUsd = combined.apiEquivalentComplete
      ? scopes.reduce((sum, scope) => sum + numeric(scope.apiEquivalentUsd), 0)
      : null;
    for (const key of ["totalTokens", "inputTokens", "cachedInputTokens", "cacheWriteTokens", "outputTokens", "knownApiEquivalentUsd"]) {
      combined[key] = scopes.reduce((sum, scope) => sum + numeric(scope[key]), 0);
    }

    const models = new Map();
    for (const scope of scopes) {
      for (const model of scope.models || []) {
        const key = String(model.model || "未知模型");
        const target = models.get(key) || { model: key, tokens: 0, cost: 0, costKnown: true, color: String(model.color || "") };
        target.tokens += numeric(model.tokens);
        target.cost += numeric(model.cost);
        target.costKnown = target.costKnown && model.costKnown !== false && model.cost !== null;
        if (!target.color && model.color) target.color = String(model.color);
        models.set(key, target);
      }
    }
    combined.models = [...models.values()].map((model) => ({
      ...model,
      cost: model.costKnown ? model.cost : null,
    }));

    const timeline = new Map();
    for (const scope of scopes) {
      for (const item of scope.timeline || []) {
        const key = String(item.label || "");
        const target = timeline.get(key) || { label: key, totalTokens: 0, apiEquivalentUsd: 0 };
        target.totalTokens += numeric(item.totalTokens);
        target.apiEquivalentUsd += numeric(item.apiEquivalentUsd);
        timeline.set(key, target);
      }
    }
    combined.timeline = [...timeline.values()];
    return combined;
  }

  function restrictQuotaReportToActiveAccounts(report) {
    const activeIds = activeAccountIds();
    return {
      ...report,
      accounts: report.accounts.filter((account) => activeIds.has(account.accountId)),
    };
  }

  function restrictHistoryThreadsToActiveAccounts(threads) {
    const activeIds = activeAccountIds();
    return threads.filter((thread) => !thread.accountId || thread.accountId === "shared" || activeIds.has(thread.accountId));
  }

  function threadBelongsToAccount(thread, account) {
    if (!thread || !account) return false;
    if (thread.accountId && thread.accountId === account.id) return true;
    return Boolean(account.codexHome && thread.codexHome && thread.codexHome === account.codexHome);
  }

  function pruneDerivedStateToActiveAccounts() {
    const activeIds = activeAccountIds();
    app.usageReport = restrictUsageReportToActiveAccounts(app.usageReport);
    app.quotaReport = restrictQuotaReportToActiveAccounts(app.quotaReport);
    app.historyThreads = restrictHistoryThreadsToActiveAccounts(app.historyThreads);
    if (app.selectedThread && !app.historyThreads.some((thread) => thread.id === app.selectedThread.id)) clearSelectedThread();
    if (!usageScopeExists(app.usageReport, app.usageAccountFilter)) app.usageAccountFilter = "all";
    if (app.quotaAccountFilter !== "all" && !activeIds.has(app.quotaAccountFilter)) app.quotaAccountFilter = "all";
    if (app.historyAccountFilter !== "all" && app.historyAccountFilter !== "shared" && !activeIds.has(app.historyAccountFilter)) app.historyAccountFilter = "all";
    app.usage = selectUsageScope(app.usageReport, app.usageAccountFilter);
  }

  function invalidateUsageAndQuotaRequests() {
    app.usageRequest += 1;
    app.usageInFlight = false;
    if (el.usagePage && el.rangeControl) setUsageLoading(false);
    app.quotaRequest += 1;
    app.quotaInFlight = false;
    if (el.refreshQuotaButton) {
      el.refreshQuotaButton.disabled = false;
      el.refreshQuotaButton.textContent = "刷新额度";
    }
  }

  function invalidateHistoryAndThreadRequests() {
    app.historyRequest += 1;
    app.threadRequest += 1;
    if (el.refreshHistoryButton) {
      el.refreshHistoryButton.disabled = false;
      el.refreshHistoryButton.textContent = "刷新记录";
    }
  }

  function invalidateStateUsageAndQuotaRequests() {
    app.stateRequest += 1;
    invalidateUsageAndQuotaRequests();
  }

  function invalidateAllAccountDerivedRequests() {
    invalidateStateUsageAndQuotaRequests();
    invalidateHistoryAndThreadRequests();
  }

  function clearUsageRefreshTimer() {
    if (app.usageRefreshTimer) window.clearInterval(app.usageRefreshTimer);
    app.usageRefreshTimer = null;
  }

  function updateUsageRefreshTimer() {
    clearUsageRefreshTimer();
    if (app.usageRefreshSeconds <= 0) return;
    app.usageRefreshTimer = window.setInterval(() => loadUsage(false), app.usageRefreshSeconds * 1000);
  }

  function clearQuotaRefreshTimer() {
    if (app.quotaRefreshTimer) window.clearInterval(app.quotaRefreshTimer);
    app.quotaRefreshTimer = null;
  }

  function updateQuotaRefreshTimer() {
    clearQuotaRefreshTimer();
    if (app.activePage !== "quota" || app.quotaRefreshSeconds <= 0) return;
    app.quotaRefreshTimer = window.setInterval(() => loadQuota(false), app.quotaRefreshSeconds * 1000);
  }

  async function loadQuota(showLoading = false) {
    if (app.quotaInFlight) return;
    app.quotaInFlight = true;
    const requestId = ++app.quotaRequest;
    const button = el.refreshQuotaButton;
    const oldHtml = button.innerHTML;
    if (showLoading) {
      button.disabled = true;
      button.textContent = "正在读取…";
    }
    try {
      const raw = await invoke("getQuotaStats", {});
      if (requestId !== app.quotaRequest) return;
      app.quotaReport = restrictQuotaReportToActiveAccounts(normalizeQuotaReport(raw));
      app.quotaLoaded = true;
      renderQuota();
    } catch (error) {
      if (requestId === app.quotaRequest) {
        app.quotaLoaded = true;
        app.quotaReport = { updatedAt: null, accounts: [] };
        renderQuota();
        showToast("额度读取失败", friendlyError(error), "error");
      }
    } finally {
      if (requestId === app.quotaRequest) {
        app.quotaInFlight = false;
        if (showLoading) {
          button.disabled = false;
          button.innerHTML = oldHtml;
        }
      }
    }
  }

  function normalizeQuotaReport(raw) {
    const rows = Array.isArray(raw?.accounts) ? raw.accounts : Array.isArray(raw) ? raw : [];
    return {
      updatedAt: raw?.updatedAt || raw?.generatedAt || null,
      accounts: rows.map((item, index) => normalizeQuotaAccount(item, index)),
    };
  }

  function normalizeQuotaAccount(raw, index) {
    const primary = normalizeQuotaWindow(raw?.primary, "primary");
    const secondary = normalizeQuotaWindow(raw?.secondary, "secondary");
    const windows = raw?.windows || {};
    const primaryMinutes = primary?.windowMinutes || 0;
    const secondaryMinutes = secondary?.windowMinutes || 0;
    return {
      accountId: idOf(raw?.accountId || raw?.id) || `quota-${index}`,
      accountName: String(raw?.accountName || raw?.name || "未命名账号"),
      authKind: String(raw?.authKind || ""),
      supported: raw?.supported !== false,
      available: raw?.available !== false,
      source: String(raw?.source || ""),
      observedAt: raw?.observedAt || null,
      planType: String(raw?.planType || raw?.plan || ""),
      windows: {
        fiveHour: normalizeQuotaWindow(windows.fiveHour || windows["5h"] || raw?.fiveHour || (primaryMinutes && primaryMinutes <= 360 ? raw?.primary : null) || (secondaryMinutes && secondaryMinutes <= 360 ? raw?.secondary : null), "5h"),
        weekly: normalizeQuotaWindow(windows.weekly || windows.week || raw?.weekly || (primaryMinutes >= 6000 && primaryMinutes < 20000 ? raw?.primary : null) || (secondaryMinutes >= 6000 && secondaryMinutes < 20000 ? raw?.secondary : null), "weekly"),
        monthly: normalizeQuotaWindow(windows.monthly || windows.month || raw?.monthly || (primaryMinutes >= 20000 ? raw?.primary : null) || (secondaryMinutes >= 20000 ? raw?.secondary : null), "monthly"),
      },
      primary,
      secondary,
      credits: raw?.credits ?? null,
      individualLimit: raw?.individualLimit ?? null,
      error: String(raw?.error || ""),
    };
  }

  function normalizeQuotaWindow(raw, fallbackKind) {
    if (!raw || typeof raw !== "object") return null;
    const usedPercent = percentOrNull(raw.usedPercent ?? raw.used ?? raw.utilizationPercent);
    const explicitRemaining = percentOrNull(raw.remainingPercent ?? raw.remaining);
    const remainingPercent = explicitRemaining == null && usedPercent != null ? 100 - usedPercent : explicitRemaining;
    return {
      kind: String(raw.kind || fallbackKind || ""),
      usedPercent,
      remainingPercent,
      windowMinutes: Math.max(0, Number(raw.windowMinutes ?? raw.minutes) || 0),
      resetsAt: raw.resetsAt || raw.resetAt || null,
      observedAt: raw.observedAt || null,
    };
  }

  function renderQuota() {
    if (!el.quotaGrid) return;
    const activeIds = activeAccountIds();
    const activeRows = app.quotaReport.accounts.filter((item) => activeIds.has(item.accountId));
    const rows = app.quotaAccountFilter === "all"
      ? activeRows
      : activeRows.filter((item) => item.accountId === app.quotaAccountFilter);
    el.quotaUpdatedAt.textContent = `${app.quotaReport.updatedAt ? `更新于 ${formatDateTime(app.quotaReport.updatedAt)}` : "尚未读取"} · 每 ${app.quotaRefreshSeconds} 秒自动刷新`;
    if (!rows.length) {
      el.quotaGrid.innerHTML = `<div class="panel empty-state"><span class="empty-state-mark">%</span><h3>${app.quotaLoaded ? "暂无额度数据" : "尚未读取额度"}</h3><p>${app.quotaLoaded ? "当前账号可能不支持额度快照，或尚未产生本地额度记录。" : "进入此页面后会从各账号的独立环境读取。"}</p></div>`;
      return;
    }
    el.quotaGrid.innerHTML = rows.map((account) => {
      const windows = [
        ["5 小时额度", account.windows.fiveHour],
        ["周额度", account.windows.weekly],
        ["月额度", account.windows.monthly],
      ].filter((entry) => entry[1]);
      const auxiliary = quotaAuxiliaryText(account);
      const statusLabel = account.available ? (account.error ? "本地快照" : "已读取") : "不可用";
      return `<article class="quota-account-card">
        <div class="quota-card-heading"><div><h3>${escapeHtml(account.accountName)}</h3><p>${escapeHtml([account.planType, quotaSourceLabel(account.source), auxiliary].filter(Boolean).join(" · ") || "独立账号额度")}</p></div><span class="status-pill${account.available ? " online" : " error"}"><i></i>${statusLabel}</span></div>
        <div class="quota-window-list">${windows.length ? windows.map(([label, window]) => quotaWindowMarkup(label, window)).join("") : `<div class="empty-state"><p>${account.supported ? "没有可显示的额度窗口。" : "此登录方式不提供官方额度窗口。"}</p></div>`}</div>
        ${account.error ? `<p class="quota-error">${escapeHtml(account.error)}</p>` : ""}
      </article>`;
    }).join("");
  }

  function quotaWindowMarkup(label, window) {
    const remaining = window.remainingPercent;
    const used = window.usedPercent;
    const remainingText = remaining == null ? "未知" : formatPercent(remaining);
    const usedText = used == null ? "" : `已用 ${formatPercent(used)}`;
    return `<div class="quota-window"><div class="quota-window-heading"><span>${escapeHtml(label)}</span><strong>剩余 ${escapeHtml(remainingText)}</strong></div><div class="quota-progress"><i style="--quota-remaining:${remaining == null ? 0 : remaining}%"></i></div><small>${escapeHtml([usedText, window.resetsAt ? `重置于 ${formatDateTime(window.resetsAt)}` : ""].filter(Boolean).join(" · ") || "等待下一次额度快照")}</small></div>`;
  }

  function quotaAuxiliaryText(account) {
    const credits = account.credits;
    const limit = account.individualLimit;
    const creditValue = typeof credits === "number" || typeof credits === "string"
      ? credits
      : credits?.remaining ?? credits?.balance ?? credits?.value;
    const limitValue = typeof limit === "number" || typeof limit === "string"
      ? limit
      : limit?.remaining ?? limit?.limit ?? limit?.value;
    if (creditValue != null && creditValue !== "") return `Credits ${creditValue}`;
    if (limitValue != null && limitValue !== "") return `独立上限 ${limitValue}`;
    return "";
  }

  function quotaSourceLabel(source) {
    return ({ "app-server": "实时状态", session: "本地会话", cache: "本地快照", hybrid: "实时与本地", unavailable: "不可用" })[source] || source;
  }

  async function loadCodexThemes(showLoading = false) {
    const button = el.refreshThemesButton;
    const oldHtml = button.innerHTML;
    if (showLoading) {
      button.disabled = true;
      button.textContent = "正在读取…";
    }
    try {
      const raw = await invoke("getCodexThemes");
      const items = Array.isArray(raw?.themes) ? raw.themes : Array.isArray(raw?.items) ? raw.items : Array.isArray(raw) ? raw : [];
      const currentId = idOf(raw?.currentThemeId || raw?.activeThemeId || raw?.codexThemeId);
      app.codexThemes = items.map((theme, index) => normalizeCodexTheme(theme, index, currentId));
      app.themesLoaded = true;
      populateCustomThemeForm(raw?.customTheme || raw?.customCodexTheme);
      renderCodexThemes();
    } catch (error) {
      app.themesLoaded = true;
      app.codexThemes = [];
      renderCodexThemes();
      showToast("主题读取失败", friendlyError(error), "error");
    } finally {
      if (showLoading) {
        button.disabled = false;
        button.innerHTML = oldHtml;
      }
    }
  }

  function normalizeCodexTheme(raw, index, currentId) {
    const id = idOf(raw?.id || raw?.themeId || raw?.name) || `theme-${index}`;
    return {
      id,
      name: String(raw?.name || raw?.label || id),
      mode: String(raw?.mode || (raw?.isDark === false ? "light" : "dark")),
      codeTheme: String(raw?.codeTheme || raw?.codeThemeId || ""),
      accent: validColor(raw?.accent || raw?.accentColor) || "#6173ff",
      surface: validColor(raw?.surface || raw?.surfaceColor) || "#151b2d",
      text: validColor(raw?.text || raw?.textColor || raw?.inkColor) || "#f2f5fb",
      active: Boolean(raw?.active || raw?.isActive) || id === currentId,
    };
  }

  function renderCodexThemes() {
    if (!app.codexThemes.length) {
      el.themeGrid.innerHTML = `<div class="empty-state"><span class="empty-state-mark">◐</span><h3>${app.themesLoaded ? "暂无可用主题" : "尚未读取主题"}</h3><p>主题服务返回本机可用方案后会显示在这里。</p></div>`;
      return;
    }
    el.themeGrid.innerHTML = app.codexThemes.map((theme) => `<article class="theme-card">
      <div class="theme-preview" style="--theme-accent:${theme.accent};--theme-surface:${theme.surface};--theme-text:${theme.text}"></div>
      <div class="theme-card-footer"><div><strong>${escapeHtml(theme.name)}</strong><span>${escapeHtml([theme.mode === "light" ? "浅色" : "深色", theme.codeTheme].filter(Boolean).join(" · "))}</span></div><button class="mini-button" type="button" data-theme-action="apply" data-theme-id="${escapeAttr(theme.id)}" ${theme.active ? "disabled" : ""}>${theme.active ? "已应用" : "应用"}</button></div>
    </article>`).join("");
  }

  async function handleThemeAction(event) {
    const button = event.target.closest("[data-theme-action='apply']");
    if (!button) return;
    const theme = app.codexThemes.find((item) => item.id === button.dataset.themeId);
    if (!theme) return;
    await withButtonBusy(button, "应用中…", async () => {
      await invoke("applyCodexTheme", theme.id);
      app.codexThemes.forEach((item) => { item.active = item.id === theme.id; });
      renderCodexThemes();
      showToast("Codex 主题已应用", theme.name);
    });
  }

  async function restoreOfficialCodexTheme() {
    await withButtonBusy(el.restoreThemeButton, "恢复中…", async () => {
      const result = await invoke("restoreCodexTheme");
      await loadCodexThemes(false);
      if (result?.runtimeRestored === false) {
        showToast(
          "已恢复官方主题设置",
          result.reason || "当前 Codex App 的外观无法确认，请退出并重新启动后生效。",
          "error",
        );
      } else if (result?.runtimeRestored !== true && result?.reason) {
        showToast("已恢复官方主题设置", result.reason);
      } else {
        showToast("已恢复官方主题", "Codex App 已返回官方外观。");
      }
    });
  }

  function populateCustomThemeForm(raw) {
    if (!raw || typeof raw !== "object") return;
    el.customThemeName.value = String(raw.name || "");
    el.customThemeMode.value = raw.mode === "light" || raw.isDark === false ? "light" : "dark";
    el.customThemeCodeTheme.value = String(raw.codeTheme || raw.codeThemeId || "");
    el.customThemeAccent.value = validColor(raw.accent || raw.accentColor) || "#6173ff";
    el.customThemeSurface.value = validColor(raw.surface || raw.surfaceColor) || "#151b2d";
    el.customThemeText.value = validColor(raw.text || raw.textColor || raw.inkColor) || "#f2f5fb";
    el.customThemeBackground.value = String(raw.background || raw.backgroundImagePath || "");
  }

  async function saveCustomCodexTheme(event) {
    event.preventDefault();
    if (!el.customThemeForm.reportValidity()) return;
    const payload = {
      name: el.customThemeName.value.trim(),
      mode: el.customThemeMode.value,
      isDark: el.customThemeMode.value !== "light",
      codeTheme: el.customThemeCodeTheme.value.trim(),
      codeThemeId: el.customThemeCodeTheme.value.trim(),
      accentColor: el.customThemeAccent.value,
      surfaceColor: el.customThemeSurface.value,
      textColor: el.customThemeText.value,
      inkColor: el.customThemeText.value,
      backgroundImagePath: el.customThemeBackground.value.trim(),
    };
    await withButtonBusy(el.saveCustomThemeButton, "保存中…", async () => {
      await invoke("saveCustomTheme", payload);
      await loadCodexThemes(false);
      showToast("自定义主题已保存", payload.name);
    });
  }

  async function loadSettings(showLoading = false) {
    const button = el.saveSystemSettingsButton;
    const oldHtml = button.innerHTML;
    if (showLoading) {
      button.disabled = true;
      button.textContent = "正在读取…";
    }
    try {
      const raw = await invoke("getSystemSettings");
      app.systemSettings = raw && typeof raw === "object" ? raw : {};
      app.settingsLoaded = true;
      renderSettings();
    } catch (error) {
      app.settingsLoaded = true;
      showToast("系统配置读取失败", friendlyError(error), "error");
    } finally {
      if (showLoading) {
        button.disabled = false;
        button.innerHTML = oldHtml;
      }
    }
  }

  function renderSettings() {
    const settings = app.systemSettings || {};
    el.projectRootInput.value = String(settings.projectRoot || settings.appRoot || settings.rootPath || "");
    el.launchDirectoryInput.value = String(settings.launchDirectory || settings.projectPath || settings.workspacePath || "");
    el.codexAppPathInput.value = String(settings.codexAppPath || "");
    updateCodexAppPathHint();
    el.proxyHostInput.value = String(settings.proxyAddress || settings.proxyHost || settings.address || "127.0.0.1");
    el.proxyPortInput.value = settings.proxyPort || settings.detectedProxyPort || "";
    el.proxyAutoDetectInput.checked = settings.proxyAutoDetect !== false && settings.autoDetect !== false;
    el.detectedProxyStatus.textContent = settings.detectedProxyPort
      ? `上次检测：127.0.0.1:${settings.detectedProxyPort}`
      : "尚未检测";
  }

  async function saveSettings(event) {
    event.preventDefault();
    const port = optionalPort(el.proxyPortInput.value);
    if (el.proxyPortInput.value && !port) {
      showToast("代理端口无效", "请输入 1–65535 的端口；8317 不会被本软件用作代理端口。", "error");
      return;
    }
    const launchDirectory = el.launchDirectoryInput.value.trim();
    const codexAppPath = el.codexAppPathInput.value.trim();
    const proxyAddress = el.proxyHostInput.value.trim() || "127.0.0.1";
    const payload = {
      projectPath: launchDirectory,
      launchDirectory,
      codexAppPath,
      proxyAddress,
      proxyHost: proxyAddress,
      proxyPort: port,
      proxyAutoDetect: el.proxyAutoDetectInput.checked,
    };
    await withButtonBusy(el.saveSystemSettingsButton, "保存中…", async () => {
      app.systemSettings = await invoke("saveSystemSettings", payload) || { ...(app.systemSettings || {}), ...payload };
      renderSettings();
      showToast("系统配置已保存", port ? `代理：${proxyAddress}:${port}` : "未设置固定代理端口");
    });
  }

  async function chooseCodexApp() {
    await withButtonBusy(el.chooseCodexAppButton, "选择中…", async () => {
      try {
        const result = await invoke("chooseCodexApp");
        if (result?.canceled === true || result?.cancelled === true) return;
        const selectedPath = codexAppPathFromSelection(result);
        if (!selectedPath) throw new Error("没有收到有效的桌面应用路径，请重新选择。");
        el.codexAppPathInput.value = selectedPath;
        updateCodexAppPathHint();
        showToast("已选择桌面应用", "身份校验已通过，请保存系统配置后再启动。");
      } catch (error) {
        showToast("无法选择桌面应用", friendlyError(error), "error");
      }
    });
  }

  function useAutomaticCodexAppDetection() {
    if (!el.codexAppPathInput.value) {
      showToast("当前已使用自动检测", "启动时会检查标准的 ChatGPT.app 与旧 Codex.app 安装位置。");
      return;
    }
    el.codexAppPathInput.value = "";
    updateCodexAppPathHint();
    showToast("已改为自动检测", "请保存系统配置后生效。");
  }

  function updateCodexAppPathHint() {
    el.codexAppPathHint.textContent = el.codexAppPathInput.value
      ? "将使用此含 Codex 的桌面应用；更改后需保存系统配置。"
      : "当前使用自动检测；启动时会检查 ChatGPT.app 与旧 Codex.app。";
  }

  function codexAppPathFromSelection(result) {
    if (typeof result === "string") return result.trim();
    if (!result || typeof result !== "object") return "";
    const selectedPath = result.path || result.appPath || result.codexAppPath || result.filePath || result.filePaths?.[0];
    return typeof selectedPath === "string" ? selectedPath.trim() : "";
  }

  async function detectProxy() {
    const preferredPort = optionalPort(el.proxyPortInput.value);
    const options = preferredPort ? { preferredPort } : {};
    await withButtonBusy(el.detectProxyButton, "检测中…", async () => {
      const result = await invoke("detectLocalProxy", options);
      if (!result?.found || !optionalPort(result?.port)) {
        el.detectedProxyStatus.textContent = `未检测到本地代理${Number(result?.checkedPorts) ? `（已检查 ${result.checkedPorts} 个端口）` : ""}`;
        showToast("未检测到本地代理", "请确认代理软件已启动，或手动填写地址与端口。", "error");
        return;
      }
      el.proxyHostInput.value = String(result.address || result.host || "127.0.0.1");
      el.proxyPortInput.value = String(result.port);
      el.detectedProxyStatus.textContent = `检测到 ${result.scheme || "http"}://${el.proxyHostInput.value}:${result.port}`;
      showToast("已检测到本地代理", `${el.proxyHostInput.value}:${result.port}`);
    });
  }

  async function openConfiguredPath(targetPath) {
    const target = String(targetPath || "").trim();
    if (!target) {
      showToast("目录尚未设置", "请先填写并保存启动目录。", "error");
      return;
    }
    try {
      await invoke("openPath", target);
    } catch (error) {
      showToast("无法打开目录", friendlyError(error), "error");
    }
  }

  function showPage(pageName) {
    app.activePage = pageName;
    document.querySelectorAll(".nav-item").forEach((button) => button.classList.toggle("is-active", button.dataset.page === pageName));
    document.querySelectorAll("[data-page-panel]").forEach((page) => page.classList.toggle("is-active", page.dataset.pagePanel === pageName));
    updateUsageRefreshTimer();
    updateQuotaRefreshTimer();
    if (pageName === "usage") requestAnimationFrame(renderChart);
    if (pageName === "history" && !app.historyLoaded) loadHistory(false);
    if (pageName === "status") renderStatusAccounts();
    if (pageName === "quota" && !app.quotaLoaded) loadQuota(false);
    if (pageName === "themes" && !app.themesLoaded) loadCodexThemes(false);
    if (pageName === "settings" && !app.settingsLoaded) loadSettings(false);
  }

  async function importAccounts() {
    await withButtonBusy(el.importButton, "正在导入…", async () => {
      const result = await invoke("importAccounts");
      if (result?.canceled) return;
      await loadState();
      await loadUsage(false);
      showToast("导入完成", importResultMessage(result));
    });
  }

  function openSelectedAccountLogin() {
    const account = selectedAccount();
    if (!account) return;
    if (account.authKind === "official_oauth") openAccountModal(account);
    else openLoginModal();
  }

  function openAccountModal(account = null) {
    const previousDraftId = resetOAuthDraftState({ requiresFreshLogin: false });
    cancelOAuthDraftById(previousDraftId);
    app.editingAccountId = account?.id || null;
    app.editingOriginalAuthKind = account?.authKind || null;
    el.accountForm.reset();
    el.accountModalEyebrow.textContent = account ? "EDIT PROFILE" : "NEW PROFILE";
    el.accountModalTitle.textContent = account ? "编辑账号" : "添加账号";
    el.saveAccountButton.textContent = account ? "保存更改" : "保存账号";
    el.accountNameInput.value = account?.name || "";
    el.authKindInput.value = account?.authKind || "access_token";
    el.modelInput.value = account?.apiModel || "";
    el.codexHomeInput.value = account?.codexHome || "";
    el.providerNameInput.value = account?.apiProviderName || "";
    el.baseUrlInput.value = account?.apiBaseUrl || "";
    el.wireApiInput.value = account?.apiWireApi || "responses";
    el.secretInput.value = "";
    el.secretInput.type = "password";
    el.revealSecretButton.textContent = "显示";
    updateAuthFields();
    showDialog(el.accountModal);
    if (account?.authKind === "official_oauth") prepareReusableOAuthDraft();
    requestAnimationFrame(() => el.accountNameInput.focus());
  }

  function handleAuthKindChange() {
    const draftId = resetOAuthDraftState({ requiresFreshLogin: true });
    updateAuthFields();
    cancelOAuthDraftById(draftId);
  }

  function updateAuthFields() {
    const compatible = el.authKindInput.value === "compatible_api";
    const officialOAuth = el.authKindInput.value === "official_oauth";
    el.apiFields.classList.toggle("is-hidden", !compatible);
    el.modelField.classList.toggle("is-hidden", !compatible);
    el.oauthFields.classList.toggle("is-hidden", !officialOAuth);
    el.oauthLoginButton.classList.toggle("is-hidden", !officialOAuth);
    el.oauthLoginStatus.classList.toggle("is-hidden", !officialOAuth);
    el.accountSecretField.classList.toggle("is-hidden", officialOAuth);
    el.providerNameInput.required = compatible;
    el.baseUrlInput.required = compatible;
    el.secretFieldLabel.textContent = compatible ? "API Key" : "Access Token";
    const editing = Boolean(app.editingAccountId);
    const changedAuthKind = editing && app.editingOriginalAuthKind !== el.authKindInput.value;
    el.secretInput.disabled = officialOAuth;
    el.revealSecretButton.disabled = officialOAuth;
    el.secretInput.required = !officialOAuth && (!editing || changedAuthKind);
    el.oauthLoginButton.disabled = !officialOAuth;
    el.secretFieldHint.textContent = editing && !changedAuthKind
      ? "留空会保留原凭据。"
      : compatible
        ? "新增或切换到兼容 API 时必须填写。"
        : "只填写 Token 本体，无需 Bearer 前缀。";
    updateOAuthSaveState();
  }

  function accountFormPayload() {
    const authKind = el.authKindInput.value;
    const payload = {
      name: el.accountNameInput.value.trim(),
      authKind,
      codexHome: el.codexHomeInput.value.trim(),
      apiProviderName: authKind === "compatible_api" ? el.providerNameInput.value.trim() : "",
      apiBaseUrl: authKind === "compatible_api" ? el.baseUrlInput.value.trim() : "",
      apiModel: authKind === "compatible_api" ? el.modelInput.value.trim() : "",
      apiWireApi: authKind === "compatible_api" ? el.wireApiInput.value : "responses",
    };
    const secret = el.secretInput.value.trim();
    if (secret && authKind === "compatible_api") payload.apiKey = secret;
    return payload;
  }

  function renderOAuthStatus(kind, text) {
    el.oauthLoginStatus.classList.remove("is-pending", "is-verified", "is-error");
    if (kind) el.oauthLoginStatus.classList.add(`is-${kind}`);
    el.oauthLoginStatus.textContent = text;
  }

  function updateOAuthSaveState() {
    const officialOAuth = el.authKindInput.value === "official_oauth";
    el.saveAccountButton.disabled = officialOAuth && !app.oauthVerified;
  }

  function resetOAuthDraftState({ requiresFreshLogin = false, statusText = "尚未登录", statusKind = "" } = {}) {
    const draftId = app.oauthDraftId;
    app.oauthDraftId = null;
    app.oauthVerified = false;
    app.oauthPending = false;
    app.oauthRequiresFreshLogin = requiresFreshLogin;
    app.oauthRequestVersion += 1;
    app.oauthButtonOperation += 1;
    app.pendingOAuthCompletions.clear();
    clearOAuthClipboard();
    if (el.oauthLoginButton) {
      el.oauthLoginButton.textContent = "生成登录链接";
      el.oauthLoginButton.disabled = el.authKindInput.value !== "official_oauth";
    }
    if (el.oauthLoginStatus) renderOAuthStatus(statusKind, statusText);
    if (el.saveAccountButton) updateOAuthSaveState();
    return draftId;
  }

  async function cancelOAuthDraftById(draftId) {
    if (!draftId || typeof bridge.cancelOAuthDraft !== "function") return;
    try { await bridge.cancelOAuthDraft(draftId); } catch { /* the main process also expires abandoned drafts */ }
  }

  function beginOAuthButtonBusy(text) {
    const operation = ++app.oauthButtonOperation;
    const html = el.oauthLoginButton.innerHTML;
    el.oauthLoginButton.disabled = true;
    el.oauthLoginButton.textContent = text;
    return { operation, html };
  }

  function finishOAuthButtonBusy(state) {
    if (!state || state.operation !== app.oauthButtonOperation) return;
    el.oauthLoginButton.innerHTML = state.html;
    el.oauthLoginButton.disabled = el.authKindInput.value !== "official_oauth";
  }

  function isCurrentOAuthRequest(requestVersion) {
    return app.oauthRequestVersion === requestVersion && el.accountModal.open && el.authKindInput.value === "official_oauth";
  }

  async function prepareReusableOAuthDraft() {
    if (!app.editingAccountId || app.oauthRequiresFreshLogin || el.authKindInput.value !== "official_oauth") return;
    const requestVersion = app.oauthRequestVersion;
    const buttonState = beginOAuthButtonBusy("检查登录状态…");
    app.oauthPending = true;
    renderOAuthStatus("pending", "正在检查登录状态…");
    updateOAuthSaveState();
    try {
      const result = await invoke("prepareOAuthDraft", {
        editingId: app.editingAccountId,
        data: accountFormPayload(),
        reuseExisting: true,
      });
      if (!isCurrentOAuthRequest(requestVersion)) {
        await cancelOAuthDraftById(idOf(result?.draftId));
        return;
      }
      app.oauthPending = false;
      if (result?.verified === true) {
        const draftId = idOf(result?.draftId);
        if (!draftId) throw new Error("官方登录状态验证结果无效。");
        app.oauthDraftId = draftId;
        app.oauthVerified = true;
        renderOAuthStatus("verified", "✓ 已登录");
      } else {
        app.oauthDraftId = null;
        app.oauthVerified = false;
        renderOAuthStatus("", "尚未登录");
      }
    } catch (error) {
      if (isCurrentOAuthRequest(requestVersion)) {
        app.oauthPending = false;
        app.oauthVerified = false;
        renderOAuthStatus("error", "登录状态检查失败，请重新登录");
        showToast("无法检查官方登录状态", friendlyError(error), "error");
      }
    } finally {
      finishOAuthButtonBusy(buttonState);
      updateOAuthSaveState();
    }
  }

  async function startOAuthLogin() {
    if (el.authKindInput.value !== "official_oauth") return;
    if (!el.accountForm.reportValidity()) return;

    const previousDraftId = resetOAuthDraftState({ requiresFreshLogin: true });
    const requestVersion = app.oauthRequestVersion;
    const buttonState = beginOAuthButtonBusy("正在生成…");
    app.oauthPending = true;
    renderOAuthStatus("pending", "正在生成登录链接…");
    updateOAuthSaveState();

    try {
      await cancelOAuthDraftById(previousDraftId);
      if (!isCurrentOAuthRequest(requestVersion)) return;
      const result = await invoke("prepareOAuthDraft", {
        editingId: app.editingAccountId,
        data: accountFormPayload(),
        reuseExisting: false,
      });
      if (!isCurrentOAuthRequest(requestVersion)) {
        await cancelOAuthDraftById(idOf(result?.draftId));
        return;
      }

      const draftId = idOf(result?.draftId);
      if (!draftId) throw new Error("官方登录服务没有返回有效的登录草稿。");
      app.oauthDraftId = draftId;
      const authUrl = trustedOfficialOAuthUrl(result?.authUrl);
      await copyOAuthLink(authUrl);
      if (!isCurrentOAuthRequest(requestVersion)) {
        await cancelOAuthDraftById(draftId);
        return;
      }
      if (app.oauthDraftId !== draftId || app.oauthVerified) {
        clearOAuthClipboard();
        return;
      }

      app.oauthPending = true;
      renderOAuthStatus("pending", "等待网页授权…");
      const queuedCompletion = app.pendingOAuthCompletions.get(draftId);
      app.pendingOAuthCompletions.clear();
      if (queuedCompletion) completeOAuthDraft(queuedCompletion);
    } catch (error) {
      if (isCurrentOAuthRequest(requestVersion)) {
        const draftId = resetOAuthDraftState({
          requiresFreshLogin: true,
          statusText: "登录未完成，请重试",
          statusKind: "error",
        });
        await cancelOAuthDraftById(draftId);
        showToast("无法生成官方登录链接", friendlyError(error), "error");
      }
    } finally {
      finishOAuthButtonBusy(buttonState);
      updateOAuthSaveState();
    }
  }

  function completeOAuthDraft(payload) {
    const draftId = idOf(payload?.draftId);
    if (!draftId || draftId !== app.oauthDraftId || !el.accountModal.open || el.authKindInput.value !== "official_oauth") return;
    app.oauthPending = false;
    app.pendingOAuthCompletions.delete(draftId);
    clearOAuthClipboard();
    if (payload?.ok === true) {
      app.oauthVerified = true;
      renderOAuthStatus("verified", "✓ 已登录");
      showToast("官方登录完成", "现在可以保存账号。");
    } else {
      app.oauthDraftId = null;
      app.oauthVerified = false;
      renderOAuthStatus("error", "登录未完成，请重试");
      showToast("官方登录未完成", String(payload?.message || "请重新生成登录链接。"), "error");
    }
    updateOAuthSaveState();
  }

  function trustedOfficialOAuthUrl(value) {
    const raw = String(value || "").trim();
    let url;
    try { url = new URL(raw); } catch { throw new Error("官方登录服务没有返回有效的 HTTPS 登录链接。"); }
    const allowedHost = url.hostname === "auth.openai.com" ||
      (url.hostname === "chatgpt.com" && url.pathname === "/codex/desktop-auth");
    if (url.protocol !== "https:" || !allowedHost || (url.port && url.port !== "443") ||
        url.username || url.password || url.toString().length > 8192) {
      throw new Error("官方登录服务返回了不受信任的登录链接。");
    }
    return url.toString();
  }

  async function saveAccount(event) {
    event.preventDefault();
    const payload = accountFormPayload();
    const authKind = payload.authKind;
    const secret = el.secretInput.value.trim();
    const wasEditing = Boolean(app.editingAccountId);

    if (authKind === "official_oauth" && (!app.oauthVerified || !app.oauthDraftId)) {
      renderOAuthStatus("error", "请先完成官方登录");
      updateOAuthSaveState();
      showToast("官方登录尚未完成", "看到“✓ 已登录”后才能保存账号。", "error");
      return;
    }

    await withButtonBusy(el.saveAccountButton, "正在保存…", async () => {
      if (authKind === "official_oauth") {
        const saved = await invoke("commitOAuthDraft", { draftId: app.oauthDraftId, data: payload });
        const accountId = idOf(saved?.id || app.editingAccountId);
        app.oauthDraftId = null;
        app.oauthVerified = false;
        app.oauthPending = false;
        clearOAuthClipboard();
        if (accountId) app.selectedAccountId = accountId;
        closeDialog(el.accountModal);
        await loadState();
        showToast(wasEditing ? "账号已更新" : "账号已添加", `${payload.name} 已完成官方登录并安全保存。`);
        return;
      }

      const saved = app.editingAccountId
        ? await invoke("updateAccount", app.editingAccountId, payload)
        : await invoke("createAccount", payload);
      const accountId = idOf(saved?.id || app.editingAccountId);
      let loginError = null;
      if (authKind === "access_token" && secret && accountId) {
        try {
          const loginStatus = await invoke("loginAccount", { accountId, accessToken: secret });
          app.loginStatuses[accountId] = normalizeStatus(loginStatus);
        } catch (error) {
          loginError = error;
        }
      }
      el.secretInput.value = "";
      closeDialog(el.accountModal);
      await loadState();
      if (loginError) showToast("账号已保存，但登录未完成", friendlyError(loginError), "error");
      else showToast(wasEditing ? "账号已更新" : "账号已添加", `${payload.name} 已安全保存。`);
    });
  }

  function openLoginModal() {
    const account = selectedAccount();
    if (!account) return;
    if (account.authKind === "official_oauth") {
      openAccountModal(account);
      return;
    }
    const compatible = account.authKind === "compatible_api";
    el.loginDescription.textContent = compatible
      ? `更新“${account.name}”的 API Key，并验证账号配置。`
      : `通过应用内置的官方 Codex CLI 登录“${account.name}”。`;
    el.loginSecretField.classList.remove("is-hidden");
    el.loginSecretLabel.textContent = compatible ? "API Key" : "Access Token";
    el.loginSecretInput.placeholder = compatible ? "粘贴 API Key" : "粘贴 Access Token";
    el.loginSecretInput.value = "";
    el.loginSecretInput.type = "password";
    el.loginSecretInput.disabled = false;
    el.loginSecretInput.required = true;
    el.revealLoginSecretButton.disabled = false;
    el.revealLoginSecretButton.textContent = "显示";
    el.confirmLoginButton.textContent = "安全登录";
    showDialog(el.loginModal);
    requestAnimationFrame(() => el.loginSecretInput.focus());
  }

  async function loginAccount(event) {
    event.preventDefault();
    const accountId = app.selectedAccountId;
    const account = accountById(accountId);
    const accessToken = el.loginSecretInput.value.trim();
    if (!accountId || !account || account.authKind === "official_oauth" || !accessToken) return;
    const compatible = account.authKind === "compatible_api";
    await withButtonBusy(el.confirmLoginButton, compatible ? "正在更新…" : "正在登录…", async () => {
      const result = compatible
        ? await invoke("updateAccount", accountId, accountUpdatePayload(account, { apiKey: accessToken }))
        : await invoke("loginAccount", { accountId, accessToken });
      el.loginSecretInput.value = "";
      closeDialog(el.loginModal);
      app.loginStatuses[accountId] = compatible
        ? { loggedIn: true, label: "API Key 已配置" }
        : normalizeStatus(result);
      renderAccountList();
      renderAccountDetail();
      renderCurrentCard();
      renderStatusAccounts();
      showToast(compatible ? "API Key 已更新" : "登录完成", result?.message || result?.text || "账号凭据已更新。", result?.success === false || result?.ok === false ? "error" : "success");
    });
  }

  async function checkLogin(accountId, notify = false) {
    if (!accountId) return;
    app.loginStatuses[accountId] = { checking: true, label: "检查中" };
    renderAccountDetail();
    renderStatusAccounts();
    try {
      const result = await invoke("getLoginStatus", accountId);
      app.loginStatuses[accountId] = normalizeStatus(result);
      if (notify) {
        const succeeded = Boolean(result?.loggedIn || result?.authenticated || result?.ok || result === true);
        const compatible = compatibleAccount(accountId);
        showToast(
          compatible && succeeded ? "本地 API Key 已配置" : succeeded ? "登录有效" : compatible ? "本地尚未配置 API Key" : "尚未登录",
          result?.message || result?.text || statusFor(accountId).label,
          result?.error || result?.ok === false ? "error" : "success",
        );
      }
    } catch (error) {
      app.loginStatuses[accountId] = { loggedIn: false, error: true, label: "检查失败" };
      if (notify) showToast("检查失败", friendlyError(error), "error");
    } finally {
      renderAccountList();
      renderAccountDetail();
      renderCurrentCard();
      renderStatusAccounts();
    }
  }

  async function setSelectedAsCurrent() {
    const account = selectedAccount();
    if (!account) return;
    await withButtonBusy(el.setCurrentButton, "切换中…", async () => {
      const result = await invoke("setCurrentAccount", account.id);
      invalidateStateUsageAndQuotaRequests();
      app.currentAccountId = account.id;
      app.accounts.forEach((item) => { item.isCurrent = item.id === account.id; });
      renderAccountList();
      renderAccountDetail();
      renderCurrentCard();
      showToast(
        result?.desktop?.handedOff ? "账号切换未确认" : "已切换账号",
        [
          `${account.name} · ChatGPT（Codex）已使用此账号启动`,
          result?.desktop?.metadataWarning,
        ].filter(Boolean).join("；"),
      );
    });
  }

  async function launchAccount(accountId) {
    const account = accountById(accountId);
    if (!account) return;
    const button = accountId === app.currentAccountId ? el.launchCurrentButton : el.detailLaunchButton;
    await withButtonBusy(button, "正在启动…", async () => {
      await invoke("launchTerminal", accountId);
      app.currentAccountId = account.id;
      app.accounts.forEach((item) => { item.isCurrent = item.id === account.id; });
      renderAccountList();
      renderAccountDetail();
      renderCurrentCard();
      showToast("终端已启动", `Codex 正在使用“${account.name}”的独立环境。`);
    });
  }

  async function launchAccountInCodexApp(accountId) {
    const account = accountById(accountId);
    if (!account) return;
    await withButtonBusy(el.detailAppLaunchButton, "正在启动…", async () => {
      const result = await invoke("launchCodexApp", account.id);
      app.currentAccountId = account.id;
      app.accounts.forEach((item) => { item.isCurrent = item.id === account.id; });
      renderAccountList();
      renderAccountDetail();
      renderCurrentCard();
      if (result?.handedOff) {
        showToast("启动进程已完成", `请求可能已交由正在运行的桌面应用处理，账号为“${account.name}”。`);
      } else if (result?.rendererVerified) {
        showToast("ChatGPT（Codex）已启动并验证", `已按“${account.name}”的独立环境打开。`);
      } else {
        showToast("桌面进程已启动", `已按“${account.name}”的独立环境发起启动；若页面仍空白，请重新安装官方桌面应用。`);
      }
      if (result?.theme?.ok === false) {
        showToast("Codex App 已启动，但主题未应用", result.theme.reason || result.theme.message || "请在 Codex 主题页重试。", "error");
      }
      if (result?.metadataWarning) showToast("账号已切换", result.metadataWarning);
    });
  }

  function clearDeletedAccountDerivedState(account) {
    if (!account) return;
    const deletingSelectedThread = threadBelongsToAccount(app.selectedThread, account);
    app.accounts = app.accounts.filter((item) => item.id !== account.id);
    if (app.currentAccountId === account.id) app.currentAccountId = null;
    if (app.selectedAccountId === account.id) app.selectedAccountId = null;
    delete app.loginStatuses[account.id];

    app.usageReport = restrictUsageReportToActiveAccounts(app.usageReport);
    app.quotaReport = restrictQuotaReportToActiveAccounts(app.quotaReport);
    app.historyThreads = app.historyThreads.filter((thread) => !threadBelongsToAccount(thread, account));
    if (app.usageAccountFilter === account.id) app.usageAccountFilter = "all";
    if (app.quotaAccountFilter === account.id) app.quotaAccountFilter = "all";
    if (app.historyAccountFilter === account.id) app.historyAccountFilter = "all";
    app.usage = selectUsageScope(app.usageReport, app.usageAccountFilter);
    app.quotaLoaded = false;
    app.historyLoaded = false;
    if (deletingSelectedThread || (app.selectedThreadId && !app.historyThreads.some((thread) => thread.id === app.selectedThreadId))) {
      clearSelectedThread();
    }
  }

  async function deleteSelectedAccount() {
    const account = selectedAccount();
    if (!account) return;
    const confirmed = await confirmAction(
      "永久删除此账号？",
      `将永久删除“${account.name}”及其本地凭据、配置、桌面会话与聊天记录：${account.codexHome}。此操作不可撤销。`,
      "继续删除",
    );
    if (!confirmed) return;
    const finalConfirmed = await confirmAction(
      "再次确认永久删除",
      `账号：${account.name}\n凭据目录：${account.codexHome}`,
      "永久删除",
    );
    if (!finalConfirmed) return;
    await withButtonBusy(el.deleteAccountButton, "删除中…", async () => {
      invalidateAllAccountDerivedRequests();
      const result = await invoke("deleteAccount", account.id);
      invalidateAllAccountDerivedRequests();
      clearDeletedAccountDerivedState(account);
      updateAccountFilterOptions();
      renderAll();
      await loadState();
      await Promise.all([
        loadUsage(false),
        loadQuota(false),
        loadHistory(false),
      ]);
      if (result?.cleanupWarning) {
        showToast("账号已删除，清理待完成", result.cleanupWarning, "error");
      } else {
        showToast("账号及其本地数据已永久删除", account.name);
      }
    });
  }

  async function cycleTheme() {
    const themes = ["system", "light", "dark"];
    const next = themes[(themes.indexOf(app.theme) + 1) % themes.length];
    app.theme = next;
    applyTheme(next);
    try {
      await invoke("setTheme", next);
    } catch (error) {
      showToast("外观未能保存", friendlyError(error), "error");
    }
  }

  function applyTheme(theme) {
    const normalized = normalizeTheme(theme);
    const resolved = normalized === "system" ? (systemTheme.matches ? "dark" : "light") : normalized;
    document.documentElement.dataset.theme = resolved;
    document.documentElement.dataset.themePreference = normalized;
    el.themeLabel && (el.themeLabel.textContent = normalized === "system" ? "跟随系统" : normalized === "dark" ? "深色外观" : "浅色外观");
  }

  function updateSegmentedControl(container, key, value, aria = false) {
    container.querySelectorAll(`button[data-${key}]`).forEach((button) => {
      const active = button.dataset[key] === value;
      button.classList.toggle("is-active", active);
      if (aria) button.setAttribute("aria-selected", String(active));
    });
  }

  function setUsageLoading(loading) {
    el.usagePage.style.opacity = loading ? ".64" : "";
    el.rangeControl.querySelectorAll("button").forEach((button) => { button.disabled = loading; });
  }

  function showDialog(dialog) {
    if (!dialog.open) dialog.showModal();
  }

  function closeDialog(dialog) {
    if (dialog?.open) dialog.close();
    if (dialog === el.accountModal) {
      el.secretInput.value = "";
      const draftId = resetOAuthDraftState({ requiresFreshLogin: false });
      cancelOAuthDraftById(draftId);
    }
    if (dialog === el.loginModal) el.loginSecretInput.value = "";
  }

  function isDialogOpen() {
    return Boolean(document.querySelector("dialog[open]"));
  }

  function confirmAction(title, message, actionLabel) {
    el.confirmTitle.textContent = title;
    el.confirmMessage.textContent = message;
    el.confirmActionButton.textContent = actionLabel;
    el.confirmModal.returnValue = "";
    showDialog(el.confirmModal);
    return new Promise((resolve) => {
      el.confirmModal.addEventListener("close", () => resolve(el.confirmModal.returnValue === "confirm"), { once: true });
    });
  }

  function toggleSecret(input, button) {
    const showing = input.type === "text";
    input.type = showing ? "password" : "text";
    button.textContent = showing ? "显示" : "隐藏";
  }

  async function copyOAuthLink(value) {
    const valueText = String(value || "").trim();
    if (!valueText) throw new Error("官方登录链接为空。");
    await copyText(valueText);
    app.copiedOAuthValues.add(valueText);
    showToast("登录链接已复制", "请粘贴到浏览器完成官方登录。");
  }

  async function copyText(value) {
    const valueText = String(value || "");
    if (!valueText) throw new Error("没有可复制的内容。");
    let copied = false;
    try {
      if (typeof bridge.writeClipboardText === "function") await bridge.writeClipboardText(valueText);
      else await navigator.clipboard.writeText(valueText);
      copied = true;
    } catch {
      const input = document.createElement("textarea");
      input.value = valueText;
      input.setAttribute("readonly", "");
      input.style.position = "fixed";
      input.style.opacity = "0";
      document.body.append(input);
      input.select();
      copied = document.execCommand("copy");
      input.remove();
    }
    if (!copied) throw new Error("无法复制内容，请重试。");
  }

  function clearOAuthClipboard() {
    const copiedValues = [...app.copiedOAuthValues];
    if (copiedValues.length && typeof bridge.clearClipboardIfMatches === "function") {
      try { bridge.clearClipboardIfMatches(copiedValues); } catch { /* clipboard may be unavailable */ }
    }
    app.copiedOAuthValues.clear();
  }

  async function withButtonBusy(button, busyText, action) {
    if (!button || button.disabled) return;
    const oldHtml = button.innerHTML;
    button.disabled = true;
    button.textContent = busyText;
    try {
      return await action();
    } catch (error) {
      showToast("操作未完成", friendlyError(error), "error");
      return undefined;
    } finally {
      button.disabled = false;
      button.innerHTML = oldHtml;
    }
  }

  async function invoke(method, ...args) {
    if (typeof bridge[method] !== "function") throw new Error(`应用服务尚未提供 ${method} 接口。`);
    return bridge[method](...args);
  }

  function showToast(title, message = "", type = "success") {
    const toast = document.createElement("div");
    toast.className = `toast ${type === "error" ? "error" : ""}`;
    toast.innerHTML = `<div class="toast-icon">${type === "error" ? "!" : "✓"}</div><div><strong>${escapeHtml(title)}</strong>${message ? `<span>${escapeHtml(message)}</span>` : ""}</div>`;
    el.toastStack.append(toast);
    window.setTimeout(() => {
      toast.classList.add("is-leaving");
      window.setTimeout(() => toast.remove(), 230);
    }, type === "error" ? 5600 : 3300);
  }

  function normalizeAccount(raw, index) {
    return {
      id: idOf(raw?.id) || idOf(raw?.name) || `account-${index}`,
      name: String(raw?.name || "未命名账号"),
      codexHome: String(raw?.codexHome || ""),
      authKind: ["compatible_api", "official_oauth"].includes(raw?.authKind) ? raw.authKind : "access_token",
      apiProviderName: String(raw?.apiProviderName || ""),
      apiBaseUrl: String(raw?.apiBaseUrl || ""),
      apiModel: String(raw?.apiModel || ""),
      apiWireApi: String(raw?.apiWireApi || "responses"),
      lastUsedAt: raw?.lastUsedAt || raw?.lastUsed || null,
      isCurrent: Boolean(raw?.isCurrent),
      loginStatus: raw?.loginStatus ?? null,
    };
  }

  function normalizeUsage(raw) {
    const models = Array.isArray(raw?.models) ? raw.models.map((model) => ({
      model: String(model?.model || "未知模型"),
      tokens: numeric(model?.tokens),
      cost: model?.cost === null || model?.apiEquivalentUsd === null ? null : numeric(model?.cost ?? model?.apiEquivalentUsd),
      costKnown: model?.costKnown !== false && model?.cost !== null && model?.apiEquivalentUsd !== null,
      color: String(model?.color || ""),
    })) : [];
    const totalTokens = numeric(raw?.totalTokens) || models.reduce((sum, model) => sum + model.tokens, 0);
    const apiEquivalentComplete = raw?.apiEquivalentComplete !== false && raw?.apiEquivalentUsd !== null;
    const apiEquivalentUsd = apiEquivalentComplete
      ? numeric(raw?.apiEquivalentUsd) || models.reduce((sum, model) => sum + (model.costKnown ? model.cost : 0), 0)
      : null;
    const rawTimeline = raw?.timeline || raw?.buckets || raw?.series || [];
    return {
      range: String(raw?.range || app.range),
      totalTokens,
      inputTokens: numeric(raw?.inputTokens),
      cachedInputTokens: numeric(raw?.cachedInputTokens),
      cacheWriteTokens: numeric(raw?.cacheWriteTokens),
      outputTokens: numeric(raw?.outputTokens),
      apiEquivalentUsd,
      knownApiEquivalentUsd: numeric(raw?.knownApiEquivalentUsd),
      apiEquivalentComplete,
      models,
      timeline: Array.isArray(rawTimeline) ? rawTimeline.map((item, index) => ({
        label: String(item?.label || formatShortDate(item?.date || item?.timestamp) || index + 1),
        totalTokens: numeric(item?.totalTokens ?? item?.tokens ?? item?.valueTokens),
        apiEquivalentUsd: numeric(item?.apiEquivalentUsd ?? item?.cost ?? item?.valueUsd),
      })) : [],
    };
  }

  function normalizeUsageReport(raw) {
    const aggregate = normalizeUsage(raw?.aggregate || raw || {});
    const perAccount = Array.isArray(raw?.perAccount) ? raw.perAccount.map((item, index) => ({
      ...normalizeUsage(item),
      accountId: idOf(item?.accountId || item?.id) || `usage-account-${index}`,
      accountName: String(item?.accountName || item?.name || "未命名账号"),
      codexHome: String(item?.codexHome || ""),
    })) : [];
    return {
      aggregate,
      perAccount,
      unattributed: raw?.unattributed ? normalizeUsage(raw.unattributed) : emptyUsage(),
      generatedAt: raw?.generatedAt || null,
    };
  }

  function emptyUsage() {
    return { range: "30d", totalTokens: 0, inputTokens: 0, cachedInputTokens: 0, cacheWriteTokens: 0, outputTokens: 0, apiEquivalentUsd: 0, knownApiEquivalentUsd: 0, apiEquivalentComplete: true, models: [], timeline: [] };
  }

  function emptyUsageReport() {
    return { aggregate: emptyUsage(), perAccount: [], unattributed: emptyUsage(), generatedAt: null };
  }

  function normalizeStatuses(raw) {
    if (!raw) return {};
    if (Array.isArray(raw)) return Object.fromEntries(raw.filter(Boolean).map((item) => [idOf(item.accountId || item.id), normalizeStatus(item)]));
    return Object.fromEntries(Object.entries(raw).map(([id, value]) => [idOf(id), normalizeStatus(value)]));
  }

  function normalizeStatus(raw) {
    if (raw === true) return { loggedIn: true, label: "已登录" };
    if (raw === false || raw == null) return { loggedIn: false, label: "未登录" };
    if (typeof raw === "string") {
      const normalized = raw.toLowerCase();
      const loggedIn = ["logged_in", "logged-in", "authenticated", "online", "ok", "已登录"].includes(normalized);
      return { loggedIn, label: loggedIn ? "已登录" : raw };
    }
    const state = String(raw.status || "").toLowerCase();
    const loggedIn = Boolean(raw.loggedIn ?? raw.authenticated ?? raw.success ?? raw.ok ?? ["logged_in", "authenticated", "online", "ok"].includes(state));
    return {
      loggedIn,
      checking: Boolean(raw.checking),
      error: Boolean(raw.error) || raw.ok === false || state === "error",
      label: String(raw.label || raw.message || raw.text || (loggedIn ? "已登录" : raw.error ? "检查失败" : "未登录")),
    };
  }

  function statusFor(accountId) {
    return app.loginStatuses[idOf(accountId)] || { loggedIn: false, label: "未检查" };
  }

  function accountById(accountId) {
    const id = idOf(accountId);
    return app.accounts.find((account) => account.id === id) || null;
  }

  function selectedAccount() {
    return accountById(app.selectedAccountId);
  }

  function compatibleAccount(accountId) {
    return accountById(accountId)?.authKind === "compatible_api";
  }

  function accountUpdatePayload(account, extra = {}) {
    return {
      name: account.name,
      authKind: account.authKind,
      codexHome: account.codexHome,
      apiProviderName: account.apiProviderName,
      apiBaseUrl: account.apiBaseUrl,
      apiModel: account.apiModel,
      apiWireApi: account.apiWireApi,
      ...extra,
    };
  }

  function authLabel(account) {
    if (!account) return "";
    if (account.authKind === "compatible_api") return account.apiProviderName ? `兼容 API · ${account.apiProviderName}` : "兼容 API";
    if (account.authKind === "official_oauth") return "通过 ChatGPT 登录（官方）";
    return "Access Token";
  }

  function usageValueLabel() {
    return "金额";
  }

  function usageScopeLabel() {
    if (app.usageAccountFilter === "all") return "全部账号";
    if (app.usageAccountFilter === "unattributed") return "未归属会话";
    return accountById(app.usageAccountFilter)?.name || "所选账号";
  }

  function initialOf(name) {
    const value = String(name || "C").trim();
    return Array.from(value)[0]?.toLocaleUpperCase("zh-CN") || "C";
  }

  function avatarColors(seed) {
    const hash = Array.from(String(seed)).reduce((value, char) => ((value << 5) - value + char.codePointAt(0)) | 0, 0);
    const index = Math.abs(hash) % palette.length;
    return [palette[index], palette[(index + 1) % palette.length]];
  }

  function normalizeTheme(value) {
    const theme = String(value || "system").toLowerCase();
    return ["system", "light", "dark"].includes(theme) ? theme : "system";
  }

  function numeric(value) {
    const number = Number(value);
    return Number.isFinite(number) && number > 0 ? number : 0;
  }

  function percentOrNull(value) {
    if (value == null || value === "") return null;
    const number = Number(value);
    return Number.isFinite(number) ? Math.min(100, Math.max(0, number)) : null;
  }

  function optionalPort(value) {
    if (value == null || value === "") return null;
    const port = Number(value);
    return Number.isInteger(port) && port > 0 && port <= 65535 && port !== 8317 ? port : null;
  }

  function idOf(value) {
    return value == null ? "" : String(value);
  }

  function formatTokens(value) {
    const amount = numeric(value);
    return new Intl.NumberFormat("zh-CN", { notation: amount >= 10000 ? "compact" : "standard", maximumFractionDigits: amount >= 10000 ? 1 : 0 }).format(amount);
  }

  function formatFullTokens(value) {
    return `${new Intl.NumberFormat("zh-CN", { maximumFractionDigits: 0 }).format(numeric(value))} Token`;
  }

  function formatUsd(value) {
    const amount = numeric(value);
    const digits = amount > 0 && amount < .01 ? 4 : 2;
    return `$${amount.toLocaleString("en-US", { minimumFractionDigits: digits, maximumFractionDigits: digits })}`;
  }

  function compactUsd(value) {
    const amount = numeric(value);
    if (amount < 1000) return `$${amount.toFixed(amount < 10 ? 1 : 0)}`;
    return `$${new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 }).format(amount)}`;
  }

  function formatPercent(value) {
    const number = Math.max(0, Number(value) || 0);
    return `${number.toFixed(Number.isInteger(number) ? 0 : 1)}%`;
  }

  function formatDate(value) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "尚未使用";
    return new Intl.DateTimeFormat("zh-CN", { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }).format(date);
  }

  function formatDateTime(value) {
    if (!value) return "时间未知";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "时间未知";
    return new Intl.DateTimeFormat("zh-CN", { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }).format(date);
  }

  function formatShortDate(value) {
    if (!value) return "";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "";
    return new Intl.DateTimeFormat("zh-CN", { month: "numeric", day: "numeric" }).format(date);
  }

  function niceMax(value) {
    if (value <= 0) return 1;
    const magnitude = 10 ** Math.floor(Math.log10(value));
    const normalized = value / magnitude;
    const nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
    return nice * magnitude;
  }

  function smoothPath(points) {
    if (points.length < 2) return "";
    let path = `M ${points[0].x} ${points[0].y}`;
    for (let index = 0; index < points.length - 1; index++) {
      const current = points[index];
      const next = points[index + 1];
      const dx = (next.x - current.x) * .42;
      path += ` C ${current.x + dx} ${current.y}, ${next.x - dx} ${next.y}, ${next.x} ${next.y}`;
    }
    return path;
  }

  function svgNode(tag, attributes = {}) {
    const node = document.createElementNS("http://www.w3.org/2000/svg", tag);
    Object.entries(attributes).forEach(([key, value]) => node.setAttribute(key, String(value)));
    return node;
  }

  function uniqueIndexes(values) {
    return [...new Set(values)];
  }

  function validColor(value) {
    const color = String(value || "").trim();
    return /^#[0-9a-f]{3,8}$/i.test(color) ? color : "";
  }

  function importResultMessage(result) {
    const count = Number(result?.importedCount ?? result?.imported ?? result?.count);
    if (Number.isFinite(count)) return `已导入 ${count} 个账号。`;
    return result?.message || "账号列表已刷新。";
  }

  function friendlyError(error) {
    if (!error) return "发生未知错误。";
    const message = String(error.message || error);
    if (/\bENOENT\b|codex cli|command not found|not found.*codex/i.test(message)) {
      return "应用内置的 Codex CLI 缺失或损坏。请重新安装本应用后再试。";
    }
    return message.replace(/^Error:\s*/i, "").split("\n")[0].slice(0, 220);
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[char]));
  }

  function escapeAttr(value) {
    return escapeHtml(value).replace(/`/g, "&#96;");
  }
})();
