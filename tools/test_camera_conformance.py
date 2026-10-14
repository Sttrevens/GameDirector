"""Live camera aim characterization from EngineContract.camera.aim.samples.

One present role is sufficient. Three supplies GET /contract; for Unity pass
--gd <gd.dll>. Compare the engine's rendered composition reviews (roll included)
at each row's liveTolerance, without changing engine camera behavior.
"""
import argparse, json, math, subprocess, sys, urllib.request

p = argparse.ArgumentParser(); p.add_argument('--endpoint', required=True)
p.add_argument('--gd', help='Core CLI gd.dll for Unity (which has no /contract route)')
p.add_argument('--role', help='One present-at-start role; defaults to the first')
a = p.parse_args()

def get(route, timeout=40):
    with urllib.request.urlopen(a.endpoint + route, timeout=timeout) as r: return json.loads(r.read())

contract = json.loads(subprocess.check_output(['dotnet', a.gd, 'contract', '--json'])) if a.gd else get('/contract')
camera = contract['camera']; states = contract['take']['states']; routes = contract['bridge']['routes']
budgets = contract['bridge']['budgets']

def route(name): return '/' + routes[name]

def post(name, data):
    req = urllib.request.Request(a.endpoint + route(name), data=json.dumps(data).encode(),
                                 headers={'Content-Type': 'application/json'})
    timeout = budgets['takeStartTimeoutSeconds'] if name == 'takeStart' else budgets['requestTimeoutSeconds']
    with urllib.request.urlopen(req, timeout=timeout) as r: return r.read()

manifest = get(route('manifest'), budgets['requestTimeoutSeconds'])
roles = [r['id'] for r in manifest['roles'] if r.get('presentAtStart')]
assert roles, 'conformance needs one role present at start'
role = a.role or roles[0]; assert role in roles, f'role must be present at start: {role}'
assert manifest['locations'], 'conformance needs an explicit camera anchor'
location = manifest['locations'][0]['id']
assert 'full' in manifest['frameTypes'], 'conformance needs the full frame size'
fingerprint = manifest['capabilities']['presentation.sourceFingerprint']

def subject_center(sample):
    """Viewport [x,y,z] from a rendered lockoff review, not a pose-only probe."""
    timeline = {'version': '0.1', 'id': 'camera-conformance', 'cues': [
        {'t': 0, 'type': 'camera.shot', 'shot': {'type': 'lockoff', 'subject': role,
         'frame': 'full', 'from': location, 'durationSeconds': camera['scene']['durationSeconds'],
         'fov': sample['fov'], 'params': {k: sample[k] for k in ('pan', 'tilt', 'roll')}}}]}
    minimum = contract['capture']['minDimension']
    height = max(minimum, math.ceil(minimum / sample['aspect'])); width = round(height * sample['aspect'])
    take = json.loads(post('takeStart', dict(timeline=timeline, frameRate=1, width=width, height=height,
                                            sourceFingerprint=fingerprint)))
    try:
        for i in range(take['frameCount']): post('takeFrame', dict(takeId=take['takeId'], frameIndex=i))
        receipt = get(route('take'), budgets['requestTimeoutSeconds'])
        assert receipt['take']['state'] == states['completed'], receipt['take']
        reviews = [r for r in receipt['reviews'] if r['composition'].get('active')]
        assert reviews, 'no active composition review recorded'
        composition = reviews[0]['composition']; assert composition['subject'] == role, composition
        return composition['center']
    finally: post('takeStop', dict(takeId=take['takeId']))

results = []
# Once per stage: each row owns its aspect, independent of any production take size.
for i, sample in enumerate(camera['aim']['samples'] + camera['lens']['aspect']['samples']):
    x, y, z = subject_center(sample); dx, dy = x - sample['x'], y - sample['y']
    passed = z > 0 and abs(dx) <= sample['liveTolerance'] and abs(dy) <= sample['liveTolerance']
    results.append(dict(row=i, pan=sample['pan'], tilt=sample['tilt'], roll=sample['roll'],
        expected=[sample['x'], sample['y']], actual=[x, y], depth=z, delta=[dx, dy],
        tolerance=sample['liveTolerance'], passed=passed))

passed = all(r['passed'] for r in results)
print(json.dumps(dict(passed=passed, endpoint=a.endpoint, role=role, samples=results)))
if not passed: sys.exit(1)
