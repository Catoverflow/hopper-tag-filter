#!/usr/bin/env python3
"""Publishes the mod to the Steam Workshop through Steam's own API.

Unlike steamcmd's workshop_build_item, this can set the item's tags. It needs the Steam client
running and logged in with an account that owns the game, and a 64-bit libsteam_api.so, which
most Linux games on Steam ship (one is picked from the Steam library; STEAM_API_LIB overrides).

    ./workshop.py --check                  talk to Steam, upload nothing
    ./workshop.py "what changed"           build and upload; a new item starts hidden
    ./workshop.py --public "what changed"  same, and make the item public
    ./workshop.py --tags-only              only set the tags of the existing item

The first upload creates the item and stores its id in workshop_id; later runs update it.
Title and description are sent when the item is created, and afterwards only with --text, so
edits made on the item's Steam page are kept.
"""

import argparse
import ctypes
import glob
import os
import subprocess
import sys
import time

APP_ID = 4348910
TITLE = "料斗标签过滤 Hopper Tag Filter"
DESCRIPTION = (
    "进料斗的筛选列表可以添加标签，按物品类型搬运。\n"
    "Adds tags to the intake hopper's filter list, so it can move items by type."
)
# Sent as the item's whole tag list on every upload.
TAGS = ["QoL", "Utilities", "Storage"]

HERE = os.path.dirname(os.path.abspath(__file__))
CONTENT = os.path.join(HERE, "dist", "HopperTagFilter")
PREVIEW = os.path.join(HERE, "preview.png")
ID_FILE = os.path.join(HERE, "workshop_id")

VISIBILITY = {"public": 0, "friends": 1, "hidden": 2}
CREATE_ITEM_RESULT = 3403
SUBMIT_ITEM_UPDATE_RESULT = 3404
RESULT_NAMES = {
    1: "OK", 2: "Fail", 8: "InvalidParam", 9: "FileNotFound", 15: "AccessDenied", 16: "Timeout",
    17: "Banned", 21: "NotLoggedOn", 24: "InsufficientPrivilege", 25: "LimitExceeded",
    33: "DuplicateName", 54: "LockingFailed",
}
UPDATE_STATUS = {1: "preparing config", 2: "preparing content", 3: "uploading content",
                 4: "uploading preview", 5: "committing changes"}
NEEDED = ["SteamAPI_RunCallbacks", "SteamAPI_Shutdown", "SteamAPI_ISteamUGC_CreateItem",
          "SteamAPI_ISteamUGC_StartItemUpdate", "SteamAPI_ISteamUGC_SetItemTags",
          "SteamAPI_ISteamUGC_SubmitItemUpdate", "SteamAPI_ISteamUtils_IsAPICallCompleted",
          "SteamAPI_ISteamUtils_GetAPICallResult"]


# Steam's structures are packed to 4 bytes on Linux.
class StringArray(ctypes.Structure):
    _pack_ = 4
    _layout_ = "ms"
    _fields_ = [("strings", ctypes.POINTER(ctypes.c_char_p)), ("count", ctypes.c_int32)]


class CreateItemResult(ctypes.Structure):
    _pack_ = 4
    _layout_ = "ms"
    _fields_ = [("result", ctypes.c_int32), ("file_id", ctypes.c_uint64), ("needs_agreement", ctypes.c_bool)]


class SubmitItemUpdateResult(ctypes.Structure):
    _pack_ = 4
    _layout_ = "ms"
    _fields_ = [("result", ctypes.c_int32), ("needs_agreement", ctypes.c_bool), ("file_id", ctypes.c_uint64)]


def fail(message):
    print("error: " + message, file=sys.stderr)
    sys.exit(1)


def load_library():
    """The newest 64-bit libsteam_api.so that has everything this script calls."""
    candidates = []
    if os.environ.get("STEAM_API_LIB"):
        candidates.append(os.environ["STEAM_API_LIB"])
    else:
        common = os.path.expanduser("~/.local/share/Steam/steamapps/common")
        candidates += sorted(glob.glob(os.path.join(common, "**", "libsteam_api.so"), recursive=True))
    best = None
    for path in candidates:
        try:
            lib = ctypes.CDLL(path)
        except OSError:
            continue  # 32-bit copy, or not loadable
        if not all(hasattr(lib, name) for name in NEEDED) or not interface(lib, "SteamUtils"):
            continue
        version = interface_version(lib, "SteamUGC")
        if version and (best is None or version > best[0]):
            best = (version, lib, path)
    if best is None:
        fail("no usable 64-bit libsteam_api.so found; set STEAM_API_LIB to one")
    return best[1], best[2]


def interface_version(lib, name):
    """The newest version of an interface the library exports, e.g. 21 for SteamAPI_SteamUGC_v021."""
    for version in range(40, 0, -1):
        if hasattr(lib, "SteamAPI_%s_v%03d" % (name, version)):
            return version
    return 0


def interface(lib, name):
    version = interface_version(lib, name)
    if not version:
        return None
    accessor = getattr(lib, "SteamAPI_%s_v%03d" % (name, version))
    accessor.restype = ctypes.c_void_p
    accessor.argtypes = []
    return accessor


