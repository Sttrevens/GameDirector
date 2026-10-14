#!/usr/bin/env bash
# Sync the generated Core DLL into the Unity package from canonical src/.
# The DLL and its Unity metadata are committed; do not edit the DLL by hand.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$REPO_ROOT/tools/build_core_dll.sh"
