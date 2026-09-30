'use strict';
// Black-box regression against the published executable. Only loopback fixtures and
// fake credentials are used; a fresh metadata fixture prevents external HTTP calls.
const {spawn} = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const assert = require('node:assert/strict');
const exe = process.argv[2];
const codex = process.argv[3];
if (!exe) throw new Error('Usage: node Test-AutomaticModelCatalog.cjs <manager-exe> [codex-exe]');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-auto-models-'));
const clientHome = path.join(root, 'codex-client');
fs.mkdirSync(clientHome);
const catalogName = '.codex-account-manager-compatible-models.json';
const accountsFile = path.join(root, 'accounts.json');
const sourceFile = path.join(root, 'compatible-model-templates.official.json');
const seed = JSON.parse(fs.readFileSync(path.join(path.dirname(exe), 'assets/codex-models/gpt-6-sol.json'))).models[0];
const published = {...seed, slug:'gpt-9.8-sol', display_name:'Official fixture model', context_window:65536};
let accounts;
let responder = (_req, res) => res.end(JSON.stringify({data:[{id:'gpt-6-sol'}, {id:'gpt-9.8-sol'}, {id:'gpt-9.9-sol'}]}));
const server = http.createServer((req, res) => {
  assert.equal(req.headers.authorization, 'Bearer fixture-key');
  res.setHeader('Content-Type', 'application/json');
  responder(req, res);
});
function writeAccounts() {fs.writeFileSync(accountsFile, JSON.stringify(accounts));}
function metadata(models=[published]) {fs.writeFileSync(sourceFile, JSON.stringify({models}));}
function readModels(index=0) {return JSON.parse(fs.readFileSync(path.join(accounts[index].codexHome,catalogName))).models;}
function run(command, args) {
  return new Promise((resolve,reject) => {
    const child=spawn(command,args,{windowsHide:true,stdio:['ignore','pipe','pipe'],env:{...process.env,CODEX_ACCOUNT_MANAGER_HOME:root,CODEX_HOME:clientHome}});
    let output='';const timer=setTimeout(()=>{child.kill();reject(new Error('Fixture process timed out'));},25000);
    child.stdout.on('data', chunk=>output+=chunk);child.stderr.on('data', chunk=>output+=chunk);
    child.on('error', error=>{clearTimeout(timer);reject(error);});
    child.on('close',code=>{clearTimeout(timer);resolve({code,output});});
  });
}
async function refresh(index=0, expectedCode=0) {
  const result=await run(exe,['--manager-root',root,'--refresh-compatible-model-catalog',accounts[index].name]);
  assert.equal(result.code,expectedCode,result.output);
}
(async()=>{
 try {
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const port=server.address().port;
  accounts=[0,1].map(index=>({name:`fixture-${index}`,authKind:'compatible_api',apiBaseUrl:`http://127.0.0.1:${port}/${index}`,apiWireApi:'responses',apiModel:'gpt-6-sol',codexHome:path.join(root,`account-${index}`),useBundledCompatibleApiModelCatalog:false}));
  for(const account of accounts){fs.mkdirSync(account.codexHome);fs.writeFileSync(path.join(account.codexHome,'auth.json'),JSON.stringify({OPENAI_API_KEY:'fixture-key'}));fs.writeFileSync(path.join(account.codexHome,'config.toml'),'model = "gpt-6-sol"\nmodel_catalog_json = "C:/custom.json"\n');}
  writeAccounts();metadata();
  await refresh();
  let models=readModels();
  assert.deepEqual(models.map(x=>x.slug),['gpt-6-sol','gpt-9.8-sol','gpt-9.9-sol']);
  assert.equal(models[1].context_window,65536,'Official metadata must take precedence');
  assert.equal(models[2].context_window,32768,'Unpublished model must get conservative fallback');
  assert.deepEqual(models[2].supported_reasoning_levels,[]);
  assert.deepEqual(models[2].service_tiers,[]);
  assert.deepEqual(models[2].input_modalities,['text']);
  assert.equal(JSON.parse(fs.readFileSync(accountsFile))[0].apiModel,'gpt-6-sol');
  assert.match(fs.readFileSync(path.join(accounts[0].codexHome,'config.toml'),'utf8'),/model_catalog_json = "C:\/custom.json"/);
  console.log('PASS: future models, official metadata, conservative fallback, default and custom config preservation');
  if(codex){const result=await run(process.execPath,[path.join(__dirname,'Test-ConfiguredModelList.cjs'),codex,path.join(accounts[0].codexHome,catalogName),'gpt-6-sol',models.map(x=>x.slug).join(',')]);assert.equal(result.code,0,result.output);console.log(result.output.trim());}
  const good=fs.readFileSync(path.join(accounts[0].codexHome,catalogName),'utf8');
  for(const [name,handler,code] of [
    ['401',(_req,res)=>{res.statusCode=401;res.end('{}');},1],
    ['empty',(_req,res)=>res.end('{"data":[]}'),0],
    ['malformed',(_req,res)=>res.end('not-json'),0],
    ['unsupported',(_req,res)=>res.end(JSON.stringify({data:[{id:'gpt-image-1'},{id:'gpt-6-audio'},{id:'gpt-9/../../secret'}]})),0],
    ['timeout',()=>{},1]]) {
    responder=handler;await refresh(0,code);assert.equal(fs.readFileSync(path.join(accounts[0].codexHome,catalogName),'utf8'),good,`${name} must retain last good catalog`);
  }
  console.log('PASS: unauthorized, timeout, malformed, empty and unsupported responses preserve the last good catalog');
  responder=(_req,res)=>res.end(JSON.stringify({data:[{id:'gpt-6-sol'}]}));
  await refresh(1);assert.deepEqual(readModels(1).map(x=>x.slug),['gpt-6-sol']);assert.equal(fs.readFileSync(path.join(accounts[0].codexHome,catalogName),'utf8'),good);
  console.log('PASS: account/provider whitelist isolation');
  responder=(_req,res)=>{accounts[0].apiBaseUrl=`http://127.0.0.1:${port}/changed`;writeAccounts();res.end(JSON.stringify({data:[{id:'gpt-9.7-sol'}]}));};
  await refresh();assert.equal(fs.readFileSync(path.join(accounts[0].codexHome,catalogName),'utf8'),good);
  responder=(_req,res)=>{fs.writeFileSync(path.join(accounts[0].codexHome,'auth.json'),JSON.stringify({OPENAI_API_KEY:'changed-fixture-key'}));res.end(JSON.stringify({data:[{id:'gpt-9.7-sol'}]}));};
  await refresh();assert.equal(fs.readFileSync(path.join(accounts[0].codexHome,catalogName),'utf8'),good);
  console.log('PASS: results fetched before endpoint/key changes cannot overwrite the current catalog');
  fs.writeFileSync(path.join(accounts[0].codexHome,'auth.json'),JSON.stringify({OPENAI_API_KEY:'fixture-key'}));
  accounts[0].useBundledCompatibleApiModelCatalog=true;writeAccounts();
  responder=(_req,res)=>res.end(JSON.stringify({data:[{id:'gpt-9.9-sol'},{id:'gpt-9.9-sol'}]}));
  await refresh();assert.equal(readModels().filter(x=>x.slug==='gpt-9.9-sol').length,1);
  responder=(_req,res)=>res.end('{"data":[]}');await refresh();assert(readModels().some(x=>x.slug==='gpt-9.9-sol'));
  console.log('PASS: bundled mode retains newly discovered models and removes duplicates');
 } finally {server.closeAllConnections();await new Promise(resolve=>server.close(resolve));fs.rmSync(root,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exitCode=1;});
