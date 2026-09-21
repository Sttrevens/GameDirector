import {DraftSession, PendingOperations} from './document-state.mjs';
const $ = id => document.getElementById(id);
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
const operations=new PendingOperations(localStorage);
function documentPath(projectId=selected,id=documentId){return `projects/${projectId}/documents/${id}`;}
function rememberSelection(){const url=new URL(location.href);url.searchParams.set('project',selected);url.searchParams.set('document',documentId);history.replaceState(null,'',url);}
function documentStatus(){if(!draftSession)return;$('documentStatus').textContent=`版本 ${draftSession.document.revision} · ${draftSession.dirty?'有未保存修改':'已保存'}`;}
function rememberDraft(){if(draftSession){draftSession.edit(film);documentStatus();}}
let folderView = null, folderGeneration = 0;
const checkingProjects = new Set(), connectionErrors = new Map();
let endpointEdited = false;
const labels = { Queued:'等待制作', Running:'制作中', Verified:'已完成 · 待审片', Failed:'制作失败', Interrupted:'制作中断', Cancelled:'已取消' };
async function api(path, body) {
  const response = await fetch('/api/' + path, body === undefined ? {} : { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(body) });
  const data = await response.json(); if (!response.ok) {const error=Error(data.error || `请求失败 (${response.status})`);error.status=response.status;error.detail=data;throw error;} return data;
}
function notice(message, error = false) { clearTimeout(noticeTimer); $('notice').textContent = message; $('notice').className = error ? 'error' : ''; $('notice').hidden = false; noticeTimer = setTimeout(() => $('notice').hidden = true, error ? 15000 : 7000); }
function action(id, fn) { $(id).addEventListener('click', async e => { e.preventDefault(); const b=$(id); b.disabled=true; try { await fn(); } catch(e) { notice(e.message,true); } finally { b.disabled=false; } }); }
function node(tag, text, className) { const e=document.createElement(tag); if(text!==undefined)e.textContent=text; if(className)e.className=className; return e; }
function project() { return state.projects.find(p=>p.id===selected); }
function needFilm() { if(!selected || !film)throw Error('请先接入项目并建立或导入拍摄脚本。'); return film; }

