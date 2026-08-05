const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const rendererPath = path.join(__dirname, '..', 'src', 'renderer.js');
const rendererSource = fs.readFileSync(rendererPath, 'utf8');

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function fakeElement(initialHtml = '') {
  let html = String(initialHtml);
  return {
    disabled: false,
    style: {},
    value: 'all',
    classList: { add() {}, toggle() {} },
    querySelectorAll() { return []; },
    replaceChildren() {},
    get innerHTML() { return html; },
    set innerHTML(value) { html = String(value); },
    get textContent() { return html; },
    set textContent(value) { html = String(value); },
  };
}

function createHarness(bridge = {}) {
  const document = {
    documentElement: { dataset: {} },
    addEventListener() {},
    getElementById() { return null; },
    querySelectorAll() { return []; },
    createElement() { return fakeElement(); },
  };
  const window = {
    codexManager: bridge,
    matchMedia() { return { matches: false, addEventListener() {} }; },
    setTimeout,
    clearTimeout,
    setInterval,
    clearInterval,
  };
  const expose = `
    const rendererTestApi = {
      app,
      el,
      loadState,
      loadUsage,
      loadQuota,
      invalidateAllAccountDerivedRequests,
      clearDeletedAccountDerivedState,
      renderUsageAccountFilter,
      renderQuota,
      filteredHistoryThreads,
      normalizeUsageReport,
      normalizeQuotaReport,
    };
    renderUsage = () => {};
    renderCurrentCard = () => {};
    renderAll = () => {};
    renderUsageAccountFilter = () => {};
    renderQuota = () => {};
    renderHistoryList = () => {};
    renderThreadReader = () => {};
    globalThis.__rendererTestApi = rendererTestApi;
  `;
  const marker = '\n})();';
  const markerIndex = rendererSource.lastIndexOf(marker);
  assert.notEqual(markerIndex, -1, 'renderer test hook insertion point must exist');
  const instrumented = `${rendererSource.slice(0, markerIndex)}${expose}${rendererSource.slice(markerIndex)}`;
  const context = vm.createContext({
    console,
    document,
    window,
    navigator: { clipboard: { writeText: async () => {} } },
    requestAnimationFrame() {},
    setTimeout,
    clearTimeout,
    setInterval,
    clearInterval,
  });
  vm.runInContext(instrumented, context, { filename: rendererPath });
  const api = context.__rendererTestApi;
  api.el.usagePage = fakeElement();
  api.el.rangeControl = fakeElement();
  api.el.refreshQuotaButton = fakeElement('刷新额度');
  api.el.refreshHistoryButton = fakeElement('刷新记录');
  api.el.quotaGrid = fakeElement();
  api.el.quotaUpdatedAt = fakeElement();
  api.el.usageAccountFilter = fakeElement();
  api.el.historyAccountFilter = fakeElement();
  api.el.quotaAccountFilter = fakeElement();
  api.el.versionLabel = fakeElement();
  return api;
}

function quotaRow(accountId, accountName) {
  return {
    accountId,
    accountName,
    available: true,
    supported: true,
    windows: { fiveHour: { usedPercent: 10, windowMinutes: 300 } },
  };
}

function usageRow(accountId, accountName, totalTokens) {
  return {
    accountId,
    accountName,
    totalTokens,
    inputTokens: totalTokens,
    outputTokens: 0,
    cachedInputTokens: 0,
    cacheWriteTokens: 0,
    apiEquivalentUsd: totalTokens / 100,
    knownApiEquivalentUsd: totalTokens / 100,
    apiEquivalentComplete: true,
    models: [],
    timeline: [],
  };
}

