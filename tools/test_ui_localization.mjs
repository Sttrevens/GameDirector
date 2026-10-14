import test from 'node:test';
import assert from 'node:assert/strict';
import {existsSync, readFileSync, readdirSync} from 'node:fs';
const root=new URL('../src/GameDirector.Workbench/wwwroot/',import.meta.url);
const moduleUrl=new URL('ui-catalog.mjs',root);
const load=async()=>{assert.ok(existsSync(moduleUrl),'Workbench UI catalog must exist');return import(moduleUrl);};

test('both catalogs have identical nonempty keys',async()=>{
  const {messages}=await load();
  assert.deepEqual(Object.keys(messages.en).sort(),Object.keys(messages['zh-CN']).sort());
  for(const table of Object.values(messages))for(const [key,value] of Object.entries(table)){
    assert.equal(typeof value,'string',key);assert.ok(value.trim(),key);
  }
});

test('Workbench UI code contains no CJK outside its catalog',()=>{
  const files=readdirSync(root).filter(n=>n!=='ui-catalog.mjs').map(n=>new URL(n,root));
  const server=new URL('../',root);
  files.push(...readdirSync(server).filter(n=>n.endsWith('.cs')).map(n=>new URL(n,server)));
  for(const file of files)assert.doesNotMatch(readFileSync(file,'utf8'),/[\u2e80-\u9fff\uf900-\ufaff\uff00-\uffef]/u,file.pathname);
});

// Only the DOM boundary is doubled: real selection, catalog lookup and renderer run.
for(const [languages,override,want,label] of [
  [['en-US'],null,'en','Assets'],
  [['zh-CN'],null,'zh-CN','项目与资产'],
  [['en-US','zh-TW'],null,'zh-CN','项目与资产'],
  [['zh-CN'],'en','en','Assets'],
  [['en-US'],'zh-CN','zh-CN','项目与资产'],
  [[],null,'en','Assets'],
  [['en-US'],'bogus','en','Assets']
])test(`renders ${languages.join(',')||'no locale'} with override ${override}`,async()=>{
  const {chooseLanguage,renderLanguage}=await load();
  const language=chooseLanguage({languages,storage:()=>({getItem:()=>override})});
  const element={dataset:{i18n:'assets'},textContent:''};
  const document={documentElement:{lang:''},querySelectorAll:selector=>selector==='[data-i18n]'?[element]:[]};
  renderLanguage(document,language);
  assert.equal(document.documentElement.lang,want);assert.equal(element.textContent,label);
});

test('denied storage falls back to browser locales; denied writes retain manual choice',async()=>{
  const {chooseLanguage,saveLanguage}=await load();
  const denied=()=>{throw Error('storage denied');};
  assert.equal(chooseLanguage({languages:['zh-HK'],storage:denied}),'zh-CN');
  assert.equal(chooseLanguage({languages:['en-GB'],storage:denied}),'en');
  assert.equal(saveLanguage('zh-CN',denied),'zh-CN');
  let saved;
  assert.equal(saveLanguage('en',()=>({setItem:(key,value)=>{saved=[key,value];}})),'en');
  assert.deepEqual(saved,['gd.language','en']);
});

test('markup contains only catalog references, including placeholders and accessible names',async()=>{
  const {messages}=await load(),html=readFileSync(new URL('index.html',root),'utf8');
  assert.match(html,/<html lang="en"/);
  assert.match(html,/id="languageToggle"/);
  for(const [,key] of html.matchAll(/data-i18n(?:-[a-z-]+)?="([^"]+)"/g))assert.ok(messages.en[key],key);
  assert.doesNotMatch(html.replace(/<[^>]*>/g,''),/[^\s]/,'No literal text nodes');
  assert.doesNotMatch(html,/\s(?:placeholder|aria-label|title)="[^"]+"/);
});

test('known message codes localize and unknown codes preserve the English diagnostic',async()=>{
  const {serverMessage}=await load();
  assert.equal(serverMessage('performance_ready','Assets and stage are ready.',{},'zh-CN'),'角色、镜头、声音素材与拍摄环境可用。');
  assert.equal(serverMessage('future_code','A newer server diagnostic.',{},'zh-CN'),'A newer server diagnostic.');
  assert.equal(serverMessage(undefined,'Uncoded diagnostic.',{},'zh-CN'),'Uncoded diagnostic.');
});
