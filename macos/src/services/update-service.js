const crypto = require('node:crypto');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const https = require('node:https');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

const REPOSITORY = 'bennettnaida-cloud/codex-account-manager';
const RELEASE_URL = `https://api.github.com/repos/${REPOSITORY}/releases/tags/latest`;
const MAX_DOWNLOAD_BYTES = 800 * 1024 * 1024;

function normalizeVersion(value) {
  const match = String(value || '').trim().replace(/^v/i, '').match(/^\d+(?:\.\d+){0,3}$/);
  return match ? match[0] : null;
}

function compareVersions(left, right) {
  const a = normalizeVersion(left)?.split('.').map(Number) || [0];
  const b = normalizeVersion(right)?.split('.').map(Number) || [0];
  for (let index = 0; index < 4; index += 1) {
    const difference = (a[index] || 0) - (b[index] || 0);
    if (difference !== 0) return difference > 0 ? 1 : -1;
  }
  return 0;
}

function requestBuffer(url, { maxBytes = 2 * 1024 * 1024, accept = 'application/json' } = {}) {
  return new Promise((resolve, reject) => {
    const request = https.get(url, {
      headers: {
        Accept: accept,
        'User-Agent': 'CodexAccountManager/' + (process.versions.electron || process.versions.node),
      },
    }, (response) => {
      if ([301, 302, 303, 307, 308].includes(response.statusCode) && response.headers.location) {
        response.resume();
        requestBuffer(new URL(response.headers.location, url).toString(), { maxBytes, accept })
          .then(resolve, reject);
        return;
      }
      if (response.statusCode !== 200) {
        response.resume();
        reject(new Error(`GitHub 返回 HTTP ${response.statusCode || '未知状态'}`));
        return;
      }
      const chunks = [];
      let total = 0;
      response.on('data', (chunk) => {
        total += chunk.length;
        if (total > maxBytes) {
          response.destroy(new Error('响应超过安全大小限制。'));
          return;
        }
        chunks.push(chunk);
      });
      response.on('end', () => resolve(Buffer.concat(chunks)));
      response.on('error', reject);
    });
    request.setTimeout(30_000, () => request.destroy(new Error('连接 GitHub 超时。')));
    request.on('error', reject);
  });
}

async function requestJson(url) {
  const payload = await requestBuffer(url);
  return JSON.parse(payload.toString('utf8'));
}

async function downloadFile(url, destination, expectedSha256) {
  await fsp.mkdir(path.dirname(destination), { recursive: true });
  await new Promise((resolve, reject) => {
    const hash = crypto.createHash('sha256');
    let total = 0;
    const output = fs.createWriteStream(destination, { flags: 'wx' });
    const request = https.get(url, {
      headers: {
        Accept: 'application/octet-stream',
        'User-Agent': 'CodexAccountManager/' + (process.versions.electron || process.versions.node),
      },
    }, (response) => {
      if ([301, 302, 303, 307, 308].includes(response.statusCode) && response.headers.location) {
        response.resume();
        output.destroy();
        fs.rm(destination, { force: true }, () => {
          downloadFile(new URL(response.headers.location, url).toString(), destination, expectedSha256)
            .then(resolve, reject);
        });
        return;
      }
      if (response.statusCode !== 200) {
        response.resume();
        output.destroy();
        reject(new Error(`下载更新包失败（HTTP ${response.statusCode || '未知状态'}）。`));
        return;
      }
      response.on('data', (chunk) => {
        total += chunk.length;
        if (total > MAX_DOWNLOAD_BYTES) {
          response.destroy(new Error('更新包超过安全大小限制。'));
          return;
        }
        hash.update(chunk);
      });
      response.on('error', (error) => output.destroy(error));
      output.on('error', reject);
      output.on('close', () => {
        if (total > MAX_DOWNLOAD_BYTES) {
          reject(new Error('更新包超过安全大小限制。'));
          return;
        }
        const actual = hash.digest('hex');
        if (actual.toLowerCase() !== String(expectedSha256).toLowerCase()) {
          reject(new Error('更新包 SHA256 校验失败，已拒绝安装。'));
          return;
        }
        resolve();
      });
      response.pipe(output);
    });
    request.setTimeout(120_000, () => request.destroy(new Error('下载更新包超时。')));
    request.on('error', (error) => {
      output.destroy();
      reject(error);
    });
  });
}

