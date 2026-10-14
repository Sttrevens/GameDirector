// Capture two fresh takes; compare every shared timestamp, including arrivals.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {pictureDiff} from './picture_diff.mjs';
const exec=promisify(execFile),repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
export const passesAgreement=m=>m.share<=.005&&m.mean<=.5;
export function sharedFrames(countA,countB,fpsA,fpsB){
 if(![fpsA,fpsB].every(n=>Number.isSafeInteger(n)&&n>0))throw Error('fps must be positive integers');const shared=[];
 for(let a=0;a<countA;a++)if(a*fpsB%fpsA===0){const b=a*fpsB/fpsA;if(b>=countB)throw Error('second take missing shared timestamp '+a/fpsA);shared.push({a,b,time:a/fpsA});}return shared;
}
export async function fpsAgreement({endpoint,timeline,fps,width,height,out,gd=path.join(repo,'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll')}){
 const root=out?path.resolve(out):fs.mkdtempSync(path.join(os.tmpdir(),'gd-fps-agreement-'));if(out)fs.mkdirSync(root,{recursive:true});const takes=fps.map((f,i)=>path.join(root,'take-'+i+'-'+f));
 try{
  for(const [i,f] of fps.entries()){const args=[gd,'take',path.resolve(timeline),'--endpoint',endpoint,'--out',takes[i],'--fps',String(f),'--width',String(width),'--height',String(height),'--keep-frames'];console.error('RUN dotnet '+args.join(' '));await exec('dotnet',args,{env:{...process.env,GAMEDIRECTOR_BROWSER:'none'},maxBuffer:8*1024*1024});}
  const frames=takes.map(t=>fs.readFileSync(path.join(t,'frames.jsonl'),'utf8').trim().split('\n').map(JSON.parse)),receipts=takes.map(t=>JSON.parse(fs.readFileSync(path.join(t,'receipt.json'),'utf8')));
  for(let i=0;i<2;i++)if(!frames[i].length||frames[i].length!==receipts[i].take?.frameCount)throw Error('incomplete take '+fps[i]);
  const pairs=sharedFrames(frames[0].length,frames[1].length,...fps),metrics=[];
  for(const pair of pairs){const files=[pair.a,pair.b].map((index,i)=>path.join(takes[i],'frames',String(index).padStart(6,'0')+'.png'));const metric=await pictureDiff(...files);if(metric.width!==width||metric.height!==height)throw Error('capture dimensions differ from request');metrics.push({...pair,...metric,passed:passesAgreement(metric)});}
  const failed=metrics.filter(m=>!m.passed),result={fps,frameCounts:frames.map(f=>f.length),shared:metrics.length,passed:failed.length===0,failures:failed.length,maxShare:Math.max(...metrics.map(m=>m.share)),maxMean:Math.max(...metrics.map(m=>m.mean)),maxDelta:Math.max(...metrics.map(m=>m.max)),firstFailure:failed[0]||null,eventStreamsEqual:JSON.stringify(receipts[0].events)===JSON.stringify(receipts[1].events),metrics};
  fs.writeFileSync(path.join(root,'result.json'),JSON.stringify(result,null,2)+'\n');return result;
 }finally{for(const take of takes)fs.rmSync(take,{recursive:true,force:true});if(!out)fs.rmSync(root,{recursive:true,force:true});}
}
async function main(){
 const args=process.argv.slice(2),get=name=>args[args.indexOf(name)+1],fps=args.flatMap((v,i)=>v==='--fps'?[Number(args[i+1])]:[]);if(fps.length!==2||!['--endpoint','--timeline','--width','--height'].every(n=>args.includes(n)))throw Error('Required: --endpoint --timeline --fps <a> --fps <b> --width --height [--out]');
 if(!fps.every(n=>Number.isSafeInteger(n)&&n>0)||fps[0]===fps[1])throw Error('two distinct positive integer fps values required');const result=await fpsAgreement({endpoint:get('--endpoint'),timeline:get('--timeline'),fps,width:Number(get('--width')),height:Number(get('--height')),out:args.includes('--out')?get('--out'):undefined});console.log(JSON.stringify(result));if(!result.passed)process.exitCode=1;
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href)main().catch(e=>{console.error(e.stack||e.message);process.exitCode=1;});
