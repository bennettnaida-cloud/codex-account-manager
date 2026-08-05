import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { createReadStream, createWriteStream } from 'node:fs';
import {
  copyFile,
  cp,
  lstat,
  mkdir,
  mkdtemp,
  open,
  readFile,
  readlink,
  readdir,
  rename,
  rm,
  stat,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createPackageWithOptions, extractAll, getRawHeader } from '@electron/asar';
import { downloadArtifact } from '@electron/get';
import archiver from 'archiver';
import { isForbiddenPrivateFile, scanSensitiveTextFile } from './release-privacy.mjs';

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const MACOS_DIR = path.resolve(SCRIPT_DIR, '..');
const PROJECT_DIR = path.resolve(MACOS_DIR, '..');
const PACKAGING_DIR = path.join(MACOS_DIR, 'packaging');

const APP_NAME = 'Codex Account Manager';
const ELECTRON_VERSION = '43.1.1';
const ELECTRON_PAYLOAD_SHA256 = 'd6d0598d042ef4d146278d08d84deac9dde145eae31eb4f32ef46206d6bd6169';
const CODEX_CLI_VERSION = '0.144.1';
const CODEX_CLI_PAYLOAD_SHA256 = '365a5685170f66bad58dd1dabb0462dfb824f82a870bcc8d9af2eb0a41cf2e18';
const RELEASE_PREFIX = 'CodexAccountManager-macOS-一键安装版';
const ELECTRON_PAYLOAD_NAME = `electron-v${ELECTRON_VERSION}-darwin-arm64.zip`;
const CODEX_CLI_PAYLOAD_NAME = `openai-codex-${CODEX_CLI_VERSION}-darwin-arm64.tgz`;
const PAYLOAD_FILES = [ELECTRON_PAYLOAD_NAME, CODEX_CLI_PAYLOAD_NAME, 'app.asar', 'AppIcon.icns'];
const LOOSE_FILES = ['一键安装.command', '卸载.command', 'README.md'];

function parseArguments(argv) {
  const options = {
    output: null,
    releaseDate: process.env.RELEASE_DATE || null,
    keepStage: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--keep-stage') {
      options.keepStage = true;
      continue;
    }
    if (argument === '--output' && argv[index + 1]) {
      options.output = path.resolve(argv[++index]);
      continue;
    }
    if (argument.startsWith('--output=')) {
      options.output = path.resolve(argument.slice('--output='.length));
      continue;
    }
    if (argument === '--date' && argv[index + 1]) {
      options.releaseDate = argv[++index];
      continue;
    }
    if (argument.startsWith('--date=')) {
      options.releaseDate = argument.slice('--date='.length);
      continue;
    }
    throw new Error(`未知参数：${argument}`);
  }

  const dateParts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(new Date());
  const date = Object.fromEntries(dateParts.map(({ type, value }) => [type, value]));
  options.releaseDate ||= `${date.year}${date.month}${date.day}`;

  if (!/^\d{8}$/.test(options.releaseDate)) {
    throw new Error('--date 必须是 YYYYMMDD 格式。');
  }

  options.output ||= path.resolve(PROJECT_DIR, '..', `${RELEASE_PREFIX}-${options.releaseDate}.zip`);
  return options;
}

async function hashFile(filePath, algorithm = 'sha256', encoding = 'hex') {
  const hash = createHash(algorithm);
  await new Promise((resolve, reject) => {
    const stream = createReadStream(filePath);
    stream.on('data', (chunk) => hash.update(chunk));
    stream.on('error', reject);
    stream.on('end', resolve);
  });
  return hash.digest(encoding);
}

async function findFiles(root, predicate) {
  const matches = [];
  async function walk(current) {
    for (const entry of await readdir(current, { withFileTypes: true })) {
      const absolute = path.join(current, entry.name);
      if (entry.isDirectory()) await walk(absolute);
      else if (entry.isFile() && predicate(absolute, entry)) matches.push(absolute);
    }
  }
  await walk(root);
  return matches;
}

async function assertSourceReady() {
  const required = [
    path.join(MACOS_DIR, 'src', 'main.js'),
    path.join(MACOS_DIR, 'src', 'preload.js'),
    path.join(MACOS_DIR, 'src', 'renderer.html'),
    path.join(MACOS_DIR, 'assets', 'AppIcon.icns'),
  ];
  for (const file of required) {
    const details = await stat(file).catch(() => null);
    if (!details?.isFile() || details.size === 0) {
      throw new Error(`缺少 macOS 应用源文件：${file}`);
    }
  }
}

