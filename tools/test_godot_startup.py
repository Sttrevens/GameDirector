#!/usr/bin/env python3
"""Native startup/cache lifecycle regression; --godot accepts Mono or standard builds."""
import argparse, hashlib, json, shutil, time
from pathlib import Path
from godot_test_support import REPO, serve


def cache_digest(project):
    return {str(p.relative_to(project)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in (project / '.godot/imported').rglob('*') if p.is_file()}


def start(project, godot, work, timeline):
    before = time.monotonic()
    with serve(project, godot, work) as bridge:
        startup = time.monotonic() - before
        imported = cache_digest(project)
        assert imported, 'startup did not import assets'
        assert bridge.get('/health')['ok']
        before = time.monotonic()
        receipt = json.loads(bridge.post('takeStart', dict(timeline=timeline, frameRate=24, width=960, height=540)))
        begin = time.monotonic() - before
        try:
            bridge.post('takeFrame', dict(takeId=receipt['takeId'], frameIndex=0))
            assert cache_digest(project) == imported, 'per-take worker changed the imported cache'
        finally:
            bridge.post('takeStop', dict(takeId=receipt['takeId']))
    return dict(startupSeconds=startup, beginSeconds=begin, importedFiles=len(imported), result='pass')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--project', type=Path, required=True, help='Disposable project copy (not the authored sample)')
    parser.add_argument('--count', type=int, default=20)
    parser.add_argument('--cache-regression', action='store_true')
    args = parser.parse_args()
    assert args.count > 0 and args.project.resolve() != (REPO / 'samples/godot/stardeck').resolve()
    args.work.mkdir(parents=True, exist_ok=True)
    if not args.project.exists():
        shutil.copytree(REPO / 'samples/godot/stardeck', args.project,
                        ignore=shutil.ignore_patterns('.godot', '*.import'))
    timeline = json.loads((REPO / 'samples/timelines/stardeck/v2.json').read_text())
    rows = []
    def run(name):
        try:
            row = dict(name=name, **start(args.project, args.godot, args.work / name, timeline))
        except Exception as error:
            rows.append(dict(name=name, result='fail', error=str(error)))
            (args.work / 'results.json').write_text(json.dumps(rows, indent=2))
            raise
        rows.append(row)
        (args.work / 'results.json').write_text(json.dumps(rows, indent=2))
        print(json.dumps(row), flush=True)
    if args.cache_regression:
        run('prime')
        # Source tree is identical, stamp remains, but generated importer outputs are gone.
        # Trusting only the source stamp would launch a worker against broken cache files.
        shutil.rmtree(args.project / '.godot/imported')
        run('damaged-cache')
    else:
        for i in range(args.count):
            cold = i < args.count // 2
            if cold: shutil.rmtree(args.project / '.godot', ignore_errors=True)
            run(f'{"cold" if cold else "warm"}-{i + 1:02}')


if __name__ == '__main__': main()
