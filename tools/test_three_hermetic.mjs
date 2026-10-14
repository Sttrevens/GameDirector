// Hermetic takes: browser reuse or skipped terminal cleanup must fail real process/profile checks.
// The only replaced boundary is Playwright. Core, bundler, HTTP queue and server are real.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {spawn,execFileSync} from 'node:child_process';
import {once} from 'node:events';
import {tempDirectory} from './test_temp.mjs';
const repo=path.resolve('.'),cli=path.join(repo,'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll');
const contract=JSON.parse(execFileSync('dotnet',[cli,'contract','--json'],{encoding:'utf8'})),R=contract.bridge.routes,S=contract.take.states,E=contract.bridge.errors;
const realDotnet=execFileSync(process.platform==='win32'?'where':'which',['dotnet'],{encoding:'utf8'}).trim().split(/\r?\n/)[0];
const route=n=>'/'+R[n],alive=pid=>{try{process.kill(pid,0);return true;}catch{return false;}};
const pause=()=>new Promise(r=>setTimeout(r,20));
// CI must fail, never hang: every response wait and child exit gets a deadline.
const bounded=(promise,ms,message)=>new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error(message)),ms);promise.then(v=>{clearTimeout(timer);resolve(v);},e=>{clearTimeout(timer);reject(e);});});
async function until(fn,message){const limit=Date.now()+15000;while(Date.now()<limit){const v=await fn();if(v)return v;await pause();}assert.fail(message);}
// Negative assertions hold for a bounded window instead of racing one event loop tick.
async function hold(ms,check){const limit=Date.now()+ms;while(Date.now()<limit){check();await pause();}}
function fixture({legacy=false,gl,settings:initialSettings={},budgets,measureDefault}={}){
 const temp=tempDirectory('gd-e8-hermetic-'),root=temp.root,control=path.join(root,'control.json'),trace=path.join(root,'trace.jsonl'),profiles=path.join(root,'profiles'),modules=path.join(root,'modules'),clock=path.join(root,'clock.mjs'),tmp=path.join(root,'tmp');
 fs.mkdirSync(path.join(modules,'node_modules/playwright'),{recursive:true});fs.mkdirSync(profiles);fs.mkdirSync(tmp);fs.writeFileSync(path.join(modules,'package.json'),'{}');
 fs.writeFileSync(path.join(modules,'node_modules/playwright/package.json'),JSON.stringify({name:'playwright',version:'process-fixture'}));
 fs.writeFileSync(path.join(modules,'node_modules/playwright/index.js'),'module.exports=require('+JSON.stringify(path.join(repo,'tools/fixtures/three-process-browser.cjs'))+');');
 // Atomic control writes: the server's injected clock parses this file on every
 // Date.now (including inside the lease interval); a torn read would crash it.
 let settings={version:'test-browser-1',offset:0,...initialSettings};const configure=changes=>{settings={...settings,...changes};const tmp=control+'.tmp';fs.writeFileSync(tmp,JSON.stringify(settings));fs.renameSync(tmp,control);};configure({});
 // Test-only virtual wall clock; lease still reads the REAL exported contract and polls normally.
 fs.writeFileSync(clock,"import fs from 'node:fs';const now=Date.now;Date.now=()=>now()+JSON.parse(fs.readFileSync(process.env.GD_TEST_BROWSER_CONTROL,'utf8')).offset;");
 const server=process.env.GD_E8_SERVER===undefined?path.join(repo,'packages/gamedirector-three/server.mjs'):process.env.GD_E8_SERVER;
 let adapter='tools/fixtures/three-ts-adapter/stage';
 if(legacy){adapter=path.join(root,'legacy-adapter');fs.mkdirSync(adapter);fs.copyFileSync(path.join(repo,'tools/fixtures/three-ts-adapter/stage/manifest.json'),path.join(adapter,'manifest.json'));} // no three.json: exercises the live legacy fingerprint path
 else if(gl!==undefined){const family=path.join(root,'family');fs.cpSync(path.join(repo,'tools/fixtures/three-ts-adapter'),family,{recursive:true});adapter=path.join(family,'stage');const file=path.join(adapter,'three.json'),config=JSON.parse(fs.readFileSync(file));config.gl=gl;fs.writeFileSync(file,JSON.stringify(config));}
 // Test-only contract export proxy shortens real wait budgets; compilation/Core
 // admission are unchanged. No production timeout seam or source mutation.
 const proxy=budgets||measureDefault!==undefined;
 if(proxy)fs.writeFileSync(path.join(root,'dotnet'),`#!/usr/bin/env node
const {spawnSync}=require('node:child_process'),args=process.argv.slice(2),r=spawnSync(${JSON.stringify(realDotnet)},args,{encoding:'utf8',maxBuffer:8*1024*1024});if(r.error)throw r.error;let stdout=r.stdout;if(args[1]==='contract'&&args.includes('--json')&&r.status===0){const c=JSON.parse(stdout);if(${JSON.stringify(measureDefault)}!==undefined)c.take.requestDefaults.measure=${JSON.stringify(measureDefault)};Object.assign(c.bridge.budgets,${JSON.stringify(budgets)});c.bridge.budgets.takeStartTimeoutSeconds=c.bridge.budgets.compileSeconds+c.bridge.budgets.pageLoadSeconds+c.bridge.budgets.stageReadySeconds+c.bridge.budgets.startSlackSeconds;stdout=JSON.stringify(c);}process.stdout.write(stdout);process.stderr.write(r.stderr);process.exit(r.status);
`,{mode:0o755});
 const child=spawn(process.execPath,['--import',clock,server,'--game-root','tools/fixtures/three-ts-game','--adapter',adapter,'--three-root','packages/gamedirector-three/node_modules/three','--cli',cli,'--port','0','--browser','chrome'],{env:{...process.env,...(proxy?{PATH:root+path.delimiter+process.env.PATH}:{}),TMPDIR:tmp,GAMEDIRECTOR_BROWSER:'none',GAMEDIRECTOR_NODE_MODULES:modules,GD_TEST_BROWSER_CONTROL:control,GD_TEST_BROWSER_TRACE:trace,GD_TEST_BROWSER_PROFILES:profiles}});
 // Only newline-terminated trace lines parse; a line mid-write is not torn JSON.
 const f={...temp,child,configure,trace,profiles,tmp,stdout:'',stderr:'',events:()=>{if(!fs.existsSync(trace))return[];const text=fs.readFileSync(trace,'utf8');return text.slice(0,text.lastIndexOf('\n')+1).split('\n').filter(Boolean).map(JSON.parse);}};
 child.stdout.on('data',s=>f.stdout+=s);child.stderr.on('data',s=>f.stderr+=s);return f;
}
async function ready(f){await until(()=>{if(f.child.exitCode!==null)assert.fail('server exited: '+f.stderr);return f.stdout.includes('"endpoint"');},'startup timeout: '+f.stderr);f.url=JSON.parse(f.stdout.trim().split('\n').at(-1)).endpoint;}
async function post(f,name,data){return fetch(f.url+route(name),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data),signal:AbortSignal.timeout(15000)});}
async function get(f,name){return (await fetch(f.url+route(name),{signal:AbortSignal.timeout(15000)})).json();}
const timeline={version:'0.1',id:'hermetic',cues:[{t:0,type:'camera.shot',shot:{type:'lockoff',subject:'hero',frame:'full',from:'front',durationSeconds:2}}]};
const data={timeline,frameRate:1,width:64,height:64};
async function start(f){const r=await post(f,'takeStart',data);assert.equal(r.status,200,await r.clone().text());const take=await r.json();
 const line=[...f.stderr.matchAll(/take (\w+) browser pid=(\d+) version=(\S+)/g)].find(m=>m[1]===take.takeId);assert.ok(line,'missing actual per-take browser PID log: '+f.stderr);const pid=Number(line[2]);assert.equal(line[3],'test-browser-1');assert.ok(alive(pid),'take process already gone');return {...take,pid};}
