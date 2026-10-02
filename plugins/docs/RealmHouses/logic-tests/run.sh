#!/usr/bin/env bash
# Runs the RealmHouses behaviour tests (Linux x64). Compiles plugins/RealmHouses.cs, UNCHANGED, together with Mocks.cs
# (a minimal stand-in for the game and Oxide types the plugin touches) and Tests.cs, then runs the scenario checks
# (founding in chat and through the name and sigil input windows, swearing with the Yes/No and Accept/Refuse windows,
# renouncing, every chat fallback, stale, repeated, late and post-reload answers, the UsePopups switch, a game that
# throws, /realm popups off through RealmHerald, reload) on the .NET runtime that tools/plugin-compile-check/check.sh
# caches.
#
# What this proves: the plugin's own logic. What it does NOT prove: that the real game behaves like the mocks (that
# the windows show on a client, how they look, and that the answers come back). That the plugin's calls match the
# REAL Oxide 2.0.3867 / Assembly-CSharp metadata is proven separately by tools/plugin-compile-check/check.sh.
#
# Usage: plugins/docs/RealmHouses/logic-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
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

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632 -out:"$OUT/RealmHousesTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/Tests.cs" "$REPO/plugins/RealmHouses.cs"

cat > "$OUT/RealmHousesTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
"$CACHE/dn/dotnet" "$OUT/RealmHousesTests.dll" "$REPO"
