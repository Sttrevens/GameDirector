// Test-only ownership/resolution helpers; production never provisions fixtures.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {tempDirectory} from './test_temp.mjs';
export const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..'),engine=path.join(repo,'packages/gamedirector-three');
export const readJSON=file=>JSON.parse(fs.readFileSync(file,'utf8'));
export const writeJSON=(file,value)=>fs.writeFileSync(file,JSON.stringify(value)+'\n');
export function bundleFixture(){
 const temp=tempDirectory('gd-three-bundle-'),gameRoot=path.join(temp.root,'game'),familyRoot=path.join(temp.root,'family'),adapterRoot=path.join(familyRoot,'stage'),threeRoot=path.join(engine,'node_modules/three');
 fs.cpSync(path.join(repo,'tools/fixtures/three-ts-game'),gameRoot,{recursive:true});fs.cpSync(path.join(repo,'tools/fixtures/three-ts-adapter'),familyRoot,{recursive:true});
 // A removed external option must reach real THREE and fail input admission,
 // not merely fail because the fixture cannot resolve a package.
 for(const root of [gameRoot,familyRoot]){fs.mkdirSync(path.join(root,'node_modules'));fs.symlinkSync(threeRoot,path.join(root,'node_modules/three'),'dir');}
 return {...temp,gameRoot,familyRoot,adapterRoot,threeRoot};
}
export const entryFile=f=>path.join(f.familyRoot,'adapter.ts');
export function editEntry(f){const file=entryFile(f),source=fs.readFileSync(file,'utf8');if(!source.includes('fixture options missing'))throw Error('fixture edit literal absent');fs.writeFileSync(file,source.replace('fixture options missing','fixture options edited'));}
export const addImport=(f,specifier)=>fs.appendFileSync(entryFile(f),'\nimport {remote} from '+JSON.stringify(specifier)+';console.log(remote);\n');
export function vendoredThree(f){const file=path.join(f.gameRoot,'src/lib/three.module.js');fs.mkdirSync(path.dirname(file),{recursive:true});fs.copyFileSync(path.join(f.threeRoot,'build/three.module.js'),file);fs.appendFileSync(entryFile(f),'\nimport {Scene as remote} from "game:src/lib/three.module.js";console.log(remote);\n');}
export function caseThree(f){const dir=path.join(f.gameRoot,'node_modules/ThReE');if(!fs.existsSync(dir))fs.symlinkSync(f.threeRoot,dir,'dir');addImport(f,'game:src/case-three.ts');}
export const reviewRefusals=[
 ...['https://example.invalid/code.js','http://example.invalid/code.js','//example.invalid/code.js'].map(url=>['external '+url,f=>addImport(f,url),/external import not allowed.*example\.invalid\/code\.js/]),
 ['vendored THREE',vendoredThree,/second THREE.*(?:digest|build file).*game:src\/lib\/three.module.js/],
 ['case-variant THREE',caseThree,/second THREE.*(?:three root|node_modules\/three|package)/],
 ['package-named THREE',f=>addImport(f,'game:src/wrapped-three/index.js'),/second THREE.*package.*three/],
 ['unknown key forbids',f=>{changeConfig(f,c=>{c.forbids=c.forbid;delete c.forbid;});fs.appendFileSync(entryFile(f),"\nimport {network} from 'game:src/forbidden.ts';console.log(network);\n");},/unknown three\.json key.*forbids/],
 ['unknown stage key',f=>changeConfig(f,c=>c.stage.qurey='typo'),/unknown three\.json stage key.*qurey/],
 ...['!','src/[broken','src/***/bad','/src/app','game:../outside'].map(pattern=>['malformed forbid '+pattern,f=>changeConfig(f,c=>c.forbid=[pattern]),/malformed forbid pattern/]),
];
export function changeConfig(f,change){const file=path.join(f.adapterRoot,'three.json'),config=readJSON(file);change(config);writeJSON(file,config);}
export function engineWithWrongPin(f){
 const root=path.join(f.root,'wrong-pin-engine');fs.mkdirSync(root);for(const name of fs.readdirSync(engine))if(/\.(?:mjs|js)$/.test(name))fs.copyFileSync(path.join(engine,name),path.join(root,name));
 const pkg=readJSON(path.join(engine,'package.json'));pkg.dependencies.esbuild='0.0.0';writeJSON(path.join(root,'package.json'),pkg);fs.symlinkSync(path.join(engine,'node_modules'),path.join(root,'node_modules'),'dir');return root;
}
