#!/usr/bin/env bash
# Sync everything the Unity-side packages need from the canonical sources:
#   1. GameDirector.Core.dll -> com.gamedirector.unity/Plugins/ (via build_core_dll.sh)
#   2. cdrebirth.manifest.json -> com.gamedirector.cdrebirth/Runtime/ (TextAsset copy)
# The copies are generated artifacts but ARE committed (Unity imports them with
# auto-generated .meta files). Canonical sources remain in src/ and adapters/.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

"$REPO_ROOT/tools/build_core_dll.sh"

cp "$REPO_ROOT/adapters/cdrebirth/manifests/cdrebirth.manifest.json" \
   "$REPO_ROOT/adapters/cdrebirth/com.gamedirector.cdrebirth/Runtime/cdrebirth.manifest.json"

echo "OK: manifest synced into adapter package Runtime/"
