'use strict';
// Native CLI contract test. All identities are synthetic and all HTTP stays on loopback.
const {spawn} = require('node:child_process');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const {once} = require('node:events');
const codex = process.argv[2] && path.resolve(process.argv[2]);
if (!codex) throw Error('Usage: node Test-WebSessionAuth.cjs <codex-exe>');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cam-session-cli-'));
const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
const token = `${encode({alg:'RS256'})}.${encode({exp:Math.floor(Date.now()/1000)+3600,email:'session@example.invalid','https://api.openai.com/auth':{chatgpt_account_id:'fixture-workspace',chatgpt_plan_type:'plus'}})}.fixture-signature`;
const auth = JSON.stringify({auth_mode:'apikey',OPENAI_API_KEY:token,web_session:{account_id:'fixture-workspace',expires_at:new Date(Date.now()+3600000).toISOString()}});
fs.writeFileSync(path.join(root,'auth.json'),auth);
let quotaRequests=0;
let requestError;
const server=http.createServer((req,res)=>{
  try {
    assert.equal(req.headers.authorization,`Bearer ${token}`);
    assert.equal(req.headers['chatgpt-account-id'],'fixture-workspace');
    if(req.url.includes('/wham/usage')) {
      quotaRequests++;
      res.setHeader('Content-Type','application/json');
      res.end(JSON.stringify({plan_type:'plus',rate_limit:{allowed:true,limit_reached:false,primary_window:{used_percent:42,limit_window_seconds:18000,reset_after_seconds:3600,reset_at:Math.floor(Date.now()/1000)+3600}}}));
    } else { res.statusCode=404;res.end('{}'); }
  } catch(error) {requestError=error;res.statusCode=500;res.end('{}');}
});
(async()=>{
  let child;
  let timer;
  const pending=new Map();
  try {
    server.listen(0,'127.0.0.1');await once(server,'listening');
    const base=`http://127.0.0.1:${server.address().port}`;
    fs.writeFileSync(path.join(root,'config.toml'),`cli_auth_credentials_store = "file"\nchatgpt_base_url = "${base}/backend-api"\nmodel_provider = "fixture"\n[model_providers.fixture]\nname = "fixture"\nbase_url = "${base}/backend-api/codex"\nwire_api = "responses"\nrequires_openai_auth = true\n`);
    const env={...process.env,CODEX_HOME:root,CODEX_SQLITE_HOME:root};
    for(const key of Object.keys(env)) if(/^(OPENAI_|CHATGPT_ACCESS_TOKEN|CODEX_(API_KEY|ACCESS_TOKEN)|HTTP_PROXY|HTTPS_PROXY|ALL_PROXY|NO_PROXY)/i.test(key))delete env[key];
    child=spawn(codex,['app-server','--stdio','--disable','plugins'],{windowsHide:true,cwd:root,env,stdio:['pipe','pipe','ignore']});
    let buffer='';let id=0;
    function request(method,params){return new Promise((resolve,reject)=>{pending.set(++id,{resolve,reject});child.stdin.write(JSON.stringify({id,method,params})+'\n');});}
    child.stdout.setEncoding('utf8');
    child.stdout.on('data',text=>{buffer+=text;while(buffer.includes('\n')){const end=buffer.indexOf('\n');const line=buffer.slice(0,end);buffer=buffer.slice(end+1);let m;try{m=JSON.parse(line);}catch{continue;}const p=pending.get(m.id);if(!p)continue;pending.delete(m.id);if(m.error)p.reject(Error('Native Session RPC failed: '+m.error.code+' '+m.error.message));else p.resolve(m.result);}});
    child.once('error',error=>{for(const p of pending.values())p.reject(error);});
    child.once('exit',()=>{for(const p of pending.values())p.reject(Error('Native Session CLI exited'));pending.clear();});
    timer=setTimeout(()=>{for(const p of pending.values())p.reject(Error('Native Session test timed out'));child.kill();},20000);
    await request('initialize',{clientInfo:{name:'cam_session_fixture',version:'2.3.42'},capabilities:{experimentalApi:true}});
    child.stdin.write(JSON.stringify({method:'initialized'})+'\n');
    const before=await request('account/read',{refreshToken:false});
    assert.equal(before.account.type,'apiKey','stored Session uses gateway API bearer transport');
    await request('account/login/start',{type:'chatgptAuthTokens',accessToken:token,chatgptAccountId:'fixture-workspace'});
    const limits=await request('account/rateLimits/read',null);
    assert(quotaRequests>0,'quota read did not reach loopback');
    assert.equal(limits.rateLimits.primary.usedPercent,42);
    assert.equal(fs.readFileSync(path.join(root,'auth.json'),'utf8'),auth,'temporary quota auth rewrote stored Session');
    if(requestError)throw requestError;
    console.log('PASS: native Session bearer storage, in-memory quota authentication, workspace headers and credential preservation');
  } finally {
    clearTimeout(timer);
    if(child && child.exitCode===null){child.stdin.end();child.kill();await once(child,'exit');}
    server.closeAllConnections();await new Promise(resolve=>server.close(resolve));
    fs.rmSync(root,{recursive:true,force:true});
  }
})().catch(error=>{console.error(error.message);process.exitCode=1;});
