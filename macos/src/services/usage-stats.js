const fs = require('node:fs');
const fsp = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const readline = require('node:readline');

const ALLOWED_RANGES = new Set(['today', '7d', '30d', 'all']);
const COLORS = ['#7967ff', '#2ec5ce', '#ff7a90', '#ffb44a', '#4f8cff', '#42cf8d', '#b86cff'];
const DEFAULT_REFRESH_INTERVAL_MS = 15_000;

function accountIdentity(account, index = 0) {
  const id = String(account?.id || '').trim();
  if (id) return id;
  const codexHome = String(account?.codexHome || '').trim();
  if (codexHome) return `home:${path.resolve(codexHome)}`;
  return `account:${index}:${String(account?.name || '').trim().toLowerCase()}`;
}

function resolveUsageArguments(rangeOrOptions, maybeOptions) {
  if (rangeOrOptions && typeof rangeOrOptions === 'object' && !Array.isArray(rangeOrOptions)) {
    return {
      range: normalizeRange(rangeOrOptions.range),
      options: { ...rangeOrOptions },
    };
  }
  return {
    range: normalizeRange(rangeOrOptions),
    options: maybeOptions && typeof maybeOptions === 'object' ? { ...maybeOptions } : {},
  };
}

// Official API rates are USD per million:
// [regular input, cached input, cache write, output].
const PRICE = {
  sol: [5, 0.5, 6.25, 30],
  terra: [2, 0.2, 2.5, 12],
  luna: [0.2, 0.02, 0.25, 1.2],
  gpt55: [5, 0.5, 5, 30],
  gpt54: [2.5, 0.25, 2.5, 15],
  // The supplied sub2api CSV bills the gpt-5.4-mini wire alias as sol.
  gpt54mini: [5, 0.5, 5, 30],
  gpt54nano: [0.2, 0.02, 0.2, 1.25],
  codex: [1.75, 0.175, 1.75, 14],
};
const LONG_CONTEXT_INPUT_THRESHOLD = 272_000;
const LONG_CONTEXT_PRICE = {
  terra: [4, 0.4, 5, 18],
  luna: [0.4, 0.04, 0.5, 1.8],
};

function nonNegativeInteger(value) {
  const number = Number(value);
  return Number.isFinite(number) && number >= 0 ? Math.trunc(number) : null;
}

function numeric(value) {
  return nonNegativeInteger(value) ?? 0;
}

function readOptionalUsageNumber(usage, names) {
  for (const name of names) {
    if (usage?.[name] === undefined || usage?.[name] === null) continue;
    const value = nonNegativeInteger(usage[name]);
    if (value !== null) return value;
  }
  return null;
}

function readUsageNumber(usage, names) {
  return readOptionalUsageNumber(usage, names) ?? 0;
}

function readInputTokens(usage) {
  return readUsageNumber(usage, ['input_tokens', 'inputTokens', 'prompt_tokens', 'promptTokens']);
}

function readCachedInputTokens(usage) {
  const flattened = readOptionalUsageNumber(usage, ['cached_input_tokens', 'cachedInputTokens']);
  if (flattened !== null) return flattened;
  const details = [
    usage?.input_tokens_details,
    usage?.input_token_details,
    usage?.prompt_tokens_details,
    usage?.inputTokensDetails,
    usage?.promptTokensDetails,
  ];
  for (const value of details) {
    const nested = readOptionalUsageNumber(value, ['cached_tokens', 'cachedTokens']);
    if (nested !== null) return nested;
  }
  return 0;
}

function readOutputTokens(usage) {
  return readUsageNumber(usage, ['output_tokens', 'outputTokens', 'completion_tokens', 'completionTokens']);
}