async function buildAppAsar(workRoot) {
  const sourceRoot = path.join(workRoot, 'asar-source');
  const appAsar = path.join(workRoot, 'app.asar');
  await mkdir(sourceRoot, { recursive: true });
  await cp(path.join(MACOS_DIR, 'src'), path.join(sourceRoot, 'src'), {
    recursive: true,
    dereference: true,
  });
  await writeFile(path.join(sourceRoot, 'package.json'), `${JSON.stringify({
    name: 'codex-account-manager-macos',
    productName: APP_NAME,
    version: '1.1.5',
    private: true,
    main: 'src/main.js',
  }, null, 2)}\n`, 'utf8');
  await createPackageWithOptions(sourceRoot, appAsar, {});
  return appAsar;
}

async function privacyScanAsar(appAsar, scanRoot) {
  console.log('执行 app.asar 凭据与隐私扫描……');
  const extracted = path.join(scanRoot, 'privacy-scan-asar');
  await mkdir(extracted, { recursive: true });
  extractAll(appAsar, extracted);

  for (const file of await findFiles(extracted, () => true)) {
    if (isForbiddenPrivateFile(file)) {
      throw new Error(`隐私扫描失败：app.asar 包含用户数据文件 ${file}`);
    }
    await scanSensitiveTextFile(file, 'app.asar 文件');
  }
}

async function validateElectronPayload(electronZip) {
  const details = await stat(electronZip);
  if (!details.isFile() || details.size < 100_000_000) {
    throw new Error(`Electron 官方 payload 体积异常：${electronZip} (${details.size} bytes)`);
  }
  const handle = await open(electronZip, 'r');
  try {
    const magic = Buffer.alloc(4);
    const { bytesRead } = await handle.read(magic, 0, magic.length, 0);
    if (bytesRead !== 4 || magic.toString('hex') !== '504b0304') {
      throw new Error(`Electron payload 不是有效 ZIP：${electronZip}`);
    }
  } finally {
    await handle.close();
  }
  const actualHash = await hashFile(electronZip);
  if (actualHash !== ELECTRON_PAYLOAD_SHA256) {
    throw new Error(`Electron ${ELECTRON_VERSION} 官方 payload SHA256 校验失败。`);
  }
}

async function validateCodexCliPayload(codexCliTgz) {
  const details = await stat(codexCliTgz);
  if (!details.isFile() || details.size < 100_000_000 || details.size > 250_000_000) {
    throw new Error(`Codex CLI 官方 payload 体积异常：${codexCliTgz} (${details.size} bytes)`);
  }
  const handle = await open(codexCliTgz, 'r');
  try {
    const magic = Buffer.alloc(2);
    const { bytesRead } = await handle.read(magic, 0, magic.length, 0);
    if (bytesRead !== 2 || magic.toString('hex') !== '1f8b') {
      throw new Error(`Codex CLI payload 不是有效的 gzip 包：${codexCliTgz}`);
    }
  } finally {
    await handle.close();
  }
  const actualHash = await hashFile(codexCliTgz);
  if (actualHash !== CODEX_CLI_PAYLOAD_SHA256) {
    throw new Error(`Codex CLI ${CODEX_CLI_VERSION} darwin-arm64 官方 payload SHA256 校验失败。`);
  }

  const tarCommand = process.platform === 'win32' ? 'tar.exe' : 'tar';
  const listing = spawnSync(tarCommand, ['-tzf', codexCliTgz], {
    encoding: 'utf8',
    timeout: 30_000,
    windowsHide: true,
    maxBuffer: 1_048_576,
  });
  if (listing.error || listing.status !== 0) {
    throw new Error(`无法检查 Codex CLI 官方 payload 结构：${listing.error?.message || listing.stderr || 'tar failed'}`);
  }
  const entries = String(listing.stdout || '').split(/\r?\n/).filter(Boolean);
  const expected = new Set([
    'package/vendor/aarch64-apple-darwin/bin/codex',
    'package/vendor/aarch64-apple-darwin/bin/codex-code-mode-host',
    'package/vendor/aarch64-apple-darwin/codex-path/rg',
    'package/vendor/aarch64-apple-darwin/codex-resources/zsh/bin/zsh',
    'package/vendor/aarch64-apple-darwin/codex-package.json',
    'package/package.json',
    'package/README.md',
  ]);
  if (entries.length !== expected.size || entries.some((entry) => !expected.has(entry))) {
    throw new Error('Codex CLI 官方 payload 文件清单与固定版本不一致。');
  }
}