async function status(f){return (await get(f,'take')).take;}
function closed(f,pid,{end=true}={}){
 assert.equal(alive(pid),false,'terminal browser still alive: '+pid);assert.equal(fs.existsSync(path.join(f.profiles,String(pid))),false,'terminal profile survived');
 const events=f.events(),close=events.findIndex(e=>e.event==='closed'&&e.pid===pid);assert.ok(close>=0,'browser.close never completed');
 const finish=events.findIndex(e=>e.event==='end'&&e.pid===pid);
 if(end)assert.ok(finish>=0&&finish<close,'gd.end must precede browser.close');else assert.equal(finish,-1,'a discarded browser must not get gd.end');
}
async function cleanup(f){
 try{
  // Snapshot live browser PIDs after the server exits, then kill, then fail: a
  // SIGTERM shutdown that orphans a process must turn the suite red, not clean up silently.
  let leaked=null;
  if(f.child.exitCode===null&&f.child.signalCode===null){const exit=once(f.child,'exit');f.child.kill('SIGTERM');try{await bounded(exit,10000,'server ignored SIGTERM');}catch{f.child.kill('SIGKILL');await bounded(exit,10000,'server ignored SIGKILL');}leaked=f.events().filter(e=>e.event==='launch'&&alive(e.pid)).map(e=>e.pid);}
  for(const e of f.events())if(e.event==='launch'&&alive(e.pid)){process.kill(e.pid,'SIGTERM');await until(()=>!alive(e.pid),'test child cleanup');}
  if(leaked)assert.deepEqual(leaked,[],'server exited via SIGTERM leaving live browser processes: '+leaked);
 }finally{f.cleanup();}
}

