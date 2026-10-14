// Test-only Playwright boundary: actual OS children/profile dirs, no GPU.
// Real server admission/queue/state/cleanup are NOT mocked. Controls inject faults.
const fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process'),{once}=require('node:events');
const read=()=>JSON.parse(fs.readFileSync(process.env.GD_TEST_BROWSER_CONTROL,'utf8'));
const trace=value=>fs.appendFileSync(process.env.GD_TEST_BROWSER_TRACE,JSON.stringify(value)+'\n');
const alive=pid=>{try{process.kill(pid,0);return true;}catch{return false;}};
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const delay=async event=>{const ms=read().delay?.[event];if(ms)await sleep(ms);};
const block=async(event,pid)=>{if(read().block!==event)return;trace({event:'blocked',at:event,pid});while(read().block===event)await sleep(10);};
module.exports={chromium:{launch:async options=>{
 trace({event:'launch-attempt',options});if(read().fault==='launch')throw Error('injected launch failure');
 const child=spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore'});await once(child,'spawn');
 const pid=child.pid,profile=path.join(process.env.GD_TEST_BROWSER_PROFILES,String(pid));fs.mkdirSync(profile,{recursive:true});
 trace({event:'launch',pid,profile,options});await delay('launch');await block('launch',pid);let closed=false,wedged=false;
 const live=()=>{if(closed||!alive(pid))throw Error('browser already closed');};
 const point=async event=>{live();trace({event,pid});const c=read();if(c.fault===event)throw Error('injected '+event+' failure');if(c.wedge===event){wedged=true;throw Error('injected '+event+' timeout: renderer main thread never settles again');}await delay(event);await block(event,pid);};
 return {
  version:()=>{live();return read().version;},
  newBrowserCDPSession:async()=>{await point('cdp');return {send:async method=>{live();await point('cdp-send');if(method!=='SystemInfo.getProcessInfo')throw Error('unexpected CDP call: '+method);return {processInfo:[{type:'browser',id:pid}]};},detach:async()=>{await point('detach');}};},
  newPage:async pageOptions=>{await point('page');trace({event:'page-options',pid,options:pageOptions});return {
   on(event){trace({event:'handler',name:event,pid});},
   // The stage fetch is real: goto fails if the bridge listener has already closed.
   goto:async(url,options)=>{await point('goto');trace({event:'navigation',pid,url,options});const res=await fetch(url);if(!res.ok)throw Error('stage GET answered '+res.status);await res.text();},
   waitForFunction:async(_fn,_arg,options)=>{await point('ready');trace({event:'ready-options',pid,options});},
   // A wedged renderer still traces the attempt, so tests can prove gd.end was never tried.
   evaluate:async(fn,arg)=>{const source=fn.toString(),event=source.includes('UNMASKED_RENDERER_WEBGL')?'renderer':['begin','frame','advance','finish','end'].find(n=>source.includes('.'+n+'('));if(!event)throw Error('unexpected evaluate: '+source);
    if(wedged){trace({event,pid,wedged:true});return new Promise(()=>{});}await point(event==='renderer'?'renderer-read':event);
    if(event==='renderer'){const renderer=read().renderer??(options.args.includes('--use-angle=swiftshader')&&options.args.includes('--enable-unsafe-swiftshader')?'ANGLE SwiftShader test':'ANGLE test GPU');trace({event:'renderer',pid,stage:arg===true,renderer});return renderer;}
    if(event==='begin')return {post:false,width:arg.w,height:arg.h,canvasWidth:arg.w,canvasHeight:arg.h,pixelRatio:1,rtNull:true,shadowAuto:true,exposure:1};
    if(event==='frame'){
     trace({event:'frame-args',pid,arg});const frame=arg.i,time=arg.i/arg.f;
     return {png:'iVBORw0KGgo=',review:{frame,time,composition:{active:false,note:'review-only'}},measures:arg.m?{frame,time,camera:{position:[frame,1,3],right:[1,0,0],up:[0,1,0],forward:[0,0,-1],fov:50,aspect:1},composition:{active:false}}:null};
    }if(event==='finish')return [];
   }
  };},
  close:async()=>{if(closed)return;if(read().fault==='close'){trace({event:'close-failed',pid});throw Error('injected browser.close failure');}closed=true;try{if(alive(pid)){const exit=once(child,'exit');child.kill('SIGTERM');await exit;}}finally{fs.rmSync(profile,{recursive:true,force:true});trace({event:'closed',pid,profile});}}
 };
}}};
