// Test ownership, including setup failure before node:test can run after hooks.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
const helper=new URL('./test_temp.mjs',import.meta.url);
const {tempDirectory}=await import(helper.href);
const allocate=register=>{assert.equal(typeof tempDirectory,'function','shared temp cleanup is missing');return tempDirectory('gd-node-temp-probe-',register).root;};
test('success cleans nested files and deregisters its exit fallback',()=>{
 let cleanup;const root=allocate(fn=>cleanup=fn),before=process.listenerCount('exit');
 try{fs.mkdirSync(path.join(root,'nested'));fs.writeFileSync(path.join(root,'nested/data'),'owned');}
 finally{cleanup();}
 assert.equal(fs.existsSync(root),false);assert.equal(process.listenerCount('exit'),before-1);cleanup();
});
test('failed setup still has registered cleanup',()=>{
 let cleanup,root;
 try{assert.throws(()=>{root=allocate(fn=>cleanup=fn);fs.writeFileSync(path.join(root,'data'),'owned');throw Error('planted failure');},/planted failure/);}
 finally{cleanup?.();}
 assert.equal(fs.existsSync(root),false);
});
test('aborted module cleans via exit when its after hook cannot run',()=>{
 assert.equal(typeof tempDirectory,'function','shared temp cleanup is missing');
 const child=spawnSync(process.execPath,['--input-type=module','-e',`import {tempDirectory} from ${JSON.stringify(helper.href)}; console.log(tempDirectory('gd-node-temp-probe-',()=>{}).root); throw Error('planted setup failure');`],{encoding:'utf8'});
 const root=child.stdout.trim();assert.notEqual(child.status,0);assert.match(child.stderr,/planted setup failure/);assert.ok(root);assert.equal(fs.existsSync(root),false);
});
