// Run the real Three HTTP server; only Chromium is replaced (no rendering).
// Owned /tmp fixtures and a PATH dotnet proxy exercise refusal without a product seam.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import {tempDirectory} from './test_temp.mjs';
import path from 'node:path';
import {spawn,execFileSync} from 'node:child_process';
import {once} from 'node:events';
import {fileURLToPath} from 'node:url';
import {BootstrapContractTimeoutMs,Uses,engineStamps,refusal} from '../packages/gamedirector-three/contract.mjs';
import {bundleFixture,changeConfig,engineWithWrongPin,reviewRefusals,editEntry} from './three_bundle_fixture.mjs';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..'),cli=path.join(repo,'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll');
const contractStdout=execFileSync('dotnet',[cli,'contract','--json'],{encoding:'utf8'}),contract=JSON.parse(contractStdout),Errors=contract.bridge.errors,R=contract.bridge.routes;
const route=name=>'/'+R[name],StartupWait=BootstrapContractTimeoutMs+15000;
const realDotnet=execFileSync(process.platform==='win32'?'where':'which',['dotnet'],{encoding:'utf8'}).trim().split(/\r?\n/)[0];
function fixture(){
 const temp=tempDirectory('gd-three-admission-'),{root}=temp,game=path.join(root,'game'),adapter=path.join(root,'adapter'),modules=path.join(root,'modules'),three=path.join(root,'three'),launches=path.join(root,'launches.jsonl'),navigations=path.join(root,'navigations.jsonl');
 for(const folder of [path.join(game,'src'),adapter,path.join(modules,'node_modules/playwright'),three])fs.mkdirSync(folder,{recursive:true});
 fs.writeFileSync(path.join(adapter,'manifest.json'),JSON.stringify({game:'Admission',manifestVersion:'0.1',roles:[{id:'hero',defaultActor:'body',presentAtStart:true}],actors:[{id:'body',clips:[]}],locations:[{id:'front',position:[0,1,3]}],audio:[],shotTypes:['lockoff'],frameTypes:['full'],capabilities:{}}));
 fs.writeFileSync(path.join(modules,'package.json'),'{}');
 fs.writeFileSync(path.join(modules,'node_modules/playwright/package.json'),JSON.stringify({name:'playwright',version:'fixture'}));
 fs.writeFileSync(path.join(modules,'node_modules/playwright/index.js'),`module.exports={chromium:{launch:async()=>{require('node:fs').appendFileSync(${JSON.stringify(launches)},'launch\\n');return {newBrowserCDPSession:async()=>({send:async()=>({processInfo:[{type:'browser',id:process.pid}]}),detach:async()=>{}}),newPage:async()=>({on(){},goto:async(url,options)=>{require('node:fs').appendFileSync(${JSON.stringify(navigations)},JSON.stringify({url,options})+'\\n');},waitForFunction:async()=>{},evaluate:async(fn,arg)=>{if(fn.toString().includes('UNMASKED_RENDERER_WEBGL'))return 'fixture GL';if(arg?.c?.id==='adapter-bug')throw TypeError('adapter exploded');if(arg?.c?.id==='source-drift')require('node:fs').writeFileSync(${JSON.stringify(path.join(game,'src/drift.txt'))},'changed');return {png:'iVBORw0KGgo='};}}),version:()=>'fixture',close:async()=>{}};}}};`);
 return {root,game,adapter,modules,three,launches,navigations,cleanup:temp.cleanup};
}
function start(f,{port=0,shim=false}={}){
 const child=spawn(process.execPath,[f.server||path.join(repo,'packages/gamedirector-three/server.mjs'),'--port',String(port),'--game-root',f.game,'--adapter',f.adapter,'--three-root',f.three,'--cli',cli],
  {env:{...process.env,GAMEDIRECTOR_NODE_MODULES:f.modules,...(shim?{PATH:f.root+path.delimiter+process.env.PATH}:{})}});
 const run={child,log:'',errors:''};child.stdout.on('data',data=>run.log+=data);child.stderr.on('data',data=>run.errors+=data);return run;
}
async function endpoint(run){
 return new Promise((resolve,reject)=>{
  const timer=setTimeout(()=>reject(Error('Three startup timeout: '+run.errors)),StartupWait);
  run.child.once('exit',code=>{clearTimeout(timer);reject(Error('Three exited '+code+': '+run.errors));});
  run.child.stdout.on('data',()=>{const end=run.log.indexOf('\n');if(end>=0){clearTimeout(timer);resolve(JSON.parse(run.log.slice(0,end)).endpoint);}});
 });
}
async function stop(run){if(run?.child.exitCode===null&&run.child.signalCode===null){const stopped=once(run.child,'exit');run.child.kill('SIGTERM');await stopped;}}
async function cleanup(f,run){try{await stop(run);}finally{f.cleanup();}}
function shim(f,command=null,change='',{indent=3,ending='\n'}={}){
 // Proxy *all* invocations byte-for-byte except the requested export. Rewrites
 // deliberately use noncanonical indentation/newlines to expose reserialization.
 fs.writeFileSync(path.join(f.root,'dotnet'),`#!/usr/bin/env node
const {spawnSync}=require('node:child_process');const args=process.argv.slice(2),r=spawnSync(${JSON.stringify(realDotnet)},args,{encoding:'utf8',maxBuffer:8*1024*1024});
if(r.error)throw r.error;let stdout=r.stdout;if(stdout&&args[1]===${JSON.stringify(command)}&&args.includes(${JSON.stringify(command==='contract'?'--json':'--take')})){const c=JSON.parse(stdout);${change};stdout=JSON.stringify(c,null,${indent})+${JSON.stringify(ending)};}
process.stdout.write(stdout);process.stderr.write(r.stderr);process.exit(r.status);
`,{mode:0o755});
}
const timeline={version:'0.1',id:'errors',cues:[{t:0,type:'camera.shot',shot:{type:'lockoff',subject:'hero',from:'front',frame:'full',durationSeconds:2}}]};
const request={timeline,frameRate:contract.capture.minFrameRate,width:contract.capture.minDimension,height:contract.capture.minDimension};
const post=(url,pathname,data)=>fetch(url+pathname,{method:'POST',headers:{'Content-Type':'application/json'},body:typeof data==='string'?data:JSON.stringify(data)});
async function failure(url,pathname,data,kind){const r=await post(url,pathname,data),answer=await r.json();assert.equal(r.status,Errors[kind].status,JSON.stringify(answer));assert.equal(answer.code,Errors[kind].code);assert.equal(typeof answer.error,'string');return answer;}

