const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { AccountStore } = require('../src/services/account-store');
const {
  applyProxyEnvironment,
  detectLocalProxy,
  formatProxyUrl,
  normalizeProjectPath,
  normalizeProxySettings,
  validPort,
} = require('../src/services/system-service');

test('proxy settings are validated and never accept the protected gateway port', () => {
  assert.equal(validPort(8317), null);
  assert.equal(validPort(7890), 7890);
  assert.deepEqual(normalizeProxySettings({
    proxyScheme: 'socks5',
    proxyAddress: '127.0.0.1',
    proxyPort: 1080,
    proxyAutoDetect: false,
  }), {
    proxyAutoDetect: false,
    proxyScheme: 'socks5',
    proxyAddress: '127.0.0.1',
    proxyPort: 1080,
    detectedProxyPort: null,
  });
  assert.throws(() => normalizeProxySettings({ proxyAddress: 'user@host', proxyPort: 7890 }));
});

test('proxy environment applies the selected address and preserves loopback bypasses', () => {
  const settings = {
    proxyScheme: 'http',
    proxyAddress: '127.0.0.1',
    proxyPort: 7890,
    proxyAutoDetect: false,
  };
  assert.equal(formatProxyUrl(settings), 'http://127.0.0.1:7890');
  const environment = applyProxyEnvironment({ NO_PROXY: 'internal.example' }, settings);
  assert.equal(environment.HTTP_PROXY, 'http://127.0.0.1:7890');
  assert.equal(environment.HTTPS_PROXY, 'http://127.0.0.1:7890');
  assert.match(environment.NO_PROXY, /internal\.example/);
  assert.match(environment.NO_PROXY, /127\.0\.0\.1/);
  assert.match(environment.NO_PROXY, /localhost/);
});

test('local proxy detection only returns a positively identified loopback candidate', async () => {
  const checked = [];
  const result = await detectLocalProxy({
    preferredPort: 8317,
    ports: [43210, 7890],
    probe: async (port) => {
      checked.push(port);
      return port === 7890 ? { address: '127.0.0.1', port, scheme: 'http' } : null;
    },
  });
  assert.equal(result.found, true);
  assert.equal(result.port, 7890);
  assert.equal(checked.includes(8317), false);
});

test('project and extended account-manager settings round-trip safely', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-system-'));
  try {
    const project = path.join(root, 'project');
    const codexApp = path.join(root, 'Codex.app');
    fs.mkdirSync(project);
    fs.mkdirSync(codexApp);
    assert.equal(normalizeProjectPath(project), project);
    assert.throws(() => normalizeProjectPath(path.join(root, 'missing')));

    const store = new AccountStore(path.join(root, 'data'), {
      accountHomesRoot: path.join(root, 'homes'),
    });
    const settings = store.loadSettings();
    settings.projectPath = project;
    settings.codexAppPath = codexApp;
    settings.proxyAutoDetect = false;
    settings.proxyPort = 7890;
    settings.usageRefreshSeconds = 3;
    settings.quotaRefreshSeconds = 10;
    store.saveSettings(settings);
    const loaded = store.loadSettings();
    assert.equal(loaded.projectPath, project);
    assert.equal(loaded.codexAppPath, codexApp);
    assert.equal(loaded.proxyPort, 7890);
    assert.equal(loaded.usageRefreshSeconds, 3);
    assert.equal(loaded.quotaRefreshSeconds, 10);

    const account = store.saveAccount({ name: 'Test', authKind: 'access_token' });
    store.setCurrentAccount(account.id);
    store.markAccountUsed(account.id, '2026-07-22T00:00:00.000Z');
    assert.equal(store.loadAccounts()[0].lastUsedAt, '2026-07-22T00:00:00.000Z');
    assert.equal(store.loadUsageSwitches().length, 1);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
