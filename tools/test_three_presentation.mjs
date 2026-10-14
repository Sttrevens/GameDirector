// Real director/Scene; the renderer seam observes routing, ownership and disposal.
import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import * as THREE from '../packages/gamedirector-three/node_modules/three/build/three.module.js';
import {ThreeDirector} from '../packages/gamedirector-three/runtime.js';
import {bundleFixture,changeConfig} from './three_bundle_fixture.mjs';
test('bundled stage query is available to navigation, including the empty query',async()=>{
 const f=bundleFixture();try{const {buildAdapter}=await import('../packages/gamedirector-three/bundle.mjs');assert.equal((await buildAdapter(f)).stageQuery,'fixture=ts');changeConfig(f,c=>delete c.stage);assert.equal((await buildAdapter(f)).stageQuery,'');}finally{f.cleanup();}
});
const contract=JSON.parse(execFileSync('dotnet',['src/GameDirector.Cli/bin/Debug/net8.0/gd.dll','contract','--json'],{encoding:'utf8'}));
const manifest={roles:[{id:'hero'}],locations:[]},compiled={duration:1,orderedCues:[]};
function fixture(look){
 const trace=[],renderer={domElement:{toDataURL:()=> 'data:image/png;base64,cG5n'},shadowMap:{autoUpdate:true},toneMappingExposure:1.7,target:null,size:new THREE.Vector2(),pixelRatio:1,setSize(w,h){this.size.set(w,h);this.domElement.width=Math.floor(w*this.pixelRatio);this.domElement.height=Math.floor(h*this.pixelRatio);},setPixelRatio(ratio){this.pixelRatio=ratio;this.setSize(this.size.x,this.size.y);},getDrawingBufferSize(target){return target.copy(this.size).multiplyScalar(this.pixelRatio).floor();},getPixelRatio(){return this.pixelRatio;},setAnimationLoop(){},getRenderTarget(){return this.target;},render(){trace.push('direct');},dispose(){trace.push('renderer dispose');}};
 const adapter={create(){const scene=new THREE.Scene(),role=new THREE.Group();scene.add(role);return {scene,roles:new Map([['hero',role]])};},animate(){},dispose(){trace.push('world dispose');},...(look?{look:(r,ctx)=>look(r,ctx,trace)}:{})};
 const d=new ThreeDirector(adapter,manifest,contract,{makeRenderer:(w,h)=>{renderer.setSize(w,h);return renderer;}}),saved=globalThis.document;globalThis.document={body:{replaceChildren(){}}};
 return {d,renderer,trace,cleanup(){try{d.end();}finally{globalThis.document=saved;}}};
}
test('presenter is sized before rendering, receives world, reports facts and disposes first',()=>{
 let context;const f=fixture((r,ctx,trace)=>{context=ctx;return {setSize(w,h){trace.push(['size',w,h]);},render(s,c){assert.equal(s,ctx.world.scene);assert.ok(c instanceof THREE.PerspectiveCamera);trace.push('present');},dispose(){trace.push('presenter dispose');}};});
 try{const facts=f.d.begin(compiled,320,180);assert.equal(context.width,320);assert.equal(context.height,180);assert.deepEqual(facts,{post:true,width:320,height:180,canvasWidth:320,canvasHeight:180,pixelRatio:1,rtNull:true,shadowAuto:true,exposure:1.7});f.d.frame(0,6);assert.deepEqual(f.trace,[['size',320,180],'present']);f.renderer.setSize(1280,720);const measured=f.d.presenterFacts();assert.equal(measured.width,1280);assert.equal(measured.height,720);assert.equal(measured.canvasWidth,1280);assert.equal(measured.canvasHeight,720);f.renderer.setPixelRatio(2);assert.equal(f.d.presenterFacts().pixelRatio,2);f.d.end();assert.deepEqual(f.trace.slice(-3),['presenter dispose','world dispose','renderer dispose']);}finally{f.cleanup();}
});
test('null or absent look keeps direct rendering',()=>{
 for(const look of [null,()=>null]){const f=fixture(look);try{const facts=f.d.begin(compiled,64,64);assert.equal(facts.post,false);f.d.frame(0,6);assert.deepEqual(f.trace,['direct']);}finally{f.cleanup();}}
});
test('look cannot start a second animation clock; guard restores on throw',()=>{
 const f=fixture(r=>{r.setAnimationLoop(()=>{});return null;}),loop=f.renderer.setAnimationLoop;try{assert.throws(()=>f.d.begin(compiled,64,64),/setAnimationLoop/);assert.equal(f.renderer.setAnimationLoop,loop);}finally{f.cleanup();}
});
test('bound render target and disabled shadow updates are refused, not merely logged',()=>{
 for(const fault of ['target','shadow']){const f=fixture(r=>({setSize(){},render(){if(fault==='target')r.target={};else r.shadowMap.autoUpdate=false;},dispose(){}}));try{f.d.begin(compiled,64,64);assert.throws(()=>f.d.frame(0,6),fault==='target'?/render target.*null/:/shadowMap.autoUpdate/);}finally{f.cleanup();}}
});
for(const phase of ['look','setSize','frame'])for(const fault of ['resize','pixel ratio'])test('measured '+fault+' mismatch during '+phase+' is refused',()=>{
 const change=r=>{if(fault==='resize')r.setSize(1280,720);else{r.setPixelRatio(2);r.setSize(160,90);}},f=fixture(r=>{if(phase==='look')change(r);return {setSize(){if(phase==='setSize')change(r);},render(){if(phase==='frame')change(r);},dispose(){}};});
 try{const begin=()=>f.d.begin(compiled,320,180);if(phase==='frame'){begin();assert.throws(()=>f.d.frame(0,6),/presenter.*(?:size|pixel ratio) mismatch/);}else assert.throws(begin,/presenter.*(?:size|pixel ratio) mismatch/);}finally{f.cleanup();}
});
test('canvas backing size is checked independently of the renderer size getter',()=>{
 const f=fixture(r=>{r.domElement.width=1280;return null;});try{assert.throws(()=>f.d.begin(compiled,320,180),/size mismatch.*1280/);}finally{f.cleanup();}
});
test('canvas size is measured even when a custom renderer omits its size getter',()=>{
 const f=fixture(r=>{delete r.getDrawingBufferSize;r.domElement.height=720;return null;});try{assert.throws(()=>f.d.begin(compiled,320,180),/size mismatch.*720/);}finally{f.cleanup();}
});
test('runtime isolates create, spawn, role animation, evaluate and look from page draws',async()=>{
 const {makeEnvelope}=await import('../packages/gamedirector-three/envelope.mjs'),e=makeEnvelope(),old=globalThis.gdEnvelope,saved=Math.random,keys=[];
 const wrapped={...e,get pageDrawCount(){return e.pageDrawCount;},scoped(key,fn){keys.push(key);const before=e.pageDrawCount,result=e.scoped(key,fn);assert.equal(e.pageDrawCount,before,'hook drew from page stream');return result;}},f=fixture(()=>{Math.random();return null;});globalThis.gdEnvelope=wrapped;Math.random=e.random;
 f.d.adapter.create=(ctx)=>{Math.random();const scene=new THREE.Scene(),role=new THREE.Group();scene.add(role);return {scene,roles:new Map([['hero',role]])};};f.d.adapter.spawn=()=>{Math.random();return new THREE.Group();};f.d.adapter.animate=()=>Math.random();f.d.adapter.evaluate=()=>Math.random();f.d.manifest.locations=[{id:'start',position:[0,0,0]}];
 try{f.d.begin({duration:1,orderedCues:[{t:0,type:'actor.spawn',role:'guest',location:'start'}]},64,64);assert.ok(keys.includes('create'));assert.ok(keys.includes('spawn:guest'));assert.ok(keys.includes('role:hero'));assert.ok(keys.includes('role:guest'));assert.ok(keys.includes('evaluate'));assert.ok(keys.includes('look'));assert.ok(e.pageDrawCount>0,'camera UUID should draw outside hooks');assert.equal(f.d.pageStreamDrawCount,e.pageDrawCount);}finally{f.cleanup();globalThis.gdEnvelope=old;Math.random=saved;}
});
