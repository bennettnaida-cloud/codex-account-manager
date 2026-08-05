const crypto = require('node:crypto');
const http = require('node:http');
const { Readable } = require('node:stream');
const { pipeline } = require('node:stream/promises');
const {
  GATEWAY_CHALLENGE_HEADER,
  GATEWAY_PID_HEADER,
  GATEWAY_PROOF_HEADER,
  assertGatewaySecret,
  createGatewayProof,
} = require('./gateway-secret');

const DEFAULT_HOST = '127.0.0.1';
const DEFAULT_PORT = 8317;
const UPSTREAM_ORIGIN = 'https://chatgpt.com';
const WHOAMI_URL = 'https://auth.openai.com/api/accounts/v1/user-auth-credential/whoami';
const REQUIRED_CODEX_VERSION = '0.144.1';
const DEFAULT_ORIGINATOR = 'codex_cli_rs';
const DEFAULT_USER_AGENT = 'codex_cli_rs/0.144.1 (Mac OS; arm64) codex-account-manager';
const IDENTITY_CACHE_TTL_MS = 30 * 60 * 1000;
const MAX_REQUEST_BODY_BYTES = 64 * 1024 * 1024;
const MAX_ERROR_BODY_BYTES = 16 * 1024;
const GATEWAY_MARKER_HEADER = 'x-codex-account-manager-gateway';
const GATEWAY_MARKER_VALUE = 'macos-v2';
const GATEWAY_VERSION_HEADER = 'x-codex-account-manager-gateway-version';
const GATEWAY_BUILD_VERSION = require('../../package.json').version;
const PROXY_ROUTE_TEST_URL = `${UPSTREAM_ORIGIN}/backend-api/codex`;
const PROXY_ROUTE_PATTERN = /^(?:PROXY|HTTPS|SOCKS|SOCKS4|SOCKS5|QUIC)\s+\S+$/i;

const FORWARDED_HEADERS = new Set([
  'accept', 'accept-language', 'cache-control', 'content-encoding', 'content-language',
  'idempotency-key', 'if-match', 'if-modified-since', 'if-none-match', 'if-unmodified-since',
  'openai-beta', 'originator', 'pragma', 'range', 'user-agent', 'version', 'session-id',
  'thread-id', 'conversation-id', 'session_id', 'conversation_id', 'x-client-request-id',
  'x-codex-beta-features', 'x-codex-installation-id', 'x-codex-models-etag', 'x-codex-seq',
  'x-codex-trace-id', 'x-codex-turn-state', 'x-codex-turn-metadata', 'x-codex-window-id',
  'x-codex-parent-thread-id', 'x-openai-subagent', 'x-openai-memgen-request',
  'x-openai-internal-codex-responses-lite', 'x-openai-internal-codex-residency',
  'x-oai-attestation', 'x-responsesapi-include-timing-metrics', 'traceparent', 'tracestate',
]);
const HOP_BY_HOP_HEADERS = new Set([
  'connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization', 'te', 'trailer',
  'transfer-encoding', 'upgrade', 'set-cookie', 'content-length', 'content-encoding',
]);
const ALLOWED_METHODS = new Set(['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS']);

function redactSecrets(value) {
  return String(value || '')
    .replace(/\bat-[A-Za-z0-9._~-]{12,}\b/g, '[REDACTED]')
    .replace(/\bsk-[A-Za-z0-9_-]{12,}\b/g, '[REDACTED]')
    .replace(/\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g, '[REDACTED]')
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{12,}\b/gi, 'Bearer [REDACTED]')
    .replace(/([?&](?:code|state)=)[^&\s]+/gi, '$1[REDACTED]')
    .replace(/[\r\n]+/g, ' ')
    .trim()
    .slice(0, 500);
}

