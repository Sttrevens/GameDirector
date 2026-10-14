// Register ownership before setup. The exit fallback also covers a module that
// aborts before node:test gets to execute its after hook (no async exit work).
import {after} from 'node:test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
export function tempDirectory(prefix,register=after){
 const root=fs.mkdtempSync(path.join(os.tmpdir(),prefix));
 const cleanup=()=>{process.removeListener('exit',cleanup);try{fs.rmSync(root,{recursive:true,force:true});}catch(error){console.error('FAIL: temp cleanup: '+root);throw error;}};
 process.once('exit',cleanup);
 try{register(cleanup);}catch(error){cleanup();throw error;}
 return {root,cleanup};
}
