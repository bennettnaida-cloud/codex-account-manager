'use strict';
const {spawn} = require('node:child_process');
const assert = require('node:assert/strict');
const codex = process.argv[2];
const catalog = process.argv[3];
const defaultModel = process.argv[4];
const expectedModels = (process.argv[5] || '').split(',').filter(Boolean).sort();
if (!codex || !catalog || !defaultModel || expectedModels.length === 0) {
  console.error('Usage: node Test-ConfiguredModelList.cjs <codex> <catalog> <default-model> <comma-separated-models>');
  process.exit(2);
}
const child = spawn(codex, [
  '-c', `model_catalog_json=${JSON.stringify(catalog)}`,
  '-c', `model=${JSON.stringify(defaultModel)}`,
  'app-server'
],
  {windowsHide:true, stdio:['pipe','pipe','ignore']});
let buffer='';
const pending = new Map();
const timer=setTimeout(() => { console.error('model/list timed out');child.kill();process.exitCode=1; },15000);
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
    const result=await request(2,'model/list',{});
    const models=result.data||result.models||[];
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
