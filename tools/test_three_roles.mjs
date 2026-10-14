// Per-role review: real Three bounds/projection; each fixture kills a cached/hidden/throwing bounds implementation.
import test,{after} from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {build} from '../packages/gamedirector-three/node_modules/esbuild/lib/main.js';
import {tempDirectory} from './test_temp.mjs';
const args=process.argv.slice(2),index=args.indexOf('--three'),threeRoot=path.resolve(index<0?(process.env.GD_THREE_MODULE||'packages/gamedirector-three/node_modules/three'):args[index+1]),{root:scratch}=tempDirectory('gd-fmt23-roles-');
// Bundle production + fixtures against ONE selected THREE, including runtime's instanceof guard.
const built=await build({stdin:{contents:`export * as THREE from 'three';export {ThreeDirector} from './runtime.js';export {roleBounds} from './role-bounds.mjs';export {clone as cloneSkeleton} from '${path.join(threeRoot,'examples/jsm/utils/SkeletonUtils.js')}';`,resolveDir:path.resolve('packages/gamedirector-three')},bundle:true,format:'esm',platform:'node',write:false,alias:{three:path.join(threeRoot,'build/three.module.js')}});
const module=path.join(scratch,'roles.mjs');fs.writeFileSync(module,built.outputFiles[0].contents);const {THREE,ThreeDirector,roleBounds,cloneSkeleton}=await import(pathToFileURL(module));
console.log('Three role tests: '+JSON.parse(fs.readFileSync(path.join(threeRoot,'package.json'))).version);
const contract=JSON.parse(execFileSync('dotnet',['src/GameDirector.Cli/bin/Debug/net8.0/gd.dll','contract','--json'],{encoding:'utf8'}));
const savedDocument=globalThis.document;globalThis.document={body:{replaceChildren(){}}};after(()=>globalThis.document=savedDocument);
const V=a=>new THREE.Vector3(...a),material=()=>new THREE.MeshBasicMaterial(),mesh=(size=[1,2,1],position=[0,1,0])=>{const m=new THREE.Mesh(new THREE.BoxGeometry(...size),material());m.position.copy(V(position));return m;};
function fixture(build=()=>mesh(),extra={},graph=contract) {
 const adapter={create(){const scene=new THREE.Scene(),hero=new THREE.Group();hero.add(build());scene.add(hero);const roles=new Map([['hero',hero]]);for(const [id,r] of Object.entries(extra)){scene.add(r);roles.set(id,r);}scene.updateMatrixWorld(true);return {scene,roles};},animate(){}};
 const manifest={locations:[{id:'from',position:[0,1,-4]}]},makeRenderer=()=>({domElement:{toDataURL:()=> 'data:image/png;base64,AA=='},dispose(){},render(s,c){s.updateMatrixWorld(true);c.updateMatrixWorld(true);}});
 const d=new ThreeDirector(adapter,manifest,graph,{makeRenderer});d.begin({duration:1,orderedCues:[{t:0,type:'camera.shot',shot:{type:'lockoff',subject:'hero',from:'from',frame:'full',ease:'linear',durationSeconds:1}}]},64,64);return d;
}
function withFixture(check,build,extra,graph) {const d=fixture(build,extra,graph);try{return check(d);}finally{d.end();}}
function orient(d,s) {d.camera.position.copy(V(s.from));d.camera.fov=s.fov;d.camera.aspect=s.aspect;d.camera.updateProjectionMatrix();d.camera.lookAt(V(s.aim));d.camera.rotateY(-s.pan*Math.PI/180);d.camera.rotateX(-s.tilt*Math.PI/180);d.camera.rotateZ(s.roll*Math.PI/180);d.renderer.render(d.world.scene,d.camera);}
function near(actual,expected,tolerance=1e-4) {assert.equal(actual?.length,expected?.length);for(let i=0;i<expected.length;i++)assert.ok(Math.abs(actual[i]-expected[i])<=tolerance,`coordinate ${i}: ${actual[i]} != ${expected[i]}`);}
function role(d,id='hero') {return d.composition().roles.find(r=>r.role===id);}
for(const [i,s] of contract.review.roles.samples.entries())test(`role crown/rect reference row=${i} aspect=${s.aspect}`,()=>withFixture(d=>{
 orient(d,s);const r=role(d);assert.equal(r.kind,'subject');assert.equal(r.state,i===7?'straddles-near':s.state);
 if(s.rect)near(r.rect,s.rect,s.tolerance);else assert.equal(r.rect,undefined);near(r.crown,s.crown,s.tolerance);
},()=>mesh(s.max.map((v,i)=>v-s.min[i]),s.min.map((v,i)=>(v+s.max[i])/2))));
test('portrait begin uses width/height for a 9:16 role row',()=>withFixture(d=>{
 const s=contract.review.roles.samples.find(s=>s.aspect===9/16&&s.pan===10&&s.tilt===0);d.begin({duration:1,orderedCues:[{t:0,type:'camera.shot',shot:{type:'lockoff',subject:'hero',from:'from',frame:'full',ease:'linear',durationSeconds:1,fov:s.fov,params:{pan:s.pan}}}]},540,960);d.renderer.render(d.world.scene,d.camera);near(role(d).rect,s.rect,s.tolerance);near(role(d).crown,s.crown,s.tolerance);
}));
test('hidden-child subtree excluded from role rect (legacy center still includes it)',()=>withFixture(d=>{
 orient(d,{from:[0,1,-4],aim:[0,1,0],fov:50,aspect:1,pan:0,tilt:0,roll:0});const r=role(d),nearer=1/(2*Math.tan(25*Math.PI/180)*3.5);near(r.rect,[.5-nearer/2,.5-nearer,.5+nearer/2,.5+nearer]);assert.ok(d.composition().center[0]<0,'legacy centre counts the hidden child');
},()=>{const root=new THREE.Group();root.add(mesh());const hidden=new THREE.Group();hidden.visible=false;hidden.add(mesh([1,2,1],[100,1,0]));root.add(hidden);return root;}));
test('skinned role rect follows posed vertices; legacy center stays cached',()=>withFixture(d=>{
 const skin=d.world.roles.get('hero').children[0];orient(d,{from:[0,1,-4],aim:[0,1,0],fov:50,aspect:1,pan:0,tilt:0,roll:0});const before=role(d),center=d.composition().center;
 skin.skeleton.bones[0].position.x=2;d.renderer.render(d.world.scene,d.camera);const after=role(d);assert.ok(after.rect[2]<before.rect[0]);assert.deepEqual(d.composition().center,center);
},()=>{const g=new THREE.BoxGeometry(1,2,1),count=g.attributes.position.count;g.setAttribute('skinIndex',new THREE.Uint16BufferAttribute(new Uint16Array(count*4),4));const weights=new Float32Array(count*4);for(let i=0;i<count;i++)weights[i*4]=1;g.setAttribute('skinWeight',new THREE.Float32BufferAttribute(weights,4));const s=new THREE.SkinnedMesh(g,material()),bone=new THREE.Bone();s.add(bone);s.position.y=1;s.bind(new THREE.Skeleton([bone]));s.computeBoundingBox();return s;}));
test('instanced child uses every instance matrix and excludes hidden descendants',()=>withFixture(d=>{
 d.renderer.render(d.world.scene,d.camera);const r=role(d);assert.ok(r.rect[0]<0,'translated instance was ignored');assert.ok(r.rect[2]<1,'hidden child polluted bounds');
},()=>{const root=new THREE.Group(),instances=new THREE.InstancedMesh(new THREE.BoxGeometry(1,2,1),material(),2);instances.setMatrixAt(0,new THREE.Matrix4().makeTranslation(0,1,0));instances.setMatrixAt(1,new THREE.Matrix4().makeTranslation(3,1,0));root.add(instances);const hidden=mesh([200,2,1]);hidden.visible=false;root.add(hidden);return root;}));
function sparseBatch(Type=THREE.BatchedMesh) {
 const b=new Type(4,100,200,material()),g=b.addGeometry(new THREE.BoxGeometry(1,2,1));for(const x of [0,-200,-100,3]){const id=b.addInstance(g);b.setMatrixAt(id,new THREE.Matrix4().makeTranslation(x,1,0));}b.setVisibleAt(2,false);b.deleteInstance(1);return b;
}
class StrictBatch extends THREE.BatchedMesh {
 active(id){if(!this._instanceInfo[id]?.active)throw Error('deleted instance '+id);}
 getVisibleAt(id){this.active(id);return super.getVisibleAt(id);}
 getMatrixAt(id,matrix){this.active(id);return super.getMatrixAt(id,matrix);}
 getGeometryIdAt(id){this.active(id);return super.getGeometryIdAt(id);}
}
for(const Type of [THREE.BatchedMesh,StrictBatch])test(`${Type.name}: sparse live id after deleted id; hidden/deleted are screen-RIGHT`,()=>withFixture(d=>{
 orient(d,{from:[0,1,-4],aim:[0,1,0],fov:50,aspect:1,pan:0,tilt:0,roll:0});const r=role(d);assert.equal(r.state,'in-front');near(r.rect,[-.5722534602547791,.19364186849863452,.6531790657506827,.8063581315013655],1e-6);
},()=>sparseBatch(Type)));
for(const relative of [false,true])test(`morph geometry bounds ignore caches, relative=${relative}; legacy reads caches`,()=>withFixture(d=>{
 orient(d,{from:[0,1,-4],aim:[0,1,0],fov:50,aspect:1,pan:0,tilt:0,roll:0});const g=d.world.roles.get('hero').children[0].geometry;
 g.boundingBox=null;g.boundingSphere=null;const b=roleBounds(d.world.roles.get('hero'),d.camera);near(b.min.toArray(),[-2.5,0,-.5],1e-6);near(b.max.toArray(),[.5,2,.5],1e-6);near(d.composition().center,[.7680633650636948,.5,4],1e-12);assert.equal(g.boundingBox,null);assert.equal(g.boundingSphere,null);
 g.computeBoundingBox();const populated=roleBounds(d.world.roles.get('hero'),d.camera);near(populated.min.toArray(),b.min.toArray(),1e-6);near(populated.max.toArray(),b.max.toArray(),1e-6);near(d.composition().center,[.7680633650636948,.5,4],1e-6);
 g.boundingBox.min.x=-200;const independent=roleBounds(d.world.roles.get('hero'),d.camera);near(independent.min.toArray(),b.min.toArray(),1e-6);assert.ok(d.composition().center[0]>10,'legacy must still read the populated cache');
},()=>{const m=mesh(),g=m.geometry,p=g.attributes.position,values=[];for(let i=0;i<p.count;i++)values.push(relative?-2:p.getX(i)-2,relative?0:p.getY(i),relative?0:p.getZ(i));g.morphTargetsRelative=relative;g.morphAttributes.position=[new THREE.Float32BufferAttribute(values,3)];m.updateMorphTargets();return m;}));
test('geometry without a position is empty, not a bounds error',()=>withFixture(d=>{
 const m=d.world.roles.get('hero').children[0];m.geometry=new THREE.BufferGeometry();assert.equal(role(d).state,'empty');assert.equal(m.geometry.boundingBox,null);const c=d.composition();assert.notEqual(c.center,null);assert.equal(c.note,undefined);
}));
for(const kind of ['InstancedMesh','BatchedMesh'])test(`${kind} composes instance and world matrices before taking world AABBs`,()=>withFixture(d=>{
 d.renderer.render(d.world.scene,d.camera);const b=roleBounds(d.world.roles.get('hero'),d.camera),edge=3/Math.sqrt(2)+.5;near(b.min.toArray(),[-edge,-.5,-edge]);near(b.max.toArray(),[edge,.5,edge]);
},()=>{const root=new THREE.Group();root.rotation.y=Math.PI/4;const g=new THREE.BoxGeometry(1,1,1),m=kind==='InstancedMesh'?new THREE.InstancedMesh(g,material(),2):new THREE.BatchedMesh(2,100,200,material()),id=kind==='BatchedMesh'?m.addGeometry(g):null;for(let i=0;i<2;i++){const index=kind==='BatchedMesh'?m.addInstance(id):i;const matrix=new THREE.Matrix4().makeRotationY(-Math.PI/4);matrix.setPosition(i===0?-3:3,0,0);m.setMatrixAt(index,matrix);}root.add(m);return root;}));
test('sprite bounds use the camera-facing world quad, center, scale and material rotation',()=>withFixture(d=>{
 orient(d,{from:[0,1,-4],aim:[0,1,0],fov:50,aspect:1,pan:0,tilt:0,roll:0});const r=role(d),half=1/(8*Math.tan(25*Math.PI/180));near(r.rect,[.5-half,.5-half/2,.5+half,.5+half/2]);near(r.crown,[.5,.5+half/2,4]);
},()=>{const s=new THREE.Sprite(new THREE.SpriteMaterial({rotation:Math.PI/2}));s.scale.set(1,2,1);s.position.y=1;return s;}));
for(const kind of ['Line','Points'])test(`${kind} is excluded: role has no renderable`,()=>withFixture(d=>{
 d.renderer.render(d.world.scene,d.camera);assert.equal(role(d).state,'empty');
},()=>new THREE[kind](new THREE.BufferGeometry().setFromPoints([V([0,0,0]),V([1,2,1])]),kind==='Line'?new THREE.LineBasicMaterial():new THREE.PointsMaterial())));
function cacheRows(d) {const rows=[];d.world.scene.traverse(o=>{for(const target of [o,o.geometry,...(o._geometryInfo||[])])if(target)for(const key of ['boundingBox','boundingSphere']){const value=target[key];rows.push({target,key,identity:value,values:value?.isBox3?[value.min.toArray(),value.max.toArray()]:value?.isSphere?[value.center.toArray(),value.radius]:value});}});return rows;}
test('composition is cache-pure: geometry/object boxes AND spheres, null and populated',()=>withFixture(d=>{
 d.renderer.render(d.world.scene,d.camera);d.world.roles.get('hero').traverse(o=>{if(o.geometry){o.geometry.boundingBox=null;o.geometry.boundingSphere=null;}if('boundingBox' in o)o.boundingBox=null;if('boundingSphere' in o)o.boundingSphere=null;for(const info of o._geometryInfo||[]){info.boundingBox=null;info.boundingSphere=null;}});
 const before=cacheRows(d);d.composition();for(const row of before)assert.equal(row.target[row.key],row.identity,`${row.key} null cache written`);
 d.world.scene.traverse(o=>{if(o.geometry){o.geometry.computeBoundingBox();o.geometry.computeBoundingSphere();}if(o.isInstancedMesh||o.isBatchedMesh){o.computeBoundingBox();o.computeBoundingSphere();}});
 const populated=cacheRows(d);d.composition();const after=cacheRows(d);for(let i=0;i<populated.length;i++){assert.equal(after[i].identity,populated[i].identity,after[i].key+' identity');assert.deepEqual(after[i].values,populated[i].values,after[i].key+' values');}
},()=>{const root=new THREE.Group();root.add(mesh());const i=new THREE.InstancedMesh(new THREE.BoxGeometry(1,2,1),material(),1);i.setMatrixAt(0,new THREE.Matrix4().makeTranslation(0,1,0));root.add(i);const b=new THREE.BatchedMesh(1,100,200,material()),id=b.addInstance(b.addGeometry(new THREE.BoxGeometry(1,2,1)));b.setMatrixAt(id,new THREE.Matrix4().makeTranslation(0,1,0));root.add(b);return root;}));
// Independent oracle: setFromObject may mutate ONLY a deep clone; original caches must retain identity/null/value.
function deepClone(root) {const copy=cloneSkeleton(root);function detach(source,target){if(source.isSkinnedMesh)target.bindMatrixInverse.copy(source.bindMatrixInverse);if(source.geometry){target.geometry=source.geometry.clone();assert.notEqual(target.geometry,source.geometry);if(source.geometry.boundingBox)assert.notEqual(target.geometry.boundingBox,source.geometry.boundingBox);}for(let i=0;i<source.children.length;i++)detach(source.children[i],target.children[i]);}detach(root,copy);return copy;}
function morphMesh(relative) {const m=mesh(),g=m.geometry,p=g.attributes.position,a=[];for(let i=0;i<p.count;i++)a.push(relative?-2:p.getX(i)-2,relative?0:p.getY(i),relative?0:p.getZ(i));g.morphTargetsRelative=relative;g.morphAttributes.position=[new THREE.Float32BufferAttribute(a,3)];m.updateMorphTargets();return m;}
function skinMesh() {const g=new THREE.BoxGeometry(1,2,1),n=g.attributes.position.count,a=new Float32Array(n*4);for(let i=0;i<n;i++)a[i*4]=1;g.setAttribute('skinIndex',new THREE.Uint16BufferAttribute(new Uint16Array(n*4),4));g.setAttribute('skinWeight',new THREE.Float32BufferAttribute(a,4));const s=new THREE.SkinnedMesh(g,material()),bone=new THREE.Bone();s.add(bone);s.bind(new THREE.Skeleton([bone]));bone.position.x=2;s.position.y=1;return s;}
function rangedBatch(indexed) {const g=new THREE.BufferGeometry().setAttribute('position',new THREE.Float32BufferAttribute(indexed?[0,0,0,1,2,1,-1,1,.5,100,100,100]:[0,0,0,1,2,1,-1,1,.5],3));if(indexed)g.setIndex([0,1,2]);const b=new THREE.BatchedMesh(2,100,200,material()),id=b.addGeometry(g);for(const x of [0,3])b.setMatrixAt(b.addInstance(id),new THREE.Matrix4().makeTranslation(x,1,0));b.setVisibleAt(1,false);return b;}
const legacyRows=[
 ...['null','populated'].map(g=>({name:'rigid geometry '+g,build:()=>mesh(),g})),
 ...[false,true].flatMap(relative=>['null','populated'].map(g=>({name:`morph relative=${relative} geometry ${g}`,build:()=>morphMesh(relative),g}))),
 {name:'no-position child',build:()=>new THREE.Mesh(new THREE.BufferGeometry(),material()),g:'null'},
 ...['null','populated'].flatMap(o=>['null','populated'].map(g=>({name:`InstancedMesh object ${o} geometry ${g}`,build:()=>{const i=new THREE.InstancedMesh(morphMesh(true).geometry,material(),2);for(let j=0;j<2;j++)i.setMatrixAt(j,new THREE.Matrix4().makeTranslation(j*3,1,0));return i;},g,o}))),
 ...[false,true].flatMap(indexed=>['null','populated'].map(b=>({name:`BatchedMesh indexed=${indexed} per-geometry ${b}`,build:()=>rangedBatch(indexed),g:'null',o:'null',b}))),
 ...['null','populated'].map(o=>({name:'SkinnedMesh object '+o,build:skinMesh,g:'null',o})),
 ...['null','populated'].map(g=>({name:'hidden child geometry '+g,build:()=>{const group=new THREE.Group(),hidden=mesh([1,2,1],[100,1,0]);hidden.visible=false;group.add(mesh(),hidden);return group;},g}))
];
for(const row of legacyRows)test('legacy centre matches deep-clone setFromObject: '+row.name,()=>withFixture(d=>{
 const root=d.world.roles.get('hero');root.position.set(-.3,.2,.4);root.rotation.y=.23;root.scale.set(.9,1.1,1);orient(d,{from:[0,1,-4],aim:[0,1,0],fov:50,aspect:1,pan:0,tilt:0,roll:0});
 const populated=()=>new THREE.Box3(V([-3,-1,-2]),V([4,5,2])),sphere=()=>new THREE.Sphere(V([1,2,3]),7);
 root.traverse(o=>{if(o.geometry){o.geometry.boundingBox=row.g==='populated'?populated():null;o.geometry.boundingSphere=row.g==='populated'?sphere():null;}if('boundingBox' in o)o.boundingBox=row.o==='populated'?populated():null;if('boundingSphere' in o)o.boundingSphere=row.o==='populated'?sphere():null;for(const info of o._geometryInfo||[]){info.boundingBox=row.b==='populated'?populated():null;info.boundingSphere=row.b==='populated'?sphere():null;}});
 const before=cacheRows(d),copy=deepClone(root);
 const p=new THREE.Box3().setFromObject(copy).getCenter(new THREE.Vector3()),depth=-p.clone().applyMatrix4(d.camera.matrixWorldInverse).z,ndc=p.project(d.camera),c=d.composition();assert.equal(c.note,undefined);near(c.center,[(ndc.x+1)/2,(ndc.y+1)/2,depth],1e-12);
 const after=cacheRows(d);assert.equal(after.length,before.length);for(let i=0;i<before.length;i++){assert.equal(after[i].identity,before[i].identity,before[i].key+' identity/null');assert.deepEqual(after[i].values,before[i].values,before[i].key+' values');}
},row.build));
test('never-aimed BatchedMesh with keeps per-geometry lazy caches null',()=>withFixture(d=>{
 orient(d,{from:[0,1,-4],aim:[0,1,0],fov:50,aspect:1,pan:0,tilt:0,roll:0});const b=d.world.roles.get('batch');d.shot.s.with=['batch'];for(const info of b._geometryInfo){info.boundingBox=null;info.boundingSphere=null;}const before=cacheRows(d);assert.equal(role(d,'batch').state,'in-front');for(const row of before)assert.equal(row.target[row.key],row.identity,row.key+' lazy cache written');
},undefined,{batch:sparseBatch()}));
test('composition kind/state vocabulary is read from the contract',()=>{
 const graph=structuredClone(contract);graph.review.roles.states[2]='gone';graph.review.roles.kinds[1]='partner';withFixture(d=>{d.shot.s.with=['missing'];const r=role(d,'missing');assert.equal(r.state,'gone');assert.equal(r.kind,'partner');},undefined,undefined,graph);
});
test('despawned with is absent without calling throwing role(); lookAt deduplicates',()=>withFixture(d=>{
 d.shot.s.with=['buddy'];d.shot.s.lookAt='buddy';d.apply({t:0,type:'actor.despawn',role:'buddy'});assert.equal(d.shot,null,'legacy despawn of lookAt ends shot');
 d.apply({t:0,type:'camera.shot',shot:{type:'lockoff',subject:'hero',with:['buddy'],from:'from',frame:'full',ease:'linear',durationSeconds:1}});d.renderer.render(d.world.scene,d.camera);
 assert.throws(()=>d.role('buddy'),/unbound/);assert.equal(role(d,'buddy').state,'absent');assert.doesNotThrow(()=>d.frame(0,1));
 d.shot.s.lookAt='hero';assert.equal(d.composition().roles.length,2);
},undefined,{buddy:mesh()}));
test('a corner exactly on the camera plane still reports straddles-near, not error',()=>withFixture(d=>{
 orient(d,{from:[0,1,-4],aim:[0,1,0],fov:50,aspect:1,pan:0,tilt:0,roll:0});assert.equal(role(d).state,'straddles-near');assert.equal(role(d).rect,undefined);
},()=>mesh([1,2,1],[0,1,-3.5])));
test('empty role on camera plane keeps the legacy centre JSON-safe',()=>withFixture(d=>{
 d.camera.position.set(0,0,0);d.renderer.render(d.world.scene,d.camera);const c=d.composition();assert.equal(c.roles[0].state,'empty');assert.equal(c.center,null);assert.match(c.note,/non-finite/);assert.doesNotThrow(()=>JSON.stringify(c));
},()=>new THREE.Group()));
test('arbitrary thrown values cannot escape either role or legacy probes',()=>{
 for(const value of [null,undefined,{get message(){throw Error('secondary diagnostic failure');}}])withFixture(d=>{const m=d.world.roles.get('hero').children[0];m.isSkinnedMesh=true;m.boundingBox=null;m.getVertexPosition=()=>{throw value;};d.renderer.render(d.world.scene,d.camera);const c=d.composition();assert.equal(c.roles[0].state,'error');assert.equal(typeof c.roles[0].message,'string');assert.equal(c.center,null);assert.equal(typeof c.note,'string');assert.doesNotThrow(()=>d.frame(0,1));});
});
test('bounds computation failure is a per-role error and never fails a frame',()=>withFixture(d=>{
 const broken=mesh();broken.getVertexPosition=()=>{throw Error('broken vertices');};broken.isSkinnedMesh=true;d.world.roles.set('buddy',broken);d.world.scene.add(broken);d.shot.s.with=['buddy'];d.renderer.render(d.world.scene,d.camera);
 const r=role(d,'buddy');assert.equal(r.state,'error');assert.match(r.message,/broken vertices/);assert.doesNotThrow(()=>d.frame(0,1));
}));
