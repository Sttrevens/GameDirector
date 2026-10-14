#!/usr/bin/env python3
"""Real editor and GDScript dock probes in an isolated copy (no source caches)."""
import argparse, json, os, platform, re, shlex, shutil, signal, socket, subprocess, sys, time
from pathlib import Path
import stage_godot

REPO = Path(__file__).resolve().parents[1]


def isolate_editor(godot, work):
    """Godot portable mode uses editor_data beside the executable on desktop OSes."""
    if platform.system() not in ('Darwin', 'Linux', 'Windows'):
        raise SystemExit('SKIP: editor-settings isolation is only known for macOS, Linux and Windows.')
    source = godot.resolve(strict=True)
    bundle = next((parent for parent in source.parents if parent.suffix == '.app'), None)
    if bundle:
        portable = work / 'engine' / bundle.name
        shutil.copytree(bundle, portable, symlinks=True)
        binary = portable / source.relative_to(bundle)
    else:
        directory = work / 'engine'; directory.mkdir()
        binary = directory / source.name
        shutil.copy2(source, binary)
        # Mono ships GodotSharp beside the binary; Windows also needs sibling DLLs/console wrapper.
        for sibling in source.parent.iterdir():
            if sibling.name == 'GodotSharp' and sibling.is_dir():
                shutil.copytree(sibling, directory / sibling.name, symlinks=True)
            elif os.name == 'nt' and sibling != source and sibling.suffix.lower() in ('.dll', '.exe'):
                shutil.copy2(sibling, directory / sibling.name)
    (binary.parent / '._sc_').touch()
    print('Editor settings isolation:', binary.parent / 'editor_data')
    return binary


def terminate_group(process, force=False):
    if os.name == 'nt':
        if process.poll() is None:
            subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        try: os.killpg(process.pid, signal.SIGKILL if force else signal.SIGTERM)
        except ProcessLookupError: pass


