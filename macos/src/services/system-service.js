const fs = require('node:fs');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');

const EXCLUDED_PORTS = new Set([8317]);
const COMMON_PROXY_PORTS = [7890, 7891, 1080, 1081, 10808, 10809, 6152, 8080, 8888, 9090];

function validPort(value) {
  const port = Number(value);
  return Number.isInteger(port) && port > 0 && port <= 65535 && !EXCLUDED_PORTS.has(port)
    ? port
    : null;
}

function normalizeProxySettings(value = {}) {
  const scheme = value.proxyScheme === 'socks5' || value.scheme === 'socks5' ? 'socks5' : 'http';
  const address = String(value.proxyAddress ?? value.address ?? '127.0.0.1').trim() || '127.0.0.1';
  if (/[/\\\s@]/.test(address) || address.length > 253) throw new Error('代理地址格式无效。');
  return {
    proxyAutoDetect: value.proxyAutoDetect !== false && value.autoDetect !== false,
    proxyScheme: scheme,
    proxyAddress: address,
    proxyPort: validPort(value.proxyPort ?? value.port),
    detectedProxyPort: validPort(value.detectedProxyPort),
  };
}

function formatProxyUrl(settings) {
  const proxy = normalizeProxySettings(settings);
  const port = proxy.proxyPort || (proxy.proxyAutoDetect ? proxy.detectedProxyPort : null);
  if (!port) return null;
  const host = proxy.proxyAddress.includes(':') && !proxy.proxyAddress.startsWith('[')
    ? `[${proxy.proxyAddress}]`
    : proxy.proxyAddress;
  return `${proxy.proxyScheme}://${host}:${port}`;
}

function applyProxyEnvironment(environment, settings) {
  const output = { ...environment };
  const proxyUrl = formatProxyUrl(settings);
  if (proxyUrl) {
    for (const name of ['HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy']) {
      output[name] = proxyUrl;
    }
  }
  const bypass = new Set();
  for (const name of ['NO_PROXY', 'no_proxy']) {
    for (const item of String(output[name] || '').split(',')) {
      const normalized = item.trim();
      if (normalized) bypass.add(normalized);
    }
  }
  for (const item of ['127.0.0.1', 'localhost', '::1', '[::1]']) bypass.add(item);
  output.NO_PROXY = [...bypass].join(',');
  output.no_proxy = output.NO_PROXY;
  return output;
}

function normalizeProjectPath(candidate, { mustExist = true } = {}) {
  const raw = String(candidate || '').trim();
  const expanded = raw === '~' ? os.homedir() : raw.startsWith('~/') ? path.join(os.homedir(), raw.slice(2)) : raw;
  const resolved = path.resolve(expanded || os.homedir());
  if (mustExist) {
    let details;
    try { details = fs.statSync(resolved); } catch { throw new Error('项目启动目录不存在。'); }
    if (!details.isDirectory()) throw new Error('项目启动路径必须是目录。');
  }
  return resolved;
}

function listLoopbackListeningPorts() {
  const executable = ['/usr/sbin/lsof', '/usr/bin/lsof'].find((candidate) => fs.existsSync(candidate));
  if (!executable) return [];
  const result = spawnSync(executable, ['-nP', '-iTCP', '-sTCP:LISTEN'], {
    encoding: 'utf8',
    timeout: 2500,
    windowsHide: true,
  });
  if (result.status !== 0 || !result.stdout) return [];
  const ports = [];
  for (const line of result.stdout.split(/\r?\n/)) {
    const match = /\s(?:127\.0\.0\.1|\[::1\]|localhost|\*):(\d+)\s+\(LISTEN\)\s*$/i.exec(line);
    const port = match ? validPort(match[1]) : null;
    if (port) ports.push(port);
  }
  return [...new Set(ports)];
}

async function probeLocalProxyPort(port, { timeoutMs = 350 } = {}) {
  const candidate = validPort(port);
  if (!candidate) return null;
  const attempt = (scheme) => new Promise((resolve) => {
    const socket = net.createConnection({ host: '127.0.0.1', port: candidate });
    let settled = false;
    let buffer = Buffer.alloc(0);
    const finish = (matched) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      resolve(matched);
    };
    const timer = setTimeout(() => finish(false), timeoutMs);
    timer.unref?.();
    socket.setTimeout(timeoutMs);
    socket.once('connect', () => {
      socket.write(scheme === 'http'
        ? 'CONNECT 127.0.0.1:1 HTTP/1.1\r\nHost: 127.0.0.1:1\r\n\r\n'
        : Buffer.from([0x05, 0x01, 0x00]));
    });
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      const matched = scheme === 'http'
        ? /^HTTP\/1\.[01]\s+\d{3}/i.test(buffer.toString('latin1'))
        : buffer.length >= 2 && buffer[0] === 0x05 && buffer[1] !== 0xff;
      if (matched) finish(true);
    });
    socket.once('timeout', () => finish(false));
    socket.once('error', () => finish(false));
    socket.once('close', () => finish(false));
  });
  if (await attempt('http')) return { address: '127.0.0.1', port: candidate, scheme: 'http' };
  if (await attempt('socks5')) return { address: '127.0.0.1', port: candidate, scheme: 'socks5' };
  return null;
}

async function detectLocalProxy({ preferredPort = null, ports = null, probe = probeLocalProxyPort } = {}) {
  const ordered = [];
  for (const value of [preferredPort, ...(ports || listLoopbackListeningPorts()), ...COMMON_PROXY_PORTS]) {
    const port = validPort(value);
    if (port && !ordered.includes(port)) ordered.push(port);
  }
  for (const port of ordered) {
    const result = await probe(port);
    if (result) return { found: true, ...result, checkedPorts: ordered.length };
  }
  return { found: false, address: '127.0.0.1', port: null, scheme: 'http', checkedPorts: ordered.length };
}

function openPath(target, { spawnProcess = spawn } = {}) {
  const resolved = normalizeProjectPath(target);
  if (process.platform !== 'darwin') return { ok: true, path: resolved, simulated: true };
  const child = spawnProcess('/usr/bin/open', [resolved], { detached: true, stdio: 'ignore' });
  child.unref?.();
  return { ok: true, path: resolved };
}

module.exports = {
  COMMON_PROXY_PORTS,
  applyProxyEnvironment,
  detectLocalProxy,
  formatProxyUrl,
  listLoopbackListeningPorts,
  normalizeProjectPath,
  normalizeProxySettings,
  openPath,
  probeLocalProxyPort,
  validPort,
};
