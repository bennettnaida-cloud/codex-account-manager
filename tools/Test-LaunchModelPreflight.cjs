'use strict';
const {spawn} = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const assert = require('node:assert/strict');
const exe = path.resolve(process.argv[2]);
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-launch-models-'));
const home = path.join(root, 'account');
const shared = path.join(root, 'shared');
fs.mkdirSync(home); fs.mkdirSync(shared);
fs.writeFileSync(path.join(shared, 'auth.json'), '{"unchanged":"fixture"}');
const catalog = path.join(home, '.codex-account-manager-compatible-models.json');
let calls = 0, status = 200, ids = ['gpt-9.1-sol'];
const server = http.createServer((req,res) => {
  calls++;
  assert.equal(req.method, 'GET');
  assert.equal(req.url, '/v1/models');
  assert.equal(req.headers.authorization, 'Bearer fixture-api-key');
  res.writeHead(status, {'Content-Type':'application/json'});
  res.end(JSON.stringify(status === 200 ? {data: ids.map(id=>({id}))} : {error:'fixture-sensitive-error'}));
});
async function preflight(expected=0) {
  const result = await new Promise((resolve,reject) => {
    const child=spawn(exe,['--manager-root',root,'--launch-model-preflight','fixture'],{
      windowsHide:true,env:{...process.env,CODEX_ACCOUNT_MANAGER_HOME:root,CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME:shared},stdio:['ignore','pipe','pipe']});
    let output=''; const timer=setTimeout(()=>{child.kill();reject(new Error('preflight timeout'));},60000);
    child.stdout.on('data',x=>output+=x);child.stderr.on('data',x=>output+=x);
    child.on('error',e=>{clearTimeout(timer);reject(e);});
    child.on('close',code=>{clearTimeout(timer);resolve({code,output});});
  });
  assert.equal(result.code,expected,result.output);
  assert(!result.output.includes('fixture-sensitive-error'));
  assert.equal(fs.readFileSync(path.join(shared,'auth.json'),'utf8'),'{"unchanged":"fixture"}');
}
(async()=>{
  try {
    await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
    const account={name:'fixture',codexHome:home,authKind:'compatible_api',apiBaseUrl:`http://127.0.0.1:${server.address().port}/v1`,apiModel:'gpt-5.5',apiWireApi:'responses',discoverDefaultModelOnLaunch:true};
    fs.writeFileSync(path.join(root,'accounts.json'),JSON.stringify([account]));
    fs.writeFileSync(path.join(home,'auth.json'),'{"OPENAI_API_KEY":"fixture-api-key"}');
    fs.writeFileSync(path.join(home,'config.toml'),'model = "gpt-5.5"\n');
    const template=JSON.parse(fs.readFileSync(path.join(path.dirname(exe),'assets/codex-models/gpt-6-sol.json'))).models[0];
    fs.writeFileSync(path.join(root,'compatible-model-templates.official.json'),JSON.stringify({models:[template]}));
    await preflight();
    const stored=JSON.parse(fs.readFileSync(path.join(root,'accounts.json')))[0];
    assert.equal(stored.apiModel,'gpt-9.1-sol');assert.equal(stored.discoverDefaultModelOnLaunch,false);
    ids.push('gpt-9.2-sol');
    await preflight();
    assert.equal(calls,2,'every launch queries even a fresh catalog');
    assert.deepEqual(JSON.parse(fs.readFileSync(catalog)).models.map(m=>m.slug),ids);
    const lastGood=fs.readFileSync(catalog,'utf8');
    for(const failure of [401,404,503]){status=failure;await preflight(1);assert.equal(fs.readFileSync(catalog,'utf8'),lastGood);}
    status=200; ids=[]; await preflight(1);assert.equal(fs.readFileSync(catalog,'utf8'),lastGood);
    console.log('PASS: every-launch remote query, imported default discovery, future models, failures preserve catalog and shared auth');
  } finally {await new Promise(resolve=>server.close(resolve));fs.rmSync(root,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exitCode=1;});