def terminate_owned(pid):
    if os.name == 'nt':
        subprocess.run(['taskkill', '/PID', str(pid), '/T', '/F'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        try: os.kill(pid, signal.SIGTERM)
        except ProcessLookupError: return
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            try: os.kill(pid, 0)
            except ProcessLookupError: return
            time.sleep(0.05)
        try: os.kill(pid, signal.SIGKILL)
        except ProcessLookupError: pass


def run(godot, work, gd):
    work = work.resolve(); work.mkdir(parents=True)
    godot = isolate_editor(godot, work)
    options = {}
    if gd is None and os.name != 'nt':
        gd = work / 'gd-test-launcher'
        gd.write_text('#!/bin/sh\nexec ' + shlex.quote(shutil.which('dotnet')) + ' ' + shlex.quote(str(REPO / 'src/GameDirector.Cli/bin/Release/net8.0/gd.dll')) + ' "$@"\n')
        gd.chmod(0o755)
    if gd is not None: options['gd'] = str(gd.resolve())
    if os.name != 'nt':
        failure = work / 'gd failure launcher'
        failure.write_text('#!/bin/sh\necho "intentional startup failure" >&2\nexit 7\n'); failure.chmod(0o755)
        capture = work / 'gd capture launcher'
        capture.write_text('#!' + sys.executable + '\nimport os,sys,json,time\nprint(json.dumps({"captured_token":os.getenv("GAMEDIRECTOR_DOCK_OWNER", ""),"argv":sys.argv[1:]}),flush=True)\ntime.sleep(120)\n')
        capture.chmod(0o755)
        signal_failure = work / 'signal-failure'; signal_failure.mkdir()
        refused = signal_failure / 'kill'
        refused.write_text('#!/bin/sh\necho refused > ' + shlex.quote(str(signal_failure / 'called')) + '\nexit 7\n'); refused.chmod(0o755)
        options.update(failure=str(failure), capture=str(capture), signal_failure=str(signal_failure))
    else:
        print('SKIP: POSIX shell startup-failure/secret-capture fixtures; Unix modes are not supported on Windows.')
    project = work / 'project'
    shutil.copytree(REPO / 'samples/godot/stardeck', project, ignore=stage_godot.IGNORE)
    for source, target in [('godot_dock_test', 'dock_test'), ('godot_dock_lifecycle_test', 'dock_lifecycle'),
                           ('godot_dock_freshness_test', 'dock_freshness'), ('godot_dock_crash_test', 'dock_crash'),
                           ('godot_dock_pid_test', 'dock_pid'), ('godot_dock_owner_test', 'dock_owner'),
                           ('godot_dock_security_test', 'dock_security'), ('godot_dock_state_test', 'dock_state'),
                           ('godot_dock_state_editor_test', 'dock_state_editor'), ('godot_dock_idle', 'dock_idle')]:
        shutil.copy2(REPO / 'tools' / (source + '.gd'), project / (target + '.gd'))
    probe = project / 'addons/dock_probe'; probe.mkdir()
    shutil.copy2(REPO / 'tools/godot_dock_editor_test.gd', probe / 'probe.gd')
    (probe / 'plugin.cfg').write_text('[plugin]\nname="Dock test"\ndescription="Regression probe"\nauthor="GameDirector"\nversion="1"\nscript="probe.gd"\n')
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0)); options['port'] = listener.getsockname()[1]
    (project / 'dock_options.json').write_text(json.dumps(options))
    config = (project / 'project.godot').read_text()
    if gd is not None:
        config += f'\n[gamedirector]\nbridge_port={float(options["port"])}\ngd_path={json.dumps(options["gd"])}\n'
    (project / 'project.godot').write_text(config)
    phases = [('import', ['--editor', '--import', '--text-driver', 'Dummy']),
              ('editor', ['--editor', 'res://stage.tscn', '--quit-after', '8', '--text-driver', 'Dummy'])]
    phases += [(name, ['--script', 'res://' + script + '.gd', '--quit-after', '50000']) for name, script in
               [('listing', 'dock_test'), ('owner', 'dock_owner'), ('security', 'dock_security'), ('state', 'dock_state')]]
    if gd is not None:
        phases += [('lifecycle', ['--script', 'res://dock_lifecycle.gd', '--quit-after', '50000'])]
        editor_phases = ['dock', 'state-stop', 'state-restart', 'state-orphan']
        if 'signal_failure' in options: editor_phases.append('state-signal')
        editor_phases += ['crash', 'recovery']
        phases += [(name, ['--editor', 'res://stage.tscn', '--text-driver', 'Dummy']) for name in editor_phases]
    else:
        print('SKIP: bridge lifecycle/editor recovery phases need --gd <native executable> on Windows.')
    rows = []; orphaned = set()
    try:
        for name, args in phases:
            if name == 'dock':
                path = project / 'project.godot'
                path.write_text(path.read_text().replace('"res://addons/gamedirector/plugin.cfg")', '"res://addons/gamedirector/plugin.cfg", "res://addons/dock_probe/plugin.cfg")'))
            command = [str(godot), '--headless', '--path', str(project), *args]
            stdout_path, stderr_path = work / (name + '.stdout'), work / (name + '.stderr')
            crashed = False
            with stdout_path.open('w') as output, stderr_path.open('w') as errors:
                environment = dict(os.environ)
                if gd is not None: environment['PATH'] = str(gd.resolve().parent) + os.pathsep + environment.get('PATH', '')
                environment['GAMEDIRECTOR_DOCK_TEST_MODE'] = name if name in ('crash', 'recovery') or name.startswith('state-') else ''
                process = subprocess.Popen(command, stdout=output, stderr=errors, text=True, start_new_session=True, env=environment)
                try:
                    deadline = time.monotonic() + (240 if name == 'dock' else 100)
                    while process.poll() is None and time.monotonic() < deadline:
                        if 'ERROR:' in stderr_path.read_text(): raise AssertionError((name, stderr_path.read_text()[-2500:]))
                        if name == 'crash' and 'DOCK_CRASH_READY:' in stdout_path.read_text():
                            process.kill(); process.wait(); crashed = True; break
                        time.sleep(0.1)
                    process.wait(timeout=1)
                finally:
                    # execute_with_pipe/create_process may detach: retain only exact test-owned PIDs.
                    orphaned.update(int(pid) for pid in re.findall(r'DOCK_OWNED_PID:(\d+)', stdout_path.read_text()))
                    record = project / '.godot/gamedirector/bridge.json'
                    if record.exists():
                        try: orphaned.add(int(json.loads(record.read_text())['pid']))
                        except (ValueError, KeyError): pass
                    if not (name == 'crash' and crashed): terminate_group(process)
                    try: process.wait(timeout=15)
                    except subprocess.TimeoutExpired: terminate_group(process, force=True); process.wait()
            text, errors = stdout_path.read_text(), stderr_path.read_text()
            rows.append({'phase': name, 'exit': process.returncode})
            assert (crashed and process.returncode != 0) if name == 'crash' else process.returncode == 0, (name, errors[-2000:])
            assert 'ERROR:' not in text + errors, (name, errors[-2000:])
            for log in (project / '.godot/gamedirector').glob('*.log'):
                assert 'ERROR:' not in log.read_text(), (name, log, log.read_text()[-2000:])
            markers = {'listing': 'DOCK_PASS:', 'owner': 'DOCK_OWNER_PASS:', 'state': 'DOCK_STATE_PASS:',
                       'lifecycle': 'DOCK_LIFECYCLE_PASS:', 'dock': 'DOCK_EDITOR_PASS:', 'recovery': 'DOCK_RECOVERY_PASS:'}
            if name == 'security' and 'capture' in options: markers[name] = 'DOCK_SECURITY_PASS:'
            if name.startswith('state-'): markers[name] = 'DOCK_STATE_EDITOR_PASS:'
            if name in markers: assert markers[name] in text, (name, text[-2000:])
        (work / 'results.json').write_text(json.dumps(rows, indent=2) + '\n')
        print(f'PASS: {len(rows)} phases ({len(rows) - int(gd is not None)} clean exits, {int(gd is not None)} intentional editor kill), real dock callbacks and recovery')
    finally:
        for pid in orphaned: terminate_owned(pid)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True, type=Path)
    parser.add_argument('--work', required=True, type=Path)
    parser.add_argument('--gd', type=Path)
    args = parser.parse_args()
    run(args.godot, args.work, args.gd)
