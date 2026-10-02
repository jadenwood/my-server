#!/usr/bin/env bash
# Runs the RealmSeasons + RealmEvents behaviour tests (Linux x64). Compiles plugins/RealmSeasons.cs and plugins/RealmEvents.cs, UNCHANGED, together with Mocks.cs
# (a minimal stand-in for the game and Oxide types the plugins touch) and Tests.cs, then runs the scenario checks
# (season start/end/ceremony, crown days, Hall of Kings, chronicle parsing, wipe survival, schedule countdowns,
# tournament scoring and prizes, truce enforcement, King's Hunt, Crown Night, corruption-safe load) on the .NET
# runtime that tools/plugin-compile-check/check.sh caches.
#
# What this proves: the plugins' own logic and state machine. What it does NOT prove: that the real game behaves like
# the mocks. That the plugins' calls match the REAL Oxide 2.0.3867 / Assembly-CSharp metadata is proven separately by
# tools/plugin-compile-check/check.sh (C# 3, real DLLs). Mocks.cs serializes with System.Text.Json, not Oxide's Newtonsoft.
#
# Usage: plugins/docs/RealmEvents/logic-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
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

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632 -out:"$OUT/RealmEventsTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/Tests.cs" "$REPO/plugins/RealmSeasons.cs" "$REPO/plugins/RealmEvents.cs"

cat > "$OUT/RealmEventsTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
"$CACHE/dn/dotnet" "$OUT/RealmEventsTests.dll" "$REPO"
