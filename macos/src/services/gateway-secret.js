const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const GATEWAY_CHALLENGE_HEADER = 'x-codex-account-manager-challenge';
const GATEWAY_PROOF_HEADER = 'x-codex-account-manager-proof';
const GATEWAY_PID_HEADER = 'x-codex-account-manager-gateway-pid';
const SECRET_FILE_NAME = 'pat-gateway-control-v1';
const SECRET_PATTERN = /^[a-f0-9]{64}$/;
const CHALLENGE_PATTERN = /^[a-f0-9]{64}$/;
const PROOF_PATTERN = /^[a-f0-9]{64}$/;

function gatewaySecretError(message) {
  const error = new Error(message);
  error.code = 'PAT_GATEWAY_SECRET_INVALID';
  return error;
}

function gatewaySecretBusyError() {
  const error = new Error('Access Token 网关密钥正在由另一个进程创建。');
  error.code = 'PAT_GATEWAY_SECRET_BUSY';
  return error;
}

function isUnixPlatform(platform) {
  return platform !== 'win32';
}

function gatewaySecretPath(userDataPath) {
  const input = String(userDataPath || '');
  if (!input || !path.isAbsolute(input)) throw gatewaySecretError('无法确定 Access Token 网关密钥目录。');
  const root = path.resolve(input);
  return path.join(root, '.cache', SECRET_FILE_NAME);
}

async function assertSecureDirectory(directory, { platform, getUid, requireMode = true }) {
  const details = await fs.promises.lstat(directory);
  if (!details.isDirectory() || details.isSymbolicLink()) {
    throw gatewaySecretError('Access Token 网关密钥目录不是安全的普通目录。');
  }
  if (!isUnixPlatform(platform)) return;
  const uid = typeof getUid === 'function' ? getUid() : null;
  if (Number.isInteger(uid) && details.uid !== uid) {
    throw gatewaySecretError('Access Token 网关密钥目录不属于当前用户。');
  }
  if (requireMode && (details.mode & 0o777) !== 0o700) {
    throw gatewaySecretError('Access Token 网关密钥目录权限必须为 0700。');
  }
}

async function createSecretAtomically(secretPath, { platform, randomBytes }) {
  const directory = path.dirname(secretPath);
  const generated = randomBytes(32).toString('hex');
  const suffix = randomBytes(12).toString('hex');
  const temporaryPath = path.join(directory, `.${SECRET_FILE_NAME}.${process.pid}.${suffix}.tmp`);
  let handle = null;
  try {
    handle = await fs.promises.open(temporaryPath, 'wx', 0o600);
    await handle.writeFile(`${generated}\n`, 'utf8');
    await handle.sync();
    await handle.close();
    handle = null;
    if (isUnixPlatform(platform)) await fs.promises.chmod(temporaryPath, 0o600);
    try {
      await fs.promises.link(temporaryPath, secretPath);
    } catch (error) {
      if (error?.code !== 'EEXIST') throw error;
    }
  } finally {
    await handle?.close().catch(() => {});
    await fs.promises.rm(temporaryPath, { force: true }).catch(() => {});
  }
}

async function readValidatedSecret(secretPath, { platform, getUid }) {
  let flags = fs.constants.O_RDONLY;
  if (isUnixPlatform(platform) && Number.isInteger(fs.constants.O_NOFOLLOW)) flags |= fs.constants.O_NOFOLLOW;
  let handle;
  try {
    handle = await fs.promises.open(secretPath, flags);
  } catch (error) {
    if (error?.code === 'ELOOP') throw gatewaySecretError('Access Token 网关密钥不能是符号链接。');
    throw error;
  }
  try {
    const details = await handle.stat();
    if (details.isFile() && details.nlink === 2) throw gatewaySecretBusyError();
    if (!details.isFile() || details.size < 64 || details.size > 80 || details.nlink !== 1) {
      throw gatewaySecretError('Access Token 网关密钥文件结构无效。');
    }
    if (isUnixPlatform(platform)) {
      const uid = typeof getUid === 'function' ? getUid() : null;
      if (Number.isInteger(uid) && details.uid !== uid) {
        throw gatewaySecretError('Access Token 网关密钥不属于当前用户。');
      }
      if ((details.mode & 0o777) !== 0o600) {
        throw gatewaySecretError('Access Token 网关密钥权限必须为 0600。');
      }
    }
    const value = String(await handle.readFile('utf8')).trim();
    if (!SECRET_PATTERN.test(value)) throw gatewaySecretError('Access Token 网关密钥内容无效。');
    return value;
  } finally {
    await handle.close();
  }
}

