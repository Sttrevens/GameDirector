"""Two-pass true-peak mastering before the generic EDL renderer adds titles."""
from pathlib import Path
import subprocess,json,re,os,sys
source=Path(sys.argv[1]);output=Path(sys.argv[2]);ff=os.environ.get('GAMEDIRECTOR_FFMPEG','ffmpeg')
target='I=-19:TP=-2.5:LRA=10'
r=subprocess.run([ff,'-v','info','-i',str(source),'-af','loudnorm='+target+':print_format=json','-f','null','-'],capture_output=True,text=True,check=True)
m=json.loads(re.findall(r'\{\s*"input_i"[\s\S]*?\}',r.stderr)[-1])
filter='loudnorm='+target+':'+':'.join(k+'='+m[v] for k,v in [('measured_I','input_i'),('measured_TP','input_tp'),('measured_LRA','input_lra'),('measured_thresh','input_thresh'),('offset','target_offset')])+':linear=false:print_format=json'
r=subprocess.run([ff,'-v','info','-n','-i',str(source),'-af',filter,'-ar','48000','-ac','2','-c:a','pcm_s24le',str(output)],capture_output=True,text=True,check=True)
output.with_suffix('.json').write_text(json.dumps(dict(source=str(source.resolve()),analysis=m,filter=filter,outputAnalysis=json.loads(re.findall(r'\{\s*"input_i"[\s\S]*?\}',r.stderr)[-1])),indent=2)+'\n')
print(output)
