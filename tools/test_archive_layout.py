"""Distribution archives must contain each payload once and retain launch modes."""
import json
import tempfile
import tarfile
import unittest
import zipfile
import stage_godot
from pathlib import Path
from unittest.mock import patch

import gamedirector_dist as dist


class ArchiveLayoutTests(unittest.TestCase):
    def test_shipped_files_must_not_embed_the_local_checkout_path(self):
        with tempfile.TemporaryDirectory() as folder:
            out = Path(folder)
            (out / "bin").mkdir()
            (out / "bin/clean.dll").write_bytes(b"/_/GameDirector/src/x.cs")
            dist.assert_no_local_paths(out, dist.REPO)
            (out / "bin/leaky.pdb").write_bytes(b"\x00" + str(dist.REPO.resolve()).encode() + b"/src/x.cs")
            with self.assertRaises(SystemExit):
                dist.assert_no_local_paths(out, dist.REPO)

    def test_release_status_requires_a_clean_worktree(self):
        clean = {"sourceCommit": "c", "sourceWorktreeDirty": False}
        dirty = {"sourceCommit": "c", "sourceWorktreeDirty": True}
        self.assertEqual(dist.release_status(clean, False), dist.CANDIDATE_STATUS)
        self.assertEqual(dist.release_status(dirty, False), dist.CANDIDATE_STATUS)
        self.assertEqual(dist.release_status(clean, True), dist.RELEASE_STATUS)
        with self.assertRaises(SystemExit):
            dist.release_status(dirty, True)

    def legal_fixture(self):
        temporary = tempfile.TemporaryDirectory(prefix='gd-legal-test-')
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        config = dist.load_config(dist.REPO)
        (root / 'tools').mkdir()
        (root / 'tools/distribution.json').write_text(json.dumps(config))
        sdk = root / 'sdk-root'
        cache = root / 'nuget'
        for app in config['apps'].values():
            obj = root / app['project'] / 'obj'
            obj.mkdir(parents=True, exist_ok=True)
            (obj / 'project.assets.json').write_text(json.dumps({
                'packageFolders': {str(cache): {}},
                'project': {'frameworks': {'net8.0': {
                    'runtimeIdentifierGraphPath': str(sdk / 'sdk/9.9.9/PortableRuntimeIdentifierGraph.json')}}}}))
        pack = cache / 'microsoft.netcore.app.runtime.osx-arm64/9.9.9'
        pack.mkdir(parents=True)
        (pack / 'LICENSE.TXT').write_bytes(b'runtime license\n')
        (pack / 'THIRD-PARTY-NOTICES.TXT').write_bytes(b'runtime notices\n')
        for name in ('LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md'):
            (root / name).write_bytes(('release ' + name + '\n').encode())
        source = root / config['unity']['packageSource']
        source.mkdir(parents=True)
        (source / 'Plugins').mkdir()
        (source / 'Plugins/GameDirector.Core.dll').write_bytes(b'canonical Core fixture')
        dll = root / 'src/GameDirector.Core/bin/Release/netstandard2.1/GameDirector.Core.dll'
        dll.parent.mkdir(parents=True)
        dll.write_bytes(b'compiled fixture')
        media = root / config['media']['manifestSource']
        media.parent.mkdir(parents=True, exist_ok=True)
        media.write_text('{}')

        def publish_only_external_process(_root, args):
            if args[0] != 'publish':
                return
            target = Path(args[args.index('-o') + 1])
            (target / 'Fonts').mkdir()
            (target / 'Fonts/NotoSansCJKsc-Regular.otf').write_bytes(b'font fixture')
            (target / 'app.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {
                'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '9.9.9'}]}}))
        return root, config, sdk, pack, publish_only_external_process

    def test_unity_payload_carries_release_documents_verbatim(self):
        root, config, _, _, publish = self.legal_fixture()
        with patch.object(dist, 'dotnet', side_effect=publish):
            package = dist.stage_unity_package(root, config, 'osx-arm64', root / 'package', '9.9.9')
        for name in ('LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md'):
            self.assertTrue((package / name).is_file(), f'missing release document: {name}')
            self.assertEqual(('release ' + name + '\n').encode(), (package / name).read_bytes())

    def test_unity_payload_preserves_the_verified_canonical_core_dll(self):
        root, config, _, _, publish = self.legal_fixture()
        with patch.object(dist, 'dotnet', side_effect=publish):
            package = dist.stage_unity_package(root, config, 'osx-arm64', root / 'package', '9.9.9')
        self.assertEqual(b'canonical Core fixture', (package / 'Plugins/GameDirector.Core.dll').read_bytes())

    def test_publish_carries_exact_runtime_pack_legal_texts(self):
        root, config, _, _, publish = self.legal_fixture()
        with patch.object(dist, 'dotnet', side_effect=publish):
            target = dist.publish_app(root, config, 'workbench', 'osx-arm64', root / 'runtime')
        legal = target / 'licenses/Microsoft.NETCore.App'
        self.assertTrue((legal / 'LICENSE.txt').is_file(), 'missing runtime license')
        self.assertEqual(b'runtime license\n', (legal / 'LICENSE.txt').read_bytes())
        self.assertEqual(b'runtime notices\n', (legal / 'THIRD-PARTY-NOTICES.txt').read_bytes())

    def test_publish_uses_installed_sdk_legal_texts_when_pack_omits_them(self):
        root, config, sdk, pack, publish = self.legal_fixture()
        for path in pack.iterdir():
            path.unlink()
        (sdk.parent / 'LICENSE.TXT').write_bytes(b'installed SDK license\n')
        docs = sdk.parent / 'share/doc/installed-sdk'
        docs.mkdir(parents=True)
        (docs / 'ThirdPartyNotices.txt').write_bytes(b'installed SDK notices\n')
        with patch.object(dist, 'dotnet', side_effect=publish):
            target = dist.publish_app(root, config, 'workbench', 'osx-arm64', root / 'runtime')
        legal = target / 'licenses/Microsoft.NETCore.App'
        self.assertTrue((legal / 'THIRD-PARTY-NOTICES.txt').is_file(), 'missing installed SDK notices')
        self.assertEqual(b'installed SDK license\n', (legal / 'LICENSE.txt').read_bytes())
        self.assertEqual(b'installed SDK notices\n', (legal / 'THIRD-PARTY-NOTICES.txt').read_bytes())

    def test_publish_refuses_missing_runtime_notices(self):
        root, config, _, pack, publish = self.legal_fixture()
        (pack / 'THIRD-PARTY-NOTICES.TXT').unlink()
        with patch.object(dist, 'dotnet', side_effect=publish):
            with self.assertRaisesRegex(SystemExit, 'runtime legal text missing'):
                dist.publish_app(root, config, 'workbench', 'osx-arm64', root / 'runtime')

    def test_godot_asset_library_zip_and_full_archive_carry_the_addon_and_license(self):
        with tempfile.TemporaryDirectory(prefix='gd-addon-archive-') as temporary:
            root=Path(temporary)/'repo'; out=Path(temporary)/'payload'; addon=root/'packages/gamedirector-godot/addons/gamedirector'
            addon.mkdir(parents=True); (addon/'plugin.cfg').write_bytes(b'[plugin]\nname="GameDirector"\n')
            (addon/'worker.gd').write_bytes(b'extends Node\n'); (root/'LICENSE').write_bytes(b'test license\n')
            for cache in ('.godot/cache', '__pycache__/module.pyc', 'bin/debug', 'obj/build', '.DS_Store', 'mesh.glb.import'):
                path = addon / cache
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('private cache')
            out.mkdir(); archive=stage_godot.addon_archive(root,out.parent/'gamedirector-godot-1.0.zip')
            (root/'adapters/stardeck').mkdir(parents=True)
            stage_godot.stage(root, out)
            with zipfile.ZipFile(archive) as opened:
                self.assertEqual({'LICENSE','addons/gamedirector/plugin.cfg','addons/gamedirector/worker.gd'},set(opened.namelist()))
                self.assertEqual(b'test license\n',opened.read('LICENSE'))
            full=out.parent/'full.tar.gz'; dist.make_archive(out,full,dist.load_config(dist.REPO),'osx-arm64')
            with tarfile.open(full) as opened:
                self.assertNotIn('full/' + archive.name, opened.getnames())
                self.assertEqual(b'extends Node\n', opened.extractfile('full/godot/addons/gamedirector/worker.gd').read())
                addon_entries = {name for name in opened.getnames() if name.startswith('full/godot/') and opened.getmember(name).isfile()}
                self.assertEqual({'full/godot/addons/gamedirector/plugin.cfg', 'full/godot/addons/gamedirector/worker.gd'}, addon_entries)

    def test_unix_archive_has_no_duplicate_members_and_keeps_entrypoints_executable(self):
        config = dist.load_config(dist.REPO)
        with tempfile.TemporaryDirectory(prefix='gd-archive-test-') as temporary:
            root = Path(temporary)
            payload = root / 'payload'
            (payload / 'bin/Fonts').mkdir(parents=True)
            (payload / 'bin/Fonts/test.txt').write_text('font fixture')
            (payload / 'bin/gamedirector-workbench').write_text('native fixture')
            (payload / 'Start GameDirector.command').write_text('#!/bin/sh\nexit 0\n')
            (payload / 'Start GameDirector.command').chmod(0o755)
            archive = root / 'fixture.tar.gz'
            dist.make_archive(payload, archive, config, 'osx-arm64')
            with tarfile.open(archive) as opened:
                members = opened.getmembers()
                names = [member.name for member in members]
                self.assertEqual(len(names), len(set(names)), 'recursive tar traversal duplicated payload')
                self.assertEqual({'fixture'}, {name.split('/')[0] for name in names})
                self.assertEqual(0o755, opened.getmember('fixture/bin/gamedirector-workbench').mode & 0o777)
                self.assertEqual(0o755, opened.getmember('fixture/Start GameDirector.command').mode & 0o777)
                self.assertEqual(0o644, opened.getmember('fixture/bin/Fonts/test.txt').mode & 0o777)


if __name__ == '__main__':
    unittest.main()
