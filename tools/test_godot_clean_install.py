#!/usr/bin/env python3
"""Artifact-only native installation. No product sources/scene are read from the repo."""
import argparse, hashlib, json, os, shutil, signal, subprocess, tarfile, time, zipfile
from pathlib import Path
from godot_test_support import serve
from godot_mcp_support import Mcp

class Install:
    def __init__(self, args):
        self.args = args
        self.work = args.work.resolve()
        self.work.mkdir(parents=True)
        self.rows = []
        self.summary = {'commands': self.rows}
        self.gd = None
    def save(self):
        (self.work/'summary.json').write_text(json.dumps(self.summary, indent=2)+'\n')
    def command(self, name, command, timeout=120):
        stdout, stderr = self.work/(name+'.stdout'), self.work/(name+'.stderr')
        with stdout.open('w') as output, stderr.open('w') as errors:
            process = subprocess.Popen([str(x) for x in command], cwd=self.work, stdout=output, stderr=errors, start_new_session=True)
            try: code = process.wait(timeout=timeout)
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try: process.wait(timeout=15)
                    except subprocess.TimeoutExpired: os.killpg(process.pid, signal.SIGKILL); process.wait()
        self.rows.append({'phase': name, 'command': [str(x) for x in command], 'exit': code})
        self.save()
        assert code == 0, (name, code, stderr.read_text()[-2000:], stdout.read_text()[-2000:])
        assert not any(error in stderr.read_text() for error in ('SCRIPT ERROR', 'Parse Error')), (name, stderr.read_text()[-2000:])
        return stdout
    def unpack(self):
        release = self.work/'release'; release.mkdir()
        with tarfile.open(self.args.archive) as archive: archive.extractall(release, filter='data')
        folders = list(release.iterdir())
        assert len(folders) == 1 and folders[0].is_dir(), 'archive must extract one top-level folder'
        release = folders[0]
        layout = json.loads((release/'distribution.json').read_text())
        self.gd = release/layout['runtimeFolder']/layout['apps']['cli']['executable']
        assert self.gd.is_file() and os.access(self.gd, os.X_OK)
        with zipfile.ZipFile(self.args.addon) as addon:
            assert all(name == 'LICENSE' or name.startswith('addons/gamedirector/') for name in addon.namelist())
            assert not any(part in ('.godot','__pycache__','bin','obj') for name in addon.namelist() for part in name.split('/'))
        project = self.work/'empty-project'; project.mkdir()
        (project/'project.godot').write_text('config_version=5\n[application]\nconfig/name="FirstFilm"\n[rendering]\nrenderer/rendering_method="gl_compatibility"\n[importer_defaults]\nscene={"meshes/ensure_tangents": false}\n[editor_plugins]\nenabled=PackedStringArray("res://addons/gamedirector/plugin.cfg")\n')
        with zipfile.ZipFile(self.args.addon) as addon: addon.extractall(project)
        (project/'assets').mkdir()
        for name in ('RobotExpressive.glb','space_ship_hallway.glb'):
            shutil.copy2(release/'samples/godot/stardeck/assets'/name, project/'assets'/name)
        # This standalone test-only builder authors a new scene, not a copied sample scene.
        shutil.copy2(Path(__file__).with_name('godot_install_scene.gd'), project/'build_scene.gd')
        self.summary['archiveSha256'] = digest(self.args.archive)
        self.summary['addonSha256'] = digest(self.args.addon)
        self.save()
        return release, project
    def film(self, project):
        timeline = self.work/'timeline.json'
        timeline.write_text(json.dumps({'version':'0.1','id':'first-film','title':'First Godot film', 'cues':[
            {'t':0,'type':'actor.anim','role':'bolt','clip':'Idle','fade':0},
            {'t':0,'type':'actor.anim','role':'pip','clip':'Idle','fade':0},
            {'t':0,'type':'camera.shot','shot':{'type':'lockoff','subject':'bolt','frame':'full','from':'camera_front',
             'durationSeconds':2,'fov':34,'ease':'linear','params':{'targetHeight':1.15}}}]}))
        self.command('import', [self.args.godot,'--headless','--path',project,'--editor','--import','--text-driver','Dummy'])
        built = self.command('scene-builder', [self.args.godot,'--headless','--path',project,'--script','res://build_scene.gd','--quit-after','30'])
        assert 'SCENE_BUILT:' in built.read_text()
        manifest = self.command('manifest', [self.gd,'godot','manifest','--project',project,'--stage','res://stage.tscn','--godot',self.args.godot])
        parsed = json.loads(manifest.read_text()); self.summary['manifestCounts'] = {key:len(parsed[key]) for key in ('roles','actors','locations')}
        self.command('validate', [self.gd,'validate',timeline,'--manifest',manifest,'--width','960','--height','540'])
        with serve(project,self.args.godot,self.work/'bridge',self.gd) as bridge:
            self.rows.append({'phase':'serve','command':[str(self.gd),'godot','serve','--project',str(project),'--stage','res://stage.tscn','--godot',str(self.args.godot),'--port','0'], 'exit':None,'endpoint':bridge.endpoint})
            self.save()
            self.command('take',[self.gd,'take',timeline,'--endpoint',bridge.endpoint,'--fps','24','--width','960','--height','540','--out',self.work/'take','--keep-frames'])
            review = self.command('review',[self.gd,'review',self.work/'take'])
            self.summary['review'] = json.loads(review.read_text())
            measure = self.command('measure',[self.gd,'measure',timeline,'--endpoint',bridge.endpoint,'--fps','24','--width','960','--height','540','--frames','0,12,24,36,47','--out',self.work/'measure'])
            report = json.loads(measure.read_text())
            assert report['frames'] == [0,12,24,36,47] and len(report['rows']) == 5, report
            self.summary['measure'] = {'rowCount':len(report['rows']), 'frames':report['frames'], 'stdoutSha256':digest(measure)}
            with Mcp([str(self.gd),'mcp'],bridge.endpoint,self.work/'mcp.log') as mcp:
                calls = []
                for name, arguments in [('gd_get_manifest',{}),('gd_validate_timeline',{'timelinePath':str(timeline),'width':960,'height':540})]:
                    started = time.monotonic()
                    answer = mcp.call(name,arguments)
                    seconds = time.monotonic() - started
                    assert not answer.get('isError',False),answer
                    if name == 'gd_validate_timeline': assert answer['content'][0]['text'].startswith('VALID'),answer
                    calls.append({'tool':name,'seconds':seconds,'result':answer})
                (self.work/'mcp-results.json').write_text(json.dumps(calls,indent=2)+'\n')
            assert mcp.p.returncode == 0, ('MCP exit', mcp.p.returncode)
            self.rows.append({'phase':'mcp','command':[str(self.gd),'mcp'],'exit':mcp.p.returncode})
            self.summary['mcp'] = {'exit':mcp.p.returncode,'tools':[row['tool'] for row in calls]}
        next(row for row in self.rows if row['phase']=='serve')['exit'] = 0
        receipt = json.loads((self.work/'take/take.json').read_text())
        frames = sorted((self.work/'take/frames').glob('*.png'))
        hashes = hashlib.sha256()
        for frame in frames: hashes.update(bytes.fromhex(digest(frame)))
        assert receipt['state']=='Verified' and receipt['frameCount']==48 and len(frames)==48,receipt
        self.summary['take'] = {'state':receipt['state'],'frameCount':receipt['frameCount'],'pngCount':len(frames),'pngOrderedSha256':hashes.hexdigest(),'videoSha256':digest(self.work/'take/picture.mp4')}
        probe = self.command('ffprobe',[shutil.which('ffprobe'),'-v','error','-count_frames','-select_streams','v:0','-show_entries','stream=width,height,nb_read_frames','-of','json',self.work/'take/picture.mp4'])
        stream = json.loads(probe.read_text())['streams'][0]
        assert stream == {'width':960,'height':540,'nb_read_frames':'48'},stream
        self.summary['take']['probe'] = stream
        self.save()
    def expectations(self, release):
        sample = release/'samples/godot/stardeck'
        timeline = release/'samples/timelines/stardeck/v2.json'
        sidecar = release/'samples/timelines/stardeck/v2.godot.expect.json'
        before = digest(sidecar)
        with serve(sample, self.args.godot, self.work/'sample-bridge', self.gd) as bridge:
            self.rows.append({'phase':'sample-serve', 'command':[str(self.gd),'godot','serve','--project',str(sample),'--stage','res://stage.tscn','--godot',str(self.args.godot),'--port','0'],'exit':None,'endpoint':bridge.endpoint})
            self.save()
            result = self.command('expect-check', [self.gd,'expect-check',timeline,'--expect',sidecar,'--endpoint',bridge.endpoint,'--work',self.work/'expect-work'], timeout=180)
            checked = json.loads(result.read_text())
            assert checked['passed'], checked
            self.summary['expect'] = {'check':checked, 'sizes':[{key:size[key] for key in ('width','height')} | {'rows':len(size['rows'])} for size in json.loads(sidecar.read_text())['sizes']], 'sidecarSha256':before}
        next(row for row in self.rows if row['phase']=='sample-serve')['exit'] = 0
        assert digest(sidecar)==before, 'baseline changed'
        self.save()