def declare(lib):
    p, u32, u64, text = ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint64, ctypes.c_char_p
    lib.SteamAPI_RunCallbacks.restype = None
    lib.SteamAPI_Shutdown.restype = None
    lib.SteamAPI_ISteamUGC_CreateItem.restype = u64
    lib.SteamAPI_ISteamUGC_CreateItem.argtypes = [p, u32, ctypes.c_int]
    lib.SteamAPI_ISteamUGC_StartItemUpdate.restype = u64
    lib.SteamAPI_ISteamUGC_StartItemUpdate.argtypes = [p, u32, u64]
    for name in ("Title", "Description", "Content", "Preview"):
        setter = getattr(lib, "SteamAPI_ISteamUGC_SetItem" + name)
        setter.restype = ctypes.c_bool
        setter.argtypes = [p, u64, text]
    lib.SteamAPI_ISteamUGC_SetItemVisibility.restype = ctypes.c_bool
    lib.SteamAPI_ISteamUGC_SetItemVisibility.argtypes = [p, u64, ctypes.c_int]
    # Newer libraries take a fourth "allow admin tags" flag; older ones ignore the extra argument.
    lib.SteamAPI_ISteamUGC_SetItemTags.restype = ctypes.c_bool
    lib.SteamAPI_ISteamUGC_SetItemTags.argtypes = [p, u64, ctypes.POINTER(StringArray), ctypes.c_bool]
    lib.SteamAPI_ISteamUGC_SubmitItemUpdate.restype = u64
    lib.SteamAPI_ISteamUGC_SubmitItemUpdate.argtypes = [p, u64, text]
    lib.SteamAPI_ISteamUGC_GetItemUpdateProgress.restype = ctypes.c_int
    lib.SteamAPI_ISteamUGC_GetItemUpdateProgress.argtypes = [p, u64, ctypes.POINTER(u64), ctypes.POINTER(u64)]
    lib.SteamAPI_ISteamUtils_IsAPICallCompleted.restype = ctypes.c_bool
    lib.SteamAPI_ISteamUtils_IsAPICallCompleted.argtypes = [p, u64, ctypes.POINTER(ctypes.c_bool)]
    lib.SteamAPI_ISteamUtils_GetAPICallResult.restype = ctypes.c_bool
    lib.SteamAPI_ISteamUtils_GetAPICallResult.argtypes = [p, u64, p, ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_bool)]


def start_steam(lib):
    os.environ["SteamAppId"] = str(APP_ID)
    os.environ["SteamGameId"] = str(APP_ID)
    if hasattr(lib, "SteamAPI_InitFlat"):
        message = ctypes.create_string_buffer(1024)
        lib.SteamAPI_InitFlat.restype = ctypes.c_int
        ok = lib.SteamAPI_InitFlat(message) == 0
        detail = message.value.decode("utf-8", "replace")
    else:
        init = getattr(lib, "SteamAPI_InitSafe", None) or lib.SteamAPI_Init
        init.restype = ctypes.c_bool
        ok, detail = bool(init()), ""
    if not ok:
        fail("could not connect to Steam (%s). Is the Steam client running and logged in, "
             "and does the account own the game?" % (detail or "no detail given"))


def wait_for_logon(lib, seconds=30):
    """Right after connecting, Steam can still report the user as not logged on for a moment,
    and uploads sent in that window are refused. Runs callbacks until it says otherwise."""
    accessor = interface(lib, "SteamUser")
    logged_on = getattr(lib, "SteamAPI_ISteamUser_BLoggedOn", None)
    if accessor is None or logged_on is None:
        time.sleep(2)
        return
    logged_on.restype = ctypes.c_bool
    logged_on.argtypes = [ctypes.c_void_p]
    user = accessor()
    deadline = time.time() + seconds
    while time.time() < deadline:
        lib.SteamAPI_RunCallbacks()
        if user and logged_on(user):
            return
        time.sleep(0.1)
    fail("the Steam client is running but not connected to Steam. Logging in with steamcmd "
         "replaces the client's session and leaves it offline; choose Steam > Go Online "
         "(or restart Steam) and run this again.")


def pump(lib, seconds):
    deadline = time.time() + seconds
    while time.time() < deadline:
        lib.SteamAPI_RunCallbacks()
        time.sleep(0.1)


def wait(lib, utils, call, result, callback_id, progress=None):
    """Runs Steam's callbacks until the call finishes, then reads its result structure."""
    failed = ctypes.c_bool(False)
    deadline = time.time() + 1800
    last = None
    while not lib.SteamAPI_ISteamUtils_IsAPICallCompleted(utils, call, ctypes.byref(failed)):
        if time.time() > deadline:
            fail("Steam did not answer within 30 minutes")
        if progress is not None:
            status = progress()
            if status != last and status:
                print("  " + status)
                last = status
        lib.SteamAPI_RunCallbacks()
        time.sleep(0.1)
    size = ctypes.sizeof(result)
    if not lib.SteamAPI_ISteamUtils_GetAPICallResult(utils, call, ctypes.byref(result), size, callback_id, ctypes.byref(failed)) or failed.value:
        fail("Steam reported that the request failed before giving a result")
    return result


