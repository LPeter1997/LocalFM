# UI/UX review checklist for LocalFM

Go through each section. For every screenshot, ask the questions of the *whole* screen, then of each
control. The point is to find what a daily user would trip over, not to tick boxes.

## Contents
1. Layout and alignment
2. Consistency with the app's own look
3. Gestures and keyboard (code-level; screenshots cannot show these)
4. States: empty, loading, error, long, many, none
5. Text and wording
6. Visual polish and motion
7. Accessibility basics
8. Playback safety

## 1. Layout and alignment
- Page titles sit at the same x/y on every page (switching pages should not make the title jump).
- Left edges line up: title, first control, list rows. A 4 px offset reads as a mistake.
- Spacing between groups is consistent (the app mostly uses 6/8/12/16/24).
- Controls on one row share a vertical centre and height (pills next to text boxes next to icon buttons).
- Nothing touches an edge or another control; right-aligned numbers have room before trailing buttons.
- At the narrowest window (820×520): no overlap, no clipped buttons, text truncates with an ellipsis rather
  than being cut, side panels (lyrics) still leave a usable content area.
- Big empty areas are intentional (an empty state), not leftover space.

## 2. Consistency with the app's own look
Design tokens live in `src/LocalFM.UI/App.axaml`:
- Colours: `Bg` < `Surface` < `Surface2` < `Surface3`, `Line`; text `Text` > `Muted` > `Faint`; one `Accent` (follows the cover).
- Buttons: `Button.primary` (one main action per area), `Button.ghost` (secondary pills), `Button.flat` (icon buttons, `.on` = active).
- Choices: `ListBox.segmented` for 2–5 exclusive options, `ToggleButton.chip` for an on/off with a label, `CheckBox` for settings pages.
- Text boxes and ComboBoxes are restyled globally; a stock Fluent look (square, white border) is a miss.
Questions:
- Does any control look like it came from a different app (default Fluent widget, odd corner radius, other colours)?
- Is the accent used sparingly for "active/primary", not as decoration? Is anything accent-coloured that is not interactive or current?
- Do equivalent things look equivalent (all "Play" buttons, all row hover states, all section cards)?
- Icons: same stroke weight and size (18 px in bars, 14 px in rows), from the shared `Ico*` set.

## 3. Gestures and keyboard
Screenshots can't show these, so check them in the code (`*.axaml.cs`, `KeyBinding`, `AddHandler`) and list
anything you couldn't verify for the user's manual testing.
- Sliders: scroll wheel changes them (volume does; look for new sliders without it). Click on track jumps.
- Lists: double-click plays, Enter plays, Delete removes where removing makes sense, right-click opens the track menu,
  drag reorders where order matters (queue does; playlists?).
- Text boxes: Enter submits, Esc clears or cancels; search is reachable with Ctrl+F.
- Navigation: Back button, Alt+Left and the mouse back button; Esc closes overlays.
- Toggles: the control that opens something also closes it; no separate redundant close button.
- Clickable-looking text (titles, artists) is clickable and shows a hand cursor; non-clickable text does not look clickable.
- Tooltips on icon-only buttons, with the shortcut in brackets when there is one.
- Global keys: Space, Ctrl+←/→, ←/→, Ctrl+↑/↓, L, V, Ctrl+M, media keys — none fire while typing in a text box.

## 4. States
For each screen, picture: nothing yet (fresh install), loading, a failure (offline, file missing), one item,
thousands of items, very long names, missing data (no cover, no year, no artist).
- Empty states say what the area is for and how to fill it, in one or two lines.
- Loading never blanks the page or blocks input; progress is visible but quiet.
- Missing data leaves no stray separators ("Queen · · 1 track"), no "null", no empty brackets.
- Placeholders (cover, artist photo) are consistent across screens.

## 5. Text and wording
- Plurals: "1 track", "2 tracks" (use `Converters.Count`).
- Sentence case for labels and buttons; verbs on buttons ("Save queue", not "Queue saving").
- Plain words a non-technical listener understands; no internal terms (ids, providers' jargon) unless needed.
- Numbers: times as m:ss / h:mm:ss, thousands separators, units with a space ("6 s", "+3 dB").

## 6. Visual polish and motion
- Restraint: one strong effect per screen at most. Effects that draw text twice, outlines on text, per-letter
  wipes or busy gradients read as glitches (the user rejected a per-letter karaoke wipe for exactly this).
- Highlight = change the colour the app already uses for "current" (Muted → Text), with a short fade (~200 ms).
- Visualizers and animations: smooth (no jitter between frames), draw nothing on silence, stop when hidden.
- Contrast between layers is visible but soft (Surface steps), hover states are visible on every interactive element.

## 7. Accessibility basics
- Text contrast: `Faint` only for tertiary info, never for something the user must read to act.
- Hit targets at least ~28 px for icon buttons.
- Keyboard focus is visible and reaches every control on the page.

## 8. Playback safety
The project's hard rule: nothing that doesn't need to may stop the music. A UI change must not do I/O or
heavy work on the UI thread, and must never call into the engine from a render or animation loop except the
lock-free reads (`ReadVisualization`, `Position`).
