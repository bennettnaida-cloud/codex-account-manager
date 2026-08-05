const assert = require('node:assert/strict');
const test = require('node:test');

const {
  ThemeService,
  ThemeServiceError,
  buildCustomThemeCss,
  describeCustomTheme,
  loadBackgroundImageDataUrl,
  normalizeDebugPort,
  _test,
} = require('../src/services/theme-service');

const DEBUG_PORT = 9444;

function endpointPayloads(port = DEBUG_PORT) {
  return {
    '/json/version': {
      webSocketDebuggerUrl: `ws://127.0.0.1:${port}/devtools/browser/browser-1`,
    },
    '/json/list': [{
      id: 'page-1',
      type: 'page',
      title: 'Codex',
      url: 'app://codex/',
      webSocketDebuggerUrl: `ws://127.0.0.1:${port}/devtools/page/page-1`,
    }],
  };
}

function fakeFetch(payloads, requests = []) {
  return async (url, options) => {
    requests.push({ url: String(url), options });
    const parsed = new URL(url);
    const payload = payloads[parsed.pathname];
    if (payload === undefined) return { ok: false, status: 404, json: async () => ({}) };
    return { ok: true, status: 200, json: async () => payload };
  };
}

function fakeSessionFactory({ verification = null, status = null, calls = [], closed = [] } = {}) {
  return async ({ target, port }) => ({
    async open() {
      calls.push({ operation: 'open', targetId: target.id, port });
      return this;
    },
    async evaluate(expression) {
      if (expression.includes('codex-account-manager:probe')) {
        calls.push({ operation: 'probe' });
        return { codex: true, markers: { shell: true, sidebar: true, main: true } };
      }
      if (expression.includes('codex-account-manager:install')) {
        calls.push({ operation: 'install', expression });
        return { stylePresent: true };
      }
      if (expression.includes('codex-account-manager:verify-removed')) {
        calls.push({ operation: 'verify-removed' });
        return { codex: true, removed: true };
      }
      if (expression.includes('codex-account-manager:verify')) {
        calls.push({ operation: 'verify' });
        return verification;
      }
      if (expression.includes('codex-account-manager:remove')) {
        calls.push({ operation: 'remove' });
        return true;
      }
      if (expression.includes('codex-account-manager:status')) {
        calls.push({ operation: 'status' });
        return status || { codex: true, active: false, themeId: null, digest: null };
      }
      throw new Error('unexpected expression');
    },
    close() {
      closed.push(target.id);
    },
  });
}

test('debug port is mandatory and protected port 8317 is always rejected', async () => {
  assert.throws(() => normalizeDebugPort(), (error) => (
    error instanceof ThemeServiceError && error.code === 'PORT_REQUIRED'
  ));
  assert.throws(() => normalizeDebugPort(8317), (error) => (
    error instanceof ThemeServiceError && error.code === 'PROTECTED_PORT'
  ));
  assert.equal(normalizeDebugPort('9444'), 9444);

  let fetchCount = 0;
  const service = new ThemeService({ fetchImpl: async () => { fetchCount += 1; } });
  await assert.rejects(
    service.applyTheme({ port: 8317, themeId: 'manager-dark' }),
    (error) => error.code === 'PROTECTED_PORT',
  );
  const status = await service.getStatus({ port: 8317 });
  assert.equal(status.ok, false);
  assert.equal(status.code, 'PROTECTED_PORT');
  assert.equal(fetchCount, 0);
});

