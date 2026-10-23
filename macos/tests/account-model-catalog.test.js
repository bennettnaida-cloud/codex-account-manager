const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { AccountModelCatalogService, projectCatalog } = require('../src/services/account-model-catalog');

function fixture(kind = 'access_token') {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-model-refresh-'));
  const account = { id: 'fixture', codexHome: root, authKind: kind,
    apiBaseUrl: 'https://provider.example/v1', apiModel: 'gpt-6-astra' };
  fs.writeFileSync(path.join(root, 'auth.json'), JSON.stringify({ OPENAI_API_KEY: 'at-' + 'fixture'.repeat(4) }));
  fs.writeFileSync(path.join(root, 'config.toml'), 'model = "gpt-6-astra"\n');
  const store = { loadAccounts: () => [account] };
  return { root, account, store, clean: () => fs.rmSync(root, { recursive: true, force: true }) };
}

test('AT refresh bypasses static catalog/cache, uses the PAT workspace and preserves future metadata', async () => {
  const f = fixture();
  try {
    const file = path.join(f.root, '.codex-account-manager-models.json');
    fs.writeFileSync(file, '{"models":[{"slug":"old-model"}]}');
    fs.appendFileSync(path.join(f.root, 'config.toml'), `model_catalog_json = ${JSON.stringify(file)} # codex-account-manager-model-catalog\n`);
    const calls = [];
    const service = new AccountModelCatalogService({ store: f.store, fetchForAccount: async () => async (url, options) => {
      calls.push({ url, headers: { ...options.headers } });
      return Response.json(url.includes('whoami') ? { chatgpt_account_id: 'fixture-workspace' } :
        { models: [{ slug: 'gpt-99.1-sol', visibility: 'list', future: { enabled: true } }] });
    } });
    assert.equal((await service.refresh(f.account)).count, 1);
    assert.equal(calls.length, 2);
    assert.equal(calls[1].headers['chatgpt-account-id'], 'fixture-workspace');
    assert.match(calls[1].url, /client_version=0.163.0/);
    assert.deepEqual(JSON.parse(fs.readFileSync(file)).models[0].future, { enabled: true });
    assert.match(fs.readFileSync(path.join(f.root, 'config.toml'), 'utf8'), /model = "gpt-6-astra"/);
    await service.refresh(f.account, { force: false });
    assert.equal(calls.length, 2, 'verified short-term cache should avoid another request');
    service.clientVersion = async () => '0.164.0';
    await service.refresh(f.account, { force: false });
    assert.equal(calls.length, 4, 'client upgrade must invalidate an otherwise fresh receipt');
    assert.match(calls[3].url, /client_version=0.164.0/);
    const good = fs.readFileSync(file, 'utf8');
    service.fetchForAccount = async () => async () => new Response('{}', { status: 401 });
    await assert.rejects(service.refresh(f.account), /HTTP 401/);
    assert.equal(fs.readFileSync(file, 'utf8'), good);
    service.fetchForAccount = async () => async (url) => {
      if (url.includes('whoami')) return Response.json({ chatgpt_account_id: 'fixture-workspace' });
      fs.writeFileSync(path.join(f.root, 'auth.json'), '{"OPENAI_API_KEY":"changed"}');
      return Response.json({ models: [{ slug: 'gpt-99.2-sol' }] });
    };
    await assert.rejects(service.refresh(f.account), /已变化/);
    assert.equal(fs.readFileSync(file, 'utf8'), good);
  } finally { f.clean(); }
});

test('compatible API imports only provider-listed coding models and leaves custom TOML catalogs intact', async () => {
  const f = fixture('compatible_api');
  try {
    const original = 'model = "gpt-6-astra"\nmodel_catalog_json = "/custom/catalog.json"\n';
    fs.writeFileSync(path.join(f.root, 'config.toml'), original);
    const service = new AccountModelCatalogService({ store: f.store, fetchForAccount: async () => async (url) => {
      assert.equal(url, 'https://provider.example/v1/models');
      return Response.json({ data: [{ id: 'gpt-99.1-sol' }, { id: 'gpt-99.1-sol' }, { id: 'gpt-image-1' }] });
    } });
    await service.refresh(f.account);
    const models = JSON.parse(fs.readFileSync(path.join(f.root, '.codex-account-manager-models.json'))).models;
    assert.deepEqual(models.map((entry) => entry.slug), ['gpt-99.1-sol']);
    assert.deepEqual(models[0].supported_reasoning_levels, []);
    assert.equal(fs.readFileSync(path.join(f.root, 'config.toml'), 'utf8'), original);
    assert.equal(projectCatalog(original, '/ignored.json'), original);
  } finally { f.clean(); }
});

test('offline, malformed, oversized and cancelled refreshes retain the last usable catalog', async () => {
  const f = fixture();
  try {
    let mode = 'success';
    const service = new AccountModelCatalogService({ store: f.store, fetchForAccount: async () => async (url) => {
      if (url.includes('whoami')) return Response.json({ chatgpt_account_id: 'fixture-workspace' });
      if (mode === 'malformed') return new Response('not json');
      if (mode === 'empty') return Response.json({ models: [] });
      if (mode === 'oversized') return new Response('{}', { headers: { 'content-length': 34 * 1024 * 1024 } });
      return Response.json({ models: [{ slug: 'gpt-99.1-sol' }] });
    } });
    await service.refresh(f.account);
    const file = path.join(f.root, '.codex-account-manager-models.json');
    const good = fs.readFileSync(file, 'utf8');
    for (mode of ['malformed', 'empty', 'oversized']) {
      await assert.rejects(service.refresh(f.account));
      assert.equal(fs.readFileSync(file, 'utf8'), good);
    }
    await assert.rejects(service.refresh(f.account, { signal: AbortSignal.abort() }));
    assert.equal(fs.readFileSync(file, 'utf8'), good);
  } finally { f.clean(); }
});
