const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const SERVICE_VERSION = 1;
const PROTECTED_PORT = 8317;
const MIN_DEBUG_PORT = 1024;
const MAX_DEBUG_PORT = 65535;
const MAX_CUSTOM_CSS_BYTES = 1024 * 1024;
const MAX_BACKGROUND_IMAGE_BYTES = 8 * 1024 * 1024;
const MAX_GENERATED_THEME_BYTES = 12 * 1024 * 1024;
const DEFAULT_TIMEOUT_MS = 5_000;
const STYLE_ID = 'codex-account-manager-theme-style';
const STATE_KEY = '__CODEX_ACCOUNT_MANAGER_THEME_STATE__';
const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '[::1]', '::1']);
const CDP_ID_PATTERN = /^[A-Za-z0-9._-]{1,200}$/;

class ThemeServiceError extends Error {
  constructor(code, message, cause = null) {
    super(message, cause ? { cause } : undefined);
    this.name = 'ThemeServiceError';
    this.code = code;
  }
}

function createBuiltInCss(id, palette) {
  const scope = `html[data-codex-account-manager-theme="${id}"]`;
  return `
${scope} {
  color-scheme: ${palette.appearance};
  --cam-theme-canvas: ${palette.canvas};
  --cam-theme-surface: ${palette.surface};
  --cam-theme-surface-raised: ${palette.surfaceRaised};
  --cam-theme-sidebar: ${palette.sidebar};
  --cam-theme-text: ${palette.text};
  --cam-theme-muted: ${palette.muted};
  --cam-theme-line: ${palette.line};
  --cam-theme-accent: ${palette.accent};
  --cam-theme-accent-soft: ${palette.accentSoft};
}
${scope},
${scope} body,
${scope} #root {
  background: var(--cam-theme-canvas) !important;
  color: var(--cam-theme-text) !important;
}
${scope} main.main-surface,
${scope} [role="main"] {
  background:
    radial-gradient(circle at 82% 8%, var(--cam-theme-accent-soft), transparent 34%),
    var(--cam-theme-canvas) !important;
  color: var(--cam-theme-text) !important;
}
${scope} aside.app-shell-left-panel {
  background: color-mix(in srgb, var(--cam-theme-sidebar) 94%, transparent) !important;
  border-color: var(--cam-theme-line) !important;
  color: var(--cam-theme-text) !important;
}
${scope} .composer-surface-chrome {
  background: color-mix(in srgb, var(--cam-theme-surface-raised) 96%, transparent) !important;
  border-color: color-mix(in srgb, var(--cam-theme-accent) 38%, var(--cam-theme-line)) !important;
  box-shadow: 0 18px 55px color-mix(in srgb, var(--cam-theme-accent) 12%, transparent) !important;
}
${scope} button:focus-visible,
${scope} a:focus-visible,
${scope} input:focus-visible,
${scope} textarea:focus-visible {
  outline-color: var(--cam-theme-accent) !important;
}
${scope} ::selection {
  background: var(--cam-theme-accent) !important;
  color: ${palette.selectionText} !important;
}
`.trim();
}

const BUILT_IN_THEME_DEFINITIONS = [
  {
    id: 'manager-light',
    name: '极光浅色',
    appearance: 'light',
    codeTheme: 'codex',
    description: '柔和紫蓝强调色与明亮画布。',
    preview: { accent: '#6c63ff', surface: '#ffffff', text: '#20243a' },
    palette: {
      appearance: 'light', canvas: '#f7f7ff', surface: '#ffffff', surfaceRaised: '#ffffff',
      sidebar: '#f1f0fb', text: '#20243a', muted: '#686d83', line: '#ddddef',
      accent: '#6c63ff', accentSoft: 'rgba(108, 99, 255, 0.16)', selectionText: '#ffffff',
    },
  },
  {
    id: 'manager-porcelain-light',
    name: '青瓷浅色',
    appearance: 'light',
    codeTheme: 'codex',
    description: '偏青的低饱和浅色主题。',
    preview: { accent: '#2f8f83', surface: '#fbfffd', text: '#213a36' },
    palette: {
      appearance: 'light', canvas: '#f2faf7', surface: '#fbfffd', surfaceRaised: '#ffffff',
      sidebar: '#e8f4ef', text: '#213a36', muted: '#60746f', line: '#cee3dc',
      accent: '#2f8f83', accentSoft: 'rgba(47, 143, 131, 0.15)', selectionText: '#ffffff',
    },
  },
  {
    id: 'manager-dark',
    name: '深海夜色',
    appearance: 'dark',
    codeTheme: 'tokyo-night',
    description: '克制的深蓝黑界面与青色强调。',
    preview: { accent: '#50c2b3', surface: '#111923', text: '#edf5f5' },
    palette: {
      appearance: 'dark', canvas: '#091017', surface: '#111923', surfaceRaised: '#16222e',
      sidebar: '#0d161f', text: '#edf5f5', muted: '#99acb2', line: '#263844',
      accent: '#50c2b3', accentSoft: 'rgba(80, 194, 179, 0.16)', selectionText: '#061311',
    },
  },
  {
    id: 'manager-nebula-dark',
    name: '星云夜色',
    appearance: 'dark',
    codeTheme: 'tokyo-night',
    description: '紫色星云强调与深色画布。',
    preview: { accent: '#8c73ff', surface: '#15152a', text: '#f2efff' },
    palette: {
      appearance: 'dark', canvas: '#0c0b18', surface: '#15152a', surfaceRaised: '#1c1b37',
      sidebar: '#111023', text: '#f2efff', muted: '#aaa5c2', line: '#302e50',
      accent: '#8c73ff', accentSoft: 'rgba(140, 115, 255, 0.18)', selectionText: '#ffffff',
    },
  },
];

