// Characterization only: compiled Core fixtures against the unchanged Three
// runtime. The renderer double replaces WebGL, not camera or timeline behavior.
import test,{after} from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
// Resolve from the engine package (not tools/ or a game checkout); npm ci in
// verify.yml installs this exact ESM file on a clean Ubuntu runner.
import * as THREE from '../packages/gamedirector-three/node_modules/three/build/three.module.js';
import {ThreeDirector} from '../packages/gamedirector-three/runtime.js';
import {ImplementedShotParams} from '../packages/gamedirector-three/shot-params.mjs';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const cli=path.join(repo,'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll');
assert.ok(fs.existsSync(cli),'build the Debug CLI first: '+cli);
const gd=(...args)=>JSON.parse(execFileSync('dotnet',[cli,...args],{encoding:'utf8',maxBuffer:8*1024*1024}));
const contract=gd('contract','--json'),camera=contract.camera,scene=camera.scene;
const root=fs.mkdtempSync(path.join(os.tmpdir(),'gd-three-conformance-'));
const manifest={game:'ContractConformance',gameVersion:'1',manifestVersion:'0.1',
  roles:[{id:'hero',defaultActor:'body',presentAtStart:true}],actors:[{id:'body',clips:[]}],
  locations:[{id:'from',position:scene.from},{id:'to',position:scene.to}],audio:[],
  shotTypes:camera.shots.map(s=>s.id),frameTypes:camera.frames.map(f=>f.id),
  capabilities:{'director.mode':'offline-sandbox','camera.params':ImplementedShotParams.join(',')}};
const manifestPath=path.join(root,'manifest.json');fs.writeFileSync(manifestPath,JSON.stringify(manifest));
const savedDocument=globalThis.document;globalThis.document={body:{replaceChildren(){}}};
after(()=>{globalThis.document=savedDocument;fs.rmSync(root,{recursive:true,force:true});});
const V=a=>new THREE.Vector3(...a),supported=new Set(ImplementedShotParams);
const rows=camera.params.filter(p=>supported.has(p.name));
const cache=new Map();
function compile(shot,{fps=4,captureFromFrame=0,prelude=true}={}) {
  const cues=prelude?[{t:0,type:'camera.shot',shot:shotSpec('lockoff')},{t:scene.durationSeconds,type:'camera.shot',shot}]:[{t:0,type:'camera.shot',shot}];
  const timeline={version:'0.1',id:'contract-conformance',cues};const key=JSON.stringify([timeline,fps,captureFromFrame]);
  if(!cache.has(key)) {
    const file=path.join(root,'timeline.json');fs.writeFileSync(file,JSON.stringify(timeline));
    const compiled=gd('compile',file,'--manifest',manifestPath,'--take','--fps',String(fps),'--width','64','--height','64','--from-frame',String(captureFromFrame));
    assert.equal(compiled.admission.ok,true,JSON.stringify(compiled.admission));cache.set(key,compiled);
  }
  return cache.get(key);
}
function shotSpec(type='lockoff',params) {
  const shot={type,subject:'hero',frame:'full',from:'from',durationSeconds:scene.durationSeconds,ease:'linear'};
  if(camera.shots.find(s=>s.id===type).requiresTarget)shot.to='to';if(params)shot.params=params;return shot;
}
function makeRenderer() {
  return {domElement:{toDataURL:()=> 'data:image/png;base64,iVBORw0KGgo='},dispose(){},
    render(scene,camera){scene.updateMatrixWorld(true);camera.updateMatrixWorld(true);}};
}
function adapter(offCenter=false) {
  return {create(){
    const world=new THREE.Scene(),role=new THREE.Group();
    const visual=new THREE.Mesh(new THREE.BoxGeometry(...scene.subjectSize),new THREE.MeshBasicMaterial());visual.position.copy(V(scene.subjectCenter));
    if(offCenter)visual.position.add(new THREE.Vector3(1,0,.5));role.add(visual);world.add(role);world.updateMatrixWorld(true);
    return {scene:world,roles:new Map([['hero',role]])};
  },animate(){}};
}
function withShot(shot,check,{offCenter=false}={}) {
  const director=new ThreeDirector(adapter(offCenter),manifest,contract,{makeRenderer});
  try {director.begin(compile(shot),64,64);director.advanceTo(scene.durationSeconds);return check(director);}
  finally {director.end();}
}
function matrices(d) {d.renderer.render(d.world.scene,d.camera);}
function pose(d) {return {position:d.camera.position.toArray(),rotation:d.camera.quaternion.toArray(),fov:d.camera.fov};}
function paramShot(name,params) {
  const shot=shotSpec(name==='orbitDeg'?'orbit':name.startsWith('toOffset')?'dolly':'lockoff',params);
  if(name==='coverage')shot.from=camera.currentAnchor;return shot;
}
function evaluated(shot,options) {return withShot(shot,d=>{d.advanceTo(scene.durationSeconds*1.5);return pose(d);},options);}
function near(actual,expected,tolerance,label) {assert.ok(Math.abs(actual-expected)<=tolerance,`${label}: actual=${actual}, expected=${expected}, delta=${actual-expected}, tolerance=${tolerance}`);}
function required(review) {
  for(const p of contract.review.required) {let value=review;for(const part of p.split('.'))value=value?.[part];assert.notEqual(value,undefined,p);}
}

