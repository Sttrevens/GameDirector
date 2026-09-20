import test from 'node:test';
import assert from 'node:assert/strict';
import {DraftSession,PendingOperations} from '../src/GameDirector.Workbench/wwwroot/document-state.mjs';
const storage=()=>{const data=new Map();return {getItem:k=>data.get(k)??null,setItem:(k,v)=>data.set(k,v),removeItem:k=>data.delete(k)};};
const doc=(revision,title)=>({id:'film',projectId:'unity',revision,film:{version:1,title}});
test('refresh/reload preserves unsaved edits and their original base',()=>{
  const disk=storage(),a=new DraftSession(disk,'unity/film');
  a.open(doc(1,'original'));a.edit({version:1,title:'my local edit'});
  const b=new DraftSession(disk,'unity/film');
  assert.equal(b.open(doc(2,'remote edit')).title,'my local edit');
  assert.equal(b.document.revision,1);assert.ok(b.dirty);
});
test('in-flight save keeps newer local changes',()=>{
  const session=new DraftSession(storage(),'unity/film');session.open(doc(1,'original'));
  const submitted={version:1,title:'submitted'};session.edit(submitted);
  session.edit({version:1,title:'edited during save'});session.accept(doc(2,'submitted'),submitted);
  assert.equal(session.film.title,'edited during save');assert.equal(session.document.revision,2);assert.ok(session.dirty);
});
test('explicit loading adopts remote contents and clears local dirty state',()=>{
  const session=new DraftSession(storage(),'unity/film');session.open(doc(1,'old'));session.edit({version:1,title:'local'});
  session.replace(doc(2,'remote'));assert.equal(session.film.title,'remote');assert.equal(session.dirty,false);
});
test('unknown result is retried with the same ID even after reload',async()=>{
  const disk=storage(),seen=[];const first=new PendingOperations(disk,()=> 'stable-id');
  await assert.rejects(first.run('produce',{revision:2},async(_,body)=>{seen.push(body.requestId);throw Error('response lost');}));
  const second=new PendingOperations(disk,()=> 'new-id');
  await second.run('produce',{revision:2},async(_,body)=>{seen.push(body.requestId);return {id:'job'};});
  assert.deepEqual(seen,['stable-id','stable-id']);
  await second.run('produce',{revision:3},async(_,body)=>assert.equal(body.requestId,'new-id'));
});
test('changed inputs cannot turn an unknown result into a duplicate operation',async()=>{
  const operations=new PendingOperations(storage(),()=> 'stable-id');
  await assert.rejects(operations.run('save',{film:'original'},async()=>{throw Error('lost');}));
  let sent=false;
  await assert.rejects(operations.run('save',{film:'edited'},async()=>{sent=true;}));
  assert.equal(sent,false);
});
test('a definitive conflict allows a separately reconciled request',async()=>{
  let next=0;const operations=new PendingOperations(storage(),()=>String(++next));
  await assert.rejects(operations.run('save',{revision:1},async()=>{throw Object.assign(Error('conflict'),{status:409});}));
  await operations.run('save',{revision:2},async(_,body)=>assert.equal(body.requestId,'2'));
});