const BUILT_IN_THEMES = new Map(BUILT_IN_THEME_DEFINITIONS.map((definition) => {
  const { palette, ...metadata } = definition;
  return [definition.id, Object.freeze({
    ...metadata,
    preview: Object.freeze({ ...metadata.preview }),
    css: createBuiltInCss(definition.id, palette),
  })];
}));

function normalizeDebugPort(value) {
  if (value === undefined || value === null || value === '') {
    throw new ThemeServiceError('PORT_REQUIRED', '必须显式提供 Codex App 的远程调试端口。');
  }
  const port = typeof value === 'string' && /^\d+$/.test(value.trim())
    ? Number(value.trim())
    : value;
  if (!Number.isInteger(port) || port < MIN_DEBUG_PORT || port > MAX_DEBUG_PORT) {
    throw new ThemeServiceError(
      'INVALID_PORT',
      `Codex App 远程调试端口必须是 ${MIN_DEBUG_PORT}-${MAX_DEBUG_PORT} 之间的整数。`,
    );
  }
  if (port === PROTECTED_PORT) {
    throw new ThemeServiceError(
      'PROTECTED_PORT',
      '端口 8317 受保护，主题服务不会连接、探测或操作该端口。',
    );
  }
  return port;
}

function normalizeTimeout(value, fallback = DEFAULT_TIMEOUT_MS) {
  if (value === undefined || value === null) return fallback;
  const timeoutMs = Number(value);
  if (!Number.isInteger(timeoutMs) || timeoutMs < 250 || timeoutMs > 30_000) {
    throw new ThemeServiceError('INVALID_TIMEOUT', '主题操作超时时间必须是 250-30000 毫秒之间的整数。');
  }
  return timeoutMs;
}

function normalizeCustomCss(value) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new ThemeServiceError('CUSTOM_CSS_REQUIRED', '自定义主题必须提供非空 CSS。');
  }
  const css = value.replace(/^\uFEFF/, '');
  const bytes = Buffer.byteLength(css, 'utf8');
  if (bytes > MAX_CUSTOM_CSS_BYTES) {
    throw new ThemeServiceError(
      'CUSTOM_CSS_TOO_LARGE',
      `自定义 CSS 不能超过 ${MAX_CUSTOM_CSS_BYTES} 字节。`,
    );
  }
  return css;
}

function normalizeCustomName(value) {
  const name = String(value || '自定义主题').trim();
  if (!name || name.length > 60 || /[\u0000-\u001f\u007f]/.test(name)) {
    throw new ThemeServiceError('INVALID_THEME_NAME', '自定义主题名称必须为 1-60 个可显示字符。');
  }
  return name;
}

const HEX_COLOR_PATTERN = /^#[0-9a-f]{6}$/i;
const CODE_THEME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
const SAFE_IMAGE_DATA_URL_PATTERN = /^data:image\/(?:png|jpeg|webp);base64,[A-Za-z0-9+/]+={0,2}$/;

function normalizeHexColor(value, fieldName, fallback) {
  const candidate = String(value || fallback).trim();
  if (!HEX_COLOR_PATTERN.test(candidate)) {
    throw new ThemeServiceError('INVALID_THEME_COLOR', `${fieldName}必须是 #RRGGBB 格式的颜色。`);
  }
  return candidate.toLowerCase();
}

