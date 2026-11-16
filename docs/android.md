# Android

LocalFM on a phone: the same `MainView` as the desktop in its compact layout, playing the phone's own music, in the
background with a media notification, and joining or hosting vibes. Left out for now: Tidy up, the album editor,
matching, moving songs, the importer, knowledge base export, folder watching and in-app updates.

## Layout

| Part | What |
|---|---|
| `src/LocalFM.Android` | The app head: `MainApplication` (sets `App.Platform`), `MainActivity` (Avalonia), `AndroidPlatform` (host, permissions, pickers, share, snapshot mode), `MediaPlaybackService`, `NetworkLocks` |
| `PlatformCapabilities.Phone` | What the phone leaves out (`src/LocalFM.UI.Shared/Platform.cs`); pages hide those actions |
| `MainView` classes | `compact` below 640 px wide or 480 px high (bottom bar, mini player, full-screen sheets), `touch` (tap plays a row, no hover-only buttons, no volume slider) |

The `AppHost` lives in `AndroidPlatform` for the whole process, not in the activity: leaving with Back, rotating or
the activity being recreated never touches playback.

## Behaviour

- **Music:** the phone's audio files, read by path with `READ_MEDIA_AUDIO` (`READ_EXTERNAL_STORAGE` up to Android 12).
  The first start asks once; Sources has an Allow access card while it is missing, which opens the app's settings
  once Android stops showing the prompt. The Music folder is added when access is granted. Folders are not watched:
  the library is rescanned whenever the app comes back. Add folder takes a document tree and maps it to its path.
- **Background:** `MediaPlaybackService` is a `mediaPlayback` foreground service while music plays and for the whole
  of a vibe. It owns the `MediaSession` (lock screen, quick settings, headset and Bluetooth buttons) and the
  MediaStyle notification, asks for audio focus (a call pauses, a short interruption resumes, the system ducks for
  navigation), pauses when headphones are unplugged, and holds a partial wake lock while playing or in a vibe.
  Paused, the notification stays (swipeable) and nothing keeps the process alive.
- **Vibes:** a multicast lock while the vibe panel looks for nearby vibes or ours is announced (Android drops
  multicast otherwise), a low-latency Wi-Fi lock during a vibe, the device model as the default name, and the share
  sheet for invites. From a mobile network, hosting is rarely reachable (carrier NAT); on Wi-Fi it works as on a
  computer.
- **Knowledge base:** Sources → Import picks a `.localfm-kb` (copied into the cache from its content URI) and merges
  it like the desktop does.
- **Updates:** a notice when GitHub has a newer release (`ReleaseNoticeService`); the APK installs over the old one
  and keeps the library, as long as it is signed with the same key.
- **Licences:** `assets/licenses/` in the APK holds LocalFM's licence and the LGPL of the bundled FFmpeg.

## Toolchain

Building needs the .NET `android` workload, JDK 21 (not newer), the Android SDK and the NDK.

```sh
dotnet workload install android                     # a user-local SDK if the system's cannot take workloads
export JAVA_HOME=<jdk-21> ANDROID_NDK_HOME=<sdk>/ndk/28.2.13676358
dotnet build src/LocalFM.Android -t:InstallAndroidDependencies -p:AndroidSdkDirectory=<sdk> -p:AcceptAndroidSDKLicenses=true
for rid in android-arm64 android-x64; do
  native/ffmpeg/build.sh $rid /tmp/ffmpeg-$rid && native/build.sh $rid /tmp/ffmpeg-$rid
done
dotnet build src/LocalFM.Android -p:AndroidSdkDirectory=<sdk> -p:JavaSdkDirectory=$JAVA_HOME     # Debug: installs with adb
dotnet publish src/LocalFM.Android -c Release -p:Version=x.y.z ...                                # trimmed, AOT, both ABIs
```

- The project is not in `LocalFM.slnx`, so a plain `dotnet test` works without the workload.
- FFmpeg is built with `--target-os=android` (unversioned sonames, the only kind an APK carries), linked with 16 KB
  page alignment for Android 15 devices, and loaded through `AndroidNativeLibrary` items; `LibraryImport` names are the
  same as on the desktop.
- Debug builds embed the assemblies (`EmbedAssembliesIntoApk`) so `adb install` works without the IDE.
- Release builds run Mono AOT (profiled) and the trimmer. IL2104 (another assembly has trim warnings) is a warning
  here, not an error: Avalonia's designer support and SQLite's Android bundle raise it. Run the Release APK on the
  emulator (below) before releasing.
- Emulator: an x86_64 image works; with no GPU, `-gpu swangle_indirect` (SwiftShader crashes).

## Checking it

- `.claude/skills/ui-review/scripts/snapshot-android.sh <out> [apk]` installs the APK, grants permissions and takes
  every page with the same `LOCALFM_SNAPSHOT_*` settings as the desktop (as intent extras). Push music first:
  `adb push <dir>/. /sdcard/Music/`. Desktop shots at 412×860 (`phone-*` in `snapshot-all.sh`) show the compact
  layout without an emulator.
- Background playback: `LOCALFM_SNAPSHOT_PLAY=all` (every track, repeating), Home, `adb shell input keyevent KEYCODE_SLEEP`,
  then `adb shell dumpsys media_session` shows the position and track moving on. `adb shell cmd media_session dispatch
  pause|play|next` presses the session's buttons.
- Vibes: `LOCALFM_SNAPSHOT_VIBE_JOIN=<invite>` joins a desktop host (`tools/LocalFM.VibeCli host`).
  `LOCALFM_SNAPSHOT_VIBE=host` hosts on the phone; the emulator is behind NAT, so a desktop listener needs
  `adb forward tcp:47900 tcp:47811` and the invite's endpoints rewritten to `127.0.0.1:47900` (`Invite with { Endpoints = … }`).

## Signing and releases

Phones only install an update signed with the same key as the installed copy. **Losing the release keystore means
every phone has to uninstall (losing its library) to get a new version**, so keep a backup somewhere safe besides
the GitHub secret.

- The keystore is a PKCS#12 file with one key, alias `localfm`, whose password is the store's.
- GitHub secrets: `ANDROID_KEYSTORE_B64` (`base64 -w0 release.keystore`) and `ANDROID_KEYSTORE_PASS`.
- `release.yml`'s `android` job builds `LocalFM-<version>.apk` with them; without them it warns and the release has
  no APK. versionCode is major·10000 + minor·100 + patch, so versions must keep growing.
- CI (`ci.yml`) builds the Release APK signed with the build's debug key and checks its size (`size-budget.txt`,
  `android`: measured 42.2 MB, budget 50 MB).
- Installing from GitHub needs "Install unknown apps" allowed for the browser or file manager.
