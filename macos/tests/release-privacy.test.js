const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

test('release privacy scanner rejects extensionless secrets, private files, and slash-style local paths', async () => {
  const { isForbiddenPrivateFile, scanSensitiveTextFile } = await import('../scripts/release-privacy.mjs');
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'cam-release-privacy-'));
  try {
    assert.equal(isForbiddenPrivateFile(path.join(root, '.env.production')), true);
    assert.equal(isForbiddenPrivateFile(path.join(root, 'identity.pem')), true);
    const sentinel = path.join(root, 'notes');
    await fs.writeFile(sentinel, 'temporary path C:/Users/example/private', 'utf8');
    await assert.rejects(scanSensitiveTextFile(sentinel, '测试文件'), /Windows 用户路径/);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});