test('startup probe closes; consecutive completed/cancelled takes own distinct non-overlapping processes',async()=>{
 const f=fixture();try{await ready(f);const probe=f.events().find(e=>e.event==='launch');closed(f,probe.pid,{end:false});
  const a=await start(f);for(let i=0;i<a.frameCount;i++){const r=await post(f,'takeFrame',{takeId:a.takeId,frameIndex:i});assert.equal(r.status,200);const bytes=Buffer.from(await r.arrayBuffer());if(i===a.frameCount-1){closed(f,a.pid);const retry=await post(f,'takeFrame',{takeId:a.takeId,frameIndex:i});assert.equal(retry.status,200);assert.deepEqual(Buffer.from(await retry.arrayBuffer()),bytes,'last PNG retry must survive browser closure');}}
  assert.equal((await status(f)).state,S.completed);const b=await start(f);assert.notEqual(b.pid,a.pid,'browser reused across takes');closed(f,a.pid);
  const events=f.events(),firstClose=events.findIndex(e=>e.event==='closed'&&e.pid===a.pid),nextLaunch=events.findIndex(e=>e.event==='launch'&&e.pid===b.pid);assert.ok(firstClose<nextLaunch,'first browser overlaps second');
  assert.equal((await post(f,'takeStop',{takeId:b.takeId})).status,200);closed(f,b.pid);assert.equal((await status(f)).state,S.cancelled);
  // stop is idempotent after completion/cancellation; it must not evaluate a closed page.
  assert.equal((await post(f,'takeStop',{takeId:b.takeId})).status,200);
  const launches=f.events().filter(e=>e.event==='launch');for(const e of launches)assert.equal(alive(e.pid),false);
  for(const e of f.events().filter(e=>e.event==='page-options'))assert.deepEqual(e.options,{deviceScaleFactor:1,hasTouch:false,isMobile:false,reducedMotion:'no-preference'});
  for(const name of ['pageerror','console'])assert.ok(f.events().some(e=>e.event==='handler'&&e.name===name),'page handler missing');
  // The bridge owns signal-driven shutdown; Playwright's handlers would race the drain.
  for(const e of launches){assert.equal(e.options.handleSIGINT,false,'Playwright must not own SIGINT');assert.equal(e.options.handleSIGTERM,false,'Playwright must not own SIGTERM');assert.equal(e.options.handleSIGHUP,false,'Playwright must not own SIGHUP');}
  assert.deepEqual(launches[0].options.args,launches[1].options.args);assert.equal(launches[1].options.channel,'chrome');
 }finally{await cleanup(f);}
});

// Real server positive path: the browser is the only doubled boundary.
for(const [name,options,flags,want] of [
 ['explicit on/off',{},[true,false],[0]],['omitted default false',{},[undefined,undefined],[]],
 ['omitted default true',{measureDefault:true},[undefined,undefined],[0,1]],['explicit off overrides true',{measureDefault:true},[false,false],[]],
])test('measure collection, status and requestDefaults '+name,async()=>{
 const f=fixture(options);try{await ready(f);const a=await start(f);
  for(let i=0;i<a.frameCount;i++){const body={takeId:a.takeId,frameIndex:i};if(flags[i]!==undefined)body.measure=flags[i];assert.equal((await post(f,'takeFrame',body)).status,200);}
  closed(f,a.pid);const take=await get(f,'take'),status=await get(f,'status');
  const expected=want.map(frame=>({frame,time:frame,camera:{position:[frame,1,3],right:[1,0,0],up:[0,1,0],forward:[0,0,-1],fov:50,aspect:1},composition:{active:false}}));
  assert.deepEqual(take.measures,expected);assert.deepEqual(take.take.measures,expected);assert.deepEqual(status.measures,expected);assert.deepEqual(status.take.measures,expected);
  assert.deepEqual(take.reviews,[0,1].map(frame=>({frame,time:frame,composition:{active:false,note:'review-only'}})));assert.deepEqual(status.reviews,take.reviews);
  assert.ok(take.reviews.every(r=>!Object.hasOwn(r,'camera')),'measurement leaked into reviews');
 }finally{await cleanup(f);}
});

// GL backend breaks caught: omitted launch args, rejected-value fallthrough, unverified
// startup selection, and a take silently moving off the identity-pinned renderer.
for(const [name,options,args,renderer] of [
 ['omitted',{},['--disable-background-timer-throttling'],'ANGLE test GPU'],
 ['legacy',{legacy:true},['--disable-background-timer-throttling'],'ANGLE test GPU'],
 ['explicit default',{gl:'default'},['--disable-background-timer-throttling'],'ANGLE test GPU'],
 ['swiftshader',{gl:'swiftshader'},['--disable-background-timer-throttling','--use-angle=swiftshader','--enable-unsafe-swiftshader'],'ANGLE SwiftShader test'],
])test('declared GL '+name+' reaches the probe and every fresh take',async()=>{
 const f=fixture(options);try{await ready(f);
  for(let i=0;i<2;i++){const a=await start(f);assert.equal((await post(f,'takeFrame',{takeId:a.takeId,frameIndex:0})).status,200);assert.equal((await post(f,'takeStop',{takeId:a.takeId})).status,200);closed(f,a.pid);}
  const launches=f.events().filter(e=>e.event==='launch');assert.equal(launches.length,3);for(const e of launches)assert.deepEqual(e.options.args,args,'declared backend flags omitted from launch');
  const reads=f.events().filter(e=>e.event==='renderer');assert.equal(reads.length,3,'probe and both takes must read a renderer');assert.deepEqual(reads.map(e=>e.stage),[false,true,true]);for(const e of reads)assert.equal(e.renderer,renderer);
  assert.ok(f.stderr.includes('startup gl='+renderer),f.stderr);
  for(const id of [...f.stderr.matchAll(/take (\w+) browser pid=/g)].map(m=>m[1]))assert.ok(f.stderr.split('\n').some(line=>line==='[GameDirector] take '+id+' gl='+renderer),f.stderr);
  assert.ok(f.stderr.split('\n').some(line=>line.includes('presenter post=')&&line.endsWith('gl='+renderer)),f.stderr);
 }finally{await cleanup(f);}
});