def digest(file):
    result = hashlib.sha256()
    with Path(file).open('rb') as source:
        for block in iter(lambda:source.read(1024*1024),b''): result.update(block)
    return result.hexdigest()

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive', required=True, type=Path)
    parser.add_argument('--addon', required=True, type=Path)
    parser.add_argument('--godot', required=True, type=Path)
    parser.add_argument('--work', required=True, type=Path)
    parser.add_argument('--expect', action='store_true')
    args = parser.parse_args()
    assert not os.environ.get('GAMEDIRECTOR_FFPROBE'), 'use PATH ffprobe, never GAMEDIRECTOR_FFPROBE'
    # Native artifact entrypoints discover only their unpacked distribution, not a developer override.
    for name in ('GAMEDIRECTOR_DISTRIBUTION_ROOT','GAMEDIRECTOR_NODE_MODULES','GAMEDIRECTOR_GODOT','DOTNET_ROOT','DOTNET_ROOT_ARM64'):
        os.environ.pop(name, None)
    os.environ['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    install = Install(args)
    release, project = install.unpack()
    install.film(project)
    if args.expect: install.expectations(release)
    install.summary['complete'] = True
    install.save()
    print(json.dumps({key:value for key,value in install.summary.items() if key not in ('commands','review')}, indent=2))