test('an older state response cannot resurrect a deleted account after a newer state wins', async () => {
  const oldRequest = deferred();
  const newRequest = deferred();
  const queue = [oldRequest, newRequest];
  const api = createHarness({ getState: () => queue.shift().promise });

  const oldLoad = api.loadState();
  const newLoad = api.loadState();
  newRequest.resolve({
    accounts: [{ id: 'active', name: '保留账号', codexHome: '/accounts/active' }],
    currentAccountId: 'active',
  });
  await newLoad;
  assert.deepEqual(Array.from(api.app.accounts, (account) => account.id), ['active']);

  const usageGenerationAfterNewState = api.app.usageRequest;
  const quotaGenerationAfterNewState = api.app.quotaRequest;
  oldRequest.resolve({
    accounts: [
      { id: 'active', name: '保留账号', codexHome: '/accounts/active' },
      { id: 'deleted', name: '已删账号', codexHome: '/accounts/deleted' },
    ],
    currentAccountId: 'deleted',
  });
  const oldResult = await oldLoad;

  assert.equal(oldResult.stale, true);
  assert.deepEqual(Array.from(api.app.accounts, (account) => account.id), ['active']);
  assert.equal(api.app.currentAccountId, 'active');
  assert.equal(api.app.usageRequest, usageGenerationAfterNewState, 'stale state must not invalidate newer usage work');
  assert.equal(api.app.quotaRequest, quotaGenerationAfterNewState, 'stale state must not invalidate newer quota work');
});

test('an invalidated quota response cannot overwrite or finish a newer request', async () => {
  const oldRequest = deferred();
  const newRequest = deferred();
  const queue = [oldRequest, newRequest];
  const api = createHarness({ getQuotaStats: () => queue.shift().promise });
  api.app.accounts = [{ id: 'active', name: '保留账号' }];

  const oldLoad = api.loadQuota(true);
  api.invalidateAllAccountDerivedRequests();
  const newLoad = api.loadQuota(true);

  oldRequest.resolve({ accounts: [quotaRow('deleted', '已删账号')] });
  await oldLoad;
  assert.equal(api.app.quotaInFlight, true, 'old finally must not clear the newer in-flight request');
  assert.equal(api.el.refreshQuotaButton.disabled, true, 'old finally must not re-enable the newer request button');
  assert.equal(api.app.quotaReport.accounts.length, 0);

  newRequest.resolve({ accounts: [quotaRow('active', '保留账号'), quotaRow('deleted', '已删账号')] });
  await newLoad;
  assert.equal(api.app.quotaInFlight, false);
  assert.deepEqual(Array.from(api.app.quotaReport.accounts, (row) => row.accountId), ['active']);
});

test('an invalidated usage response cannot finish a newer request and deleted rows are excluded from totals', async () => {
  const oldRequest = deferred();
  const newRequest = deferred();
  const queue = [oldRequest, newRequest];
  const api = createHarness({ getUsageStats: () => queue.shift().promise });
  api.app.accounts = [{ id: 'active', name: '保留账号' }];

  const oldLoad = api.loadUsage(true);
  api.invalidateAllAccountDerivedRequests();
  const newLoad = api.loadUsage(true);

  oldRequest.resolve({
    aggregate: usageRow('all', '全部', 90),
    perAccount: [usageRow('deleted', '已删账号', 90)],
    unattributed: usageRow('', '未归属', 0),
  });
  await oldLoad;
  assert.equal(api.app.usageInFlight, true, 'old finally must not clear the newer usage request');
  assert.equal(api.app.usageReport.perAccount.length, 0);

  newRequest.resolve({
    aggregate: usageRow('all', '全部', 105),
    perAccount: [usageRow('active', '保留账号', 10), usageRow('deleted', '已删账号', 90)],
    unattributed: usageRow('', '未归属', 5),
  });
  await newLoad;
  assert.equal(api.app.usageInFlight, false);
  assert.deepEqual(Array.from(api.app.usageReport.perAccount, (row) => row.accountId), ['active']);
  assert.equal(api.app.usageReport.aggregate.totalTokens, 15, 'all-account total is rebuilt from active accounts plus unattributed usage');
});

