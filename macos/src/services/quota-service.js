const fs = require('node:fs');
const fsp = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const readline = require('node:readline');
const { spawn } = require('node:child_process');

const { _test: usageHelpers } = require('./usage-stats');

const DEFAULT_REFRESH_INTERVAL_MS = 30_000;
const DEFAULT_RPC_TIMEOUT_MS = 15_000;
const QUOTA_SNAPSHOT_CACHE_VERSION = 1;
const MAX_QUOTA_SNAPSHOT_CACHE_BYTES = 4_194_304;

function accountIdentity(account, index = 0) {
  const id = String(account?.id || '').trim();
  if (id) return id;
  const codexHome = String(account?.codexHome || '').trim();
  if (codexHome) return `home:${path.resolve(codexHome)}`;
  return `account:${index}:${String(account?.name || '').trim().toLowerCase()}`;
}

function integer(value) {
  if (value === null || value === undefined) return null;
  if (typeof value === 'string' && value.trim() === '') return null;
  const number = Number(value);
  return Number.isInteger(number) ? number : null;
}

function finiteNumber(value) {
  if (value === null || value === undefined) return null;
  if (typeof value === 'string' && value.trim() === '') return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function firstValue(object, names) {
  for (const name of names) {
    if (object?.[name] !== undefined && object?.[name] !== null) return object[name];
  }
  return null;
}

function parseTimestamp(value) {
  if (typeof value === 'number' && Number.isFinite(value)) {
    const milliseconds = Math.abs(value) < 10_000_000_000 ? value * 1_000 : value;
    return Number.isFinite(milliseconds) ? milliseconds : null;
  }
  const parsed = Date.parse(String(value || ''));
  return Number.isFinite(parsed) ? parsed : null;
}

function credentialEpoch(account) {
  const value = String(account?.credentialEpoch || '').trim();
  return /^[A-Za-z0-9-]{16,80}$/.test(value) ? value : null;
}

function credentialActivatedTimestamp(account) {
  return parseTimestamp(account?.credentialActivatedAt);
}

function snapshotIsInActiveCredentialEpoch(account, snapshot) {
  const activatedAt = credentialActivatedTimestamp(account);
  const observedAt = parseTimestamp(snapshot?.observedAt);
  return activatedAt !== null && observedAt !== null && observedAt >= activatedAt;
}

function parseResetTimestamp(value) {
  const timestamp = parseTimestamp(value);
  return timestamp === null ? null : new Date(timestamp).toISOString();
}

function classifyWindow(windowMinutes) {
  const value = integer(windowMinutes);
  if (value === null) return 'unknown';
  if (value >= 240 && value <= 360) return 'fiveHour';
  if (value >= 9_000 && value <= 11_000) return 'weekly';
  if (value >= 40_000 && value <= 47_000) return 'monthly';
  return 'unknown';
}

function parseWindow(rawWindow, observedAt) {
  if (!rawWindow || typeof rawWindow !== 'object') return null;
  const rawUsedPercent = firstValue(rawWindow, ['usedPercent', 'used_percent']);
  const usedPercentValue = finiteNumber(rawUsedPercent);
  const usedPercent = usedPercentValue !== null && usedPercentValue >= 0 && usedPercentValue <= 100
    ? usedPercentValue
    : null;
  const rawWindowMinutes = firstValue(rawWindow, [
    'windowMinutes',
    'windowDurationMins',
    'window_minutes',
  ]);
  const windowMinutesValue = integer(rawWindowMinutes);
  const windowMinutes = windowMinutesValue !== null && windowMinutesValue > 0 ? windowMinutesValue : null;
  const resetsAt = parseResetTimestamp(firstValue(rawWindow, ['resetsAt', 'resets_at']));
  if (usedPercent === null && windowMinutes === null && resetsAt === null) return null;
  return {
    kind: classifyWindow(windowMinutes),
    usedPercent,
    remainingPercent: usedPercent === null ? null : Math.max(0, 100 - usedPercent),
    windowMinutes,
    resetsAt,
    observedAt,
  };
}

function selectCodexRateLimits(result) {
  if (!result || typeof result !== 'object') return null;
  const byLimitId = result.rateLimitsByLimitId || result.rate_limits_by_limit_id;
  if (byLimitId && typeof byLimitId === 'object') {
    if (byLimitId.codex && typeof byLimitId.codex === 'object') return byLimitId.codex;
    for (const [key, candidate] of Object.entries(byLimitId)) {
      if (!candidate || typeof candidate !== 'object') continue;
      const limitId = String(candidate.limitId || candidate.limit_id || '');
      if (key.toLowerCase() === 'codex' || limitId.toLowerCase() === 'codex') return candidate;
    }
  }
  const direct = result.rateLimits || result.rate_limits || result;
  return direct && typeof direct === 'object' ? direct : null;
}

function parseCredits(raw) {
  if (!raw || typeof raw !== 'object') return null;
  const hasCredits = firstValue(raw, ['hasCredits', 'has_credits']);
  const unlimited = firstValue(raw, ['unlimited']);
  const balance = firstValue(raw, ['balance']);
  return {
    hasCredits: typeof hasCredits === 'boolean' ? hasCredits : null,
    unlimited: typeof unlimited === 'boolean' ? unlimited : null,
    balance: balance === null ? null : String(balance),
  };
}

function parseIndividualLimit(raw) {
  if (!raw || typeof raw !== 'object') return null;
  const remaining = finiteNumber(firstValue(raw, ['remainingPercent', 'remaining_percent']));
  return {
    limit: firstValue(raw, ['limit']) === null ? null : String(firstValue(raw, ['limit'])),
    used: firstValue(raw, ['used']) === null ? null : String(firstValue(raw, ['used'])),
    remainingPercent: remaining !== null && remaining >= 0 && remaining <= 100 ? remaining : null,
    resetsAt: parseResetTimestamp(firstValue(raw, ['resetsAt', 'resets_at'])),
  };
}

function parseRateLimits(result, observedAtValue = Date.now()) {
  const observedTimestamp = parseTimestamp(observedAtValue) ?? Date.now();
  const observedAt = new Date(observedTimestamp).toISOString();
  const rateLimits = selectCodexRateLimits(result);
  if (!rateLimits) return null;
  const primary = parseWindow(rateLimits.primary, observedAt);
  const secondary = parseWindow(rateLimits.secondary, observedAt);
  const credits = parseCredits(rateLimits.credits);
  const individualLimit = parseIndividualLimit(rateLimits.individualLimit || rateLimits.individual_limit);
  const planTypeValue = firstValue(rateLimits, ['planType', 'plan_type']);
  const planType = planTypeValue === null || String(planTypeValue).trim() === ''
    ? null
    : String(planTypeValue).trim();
  if (!primary && !secondary && !credits && !individualLimit && !planType) return null;
  return { observedAt, primary, secondary, credits, individualLimit, planType };
}

function safeQuotaErrorMessage(error) {
  const raw = [
    typeof error === 'string' ? error : null,
    error?.code,
    error?.message,
    error?.data?.code,
    error?.data?.message,
  ].filter(Boolean).join(' ').toLowerCase();
  if (/登录凭据已失效|token[_ -]?invalidated|invalid[_ -]?grant|refresh[_ -]?token|token[_ -]?reused|\b401\b|unauthori[sz]ed/.test(raw)) {
    return 'ChatGPT 登录凭据已失效，请在账号中心重新登录后刷新额度。';
  }
  if (/尚未完成 chatgpt 登录|not[_ -]?logged[_ -]?in|login[_ -]?required|authentication[_ -]?required/.test(raw)) {
    return '该账号尚未完成 ChatGPT 登录，请登录后刷新额度。';
  }
  if (/timed?\s*out|timeout|响应超时/.test(raw)) {
    return 'Codex 官方额度接口响应超时，请稍后重试。';
  }
  if (/未找到 codex cli|codex cli.*(?:not found|缺失|损坏)/.test(raw)) {
    return '未找到 Codex CLI，无法实时读取官方额度。';
  }
  return 'Codex 官方额度暂时无法读取，请稍后重试。';
}

function emptyAccountQuota(account, index = 0) {
  const supported = String(account?.authKind || '') !== 'compatible_api';
  return {
    accountId: accountIdentity(account, index),
    accountName: String(account?.name || '').trim(),
    authKind: String(account?.authKind || 'access_token'),
    supported,
    available: false,
    source: 'unavailable',
    observedAt: null,
    planType: null,
    primary: null,
    secondary: null,
    windows: { fiveHour: null, weekly: null, monthly: null },
    fiveHour: null,
    weekly: null,
    monthly: null,
    credits: null,
    individualLimit: null,
    snapshotCount: 0,
    filesScanned: 0,
    error: null,
    cacheWarning: null,
  };
}

function isNewer(candidate, existing) {
  if (!candidate) return false;
  if (!existing) return true;
  return (parseTimestamp(candidate.observedAt) ?? 0) >= (parseTimestamp(existing.observedAt) ?? 0);
}

function applyQuotaSnapshot(target, snapshot, source) {
  if (!snapshot) return target;
  const hadSnapshot = target.available;
  target.available = true;
  target.snapshotCount += 1;
  target.source = hadSnapshot && target.source !== source ? 'hybrid' : source;
  if (!target.observedAt || (parseTimestamp(snapshot.observedAt) ?? 0) >= (parseTimestamp(target.observedAt) ?? 0)) {
    target.observedAt = snapshot.observedAt;
    if (snapshot.planType) target.planType = snapshot.planType;
    if (snapshot.credits) target.credits = snapshot.credits;
    if (snapshot.individualLimit) target.individualLimit = snapshot.individualLimit;
  }
  if (snapshot.primary && isNewer(snapshot.primary, target.primary)) target.primary = snapshot.primary;
  if (snapshot.secondary && isNewer(snapshot.secondary, target.secondary)) target.secondary = snapshot.secondary;
  const historicalWindows = snapshot.windows && typeof snapshot.windows === 'object'
    ? [snapshot.windows.fiveHour, snapshot.windows.weekly, snapshot.windows.monthly]
    : [];
  for (const window of [snapshot.primary, snapshot.secondary, ...historicalWindows]) {
    if (!window || !['fiveHour', 'weekly', 'monthly'].includes(window.kind)) continue;
    if (isNewer(window, target.windows[window.kind])) target.windows[window.kind] = window;
  }
  target.fiveHour = target.windows.fiveHour;
  target.weekly = target.windows.weekly;
  target.monthly = target.windows.monthly;
  return target;
}

async function collectJsonlFiles(root, accountId, output, seen) {
  if (!root) return;
  let stat;
  try { stat = await fsp.stat(root); } catch { return; }
  if (!stat.isDirectory()) return;
  const entries = await fsp.readdir(root, { withFileTypes: true });
  await Promise.all(entries.map(async (entry) => {
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) await collectJsonlFiles(fullPath, accountId, output, seen);
    else if (entry.isFile() && entry.name.endsWith('.jsonl')) {
      let canonical = fullPath;
      try { canonical = await fsp.realpath(fullPath); } catch { /* use original */ }
      if (!seen.has(canonical)) {
        seen.add(canonical);
        output.push({ filePath: fullPath, accountId: accountId || null });
      }
    }
  }));
}

