import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {DraftSession,PendingOperations,responseResult,formatStorageBytes} from '../src/GameDirector.Workbench/wwwroot/document-state.mjs';
import {MediaInstallPoller} from '../src/GameDirector.Workbench/wwwroot/media-install-state.mjs';
import {browserSession,sessionRequired} from '../src/GameDirector.Workbench/wwwroot/browser-session.mjs';
const storage=()=>{const data=new Map();return {getItem:k=>data.get(k)??null,setItem:(k,v)=>data.set(k,v),removeItem:k=>data.delete(k),get length(){return data.size},key:i=>[...data.keys()][i]};};
const doc=(revision,title)=>({id:'film',projectId:'unity',revision,film:{version:1,title}});
for(const [bytes,expected] of [[0,'0'],[500,'0.5 KB'],[2048,'2.0 KB'],[1572864,'1.5 MB'],[12582912,'12 MB'],[3435973837,'3.2 GB']]){
  test(`storage size ${bytes} bytes displays as ${expected}`,()=>{
    assert.equal(formatStorageBytes(bytes),expected);
  });
}
test('browser exchanges only the one-time code and removes it before fetching',async()=>{
  const events=[];
  await browserSession('http://127.0.0.1:39800/?project=p&code=one-use&document=f#shots',
    {replaceState:(_,title,url)=>events.push(['url',String(url)])},async(path,options)=>{
      events.push(['fetch',path,options]);return {ok:true,status:200,json:async()=>({authenticated:true})};
    });
  assert.equal(events[0][1],'http://127.0.0.1:39800/?project=p&document=f#shots');
  assert.equal(events[1][1],'/api/session');assert.equal(events[1][2].method,'POST');
  assert.equal(events[1][2].credentials,'same-origin');assert.deepEqual(JSON.parse(events[1][2].body),{code:'one-use'});
  assert.equal(events[1][2].headers.Authorization,undefined);
});
test('an expired code leaves a clean URL and gives one actionable session instruction',async()=>{
  let url;
  await assert.rejects(browserSession('http://127.0.0.1:39800/?code=expired',
    {replaceState:(_,title,next)=>{url=String(next);}},async()=>({ok:false,status:401,json:async()=>({error:'expired',code:'unauthorized',retryable:false})})),error=>error.message===sessionRequired&&error.status===401);
  assert.equal(url,'http://127.0.0.1:39800/');
});
test('reload without a code checks the cookie session instead of minting or retaining secrets',async()=>{
  const history={replaceState:()=>assert.fail('no code to strip')};
  await browserSession('http://127.0.0.1:39800/?project=p',history,async(path,options)=>{
    assert.equal(path,'/api/state');assert.equal(options.credentials,'same-origin');assert.equal(options.body,undefined);
    return {ok:true,status:200,json:async()=>({projects:[],jobs:[]})};
  });
  await assert.rejects(browserSession('http://127.0.0.1:39800/',history,async()=>({ok:false,status:401,json:async()=>({code:'unauthorized'})})),error=>error.message===sessionRequired);
});

test('app.js without credentials displays just the session instruction and never starts the UI',async()=>{
  const saved=Object.fromEntries(['fetch','document','location','history'].map(key=>[key,Object.getOwnPropertyDescriptor(globalThis,key)]));
  let lines=[],requests=[];
  try{
    globalThis.document={createElement:()=>({textContent:''}),body:{replaceChildren:line=>{lines.push(line.textContent);}}};
    globalThis.location={href:'http://127.0.0.1:39800/?code=invalid'};
    globalThis.history={replaceState:(_,title,url)=>{globalThis.location.href=String(url);}};
    globalThis.fetch=async(path,options)=>{requests.push(path);return {ok:false,status:401,json:async()=>({code:'unauthorized',retryable:false})};};
    const appUrl=new URL('../src/GameDirector.Workbench/wwwroot/app.js',import.meta.url);
    const source=readFileSync(appUrl,'utf8').replace(/from '\.\/([^']+)'/g,(_,file)=>`from '${new URL(file,appUrl).href}'`);
    await assert.rejects(import('data:text/javascript;base64,'+Buffer.from(source).toString('base64')),error=>error.message===sessionRequired);
    assert.deepEqual(lines,[sessionRequired]);assert.deepEqual(requests,['/api/session']);
    assert.equal(globalThis.location.href,'http://127.0.0.1:39800/');
  }finally{for(const [key,descriptor] of Object.entries(saved)){if(descriptor)Object.defineProperty(globalThis,key,descriptor);else delete globalThis[key];}}
});

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

