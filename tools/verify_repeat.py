"""Compare two fresh game takes on the same scene, machine and render settings."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('timeline', type=Path)
p.add_argument('--out', type=Path, required=True)
p.add_argument('--endpoint', default='http://localhost:28819')
p.add_argument('--fps', type=int, default=4)
p.add_argument('--width', type=int, default=640)
p.add_argument('--height', type=int, default=360)
a = p.parse_args()
root = a.out.resolve()
root.mkdir(parents=True, exist_ok=False)
cli = Path(__file__).resolve().parents[1] / 'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll'
for name in ('a', 'b'):
    subprocess.run(['dotnet', str(cli), 'take', str(a.timeline.resolve()), '--endpoint', a.endpoint, '--out',
                    str(root / name), '--fps', str(a.fps), '--width', str(a.width),
                    '--height', str(a.height)], check=True)

left = sorted((root / 'a/frames').glob('*.png'))
right = sorted((root / 'b/frames').glob('*.png'))
rows = [dict(frame=i, equal=x.read_bytes() == y.read_bytes(),
             a=hashlib.sha256(x.read_bytes()).hexdigest(),
             b=hashlib.sha256(y.read_bytes()).hexdigest())
        for i, (x, y) in enumerate(zip(left, right))]
events = [json.loads((root / name / 'receipt.json').read_text())['events'] for name in ('a', 'b')]
result = dict(frameCountA=len(left), frameCountB=len(right),
              identicalFrames=sum(row['equal'] for row in rows),
              eventStreamsEqual=events[0] == events[1], frames=rows)
(root / 'repeat.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps({k: v for k, v in result.items() if k != 'frames'}))
raise SystemExit(0 if left and len(left) == len(right) == result['identicalFrames']
                 and result['eventStreamsEqual'] else 1)