function rootDescriptors(accounts, options, accountRows) {
  if (Array.isArray(options.sessionRoots)) {
    const ids = new Set(accountRows.map((item) => item.accountId));
    return options.sessionRoots.map((entry) => {
      if (typeof entry === 'string') return { root: entry, accountId: null };
      const requestedId = String(entry?.accountId || '').trim();
      return {
        root: String(entry?.root || entry?.path || '').trim(),
        accountId: ids.has(requestedId) ? requestedId : null,
      };
    }).filter((entry) => entry.root);
  }
  const roots = [];
  if (options.includeDefaultRoot !== false) {
    roots.push({ root: path.join(os.homedir(), '.codex', 'sessions'), accountId: null });
  }
  accounts.forEach((account, index) => {
    const codexHome = String(account?.codexHome || '').trim();
    if (codexHome) roots.push({ root: path.join(codexHome, 'sessions'), accountId: accountIdentity(account, index) });
  });
  return roots;
}

function extractRateLimitsRecord(record) {
  const payload = record?.payload || {};
  return payload.rate_limits || payload.rateLimits ||
    payload?.info?.rate_limits || payload?.info?.rateLimits ||
    record?.rate_limits || record?.rateLimits || null;
}

async function scanQuotaSession(file, switches, addSnapshot) {
  const stream = fs.createReadStream(file.filePath, { encoding: 'utf8' });
  const reader = readline.createInterface({ input: stream, crlfDelay: Infinity });
  for await (const line of reader) {
    if (!line.trim()) continue;
    let record;
    try { record = JSON.parse(line); } catch { continue; }
    const rateLimits = extractRateLimitsRecord(record);
    if (!rateLimits) continue;
    const timestamp = parseTimestamp(record.timestamp || record?.payload?.timestamp);
    if (timestamp === null) continue;
    const snapshot = parseRateLimits(rateLimits, timestamp);
    if (!snapshot) continue;
    const accountId = file.accountId || usageHelpers.activeAccountId(timestamp, switches);
    if (accountId) addSnapshot(accountId, snapshot);
  }
}

