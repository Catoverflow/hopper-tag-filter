#!/bin/sh
# Builds the mod into dist/HopperTagFilter against the game's own assemblies.
# "./build.sh install" also copies it into the game's Mods folder.
# Needs Mono's C# compiler (csc or mcs). Set GAME_DIR if the game is installed elsewhere.
set -eu

here="$(cd "$(dirname "$0")" && pwd)"
game="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Probably Stolen}"
managed="$game/Probably Stolen_Data/Managed"
out="$here/dist/HopperTagFilter"

if [ ! -f "$managed/Assembly-CSharp.dll" ]; then
	echo "Game not found at: $game (set GAME_DIR)" >&2
	exit 1
fi

if command -v csc >/dev/null 2>&1; then compiler=csc; else compiler=mcs; fi

rm -rf "$out"
mkdir -p "$out/Localization"

"$compiler" -nologo -target:library -optimize+ -debug- -nostdlib -noconfig -langversion:7.3 \
	-out:"$out/HopperTagFilter.dll" \
	-r:"$managed/mscorlib.dll" \
	-r:"$managed/netstandard.dll" \
	-r:"$managed/System.dll" \
	-r:"$managed/System.Core.dll" \
	-r:"$managed/Assembly-CSharp.dll" \
	-r:"$managed/0Harmony.dll" \
	-r:"$managed/UnityEngine.dll" \
	-r:"$managed/UnityEngine.CoreModule.dll" \
	-r:"$managed/UnityEngine.UI.dll" \
	-r:"$managed/UnityEngine.UIModule.dll" \
	-r:"$managed/UnityEngine.InputLegacyModule.dll" \
	-r:"$managed/UnityEngine.TextRenderingModule.dll" \
	-r:"$managed/UnityEngine.AudioModule.dll" \
	-r:"$managed/Unity.TextMeshPro.dll" \
	"$here/src/HopperTagFilter.cs"

cp "$here/manifest.xml" "$out/manifest.xml"
cp "$here"/Localization/*.csv "$out/Localization/"
echo "Built $out"

if [ "${1:-}" = "install" ]; then
	target="$game/Mods/HopperTagFilter"
	rm -rf "$target"
	mkdir -p "$game/Mods"
	cp -r "$out" "$target"
	echo "Installed $target"
fi
