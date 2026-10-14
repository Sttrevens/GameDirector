"""Temp ownership regressions: cleanup on success/failure, diagnosis only on failure."""
import contextlib, io, shutil, subprocess, sys, tempfile, unittest
import test_temp
from pathlib import Path
from test_temp import temp_directory

class TempTests(unittest.TestCase):
 def test_process_is_stopped_before_scratch_cleanup(self):
  stop=getattr(test_temp,'stop_process',None);self.assertTrue(callable(stop),'bounded child teardown is missing')
  with subprocess.Popen([sys.executable,'-c','import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); print("ready",flush=True); time.sleep(60)'],stdout=subprocess.PIPE,text=True) as child:
   try:
    self.assertEqual('ready',child.stdout.readline().strip());stop(child,timeout=.1);self.assertIsNotNone(child.poll())
   finally:
    if child.poll() is None:child.kill();child.wait()
 def test_success_cleans_nested_files(self):
  with temp_directory('gd-temp-probe-') as root:
   (root/'nested').mkdir();(root/'nested/data').write_text('owned')
  self.assertFalse(root.exists())
 def test_failure_cleans(self):
  with self.assertRaisesRegex(AssertionError,'planted failure'):
   with temp_directory('gd-temp-probe-') as root:
    (root/'data').write_text('owned');raise AssertionError('planted failure')
  self.assertFalse(root.exists())
 def test_keep_flag_still_cleans_success(self):
  with temp_directory('gd-temp-probe-',keep_on_failure=True) as root:(root/'data').write_text('owned')
  self.assertFalse(root.exists())
 def test_failure_keeps_and_prints_owned_path(self):
  output=io.StringIO();root=None
  try:
   with contextlib.redirect_stderr(output),self.assertRaisesRegex(AssertionError,'planted failure'):
    with temp_directory('gd-temp-probe-',keep_on_failure=True) as root:
     (root/'data').write_text('evidence');raise AssertionError('planted failure')
   self.assertEqual('evidence',(root/'data').read_text());self.assertIn(str(root),output.getvalue())
   self.assertIn('FAIL: retained test directory:',output.getvalue())
  finally:
   if root and root.exists():shutil.rmtree(root)
 def test_directory_links_do_not_delete_unowned_targets(self):
  with tempfile.TemporaryDirectory(prefix='gd-temp-target-') as target:
   path=Path(target);(path/'unowned').write_text('safe')
   with temp_directory('gd-temp-probe-') as root:(root/'link').symlink_to(path,target_is_directory=True)
   self.assertEqual('safe',(path/'unowned').read_text());self.assertFalse(root.exists())

if __name__=='__main__':unittest.main(verbosity=2)