test('Three owns admission, deliberate error codes and unknown adapter failures',async()=>{
 const f=fixture(),run=start(f);
 try{
  const url=await endpoint(run);
  for(const site of ['cross-site','same-site','','invalid'])for(const pathname of [route('health'),route('manifest'),route('capture')]){
   const response=await fetch(url+pathname,{headers:{'Sec-Fetch-Site':site}}),answer=await response.json();
   assert.equal(response.status,Errors.forbidden.status,site+' '+pathname);assert.equal(answer.code,Errors.forbidden.code);assert.match(answer.error,/cross-site/);
  }
  for(const site of [undefined,'none','same-origin']){const response=await fetch(url+route('manifest'),{headers:site===undefined?{}:{'Sec-Fetch-Site':site}});assert.equal(response.status,200);assert.equal((await response.json()).game,'Admission');}
  const rebinding=await new Promise((resolve,reject)=>{http.get(url+route('health'),{headers:{Host:'untrusted.invalid'}},response=>{response.resume();resolve(response.statusCode);}).on('error',reject);});assert.equal(rebinding,Errors.forbidden.status);
  const fail=(name,data,kind)=>failure(url,route(name),data,kind);
  await failure(url,'/no-such-route',{},'notFound');
  await fail('takeStart','{broken','invalidRequest');await fail('takeStart','null','invalidRequest');
  const noTimeline={...request};delete noTimeline.timeline;await fail('takeStart',noTimeline,'invalidRequest');
  for(const timeline of [null,[],'not-an-object'])await fail('takeStart',{...request,timeline},'invalidRequest');
  await fail('takeStart',{...request,width:contract.capture.minDimension-1},'invalidRequest');
  await fail('takeStart',{...request,width:String(contract.capture.minDimension)},'invalidRequest');
  await fail('takeStart',{...request,frameRate:1.5},'invalidRequest');await fail('takeStart',{...request,width:1e10},'invalidRequest');
  await fail('takeStart',{...request,captureFromFrame:3e9},'invalidRequest');await fail('takeStart',{...request,captureFromFrame:null},'invalidRequest');
  await fail('takeStart',{...request,timeline:{...timeline,oops:1}},'invalidRequest');await fail('takeStart',{...request,sourceFingerprint:'stale'},'stateConflict');
  await fail('takeStart',{...request,timeline:{...timeline,id:'source-drift'}},'stateConflict');await fail('takeStart',{...request,timeline:{...timeline,id:'adapter-bug'}},'internal');
  // Inclusive byte ceiling: exactly maxRequestBytes must reach admission and
  // execute a valid take; one extra byte must never reach the compiler/stage.
  const encoded=JSON.stringify(request),atLimit=encoded+' '.repeat(contract.bridge.limits.maxRequestBytes-Buffer.byteLength(encoded));
  assert.equal(Buffer.byteLength(atLimit),contract.bridge.limits.maxRequestBytes);
  await fail('takeStart',atLimit+' ','payloadTooLarge');
  const bounded=await post(url,route('takeStart'),atLimit);assert.equal(bounded.status,200);const boundedTake=await bounded.json();
  assert.equal((await post(url,route('takeStop'),{takeId:boundedTake.takeId})).status,200);
  const response=await post(url,route('takeStart'),request);assert.equal(response.status,200);const take=await response.json();
  assert.equal(take.audio,contract.take.silentPictureAudio);assert.equal(take.captureFromFrame,contract.take.requestDefaults.captureFromFrame);
  // Without the object clause these answer stateConflict: stop reads a missing
  // takeId and start's active-take guard runs before timeline checks.
  for(const scalar of ['5','"x"','true']){await fail('takeStop',scalar,'invalidRequest');await fail('takeStart',scalar,'invalidRequest');}
  await fail('takeStart',request,'stateConflict');await fail('takeFrame','null','invalidRequest');await fail('takeFrame','[]','invalidRequest');await fail('takeStop','[]','invalidRequest');
  await fail('takeFrame',{takeId:'not-owned',frameIndex:0},'stateConflict');await fail('takeFrame',{takeId:take.takeId,frameIndex:'0'},'invalidRequest');
  await fail('takeFrame',{takeId:take.takeId,frameIndex:3e9},'invalidRequest');await fail('takeFrame',{takeId:take.takeId,frameIndex:1.5},'invalidRequest');
  await fail('takeFrame',{takeId:take.takeId,frameIndex:0,advanceOnly:'yes'},'invalidRequest');await fail('takeFrame',{takeId:take.takeId,frameIndex:0,advanceOnly:null},'invalidRequest');
  await fail('takeFrame',{takeId:take.takeId,frameIndex:0,measure:'yes'},'invalidRequest');await fail('takeFrame',{takeId:take.takeId,frameIndex:0,measure:null},'invalidRequest');
  await fail('takeFrame',{takeId:take.takeId,frameIndex:0,advanceOnly:true,measure:true},'invalidRequest');
  await fail('takeFrame',{takeId:take.takeId,frameIndex:1},'stateConflict');await fail('takeStop',{takeId:'not-owned'},'stateConflict');await fail('timelinePlay',timeline,'busy');
  assert.equal((await post(url,route('takeStop'),{takeId:take.takeId})).status,200);
  // Strict-parse failures of the engine manifest are adapter/Core skew, not a
  // film admission verdict: internal even when the manifest version is absent.
  const manifestPath=path.join(f.adapter,'manifest.json'),saved=fs.readFileSync(manifestPath),missingVersion=JSON.parse(saved);delete missingVersion.manifestVersion;
  for(const skew of [{...JSON.parse(saved),unexpectedField:true},missingVersion,{...JSON.parse(saved),manifestVersion:'0.2'},
    {...JSON.parse(saved),roles:[...JSON.parse(saved).roles,...JSON.parse(saved).roles]}, {...JSON.parse(saved),capabilities:{'actor.animation.fade':'wrong'}}]){
   fs.writeFileSync(manifestPath,JSON.stringify(skew));await fail('takeStart',request,'internal');
  }
  fs.writeFileSync(manifestPath,saved);await fail('takeStart',{...request,timeline:{...timeline,cues:[{...timeline.cues[0],shot:{...timeline.cues[0].shot,subject:'ghost'}}]}},'invalidRequest');
 }finally{await cleanup(f,run);}
});

