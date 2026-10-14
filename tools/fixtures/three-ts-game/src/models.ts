import * as THREE from 'three';
import {mergeGeometries as examplesMerge} from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import {mergeGeometries as addonsMerge} from 'three/addons/utils/BufferGeometryUtils.js';
import colours from '../art/cubes.json';
import {handheld} from './device';
export function cube(binding:string,side:number){
 if(examplesMerge!==addonsMerge)throw Error('jsm/addons must share one module');
 if(import.meta.env.DEV||handheld)throw Error('fixture define/device alias not applied');
 const colour=colours[binding as keyof typeof colours];if(colour===undefined)throw Error('unknown fixture binding: '+binding);
 const mesh=new THREE.Mesh(new THREE.BoxGeometry(side,side,side),new THREE.MeshBasicMaterial({color:colour}));mesh.position.y=side/2;return mesh;
}