test('error parsing reads the status before the body, even an empty one',async()=>{
  let statusRead=false,statusBeforeBody=false;
  const empty={ok:false,get status(){statusRead=true;return 400;},json:async()=>{statusBeforeBody=statusRead;throw new SyntaxError('Unexpected end of JSON input');}};
  await assert.rejects(responseResult(empty),error=>{assert.equal(error.status,400);assert.equal(error.retryable,false);assert.equal(error.code,undefined);return true;});
  assert.equal(statusBeforeBody,true);
  const typed={ok:false,status:503,json:async()=>({error:'engine down',code:'engine_unavailable',retryable:true})};
  await assert.rejects(responseResult(typed),error=>{assert.equal(error.status,503);assert.equal(error.code,'engine_unavailable');assert.equal(error.retryable,true);return true;});
  assert.deepEqual(await responseResult({ok:true,status:200,json:async()=>({value:1})}),{value:1});
});

test('an empty-body 400 clears the pending key instead of sticking forever',async()=>{
  const disk=storage();const operations=new PendingOperations(disk,()=> 'stable-id');
  const send=async()=>responseResult({ok:false,status:400,json:async()=>{throw new SyntaxError('Unexpected end of JSON input');}});
  await assert.rejects(operations.run('save',{revision:1},send),error=>error.status===400);
  assert.equal(disk.getItem('gd.operation.v1.save'),null);
  await operations.run('save',{revision:2},async(_,body)=>{assert.equal(body.requestId,'stable-id');return {ok:1};});
});

test('a retryable 503 keeps the pending key and the retry reuses the operation identity',async()=>{
  const disk=storage();const operations=new PendingOperations(disk,()=> 'stable-id');
  const down=async()=>responseResult({ok:false,status:503,json:async()=>({error:'engine down',code:'engine_unavailable',retryable:true})});
  await assert.rejects(operations.run('produce',{revision:2},down),error=>error.retryable===true);
  assert.notEqual(disk.getItem('gd.operation.v1.produce'),null);
  const seen=[];
  await operations.run('produce',{revision:2},async(_,body)=>{seen.push(body.requestId);return {id:'job'};});
  assert.deepEqual(seen,['stable-id']);
  assert.equal(disk.getItem('gd.operation.v1.produce'),null);
});

test('pending operations are isolated per tab like drafts',async()=>{
  const tabA=storage(),tabB=storage();
  const a=new PendingOperations(tabA,()=> 'a-id'),b=new PendingOperations(tabB,()=> 'b-id');
  await assert.rejects(a.run('save',{revision:1},async()=>{throw Error('lost');}));
  assert.equal(b.pending('save'),null); // tab B never replays tab A's operation
  assert.deepEqual(b.entries(),[]);
  assert.deepEqual(a.entries().map(([path])=>path),['save']);
  await b.run('save',{revision:1},async(_,body)=>{assert.equal(body.requestId,'b-id');return {};});
});

test('discard releases a pending entry explicitly',async()=>{
  const disk=storage();const operations=new PendingOperations(disk,()=> 'first-id');
  await assert.rejects(operations.run('projects/p/direct',{brief:'x'},async()=>{throw Error('lost');}));
  assert.equal(operations.entries().length,1);
  operations.discard('projects/p/direct');
  assert.deepEqual(operations.entries(),[]);
  const fresh=new PendingOperations(disk,()=> 'second-id');
  await fresh.run('projects/p/direct',{brief:'x'},async(_,body)=>{assert.equal(body.requestId,'second-id');return {};});
});