function parseBearerCredential(authorization) {
  const match = /^Bearer\s+([^\s]+)$/i.exec(String(authorization || '').trim());
  if (!match) return null;
  const token = match[1];
  if (/^at-[A-Za-z0-9._~-]{12,}$/.test(token)) return { token, personalAccessToken: true };
  const segments = token.split('.');
  if (token.startsWith('eyJ') && token.length >= 64 && segments.length === 3 && segments.every(Boolean)) {
    return { token, personalAccessToken: false };
  }
  return null;
}

function safeIncomingAccountId(value) {
  const accountId = String(value || '').trim();
  return accountId && accountId.length <= 128 && /^[A-Za-z0-9_-]+$/.test(accountId) ? accountId : null;
}

function hasPathPrefix(value, prefix) {
  const pathValue = String(value || '').toLowerCase();
  const prefixValue = prefix.toLowerCase();
  return pathValue === prefixValue || pathValue.startsWith(`${prefixValue}/`);
}

function containsUnsafePathSegment(rawUrl) {
  const rawPath = String(rawUrl || '').split('?', 1)[0];
  return rawPath.split('/').some((segment) => {
    let decoded = segment;
    let stabilized = false;
    try {
      for (let attempt = 0; attempt < 16; attempt += 1) {
        const next = decodeURIComponent(decoded);
        if (next === decoded) {
          stabilized = true;
          break;
        }
        decoded = next;
      }
    } catch {
      return true;
    }
    return !stabilized || decoded === '.' || decoded === '..' || decoded.includes('/') || decoded.includes('\\');
  });
}

function buildUpstreamUrl(rawUrl) {
  if (containsUnsafePathSegment(rawUrl)) return null;
  let incoming;
  try {
    incoming = new URL(String(rawUrl || ''), `http://${DEFAULT_HOST}:${DEFAULT_PORT}`);
  } catch {
    return null;
  }
  const pathValue = incoming.pathname;
  let upstreamPath;
  if (hasPathPrefix(pathValue, '/backend-api/codex')) {
    const suffix = pathValue.slice('/backend-api/codex'.length) || '/responses';
    upstreamPath = `/backend-api/codex${suffix}`;
  } else if (hasPathPrefix(pathValue, '/backend-api')) {
    upstreamPath = `/backend-api${pathValue.slice('/backend-api'.length)}`;
  } else if (hasPathPrefix(pathValue, '/api/codex')) {
    upstreamPath = `/api/codex${pathValue.slice('/api/codex'.length)}`;
  } else {
    return null;
  }
  const upstream = new URL(`${UPSTREAM_ORIGIN}${upstreamPath}${incoming.search}`);
  return hasPathPrefix(upstream.pathname, '/backend-api') || hasPathPrefix(upstream.pathname, '/api/codex')
    ? upstream.toString()
    : null;
}

function versionAtLeast(value, minimum) {
  const parse = (input) => String(input || '').trim().replace(/^v/i, '').split('.').map((item) => Number(item));
  const actual = parse(value);
  const required = parse(minimum);
  if (actual.some((item) => !Number.isInteger(item)) || required.some((item) => !Number.isInteger(item))) return false;
  for (let index = 0; index < Math.max(actual.length, required.length); index += 1) {
    const left = actual[index] || 0;
    const right = required[index] || 0;
    if (left !== right) return left > right;
  }
  return true;
}

function proxyRequiredError(message) {
  const error = new Error(message);
  error.code = 'PROXY_REQUIRED';
  return error;
}

function isFailClosedProxyRoute(value) {
  const routes = String(value || '')
    .split(';')
    .map((route) => route.trim())
    .filter(Boolean);
  return routes.length > 0 && routes.every((route) => PROXY_ROUTE_PATTERN.test(route));
}

