"""Build an owned, disposable Unity project and verify the independent film.

Requires an explicitly selected Unity editor. A complete package uses its own
native Workbench; source-package validation uses a built Debug Workbench. No
existing game project is opened or modified. Output is retained for inspection.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--editor', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--package', help='Unpacked complete Unity package; defaults to repository package')
    parser.add_argument('--check-launcher', action='store_true', help='Also exercise the bundled native runtime launch in Unity')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    root = Path(args.out).resolve()
    editor = Path(args.editor).resolve()
    if not editor.is_file():
        parser.error('Unity editor does not exist')
    if args.check_launcher and not args.package:
        parser.error('--check-launcher requires a complete --package')
    root.mkdir(parents=True, exist_ok=False)
    project = root / 'project'
    (project / 'Assets/Editor').mkdir(parents=True)
    (project / 'ProjectSettings').mkdir()
    (project / '.gamedirector-validation-project').write_text('Owned by GameDirector validation runner\n')
    source = Path(args.package).resolve() if args.package else repo / 'packages/com.gamedirector.unity'
    metadata = json.loads((source / 'package.json').read_text(encoding='utf-8'))
    target = project / 'Packages' / metadata['name']
    shutil.copytree(source, target)
    if not args.package:
        core = repo / 'src/GameDirector.Core/bin/Debug/netstandard2.1/GameDirector.Core.dll'
        if not core.is_file():
            parser.error('Build the solution before validation')
        shutil.copy2(core, target / 'Plugins/GameDirector.Core.dll')
    dependencies = dict(metadata.get('dependencies', {}))
    dependencies['com.unity.test-framework'] = '1.1.33'
    (project / 'Packages/manifest.json').write_text(json.dumps({
        'dependencies': dependencies, 'testables': [metadata['name']]
    }, indent=2), encoding='utf-8')
    shutil.copy2(repo / 'tools/unity-validation/FilmDocumentValidation.cs', project / 'Assets/Editor')
    command = [sys.executable, str(repo / 'tools/test_unity_documents.py'),
               '--editor', str(editor), '--project', str(project), '--out', str(root / 'evidence')]
    if args.package:
        layout = json.loads((repo / 'tools/distribution.json').read_text(encoding='utf-8'))
        runtime = target / layout['unity']['bundledRuntimeFolder']
        runtime_manifest = json.loads((runtime / layout['runtimeManifestFile']).read_text(encoding='utf-8'))
        executable = runtime / (layout['apps']['workbench']['executable'] + layout['platforms'][runtime_manifest['rid']]['executableExtension'])
        command += ['--workbench', str(executable)]
    result = subprocess.run(command)
    if result.returncode == 0 and args.check_launcher:
        shutil.copy2(repo / 'tools/unity-validation/RuntimeLaunchValidation.cs', project / 'Assets/Editor')
        launch_root = root / 'launcher'
        launch_root.mkdir()
        env = dict(os.environ, GD_VALIDATION_EVIDENCE=str(launch_root))
        env.pop('GAMEDIRECTOR_WORKBENCH', None)
        try:
            result = subprocess.run([str(editor), '-batchmode', '-projectPath', str(project),
                '-executeMethod', 'RuntimeLaunchValidation.Run', '-logFile', str(launch_root / 'unity.log')],
                env=env, timeout=300)
        finally:
            descriptor = launch_root / 'store/.service.json'
            if descriptor.exists():
                # The descriptor lives only in this newly-created owned store.
                service = json.loads(descriptor.read_text(encoding='utf-8'))
                if os.name == 'nt':
                    subprocess.run(['taskkill', '/PID', str(service['processId']), '/T', '/F'],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                else:
                    import signal
                    try:
                        os.kill(service['processId'], signal.SIGTERM)
                    except ProcessLookupError:
                        pass
    return result.returncode


if __name__ == '__main__':
    sys.exit(main())