async function readSecretWithRetry(secretPath, options) {
  let lastError = null;
  for (let attempt = 0; attempt < 40; attempt += 1) {
    try {
      return await readValidatedSecret(secretPath, options);
    } catch (error) {
      lastError = error;
      if (error?.code !== 'PAT_GATEWAY_SECRET_BUSY') throw error;
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  }
  throw lastError || gatewaySecretBusyError();
}

async function loadOrCreateGatewaySecret(userDataPath, {
  platform = process.platform,
  getUid = process.getuid,
  randomBytes = crypto.randomBytes,
} = {}) {
  const secretPath = gatewaySecretPath(userDataPath);
  const directory = path.dirname(secretPath);
  await fs.promises.mkdir(directory, { recursive: true, mode: 0o700 });
  await assertSecureDirectory(directory, { platform, getUid, requireMode: false });
  if (isUnixPlatform(platform)) await fs.promises.chmod(directory, 0o700);
  await assertSecureDirectory(directory, { platform, getUid });
  try {
    return await readSecretWithRetry(secretPath, { platform, getUid });
  } catch (error) {
    if (error?.code !== 'ENOENT') throw error;
  }
  await createSecretAtomically(secretPath, { platform, randomBytes });
  return readSecretWithRetry(secretPath, { platform, getUid });
}

async function loadGatewaySecret(userDataPath, {
  platform = process.platform,
  getUid = process.getuid,
} = {}) {
  const secretPath = gatewaySecretPath(userDataPath);
  await assertSecureDirectory(path.dirname(secretPath), { platform, getUid });
  return readSecretWithRetry(secretPath, { platform, getUid });
}

function assertGatewaySecret(secret) {
  const value = String(secret || '');
  if (!SECRET_PATTERN.test(value)) throw gatewaySecretError('Access Token 网关密钥不可用。');
  return value;
}

function createGatewayChallenge(randomBytes = crypto.randomBytes) {
  return randomBytes(32).toString('hex');
}

function createGatewayProof(secret, challenge, marker, version, pid, statusCode) {
  const key = assertGatewaySecret(secret);
  const nonce = String(challenge || '').toLowerCase();
  const protocolMarker = String(marker || '').trim();
  const buildVersion = String(version || '').trim();
  const processId = Number(pid);
  const responseStatus = Number(statusCode);
  if (!CHALLENGE_PATTERN.test(nonce) || !protocolMarker || protocolMarker.length > 64 ||
      !buildVersion || buildVersion.length > 64 || !Number.isSafeInteger(processId) || processId <= 0 ||
      !Number.isInteger(responseStatus) || responseStatus < 100 || responseStatus > 599) {
    throw gatewaySecretError('Access Token 网关健康证明参数无效。');
  }
  return crypto.createHmac('sha256', key)
    .update(`health-v1\n${nonce}\n${protocolMarker}\n${buildVersion}\n${processId}\n${responseStatus}`, 'utf8')
    .digest('hex');
}

function gatewayProofMatches(secret, challenge, marker, version, pid, statusCode, proof) {
  const actualValue = String(proof || '').toLowerCase();
  if (!PROOF_PATTERN.test(actualValue)) return false;
  try {
    const expected = Buffer.from(createGatewayProof(secret, challenge, marker, version, pid, statusCode), 'hex');
    const actual = Buffer.from(actualValue, 'hex');
    return expected.length === actual.length && crypto.timingSafeEqual(expected, actual);
  } catch {
    return false;
  }
}

module.exports = {
  GATEWAY_CHALLENGE_HEADER,
  GATEWAY_PID_HEADER,
  GATEWAY_PROOF_HEADER,
  SECRET_FILE_NAME,
  assertGatewaySecret,
  createGatewayChallenge,
  createGatewayProof,
  gatewayProofMatches,
  gatewaySecretPath,
  loadGatewaySecret,
  loadOrCreateGatewaySecret,
};