for(const gl of ['webgpu','SwiftShader',null,42,'__proto__'])test('unknown GL '+JSON.stringify(gl)+' refuses before launch/listen',async()=>{
 const f=fixture({gl});try{const [code]=await bounded(once(f.child,'exit'),15000,'GL refusal timed out');assert.notEqual(code,0);assert.match(f.stderr,/unknown three.json gl.*default.*swiftshader/);assert.equal(f.stdout,'');assert.equal(f.events().filter(e=>e.event==='launch').length,0);}finally{await cleanup(f);}
});

test('declared SwiftShader expectation mismatch refuses startup and closes its probe',async()=>{
 const f=fixture({gl:'swiftshader',settings:{renderer:'ANGLE unexpected GPU'}});try{const [code]=await bounded(once(f.child,'exit'),15000,'GL expectation refusal timed out');assert.notEqual(code,0);assert.match(f.stderr,/declared GL backend swiftshader.*ANGLE unexpected GPU/);assert.equal(f.stdout,'');const probe=f.events().find(e=>e.event==='launch');assert.ok(probe,'expectation must inspect a real probe');closed(f,probe.pid,{end:false});}finally{await cleanup(f);}
});

for(const legacy of [false,true])test('per-take GL renderer skew refuses internal/restart and closes '+(legacy?'legacy':'bundled')+' browser',async()=>{
 const f=fixture({legacy});try{await ready(f);const pinned=(await get(f,'manifest')).capabilities['presentation.sourceFingerprint'];f.configure({renderer:'ANGLE changed GPU'});
  const before=f.events().length,r=await post(f,'takeStart',data),answer=await r.json();assert.equal(r.status,E.internal.status);assert.equal(answer.code,E.internal.code);assert.match(answer.error,/ANGLE changed GPU.*ANGLE test GPU.*restart the bridge/);
  const events=f.events().slice(before),launch=events.find(e=>e.event==='launch');assert.ok(launch);assert.ok(events.some(e=>e.event==='begin'),'must measure the loaded stage renderer');closed(f,launch.pid,{end:false});assert.equal(await status(f),null,'renderer-skewed start published a take');
  assert.equal((await get(f,'manifest')).capabilities['presentation.sourceFingerprint'],pinned,'renderer refusal changed identity');
  f.configure({renderer:null});const a=await start(f);assert.equal((await post(f,'takeStop',{takeId:a.takeId})).status,200);closed(f,a.pid);
 }finally{await cleanup(f);}
});

test('bundled startup renderer changes identity but not the masked manifest',async()=>{
 const manifests=[];for(const renderer of ['ANGLE test GPU','ANGLE different GPU']){const f=fixture({settings:{renderer}});try{await ready(f);manifests.push(await get(f,'manifest'));}finally{await cleanup(f);}}
 const key='presentation.sourceFingerprint';assert.notEqual(manifests[0].capabilities[key],manifests[1].capabilities[key],'startup renderer excluded from bundled fingerprint');for(const m of manifests)m.capabilities[key]='X';assert.deepEqual(manifests[0],manifests[1]);
});

test('lease cleanup is serialized: a queued take/start cannot launch before the lease closes its browser',async()=>{
 const f=fixture();try{await ready(f);const a=await start(f);f.configure({offset:(contract.capture.idleLeaseSeconds+1)*1000,block:'end'});
  await until(()=>f.events().some(e=>e.event==='blocked'&&e.at==='end'&&e.pid===a.pid),'lease cleanup never reached gd.end');
  assert.equal((await status(f)).state,S.cancelled,'lease did not cancel before disposal');
  const attempts=f.events().filter(e=>e.event==='launch-attempt').length,request=post(f,'takeStart',data);request.catch(()=>{});
  await until(()=>f.stderr.includes('request queued behind active work'),'queued take/start never reached the server: '+f.stderr);
  // Hold until a's browser closes (the 2 s disposal bound ends the block on its own):
  // no launch and no second disposal of a may appear before its 'closed' trace.
  await until(()=>{assert.equal(f.events().filter(e=>e.event==='launch-attempt').length,attempts,'queued take launched before lease cleanup finished');assert.equal(f.events().filter(e=>e.event==='end'&&e.pid===a.pid).length,1,'queued take raced the lease disposal');return f.events().some(e=>e.event==='closed'&&e.pid===a.pid);},'lease browser never closed');
  f.configure({block:null,offset:0});
  const r=await bounded(request,15000,'queued take/start never answered');assert.equal(r.status,200,await r.clone().text());const b=await r.json();
  const line=[...f.stderr.matchAll(/take (\w+) browser pid=(\d+)/g)].find(m=>m[1]===b.takeId);assert.ok(line,'missing take browser log: '+f.stderr);const bPid=Number(line[2]);assert.notEqual(bPid,a.pid);
  const events=f.events(),aClose=events.findIndex(e=>e.event==='closed'&&e.pid===a.pid),bLaunch=events.findIndex(e=>e.event==='launch'&&e.pid===bPid);assert.ok(aClose>=0&&bLaunch>aClose,'lease browser must close before the queued launch');
  assert.equal((await post(f,'takeFrame',{takeId:b.takeId,frameIndex:0})).status,200,'queued take never captured a frame');
  assert.equal((await post(f,'takeStop',{takeId:b.takeId})).status,200);closed(f,bPid);
 }finally{f.configure({block:null,offset:0});await cleanup(f);}
});

