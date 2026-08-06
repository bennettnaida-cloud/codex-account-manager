const fs = require('node:fs');
const crypto = require('node:crypto');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { app, BrowserWindow, dialog, ipcMain, Menu, nativeTheme, session } = require('electron');
const {
  createAccountActivationQueue,
  launchThenCommitAccount,
} = require('./services/account-activation');
const { AccountStore } = require('./services/account-store');
const { CodexCliService } = require('./services/codex-cli');
const { getUsageStats } = require('./services/usage-stats');
const { getQuotaStats, readRateLimitsViaAppServer } = require('./services/quota-service');
const { HistoryService } = require('./services/history-service');
const { LocalPatGateway, createPatGatewayUpstreamPreparer } = require('./services/local-pat-gateway');
const { DAEMON_ARGUMENT, PatGatewayController } = require('./services/pat-gateway-controller');
const { loadGatewaySecret, loadOrCreateGatewaySecret } = require('./services/gateway-secret');
const { ThemeService, buildCustomThemeCss, normalizeCustomTheme } = require('./services/theme-service');
const { checkAndSaveOfficialCatalog } = require('./services/model-catalog');
const {
  checkForUpdate,
  downloadAndScheduleInstall,
  UPDATE_CHECK_STATUS,
} = require('./services/update-service');
const {
  applyProxyEnvironment,
  detectLocalProxy,
  formatProxyUrl,
  normalizeProjectPath,
  normalizeProxySettings,
  openPath,
  validPort,
} = require('./services/system-service');

let mainWindow = null;
let store = null;
let cli = null;
let themeService = null;
let patGateway = null;
let patGatewayNetworkSession = null;
const loginStatuses = new Map();
const oauthDrafts = new Map();
const oauthCleanupTasks = new Set();
const codexAppSessions = new Map();
const retiringAccountIds = new Set();
const activeQuotaReads = new Set();
const runAccountActivation = createAccountActivationQueue();
const OAUTH_DRAFT_TTL_MS = 15 * 60 * 1000;
const SHARED_HISTORY_ACCOUNT_ID = 'shared';
let oauthShutdownPromise = null;
let oauthShutdownComplete = false;
let updateCheckInFlight = null;
let automaticUpdateCheckStarted = false;
let pendingUpdate = null;
const isPatGatewayDaemon = process.argv.includes(DAEMON_ARGUMENT);

app.setName('Codex Account Manager');

function publicAccount(account, currentAccountId) {
  return {
    id: account.id,
    name: account.name,
    codexHome: account.codexHome,
    authKind: account.authKind,
    apiProviderName: account.apiProviderName,
    apiBaseUrl: account.apiBaseUrl,
    apiModel: account.apiModel,
    apiWireApi: account.apiWireApi,
    lastUsedAt: account.lastUsedAt || null,
    quotaLimitType: account.quotaLimitType || 'unknown',
    quotaPrimaryWindowMinutes: account.quotaPrimaryWindowMinutes ?? null,
    quotaSecondaryWindowMinutes: account.quotaSecondaryWindowMinutes ?? null,
    isCurrent: account.id === currentAccountId,
    loginStatus: loginStatuses.get(account.id) || null,
  };
}

function publicSettings(settings = store.loadSettings()) {
  return {
    projectRoot: app.getPath('userData'),
    projectPath: settings.projectPath,
    launchDirectory: settings.projectPath,
    proxyAutoDetect: settings.proxyAutoDetect,
    proxyScheme: settings.proxyScheme,
    proxyAddress: settings.proxyAddress,
    proxyHost: settings.proxyAddress,
    proxyPort: settings.proxyPort,
    detectedProxyPort: settings.detectedProxyPort,
    proxyUrl: formatProxyUrl(settings),
    usageRefreshSeconds: settings.usageRefreshSeconds,
    quotaRefreshSeconds: settings.quotaRefreshSeconds,
    codexAppPath: settings.codexAppPath,
    codexThemeId: settings.codexThemeId,
    customCodexTheme: settings.customCodexTheme,
  };
}

function buildState() {
  const settings = store.loadSettings();
  const accounts = store.loadAccounts().map((account) => publicAccount(account, settings.currentAccountId));
  return {
    accounts,
    currentAccountId: settings.currentAccountId,
    theme: settings.theme,
    settings: publicSettings(settings),
    loginStatuses: Object.fromEntries(loginStatuses),
    appVersion: app.getVersion(),
    platform: process.platform,
    architecture: process.arch,
  };
}

function notifyStateChanged() {
  if (mainWindow && !mainWindow.isDestroyed()) mainWindow.webContents.send('state:changed', buildState());
}

function findAccount(id) {
  const account = store.loadAccounts().find((item) => item.id === id);
  if (!account) throw new Error('账号不存在。');
  return account;
}

function handleIpc(channel, listener) {
  ipcMain.handle(channel, (event, ...args) => {
    const expectedUrl = mainWindow?.webContents.getURL();
    if (!mainWindow || event.sender !== mainWindow.webContents || event.senderFrame?.url !== expectedUrl) {
      throw new Error('拒绝来自非应用页面的请求。');
    }
    return listener(event, ...args);
  });
}

function sendOAuthDraftResult(payload) {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send('account:oauth-draft-completed', payload);
  }
}

function cancelOAuthDraftNow(draftId) {
  const normalizedId = String(draftId || '');
  const state = oauthDrafts.get(normalizedId);
  if (!state) return Promise.resolve(false);
  if (state.cancelTask) return state.cancelTask;
  if (state.timer) clearTimeout(state.timer);
  state.timer = null;
  const task = (async () => {
    if (state.session) await state.session.cancel();
    if (state.draft?.pendingCodexHome) {
      store.cleanupOfficialOAuthDraft(state.draft.pendingCodexHome);
      if (fs.existsSync(state.draft.pendingCodexHome)) {
        throw new Error('官方登录临时凭据未能清理，本次操作已取消。请重启管理器后重试。');
      }
    }
    if (oauthDrafts.get(normalizedId) === state) oauthDrafts.delete(normalizedId);
    return true;
  })();
  state.cancelTask = task;
  task.catch(() => {
    if (oauthDrafts.get(normalizedId) === state && state.cancelTask === task) state.cancelTask = null;
  });
  return task;
}

