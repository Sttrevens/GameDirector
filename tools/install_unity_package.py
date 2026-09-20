"""Explicit, repeatable package install/upgrade. Default is a read-only plan.
Never runs during filming; no game settings, existing assets or scenes are changed.
"""
import argparse, hashlib, json, os, shutil, tempfile
from pathlib import Path

def digest(data):return hashlib.sha256(data).hexdigest()
def install(source,project,apply=False):
 source=Path(source).resolve();project=Path(project).resolve()
 if not (project/'Assets').is_dir() or not (project/'ProjectSettings').is_dir():raise ValueError('Choose a Unity project root.')
 package=json.loads((source/'package.json').read_text());name=package['name']
 if not name.startswith('com.gamedirector.') or '/' in name or '\\' in name:raise ValueError('Unexpected package identity.')
 target=project/'Packages'/name
 if target.is_symlink() or (project/'Packages').is_symlink():raise ValueError('Package destination must not be a symlink.')
 receipt=target/'.gamedirector-install.json'
 if receipt.is_symlink():raise ValueError('Install receipt must not be a symlink.')
 old=json.loads(receipt.read_text())['files'] if receipt.exists() else {}
 desired={str(p.relative_to(source)):p.read_bytes() for p in source.rglob('*') if p.is_file() and p.name not in ('.DS_Store','.gamedirector-sync.json','.gamedirector-install.json')}
 changes=[]
 for relative,data in desired.items():
  file=target/relative
  if any(p.is_symlink() for p in [file,*file.parents] if p!=project.parent):raise ValueError('Symlink in package path: '+relative)
  if file.exists():
   actual=file.read_bytes()
   if actual==data:continue
   if relative not in old or digest(actual)!=old[relative]:raise ValueError('Preserved local package edit: '+relative)
  changes.append(relative)
 removed=[]
 for relative,expected in old.items():
  file=target/relative
  if file.resolve().is_relative_to(target.resolve()) is False:raise ValueError('Invalid install receipt path.')
  if relative not in desired and file.exists():
   if file.is_symlink() or digest(file.read_bytes())!=expected:raise ValueError('Preserved local package edit: '+relative)
   removed.append(relative)
 metadata={'package':name,'version':package['version'],'files':{k:digest(v) for k,v in sorted(desired.items())}}
 receipt_data=(json.dumps(metadata,indent=2)+'\n').encode()
 receipt_change=not receipt.exists() or receipt.read_bytes()!=receipt_data
 if apply:
  target.mkdir(parents=True,exist_ok=True)
  for relative in changes:
   file=target/relative;file.parent.mkdir(parents=True,exist_ok=True)
   fd,temp=tempfile.mkstemp(prefix='.gd-install-',dir=file.parent)
   try:
    with os.fdopen(fd,'wb') as f:f.write(desired[relative])
    os.replace(temp,file)
   finally:
    if os.path.exists(temp):os.unlink(temp)
  for relative in removed:(target/relative).unlink()
  if receipt_change:receipt.write_bytes(receipt_data)
 return {'package':name,'version':package['version'],'destination':str(target),'mode':'installed' if apply else 'plan','changedFiles':changes,'removedOwnedFiles':removed,'receiptChanged':receipt_change,'writes':len(changes)+len(removed)+int(receipt_change)}

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--source',required=True);p.add_argument('--project',required=True);p.add_argument('--apply',action='store_true');a=p.parse_args()
 print(json.dumps(install(a.source,a.project,a.apply),indent=2))
