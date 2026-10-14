#!/usr/bin/env python3
"""Executable declarations and archive modes must not depend on the build host."""
import json
import os
import tarfile
import tempfile
import unittest
from pathlib import Path
import gamedirector_dist as dist


class RuntimePackagingTests(unittest.TestCase):
    def fixture(self, root):
        config = dist.load_config(dist.REPO)
        folder = root / 'bin'; folder.mkdir()
        for name in ('gd', 'gamedirector-mcp', 'gamedirector-workbench', 'createdump', 'library.dylib'):
            (folder / name).write_text(name)
            (folder / name).chmod(0o755) # accidental X_OK on non-program library
        (root / 'Start GameDirector.command').write_text('launcher')
        nested = folder / 'Fonts'; nested.mkdir(); (nested / 'gd').write_text('not an entrypoint')
        (nested / 'gd').chmod(0o755)
        return config, folder

    def test_manifest_executables_are_only_present_declared_unix_executables(self):
        with tempfile.TemporaryDirectory() as t:
            config, folder = self.fixture(Path(t))
            for rid, expected in [('osx-arm64', ['createdump','gamedirector-mcp','gamedirector-workbench','gd']), ('win-x64', [])]:
                with self.subTest(rid=rid):
                    manifest = dist.write_runtime_manifest(folder,config,rid,'0.1.0')
                    self.assertEqual(expected, manifest['executables'])

    def test_unity_tgz_has_exact_declared_modes_not_basename_matches(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t); package=root/'package'; runtime=package/'Tools~'; runtime.mkdir(parents=True)
            config, folder=self.fixture(runtime)
            archive=dist.make_unity_archive(package,root/'fixture.tgz',config,'osx-arm64')
            with tarfile.open(archive) as tar:
                modes={m.name.removeprefix('package/'):m.mode for m in tar.getmembers() if m.isfile()}
            self.assertEqual(0o755,modes['Tools~/bin/gd'])
            self.assertEqual(0o644,modes['Tools~/bin/library.dylib'])
            self.assertEqual(0o644,modes['Tools~/bin/Fonts/gd'])
            self.assertEqual(0o644,modes['Tools~/Start GameDirector.command'])

    def test_archive_has_exact_declared_modes_despite_accidental_host_execute_bits(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t); source=root/'distribution'; source.mkdir(); config, folder=self.fixture(source)
            archive=dist.make_archive(source,root/'fixture.tar.gz',config,'osx-arm64')
            with tarfile.open(archive) as tar:
                modes={m.name.removeprefix('fixture/'):m.mode for m in tar.getmembers() if m.isfile()}
            self.assertEqual({'bin/gd':0o755,'bin/gamedirector-mcp':0o755,'bin/gamedirector-workbench':0o755,'bin/createdump':0o755,'bin/library.dylib':0o644,'bin/Fonts/gd':0o644,'Start GameDirector.command':0o755},modes)


if __name__=='__main__': unittest.main()
