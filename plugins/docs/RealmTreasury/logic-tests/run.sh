#!/usr/bin/env bash
# Runs the RealmTreasury behaviour tests (Linux x64). Compiles plugins/RealmTreasury.cs, UNCHANGED, together with Mocks.cs
# (a minimal stand-in for the game and Oxide types the plugin touches) and Tests.cs, then runs the scenario checks
# (mint caps, grants, purses, asks, bids, fees, owed/collect, partial-take faults, expiry, vaults, stewards, budgets,
# tithe levy, tax observation, escheat, freeze, audit/tamper detection, a 4000-step randomized zero-sum fuzz, reload,
# corruption-safe load, config clamps) on the .NET runtime that tools/plugin-compile-check/check.sh caches.
#
# What this proves: the plugin's own logic and state machine. What it does NOT prove: that the real game behaves like
# the mocks. That the plugin's calls match the REAL Oxide 2.0.3867 / Assembly-CSharp metadata is proven separately by
# tools/plugin-compile-check/check.sh (C# 3, real DLLs). Mocks.cs serializes with System.Text.Json, not Oxide's Newtonsoft.
#
# Usage: plugins/docs/RealmTreasury/logic-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
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

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632 -out:"$OUT/RealmTreasuryTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/Tests.cs" "$REPO/plugins/RealmTreasury.cs"

cat > "$OUT/RealmTreasuryTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
"$CACHE/dn/dotnet" "$OUT/RealmTreasuryTests.dll"
