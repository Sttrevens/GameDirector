import * as THREE from 'three';
import base from './adapter';
export function lit(ctx){
 if(devicePixelRatio!==1||navigator.maxTouchPoints!==0||matchMedia('(prefers-reduced-motion: reduce)').matches)throw Error('fixture device pins missing');
 console.warn('fixture device pins dpr=1 touch=0 reduced=false');console.error('fixture console forwarding');
 const world=base.create(ctx);for(const actor of world.roles.values()){actor.material.dispose();actor.material=new THREE.MeshStandardMaterial({color:actor.material.color});actor.castShadow=true;actor.receiveShadow=true;}
 const sun=new THREE.DirectionalLight(0xffffff,3);sun.position.set(-2,5,4);sun.castShadow=true;sun.shadow.mapSize.set(128,128);world.scene.add(sun,new THREE.AmbientLight(0xffffff,.8));
 const ground=new THREE.Mesh(new THREE.PlaneGeometry(12,12),new THREE.MeshStandardMaterial({color:0x505050}));ground.rotation.x=-Math.PI/2;ground.receiveShadow=true;world.scene.add(ground);return world;
}
