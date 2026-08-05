import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { createReadStream } from 'node:fs';
import { mkdtemp, readFile, readdir, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { extractFile, listPackage } from '@electron/asar';
import { isForbiddenPrivateFile, scanSensitiveTextFile } from './release-privacy.mjs';

const ELECTRON_PAYLOAD = 'electron-v43.1.1-darwin-arm64.zip';
const ELECTRON_SHA256 = 'd6d0598d042ef4d146278d08d84deac9dde145eae31eb4f32ef46206d6bd6169';
const CODEX_CLI_PAYLOAD = 'openai-codex-0.144.1-darwin-arm64.tgz';
const CODEX_CLI_SHA256 = '365a5685170f66bad58dd1dabb0462dfb824f82a870bcc8d9af2eb0a41cf2e18';
const LOOSE_FILES = ['一键安装.command', '卸载.command', 'README.md'];
const EXPECTED_CHECKSUM_FILES = new Set([
  `payload/${ELECTRON_PAYLOAD}`,
  `payload/${CODEX_CLI_PAYLOAD}`,
  'payload/app.asar',
  'payload/AppIcon.icns',
  ...LOOSE_FILES,
]);
const EXPECTED_RELEASE_FILES = new Set([...EXPECTED_CHECKSUM_FILES, 'SHA256SUMS.txt']);
const EXPECTED_RELEASE_DIRECTORIES = new Set(['payload']);
const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));

function usage() {
  console.error('用法：npm run verify:mac -- <zip路径>');
  process.exitCode = 2;
}

async function hashFile(filePath) {
  const hash = createHash('sha256');
  await new Promise((resolve, reject) => {
    const stream = createReadStream(filePath);
    stream.on('data', (chunk) => hash.update(chunk));
    stream.on('error', reject);
    stream.on('end', resolve);
  });
  return hash.digest('hex');
}

function extractZip(zipPath, destination) {
  const command = process.platform === 'win32' ? 'tar.exe' : 'tar';
  const result = spawnSync(command, ['-xf', zipPath, '-C', destination], {
    encoding: 'utf8',
    timeout: 120_000,
    windowsHide: true,
    maxBuffer: 4_194_304,
  });
  if (result.error || result.status !== 0) {
    throw new Error(`无法展开发布 ZIP：${result.error?.message || result.stderr || 'tar failed'}`);
  }
}

function runTar(argumentsList, { maxBuffer = 4_194_304 } = {}) {
  const command = process.platform === 'win32' ? 'tar.exe' : 'tar';
  const result = spawnSync(command, argumentsList, {
    encoding: 'utf8',
    timeout: 120_000,
    windowsHide: true,
    maxBuffer,
  });
  if (result.error || result.status !== 0) {
    throw new Error(`tar 校验失败：${result.error?.message || result.stderr || 'tar failed'}`);
  }
  return String(result.stdout || '');
}

async function collectTree(root) {
  const files = [];
  const directories = [];
  async function walk(current, relative) {
    const entries = await readdir(current, { withFileTypes: true });
    for (const entry of entries) {
      const childRelative = relative ? `${relative}/${entry.name}` : entry.name;
      const absolute = path.join(current, entry.name);
      if (entry.isDirectory()) {
        directories.push(childRelative);
        await walk(absolute, childRelative);
      } else if (entry.isFile()) {
        files.push(childRelative);
      } else {
        throw new Error(`发布校验拒绝非常规文件或符号链接：${childRelative}`);
      }
    }
  }
  await walk(root, '');
  files.sort();
  directories.sort();
  return { files, directories };
}

function assertExactSet(actualValues, expectedValues, label) {
  const actual = new Set(actualValues);
  const expected = new Set(expectedValues);
  const missing = [...expected].filter((value) => !actual.has(value));
  const extra = [...actual].filter((value) => !expected.has(value));
  if (actual.size !== expected.size || missing.length || extra.length) {
    throw new Error(`${label} 文件集合不一致；缺少：${missing.join(', ') || '无'}；额外：${extra.join(', ') || '无'}`);
  }
}

