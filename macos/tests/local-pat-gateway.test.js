const assert = require('node:assert/strict');
const http = require('node:http');
const test = require('node:test');

const {
  GATEWAY_CHALLENGE_HEADER,
  GATEWAY_MARKER_HEADER,
  GATEWAY_MARKER_VALUE,
  LocalPatGateway,
  createPatGatewayUpstreamPreparer,
  _test,
} = require('../src/services/local-pat-gateway');

const TEST_GATEWAY_SECRET = '11'.repeat(32);
const TEST_HEALTH_HEADERS = { [GATEWAY_CHALLENGE_HEADER]: 'ab'.repeat(32) };

function requestGateway(address, {
  path = '/backend-api/codex',
  method = 'POST',
  headers = {},
  body = '',
} = {}) {
  return new Promise((resolve, reject) => {
    const request = http.request({
      host: address.host,
      port: address.port,
      path,
      method,
      headers,
    }, (response) => {
      const chunks = [];
      response.on('data', (chunk) => chunks.push(chunk));
      response.on('end', () => resolve({
        status: response.statusCode,
        headers: response.headers,
        body: Buffer.concat(chunks).toString('utf8'),
      }));
    });
    request.on('error', reject);
    if (body) request.write(body);
    request.end();
  });
}

test('proxy preparation recovers after rejection and commits only a verified route', async () => {
  let proxyUrl = 'http://127.0.0.1:7890';
  let resolveCount = 0;
  const calls = [];
  const networkSession = {
    async setProxy(config) {
      calls.push(['setProxy', config.proxyRules]);
    },
    async closeAllConnections() {
      calls.push(['closeAllConnections']);
    },
    async resolveProxy(url) {
      resolveCount += 1;
      calls.push(['resolveProxy', url]);
      if (resolveCount === 1) throw new Error('temporary resolver failure');
      return 'PROXY 127.0.0.1:7890';
    },
  };
  const prepare = createPatGatewayUpstreamPreparer({
    networkSession,
    proxyUrlProvider: () => proxyUrl,
  });

  const first = prepare();
  const queuedRetry = prepare();
  await assert.rejects(first, /temporary resolver failure/);
  await queuedRetry;
  assert.deepEqual(calls.slice(0, 6).map((entry) => entry[0]), [
    'setProxy', 'closeAllConnections', 'resolveProxy',
    'setProxy', 'closeAllConnections', 'resolveProxy',
  ], 'a failed validation does not poison the queue or commit the proxy key');

  await prepare();
  assert.equal(calls.filter(([kind]) => kind === 'setProxy').length, 2,
    'a previously verified proxy is resolved again without needless reconfiguration');

  proxyUrl = 'socks5://127.0.0.1:1080';
  await prepare();
  assert.deepEqual(calls.slice(-3).map((entry) => entry[0]), [
    'setProxy', 'closeAllConnections', 'resolveProxy',
  ], 'changing nodes closes reusable connections before route validation');
});

test('proxy preparation rejects missing and direct-fallback routes, then remains retryable', async () => {
  let proxyUrl = '';
  const routes = ['PROXY 127.0.0.1:7890; DIRECT', 'SYSTEM', 'PROXY 127.0.0.1:7890'];
  let setProxyCount = 0;
  const prepare = createPatGatewayUpstreamPreparer({
    networkSession: {
      async setProxy() { setProxyCount += 1; },
      async closeAllConnections() {},
      async resolveProxy() { return routes.shift(); },
    },
    proxyUrlProvider: () => proxyUrl,
  });

  await assert.rejects(prepare(), (error) => error?.code === 'PROXY_REQUIRED');
  assert.equal(setProxyCount, 0);
  proxyUrl = 'http://127.0.0.1:7890';
  await assert.rejects(prepare(), (error) => error?.code === 'PROXY_REQUIRED');
  await assert.rejects(prepare(), (error) => error?.code === 'PROXY_REQUIRED');
  await prepare();
  assert.equal(setProxyCount, 3, 'every invalid route forces the next retry to re-apply the proxy');
  assert.equal(_test.isFailClosedProxyRoute('HTTPS proxy.local:443; SOCKS5 127.0.0.1:1080'), true);
  assert.equal(_test.isFailClosedProxyRoute('PROXY 127.0.0.1:7890; DIRECT'), false);
});

