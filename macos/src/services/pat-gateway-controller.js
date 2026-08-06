const http = require('node:http');
const path = require('node:path');
const { spawn } = require('node:child_process');
const {
  GATEWAY_CHALLENGE_HEADER,
  GATEWAY_PID_HEADER,
  GATEWAY_PROOF_HEADER,
  assertGatewaySecret,
  createGatewayChallenge,
  gatewayProofMatches,
} = require('./gateway-secret');
const {
  DEFAULT_PORT,
  GATEWAY_BUILD_VERSION,
  GATEWAY_MARKER_HEADER,
  GATEWAY_MARKER_VALUE,
  GATEWAY_VERSION_HEADER,
} = require('./local-pat-gateway');

const DEFAULT_HOST = '127.0.0.1';
const DAEMON_ARGUMENT = '--local-pat-gateway-daemon';
const HEALTH_PATH = '/healthz';
const MAX_HEALTH_BODY_BYTES = 8 * 1024;

function delay(milliseconds) {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, milliseconds);
    timer.unref?.();
  });
}

function healthMessage(body) {
  try {
    const value = JSON.parse(String(body || ''));
    return typeof value?.error === 'string' && value.error.trim()
      ? value.error.trim()
      : null;
  } catch {
    return null;
  }
}

function probePatGateway({
  gatewaySecret,
  host = DEFAULT_HOST,
  port = DEFAULT_PORT,
  timeoutMs = 1_000,
  requestImpl = http.request,
  randomBytesImpl,
} = {}) {
  const secret = assertGatewaySecret(gatewaySecret);
  const challenge = createGatewayChallenge(randomBytesImpl);
  return new Promise((resolve) => {
    let settled = false;
    const finish = (result) => {
      if (settled) return;
      settled = true;
      resolve(result);
    };
    let request;
    try {
      request = requestImpl({
        host,
        port,
        path: HEALTH_PATH,
        method: 'GET',
        headers: {
          connection: 'close',
          host: `${host}:${port}`,
          [GATEWAY_CHALLENGE_HEADER]: challenge,
        },
        timeout: timeoutMs,
      }, (response) => {
        const chunks = [];
        let total = 0;
        response.on('data', (chunkValue) => {
          if (total >= MAX_HEALTH_BODY_BYTES) return;
          const chunk = Buffer.from(chunkValue);
          const remaining = MAX_HEALTH_BODY_BYTES - total;
          chunks.push(chunk.subarray(0, remaining));
          total += Math.min(chunk.length, remaining);
        });
        response.once('end', () => {
          const marker = String(response.headers?.[GATEWAY_MARKER_HEADER] || '');
          const protocolMatched = marker === GATEWAY_MARKER_VALUE;
          const version = String(response.headers?.[GATEWAY_VERSION_HEADER] || '');
          const versionMatched = version === GATEWAY_BUILD_VERSION;
          const pid = Number(response.headers?.[GATEWAY_PID_HEADER]);
          const proof = String(response.headers?.[GATEWAY_PROOF_HEADER] || '');
          const statusCode = Number(response.statusCode) || 0;
          const authenticated = gatewayProofMatches(secret, challenge, marker, version, pid, statusCode, proof);
          // The authenticated marker is the compatibility boundary. Build versions may
          // differ during a rolling app update while the existing daemon keeps serving.
          const owned = authenticated && protocolMatched;
          const body = Buffer.concat(chunks, total).toString('utf8');
          finish({
            reachable: true,
            owned,
            authenticated,
            versionMatched,
            incompatible: false,
            pid: Number.isSafeInteger(pid) && pid > 0 ? pid : null,
            version,
            ready: owned && statusCode >= 200 && statusCode < 300,
            statusCode,
            message: healthMessage(body),
          });
        });
        response.once('error', (error) => finish({ reachable: true, owned: false, ready: false, error }));
        response.resume();
      });
    } catch (error) {
      finish({ reachable: false, owned: false, ready: false, error });
      return;
    }
    request.once('timeout', () => request.destroy(Object.assign(new Error('PAT gateway health check timed out.'), {
      code: 'ETIMEDOUT',
    })));
    request.once('error', (error) => finish({ reachable: false, owned: false, ready: false, error }));
    request.end();
  });
}

class PatGatewayController {
  constructor({
    gatewaySecret,
    execPath,
    appPath = null,
    packaged = true,
    platform = process.platform,
    environment = process.env,
    host = DEFAULT_HOST,
    port = DEFAULT_PORT,
    startupTimeoutMs = 10_000,
    pollIntervalMs = 100,
    probeTimeoutMs = 750,
    probeImpl = probePatGateway,
    spawnImpl = spawn,
    delayImpl = delay,
  } = {}) {
    this.gatewaySecret = assertGatewaySecret(gatewaySecret);
    this.execPath = String(execPath || '').trim();
    this.appPath = appPath ? path.resolve(String(appPath)) : null;
    this.packaged = packaged !== false;
    this.platform = platform;
    this.environment = { ...environment };
    this.host = host;
    this.port = port;
    this.startupTimeoutMs = startupTimeoutMs;
    this.pollIntervalMs = pollIntervalMs;
    this.probeTimeoutMs = probeTimeoutMs;
    this.probeImpl = probeImpl;
    this.spawnImpl = spawnImpl;
    this.delayImpl = delayImpl;
    this.startPromise = null;
  }

