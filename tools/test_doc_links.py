#!/usr/bin/env python3
"""Check repository Markdown links; --self-test exercises parser regressions."""
import argparse
from pathlib import Path
import subprocess
import re
import tempfile
import unittest

from doc_links import anchors, check, visible


def english_prose_errors(root, files):
    """English docs may link to translations or contain CJK in fenced examples, not untranslated prose."""
    errors = []
    for source in files:
        if re.search(r'\.zh(?:-[\w-]+)?\.md$', source.name):
            continue
        text = visible(source.read_text(encoding='utf-8'))
        text = re.sub(r'\[[^\]]+\]\([^)]*\.zh(?:-[\w-]+)?\.md[^)]*\)', '', text)
        for line, content in enumerate(text.splitlines(), 1):
            if re.search(r'[\u4e00-\u9fff]', content):
                errors.append(f'{source.relative_to(root)}:{line}: untranslated prose in English documentation')
    return errors


class LinkTests(unittest.TestCase):
    def test_github_unicode_punctuation_and_duplicate_heading_ids(self):
        text = '# Hello, `world`!\n# Hello, world!\n# 中文 · API\nTitle\n=====\n'
        self.assertEqual({'hello-world', 'hello-world-1', '中文--api', 'title'}, anchors(text))

    def test_missing_target_and_anchor_are_reported(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'target.md').write_text('# Existing\n')
            source = root / 'README.md'
            source.write_text('[file](absent.md)\n[heading](target.md#missing)\n')
            errors, count = check(root, [source])
            self.assertEqual(2, count)
            self.assertEqual(2, len(errors))
            self.assertIn('absent.md', errors[0])
            self.assertIn('#missing', errors[1])

    def test_html_images_references_encoded_paths_and_local_anchors(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'a b.md').write_text('# 中 文\n<a id="custom"></a>\n')
            (root / 'hero.webp').write_bytes(b'image')
            source = root / 'README.md'
            source.write_text('# Here\n[one](a%20b.md#中-文)\n[two][r]\n'
                              '[r]: <a b.md#custom>\n<img src="hero.webp">\n[local](#here)\n')
            errors, count = check(root, [source])
            self.assertEqual([], errors)
            self.assertEqual(4, count)

    def test_english_docs_allow_translation_links_and_fenced_examples_not_untranslated_prose(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            translation = root / 'README.zh-CN.md'
            translation.write_text('# 中文说明\n', encoding='utf-8')
            source = root / 'README.md'
            source.write_text('[中文](README.zh-CN.md)\n```json\n{"text":"你好"}\n```\n中文正文\n', encoding='utf-8')
            self.assertEqual([f'README.md:5: untranslated prose in English documentation'],
                             english_prose_errors(root, [source, translation]))

    def test_fenced_code_inline_code_and_external_links_are_not_files(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / 'README.md'
            source.write_text('```md\n[example](missing.md)\n```\n'
                              '`[sample](absent.md)`\n[web](https://example.org/nope#x)\n')
            self.assertEqual(([], 0), check(Path(folder), [source]))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        unittest.main(argv=[__file__])
    else:
        root = Path(__file__).resolve().parents[1]
        names = subprocess.check_output(['git', 'ls-files', '--cached', '--others',
                                         '--exclude-standard', '*.md'], cwd=root, text=True).splitlines()
        files = [root / name for name in sorted(set(names))]
        errors, count = check(root, files)
        errors += english_prose_errors(root, files)
        for error in errors:
            print(error)
        print(f'{len(files)} Markdown files, {count} relative links, {len(errors)} errors')
        raise SystemExit(bool(errors))
