#!/usr/bin/env bash
# Canonical Unity Core binary: pinned SDK, Release IL, no machine-local PDB.
# Build in scratch so --check never changes either the package or repo bin/obj.
set -euo pipefail
if [[ $# -gt 1 || ( $# -eq 1 && "$1" != "--check" ) ]]; then
  echo "Usage: bash tools/build_core_dll.sh [--check]" >&2; exit 2
fi
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# The canonical DLL is proven byte-for-byte with ONE Roslyn: the SDK version the
# repo pins. Read it from the repo's global.json (never restated here) and make
# the scratch build resolve exactly that SDK even where a newer one is installed.
SDK_VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$REPO_ROOT/global.json")"
if [[ -z "$SDK_VERSION" ]]; then
  echo "Cannot read sdk.version from $REPO_ROOT/global.json" >&2; exit 1
fi
BUILD_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/gd-core-dll.XXXXXX")"
# Roslyn sees physical input paths, not a symlinked scratch parent's spelling.
# The explicit PathMap below must use that same physical root on every OS.
BUILD_ROOT="$(cd "$BUILD_ROOT" && pwd -P)"
trap 'rm -rf "$BUILD_ROOT"' EXIT
export PATH="/opt/homebrew/bin:$PATH"
# Copy only build inputs; an earlier configuration's generated files must not
# enter Compile items. Directory.Build.props maps this root to the same path.
tar -C "$REPO_ROOT" --exclude=bin --exclude=obj -cf - \
  Directory.Build.props src/GameDirector.Core | tar -C "$BUILD_ROOT" -xf -
# Pin the SDK inside the scratch tree (the repo keeps rollForward:latestMajor so
# developers may build with newer SDKs; the canonical binary may not).
printf '{"sdk":{"version":"%s","rollForward":"disable"}}\n' "$SDK_VERSION" > "$BUILD_ROOT/global.json"
cd "$BUILD_ROOT"
ACTUAL="$(dotnet --version)" || ACTUAL=""
if [[ "$ACTUAL" != "$SDK_VERSION" ]]; then
  echo "install SDK $SDK_VERSION to build the canonical Unity Core DLL (required by $REPO_ROOT/global.json; canonical rollForward:disable; dotnet resolved '${ACTUAL:-none}')" >&2
  echo "Installed SDKs:" >&2
  dotnet --list-sdks >&2 || true
  exit 1
fi
dotnet restore src/GameDirector.Core/GameDirector.Core.csproj --locked-mode
dotnet build src/GameDirector.Core/GameDirector.Core.csproj --no-restore -c Release \
  -p:Deterministic=true -p:ContinuousIntegrationBuild=true \
  -p:PathMap="$BUILD_ROOT=/_/GameDirector" -p:DebugType=None -p:DebugSymbols=false
DLL="$BUILD_ROOT/src/GameDirector.Core/bin/Release/netstandard2.1/GameDirector.Core.dll"
TARGET="$REPO_ROOT/packages/com.gamedirector.unity/Plugins/GameDirector.Core.dll"
if [[ "${1:-}" == "--check" ]]; then
  if ! cmp -s "$DLL" "$TARGET"; then
    echo "Core DLL is stale: run tools/build_core_dll.sh and commit packages/com.gamedirector.unity/Plugins/GameDirector.Core.dll." >&2
    exit 1
  fi
  echo "OK: committed Core DLL matches the deterministic source build"
else
  mkdir -p "$(dirname "$TARGET")"
  cp "$DLL" "$TARGET"
  echo "OK: packages/com.gamedirector.unity/Plugins/GameDirector.Core.dll"
fi
