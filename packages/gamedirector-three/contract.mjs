import {EaseKernels,MotionKernels,ImplementedFormulas} from './camera-kernels.mjs';
import {ImplementedShotParams} from './shot-params.mjs';
export const Implements={major:1,minor:2},EngineId='three';
export const BootstrapContractTimeoutMs=30000,BootstrapContractBytes=1024*1024; // engine-local: bound reading the graph before its budgets exist
export const Uses={errors:['invalidRequest','stateConflict','busy','internal','forbidden','notFound','methodNotAllowed','unsupportedMediaType','payloadTooLarge'],
  states:['capturing','completed','failed','cancelled'],routes:['health','manifest','contract','take','status','stage','takeStart','takeFrame','takeStop']};
// Engine-owned stamps, in the existing live manifest's insertion order. Values
// and separators belong to the rows, not to this implementation's vocabulary.
export function engineStamps(contract,{sourceFingerprint,sourceRoot,implementedParams=ImplementedShotParams}){
 const rows=new Map(contract?.capabilities?.map(r=>[r.key,r])),single=key=>{const row=rows.get(key);return row?.values?.length===1?row.values[0].value:undefined;},separator=rows.get('camera.params')?.separator;
 // Invalid claims stay unset for refusal() to diagnose; never let join() supply a separator.
 return {'director.mode':single('director.mode'),'presentation.sourceFingerprint':sourceFingerprint,engine:EngineId,'project.sourceRoot':sourceRoot,
  'capture.audio':single('capture.audio'),'camera.params':typeof separator==='string'&&separator.length>0?implementedParams.join(separator):undefined,
  ...(contract.measure?{measure:single('measure')}:{})};
}
// Pure startup check, like UnityEngineContract.Check: return the first cause,
// never perform I/O or initialize THREE. Higher minors still require coverage.
export function refusal(contract,{stamps={},implementedParams=ImplementedShotParams,implements:implementsVersion=Implements}={}){
 if(contract?.schema!=='gamedirector.engine-contract')return 'engine contract schema missing or foreign: '+contract?.schema;
 const version=contract.version;
 if(!version||version.major!==implementsVersion.major||!Number.isInteger(version.minor)||version.minor<implementsVersion.minor)return 'engine implements engine contract '+implementsVersion.major+'.'+implementsVersion.minor+' but GameDirector.Core provides '+version?.major+'.'+version?.minor+'; install matching GameDirector packages';
 const arrays=['camera.frames','camera.eases','camera.motions','camera.shots','camera.params','capabilities'];
 const required=['take.states','capture','frameClock','bridge.errors','frameClock.cueEpsilonSeconds','capture.idleLeaseSeconds','camera.currentAnchor','camera.fallbackFrame','camera.lens.defaultFov','camera.lens.sensorSizeMm',
  'camera.rig.nearClip','camera.rig.farClip','camera.rig.minSubjectHeight','camera.rig.degenerateSqrMagnitude','camera.rig.autoFrameFallback.up','camera.rig.autoFrameFallback.back',
  'camera.rig.distanceFallback.up','camera.rig.distanceFallback.back','review.intervalSeconds','review.roles.kinds','review.roles.states','bridge.loopbackHost','bridge.loopbackAlias','bridge.ports.three',
  'bridge.limits.maxRequestBytes','bridge.limits.maxQueuedRequests','bridge.budgets.compileSeconds','bridge.budgets.pageLoadSeconds','bridge.budgets.stageReadySeconds',
  'take.silentPictureAudio','take.requestDefaults.captureFromFrame','take.requestDefaults.advanceOnly','take.requestDefaults.measure',
  'measure.fields','measure.flag','measure.collection','measure.capability',
  ...Uses.states.map(k=>'take.states.'+k),...Uses.routes.map(k=>'bridge.routes.'+k),...Uses.errors.flatMap(k=>['bridge.errors.'+k,'bridge.errors.'+k+'.status','bridge.errors.'+k+'.code'])];
 const at=key=>key.split('.').reduce((v,k)=>v?.[k],contract);
 for(const key of [...arrays,...required])if(at(key)==null)return 'rebuild gd: contract lacks '+key;
 for(const key of arrays)if(!Array.isArray(at(key)))return 'rebuild gd: contract needs array '+key;
 const camera=contract.camera;
 for(const row of camera.eases)if(!Object.hasOwn(EaseKernels,row.formula))return 'unimplemented ease formula: '+row.formula;
 for(const id of [...camera.shots.map(s=>s.motion),...camera.motions.map(m=>m.id)])if(!Object.hasOwn(MotionKernels,id))return 'unimplemented motion: '+id;
 for(const [key,path] of [['progress','camera.progress'],['focalToFov','camera.lens.focalToFov'],['autoFrameDistance','camera.rig.autoFrameDistance']])if(at(path)?.id!==ImplementedFormulas[key])return 'unimplemented camera formula at '+path+': '+at(path)?.id;
 for(const [path,id] of [['camera.lens.aspect','hor-plus'],['review.roles.bounds','role-bounds-v1'],['review.roles.crown','bounds-top-center']])if(at(path)?.id!==id)return 'unimplemented formula at '+path+': '+at(path)?.id;
 const params=new Set(camera.params.map(p=>p.name));for(const name of implementedParams)if(!params.has(name))return 'implemented shot param absent from camera.params: '+name;
 const rows=new Map(contract.capabilities.map(r=>[r.key,r])),paramRow=rows.get('camera.params');
 if(!paramRow)return 'engine stamp row camera.params missing';
 if(typeof paramRow.separator!=='string'||paramRow.separator.length===0)return 'engine stamp row camera.params requires a non-empty string separator';
 for(const key of ['director.mode','capture.audio']){const row=rows.get(key);if(!row)return 'engine stamp row '+key+' missing';if(!Array.isArray(row.values)||row.values.length!==1)return 'engine stamp row '+key+' requires exactly one value';}
 for(const row of rows.values())if(row.declarer==='engine'&&(!Object.hasOwn(stamps,row.key)||stamps[row.key]==null))return 'engine row missing stamp: '+row.key;
 for(const [key,value] of Object.entries(stamps)){
  const row=rows.get(key);if(!row||!['engine','adapter-or-engine'].includes(row.declarer))return 'stamp '+key+' is not an engine or adapter-or-engine row (declarer: '+row?.declarer+')';
  if(row.kind==='enum'&&!row.values?.some(v=>v.value===value))return 'stamped enum '+key+' is outside row values: '+value;
 }
 if(!camera.frames.some(f=>f.id===camera.fallbackFrame))return 'fallbackFrame not in camera.frames: '+camera.fallbackFrame;
 return null;
}
