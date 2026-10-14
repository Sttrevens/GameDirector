// Definitions come from gd contract --json. Only deliberate sites create this
// error; a TypeError, adapter rejection or failed subprocess is internal.
export class BridgeRequestError extends Error {
 constructor(definition,message){super(message);this.status=definition.status;this.code=definition.code;}
}
export function classifyError(error,protocol){
 const definition=error instanceof BridgeRequestError?error:protocol.internal;
 return {status:definition.status,body:{error:error.message,code:definition.code}};
}
// Pure part of the live fingerprint gate: same inputs/diagnosis as the server,
// without requiring a test to mutate package files or launch a browser.
export function assertEngineSource(current,startup,protocol){
 if(current!==startup)throw new BridgeRequestError(protocol.stateConflict,'engine source changed; restart the bridge before another take');
}
