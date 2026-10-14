// Engine-owned GL selection: names, Chromium flags and startup expectations
// live together. No flags or backend defaults belong to Core's contract.
export const GlBackends={
 default:{args:[],expect:null},
 swiftshader:{args:['--use-angle=swiftshader','--enable-unsafe-swiftshader'],expect:/SwiftShader/},
};
export function declaredGl(value){
 if(value!==undefined&&(typeof value!=='string'||!Object.hasOwn(GlBackends,value)))throw Error('unknown three.json gl '+JSON.stringify(value)+'; allowed: '+Object.keys(GlBackends).join(', '));
 return value??'default'; // engine-local: omitted three.json gl and legacy adapters use the platform WebGL path
}
// Serialized by Playwright into the page. Startup creates a WebGL2 context;
// a take measures the actual Three renderer, never a substitute context.
export function readGlRenderer(stage){
 const gl=stage?window.gd.renderer.getContext():document.createElement('canvas').getContext('webgl2');
 if(!gl)throw Error('WebGL2 context unavailable for GL renderer probe');
 const debug=gl.getExtension('WEBGL_debug_renderer_info');if(!debug)throw Error('UNMASKED_RENDERER_WEBGL unavailable');
 const renderer=gl.getParameter(debug.UNMASKED_RENDERER_WEBGL);
 if(typeof renderer!=='string'||!renderer)throw Error('UNMASKED_RENDERER_WEBGL string unavailable');
 return renderer;
}
