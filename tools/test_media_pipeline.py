"""Real FFmpeg regression: source EOF, frame-rate conversion and caption finishing.

Build the CLI first. Requires FFmpeg/ffprobe; honours GAMEDIRECTOR_FFMPEG.
Artifacts are retained under captures/edit/media-tests for failed-run diagnosis.
"""
import json
import os
import subprocess
import uuid
import re
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
root = repo / 'captures/edit/media-tests' / uuid.uuid4().hex[:12]
root.mkdir(parents=True)
ffmpeg = os.environ.get('GAMEDIRECTOR_FFMPEG', 'ffmpeg')
cli = repo / 'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll'

for fps in (24, 30):
    subprocess.run([ffmpeg, '-v', 'error', '-n', '-f', 'lavfi', '-i',
                    f'testsrc2=size=128x72:rate={fps}:duration=3',
                    '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=3',
                    '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac',
                    '-shortest', str(root / f'{fps}.mp4')], check=True)

(root / 'titles.ass').write_text('''[Script Info]
ScriptType: v4.00+
PlayResX: 128
PlayResY: 72
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, Alignment
Style: Default,Arial,12,&H00FFFFFF,2
[Events]
Format: Layer, Start, End, Style, Text
Dialogue: 0,0:00:00.00,0:00:01.00,Default,TAIL
''')

for captioned in (False, True):
    plan = dict(version=1, frameRate=24, width=128, height=72,
                sources={'a': '24.mp4', 'b': '30.mp4'}, ranges=[
                    dict(source='a', start=2, end=3, reason='CFR source EOF'),
                    dict(source='b', start=2, end=3, reason='Converted source EOF')])
    if captioned:
        plan['subtitles'] = 'titles.ass'
    (root / 'edit.json').write_text(json.dumps(plan))
    output = root / ('captioned' if captioned else 'plain')
    subprocess.run(['dotnet', str(cli), 'edit', str(root / 'edit.json'),
                    '--out', str(output)], check=True)
    receipt = json.loads((output / 'delivery.json').read_text())
    assert receipt['state'] == 'Verified' and receipt['frames'] == 48

# A silent music track must not turn up already-mastered source audio. FFmpeg's
# limiter enables automatic make-up gain by default even below its threshold.
subprocess.run([ffmpeg, '-v', 'error', '-n', '-f', 'lavfi', '-i',
                'anullsrc=r=48000:cl=stereo', '-t', '3', str(root / 'silence.wav')], check=True)
levels=[]
for with_music in (False, True):
    plan=dict(version=1,frameRate=24,width=128,height=72,sources={'a':'24.mp4'},
              ranges=[dict(source='a',start=0,end=3,reason='Preserve mastered source gain')])
    if with_music: plan.update(music='silence.wav',musicVolume=1)
    (root/'gain.json').write_text(json.dumps(plan))
    output=root/('gain-music' if with_music else 'gain-source')
    subprocess.run(['dotnet',str(cli),'edit',str(root/'gain.json'),'--out',str(output)],check=True)
    r=subprocess.run([ffmpeg,'-i',str(output/'final.mp4'),'-af',
                      'atrim=0.5:1.5,volumedetect','-f','null','-'],capture_output=True,text=True,check=True)
    levels.append(float(re.search(r'mean_volume: ([\-\d.]+) dB',r.stderr).group(1)))
assert abs(levels[1]-levels[0])<.15, f'Silent music changed source gain: {levels}'
print(f'PASS: plain/captioned EOF, frame-rate conversion, mastered gain {levels}; {root}')
