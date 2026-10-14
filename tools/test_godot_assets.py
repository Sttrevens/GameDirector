#!/usr/bin/env python3
"""Prevent Windows link placeholders and divergent engine copies of licensed assets."""
import hashlib, tempfile, unittest
import stage_godot
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
ASSETS = {
    'RobotExpressive.glb': '047f5e5fb3bb6d378bd1df16ca6137f2a596c99b3a1b5690b4020c05aaf6f319',
    'space_ship_hallway.glb': '051c58dea01930b04d9d23d15f6a9a6a0628358d17954feb47dfa06e075f3bfe',
}

def portable_files(folder):
    files = {p.name for p in folder.iterdir() if p.is_file()}
    return files - stage_godot.IGNORE(folder, files)

class AssetTests(unittest.TestCase):
    def test_cache_files_do_not_count_as_shipped_addon_sources(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ('plugin.gd', '.DS_Store', 'plugin.gd.import'):
                (root / name).write_text('fixture')
            self.assertEqual({'plugin.gd'}, portable_files(root))

    def test_staged_stardeck_manifest_is_byte_identical(self):
        with tempfile.TemporaryDirectory(prefix='gd-stardeck-stage-') as folder:
            out = Path(folder)
            stage_godot.stage(REPO, out)
            self.assertEqual((REPO / 'adapters/stardeck/manifest.json').read_bytes(),
                             (out / 'adapters/stardeck/manifest.json').read_bytes())

    def test_each_engine_has_real_original_asset_bytes(self):
        for name, sha in ASSETS.items():
            for folder in ('adapters/stardeck/assets', 'samples/godot/stardeck/assets'):
                with self.subTest(name=name, folder=folder):
                    path = REPO / folder / name
                    self.assertFalse(path.is_symlink() or path.parent.is_symlink(), 'Windows checkout requires real files')
                    self.assertEqual(sha, hashlib.sha256(path.read_bytes()).hexdigest())

    def test_sample_addon_is_portable_and_matches_distributable_sources(self):
        source = REPO / 'packages/gamedirector-godot/addons/gamedirector'
        sample = REPO / 'samples/godot/stardeck/addons/gamedirector'
        self.assertFalse(sample.is_symlink())
        sources = portable_files(source)
        copies = portable_files(sample)
        self.assertEqual(sources, copies, 'addon copies must include the same UID and data files')
        for name in sources:
            self.assertEqual((source / name).read_bytes(), (sample / name).read_bytes(), name)
        for script in source.glob('*.gd'):
            self.assertTrue(script.with_suffix('.gd.uid').is_file(), f'missing stable UID: {script.name}')

if __name__ == '__main__': unittest.main()