for(const phase of ['frame','finish','end'])test('failure at '+phase+' always closes browser even when disposal throws',async()=>{
 const f=fixture();try{await ready(f);const a=await start(f);f.configure({fault:phase});
  let r;if(phase==='end')r=await post(f,'takeStop',{takeId:a.takeId});else{for(let i=0;i<a.frameCount;i++){r=await post(f,'takeFrame',{takeId:a.takeId,frameIndex:i});if(r.status!==200)break;}}
  assert.equal(r.status,E.internal.status);assert.equal((await r.json()).code,E.internal.code);closed(f,a.pid);assert.equal((await status(f)).state,phase==='end'?S.cancelled:S.failed);
  f.configure({fault:null});const b=await start(f);assert.notEqual(b.pid,a.pid);assert.equal((await post(f,'takeStop',{takeId:b.takeId})).status,200);closed(f,b.pid);
 }finally{await cleanup(f);}
});

for(const phase of ['launch','cdp','page','goto','ready','begin','renderer-read'])test('partial take/start failure at '+phase+' releases every acquired process/profile',async()=>{
 const f=fixture();try{await ready(f);const before=f.events().filter(e=>e.event==='launch').length;f.configure({fault:phase});const r=await post(f,'takeStart',data);assert.equal(r.status,E.internal.status);assert.equal((await r.json()).code,E.internal.code);
  assert.equal((await status(f)),null,'failed start publishes no capturing take');const spawned=f.events().filter(e=>e.event==='launch').slice(before);assert.equal(spawned.length,phase==='launch'?0:1);for(const e of spawned){closed(f,e.pid,{end:false});if(['page','goto','ready','begin','renderer-read'].includes(phase))assert.match(f.stderr,new RegExp('take \\w+ browser pid='+e.pid+' version=test-browser-1'),'failed start lost its PID audit witness');}
  f.configure({fault:null});const b=await start(f);assert.equal((await post(f,'takeStop',{takeId:b.takeId})).status,200);closed(f,b.pid);
 }finally{await cleanup(f);}
});

// H1/H2: dropping the race, resetting its deadline, or skipping failed-start
// closure must fail real HTTP/error/PID/profile assertions before fixture cleanup.
const shortBudgets={pageLoadSeconds:1,stageReadySeconds:1};
for(const at of ['begin','renderer-read'])test('blocked '+at+' shares the readiness deadline, closes, and permits the next start',async()=>{
 const f=fixture({budgets:shortBudgets});try{await ready(f);f.configure({delay:{ready:800},block:at});
  const request=post(f,'takeStart',data);request.catch(()=>{});const blocked=await until(()=>f.events().find(e=>e.event==='blocked'&&e.at===at),'take never reached blocked '+at);
  // Ready already consumed 800ms of this one-second budget. A fresh one-second
  // read bound (or unbounded evaluate) cannot satisfy this 500ms response bound.
  const r=await bounded(request,500,'blocked '+at+' exceeded the shared readiness deadline');assert.equal(r.status,E.internal.status);const answer=await r.json();assert.equal(answer.code,E.internal.code);assert.match(answer.error,/did not settle/);
  closed(f,blocked.pid,{end:false});assert.equal(await status(f),null,'blocked start published a take');assert.match(f.stderr,new RegExp('browser pid='+blocked.pid+' version=test-browser-1'));
  f.configure({delay:null,block:null});const a=await start(f);assert.equal((await post(f,'takeStop',{takeId:a.takeId})).status,200);closed(f,a.pid);
 }finally{f.configure({delay:null,block:null});await cleanup(f);}
});

for(const at of ['page','renderer-read'])for(const mode of ['fault','block'])test('startup probe '+at+' '+mode+' exits bounded with PID/profile gone',async()=>{
 const f=fixture({budgets:shortBudgets,settings:{[mode]:at}});try{
  const [code]=await bounded(once(f.child,'exit'),3000,'startup probe '+at+' '+mode+' exceeded load budget');assert.notEqual(code,0);assert.equal(f.stdout,'','failed probe opened listener');
  assert.match(f.stderr,mode==='fault'?new RegExp('injected '+at+' failure'):/did not settle/);const probe=f.events().find(e=>e.event==='launch');assert.ok(probe);closed(f,probe.pid,{end:false});
 }finally{f.configure({fault:null,block:null});await cleanup(f);}
});

