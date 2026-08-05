const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const mainSource = fs.readFileSync(path.join(__dirname, '..', 'src', 'main.js'), 'utf8');

function section(startText, endText, from = 0) {
  const start = mainSource.indexOf(startText, from);
  assert.notEqual(start, -1, `missing section start: ${startText}`);
  const end = mainSource.indexOf(endText, start + startText.length);
  assert.notEqual(end, -1, `missing section end: ${endText}`);
  return mainSource.slice(start, end);
}

test('manager startup migrates metadata before configs and reports fatal startup errors', () => {
  const managerReady = section('app.whenReady().then(async () => {', '});\n  }', mainSource.indexOf('} else {'));
  const storeCreated = managerReady.indexOf('store = new AccountStore');
  const metadataMigrated = managerReady.indexOf('store.migrateAccountMetadata()');
  const tokenConfigsMigrated = managerReady.indexOf('store.migrateAccessTokenConfigs(accounts)');
  const controllerCreated = managerReady.indexOf('new PatGatewayController');
  assert.ok(storeCreated >= 0 && storeCreated < metadataMigrated);
  assert.ok(metadataMigrated < tokenConfigsMigrated);
  assert.ok(tokenConfigsMigrated < controllerCreated);
  assert.match(managerReady, /accounts\.some\(\(account\) => account\.authKind === 'access_token'\)/);
  assert.match(managerReady, /patGateway\.ensureRunning\(\)\.catch/);
  assert.match(mainSource, /showErrorBox\('Codex Account Manager 启动失败'/);
});

test('quota wiring uses the epoch-isolated cache without discarding a successful live report', () => {
  const quota = section('function loadQuota(', 'function installIpcHandlers()');
  assert.match(quota, /quotaSnapshotsPath:\s*store\.quotaSnapshotsPath/);
  assert.match(quota, /isCredentialStillActive:\s*quotaCredentialStillActive/);
  assert.match(quota, /signal:\s*controller\.signal/);
  assert.match(quota, /live:\s*input\.live !== false && retiringAccountIds\.size === 0/);
  assert.match(quota, /activeQuotaReads\.delete/);
  assert.match(quota, /await cli\.ensureAccountServices\(account\)/);
  assert.match(quota, /catch \{[\s\S]*row\.cacheWarning/);
});

test('account deletion retires and drains OAuth, quota, and terminal work before removing data', () => {
  const deletion = section("handleIpc('account:delete'", "handleIpc('account:import'");
  const retire = deletion.indexOf('retiringAccountIds.add');
  const oauth = deletion.indexOf('cancelOAuthDraftsBeforeAccountDeletion');
  const quota = deletion.indexOf('stopQuotaReadsForAccount');
  const terminal = deletion.indexOf('stopManagedTerminalSessions');
  const desktop = deletion.indexOf('stopManagedCodexApps');
  const remove = deletion.indexOf('store.removeAccount');
  assert.ok(retire >= 0 && retire < oauth);
  assert.ok(oauth < quota);
  assert.ok(quota < terminal && terminal < desktop && desktop < remove);
  assert.match(deletion, /finally\s*\{\s*retiringAccountIds\.delete/);
});

test('OAuth draft preparation is serialized and cancellation remains visible until child drain completes', () => {
  const cancellation = section('function cancelOAuthDraftNow', 'function cancelOAuthDraft(');
  assert.match(cancellation, /if \(state\.cancelTask\) return state\.cancelTask/);
  assert.ok(cancellation.indexOf('await state.session.cancel()') < cancellation.indexOf('cleanupOfficialOAuthDraft'));
  assert.ok(cancellation.indexOf('cleanupOfficialOAuthDraft') < cancellation.indexOf('oauthDrafts.delete'));
  assert.match(cancellation, /existsSync\(state\.draft\.pendingCodexHome\)/);

  const deletionDrain = section('async function cancelOAuthDraftsBeforeAccountDeletion', 'function assertOfficialOAuthData');
  assert.match(deletionDrain, /\.\.\.oauthDrafts\.keys\(\)/);
  assert.doesNotMatch(deletionDrain, /editingId/);

  const prepare = section("handleIpc('account:oauth-draft-prepare'", "handleIpc('account:oauth-draft-commit'");
  assert.match(prepare, /runAccountActivation/);
  assert.match(prepare, /state\.cancelTask/);
  assert.match(prepare, /\.catch\(\(\) => \{[\s\S]*cancelOAuthDraft\(draftId\)\.then/);
  assert.doesNotMatch(prepare, /\.catch\(\(\) => \{[\s\S]*oauthDrafts\.delete\(draftId\)/);
});

test('CLI login and status operations share the deletion activation queue', () => {
  for (const [start, end] of [
    ["handleIpc('account:login'", "handleIpc('account:oauth-draft-prepare'"],
    ["handleIpc('account:status'", "handleIpc('account:status-all'"],
    ["handleIpc('account:status-all'", "handleIpc('account:launch-terminal'"],
  ]) {
    const handler = section(start, end);
    assert.match(handler, /runAccountActivation/);
  }
  const login = section("handleIpc('account:login'", "handleIpc('account:oauth-draft-prepare'");
  assert.ok(login.indexOf('findAccount') < login.indexOf('cli.login'));
  assert.ok(login.indexOf('cli.login') < login.indexOf('store.activateCredential'));
  for (const channel of ['history:archive', 'history:delete']) {
    const handlerStart = mainSource.indexOf(`handleIpc('${channel}'`);
    assert.notEqual(handlerStart, -1);
    assert.match(mainSource.slice(handlerStart, handlerStart + 220), /runAccountActivation/);
  }
});

test('credential activation follows successful PAT login and only fresh OAuth commits', () => {
  const login = section("handleIpc('account:login'", "handleIpc('account:oauth-draft-prepare'");
  assert.ok(login.indexOf('await cli.login') < login.indexOf('store.activateCredential(account.id)'));
  assert.ok(login.indexOf('store.activateCredential(account.id)') < login.indexOf('loginStatuses.set'));

  const oauth = section("handleIpc('account:oauth-draft-commit'", "handleIpc('account:oauth-draft-cancel'");
  assert.ok(oauth.indexOf('state.cancelTask') < oauth.indexOf('commitOfficialOAuthDraft'));
  const reuseStart = oauth.indexOf('if (state.reuseExisting)');
  const freshStart = oauth.indexOf('} else {', reuseStart);
  const reuseBranch = oauth.slice(reuseStart, freshStart);
  const freshBranch = oauth.slice(freshStart);
  assert.doesNotMatch(reuseBranch, /activateCredential/);
  assert.ok(freshBranch.indexOf('commitOfficialOAuthDraft') < freshBranch.indexOf('activateCredential'));
});

test('gateway daemon remains UI-free and reads only proxy settings', () => {
  const runner = section('async function runPatGatewayDaemon()', 'if (isPatGatewayDaemon)');
  assert.match(runner, /settings\.json/);
  assert.match(runner, /loadGatewaySecret\(userDataPath\)/);
  assert.match(runner, /gatewaySecret/);
  assert.match(runner, /ensureListening/);
  assert.doesNotMatch(runner, /new AccountStore|createWindow|installIpcHandlers/);
  assert.ok(mainSource.indexOf('if (isPatGatewayDaemon)') < mainSource.indexOf('app.requestSingleInstanceLock()'));
});
