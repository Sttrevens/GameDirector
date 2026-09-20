#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet build "$root/src/GameDirector.Workbench" -v quiet
exec dotnet "$root/src/GameDirector.Workbench/bin/Debug/net8.0/gamedirector-workbench.dll" --open true "$@"
