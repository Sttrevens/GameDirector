// Pixel metrics must catch channel delta 3 (not delta 2); PNGs decode via ffmpeg.
import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {tempDirectory} from './test_temp.mjs';
const picture=await import('./picture_diff.mjs').catch(e=>{if(e.code!=='ERR_MODULE_NOT_FOUND')throw e;return {};});
test('RGBA mean/max/share count any channel, including alpha, with a strict >2 cutoff',()=>{
 assert.equal(typeof picture.rgbaDiff,'function','picture diff missing');const a=Buffer.alloc(16,100),b=Buffer.from(a);b[0]+=3;b[7]-=2;assert.deepEqual(picture.rgbaDiff(a,b),{share:.25,mean:5/16,max:3});b[7]-=1;assert.deepEqual(picture.rgbaDiff(a,b),{share:.5,mean:6/16,max:3});assert.throws(()=>picture.rgbaDiff(a,b.subarray(4)),/dimensions|length/);
});
test('picture_diff self-check: identical PNG and one channel of one pixel changed by 3',async()=>{
 const f=tempDirectory('gd-picture-diff-');try{const a=Buffer.alloc(16,100),b=Buffer.from(a);b[0]+=3;const files=['a.png','b.png'].map(n=>path.join(f.root,n));for(const [i,data] of [a,b].entries())execFileSync(process.env.GAMEDIRECTOR_FFMPEG||'ffmpeg',['-v','error','-f','rawvideo','-pixel_format','rgba','-video_size','2x2','-i','pipe:0','-frames:v','1',files[i]],{input:data});
  assert.deepEqual(await picture.pictureDiff(files[0],files[0]),{width:2,height:2,share:0,mean:0,max:0});assert.deepEqual(await picture.pictureDiff(...files),{width:2,height:2,share:1/4,mean:3/16,max:3});
 }finally{f.cleanup();}
});
