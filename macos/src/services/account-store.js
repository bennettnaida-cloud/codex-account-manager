const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const {
  configureModelCatalog,
  defaultModel,
  defaultReasoningEffort,
} = require('./model-catalog');

const ACCESS_TOKEN_BASE_URL = 'http://127.0.0.1:8317/backend-api/codex';
const ACCESS_TOKEN_CHATGPT_BASE_URL = 'http://127.0.0.1:8317/backend-api';
const AUTH_KIND_ACCESS_TOKEN = 'access_token';
const AUTH_KIND_COMPATIBLE_API = 'compatible_api';
const AUTH_KIND_OFFICIAL_OAUTH = 'official_oauth';
const DEFAULT_OAUTH_DRAFT_TTL_MS = 15 * 60 * 1000;
const QUOTA_SNAPSHOT_CACHE_VERSION = 1;
const PENDING_OAUTH_DIRECTORY_PATTERN = /^\.pending-oauth-[A-Za-z0-9]{6}$/;
const DELETE_TOMB_DIRECTORY_PATTERN = /^\.delete-tomb-([A-Za-z0-9-]{8,80})-([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;

function tomlString(value) {
  return `"${String(value ?? '').replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

function slugify(value) {
  const slug = String(value ?? '')
    .normalize('NFKC')
    .toLowerCase()
    .replace(/[^a-z0-9\u4e00-\u9fff]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 42);
  return slug || 'account';
}

function atomicWriteJson(filePath, value, mode = 0o600) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true, mode: 0o700 });
  const tempPath = `${filePath}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tempPath, `${JSON.stringify(value, null, 2)}\n`, { encoding: 'utf8', mode });
  fs.renameSync(tempPath, filePath);
  try {
    fs.chmodSync(filePath, mode);
  } catch {
    // Windows development builds do not implement POSIX permissions.
  }
}

function atomicWriteText(filePath, text, mode = 0o600) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true, mode: 0o700 });
  const tempPath = `${filePath}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tempPath, text, { encoding: 'utf8', mode });
  fs.renameSync(tempPath, filePath);
  try {
    fs.chmodSync(filePath, mode);
  } catch {
    // See atomicWriteJson.
  }
}

function buildAccessTokenConfig() {
  const model = defaultModel();
  return `model = "${model}"
review_model = "${model}"
model_reasoning_effort = "${defaultReasoningEffort()}"
chatgpt_base_url = "${ACCESS_TOKEN_CHATGPT_BASE_URL}"
disable_response_storage = true
model_provider = "codex_account_manager"
service_tier = "default"
model_auto_compact_token_limit = 1000000000

approval_policy = "on-request"
sandbox_mode = "workspace-write"

[features]
js_repl = false
remote_compaction_v2 = false
remote_plugin = false

[model_providers.codex_account_manager]
name = "OpenAI Token HTTP"
base_url = "${ACCESS_TOKEN_BASE_URL}"
wire_api = "responses"
requires_openai_auth = true
supports_websockets = false
stream_max_retries = 0
request_max_retries = 1

[plugins."sites@openai-bundled"]
enabled = false
`;
}

function preserveUnmanagedConfigSections(existingText) {
  const lines = String(existingText || '').replace(/\r\n?/g, '\n').split('\n');
  const preserved = [];
  let keepSection = false;
  for (const line of lines) {
    const header = /^\s*\[([^\]]+)\]\s*(?:#.*)?$/.exec(line);
    if (header) {
      const section = header[1].trim().toLowerCase();
      keepSection = section !== 'features' &&
        section !== 'model_providers.codex_account_manager' &&
        section !== 'plugins."sites@openai-bundled"';
    }
    if (keepSection) preserved.push(line);
  }
  return preserved.join('\n').trim();
}

function projectAccessTokenConfig(existingText = '') {
  const unmanaged = preserveUnmanagedConfigSections(existingText);
  return `${buildAccessTokenConfig().trim()}\n${unmanaged ? `\n${unmanaged}\n` : ''}`;
}

function credentialFingerprint(authPath) {
  try {
    const details = fs.lstatSync(authPath);
    if (!details.isFile() || details.isSymbolicLink() || details.size > 2_097_152) return null;
    return crypto.createHash('sha256').update(fs.readFileSync(authPath)).digest('hex');
  } catch {
    return null;
  }
}

function validIsoTimestamp(value) {
  const text = typeof value === 'string' ? value.trim() : '';
  return text && Number.isFinite(Date.parse(text)) ? new Date(Date.parse(text)).toISOString() : null;
}

function optionalFiniteNumber(value) {
  if (value === null || value === undefined) return null;
  if (typeof value === 'string' && value.trim() === '') return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function buildOfficialOAuthConfig() {
  return `cli_auth_credentials_store = "file"
forced_login_method = "chatgpt"

approval_policy = "on-request"
sandbox_mode = "workspace-write"
`;
}

function isOfficialOAuthAuthFile(authPath) {
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

function buildCompatibleApiConfig(account) {
  const providerName = account.apiProviderName || 'OpenAI Compatible';
  const model = account.apiModel || defaultModel();
  const wireApi = 'responses';
  return `model_provider = "codex_account_manager"
model = ${tomlString(model)}
review_model = ${tomlString(model)}
model_reasoning_effort = "high"
disable_response_storage = true
network_access = "enabled"
service_tier = "default"
model_auto_compact_token_limit = 1000000000

approval_policy = "on-request"
sandbox_mode = "workspace-write"

[features]
remote_compaction_v2 = false
remote_plugin = false