test('PAT gateway resolves workspace identity once and forwards only to fixed ChatGPT routes', async () => {
  const calls = [];
  const gateway = new LocalPatGateway({
    gatewaySecret: TEST_GATEWAY_SECRET,
    port: 0,
    prepareUpstream: async () => calls.push({ kind: 'prepare' }),
    fetchImpl: async (url, options) => {
      const headers = Object.fromEntries(new Headers(options?.headers).entries());
      calls.push({ kind: 'fetch', url: String(url), options, headers });
      if (String(url).includes('/whoami')) {
        return new Response(JSON.stringify({
          chatgpt_account_id: 'workspace_123',
          chatgpt_account_is_fedramp: true,
        }), { status: 200, headers: { 'content-type': 'application/json' } });
      }
      return new Response('data: {"ok":true}\n\n', {
        status: 200,
        headers: { 'content-type': 'text/event-stream', 'content-encoding': 'gzip', 'x-upstream-test': 'yes' },
      });
    },
  });
  const token = 'at-test-only-not-a-real-token-123456';
  try {
    const address = await gateway.ensureRunning();
    await gateway.ensureReady();
    const first = await requestGateway(address, {
      headers: {
        authorization: `Bearer ${token}`,
        'content-type': 'application/json',
        'x-codex-trace-id': 'trace-1',
        'x-codex-account-manager-secret': 'must-not-forward',
      },
      body: '{"model":"gpt-5.6-terra"}',
    });
    assert.equal(first.status, 200);
    assert.equal(first.headers[GATEWAY_MARKER_HEADER], GATEWAY_MARKER_VALUE);
    assert.equal(first.headers['content-type'], 'text/event-stream');
    assert.equal(first.headers['content-encoding'], undefined, 'fetch-decoded bodies must not retain compression metadata');
    assert.match(first.body, /"ok":true/);

    const fetchCalls = calls.filter((entry) => entry.kind === 'fetch');
    assert.equal(fetchCalls.length, 2);
    assert.equal(fetchCalls[0].url, 'https://auth.openai.com/api/accounts/v1/user-auth-credential/whoami');
    assert.equal(fetchCalls[1].url, 'https://chatgpt.com/backend-api/codex/responses');
    assert.equal(fetchCalls[1].headers.authorization, `Bearer ${token}`);
    assert.equal(fetchCalls[1].headers['chatgpt-account-id'], 'workspace_123');
    assert.equal(fetchCalls[1].headers['x-openai-fedramp'], 'true');
    assert.equal(fetchCalls[1].headers['x-codex-trace-id'], 'trace-1');
    assert.equal(fetchCalls[1].headers['x-codex-account-manager-secret'], undefined);
    assert.equal(fetchCalls[1].headers.version, '0.144.1');
    assert.match(fetchCalls[1].headers['openai-beta'], /responses=experimental/);

    const second = await requestGateway(address, {
      path: '/backend-api/models?client=codex',
      method: 'GET',
      headers: { authorization: `Bearer ${token}` },
    });
    assert.equal(second.status, 200);
    assert.equal(calls.filter((entry) => entry.kind === 'fetch' && entry.url.includes('/whoami')).length, 1,
      'the hashed PAT identity cache avoids repeated whoami calls');
    assert.equal(calls.filter((entry) => entry.kind === 'fetch').at(-1).url,
      'https://chatgpt.com/backend-api/models?client=codex');
  } finally {
    await gateway.close();
  }
});

test('gateway rejects missing credentials, path escapes, unsupported origins, and absent proxy', async () => {
  let fetchCount = 0;
  let proxyAvailable = false;
  const gateway = new LocalPatGateway({
    gatewaySecret: TEST_GATEWAY_SECRET,
    port: 0,
    prepareUpstream: async () => {
      if (proxyAvailable) return;
      const error = new Error('proxy missing');
      error.code = 'PROXY_REQUIRED';
      throw error;
    },
    fetchImpl: async () => {
      fetchCount += 1;
      return new Response('{}', { status: 200 });
    },
  });
  try {
    await assert.rejects(gateway.ensureRunning(), (error) => error?.code === 'PROXY_REQUIRED');
    const address = gateway.address();
    assert.notEqual(address.port, 0, 'the listener remains available for a later readiness retry');
    await assert.rejects(gateway.ensureReady(), (error) => error?.code === 'PROXY_REQUIRED');
    const health = await requestGateway(address, { path: '/healthz', method: 'GET', headers: TEST_HEALTH_HEADERS });
    assert.equal(health.status, 503);

    const missing = await requestGateway(address, { path: '/backend-api/codex', method: 'GET' });
    assert.equal(missing.status, 401);

    const escaped = await requestGateway(address, {
      path: '/backend-api/%25252e%25252e%25252f/v1/models',
      method: 'GET',
      headers: { authorization: 'Bearer at-test-only-not-a-real-token-123456' },
    });
    assert.equal(escaped.status, 404);

    const unsupported = await requestGateway(address, {
      path: '/v1/models',
      method: 'GET',
      headers: { authorization: 'Bearer at-test-only-not-a-real-token-123456' },
    });
    assert.equal(unsupported.status, 404);

    const proxyRequired = await requestGateway(address, {
      path: '/backend-api/codex',
      method: 'GET',
      headers: { authorization: 'Bearer at-test-only-not-a-real-token-123456' },
    });
    assert.equal(proxyRequired.status, 503);
    assert.match(proxyRequired.body, /未检测到可用的本地代理/);
    assert.equal(fetchCount, 0);

    proxyAvailable = true;
    const recoveredAddress = await gateway.ensureRunning();
    assert.equal(recoveredAddress.port, address.port, 'readiness retries reuse the owned listener');
    const recoveredHealth = await requestGateway(address, {
      path: '/healthz',
      method: 'GET',
      headers: TEST_HEALTH_HEADERS,
    });
    assert.equal(recoveredHealth.status, 200);
  } finally {
    await gateway.close();
  }
});

test('gateway redaction covers bare PAT, API keys, JWTs, and Bearer credentials', () => {
  const jwt = 'eyJaaaaaaaaaaaa.bbbbbbbbbbbb.cccccccccccc';
  const value = _test.redactSecrets(
    `at-test-only-not-a-real-token-123456 sk-test-only-not-a-real-key-123456 ${jwt} Bearer at-another-test-token-123456`,
  );
  assert.doesNotMatch(value, /at-test|sk-test|eyJ|at-another/);
  assert.match(value, /\[REDACTED\]/);
});
