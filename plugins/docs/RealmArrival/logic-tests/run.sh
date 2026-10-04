#!/usr/bin/env bash
# Runs the RealmArrival behaviour tests (Linux x64). Compiles plugins/RealmArrival.cs UNCHANGED together with Mocks.cs
# (a stand-in for the game and Oxide types the plugin touches: the spawn and respawn events, a server-side teleport, the
# spawn-point provider, the block grid, sleepers, popups, and timers on a virtual clock), World.cs (the Gatehouse site
# and fake RealmSentinel, RealmTravel, RealmWarden, RealmHouses, CrownAndConsequences, RealmEvents, RealmArena,
# RealmQuests, RealmRenown and RealmHerald) and Tests.cs, then runs the checks on the .NET runtime that
# tools/plugin-compile-check/check.sh caches.
#
# What this proves: the plugin's own logic. What it does NOT prove: that the real game behaves like the mocks. That the
# plugin's calls match the REAL Oxide 2.0.3867 / Assembly-CSharp metadata is proven separately by
# tools/plugin-compile-check/check.sh (C# 3, real DLLs). Mocks.cs serializes with System.Text.Json, not Newtonsoft.
#
# Usage: plugins/docs/RealmArrival/logic-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
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

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632,0618 -out:"$OUT/RealmArrivalTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/World.cs" "$HERE"/Tests*.cs "$REPO/plugins/RealmArrival.cs"

cat > "$OUT/RealmArrivalTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
"$CACHE/dn/dotnet" "$OUT/RealmArrivalTests.dll" "$REPO"
