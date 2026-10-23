'use strict';
const {spawn} = require('node:child_process');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const codex = process.argv[2] && path.resolve(process.argv[2]);
const catalog = process.argv[3] && path.resolve(process.argv[3]);
const defaultModel = process.argv[4];
const expectedModels = (process.argv[5] || '').split(',').filter(Boolean).sort();
if (!codex || !catalog || !defaultModel || expectedModels.length === 0) {
  console.error('Usage: node Test-ConfiguredModelList.cjs <codex> <catalog> <default-model> <comma-separated-models>');
  process.exit(2);
}
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-model-list-'));
const env = {...process.env, CODEX_HOME:root, CODEX_SQLITE_HOME:root};
for (const key of Object.keys(env)) {
  if (/^(OPENAI_API_KEY|OPENAI_API_KEY_FILE|OPENAI_BASE_URL|OPENAI_ACCESS_TOKEN|CHATGPT_ACCESS_TOKEN|CODEX_API_KEY|CODEX_API_KEY_FILE|HTTP_PROXY|HTTPS_PROXY|ALL_PROXY|NO_PROXY)$/i.test(key)) delete env[key];
}
const child = spawn(codex, [
  '-c', `model_catalog_json=${JSON.stringify(catalog)}`,
  '-c', `model=${JSON.stringify(defaultModel)}`,
  '-c', 'cli_auth_credentials_store="file"',
  '-c', 'model_provider="cam_offline_fixture"',
  '-c', 'model_providers.cam_offline_fixture={name="Offline fixture",base_url="http://127.0.0.1:1",wire_api="responses",requires_openai_auth=false}',
  '--disable', 'plugins',
  'app-server'
],
  {windowsHide:true, cwd:root, env, stdio:['pipe','pipe','ignore']});
let buffer='';
const pending = new Map();
const timer=setTimeout(() => { console.error('model/list timed out');child.kill();process.exitCode=1; },15000);
child.once('error',error=>{clearTimeout(timer);for(const task of pending.values())task.reject(error);process.exitCode=1;});
child.once('close',()=>{clearTimeout(timer);for(const task of pending.values())task.reject(new Error('App-server exited before replying'));pending.clear();fs.rmSync(root,{recursive:true,force:true});});
child.stdout.setEncoding('utf8');
child.stdout.on('data',text => {
  buffer+=text;
  while(buffer.includes('\n')) {
    const end=buffer.indexOf('\n'); const line=buffer.slice(0,end);buffer=buffer.slice(end+1);
    let message; try{message=JSON.parse(line);}catch{continue;}
    const task=pending.get(message.id); if(!task)continue;
    pending.delete(message.id);if(message.error)task.reject(new Error('App-server request failed'));else task.resolve(message.result);
  }
});
function request(id,method,params){return new Promise((resolve,reject)=>{pending.set(id,{resolve,reject});child.stdin.write(JSON.stringify({id,method,params})+'\n');});}
(async()=>{
  try {
    await request(1,'initialize',{clientInfo:{name:'cam_catalog_test',version:'2.3.16'}});
    child.stdin.write(JSON.stringify({method:'initialized'})+'\n');
    const models=[];
    let cursor=null;
    const seen=new Set();
    do {
      const result=await request(2+models.length,'model/list',{limit:2,includeHidden:false,...(cursor ? {cursor} : {})});
      models.push(...(result.data||result.models||[]));
      cursor=result.nextCursor;
      if(cursor){assert(!seen.has(cursor),'model/list repeated a pagination cursor');seen.add(cursor);}
      assert(models.length<=1000,'model/list pagination exceeded the fixture bound');
    } while(cursor);
    const ids=models.map(model => model.model||model.id||model.slug).sort();
    assert.deepEqual(ids, expectedModels);
    const selected=models.find(model => (model.model||model.id||model.slug)===defaultModel);
    assert(selected, `Default model ${defaultModel} is missing.`);
    assert.equal(
      models.every(model => (model.hidden ?? false) === false),
      true,
      'At least one returned model is hidden.');
    console.log(`App-server model/list exposes ${ids.length} selectable models including configured ${defaultModel}.`);
  } catch(error){console.error(error.message);process.exitCode=1;}
  finally{clearTimeout(timer);child.stdin.end();child.kill();}
})();