async function readLocalQuotaSnapshots(accounts, options = {}) {
  const rows = accounts.map((account, index) => emptyAccountQuota(account, index));
  const byId = new Map(rows.map((row) => [row.accountId, row]));
  const accountsById = new Map(accounts.map((account, index) => [accountIdentity(account, index), account]));
  const switches = usageHelpers.normalizeSwitches(options.switches, accounts);
  const files = [];
  const seen = new Set();
  for (const descriptor of rootDescriptors(accounts, options, rows)) {
    await collectJsonlFiles(descriptor.root, descriptor.accountId, files, seen);
  }
  for (const file of files) {
    try {
      await scanQuotaSession(file, switches, (accountId, snapshot) => {
        const row = byId.get(accountId);
        const account = accountsById.get(accountId);
        if (row?.supported && snapshotIsInActiveCredentialEpoch(account, snapshot)) {
          applyQuotaSnapshot(row, snapshot, 'session');
        }
      });
    } catch {
      // A live session can rotate while it is being read.
    }
  }
  for (const row of rows) {
    row.filesScanned = files.filter((file) => !file.accountId || file.accountId === row.accountId).length;
  }
  return rows;
}

function normalizeCachedQuotaSnapshot(raw) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const observedTimestamp = parseTimestamp(raw.observedAt);
  if (observedTimestamp === null) return null;
  const observedAt = new Date(observedTimestamp).toISOString();
  const parsed = parseRateLimits(raw, observedAt) || {
    observedAt,
    primary: null,
    secondary: null,
    credits: null,
    individualLimit: null,
    planType: null,
  };
  const windows = { fiveHour: null, weekly: null, monthly: null };
  for (const kind of Object.keys(windows)) {
    const rawWindow = raw.windows?.[kind];
    const window = parseWindow(rawWindow, parseResetTimestamp(rawWindow?.observedAt) || observedAt);
    if (window?.kind === kind) windows[kind] = window;
  }
  const hasWindow = Object.values(windows).some(Boolean);
  if (!parsed.primary && !parsed.secondary && !parsed.credits && !parsed.individualLimit &&
      !parsed.planType && !hasWindow) return null;
  return { ...parsed, observedAt, windows };
}

