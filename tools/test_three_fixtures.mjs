// Live deterministic-hook and presenter gates; one Chrome bridge at a time. Retain only compact reports/logs.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {spawn,execFile} from 'node:child_process';
import {once} from 'node:events';
import {promisify} from 'node:util';
const exec=promisify(execFile),args=process.argv.slice(2),get=n=>args[args.indexOf(n)+1],variant=get('--variant'),out=path.resolve(get('--out')),port=args.includes('--port')?get('--port'):'39880',endpoint='http://127.0.0.1:'+port;
assert.ok(['rand','look','walk','swiftshader'].includes(variant),'--variant rand|look|walk|swiftshader --out <new directory>');fs.mkdirSync(out,{recursive:true});
const probe=args.includes('--probe')?get('--probe'):null,dirs=[],gd='src/GameDirector.Cli/bin/Debug/net8.0/gd.dll',env={...process.env,GAMEDIRECTOR_BROWSER:'none'};let stage='tools/fixtures/three-ts-adapter/'+(variant==='swiftshader'?'look':variant);
assert.ok(!probe||(variant==='look'&&probe==='resize'),'--probe resize requires --variant look');assert.ok(fs.existsSync(stage+'/three.json'),'fixture stage missing: '+stage);
if(probe||variant==='swiftshader'){const family=path.join(out,'probe-family');dirs.push(family);fs.cpSync('tools/fixtures/three-ts-adapter',family,{recursive:true});stage=path.join(family,variant==='swiftshader'?'look':variant);const file=path.join(stage,'three.json'),config=JSON.parse(fs.readFileSync(file));if(probe)config.entry='resize.ts';else config.gl='swiftshader';fs.writeFileSync(file,JSON.stringify(config)+'\n');}
const timeline=stage+'/timeline.json';
const serverArgs=['packages/gamedirector-three/server.mjs','--game-root','tools/fixtures/three-ts-game','--adapter',stage,'--three-root','packages/gamedirector-three/node_modules/three','--port',port,'--browser','chrome'];console.log('RUN node '+serverArgs.join(' '));
const child=spawn(process.execPath,serverArgs,{env});let stdout='',stderr='';child.stdout.on('data',s=>stdout+=s);child.stderr.on('data',s=>stderr+=s);let result;
async function command(cmd,argv){console.log('RUN '+cmd+' '+argv.join(' '));const r=await exec(cmd,argv,{env,maxBuffer:8*1024*1024});if(r.stdout)console.log(r.stdout.trim());return r;}
const post=(name,data)=>fetch(endpoint+'/take/'+name,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});
const rows=name=>fs.readFileSync(path.join(out,name,'frames.jsonl'),'utf8').trim().split('\n').map(JSON.parse).map(r=>r.sha256);
async function take(name,from){const directory=path.join(out,name);dirs.push(directory);await command('dotnet',[gd,'take',timeline,'--endpoint',endpoint,'--out',directory,'--fps','6','--width','320','--height','180','--keep-frames',...(from===undefined?[]:['--from-frame',String(from)])]);return rows(name);}
try{
 await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('fixture startup timeout: '+stderr)),30000);const done=fn=>{clearTimeout(timer);fn();};child.once('error',e=>done(()=>reject(e)));child.once('exit',c=>done(()=>reject(Error('fixture exited '+c+': '+stderr))));child.stdout.on('data',()=>{if(stdout.includes('"endpoint"'))done(resolve);});});
 if(probe){
  const started=await post('start',{timeline:JSON.parse(fs.readFileSync(timeline)),frameRate:6,width:320,height:180});let response=started,when='start';
  if(started.status===200){const take=await started.json();when='frame';response=await post('frame',{takeId:take.takeId,frameIndex:0});}
  assert.equal(response.status,500,'resize probe wrongly succeeded: HTTP '+response.status);const body=await response.json();assert.match(body.error,/presenter size mismatch/);assert.match(body.error,/1280×720/);assert.match(body.error,/320×180/);result={variant,probe,when,status:response.status,error:body.error};
 }else if(args.includes('--expect-error')){
  const when=get('--expect-error'),request={timeline:JSON.parse(fs.readFileSync(timeline)),frameRate:6,width:320,height:180};const started=await post('start',request);let response=started;
  if(when==='frame'){assert.equal(started.status,200,await started.clone().text());const take=await started.json();response=await post('frame',{takeId:take.takeId,frameIndex:0});}
  const body=await response.json();assert.equal(response.status,500,JSON.stringify(body));assert.match(body.error,when==='frame'?/render target.*null/:/setAnimationLoop/);result={expectedError:when,status:response.status,error:body.error};
 }else{
  const full=await take('full');result={variant,frames:full.length,firstHash:full[0]};
  if(variant==='rand'){
   const tail=await take('tail',7),counts=[...stderr.matchAll(/fixture page-stream frame=7 draws=(\d+)/g)].map(m=>Number(m[1]));assert.equal(counts.length,2);assert.notEqual(counts[0],counts[1],'render-time stream proof is blind');result.pageDrawCounts=counts;result.tail=tail.length;assert.deepEqual(tail,full.slice(7),'from-frame tail hashes differ');
   const repeat=path.join(out,'rep');dirs.push(repeat);await command('python3',['tools/verify_repeat.py',timeline,'--out',repeat,'--endpoint',endpoint,'--fps','6','--width','320','--height','180']);result.repeat=JSON.parse(fs.readFileSync(path.join(repeat,'repeat.json')));delete result.repeat.frames;
  }
  if(variant==='swiftshader'){
   const repeat=await take('repeat');assert.deepEqual(repeat,full,'SwiftShader PNG bytes differ across fresh-process takes');assert.deepEqual(fs.readFileSync(path.join(out,'repeat/frames.jsonl')),fs.readFileSync(path.join(out,'full/frames.jsonl')),'SwiftShader frame receipts differ');
   const renderers=[...stderr.matchAll(/presenter .* gl=(.*)/g)].map(m=>m[1]);assert.equal(renderers.length,2);for(const renderer of renderers)assert.match(renderer,/SwiftShader/,'declared flags did not select the page renderer');assert.equal(renderers[0],renderers[1]);result.renderer=renderers[0];result.repeatEqual=true;
  }
  if(variant==='walk'){
   const report=path.join(out,'fps');await command('node',['tools/test_fps_agreement.mjs','--endpoint',endpoint,'--timeline',timeline,'--fps','6','--fps','24','--width','320','--height','180','--out',report]);result.fps=JSON.parse(fs.readFileSync(path.join(report,'result.json')));delete result.fps.metrics;
  }
  assert.match(stderr,new RegExp('presenter post='+(['look','swiftshader'].includes(variant)?'on':'off')+' size=320×180 rtNull=true shadowAuto=true exposure='));assert.doesNotMatch(stderr,/render error:/);assert.match(stderr,/\[stage warning\] fixture device pins dpr=1 touch=0 reduced=false/);assert.match(stderr,/\[stage error\] fixture console forwarding/);assert.match(stderr,/pixelRatio=1 canvas=320×180/);
 }
 console.log(JSON.stringify(result));
}finally{
 if(child.exitCode===null&&child.signalCode===null){const exited=once(child,'exit');child.kill('SIGTERM');await exited;}fs.writeFileSync(path.join(out,'bridge.log'),stdout+stderr);if(result)fs.writeFileSync(path.join(out,'result.json'),JSON.stringify(result,null,2)+'\n');for(const directory of dirs)fs.rmSync(directory,{recursive:true,force:true});
}
