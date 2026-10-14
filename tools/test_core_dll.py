#!/usr/bin/env python3
"""Canonical-DLL build/diagnostic regression: owned scratch only, real SDK."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
from test_temp import temp_directory

REPO = Path(__file__).resolve().parents[1]


def copy_inputs(dest):
    (dest / 'tools').mkdir(parents=True)
    for name in ['global.json', 'Directory.Build.props', 'tools/build_core_dll.sh']:
        shutil.copy2(REPO / name, dest / name)
    shutil.copytree(REPO / 'src/GameDirector.Core', dest / 'src/GameDirector.Core',
                    ignore=shutil.ignore_patterns('bin', 'obj'))
    plugin = dest / 'packages/com.gamedirector.unity/Plugins'
    plugin.mkdir(parents=True)
    shutil.copy2(REPO / 'packages/com.gamedirector.unity/Plugins/GameDirector.Core.dll', plugin)


def main():
    with temp_directory('gd-core-dll-test-') as root:
        copy_inputs(root)
        sdk = json.loads((root / 'global.json').read_text())['sdk']['version']
        physical_parent = root / 'physical-scratch'
        physical_parent.mkdir()
        logical_parent = root / 'logical-scratch'
        logical_parent.symlink_to(physical_parent.resolve(), target_is_directory=True)
        # Force different logical/physical paths even when /tmp is not a symlink
        # (Linux CI). Inspect the real compiler invocation through a forwarding host.
        env = {**os.environ, 'TMPDIR': str(logical_parent), 'GD_BUILD_PROBE': str(root / 'invocation.txt')}
        run = subprocess.run(['bash', '-c', '''
            dotnet() {
              if [[ "$1" == build ]]; then
                printf 'physical=%s\\n' "$(pwd -P)" > "$GD_BUILD_PROBE"
                printf '%s\\n' "$@" >> "$GD_BUILD_PROBE"
              fi
              command dotnet "$@"
            }
            export -f dotnet
            bash tools/build_core_dll.sh --check
            '''], cwd=root, env=env,
            capture_output=True, text=True)
        assert run.returncode == 0, run.stdout + run.stderr
        invocation = (root / 'invocation.txt').read_text().splitlines()
        physical = invocation[0].removeprefix('physical=')
        assert Path(physical).parent == physical_parent.resolve(), (physical, logical_parent)
        assert str(logical_parent) != str(logical_parent.resolve())
        mappings = [arg.removeprefix('-p:PathMap=') for arg in invocation if arg.startswith('-p:PathMap=')]
        # An explicit override must match the physical root; without an override,
        # Directory.Build.props provides the canonical mapping itself.
        assert not mappings or mappings == [physical + '=/_/GameDirector'], invocation
        bogus_sdk = '0.0.1'
        assert sdk not in bogus_sdk
        (root / 'global.json').write_text(json.dumps({'sdk': {'version': bogus_sdk, 'rollForward': 'latestMajor'}}))
        missing = subprocess.run(['bash', 'tools/build_core_dll.sh', '--check'], cwd=root, env=env,
                                 capture_output=True, text=True)
        assert missing.returncode == 1, missing
        assert str(root / 'global.json') in missing.stderr, missing.stderr
        assert 'install SDK ' + bogus_sdk in missing.stderr and 'rollForward:disable' in missing.stderr, missing.stderr
        # Only the real host emits an SDK inventory row. The wrapper's pin/hint
        # cannot accidentally satisfy this assertion by containing the same digits.
        assert re.search(r'^' + re.escape(sdk) + r' \[.+\]\s*$', missing.stderr, re.MULTILINE), missing.stderr
        assert not (REPO / 'global.json').samefile(root / 'global.json')
        print(json.dumps({'passed': True, 'physicalPathMap': physical, 'sdk': sdk,
                          'missingSdkRefused': True, 'evidence': str(root)}))


if __name__ == '__main__':
    main()
