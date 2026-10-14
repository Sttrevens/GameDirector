import * as THREE from 'three';
import {GLTFLoader} from 'three/addons/loaders/GLTFLoader.js';
import * as SkeletonUtils from 'three/addons/utils/SkeletonUtils.js';
import {RoomEnvironment} from 'three/addons/environments/RoomEnvironment.js';
import {EffectComposer} from 'three/addons/postprocessing/EffectComposer.js';
import {RenderPass} from 'three/addons/postprocessing/RenderPass.js';
import {UnrealBloomPass} from 'three/addons/postprocessing/UnrealBloomPass.js';
import {OutputPass} from 'three/addons/postprocessing/OutputPass.js';
import {ShaderPass} from 'three/addons/postprocessing/ShaderPass.js';

// Stardeck example project: two RobotExpressive performers (CC0) in a
// spaceship hallway (CC-BY 4.0, yeeyeeman). Assets load once at module
// evaluation; create() only clones, so construction is synchronous and
// deterministic.
const loader=new GLTFLoader();
const [robotSrc,hallSrc]=await Promise.all([
  loader.loadAsync(new URL('./assets/RobotExpressive.glb',import.meta.url).href),
  loader.loadAsync(new URL('./assets/space_ship_hallway.glb',import.meta.url).href),
]);

const ROBOT_SCALE=0.4;
const HALL_OFFSET=new THREE.Vector3(-20.115,3.08,11.0); // floor at y=0, corridor centred on x=0, z in [-14.6, 14.6]
const ROLES={bolt:{start:'bolt_start',face:'pip_start',paint:null},pip:{start:'pip_start',face:'bolt_start',paint:0xff5a46}};
const LOOP=new Set(['Idle','Walking','Running','Dance']);
const HOLD=new Set(['Death','Sitting','Standing']);
const FACES={Surprised:'Surprised',Sad:'Sad',Angry:'Angry'}; // expression clips: idle body + face morph
const WALK_SPEED=1.25,RUN_SPEED=3.6,LOCO_FADE=0.25;

function at(ctx,id){const p=ctx.manifest.locations.find(l=>l.id===id)?.position;if(!p)throw Error('missing location '+id);return new THREE.Vector3(...p);}

function makeRobot(paint){
  const root=SkeletonUtils.clone(robotSrc.scene);root.scale.setScalar(ROBOT_SCALE);
  let face=null;
  root.traverse(o=>{
    if(!o.isMesh)return;o.castShadow=true;o.receiveShadow=true;
    o.material=o.material.clone();
    if(o.material.name==='Main'){if(paint!=null)o.material.color.setHex(paint);o.material.roughness=0.38;o.material.metalness=0.15;}
    if(o.material.name==='Grey'){o.material.roughness=0.45;o.material.metalness=0.3;}
    if(o.morphTargetDictionary&&'Surprised' in o.morphTargetDictionary)face=o;
  });
  const mixer=new THREE.AnimationMixer(root);
  const actions=new Map(robotSrc.animations.map(c=>{const a=mixer.clipAction(c);a.play();a.setEffectiveWeight(0);return [c.name,a];}));
  // Skinned bounds are cached on first use. Pose the rest (Idle) skeleton and
  // compute them now, otherwise a pre-render Box3 sees unposed bones far away.
  const idle=actions.get('Idle');idle.setEffectiveWeight(1);idle.time=0;mixer.update(0);root.updateMatrixWorld(true);
  root.traverse(o=>{if(o.isSkinnedMesh){o.skeleton.update();o.computeBoundingBox();o.computeBoundingSphere();o.frustumCulled=false;}});
  idle.setEffectiveWeight(0);
  root.userData.rig={mixer,actions,face,layers:[]};
  return root;
}

// One-shot emotes return to Idle once they finish; loops wrap; holds clamp.
function sample(name,t){
  const clip=FACES[name]?'Idle':name,dur=robotSrc.animations.find(c=>c.name===clip).duration;
  if(LOOP.has(clip))return [{clip,time:((t%dur)+dur)%dur,w:1}];
  if(HOLD.has(clip))return [{clip,time:Math.min(t,dur-1e-4),w:1}];
  const back=Math.min(1,Math.max(0,(t-dur+0.3)/0.3));
  return [{clip,time:Math.min(t,dur-1e-4),w:1-back},{clip:'Idle',time:Math.max(0,t-dur+0.3),w:back}];
}

function haze(){
  // A soft volumetric beam: additive cone, brighter at the top.
  const g=new THREE.ConeGeometry(1.6,6.6,48,1,true);g.translate(0,-3.3,0);
  const m=new THREE.ShaderMaterial({transparent:true,depthWrite:false,blending:THREE.AdditiveBlending,side:THREE.DoubleSide,
    uniforms:{color:{value:new THREE.Color(0xcfe3ff)},strength:{value:0.10}},
    vertexShader:'varying float vY;varying vec3 vN;varying vec3 vV;void main(){vY=position.y;vec4 mv=modelViewMatrix*vec4(position,1.);vN=normalize(normalMatrix*normal);vV=normalize(-mv.xyz);gl_Position=projectionMatrix*mv;}',
    fragmentShader:'uniform vec3 color;uniform float strength;varying float vY;varying vec3 vN;varying vec3 vV;void main(){float fall=clamp(1.+vY/6.6,0.,1.);float rim=pow(abs(dot(vN,vV)),1.5);gl_FragColor=vec4(color*strength*fall*fall*rim,1.);}'});
  return new THREE.Mesh(g,m);
}

