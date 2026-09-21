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

## Development setup

- .NET SDK **8.0.125+** (see `global.json`; `rollForward: latestFeature`)
- Python 3 (release packaging + installer contract tests)
- FFmpeg + ffprobe with `libx264` and `libass` (media tests and take/edit)
- Unity 2022.3 LTS, only if you work on the Unity bridge package

```bash
dotnet test                              # core + installer + contract tests
python tools/test_unity_installer.py     # Python installer contract
python tools/test_archive_layout.py      # archive layout contract
```

## The best first contribution: a second-game adapter

Porting GameDirector to your game is deliberately small:

1. **Write a capability manifest** (`adapters/<yourgame>/manifests/*.json`):
   roles, actors + animation clips, locations, audio, shot types. Use
   `adapters/cdrebirth/manifests/cdrebirth.manifest.json` as a reference.
2. **Implement a thin adapter** over the generic base in
   `packages/com.gamedirector.unity` (Unity), or model it on the Three.js
   interpreter (`packages/gamedirector-three`) and the
   [Sandring recipe](adapters/sandring/README.md) for another engine.
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