function readReasoningOutputTokens(usage) {
  const flattened = readOptionalUsageNumber(usage, ['reasoning_output_tokens', 'reasoningOutputTokens']);
  if (flattened !== null) return flattened;
  for (const details of [
    usage?.output_tokens_details,
    usage?.completion_tokens_details,
    usage?.outputTokensDetails,
    usage?.completionTokensDetails,
  ]) {
    const nested = readOptionalUsageNumber(details, ['reasoning_tokens', 'reasoningTokens']);
    if (nested !== null) return nested;
  }
  return 0;
}

function extractCacheWrite(usage, inputTokens = readInputTokens(usage), cachedInputTokens = readCachedInputTokens(usage)) {
  const normalizedInput = numeric(inputTokens);
  const normalizedCached = Math.min(normalizedInput, numeric(cachedInputTokens));
  const maximum = normalizedInput - normalizedCached;
  const candidates = [
    usage?.cache_write_tokens,
    usage?.cache_write_input_tokens,
    usage?.cache_creation_input_tokens,
    usage?.input_tokens_details?.cache_write_tokens,
    usage?.input_token_details?.cache_write_tokens,
    usage?.prompt_tokens_details?.cache_write_tokens,
    usage?.inputTokensDetails?.cacheWriteTokens,
    usage?.promptTokensDetails?.cacheWriteTokens,
  ];
  for (const candidate of candidates) {
    if (candidate === undefined || candidate === null) continue;
    const value = nonNegativeInteger(candidate);
    if (value === null || value > maximum) return null;
    return value;
  }
  return null;
}

function parseUsageSnapshot(usage) {
  if (!usage || typeof usage !== 'object') return null;
  const inputTokens = readInputTokens(usage);
  const cachedInputTokens = Math.min(inputTokens, readCachedInputTokens(usage));
  const outputTokens = readOutputTokens(usage);
  const reasoningOutputTokens = readReasoningOutputTokens(usage);
  const explicitTotal = readOptionalUsageNumber(usage, ['total_tokens', 'totalTokens']);
  return {
    inputTokens,
    cachedInputTokens,
    cacheWriteTokens: extractCacheWrite(usage, inputTokens, cachedInputTokens),
    outputTokens,
    reasoningOutputTokens,
    totalTokens: explicitTotal ?? (inputTokens + outputTokens),
  };
}

function usageSignature(usage) {
  return JSON.stringify([
    usage.inputTokens,
    usage.cachedInputTokens,
    usage.cacheWriteTokens,
    usage.outputTokens,
    usage.reasoningOutputTokens,
    usage.totalTokens,
  ]);
}

function cumulativeDelta(previous, current) {
  const baseline = previous || {
    inputTokens: 0,
    cachedInputTokens: 0,
    cacheWriteTokens: 0,
    outputTokens: 0,
    reasoningOutputTokens: 0,
    totalTokens: 0,
  };
  if (current.inputTokens < baseline.inputTokens ||
      current.cachedInputTokens < baseline.cachedInputTokens ||
      (previous && previous.cacheWriteTokens !== null && current.cacheWriteTokens !== null &&
       current.cacheWriteTokens < previous.cacheWriteTokens) ||
      current.outputTokens < baseline.outputTokens ||
      current.reasoningOutputTokens < baseline.reasoningOutputTokens ||
      current.totalTokens < baseline.totalTokens) {
    return null;
  }
  const cacheWriteTokens = current.cacheWriteTokens !== null &&
    (!previous || previous.cacheWriteTokens !== null)
    ? current.cacheWriteTokens - (previous?.cacheWriteTokens ?? 0)
    : null;
  return normalizeUsageEvent({
    inputTokens: current.inputTokens - baseline.inputTokens,
    cachedInputTokens: current.cachedInputTokens - baseline.cachedInputTokens,
    cacheWriteTokens,
    outputTokens: current.outputTokens - baseline.outputTokens,
    reasoningOutputTokens: current.reasoningOutputTokens - baseline.reasoningOutputTokens,
    totalTokens: current.totalTokens - baseline.totalTokens,
  });
}

