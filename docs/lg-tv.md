# TVs and browsers

LocalFM can share its library with devices at home: a smart TV, or any web browser (another computer, a tablet, a
phone). They browse the library and play on their own, with their own queue; the music streams from the computer,
which only has to be running LocalFM. Nothing is stored on the TV.

## On the computer

Sources → **TV and other devices** → *Share my library on this network*. LocalFM shows the address to open
(`http://<this computer>:47812`) and remembers the setting. *Pair a device…* shows a six-digit PIN, valid for ten
minutes and once; a paired device keeps a token and doesn't need the PIN again. Remove a device in the list to lock it
out.

Only home networks can connect (private IPv4, IPv6 ULA and link-local, and 100.64/10 for VPNs such as Tailscale);
nothing is mapped on the router. The connection is plain HTTP inside the home network, so treat the share like a
file share.

## In the TV's web browser (no install)

Open the web browser on the TV, type the address from the computer, and enter the PIN. Bookmark the page. This works
on any smart TV with a reasonably modern browser.

## As an app on an LG TV (webOS)

The app is the same client packaged for webOS, so it gets a tile in the launcher and the remote's Back and media
keys. It asks once for the computer's address. LG allows installing your own apps on your own TV for free through
**Developer Mode**. No paid licence is needed, only a free LG developer account.

1. Make an account at [webostv.developer.lge.com](https://webostv.developer.lge.com) (free).
2. On the TV, install **Developer Mode** from the LG Content Store, sign in, turn *Dev Mode Status* on (the TV
   restarts), then turn *Key Server* on.
3. Build the app on the computer (needs Node.js): `packaging/webos/build.sh` →
   `/tmp/localfm-package/webos/org.localfm.tv_<version>_all.ipk` (or the folder you pass).
4. Install it, either with the CLI:
   ```sh
   npx -p @webos-tools/cli ares-setup-device      # add the TV: its IP, port 9922, user prisoner
   npx -p @webos-tools/cli ares-novacom --device tv --getkey   # passphrase from the Developer Mode app
   npx -p @webos-tools/cli ares-install --device tv /tmp/localfm-package/webos/org.localfm.tv_*_all.ipk
   ```
   or with [webOS Dev Manager](https://github.com/webosbrew/dev-manager-desktop) (a desktop app: add the TV, then
   drag the .ipk onto it).
5. Open LocalFM on the TV, type the computer's address (`192.168.1.20`; the port is added), and enter the PIN.

**The Developer Mode timer.** Developer Mode runs for a limited time (the Developer Mode app shows *Remain Session*;
LG's figure has changed over the years, 50 to 1000 hours). Press **Extend** in the app before it runs out: once it
expires, the TV removes the apps installed this way and you install again after turning Developer Mode back on.
Some tools (webOS Dev Manager, home-automation scripts) can extend it automatically. Rooted TVs (Homebrew Channel) don't have the timer, but rooting
only works on some firmware and is not needed.

## What works and what doesn't

- Albums, artists (photo and bio), songs, playlists, search; play, shuffle, repeat, seek; now playing with synced
  lyrics; quieter tracks follow ReplayGain (volume can only go down).
- Remote: arrows move, OK opens or plays, Back goes back, play/pause/stop and ⏪/⏩ (seek 10 s) on the remote, and the
  Magic Remote pointer.
- MP3 and AAC play directly, and FLAC/Vorbis/Opus where the TV supports them. Anything else (APE, WavPack, DSD, WMA,
  24-bit WAV, …) is decoded by LocalFM and sent as WAV, so it plays everywhere with seeking.
- No gapless playback or crossfade between tracks, no EQ (one audio element in the browser), no editing.
- The TV doesn't follow the computer's playback (that's a vibe). The library protocol is shared with vibes so that
  can come later.

## How it works

| Part | What |
|---|---|
| `src/LocalFM.Vibe/Library` | `LibraryApi` answers `LibraryRequest`s (the vibe protocol's JSON messages) from the knowledge base; `MediaResolver` maps track and picture ids back to library files. Replies never contain paths |
| `src/LocalFM.Vibe/Web` | `WebGateway`: a small HTTP/1.1 server with a web socket (`/ws`: `hello` with a token or `pin:NNNNNN` → `welcome`, then `lib` requests), `/media/<track>` and `/img/<id>` with byte ranges, `/media/<track>.wav` (`WavStreamer`), `/api/info`. `PairedDevices` keeps token hashes in the kv table; `LibraryShareService` turns it on and off |
| `src/LocalFM.Vibe/Web/client` | The web app, embedded in the assembly: ES2017 and plain CSS for old TV engines (webOS 5 has Chromium 68), no build step |
| `packaging/webos` | `appinfo.json.in`, icons and `build.sh` for the IPK |
