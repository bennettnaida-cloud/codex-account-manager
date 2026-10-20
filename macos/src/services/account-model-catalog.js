const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { atomicWriteText } = require('./account-store');

const FILE_NAME = '.codex-account-manager-models.json';
const MARKER = ' # codex-account-manager-model-catalog';
const MAX_BYTES = 32 * 1024 * 1024;
const REFRESH_MS = 15 * 60 * 1000;
const safeId = (id) => typeof id === 'string' && /^[A-Za-z0-9][A-Za-z0-9._:-]{0,99}$/.test(id);
const hash = (value) => crypto.createHash('sha256').update(value).digest('hex');

function credential(account) {
  const file = path.join(account.codexHome, 'auth.json');
  const details = fs.lstatSync(file);
  if (!details.isFile() || details.isSymbolicLink() || details.size > 2 * 1024 * 1024)
    throw new Error('账号凭据文件无效，模型目录未更新。');
  const source = fs.readFileSync(file);
  const auth = JSON.parse(source.toString('utf8'));
  if (auth.tokens) throw new Error('凭据类型与 AT/API 账号不匹配。');
  const token = auth.personal_access_token || auth.OPENAI_API_KEY;
  if (typeof token !== 'string' || !token || /[\r\n]/.test(token) ||
      (account.authKind === 'access_token' && !/^at-[A-Za-z0-9._~-]{12,}$/.test(token)))
    throw new Error('账号缺少有效凭据，模型目录未更新。');
  return { token, identity: hash(source) };
}

function accountIdentity(account, auth) {
  return hash(JSON.stringify([account.id, path.resolve(account.codexHome), account.authKind,
    account.apiBaseUrl || '', account.apiModel || '', auth.identity]));
}

