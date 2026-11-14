#!/usr/bin/env bash
# Builds a small, self-contained test library for UI reviews: no real music, nothing private.
#   make-test-library.sh <dir>
# Creates <dir>/music with:
#   - "Visual Test/Signal Bloom": 60 s of layered tones + pink noise (moves every visualizer mode) with a cover
#   - "Karaoke Test/Neon Words": 40 s with word-synced (enhanced LRC) lyrics in its tags
#   - "Estimate Test/Line Timed": 40 s with line-synced lyrics only (exercises the word-timing estimate)
#   - "Messy Names/...": YouTube-rip style names (Tidy up suggestions, long titles, " - Topic" artists)
#   - "Copy Test": two copies of one album (1987 and 1998), for the merge hint and the move panel
# Then scans it into <dir>/home (LOCALFM_HOME) with the Debug build.
set -euo pipefail
dir=${1:?usage: make-test-library.sh <dir>}
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
bin="$repo/src/LocalFM.UI/bin/Debug/net10.0/LocalFM"
mkdir -p "$dir/music/visual" "$dir/music/karaoke" "$dir/music/estimate" "$dir/music/messy" "$dir/home"
cd "$dir"

if [ ! -f music/visual/v.flac ]; then
  ffmpeg -loglevel error -y -f lavfi -i "gradients=s=600x600:c0=0x2b6cb0:c1=0x63e6be:x0=0:y0=0:x1=600:y1=600:d=1" -frames:v 1 cover.png
  ffmpeg -loglevel error -y \
    -f lavfi -i "aevalsrc=0.35*sin(2*PI*55*t)*pow(0.5+0.5*sin(2*PI*2*t)\,4)+0.18*sin(2*PI*220*t)*(0.6+0.4*sin(2*PI*0.5*t))+0.12*sin(2*PI*660*t+3*sin(2*PI*1.3*t))+0.06*sin(2*PI*2500*t)*(0.5+0.5*sin(2*PI*3*t)):s=44100:d=60" \
    -f lavfi -i "anoisesrc=c=pink:a=0.06:d=60:r=44100" -i cover.png \
    -filter_complex "[0][1]amix=inputs=2:normalize=0[a]" -map "[a]" -map 2 -c:v png -disposition:v attached_pic \
    -metadata title="Signal Bloom" -metadata artist="Test Band" -metadata album="Visual Test" music/visual/v.flac
fi

if [ ! -f music/karaoke/a.flac ]; then
  python3 - <<'EOF' > elrc.txt
words = "Under the neon lights tonight we ride the endless highway home".split()
t, out = 0.3, []
for n in range(10):
    ws = words[(n * 3) % len(words):][:5] or words[:5]
    line, wt = f"[{int(t//60):02d}:{t%60:05.2f}]", t
    for w in ws:
        line += f"<{int(wt//60):02d}:{wt%60:05.2f}>{w} "; wt += 0.5
    out.append(line.rstrip() + f"<{int(wt//60):02d}:{wt%60:05.2f}>"); t = wt + 0.6
print("\n".join(out))
EOF
  ffmpeg -loglevel error -y -f lavfi -i "sine=f=220:d=40" -metadata title="Neon Words" -metadata artist="Test Band" \
    -metadata album="Karaoke Test" -metadata lyrics="$(cat elrc.txt)" music/karaoke/a.flac
fi

if [ ! -f music/estimate/b.flac ]; then
  python3 -c '
w = "Under the neon lights tonight we ride the endless highway home".split() * 3
print("\n".join(f"[00:{2*i+0.3:05.2f}]" + " ".join(w[i*2:i*2+6]) for i in range(15)))' > lrc.txt
  ffmpeg -loglevel error -y -f lavfi -i "sine=f=330:d=40" -metadata title="Line Timed" -metadata artist="Test Band" \
    -metadata album="Estimate Test" -metadata lyrics="$(cat lrc.txt)" music/estimate/b.flac
fi

if [ ! -f "music/messy/m1.mp3" ]; then
  ffmpeg -loglevel error -y -f lavfi -i "sine=f=440:d=8" -metadata title="Example Band - A Really Long Song Title That Keeps Going (Official Music Video) [dQw4w9WgXcQ]" \
    -metadata artist="Example Band - Topic" -metadata album="An Album Name Long Enough To Need Trimming In Narrow Places" music/messy/m1.mp3
  ffmpeg -loglevel error -y -f lavfi -i "sine=f=550:d=8" -metadata title="Second+Song+Here" \
    -metadata artist="ExampleBandVEVO" -metadata album="An Album Name Long Enough To Need Trimming In Narrow Places" music/messy/m2.mp3
fi

# Two copies of one album (songs ripped one at a time, different years): the album page offers to merge them.
if [ ! -f "music/copies/c1.mp3" ]; then
  mkdir -p music/copies
  ffmpeg -loglevel error -y -f lavfi -i "sine=f=600:d=8" -metadata title="First Copy Song" -metadata artist="Test Band" \
    -metadata album="Copy Test" -metadata track=7 -metadata date=1987 music/copies/c1.mp3
  ffmpeg -loglevel error -y -f lavfi -i "sine=f=650:d=8" -metadata title="Second Copy Song" -metadata artist="Test Band" \
    -metadata album="Copy Test" -metadata track=12 -metadata date=1998 music/copies/c2.mp3
fi

if [ ! -f home/library.db ]; then
  LOCALFM_OUTPUT=null LOCALFM_HOME="$dir/home" LOCALFM_SNAPSHOT="$dir/scan.png" LOCALFM_SNAPSHOT_WAIT=4000 \
    timeout 90 "$bin" --add-folder "$dir/music" >/dev/null 2>&1 || true
fi
echo "test library: LOCALFM_HOME=$dir/home"
