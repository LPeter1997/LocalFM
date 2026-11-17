# LocalFM

A local-first music player and music knowledge base: Native AOT .NET 10 + Avalonia, FFmpeg decoding, miniaudio output.

## Layout

| Path | What |
|---|---|
| `native/localfm_native` | C shim: lock-free output ring + miniaudio device (pause/volume/seek fades on the audio thread), FFmpeg decoder/prober |
| `src/LocalFM.Core` | ULID ids, logging, data paths |
| `src/LocalFM.Native` | `[LibraryImport]` bindings + safe wrappers (`AudioOutput`, `Decoder`, `MediaProbe`) |
| `src/LocalFM.Playback` | Gapless engine: engine thread decodes into the ring, splices the next track sample-exactly |
| `src/LocalFM.Library` | SQLite knowledge base (MusicBrainz-shaped schema in `Migrations/`), scanner, importer, watcher, queries |
| `src/LocalFM.Metadata` | Provider contract (implementations are the next phase) |
| `src/LocalFM.App` | Play queue, player service (history, ReplayGain, resume) |
| `src/LocalFM.Vibe` | "Vibe together": host shares playback, listeners follow in sync (TLS, invite codes, remote file streaming) |
| `src/LocalFM.UI` | Avalonia app (`LocalFM` executable; `--vibe-host` / `--vibe-join CODE` until the vibe panel exists) |
| `tools/LocalFM.VibeCli` | Headless vibe harness: `host [--null] a.flac b.mp3` prints an invite, `join [--null] [--debug] CODE` follows it |
| `tools/LocalFM.EngineCli` | Headless engine harness: `dotnet run --project tools/LocalFM.EngineCli -- a.flac b.mp3` |

## Build

Needs the .NET 10 SDK, CMake, a C compiler and FFmpeg 8.x shared libraries (`libavformat.so.62`…; runtime
packages are enough, the FFmpeg headers and miniaudio are vendored under `native/third_party`).

```sh
dotnet build                               # on Linux also builds native/out/linux-x64/liblocalfm_native.so via CMake
dotnet test                                # needs the ffmpeg CLI for fixtures (tests skip without it)
dotnet run --project src/LocalFM.UI        # or: -- --add-folder ~/Music
dotnet publish src/LocalFM.UI -r linux-x64 -c Release   # Native AOT
```

Bundled FFmpeg (what packages ship, on any OS; Windows runs these in MSYS2 / Git Bash):

```sh
native/ffmpeg/build.sh linux-x64 /tmp/ffmpeg   # audio-only LGPL FFmpeg 8.1.3
native/build.sh linux-x64 /tmp/ffmpeg          # localfm_native + FFmpeg's libraries into native/out/linux-x64/
```

## Install and release

Releases are on [GitHub](https://github.com/LPeter1997/LocalFM/releases): Windows `Setup.exe`/portable zip, macOS
`.pkg`/`.dmg`, Linux AppImage (these update themselves), and `.deb`, `.rpm`, a tarball plus Flatpak, AUR and winget
manifests (the package manager updates those). `./package.sh <version>` (`package.ps1` on Windows) builds them
locally; [docs/release.md](docs/release.md) has the whole flow, CI and how to make a release.

## Environment

- `LOCALFM_HOME` — data folder (default: `~/.local/share/LocalFM`): `library.db`, `images/`, `logs/`
- `LOCALFM_LOG=debug|info|warning|error` — how much goes into the session log (default `info`)
- `LOCALFM_OUTPUT=null` — silent real-time output (headless runs)
- `LOCALFM_UPDATE_SOURCE=<folder or URL>` — read updates from vpk output instead of GitHub (docs/release.md)
- `LOCALFM_SNAPSHOT=out.png` (+ `LOCALFM_SNAPSHOT_PAGE=albums|album|artists|tracks|playlists|queue|sources`, `LOCALFM_SNAPSHOT_PLAY=1`) — render the window to PNG and exit

## Session logs

Every run writes `<data folder>/logs/localfm-<date>-<time>-<pid>.log` (the newest 10 are kept; on Windows the data
folder is `%LOCALAPPDATA%\LocalFM`, on macOS `~/Library/Application Support/LocalFM`). It starts with the version, OS,
audio device and sound settings, then records every play, pause, seek and skip with who asked (`app`, `key Space`,
`seek bar`, `tray`, `MPRIS :1.42`, `vibe`, …; `busctl --user status :1.42` names an MPRIS client's process), and
anything that could be heard as a dropout: underruns, audio gaps where the system ran the audio thread late,
engine-thread stalls, and the device stopping or moving. Logging only queues the line; a background thread writes it.

## Licence

LocalFM is released under the [MIT licence](LICENSE). Packages ship an audio-only build of
[FFmpeg](https://ffmpeg.org), licensed under the LGPL 2.1 or later, as separate shared libraries next to the app with their licence
(`FFmpeg-COPYING.LGPLv2.1.txt`). `native/ffmpeg/build.sh` rebuilds them from the pinned FFmpeg source.