[model_providers.codex_account_manager]
name = ${tomlString(providerName)}
base_url = ${tomlString(String(account.apiBaseUrl || '').replace(/\/+$/, ''))}
wire_api = ${tomlString(wireApi)}
requires_openai_auth = false
supports_websockets = false
stream_max_retries = 0
request_max_retries = 1
`;
}

class AccountStore {
  constructor(userDataPath, {
    accountHomesRoot = path.join(os.homedir(), '.codex-accounts'),
    oauthDraftTtlMs = DEFAULT_OAUTH_DRAFT_TTL_MS,
  } = {}) {
    this.userDataPath = userDataPath;
    configureModelCatalog(userDataPath);
    this.accountsPath = path.join(userDataPath, 'accounts.json');
    this.accountsBackupPath = path.join(userDataPath, 'accounts.json.bak');
    this.settingsPath = path.join(userDataPath, 'settings.json');
    this.switchesPath = path.join(userDataPath, 'usage-account-switches.json');
    this.quotaSnapshotsPath = path.join(userDataPath, 'quota-snapshots.json');
    this.accountHomesRoot = path.resolve(accountHomesRoot);
    fs.mkdirSync(this.userDataPath, { recursive: true, mode: 0o700 });
    fs.mkdirSync(this.accountHomesRoot, { recursive: true, mode: 0o700 });
    try {
      if (fs.lstatSync(this.accountHomesRoot).isSymbolicLink()) {
        throw new Error('账号根目录不能是符号链接。');
      }
      fs.chmodSync(this.accountHomesRoot, 0o700);
    } catch (error) {
      if (error?.code !== 'EPERM') throw error;
    }
    this.cleanupStaleOfficialOAuthDrafts({ maxAgeMs: oauthDraftTtlMs });
    this.lastDeleteTombReconciliation = this.reconcileDeleteTombs();
  }

  cleanupStaleOfficialOAuthDrafts({ maxAgeMs = DEFAULT_OAUTH_DRAFT_TTL_MS, now = Date.now() } = {}) {
    const requestedMaxAgeMs = optionalFiniteNumber(maxAgeMs);
    const ttl = requestedMaxAgeMs !== null && requestedMaxAgeMs >= 0
      ? requestedMaxAgeMs
      : DEFAULT_OAUTH_DRAFT_TTL_MS;
    const cleanupNow = optionalFiniteNumber(now) ?? Date.now();
    let removedCount = 0;
    let entries = [];
    try {
      entries = fs.readdirSync(this.accountHomesRoot, { withFileTypes: true });
    } catch {
      return removedCount;
    }
    for (const entry of entries) {
      if (!PENDING_OAUTH_DIRECTORY_PATTERN.test(entry.name)) continue;
      const candidate = path.join(this.accountHomesRoot, entry.name);
      try {
        const details = fs.lstatSync(candidate);
        if (!details.isDirectory() || details.isSymbolicLink()) continue;
        const realRoot = fs.realpathSync(this.accountHomesRoot);
        const realCandidate = fs.realpathSync(candidate);
        const relative = path.relative(realRoot, realCandidate);
        if (!relative || relative.startsWith('..') || path.isAbsolute(relative) || relative.includes(path.sep)) continue;
        const lastChangedAt = Math.max(details.mtimeMs || 0, details.ctimeMs || 0);
        if (cleanupNow - lastChangedAt < ttl) continue;
        fs.rmSync(realCandidate, { recursive: true, force: false });
        removedCount += 1;
      } catch {
        // A stale draft is best-effort cleanup; never follow or broaden an unsafe path.
      }
    }
    return removedCount;
  }

  reconcileDeleteTombs() {
    const report = {
      status: 'reconciled',
      restored: [],
      deleted: [],
      retained: [],
      ignoredCount: 0,
    };
    const tombs = [];
    let entries = [];
    try {
      entries = fs.readdirSync(this.accountHomesRoot, { withFileTypes: true });
    } catch (error) {
      report.status = 'account-root-unreadable';
      report.retained.push({ tombName: null, accountId: null, reason: 'account-root-unreadable', code: error?.code || null });
      return report;
    }
    for (const entry of entries) {
      const match = DELETE_TOMB_DIRECTORY_PATTERN.exec(entry.name);
      if (!match) {
        if (entry.name.startsWith('.delete-tomb-')) report.ignoredCount += 1;
        continue;
      }
      const tombPath = path.join(this.accountHomesRoot, entry.name);
      try {
        const details = fs.lstatSync(tombPath);
        if (!details.isDirectory() || details.isSymbolicLink()) {
          report.ignoredCount += 1;
          continue;
        }
        this.assertDeleteTombHome(tombPath);
        tombs.push({ tombName: entry.name, tombPath, accountId: match[1] });
      } catch {
        report.ignoredCount += 1;
      }
    }
    if (tombs.length === 0) return report;

    let rawAccounts;
    try {
      rawAccounts = JSON.parse(fs.readFileSync(this.accountsPath, 'utf8'));
      if (!Array.isArray(rawAccounts)) throw new TypeError('accounts.json must contain an array');
    } catch (error) {
      report.status = error?.code === 'ENOENT' ? 'manifest-missing' : 'manifest-unreadable';
      for (const tomb of tombs) {
        report.retained.push({
          tombName: tomb.tombName,
          accountId: tomb.accountId,
          reason: report.status,
          code: error?.code || null,
        });
      }
      return report;
    }

    const manifestEntries = new Map();
    for (const raw of rawAccounts) {
      const id = String(raw?.id || '').trim();
      if (!/^[A-Za-z0-9-]{8,80}$/.test(id)) continue;
      const existing = manifestEntries.get(id) || [];
      existing.push(raw);
      manifestEntries.set(id, existing);
    }
    const tombsById = new Map();
    for (const tomb of tombs) {
      const existing = tombsById.get(tomb.accountId) || [];
      existing.push(tomb);
      tombsById.set(tomb.accountId, existing);
    }

    for (const [accountId, matchingTombs] of tombsById.entries()) {
      if (matchingTombs.length !== 1) {
        for (const tomb of matchingTombs) {
          report.retained.push({ tombName: tomb.tombName, accountId, reason: 'duplicate-tombs', code: null });
        }
        continue;
      }
      const tomb = matchingTombs[0];
      const matchingAccounts = manifestEntries.get(accountId) || [];
      if (matchingAccounts.length > 1) {
        report.retained.push({ tombName: tomb.tombName, accountId, reason: 'duplicate-manifest-id', code: null });
        continue;
      }
      if (matchingAccounts.length === 0) {
        try {
          this.finalizeDeletedAccountMetadata(accountId);
          fs.rmSync(this.assertDeleteTombHome(tomb.tombPath), { recursive: true, force: false });
          report.deleted.push({ tombName: tomb.tombName, accountId });
        } catch (error) {
          report.retained.push({
            tombName: tomb.tombName,
            accountId,
            reason: fs.existsSync(tomb.tombPath) ? 'metadata-cleanup-or-delete-failed' : 'delete-failed',
            code: error?.code || null,
          });
        }
        continue;
      }

      const rawHome = String(matchingAccounts[0]?.codexHome || '').trim();
      const expandedHome = rawHome.startsWith('~/') ? path.join(os.homedir(), rawHome.slice(2)) : rawHome;
      const originalHome = expandedHome ? path.resolve(expandedHome) : '';
      if (!originalHome || !this.isManagedCodexHome(originalHome)) {
        report.retained.push({ tombName: tomb.tombName, accountId, reason: 'manifest-home-invalid', code: null });
        continue;
      }
      if (fs.existsSync(originalHome)) {
        report.retained.push({ tombName: tomb.tombName, accountId, reason: 'home-conflict', code: null });
        continue;
      }
      try {
        fs.renameSync(this.assertDeleteTombHome(tomb.tombPath), originalHome);
        report.restored.push({ tombName: tomb.tombName, accountId });
      } catch (error) {
        report.retained.push({
          tombName: tomb.tombName,
          accountId,
          reason: 'restore-failed',
          code: error?.code || null,
        });
      }
    }
    return report;
  }

  readJson(filePath, fallback) {
    try {
      return JSON.parse(fs.readFileSync(filePath, 'utf8'));
    } catch {
      return fallback;
    }
  }

  loadAccounts() {
    let rawText;
    try {
      rawText = fs.readFileSync(this.accountsPath, 'utf8');
    } catch (error) {
      if (error?.code === 'ENOENT') return [];
      throw this.accountManifestReadError(error);
    }
    let raw;
    try {
      raw = JSON.parse(rawText);
    } catch (error) {
      throw this.accountManifestReadError(error);
    }
    if (!Array.isArray(raw)) throw this.accountManifestReadError(new TypeError('accounts.json must contain an array'));
    return raw.map((item) => this.normalizeAccount(item)).filter(Boolean);
  }

  accountManifestReadError(cause) {
    const error = new Error('账号清单 accounts.json 无法读取或已损坏。为防止覆盖，已停止写入；请从 accounts.json.bak 恢复后重试。');
    error.code = 'ACCOUNT_MANIFEST_UNREADABLE';
    if (cause) error.cause = cause;
    return error;
  }

  writeAccountsManifest(accounts) {
    if (!Array.isArray(accounts)) throw new TypeError('账号清单必须是数组。');
    const previousBackup = fs.existsSync(this.accountsBackupPath)
      ? fs.readFileSync(this.accountsBackupPath)
      : null;
    atomicWriteJson(this.accountsBackupPath, accounts);
    try {
      atomicWriteJson(this.accountsPath, accounts);
    } catch (error) {
      try {
        this.restoreFile(this.accountsBackupPath, previousBackup);
      } catch (rollbackError) {
        error.rollbackError = rollbackError;
      }
      throw error;
    }
  }

  clearQuotaSnapshotsForAccount(accountId) {
    const requestedId = String(accountId || '').trim();
    if (!requestedId) return 0;
    let rawText;
    try {
      rawText = fs.readFileSync(this.quotaSnapshotsPath, 'utf8');
    } catch (error) {
      if (error?.code === 'ENOENT') return 0;
      throw error;
    }
    let cache;
    try {
      cache = JSON.parse(rawText);
    } catch {
      fs.rmSync(this.quotaSnapshotsPath, { force: true });
      return 0;
    }
    if (!cache || typeof cache !== 'object' || !Array.isArray(cache.entries)) {
      fs.rmSync(this.quotaSnapshotsPath, { force: true });
      return 0;
    }
    const remaining = cache.entries.filter((entry) => String(entry?.accountId || '') !== requestedId);
    const removedCount = cache.entries.length - remaining.length;
    if (removedCount === 0) return 0;
    if (remaining.length === 0) {
      fs.rmSync(this.quotaSnapshotsPath, { force: true });
    } else {
      atomicWriteJson(this.quotaSnapshotsPath, {
        version: QUOTA_SNAPSHOT_CACHE_VERSION,
        entries: remaining,
      });
    }
    return removedCount;
  }

  finalizeDeletedAccountMetadata(accountId) {
    const requestedId = String(accountId || '').trim();
    if (!requestedId) throw new Error('待完成删除的账号 ID 无效。');
    let switches = [];
    try {
      switches = JSON.parse(fs.readFileSync(this.switchesPath, 'utf8'));
      if (!Array.isArray(switches)) throw new TypeError('usage-account-switches.json must contain an array');
    } catch (error) {
      if (error?.code !== 'ENOENT') throw error;
      switches = [];
    }
    let switchesChanged = false;
    const anonymized = switches.map((entry) => {
      if (!entry || typeof entry !== 'object') return entry;
      const recordedId = String(entry.accountId || entry.accountKey || entry.AccountId || entry.AccountKey || '').trim();
      if (recordedId !== requestedId) return entry;
      switchesChanged = true;
      return {
        timestampUtc: String(
          entry.timestampUtc || entry.switchedAtUtc || entry.switched_at_utc ||
          entry.TimestampUtc || entry.SwitchedAtUtc || entry.timestamp || entry.at || '',
        ),
        accountId: '',
        accountName: '',
        source: 'deleted-account-boundary',
      };
    });
    if (switchesChanged) atomicWriteJson(this.switchesPath, anonymized);
    let rawSettings = null;
    try {
      rawSettings = JSON.parse(fs.readFileSync(this.settingsPath, 'utf8'));
      if (!rawSettings || typeof rawSettings !== 'object' || Array.isArray(rawSettings)) {
        throw new TypeError('settings.json must contain an object');
      }
    } catch (error) {
      if (error?.code !== 'ENOENT') throw error;
      rawSettings = null;
    }
    if (rawSettings?.currentAccountId === requestedId) {
      this.saveSettings({ ...rawSettings, currentAccountId: null });
    }
    const quotaSnapshotsRemoved = this.clearQuotaSnapshotsForAccount(requestedId);
    return { switchesChanged, quotaSnapshotsRemoved };
  }

  migrateAccountMetadata() {
    if (!fs.existsSync(this.accountsPath)) return [];
    let raw;
    try {
      raw = JSON.parse(fs.readFileSync(this.accountsPath, 'utf8'));
    } catch (error) {
      throw this.accountManifestReadError(error);
    }
    if (!Array.isArray(raw)) throw this.accountManifestReadError(new TypeError('accounts.json must contain an array'));
    const accounts = raw.map((item) => this.normalizeAccount(item)).filter(Boolean);
    let changed = accounts.length !== raw.length;
    const now = new Date().toISOString();
    const rotatedAccountIds = new Set();
    for (const account of accounts) {
      const source = raw.find((item) => item && String(item.id || '') === account.id) || {};
      const storedEpochValid = /^[A-Za-z0-9-]{16,80}$/.test(String(source.credentialEpoch || ''));
      const storedActivatedAt = validIsoTimestamp(source.credentialActivatedAt);
      const hasStoredFingerprint = Object.prototype.hasOwnProperty.call(source, 'credentialFingerprint');
      const storedFingerprintValid = source.credentialFingerprint === null ||
        /^[a-f0-9]{64}$/i.test(String(source.credentialFingerprint || ''));
      const storedFingerprint = /^[a-f0-9]{64}$/i.test(String(source.credentialFingerprint || ''))
        ? String(source.credentialFingerprint).toLowerCase()
        : null;
      const currentFingerprint = credentialFingerprint(path.join(account.codexHome, 'auth.json'));
      if (!storedEpochValid || !storedActivatedAt || !hasStoredFingerprint || !storedFingerprintValid ||
          storedFingerprint !== currentFingerprint) {
        account.credentialEpoch = crypto.randomUUID();
        account.credentialActivatedAt = now;
        account.credentialFingerprint = currentFingerprint;
        rotatedAccountIds.add(account.id);
        changed = true;
      }
      if (source.apiWireApi !== account.apiWireApi) {
        changed = true;
      }
    }
    if (changed) this.writeAccountsManifest(accounts);
    for (const accountId of rotatedAccountIds) {
      try { this.clearQuotaSnapshotsForAccount(accountId); } catch { /* epoch isolation remains fail-closed */ }
    }
    this.migrateCompatibleApiConfigs(accounts);
    return accounts;
  }

  syncCredentialState(id, { force = false, activatedAt = new Date().toISOString() } = {}) {
    const accounts = this.loadAccounts();
    const account = accounts.find((item) => item.id === id);
    if (!account) throw new Error('账号不存在。');
    const fingerprint = credentialFingerprint(path.join(account.codexHome, 'auth.json'));
    const hasCompleteEpoch = /^[A-Za-z0-9-]{16,80}$/.test(String(account.credentialEpoch || '')) &&
      Boolean(validIsoTimestamp(account.credentialActivatedAt));
    if (!force && hasCompleteEpoch && fingerprint === account.credentialFingerprint) return account;
    account.credentialEpoch = crypto.randomUUID();
    account.credentialActivatedAt = validIsoTimestamp(activatedAt) || new Date().toISOString();
    account.credentialFingerprint = fingerprint;
    account.updatedAt = new Date().toISOString();
    this.writeAccountsManifest(accounts);
    try { this.clearQuotaSnapshotsForAccount(account.id); } catch { /* the new epoch cannot read old entries */ }
    return account;
  }

  activateCredential(id, { activatedAt = new Date().toISOString() } = {}) {
    return this.syncCredentialState(id, { force: true, activatedAt });
  }

  ensureAccessTokenConfig(account) {
    if (!account || account.authKind !== AUTH_KIND_ACCESS_TOKEN) return false;
    if (!this.isManagedCodexHome(account.codexHome)) {
      throw new Error('Access Token 账号目录不在受管理目录中。');
    }
    fs.mkdirSync(account.codexHome, { recursive: true, mode: 0o700 });
    const realHome = this.ensureSafeManagedAccountHome(account.codexHome);
    const configPath = path.join(realHome, 'config.toml');
    const existing = fs.existsSync(configPath) ? fs.readFileSync(configPath, 'utf8') : '';
    const projected = projectAccessTokenConfig(existing);
    if (existing.replace(/\r\n?/g, '\n') === projected) return false;
    atomicWriteText(configPath, projected);
    return true;
  }

  migrateAccessTokenConfigs(accounts = this.loadAccounts()) {
    let updatedCount = 0;
    for (const account of accounts) {
      if (this.ensureAccessTokenConfig(account)) updatedCount += 1;
    }
    return updatedCount;
  }

  ensureCompatibleApiConfig(account) {
    if (!account || account.authKind !== AUTH_KIND_COMPATIBLE_API) return false;
    if (!this.isManagedCodexHome(account.codexHome)) {
      throw new Error('兼容 API 账号目录不在受管理目录中。');
    }
    fs.mkdirSync(account.codexHome, { recursive: true, mode: 0o700 });
    const realHome = this.ensureSafeManagedAccountHome(account.codexHome);
    const configPath = path.join(realHome, 'config.toml');
    const existing = fs.existsSync(configPath) ? fs.readFileSync(configPath, 'utf8') : '';
    const projected = buildCompatibleApiConfig(account);
    if (existing.replace(/\r\n?/g, '\n') === projected) return false;
    atomicWriteText(configPath, projected);
    return true;
  }

  migrateCompatibleApiConfigs(accounts = this.loadAccounts()) {
    let updatedCount = 0;
    for (const account of accounts) {
      if (this.ensureCompatibleApiConfig(account)) updatedCount += 1;
    }
    return updatedCount;
  }

  loadSettings() {
    const value = this.readJson(this.settingsPath, {});
    const proxyPort = Number(value.proxyPort);
    const detectedProxyPort = Number(value.detectedProxyPort);
    const usageRefreshSeconds = Number(value.usageRefreshSeconds);
    const quotaRefreshSeconds = Number(value.quotaRefreshSeconds);
    return {
      currentAccountId: typeof value.currentAccountId === 'string' ? value.currentAccountId : null,
      theme: ['system', 'light', 'dark'].includes(value.theme) ? value.theme : 'system',
      projectPath: typeof value.projectPath === 'string' && value.projectPath.trim()
        ? path.resolve(value.projectPath.trim())
        : os.homedir(),
      codexAppPath: typeof value.codexAppPath === 'string' && value.codexAppPath.trim()
        ? path.resolve(value.codexAppPath.trim())
        : null,
      proxyAutoDetect: value.proxyAutoDetect !== false,
      proxyScheme: ['http', 'socks5'].includes(value.proxyScheme) ? value.proxyScheme : 'http',
      proxyAddress: typeof value.proxyAddress === 'string' && value.proxyAddress.trim()
        ? value.proxyAddress.trim()
        : '127.0.0.1',
      proxyPort: Number.isInteger(proxyPort) && proxyPort > 0 && proxyPort <= 65535 && proxyPort !== 8317
        ? proxyPort
        : null,
      detectedProxyPort: Number.isInteger(detectedProxyPort) && detectedProxyPort > 0 &&
        detectedProxyPort <= 65535 && detectedProxyPort !== 8317
        ? detectedProxyPort
        : null,
      usageRefreshSeconds: Number.isInteger(usageRefreshSeconds) && usageRefreshSeconds >= 2 && usageRefreshSeconds <= 60
        ? usageRefreshSeconds
        : 5,
      quotaRefreshSeconds: Number.isInteger(quotaRefreshSeconds) && quotaRefreshSeconds >= 5 && quotaRefreshSeconds <= 300
        ? quotaRefreshSeconds
        : 15,
      codexThemeId: typeof value.codexThemeId === 'string' && value.codexThemeId.trim()
        ? value.codexThemeId.trim()
        : 'official-default',
      customCodexTheme: value.customCodexTheme && typeof value.customCodexTheme === 'object' &&
        !Array.isArray(value.customCodexTheme)
        ? value.customCodexTheme
        : null,
    };
  }

  saveSettings(settings) {
    atomicWriteJson(this.settingsPath, settings);
  }

  normalizeAccount(value, { imported = false } = {}) {
    if (!value || typeof value !== 'object') return null;
    const name = String(value.name ?? '').trim();
    if (!name) return null;
    const id = /^[a-zA-Z0-9-]{8,80}$/.test(String(value.id ?? ''))
      ? String(value.id)
      : crypto.randomUUID();
    const requestedAuthKind = String(value.authKind || '');
    const authKind = requestedAuthKind === AUTH_KIND_COMPATIBLE_API || requestedAuthKind === AUTH_KIND_OFFICIAL_OAUTH
      ? requestedAuthKind
      : AUTH_KIND_ACCESS_TOKEN;
    let codexHome = String(value.codexHome ?? '').trim();
    if (codexHome.startsWith('~/')) codexHome = path.join(os.homedir(), codexHome.slice(2));
    if (!codexHome || imported || !this.isManagedCodexHome(codexHome)) {
      codexHome = this.uniqueCodexHome(name, id);
    }
    return {
      id,
      name,
      codexHome: path.resolve(codexHome),
      authKind,
      apiProviderName: String(value.apiProviderName || 'OpenAI'),
      apiBaseUrl: String(value.apiBaseUrl || ''),
      apiModel: String(value.apiModel || defaultModel()),
      apiWireApi: 'responses',
      createdAt: value.createdAt || new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      lastUsedAt: typeof value.lastUsedAt === 'string' ? value.lastUsedAt : null,
      quotaLimitType: typeof value.quotaLimitType === 'string' ? value.quotaLimitType : 'unknown',
      quotaPrimaryWindowMinutes: optionalFiniteNumber(value.quotaPrimaryWindowMinutes),
      quotaSecondaryWindowMinutes: optionalFiniteNumber(value.quotaSecondaryWindowMinutes),
      credentialEpoch: /^[A-Za-z0-9-]{16,80}$/.test(String(value.credentialEpoch || ''))
        ? String(value.credentialEpoch)
        : crypto.randomUUID(),
      credentialActivatedAt: validIsoTimestamp(value.credentialActivatedAt),
      credentialFingerprint: /^[a-f0-9]{64}$/i.test(String(value.credentialFingerprint || ''))
        ? String(value.credentialFingerprint).toLowerCase()
        : null,
    };
  }

  uniqueCodexHome(name, id) {
    return path.join(this.accountHomesRoot, `${slugify(name)}-${String(id).slice(0, 8)}`);
  }

  isManagedCodexHome(candidate) {
    const root = path.resolve(this.accountHomesRoot);
    const target = path.resolve(String(candidate || ''));
    const relative = path.relative(root, target);
    return Boolean(relative) && !relative.startsWith('..') && !path.isAbsolute(relative) && !relative.includes(path.sep);
  }

  validateApiUrl(rawValue) {
    let url;
    try {
      url = new URL(String(rawValue || '').trim());
    } catch {
      throw new Error('兼容 API 地址格式无效。');
    }
    const localHosts = new Set(['localhost', '127.0.0.1', '::1', '[::1]']);
    if (url.protocol !== 'https:' && !(url.protocol === 'http:' && localHosts.has(url.hostname))) {
      throw new Error('兼容 API 必须使用 HTTPS；只有本机 localhost 可使用 HTTP。');
    }
    if (url.username || url.password || url.search || url.hash) {
      throw new Error('兼容 API 地址不能包含用户名、密码、查询参数或片段。');
    }
    return url.toString().replace(/\/$/, '');
  }

  validateAccount(account, accounts, editingId = null) {
    if (!account.name) throw new Error('账号名称不能为空。');
    if (account.name.length > 80) throw new Error('账号名称不能超过 80 个字符。');
    if (account.authKind === 'compatible_api') {
      account.apiBaseUrl = this.validateApiUrl(account.apiBaseUrl);
    }
    if (accounts.some((item) => item.id !== editingId && item.name.localeCompare(account.name, undefined, { sensitivity: 'accent' }) === 0)) {
      throw new Error(`账号“${account.name}”已存在。`);
    }
    if (accounts.some((item) => item.id !== editingId && path.resolve(item.codexHome) === path.resolve(account.codexHome))) {
      throw new Error('每个账号必须使用独立的 CODEX_HOME。');
    }
    if (path.resolve(account.codexHome) === path.resolve(path.join(os.homedir(), '.codex'))) {
      throw new Error('账号目录不能直接使用共享的 ~/.codex。');
    }
    if (!this.isManagedCodexHome(account.codexHome)) {
      throw new Error('账号目录必须位于受管理的 ~/.codex-accounts 目录中。');
    }
  }

  createAccountCandidate(input, editingId = null) {
    const accounts = this.loadAccounts();
    const existing = editingId ? accounts.find((item) => item.id === editingId) : null;
    if (editingId && !existing) throw new Error('要编辑的账号不存在。');
    const candidate = this.normalizeAccount({
      ...existing,
      ...input,
      id: existing?.id || crypto.randomUUID(),
      codexHome: existing?.codexHome || '',
      createdAt: existing?.createdAt || new Date().toISOString(),
    });
    if (!candidate) throw new Error('账号信息不完整。');
    if (!existing) candidate.codexHome = this.uniqueCodexHome(candidate.name, candidate.id);
    this.validateAccount(candidate, accounts, editingId);
    return { accounts, existing, candidate };
  }

  canReuseOfficialOAuth(input, editingId) {
    if (!editingId) return false;
    const { existing, candidate } = this.createAccountCandidate(input, editingId);
    return existing?.authKind === AUTH_KIND_OFFICIAL_OAUTH &&
      candidate.authKind === AUTH_KIND_OFFICIAL_OAUTH &&
      path.resolve(candidate.codexHome) === path.resolve(existing.codexHome) &&
      isOfficialOAuthAuthFile(path.join(existing.codexHome, 'auth.json'));
  }

  prepareOfficialOAuthDraft(input, editingId = null) {
    const { existing, candidate } = this.createAccountCandidate(input, editingId);
    if (candidate.authKind !== AUTH_KIND_OFFICIAL_OAUTH) {
      throw new Error('只有“通过 ChatGPT 登录（官方）”账号可以创建登录草稿。');
    }
    const pendingCodexHome = fs.mkdtempSync(path.join(this.accountHomesRoot, '.pending-oauth-'));
    try {
      fs.chmodSync(pendingCodexHome, 0o700);
      this.assertPendingOAuthHome(pendingCodexHome);
      atomicWriteText(path.join(pendingCodexHome, 'config.toml'), buildOfficialOAuthConfig());
      return {
        editingId: existing?.id || null,
        candidate,
        pendingCodexHome,
      };
    } catch (error) {
      this.cleanupOfficialOAuthDraft(pendingCodexHome);
      throw error;
    }
  }

  commitOfficialOAuthDraft(draft, input) {
    if (!draft || typeof draft !== 'object') throw new Error('官方登录草稿不存在。');
    const pendingCodexHome = this.assertPendingOAuthHome(draft.pendingCodexHome);
    if (!isOfficialOAuthAuthFile(path.join(pendingCodexHome, 'auth.json'))) {
      throw new Error('官方登录尚未生成可用凭据，不能保存账号。');
    }

    const accounts = this.loadAccounts();
    const existing = draft.editingId ? accounts.find((item) => item.id === draft.editingId) : null;
    if (draft.editingId && !existing) throw new Error('要编辑的账号不存在。');
    const candidate = this.normalizeAccount({
      ...draft.candidate,
      ...input,
      id: draft.candidate.id,
      codexHome: draft.candidate.codexHome,
      authKind: AUTH_KIND_OFFICIAL_OAUTH,
      createdAt: draft.candidate.createdAt,
    });
    if (!candidate) throw new Error('账号信息不完整。');
    candidate.codexHome = path.resolve(draft.candidate.codexHome);
    this.validateAccount(candidate, accounts, draft.editingId);

    const sanitized = { ...candidate };
    delete sanitized.apiKey;
    const next = existing
      ? accounts.map((item) => (item.id === draft.editingId ? sanitized : item))
      : [...accounts, sanitized];
    next.sort((a, b) => a.name.localeCompare(b.name, 'zh-CN'));

    if (!existing) {
      if (fs.existsSync(candidate.codexHome)) {
        throw new Error('目标账号目录已经存在，拒绝覆盖。');
      }
      fs.renameSync(pendingCodexHome, candidate.codexHome);
      try {
        this.writeAccountsManifest(next);
      } catch (error) {
        try { fs.renameSync(candidate.codexHome, pendingCodexHome); } catch { /* retain recoverable credentials */ }
        throw error;
      }
      return sanitized;
    }

    this.ensureSafeManagedAccountHome(existing.codexHome);
    const targetAuthPath = path.join(existing.codexHome, 'auth.json');
    const targetConfigPath = path.join(existing.codexHome, 'config.toml');
    const oldAuth = fs.existsSync(targetAuthPath) ? fs.readFileSync(targetAuthPath) : null;
    const oldConfig = fs.existsSync(targetConfigPath) ? fs.readFileSync(targetConfigPath) : null;
    try {
      atomicWriteText(targetAuthPath, fs.readFileSync(path.join(pendingCodexHome, 'auth.json'), 'utf8'));
      atomicWriteText(targetConfigPath, fs.readFileSync(path.join(pendingCodexHome, 'config.toml'), 'utf8'));
      this.writeAccountsManifest(next);
    } catch (error) {
      this.restoreFile(targetAuthPath, oldAuth);
      this.restoreFile(targetConfigPath, oldConfig);
      throw error;
    }
    this.cleanupOfficialOAuthDraft(pendingCodexHome);
    return sanitized;
  }

  assertPendingOAuthHome(candidate) {
    const target = this.ensureSafeManagedAccountHome(candidate);
    if (!PENDING_OAUTH_DIRECTORY_PATTERN.test(path.basename(target))) {
      throw new Error('官方登录临时目录格式无效。');
    }
    return target;
  }

  ensureSafeManagedAccountHome(candidate) {
    if (!this.isManagedCodexHome(candidate)) throw new Error('账号目录不在受管理目录中。');
    const requested = path.resolve(String(candidate || ''));
    const details = fs.lstatSync(requested);
    if (!details.isDirectory() || details.isSymbolicLink()) throw new Error('账号目录不是安全的真实目录。');
    const root = fs.realpathSync(this.accountHomesRoot);
    const target = fs.realpathSync(requested);
    const relative = path.relative(root, target);
    if (!relative || relative.startsWith('..') || path.isAbsolute(relative) || relative.includes(path.sep)) {
      throw new Error('账号目录解析到了受管理目录之外。');
    }
    return target;
  }

  assertDeleteTombHome(candidate) {
    const target = this.ensureSafeManagedAccountHome(candidate);
    if (!DELETE_TOMB_DIRECTORY_PATTERN.test(path.basename(target))) {
      throw new Error('账号删除临时目录格式无效。');
    }
    return target;
  }

  cleanupOfficialOAuthDraft(candidate) {
    if (!candidate || !fs.existsSync(candidate)) return;
    try {
      const target = this.assertPendingOAuthHome(candidate);
      fs.rmSync(target, { recursive: true, force: true });
    } catch {
      // Never broaden a cleanup if path validation fails.
    }
  }

  restoreFile(filePath, content) {
    if (content == null) {
      fs.rmSync(filePath, { force: true });
      return;
    }
    const tempPath = `${filePath}.${process.pid}.${Date.now()}.restore`;
    fs.writeFileSync(tempPath, content, { mode: 0o600 });
    fs.renameSync(tempPath, filePath);
  }

  ensureAccountHome(account, apiKey, { resetStoredAuth = false } = {}) {
    if (!this.isManagedCodexHome(account.codexHome)) {
      throw new Error('拒绝写入受管理目录以外的路径。');
    }
    if (fs.existsSync(account.codexHome) && fs.lstatSync(account.codexHome).isSymbolicLink()) {
      throw new Error('账号目录不能是符号链接。');
    }
    fs.mkdirSync(account.codexHome, { recursive: true, mode: 0o700 });
    fs.chmodSync(account.codexHome, 0o700);
    const realRoot = fs.realpathSync(this.accountHomesRoot);
    const realHome = fs.realpathSync(account.codexHome);
    const relative = path.relative(realRoot, realHome);
    if (!relative || relative.startsWith('..') || path.isAbsolute(relative) || relative.includes(path.sep)) {
      throw new Error('账号目录解析到了受管理目录之外。');
    }
    const configPath = path.join(account.codexHome, 'config.toml');
    const authPath = path.join(account.codexHome, 'auth.json');
    if (resetStoredAuth) fs.rmSync(authPath, { force: true });
    if (account.authKind === AUTH_KIND_COMPATIBLE_API) {
      atomicWriteText(configPath, buildCompatibleApiConfig(account));
      if (apiKey) {
        atomicWriteJson(authPath, { OPENAI_API_KEY: String(apiKey).trim() });
      } else if (!fs.existsSync(authPath)) {
        throw new Error('新增兼容 API 账号时必须填写 API Key。');
      }
    } else if (account.authKind === AUTH_KIND_OFFICIAL_OAUTH) {
      atomicWriteText(configPath, buildOfficialOAuthConfig());
    } else {
      const existingConfig = fs.existsSync(configPath) ? fs.readFileSync(configPath, 'utf8') : '';
      const projectedConfig = projectAccessTokenConfig(existingConfig);
      if (existingConfig.replace(/\r\n?/g, '\n') !== projectedConfig) {
        atomicWriteText(configPath, projectedConfig);
      }
    }
  }

  saveAccount(input, editingId = null) {
    const { accounts, existing, candidate } = this.createAccountCandidate(input, editingId);
    if (candidate.authKind === AUTH_KIND_COMPATIBLE_API &&
        Object.prototype.hasOwnProperty.call(input || {}, 'apiWireApi') &&
        input.apiWireApi !== 'responses') {
      throw new Error('兼容 API 仅支持 responses wire API。');
    }
    if (candidate.authKind === AUTH_KIND_OFFICIAL_OAUTH &&
        !(existing?.authKind === AUTH_KIND_OFFICIAL_OAUTH &&
          path.resolve(existing.codexHome) === path.resolve(candidate.codexHome) &&
          isOfficialOAuthAuthFile(path.join(existing.codexHome, 'auth.json')))) {
      throw new Error('请先完成 ChatGPT 官方登录并看到“✓ 已登录”，再保存账号。');
    }
    this.ensureAccountHome(candidate, input.apiKey, {
      resetStoredAuth: Boolean(existing && existing.authKind !== candidate.authKind),
    });
    const nextFingerprint = credentialFingerprint(path.join(candidate.codexHome, 'auth.json'));
    const credentialChanged = !existing || existing.authKind !== candidate.authKind ||
      nextFingerprint !== existing.credentialFingerprint;
    candidate.credentialFingerprint = nextFingerprint;
    if (credentialChanged) {
      candidate.credentialEpoch = crypto.randomUUID();
      candidate.credentialActivatedAt = new Date().toISOString();
    }
    const sanitized = { ...candidate };
    delete sanitized.apiKey;
    const next = existing
      ? accounts.map((item) => (item.id === editingId ? sanitized : item))
      : [...accounts, sanitized];
    next.sort((a, b) => a.name.localeCompare(b.name, 'zh-CN'));
    this.writeAccountsManifest(next);
    if (credentialChanged) {
      try { this.clearQuotaSnapshotsForAccount(candidate.id); } catch { /* the rotated epoch excludes stale cache */ }
    }
    return sanitized;
  }

  removeAccount(id) {
    const accounts = this.loadAccounts();
    const account = accounts.find((item) => item.id === id);
    if (!account) throw new Error('账号不存在。');
    if (!this.isManagedCodexHome(account.codexHome)) {
      throw new Error('账号凭据目录不在受管理目录中，拒绝永久删除。');
    }
    const next = accounts.filter((item) => item.id !== id);
    const switches = this.readJson(this.switchesPath, []);
    let anonymizedSwitches = switches;
    let switchesChanged = false;
    if (Array.isArray(switches)) {
      anonymizedSwitches = switches.map((entry) => {
        if (!entry || typeof entry !== 'object') return entry;
        const recordedId = String(entry.accountId || entry.accountKey || entry.AccountId || entry.AccountKey || '').trim();
        const recordedName = String(entry.accountName || entry.name || entry.AccountName || entry.Name || '').trim();
        const belongsToDeletedAccount = recordedId === account.id ||
          (!recordedId && recordedName.localeCompare(account.name, undefined, { sensitivity: 'accent' }) === 0);
        if (!belongsToDeletedAccount) return entry;
        switchesChanged = true;
        return {
          timestampUtc: String(
            entry.timestampUtc || entry.switchedAtUtc || entry.switched_at_utc ||
            entry.TimestampUtc || entry.SwitchedAtUtc || entry.timestamp || entry.at || '',
          ),
          accountId: '',
          accountName: '',
          source: 'deleted-account-boundary',
        };
      });
    }
    const settings = this.loadSettings();
    const settingsChanged = settings.currentAccountId === id;
    const metadataPaths = [
      this.accountsPath,
      this.accountsBackupPath,
      this.switchesPath,
      this.settingsPath,
      this.quotaSnapshotsPath,
    ];
    const metadataSnapshots = new Map(metadataPaths.map((filePath) => [
      filePath,
      fs.existsSync(filePath) ? fs.readFileSync(filePath) : null,
    ]));
    let tombPath = null;
    const originalHome = path.resolve(account.codexHome);
    if (fs.existsSync(originalHome)) {
      const realHome = this.ensureSafeManagedAccountHome(originalHome);
      tombPath = path.join(this.accountHomesRoot, `.delete-tomb-${account.id}-${crypto.randomUUID()}`);
      fs.renameSync(realHome, tombPath);
    }

    try {
      this.writeAccountsManifest(next);
      if (switchesChanged) atomicWriteJson(this.switchesPath, anonymizedSwitches);
      if (settingsChanged) this.saveSettings({ ...settings, currentAccountId: null });
      this.clearQuotaSnapshotsForAccount(account.id);
    } catch (error) {
      const rollbackErrors = [];
      if (tombPath && fs.existsSync(tombPath)) {
        try {
          if (fs.existsSync(originalHome)) throw new Error('原账号目录已被重新创建，无法安全回滚。');
          fs.renameSync(this.assertDeleteTombHome(tombPath), originalHome);
          tombPath = null;
        } catch (rollbackError) {
          rollbackErrors.push(rollbackError);
        }
      }
      for (const filePath of metadataPaths) {
        try {
          this.restoreFile(filePath, metadataSnapshots.get(filePath));
        } catch (rollbackError) {
          rollbackErrors.push(rollbackError);
        }
      }
      if (rollbackErrors.length > 0) error.rollbackErrors = rollbackErrors;
      throw error;
    }
    let cleanupWarning = null;
    if (tombPath) {
      try {
        fs.rmSync(this.assertDeleteTombHome(tombPath), { recursive: true, force: false });
      } catch {
        cleanupWarning = '账号已删除，但凭据临时目录未能完全清理。请重启管理器后再次检查。';
      }
    }
    return { accountId: account.id, cleanupWarning };
  }

  setCurrentAccount(id) {
    const account = this.loadAccounts().find((item) => item.id === id);
    if (!account) throw new Error('账号不存在。');
    const settings = this.loadSettings();
    const switches = this.readJson(this.switchesPath, []);
    const next = Array.isArray(switches) ? switches.slice(-1999) : [];
    next.push({ timestampUtc: new Date().toISOString(), accountId: id, accountName: account.name });
    const previousSwitches = fs.existsSync(this.switchesPath)
      ? fs.readFileSync(this.switchesPath)
      : null;
    atomicWriteJson(this.switchesPath, next);
    try {
      this.saveSettings({ ...settings, currentAccountId: id });
    } catch (error) {
      try {
        this.restoreFile(this.switchesPath, previousSwitches);
      } catch (rollbackError) {
        error.rollbackError = rollbackError;
      }
      throw error;
    }
    return account;
  }

  loadUsageSwitches() {
    const values = this.readJson(this.switchesPath, []);
    if (!Array.isArray(values)) return [];
    return values.filter((item) => item && typeof item === 'object' &&
      typeof item.timestampUtc === 'string' && typeof item.accountId === 'string');
  }

  markAccountUsed(id, timestamp = new Date().toISOString()) {
    const accounts = this.loadAccounts();
    const account = accounts.find((item) => item.id === id);
    if (!account) throw new Error('账号不存在。');
    account.lastUsedAt = timestamp;
    account.updatedAt = timestamp;
    this.writeAccountsManifest(accounts);
    return account;
  }

  updateQuotaProfile(id, profile = {}) {
    const accounts = this.loadAccounts();
    const account = accounts.find((item) => item.id === id);
    if (!account) return null;
    account.quotaLimitType = typeof profile.quotaLimitType === 'string'
      ? profile.quotaLimitType
      : account.quotaLimitType;
    if (Object.prototype.hasOwnProperty.call(profile, 'quotaPrimaryWindowMinutes')) {
      const value = optionalFiniteNumber(profile.quotaPrimaryWindowMinutes);
      if (value !== null || profile.quotaPrimaryWindowMinutes === null) {
        account.quotaPrimaryWindowMinutes = value;
      }
    }
    if (Object.prototype.hasOwnProperty.call(profile, 'quotaSecondaryWindowMinutes')) {
      const value = optionalFiniteNumber(profile.quotaSecondaryWindowMinutes);
      if (value !== null || profile.quotaSecondaryWindowMinutes === null) {
        account.quotaSecondaryWindowMinutes = value;
      }
    }
    this.writeAccountsManifest(accounts);
    return account;
  }

  importAccounts(values) {
    if (!Array.isArray(values)) throw new Error('所选文件不是账号数组。');
    const existing = this.loadAccounts();
    const result = [...existing];
    let importedCount = 0;
    for (const value of values) {
      const candidate = this.normalizeAccount(value, { imported: true });
      if (!candidate) continue;
      if (candidate.authKind === AUTH_KIND_OFFICIAL_OAUTH) continue;
      if (result.some((item) => item.name.toLowerCase() === candidate.name.toLowerCase())) continue;
      candidate.id = crypto.randomUUID();
      candidate.codexHome = this.uniqueCodexHome(candidate.name, candidate.id);
      this.validateAccount(candidate, result);
      // Imported account manifests never carry secrets. The user logs in afterwards.
      fs.mkdirSync(candidate.codexHome, { recursive: true, mode: 0o700 });
      if (candidate.authKind === AUTH_KIND_ACCESS_TOKEN) {
        atomicWriteText(path.join(candidate.codexHome, 'config.toml'), buildAccessTokenConfig());
      } else if (candidate.authKind === AUTH_KIND_OFFICIAL_OAUTH) {
        atomicWriteText(path.join(candidate.codexHome, 'config.toml'), buildOfficialOAuthConfig());
      }
      result.push(candidate);
      importedCount += 1;
    }
    result.sort((a, b) => a.name.localeCompare(b.name, 'zh-CN'));
    this.writeAccountsManifest(result);
    return importedCount;
  }
}

module.exports = {
  AccountStore,
  DELETE_TOMB_DIRECTORY_PATTERN,
  DEFAULT_OAUTH_DRAFT_TTL_MS,
  atomicWriteText,
  buildAccessTokenConfig,
  buildCompatibleApiConfig,
  buildOfficialOAuthConfig,
  isOfficialOAuthAuthFile,
};