async function refresh() {
  state=await api('state'); const p=$('project'); const ids=JSON.stringify(state.projects.map(x=>[x.id,x.name]));
  if(p.dataset.ids!==ids){p.replaceChildren(new Option('选择或接入项目',''),...state.projects.map(x=>new Option(x.name,x.id)));p.dataset.ids=ids;p.value=selected;}
  if(!selected&&state.projects.length){selected=state.projects[0].id;p.value=selected;await loadDraft();}
  renderAssets(); renderJobs();renderConnection();await refreshDocuments();
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
  const picker=$('document');picker.replaceChildren(...list.map(d=>new Option(d.title||d.id,d.id)));
  if(!list.some(d=>d.id===docId))picker.add(new Option('未保存的作品',docId));picker.value=docId;
  const value=await api(documentPath(projectId,docId));
  if(selected!==projectId||documentId!==docId)return;
  latestDocument=value;renderPending();
  if(value.revision!==draftSession.document.revision){
    if(!draftSession.dirty){setFilm(draftSession.replace(value),false);documentStatus();}
    else showConflict(value);
  }
}
function renderPending(){
  const panel=$('pendingOperations');panel.replaceChildren();
  for(const [path,label] of [[documentPath(),'确认上次保存结果'],[documentPath()+'/produce','确认上次制作结果']]){
    const input=operations.pending(path);if(!input)continue;
    const button=node('button',label);button.onclick=async()=>{button.disabled=true;try{const session=draftSession;const result=await operations.run(path,input,api);if(input.film&&session===draftSession){setFilm(session.accept(result,input.film),false);documentStatus();}await refresh();notice('已确认上次操作；你的本地修改仍保留。');}catch(error){notice(error.message,true);}finally{button.disabled=false;}};panel.append(button);
  }
  panel.hidden=!panel.children.length;
}
function showConflict(value){
  const conflict=draftSession&&value.revision!==draftSession.document.revision&&draftSession.dirty;
  $('documentConflict').hidden=!conflict;
  if(conflict){latestDocument=value;$('conflictText').textContent=`另一处已保存版本 ${value.revision}。你的修改保留在本窗口。可先导出本地脚本，再载入新版；也可另存为新作品。`;}
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
  if(!readiness.ready)throw Error('还有拍摄条件需要处理，请查看准备检查。');
  const job=await operations.run(path+'/produce',{revision:saved.revision,preview},api);
  await refresh();notice('任务已进入制作；关闭页面后可继续查看同一任务。');return job;
}
function renderReadiness(report){const panel=$('readiness');panel.replaceChildren();for(const check of report.checks){const row=node('p',(check.ready?'✓ ':'待处理 · ')+check.message);if(check.action)row.append(node('small',check.action));if(check.actionId==='install-media'){const b=node('button','安装编码组件…');b.onclick=()=>openMediaInstall();row.append(b);}panel.append(row);}panel.hidden=false;}
async function openMediaInstall(){
  const status=await api('media/install');const list=$('mediaOptions');list.replaceChildren();
  for(const o of status.options){const e=node('div',undefined,'media-option');e.append(node('b',o.label),node('p',o.detail),node('small',o.license));
    let descriptor;if(o.kind==='userBundle'){descriptor=node('input');descriptor.type='text';descriptor.placeholder='套件描述文件的完整路径';descriptor.setAttribute('aria-label','离线套件描述文件路径');e.append(descriptor);}
    const b=node('button','安装');b.onclick=async()=>{b.disabled=true;$('mediaInstallStatus').textContent='正在安装，可能需要几分钟…';
      try{const r=await api('media/install',{optionId:o.id,descriptorPath:descriptor?.value.trim()||null});$('mediaInstallStatus').textContent=r.note||'安装完成。';notice('编码组件已就绪。');$('mediaDialog').close();}
      catch(e){$('mediaInstallStatus').textContent=e.message;}finally{b.disabled=false;}};
    e.append(b);list.append(e);}
  if(!status.options.length)list.append(node('p','这个平台暂时没有一键安装方式，请按准备检查中的提示手动配置。','muted'));
  $('mediaInstallStatus').textContent='';$('mediaDialog').showModal();
}
function setFilm(value,remember=true){film=value;if(film){film.audio??=[];film.subtitles??=[];} $('filmTitle').value=film?.title||''; $('script').value=film?JSON.stringify(film,null,2):'';renderShots();renderAudio();if(remember)rememberDraft();}
function syncFilm(){if(!film)return; film.title=$('filmTitle').value; $('script').value=JSON.stringify(film,null,2);updateMeta();rememberDraft();}
function updateMeta(){const shots=film?.scenes.flatMap(s=>s.shots)||[];$('filmMeta').textContent=film?`${film.scenes.length} 场表演 · ${shots.length} 个镜头 · ${shots.reduce((n,s)=>n+s.end-s.start,0).toFixed(1)} 秒 · ${film.width} × ${film.height}`:'从一个场景、一段表演开始。';}
function renderAssets(){
  const p=project(), m=p?.manifest, search=$('search').value.toLowerCase(); $('connection').textContent=m?`${p.name} · 资产已读取`:p?`${p.name} · 等待引擎`:'尚未连接项目';
  const roles=m?.roles||[], inventory=p?.inventory?.assets||[];
  $('assetCount').textContent=`${roles.length} 个声明角色 / ${inventory.length} 个发现资产`;
  const list=$('assets');list.replaceChildren();
  for(const r of roles){const actor=m.actors.find(a=>a.id===r.defaultActor);const clips=actor?.clips||[];if(![r.id,...clips].join(' ').toLowerCase().includes(search))continue;
    const e=node('div',undefined,'asset'), name=node('div',undefined,'asset-name');name.append(node('span',r.displayName||r.id),node('span',r.presentAtStart?'已绑定':'可生成','chip'));e.append(name,node('small',r.id));const chips=node('div',undefined,'chips');for(const c of clips)chips.append(node('span',c,'chip'));e.append(chips);list.append(e);}
  for(const l of m?.locations||[]){if(!l.id.toLowerCase().includes(search))continue;const e=node('div',undefined,'asset');e.append(node('span',l.id,'asset-name'),node('small','机位 / 空间锚点'));list.append(e);}
  const found=inventory.filter(a=>(a.name+' '+a.id+' '+a.kind).toLowerCase().includes(search));
  for(const a of found.slice(0,100)){const e=node('div',undefined,'asset');e.append(node('span',a.name,'asset-name'),node('small',`${a.kind} · 已发现，调度绑定待验证`),node('small',a.id));list.append(e);}
  if(found.length>100)list.append(node('p',`还有 ${found.length-100} 项，请搜索缩小范围。`,'muted'));
  if(!list.children.length)list.append(node('p',p?'尚无匹配资产。启动离线拍摄场景后刷新，或从引擎窗口同步资产。':'接入项目后查看真实角色、动作与机位。','muted'));
}
function renderShots(){const list=$('shots');list.replaceChildren();updateMeta();fillCameraShots();if(!film){list.append(node('p','你的镜头会排列在这里。','muted'));return;}
  for(const scene of film.scenes)for(const shot of scene.shots){const e=node('article',undefined,'shot');const top=node('div',undefined,'shot-top');top.append(node('span',shot.id),node('span',scene.id));e.append(top);
    const purpose=node('input');purpose.value=shot.purpose;purpose.setAttribute('aria-label',shot.id+' 镜头意图');purpose.onchange=()=>{shot.purpose=purpose.value;syncFilm();};e.append(purpose);
    const m=project()?.manifest;for(const [field,title,items] of [['subject','角色',m?.roles.map(x=>x.id)||[]],['from','机位',m?.locations.map(x=>x.id)||[]],['type','运镜',m?.shotTypes||[]],['frame','景别',m?.frameTypes||[]]]){const label=node('label',title), select=node('select');select.setAttribute('aria-label',shot.id+' '+title);for(const x of new Set([shot.camera[field],...items].filter(Boolean)))select.add(new Option(x,x));select.value=shot.camera[field];select.onchange=()=>{shot.camera[field]=select.value;syncFilm();};label.append(select);e.append(label);}
    const times=node('div',undefined,'times');for(const [key,name] of [['start','表演起点 / s'],['end','终点 / s']]){const l=node('label',name),input=node('input');input.type='number';input.min=0;input.step=1/film.frameRate;input.value=shot[key];input.setAttribute('aria-label',shot.id+' '+name);input.onchange=()=>{shot[key]=Number(input.value);syncFilm();};l.append(input);times.append(l);}e.append(times);list.append(e);}
}
function showVideo(job){$('video').src=`/api/jobs/${job.id}/video`;$('video').hidden=false;$('emptyScreen').hidden=true;$('viewerStatus').textContent=`${job.request.film.title} · ${job.request.film.width} × ${job.request.film.height} · 待审片`;$('download').href=$('video').src;$('download').download=job.request.film.title+'.mp4';$('download').hidden=false;}
function renderJobs(){const list=$('jobs');list.replaceChildren();const jobs=state.jobs.filter(j=>j.request.projectId===selected);for(const j of jobs){const e=node('div',undefined,'job'),body=node('div');body.append(node('b',`${j.request.film.title} · ${labels[j.state]||j.state}`),node('p',`${j.completedShots}/${j.totalShots} 镜头 · 复用 ${j.reusedShots} · ${j.progress}`));e.append(body);
    const button=node('button',j.state==='Verified'?'审片':j.state==='Running'||j.state==='Queued'?'停止':'恢复');button.onclick=async()=>{button.disabled=true;try{if(j.state==='Verified')showVideo(j);else await api(`jobs/${j.id}/${j.state==='Running'||j.state==='Queued'?'cancel':'resume'}`,{});await refresh();}catch(e){notice(e.message,true);}finally{button.disabled=false;}};e.append(button);list.append(e);}
  if(!jobs.length)list.append(node('p','出片任务保存在本机，关闭窗口不会丢失。','muted'));
}
async function providerStatus(){provider=await api('provider');$('providerBadge').textContent=provider.configured?`API 导演 · ${provider.model}`:'外部 Agent / 手动脚本';if(provider.configured){$('apiEndpoint').value=provider.endpoint;$('model').value=provider.model;}}
async function direct(render){
  if(!selected||!draftSession)throw Error('请先连接项目。');
  if(!provider?.configured){$('settingsDialog').showModal();return;}
  const projectId=selected,docId=documentId,session=draftSession,signature=JSON.stringify(film);
  notice('导演正在根据可用资产安排表演与镜头…');
  const r=await operations.run('direct',{projectId,brief:$('brief').value,currentFilm:film,render:false},api);
  if(session!==draftSession||JSON.stringify(film)!==signature){
    pendingProposal={projectId,docId,film:r.film};sessionStorage.setItem('gd.proposal',JSON.stringify(pendingProposal));
    $('applyProposal').hidden=false;notice('方案已生成。你在等待时修改或切换了作品，方案已保留，点击“载入生成方案”时再应用。');return;
  }
  setFilm(r.film);await saveDocument();
  if(render)await produceDocument();else notice('方案已保存为新版本，可以检查镜头后制作。');
}
async function switchDocument(id){documentId=id;draftSession=null;setFilm(null,false);$('documentConflict').hidden=true;await loadDraft();await refreshDocuments();}
$('project').onchange=async()=>{
  selected=$('project').value;documentId='default';draftSession=null;setFilm(null,false);
  $('video').pause();$('video').removeAttribute('src');$('video').hidden=true;$('emptyScreen').hidden=false;$('download').hidden=true;$('viewerStatus').textContent='初剪预览 · 尚未出片';
  try{if(selected)await loadDraft();await loadMedia();renderAssets();renderJobs();renderConnection();await refreshDocuments();await catalogStatus();await renderRecords();}catch(e){notice(e.message,true);}
};
$('document').onchange=()=>switchDocument($('document').value).catch(e=>notice(e.message,true));
action('newDocument',async()=>{if(!selected)throw Error('请先选择项目。');await switchDocument(crypto.randomUUID());setFilm({version:1,title:'新的作品',frameRate:catalog.defaults.frameRate,width:catalog.defaults.width,height:catalog.defaults.height,scenes:[],audio:[],subtitles:[]});await saveDocument();await refreshDocuments();});
action('reloadDocument',async()=>{const value=await api(documentPath());setFilm(draftSession.replace(value),false);$('documentConflict').hidden=true;documentStatus();});
action('forkDocument',async()=>{const local=structuredClone(needFilm());await switchDocument(crypto.randomUUID());setFilm(local);await saveDocument();await refreshDocuments();notice('你的修改已另存为新作品，原作品保持不变。');});
action('applyProposal',async()=>{pendingProposal??=JSON.parse(sessionStorage.getItem('gd.proposal')||'null');if(!pendingProposal)return;if(selected!==pendingProposal.projectId||documentId!==pendingProposal.docId)throw Error('请先切回生成方案所属的作品。');setFilm(pendingProposal.film);pendingProposal=null;sessionStorage.removeItem('gd.proposal');$('applyProposal').hidden=true;notice('生成方案已载入本地草稿，保存时会检查版本冲突。');});
$('applyProposal').hidden=!sessionStorage.getItem('gd.proposal');
window.addEventListener('beforeunload',event=>{if(draftSession?.dirty){event.preventDefault();event.returnValue='';}});
$('search').oninput=renderAssets;$('filmTitle').oninput=syncFilm;
function folderButton(text, path, className='') { const button=node('button',text,className);button.type='button';button.onclick=()=>browseFolders(path);return button; }
function renderFolders(){
  const list=$('folderList');list.replaceChildren();if(!folderView)return;
  const query=$('folderSearch').value.toLocaleLowerCase();
  for(const folder of folderView.directories.filter(d=>d.name.toLocaleLowerCase().includes(query))){const button=folderButton(folder.name,folder.path,'folder-item');button.setAttribute('aria-label','打开文件夹 '+folder.name);list.append(button);}
  if(!list.children.length)list.append(node('p',query?'没有匹配的子文件夹。':'这里没有子文件夹，可以直接选择当前文件夹。','muted'));
  if(folderView.truncated)list.append(node('p','仅显示前 2000 个子文件夹；也可取消后直接输入完整路径。','muted'));
}
async function browseFolders(path){
  const generation=++folderGeneration;folderView=null;$('chooseProjectFolder').disabled=true;$('folderUp').disabled=true;$('folderError').hidden=true;$('folderSelection').textContent='';$('folderBreadcrumbs').replaceChildren();$('folderList').replaceChildren(node('p','正在读取文件夹…','muted'));$('folderSearch').value='';
  try{const value=await api('folders',{path:path||null});if(generation!==folderGeneration||!$('folderDialog').open)return;folderView=value;
    $('folderSelection').textContent=value.path;$('chooseProjectFolder').disabled=false;$('folderUp').disabled=!value.parent;
    const shortcuts=$('folderShortcuts');shortcuts.replaceChildren(folderButton('主目录',value.home));
    for(const root of value.roots.filter(r=>r!==value.home))shortcuts.append(folderButton(root,root));
    for(const p of state.projects.filter(p=>p.sourceRoot))shortcuts.append(folderButton(p.name,p.sourceRoot));
    const crumbs=$('folderBreadcrumbs');let target=value.path,items=[];
    while(target){items.unshift(target);const trimmed=target.replace(/[\\/]+$/,'');const split=Math.max(trimmed.lastIndexOf('/'),trimmed.lastIndexOf('\\'));if(split<0)break;let parent=trimmed.slice(0,split)||'/';if(/^[A-Za-z]:$/.test(parent))parent+='\\';if(parent===target)break;target=parent;}
    for(const item of items){const name=item.replace(/[\\/]+$/,'').split(/[\\/]/).pop()||item;crumbs.append(folderButton(name,item));}
    renderFolders();
  }catch(error){if(generation!==folderGeneration||!$('folderDialog').open)return;$('folderList').replaceChildren();$('folderError').textContent=error.message;$('folderError').hidden=false;}
}
action('browseProjectFolder',()=>{folderView=null;$('folderShortcuts').replaceChildren(folderButton('主目录',null));$('folderDialog').showModal();return browseFolders($('sourceRoot').value);});
$('folderDialog').addEventListener('close',()=>{folderGeneration++;folderView=null;});
$('folderSearch').oninput=renderFolders;
$('folderUp').onclick=()=>{if(folderView?.parent)return browseFolders(folderView.parent);};
action('chooseProjectFolder',()=>{if(!folderView)return;$('sourceRoot').value=folderView.path;if(!$('projectName').value)$('projectName').value=folderView.name;$('folderDialog').close();$('sourceRoot').focus();});
function openProjectForm(p=null){
  $('connectForm').reset();$('connectError').hidden=true;$('projectId').readOnly=!!p;endpointEdited=!!p;
  $('connectTitle').textContent=p?'项目接入设置':'接入一个项目';$('submitProject').textContent=p?'保存设置':'添加项目';
  if(p){$('projectId').value=p.id;$('projectName').value=p.name;$('engine').value=p.engine;$('sourceRoot').value=p.sourceRoot||'';$('endpoint').value=p.endpoint;}
  else $('endpoint').value=engineEndpoint($('engine').value)||$('endpoint').value;
  $('connectDialog').showModal();
}
function renderConnection(){
  const p=project(),box=$('projectConnection');box.hidden=!p;if(!p)return;
  const checking=checkingProjects.has(p.id),error=connectionErrors.get(p.id);
  $('projectConnectionTitle').textContent=checking?'项目已保存 · 正在检查引擎':error||!p.manifest?'项目已保存 · 等待引擎连接':'已有资产快照';
  const next=p.engine==='Unity'?'在 Unity 打开 Tools → GameDirector → Director Workbench，打开离线拍摄场景并进入 Play Mode，再检查连接。':'启动这个项目的导演适配器，再检查连接。';
  $('projectConnectionDetail').textContent=checking?'正在读取可调度资产，项目资料已保存在本机。':error?error+' '+next:!p.manifest?next:'可以重新检查连接，或修改工程目录与连接地址。';
  $('retryConnection').disabled=checking;$('retryConnection').textContent=checking?'检查中…':'检查连接';
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
    if(selected!==p.id){documentId='default';draftSession=null;}selected=p.id;connectionErrors.delete(p.id);$('connectDialog').close();await refresh();$('project').value=selected;await loadDraft();await loadMedia();notice('项目已保存在工作台，正在检查引擎连接。');checkProjectConnection(p.id);
  }catch(error){if($('connectDialog').open){$('connectError').textContent=error.message;$('connectError').hidden=false;}else notice(error.message,true);}
  finally{button.disabled=false;}
};
$('settingsForm').onsubmit=async e=>{e.preventDefault();try{await api('provider',{endpoint:$('apiEndpoint').value,model:$('model').value,apiKey:$('apiKey').value});$('apiKey').value='';await providerStatus();
  if($('jevEndpoint').value.trim())await api('decisions/provider',{endpoint:$('jevEndpoint').value.trim(),model:$('jevModel').value.trim(),apiKey:$('jevApiKey').value});
  // 阈值可留空：空白即未校准（null），绝不是 0。只有 0–1 的数字才会被保存。
  const policies={version:1,semanticReview:{enabled:$('jevReviewOn').checked,minConfidence:null}};
  for(const [minId,onId,key] of [['jevPerfMin','jevPerfOn','performanceMatch'],['jevCamMin','jevCamOn','cameraChoice'],['jevAudioMin','jevAudioOn','audioMatch'],['jevRankMin','jevRankOn','filmRanking']]){
    const raw=$(minId).value.trim(),value=raw===''?null:Number(raw);
    if(value!==null&&(!Number.isFinite(value)||value<0||value>1))throw Error('置信度阈值需在 0 与 1 之间，或留空表示未校准。');
    policies[key]={enabled:$(onId).checked,minConfidence:value};}
  await api('decisions/policies',policies);
  $('jevApiKey').value='';await decisionStatus();$('settingsDialog').close();notice('连接已配置；尚未调用模型。');}catch(e){notice(e.message,true);}};
