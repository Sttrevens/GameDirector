import * as THREE from 'three';
import {EaseKernels,MotionKernels,isReviewFrame} from './camera-kernels.mjs';
import * as helpers from './helpers.mjs';
import {roleBounds,roleComposition,viewport,reviewMessage} from './role-bounds.mjs';
import {measureRow} from './measure.mjs';

function defaultRenderer(width,height) {
  const renderer=new THREE.WebGLRenderer({antialias:true,preserveDrawingBuffer:true});renderer.setSize(width,height);renderer.setPixelRatio(1);renderer.shadowMap.enabled=true;renderer.shadowMap.type=THREE.PCFSoftShadowMap;renderer.outputColorSpace=THREE.SRGBColorSpace;renderer.toneMapping=THREE.ACESFilmicToneMapping;renderer.toneMappingExposure=1.05;
  return renderer;
}

// Engine-level presentation only. A game adapter supplies real visual assets,
// procedural animation and disposal; compiled cue order comes from the .NET core.
// Camera scalars/defaults come only from the Core graph (gd contract --json,
// served at /contract); kernels keep the existing arithmetic, with orbit's
// sign matching Unity. Renderer look and initial pose remain local choices.
export class ThreeDirector {
  constructor(adapter, manifest, contract, {makeRenderer=defaultRenderer,options={}}={}) {
    this.adapter=adapter;this.helpers=helpers;this.ctx={manifest,options};this.manifest=manifest;this.cueEpsilon=contract.frameClock.cueEpsilonSeconds;this.makeRenderer=makeRenderer;
    const camera=contract.camera;this.lens=camera.lens;this.rig=camera.rig;this.review=contract.review;this.measureDefault=contract.take.requestDefaults.measure;
    this.coverage=new Map(camera.frames.map(f=>[f.id,f.coverage]));this.fallbackCoverage=this.coverage.get(camera.fallbackFrame);
    this.currentAnchor=camera.currentAnchor;this.params=new Map(camera.params.map(p=>[p.name,p.default]));
    this.easeFormulas=new Map(camera.eases.map(e=>[e.id,e.formula]));this.motions=new Map(camera.shots.map(s=>[s.id,s.motion]));
  }
  defaultOf(name) {const value=this.params.get(name);if(value==null)throw Error("shot param '"+name+"' has no contract default (unknown or derived)");return value;}
  hook(key,fn) {const envelope=globalThis.gdEnvelope;return envelope?envelope.scoped(key,fn):fn();}
  get pageStreamDrawCount() {return globalThis.gdEnvelope?.pageDrawCount;}
  presenterFacts() {const size=this.renderer.getDrawingBufferSize?.(new THREE.Vector2()),canvas=this.renderer.domElement;return {post:!!this.presenter,width:size===undefined?canvas?.width:size.x,height:size===undefined?canvas?.height:size.y,canvasWidth:canvas?.width,canvasHeight:canvas?.height,pixelRatio:this.renderer.getPixelRatio?.(),rtNull:this.renderer.getRenderTarget?.()==null,shadowAuto:this.renderer.shadowMap?.autoUpdate,exposure:this.renderer.toneMappingExposure};}
  assertPresentation() {const f=this.presenterFacts();if(!f.rtNull)throw Error('presenter render target must be null after presentation');if(this.renderer.shadowMap&&f.shadowAuto!==true)throw Error('presenter shadowMap.autoUpdate must be true');
    if([[f.width,this.width],[f.height,this.height],[f.canvasWidth,this.width],[f.canvasHeight,this.height],[f.pixelRatio,1]].some(([actual,want])=>actual!==undefined&&actual!==want))throw Error('presenter size mismatch: expected '+this.width+'×'+this.height+' at pixel ratio 1; measured drawing buffer '+f.width+'×'+f.height+', canvas '+f.canvasWidth+'×'+f.canvasHeight+', pixel ratio '+f.pixelRatio);return f;
  }
  begin(compiled,width,height) {
    this.end();this.world=this.hook('create',()=>this.adapter.create(this.ctx));if(!(this.world.scene instanceof THREE.Scene))throw Error('adapter world.scene must be instanceof THREE.Scene (one THREE instance)');this.compiled=compiled;this.index=0;this.time=0;this.worldTime=0;this.scale=1;this.moves=new Map();this.poses=new Map();this.events=[];this.reviews=[];this.shot=null;
    this.renderer=this.makeRenderer(width,height);this.width=width;this.height=height;this.presenter=null;
    if(this.adapter.look){const loop=this.renderer.setAnimationLoop;this.renderer.setAnimationLoop=()=>{throw Error('adapter.look must not call setAnimationLoop');};try{this.presenter=this.hook('look',()=>this.adapter.look(this.renderer,{width,height,world:this.world}));}finally{this.renderer.setAnimationLoop=loop;}}
    if(this.presenter){for(const method of ['render','setSize','dispose'])if(typeof this.presenter[method]!=='function')throw Error('presenter needs '+method);this.presenter.setSize(width,height);}this.assertPresentation();
    document.body.replaceChildren(this.renderer.domElement);this.camera=new THREE.PerspectiveCamera(this.lens.defaultFov,width/height,this.rig.nearClip,this.rig.farClip);this.camera.position.set(0,4,10);this.advanceTo(0);return this.assertPresentation(); // engine-local: unchanged initial camera pose and timeline origin
  }
  role(id) {const role=this.world.roles.get(id);if(!role || !role.visible)throw Error('unbound role: '+id);return role;}
  location(id) {const p=this.manifest.locations.find(l=>l.id===id)?.position;if(!p)throw Error('unbound location: '+id);return new THREE.Vector3(...p);}
  target(id) {const r=this.role(id);return new THREE.Box3().setFromObject(r).getCenter(new THREE.Vector3());}
  heading(r,id) {const p=this.world.roles.has(id)?this.role(id).position:this.location(id);r.lookAt(p.x,r.position.y,p.z);}
  apply(c) {
    this.events.push({t:c.t,type:c.type,role:c.role,label:c.label});
    switch(c.type) {
      case 'marker':break;
      case 'world.timescale':this.scale=c.scale;break;
      case 'actor.anim':this.role(c.role);this.poses.set(c.role,{clip:c.clip,start:this.worldTime,fade:c.fade});break;
      case 'actor.move':this.moves.set(c.role,{to:this.location(c.to),speed:c.speed,heading:c.headingTo});this.role(c.role);break;
      case 'actor.face':this.heading(this.role(c.role),c.headingTo);break;
      case 'actor.spawn':{
        if(this.world.roles.get(c.role)?.visible)throw Error('role already present');
        const r=this.hook('spawn:'+c.role,()=>this.adapter.spawn?.(this.world,c.role,this.ctx));if(!r)throw Error('spawn not implemented: '+c.role);
        r.position.copy(this.location(c.location));this.world.roles.set(c.role,r);this.world.scene.add(r);if(c.headingTo)this.heading(r,c.headingTo);break;
      }
      case 'actor.despawn':this.role(c.role).visible=false;this.moves.delete(c.role);if(this.shot?.s.subject===c.role || this.shot?.s.lookAt===c.role)this.shot=null;break;
      case 'camera.shot':{
        // Resolution order is the Unity rig's, step for step: anchor (or the
        // live camera position), coverage auto-distance for current-anchor
        // shots only, then world offsets, then an explicit-distance
        // re-projection along the current view direction for ANY anchor.
        const s=c.shot,p=s.params||{},param=name=>p[name]??this.defaultOf(name);
        const formula=this.easeFormulas.get(s.ease);if(formula===undefined)throw Error('unknown ease id: '+s.ease);
        if(!Object.hasOwn(EaseKernels,formula))throw Error('unimplemented ease formula: '+formula);const ease=EaseKernels[formula];
        const motionId=this.motions.get(s.type),motion=motionId===undefined?MotionKernels.hold:MotionKernels[motionId];if(typeof motion!=='function')throw Error('unimplemented motion: '+motionId);
        const subject=this.role(s.subject);const b=new THREE.Box3().setFromObject(subject);const center=b.getCenter(new THREE.Vector3());
        const offset=center.clone().sub(subject.position);if(p.targetHeight!==undefined)offset.set(0,p.targetHeight,0);
        const target=()=>this.role(s.subject).position.clone().add(offset);
        this.camera.fov=s.fov??(s.focalLengthMm?THREE.MathUtils.radToDeg(2*Math.atan(this.lens.sensorSizeMm/2/s.focalLengthMm)):this.lens.defaultFov);this.camera.updateProjectionMatrix();
        let from=s.from===this.currentAnchor?this.camera.position.clone():this.location(s.from);
        if(s.from===this.currentAnchor&&param('distance')<=0) {
          const coverage=p.coverage??this.coverage.get(s.frame)??this.fallbackCoverage;
          const distance=Math.max(this.rig.minSubjectHeight,b.max.y-b.min.y)/(2*Math.tan(THREE.MathUtils.degToRad(this.camera.fov)/2)*coverage);
          const back=from.clone().sub(target());if(back.lengthSq()<this.rig.degenerateSqrMagnitude){const f=this.rig.autoFrameFallback;back.set(0,f.up,f.back);}
          from=target().add(back.normalize().multiplyScalar(distance));
        }
        from.add(new THREE.Vector3(param('offsetX'),param('offsetY'),param('offsetZ')));
        if(param('distance')>0){const d=from.clone().sub(target());if(d.lengthSq()<this.rig.degenerateSqrMagnitude){const f=this.rig.distanceFallback;d.set(0,f.up,f.back);}from=target().add(d.normalize().multiplyScalar(param('distance')));}
        const to=s.to?(s.to===this.currentAnchor?this.camera.position.clone():this.location(s.to)):from.clone();to.add(new THREE.Vector3(param('toOffsetX'),param('toOffsetY'),param('toOffsetZ')));
        this.shot={s,from,to,offset,param,ease,motion,motionId,startFov:this.camera.fov,tracking:from.clone().sub(target()),start:this.time};break;
      }
      default:throw Error('unsupported picture cue: '+c.type);
    }
    this.evaluate(0,false);
  }
  evaluate(dt,withCamera=true) {
    const worldDelta=dt*this.scale,motion=new Map();this.worldTime+=worldDelta;
    for(const [id,m] of this.moves) {const r=this.role(id);const delta=m.to.clone().sub(r.position),distance=delta.length(),step=m.speed*dt*this.scale,arrived=step>=distance;motion.set(id,{moved:Math.min(step,distance),arrivedAt:arrived?this.worldTime-worldDelta+distance/m.speed:null});r.lookAt(m.to.x,r.position.y,m.to.z);if(arrived){r.position.copy(m.to);this.moves.delete(id);if(m.heading)this.heading(r,m.heading);}else if(distance>0)r.position.addScaledVector(delta,step/distance);}
    // A role with no anim cue gets clip:null; the adapter picks its rest pose.
    for(const [id,r] of this.world.roles)if(r.visible){const p=this.poses.get(id)||{clip:null,start:0,fade:0},movement=motion.get(id)||{moved:0,arrivedAt:null};this.hook('role:'+id,()=>this.adapter.animate(r,{clip:p.clip,time:this.worldTime-p.start,worldTime:this.worldTime,speed:this.moves.get(id)?.speed||0,delta:worldDelta,fade:p.fade,...movement}));} // engine-local: rest pose and idle speed, not cue defaults
    this.hook('evaluate',()=>this.adapter.evaluate?.(this.world,this.worldTime));
    if(withCamera && this.shot){const {s,from,to,offset,tracking,start,param,ease,motion,motionId}=this.shot;let p=s.durationSeconds>0?Math.min(1,Math.max(0,(this.time-start)/s.durationSeconds)):0;const e=ease(p);let target=this.role(s.subject).position.clone().add(offset),pos=from.clone();
      motion({pos,to,e,target,tracking,up:new THREE.Vector3(0,1,0),orbitRad:motionId==='orbit-up'?THREE.MathUtils.degToRad(param('orbitDeg')):undefined});
      if(s.lookAt)target=this.target(s.lookAt);
      if(s.params?.endFov!==undefined){this.camera.fov=this.shot.startFov+(s.params.endFov-this.shot.startFov)*e;this.camera.updateProjectionMatrix();}
      this.camera.position.copy(pos);this.camera.lookAt(target);
      // Unity applies LookRotation(dir) * Euler(tilt, pan, roll): intrinsic
      // yaw, then pitch, then roll about the camera's local axes, left-handed
      // (+pan turns right, +tilt looks down, +roll is clockwise for the
      // viewer). This right-handed camera looks down -Z, so the equivalent
      // sequence after lookAt is rotateY(-pan), rotateX(-tilt), rotateZ(roll).
      // Pinned by tools/test_camera_conformance.py on both engines.
      this.camera.rotateY(THREE.MathUtils.degToRad(-param('pan')));
      this.camera.rotateX(THREE.MathUtils.degToRad(-param('tilt')));
      this.camera.rotateZ(THREE.MathUtils.degToRad(param('roll')));
    }
  }
  advanceTo(target) {
    if(target<this.time)throw Error('cannot rewind an active performance');
    while(this.index<this.compiled.orderedCues.length && this.compiled.orderedCues[this.index].t<=target+this.cueEpsilon) {const cue=this.compiled.orderedCues[this.index++];const boundary=Math.min(target,Math.max(this.time,cue.t)),dt=boundary-this.time;this.time=boundary;if(dt>0)this.evaluate(dt);this.apply(cue);}
    const dt=target-this.time;this.time=target;this.evaluate(Math.max(0,dt));
  }
  // Every request owns one performance step, even when no pixels are wanted.
  // A camera need not exist yet: evaluate() already tolerates no active shot.
  advance(index,fps) {this.advanceTo(Math.min(this.compiled.duration,(index+1)/fps));}
  frame(index,fps,measure=this.measureDefault) {
    if(this.presenter)this.presenter.render(this.world.scene,this.camera);else this.renderer.render(this.world.scene,this.camera);this.assertPresentation();
    // Composition reviews follow Core's cadence, counted from timeline frame 0.
    let review=null;if(isReviewFrame(index,fps,this.review.intervalSeconds)){review={frame:index,time:this.time,composition:this.composition()};this.reviews.push(review);}
    const row=measure?measureRow(index,this.time,this.camera,this.composition()):null;
    const png=this.renderer.domElement.toDataURL('image/png').split(',')[1];this.advance(index,fps);
    return {png,review,measures:row};
  }
  composition() {
    if(!this.shot)return {active:false};
    const s=this.shot.s,vocabulary=this.review.roles,[subjectKind,withKind,lookAtKind]=vocabulary.kinds,entries=new Map([[s.subject,subjectKind]]);
    for(const id of s.with||[])if(!entries.has(id))entries.set(id,withKind);if(s.lookAt&&!entries.has(s.lookAt))entries.set(s.lookAt,lookAtKind);
    const roles=[...entries].map(([role,kind])=>({role,kind,...roleComposition(this.world.roles.get(role),this.camera,vocabulary)}));
    // Preserve the legacy cached/hidden-inclusive centre, but compute any null
    // cache into a local. A review probe must never write caches or fail a take.
    let center=null,note;try {const subject=this.world.roles.get(s.subject);if(subject)center=viewport(roleBounds(subject,this.camera,{legacy:true}).getCenter(new THREE.Vector3()),this.camera);}catch(e){note=reviewMessage(e);}
    if(center?.some(v=>!Number.isFinite(v))){center=null;note='non-finite legacy projection';}
    return {active:true,subject:s.subject,center,roles,...(note?{note}:{})};
  }
  finish() {this.advanceTo(this.compiled.duration);return this.events;}
  end() {this.presenter?.dispose();this.presenter=null;if(this.world){this.adapter.dispose?.(this.world);this.world.scene.traverse(o=>{o.geometry?.dispose();const ms=Array.isArray(o.material)?o.material:[o.material];for(const m of ms)if(m){m.map?.dispose();m.dispose();}});}this.renderer?.dispose();this.world=null;}
}
