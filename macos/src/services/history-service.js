const fs = require('node:fs');
const fsp = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');

const UUID_PATTERN = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const UUID_EXACT = new RegExp(`^${UUID_PATTERN}$`, 'i');
const UUID_IN_TEXT = new RegExp(UUID_PATTERN, 'ig');
const HISTORY_ROOTS = Object.freeze([
  { name: 'sessions', archived: false },
  { name: 'archived_sessions', archived: true },
]);

const DEFAULT_MAX_FILES = 5_000;
const DEFAULT_MAX_MESSAGES = 80;
const DEFAULT_MAX_MESSAGE_CHARACTERS = 4_000;
const DEFAULT_MAX_JSON_LINE_BYTES = 512 * 1024;
const DEFAULT_SUMMARY_SOURCE_BYTES = 12 * 1024 * 1024;
const DEFAULT_TRANSCRIPT_SOURCE_BYTES = 32 * 1024 * 1024;
const SUMMARY_HEAD_BYTES = 2 * 1024 * 1024;
const MAX_SEARCH_CHARACTERS = 128 * 1024;

function clampInteger(value, minimum, maximum, fallback) {
  const number = Number(value);
  return Number.isFinite(number)
    ? Math.min(maximum, Math.max(minimum, Math.trunc(number)))
    : fallback;
}

function validateThreadId(value) {
  const threadId = String(value || '').trim().toLowerCase();
  if (!UUID_EXACT.test(threadId)) throw new Error('Codex 任务 ID 无效。');
  return threadId;
}

function normalizeSingleLine(value, maximum = 500) {
  const normalized = String(value || '').replace(/[\r\n\t]+/g, ' ').replace(/\s{2,}/g, ' ').trim();
  return limitText(redactSecrets(normalized), maximum).text;
}