test('project-scoped direct keys still call the real direct endpoint and retain the target document',async()=>{
  let next=0;const operations=new PendingOperations(storage(),()=>String(++next));
  await assert.rejects(operations.run('projects/a/direct',{projectId:'a',brief:'x'},async(path)=>{assert.equal(path,'direct');throw Error('lost');},{documentId:'film-a'}));
  assert.deepEqual(operations.entries(),[['projects/a/direct',{projectId:'a',brief:'x'},{documentId:'film-a'}]]);
  await operations.run('projects/b/direct',{projectId:'b',brief:'y'},async(path,body)=>{assert.equal(path,'direct');assert.equal(body.requestId,'2');return {};},{documentId:'film-b'});
  assert.ok(operations.pending('projects/a/direct')); // another project does not consume it
});

test('explicit non-retryable failures release keys even for 5xx; transport 408 and 429 keep them',async()=>{
  const operations=new PendingOperations(storage(),()=> 'stable');
  for(const status of [412,422,500,503]){
    await assert.rejects(operations.run('save',{title:'bad'},async()=>responseResult({ok:false,status,json:async()=>({error:'cannot retry',code:'failure',retryable:false})})));
    assert.equal(operations.pending('save'),null);
  }
  for(const status of [408,429]){
    await assert.rejects(operations.run('save',{title:'later'},async()=>responseResult({ok:false,status,json:async()=>{throw new SyntaxError('empty');}})));
    assert.ok(operations.pending('save'));
    operations.discard('save');
  }
});

test('unknown HTTP failures default to non-retryable, including HTML 500 pages',async()=>{
  const operations=new PendingOperations(storage());
  await assert.rejects(operations.run('save',{},async()=>responseResult({ok:false,status:500,json:async()=>{throw new SyntaxError('HTML');}})),error=>error.retryable===false&&error.status===500);
  assert.equal(operations.pending('save'),null);
});

test('media status polls live output until a final receipt, without replaying a start',async()=>{
  const rendered=[],pending=[],running={running:{optionId:'fake',outputTail:['installing']},lastReceipt:null};
  let reads=0;
  const poller=new MediaInstallPoller(async()=>{reads++;return {running:null,lastReceipt:{state:'completed'}};},s=>rendered.push(s),e=>{throw e;},fn=>{pending.push(fn);return fn;},()=>{});
  poller.open(running);assert.equal(rendered[0].running.outputTail[0],'installing');assert.equal(pending.length,1);
  await pending.shift()();assert.equal(reads,1);assert.equal(rendered[1].lastReceipt.state,'completed');assert.equal(pending.length,0);
});
test('closing the install dialog stops polling, not the server install, and ignores stale replies',async()=>{
  const rendered=[];let finish;
  const poller=new MediaInstallPoller(()=>new Promise(resolve=>{finish=resolve;}),s=>rendered.push(s),e=>{throw e;},()=>1,()=>{});
  poller.open({running:{optionId:'first'}});const read=poller.refresh();poller.stop();
  poller.open({running:{optionId:'new'}});finish({running:null,lastReceipt:{state:'completed'}});await read;
  assert.equal(rendered.at(-1).running.optionId,'new');poller.stop();
});
test('a transient status failure keeps install polling alive',async()=>{
  const pending=[],errors=[];
  const poller=new MediaInstallPoller(async()=>{throw Error('lost status');},()=>{},e=>errors.push(e.message),fn=>{pending.push(fn);return fn;},()=>{});
  poller.open({running:{optionId:'fake'}});await pending.shift()();
  assert.deepEqual(errors,['lost status']);assert.equal(pending.length,1);poller.stop();
});

test('discard followed by a fresh operation is not erased by the old in-flight completion',async()=>{
  let next=0,finish;const operations=new PendingOperations(storage(),()=>String(++next));
  const old=operations.run('save',{title:'old'},()=>new Promise(resolve=>{finish=resolve;}));
  operations.discard('save');
  await assert.rejects(operations.run('save',{title:'new'},async()=>{throw Error('lost');}));
  finish({revision:1});await old;
  assert.deepEqual(operations.pending('save'),{title:'new'});
});
