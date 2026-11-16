#!/usr/bin/env bash
# Screenshots LocalFM on a running Android emulator or phone (adb), in the states snapshot-all.sh uses.
#   snapshot-android.sh <out-dir> [apk]
# The apk defaults to the Debug build (src/LocalFM.Android). The device's Music folder is the library: push the
# test library first (adb push <dir>/music/. /sdcard/Music/, from make-test-library.sh).
# Each shot starts the app fresh with LOCALFM_SNAPSHOT_* intent extras, waits for "snapshot ready" in logcat
# (SnapshotScript has put the app in that state), then takes the screen.
set -uo pipefail
out=${1:?usage: snapshot-android.sh <out-dir> [apk]}
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
apk=${2:-$repo/src/LocalFM.Android/bin/Debug/net10.0-android/io.github.lpeter1997.localfm-Signed.apk}
adb=${ADB:-$HOME/Android/Sdk/platform-tools/adb}
pkg=io.github.lpeter1997.localfm
mkdir -p "$out"

"$adb" install -r "$apk" > /dev/null || { echo "install failed"; exit 1; }
"$adb" shell pm grant $pkg android.permission.READ_MEDIA_AUDIO 2> /dev/null
"$adb" shell pm grant $pkg android.permission.POST_NOTIFICATIONS 2> /dev/null
activity=$("$adb" shell cmd package resolve-activity --brief $pkg | tail -1 | tr -d '\r')

shot() { # name, then KEY=value settings
  local name=$1; shift
  local extras=(--es LOCALFM_SNAPSHOT 1)
  for kv in "$@"; do extras+=(--es "${kv%%=*}" "${kv#*=}"); done
  "$adb" shell am force-stop $pkg
  "$adb" logcat -c
  "$adb" shell am start -n "$activity" "${extras[@]}" > /dev/null
  for _ in $(seq 120); do
    "$adb" logcat -d -s LocalFM:I | grep -q "snapshot ready" && break
    sleep 0.5
  done
  "$adb" exec-out screencap -p > "$out/$name.png"
  [ -s "$out/$name.png" ] && echo "$out/$name.png" || echo "FAILED $name"
}

for p in albums artists tracks playlists queue stats sound sources album; do
  shot "phone-$p" LOCALFM_SNAPSHOT_PAGE=$p
done
shot phone-playing LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 LOCALFM_SNAPSHOT_WAIT=3000
shot phone-np      LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 LOCALFM_SNAPSHOT_NOWPLAYING=Spectrum LOCALFM_SNAPSHOT_WAIT=3000
shot phone-lyrics  LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 LOCALFM_SNAPSHOT_LYRICS=1 LOCALFM_SNAPSHOT_WAIT=4000
shot phone-vibe    LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_VIBE=1
"$adb" shell am force-stop $pkg
