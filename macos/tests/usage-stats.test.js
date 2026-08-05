const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { EventEmitter } = require('node:events');
const { PassThrough, Writable } = require('node:stream');
const test = require('node:test');

const { getUsageStats, _test } = require('../src/services/usage-stats');
const {
  getQuotaStats,
  readRateLimitsViaAppServer,
  _test: quotaTest,
} = require('../src/services/quota-service');

async function parseRecords(records, fromTimestamp = null) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-usage-stats-'));
  const filePath = path.join(root, 'session.jsonl');
  try {
    fs.writeFileSync(filePath, `${records.map((record) => JSON.stringify(record)).join('\n')}\n`, 'utf8');
    const events = [];
    await _test.parseSession(filePath, fromTimestamp, (event) => events.push(event));
    return events;
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

function tokenRecord(timestamp, usage, { model, totalUsage } = {}) {
  return {
    type: 'event_msg',
    timestamp,
    payload: {
      type: 'token_count',
      ...(model ? { model } : {}),
      info: {
        ...(usage ? { last_token_usage: usage } : {}),
        ...(totalUsage ? { total_token_usage: totalUsage } : {}),
      },
    },
  };
}

test('known model API-equivalent rates match the Windows price table', () => {
  const costFor = (model, inputTokens = 100_000) => _test.eventCost({
    model,
    inputTokens,
    cachedInputTokens: 0,
    cacheWriteTokens: null,
    outputTokens: 0,
    totalTokens: inputTokens,
  });

  assert.equal(costFor('gpt-5.6-sol'), 0.5);
  assert.equal(costFor('gpt-5.6-terra'), 0.2);
  assert.equal(costFor('gpt-5.6-luna'), 0.02);
  assert.equal(costFor('gpt-5.5'), 0.5);
  assert.equal(costFor('gpt-5.4'), 0.25);
  assert.equal(costFor('gpt-5.4-mini'), 0.5);
  assert.equal(costFor('gpt-5.4-nano'), 0.02);
  assert.equal(costFor('codex-mini-latest'), 0.175);
  assert.equal(costFor('gpt-5.4', 300_000), 0.75);
  assert.equal(costFor('gpt-5.4-mini', 300_000), 1.5);
  assert.equal(costFor('codex-mini-latest', 300_000), 0.525);
  assert.equal(costFor('private-model'), null);
  assert.equal(costFor(''), null);
});

test('an anonymized deleted-account boundary cannot attach to a replacement with the same name', () => {
  const timestampUtc = '2026-08-05T12:00:00.000Z';
  const switches = _test.normalizeSwitches([{
    timestampUtc,
    accountId: '',
    accountName: '',
    source: 'deleted-account-boundary',
  }], [{ id: 'replacement', name: 'Deleted account', codexHome: '/replacement' }]);

  assert.equal(switches.length, 1);
  assert.equal(switches[0].accountId, null);
  assert.equal(switches[0].accountName, null);
  assert.equal(_test.activeAccountId(Date.parse(timestampUtc) + 1, switches), null);
});

test('terra and luna apply official long-context rates only above 272K input tokens', () => {
  const costFor = (model, inputTokens) => _test.eventCost({
    model,
    inputTokens,
    cachedInputTokens: inputTokens === 1_000_000 ? 200_000 : 0,
    cacheWriteTokens: inputTokens === 1_000_000 ? 400_000 : 0,
    outputTokens: inputTokens === 1_000_000 ? 100_000 : 0,
    totalTokens: inputTokens,
  });

  assert.equal(costFor('gpt-5.6-terra', 272_000), 0.544);
  assert.equal(costFor('gpt-5.6-terra', 272_001), 1.088004);
  assert.equal(costFor('gpt-5.6-terra', 1_000_000), 5.48);
  assert.equal(costFor('gpt-5.6-luna', 1_000_000), 0.548);
});

test('responses and chat-completions usage aliases produce the same normalized event', async () => {
  const events = await parseRecords([
    { type: 'turn_context', timestamp: '2026-07-15T00:00:00Z', payload: { model: 'chat-latest' } },
    tokenRecord('2026-07-15T00:00:01Z', {
      prompt_tokens: 100,
      prompt_tokens_details: { cached_tokens: 25, cache_write_tokens: 30 },
      completion_tokens: 20,
      completion_tokens_details: { reasoning_tokens: 5 },
      total_tokens: 120,
    }),
  ]);

  assert.deepEqual(events.map(({ timestamp, ...event }) => event), [{
    model: 'chat-latest',
    inputTokens: 100,
    cachedInputTokens: 25,
    cacheWriteTokens: 30,
    outputTokens: 20,
    reasoningOutputTokens: 5,
    totalTokens: 120,
  }]);
});

test('cache-write values are known only when non-negative and inside uncached input', async () => {
  const events = await parseRecords([
    tokenRecord('2026-07-15T00:00:01Z', {
      input_tokens: 100,
      cached_input_tokens: 25,
      cache_write_input_tokens: 75,
      output_tokens: 1,
      total_tokens: 101,
    }, { model: 'gpt-5.6-terra' }),
    tokenRecord('2026-07-15T00:00:02Z', {
      input_tokens: 100,
      cached_input_tokens: 25,
      cache_write_input_tokens: 76,
      output_tokens: 1,
      total_tokens: 101,
    }, { model: 'gpt-5.6-terra' }),
    tokenRecord('2026-07-15T00:00:03Z', {
      input_tokens: 100,
      cached_input_tokens: 25,
      cache_write_input_tokens: -1,
      output_tokens: 1,
      total_tokens: 101,
    }, { model: 'gpt-5.6-terra' }),
  ]);

  assert.deepEqual(events.map((event) => event.cacheWriteTokens), [75, null, null]);
  const accumulator = _test.createUsageAccumulator('30d');
  for (const event of events) accumulator.addEvent(event);
  const totals = accumulator.finish();
  assert.equal(totals.cacheWriteTokens, 75);
  assert.equal(totals.cacheWriteKnownEvents, 1);
  assert.equal(totals.cacheWriteUnknownEvents, 2);
});

test('cumulative snapshots establish an out-of-range baseline, emit deltas, and deduplicate repeats', async () => {
  const firstTotal = {
    input_tokens: 100,
    cached_input_tokens: 20,
    cache_write_tokens: 10,
    output_tokens: 10,
    reasoning_output_tokens: 2,
    total_tokens: 110,
  };
  const secondTotal = {
    input_tokens: 160,
    cached_input_tokens: 30,
    cache_write_tokens: 16,
    output_tokens: 20,
    reasoning_output_tokens: 4,
    total_tokens: 180,
  };
  const thirdTotal = {
    input_tokens: 200,
    cached_input_tokens: 40,
    cache_write_tokens: 20,
    output_tokens: 25,
    reasoning_output_tokens: 5,
    total_tokens: 225,
  };
  const lastThird = {
    input_tokens: 40,
    cached_input_tokens: 10,
    cache_write_tokens: 4,
    output_tokens: 5,
    reasoning_output_tokens: 1,
    total_tokens: 45,
  };
  const events = await parseRecords([
    { type: 'turn_context', timestamp: '2026-07-15T00:00:00Z', payload: { model: 'gpt-5.6-terra' } },
    tokenRecord('2026-07-15T00:00:10Z', null, { totalUsage: firstTotal }),
    tokenRecord('2026-07-15T00:01:00Z', null, { totalUsage: secondTotal }),
    tokenRecord('2026-07-15T00:01:10Z', {
      input_tokens: 60,
      cached_input_tokens: 10,
      output_tokens: 10,
      total_tokens: 70,
    }, { totalUsage: secondTotal }),
    tokenRecord('2026-07-15T00:02:00Z', lastThird, { totalUsage: thirdTotal }),
  ], Date.parse('2026-07-15T00:00:30Z'));

  assert.equal(events.length, 2);
  assert.deepEqual(events.map(({ timestamp, model, ...usage }) => usage), [
    {
      inputTokens: 60,
      cachedInputTokens: 10,
      cacheWriteTokens: 6,
      outputTokens: 10,
      reasoningOutputTokens: 2,
      totalTokens: 70,
    },
    {
      inputTokens: 40,
      cachedInputTokens: 10,
      cacheWriteTokens: 4,
      outputTokens: 5,
      reasoningOutputTokens: 1,
      totalTokens: 45,
    },
  ]);
});

test('invalid timestamps are excluded and range values are allowlisted', async () => {
  const events = await parseRecords([
    tokenRecord('not-a-date', { input_tokens: 100, output_tokens: 20, total_tokens: 120 }, { model: 'gpt-5.6-terra' }),
    tokenRecord('2026-01-01T00:00:00Z', { input_tokens: 50, output_tokens: 5, total_tokens: 55 }, { model: 'gpt-5.6-terra' }),
    tokenRecord('2026-07-15T00:00:00Z', { input_tokens: 20, output_tokens: 2, total_tokens: 22 }, { model: 'gpt-5.6-terra' }),
  ], Date.parse('2026-07-01T00:00:00Z'));

  assert.equal(events.length, 1);
  assert.equal(events[0].totalTokens, 22);
  assert.equal(_test.normalizeRange(undefined), '30d');
  assert.equal(_test.normalizeRange('all'), 'all');
  assert.equal(_test.rangeStart('all', Date.parse('2026-07-15T00:00:00Z')), null);
  assert.throws(() => _test.normalizeRange('forever'), /无效的统计范围/);
  assert.throws(() => _test.rangeStart('../all'), /无效的统计范围/);
});

test('unknown-model costs are explicitly incomplete instead of silently using terra', () => {
  const accumulator = _test.createUsageAccumulator('7d', 3);
  accumulator.addEvent({
    model: 'gpt-5.6-terra',
    inputTokens: 100_000,
    cachedInputTokens: 0,
    cacheWriteTokens: null,
    outputTokens: 0,
    totalTokens: 100_000,
  });
  accumulator.addEvent({
    model: 'private-model',
    inputTokens: 100_000,
    cachedInputTokens: 0,
    cacheWriteTokens: null,
    outputTokens: 0,
    totalTokens: 100_000,
  });
  const totals = accumulator.finish();

  assert.equal(totals.knownApiEquivalentUsd, 0.2);
  assert.equal(totals.apiEquivalentUsd, null);
  assert.equal(totals.apiEquivalentComplete, false);
  assert.equal(totals.apiEquivalentUnknownEvents, 1);
  assert.equal(totals.filesScanned, 3);
  const unknown = totals.models.find((item) => item.model === 'private-model');
  assert.equal(unknown.cost, null);
  assert.equal(unknown.knownCost, 0);
  assert.equal(unknown.costKnown, false);
  assert.equal(unknown.unknownCostEvents, 1);
});

test('subagent sessions suppress fork replay until the live-turn boundary', async () => {
  const events = await parseRecords([
    { type: 'session_meta', timestamp: '2026-07-15T00:00:00Z', payload: { thread_source: 'subagent', source: { subagent: {} } } },
    { type: 'turn_context', timestamp: '2026-07-15T00:00:01Z', payload: { model: 'gpt-5.6-sol' } },
    tokenRecord('2026-07-15T00:00:02Z', { input_tokens: 100, output_tokens: 10, total_tokens: 110 }),
    { type: 'inter_agent_communication_metadata', timestamp: '2026-07-15T00:00:03Z', payload: {} },
    tokenRecord('2026-07-15T00:00:04Z', { input_tokens: 200, output_tokens: 20, total_tokens: 220 }),
  ]);

  assert.equal(events.length, 1);
  assert.equal(events[0].totalTokens, 220);
  assert.equal(events[0].model, 'gpt-5.6-sol');
});

test('usage is isolated by account roots and shared-session switch epochs with a real timeline', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-account-usage-'));
  const sharedSessions = path.join(root, 'shared');
  const alphaSessions = path.join(root, 'alpha', 'sessions');
  const betaSessions = path.join(root, 'beta', 'sessions');
  fs.mkdirSync(sharedSessions, { recursive: true });
  fs.mkdirSync(alphaSessions, { recursive: true });
  fs.mkdirSync(betaSessions, { recursive: true });
  const makeUsage = (tokens) => ({ input_tokens: tokens, output_tokens: 0, total_tokens: tokens });
  try {
    fs.writeFileSync(path.join(sharedSessions, 'shared.jsonl'), [
      tokenRecord('2026-07-20T00:00:00Z', makeUsage(10), { model: 'gpt-5.6-terra' }),
      tokenRecord('2026-07-20T00:02:00Z', makeUsage(20), { model: 'gpt-5.6-terra' }),
      tokenRecord('2026-07-20T00:04:00Z', makeUsage(30), { model: 'gpt-5.6-terra' }),
    ].map(JSON.stringify).join('\n'), 'utf8');
    fs.writeFileSync(path.join(alphaSessions, 'alpha.jsonl'), `${JSON.stringify(
      tokenRecord('2026-07-20T00:05:00Z', makeUsage(40), { model: 'gpt-5.6-sol' }),
    )}\n`, 'utf8');

    const accounts = [
      { id: 'alpha', name: 'Alpha', codexHome: path.join(root, 'alpha') },
      { id: 'beta', name: 'Beta', codexHome: path.join(root, 'beta') },
    ];
    const switches = [
      { timestampUtc: '2026-07-20T00:01:00Z', accountId: 'alpha', accountName: 'Alpha' },
      { timestampUtc: '2026-07-20T00:03:00Z', accountId: 'beta', accountName: 'Beta' },
    ];
    const options = {
      range: '7d',
      now: Date.parse('2026-07-20T12:00:00Z'),
      switches,
      sessionRoots: [
        { path: sharedSessions },
        { path: alphaSessions, accountId: 'alpha' },
        { path: betaSessions, accountId: 'beta' },
      ],
    };
    const report = await getUsageStats(accounts, options);

    assert.equal(report.totalTokens, 100);
    assert.equal(report.aggregate.totalTokens, 100);
    assert.equal(report.perAccount.find((item) => item.accountId === 'alpha').totalTokens, 60);
    assert.equal(report.perAccount.find((item) => item.accountId === 'beta').totalTokens, 30);
    assert.equal(report.unattributed.totalTokens, 10);
    assert.equal(report.switchEventCount, 2);
    assert.equal(report.timeline.length, 7);
    assert.equal(report.timeline.reduce((sum, item) => sum + item.totalTokens, 0), 100);
    assert.ok(report.timeline.some((item) => item.events === 4));

    const alpha = await getUsageStats(accounts, { ...options, accountId: 'alpha' });
    assert.equal(alpha.scope, 'alpha');
    assert.equal(alpha.totalTokens, 60);
    assert.equal(alpha.aggregate.totalTokens, 100);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('quota parser selects Codex limits and classifies 5-hour, weekly, and monthly windows', () => {
  const snapshot = quotaTest.parseRateLimits({
    rateLimits: {
      primary: { usedPercent: 99, windowDurationMins: 1, resetsAt: 1_786_000_000 },
    },
    rateLimitsByLimitId: {
      codex: {
        limitId: 'codex',
        planType: 'business',
        primary: { usedPercent: 12, windowDurationMins: 300, resetsAt: 1_786_366_863 },
        secondary: { usedPercent: 33, windowDurationMins: 10_080, resetsAt: 1_786_800_000 },
        credits: { hasCredits: true, unlimited: false, balance: '12.50' },
      },
    },
  }, '2026-07-20T00:00:00Z');

  assert.equal(snapshot.planType, 'business');
  assert.equal(snapshot.primary.kind, 'fiveHour');
  assert.equal(snapshot.primary.remainingPercent, 88);
  assert.equal(snapshot.secondary.kind, 'weekly');
  assert.equal(snapshot.credits.balance, '12.50');
  assert.equal(quotaTest.classifyWindow(43_800), 'monthly');
  const fractional = quotaTest.parseWindow({ usedPercent: 12.5, windowDurationMins: 300 }, snapshot.observedAt);
  assert.equal(fractional.usedPercent, 12.5);
  assert.equal(fractional.remainingPercent, 87.5);
});

test('quota live failures keep local snapshots and expose only actionable credential errors', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-quota-live-fallback-'));
  const sessions = path.join(root, 'alpha', 'sessions');
  fs.mkdirSync(sessions, { recursive: true });
  try {
    fs.writeFileSync(path.join(sessions, 'quota.jsonl'), `${JSON.stringify({
      timestamp: '2026-07-20T00:00:00Z',
      payload: {
        rate_limits: {
          primary: { used_percent: 12.5, window_minutes: 300, resets_at: 1_786_366_863 },
        },
      },
    })}\n`, 'utf8');
    const account = {
      id: 'alpha',
      name: 'Alpha',
      authKind: 'official_oauth',
      codexHome: path.join(root, 'alpha'),
      credentialEpoch: 'epoch-alpha-00000001',
      credentialActivatedAt: '2026-07-19T00:00:00Z',
    };
    const report = await getQuotaStats([account], {
      allowMissingAuth: true,
      now: Date.parse('2026-07-20T01:00:00Z'),
      readLiveQuota: async () => {
        const error = new Error('server rejected secret-value');
        error.code = 'token_invalidated';
        throw error;
      },
      sessionRoots: [{ path: sessions, accountId: 'alpha' }],
    });

    assert.equal(report.accounts[0].available, true);
    assert.equal(report.accounts[0].windows.fiveHour.usedPercent, 12.5);
    assert.match(report.accounts[0].error, /登录凭据已失效/);
    assert.match(report.accounts[0].error, /本地额度快照/);
    assert.doesNotMatch(report.accounts[0].error, /secret-value/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('local rate-limit snapshots remain account-isolated and retain partial 5h/week/month history', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-account-quota-'));
  const sessions = path.join(root, 'alpha', 'sessions');
  fs.mkdirSync(sessions, { recursive: true });
  const quotaRecord = (timestamp, rateLimits) => ({
    type: 'event_msg',
    timestamp,
    payload: { type: 'token_count', rate_limits: rateLimits },
  });
  try {
    fs.writeFileSync(path.join(sessions, 'quota.jsonl'), [
      quotaRecord('2026-07-20T00:00:00Z', {
        primary: { used_percent: 15, window_minutes: 300, resets_at: 1_786_366_863 },
        secondary: { used_percent: 25, window_minutes: 10_080, resets_at: 1_786_800_000 },
        plan_type: 'plus',
      }),
      quotaRecord('2026-07-20T01:00:00Z', {
        primary: { used_percent: 40, window_minutes: 43_800, resets_at: 1_789_000_000 },
      }),
    ].map(JSON.stringify).join('\n'), 'utf8');
    const accounts = [
      {
        id: 'alpha',
        name: 'Alpha',
        authKind: 'official_oauth',
        codexHome: path.join(root, 'alpha'),
        credentialEpoch: 'epoch-alpha-00000001',
        credentialActivatedAt: '2026-07-19T00:00:00Z',
      },
      {
        id: 'beta',
        name: 'Beta',
        authKind: 'official_oauth',
        codexHome: path.join(root, 'beta'),
        credentialEpoch: 'epoch-beta-000000001',
        credentialActivatedAt: '2026-07-19T00:00:00Z',
      },
    ];
    const result = await getQuotaStats(accounts, {
      live: false,
      now: Date.parse('2026-07-20T02:00:00Z'),
      sessionRoots: [{ path: sessions, accountId: 'alpha' }],
    });
    const alpha = result.accounts[0];
    const beta = result.accounts[1];

    assert.equal(alpha.source, 'session');
    assert.equal(alpha.windows.fiveHour.usedPercent, 15);
    assert.equal(alpha.windows.weekly.usedPercent, 25);
    assert.equal(alpha.windows.monthly.usedPercent, 40);
    assert.equal(alpha.primary.kind, 'monthly');
    assert.equal(alpha.secondary.kind, 'weekly');
    assert.equal(beta.available, false);
    assert.equal(beta.windows.fiveHour, null);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('quota JSONL accepts only records at or after the active credential boundary', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-quota-credential-boundary-'));
  const sessions = path.join(root, 'sessions');
  fs.mkdirSync(sessions, { recursive: true });
  try {
    const record = (timestamp, usedPercent) => ({
      timestamp,
      payload: {
        rate_limits: {
          primary: { used_percent: usedPercent, window_minutes: 300 },
        },
      },
    });
    fs.writeFileSync(path.join(sessions, 'quota.jsonl'), [
      record('2026-07-20T00:59:59Z', 91),
      record('2026-07-20T01:00:00Z', 22),
    ].map(JSON.stringify).join('\n'), 'utf8');
    const account = {
      id: 'boundary',
      name: 'Boundary',
      authKind: 'official_oauth',
      codexHome: root,
      credentialEpoch: 'epoch-boundary-0001',
      credentialActivatedAt: '2026-07-20T01:00:00Z',
    };
    const report = await getQuotaStats([account], {
      live: false,
      includeDefaultRoot: false,
      sessionRoots: [{ path: sessions, accountId: account.id }],
    });
    assert.equal(report.accounts[0].snapshotCount, 1);
    assert.equal(report.accounts[0].windows.fiveHour.usedPercent, 22);

    const withoutBoundary = await getQuotaStats([{ ...account, credentialActivatedAt: null }], {
      live: false,
      includeDefaultRoot: false,
      sessionRoots: [{ path: sessions, accountId: account.id }],
    });
    assert.equal(withoutBoundary.accounts[0].available, false, 'missing activation metadata fails closed');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('live quota snapshots persist and only fall back within the same credential epoch', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-quota-epoch-cache-'));
  const quotaSnapshotsPath = path.join(root, 'quota-snapshots.json');
  try {
    const account = {
      id: 'cached-account',
      name: 'Cached',
      authKind: 'official_oauth',
      codexHome: path.join(root, 'account'),
      credentialEpoch: 'epoch-cached-000001',
      credentialActivatedAt: '2026-07-20T00:00:00Z',
    };
    const success = await getQuotaStats([account], {
      allowMissingAuth: true,
      includeDefaultRoot: false,
      sessionRoots: [],
      quotaSnapshotsPath,
      now: Date.parse('2026-07-20T01:00:00Z'),
      readLiveQuota: async () => quotaTest.parseRateLimits({
        primary: { usedPercent: 27, windowMinutes: 300 },
      }, '2026-07-20T01:00:00Z'),
    });
    assert.equal(success.accounts[0].available, true);
    assert.equal(success.accounts[0].windows.fiveHour.usedPercent, 27);
    assert.equal(success.accounts[0].cacheWarning, null);
    const saved = JSON.parse(fs.readFileSync(quotaSnapshotsPath, 'utf8'));
    assert.equal(saved.entries[0].accountId, account.id);
    assert.equal(saved.entries[0].credentialEpoch, account.credentialEpoch);

    const failedReader = async () => { throw new Error('temporary network failure'); };
    const sameEpoch = await getQuotaStats([account], {
      allowMissingAuth: true,
      includeDefaultRoot: false,
      sessionRoots: [],
      quotaSnapshotsPath,
      readLiveQuota: failedReader,
    });
    assert.equal(sameEpoch.accounts[0].available, true);
    assert.equal(sameEpoch.accounts[0].source, 'cache');
    assert.equal(sameEpoch.accounts[0].windows.fiveHour.usedPercent, 27);

    const nextCredential = {
      ...account,
      credentialEpoch: 'epoch-cached-000002',
      credentialActivatedAt: '2026-07-20T02:00:00Z',
    };
    const differentEpoch = await getQuotaStats([nextCredential], {
      allowMissingAuth: true,
      includeDefaultRoot: false,
      sessionRoots: [],
      quotaSnapshotsPath,
      readLiveQuota: failedReader,
    });
    assert.equal(differentEpoch.accounts[0].available, false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('quota cache write failures keep the successful live result', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-quota-cache-write-failure-'));
  const quotaSnapshotsPath = path.join(root, 'quota-snapshots.json');
  fs.mkdirSync(quotaSnapshotsPath);
  try {
    const account = {
      id: 'cache-write-failure',
      name: 'Cache write failure',
      authKind: 'official_oauth',
      codexHome: root,
      credentialEpoch: 'epoch-write-failure-1',
      credentialActivatedAt: '2026-07-20T00:00:00Z',
    };
    const report = await getQuotaStats([account], {
      allowMissingAuth: true,
      includeDefaultRoot: false,
      sessionRoots: [],
      quotaSnapshotsPath,
      readLiveQuota: async () => quotaTest.parseRateLimits({
        primary: { usedPercent: 31, windowMinutes: 300 },
      }, '2026-07-20T01:00:00Z'),
    });
    assert.equal(report.accounts[0].available, true);
    assert.equal(report.accounts[0].windows.fiveHour.usedPercent, 31);
    assert.equal(report.accounts[0].error, null);
    assert.match(report.accounts[0].cacheWarning, /本地快照未能保存/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('a retired credential cannot be restored by a late live quota response', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-quota-retired-race-'));
  const quotaSnapshotsPath = path.join(root, 'quota-snapshots.json');
  let credentialActive = true;
  let releaseLive;
  let markLiveStarted;
  const liveStarted = new Promise((resolve) => { markLiveStarted = resolve; });
  const release = new Promise((resolve) => { releaseLive = resolve; });
  const account = {
    id: 'retired-account',
    name: 'Retired account',
    authKind: 'official_oauth',
    codexHome: root,
    credentialEpoch: 'epoch-retired-000001',
    credentialActivatedAt: '2026-07-20T00:00:00Z',
  };
  try {
    const pending = getQuotaStats([account], {
      allowMissingAuth: true,
      includeDefaultRoot: false,
      sessionRoots: [],
      quotaSnapshotsPath,
      isCredentialStillActive: () => credentialActive,
      readLiveQuota: async () => {
        markLiveStarted();
        await release;
        return quotaTest.parseRateLimits({
          primary: { usedPercent: 44, windowMinutes: 300 },
        }, '2026-07-20T01:00:00Z');
      },
    });
    await liveStarted;
    credentialActive = false;
    releaseLive();
    const report = await pending;
    assert.deepEqual(report.accounts, []);
    assert.equal(fs.existsSync(quotaSnapshotsPath), false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('aborting an app-server quota read waits for the dedicated child to exit', async () => {
  const controller = new AbortController();
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.exitCode = null;
  const signals = [];
  let markRateRequest;
  const rateRequested = new Promise((resolve) => { markRateRequest = resolve; });
  let buffered = '';
  child.stdin = new Writable({
    write(chunk, _encoding, callback) {
      buffered += chunk.toString('utf8');
      while (buffered.includes('\n')) {
        const newline = buffered.indexOf('\n');
        const line = buffered.slice(0, newline);
        buffered = buffered.slice(newline + 1);
        if (!line) continue;
        const message = JSON.parse(line);
        if (message.method === 'initialize') {
          queueMicrotask(() => child.stdout.write(`${JSON.stringify({ id: message.id, result: {} })}\n`));
        } else if (message.method === 'account/rateLimits/read') {
          markRateRequest();
        }
      }
      callback();
    },
  });
  child.kill = (signal) => {
    signals.push(signal);
    if (signal === 'SIGTERM') {
      setTimeout(() => {
        child.exitCode = 0;
        child.emit('close', 0);
      }, 25);
    }
    return true;
  };

  const pending = readRateLimitsViaAppServer(
    { id: 'abort-account', codexHome: os.tmpdir() },
    {
      codexPath: '/fake/codex',
      spawnProcess: () => child,
      timeoutMs: 1_000,
      processStopTimeoutMs: 100,
      signal: controller.signal,
    },
  );
  await rateRequested;
  controller.abort();
  await assert.rejects(pending, (error) => error?.code === 'ABORT_ERR');
  assert.deepEqual(signals, ['SIGTERM']);
  assert.equal(child.exitCode, 0);
});

test('an app-server child that survives TERM and KILL fails closed', async () => {
  const controller = new AbortController();
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.exitCode = null;
  const signals = [];
  let markRateRequest;
  const rateRequested = new Promise((resolve) => { markRateRequest = resolve; });
  let buffered = '';
  child.stdin = new Writable({
    write(chunk, _encoding, callback) {
      buffered += chunk.toString('utf8');
      while (buffered.includes('\n')) {
        const newline = buffered.indexOf('\n');
        const line = buffered.slice(0, newline);
        buffered = buffered.slice(newline + 1);
        if (!line) continue;
        const message = JSON.parse(line);
        if (message.method === 'initialize') {
          queueMicrotask(() => child.stdout.write(`${JSON.stringify({ id: message.id, result: {} })}\n`));
        } else if (message.method === 'account/rateLimits/read') {
          markRateRequest();
        }
      }
      callback();
    },
  });
  child.kill = (signal) => {
    signals.push(signal);
    return true;
  };

  const pending = readRateLimitsViaAppServer(
    { id: 'stubborn-account', codexHome: os.tmpdir() },
    {
      codexPath: '/fake/codex',
      spawnProcess: () => child,
      timeoutMs: 1_000,
      processStopTimeoutMs: 10,
      signal: controller.signal,
    },
  );
  await rateRequested;
  controller.abort();
  await assert.rejects(pending, (error) => error?.code === 'QUOTA_PROCESS_STOP_FAILED');
  assert.deepEqual(signals, ['SIGTERM', 'SIGKILL']);
  child.exitCode = 0;
  child.emit('close', 0);
});

test('quota numeric parsing rejects null instead of coercing it to zero', async () => {
  assert.equal(quotaTest.parseWindow({ usedPercent: null, windowDurationMins: null }, '2026-07-20T00:00:00Z'), null);
  const report = await getQuotaStats([], { live: false, now: null, includeDefaultRoot: false, sessionRoots: [] });
  assert.notEqual(report.updatedAt, '1970-01-01T00:00:00.000Z');
});

test('app-server quota reader sends only initialize and account/rateLimits/read RPCs', async () => {
  const messages = [];
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.exitCode = null;
  let buffered = '';
  child.stdin = new Writable({
    write(chunk, _encoding, callback) {
      buffered += chunk.toString('utf8');
      while (buffered.includes('\n')) {
        const newline = buffered.indexOf('\n');
        const line = buffered.slice(0, newline);
        buffered = buffered.slice(newline + 1);
        if (!line) continue;
        const message = JSON.parse(line);
        messages.push(message);
        if (message.method === 'initialize') {
          queueMicrotask(() => child.stdout.write(`${JSON.stringify({ id: message.id, result: {} })}\n`));
        } else if (message.method === 'account/rateLimits/read') {
          queueMicrotask(() => child.stdout.write(`${JSON.stringify({
            id: message.id,
            result: {
              rateLimitsByLimitId: {
                codex: {
                  limitId: 'codex',
                  primary: { usedPercent: 21, windowDurationMins: 300, resetsAt: 1_786_366_863 },
                },
              },
            },
          })}\n`));
        }
      }
      callback();
    },
  });
  child.kill = () => {
    if (child.exitCode !== null) return false;
    child.exitCode = 0;
    queueMicrotask(() => child.emit('close', 0));
    return true;
  };

  const snapshot = await readRateLimitsViaAppServer(
    { id: 'alpha', codexHome: os.tmpdir() },
    {
      codexPath: '/fake/codex',
      spawnProcess: () => child,
      timeoutMs: 1_000,
      now: Date.parse('2026-07-20T00:00:00Z'),
    },
  );

  assert.equal(snapshot.primary.usedPercent, 21);
  assert.deepEqual(messages.map((message) => message.method), [
    'initialize',
    'initialized',
    'account/rateLimits/read',
  ]);
  assert.equal(messages.some((message) => /model|turn|response/i.test(String(message.method))), false);
});

test('app-server credential errors remain actionable through the full quota report', async () => {
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.exitCode = null;
  let buffered = '';
  child.stdin = new Writable({
    write(chunk, _encoding, callback) {
      buffered += chunk.toString('utf8');
      while (buffered.includes('\n')) {
        const newline = buffered.indexOf('\n');
        const line = buffered.slice(0, newline);
        buffered = buffered.slice(newline + 1);
        if (!line) continue;
        const message = JSON.parse(line);
        if (message.method === 'initialize') {
          queueMicrotask(() => child.stdout.write(`${JSON.stringify({ id: message.id, result: {} })}\n`));
        } else if (message.method === 'account/rateLimits/read') {
          queueMicrotask(() => child.stdout.write(`${JSON.stringify({
            id: message.id,
            error: { code: -32_000, message: 'token_invalidated secret-server-detail' },
          })}\n`));
        }
      }
      callback();
    },
  });
  child.kill = () => {
    if (child.exitCode !== null) return false;
    child.exitCode = 0;
    queueMicrotask(() => child.emit('close', 0));
    return true;
  };

  const report = await getQuotaStats([{
    id: 'invalidated',
    name: 'Invalidated',
    authKind: 'official_oauth',
    codexHome: os.tmpdir(),
  }], {
    allowMissingAuth: true,
    codexPath: '/fake/codex',
    includeDefaultRoot: false,
    sessionRoots: [],
    spawnProcess: () => child,
    timeoutMs: 1_000,
  });

  assert.match(report.accounts[0].error, /登录凭据已失效/);
  assert.doesNotMatch(report.accounts[0].error, /secret-server-detail|暂时无法读取/);
});
