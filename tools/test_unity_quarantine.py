#!/usr/bin/env python3
"""Real UPM tarball quarantine propagation and Copy MCP Config launch path (macOS)."""
import argparse
import json
import os
import shutil
import signal
import subprocess
from pathlib import Path


def attributes(path):
    if not path.exists(): return None
    query=subprocess.run(['/usr/bin/xattr','-p','com.apple.quarantine',str(path)],capture_output=True,text=True)
    return {'com.apple.quarantine':query.stdout.strip()} if query.returncode==0 else {}


def main():
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('--editor',required=True); p.add_argument('--tgz',required=True,type=Path); p.add_argument('--out',required=True,type=Path); p.add_argument('--expect-launch',action='store_true'); a=p.parse_args()
    out=a.out.resolve(); out.mkdir(parents=True,exist_ok=False); project=out/'project'
    (project/'Assets/Editor').mkdir(parents=True); (project/'ProjectSettings').mkdir(); (project/'Packages').mkdir()
    (project/'.gamedirector-validation-project').write_text('Owned quarantine fixture\n')
    (project/'Packages/manifest.json').write_text('{"dependencies":{"com.unity.nuget.newtonsoft-json":"3.2.1"}}')
    script=Path(__file__).parent/'unity-validation/QuarantineValidation.cs'; shutil.copy2(script,project/'Assets/Editor/QuarantineValidation.cs')
    archive=out/a.tgz.name; shutil.copy2(a.tgz,archive); subprocess.run(['/usr/bin/xattr','-w','com.apple.quarantine','0081;67000000;GameDirectorValidation;',str(archive)],check=True)
    env=dict(os.environ,GD_QUARANTINE_TGZ=str(archive),GD_QUARANTINE_EVIDENCE=str(out),GAMEDIRECTOR_HOME=str(out/'home'))
    def run(method):
        child=subprocess.Popen([a.editor,'-batchmode','-projectPath',str(project),'-executeMethod','QuarantineValidation.'+method,'-logFile',str(out/(method+'.log'))],env=env,start_new_session=True)
        try: return child.wait(timeout=600)
        finally:
            if child.poll() is None: os.killpg(child.pid,signal.SIGKILL); child.wait()
    result={'archive':str(archive),'archiveAttributes':attributes(archive)}
    try:
        result['addExit']=run('Add')
        assert (out/'package-path.txt').exists(), 'UPM add produced no resolved path; see Add.log'
        package=Path((out/'package-path.txt').read_text()); runtime=package/'Tools~/bin'; gd=runtime/'gd'
        result.update(resolvedPackage=str(package),resolvedGdBefore=attributes(gd),runtimeMirrorsBefore=list((out/'home/RuntimeMirror').glob('*/gd')))
        result['runtimeMirrorsBefore']=[str(path) for path in result['runtimeMirrorsBefore']]
        result['launchEditorExit']=run('Launch'); launch=json.loads((out/'launch.json').read_text())
        result.update(launch=launch,resolvedGdAfter=attributes(gd),mirrorGdAttributes={str(path):attributes(path) for path in (out/'home/RuntimeMirror').glob('*/gd')})
        cli=Path(launch['cli']) if launch.get('cli') else None
        result['launchedGdAttributes']=attributes(cli) if cli else None
        (out/'result.json').write_text(json.dumps(result,indent=2)+'\n'); print(json.dumps(result))
        if a.expect_launch: assert launch['passed'], 'Bundled gd did not launch; quarantine evidence retained'
    finally:
        # Compiler subprocesses only from this owned project.
        for row in subprocess.check_output(['ps','-axo','pid=,command='],text=True).splitlines():
            pid,_,cmd=row.strip().partition(' ')
            if str(project) in cmd and ('csc.exe' in cmd or 'VBCSCompiler' in cmd):
                try: os.kill(int(pid),signal.SIGTERM)
                except ProcessLookupError: pass


if __name__=='__main__': main()
