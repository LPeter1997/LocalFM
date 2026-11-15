#!/bin/sh
# Builds the release for this machine's OS (Native AOT does not cross-compile between operating systems;
# package.ps1 is the Windows side). Ported from CheeseGit; docs/release.md has the whole flow.
#   ./package.sh <version>        RID=linux-arm64|osx-x64 OUT=dir NOTES=release-notes.md PREVIOUS=dir FFMPEG_PREFIX=dir
#
# Native code first: localfm_native and the audio-only FFmpeg it ships with go into native/out/<rid>/.
# FFMPEG_PREFIX names an FFmpeg built by native/ffmpeg/build.sh (CI caches it); without one, an existing
# native/out/<rid>/ with the bundled FFmpeg is reused, and otherwise FFmpeg is built here (a minute or so).
#
# PREVIOUS holds the last release of this channel (vpk download github): vpk then builds a delta package
# against it, so updates download only what changed.
#
# Linux: the Velopack AppImage and .nupkg (in-app updates), then the distro tarball, built with the updater
#   compiled out, and from it the .deb and .rpm (nfpm, or its container through podman/docker), plus the
#   Flatpak manifest, PKGBUILD and winget manifests with the checksums filled in (packaging/fill.sh).
# macOS: the Velopack .pkg, portable zip and .nupkg, and a .dmg of the app bundle.
#
# Signing is configured through vpk's own environment variables, so no secret passes through here:
# VPK_SIGN_APP_IDENTITY, VPK_SIGN_INSTALL_IDENTITY, VPK_NOTARY_PROFILE (macOS; the .dmg is notarized with the
# same profile). The Velopack channel is the RID, so every architecture can share one GitHub release.
set -e
cd "$(dirname "$0")"
here=$(pwd)
version=$1
[ -n "$version" ] || { echo "usage: package.sh <version>" >&2; exit 2; }
case "$(uname -s)" in
    Linux) os=linux ;;
    Darwin) os=osx ;;
    *) echo "package.sh runs on Linux and macOS; use package.ps1 on Windows" >&2; exit 1 ;;
