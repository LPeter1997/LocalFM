#!/usr/bin/env bash
# Screenshots every screen of LocalFM in the states a review needs, without touching the screen or speakers.
#   snapshot-all.sh <LOCALFM_HOME> <out-dir> [binary]
# binary defaults to the Debug build; pass the AOT binary to check the release build.
# Output: <out-dir>/<name>.png, one line per shot. Silent output (LOCALFM_OUTPUT=null) still runs the
# audio clock, so playback position, lyrics and visualizers move as in real use.
set -uo pipefail
home=${1:?usage: snapshot-all.sh <LOCALFM_HOME> <out-dir> [binary]}
out=${2:?usage: snapshot-all.sh <LOCALFM_HOME> <out-dir> [binary]}
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
bin=${3:-$repo/src/LocalFM.UI/bin/Debug/net10.0/LocalFM}
mkdir -p "$out"

shot() { # name, then extra env assignments
  local name=$1; shift
  env LOCALFM_OUTPUT=null LOCALFM_HOME="$home" LOCALFM_SNAPSHOT="$out/$name.png" "$@" timeout 90 "$bin" >/dev/null 2>&1
  [ -f "$out/$name.png" ] && echo "$out/$name.png" || echo "FAILED $name"
}

# Every page, idle.
for p in albums artists tracks playlists queue stats tidy sound sources album edit match; do
  shot "$p" LOCALFM_SNAPSHOT_PAGE=$p
done
# Playing: player bar, now-playing view in each visualizer mode, lyrics panel, miniplayer.
shot playing      LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 LOCALFM_SNAPSHOT_WAIT=3000
for m in Spectrum Mirror Wave Off; do
  shot "np-$m"    LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 LOCALFM_SNAPSHOT_NOWPLAYING=$m LOCALFM_SNAPSHOT_WAIT=3000
done
shot lyrics       LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 LOCALFM_SNAPSHOT_LYRICS=1 LOCALFM_SNAPSHOT_WAIT=4000
shot mini         LOCALFM_SNAPSHOT_PAGE=mini LOCALFM_SNAPSHOT_PLAY=1
# Smallest allowed window (MinWidth 820 x MinHeight 520): truncation, wrapping, overlap.
for p in albums queue sound stats; do
  shot "narrow-$p" LOCALFM_SNAPSHOT_PAGE=$p LOCALFM_SNAPSHOT_WIDTH=820 LOCALFM_SNAPSHOT_HEIGHT=520
done
shot narrow-lyrics LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 LOCALFM_SNAPSHOT_LYRICS=1 LOCALFM_SNAPSHOT_WIDTH=820 LOCALFM_SNAPSHOT_HEIGHT=520
# Long pages scrolled down.
shot stats-scrolled LOCALFM_SNAPSHOT_PAGE=stats LOCALFM_SNAPSHOT_SCROLL=600
shot sound-scrolled LOCALFM_SNAPSHOT_PAGE=sound LOCALFM_SNAPSHOT_SCROLL=600
# Phone-sized (the compact layout Android uses; snapshot-android.sh shoots the real thing).
phone=(LOCALFM_SNAPSHOT_WIDTH=412 LOCALFM_SNAPSHOT_HEIGHT=860)
for p in albums artists tracks playlists queue stats sound sources album more; do
  shot "phone-$p" LOCALFM_SNAPSHOT_PAGE=$p "${phone[@]}"
done
shot phone-playing LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 "${phone[@]}"
shot phone-np      LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 LOCALFM_SNAPSHOT_NOWPLAYING=Spectrum LOCALFM_SNAPSHOT_WAIT=3000 "${phone[@]}"
shot phone-lyrics  LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_PLAY=1 LOCALFM_SNAPSHOT_LYRICS=1 LOCALFM_SNAPSHOT_WAIT=4000 "${phone[@]}"
shot phone-vibe    LOCALFM_SNAPSHOT_PAGE=albums LOCALFM_SNAPSHOT_VIBE=1 "${phone[@]}"
shot phone-artist  "LOCALFM_SNAPSHOT_PAGE=artist:Example Band" "${phone[@]}"
