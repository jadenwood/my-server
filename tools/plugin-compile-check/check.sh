#!/usr/bin/env bash
# Compile-checks plugins/*.cs with Roslyn at C# 3 (-langversion:3) against the real metadata that
# Oxide.ReignOfKings 2.0.3867 ships (patched Assembly-CSharp.dll, Oxide.*.dll) and the .NET 2.0/3.5
# reference assemblies. Linux x64, needs curl, unzip, python3 and sha256sum. Nothing is run; it only compiles.
#
# Usage: tools/plugin-compile-check/check.sh [cache-dir]     (default cache: ~/.cache/realm-compile-check)
# Exit code 0 = all plugins compile together with no errors.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
CACHE="${1:-$HOME/.cache/realm-compile-check}"
mkdir -p "$CACHE"; cd "$CACHE"

OXIDE_ZIP_URL="https://github.com/OxideMod/Oxide.ReignOfKings/releases/download/2.0.3867/Oxide.ReignOfKings.zip"
OXIDE_ZIP_SHA="6c35c623fa9ee412945f61a32e8196091b40d56bfe9f8376d3d8e6243b72c6c8"
RUNTIME_VER="10.0.12"; HOST_VER="8.0.31"; TOOLSET_VER="5.9.0"; REFASM_VER="1.0.3"

nupkg() { # id version
  local id="$1" ver="$2"
  [ -d "pkg/$id" ] && return 0
  mkdir -p "pkg/$id"
  curl -fsSL -o "pkg/$id.nupkg" "https://api.nuget.org/v3-flatcontainer/$id/$ver/$id.$ver.nupkg"
  (cd "pkg/$id" && unzip -qo "../$id.nupkg")
}

if [ ! -f oxide/ROK_Data/Managed/Assembly-CSharp.dll ]; then
  curl -fsSL -o oxide.zip "$OXIDE_ZIP_URL"
  echo "$OXIDE_ZIP_SHA  oxide.zip" | sha256sum -c -
  mkdir -p oxide && (cd oxide && unzip -qo ../oxide.zip)
fi

nupkg microsoft.netcore.app.runtime.linux-x64 "$RUNTIME_VER"
nupkg runtime.linux-x64.microsoft.netcore.dotnethost "$HOST_VER"
nupkg microsoft.net.compilers.toolset "$TOOLSET_VER"
nupkg microsoft.netframework.referenceassemblies.net20 "$REFASM_VER"
nupkg microsoft.netframework.referenceassemblies.net35 "$REFASM_VER"

# Minimal dotnet layout (muxer + hostfxr + shared framework) just to run csc.dll.
if [ ! -x dn/dotnet ]; then
  RT=pkg/microsoft.netcore.app.runtime.linux-x64/runtimes/linux-x64
  mkdir -p "dn/host/fxr/$RUNTIME_VER" "dn/shared/Microsoft.NETCore.App/$RUNTIME_VER"
  cp pkg/runtime.linux-x64.microsoft.netcore.dotnethost/runtimes/linux-x64/native/dotnet dn/
  cp "$RT/native/libhostfxr.so" "dn/host/fxr/$RUNTIME_VER/"
  cp "$RT"/lib/net*/*.dll "$RT"/lib/net*/*.json "$RT"/native/* "dn/shared/Microsoft.NETCore.App/$RUNTIME_VER/"
  chmod +x dn/dotnet
fi

export DOTNET_ROOT="$CACHE/dn"
CSC=("$CACHE/dn/dotnet" "$(ls -d "$CACHE"/pkg/microsoft.net.compilers.toolset/tasks/netcore/bincore)/csc.dll" -nologo -noconfig -nostdlib+ -target:library)
R20="$CACHE/pkg/microsoft.netframework.referenceassemblies.net20/build/.NETFramework/v2.0"
R35="$CACHE/pkg/microsoft.netframework.referenceassemblies.net35/build/.NETFramework/v3.5"
M="$CACHE/oxide/ROK_Data/Managed"

# Compile-only stubs for the two Unity/uLink base classes the game types derive from (not shipped in the zip).
mkdir -p stubs
"${CSC[@]}" -r:"$R20/mscorlib.dll" -out:stubs/UnityEngine.dll "$HERE/UnityEngine.cs"
"${CSC[@]}" -r:"$R20/mscorlib.dll" -r:stubs/UnityEngine.dll -out:stubs/uLink.dll "$HERE/uLink.cs"

"${CSC[@]}" -langversion:3 -warnaserror- -nowarn:0649 -out:"$CACHE/plugins.dll" \
  -r:"$R20/mscorlib.dll" -r:"$R20/System.dll" -r:"$R35/System.Core.dll" \
  -r:"$M/Assembly-CSharp.dll" -r:"$M/Oxide.Core.dll" -r:"$M/Oxide.CSharp.dll" -r:"$M/Oxide.ReignOfKings.dll" \
  -r:"$M/Oxide.References.dll" -r:"$M/Oxide.Common.dll" -r:"$M/Oxide.Unity.dll" \
  -r:stubs/UnityEngine.dll -r:stubs/uLink.dll \
  "$REPO"/plugins/*.cs
echo "OK: plugins/*.cs compile at C# 3 against Oxide.ReignOfKings 2.0.3867 metadata."
