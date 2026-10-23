import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readdirSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

assert.equal(process.platform, 'darwin', 'Native macOS is required.');
assert.equal(process.arch, 'arm64', 'Apple Silicon is required.');
assert.equal(process.env.GITHUB_ACTIONS, 'true', 'This installer smoke test runs only on a disposable GitHub runner.');
const zip = path.resolve(process.argv[2]);
const root = mkdtempSync(path.join(tmpdir(), 'cam-macos-smoke-'));
const installed = '/Applications/Codex Account Manager.app';
assert.equal(existsSync(installed), false, 'The runner must not have an existing manager.');
function run(command, args, options = {}) {
  const result = spawnSync(command, args, { encoding: 'utf8', timeout: 180_000, maxBuffer: 4 * 1024 * 1024, ...options });
  assert.ifError(result.error);
  assert.equal(result.status, 0, `${command}: ${result.stdout}\n${result.stderr}`);
  return result.stdout;
}
try {
  run('/usr/bin/ditto', ['-x', '-k', zip, root]);
  const unpacked = readdirSync(root).find((entry) => entry.startsWith('CodexAccountManager-macOS-'));
  assert(unpacked, 'Release root missing');
  run('/bin/bash', [path.join(root, unpacked, '一键安装.command')], { env: { ...process.env, CAM_INSTALL_SKIP_LAUNCH: '1' } });
  run('/usr/bin/codesign', ['--verify', '--deep', '--strict', installed]);
  const cli = path.join(installed, 'Contents/Resources/codex-cli/bin/codex');
  assert.match(run('/usr/bin/lipo', ['-archs', cli]), /arm64/);
  assert.equal(run(cli, ['--version']).trim(), 'codex-cli 0.163.0');
  run(process.execPath, [new URL('../../tools/Test-ConfiguredModelList.cjs', import.meta.url).pathname,
    cli, new URL('../../assets/codex-models/gpt-6.1-sol.json', import.meta.url).pathname,
    'gpt-6.1-sol', 'gpt-6.1-sol']);
  try {
    run(path.join(installed, 'Contents/MacOS/Codex Account Manager'), [], {
      timeout: 30_000, env: { ...process.env, CAM_RELEASE_SMOKE_ROOT: root },
    });
  } catch (error) {
    const report = path.join(root, 'result.json');
    if (existsSync(report)) console.error('Isolated application smoke diagnostic:', readFileSync(report, 'utf8'));
    throw error;
  }
  const result = JSON.parse(readFileSync(path.join(root, 'result.json'), 'utf8'));
  assert.equal(result.passed, true, result.error);
  assert.equal(result.version, readFileSync(new URL('../../VERSION', import.meta.url), 'utf8').trim());
  assert.equal(result.rendererReady, true);
  console.log('Native install, ad-hoc signature, Apple Silicon CLI and rendered application smoke test passed:', result);
} finally {
  rmSync(root, { recursive: true, force: true });
}
