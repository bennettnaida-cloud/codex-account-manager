const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
  configureModelCatalog,
  defaultModel,
  officialRates,
  _test: { parseOfficialPrice },
} = require('../src/services/model-catalog');

test('bundled catalog supplies one default model and long-context official rates', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-model-catalog-'));
  try {
    configureModelCatalog(root);
    assert.equal(defaultModel(), 'gpt-5.6');
    assert.deepEqual(officialRates('gpt-5.6-terra', 1000), [2, 0.2, 2.5, 12]);
    assert.deepEqual(officialRates('gpt-5.6', 300000), [10, 1, 12.5, 45]);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('official parser requires all three token prices before accepting a page', () => {
  const existing = {
    id: 'gpt-5.6-terra',
    aliases: [],
    cacheWriteMultiplier: 1.25,
    longContextThreshold: 272000,
    longInputMultiplier: 2,
    longOutputMultiplier: 1.5,
  };
  const parsed = parseOfficialPrice(
    'Text tokens Per 1M tokens Input $2.00 Cached input $0.20 Output $12.00 ' +
      'Prompts with >272K input tokens are priced at 2x input and 1.5x output. ' +
      'Cache writes are billed at 1.25x the uncached input token rate.',
    existing,
  );
  assert.equal(parsed.inputUsdPerMillion, 2);
  assert.equal(parsed.cachedInputUsdPerMillion, 0.2);
  assert.equal(parsed.outputUsdPerMillion, 12);
  assert.throws(() => parseOfficialPrice('Input $2.00 Output $12.00', existing), /完整/);
});

test('official parser accepts the current markdown table and pro models without a cache discount', () => {
  const parsed = parseOfficialPrice(`
    ### Text tokens
    | Metric | Price | Unit |
    | --- | ---: | --- |
    | Input | $0.75 | 1M tokens |
    | Cached input | $0.075 | 1M tokens |
    | Output | $4.5 | 1M tokens |
  `, { id: 'gpt-5.4-mini', aliases: [], longContextThreshold: 272000, longInputMultiplier: 2, longOutputMultiplier: 1.5 });
  assert.deepEqual(
    [parsed.inputUsdPerMillion, parsed.cachedInputUsdPerMillion, parsed.outputUsdPerMillion],
    [0.75, 0.075, 4.5],
  );
  assert.equal(parsed.usesLongContextPricing, false);

  const pro = parseOfficialPrice(`
    ### Text tokens
    | Metric | Price | Unit |
    | --- | ---: | --- |
    | Input | $30 | 1M tokens |
    | Output | $180 | 1M tokens |
  `, { id: 'gpt-5.5-pro', aliases: [], longContextThreshold: 272000, longInputMultiplier: 2, longOutputMultiplier: 1.5 });
  assert.deepEqual(
    [pro.inputUsdPerMillion, pro.cachedInputUsdPerMillion, pro.outputUsdPerMillion],
    [30, 30, 180],
  );
});
