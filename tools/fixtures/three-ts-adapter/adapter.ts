import * as THREE from 'three';
import {cube} from 'game:src/models.ts';
export default {
 create(ctx){
  if(ctx.options.side!==1)throw Error('fixture options missing');
  const scene=new THREE.Scene(),roles=new Map();scene.background=new THREE.Color(0x18212a);
  for(const [i,spec] of ctx.manifest.roles.entries())if(spec.presentAtStart){const actor=cube(spec.bindingHint,ctx.options.side);actor.position.x=i*1.5-.75;scene.add(actor);roles.set(spec.id,actor);}
  return {scene,roles};
 },
 spawn(world,id,ctx){const spec=ctx.manifest.roles.find(r=>r.id===id);return cube(spec.bindingHint,ctx.options.side);},
 animate(actor,{worldTime}){actor.rotation.y=worldTime*.1;}
};