function verifyZipExecutableModes(zipPath) {
  const listing = runTar(['-tvf', zipPath]);
  const commandLines = listing.split(/\r?\n/).filter((line) => line.trim().endsWith('.command'));
  if (commandLines.length !== 2 || commandLines.some((line) => !/^-rwxr-xr-x\s/.test(line))) {
    throw new Error('发布 ZIP 中的两个 .command 脚本没有保持 0755 可执行权限。');
  }
}

function verifyCodexCliPayload(codexCliTgz) {
  const expectedFiles = new Set([
    'package/vendor/aarch64-apple-darwin/bin/codex',
    'package/vendor/aarch64-apple-darwin/bin/codex-code-mode-host',
    'package/vendor/aarch64-apple-darwin/codex-path/rg',
    'package/vendor/aarch64-apple-darwin/codex-resources/zsh/bin/zsh',
    'package/vendor/aarch64-apple-darwin/codex-package.json',
    'package/package.json',
    'package/README.md',
  ]);
  const entries = runTar(['-tzf', codexCliTgz]).split(/\r?\n/).filter(Boolean);
  assertExactSet(entries, expectedFiles, 'Codex CLI payload');

  const packageJson = JSON.parse(runTar(['-xOzf', codexCliTgz, 'package/package.json'], { maxBuffer: 1_048_576 }));
  const metadata = JSON.parse(runTar([
    '-xOzf', codexCliTgz, 'package/vendor/aarch64-apple-darwin/codex-package.json',
  ], { maxBuffer: 1_048_576 }));
  if (packageJson?.name !== '@openai/codex' || packageJson?.version !== '0.144.1-darwin-arm64' ||
      packageJson?.os?.length !== 1 || packageJson.os[0] !== 'darwin' ||
      packageJson?.cpu?.length !== 1 || packageJson.cpu[0] !== 'arm64' ||
      metadata?.version !== '0.144.1' || metadata?.target !== 'aarch64-apple-darwin' ||
      metadata?.entrypoint !== 'bin/codex') {
    throw new Error('Codex CLI metadata 与固定的 0.144.1 Apple Silicon 版本不一致。');
  }
}

function parseInternalChecksums(value) {
  const records = new Map();
  for (const line of String(value || '').split(/\r?\n/)) {
    if (!line || line.startsWith('#')) continue;
    const match = /^([a-f0-9]{64})\s{2}(.+)$/i.exec(line);
    if (!match || path.isAbsolute(match[2]) || match[2].split('/').includes('..')) {
      throw new Error(`内部 SHA256SUMS.txt 行格式无效：${line}`);
    }
    if (records.has(match[2])) throw new Error(`内部 SHA256SUMS.txt 存在重复文件：${match[2]}`);
    records.set(match[2], match[1].toLowerCase());
  }
  if (records.size !== EXPECTED_CHECKSUM_FILES.size ||
      [...records.keys()].some((name) => !EXPECTED_CHECKSUM_FILES.has(name))) {
    throw new Error('内部 SHA256SUMS.txt 文件清单不完整或包含未知条目。');
  }
  return records;
}

