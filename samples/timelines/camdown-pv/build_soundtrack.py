"""Authored robot dialogue + CDREBIRTH music/foley. No live gameplay audio claim."""
from pathlib import Path
import argparse,json,subprocess,os,wave,math,struct,hashlib,sys
p=argparse.ArgumentParser();p.add_argument('cdrebirth',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
root=a.cdrebirth.resolve()/'Assets/Art/Audio';out=a.output.resolve()/'assets';out.mkdir(parents=True,exist_ok=True)
ff=os.environ.get('GAMEDIRECTOR_FFMPEG','ffmpeg')
lines=[(.6,'hero','开机。'),(3.6,'hero','你去夸它两句。'),(8.1,'crew','哥，真帅！'),(14.4,'hero','有了有了。'),(17.6,'crew','也就一般吧。'),(27.4,'hero','这条，爆了。'),(32.55,'hero','快跑！'),(38.5,'crew','拍到了吗？'),(40.25,'hero','拍到了。')]
inputs=[];filters=[];labels=[];ledger=[]
def add(path,chain,at=0):
 i=len(inputs);inputs.append(path);label='a'+str(i)
 filters.append(f'[{i}:a]{chain},aresample=48000,adelay={round(at*1000)}:all=1[{label}]');labels.append(label)
 return i
for n,(at,role,text) in enumerate(lines):
 voice='Reed (Chinese (China mainland))' if role=='hero' else 'Eddy (Chinese (China mainland))'
 key=hashlib.sha256(json.dumps(dict(voice=voice,rate=205,text=text,filter='helmet-v1'),sort_keys=True).encode()).hexdigest()[:16];raw=out/f'voice-{key}.aiff';wav=out/f'voice-{key}.wav'
 if not raw.exists():subprocess.run(['say','-v',voice,'-r','205','-o',str(raw),text],check=True)
 # Helmet radio treatment; two stock synthetic voices, not voice clones.
 if not wav.exists():subprocess.run([ff,'-v','error','-n','-i',str(raw),'-af','highpass=f=230,lowpass=f=3900,acompressor=threshold=0.12:ratio=3:attack=5:release=80,loudnorm=I=-19:TP=-3:LRA=7,afade=t=in:d=0.03,areverse,afade=t=in:d=0.03,areverse','-ar','48000','-ac','2',str(wav)],check=True)
 duration=float(subprocess.check_output(['ffprobe','-v','error','-show_entries','format=duration','-of','csv=p=0',str(wav)],text=True))
 add(wav,'volume=1',at);ledger.append(dict(start=at,end=at+duration,role=role,text=text,voice=voice))
# Score changes mirror the turn from compliment to danger; space around dialogue.
base=root/'BGM/No_Drums_Or_Percussion Quantum_Leap.mp3'
full=root/'BGM/Quantum_Leap.mp3'
add(base,"atrim=12:29,asetpts=PTS-STARTPTS,loudnorm=I=-28:TP=-5:LRA=9,afade=t=in:d=2,afade=t=out:st=15.5:d=1.5",3)
add(full,"atrim=38:53,asetpts=PTS-STARTPTS,loudnorm=I=-21:TP=-4:LRA=9,afade=t=in:d=4,afade=t=out:st=14.7:d=0.3",23)
add(full,"atrim=74:80,asetpts=PTS-STARTPTS,loudnorm=I=-22:TP=-4:LRA=9,afade=t=in:d=0.2,afade=t=out:st=4:d=2",42)
add(root/'Horror SFX Free/Ambient/Creepy_ambience_5.wav','aloop=loop=-1:size=2000000,atrim=0:48,volume=0.05,afade=t=in:d=1,afade=t=out:st=46:d=2')
add(root/'Horror SFX Free/Monsters & Ghosts/Monster_Roar_2.wav','atrim=0:4,volume=0.5,afade=t=in:d=0.03,afade=t=out:st=3:d=1',23.3)
add(root/'Horror SFX Free/Stingers and Spooky Triggers/Piano_stinger_dissonent.wav','atrim=0:4,volume=0.27,afade=t=in:d=0.03,afade=t=out:st=2:d=2',20)
step=root/'FootStep/ConcreteMud/footstep_bare_concrete_mud_walk_3.wav'
for t in [6.2,6.85,7.5,8.15,8.8]+[33.12+i*.30 for i in range(12)]:
 add(step,'atrim=0:0.28,volume=0.45,afade=t=in:d=0.03,afade=t=out:st=0.23:d=0.05',t)
# Electronic camera arming / recording confirmation, with an authored rising alarm.
rate=48000;dur=48;audio=[0.0]*(rate*dur)
def tone(start,duration,freq,gain):
 for j in range(int(duration*rate)):
  env=min(j/(rate*.015),1,max(0,(duration-j/rate)/.045))
  audio[int(start*rate)+j]+=math.sin(2*math.pi*freq*j/rate)*gain*env
for t in [.15,.32]:tone(t,.1,1046,.15)
for t in [14.12,14.28,40.05]:tone(t,.1,1568,.075)
for t in [20.1,21.0,21.8,22.4]:tone(t,.12,65,.2)
beeps=out/'camera-foley.wav'
with wave.open(str(beeps),'wb') as w:
 w.setparams((1,2,rate,0,'NONE','not compressed'));w.writeframes(b''.join(struct.pack('<h',round(max(-1,min(1,x))*32767)) for x in audio))
add(beeps,'volume=1')
filters.append(''.join('['+x+']' for x in labels)+f'amix=inputs={len(labels)}:duration=longest:normalize=0,atrim=0:48,alimiter=limit=0.85,afade=t=out:st=47:d=1[a]')
args=[ff,'-v','error','-n']
for f in inputs:args+=['-i',str(f)]
args+=['-filter_complex',';'.join(filters),'-map','[a]','-t','48','-ar','48000','-ac','2','-c:a','pcm_s24le',str(out/'soundtrack.wav')]
subprocess.run(args,check=True)
(out/'dialogue.json').write_text(json.dumps(ledger,ensure_ascii=False,indent=2)+'\n')
(out/'sound-sources.json').write_text(json.dumps(dict(inputs=[str(x) for x in inputs],filters=filters,voices='macOS stock synthetic helmet voices; authored script'),ensure_ascii=False,indent=2)+'\n')
print(json.dumps(ledger,ensure_ascii=False,indent=2))

subprocess.run([sys.executable,str(Path(__file__).with_name("master_soundtrack.py")),str(out/"soundtrack.wav"),str(out/"mastered.wav")],check=True)
