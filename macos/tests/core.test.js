const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
  AccountStore,
  DELETE_TOMB_DIRECTORY_PATTERN,
  isOfficialOAuthAuthFile,
} = require('../src/services/account-store');
const { _test } = require('../src/services/usage-stats');

function writeOAuthAuth(codexHome, suffix = '') {
  fs.writeFileSync(path.join(codexHome, 'auth.json'), JSON.stringify({
    auth_mode: 'chatgpt',
    tokens: {
      id_token: `test-id${suffix}`,
      access_token: `test-access${suffix}`,
      refresh_token: `test-refresh${suffix}`,
    },
  }));
}

async function main() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-test-'));
  try {
    const store = new AccountStore(path.join(root, 'user-data'), {
      accountHomesRoot: path.join(root, 'account-homes'),
    });
    const account = store.saveAccount({
      name: '测试账号',
      authKind: 'access_token',
    });
    assert.equal(account.name, '测试账号');
    assert.equal(store.loadAccounts().length, 1);
    const manifest = fs.readFileSync(store.accountsPath, 'utf8');
    assert.doesNotMatch(manifest, /sk-test-secret|accessToken|"apiKey"/i);
    const config = fs.readFileSync(path.join(account.codexHome, 'config.toml'), 'utf8');
    assert.doesNotMatch(config, /\[windows\]|windows_wsl/i);
    assert.match(config, /model = "gpt-5\.6"/);
    assert.ok(account.codexHome.startsWith(path.join(root, 'account-homes') + path.sep));

    store.updateQuotaProfile(account.id, {
      quotaPrimaryWindowMinutes: 300,
      quotaSecondaryWindowMinutes: 10_080,
    });
    store.updateQuotaProfile(account.id, {
      quotaPrimaryWindowMinutes: null,
      quotaSecondaryWindowMinutes: null,
    });
    const nullQuotaProfile = store.loadAccounts().find((item) => item.id === account.id);
    assert.equal(nullQuotaProfile.quotaPrimaryWindowMinutes, null);
    assert.equal(nullQuotaProfile.quotaSecondaryWindowMinutes, null);

    const credentialStore = new AccountStore(path.join(root, 'credential-data'), {
      accountHomesRoot: path.join(root, 'credential-homes'),
    });
    const credentialAccount = credentialStore.saveAccount({
      name: '凭据版本账号',
      authKind: 'access_token',
    });
    fs.writeFileSync(path.join(credentialAccount.codexHome, 'auth.json'), JSON.stringify({
      OPENAI_API_KEY: 'at-test-credential-version',
    }));
    fs.writeFileSync(credentialStore.quotaSnapshotsPath, `${JSON.stringify({
      version: 1,
      entries: [{
        accountId: credentialAccount.id,
        credentialEpoch: credentialAccount.credentialEpoch,
        snapshot: { observedAt: '2026-08-05T00:00:00Z', primary: { usedPercent: 10, windowMinutes: 300 } },
      }],
    })}\n`);
    const activatedCredential = credentialStore.activateCredential(credentialAccount.id, {
      activatedAt: '2026-08-05T01:00:00Z',
    });
    assert.notEqual(activatedCredential.credentialEpoch, credentialAccount.credentialEpoch);
    assert.equal(activatedCredential.credentialActivatedAt, '2026-08-05T01:00:00.000Z');
    assert.match(activatedCredential.credentialFingerprint, /^[a-f0-9]{64}$/);
    assert.equal(fs.existsSync(credentialStore.quotaSnapshotsPath), false, 'credential activation removes old quota cache');

    const migrationStore = new AccountStore(path.join(root, 'credential-migration-data'), {
      accountHomesRoot: path.join(root, 'credential-migration-homes'),
    });
    const migrationAccount = migrationStore.saveAccount({ name: '旧凭据元数据', authKind: 'access_token' });
    const legacyManifest = JSON.parse(fs.readFileSync(migrationStore.accountsPath, 'utf8'));
    delete legacyManifest[0].credentialEpoch;
    delete legacyManifest[0].credentialActivatedAt;
    delete legacyManifest[0].credentialFingerprint;
    fs.writeFileSync(migrationStore.accountsPath, `${JSON.stringify(legacyManifest, null, 2)}\n`);
    fs.writeFileSync(migrationStore.accountsBackupPath, `${JSON.stringify(legacyManifest, null, 2)}\n`);
    fs.writeFileSync(migrationStore.quotaSnapshotsPath, `${JSON.stringify({
      version: 1,
      entries: [{
        accountId: migrationAccount.id,
        credentialEpoch: 'legacy-epoch-000001',
        snapshot: { observedAt: '2026-08-04T00:00:00Z', primary: { usedPercent: 90, windowMinutes: 300 } },
      }],
    })}\n`);
    const migratedOnce = migrationStore.migrateAccountMetadata()[0];
    assert.match(migratedOnce.credentialEpoch, /^[A-Za-z0-9-]{16,80}$/);
    assert.ok(Date.parse(migratedOnce.credentialActivatedAt));
    assert.equal(migratedOnce.credentialFingerprint, null);
    assert.equal(fs.existsSync(migrationStore.quotaSnapshotsPath), false);
    const migratedTwice = migrationStore.migrateAccountMetadata()[0];
    assert.equal(migratedTwice.credentialEpoch, migratedOnce.credentialEpoch, 'migration persists a stable epoch');
    assert.equal(migratedTwice.credentialActivatedAt, migratedOnce.credentialActivatedAt);

    const compatibleStore = new AccountStore(path.join(root, 'compatible-wire-data'), {
      accountHomesRoot: path.join(root, 'compatible-wire-homes'),
    });
    assert.throws(() => compatibleStore.saveAccount({
      name: '旧 Chat wire',
      authKind: 'compatible_api',
      apiBaseUrl: 'https://api.example.com/v1',
      apiWireApi: 'chat',
      apiKey: 'sk-test-compatible-wire',
    }), /仅支持 responses/);
    const compatibleAccount = compatibleStore.saveAccount({
      name: 'Responses wire',
      authKind: 'compatible_api',
      apiBaseUrl: 'https://api.example.com/v1',
      apiWireApi: 'responses',
      apiKey: 'sk-test-compatible-wire',
    });
    const compatibleConfigPath = path.join(compatibleAccount.codexHome, 'config.toml');
    assert.match(fs.readFileSync(compatibleConfigPath, 'utf8'), /wire_api\s*=\s*"responses"/);
    const legacyCompatibleManifest = JSON.parse(fs.readFileSync(compatibleStore.accountsPath, 'utf8'));
    legacyCompatibleManifest[0].apiWireApi = 'chat';
    fs.writeFileSync(compatibleStore.accountsPath, `${JSON.stringify(legacyCompatibleManifest, null, 2)}\n`);
    fs.writeFileSync(compatibleStore.accountsBackupPath, `${JSON.stringify(legacyCompatibleManifest, null, 2)}\n`);
    fs.writeFileSync(compatibleConfigPath, fs.readFileSync(compatibleConfigPath, 'utf8').replace('wire_api = "responses"', 'wire_api = "chat"'));
    compatibleStore.migrateAccountMetadata();
    assert.equal(compatibleStore.loadAccounts()[0].apiWireApi, 'responses');
    assert.match(fs.readFileSync(compatibleConfigPath, 'utf8'), /wire_api\s*=\s*"responses"/);
    assert.doesNotMatch(fs.readFileSync(compatibleConfigPath, 'utf8'), /wire_api\s*=\s*"chat"/);

    const stalePending = path.join(store.accountHomesRoot, '.pending-oauth-st1234');
    const freshPending = path.join(store.accountHomesRoot, '.pending-oauth-fr1234');
    const pendingPrefixFile = path.join(store.accountHomesRoot, '.pending-oauth-fi1234');
    const nearPendingLong = path.join(store.accountHomesRoot, '.pending-oauth-st12345');
    const nearPendingInvalid = path.join(store.accountHomesRoot, '.pending-oauth-ab_123');
    fs.mkdirSync(stalePending);
    fs.mkdirSync(freshPending);
    fs.mkdirSync(nearPendingLong);
    fs.mkdirSync(nearPendingInvalid);
    fs.writeFileSync(path.join(stalePending, 'auth.json'), '{"stale":true}');
    fs.writeFileSync(pendingPrefixFile, 'keep');
    const cleanupNow = Date.now() + 60_000;
    fs.utimesSync(freshPending, new Date(cleanupNow), new Date(cleanupNow));
    const outsidePendingTarget = path.join(root, 'outside-pending-target');
    const pendingSymlink = path.join(store.accountHomesRoot, '.pending-oauth-sy1234');
    fs.mkdirSync(outsidePendingTarget);
    let pendingSymlinkCreated = false;
    try {
      fs.symlinkSync(outsidePendingTarget, pendingSymlink, process.platform === 'win32' ? 'junction' : 'dir');
      pendingSymlinkCreated = true;
    } catch {
      // Some Windows test hosts do not grant symbolic-link privileges.
    }
    assert.equal(store.cleanupStaleOfficialOAuthDrafts({ maxAgeMs: 30_000, now: cleanupNow }), 1);
    assert.equal(fs.existsSync(stalePending), false, 'expired real draft directories are removed');
    assert.equal(fs.existsSync(freshPending), true, 'fresh draft directories are preserved');
    assert.equal(fs.existsSync(pendingPrefixFile), true, 'prefix-matching files are never removed');
    assert.equal(fs.existsSync(nearPendingLong), true, 'near-match names with a non-six-character suffix are preserved');
    assert.equal(fs.existsSync(nearPendingInvalid), true, 'near-match names with invalid suffix characters are preserved');
    if (pendingSymlinkCreated) {
      assert.equal(fs.lstatSync(pendingSymlink).isSymbolicLink(), true);
      assert.equal(fs.existsSync(outsidePendingTarget), true, 'draft cleanup never follows symbolic links');
    }
    const startupCleanupHomes = path.join(root, 'startup-cleanup-homes');
    const startupStaleDraft = path.join(startupCleanupHomes, '.pending-oauth-up1234');
    fs.mkdirSync(startupStaleDraft, { recursive: true });
    new AccountStore(path.join(root, 'startup-cleanup-data'), {
      accountHomesRoot: startupCleanupHomes,
      oauthDraftTtlMs: 0,
    });
    assert.equal(
      fs.existsSync(startupStaleDraft),
      false,
      'AccountStore startup removes expired pending OAuth directories',
    );

    const tombNameFor = (id) => `.delete-tomb-${id}-${crypto.randomUUID()}`;

    const crashBeforeData = path.join(root, 'tomb-crash-before-data');
    const crashBeforeHomes = path.join(root, 'tomb-crash-before-homes');
    const crashBeforeStore = new AccountStore(crashBeforeData, { accountHomesRoot: crashBeforeHomes });
    const crashBeforeAccount = crashBeforeStore.saveAccount({ name: '清单提交前崩溃', authKind: 'access_token' });
    const crashBeforeTomb = path.join(crashBeforeHomes, tombNameFor(crashBeforeAccount.id));
    fs.renameSync(crashBeforeAccount.codexHome, crashBeforeTomb);
    const crashBeforeRestart = new AccountStore(crashBeforeData, { accountHomesRoot: crashBeforeHomes });
    assert.equal(fs.existsSync(crashBeforeAccount.codexHome), true, 'manifest still containing the id restores its tomb');
    assert.equal(fs.existsSync(crashBeforeTomb), false);
    assert.equal(crashBeforeRestart.lastDeleteTombReconciliation.restored[0].accountId, crashBeforeAccount.id);

    const crashAfterData = path.join(root, 'tomb-crash-after-data');
    const crashAfterHomes = path.join(root, 'tomb-crash-after-homes');
    const crashAfterStore = new AccountStore(crashAfterData, { accountHomesRoot: crashAfterHomes });
    const crashAfterAccount = crashAfterStore.saveAccount({ name: '清单提交后崩溃', authKind: 'access_token' });
    crashAfterStore.setCurrentAccount(crashAfterAccount.id);
    fs.writeFileSync(crashAfterStore.quotaSnapshotsPath, `${JSON.stringify({
      version: 1,
      entries: [{
        accountId: crashAfterAccount.id,
        credentialEpoch: crashAfterAccount.credentialEpoch,
        snapshot: {},
      }],
    })}\n`);
    const crashAfterTomb = path.join(crashAfterHomes, tombNameFor(crashAfterAccount.id));
    fs.renameSync(crashAfterAccount.codexHome, crashAfterTomb);
    crashAfterStore.writeAccountsManifest([]);
    const crashAfterRestart = new AccountStore(crashAfterData, { accountHomesRoot: crashAfterHomes });
    assert.equal(fs.existsSync(crashAfterTomb), false, 'manifest without the id completes physical deletion');
    assert.equal(crashAfterRestart.lastDeleteTombReconciliation.deleted[0].accountId, crashAfterAccount.id);
    assert.equal(crashAfterRestart.loadSettings().currentAccountId, null);
    assert.equal(crashAfterRestart.loadUsageSwitches()[0].accountId, '');
    assert.equal(crashAfterRestart.loadUsageSwitches()[0].accountName, '');
    assert.equal(fs.existsSync(crashAfterRestart.quotaSnapshotsPath), false);

    const crashCleanupFailureData = path.join(root, 'tomb-cleanup-retry-data');
    const crashCleanupFailureHomes = path.join(root, 'tomb-cleanup-retry-homes');
    const crashCleanupFailureStore = new AccountStore(crashCleanupFailureData, {
      accountHomesRoot: crashCleanupFailureHomes,
    });
    const crashCleanupFailureAccount = crashCleanupFailureStore.saveAccount({
      name: '删除元数据待重试',
      authKind: 'access_token',
    });
    const crashCleanupFailureTomb = path.join(
      crashCleanupFailureHomes,
      tombNameFor(crashCleanupFailureAccount.id),
    );
    fs.renameSync(crashCleanupFailureAccount.codexHome, crashCleanupFailureTomb);
    crashCleanupFailureStore.writeAccountsManifest([]);
    fs.writeFileSync(crashCleanupFailureStore.settingsPath, '{broken-settings');
    const crashCleanupFailureRestart = new AccountStore(crashCleanupFailureData, {
      accountHomesRoot: crashCleanupFailureHomes,
    });
    assert.equal(fs.existsSync(crashCleanupFailureTomb), true, 'metadata cleanup failure retains the tomb for retry');
    assert.equal(
      crashCleanupFailureRestart.lastDeleteTombReconciliation.retained[0].reason,
      'metadata-cleanup-or-delete-failed',
    );

    const missingManifestData = path.join(root, 'tomb-missing-manifest-data');
    const missingManifestHomes = path.join(root, 'tomb-missing-manifest-homes');
    fs.mkdirSync(missingManifestHomes, { recursive: true });
    const missingManifestTomb = path.join(missingManifestHomes, tombNameFor('missing-account-01'));
    fs.mkdirSync(missingManifestTomb);
    const missingManifestRestart = new AccountStore(missingManifestData, { accountHomesRoot: missingManifestHomes });
    assert.equal(missingManifestRestart.lastDeleteTombReconciliation.status, 'manifest-missing');
    assert.equal(fs.existsSync(missingManifestTomb), true, 'a missing manifest never authorizes tomb deletion');

    const corruptTombData = path.join(root, 'tomb-corrupt-manifest-data');
    const corruptTombHomes = path.join(root, 'tomb-corrupt-manifest-homes');
    fs.mkdirSync(corruptTombData, { recursive: true });
    fs.mkdirSync(corruptTombHomes, { recursive: true });
    const corruptManifestTomb = path.join(corruptTombHomes, tombNameFor('corrupt-account-01'));
    fs.mkdirSync(corruptManifestTomb);
    fs.writeFileSync(path.join(corruptTombData, 'accounts.json'), '{broken-json');
    const corruptTombRestart = new AccountStore(corruptTombData, { accountHomesRoot: corruptTombHomes });
    assert.equal(corruptTombRestart.lastDeleteTombReconciliation.status, 'manifest-unreadable');
    assert.equal(fs.existsSync(corruptManifestTomb), true, 'a corrupt manifest preserves every strict tomb');

    const conflictData = path.join(root, 'tomb-conflict-data');
    const conflictHomes = path.join(root, 'tomb-conflict-homes');
    const conflictStore = new AccountStore(conflictData, { accountHomesRoot: conflictHomes });
    const conflictAccount = conflictStore.saveAccount({ name: '原目录冲突', authKind: 'access_token' });
    const conflictTomb = path.join(conflictHomes, tombNameFor(conflictAccount.id));
    fs.mkdirSync(conflictTomb);
    const conflictRestart = new AccountStore(conflictData, { accountHomesRoot: conflictHomes });
    assert.equal(fs.existsSync(conflictAccount.codexHome), true);
    assert.equal(fs.existsSync(conflictTomb), true);
    assert.equal(conflictRestart.lastDeleteTombReconciliation.retained[0].reason, 'home-conflict');

    const duplicateData = path.join(root, 'tomb-duplicate-data');
    const duplicateHomes = path.join(root, 'tomb-duplicate-homes');
    const duplicateSeed = new AccountStore(duplicateData, { accountHomesRoot: duplicateHomes });
    duplicateSeed.writeAccountsManifest([]);
    const duplicateId = 'duplicate-account-01';
    const duplicateTombs = [
      path.join(duplicateHomes, tombNameFor(duplicateId)),
      path.join(duplicateHomes, tombNameFor(duplicateId)),
    ];
    for (const tombPath of duplicateTombs) fs.mkdirSync(tombPath);
    const nearMatchTomb = path.join(duplicateHomes, `.delete-tomb-${duplicateId}-not-a-uuid`);
    fs.mkdirSync(nearMatchTomb);
    const duplicateRestart = new AccountStore(duplicateData, { accountHomesRoot: duplicateHomes });
    assert.equal(duplicateRestart.lastDeleteTombReconciliation.retained.length, 2);
    assert.ok(duplicateRestart.lastDeleteTombReconciliation.retained.every((entry) => entry.reason === 'duplicate-tombs'));
    assert.ok(duplicateTombs.every((tombPath) => fs.existsSync(tombPath)), 'same-id tombs are never resolved arbitrarily');
    assert.equal(fs.existsSync(nearMatchTomb), true, 'near-match tomb names are ignored');

    const deletionStore = new AccountStore(path.join(root, 'deletion-transaction-data'), {
      accountHomesRoot: path.join(root, 'deletion-transaction-homes'),
    });
    const deletionAccount = deletionStore.saveAccount({
      name: '删除事务账号',
      authKind: 'access_token',
    });
    fs.writeFileSync(path.join(deletionAccount.codexHome, 'preserve-on-rollback.txt'), 'credential-data');
    deletionStore.setCurrentAccount(deletionAccount.id);
    fs.writeFileSync(deletionStore.quotaSnapshotsPath, `${JSON.stringify({
      version: 1,
      entries: [
        { accountId: deletionAccount.id, credentialEpoch: deletionAccount.credentialEpoch, snapshot: {} },
        { accountId: 'keep-account', credentialEpoch: 'keep-epoch-0000001', snapshot: {} },
      ],
    })}\n`);
    const deletionSnapshots = {
      accounts: fs.readFileSync(deletionStore.accountsPath),
      accountsBackup: fs.readFileSync(deletionStore.accountsBackupPath),
      switches: fs.readFileSync(deletionStore.switchesPath),
      settings: fs.readFileSync(deletionStore.settingsPath),
      quota: fs.readFileSync(deletionStore.quotaSnapshotsPath),
    };
    const realSaveDeletionSettings = deletionStore.saveSettings.bind(deletionStore);
    deletionStore.saveSettings = () => { throw new Error('injected settings write failure'); };
    assert.throws(
      () => deletionStore.removeAccount(deletionAccount.id),
      /injected settings write failure/,
    );
    deletionStore.saveSettings = realSaveDeletionSettings;
    assert.equal(fs.readFileSync(path.join(deletionAccount.codexHome, 'preserve-on-rollback.txt'), 'utf8'), 'credential-data');
    assert.deepEqual(fs.readFileSync(deletionStore.accountsPath), deletionSnapshots.accounts);
    assert.deepEqual(fs.readFileSync(deletionStore.accountsBackupPath), deletionSnapshots.accountsBackup);
    assert.deepEqual(fs.readFileSync(deletionStore.switchesPath), deletionSnapshots.switches);
    assert.deepEqual(fs.readFileSync(deletionStore.settingsPath), deletionSnapshots.settings);
    assert.deepEqual(fs.readFileSync(deletionStore.quotaSnapshotsPath), deletionSnapshots.quota);
    assert.equal(
      fs.readdirSync(deletionStore.accountHomesRoot).some((name) => name.startsWith('.delete-tomb-')),
      false,
      'failed deletion restores the account home instead of leaving a tomb',
    );
    deletionStore.removeAccount(deletionAccount.id);
    assert.equal(deletionStore.loadAccounts().length, 0);
    assert.equal(fs.existsSync(deletionAccount.codexHome), false, 'successful deletion physically removes the account home');
    const deletionQuotaCache = JSON.parse(fs.readFileSync(deletionStore.quotaSnapshotsPath, 'utf8'));
    assert.deepEqual(deletionQuotaCache.entries.map((entry) => entry.accountId), ['keep-account']);
    assert.equal(
      fs.readdirSync(deletionStore.accountHomesRoot).some((name) => name.startsWith('.delete-tomb-')),
      false,
      'successful deletion does not leave a hidden soft-delete directory',
    );

    const tombCleanupStore = new AccountStore(path.join(root, 'tomb-cleanup-data'), {
      accountHomesRoot: path.join(root, 'tomb-cleanup-homes'),
    });
    const tombCleanupAccount = tombCleanupStore.saveAccount({
      name: '清理失败仍删除账号',
      authKind: 'access_token',
    });
    tombCleanupStore.setCurrentAccount(tombCleanupAccount.id);
    const realRmSync = fs.rmSync;
    let tombCleanupResult;
    fs.rmSync = (target, options) => {
      if (path.basename(String(target)).startsWith('.delete-tomb-')) {
        throw new Error('injected tomb cleanup failure');
      }
      return realRmSync(target, options);
    };
    try {
      tombCleanupResult = tombCleanupStore.removeAccount(tombCleanupAccount.id);
    } finally {
      fs.rmSync = realRmSync;
    }
    assert.match(tombCleanupResult.cleanupWarning, /账号已删除/);
    assert.equal(tombCleanupStore.loadAccounts().length, 0, 'post-commit tomb cleanup failure never revives the account');
    assert.equal(tombCleanupStore.loadSettings().currentAccountId, null);
    assert.equal(fs.existsSync(tombCleanupAccount.codexHome), false);
    const retainedTombs = fs.readdirSync(tombCleanupStore.accountHomesRoot)
      .filter((name) => name.startsWith('.delete-tomb-'));
    assert.equal(retainedTombs.length, 1, 'failed physical cleanup retains one strictly scoped tomb');
    assert.match(retainedTombs[0], DELETE_TOMB_DIRECTORY_PATTERN);
    realRmSync(path.join(tombCleanupStore.accountHomesRoot, retainedTombs[0]), { recursive: true, force: false });

    const corruptManifestStore = new AccountStore(path.join(root, 'corrupt-manifest-data'), {
      accountHomesRoot: path.join(root, 'corrupt-manifest-homes'),
    });
    const knownGoodAccount = corruptManifestStore.saveAccount({
      name: '清单备份账号',
      authKind: 'access_token',
    });
    const knownGoodBackup = fs.readFileSync(corruptManifestStore.accountsBackupPath, 'utf8');
    assert.equal(JSON.parse(knownGoodBackup)[0].id, knownGoodAccount.id);
    fs.writeFileSync(corruptManifestStore.accountsPath, '{broken-json', 'utf8');
    const corruptManifestBytes = fs.readFileSync(corruptManifestStore.accountsPath);
    assert.throws(
      () => corruptManifestStore.loadAccounts(),
      (error) => error.code === 'ACCOUNT_MANIFEST_UNREADABLE' && /accounts\.json\.bak/.test(error.message),
    );
    assert.throws(
      () => corruptManifestStore.saveAccount({ name: '绝不能覆盖', authKind: 'access_token' }),
      (error) => error.code === 'ACCOUNT_MANIFEST_UNREADABLE' && /accounts\.json\.bak/.test(error.message),
    );
    assert.deepEqual(
      fs.readFileSync(corruptManifestStore.accountsPath),
      corruptManifestBytes,
      'a corrupt account manifest is never replaced with a new one-account array',
    );
    assert.equal(fs.readFileSync(corruptManifestStore.accountsBackupPath, 'utf8'), knownGoodBackup);

    const manifestBeforeDraft = fs.readFileSync(store.accountsPath, 'utf8');
    assert.throws(() => store.saveAccount({
      name: '未登录的官方账号',
      authKind: 'official_oauth',
    }));
    assert.equal(store.loadAccounts().length, 1, 'official OAuth cannot be added before login succeeds');
    assert.equal(fs.readFileSync(store.accountsPath, 'utf8'), manifestBeforeDraft);

    const draft = store.prepareOfficialOAuthDraft({
      name: 'ChatGPT 官方账号',
      authKind: 'official_oauth',
    });
    assert.equal(store.loadAccounts().length, 1, 'preparing a draft must not add an account');
    assert.equal(fs.readFileSync(store.accountsPath, 'utf8'), manifestBeforeDraft);
    assert.ok(path.basename(draft.pendingCodexHome).startsWith('.pending-oauth-'));
    assert.equal(path.dirname(draft.pendingCodexHome), path.join(root, 'account-homes'));
    assert.equal(fs.existsSync(draft.pendingCodexHome), true);
    assert.equal(fs.existsSync(path.join(draft.pendingCodexHome, 'auth.json')), false);
    const pendingConfig = fs.readFileSync(path.join(draft.pendingCodexHome, 'config.toml'), 'utf8');
    assert.match(pendingConfig, /cli_auth_credentials_store\s*=\s*"file"/);
    assert.match(pendingConfig, /forced_login_method\s*=\s*"chatgpt"/);
    assert.doesNotMatch(pendingConfig, /model_provider|model_providers|backend-api|codex_account_manager|8317/i);

    assert.throws(() => store.commitOfficialOAuthDraft(draft, {
      name: 'ChatGPT 官方账号',
      authKind: 'official_oauth',
    }));
    assert.equal(store.loadAccounts().length, 1);
    fs.writeFileSync(path.join(draft.pendingCodexHome, 'auth.json'), JSON.stringify({
      auth_mode: 'chatgpt',
      tokens: { id_token: 'test-id', access_token: 'test-access' },
    }));
    assert.throws(() => store.commitOfficialOAuthDraft(draft, {
      name: 'ChatGPT 官方账号',
      authKind: 'official_oauth',
    }));
    assert.equal(store.loadAccounts().length, 1, 'incomplete credentials must not enter accounts.json');

    writeOAuthAuth(draft.pendingCodexHome);
    let oauthAccount = store.commitOfficialOAuthDraft(draft, {
      name: 'ChatGPT 官方账号',
      authKind: 'official_oauth',
    });
    oauthAccount = store.activateCredential(oauthAccount.id, { activatedAt: '2026-08-05T02:00:00Z' });
    assert.equal(oauthAccount.authKind, 'official_oauth');
    assert.match(oauthAccount.credentialFingerprint, /^[a-f0-9]{64}$/);
    assert.equal(oauthAccount.credentialActivatedAt, '2026-08-05T02:00:00.000Z');
    assert.equal(store.loadAccounts().length, 2);
    assert.equal(fs.existsSync(draft.pendingCodexHome), false, 'the pending directory is atomically moved on commit');
    assert.equal(fs.existsSync(oauthAccount.codexHome), true);
    assert.equal(isOfficialOAuthAuthFile(path.join(oauthAccount.codexHome, 'auth.json')), true);
    const oauthConfig = fs.readFileSync(path.join(oauthAccount.codexHome, 'config.toml'), 'utf8');
    assert.match(oauthConfig, /cli_auth_credentials_store\s*=\s*"file"/);
    assert.match(oauthConfig, /forced_login_method\s*=\s*"chatgpt"/);
    assert.doesNotMatch(oauthConfig, /model_provider|model_providers|backend-api|codex_account_manager|8317/i);
    assert.doesNotMatch(oauthConfig, /(?:^|\n)\s*(?:model|review_model|model_reasoning_effort)\s*=/i);

    const editDraft = store.prepareOfficialOAuthDraft({
      name: 'ChatGPT 官方账号（改名）',
      authKind: 'official_oauth',
    }, oauthAccount.id);
    assert.equal(store.loadAccounts().find((item) => item.id === oauthAccount.id).name, 'ChatGPT 官方账号');
    writeOAuthAuth(editDraft.pendingCodexHome, '-updated');
    fs.writeFileSync(store.quotaSnapshotsPath, `${JSON.stringify({
      version: 1,
      entries: [{
        accountId: oauthAccount.id,
        credentialEpoch: oauthAccount.credentialEpoch,
        snapshot: { observedAt: '2026-08-05T02:30:00Z', primary: { usedPercent: 45, windowMinutes: 300 } },
      }],
    })}\n`);
    let updatedOAuth = store.commitOfficialOAuthDraft(editDraft, {
      name: 'ChatGPT 官方账号（改名）',
      authKind: 'official_oauth',
    });
    updatedOAuth = store.activateCredential(updatedOAuth.id, { activatedAt: '2026-08-05T03:00:00Z' });
    assert.equal(updatedOAuth.id, oauthAccount.id);
    assert.notEqual(updatedOAuth.credentialEpoch, oauthAccount.credentialEpoch);
    assert.notEqual(updatedOAuth.credentialFingerprint, oauthAccount.credentialFingerprint);
    assert.equal(updatedOAuth.credentialActivatedAt, '2026-08-05T03:00:00.000Z');
    assert.equal(fs.existsSync(store.quotaSnapshotsPath), false, 'OAuth re-login cannot reuse the prior credential snapshot');
    assert.equal(updatedOAuth.codexHome, oauthAccount.codexHome);
    assert.equal(updatedOAuth.name, 'ChatGPT 官方账号（改名）');
    assert.match(fs.readFileSync(path.join(updatedOAuth.codexHome, 'auth.json'), 'utf8'), /test-refresh-updated/);
    assert.equal(store.canReuseOfficialOAuth({
      name: 'ChatGPT 官方账号（再次改名）',
      authKind: 'official_oauth',
    }, updatedOAuth.id), true);

    const cancelledDraft = store.prepareOfficialOAuthDraft({
      name: '取消的官方账号',
      authKind: 'official_oauth',
    });
    assert.equal(fs.existsSync(cancelledDraft.pendingCodexHome), true);
    store.cleanupOfficialOAuthDraft(cancelledDraft.pendingCodexHome);
    assert.equal(fs.existsSync(cancelledDraft.pendingCodexHome), false);
    assert.equal(store.loadAccounts().some((item) => item.name === '取消的官方账号'), false);

    const importedCount = store.importAccounts([
      { name: '导入的官方账号', authKind: 'official_oauth' },
    ]);
    assert.equal(importedCount, 0, 'OAuth manifests without credentials must be skipped');
    assert.equal(store.loadAccounts().some((item) => item.name === '导入的官方账号'), false);

    const persistedManifest = fs.readFileSync(store.accountsPath, 'utf8');
    assert.doesNotMatch(persistedManifest, /test-access|test-refresh|"tokens"/i);

    const rendererHtml = fs.readFileSync(path.join(__dirname, '..', 'src', 'renderer.html'), 'utf8');
    const rendererJs = fs.readFileSync(path.join(__dirname, '..', 'src', 'renderer.js'), 'utf8');
    const cliSource = fs.readFileSync(path.join(__dirname, '..', 'src', 'services', 'codex-cli.js'), 'utf8');
    const mainSource = fs.readFileSync(path.join(__dirname, '..', 'src', 'main.js'), 'utf8');
    const preloadSource = fs.readFileSync(path.join(__dirname, '..', 'src', 'preload.js'), 'utf8');
    const setCurrentHandler = mainSource.slice(
      mainSource.indexOf("handleIpc('account:set-current'"),
      mainSource.indexOf("handleIpc('account:login'"),
    );
    const desktopLaunchHandler = mainSource.slice(
      mainSource.indexOf("handleIpc('account:launch-codex-app'"),
      mainSource.indexOf("handleIpc('usage:get'"),
    );
    const deleteAccountHandler = mainSource.slice(
      mainSource.indexOf("handleIpc('account:delete'"),
      mainSource.indexOf("handleIpc('account:import'"),
    );
    const restoreThemeAction = rendererJs.slice(
      rendererJs.indexOf('async function restoreOfficialCodexTheme()'),
      rendererJs.indexOf('function populateCustomThemeForm('),
    );
    const applyThemeMain = mainSource.slice(
      mainSource.indexOf('function applyCodexTheme('),
      mainSource.indexOf('function restoreCodexTheme('),
    );
    const restoreThemeMain = mainSource.slice(
      mainSource.indexOf('function restoreCodexTheme('),
      mainSource.indexOf('function saveCustomTheme('),
    );
    const saveCustomThemeMain = mainSource.slice(
      mainSource.indexOf('function saveCustomTheme('),
      mainSource.indexOf('function loadQuota('),
    );
    const reapplyAccountAction = rendererJs.slice(
      rendererJs.indexOf('async function setSelectedAsCurrent()'),
      rendererJs.indexOf('async function launchAccount('),
    );
    const deleteAccountAction = rendererJs.slice(
      rendererJs.indexOf('async function deleteSelectedAccount()'),
      rendererJs.indexOf('async function cycleTheme()'),
    );
    const secondInstanceStart = mainSource.indexOf("app.on('second-instance'");
    const secondInstanceHandler = mainSource.slice(
      secondInstanceStart,
      mainSource.indexOf('app.whenReady()', secondInstanceStart),
    );
    const daemonStart = mainSource.indexOf('if (isPatGatewayDaemon)');
    const daemonBranch = mainSource.slice(daemonStart, mainSource.indexOf('} else {', daemonStart));
    const daemonRunner = mainSource.slice(
      mainSource.indexOf('async function runPatGatewayDaemon()'),
      daemonStart,
    );
    const beforeQuitHandler = mainSource.slice(mainSource.indexOf("app.on('before-quit'"));
    assert.match(rendererHtml, /option value="official_oauth"/);
    assert.match(rendererHtml, /id="oauthLoginButton"/);
    assert.match(rendererHtml, /id="oauthLoginStatus"/);
    assert.match(rendererHtml, /生成登录链接/);
    assert.match(rendererHtml, /应用内置的官方 Codex CLI/);
    assert.doesNotMatch(rendererHtml, /option value="chat"/);
    assert.doesNotMatch(rendererHtml, /deviceAuthPanel|deviceAuthCode|deviceAuthUrl|一次性设备码/);
    assert.match(rendererJs, /prepareOAuthDraft/);
    assert.match(rendererJs, /commitOAuthDraft/);
    assert.match(rendererJs, /cancelOAuthDraft/);
    assert.match(rendererJs, /onOAuthDraftCompleted/);
    assert.match(rendererJs, /oauthDraftId/);
    assert.match(rendererJs, /oauthVerified/);
    assert.doesNotMatch(rendererJs, /activeDeviceLoginAccountId|deviceAuth|copyDeviceCode|copyDeviceUrl/);
    for (const page of ['accounts', 'history', 'status', 'usage', 'quota', 'themes', 'settings']) {
      assert.match(rendererHtml, new RegExp(`data-page="${page}"`));
    }
    for (const pageId of ['historyPage', 'statusPage', 'quotaPage', 'themesPage', 'settingsPage']) {
      assert.match(rendererHtml, new RegExp(`id="${pageId}"`));
    }
    for (const method of [
      'getHistory', 'searchHistory', 'readThread', 'setThreadArchived', 'deleteThread',
      'getQuotaStats', 'getSystemSettings', 'saveSystemSettings', 'detectLocalProxy', 'openPath',
      'chooseCodexApp', 'launchCodexApp', 'getAllLoginStatuses', 'getCodexThemes', 'applyCodexTheme', 'restoreCodexTheme', 'saveCustomTheme',
    ]) {
      assert.match(rendererJs, new RegExp(`["']${method}["']`));
    }
    assert.match(rendererHtml, /data-range="today"/);
    assert.match(rendererHtml, /id="usageAccountFilter"/);
    assert.match(rendererHtml, /id="usageRefreshButton"/);
    assert.match(rendererHtml, /id="usageRefreshInterval"/);
    assert.match(rendererHtml, /option value="15" selected>15 秒/);
    assert.match(rendererHtml, /id="detailAppLaunchButton"/);
    assert.match(rendererHtml, /id="setCurrentButton"[^>]*>切换账号</);
    assert.match(rendererJs, /usageReport/);
    assert.match(rendererJs, /perAccount/);
    assert.match(rendererJs, /unattributed/);
    assert.match(rendererJs, /beforeunload/);
    assert.match(rendererJs, /usageRefreshSeconds:\s*15/);
    assert.match(rendererJs, /quotaRefreshSeconds:\s*30/);
    assert.match(rendererJs, /updateQuotaRefreshTimer/);
    assert.match(rendererJs, /clearQuotaRefreshTimer/);
    assert.match(rendererJs, /detailAppLaunchButton\.addEventListener/);
    assert.match(rendererJs, /Number\.isInteger\(number\)\s*\?\s*0\s*:\s*1/);
    assert.match(rendererJs, /historyList\.addEventListener/);
    assert.match(rendererJs, /statusAccountList\.addEventListener/);
    assert.match(rendererJs, /themeGrid\.addEventListener/);
    assert.match(cliSource, /\['app-server', '--stdio', '--disable', 'plugins'\]/);
    assert.match(cliSource, /account\/login\/start/);
    assert.match(cliSource, /account\/login\/completed/);
    assert.match(cliSource, /account\/login\/cancel/);
    assert.doesNotMatch(cliSource, /--device-auth|createDeviceAuthOutputHandler|cleanupTemporaryBrowserProfile|browserLauncher/);
    assert.match(cliSource, /desktopProfile/);
    assert.match(cliSource, /switchCodexApp/);
    assert.match(cliSource, /CROSS_ACCOUNT_HANDOFF/);
    assert.match(cliSource, /OPENAI_APPLE_TEAM_IDENTIFIER\s*=\s*'2DC432GLL2'/);
    assert.match(cliSource, /'\/usr\/bin\/codesign'/);
    assert.doesNotMatch(cliSource, /\/usr\/bin\/mdfind/);
    assert.match(mainSource, /themeDebugProfile:\s*useThemeRuntime/);
    assert.match(mainSource, /createAccountActivationQueue/);
    assert.match(mainSource, /runAccountActivation\(\(\)\s*=>\s*launchCodexAppForAccountUnlocked/);
    assert.match(mainSource, /launchCodexAppForAccountUnlocked\(findAccount\(account\.id\),\s*options\)/);
    assert.match(mainSource, /const freshAccount = findAccount\(account\.id\)/);
    assert.match(applyThemeMain, /runAccountActivation/);
    assert.match(applyThemeMain, /store\.saveSettings\(\{\s*\.\.\.store\.loadSettings\(\)/);
    assert.match(restoreThemeMain, /runAccountActivation/);
    assert.match(restoreThemeMain, /store\.saveSettings\(\{\s*\.\.\.store\.loadSettings\(\)/);
    assert.match(saveCustomThemeMain, /runAccountActivation/);
    assert.match(setCurrentHandler, /replaceRunningDesktop:\s*true/);
    assert.match(setCurrentHandler, /selectAsCurrent:\s*true/);
    assert.match(desktopLaunchHandler, /replaceRunningDesktop:\s*true/);
    assert.match(desktopLaunchHandler, /selectAsCurrent:\s*true/);
    assert.doesNotMatch(reapplyAccountAction, /account\.id\s*===\s*app\.currentAccountId/);
    assert.match(mainSource, /enableThemeDebug:\s*true/);
    assert.match(mainSource, /主题桌面实例已经在运行/);
    assert.match(deleteAccountHandler, /runAccountActivation/);
    assert.match(deleteAccountHandler, /cleanupWarning/);
    assert.ok(
      deleteAccountHandler.indexOf('stopManagedCodexApps') < deleteAccountHandler.indexOf('store.removeAccount'),
      'a running managed desktop must stop before its account directory is deleted',
    );
    assert.match(restoreThemeAction, /runtimeRestored\s*===\s*false/);
    assert.match(restoreThemeAction, /runtimeRestored\s*!==\s*true\s*&&\s*result\?\.reason/);
    assert.match(restoreThemeAction, /已恢复官方主题设置/);
    assert.match(deleteAccountAction, /账号已删除，清理待完成/);
    assert.match(mainSource, /account:oauth-draft-prepare/);
    assert.match(mainSource, /account:oauth-draft-commit/);
    assert.match(mainSource, /account:oauth-draft-cancel/);
    assert.match(mainSource, /oauthCleanupTasks/);
    assert.match(beforeQuitHandler, /event\.preventDefault\(\)/);
    assert.match(beforeQuitHandler, /Promise\.allSettled\(\[\.\.\.oauthCleanupTasks\]\)/);
    assert.match(beforeQuitHandler, /oauthShutdownComplete\s*=\s*true/);
    assert.ok(
      daemonStart < mainSource.indexOf('app.requestSingleInstanceLock()'),
      'the gateway daemon must bypass the manager single-instance lock',
    );
    assert.match(daemonBranch, /runPatGatewayDaemon/);
    assert.doesNotMatch(daemonBranch, /createWindow|installIpcHandlers|requestSingleInstanceLock/);
    assert.match(daemonRunner, /settings\.json/);
    assert.doesNotMatch(daemonRunner, /new AccountStore/);
    assert.match(mainSource, /new PatGatewayController/);
    assert.match(secondInstanceHandler, /createWindow\(\)/);
    assert.match(secondInstanceHandler, /if \(app\.isReady\(\)\) createWindow\(\);\s*return;/);
    assert.ok(
      secondInstanceHandler.indexOf('createWindow()') < secondInstanceHandler.indexOf('mainWindow.isMinimized()'),
      'a second instance recreates a closed main window before restoring or focusing it',
    );
    assert.doesNotMatch(mainSource, /account:device-auth|account:login-cancel/);
    assert.match(preloadSource, /prepareOAuthDraft/);
    assert.match(preloadSource, /commitOAuthDraft/);
    assert.match(preloadSource, /cancelOAuthDraft/);
    assert.match(preloadSource, /onOAuthDraftCompleted/);
    assert.match(preloadSource, /clearClipboardIfMatches/);
    assert.match(preloadSource, /chooseCodexApp/);
    assert.doesNotMatch(preloadSource, /onDeviceAuth/);

    assert.throws(() => store.saveAccount({
      name: '不安全 API',
      authKind: 'compatible_api',
      apiBaseUrl: 'http://api.example.com/v1',
      apiKey: 'sk-test-secret-value',
    }));
    assert.throws(() => store.saveAccount({
      name: '带密码 URL',
      authKind: 'compatible_api',
      apiBaseUrl: 'https://user:password@api.example.com/v1',
      apiKey: 'sk-test-secret-value',
    }));

    const shortTerra = _test.eventCost({
      model: 'gpt-5.6-terra',
      inputTokens: 100_000,
      cachedInputTokens: 0,
      cacheWriteTokens: null,
      outputTokens: 100_000,
    });
    assert.equal(shortTerra, 1.4);
    const splitTerra = _test.eventCost({
      model: 'gpt-5.6-terra',
      inputTokens: 100_000,
      cachedInputTokens: 20_000,
      cacheWriteTokens: 30_000,
      outputTokens: 10_000,
    });
    assert.equal(splitTerra, (50_000 * 2 + 20_000 * 0.2 + 30_000 * 2.5 + 10_000 * 12) / 1_000_000);

    const sessionPath = path.join(root, 'fixture.jsonl');
    fs.writeFileSync(sessionPath, [
      JSON.stringify({ type: 'turn_context', timestamp: '2026-07-15T00:00:00Z', payload: { model: 'gpt-5.6-luna' } }),
      JSON.stringify({ type: 'event_msg', timestamp: '2026-07-15T00:00:01Z', payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 100, cached_input_tokens: 25, output_tokens: 20, total_tokens: 120 } } } }),
    ].join('\n'));
    const events = [];
    await _test.parseSession(sessionPath, null, (event) => events.push(event));
    assert.equal(events.length, 1);
    assert.equal(events[0].model, 'gpt-5.6-luna');
    assert.equal(events[0].cachedInputTokens, 25);
    assert.equal(events[0].cacheWriteTokens, null);

    store.setCurrentAccount(account.id);
    assert.equal(store.loadUsageSwitches().some((entry) => entry.accountId === account.id), true);

    const historyFailureStore = new AccountStore(path.join(root, 'history-failure-data'), {
      accountHomesRoot: path.join(root, 'history-failure-homes'),
    });
    const historyFailureAccount = historyFailureStore.saveAccount({
      name: '切换历史写入失败账号',
      authKind: 'access_token',
    });
    fs.mkdirSync(historyFailureStore.switchesPath);
    assert.throws(
      () => historyFailureStore.setCurrentAccount(historyFailureAccount.id),
      /EPERM|EISDIR|directory|目录/i,
    );
    assert.equal(
      historyFailureStore.loadSettings().currentAccountId,
      null,
      'switch-history failure must not partially commit the current account',
    );

    const settingsFailureStore = new AccountStore(path.join(root, 'settings-failure-data'), {
      accountHomesRoot: path.join(root, 'settings-failure-homes'),
    });
    const settingsFailureAccount = settingsFailureStore.saveAccount({
      name: '设置写入失败账号',
      authKind: 'access_token',
    });
    fs.mkdirSync(settingsFailureStore.settingsPath);
    assert.throws(
      () => settingsFailureStore.setCurrentAccount(settingsFailureAccount.id),
      /EPERM|EISDIR|directory|目录/i,
    );
    assert.equal(
      settingsFailureStore.loadUsageSwitches().length,
      0,
      'settings failure must roll back the switch-history entry',
    );

    for (const savedAccount of store.loadAccounts()) store.removeAccount(savedAccount.id);
    assert.equal(store.loadAccounts().length, 0);
    assert.equal(fs.existsSync(account.codexHome), false);
    assert.equal(fs.existsSync(updatedOAuth.codexHome), false);
    const deletedBoundaries = store.loadUsageSwitches();
    assert.equal(deletedBoundaries.length, 1);
    assert.equal(deletedBoundaries[0].accountId, '');
    assert.equal(deletedBoundaries[0].accountName, '');
    assert.equal(deletedBoundaries[0].source, 'deleted-account-boundary');
    console.log('macOS core tests passed');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
