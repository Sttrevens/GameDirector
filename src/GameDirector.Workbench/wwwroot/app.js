import {messages,chooseLanguage,saveLanguage,renderLanguage,t,serverMessage} from './ui-catalog.mjs';
import {DraftSession, PendingOperations, responseResult, formatStorageBytes} from './document-state.mjs';
// Select before authentication so session instructions use the same language.
let uiLanguage=chooseLanguage({languages:globalThis.navigator?.languages});
// Saving is reserved for the manual toggle; automatic selection never persists.

import {MediaInstallPoller} from './media-install-state.mjs';
import {browserSession} from './browser-session.mjs';
if(document.documentElement)renderLanguage(document,uiLanguage);
let refreshTimer;
function requireSession(){clearInterval(refreshTimer);const line=document.createElement('p');line.textContent=t('sessionRequired',{},uiLanguage);document.body.replaceChildren(line);}
try{await browserSession(location.href,history,fetch);}catch(error){requireSession();throw error;}
renderLanguage(document,uiLanguage);
const $ = id => document.getElementById(id);
$('languageToggle').onclick=async()=>{
  uiLanguage=saveLanguage(uiLanguage==='en'?'zh-CN':'en');
  renderLanguage(document,uiLanguage);
  // Repaint only the view; never reload, save, or replace the local film draft.
  renderAssets();renderJobs();renderShots();renderAudio();renderPending();renderConnection();documentStatus();renderFolders();
  if(latestDocument)showConflict(latestDocument);
  if(readinessReport)renderReadiness(readinessReport);
  if(visibleJob)renderVideoStatus(visibleJob);
  if(installStatus)renderMediaInstall(installStatus);
  $('notice').hidden=true;
  try{await refreshDocuments();await showStorage();await catalogStatus();await renderRecords();
    if(!$('settingsDialog').open){await providerStatus();await decisionStatus();}
  }catch(error){notice(error.message,true);}
};
let state = { projects: [], jobs: [] }, selected = '', film = null, noticeTimer, provider = null;
let media = [];
// Product catalog (engines, film defaults, limits) is served by the Workbench;
// the UI never restates ports, sizes or formats. Static HTML values are only
// the pre-fetch fallback.
let catalog = { engines: [], defaults: { frameRate: 24, width: 1280, height: 720 }, limits: { maxAudioBytes: 5*1024*1024, maxScriptBytes: 2*1024*1024 } };
function engineProfile(id){return catalog.engines.find(e=>e.id===id);}
function engineEndpoint(engine){return engineProfile(engine)?.defaultEndpoint||'';}
const urlSelection=new URLSearchParams(location.search);
selected=urlSelection.get('project')||'';
let documentId=urlSelection.get('document')||'default', draftSession=null, latestDocument=null, pendingProposal=null;
// Pending operations live per tab (like drafts): a shared localStorage key let
// one tab replay another tab's save and adopt its revision as the baseline.
const operations=new PendingOperations(sessionStorage);
function documentPath(projectId=selected,id=documentId){return `projects/${projectId}/documents/${id}`;}
function directPath(projectId=selected){return `projects/${projectId}/direct`;}
function rememberSelection(){const url=new URL(location.href);url.searchParams.set('project',selected);url.searchParams.set('document',documentId);history.replaceState(null,'',url);}
function documentStatus(){if(!draftSession)return;$('documentStatus').textContent=t('revisionStatus', [draftSession.document.revision, draftSession.dirty?t('unsaved'):t('saved')]);}
function rememberDraft(){if(draftSession){draftSession.edit(film);documentStatus();}}
let folderView = null, folderGeneration = 0;
const checkingProjects = new Set(), connectionErrors = new Map();
let endpointEdited = false;
const labels = new Proxy({}, {get:(_, key)=>t('state.'+key)});
const actionLabels = new Proxy({}, {get:(_, key)=>t('action.'+key)});
async function api(path, body, method=body===undefined?'GET':'POST') {
  let response;
  try{response=await fetch('/api/' + path, method==='GET' ? {} : { method, headers:{'Content-Type':'application/json'}, body:JSON.stringify(body??{}) });}
  catch(cause){const error=Error(cause?.message||t('networkError'));error.status=undefined;error.code=undefined;error.retryable=true;throw error;} // no response: may have committed
  try{return await responseResult(response);}catch(error){if(error.status===401)requireSession();throw error;}
}
function notice(message, error = false) { if(!$('notice'))return; clearTimeout(noticeTimer); $('notice').textContent = message; $('notice').className = error ? 'error' : ''; $('notice').hidden = false; noticeTimer = setTimeout(() => $('notice').hidden = true, error ? 15000 : 7000); }
function action(id, fn) { $(id).addEventListener('click', async e => { e.preventDefault(); const b=$(id); b.disabled=true; try { await fn(); } catch(e) { notice(e.message,true); } finally { b.disabled=false;renderPending(); } }); }
function node(tag, text, className) { const e=document.createElement(tag); if(text!==undefined)e.textContent=text; if(className)e.className=className; return e; }
function localizedNode(tag,key,fallback){const e=node(tag,fallback??t(key));if(Object.hasOwn(messages.en,key))e.dataset.i18n=key;return e;}
function project() { return state.projects.find(p=>p.id===selected); }
function needFilm() { if(!selected || !film)throw Error(t('needFilm')); return film; }