function createPatGatewayUpstreamPreparer({
  networkSession,
  proxyUrlProvider,
  testUrl = PROXY_ROUTE_TEST_URL,
} = {}) {
  if (!networkSession || typeof networkSession.setProxy !== 'function' ||
      typeof networkSession.resolveProxy !== 'function' ||
      typeof networkSession.closeAllConnections !== 'function') {
    throw new TypeError('PAT gateway requires a complete Electron network session.');
  }
  if (typeof proxyUrlProvider !== 'function') {
    throw new TypeError('PAT gateway requires a proxy URL provider.');
  }

  let verifiedProxyKey = null;
  let proxyUpdate = Promise.resolve();

  return async function preparePatGatewayUpstream() {
    const proxyUrl = String(proxyUrlProvider() || '').trim();
    if (!proxyUrl) throw proxyRequiredError('未检测到可用的本地代理。');

    const update = proxyUpdate.catch(() => undefined).then(async () => {
      try {
        if (verifiedProxyKey !== proxyUrl) {
          await networkSession.setProxy({
            mode: 'fixed_servers',
            proxyRules: proxyUrl,
          });
          await networkSession.closeAllConnections();
        }
        const route = String(await networkSession.resolveProxy(testUrl));
        if (!isFailClosedProxyRoute(route)) {
          throw proxyRequiredError('代理规则没有以严格代理模式应用到 ChatGPT 上游。');
        }
        verifiedProxyKey = proxyUrl;
        return { proxyUrl, route };
      } catch (error) {
        // A failed validation must force the next attempt to re-apply the proxy.
        verifiedProxyKey = null;
        throw error;
      }
    });
    proxyUpdate = update;
    return update;
  };
}

function forwardedRequestHeaders(incoming, credential, identity) {
  const headers = new Headers();
  for (const [rawName, rawValue] of Object.entries(incoming.headers || {})) {
    const name = rawName.toLowerCase();
    if (name === 'authorization' || name === 'chatgpt-account-id' || name === 'x-openai-fedramp' ||
        name === 'content-type' || name.startsWith('x-codex-account-manager-')) continue;
    if (!FORWARDED_HEADERS.has(name) && !name.startsWith('x-codex-') &&
        !name.startsWith('x-openai-') && !name.startsWith('x-oai-')) continue;
    const value = Array.isArray(rawValue) ? rawValue.join(', ') : String(rawValue || '');
    if (value) headers.set(name, value);
  }
  const contentType = incoming.headers?.['content-type'];
  if (contentType) headers.set('content-type', String(contentType));
  headers.set('authorization', `Bearer ${credential.token}`);
  const accountId = identity?.accountId || safeIncomingAccountId(incoming.headers?.['chatgpt-account-id']);
  if (accountId) headers.set('chatgpt-account-id', accountId);
  if (identity?.isFedRamp === true) headers.set('x-openai-fedramp', 'true');
  if (!String(headers.get('originator') || '').toLowerCase().startsWith('codex_')) {
    headers.set('originator', DEFAULT_ORIGINATOR);
  }
  if (!String(headers.get('user-agent') || '').toLowerCase().startsWith('codex')) {
    headers.set('user-agent', DEFAULT_USER_AGENT);
  }
  if (!versionAtLeast(headers.get('version'), REQUIRED_CODEX_VERSION)) {
    headers.set('version', REQUIRED_CODEX_VERSION);
  }
  if (!String(headers.get('openai-beta') || '').toLowerCase().includes('responses=experimental')) {
    headers.append('openai-beta', 'responses=experimental');
  }
  return headers;
}

async function readRequestBody(request, maximumBytes = MAX_REQUEST_BODY_BYTES) {
  const chunks = [];
  let total = 0;
  for await (const chunk of request) {
    total += chunk.length;
    if (total > maximumBytes) {
      const error = new Error('请求内容过大。');
      error.code = 'REQUEST_TOO_LARGE';
      throw error;
    }
    chunks.push(chunk);
  }
  return chunks.length ? Buffer.concat(chunks, total) : null;
}

