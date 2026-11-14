---
name: ui-review
description: Screenshot-driven UI/UX review and polish for the LocalFM Avalonia app. Use it whenever you change or add anything visible in LocalFM (a view, control, style, animation, visualizer, lyrics, player bar, miniplayer), before reporting UI work as done, and whenever the user asks to check, review, polish or "look at" the UI or UX, mentions misalignment, inconsistent styling, missing gestures/shortcuts, empty states, or says something looks bad — even if they don't say "review".
---

# LocalFM UI/UX review

LocalFM is used every day as the user's music player, so small UI flaws (a control that looks borrowed from
another app, a title that jumps between pages, a slider you can't scroll) matter as much as features.
This skill turns "look at the UI" into a repeatable pass: render every screen headlessly, read the screenshots
against a checklist, check gestures in code, fix, re-render, and report honestly what was and wasn't verified.

## How the screenshots work

The app has a built-in snapshot mode (`src/LocalFM.UI/Views/MainWindow.axaml.cs`, `SnapshotAndExit`): with
`LOCALFM_SNAPSHOT=<png>` it renders its own window to a PNG and exits, so there's no screen capture and no window
stealing focus. `LOCALFM_OUTPUT=null` keeps playback silent while the audio clock still runs, so lyrics,
position and visualizers move as in real use. Other options: `LOCALFM_SNAPSHOT_PAGE` (a nav page,
`album`, `match`, `edit`, `mini`, `artist:<name>`), `_PLAY=1`, `_LYRICS=1`, `_NOWPLAYING=<mode>`, `_WAIT=<ms>`,
`_WIDTH`/`_HEIGHT`, `_SCROLL=<px>`. Add an option there when a state you need can't be reached.

Never point a review at the user's real library (`~/.local/share/LocalFM`). Use a test library in the
scratchpad; it is private data and a review must not change it.

## Workflow

1. **Build** the Debug UI: `dotnet build src/LocalFM.UI -v q`.
2. **Test library** (once per session, reuse after):
   `bash .claude/skills/ui-review/scripts/make-test-library.sh <scratchpad>/uitest`
   It makes generated audio (a rich signal with a cover, word-synced and line-synced lyrics, YouTube-style messy
   names) and scans it. Add cases to the script when a review needs a state it lacks.
3. **Snapshot everything**:
   `bash .claude/skills/ui-review/scripts/snapshot-all.sh <scratchpad>/uitest/home <scratchpad>/ui-<n>`
   (third argument: an AOT binary to check the release build). It covers every page, playing states,
   each visualizer mode, lyrics, miniplayer, the minimum window size and scrolled long pages. When reviewing
   one change, you can run just the relevant shots by copying the `shot` lines.
4. **Read the screenshots** with the Read tool, one by one, against `references/checklist.md`. Crop and
   enlarge (Python PIL) when judging text rendering, alignment within a few pixels, or a highlight effect.
   Compare equivalent screens side by side for consistency.
5. **Check gestures and keyboard in code** (section 3 of the checklist): grep the views for
   `PointerWheelChanged`, `DoubleTapped`, `KeyBinding`, `AddHandler`, `Tapped`, and the key switch in
   `MainWindow.axaml.cs`. Screenshots can't show input handling, so this is the only way to catch a missing one.
6. **Write the findings list** before fixing: what, where (file:line), why it matters, the fix. Order by what a
   user would notice first. Fix the clear ones; ask about ones that change behaviour or taste.
7. **Fix, then re-snapshot the affected screens** and look again. A fix isn't done until the new screenshot
   shows it. Keep the before/after pair for the report when it helps.
8. **Run the tests and the AOT publish** after UI changes (`dotnet test LocalFM.slnx`;
   `dotnet publish src/LocalFM.UI -r linux-x64 -c Release -p:PublishAot=true` with zero IL2xxx/IL3xxx warnings).
   Compiled bindings and trimming can break things that work in Debug.

## Taste: what this user wants

- **Restrained over flashy.** Reuse the app's own vocabulary (Muted → Text for "current", Surface steps for depth,
  pill buttons, segmented choices, flat icon buttons). Accent is for active/primary, not decoration.
- **No redundant controls.** If a button toggles something open, it also closes it.
- **Everyday gestures are expected**, not extras: wheel on sliders, Enter to submit, Esc to close, drag to reorder,
  double-click to play, mouse back button, clickable names.
- **Effects must look clean at 100% zoom.** Drawing text twice (glow/overlay/clip layers) causes fringes; prefer
  one layout with per-run colours. Animations ease (~200 ms) and stop when off-screen.

## Reporting

Lead with what changed for the user, grouped by screen. Then state plainly what screenshots could not verify
(scrolling, dragging, clicking, hover, focus, real audio devices) so it goes on the user's manual test list.
Attach or reference the key screenshots by full path. Don't claim an interaction works because the code for it
compiles.