function normalizeCustomTheme(value = {}) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new ThemeServiceError('INVALID_CUSTOM_THEME', '自定义主题配置必须是对象。');
  }
  const modeValue = String(value.mode || '').trim().toLowerCase();
  const mode = modeValue || (value.isDark === false ? 'light' : 'dark');
  if (!['light', 'dark'].includes(mode)) {
    throw new ThemeServiceError('INVALID_THEME_MODE', '自定义主题模式只能是 light 或 dark。');
  }
  const codeTheme = String(value.codeTheme || value.codeThemeId || '').trim();
  if (codeTheme && !CODE_THEME_PATTERN.test(codeTheme)) {
    throw new ThemeServiceError('INVALID_CODE_THEME', '代码主题 ID 只能包含字母、数字、点、下划线和连字符。');
  }
  const backgroundImagePath = String(value.backgroundImagePath || '').trim();
  if (backgroundImagePath.length > 4096 || backgroundImagePath.includes('\0')) {
    throw new ThemeServiceError('INVALID_BACKGROUND_PATH', '背景图片路径格式无效。');
  }
  const dark = mode === 'dark';
  return {
    id: 'custom',
    name: normalizeCustomName(value.name),
    appearance: mode,
    mode,
    codeTheme,
    accent: normalizeHexColor(value.accentColor || value.accent, '强调色', dark ? '#8c73ff' : '#6c63ff'),
    surface: normalizeHexColor(value.surfaceColor || value.surface, '表面色', dark ? '#15152a' : '#ffffff'),
    text: normalizeHexColor(value.textColor || value.inkColor || value.text, '文字色', dark ? '#f2efff' : '#20243a'),
    backgroundImagePath,
  };
}

function detectImageMime(bytes) {
  if (bytes.length >= 8 && bytes.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))) {
    return 'image/png';
  }
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) {
    return 'image/jpeg';
  }
  if (
    bytes.length >= 12 &&
    bytes.subarray(0, 4).toString('ascii') === 'RIFF' &&
    bytes.subarray(8, 12).toString('ascii') === 'WEBP'
  ) return 'image/webp';
  return null;
}

async function loadBackgroundImageDataUrl(candidate, {
  realpath = fs.promises.realpath,
  stat = fs.promises.stat,
  readFile = fs.promises.readFile,
  pathModule = path,
} = {}) {
  const sourcePath = String(candidate || '').trim();
  if (!sourcePath) return null;
  if (sourcePath.length > 4096 || sourcePath.includes('\0') || !pathModule.isAbsolute(sourcePath)) {
    throw new ThemeServiceError('INVALID_BACKGROUND_PATH', '背景图片必须使用本机绝对路径。');
  }
  let resolved;
  let details;
  try {
    resolved = await realpath(sourcePath);
    details = await stat(resolved);
  } catch (error) {
    throw new ThemeServiceError('BACKGROUND_NOT_FOUND', '找不到所选的背景图片。', error);
  }
  if (!details?.isFile?.()) {
    throw new ThemeServiceError('INVALID_BACKGROUND_FILE', '所选背景必须是普通图片文件。');
  }
  if (!Number.isFinite(details.size) || details.size <= 0 || details.size > MAX_BACKGROUND_IMAGE_BYTES) {
    throw new ThemeServiceError(
      'BACKGROUND_TOO_LARGE',
      `背景图片必须大于 0 字节且不超过 ${MAX_BACKGROUND_IMAGE_BYTES} 字节。`,
    );
  }
  let bytes;
  try {
    bytes = await readFile(resolved);
  } catch (error) {
    throw new ThemeServiceError('BACKGROUND_READ_FAILED', '无法读取所选背景图片。', error);
  }
  if (!Buffer.isBuffer(bytes)) bytes = Buffer.from(bytes);
  if (bytes.length <= 0 || bytes.length > MAX_BACKGROUND_IMAGE_BYTES) {
    throw new ThemeServiceError('BACKGROUND_TOO_LARGE', '背景图片读取后大小超出允许范围。');
  }
  const mime = detectImageMime(bytes);
  if (!mime) {
    throw new ThemeServiceError('UNSUPPORTED_BACKGROUND', '背景图片只支持 PNG、JPEG 或 WebP。');
  }
  return `data:${mime};base64,${bytes.toString('base64')}`;
}

