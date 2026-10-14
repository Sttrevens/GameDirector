#!/usr/bin/env python3
"""Publish must resolve offline packages from the caller's NuGet configuration."""
import json
import os
import tempfile
import subprocess
import sys
import unittest
from pathlib import Path
from unittest.mock import patch
import gamedirector_dist as dist


class NugetPackagingTests(unittest.TestCase):
    def run_publish(self, use_environment):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t).resolve(); tools=root/'tools'; tools.mkdir(); cache=root/'custom-packages'; cache.mkdir()
            legal=cache/'test.runtime.runtime.osx-arm64/1'; legal.mkdir(parents=True)
            (legal/'LICENSE.txt').write_text('license'); (legal/'THIRD-PARTY-NOTICES.txt').write_text('notices')
            app=root/'src/app'; (app/'obj').mkdir(parents=True)
            (app/'obj/project.assets.json').write_text(json.dumps({'packageFolders':{str(cache):{}},'project':{'frameworks':{}}}))
            fake=tools/'dotnet_fixture.py'; fake.write_text(
                'import sys,json\nfrom pathlib import Path\na=sys.argv[1:]\n'+
                f'cache={str(cache)!r}\n'+
                'if a[:2]==["nuget","locals"]: print("global-packages: "+cache)\n'+
                'elif a[0]=="publish":\n out=Path(a[a.index("-o")+1]); (out/"command.json").write_text(json.dumps(a)); (out/"app.runtimeconfig.json").write_text(json.dumps({"runtimeOptions":{"includedFrameworks":[{"name":"Test.Runtime","version":"1"}]}}))\n'+
                'else: sys.exit(3)\n')
            env=dict(os.environ); env.pop('NUGET_PACKAGES',None)
            if use_environment: env['NUGET_PACKAGES']=str(cache)
            # Substitute only the external dotnet executable, not the packaging
            # methods: launch a real Python child on Windows as well as Unix.
            real_run, real_output = subprocess.run, subprocess.check_output
            def command(args):
                return [sys.executable, str(fake), *args[1:]] if args[0]=='dotnet' else args
            with patch.dict(os.environ,env,clear=True), \
                 patch.object(dist.subprocess,'run',side_effect=lambda args, **kw: real_run(command(args), **kw)), \
                 patch.object(dist.subprocess,'check_output',side_effect=lambda args, **kw: real_output(command(args), **kw)):
                out=dist.publish_app(root,{'apps':{'cli':{'project':'src/app'}}},'cli','osx-arm64',root/'out')
            command=json.loads((out/'command.json').read_text())
            self.assertEqual(str(cache), command[command.index('--source')+1])
            self.assertTrue((out/'licenses/Test.Runtime/LICENSE.txt').is_file())

    def test_explicit_nuget_packages_is_honored(self): self.run_publish(True)
    def test_configured_global_packages_is_honored(self): self.run_publish(False)


if __name__=='__main__': unittest.main()
