import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
const exec=promisify(execFile),here=path.dirname(fileURLToPath(import.meta.url));
const option=(name,fallback)=>{const i=process.argv.indexOf(name);return i<0?fallback:process.argv[i+1];};
const gameRoot=path.resolve(option('--game-root','')),adapterRoot=path.resolve(option('--adapter',''));
if(!option('--game-root')||!option('--adapter'))throw Error('Required: --game-root <source checkout> --adapter <adapter directory>');
const cli=path.resolve(option('--cli',path.join(here,'../../src/GameDirector.Cli/bin/Debug/net8.0/gd.dll')));
const threeRoot=path.resolve(option('--three-root',path.join(gameRoot,'node_modules/three')));
const require=createRequire(process.env.GAMEDIRECTOR_NODE_MODULES?path.join(process.env.GAMEDIRECTOR_NODE_MODULES,'package.json'):import.meta.url);
const {chromium}=require('playwright');
const browser=await chromium.launch({headless:true,channel:option('--browser',undefined),args:['--disable-background-timer-throttling']});
const page=await browser.newPage();page.on('pageerror',e=>process.stderr.write('render error: '+e.message+'\n'));
let take=null,lastFrame=null,compiled=null,queue=Promise.resolve(),pending=0,lastRequest=0;
function collect(dir,out=[]){for(const e of fs.readdirSync(dir,{withFileTypes:true}).sort((a,b)=>a.name.localeCompare(b.name))){const p=path.join(dir,e.name);if(e.isDirectory()&&!['node_modules','.git'].includes(e.name))collect(p,out);else if(e.isFile())out.push(p);}return out;}
function digest(files){const h=crypto.createHash('sha256');for(const p of files)h.update(p).update(fs.readFileSync(p));return h.digest('hex');}
const engineAtStartup=digest(collect(here));
function fingerprint(){
 if(digest(collect(here))!==engineAtStartup)throw Error('engine source changed; restart the bridge before another take');
 const files=[...collect(path.join(gameRoot,'src')),...collect(adapterRoot),...collect(threeRoot)];
 return crypto.createHash('sha256').update(digest(files)).update(engineAtStartup).update(browser.version()).digest('hex');
}
function manifest(){const m=JSON.parse(fs.readFileSync(path.join(adapterRoot,'manifest.json')));m.capabilities={...m.capabilities,'director.mode':'offline-sandbox','presentation.sourceFingerprint':fingerprint(),'engine':'three','project.sourceRoot':gameRoot,'capture.audio':'post-production'};return m;}
const json=(res,obj,status=200)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(obj));};
function serve(res,base,relative){const full=path.resolve(base,relative);if(!full.startsWith(path.resolve(base)+path.sep)||!fs.existsSync(full)||!fs.statSync(full).isFile()){json(res,{error:'not found'},404);return;}
 if(!/\.(js|mjs|json|png|jpg|webp)$/.test(full)){json(res,{error:'unsupported asset'},403);return;}
 res.writeHead(200,{'Content-Type':/\.(js|mjs)$/.test(full)?'text/javascript':full.endsWith('.json')?'application/json':'application/octet-stream','Cache-Control':'no-store'});fs.createReadStream(full).pipe(res);}
async function body(req){let data='';for await(const c of req){data+=c;if(data.length>2*1024*1024)throw Error('request too large');}return JSON.parse(data||'{}');}
function preflight(request,m){if(!Number.isInteger(request.frameRate)||request.frameRate<1||request.frameRate>60||!Number.isInteger(request.width)||request.width<64||request.width>3840||request.width%2||!Number.isInteger(request.height)||request.height<64||request.height>2160||request.height%2)throw Error('invalid take dimensions/rate');
 if(request.sourceFingerprint && request.sourceFingerprint!==m.capabilities['presentation.sourceFingerprint'])throw Error('source fingerprint changed');
 if(!request.timeline?.cues?.some(c=>c.type==='camera.shot'&&c.t===0))throw Error('camera shot at t=0 required');
 for(const c of request.timeline.cues){if(c.type==='actor.spawn' && m.capabilities?.['actor.spawn']!=='supported')throw Error('adapter does not declare spawn support');if(c.type.startsWith('audio.'))throw Error('picture capture requires post-production audio');if(c.type==='actor.anim' && (c.fade??.2)>0)throw Error('this procedural adapter supports cut-only animation; set fade:0');if(c.shot?.params && Object.keys(c.shot.params).some(k=>!['targetHeight','distance','offsetX','offsetY','offsetZ','toOffsetX','toOffsetY','toOffsetZ','orbitDeg','tilt','pan','roll'].includes(k)))throw Error('unsupported Three camera parameter');}}
