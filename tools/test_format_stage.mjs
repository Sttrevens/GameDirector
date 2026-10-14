// G-F live: Core-generated box samples, a real portrait WebGL canvas and gd review.
// One owned Chrome bridge; all media/takes deleted after hashes and verdicts are recorded.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {spawn,execFile,execFileSync,spawnSync} from 'node:child_process';
import {once} from 'node:events';
import {promisify} from 'node:util';
const exec=promisify(execFile),args=process.argv.slice(2),get=n=>args[args.indexOf(n)+1];assert.ok(args.includes('--out'),'--out <new report directory>');
const out=path.resolve(get('--out')),port=args.includes('--port')?get('--port'):'39880',endpoint='http://127.0.0.1:'+port,gd='src/GameDirector.Cli/bin/Debug/net8.0/gd.dll',env={...process.env,GAMEDIRECTOR_BROWSER:'none'};
fs.mkdirSync(out,{recursive:true});const contract=JSON.parse(execFileSync('dotnet',[gd,'contract','--json'],{env,encoding:'utf8'})),dirs=[],results=[];
const argv=['packages/gamedirector-three/server.mjs','--game-root','tools/fixtures/three-ts-game','--adapter','tools/fixtures/format-stage','--three-root','packages/gamedirector-three/node_modules/three','--port',port,'--browser','chrome'];console.log('RUN node '+argv.join(' '));
const child=spawn(process.execPath,argv,{env});let stdout='',stderr='';child.stdout.on('data',s=>stdout+=s);child.stderr.on('data',s=>stderr+=s);
function review(directory){const args=[gd,'review',directory,'--safe-area','tiktok'];console.log('RUN dotnet '+args.join(' '));const r=spawnSync('dotnet',args,{env,encoding:'utf8'});assert.ok([0,1].includes(r.status),r.stderr);const value=JSON.parse(r.stdout);assert.equal(value.judged,true,r.stderr);return {exit:r.status,...value};}
async function take(name,timeline,width,height){const file=path.join(out,name+'.json'),directory=path.join(out,name);fs.writeFileSync(file,JSON.stringify(timeline));dirs.push(directory);const args=[gd,'take',file,'--endpoint',endpoint,'--out',directory,'--fps','1','--width',String(width),'--height',String(height),'--keep-frames'];console.log('RUN dotnet '+args.join(' '));await exec('dotnet',args,{env,maxBuffer:8*1024*1024});return directory;}
const shot=params=>({type:'lockoff',subject:'hero',from:'from',frame:'full',durationSeconds:1,ease:'linear',params});
try{
 await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('startup timeout: '+stderr)),30000);child.once('exit',c=>{clearTimeout(timer);reject(Error('bridge exited '+c+': '+stderr));});child.stdout.on('data',()=>{if(stdout.includes('"endpoint"')){clearTimeout(timer);resolve();}});});
 console.log('RUN python3 tools/test_camera_conformance.py --endpoint '+endpoint);const camera=await exec('python3',['tools/test_camera_conformance.py','--endpoint',endpoint],{env,maxBuffer:8*1024*1024});fs.writeFileSync(path.join(out,'camera.json'),camera.stdout);const conformance=JSON.parse(camera.stdout);assert.equal(conformance.passed,true);assert.equal(conformance.samples.length,14);
 for(const [width,height] of [[640,360],[360,640],[1080,1920],[960,540],[540,960]]){
  const aspect=width/height,rows=contract.review.roles.samples.slice(0,6).filter(s=>Math.abs(s.aspect-aspect)<1e-8),timeline={version:'0.1',id:'format-samples',cues:rows.map((s,i)=>({t:i,type:'camera.shot',shot:{...shot({pan:s.pan,tilt:s.tilt,roll:s.roll}),fov:s.fov}}))};
  const directory=await take('samples-'+width+'x'+height,timeline,width,height),receipt=JSON.parse(fs.readFileSync(path.join(directory,'receipt.json')));assert.equal(receipt.reviews.length,3);
  const projections=[];for(const [i,r] of receipt.reviews.entries()){
   const actual=r.composition.roles.find(r=>r.role==='hero'),expected=rows[i];assert.equal(actual.state,expected.state);let delta=0;
   for(const key of ['rect','crown']){assert.equal(actual[key].length,expected[key].length);for(let j=0;j<actual[key].length;j++){const error=Math.abs(actual[key][j]-expected[key][j]);assert.ok(error<=expected.liveTolerance,`${width}x${height} row ${i} ${key}[${j}] delta ${error}`);delta=Math.max(delta,error);}}
   projections.push({row:i,actual,expected,maxDelta:delta});
  }
  let bottom;
  if(height===1920){const frame=path.join(directory,'frames/000002.png'),ffmpeg=process.env.GAMEDIRECTOR_FFMPEG||'ffmpeg';const {stdout:rgba}=await exec(ffmpeg,['-v','error','-i',frame,'-f','rawvideo','-pix_fmt','rgba','-'],{env,encoding:'buffer',maxBuffer:16*1024*1024});assert.equal(rgba.length,width*height*4);bottom=[...rgba.subarray((height-1)*width*4,(height-1)*width*4+4)];assert.equal(bottom[3],255);assert.ok(bottom.slice(0,3).every(v=>v>10),'portrait bottom row not rendered');}
  const verdict=review(directory);assert.equal(verdict.review.errors,0,JSON.stringify(verdict));assert.equal(verdict.exit,0);
  results.push({width,height,projections,bottom,verdict,hashes:fs.readFileSync(path.join(directory,'frames.jsonl'),'utf8').trim().split('\n').map(JSON.parse)});fs.rmSync(directory,{recursive:true,force:true});
  const pair={version:'0.1',id:'format-pair',cues:[{t:0,type:'camera.shot',shot:{...shot(),with:['partner','skin','sprite']}}]},pairDir=await take('pair-'+width+'x'+height,pair,width,height),pairVerdict=review(pairDir);
  // The instanced partner and sprite are laterally clipped in portrait only.
  const want=aspect<1?2:0;assert.equal(pairVerdict.review.errors,want,JSON.stringify(pairVerdict));assert.equal(pairVerdict.exit,want?1:0);
  results.push({width,height,pair:true,verdict:pairVerdict,receipt:JSON.parse(fs.readFileSync(path.join(pairDir,'receipt.json'))),hashes:fs.readFileSync(path.join(pairDir,'frames.jsonl'),'utf8').trim().split('\n').map(JSON.parse)});fs.rmSync(pairDir,{recursive:true,force:true});
 }
 assert.doesNotMatch(stderr,/render error:/);fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,results},null,2)+'\n');console.log(JSON.stringify({passed:true,sizes:5,cameraRows:14,referenceReviews:15,pairVerdicts:5,bottom:results.find(r=>r.bottom)?.bottom}));
}finally{if(child.exitCode===null&&child.signalCode===null){const exited=once(child,'exit');child.kill('SIGTERM');await exited;}fs.writeFileSync(path.join(out,'bridge.log'),stdout+stderr);for(const directory of dirs)fs.rmSync(directory,{recursive:true,force:true});}
