// Cosmetic RNG only: no global seed, clock virtualisation or THREE dependency.
export function fnv1a(key){let h=2166136261;for(let i=0;i<key.length;i++)h=Math.imul(h^key.charCodeAt(i),16777619);return h>>>0;}
export function seedFor(key,hash=fnv1a){const seed=hash(key)>>>0;return seed===0?1:seed;}
export function xorshift32(seed){let s=seed;return ()=>{s^=s<<13;s^=s>>>17;s^=s<<5;return (s>>>0)/4294967296;};} // engine-local: xorshift32's algorithmic shifts and uint32 normalization
export function makeEnvelope(hash=fnv1a){
 const streams=new Map(),page=xorshift32(seedFor('page',hash));let draws=0;
 const random=()=>{draws++;return page();};
 return {random,get pageDrawCount(){return draws;},scoped(key,fn){
  if(!streams.has(key))streams.set(key,xorshift32(seedFor(key,hash)));
  const saved=Math.random;Math.random=streams.get(key);try{return fn();}finally{Math.random=saved;}
 }};
}
export function installEnvelope(){const envelope=makeEnvelope();globalThis.gdEnvelope=envelope;Math.random=envelope.random;return envelope;}
