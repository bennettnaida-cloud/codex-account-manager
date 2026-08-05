const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { HistoryService } = require('../src/services/history-service');

function response(timestamp, role, text) {
  return {
    timestamp,
    type: 'response_item',
    payload: {
      type: 'message',
      role,
      content: [{ type: role === 'assistant' ? 'output_text' : 'input_text', text }],
    },
  };
}

function event(timestamp, type, message) {
  return { timestamp, type: 'event_msg', payload: { type, message } };
}

function writeRollout(home, {
  id,
  archived = false,
  date = '2026-07-20',
  source = 'cli',
  records = [],
  fileName = null,
}) {
  const directory = archived
    ? path.join(home, 'archived_sessions')
    : path.join(home, 'sessions', ...date.split('-'));
  fs.mkdirSync(directory, { recursive: true });
  const target = path.join(
    directory,
    fileName || `rollout-${date}T12-00-00-${id}.jsonl`,
  );
  const metadata = {
    timestamp: `${date}T12:00:00.000Z`,
    type: 'session_meta',
    payload: {
      id,
      timestamp: `${date}T12:00:00.000Z`,
      cwd: '/Users/test/Project',
      model_provider: 'openai',
      source,
    },
  };
  const lines = [metadata, ...records].map((record) =>
    typeof record === 'string' ? record : JSON.stringify(record));
  fs.writeFileSync(target, `${lines.join('\n')}\n`, 'utf8');
  return target;
}

function temporaryHome(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-history-'));
  const home = path.join(root, '.codex');
  fs.mkdirSync(home);
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return { root, home };
}

test('listThreads scans active and archived JSONL, searches bodies, and excludes subagents', async (t) => {
  const { home } = temporaryHome(t);
  const activeId = '019f6d10-7f43-7a84-89c6-b94ba0c82451';
  const archivedId = '019f6d10-7f43-7a84-89c6-b94ba0c82452';
  const subagentId = '019f6d10-7f43-7a84-89c6-b94ba0c82453';
  writeRollout(home, {
    id: activeId,
    records: [
      { type: 'turn_context', timestamp: '2026-07-20T12:00:01Z', payload: { model: 'gpt-5.6-sol' } },
      response('2026-07-20T12:00:02Z', 'user', '<environment_context>hidden</environment_context>'),
      response('2026-07-20T12:00:03Z', 'user', '修复项目中的登录问题'),
      response('2026-07-20T12:00:04Z', 'assistant', '已经开始处理。'),
      response('2026-07-20T12:00:05Z', 'user', '只有正文搜索才能找到：海王星'),
      response('2026-07-20T12:00:06Z', 'assistant', 'access_token=secret-value-that-must-not-leak'),
    ],
  });
  writeRollout(home, {
    id: archivedId,
    archived: true,
    date: '2026-07-19',
    records: [response('2026-07-19T12:00:01Z', 'user', '已经归档的任务')],
  });
  writeRollout(home, {
    id: subagentId,
    source: { subagent: { path: '/root/worker' } },
    records: [response('2026-07-20T12:00:01Z', 'user', '不应显示的子代理')],
  });

  const service = new HistoryService({ defaultCodexHome: home });
  const all = await service.listThreads({ includeArchived: true });
  assert.deepEqual(new Set(all.threads.map((item) => item.id)), new Set([activeId, archivedId]));
  assert.equal(all.threads.find((item) => item.id === activeId).title, '修复项目中的登录问题');
  assert.equal(all.threads.find((item) => item.id === activeId).model, 'gpt-5.6-sol');
  assert.equal(all.threads.find((item) => item.id === archivedId).archived, true);
  assert.doesNotMatch(JSON.stringify(all), /secret-value-that-must-not-leak/);

  const activeOnly = await service.getHistory({ includeArchived: false });
  assert.deepEqual(activeOnly.threads.map((item) => item.id), [activeId]);
  const search = await service.searchHistory({ query: '海王星' });
  assert.deepEqual(search.threads.map((item) => item.id), [activeId]);
  const noResult = await service.searchHistory({ query: '不应显示的子代理' });
  assert.equal(noResult.threads.length, 0);
});

