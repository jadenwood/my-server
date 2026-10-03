#!/usr/bin/env bash
# Runs the RealmPainter behaviour tests (Linux x64). Compiles plugins/RealmPainter.cs, UNCHANGED, together with Mocks.cs
# (a minimal stand-in for the game, Unity and Oxide types the plugin touches) and Tests.cs, then runs the scenario
# checks (PNG codec, art bundle, text layout, binding, live boards, rate limits, protection, faces and targeting, the
# registry) on the .NET runtime that tools/plugin-compile-check/check.sh caches. Then decode-check.mjs decodes every
# PNG the C# encoder wrote with the independent Node decoder (art/tools/painter/png.mjs and node:zlib) and compares
# the pixels.
#
# What this proves: the plugin's own logic and that its PNGs are valid. What it does NOT prove: that the real game
# accepts a server-set picture, shows it, or which way up (see plugins/docs/RealmPainter.md, "First test").
# That the plugin's calls match the REAL Oxide 2.0.3867 / Assembly-CSharp metadata is proven separately by
# tools/plugin-compile-check/check.sh.
#
# Usage: plugins/docs/RealmPainter/logic-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
#        PREVIEWS=1 ... also copies the preview boards to plugins/docs/RealmPainter/previews/
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

"${CSC[@]}" -target:exe -langversion:latest -nowarn:0649,0169,0414,8632,0618 -out:"$OUT/RealmPainterTests.dll" "${REFS[@]}" \
  "$HERE/Mocks.cs" "$HERE/Tests.cs" "$REPO/plugins/RealmPainter.cs"

cat > "$OUT/RealmPainterTests.runtimeconfig.json" <<JSON
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RTVER" },
  "configProperties": { "System.Globalization.Invariant": true } } }
JSON
mkdir -p "$OUT/png"
"$CACHE/dn/dotnet" "$OUT/RealmPainterTests.dll" "$REPO" "$OUT/png"
node "$HERE/decode-check.mjs" "$OUT/png"

if [ "${PREVIEWS:-}" = "1" ]; then
  mkdir -p "$HERE/../previews"
  for f in "$OUT"/png/board-*.png; do
    case "$(basename "$f")" in board-chronicle-sign.png) ;; *) cp "$f" "$HERE/../previews/" ;; esac
  done
  echo "previews copied to plugins/docs/RealmPainter/previews/"
fi
