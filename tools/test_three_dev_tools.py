"""Dev helpers: catch hash/count drift and lock/installed-Three skew, without npm or fixture repos."""
import importlib.util,json,os,subprocess,sys,tempfile,unittest
from pathlib import Path
sys.dont_write_bytecode=True
here=Path(__file__).resolve().parent

class BaselineTests(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory(prefix='gd-baselines-test-',dir='/tmp');self.addCleanup(self.temp.cleanup)
  self.root=Path(self.temp.name);self.take=self.root/'take';self.take.mkdir()
  self.base=self.root/'.gamedirector-dev/baselines/sandring';self.base.mkdir(parents=True)
  self.rows=[dict(frameIndex=i,sha256=('a' if i<60 else 'b')*64) for i in range(62)]
  self.write(self.base/'frames.jsonl',self.rows);self.write(self.base/'from60.jsonl',self.rows[60:])
 def write(self,path,rows):path.write_text(''.join(json.dumps(r)+'\n' for r in rows))
 def run_check(self,*args):
  self.assertTrue((here/'baselines.py').is_file(),'baselines helper missing')
  return subprocess.run([sys.executable,str(here/'baselines.py'),'check-sandring',str(self.take),*args],env={**os.environ,'HOME':str(self.root),'PYTHONDONTWRITEBYTECODE':'1','GAMEDIRECTOR_BROWSER':'none'},capture_output=True,text=True)
 def test_identical_hashes_pass_despite_extra_receipt_fields(self):
  self.write(self.take/'frames.jsonl',[dict(r,extra='ignored') for r in self.rows]);r=self.run_check();self.assertEqual(r.returncode,0,r.stderr)
 def test_from60_compares_tail_with_capture_relative_indices(self):
  self.write(self.take/'frames.jsonl',[dict(r,frameIndex=i) for i,r in enumerate(self.rows[60:])]);r=self.run_check('--from','60');self.assertEqual(r.returncode,0,r.stderr)
 def test_changed_hash_refuses(self):
  rows=[dict(r) for r in self.rows];rows[3]['sha256']='c'*64;self.write(self.take/'frames.jsonl',rows)
  r=self.run_check();self.assertEqual(r.returncode,1);self.assertIn('mismatch',r.stderr.lower())
 def test_missing_or_extra_frame_refuses(self):
  for rows in [self.rows[:-1],self.rows+[dict(frameIndex=62,sha256='b'*64)]]:
   with self.subTest(count=len(rows)):
    self.write(self.take/'frames.jsonl',rows);r=self.run_check();self.assertEqual(r.returncode,1);self.assertIn('count',r.stderr.lower())
 def test_reordered_hashes_refuse(self):
  rows=list(self.rows);rows[0],rows[60]=rows[60],rows[0];self.write(self.take/'frames.jsonl',rows)
  r=self.run_check();self.assertEqual(r.returncode,1);self.assertIn('mismatch',r.stderr.lower())
 def test_malformed_or_missing_receipt_refuses(self):
  (self.take/'frames.jsonl').write_text('{broken\n');r=self.run_check();self.assertEqual(r.returncode,1)
  (self.take/'frames.jsonl').unlink();r=self.run_check();self.assertEqual(r.returncode,1)

class ThreeLockTests(unittest.TestCase):
 def setUp(self):
  path=here/'provision_three_game.py';self.assertTrue(path.is_file(),'snapshot helper missing')
  spec=importlib.util.spec_from_file_location('provision',path);self.mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.mod)
  self.temp=tempfile.TemporaryDirectory(prefix='gd-three-lock-test-',dir='/tmp');self.addCleanup(self.temp.cleanup)
  self.root=Path(self.temp.name);(self.root/'node_modules/three').mkdir(parents=True)
  (self.root/'node_modules/three/package.json').write_text(json.dumps(dict(name='three',version='0.186.1')))
  self.lock={'lockfileVersion':3,'packages':{'node_modules/three':{'version':'0.186.1'}}};self.write_lock()
 def write_lock(self):(self.root/'package-lock.json').write_text(json.dumps(self.lock))
 def test_locked_three_matches_installed_without_writes(self):
  before={str(p):(p.read_bytes(),p.stat().st_mtime_ns) for p in self.root.rglob('*') if p.is_file()}
  self.assertEqual(self.mod.check_three(self.root),'0.186.1')
  self.assertEqual(before,{str(p):(p.read_bytes(),p.stat().st_mtime_ns) for p in self.root.rglob('*') if p.is_file()})
 def test_planted_lock_version_refuses(self):
  self.lock['packages']['node_modules/three']['version']='0.185.1';self.write_lock()
  with self.assertRaisesRegex(ValueError,'three.*0.185.1.*0.186.1'):self.mod.check_three(self.root)
 def test_missing_lock_entry_refuses(self):
  del self.lock['packages']['node_modules/three'];self.write_lock()
  with self.assertRaisesRegex(ValueError,'lock.*three'):self.mod.check_three(self.root)
 def test_missing_installed_three_refuses(self):
  (self.root/'node_modules/three/package.json').unlink()
  with self.assertRaises((ValueError,OSError)):self.mod.check_three(self.root)
 def test_unsafe_names_and_archive_paths_refuse_before_writes(self):
  for name,paths in [('../escape',['src']),('game',['../src']),('game',['/src']),('game',[':(glob)*']),('game',['node_modules']),('game',['.gamedirector-provenance.json']),('game',['.git'])]:
   with self.subTest(name=name,paths=paths):
    with self.assertRaises(ValueError):self.mod.validate_inputs(name,paths)
 def test_archive_paths_canonicalize_for_idempotence(self):
  self.assertEqual(self.mod.validate_inputs('game',['src','package-lock.json','src']),['package-lock.json','src'])
 def test_publication_moves_before_freezing_root_and_includes_node_modules(self):
  self.assertTrue(hasattr(self.mod,'publish_snapshot'),'snapshot publication helper missing')
  stage=self.root/'scratch/snapshot';(stage/'node_modules/three').mkdir(parents=True)
  (stage/'node_modules/three/package.json').write_bytes(b'{"version":"0.186.1"}\n');(stage/'.gamedirector-provenance.json').write_bytes(b'{}\n')
  target=self.root/'home/games/game/sha'
  try:
   self.mod.publish_snapshot(stage,target);self.assertFalse(stage.exists())
   self.assertEqual((target/'node_modules/three/package.json').read_bytes(),b'{"version":"0.186.1"}\n')
   self.assertEqual((target/'.gamedirector-provenance.json').read_bytes(),b'{}\n')
   for path in [target,*target.rglob('*')]:self.assertEqual(path.stat().st_mode&0o222,0,str(path))
  finally:
   if target.exists():
    for path in [target,*target.rglob('*')]:path.chmod(path.stat().st_mode|0o700)

if __name__=='__main__':unittest.main()
