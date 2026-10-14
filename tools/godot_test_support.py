"""Test-only Godot fixture/process helpers. Never writes into a source project."""
import contextlib, json, os, select, shutil, signal, subprocess, time, urllib.error, urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
CLI = REPO / 'src/GameDirector.Cli/bin/Release/net8.0/gd.dll'

def host_rid():
    import platform
    system, machine = platform.system(), platform.machine().lower()
    os_name = {'Darwin': 'osx', 'Windows': 'win', 'Linux': 'linux'}.get(system)
    arch = {'arm64': 'arm64', 'aarch64': 'arm64', 'x86_64': 'x64', 'amd64': 'x64'}.get(machine)
    if not os_name or not arch: raise SystemExit(f'Unsupported MCP test host: {system}/{machine}')
    return f'{os_name}-{arch}'

class Bridge:
    def __init__(self, endpoint): self.endpoint = endpoint; self.contract = self.get('/contract')
    def get(self, path):
        with urllib.request.urlopen(self.endpoint + path, timeout=45) as r: return json.loads(r.read())
    def post(self, name, data):
        path = '/' + self.contract['bridge']['routes'][name]
        request = urllib.request.Request(self.endpoint + path, json.dumps(data).encode(), {'Content-Type': 'application/json'})
        with urllib.request.urlopen(request, timeout=self.contract['bridge']['budgets']['takeStartTimeoutSeconds']) as r: return r.read()
    def capture(self, timeline, fps, width, height):
        t = json.loads(self.post('takeStart', dict(timeline=timeline, frameRate=fps, width=width, height=height)))
        try:
            for i in range(t['frameCount']): self.post('takeFrame', dict(takeId=t['takeId'], frameIndex=i))
            snapshot = self.get('/' + self.contract['bridge']['routes']['take'])
            assert snapshot['take']['state'] == self.contract['take']['states']['completed'], snapshot['take']
            assert snapshot['take']['capturedFrames'] == t['frameCount']
            return snapshot
        finally: self.post('takeStop', dict(takeId=t['takeId']))

@contextlib.contextmanager
def serve(project, godot, work, gd=CLI, stage='res://stage.tscn'):
    work = Path(work); work.mkdir(parents=True, exist_ok=True)
    log = work / 'supervisor.log'
    with log.open('w') as errors:
        command = ['dotnet', str(gd)] if Path(gd).suffix == '.dll' else [str(gd)]
        p = subprocess.Popen(command + ['godot', 'serve', '--project', str(project), '--stage', stage,
                              '--godot', str(godot), '--port', '0'], stdout=subprocess.PIPE, stderr=errors)
        try:
            text = b''; deadline = time.monotonic() + 100
            while time.monotonic() < deadline:
                if select.select([p.stdout], [], [], 1)[0]:
                    chunk = os.read(p.stdout.fileno(), 4096)
                    if not chunk: raise RuntimeError(f'supervisor exited {p.poll()}: {log.read_text()[-2500:]}')
                    text += chunk
                    try: endpoint = json.loads(text)['endpoint']; break
                    except json.JSONDecodeError: pass
            else: raise TimeoutError(f'supervisor readiness: {log}')
            yield Bridge(endpoint)
        finally:
            if p.poll() is None: p.send_signal(signal.SIGTERM)
            try: p.wait(timeout=15)
            except subprocess.TimeoutExpired: p.kill(); p.wait()
            p.stdout.close()
    assert p.returncode == 0, f'supervisor exit {p.returncode}: {log}'
    text = log.read_text()
    assert 'SCRIPT ERROR:' not in text and 'ERROR:' not in text, text[-2500:]

def fixture(work, camera):
    project = Path(work) / 'project'; project.mkdir(parents=True)
    shutil.copytree(REPO / 'packages/gamedirector-godot/addons', project / 'addons')
    (project / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="ContractConformance"\n[rendering]\nrenderer/rendering_method="gl_compatibility"\n')
    scene = camera['scene']; vector = lambda v: 'Vector3(' + ','.join(map(str, v)) + ')'
    parts = ['[gd_scene load_steps=3 format=3]\n', '[sub_resource type="BoxMesh" id="box"]\nsize = ' + vector(scene['subjectSize']) + '\n',
             '[sub_resource type="StandardMaterial3D" id="material"]\nalbedo_color = Color(0.8, 0.5, 0.2, 1)\n',
             '[node name="Conformance" type="Node3D"]\nmetadata/gamedirector_game = "ContractConformance"\n',
             '[node name="hero" type="Node3D" parent="."]\nmetadata/gamedirector_role = "hero"\nmetadata/gamedirector_actor = "body"\n',
             '[node name="Visual" type="MeshInstance3D" parent="hero"]\nmesh = SubResource("box")\nmaterial_override = SubResource("material")\nposition = ' + vector(scene['subjectCenter']) + '\n']
    for name, position in dict(anchor=scene['from'], destination=scene['to'], origin=[0,0,0], step=scene['subjectStep']).items():
        parts.append(f'[node name="{name}" type="Marker3D" parent="."]\nmetadata/gamedirector_location = "{name}"\nposition = {vector(position)}\n')
    (project / 'stage.tscn').write_text('\n'.join(parts)); return project
