"""Distribution staging contains executable addon sources and asset bytes, never Godot caches."""
import tempfile, unittest
from pathlib import Path
from stage_godot import stage

class StagingTests(unittest.TestCase):
    def test_addon_and_stardeck_are_staged_without_cache_or_dangling_links(self):
        with tempfile.TemporaryDirectory(prefix='gd-godot-stage-') as temp:
            root=Path(temp)/'repo'; out=Path(temp)/'dist'
            addon=root/'packages/gamedirector-godot/addons/gamedirector'; addon.mkdir(parents=True)
            (addon/'worker.gd').write_text('extends Node\n')
            cache=addon/'.godot'; cache.mkdir(); (cache/'secret.json').write_text('{}')
            adapter=root/'adapters/stardeck'; adapter.mkdir(parents=True)
            assets=adapter/'assets'; assets.mkdir(); (assets/'fixture.glb').write_bytes(b'approved fixture')
            (assets/'fixture.glb.import').write_text('cached import')
            stage(root,out)
            self.assertEqual('extends Node\n',(out/'godot/addons/gamedirector/worker.gd').read_text())
            self.assertEqual(b'approved fixture',(out/'adapters/stardeck/assets/fixture.glb').read_bytes())
            self.assertFalse((out/'adapters/stardeck/assets').is_symlink())
            self.assertFalse(any(p.name=='.godot' or p.suffix=='.import' for p in out.rglob('*')))

if __name__=='__main__': unittest.main()