async function refresh() {
  state=await api('state'); const p=$('project'); const ids=JSON.stringify(state.projects.map(x=>[x.id,x.name]));
  if(p.dataset.ids!==ids){p.replaceChildren(new Option(t('selectProject'),''),...state.projects.map(x=>new Option(x.name,x.id)));p.dataset.ids=ids;p.value=selected;}
  if(!selected&&state.projects.length){selected=state.projects[0].id;p.value=selected;await loadDraft();}
  renderAssets(); renderJobs();renderConnection();renderPending();await refreshDocuments();
}
let draftGeneration=0;
async function loadDraft(){
  const id=selected,docId=documentId,generation=++draftGeneration;
  const value=await api(documentPath(id,docId));
  if(selected!==id||documentId!==docId||generation!==draftGeneration)return;
  draftSession=new DraftSession(sessionStorage,id+'/'+docId);latestDocument=value;
  rememberSelection();
  setFilm(draftSession.open(value),false);documentStatus();showConflict(value);
}
async function refreshDocuments(){
  if(!selected||!draftSession)return;
  const projectId=selected,docId=documentId,list=await api(`projects/${projectId}/documents`);
  if(selected!==projectId||documentId!==docId)return;
  const picker=$('document');picker.replaceChildren(...list.map(d=>{const o=new Option(d.damaged?t('damagedDocument', [d.id]):d.title||d.id,d.id);if(d.damaged){o.disabled=true;o.title=d.error||'';}return o;}));
  if(!list.some(d=>d.id===docId))picker.add(new Option(t('unsavedFilm'),docId));picker.value=docId;
  const value=await api(documentPath(projectId,docId));
  if(selected!==projectId||documentId!==docId)return;
  latestDocument=value;renderPending();
  if(value.revision!==draftSession.document.revision){
    if(!draftSession.dirty){setFilm(draftSession.replace(value),false);documentStatus();}
    else showConflict(value);
  }
}
function pendingLabel(path){
  if(path.endsWith('/produce'))return t('confirmProduction');
  if(path.endsWith('/direct'))return t('confirmDirect');
  return t('confirmSave');
}
// Every pending entry of this tab is listed — including direct generation —
// each with an explicit retry (same requestId) or discard.
function renderPending(){
  const panel=$('pendingOperations');if(!panel)return;panel.replaceChildren();
  for(const [path,input,context] of operations.entries()){
    const row=node('div');row.append(node('span',pendingLabel(path)+' · '+path));
    const retry=node('button',t('retry')),drop=node('button',t('discard'));
    retry.onclick=async()=>{retry.disabled=true;drop.disabled=true;
      try{
        const session=draftSession,target=documentPath(),result=await operations.run(path,input,api,context);
        if(path.endsWith('/direct')&&result?.film){pendingProposal={projectId:input.projectId,docId:context?.documentId||'default',film:result.film};sessionStorage.setItem('gd.proposal',JSON.stringify(pendingProposal));$('applyProposal').hidden=false;}
        else if(input.film&&path===target&&session&&session===draftSession){setFilm(session.accept(result,input.film),false);documentStatus();}
        await refresh();notice(t('operationConfirmed'));
      }catch(error){notice(error.message,true);}finally{renderPending();}};
    drop.title=t('discardHint');
    drop.onclick=()=>{operations.discard(path);renderPending();};
    row.append(retry,drop);panel.append(row);
  }
  panel.hidden=!panel.children.length;
}
function showConflict(value){
  const conflict=draftSession&&value.revision!==draftSession.document.revision&&draftSession.dirty;
  $('documentConflict').hidden=!conflict;
  if(conflict){latestDocument=value;$('conflictText').textContent=t('conflictText', [value.revision]);}
}
async function saveDocument(){
  const current=draftSession,projectId=selected,docId=documentId,submitted=structuredClone(needFilm());
  current.edit(submitted);
  if(!current.dirty&&current.document.revision>0)return current.document;
  try{
    const saved=await operations.run(documentPath(projectId,docId),{expectedRevision:current.document.revision,film:submitted},api);
    const local=current.accept(saved,submitted);
    if(current===draftSession){setFilm(local,false);documentStatus();$('documentConflict').hidden=true;}
    return saved;
  }catch(error){if(current===draftSession&&error.detail?.current)showConflict(error.detail.current);throw error;}
}
async function produceDocument(preview=false){
  const projectId=selected,docId=documentId,saved=await saveDocument();
  const path=documentPath(projectId,docId);
  const readiness=await api(path+'/readiness',{revision:saved.revision,preview});
  if(selected===projectId&&documentId===docId)renderReadiness(readiness);
  if(!readiness.ready)throw Error(t('notReady'));
  const job=await operations.run(path+'/produce',{revision:saved.revision,preview},api);
  await refresh();notice(t('productionQueued'));return job;
}
let readinessReport=null,visibleJob=null,installStatus=null;
function renderReadiness(report){readinessReport=report;const panel=$('readiness');panel.replaceChildren();for(const check of report.checks){const row=node('p',(check.ready?'✓ ':t('needsAttention'))+serverMessage(check.code,check.message,check.messageArgs));if(check.action)row.append(node('small',serverMessage(check.actionCode,check.action,check.messageArgs)));if(check.actionId==='install-media'){const b=node('button',t('installTools'));b.onclick=()=>openMediaInstall();row.append(b);}panel.append(row);}panel.hidden=false;}
const mediaInstallPoller=new MediaInstallPoller(()=>api('media/install'),renderMediaInstall,e=>$('mediaInstallStatus').textContent=e.message);
function renderMediaInstall(status){
  installStatus=status;const running=status.running,last=status.lastReceipt;
  for(const b of $('mediaOptions').querySelectorAll('button'))b.disabled=!!running;
  $('mediaInstallCancel').hidden=!running;$('mediaInstallCancel').disabled=false;
  $('mediaInstallStatus').textContent=running?t('installRunning', [running.optionId, new Date(running.startedUtc).toLocaleTimeString()])
    :last?.state==='completed'?(serverMessage(last.receipt?.noteCode,last.receipt?.note,last.receipt?.messageArgs)||t('installComplete')):last?.state==='cancelled'?t('installCancelled'):last?.error||'';
  const tail=running?.outputTail||last?.outputTail||[];$('mediaInstallTail').textContent=tail.join('\n');$('mediaInstallTail').hidden=!tail.length;
}
$('mediaDialog').addEventListener('close',()=>mediaInstallPoller.stop());
action('mediaInstallCancel',async()=>{await api('media/install',{},'DELETE');await mediaInstallPoller.refresh();});
async function openMediaInstall(){
  const status=await api('media/install');const list=$('mediaOptions');list.replaceChildren();
  for(const o of status.options){const e=node('div',undefined,'media-option');e.append(localizedNode('b','server.'+o.labelCode,serverMessage(o.labelCode,o.label)),localizedNode('p','server.'+o.detailCode,serverMessage(o.detailCode,o.detail)),localizedNode('small','server.'+o.licenseCode,serverMessage(o.licenseCode,o.license)));
    let descriptor;if(o.kind==='userBundle'){descriptor=node('input');descriptor.type='text';descriptor.placeholder=t('descriptorPlaceholder');descriptor.setAttribute('data-i18n-placeholder','descriptorPlaceholder');descriptor.setAttribute('aria-label',t('descriptorLabel'));descriptor.setAttribute('data-i18n-aria-label','descriptorLabel');e.append(descriptor);}
    const b=localizedNode('button','install');b.onclick=async()=>{b.disabled=true;
      try{const r=await api('media/install',{optionId:o.id,descriptorPath:descriptor?.value.trim()||null});if($('mediaDialog').open)mediaInstallPoller.open({running:{...r,outputTail:[]}});}
      catch(e){await mediaInstallPoller.refresh();$('mediaInstallStatus').textContent=e.message;}};
    e.append(b);list.append(e);}
  if(!status.options.length)list.append(node('p',t('noInstallOptions'),'muted'));
  $('mediaDialog').showModal();mediaInstallPoller.open(status);
}
function setFilm(value,remember=true){film=value;if(film){film.audio??=[];film.subtitles??=[];} $('filmTitle').value=film?.title||''; $('script').value=film?JSON.stringify(film,null,2):'';renderShots();renderAudio();if(remember)rememberDraft();}
function syncFilm(){if(!film)return; film.title=$('filmTitle').value; $('script').value=JSON.stringify(film,null,2);updateMeta();rememberDraft();}
function updateMeta(){const shots=film?.scenes.flatMap(s=>s.shots)||[];$('filmMeta').textContent=film?t('filmMeta', [film.scenes.length, shots.length, shots.reduce((n,s)=>n+s.end-s.start,0).toFixed(1), film.width, film.height]):t('filmIntro');}
function renderAssets(){
  const p=project(), m=p?.manifest, search=$('search').value.toLowerCase(); $('connection').textContent=m?t('assetsLoaded', [p.name]):p?t('waitingEngine', [p.name]):t('notConnected');
  const roles=m?.roles||[], inventory=p?.inventory?.assets||[];
  $('assetCount').textContent=t('assetCount', [roles.length, inventory.length]);
  const list=$('assets');list.replaceChildren();
  for(const r of roles){const actor=m.actors.find(a=>a.id===r.defaultActor);const clips=actor?.clips||[];if(![r.id,...clips].join(' ').toLowerCase().includes(search))continue;
    const e=node('div',undefined,'asset'), name=node('div',undefined,'asset-name');name.append(node('span',r.displayName||r.id),node('span',r.presentAtStart?t('bound'):t('spawnable'),'chip'));e.append(name,node('small',r.id));const chips=node('div',undefined,'chips');for(const c of clips)chips.append(node('span',c,'chip'));e.append(chips);list.append(e);}
  for(const l of m?.locations||[]){if(!l.id.toLowerCase().includes(search))continue;const e=node('div',undefined,'asset');e.append(node('span',l.id,'asset-name'),node('small',t('cameraAnchor')));list.append(e);}
  const found=inventory.filter(a=>(a.name+' '+a.id+' '+a.kind).toLowerCase().includes(search));
  for(const a of found.slice(0,100)){const e=node('div',undefined,'asset');e.append(node('span',a.name,'asset-name'),node('small',t('discoveredAsset', [a.kind])),node('small',a.id));list.append(e);}
  if(found.length>100)list.append(node('p',t('moreAssets', [found.length-100]),'muted'));
  if(!list.children.length)list.append(node('p',p?t('noMatchingAssets'):t('connectForAssets'),'muted'));
}
function renderShots(){const list=$('shots');list.replaceChildren();updateMeta();fillCameraShots();if(!film){list.append(node('p',t('shotsEmpty'),'muted'));return;}
  for(const scene of film.scenes)for(const shot of scene.shots){const e=node('article',undefined,'shot');const top=node('div',undefined,'shot-top');top.append(node('span',shot.id),node('span',scene.id));e.append(top);
    const purpose=node('input');purpose.value=shot.purpose;purpose.setAttribute('aria-label',shot.id+t('shotPurposeSuffix'));purpose.onchange=()=>{shot.purpose=purpose.value;syncFilm();};e.append(purpose);
    const m=project()?.manifest;for(const [field,title,items] of [['subject',t('role'),m?.roles.map(x=>x.id)||[]],['from',t('anchor'),m?.locations.map(x=>x.id)||[]],['type',t('cameraMove'),m?.shotTypes||[]],['frame',t('framing'),m?.frameTypes||[]]]){const label=node('label',title), select=node('select');select.setAttribute('aria-label',shot.id+' '+title);for(const x of new Set([shot.camera[field],...items].filter(Boolean)))select.add(new Option(x,x));select.value=shot.camera[field];select.onchange=()=>{shot.camera[field]=select.value;syncFilm();};label.append(select);e.append(label);}
    const times=node('div',undefined,'times');for(const [key,name] of [['start',t('performanceStart')],['end',t('performanceEnd')]]){const l=node('label',name),input=node('input');input.type='number';input.min=0;input.step=1/film.frameRate;input.value=shot[key];input.setAttribute('aria-label',shot.id+' '+name);input.onchange=()=>{shot[key]=Number(input.value);syncFilm();};l.append(input);times.append(l);}e.append(times);list.append(e);}
}
function renderVideoStatus(job){$('viewerStatus').textContent=t('videoReady',[job.request.film.title,job.request.film.width,job.request.film.height]);}
function showVideo(job){visibleJob=job;$('video').src=`/api/jobs/${job.id}/video`;$('video').hidden=false;$('emptyScreen').hidden=true;renderVideoStatus(job);$('download').href=$('video').src;$('download').download=job.request.film.title+'.mp4';$('download').hidden=false;}
function renderJobs(){const list=$('jobs');list.replaceChildren();const jobs=state.jobs.filter(j=>j.request.projectId===selected);for(const j of jobs){const e=node('div',undefined,'job'),body=node('div');body.append(node('b',`${j.request.film.title} · ${labels[j.state]||j.state}`),node('p',t('jobProgress', [j.completedShots, j.totalShots, j.reusedShots, j.progress])));e.append(body);
    // The server states which actions this job allows; the UI only renders them.
    for(const a of j.actions||[]){const button=node('button',actionLabels[a]||a);button.onclick=async()=>{button.disabled=true;try{if(a==='openVideo')showVideo(j);else await api(`jobs/${j.id}/${a}`,{});await refresh();}catch(e){notice(e.message,true);}finally{button.disabled=false;}};e.append(button);}list.append(e);}
  if(!jobs.length)list.append(node('p',t('jobsEmpty'),'muted'));
}
async function providerStatus(){provider=await api('provider');$('providerBadge').textContent=provider.configured?t('apiDirector', [provider.model]):t('externalDirector');if(provider.configured){$('apiEndpoint').value=provider.endpoint;$('model').value=provider.model;}$('apiKey').placeholder=provider.hasKey?t('keySaved'):t('optionalKey');}
async function direct(render){
  if(!selected||!draftSession)throw Error(t('needProject'));
  if(!provider?.configured){$('settingsDialog').showModal();return;}
  const projectId=selected,docId=documentId,session=draftSession,signature=JSON.stringify(film);
  notice(t('directing'));
  const r=await operations.run(directPath(projectId),{projectId,brief:$('brief').value,currentFilm:film,render:false},api,{documentId:docId});
  if(session!==draftSession||JSON.stringify(film)!==signature){
    pendingProposal={projectId,docId,film:r.film};sessionStorage.setItem('gd.proposal',JSON.stringify(pendingProposal));
    $('applyProposal').hidden=false;notice(t('proposalHeld'));return;
  }
  setFilm(r.film);await saveDocument();
  if(render)await produceDocument();else notice(t('proposalSaved'));
}
async function switchDocument(id){documentId=id;draftSession=null;setFilm(null,false);$('documentConflict').hidden=true;await loadDraft();await refreshDocuments();}
$('project').onchange=async()=>{
  selected=$('project').value;documentId='default';draftSession=null;visibleJob=null;setFilm(null,false);
  $('video').pause();$('video').removeAttribute('src');$('video').hidden=true;$('emptyScreen').hidden=false;$('download').hidden=true;$('viewerStatus').textContent=t('noCut');
  try{if(selected)await loadDraft();await loadMedia();renderAssets();renderJobs();renderConnection();await refreshDocuments();await catalogStatus();await renderRecords();}catch(e){notice(e.message,true);}
};
$('document').onchange=()=>switchDocument($('document').value).catch(e=>notice(e.message,true));
action('newDocument',async()=>{if(!selected)throw Error(t('needSelection'));await switchDocument(crypto.randomUUID());setFilm({version:1,title:t('newFilmTitle'),frameRate:catalog.defaults.frameRate,width:catalog.defaults.width,height:catalog.defaults.height,scenes:[],audio:[],subtitles:[]});await saveDocument();await refreshDocuments();});
action('reloadDocument',async()=>{const value=await api(documentPath());setFilm(draftSession.replace(value),false);$('documentConflict').hidden=true;documentStatus();});
action('forkDocument',async()=>{const local=structuredClone(needFilm());await switchDocument(crypto.randomUUID());setFilm(local);await saveDocument();await refreshDocuments();notice(t('forkSaved'));});
action('applyProposal',async()=>{pendingProposal??=JSON.parse(sessionStorage.getItem('gd.proposal')||'null');if(!pendingProposal)return;if(selected!==pendingProposal.projectId||documentId!==pendingProposal.docId)throw Error(t('proposalWrongFilm'));setFilm(pendingProposal.film);pendingProposal=null;sessionStorage.removeItem('gd.proposal');$('applyProposal').hidden=true;notice(t('proposalLoaded'));});
$('applyProposal').hidden=!sessionStorage.getItem('gd.proposal');
window.addEventListener('beforeunload',event=>{if(draftSession?.dirty){event.preventDefault();event.returnValue='';}});
$('search').oninput=renderAssets;$('filmTitle').oninput=syncFilm;
function folderButton(text, path, className='',key=null) { const button=node('button',text,className);if(key)button.dataset.i18n=key;button.type='button';button.onclick=()=>browseFolders(path);return button; }
function renderFolders(){
  const list=$('folderList');list.replaceChildren();if(!folderView)return;
  const query=$('folderSearch').value.toLocaleLowerCase();
  for(const folder of folderView.directories.filter(d=>d.name.toLocaleLowerCase().includes(query))){const button=folderButton(folder.name,folder.path,'folder-item');button.setAttribute('aria-label',t('openFolder')+folder.name);list.append(button);}
  if(!list.children.length)list.append(node('p',query?t('noMatchingFolders'):t('noSubfolders'),'muted'));
  if(folderView.truncated)list.append(node('p',t('foldersTruncated'),'muted'));
}
async function browseFolders(path){
  const generation=++folderGeneration;folderView=null;$('chooseProjectFolder').disabled=true;$('folderUp').disabled=true;$('folderError').hidden=true;$('folderSelection').textContent='';$('folderBreadcrumbs').replaceChildren();$('folderList').replaceChildren(node('p',t('loadingFolders'),'muted'));$('folderSearch').value='';
  try{const value=await api('folders',{path:path||null});if(generation!==folderGeneration||!$('folderDialog').open)return;folderView=value;
    $('folderSelection').textContent=value.path;$('chooseProjectFolder').disabled=false;$('folderUp').disabled=!value.parent;
    const shortcuts=$('folderShortcuts');shortcuts.replaceChildren(folderButton(t('home'),value.home,'','home'));
    for(const root of value.roots.filter(r=>r!==value.home))shortcuts.append(folderButton(root,root));
    for(const p of state.projects.filter(p=>p.sourceRoot))shortcuts.append(folderButton(p.name,p.sourceRoot));
    const crumbs=$('folderBreadcrumbs');let target=value.path,items=[];
    while(target){items.unshift(target);const trimmed=target.replace(/[\\/]+$/,'');const split=Math.max(trimmed.lastIndexOf('/'),trimmed.lastIndexOf('\\'));if(split<0)break;let parent=trimmed.slice(0,split)||'/';if(/^[A-Za-z]:$/.test(parent))parent+='\\';if(parent===target)break;target=parent;}
    for(const item of items){const name=item.replace(/[\\/]+$/,'').split(/[\\/]/).pop()||item;crumbs.append(folderButton(name,item));}
    renderFolders();
  }catch(error){if(generation!==folderGeneration||!$('folderDialog').open)return;$('folderList').replaceChildren();$('folderError').textContent=error.message;$('folderError').hidden=false;}
}
action('browseProjectFolder',()=>{folderView=null;$('folderShortcuts').replaceChildren(folderButton(t('home'),null,'','home'));$('folderDialog').showModal();return browseFolders($('sourceRoot').value);});
$('folderDialog').addEventListener('close',()=>{folderGeneration++;folderView=null;});
$('folderSearch').oninput=renderFolders;
$('folderUp').onclick=()=>{if(folderView?.parent)return browseFolders(folderView.parent);};
action('chooseProjectFolder',()=>{if(!folderView)return;$('sourceRoot').value=folderView.path;if(!$('projectName').value)$('projectName').value=folderView.name;$('folderDialog').close();$('sourceRoot').focus();});
function openProjectForm(p=null){
  $('connectForm').reset();$('connectError').hidden=true;$('projectId').readOnly=!!p;endpointEdited=!!p;
  $('connectTitle').dataset.i18n=p?'projectSettings':'connectTitle';$('submitProject').dataset.i18n=p?'saveSettings':'addProject';
  $('connectTitle').textContent=t($('connectTitle').dataset.i18n);$('submitProject').textContent=t($('submitProject').dataset.i18n);
  if(p){$('projectId').value=p.id;$('projectName').value=p.name;$('engine').value=p.engine;$('sourceRoot').value=p.sourceRoot||'';$('endpoint').value=p.endpoint;}
  else $('endpoint').value=engineEndpoint($('engine').value)||$('endpoint').value;
  $('connectDialog').showModal();
}
function renderConnection(){
  const p=project(),box=$('projectConnection');box.hidden=!p;if(!p)return;
  const checking=checkingProjects.has(p.id),error=connectionErrors.get(p.id);
  $('projectConnectionTitle').textContent=checking?t('projectChecking'):error||!p.manifest?t('projectWaiting'):t('assetSnapshot');
  const next=p.engine==='Unity'?t('unityConnectNext'):t('engineConnectNext');
  $('projectConnectionDetail').textContent=checking?t('checkingAssets'):error?error+' '+next:!p.manifest?next:t('connectionOptions');
  $('retryConnection').disabled=checking;$('retryConnection').textContent=checking?t('checking'):t('checkConnection');
}
async function checkProjectConnection(id){
  if(checkingProjects.has(id))return;checkingProjects.add(id);connectionErrors.delete(id);renderConnection();
  try{await api(`projects/${id}/connect`,{});}
  catch(error){connectionErrors.set(id,error.message);}
  finally{checkingProjects.delete(id);try{await refresh();if(selected===id)renderShots();}catch(error){notice(error.message,true);}renderConnection();}
}
$('endpoint').oninput=()=>{endpointEdited=true;};
$('engine').onchange=()=>{if(!endpointEdited){const endpoint=engineEndpoint($('engine').value);if(endpoint)$('endpoint').value=endpoint;}};
$('retryConnection').onclick=()=>{if(selected)checkProjectConnection(selected);};
action('editProjectConnection',()=>openProjectForm(project()));
action('connectButton',()=>openProjectForm());action('settingsButton',()=> $('settingsDialog').showModal());
document.querySelectorAll('[data-close]').forEach(b=>b.onclick=()=>$(b.dataset.close).close());
$('connectForm').onsubmit=async e=>{
  e.preventDefault();const button=$('submitProject');if(button.disabled)return;button.disabled=true;$('connectError').hidden=true;
  try{const p=await api('projects',{id:$('projectId').value,name:$('projectName').value,engine:$('engine').value,endpoint:$('endpoint').value,sourceRoot:$('sourceRoot').value||null});
    if(selected!==p.id){documentId='default';draftSession=null;}selected=p.id;connectionErrors.delete(p.id);$('connectDialog').close();await refresh();$('project').value=selected;await loadDraft();await loadMedia();notice(t('projectSaved'));checkProjectConnection(p.id);
  }catch(error){if($('connectDialog').open){$('connectError').textContent=error.message;$('connectError').hidden=false;}else notice(error.message,true);}
  finally{button.disabled=false;}
};
$('settingsForm').onsubmit=async e=>{e.preventDefault();try{
  // Keys are write-only; a blank field retains the key for the same endpoint.
  if($('apiEndpoint').value.trim()){const body={endpoint:$('apiEndpoint').value.trim(),model:$('model').value.trim()};if($('apiKey').value)body.apiKey=$('apiKey').value;await api('provider',body);$('apiKey').value='';await providerStatus();}
  if($('jevEndpoint').value.trim()){const body={endpoint:$('jevEndpoint').value.trim(),model:$('jevModel').value.trim()};if($('jevApiKey').value)body.apiKey=$('jevApiKey').value;await api('decisions/provider',body);}
  // Blank thresholds mean uncalibrated (null), not zero. Persist only 0-1 values.
  const policies={version:1,semanticReview:{enabled:$('jevReviewOn').checked,minConfidence:null}};
  for(const [minId,onId,key] of [['jevPerfMin','jevPerfOn','performanceMatch'],['jevCamMin','jevCamOn','cameraChoice'],['jevAudioMin','jevAudioOn','audioMatch'],['jevRankMin','jevRankOn','filmRanking']]){
    const raw=$(minId).value.trim(),value=raw===''?null:Number(raw);
    if(value!==null&&(!Number.isFinite(value)||value<0||value>1))throw Error(t('invalidThreshold'));
    policies[key]={enabled:$(onId).checked,minConfidence:value};}
  await api('decisions/policies',policies);
  $('jevApiKey').value='';await decisionStatus();$('settingsDialog').close();notice(t('connectionSaved'));}catch(e){notice(e.message,true);}};
