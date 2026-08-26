#!/usr/bin/env bash
# Rebuild GameDirector.Core.dll into the Unity bridge package Plugins/ folder.
# Unity consumes the precompiled netstandard2.1 DLL so the Unity project needs
# no dotnet toolchain. Commit the rebuilt DLL with your change.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export PATH="/opt/homebrew/bin:$PATH"

cd "$REPO_ROOT"
dotnet build src/GameDirector.Core/GameDirector.Core.csproj -c Release

mkdir -p packages/com.gamedirector.unity/Plugins
cp src/GameDirector.Core/bin/Release/netstandard2.1/GameDirector.Core.dll \
   packages/com.gamedirector.unity/Plugins/

echo "OK: packages/com.gamedirector.unity/Plugins/GameDirector.Core.dll"
echo "NOTE: Unity will generate the .meta on first import; let it, then commit both."
