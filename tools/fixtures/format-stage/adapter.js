// Composition-review pure-JS stage: boxes, posed skin, hidden subtree, instances and a sprite.
import * as THREE from 'three';
const box=(x=0,z=0)=>{const m=new THREE.Mesh(new THREE.BoxGeometry(1,2,1),new THREE.MeshBasicMaterial({color:0x80c0e0}));m.position.set(x,1,z);return m;};
function skin() {
 const g=new THREE.BoxGeometry(1,2,1),n=g.attributes.position.count,weights=new Float32Array(n*4);for(let i=0;i<n;i++)weights[i*4]=1;
 g.setAttribute('skinIndex',new THREE.Uint16BufferAttribute(new Uint16Array(n*4),4));g.setAttribute('skinWeight',new THREE.Float32BufferAttribute(weights,4));
 const m=new THREE.SkinnedMesh(g,new THREE.MeshBasicMaterial({color:0xc080e0})),bone=new THREE.Bone();m.position.set(0,1,2);m.add(bone);m.bind(new THREE.Skeleton([bone]));m.computeBoundingBox();return m;
}
export default {
 create(){const scene=new THREE.Scene();scene.background=new THREE.Color(0x234567);const hero=new THREE.Group();hero.add(box());const hidden=new THREE.Group();hidden.visible=false;hidden.add(box(100));
  const partner=new THREE.Group(),instances=new THREE.InstancedMesh(new THREE.BoxGeometry(1,2,1),new THREE.MeshBasicMaterial({color:0xe0a080}),1);instances.setMatrixAt(0,new THREE.Matrix4().makeTranslation(1.5,1,0));partner.add(instances);partner.add(hidden);
  const posed=skin(),sprite=new THREE.Sprite(new THREE.SpriteMaterial({color:0x80e080}));sprite.position.set(-2,1,1);sprite.scale.set(1,2,1);
  const roles=new Map([['hero',hero],['partner',partner],['skin',posed],['sprite',sprite]]);for(const role of roles.values())scene.add(role);return {scene,roles};},
 animate(role){if(role.isSkinnedMesh)role.skeleton.bones[0].position.y=.25;}
};
