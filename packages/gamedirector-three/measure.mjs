import * as THREE from 'three';
// Fixed protocol schema (like review); read only the just-rendered matrix, never update it.
export function measureRow(index,time,camera,composition){
 const basis=column=>new THREE.Vector3().setFromMatrixColumn(camera.matrixWorld,column).normalize();
 return {frame:index,time,camera:{
  position:new THREE.Vector3().setFromMatrixPosition(camera.matrixWorld).toArray(),
  right:basis(0).toArray(),up:basis(1).toArray(),forward:basis(2).negate().toArray(),
  fov:camera.fov,aspect:camera.aspect},composition};
}
