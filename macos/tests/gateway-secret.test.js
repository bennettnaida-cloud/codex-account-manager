const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const {
  createGatewayChallenge,
  createGatewayProof,
  gatewayProofMatches,
  gatewaySecretPath,
  loadGatewaySecret,
  loadOrCreateGatewaySecret,
} = require('../src/services/gateway-secret');

test('gateway secret is atomically created once and reused across manager and daemon processes', async () => {
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'cam-gateway-secret-'));
  try {
    const generated = await loadOrCreateGatewaySecret(root, { platform: 'win32' });
    assert.match(generated, /^[a-f0-9]{64}$/);
    assert.equal(await loadGatewaySecret(root, { platform: 'win32' }), generated);
    assert.equal(await loadOrCreateGatewaySecret(root, { platform: 'win32' }), generated);
    const details = await fs.promises.lstat(gatewaySecretPath(root));
    assert.equal(details.isFile(), true);
    assert.equal(details.nlink, 1);
  } finally {
    await fs.promises.rm(root, { recursive: true, force: true });
  }
});

test('parallel first creation converges on one complete secret without transient hard-link failures', async () => {
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'cam-gateway-secret-race-'));
  try {
    const values = await Promise.all(Array.from({ length: 12 }, () =>
      loadOrCreateGatewaySecret(root, { platform: 'win32' })));
    assert.equal(new Set(values).size, 1);
    assert.match(values[0], /^[a-f0-9]{64}$/);
    assert.equal((await fs.promises.lstat(gatewaySecretPath(root))).nlink, 1);
  } finally {
    await fs.promises.rm(root, { recursive: true, force: true });
  }
});

test('gateway secret loader fails closed for corrupt content instead of replacing it', async () => {
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'cam-gateway-secret-invalid-'));
  try {
    const secretPath = gatewaySecretPath(root);
    await fs.promises.mkdir(path.dirname(secretPath), { recursive: true });
    await fs.promises.writeFile(secretPath, 'truncated', 'utf8');
    await assert.rejects(
      loadOrCreateGatewaySecret(root, { platform: 'win32' }),
      (error) => error?.code === 'PAT_GATEWAY_SECRET_INVALID',
    );
  } finally {
    await fs.promises.rm(root, { recursive: true, force: true });
  }
});

test('health proof binds a fresh nonce, marker, version, pid, and status', () => {
  const secret = '34'.repeat(32);
  const challenge = createGatewayChallenge(() => Buffer.alloc(32, 0x56));
  const proof = createGatewayProof(secret, challenge, 'macos-v2', '1.1.5', 123, 200);
  assert.equal(gatewayProofMatches(secret, challenge, 'macos-v2', '1.1.5', 123, 200, proof), true);
  assert.equal(gatewayProofMatches(secret, '78'.repeat(32), 'macos-v2', '1.1.5', 123, 200, proof), false);
  assert.equal(gatewayProofMatches(secret, challenge, 'macos-v2', '1.1.4', 123, 200, proof), false);
  assert.equal(gatewayProofMatches(secret, challenge, 'macos-v2', '1.1.5', 124, 200, proof), false);
  assert.equal(gatewayProofMatches(secret, challenge, 'macos-v2', '1.1.5', 123, 503, proof), false);
});

test('macOS secret implementation explicitly enforces no-follow, UID, 0700, and 0600 checks', () => {
  const source = fs.readFileSync(path.join(__dirname, '..', 'src', 'services', 'gateway-secret.js'), 'utf8');
  assert.match(source, /O_NOFOLLOW/);
  assert.match(source, /details\.uid !== uid/);
  assert.match(source, /\(details\.mode & 0o777\) !== 0o700/);
  assert.match(source, /\(details\.mode & 0o777\) !== 0o600/);
  assert.ok(source.indexOf('assertSecureDirectory(directory, { platform, getUid, requireMode: false })') <
    source.indexOf('chmod(directory, 0o700)'), 'symlinks and ownership are checked before chmod follows the path');
});