function quotaSnapshotAccumulator() {
  return {
    available: false,
    snapshotCount: 0,
    source: 'cache',
    observedAt: null,
    planType: null,
    primary: null,
    secondary: null,
    windows: { fiveHour: null, weekly: null, monthly: null },
    fiveHour: null,
    weekly: null,
    monthly: null,
    credits: null,
    individualLimit: null,
  };
}

function mergeQuotaSnapshots(...snapshots) {
  const aggregate = quotaSnapshotAccumulator();
  for (const snapshot of snapshots) {
    if (snapshot) applyQuotaSnapshot(aggregate, snapshot, 'cache');
  }
  if (!aggregate.available) return null;
  return {
    observedAt: aggregate.observedAt,
    planType: aggregate.planType,
    primary: aggregate.primary,
    secondary: aggregate.secondary,
    windows: aggregate.windows,
    credits: aggregate.credits,
    individualLimit: aggregate.individualLimit,
  };
}

function readQuotaSnapshotCache(filePath) {
  const requestedPath = typeof filePath === 'string' ? filePath.trim() : '';
  if (!requestedPath) return [];
  try {
    const details = fs.lstatSync(requestedPath);
    if (!details.isFile() || details.isSymbolicLink() || details.size > MAX_QUOTA_SNAPSHOT_CACHE_BYTES) return [];
    const cache = JSON.parse(fs.readFileSync(requestedPath, 'utf8'));
    if (!cache || cache.version !== QUOTA_SNAPSHOT_CACHE_VERSION || !Array.isArray(cache.entries)) return [];
    return cache.entries.map((entry) => {
      const accountId = String(entry?.accountId || '').trim();
      const entryEpoch = String(entry?.credentialEpoch || '').trim();
      const snapshot = normalizeCachedQuotaSnapshot(entry?.snapshot);
      if (!accountId || !/^[A-Za-z0-9-]{16,80}$/.test(entryEpoch) || !snapshot) return null;
      return {
        accountId,
        credentialEpoch: entryEpoch,
        savedAt: parseResetTimestamp(entry?.savedAt),
        snapshot,
      };
    }).filter(Boolean);
  } catch {
    return [];
  }
}

