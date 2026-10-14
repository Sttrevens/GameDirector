// Real runtime/measurement; only WebGL rendering and PNG encoding are doubled.
import test,{after} from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';
import * as THREE from '../packages/gamedirector-three/node_modules/three/build/three.module.js';
import {ThreeDirector} from '../packages/gamedirector-three/runtime.js';
const cli=path.resolve('src/GameDirector.Cli/bin/Debug/net8.0/gd.dll');
const contract=JSON.parse(execFileSync('dotnet',[cli,'contract','--json'],{encoding:'utf8'}));
const document=globalThis.document;globalThis.document={body:{replaceChildren(){}}};after(()=>{globalThis.document=document;});
function renderer(){
 const states=[];return {states,domElement:{toDataURL:()=> 'data:image/png;base64,'+Buffer.from(JSON.stringify(states.at(-1))).toString('base64')},dispose(){},render(scene,camera){
  scene.updateMatrixWorld(true);camera.updateMatrixWorld(true);const nodes=[];
  scene.traverse(o=>nodes.push({type:o.type,visible:o.visible,matrix:o.matrixWorld.toArray(),color:o.material?.color?.toArray(),clock:o.userData.clock}));
  states.push({matrixWorld:camera.matrixWorld.toArray(),projectionMatrix:camera.projectionMatrix.toArray(),fov:camera.fov,aspect:camera.aspect,sceneDigest:createHash('sha256').update(JSON.stringify(nodes)).digest('hex')});
 }};
}
const adapter={create(){const scene=new THREE.Scene(),body=new THREE.Mesh(new THREE.BoxGeometry(1,2,1),new THREE.MeshBasicMaterial());scene.add(body);return {scene,roles:new Map([['hero',body]])};},animate(body,{worldTime}){body.rotation.y=worldTime*.2;body.userData.clock=worldTime;}};
const manifest={locations:[{id:'first',position:[0,1,5]},{id:'second',position:[6,3,2.5]}]};
const cue=(t,from,params={})=>({t,type:'camera.shot',shot:{type:'lockoff',subject:'hero',from,frame:'full',durationSeconds:1,ease:'linear',params}});
function director(graph=contract,cues=[cue(0,'first'),cue(1,'second',{pan:-15,targetHeight:.9})]){const d=new ThreeDirector(adapter,manifest,graph,{makeRenderer:renderer});d.begin({id:'measure',duration:2,frameCount:12,orderedCues:cues},64,64);return d;}
function paths(value,prefix=''){
 return Object.entries(value).flatMap(([key,v])=>{const name=prefix+key;if(name==='composition')return [name];if(Array.isArray(v))return [name+'['+v.length+']'];return v&&typeof v==='object'?paths(v,name+'.'):[name];});
}
function near(actual,want,label,tolerance){assert.ok(Number.isFinite(actual)&&Math.abs(actual-want)<=tolerance,label+': '+actual+' != '+want);}
function vector(actual,want,label,tolerance){assert.equal(actual.length,3);actual.forEach((n,i)=>near(n,want[i],label+'['+i+']',tolerance));}
// M2/M4: compare the row with the index/pose actually rendered, never post-advance state.
test('measure is captured after render before advance and separate from reviews',()=>{
 const d=director();try{
  for(let i=0;i<12;i++){
   const r=d.frame(i,6,true),m=r.measures;assert.ok(m);assert.equal(m.frame,i);assert.equal(m.time,i/6);
   assert.ok(Math.abs(d.time-Math.min(2,(i+1)/6))<1e-12);assert.deepEqual(m.camera.position,i<6?[0,1,5]:[6,3,2.5]);
   if(r.review)assert.deepEqual(m.composition,r.review.composition);
  }
  assert.deepEqual(d.reviews.map(r=>r.frame),[0,6]);
 }finally{d.end();}
});
test('real measureRow output paths witness the declared protocol schema',()=>{
 const d=director();try{assert.equal(d.frame(0,6).measures,null);const m=d.frame(1,6,true).measures;assert.deepEqual(paths(m).sort(),[...contract.measure.fields].sort());}finally{d.end();}
});
test('fixed wire flag and collection do not follow capability key or positional field order',()=>{
 const graph=structuredClone(contract);graph.measure.capability='other-inspection-key';graph.measure.collection='other-collection';graph.measure.fields.reverse();graph.take.requestDefaults.measure=true;
 const d=director(graph);try{const result=d.frame(0,6);assert.ok(Object.hasOwn(result,'measures'),'fixed measures collection missing');const m=result.measures;assert.ok(m,'measure default must use the flag, not capability key');assert.equal(m.frame,0);assert.equal(m.time,0);assert.deepEqual(m.camera.position,[0,1,5]);assert.equal(d.frame(1,6,false).measures,null);}finally{d.end();}
});
// M9: normative WHOLE basis, produced by real ThreeDirector, not a standalone test-local camera.
for(const [index,s] of contract.measure.samples.entries())test('declared measure sample '+index+' matches runtime right/up/forward and lens',()=>{
 const stage={create(){const world=adapter.create();world.roles.get('hero').position.fromArray(s.aim);return world;},animate(){}};
 const d=new ThreeDirector(stage,{locations:[{id:'origin',position:s.from}]},contract,{makeRenderer:renderer});
 try{
  d.begin({id:'sample',duration:1,frameCount:6,orderedCues:[cue(0,'origin',{targetHeight:0,pan:s.pan,tilt:s.tilt,roll:s.roll})]},960,540);
  const fov=d.camera.fov,aspect=d.camera.aspect,m=d.frame(0,6,true).measures,c=m.camera;
  // Three's double-precision conformance uses the reference bound, not float32 liveTolerance.
  const tolerance=s.tolerance;
  vector(c.position,s.from,'position',tolerance);for(const axis of ['right','up','forward']){vector(c[axis],s[axis],axis,tolerance);near(Math.hypot(...c[axis]),1,axis+' unit',tolerance);}
  const r=new THREE.Vector3(...c.right),u=new THREE.Vector3(...c.up),f=new THREE.Vector3(...c.forward);
  near(r.dot(u),0,'right/up',tolerance);near(r.dot(f),0,'right/forward',tolerance);near(u.dot(f),0,'up/forward',tolerance);
  if(s.pan===0&&s.tilt===0)near(f.dot(new THREE.Vector3(...s.aim).sub(new THREE.Vector3(...s.from)).normalize()),1,'unoffset forward/aim',tolerance);
  near(THREE.MathUtils.radToDeg(Math.asin(Math.max(-1,Math.min(1,c.right[1])))),s.horizonRoll,'horizon',tolerance);
  assert.equal(c.fov,fov);assert.equal(c.aspect,aspect);if(index===0)assert.ok(c.right[1]>0,'authored +roll sign must survive runtime extraction');
 }finally{d.end();}
});
// Even a post-render fov mutation must perturb a later rendered state and turn this red.
test('measure on/off preserves render-time camera/scene states, reviews, events and final time',()=>{
 function run(flag){const d=director();try{const frames=[];for(let i=0;i<12;i++){const r=d.frame(i,6,flag);frames.push({png:r.png,review:r.review});}return {states:d.renderer.states,frames,reviews:d.reviews,events:d.finish(),time:d.time};}finally{d.end();}}
 assert.deepEqual(run(true),run(false));
});
// N1: continued evaluation after despawn must fail; a constant lockoff cannot witness freezing.
test('despawning a dolly subject freezes the last pre-despawn pose while the control keeps moving',()=>{
 function run(despawn){
  const shot={...cue(0,'first',{roll:10}),shot:{...cue(0,'first',{roll:10}).shot,type:'dolly',to:'second',durationSeconds:2}};
  const d=director(contract,[shot,...(despawn?[{t:.5,type:'actor.despawn',role:'hero'}]:[])]);try{
   const rows=[];for(let i=0;i<12;i++)rows.push(d.frame(i,6,true).measures);
   return {rows,events:d.events,time:d.time};
  }finally{d.end();}
 }
 const control=run(false),despawned=run(true),cutFrame=3;
 assert.deepEqual(despawned.rows.slice(0,cutFrame),control.rows.slice(0,cutFrame));
 // advance evaluates at t=.5 just before applying despawn; the control renders that same pose at row 3.
 const lastPreDespawnPose=control.rows[cutFrame].camera;
 assert.deepEqual(lastPreDespawnPose.position,[1.5,1.5,4.375]); // from + (.5/2)*(to-from)
 assert.notDeepEqual(control.rows[2].camera,control.rows[0].camera,'dolly must already be moving before despawn');
 for(const m of despawned.rows.slice(cutFrame)){assert.deepEqual(m.composition,{active:false});assert.deepEqual(m.camera,lastPreDespawnPose);}
 for(const m of control.rows.slice(cutFrame+1)){assert.equal(m.composition.active,true);assert.notDeepEqual(m.camera.position,lastPreDespawnPose.position,'no-despawn control must keep moving');}
 assert.ok(despawned.events.some(e=>e.type==='actor.despawn'));assert.ok(control.events.every(e=>e.type!=='actor.despawn'));
 assert.equal(despawned.time,2);assert.equal(control.time,2);
});