async function buildCustomThemeCss(value, {
  loadBackgroundImage = loadBackgroundImageDataUrl,
} = {}) {
  const theme = normalizeCustomTheme(value);
  const backgroundDataUrl = theme.backgroundImagePath
    ? await loadBackgroundImage(theme.backgroundImagePath)
    : null;
  if (backgroundDataUrl !== null && (
    typeof backgroundDataUrl !== 'string' ||
    !SAFE_IMAGE_DATA_URL_PATTERN.test(backgroundDataUrl) ||
    Buffer.byteLength(backgroundDataUrl, 'utf8') > MAX_GENERATED_THEME_BYTES
  )) {
    throw new ThemeServiceError('INVALID_BACKGROUND_DATA', '背景图片没有生成安全的本地图片数据。');
  }
  const scope = 'html[data-codex-account-manager-theme="custom"]';
  const backgroundLayer = backgroundDataUrl
    ? `linear-gradient(color-mix(in srgb, ${theme.surface} 72%, transparent), color-mix(in srgb, ${theme.surface} 88%, transparent)), url('${backgroundDataUrl}')`
    : `radial-gradient(circle at 82% 8%, color-mix(in srgb, ${theme.accent} 18%, transparent), transparent 36%), ${theme.surface}`;
  const css = `
${scope} {
  color-scheme: ${theme.mode};
  --cam-theme-accent: ${theme.accent};
  --cam-theme-surface: ${theme.surface};
  --cam-theme-text: ${theme.text};
}
${scope}, ${scope} body, ${scope} #root {
  background: ${theme.surface} !important;
  color: ${theme.text} !important;
}
${scope} main.main-surface, ${scope} [role="main"] {
  background: ${backgroundLayer} !important;
  background-position: center !important;
  background-size: cover !important;
  color: ${theme.text} !important;
}
${scope} aside.app-shell-left-panel {
  background: color-mix(in srgb, ${theme.surface} 92%, transparent) !important;
  border-color: color-mix(in srgb, ${theme.accent} 30%, ${theme.surface}) !important;
  color: ${theme.text} !important;
}
${scope} .composer-surface-chrome {
  background: color-mix(in srgb, ${theme.surface} 92%, ${theme.text} 8%) !important;
  border-color: color-mix(in srgb, ${theme.accent} 42%, ${theme.surface}) !important;
}
${scope} button:focus-visible, ${scope} a:focus-visible,
${scope} input:focus-visible, ${scope} textarea:focus-visible {
  outline-color: ${theme.accent} !important;
}
${scope} ::selection {
  background: ${theme.accent} !important;
  color: ${theme.mode === 'dark' ? '#ffffff' : '#101018'} !important;
}
`.trim();
  if (Buffer.byteLength(css, 'utf8') > MAX_GENERATED_THEME_BYTES) {
    throw new ThemeServiceError('GENERATED_THEME_TOO_LARGE', '背景图片生成的主题数据过大。');
  }
  return {
    id: theme.id,
    name: theme.name,
    appearance: theme.appearance,
    mode: theme.mode,
    codeTheme: theme.codeTheme,
    preview: { accent: theme.accent, surface: theme.surface, text: theme.text },
    css,
    digest: themeDigest(css),
    custom: true,
  };
}

function themeDigest(css) {
  return crypto.createHash('sha256').update(css, 'utf8').digest('hex');
}

function resolveTheme({ themeId, customCss, customName } = {}) {
  if (customCss !== undefined && customCss !== null) {
    const css = normalizeCustomCss(customCss);
    return {
      id: 'custom',
      name: normalizeCustomName(customName),
      appearance: 'custom',
      css,
      digest: themeDigest(css),
      custom: true,
    };
  }
  const id = String(themeId || '').trim().toLowerCase();
  const builtIn = BUILT_IN_THEMES.get(id);
  if (!builtIn) {
    throw new ThemeServiceError('THEME_NOT_FOUND', '请选择有效的内置主题，或提供自定义 CSS。');
  }
  return {
    id: builtIn.id,
    name: builtIn.name,
    appearance: builtIn.appearance,
    css: builtIn.css,
    digest: themeDigest(builtIn.css),
    custom: false,
  };
}

function validatedWebSocketUrl(value, port, kind = 'page', expectedId = null) {
  let url;
  try {
    url = new URL(String(value || ''));
  } catch {
    throw new ThemeServiceError('CDP_INVALID_ENDPOINT', '调试端点返回了无效的 WebSocket 地址。');
  }
  const segment = kind === 'browser' ? 'browser' : 'page';
  const match = new RegExp(`^/devtools/${segment}/([A-Za-z0-9._-]{1,200})$`).exec(url.pathname);
  const id = match?.[1] || null;
  if (
    url.protocol !== 'ws:' ||
    !LOOPBACK_HOSTS.has(url.hostname) ||
    Number(url.port) !== port ||
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    !id ||
    !CDP_ID_PATTERN.test(id) ||
    (expectedId !== null && id !== expectedId)
  ) {
    throw new ThemeServiceError(
      'CDP_INVALID_ENDPOINT',
      '调试端点返回了非回环地址或形状不安全的 WebSocket 地址。',
    );
  }
  return { url: url.href, id };
}

function isSafeAppTarget(target, port) {
  if (
    target?.type !== 'page' ||
    typeof target.id !== 'string' ||
    !CDP_ID_PATTERN.test(target.id) ||
    typeof target.url !== 'string' ||
    !target.url.startsWith('app://')
  ) return false;
  try {
    validatedWebSocketUrl(target.webSocketDebuggerUrl, port, 'page', target.id);
    return true;
  } catch {
    return false;
  }
}

