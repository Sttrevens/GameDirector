"""Owned test scratch: always delete, unless a diagnostic test actually fails."""
from contextlib import contextmanager
from pathlib import Path
import shutil, subprocess, sys, tempfile

@contextmanager
def temp_directory(prefix,keep_on_failure=False):
 root=Path(tempfile.mkdtemp(prefix=prefix));keep=False
 try:yield root
 except BaseException:
  keep=keep_on_failure
  if keep:print('FAIL: retained test directory: '+str(root),file=sys.stderr,flush=True)
  raise
 finally:
  if not keep:shutil.rmtree(root)

def stop_process(process,timeout=20):
 if process is None or process.poll() is not None:return
 process.terminate()
 try:process.wait(timeout=timeout)
 except subprocess.TimeoutExpired:process.kill();process.wait(timeout=timeout)
