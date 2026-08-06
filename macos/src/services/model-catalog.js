const fs = require('node:fs');
const https = require('node:https');
const path = require('node:path');

const MODELS_URL = 'https://developers.openai.com/api/docs/models';
const COMPARE_URL = 'https://developers.openai.com/api/docs/models/compare';
let userOverridePath = null;
let currentCatalog = null;

function bundledCandidates() {
  return [
    path.join(__dirname, '..', '..', 'model-catalog.json'),
    path.join(__dirname, '..', '..', '..', 'assets', 'model-catalog.json'),
  ];
}

function normalizeModel(value) {
  return String(value || '').trim().toLowerCase().replace(/[ _]+/g, '-');
}

function modelMatches(model, value) {
  const normalized = normalizeModel(value);
  return normalized.includes(normalizeModel(model.id)) ||
    (model.aliases || []).some((alias) => normalized === normalizeModel(alias));
}

function validateCatalog(catalog) {
  if (catalog?.schemaVersion !== 1 || !Array.isArray(catalog.models) || !catalog.models.length ||
      !catalog.models.some((model) => modelMatches(model, catalog.defaultModel))) {
    throw new Error('模型价格目录结构无效。');
  }
  for (const model of catalog.models) {
    const prices = [model.inputUsdPerMillion, model.cachedInputUsdPerMillion, model.outputUsdPerMillion];
    if (!/^gpt-[a-z0-9.-]+$/i.test(String(model.id || '')) ||
        prices.some((value) => !Number.isFinite(Number(value)) || Number(value) <= 0 || Number(value) > 1000)) {
      throw new Error(`模型 ${model.id || '未知'} 的价格目录数据无效。`);
    }
  }
  return catalog;
}

function loadCatalog() {
  for (const candidate of [userOverridePath, ...bundledCandidates()].filter(Boolean)) {
    try {
      if (!fs.statSync(candidate).isFile()) continue;
      return validateCatalog(JSON.parse(fs.readFileSync(candidate, 'utf8')));
    } catch {
      // Ignore a damaged override and continue to the bundled, last-known-good catalog.
    }
  }
  throw new Error('缺少有效的模型价格目录 model-catalog.json。');
}

function configureModelCatalog(userDataPath) {
  userOverridePath = path.join(path.resolve(userDataPath), 'model-catalog.official.json');
  currentCatalog = loadCatalog();
  return currentCatalog;
}

function catalog() {
  currentCatalog ||= loadCatalog();
  return currentCatalog;
}

function defaultModel() {
  return catalog().defaultModel;
}

function defaultReasoningEffort() {
  return catalog().defaultReasoningEffort || 'medium';
}

function officialRates(modelName, inputTokens = 0) {
  const model = [...catalog().models]
    .sort((left, right) => String(right.id).length - String(left.id).length)
    .find((candidate) => modelMatches(candidate, modelName));
  if (!model) return null;
  const longContext = model.usesLongContextPricing !== false &&
    Number(inputTokens) > Number(model.longContextThreshold || 272000);
  const inputMultiplier = longContext ? Number(model.longInputMultiplier || 2) : 1;
  const outputMultiplier = longContext ? Number(model.longOutputMultiplier || 1.5) : 1;
  const input = Number(model.inputUsdPerMillion) * inputMultiplier;
  return [
    input,
    Number(model.cachedInputUsdPerMillion) * inputMultiplier,
    input * Number(model.cacheWriteMultiplier || 1.25),
    Number(model.outputUsdPerMillion) * outputMultiplier,
  ];
}

function normalizedDocumentText(source) {
  return String(source || '')
    .replace(/<[^>]+>/g, ' ')
    .replace(/&nbsp;/gi, ' ')
    .replace(/&amp;/gi, '&')
    .replace(/\s+/g, ' ')
    .trim();
}

