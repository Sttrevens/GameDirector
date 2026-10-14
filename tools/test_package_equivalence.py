#!/usr/bin/env python3
"""Archive default/explicit/slashed Unity installs must equal the tgz, modes included."""
import argparse
import hashlib
import json
import os
import stat
import subprocess
import tarfile
from pathlib import Path


def tree(folder):
    return {path.relative_to(folder).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(folder.rglob('*')) if path.is_file()
            and path.name != '.gamedirector-install.json'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--distribution', required=True, type=Path)
    parser.add_argument('--package', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=False)
    distribution = args.distribution.resolve()
    layout = json.loads((distribution / 'distribution.json').read_text())
    runtime = distribution / layout['runtimeFolder']
    manifest = json.loads((runtime / layout['runtimeManifestFile']).read_text())
    cli = runtime / manifest['apps']['cli']
    env = dict(os.environ, PATH='', DOTNET_MULTILEVEL_LOOKUP='0')
    env.pop('GAMEDIRECTOR_DISTRIBUTION_ROOT', None)
    expected = args.out / 'upm'; expected.mkdir()
    with tarfile.open(args.package) as archive:
        assert {member.name.split('/')[0] for member in archive.getmembers()} == {'package'}, 'UPM tarball root'
        archive.extractall(expected, filter='data')
    wanted = tree(expected / 'package')
    cases = []
    source = str(distribution / layout['unity']['distributionFolder'])
    for name, explicit in [('default', None), ('explicit', source), ('trailing-slash', source + os.sep)]:
        project = args.out / name
        (project / 'Assets').mkdir(parents=True); (project / 'ProjectSettings').mkdir()
        def install(apply=False):
            command = [str(cli), 'unity', 'install', '--project', str(project.resolve())]
            if explicit is not None: command += ['--source', explicit]
            if apply: command.append('--apply')
            result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=180)
            assert result.returncode == 0, (name, result.stderr)
            return json.loads(result.stdout)
        plan = install()
        assert plan['mode'] == 'plan' and plan['writes'] > 0
        assert not (project / 'Packages').exists(), 'plan must not write'
        installed = install(True); assert installed['mode'] == 'installed'
        target = Path(installed['destination']); actual = tree(target)
        assert wanted == actual, {'case': name, 'missing': sorted(wanted.keys() - actual.keys()),
            'extra': sorted(actual.keys() - wanted.keys()),
            'different': [n for n in wanted.keys() & actual.keys() if wanted[n] != actual[n]]}
        executable_count = 0
        if os.name != 'nt':
            bundled = Path(layout['unity']['bundledRuntimeParent']) / layout['runtimeFolder']
            executables = {bundled / n for n in manifest['executables']}
            for path in target.joinpath(bundled).rglob('*'):
                if not path.is_file(): continue
                relative = path.relative_to(target)
                expected_mode = 0o755 if relative in executables else 0o644
                assert stat.S_IMODE(path.stat().st_mode) == expected_mode, (name, relative.as_posix(), oct(path.stat().st_mode), oct(expected_mode))
                assert stat.S_IMODE((expected / 'package' / relative).stat().st_mode) == expected_mode
            executable_count = len(executables)
        assert install(True)['writes'] == 0
        cases.append(dict(name=name, comparedFiles=len(wanted), planWrites=plan['writes'], installWrites=installed['writes'], repeatWrites=0, executables=executable_count))
    assert not (distribution / layout['unity']['distributionFolder'] / layout['unity']['bundledRuntimeParent'] / layout['runtimeFolder']).exists()
    result = dict(passed=True, comparedFiles=len(wanted), planWrites=cases[0]['planWrites'], installWrites=cases[0]['installWrites'], repeatWrites=0,
                  cases=cases, sdkOnPath=False, soleException='.gamedirector-install.json (installer ownership receipt)',
                  runtimeFiles=len(manifest['sha256']), packageSha256=hashlib.sha256(args.package.read_bytes()).hexdigest())
    (args.out / 'result.json').write_text(json.dumps(result, indent=2) + '\n'); print(json.dumps(result))


if __name__ == '__main__': main()
