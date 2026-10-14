// perf-probe metric, not look parity: any RGBA channel |delta|>2, mean and max.
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {pathToFileURL} from 'node:url';
const exec=promisify(execFile);
export function rgbaDiff(a,b){
 if(!a.length||a.length!==b.length||a.length%4)throw Error('RGBA lengths/dimensions must agree and contain whole pixels');let changed=0,total=0,max=0;
 for(let i=0;i<a.length;i+=4){let hit=false;for(let c=0;c<4;c++){const d=Math.abs(a[i+c]-b[i+c]);total+=d;max=Math.max(max,d);if(d>2)hit=true;}if(hit)changed++;}
 return {share:changed/(a.length/4),mean:total/a.length,max};
}
export async function decodePicture(file){
 const {stdout}=await exec(process.env.GAMEDIRECTOR_FFMPEG||'ffmpeg',['-v','error','-i',file,'-frames:v','1','-f','image2pipe','-vcodec','pam','-pix_fmt','rgba','pipe:1'],{encoding:'buffer',maxBuffer:256*1024*1024});
 const end=stdout.indexOf('ENDHDR\n');if(end<0)throw Error('ffmpeg did not return PAM RGBA: '+file);const header=stdout.subarray(0,end).toString(),field=name=>Number(header.match(new RegExp('^'+name+' (\\d+)$','m'))?.[1]);
 const width=field('WIDTH'),height=field('HEIGHT'),rgba=stdout.subarray(end+7);if(!width||!height||field('DEPTH')!==4||field('MAXVAL')!==255||rgba.length!==width*height*4)throw Error('invalid ffmpeg RGBA dimensions: '+file);return {width,height,rgba};
}
export async function pictureDiff(a,b){const left=await decodePicture(a),right=await decodePicture(b);if(left.width!==right.width||left.height!==right.height)throw Error('picture dimensions differ');return {width:left.width,height:left.height,...rgbaDiff(left.rgba,right.rgba)};}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){try{if(process.argv.length!==4)throw Error('Usage: node tools/picture_diff.mjs <a.png> <b.png>');console.log(JSON.stringify(await pictureDiff(...process.argv.slice(2))));}catch(e){console.error(e.message);process.exitCode=1;}}