test('readThread returns only filtered user/assistant text, deduplicates events, and redacts secrets', async (t) => {
  const { home } = temporaryHome(t);
  const id = '019f6d10-7f43-7a84-89c6-b94ba0c82461';
  const timestamp = '2026-07-20T12:00:02.000Z';
  writeRollout(home, {
    id,
    records: [
      '{malformed-json',
      response('2026-07-20T12:00:01Z', 'developer', '内部开发者提示不能显示'),
      event(timestamp, 'user_message', '重复的用户消息'),
      response(timestamp, 'user', '重复的用户消息'),
      response('2026-07-20T12:00:03Z', 'user', [
        '# Files mentioned by the user:',
        '',
        '## screenshot.png: /tmp/private.png',
        '',
        '## My request for Codex:',
        '只显示真正的请求',
      ].join('\n')),
      response('2026-07-20T12:00:04Z', 'assistant',
        '结果 sk-abcdefghijklmnop and Bearer abcdefghijklmnop and OPENAI_API_KEY="opaque-secret-value"'),
      event('2026-07-20T12:00:05Z', 'token_count', '工具载荷不能显示'),
    ],
  });

  const service = new HistoryService({ defaultCodexHome: home });
  const transcript = await service.readThread({ threadId: id });
  assert.equal(transcript.status, 'available');
  assert.deepEqual(transcript.messages.map((item) => item.role), ['user', 'user', 'assistant']);
  assert.equal(transcript.messages[0].text, '重复的用户消息');
  assert.equal(transcript.messages[1].text, '只显示真正的请求');
  assert.match(transcript.messages[2].text, /\[REDACTED\]/);
  assert.doesNotMatch(JSON.stringify(transcript), /内部开发者提示|工具载荷|abcdefghijklmnop|opaque-secret-value/);
  assert.equal(transcript.ignoredMalformedLines, 1);

  const limited = await service.readThread({ threadId: id, maxMessages: 2 });
  assert.equal(limited.messages.length, 2);
  assert.equal(limited.isTruncated, true);
});

test('invalid IDs and missing sources return bounded, renderer-safe results', async (t) => {
  const { home } = temporaryHome(t);
  const service = new HistoryService({ defaultCodexHome: home });
  const invalid = await service.readThread({ threadId: '../../auth.json' });
  assert.equal(invalid.status, 'unavailable');
  assert.equal(invalid.messages.length, 0);
  const missing = await service.readThread({ threadId: '019f6d10-7f43-7a84-89c6-b94ba0c82462' });
  assert.equal(missing.status, 'source_missing');
  await assert.rejects(
    service.deleteThread({ threadId: '../../auth.json' }),
    /任务 ID 无效/,
  );
});

test('archive and delete prefer an injected Codex command runner', async (t) => {
  const { root, home } = temporaryHome(t);
  const calls = [];
  const service = new HistoryService({
    defaultCodexHome: home,
    commandRunner: async (request) => {
      calls.push(request);
      return { code: 0 };
    },
  });
  const id = '019f6d10-7f43-7a84-89c6-b94ba0c82471';
  assert.deepEqual(await service.setThreadArchived({ threadId: id, archived: true }), {
    ok: true, threadId: id, archived: true, changed: true, via: 'codex',
  });
  await service.setThreadArchived({ threadId: id, archived: false });
  await service.deleteThread({ threadId: id });
  assert.deepEqual(calls.map((item) => item.args), [
    ['archive', id],
    ['unarchive', id],
    ['delete', '--force', id],
  ]);
  assert.ok(calls.every((item) => item.codexHome === fs.realpathSync(home)));
  await assert.rejects(
    service.listThreads({ codexHome: root }),
    /未授权的 CODEX_HOME/,
  );
});

