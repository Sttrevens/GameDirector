import * as THREE from 'three';

// Engine-level presentation only. A game adapter supplies real visual assets,
// procedural animation and disposal; compiled cue order comes from the .NET core.
export class ThreeDirector {
  constructor(adapter, manifest) { this.adapter=adapter;this.manifest=manifest; }
  begin(compiled,width,height) {
    this.end();this.world=this.adapter.create();this.compiled=compiled;this.index=0;this.time=0;this.worldTime=0;this.scale=1;this.moves=new Map();this.poses=new Map();this.events=[];this.shot=null;
    this.renderer=new THREE.WebGLRenderer({antialias:true,preserveDrawingBuffer:true});this.renderer.setSize(width,height);this.renderer.setPixelRatio(1);this.renderer.shadowMap.enabled=true;this.renderer.shadowMap.type=THREE.PCFSoftShadowMap;this.renderer.outputColorSpace=THREE.SRGBColorSpace;this.renderer.toneMapping=THREE.ACESFilmicToneMapping;this.renderer.toneMappingExposure=1.05;
    document.body.replaceChildren(this.renderer.domElement);this.camera=new THREE.PerspectiveCamera(50,width/height,.03,1000);this.camera.position.set(0,4,10);this.advanceTo(0);
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
      case 'actor.anim':this.role(c.role);this.poses.set(c.role,{clip:c.clip,start:this.worldTime,fade:c.fade||0});break;
      case 'actor.move':this.moves.set(c.role,{to:this.location(c.to),speed:c.speed??3,heading:c.headingTo});this.role(c.role);break;
      case 'actor.face':this.heading(this.role(c.role),c.headingTo);break;
      case 'actor.spawn':{
        if(this.world.roles.get(c.role)?.visible)throw Error('role already present');
        const r=this.adapter.spawn?.(this.world,c.role);if(!r)throw Error('spawn not implemented: '+c.role);
        r.position.copy(this.location(c.location));this.world.roles.set(c.role,r);this.world.scene.add(r);if(c.headingTo)this.heading(r,c.headingTo);break;
      }
      case 'actor.despawn':this.role(c.role).visible=false;this.moves.delete(c.role);if(this.shot?.s.subject===c.role || this.shot?.s.lookAt===c.role)this.shot=null;break;
      case 'camera.shot':{
        const s=c.shot;const subject=this.role(s.subject);const b=new THREE.Box3().setFromObject(subject);const center=b.getCenter(new THREE.Vector3());
        const offset=center.clone().sub(subject.position);if(s.params?.targetHeight!==undefined)offset.set(0,s.params.targetHeight,0);
        this.camera.fov=s.fov??(s.focalLengthMm?THREE.MathUtils.radToDeg(2*Math.atan(12/s.focalLengthMm)):50);this.camera.updateProjectionMatrix();
        let from=s.from==='current'?this.camera.position.clone():this.location(s.from);
        if(s.from==='current') {const coverage={'extreme-closeup':2.8,closeup:1.5,medium:1.05,full:.8,wide:.4}[s.frame]??1;const distance=s.params?.distance??Math.max(.1,b.max.y-b.min.y)/(2*Math.tan(THREE.MathUtils.degToRad(this.camera.fov)/2)*coverage);const back=from.clone().sub(center);if(back.lengthSq()<.01)back.set(0,.25,1);from=center.clone().add(back.normalize().multiplyScalar(distance));}
        const p=s.params||{};from.add(new THREE.Vector3(p.offsetX||0,p.offsetY||0,p.offsetZ||0));
        const to=s.to?(s.to==='current'?this.camera.position.clone():this.location(s.to)):from.clone();to.add(new THREE.Vector3(p.toOffsetX||0,p.toOffsetY||0,p.toOffsetZ||0));
        this.shot={s,from,to,offset,tracking:from.clone().sub(center),start:this.time};break;
      }
      default:throw Error('unsupported picture cue: '+c.type);
    }
    this.evaluate(0,false);
  }
  evaluate(dt,withCamera=true) {
    this.worldTime+=dt*this.scale;
    for(const [id,m] of this.moves) {const r=this.role(id);const delta=m.to.clone().sub(r.position),distance=delta.length(),step=m.speed*dt*this.scale;r.lookAt(m.to.x,r.position.y,m.to.z);if(step>=distance){r.position.copy(m.to);this.moves.delete(id);if(m.heading)this.heading(r,m.heading);}else if(distance>0)r.position.addScaledVector(delta,step/distance);}
    for(const [id,r] of this.world.roles)if(r.visible){const p=this.poses.get(id)||{clip:'idle',start:0,fade:0};this.adapter.animate(r,{clip:p.clip,time:this.worldTime-p.start,worldTime:this.worldTime,speed:this.moves.get(id)?.speed||0,delta:dt*this.scale,fade:p.fade});}
    this.adapter.evaluate?.(this.world,this.worldTime);
    if(withCamera && this.shot){const {s,from,to,offset,tracking,start}=this.shot;let p=s.durationSeconds>0?Math.min(1,Math.max(0,(this.time-start)/s.durationSeconds)):0;const e=s.ease==='linear'?p:s.ease==='in'?p*p:s.ease==='out'?1-(1-p)*(1-p):p*p*(3-2*p);let target=this.role(s.subject).position.clone().add(offset),pos=from.clone();
      if(s.type==='dolly'||s.type==='crane')pos.lerp(to,e);else if(s.type==='tracking')pos.copy(target).add(tracking);else if(s.type==='orbit')pos.copy(tracking).applyAxisAngle(new THREE.Vector3(0,1,0),THREE.MathUtils.degToRad(s.params?.orbitDeg??90)*e).add(target);
      if(s.lookAt)target=this.target(s.lookAt);this.camera.position.copy(pos);this.camera.lookAt(target);this.camera.rotateX(THREE.MathUtils.degToRad(s.params?.tilt||0));this.camera.rotateY(THREE.MathUtils.degToRad(s.params?.pan||0));this.camera.rotateZ(THREE.MathUtils.degToRad(s.params?.roll||0));
    }
  }
  advanceTo(target) {
    if(target<this.time-1e-8)throw Error('cannot rewind an active performance');
    while(this.index<this.compiled.orderedCues.length && this.compiled.orderedCues[this.index].t<=target+1e-8) {const cue=this.compiled.orderedCues[this.index++];const dt=cue.t-this.time;this.time=cue.t;if(dt>0)this.evaluate(dt);this.apply(cue);}
    const dt=target-this.time;this.time=target;this.evaluate(Math.max(0,dt));
  }
  frame(index,fps) {this.advanceTo(index/fps);this.renderer.render(this.world.scene,this.camera);return this.renderer.domElement.toDataURL('image/png').split(',')[1];}
  finish() {this.advanceTo(this.compiled.duration);return this.events;}
  end() {if(this.world){this.adapter.dispose?.(this.world);this.world.scene.traverse(o=>{o.geometry?.dispose();const ms=Array.isArray(o.material)?o.material:[o.material];for(const m of ms)if(m){m.map?.dispose();m.dispose();}});}this.renderer?.dispose();this.world=null;}
}