function parseOfficialPrice(source, existing) {
  const text = normalizedDocumentText(source);
  const match = /Text tokens.{0,600}?\|\s*Input\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|.{0,160}?\|\s*Cached input\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|.{0,160}?\|\s*Output\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|/i.exec(text) ||
    /Text tokens\s+Per 1M tokens\s+Input\s+\$([0-9]+(?:\.[0-9]+)?)\s+Cached input\s+\$([0-9]+(?:\.[0-9]+)?)\s+Output\s+\$([0-9]+(?:\.[0-9]+)?)/i.exec(text);
  const noCacheDiscount = match ? null :
    /Text tokens.{0,600}?\|\s*Input\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|.{0,220}?\|\s*Output\s*\|\s*\$([0-9]+(?:\.[0-9]+)?)\s*\|\s*1M tokens\s*\|/i.exec(text);
  if (!match && !noCacheDiscount) {
    throw new Error(`官网页面没有包含完整的 ${existing.id} 输入、缓存输入与输出价格。`);
  }
  const input = Number((match || noCacheDiscount)[1]);
  const cachedInput = match ? Number(match[2]) : input;
  const output = Number(match ? match[3] : noCacheDiscount[2]);
  const aliases = [...(existing.aliases || [])];
  const alias = /`?\b(gpt-\d+(?:\.\d+)*)\b`?\s+alias\s+routes\s+requests\s+to/i.exec(text)?.[1];
  if (alias && !aliases.some((value) => value.toLowerCase() === alias.toLowerCase())) aliases.push(alias.toLowerCase());
  return {
    ...existing,
    aliases,
    inputUsdPerMillion: input,
    cachedInputUsdPerMillion: cachedInput,
    outputUsdPerMillion: output,
    cacheWriteMultiplier: /cache writes are billed at\s*1\.25x/i.test(text) ? 1.25 : 1,
    usesLongContextPricing: />\s*272K input tokens/i.test(text) && /2x input/i.test(text) && /1\.5x output/i.test(text),
    longContextThreshold: />\s*272K input tokens/i.test(text) ? 272000 : existing.longContextThreshold,
    longInputMultiplier: /2x input/i.test(text) ? 2 : existing.longInputMultiplier,
    longOutputMultiplier: /1\.5x output/i.test(text) ? 1.5 : existing.longOutputMultiplier,
  };
}