async function validateIcon(iconPath) {
  const handle = await open(iconPath, 'r');
  try {
    const magic = Buffer.alloc(4);
    const { bytesRead } = await handle.read(magic, 0, magic.length, 0);
    if (bytesRead !== 4 || magic.toString('ascii') !== 'icns') {
      throw new Error(`AppIcon.icns 格式无效：${iconPath}`);
    }
  } finally {
    await handle.close();
  }
}

async function stagePayload(releaseRoot, appAsar, electronZip, codexCliTgz) {
  const payloadRoot = path.join(releaseRoot, 'payload');
  await mkdir(payloadRoot, { recursive: true });
  await copyFile(electronZip, path.join(payloadRoot, ELECTRON_PAYLOAD_NAME));
  await copyFile(codexCliTgz, path.join(payloadRoot, CODEX_CLI_PAYLOAD_NAME));
  await copyFile(appAsar, path.join(payloadRoot, 'app.asar'));
  await copyFile(path.join(MACOS_DIR, 'assets', 'AppIcon.icns'), path.join(payloadRoot, 'AppIcon.icns'));
  for (const file of LOOSE_FILES) {
    await copyFile(path.join(PACKAGING_DIR, file), path.join(releaseRoot, file));
  }
}

async function scanReleasePrivacy(releaseRoot) {
  for (const file of await findFiles(releaseRoot, () => true)) {
    if (isForbiddenPrivateFile(file)) {
      throw new Error(`隐私扫描失败：发布目录包含 ${file}`);
    }
    await scanSensitiveTextFile(file, '发布目录文件');
  }
}

async function writeChecksums(releaseRoot, appAsar) {
  const { headerString } = getRawHeader(appAsar);
  const headerHash = createHash('sha256').update(headerString).digest('hex');
  const lines = [
    `# ${APP_NAME} macOS Apple Silicon payload checksums`,
    `# Electron ${ELECTRON_VERSION}; Codex CLI ${CODEX_CLI_VERSION} darwin-arm64`,
    `# app.asar Electron header SHA256: ${headerHash}`,
  ];
  for (const file of PAYLOAD_FILES.map((name) => `payload/${name}`).concat(LOOSE_FILES)) {
    const target = path.join(releaseRoot, ...file.split('/'));
    lines.push(`${await hashFile(target)}  ${file}`);
  }
  await writeFile(path.join(releaseRoot, 'SHA256SUMS.txt'), `${lines.join('\n')}\n`, 'utf8');
}

async function readMagic(filePath) {
  const handle = await open(filePath, 'r');
  try {
    const buffer = Buffer.alloc(4);
    const { bytesRead } = await handle.read(buffer, 0, buffer.length, 0);
    return bytesRead === 4 ? buffer.toString('hex') : '';
  } finally {
    await handle.close();
  }
}

function isMachOMagic(hex) {
  return new Set(['feedface', 'cefaedfe', 'feedfacf', 'cffaedfe', 'cafebabe', 'bebafeca', 'cafebabf', 'bfbafeca']).has(hex);
}

async function shouldBeExecutable(filePath, relativePath) {
  if (relativePath.endsWith('.command')) return true;
  if (/\.(?:dylib|so)$/.test(relativePath)) return true;
  return isMachOMagic(await readMagic(filePath));
}

async function addTreeToZip(archive, root, prefix) {
  async function walk(current, relative) {
    const entries = await readdir(current, { withFileTypes: true });
    entries.sort((left, right) => left.name.localeCompare(right.name, 'en'));
    for (const entry of entries) {
      const absolute = path.join(current, entry.name);
      const childRelative = relative ? `${relative}/${entry.name}` : entry.name;
      const zipPath = `${prefix}/${childRelative}`;
      const details = await lstat(absolute);
      if (details.isSymbolicLink()) {
        archive.symlink(zipPath, await readlink(absolute), 0o120777);
      } else if (details.isDirectory()) {
        archive.append('', { name: `${zipPath}/`, mode: 0o40755 });
        await walk(absolute, childRelative);
      } else if (details.isFile()) {
        const mode = await shouldBeExecutable(absolute, `/${childRelative.replaceAll('\\', '/')}`) ? 0o100755 : 0o100644;
        const alreadyCompressed = /\.(?:zip|tgz|icns)$/i.test(childRelative);
        archive.append(createReadStream(absolute), {
          name: zipPath,
          mode,
          store: alreadyCompressed,
          date: new Date('1980-01-01T00:00:00Z'),
        });
      }
    }
  }
  archive.append('', { name: `${prefix}/`, mode: 0o40755 });
  await walk(root, '');
}

