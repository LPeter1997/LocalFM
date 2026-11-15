#!/usr/bin/env bash
# Builds the audio-only, LGPL FFmpeg that LocalFM ships (docs/release.md): shared libraries only (avformat, avcodec,
# avutil, swresample), with the demuxers, decoders and parsers for the formats the scanner accepts
# (Scanner.AudioExtensions) and nothing else: no programs, network, devices, filters, video or external libraries.
#
#   native/ffmpeg/build.sh <rid> <prefix>        e.g. linux-x64 /tmp/ffmpeg-linux-x64
#
# Linux and macOS build natively. Windows runs this in MSYS2 with the MSVC environment loaded (cl.exe on PATH):
# --toolchain=msvc gives DLLs plus .lib import libraries for the MSVC-built localfm_native.
# The libraries find each other next to the app: $ORIGIN (Linux) and @rpath (macOS) are built in.
# SOURCE_DIR=<dir> reuses an unpacked source tree; otherwise the pinned release is downloaded and checked.
set -euo pipefail

version=8.1.3
sha256=7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3 # signed by FFmpeg's release key FCF986EA…D67658D8

rid=${1:?usage: build.sh <rid> <prefix>}
prefix=${2:?usage: build.sh <rid> <prefix>}
mkdir -p "$prefix"
prefix=$(cd "$prefix" && pwd)
work=${WORK_DIR:-${TMPDIR:-/tmp}/localfm-ffmpeg-$rid}
mkdir -p "$work"

if [ -n "${SOURCE_DIR:-}" ]; then
    src=$SOURCE_DIR
else
    tarball="$work/ffmpeg-$version.tar.xz"
    [ -f "$tarball" ] || curl -fsSL "https://ffmpeg.org/releases/ffmpeg-$version.tar.xz" -o "$tarball"
    actual=$( (sha256sum "$tarball" 2>/dev/null || shasum -a 256 "$tarball") | cut -d' ' -f1)
    [ "$actual" = "$sha256" ] || { echo "ffmpeg-$version.tar.xz: sha256 $actual, expected $sha256" >&2; rm -f "$tarball"; exit 1; }
    src="$work/ffmpeg-$version"
    rm -rf "$src"
    tar -C "$work" -xJf "$tarball"
fi

# What LocalFM plays: the containers and codecs behind .mp3 .flac .ogg .oga .opus .m4a .m4b .mp4 .aac .alac .wav
# .wave .aif .aiff .aifc .wv .ape .mpc .wma .mka .dsf .dff .tta .spx .webm (and what those commonly carry).
demuxers=mp3,flac,ogg,mov,aac,caf,wav,w64,aiff,wv,ape,mpc,mpc8,asf,matroska,dsf,iff,tta
decoders=mp1,mp1float,mp2,mp2float,mp3,mp3float,mp3on4,mp3on4float,flac,vorbis,opus,speex,aac,aac_fixed,aac_latm,alac
decoders=$decoders,pcm_s8,pcm_u8,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be
decoders=$decoders,pcm_f64le,pcm_f64be,pcm_alaw,pcm_mulaw,pcm_s16le_planar,pcm_s24le_planar,pcm_s32le_planar
decoders=$decoders,adpcm_ms,adpcm_ima_wav,gsm_ms,wavpack,ape,mpc7,mpc8,wmav1,wmav2,wmapro,wmalossless,wmavoice
decoders=$decoders,dsd_lsbf,dsd_msbf,dsd_lsbf_planar,dsd_msbf_planar,tta,ac3,eac3,dca,mlp,truehd
parsers=mpegaudio,flac,aac,aac_latm,vorbis,opus,ac3,dca,mlp

args=(
    --prefix="$prefix"
    --disable-everything --disable-autodetect --disable-programs --disable-doc --disable-network
    --disable-avdevice --disable-avfilter --disable-swscale
    --enable-shared --disable-static --enable-pic
    --enable-avformat --enable-avcodec --enable-swresample
    --enable-protocol=file
    --enable-demuxer="$demuxers" --enable-decoder="$decoders" --enable-parser="$parsers"
)

case "$rid" in
    linux-*)
        # Each library finds its siblings in its own folder (the app's). configure evaluates this once, make twice
        # (library.mak) and the recipe's shell once, leaving $ORIGIN.
        args+=(--extra-ldsoflags='-Wl,-rpath,\\\$\$\$\$ORIGIN')
        ;;
    osx-*)
        args+=(--install-name-dir='@rpath')
        [ "$rid" = osx-arm64 ] && args+=(--arch=arm64 --extra-cflags=-mmacosx-version-min=12.0 --extra-ldflags=-mmacosx-version-min=12.0)
        [ "$rid" = osx-x64 ] && args+=(--arch=x86_64 --extra-cflags=-mmacosx-version-min=12.0 --extra-ldflags=-mmacosx-version-min=12.0)
        ;;
    win-x64) args+=(--toolchain=msvc --target-os=win64 --arch=x86_64) ;;
    # MSVC's ARM assembler cannot take FFmpeg's GNU-syntax assembly without gas-preprocessor.pl; the C versions
    # decode audio more than fast enough.
    win-arm64) args+=(--toolchain=msvc --target-os=win64 --arch=aarch64 --disable-asm) ;;
    *) echo "unknown rid: $rid" >&2; exit 2 ;;
esac

# Hand-written assembly speeds up some decoders a little; without nasm (x86) the C versions are used.
case "$rid" in
    *-x64) command -v nasm > /dev/null || { echo "nasm not found: building without x86 assembly" >&2; args+=(--disable-x86asm); } ;;
esac

build="$work/build"
rm -rf "$build"
mkdir -p "$build"
cd "$build"
"$src/configure" "${args[@]}" > "$work/configure.log" 2>&1 || { tail -40 "$work/configure.log"; cat ffbuild/config.log 2>/dev/null | tail -40; exit 1; }
jobs=$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4)
make -j"$jobs" > "$work/make.log" 2>&1 || { tail -60 "$work/make.log"; exit 1; }
make install > "$work/install.log" 2>&1 || { tail -40 "$work/install.log"; exit 1; }
cp "$src/COPYING.LGPLv2.1" "$prefix/"
echo "FFmpeg $version ($rid) installed in $prefix"