function discoverTrackedModelIds(indexText, previous) {
  const discovered = previous.models.map((model) => model.id);
  const pattern = /\/api\/docs\/models\/(gpt-(\d+(?:\.\d+)*)(?:-(?:sol|terra|luna|mini|nano|pro))?)(?:\.md)?(?=[)\s?#])/gi;
  for (const match of indexText.matchAll(pattern)) {
    const parts = match[2].split('.').map(Number);
    const atLeast54 = parts[0] > 5 || (parts[0] === 5 && (parts[1] || 0) >= 4);
    if (!atLeast54 || discovered.some((id) => id.toLowerCase() === match[1].toLowerCase())) continue;
    discovered.push(match[1].toLowerCase());
  }
  return discovered;
}

function detectDefaultModelId(indexText, models) {
  for (const model of models) {
    const flexibleName = model.id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replaceAll('-', '[- ]');
    if (new RegExp(`not sure where to start.{0,120}?${flexibleName}`, 'i').test(indexText) ||
        new RegExp(`${flexibleName}.{0,100}?Start here`, 'i').test(indexText)) return model.id;
  }
  for (const model of models) {
    if (indexText.toLowerCase().includes(`${model.id.replace(/-/g, ' ')} default`)) return model.id;
  }
  return /\b(gpt-\d+(?:\.\d+)*(?:-[a-z0-9-]+)?)\b.{0,100}?\bDefault\b/i.exec(indexText)?.[1]?.toLowerCase() || null;
}

function trustedDocsUrl(value) {
  const url = new URL(value);
  if (url.protocol !== 'https:' || url.hostname !== 'developers.openai.com') {
    throw new Error(`拒绝访问非 OpenAI 官方地址：${url.origin}`);
  }
  return url;
}

function requestText(url, { requestImpl = https.get } = {}) {
  return new Promise((resolve, reject) => {
    const requestUrl = trustedDocsUrl(url);
    const request = requestImpl(requestUrl, {
      headers: {
        Accept: 'text/markdown, text/html;q=0.8',
        'User-Agent': `CodexAccountManager/${process.versions.electron || process.versions.node}`,
      },
    }, (response) => {
      if (response.statusCode !== 200) {
        response.resume();
        reject(new Error(`OpenAI 官网返回 HTTP ${response.statusCode || '未知状态'}。`));
        return;
      }
      const chunks = [];
      let total = 0;
      response.on('data', (chunk) => {
        total += chunk.length;
        if (total > 2_000_000) response.destroy(new Error('官网返回内容超过安全大小限制。'));
        else chunks.push(chunk);
      });
      response.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
      response.on('error', reject);
    });
    request.setTimeout(30_000, () => request.destroy(new Error('连接 OpenAI 官网超时。')));
    request.on('error', reject);
  });
}

async function downloadOfficialText(pageUrl, options) {
  let lastError;
  for (const candidate of [`${pageUrl}.md`, pageUrl]) {
    try {
      return await requestText(candidate, options);
    } catch (error) {
      lastError = error;
    }
  }
  throw new Error(`无法读取 OpenAI 官方页面 ${pageUrl}：${lastError?.message || '未知网络错误'}`);
}

function describeChanges(previous, next) {
  const changes = [];
  if (previous.defaultModel.toLowerCase() !== next.defaultModel.toLowerCase()) {
    changes.push(`默认模型：${previous.defaultModel} -> ${next.defaultModel}`);
  }
  for (const model of next.models) {
    const old = previous.models.find((candidate) => candidate.id.toLowerCase() === model.id.toLowerCase());
    if (!old) changes.push(`新增模型：${model.id}`);
    else if (['inputUsdPerMillion', 'cachedInputUsdPerMillion', 'outputUsdPerMillion']
      .some((key) => Number(old[key]) !== Number(model[key]))) {
      changes.push(`${model.id}：输入 $${old.inputUsdPerMillion} -> $${model.inputUsdPerMillion}，缓存 $${old.cachedInputUsdPerMillion} -> $${model.cachedInputUsdPerMillion}，输出 $${old.outputUsdPerMillion} -> $${model.outputUsdPerMillion}`);
    }
  }
  return changes;
}

async function checkAndSaveOfficialCatalog({ userDataPath, requestImpl } = {}) {
  if (userDataPath) configureModelCatalog(userDataPath);
  const previous = catalog();
  const indexText = normalizedDocumentText(await downloadOfficialText(MODELS_URL, { requestImpl }));
  const models = [];
  for (const modelId of discoverTrackedModelIds(indexText, previous)) {
    const existing = previous.models.find((model) => model.id.toLowerCase() === modelId.toLowerCase()) || {
      id: modelId,
      aliases: [],
      cacheWriteMultiplier: 1,
      usesLongContextPricing: false,
      longContextThreshold: 272000,
      longInputMultiplier: 2,
      longOutputMultiplier: 1.5,
    };
    const source = await downloadOfficialText(`${MODELS_URL}/${encodeURIComponent(modelId)}`, { requestImpl });
    models.push(parseOfficialPrice(source, existing));
  }
  const defaultId = detectDefaultModelId(indexText, models);
  let defaultPrice = models.find((model) => modelMatches(model, defaultId));
  if (!defaultPrice) {
    if (!defaultId) throw new Error('官网模型目录中未找到明确的默认模型，已拒绝更新本地目录。');
    const source = await downloadOfficialText(`${MODELS_URL}/${encodeURIComponent(defaultId)}`, { requestImpl });
    defaultPrice = parseOfficialPrice(source, {
      id: defaultId,
      aliases: [],
      cacheWriteMultiplier: 1.25,
      longContextThreshold: 272000,
      longInputMultiplier: 2,
      longOutputMultiplier: 1.5,
    });
    models.push(defaultPrice);
  }
  const next = validateCatalog({
    schemaVersion: 1,
    defaultModel: defaultPrice.aliases?.find((alias) => (alias.match(/-/g) || []).length === 1) || defaultPrice.id,
    defaultReasoningEffort: previous.defaultReasoningEffort || 'medium',
    catalogSource: 'official',
    verifiedAtUtc: new Date().toISOString(),
    sources: [MODELS_URL, COMPARE_URL],
    models,
  });
  const changes = describeChanges(previous, next);
  const outputPath = userOverridePath || path.join(path.resolve(userDataPath || process.cwd()), 'model-catalog.official.json');
  fs.mkdirSync(path.dirname(outputPath), { recursive: true, mode: 0o700 });
  const tempPath = `${outputPath}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tempPath, `${JSON.stringify(next, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 });
  fs.renameSync(tempPath, outputPath);
  currentCatalog = next;
  return { catalog: next, changes };
}

module.exports = {
  MODELS_URL,
  checkAndSaveOfficialCatalog,
  configureModelCatalog,
  defaultModel,
  defaultReasoningEffort,
  officialRates,
  _test: { parseOfficialPrice, validateCatalog },
};