action('disconnectProvider',async()=>{await api('provider',{endpoint:'',model:'',apiKey:''});await providerStatus();$('settingsDialog').close();});
action('disconnectDecisions',async()=>{await api('decisions/provider',{endpoint:'',model:'',apiKey:''});$('jevEndpoint').value='';$('jevModel').value='';await decisionStatus();});
action('refresh',async()=>{if(!selected)throw Error(t('connectFirst'));await checkProjectConnection(selected);});
action('starter',async()=>{if(!selected)throw Error(t('connectFirst'));const id=selected;const value=await api(`projects/${id}/starter`);if(selected===id)setFilm(value);});
action('importButton',()=>$('importFile').click());$('importFile').onchange=async()=>{try{const file=$('importFile').files[0];if(!file)return;if(file.size>catalog.limits.maxScriptBytes)throw Error(t('scriptLimit',[Math.round(catalog.limits.maxScriptBytes/1048576)]));const f=JSON.parse(await file.text());if(f.version!==1||!Array.isArray(f.scenes))throw Error(t('importFilmVersion'));setFilm(f);notice(t('scriptLoaded'));}catch(e){notice(e.message,true);}finally{$('importFile').value='';}};
action('applyScript',()=>{const value=JSON.parse($('script').value);if(value.version!==1||!Array.isArray(value.scenes))throw Error(t('scriptVersion'));setFilm(value);});
action('save',async()=>{await saveDocument();notice(t('filmSaved'));});
action('validate',async()=>{const path=documentPath(),saved=await saveDocument();renderReadiness(await api(path+'/readiness',{revision:saved.revision}));});
for(const id of ['preview','render'])action(id,()=>produceDocument(id==='preview'));
action('direct',()=>direct(false));action('directRender',()=>direct(true));$('studioUrl').textContent=location.origin;
function storageText(s){const fmt=formatStorageBytes,u=s.usage;return t('storageRoot',[s.root])+(u?t('storageUsage',[fmt(u.cache),fmt(u.jobs),fmt(u.documents),fmt(u.media),fmt(u.other),u.reclaimable?t('reclaimable',[fmt(u.reclaimable)]):'']):'');}
async function showStorage(){$('storageInfo').textContent=storageText(await api('storage'));}
action('pruneStorage',async()=>{if(!confirm(t('pruneConfirm')))return;const r=await api('storage/prune',{});notice(t('pruned', [r.removed.length, (r.bytesFreed/1048576).toFixed(1)]));await showStorage();});
renderPending();
try{
  catalog=await api('health');
  const engineSelect=$('engine');engineSelect.replaceChildren(...catalog.engines.map(e=>new Option(e.label,e.id)));
  if(catalog.limits.maxAudioFormats)$('audioFile').accept=catalog.limits.maxAudioFormats.join(',');
  await refresh();if(selected&&!draftSession)await loadDraft();await refreshDocuments();await providerStatus();await loadMedia();await showStorage();
  await decisionStatus();await catalogStatus();await renderRecords();
}catch(e){notice(e.message,true);}
refreshTimer=setInterval(()=>refresh().catch(error=>{if(error.status!==401&&$('connection'))$('connection').textContent=t('connectionLost');}),3000);

