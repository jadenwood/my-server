#!/usr/bin/env bash
# Runs the RealmSculptor behaviour tests (Linux x64). Compiles plugins/RealmSculptor.cs, UNCHANGED, together with
# Mocks.cs (a minimal stand-in for the game and Oxide types the plugin touches) and Tests.cs, then runs the scenario
# checks (loading the real art/sculptures files, batching, painting after the block exists, turning, conflicts and
# force, undo and remove, protection, the decay guard, repair, a restart in the middle of a job, a damaged data file,
# the material dump) on the .NET runtime that tools/plugin-compile-check/check.sh caches. It also reads the REAL
# Assembly-CSharp.dll metadata to prove that every member the plugin binds by reflection exists with that shape.
#
# What this proves: the plugin's own logic, and that the reflected names exist. What it does NOT prove: that the real
# game behaves like the mocks (for example that clients show server-placed blocks, or how a colour looks on a texture).
#
# Usage: plugins/docs/RealmSculptor/logic-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
# Exit code 0 = all checks pass.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../../.." && pwd)"
CACHE="${1:-$HOME/.cache/realm-compile-check}"

if [ ! -x "$CACHE/dn/dotnet" ] || [ ! -f "$CACHE/oxide/ROK_Data/Managed/Assembly-CSharp.dll" ]; then
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

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632 -out:"$OUT/RealmSculptorTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/Tests.cs" "$REPO/plugins/RealmSculptor.cs"

cat > "$OUT/RealmSculptorTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
"$CACHE/dn/dotnet" "$OUT/RealmSculptorTests.dll" "$REPO" "$CACHE/oxide/ROK_Data/Managed/Assembly-CSharp.dll"
