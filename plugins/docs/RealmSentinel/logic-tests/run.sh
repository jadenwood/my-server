#!/usr/bin/env bash
# Runs the RealmSentinel behaviour tests (Linux x64). Compiles plugins/RealmSentinel.cs, UNCHANGED, together with Mocks.cs
# (a minimal stand-in for the game and Oxide types the plugin touches) and Tests.cs, then replays every cheat the plugin
# watches for (speed, teleport, fly, reach, damage, fire rate, kill rate, item jumps, gather and craft rates, chat and
# command floods, reconnect cycling, staff-name impersonation), the responses (watch and enforce: alert, freeze, kick,
# ban), the commands, the Steward feed, persistence and corruption-safe load, and an hour each of honest high-ping
# players, on the .NET runtime that tools/plugin-compile-check/check.sh caches.
#
# What this proves: the plugin's own logic and state machine. What it does NOT prove: that the real game behaves like
# the mocks (when positions update on the server, what a hit reports, which containers a loot bag has). That the
# plugin's calls match the REAL Oxide 2.0.3867 / Assembly-CSharp metadata is proven separately by
# tools/plugin-compile-check/check.sh (C# 3, real DLLs). Mocks.cs serializes with System.Text.Json, not Oxide's Newtonsoft.
#
# Usage: plugins/docs/RealmSentinel/logic-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
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

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632 -out:"$OUT/RealmSentinelTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/Tests.cs" "$REPO/plugins/RealmSentinel.cs"

cat > "$OUT/RealmSentinelTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
"$CACHE/dn/dotnet" "$OUT/RealmSentinelTests.dll"
