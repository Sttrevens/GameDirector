// Adapter-owned state, integer ticks anchored at world time zero. No frame dt.
export function fixedSteps(state,worldTime,hz,eps,fn){
 if(!Number.isFinite(worldTime)||worldTime<0||!Number.isFinite(hz)||hz<=0||!Number.isFinite(eps)||eps<0)throw Error('fixedSteps needs nonnegative time/epsilon and positive hz');
 if(state.step===undefined)state.step=0;const end=Math.floor((worldTime+eps)*hz);
 if(end<state.step)throw Error('fixedSteps cannot rewind; reset state for a new performance');
 while(state.step<end){const step=state.step+1;fn(1/hz,step/hz);state.step=step;}
}
// Half-open [start,end); an omitted end holds. A gap/before-first returns null.
export function segmentAt(segments,t){const segment=segments.find(s=>t>=s.start&&(s.end===undefined||t<s.end));return segment===undefined?null:segment;}