function cancelOAuthDraft(draftId) {
  const task = cancelOAuthDraftNow(draftId);
  oauthCleanupTasks.add(task);
  task.then(
    () => oauthCleanupTasks.delete(task),
    () => oauthCleanupTasks.delete(task),
  );
  return task;
}

function expireOAuthDraftLater(draftId) {
  const timer = setTimeout(() => {
    cancelOAuthDraft(draftId).catch(() => {});
  }, OAUTH_DRAFT_TTL_MS);
  timer.unref();
  return timer;
}

async function cancelOAuthDraftsBeforeAccountDeletion() {
  const draftIds = [...oauthDrafts.keys()];
  await Promise.all(draftIds.map((draftId) => cancelOAuthDraft(draftId)));
}

function assertOfficialOAuthData(data) {
  if (!data || data.authKind !== 'official_oauth') {
    throw new Error('登录草稿必须使用“通过 ChatGPT 登录（官方）”。');
  }
}

function normalizeCheckedStatus(account, value, error = null) {
  const checkedAt = new Date().toISOString();
  if (error) {
    return {
      ok: false,
      loggedIn: false,
      error: true,
      badge: 'FAILED',
      text: error.message || '状态检查失败。',
      checkedAt,
    };
  }
  const status = { ...(value || {}), checkedAt };
  if (account.authKind === 'compatible_api') {
    const configured = status.ok === true;
    return {
      ...status,
      ok: configured,
      loggedIn: configured,
      configured,
      configuredOnly: true,
      authenticated: null,
      text: configured
        ? '本地 API Key 已配置（未验证服务端登录）'
        : '本地尚未配置 API Key',
    };
  }
  return { ...status, loggedIn: status.ok === true };
}

async function checkAccountStatus(account) {
  try {
    const status = normalizeCheckedStatus(account, await cli.status(account));
    loginStatuses.set(account.id, status);
    return status;
  } catch (error) {
    const status = normalizeCheckedStatus(account, null, error);
    loginStatuses.set(account.id, status);
    return status;
  }
}

async function checkAllAccountStatuses() {
  const accounts = store.loadAccounts();
  for (const account of accounts) {
    loginStatuses.set(account.id, {
      ...(loginStatuses.get(account.id) || {}),
      checking: true,
      text: '正在检查…',
    });
  }
  notifyStateChanged();
  let cursor = 0;
  const workers = Array.from({ length: Math.min(3, accounts.length) }, async () => {
    while (cursor < accounts.length) {
      const index = cursor;
      cursor += 1;
      await checkAccountStatus(accounts[index]);
    }
  });
  await Promise.all(workers);
  notifyStateChanged();
  const statuses = Object.fromEntries(accounts.map((account) => [account.id, loginStatuses.get(account.id)]));
  return {
    ok: true,
    checkedAt: new Date().toISOString(),
    statuses,
    accounts: accounts.map((account) => ({ accountId: account.id, ...statuses[account.id] })),
  };
}

function interval(value, minimum, maximum, fallback) {
  const number = Number(value);
  return Number.isInteger(number) && number >= minimum && number <= maximum ? number : fallback;
}

function saveManagerSettings(input = {}) {
  const current = store.loadSettings();
  const launchDirectory = input.launchDirectory ?? input.projectPath;
  const projectPath = launchDirectory === undefined || String(launchDirectory).trim() === ''
    ? current.projectPath
    : normalizeProjectPath(launchDirectory);
  const rawPort = input.proxyPort ?? input.port;
  if (rawPort !== undefined && rawPort !== null && String(rawPort).trim() !== '' && !validPort(rawPort)) {
    if (Number(rawPort) === 8317) throw new Error('8317 是保留端口，不能作为代理端口。');
    throw new Error('代理端口必须是 1 到 65535 之间的有效端口。');
  }
  const proxy = normalizeProxySettings({
    ...current,
    proxyAutoDetect: input.proxyAutoDetect ?? input.autoDetect ?? current.proxyAutoDetect,
    proxyScheme: input.proxyScheme ?? input.scheme ?? current.proxyScheme,
    proxyAddress: input.proxyAddress ?? input.proxyHost ?? input.address ?? current.proxyAddress,
    proxyPort: rawPort === undefined
      ? current.proxyPort
      : (String(rawPort ?? '').trim() ? rawPort : null),
    detectedProxyPort: input.detectedProxyPort ?? current.detectedProxyPort,
  });
  let codexAppPath = current.codexAppPath;
  if (Object.prototype.hasOwnProperty.call(input, 'codexAppPath')) {
    const requestedAppPath = String(input.codexAppPath || '').trim();
    codexAppPath = requestedAppPath
      ? cli.resolveCodexApplication(requestedAppPath).appPath
      : null;
  }
  const next = {
    ...current,
    ...proxy,
    projectPath,
    codexAppPath,
    usageRefreshSeconds: interval(input.usageRefreshSeconds, 2, 60, current.usageRefreshSeconds),
    quotaRefreshSeconds: interval(input.quotaRefreshSeconds, 5, 300, current.quotaRefreshSeconds),
  };
  store.saveSettings(next);
  notifyStateChanged();
  return publicSettings(next);
}

async function detectAndSaveLocalProxy(payload = {}) {
  const settings = store.loadSettings();
  const requestedPort = payload.preferredPort ?? payload.proxyPort ?? settings.proxyPort;
  if (requestedPort !== null && requestedPort !== undefined && String(requestedPort).trim() && !validPort(requestedPort)) {
    if (Number(requestedPort) === 8317) throw new Error('8317 是保留端口，不会参与代理检测。');
    throw new Error('代理端口无效。');
  }
  const result = await detectLocalProxy({ preferredPort: validPort(requestedPort) });
  if (!result.found) return { ...result, settings: publicSettings(settings) };
  const next = {
    ...store.loadSettings(),
    proxyScheme: result.scheme,
    proxyAddress: '127.0.0.1',
    proxyPort: result.port,
    detectedProxyPort: result.port,
  };
  store.saveSettings(next);
  notifyStateChanged();
  return { ...result, settings: publicSettings(next) };
}