test('startup launch and renderer read share one load deadline',async()=>{
 const f=fixture({budgets:shortBudgets,settings:{delay:{launch:800},block:'renderer-read'}});try{
  const exit=once(f.child,'exit');await until(()=>f.events().some(e=>e.event==='blocked'&&e.at==='renderer-read'),'probe never reached renderer read');
  const [code]=await bounded(exit,500,'startup renderer read reset the launch/load deadline');assert.notEqual(code,0);assert.match(f.stderr,/did not settle/);assert.equal(f.stdout,'');closed(f,f.events().find(e=>e.event==='launch').pid,{end:false});
 }finally{f.configure({delay:null,block:null});await cleanup(f);}
});

test('per-take browser version skew refuses internal/restart and closes before stage navigation',async()=>{
 const f=fixture();try{await ready(f);const fingerprint=(await get(f,'manifest')).capabilities['presentation.sourceFingerprint'];f.configure({version:'test-browser-2'});
  const before=f.events().length,r=await post(f,'takeStart',data),answer=await r.json();assert.equal(r.status,E.internal.status);assert.equal(answer.code,E.internal.code);assert.match(answer.error,/restart the bridge/);
  const events=f.events().slice(before);assert.equal(events.filter(e=>e.event==='launch').length,1);closed(f,events.find(e=>e.event==='launch').pid,{end:false});assert.ok(!events.some(e=>e.event==='goto'));
  assert.equal((await get(f,'manifest')).capabilities['presentation.sourceFingerprint'],fingerprint,'startup fingerprint must not drift to rejected version');
 }finally{await cleanup(f);}
});

test('legacy (non-three.json) fingerprint stays pinned to the startup browser version',async()=>{
 const f=fixture({legacy:true});try{await ready(f);const fp=async()=>(await get(f,'manifest')).capabilities['presentation.sourceFingerprint'],pinned=await fp();
  f.configure({version:'test-browser-2'});
  const before=f.events().length,r=await post(f,'takeStart',data),answer=await r.json();assert.equal(r.status,E.internal.status);assert.equal(answer.code,E.internal.code);assert.match(answer.error,/restart the bridge/);
  const events=f.events().slice(before);assert.equal(events.filter(e=>e.event==='launch').length,1);closed(f,events.find(e=>e.event==='launch').pid,{end:false});assert.ok(!events.some(e=>e.event==='goto'));
  assert.equal(await fp(),pinned,'a skewed launch moved the legacy fingerprint');
  f.configure({version:'test-browser-1'});const a=await start(f);
  f.configure({version:'test-browser-2'});assert.equal(await fp(),pinned,'a live take browser moved the legacy fingerprint');
  f.configure({version:'test-browser-1'});assert.equal((await post(f,'takeStop',{takeId:a.takeId})).status,200);closed(f,a.pid);
  assert.equal(await fp(),pinned,'a finished take moved the legacy fingerprint');
 }finally{f.configure({version:'test-browser-1'});await cleanup(f);}
});

test('a gd.end that never settles cannot gate browser close or the take/stop answer',async()=>{
 const f=fixture();try{await ready(f);const a=await start(f);f.configure({block:'end'});
  const r=await bounded(post(f,'takeStop',{takeId:a.takeId}),10000,'take/stop wedged behind a blocked gd.end');
  assert.equal(r.status,E.internal.status);const answer=await r.json();assert.equal(answer.code,E.internal.code);assert.match(answer.error,/did not settle/);
  closed(f,a.pid);assert.equal((await status(f)).state,S.cancelled);
  f.configure({block:null});const b=await start(f);assert.notEqual(b.pid,a.pid);assert.equal((await post(f,'takeStop',{takeId:b.takeId})).status,200);closed(f,b.pid);
 }finally{f.configure({block:null});await cleanup(f);}
});

test('a stage that never becomes ready while its renderer is blocked still answers take/start',async()=>{
 const f=fixture();try{await ready(f);f.configure({wedge:'ready'});
  const r=await bounded(post(f,'takeStart',data),10000,'take/start wedged behind a blocked renderer');
  assert.equal(r.status,E.internal.status);assert.equal((await r.json()).code,E.internal.code);
  closed(f,f.events().filter(e=>e.event==='launch').at(-1).pid,{end:false});assert.equal((await status(f)),null,'failed start publishes no capturing take');
  f.configure({wedge:null});const b=await start(f);assert.equal((await post(f,'takeStop',{takeId:b.takeId})).status,200);closed(f,b.pid);
 }finally{f.configure({wedge:null});await cleanup(f);}
});

for(const [name,settings] of [['end failure',{fault:'end'}],['close failure',{fault:'close'}],['blocked end',{block:'end'}]])
test('completion keeps its final PNG and Completed state when disposal fails: '+name,async()=>{
 const f=fixture();try{await ready(f);const a=await start(f);f.configure(settings);let r;
  for(let i=0;i<a.frameCount;i++){r=await bounded(post(f,'takeFrame',{takeId:a.takeId,frameIndex:i}),15000,'completion wedged behind disposal');assert.equal(r.status,200,'captured frame must reply 200: '+await r.clone().text());}
  assert.ok((await r.arrayBuffer()).byteLength>0,'final PNG lost');
  assert.equal((await status(f)).state,S.completed,'disposal failure must not fail a captured take');
  if(settings.fault==='close'){assert.ok(alive(a.pid),'close rejection must retain ownership');assert.ok(!f.events().some(e=>e.event==='closed'&&e.pid===a.pid),'rejected close must not trace closed');f.configure({fault:null});assert.equal((await post(f,'takeStop',{takeId:a.takeId})).status,200);}
  else f.configure({fault:null,block:null});
  closed(f,a.pid);const b=await start(f);assert.notEqual(b.pid,a.pid);assert.equal((await post(f,'takeStop',{takeId:b.takeId})).status,200);closed(f,b.pid);
 }finally{f.configure({fault:null,block:null});await cleanup(f);}
});

