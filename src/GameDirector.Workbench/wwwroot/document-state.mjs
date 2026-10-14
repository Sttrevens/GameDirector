import {t,serverMessage} from './ui-catalog.mjs';
// Per-tab recovery avoids one browser window overwriting another's unsaved edits.
// The server remains the authority for accepted document revisions.

export function formatStorageBytes(n){
  if(n===0)return '0';
  if(n>=1073741824)return t('storageGB',[(n/1073741824).toFixed(1)]);
  if(n<1048576)return t('storageKB',[(n/1024).toFixed(1)]);
  const mb=n/1048576;return t('storageMB',[(mb<10?mb.toFixed(1):Math.round(mb))]);
}

// Status first, body second: an error body may be empty or non-JSON (a strict
// binder rejection, a proxy page), and it is the status — plus the server's
// code/retryable when present — that drives the retry decision, never the
// parseability of the body. Network failures (no response at all) are the one
// case where the same request may have committed and is always retryable.
export async function responseResult(response){
  const status=response.status,ok=response.ok;
  let data=null;
  try{data=await response.json();}catch{/* empty or non-JSON body: the status still decides */}
  if(ok)return data;
  const error=Error(serverMessage(data?.messageCode||data?.code,data?.error,data?.messageArgs)||t('requestFailed',[status]));
  error.status=status;error.code=data?.code;error.retryable=data?.retryable===true;error.detail=data;
  throw error;
}

export class DraftSession {
  constructor(storage, scope) { this.storage=storage; this.key='gd.draft.v1.'+scope; this.document=null; this.film=null; }
  open(document) {
    this.document=structuredClone(document); this.film=structuredClone(document.film);
    const saved=this.storage.getItem(this.key);
    if(saved){const local=JSON.parse(saved);if(local.document.id===document.id&&local.document.projectId===document.projectId){this.document=local.document;this.film=local.film;}}
    return this.film;
  }
  get dirty(){return JSON.stringify(this.film)!==JSON.stringify(this.document?.film??null);}
  edit(film){this.film=structuredClone(film);this.persist();}
  persist(){if(this.document)this.storage.setItem(this.key,JSON.stringify({document:this.document,film:this.film}));}
  accept(document,submitted){
    // Saving does not discard edits made while the request was in flight.
    const unchanged=JSON.stringify(this.film)===JSON.stringify(submitted);
    this.document=structuredClone(document);
    if(unchanged)this.film=structuredClone(document.film);
    this.persist();return this.film;
  }
  replace(document){this.document=structuredClone(document);this.film=structuredClone(document.film);this.persist();return this.film;}
}

const operationPrefix='gd.operation.v1.';
export class PendingOperations {
  constructor(storage,uuid=()=>crypto.randomUUID()){this.storage=storage;this.uuid=uuid;}
  pending(path){const value=this.storage.getItem(operationPrefix+path);return value?JSON.parse(JSON.parse(value).signature):null;}
  entries(){const found=[];for(let i=0;i<this.storage.length;i++){const key=this.storage.key(i);if(key&&key.startsWith(operationPrefix)){const saved=JSON.parse(this.storage.getItem(key));found.push([key.slice(operationPrefix.length),JSON.parse(saved.signature),saved.context]);}}return found;}
  discard(path){this.storage.removeItem(operationPrefix+path);}
  async run(path,body,send,context){
    const key=operationPrefix+path, signature=JSON.stringify(body);
    const saved=this.storage.getItem(key);let request=saved?JSON.parse(saved):null;
    if(request&&request.signature!==signature)
      throw Error(t('operationUnknown'));
    if(!request){request={signature,requestId:this.uuid(),context};this.storage.setItem(key,JSON.stringify(request));}
    // A discarded in-flight operation may finish after a NEW one has started.
    // Only release our own identity; never erase that new receipt candidate.
    const savedRequest=JSON.stringify(request),release=()=>{if(this.storage.getItem(key)===savedRequest)this.storage.removeItem(key);};
    // Direct's storage scope is per project; its existing HTTP route is not.
    const endpoint=/^projects\/[^/]+\/direct$/.test(path)?'direct':path;
    try{
      const result=await send(endpoint,{...body,requestId:request.requestId});
      release();return result;
    }catch(error){
      // Explicit semantics win, not a blanket 5xx guess. No-response failures
      // may have committed; 408/429 remain transport-level retry exceptions.
      const keep=error.retryable===true||error.status===408||error.status===429||(error.status===undefined&&error.retryable!==false);
      if(!keep)release();
      throw error;
    }
  }
}
