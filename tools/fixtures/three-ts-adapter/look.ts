import * as THREE from 'three';
import {lit} from './lit';
import base from './adapter';
export default {
 create:lit,animate:base.animate,
 look(renderer,{width,height,world}){
  if(new URLSearchParams(location.search).get('fixture')!=='look')throw Error('stage.query was not appended');
  if(!renderer.extensions.has('EXT_color_buffer_float'))throw Error('fixture HalfFloat presenter unsupported');
  renderer.toneMappingExposure=1.3;const target=new THREE.WebGLRenderTarget(1,1,{type:THREE.HalfFloatType}),scene=new THREE.Scene(),camera=new THREE.OrthographicCamera(-1,1,1,-1,0,1);
  const material=new THREE.ShaderMaterial({uniforms:{image:{value:target.texture}},vertexShader:'varying vec2 uv0;void main(){uv0=uv;gl_Position=vec4(position.xy,0.,1.);}',fragmentShader:'uniform sampler2D image;varying vec2 uv0;void main(){gl_FragColor=texture2D(image,uv0);}'}),quad=new THREE.Mesh(new THREE.PlaneGeometry(2,2),material);scene.add(quad);let sized=false;
  return {setSize(w,h){if(w!==width||h!==height)throw Error('fixture presenter size mismatch');target.setSize(w,h);sized=true;},render(s,c){if(!sized)throw Error('fixture presenter not sized');renderer.setRenderTarget(target);renderer.render(s,c);renderer.setRenderTarget(null);renderer.render(scene,camera);},dispose(){target.dispose();quad.geometry.dispose();material.dispose();}};
 }
};
