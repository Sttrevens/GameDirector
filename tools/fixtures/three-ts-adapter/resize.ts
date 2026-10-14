// G1 adversarial look: renderer dimensions must be measured, not echoed.
import look from './look';
export default {...look,look(renderer,ctx){renderer.setSize(1280,720);return look.look(renderer,ctx);}};
