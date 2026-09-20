import * as THREE from 'three';
import {createFighter,animateFighter} from '/game/src/fighters.js';
import {createArena} from '/game/src/arena.js';
import {CATALOG} from '/game/src/catalog-roster.js';

export default {
  create() {
    const scene=new THREE.Scene();scene.background=new THREE.Color(0x19151a);scene.fog=new THREE.FogExp2(0x241c21,.018);
    // A seeded, synchronous construction scope; never changes a live game RNG.
    const random=Math.random;let seed=73013;Math.random=()=>{seed=(Math.imul(seed,1664525)+1013904223)>>>0;return seed/4294967296;};
    let arena;const roles=new Map();
    try {
      arena=createArena(scene);
      for(const [id,catalog,x] of [['legion','sand-legion',-1.6],['minotaur','minotaur',1.6]]) {
        const entry=CATALOG.find(c=>c.id===catalog);if(!entry)throw Error('missing source fighter: '+catalog);
        const actor=createFighter({...entry,...entry.look,catalogId:entry.id});actor.position.set(x,0,0);scene.add(actor);roles.set(id,actor);
        actor.userData.directorBase=[];actor.traverse(o=>{if(o!==actor)actor.userData.directorBase.push({o,p:o.position.clone(),q:o.quaternion.clone(),s:o.scale.clone()});});
      }
    } finally {Math.random=random;}
    const rim=new THREE.DirectionalLight(0x88aaff,1.3);rim.position.set(3,5,-3);scene.add(rim);arena.sun.intensity=1.8;
    const key=new THREE.DirectionalLight(0xffe1b2,2.4);key.position.set(-2,4,6);scene.add(key);
    scene.add(new THREE.HemisphereLight(0xb7c6e5,0x624327,1.1));
    return {scene,roles,arena};
  },
  animate(actor,{clip,time,speed}) {
    // The game's procedural poses leave some axes unchanged. Restore its authored
    // bind pose before evaluating so one pose cannot leak into the next take.
    for(const b of actor.userData.directorBase){b.o.position.copy(b.p);b.o.quaternion.copy(b.q);b.o.scale.copy(b.s);}
    animateFighter(actor,{t:time,speed,pose:clip,gait:actor.userData.gait,hit:1});
  },
  evaluate(world,time) {world.arena.dust.rotation.y=time*.015;},
};