async function mutate(route,data,res,baseURL){lastRequest=Date.now();
 if(route==='/take/start'){
  if(take?.state==='Capturing')throw Error('take already active');const m=manifest();preflight(data,m);
  const temp=fs.mkdtempSync(path.join(os.tmpdir(),'gd-three-'));
  try{fs.writeFileSync(path.join(temp,'timeline.json'),JSON.stringify(data.timeline));fs.writeFileSync(path.join(temp,'manifest.json'),JSON.stringify(m));
   const result=await exec('dotnet',[cli,'compile',path.join(temp,'timeline.json'),'--manifest',path.join(temp,'manifest.json')],{timeout:30000,maxBuffer:8*1024*1024});compiled=JSON.parse(result.stdout);
  }finally{fs.rmSync(temp,{recursive:true,force:true});}
  if(compiled.duration<=0||compiled.duration>600)throw Error('duration must be >0 and <=600 seconds');
  await page.goto(baseURL+'/stage');await page.waitForFunction(()=>window.gdReady===true,null,{timeout:30000});
  await page.evaluate(({c,w,h})=>window.gd.begin(c,w,h),{c:compiled,w:data.width,h:data.height});
  if(fingerprint()!==m.capabilities['presentation.sourceFingerprint'])throw Error('source changed while preparing stage');
  take={takeId:crypto.randomUUID().replaceAll('-',''),timelineId:data.timeline.id,state:'Capturing',frameRate:data.frameRate,width:data.width,height:data.height,frameCount:Math.ceil(compiled.duration*data.frameRate-1e-8),capturedFrames:0,audio:'silent-picture; mix audio in the edit'};lastFrame=null;json(res,take);return;
 }
 if(route==='/take/frame'){
  if(!take || data.takeId!==take.takeId)throw Error('take identity mismatch');
  if(data.frameIndex!==take.capturedFrames-1 || !lastFrame){if(take.state!=='Capturing'||data.frameIndex!==take.capturedFrames)throw Error('frame index mismatch');
   try { const png=await page.evaluate(({i,f})=>window.gd.frame(i,f),{i:data.frameIndex,f:take.frameRate});lastFrame=Buffer.from(png,'base64');take.capturedFrames++;
   if(take.capturedFrames===take.frameCount){take.events=await page.evaluate(()=>window.gd.finish());take.state='Completed';}
   } catch(e) {take.state='Failed';await page.evaluate(()=>window.gd?.end()).catch(()=>{});throw e;}
  }res.writeHead(200,{'Content-Type':'image/png'});res.end(lastFrame);return;
 }
 if(route==='/take/stop'){if(!take||data.takeId!==take.takeId)throw Error('take identity mismatch');if(take.state==='Capturing')take.state='Cancelled';await page.evaluate(()=>window.gd?.end());json(res,{ok:true});return;}
 throw Error('unsupported route');
}
const server=http.createServer(async(req,res)=>{const url=new URL(req.url,'http://127.0.0.1');
 try{
  if(req.headers.origin && req.method!=='GET'){json(res,{error:'browser-origin bridge requests rejected'},403);return;}
  if(req.method==='GET'){
   if(url.pathname==='/health'){json(res,{ok:true,engine:'three'});return;}
   if(url.pathname==='/manifest'){json(res,manifest());return;}
   if(url.pathname==='/take'||url.pathname==='/status'){json(res,{take,events:take?.events||[]});return;}
   if(url.pathname==='/stage'){res.writeHead(200,{'Content-Type':'text/html','Cache-Control':'no-store'});res.end(`<!doctype html><meta charset="utf-8"><title>GameDirector offline stage</title><style>body{margin:0;background:#000}canvas{display:block}</style><script type="importmap">{"imports":{"three":"/vendor/build/three.module.js","three/addons/":"/vendor/examples/jsm/"}}</script><script type="module">import {ThreeDirector} from '/engine/runtime.js';import adapter from '/adapter/adapter.js';const m=await fetch('/manifest').then(r=>r.json());window.gd=new ThreeDirector(adapter,m);window.gdReady=true;</script>`);return;}
   for(const [prefix,base] of [['/game/src/',path.join(gameRoot,'src')],['/vendor/',threeRoot],['/engine/',here],['/adapter/',adapterRoot]])if(url.pathname.startsWith(prefix)){serve(res,base,decodeURIComponent(url.pathname.slice(prefix.length)));return;}
   json(res,{error:'not found'},404);return;
  }
  if(req.method!=='POST'){json(res,{error:'method not allowed'},405);return;}
  if(pending>=8){json(res,{error:'queue full'},503);return;}pending++;
  try{const data=await body(req);const job=queue.then(()=>mutate(url.pathname,data,res,`http://127.0.0.1:${server.address().port}`));queue=job.catch(()=>{});await job;}finally{pending--;}
 }catch(e){process.stderr.write(url.pathname+': '+e.message+'\n');if(!res.headersSent)json(res,{error:e.message},400);else res.destroy();}
});
await new Promise(resolve=>server.listen(Number(option('--port','39778')),'127.0.0.1',resolve));
const lease=setInterval(()=>{if(take?.state==='Capturing'&&Date.now()-lastRequest>60000&&pending===0){take.state='Cancelled';page.evaluate(()=>window.gd?.end()).catch(()=>{});}},1000);lease.unref();
console.log(JSON.stringify({endpoint:`http://127.0.0.1:${server.address().port}`,game:manifest().game,scope:'offline visual assets; no live game/provider calls'}));
async function shutdown(){clearInterval(lease);server.close();await browser.close();process.exit(0);}process.on('SIGINT',shutdown);process.on('SIGTERM',shutdown);
