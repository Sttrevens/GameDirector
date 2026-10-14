import {lit} from './lit';
export default {
 create(ctx){const world=lit(ctx);for(const actor of world.roles.values()){actor.position.x=0;actor.userData.steps={};actor.userData.pose=.3;actor.userData.arrival=Infinity;}return world;},
 animate(actor,{worldTime,arrivedAt,delta}){
  if(actor.name==='companion')return;
  if(arrivedAt!==null)actor.userData.arrival=arrivedAt;
  const {fixedSteps,segmentAt}=window.gd.helpers,segments=[{start:0,end:actor.userData.arrival,pose:.3},{start:actor.userData.arrival,pose:1}];
  fixedSteps(actor.userData.steps,worldTime,60,1e-9,(_,t)=>{const goal=segmentAt(segments,t).pose;actor.userData.pose+=(goal-actor.userData.pose)*.18;});actor.scale.y=actor.userData.pose;
 }
};
