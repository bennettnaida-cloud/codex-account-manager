async function launchThenCommitAccount({
  accountId,
  currentAccountId,
  selectAsCurrent = true,
  launch,
  setCurrentAccount,
  markAccountUsed,
}) {
  if (typeof launch !== 'function' || typeof setCurrentAccount !== 'function' ||
      typeof markAccountUsed !== 'function') {
    throw new TypeError('账号启动事务缺少必要操作。');
  }
  const result = await launch();
  if (selectAsCurrent && String(currentAccountId || '') !== String(accountId || '')) {
    await setCurrentAccount(accountId);
  }
  try {
    await markAccountUsed(accountId);
    return result;
  } catch {
    return {
      ...(result && typeof result === 'object' ? result : { result }),
      metadataWarning: '账号已切换，但最近使用时间未能保存。',
    };
  }
}

function createAccountActivationQueue() {
  let tail = Promise.resolve();
  return function runAccountActivation(operation) {
    if (typeof operation !== 'function') {
      return Promise.reject(new TypeError('账号启动队列缺少可执行操作。'));
    }
    const pending = tail.then(() => operation());
    tail = pending.catch(() => {});
    return pending;
  };
}

module.exports = { createAccountActivationQueue, launchThenCommitAccount };
