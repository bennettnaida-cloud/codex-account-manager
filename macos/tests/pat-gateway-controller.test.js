const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const http = require('node:http');
const path = require('node:path');
const test = require('node:test');
const {
  DAEMON_ARGUMENT,
  PatGatewayController,
  probePatGateway,
} = require('../src/services/pat-gateway-controller');
const {
  GATEWAY_BUILD_VERSION,
  GATEWAY_CHALLENGE_HEADER,
  GATEWAY_MARKER_HEADER,
  GATEWAY_MARKER_VALUE,
  GATEWAY_PID_HEADER,
  GATEWAY_PROOF_HEADER,
  GATEWAY_VERSION_HEADER,
  LocalPatGateway,
} = require('../src/services/local-pat-gateway');
const { createGatewayProof } = require('../src/services/gateway-secret');

const TEST_GATEWAY_SECRET = '22'.repeat(32);

function childProcessStub() {
  const child = new EventEmitter();
  child.unrefCount = 0;
  child.unref = () => { child.unrefCount += 1; };
  return child;
}

async function listen(server) {
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  return server.address().port;
}

test('health probing verifies both protocol ownership and the exact app version', async () => {
  let proxyReady = false;
  const gateway = new LocalPatGateway({
    gatewaySecret: TEST_GATEWAY_SECRET,
    port: 0,
    prepareUpstream: async () => {
      if (!proxyReady) {
        const error = new Error('proxy missing');
        error.code = 'PROXY_REQUIRED';
        throw error;
      }
    },
    fetchImpl: async () => new Response('{}', { status: 200 }),
  });
  try {
    const address = await gateway.ensureListening();
    const unready = await probePatGateway({ gatewaySecret: TEST_GATEWAY_SECRET, port: address.port });
    assert.equal(unready.owned, true);
    assert.equal(unready.ready, false);
    assert.equal(unready.version, GATEWAY_BUILD_VERSION);
    proxyReady = true;
    const ready = await probePatGateway({ gatewaySecret: TEST_GATEWAY_SECRET, port: address.port });
    assert.equal(ready.owned, true);
    assert.equal(ready.ready, true);
  } finally {
    await gateway.close();
  }
});

test('public marker and version cannot impersonate the gateway or obtain credentials', async () => {
  const captured = [];
  const fake = http.createServer((request, response) => {
    captured.push({ ...request.headers });
    response.statusCode = 200;
    response.setHeader(GATEWAY_MARKER_HEADER, GATEWAY_MARKER_VALUE);
    response.setHeader(GATEWAY_VERSION_HEADER, GATEWAY_BUILD_VERSION);
    response.setHeader(GATEWAY_PID_HEADER, String(process.pid));
    response.end('{"status":"ready"}\n');
  });
  const port = await listen(fake);
  try {
    const result = await probePatGateway({ gatewaySecret: TEST_GATEWAY_SECRET, port });
    assert.equal(result.reachable, true);
    assert.equal(result.authenticated, false);
    assert.equal(result.owned, false);
    assert.match(captured[0][GATEWAY_CHALLENGE_HEADER], /^[a-f0-9]{64}$/);
    assert.equal(captured[0].authorization, undefined);
    assert.equal(captured[0][GATEWAY_PROOF_HEADER], undefined);
    assert.doesNotMatch(JSON.stringify(captured[0]), new RegExp(TEST_GATEWAY_SECRET));
  } finally {
    await new Promise((resolve) => fake.close(resolve));
  }
});

test('a captured health proof cannot be replayed for a new nonce', async () => {
  let capturedProof = null;
  let requests = 0;
  const fake = http.createServer((request, response) => {
    requests += 1;
    const challenge = String(request.headers[GATEWAY_CHALLENGE_HEADER] || '');
    const proof = capturedProof || createGatewayProof(
      TEST_GATEWAY_SECRET,
      challenge,
      GATEWAY_MARKER_VALUE,
      GATEWAY_BUILD_VERSION,
      process.pid,
      200,
    );
    capturedProof ||= proof;
    response.statusCode = 200;
    response.setHeader(GATEWAY_MARKER_HEADER, GATEWAY_MARKER_VALUE);
    response.setHeader(GATEWAY_VERSION_HEADER, GATEWAY_BUILD_VERSION);
    response.setHeader(GATEWAY_PID_HEADER, String(process.pid));
    response.setHeader(GATEWAY_PROOF_HEADER, proof);
    response.end('{"status":"ready"}\n');
  });
  const port = await listen(fake);
  const nonces = [Buffer.alloc(32, 0x41), Buffer.alloc(32, 0x42)];
  try {
    const first = await probePatGateway({
      gatewaySecret: TEST_GATEWAY_SECRET,
      port,
      randomBytesImpl: () => nonces.shift(),
    });
    const replayed = await probePatGateway({
      gatewaySecret: TEST_GATEWAY_SECRET,
      port,
      randomBytesImpl: () => nonces.shift(),
    });
    assert.equal(first.owned, true);
    assert.equal(replayed.owned, false);
    assert.equal(replayed.authenticated, false);
    assert.equal(requests, 2);
  } finally {
    await new Promise((resolve) => fake.close(resolve));
  }
});

