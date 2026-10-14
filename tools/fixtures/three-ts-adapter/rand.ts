import {lit} from './lit';
export default {
 create(ctx){const world=lit(ctx);for(const actor of world.roles.values())actor.userData.origin=actor.position.clone();return world;},
 animate(actor){actor.position.x=actor.userData.origin.x+(Math.random()-.5)*.7;actor.position.z=(Math.random()-.5)*.7;},
 evaluate(world,time){if(Math.abs(time-7/6)<1e-9)console.warn('fixture page-stream frame=7 draws='+window.gd.pageStreamDrawCount);}
};
