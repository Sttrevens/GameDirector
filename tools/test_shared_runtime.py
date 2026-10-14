#!/usr/bin/env python3
"""Real self-contained publishes must share byte-identical overlapping files."""
import argparse
import itertools
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
import gamedirector_dist as dist

RID = None

class SharedRuntimeTests(unittest.TestCase):
    def test_merge_preserves_shared_and_app_specific_bytes(self):
        with tempfile.TemporaryDirectory(prefix='gd-merge-') as temporary:
            root = Path(temporary)
            sources = []
            for app in ('cli', 'mcp', 'workbench'):
                folder = root / app
                folder.mkdir()
                (folder / 'shared.dll').write_bytes(b'identical runtime')
                (folder / app).write_bytes(app.encode())
                sources.append(folder)
            dist.merge_publishes(sources, root / 'bin')
            self.assertEqual({'shared.dll', 'cli', 'mcp', 'workbench'}, set(dist.hash_tree(root / 'bin')))
            self.assertEqual(b'identical runtime', (root / 'bin/shared.dll').read_bytes())
            self.assertEqual(b'mcp', (root / 'bin/mcp').read_bytes())

    def test_merge_refuses_differing_overlap_without_publishing_partial_output(self):
        with tempfile.TemporaryDirectory(prefix='gd-collision-') as temporary:
            root = Path(temporary)
            for app, data in (('cli', b'old'), ('mcp', b'new')):
                (root / app).mkdir()
                (root / app / 'shared.dll').write_bytes(data)
            with self.assertRaisesRegex(SystemExit, 'shared.dll'):
                dist.merge_publishes([root / 'cli', root / 'mcp'], root / 'bin')
            self.assertFalse((root / 'bin').exists(), 'collision must fail before copying')

    def test_three_actual_publishes_have_only_byte_identical_overlaps(self):
        config = dist.load_config(dist.REPO)
        rid = RID or __import__('godot_test_support').host_rid()
        locks = [dist.REPO / name for name in subprocess.check_output(
            ['git', 'ls-files', '*packages.lock.json'], cwd=dist.REPO, text=True).splitlines()]
        before = {path: path.read_bytes() for path in locks}
        with tempfile.TemporaryDirectory(prefix='gd-publish-overlap-') as temporary:
            root = Path(temporary)
            trees = {}
            for app in config['apps']:
                folder = dist.publish_app(dist.REPO, config, app, rid, root / app)
                trees[app] = dist.hash_tree(folder)
            self.assertEqual([], [str(path.relative_to(dist.REPO)) for path in locks
                                  if path.read_bytes() != before[path]],
                             'publishing must not rewrite committed dependency locks')
            overlaps = 0
            failures = []
            for left, right in itertools.combinations(trees, 2):
                common = trees[left].keys() & trees[right].keys()
                overlaps += len(common)
                failures += [f'{left}/{right}: {name}' for name in sorted(common)
                             if trees[left][name] != trees[right][name]]
            self.assertGreater(overlaps, 100, 'must compare complete self-contained publishes')
            self.assertEqual([], failures, 'non-identical shared files: ' + ', '.join(failures))
            dist.merge_publishes([root / app for app in trees], root / 'bin')
            union = set().union(*(set(tree) for tree in trees.values()))
            self.assertEqual(union, set(dist.hash_tree(root / 'bin')))
            print(json.dumps({'rid': rid, 'pairwiseOverlapComparisons': overlaps,
                              'unionFiles': len(union), 'collisions': len(failures)}))

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime')
    args, remaining = parser.parse_known_args()
    RID = args.runtime
    unittest.main(argv=[__file__, *remaining])