function priceKey(model) {
  const value = String(model || '').trim().toLowerCase();
  if (value.includes('gpt-5.6-sol') || value === 'gpt-5.6') return 'sol';
  if (value.includes('gpt-5.6-terra')) return 'terra';
  if (value.includes('gpt-5.6-luna')) return 'luna';
  if (value.includes('gpt-5.5') || value.includes('chat-latest')) return 'gpt55';
  if (value.includes('gpt-5.4-mini')) return 'gpt54mini';
  if (value.includes('gpt-5.4-nano')) return 'gpt54nano';
  if (value.includes('gpt-5.4')) return 'gpt54';
  if (value.includes('codex')) return 'codex';
  return null;
}

function normalizedModel(model) {
  const value = String(model || '').trim();
  if (!value) return '未知模型';
  const lower = value.toLowerCase();
  for (const known of [
    'gpt-5.6-sol',
    'gpt-5.6-terra',
    'gpt-5.6-luna',
    'gpt-5.5',
    'gpt-5.4-mini',
    'gpt-5.4-nano',
    'gpt-5.4',
  ]) {
    if (lower.includes(known)) return known;
  }
  return value.slice(0, 80);
}

function normalizeUsageEvent(event) {
  const inputTokens = numeric(event?.inputTokens);
  const cachedInputTokens = Math.min(inputTokens, numeric(event?.cachedInputTokens));
  const candidateCacheWrite = event?.cacheWriteTokens === null || event?.cacheWriteTokens === undefined
    ? null
    : nonNegativeInteger(event.cacheWriteTokens);
  const cacheWriteTokens = candidateCacheWrite !== null &&
    candidateCacheWrite <= inputTokens - cachedInputTokens
    ? candidateCacheWrite
    : null;
  const outputTokens = numeric(event?.outputTokens);
  const reasoningOutputTokens = numeric(event?.reasoningOutputTokens);
  const explicitTotal = event?.totalTokens === undefined || event?.totalTokens === null
    ? null
    : nonNegativeInteger(event.totalTokens);
  return {
    ...event,
    inputTokens,
    cachedInputTokens,
    cacheWriteTokens,
    outputTokens,
    reasoningOutputTokens,
    totalTokens: explicitTotal ?? (inputTokens + outputTokens),
  };
}

function hasTokenUsage(event) {
  return event.totalTokens > 0 || event.inputTokens > 0 || event.outputTokens > 0;
}

function eventCost(rawEvent) {
  const event = normalizeUsageEvent(rawEvent);
  if (!hasTokenUsage(event)) return 0;
  const key = priceKey(event.model);
  if (!key) return null;
  const input = event.inputTokens;
  const cached = event.cachedInputTokens;
  const cacheWrite = event.cacheWriteTokens ?? 0;
  const regular = input - cached - cacheWrite;
  const rate = input > LONG_CONTEXT_INPUT_THRESHOLD && LONG_CONTEXT_PRICE[key]
    ? LONG_CONTEXT_PRICE[key]
    : PRICE[key];
  return (regular * rate[0] + cached * rate[1] + cacheWrite * rate[2] + event.outputTokens * rate[3]) / 1_000_000;
}

async function collectJsonlFiles(root, output, seen) {
  if (!root) return;
  let stat;
  try {
    stat = await fsp.stat(root);
  } catch {
    return;
  }
  if (!stat.isDirectory()) return;
  const entries = await fsp.readdir(root, { withFileTypes: true });
  await Promise.all(entries.map(async (entry) => {
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) await collectJsonlFiles(fullPath, output, seen);
    else if (entry.isFile() && entry.name.endsWith('.jsonl')) {
      let canonical = fullPath;
      try { canonical = await fsp.realpath(fullPath); } catch { /* use original */ }
      if (!seen.has(canonical)) {
        seen.add(canonical);
        output.push(fullPath);
      }
    }
  }));
}