async function boundedResponseText(response, maximumBytes = MAX_ERROR_BODY_BYTES) {
  if (!response?.body || typeof response.body.getReader !== 'function') return '';
  const reader = response.body.getReader();
  const chunks = [];
  let total = 0;
  try {
    while (total < maximumBytes) {
      const { done, value } = await reader.read();
      if (done) break;
      const chunk = Buffer.from(value);
      const remaining = maximumBytes - total;
      chunks.push(chunk.subarray(0, remaining));
      total += Math.min(chunk.length, remaining);
      if (chunk.length > remaining) break;
    }
    return Buffer.concat(chunks, total).toString('utf8');
  } catch {
    return '';
  } finally {
    try { await reader.cancel(); } catch { /* response already ended */ }
  }
}

class LocalPatGateway {
  constructor({
    fetchImpl,
    gatewaySecret,
    prepareUpstream = null,
    host = DEFAULT_HOST,
    port = DEFAULT_PORT,
    now = () => Date.now(),
  } = {}) {
    if (typeof fetchImpl !== 'function') throw new TypeError('LocalPatGateway requires fetchImpl.');
    this.fetchImpl = fetchImpl;
    this.gatewaySecret = assertGatewaySecret(gatewaySecret);
    this.prepareUpstream = typeof prepareUpstream === 'function' ? prepareUpstream : async () => {};
    this.host = host;
    this.port = port;
    this.now = now;
    this.server = null;
    this.startPromise = null;
    this.identityCache = new Map();
  }

  async ensureListening() {
    if (this.server?.listening) return this.address();
    if (this.startPromise) return this.startPromise;
    this.startPromise = new Promise((resolve, reject) => {
      const server = http.createServer((request, response) => {
        this.handle(request, response).catch((error) => {
          if (!response.headersSent) this.writeError(response, 502, `本地 PAT 网关处理请求失败：${redactSecrets(error?.message)}`);
          else response.destroy();
        });
      });
      server.keepAliveTimeout = 65_000;
      server.headersTimeout = 70_000;
      server.once('error', (error) => {
        this.startPromise = null;
        if (error?.code === 'EADDRINUSE') {
          const conflict = new Error('本地端口 8317 已被其它程序占用，Access Token 网关无法启动。');
          conflict.code = 'PAT_GATEWAY_PORT_IN_USE';
          reject(conflict);
        } else {
          reject(error);
        }
      });
      server.listen(this.port, this.host, () => {
        this.server = server;
        this.startPromise = null;
        resolve(this.address());
      });
    });
    return this.startPromise;
  }

  async ensureRunning() {
    const address = await this.ensureListening();
    await this.prepareUpstream();
    return address;
  }

  async ensureReady() {
    return this.ensureRunning();
  }

  address() {
    const value = this.server?.address();
    const port = value && typeof value === 'object' ? value.port : this.port;
    return { host: this.host, port, baseUrl: `http://${this.host}:${port}` };
  }

  async close() {
    const server = this.server;
    this.server = null;
    this.startPromise = null;
    this.identityCache.clear();
    if (!server?.listening) return;
    await new Promise((resolve) => server.close(() => resolve()));
  }

  isLoopbackRequest(request) {
    const remote = String(request.socket?.remoteAddress || '');
    return remote === '127.0.0.1' || remote === '::1' || remote === '::ffff:127.0.0.1';
  }

  writeJson(response, statusCode, value, { gatewayChallenge = null } = {}) {
    const body = Buffer.from(`${JSON.stringify(value)}\n`, 'utf8');
    response.statusCode = statusCode;
    response.setHeader(GATEWAY_MARKER_HEADER, GATEWAY_MARKER_VALUE);
    response.setHeader(GATEWAY_VERSION_HEADER, GATEWAY_BUILD_VERSION);
    response.setHeader(GATEWAY_PID_HEADER, String(process.pid));
    if (gatewayChallenge) {
      response.setHeader(GATEWAY_PROOF_HEADER, createGatewayProof(
        this.gatewaySecret,
        gatewayChallenge,
        GATEWAY_MARKER_VALUE,
        GATEWAY_BUILD_VERSION,
        process.pid,
        statusCode,
      ));
    }
    response.setHeader('content-type', 'application/json; charset=utf-8');
    response.setHeader('content-length', body.length);
    response.end(body);
  }

