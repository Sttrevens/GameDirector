# Third-party notices

GameDirector's own code is licensed under [MIT](LICENSE). Third-party components
retain their respective licenses. Versions and license identifiers below were
checked against the actual NuGet `.nuspec`, npm `package.json`, and bundled font
license files, not inferred from project names.

## Components used by the shipped applications

| Component | Version | License | Upstream |
| --- | --- | --- | --- |
| .NET runtime | 8.0.25 | MIT | [dotnet/runtime](https://github.com/dotnet/runtime) |
| ASP.NET Core runtime | 8.0.25 | MIT | [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore) |
| ModelContextProtocol and ModelContextProtocol.Core | 2.2.0 | Apache-2.0 | [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk) |
| Microsoft.Extensions.Hosting | 10.0.11 | MIT | [dotnet](https://github.com/dotnet/dotnet) |
| Noto Sans CJK SC | bundled Regular font | SIL Open Font License 1.1 | [Noto CJK](https://github.com/notofonts/noto-cjk) |

The .NET and ASP.NET Core runtime packages identify Microsoft as author and
copyright holder. The MCP packages carry © Model Context Protocol a Series of
LF Projects, LLC. Microsoft.Extensions.Hosting carries © Microsoft Corporation.
Runtime dependencies may include additional third-party components covered by
the runtime's upstream notices:
[.NET license](https://github.com/dotnet/runtime/blob/v8.0.25/LICENSE.TXT),
[.NET third-party notices](https://github.com/dotnet/runtime/blob/v8.0.25/THIRD-PARTY-NOTICES.TXT),
[ASP.NET Core license](https://github.com/dotnet/aspnetcore/blob/v8.0.25/LICENSE.txt).
The MCP SDK uses the [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0).
Microsoft.Extensions packages use the [MIT license](https://licenses.nuget.org/MIT).

The font's complete license is [Fonts/OFL.txt](src/GameDirector.Client/Fonts/OFL.txt).
The font, its license, and provenance accompany the application under `Fonts/`
(and `Tools~/bin/Fonts/` in the Unity bundle). The font is not relicensed
under GameDirector's MIT license.

## Optional Three engine tooling

These dependencies are installed by npm; release archives exclude `node_modules`.

| Component | Version | License | Upstream |
| --- | --- | --- | --- |
| esbuild and its optional platform packages | 0.25.12 | MIT | [esbuild](https://github.com/evanw/esbuild) |
| three | 0.170.0 | MIT | [three.js](https://github.com/mrdoob/three.js) |
| playwright and playwright-core | 1.62.1 | Apache-2.0 | [Playwright](https://github.com/microsoft/playwright) |

The Unity package declares `com.unity.nuget.newtonsoft-json` 3.2.1 as a Package
Manager dependency, not a vendored binary. Unity supplies that dependency and
its license notices through Package Manager.

FFmpeg and ffprobe are external tools, not included in these archives. Their
licenses depend on the user's selected build; consult that build's notices.
The optional game-specific adapter retains its existing package license field;
this notice does not grant additional rights to external game assets or code.
