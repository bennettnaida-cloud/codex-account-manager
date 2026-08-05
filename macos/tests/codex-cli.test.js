const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { PassThrough } = require('node:stream');
const test = require('node:test');

const {
  AppServerOAuthSession,
  CodexCliService,
  buildChildEnvironment,
  isOfficialOAuthAuth,
  readDesktopProcessList,
  readTerminalProcessList,
  redactSecrets,
  validateOfficialAuthUrl,
  waitForOfficialOAuthAuth,
} = require('../src/services/codex-cli');

function writeOAuthAuth(codexHome, suffix = '') {
  fs.mkdirSync(codexHome, { recursive: true });
  fs.writeFileSync(path.join(codexHome, 'auth.json'), JSON.stringify({
    auth_mode: 'chatgpt',
    tokens: {
      id_token: `test-id${suffix}`,
      access_token: `test-access${suffix}`,
      refresh_token: `test-refresh${suffix}`,
    },
  }));
}

function tick() {
  return new Promise((resolve) => setImmediate(resolve));
}

function createCodexApplication(parentDirectory, {
  appName = 'Codex.app',
  executableName = 'Codex',
  bundleIdentifier = 'com.openai.codex',
  plistExecutableName = executableName,
} = {}) {
  const appPath = path.join(parentDirectory, appName);
  const executable = path.join(appPath, 'Contents', 'MacOS', executableName);
  fs.mkdirSync(path.dirname(executable), { recursive: true });
  fs.writeFileSync(executable, '#!/bin/sh\n');
  fs.chmodSync(executable, 0o755);
  fs.writeFileSync(path.join(appPath, 'Contents', 'Info.plist'), JSON.stringify({
    CFBundleIdentifier: bundleIdentifier,
    CFBundleExecutable: plistExecutableName,
  }));
  return { appPath, executable };
}

function readTestApplicationInfoPlist(infoPlistPath) {
  return JSON.parse(fs.readFileSync(infoPlistPath, 'utf8'));
}

function readTestApplicationSignature() {
  return { identifier: 'com.openai.codex', teamIdentifier: '2DC432GLL2' };
}

