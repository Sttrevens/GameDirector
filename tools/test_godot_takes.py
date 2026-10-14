#!/usr/bin/env python3
"""G1/G2 CLI film gates. Compact evidence is durable; PNGs/videos use --scratch."""
import argparse, hashlib, json, shutil, subprocess
from pathlib import Path
from godot_test_support import CLI, REPO, serve


def digest(files):
    return hashlib.sha256(b''.join(hashlib.sha256(p.read_bytes()).digest() for p in files)).hexdigest()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--godot', required=True)
    p.add_argument('--scratch', required=True, type=Path)
    p.add_argument('--evidence', required=True, type=Path)
    p.add_argument('--project', type=Path, default=REPO / 'samples/godot/stardeck')
    p.add_argument('--verify-only', action='store_true', help='Verify retained artifacts without rerendering')
    args = p.parse_args(); args.evidence.mkdir(parents=True, exist_ok=True)
    rows = []
    for name, width, height, first in [('landscape',960,540,0), ('portrait',540,960,0), ('fresh',960,540,0), ('preroll',960,540,137)]:
        out = args.scratch / name
        if not args.verify_only:
            record(args, name, width, height, first, out)
        files = sorted((out / 'frames').glob('*.png'))
        assert len(files) == 384-first, len(files)
        artifact = json.loads((out / 'take.json').read_text())
        assert artifact['state'] == 'Verified' and artifact['frameCount'] == 384-first and artifact.get('captureFromFrame',0)==first, artifact
        videos = list(out.glob('*.mp4')); assert len(videos)==1, videos
        probe = json.loads(subprocess.check_output(['ffprobe','-v','error','-count_frames','-select_streams','v:0',
            '-show_entries','stream=width,height,nb_read_frames','-of','json',str(videos[0])]))
        stream=probe['streams'][0]
        assert (stream['width'],stream['height'],int(stream['nb_read_frames'])) == (width,height,384-first),stream
        for filename in ('take.json','receipt.json','job.json'):
            if (out/filename).exists(): shutil.copy2(out/filename,args.evidence/name/filename)
        row=dict(name=name,width=width,height=height,fromFrame=first,frames=len(files),orderedDigest=digest(files),
                 videoSha256=hashlib.sha256(videos[0].read_bytes()).hexdigest(),probe=stream)
        rows.append(row); (args.evidence/'results.json').write_text(json.dumps(rows,indent=2)); print(json.dumps(row),flush=True)
    full={p.name:p for p in (args.scratch/'landscape/frames').glob('*.png')}
    for other, count, offset in [('fresh',384,0),('preroll',247,137)]:
        files=sorted((args.scratch/other/'frames').glob('*.png'))
        # Recorder filenames are capture-relative; status/review indices are timeline-relative.
        assert len(files)==count and all(full[f'{int(p.stem)+offset:06d}.png'].read_bytes()==p.read_bytes() for p in files)
    assert rows[0]['orderedDigest']==rows[2]['orderedDigest']
    print('PASS: G1 fresh 384/384 identical; G2 suffix 247/247 identical')

def record(args, name, width, height, first, out):
    with serve(args.project, args.godot, args.evidence / name) as bridge:
        cmd = ['dotnet',str(CLI),'take',str(REPO / 'samples/timelines/stardeck/v2.json'),
               '--endpoint',bridge.endpoint,'--out',str(out),'--fps','24','--width',str(width),
               '--height',str(height),'--keep-frames','--from-frame',str(first)]
        run = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
        (args.evidence / name / 'take.log').write_text(run.stdout + run.stderr)
        assert run.returncode == 0, run.stderr[-2500:]
        (args.evidence / name / 'status.json').write_text(json.dumps(bridge.get('/status'),indent=2))

if __name__=='__main__': main()
