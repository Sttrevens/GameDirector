import importlib.util,json,tempfile,hashlib
from pathlib import Path
spec=importlib.util.spec_from_file_location('installer',Path(__file__).with_name('install_unity_package.py'));mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)
with tempfile.TemporaryDirectory(prefix='gd-install-test-') as folder:
 root=Path(folder);project=root/'game';(project/'Assets').mkdir(parents=True);(project/'ProjectSettings').mkdir()
 source=root/'package';source.mkdir();(source/'package.json').write_text(json.dumps({'name':'com.gamedirector.test','version':'1'}));(source/'owned.cs').write_text('v1')
 assert mod.install(source,project)['writes']==3 and not (project/'Packages').exists()
 first=mod.install(source,project,True);target=Path(first['destination'])
 before={str(p): (p.read_bytes(),p.stat().st_mtime_ns) for p in target.rglob('*') if p.is_file()}
 assert mod.install(source,project,True)['writes']==0
 assert before=={str(p):(p.read_bytes(),p.stat().st_mtime_ns) for p in target.rglob('*') if p.is_file()}
 (target/'owned.cs').write_text('user edit');(source/'owned.cs').write_text('v2')
 try:mod.install(source,project,True);raise AssertionError('overwrote user')
 except ValueError:pass
 assert (target/'owned.cs').read_text()=='user edit'
 (target/'owned.cs').write_text('v1');assert mod.install(source,project,True)['writes']==2
 assert (target/'owned.cs').read_text()=='v2'
 (source/'owned.cs').unlink();assert mod.install(source,project,True)['removedOwnedFiles']==['owned.cs']
 print(json.dumps({'passed':True,'checks':['dry plan no writes','repeat install zero byte and mtime changes','preserve user edit','owned upgrade','owned removal']}))
