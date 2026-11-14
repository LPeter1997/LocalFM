# localfm_native: output ring + miniaudio device + FFmpeg decoder/prober.
#
#   cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build native/build
#
# The library lands in native/out/<rid>/, where LocalFM.Native picks it up.
# FFmpeg headers are vendored (8.1). At link time:
#   Linux:          the system's FFmpeg 8.x shared libraries (runtime packages are enough, no -devel)
#   Windows/macOS:  -DFFMPEG_ROOT=<shared FFmpeg 8.x build with lib/> (import libs / dylibs)
cmake_minimum_required(VERSION 3.20)
project(localfm_native C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(FFMPEG_ROOT "" CACHE PATH "Root of a shared FFmpeg 8.x build (lib/), required on Windows/macOS")

# Runtime identifier matching .NET's naming.
string(TOLOWER "${CMAKE_SYSTEM_PROCESSOR}" _arch)
if(_arch MATCHES "^(x86_64|amd64)$")
  set(_arch x64)
elseif(_arch MATCHES "^(aarch64|arm64)$")
  set(_arch arm64)
endif()
if(WIN32)
  set(LFM_RID "win-${_arch}")
elseif(APPLE)
  set(LFM_RID "osx")
else()
  set(LFM_RID "linux-${_arch}")
endif()

add_library(localfm_native SHARED
  localfm_native/output.c
  localfm_native/ffmpeg.c)
target_include_directories(localfm_native PRIVATE third_party/ffmpeg/include)

# FFmpeg 8.x: libavformat 62, libavcodec 62, libavutil 60, libswresample 6.
set(_ff_libs avformat:62 avcodec:62 avutil:60 swresample:6)
foreach(_entry IN LISTS _ff_libs)
  string(REPLACE ":" ";" _parts "${_entry}")
  list(GET _parts 0 _name)
  list(GET _parts 1 _major)
  find_library(FF_${_name}
    NAMES ${_name} lib${_name}.so.${_major} lib${_name}.${_major}.dylib
    HINTS "${FFMPEG_ROOT}/lib" "${FFMPEG_ROOT}/bin"
    REQUIRED)
  target_link_libraries(localfm_native PRIVATE "${FF_${_name}}")
endforeach()

if(MSVC)
  target_compile_options(localfm_native PRIVATE /W3 /experimental:c11atomics)
else()
  target_compile_options(localfm_native PRIVATE -Wall -Wextra -Wno-unused-parameter)
  target_compile_definitions(localfm_native PRIVATE _GNU_SOURCE)
endif()

if(UNIX AND NOT APPLE)
  target_link_libraries(localfm_native PRIVATE pthread m dl)
elseif(APPLE)
  # miniaudio uses CoreAudio through the runtime linker; these frameworks make that explicit.
  target_link_libraries(localfm_native PRIVATE "-framework CoreFoundation" "-framework CoreAudio" "-framework AudioToolbox")
endif()

set(_out "${CMAKE_CURRENT_SOURCE_DIR}/out/${LFM_RID}")
set_target_properties(localfm_native PROPERTIES
  LIBRARY_OUTPUT_DIRECTORY "${_out}"
  RUNTIME_OUTPUT_DIRECTORY "${_out}"
  ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")
foreach(_cfg Debug Release RelWithDebInfo MinSizeRel)
  string(TOUPPER ${_cfg} _CFG)
  set_target_properties(localfm_native PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY_${_CFG} "${_out}"
    RUNTIME_OUTPUT_DIRECTORY_${_CFG} "${_out}")
endforeach()
