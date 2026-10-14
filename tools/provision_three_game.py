"""Provision a local git-object snapshot under ~/.gamedirector-dev/games; never mutate the source repo."""
import argparse,hashlib,json,os,re,shutil,subprocess,sys,tempfile
from pathlib import Path
PROVENANCE='.gamedirector-provenance.json'

def validate_inputs(name,paths):
 if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]*',name):raise ValueError('name must be a simple directory name')
 for path in paths:
  parts=path.split('/')
  if any(not part or part in {'.','..','.git','node_modules',PROVENANCE} for part in parts) or re.search(r'[:*?\[\\\x00-\x1f]',path):
   raise ValueError(f'unsafe archive path: {path}')
 return sorted(set(paths))

def check_three(root):
 lock=json.loads((root/'package-lock.json').read_bytes())
 version=lock.get('packages',{}).get('node_modules/three',{}).get('version')
 if not isinstance(version,str) or not version:raise ValueError('package-lock.json must resolve node_modules/three to an exact version')
 installed=json.loads((root/'node_modules/three/package.json').read_bytes())['version']
 if version!=installed:raise ValueError(f'three version mismatch: lock {version}, installed {installed}')
 return installed

def sha256(path):return hashlib.sha256(path.read_bytes()).hexdigest()

def tree_paths(root):return [root,*root.rglob('*')]

def check_links(root):
 for path in tree_paths(root):
  if path.is_symlink() and not path.resolve(strict=True).is_relative_to(root.resolve()):raise ValueError(f'snapshot link escapes root: {path}')

def readonly(root):
 check_links(root)
 for path in tree_paths(root):
  if not path.is_symlink() and path.stat().st_mode&0o222:raise ValueError(f'snapshot is not read-only: {path}')

def publish_snapshot(stage,root):
 check_links(stage)
 # macOS needs the directory root writable when rename changes its parent.
 for path in tree_paths(stage)[1:]:
  if not path.is_symlink():path.chmod(path.stat().st_mode&~0o222)
 root.parent.mkdir(parents=True,exist_ok=True)
 if root.exists():raise ValueError(f'destination appeared during provisioning; refusing {root}')
 stage.rename(root)
 try:root.chmod(root.stat().st_mode&~0o222);readonly(root)
 except BaseException:
  for path in tree_paths(root):
   if not path.is_symlink():path.chmod(path.stat().st_mode|0o700)
  shutil.rmtree(root);raise