test('built-in themes expose metadata without leaking their CSS payload', () => {
  const service = new ThemeService();
  const themes = service.listThemes();
  assert.deepEqual(themes.map((theme) => theme.id), [
    'manager-light',
    'manager-porcelain-light',
    'manager-dark',
    'manager-nebula-dark',
  ]);
  assert.ok(themes.every((theme) => theme.builtIn && !Object.hasOwn(theme, 'css')));
  assert.ok(themes.every((theme) => ['light', 'dark'].includes(theme.appearance)));
  assert.ok(themes.every((theme) => theme.mode === theme.appearance));
  assert.ok(themes.every((theme) => /^#[0-9a-f]{6}$/i.test(theme.preview.accent)));
  assert.ok(themes.every((theme) => theme.accent === theme.preview.accent));
  assert.ok(themes.every((theme) => typeof theme.codeTheme === 'string'));

  const custom = describeCustomTheme({
    name: '我的主题', mode: 'dark', codeTheme: 'tokyo-night',
    accentColor: '#6173ff', surfaceColor: '#151b2d', textColor: '#f2f5fb',
  });
  const withCustom = service.listThemes({
    name: '我的主题', mode: 'dark', codeTheme: 'tokyo-night',
    accentColor: '#6173ff', surfaceColor: '#151b2d', textColor: '#f2f5fb',
  });
  assert.equal(custom.id, 'custom');
  assert.equal(custom.builtIn, false);
  assert.deepEqual(withCustom.at(-1), custom);
});

test('front-end custom theme object is validated and converted to self-contained CSS', async () => {
  const sourcePath = '/Users/test/Pictures/background.png';
  const imageData = `data:image/png;base64,${Buffer.from('safe-image').toString('base64')}`;
  let loadedPath = null;
  const theme = await buildCustomThemeCss({
    name: '本地星云',
    mode: 'dark',
    isDark: true,
    codeThemeId: 'tokyo-night',
    accentColor: '#6173ff',
    surfaceColor: '#151b2d',
    inkColor: '#f2f5fb',
    backgroundImagePath: sourcePath,
  }, {
    loadBackgroundImage: async (candidate) => {
      loadedPath = candidate;
      return imageData;
    },
  });

  assert.equal(loadedPath, sourcePath);
  assert.equal(theme.id, 'custom');
  assert.equal(theme.mode, 'dark');
  assert.equal(theme.codeTheme, 'tokyo-night');
  assert.deepEqual(theme.preview, { accent: '#6173ff', surface: '#151b2d', text: '#f2f5fb' });
  assert.equal(theme.css.includes(imageData), true);
  assert.equal(theme.css.includes(sourcePath), false);
  assert.equal(theme.digest, _test.themeDigest(theme.css));
});

test('background loader accepts only bounded PNG, JPEG or WebP bytes from absolute paths', async () => {
  const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00]);
  const result = await loadBackgroundImageDataUrl('/safe/background.png', {
    pathModule: { isAbsolute: () => true },
    realpath: async () => '/resolved/background.png',
    stat: async () => ({ size: png.length, isFile: () => true }),
    readFile: async () => png,
  });
  assert.equal(result, `data:image/png;base64,${png.toString('base64')}`);

  await assert.rejects(
    loadBackgroundImageDataUrl('relative.png', { pathModule: { isAbsolute: () => false } }),
    (error) => error.code === 'INVALID_BACKGROUND_PATH',
  );
  await assert.rejects(
    loadBackgroundImageDataUrl('/safe/not-image.txt', {
      pathModule: { isAbsolute: () => true },
      realpath: async () => '/resolved/not-image.txt',
      stat: async () => ({ size: 4, isFile: () => true }),
      readFile: async () => Buffer.from('text'),
    }),
    (error) => error.code === 'UNSUPPORTED_BACKGROUND',
  );
});

test('custom CSS is injected in memory and success is returned only after exact verification', async () => {
  const requests = [];
  const calls = [];
  const closed = [];
  const customCss = 'html { --private-test-value: "do-not-return"; }';
  const digest = _test.themeDigest(customCss);
  const verification = {
    codex: true,
    stylePresent: true,
    cssMatches: true,
    themeId: 'custom',
    rootThemeId: 'custom',
    digest,
  };
  const service = new ThemeService({
    fetchImpl: fakeFetch(endpointPayloads(), requests),
    sessionFactory: fakeSessionFactory({ verification, calls, closed }),
    now: () => Date.parse('2026-07-22T00:00:00.000Z'),
  });

  const result = await service.applyTheme({
    port: DEBUG_PORT,
    customName: '我的主题',
    customCss,
  });

  assert.equal(result.ok, true);
  assert.equal(result.status, 'applied');
  assert.equal(result.themeId, 'custom');
  assert.equal(result.digest, digest);
  assert.equal(result.targetCount, 1);
  assert.equal(JSON.stringify(result).includes('do-not-return'), false);
  assert.deepEqual(calls.map((call) => call.operation), ['open', 'probe', 'install', 'verify']);
  const installExpression = calls.find((call) => call.operation === 'install').expression;
  assert.equal(installExpression.includes('do-not-return'), true);
  assert.deepEqual(closed, ['page-1']);
  assert.deepEqual(requests.map((request) => new URL(request.url).pathname).sort(), [
    '/json/list',
    '/json/version',
  ]);
  assert.ok(requests.every((request) => request.url.startsWith(`http://127.0.0.1:${DEBUG_PORT}/`)));
});

test('failed renderer verification is reported and never presented as success', async () => {
  const calls = [];
  const service = new ThemeService({
    fetchImpl: fakeFetch(endpointPayloads()),
    sessionFactory: fakeSessionFactory({
      verification: {
        codex: true,
        stylePresent: true,
        cssMatches: false,
        themeId: 'manager-dark',
        rootThemeId: 'manager-dark',
        digest: 'wrong',
      },
      calls,
    }),
  });

  await assert.rejects(
    service.applyTheme({ port: DEBUG_PORT, themeId: 'manager-dark' }),
    (error) => error instanceof ThemeServiceError && error.code === 'THEME_VERIFICATION_FAILED',
  );
  assert.deepEqual(calls.map((call) => call.operation), ['open', 'probe', 'install', 'verify', 'remove']);
});