test('deleting an account clears its derived state and render filters cannot resurrect it', () => {
  const api = createHarness();
  const deleted = { id: 'deleted', name: '已删账号', codexHome: '/accounts/deleted' };
  api.app.accounts = [{ id: 'active', name: '保留账号', codexHome: '/accounts/active' }, deleted];
  api.app.currentAccountId = deleted.id;
  api.app.selectedAccountId = deleted.id;
  api.app.loginStatuses = { active: { loggedIn: true }, deleted: { loggedIn: true } };
  api.app.usageAccountFilter = deleted.id;
  api.app.quotaAccountFilter = deleted.id;
  api.app.historyAccountFilter = deleted.id;
  api.app.usageReport = api.normalizeUsageReport({
    aggregate: usageRow('all', '全部', 115),
    perAccount: [usageRow('active', '保留账号', 10), usageRow('deleted', '已删账号', 100)],
    unattributed: usageRow('', '未归属', 5),
  });
  api.app.quotaReport = api.normalizeQuotaReport({
    accounts: [quotaRow('active', '保留账号'), quotaRow('deleted', '已删账号')],
  });
  api.app.historyThreads = [
    { id: 'active-thread', accountId: 'active', codexHome: '/accounts/active', archived: false },
    { id: 'deleted-thread', accountId: 'deleted', codexHome: '/accounts/deleted', archived: false },
    { id: 'shared-thread', accountId: '', codexHome: '', archived: false },
  ];
  api.app.selectedThreadId = 'deleted-thread';
  api.app.selectedThread = api.app.historyThreads[1];

  api.clearDeletedAccountDerivedState(deleted);

  assert.deepEqual(Array.from(api.app.accounts, (account) => account.id), ['active']);
  assert.deepEqual(Array.from(api.app.usageReport.perAccount, (row) => row.accountId), ['active']);
  assert.equal(api.app.usageReport.aggregate.totalTokens, 15);
  assert.deepEqual(Array.from(api.app.quotaReport.accounts, (row) => row.accountId), ['active']);
  assert.deepEqual(Array.from(api.app.historyThreads, (thread) => thread.id), ['active-thread', 'shared-thread']);
  assert.equal(api.app.selectedThread, null);
  assert.equal(api.app.loginStatuses.deleted, undefined);
  assert.equal(api.app.usageAccountFilter, 'all');
  assert.equal(api.app.quotaAccountFilter, 'all');
  assert.equal(api.app.historyAccountFilter, 'all');

  api.renderUsageAccountFilter();
  assert.doesNotMatch(api.el.usageAccountFilter.innerHTML, /deleted|已删账号/);

  api.app.quotaReport.accounts.push(api.normalizeQuotaReport({ accounts: [quotaRow('deleted', '已删账号')] }).accounts[0]);
  api.renderQuota();
  assert.match(api.el.quotaGrid.innerHTML, /保留账号/);
  assert.doesNotMatch(api.el.quotaGrid.innerHTML, /已删账号/);

  api.app.historyThreads.push({ id: 'stale-deleted-thread', accountId: 'deleted', codexHome: '/accounts/deleted', archived: false });
  assert.deepEqual(Array.from(api.filteredHistoryThreads(), (thread) => thread.id), ['active-thread', 'shared-thread']);
});

test('the delete action invalidates old requests before IPC and force-refreshes every derived view after success', () => {
  const start = rendererSource.indexOf('async function deleteSelectedAccount()');
  const end = rendererSource.indexOf('async function cycleTheme()', start);
  const action = rendererSource.slice(start, end);
  const invalidation = action.indexOf('invalidateAllAccountDerivedRequests()');
  const deletion = action.indexOf('invoke("deleteAccount"');
  const clearing = action.indexOf('clearDeletedAccountDerivedState(account)');
  const reloadState = action.indexOf('await loadState()');
  assert.ok(invalidation >= 0 && invalidation < deletion, 'request generations must advance before deletion starts');
  assert.ok(clearing > deletion && clearing < reloadState, 'local deleted-account state is cleared before server state is rendered');
  assert.match(action, /Promise\.all\(\[\s*loadUsage\(false\),\s*loadQuota\(false\),\s*loadHistory\(false\),\s*\]\)/);
});