function buildProbeExpression() {
  return `/* codex-account-manager:probe */ (() => {
    const markers = {
      shell: Boolean(document.querySelector('main.main-surface')),
      sidebar: Boolean(document.querySelector('aside.app-shell-left-panel')),
      composer: Boolean(document.querySelector('.composer-surface-chrome')),
      main: Boolean(document.querySelector('[role="main"]')),
    };
    return {
      codex: location.protocol === 'app:' && markers.shell && markers.sidebar &&
        (markers.composer || markers.main),
      markers,
    };
  })()`;
}

function buildInstallExpression(theme) {
  return `/* codex-account-manager:install */ (() => {
    const styleId = ${JSON.stringify(STYLE_ID)};
    const stateKey = ${JSON.stringify(STATE_KEY)};
    const css = ${JSON.stringify(theme.css)};
    const themeId = ${JSON.stringify(theme.id)};
    const digest = ${JSON.stringify(theme.digest)};
    let style = document.getElementById(styleId);
    if (style && style.tagName !== 'STYLE') {
      style.remove();
      style = null;
    }
    if (!style) {
      style = document.createElement('style');
      style.id = styleId;
      (document.head || document.documentElement).appendChild(style);
    }
    style.textContent = css;
    style.dataset.themeId = themeId;
    style.dataset.digest = digest;
    document.documentElement.dataset.codexAccountManagerTheme = themeId;
    window[stateKey] = {
      version: ${SERVICE_VERSION},
      themeId,
      digest,
      appliedAt: new Date().toISOString(),
    };
    return {
      stylePresent: style.isConnected,
      themeId: style.dataset.themeId,
      digest: style.dataset.digest,
      cssMatches: style.textContent === css,
    };
  })()`;
}

function buildVerifyExpression(theme) {
  return `/* codex-account-manager:verify */ (() => {
    const style = document.getElementById(${JSON.stringify(STYLE_ID)});
    const css = ${JSON.stringify(theme.css)};
    return {
      codex: location.protocol === 'app:',
      stylePresent: Boolean(style && style.isConnected && style.tagName === 'STYLE'),
      themeId: style?.dataset.themeId || null,
      rootThemeId: document.documentElement.dataset.codexAccountManagerTheme || null,
      digest: style?.dataset.digest || null,
      cssMatches: Boolean(style && style.textContent === css),
    };
  })()`;
}

function buildRemoveExpression() {
  return `/* codex-account-manager:remove */ (() => {
    document.getElementById(${JSON.stringify(STYLE_ID)})?.remove();
    delete document.documentElement.dataset.codexAccountManagerTheme;
    delete window[${JSON.stringify(STATE_KEY)}];
    return true;
  })()`;
}

function buildRemovedVerificationExpression() {
  return `/* codex-account-manager:verify-removed */ (() => ({
    codex: location.protocol === 'app:',
    removed: !document.getElementById(${JSON.stringify(STYLE_ID)}) &&
      !document.documentElement.dataset.codexAccountManagerTheme &&
      !window[${JSON.stringify(STATE_KEY)}],
  }))()`;
}

function buildStatusExpression() {
  return `/* codex-account-manager:status */ (() => {
    const style = document.getElementById(${JSON.stringify(STYLE_ID)});
    return {
      codex: location.protocol === 'app:',
      active: Boolean(style && style.isConnected && style.tagName === 'STYLE'),
      themeId: style?.dataset.themeId || null,
      digest: style?.dataset.digest || null,
    };
  })()`;
}

function isVerifiedTheme(result, theme) {
  return Boolean(
    result?.codex &&
    result.stylePresent &&
    result.cssMatches &&
    result.themeId === theme.id &&
    result.rootThemeId === theme.id &&
    result.digest === theme.digest
  );
}

function decodeWebSocketMessage(data) {
  if (typeof data === 'string') return data;
  if (Buffer.isBuffer(data)) return data.toString('utf8');
  if (data instanceof ArrayBuffer) return Buffer.from(data).toString('utf8');
  if (ArrayBuffer.isView(data)) return Buffer.from(data.buffer, data.byteOffset, data.byteLength).toString('utf8');
  return String(data);
}

class CdpSession {
  constructor({ target, port, createWebSocket, timeoutMs }) {
    const validated = validatedWebSocketUrl(target.webSocketDebuggerUrl, port, 'page', target.id);
    this.ws = createWebSocket(validated.url);
    this.timeoutMs = Math.min(Math.max(timeoutMs, 250), 10_000);
    this.nextId = 1;
    this.pending = new Map();
    this.closed = false;
    this.opened = false;
  }