function normalizeMessageText(value) {
  let normalized = String(value || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n').trim();
  while (normalized.includes('\n\n\n')) normalized = normalized.replace(/\n\n\n/g, '\n\n');
  return redactSecrets(normalized);
}

function redactSecrets(value) {
  return String(value || '')
    .replace(/("(?:(?:openai|codex|azure_openai)[_-])?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|token|api[_-]?key|authorization|client[_-]?secret|password)"\s*:\s*")[^"]*(")/gi, '$1[REDACTED]$2')
    .replace(/((?:(?:openai|codex|azure_openai)[_-])?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|token|api[_-]?key|authorization|client[_-]?secret|password)\s*[=:]\s*["']?)([^\s,;"']+)/gi, '$1[REDACTED]')
    .replace(/\bsk-[A-Za-z0-9_-]{12,}\b/g, '[REDACTED]')
    .replace(/\b(?:sess|pat|oai)-[A-Za-z0-9_-]{12,}\b/gi, '[REDACTED]')
    .replace(/\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g, '[REDACTED]')
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{12,}\b/gi, 'Bearer [REDACTED]')
    .replace(/-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----/gi, '[REDACTED PRIVATE KEY]')
    .replace(/([?&](?:code|state|code_verifier|refresh_token|access_token)=)[^&\s]+/gi, '$1[REDACTED]');
}

function limitText(value, maximum) {
  const text = String(value || '');
  if (text.length <= maximum) return { text, truncated: false };
  let end = Math.max(1, maximum - 1);
  const code = text.charCodeAt(end - 1);
  if (code >= 0xD800 && code <= 0xDBFF) end -= 1;
  return { text: `${text.slice(0, Math.max(1, end)).trimEnd()}…`, truncated: true };
}

function removeLeadingBlock(value, closingMarker) {
  const index = value.toLowerCase().indexOf(closingMarker.toLowerCase());
  return index < 0 ? '' : value.slice(index + closingMarker.length).trimStart();
}

function filterConversationText(role, value) {
  if (role !== 'user') return normalizeMessageText(value);
  let text = String(value || '').trim();
  if (text.toLowerCase().startsWith(
    'another language model started to solve this problem and produced a summary of its thinking process.',
  )) return '';

  let changed = true;
  while (changed && text) {
    changed = false;
    const trimmed = text.trimStart();
    if (/^# Files mentioned by the user:/i.test(trimmed)) {
      const marker = '## My request for Codex:';
      const requestIndex = trimmed.toLowerCase().indexOf(marker.toLowerCase());
      if (requestIndex >= 0) {
        text = trimmed.slice(requestIndex + marker.length).trimStart();
        changed = true;
        continue;
      }
    }
    if (/^# AGENTS\.md instructions/i.test(trimmed)) {
      text = removeLeadingBlock(trimmed, '</INSTRUCTIONS>');
      changed = true;
      continue;
    }
    for (const tag of [
      'INSTRUCTIONS',
      'environment_context',
      'permissions instructions',
      'app-context',
      'collaboration_mode',
      'skills_instructions',
      'apps_instructions',
      'plugins_instructions',
      'multi_agent_mode',
      'turn_aborted',
    ]) {
      if (!trimmed.toLowerCase().startsWith(`<${tag.toLowerCase()}`)) continue;
      text = removeLeadingBlock(trimmed, `</${tag}>`);
      changed = true;
      break;
    }
  }
  return normalizeMessageText(text);
}

function readTextValue(value) {
  if (typeof value === 'string') return value;
  if (!value || typeof value !== 'object') return '';
  if (!Array.isArray(value)) return typeof value.text === 'string' ? value.text : '';
  const parts = [];
  for (const item of value) {
    if (typeof item === 'string') {
      if (item.trim()) parts.push(item);
      continue;
    }
    if (!item || typeof item !== 'object' || typeof item.text !== 'string') continue;
    const type = String(item.type || '').toLowerCase();
    if (!type || type === 'text' || type === 'input_text' || type === 'output_text') parts.push(item.text);
  }
  return parts.join('\n');
}

function readMessageText(value) {
  if (!value || typeof value !== 'object') return '';
  for (const key of ['message', 'text', 'content']) {
    const text = readTextValue(value[key]);
    if (text.trim()) return text;
  }
  return '';
}

function parseTimestamp(root, payload) {
  const parsed = Date.parse(root?.timestamp || payload?.timestamp || '');
  return Number.isFinite(parsed) ? parsed : null;
}

function parseMessage(record, sequence, maxMessageCharacters) {
  if (!record || typeof record !== 'object') return null;
  const payload = record.payload && typeof record.payload === 'object' ? record.payload : null;
  const type = String(record.type || '').toLowerCase();
  let role = '';
  let text = '';
  let priority = 0;

  if (type === 'response_item' && String(payload?.type || '').toLowerCase() === 'message') {
    role = String(payload.role || '').toLowerCase();
    text = readMessageText(payload);
    priority = 2;
  } else if (type === 'event_msg') {
    const eventType = String(payload?.type || '').toLowerCase();
    if (eventType === 'user_message') role = 'user';
    else if (eventType === 'agent_message' || eventType === 'assistant_message') role = 'assistant';
    else return null;
    text = readMessageText(payload);
    priority = 1;
  } else if (type === 'message') {
    role = String(record.role || '').toLowerCase();
    text = readMessageText(record);
  } else {
    return null;
  }

  if (role !== 'user' && role !== 'assistant') return null;
  text = filterConversationText(role, text);
  if (!text) return null;
  const limited = limitText(text, maxMessageCharacters);
  return {
    role,
    text: limited.text,
    timestampMs: parseTimestamp(record, payload),
    sequence,
    priority,
    textTruncated: limited.truncated,
  };
}

function parseMetadata(record) {
  if (String(record?.type || '').toLowerCase() !== 'session_meta') return null;
  const payload = record?.payload && typeof record.payload === 'object' ? record.payload : {};
  const source = payload.source;
  const sourceText = typeof source === 'string' ? source.toLowerCase() : '';
  const threadSource = String(payload.thread_source || '').toLowerCase();
  return {
    id: UUID_EXACT.test(String(payload.id || '')) ? String(payload.id).toLowerCase() : '',
    workingDirectory: normalizeSingleLine(payload.cwd || payload.working_directory || '', 1_000),
    provider: normalizeSingleLine(payload.model_provider || payload.provider || '', 120),
    timestampMs: parseTimestamp(record, payload),
    subagent: threadSource === 'subagent' || sourceText === 'subagent' ||
      Boolean(source && typeof source === 'object' && source.subagent) ||
      Boolean(payload.agent_path),
  };
}

function filenameThreadId(filePath) {
  const matches = path.basename(filePath, path.extname(filePath)).match(UUID_IN_TEXT);
  return matches?.length ? matches[matches.length - 1].toLowerCase() : '';
}

function sameMessage(left, right) {
  if (left.role !== right.role || left.text !== right.text) return false;
  if (left.timestampMs !== null && right.timestampMs !== null) {
    return Math.abs(right.timestampMs - left.timestampMs) <= 3_000;
  }
  return Math.abs(right.sequence - left.sequence) <= 3;
}

function deduplicateMessages(messages) {
  const ordered = [...messages].sort((left, right) => {
    const leftTime = left.timestampMs ?? Number.MIN_SAFE_INTEGER;
    const rightTime = right.timestampMs ?? Number.MIN_SAFE_INTEGER;
    return leftTime - rightTime || left.sequence - right.sequence;
  });
  const result = [];
  for (const message of ordered) {
    const previous = result[result.length - 1];
    if (previous && sameMessage(previous, message)) {
      if (message.priority > previous.priority) result[result.length - 1] = message;
    } else {
      result.push(message);
    }
  }
  return result;
}

async function* readBoundedSegment(filePath, start, end, options = {}) {
  const maxLineBytes = options.maxLineBytes || DEFAULT_MAX_JSON_LINE_BYTES;
  const discardInitialLine = start > 0;
  const keepFinalLine = Boolean(options.keepFinalLine);
  const stream = fs.createReadStream(filePath, { start, end });
  let parts = [];
  let length = 0;
  let oversized = false;
  let discard = discardInitialLine;

  const append = (part) => {
    if (oversized || part.length === 0) return;
    if (length + part.length > maxLineBytes) {
      parts = [];
      length = 0;
      oversized = true;
      return;
    }
    parts.push(part);
    length += part.length;
  };

  for await (const chunk of stream) {
    let offset = 0;
    while (offset < chunk.length) {
      const newline = chunk.indexOf(0x0A, offset);
      if (newline < 0) {
        append(chunk.subarray(offset));
        break;
      }
      append(chunk.subarray(offset, newline));
      if (discard) {
        discard = false;
      } else if (oversized) {
        yield { text: null, oversized: true };
      } else {
        let line = parts.length === 1 ? parts[0] : Buffer.concat(parts, length);
        if (line.length && line[line.length - 1] === 0x0D) line = line.subarray(0, line.length - 1);
        yield { text: line.toString('utf8'), oversized: false };
      }
      parts = [];
      length = 0;
      oversized = false;
      offset = newline + 1;
    }
  }

  if (keepFinalLine && !discard && (length || oversized)) {
    if (oversized) yield { text: null, oversized: true };
    else {
      let line = parts.length === 1 ? parts[0] : Buffer.concat(parts, length);
      if (line.length && line[line.length - 1] === 0x0D) line = line.subarray(0, line.length - 1);
      yield { text: line.toString('utf8'), oversized: false };
    }
  }
}

async function parseRolloutFile(file, options = {}) {
  const maxSourceBytes = clampInteger(
    options.maxSourceBytes,
    128,
    128 * 1024 * 1024,
    DEFAULT_SUMMARY_SOURCE_BYTES,
  );
  const maxMessageCharacters = clampInteger(
    options.maxMessageCharacters,
    80,
    12_000,
    DEFAULT_MAX_MESSAGE_CHARACTERS,
  );
  const transcriptOnly = Boolean(options.transcriptOnly);
  const size = file.stat.size;
  const segments = [];
  let sourceTruncated = size > maxSourceBytes;
  if (size <= maxSourceBytes) {
    segments.push({ start: 0, end: Math.max(0, size - 1), keepFinalLine: true });
  } else if (transcriptOnly) {
    segments.push({ start: size - maxSourceBytes, end: size - 1, keepFinalLine: true });
  } else {
    const headBytes = Math.min(SUMMARY_HEAD_BYTES, Math.floor(maxSourceBytes / 2));
    segments.push({ start: 0, end: headBytes - 1, keepFinalLine: false });
    segments.push({ start: size - (maxSourceBytes - headBytes), end: size - 1, keepFinalLine: true });
  }

  let metadata = null;
  let sequence = 0;
  let malformedLines = 0;
  let oversizedLines = 0;
  let textTruncated = false;
  let model = '';
  let latestTimestamp = Number.isFinite(file.stat.mtimeMs) ? file.stat.mtimeMs : 0;
  const messages = [];

  if (size > 0) {
    for (const segment of segments) {
      for await (const line of readBoundedSegment(file.path, segment.start, segment.end, {
        maxLineBytes: options.maxJsonLineBytes || DEFAULT_MAX_JSON_LINE_BYTES,
        keepFinalLine: segment.keepFinalLine,
      })) {
        if (line.oversized) {
          oversizedLines += 1;
          continue;
        }
        if (!line.text?.trim()) continue;
        sequence += 1;
        let record;
        try {
          record = JSON.parse(line.text);
        } catch {
          malformedLines += 1;
          continue;
        }
        const parsedMetadata = parseMetadata(record);
        if (parsedMetadata) {
          if (!metadata) metadata = parsedMetadata;
          else {
            metadata.subagent ||= parsedMetadata.subagent;
            metadata.id ||= parsedMetadata.id;
            metadata.workingDirectory ||= parsedMetadata.workingDirectory;
            metadata.provider ||= parsedMetadata.provider;
          }
          if (parsedMetadata.timestampMs !== null) latestTimestamp = Math.max(latestTimestamp, parsedMetadata.timestampMs);
        }
        if (String(record?.type || '').toLowerCase() === 'turn_context') {
          model = normalizeSingleLine(record?.payload?.model || model, 120);
        }
        const message = parseMessage(record, sequence, maxMessageCharacters);
        if (!message) continue;
        textTruncated ||= message.textTruncated;
        if (message.timestampMs !== null) latestTimestamp = Math.max(latestTimestamp, message.timestampMs);
        messages.push(message);
      }
    }
  }

  const fileId = filenameThreadId(file.path);
  if (metadata?.id && fileId && metadata.id !== fileId) {
    return { ignored: true, reason: 'thread-id-mismatch' };
  }
  const id = metadata?.id || fileId;
  if (!id || metadata?.subagent) return { ignored: true, reason: metadata?.subagent ? 'subagent' : 'missing-id' };

  const deduplicated = deduplicateMessages(messages);
  return {
    id,
    archived: file.archived,
    workingDirectory: metadata?.workingDirectory || '',
    provider: metadata?.provider || '',
    model,
    updatedAt: new Date(Math.max(0, latestTimestamp)).toISOString(),
    messages: deduplicated,
    malformedLines,
    oversizedLines,
    textTruncated,
    sourceTruncated,
    sourcePath: file.path,
    sourceRoot: file.rootReal,
    stat: file.stat,
  };
}

function isInsideDirectory(candidate, root) {
  const relative = path.relative(root, candidate);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

async function readableRoot(homeReal, name) {
  const candidate = path.join(homeReal, name);
  let details;
  try {
    details = await fsp.lstat(candidate);
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
  if (!details.isDirectory() || details.isSymbolicLink()) return null;
  const real = await fsp.realpath(candidate);
  return isInsideDirectory(real, homeReal) ? { path: candidate, real } : null;
}

async function collectSessionFiles(homeReal, maxFiles) {
  const files = [];
  const canonical = new Set();
  let truncated = false;
  let ignoredEntries = 0;

  for (const descriptor of HISTORY_ROOTS) {
    const root = await readableRoot(homeReal, descriptor.name);
    if (!root) continue;
    const pending = [{ directory: root.path, depth: 0 }];
    while (pending.length) {
      const current = pending.pop();
      let entries;
      try {
        entries = await fsp.readdir(current.directory, { withFileTypes: true });
      } catch {
        ignoredEntries += 1;
        continue;
      }
      for (const entry of entries) {
        if (files.length >= maxFiles) {
          truncated = true;
          break;
        }
        const fullPath = path.join(current.directory, entry.name);
        if (entry.isSymbolicLink()) {
          ignoredEntries += 1;
          continue;
        }
        if (entry.isDirectory()) {
          if (current.depth < 12) pending.push({ directory: fullPath, depth: current.depth + 1 });
          else ignoredEntries += 1;
          continue;
        }
        if (!entry.isFile() || path.extname(entry.name).toLowerCase() !== '.jsonl') continue;
        try {
          const details = await fsp.lstat(fullPath);
          if (!details.isFile() || details.isSymbolicLink()) {
            ignoredEntries += 1;
            continue;
          }
          const real = await fsp.realpath(fullPath);
          if (!isInsideDirectory(real, root.real) || canonical.has(real)) {
            ignoredEntries += 1;
            continue;
          }
          canonical.add(real);
          files.push({
            path: fullPath,
            real,
            rootReal: root.real,
            rootName: descriptor.name,
            archived: descriptor.archived,
            stat: details,
          });
        } catch {
          ignoredEntries += 1;
        }
      }
      if (truncated) break;
    }
    if (truncated) break;
  }
  return { files, truncated, ignoredEntries };
}

function publicThread(parsed) {
  const firstUser = parsed.messages.find((message) => message.role === 'user');
  const latestMessage = parsed.messages[parsed.messages.length - 1];
  const title = normalizeSingleLine(firstUser?.text || '未命名任务', 140) || '未命名任务';
  const preview = normalizeSingleLine(latestMessage?.text || firstUser?.text || '', 280);
  return {
    id: parsed.id,
    title,
    preview,
    workingDirectory: parsed.workingDirectory,
    model: parsed.model,
    provider: parsed.provider,
    updatedAt: parsed.updatedAt,
    archived: parsed.archived,
    hasUserEvent: Boolean(firstUser),
    messageCount: parsed.messages.length,
  };
}

function buildSearchText(parsed, thread) {
  let result = `${thread.title}\n${thread.preview}\n${thread.workingDirectory}\n${thread.model}\n${thread.provider}`;
  for (const message of parsed.messages) {
    if (result.length >= MAX_SEARCH_CHARACTERS) break;
    result += `\n${message.text.slice(0, MAX_SEARCH_CHARACTERS - result.length)}`;
  }
  return result.toLocaleLowerCase();
}

function preferThread(next, current) {
  if (!current) return true;
  const nextTime = Date.parse(next.thread.updatedAt) || 0;
  const currentTime = Date.parse(current.thread.updatedAt) || 0;
  if (nextTime !== currentTime) return nextTime > currentTime;
  if (next.thread.archived !== current.thread.archived) return !next.thread.archived;
  return false;
}

async function ensureSafeDirectory(homeReal, segments) {
  let current = homeReal;
  for (const segment of segments) {
    if (!/^[A-Za-z0-9_-]+$/.test(segment)) throw new Error('目标会话目录格式无效。');
    const next = path.join(current, segment);
    try {
      const details = await fsp.lstat(next);
      if (!details.isDirectory() || details.isSymbolicLink()) throw new Error('目标会话目录不是安全的真实目录。');
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      await fsp.mkdir(next, { mode: 0o700 });
    }
    const real = await fsp.realpath(next);
    if (!isInsideDirectory(real, homeReal)) throw new Error('目标会话目录解析到了 CODEX_HOME 之外。');
    current = real;
  }
  return current;
}

function archiveDate(parsed) {
  const match = path.basename(parsed.sourcePath).match(/^rollout-(\d{4})-(\d{2})-(\d{2})T/i);
  if (match) return match.slice(1, 4);
  const date = new Date(parsed.updatedAt);
  if (!Number.isFinite(date.getTime())) throw new Error('无法确定会话的原始日期目录。');
  return [
    String(date.getUTCFullYear()).padStart(4, '0'),
    String(date.getUTCMonth() + 1).padStart(2, '0'),
    String(date.getUTCDate()).padStart(2, '0'),
  ];
}

function commandFailure(result, action) {
  if (result === false || (result && Number.isInteger(result.code) && result.code !== 0) ||
      (result && result.ok === false)) {
    const detail = limitText(redactSecrets(result?.stderr || result?.stdout || result?.message || ''), 1_000).text;
    throw new Error(`${action}失败${detail ? `：${detail}` : '。'}`);
  }
}

class HistoryService {
  constructor(options = {}) {
    this.defaultCodexHome = path.resolve(options.defaultCodexHome || path.join(os.homedir(), '.codex'));
    const allowed = Array.isArray(options.allowedCodexHomes) && options.allowedCodexHomes.length
      ? options.allowedCodexHomes
      : [this.defaultCodexHome];
    this.allowedCodexHomes = new Set(allowed.map((item) => path.resolve(String(item))));
    this.commandRunner = typeof options.commandRunner === 'function' ? options.commandRunner : null;
    this.maxFiles = clampInteger(options.maxFiles, 1, 20_000, DEFAULT_MAX_FILES);
  }

  async resolveHome(candidate, { mustExist = false } = {}) {
    const requested = path.resolve(String(candidate || this.defaultCodexHome));
    if (!this.allowedCodexHomes.has(requested)) throw new Error('拒绝访问未授权的 CODEX_HOME。');
    try {
      const details = await fsp.lstat(requested);
      if (!details.isDirectory()) throw new Error('CODEX_HOME 不是目录。');
      return await fsp.realpath(requested);
    } catch (error) {
      if (error.code === 'ENOENT' && !mustExist) return requested;
      throw error;
    }
  }

  async listThreads(input = {}) {
    const home = await this.resolveHome(input.codexHome);
    const includeArchived = input.includeArchived !== false;
    const limit = clampInteger(input.limit, 1, 10_000, 500);
    const query = normalizeSingleLine(input.query || '', 500).toLocaleLowerCase();
    const scan = await collectSessionFiles(home, this.maxFiles);
    const indexed = new Map();
    let ignoredFiles = scan.ignoredEntries;

    for (const file of scan.files) {
      try {
        const parsed = await parseRolloutFile(file, {
          maxSourceBytes: DEFAULT_SUMMARY_SOURCE_BYTES,
          maxMessageCharacters: 12_000,
        });
        if (parsed.ignored || !parsed.messages.some((message) => message.role === 'user')) {
          ignoredFiles += 1;
          continue;
        }
        if (!includeArchived && parsed.archived) continue;
        const thread = publicThread(parsed);
        const candidate = { thread, searchText: buildSearchText(parsed, thread) };
        if (preferThread(candidate, indexed.get(thread.id))) indexed.set(thread.id, candidate);
      } catch {
        ignoredFiles += 1;
      }
    }

    let values = [...indexed.values()];
    if (query) values = values.filter((item) => item.searchText.includes(query));
    values.sort((left, right) => (Date.parse(right.thread.updatedAt) || 0) - (Date.parse(left.thread.updatedAt) || 0));
    const resultTruncated = values.length > limit;
    return {
      threads: values.slice(0, limit).map((item) => item.thread),
      scannedFiles: scan.files.length,
      ignoredFiles,
      truncated: scan.truncated || resultTruncated,
    };
  }

  async getHistory(input = {}) {
    return this.listThreads(input);
  }

  async searchHistory(input = {}) {
    return this.listThreads(input);
  }

  async findThreadFiles(home, threadId) {
    const scan = await collectSessionFiles(home, this.maxFiles);
    const matches = [];
    for (const file of scan.files) {
      const idFromName = filenameThreadId(file.path);
      if (idFromName && idFromName !== threadId) continue;
      try {
        const parsed = await parseRolloutFile(file, {
          maxSourceBytes: Math.min(DEFAULT_SUMMARY_SOURCE_BYTES, 4 * 1024 * 1024),
          maxMessageCharacters: DEFAULT_MAX_MESSAGE_CHARACTERS,
        });
        if (!parsed.ignored && parsed.id === threadId) matches.push(parsed);
      } catch {
        // A live file can rotate between discovery and parsing.
      }
    }
    return matches.sort((left, right) => (Date.parse(right.updatedAt) || 0) - (Date.parse(left.updatedAt) || 0));
  }

  async readThread(input = {}) {
    let threadId;
    try {
      threadId = validateThreadId(input.threadId);
    } catch (error) {
      return {
        status: 'unavailable', messages: [], isTruncated: false,
        ignoredMalformedLines: 0, ignoredOversizedLines: 0, notice: error.message,
      };
    }
    let home;
    try {
      home = await this.resolveHome(input.codexHome);
      const matches = await this.findThreadFiles(home, threadId);
      if (!matches.length) {
        return {
          status: 'source_missing', messages: [], isTruncated: false,
          ignoredMalformedLines: 0, ignoredOversizedLines: 0,
          notice: '找不到这条聊天的本地会话文件。',
        };
      }
      const file = matches[0];
      const parsed = await parseRolloutFile({
        path: file.sourcePath,
        rootReal: file.sourceRoot,
        archived: file.archived,
        stat: await fsp.lstat(file.sourcePath),
      }, {
        transcriptOnly: true,
        maxSourceBytes: DEFAULT_TRANSCRIPT_SOURCE_BYTES,
        maxMessageCharacters: clampInteger(
          input.maxMessageCharacters,
          80,
          12_000,
          DEFAULT_MAX_MESSAGE_CHARACTERS,
        ),
      });
      if (parsed.ignored) throw new Error('本地会话文件的任务标识不一致。');
      const maxMessages = clampInteger(input.maxMessages, 1, 200, DEFAULT_MAX_MESSAGES);
      const messageLimitReached = parsed.messages.length > maxMessages;
      const messages = parsed.messages.slice(-maxMessages).map((message) => ({
        role: message.role,
        text: message.text,
        timestamp: message.timestampMs === null ? null : new Date(message.timestampMs).toISOString(),
      }));
      const isTruncated = parsed.sourceTruncated || parsed.textTruncated ||
        parsed.oversizedLines > 0 || messageLimitReached;
      return {
        status: messages.length ? 'available' : 'empty',
        messages,
        isTruncated,
        ignoredMalformedLines: parsed.malformedLines,
        ignoredOversizedLines: parsed.oversizedLines,
        notice: messages.length
          ? (isTruncated ? `已显示最近 ${messages.length} 条简版消息，过长或过早内容已安全省略。` : `已读取 ${messages.length} 条简版消息。`)
          : '会话文件存在，但没有可显示的用户或助手正文。',
      };
    } catch {
      return {
        status: 'unavailable', messages: [], isTruncated: false,
        ignoredMalformedLines: 0, ignoredOversizedLines: 0,
        notice: '暂时无法读取这条聊天的本地会话文件。',
      };
    }
  }

  async setThreadArchived(input = {}) {
    const threadId = validateThreadId(input.threadId);
    if (typeof input.archived !== 'boolean') throw new Error('归档状态必须是布尔值。');
    const home = await this.resolveHome(input.codexHome, { mustExist: true });
    const action = input.archived ? '归档任务' : '取消归档任务';
    if (this.commandRunner) {
      const result = await this.commandRunner({
        codexHome: home,
        args: [input.archived ? 'archive' : 'unarchive', threadId],
      });
      commandFailure(result, action);
      return { ok: true, threadId, archived: input.archived, changed: true, via: 'codex' };
    }

    const matches = await this.findThreadFiles(home, threadId);
    if (matches.some((item) => item.archived === input.archived) &&
        !matches.some((item) => item.archived !== input.archived)) {
      return { ok: true, threadId, archived: input.archived, changed: false, via: 'filesystem' };
    }
    const sources = matches.filter((item) => item.archived !== input.archived);
    if (!sources.length) throw new Error('找不到要归档的本地会话文件。');
    if (sources.length !== 1) throw new Error('发现多个同 ID 会话文件，拒绝执行有歧义的归档操作。');
    const source = sources[0];
    const details = await fsp.lstat(source.sourcePath);
    if (!details.isFile() || details.isSymbolicLink()) throw new Error('源会话文件不是安全的真实文件。');
    const realSource = await fsp.realpath(source.sourcePath);
    if (!isInsideDirectory(realSource, source.sourceRoot)) throw new Error('源会话文件解析到了历史目录之外。');

    const destinationDirectory = input.archived
      ? await ensureSafeDirectory(home, ['archived_sessions'])
      : await ensureSafeDirectory(home, ['sessions', ...archiveDate(source)]);
    const destination = path.join(destinationDirectory, path.basename(source.sourcePath));
    try {
      await fsp.lstat(destination);
      throw new Error('目标会话文件已经存在，拒绝覆盖。');
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
    await fsp.copyFile(realSource, destination, fs.constants.COPYFILE_EXCL);
    try {
      await fsp.unlink(realSource);
    } catch (error) {
      try { await fsp.unlink(destination); } catch { /* preserve the original on rollback failure */ }
      throw error;
    }
    return { ok: true, threadId, archived: input.archived, changed: true, via: 'filesystem' };
  }

  async deleteThread(input = {}) {
    const threadId = validateThreadId(input.threadId);
    const home = await this.resolveHome(input.codexHome, { mustExist: true });
    if (this.commandRunner) {
      const result = await this.commandRunner({ codexHome: home, args: ['delete', '--force', threadId] });
      commandFailure(result, '永久删除任务');
      return { ok: true, threadId, deletedFiles: null, via: 'codex' };
    }

    const matches = await this.findThreadFiles(home, threadId);
    let deletedFiles = 0;
    for (const source of matches) {
      const details = await fsp.lstat(source.sourcePath);
      if (!details.isFile() || details.isSymbolicLink()) throw new Error('会话文件不是安全的真实文件。');
      const real = await fsp.realpath(source.sourcePath);
      if (!isInsideDirectory(real, source.sourceRoot)) throw new Error('会话文件解析到了历史目录之外。');
      await fsp.unlink(real);
      deletedFiles += 1;
    }
    return { ok: true, threadId, deletedFiles, via: 'filesystem' };
  }
}

module.exports = {
  HistoryService,
  redactSecrets,
  _test: {
    collectSessionFiles,
    deduplicateMessages,
    filenameThreadId,
    filterConversationText,
    parseMessage,
    parseRolloutFile,
    validateThreadId,
  },
};