  writeError(response, statusCode, message, options) {
    this.writeJson(response, statusCode, { error: redactSecrets(message) || '本地 PAT 网关请求失败。' }, options);
  }

  async handle(request, response) {
    response.setHeader(GATEWAY_MARKER_HEADER, GATEWAY_MARKER_VALUE);
    if (!this.isLoopbackRequest(request)) return this.writeError(response, 403, '只允许本机访问 PAT 网关。');
    const rawUrl = String(request.url || '/');
    if (rawUrl.split('?', 1)[0].toLowerCase() === '/healthz') {
      const challenge = String(request.headers?.[GATEWAY_CHALLENGE_HEADER] || '').trim().toLowerCase();
      if (!/^[a-f0-9]{64}$/.test(challenge)) {
        return this.writeError(response, 400, 'Access Token 网关健康检查缺少有效的随机挑战。');
      }
      try {
        await this.prepareUpstream();
        return this.writeJson(response, 200, {
          status: 'ready',
          protocol: GATEWAY_MARKER_VALUE,
          version: GATEWAY_BUILD_VERSION,
          pid: process.pid,
          listen: `${this.host}:${this.address().port}`,
        }, { gatewayChallenge: challenge });
      } catch (error) {
        const message = error?.code === 'PROXY_REQUIRED'
          ? '未检测到可用的本地代理；为防止意外直连，网关尚未就绪。'
          : `无法准备上游代理：${redactSecrets(error?.message)}`;
        return this.writeError(response, 503, message, { gatewayChallenge: challenge });
      }
    }
    if (!ALLOWED_METHODS.has(String(request.method || '').toUpperCase())) {
      return this.writeError(response, 405, '本地 PAT 网关不支持这个请求方法。');
    }
    const upstreamUrl = buildUpstreamUrl(rawUrl);
    if (!upstreamUrl) return this.writeError(response, 404, '本地 PAT 网关不支持这个路径。');
    const credential = parseBearerCredential(request.headers.authorization);
    if (!credential) return this.writeError(response, 401, '请求没有携带可用的 Codex PAT 或 ChatGPT OAuth Bearer。');

    try {
      await this.prepareUpstream();
    } catch (error) {
      const message = error?.code === 'PROXY_REQUIRED'
        ? '未检测到可用的本地代理；为防止意外直连，上游请求已停止。'
        : `无法准备上游代理：${redactSecrets(error?.message)}`;
      return this.writeError(response, 503, message);
    }

    let body;
    try {
      body = await readRequestBody(request);
    } catch (error) {
      return this.writeError(response, error?.code === 'REQUEST_TOO_LARGE' ? 413 : 400, error?.message);
    }

    let identity = null;
    if (credential.personalAccessToken) {
      try {
        identity = await this.getPatIdentity(credential.token);
      } catch (error) {
        const inactiveWorkspace = error?.inactiveWorkspace === true;
        if (inactiveWorkspace) {
          return this.writeError(response, 403, 'PAT 未必过期，但当前 ChatGPT 工作区成员资格无效；请在该工作区重新生成或切换账号。');
        }
        if (error?.status === 401 || error?.status === 403) {
          return this.writeError(response, error.status, 'OpenAI 拒绝了 PAT 元数据请求；令牌状态无法确认，请检查代理、工作区和令牌后重试。');
        }
        return this.writeError(response, 502, `通过本地代理请求 OpenAI PAT 元数据失败：${redactSecrets(error?.message)}`);
      }
    }

    const controller = new AbortController();
    const abort = () => controller.abort();
    request.once('aborted', abort);
    response.once('close', () => { if (!response.writableEnded) abort(); });
    let upstream;
    try {
      upstream = await this.fetchImpl(upstreamUrl, {
        method: request.method,
        headers: forwardedRequestHeaders(request, credential, identity),
        body: body && body.length ? body : undefined,
        redirect: 'manual',
        signal: controller.signal,
      });
    } catch (error) {
      return this.writeError(response, 502, `通过本地代理请求 ChatGPT Codex 上游失败：${redactSecrets(error?.message)}`);
    } finally {
      request.removeListener('aborted', abort);
    }

    if ((upstream.status === 401 || upstream.status === 403) && credential.personalAccessToken) {
      this.identityCache.delete(crypto.createHash('sha256').update(credential.token).digest('hex'));
      const errorBody = await boundedResponseText(upstream.clone());
      if (/owner (?:is )?not an active member of (?:the )?selected workspace/i.test(errorBody)) {
        return this.writeError(response, 403, 'PAT 未必过期，但当前 ChatGPT 工作区成员资格无效；请在该工作区重新生成或切换账号。');
      }
      return this.writeError(response, upstream.status, 'ChatGPT Codex 上游拒绝了 PAT；令牌状态无法确认，请检查代理、工作区和令牌后重试。');
    }

    response.statusCode = upstream.status;
    for (const [name, value] of upstream.headers.entries()) {
      const normalized = name.toLowerCase();
      if (!HOP_BY_HOP_HEADERS.has(normalized)) response.setHeader(name, value);
    }
    if (!upstream.body || request.method === 'HEAD') {
      response.end();
      return;
    }
    try {
      await pipeline(Readable.fromWeb(upstream.body), response);
    } catch (error) {
      if (!controller.signal.aborted) response.destroy(error);
    }
  }