def result_name(code):
    return "%d (%s)" % (code, RESULT_NAMES.get(code, "see EResult in the Steamworks documentation"))


def read_id():
    try:
        return int(open(ID_FILE).read().strip() or "0")
    except (OSError, ValueError):
        return 0


def main():
    parser = argparse.ArgumentParser(description="Publish the mod to the Steam Workshop.")
    parser.add_argument("note", nargs="?", default="", help="change note shown on the item's page")
    parser.add_argument("--check", action="store_true", help="connect to Steam and stop; nothing is uploaded")
    parser.add_argument("--tags-only", action="store_true", help="only set the tags of the existing item")
    parser.add_argument("--text", action="store_true", help="also send the title and description")
    group = parser.add_mutually_exclusive_group()
    for name in VISIBILITY:
        group.add_argument("--" + name, dest="visibility", action="store_const", const=name)
    args = parser.parse_args()

    lib, path = load_library()
    declare(lib)
    print("Steam API library: " + path)
    item = read_id()
    if args.tags_only and not item:
        fail("--tags-only needs an existing item; put its id in " + ID_FILE)
    if not args.check and not args.tags_only:
        subprocess.run([os.path.join(HERE, "build.sh")], check=True)

    start_steam(lib)
    try:
        ugc = interface(lib, "SteamUGC")()
        utils = interface(lib, "SteamUtils")()
        if not ugc or not utils:
            fail("Steam did not provide its Workshop interface")
        wait_for_logon(lib)
        if args.check:
            print("Connected to Steam and logged on. Item id: %s. Tags that would be set: %s" % (item or "none yet", ", ".join(TAGS)))
            return

        created = False
        if not item:
            print("Creating a new Workshop item...")
            call = lib.SteamAPI_ISteamUGC_CreateItem(ugc, APP_ID, 0)
            made = wait(lib, utils, call, CreateItemResult(), CREATE_ITEM_RESULT)
            if made.result != 1:
                fail("Steam refused to create the item: " + result_name(made.result))
            item, created = made.file_id, True
            with open(ID_FILE, "w") as f:
                f.write("%d\n" % item)
            print("Created item %d (saved to workshop_id)" % item)

        strings = (ctypes.c_char_p * len(TAGS))(*[tag.encode("utf-8") for tag in TAGS])
        tags = StringArray(strings, len(TAGS))
        visibility = args.visibility or ("hidden" if created else None)

        def submit():
            handle = lib.SteamAPI_ISteamUGC_StartItemUpdate(ugc, APP_ID, item)
            sent = []
            if not args.tags_only:
                if created or args.text:
                    lib.SteamAPI_ISteamUGC_SetItemTitle(ugc, handle, TITLE.encode("utf-8"))
                    lib.SteamAPI_ISteamUGC_SetItemDescription(ugc, handle, DESCRIPTION.encode("utf-8"))
                    sent.append("title and description")
                lib.SteamAPI_ISteamUGC_SetItemContent(ugc, handle, CONTENT.encode("utf-8"))
                sent.append("content")
                if os.path.isfile(PREVIEW):
                    lib.SteamAPI_ISteamUGC_SetItemPreview(ugc, handle, PREVIEW.encode("utf-8"))
                    sent.append("preview image")
                if visibility:
                    lib.SteamAPI_ISteamUGC_SetItemVisibility(ugc, handle, VISIBILITY[visibility])
                    sent.append("visibility " + visibility)
            if not lib.SteamAPI_ISteamUGC_SetItemTags(ugc, handle, ctypes.byref(tags), False):
                fail("Steam rejected the tag list: " + ", ".join(TAGS))
            sent.append("tags " + ", ".join(TAGS))
            print("Uploading: " + "; ".join(sent))

            def progress():
                done, total = ctypes.c_uint64(0), ctypes.c_uint64(0)
                status = lib.SteamAPI_ISteamUGC_GetItemUpdateProgress(ugc, handle, ctypes.byref(done), ctypes.byref(total))
                return UPDATE_STATUS.get(status)

            call = lib.SteamAPI_ISteamUGC_SubmitItemUpdate(ugc, handle, args.note.encode("utf-8"))
            return wait(lib, utils, call, SubmitItemUpdateResult(), SUBMIT_ITEM_UPDATE_RESULT, progress)

        outcome = submit()
        for _ in range(2):
            if outcome.result != 21:
                break
            # NotLoggedOn: this connection was not ready yet. Give it a moment and send it again.
            print("  Steam was not ready (NotLoggedOn); trying again in a few seconds")
            pump(lib, 5)
            outcome = submit()
        if outcome.result != 1:
            fail("Steam refused the update: " + result_name(outcome.result))
        print("Done: https://steamcommunity.com/sharedfiles/filedetails/?id=%d" % item)
        if outcome.needs_agreement:
            print("The item stays hidden from others until you accept the Workshop agreement: "
                  "https://steamcommunity.com/sharedfiles/workshoplegalagreement")
    finally:
        lib.SteamAPI_Shutdown()


if __name__ == "__main__":
    main()
