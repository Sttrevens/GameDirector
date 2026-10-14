import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {ImplementedShotParams} from './shot-params.mjs';
import {GlBackends,declaredGl,readGlRenderer} from './gl-backends.mjs';
import {BridgeRequestError,classifyError,assertEngineSource} from './bridge-errors.mjs';
import {EngineId,BootstrapContractTimeoutMs,BootstrapContractBytes,engineStamps,refusal} from './contract.mjs';
const exec=promisify(execFile),here=path.dirname(fileURLToPath(import.meta.url));
const option=(name,fallback)=>{const i=process.argv.indexOf(name);return i<0?fallback:process.argv[i+1];};
const gameRoot=path.resolve(option('--game-root','')),adapterRoot=path.resolve(option('--adapter',''));
if(!option('--game-root')||!option('--adapter'))throw Error('Required: --game-root <source checkout> --adapter <adapter directory>');
const cli=path.resolve(option('--cli',path.join(here,'../../src/GameDirector.Cli/bin/Debug/net8.0/gd.dll')));
const threeRoot=path.resolve(option('--three-root',path.join(gameRoot,'node_modules/three')));
const require=createRequire(process.env.GAMEDIRECTOR_NODE_MODULES?path.join(process.env.GAMEDIRECTOR_NODE_MODULES,'package.json'):import.meta.url);
// Fetch once and refuse before launching Playwright or opening a listener.
// GET /contract serves these exact stdout bytes, also hashed into identity.
const contractStdout=(await exec('dotnet',[cli,'contract','--json'],{timeout:BootstrapContractTimeoutMs,maxBuffer:BootstrapContractBytes})).stdout;
const contract=JSON.parse(contractStdout);
const why=refusal(contract,{stamps:engineStamps(contract,{sourceFingerprint:'bootstrap',sourceRoot:gameRoot}),implementedParams:ImplementedShotParams});
if(why){process.stderr.write('[GameDirector] Bridge refused: '+why+'\n');process.exit(1);}
const States=contract.take.states,Errors=contract.bridge.errors,R=contract.bridge.routes,{limits,budgets}=contract.bridge,requestDefaults=contract.take.requestDefaults;
const contractVersion=contract.version.major+'.'+contract.version.minor,contractDigest=crypto.createHash('sha256').update(contractStdout).digest('hex');
const bundler=fs.existsSync(path.join(adapterRoot,'three.json'))?await import('./bundle.mjs'):null;
const bundle=bundler?await bundler.buildAdapter({gameRoot,adapterRoot,threeRoot}):null;
if(bundle)process.stderr.write('[GameDirector] bundle built inputs='+bundle.inputs.length+' provenance='+bundle.provenance.sha+'\n');
const {chromium}=require('playwright'),playwrightVersion=require('playwright/package.json').version;
// Signal handlers stay with the bridge: Playwright's own SIGINT/SIGTERM/SIGHUP
// handlers would race the drain (target-closed 500s, exit 130). Its 'exit'
// handler still kills launched browsers when the process exits.
const gl=declaredGl(bundle?.gl),backend=GlBackends[gl];
const browserOptions={headless:true,channel:option('--browser',undefined),args:['--disable-background-timer-throttling',...backend.args],handleSIGINT:false,handleSIGTERM:false,handleSIGHUP:false};
const pageOptions={deviceScaleFactor:1,hasTouch:false,isMobile:false,reducedMotion:'no-preference'};
const raced=(promise,ms,what)=>new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error(what+' did not settle within its deadline')),Math.max(1,ms));promise.then(value=>{clearTimeout(timer);resolve(value);},error=>{clearTimeout(timer);reject(error);});});
const probeDeadline=performance.now()+budgets.pageLoadSeconds*1000,probeLeft=()=>Math.max(1,probeDeadline-performance.now()); // engine-local: seconds to milliseconds; startup launch/page/context share one load budget
const probe=await chromium.launch({...browserOptions,timeout:probeLeft()});let browserVersion,startupRenderer;
try{
 browserVersion=probe.version();
 const probePage=await raced(probe.newPage(pageOptions),probeLeft(),'GL probe page');startupRenderer=await raced(probePage.evaluate(readGlRenderer,false),probeLeft(),'GL renderer probe');
 if(backend.expect&&!backend.expect.test(startupRenderer))throw Error('declared GL backend '+gl+' expected '+backend.expect+' but actual renderer is '+startupRenderer);
 process.stderr.write('[GameDirector] startup gl='+startupRenderer+' backend='+gl+'\n');
}finally{await probe.close();}
// Wire integers are Int32 exactly (the CLI parses them into C# int); Core
// admission owns the legal ranges, so no range is restated here.
const int32=v=>Number.isInteger(v)&&v===(v|0);
const fail=(kind,message)=>new BridgeRequestError(Errors[kind],message);
let take=null,lastFrame=null,nextFrame=0,compiled=null,queue=Promise.resolve(),pending=0,lastRequest=0,takeBrowser=null,page=null,shuttingDown=false,shutdownPromise,queueBusy=false;
// gd.end() runs inside the renderer and Playwright's evaluate has no timeout, so
// a wedged main thread would never settle it; the disposal hint gets a short bound
// and browser.close() always follows. CDP calls and newPage accept no timeout
// either, so the same race puts them under the caller's load deadline.
const endDisposalMs=2*1000; // engine-local: seconds to milliseconds for the renderer-disposal bound; never gates browser.close()
// A fresh process, not merely a document/context: Chromium's GPU caches survive navigation.
// The shutdown guards live at admit and immediately before launch: nothing awaits
// between admit's resolution and this function's first real await, so those two
// checks cover every window in which a signal can be observed.
async function openTakeBrowser(takeId,loadDeadline){
 await closeTakeBrowser();
 if(shuttingDown)throw fail('busy','bridge is shutting down'); // a signal may have landed while the prior browser was closing
 const left=()=>Math.max(1,loadDeadline-performance.now());
 takeBrowser=await chromium.launch({...browserOptions,timeout:left()});
 const version=takeBrowser.version();if(version!==browserVersion)throw fail('internal','take browser version '+version+' differs from startup '+browserVersion+'; restart the bridge before another take');
 const session=await raced(takeBrowser.newBrowserCDPSession(),left(),'take browser CDP session');let pid;
 try{
  pid=(await raced(session.send('SystemInfo.getProcessInfo'),left(),'take browser process info')).processInfo.find(p=>p.type==='browser')?.id;
  await raced(session.detach(),left(),'take browser CDP detach').catch(()=>{}); // detach is hygiene: bounded and swallowed
 }catch(e){session.detach().catch(()=>{});throw e;} // a failed CDP read discards the browser; its detach is never awaited
 if(!Number.isSafeInteger(pid)||pid<=0)throw fail('internal','take browser PID unavailable');
 process.stderr.write(`[GameDirector] take ${takeId} browser pid=${pid} version=${version}\n`);
 page=await raced(takeBrowser.newPage(pageOptions),left(),'take page creation');page.on('pageerror',e=>process.stderr.write('render error: '+e.message+'\n'));
 page.on('console',m=>{if(m.type()==='warning'||m.type()==='error')process.stderr.write('[stage '+m.type()+'] '+m.text()+'\n');});
}
// Requests, lease expiry and shutdown serialize ownership; disposal failure cannot skip close.
// A failed start discards the process, so it closes directly without the renderer hint.
async function closeTakeBrowser({end=true}={}){
 const browser=takeBrowser,ownedPage=page;if(!browser)return;
 try{if(end&&ownedPage)await raced(ownedPage.evaluate(()=>window.gd?.end()),endDisposalMs,'gd.end disposal');}
 finally{await browser.close();takeBrowser=null;page=null;}
}
function collect(dir,out=[]){for(const e of fs.readdirSync(dir,{withFileTypes:true}).sort((a,b)=>a.name.localeCompare(b.name))){const p=path.join(dir,e.name);if(e.isDirectory()&&!['node_modules','.git'].includes(e.name))collect(p,out);else if(e.isFile())out.push(p);}return out;}
function digest(files){const h=crypto.createHash('sha256');for(const p of files)h.update(p).update(fs.readFileSync(p));return h.digest('hex');}
const engineFiles=bundle?bundler.sourceFiles(here,'engine'):null,engineAtStartup=bundle?bundler.sourceDigest(engineFiles):digest(collect(here));
const bundledFingerprint=bundle?.fingerprint({engineDigest:engineAtStartup,contractDigest,browserVersion,playwrightVersion,glRenderer:startupRenderer});
function fingerprint(){
 if(bundle)return bundledFingerprint;
 assertEngineSource(digest(collect(here)),engineAtStartup,Errors);
 const files=[...collect(path.join(gameRoot,'src')),...collect(adapterRoot),...collect(threeRoot)];
 return crypto.createHash('sha256').update(digest(files)).update(engineAtStartup).update(browserVersion).update(playwrightVersion).update(contractDigest).digest('hex');
}
function assertBundleFresh(){if(bundle){const start=performance.now(),changed=bundle.changedInput(engineFiles);process.stderr.write('[GameDirector] bundle stat '+(performance.now()-start).toFixed(3)+' ms; rebuild=false\n');if(changed)throw fail('stateConflict','bundle input changed since startup: '+changed+'; restart the bridge');}}
function manifest(){const m=JSON.parse(fs.readFileSync(path.join(adapterRoot,'manifest.json')));m.capabilities={...m.capabilities,...engineStamps(contract,{sourceFingerprint:fingerprint(),sourceRoot:gameRoot})};return m;}
const json=(res,obj,status=200)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(obj));}; // engine-local: HTTP success and JSON wire policy (planned protocol work)
const errorJson=(res,definition,message)=>json(res,{error:message,code:definition.code},definition.status);
// Static assets use a general MIME table (not an extension whitelist), so
// standard Three content pipelines (glTF, KTX2, wasm) work for any adapter.
const Mime={'.js':'text/javascript','.mjs':'text/javascript','.json':'application/json','.png':'image/png','.jpg':'image/jpeg','.jpeg':'image/jpeg','.webp':'image/webp','.gltf':'model/gltf+json','.glb':'model/gltf-binary','.ktx2':'image/ktx2','.wasm':'application/wasm','.bin':'application/octet-stream'};
function serve(res,base,relative){const full=path.resolve(base,relative);if(!full.startsWith(path.resolve(base)+path.sep)||!fs.existsSync(full)||!fs.statSync(full).isFile()){errorJson(res,Errors.notFound,'not found');return;}
 const type=Mime[path.extname(full).toLowerCase()];if(!type){errorJson(res,Errors.forbidden,'unsupported asset');return;}
 res.writeHead(200,{'Content-Type':type,'Cache-Control':'no-store'});fs.createReadStream(full).pipe(res);} // engine-local: asset HTTP success and cache policy