test('applyTheme accepts the front-end customTheme object contract', async () => {
  const customTheme = {
    name: 'Porcelain',
    mode: 'light',
    isDark: false,
    codeTheme: 'codex',
    accentColor: '#2f8f83',
    surfaceColor: '#fbfffd',
    textColor: '#213a36',
    backgroundImagePath: '',
  };
  const built = await buildCustomThemeCss(customTheme);
  const calls = [];
  const service = new ThemeService({
    fetchImpl: fakeFetch(endpointPayloads()),
    sessionFactory: fakeSessionFactory({
      verification: {
        codex: true,
        stylePresent: true,
        cssMatches: true,
        themeId: 'custom',
        rootThemeId: 'custom',
        digest: built.digest,
      },
      calls,
    }),
  });
  const result = await service.applyTheme({ port: DEBUG_PORT, customTheme });
  assert.equal(result.ok, true);
  assert.equal(result.custom, true);
  assert.equal(result.digest, built.digest);
  assert.deepEqual(calls.map((call) => call.operation), ['open', 'probe', 'install', 'verify']);
});

test('restore verifies removal from every verified Codex renderer', async () => {
  const calls = [];
  const service = new ThemeService({
    fetchImpl: fakeFetch(endpointPayloads()),
    sessionFactory: fakeSessionFactory({ calls }),
    now: () => Date.parse('2026-07-22T00:00:00.000Z'),
  });

  const result = await service.removeTheme({ port: DEBUG_PORT });
  assert.equal(result.ok, true);
  assert.equal(result.status, 'official');
  assert.equal(result.targetCount, 1);
  assert.deepEqual(calls.map((call) => call.operation), ['open', 'probe', 'remove', 'verify-removed']);
});

test('status distinguishes official, applied, mixed and unreachable states', async () => {
  const appliedCss = _test.BUILT_IN_THEMES.get('manager-light').css;
  const digest = _test.themeDigest(appliedCss);
  const applied = new ThemeService({
    fetchImpl: fakeFetch(endpointPayloads()),
    sessionFactory: fakeSessionFactory({
      status: { codex: true, active: true, themeId: 'manager-light', digest },
    }),
    now: () => Date.parse('2026-07-22T00:00:00.000Z'),
  });
  const appliedStatus = await applied.getStatus({ port: DEBUG_PORT });
  assert.equal(appliedStatus.ok, true);
  assert.equal(appliedStatus.status, 'applied');
  assert.equal(appliedStatus.themeId, 'manager-light');

  const unavailable = new ThemeService({
    fetchImpl: async () => { throw new Error('connection refused'); },
  });
  const unavailableStatus = await unavailable.getStatus({ port: DEBUG_PORT });
  assert.equal(unavailableStatus.ok, false);
  assert.equal(unavailableStatus.code, 'CDP_UNREACHABLE');
  assert.match(unavailableStatus.reason, /127\.0\.0\.1:9444/);
});

test('unsafe external or mismatched CDP WebSocket endpoints are rejected before opening a session', async () => {
  assert.throws(
    () => _test.validatedWebSocketUrl('ws://example.com:9444/devtools/page/page-1', DEBUG_PORT),
    (error) => error.code === 'CDP_INVALID_ENDPOINT',
  );
  assert.throws(
    () => _test.validatedWebSocketUrl('ws://127.0.0.1:9445/devtools/page/page-1', DEBUG_PORT),
    (error) => error.code === 'CDP_INVALID_ENDPOINT',
  );

  let sessionCount = 0;
  const payloads = endpointPayloads();
  payloads['/json/list'][0].webSocketDebuggerUrl = 'ws://example.com:9444/devtools/page/page-1';
  const service = new ThemeService({
    fetchImpl: fakeFetch(payloads),
    sessionFactory: async () => { sessionCount += 1; },
  });
  const status = await service.getStatus({ port: DEBUG_PORT });
  assert.equal(status.ok, false);
  assert.equal(status.code, 'CODEX_RENDERER_NOT_FOUND');
  assert.equal(sessionCount, 0);
});

test('custom CSS validation enforces a bounded non-empty in-memory payload', () => {
  assert.throws(
    () => _test.resolveTheme({ customCss: '   ' }),
    (error) => error.code === 'CUSTOM_CSS_REQUIRED',
  );
  assert.throws(
    () => _test.resolveTheme({ customCss: 'x'.repeat(_test.MAX_CUSTOM_CSS_BYTES + 1) }),
    (error) => error.code === 'CUSTOM_CSS_TOO_LARGE',
  );
  assert.equal(_test.resolveTheme({ customCss: 'body { color: red; }' }).custom, true);
});