function historyScopes(requestedAccountId = 'all') {
  const accounts = store.loadAccounts();
  const scopes = [{
    accountId: SHARED_HISTORY_ACCOUNT_ID,
    accountName: '共享 Codex 记录',
    codexHome: path.join(os.homedir(), '.codex'),
  }, ...accounts.map((account) => ({
    accountId: account.id,
    accountName: account.name,
    codexHome: account.codexHome,
  }))];
  const requested = String(requestedAccountId || 'all');
  const filtered = requested === 'all'
    ? scopes
    : scopes.filter((scope) => scope.accountId === requested);
  if (requested !== 'all' && filtered.length === 0) throw new Error('聊天记录所属账号不存在。');
  const seen = new Set();
  return filtered.filter((scope) => {
    const key = path.resolve(scope.codexHome);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function createHistoryService(scopes) {
  const allowedCodexHomes = scopes.map((scope) => scope.codexHome);
  return new HistoryService({
    defaultCodexHome: path.join(os.homedir(), '.codex'),
    allowedCodexHomes,
  });
}

async function listHistory(input = {}) {
  let scopes = historyScopes(input.accountId || 'all');
  if (input.codexHome) {
    const requestedHome = path.resolve(String(input.codexHome));
    scopes = historyScopes('all').filter((scope) => path.resolve(scope.codexHome) === requestedHome);
    if (scopes.length === 0) throw new Error('拒绝访问未授权的 CODEX_HOME。');
  }
  const service = createHistoryService(scopes);
  const archiveFilter = input.includeArchived === false
    ? 'active'
    : ['active', 'archived'].includes(input.archiveFilter)
    ? input.archiveFilter
    : 'all';
  const requestedLimit = interval(input.limit, 1, 2_000, 500);
  const reports = await Promise.all(scopes.map(async (scope) => ({
    scope,
    report: await service.listThreads({
      codexHome: scope.codexHome,
      query: input.query,
      includeArchived: archiveFilter !== 'active',
      limit: requestedLimit,
    }),
  })));
  const threads = reports.flatMap(({ scope, report }) => report.threads
    .filter((thread) => archiveFilter !== 'archived' || thread.archived)
    .map((thread) => ({ ...thread, ...scope })));
  threads.sort((left, right) => (Date.parse(right.updatedAt) || 0) - (Date.parse(left.updatedAt) || 0));
  return {
    threads: threads.slice(0, requestedLimit),
    scannedFiles: reports.reduce((sum, item) => sum + item.report.scannedFiles, 0),
    ignoredFiles: reports.reduce((sum, item) => sum + item.report.ignoredFiles, 0),
    truncated: threads.length > requestedLimit || reports.some((item) => item.report.truncated),
  };
}

function historyScopeForInput(input = {}) {
  const requestedId = String(input.accountId || '');
  const scopes = historyScopes('all');
  const scope = scopes.find((item) => item.accountId === requestedId) ||
    scopes.find((item) => path.resolve(item.codexHome) === path.resolve(String(input.codexHome || '')));
  if (!scope) throw new Error('请选择这条聊天记录所属的账号。');
  return { scope, service: createHistoryService(scopes) };
}

async function readHistoryThread(input = {}) {
  const { scope, service } = historyScopeForInput(input);
  const report = await service.readThread({
    codexHome: scope.codexHome,
    threadId: input.threadId,
    maxMessages: input.maxMessages,
    maxMessageCharacters: input.maxMessageCharacters,
  });
  return { ...report, ...scope, threadId: String(input.threadId || '') };
}

async function archiveHistoryThread(input = {}) {
  const { scope, service } = historyScopeForInput(input);
  return service.setThreadArchived({
    codexHome: scope.codexHome,
    threadId: input.threadId,
    archived: input.archived === true,
  });
}

async function deleteHistoryThread(input = {}) {
  const { scope, service } = historyScopeForInput(input);
  return service.deleteThread({ codexHome: scope.codexHome, threadId: input.threadId });
}

async function findFreeDebugPort() {
  for (let attempt = 0; attempt < 8; attempt += 1) {
    const port = await new Promise((resolve, reject) => {
      const server = net.createServer();
      server.unref();
      server.once('error', reject);
      server.listen(0, '127.0.0.1', () => {
        const address = server.address();
        server.close((error) => error ? reject(error) : resolve(Number(address?.port)));
      });
    });
    if (validPort(port)) return port;
  }
  throw new Error('无法分配 Codex App 本地调试端口。');
}

async function launchTerminalForAccount(account) {
  return runAccountActivation(async () => {
    const freshAccount = findAccount(account.id);
    const settings = store.loadSettings();
    const result = await launchThenCommitAccount({
      accountId: freshAccount.id,
      currentAccountId: settings.currentAccountId,
      launch: () => cli.launchTerminal(freshAccount, settings.projectPath),
      setCurrentAccount: (id) => store.setCurrentAccount(id),
      markAccountUsed: (id) => store.markAccountUsed(id),
    });
    notifyStateChanged();
    return { ...result, currentAccountId: freshAccount.id };
  });
}

function launchCodexAppForAccount(account, options = {}) {
  return runAccountActivation(() =>
    launchCodexAppForAccountUnlocked(findAccount(account.id), options));
}

async function launchCodexAppForAccountUnlocked(account, {
  applySavedTheme = true,
  enableThemeDebug = false,
  replaceRunningDesktop = false,
  selectAsCurrent = false,
} = {}) {
  const settings = store.loadSettings();
  const applyConfiguredTheme = applySavedTheme && settings.codexThemeId !== 'official-default';
  const useThemeRuntime = enableThemeDebug || applyConfiguredTheme;
  let remoteDebuggingPort = useThemeRuntime ? await findFreeDebugPort() : null;
  let existingThemeRuntime = codexAppSessions.get(account.id) || null;
  let launchedPid = null;
  const launchOptions = {
    remoteDebuggingPort,
    themeDebugProfile: useThemeRuntime,
    onExit: () => {
      const runtime = codexAppSessions.get(account.id);
      if (runtime && Number.isInteger(launchedPid) && runtime.pid === launchedPid) {
        codexAppSessions.delete(account.id);
        notifyStateChanged();
      }
    },
  };
  const result = await launchThenCommitAccount({
    accountId: account.id,
    currentAccountId: settings.currentAccountId,
    selectAsCurrent,
    launch: () => replaceRunningDesktop
      ? cli.switchCodexApp(account, store.loadAccounts(), settings.projectPath, launchOptions)
      : cli.launchCodexApp(account, settings.projectPath, launchOptions),
    setCurrentAccount: (id) => store.setCurrentAccount(id),
    markAccountUsed: (id) => store.markAccountUsed(id),
  });
  launchedPid = result.pid;
  if (replaceRunningDesktop) {
    codexAppSessions.clear();
    existingThemeRuntime = null;
  }
  if (useThemeRuntime && !result.handedOff) {
    codexAppSessions.set(account.id, {
      accountId: account.id,
      pid: result.pid,
      port: remoteDebuggingPort,
      launchedAt: new Date().toISOString(),
    });
  } else if (useThemeRuntime && result.handedOff && existingThemeRuntime) {
    remoteDebuggingPort = existingThemeRuntime.port;
  }
  notifyStateChanged();
  let theme = null;
  if (applyConfiguredTheme) {
    if (result.handedOff && !existingThemeRuntime) {
      theme = {
        ok: false,
        status: 'restart-required',
        reason: '检测到主题桌面实例已在运行，但管理器无法恢复它的调试会话。请退出该桌面实例后重新启动。',
      };
    } else {
      try {
        theme = await applySavedThemeToPort(remoteDebuggingPort, settings);
      } catch (error) {
        theme = { ok: false, status: 'unavailable', reason: error.message };
      }
    }
  }
  return {
    ...result,
    remoteDebuggingPort,
    rendererVerified: theme?.ok === true,
    theme,
  };
}

function customThemeMetadata(customTheme) {
  if (!customTheme) return null;
  const normalized = normalizeCustomTheme(customTheme);
  return {
    id: 'custom',
    name: normalized.name,
    appearance: normalized.mode,
    mode: normalized.mode,
    isDark: normalized.mode === 'dark',
    codeTheme: normalized.codeTheme,
    description: '保存在本机账号管理器中的自定义主题。',
    preview: {
      accent: normalized.accent,
      surface: normalized.surface,
      text: normalized.text,
    },
    accent: normalized.accent,
    surface: normalized.surface,
    text: normalized.text,
    backgroundImagePath: normalized.backgroundImagePath,
    builtIn: false,
    custom: true,
  };
}

async function applySavedThemeToPort(port, settings = store.loadSettings()) {
  if (settings.codexThemeId === 'custom') {
    if (!settings.customCodexTheme) throw new Error('自定义主题配置不存在。');
    return themeService.applyTheme({ port, customTheme: settings.customCodexTheme });
  }
  return themeService.applyTheme({ port, themeId: settings.codexThemeId });
}

function currentThemeRuntime(options = {}) {
  return runAccountActivation(() => currentThemeRuntimeUnlocked(options));
}

async function currentThemeRuntimeUnlocked({ launchIfMissing = false } = {}) {
  const currentAccountId = store.loadSettings().currentAccountId;
  if (!currentAccountId) throw new Error('请先在账号中心选择并设为当前账号。');
  const account = findAccount(currentAccountId);
  let runtime = codexAppSessions.get(account.id) || null;
  if (runtime) {
    const status = await themeService.getStatus({ port: runtime.port });
    if (status.ok) return { account, runtime, status };
    codexAppSessions.delete(account.id);
    runtime = null;
  }
  if (!launchIfMissing) return { account, runtime: null, status: null };
  const launched = await launchCodexAppForAccountUnlocked(account, {
    applySavedTheme: false,
    enableThemeDebug: true,
  });
  runtime = codexAppSessions.get(account.id);
  if (!runtime && launched.handedOff) {
    throw new Error('主题桌面实例已经在运行，但当前管理器没有它的调试会话。请退出该桌面实例后重试。');
  }
  if (!runtime) throw new Error('无法建立 Codex 主题调试会话。');
  return { account, runtime, status: null, launched };
}

async function getCodexThemes() {
  const settings = store.loadSettings();
  const themes = themeService.listThemes();
  const customTheme = customThemeMetadata(settings.customCodexTheme);
  if (customTheme) themes.push(customTheme);
  let runtimeStatus = null;
  if (settings.currentAccountId) {
    try {
      runtimeStatus = (await currentThemeRuntime()).status;
    } catch {
      runtimeStatus = null;
    }
  }
  const configuredId = settings.codexThemeId || 'official-default';
  return {
    themes: themes.map((theme) => ({
      ...theme,
      active: configuredId !== 'official-default' && theme.id === configuredId,
    })),
    currentThemeId: configuredId,
    codexThemeId: configuredId,
    customTheme: settings.customCodexTheme,
    customCodexTheme: settings.customCodexTheme,
    runtimeStatus,
  };
}

function applyCodexTheme(themeIdValue) {
  return runAccountActivation(() => applyCodexThemeUnlocked(themeIdValue));
}

async function applyCodexThemeUnlocked(themeIdValue) {
  const themeId = String(themeIdValue || '').trim();
  const settings = store.loadSettings();
  const builtIn = themeService.listThemes().some((theme) => theme.id === themeId);
  if (!builtIn && themeId !== 'custom') throw new Error('所选 Codex 主题不存在。');
  if (themeId === 'custom' && !settings.customCodexTheme) throw new Error('请先保存自定义主题。');
  const { runtime } = await currentThemeRuntimeUnlocked({ launchIfMissing: true });
  const result = themeId === 'custom'
    ? await themeService.applyTheme({ port: runtime.port, customTheme: settings.customCodexTheme })
    : await themeService.applyTheme({ port: runtime.port, themeId });
  const next = { ...settings, codexThemeId: themeId };
  try {
    store.saveSettings({ ...store.loadSettings(), codexThemeId: next.codexThemeId });
  } catch (error) {
    try {
      if (settings.codexThemeId === 'official-default') {
        await themeService.removeTheme({ port: runtime.port });
      } else {
        await applySavedThemeToPort(runtime.port, settings);
      }
    } catch (rollbackError) {
      error.rollbackError = rollbackError;
    }
    throw error;
  }
  notifyStateChanged();
  return result;
}

function restoreCodexTheme() {
  return runAccountActivation(() => restoreCodexThemeUnlocked());
}

async function restoreCodexThemeUnlocked() {
  const settings = store.loadSettings();
  const { account, runtime } = await currentThemeRuntimeUnlocked({ launchIfMissing: false });
  let result;
  if (runtime) {
    result = await themeService.removeTheme({ port: runtime.port });
  } else if (settings.codexThemeId === 'official-default') {
    result = { ok: true, status: 'official', targetCount: 0, runtimeRestored: true };
  } else {
    try {
      const themeProfile = path.resolve(account.codexHome, 'desktop-profile-theme');
      const themeRuntimeStillRunning = cli.listManagedCodexAppProcesses([account]).some((record) =>
        path.resolve(record.desktopProfile) === themeProfile && cli.desktopProcessIsAlive(record));
      result = themeRuntimeStillRunning
        ? {
          ok: false,
          status: 'restart-required',
          targetCount: 0,
          runtimeRestored: false,
          reason: '官方主题设置已保存，但当前 Codex App 的主题调试会话无法恢复。请退出并重新启动 Codex App。',
        }
        : {
          ok: true,
          status: 'preference-only',
          targetCount: 0,
          runtimeRestored: null,
          reason: '官方主题设置已保存；当前没有可恢复的主题调试会话，下次启动 Codex App 时生效。',
        };
    } catch {
      result = {
        ok: false,
        status: 'runtime-unknown',
        targetCount: 0,
        runtimeRestored: false,
        reason: '官方主题设置已保存，但无法确认当前 Codex App 的外观。请退出并重新启动 Codex App。',
      };
    }
  }
  try {
    store.saveSettings({ ...store.loadSettings(), codexThemeId: 'official-default' });
  } catch (error) {
    if (runtime && settings.codexThemeId !== 'official-default') {
      try {
        await applySavedThemeToPort(runtime.port, settings);
      } catch (rollbackError) {
        error.rollbackError = rollbackError;
      }
    }
    throw error;
  }
  notifyStateChanged();
  return { ...result, preferenceSaved: true };
}

function saveCustomTheme(input = {}) {
  return runAccountActivation(() => saveCustomThemeUnlocked(input));
}

async function saveCustomThemeUnlocked(input = {}) {
  const normalized = normalizeCustomTheme(input);
  await buildCustomThemeCss(normalized);
  const customCodexTheme = {
    name: normalized.name,
    mode: normalized.mode,
    isDark: normalized.mode === 'dark',
    codeTheme: normalized.codeTheme,
    codeThemeId: normalized.codeTheme,
    accent: normalized.accent,
    accentColor: normalized.accent,
    surface: normalized.surface,
    surfaceColor: normalized.surface,
    text: normalized.text,
    textColor: normalized.text,
    inkColor: normalized.text,
    backgroundImagePath: normalized.backgroundImagePath,
  };
  const settings = store.loadSettings();
  store.saveSettings({ ...settings, customCodexTheme });
  notifyStateChanged();
  return { ok: true, theme: customThemeMetadata(customCodexTheme) };
}

function quotaCredentialStillActive(account) {
  const accountId = String(account?.id || '').trim();
  const epoch = String(account?.credentialEpoch || '').trim();
  if (!accountId || !epoch || retiringAccountIds.has(accountId)) return false;
  return store.loadAccounts().some((current) =>
    current.id === accountId && String(current.credentialEpoch || '').trim() === epoch);
}

async function stopQuotaReadsForAccount(accountId) {
  const matching = [...activeQuotaReads].filter((operation) => operation.accountIds.has(accountId));
  for (const operation of matching) operation.controller.abort();
  const results = await Promise.allSettled(matching.map((operation) => operation.promise));
  const unsafeStop = results.find((result) =>
    result.status === 'rejected' && result.reason?.code === 'QUOTA_PROCESS_STOP_FAILED');
  if (unsafeStop) throw unsafeStop.reason;
}

function loadQuota(input = {}) {
  const settings = store.loadSettings();
  const accounts = store.loadAccounts().filter((account) => !retiringAccountIds.has(account.id));
  const accountSnapshots = new Map(accounts.map((account) => [account.id, account]));
  const controller = new AbortController();
  const operation = {
    accountIds: new Set(accounts.map((account) => account.id)),
    controller,
    promise: null,
  };
  activeQuotaReads.add(operation);
  const task = (async () => {
    const report = await getQuotaStats(accounts, {
      accountId: input.accountId || 'all',
      switches: store.loadUsageSwitches(),
      codexCli: cli,
      live: input.live !== false && retiringAccountIds.size === 0,
      signal: controller.signal,
      environment: applyProxyEnvironment(process.env, settings),
      quotaSnapshotsPath: store.quotaSnapshotsPath,
      isCredentialStillActive: quotaCredentialStillActive,
      readLiveQuota: async (account, options) => {
        await cli.ensureAccountServices(account);
        return readRateLimitsViaAppServer(account, options);
      },
    });
    report.accounts = report.accounts.filter((row) => {
      const snapshot = accountSnapshots.get(row.accountId);
      return snapshot && quotaCredentialStillActive(snapshot);
    });
    for (const row of report.accounts) {
      if (!row.available) continue;
      const snapshot = accountSnapshots.get(row.accountId);
      if (!snapshot || !quotaCredentialStillActive(snapshot)) continue;
      try {
        store.updateQuotaProfile(row.accountId, {
          quotaLimitType: row.planType || 'unknown',
          quotaPrimaryWindowMinutes: row.primary?.windowMinutes ?? null,
          quotaSecondaryWindowMinutes: row.secondary?.windowMinutes ?? null,
        });
      } catch {
        row.cacheWarning = row.cacheWarning || '实时额度已读取，但账号额度元数据未能保存。';
      }
    }
    return report;
  })();
  operation.promise = task.finally(() => activeQuotaReads.delete(operation));
  return operation.promise;
}

function installIpcHandlers() {
  handleIpc('state:get', () => buildState());
  handleIpc('account:create', (_event, data) => runAccountActivation(() => {
    if (data?.authKind === 'official_oauth') {
      throw new Error('请先生成官方登录链接并看到“✓ 已登录”，再保存账号。');
    }
    const account = store.saveAccount(data || {});
    notifyStateChanged();
    return publicAccount(account, store.loadSettings().currentAccountId);
  }));
  handleIpc('account:update', (_event, payload) => runAccountActivation(() => {
    if (payload?.data?.authKind === 'official_oauth') {
      throw new Error('官方 OAuth 账号必须通过已验证的登录草稿保存。');
    }
    if (payload?.id) findAccount(payload.id);
    const account = store.saveAccount(payload?.data || {}, payload?.id);
    notifyStateChanged();
    return publicAccount(account, store.loadSettings().currentAccountId);
  }));
  handleIpc('account:delete', (_event, id) => runAccountActivation(async () => {
    const account = findAccount(id);
    retiringAccountIds.add(id);
    try {
      await cancelOAuthDraftsBeforeAccountDeletion();
      await stopQuotaReadsForAccount(id);
      await cli.stopManagedTerminalSessions(account);
      await cli.stopManagedCodexApps([account]);
      const removal = store.removeAccount(id);
      loginStatuses.delete(id);
      codexAppSessions.delete(id);
      notifyStateChanged();
      return { ok: true, cleanupWarning: removal?.cleanupWarning || null };
    } finally {
      retiringAccountIds.delete(id);
    }
  }));
  handleIpc('account:import', async () => {
    const result = await dialog.showOpenDialog(mainWindow, {
      title: '导入账号列表',
      filters: [{ name: 'JSON', extensions: ['json'] }],
      properties: ['openFile'],
    });
    if (result.canceled || result.filePaths.length === 0) return { canceled: true, importedCount: 0 };
    return runAccountActivation(() => {
      let values;
      try { values = JSON.parse(fs.readFileSync(result.filePaths[0], 'utf8')); }
      catch { throw new Error('无法读取该 JSON 文件。'); }
      const importedCount = store.importAccounts(values);
      notifyStateChanged();
      return { canceled: false, importedCount };
    });
  });
  handleIpc('account:set-current', async (_event, id) => {
    const account = findAccount(id);
    const desktop = await launchCodexAppForAccount(account, {
      replaceRunningDesktop: true,
      selectAsCurrent: true,
    });
    return { ok: true, currentAccountId: account.id, desktop };
  });
  handleIpc('account:login', (_event, payload) => runAccountActivation(async () => {
    const account = findAccount(payload?.accountId);
    const status = normalizeCheckedStatus(account, await cli.login(account, payload?.accessToken));
    const activatedAccount = store.activateCredential(account.id);
    loginStatuses.set(activatedAccount.id, status);
    notifyStateChanged();
    return status;
  }));
  handleIpc('account:oauth-draft-prepare', (_event, payload) => runAccountActivation(async () => {
    const data = payload?.data || {};
    const editingId = typeof payload?.editingId === 'string' ? payload.editingId : null;
    assertOfficialOAuthData(data);

    if (payload?.reuseExisting === true) {
      if (!editingId || !store.canReuseOfficialOAuth(data, editingId)) return { verified: false };
      const draftId = crypto.randomUUID();
      oauthDrafts.set(draftId, {
        draftId,
        editingId,
        reuseExisting: true,
        verified: true,
        timer: expireOAuthDraftLater(draftId),
      });
      return { draftId, verified: true };
    }

    if ([...oauthDrafts.values()].some((item) => item.session)) {
      throw new Error('已有一个 ChatGPT 官方登录正在等待完成，请先取消。');
    }
    const draftId = crypto.randomUUID();
    const draft = store.prepareOfficialOAuthDraft(data, editingId);
    const pendingAccount = {
      ...draft.candidate,
      id: draftId,
      codexHome: draft.pendingCodexHome,
      authKind: 'official_oauth',
    };
    let session;
    try {
      session = cli.startOfficialLogin(pendingAccount);
    } catch (error) {
      store.cleanupOfficialOAuthDraft(draft.pendingCodexHome);
      throw error;
    }
    const state = {
      draftId,
      editingId,
      draft,
      session,
      reuseExisting: false,
      verified: false,
      timer: expireOAuthDraftLater(draftId),
    };
    oauthDrafts.set(draftId, state);
    session.completed.then((status) => {
      if (oauthDrafts.get(draftId) !== state || state.cancelTask) return;
      state.session = null;
      state.verified = true;
      sendOAuthDraftResult({ draftId, ok: true, status });
    }).catch(() => {
      if (oauthDrafts.get(draftId) !== state || state.cancelTask) return;
      cancelOAuthDraft(draftId).then(
        () => sendOAuthDraftResult({ draftId, ok: false, message: '官方网页登录未完成或已取消。' }),
        () => sendOAuthDraftResult({
          draftId,
          ok: false,
          message: '官方登录进程未能安全退出；临时凭据已保留，请稍后取消或重试。',
        }),
      );
    });
    try {
      const ready = await session.ready;
      if (oauthDrafts.get(draftId) !== state) throw new Error('官方网页登录已取消。');
      return { draftId, authUrl: ready.authUrl, verified: false };
    } catch {
      await cancelOAuthDraft(draftId);
      throw new Error('无法生成 ChatGPT 官方登录链接，请重试。');
    }
  }));
  handleIpc('account:oauth-draft-commit', (_event, payload) => runAccountActivation(() => {
    const draftId = String(payload?.draftId || '');
    const data = payload?.data || {};
    assertOfficialOAuthData(data);
    const state = oauthDrafts.get(draftId);
    if (!state || !state.verified) throw new Error('官方登录尚未完成，不能保存账号。');
    if (state.cancelTask) throw new Error('官方登录草稿正在取消，不能保存账号。');
    let account;
    if (state.reuseExisting) {
      if (!state.editingId || !store.canReuseOfficialOAuth(data, state.editingId)) {
        throw new Error('原账号凭据已变化，请重新登录。');
      }
      account = store.saveAccount(data, state.editingId);
    } else {
      account = store.commitOfficialOAuthDraft(state.draft, data);
      account = store.activateCredential(account.id);
    }
    oauthDrafts.delete(draftId);
    if (state.timer) clearTimeout(state.timer);
    loginStatuses.set(account.id, {
      ok: true,
      loggedIn: true,
      badge: 'OAUTH',
      text: '✓ 已登录',
      checkedAt: new Date().toISOString(),
    });
    notifyStateChanged();
    return publicAccount(account, store.loadSettings().currentAccountId);
  }));
  handleIpc('account:oauth-draft-cancel', async (_event, draftId) => ({
    ok: await cancelOAuthDraft(draftId),
  }));
  handleIpc('account:status', (_event, id) => runAccountActivation(async () => {
    const account = findAccount(id);
    const status = await checkAccountStatus(account);
    notifyStateChanged();
    return status;
  }));
  handleIpc('account:status-all', () => runAccountActivation(() => checkAllAccountStatuses()));
  handleIpc('account:launch-terminal', (_event, id) => launchTerminalForAccount(findAccount(id)));
  handleIpc('account:launch-codex-app', (_event, id) => {
    const account = findAccount(id);
    return launchCodexAppForAccount(account, {
      replaceRunningDesktop: true,
      selectAsCurrent: true,
    });
  });
  handleIpc('usage:get', (_event, options = {}) => {
    const range = ['today', '7d', '30d', 'all'].includes(options?.range) ? options.range : '30d';
    return getUsageStats(store.loadAccounts(), {
      range,
      accountId: options.accountId || 'all',
      switches: store.loadUsageSwitches(),
    });
  });
  handleIpc('quota:get', (_event, options) => loadQuota(options || {}));
  handleIpc('history:list', (_event, options) => listHistory(options || {}));
  handleIpc('history:read', (_event, options) => readHistoryThread(options || {}));
  handleIpc('history:archive', (_event, options) =>
    runAccountActivation(() => archiveHistoryThread(options || {})));
  handleIpc('history:delete', (_event, options) =>
    runAccountActivation(() => deleteHistoryThread(options || {})));
  handleIpc('settings:get', () => publicSettings());
  handleIpc('settings:save', (_event, options) => saveManagerSettings(options || {}));
  handleIpc('settings:choose-launch-directory', async () => {
    const result = await dialog.showOpenDialog(mainWindow, {
      title: '选择 Codex 默认启动目录',
      defaultPath: store.loadSettings().projectPath,
      properties: ['openDirectory', 'createDirectory'],
    });
    if (result.canceled || result.filePaths.length === 0) return { canceled: true, path: null };
    return { canceled: false, path: normalizeProjectPath(result.filePaths[0]) };
  });
  handleIpc('settings:choose-codex-app', async () => {
    const configuredPath = store.loadSettings().codexAppPath;
    const result = await dialog.showOpenDialog(mainWindow, {
      title: '选择包含 Codex 的 ChatGPT 或旧版 Codex 应用',
      defaultPath: configuredPath || '/Applications',
      properties: ['openFile'],
      filters: [{ name: 'macOS 应用', extensions: ['app'] }],
    });
    if (result.canceled || result.filePaths.length === 0) return { canceled: true, path: null };
    const application = cli.resolveCodexApplication(result.filePaths[0]);
    return {
      canceled: false,
      path: application.appPath,
      appKind: application.appKind,
    };
  });
  handleIpc('settings:open-path', (_event, target) => openPath(target));
  handleIpc('settings:detect-proxy', (_event, options) => detectAndSaveLocalProxy(options || {}));
  handleIpc('settings:model-catalog-check', async () => {
    const result = await checkAndSaveOfficialCatalog({ userDataPath: app.getPath('userData') });
    store.migrateAccessTokenConfigs(store.loadAccounts());
    return result;
  });
  handleIpc('codex-theme:list', () => getCodexThemes());
  handleIpc('codex-theme:apply', (_event, themeId) => applyCodexTheme(themeId));
  handleIpc('codex-theme:restore', () => restoreCodexTheme());
  handleIpc('codex-theme:save-custom', (_event, options) => saveCustomTheme(options || {}));
  handleIpc('theme:set', (_event, theme) => {
    if (!['system', 'light', 'dark'].includes(theme)) throw new Error('无效的主题。');
    const settings = store.loadSettings();
    settings.theme = theme;
    store.saveSettings(settings);
    nativeTheme.themeSource = theme;
    notifyStateChanged();
    return { ok: true };
  });
  handleIpc('app:update-check', () => checkForUpdate({
    currentVersion: app.getVersion(),
    platform: process.platform,
  }));
}

async function promptForUpdate(manual = false) {
  if (updateCheckInFlight || (!manual && automaticUpdateCheckStarted)) {
    return updateCheckInFlight;
  }
  if (!manual) automaticUpdateCheckStarted = true;
  updateCheckInFlight = (async () => {
    try {
      const checkResult = await checkForUpdate({
        currentVersion: app.getVersion(),
        platform: process.platform,
      });
      const update = checkResult?.update || null;
      if (!update) {
        pendingUpdate = null;
        createMenu();
        if (manual) {
          await dialog.showMessageBox(mainWindow, {
            type: 'info',
            title: '检查更新',
            message: describeUpdateCheckStatus(checkResult?.status),
          });
        }
        return null;
      }

      pendingUpdate = update;
      createMenu();
      if (!manual) {
        if (mainWindow && !mainWindow.isDestroyed()) {
          mainWindow.webContents.send('app:update-available', {
            version: update.version,
          });
        }
        return update;
      }

      const answer = await dialog.showMessageBox(mainWindow, {
        type: 'info',
        title: 'Codex Account Manager 更新',
        message: `发现新版本 ${update.version}`,
        detail: '现在下载并安装吗？已有账号、额度记录和本地配置会保留。',
        buttons: ['现在更新', '稍后'],
        defaultId: 0,
        cancelId: 1,
      });
      if (answer.response !== 0) return update;
      await downloadAndScheduleInstall(update, { currentPid: process.pid });
      await dialog.showMessageBox(mainWindow, {
        type: 'info',
        title: '更新已准备',
        message: '更新包已校验完成，程序将关闭并安装新版本。',
      });
      app.quit();
      return update;
    } catch (error) {
      if (manual) {
        await dialog.showMessageBox(mainWindow, {
          type: 'warning',
          title: '更新失败',
          message: '更新失败，当前版本未被修改。',
          detail: String(error?.message || error),
        });
      }
      return null;
    } finally {
      updateCheckInFlight = null;
    }
  })();
  return updateCheckInFlight;
}

function describeUpdateCheckStatus(status) {
  switch (status) {
    case UPDATE_CHECK_STATUS.UP_TO_DATE:
      return `当前已是最新版本（${app.getVersion()}）。`;
    case UPDATE_CHECK_STATUS.NETWORK_UNAVAILABLE:
      return '无法连接 GitHub，请检查网络或代理设置。';
    case UPDATE_CHECK_STATUS.RELEASE_UNAVAILABLE:
      return 'GitHub 暂未发布可用版本。';
    case UPDATE_CHECK_STATUS.MANIFEST_MISSING:
      return 'GitHub Release 缺少更新清单（update-manifest.json）。';
    case UPDATE_CHECK_STATUS.MANIFEST_INVALID:
      return 'GitHub Release 的更新清单格式无效，暂时无法确认版本。';
    case UPDATE_CHECK_STATUS.PLATFORM_ASSET_MISSING:
      return 'GitHub Release 缺少 macOS 更新包，暂时无法更新。';
    default:
      return '当前没有可用更新。';
  }
}

function createMenu() {
  const template = [
    {
      label: 'Codex Account Manager',
      submenu: [
        { role: 'about', label: '关于 Codex Account Manager' },
        {
          label: pendingUpdate ? `可更新（${pendingUpdate.version}）` : '检查更新',
          click: () => { promptForUpdate(true).catch(() => {}); },
        },
        { type: 'separator' },
        { role: 'hide', label: '隐藏' },
        { role: 'hideOthers', label: '隐藏其他' },
        { role: 'unhide', label: '全部显示' },
        { type: 'separator' },
        { role: 'quit', label: '退出' },
      ],
    },
    { label: '编辑', submenu: [{ role: 'undo', label: '撤销' }, { role: 'redo', label: '重做' }, { type: 'separator' }, { role: 'cut', label: '剪切' }, { role: 'copy', label: '复制' }, { role: 'paste', label: '粘贴' }, { role: 'selectAll', label: '全选' }] },
    { label: '窗口', submenu: [{ role: 'minimize', label: '最小化' }, { role: 'zoom', label: '缩放' }, { role: 'front', label: '前置全部窗口' }] },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1240,
    height: 820,
    minWidth: 1000,
    minHeight: 680,
    show: false,
    title: 'Codex Account Manager',
    titleBarStyle: 'hiddenInset',
    trafficLightPosition: { x: 18, y: 18 },
    backgroundColor: nativeTheme.shouldUseDarkColors ? '#0b0d14' : '#f4f6fb',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      spellcheck: false,
    },
  });
  mainWindow.loadFile(path.join(__dirname, 'renderer.html'));
  mainWindow.once('ready-to-show', () => mainWindow.show());
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  mainWindow.webContents.on('will-navigate', (event, url) => {
    if (url !== mainWindow.webContents.getURL()) event.preventDefault();
  });
  mainWindow.on('closed', () => {
    for (const draftId of [...oauthDrafts.keys()]) cancelOAuthDraft(draftId).catch(() => {});
    mainWindow = null;
  });
}

async function runPatGatewayDaemon() {
  app.dock?.hide();
  const userDataPath = app.getPath('userData');
  const gatewaySecret = await loadGatewaySecret(userDataPath);
  const settingsPath = path.join(userDataPath, 'settings.json');
  const readDaemonProxyUrl = () => {
    try {
      const value = JSON.parse(fs.readFileSync(settingsPath, 'utf8'));
      return value && typeof value === 'object' && !Array.isArray(value)
        ? formatProxyUrl(value)
        : null;
    } catch {
      return null;
    }
  };
  patGatewayNetworkSession = session.fromPartition('codex-account-manager-pat-gateway-daemon', { cache: false });
  const preparePatGatewayUpstream = createPatGatewayUpstreamPreparer({
    networkSession: patGatewayNetworkSession,
    proxyUrlProvider: readDaemonProxyUrl,
  });
  patGateway = new LocalPatGateway({
    gatewaySecret,
    prepareUpstream: preparePatGatewayUpstream,
    fetchImpl: (url, options) => patGatewayNetworkSession.fetch(url, options),
  });
  await patGateway.ensureListening();
}

if (isPatGatewayDaemon) {
  app.whenReady()
    .then(() => runPatGatewayDaemon())
    .catch(() => app.exit(1));
} else {
  const hasLock = app.requestSingleInstanceLock();
  if (!hasLock) app.quit();
  else {
  app.on('second-instance', () => {
    if (!mainWindow || mainWindow.isDestroyed()) {
      if (app.isReady()) createWindow();
      return;
    }
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
  });

  app.whenReady().then(async () => {
    store = new AccountStore(app.getPath('userData'));
    const accounts = store.migrateAccountMetadata();
    store.migrateAccessTokenConfigs(accounts);
    const gatewaySecret = await loadOrCreateGatewaySecret(app.getPath('userData'));
    patGateway = new PatGatewayController({
      gatewaySecret,
      execPath: process.execPath,
      appPath: app.getAppPath(),
      packaged: app.isPackaged,
      platform: process.platform,
    });
    cli = new CodexCliService({
      resourcesPath: process.resourcesPath,
      userDataPath: app.getPath('userData'),
      allowExecutableOverride: !app.isPackaged,
      settingsProvider: () => store.loadSettings(),
      patGateway,
    });
    themeService = new ThemeService();
    const settings = store.loadSettings();
    nativeTheme.themeSource = settings.theme;
    if (settings.proxyAutoDetect) detectAndSaveLocalProxy().catch(() => {});
    if (accounts.some((account) => account.authKind === 'access_token')) {
      patGateway.ensureRunning().catch(() => {});
    }
    session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
    session.defaultSession.setPermissionCheckHandler(() => false);
    installIpcHandlers();
    createMenu();
    createWindow();
    setTimeout(() => { promptForUpdate(false).catch(() => {}); }, 5000).unref();
    app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
  }).catch((error) => {
    const message = String(error?.message || '未知启动错误').replace(/[\r\n]+/g, ' ').slice(0, 800);
    dialog.showErrorBox('Codex Account Manager 启动失败', message);
    app.exit(1);
  });
  }

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') app.quit();
  });

  app.on('before-quit', (event) => {
    if (oauthShutdownComplete) return;
    event.preventDefault();
    if (oauthShutdownPromise) return;
    for (const draftId of [...oauthDrafts.keys()]) cancelOAuthDraft(draftId);
    oauthShutdownPromise = Promise.allSettled([...oauthCleanupTasks]).then(() => {
      oauthShutdownComplete = true;
      app.quit();
    });
  });
}