test('a rejected browser.close cannot discard ownership or launch a second process',async()=>{
 const f=fixture();try{await ready(f);const a=await start(f);f.configure({fault:'close'});
  assert.equal((await post(f,'takeStop',{takeId:a.takeId})).status,E.internal.status);assert.ok(alive(a.pid),'injected SDK failure must keep its process live');
  const count=f.events().filter(e=>e.event==='launch').length,r=await post(f,'takeStart',data);assert.equal(r.status,E.internal.status,'must refuse rather than overlap an unclosed browser');
  assert.equal(f.events().filter(e=>e.event==='launch').length,count,'lost browser ownership allowed a second process');
  f.configure({fault:null});assert.equal((await post(f,'takeStop',{takeId:a.takeId})).status,200);closed(f,a.pid);
 }finally{f.configure({fault:null});await cleanup(f);}
});

for(const signal of ['SIGINT','SIGTERM'])test(signal+' shutdown closes an active take',async()=>{
 const f=fixture();try{await ready(f);const a=await start(f),exit=once(f.child,'exit');f.child.kill(signal);const [code]=await bounded(exit,15000,'server ignored '+signal);assert.equal(code,0);closed(f,a.pid);}finally{await cleanup(f);}
});

test('a stalled CDP detach cannot wedge take/start',async()=>{
 const f=fixture();try{await ready(f);f.configure({fault:'cdp-send',block:'detach'});
  const r=await bounded(post(f,'takeStart',data),10000,'take/start wedged behind a stalled CDP detach');
  assert.equal(r.status,E.internal.status);assert.equal((await r.json()).code,E.internal.code);
  closed(f,f.events().filter(e=>e.event==='launch').at(-1).pid,{end:false});assert.equal((await status(f)),null,'failed start publishes no capturing take');
  f.configure({fault:null,block:null});const b=await start(f);assert.equal((await post(f,'takeStop',{takeId:b.takeId})).status,200);closed(f,b.pid);
 }finally{f.configure({fault:null,block:null});await cleanup(f);}
});

test('shutdown during a retained browser\'s disposal: the start answers busy and never launches',async()=>{
 const f=fixture();try{await ready(f);const a=await start(f);f.configure({fault:'close'});
  assert.equal((await post(f,'takeStop',{takeId:a.takeId})).status,E.internal.status);assert.ok(alive(a.pid),'close rejection must retain ownership');f.configure({fault:null,block:'end'});
  const request=post(f,'takeStart',data);request.catch(()=>{});
  await until(()=>f.events().some(e=>e.event==='blocked'&&e.at==='end'&&e.pid===a.pid),'start never reached the retained browser disposal');
  const exit=once(f.child,'exit');f.child.kill('SIGTERM');
  await until(()=>f.stderr.includes('bridge shutdown: draining takes'),'shutdown did not report draining');
  const attempts=f.events().filter(e=>e.event==='launch-attempt').length;
  await hold(300,()=>assert.equal(f.child.exitCode,null,'shutdown exited behind a blocked disposal'));
  f.configure({block:null});
  const r=await bounded(request,15000,'start never answered after shutdown');assert.equal(r.status,E.busy.status);assert.equal((await r.json()).code,E.busy.code);
  assert.equal((await bounded(exit,15000,'server did not exit after unblock'))[0],0);
  closed(f,a.pid); // the blocked disposal completed: end precedes close
  assert.equal(f.events().filter(e=>e.event==='launch-attempt').length,attempts,'a take launched after shutdown began');
 }finally{f.configure({fault:null,block:null});await cleanup(f);}
});

