#!/usr/bin/env bash
# Builds the LocalFM TV app for LG webOS: packaging/webos/build.sh [out-dir]
# The app is the same web client LocalFM serves (src/LocalFM.Vibe/Web/client); packaged, it asks once for the
# computer's address. Needs Node.js: ares-package comes from @webos-tools/cli via npx. Install it on a TV in
# Developer Mode (docs/lg-tv.md).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
out=${1:-${OUT:-${TMPDIR:-/tmp}/localfm-package}/webos}
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/Directory.Build.props" | head -1)

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
cp -r "$repo/src/LocalFM.Vibe/Web/client/." "$stage/"
cp "$repo/src/LocalFM.UI/Assets/icon.svg" "$stage/icon.svg"
cp "$here/icon.png" "$here/largeIcon.png" "$stage/"
sed "s/@VERSION@/$version/" "$here/appinfo.json.in" > "$stage/appinfo.json"

mkdir -p "$out"
npx --yes -p @webos-tools/cli@3.2.6 ares-package --no-minify "$stage" -o "$out" > /dev/null
ls "$out"/org.localfm.tv_"$version"_all.ipk
