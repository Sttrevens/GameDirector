// Adoption checks: real compiled cues and Three camera math, no WebGL. Each
// mutation catches an engine literal hiding a Core value; no legacy key remains.
import test,{after} from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {tempDirectory} from './test_temp.mjs';
import path from 'node:path';
import {execFileSync,spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import * as THREE from '../packages/gamedirector-three/node_modules/three/build/three.module.js';
import {ThreeDirector} from '../packages/gamedirector-three/runtime.js';
import {ImplementedShotParams} from '../packages/gamedirector-three/shot-params.mjs';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..'),cli=path.join(repo,'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll');
const gd=(...args)=>JSON.parse(execFileSync('dotnet',[cli,...args],{encoding:'utf8',maxBuffer:8*1024*1024}));
const Legacy=['frames','defaultFov','sensorSizeMm','eases','shotParams','currentAnchor','takeStates','bridgeErrors'];
const contract=gd('contract','--json');
const scene=contract.camera.scene,{root}=tempDirectory('gd-three-contract-');
const manifest={game:'ThreeContract',manifestVersion:'0.1',roles:[{id:'hero',defaultActor:'body',presentAtStart:true}],actors:[{id:'body',clips:[]}],
  locations:[{id:'from',position:scene.from},{id:'to',position:scene.to}],audio:[],shotTypes:contract.camera.shots.map(s=>s.id),
  frameTypes:contract.camera.frames.map(f=>f.id),capabilities:{'director.mode':'offline-sandbox','camera.params':ImplementedShotParams.join(',')}};
const manifestPath=path.join(root,'manifest.json');fs.writeFileSync(manifestPath,JSON.stringify(manifest));
const savedDocument=globalThis.document;globalThis.document={body:{replaceChildren(){}}};
after(()=>{globalThis.document=savedDocument;});
const V=a=>new THREE.Vector3(...a);
const adapter={create(){const world=new THREE.Scene(),role=new THREE.Group(),visual=new THREE.Mesh(new THREE.BoxGeometry(...scene.subjectSize),new THREE.MeshBasicMaterial());
  visual.position.copy(V(scene.subjectCenter));role.add(visual);world.add(role);world.updateMatrixWorld(true);return {scene:world,roles:new Map([['hero',role]])};},animate(){}};
const makeRenderer=()=>({domElement:{toDataURL:()=> 'data:image/png;base64,iVBORw0KGgo='},dispose(){},render(scene,camera){scene.updateMatrixWorld(true);camera.updateMatrixWorld(true);}});
const shot=(type='lockoff',extra={})=>({type,subject:'hero',frame:'full',from:'from',durationSeconds:scene.durationSeconds,ease:'linear',...extra});
const cache=new Map();
function compile(s,fps=4) {
  const timeline={version:'0.1',id:'three-contract',cues:[{t:0,type:'camera.shot',shot:s}]},key=JSON.stringify([timeline,fps]);
  if(!cache.has(key)){const file=path.join(root,'timeline.json');fs.writeFileSync(file,JSON.stringify(timeline));
    const c=gd('compile',file,'--manifest',manifestPath,'--take','--fps',String(fps),'--width','64','--height','64');
    assert.equal(c.admission.ok,true,JSON.stringify(c.admission));cache.set(key,c);}
  return cache.get(key);
}
function run(c,s,check,fps=4) {
  const graph=structuredClone(c);for(const key of Legacy)assert.equal(Object.hasOwn(graph,key),false,key);
  const d=new ThreeDirector(adapter,manifest,graph,{makeRenderer});
  try{d.begin(compile(s,fps),64,64);return check(d);}finally{d.end();}
}
const mutate=f=>{const c=structuredClone(contract);f(c);return c;};
const near=(a,b)=>assert.ok(Math.abs(a-b)<1e-9,`actual=${a}, expected=${b}`);
const swept=d=>{const a=V(scene.from).sub(d.target('hero')),b=d.camera.position.clone().sub(d.target('hero'));a.y=b.y=0;return THREE.MathUtils.radToDeg(a.angleTo(b));};
test('unmutated full-export compiled take uses graph coverage and defaults',()=>{
  run(contract,shot('lockoff',{from:contract.camera.currentAnchor}),d=>{
    const expected=scene.subjectSize[1]/(2*Math.tan(THREE.MathUtils.degToRad(contract.camera.lens.defaultFov)/2)*contract.camera.frames.find(f=>f.id==='full').coverage);
    near(d.camera.position.distanceTo(d.target('hero')),expected);assert.equal(d.camera.near,contract.camera.rig.nearClip);
    for(let i=0;i<d.compiled.frameCount;i++)d.frame(i,4);assert.deepEqual(d.reviews.map(r=>r.frame),[0]);
  });
});
test('contract mutation orbitDeg default changes default-orbit swept angle to 45',()=>{
  const s=shot('orbit'),check=d=>{d.advanceTo(scene.durationSeconds);return swept(d);};
  near(run(contract,s,check),90);
  const changed=run(mutate(c=>c.camera.params.find(p=>p.name==='orbitDeg').default=45),s,check);
  near(changed,45);assert.notEqual(changed,run(contract,s,check));
});
test('contract mutation review.intervalSeconds changes reviewed frame list',()=>{
  const s=shot('lockoff',{durationSeconds:3}),check=d=>{for(let i=0;i<d.compiled.frameCount;i++)d.frame(i,4);return d.reviews.map(r=>r.frame);};
  const original=run(contract,s,check),changed=run(mutate(c=>c.review.intervalSeconds=2),s,check);
  assert.deepEqual(original,[0,4,8]);assert.deepEqual(changed,[0,8]);assert.notDeepEqual(changed,original);
});
test('contract mutation rig.nearClip changes camera near to .05',()=>{
  const original=run(contract,shot(),d=>d.camera.near),changed=run(mutate(c=>c.camera.rig.nearClip=.05),shot(),d=>d.camera.near);
  assert.equal(original,.03);assert.equal(changed,.05);assert.notEqual(changed,original);
});
test('contract mutation fallbackFrame uses full coverage for unknown frame id',t=>{
  const s=shot('lockoff',{from:contract.camera.currentAnchor,frame:'unknown-frame'}),file=path.join(root,'unknown-frame.json');
  fs.writeFileSync(file,JSON.stringify({version:'0.1',id:'unknown-frame',cues:[{t:0,type:'camera.shot',shot:s}]}));
  const compiled=spawnSync('dotnet',[cli,'compile',file,'--manifest',manifestPath,'--take'],{encoding:'utf8'});
  assert.notEqual(compiled.status,0);assert.match(compiled.stderr,/unknown-frame/);
  t.diagnostic('gd compile refuses an unknown frame id; drive apply() with a hand-built cue after a compiled begin().');
  const check=c=>run(c,shot(),d=>{d.apply({t:0,type:'camera.shot',shot:s});d.evaluate(0);return d.camera.position.distanceTo(d.target('hero'));});
  const original=check(contract),changed=check(mutate(c=>c.camera.fallbackFrame='full'));
  near(changed,scene.subjectSize[1]/(2*Math.tan(THREE.MathUtils.degToRad(contract.camera.lens.defaultFov)/2)*contract.camera.frames.find(f=>f.id==='full').coverage));
  near(original,changed*2);assert.notEqual(changed,original);
});
test('contract mutation offsetY default shifts the start pose by +1 y',()=>{
  const original=run(contract,shot(),d=>d.camera.position.toArray()),changed=run(mutate(c=>c.camera.params.find(p=>p.name==='offsetY').default=1),shot(),d=>d.camera.position.toArray());
  assert.deepEqual(original,scene.from);assert.deepEqual(changed,[original[0],original[1]+1,original[2]]);assert.notDeepEqual(changed,original);
});
test('defaultOf throws for unknown or derived params instead of inventing a default',()=>run(contract,shot(),d=>{
  assert.throws(()=>d.defaultOf('unknown'),/unknown.*no contract default/);assert.throws(()=>d.defaultOf('coverage'),/coverage.*no contract default/);
}));
for(const [id,mutateEase,reason] of [['unknown ease id',s=>s.ease='unknown-ease',/unknown ease.*unknown-ease/],['unknown ease formula',()=>{},/unimplemented ease formula.*unknown-formula/]])test(id+' throws when shot is applied',()=>{
  const c=structuredClone(contract),s=shot();mutateEase(s);if(id==='unknown ease formula')c.camera.eases.find(e=>e.id===s.ease).formula='unknown-formula';
  assert.throws(()=>run(c,shot(),d=>d.apply({t:0,type:'camera.shot',shot:s})),reason);
});
test('row-driven registered motions and unregistered shot types still hold',()=>{
  const c=mutate(c=>c.camera.shots.find(s=>s.id==='dolly').motion='hold');
  run(c,shot('dolly',{to:'to'}),d=>{d.advanceTo(scene.durationSeconds);assert.deepEqual(d.camera.position.toArray(),scene.from);});
  run(contract,shot(),d=>{d.apply({t:0,type:'camera.shot',shot:shot('custom',{to:'to'})});d.advanceTo(scene.durationSeconds);assert.deepEqual(d.camera.position.toArray(),scene.from);});
});
// Lazy import lets the mutation cases demonstrate real failures against the
// pre-adoption runtime before contract.mjs exists.
const options=async c=>{const {engineStamps}=await import('../packages/gamedirector-three/contract.mjs');return {stamps:engineStamps(c,{sourceFingerprint:'fixture',sourceRoot:root}),implementedParams:ImplementedShotParams};};
const cases=[
  ['missing schema',c=>delete c.schema,/schema.*missing/],
  ['foreign schema',c=>c.schema='foreign',/schema.*foreign/],
  ['major+1',c=>c.version.major++,/implements.*1\.2.*provides.*2\.2/],
  ['minor below Implements.minor',c=>c.version.minor=0,/implements.*1\.2.*provides.*1\.0/,{implements:{major:1,minor:2}}],
  ['unknown ease formula',c=>c.camera.eases[0].formula='new-ease',/ease formula.*new-ease/],
  ['unknown motion id',c=>c.camera.shots[0].motion='new-motion',/motion.*new-motion/],
  ['unknown motion row',c=>c.camera.motions.push({id:'new-motion'}),/motion.*new-motion/],
  ['missing formula id',c=>delete c.camera.progress.id,/formula.*camera\.progress/],
  ['unknown focal formula',c=>c.camera.lens.focalToFov.id='new-focal',/formula.*new-focal/],
  ['unknown auto-frame formula',c=>c.camera.rig.autoFrameDistance.id='new-distance',/formula.*new-distance/],
  ['unknown aspect formula',c=>c.camera.lens.aspect.id='new-aspect',/formula.*new-aspect/],
  ['unknown role bounds formula',c=>c.review.roles.bounds.id='new-bounds',/bounds.*new-bounds/],
  ['unknown crown formula',c=>c.review.roles.crown.id='new-crown',/crown.*new-crown/],
  ['implemented param absent',c=>c.camera.params=c.camera.params.filter(p=>p.name!=='orbitDeg'),/param.*orbitDeg/],
  ['missing camera.params stamp row',c=>c.capabilities=c.capabilities.filter(r=>r.key!=='camera.params'),/stamp row.*camera\.params.*missing/],
  ['missing camera.params separator',c=>delete c.capabilities.find(r=>r.key==='camera.params').separator,/camera\.params.*non-empty string separator/],
  ['empty camera.params separator',c=>c.capabilities.find(r=>r.key==='camera.params').separator='',/camera\.params.*non-empty string separator/],
  ['non-string camera.params separator',c=>c.capabilities.find(r=>r.key==='camera.params').separator=123,/camera\.params.*non-empty string separator/],
  ['two-valued capture.audio row',c=>c.capabilities.find(r=>r.key==='capture.audio').values.push({value:'other-audio'}),/capture\.audio.*exactly one value/],
  ['zero-valued capture.audio row',c=>c.capabilities.find(r=>r.key==='capture.audio').values=[],/capture\.audio.*exactly one value/],
  ['two-valued director.mode row',c=>c.capabilities.find(r=>r.key==='director.mode').values.push({value:'other-mode'}),/director\.mode.*exactly one value/],
  ['zero-valued director.mode row',c=>c.capabilities.find(r=>r.key==='director.mode').values=[],/director\.mode.*exactly one value/],
  ['missing capture.audio stamp row',c=>c.capabilities=c.capabilities.filter(r=>r.key!=='capture.audio'),/stamp row.*capture\.audio.*missing/],
  ['missing director.mode stamp row',c=>c.capabilities=c.capabilities.filter(r=>r.key!=='director.mode'),/stamp row.*director\.mode.*missing/],
  ['engine row without stamp',c=>c.capabilities.push({key:'new-engine-stamp',declarer:'engine'}),/stamp.*new-engine-stamp/],
  ['stamped adapter-only row',c=>c.capabilities.find(r=>r.key==='engine').declarer='adapter',/stamp.*engine.*adapter/],
  ['stamped unregistered row',c=>c.capabilities=c.capabilities.filter(r=>r.key!=='engine'),/stamp.*engine.*not an engine/],
  ['stamped enum outside values',c=>c.capabilities.find(r=>r.key==='director.mode').values=[{value:'offline-sandbox'}],/enum.*director\.mode.*bad-mode/,{badMode:true}],
  ['missing Uses key',c=>delete c.bridge.routes.takeStart,/bridge\.routes\.takeStart/],
  ['fallbackFrame absent from frames',c=>c.camera.fallbackFrame='missing-frame',/fallbackFrame.*missing-frame/],
];
for(const [name,change,reason,extra={}] of cases)test('refusal names '+name,async()=>{
  const {refusal}=await import('../packages/gamedirector-three/contract.mjs'),c=structuredClone(contract);change(c);const opts={...await options(c),...extra};
  if(extra.badMode)opts.stamps['director.mode']='bad-mode';assert.match(refusal(c,opts),reason);
});
test('refusal accepts minor+1 and shipped full-export contract',async()=>{
  const {refusal}=await import('../packages/gamedirector-three/contract.mjs');assert.equal(refusal(contract,await options(contract)),null);
  const newer=mutate(c=>{c.version.minor++;c.camera.fit={id:'unknown-future-fit'};c.review.future={id:'unknown-future-review'};});assert.equal(refusal(newer,await options(newer)),null);
  const old=JSON.parse(fs.readFileSync(path.join(repo,'docs/contract/engine-contract-1.0.json')));assert.match(refusal(old,await options(old)),/implements.*1\.2.*provides.*1\.0/);
});
test('engine stamps preserve today\'s key order and take enum/list values from rows',async()=>{
  const {engineStamps}=await import('../packages/gamedirector-three/contract.mjs');
  const c=mutate(c=>{c.capabilities.find(r=>r.key==='director.mode').values[0].value='future-mode';c.capabilities.find(r=>r.key==='capture.audio').values[0].value='future-audio';c.capabilities.find(r=>r.key==='camera.params').separator='|';});
  assert.deepEqual(engineStamps(c,{sourceFingerprint:'fixture',sourceRoot:root}),{
    'director.mode':'future-mode','presentation.sourceFingerprint':'fixture',engine:'three','project.sourceRoot':root,'capture.audio':'future-audio','camera.params':ImplementedShotParams.join('|'),measure:'measure-v1'});
  assert.deepEqual(Object.keys(engineStamps(c,{sourceFingerprint:'fixture',sourceRoot:root})),['director.mode','presentation.sourceFingerprint','engine','project.sourceRoot','capture.audio','camera.params','measure']);
});
test('engine stamps never let join invent a separator for an invalid row',async()=>{
  const {engineStamps}=await import('../packages/gamedirector-three/contract.mjs');
  for(const separator of [undefined,'',null,123]){const c=mutate(c=>c.capabilities.find(r=>r.key==='camera.params').separator=separator);
    assert.equal(engineStamps(c,{sourceFingerprint:'fixture',sourceRoot:root})['camera.params'],undefined);}
});
// Match only top-level reads, including optional chaining and bracket access;
// camera.frames is legitimate. Plant reads to prove the denylist is live.
const legacyReads=text=>text.match(new RegExp(`\\bcontract\\s*(?:(?:\\?\\.)?\\s*\\[\\s*['"](?:${Legacy.join('|')})['"]\\s*\\]|(?:\\?\\.|\\.)\\s*(?:${Legacy.join('|')})\\b)`,'g'))||[];
test('static legacy-read scanner catches planted direct, optional and bracket reads',()=>{
  for(const key of Legacy)for(const text of [`contract.${key}`,`contract?.${key}`,`contract['${key}']`,`contract?.["${key}"]`])assert.deepEqual(legacyReads(text),[text]);
  assert.deepEqual(legacyReads('contract.camera.frames; contract.camera.lens.defaultFov; contract.take.states'),[]);
});
test('static legacy-read check forbids all 8 retired keys in Three product modules',()=>{
  for(const file of ['runtime.js','server.mjs','contract.mjs','camera-kernels.mjs','bridge-errors.mjs']){
    const full=path.join(repo,'packages/gamedirector-three',file);assert.deepEqual(legacyReads(fs.readFileSync(full,'utf8')),[],file+' reads legacy contract keys');
  }
});