async function collectOwnedJsonlFiles(root, accountId, output, seen) {
  if (!root) return;
  let stat;
  try {
    stat = await fsp.stat(root);
  } catch {
    return;
  }
  if (!stat.isDirectory()) return;
  const entries = await fsp.readdir(root, { withFileTypes: true });
  await Promise.all(entries.map(async (entry) => {
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) await collectOwnedJsonlFiles(fullPath, accountId, output, seen);
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

function parseTimestamp(value) {
  const timestamp = Date.parse(value || '');
  return Number.isFinite(timestamp) ? timestamp : null;
}

async function parseSession(filePath, fromTimestamp, addEvent) {
  const stream = fs.createReadStream(filePath, { encoding: 'utf8' });
  const reader = readline.createInterface({ input: stream, crlfDelay: Infinity });
  const lowerBound = fromTimestamp === null || fromTimestamp === undefined
    ? null
    : Number(fromTimestamp);
  let currentModel = null;
  let previousCumulative = null;
  let previousCumulativeSignature = null;
  let sessionMetadataSeen = false;
  let suppressForkReplay = false;
  for await (const line of reader) {
    if (!line.trim()) continue;
    let record;
    try { record = JSON.parse(line); } catch { continue; }
    const payload = record?.payload || {};
    if (!sessionMetadataSeen && record?.type === 'session_meta') {
      sessionMetadataSeen = true;
      suppressForkReplay = String(payload.thread_source || '').toLowerCase() === 'subagent' ||
        Boolean(payload?.source?.subagent);
      continue;
    }
    if (record?.type === 'turn_context') {
      currentModel = payload.model || currentModel;
      continue;
    }
    if (suppressForkReplay) {
      if (record?.type === 'inter_agent_communication_metadata') suppressForkReplay = false;
      continue;
    }
    if (record?.type !== 'event_msg' || payload.type !== 'token_count') continue;
    const timestamp = parseTimestamp(record.timestamp || payload.timestamp);
    if (timestamp === null) continue;

    const lastUsage = parseUsageSnapshot(payload?.info?.last_token_usage || payload?.last_token_usage);
    const cumulative = parseUsageSnapshot(payload?.info?.total_token_usage || payload?.total_token_usage);
    let usage = lastUsage;
    if (cumulative) {
      const signature = usageSignature(cumulative);
      if (signature === previousCumulativeSignature) {
        usage = null;
      } else if (!usage) {
        usage = cumulativeDelta(previousCumulative, cumulative);
      }
      previousCumulative = cumulative;
      previousCumulativeSignature = signature;
    }
    if (!usage || !hasTokenUsage(usage)) continue;
    // Records before the visible lower bound still update cumulative state, but never
    // enter the displayed totals. This prevents the first in-range total snapshot from
    // being charged from zero.
    if (lowerBound !== null && timestamp < lowerBound) continue;
    addEvent(normalizeUsageEvent({
      ...usage,
      model: payload.model || currentModel || '未知模型',
      timestamp,
    }));
  }
}

function normalizeRange(range) {
  const value = range === undefined || range === null || range === '' ? '30d' : String(range);
  if (!ALLOWED_RANGES.has(value)) throw new Error(`无效的统计范围：${value}`);
  return value;
}

function rangeStart(range, now = Date.now()) {
  const value = normalizeRange(range);
  if (value === 'today') {
    const date = new Date(now);
    date.setHours(0, 0, 0, 0);
    return date.getTime();
  }
  if (value === '7d') return now - 7 * 86_400_000;
  if (value === '30d') return now - 30 * 86_400_000;
  return null;
}

function switchTimestamp(value) {
  if (!value || typeof value !== 'object') return null;
  for (const candidate of [
    value.timestampUtc,
    value.switchedAtUtc,
    value.switched_at_utc,
    value.TimestampUtc,
    value.SwitchedAtUtc,
    value.timestamp,
    value.at,
  ]) {
    const timestamp = parseTimestamp(candidate);
    if (timestamp !== null) return timestamp;
  }
  return null;
}

function buildAccountLookup(accounts) {
  const normalizedAccounts = (Array.isArray(accounts) ? accounts : []).map((account, index) => ({
    account,
    id: accountIdentity(account, index),
    name: String(account?.name || '').trim(),
    codexHome: String(account?.codexHome || '').trim(),
  }));
  return {
    accounts: normalizedAccounts,
    byId: new Map(normalizedAccounts.map((item) => [item.id, item])),
    byName: new Map(normalizedAccounts
      .filter((item) => item.name)
      .map((item) => [item.name.toLocaleLowerCase(), item])),
  };
}

function normalizeSwitches(switches, accounts) {
  const lookup = buildAccountLookup(accounts);
  const candidates = [];
  for (const entry of Array.isArray(switches) ? switches : []) {
    const timestamp = switchTimestamp(entry);
    if (timestamp === null) continue;
    const recordedId = String(
      entry?.accountId || entry?.accountKey || entry?.AccountId || entry?.AccountKey || '',
    ).trim();
    const recordedName = String(
      entry?.accountName || entry?.name || entry?.AccountName || entry?.Name || '',
    ).trim();
    const match = (recordedId && lookup.byId.get(recordedId)) ||
      (recordedName && lookup.byName.get(recordedName.toLocaleLowerCase())) ||
      null;
    // Passive detections from old manager copies are trusted only when they still
    // identify a known account. Explicit user switches remain valid boundaries.
    if (String(entry?.source || entry?.Source || '').toLowerCase() === 'detected' && !match) continue;
    candidates.push({
      timestamp,
      accountId: match?.id || null,
      accountName: match?.name || recordedName || null,
      source: String(entry?.source || entry?.Source || 'switch'),
      metadataScore: (recordedId ? 2 : 0) + (recordedName ? 1 : 0),
    });
  }

  candidates.sort((a, b) => a.timestamp - b.timestamp || b.metadataScore - a.metadataScore);
  const deduplicated = [];
  for (const candidate of candidates) {
    const previous = deduplicated[deduplicated.length - 1];
    if (previous && previous.timestamp === candidate.timestamp &&
        String(previous.accountName || '').toLocaleLowerCase() === String(candidate.accountName || '').toLocaleLowerCase()) {
      if (candidate.metadataScore > previous.metadataScore) deduplicated[deduplicated.length - 1] = candidate;
      continue;
    }
    deduplicated.push(candidate);
  }

  const normalized = [];
  for (const candidate of deduplicated) {
    const previous = normalized[normalized.length - 1];
    const sameBoundary = previous && (
      (previous.accountId && candidate.accountId && previous.accountId === candidate.accountId) ||
      (!previous.accountId && !candidate.accountId &&
        String(previous.accountName || '').toLocaleLowerCase() === String(candidate.accountName || '').toLocaleLowerCase())
    );
    if (!sameBoundary) normalized.push(candidate);
  }
  return normalized;
}

function activeAccountId(timestamp, switches) {
  let low = 0;
  let high = switches.length - 1;
  let match = -1;
  while (low <= high) {
    const middle = low + Math.floor((high - low) / 2);
    if (switches[middle].timestamp <= timestamp) {
      match = middle;
      low = middle + 1;
    } else {
      high = middle - 1;
    }
  }
  return match >= 0 ? switches[match].accountId : null;
}

function startOfLocalDay(timestamp) {
  const date = new Date(timestamp);
  date.setHours(0, 0, 0, 0);
  return date.getTime();
}

function startOfLocalMonth(timestamp) {
  const date = new Date(timestamp);
  date.setDate(1);
  date.setHours(0, 0, 0, 0);
  return date.getTime();
}

function timelineDefinitions(range, now, events) {
  const definitions = [];
  const current = new Date(now);
  if (range === 'today') {
    const start = startOfLocalDay(now);
    for (let hour = 0; hour <= current.getHours(); hour += 1) {
      const bucketStart = start + hour * 3_600_000;
      definitions.push({
        key: new Date(bucketStart).toISOString(),
        label: `${String(hour).padStart(2, '0')}:00`,
        start: bucketStart,
        end: bucketStart + 3_600_000,
      });
    }
    return definitions;
  }

  if (range === '7d' || range === '30d') {
    const days = range === '7d' ? 7 : 30;
    const today = startOfLocalDay(now);
    for (let offset = days - 1; offset >= 0; offset -= 1) {
      const date = new Date(today);
      date.setDate(date.getDate() - offset);
      const bucketStart = date.getTime();
      const next = new Date(bucketStart);
      next.setDate(next.getDate() + 1);
      definitions.push({
        key: date.toISOString().slice(0, 10),
        label: `${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`,
        start: bucketStart,
        end: next.getTime(),
      });
    }
    return definitions;
  }

  const validTimestamps = events
    .map((event) => Number(event.timestamp))
    .filter((timestamp) => Number.isFinite(timestamp) && timestamp <= now);
  if (validTimestamps.length === 0) return definitions;
  const firstTimestamp = validTimestamps.reduce(
    (minimum, timestamp) => Math.min(minimum, timestamp),
    Number.POSITIVE_INFINITY,
  );
  let cursor = new Date(startOfLocalMonth(firstTimestamp));
  const last = startOfLocalMonth(now);
  // Guard against a corrupt ancient timestamp creating an unbounded chart.
  const earliestAllowed = new Date(last);
  earliestAllowed.setMonth(earliestAllowed.getMonth() - 119);
  if (cursor < earliestAllowed) cursor = earliestAllowed;
  while (cursor.getTime() <= last) {
    const bucketStart = cursor.getTime();
    const next = new Date(bucketStart);
    next.setMonth(next.getMonth() + 1);
    definitions.push({
      key: `${cursor.getFullYear()}-${String(cursor.getMonth() + 1).padStart(2, '0')}`,
      label: `${cursor.getFullYear()}-${String(cursor.getMonth() + 1).padStart(2, '0')}`,
      start: bucketStart,
      end: next.getTime(),
    });
    cursor = next;
  }
  return definitions;
}

function buildTimeline(events, range, now = Date.now()) {
  const definitions = timelineDefinitions(range, now, events);
  const buckets = definitions.map((definition) => ({
    ...definition,
    totalTokens: 0,
    inputTokens: 0,
    cachedInputTokens: 0,
    cacheWriteTokens: 0,
    cacheWriteKnownEvents: 0,
    cacheWriteUnknownEvents: 0,
    outputTokens: 0,
    reasoningOutputTokens: 0,
    knownApiEquivalentUsd: 0,
    apiEquivalentUnknownEvents: 0,
    events: 0,
  }));
  for (const rawEvent of events) {
    const timestamp = Number(rawEvent?.timestamp);
    if (!Number.isFinite(timestamp)) continue;
    const bucket = buckets.find((candidate) => timestamp >= candidate.start && timestamp < candidate.end);
    if (!bucket) continue;
    const event = normalizeUsageEvent(rawEvent);
    const cost = eventCost(event);
    bucket.totalTokens += event.totalTokens;
    bucket.inputTokens += event.inputTokens;
    bucket.cachedInputTokens += event.cachedInputTokens;
    bucket.outputTokens += event.outputTokens;
    bucket.reasoningOutputTokens += event.reasoningOutputTokens;
    if (event.cacheWriteTokens === null) bucket.cacheWriteUnknownEvents += 1;
    else {
      bucket.cacheWriteKnownEvents += 1;
      bucket.cacheWriteTokens += event.cacheWriteTokens;
    }
    if (cost === null) bucket.apiEquivalentUnknownEvents += 1;
    else bucket.knownApiEquivalentUsd += cost;
    bucket.events += 1;
  }
  return buckets.map((bucket) => {
    const knownApiEquivalentUsd = roundCost(bucket.knownApiEquivalentUsd);
    const apiEquivalentComplete = bucket.apiEquivalentUnknownEvents === 0;
    return {
      ...bucket,
      start: new Date(bucket.start).toISOString(),
      end: new Date(bucket.end).toISOString(),
      knownApiEquivalentUsd,
      apiEquivalentUsd: apiEquivalentComplete ? knownApiEquivalentUsd : null,
      apiEquivalentComplete,
    };
  });
}

function roundCost(value) {
  return Number(value.toFixed(6));
}

function createUsageAccumulator(range, filesScanned = 0, { now = Date.now() } = {}) {
  const normalizedRange = normalizeRange(range);
  const models = new Map();
  const timelineEvents = [];
  const totals = {
    range: normalizedRange,
    totalTokens: 0,
    inputTokens: 0,
    cachedInputTokens: 0,
    cacheWriteTokens: 0,
    cacheWriteKnownEvents: 0,
    cacheWriteUnknownEvents: 0,
    outputTokens: 0,
    reasoningOutputTokens: 0,
    knownApiEquivalentUsd: 0,
    apiEquivalentUnknownEvents: 0,
    filesScanned,
  };

  function addEvent(rawEvent) {
    const event = normalizeUsageEvent(rawEvent);
    if (!hasTokenUsage(event)) return;
    timelineEvents.push(event);
    const cost = eventCost(event);
    totals.totalTokens += event.totalTokens;
    totals.inputTokens += event.inputTokens;
    totals.cachedInputTokens += event.cachedInputTokens;
    totals.outputTokens += event.outputTokens;
    totals.reasoningOutputTokens += event.reasoningOutputTokens;
    if (cost === null) totals.apiEquivalentUnknownEvents += 1;
    else totals.knownApiEquivalentUsd += cost;
    if (event.cacheWriteTokens === null) totals.cacheWriteUnknownEvents += 1;
    else {
      totals.cacheWriteKnownEvents += 1;
      totals.cacheWriteTokens += event.cacheWriteTokens;
    }

    const model = normalizedModel(event.model);
    const bucket = models.get(model) || {
      model,
      tokens: 0,
      knownCost: 0,
      unknownCostEvents: 0,
      events: 0,
    };
    bucket.tokens += event.totalTokens;
    if (cost === null) bucket.unknownCostEvents += 1;
    else bucket.knownCost += cost;
    bucket.events += 1;
    models.set(model, bucket);
  }

  function finish() {
    const knownApiEquivalentUsd = roundCost(totals.knownApiEquivalentUsd);
    const apiEquivalentComplete = totals.apiEquivalentUnknownEvents === 0;
    return {
      ...totals,
      knownApiEquivalentUsd,
      apiEquivalentUsd: apiEquivalentComplete ? knownApiEquivalentUsd : null,
      apiEquivalentComplete,
      timeline: buildTimeline(timelineEvents, normalizedRange, now),
      models: [...models.values()]
        .sort((a, b) => b.tokens - a.tokens)
        .map((item, index) => {
          const costKnown = item.unknownCostEvents === 0;
          const knownCost = roundCost(item.knownCost);
          return {
            model: item.model,
            tokens: item.tokens,
            cost: costKnown ? knownCost : null,
            knownCost,
            costKnown,
            unknownCostEvents: item.unknownCostEvents,
            events: item.events,
            color: COLORS[index % COLORS.length],
          };
        }),
    };
  }

  return { addEvent, finish };
}

function sessionRootDescriptors(accounts, options, lookup) {
  if (Array.isArray(options.sessionRoots)) {
    return options.sessionRoots.map((entry) => {
      if (typeof entry === 'string') return { root: entry, accountId: null };
      const requestedId = String(entry?.accountId || '').trim();
      return {
        root: String(entry?.root || entry?.path || '').trim(),
        accountId: requestedId && lookup.byId.has(requestedId) ? requestedId : null,
      };
    }).filter((entry) => entry.root);
  }

  const roots = [];
  if (options.includeDefaultRoot !== false) {
    roots.push({ root: path.join(os.homedir(), '.codex', 'sessions'), accountId: null });
  }
  lookup.accounts.forEach((item) => {
    if (item.codexHome) roots.push({ root: path.join(item.codexHome, 'sessions'), accountId: item.id });
  });
  return roots;
}

function decorateAccountStats(item, stats) {
  return {
    accountId: item?.id || null,
    accountName: item?.name || null,
    codexHome: item?.codexHome || null,
    ...stats,
  };
}

async function getUsageStats(accounts, rangeOrOptions = '30d', maybeOptions = {}) {
  const { range: normalizedRange, options } = resolveUsageArguments(rangeOrOptions, maybeOptions);
  const now = Number.isFinite(Number(options.now)) ? Number(options.now) : Date.now();
  const lookup = buildAccountLookup(accounts);
  const switches = normalizeSwitches(options.switches, accounts);
  const files = [];
  const seen = new Set();
  for (const descriptor of sessionRootDescriptors(accounts, options, lookup)) {
    await collectOwnedJsonlFiles(descriptor.root, descriptor.accountId, files, seen);
  }

  const aggregateAccumulator = createUsageAccumulator(normalizedRange, files.length, { now });
  const unattributedAccumulator = createUsageAccumulator(normalizedRange, 0, { now });
  const accountAccumulators = new Map(lookup.accounts.map((item) => [
    item.id,
    createUsageAccumulator(
      normalizedRange,
      files.filter((file) => file.accountId === item.id).length,
      { now },
    ),
  ]));
  const fromTimestamp = rangeStart(normalizedRange, now);
  for (const file of files) {
    try {
      await parseSession(file.filePath, fromTimestamp, (event) => {
        aggregateAccumulator.addEvent(event);
        const attributedId = file.accountId || activeAccountId(event.timestamp, switches);
        const target = attributedId ? accountAccumulators.get(attributedId) : null;
        if (target) target.addEvent(event);
        else unattributedAccumulator.addEvent(event);
      });
    } catch {
      // A live session may rotate while being read.
    }
  }

  const aggregate = aggregateAccumulator.finish();
  const perAccount = lookup.accounts.map((item) =>
    decorateAccountStats(item, accountAccumulators.get(item.id).finish()));
  const unattributed = decorateAccountStats(
    { id: null, name: '未归属', codexHome: null },
    unattributedAccumulator.finish(),
  );
  const reportMetadata = {
    aggregate,
    perAccount,
    unattributed,
    generatedAt: new Date(now).toISOString(),
    refreshAfterMs: DEFAULT_REFRESH_INTERVAL_MS,
    switchEventCount: switches.length,
  };

  const requestedScope = String(options.accountId || 'all');
  if (requestedScope !== 'all') {
    const selected = requestedScope === 'unassigned' || requestedScope === 'unattributed'
      ? unattributed
      : perAccount.find((item) => item.accountId === requestedScope);
    if (selected) {
      return {
        ...selected,
        ...reportMetadata,
        scope: requestedScope === 'unassigned' ? 'unattributed' : requestedScope,
      };
    }
  }
  return {
    ...aggregate,
    ...reportMetadata,
    scope: 'all',
  };
}

module.exports = {
  getUsageStats,
  _test: {
    createUsageAccumulator,
    cumulativeDelta,
    eventCost,
    extractCacheWrite,
    activeAccountId,
    buildTimeline,
    normalizeSwitches,
    normalizeRange,
    normalizedModel,
    parseSession,
    parseUsageSnapshot,
    rangeStart,
  },
};