async function loadMedia(){const id=selected;if(!id){media=[];renderAudio();return;}const items=await api(`projects/${id}/media`);if(selected===id){media=items;renderAudio();}}
function renderAudio(){
  const select=$('audioMedia');select.replaceChildren(...media.map(m=>new Option(t('mediaDuration', [m.name, m.duration.toFixed(1)]),m.id)));
  const rows=$('audioTracks');rows.replaceChildren();
  for(const cue of film?.audio||[]){const row=node('div',undefined,'audio-track');row.append(node('b',media.find(m=>m.id===cue.mediaId)?.name||cue.mediaId));
    const controls=node('div',undefined,'audio-controls');
    const bus=node('select');bus.setAttribute('aria-label',cue.id+t('busSuffix'));for(const [id,name] of [['dialogue',t('dialogue')],['music',t('music')],['sfx',t('sfx')]])bus.add(new Option(name,id));bus.value=cue.bus;bus.onchange=()=>{cue.bus=bus.value;syncFilm();};controls.append(bus);
    for(const [key,title] of [['at',t('filmTime')],['sourceStart',t('sourceStart')],['duration',t('duration')],['volume',t('volume')],['fadeIn',t('fadeIn')],['fadeOut',t('fadeOut')]]){const label=node('label',title),input=node('input');input.type='number';input.min=0;input.step=.01;input.value=cue[key]??0;input.setAttribute('aria-label',cue.id+' '+title);input.onchange=()=>{cue[key]=Number(input.value);syncFilm();};label.append(input);controls.append(label);}
    const remove=node('button',t('remove'));remove.onclick=()=>{film.audio=film.audio.filter(a=>a!==cue);syncFilm();renderAudio();};controls.append(remove);row.append(controls);rows.append(row);
  }
  $('captions').value=(film?.subtitles||[]).map(c=>`${c.start} | ${c.end} | ${c.text}`).join('\n');
}
action('audioImportButton',()=>{if(!selected)throw Error(t('needSelection'));$('audioFile').click();});
$('audioFile').onchange=async()=>{try{const id=selected,file=$('audioFile').files[0];if(!file)return;if(file.size>catalog.limits.maxAudioBytes)throw Error(t('audioLimit',[Math.round(catalog.limits.maxAudioBytes/1048576)]));const base64=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(reader.result.split(',')[1]);reader.onerror=reject;reader.readAsDataURL(file);});await api(`projects/${id}/media`,{name:file.name,base64});if(selected===id)await loadMedia();notice(t('audioImported'));}catch(e){notice(e.message,true);}finally{$('audioFile').value='';}};
action('addAudio',()=>{const f=needFilm(),m=media.find(a=>a.id===$('audioMedia').value);if(!m)throw Error(t('needAudio'));f.audio??=[];f.audio.push({id:'sound-'+crypto.randomUUID(),mediaId:m.id,bus:'sfx',at:0,sourceStart:0,duration:Math.min(m.duration,f.scenes.flatMap(s=>s.shots).reduce((n,s)=>n+s.end-s.start,0)),volume:1,fadeIn:0,fadeOut:0});syncFilm();renderAudio();});
action('applyCaptions',()=>{const f=needFilm();f.subtitles=$('captions').value.split('\n').filter(l=>l.trim()).map(l=>{const [a,b,...text]=l.split('|');if(!text.length||!a.trim()||!b.trim()||!Number.isFinite(Number(a))||!Number.isFinite(Number(b)))throw Error(t('captionError'));return {start:Number(a),end:Number(b),text:text.join('|').trim()};});syncFilm();notice(t('captionsApplied'));});
action('exportButton',()=>{const f=needFilm(),url=URL.createObjectURL(new Blob([JSON.stringify(f,null,2)],{type:'application/json'})),a=node('a');a.href=url;a.download=f.title+'.film.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});

// ---- Decision assistant (Jev option decisions) ----
let decisions=null;
const outcomeLabels=new Proxy({}, {get:(_, key)=>t('outcome.'+key)});
const taskKeys={'performance-match':'performanceMatch','camera-choice':'cameraChoice','audio-match':'audioMatch','film-ranking':'filmRank','semantic-review':'semanticReview'};
const taskLabels=new Proxy({}, {get:(_, key)=>taskKeys[key]?t(taskKeys[key]):key});
async function decisionStatus(){decisions=await api('decisions/provider');const p=decisions.policies;
  $('decisionStatus').textContent=decisions.configured?t('decisionConfigured', [decisions.model]):t('decisionUnconfigured');
  if(decisions.configured){$('jevEndpoint').value=decisions.endpoint||'';$('jevModel').value=decisions.model||'';}
  $('jevApiKey').placeholder=decisions.hasKey?t('keySaved'):'';
  for(const [key,on,min] of [['performanceMatch','jevPerfOn','jevPerfMin'],['cameraChoice','jevCamOn','jevCamMin'],['audioMatch','jevAudioOn','jevAudioMin'],['filmRanking','jevRankOn','jevRankMin']]){$(on).checked=!!p[key].enabled;$(min).value=p[key].minConfidence??'';}
  $('jevReviewOn').checked=!!p.semanticReview?.enabled;
}
function renderDecisionOutcome(panel,record){
  panel.replaceChildren();const e=node('div',undefined,'decision-result');
  e.append(node('div',`${outcomeLabels[record.outcome]||record.outcome}${record.selectedCandidateId?' · '+record.selectedCandidateId:''}`,'outcome'),node('div',record.reason||'','reason'));
  for(const a of record.answers||[]){
    if(a.choice&&a.confidence!=null)e.append(node('small',t('choiceConfidence', [a.choice, (a.confidence*100).toFixed(0)])));
    if(a.noul!=null)e.append(node('small',t('probability', [a.questionId, (a.noul*100).toFixed(0)])));
    if(a.score!=null)e.append(node('small',t('answerScore', [a.questionId, a.score, a.confidence!=null?t('confidenceSuffix',[(a.confidence*100).toFixed(0)]):''])));
    const probs=Object.entries(a.probabilities||{}).sort((x,y)=>y[1]-x[1]);
    for(const [id,p] of probs.slice(0,6)){const row=node('div',undefined,'prob');row.append(node('span',id.length>24?id.slice(0,24)+'…':id));const bar=node('div',undefined,'bar');bar.style.width=Math.max(2,Math.round(p*120))+'px';row.append(bar,node('small',(p*100).toFixed(0)+'%'));e.append(row);}
  }
  panel.append(e);return e;
}
async function catalogStatus(){
  if(!selected){$('catalogStatus').textContent='';return;}
  const s=await api(`projects/${selected}/catalog`);
  $('catalogStatus').textContent=s.present?(s.fresh?t('catalogReady', [s.performances, s.evidenceBacked]):t('catalogUnavailable', [(s.errors||[]).slice(0,2).join('; ')||t('manifestChanged')])):t('catalogMissing');
}
action('catalogImport',()=>{if(!selected)throw Error(t('needSelection'));$('catalogFile').click();});
action('catalogExport',async()=>{if(!selected)throw Error(t('needSelection'));const catalog=await api(`projects/${selected}/catalog/content`);const url=URL.createObjectURL(new Blob([JSON.stringify(catalog,null,2)],{type:'application/json'}));const link=node('a');link.href=url;link.download=`${selected}.performances.json`;document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);});
$('catalogFile').onchange=async()=>{try{const file=$('catalogFile').files[0];if(!file)return;const catalog=JSON.parse(await file.text());const s=await api(`projects/${selected}/catalog`,catalog);await catalogStatus();notice(s.fresh?t('catalogImported'):t('catalogNeedsAttention'));}catch(e){notice(e.message,true);}finally{$('catalogFile').value='';}};
action('catalogScaffold',async()=>{if(!selected)throw Error(t('needSelection'));const r=await api(`projects/${selected}/catalog/scaffold`,{});await catalogStatus();notice(r.scaffolded===false?t('catalogExists'):t('catalogScaffolded'));});
action('perfMatch',async()=>{
  if(!selected)throw Error(t('needSelection'));const intent=$('perfIntent').value.trim();if(!intent)throw Error(t('needPerformanceIntent'));
  notice(t('askingDecision'));const r=await api(`projects/${selected}/decisions/performance-match`,{requestId:crypto.randomUUID(),intent});
  renderDecisionOutcome($('perfResults'),r);await renderRecords();notice(outcomeLabels[r.outcome]||r.outcome);
});
function fillCameraShots(){const sel=$('cameraShot');if(!sel)return;const before=sel.value;sel.replaceChildren();for(const scene of film?.scenes||[])for(const shot of scene.shots)sel.add(new Option(`${shot.id} · ${(shot.purpose||'').slice(0,18)}`,shot.id));if([...sel.options].some(o=>o.value===before))sel.value=before;}
action('cameraSuggest',async()=>{
  const f=needFilm();const shotId=$('cameraShot').value;if(!shotId)throw Error(t('needShot'));
  const list=await api(`projects/${selected}/decisions/camera-choice/suggest`,{shotId,film:f});
  $('cameraCandidates').value=JSON.stringify(list.filter(c=>c.compiles).map(c=>({id:c.id,camera:c.camera})),null,2);
  notice(t('camerasSuggested', [list.filter(c=>c.compiles).length]));
});
action('cameraDecide',async()=>{
  const f=needFilm();const shotId=$('cameraShot').value;if(!shotId)throw Error(t('needShot'));
  const intent=$('cameraIntent').value.trim();if(!intent)throw Error(t('needCameraIntent'));
  let candidates=null;const text=$('cameraCandidates').value.trim();
  if(text){candidates=JSON.parse(text);if(!Array.isArray(candidates)||!candidates.length)throw Error(t('invalidCameraCandidates'));}
  notice(t('askingDecision'));
  const r=await api(`projects/${selected}/decisions/camera-choice`,{requestId:crypto.randomUUID(),shotId,intent,documentId,documentRevision:draftSession?.document?.revision??null,film:f,candidates});
  const e=renderDecisionOutcome($('cameraResults'),r);
  if(r.outcome==='selected'&&r.proposal){const b=node('button',t('loadCamera'));b.onclick=()=>{setFilm(structuredClone(r.proposal));notice(t('cameraLoaded'));};e.append(b);}
  await renderRecords();
});
action('audioMatch',async()=>{
  if(!selected)throw Error(t('needSelection'));const intent=$('audioIntent').value.trim();if(!intent)throw Error(t('needAudioIntent'));
  notice(t('askingDecision'));const r=await api(`projects/${selected}/decisions/audio-match`,{requestId:crypto.randomUUID(),intent,bus:$('audioBus').value});
  const e=renderDecisionOutcome($('audioResults'),r);
  if(r.outcome==='selected'&&r.selectedCandidateId){const m=media.find(a=>a.id===r.selectedCandidateId);if(m){const b=node('button',t('addNamedAudio', [m.name]));b.onclick=()=>{const f=needFilm();f.audio??=[];f.audio.push({id:'sound-'+crypto.randomUUID(),mediaId:m.id,bus:$('audioBus').value,at:0,sourceStart:0,duration:Math.min(m.duration,f.scenes.flatMap(s=>s.shots).reduce((n,s)=>n+s.end-s.start,0)),volume:1,fadeIn:0,fadeOut:0});syncFilm();renderAudio();notice(t('audioAdded'));};e.append(b);}}
  await renderRecords();
});
async function renderRecords(){
  if(!selected)return;const list=await api(`projects/${selected}/decisions`);const panel=$('decisionRecords');panel.replaceChildren();
  for(const r of list.slice(0,12)){const e=node('div',undefined,'decision-record');e.append(node('span',`${taskLabels[r.task]||r.task} · ${outcomeLabels[r.outcome]||r.outcome}${r.selectedCandidateId?' · '+r.selectedCandidateId.slice(0,30):''}`),node('small',`${new Date(r.createdAt).toLocaleString()} · ${r.reason||''}`));panel.append(e);}
  if(!list.length)panel.append(node('p',t('noRecords'),'muted'));
}
action('recordsRefresh',renderRecords);
$('audioMedia').onchange=()=>{const m=media.find(a=>a.id===$('audioMedia').value);$('mediaDescription').value=m?.description||'';};
action('saveMediaDescription',async()=>{const m=media.find(a=>a.id===$('audioMedia').value);if(!m)throw Error(t('needImportedAudio'));await api(`projects/${selected}/media/${m.id}`,{description:$('mediaDescription').value||null});await loadMedia();notice(t('descriptionSaved'));});

// ---- Film comparison and semantic review (proposal preparation, not picture review) ----
function renderRanking(panel,record){
  const e=renderDecisionOutcome(panel,record);
  if(record.ranking?.length){const table=node('div');
    record.ranking.forEach((r,i)=>{const row=node('div',undefined,'prob');
      row.append(node('span',`#${i+1} ${r.candidateId}`));
      row.append(node('small',(r.score!=null?t('score', [r.score]):t('unscored'))+(r.confidence!=null?t('confidenceSuffix',[(r.confidence*100).toFixed(0)]):'')));
      table.append(row);if(r.evidence)table.append(node('small',r.evidence));});
    e.append(table);}
  return e;
}
action('rankCompare',async()=>{
  if(!selected)throw Error(t('needSelection'));const brief=$('rankBrief').value.trim();if(!brief)throw Error(t('needRankBrief'));
  const candidates=[];const f=film;
  if(f)candidates.push({id:'current',rationale:t('currentDraft'),film:f});
  const text=$('rankCandidates').value.trim();
  if(text){const external=JSON.parse(text);if(!Array.isArray(external))throw Error(t('invalidRankCandidates'));for(const c of external)candidates.push(c);}
  if(!candidates.length)throw Error(t('noCandidates'));
  notice(t('askingDecision'));const r=await api(`projects/${selected}/decisions/film-ranking`,{requestId:crypto.randomUUID(),brief,candidates});
  renderRanking($('rankResults'),r);await renderRecords();notice(outcomeLabels[r.outcome]||r.outcome);
});
action('reviewRun',async()=>{
  if(!selected)throw Error(t('needSelection'));const subject=$('reviewSubject').value.trim();if(!subject)throw Error(t('needReviewText'));
  const checks=JSON.parse($('reviewChecks').value||'[]');if(!Array.isArray(checks)||!checks.length)throw Error(t('invalidReviewChecks'));
  notice(t('askingDecision'));const r=await api(`projects/${selected}/decisions/semantic-review`,{requestId:crypto.randomUUID(),subject,label:film?t('reviewFilmLabel', [film.title]):null,checks});
  renderDecisionOutcome($('reviewResults'),r);await renderRecords();notice(outcomeLabels[r.outcome]||r.outcome);
});
