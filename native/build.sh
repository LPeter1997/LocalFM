#!/usr/bin/env bash
# Builds localfm_native against the bundled FFmpeg and puts everything the app needs at run time into
# native/out/<rid>/ (LocalFM.Native copies that folder next to the app): the library, FFmpeg's shared libraries
# and FFmpeg's licence. Used by the package scripts and CI on every OS (Windows: Git Bash with CMake and Visual
# Studio; the MSVC environment is not needed, CMake finds the compiler itself).
#
#   native/build.sh <rid> <ffmpeg-prefix>        the prefix from native/ffmpeg/build.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
rid=${1:?usage: build.sh <rid> <ffmpeg-prefix>}
ffmpeg=$(cd "${2:?usage: build.sh <rid> <ffmpeg-prefix>}" && pwd)
out="$here/out/$rid"
build="$here/build-$rid"

args=(-S "$here" -B "$build" -DCMAKE_BUILD_TYPE=Release -DFFMPEG_ROOT="$ffmpeg")
case "$rid" in
    win-x64) args+=(-A x64) ;;
    win-arm64) args+=(-A ARM64) ;;
    osx-x64) args+=(-DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0) ;;
    osx-arm64) args+=(-DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0) ;;
    linux-*) command -v ninja > /dev/null && args+=(-G Ninja) ;;
    android-*)
        ndk=${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:?set ANDROID_NDK_HOME to the NDK}}
        case "$rid" in android-arm64) abi=arm64-v8a ;; android-x64) abi=x86_64 ;; *) echo "unknown rid: $rid" >&2; exit 2 ;; esac
        args+=(-DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" -DANDROID_ABI=$abi
               -DANDROID_PLATFORM=android-${ANDROID_API:-26})
        command -v ninja > /dev/null && args+=(-G Ninja) ;;
    *) echo "unknown rid: $rid" >&2; exit 2 ;;
esac
# CMake names the output folder after the machine it builds for; a cross build (osx-x64 on arm64) is told.
args+=(-DLFM_RID_OVERRIDE="$rid")

rm -rf "$out"
cmake "${args[@]}"
cmake --build "$build" --config Release

# FFmpeg's runtime files, under the names the loader asks for.
case "$rid" in
    android-*) cp "$ffmpeg"/lib/lib*.so "$out/" ;; # unversioned (native/ffmpeg/build.sh)
    linux-*) for f in "$ffmpeg"/lib/lib*.so.[0-9]*; do
                 name=$(basename "$f")
                 [[ "$name" =~ ^lib[a-z]+\.so\.[0-9]+$ ]] && cp -L "$f" "$out/$name"
             done ;;
    osx-*) for f in "$ffmpeg"/lib/lib*.[0-9]*.dylib; do
               name=$(basename "$f")
               [[ "$name" =~ ^lib[a-z]+\.[0-9]+\.dylib$ ]] && cp -L "$f" "$out/$name"
           done ;;
    win-*) cp "$ffmpeg"/bin/*.dll "$out/" ;;
esac
cp "$ffmpeg/COPYING.LGPLv2.1" "$out/FFmpeg-COPYING.LGPLv2.1.txt"
ls -la "$out"
