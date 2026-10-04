#!/usr/bin/env bash
# Runs the RealmArena behaviour tests (Linux x64). Compiles plugins/RealmArena.cs and plugins/RealmTreasury.cs, UNCHANGED,
# together with Mocks.cs (a stand-in for the game and Oxide types the plugins touch, with player health that takes blows
# the way the game does), World.cs (the shared test world) and Tests.cs, then runs the checks (challenges and answers,
# the ring, the felling guard, interference, wagers through the treasury's escrow and its zero-sum audit, Elo and the
# abuse limits, team duels, the weekly champion, brackets and the Royal Tournament hook, trial by combat, the tavern
# games, popups, reloads, damaged data and config) on the .NET runtime that tools/plugin-compile-check/check.sh caches.
#
# What this proves: the plugin's own logic and state machine, and that its stakes balance in the real treasury code.
# What it does NOT prove: that the real game behaves like the mocks. That the plugin's calls match the REAL
# Oxide 2.0.3867 / Assembly-CSharp metadata is proven separately by tools/plugin-compile-check/check.sh (C# 3, real DLLs).
# Mocks.cs serializes with System.Text.Json, not Newtonsoft.
#
# Usage: plugins/docs/RealmArena/logic-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
# Exit code 0 = all checks pass.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../../.." && pwd)"
CACHE="${1:-$HOME/.cache/realm-compile-check}"

if [ ! -x "$CACHE/dn/dotnet" ]; then
  "$REPO/tools/plugin-compile-check/check.sh" "$CACHE" >/dev/null
fi

export DOTNET_ROOT="$CACHE/dn"
RTDIR="$(ls -d "$CACHE"/dn/shared/Microsoft.NETCore.App/*/ | head -n1)"
RTVER="$(basename "$RTDIR")"
CSC=("$CACHE/dn/dotnet" "$(ls -d "$CACHE"/pkg/microsoft.net.compilers.toolset/tasks/netcore/bincore)/csc.dll" -nologo -noconfig -nostdlib+)
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

REFS=()
for f in "$RTDIR"*.dll; do
  case "$(basename "$f")" in
    mscorlib.dll|Microsoft.VisualBasic*|System.Private.*) ;;
    *) REFS+=("-r:$f") ;;
  esac
done
REFS+=("-r:${RTDIR}System.Private.CoreLib.dll")

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632,0618,0168,0219,SYSLIB0023 -out:"$OUT/RealmArenaTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/World.cs" "$HERE/Tests.cs" "$REPO/plugins/RealmArena.cs" "$REPO/plugins/RealmTreasury.cs"

cat > "$OUT/RealmArenaTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
"$CACHE/dn/dotnet" "$OUT/RealmArenaTests.dll" "$REPO"
