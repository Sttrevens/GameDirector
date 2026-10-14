"""Scene-derived Stardeck vocabulary parity and both-aspect admission. No engine rendering."""
import argparse, json, subprocess
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser()
p.add_argument('--gd', default=str(repo / 'src/GameDirector.Cli/bin/Release/net8.0/gd.dll'))
p.add_argument('--godot', required=True)
p.add_argument('--out', required=True)
a = p.parse_args()

def gd(*args):
    return subprocess.check_output(['dotnet', a.gd, *args], text=True, timeout=90)

manifest = json.loads(gd('godot', 'manifest', '--project', str(repo / 'samples/godot/stardeck'),
                         '--stage', 'res://stage.tscn', '--godot', a.godot))
three = json.loads((repo / 'adapters/stardeck/manifest.json').read_text())
for key in ('roles', 'actors', 'locations'):
    def canonical(rows):
        rows = [dict(r) for r in rows]
        for row in rows:
            if 'clips' in row: row['clips'] = sorted(row['clips'])
            # CLR descriptor default fields are not additional vocabulary.
            for k in ('tags', 'headingDeg'):
                if row.get(k) in ([], 0): row.pop(k, None)
        return sorted(rows, key=lambda r: r['id'])
    assert canonical(manifest[key]) == canonical(three[key]), key
out = Path(a.out); out.write_text(json.dumps(manifest, indent=2) + '\n')
for width, height in ((960, 540), (540, 960)):
    print(gd('validate', str(repo / 'samples/timelines/stardeck/v2.json'), '--manifest', str(out),
             '--width', str(width), '--height', str(height)).strip())
print(json.dumps(dict(roles=len(manifest['roles']), actors=len(manifest['actors']),
                     clips=[len(r['clips']) for r in manifest['actors']], locations=len(manifest['locations']))))