test('controller reuses only an owned ready gateway without spawning', async () => {
  let spawns = 0;
  const controller = new PatGatewayController({
    gatewaySecret: TEST_GATEWAY_SECRET,
    execPath: '/Applications/Codex Account Manager.app/Contents/MacOS/Codex Account Manager',
    platform: 'darwin',
    probeImpl: async () => ({ reachable: true, owned: true, ready: true, statusCode: 200 }),
    spawnImpl: () => { spawns += 1; return childProcessStub(); },
  });
  assert.equal((await controller.ensureReady()).baseUrl, 'http://127.0.0.1:8317');
  assert.equal(spawns, 0);
});

test('parallel starts create one detached packaged daemon and wait for its marker', async () => {
  let probes = 0;
  const spawnCalls = [];
  const child = childProcessStub();
  const controller = new PatGatewayController({
    gatewaySecret: TEST_GATEWAY_SECRET,
    execPath: '/Applications/Codex Account Manager.app/Contents/MacOS/Codex Account Manager',
    platform: 'darwin',
    environment: { ELECTRON_RUN_AS_NODE: '1', SAFE_VALUE: 'yes' },
    probeImpl: async () => {
      probes += 1;
      return probes >= 4
        ? { reachable: true, owned: true, ready: true, statusCode: 200 }
        : { reachable: false, owned: false, ready: false, error: Object.assign(new Error('refused'), { code: 'ECONNREFUSED' }) };
    },
    spawnImpl: (...args) => { spawnCalls.push(args); return child; },
    delayImpl: async () => {},
  });
  const [left, right] = await Promise.all([controller.ensureRunning(), controller.ensureRunning()]);
  assert.equal(left.baseUrl, right.baseUrl);
  assert.equal(spawnCalls.length, 1);
  assert.deepEqual(spawnCalls[0][1], [DAEMON_ARGUMENT]);
  assert.equal(spawnCalls[0][2].detached, true);
  assert.equal(spawnCalls[0][2].stdio, 'ignore');
  assert.equal(spawnCalls[0][2].env.ELECTRON_RUN_AS_NODE, undefined);
  assert.equal(spawnCalls[0][2].env.CODEX_ACCOUNT_MANAGER_PAT_GATEWAY_DAEMON, '1');
  assert.equal(child.unrefCount, 1);
});

test('owned but unready gateway fails closed and recovers without respawn', async () => {
  let ready = false;
  let spawns = 0;
  const controller = new PatGatewayController({
    gatewaySecret: TEST_GATEWAY_SECRET,
    execPath: '/Applications/Codex Account Manager.app/Contents/MacOS/Codex Account Manager',
    platform: 'darwin',
    probeImpl: async () => ({
      reachable: true,
      owned: true,
      ready,
      statusCode: ready ? 200 : 503,
      message: ready ? null : '未检测到可用的本地代理。',
    }),
    spawnImpl: () => { spawns += 1; return childProcessStub(); },
  });
  await assert.rejects(controller.ensureReady(), (error) => error?.code === 'PROXY_REQUIRED');
  ready = true;
  await controller.ensureReady();
  assert.equal(spawns, 0);
});

test('foreign listener is rejected instead of being trusted or replaced', async () => {
  const controller = new PatGatewayController({
    gatewaySecret: TEST_GATEWAY_SECRET,
    execPath: '/Applications/Codex Account Manager.app/Contents/MacOS/Codex Account Manager',
    platform: 'darwin',
    probeImpl: async () => ({ reachable: true, owned: false, ready: false, statusCode: 200 }),
  });
  await assert.rejects(controller.ensureRunning(), (error) => error?.code === 'PAT_GATEWAY_PORT_IN_USE');
});

test('same protocol with a stale build version is diagnosed explicitly', async () => {
  const controller = new PatGatewayController({
    gatewaySecret: TEST_GATEWAY_SECRET,
    execPath: '/Applications/Codex Account Manager.app/Contents/MacOS/Codex Account Manager',
    platform: 'darwin',
    probeImpl: async () => ({
      reachable: true,
      owned: false,
      incompatible: true,
      ready: false,
      version: '1.1.4',
      statusCode: 200,
    }),
  });
  await assert.rejects(
    controller.ensureRunning(),
    (error) => error?.code === 'PAT_GATEWAY_VERSION_MISMATCH' && /1\.1\.4/.test(error.message),
  );
});

test('development daemon receives the app path before its dedicated argument', async () => {
  const spawnCalls = [];
  let probes = 0;
  const controller = new PatGatewayController({
    gatewaySecret: TEST_GATEWAY_SECRET,
    execPath: '/tmp/Electron.app/Contents/MacOS/Electron',
    appPath: '/tmp/codex-account-manager/macos',
    packaged: false,
    platform: 'darwin',
    probeImpl: async () => {
      probes += 1;
      return probes >= 2
        ? { reachable: true, owned: true, ready: false, statusCode: 503 }
        : { reachable: false, owned: false, ready: false };
    },
    spawnImpl: (...args) => { spawnCalls.push(args); return childProcessStub(); },
    delayImpl: async () => {},
  });
  await controller.ensureRunning();
  assert.deepEqual(spawnCalls[0][1], [path.resolve('/tmp/codex-account-manager/macos'), DAEMON_ARGUMENT]);
});
