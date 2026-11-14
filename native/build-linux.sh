#!/usr/bin/env bash
# Configures and builds liblocalfm_native with CMake. Output: native/out/<rid>/
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
gen=()
command -v ninja >/dev/null && gen=(-G Ninja)
cmake -S "$here" -B "$here/build" "${gen[@]}" -DCMAKE_BUILD_TYPE=RelWithDebInfo "$@"
cmake --build "$here/build"
