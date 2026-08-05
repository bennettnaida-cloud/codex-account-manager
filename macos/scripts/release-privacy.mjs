import path from 'node:path';
import { readFile, stat } from 'node:fs/promises';

const MAX_SCANNABLE_BYTES = 16 * 1024 * 1024;
const KNOWN_BINARY_EXTENSIONS = new Set([
  '.asar', '.dylib', '.gif', '.icns', '.jpeg', '.jpg', '.otf', '.pdf', '.png', '.so', '.tgz', '.ttf',
  '.webp', '.woff', '.woff2', '.zip',
]);

export const FORBIDDEN_FILE_NAMES = new Set([
  '.env',
  '.netrc',
  '.npmrc',
  'accounts.json',
  'appsettings.json',
  'auth.json',
  'id_ed25519',
  'id_rsa',
  'pat-gateway-secret',
  'pat-gateway-control-v1',
  'quota-capacity-measurements.json',
  'quota-monitor-settings.json',
  'quota-probe-usage.json',
  'quota-snapshots.json',
  'token-metadata.json',
  'usage-account-switches.json',
]);

export const FORBIDDEN_CONTENT = [
  { name: 'OpenAI API Key', expression: /\bsk-[A-Za-z0-9_-]{20,}\b/ },
  { name: 'Business Access Token', expression: /\bat-[A-Za-z0-9._~-]{12,}\b/ },
  { name: 'JWT/Access Token', expression: /\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b/ },
  { name: 'Bearer Token', expression: /\bBearer\s+[A-Za-z0-9._~+/=-]{20,}\b/i },
  { name: 'PEM 私钥', expression: /-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----/ },
  { name: '带认证信息的 URL', expression: /\b[a-z][a-z0-9+.-]*:\/\/[^\s/:@]+:[^\s/@]+@[^\s/]+/i },
  { name: 'Windows 用户路径', expression: /[A-Z]:[\\/]Users[\\/][^\\/\s"']+/i },
  { name: 'macOS 用户路径', expression: /\/Users\/[^/\s"']+/ },
  { name: '本机构建路径', expression: /D:[\\/]GPT(?:[\\/]|\b)/i },
  { name: '电子邮箱', expression: /\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/i },
  {
    name: '硬编码 OAuth/API 凭据',
    expression: /\b(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key)\b["']?\s*[:=]\s*["'][A-Za-z0-9._~+/=-]{20,}["']/i,
  },
];

export function isForbiddenPrivateFile(filePath) {
  const name = path.basename(filePath).toLowerCase();
  return FORBIDDEN_FILE_NAMES.has(name) || name.startsWith('.env.') ||
    /\.(?:jsonl|key|p12|pfx|pem|sqlite|sqlite3)$/i.test(name);
}

export async function scanSensitiveTextFile(file, scope) {
  const details = await stat(file);
  const extension = path.extname(file).toLowerCase();
  if (KNOWN_BINARY_EXTENSIONS.has(extension)) return;
  if (details.size > MAX_SCANNABLE_BYTES) {
    throw new Error(`隐私扫描失败：${scope} ${file} 不是已知二进制格式且体积过大，无法安全扫描。`);
  }
  const bytes = await readFile(file);
  if (bytes.includes(0)) return;
  const content = bytes.toString('utf8');
  for (const rule of FORBIDDEN_CONTENT) {
    if (rule.expression.test(content)) {
      throw new Error(`隐私扫描失败：${scope} ${file} 命中“${rule.name}”。`);
    }
  }
}
