import * as THREE from 'three';
// Review-only role-bounds-v1. Matrices are those of the just-rendered scene;
// never call computeBoundingBox/Sphere, updateMatrixWorld or skeleton.update.
const legacyGeometry=g=>g.boundingBox?g.boundingBox.clone():localGeometry(g);
function localGeometry(g) {
 // BufferGeometry.computeBoundingBox arithmetic, but only locals, regardless of cache state.
 const box=new THREE.Box3(),position=g.attributes.position,v=new THREE.Vector3();if(position?.isGLBufferAttribute)return box.set(new THREE.Vector3(-Infinity,-Infinity,-Infinity),new THREE.Vector3(Infinity,Infinity,Infinity));if(!position)return box;
 box.setFromBufferAttribute(position);for(const morph of g.morphAttributes.position||[]){const bounds=new THREE.Box3().setFromBufferAttribute(morph);if(g.morphTargetsRelative){box.expandByPoint(v.addVectors(box.min,bounds.min));box.expandByPoint(v.addVectors(box.max,bounds.max));}else{box.expandByPoint(bounds.min);box.expandByPoint(bounds.max);}}return box;
}
function legacyBatchGeometry(o,id) {
 const info=o._geometryInfo[id];if(info.boundingBox)return info.boundingBox.clone();
 // BatchedMesh.getBoundingBoxAt uses the draw range (indices when present), not all allocated vertices.
 const box=new THREE.Box3(),v=new THREE.Vector3(),{index,attributes:{position}}=o.geometry;
 for(let i=info.start;i<info.start+info.count;i++)box.expandByPoint(v.fromBufferAttribute(position,index?index.getX(i):i));return box;
}
function spriteBox(o,camera) {
 const center=new THREE.Vector3().setFromMatrixPosition(o.matrixWorld),scale=new THREE.Vector3().setFromMatrixScale(o.matrixWorld),box=new THREE.Box3();
 if(o.material.sizeAttenuation===false&&camera.isPerspectiveCamera)scale.multiplyScalar(-center.clone().applyMatrix4(camera.matrixWorldInverse).z);
 const right=new THREE.Vector3().setFromMatrixColumn(camera.matrixWorld,0),up=new THREE.Vector3().setFromMatrixColumn(camera.matrixWorld,1),c=Math.cos(o.material.rotation),s=Math.sin(o.material.rotation);
 for(const x of [0,1])for(const y of [0,1]){const dx=(x-o.center.x)*scale.x,dy=(y-o.center.y)*scale.y;box.expandByPoint(center.clone().addScaledVector(right,c*dx-s*dy).addScaledVector(up,s*dx+c*dy));}return box;
}
function posedBox(o,world) {
 const box=new THREE.Box3(),v=new THREE.Vector3();for(let i=0;i<o.geometry.attributes.position.count;i++){o.getVertexPosition(i,v);if(world)v.applyMatrix4(world);box.expandByPoint(v);}return box;
}
export function roleBounds(root,camera,{legacy=false}={}) {
 const result=new THREE.Box3(),matrix=new THREE.Matrix4();
 function visit(o) {
  if(!legacy&&!o.visible)return;
  if(legacy&&o.geometry){const box=o.boundingBox!==undefined?(o.boundingBox?o.boundingBox.clone():o.isSkinnedMesh?posedBox(o):instanceBox(o)):legacyGeometry(o.geometry);result.union(box.applyMatrix4(o.matrixWorld));}
  else if(o.isSprite)result.union(spriteBox(o,camera));
  else if(o.isInstancedMesh)result.union(instanceBox(o,o.matrixWorld));
  else if(o.isBatchedMesh){
   // r185 has sparse instance ids; instanceCount is not the last live id.
   // getBoundingBoxAt writes a lazy cache. Read geometry ranges into locals instead.
   for(let i=0;i<o._instanceInfo.length;i++){
    const instance=o._instanceInfo[i];if(!instance.active||!instance.visible)continue;
    const info=o._geometryInfo[o.getGeometryIdAt(i)],box=new THREE.Box3(),v=new THREE.Vector3(),position=o.geometry.attributes.position;
    for(let j=info.vertexStart;j<info.vertexStart+info.vertexCount;j++)box.expandByPoint(v.fromBufferAttribute(position,j));
    o.getMatrixAt(i,matrix);matrix.premultiply(o.matrixWorld);result.union(box.applyMatrix4(matrix));
   }
  }else if(o.isMesh)result.union(o.isSkinnedMesh?posedBox(o,o.matrixWorld):localGeometry(o.geometry).applyMatrix4(o.matrixWorld));
  for(const child of o.children)visit(child);
 }
 function instanceBox(o,world) {
  if(o.isBatchedMesh){const box=new THREE.Box3();for(let i=0;i<o._instanceInfo.length;i++)if(o._instanceInfo[i].active){const local=legacyBatchGeometry(o,o.getGeometryIdAt(i));o.getMatrixAt(i,matrix);box.union(local.applyMatrix4(matrix));}return box;}
  const box=new THREE.Box3(),local=legacy?legacyGeometry(o.geometry):localGeometry(o.geometry);for(let i=0;i<o.count;i++){o.getMatrixAt(i,matrix);if(world)matrix.premultiply(world);box.union(local.clone().applyMatrix4(matrix));}return box;
 }
 visit(root);return result;
}
export function viewport(point,camera) {
 const depth=-point.clone().applyMatrix4(camera.matrixWorldInverse).z,ndc=point.clone().project(camera);return [(ndc.x+1)/2,(ndc.y+1)/2,depth];
}
export function reviewMessage(e) {try{return String(e?.message||e);}catch{return 'bounds probe failed';}}
export function roleComposition(root,camera,vocabulary) {
 const [inFront,behind,absent,empty,straddles,error]=vocabulary.states;
 try {
  if(!root)return {state:absent};for(let p=root;p;p=p.parent)if(!p.visible)return {state:absent};
  const box=roleBounds(root,camera);if(box.isEmpty())return {state:empty};if([...box.min.toArray(),...box.max.toArray()].some(v=>!Number.isFinite(v)))throw Error('non-finite role bounds');
  const corners=[];for(const x of [box.min.x,box.max.x])for(const y of [box.min.y,box.max.y])for(const z of [box.min.z,box.max.z])corners.push(viewport(new THREE.Vector3(x,y,z),camera));
  const beyond=corners.every(v=>v[2]>camera.near),state=corners.every(v=>v[2]<=0)?behind:beyond?inFront:straddles;
  const projected=viewport(new THREE.Vector3((box.min.x+box.max.x)/2,box.max.y,(box.min.z+box.max.z)/2),camera),crown=projected.every(Number.isFinite)?projected:undefined;
  const rect=beyond?[Math.min(...corners.map(v=>v[0])),Math.min(...corners.map(v=>v[1])),Math.max(...corners.map(v=>v[0])),Math.max(...corners.map(v=>v[1]))]:undefined;
  if(beyond&&corners.flat().some(v=>!Number.isFinite(v)))throw Error('non-finite bounds projection');return {state,rect,crown};
 }catch(e){return {state:error,message:reviewMessage(e)};}
}
