// Sharing page/role streams, resetting a key, or leaking Math.random must fail.
import test from 'node:test';
import assert from 'node:assert/strict';
const envelope=await import('../packages/gamedirector-three/envelope.mjs').catch(e=>{if(e.code!=='ERR_MODULE_NOT_FOUND')throw e;return {};});
const helpers=await import('../packages/gamedirector-three/helpers.mjs').catch(e=>{if(e.code!=='ERR_MODULE_NOT_FOUND')throw e;return {};});
test('FNV-1a seeds and xorshift32 use literal known vectors; zero hashes become one',()=>{
 assert.equal(typeof envelope.makeEnvelope,'function','deterministic envelope missing');const {fnv1a,seedFor,xorshift32}=envelope;
 assert.equal(fnv1a(''),2166136261);assert.equal(fnv1a('hello'),0x4f9f2cab);assert.equal(seedFor('zero',()=>0),1);
 const r=xorshift32(1);assert.deepEqual([r(),r(),r()],[270369,67634689,2647435461].map(n=>n/4294967296));
});
test('per-key streams persist and ignore other keys and page draw order',()=>{
 const a=envelope.makeEnvelope(),b=envelope.makeEnvelope(),draw=e=>e.scoped('role:hero',()=>Math.random());
 assert.equal(draw(a),draw(b));a.scoped('role:guest',()=>{for(let i=0;i<19;i++)Math.random();});for(let i=0;i<7;i++)a.random();assert.equal(draw(a),draw(b));
 assert.notEqual(draw(a),envelope.makeEnvelope().scoped('role:hero',()=>Math.random()),'key was reset');assert.equal(a.pageDrawCount,7);
});
test('scope restores the caller random on success, nesting, local replacement and throw',()=>{
 const e=envelope.makeEnvelope(),saved=Math.random;try{
  Math.random=e.random;const page=Math.random;e.scoped('outer',()=>{const outer=Math.random;e.scoped('inner',()=>Math.random());assert.equal(Math.random,outer);});assert.equal(Math.random,page);
  assert.throws(()=>e.scoped('throw',()=>{Math.random=()=>.123;throw Error('probe');}),/probe/);assert.equal(Math.random,page);
  Math.random();assert.equal(e.pageDrawCount,1);
 }finally{Math.random=saved;}
});
test('installed envelope puts unscoped draws on the page stream',()=>{
 const saved=Math.random,old=globalThis.gdEnvelope;try{const e=envelope.installEnvelope();assert.equal(globalThis.gdEnvelope,e);Math.random();e.scoped('create',()=>Math.random());Math.random();assert.equal(e.pageDrawCount,2);}finally{Math.random=saved;globalThis.gdEnvelope=old;}
});
test('fixedSteps produces the same integer 60 Hz grid at 6 and 24 fps',()=>{
 assert.equal(typeof helpers.fixedSteps,'function','deterministic helpers missing');const sequences=[];
 for(const fps of [6,24]){const state={},times=[];for(let i=0;i<=3*fps;i++)helpers.fixedSteps(state,i/fps,60,1e-9,(dt,t)=>{assert.equal(dt,1/60);times.push(t);});assert.equal(state.step,180);sequences.push(times);}
 assert.deepEqual(sequences[0],sequences[1]);assert.equal(sequences[0][0],1/60);assert.equal(sequences[0].at(-1),3);
});
test('fixedSteps epsilon, repeats, reset and rewind are explicit',()=>{
 const state={},times=[];const step=t=>helpers.fixedSteps(state,t,60,1e-9,(_,t)=>times.push(t));step(1/60-5e-10);step(1/60);assert.deepEqual(times,[1/60]);assert.throws(()=>step(0),/rewind/);
 helpers.fixedSteps({},1/60,60,0,(_,t)=>times.push(t));assert.deepEqual(times,[1/60,1/60]);for(const args of [[NaN,60,0],[1,0,0],[1,60,-1]])assert.throws(()=>helpers.fixedSteps({},...args,()=>{}),/fixedSteps/);
});
test('segmentAt uses half-open boundaries, explicit gaps and a held final segment',()=>{
 const segments=[{start:0,end:1,clip:'windup'},{start:1,end:1.5,clip:'recover'},{start:2,clip:'idle'}];assert.equal(helpers.segmentAt(segments,-1),null);assert.equal(helpers.segmentAt(segments,.999).clip,'windup');assert.equal(helpers.segmentAt(segments,1).clip,'recover');assert.equal(helpers.segmentAt(segments,1.5),null);assert.equal(helpers.segmentAt(segments,200).clip,'idle');
});