action('disconnectProvider',async()=>{await api('provider',{endpoint:'',model:'',apiKey:''});await providerStatus();$('settingsDialog').close();});
action('disconnectDecisions',async()=>{await api('decisions/provider',{endpoint:'',model:'',apiKey:''});$('jevEndpoint').value='';$('jevModel').value='';await decisionStatus();});
action('refresh',async()=>{if(!selected)throw Error('请先接入项目。');await checkProjectConnection(selected);});
action('starter',async()=>{if(!selected)throw Error('请先接入项目。');const id=selected;const value=await api(`projects/${id}/starter`);if(selected===id)setFilm(value);});
action('importButton',()=>$('importFile').click());$('importFile').onchange=async()=>{try{const file=$('importFile').files[0];if(!file)return;if(file.size>catalog.limits.maxScriptBytes)throw Error('脚本超过 '+Math.round(catalog.limits.maxScriptBytes/1048576)+' MB。');const f=JSON.parse(await file.text());if(f.version!==1||!Array.isArray(f.scenes))throw Error('请导入 version 1 的 FilmPlan 拍摄脚本。');setFilm(f);notice('脚本已载入，出片前会校验资产与时间线。');}catch(e){notice(e.message,true);}finally{$('importFile').value='';}};
action('applyScript',()=>{const value=JSON.parse($('script').value);if(value.version!==1||!Array.isArray(value.scenes))throw Error('拍摄脚本需要 version 1 和 scenes。');setFilm(value);});
action('save',async()=>{await saveDocument();notice('作品已保存，旧版本仍保留。');});
action('validate',async()=>{const path=documentPath(),saved=await saveDocument();renderReadiness(await api(path+'/readiness',{revision:saved.revision}));});
for(const id of ['preview','render'])action(id,()=>produceDocument(id==='preview'));
action('direct',()=>direct(false));action('directRender',()=>direct(true));$('studioUrl').textContent=location.origin;
try{
  catalog=await api('health');
  const engineSelect=$('engine');engineSelect.replaceChildren(...catalog.engines.map(e=>new Option(e.label,e.id)));
  if(catalog.limits.maxAudioFormats)$('audioFile').accept=catalog.limits.maxAudioFormats.join(',');
  await refresh();if(selected&&!draftSession)await loadDraft();await refreshDocuments();await providerStatus();await loadMedia();const storage=await api('storage');$('storageInfo').textContent='影片存储：'+storage.root;
  await decisionStatus();await catalogStatus();await renderRecords();
}catch(e){notice(e.message,true);}
setInterval(()=>refresh().catch(()=>{$('connection').textContent='工作台连接中断';}),3000);

