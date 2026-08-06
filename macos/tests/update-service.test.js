const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { EventEmitter } = require('node:events');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { PassThrough } = require('node:stream');
const test = require('node:test');

const {
  compareVersions,
  normalizeVersion,
  _test: { downloadFile },
} = require('../src/services/update-service');

test('update versions use numeric ordering and tolerate a leading v', () => {
  assert.equal(normalizeVersion('v2.0.12'), '2.0.12');
  assert.equal(compareVersions('2.0.12', '2.0.11'), 1);
  assert.equal(compareVersions('2.0.12', '2.0.12'), 0);
  assert.equal(compareVersions('2.0.11', '2.0.12'), -1);
});

test('invalid update versions are ignored by the comparison helper', () => {
  assert.equal(normalizeVersion('latest'), null);
  assert.equal(compareVersions('latest', '1.1.5'), -1);
});

test('download follows a redirect before opening the destination and verifies the final payload', async () => {
  const payload = Buffer.from('verified update payload', 'utf8');
  const expectedSha256 = crypto.createHash('sha256').update(payload).digest('hex');
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'cam-update-test-'));
  const destination = path.join(root, 'update.zip');
  const requested = [];
  const requestImpl = (url, _options, callback) => {
    const request = new EventEmitter();
    request.setTimeout = () => request;
    request.destroy = (error) => { if (error) request.emit('error', error); };
    queueMicrotask(() => {
      requested.push(url.toString());
      const response = new PassThrough();
      response.statusCode = requested.length === 1 ? 302 : 200;
      response.headers = requested.length === 1
        ? { location: 'https://release-assets.githubusercontent.com/final.zip' }
        : {};
      callback(response);
      response.end(requested.length === 1 ? undefined : payload);
    });
    return request;
  };

  try {
    await downloadFile('https://github.com/example/update.zip', destination, expectedSha256, { requestImpl });
    assert.deepEqual(await fs.readFile(destination), payload);
    assert.equal(requested.length, 2);
    assert.equal((await fs.readdir(root)).filter((name) => name.includes('.partial-')).length, 0);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});
