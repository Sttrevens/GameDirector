<div align="center">

# GameDirector

**Let AI direct your game.**

Describe a scene in plain words. An AI agent directs it as a shot list,<br>
and your engine shoots it frame by frame. Give notes, and it re-shoots.

[![Verify](https://github.com/Sttrevens/GameDirector/actions/workflows/verify.yml/badge.svg)](https://github.com/Sttrevens/GameDirector/actions/workflows/verify.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Release](https://img.shields.io/badge/release-0.1.0-orange)](https://github.com/Sttrevens/GameDirector/releases)

[Quick start](#quick-start) · [Connect your agent](#connect-your-agent) · [Docs](#learn-more) · [中文](docs/README.zh-CN.md)

<img src="docs/media/hero.webp" width="100%" alt="Two robots, Bolt and Pip, wave and dance in a starship corridor while the camera orbits them, a shot directed by AI">

</div>

https://github.com/user-attachments/assets/f4008315-11ab-40ab-a28e-a7e5a67bd246

<p align="center"><sub>Every shot in this film was directed by AI and shot with GameDirector, using the <a href="samples/timelines/stardeck">Stardeck sample</a> in this repo.<br>Assets: RobotExpressive (CC0, Tomás Laulhé) · Space Ship Hallway (CC BY 4.0, yeeyeeman) · Music: "Dreaming Big" by Ahjay Stelino (Mixkit)</sub></p>

## How it works

| 1 · Assets | 2 · Direct | 3 · Revise |
|:---:|:---:|:---:|
| <img src="docs/media/step-1-assets.jpg" alt="The robots Bolt and Pip, their clips and the starship corridor, from the game's manifest"> | <img src="docs/media/step-2-direct.jpg" alt="A one-line prompt becomes a compiled three-shot storyboard"> | <img src="docs/media/step-3-revise.jpg" alt="Plain-language notes produce a revised six-shot cut"> |
| Your game describes itself: characters, animations, faces, props, locations and camera moves. The AI works only with what is on set. | Say what you want. The AI writes a timeline, and every role, clip and mark is checked before a single frame is shot. | Give notes in plain language. The AI revises the timeline and the engine re-shoots it. |

**AI directs. The engine executes.**

- **Reproducible.** A timeline is a plain JSON file. Shoot it again and you get the same frames. Diff it, version it, edit it by hand.
- **Verifiable.** Anything that is not in the game's manifest is rejected before the shoot. After the shoot, `gd review` checks the framing and `gd measure` reads back the exact camera and character positions.
- **Pluggable.** Unity, Godot and Three.js today. Any agent that speaks MCP can direct.

## Engines

|  | Unity | Godot | Three.js |
|---|---|---|---|
| Install | Package `.tgz` | Addon `.zip` | Node bridge + adapter |
| Shoot takes | ✅ | ✅ | ✅ |
| Editor panel | Director Workbench | GameDirector dock | — |
| Framing review (`gd review`) | Subject centre | Every character | Every character |
| Frame measurement (`gd measure`) | — | ✅ | ✅ |

Unreal Engine is next.

## Quick start

Everything is on the [Releases](https://github.com/Sttrevens/GameDirector/releases) page. Each download is self-contained: you do not need .NET, Node or Python.

| You use | Download | Size |
|---|---|---|
| Unity | `com.gamedirector.unity-0.1.0-<platform>.tgz` | 46–59 MB |
| Godot | `gamedirector-0.1.0-<platform>` + `gamedirector-godot-0.1.0.zip` | 50–64 MB |
| Three.js or the command line | `gamedirector-0.1.0-<platform>` | 50–64 MB |

Platforms: `win-x64`, `osx-arm64` (Apple silicon), `osx-x64` (Intel Mac) and `linux-x64`. On macOS and Linux, one command downloads and unpacks it:

```bash
curl -fsSL https://github.com/Sttrevens/GameDirector/releases/download/v0.1.0/gamedirector-0.1.0-osx-arm64.tar.gz | tar -xz
```

If you downloaded the archive in a browser instead, macOS marks the unpacked tools as downloaded and may refuse to run them. Run `xattr -dr com.apple.quarantine gamedirector-0.1.0-osx-arm64` right after unpacking, before you start any tool. If macOS has already blocked a tool, unpack again and clear the mark first.

The tools live in the `bin/` folder; nothing is added to your PATH. Encoding films needs FFmpeg: `bin/gd doctor` checks for it and `bin/gd media install` sets it up.

This release is tested on macOS with Apple silicon. The Windows, Linux and Intel Mac builds are included and less tested.

### Unity (2022.3+)

1. In Package Manager, choose **+ → Add package from tarball** and pick the `.tgz`.
2. **Window → GameDirector → Create Independent Example** builds a scene with characters, animation, audio and three shots.
3. Enter Play Mode and open **Window → GameDirector → Director Workbench** to direct and shoot, or use **Window → GameDirector → Copy MCP Config** to hand the set to your agent.

### Godot (4.6+)

1. Unzip `gamedirector-godot-0.1.0.zip` into your project and enable **GameDirector** in **Project Settings → Plugins**. In **Editor → Editor Settings**, set `gamedirector/gd_path` to the downloaded `bin/gd`.
2. Tag your scene: set `gamedirector_role` on characters and `gamedirector_location` on `Marker3D` nodes. The **GameDirector** dock lists everything it finds.
3. Click **Start bridge**, then **Copy MCP config**, and paste the config into your agent.

The full walkthrough is in the [Godot guide](docs/godot.md). Try it on the [Stardeck sample](samples/godot/stardeck/README.md).

### Three.js

Write a small adapter for your scene and run the bridge. The [Three.js guide](packages/gamedirector-three/README.md) explains both, and [`adapters/stardeck`](adapters/stardeck) is a working example.

## Connect your agent

GameDirector is an MCP server. Add it to Claude Code, Cursor, Codex or any MCP host:

```json
{
  "mcpServers": {
    "gamedirector": {
      "command": "<download folder>/bin/gd",
      "args": ["mcp"],
      "env": { "GAMEDIRECTOR_ENDPOINT": "http://127.0.0.1:39779" }
    }
  }
}
```

- **Unity:** **Window → GameDirector → Copy MCP Config** copies the right config for you.
- **Godot:** the dock's **Copy MCP config** button copies this, with your bridge's port.
- **Codex:** use the same command, args and env under `mcp_servers.gamedirector` in TOML.

Then just ask your agent:

> *A 16-second short: Bolt meets Pip in a starship corridor. They wave, then dance.*

And when you want it different:

> *Pacing feels flat. Open low, tracking Bolt. Give Pip a close-up when they meet, then orbit them during the dance.*

For better results, give your agent the directing playbook in [`skills/gamedirector/SKILL.md`](skills/gamedirector/SKILL.md).

## Command line

Everything the agent does is also a plain command (`bin/gd` in the download). Commands talk to the Unity bridge by default; add `--endpoint http://127.0.0.1:39779` for Godot.

```bash
gd validate timeline.json                  # check a timeline against the live game
gd take timeline.json --out take-01        # shoot it, frame by frame
gd review take-01                          # framing report for every shot
gd measure timeline.json --frames 0,48 --out measure-01   # exact camera and character numbers
gd edit edit.json --out cut-01             # cut takes into a finished film
gd expect-record timeline.json --out shot.expect.json --endpoint <bridge>   # lock a shot's numbers
gd expect-check  timeline.json --expect shot.expect.json --endpoint <bridge> # catch regressions
```

Every take ships with receipts: the timeline, the game manifest, a hash for every frame, and the encoded `picture.mp4`. See [the production pipeline](docs/production.md).

## Why GameDirector

Editor-automation tools let an AI click around your editor in real time. That helps while you build a game, but no two runs come out the same. GameDirector works like a film set instead:

- **The AI writes a script, not mouse moves.** The output is a timeline you can read, review and keep.
- **Your game says what is on set.** Roles, clips and locations come from the game, so the AI cannot invent things that do not exist.
- **The engine shoots it the same way every time,** so a note changes only what you asked to change.

## Learn more

- [Directing playbook](docs/directing-playbook.md): shot types, pacing and aspect ratios
- [Timeline format](docs/timeline-format.md)
- [Architecture](docs/architecture.md)
- [Engine contract](docs/contract/README.md): add your own engine
- [Install and distribution](docs/install.md)
- [Changelog](CHANGELOG.md)

<details>
<summary><b>Build from source</b></summary>

```bash
dotnet test
dotnet run --project src/GameDirector.Cli -- grammar     # timeline cheat sheet
dotnet run --project src/GameDirector.Cli -- --help
```

</details>

## Contributing

Contributions are welcome, especially adapters for your own game, new shot types and engine ports (Unreal!). See [CONTRIBUTING.md](CONTRIBUTING.md).

## Credits and license

[MIT](LICENSE) © 2026 Sttrevens.

The Stardeck sample uses [RobotExpressive](samples/assets/stardeck/ATTRIBUTION.md) by Tomás Laulhé (CC0) and "Space ship hallway" by yeeyeeman (CC BY 4.0). See [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md).
