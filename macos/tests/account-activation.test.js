const assert = require('node:assert/strict');
const test = require('node:test');

const {
  createAccountActivationQueue,
  launchThenCommitAccount,
} = require('../src/services/account-activation');

test('failed desktop launch never commits current account or last-used metadata', async () => {
  const calls = [];
  await assert.rejects(
    launchThenCommitAccount({
      accountId: 'target',
      currentAccountId: 'old',
      launch: async () => {
        calls.push('launch');
        throw new Error('handoff');
      },
      setCurrentAccount: async () => calls.push('set-current'),
      markAccountUsed: async () => calls.push('mark-used'),
    }),
    /handoff/,
  );
  assert.deepEqual(calls, ['launch']);
});

test('successful desktop launch commits metadata only after the process is confirmed', async () => {
  const calls = [];
  let confirmLaunch;
  const launchConfirmed = new Promise((resolve) => { confirmLaunch = resolve; });
  const pending = launchThenCommitAccount({
    accountId: 'target',
    currentAccountId: 'old',
    launch: async () => {
      calls.push('launch-started');
      await launchConfirmed;
      calls.push('launch-confirmed');
      return { ok: true, pid: 4_242 };
    },
    setCurrentAccount: async (id) => calls.push(`set-current:${id}`),
    markAccountUsed: async (id) => calls.push(`mark-used:${id}`),
  });
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(calls, ['launch-started']);
  confirmLaunch();

  assert.deepEqual(await pending, { ok: true, pid: 4_242 });
  assert.deepEqual(calls, [
    'launch-started',
    'launch-confirmed',
    'set-current:target',
    'mark-used:target',
  ]);
});

test('reapplying the recorded current account still waits for launch but avoids duplicate switch history', async () => {
  const calls = [];
  await launchThenCommitAccount({
    accountId: 'target',
    currentAccountId: 'target',
    launch: async () => {
      calls.push('launch-confirmed');
      return { ok: true };
    },
    setCurrentAccount: async () => calls.push('set-current'),
    markAccountUsed: async () => calls.push('mark-used'),
  });
  assert.deepEqual(calls, ['launch-confirmed', 'mark-used']);
});

test('theme-only launch records use without changing the selected account', async () => {
  const calls = [];
  await launchThenCommitAccount({
    accountId: 'theme-target',
    currentAccountId: 'current',
    selectAsCurrent: false,
    launch: async () => ({ ok: true }),
    setCurrentAccount: async () => calls.push('set-current'),
    markAccountUsed: async () => calls.push('mark-used'),
  });
  assert.deepEqual(calls, ['mark-used']);
});

test('last-used metadata failure does not report a confirmed account switch as failed', async () => {
  const calls = [];
  const result = await launchThenCommitAccount({
    accountId: 'target',
    currentAccountId: 'old',
    launch: async () => ({ ok: true, pid: 7_777 }),
    setCurrentAccount: async () => calls.push('set-current'),
    markAccountUsed: async () => { throw new Error('disk full'); },
  });
  assert.deepEqual(calls, ['set-current']);
  assert.equal(result.ok, true);
  assert.equal(result.pid, 7_777);
  assert.match(result.metadataWarning, /最近使用时间未能保存/);
  assert.doesNotMatch(result.metadataWarning, /disk full/);
});

test('activation queue serializes reapply and switch so a stale current-account snapshot cannot win', async () => {
  const runAccountActivation = createAccountActivationQueue();
  const calls = [];
  let currentAccountId = 'account-a';
  let releaseFirstLaunch;
  const firstLaunchGate = new Promise((resolve) => { releaseFirstLaunch = resolve; });

  const reapplyA = runAccountActivation(async () => {
    const capturedCurrentAccountId = currentAccountId;
    return launchThenCommitAccount({
      accountId: 'account-a',
      currentAccountId: capturedCurrentAccountId,
      launch: async () => {
        calls.push('launch-a-started');
        await firstLaunchGate;
        calls.push('launch-a-finished');
        return { ok: true };
      },
      setCurrentAccount: async (id) => {
        calls.push(`set-current:${id}`);
        currentAccountId = id;
      },
      markAccountUsed: async () => {},
    });
  });
  await new Promise((resolve) => setImmediate(resolve));

  const switchToB = runAccountActivation(async () => {
    calls.push('switch-b-entered');
    const capturedCurrentAccountId = currentAccountId;
    return launchThenCommitAccount({
      accountId: 'account-b',
      currentAccountId: capturedCurrentAccountId,
      launch: async () => {
        calls.push('launch-b');
        return { ok: true };
      },
      setCurrentAccount: async (id) => {
        calls.push(`set-current:${id}`);
        currentAccountId = id;
      },
      markAccountUsed: async () => {},
    });
  });

  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(calls, ['launch-a-started']);
  releaseFirstLaunch();
  await Promise.all([reapplyA, switchToB]);

  assert.equal(currentAccountId, 'account-b');
  assert.deepEqual(calls, [
    'launch-a-started',
    'launch-a-finished',
    'switch-b-entered',
    'launch-b',
    'set-current:account-b',
  ]);
});

test('activation queue continues after a failed operation', async () => {
  const runAccountActivation = createAccountActivationQueue();
  const calls = [];
  const failed = runAccountActivation(async () => {
    calls.push('failed');
    throw new Error('launch failed');
  });
  const succeeded = runAccountActivation(async () => {
    calls.push('succeeded');
    return { ok: true };
  });

  await assert.rejects(failed, /launch failed/);
  assert.deepEqual(await succeeded, { ok: true });
  assert.deepEqual(calls, ['failed', 'succeeded']);
});

test('an in-flight login finishes before queued deletion enters', async () => {
  const runAccountActivation = createAccountActivationQueue();
  const calls = [];
  let releaseLogin;
  const loginGate = new Promise((resolve) => { releaseLogin = resolve; });
  const login = runAccountActivation(async () => {
    calls.push('login-started');
    await loginGate;
    calls.push('login-finished');
  });
  await new Promise((resolve) => setImmediate(resolve));
  const deletion = runAccountActivation(async () => {
    calls.push('delete-entered');
  });
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(calls, ['login-started']);
  releaseLogin();
  await Promise.all([login, deletion]);
  assert.deepEqual(calls, ['login-started', 'login-finished', 'delete-entered']);
});

test('a login queued after deletion revalidates the account before spawning', async () => {
  const runAccountActivation = createAccountActivationQueue();
  let accountExists = true;
  let spawned = false;
  let releaseDeletion;
  const deletionGate = new Promise((resolve) => { releaseDeletion = resolve; });
  const deletion = runAccountActivation(async () => {
    await deletionGate;
    accountExists = false;
  });
  await new Promise((resolve) => setImmediate(resolve));
  const login = runAccountActivation(async () => {
    if (!accountExists) throw new Error('账号不存在。');
    spawned = true;
  });
  releaseDeletion();
  await deletion;
  await assert.rejects(login, /账号不存在/);
  assert.equal(spawned, false);
});