  async open() {
    if (this.opened) return this;
    await new Promise((resolve, reject) => {
      let settled = false;
      const finish = (callback) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        callback();
      };
      const timer = setTimeout(() => finish(() => {
        this.close();
        reject(new ThemeServiceError('CDP_SOCKET_TIMEOUT', '连接 Codex 渲染器超时。'));
      }), this.timeoutMs);
      timer.unref?.();
      this.ws.addEventListener('open', () => finish(resolve), { once: true });
      this.ws.addEventListener('error', () => finish(() => reject(
        new ThemeServiceError('CDP_SOCKET_FAILED', '无法建立 Codex 渲染器调试连接。'),
      )), { once: true });
    });
    this.opened = true;
    this.ws.addEventListener('message', (event) => this.onMessage(event));
    this.ws.addEventListener('error', () => this.close());
    this.ws.addEventListener('close', () => this.close());
    await this.send('Runtime.enable');
    return this;
  }

  onMessage(event) {
    let message;
    try {
      message = JSON.parse(decodeWebSocketMessage(event.data));
    } catch {
      this.close();
      return;
    }
    if (!Number.isInteger(message.id)) return;
    const waiter = this.pending.get(message.id);
    if (!waiter) return;
    clearTimeout(waiter.timer);
    this.pending.delete(message.id);
    if (message.error) {
      waiter.reject(new ThemeServiceError('CDP_COMMAND_FAILED', 'Codex 渲染器拒绝了主题命令。'));
    } else {
      waiter.resolve(message.result);
    }
  }

  send(method, params = {}) {
    if (this.closed) {
      return Promise.reject(new ThemeServiceError('CDP_SOCKET_CLOSED', 'Codex 渲染器调试连接已关闭。'));
    }
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new ThemeServiceError('CDP_COMMAND_TIMEOUT', 'Codex 渲染器没有及时确认主题命令。'));
      }, this.timeoutMs);
      timer.unref?.();
      this.pending.set(id, { resolve, reject, timer });
      try {
        this.ws.send(JSON.stringify({ id, method, params }));
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(new ThemeServiceError('CDP_COMMAND_FAILED', '无法向 Codex 渲染器发送主题命令。', error));
      }
    });
  }

  async evaluate(expression) {
    const result = await this.send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true,
      userGesture: false,
    });
    if (result?.exceptionDetails) {
      throw new ThemeServiceError('RENDERER_EVALUATION_FAILED', 'Codex 页面拒绝执行主题操作。');
    }
    return result?.result?.value;
  }

  close() {
    if (this.closed) return;
    this.closed = true;
    for (const waiter of this.pending.values()) {
      clearTimeout(waiter.timer);
      waiter.reject(new ThemeServiceError('CDP_SOCKET_CLOSED', 'Codex 渲染器调试连接已关闭。'));
    }
    this.pending.clear();
    try { this.ws.close(); } catch { /* The socket is already gone. */ }
  }
}

function publicTheme(theme) {
  return {
    id: theme.id,
    name: theme.name,
    appearance: theme.appearance,
    mode: theme.appearance,
    isDark: theme.appearance === 'dark',
    codeTheme: theme.codeTheme || '',
    description: theme.description,
    preview: { ...theme.preview },
    accent: theme.preview.accent,
    surface: theme.preview.surface,
    text: theme.preview.text,
    builtIn: true,
  };
}

function describeCustomTheme(value) {
  const theme = normalizeCustomTheme(value);
  return {
    id: 'custom',
    name: theme.name,
    appearance: theme.mode,
    mode: theme.mode,
    isDark: theme.mode === 'dark',
    codeTheme: theme.codeTheme,
    description: '保存在本机设置中的自定义 Codex 主题。',
    preview: { accent: theme.accent, surface: theme.surface, text: theme.text },
    accent: theme.accent,
    surface: theme.surface,
    text: theme.text,
    builtIn: false,
  };
}

function publicFailure(error, port = null) {
  const known = error instanceof ThemeServiceError
    ? error
    : new ThemeServiceError('THEME_SERVICE_FAILED', 'Codex 主题服务发生未预期错误。');
  return {
    ok: false,
    status: 'unavailable',
    code: known.code,
    reason: known.message,
    port,
  };
}