// Core's inclusive byte ceiling: decode once so split UTF-8 stays intact.
async function body(req){const chunks=[];let bytes=0;
 for await(const c of req){bytes+=c.length;if(bytes>limits.maxRequestBytes)throw fail('payloadTooLarge','request body too large');chunks.push(c);}
 const text=Buffer.concat(chunks).toString('utf8');let data;
 try{data=JSON.parse(text);}catch(e){if(e instanceof SyntaxError)throw fail('invalidRequest',e.message);throw e;}
 if(!data||typeof data!=='object'||Array.isArray(data))throw fail('invalidRequest','request must be a JSON object');return data;}
// "Can this timeline be shot here?" is the compiler's decision, not the
// bridge's: gd compile --take returns the compiled cues plus the admission
// verdict and the frame count. The bridge adds only what only it can know —
// its live source fingerprint — and then executes.
async function admit(data,m){
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'gd-three-'));
 try{
  fs.writeFileSync(path.join(temp,'timeline.json'),JSON.stringify(data.timeline));
  fs.writeFileSync(path.join(temp,'manifest.json'),JSON.stringify(m));
  try{
   const result=await exec('dotnet',[cli,'compile',path.join(temp,'timeline.json'),'--manifest',path.join(temp,'manifest.json'),'--take','--fps',String(data.frameRate),'--width',String(data.width),'--height',String(data.height),'--from-frame',String(data.captureFromFrame??requestDefaults.captureFromFrame)],{timeout:budgets.compileSeconds*1000,maxBuffer:8*1024*1024}); // engine-local: milliseconds conversion and 8 MiB compile output cap
   const envelope=JSON.parse(result.stdout);if(envelope.contract!==contractVersion)throw fail('internal','compiled engine contract changed; restart the bridge before another take');return envelope;
  }catch(e){
   let rejected;try{rejected=JSON.parse(e.stdout);}catch{}
   if(rejected){if(rejected.contract!==contractVersion)throw fail('internal','compiled engine contract changed; restart the bridge before another take');const errors=rejected.admission?.errors;if(errors?.length)throw fail('invalidRequest',errors.join('; '));}
   throw e; // no admission verdict: subprocess/tooling failures belong to the engine
  }
 }finally{fs.rmSync(temp,{recursive:true,force:true});}
}
async function mutate(route,data,res,baseURL){
 if(shuttingDown)throw fail('busy','bridge is shutting down');
 if(route==='/'+R.takeStart){
  if(take?.state===States.capturing)throw fail('stateConflict','take already active');
  if(!data.timeline||typeof data.timeline!=='object'||Array.isArray(data.timeline)||![data.frameRate,data.width,data.height,data.captureFromFrame===undefined?requestDefaults.captureFromFrame:data.captureFromFrame].every(int32))throw fail('invalidRequest','take timeline and Int32 geometry/capture start are required');
  assertBundleFresh();const m=manifest();
  if(data.sourceFingerprint && data.sourceFingerprint!==m.capabilities['presentation.sourceFingerprint'])throw fail('stateConflict','source fingerprint changed');
  compiled=await admit(data,m);
  if(shuttingDown)throw fail('busy','bridge is shutting down');
  const takeId=crypto.randomUUID().replaceAll('-',''),loadDeadline=performance.now()+budgets.pageLoadSeconds*1000; // engine-local: seconds to milliseconds; launch and navigation share one load budget
  try{
   await openTakeBrowser(takeId,loadDeadline);
   await page.goto(baseURL+'/'+R.stage+(bundle?.stageQuery?'?'+bundle.stageQuery:''),{timeout:Math.max(1,loadDeadline-performance.now())});
   const readyDeadline=performance.now()+budgets.stageReadySeconds*1000,readyLeft=()=>Math.max(1,readyDeadline-performance.now()); // engine-local: seconds to milliseconds; readiness, begin and GL read share one budget
   await page.waitForFunction(()=>window.gdReady===true,null,{timeout:readyLeft()});
   const facts=await raced(page.evaluate(({c,w,h})=>window.gd.begin(c,w,h),{c:compiled,w:data.width,h:data.height}),readyLeft(),'stage begin');
   const renderer=await raced(page.evaluate(readGlRenderer,true),readyLeft(),'take GL renderer');
   if(renderer!==startupRenderer)throw fail('internal','take GL renderer '+renderer+' differs from startup '+startupRenderer+' (declared backend '+gl+'); restart the bridge before another take');
   process.stderr.write(`[GameDirector] take ${takeId} gl=${renderer}\n`);
   process.stderr.write(`[GameDirector] presenter post=${facts.post?'on':'off'} size=${facts.width}×${facts.height} rtNull=${facts.rtNull} shadowAuto=${facts.shadowAuto} exposure=${facts.exposure} pixelRatio=${facts.pixelRatio} canvas=${facts.canvasWidth}×${facts.canvasHeight} gl=${renderer}\n`);
   assertBundleFresh();if(fingerprint()!==m.capabilities['presentation.sourceFingerprint'])throw fail('stateConflict','source changed while preparing stage');
   if(shuttingDown)throw fail('busy','bridge is shutting down');
   take={takeId,timelineId:compiled.id,state:States.capturing,frameRate:data.frameRate,width:data.width,height:data.height,frameCount:compiled.frameCount,captureFromFrame:data.captureFromFrame??requestDefaults.captureFromFrame,capturedFrames:0,reviews:[],measures:[],audio:contract.take.silentPictureAudio};lastFrame=null;nextFrame=0;lastRequest=Date.now();json(res,take);return;
  }catch(e){await closeTakeBrowser({end:false}).catch(()=>{});throw e;} // a failed start discards the process: close directly, no renderer end()
 }
 if(route==='/'+R.takeFrame){
  if(!int32(data.frameIndex)||(data.advanceOnly!==undefined&&typeof data.advanceOnly!=='boolean')||(data.measure!==undefined&&typeof data.measure!=='boolean'))throw fail('invalidRequest','frame index must be an Int32; advanceOnly and measure must be booleans');
  const measure=data.measure??requestDefaults.measure;
  if(measure&&(data.advanceOnly??requestDefaults.advanceOnly))throw fail('invalidRequest','measure is valid only on rendered frames');
  if(!take || data.takeId!==take.takeId)throw fail('stateConflict','take identity mismatch');
  lastRequest=Date.now();
  const advance=data.advanceOnly??requestDefaults.advanceOnly;
  if(data.frameIndex<0||data.frameIndex>=take.frameCount||advance!==(data.frameIndex<take.captureFromFrame))throw fail('stateConflict','frame mode/index mismatch');
  if(nextFrame===0 || data.frameIndex!==nextFrame-1){if(take.state!==States.capturing||data.frameIndex!==nextFrame)throw fail('stateConflict','frame index mismatch');
   try {
    if(advance){await page.evaluate(({i,f})=>window.gd.advance(i,f),{i:data.frameIndex,f:take.frameRate});lastFrame=null;}
    else {const r=await page.evaluate(({i,f,m})=>window.gd.frame(i,f,m),{i:data.frameIndex,f:take.frameRate,m:measure});lastFrame=Buffer.from(r.png,'base64');take.capturedFrames++;if(r.review)take.reviews.push(r.review);if(r.measures)take.measures.push(r.measures);}
    nextFrame++;
    if(nextFrame===take.frameCount){take.events=await page.evaluate(()=>window.gd.finish());take.state=States.completed;await closeTakeBrowser().catch(e=>process.stderr.write('take cleanup: '+e.message+'\n'));} // capture finished: disposal errors are logged, never a take failure or a lost final frame
   } catch(e) {take.state=States.failed;await closeTakeBrowser().catch(()=>{});throw e;}
  }
  if(advance){res.writeHead(204);res.end();}else {res.writeHead(200,{'Content-Type':'image/png'});res.end(lastFrame);}return; // engine-local: replay-only empty response and PNG success (planned protocol work)
 }
 if(route==='/'+R.takeStop){await stopTake(data);json(res,{ok:true});return;}
 if(take?.state===States.capturing)throw fail('busy','bridge busy capturing a take');
 throw fail('notFound','unsupported route');
}
// Core TakeStopRequest, deliberately not a FrameRequest (no frame/mode fields).
async function stopTake({takeId}){
 if(!take||takeId!==take.takeId)throw fail('stateConflict','take identity mismatch');
 lastRequest=Date.now();if(take.state===States.capturing)take.state=States.cancelled;await closeTakeBrowser();
}
const server=http.createServer(async(req,res)=>{const url=new URL(req.url,'http://'+contract.bridge.loopbackHost);
 try{
  // Loopback admission, same rules as the C# bridges: an exact Host match
  // defeats DNS rebinding (a foreign page reading game source through GETs),
  // browser origins may not mutate, and POSTs are bounded application/json.
  const port=listenPort;
  if(req.headers.host!==`${contract.bridge.loopbackHost}:${port}`&&req.headers.host!==`${contract.bridge.loopbackAlias}:${port}`){errorJson(res,Errors.forbidden,'Host header must be the loopback bridge address');return;}
  const fetchSite=req.headers['sec-fetch-site'];
  if(fetchSite!==undefined&&fetchSite!=='none'&&fetchSite!=='same-origin'){errorJson(res,Errors.forbidden,'cross-site bridge requests rejected');return;}
  if(req.headers.origin && req.method!=='GET'){errorJson(res,Errors.forbidden,'browser-origin bridge requests rejected');return;}
  if(req.method==='GET'){
   if(url.pathname==='/'+R.health){json(res,{ok:true,engine:EngineId,...(bundle?{bundleBuildCount:bundler.buildCount()}:{})});return;}
   if(url.pathname==='/'+R.manifest){json(res,manifest());return;}
   if(url.pathname==='/'+R.contract){res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(contractStdout);return;} // engine-local: HTTP success and JSON wire policy
   if(url.pathname==='/'+R.take||url.pathname==='/'+R.status){json(res,{take,events:take?.events||[],reviews:take?.reviews||[],measures:take?.measures||[]});return;}
   if(url.pathname==='/'+R.stage){res.writeHead(200,{'Content-Type':'text/html','Cache-Control':'no-store'});res.end(`<!doctype html><meta charset="utf-8"><title>GameDirector offline stage</title><style>body{margin:0;background:#000}canvas{display:block}</style><script type="importmap">{"imports":{"three":"/vendor/build/three.module.js","three/addons/":"/vendor/examples/jsm/"${bundle?',"three/examples/jsm/":"/vendor/examples/jsm/"':''}}}</script><script type="module">import {installEnvelope} from '/engine/envelope.mjs';installEnvelope();</script><script type="module">import {ThreeDirector} from '/engine/runtime.js';import adapter from '/adapter/adapter.js';const m=await fetch('/${R.manifest}').then(r=>r.json());const contract=await fetch('/${R.contract}').then(r=>r.json());window.gd=new ThreeDirector(adapter,m,contract${bundle?',{options:'+JSON.stringify(bundle.options).replaceAll('<','\\u003c')+'}':''});window.gdReady=true;</script>`);return;} // engine-local: stage HTML, renderer background and module mounts
   if(bundle&&url.pathname==='/adapter/adapter.js'){res.writeHead(200,{'Content-Type':'text/javascript','Cache-Control':'no-store'});res.end(bundle.code);return;} // engine-local: in-memory ESM asset success and cache policy
   for(const [prefix,base] of [['/game/src/',path.join(gameRoot,'src')],['/vendor/',threeRoot],['/engine/',here],['/adapter/',adapterRoot]])if((!bundle||prefix!=='/game/src/')&&url.pathname.startsWith(prefix)){serve(res,base,decodeURIComponent(url.pathname.slice(prefix.length)));return;}
   errorJson(res,Errors.notFound,'not found');return;
  }
  if(req.method!=='POST'){errorJson(res,Errors.methodNotAllowed,'method not allowed');return;}
  if((req.headers['content-type']||'').split(';')[0].trim().toLowerCase()!=='application/json'){errorJson(res,Errors.unsupportedMediaType,'application/json required');return;} // engine-local: media-type parsing and absent-header sentinel
  if(pending>=limits.maxQueuedRequests){errorJson(res,Errors.busy,'queue full');return;}pending++;
  try{const data=await body(req);if(queueBusy||pending>1)process.stderr.write('[GameDirector] request queued behind active work\n');const base=`http://${contract.bridge.loopbackHost}:${listenPort}`;const job=queue.then(async()=>{queueBusy=true;try{await mutate(url.pathname,data,res,base);}finally{queueBusy=false;}});queue=job.catch(()=>{});await job;}finally{pending--;}
 }catch(e){process.stderr.write(url.pathname+': '+(e.stack||e.message)+'\n');const error=classifyError(e,Errors);if(!res.headersSent)json(res,error.body,error.status);else res.destroy();}
});
await new Promise(resolve=>server.listen(Number(option('--port',contract.bridge.ports.three)),contract.bridge.loopbackHost,resolve));
const listenPort=server.address().port; // captured once: server.address() turns null as soon as close() begins
// An abandoned take must return the stage on its own; the idle lease length
// comes from the Core contract. The lease renews only on requests that pass
// take-identity, so a mismatched takeId can never keep a take alive.
const lease=setInterval(()=>{if(!shuttingDown&&take?.state===States.capturing&&Date.now()-lastRequest>contract.capture.idleLeaseSeconds*1000&&pending===0){ // engine-local: seconds to milliseconds
 const job=queue.then(async()=>{queueBusy=true;try{if(take?.state===States.capturing&&Date.now()-lastRequest>contract.capture.idleLeaseSeconds*1000){take.state=States.cancelled;await closeTakeBrowser();}}finally{queueBusy=false;}}); // engine-local: seconds to milliseconds
 queue=job.catch(e=>process.stderr.write('lease cleanup: '+e.message+'\n'));
}},1000);lease.unref(); // engine-local: 1000 ms lease polling
console.log(JSON.stringify({endpoint:`http://${contract.bridge.loopbackHost}:${listenPort}`,game:manifest().game,scope:'offline visual assets; no live game/provider calls'}));
async function shutdown(){
 if(shutdownPromise)return shutdownPromise;shuttingDown=true;clearInterval(lease);process.stderr.write('bridge shutdown: draining takes\n');
 // The listener stays open until the queue drains: an in-flight start's stage and
 // module fetches must still be served. New POSTs are refused by the shuttingDown guards.
 shutdownPromise=(async()=>{await queue;if(take?.state===States.capturing)take.state=States.cancelled;await closeTakeBrowser();server.close();})();
 try{await shutdownPromise;}catch(e){process.stderr.write('shutdown cleanup: '+e.message+'\n');if(takeBrowser){shutdownPromise=null;return;}}
 process.exit(0);
}process.on('SIGINT',shutdown);process.on('SIGTERM',shutdown);