  async getPatIdentity(token) {
    const key = crypto.createHash('sha256').update(token).digest('hex');
    const cached = this.identityCache.get(key);
    if (cached && cached.expiresAt > this.now()) return cached.identity;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 20_000);
    timer.unref?.();
    let response;
    try {
      response = await this.fetchImpl(WHOAMI_URL, {
        method: 'GET',
        headers: {
          authorization: `Bearer ${token}`,
          accept: 'application/json',
          originator: DEFAULT_ORIGINATOR,
          'user-agent': DEFAULT_USER_AGENT,
        },
        redirect: 'manual',
        signal: controller.signal,
      });
    } finally {
      clearTimeout(timer);
    }
    if (response.status === 401 || response.status === 403) {
      const body = await boundedResponseText(response);
      const error = new Error(`whoami returned HTTP ${response.status}`);
      error.status = response.status;
      error.inactiveWorkspace = /owner (?:is )?not an active member of (?:the )?selected workspace/i.test(body);
      throw error;
    }
    if (!response.ok) throw new Error(`whoami returned HTTP ${response.status}`);
    const value = await response.json();
    const accountId = safeIncomingAccountId(value?.chatgpt_account_id);
    if (!accountId) throw new Error('whoami response omitted chatgpt_account_id');
    const identity = { accountId, isFedRamp: value?.chatgpt_account_is_fedramp === true };
    this.identityCache.set(key, { identity, expiresAt: this.now() + IDENTITY_CACHE_TTL_MS });
    return identity;
  }
}

module.exports = {
  DEFAULT_PORT,
  GATEWAY_CHALLENGE_HEADER,
  GATEWAY_MARKER_HEADER,
  GATEWAY_MARKER_VALUE,
  GATEWAY_PID_HEADER,
  GATEWAY_PROOF_HEADER,
  GATEWAY_VERSION_HEADER,
  GATEWAY_BUILD_VERSION,
  LocalPatGateway,
  createPatGatewayUpstreamPreparer,
  _test: {
    buildUpstreamUrl,
    forwardedRequestHeaders,
    isFailClosedProxyRoute,
    parseBearerCredential,
    redactSecrets,
  },
};