test('shutdown during admission refuses before the end-hinting disposal and before launch',async()=>{
 const f=fixture();try{await ready(f);const a=await start(f);f.configure({fault:'close'});
  assert.equal((await post(f,'takeStop',{takeId:a.takeId})).status,E.internal.status);assert.ok(alive(a.pid),'close rejection must retain ownership');f.configure({fault:null,block:'end'});
  const request=post(f,'takeStart',data);request.catch(()=>{});
  // Admission creates gd-three-<tmp> before spawning the compiler; the signal lands inside that window.
  await until(()=>fs.readdirSync(f.tmp).some(e=>e.startsWith('gd-three-')),'admission never started');
  const exit=once(f.child,'exit');f.child.kill('SIGTERM');
  await until(()=>f.stderr.includes('bridge shutdown: draining takes'),'shutdown did not report draining');
  // With the after-admit guard the refusal is immediate; the retained browser's
  // disposal belongs to the drain (still blocked below). Without it the start
  // itself runs the end-hinting disposal and answers only after the 2 s disposal
  // bound releases it — by then the retained browser is already closed.
  const r=await bounded(request,10000,'start never answered after shutdown during admission');
  assert.equal(r.status,E.busy.status,'start must refuse busy once shutdown begins: '+JSON.stringify(await r.clone().json()));
  assert.equal((await r.json()).code,E.busy.code);
  assert.ok(alive(a.pid),'retained browser died before the drain disposed it');
  assert.ok(!f.events().some(e=>e.event==='closed'&&e.pid===a.pid),'a refused start disposed the retained browser; that belongs to the drain');
  await until(()=>f.events().some(e=>e.event==='blocked'&&e.at==='end'&&e.pid===a.pid),'drain never reached the retained browser disposal');
  await hold(300,()=>{assert.equal(f.child.exitCode,null,'shutdown exited behind a blocked disposal');assert.ok(alive(a.pid),'retained browser died before disposal');});
  f.configure({block:null});
  assert.equal((await bounded(exit,15000,'server did not exit after unblock'))[0],0);
  closed(f,a.pid);
  assert.equal(f.events().filter(e=>e.event==='launch').length,2,'a take launched after shutdown began'); // startup probe + a only
 }finally{f.configure({fault:null,block:null});await cleanup(f);}
});

test('shutdown keeps the listener open until the queue drains: an in-flight start answers busy',async()=>{
 const f=fixture();try{await ready(f);f.configure({block:'goto'});
  const request=post(f,'takeStart',data);request.catch(()=>{});
  const blocked=await until(()=>f.events().find(e=>e.event==='blocked'&&e.at==='goto'),'in-flight start never blocked before goto');
  const exit=once(f.child,'exit');f.child.kill('SIGTERM');
  await until(()=>f.stderr.includes('bridge shutdown: draining takes'),'shutdown did not report draining');
  await hold(300,()=>assert.equal(f.child.exitCode,null,'shutdown exited behind an in-flight start'));
  f.configure({block:null});
  const r=await bounded(request,15000,'in-flight start never answered');
  assert.equal(r.status,E.busy.status,'the stage fetch must be served until the queue drains: '+JSON.stringify(await r.clone().json()));
  assert.equal((await bounded(exit,15000,'server did not exit after unblock'))[0],0);
  closed(f,blocked.pid,{end:false});
 }finally{f.configure({block:null});await cleanup(f);}
});

test('shutdown close rejection retains ownership; a later signal retries instead of orphaning',async()=>{
 const f=fixture();try{await ready(f);const a=await start(f);f.configure({fault:'close'});const exit=once(f.child,'exit');f.child.kill('SIGTERM');
  await until(()=>{assert.equal(f.child.exitCode,null,'shutdown exited while its browser was still live');return f.stderr.includes('shutdown cleanup:');},'shutdown did not report failed cleanup');
  assert.ok(alive(a.pid));f.configure({fault:null});f.child.kill('SIGTERM');assert.equal((await bounded(exit,15000,'shutdown retry did not exit'))[0],0);closed(f,a.pid);
 }finally{f.configure({fault:null});await cleanup(f);}
});

test('shutdown during a blocked launch drains the queue; nothing launches after shutdown begins',async()=>{
 const f=fixture();try{await ready(f);f.configure({block:'launch'});
  const first=post(f,'takeStart',data);first.catch(()=>{});
  const blocked=await until(()=>f.events().find(e=>e.event==='blocked'&&e.at==='launch'),'in-flight start never blocked inside launch');
  // The second request is schema-invalid: if the shutdown guard is missing it dies
  // at validation (400), never as busy (503). The server log line proves the queued
  // request is chained before the signal lands.
  const queued=post(f,'takeStart',{timeline:null,frameRate:1,width:64,height:64});queued.catch(()=>{});
  await until(()=>f.stderr.includes('request queued behind active work'),'queued take/start never reached the server: '+f.stderr);
  const launchesBefore=f.events().filter(e=>e.event==='launch').length,attemptsBefore=f.events().filter(e=>e.event==='launch-attempt').length;
  const exit=once(f.child,'exit');f.child.kill('SIGTERM');
  await until(()=>f.stderr.includes('bridge shutdown: draining takes'),'shutdown did not report draining');
  await hold(700,()=>{assert.equal(f.child.exitCode,null,'shutdown exited behind a blocked in-flight start');assert.ok(alive(blocked.pid),'blocked launch child died');});
  f.configure({block:null});
  const [r1,r2]=await Promise.all([first,queued]);
  assert.equal(r1.status,E.busy.status,'in-flight start must answer busy once shutdown begins');assert.equal((await r1.json()).code,E.busy.code);
  assert.equal(r2.status,E.busy.status,'queued start must answer busy once shutdown begins');assert.equal((await r2.json()).code,E.busy.code);
  assert.equal((await bounded(exit,15000,'server did not exit after unblock'))[0],0);
  closed(f,blocked.pid,{end:false});
  assert.equal(f.events().filter(e=>e.event==='launch').length,launchesBefore,'a queued take launched after shutdown began');
  assert.equal(f.events().filter(e=>e.event==='launch-attempt').length,attemptsBefore,'a queued take attempted launch after shutdown began');
 }finally{f.configure({block:null});await cleanup(f);}
});