async function verifyInternalRelease(zipPath) {
  const temporaryRoot = await mkdtemp(path.join(tmpdir(), 'cam-macos-verify-'));
  try {
    extractZip(zipPath, temporaryRoot);
    const entries = await readdir(temporaryRoot, { withFileTypes: true });
    if (entries.length !== 1 || !entries[0].isDirectory()) throw new Error('发布 ZIP 顶层结构异常。');
    const releaseRoot = path.join(temporaryRoot, entries[0].name);
    const releaseTree = await collectTree(releaseRoot);
    assertExactSet(releaseTree.files, EXPECTED_RELEASE_FILES, '发布 ZIP');
    assertExactSet(releaseTree.directories, EXPECTED_RELEASE_DIRECTORIES, '发布 ZIP 目录');
    for (const relative of releaseTree.files) {
      const target = path.join(releaseRoot, ...relative.split('/'));
      if (isForbiddenPrivateFile(target)) throw new Error(`发布 ZIP 包含私密运行时文件：${relative}`);
      await scanSensitiveTextFile(target, '发布 ZIP 文件');
    }
    const checksums = parseInternalChecksums(await readFile(path.join(releaseRoot, 'SHA256SUMS.txt'), 'utf8'));
    for (const [relative, expected] of checksums) {
      const target = path.join(releaseRoot, ...relative.split('/'));
      const details = await stat(target);
      if (!details.isFile()) throw new Error(`发布文件不是普通文件：${relative}`);
      const actual = await hashFile(target);
      if (actual !== expected) throw new Error(`发布文件 SHA256 校验失败：${relative}`);
    }
    if (checksums.get(`payload/${ELECTRON_PAYLOAD}`) !== ELECTRON_SHA256 ||
        checksums.get(`payload/${CODEX_CLI_PAYLOAD}`) !== CODEX_CLI_SHA256) {
      throw new Error('官方 Electron 或 Codex CLI payload 未匹配固定发布哈希。');
    }
    const packagingRoot = path.resolve(SCRIPT_DIR, '..', 'packaging');
    for (const relative of LOOSE_FILES) {
      const packed = await readFile(path.join(releaseRoot, relative));
      const source = await readFile(path.join(packagingRoot, relative));
      if (!packed.equals(source)) throw new Error(`发布 ZIP 与当前 packaging 文件不一致：${relative}`);
    }
    verifyCodexCliPayload(path.join(releaseRoot, 'payload', CODEX_CLI_PAYLOAD));

    const appAsar = path.join(releaseRoot, 'payload', 'app.asar');
    const packageJson = JSON.parse(extractFile(appAsar, 'package.json').toString('utf8'));
    assertExactSet(Object.keys(packageJson || {}), ['name', 'productName', 'version', 'private', 'main'], 'app.asar package.json');
    if (packageJson?.name !== 'codex-account-manager-macos' ||
        packageJson?.productName !== 'Codex Account Manager' ||
        packageJson?.version !== '1.1.5' || packageJson?.private !== true || packageJson?.main !== 'src/main.js') {
      throw new Error('app.asar 版本或入口与发布版本不一致。');
    }
    const sourceRoot = path.resolve(SCRIPT_DIR, '..', 'src');
    const sourceTree = await collectTree(sourceRoot);
    const expectedAsarEntries = [
      '/package.json',
      '/src',
      ...sourceTree.directories.map((relative) => `/src/${relative}`),
      ...sourceTree.files.map((relative) => `/src/${relative}`),
    ];
    const actualAsarEntries = listPackage(appAsar).map((entry) =>
      `/${String(entry).replaceAll('\\', '/').replace(/^\/+/, '')}`);
    assertExactSet(actualAsarEntries, expectedAsarEntries, 'app.asar');
    for (const relative of sourceTree.files) {
      const packed = extractFile(appAsar, path.join('src', ...relative.split('/')));
      const source = await readFile(path.join(sourceRoot, ...relative.split('/')));
      if (!packed.equals(source)) throw new Error(`app.asar 与当前源码不一致：src/${relative}`);
    }
  } finally {
    await rm(temporaryRoot, { recursive: true, force: true });
  }
}

async function main() {
  const input = process.argv[2];
  if (!input) return usage();
  const zipPath = path.resolve(input);
  const sidecar = `${zipPath}.sha256`;
  const details = await stat(zipPath);
  if (!details.isFile() || details.size < 1_000_000) {
    throw new Error(`ZIP 不存在或体积异常：${zipPath}`);
  }
  const line = (await readFile(sidecar, 'utf8')).trim();
  const match = /^([a-f0-9]{64})\s{2}(.+)$/i.exec(line);
  if (!match) throw new Error(`SHA256 文件格式无效：${sidecar}`);
  if (match[2] !== path.basename(zipPath)) throw new Error('SHA256 文件中的文件名与 ZIP 不一致。');
  const actual = await hashFile(zipPath);
  if (actual.toLowerCase() !== match[1].toLowerCase()) {
    throw new Error(`SHA256 校验失败。\n预期：${match[1]}\n实际：${actual}`);
  }
  verifyZipExecutableModes(zipPath);
  await verifyInternalRelease(zipPath);
  console.log(`校验通过：${zipPath}`);
  console.log(`SHA256: ${actual}`);
}

main().catch((error) => {
  console.error(error?.stack || error);
  process.exitCode = 1;
});
