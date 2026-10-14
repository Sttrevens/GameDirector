"""Source lint for English-only agent, terminal and installer surfaces. No engines."""
import re
import subprocess
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
CJK = re.compile(r"[\u2e80-\u9fff\uf900-\ufaff\uff00-\uffef]")


class EnglishSurfaceTests(unittest.TestCase):
    def test_agent_terminal_adapter_and_installer_sources_have_no_cjk(self):
        prefixes = ("src/GameDirector.Cli/", "src/GameDirector.Mcp/", "src/GameDirector.Client/",
                    "src/GameDirector.Godot/", "packages/gamedirector-godot/", "adapters/sandring/")
        explicit = {"tools/package_unity_candidate.py", "tools/package_release.py", "tools/install_unity_package.py"}
        files = subprocess.check_output(["git", "ls-files"], cwd=REPO, text=True).splitlines()
        for name in files:
            if name not in explicit and not name.startswith(prefixes):
                continue
            try:
                text = (REPO / name).read_text(encoding="utf-8")
            except UnicodeError:
                continue
            with self.subTest(file=name):
                self.assertIsNone(CJK.search(text), name)


if __name__ == "__main__":
    unittest.main()