esac
case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) echo "unknown architecture: $(uname -m)" >&2; exit 1 ;;
esac
rid=${RID:-$os-$arch}
arch=${rid#*-}
out=${OUT:-${TMPDIR:-/tmp}/localfm-package}
releases="$out/releases"
rm -rf "$out"
mkdir -p "$releases"
[ -n "$PREVIOUS" ] && cp -R "$PREVIOUS"/. "$releases"/
notes=""
[ -n "$NOTES" ] && notes="--releaseNotes $NOTES"

# awk does the formatting under LC_ALL=C: a locale with a decimal comma breaks printf.
size() { [ -e "$2" ] && du -skc "$2" | tail -1 | LC_ALL=C awk -v n="$1" '{ printf "%-40s %6.1f MB\n", n, $1 * 1024 / 1000000 }'; true; }

# Native code: the library and the FFmpeg it ships with.
if [ -n "$FFMPEG_PREFIX" ]; then
    native/build.sh "$rid" "$FFMPEG_PREFIX" > "$out/native.log" 2>&1 || { cat "$out/native.log"; exit 1; }
elif [ ! -e "native/out/$rid/FFmpeg-COPYING.LGPLv2.1.txt" ]; then
    echo "building FFmpeg for $rid (set FFMPEG_PREFIX to reuse one)"
    native/ffmpeg/build.sh "$rid" "$out/ffmpeg" > "$out/ffmpeg.log" 2>&1 || { tail -50 "$out/ffmpeg.log"; exit 1; }
    native/build.sh "$rid" "$out/ffmpeg" > "$out/native.log" 2>&1 || { cat "$out/native.log"; exit 1; }
fi

publish() { # <dir> [msbuild args]
    dir=$1
    shift
    dotnet publish src/LocalFM.UI -c Release -r "$rid" -o "$dir" -p:Version="$version" --nologo -v q "$@"
}

dotnet tool restore > /dev/null
publish "$out/publish"
size "publish folder ($rid)" "$out/publish"
size "  LocalFM (AOT binary)" "$out/publish/LocalFM"

if [ "$os" = linux ]; then
    # shellcheck disable=SC2086 # $notes is empty or two words
    dotnet vpk pack --packId LocalFM --packVersion "$version" --packTitle LocalFM --packAuthors "Peter Lenkefi" \
        --packDir "$out/publish" --mainExe LocalFM --icon src/LocalFM.UI/Assets/icon.png --categories AudioVideo \
        --runtime "$rid" --channel "$rid" --outputDir "$releases" $notes > "$out/vpk.log" 2>&1 \
        || { cat "$out/vpk.log"; exit 1; }

    # The distro tarball: the system's package manager updates it, so the updater is compiled out.
    id=io.github.LPeter1997.LocalFM
    top="$out/distro/localfm-$version"
    publish "$top/lib" -p:LocalFMUpdater=false
    mkdir -p "$top/share/applications" "$top/share/metainfo" "$top/share/icons/hicolor/512x512/apps" "$top/share/licenses/localfm"
    cp packaging/linux/$id.desktop "$top/share/applications/"
    sed "s|<!-- @RELEASES@ -->|<release version=\"$version\" date=\"$(date -u +%Y-%m-%d)\" />|" \
        packaging/linux/$id.metainfo.xml > "$top/share/metainfo/$id.metainfo.xml"
    cp src/LocalFM.UI/Assets/icon.png "$top/share/icons/hicolor/512x512/apps/$id.png"
    cp LICENSE "$top/share/licenses/localfm/"
    cp "native/out/$rid/FFmpeg-COPYING.LGPLv2.1.txt" "$top/share/licenses/localfm/"
    tarball="$releases/localfm-$version-linux-$arch.tar.gz"
    tar -C "$out/distro" -czf "$tarball" "localfm-$version"

    # deb and rpm: nfpm itself, else its container image.
    case "$arch" in x64) nfpm_arch=amd64 ;; *) nfpm_arch=arm64 ;; esac
    nfpm_run() {
        if command -v nfpm > /dev/null; then
            (cd "$top" && NFPM_ARCH=$nfpm_arch VERSION=$version nfpm "$@" --config "$here/packaging/linux/nfpm.yaml")
        elif engine=$(command -v podman || command -v docker); then
            "$engine" run --rm -v "$here/packaging:/packaging:ro,z" -v "$out:$out:z" -w "$top" \
                -e NFPM_ARCH=$nfpm_arch -e VERSION="$version" \
                docker.io/goreleaser/nfpm:latest "$@" --config /packaging/linux/nfpm.yaml
        else
            return 127
        fi
    }
    for format in deb rpm; do
        nfpm_run package --packager $format --target "$releases/" > "$out/nfpm-$format.log" 2>&1 \
            || echo "skipped .$format: no nfpm (or podman/docker), or it failed: $out/nfpm-$format.log"
    done

    packaging/fill.sh "$version" "$releases"

    for f in "$releases"/*.AppImage "$releases"/*-full.nupkg "$tarball" "$releases"/*.deb "$releases"/*.rpm; do
        [ -e "$f" ] && size "$(basename "$f")" "$f"
    done
else
    # shellcheck disable=SC2086
    dotnet vpk pack --packId LocalFM --packVersion "$version" --packTitle LocalFM --packAuthors "Peter Lenkefi" \
        --packDir "$out/publish" --mainExe LocalFM --icon src/LocalFM.UI/obj/LocalFM.icns \
        --bundleId io.github.LPeter1997.LocalFM --runtime "$rid" --channel "$rid" --outputDir "$releases" $notes \
        > "$out/vpk.log" 2>&1 || { cat "$out/vpk.log"; exit 1; }

    # The .dmg holds the same (signed) bundle as the portable zip, with an Applications link to drag it to.
    staging="$out/dmg"
    mkdir -p "$staging"
    ditto -x -k "$releases/LocalFM-$rid-Portable.zip" "$staging"
    ln -s /Applications "$staging/Applications"
    dmg="$releases/LocalFM-$version-$rid.dmg"
    hdiutil create -volname LocalFM -srcfolder "$staging" -ov -format UDZO "$dmg" > /dev/null
    if [ -n "$VPK_SIGN_APP_IDENTITY" ]; then
        codesign --sign "$VPK_SIGN_APP_IDENTITY" --timestamp "$dmg"
    fi
    if [ -n "$VPK_NOTARY_PROFILE" ]; then
        xcrun notarytool submit "$dmg" --keychain-profile "$VPK_NOTARY_PROFILE" --wait
        xcrun stapler staple "$dmg"
    fi

    for f in "$releases"/*.pkg "$releases"/*-Portable.zip "$dmg" "$releases"/*-full.nupkg; do
        [ -e "$f" ] && size "$(basename "$f")" "$f"
    done
fi
echo "output: $releases"
