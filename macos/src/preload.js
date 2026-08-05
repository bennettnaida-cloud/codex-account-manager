const { clipboard, contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('codexManager', {
  getState: () => ipcRenderer.invoke('state:get'),
  createAccount: (data) => ipcRenderer.invoke('account:create', data),
  updateAccount: (id, data) => ipcRenderer.invoke('account:update', { id, data }),
  deleteAccount: (id) => ipcRenderer.invoke('account:delete', id),
  importAccounts: () => ipcRenderer.invoke('account:import'),
  setCurrentAccount: (id) => ipcRenderer.invoke('account:set-current', id),
  loginAccount: (data) => ipcRenderer.invoke('account:login', data),
  prepareOAuthDraft: (data) => ipcRenderer.invoke('account:oauth-draft-prepare', data),
  commitOAuthDraft: (data) => ipcRenderer.invoke('account:oauth-draft-commit', data),
  cancelOAuthDraft: (draftId) => ipcRenderer.invoke('account:oauth-draft-cancel', draftId),
  getLoginStatus: (id) => ipcRenderer.invoke('account:status', id),
  getAllLoginStatuses: () => ipcRenderer.invoke('account:status-all'),
  launchTerminal: (id) => ipcRenderer.invoke('account:launch-terminal', id),
  launchCodexApp: (id) => ipcRenderer.invoke('account:launch-codex-app', id),
  getUsageStats: (options) => ipcRenderer.invoke('usage:get', options || {}),
  getQuotaStats: (options) => ipcRenderer.invoke('quota:get', options || {}),
  getHistory: (options) => ipcRenderer.invoke('history:list', options || {}),
  searchHistory: (options) => ipcRenderer.invoke('history:list', options || {}),
  readThread: (options) => ipcRenderer.invoke('history:read', options || {}),
  setThreadArchived: (options) => ipcRenderer.invoke('history:archive', options || {}),
  deleteThread: (options) => ipcRenderer.invoke('history:delete', options || {}),
  getSystemSettings: () => ipcRenderer.invoke('settings:get'),
  saveSystemSettings: (options) => ipcRenderer.invoke('settings:save', options || {}),
  chooseLaunchDirectory: () => ipcRenderer.invoke('settings:choose-launch-directory'),
  chooseCodexApp: () => ipcRenderer.invoke('settings:choose-codex-app'),
  detectLocalProxy: (options) => ipcRenderer.invoke('settings:detect-proxy', options || {}),
  openPath: (targetPath) => ipcRenderer.invoke('settings:open-path', targetPath),
  getCodexThemes: () => ipcRenderer.invoke('codex-theme:list'),
  applyCodexTheme: (themeId) => ipcRenderer.invoke('codex-theme:apply', themeId),
  restoreCodexTheme: () => ipcRenderer.invoke('codex-theme:restore'),
  saveCustomTheme: (options) => ipcRenderer.invoke('codex-theme:save-custom', options || {}),
  setTheme: (theme) => ipcRenderer.invoke('theme:set', theme),
  checkForUpdates: () => ipcRenderer.invoke('app:update-check'),
  onStateChanged: (callback) => {
    const listener = (_event, state) => callback(state);
    ipcRenderer.on('state:changed', listener);
    return () => ipcRenderer.removeListener('state:changed', listener);
  },
  onOAuthDraftCompleted: (callback) => {
    const listener = (_event, payload) => callback(payload);
    ipcRenderer.on('account:oauth-draft-completed', listener);
    return () => ipcRenderer.removeListener('account:oauth-draft-completed', listener);
  },
  writeClipboardText: (value) => clipboard.writeText(String(value || '')),
  clearClipboardIfMatches: (values) => {
    const allowed = Array.isArray(values) ? values.map((value) => String(value || '')) : [];
    if (allowed.includes(clipboard.readText())) clipboard.clear();
  },
});
