// TypeScript bundle gates: removing external THREE, input admission, portable identity, ctx,
// or exact arrival must fail observable bundling/runtime behavior.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {pathToFileURL} from 'node:url';
import {execFileSync} from 'node:child_process';
import * as THREE from '../packages/gamedirector-three/node_modules/three/build/three.module.js';
import {ThreeDirector} from '../packages/gamedirector-three/runtime.js';
import {bundleFixture,changeConfig,engineWithWrongPin,readJSON,writeJSON,repo,engine,reviewRefusals,editEntry,entryFile} from './three_bundle_fixture.mjs';
const build=async f=>(await import('../packages/gamedirector-three/bundle.mjs')).buildAdapter(f);
const versions={engineDigest:'engine',contractDigest:'contract',browserVersion:'browser',playwrightVersion:'playwright',glRenderer:'ANGLE test GPU'};
const identity=async f=>(await build(f)).fingerprint(versions);
const has=(b,label,relative)=>b.inputs.some(i=>i.label===label&&i.path===relative);

test('TS/JSON bundle preserves external ESM and admits only the resolved alias',async()=>{
 const f=bundleFixture();try{const b=await build(f);
  assert.match(b.code,/from\s*["']three["']/);assert.match(b.code,/three\/examples\/jsm/);assert.match(b.code,/three\/addons/);assert.doesNotMatch(b.code,/\brequire\s*\(/);
  assert.ok(has(b,'game','art/cubes.json'),'out-of-src JSON missing from labelled metafile inputs');assert.ok(has(b,'family','shims/device.ts'));assert.equal(has(b,'game','src/device.ts'),false);
  assert.equal(b.options.side,1);assert.equal(b.changedInput(),null);
 }finally{f.cleanup();}
});
const refusals=[
 ['unknown GL backend',f=>changeConfig(f,c=>c.gl='webgpu'),/unknown three.json gl.*webgpu.*default.*swiftshader/],
 ['gameSha mismatch',f=>changeConfig(f,c=>c.gameSha='3333333333333333333333333333333333333333'),/gameSha.*provenance.*sha/],
 ['second THREE',f=>fs.appendFileSync(path.join(f.familyRoot,'adapter.ts'),"\nimport {Scene} from 'game:src/second-three.ts';console.log(Scene);\n"),/second THREE.*node_modules\/three/],
 ['forbidden input',f=>fs.appendFileSync(path.join(f.familyRoot,'adapter.ts'),"\nimport {network} from 'game:src/forbidden.ts';console.log(network);\n"),/forbid.*game:src\/forbidden.ts/],
 ['outside roots',f=>{fs.writeFileSync(path.join(f.root,'outside.ts'),'export const outside=1;');fs.appendFileSync(path.join(f.familyRoot,'adapter.ts'),"\nimport {outside} from '../outside.ts';console.log(outside);\n");},/outside.*(?:snapshot|roots).*outside.ts/],
 ['three version mismatch',f=>changeConfig(f,c=>c.three='0.999.0'),/three version.*0\.170\.0.*0\.999\.0/],
 ['missing engine export',f=>f.threeRoot=path.join(f.gameRoot,'stub-three'),/missing.*PCFSoftShadowMap/],
];
for(const [name,change,reason] of [...refusals,...reviewRefusals])test('startup refusal names '+name,async()=>{const f=bundleFixture();try{change(f);await assert.rejects(build(f),reason);}finally{f.cleanup();}});
test('startup refusal names the locally pinned esbuild version',async()=>{
 const f=bundleFixture();try{const copy=engineWithWrongPin(f),{buildAdapter}=await import(pathToFileURL(path.join(copy,'bundle.mjs')));await assert.rejects(buildAdapter(f),/esbuild version.*pin.*0\.0\.0/);}finally{f.cleanup();}
});
test('resolved declared GL is identity-bearing even when omitted from three.json',async()=>{
 const f=bundleFixture();try{const b=await build(f);assert.equal(b.gl,'default');
  // Supplying the same resolved declaration cannot add a new identity field.
  // This catches dropping identity.gl even though three.json is also hashed.
  assert.equal(b.fingerprint({...versions,gl:'default'}),b.fingerprint(versions),'resolved default GL excluded from identity');
  assert.notEqual(b.fingerprint({...versions,gl:'swiftshader'}),b.fingerprint(versions),'different declared GL aliased');
  changeConfig(f,c=>c.gl='swiftshader');const swift=await build(f);assert.equal(swift.gl,'swiftshader');assert.equal(swift.fingerprint({...versions,gl:'swiftshader'}),swift.fingerprint(versions));assert.notEqual(swift.fingerprint(versions),b.fingerprint(versions));
 }finally{f.cleanup();}
});

test('identity includes out-of-src JSON contents, not mtimes',async()=>{
 const f=bundleFixture();try{const a=await identity(f),file=path.join(f.gameRoot,'art/cubes.json');fs.utimesSync(file,1234567890,1234567890);assert.equal(await identity(f),a,'touch changed identity');writeJSON(file,{hero:255,companion:4517495});assert.notEqual(await identity(f),a,'JSON edit did not change identity');}finally{f.cleanup();}
});
test('identity includes stage query and provenance sha/tree, never provenance repo',async()=>{
 const f=bundleFixture();try{const a=await identity(f),file=path.join(f.gameRoot,'.gamedirector-provenance.json'),saved=readJSON(file);
  writeJSON(file,{...saved,repo:'/a/different/repository'});assert.equal(await identity(f),a,'provenance repo leaked into identity');
  writeJSON(file,{...saved,tree:'4444444444444444444444444444444444444444'});assert.notEqual(await identity(f),a,'tree excluded');
  writeJSON(file,{...saved,sha:'5555555555555555555555555555555555555555'});changeConfig(f,c=>c.gameSha='5555555555555555555555555555555555555555');assert.notEqual(await identity(f),a,'sha excluded');
  writeJSON(file,saved);changeConfig(f,c=>{c.gameSha=saved.sha;c.stage.query='fixture=changed';});assert.notEqual(await identity(f),a,'stage query excluded');
 }finally{f.cleanup();}
});
test('identity survives independent game/family root relocation',async()=>{
 const f=bundleFixture();try{const a=await identity(f),gameRoot=path.join(f.root,'relocated-game'),familyRoot=path.join(f.root,'elsewhere','relocated-family');
  fs.cpSync(f.gameRoot,gameRoot,{recursive:true});fs.cpSync(f.familyRoot,familyRoot,{recursive:true});
  assert.equal(await identity({...f,gameRoot}),a,'game root absolute path in identity');assert.equal(await identity({...f,familyRoot,adapterRoot:path.join(familyRoot,'stage')}),a,'family root absolute path in identity');
  assert.equal(await identity({...f,gameRoot,familyRoot,adapterRoot:path.join(familyRoot,'stage')}),a,'roots changed identity');
 }finally{f.cleanup();}
});
test('identity includes engine/contract/browser/Playwright versions and family files',async()=>{
 const f=bundleFixture();try{const b=await build(f),a=b.fingerprint(versions);for(const key of Object.keys(versions))assert.notEqual(b.fingerprint({...versions,[key]:'changed'}),a,key+' excluded');
  fs.writeFileSync(path.join(f.familyRoot,'notes.txt'),'identity scope');assert.notEqual(await identity(f),a,'family files excluded');
 }finally{f.cleanup();}
});
test('startup digests are immutable; emitted-code edit does not invoke a rebuild',async()=>{
 const f=bundleFixture();try{const {buildCount}=await import('../packages/gamedirector-three/bundle.mjs'),before=buildCount(),b=await build(f),a=b.fingerprint(versions);assert.equal(buildCount(),before+1);
  editEntry(f);assert.equal(b.changedInput(),'family:adapter.ts');assert.equal(b.fingerprint(versions),a);assert.equal(buildCount(),before+1,'freshness invoked esbuild');
  const rebuilt=await build(f);assert.equal(buildCount(),before+2,'counter does not track real invocations');assert.notEqual(rebuilt.code,b.code,'fixture edit must change emitted bytes');
 }finally{f.cleanup();}
});
test('post-build metafile external scan examines every input import',async()=>{
 const {assertExternalImports}=await import('../packages/gamedirector-three/bundle.mjs');
 assert.doesNotThrow(()=>assertExternalImports({inputs:{a:{imports:[{path:'three',external:true}]},b:{imports:[{path:'three/addons/utils/x.js',external:true},{path:'three/examples/jsm/utils/x.js',external:true}]}}}));
 for(const specifier of ['https://example.invalid/code.js','http://example.invalid/code.js','//example.invalid/code.js','three/private.js','three/addons/../../../escape.js'])assert.throws(()=>assertExternalImports({inputs:{a:{imports:[]},b:{imports:[{path:specifier,external:true}]}}}),/external import not allowed/);
});
for(const where of ['stage','vendor'])test('added-file freshness names the '+where+' directory',async()=>{
 const f=bundleFixture();try{if(where==='vendor'){f.threeRoot=path.join(f.root,'vendor');fs.cpSync(path.join(engine,'node_modules/three'),f.threeRoot,{recursive:true});}
  const b=await build(f),directory=where==='stage'?f.adapterRoot:path.join(f.threeRoot,'build');fs.writeFileSync(path.join(directory,'added.js'),'export const added=1;');assert.equal(b.changedInput(),where==='stage'?'family:stage/':'three:build/');
 }finally{f.cleanup();}
});
test('onLoad hashes exactly the compiled bytes and reads each input only once',async()=>{
 const f=bundleFixture(),file=path.join(f.gameRoot,'art/cubes.json'),original=fs.readFileSync;const bytes=original(file),want=crypto.createHash('sha256').update(bytes).digest('hex');let reads=0;
 try{fs.readFileSync=function(p,...args){if(p===fs.realpathSync.native(file))reads++;return original.call(this,p,...args);};const b=await build(f);assert.equal(reads,1);assert.equal(b.inputs.find(r=>r.label==='game'&&r.path==='art/cubes.json').sha256,want);assert.match(b.code,/16729156/);}finally{fs.readFileSync=original;f.cleanup();}
});
for(const kind of ['input','three.json','provenance','family file','three file'])test('startup refuses a deterministic read race in '+kind,async()=>{
 const f=bundleFixture();let file;
 if(kind==='input')file=path.join(f.gameRoot,'art/cubes.json');else if(kind==='three.json')file=path.join(f.adapterRoot,'three.json');else if(kind==='provenance')file=path.join(f.gameRoot,'.gamedirector-provenance.json');else if(kind==='family file'){file=path.join(f.familyRoot,'notes.txt');fs.writeFileSync(file,'family identity');}else{f.threeRoot=path.join(f.root,'vendor');fs.cpSync(path.join(f.gameRoot,'stub-three'),f.threeRoot,{recursive:true});fs.appendFileSync(path.join(f.threeRoot,'build/three.module.js'),'\nexport const PCFSoftShadowMap=2;\n');file=path.join(f.threeRoot,'package.json');}
 const target=fs.realpathSync.native(file),original=fs.readFileSync;let edited=false;
 try{fs.readFileSync=function(p,...args){const bytes=original.call(this,p,...args);if(p===target&&!edited){edited=true;fs.appendFileSync(file,'\n ');}return bytes;};await assert.rejects(build(f),/changed during startup/);assert.equal(edited,true,'race did not fire');}finally{fs.readFileSync=original;f.cleanup();}
});
test('explicit fingerprinted JSONC tsconfig controls real class-field semantics',async()=>{
 const f=bundleFixture();try{fs.writeFileSync(entryFile(f),'class Base {get value(){return 7;} set value(v){}} class Probe extends Base {value=1;} export default new Probe().value;');
  const config=path.join(f.gameRoot,'tsconfig.json');writeJSON(config,{compilerOptions:{useDefineForClassFields:false}});const a=await build(f);assert.equal((await import('data:text/javascript,'+encodeURIComponent(a.code))).default,7);
  fs.writeFileSync(config,'{ // JSONC config\n "compilerOptions":{"useDefineForClassFields":true,},\n}\n');const b=await build(f);assert.equal((await import('data:text/javascript,'+encodeURIComponent(b.code))).default,1);assert.notEqual(a.fingerprint(versions),b.fingerprint(versions));assert.ok(b.metadata.some(r=>r.label==='game'&&r.path==='tsconfig.json'));
 }finally{f.cleanup();}
});
test('family tsconfig wins and extends requires a flattened config',async()=>{
 const f=bundleFixture();try{fs.writeFileSync(entryFile(f),'class Base {get value(){return 7;} set value(v){}} class Probe extends Base {value=1;} export default new Probe().value;');writeJSON(path.join(f.gameRoot,'tsconfig.json'),{compilerOptions:{useDefineForClassFields:false}});const file=path.join(f.familyRoot,'tsconfig.json');writeJSON(file,{compilerOptions:{useDefineForClassFields:true}});const b=await build(f);assert.equal((await import('data:text/javascript,'+encodeURIComponent(b.code))).default,1);writeJSON(file,{extends:'../game/tsconfig.json'});await assert.rejects(build(f),/extends.*flat family tsconfig/);}finally{f.cleanup();}
});
test('post-build re-stat refuses an edit after a family byte snapshot',async()=>{
 const f=bundleFixture(),target=fs.realpathSync.native(entryFile(f)),original=fs.readFileSync;let triggered=false,finished;
 try{fs.readFileSync=function(p,...args){const bytes=original.call(this,p,...args);if(p===target&&!triggered){triggered=true;finished=new Promise((resolve,reject)=>setImmediate(()=>{try{editEntry(f);resolve();}catch(e){reject(e);}}));}return bytes;};await assert.rejects(build(f),/changed during startup.*family:adapter.ts/);assert.equal(triggered,true);await finished;}finally{fs.readFileSync=original;await finished;f.cleanup();}
});
test('ancestor metadata labels survive root relocation across empty directory levels',async()=>{
 const f=bundleFixture();try{writeJSON(path.join(f.root,'package.json'),{name:'ambient',sideEffects:true});const a=await identity(f),gameRoot=path.join(f.root,'deeper','game'),familyRoot=path.join(f.root,'other','deeper','family');fs.cpSync(f.gameRoot,gameRoot,{recursive:true});fs.cpSync(f.familyRoot,familyRoot,{recursive:true});assert.equal(await identity({...f,gameRoot}),a,'ancestor label leaked root depth');assert.equal(await identity({...f,gameRoot,familyRoot,adapterRoot:path.join(familyRoot,'stage')}),a,'ancestor label leaked relocated family depth');}finally{f.cleanup();}
});
test('consulted ancestor package/jsconfig/tsconfig metadata is labelled and fingerprinted',async()=>{
 const f=bundleFixture();try{for(const name of ['package.json','jsconfig.json','tsconfig.json'])writeJSON(path.join(f.root,name),name==='package.json'?{name:'ambient',sideEffects:true}:{compilerOptions:{jsxFactory:'ambientFactory'}});
  const a=await build(f);for(const name of ['package.json','jsconfig.json','tsconfig.json'])assert.ok(a.metadata.some(r=>r.label.startsWith('metadata-')&&r.path.endsWith(name)),name+' missing from identity');
  writeJSON(path.join(f.root,'package.json'),{name:'ambient',sideEffects:false});assert.match(a.changedInput(),/metadata-.*package.json/);assert.notEqual((await build(f)).fingerprint(versions),a.fingerprint(versions));
 }finally{f.cleanup();}
});
const contract=JSON.parse(execFileSync('dotnet',[path.join(repo,'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll'),'contract','--json'],{encoding:'utf8'}));
const renderer=()=>({domElement:{},dispose(){}});
function withDocument(fn){const saved=globalThis.document;globalThis.document={body:{replaceChildren(){}}};try{return fn();}finally{globalThis.document=saved;}}
const movingManifest={roles:[{id:'hero',bindingHint:'cube'},{id:'guest',bindingHint:'cube'}],locations:[{id:'end',position:[2.1,0,0]},{id:'start',position:[0,0,0]}]};
for(const fps of [6,24])test('exact arrivedAt once and moved sum at '+fps+' fps',()=>withDocument(()=>{
 const calls=[],adapter={create(){const scene=new THREE.Scene(),role=new THREE.Group();scene.add(role);return {scene,roles:new Map([['hero',role]])};},animate(role,ctx){calls.push(ctx);}},d=new ThreeDirector(adapter,movingManifest,contract,{makeRenderer:renderer});
 try{d.begin({duration:3,orderedCues:[{t:0,type:'actor.move',role:'hero',to:'end',speed:1}]},64,64);for(let i=0;i<3*fps;i++)d.advance(i,fps);
  const arrivals=calls.filter(c=>c.arrivedAt!==null);assert.equal(arrivals.length,1,'arrival must be reported exactly once');assert.ok(Math.abs(arrivals[0].arrivedAt-2.1)<=1e-9,JSON.stringify(arrivals));
  assert.ok(Math.abs(calls.reduce((sum,c)=>sum+c.moved,0)-2.1)<=1e-9);assert.deepEqual(d.role('hero').position.toArray(),[2.1,0,0]);
 }finally{d.end();}
}));
test('create and spawn receive the same manifest/options ctx; idle moved/arrival are zero/null',()=>withDocument(()=>{
 let created,spawned;const calls=[],options={side:2},adapter={create(ctx){created=ctx;const scene=new THREE.Scene(),role=new THREE.Group();scene.add(role);return {scene,roles:new Map([['hero',role]])};},spawn(world,id,ctx){spawned=ctx;return new THREE.Group();},animate(role,ctx){calls.push(ctx);}},d=new ThreeDirector(adapter,movingManifest,contract,{makeRenderer:renderer,options});
 try{d.begin({duration:1,orderedCues:[{t:0,type:'actor.spawn',role:'guest',location:'start'}]},64,64);assert.deepEqual(created,{manifest:movingManifest,options});assert.equal(spawned,created);for(const c of calls){assert.equal(c.moved,0);assert.equal(c.arrivedAt,null);}}finally{d.end();}
}));
test('begin refuses a real Scene from a second THREE instance',async()=>{
 const second=await import(pathToFileURL(path.join(engine,'node_modules/three/build/three.module.js')).href+'?second-three');assert.notEqual(second.Scene,THREE.Scene);
 withDocument(()=>{const adapter={create:()=>({scene:new second.Scene(),roles:new Map()}),animate(){}},d=new ThreeDirector(adapter,movingManifest,contract,{makeRenderer:renderer});try{assert.throws(()=>d.begin({duration:1,orderedCues:[]},64,64),/instanceof THREE.Scene/);}finally{d.end();}});
});