test('command errors are bounded and secret-redacted', async (t) => {
  const { home } = temporaryHome(t);
  const service = new HistoryService({
    defaultCodexHome: home,
    commandRunner: async () => ({ code: 1, stderr: 'failed sk-abcdefghijklmnop' }),
  });
  await assert.rejects(
    service.deleteThread({ threadId: '019f6d10-7f43-7a84-89c6-b94ba0c82472' }),
    (error) => /永久删除任务失败/.test(error.message) && !/abcdefghijklmnop/.test(error.message),
  );
});

test('filesystem fallback archives, restores, and permanently deletes only the exact rollout', async (t) => {
  const { home } = temporaryHome(t);
  const id = '019f6d10-7f43-7a84-89c6-b94ba0c82481';
  const original = writeRollout(home, {
    id,
    date: '2026-07-18',
    records: [response('2026-07-18T12:00:01Z', 'user', '本地归档测试')],
  });
  const service = new HistoryService({ defaultCodexHome: home });

  const archived = await service.setThreadArchived({ threadId: id, archived: true });
  const archivePath = path.join(home, 'archived_sessions', path.basename(original));
  assert.equal(archived.via, 'filesystem');
  assert.equal(fs.existsSync(original), false);
  assert.equal(fs.existsSync(archivePath), true);
  assert.equal((await service.listThreads()).threads[0].archived, true);

  const restored = await service.setThreadArchived({ threadId: id, archived: false });
  const restoredPath = path.join(home, 'sessions', '2026', '07', '18', path.basename(original));
  assert.equal(restored.changed, true);
  assert.equal(fs.existsSync(archivePath), false);
  assert.equal(fs.existsSync(restoredPath), true);

  const deleted = await service.deleteThread({ threadId: id });
  assert.equal(deleted.deletedFiles, 1);
  assert.equal(fs.existsSync(restoredPath), false);
  assert.equal((await service.readThread({ threadId: id })).status, 'source_missing');
});

test('permanent delete removes exact duplicate copies but preserves unrelated files', async (t) => {
  const { home } = temporaryHome(t);
  const id = '019f6d10-7f43-7a84-89c6-b94ba0c82491';
  const otherId = '019f6d10-7f43-7a84-89c6-b94ba0c82492';
  const active = writeRollout(home, { id, records: [response('2026-07-20T12:00:01Z', 'user', 'active')] });
  const archived = writeRollout(home, { id, archived: true, records: [response('2026-07-20T12:00:01Z', 'user', 'archive')] });
  const other = writeRollout(home, { id: otherId, records: [response('2026-07-20T12:00:01Z', 'user', 'keep')] });
  const service = new HistoryService({ defaultCodexHome: home });
  const result = await service.deleteThread({ threadId: id });
  assert.equal(result.deletedFiles, 2);
  assert.equal(fs.existsSync(active), false);
  assert.equal(fs.existsSync(archived), false);
  assert.equal(fs.existsSync(other), true);
});

test('symbolic-link JSONL entries are ignored and never deleted', async (t) => {
  const { root, home } = temporaryHome(t);
  const id = '019f6d10-7f43-7a84-89c6-b94ba0c82501';
  const outside = path.join(root, `rollout-2026-07-20T12-00-00-${id}.jsonl`);
  fs.writeFileSync(outside, `${JSON.stringify({
    type: 'session_meta', payload: { id, source: 'cli' },
  })}\n${JSON.stringify(response('2026-07-20T12:00:01Z', 'user', 'outside'))}\n`);
  const sessions = path.join(home, 'sessions');
  fs.mkdirSync(sessions);
  const link = path.join(sessions, path.basename(outside));
  try {
    fs.symlinkSync(outside, link, 'file');
  } catch (error) {
    if (error.code === 'EPERM' || error.code === 'EACCES') {
      t.skip('当前 Windows 环境不允许创建测试符号链接');
      return;
    }
    throw error;
  }
  const service = new HistoryService({ defaultCodexHome: home });
  assert.equal((await service.listThreads()).threads.length, 0);
  assert.equal((await service.deleteThread({ threadId: id })).deletedFiles, 0);
  assert.equal(fs.existsSync(outside), true);
});