class ThemeService {
  constructor({
    fetchImpl = globalThis.fetch,
    createWebSocket = null,
    sessionFactory = null,
    loadBackgroundImage = loadBackgroundImageDataUrl,
    now = () => Date.now(),
    sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)),
  } = {}) {
    this.fetchImpl = fetchImpl;
    this.createWebSocket = createWebSocket || ((url) => {
      if (typeof globalThis.WebSocket !== 'function') {
        throw new ThemeServiceError(
          'WEBSOCKET_UNAVAILABLE',
          '当前运行环境不支持连接 Codex 调试端口。',
        );
      }
      return new globalThis.WebSocket(url);
    });
    this.sessionFactory = sessionFactory || ((options) => new CdpSession({
      ...options,
      createWebSocket: this.createWebSocket,
    }));
    this.loadBackgroundImage = loadBackgroundImage;
    this.now = now;
    this.sleep = sleep;
  }

  listThemes(customTheme = null) {
    const themes = [...BUILT_IN_THEMES.values()].map(publicTheme);
    if (customTheme) themes.push(describeCustomTheme(customTheme));
    return themes;
  }

  async fetchJson(port, resource, timeoutMs) {
    if (typeof this.fetchImpl !== 'function') {
      throw new ThemeServiceError('FETCH_UNAVAILABLE', '当前运行环境不支持访问 Codex 调试端口。');
    }
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    timer.unref?.();
    try {
      const response = await this.fetchImpl(`http://127.0.0.1:${port}${resource}`, {
        redirect: 'error',
        cache: 'no-store',
        headers: { Accept: 'application/json' },
        signal: controller.signal,
      });
      if (!response?.ok) {
        throw new ThemeServiceError('CDP_HTTP_FAILED', `Codex 调试端口返回 HTTP ${response?.status || '错误'}。`);
      }
      try {
        return await response.json();
      } catch (error) {
        throw new ThemeServiceError('CDP_INVALID_JSON', 'Codex 调试端口返回了无效数据。', error);
      }
    } catch (error) {
      if (error instanceof ThemeServiceError) throw error;
      throw new ThemeServiceError(
        'CDP_UNREACHABLE',
        `无法连接 127.0.0.1:${port} 的 Codex 调试端口。`,
        error,
      );
    } finally {
      clearTimeout(timer);
    }
  }

  async discoverTargets(port, timeoutMs) {
    const [version, list] = await Promise.all([
      this.fetchJson(port, '/json/version', timeoutMs),
      this.fetchJson(port, '/json/list', timeoutMs),
    ]);
    validatedWebSocketUrl(version?.webSocketDebuggerUrl, port, 'browser');
    if (!Array.isArray(list)) {
      throw new ThemeServiceError('CDP_INVALID_TARGETS', 'Codex 调试端点没有返回有效的页面列表。');
    }
    return list.filter((target) => isSafeAppTarget(target, port));
  }

  async openSession(target, port, timeoutMs) {
    let session;
    try {
      session = await this.sessionFactory({ target, port, timeoutMs });
      if (!session || typeof session.evaluate !== 'function' || typeof session.close !== 'function') {
        throw new Error('invalid session');
      }
      if (typeof session.open === 'function') session = await session.open();
      return session;
    } catch (error) {
      try { session?.close?.(); } catch { /* Nothing else to clean up. */ }
      if (error instanceof ThemeServiceError) throw error;
      throw new ThemeServiceError('CDP_SOCKET_FAILED', '无法连接 Codex 渲染页面。', error);
    }
  }

  async connectVerified(port, timeoutMs, { wait = true } = {}) {
    const deadline = this.now() + timeoutMs;
    let lastError = null;
    let sawSafeAppTarget = false;
    do {
      let targets = [];
      try {
        const remaining = Math.max(250, Math.min(1_500, deadline - this.now()));
        targets = await this.discoverTargets(port, remaining);
        sawSafeAppTarget ||= targets.length > 0;
      } catch (error) {
        lastError = error;
      }

      const verified = [];
      for (const target of targets) {
        let session;
        try {
          session = await this.openSession(target, port, Math.max(250, Math.min(2_000, deadline - this.now())));
          const probe = await session.evaluate(buildProbeExpression());
          if (probe?.codex) verified.push({ target, session });
          else session.close();
        } catch (error) {
          lastError = error;
          try { session?.close?.(); } catch { /* Continue checking other pages. */ }
        }
      }
      if (verified.length > 0) return verified;
      if (!wait || this.now() >= deadline) break;
      await this.sleep(Math.min(200, Math.max(1, deadline - this.now())));
    } while (this.now() < deadline);

    if (lastError?.code === 'CDP_INVALID_ENDPOINT') throw lastError;
    if (!sawSafeAppTarget && lastError?.code === 'CDP_UNREACHABLE') {
      throw new ThemeServiceError(
        'CDP_UNREACHABLE',
        `无法连接 127.0.0.1:${port}。请先通过账号管理器启动已启用远程调试的 Codex App。`,
      );
    }
    throw new ThemeServiceError(
      'CODEX_RENDERER_NOT_FOUND',
      `在 127.0.0.1:${port} 没有找到经过验证的 Codex App 页面。`,
      lastError,
    );
  }

  async applyTheme({
    port: rawPort,
    themeId,
    customCss,
    customName,
    customTheme,
    timeoutMs: rawTimeout,
  } = {}) {
    const port = normalizeDebugPort(rawPort);
    const timeoutMs = normalizeTimeout(rawTimeout);
    if (customTheme && customCss !== undefined && customCss !== null) {
      throw new ThemeServiceError(
        'AMBIGUOUS_CUSTOM_THEME',
        '自定义主题对象与自定义 CSS 不能同时提供。',
      );
    }
    const theme = customTheme
      ? await buildCustomThemeCss(customTheme, { loadBackgroundImage: this.loadBackgroundImage })
      : resolveTheme({ themeId, customCss, customName });
    const connected = await this.connectVerified(port, timeoutMs);
    const applied = [];
    const touched = [];
    try {
      for (const { session } of connected) {
        // Treat the renderer as modified before sending the command: a socket
        // failure can happen after Chromium executed the expression but before
        // its reply reached us, so rollback must include this session as well.
        touched.push(session);
        await session.evaluate(buildInstallExpression(theme));
        const verification = await session.evaluate(buildVerifyExpression(theme));
        if (!isVerifiedTheme(verification, theme)) {
          throw new ThemeServiceError(
            'THEME_VERIFICATION_FAILED',
            'Codex 页面没有确认主题已完整应用，已撤销本次操作。',
          );
        }
        applied.push(session);
      }
      return {
        ok: true,
        status: 'applied',
        themeId: theme.id,
        themeName: theme.name,
        custom: theme.custom,
        digest: theme.digest,
        targetCount: applied.length,
        port,
        verifiedAt: new Date(this.now()).toISOString(),
      };
    } catch (error) {
      await Promise.allSettled(touched.map(async (session) => {
        await session.evaluate(buildRemoveExpression());
      }));
      if (error instanceof ThemeServiceError) throw error;
      throw new ThemeServiceError(
        'THEME_APPLY_FAILED',
        'Codex 主题应用失败，已撤销能够确认的部分。',
        error,
      );
    } finally {
      for (const { session } of connected) session.close();
    }
  }

  async removeTheme({ port: rawPort, timeoutMs: rawTimeout } = {}) {
    const port = normalizeDebugPort(rawPort);
    const timeoutMs = normalizeTimeout(rawTimeout);
    const connected = await this.connectVerified(port, timeoutMs);
    let removedCount = 0;
    try {
      for (const { session } of connected) {
        await session.evaluate(buildRemoveExpression());
        const verification = await session.evaluate(buildRemovedVerificationExpression());
        if (!verification?.codex || !verification.removed) {
          throw new ThemeServiceError(
            'THEME_REMOVE_VERIFICATION_FAILED',
            'Codex 页面没有确认主题已移除。',
          );
        }
        removedCount += 1;
      }
      return {
        ok: true,
        status: 'official',
        themeId: null,
        targetCount: removedCount,
        port,
        verifiedAt: new Date(this.now()).toISOString(),
      };
    } catch (error) {
      if (error instanceof ThemeServiceError) throw error;
      throw new ThemeServiceError('THEME_REMOVE_FAILED', '恢复 Codex 官方主题失败。', error);
    } finally {
      for (const { session } of connected) session.close();
    }
  }

  async getStatus({ port: rawPort, timeoutMs: rawTimeout } = {}) {
    let port = null;
    try {
      port = normalizeDebugPort(rawPort);
      const timeoutMs = normalizeTimeout(rawTimeout, 1_500);
      const connected = await this.connectVerified(port, timeoutMs, { wait: false });
      try {
        const states = [];
        for (const { session } of connected) states.push(await session.evaluate(buildStatusExpression()));
        const validStates = states.filter((state) => state?.codex);
        const activeStates = validStates.filter((state) => state.active);
        const identities = new Set(activeStates.map((state) => `${state.themeId || ''}:${state.digest || ''}`));
        const mixed = activeStates.length > 0 && (
          activeStates.length !== validStates.length || identities.size > 1
        );
        const first = activeStates[0] || null;
        return {
          ok: true,
          status: mixed ? 'mixed' : first ? 'applied' : 'official',
          themeId: mixed ? null : first?.themeId || null,
          digest: mixed ? null : first?.digest || null,
          targetCount: validStates.length,
          themedTargetCount: activeStates.length,
          port,
          checkedAt: new Date(this.now()).toISOString(),
        };
      } finally {
        for (const { session } of connected) session.close();
      }
    } catch (error) {
      return publicFailure(error, port);
    }
  }
}

module.exports = {
  ThemeService,
  ThemeServiceError,
  buildCustomThemeCss,
  describeCustomTheme,
  loadBackgroundImageDataUrl,
  normalizeCustomTheme,
  normalizeDebugPort,
  _test: {
    BUILT_IN_THEMES,
    MAX_CUSTOM_CSS_BYTES,
    MAX_BACKGROUND_IMAGE_BYTES,
    MAX_GENERATED_THEME_BYTES,
    PROTECTED_PORT,
    SERVICE_VERSION,
    STATE_KEY,
    STYLE_ID,
    CdpSession,
    buildInstallExpression,
    buildProbeExpression,
    buildRemoveExpression,
    buildRemovedVerificationExpression,
    buildStatusExpression,
    buildVerifyExpression,
    isSafeAppTarget,
    isVerifiedTheme,
    detectImageMime,
    normalizeCustomCss,
    resolveTheme,
    themeDigest,
    validatedWebSocketUrl,
  },
};