def provision(repo,sha,name,paths):
 paths=validate_inputs(name,paths)
 if not re.fullmatch(r'[0-9a-fA-F]{7,40}',sha):raise ValueError('sha must be a commit hash (not a ref or URL)')
 repo=repo.resolve();env={**os.environ,'GIT_OPTIONAL_LOCKS':'0','GAMEDIRECTOR_BROWSER':'none'}
 def git(*args):return subprocess.check_output(['git','-C',str(repo),*args],env=env)
 fullsha=git('rev-parse',sha+'^{commit}').decode().strip();tree=git('rev-parse',fullsha+'^{tree}').decode().strip()
 root=Path.home()/'.gamedirector-dev/games'/name/fullsha
 if any(path.is_symlink() for path in [root,*root.parents]):raise ValueError('snapshot destination must not contain symlinks')
 if root.is_relative_to(repo):raise ValueError('snapshot destination must be outside the source repo')
 lockbytes=git('show',fullsha+':package-lock.json');locksha=hashlib.sha256(lockbytes).hexdigest()
 identity=dict(sha=fullsha,tree=tree,paths=paths,lockSha256=locksha)
 if root.exists():
  previous=json.loads((root/PROVENANCE).read_bytes())
  for key,value in identity.items():
   if previous.get(key)!=value:raise ValueError(f'existing provenance conflicts: {key}; refusing {root}')
  if sha256(root/'package-lock.json')!=locksha:raise ValueError('existing snapshot lock sha256 conflicts with provenance')
  if check_three(root)!=previous.get('three'):raise ValueError('existing snapshot three conflicts with provenance')
  readonly(root);print(f'No-op: {root}');return root
 if not {'package.json','package-lock.json'}.issubset(paths):raise ValueError('--paths must include package.json and package-lock.json')
 entries=git('ls-tree','-r','-z',fullsha,'--',*paths).split(b'\0');files=[]
 for entry in filter(None,entries):
  metadata,path=entry.split(b'\t',1);mode,kind,_=metadata.split();path=path.decode()
  validate_inputs(name,[path])
  if mode not in [b'100644',b'100755'] or kind!=b'blob':raise ValueError(f'archive refuses symlink/submodule: {path}')
  files.append(path)
 for path in paths:
  if not any(file==path or file.startswith(path+'/') for file in files):raise ValueError(f'path absent at {fullsha}: {path}')
 lock=json.loads(lockbytes)
 if any(v.get('link') or str(v.get('resolved','')).startswith('file:') for v in lock.get('packages',{}).values()):raise ValueError('local/link dependencies are not supported in a read-only object snapshot')
 stage=Path(tempfile.mkdtemp(prefix='gd-three-game-',dir='/tmp'))
 try:
  with tempfile.TemporaryFile(dir='/tmp') as errors:
   with subprocess.Popen(['git','-C',str(repo),'archive',fullsha,'--',*paths],stdout=subprocess.PIPE,stderr=errors,env=env) as archive:
    try:tar=subprocess.run(['tar','-xf','-','-C',str(stage)],stdin=archive.stdout,capture_output=True)
    finally:archive.stdout.close()
    code=archive.wait()
   errors.seek(0)
   if code:raise ValueError('git archive failed: '+errors.read().decode(errors='replace'))
   if tar.returncode:raise ValueError('tar failed: '+tar.stderr.decode(errors='replace'))
  extracted=sorted(p.relative_to(stage).as_posix() for p in stage.rglob('*') if p.is_file() or p.is_symlink())
  if extracted!=sorted(files):raise ValueError('git archive file list differs from ls-tree (check export-ignore attributes)')
  if sha256(stage/'package-lock.json')!=locksha:raise ValueError('archived lock sha256 differs from git object')
  node=subprocess.check_output(['node','--version'],env=env,text=True).strip();npm=subprocess.check_output(['npm','--version'],env=env,text=True).strip()
  with tempfile.TemporaryDirectory(prefix='gd-three-npm-',dir='/tmp') as cache:
   result=subprocess.run(['npm','ci','--omit=dev','--ignore-scripts'],cwd=stage,env={**env,'npm_config_cache':cache,'npm_config_ignore_scripts':'true'},capture_output=True,text=True)
  print(result.stdout,end='');print(result.stderr,end='',file=sys.stderr);output=result.stdout+'\n'+result.stderr
  if result.returncode:raise ValueError(f'npm ci refused (exit {result.returncode}); no snapshot published')
  if re.search(r'^> |npm (?:error|ERR!)|\b(?:ENOTFOUND|ETIMEDOUT|ECONNRESET|EAI_AGAIN|ECONNREFUSED|ENETUNREACH|ERR_SOCKET_TIMEOUT)\b',output,re.M):raise ValueError('npm reported an install script or network error; no snapshot published')
  if sha256(stage/'package-lock.json')!=locksha:raise ValueError('npm ci rewrote package-lock.json; no snapshot published')
  three=check_three(stage);warnings=[line for line in output.splitlines() if re.search(r'npm warn EBADENGINE',line,re.I)]
  # repo is audit metadata only; source identity consumes sha/tree, never this absolute path.
  provenance={**identity,'repo':str(repo),'node':node,'npm':npm,'three':three,'enginesWarnings':warnings}
  (stage/PROVENANCE).write_text(json.dumps(provenance,indent=2,sort_keys=True)+'\n')
  publish_snapshot(stage,root)
  print(f'Provisioned: {root} (three {three})');return root
 finally:
  if stage.exists():
   for path in tree_paths(stage):
    if not path.is_symlink():path.chmod(path.stat().st_mode|0o700)
   shutil.rmtree(stage)

def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--repo',type=Path,required=True);p.add_argument('--sha',required=True)
 p.add_argument('--name',required=True);p.add_argument('--paths',nargs='+',required=True);a=p.parse_args()
 try:provision(a.repo,a.sha,a.name,a.paths)
 except (OSError,ValueError,KeyError,TypeError,subprocess.CalledProcessError) as e:p.exit(1,f'Provisioning refused: {e}\n')

if __name__=='__main__':main()
