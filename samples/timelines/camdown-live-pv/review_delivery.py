"""Retain cut-adjacent and dialogue/result frames from the delivered movie."""
from pathlib import Path
import json,subprocess,os,sys
root=Path(sys.argv[1]).resolve();out=root/'review';out.mkdir(exist_ok=False);frames=out/'frames';frames.mkdir();ff=os.environ.get('GAMEDIRECTOR_FFMPEG','ffmpeg')
edl=json.loads((root/'edit.json').read_text());cuts=sorted({float(r['start']) for r in edl['ranges']}|{49.})
clock=24;duration=54;times=sorted({round(t*clock)/clock for c in cuts for t in [max(0,c-.8),c+.0833333,c+.8] if t<duration}|{.75,2.1,4.6,9.85,10.6,11.4,13.25,14.5,15.6,20.4,21.25,23.6,24.8,27.2,30.5,33.6,36.5,37.5,38.5,39.5,41.6,44.1,46.4,47.6,50,53.5})
indices=sorted(set(round(t*clock) for t in times));select='+'.join(f'eq(n,{i})' for i in indices)
subprocess.run([ff,'-v','error','-n','-i',str(root/'final.mp4'),'-vf',f"select='{select}'",'-fps_mode','vfr','-start_number','0',str(frames/'%03d.png')],check=True)
for i in range(0,len(indices),16):
 subprocess.run([ff,'-v','error','-n','-framerate','1','-start_number',str(i),'-i',str(frames/'%03d.png'),'-vf','scale=480:270,tile=4x4','-frames:v','1',str(out/f'contact-{i//16}.png')],check=True)
(out/'samples.json').write_text(json.dumps([dict(file=f'frames/{i:03d}.png',frame=f,time=f/clock) for i,f in enumerate(indices)],indent=2)+'\n')
print(f'{len(indices)} actual output frames; {len(cuts)} cut boundaries')