function projectCatalog(config, catalogPath) {
  const cleaned = config.split(/\r?\n/).filter((line) => !line.endsWith(MARKER)).join('\n');
  const root = cleaned.split(/^\s*\[/m)[0];
  if (/^\s*model_catalog_json\s*=/m.test(root)) return config;
  const tomlPath = JSON.stringify(catalogPath);
  return `model_catalog_json = ${tomlPath}${MARKER}\n${cleaned}`;
}

function validModels(value) {
  const entries = Array.isArray(value) ? value : value?.models;
  if (!Array.isArray(entries)) throw new Error('在线模型目录格式无效。');
  const seen = new Set();
  const models = entries.filter((entry) => entry && safeId(entry.slug) &&
    !seen.has(entry.slug) && seen.add(entry.slug));
  if (!models.length) throw new Error('在线模型目录为空，保留上一份目录。');
  return models;
}

function modelTemplate(id) {
  for (const root of [path.join(__dirname, '..', '..', 'codex-models'),
    path.join(__dirname, '..', '..', '..', 'assets', 'codex-models')]) {
    for (const name of [id, 'fallback']) {
      try {
        const entry = JSON.parse(fs.readFileSync(path.join(root, `${name}.json`), 'utf8')).models[0];
        return { ...entry, slug: id, display_name: name === 'fallback' ? id : entry.display_name, visibility: 'list' };
      } catch { /* Continue to the next packaged template. */ }
    }
  }
  throw new Error('缺少兼容 API 模型模板，请重新安装管理器。');
}

async function responseJson(fetchImpl, url, headers, signal) {
  const response = await fetchImpl(url, { headers, signal, redirect: 'error', credentials: 'omit' });
  if (!response.ok) throw new Error(`在线模型查询失败（HTTP ${response.status}），保留上一份目录。`);
  if (Number(response.headers.get('content-length')) > MAX_BYTES) throw new Error('模型目录响应过大。');
  const chunks = [];
  let size = 0;
  for await (const chunk of response.body) {
    signal?.throwIfAborted();
    size += chunk.byteLength;
    if (size > MAX_BYTES) { await response.body.cancel?.().catch(() => {}); throw new Error('模型目录响应过大。'); }
    chunks.push(Buffer.from(chunk));
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

class AccountModelCatalogService {
  constructor({ store, fetchForAccount, clientVersion = async () => '0.154.0' }) {
    this.store = store;
    this.fetchForAccount = fetchForAccount;
    this.clientVersion = clientVersion;
    this.pending = new Map();
  }

  refresh(account, options = {}) {
    const previous = this.pending.get(account.id) || Promise.resolve();
    const operation = previous.catch(() => {}).then(() => this.refreshCore(account, options));
    this.pending.set(account.id, operation);
    operation.finally(() => { if (this.pending.get(account.id) === operation) this.pending.delete(account.id); }).catch(() => {});
    return operation;
  }

  async refreshCore(account, { force = true, signal } = {}) {
    if (!['access_token', 'compatible_api'].includes(account.authKind)) return { skipped: true };
    signal?.throwIfAborted();
    const auth = credential(account);
    const identity = accountIdentity(account, auth);
    const isCurrent = () => {
      try {
        const current = this.store.loadAccounts().find((item) => item.id === account.id);
        return current && accountIdentity(current, credential(current)) === identity;
      } catch { return false; }
    };
    if (!isCurrent()) throw new Error('账号已变化，已取消模型查询。');
    const catalogPath = path.join(account.codexHome, FILE_NAME);
    const receiptPath = `${catalogPath}.refresh.json`;
    let cached = null;
    try {
      const info = fs.statSync(catalogPath);
      if (info.size > MAX_BYTES) throw new Error('oversized');
      const text = fs.readFileSync(catalogPath, 'utf8');
      const receipt = JSON.parse(fs.readFileSync(receiptPath, 'utf8'));
      const age = Date.now() - receipt.fetchedAt;
      if (receipt.identity === identity && receipt.hash === hash(text) && age >= 0) {
        cached = { models: validModels(JSON.parse(text)), age };
        if (!force && age < REFRESH_MS) { this.syncConfig(account, catalogPath); return { cached: true, count: cached.models.length }; }
      }
    } catch { /* A legacy or another credential's file is never online proof. */ }
    const deadline = AbortSignal.timeout(20_000);
    const cancellation = signal ? AbortSignal.any([signal, deadline]) : deadline;
    try {
      const fetchImpl = await this.fetchForAccount(account);
      let models;
      if (account.authKind === 'access_token') {
        const version = await this.clientVersion(account);
        if (!/^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$/.test(version)) throw new Error('无法读取 CLI 版本。');
        const headers = { Authorization: `Bearer ${auth.token}`, Accept: 'application/json', originator: 'codex_cli_rs',
          version, 'User-Agent': `codex_cli_rs/${version} (Mac OS; arm64) codex-account-manager` };
        const who = await responseJson(fetchImpl, 'https://auth.openai.com/api/accounts/v1/user-auth-credential/whoami', headers, cancellation);
        if (!/^[A-Za-z0-9_-]{1,128}$/.test(who.chatgpt_account_id || '')) throw new Error('AT 工作区身份无效。');
        headers['chatgpt-account-id'] = who.chatgpt_account_id;
        if (who.chatgpt_account_is_fedramp === true) headers['x-openai-fedramp'] = 'true';
        models = validModels(await responseJson(fetchImpl,
          `https://chatgpt.com/backend-api/codex/models?client_version=${encodeURIComponent(version)}`, headers, cancellation));
      } else {
        const url = new URL(account.apiBaseUrl);
        if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new Error('API 地址无效。');
        url.pathname = `${url.pathname.replace(/\/+$/, '')}/models`;
        const source = await responseJson(fetchImpl, url.toString(), { Authorization: `Bearer ${auth.token}` }, cancellation);
        const ids = [...new Set((source.data || source.models || []).map((entry) => entry.id || entry.slug))]
          .filter((id) => safeId(id) && /^gpt-(?:[5-9]|[1-9][0-9]+)(?:\.[0-9]+)*(?:-[a-z0-9]+)*$/.test(id) &&
            !/-(image|audio|realtime|transcribe|tts|embedding|search|chat|moderation)(-|$)/.test(id));
        models = validModels(ids.map(modelTemplate));
      }
      cancellation.throwIfAborted();
      if (!isCurrent()) throw new Error('账号或凭据已变化，已丢弃过期的模型结果。');
      const text = JSON.stringify({ models }, null, 2) + '\n';
      atomicWriteText(catalogPath, text);
      atomicWriteText(receiptPath, JSON.stringify({ identity, hash: hash(text), fetchedAt: Date.now() }));
      this.syncConfig(account, catalogPath);
      return { count: models.length, refreshed: true };
    } catch (error) {
      // Refresh failures are visible to the background result and never become
      // false online successes. A valid existing catalog is left in place.
      if (!force && cached && isCurrent() && !signal?.aborted) return { cached: true, stale: true, count: cached.models.length };
      throw error;
    }
  }

  syncConfig(account, catalogPath) {
    const file = path.join(account.codexHome, 'config.toml');
    const original = fs.readFileSync(file, 'utf8');
    const projected = projectCatalog(original, catalogPath);
    if (projected !== original) atomicWriteText(file, projected);
  }

  refreshAll(options = {}) {
    return Promise.allSettled(this.store.loadAccounts().filter((a) => a.authKind !== 'official_oauth').map((a) => this.refresh(a, options)));
  }
}

module.exports = { AccountModelCatalogService, projectCatalog, validModels };
