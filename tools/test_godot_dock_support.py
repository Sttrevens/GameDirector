"""Hermetic portability regression for the dock runner (no installed Godot required)."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

import test_godot_dock as dock


class DockRunnerTests(unittest.TestCase):
    @unittest.skipIf(os.name == 'nt', 'POSIX executable-script fixture; Windows uses a real --godot .exe')
    def test_plain_binary_cli_runs_in_an_isolated_engine_directory(self):
        # Catches next(... .app) crashing on normal Linux/Windows-style binary layouts.
        with tempfile.TemporaryDirectory(prefix='gd-dock-support-') as folder:
            root = Path(folder)
            engine = root / 'godot'
            engine.write_text('#!' + sys.executable + '''
import os, sys, time
mode = os.getenv('GAMEDIRECTOR_DOCK_TEST_MODE', '')
if mode == 'crash':
    print('DOCK_CRASH_READY:fixture', flush=True)
    time.sleep(60)
else:
    print('DOCK_PASS:fixture DOCK_LIFECYCLE_PASS:fixture DOCK_EDITOR_PASS:fixture DOCK_RECOVERY_PASS:fixture DOCK_OWNER_PASS:fixture DOCK_SECURITY_PASS:fixture DOCK_STATE_PASS:fixture DOCK_STATE_EDITOR_PASS:fixture', flush=True)
''')
            engine.chmod(0o755)
            try:
                dock.run(engine, root / 'work', engine)
            except Exception as error:
                self.fail(f'plain --godot binary must not require a macOS .app: {type(error).__name__}: {error}')
            rows = json.loads((root / 'work/results.json').read_text())
            self.assertTrue(rows)
            self.assertEqual(1, sum(row['phase'] == 'crash' for row in rows))
            self.assertFalse((root / '._sc_').exists(), 'runner touched the source engine settings')
            self.assertTrue((root / 'work/engine/._sc_').is_file())
            self.assertTrue((root / 'work/engine/godot').is_file())


if __name__ == '__main__':
    unittest.main()
