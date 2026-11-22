#!/bin/sh
# Prepares a Steam Workshop upload: builds the mod and writes dist/workshop.vdf.
# The upload itself is the steamcmd command printed at the end, which you run yourself.
#
#   ./workshop.sh "what changed"                 new item: created hidden; existing item: content only
#   VISIBILITY=0 ./workshop.sh "what changed"    also set the visibility (0 public, 1 friends, 2 hidden)
#   TEXT=1 ./workshop.sh "what changed"          also send the title and description below
#
# The first upload creates the item. Its id is then kept in workshop_id, so later runs update
# the same item and leave its title, description and visibility as they are on Steam.
# steamcmd cannot set Workshop tags; workshop.py can.
set -eu

here="$(cd "$(dirname "$0")" && pwd)"
vdf="$here/dist/workshop.vdf"
title="料斗标签过滤 Hopper Tag Filter"
description="进料斗的筛选列表可以添加标签，按物品类型搬运。
Adds tags to the intake hopper's filter list, so it can move items by type."
visibility="${VISIBILITY:-}"
note="${1:-}"

# steamcmd writes a new item's id back into the .vdf; keep it.
if [ ! -f "$here/workshop_id" ] && [ -f "$vdf" ]; then
	found="$(sed -n 's/.*"publishedfileid"[[:space:]]*"\([0-9]*\)".*/\1/p' "$vdf" | head -n 1)"
	if [ -n "$found" ] && [ "$found" != "0" ]; then
		echo "$found" > "$here/workshop_id"
		echo "Saved Workshop item id $found to workshop_id"
	fi
fi
id="$(cat "$here/workshop_id" 2>/dev/null || echo 0)"

"$here/build.sh"

preview=""
if [ -f "$here/preview.png" ]; then
	preview="	\"previewfile\"      \"$here/preview.png\""
fi

# A new item gets the title and description and starts hidden. An existing one keeps what is
# on Steam unless asked otherwise: keys left out of the file are left alone.
if [ "$id" = "0" ]; then
	visibility="${visibility:-2}"
	text=1
else
	text="${TEXT:-}"
fi
extra=""
if [ -n "$visibility" ]; then
	extra="	\"visibility\"       \"$visibility\""
fi
if [ -n "$text" ]; then
	extra="$extra
	\"title\"            \"$title\"
	\"description\"      \"$description\""
fi

cat > "$vdf" <<VDF
"workshopitem"
{
	"appid"            "4348910"
	"publishedfileid"  "$id"
	"contentfolder"    "$here/dist/HopperTagFilter"
$preview
$extra
	"changenote"       "$note"
}
VDF

echo "Wrote $vdf (item id $id)"
echo "Upload with:"
echo "  steamcmd +login <your_steam_account> +workshop_build_item \"$vdf\" +quit"
