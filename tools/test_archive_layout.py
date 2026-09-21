"""Distribution archives must contain each payload once and retain launch modes."""
import tempfile
import tarfile
import unittest
from pathlib import Path

import gamedirector_dist as dist


class ArchiveLayoutTests(unittest.TestCase):
    def test_unix_archive_has_no_duplicate_members_and_keeps_entrypoints_executable(self):
        config = dist.load_config(dist.REPO)
        with tempfile.TemporaryDirectory(prefix='gd-archive-test-') as temporary:
            root = Path(temporary)
            payload = root / 'payload'
            (payload / 'workbench/Fonts').mkdir(parents=True)
            (payload / 'workbench/Fonts/test.txt').write_text('font fixture')
            (payload / 'workbench/gamedirector-workbench').write_text('native fixture')
            (payload / 'Start GameDirector.command').write_text('#!/bin/sh\nexit 0\n')
            (payload / 'Start GameDirector.command').chmod(0o755)
            archive = root / 'fixture.tar.gz'
            dist.make_archive(payload, archive, config, 'osx-arm64')
            with tarfile.open(archive) as opened:
                members = opened.getmembers()
                names = [member.name for member in members]
                self.assertEqual(len(names), len(set(names)), 'recursive tar traversal duplicated payload')
                self.assertEqual(0o755, opened.getmember('workbench/gamedirector-workbench').mode & 0o777)
                self.assertEqual(0o755, opened.getmember('Start GameDirector.command').mode & 0o777)
                self.assertEqual(0o644, opened.getmember('workbench/Fonts/test.txt').mode & 0o777)


if __name__ == '__main__':
    unittest.main()