test('packaged builds prefer the pinned bundled Apple Silicon CLI and reject damaged metadata', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-bundled-cli-'));
  const previousOverride = process.env.CODEX_ACCOUNT_MANAGER_CODEX_BIN;
  try {
    const bundledRoot = path.join(root, 'codex-cli');
    const bundledCodex = path.join(bundledRoot, 'bin', 'codex');
    const fallbackCodex = path.join(root, 'fallback-codex');
    fs.mkdirSync(path.dirname(bundledCodex), { recursive: true });
    fs.writeFileSync(bundledCodex, '#!/bin/sh\n');
    fs.chmodSync(bundledCodex, 0o755);
    fs.writeFileSync(path.join(bundledRoot, 'codex-package.json'), JSON.stringify({
      layoutVersion: 1,
      version: '0.144.1',
      target: 'aarch64-apple-darwin',
      entrypoint: 'bin/codex',
    }));
    fs.writeFileSync(fallbackCodex, '#!/bin/sh\n');
    fs.chmodSync(fallbackCodex, 0o755);
    process.env.CODEX_ACCOUNT_MANAGER_CODEX_BIN = fallbackCodex;

    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      allowExecutableOverride: true,
    });
    assert.equal(service.getCodexPath(), fs.realpathSync(bundledCodex));
    assert.equal(service.getCodexPath(), fs.realpathSync(bundledCodex), 'the verified result is cached');

    fs.writeFileSync(path.join(bundledRoot, 'codex-package.json'), JSON.stringify({
      layoutVersion: 1,
      version: '0.144.0',
      target: 'aarch64-apple-darwin',
      entrypoint: 'bin/codex',
    }));
    const damaged = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      allowExecutableOverride: true,
    });
    assert.equal(damaged.getCodexPath(), fallbackCodex, 'a damaged bundle falls back instead of being executed');
  } finally {
    if (previousOverride === undefined) delete process.env.CODEX_ACCOUNT_MANAGER_CODEX_BIN;
    else process.env.CODEX_ACCOUNT_MANAGER_CODEX_BIN = previousOverride;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

function createProtocolChild(onMessage = () => {}) {
  const child = new EventEmitter();
  child.stdin = new PassThrough();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.exitCode = null;
  child.kills = [];
  child.messages = [];

  let input = '';
  child.stdin.setEncoding('utf8');
  child.stdin.on('data', (chunk) => {
    input += chunk;
    while (input.includes('\n')) {
      const newline = input.indexOf('\n');
      const line = input.slice(0, newline);
      input = input.slice(newline + 1);
      if (!line) continue;
      const message = JSON.parse(line);
      child.messages.push(message);
      queueMicrotask(() => onMessage(message, child));
    }
  });

  child.send = (message) => child.stdout.write(`${JSON.stringify(message)}\n`);
  child.sendRaw = (line) => child.stdout.write(`${line}\n`);
  child.close = (code = 1) => {
    if (child.exitCode !== null) return;
    child.exitCode = code;
    child.emit('close', code);
  };
  child.kill = (signal = 'SIGTERM') => {
    child.kills.push(signal);
    if (child.exitCode === null) {
      child.exitCode = signal === 'SIGKILL' ? 137 : 0;
      setImmediate(() => child.emit('close', child.exitCode));
    }
    return true;
  };
  return child;
}

test('official OAuth bounds initialization RPC time and redacts bare Business PAT output', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-oauth-timeout-'));
  try {
    const child = createProtocolChild(() => { /* deliberately never reply */ });
    const session = new AppServerOAuthSession({
      account: { id: 'timeout', codexHome: root, authKind: 'official_oauth' },
      executable: '/test/codex',
      env: {},
      spawnProcess: () => child,
      timeoutMs: 1_000,
      initializeTimeoutMs: 50,
      loginStartTimeoutMs: 60,
    });
    const started = session.start();
    const [ready, completed] = await Promise.allSettled([started.ready, started.completed]);
    assert.equal(ready.status, 'rejected');
    assert.match(ready.reason.message, /initialize/);
    assert.equal(completed.status, 'rejected');
    assert.ok(child.kills.includes('SIGTERM'));
    assert.doesNotMatch(redactSecrets('failed at-test-only-not-a-real-token-123456'), /at-test/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

function createServiceHarness({ root, account, onMessage, timeoutMs = 10_000 }) {
  const child = createProtocolChild(onMessage);
  let spawnCall = null;
  const service = new CodexCliService({
    resourcesPath: root,
    userDataPath: root,
    appServerFactory: ({ account: selectedAccount, executable, env }) => new AppServerOAuthSession({
      account: selectedAccount,
      executable,
      env,
      timeoutMs,
      spawnProcess: (command, args, options) => {
        spawnCall = { command, args, options };
        return child;
      },
    }),
  });
  service.getCodexPath = () => path.join(root, 'codex');
  return { child, service, getSpawnCall: () => spawnCall };
}

function respondToInitializeAndStart(message, child, result = {}) {
  if (message.method === 'initialize') {
    child.send({ id: message.id, result: { userAgent: 'test-app-server' } });
  } else if (message.method === 'account/login/start') {
    child.send({
      id: message.id,
      result: {
        type: 'chatgpt',
        loginId: 'login-test-1',
        authUrl: 'https://auth.openai.com/oauth/authorize?state=test-state',
        ...result,
      },
    });
  }
}

function createOAuthDrainHarness(root, {
  onMessage = () => {},
  onKill = () => {},
  childStopTimeoutMs = 20,
  childKillTimeoutMs = 20,
} = {}) {
  const account = {
    id: 'oauth-drain',
    name: 'OAuth drain',
    authKind: 'official_oauth',
    codexHome: path.join(root, 'oauth-home'),
  };
  const child = createProtocolChild(onMessage);
  child.kill = (signal = 'SIGTERM') => {
    child.kills.push(signal);
    onKill(signal, child);
    return true;
  };
  const session = new AppServerOAuthSession({
    account,
    executable: '/test/codex',
    env: {},
    spawnProcess: () => child,
    timeoutMs: 10_000,
    initializeTimeoutMs: 500,
    loginStartTimeoutMs: 500,
    childStopTimeoutMs,
    childKillTimeoutMs,
  });
  return { account, child, session, handle: session.start() };
}

test('official OAuth speaks the Codex app-server JSONL protocol in an isolated environment', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-app-server-'));
  const account = {
    id: 'oauth-test',
    name: 'OAuth',
    authKind: 'official_oauth',
    codexHome: path.join(root, 'oauth-home'),
  };
  const savedEnvironment = {
    CODEX_HOME: process.env.CODEX_HOME,
    OPENAI_API_KEY: process.env.OPENAI_API_KEY,
    CODEX_ACCESS_TOKEN: process.env.CODEX_ACCESS_TOKEN,
    AZURE_OPENAI_API_KEY: process.env.AZURE_OPENAI_API_KEY,
  };
  process.env.CODEX_HOME = path.join(root, 'shared-home-must-not-leak');
  process.env.OPENAI_API_KEY = 'must-not-be-inherited';
  process.env.CODEX_ACCESS_TOKEN = 'must-not-be-inherited';
  process.env.AZURE_OPENAI_API_KEY = 'must-not-be-inherited';

  try {
    const harness = createServiceHarness({
      root,
      account,
      onMessage: (message, child) => respondToInitializeAndStart(message, child),
    });
    const handle = harness.service.startOfficialLogin(account);
    let completed = false;
    handle.completed.then(() => { completed = true; }, () => { completed = true; });

    const ready = await handle.ready;
    assert.deepEqual(ready, {
      loginId: 'login-test-1',
      authUrl: 'https://auth.openai.com/oauth/authorize?state=test-state',
    });
    assert.deepEqual(harness.child.messages.slice(0, 3), [
      {
        id: 1,
        method: 'initialize',
        params: {
          clientInfo: {
            name: 'codex-account-manager',
            title: 'Codex Account Manager',
            version: '1.0.0',
          },
          capabilities: { experimentalApi: true },
        },
      },
      { method: 'initialized' },
      { id: 2, method: 'account/login/start', params: { type: 'chatgpt' } },
    ]);

    const spawnCall = harness.getSpawnCall();
    assert.equal(spawnCall.command, path.join(root, 'codex'));
    assert.deepEqual(spawnCall.args, ['app-server', '--stdio', '--disable', 'plugins']);
    assert.equal(spawnCall.options.env.CODEX_HOME, account.codexHome);
    assert.equal(spawnCall.options.env.CODEX_SQLITE_HOME, account.codexHome);
    assert.equal(spawnCall.options.env.OPENAI_API_KEY, undefined);
    assert.equal(spawnCall.options.env.CODEX_ACCESS_TOKEN, undefined);
    assert.equal(spawnCall.options.env.AZURE_OPENAI_API_KEY, undefined);

    harness.child.sendRaw('not JSON');
    harness.child.send({ method: 'unrelated/notification', params: { success: true } });
    harness.child.send({
      method: 'account/login/completed',
      params: { loginId: 'another-login', success: true, error: null },
    });
    await tick();
    assert.equal(completed, false, 'unrelated notifications and a different loginId must be ignored');

    writeOAuthAuth(account.codexHome);
    harness.child.send({
      method: 'account/login/completed',
      params: { loginId: 'login-test-1', success: true, error: null },
    });
    const result = await handle.completed;
    assert.equal(result.ok, true);
    assert.equal(result.badge, 'OAUTH');
    assert.equal(JSON.stringify(result).includes('auth.openai.com'), false);
    assert.equal(JSON.stringify(result).includes('test-access'), false);
    assert.equal(isOfficialOAuthAuth(path.join(account.codexHome, 'auth.json')), true);
    assert.equal(harness.service.hasValidOfficialOAuth(account), true);
    assert.deepEqual(harness.child.kills, ['SIGTERM']);
  } finally {
    for (const [key, value] of Object.entries(savedEnvironment)) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('a matching completion delivered with the login start response is replayed after loginId is set', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-app-server-fast-completion-'));
  const account = {
    id: 'oauth-fast-completion',
    name: 'OAuth',
    authKind: 'official_oauth',
    codexHome: path.join(root, 'oauth-home'),
  };
  try {
    const harness = createServiceHarness({
      root,
      account,
      onMessage(message, child) {
        if (message.method === 'initialize') {
          child.send({ id: message.id, result: {} });
          return;
        }
        if (message.method !== 'account/login/start') return;
        writeOAuthAuth(account.codexHome);
        child.stdout.write(`${[
          JSON.stringify({
            id: message.id,
            result: {
              type: 'chatgpt',
              loginId: 'login-fast-1',
              authUrl: 'https://auth.openai.com/oauth/authorize?state=fast',
            },
          }),
          JSON.stringify({
            method: 'account/login/completed',
            params: { loginId: 'login-fast-1', success: true, error: null },
          }),
        ].join('\n')}\n`);
      },
    });
    const handle = harness.service.startOfficialLogin(account);
    const completion = handle.completed;
    assert.deepEqual(await handle.ready, {
      loginId: 'login-fast-1',
      authUrl: 'https://auth.openai.com/oauth/authorize?state=fast',
    });
    const result = await completion;
    assert.equal(result.ok, true);
    assert.equal(result.badge, 'OAUTH');
    assert.equal(harness.service.hasValidOfficialOAuth(account), true);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('app-server start errors, wrong login types, and untrusted URLs fail closed', async (t) => {
  const scenarios = [
    {
      name: 'login start RPC error',
      respond(message, child) {
        if (message.method === 'initialize') child.send({ id: message.id, result: {} });
        else if (message.method === 'account/login/start') child.send({ id: message.id, error: { code: -32000, message: 'private detail' } });
      },
    },
    {
      name: 'wrong login type',
      respond(message, child) {
        respondToInitializeAndStart(message, child, { type: 'apiKey' });
      },
    },
    {
      name: 'untrusted auth URL',
      respond(message, child) {
        respondToInitializeAndStart(message, child, { authUrl: 'https://auth.openai.com.evil.example/steal' });
      },
    },
  ];

  for (const scenario of scenarios) {
    await t.test(scenario.name, async () => {
      const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-app-server-error-'));
      const account = {
        id: `oauth-${scenario.name}`,
        name: 'OAuth',
        authKind: 'official_oauth',
        codexHome: path.join(root, 'oauth-home'),
      };
      try {
        const harness = createServiceHarness({ root, account, onMessage: scenario.respond });
        const handle = harness.service.startOfficialLogin(account);
        const readyError = handle.ready.then(() => null, (error) => error);
        const completedError = handle.completed.then(() => null, (error) => error);
        assert.ok(await readyError instanceof Error);
        assert.ok(await completedError instanceof Error);
        assert.equal(harness.service.hasValidOfficialOAuth(account), false);
        assert.deepEqual(harness.child.kills, ['SIGTERM']);
      } finally {
        fs.rmSync(root, { recursive: true, force: true });
      }
    });
  }
});

test('official auth URL validation accepts only the two official HTTPS destinations', () => {
  assert.equal(
    validateOfficialAuthUrl('https://auth.openai.com/oauth/authorize?state=test'),
    'https://auth.openai.com/oauth/authorize?state=test',
  );
  assert.equal(
    validateOfficialAuthUrl('https://chatgpt.com/codex/desktop-auth?code=test'),
    'https://chatgpt.com/codex/desktop-auth?code=test',
  );

  for (const value of [
    'http://auth.openai.com/oauth/authorize',
    'https://auth.openai.com.evil.example/oauth/authorize',
    'https://user:password@auth.openai.com/oauth/authorize',
    'https://auth.openai.com:444/oauth/authorize',
    'https://chatgpt.com/codex/desktop-auth/extra',
    'https://example.com/codex/desktop-auth',
    `https://auth.openai.com/${'a'.repeat(8200)}`,
  ]) {
    assert.throws(() => validateOfficialAuthUrl(value));
  }
});

test('cancelling after ready sends account/login/cancel for the exact loginId', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-app-server-cancel-'));
  const account = {
    id: 'oauth-cancel',
    name: 'OAuth',
    authKind: 'official_oauth',
    codexHome: path.join(root, 'oauth-home'),
  };
  try {
    const harness = createServiceHarness({
      root,
      account,
      onMessage(message, child) {
        respondToInitializeAndStart(message, child);
        if (message.method === 'account/login/cancel') {
          child.send({ id: message.id, result: { status: 'canceled' } });
        }
      },
    });
    const handle = harness.service.startOfficialLogin(account);
    await handle.ready;
    const completion = handle.completed.then(() => null, (error) => error);

    assert.equal(harness.service.cancelOfficialLogin(account.id), true);
    assert.ok(await completion instanceof Error);
    assert.deepEqual(harness.child.messages.find((message) => message.method === 'account/login/cancel'), {
      id: 3,
      method: 'account/login/cancel',
      params: { loginId: 'login-test-1' },
    });
    assert.deepEqual(harness.child.kills, ['SIGTERM']);
    assert.equal(harness.service.cancelOfficialLogin(account.id), false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('cancelling before app-server is ready terminates only that child without a cancel RPC', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-app-server-early-cancel-'));
  const account = {
    id: 'oauth-early-cancel',
    name: 'OAuth',
    authKind: 'official_oauth',
    codexHome: path.join(root, 'oauth-home'),
  };
  try {
    const harness = createServiceHarness({ root, account, onMessage: () => {} });
    const handle = harness.service.startOfficialLogin(account);
    const readyError = handle.ready.then(() => null, (error) => error);
    const completedError = handle.completed.then(() => null, (error) => error);
    assert.equal(await handle.cancel(), true);
    assert.ok(await readyError instanceof Error);
    assert.ok(await completedError instanceof Error);
    assert.equal(harness.child.messages.some((message) => message.method === 'account/login/cancel'), false);
    assert.deepEqual(harness.child.kills, ['SIGTERM']);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('OAuth cancel stays pending until the dedicated child emits close', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-oauth-drain-close-'));
  try {
    const harness = createOAuthDrainHarness(root, {
      childStopTimeoutMs: 500,
      childKillTimeoutMs: 500,
    });
    harness.handle.ready.catch(() => {});
    const completion = harness.handle.completed.then(() => null, (error) => error);
    let cancelSettled = false;
    const cancelling = harness.handle.cancel().finally(() => { cancelSettled = true; });
    await tick();
    assert.deepEqual(harness.child.kills, ['SIGTERM']);
    assert.equal(cancelSettled, false);

    harness.child.close(0);
    assert.equal(await cancelling, true);
    assert.ok(await completion instanceof Error);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('OAuth child drain escalates TERM to KILL and still waits for close', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-oauth-drain-kill-'));
  try {
    const harness = createOAuthDrainHarness(root, {
      onKill(signal, child) {
        if (signal === 'SIGKILL') setTimeout(() => child.close(137), 10);
      },
      childStopTimeoutMs: 20,
      childKillTimeoutMs: 200,
    });
    harness.handle.ready.catch(() => {});
    harness.handle.completed.catch(() => {});

    assert.equal(await harness.handle.cancel(), true);
    assert.deepEqual(harness.child.kills, ['SIGTERM', 'SIGKILL']);
    assert.equal(harness.child.exitCode, 137);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('OAuth child drain fails closed after TERM and KILL, then permits an exact retry', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-oauth-drain-retry-'));
  let closeOnRetry = false;
  try {
    const harness = createOAuthDrainHarness(root, {
      onKill(signal, child) {
        if (closeOnRetry && signal === 'SIGTERM') queueMicrotask(() => child.close(0));
      },
      childStopTimeoutMs: 20,
      childKillTimeoutMs: 20,
    });
    harness.handle.ready.catch(() => {});
    let completionSettled = false;
    const completion = harness.handle.completed
      .then(() => null, (error) => error)
      .finally(() => { completionSettled = true; });

    await assert.rejects(
      harness.handle.cancel(),
      (error) => error.code === 'OAUTH_PROCESS_STOP_FAILED',
    );
    assert.deepEqual(harness.child.kills, ['SIGTERM', 'SIGKILL']);
    assert.equal(completionSettled, false, 'credentials must be retained while the child may still be alive');

    closeOnRetry = true;
    assert.equal(await harness.handle.cancel(), false);
    assert.deepEqual(harness.child.kills, ['SIGTERM', 'SIGKILL', 'SIGTERM']);
    assert.ok(await completion instanceof Error);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('OAuth completed settles when the child closes after an earlier stop timeout', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-oauth-drain-late-close-'));
  try {
    const harness = createOAuthDrainHarness(root, {
      childStopTimeoutMs: 20,
      childKillTimeoutMs: 20,
    });
    harness.handle.ready.catch(() => {});
    let completionSettled = false;
    const completion = harness.handle.completed
      .then(() => null, (error) => error)
      .finally(() => { completionSettled = true; });

    await assert.rejects(
      harness.handle.cancel(),
      (error) => error.code === 'OAUTH_PROCESS_STOP_FAILED',
    );
    assert.equal(completionSettled, false);

    harness.child.close(137);
    assert.ok(await completion instanceof Error);
    assert.equal(completionSettled, true);
    assert.deepEqual(harness.child.kills, ['SIGTERM', 'SIGKILL']);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('OAuth success and cancel-after-success share the same close drain', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-oauth-success-drain-'));
  try {
    const harness = createOAuthDrainHarness(root, {
      onMessage: respondToInitializeAndStart,
      childStopTimeoutMs: 500,
      childKillTimeoutMs: 500,
    });
    await harness.handle.ready;
    writeOAuthAuth(harness.account.codexHome);
    harness.child.send({
      method: 'account/login/completed',
      params: { loginId: 'login-test-1', success: true, error: null },
    });
    let completedSettled = false;
    const completed = harness.handle.completed.finally(() => { completedSettled = true; });
    for (let attempt = 0; attempt < 20 && harness.child.kills.length === 0; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.deepEqual(harness.child.kills, ['SIGTERM']);
    assert.equal(completedSettled, false);

    let cancelSettled = false;
    const cancelAfterSuccess = harness.handle.cancel().finally(() => { cancelSettled = true; });
    await tick();
    assert.equal(cancelSettled, false);
    harness.child.close(0);

    assert.equal(await cancelAfterSuccess, false);
    assert.deepEqual(await completed, { ok: true });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('automatic OAuth protocol failure rejects completed only after child close', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-oauth-failure-drain-'));
  try {
    const harness = createOAuthDrainHarness(root, {
      onMessage(message, child) {
        if (message.method === 'initialize') {
          child.send({ id: message.id, error: { code: -32000, message: 'failed' } });
        }
      },
      childStopTimeoutMs: 500,
      childKillTimeoutMs: 500,
    });
    const readyError = harness.handle.ready.then(() => null, (error) => error);
    let completedSettled = false;
    const completedError = harness.handle.completed
      .then(() => null, (error) => error)
      .finally(() => { completedSettled = true; });

    assert.ok(await readyError instanceof Error);
    await tick();
    assert.deepEqual(harness.child.kills, ['SIGTERM']);
    assert.equal(completedSettled, false);
    harness.child.close(1);
    assert.ok(await completedError instanceof Error);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('an app-server process that exits early rejects both readiness and completion', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-app-server-exit-'));
  const account = {
    id: 'oauth-exit',
    name: 'OAuth',
    authKind: 'official_oauth',
    codexHome: path.join(root, 'oauth-home'),
  };
  try {
    const harness = createServiceHarness({ root, account, onMessage: () => {} });
    const handle = harness.service.startOfficialLogin(account);
    const readyError = handle.ready.then(() => null, (error) => error);
    const completedError = handle.completed.then(() => null, (error) => error);
    harness.child.close(42);
    assert.ok(await readyError instanceof Error);
    assert.ok(await completedError instanceof Error);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('official login is globally single-flight until completion', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-app-server-single-flight-'));
  const account = {
    id: 'oauth-one',
    name: 'OAuth one',
    authKind: 'official_oauth',
    codexHome: path.join(root, 'oauth-one'),
  };
  const other = {
    id: 'oauth-two',
    name: 'OAuth two',
    authKind: 'official_oauth',
    codexHome: path.join(root, 'oauth-two'),
  };
  let resolveCompletion;
  const completion = new Promise((resolve) => { resolveCompletion = resolve; });
  try {
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      appServerFactory: () => ({
        start: () => ({
          ready: Promise.resolve({ loginId: 'single-flight', authUrl: 'https://auth.openai.com/oauth/authorize' }),
          completed: completion,
          cancel: async () => true,
        }),
        cancel: async () => true,
      }),
    });
    service.getCodexPath = () => path.join(root, 'codex');
    const handle = service.startOfficialLogin(account);
    assert.throws(() => service.startOfficialLogin(account));
    assert.throws(() => service.startOfficialLogin(other));
    writeOAuthAuth(account.codexHome);
    resolveCompletion({ ok: true });
    assert.equal((await handle.completed).ok, true);
    assert.equal(service.activeOfficialLogins.size, 0);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('a success notification without complete OAuth credentials still fails', { timeout: 8_000 }, async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-app-server-no-auth-'));
  const account = {
    id: 'oauth-no-auth',
    name: 'OAuth',
    authKind: 'official_oauth',
    codexHome: path.join(root, 'oauth-home'),
  };
  try {
    fs.mkdirSync(account.codexHome, { recursive: true });
    fs.writeFileSync(path.join(account.codexHome, 'auth.json'), JSON.stringify({
      auth_mode: 'chatgpt',
      tokens: { id_token: 'id', access_token: 'access' },
    }));
    const harness = createServiceHarness({
      root,
      account,
      timeoutMs: 7_000,
      onMessage: (message, child) => respondToInitializeAndStart(message, child),
    });
    const handle = harness.service.startOfficialLogin(account);
    await handle.ready;
    const completion = handle.completed.then(() => null, (error) => error);
    harness.child.send({
      method: 'account/login/completed',
      params: { loginId: 'login-test-1', success: true, error: null },
    });
    assert.ok(await completion instanceof Error);
    assert.equal(harness.service.hasValidOfficialOAuth(account), false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('credential polling requires all three ChatGPT OAuth tokens', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-auth-poll-'));
  const authPath = path.join(root, 'auth.json');
  try {
    fs.writeFileSync(authPath, JSON.stringify({
      auth_mode: 'chatgpt',
      tokens: { id_token: 'id', access_token: 'access' },
    }));
    await assert.rejects(waitForOfficialOAuthAuth(authPath, 20));
    writeOAuthAuth(root);
    await waitForOfficialOAuthAuth(authPath, 20);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('Access Token login remains separate and legacy device auth/browser code is absent', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-token-'));
  const calls = [];
  const account = {
    id: 'token-test',
    name: 'Token',
    authKind: 'access_token',
    codexHome: path.join(root, 'token-home'),
  };
  try {
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      patGateway: { ensureReady: async () => {} },
      commandRunner: async (request) => {
        calls.push(request);
        return { code: 0, stdout: 'Logged in using a personal access token' };
      },
    });
    await service.login(account, 'test-personal-access-token');
    assert.deepEqual(calls[0].args, ['login', '--with-access-token']);
    assert.equal(calls[0].stdinText, 'test-personal-access-token');
    assert.equal(calls[0].env.CODEX_HOME, account.codexHome);

    await assert.rejects(service.login({ ...account, authKind: 'official_oauth' }));
    assert.equal(calls.length, 1, 'official OAuth must not fall back to the legacy login command');

    const source = fs.readFileSync(path.join(__dirname, '..', 'src', 'services', 'codex-cli.js'), 'utf8');
    assert.doesNotMatch(source, /--device-auth|createDeviceAuthOutputHandler|cleanupTemporaryBrowserProfile|browserLauncher/);
    assert.match(source, /desktopProfile/);
    assert.match(source, /account\/login\/start/);
    assert.match(source, /account\/login\/completed/);
    assert.match(source, /account\/login\/cancel/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('Access Token CLI and launch paths require the local PAT gateway before use', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-pat-runtime-'));
  try {
    const account = { id: 'pat', authKind: 'access_token', codexHome: root };
    let gatewayStarts = 0;
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      patGateway: { ensureReady: async () => { gatewayStarts += 1; } },
      commandRunner: async () => ({ code: 0, stdout: 'logged in', stderr: '' }),
    });
    await service.status(account);
    await service.login(account, 'at-test-only-not-a-real-token-123456');
    assert.equal(gatewayStarts, 2);

    const compatible = { id: 'api', authKind: 'compatible_api', codexHome: root };
    await service.status(compatible);
    assert.equal(gatewayStarts, 2, 'compatible API accounts never depend on the PAT gateway');

    const failClosed = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      commandRunner: async () => ({ code: 0, stdout: 'must not run', stderr: '' }),
    });
    await assert.rejects(
      failClosed.status(account),
      (error) => error?.code === 'PAT_GATEWAY_UNAVAILABLE',
    );
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('buildChildEnvironment strips credential variables and isolates SQLite state', () => {
  const keys = ['CODEX_HOME', 'CODEX_SQLITE_HOME', 'OPENAI_API_KEY', 'OPENAI_ACCESS_TOKEN', 'CODEX_ACCESS_TOKEN', 'AZURE_OPENAI_API_KEY'];
  const saved = Object.fromEntries(keys.map((key) => [key, process.env[key]]));
  try {
    for (const key of keys) process.env[key] = `secret-${key}`;
    const env = buildChildEnvironment('/isolated/codex-home');
    assert.equal(env.CODEX_HOME, '/isolated/codex-home');
    assert.equal(env.CODEX_SQLITE_HOME, '/isolated/codex-home');
    assert.equal(env.OPENAI_API_KEY, undefined);
    assert.equal(env.OPENAI_ACCESS_TOKEN, undefined);
    assert.equal(env.CODEX_ACCESS_TOKEN, undefined);
    assert.equal(env.AZURE_OPENAI_API_KEY, undefined);
  } finally {
    for (const [key, value] of Object.entries(saved)) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
  }
});

test('official ChatGPT desktop launch isolates the profile, reports startup, and scopes theme debugging', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-codex-app-'));
  try {
    const application = createCodexApplication(root, {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const { appPath, executable } = application;
    const projectPath = path.join(root, 'project');
    const accountHome = path.join(root, 'account');
    fs.mkdirSync(projectPath);
    fs.mkdirSync(accountHome);
    const calls = [];
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
      processStartupTimeoutMs: 20,
      settingsProvider: () => ({
        projectPath,
        proxyAutoDetect: false,
        proxyScheme: 'http',
        proxyAddress: '127.0.0.1',
        proxyPort: 7890,
      }),
      spawnProcess: (command, args, options) => {
        calls.push({ command, args, options });
        const child = new EventEmitter();
        child.pid = 4321 + calls.length;
        child.unref = () => {};
        queueMicrotask(() => child.emit('spawn'));
        return child;
      },
    });
    const result = await service.launchCodexApp({ id: 'account-1', codexHome: accountHome });
    assert.equal(result.ok, true);
    assert.equal(result.appKind, 'chatgpt');
    assert.equal(calls.length, 1);
    assert.equal(calls[0].command, executable);
    assert.ok(calls[0].args.some((value) => value.startsWith('--user-data-dir=')));
    assert.ok(calls[0].args.some((value) => value.startsWith('codex://threads/new?path=')));
    assert.equal(calls[0].args.some((value) => value.startsWith('--remote-debugging-port=')), false);
    assert.equal(calls[0].options.cwd, projectPath);
    assert.equal(calls[0].options.env.CODEX_HOME, accountHome);
    assert.equal(calls[0].options.env.CODEX_SQLITE_HOME, accountHome);
    assert.equal(calls[0].options.env.HTTPS_PROXY, 'http://127.0.0.1:7890');

    const themed = await service.launchCodexApp(
      { id: 'account-1', codexHome: accountHome },
      null,
      { remoteDebuggingPort: 9222, themeDebugProfile: true },
    );
    assert.equal(themed.desktopProfile, path.join(accountHome, 'desktop-profile-theme'));
    assert.ok(calls[1].args.includes('--remote-debugging-address=127.0.0.1'));
    assert.ok(calls[1].args.includes('--remote-debugging-port=9222'));
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('desktop process-list command failures are reported instead of becoming an empty snapshot', () => {
  const failures = [
    () => { throw new Error('ps unavailable'); },
    () => ({ status: null, error: new Error('spawn timeout'), stdout: '' }),
    () => ({ status: 1, stdout: '' }),
  ];
  for (const runner of failures) {
    assert.throws(
      () => readDesktopProcessList(runner),
      (error) => error.code === 'DESKTOP_PROCESS_DISCOVERY_FAILED' && /操作已取消/.test(error.message),
    );
  }
  assert.equal(readDesktopProcessList(() => ({ status: 0, stdout: '' })), '');
});

test('desktop process liveness treats signal-terminated tracked children as exited', async () => {
  const service = new CodexCliService({
    resourcesPath: os.tmpdir(),
    userDataPath: os.tmpdir(),
    platform: 'darwin',
  });
  assert.equal(service.desktopProcessIsAlive({
    pid: 8_001,
    child: { exitCode: null, signalCode: null },
  }), true);
  assert.equal(service.desktopProcessIsAlive({
    pid: 8_002,
    child: { exitCode: null, signalCode: 'SIGTERM' },
  }), false);
  assert.equal(service.desktopProcessIsAlive({
    pid: 8_003,
    child: { exitCode: 0, signalCode: null },
  }), false);

  const trackedChild = {
    exitCode: null,
    signalCode: null,
    kill(signal) {
      this.signalCode = signal;
      return true;
    },
  };
  service.listManagedCodexAppProcesses = () => [{ pid: 8_004, child: trackedChild }];
  assert.deepEqual((await service.stopManagedCodexApps([], { timeoutMs: 100 })).stoppedPids, [8_004]);
  assert.equal(trackedChild.signalCode, 'SIGTERM');
});

test('managed-process fallback is scoped to the accounts requested for shutdown', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-desktop-stop-scope-'));
  try {
    const application = createCodexApplication(root, {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const accountA = { id: 'account-a', codexHome: path.join(root, 'account-a') };
    const accountB = { id: 'account-b', codexHome: path.join(root, 'account-b') };
    fs.mkdirSync(accountA.codexHome);
    fs.mkdirSync(accountB.codexHome);
    const alive = new Set([8_101, 8_102]);
    const kills = [];
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [application.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
      processListRunner: () => '',
      processAlive: (pid) => alive.has(pid),
      processKiller: (pid, signal) => {
        kills.push({ pid, signal });
        alive.delete(pid);
      },
    });
    service.desktopProcesses.set(8_101, {
      pid: 8_101,
      accountId: accountA.id,
      desktopProfile: path.join(accountA.codexHome, 'desktop-profile'),
      child: null,
    });
    service.desktopProcesses.set(8_102, {
      pid: 8_102,
      accountId: accountB.id,
      desktopProfile: path.join(accountB.codexHome, 'desktop-profile'),
      child: null,
    });

    const result = await service.stopManagedCodexApps([accountA]);

    assert.deepEqual(result.stoppedPids, [8_101]);
    assert.deepEqual(kills, [{ pid: 8_101, signal: 'SIGTERM' }]);
    assert.equal(alive.has(8_102), true);
    assert.equal(service.desktopProcesses.has(8_102), true);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('desktop switch fails closed when process discovery fails and never launches the target', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-desktop-discovery-failure-'));
  try {
    const application = createCodexApplication(root, {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const projectPath = path.join(root, 'project');
    const account = { id: 'target', codexHome: path.join(root, 'target-account') };
    fs.mkdirSync(projectPath);
    fs.mkdirSync(account.codexHome);
    let launchCount = 0;
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [application.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
      settingsProvider: () => ({ projectPath }),
      processListRunner: () => { throw new Error('ps unavailable'); },
      spawnProcess: () => {
        launchCount += 1;
        throw new Error('target must not launch');
      },
    });

    await assert.rejects(
      service.switchCodexApp(account, [account], projectPath),
      (error) => error.code === 'DESKTOP_PROCESS_DISCOVERY_FAILED' && /操作已取消/.test(error.message),
    );
    assert.equal(launchCount, 0);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('desktop switch rejects target preflight failures before stopping the old desktop', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-desktop-preflight-'));
  try {
    const application = createCodexApplication(root, {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const projectPath = path.join(root, 'project');
    const targetHome = path.join(root, 'target-account');
    fs.mkdirSync(projectPath);
    fs.mkdirSync(targetHome);

    const createService = ({
      appSignatureReader = readTestApplicationSignature,
      patGateway = null,
    } = {}) => new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [application.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader,
      settingsProvider: () => ({ projectPath }),
      patGateway,
    });
    const expectPreflightFailure = async (service, account, selectedProjectPath, validator) => {
      let stopCount = 0;
      service.stopManagedCodexApps = async () => {
        stopCount += 1;
        return { stoppedPids: [], stoppedRecords: [] };
      };
      await assert.rejects(
        service.switchCodexApp(account, [account], selectedProjectPath),
        validator,
      );
      assert.equal(stopCount, 0, 'the existing desktop must remain running when target preflight fails');
    };

    await expectPreflightFailure(
      createService(),
      { id: 'missing-project', codexHome: targetHome },
      path.join(root, 'missing-project'),
      /项目启动目录不存在/,
    );

    await expectPreflightFailure(
      createService({ appSignatureReader: () => { throw new Error('invalid signature'); } }),
      { id: 'invalid-app', codexHome: targetHome },
      projectPath,
      (error) => error.code === 'CODEX_APP_NOT_FOUND',
    );

    await expectPreflightFailure(
      createService({
        patGateway: {
          ensureReady: async () => { throw new Error('gateway unavailable'); },
        },
      }),
      { id: 'gateway-failure', authKind: 'access_token', codexHome: targetHome },
      projectPath,
      /gateway unavailable/,
    );

    const unusableHome = path.join(root, 'account-home-is-a-file');
    fs.writeFileSync(unusableHome, 'not a directory');
    await expectPreflightFailure(
      createService(),
      { id: 'invalid-profile', codexHome: unusableHome },
      projectPath,
      (error) => error.code === 'DESKTOP_PROFILE_UNAVAILABLE',
    );
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('cross-account desktop switch stops only managed profiles and starts the target account independently', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-desktop-switch-'));
  try {
    const application = createCodexApplication(root, {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const projectPath = path.join(root, 'project');
    const oldAccount = { id: 'old', name: 'Old', codexHome: path.join(root, 'old-account') };
    const targetAccount = {
      id: 'target',
      name: 'Target',
      authKind: 'access_token',
      codexHome: path.join(root, 'target-account'),
    };
    fs.mkdirSync(projectPath);
    fs.mkdirSync(oldAccount.codexHome);
    fs.mkdirSync(targetAccount.codexHome);
    const managedProfile = path.join(oldAccount.codexHome, 'desktop-profile');
    const managedThemeProfile = path.join(oldAccount.codexHome, 'desktop-profile-theme');
    const alive = new Set([9_001, 9_002, 9_003, 9_005]);
    const kills = [];
    const launches = [];
    const sequence = [];
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [application.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: () => {
        sequence.push('app-validated');
        return readTestApplicationSignature();
      },
      processStartupTimeoutMs: 20,
      desktopStopTimeoutMs: 200,
      settingsProvider: () => ({ projectPath }),
      processListRunner: () => [
        `9001 ${application.executable} --user-data-dir=${managedProfile}`,
        `9002 ${application.executable} --user-data-dir="${managedThemeProfile}"`,
        `9003 ${application.executable} --user-data-dir=${path.join(root, 'ordinary-chatgpt-profile')}`,
        `9004 /usr/bin/other ${application.executable} --user-data-dir=${managedProfile}`,
        `9005 ${application.executable} --user-data-dir=${managedProfile}-backup`,
      ].join('\n'),
      processAlive: (pid) => alive.has(pid),
      processKiller: (pid, signal) => {
        sequence.push('old-desktop-stopped');
        kills.push({ pid, signal });
        alive.delete(pid);
      },
      spawnProcess: (command, args, options) => {
        sequence.push('target-launched');
        launches.push({ command, args, options });
        const child = new EventEmitter();
        child.pid = 9_100;
        child.exitCode = null;
        child.unref = () => {};
        child.kill = () => true;
        queueMicrotask(() => child.emit('spawn'));
        return child;
      },
      patGateway: {
        ensureReady: async () => { sequence.push('gateway-ready'); },
      },
    });

    const result = await service.switchCodexApp(
      targetAccount,
      [oldAccount, targetAccount],
      projectPath,
    );

    assert.deepEqual(result.stoppedPids.sort(), [9_001, 9_002]);
    assert.deepEqual(kills, [
      { pid: 9_001, signal: 'SIGTERM' },
      { pid: 9_002, signal: 'SIGTERM' },
    ]);
    assert.equal(alive.has(9_003), true, 'an ordinary ChatGPT profile must not be terminated');
    assert.equal(alive.has(9_005), true, 'a profile sharing the managed path prefix must not be terminated');
    assert.equal(launches.length, 1);
    assert.equal(launches[0].command, application.executable);
    assert.ok(launches[0].args.includes(`--user-data-dir=${path.join(targetAccount.codexHome, 'desktop-profile')}`));
    assert.equal(launches[0].options.env.CODEX_HOME, targetAccount.codexHome);
    assert.equal(launches[0].options.env.CODEX_SQLITE_HOME, targetAccount.codexHome);
    assert.equal(result.handedOff, false);
    assert.equal(result.switchedAccount, true);
    const appValidated = sequence.indexOf('app-validated');
    const gatewayReady = sequence.indexOf('gateway-ready');
    const firstStop = sequence.indexOf('old-desktop-stopped');
    assert.notEqual(appValidated, -1);
    assert.notEqual(gatewayReady, -1);
    assert.ok(firstStop > appValidated);
    assert.ok(firstStop > gatewayReady);
    assert.ok(sequence.indexOf('target-launched') > firstStop);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('cross-account desktop switch rejects a single-instance handoff', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-desktop-handoff-'));
  try {
    const application = createCodexApplication(root, {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const projectPath = path.join(root, 'project');
    const account = { id: 'target', codexHome: path.join(root, 'target-account') };
    fs.mkdirSync(projectPath);
    fs.mkdirSync(account.codexHome);
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [application.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
      processListRunner: () => '',
      processStartupTimeoutMs: 20,
      settingsProvider: () => ({ projectPath }),
      spawnProcess: () => {
        const child = new EventEmitter();
        child.pid = 9_200;
        child.exitCode = null;
        child.unref = () => {};
        queueMicrotask(() => {
          child.emit('spawn');
          child.exitCode = 0;
          child.emit('exit', 0, null);
        });
        return child;
      },
    });

    await assert.rejects(
      service.switchCodexApp(account, [account], projectPath),
      (error) => error.code === 'CROSS_ACCOUNT_HANDOFF' && /当前账号未更改/.test(error.message),
    );
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('failed target launch restores the previously running account', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-desktop-switch-rollback-'));
  try {
    const application = createCodexApplication(root, {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const projectPath = path.join(root, 'project');
    const oldAccount = { id: 'old', name: 'Old', codexHome: path.join(root, 'old-account') };
    const targetAccount = { id: 'target', name: 'Target', codexHome: path.join(root, 'target-account') };
    fs.mkdirSync(projectPath);
    fs.mkdirSync(oldAccount.codexHome);
    fs.mkdirSync(targetAccount.codexHome);
    const oldProfile = path.join(oldAccount.codexHome, 'desktop-profile');
    const alive = new Set([9_301]);
    const launches = [];
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [application.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
      processStartupTimeoutMs: 20,
      desktopStopTimeoutMs: 100,
      settingsProvider: () => ({ projectPath }),
      processListRunner: () => `9301 ${application.executable} --user-data-dir=${oldProfile}`,
      processAlive: (pid) => alive.has(pid),
      processKiller: (pid) => { alive.delete(pid); },
      spawnProcess: (_command, _args, options) => {
        launches.push(options.env.CODEX_HOME);
        const child = new EventEmitter();
        child.pid = launches.length === 1 ? 9_302 : 9_303;
        child.exitCode = null;
        child.unref = () => {};
        child.kill = () => true;
        queueMicrotask(() => {
          if (launches.length === 1) child.emit('error', new Error('injected target launch failure'));
          else child.emit('spawn');
        });
        return child;
      },
    });

    await assert.rejects(
      service.switchCodexApp(targetAccount, [oldAccount, targetAccount], projectPath),
      (error) => /injected target launch failure/.test(error.message) &&
        /原账号桌面已自动恢复/.test(error.message) &&
        error.rollbackRestoredAccountIds?.[0] === oldAccount.id,
    );
    assert.deepEqual(launches, [targetAccount.codexHome, oldAccount.codexHome]);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('Codex app discovery gives a configured path priority over other candidates', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-codex-app-priority-'));
  try {
    const configured = createCodexApplication(path.join(root, 'configured'));
    const injected = createCodexApplication(path.join(root, 'injected'));
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      settingsProvider: () => ({ codexAppPath: configured.appPath }),
      appCandidates: [injected.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
    });

    const result = service.findCodexApplication();
    assert.equal(result.appPath, fs.realpathSync(configured.appPath));
    assert.equal(result.executable, fs.realpathSync(configured.executable));
    assert.equal(result.appKind, 'codex-legacy');
    assert.deepEqual(result.diagnostics.map(({ source, ok }) => ({ source, ok })), [
      { source: 'settings', ok: true },
    ]);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('desktop app discovery uses explicit candidates in order and does not scan the whole disk', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-codex-app-spotlight-'));
  try {
    const chatGpt = createCodexApplication(path.join(root, 'chatgpt'), {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const legacyCodex = createCodexApplication(path.join(root, 'legacy'));
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [chatGpt.appPath, legacyCodex.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
    });

    const result = service.findCodexApplication();
    assert.equal(result.appPath, fs.realpathSync(chatGpt.appPath));
    assert.equal(result.executable, fs.realpathSync(chatGpt.executable));
    assert.equal(result.appKind, 'chatgpt');
    assert.deepEqual(result.diagnostics.map(({ source, ok }) => ({ source, ok })), [
      { source: 'injected', ok: true },
    ]);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('desktop app discovery falls back to legacy Codex.app when ChatGPT.app is invalid', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-codex-app-legacy-'));
  try {
    const invalidChatGpt = createCodexApplication(path.join(root, 'invalid-chatgpt'), {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
      bundleIdentifier: 'com.openai.chat',
    });
    const legacyCodex = createCodexApplication(path.join(root, 'legacy'));
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [invalidChatGpt.appPath, legacyCodex.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
    });

    const result = service.findCodexApplication();
    assert.equal(result.appPath, fs.realpathSync(legacyCodex.appPath));
    assert.equal(result.executable, fs.realpathSync(legacyCodex.executable));
    assert.equal(result.appKind, 'codex-legacy');
    assert.deepEqual(result.diagnostics.map(({ source, ok }) => ({ source, ok })), [
      { source: 'injected', ok: false },
      { source: 'injected', ok: true },
    ]);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('manual desktop app validation accepts exact app pairs and rejects mismatches', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-codex-app-validation-'));
  try {
    const chatGpt = createCodexApplication(path.join(root, 'chatgpt'), {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const legacyCodex = createCodexApplication(path.join(root, 'legacy'));
    const ordinaryChatGpt = createCodexApplication(path.join(root, 'ordinary-chatgpt'), {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
      bundleIdentifier: 'com.openai.chat',
    });
    const missingDeclaredChatGptExecutable = createCodexApplication(path.join(root, 'mismatched-chatgpt'), {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
      plistExecutableName: 'Codex',
    });
    const missingDeclaredCodexExecutable = createCodexApplication(path.join(root, 'mismatched-codex'), {
      executableName: 'Codex',
      plistExecutableName: 'ChatGPT',
    });
    const unrelated = createCodexApplication(path.join(root, 'unrelated'), {
      appName: 'Other.app',
      executableName: 'ChatGPT',
    });
    const fileNamedApp = path.join(root, 'file', 'Codex.app');
    fs.mkdirSync(path.dirname(fileNamedApp), { recursive: true });
    fs.writeFileSync(fileNamedApp, 'not an application');
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
    });

    assert.deepEqual(service.resolveCodexApplication(chatGpt.appPath), {
      appPath: fs.realpathSync(chatGpt.appPath),
      executable: fs.realpathSync(chatGpt.executable),
      appKind: 'chatgpt',
    });
    assert.deepEqual(service.resolveCodexApplication(legacyCodex.appPath), {
      appPath: fs.realpathSync(legacyCodex.appPath),
      executable: fs.realpathSync(legacyCodex.executable),
      appKind: 'codex-legacy',
    });
    assert.throws(
      () => service.resolveCodexApplication(ordinaryChatGpt.appPath),
      (error) => error.code === 'INVALID_CODEX_APP' &&
        error.diagnostics[0].reason.includes('普通 ChatGPT.app') &&
        error.diagnostics[0].reason.includes('com.openai.chat') &&
        error.diagnostics[0].reason.includes('codex app'),
    );
    const ordinaryOnly = new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [ordinaryChatGpt.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
    });
    assert.throws(
      () => ordinaryOnly.findCodexApplication(),
      (error) => error.code === 'CODEX_APP_NOT_FOUND' &&
        /检测到了不可用的桌面应用/.test(error.message) && /普通 ChatGPT\.app/.test(error.message),
    );

    for (const candidate of [
      missingDeclaredChatGptExecutable.appPath,
      missingDeclaredCodexExecutable.appPath,
      unrelated.appPath,
      fileNamedApp,
    ]) {
      assert.throws(
        () => service.resolveCodexApplication(candidate),
        (error) => error.code === 'INVALID_CODEX_APP' && /ChatGPT\.app/.test(error.message),
      );
    }

    assert.throws(
      () => service.findCodexApplication(),
      (error) => error.code === 'CODEX_APP_NOT_FOUND' &&
        /系统配置/.test(error.message) && Array.isArray(error.diagnostics),
    );
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('desktop app validation rejects damaged signatures and non-OpenAI signing teams', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-codex-signature-'));
  try {
    const application = createCodexApplication(root, {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const baseOptions = {
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [],
      appMetadataReader: readTestApplicationInfoPlist,
    };
    const damaged = new CodexCliService({
      ...baseOptions,
      appSignatureReader: () => { throw new Error('invalid signature'); },
    });
    assert.throws(
      () => damaged.resolveCodexApplication(application.appPath),
      (error) => error.code === 'INVALID_CODEX_APP' && /代码签名无效/.test(error.message),
    );

    const foreign = new CodexCliService({
      ...baseOptions,
      appSignatureReader: () => ({
        identifier: 'com.openai.codex',
        teamIdentifier: 'NOT_OPENAI',
      }),
    });
    assert.throws(
      () => foreign.resolveCodexApplication(application.appPath),
      (error) => error.code === 'INVALID_CODEX_APP' && /OpenAI 官方签名/.test(error.message),
    );
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('desktop launch rejects asynchronous spawn errors and early process exits', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-codex-process-'));
  try {
    const application = createCodexApplication(root, {
      appName: 'ChatGPT.app',
      executableName: 'ChatGPT',
    });
    const projectPath = path.join(root, 'project');
    const account = { id: 'account-process', codexHome: path.join(root, 'account') };
    fs.mkdirSync(projectPath);
    fs.mkdirSync(account.codexHome);
    const createService = (spawnProcess) => new CodexCliService({
      resourcesPath: root,
      userDataPath: root,
      platform: 'darwin',
      appCandidates: [application.appPath],
      appMetadataReader: readTestApplicationInfoPlist,
      appSignatureReader: readTestApplicationSignature,
      processStartupTimeoutMs: 20,
      settingsProvider: () => ({ projectPath }),
      spawnProcess,
    });
    const createChild = () => {
      const child = new EventEmitter();
      child.pid = 9090;
      child.unref = () => {};
      return child;
    };

    await assert.rejects(
      createService(() => {
        const child = createChild();
        queueMicrotask(() => child.emit('error', new Error('spawn failed')));
        return child;
      }).launchCodexApp(account),
      /无法启动 ChatGPT（Codex）桌面进程/,
    );

    await assert.rejects(
      createService(() => {
        const child = createChild();
        queueMicrotask(() => {
          child.emit('spawn');
          child.emit('exit', 9, null);
        });
        return child;
      }).launchCodexApp(account),
      /启动后立即退出/,
    );

    const handoff = await createService(() => {
      const child = createChild();
      queueMicrotask(() => {
        child.emit('spawn');
        child.emit('exit', 0, null);
      });
      return child;
    }).launchCodexApp(account);
    assert.equal(handoff.handedOff, true, 'exit code 0 is a normal Electron single-instance handoff');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

function createManagedTerminalHarness(root, {
  autoReady = true,
  termExits = true,
  terminalLaunchTimeoutMs = 1_000,
  terminalStopTimeoutMs = 100,
  terminalLegacyQuarantineMs = 250,
} = {}) {
  const projectPath = path.join(root, 'project');
  const codex = path.join(root, 'codex');
  fs.mkdirSync(projectPath, { recursive: true });
  fs.writeFileSync(codex, '#!/bin/sh\n');
  fs.chmodSync(codex, 0o755);
  const processes = new Map();
  const kills = [];
  const launcherScripts = new Map();
  const pendingLaunches = [];
  let nextPid = 20_000;
  const state = { autoReady, termExits, onKill: null, onSnapshot: null, snapshotCount: 0 };
  const serializeProcesses = () => {
    state.snapshotCount += 1;
    if (typeof state.onSnapshot === 'function') {
      state.onSnapshot({ count: state.snapshotCount, processes });
    }
    return [...processes.values()]
      .map((record) => `${record.pid} ${record.ppid} ${record.uid} ${record.command}`)
      .join('\n');
  };
  const confirmLaunch = (launch) => {
    if (launch.confirmed) return launch;
    launch.confirmed = true;
    if (!fs.existsSync(launch.descriptor.descriptorPath)) {
      launch.blocked = true;
      return launch;
    }
    try {
      fs.mkdirSync(launch.descriptor.claimPath, { mode: 0o700 });
    } catch (error) {
      if (error?.code !== 'EEXIST') throw error;
      launch.blocked = true;
      return launch;
    }
    fs.writeFileSync(
      path.join(launch.descriptor.claimPath, 'owner'),
      `${launch.descriptor.nonce}\n${launch.descriptor.accountId}\n${launch.wrapperPid}\n`,
      { mode: 0o600 },
    );
    fs.writeFileSync(
      launch.descriptor.readyPath,
      `${launch.descriptor.nonce}\n${launch.wrapperPid}\n${launch.childPid}\n`,
      { mode: 0o600 },
    );
    processes.set(launch.wrapperPid, {
      pid: launch.wrapperPid,
      ppid: 1,
      uid: 501,
      command: `/bin/zsh "${launch.descriptor.launcherPath}"`,
    });
    processes.set(launch.childPid, {
      pid: launch.childPid,
      ppid: launch.wrapperPid,
      uid: 501,
      command: `"${codex}" -C "${projectPath}"`,
    });
    fs.rmSync(launch.descriptor.launcherPath, { force: true });
    return launch;
  };
  const service = new CodexCliService({
    resourcesPath: root,
    userDataPath: path.join(root, 'data'),
    platform: 'darwin',
    processUid: 501,
    terminalLaunchTimeoutMs,
    terminalStopTimeoutMs,
    terminalLegacyQuarantineMs,
    settingsProvider: () => ({ projectPath, proxyPort: null }),
    terminalProcessListRunner: serializeProcesses,
    processKiller: (pid, signal) => {
      const record = processes.get(pid) || null;
      kills.push({ pid, signal, record: record ? { ...record } : null });
      if (typeof state.onKill === 'function') {
        const handled = state.onKill({ pid, signal, record, processes });
        if (handled === true) return true;
      }
      if (signal !== 'SIGKILL' && !state.termExits) return true;
      processes.delete(pid);
      if (record && record.ppid > 1) processes.delete(record.ppid);
      if (record) {
        for (const child of [...processes.values()]) {
          if (child.ppid === record.pid) processes.delete(child.pid);
        }
      }
      return true;
    },
    spawnProcess: (_command, args) => {
      const launcherPath = args[2];
      launcherScripts.set(launcherPath, fs.readFileSync(launcherPath, 'utf8'));
      const sessionsDir = path.join(root, 'data', 'terminal-sessions');
      const descriptorPath = fs.readdirSync(sessionsDir)
        .filter((name) => name.endsWith('.json'))
        .map((name) => path.join(sessionsDir, name))
        .find((candidate) => JSON.parse(fs.readFileSync(candidate, 'utf8')).launcherPath === launcherPath);
      const descriptor = JSON.parse(fs.readFileSync(descriptorPath, 'utf8'));
      const launch = {
        descriptor,
        wrapperPid: nextPid++,
        childPid: nextPid++,
        confirmed: false,
      };
      pendingLaunches.push(launch);
      if (state.autoReady) confirmLaunch(launch);
      const child = new EventEmitter();
      queueMicrotask(() => child.emit('close', 0, null));
      return child;
    },
  });
  service.getCodexPath = () => codex;
  return {
    codex,
    confirmLaunch,
    kills,
    launcherScripts,
    pendingLaunches,
    processes,
    projectPath,
    service,
    state,
  };
}

function createTerminalArtifacts(harness, account, nonce, {
  descriptorKind = 'valid',
  launcher = true,
  ready = null,
  readyTempPids = [],
  claim = null,
} = {}) {
  harness.service.ensureTerminalDirectories();
  fs.mkdirSync(account.codexHome, { recursive: true });
  const launchersDir = path.join(harness.service.userDataPath, 'terminal-launchers');
  const sessionsDir = path.join(harness.service.userDataPath, 'terminal-sessions');
  const descriptor = {
    version: 1,
    accountId: account.id,
    nonce,
    codexHome: account.codexHome,
    executable: harness.codex,
    workingDirectory: harness.projectPath,
    launcherPath: path.join(launchersDir, `codex-${account.id}-${nonce}.command`),
    descriptorPath: path.join(sessionsDir, `session-${account.id}-${nonce}.json`),
    readyPath: path.join(sessionsDir, `session-${account.id}-${nonce}.ready`),
    claimPath: path.join(sessionsDir, `claim-${account.id}-${nonce}`),
    createdAt: new Date(0).toISOString(),
  };
  if (descriptorKind === 'valid') {
    fs.writeFileSync(descriptor.descriptorPath, `${JSON.stringify(descriptor, null, 2)}\n`, { mode: 0o600 });
  } else if (descriptorKind === 'corrupt') {
    fs.writeFileSync(descriptor.descriptorPath, '{not-json\n', { mode: 0o600 });
  }
  if (launcher) fs.writeFileSync(descriptor.launcherPath, '#!/bin/zsh\n', { mode: 0o700 });
  if (ready) {
    fs.writeFileSync(
      descriptor.readyPath,
      `${nonce}\n${ready.wrapperPid}\n${ready.childPid}\n`,
      { mode: 0o600 },
    );
  }
  for (const pid of readyTempPids) {
    fs.writeFileSync(`${descriptor.readyPath}.tmp.${pid}`, `${nonce}\n0\n0\n`, { mode: 0o600 });
  }
  if (claim) {
    fs.mkdirSync(descriptor.claimPath, { mode: 0o700 });
    if (claim.wrapperPid) {
      fs.writeFileSync(
        path.join(descriptor.claimPath, 'owner'),
        `${nonce}\n${account.id}\n${claim.wrapperPid}\n`,
        { mode: 0o600 },
      );
    }
    for (const pid of claim.ownerTempPids || []) {
      fs.writeFileSync(
        path.join(descriptor.claimPath, `owner.tmp.${pid}`),
        `${nonce}\n${account.id}\n${pid}\n`,
        { mode: 0o600 },
      );
    }
  }
  return descriptor;
}

test('terminal launch uses the selected project instead of treating ~/.codex as project config', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-'));
  try {
    const projectPath = path.join(root, 'project');
    const accountHome = path.join(root, 'account');
    const codex = path.join(root, 'codex');
    fs.mkdirSync(projectPath);
    fs.mkdirSync(accountHome);
    fs.writeFileSync(codex, '#!/bin/sh\n');
    fs.chmodSync(codex, 0o755);
    const calls = [];
    let launcherScript = '';
    let processList = '';
    const service = new CodexCliService({
      resourcesPath: root,
      userDataPath: path.join(root, 'data'),
      platform: 'darwin',
      processUid: 501,
      terminalProcessListRunner: () => processList,
      settingsProvider: () => ({ projectPath, proxyPort: null }),
      spawnProcess: (command, args, options) => {
        calls.push({ command, args, options });
        const launcherPath = args[2];
        launcherScript = fs.readFileSync(launcherPath, 'utf8');
        const sessionsDir = path.join(root, 'data', 'terminal-sessions');
        const descriptorName = fs.readdirSync(sessionsDir).find((name) => name.endsWith('.json'));
        const descriptor = JSON.parse(fs.readFileSync(path.join(sessionsDir, descriptorName), 'utf8'));
        fs.mkdirSync(descriptor.claimPath, { mode: 0o700 });
        fs.writeFileSync(
          path.join(descriptor.claimPath, 'owner'),
          `${descriptor.nonce}\n${descriptor.accountId}\n7101\n`,
          { mode: 0o600 },
        );
        fs.writeFileSync(descriptor.readyPath, `${descriptor.nonce}\n7101\n7102\n`, { mode: 0o600 });
        processList = [
          `7101 1 501 /bin/zsh "${launcherPath}"`,
          `7102 7101 501 "${codex}" -C "${projectPath}"`,
        ].join('\n');
        fs.rmSync(launcherPath);
        const child = new EventEmitter();
        queueMicrotask(() => child.emit('close', 0, null));
        return child;
      },
    });
    service.getCodexPath = () => codex;
    const result = await service.launchTerminal({ id: 'account-1', codexHome: accountHome });
    assert.equal(result.projectPath, projectPath);
    assert.equal(result.pid, 7102);
    assert.match(calls[0].args[2], /codex-account-1-[0-9a-f-]{36}\.command$/i);
    assert.equal(fs.existsSync(calls[0].args[2]), false, 'the launcher is one-shot and removes itself after readiness');
    assert.match(launcherScript, new RegExp(`cd '${projectPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}'`));
    assert.match(launcherScript, /CODEX_SQLITE_HOME/);
    assert.match(launcherScript, /write_ready/);
    assert.match(launcherScript, /cleanup_session/);
    assert.match(launcherScript, /codex_pid="\$!"/);
    assert.match(launcherScript, /<\/dev\/tty >\/dev\/tty 2>&1 &/);
    assert.doesNotMatch(launcherScript, /\nexec\s/);
    assert.match(launcherScript, / -C /);
    assert.equal(calls[0].command, '/usr/bin/open');
    assert.equal(calls[0].options.detached, undefined);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('terminal launch waits for open and rejects asynchronous or non-zero failures', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-errors-'));
  try {
    const accountHome = path.join(root, 'account');
    const codex = path.join(root, 'codex');
    fs.mkdirSync(accountHome);
    fs.writeFileSync(codex, '#!/bin/sh\n');
    fs.chmodSync(codex, 0o755);

    const createService = (spawnProcess, terminalLaunchTimeoutMs = 10_000) => {
      const service = new CodexCliService({
        resourcesPath: root,
        userDataPath: path.join(root, 'data'),
        platform: 'darwin',
        terminalLaunchTimeoutMs,
        spawnProcess,
      });
      service.getCodexPath = () => codex;
      return service;
    };
    const account = { id: 'account-1', codexHome: accountHome };

    await assert.rejects(
      createService(() => {
        const child = new EventEmitter();
        queueMicrotask(() => child.emit('error', new Error('open unavailable')));
        return child;
      }).launchTerminal(account, root),
      /open unavailable/,
    );

    await assert.rejects(
      createService(() => {
        const child = new EventEmitter();
        queueMicrotask(() => child.emit('close', 7, null));
        return child;
      }).launchTerminal(account, root),
      /代码 7/,
    );

    let killedWith = null;
    await assert.rejects(
      createService(() => {
        const child = new EventEmitter();
        child.kill = (signal) => { killedWith = signal; };
        return child;
      }, 20).launchTerminal(account, root),
      /超时/,
    );
    assert.equal(killedWith, 'SIGKILL');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('terminal launch does not commit until the random session ready record and exact process identity are confirmed', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-ready-'));
  try {
    const harness = createManagedTerminalHarness(root, { autoReady: false });
    const accountHome = path.join(root, 'account-ready');
    fs.mkdirSync(accountHome);
    let settled = false;
    const launch = harness.service.launchTerminal({ id: 'account-ready', codexHome: accountHome })
      .finally(() => { settled = true; });
    await tick();
    await new Promise((resolve) => setTimeout(resolve, 40));
    assert.equal(settled, false, '/usr/bin/open success alone cannot confirm the launch');
    assert.equal(harness.pendingLaunches.length, 1);
    const pending = harness.pendingLaunches[0];
    assert.match(path.basename(pending.descriptor.launcherPath), /^codex-account-ready-[0-9a-f-]{36}\.command$/i);
    assert.match(path.basename(pending.descriptor.descriptorPath), /^session-account-ready-[0-9a-f-]{36}\.json$/i);
    harness.confirmLaunch(pending);
    const result = await launch;
    assert.equal(result.pid, pending.childPid);
    assert.equal(result.terminalSessionId, pending.descriptor.nonce);
    assert.equal(fs.existsSync(pending.descriptor.launcherPath), false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('managed Terminal shutdown stops every exact session for one account and preserves other accounts', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-stop-scope-'));
  try {
    const harness = createManagedTerminalHarness(root);
    const accountA = { id: 'account-alpha', codexHome: path.join(root, 'account-alpha') };
    const accountB = { id: 'account-beta', codexHome: path.join(root, 'account-beta') };
    fs.mkdirSync(accountA.codexHome);
    fs.mkdirSync(accountB.codexHome);
    const firstA = await harness.service.launchTerminal(accountA);
    const secondA = await harness.service.launchTerminal(accountA);
    const onlyB = await harness.service.launchTerminal(accountB);
    const result = await harness.service.stopManagedTerminalSessions(accountA);

    assert.equal(result.cleanedSessions, 2);
    assert.equal(result.removedLegacyLaunchers, 0);
    assert.deepEqual(new Set(result.stoppedPids), new Set([firstA.pid, secondA.pid]));
    assert.equal(harness.processes.has(firstA.pid), false);
    assert.equal(harness.processes.has(secondA.pid), false);
    assert.equal(harness.processes.has(onlyB.pid), true, 'another account Terminal session remains running');
    const remainingDescriptors = fs.readdirSync(path.join(root, 'data', 'terminal-sessions'))
      .filter((name) => name.endsWith('.json'));
    assert.equal(remainingDescriptors.length, 1);
    assert.match(remainingDescriptors[0], /account-beta/);
    assert.ok(harness.kills.every((call) => call.record?.uid === 501));
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('managed Terminal shutdown revalidates exact identity before TERM and KILL', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-stop-signals-'));
  try {
    const harness = createManagedTerminalHarness(root, { termExits: false });
    const account = { id: 'account-signals', codexHome: path.join(root, 'account-signals') };
    fs.mkdirSync(account.codexHome);
    const launched = await harness.service.launchTerminal(account);
    const launch = harness.pendingLaunches[0];
    harness.state.onKill = ({ pid, signal, processes }) => {
      if (signal !== 'SIGKILL') return false;
      const record = processes.get(pid);
      processes.delete(pid);
      if (record?.ppid > 1) processes.delete(record.ppid);
      return true;
    };

    const result = await harness.service.stopManagedTerminalSessions(account, { timeoutMs: 100 });

    assert.deepEqual(harness.kills.map(({ pid, signal }) => ({ pid, signal })), [
      { pid: launch.childPid, signal: 'SIGTERM' },
      { pid: launch.childPid, signal: 'SIGKILL' },
    ]);
    assert.deepEqual(result.stoppedPids, [launched.pid]);
    assert.equal(harness.kills.some(({ pid }) => pid === launch.wrapperPid), false, 'the wrapper is never signalled');
    assert.equal(harness.processes.size, 0);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('managed Terminal shutdown fails closed on UID, parent, command, or PID-reuse uncertainty', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-stop-identity-'));
  try {
    const harness = createManagedTerminalHarness(root, { termExits: false });
    const account = { id: 'account-identity', codexHome: path.join(root, 'account-identity') };
    fs.mkdirSync(account.codexHome);
    await harness.service.launchTerminal(account);
    const launch = harness.pendingLaunches[0];
    harness.processes.get(launch.childPid).uid = 999;
    await assert.rejects(
      harness.service.stopManagedTerminalSessions(account),
      (error) => error.code === 'TERMINAL_SESSION_UNCERTAIN' && /身份/.test(error.message),
    );
    assert.deepEqual(harness.kills, [], 'an invalid initial identity is never signalled');

    harness.processes.get(launch.childPid).uid = 501;
    harness.state.onKill = ({ pid, signal, processes }) => {
      if (pid === launch.childPid && signal === 'SIGTERM') {
        processes.get(pid).command = '/usr/bin/unrelated-process --pid-reused';
        return true;
      }
      return true;
    };
    await assert.rejects(
      harness.service.stopManagedTerminalSessions(account, { timeoutMs: 100 }),
      (error) => error.code === 'TERMINAL_SESSION_UNCERTAIN' && /身份/.test(error.message),
    );
    assert.equal(harness.kills.some((call) => call.signal === 'SIGKILL'), false, 'PID reuse is never sent SIGKILL');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('an unregistered Codex CLI blocks account deletion without broad signals', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-legacy-'));
  try {
    const harness = createManagedTerminalHarness(root);
    const account = { id: 'account-legacy', codexHome: path.join(root, 'account-legacy') };
    fs.mkdirSync(account.codexHome);
    harness.service.ensureTerminalDirectories();
    harness.processes.set(31_001, {
      pid: 31_001,
      ppid: 1,
      uid: 501,
      command: `"${harness.codex}" -C "${harness.projectPath}"`,
    });

    await assert.rejects(
      harness.service.stopManagedTerminalSessions(account),
      (error) => error.code === 'TERMINAL_SESSION_UNCERTAIN' && /无法归属账号/.test(error.message),
    );
    assert.deepEqual(harness.kills, [], 'an unregistered CLI is never guessed or broadly killed');
    assert.equal(harness.processes.has(31_001), true);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('managed shutdown fails closed if the wrapper disappears and the child is reparented', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-reparent-'));
  try {
    const harness = createManagedTerminalHarness(root, { termExits: false });
    const account = { id: 'account-reparent', codexHome: path.join(root, 'account-reparent') };
    fs.mkdirSync(account.codexHome);
    await harness.service.launchTerminal(account);
    const launch = harness.pendingLaunches[0];
    harness.state.onKill = ({ pid, signal, processes }) => {
      if (pid === launch.childPid && signal === 'SIGTERM') {
        processes.delete(launch.wrapperPid);
        processes.get(launch.childPid).ppid = 1;
        return true;
      }
      return true;
    };

    await assert.rejects(
      harness.service.stopManagedTerminalSessions(account, { timeoutMs: 100 }),
      (error) => error.code === 'TERMINAL_SESSION_UNCERTAIN' && /身份/.test(error.message),
    );
    assert.deepEqual(harness.kills.map(({ pid, signal }) => ({ pid, signal })), [
      { pid: launch.childPid, signal: 'SIGTERM' },
    ]);
    assert.equal(harness.processes.has(launch.childPid), true, 'a reparented or PID-reused process is never killed');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('manager atomic claim revokes an unclaimed launcher before a delayed wrapper can start', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-atomic-claim-'));
  try {
    const harness = createManagedTerminalHarness(root, { autoReady: false, terminalLaunchTimeoutMs: 1_000 });
    const account = { id: 'account-atomic', codexHome: path.join(root, 'account-atomic') };
    fs.mkdirSync(account.codexHome);
    const launchOutcome = harness.service.launchTerminal(account).then(
      (value) => ({ value }),
      (error) => ({ error }),
    );
    await tick();
    assert.equal(harness.pendingLaunches.length, 1);
    const pending = harness.pendingLaunches[0];

    const stopped = await harness.service.stopManagedTerminalSessions(account);
    assert.equal(stopped.cleanedSessions, 1);
    await new Promise((resolve) => setTimeout(resolve, 300));
    harness.confirmLaunch(pending);

    const outcome = await launchOutcome;
    assert.equal(outcome.error?.code, 'TERMINAL_SESSION_REVOKED');
    assert.equal(pending.blocked, true, 'the delayed wrapper loses the atomic claim/desriptor gate');
    assert.equal(harness.processes.size, 0);
    assert.equal(fs.existsSync(pending.descriptor.descriptorPath), false);
    assert.equal(fs.existsSync(pending.descriptor.launcherPath), false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('deleting one account does not reclaim another account stale or queued artifacts', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-cross-artifacts-'));
  try {
    const harness = createManagedTerminalHarness(root);
    const accountA = { id: 'account-stale-a', codexHome: path.join(root, 'account-stale-a') };
    const accountB = { id: 'account-target-b', codexHome: path.join(root, 'account-target-b') };
    const descriptorA = createTerminalArtifacts(
      harness,
      accountA,
      '11111111-1111-4111-8111-111111111111',
      {
        ready: { wrapperPid: 41_001, childPid: 41_002 },
        readyTempPids: [41_003],
        claim: { wrapperPid: 41_001, ownerTempPids: [41_004] },
      },
    );
    fs.mkdirSync(accountB.codexHome);

    const result = await harness.service.stopManagedTerminalSessions(accountB);

    assert.equal(result.cleanedSessions, 0);
    assert.equal(fs.existsSync(descriptorA.descriptorPath), true);
    assert.equal(fs.existsSync(descriptorA.readyPath), true);
    assert.equal(fs.existsSync(`${descriptorA.readyPath}.tmp.41003`), true);
    assert.equal(fs.existsSync(path.join(descriptorA.claimPath, 'owner.tmp.41004')), true);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('legacy fixed launcher quarantine blocks immediate retry and catches a delayed old CLI', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-legacy-quarantine-'));
  try {
    const harness = createManagedTerminalHarness(root, { terminalLegacyQuarantineMs: 250 });
    const account = { id: 'account-legacy-delay', codexHome: path.join(root, 'account-legacy-delay') };
    fs.mkdirSync(account.codexHome);
    harness.service.ensureTerminalDirectories();
    const legacyLauncher = path.join(
      harness.service.userDataPath,
      'terminal-launchers',
      'codex-account-legacy-delay.command',
    );
    const quarantine = path.join(
      harness.service.userDataPath,
      'terminal-sessions',
      'legacy-account-legacy-delay.quarantine.json',
    );
    fs.writeFileSync(legacyLauncher, '#!/bin/zsh\nexec /opt/homebrew/bin/codex\n', { mode: 0o700 });
    const delayed = setTimeout(() => {
      harness.processes.set(42_001, {
        pid: 42_001,
        ppid: 1,
        uid: 501,
        command: `/bin/zsh "${legacyLauncher}"`,
      });
      harness.processes.set(42_002, {
        pid: 42_002,
        ppid: 42_001,
        uid: 501,
        command: '/opt/homebrew/bin/codex -C /tmp/project',
      });
    }, 120);

    await assert.rejects(
      harness.service.stopManagedTerminalSessions(account),
      (error) => error.code === 'TERMINAL_LEGACY_LAUNCHER_REVOKED',
    );
    assert.equal(fs.existsSync(legacyLauncher), false);
    assert.equal(fs.existsSync(quarantine), true);
    await assert.rejects(
      harness.service.stopManagedTerminalSessions(account),
      (error) => error.code === 'TERMINAL_LEGACY_QUARANTINED',
    );

    await new Promise((resolve) => setTimeout(resolve, 300));
    await assert.rejects(
      harness.service.stopManagedTerminalSessions(account),
      (error) => error.code === 'TERMINAL_SESSION_UNCERTAIN' && /旧 Terminal/.test(error.message),
    );
    assert.deepEqual(harness.kills, []);
    harness.processes.delete(42_001);
    harness.processes.delete(42_002);
    const result = await harness.service.stopManagedTerminalSessions(account);
    assert.equal(result.cleanedSessions, 0);
    assert.equal(fs.existsSync(quarantine), false);
    clearTimeout(delayed);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('legacy fixed launcher already opened but not execed is never broadly signalled', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-legacy-pending-'));
  try {
    const harness = createManagedTerminalHarness(root);
    const account = { id: 'account-legacy-pending', codexHome: path.join(root, 'account-legacy-pending') };
    fs.mkdirSync(account.codexHome);
    harness.service.ensureTerminalDirectories();
    const legacyLauncher = path.join(
      harness.service.userDataPath,
      'terminal-launchers',
      'codex-account-legacy-pending.command',
    );
    fs.writeFileSync(legacyLauncher, '#!/bin/zsh\n', { mode: 0o700 });
    harness.processes.set(43_001, {
      pid: 43_001,
      ppid: 1,
      uid: 501,
      command: `/bin/zsh "${legacyLauncher}"`,
    });

    await assert.rejects(
      harness.service.stopManagedTerminalSessions(account),
      (error) => error.code === 'TERMINAL_LEGACY_LAUNCHER_REVOKED',
    );
    await new Promise((resolve) => setTimeout(resolve, 300));
    await assert.rejects(
      harness.service.stopManagedTerminalSessions(account),
      (error) => error.code === 'TERMINAL_SESSION_UNCERTAIN' && /旧 Terminal/.test(error.message),
    );
    assert.deepEqual(harness.kills, []);
    assert.equal(harness.processes.has(43_001), true);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('all known install locations, Homebrew/npm CLIs, and unknown app-server processes block deletion', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-unknown-paths-'));
  try {
    const harness = createManagedTerminalHarness(root);
    const account = { id: 'account-unknown-cli', codexHome: path.join(root, 'account-unknown-cli') };
    fs.mkdirSync(account.codexHome);
    const commands = [
      '"/Applications/Codex Account Manager.app/Contents/Resources/codex-cli/bin/codex" -C /tmp/project',
      `"${path.join(os.homedir(), 'Applications/Codex Account Manager.app/Contents/Resources/codex-cli/bin/codex')}" -C /tmp/project`,
      '/opt/homebrew/bin/codex -C /tmp/project',
      '/usr/local/bin/codex -C /tmp/project',
      '/usr/local/bin/node /usr/local/lib/node_modules/@openai/codex/bin/codex.js -C /tmp/project',
      `"${harness.codex}" app-server --stdio --disable plugins`,
    ];
    for (let index = 0; index < commands.length; index += 1) {
      harness.processes.clear();
      harness.processes.set(44_000 + index, {
        pid: 44_000 + index,
        ppid: 1,
        uid: 501,
        command: commands[index],
      });
      await assert.rejects(
        harness.service.stopManagedTerminalSessions(account),
        (error) => error.code === 'TERMINAL_SESSION_UNCERTAIN' && /无法归属账号/.test(error.message),
        commands[index],
      );
      assert.deepEqual(harness.kills, []);
    }
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('inactive ready and claim temporary crash artifacts are reclaimed exactly', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-temp-reclaim-'));
  try {
    const harness = createManagedTerminalHarness(root);
    const account = { id: 'account-temp-reclaim', codexHome: path.join(root, 'account-temp-reclaim') };
    const descriptor = createTerminalArtifacts(
      harness,
      account,
      '22222222-2222-4222-8222-222222222222',
      {
        ready: { wrapperPid: 45_001, childPid: 45_002 },
        readyTempPids: [45_003],
        claim: { wrapperPid: 45_001, ownerTempPids: [45_004] },
      },
    );

    const result = await harness.service.stopManagedTerminalSessions(account);

    assert.equal(result.cleanedSessions, 1);
    for (const artifact of [
      descriptor.descriptorPath,
      descriptor.launcherPath,
      descriptor.readyPath,
      `${descriptor.readyPath}.tmp.45003`,
      descriptor.claimPath,
    ]) assert.equal(fs.existsSync(artifact), false, artifact);
    assert.deepEqual(harness.kills, []);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('ready.tmp and owner.tmp PID reuse fail closed without cleanup or signals', async () => {
  const scenarios = [
    { label: 'ready tmp', readyTempPids: [46_001], claim: null },
    { label: 'owner tmp', readyTempPids: [], claim: { ownerTempPids: [46_001] } },
  ];
  for (let index = 0; index < scenarios.length; index += 1) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), `cam-macos-terminal-temp-reuse-${index}-`));
    try {
      const harness = createManagedTerminalHarness(root);
      const account = { id: `account-temp-reuse-${index}`, codexHome: path.join(root, `account-${index}`) };
      const nonce = `${index + 3}${index + 3}${index + 3}${index + 3}${index + 3}${index + 3}${index + 3}${index + 3}-3333-4333-8333-333333333333`;
      const descriptor = createTerminalArtifacts(harness, account, nonce, scenarios[index]);
      harness.processes.set(46_001, {
        pid: 46_001,
        ppid: 1,
        uid: 501,
        command: '/usr/bin/unrelated-process --pid-reused',
      });

      await assert.rejects(
        harness.service.stopManagedTerminalSessions(account),
        (error) => error.code === 'TERMINAL_SESSION_UNCERTAIN' && /PID 已被复用/.test(error.message),
        scenarios[index].label,
      );
      assert.equal(fs.existsSync(descriptor.descriptorPath), true);
      assert.deepEqual(harness.kills, []);
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  }
});

test('active orphan ready, launcher, or corrupt descriptor artifacts fail closed', async () => {
  const scenarios = [
    {
      label: 'orphan ready',
      options: { descriptorKind: 'none', launcher: false, ready: { wrapperPid: 47_001, childPid: 47_002 } },
      process: { pid: 47_002, ppid: 1, uid: 501, command: '/opt/homebrew/bin/codex -C /tmp/project' },
    },
    {
      label: 'orphan launcher',
      options: { descriptorKind: 'none', launcher: true },
      wrapper: true,
    },
    {
      label: 'corrupt descriptor',
      options: { descriptorKind: 'corrupt', launcher: false, ready: { wrapperPid: 47_001, childPid: 47_002 } },
      process: { pid: 47_002, ppid: 1, uid: 501, command: '/usr/local/bin/codex -C /tmp/project' },
    },
  ];
  for (let index = 0; index < scenarios.length; index += 1) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), `cam-macos-terminal-orphan-active-${index}-`));
    try {
      const harness = createManagedTerminalHarness(root);
      const account = { id: `account-orphan-active-${index}`, codexHome: path.join(root, `account-${index}`) };
      const nonce = `5555555${index + 1}-5555-4555-8555-555555555555`;
      const descriptor = createTerminalArtifacts(harness, account, nonce, scenarios[index].options);
      if (scenarios[index].wrapper) {
        harness.processes.set(47_001, {
          pid: 47_001,
          ppid: 1,
          uid: 501,
          command: `/bin/zsh "${descriptor.launcherPath}"`,
        });
      } else {
        harness.processes.set(scenarios[index].process.pid, scenarios[index].process);
      }

      await assert.rejects(
        harness.service.stopManagedTerminalSessions(account),
        (error) => error.code === 'TERMINAL_SESSION_UNCERTAIN',
        scenarios[index].label,
      );
      assert.deepEqual(harness.kills, []);
      assert.equal(harness.processes.size, 1);
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  }
});

test('inactive orphan ready, launcher, and corrupt descriptor artifacts are recoverable', async () => {
  const scenarios = [
    { descriptorKind: 'none', launcher: false, ready: { wrapperPid: 48_001, childPid: 48_002 } },
    { descriptorKind: 'none', launcher: true },
    { descriptorKind: 'corrupt', launcher: true, ready: { wrapperPid: 48_001, childPid: 48_002 } },
  ];
  for (let index = 0; index < scenarios.length; index += 1) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), `cam-macos-terminal-orphan-stale-${index}-`));
    try {
      const harness = createManagedTerminalHarness(root);
      const account = { id: `account-orphan-stale-${index}`, codexHome: path.join(root, `account-${index}`) };
      const nonce = `6666666${index + 1}-6666-4666-8666-666666666666`;
      const descriptor = createTerminalArtifacts(harness, account, nonce, scenarios[index]);

      await harness.service.stopManagedTerminalSessions(account);

      assert.equal(fs.existsSync(descriptor.descriptorPath), false);
      assert.equal(fs.existsSync(descriptor.launcherPath), false);
      assert.equal(fs.existsSync(descriptor.readyPath), false);
      assert.equal(fs.existsSync(descriptor.claimPath), false);
      assert.deepEqual(harness.kills, []);
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  }
});

test('wrapper EXIT cleanup racing ready and claim reads is treated as a completed shutdown', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-macos-terminal-exit-race-'));
  try {
    const harness = createManagedTerminalHarness(root);
    const account = { id: 'account-exit-race', codexHome: path.join(root, 'account-exit-race') };
    fs.mkdirSync(account.codexHome);
    await harness.service.launchTerminal(account);
    const launch = harness.pendingLaunches[0];
    const originalReadReady = harness.service.readTerminalReady.bind(harness.service);
    let raceInjected = false;
    harness.service.readTerminalReady = (descriptor) => {
      const ready = originalReadReady(descriptor);
      if (!raceInjected) {
        raceInjected = true;
        harness.processes.delete(launch.childPid);
        harness.processes.delete(launch.wrapperPid);
        fs.rmSync(descriptor.readyPath, { force: true });
        fs.rmSync(descriptor.descriptorPath, { force: true });
        fs.rmSync(path.join(descriptor.claimPath, 'owner'), { force: true });
        try { fs.rmdirSync(descriptor.claimPath); } catch (error) {
          if (error?.code !== 'ENOENT') throw error;
        }
      }
      return ready;
    };

    const result = await harness.service.stopManagedTerminalSessions(account);

    assert.equal(result.cleanedSessions, 1);
    assert.deepEqual(harness.kills, []);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('Terminal process discovery uses an untruncated single UID/PPID/command snapshot', () => {
  const calls = [];
  const output = readTerminalProcessList((command, args, options) => {
    calls.push({ command, args, options });
    return { status: 0, stdout: '420 1 501 /Applications/Codex Account Manager.app/Contents/Resources/codex-cli/bin/codex\n' };
  });
  assert.match(output, /420 1 501/);
  assert.equal(calls[0].command, '/bin/ps');
  assert.deepEqual(calls[0].args, ['-ww', '-axo', 'pid=,ppid=,uid=,command=']);
});
