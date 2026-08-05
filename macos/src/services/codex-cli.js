const fs = require('node:fs');
const crypto = require('node:crypto');
const os = require('node:os');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');
const readline = require('node:readline');
const { applyProxyEnvironment, normalizeProjectPath } = require('./system-service');

const CODEX_BUNDLE_IDENTIFIER = 'com.openai.codex';
const OPENAI_APPLE_TEAM_IDENTIFIER = '2DC432GLL2';
const DEFAULT_DESKTOP_STOP_TIMEOUT_MS = 5_000;
const DEFAULT_TERMINAL_LAUNCH_TIMEOUT_MS = 10_000;
const DEFAULT_TERMINAL_STOP_TIMEOUT_MS = 5_000;
const DEFAULT_TERMINAL_LEGACY_QUARANTINE_MS = 10_000;
const DESKTOP_PROCESS_DISCOVERY_FAILED = 'DESKTOP_PROCESS_DISCOVERY_FAILED';
const TERMINAL_PROCESS_DISCOVERY_FAILED = 'TERMINAL_PROCESS_DISCOVERY_FAILED';
const BUNDLED_CODEX_VERSION = '0.144.1';
const BUNDLED_CODEX_TARGET = 'aarch64-apple-darwin';
const TERMINAL_SESSION_VERSION = 1;
const TERMINAL_ACCOUNT_ID_PATTERN = /^[A-Za-z0-9-]{1,80}$/;
const TERMINAL_NONCE_PATTERN = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const TERMINAL_DESCRIPTOR_PATTERN = /^session-([A-Za-z0-9-]{1,80})-([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\.json$/i;
const TERMINAL_READY_PATTERN = /^session-([A-Za-z0-9-]{1,80})-([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\.ready$/i;
const TERMINAL_READY_TEMP_PATTERN = /^session-([A-Za-z0-9-]{1,80})-([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\.ready\.tmp\.([0-9]+)$/i;
const TERMINAL_CLAIM_PATTERN = /^claim-([A-Za-z0-9-]{1,80})-([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;
const TERMINAL_LAUNCHER_PATTERN = /^codex-([A-Za-z0-9-]{1,80})-([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\.command$/i;
const TERMINAL_LEGACY_QUARANTINE_PATTERN = /^legacy-([A-Za-z0-9-]{1,80})\.quarantine\.json$/;
const MAX_TERMINAL_DESCRIPTOR_BYTES = 16_384;
const MAX_TERMINAL_READY_BYTES = 1_024;

function desktopProcessDiscoveryError(cause = null) {
  const error = new Error('无法检查正在运行的 ChatGPT（Codex）进程，本次操作已取消。请稍后重试。');
  error.code = DESKTOP_PROCESS_DISCOVERY_FAILED;
  if (cause) error.cause = cause;
  return error;
}

function readDesktopProcessList(runProcessListCommand = spawnSync) {
  let result;
  try {
    result = runProcessListCommand('/bin/ps', ['-axo', 'pid=,command='], {
      encoding: 'utf8',
      timeout: 5_000,
      windowsHide: true,
      maxBuffer: 4_194_304,
    });
  } catch (error) {
    throw desktopProcessDiscoveryError(error);
  }
  if (result?.error) throw desktopProcessDiscoveryError(result.error);
  if (!result || result.status !== 0) {
    throw desktopProcessDiscoveryError(new Error(`ps exited with status ${result?.status ?? 'unknown'}`));
  }
  return String(result.stdout || '');
}

function terminalProcessDiscoveryError(cause = null) {
  const error = new Error('无法核验 Terminal 中正在运行的 Codex 进程，本次操作已取消。请关闭相关 Terminal 窗口后重试。');
  error.code = TERMINAL_PROCESS_DISCOVERY_FAILED;
  if (cause) error.cause = cause;
  return error;
}

function readTerminalProcessList(runProcessListCommand = spawnSync) {
  let result;
  try {
    result = runProcessListCommand('/bin/ps', ['-ww', '-axo', 'pid=,ppid=,uid=,command='], {
      encoding: 'utf8',
      timeout: 5_000,
      windowsHide: true,
      maxBuffer: 4_194_304,
    });
  } catch (error) {
    throw terminalProcessDiscoveryError(error);
  }
  if (result?.error) throw terminalProcessDiscoveryError(result.error);
  if (!result || result.status !== 0) {
    throw terminalProcessDiscoveryError(new Error(`ps exited with status ${result?.status ?? 'unknown'}`));
  }
  return String(result.stdout || '');
}

function parseTerminalProcessList(output) {
  const records = [];
  for (const line of String(output || '').split(/\r?\n/)) {
    const match = /^\s*(\d+)\s+(\d+)\s+(\d+)\s+(.+)$/.exec(line);
    if (!match) continue;
    const pid = Number(match[1]);
    const ppid = Number(match[2]);
    const uid = Number(match[3]);
    if (!Number.isInteger(pid) || pid <= 1 || !Number.isInteger(ppid) || ppid < 0 ||
        !Number.isInteger(uid) || uid < 0) continue;
    records.push({ pid, ppid, uid, command: match[4] });
  }
  return records;
}

function shellQuote(value) {
  return `'${String(value).replace(/'/g, `'\\''`)}'`;
}

function commandHasExactArgument(command, argument) {
  const escaped = String(argument).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`(?:^|\\s)${escaped}(?=\\s|$)`).test(String(command || ''));
}

function commandHasExactPathArgument(command, argument) {
  return [String(argument), `"${argument}"`, `'${argument}'`]
    .some((candidate) => commandHasExactArgument(command, candidate));
}

function commandUsesDesktopProfile(command, desktopProfile) {
  return [
    `--user-data-dir=${desktopProfile}`,
    `--user-data-dir="${desktopProfile}"`,
    `--user-data-dir='${desktopProfile}'`,
  ].some((argument) => commandHasExactArgument(command, argument));
}

function commandStartsWithExecutable(command, executable) {
  const text = String(command || '').trim();
  return [String(executable), `"${executable}"`, `'${executable}'`]
    .some((candidate) => text === candidate || text.startsWith(`${candidate} `));
}

function terminalCommandTokens(command) {
  const tokens = [];
  const pattern = /"([^"]*)"|'([^']*)'|(\S+)/g;
  let match;
  while ((match = pattern.exec(String(command || ''))) !== null) {
    tokens.push(match[1] ?? match[2] ?? match[3]);
  }
  return tokens;
}

function commandLooksLikeCodexCli(command) {
  const tokens = terminalCommandTokens(command);
  if (tokens.length === 0) return false;
  const basename = (value) => path.basename(String(value || '')).toLowerCase();
  if (basename(tokens[0]) === 'codex') return true;
  return ['node', 'nodejs'].includes(basename(tokens[0])) &&
    /^(?:codex|codex\.js)$/i.test(basename(tokens[1]));
}

function terminalLifecycleError(message, code = 'TERMINAL_SESSION_UNCERTAIN', cause = null) {
  const error = new Error(message);
  error.code = code;
  if (cause) error.cause = cause;
  return error;
}

function ensurePrivateDirectory(directory) {
  fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
  const details = fs.lstatSync(directory);
  if (!details.isDirectory() || details.isSymbolicLink()) {
    throw terminalLifecycleError('Terminal 会话目录不是安全的真实目录，操作已取消。');
  }
  try { fs.chmodSync(directory, 0o700); } catch (error) {
    if (error?.code !== 'EPERM') throw error;
  }
  return path.resolve(directory);
}

function assertDirectTerminalArtifact(directory, candidate) {
  const root = path.resolve(directory);
  const target = path.resolve(String(candidate || ''));
  if (path.dirname(target) !== root) {
    throw terminalLifecycleError('Terminal 会话文件路径超出受管目录，操作已取消。');
  }
  return target;
}

function readRegularTerminalFile(filePath, maxBytes, { missing = null } = {}) {
  let details;
  try {
    details = fs.lstatSync(filePath);
  } catch (error) {
    if (error?.code === 'ENOENT') return missing;
    throw error;
  }
  if (!details.isFile() || details.isSymbolicLink() || details.size > maxBytes) {
    throw terminalLifecycleError('Terminal 会话文件不安全或格式异常，账号删除已取消。');
  }
  try {
    return fs.readFileSync(filePath, 'utf8');
  } catch (error) {
    if (error?.code === 'ENOENT') return missing;
    throw error;
  }
}

function removeRegularTerminalFile(filePath, { missingOk = true } = {}) {
  let details;
  try {
    details = fs.lstatSync(filePath);
  } catch (error) {
    if (missingOk && error?.code === 'ENOENT') return false;
    throw error;
  }
  if (!details.isFile() || details.isSymbolicLink()) {
    throw terminalLifecycleError('Terminal 会话残留不是安全的普通文件，账号删除已取消。');
  }
  try {
    fs.rmSync(filePath, { force: false });
  } catch (error) {
    if (missingOk && error?.code === 'ENOENT') return false;
    throw error;
  }
  return true;
}

function terminalArtifactPaths(launchersDir, sessionsDir, accountId, nonce) {
  if (!TERMINAL_ACCOUNT_ID_PATTERN.test(String(accountId || '')) ||
      !TERMINAL_NONCE_PATTERN.test(String(nonce || ''))) {
    throw terminalLifecycleError('Terminal 会话标识无效，操作已取消。');
  }
  return {
    launcherPath: path.join(launchersDir, `codex-${accountId}-${nonce}.command`),
    descriptorPath: path.join(sessionsDir, `session-${accountId}-${nonce}.json`),
    readyPath: path.join(sessionsDir, `session-${accountId}-${nonce}.ready`),
    claimPath: path.join(sessionsDir, `claim-${accountId}-${nonce}`),
  };
}

function parseTerminalReady(text, expectedNonce) {
  const lines = String(text || '').trim().split(/\r?\n/);
  if (lines.length !== 3 || lines[0] !== expectedNonce) {
    throw terminalLifecycleError('Terminal 会话就绪记录与启动请求不匹配，操作已取消。');
  }
  const wrapperPid = Number(lines[1]);
  const childPid = Number(lines[2]);
  if (!Number.isInteger(wrapperPid) || wrapperPid <= 1 ||
      !Number.isInteger(childPid) || childPid < 0 || wrapperPid === childPid) {
    throw terminalLifecycleError('Terminal 会话 PID 记录无效，操作已取消。');
  }
  return { wrapperPid, childPid };
}

function parseTerminalClaimOwner(text, expectedAccountId, expectedNonce) {
  const lines = String(text || '').trim().split(/\r?\n/);
  if (lines.length !== 3 || lines[0] !== expectedNonce || lines[1] !== expectedAccountId) {
    throw terminalLifecycleError('Terminal 会话 claim 与账号或 nonce 不匹配，操作已取消。');
  }
  const wrapperPid = Number(lines[2]);
  if (!Number.isInteger(wrapperPid) || wrapperPid <= 1) {
    throw terminalLifecycleError('Terminal 会话 claim 中的 PID 无效，操作已取消。');
  }
  return { wrapperPid };
}

function waitBriefly(milliseconds = 25) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

class CodexCliService {
  constructor({
    resourcesPath,
    userDataPath,
    allowExecutableOverride = false,
    commandRunner = null,
    appServerFactory = null,
    settingsProvider = null,
    spawnProcess = spawn,
    platform = process.platform,
    appCandidates = null,
    appMetadataReader = readApplicationInfoPlist,
    appSignatureReader = readApplicationSignature,
    processStartupTimeoutMs = 2_500,
    processListRunner = null,
    terminalProcessListRunner = null,
    processKiller = null,
    processAlive = null,
    processUid = null,
    desktopStopTimeoutMs = DEFAULT_DESKTOP_STOP_TIMEOUT_MS,
    terminalLaunchTimeoutMs = DEFAULT_TERMINAL_LAUNCH_TIMEOUT_MS,
    terminalStopTimeoutMs = DEFAULT_TERMINAL_STOP_TIMEOUT_MS,
    terminalLegacyQuarantineMs = DEFAULT_TERMINAL_LEGACY_QUARANTINE_MS,
    patGateway = null,
  }) {
    this.resourcesPath = resourcesPath;
    this.userDataPath = userDataPath;
    this.allowExecutableOverride = allowExecutableOverride;
    this.commandRunner = typeof commandRunner === 'function' ? commandRunner : null;
    this.appServerFactory = typeof appServerFactory === 'function' ? appServerFactory : null;
    this.settingsProvider = typeof settingsProvider === 'function' ? settingsProvider : () => ({});
    this.spawnProcess = spawnProcess;
    this.platform = platform;
    this.appCandidates = Array.isArray(appCandidates) ? appCandidates : null;
    this.appMetadataReader = typeof appMetadataReader === 'function'
      ? appMetadataReader
      : readApplicationInfoPlist;
    this.appSignatureReader = typeof appSignatureReader === 'function'
      ? appSignatureReader
      : readApplicationSignature;
    this.processStartupTimeoutMs = Number.isInteger(processStartupTimeoutMs) && processStartupTimeoutMs >= 10
      ? processStartupTimeoutMs
      : 2_500;
    this.processListRunner = typeof processListRunner === 'function'
      ? processListRunner
      : () => readDesktopProcessList();
    this.terminalProcessListRunner = typeof terminalProcessListRunner === 'function'
      ? terminalProcessListRunner
      : () => readTerminalProcessList();
    this.processKiller = typeof processKiller === 'function'
      ? processKiller
      : (pid, signal) => process.kill(pid, signal);
    this.processAlive = typeof processAlive === 'function'
      ? processAlive
      : (pid) => {
        try {
          process.kill(pid, 0);
          return true;
        } catch (error) {
          return error?.code === 'EPERM';
        }
      };
    const detectedUid = typeof process.getuid === 'function' ? process.getuid() : null;
    this.processUid = Number.isInteger(processUid) && processUid >= 0
      ? processUid
      : Number.isInteger(detectedUid) && detectedUid >= 0
        ? detectedUid
        : null;
    this.desktopStopTimeoutMs = Number.isInteger(desktopStopTimeoutMs) && desktopStopTimeoutMs >= 100
      ? desktopStopTimeoutMs
      : DEFAULT_DESKTOP_STOP_TIMEOUT_MS;
    this.terminalLaunchTimeoutMs = Number.isInteger(terminalLaunchTimeoutMs) && terminalLaunchTimeoutMs >= 10
      ? terminalLaunchTimeoutMs
      : DEFAULT_TERMINAL_LAUNCH_TIMEOUT_MS;
    this.terminalStopTimeoutMs = Number.isInteger(terminalStopTimeoutMs) && terminalStopTimeoutMs >= 100
      ? terminalStopTimeoutMs
      : DEFAULT_TERMINAL_STOP_TIMEOUT_MS;
    this.terminalLegacyQuarantineMs = Number.isInteger(terminalLegacyQuarantineMs) &&
      terminalLegacyQuarantineMs >= 100
      ? terminalLegacyQuarantineMs
      : DEFAULT_TERMINAL_LEGACY_QUARANTINE_MS;
    this.patGateway = patGateway && typeof patGateway.ensureReady === 'function' ? patGateway : null;
    this.terminalLaunchersDir = path.join(this.userDataPath, 'terminal-launchers');
    this.terminalSessionsDir = path.join(this.userDataPath, 'terminal-sessions');
    this.desktopProcesses = new Map();
    this.activeOfficialLogins = new Map();
    this.resolvedCodexPath = null;
  }

  childEnvironment(codexHome) {
    return buildChildEnvironment(codexHome, this.settingsProvider() || {});
  }

  async ensureAccountServices(account) {
    if (account?.authKind !== 'access_token') return;
    if (!this.patGateway) {
      const error = new Error('Access Token 本地网关不可用；为防止意外直连，操作已停止。请重新安装本应用。');
      error.code = 'PAT_GATEWAY_UNAVAILABLE';
      throw error;
    }
    await this.patGateway.ensureReady();
  }

  getCodexPath() {
    if (this.resolvedCodexPath) {
      try {
        fs.accessSync(this.resolvedCodexPath, this.platform === 'win32' ? fs.constants.F_OK : fs.constants.X_OK);
        return this.resolvedCodexPath;
      } catch {
        this.resolvedCodexPath = null;
      }
    }

    if (this.platform === 'darwin' && this.resourcesPath) {
      const resourcesRoot = path.resolve(this.resourcesPath);
      const bundledRoot = path.join(resourcesRoot, 'codex-cli');
      const bundledCandidate = path.join(bundledRoot, 'bin', 'codex');
      const metadataPath = path.join(bundledRoot, 'codex-package.json');
      try {
        const rootDetails = fs.lstatSync(bundledRoot);
        const binaryDetails = fs.lstatSync(bundledCandidate);
        const metadataDetails = fs.lstatSync(metadataPath);
        if (!rootDetails.isDirectory() || rootDetails.isSymbolicLink() ||
            !binaryDetails.isFile() || binaryDetails.isSymbolicLink() ||
            !metadataDetails.isFile() || metadataDetails.isSymbolicLink()) {
          throw new Error('unsafe bundled CLI layout');
        }
        const realRoot = fs.realpathSync(resourcesRoot);
        const realBinary = fs.realpathSync(bundledCandidate);
        const realMetadata = fs.realpathSync(metadataPath);
        if (!isPathInside(realRoot, realBinary) || !isPathInside(realRoot, realMetadata)) {
          throw new Error('bundled CLI escaped Resources');
        }
        const metadata = JSON.parse(fs.readFileSync(realMetadata, 'utf8'));
        if (metadata?.layoutVersion !== 1 || metadata?.version !== BUNDLED_CODEX_VERSION ||
            metadata?.target !== BUNDLED_CODEX_TARGET || metadata?.entrypoint !== 'bin/codex') {
          throw new Error('unexpected bundled CLI metadata');
        }
        fs.accessSync(realBinary, fs.constants.X_OK);
        this.resolvedCodexPath = realBinary;
        return realBinary;
      } catch {
        // A damaged application payload must never shadow a valid user-installed CLI.
      }
    }

    const candidates = new Set([
      this.allowExecutableOverride ? process.env.CODEX_ACCOUNT_MANAGER_CODEX_BIN : null,
      this.platform === 'darwin' ? '/opt/homebrew/bin/codex' : null,
      this.platform === 'darwin' ? '/usr/local/bin/codex' : null,
      this.platform === 'darwin' ? path.join(os.homedir(), '.local', 'bin', 'codex') : null,
      this.platform === 'darwin' ? path.join(os.homedir(), '.npm-global', 'bin', 'codex') : null,
    ].filter(Boolean));

    for (const directory of String(process.env.PATH || '').split(path.delimiter)) {
      if (directory) candidates.add(path.join(directory, this.platform === 'win32' ? 'codex.exe' : 'codex'));
    }

    // Apps launched from Finder inherit a minimal PATH. Query the user's login
    // shell so Homebrew/npm installations remain discoverable without relaunching Finder.
    if (this.platform === 'darwin') {
      const shellResult = spawnSync('/bin/zsh', ['-lic', 'command -v codex'], {
        encoding: 'utf8',
        timeout: 5_000,
        windowsHide: true,
      });
      if (shellResult.status === 0 && shellResult.stdout.trim()) {
        for (const line of shellResult.stdout.trim().split(/\r?\n/)) {
          const candidate = line.trim();
          if (path.isAbsolute(candidate)) candidates.add(candidate);
        }
      }
    } else if (this.platform === 'win32') {
      const whereResult = spawnSync('where.exe', ['codex'], { encoding: 'utf8', timeout: 5_000, windowsHide: true });
      if (whereResult.status === 0 && whereResult.stdout.trim()) candidates.add(whereResult.stdout.trim().split(/\r?\n/)[0]);
    }

    const found = [...candidates].find((candidate) => {
      try {
        fs.accessSync(candidate, this.platform === 'win32' ? fs.constants.F_OK : fs.constants.X_OK);
        return true;
      } catch {
        return false;
      }
    });
    if (!found) {
      throw new Error('内置 Codex CLI 缺失或损坏。请重新安装本应用；开发版也可安装系统 Codex CLI 后重试。');
    }
    this.resolvedCodexPath = found;
    return found;
  }

  async run(account, args, { stdinText = null, timeoutMs = 30_000, onOutput = null, signal = null } = {}) {
    await this.ensureAccountServices(account);
    const childEnvironment = this.childEnvironment(account.codexHome);

    if (this.commandRunner) {
      const pending = Promise.resolve(this.commandRunner({
        account,
        args: [...args],
        env: childEnvironment,
        stdinText,
        timeoutMs,
        onOutput,
        signal,
      })).then((result) => ({
        code: Number.isInteger(result?.code) ? result.code : -1,
        stdout: redactSecrets(String(result?.stdout || '').trim()),
        stderr: redactSecrets(String(result?.stderr || '').trim()),
      }));
      if (!signal) return pending;
      return new Promise((resolve, reject) => {
        let settled = false;
        const onAbort = () => {
          if (settled) return;
          settled = true;
          reject(new Error('通过 ChatGPT 登录已取消。'));
        };
        signal.addEventListener('abort', onAbort, { once: true });
        if (signal.aborted) onAbort();
        pending.then((result) => {
          if (settled) return;
          settled = true;
          signal.removeEventListener('abort', onAbort);
          resolve(result);
        }, (error) => {
          if (settled) return;
          settled = true;
          signal.removeEventListener('abort', onAbort);
          reject(error);
        });
      });
    }

    const executable = this.getCodexPath();
    return new Promise((resolve, reject) => {
      const child = this.spawnProcess(executable, args, {
        cwd: account.codexHome,
        env: childEnvironment,
        stdio: ['pipe', 'pipe', 'pipe'],
        windowsHide: true,
      });
      let stdout = '';
      let stderr = '';
      let settled = false;
      const appendOutput = (current, data) => {
        if (current.length >= 262_144) return current;
        return (current + data.toString('utf8')).slice(0, 262_144);
      };
      const stopChild = () => {
        if (child.exitCode === null) child.kill('SIGTERM');
        setTimeout(() => { if (child.exitCode === null) child.kill('SIGKILL'); }, 2_000).unref();
      };
      const finish = (callback) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        if (signal) signal.removeEventListener('abort', onAbort);
        callback();
      };
      const onAbort = () => {
        stopChild();
        finish(() => reject(new Error('通过 ChatGPT 登录已取消。')));
      };
      const timer = setTimeout(() => {
        stopChild();
        finish(() => reject(new Error('Codex CLI 响应超时。')));
      }, timeoutMs);
      if (signal) {
        signal.addEventListener('abort', onAbort, { once: true });
        if (signal.aborted) onAbort();
      }
      child.stdout.on('data', (data) => {
        stdout = appendOutput(stdout, data);
        if (onOutput) onOutput(data.toString('utf8'), 'stdout');
      });
      child.stderr.on('data', (data) => {
        stderr = appendOutput(stderr, data);
        if (onOutput) onOutput(data.toString('utf8'), 'stderr');
      });
      child.on('error', (error) => {
        finish(() => reject(error));
      });
      child.on('close', (code) => {
        finish(() => resolve({ code: code ?? -1, stdout: redactSecrets(stdout.trim()), stderr: redactSecrets(stderr.trim()) }));
      });
      if (stdinText !== null) child.stdin.end(`${stdinText}\n`);
      else child.stdin.end();
    });
  }

  async login(account, accessToken) {
    if (account.authKind === 'official_oauth') {
      throw new Error('官方网页登录请使用账号表单中的“生成登录链接”流程。');
    }
    if (account.authKind !== 'access_token') {
      throw new Error('兼容 API 账号已通过 API Key 配置，无需执行 Access Token 登录。');
    }
    const token = String(accessToken || '').trim();
    if (!token) throw new Error('Access Token 不能为空。');
    const result = await this.run(account, ['login', '--with-access-token'], { stdinText: token, timeoutMs: 60_000 });
    if (result.code !== 0) throw new Error(result.stderr || result.stdout || '登录失败。');
    return { ok: true, text: result.stdout || '登录成功' };
  }

  cancelOfficialLogin(accountId) {
    const handle = this.activeOfficialLogins.get(String(accountId || ''));
    if (!handle) return false;
    Promise.resolve(handle.cancel()).catch(() => {});
    return true;
  }

  startOfficialLogin(account) {
    if (account.authKind !== 'official_oauth') {
      throw new Error('只有 ChatGPT 官方账号可以生成网页登录链接。');
    }
    const accountId = String(account.id || account.codexHome);
    if (this.activeOfficialLogins.size > 0) {
      throw new Error(this.activeOfficialLogins.has(accountId)
        ? '此账号正在进行 ChatGPT 官方网页登录。'
        : '已有另一个账号正在登录，请先完成或取消。');
    }
    const executable = this.getCodexPath();
    const session = this.appServerFactory
      ? this.appServerFactory({
        account,
        executable,
        env: this.childEnvironment(account.codexHome),
      })
      : new AppServerOAuthSession({
        account,
        executable,
        env: this.childEnvironment(account.codexHome),
      });
    const started = session.start();
    const handle = {
      ready: Promise.resolve(started.ready),
      completed: Promise.resolve(started.completed)
        .then(() => {
          if (!isOfficialOAuthAuth(path.join(account.codexHome, 'auth.json'))) {
            throw new Error('官方登录已返回成功，但独立账号目录中没有可用凭据。');
          }
          return { ok: true, badge: 'OAUTH', text: '✓ 已登录' };
        })
        .finally(() => {
          if (this.activeOfficialLogins.get(accountId) === handle) {
            this.activeOfficialLogins.delete(accountId);
          }
        }),
      cancel: () => (typeof started.cancel === 'function' ? started.cancel() : session.cancel()),
    };
    this.activeOfficialLogins.set(accountId, handle);
    return handle;
  }

  hasValidOfficialOAuth(account) {
    return account?.authKind === 'official_oauth' &&
      isOfficialOAuthAuth(path.join(account.codexHome, 'auth.json'));
  }

  async status(account) {
    if (account.authKind === 'compatible_api') {
      let hasAuth = false;
      try {
        const auth = JSON.parse(fs.readFileSync(path.join(account.codexHome, 'auth.json'), 'utf8'));
        hasAuth = typeof auth.OPENAI_API_KEY === 'string' && auth.OPENAI_API_KEY.trim().length > 0;
      } catch {
        hasAuth = false;
      }
      return { ok: hasAuth, badge: hasAuth ? 'API_KEY' : 'FAILED', text: hasAuth ? 'API Key 已配置' : '尚未配置 API Key' };
    }
    const result = await this.run(account, ['login', 'status']);
    const text = [result.stdout, result.stderr].filter(Boolean).join('\n');
    if (account.authKind === 'official_oauth') {
      const validOAuth = isOfficialOAuthAuth(path.join(account.codexHome, 'auth.json'));
      return {
        ok: result.code === 0 && validOAuth,
        badge: result.code === 0 && validOAuth ? 'OAUTH' : 'FAILED',
        text: result.code === 0 && validOAuth
          ? '通过 ChatGPT 登录（官方）'
          : '尚未通过 ChatGPT 登录（官方）',
      };
    }
    return {
      ok: result.code === 0,
      badge: result.code === 0 ? (/personal access token/i.test(text) ? 'TOKEN' : 'LOGGED') : 'FAILED',
      text: text || (result.code === 0 ? '已登录' : '未登录'),
    };
  }

  async launchTerminal(account, projectPath = null) {
    if (this.platform !== 'darwin') {
      throw new Error('终端启动功能需要在 macOS 中运行。');
    }
    await this.ensureAccountServices(account);
    const executable = this.getCodexPath();
    const settings = this.settingsProvider() || {};
    const workingDirectory = normalizeProjectPath(projectPath || settings.projectPath || os.homedir());
    const accountId = String(account?.id || '').trim();
    if (!TERMINAL_ACCOUNT_ID_PATTERN.test(accountId)) {
      throw terminalLifecycleError('账号 ID 无法用于安全的 Terminal 会话，启动已取消。', 'TERMINAL_ACCOUNT_INVALID');
    }
    const requestedCodexHome = String(account?.codexHome || '').trim();
    if (!requestedCodexHome || !path.isAbsolute(requestedCodexHome)) {
      throw terminalLifecycleError('账号凭据目录无效，Terminal 启动已取消。', 'TERMINAL_ACCOUNT_INVALID');
    }
    const codexHome = path.resolve(requestedCodexHome);
    this.ensureTerminalDirectories();
    if (this.removeLegacyTerminalLauncher(accountId)) {
      this.writeLegacyTerminalQuarantine(accountId);
      throw terminalLifecycleError(
        '已撤销旧版 Terminal 启动器。为防止已排队的旧会话迟到启动，请关闭相关 Terminal 窗口后重试。',
        'TERMINAL_LEGACY_LAUNCHER_REVOKED',
      );
    }
    const legacyQuarantine = this.readLegacyTerminalQuarantine(accountId);
    if (legacyQuarantine) {
      const processes = await this.verifyLegacyTerminalQuarantines([legacyQuarantine]);
      const descriptors = this.listTerminalDescriptors(processes, new Set([accountId]));
      const inspected = descriptors.map((descriptor) => this.inspectTerminalSession(descriptor, processes));
      this.assertNoUnregisteredTerminalCodex(processes, inspected);
      removeRegularTerminalFile(legacyQuarantine.quarantinePath);
    }
    const nonce = crypto.randomUUID();
    const artifactPaths = terminalArtifactPaths(
      this.terminalLaunchersDir,
      this.terminalSessionsDir,
      accountId,
      nonce,
    );
    const descriptor = {
      version: TERMINAL_SESSION_VERSION,
      accountId,
      nonce,
      codexHome,
      executable: path.resolve(executable),
      workingDirectory,
      ...artifactPaths,
      createdAt: new Date().toISOString(),
    };
    const proxyEnvironment = applyProxyEnvironment({}, settings);
    const proxyLines = ['HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY']
      .filter((name) => proxyEnvironment[name])
      .map((name) => `export ${name}=${shellQuote(proxyEnvironment[name])}`)
      .join('\n');
    const script = this.buildTerminalLauncherScript(descriptor, proxyLines);
    try {
      fs.writeFileSync(
        descriptor.descriptorPath,
        `${JSON.stringify(descriptor, null, 2)}\n`,
        { encoding: 'utf8', mode: 0o600, flag: 'wx' },
      );
      fs.writeFileSync(descriptor.launcherPath, script, { encoding: 'utf8', mode: 0o700, flag: 'wx' });
      fs.chmodSync(descriptor.launcherPath, 0o700);
    } catch (error) {
      try { removeRegularTerminalFile(descriptor.launcherPath); } catch { /* preserve the original error */ }
      try { removeRegularTerminalFile(descriptor.descriptorPath); } catch { /* preserve the original error */ }
      throw error;
    }

    try {
      const child = this.spawnProcess('/usr/bin/open', ['-a', 'Terminal', descriptor.launcherPath], { stdio: 'ignore' });
      await waitForTerminalOpen(child, this.terminalLaunchTimeoutMs);
      const session = await this.waitForTerminalSessionReady(descriptor, this.terminalLaunchTimeoutMs);
      return {
        ok: true,
        projectPath: workingDirectory,
        terminalSessionId: nonce,
        pid: session.child.pid,
      };
    } catch (error) {
      try {
        await this.cancelPendingTerminalSession(descriptor);
      } catch (cleanupError) {
        error.cleanupError = cleanupError;
      }
      throw error;
    }
  }

  ensureTerminalDirectories() {
    ensurePrivateDirectory(this.terminalLaunchersDir);
    ensurePrivateDirectory(this.terminalSessionsDir);
  }

  removeLegacyTerminalLauncher(accountId) {
    if (!TERMINAL_ACCOUNT_ID_PATTERN.test(String(accountId || ''))) {
      throw terminalLifecycleError('旧 Terminal 启动器的账号 ID 无效，清理已取消。');
    }
    ensurePrivateDirectory(this.terminalLaunchersDir);
    const legacyPath = assertDirectTerminalArtifact(
      this.terminalLaunchersDir,
      path.join(this.terminalLaunchersDir, `codex-${accountId}.command`),
    );
    return removeRegularTerminalFile(legacyPath);
  }

  legacyTerminalLauncherPath(accountId) {
    if (!TERMINAL_ACCOUNT_ID_PATTERN.test(String(accountId || ''))) {
      throw terminalLifecycleError('旧 Terminal 启动器的账号 ID 无效，检查已取消。');
    }
    return assertDirectTerminalArtifact(
      this.terminalLaunchersDir,
      path.join(this.terminalLaunchersDir, `codex-${accountId}.command`),
    );
  }

  legacyTerminalQuarantinePath(accountId) {
    if (!TERMINAL_ACCOUNT_ID_PATTERN.test(String(accountId || ''))) {
      throw terminalLifecycleError('旧 Terminal 隔离标记的账号 ID 无效，检查已取消。');
    }
    return assertDirectTerminalArtifact(
      this.terminalSessionsDir,
      path.join(this.terminalSessionsDir, `legacy-${accountId}.quarantine.json`),
    );
  }

  readLegacyTerminalQuarantine(accountId) {
    const quarantinePath = this.legacyTerminalQuarantinePath(accountId);
    const text = readRegularTerminalFile(quarantinePath, MAX_TERMINAL_DESCRIPTOR_BYTES);
    if (text === null) return null;
    let record;
    try { record = JSON.parse(text); } catch (cause) {
      throw terminalLifecycleError('旧 Terminal 隔离标记已损坏，账号删除已取消。', 'TERMINAL_SESSION_UNCERTAIN', cause);
    }
    const launcherPath = this.legacyTerminalLauncherPath(accountId);
    if (record?.version !== 1 || record?.accountId !== accountId ||
        path.resolve(String(record?.launcherPath || '')) !== path.resolve(launcherPath) ||
        !Number.isSafeInteger(record?.createdAtMs) || record.createdAtMs < 0 ||
        !Number.isSafeInteger(record?.notBeforeMs) || record.notBeforeMs < record.createdAtMs) {
      throw terminalLifecycleError('旧 Terminal 隔离标记内容无效，账号删除已取消。');
    }
    return { ...record, quarantinePath, launcherPath };
  }

  writeLegacyTerminalQuarantine(accountId) {
    const quarantinePath = this.legacyTerminalQuarantinePath(accountId);
    const launcherPath = this.legacyTerminalLauncherPath(accountId);
    const createdAtMs = Date.now();
    const record = {
      version: 1,
      accountId,
      launcherPath,
      createdAtMs,
      notBeforeMs: createdAtMs + this.terminalLegacyQuarantineMs,
    };
    const existing = this.readLegacyTerminalQuarantine(accountId);
    fs.writeFileSync(
      quarantinePath,
      `${JSON.stringify(record, null, 2)}\n`,
      { encoding: 'utf8', mode: 0o600, flag: existing ? 'w' : 'wx' },
    );
    return { ...record, quarantinePath };
  }

  async verifyLegacyTerminalQuarantines(records) {
    if (records.length === 0) return this.terminalProcessSnapshot();
    const now = Date.now();
    if (records.some((record) => now < record.notBeforeMs)) {
      throw terminalLifecycleError(
        '旧版 Terminal 启动请求仍在隔离冷却期。请关闭相关 Terminal 窗口，稍后重试删除。',
        'TERMINAL_LEGACY_QUARANTINED',
      );
    }
    const launcherPaths = records.map((record) => record.launcherPath);
    let processes = this.terminalProcessSnapshot();
    this.assertNoRunningLegacyTerminalLaunchers(processes, launcherPaths);
    await waitBriefly(50);
    processes = this.terminalProcessSnapshot();
    this.assertNoRunningLegacyTerminalLaunchers(processes, launcherPaths);
    return processes;
  }

  assertNoRunningLegacyTerminalLaunchers(processes, launcherPaths) {
    const active = processes.find((record) => record.uid === this.processUid &&
      launcherPaths.some((launcherPath) => commandHasExactPathArgument(record.command, launcherPath)));
    if (active) {
      throw terminalLifecycleError('检测到仍在启动或运行的旧 Terminal 会话。请关闭对应 Terminal 窗口后重试删除。');
    }
  }

  buildTerminalLauncherScript(descriptor, proxyLines = '') {
    const readyTempPrefix = `${descriptor.readyPath}.tmp`;
    return `#!/bin/zsh
set -eu
umask 077
unset OPENAI_API_KEY OPENAI_ACCESS_TOKEN OPENAI_TOKEN CODEX_ACCESS_TOKEN CODEX_API_KEY AZURE_OPENAI_API_KEY
export CODEX_HOME=${shellQuote(descriptor.codexHome)}
export CODEX_SQLITE_HOME=${shellQuote(descriptor.codexHome)}
export CODEX_ACCOUNT_MANAGER_ACCOUNT_ID=${shellQuote(descriptor.accountId)}
export CODEX_ACCOUNT_MANAGER_SESSION_NONCE=${shellQuote(descriptor.nonce)}
${proxyLines}
session_nonce=${shellQuote(descriptor.nonce)}
session_account_id=${shellQuote(descriptor.accountId)}
descriptor_path=${shellQuote(descriptor.descriptorPath)}
ready_path=${shellQuote(descriptor.readyPath)}
launcher_path=${shellQuote(descriptor.launcherPath)}
claim_path=${shellQuote(descriptor.claimPath)}
claim_owner="$claim_path/owner"
claim_owner_tmp="$claim_path/owner.tmp.$$"
ready_tmp=${shellQuote(readyTempPrefix)}."$$"
codex_pid=""
claim_owned=0
preserve_session=0
cleanup_session() {
  (( preserve_session == 0 )) || return
  (( claim_owned == 1 )) || return
  /bin/rm -f -- "$ready_tmp" "$ready_path" "$descriptor_path" "$launcher_path" "$claim_owner_tmp" "$claim_owner"
  /bin/rmdir "$claim_path" 2>/dev/null || true
}
terminate_session() {
  exit_code="$1"
  if [[ -n "$codex_pid" ]]; then
    preserve_session=1
    return 0
  fi
  trap - HUP INT TERM
  exit "$exit_code"
}
write_ready() {
  /usr/bin/printf '%s\\n%s\\n%s\\n' "$session_nonce" "$$" "${'${codex_pid:-0}'}" > "$ready_tmp"
  /bin/chmod 600 "$ready_tmp"
  /bin/mv -f -- "$ready_tmp" "$ready_path"
}
trap cleanup_session EXIT
trap 'terminate_session 129' HUP
trap 'terminate_session 130' INT
trap 'terminate_session 143' TERM
[[ -f "$descriptor_path" && ! -L "$descriptor_path" ]] || exit 74
if ! /bin/mkdir -m 700 "$claim_path" 2>/dev/null; then
  exit 75
fi
claim_owned=1
/usr/bin/printf '%s\\n%s\\n%s\\n' "$session_nonce" "$session_account_id" "$$" > "$claim_owner_tmp"
/bin/chmod 600 "$claim_owner_tmp"
/bin/mv -f -- "$claim_owner_tmp" "$claim_owner"
write_ready
[[ -d ${shellQuote(descriptor.codexHome)} && ! -L ${shellQuote(descriptor.codexHome)} ]] || exit 74
cd ${shellQuote(descriptor.workingDirectory)}
[[ -f "$descriptor_path" && ! -L "$descriptor_path" ]] || exit 74
${shellQuote(descriptor.executable)} -C ${shellQuote(descriptor.workingDirectory)} "$@" </dev/tty >/dev/tty 2>&1 &
codex_pid="$!"
write_ready
/bin/rm -f -- "$launcher_path"
while true; do
  if wait "$codex_pid"; then
    exit_code=0
  else
    exit_code="$?"
  fi
  /bin/kill -0 "$codex_pid" 2>/dev/null || break
done
codex_pid=""
preserve_session=0
exit "$exit_code"
`;
  }

  readTerminalDescriptor(descriptorPath) {
    const safePath = assertDirectTerminalArtifact(this.terminalSessionsDir, descriptorPath);
    const nameMatch = TERMINAL_DESCRIPTOR_PATTERN.exec(path.basename(safePath));
    if (!nameMatch) throw terminalLifecycleError('Terminal 会话描述文件名无效，操作已取消。');
    const text = readRegularTerminalFile(safePath, MAX_TERMINAL_DESCRIPTOR_BYTES);
    if (text === null) return null;
    let descriptor;
    try { descriptor = JSON.parse(text); } catch (cause) {
      throw terminalLifecycleError('Terminal 会话描述文件已损坏，账号删除已取消。', 'TERMINAL_SESSION_UNCERTAIN', cause);
    }
    const accountId = String(descriptor?.accountId || '');
    const nonce = String(descriptor?.nonce || '');
    const expectedPaths = terminalArtifactPaths(
      this.terminalLaunchersDir,
      this.terminalSessionsDir,
      accountId,
      nonce,
    );
    const fieldsMatch = descriptor?.version === TERMINAL_SESSION_VERSION &&
      accountId === nameMatch[1] && nonce.toLowerCase() === nameMatch[2].toLowerCase() &&
      path.resolve(String(descriptor?.launcherPath || '')) === path.resolve(expectedPaths.launcherPath) &&
      path.resolve(String(descriptor?.descriptorPath || '')) === path.resolve(expectedPaths.descriptorPath) &&
      path.resolve(String(descriptor?.readyPath || '')) === path.resolve(expectedPaths.readyPath) &&
      path.resolve(String(descriptor?.claimPath || '')) === path.resolve(expectedPaths.claimPath) &&
      path.isAbsolute(String(descriptor?.codexHome || '')) &&
      path.isAbsolute(String(descriptor?.executable || '')) &&
      path.isAbsolute(String(descriptor?.workingDirectory || ''));
    if (!fieldsMatch) {
      throw terminalLifecycleError('Terminal 会话描述与文件名不一致，账号删除已取消。');
    }
    return {
      ...descriptor,
      descriptorPath: safePath,
      launcherPath: expectedPaths.launcherPath,
      readyPath: expectedPaths.readyPath,
      claimPath: expectedPaths.claimPath,
      codexHome: path.resolve(descriptor.codexHome),
      executable: path.resolve(descriptor.executable),
      workingDirectory: path.resolve(descriptor.workingDirectory),
    };
  }

  readTerminalReady(descriptor) {
    const readyPath = assertDirectTerminalArtifact(this.terminalSessionsDir, descriptor.readyPath);
    const nameMatch = TERMINAL_READY_PATTERN.exec(path.basename(readyPath));
    if (!nameMatch || nameMatch[1] !== descriptor.accountId ||
        nameMatch[2].toLowerCase() !== descriptor.nonce.toLowerCase()) {
      throw terminalLifecycleError('Terminal 会话就绪文件名无效，操作已取消。');
    }
    const text = readRegularTerminalFile(readyPath, MAX_TERMINAL_READY_BYTES);
    return text === null ? null : parseTerminalReady(text, descriptor.nonce);
  }

  readTerminalClaim(descriptor, remainingRetries = 2) {
    const claimPath = assertDirectTerminalArtifact(this.terminalSessionsDir, descriptor.claimPath);
    const nameMatch = TERMINAL_CLAIM_PATTERN.exec(path.basename(claimPath));
    if (!nameMatch || nameMatch[1] !== descriptor.accountId ||
        nameMatch[2].toLowerCase() !== descriptor.nonce.toLowerCase()) {
      throw terminalLifecycleError('Terminal 会话 claim 目录名无效，操作已取消。');
    }
    let details;
    try {
      details = fs.lstatSync(claimPath);
    } catch (error) {
      if (error?.code === 'ENOENT') return null;
      throw error;
    }
    if (!details.isDirectory() || details.isSymbolicLink()) {
      throw terminalLifecycleError('Terminal 会话 claim 不是安全的真实目录，操作已取消。');
    }
    let entries;
    try {
      entries = fs.readdirSync(claimPath, { withFileTypes: true });
    } catch (error) {
      if (error?.code === 'ENOENT') return null;
      throw error;
    }
    const owner = entries.find((entry) => entry.name === 'owner') || null;
    const revoked = entries.find((entry) => entry.name === 'revoked') || null;
    const temporaryOwners = entries.filter((entry) => /^owner\.tmp\.\d+$/.test(entry.name));
    const unknown = entries.filter((entry) =>
      entry.name !== 'owner' && entry.name !== 'revoked' && !/^owner\.tmp\.\d+$/.test(entry.name));
    if (unknown.length > 0 || temporaryOwners.some((entry) => !entry.isFile() || entry.isSymbolicLink()) ||
        (revoked && (!revoked.isFile() || revoked.isSymbolicLink()))) {
      throw terminalLifecycleError('Terminal 会话 claim 中包含无法识别的对象，操作已取消。');
    }
    if (revoked) {
      if (owner || temporaryOwners.length > 0) {
        throw terminalLifecycleError('Terminal 会话 claim 同时包含启动与撤销标记，操作已取消。');
      }
      const text = readRegularTerminalFile(path.join(claimPath, 'revoked'), MAX_TERMINAL_READY_BYTES);
      if (text === null && remainingRetries > 0) {
        return this.readTerminalClaim(descriptor, remainingRetries - 1);
      }
      if (text === null) return null;
      const lines = String(text || '').trim().split(/\r?\n/);
      if (lines.length !== 3 || lines[0] !== descriptor.nonce ||
          lines[1] !== descriptor.accountId || lines[2] !== 'manager') {
        throw terminalLifecycleError('Terminal 会话撤销标记无效，操作已取消。');
      }
      return { wrapperPid: null, pendingOwner: false, managerRevoked: true };
    }
    if (!owner) return { wrapperPid: null, pendingOwner: true, managerRevoked: false };
    const ownerPath = path.join(claimPath, 'owner');
    const text = readRegularTerminalFile(ownerPath, MAX_TERMINAL_READY_BYTES);
    if (text === null && remainingRetries > 0) {
      return this.readTerminalClaim(descriptor, remainingRetries - 1);
    }
    if (text === null) return null;
    return {
      ...parseTerminalClaimOwner(text, descriptor.accountId, descriptor.nonce),
      pendingOwner: false,
      managerRevoked: false,
    };
  }

  terminalLauncherPresent(descriptor) {
    const launcherPath = assertDirectTerminalArtifact(this.terminalLaunchersDir, descriptor.launcherPath);
    try {
      const details = fs.lstatSync(launcherPath);
      if (!details.isFile() || details.isSymbolicLink()) {
        throw terminalLifecycleError('Terminal 启动器不是安全的普通文件，操作已取消。');
      }
      return true;
    } catch (error) {
      if (error?.code === 'ENOENT') return false;
      throw error;
    }
  }

  terminalProcessSnapshot() {
    if (!Number.isInteger(this.processUid) || this.processUid < 0) {
      throw terminalLifecycleError('无法确认当前 macOS 用户身份，Terminal 会话操作已取消。');
    }
    let output;
    try {
      output = this.terminalProcessListRunner();
      if (typeof output !== 'string') throw new TypeError('ps output must be a string');
    } catch (error) {
      if (error?.code === TERMINAL_PROCESS_DISCOVERY_FAILED) throw error;
      throw terminalProcessDiscoveryError(error);
    }
    return parseTerminalProcessList(output);
  }

  inspectTerminalSession(descriptor, processes = this.terminalProcessSnapshot()) {
    const ready = this.readTerminalReady(descriptor);
    const claim = this.readTerminalClaim(descriptor);
    if (ready && !claim) {
      const refreshed = this.terminalProcessSnapshot();
      const referencedPids = new Set([ready.wrapperPid, ready.childPid].filter((pid) => pid > 1));
      const stillActive = refreshed.some((record) =>
        commandHasExactPathArgument(record.command, descriptor.launcherPath) || referencedPids.has(record.pid));
      if (!stillActive) {
        return { descriptor, ready, claim: null, wrapper: null, child: null, status: 'stale' };
      }
      throw terminalLifecycleError('Terminal 会话已有就绪记录但缺少原子 claim，账号删除已取消。');
    }
    const wrappersByPath = processes.filter((record) =>
      record.uid === this.processUid && commandHasExactPathArgument(record.command, descriptor.launcherPath));
    if (wrappersByPath.length > 1) {
      throw terminalLifecycleError('同一 Terminal 会话出现多个启动包装进程，账号删除已取消。');
    }
    const recordedWrapperPid = ready?.wrapperPid || claim?.wrapperPid || null;
    let wrapper = recordedWrapperPid
      ? processes.find((record) => record.pid === recordedWrapperPid) || null
      : wrappersByPath[0] || null;
    if (wrapper && (wrapper.uid !== this.processUid ||
        !commandHasExactPathArgument(wrapper.command, descriptor.launcherPath))) {
      throw terminalLifecycleError('Terminal 包装进程身份发生变化，账号删除已取消。');
    }
    if (ready && wrappersByPath.length === 1 && wrappersByPath[0].pid !== ready.wrapperPid) {
      throw terminalLifecycleError('Terminal 包装进程 PID 与会话记录不一致，账号删除已取消。');
    }
    if (claim?.wrapperPid && ready?.wrapperPid && claim.wrapperPid !== ready.wrapperPid) {
      throw terminalLifecycleError('Terminal 会话 claim 与 ready 的包装进程 PID 不一致，账号删除已取消。');
    }
    if (claim?.wrapperPid && wrappersByPath.length === 1 && wrappersByPath[0].pid !== claim.wrapperPid) {
      throw terminalLifecycleError('Terminal 会话 claim 与实际包装进程 PID 不一致，账号删除已取消。');
    }

    let child = null;
    if (ready?.childPid > 1) child = processes.find((record) => record.pid === ready.childPid) || null;
    const childrenByParent = wrapper ? processes.filter((record) =>
      record.ppid === wrapper.pid && record.uid === this.processUid &&
      commandStartsWithExecutable(record.command, descriptor.executable)) : [];
    if (childrenByParent.length > 1) {
      throw terminalLifecycleError('同一 Terminal 会话出现多个 Codex 子进程，账号删除已取消。');
    }
    if (!child && ready?.childPid === 0 && childrenByParent.length === 1) child = childrenByParent[0];
    if (child && (child.uid !== this.processUid || child.ppid !== wrapper?.pid ||
        !commandStartsWithExecutable(child.command, descriptor.executable))) {
      throw terminalLifecycleError('Terminal Codex 进程身份发生变化，账号删除已取消。');
    }
    if (ready?.childPid > 1 && childrenByParent.length === 1 && childrenByParent[0].pid !== ready.childPid) {
      throw terminalLifecycleError('Terminal Codex PID 与父子关系不一致，账号删除已取消。');
    }

    if (!wrapper && !child) {
      const status = !ready && !claim && this.terminalLauncherPresent(descriptor) ? 'unclaimed' : 'stale';
      return { descriptor, ready, claim, wrapper: null, child: null, status };
    }
    if (!wrapper) {
      throw terminalLifecycleError('Terminal 会话只剩部分进程，无法确认是否仍会写入账号目录，删除已取消。');
    }
    if (ready?.childPid > 1 && !child) {
      return { descriptor, ready, claim, wrapper, child: null, status: 'closing' };
    }
    return { descriptor, ready, claim, wrapper, child, status: child ? 'running' : 'pending' };
  }

  async waitForTerminalSessionReady(expectedDescriptor, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      const descriptor = this.readTerminalDescriptor(expectedDescriptor.descriptorPath);
      if (!descriptor) throw terminalLifecycleError('Terminal 启动请求在确认前被撤销。', 'TERMINAL_SESSION_REVOKED');
      const ready = this.readTerminalReady(descriptor);
      if (ready?.childPid > 1) {
        const inspected = this.inspectTerminalSession(descriptor);
        if (inspected.status === 'running') {
          if (fs.existsSync(descriptor.launcherPath)) {
            await waitBriefly();
            continue;
          }
          return inspected;
        }
      }
      await waitBriefly();
    }
    throw terminalLifecycleError('Terminal 已接受启动请求，但未能确认账号隔离的 Codex 进程。', 'TERMINAL_SESSION_START_TIMEOUT');
  }

  cleanupTerminalArtifacts(descriptor) {
    removeRegularTerminalFile(assertDirectTerminalArtifact(this.terminalSessionsDir, descriptor.descriptorPath));
    removeRegularTerminalFile(assertDirectTerminalArtifact(this.terminalLaunchersDir, descriptor.launcherPath));
    const readyPath = assertDirectTerminalArtifact(this.terminalSessionsDir, descriptor.readyPath);
    const readyTempPrefix = `${path.basename(readyPath)}.tmp.`;
    for (const entry of fs.readdirSync(this.terminalSessionsDir, { withFileTypes: true })) {
      if (!entry.name.startsWith(readyTempPrefix)) continue;
      const match = TERMINAL_READY_TEMP_PATTERN.exec(entry.name);
      if (!match || match[1] !== descriptor.accountId ||
          match[2].toLowerCase() !== descriptor.nonce.toLowerCase()) {
        throw terminalLifecycleError('Terminal 临时就绪记录名称异常，账号删除已取消。');
      }
      removeRegularTerminalFile(path.join(this.terminalSessionsDir, entry.name));
    }
    removeRegularTerminalFile(readyPath);
    const claimPath = assertDirectTerminalArtifact(this.terminalSessionsDir, descriptor.claimPath);
    let entries;
    try {
      const details = fs.lstatSync(claimPath);
      if (!details.isDirectory() || details.isSymbolicLink()) {
        throw terminalLifecycleError('Terminal 会话 claim 残留不安全，账号删除已取消。');
      }
      entries = fs.readdirSync(claimPath, { withFileTypes: true });
    } catch (error) {
      if (error?.code === 'ENOENT') return;
      throw error;
    }
    for (const entry of entries) {
      if (entry.name !== 'owner' && entry.name !== 'revoked' && !/^owner\.tmp\.\d+$/.test(entry.name)) {
        throw terminalLifecycleError('Terminal 会话 claim 中包含未知残留，账号删除已取消。');
      }
      removeRegularTerminalFile(path.join(claimPath, entry.name));
    }
    try {
      fs.rmdirSync(claimPath);
    } catch (error) {
      if (error?.code !== 'ENOENT') throw error;
    }
  }

  listTerminalDescriptors(processes = this.terminalProcessSnapshot(), targetAccountIds = null) {
    this.ensureTerminalDirectories();
    const groups = new Map();
    const groupFor = (accountId, nonce) => {
      const key = `${accountId}\0${nonce.toLowerCase()}`;
      if (!groups.has(key)) {
        groups.set(key, {
          accountId,
          nonce,
          descriptorPath: null,
          readyPath: null,
          readyTempPaths: [],
          claimPath: null,
          launcherPath: null,
        });
      }
      return groups.get(key);
    };
    const assertRegularEntry = (entry, label) => {
      if (!entry.isFile() || entry.isSymbolicLink()) {
        throw terminalLifecycleError(`${label}不是安全的普通文件，账号删除已取消。`);
      }
    };

    for (const entry of fs.readdirSync(this.terminalSessionsDir, { withFileTypes: true })) {
      const descriptorMatch = TERMINAL_DESCRIPTOR_PATTERN.exec(entry.name);
      const readyMatch = TERMINAL_READY_PATTERN.exec(entry.name);
      const readyTempMatch = TERMINAL_READY_TEMP_PATTERN.exec(entry.name);
      const claimMatch = TERMINAL_CLAIM_PATTERN.exec(entry.name);
      const legacyQuarantineMatch = TERMINAL_LEGACY_QUARANTINE_PATTERN.exec(entry.name);
      if (descriptorMatch) {
        assertRegularEntry(entry, 'Terminal 会话描述');
        groupFor(descriptorMatch[1], descriptorMatch[2]).descriptorPath =
          path.join(this.terminalSessionsDir, entry.name);
      } else if (readyMatch) {
        assertRegularEntry(entry, 'Terminal 就绪记录');
        groupFor(readyMatch[1], readyMatch[2]).readyPath =
          path.join(this.terminalSessionsDir, entry.name);
      } else if (readyTempMatch) {
        assertRegularEntry(entry, 'Terminal 临时就绪记录');
        groupFor(readyTempMatch[1], readyTempMatch[2]).readyTempPaths.push(
          path.join(this.terminalSessionsDir, entry.name),
        );
      } else if (claimMatch) {
        if (!entry.isDirectory() || entry.isSymbolicLink()) {
          throw terminalLifecycleError('Terminal claim 残留不是安全的真实目录，账号删除已取消。');
        }
        groupFor(claimMatch[1], claimMatch[2]).claimPath =
          path.join(this.terminalSessionsDir, entry.name);
      } else if (legacyQuarantineMatch) {
        assertRegularEntry(entry, '旧 Terminal 隔离标记');
      } else {
        throw terminalLifecycleError('发现无法识别的 Terminal 会话残留，账号删除已取消。');
      }
    }

    for (const entry of fs.readdirSync(this.terminalLaunchersDir, { withFileTypes: true })) {
      const match = TERMINAL_LAUNCHER_PATTERN.exec(entry.name);
      const legacyMatch = /^codex-([A-Za-z0-9-]{1,80})\.command$/.exec(entry.name);
      if (!match && !legacyMatch) {
        throw terminalLifecycleError('发现无法识别的 Terminal 启动器残留，账号删除已取消。');
      }
      assertRegularEntry(entry, 'Terminal 启动器');
      if (match) {
        groupFor(match[1], match[2]).launcherPath = path.join(this.terminalLaunchersDir, entry.name);
      }
    }

    const descriptors = [];
    for (const group of groups.values()) {
      const isTargetAccount = !targetAccountIds || targetAccountIds.has(group.accountId);
      const expectedPaths = terminalArtifactPaths(
        this.terminalLaunchersDir,
        this.terminalSessionsDir,
        group.accountId,
        group.nonce,
      );
      const pseudoDescriptor = {
        accountId: group.accountId,
        nonce: group.nonce,
        ...expectedPaths,
      };
      const wrappers = processes.filter((record) =>
        commandHasExactPathArgument(record.command, expectedPaths.launcherPath));
      if (wrappers.length > 1 || wrappers.some((record) => record.uid !== this.processUid)) {
        throw terminalLifecycleError('Terminal 残留对应的包装进程身份不确定，账号删除已取消。');
      }

      const temporaryPids = [
        ...group.readyTempPaths.map((filePath) => Number(path.basename(filePath).match(/\.tmp\.(\d+)$/)?.[1])),
      ];
      if (group.claimPath) {
        this.readTerminalClaim(pseudoDescriptor);
        let claimEntries = [];
        try {
          claimEntries = fs.readdirSync(group.claimPath, { withFileTypes: true });
        } catch (error) {
          if (error?.code !== 'ENOENT') throw error;
        }
        for (const entry of claimEntries) {
          const match = /^owner\.tmp\.(\d+)$/.exec(entry.name);
          if (match) temporaryPids.push(Number(match[1]));
        }
      }
      for (const temporaryPid of temporaryPids) {
        const record = processes.find((item) => item.pid === temporaryPid) || null;
        if (record && (record.uid !== this.processUid ||
            !commandHasExactPathArgument(record.command, expectedPaths.launcherPath))) {
          throw terminalLifecycleError('Terminal 临时残留 PID 已被复用，账号删除已取消。');
        }
      }

      let descriptor = null;
      let descriptorError = null;
      if (group.descriptorPath) {
        try {
          descriptor = this.readTerminalDescriptor(group.descriptorPath);
        } catch (error) {
          descriptorError = error;
        }
      }
      if (descriptor) {
        if (isTargetAccount) {
          for (const filePath of group.readyTempPaths) {
            const pid = Number(path.basename(filePath).match(/\.tmp\.(\d+)$/)?.[1]);
            if (!processes.some((record) => record.pid === pid)) {
              removeRegularTerminalFile(filePath);
            }
          }
          if (group.claimPath) {
            let claimEntries = [];
            try {
              claimEntries = fs.readdirSync(group.claimPath, { withFileTypes: true });
            } catch (error) {
              if (error?.code !== 'ENOENT') throw error;
            }
            for (const entry of claimEntries) {
              const match = /^owner\.tmp\.(\d+)$/.exec(entry.name);
              if (match && !processes.some((record) => record.pid === Number(match[1]))) {
                removeRegularTerminalFile(path.join(group.claimPath, entry.name));
              }
            }
          }
        }
        descriptors.push(descriptor);
        continue;
      }

      const ready = group.readyPath ? this.readTerminalReady(pseudoDescriptor) : null;
      const claim = group.claimPath ? this.readTerminalClaim(pseudoDescriptor) : null;
      const referencedPids = [ready?.wrapperPid, ready?.childPid, claim?.wrapperPid, ...temporaryPids]
        .filter((pid) => Number.isInteger(pid) && pid > 1);
      if (wrappers.length > 0 || referencedPids.some((pid) => processes.some((record) => record.pid === pid))) {
        throw descriptorError || terminalLifecycleError('活跃 Terminal 会话缺少可信描述文件，账号删除已取消。');
      }
      if (!isTargetAccount) continue;
      this.revokeTerminalSession({ descriptor: pseudoDescriptor, status: 'stale' });
      this.cleanupTerminalArtifacts(pseudoDescriptor);
    }
    return descriptors;
  }

  assertNoUnregisteredTerminalCodex(processes, inspectedSessions) {
    const knownChildPids = new Set(inspectedSessions.filter((item) => item.child).map((item) => item.child.pid));
    const executables = new Set(inspectedSessions.map((item) => item.descriptor.executable));
    try { executables.add(path.resolve(this.getCodexPath())); } catch { /* descriptor executables still remain authoritative */ }
    executables.add(path.resolve('/Applications/Codex Account Manager.app/Contents/Resources/codex-cli/bin/codex'));
    executables.add(path.resolve(os.homedir(), 'Applications/Codex Account Manager.app/Contents/Resources/codex-cli/bin/codex'));
    const unknown = processes.find((record) => record.uid === this.processUid &&
      ([...executables].some((executable) => commandStartsWithExecutable(record.command, executable)) ||
        commandLooksLikeCodexCli(record.command)) &&
      !knownChildPids.has(record.pid));
    if (unknown) {
      throw terminalLifecycleError('检测到无法归属账号的旧 Terminal Codex 进程。请关闭所有旧 Terminal Codex 窗口后重试删除。');
    }
  }

  assertTerminalProcessIdentity(session, role) {
    const expected = session[role];
    if (!expected) return null;
    const record = this.terminalProcessSnapshot().find((item) => item.pid === expected.pid) || null;
    if (!record) return null;
    const valid = role === 'wrapper'
      ? record.uid === this.processUid && commandHasExactPathArgument(record.command, session.descriptor.launcherPath)
      : record.uid === this.processUid && record.ppid === session.wrapper?.pid &&
        commandStartsWithExecutable(record.command, session.descriptor.executable);
    if (!valid) {
      throw terminalLifecycleError(`Terminal ${role === 'wrapper' ? '包装' : 'Codex'}进程身份在发送信号前发生变化，账号删除已取消。`);
    }
    return record;
  }

  signalTerminalSession(session, signal) {
    const descriptor = session.revoked
      ? session.descriptor
      : this.readTerminalDescriptor(session.descriptor.descriptorPath);
    if (!descriptor) {
      if (this.terminalSessionStillRunning(session)) {
        throw terminalLifecycleError('Terminal 会话记录在进程退出前消失，账号删除已取消。');
      }
      return [];
    }
    const current = this.inspectTerminalSession(descriptor);
    if (current.status === 'stale' || current.status === 'closing') return [];
    if (current.status !== 'running' || !current.child || !current.wrapper) {
      throw terminalLifecycleError('Terminal 会话尚未形成可安全停止的 Codex 子进程，账号删除已取消。');
    }
    if (session.wrapper && current.wrapper && current.wrapper.pid !== session.wrapper.pid) {
      throw terminalLifecycleError('Terminal 包装进程 PID 在发送信号前发生变化，账号删除已取消。');
    }
    if (session.child && current.child.pid !== session.child.pid) {
      throw terminalLifecycleError('Terminal Codex PID 在发送信号前发生变化，账号删除已取消。');
    }
    try {
      this.processKiller(current.child.pid, signal);
      return [current.child.pid];
    } catch (cause) {
      throw terminalLifecycleError('无法停止账号对应的 Terminal Codex 进程，删除已取消。', 'TERMINAL_SESSION_STOP_FAILED', cause);
    }
  }

  terminalSessionStillRunning(session) {
    for (const role of ['child', 'wrapper']) {
      if (this.assertTerminalProcessIdentity(session, role)) return true;
    }
    return false;
  }

  async waitForTerminalSessionExit(session, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (!this.terminalSessionStillRunning(session)) return true;
      await waitBriefly(50);
    }
    return !this.terminalSessionStillRunning(session);
  }

  async waitForTerminalSessionStoppable(session, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    let current = session;
    while (Date.now() < deadline) {
      const descriptor = session.revoked
        ? session.descriptor
        : this.readTerminalDescriptor(session.descriptor.descriptorPath);
      if (!descriptor) {
        if (!this.terminalSessionStillRunning(current)) {
          return { ...current, wrapper: null, child: null, status: 'stale' };
        }
        throw terminalLifecycleError('Terminal 会话记录在进程退出前消失，账号删除已取消。');
      }
      current = {
        ...this.inspectTerminalSession(descriptor, this.terminalProcessSnapshot()),
        revoked: Boolean(session.revoked),
      };
      if (current.status === 'running') return current;
      if (current.status === 'stale') return current;
      await waitBriefly(50);
    }
    throw terminalLifecycleError('Terminal 会话仍处于启动或收尾阶段，无法确认账号目录已经停用，删除已取消。');
  }

  async stopTerminalSession(session, timeoutMs = this.terminalStopTimeoutMs) {
    const current = await this.waitForTerminalSessionStoppable(session, timeoutMs);
    if (current.status === 'stale') return [];
    const stoppedPids = this.signalTerminalSession(current, 'SIGTERM');
    if (await this.waitForTerminalSessionExit(current, timeoutMs)) return stoppedPids;
    stoppedPids.push(...this.signalTerminalSession(current, 'SIGKILL'));
    if (!await this.waitForTerminalSessionExit(current, 1_000)) {
      throw terminalLifecycleError('账号对应的 Terminal Codex 进程拒绝退出，账号删除已取消。', 'TERMINAL_SESSION_STOP_FAILED');
    }
    return [...new Set(stoppedPids)];
  }

  revokeTerminalSession(session) {
    const claimPath = assertDirectTerminalArtifact(
      this.terminalSessionsDir,
      session.descriptor.claimPath,
    );
    let managerClaimed = false;
    try {
      fs.mkdirSync(claimPath, { mode: 0o700 });
      managerClaimed = true;
      fs.writeFileSync(
        path.join(claimPath, 'revoked'),
        `${session.descriptor.nonce}\n${session.descriptor.accountId}\nmanager\n`,
        { encoding: 'utf8', mode: 0o600, flag: 'wx' },
      );
    } catch (error) {
      if (error?.code !== 'EEXIST') throw error;
      const details = fs.lstatSync(claimPath);
      if (!details.isDirectory() || details.isSymbolicLink()) {
        throw terminalLifecycleError('Terminal 会话 claim 无法作为原子撤销门闩，账号删除已取消。');
      }
      managerClaimed = Boolean(this.readTerminalClaim(session.descriptor)?.managerRevoked);
    }
    removeRegularTerminalFile(assertDirectTerminalArtifact(
      this.terminalSessionsDir,
      session.descriptor.descriptorPath,
    ));
    removeRegularTerminalFile(assertDirectTerminalArtifact(
      this.terminalLaunchersDir,
      session.descriptor.launcherPath,
    ));
    return { ...session, revoked: true, managerClaimed };
  }

  async cancelPendingTerminalSession(descriptor) {
    const current = this.readTerminalDescriptor(descriptor.descriptorPath);
    if (!current) return;
    const session = this.revokeTerminalSession(this.inspectTerminalSession(current));
    await this.stopTerminalSession(session, Math.min(this.terminalStopTimeoutMs, 1_000));
    this.cleanupTerminalArtifacts(current);
  }

  async stopManagedTerminalSessions(accountOrAccounts, { timeoutMs = this.terminalStopTimeoutMs } = {}) {
    if (this.platform !== 'darwin') return { stoppedPids: [], removedLegacyLaunchers: 0, cleanedSessions: 0 };
    const accounts = (Array.isArray(accountOrAccounts) ? accountOrAccounts : [accountOrAccounts])
      .filter(Boolean);
    this.ensureTerminalDirectories();
    const requested = new Map();
    const legacyLauncherPaths = [];
    const legacyQuarantines = [];
    let removedLegacyLaunchers = 0;
    for (const account of accounts) {
      const accountId = String(account?.id || '').trim();
      if (!TERMINAL_ACCOUNT_ID_PATTERN.test(accountId)) {
        throw terminalLifecycleError('待删除账号的 ID 无法安全匹配 Terminal 会话，删除已取消。');
      }
      requested.set(accountId, path.resolve(String(account?.codexHome || '')));
      legacyLauncherPaths.push(this.legacyTerminalLauncherPath(accountId));
      if (this.removeLegacyTerminalLauncher(accountId)) {
        removedLegacyLaunchers += 1;
        legacyQuarantines.push(this.writeLegacyTerminalQuarantine(accountId));
      } else {
        const quarantine = this.readLegacyTerminalQuarantine(accountId);
        if (quarantine) legacyQuarantines.push(quarantine);
      }
    }

    if (removedLegacyLaunchers > 0) {
      throw terminalLifecycleError(
        '已撤销旧版 Terminal 启动器。为防止已排队的旧会话迟到启动，请关闭相关 Terminal 窗口后重试删除。',
        'TERMINAL_LEGACY_LAUNCHER_REVOKED',
      );
    }

    let processes = await this.verifyLegacyTerminalQuarantines(legacyQuarantines);
    this.assertNoRunningLegacyTerminalLaunchers(processes, legacyLauncherPaths);
    const descriptors = this.listTerminalDescriptors(processes, new Set(requested.keys()));
    let inspected = descriptors.map((descriptor) => this.inspectTerminalSession(descriptor, processes));
    const cleanupRace = inspected.some((session) => session.status === 'stale' &&
      processes.some((record) =>
        record.pid === session.ready?.wrapperPid || record.pid === session.ready?.childPid ||
        commandHasExactPathArgument(record.command, session.descriptor.launcherPath)));
    if (cleanupRace) {
      processes = this.terminalProcessSnapshot();
      inspected = descriptors.map((descriptor) => this.inspectTerminalSession(descriptor, processes));
    }
    this.assertNoUnregisteredTerminalCodex(processes, inspected);
    const targetSessions = inspected.filter((session) => requested.has(session.descriptor.accountId));
    for (const session of targetSessions) {
      if (path.resolve(session.descriptor.codexHome) !== requested.get(session.descriptor.accountId)) {
        throw terminalLifecycleError('Terminal 会话账号目录与待删除账号不一致，删除已取消。');
      }
    }

    const revokedTargets = targetSessions.map((session) => this.revokeTerminalSession(session));
    const stoppedPids = [];
    let cleanedSessions = 0;
    for (const session of revokedTargets) {
      stoppedPids.push(...await this.stopTerminalSession(session, timeoutMs));
      this.cleanupTerminalArtifacts(session.descriptor);
      cleanedSessions += 1;
    }
    if (legacyQuarantines.length > 0) {
      const finalProcesses = this.terminalProcessSnapshot();
      this.assertNoRunningLegacyTerminalLaunchers(finalProcesses, legacyLauncherPaths);
      this.assertNoUnregisteredTerminalCodex(finalProcesses, inspected);
      for (const quarantine of legacyQuarantines) {
        removeRegularTerminalFile(quarantine.quarantinePath);
      }
    }
    return {
      stoppedPids: [...new Set(stoppedPids)],
      removedLegacyLaunchers,
      cleanedSessions,
    };
  }

  findCodexApplication() {
    if (this.platform !== 'darwin') throw new Error('官方 Codex App 启动功能仅支持 macOS。');
    const settings = this.settingsProvider() || {};
    const diagnostics = [];
    const candidates = [];
    const seenCandidates = new Set();
    const addCandidate = (candidate, source) => {
      const value = String(candidate || '').trim();
      if (!value) return;
      const dedupeKey = path.normalize(value);
      if (seenCandidates.has(dedupeKey)) return;
      seenCandidates.add(dedupeKey);
      candidates.push({ appPath: value, source });
    };

    addCandidate(settings.codexAppPath, 'settings');
    if (this.allowExecutableOverride) {
      addCandidate(process.env.CODEX_ACCOUNT_MANAGER_CODEX_APP, 'environment');
    }
    if (this.appCandidates) {
      for (const candidate of this.appCandidates) addCandidate(candidate, 'injected');
    } else {
      addCandidate('/Applications/ChatGPT.app', 'standard');
      addCandidate('/Applications/Codex.app', 'standard');
      addCandidate(path.join(os.homedir(), 'Applications', 'ChatGPT.app'), 'standard');
      addCandidate(path.join(os.homedir(), 'Applications', 'Codex.app'), 'standard');
    }

    let checkedCandidateCount = 0;
    const checkNewCandidates = () => {
      while (checkedCandidateCount < candidates.length) {
        const candidate = candidates[checkedCandidateCount];
        checkedCandidateCount += 1;
        const validation = inspectCodexApplication(
          candidate.appPath,
          this.appMetadataReader,
          this.appSignatureReader,
        );
        diagnostics.push({
          source: candidate.source,
          candidate: candidate.appPath,
          ok: validation.ok,
          reason: validation.reason,
        });
        if (validation.ok) {
          return {
            appPath: validation.appPath,
            executable: validation.executable,
            appKind: validation.appKind,
            diagnostics,
          };
        }
      }
      return null;
    };

    const configuredOrStandardApplication = checkNewCandidates();
    if (configuredOrStandardApplication) return configuredOrStandardApplication;

    const actionableRejection = diagnostics.find((item) =>
      /普通 ChatGPT\.app|代码签名无效|OpenAI 官方签名/.test(String(item.reason || '')));
    const error = new Error(actionableRejection
      ? `检测到了不可用的桌面应用：${actionableRejection.reason}`
      : '没有找到包含 Codex 的官方 ChatGPT.app 或旧版 Codex.app。请更新 Codex CLI 后在终端运行“codex app”安装官方桌面应用，或在“系统配置”中手动选择；不要重命名普通 ChatGPT.app。');
    error.code = 'CODEX_APP_NOT_FOUND';
    error.diagnostics = diagnostics;
    throw error;
  }

  resolveCodexApplication(candidatePath) {
    if (this.platform !== 'darwin') throw new Error('官方 Codex App 校验功能仅支持 macOS。');
    const validation = inspectCodexApplication(
      candidatePath,
      this.appMetadataReader,
      this.appSignatureReader,
    );
    if (validation.ok) {
      return {
        appPath: validation.appPath,
        executable: validation.executable,
        appKind: validation.appKind,
      };
    }
    const error = new Error(`所选路径不是可用的 ChatGPT.app 或旧版 Codex.app：${validation.reason}`);
    error.code = 'INVALID_CODEX_APP';
    error.diagnostics = [{
      source: 'manual',
      candidate: String(candidatePath || ''),
      ok: false,
      reason: validation.reason,
    }];
    throw error;
  }

  managedDesktopProfiles(accounts) {
    const profiles = [];
    for (const account of Array.isArray(accounts) ? accounts : []) {
      const codexHome = String(account?.codexHome || '').trim();
      if (!codexHome) continue;
      const accountId = String(account?.id || path.resolve(codexHome));
      profiles.push(
        { accountId, desktopProfile: path.resolve(codexHome, 'desktop-profile') },
        { accountId, desktopProfile: path.resolve(codexHome, 'desktop-profile-theme') },
      );
    }
    return profiles;
  }

  listManagedCodexAppProcesses(accounts) {
    if (this.platform !== 'darwin') return [];
    const application = this.findCodexApplication();
    const profiles = this.managedDesktopProfiles(accounts);
    const managedProfileKeys = new Set(profiles.map((profile) =>
      `${profile.accountId}\0${path.resolve(profile.desktopProfile)}`));
    const byPid = new Map();
    let processList;
    try {
      const output = this.processListRunner();
      if (typeof output !== 'string') throw new TypeError('ps output must be a string');
      processList = output;
    } catch (error) {
      if (error?.code === DESKTOP_PROCESS_DISCOVERY_FAILED) throw error;
      throw desktopProcessDiscoveryError(error);
    }
    for (const line of processList.split(/\r?\n/)) {
      const match = /^\s*(\d+)\s+(.+)$/.exec(line);
      if (!match) continue;
      const pid = Number(match[1]);
      const command = match[2];
      if (!Number.isInteger(pid) || pid <= 1 || pid === process.pid ||
          !commandStartsWithExecutable(command, application.executable)) {
        continue;
      }
      const profile = profiles.find((item) => commandUsesDesktopProfile(command, item.desktopProfile));
      if (!profile) continue;
      byPid.set(pid, {
        pid,
        accountId: profile.accountId,
        desktopProfile: profile.desktopProfile,
        child: null,
      });
    }
    for (const record of this.desktopProcesses.values()) {
      const recordProfileKey = `${String(record.accountId || '')}\0${path.resolve(String(record.desktopProfile || ''))}`;
      if (!managedProfileKeys.has(recordProfileKey)) continue;
      if (!byPid.has(record.pid)) byPid.set(record.pid, record);
    }
    return [...byPid.values()];
  }

  desktopProcessIsAlive(record) {
    if (record.child && record.child.exitCode !== undefined) {
      return record.child.exitCode === null && record.child.signalCode == null;
    }
    try { return this.processAlive(record.pid) === true; } catch { return false; }
  }

  signalDesktopProcess(record, signal) {
    if (record.child && record.child.exitCode === null && typeof record.child.kill === 'function') {
      try { return record.child.kill(signal); } catch { /* fall through to the exact pid */ }
    }
    try {
      this.processKiller(record.pid, signal);
      return true;
    } catch {
      return false;
    }
  }

  async waitForDesktopProcessesToExit(records, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (records.some((record) => this.desktopProcessIsAlive(record)) && Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
    return records.filter((record) => this.desktopProcessIsAlive(record));
  }

  async stopManagedCodexApps(accounts, { timeoutMs = this.desktopStopTimeoutMs } = {}) {
    const records = this.listManagedCodexAppProcesses(accounts);
    if (records.length === 0) return { stoppedPids: [], stoppedRecords: [] };
    for (const record of records) this.signalDesktopProcess(record, 'SIGTERM');
    let remaining = await this.waitForDesktopProcessesToExit(records, timeoutMs);
    if (remaining.length > 0) {
      for (const record of remaining) this.signalDesktopProcess(record, 'SIGKILL');
      remaining = await this.waitForDesktopProcessesToExit(remaining, 1_000);
    }
    for (const record of records) {
      if (!this.desktopProcessIsAlive(record)) this.desktopProcesses.delete(record.pid);
    }
    if (remaining.length > 0) {
      throw new Error('旧账号的 ChatGPT（Codex）进程未能退出，账号切换已取消。请手动退出后重试。');
    }
    return {
      stoppedPids: records.map((record) => record.pid),
      stoppedRecords: records.map((record) => ({
        accountId: record.accountId,
        desktopProfile: record.desktopProfile,
      })),
    };
  }

  async switchCodexApp(account, accounts, projectPath = null, options = {}) {
    const { stopTimeoutMs, ...launchOptions } = options;
    const preparedLaunch = await this.prepareCodexAppLaunch(account, projectPath, launchOptions);
    const stopped = await this.stopManagedCodexApps(accounts, { timeoutMs: stopTimeoutMs });
    try {
      const launched = await this.launchPreparedCodexApp(preparedLaunch, {
        onExit: launchOptions.onExit,
        rejectHandoff: true,
      });
      return { ...launched, switchedAccount: true, stoppedPids: stopped.stoppedPids };
    } catch (error) {
      const accountById = new Map((Array.isArray(accounts) ? accounts : [])
        .filter((item) => item?.id)
        .map((item) => [String(item.id), item]));
      const previous = new Map();
      for (const record of stopped.stoppedRecords || []) {
        const previousAccount = accountById.get(String(record.accountId || ''));
        if (previousAccount && !previous.has(previousAccount.id)) {
          previous.set(previousAccount.id, { account: previousAccount, record });
        }
      }
      const restoredAccountIds = [];
      const rollbackErrors = [];
      for (const { account: previousAccount, record } of previous.values()) {
        try {
          await this.launchCodexApp(previousAccount, projectPath, {
            remoteDebuggingPort: launchOptions.remoteDebuggingPort,
            themeDebugProfile: path.basename(record.desktopProfile) === 'desktop-profile-theme',
            rejectHandoff: true,
          });
          restoredAccountIds.push(previousAccount.id);
          break;
        } catch (rollbackError) {
          rollbackErrors.push(rollbackError);
        }
      }
      error.rollbackRestoredAccountIds = restoredAccountIds;
      if (rollbackErrors.length > 0) error.rollbackErrors = rollbackErrors;
      if (restoredAccountIds.length > 0) {
        error.message = `${error.message}；原账号桌面已自动恢复。`;
      } else if (previous.size > 0) {
        error.message = `${error.message}；原账号桌面未能自动恢复，请手动重新启动。`;
      }
      throw error;
    }
  }

  async prepareCodexAppLaunch(account, projectPath = null, {
    remoteDebuggingPort = null,
    themeDebugProfile = false,
  } = {}) {
    const codexHome = String(account?.codexHome || '').trim();
    if (!codexHome) throw new Error('目标账号目录不可用。');
    const settings = this.settingsProvider() || {};
    const workingDirectory = normalizeProjectPath(projectPath || settings.projectPath || os.homedir());
    try {
      fs.accessSync(workingDirectory, fs.constants.R_OK | fs.constants.W_OK | fs.constants.X_OK);
    } catch (cause) {
      const error = new Error('项目启动目录不可读写。');
      error.code = 'PROJECT_DIRECTORY_UNAVAILABLE';
      error.cause = cause;
      throw error;
    }
    const application = this.findCodexApplication();
    const desktopProfile = path.join(
      codexHome,
      themeDebugProfile ? 'desktop-profile-theme' : 'desktop-profile',
    );
    try {
      fs.mkdirSync(desktopProfile, { recursive: true, mode: 0o700 });
      fs.accessSync(desktopProfile, fs.constants.R_OK | fs.constants.W_OK | fs.constants.X_OK);
    } catch (cause) {
      const error = new Error('目标账号的桌面配置目录不可读写。');
      error.code = 'DESKTOP_PROFILE_UNAVAILABLE';
      error.cause = cause;
      throw error;
    }
    await this.ensureAccountServices(account);
    const deepLink = `codex://threads/new?path=${encodeURIComponent(workingDirectory)}`;
    const args = [`--user-data-dir=${desktopProfile}`];
    const port = Number(remoteDebuggingPort);
    if (Number.isInteger(port) && port >= 1024 && port <= 65535 && port !== 8317) {
      args.push('--remote-debugging-address=127.0.0.1');
      args.push(`--remote-debugging-port=${port}`);
    }
    args.push(deepLink);
    const environment = this.childEnvironment(codexHome);
    environment.CODEX_PROJECT_PATH = workingDirectory;
    return {
      accountId: String(account?.id || path.resolve(codexHome)),
      application,
      args,
      desktopProfile,
      environment,
      workingDirectory,
    };
  }

  async launchPreparedCodexApp(preparedLaunch, {
    onExit = null,
    rejectHandoff = false,
  } = {}) {
    const {
      accountId,
      application,
      args,
      desktopProfile,
      environment,
      workingDirectory,
    } = preparedLaunch;
    const child = this.spawnProcess(application.executable, args, {
      cwd: workingDirectory,
      env: environment,
      detached: true,
      stdio: 'ignore',
    });
    const startup = await waitForDesktopProcessStart(child, this.processStartupTimeoutMs);
    if (startup.handedOff && rejectHandoff) {
      const error = new Error('目标账号的桌面进程未独立启动，请退出仍在运行的 ChatGPT（Codex）后重试。当前账号未更改。');
      error.code = 'CROSS_ACCOUNT_HANDOFF';
      throw error;
    }
    if (!startup.handedOff && Number.isInteger(child.pid)) {
      this.desktopProcesses.set(child.pid, {
        pid: child.pid,
        accountId,
        desktopProfile,
        child,
      });
    }
    if (!startup.handedOff && typeof child?.once === 'function') {
      child.once('exit', (code, signal) => {
        if (Number.isInteger(child.pid)) this.desktopProcesses.delete(child.pid);
        if (typeof onExit === 'function') onExit({ code, signal, pid: child.pid ?? null });
      });
    }
    child.unref?.();
    return {
      ok: true,
      appPath: application.appPath,
      appKind: application.appKind,
      projectPath: workingDirectory,
      desktopProfile,
      handedOff: startup.handedOff,
      pid: Number.isInteger(child.pid) ? child.pid : null,
    };
  }

  async launchCodexApp(account, projectPath = null, options = {}) {
    const preparedLaunch = await this.prepareCodexAppLaunch(account, projectPath, options);
    return this.launchPreparedCodexApp(preparedLaunch, options);
  }
}

function inspectCodexApplication(
  candidatePath,
  metadataReader = readApplicationInfoPlist,
  signatureReader = readApplicationSignature,
) {
  const requestedPath = String(candidatePath || '').trim();
  if (!requestedPath) return { ok: false, reason: '没有提供应用路径。' };
  if (!path.isAbsolute(requestedPath)) return { ok: false, reason: '请选择应用的绝对路径。' };
  const requestedAppName = path.basename(path.normalize(requestedPath)).toLowerCase();
  const appKind = requestedAppName === 'chatgpt.app'
    ? 'chatgpt'
    : requestedAppName === 'codex.app'
      ? 'codex-legacy'
      : null;
  if (!appKind) {
    return { ok: false, reason: '所选项目必须是 ChatGPT.app 或旧版 Codex.app。' };
  }

  let resolvedApp;
  try {
    resolvedApp = fs.realpathSync(requestedPath);
  } catch {
    return { ok: false, reason: '应用路径不存在或无法访问。' };
  }
  if (path.basename(resolvedApp).toLowerCase() !== requestedAppName) {
    return { ok: false, reason: '所选路径没有指向名称匹配的真实应用包。' };
  }
  try {
    if (!fs.statSync(resolvedApp).isDirectory()) {
      return { ok: false, reason: '所选应用不是目录。' };
    }
  } catch {
    return { ok: false, reason: '所选应用无法访问。' };
  }

  const infoPlistPath = path.join(resolvedApp, 'Contents', 'Info.plist');
  let resolvedInfoPlist;
  try {
    resolvedInfoPlist = fs.realpathSync(infoPlistPath);
    if (!fs.statSync(resolvedInfoPlist).isFile() || !isPathInside(resolvedApp, resolvedInfoPlist)) {
      return { ok: false, reason: 'Contents/Info.plist 无效或指向应用目录之外。' };
    }
  } catch {
    return { ok: false, reason: '应用缺少可读取的 Contents/Info.plist。' };
  }

  let metadata;
  try {
    metadata = metadataReader(resolvedInfoPlist);
  } catch {
    return { ok: false, reason: '无法读取应用的 Contents/Info.plist。' };
  }
  if (!metadata || metadata.CFBundleIdentifier !== CODEX_BUNDLE_IDENTIFIER) {
    const isClassicChatGpt = metadata?.CFBundleIdentifier === 'com.openai.chat';
    return {
      ok: false,
      reason: isClassicChatGpt
        ? '这是普通 ChatGPT.app（com.openai.chat），不是包含 Codex 的桌面应用；请勿重命名它，可更新 Codex CLI 后运行“codex app”安装正确版本。'
        : `CFBundleIdentifier 必须是 ${CODEX_BUNDLE_IDENTIFIER}；此应用不能用于 Codex 账号启动。`,
    };
  }

  let signature;
  try {
    signature = signatureReader(resolvedApp);
  } catch {
    return {
      ok: false,
      reason: '应用代码签名无效或已被修改。请删除该应用，并通过官方“codex app”重新安装。',
    };
  }
  if (signature?.identifier !== CODEX_BUNDLE_IDENTIFIER ||
      signature?.teamIdentifier !== OPENAI_APPLE_TEAM_IDENTIFIER) {
    return {
      ok: false,
      reason: '应用不是 OpenAI 官方签名的 Codex 桌面版。请删除该应用，并通过官方“codex app”重新安装。',
    };
  }

  const executableName = typeof metadata.CFBundleExecutable === 'string'
    ? metadata.CFBundleExecutable.trim()
    : '';
  if (!executableName || executableName !== metadata.CFBundleExecutable ||
      executableName === '.' || executableName === '..' || /[\\/]/.test(executableName)) {
    return { ok: false, reason: 'Info.plist 中的 CFBundleExecutable 无效。' };
  }

  const executableRoot = path.join(resolvedApp, 'Contents', 'MacOS');
  const executablePath = path.join(executableRoot, executableName);
  let executable;
  try {
    fs.accessSync(executablePath, fs.constants.X_OK);
    if (!fs.statSync(executablePath).isFile()) {
      return { ok: false, reason: '应用主程序不是普通文件。' };
    }
    executable = fs.realpathSync(executablePath);
  } catch {
    return {
      ok: false,
      reason: `应用缺少可执行的 Contents/MacOS/${executableName} 主程序。`,
    };
  }

  if (!isPathInside(executableRoot, executable)) {
    return { ok: false, reason: '应用主程序指向了应用目录之外。' };
  }
  return {
    ok: true,
    appPath: resolvedApp,
    executable,
    appKind,
  };
}

function isPathInside(rootPath, candidatePath) {
  const relativePath = path.relative(rootPath, candidatePath);
  return Boolean(relativePath) && !path.isAbsolute(relativePath) &&
    relativePath !== '..' && !relativePath.startsWith(`..${path.sep}`);
}

function readApplicationInfoPlist(infoPlistPath) {
  const result = spawnSync('/usr/bin/plutil', ['-convert', 'json', '-o', '-', infoPlistPath], {
    encoding: 'utf8',
    timeout: 5_000,
    windowsHide: true,
    maxBuffer: 1_048_576,
  });
  if (result.error || result.status !== 0) {
    throw new Error('plutil 无法读取应用信息。');
  }
  const metadata = JSON.parse(String(result.stdout || ''));
  if (!metadata || typeof metadata !== 'object' || Array.isArray(metadata)) {
    throw new Error('Info.plist 内容无效。');
  }
  return metadata;
}

function readApplicationSignature(appPath) {
  const verification = spawnSync('/usr/bin/codesign', [
    '--verify',
    '--deep',
    '--strict',
    '--verbose=2',
    appPath,
  ], {
    encoding: 'utf8',
    timeout: 15_000,
    windowsHide: true,
    maxBuffer: 1_048_576,
  });
  if (verification.error || verification.status !== 0) {
    throw new Error('codesign 严格校验失败。');
  }

  const details = spawnSync('/usr/bin/codesign', ['-d', '--verbose=4', appPath], {
    encoding: 'utf8',
    timeout: 10_000,
    windowsHide: true,
    maxBuffer: 1_048_576,
  });
  if (details.error || details.status !== 0) throw new Error('无法读取应用签名身份。');
  const output = `${details.stdout || ''}\n${details.stderr || ''}`;
  const identifier = /^Identifier=(.+)$/m.exec(output)?.[1]?.trim() || '';
  const teamIdentifier = /^TeamIdentifier=(.+)$/m.exec(output)?.[1]?.trim() || '';
  if (!identifier || !teamIdentifier) throw new Error('应用签名身份不完整。');
  return { identifier, teamIdentifier };
}

function waitForDesktopProcessStart(child, timeoutMs) {
  if (!child || typeof child.once !== 'function') return Promise.resolve({ handedOff: false });
  return new Promise((resolve, reject) => {
    let timer = null;
    let settled = false;
    const cleanup = () => {
      if (timer) clearTimeout(timer);
      child.removeListener?.('spawn', onSpawn);
      child.removeListener?.('error', onError);
      child.removeListener?.('exit', onExit);
    };
    const fail = (message, cause = null) => {
      if (settled) return;
      settled = true;
      cleanup();
      const error = new Error(message);
      if (cause) error.cause = cause;
      reject(error);
    };
    const onError = (error) => fail(
      `无法启动 ChatGPT（Codex）桌面进程：${String(error?.message || error || '未知错误')}`,
      error,
    );
    const onExit = (code, signal) => {
      if (code === 0 && !signal) {
        if (settled) return;
        settled = true;
        cleanup();
        child.on?.('error', () => {});
        resolve({ handedOff: true });
        return;
      }
      fail(
        `ChatGPT（Codex）桌面进程启动后立即退出（${signal || `代码 ${code ?? '未知'}`}）。请重新安装官方桌面应用，并检查 ~/Library/Logs/com.openai.codex。`,
      );
    };
    const onSpawn = () => {
      timer = setTimeout(() => {
        if (settled) return;
        settled = true;
        cleanup();
        child.on?.('error', () => {});
        resolve({ handedOff: false });
      }, timeoutMs);
    };
    child.once('spawn', onSpawn);
    child.once('error', onError);
    child.once('exit', onExit);
  });
}

function waitForTerminalOpen(child, timeoutMs) {
  if (!child || typeof child.once !== 'function') {
    return Promise.reject(new Error('无法请求 Terminal 启动 Codex：启动进程无效。'));
  }
  return new Promise((resolve, reject) => {
    let settled = false;
    const cleanup = () => {
      clearTimeout(timer);
      child.removeListener?.('error', onError);
      child.removeListener?.('close', onClose);
    };
    const fail = (message, cause = null) => {
      if (settled) return;
      settled = true;
      cleanup();
      const error = new Error(message);
      if (cause) error.cause = cause;
      reject(error);
    };
    const onError = (error) => fail(
      `无法请求 Terminal 启动 Codex：${String(error?.message || error || '未知错误')}`,
      error,
    );
    const onClose = (code, signal) => {
      if (code === 0 && !signal) {
        if (settled) return;
        settled = true;
        cleanup();
        resolve();
        return;
      }
      fail(`Terminal 启动命令失败（${signal || `代码 ${code ?? '未知'}`}）。`);
    };
    const timer = setTimeout(() => {
      if (settled) return;
      child.on?.('error', () => {});
      try {
        child.kill?.('SIGKILL');
      } catch {
        // The timeout remains the actionable launch error.
      }
      fail('等待 Terminal 接受 Codex 启动请求超时。');
    }, timeoutMs);
    timer.unref?.();
    child.once('error', onError);
    child.once('close', onClose);
  });
}

function isOfficialOAuthAuth(authPath) {
  try {
    const auth = JSON.parse(fs.readFileSync(authPath, 'utf8'));
    const tokens = auth && typeof auth === 'object' ? auth.tokens : null;
    const validMode = auth.auth_mode == null || String(auth.auth_mode).toLowerCase() === 'chatgpt';
    return validMode && tokens && typeof tokens === 'object' &&
      ['id_token', 'access_token', 'refresh_token'].every((key) =>
        typeof tokens[key] === 'string' && tokens[key].trim().length > 0);
  } catch {
    return false;
  }
}

function redactSecrets(value) {
  return String(value || '')
    .replace(/\bat-[A-Za-z0-9._~-]{12,}\b/g, '[REDACTED]')
    .replace(/\bsk-[A-Za-z0-9_-]{12,}\b/g, '[REDACTED]')
    .replace(/\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g, '[REDACTED]')
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{12,}\b/gi, 'Bearer [REDACTED]')
    .replace(/([?&](?:code|state)=)[^&\s]+/gi, '$1[REDACTED]')
    .replace(/("authUrl"\s*:\s*")[^"]+/gi, '$1[REDACTED]');
}

function buildChildEnvironment(codexHome, proxySettings = {}) {
  const childEnvironment = { ...process.env };
  for (const key of Object.keys(childEnvironment)) {
    if (key === 'CODEX_HOME' || /^(?:OPENAI|CODEX|AZURE_OPENAI).*(?:KEY|TOKEN|SECRET|PASSWORD)$/i.test(key)) {
      delete childEnvironment[key];
    }
  }
  childEnvironment.CODEX_HOME = codexHome;
  childEnvironment.CODEX_SQLITE_HOME = codexHome;
  return applyProxyEnvironment(childEnvironment, proxySettings);
}

function validateOfficialAuthUrl(value) {
  let authUrl;
  try {
    authUrl = new URL(String(value || ''));
  } catch {
    throw new Error('官方登录服务没有返回有效的 HTTPS 登录链接。');
  }
  const allowedHost = authUrl.hostname === 'auth.openai.com' ||
    (authUrl.hostname === 'chatgpt.com' && authUrl.pathname === '/codex/desktop-auth');
  if (authUrl.protocol !== 'https:' || !allowedHost ||
      (authUrl.port && authUrl.port !== '443') || authUrl.username || authUrl.password ||
      authUrl.toString().length > 8192) {
    throw new Error('官方登录服务返回了不受信任的登录链接。');
  }
  return authUrl.toString();
}

function waitForOfficialOAuthAuth(authPath, timeoutMs = 5_000) {
  const startedAt = Date.now();
  return new Promise((resolve, reject) => {
    const check = () => {
      if (isOfficialOAuthAuth(authPath)) {
        resolve();
        return;
      }
      if (Date.now() - startedAt >= timeoutMs) {
        reject(new Error('官方登录完成后没有在独立目录生成可用凭据。'));
        return;
      }
      setTimeout(check, 50).unref();
    };
    check();
  });
}

class AppServerOAuthSession {
  constructor({
    account,
    executable,
    env,
    spawnProcess = spawn,
    timeoutMs = 600_000,
    initializeTimeoutMs = 20_000,
    loginStartTimeoutMs = 30_000,
    childStopTimeoutMs = 2_000,
    childKillTimeoutMs = 1_000,
  }) {
    this.account = account;
    this.executable = executable;
    this.env = env;
    this.spawnProcess = spawnProcess;
    this.timeoutMs = timeoutMs;
    this.initializeTimeoutMs = Math.max(50, Number(initializeTimeoutMs) || 20_000);
    this.loginStartTimeoutMs = Math.max(50, Number(loginStartTimeoutMs) || 30_000);
    this.childStopTimeoutMs = Math.max(10, Number(childStopTimeoutMs) || 2_000);
    this.childKillTimeoutMs = Math.max(10, Number(childKillTimeoutMs) || 1_000);
    this.child = null;
    this.reader = null;
    this.loginId = '';
    this.earlyLoginCompletions = [];
    this.completionReceived = false;
    this.nextRequestId = 0;
    this.pendingRequests = new Map();
    this.finished = false;
    this.cancelled = false;
    this.timeout = null;
    this.childClosed = false;
    this.childClosePromise = null;
    this.resolveChildClose = null;
    this.childDrainPromise = null;
    this.failureError = null;
    this.successResult = null;
    this.completedSettled = false;
  }

  start() {
    if (this.child) throw new Error('官方登录会话已经启动。');
    let resolveReady;
    let rejectReady;
    let resolveCompleted;
    let rejectCompleted;
    const ready = new Promise((resolve, reject) => { resolveReady = resolve; rejectReady = reject; });
    const completed = new Promise((resolve, reject) => { resolveCompleted = resolve; rejectCompleted = reject; });
    this.resolveReady = resolveReady;
    this.rejectReady = rejectReady;
    this.resolveCompleted = resolveCompleted;
    this.rejectCompleted = rejectCompleted;

    this.child = this.spawnProcess(this.executable, ['app-server', '--stdio', '--disable', 'plugins'], {
      cwd: os.tmpdir(),
      env: this.env,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });
    this.childClosePromise = new Promise((resolve) => { this.resolveChildClose = resolve; });
    this.reader = readline.createInterface({ input: this.child.stdout, crlfDelay: Infinity });
    this.reader.on('line', (line) => this.handleLine(line));
    this.child.stderr.on('data', () => { /* diagnostics may contain authUrl; never retain or log them */ });
    this.child.on('error', () => {
      this.fail(new Error('无法启动 Codex 官方登录服务。'));
    });
    this.child.on('close', () => {
      this.childClosed = true;
      this.resolveChildClose?.();
      if (!this.finished) {
        this.fail(new Error(this.cancelled ? '通过 ChatGPT 登录已取消。' : 'Codex 官方登录服务提前退出。'));
      } else {
        this.settleCompletionAfterStop();
      }
    });
    this.timeout = setTimeout(() => {
      this.fail(new Error('等待 ChatGPT 官方网页登录超时。'));
    }, this.timeoutMs);
    this.timeout.unref();

    this.bootstrap().catch((error) => {
      this.fail(error instanceof Error ? error : new Error('无法初始化 Codex 官方登录服务。'));
    });
    return {
      ready,
      completed,
      cancel: () => this.cancel(),
    };
  }

  async bootstrap() {
    await this.request('initialize', {
      clientInfo: {
        name: 'codex-account-manager',
        title: 'Codex Account Manager',
        version: '1.0.0',
      },
      capabilities: { experimentalApi: true },
    }, this.initializeTimeoutMs);
    this.notify('initialized');
    const result = await this.request('account/login/start', { type: 'chatgpt' }, this.loginStartTimeoutMs);
    if (result?.type !== 'chatgpt') {
      throw new Error('Codex 官方登录服务返回了错误的登录类型。');
    }
    const loginId = String(result?.loginId || '');
    if (!/^[A-Za-z0-9._:-]{1,256}$/.test(loginId)) {
      throw new Error('Codex 官方登录服务没有返回有效的登录会话标识。');
    }
    const authUrl = validateOfficialAuthUrl(result?.authUrl);
    this.loginId = loginId;
    this.resolveReady({ loginId, authUrl });
    const earlyCompletion = this.earlyLoginCompletions.find((params) =>
      String(params?.loginId || '') === loginId);
    this.earlyLoginCompletions = [];
    if (earlyCompletion) this.handleLoginCompleted(earlyCompletion);
  }

  request(method, params, timeoutMs = this.loginStartTimeoutMs) {
    const id = ++this.nextRequestId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(id);
        reject(new Error(`Codex 官方登录请求超时（${method}）。`));
      }, Math.max(50, Number(timeoutMs) || this.loginStartTimeoutMs));
      timer.unref?.();
      this.pendingRequests.set(id, { resolve, reject, timer });
      try {
        this.write({ id, method, ...(params == null ? {} : { params }) });
      } catch (error) {
        this.pendingRequests.delete(id);
        clearTimeout(timer);
        reject(error);
      }
    });
  }

  notify(method, params = null) {
    this.write({ method, ...(params == null ? {} : { params }) });
  }

  write(message) {
    if (!this.child || this.child.exitCode !== null || this.child.stdin.destroyed) {
      throw new Error('Codex 官方登录服务不可用。');
    }
    this.child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  handleLine(line) {
    let message;
    try { message = JSON.parse(String(line || '')); } catch { return; }
    if (!message || typeof message !== 'object') return;
    if (Number.isInteger(message.id) && this.pendingRequests.has(message.id)) {
      const pending = this.pendingRequests.get(message.id);
      this.pendingRequests.delete(message.id);
      clearTimeout(pending.timer);
      if (message.error) pending.reject(new Error('Codex 官方登录请求失败。'));
      else pending.resolve(message.result || {});
      return;
    }
    if (message.method !== 'account/login/completed' || !message.params || this.finished) return;
    if (!this.loginId) {
      if (this.earlyLoginCompletions.length < 8) this.earlyLoginCompletions.push(message.params);
      return;
    }
    this.handleLoginCompleted(message.params);
  }

  handleLoginCompleted(params) {
    if (String(params?.loginId || '') !== this.loginId || this.finished || this.completionReceived) return;
    this.completionReceived = true;
    if (params.success !== true) {
      this.fail(new Error('ChatGPT 官方网页登录未完成。'));
      return;
    }
    waitForOfficialOAuthAuth(path.join(this.account.codexHome, 'auth.json'))
      .then(() => this.succeed())
      .catch((error) => {
        this.fail(error);
      });
  }

  async succeed() {
    if (this.finished) return;
    this.finished = true;
    this.cleanupProtocolState();
    this.successResult = { ok: true };
    try {
      await this.stopChild();
    } catch {
      // Keep completed pending until cancel/retry confirms that the child has exited.
    }
  }

  fail(error) {
    if (this.finished) return;
    this.finished = true;
    this.failureError = error;
    this.cleanupProtocolState(error);
    this.rejectReady(error);
    this.stopChild().catch(() => {
      // Keep completed pending until a later retry confirms that the child has exited.
    });
  }

  cleanupProtocolState(error = new Error('Codex 官方登录会话已结束。')) {
    if (this.timeout) clearTimeout(this.timeout);
    this.timeout = null;
    this.earlyLoginCompletions = [];
    for (const pending of this.pendingRequests.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pendingRequests.clear();
  }

  async cancel() {
    if (this.finished) {
      await this.stopChild();
      return false;
    }
    this.cancelled = true;
    if (this.loginId) {
      try {
        await Promise.race([
          this.request('account/login/cancel', { loginId: this.loginId }),
          new Promise((resolve) => setTimeout(resolve, 250)),
        ]);
      } catch {
        // The dedicated process is terminated below even if cancel acknowledgement fails.
      }
    }
    const error = new Error('通过 ChatGPT 登录已取消。');
    this.fail(error);
    await this.stopChild();
    return true;
  }

  waitForChildClose(timeoutMs) {
    if (!this.child || this.childClosed) return Promise.resolve(true);
    return new Promise((resolve) => {
      let settled = false;
      const finish = (value) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(value);
      };
      const timer = setTimeout(() => finish(false), timeoutMs);
      this.childClosePromise.then(() => finish(true));
    });
  }

  settleCompletionAfterStop() {
    if (this.completedSettled) return;
    if (this.successResult) {
      this.completedSettled = true;
      this.resolveCompleted(this.successResult);
      return;
    }
    if (!this.failureError) return;
    this.completedSettled = true;
    this.rejectCompleted(this.failureError);
  }

  async stopChild() {
    if (!this.child || this.childClosed) {
      this.settleCompletionAfterStop();
      return true;
    }
    if (this.childDrainPromise) {
      const result = await this.childDrainPromise;
      this.settleCompletionAfterStop();
      return result;
    }
    const child = this.child;
    const drain = (async () => {
      try { this.reader?.close(); } catch { /* ignore */ }
      try { child.stdin.end(); } catch { /* ignore */ }
      if (this.childClosed) return true;
      try { child.kill('SIGTERM'); } catch { /* the close wait below remains authoritative */ }
      if (await this.waitForChildClose(this.childStopTimeoutMs)) return true;
      try { child.kill('SIGKILL'); } catch { /* the close wait below remains authoritative */ }
      if (await this.waitForChildClose(this.childKillTimeoutMs)) return true;
      const error = new Error('无法确认 Codex 官方登录服务已经退出；临时凭据保留，请稍后重试。');
      error.code = 'OAUTH_PROCESS_STOP_FAILED';
      throw error;
    })();
    this.childDrainPromise = drain;
    try {
      const result = await drain;
      this.settleCompletionAfterStop();
      return result;
    } catch (error) {
      if (this.childDrainPromise === drain) this.childDrainPromise = null;
      throw error;
    }
  }
}

module.exports = {
  AppServerOAuthSession,
  CodexCliService,
  buildChildEnvironment,
  isOfficialOAuthAuth,
  redactSecrets,
  readDesktopProcessList,
  readTerminalProcessList,
  validateOfficialAuthUrl,
  waitForOfficialOAuthAuth,
};