function cachedQuotaForAccount(entries, account) {
  const accountId = accountIdentity(account);
  const activeEpoch = credentialEpoch(account);
  if (!activeEpoch || credentialActivatedTimestamp(account) === null) return null;
  const snapshots = entries
    .filter((entry) => entry.accountId === accountId && entry.credentialEpoch === activeEpoch)
    .map((entry) => entry.snapshot)
    .filter((snapshot) => snapshotIsInActiveCredentialEpoch(account, snapshot));
  return mergeQuotaSnapshots(...snapshots);
}

function writeQuotaSnapshotCache(filePath, updates, now = Date.now()) {
  const requestedPath = typeof filePath === 'string' ? filePath.trim() : '';
  if (!requestedPath || !Array.isArray(updates) || updates.length === 0) return new Set();
  if (fs.existsSync(requestedPath) && fs.lstatSync(requestedPath).isSymbolicLink()) {
    throw new Error('额度快照缓存不能是符号链接。');
  }
  const existing = readQuotaSnapshotCache(requestedPath);
  const normalizedUpdates = updates.map(({ account, snapshot }) => {
    const accountId = accountIdentity(account);
    const activeEpoch = credentialEpoch(account);
    const normalizedSnapshot = normalizeCachedQuotaSnapshot(snapshot);
    if (!accountId || !activeEpoch || credentialActivatedTimestamp(account) === null || !normalizedSnapshot) return null;
    const previous = existing.find((entry) =>
      entry.accountId === accountId && entry.credentialEpoch === activeEpoch)?.snapshot || null;
    return {
      accountId,
      credentialEpoch: activeEpoch,
      snapshot: mergeQuotaSnapshots(previous, normalizedSnapshot),
    };
  }).filter((entry) => entry?.snapshot);
  if (normalizedUpdates.length === 0) return new Set();
  const updatedIds = new Set(normalizedUpdates.map((entry) => entry.accountId));
  const entries = existing
    .filter((entry) => !updatedIds.has(entry.accountId))
    .map((entry) => ({
      accountId: entry.accountId,
      credentialEpoch: entry.credentialEpoch,
      savedAt: entry.savedAt,
      snapshot: entry.snapshot,
    }));
  const savedAt = new Date(parseTimestamp(now) ?? Date.now()).toISOString();
  for (const entry of normalizedUpdates) entries.push({ ...entry, savedAt });
  fs.mkdirSync(path.dirname(requestedPath), { recursive: true, mode: 0o700 });
  const tempPath = `${requestedPath}.${process.pid}.${Date.now()}.tmp`;
  try {
    fs.writeFileSync(tempPath, `${JSON.stringify({
      version: QUOTA_SNAPSHOT_CACHE_VERSION,
      entries,
    }, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 });
    fs.renameSync(tempPath, requestedPath);
    try { fs.chmodSync(requestedPath, 0o600); } catch { /* Windows development host */ }
  } finally {
    fs.rmSync(tempPath, { force: true });
  }
  return updatedIds;
}

function buildChildEnvironment(codexHome, baseEnvironment = process.env) {
  const environment = { ...baseEnvironment };
  for (const key of Object.keys(environment)) {
    if (key === 'CODEX_HOME' || /^(?:OPENAI|CODEX|AZURE_OPENAI).*(?:KEY|TOKEN|SECRET|PASSWORD)$/i.test(key)) {
      delete environment[key];
    }
  }
  environment.CODEX_HOME = codexHome;
  environment.CODEX_SQLITE_HOME = codexHome;
  return environment;
}

function isExecutable(candidate) {
  if (!candidate) return false;
  try {
    fs.accessSync(candidate, process.platform === 'win32' ? fs.constants.F_OK : fs.constants.X_OK);
    return true;
  } catch {
    return false;
  }
}

function resolveCodexExecutable(options = {}) {
  if (typeof options.codexPath === 'string' && options.codexPath.trim()) return options.codexPath.trim();
  if (options.codexCli && typeof options.codexCli.getCodexPath === 'function') {
    try { return options.codexCli.getCodexPath(); } catch { return null; }
  }
  const candidates = [
    process.env.CODEX_CLI_PATH,
    '/opt/homebrew/bin/codex',
    '/usr/local/bin/codex',
    path.join(os.homedir(), '.local', 'bin', 'codex'),
  ];
  const pathName = process.platform === 'win32' ? 'codex.exe' : 'codex';
  for (const directory of String(process.env.PATH || '').split(path.delimiter)) {
    if (directory) candidates.push(path.join(directory, pathName));
  }
  return candidates.find(isExecutable) || null;
}

function quotaAbortError() {
  const error = new Error('额度读取已取消。');
  error.code = 'ABORT_ERR';
  return error;
}

function childHasExited(child) {
  return !child ||
    (child.exitCode !== null && child.exitCode !== undefined) ||
    (child.signalCode !== null && child.signalCode !== undefined);
}

function waitForChildExit(child, timeoutMs) {
  if (childHasExited(child)) return Promise.resolve(true);
  return new Promise((resolve) => {
    let settled = false;
    let timer = null;
    const finish = (exited) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      child?.removeListener?.('exit', onExit);
      child?.removeListener?.('close', onExit);
      resolve(exited);
    };
    const onExit = () => finish(true);
    timer = setTimeout(() => finish(childHasExited(child)), timeoutMs);
    timer.unref?.();
    child?.once?.('exit', onExit);
    child?.once?.('close', onExit);
  });
}

async function stopDedicatedProcess(child, reader, options = {}) {
  try { reader?.close(); } catch { /* ignore */ }
  try { child?.stdin?.end(); } catch { /* ignore */ }
  if (childHasExited(child)) return;
  const requestedStopTimeoutMs = finiteNumber(options.processStopTimeoutMs);
  const stopTimeoutMs = requestedStopTimeoutMs === null
    ? 2_000
    : Math.max(10, requestedStopTimeoutMs);
  try { child.kill('SIGTERM'); } catch { /* verify exit below */ }
  if (await waitForChildExit(child, stopTimeoutMs)) return;
  try { child.kill('SIGKILL'); } catch { /* verify exit below */ }
  if (await waitForChildExit(child, stopTimeoutMs)) return;
  const error = new Error('Codex 官方额度后台进程无法安全停止；为保护账号数据，本次操作已中止。');
  error.code = 'QUOTA_PROCESS_STOP_FAILED';
  throw error;
}

async function readRateLimitsViaAppServer(account, options = {}) {
  const signal = options.signal;
  if (signal?.aborted) throw quotaAbortError();
  const executable = resolveCodexExecutable(options);
  if (!executable) throw new Error('未找到 Codex CLI，无法读取官方额度。');
  const spawnProcess = options.spawnProcess || spawn;
  const requestedTimeoutMs = finiteNumber(options.timeoutMs);
  const timeoutMs = requestedTimeoutMs !== null
    ? Math.max(500, requestedTimeoutMs)
    : DEFAULT_RPC_TIMEOUT_MS;
  const child = spawnProcess(executable, ['app-server', '--stdio', '--disable', 'plugins'], {
    cwd: os.tmpdir(),
    env: buildChildEnvironment(account.codexHome, options.environment || process.env),
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });
  const reader = readline.createInterface({ input: child.stdout, crlfDelay: Infinity });
  let nextRequestId = 0;
  let closed = false;
  const pending = new Map();
  const rejectAll = (error) => {
    if (closed) return;
    closed = true;
    for (const request of pending.values()) {
      clearTimeout(request.timer);
      request.reject(error);
    }
    pending.clear();
  };
  reader.on('line', (line) => {
    let message;
    try { message = JSON.parse(String(line || '')); } catch { return; }
    const id = integer(message?.id);
    if (id === null || !pending.has(id)) return;
    const request = pending.get(id);
    pending.delete(id);
    clearTimeout(request.timer);
    if (message.error) request.reject(new Error(safeQuotaErrorMessage(message.error)));
    else if (message.result && typeof message.result === 'object') request.resolve(message.result);
    else request.reject(new Error('Codex 官方额度接口返回格式不完整。'));
  });
  child.stderr?.on?.('data', () => { /* never retain diagnostics that may contain credentials */ });
  child.on?.('error', () => rejectAll(new Error('无法启动 Codex 官方额度服务。')));
  child.on?.('close', () => rejectAll(new Error('Codex 官方额度服务提前退出。')));
  const abortListener = () => rejectAll(quotaAbortError());
  signal?.addEventListener?.('abort', abortListener, { once: true });

  const request = (method, params = null) => new Promise((resolve, reject) => {
    if (closed || !child.stdin || child.stdin.destroyed) {
      reject(new Error('Codex 官方额度服务不可用。'));
      return;
    }
    const id = ++nextRequestId;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error('Codex 官方额度接口响应超时。'));
    }, timeoutMs);
    timer.unref?.();
    pending.set(id, { resolve, reject, timer });
    try {
      child.stdin.write(`${JSON.stringify({ id, method, ...(params === null ? {} : { params }) })}\n`);
    } catch {
      clearTimeout(timer);
      pending.delete(id);
      reject(new Error('无法写入 Codex 官方额度请求。'));
    }
  });

  try {
    await request('initialize', {
      clientInfo: {
        name: 'codex-account-manager',
        title: 'Codex Account Manager',
        version: '1.0.0',
      },
      capabilities: { experimentalApi: true },
    });
    child.stdin.write(`${JSON.stringify({ method: 'initialized' })}\n`);
    const result = await request('account/rateLimits/read');
    const snapshot = parseRateLimits(result, options.now ?? Date.now());
    if (!snapshot) throw new Error('官方额度接口没有返回可用的 Codex 额度窗口。');
    return snapshot;
  } finally {
    signal?.removeEventListener?.('abort', abortListener);
    rejectAll(new Error('Codex 官方额度读取已结束。'));
    await stopDedicatedProcess(child, reader, options);
  }
}

function hasStoredAuth(account) {
  const codexHome = String(account?.codexHome || '').trim();
  return Boolean(codexHome) && fs.existsSync(path.join(codexHome, 'auth.json'));
}

async function runWithConcurrency(items, concurrency, worker) {
  let cursor = 0;
  const runners = Array.from({ length: Math.min(Math.max(1, concurrency), items.length) }, async () => {
    while (cursor < items.length) {
      const index = cursor;
      cursor += 1;
      await worker(items[index], index);
    }
  });
  const results = await Promise.allSettled(runners);
  const failed = results.find((result) => result.status === 'rejected');
  if (failed) throw failed.reason;
}

async function getQuotaStats(accounts, options = {}) {
  const normalizedAccounts = Array.isArray(accounts) ? accounts : [];
  const isCredentialStillActive = typeof options.isCredentialStillActive === 'function'
    ? (account) => {
      try { return options.isCredentialStillActive(account) === true; } catch { return false; }
    }
    : () => true;
  const requestedNow = finiteNumber(options.now);
  const now = requestedNow === null ? Date.now() : requestedNow;
  const rows = await readLocalQuotaSnapshots(normalizedAccounts, { ...options, now });
  const quotaSnapshotsPath = typeof options.quotaSnapshotsPath === 'string'
    ? options.quotaSnapshotsPath.trim()
    : '';
  const cachedEntries = readQuotaSnapshotCache(quotaSnapshotsPath);
  const applyCachedFallback = (account, row) => {
    if (!isCredentialStillActive(account)) return;
    const cached = cachedQuotaForAccount(cachedEntries, account);
    if (cached) applyQuotaSnapshot(row, cached, 'cache');
  };
  const executable = resolveCodexExecutable(options);
  const liveReader = options.readLiveQuota || readRateLimitsViaAppServer;
  const liveRequested = options.live !== false;
  const canReadLive = Boolean(executable || options.readLiveQuota || options.spawnProcess);
  if (liveRequested && canReadLive) {
    const successfulLiveSnapshots = [];
    const work = normalizedAccounts
      .map((account, index) => ({ account, index, row: rows[index] }))
      .filter(({ account, row }) => isCredentialStillActive(account) && row.supported &&
        (options.allowMissingAuth === true || hasStoredAuth(account)));
    const workIds = new Set(work.map(({ row }) => row.accountId));
    for (let index = 0; index < rows.length; index += 1) {
      const row = rows[index];
      if (isCredentialStillActive(normalizedAccounts[index]) && row.supported && !workIds.has(row.accountId)) {
        applyCachedFallback(normalizedAccounts[index], row);
        row.error = row.available
          ? '未检测到可用登录凭据，当前显示最近一次本地额度快照。'
          : '未检测到可用登录凭据，请先登录该账号。';
      }
    }
    await runWithConcurrency(work, Number(options.concurrency) || 2, async ({ account, row }) => {
      try {
        const live = await liveReader(account, { ...options, codexPath: executable, now });
        if (!isCredentialStillActive(account)) return;
        const snapshot = live?.primary || live?.secondary
          ? live
          : parseRateLimits(live, now);
        if (!snapshot) throw new Error('empty quota snapshot');
        applyQuotaSnapshot(row, snapshot, 'app-server');
        row.error = null;
        successfulLiveSnapshots.push({ account, row, snapshot });
      } catch (error) {
        if (error?.code === 'QUOTA_PROCESS_STOP_FAILED') throw error;
        if (!isCredentialStillActive(account)) return;
        applyCachedFallback(account, row);
        const detail = safeQuotaErrorMessage(error);
        row.error = row.available
          ? `${detail} 当前显示最近一次本地额度快照。`
        : detail;
      }
    });
    const activeLiveSnapshots = successfulLiveSnapshots.filter(({ account }) => isCredentialStillActive(account));
    if (quotaSnapshotsPath && activeLiveSnapshots.length > 0) {
      try {
        const writtenIds = writeQuotaSnapshotCache(
          quotaSnapshotsPath,
          activeLiveSnapshots.map(({ account, snapshot }) => ({ account, snapshot })),
          now,
        );
        for (const { row } of activeLiveSnapshots) {
          if (!writtenIds.has(row.accountId)) {
            row.cacheWarning = '实时额度已读取，但凭据版本元数据尚未初始化，本地快照未保存。';
          }
        }
      } catch {
        for (const { row } of activeLiveSnapshots) {
          row.cacheWarning = '实时额度已读取，但本地快照未能保存。';
        }
      }
    }
  } else if (liveRequested) {
    for (let index = 0; index < rows.length; index += 1) {
      const row = rows[index];
      if (!row.supported) continue;
      applyCachedFallback(normalizedAccounts[index], row);
      row.error = row.available
        ? '未找到 Codex CLI，当前显示最近一次本地额度快照。'
        : '未找到 Codex CLI，无法实时读取官方额度。';
    }
  } else {
    for (let index = 0; index < rows.length; index += 1) {
      if (rows[index].supported) applyCachedFallback(normalizedAccounts[index], rows[index]);
    }
  }

  const requestedId = String(options.accountId || 'all');
  const activeRows = rows.filter((_row, index) => isCredentialStillActive(normalizedAccounts[index]));
  const filteredRows = requestedId === 'all'
    ? activeRows
    : activeRows.filter((row) => row.accountId === requestedId);
  return {
    updatedAt: new Date(now).toISOString(),
    refreshAfterMs: DEFAULT_REFRESH_INTERVAL_MS,
    accounts: filteredRows,
  };
}

async function readAccountQuota(account, options = {}) {
  const report = await getQuotaStats([account], { ...options, accountId: 'all' });
  return report.accounts[0] || emptyAccountQuota(account);
}

module.exports = {
  getQuotaStats,
  readAccountQuota,
  readLocalQuotaSnapshots,
  readRateLimitsViaAppServer,
  _test: {
    accountIdentity,
    applyQuotaSnapshot,
    buildChildEnvironment,
    classifyWindow,
    extractRateLimitsRecord,
    parseRateLimits,
    parseWindow,
    readQuotaSnapshotCache,
    resolveCodexExecutable,
    safeQuotaErrorMessage,
    selectCodexRateLimits,
    snapshotIsInActiveCredentialEpoch,
    writeQuotaSnapshotCache,
  },
};
