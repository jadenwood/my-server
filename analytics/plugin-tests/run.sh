#!/usr/bin/env bash
# Runs the RealmStats behaviour tests (Linux x64). Compiles plugins/RealmStats.cs, UNCHANGED, with Mocks.cs (a minimal
# stand-in for the game and Oxide types the plugin touches) and Tests.cs, then runs the scenario checks on the .NET
# runtime that tools/plugin-compile-check/check.sh caches.
#
# What this proves: the plugin's own logic, data format and corruption handling. What it does NOT prove: that the real
# game behaves like the mocks (that is UNVERIFIED until the smoke test). That the plugin's calls match the REAL
# Oxide 2.0.3867 / Assembly-CSharp metadata is proven separately by tools/plugin-compile-check/check.sh.
# Mocks.cs serializes with System.Text.Json (field names, like Oxide's Newtonsoft default), not Newtonsoft itself.
#
# Usage: analytics/plugin-tests/run.sh [cache-dir]          (default: ~/.cache/realm-compile-check)
#        RS_EXPORT=<dir> analytics/plugin-tests/run.sh      also writes a simulated 14-day data set to <dir>
# Exit code 0 = all checks pass.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
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

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632,0219,0168 -out:"$OUT/RealmStatsTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/Tests.cs" "$REPO/plugins/RealmStats.cs"

cat > "$OUT/RealmStatsTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
"$CACHE/dn/dotnet" "$OUT/RealmStatsTests.dll"