async function checkForUpdate({ currentVersion, platform = process.platform } = {}) {
  const normalizedCurrent = normalizeVersion(currentVersion) || '0.0.0';
  const release = await requestJson(RELEASE_URL);
  const releaseAssets = Array.isArray(release?.assets) ? release.assets : [];
  const manifestAsset = releaseAssets.find((asset) => asset?.name === 'update-manifest.json');
  if (!manifestAsset?.browser_download_url) return null;
  const manifest = await requestJson(manifestAsset.browser_download_url);
  const remoteVersion = normalizeVersion(manifest?.version);
  if (!remoteVersion || compareVersions(remoteVersion, normalizedCurrent) <= 0) return null;
  const platformKey = platform === 'darwin' ? 'macos' : platform === 'win32' ? 'windows' : null;
  const descriptor = platformKey ? manifest?.assets?.[platformKey] : null;
  if (!descriptor?.name || !/^[a-f0-9]{64}$/i.test(String(descriptor.sha256 || ''))) return null;
  const asset = releaseAssets.find((candidate) => candidate?.name === descriptor.name);
  if (!asset?.browser_download_url) return null;
  return {
    version: remoteVersion,
    commit: String(manifest.commit || ''),
    releaseUrl: String(release.html_url || `https://github.com/${REPOSITORY}/releases`),
    assetName: descriptor.name,
    assetUrl: asset.browser_download_url,
    sha256: String(descriptor.sha256).toLowerCase(),
  };
}

function buildInstallerHelper() {
  return `#!/bin/bash
set -euo pipefail
PID="$1"
ROOT="$2"
ZIP="$3"
cleanup() { /bin/rm -rf -- "$ROOT"; }
trap cleanup EXIT
for _ in $(/usr/bin/seq 1 180); do
  if ! /bin/kill -0 "$PID" 2>/dev/null; then break; fi
  /bin/sleep 0.25
done
if /bin/kill -0 "$PID" 2>/dev/null; then
  exit 1
fi
EXTRACT="$ROOT/extracted"
/bin/mkdir -p "$EXTRACT"
/usr/bin/ditto -x -k "$ZIP" "$EXTRACT"
INSTALLER="$(/usr/bin/find "$EXTRACT" -type f -name '一键安装.command' -print -quit)"
if [[ -z "$INSTALLER" ]]; then exit 1; fi
/bin/chmod +x "$INSTALLER"
/bin/bash "$INSTALLER"
`;
}

async function downloadAndScheduleInstall(update, { currentPid = process.pid } = {}) {
  const root = await fsp.mkdtemp(path.join(os.tmpdir(), 'codex-account-manager-update-'));
  const zipPath = path.join(root, path.basename(update.assetName));
  try {
    await downloadFile(update.assetUrl, zipPath, update.sha256);
    const helperPath = path.join(root, 'apply-update.sh');
    await fsp.writeFile(helperPath, buildInstallerHelper(), { encoding: 'utf8', mode: 0o700 });
    const child = spawn('/bin/bash', [helperPath, String(currentPid), root, zipPath], {
      detached: true,
      stdio: 'ignore',
    });
    child.unref();
    return { root, zipPath, helperPath };
  } catch (error) {
    await fsp.rm(root, { recursive: true, force: true }).catch(() => {});
    throw error;
  }
}

module.exports = {
  REPOSITORY,
  normalizeVersion,
  compareVersions,
  checkForUpdate,
  downloadAndScheduleInstall,
};
