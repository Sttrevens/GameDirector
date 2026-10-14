// THREE-free kernels. Immutable formula ids identify these exact double
// expressions and object operations; evaluation order is part of the contract.
export const EaseKernels={identity:p=>p,'quad-in':p=>p*p,'quad-out':p=>1-(1-p)*(1-p),smoothstep:p=>p*p*(3-2*p)};
export const MotionKernels={hold:k=>k.pos,lerp:k=>k.pos.lerp(k.to,k.e),'track-offset':k=>k.pos.copy(k.target).add(k.tracking),
  // +orbitDeg carries the camera toward its own left, matching Unity.
  'orbit-up':k=>k.pos.copy(k.tracking).applyAxisAngle(k.up,-(k.orbitRad*k.e)).add(k.target)};
export const ImplementedFormulas={progress:'clamp01-linear',focalToFov:'vertical-2atan',autoFrameDistance:'height-over-coverage'};
export const isReviewFrame=(i,fps,interval)=>i%(fps*interval)===0;
