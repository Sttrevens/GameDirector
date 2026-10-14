// Opt-in TS/JSON admission and startup identity. esbuild is engine-local;
// byte snapshots, resolution metadata and directory stats own containment.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {fileURLToPath,pathToFileURL} from 'node:url';
import * as esbuild from 'esbuild';
import {declaredGl} from './gl-backends.mjs';
const here=path.dirname(fileURLToPath(import.meta.url)),real=file=>fs.realpathSync.native(file);
const slash=p=>p.split(path.sep).join('/'),inside=(root,file)=>file===root||file.startsWith(root+path.sep);
const compare=(a,b)=>a<b?-1:a>b?1:0,sha=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
const canonical=value=>Array.isArray(value)?value.map(canonical):value&&typeof value==='object'?Object.fromEntries(Object.keys(value).sort().map(k=>[k,canonical(value[k])])):value;
const hash=value=>sha(JSON.stringify(canonical(value))),rows=files=>files.filter(r=>!r.directory).map(({label,path,sha256})=>({label,path,sha256})).sort((a,b)=>compare(a.label+':'+a.path,b.label+':'+b.path));
const sameStat=(a,b)=>['size','mtimeNs','ctimeNs','ino','dev'].every(k=>a[k]===b[k]),tag=r=>r.label+':'+r.path;
function record(file,label,relative){const stat=fs.statSync(file,{bigint:true}),contents=fs.readFileSync(file),r={file,label,path:slash(relative),contents,sha256:sha(contents),stat};if(!sameStat(stat,fs.statSync(file,{bigint:true})))throw Error('bundle input changed during startup: '+tag(r));return r;}
function changedSource(files){for(const r of files){let stat;try{stat=fs.statSync(r.file,{bigint:true});}catch{return tag(r);}if(!sameStat(stat,r.stat))return tag(r);}return null;}
export function sourceFiles(root,label,read=record){
 root=real(root);const files=[];
 function visit(dir,parents=new Set()){
  const native=real(dir);if(!inside(root,native))throw Error('identity file outside '+label+' root: '+dir);if(parents.has(native))throw Error('cyclic identity directory: '+dir);
  const relative=slash(path.relative(root,dir)),stat=fs.statSync(dir,{bigint:true});files.push({file:dir,label,path:(relative||'.')+'/',directory:true,stat}); // engine-local: dot names the root directory in a stat diagnostic
  const next=new Set([...parents,native]);for(const e of fs.readdirSync(dir,{withFileTypes:true}).sort((a,b)=>compare(a.name,b.name))){if(['node_modules','.git'].includes(e.name))continue;
   const file=path.join(dir,e.name),s=fs.statSync(file);if(s.isDirectory())visit(file,next);else if(s.isFile()){if(!inside(root,real(file)))throw Error('identity file outside '+label+' root: '+file);files.push(read(real(file),label,path.relative(root,file)));}}
 }
 visit(root);return files;
}
export const sourceDigest=files=>hash(rows(files));
let invocations=0;
export const buildCount=()=>invocations;
// Both the resolver and the post-build metafile guard use this exact set.
function admitExternal(specifier){const prefix=['three/addons/','three/examples/jsm/'].find(p=>specifier.startsWith(p)),tail=prefix?specifier.slice(prefix.length):null;if(specifier!=='three'&&(!tail||tail.split('/').some(p=>!p||p==='.'||p==='..')||/[\\?#\u0000-\u001f]/.test(tail)))throw Error('external import not allowed: '+specifier);}
export function assertExternalImports(metafile){for(const input of Object.values(metafile.inputs))for(const imported of input.imports)if(imported.external)admitExternal(imported.path);}
function knownKeys(value,keys,name){for(const key of Object.keys(value))if(!keys.includes(key))throw Error('unknown three.json '+name+'key: '+key);}
function validatePattern(pattern){
 let p=pattern.startsWith('!')?pattern.slice(1):pattern;const colon=p.indexOf(':');if(colon>=0){if(!['game','family'].includes(p.slice(0,colon)))throw Error('malformed forbid pattern: '+pattern);p=p.slice(colon+1);}p=p.replace(/\/$/,'');
 if(!p||p.startsWith('/')||/[\\[\]{}:\u0000-\u001f]/.test(p)||p.split('/').some(s=>!s||s==='.'||s==='..'||s.includes('**')&&s!=='**'))throw Error('malformed forbid pattern: '+pattern);
}
function matches(pattern,value){
 if(!/[?*]/.test(pattern)){const p=pattern.replace(/\/$/,'');return value===p||value.startsWith(p+'/');}
 let expression='';for(let i=0;i<pattern.length;i++){const c=pattern[i];if(c==='*'){if(pattern[i+1]==='*'){i++;if(pattern[i+1]==='/'){i++;expression+='(?:.*/)?';}else expression+='.*';}else expression+='[^/]*';}else if(c==='?')expression+='[^/]';else expression+=c.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');}
 return new RegExp('^'+expression+'$').test(value);
}
function relativePath(root,value,name){if(typeof value!=='string'||!value||path.isAbsolute(value))throw Error('three.json '+name+' must be a relative path');const full=path.resolve(root,value);if(!inside(root,full))throw Error('three.json '+name+' outside root: '+value);return full;}
// JSONC strings are preserved while comments/trailing commas are removed.
function jsonConfig(text){return JSON.parse(text.replace(/"(?:\\.|[^"\\])*"|\/\/[^\n]*|\/\*[\s\S]*?\*\//g,s=>s.startsWith('"')?s:' ').replace(/"(?:\\.|[^"\\])*"|,\s*(?=[}\]])/g,s=>s.startsWith('"')?s:''));}
const engineExports=['WebGLRenderer','PCFSoftShadowMap','SRGBColorSpace','ACESFilmicToneMapping','Scene','PerspectiveCamera','Vector3','Box3','MathUtils'];
const metadataNames=['package.json','tsconfig.json','jsconfig.json'],loaders={'.js':'js','.mjs':'js','.cjs':'js','.jsx':'jsx','.ts':'ts','.mts':'ts','.cts':'ts','.tsx':'tsx','.json':'json'};
export async function buildAdapter({gameRoot,adapterRoot,threeRoot}){
 gameRoot=real(gameRoot);adapterRoot=real(adapterRoot);threeRoot=real(threeRoot);const snapshots=new Map();
 function snapshot(file,label,relative){file=real(file);let r=snapshots.get(file);if(!r){r=record(file,label,relative);snapshots.set(file,r);}return {...r,label,path:slash(relative)};}
 const configFile=path.join(adapterRoot,'three.json'),configRecord=snapshot(configFile,'stage','three.json'),config=JSON.parse(configRecord.contents);
 if(!config||typeof config!=='object'||Array.isArray(config))throw Error('three.json must be an object');knownKeys(config,['family','entry','gameSha','three','stage','alias','define','forbid','options','gl'],'');
 const gl=declaredGl(config.gl);
 const {family='.',entry,gameSha,three,stage={},alias={},define={},forbid=[],options={}}=config;
 if(typeof family!=='string'||!family||path.isAbsolute(family))throw Error('three.json family must be a relative path');const familyRoot=real(path.resolve(adapterRoot,family));
 if(!inside(familyRoot,adapterRoot))throw Error('three.json family must contain the stage directory');Object.assign(snapshots.get(real(configFile)),{label:'family',path:slash(path.relative(familyRoot,configFile))});
 const roots=[['game',gameRoot],['family',familyRoot]],aliases=new Map(),labelledAliases={};
 function label(file){for(const [label,root] of roots)if(inside(root,file))return {label,path:slash(path.relative(root,file))};throw Error('bundle input outside snapshot and family roots: '+file);}
 const ancestry=new Map();for(const [owner,root] of roots){let rank=0;for(let dir=path.dirname(root);;dir=path.dirname(dir)){const files=metadataNames.map(name=>path.join(dir,name)).filter(file=>fs.existsSync(file)&&fs.statSync(file).isFile()).map(real).filter(file=>!roots.some(([,r])=>inside(r,file)));if(files.length){for(const file of files)if(!ancestry.has(file))ancestry.set(file,{label:'metadata-'+owner,path:'ancestor-'+rank+'/'+path.basename(file)});rank++;}if(dir===path.dirname(dir))break;}}
 function metadataKey(file){if(inside(threeRoot,file))return {label:'three',path:slash(path.relative(threeRoot,file))};for(const [label,root] of roots)if(inside(root,file))return {label,path:slash(path.relative(root,file))};const key=ancestry.get(file);if(key)return key;throw Error('resolution metadata changed during startup or outside roots and ancestry: '+file);}
 function readMetadata(file){const key=metadataKey(real(file));return snapshot(file,key.label,key.path);}
 // Resolver metadata is not exposed through onLoad. Snapshot all local package
 // contexts before resolving, plus ancestor metadata as labelled rows. Ancestor
 // refusal would break the required live fixture (home package.json above it).
 function metadataPaths(){const found=new Set(),seen=new Set();
  function visit(dir){dir=real(dir);if(seen.has(dir)||!roots.some(([,root])=>inside(root,dir)))return;seen.add(dir);
   for(const name of metadataNames){const file=path.join(dir,name);if(fs.existsSync(file)&&fs.statSync(file).isFile())found.add(real(file));}
   for(const e of fs.readdirSync(dir,{withFileTypes:true})){if(e.name==='.git')continue;const file=path.join(dir,e.name);if(e.isDirectory()||e.isSymbolicLink()&&fs.statSync(file).isDirectory())visit(file);}
  }
  for(const [,root] of roots){visit(root);for(let dir=path.dirname(root);;dir=path.dirname(dir)){for(const name of metadataNames){const file=path.join(dir,name);if(fs.existsSync(file)&&fs.statSync(file).isFile())found.add(real(file));}if(dir===path.dirname(dir))break;}}
  return [...found].sort(compare);
 }
 for(const [name,value] of Object.entries({stage,alias,define,options}))if(!value||typeof value!=='object'||Array.isArray(value))throw Error('three.json '+name+' must be an object');
 if(!Array.isArray(forbid)||forbid.some(p=>typeof p!=='string'||!p))throw Error('three.json forbid must be an array of nonempty patterns');knownKeys(stage,['query'],'stage ');for(const pattern of forbid)validatePattern(pattern);
 const query=stage.query??''; // engine-local: an omitted stage query means no query, not a contract default
 if(typeof query!=='string')throw Error('three.json stage.query must be a string');for(const [key,value] of Object.entries(define))if(typeof value!=='string')throw Error('three.json define must contain esbuild expression strings: '+key);
 const provenanceFile=path.join(gameRoot,'.gamedirector-provenance.json'),provenance=JSON.parse(snapshot(provenanceFile,'game','.gamedirector-provenance.json').contents);
 if(typeof gameSha!=='string'||!gameSha||gameSha!==provenance.sha)throw Error('three.json gameSha '+gameSha+' differs from provenance sha '+provenance.sha);if(typeof provenance.tree!=='string'||!provenance.tree)throw Error('provenance tree is missing');
 const familyFiles=sourceFiles(familyRoot,'family',snapshot),threeFiles=sourceFiles(threeRoot,'three',snapshot),threeVersion=JSON.parse(readMetadata(path.join(threeRoot,'package.json')).contents).version;
 if(typeof three!=='string'||!three||threeVersion!==three)throw Error('three version '+threeVersion+' differs from three.json pin '+three);
 const THREE=await import(pathToFileURL(path.join(threeRoot,'build/three.module.js')));for(const name of engineExports)if(THREE[name]===undefined)throw Error('three missing engine-used export: '+name);
 const pin=JSON.parse(fs.readFileSync(path.join(here,'package.json'),'utf8')).dependencies.esbuild;if(!/^\d+\.\d+\.\d+$/.test(pin)||esbuild.version!==pin)throw Error('esbuild version '+esbuild.version+' differs from engine pin '+pin);
 const metadataBefore=metadataPaths(),metadataFiles=metadataBefore.map(readMetadata),threeBuildRoot=real(path.join(threeRoot,'build')),threeBuildDigests=new Set(threeFiles.filter(r=>!r.directory&&inside(threeBuildRoot,r.file)).map(r=>r.sha256));
 function nearestPackage(file){for(let dir=path.dirname(file);;dir=path.dirname(dir)){const pkg=path.join(dir,'package.json');if(fs.existsSync(pkg))return JSON.parse(readMetadata(pkg).contents);if(dir===path.dirname(dir))return null;}}
 function admit(file,r){
  if(inside(threeRoot,file))throw Error('second THREE input inside three root: '+file);
  const key=label(file),name=tag(key);if(r&&threeBuildDigests.has(r.sha256))throw Error('second THREE build file digest matches '+name);if(nearestPackage(file)?.name==='three')throw Error('second THREE input in package named three: '+name);
  let blocked=false,cause;for(const pattern of forbid){const allow=pattern.startsWith('!'),p=allow?pattern.slice(1):pattern;if(matches(p,p.includes(':')?name:key.path)){blocked=!allow;cause=pattern;}}if(blocked)throw Error('forbid '+cause+' matched bundle input '+name);return key;
 }
 for(const [from,to] of Object.entries(alias)){const original=real(relativePath(gameRoot,from,'alias key')),shim=real(relativePath(familyRoot,to,'alias value'));if(!inside(gameRoot,original)||!inside(familyRoot,shim))throw Error('three.json alias outside roots: '+from+' -> '+to);aliases.set(original,shim);labelledAliases['game:'+slash(path.relative(gameRoot,original))]='family:'+slash(path.relative(familyRoot,shim));}
 const entryFile=real(relativePath(familyRoot,entry,'entry'));if(!inside(familyRoot,entryFile))throw Error('three.json entry outside family root: '+entry);
 // Flat family tsconfig wins, then snapshot tsconfig; otherwise explicit empty
 // compiler options. No implicit TS discovery. JSONC is supported; flatten an
 // extends chain into a family config so no inherited option can escape identity.
 const configRoot=fs.existsSync(path.join(familyRoot,'tsconfig.json'))?familyRoot:gameRoot,tsconfigFile=path.join(configRoot,'tsconfig.json');let tsconfigRaw={compilerOptions:{}},identityConfig=tsconfigRaw;
 if(fs.existsSync(tsconfigFile)){tsconfigRaw=jsonConfig(readMetadata(tsconfigFile).contents.toString('utf8'));if(Object.hasOwn(tsconfigRaw,'extends'))throw Error('tsconfig extends requires a flat family tsconfig: '+tsconfigFile);identityConfig=structuredClone(tsconfigRaw);
  const base=tsconfigRaw.compilerOptions?.baseUrl;if(base!==undefined){if(typeof base!=='string'||path.isAbsolute(base))throw Error('tsconfig baseUrl must be root-relative');const absolute=path.resolve(configRoot,base);if(!inside(configRoot,absolute))throw Error('tsconfig baseUrl outside root: '+base);tsconfigRaw.compilerOptions.baseUrl=absolute;identityConfig.compilerOptions.baseUrl=tag(label(absolute));}
 }
 const buildOptions={bundle:true,format:'esm',platform:'browser',target:'es2023',splitting:false,external:['three','three/addons/*','three/examples/jsm/*'],loader:{'.json':'json'},define,tsconfigRaw,metafile:true,write:false,outfile:'adapter.js',logLevel:'silent'};
 const inputFiles=new Map(),plugin={name:'gamedirector-roots',setup(build){build.onResolve({filter:/.*/},async args=>{
  if(args.pluginData?.gdResolved)return;if(args.path==='three'||args.path.startsWith('three/')){admitExternal(args.path);return {path:args.path,external:true};}
  let request=args.path;if(request.startsWith('game:'))request=relativePath(gameRoot,request.slice('game:'.length),'game import');const resolved=await build.resolve(request,{resolveDir:args.resolveDir||familyRoot,kind:args.kind,pluginData:{gdResolved:true}});
  if(resolved.errors.length)return {errors:resolved.errors};if(resolved.external){admitExternal(resolved.path);return {path:resolved.path,external:true};}const original=real(resolved.path),file=aliases.get(original)??original;admit(file);return {path:file,...(file===original?{sideEffects:resolved.sideEffects}:{})};
 });build.onLoad({filter:/.*/,namespace:'file'},args=>{
  const file=real(args.path),key=admit(file),r=snapshot(file,key.label,key.path),loader=loaders[path.extname(file).toLowerCase()];if(!loader)throw Error('unsupported bundle input loader: '+tag(key));admit(file,r);inputFiles.set(file,r);return {contents:r.contents,loader,resolveDir:path.dirname(file)};
 });}};
 let built;invocations++;try{built=await esbuild.build({...buildOptions,absWorkingDir:familyRoot,entryPoints:[entryFile],plugins:[plugin]});}catch(error){throw Error('bundle refused: '+error.message,{cause:error});}
 assertExternalImports(built.metafile);for(const input of Object.keys(built.metafile.inputs)){const file=real(path.resolve(familyRoot,input));if(!inputFiles.has(file))throw Error('bundle input bypassed byte snapshot: '+input);}
 const after=metadataPaths();if(JSON.stringify(after)!==JSON.stringify(metadataBefore))throw Error('resolution metadata changed during startup; restart the bridge');
 const cached=[...snapshots.values(),...familyFiles.filter(r=>r.directory),...threeFiles.filter(r=>r.directory)],changed=changedSource(cached);if(changed)throw Error('bundle input changed during startup: '+changed);
 const identity={gl,three:{version:threeVersion,digest:sourceDigest(threeFiles)},family:rows(familyFiles),inputs:rows([...inputFiles.values()]),metadata:rows(metadataFiles),esbuild:esbuild.version,
  buildOptions:{...buildOptions,tsconfigRaw:identityConfig,entryPoints:['family:'+slash(path.relative(familyRoot,entryFile))],absWorkingDir:'family:',alias:labelledAliases},stageQuery:query,provenance:{sha:provenance.sha,tree:provenance.tree}};
 const statCache=cached.map(({file,label,path,stat,directory})=>({file,label,path,stat,directory}));
 return {code:built.outputFiles[0].text,metafile:built.metafile,inputs:identity.inputs,metadata:identity.metadata,options,gl,stageQuery:query,provenance:identity.provenance,fingerprint:versions=>hash({...identity,...versions}),changedInput:(extra=[])=>changedSource([...statCache,...extra])};
}
