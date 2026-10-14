# Synthetic TypeScript game

No real game code. The provenance is synthetic, in the format produced by
`tools/provision_three_game.py`; this test tree is intentionally editable.
`src/models.ts` exercises TypeScript, attribute-free JSON outside `src`, a
resolved-path device shim, `import.meta.env.DEV`, and jsm/addons module identity.
Tests copy both fixture roots to owned `/tmp` directories. Only the refusal
variant mounts installed THREE at `node_modules/three` for `src/second-three.ts`.
`stub-three` intentionally lacks the engine-used `PCFSoftShadowMap` export.
`src/case-three.ts` uses differently-cased package paths; tests create its alias
on case-sensitive hosts. `src/wrapped-three` has package name `three` but different
bytes. The vendored-copy test copies an actual build file to `src/lib` in its owned
/tmp root, rather than committing duplicate vendor code. Review tests also use
owned config ancestors/read-race edits; no real game's files are edited.
