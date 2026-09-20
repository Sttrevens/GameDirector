from pathlib import Path
import subprocess,shutil,json,argparse,os
p=argparse.ArgumentParser(description='Build the KEEP ROLLING score from CDREBIRTH audio assets.')
p.add_argument('cdrebirth',type=Path)
p.add_argument('output',type=Path,help='Film workspace containing edit.json and takes')
a=p.parse_args()
root=a.cdrebirth.resolve()/'Assets/Art/Audio'
out=a.output.resolve()/'assets'
out.mkdir(parents=True,exist_ok=True)
files=[root/'BGM/No_Drums_Or_Percussion Quantum_Leap.mp3',root/'Horror SFX Free/Ambient/Creepy_ambience_5.wav',root/'Horror SFX Free/Ambient/Radio_static.wav',root/'Horror SFX Free/Monsters & Ghosts/Monster_Roar_2.wav',root/'Horror SFX Free/Stingers and Spooky Triggers/Piano_stinger_dissonent.wav']
for i,f in enumerate(files):
 assert f.is_file(),f
 shutil.copy2(f,out/(str(i)+f.suffix))
args=[os.environ.get('GAMEDIRECTOR_FFMPEG','ffmpeg'),'-v','error','-n']
for i,f in enumerate(files):args+=['-i',str(out/(str(i)+f.suffix))]
filters=[
 '[0:a]atrim=12:66,asetpts=PTS-STARTPTS,aresample=48000,loudnorm=I=-23:TP=-3:LRA=9,volume=0.58,afade=t=in:st=1:d=5,afade=t=out:st=45:d=3[score]',
 '[1:a]aloop=loop=-1:size=2000000,atrim=0:54,aresample=48000,volume=0.075,afade=t=in:d=2,afade=t=out:st=52:d=2[air]',
 '[2:a]atrim=0:2.7,aresample=48000,highpass=f=400,lowpass=f=4200,volume=0.2,afade=t=in:d=0.03,afade=t=out:st=2.3:d=0.4,asplit=3[s0][s1][s2]',
 '[s0]adelay=23000|23000[speaker1]',
 '[s1]adelay=32000|32000[speaker2]',
 '[s2]volume=1.3,adelay=40000|40000[speaker3]',
 '[3:a]atrim=0:4,aresample=48000,volume=0.42,afade=t=in:d=0.03,afade=t=out:st=3:d=1,adelay=43000|43000[roar]',
 '[4:a]atrim=0:6,aresample=48000,volume=0.3,afade=t=in:d=0.03,afade=t=out:st=4:d=2,adelay=10000|10000[reveal]',
 '[score][air][speaker1][speaker2][speaker3][roar][reveal]amix=inputs=7:duration=longest:normalize=0,atrim=0:54,alimiter=limit=0.9,afade=t=out:st=52:d=2[a]']
args+=['-filter_complex',';'.join(filters),'-map','[a]','-t','54','-ar','48000','-ac','2','-c:a','pcm_s24le',str(out/'soundtrack.wav')]
subprocess.run(args,check=True)
(out/'sound-sources.json').write_text(json.dumps({'sources':[str(x) for x in files],'duration':54,'edit':filters},ensure_ascii=False,indent=2))
