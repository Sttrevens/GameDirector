"""Authored robot dialogue + CDREBIRTH music/foley. No live gameplay audio claim."""
from pathlib import Path
import argparse,json,subprocess,os,wave,math,struct,hashlib,sys
p=argparse.ArgumentParser();p.add_argument('cdrebirth',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
story=json.loads(Path(__file__).with_name('story.json').read_text());duration=story['duration']
root=a.cdrebirth.resolve()/'Assets/Art/Audio';out=a.output.resolve()/'assets';out.mkdir(parents=True,exist_ok=True)
ff=os.environ.get('GAMEDIRECTOR_FFMPEG','ffmpeg')
lines=story['dialogue']
inputs=[];filters=[];labels=[];ledger=[]
def add(path,chain,at=0):
 i=len(inputs);inputs.append(path);label='a'+str(i)
 filters.append(f'[{i}:a]{chain},aresample=48000,adelay={round(at*1000)}:all=1[{label}]');labels.append(label)
 return i
for n,line in enumerate(lines):
 at,role,text,rate=line['t'],line['role'],line['text'],line['rate']
 voice='Reed (Chinese (China mainland))' if role=='hero' else 'Eddy (Chinese (China mainland))'
 key=hashlib.sha256(json.dumps(dict(voice=voice,rate=rate,text=text,filter='helmet-v1'),sort_keys=True).encode()).hexdigest()[:16];raw=out/f'voice-{key}.aiff';wav=out/f'voice-{key}.wav'
 if not raw.exists():subprocess.run(['say','-v',voice,'-r',str(rate),'-o',str(raw),text],check=True)
 # Helmet radio treatment; two stock synthetic voices, not voice clones.
 if not wav.exists():subprocess.run([ff,'-v','error','-n','-i',str(raw),'-af','highpass=f=230,lowpass=f=3900,acompressor=threshold=0.12:ratio=3:attack=5:release=80,loudnorm=I=-19:TP=-3:LRA=7,afade=t=in:d=0.03,areverse,afade=t=in:d=0.03,areverse','-ar','48000','-ac','2',str(wav)],check=True)
 line_duration=float(subprocess.check_output(['ffprobe','-v','error','-show_entries','format=duration','-of','csv=p=0',str(wav)],text=True))
 add(wav,'volume=1',at);ledger.append(dict(start=at,end=at+line_duration,role=role,text=text,voice=voice))
# Score changes mirror the turn from compliment to danger; space around dialogue.
base=root/'BGM/No_Drums_Or_Percussion Quantum_Leap.mp3'
full=root/'BGM/Quantum_Leap.mp3'
add(base,"atrim=12:29,asetpts=PTS-STARTPTS,loudnorm=I=-28:TP=-5:LRA=9,afade=t=in:d=2,afade=t=out:st=15.5:d=1.5",6)
add(full,"atrim=38:53,asetpts=PTS-STARTPTS,loudnorm=I=-21:TP=-4:LRA=9,afade=t=in:d=4,afade=t=out:st=14.7:d=0.3",26)
add(full,"atrim=74:81,asetpts=PTS-STARTPTS,loudnorm=I=-22:TP=-4:LRA=9,afade=t=in:d=0.2,afade=t=out:st=5:d=2",47)
add(root/'Horror SFX Free/Ambient/Creepy_ambience_5.wav','aloop=loop=-1:size=2000000,atrim=0:54,volume=0.05,afade=t=in:d=1,afade=t=out:st=52:d=2')
add(root/'Horror SFX Free/Monsters & Ghosts/Monster_Roar_2.wav','atrim=0:4,volume=0.5,afade=t=in:d=0.03,afade=t=out:st=3:d=1',26.3)
add(root/'Horror SFX Free/Stingers and Spooky Triggers/Piano_stinger_dissonent.wav','atrim=0:4,volume=0.27,afade=t=in:d=0.03,afade=t=out:st=2:d=2',23)
step=root/'FootStep/ConcreteMud/footstep_bare_concrete_mud_walk_3.wav'
for t in [9.2,9.85,10.5,11.15,11.8]+[36.12+i*.30 for i in range(12)]:
 add(step,'atrim=0:0.28,volume=0.45,afade=t=in:d=0.03,afade=t=out:st=0.23:d=0.05',t)
# Electronic camera arming / recording confirmation, with an authored rising alarm.
rate=48000;dur=54;audio=[0.0]*(rate*dur)
def tone(start,duration,freq,gain):
 for j in range(int(duration*rate)):
  env=min(j/(rate*.015),1,max(0,(duration-j/rate)/.045))
  audio[int(start*rate)+j]+=math.sin(2*math.pi*freq*j/rate)*gain*env
for t in [.15,.32]:tone(t,.1,1046,.15)
for t in [3.05,4.0,14,15.05,24.5,27,37,38.2,46,47]:tone(t,.1,1568,.018)
for t in [23.1,24.0,24.8,25.4]:tone(t,.12,65,.2)
for i,f in enumerate([523,659,784]):tone(13.08+i*.065,.14,f,.035)
for i,f in enumerate([330,247,165]):tone(23.42+i*.08,.12,f,.04)
beeps=out/'camera-foley.wav'
with wave.open(str(beeps),'wb') as w:
 w.setparams((1,2,rate,0,'NONE','not compressed'));w.writeframes(b''.join(struct.pack('<h',round(max(-1,min(1,x))*32767)) for x in audio))
add(beeps,'volume=1')
filters.append(''.join('['+x+']' for x in labels)+f'amix=inputs={len(labels)}:duration=longest:normalize=0,atrim=0:54,alimiter=limit=0.85:level=0,afade=t=out:st=53:d=1[a]')
args=[ff,'-v','error','-n']
for f in inputs:args+=['-i',str(f)]
args+=['-filter_complex',';'.join(filters),'-map','[a]','-t','54','-ar','48000','-ac','2','-c:a','pcm_s24le',str(out/'soundtrack.wav')]
subprocess.run(args,check=True)
(out/'dialogue.json').write_text(json.dumps(ledger,ensure_ascii=False,indent=2)+'\n')
(out/'sound-sources.json').write_text(json.dumps(dict(inputs=[str(x) for x in inputs],filters=filters,voices='macOS stock synthetic helmet voices; authored script'),ensure_ascii=False,indent=2)+'\n')
print(json.dumps(ledger,ensure_ascii=False,indent=2))

subprocess.run([sys.executable,str(Path(__file__).parent.parent/"camdown-pv"/"master_soundtrack.py"),str(out/"soundtrack.wav"),str(out/"mastered.wav")],check=True)