async function loadMedia(){const id=selected;if(!id){media=[];renderAudio();return;}const items=await api(`projects/${id}/media`);if(selected===id){media=items;renderAudio();}}
function renderAudio(){
  const select=$('audioMedia');select.replaceChildren(...media.map(m=>new Option(`${m.name} · ${m.duration.toFixed(1)}秒`,m.id)));
  const rows=$('audioTracks');rows.replaceChildren();
  for(const cue of film?.audio||[]){const row=node('div',undefined,'audio-track');row.append(node('b',media.find(m=>m.id===cue.mediaId)?.name||cue.mediaId));
    const controls=node('div',undefined,'audio-controls');
    const bus=node('select');bus.setAttribute('aria-label',cue.id+' 用途');for(const [id,name] of [['dialogue','对白'],['music','配乐'],['sfx','音效']])bus.add(new Option(name,id));bus.value=cue.bus;bus.onchange=()=>{cue.bus=bus.value;syncFilm();};controls.append(bus);
    for(const [key,title] of [['at','成片位置'],['sourceStart','素材起点'],['duration','持续秒数'],['volume','音量'],['fadeIn','淡入秒数'],['fadeOut','淡出秒数']]){const label=node('label',title),input=node('input');input.type='number';input.min=0;input.step=.01;input.value=cue[key]??0;input.setAttribute('aria-label',cue.id+' '+title);input.onchange=()=>{cue[key]=Number(input.value);syncFilm();};label.append(input);controls.append(label);}
    const remove=node('button','移除');remove.onclick=()=>{film.audio=film.audio.filter(a=>a!==cue);syncFilm();renderAudio();};controls.append(remove);row.append(controls);rows.append(row);
  }
  $('captions').value=(film?.subtitles||[]).map(c=>`${c.start} | ${c.end} | ${c.text}`).join('\n');
}
action('audioImportButton',()=>{if(!selected)throw Error('请先选择项目。');$('audioFile').click();});
$('audioFile').onchange=async()=>{try{const id=selected,file=$('audioFile').files[0];if(!file)return;if(file.size>catalog.limits.maxAudioBytes)throw Error('每个声音文件最多 '+Math.round(catalog.limits.maxAudioBytes/1048576)+' MiB，请使用压缩音频或拆分素材。');const base64=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(reader.result.split(',')[1]);reader.onerror=reject;reader.readAsDataURL(file);});await api(`projects/${id}/media`,{name:file.name,base64});if(selected===id)await loadMedia();notice('声音已复制到工作台，可加入声音轨。');}catch(e){notice(e.message,true);}finally{$('audioFile').value='';}};
action('addAudio',()=>{const f=needFilm(),m=media.find(a=>a.id===$('audioMedia').value);if(!m)throw Error('请先导入声音文件。');f.audio??=[];f.audio.push({id:'sound-'+crypto.randomUUID(),mediaId:m.id,bus:'sfx',at:0,sourceStart:0,duration:Math.min(m.duration,f.scenes.flatMap(s=>s.shots).reduce((n,s)=>n+s.end-s.start,0)),volume:1,fadeIn:0,fadeOut:0});syncFilm();renderAudio();});
action('applyCaptions',()=>{const f=needFilm();f.subtitles=$('captions').value.split('\n').filter(l=>l.trim()).map(l=>{const [a,b,...text]=l.split('|');if(!text.length||!a.trim()||!b.trim()||!Number.isFinite(Number(a))||!Number.isFinite(Number(b)))throw Error('字幕格式：开始秒 | 结束秒 | 内容');return {start:Number(a),end:Number(b),text:text.join('|').trim()};});syncFilm();notice('字幕已应用，制作前会检查时间范围。');});
action('exportButton',()=>{const f=needFilm(),url=URL.createObjectURL(new Blob([JSON.stringify(f,null,2)],{type:'application/json'})),a=node('a');a.href=url;a.download=f.title+'.film.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});

// ---- 决策助手（Jev 选项决策）----
let decisions=null;
const outcomeLabels={selected:'已选定','needs-review':'待人工复核','no-match':'无匹配',completed:'已完成 · 仅供人工查阅'};
const taskLabels={'performance-match':'表演匹配','camera-choice':'镜头相机','audio-match':'声音匹配','film-ranking':'候选排序','semantic-review':'语义复核'};
async function decisionStatus(){decisions=await api('decisions/provider');const p=decisions.policies;
  $('decisionStatus').textContent=decisions.configured?`决策模型 ${decisions.model} · 密钥仅存内存，重启后清除`:'未配置决策模型。在“导演连接”中设置；外部 Agent 也可提交候选。';
  if(decisions.configured){$('jevEndpoint').value=decisions.endpoint||'';$('jevModel').value=decisions.model||'';}
  for(const [key,on,min] of [['performanceMatch','jevPerfOn','jevPerfMin'],['cameraChoice','jevCamOn','jevCamMin'],['audioMatch','jevAudioOn','jevAudioMin'],['filmRanking','jevRankOn','jevRankMin']]){$(on).checked=!!p[key].enabled;$(min).value=p[key].minConfidence??'';}
  $('jevReviewOn').checked=!!p.semanticReview?.enabled;
}
function renderDecisionOutcome(panel,record){
  panel.replaceChildren();const e=node('div',undefined,'decision-result');
  e.append(node('div',`${outcomeLabels[record.outcome]||record.outcome}${record.selectedCandidateId?' · '+record.selectedCandidateId:''}`,'outcome'),node('div',record.reason||'','reason'));
  for(const a of record.answers||[]){
    if(a.choice&&a.confidence!=null)e.append(node('small',`选择 ${a.choice} · 置信度 ${(a.confidence*100).toFixed(0)}%`));
    if(a.noul!=null)e.append(node('small',`${a.questionId} · 成立概率 ${(a.noul*100).toFixed(0)}%`));
    if(a.score!=null)e.append(node('small',`${a.questionId} · 评分 ${a.score}${a.confidence!=null?' · 置信度 '+(a.confidence*100).toFixed(0)+'%':''}`));
    const probs=Object.entries(a.probabilities||{}).sort((x,y)=>y[1]-x[1]);
    for(const [id,p] of probs.slice(0,6)){const row=node('div',undefined,'prob');row.append(node('span',id.length>24?id.slice(0,24)+'…':id));const bar=node('div',undefined,'bar');bar.style.width=Math.max(2,Math.round(p*120))+'px';row.append(bar,node('small',(p*100).toFixed(0)+'%'));e.append(row);}
  }
  panel.append(e);return e;
}
async function catalogStatus(){
  if(!selected){$('catalogStatus').textContent='';return;}
  const s=await api(`projects/${selected}/catalog`);
  $('catalogStatus').textContent=s.present?(s.fresh?`目录有效 · ${s.performances} 条表演 · ${s.evidenceBacked} 条有证据支撑`:`目录已导入但当前不可用：${(s.errors||[]).slice(0,2).join('；')||'清单已变化'}`):'尚未导入表演目录。导入 JSON，或从当前清单生成骨架后补充含义与证据。';
}
action('catalogImport',()=>{if(!selected)throw Error('请先选择项目。');$('catalogFile').click();});
action('catalogExport',async()=>{if(!selected)throw Error('请先选择项目。');const catalog=await api(`projects/${selected}/catalog/content`);const url=URL.createObjectURL(new Blob([JSON.stringify(catalog,null,2)],{type:'application/json'}));const link=node('a');link.href=url;link.download=`${selected}.performances.json`;document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);});
$('catalogFile').onchange=async()=>{try{const file=$('catalogFile').files[0];if(!file)return;const catalog=JSON.parse(await file.text());const s=await api(`projects/${selected}/catalog`,catalog);await catalogStatus();notice(s.fresh?'表演目录已导入并通过校验。':'目录已导入，但校验提示需要处理。');}catch(e){notice(e.message,true);}finally{$('catalogFile').value='';}};
action('catalogScaffold',async()=>{if(!selected)throw Error('请先选择项目。');const r=await api(`projects/${selected}/catalog/scaffold`,{});await catalogStatus();notice(r.scaffolded===false?'已存在表演目录，骨架不会覆盖它；如需替换请显式导入新文件。':'已生成目录骨架，请补充每条表演的含义与证据后再用于匹配。');});
action('perfMatch',async()=>{
  if(!selected)throw Error('请先选择项目。');const intent=$('perfIntent').value.trim();if(!intent)throw Error('请写下表演意图。');
  notice('正在向决策模型提问…');const r=await api(`projects/${selected}/decisions/performance-match`,{requestId:crypto.randomUUID(),intent});
  renderDecisionOutcome($('perfResults'),r);await renderRecords();notice(outcomeLabels[r.outcome]||r.outcome);
});
function fillCameraShots(){const sel=$('cameraShot');if(!sel)return;const before=sel.value;sel.replaceChildren();for(const scene of film?.scenes||[])for(const shot of scene.shots)sel.add(new Option(`${shot.id} · ${(shot.purpose||'').slice(0,18)}`,shot.id));if([...sel.options].some(o=>o.value===before))sel.value=before;}
action('cameraSuggest',async()=>{
  const f=needFilm();const shotId=$('cameraShot').value;if(!shotId)throw Error('请先选择镜头。');
  const list=await api(`projects/${selected}/decisions/camera-choice/suggest`,{shotId,film:f});
  $('cameraCandidates').value=JSON.stringify(list.filter(c=>c.compiles).map(c=>({id:c.id,camera:c.camera})),null,2);
  notice(`已生成 ${list.filter(c=>c.compiles).length} 个可编译候选（清单词表枚举，可编辑后再决策）。`);
});
action('cameraDecide',async()=>{
  const f=needFilm();const shotId=$('cameraShot').value;if(!shotId)throw Error('请先选择镜头。');
  const intent=$('cameraIntent').value.trim();if(!intent)throw Error('请写下相机意图。');
  let candidates=null;const text=$('cameraCandidates').value.trim();
  if(text){candidates=JSON.parse(text);if(!Array.isArray(candidates)||!candidates.length)throw Error('候选相机需要非空 JSON 数组。');}
  notice('正在向决策模型提问…');
  const r=await api(`projects/${selected}/decisions/camera-choice`,{requestId:crypto.randomUUID(),shotId,intent,documentId,documentRevision:draftSession?.document?.revision??null,film:f,candidates});
  const e=renderDecisionOutcome($('cameraResults'),r);
  if(r.outcome==='selected'&&r.proposal){const b=node('button','载入相机方案（只改该镜头相机）');b.onclick=()=>{setFilm(structuredClone(r.proposal));notice('相机方案已载入本地草稿，其余镜头/表演/声音保持不变；保存草稿时会检查版本。');};e.append(b);}
  await renderRecords();
});
action('audioMatch',async()=>{
  if(!selected)throw Error('请先选择项目。');const intent=$('audioIntent').value.trim();if(!intent)throw Error('请写下声音意图。');
  notice('正在向决策模型提问…');const r=await api(`projects/${selected}/decisions/audio-match`,{requestId:crypto.randomUUID(),intent,bus:$('audioBus').value});
  const e=renderDecisionOutcome($('audioResults'),r);
  if(r.outcome==='selected'&&r.selectedCandidateId){const m=media.find(a=>a.id===r.selectedCandidateId);if(m){const b=node('button',`加入声音轨：${m.name}`);b.onclick=()=>{const f=needFilm();f.audio??=[];f.audio.push({id:'sound-'+crypto.randomUUID(),mediaId:m.id,bus:$('audioBus').value,at:0,sourceStart:0,duration:Math.min(m.duration,f.scenes.flatMap(s=>s.shots).reduce((n,s)=>n+s.end-s.start,0)),volume:1,fadeIn:0,fadeOut:0});syncFilm();renderAudio();notice('已加入声音轨，可继续调整时间。');};e.append(b);}}
  await renderRecords();
});
async function renderRecords(){
  if(!selected)return;const list=await api(`projects/${selected}/decisions`);const panel=$('decisionRecords');panel.replaceChildren();
  for(const r of list.slice(0,12)){const e=node('div',undefined,'decision-record');e.append(node('span',`${taskLabels[r.task]||r.task} · ${outcomeLabels[r.outcome]||r.outcome}${r.selectedCandidateId?' · '+r.selectedCandidateId.slice(0,30):''}`),node('small',`${new Date(r.createdAt).toLocaleString()} · ${r.reason||''}`));panel.append(e);}
  if(!list.length)panel.append(node('p','还没有决策记录。','muted'));
}
action('recordsRefresh',renderRecords);
$('audioMedia').onchange=()=>{const m=media.find(a=>a.id===$('audioMedia').value);$('mediaDescription').value=m?.description||'';};
action('saveMediaDescription',async()=>{const m=media.find(a=>a.id===$('audioMedia').value);if(!m)throw Error('请先选择已导入声音。');await api(`projects/${selected}/media/${m.id}`,{description:$('mediaDescription').value||null});await loadMedia();notice('声音描述已保存，可用于声音匹配。');});

// ---- 候选影片比较与语义复核（均为方案准备阶段；不评判渲染画面）----
function renderRanking(panel,record){
  const e=renderDecisionOutcome(panel,record);
  if(record.ranking?.length){const table=node('div');
    record.ranking.forEach((r,i)=>{const row=node('div',undefined,'prob');
      row.append(node('span',`#${i+1} ${r.candidateId}`));
      row.append(node('small',(r.score!=null?`得分 ${r.score}`:'未评分')+(r.confidence!=null?` · 置信度 ${(r.confidence*100).toFixed(0)}%`:'')));
      table.append(row);if(r.evidence)table.append(node('small',r.evidence));});
    e.append(table);}
  return e;
}
action('rankCompare',async()=>{
  if(!selected)throw Error('请先选择项目。');const brief=$('rankBrief').value.trim();if(!brief)throw Error('请写下评选简报。');
  const candidates=[];const f=film;
  if(f)candidates.push({id:'current',rationale:'当前草稿',film:f});
  const text=$('rankCandidates').value.trim();
  if(text){const external=JSON.parse(text);if(!Array.isArray(external))throw Error('外部候选需要 JSON 数组。');for(const c of external)candidates.push(c);}
  if(!candidates.length)throw Error('没有可比较的候选：请打开一个作品草稿，或提供外部候选 JSON。');
  notice('正在向决策模型提问…');const r=await api(`projects/${selected}/decisions/film-ranking`,{requestId:crypto.randomUUID(),brief,candidates});
  renderRanking($('rankResults'),r);await renderRecords();notice(outcomeLabels[r.outcome]||r.outcome);
});
action('reviewRun',async()=>{
  if(!selected)throw Error('请先选择项目。');const subject=$('reviewSubject').value.trim();if(!subject)throw Error('请粘贴被复核文本。');
  const checks=JSON.parse($('reviewChecks').value||'[]');if(!Array.isArray(checks)||!checks.length)throw Error('检查项需要非空 JSON 数组。');
  notice('正在向决策模型提问…');const r=await api(`projects/${selected}/decisions/semantic-review`,{requestId:crypto.randomUUID(),subject,label:film?`作品《${film.title}》的文本`:null,checks});
  renderDecisionOutcome($('reviewResults'),r);await renderRecords();notice(outcomeLabels[r.outcome]||r.outcome);
});