test('engine source skew is a state conflict, not an adapter/internal fault',async()=>{
 const {assertEngineSource,classifyError}=await import('../packages/gamedirector-three/bridge-errors.mjs');
 assert.doesNotThrow(()=>assertEngineSource('unchanged','unchanged',Errors));
 assert.throws(()=>assertEngineSource('changed','startup',Errors),error=>{const answer=classifyError(error,Errors);
  assert.equal(answer.status,Errors.stateConflict.status);assert.equal(answer.body.code,Errors.stateConflict.code);assert.match(answer.body.error,/engine source changed; restart the bridge/);return true;});
});
test('a stale engine contract fails naming every consumed protocol key',()=>{
 const opts={stamps:engineStamps(contract,{sourceFingerprint:'fixture',sourceRoot:'/tmp/fixture'})};assert.equal(refusal(contract,opts),null);
 const keys=['take.states','capture','frameClock','bridge.errors',...Object.keys(contract.take.states).filter(k=>Uses.states.includes(k)).map(k=>'take.states.'+k),
  'capture.idleLeaseSeconds','frameClock.cueEpsilonSeconds',...Object.keys(Errors).filter(k=>Uses.errors.includes(k)).flatMap(k=>['bridge.errors.'+k,'bridge.errors.'+k+'.status','bridge.errors.'+k+'.code']),
  ...Object.keys(R).filter(k=>Uses.routes.includes(k)).map(k=>'bridge.routes.'+k)];
 for(const key of keys){const stale=structuredClone(contract),parts=key.split('.');let owner=stale;for(const p of parts.slice(0,-1))owner=owner[p];delete owner[parts.at(-1)];
  assert.equal(refusal(stale,opts),'rebuild gd: contract lacks '+key,key);owner[parts.at(-1)]=null;assert.equal(refusal(stale,opts),'rebuild gd: contract lacks '+key,key);}
});
async function freePort(){const s=net.createServer();await new Promise(resolve=>s.listen(0,contract.bridge.loopbackHost,resolve));const port=s.address().port;await new Promise(resolve=>s.close(resolve));return port;}
test('startup refuses incompatible, uncovered and stale contracts before browser or listener',async()=>{
 const changes=[['major+1','c.version.major++',/implements.*provides/],['minor below','c.version.minor=-1',/implements.*provides/],['ease',"c.camera.eases[0].formula='new-ease'",/ease formula.*new-ease/],
  ['motion',"c.camera.shots[0].motion='new-motion'",/motion.*new-motion/],['stamp',"c.capabilities.push({key:'new-stamp',declarer:'engine'})",/stamp.*new-stamp/],
  ...['bridge.errors','take.states.completed','capture.idleLeaseSeconds','frameClock.cueEpsilonSeconds','bridge.routes.takeStart'].map(k=>[k,'delete c.'+k,new RegExp('contract lacks '+k.replaceAll('.','\\.'))])];
 for(const [name,change,reason] of changes){const f=fixture();shim(f,'contract',change);const port=await freePort(),run=start(f,{port,shim:true});
  let timer,opened=false;
  const probe=setInterval(()=>{const socket=net.connect(port,contract.bridge.loopbackHost);socket.on('connect',()=>{opened=true;socket.destroy();});socket.on('error',()=>{});},10);
  try{
   const [code]=await Promise.race([once(run.child,'exit'),new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('refusal startup timeout: '+run.errors)),StartupWait);})]);
   assert.notEqual(code,0,name);assert.match(run.errors,reason,name);assert.equal(run.log.trim(),'',name+' opened listener');
   assert.equal(opened,false,name+' opened port');assert.equal(fs.existsSync(f.launches),false,name+' launched Playwright');
   await assert.rejects(fetch(`http://${contract.bridge.loopbackHost}:${port}/`),name+' left a port open');
  }finally{clearTimeout(timer);clearInterval(probe);await cleanup(f,run);}
 }
});
test('three.json refusals name their cause before a port opens or Playwright launches',async()=>{
 const cases=[
  ['gameSha',b=>changeConfig(b,c=>c.gameSha='wrong-sha'),/gameSha.*provenance.*sha/],
  ['second THREE',b=>fs.appendFileSync(path.join(b.familyRoot,'adapter.ts'),"\nimport {Scene} from 'game:src/second-three.ts';console.log(Scene);\n"),/second THREE.*node_modules\/three/],
  ['forbid',b=>fs.appendFileSync(path.join(b.familyRoot,'adapter.ts'),"\nimport {network} from 'game:src/forbidden.ts';console.log(network);\n"),/forbid.*game:src\/forbidden.ts/],
  ['outside roots',b=>{fs.writeFileSync(path.join(b.root,'outside.ts'),'export const outside=1;');fs.appendFileSync(path.join(b.familyRoot,'adapter.ts'),"\nimport {outside} from '../outside.ts';console.log(outside);\n");},/outside.*(?:snapshot|roots).*outside.ts/],
  ['three version',b=>changeConfig(b,c=>c.three='0.999.0'),/three version.*0\.170\.0.*0\.999\.0/],
  ['missing export',b=>b.threeRoot=path.join(b.gameRoot,'stub-three'),/missing.*PCFSoftShadowMap/],
  ['esbuild version',(b,f)=>f.server=path.join(engineWithWrongPin(b),'server.mjs'),/esbuild version.*pin.*0\.0\.0/],
  ...reviewRefusals,
 ];
 for(const [name,change,reason] of cases){const f=fixture(),b=bundleFixture();let run,timer,probe,opened=false;
  try{change(b,f);Object.assign(f,{game:b.gameRoot,adapter:b.adapterRoot,three:b.threeRoot});const port=await freePort();run=start(f,{port});
   probe=setInterval(()=>{const socket=net.connect(port,contract.bridge.loopbackHost);socket.on('connect',()=>{opened=true;socket.destroy();});socket.on('error',()=>{});},10);
   const [code]=await Promise.race([once(run.child,'exit'),new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('three.json refusal startup timeout: '+run.errors)),StartupWait);})]);
   assert.notEqual(code,0,name);assert.match(run.errors,reason,name);assert.equal(run.log.trim(),'',name+' opened listener');assert.equal(opened,false,name+' opened port');
   assert.equal(fs.existsSync(f.launches)?fs.readFileSync(f.launches,'utf8').trim().split('\n').length:0,0,name+' launched Playwright');await assert.rejects(fetch(`http://${contract.bridge.loopbackHost}:${port}/`));
  }finally{clearTimeout(timer);clearInterval(probe);await cleanup(f,run);b.cleanup();}
 }
});
test('three.json contract refusal precedes bundle refusal',async()=>{
 const f=fixture(),b=bundleFixture();let run;
 try{changeConfig(b,c=>c.gameSha='wrong-sha');Object.assign(f,{game:b.gameRoot,adapter:b.adapterRoot,three:b.threeRoot});shim(f,'contract','c.version.major++');run=start(f,{shim:true});
  const [code]=await once(run.child,'exit');assert.notEqual(code,0);assert.match(run.errors,/implements.*provides/);assert.doesNotMatch(run.errors,/gameSha/);assert.equal(fs.existsSync(f.launches),false);
 }finally{await cleanup(f,run);b.cleanup();}
});
test('three.json removes the disk game source mount; raw JS keeps it',async()=>{
 for(const optIn of [true,false]){const f=fixture(),b=optIn?bundleFixture():null;let run;
  try{if(b)Object.assign(f,{game:b.gameRoot,adapter:b.adapterRoot,three:b.threeRoot});fs.writeFileSync(path.join(f.game,'src/probe.js'),'export const diskSource=1;');run=start(f);const url=await endpoint(run),response=await fetch(url+'/game/src/probe.js');assert.equal(response.status,optIn?Errors.notFound.status:200,'unfingerprinted game mount');if(!optIn)assert.match(await response.text(),/diskSource/);
  }finally{await cleanup(f,run);b?.cleanup();}
 }
});
for(const where of ['stage','vendor'])test('three.json added-file stat check names the '+where+' directory',async()=>{
 const f=fixture(),b=bundleFixture();let run;
 try{if(where==='vendor'){b.threeRoot=path.join(b.root,'vendor');fs.cpSync(path.join(repo,'packages/gamedirector-three/node_modules/three'),b.threeRoot,{recursive:true});}
  Object.assign(f,{game:b.gameRoot,adapter:b.adapterRoot,three:b.threeRoot});run=start(f);const url=await endpoint(run),directory=where==='stage'?b.adapterRoot:path.join(b.threeRoot,'build');fs.writeFileSync(path.join(directory,'added.js'),'export const added=1;');const data={...request,timeline:JSON.parse(fs.readFileSync(path.join(b.familyRoot,'timeline.json')))},answer=await failure(url,route('takeStart'),data,'stateConflict');assert.ok(answer.error.includes(where==='stage'?'family:stage/':'three:build/'),answer.error);
 }finally{await cleanup(f,run);b.cleanup();}
});
test('three.json input edit is a named 409 without navigation or esbuild invocation',async()=>{
 const f=fixture(),b=bundleFixture();let run;
 try{Object.assign(f,{game:b.gameRoot,adapter:b.adapterRoot,three:b.threeRoot});run=start(f);const url=await endpoint(run),timeline=JSON.parse(fs.readFileSync(path.join(b.familyRoot,'timeline.json'))),data={...request,timeline};
  const original=await (await fetch(url+'/adapter/adapter.js')).text(),buildCount=async()=>(await (await fetch(url+route('health'))).json()).bundleBuildCount;assert.match(original,/from\s*["']three["']/);const startupBuilds=await buildCount();assert.equal(startupBuilds,1,'real esbuild invocation count unavailable');
  for(let i=0;i<3;i++){const response=await post(url,route('takeStart'),data);assert.equal(response.status,200);const t=await response.json();assert.equal((await post(url,route('takeStop'),{takeId:t.takeId})).status,200);}
  const navigations=fs.readFileSync(f.navigations,'utf8');editEntry(b);
  const answer=await failure(url,route('takeStart'),data,'stateConflict');assert.match(answer.error,/bundle input changed since startup: family:adapter.ts; restart the bridge/);
  assert.equal(fs.readFileSync(f.navigations,'utf8'),navigations);assert.equal(await (await fetch(url+'/adapter/adapter.js')).text(),original,'bundle rebuilt');
  const timings=[...run.errors.matchAll(/bundle stat ([\d.]+) ms; rebuild=false/g)].map(m=>Number(m[1]));assert.ok(timings.length>=4,run.errors);for(const ms of timings.slice(1))assert.ok(ms<=50,'stat cost '+ms+'ms');
  assert.equal(await buildCount(),startupBuilds,'take/start invoked esbuild despite 409');
  for(let i=2;i+1<timings.length;i+=2)assert.ok(timings[i]+timings[i+1]<=50,'per-take stat cost exceeds 50ms');
 }finally{await cleanup(f,run);b.cleanup();}
});
test('dotnet shim passes unmodified CLI stdout through byte-for-byte',async()=>{
 const f=fixture();try{shim(f);assert.equal(execFileSync(path.join(f.root,'dotnet'),[cli,'contract','--json'],{encoding:'utf8'}),contractStdout);}finally{await cleanup(f);}
});
test('startup accepts a covered higher minor and serves its exact bootstrap bytes',async()=>{
 const f=fixture();let run;
 try{shim(f,'contract','c.version.minor++');const emitted=execFileSync(path.join(f.root,'dotnet'),[cli,'contract','--json'],{encoding:'utf8'});
  assert.notEqual(emitted,JSON.stringify(JSON.parse(emitted)),'fixture must expose reserialization');assert.ok(emitted.endsWith('\n'));
  run=start(f,{shim:true});const url=await endpoint(run),response=await fetch(url+route('contract')),bytes=await response.text();assert.equal(response.status,200);assert.equal(bytes,emitted);
  const newer=structuredClone(contract);newer.version.minor++;assert.deepEqual(JSON.parse(bytes),newer);assert.equal((await fetch(url+route('manifest'))).status,200);
 }finally{await cleanup(f,run);}
});
test('contract stdout formatting alone changes the manifest fingerprint',async()=>{
 const f=fixture(),bytes=[],manifests=[];let run;
 try{
  // Sequential listeners share the exact same physical game/adapter/three and
  // browser version. The PATH shim is outside every fingerprint source root.
  for(const indent of [3,5]){shim(f,'contract','',{indent});bytes.push(execFileSync(path.join(f.root,'dotnet'),[cli,'contract','--json'],{encoding:'utf8'}));
   run=start(f,{shim:true});const url=await endpoint(run),response=await fetch(url+route('manifest'));assert.equal(response.status,200);manifests.push(await response.json());
   await stop(run);run=null;
  }
  assert.notEqual(bytes[0],bytes[1]);assert.deepEqual(JSON.parse(bytes[0]),JSON.parse(bytes[1]));assert.deepEqual(JSON.parse(bytes[0]),contract);
  const key='presentation.sourceFingerprint';assert.notEqual(manifests[0].capabilities[key],manifests[1].capabilities[key],'exact contract stdout digest must affect fingerprint');
  for(const m of manifests)delete m.capabilities[key];assert.deepEqual(manifests[0],manifests[1]);
 }finally{await cleanup(f,run);}
});
test('compile envelope version mismatch is internal and names restart before stage navigation',async()=>{
 const f=fixture();shim(f,'compile',"c.contract='2.0'");const run=start(f,{shim:true});
 try{const url=await endpoint(run);
  for(const data of [request,{...request,width:contract.capture.minDimension-1}]){const answer=await failure(url,route('takeStart'),data,'internal');assert.match(answer.error,/restart the bridge/);}
  const status=await (await fetch(url+route('take'))).json();assert.equal(status.take,null);assert.equal(fs.existsSync(f.navigations),false,'skewed envelope reached stage');
 }finally{await cleanup(f,run);}
});
test('compile envelope minor version mismatch is internal and names restart before stage navigation',async()=>{
 const f=fixture();shim(f,'compile',"c.contract='1.2'");const run=start(f,{shim:true});
 try{const url=await endpoint(run),loaded=await (await fetch(url+route('contract'))).json();
  assert.deepEqual(loaded.version,{major:1,minor:2});
  // Matching minor must admit: a reject-all guard cannot satisfy this test.
  const matching=await post(url,route('takeStart'),request);assert.equal(matching.status,200,await matching.text());
  const take=await (await fetch(url+route('take'))).json();assert.equal((await post(url,route('takeStop'),{takeId:take.take.takeId})).status,200);
  const stopped=await (await fetch(url+route('take'))).json(),navigations=fs.readFileSync(f.navigations,'utf8');shim(f,'compile',"c.contract='1.3'");
  for(const data of [request,{...request,width:contract.capture.minDimension-1}]){const answer=await failure(url,route('takeStart'),data,'internal');assert.match(answer.error,/restart the bridge/);}
  const status=await (await fetch(url+route('take'))).json();assert.deepEqual(status.take,stopped.take,'skewed envelope replaced the stopped take');
  assert.equal(fs.readFileSync(f.navigations,'utf8'),navigations,'skewed envelope reached stage');
 }finally{await cleanup(f,run);}
});
test('queue admits the contract limit and rejects only the next pending body',async()=>{
 const f=fixture(),run=start(f),held=[],arrivals=[],responses=[];let timer;
 try{
  const url=await endpoint(run);
  for(let i=0;i<contract.bridge.limits.maxQueuedRequests;i++){
   const response=new Promise((resolve,reject)=>{const r=http.request(url+route('takeStop'),{method:'POST',headers:{'Content-Type':'application/json','Content-Length':1,Expect:'100-continue'}},response=>{let text='';response.on('data',c=>text+=c);response.on('end',()=>resolve({status:response.statusCode,body:JSON.parse(text)}));});
    const arrival=new Promise((resolve,reject)=>{r.once('continue',resolve);r.once('error',reject);});arrival.catch(()=>{});arrivals.push(arrival);
    r.on('error',reject);r.flushHeaders();held.push(r);});response.catch(()=>{});responses.push(response);
  }
  // 100-continue acknowledges each header block. In this server, pending++
  // is synchronous before the first await (body); that handler must finish
  // this prefix before servicing any subsequent probe. No body is sent yet.
  await Promise.race([Promise.all(arrivals),new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('held request header barrier timed out')),StartupWait);})]);clearTimeout(timer);
  // After all 32 acknowledgements, even one stateConflict/non-busy admission
  // is a failure, not a retry. Check repeatedly while the bodies remain held.
  for(let i=0;i<3;i++)await failure(url,route('takeStop'),{},'busy');
  for(const r of held)r.end('0');for(const answer of await Promise.all(responses)){assert.equal(answer.status,Errors.invalidRequest.status);assert.equal(answer.body.code,Errors.invalidRequest.code);}
 }finally{clearTimeout(timer);for(const r of held)r.destroy();await cleanup(f,run);}
});