async function createZip(releaseRoot, destination) {
  await mkdir(path.dirname(destination), { recursive: true });
  const partial = `${destination}.part-${process.pid}`;
  await rm(partial, { force: true });
  const output = createWriteStream(partial, { flags: 'wx' });
  const archive = archiver('zip', { zlib: { level: 9 }, forceLocalTime: false });
  const completion = new Promise((resolve, reject) => {
    output.on('close', resolve);
    output.on('error', reject);
    archive.on('warning', (error) => error.code === 'ENOENT' ? console.warn(error.message) : reject(error));
    archive.on('error', reject);
  });
  archive.pipe(output);

  try {
    await addTreeToZip(archive, releaseRoot, path.basename(releaseRoot));
    await archive.finalize();
    await completion;
    await rm(destination, { force: true });
    await rename(partial, destination);
  } catch (error) {
    archive.abort();
    output.destroy();
    await completion.catch(() => {});
    await rm(partial, { force: true });
    throw error;
  }
}

async function removeSupersededMacReleases(output, hashPath) {
  const outputDirectory = path.dirname(output);
  const keep = new Set([path.basename(output), path.basename(hashPath)]);
  const releasePattern = /^CodexAccountManager-macOS-一键安装版-\d{8}\.zip(?:\.sha256)?$/u;
  const entries = await readdir(outputDirectory, { withFileTypes: true });
  for (const entry of entries) {
    if (!entry.isFile() || keep.has(entry.name) || !releasePattern.test(entry.name)) continue;
    await rm(path.join(outputDirectory, entry.name), { force: true });
  }
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  await assertSourceReady();
  const workRoot = await mkdtemp(path.join(tmpdir(), 'cam-macos-build-'));
  const releaseRoot = path.join(workRoot, `${RELEASE_PREFIX}-${options.releaseDate}`);
  await mkdir(releaseRoot, { recursive: true });

  try {
    console.log('生成不含源码目录的 app.asar……');
    const appAsar = await buildAppAsar(workRoot);
    await privacyScanAsar(appAsar, workRoot);

    const suppliedElectronZip = process.env.ELECTRON_PAYLOAD_PATH;
    console.log(suppliedElectronZip
      ? `校验指定的 Electron ${ELECTRON_VERSION} darwin-arm64 官方 ZIP……`
      : `获取并校验 Electron ${ELECTRON_VERSION} darwin-arm64 官方 ZIP……`);
    const electronZip = suppliedElectronZip
      ? path.resolve(suppliedElectronZip)
      : await downloadArtifact({
        version: ELECTRON_VERSION,
        platform: 'darwin',
        arch: 'arm64',
        artifactName: 'electron',
      });
    await validateElectronPayload(electronZip);
    const suppliedCodexCliTgz = process.env.CODEX_CLI_PAYLOAD_PATH;
    const defaultCodexCliTgz = path.join(PROJECT_DIR, 'dist', CODEX_CLI_PAYLOAD_NAME);
    const codexCliTgz = path.resolve(suppliedCodexCliTgz || defaultCodexCliTgz);
    console.log(`校验 Codex CLI ${CODEX_CLI_VERSION} darwin-arm64 官方 payload……`);
    await validateCodexCliPayload(codexCliTgz);
    await validateIcon(path.join(MACOS_DIR, 'assets', 'AppIcon.icns'));

    await stagePayload(releaseRoot, appAsar, electronZip, codexCliTgz);
    await scanReleasePrivacy(releaseRoot);
    await writeChecksums(releaseRoot, appAsar);
    await createZip(releaseRoot, options.output);

    const zipHash = await hashFile(options.output);
    const hashPath = `${options.output}.sha256`;
    await writeFile(hashPath, `${zipHash}  ${path.basename(options.output)}\n`, 'utf8');
    await removeSupersededMacReleases(options.output, hashPath);

    console.log('\n构建完成：');
    console.log(options.output);
    console.log(hashPath);
    console.log(`SHA256: ${zipHash}`);
    console.log('\n应用 bundle 会由一键安装脚本在 Apple Silicon Mac 上从官方 Electron 与 Codex CLI payload 原生组装。');
    console.log('当前产物未经过 Apple Developer ID 签名与公证；安装脚本会执行本机 ad-hoc 签名。');
    if (options.keepStage) console.log(`保留临时目录：${workRoot}`);
  } finally {
    if (!options.keepStage) await rm(workRoot, { recursive: true, force: true });
  }
}

main().catch((error) => {
  console.error(error?.stack || error);
  process.exitCode = 1;
});
