#!/usr/bin/env bash
# Runs the RealmRavens logic tests (Linux x64). Compiles plugins/RealmRavens.cs together with RulesTests.cs at
# C# 3 against the same Oxide 2.0.3867 metadata as tools/plugin-compile-check/check.sh, then runs the result on the
# .NET runtime that check.sh caches. Nothing touches a game server; no game type is constructed.
#
# Usage: plugins/docs/RealmRavens/logic-tests/run.sh [cache-dir]   (default: ~/.cache/realm-compile-check)
# Exit code 0 = all tests pass.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../../.." && pwd)"
CACHE="${1:-$HOME/.cache/realm-compile-check}"

# check.sh downloads and verifies the Oxide zip (sha256) and the compiler/runtime packages on first use.
if [ ! -x "$CACHE/dn/dotnet" ] || [ ! -f "$CACHE/stubs/uLink.dll" ]; then
  "$REPO/tools/plugin-compile-check/check.sh" "$CACHE" >/dev/null
fi

export DOTNET_ROOT="$CACHE/dn"
CSC=("$CACHE/dn/dotnet" "$(ls -d "$CACHE"/pkg/microsoft.net.compilers.toolset/tasks/netcore/bincore)/csc.dll" -nologo -noconfig -nostdlib+)
R20="$CACHE/pkg/microsoft.netframework.referenceassemblies.net20/build/.NETFramework/v2.0"
R35="$CACHE/pkg/microsoft.netframework.referenceassemblies.net35/build/.NETFramework/v3.5"
M="$CACHE/oxide/ROK_Data/Managed"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

"${CSC[@]}" -target:exe -langversion:3 -nowarn:0649 -out:"$OUT/RealmRavensTests.dll" \
  -r:"$R20/mscorlib.dll" -r:"$R20/System.dll" -r:"$R35/System.Core.dll" \
  -r:"$M/Assembly-CSharp.dll" -r:"$M/Oxide.Core.dll" -r:"$M/Oxide.CSharp.dll" -r:"$M/Oxide.ReignOfKings.dll" \
  -r:"$M/Oxide.References.dll" -r:"$M/Oxide.Common.dll" -r:"$M/Oxide.Unity.dll" \
  -r:"$CACHE/stubs/UnityEngine.dll" -r:"$CACHE/stubs/uLink.dll" \
  "$REPO/plugins/RealmRavens.cs" "$HERE/RulesTests.cs"

cp "$M"/Assembly-CSharp.dll "$M"/Oxide.*.dll "$CACHE"/stubs/uLink.dll "$OUT/"
# Runtime-only UnityEngine stub (a superset of the compile stub): Oxide's Newtonsoft touches Unity vector types.
"${CSC[@]}" -target:library -r:"$R20/mscorlib.dll" -out:"$OUT/UnityEngine.dll" "$HERE/UnityEngineRuntimeStub.cs"
RT="$(ls "$CACHE/dn/shared/Microsoft.NETCore.App" | head -n1)"
cat > "$OUT/RealmRavensTests.runtimeconfig.json" <<EOF
{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "$RT" },
  "configProperties": { "System.Globalization.Invariant": true } } }
EOF
"$CACHE/dn/dotnet" "$OUT/RealmRavensTests.dll"
