# Packaging, updates and releases

The flow is CheeseGit's (`~/Development/CheeseGit/docs/release.md`), adapted to LocalFM's native code and to
"nothing stops the music unasked".

## Decisions

- **Releases live on GitHub** (`github.com/LPeter1997/LocalFM`, public): the in-app updater reads them with no token,
  and GitHub Actions has runners for every RID. The working remote is Codeberg; GitHub gets `master` for CI and
  releases.
- **The Velopack channel is the RID** (`win-x64`, `osx-arm64`, `linux-x64`, …): every architecture shares one release
  without file name clashes, and an installed copy keeps updating from its own channel.
- **App id `io.github.LPeter1997.LocalFM`** for the desktop file, metainfo, Flatpak, icons, the X11 WM_CLASS, the
  MPRIS `DesktopEntry` and the macOS bundle id.
- **FFmpeg is our own audio-only LGPL build** (`native/ffmpeg/build.sh`, FFmpeg 8.1.3, sha256-pinned): shared
  avformat, avcodec, avutil and swresample with only the demuxers, decoders and parsers for the formats the scanner
  accepts; no programs, network, devices, filters, video or external libraries. Every package carries it next to the
  app (`$ORIGIN` / `@rpath`), with its licence (`FFmpeg-COPYING.LGPLv2.1.txt`). Ubuntu 22.04, the glibc floor, ships
  FFmpeg 4.4, so the system's is never used by packages. A Linux dev build without `native/out/<rid>/` still builds
  against the system's FFmpeg (`native/build-linux.sh`).
- **ICU stays** (no invariant globalization): sorting is culture-aware. The deb/rpm depend on libicu.
- **The AOT compiler has a security floor** (`Directory.Build.targets`, from CheeseGit): an SDK older than a fix for an
  advisory on ILCompiler gets the patched version instead of failing restore (NU1903).

## Native code

| Step | Script | Output |
|---|---|---|
| FFmpeg | `native/ffmpeg/build.sh <rid> <prefix>` | shared libraries in `<prefix>` (Windows: MSYS2 with the MSVC environment, `--toolchain=msvc`) |
| localfm_native | `native/build.sh <rid> <prefix>` | `native/out/<rid>/`: the library, FFmpeg's libraries and licence |

