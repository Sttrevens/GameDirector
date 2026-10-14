#!/usr/bin/env python3
"""Every DirectorTools tool through gd mcp, real Godot, configurable cold-call deadline."""
import argparse, json, shlex, shutil, subprocess, time
from pathlib import Path
from godot_test_support import REPO, CLI, serve, host_rid
from godot_mcp_support import Mcp
import gamedirector_dist as dist
import stage_godot

DIRECTOR_TOOLS = {'gd_get_manifest', 'gd_validate_timeline', 'gd_status', 'gd_grammar', 'gd_take',
                  'gd_play_timeline', 'gd_capture_frame', 'gd_stop', 'gd_edit', 'gd_director_guide',
                  'gd_doctor', 'gd_performance_catalog', 'gd_resume'}

def developer_distribution(work):
    """Framework-dependent test layout with valid integrity manifests; not a release."""
    root = work / 'developer-distribution'
    config = dist.load_config(REPO)
    version = dist.read_version(REPO)
    folder = root / config['runtimeFolder']
    build = REPO/'src/GameDirector.Mcp/bin/Release/net8.0'
    if not (build/'gamedirector-mcp.dll').is_file():
        raise SystemExit('Prerequisite missing: Release MCP build. Run dotnet build -c Release; install Godot and FFmpeg/ffprobe before this live test.')
    shutil.copytree(build, folder)
    rid = host_rid()
    executable = folder / ('gamedirector-mcp' + config['platforms'][rid]['executableExtension'])
    if not rid.startswith('win-'):
        host = shutil.which('dotnet')
        if not host: raise SystemExit('Prerequisite missing: dotnet on PATH for the developer MCP test layout.')
        executable.write_text('#!/bin/sh\nexec ' + shlex.quote(host) + ' ' + shlex.quote(str(folder/'gamedirector-mcp.dll')) + ' "$@"\n')
        executable.chmod(0o755)
    dist.write_runtime_manifest(folder, config, rid, version)
    dist.write_distribution_manifest_copy(REPO, root)
    return {'GAMEDIRECTOR_DISTRIBUTION_ROOT': str(root)}

def run(args):
    if not args.gd.is_file():
        raise SystemExit('Prerequisite missing: Release CLI build. Run dotnet build -c Release; install Godot and FFmpeg/ffprobe before this live test.')
    args.work.mkdir(parents=True)
    project = args.project
    if project is None:
        project = args.work / 'project'
        shutil.copytree(REPO/'samples/godot/stardeck', project, ignore=stage_godot.IGNORE)
    command = ['dotnet', str(args.gd)] if args.gd.suffix == '.dll' else [str(args.gd)]
    environment = developer_distribution(args.work) if args.gd == CLI else {}
    results = []
    with serve(project, args.godot, args.work/'supervisor', args.gd) as bridge:
        with Mcp(command+['mcp'], bridge.endpoint, args.work/'mcp.log', timeout=args.rpc_timeout, env=environment) as mcp:
            tools = mcp.request('tools/list', {})['tools']
            (args.work/'tools.json').write_text(json.dumps(tools, indent=2))
            assert DIRECTOR_TOOLS <= {tool['name'] for tool in tools}
            # Short real take keeps the all-tools matrix below the per-call deadline.
            source = json.loads(args.timeline.read_text())
            short = dict(version=source['version'], id='godot-mcp-short', title='Godot MCP acceptance',
                         cues=[row for row in source['cues'] if row['t'] == 0])
            for row in short['cues']:
                if 'shot' in row: row['shot']['durationSeconds'] = 1
            timeline = args.work/'short.json'; timeline.write_text(json.dumps(short))
            manifest = args.work/'manifest.json'; manifest.write_text(json.dumps(bridge.get('/manifest')))
            catalog = args.work/'catalog.json'
            setup = subprocess.run(command+['catalog-init', '--manifest', str(manifest), '--out', str(catalog)], capture_output=True, text=True, timeout=30)
            assert setup.returncode == 0, setup.stderr
            edit = args.work/'edit.json'
            edit.write_text(json.dumps(dict(version=1, frameRate=24, width=960, height=540,
                sources={'take': 'take/picture.mp4'}, ranges=[dict(source='take', start=0, end=1, reason='Keep the complete one-second test shot.')])) )
            calls = [
                ('gd_get_manifest', {}, 'success'),
                ('gd_validate_timeline', dict(timelinePath=str(timeline), width=960, height=540), 'success'),
                ('gd_status', {}, 'success'), ('gd_grammar', {}, 'success'),
            ]
            if not args.minimal:
                calls += [
                    ('gd_take', dict(timelinePath=str(timeline), outputDirectory=str(args.work/'take'), frameRate=24, width=960, height=540), 'success'),
                    ('gd_play_timeline', dict(timelinePath=str(timeline)), 'unsupported on Godot'),
                    ('gd_capture_frame', dict(outPath=str(args.work/'interactive.png')), 'unsupported on Godot'),
                    ('gd_stop', {}, 'unsupported on Godot'),
                    ('gd_edit', dict(editPath=str(edit), outputDirectory=str(args.work/'edit')), 'success'),
                    ('gd_director_guide', {}, 'success'),
                    ('gd_doctor', dict(endpoint=bridge.endpoint), 'success'),
                    ('gd_performance_catalog', dict(catalogPath=str(catalog), manifestPath=str(manifest)), 'success'),
                    ('gd_resume', dict(outputDirectory=str(args.work/'take')), 'success'),
                ]
            for name, arguments, expected in calls:
                start = time.monotonic(); answer = mcp.call(name, arguments); seconds = time.monotonic()-start
                text = '\n'.join(block.get('text','') for block in answer.get('content', []))
                passed = not answer.get('isError', False) if expected == 'success' else answer.get('isError', False) and expected in text
                if name == 'gd_validate_timeline': passed = passed and text.startswith('VALID')
                results.append(dict(tool=name, expected=expected, passed=bool(passed), seconds=seconds, result=answer))
                (args.work/'results.json').write_text(json.dumps(results, indent=2))
                assert passed, (name, answer)
            if not args.minimal: assert {row['tool'] for row in results} == DIRECTOR_TOOLS
            matrix = ['| tool | Godot result | expected |', '| --- | --- | --- |']
            for row in results:
                result = 'success' if row['expected'] == 'success' else 'unsupported on Godot (HTTP 409)'
                matrix.append(f"| {row['tool']} | {result}; {row['seconds']:.3f}s | {row['expected']} |")
            (args.work/'matrix.md').write_text('\n'.join(matrix)+'\n')
        assert mcp.p.returncode == 0, ('MCP exit', mcp.p.returncode)
    print(json.dumps([{key:value for key,value in row.items() if key!='result'} for row in results]))

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--work', required=True, type=Path)
    parser.add_argument('--gd', type=Path, default=CLI)
    parser.add_argument('--project', type=Path)
    parser.add_argument('--timeline', type=Path, default=REPO/'samples/timelines/stardeck/v2.json')
    parser.add_argument('--minimal', action='store_true')
    parser.add_argument('--rpc-timeout', type=float, default=60, help='per-RPC absolute deadline in seconds (cold take default: 60)')
    run(parser.parse_args())
