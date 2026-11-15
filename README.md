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

Windows/macOS (untested so far): build the native library once against a shared FFmpeg 8.x build,
then build as usual:

```sh
cmake -S native -B native/build -DFFMPEG_ROOT=/path/to/ffmpeg-shared && cmake --build native/build --config Release
```

## Environment

- `LOCALFM_HOME` — data folder (default: `~/.local/share/LocalFM`): `library.db`, `images/`
- `LOCALFM_OUTPUT=null` — silent real-time output (headless runs)
- `LOCALFM_SNAPSHOT=out.png` (+ `LOCALFM_SNAPSHOT_PAGE=albums|album|artists|tracks|playlists|queue|sources`, `LOCALFM_SNAPSHOT_PLAY=1`) — render the window to PNG and exit

## Licence

LocalFM is released under the [MIT licence](LICENSE). Packages ship an audio-only build of
[FFmpeg](https://ffmpeg.org), licensed under the LGPL 2.1 (`native/third_party/ffmpeg/COPYING.LGPLv2.1`), as separate
shared libraries.
