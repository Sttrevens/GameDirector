// Per-tab recovery avoids one browser window overwriting another's unsaved edits.
// The server remains the authority for accepted document revisions.
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

export class PendingOperations {
  constructor(storage,uuid=()=>crypto.randomUUID()){this.storage=storage;this.uuid=uuid;}
  pending(path){const value=this.storage.getItem('gd.operation.v1.'+path);return value?JSON.parse(JSON.parse(value).signature):null;}
  async run(path,body,send){
    const key='gd.operation.v1.'+path, signature=JSON.stringify(body);
    const saved=this.storage.getItem(key);let request=saved?JSON.parse(saved):null;
    if(request&&request.signature!==signature)
      throw Error('上一次操作的结果尚未确认。请保留原输入重试，确认结果后再发起新操作。');
    if(!request){request={signature,requestId:this.uuid()};this.storage.setItem(key,JSON.stringify(request));}
    try{
      const result=await send(path,{...body,requestId:request.requestId});
      this.storage.removeItem(key);return result;
    }catch(error){
      // A lost response/5xx may have committed. Preserve the operation identity.
      if(error.status>=400&&error.status<500)this.storage.removeItem(key);
      throw error;
    }
  }
}
