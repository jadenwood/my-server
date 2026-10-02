#!/usr/bin/env bash
# Cross-plugin behaviour test (Linux x64): compiles plugins/RealmLaws.cs and plugins/RealmContracts.cs, UNCHANGED,
# with Mocks.cs and Tests.cs, routes Plugin.Call the way Oxide does (non-public instance methods by name), and checks
# the court-outlawry hand-off end to end. Uses the .NET runtime tools/plugin-compile-check/check.sh caches.
# Mocks serialize with System.Text.Json, not Oxide's Newtonsoft. Nothing here proves in-game behaviour.
#
# Usage: tools/realm-integration/cross-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
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

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632 -out:"$OUT/RealmCrossTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/Tests.cs" "$REPO/plugins/RealmLaws.cs" "$REPO/plugins/RealmContracts.cs"

cat > "$OUT/RealmCrossTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
"$CACHE/dn/dotnet" "$OUT/RealmCrossTests.dll"