export default {
  create(ctx){
    const scene=new THREE.Scene();scene.background=new THREE.Color(0x05070b);scene.fog=new THREE.FogExp2(0x0a0e15,0.014);
    const hall=hallSrc.scene.clone(true);hall.position.copy(HALL_OFFSET);
    hall.traverse(o=>{if(!o.isMesh)return;o.receiveShadow=true;o.castShadow=true;o.material=o.material.clone();const m=o.material;
      if(m.name==='Vent_light'||m.name==='Wall_blue'){m.emissiveIntensity=(m.emissiveIntensity||1)*1.4;}
      if(m.name==='Floor_and_vent'||m.name==='Floor_vent'){m.roughness=0.82;m.metalness=0.05;}
      if(m.name==='Wall_Roof_White'){m.roughness=0.5;m.color.multiplyScalar(0.82);}
    });
    scene.add(hall);
    const roles=new Map();
    for(const [id,r] of Object.entries(ROLES)){const robot=makeRobot(r.paint);robot.position.copy(at(ctx,r.start));const f=at(ctx,r.face);robot.lookAt(f.x,robot.position.y,f.z);robot.name=id;scene.add(robot);roles.set(id,robot);}
    // Lighting: cool ceiling key with soft shadows, warm practicals, studio env for reflections.
    scene.add(new THREE.HemisphereLight(0xdbe8ff,0x1a1d24,0.3));
    const key=new THREE.DirectionalLight(0xf2f6ff,2.0);key.position.set(1.5,9,4);key.target.position.set(0,0,0);key.castShadow=true;
    Object.assign(key.shadow.camera,{left:-6,right:6,top:16,bottom:-16,near:1,far:30});key.shadow.mapSize.set(2048,2048);key.shadow.bias=-0.0004;key.shadow.radius=4;
    scene.add(key,key.target);
    const rim=new THREE.DirectionalLight(0xa9c8ff,1.1);rim.position.set(-2,8,-9);scene.add(rim);
    const fill=new THREE.DirectionalLight(0xffe2c4,0.5);fill.position.set(4,2,10);scene.add(fill);
    for(const z of [-9,-3,3,9]){const p=new THREE.PointLight(0xbcd6ff,5,9,2);p.position.set(0,5.6,z);scene.add(p);const b=haze();b.position.set(0,6.4,z);scene.add(b);}
    return {scene,roles};
  },
  look(renderer,{width,height,world}){
    const pmrem=new THREE.PMREMGenerator(renderer);world.scene.environment=pmrem.fromScene(new RoomEnvironment(),0.04).texture;world.scene.environmentIntensity=0.3;pmrem.dispose();
    renderer.toneMapping=THREE.ACESFilmicToneMapping;renderer.toneMappingExposure=0.92;
    const target=new THREE.WebGLRenderTarget(width,height,{type:THREE.HalfFloatType,samples:4});
    const composer=new EffectComposer(renderer,target);const pass=new RenderPass(world.scene,null);composer.addPass(pass);
    composer.addPass(new UnrealBloomPass(new THREE.Vector2(width,height),0.32,0.55,0.92));
    composer.addPass(new OutputPass());
    composer.addPass(new ShaderPass({uniforms:{tDiffuse:{value:null}},
      vertexShader:'varying vec2 vUv;void main(){vUv=uv;gl_Position=projectionMatrix*modelViewMatrix*vec4(position,1.);}',
      fragmentShader:'uniform sampler2D tDiffuse;varying vec2 vUv;void main(){vec4 c=texture2D(tDiffuse,vUv);vec2 d=vUv-.5;float v=smoothstep(.85,.25,length(d*vec2(1.15,1.)));gl_FragColor=vec4(c.rgb*mix(.62,1.,v),1.);}'}));
    return {render(scene,camera){pass.scene=scene;pass.camera=camera;composer.render();},setSize(w,h){composer.setSize(w,h);},dispose(){composer.dispose();target.dispose();}};
  },
  animate(actor,{clip,time,worldTime,moved,speed}){
    const rig=actor.userData.rig,name=clip??'Idle';
    // Locomotion is implied by motion: a moving role walks, then settles into its cue clip.
    const moving=speed>0||moved>1e-6,want=moving?'__walk':name+'@'+(worldTime-time).toFixed(5);
    const top=rig.layers[rig.layers.length-1];
    if(!top||top.key!==want){rig.layers.push({key:want,clip:moving?'Walking':name,start:moving?worldTime:worldTime-time,since:worldTime,fade:moving||top?.key==='__walk'?LOCO_FADE:(top?0.2:0)});if(rig.layers.length>3)rig.layers.shift();}
    const weights=new Map(),times=new Map();let face=0,faceName=null;
    let remaining=1;
    for(let i=rig.layers.length-1;i>=0&&remaining>1e-4;i--){
      const L=rig.layers[i],w=(L.fade>0?Math.min(1,(worldTime-L.since)/L.fade):1)*remaining;remaining-=w;
      const lt=L.clip==='Walking'?(worldTime-L.start)*1.05:worldTime-L.start;
      for(const s of sample(L.clip,lt)){weights.set(s.clip,(weights.get(s.clip)||0)+w*s.w);if(!times.has(s.clip)||w*s.w>0.5)times.set(s.clip,s.time);}
      if(FACES[L.clip]){face+=w;faceName=FACES[L.clip];}
    }
    for(const [n,a] of rig.actions){const w=weights.get(n)||0;a.enabled=w>0;a.setEffectiveWeight(w);if(w>0)a.time=times.get(n);}
    rig.mixer.update(0);
    if(rig.face){const infl=rig.face.morphTargetInfluences,dict=rig.face.morphTargetDictionary;for(const k of Object.keys(dict))infl[dict[k]]=k===faceName?Math.min(1,face):0;}
  },
};