test('injected renderer drives the real begin and camera',()=>withShot(shotSpec(),d=>{
  matrices(d);assert.deepEqual(d.camera.position.toArray(),scene.from);assert.equal(d.composition().active,true);
}));
for(const ease of camera.eases)for(const s of ease.samples)test(`ease ${ease.id} p=${s.p}`,()=>{
  const shot=shotSpec('dolly');shot.ease=ease.id;
  withShot(shot,d=>{d.advanceTo(scene.durationSeconds*(1+s.p));assert.equal(d.camera.position.distanceTo(V(scene.from))/V(scene.to).distanceTo(V(scene.from)),s.e);});
});
for(const shot of camera.shots)for(const [i,s] of camera.motions.find(m=>m.id===shot.motion).samples.entries()) {
  if(Object.keys(s.params||{}).some(name=>!supported.has(name)))continue;
  test(`motion ${shot.id} row=${i} ${s.observable}`,{},()=>{
    const authored=shotSpec(shot.id,s.params);authored.ease=s.ease;if(s.authoredTo)authored.to='to';else delete authored.to;
    withShot(authored,d=>{
      matrices(d);const from=V(scene.from),pos0=d.camera.position.clone(),right=new THREE.Vector3().setFromMatrixColumn(d.camera.matrixWorld,0),target0=d.target('hero');
      if(s.stepSubject)d.role('hero').position.add(V(scene.subjectStep));d.advanceTo(scene.durationSeconds*(1+s.p));
      // With no authored offsets, the contract anchor is the resolved from;
      // only side uses the engine's evaluated p=0 pose.
      const pos=d.camera.position,target=d.target('hero'),a=from.clone().sub(target0),b=pos.clone().sub(target);a.y=b.y=0;
      const actual=(()=>{switch(s.observable){
        case 'displacement':return pos.distanceTo(from);
        case 'fraction':return pos.distanceTo(from)/V(scene.to).distanceTo(from);
        case 'radius':return b.length();case 'radius3d':return pos.distanceTo(target);
        case 'swept-deg':return THREE.MathUtils.radToDeg(a.angleTo(b));
        case 'side':return Math.sign(pos.clone().sub(pos0).dot(right));
        case 'drift':return pos.clone().sub(target).sub(from.clone().sub(target0)).length();
        default:throw Error('uncovered observable '+s.observable);
      }})();near(actual,s.expected,s.tolerance,`${shot.id} ${s.observable}`);
    });
  });
}
for(const [i,s] of [...camera.aim.samples,...camera.lens.aspect.samples].entries())test(`aim/Hor+ row=${i} aspect=${s.aspect} pan=${s.pan} tilt=${s.tilt} roll=${s.roll}`,()=>{
  const shot=shotSpec('lockoff',{pan:s.pan,tilt:s.tilt,roll:s.roll});shot.fov=s.fov;
  withShot(shot,d=>{d.camera.aspect=s.aspect;d.camera.updateProjectionMatrix();matrices(d);const [x,y,z]=d.composition().center;
    assert.ok(z>0,'subject behind camera');near(x,s.x,s.tolerance,'viewport x');near(y,s.y,s.tolerance,'viewport y');});
});
for(const s of camera.lens.samples)test(`lens ${s.focalLengthMm}mm`,()=>{
  const shot=shotSpec();shot.focalLengthMm=s.focalLengthMm;
  withShot(shot,d=>near(d.camera.fov,s.fov,s.tolerance,'fov'));
});
for(const [i,s] of camera.rig.samples.entries())test(`auto-frame row=${i}`,()=>{
  const shot=shotSpec('lockoff',s.coverage===undefined?undefined:{coverage:s.coverage});shot.from=camera.currentAnchor;shot.frame=s.frame;
  withShot(shot,d=>near(d.camera.position.distanceTo(d.target('hero')),s.radius3D,s.tolerance,'radius3d'));
});
for(const p of rows.filter(p=>p.default!==undefined))test(`default ${p.name} omitted equals authored exactly`,()=>{
  assert.deepEqual(evaluated(paramShot(p.name)),evaluated(paramShot(p.name,{[p.name]:p.default})));
});
for(const p of rows.filter(p=>p.derivation)) {
  if(p.derivation==='frame-coverage')for(const f of camera.frames)test(`derived ${p.name} ${f.id}`,()=>{
    const omitted=paramShot(p.name);omitted.frame=f.id;const authored=structuredClone(omitted);authored.params={[p.name]:f.coverage};
    assert.deepEqual(evaluated(omitted),evaluated(authored));
  });
  else if(p.derivation==='start-fov')test(`derived ${p.name}`,()=>{
    const omitted=paramShot(p.name);omitted.fov=camera.lens.samples[0].fov;
    const authored=structuredClone(omitted);authored.params={[p.name]:omitted.fov};assert.deepEqual(evaluated(omitted),evaluated(authored));
  });
  else if(p.derivation==='subject-bounds-center')test(`derived ${p.name} preserves the off-center full vector, not .y`,()=>{
    const omitted=withShot(shotSpec(),d=>{
      const offset=d.target('hero').sub(d.role('hero').position);const expected=new THREE.PerspectiveCamera();expected.position.copy(d.camera.position);
      expected.lookAt(d.role('hero').position.clone().add(offset));assert.deepEqual(d.camera.quaternion.toArray(),expected.quaternion.toArray());return pose(d);
    },{offCenter:true});
    assert.notDeepEqual(omitted,evaluated(shotSpec('lockoff',{[p.name]:scene.subjectCenter[1]}),{offCenter:true}));
  });
  else throw Error('uncovered derivation '+p.derivation);
}
function nonDefault(name) {
  switch(name){case 'targetHeight':case 'distance':return 2;case 'coverage':return 1.5;case 'orbitDeg':return 45;
    case 'pan':return 20;case 'tilt':return 15;case 'roll':return 30;case 'endFov':return 70;default:return .5;}
}
for(const p of rows)test(`implemented param ${p.name} changes the pose`,()=>{
  assert.notDeepEqual(evaluated(paramShot(p.name)),evaluated(paramShot(p.name,{[p.name]:nonDefault(p.name)})));
});
for(const [i,s] of contract.review.samples.entries())test(`review cadence row=${i} fps=${s.fps} from=${s.captureFromFrame}`,()=>{
  const shot=shotSpec();shot.durationSeconds=s.frameCount/s.fps;
  const compiled=compile(shot,{fps:s.fps,captureFromFrame:s.captureFromFrame,prelude:false});assert.equal(compiled.frameCount,s.frameCount);
  const d=new ThreeDirector(adapter(),manifest,contract,{makeRenderer});
  try {
    d.begin(compiled,64,64);
    for(let index=0;index<compiled.frameCount;index++)if(index<s.captureFromFrame)d.advance(index,s.fps);else d.frame(index,s.fps);
    assert.deepEqual(d.reviews.map(r=>r.frame),s.reviewed);for(const review of d.reviews)required(review);
  } finally {d.end();}
});
for(const p of contract.review.required)test(`review path ${p}`,()=>{
  const fps=contract.review.samples[0].fps,d=new ThreeDirector(adapter(),manifest,contract,{makeRenderer});
  try {
    d.begin(compile(shotSpec(),{fps,prelude:false}),64,64);
    const {review}=d.frame(0,fps);assert.ok(review,'rendered timeline frame 0 must emit a review');
    let value=review;for(const part of p.split('.'))value=value?.[part];assert.notEqual(value,undefined,p);
  } finally {d.end();}
});
