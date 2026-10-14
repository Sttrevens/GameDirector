#!/usr/bin/env python3
"""Guard every shipped product-version declaration against the build stamp."""
import configparser
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[1]


SEMVER = r'(\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.[\w.]+)?)'


def release_headings(text):
    return re.findall(r'^## (?:Shipped in )?' + SEMVER + r'(?:\s|$)', text, re.M)


def document_versions(text):
    prefixes = (r'(?:com\.gamedirector\.unity|gamedirector(?:-godot)?)-',
                r'/badge/release-', r'/releases/download/v')
    # Past releases stay in history; only the first version heading declares the current release.
    return [version for prefix in prefixes for version in re.findall(prefix + SEMVER, text, re.M)] + release_headings(text)[:1]


class ProductVersions(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.version = ET.parse(REPO / 'Directory.Build.props').findtext('.//Version')

    def test_engine_packages_and_addons_share_build_version(self):
        paths = ('packages/com.gamedirector.unity/package.json',
                 'packages/gamedirector-three/package.json')
        for path in paths:
            with self.subTest(path=path):
                package = json.loads((REPO / path).read_text())
                self.assertEqual(self.version, package['version'])
                for name, version in package.get('dependencies', {}).items():
                    if name.startswith(('com.gamedirector.', '@gamedirector/')):
                        self.assertEqual(self.version, version, name)
        lock = json.loads((REPO / 'packages/gamedirector-three/package-lock.json').read_text())
        self.assertEqual(self.version, lock['version'])
        self.assertEqual(self.version, lock['packages']['']['version'])
        for folder in ('packages/gamedirector-godot', 'samples/godot/stardeck'):
            with self.subTest(folder=folder):
                cfg = configparser.ConfigParser()
                cfg.read(REPO / folder / 'addons/gamedirector/plugin.cfg')
                self.assertEqual(self.version, cfg['plugin']['version'].strip('"'))

    def test_project_reference_lockfiles_share_build_version(self):
        paths = subprocess.check_output(['git', 'ls-files', '*packages.lock.json'],
                                        cwd=REPO, text=True).splitlines()
        checked = 0
        for path in paths:
            lock = json.loads((REPO / path).read_text())
            for packages in lock['dependencies'].values():
                for package in packages.values():
                    if package['type'].lower() != 'project':
                        continue
                    for name, version in package.get('dependencies', {}).items():
                        if name.startswith('GameDirector.'):
                            with self.subTest(path=path, dependency=name):
                                self.assertEqual(f'[{self.version}, )', version)
                            checked += 1
        self.assertGreater(checked, 0)

    def test_public_release_declarations_share_build_version(self):
        # Catches a stale badge/download filename/changelog while package stamps advance.
        for path in ('README.md', 'docs/README.zh-CN.md', 'CHANGELOG.md', 'docs/roadmap.md'):
            with self.subTest(path=path):
                text = (REPO / path).read_text(encoding='utf-8')
                if path in ('CHANGELOG.md', 'docs/roadmap.md'):
                    headings = release_headings(text)
                    self.assertIn(self.version, headings, f'current release heading missing in {path}')
                    self.assertEqual(self.version, headings[0], f'current release is not newest in {path}')
                else:
                    versions = document_versions(text)
                    self.assertTrue(versions, f'no release declarations found in {path}')
                    self.assertEqual({self.version}, set(versions))

    def test_historical_versions_do_not_override_the_newest_release(self):
        text = '# Changelog\n\n## 0.1.1\nNew fixes.\n\n## 0.1.0\nFirst release.\n'
        self.assertEqual(['0.1.1'], document_versions(text))
        roadmap = '# Roadmap\n\n## Shipped in 0.1.1\nNew fixes.\n\n## Shipped in 0.1.0\nFirst release.\n'
        self.assertEqual(['0.1.1'], document_versions(roadmap))

    def test_historical_download_links_do_not_make_the_next_release_fail(self):
        # Catches applying the active README badge/download rule to historical release bodies.
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'docs').mkdir()
            for path in ('README.md', 'docs/README.zh-CN.md'):
                (root / path).write_text('![release](https://example.org/badge/release-0.1.1-blue)\n')
            for path, prefix in (('CHANGELOG.md', '## '), ('docs/roadmap.md', '## Shipped in ')):
                (root / path).write_text(prefix + '0.1.1\nNew release.\n' + prefix + '0.1.0\n'
                                        '[Old archive](https://example.org/releases/download/v0.1.0/gamedirector-0.1.0-osx-arm64.tar.gz)\n')
            case = ProductVersions('test_public_release_declarations_share_build_version')
            case.version = '0.1.1'
            result = unittest.TestResult()
            with patch(__name__ + '.REPO', root):
                case.run(result)
            self.assertTrue(result.wasSuccessful(), result.failures + result.errors)

    def test_bug_report_example_uses_build_version(self):
        text = (REPO / '.github/ISSUE_TEMPLATE/bug_report.md').read_text()
        example = re.search(r'GameDirector version / distribution \(e\.g\. ([^,]+),', text)
        self.assertIsNotNone(example)
        self.assertEqual(self.version, example[1])


if __name__ == '__main__':
    unittest.main()