  address() {
    return { host: this.host, port: this.port, baseUrl: `http://${this.host}:${this.port}` };
  }

  async probe() {
    return this.probeImpl({
      gatewaySecret: this.gatewaySecret,
      host: this.host,
      port: this.port,
      timeoutMs: this.probeTimeoutMs,
    });
  }

  portConflictError() {
    const error = new Error(`本地端口 ${this.port} 已被其它程序占用，无法启动 Access Token 网关。`);
    error.code = 'PAT_GATEWAY_PORT_IN_USE';
    return error;
  }

  versionMismatchError(result) {
    const found = result?.version || '未知版本';
    const error = new Error(`端口 ${this.port} 上运行的是不兼容的旧版 Access Token 网关（${found}）；请重新安装并启动本应用。`);
    error.code = 'PAT_GATEWAY_VERSION_MISMATCH';
    return error;
  }

  notReadyError(result) {
    const message = result?.message || 'Access Token 网关尚未就绪，请检查本地代理设置。';
    const error = new Error(message);
    error.code = /代理|proxy/i.test(message) ? 'PROXY_REQUIRED' : 'PAT_GATEWAY_NOT_READY';
    return error;
  }

  async ensureRunning() {
    const first = await this.probe();
    if (first.owned) return this.address();
    if (first.incompatible) throw this.versionMismatchError(first);
    if (first.reachable) throw this.portConflictError();
    if (this.startPromise) return this.startPromise;
    this.startPromise = this.startDaemon().finally(() => {
      this.startPromise = null;
    });
    return this.startPromise;
  }

  async ensureReady() {
    await this.ensureRunning();
    const result = await this.probe();
    if (result.ready) return this.address();
    if (result.owned) throw this.notReadyError(result);
    if (result.incompatible) throw this.versionMismatchError(result);
    if (result.reachable) throw this.portConflictError();
    const error = new Error('Access Token 网关进程已退出，请重新尝试。');
    error.code = 'PAT_GATEWAY_EXITED';
    throw error;
  }

  spawnArguments() {
    if (this.packaged) return [DAEMON_ARGUMENT];
    if (!this.appPath) throw new Error('开发版无法确定 Electron 应用目录，不能启动 Access Token 网关。');
    return [this.appPath, DAEMON_ARGUMENT];
  }

  spawnEnvironment() {
    const environment = {
      ...this.environment,
      CODEX_ACCOUNT_MANAGER_PAT_GATEWAY_DAEMON: '1',
    };
    delete environment.ELECTRON_RUN_AS_NODE;
    return environment;
  }

  async startDaemon() {
    if (this.platform !== 'darwin') {
      const error = new Error('独立 Access Token 网关只能由 macOS 应用启动。');
      error.code = 'PAT_GATEWAY_UNSUPPORTED_PLATFORM';
      throw error;
    }
    if (!this.execPath || !path.isAbsolute(this.execPath)) {
      throw new Error('无法确定应用可执行文件，不能启动 Access Token 网关。');
    }
    const child = this.spawnImpl(this.execPath, this.spawnArguments(), {
      cwd: path.dirname(this.execPath),
      detached: true,
      stdio: 'ignore',
      env: this.spawnEnvironment(),
    });
    child.unref?.();
    let spawnError = null;
    let exit = null;
    child.once?.('error', (error) => { spawnError = error; });
    child.once?.('exit', (code, signal) => { exit = { code, signal }; });

    const deadline = Date.now() + this.startupTimeoutMs;
    while (Date.now() <= deadline) {
      if (spawnError) throw spawnError;
      const result = await this.probe();
      if (result.owned) return this.address();
      if (result.incompatible) throw this.versionMismatchError(result);
      if (result.reachable) throw this.portConflictError();
      if (exit) {
        const exitReason = exit.signal || (exit.code ?? 'unknown');
        const error = new Error(`Access Token 网关启动后立即退出（${exitReason}）。`);
        error.code = 'PAT_GATEWAY_START_FAILED';
        throw error;
      }
      await this.delayImpl(this.pollIntervalMs);
    }
    const error = new Error('等待 Access Token 网关启动超时。');
    error.code = 'PAT_GATEWAY_START_TIMEOUT';
    throw error;
  }
}

module.exports = {
  DAEMON_ARGUMENT,
  PatGatewayController,
  probePatGateway,
};
