# Synthetic Stardeck cue contract coverage

This is a **strict-loader and compiler fixture**, not an engine adapter or a playable Stardeck performance. It borrows public Bolt/Pip actor and location names. The real [Stardeck adapter manifest](../../../adapters/stardeck/manifest.json) declares `actor.spawn: unsupported` and no audio; this independent, clearly synthetic manifest declares a spawnable Pip and a symbolic beep instead. It does not claim that the real Three/Godot adapters implement either operation, and ships no audio or prefab.

The authored timeline covers `actor.spawn`, `audio.play`, `audio.stop`, and `world.timescale` (including the compiler-injected restore). The authored `fixture.networking` extension is deliberately uninterpreted by Core. `SampleAssetTests` loads both JSON files through the real strict authored loader and compiles against this manifest. The repository inventory includes them, so misspelled fields, undeclared names or loss of this coverage fail the release tests.

For a real take-based edit plan, see [Stardeck's edit sample](../../timelines/stardeck/README.md).
