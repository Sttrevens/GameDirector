// Grid selection must not compare different timestamps or relax either threshold.
import test from 'node:test';
import assert from 'node:assert/strict';
const tool=await import('./test_fps_agreement.mjs').catch(e=>{if(e.code!=='ERR_MODULE_NOT_FOUND')throw e;return {};});
test('shared timestamps use integer cross-products, even for non-multiple rates',()=>{
 assert.equal(typeof tool.sharedFrames,'function','fps tool missing');assert.deepEqual(tool.sharedFrames(6,8,3,4),[{a:0,b:0,time:0},{a:3,b:4,time:1}]);const walk=tool.sharedFrames(18,72,6,24);assert.equal(walk.length,18);assert.deepEqual(walk[13],{a:13,b:52,time:13/6});assert.throws(()=>tool.sharedFrames(2,3,0,6),/fps/);
});
test('both inclusive thresholds are required; NaNs do not pass',()=>{
 assert.equal(tool.passesAgreement({share:.005,mean:.5}),true);assert.equal(tool.passesAgreement({share:.005001,mean:0}),false);assert.equal(tool.passesAgreement({share:0,mean:.500001}),false);assert.equal(tool.passesAgreement({share:NaN,mean:0}),false);
});
