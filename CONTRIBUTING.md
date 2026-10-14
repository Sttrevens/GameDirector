# Contributing to GameDirector

Thanks for your interest! GameDirector is built so that porting it to a new
game is a small, well-defined piece of work — and that is exactly where we
want the most contributions.

## Ground rules

1. **Determinism is sacred.** `src/GameDirector.Core` stays zero-dependency
   `netstandard2.1`. Same timeline + same dt sequence must always produce the
   same event sequence. No wall-clock, RNG or I/O in the core.
2. **Tests are the contract.** `dotnet test` must be green before a PR. The
   suite includes installer/contract tests — behavior like "repeat install
   performs zero writes" is pinned by tests, not by convention.
3. **Honest status.** READMEs and docs distinguish *measured* from *planned*.
   Don't claim platform/game support that hasn't been run.
4. **No credentials, ever.** Model keys live in memory or local provider
   settings only — never in Film assets, plans, manifests, samples or tests.
5. **Production never calls a model.** Decision providers (Jev) live in the
   separate `GameDirector.Decisions` module; capture/edit jobs stay offline.
6. **Every fact is one row.** Content (cues, vocabulary, capabilities,
   manifests, bindings) is declared once in a table or contract row — see
   "Content shape" in `docs/architecture.md` before adding a second copy.

## Development setup

- .NET SDK **8.0.125+** (see `global.json`; `rollForward: latestMajor`)
  — the canonical Unity Core DLL rebuild/check requires **exactly 8.0.125**.
- Python 3 (release packaging + installer contract tests)
- Node 22 (Three engine tests); run `npm ci --ignore-scripts --prefix packages/gamedirector-three`
- FFmpeg + ffprobe with `libx264` and `libass` (media tests and take/edit)
- Unity 2022.3 LTS, only if you work on the Unity bridge package

```bash
dotnet test                              # core + installer + contract tests
python tools/test_unity_installer.py     # Python installer contract
python tools/test_archive_layout.py      # archive layout contract
node tools/test_document_state.mjs

# Engine contracts (build Debug CLI first)
dotnet restore --locked-mode
dotnet build --no-restore
python3 tools/test_cli_contract.py
npm ci --ignore-scripts --prefix packages/gamedirector-three
node tools/test_three_bundle.mjs
node tools/test_three_envelope.mjs
node tools/test_three_presentation.mjs
node tools/test_fps_agreement_unit.mjs
node tools/test_bridge_admission.mjs
node tools/test_three_hermetic.mjs
node tools/test_three_conformance.mjs
node tools/test_three_roles.mjs
node tools/test_three_measure.mjs
node tools/test_three_contract.mjs
python3 tools/test_engine_contract_reads.py --self-test
python3 tools/test_engine_contract_reads.py
bash tools/build_core_dll.sh --check
python3 tools/test_core_dll.py
```

## The best first contribution: a second-game adapter

Porting GameDirector to your game is deliberately small:

1. **Write a capability manifest** (`adapters/<yourgame>/manifests/*.json`):
   roles, actors + animation clips, locations, audio, shot types. Use
   `adapters/stardeck/manifest.json` as a reference.
   Registered capabilities and their per-row undeclared behavior are in
   `gd grammar` / `gd contract --json` → `capabilities[]`. Unregistered extension
   keys are accepted and identity-bearing, but uninterpreted. A new engine follows
   [the EngineContract implementer guide](docs/contract/README.md).
2. **Bind the scene** with Unity's GenericSceneAdapter and role/location markers,
   or Godot metadata; no code is needed for these paths. Three.js uses a small
   `adapter.js`, as in [Stardeck](adapters/stardeck/README.md). See
   [Adapters](docs/architecture.md#adapters) for custom runtime systems.
3. **Validate offline first** — sandbox/director mode only; no networking.
4. **Record one real take** and include the receipts (`take.json`, event log)
   as evidence in your PR.

Good first issues: Godot/UE adapter exploration, new shot-type primitives
(with compiler + player tests), manifest validators, docs translations.

## Pull requests

- One concern per PR; keep refactors separate from behavior changes.
- Add/adjust tests for any behavior change — a PR without a failing-then-
  passing test story will be asked for one.
- Update docs when you change a contract (CLI surface, DSL, manifest schema,
  distribution layout).
- Sample assets must be your own or freely licensed; say which in the PR.
- Fill in the PR template checklist; CI (`verify.yml`) must pass.

## Code style

Follow the existing patterns in the file you're touching. The codebase favors
explicit failure over silent fallback, plan-then-apply mutations, and
hash-verified artifacts — when in doubt, copy the strictest nearby example.
