"""Black-box contract exports: gd contract --json and gd compile --take.

These two commands are how non-.NET engines (e.g. the Three bridge) consume the
Core admission decision instead of restating it. Asserts the JSON shapes and
exit codes; uses synthetic fixtures, not a real engine.
"""
import json, math, subprocess, uuid
from pathlib import Path

from test_temp import temp_directory

repo = Path(__file__).resolve().parents[1]
cli = repo / 'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll'
assert cli.is_file(), f'CLI not built: {cli}'
with temp_directory('gd-cli-contract-tests-') as root:

    manifest = dict(game='ContractFixture', gameVersion='1', manifestVersion='0.1',
        roles=[dict(id='hero', defaultActor='body', presentAtStart=True)],
        actors=[dict(id='body', clips=['wave'])],
        locations=[dict(id='front', position=[0, 1, 3]), dict(id='side', position=[3, 1, 1])],
        audio=[dict(id='tone', kind='sfx')], shotTypes=['lockoff'], frameTypes=['full', 'closeup'],
        capabilities={'director.mode': 'offline-sandbox', 'actor.animation.fade': 'cut-only', 'camera.params': 'tilt,pan'})
    (root / 'manifest.json').write_text(json.dumps(manifest))
    valid = dict(version='0.1', id='contract', cues=[
        dict(t=0, type='camera.shot', shot=dict(type='lockoff', subject='hero', frame='full', **{'from': 'front'}, durationSeconds=2.5)),
        dict(t=0.5, type='actor.anim', role='hero', clip='wave', fade=0)])
    (root / 'valid.json').write_text(json.dumps(valid))

    def run(*args):
        return subprocess.run(['dotnet', str(cli), *map(str, args)], capture_output=True, text=True)

    # The versioned graph is frozen by a reviewed golden, not a second value table.
    contract = json.loads(run('contract', '--json').stdout)
    assert contract['schema'] == 'gamedirector.engine-contract' and contract['version'] == dict(major=1, minor=2)
    assert set(contract) == {'schema', 'version', 'capture', 'frameClock', 'camera', 'review', 'measure', 'bridge', 'take', 'capabilities'}
    assert contract == json.loads((repo / 'docs/contract/engine-contract-1.2.json').read_text())
    legacy = json.loads(json.dumps(contract)); del legacy['measure']; del legacy['take']['requestDefaults']['measure']
    legacy['capabilities'] = [r for r in legacy['capabilities'] if r['key'] != 'measure']; legacy['version']['minor'] = 1
    assert legacy == json.loads((repo / 'docs/contract/engine-contract-1.1.json').read_text())
    del legacy['camera']['lens']['aspect']; del legacy['review']['roles']
    assert legacy['review']['optional'].pop() == 'composition.roles'; legacy['version']['minor'] = 0
    assert legacy == json.loads((repo / 'docs/contract/engine-contract-1.0.json').read_text())
    contract_version = f"{contract['version']['major']}.{contract['version']['minor']}"
    clock = contract['frameClock']
    assert run('contract').returncode != 0  # --json is required

    # gd compile --take: compiled timeline + frameCount + admission verdict.
    ok = run('compile', root / 'valid.json', '--manifest', root / 'manifest.json', '--take', '--fps', 24, '--width', 1280, '--height', 720)
    assert ok.returncode == 0, ok.stderr
    out = json.loads(ok.stdout)
    assert out['id'] == 'contract' and out['duration'] == 2.5 and len(out['orderedCues']) == 2
    assert out['frameCount'] == math.ceil(2.5 * 24 - clock['alignmentToleranceFrames']) == 60
    assert out['admission'] == {'ok': True, 'errors': []}
    assert out['contract'] == contract_version and set(out) == {'contract', 'id', 'orderedCues', 'duration', 'frameCount', 'admission'}

    # A late opening camera is legal only when capture starts after its cue; the
    # exported frameCount still includes the replayed prefix, and bounds are Core's.
    late = json.loads(json.dumps(valid)); late['cues'][0]['t'] = 1
    (root / 'late-camera.json').write_text(json.dumps(late))
    for from_frame,accepted in [(0,False),(23,False),(24,True),(83,True),(84,False),(-1,False)]:
     r = run('compile', root / 'late-camera.json', '--manifest', root / 'manifest.json', '--take', '--fps', 24, '--from-frame', from_frame)
     assert r.returncode == (0 if accepted else 1), (from_frame,r.stderr)
     if accepted: assert json.loads(r.stdout)['frameCount'] == 84

    # Frame counts use the exported tolerance, not a second locally chosen epsilon.
    near = json.loads(json.dumps(valid)); near['cues'][0]['shot']['durationSeconds'] = 1 + .000005 / 24
    (root / 'near-frame.json').write_text(json.dumps(near))
    r = run('compile', root / 'near-frame.json', '--manifest', root / 'manifest.json', '--take', '--fps', 24)
    assert r.returncode == 0 and json.loads(r.stdout)['frameCount'] == 24, r.stdout
    near['cues'][0]['shot']['durationSeconds'] = 1 + .00002 / 24
    (root / 'fractional-frame.json').write_text(json.dumps(near))
    r = run('compile', root / 'fractional-frame.json', '--manifest', root / 'manifest.json', '--take', '--fps', 24)
    assert r.returncode == 0 and json.loads(r.stdout)['frameCount'] == 25, r.stdout

    # Admission failures keep the JSON shape and exit non-zero.
    audio = dict(valid, cues=[*valid['cues'], dict(t=1, type='audio.play', audioId='tone')])
    (root / 'audio.json').write_text(json.dumps(audio))
    rejected = run('compile', root / 'audio.json', '--manifest', root / 'manifest.json', '--take')
    assert rejected.returncode == 1 and json.loads(rejected.stdout)['admission']['ok'] is False
    assert any('audio' in e for e in json.loads(rejected.stdout)['admission']['errors'])

    # The manifest capabilities are enforced: fade default 0.2 is cut-only-illegal,
    # and a param outside the declared camera.params list fails.
    fade = dict(valid, cues=[dict(valid['cues'][0]), dict(t=0.5, type='actor.anim', role='hero', clip='wave')])
    (root / 'fade.json').write_text(json.dumps(fade))
    r = run('compile', root / 'fade.json', '--manifest', root / 'manifest.json', '--take')
    assert r.returncode == 1 and 'GD1012' in r.stderr and 'fade: 0' in r.stderr
    param = json.loads(json.dumps(valid)); param['cues'][0]['shot']['params'] = {'shake': 0.4}
    (root / 'param.json').write_text(json.dumps(param))
    r = run('compile', root / 'param.json', '--manifest', root / 'manifest.json', '--take')
    assert r.returncode == 1 and 'GD1012' in r.stderr and 'camera.params' in r.stderr

    # Compile errors surface inside the same admission envelope.
    broken = dict(valid, cues=[dict(valid['cues'][0], shot=dict(type='lockoff', subject='ghost', frame='full', durationSeconds=1))])
    (root / 'broken.json').write_text(json.dumps(broken))
    r = run('compile', root / 'broken.json', '--manifest', root / 'manifest.json', '--take')
    assert r.returncode == 1
    out = json.loads(r.stdout)
    assert out['admission']['ok'] is False and out['frameCount'] == 0 and 'id' not in out

    # Authored files are parsed strictly: a misspelled field fails with its JSON path.
    typo = json.loads(json.dumps(valid)); typo['cues'][0]['shot']['durationSecond'] = 2.5
    del typo['cues'][0]['shot']['durationSeconds']
    (root / 'typo.json').write_text(json.dumps(typo))
    r = run('compile', root / 'typo.json', '--manifest', root / 'manifest.json', '--take')
    assert r.returncode == 1 and 'cues[0].shot.durationSecond: unknown property' in r.stderr, r.stderr
    # A timeline parse refusal is an admission verdict with the one exact envelope.
    assert json.loads(r.stdout) == {'contract': contract_version, 'frameCount': 0, 'admission': {'ok': False, 'errors': [r.stderr.strip()]}}, r.stdout
    missing = dict(valid); del missing['version']
    (root / 'missing-version.json').write_text(json.dumps(missing))
    r = run('compile', root / 'missing-version.json', '--manifest', root / 'manifest.json')
    assert r.returncode == 1 and 'version: required' in r.stderr, r.stderr

    # A strict-parse failure of the ENGINE's manifest is adapter/Core skew, never
    # the film's admission verdict: no envelope at all, so engines map it internal.
    missing_manifest_version = dict(manifest); del missing_manifest_version['manifestVersion']
    for broken_manifest, error in [(dict(manifest, unexpectedField=True), 'unexpectedField: unknown property'),
                                  (missing_manifest_version, 'manifestVersion: required')]:
        (root / 'broken-manifest.json').write_text(json.dumps(broken_manifest))
        r = run('compile', root / 'valid.json', '--manifest', root / 'broken-manifest.json', '--take')
        assert r.returncode != 0 and r.stdout.strip() == '', (r.returncode, r.stdout, r.stderr)
        assert error in r.stderr, r.stderr

    # Without --take the output shape is exactly the compiled timeline as before.
    plain = run('compile', root / 'valid.json', '--manifest', root / 'manifest.json')
    assert plain.returncode == 0
    out = json.loads(plain.stdout)
    assert set(out.keys()) == {'id', 'title', 'orderedCues', 'duration'} or set(out.keys()) == {'id', 'orderedCues', 'duration'}, out.keys()

    print(json.dumps(dict(passed=True, checks=['contract bounds and coverage table', 'params table with units and meanings',
        'bridge error protocol table exported once from Core',
        'compile --take JSON shape and frameCount', 'capture-start camera and bounds through --from-frame', 'exported clock tolerance and near-frame counts', 'audio admission failure', 'cut-only fade GD1012', 'camera.params GD1012',
        'compile errors in the admission envelope', 'compile without --take unchanged',
        'strict authored parse names the misspelled field', 'timeline parse refusal keeps the one envelope', 'engine manifest skew is no admission verdict', 'missing version is required'], evidence=str(root))))