`LocalFM.Native` copies everything in `native/out/<rid>/` beside the app (the RID, or the host's without one). The
package scripts run both steps (`FFMPEG_PREFIX` reuses an FFmpeg build); CI builds FFmpeg once per RID and caches it
until `build.sh` changes (`.github/workflows/ffmpeg.yml`, `.github/actions/native`).

## Packages

| OS | Script | Output |
|---|---|---|
| Windows | `package.ps1 -Version x.y.z [-Rid win-arm64] [-FfmpegPrefix dir]` | `LocalFM-<rid>-Setup.exe` (per user, no admin), `-Portable.zip`, `.nupkg` (+ delta) |
| macOS | `package.sh x.y.z` | Velopack `.pkg`, portable zip, `.nupkg`, and a `.dmg` with an Applications link |
| Linux | `package.sh x.y.z` | AppImage + `.nupkg` (in-app updates); distro tarball, `.deb`, `.rpm` (nfpm, or its container through podman/docker) |
| Android | `release.yml`'s `android` job (docs/android.md) | `LocalFM-<version>.apk` (arm64 and x64), signed with the release keystore |

- Native AOT does not cross-compile between operating systems; Windows needs the MSVC linker ("Desktop development
  with C++") and CMake. `package.ps1` needs `native\out\<rid>\` built, or `-FfmpegPrefix` to build it in Git Bash.
- `PREVIOUS=<dir>` / `-Previous <dir>` (the channel's last release, from `vpk download github`) makes vpk build a delta.
- The distro tarball, `.deb`, `.rpm`, AUR and Flatpak are built with `-p:LocalFMUpdater=false`, which compiles the
  updater out (`NO_UPDATER`). They install to `/usr/lib/localfm` with `/usr/bin/localfm` as a symlink.
  `desktop-file-validate` and `appstreamcli validate --pedantic` pass (one pedantic note: the app id has capitals).
- `packaging/fill.sh` fills the Flatpak manifest, AUR `PKGBUILD` and winget manifests with the version and checksums.
- **Flatpak:** the distro tarball on `org.freedesktop.Platform` 26.08: PulseAudio, network (metadata, lyrics, vibes),
  the home folder and removable media, the MPRIS name and the tray. The system's yt-dlp cannot run inside it; the
  Import page says to locate yt-dlp's single-file `yt-dlp_linux` instead.
- **Size budget:** `check-size.sh <publish-dir> <rid>` against `size-budget.txt` (measured 2026-11-15 with FFmpeg: linux-x64
  55.6 MB, osx-arm64 54.9 MB, win-x64 ~63 MB once SkiaSharp's 100 MB of native .pdb files were dropped from the
  publish; budgets 60/60/70 MB). `check-size.sh` lists the largest files, so a jump points at its cause. The AppImage is 29 MB, the tarball 24 MB.
- **Icons:** `src/LocalFM.UI/Assets/icon.svg` → `icon.png` (512 px) is the only icon; `tools/LocalFM.IconGen` writes
  the `.ico` and `.icns` at build time.

## Updates

| Copy | Mode | Behaviour |
|---|---|---|
| Setup.exe, portable zip, AppImage, macOS package (Velopack) | In-app | Banner: **When I quit** (download now, install once LocalFM quits; closing to the tray is not quitting), **Restart now** (pause, save the queue and position, install, and play on), **Skip** (remembered), × (ask again next start) |
| deb, rpm, AUR, Flatpak, Android APK | Notice | The banner says a version exists and links to its release page |
| Dev builds, publish folders | None | No checks |

- **Restart now is hidden in a vibe**: restarting would end it (for everyone, when hosting). "When I quit" stays.
- Restart now saves the player state and `update.resume=1` in the kv store before Velopack takes over; the next start
  restores the queue and position and, when music was playing, plays on.
- The updater reads GitHub releases with no token; pre-releases and drafts are not offered. Downgrades are not
  supported (database migrations only go forward).
- The startup check is silent on failure (offline, rate limits) and can be turned off on Sources → Updates, where
  "Check now" reports errors and "up to date".
- Release notes travel with the update (`vpk pack --releaseNotes`; the GitHub release body for the notice), and the
  new version shows them once as **What's new**. Notes for a version that never got installed are dropped.
- The AppImage asks once to add itself to the applications menu (Sources → Updates keeps the button).
- `LOCALFM_UPDATE_SOURCE=<folder or URL of vpk output>` replaces the GitHub feed, to try an update before publishing:

  ```sh
  OUT=/tmp/u/v1 ./package.sh 0.1.0                     # the copy to update (AppImage, or install Setup.exe)
  mkdir /tmp/u/prev && cp /tmp/u/v1/releases/{releases.*.json,RELEASES-*,assets.*.json,*-full.nupkg} /tmp/u/prev/
  OUT=/tmp/u/v2 PREVIOUS=/tmp/u/prev NOTES=notes.md ./package.sh 0.1.1
  LOCALFM_UPDATE_SOURCE=/tmp/u/v2/releases /tmp/u/v1/releases/LocalFM-linux-x64.AppImage
  ```

  Verified 2026-11-15 on the Linux AppImage for both buttons, with music playing:
  `LOCALFM_SNAPSHOT_UPDATE=quit|restart` (with a test `LOCALFM_HOME`) presses them without a click. Velopack keeps
  downloads in `/var/tmp/velopack/LocalFM/packages` and installs a pending one as soon as any copy starts, so clear
  that folder between runs (and after testing, before installing a real release).
- Snapshots of the banner states: `LOCALFM_SNAPSHOT_UPDATE=available|downloading|notice|whatsnew` (no network).

## CI

- `.github/workflows/ci.yml`: on Ubuntu 22.04, Windows and macOS: the bundled FFmpeg (cached) and localfm_native, the
  tests (fixtures made with the ffmpeg CLI), a real AOT publish with the size check, and a screenshot of the published
  app playing a generated song (artifact `screenshot-<rid>`), the first look at each OS's UI.
- `.github/workflows/release.yml` (manual): checks the version, builds FFmpeg (cached) and packages the six RIDs on
  their own runners, fills the manifests (kept as an artifact), and creates the release with every file at once.
  Optional secrets, listed in its header, enable macOS signing and notarization, Windows signing and winget updates.
  Without them, packages are unsigned: macOS Gatekeeper and Windows SmartScreen warn on first launch.
  The Android APK needs `ANDROID_KEYSTORE_B64` and `ANDROID_KEYSTORE_PASS`; **back up the keystore**: phones only
  update an app signed with the same key (docs/android.md).
- `ci.yml` also builds the Release APK (trimmed, AOT) with its size check; FFmpeg for `android-arm64`/`android-x64`
  is built with the NDK pinned in `.github/actions/ndk`.

## Making a release

1. `release.yml` must be on GitHub's default branch (`master`) for the Run workflow button to appear.
2. Actions → release → Run workflow: a version above the last one, the release notes as base64
   (`base64 -w0 notes.md`), and "pre-release" for a test that installed copies will not pick up.
3. Updates only show up from the second release on. Install a release from GitHub and update it on each OS before
   relying on it.
4. winget: submit the first manifest by hand from the release's `manifests` artifact; with `WINGET_TOKEN`, later
   releases update it.
