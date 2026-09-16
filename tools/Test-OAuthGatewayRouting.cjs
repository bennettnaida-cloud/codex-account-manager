'use strict';
// Exercise the installed app-server with synthetic OAuth credentials only.
const {spawn}=require('node:child_process');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const http=require('node:http');
const assert=require('node:assert/strict');
const [exe,catalog]=process.argv.slice(2);
assert(exe&&catalog,'Expected Codex executable and model catalog paths');
const fixture=fs.mkdtempSync(path.join(os.tmpdir(),'cam-oauth-routing-'));
const children=[];
const servers=[];
let timer;
async function run(withGateway){
 const home=path.join(fixture,withGateway?'routed':'old');fs.mkdirSync(home);
 let seenResolve;const seen=new Promise(r=>seenResolve=r); let directSeen=false;
 const server=http.createServer((req,res)=>{
  const isGateway=req.url.startsWith('/backend-api/');
  if(req.url.includes('responses')) seenResolve({isGateway,url:req.url});
  req.resume();res.writeHead(403,{'content-type':'application/json'});
  res.end('{"error":{"code":"fixture_stop","message":"Synthetic test only"}}');
 });
 servers.push(server);await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const base=`http://127.0.0.1:${server.address().port}`;
 // A rejecting local HTTP proxy prevents the old config from contacting any real service.
 server.on('connect',(req,socket)=>{directSeen=true;if(!withGateway)seenResolve({isGateway:false,url:req.url});socket.end('HTTP/1.1 403 Forbidden\r\n\r\n');});
 const claims={exp:Math.floor(Date.now()/1000)+3600,email:'fixture@example.invalid',
  'https://api.openai.com/auth':{chatgpt_account_id:'fixture-account',chatgpt_plan_type:'plus',chatgpt_user_id:'fixture-user'}};
 const jwt=Buffer.from('{"alg":"none"}').toString('base64url')+'.'+Buffer.from(JSON.stringify(claims)).toString('base64url')+'.fixture';
 fs.writeFileSync(path.join(home,'auth.json'),JSON.stringify({auth_mode:'chatgpt',tokens:{access_token:jwt,id_token:jwt,refresh_token:'fixture-only',account_id:'fixture-account'},last_refresh:new Date().toISOString()}));
 fs.writeFileSync(path.join(home,'config.toml'),[
  'model_provider = "codex_official_https"','model = "gpt-6-astra"',
  'cli_auth_credentials_store = "file"',`model_catalog_json = ${JSON.stringify(catalog)}`,
  ...(withGateway?[`chatgpt_base_url = "${base}/backend-api"`,`openai_base_url = "${base}/backend-api/codex"`]:[]),
  '[model_providers.codex_official_https]','name = "OpenAI"',`base_url = "${base}/backend-api/codex"`,
  'wire_api = "responses"','requires_openai_auth = true','supports_websockets = false',''
 ].join('\n'));
 const env={...process.env,CODEX_HOME:home,HTTP_PROXY:base,HTTPS_PROXY:base,ALL_PROXY:base,NO_PROXY:'127.0.0.1,localhost'};
 for(const name of Object.keys(env))if(/^(OPENAI_API_KEY|CODEX_API_KEY|OPENAI_BASE_URL|CHATGPT_BASE_URL)$/i.test(name))delete env[name];
 const child=spawn(exe,['app-server'],{cwd:fixture,env,windowsHide:true,stdio:['pipe','pipe','ignore']});children.push(child);
 const pending=new Map();let buffer='',id=0;
 child.stdout.on('data',data=>{buffer+=data;while(buffer.includes('\n')){const end=buffer.indexOf('\n');const line=buffer.slice(0,end);buffer=buffer.slice(end+1);let m;try{m=JSON.parse(line);}catch{continue;}const p=pending.get(m.id);if(p){pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}}});
 const rpc=(method,params)=>new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});child.stdin.write(JSON.stringify({id:n,method,params})+'\n');});
 await rpc('initialize',{clientInfo:{name:'cam_routing_fixture',version:'1'},capabilities:{experimentalApi:true}});
 const created=await rpc('thread/start',{model:'gpt-6-astra',modelProvider:'openai',ephemeral:true,cwd:fixture,approvalPolicy:'never',sandbox:'read-only',baseInstructions:'Routing fixture. Do not use tools.'});
 await rpc('turn/start',{threadId:created.thread.id,input:[{type:'text',text:'Reply OK.'}]});
 const result=await Promise.race([seen,new Promise((_,reject)=>setTimeout(()=>reject(new Error(`No model request reached gateway; direct connections observed=${directSeen}`)),10000))]);
 assert.equal(result.isGateway,withGateway,`Unexpected routing: ${result.url}`);
 console.log(`${withGateway?'Fixed':'Old'} OAuth config: ${withGateway?'gateway request observed':'direct request reproduced and blocked locally'}`);
 child.kill();await new Promise(r=>child.once('exit',r));
 server.closeAllConnections();await new Promise(r=>server.close(r));
}
(async()=>{timer=setTimeout(()=>{console.error('Routing fixture timed out');for(const c of children)c.kill();for(const s of servers){s.closeAllConnections();s.close();}process.exitCode=1;},30000);
try{await run(false);await run(true);}catch(e){console.error(e.message);process.exitCode=1;}
finally{clearTimeout(timer);for(const c of children){if(c.exitCode===null&&c.signalCode===null){const exited=new Promise(r=>c.once('exit',r));c.kill();await exited;}}for(const s of servers){s.closeAllConnections();s.close();}try{fs.rmSync(fixture,{recursive:true,force:true,maxRetries:10,retryDelay:100});}catch{console.error('Fixture cleanup pending: '+fixture);}}})();
