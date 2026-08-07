# Changelog

All notable changes to Hypertree are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

---

## [v0.3.14] - 2026-08-07

### Fixed

- The map dimming stays dark to the edges, so overflowing branches no longer sit on a washed-out gap at the bottom.

---

## [v0.3.13] - 2026-08-06

### Fixed

- **`Shift+Delete`** removing a branch now clears its desktops at once; they no longer linger on main before disappearing the next time the map opens.

---

## [v0.3.12] - 2026-08-06

### Added

- **Monitor layout restore** — undock and your window arrangement across monitors is remembered; redock and a notification offers to put the windows back on the right screens, only when they're actually out of place, and names how many will move.
- Restoring puts maximised windows back across monitors, keeps your focus where it is, and covers the shuffle with a brief "Restoring positions" screen.

---

## [v0.3.11] - 2026-08-05

### Fixed

- The main timeline holds its place at the top of the stack instead of drifting to follow the active branch; a branch you moved above main still loads where you left it.

---

## [v0.3.10] - 2026-08-05

### Added

- **Branch switcher** — a floating, draggable panel listing every branch in map order, each showing the desktop a click jumps to; the desktop chip picks another when a branch has several. Collapse it to a logo bubble from its header or **`Ctrl+Alt+W`**, and right-click for **Exit**. Off by default (**Settings → Switcher**). Borrowed from Perch's "click to switch".

### Fixed

- Changing any setting no longer wipes your saved **custom launcher commands**.

---

## [v0.3.9] - 2026-08-05

### Added

- **`Shift+M`** on the map pulls windows from other desktops onto the current one — the mirror of `m` (move away). A grid shows everything running elsewhere, each card naming its desktop; `Space` ticks, `Enter` pulls.

### Changed

- The move and pull grids gained a **search box** — filter windows by title, process, or desktop as you type.
- Bigger window thumbnails in both grids, so a wall of near-identical terminals is tellable apart.

### Fixed

- Start-at-login only ever registers the installed copy now, so an update can't leave login relaunching an old version that then reports itself a "dev build".
- A start-at-login entry left pointing at a previous install location self-heals the next time Hypertree runs.

---

## [v0.3.8] - 2026-08-04

### Changed

- On the map, **`Shift+R`** renames the selected **branch** (a no-op on main); `r` still renames the desktop.
- On the map, **`Shift+↑/↓`** now re-slots the **main timeline** too, not just branches — sink main below a branch or lift it above one.

---

## [v0.3.7] - 2026-08-01

### Added

- **Application launcher** — `Ctrl+Alt+O` opens a spotlight over your installed apps, each with its real icon; type to filter, Enter to launch. Covers both classic desktop apps and packaged / Store apps (Windows Terminal, Calculator, …), the same set Windows Search shows.
- `o` opens the launcher from the map — Esc pops back to it.
- **Custom commands** — save your own launcher entries (an app, file, folder, or URL, with optional arguments and working directory) in the launcher's "Custom commands…" screen; they sit above the discovered apps.
- `Command…` in the launcher runs a one-off shell command, like Win+R. Only this explicit entry runs typed text, so filtering the list never launches anything by accident.
- The launcher chord is rebindable in Settings.

---

## [v0.3.6] - 2026-07-30

### Added

- **Navigation history** — `Ctrl+Alt+A` / `Ctrl+Alt+S` step back and forward through the desktops your navigation landed on. One crumb per completed gesture or jump, not every step in between.
- `Ctrl+Alt+Q` bounces between your two most recent desktops — press to hop over, press again to hop back.
- All three chords are rebindable in Settings.
- The map shows the trail top-right as a small history queue: the entry you're standing on highlighted, entries you've stepped back past dimmed.

### Removed

- The settings cog on the map — settings stay reachable from the tray menu and the command palette.

---

## [v0.3.5] - 2026-07-30

### Added

- **One-line install** — a PowerShell one-liner from the README installs Hypertree without admin rights, and puts `htree` on your PATH.
- Installing that way skips the **"Windows protected your PC"** SmartScreen dialog, since nothing tags the download with the mark-of-the-web.
- The installer is verified against the release's `SHA256SUMS.txt` and deleted rather than run on any mismatch.
- Every release now publishes a `SHA256SUMS.txt` manifest covering its assets, so a download can be checked by hand.

---

## [v0.3.4] - 2026-07-30

### Changed

- **`n` on the map creates the desktop in the selected row** — the branch you're looking at, not always the main timeline.
- The new tile lands at the end of that branch, and the selection homes onto it ready to rename.
- A branch desktop takes the branch's `branch · name` prefix in Windows; its tile keeps the bare name.

---

## [v0.3.3] - 2026-07-28

### Changed

- **Navigating no longer flashes the desktop you're landing on** — the backdrop goes up before the switch instead of after it, so the destination is never presented uncovered.
- **The navigation flash fades in and out** instead of snapping on and vanishing in one frame. Independent of "Animate navigation moves", which governs the directional wipe.
- **Dismissing the board no longer punches the screen back to full brightness** — it eases away, which is what flashed even when you weren't changing desktops.
- **A held run of moves no longer pulses the backdrop** — the dim stays put once the board is up.

---

## [v0.3.2] - 2026-07-28

### Changed

- **Checking for updates no longer takes over the screen** — it reports through Windows notifications instead of a card that dimmed the desktop and swallowed clicks.
- **One notification per check**: it says it's checking, then updates in place with the result.
- **Click an "Update available" notification** to download it and restart.
- **The tray menu offers "Update now — vX"** once a check has found a release, matching the command palette.
- **Settings → Updates raises the same notifications**, and still shows the detail inline.

---

## [v0.3.1] - 2026-07-28

### Added

- **`p` on the map opens the command palette** and **`f`** the finder — bare letters alongside `r`/`n`/`b`/`m`/`v`. Both open over the map, so `Esc` pops back to it.

### Changed

- **`Ctrl+Alt+P` while the map is open now works like `p`**: the palette opens over the map instead of replacing it, so backing out returns you to the map. A second press closes it the way `Esc` does.

---

## [v0.3.0] - 2026-07-28

### Added

- **ASCII map style**: a terminal look for the whole board — each desktop a monospace box-drawing
  card, timelines joined by an ASCII spine, and a blinking block cursor on the desktop you're on.
  Pick it under **Settings → Appearance → Map style**, or cycle **board → metro → ASCII** with
  **`v`** on the map. Like the other styles it applies everywhere a board is drawn, and it's now
  the default for new installs (your saved choice is kept).
- **Metro-map style**: draw the whole desktop tree as a transit diagram — each timeline a coloured line, each desktop a station, a green "you are here" train marking where you stand. Turn it on in **Settings → Appearance** or flip it with **`v`** on the map; it's a whole-app choice that applies everywhere a board is drawn (the flash, the map, previews, the move flow), and the map stays fully interactive in it (click, switch, drag-rearrange). (See `docs/design/metro-map.md`.)

### Changed

- **The map no longer slides under you as you move.** Both map themes (board and metro) now
  render through one shared layout + camera, so they behave identically: navigating or
  moving the selection walks the cursor across a **stationary** map, and the map only pans —
  by the minimum needed, leaving a marker and a half of context — when the selection reaches a screen
  edge. Move back and it holds still. The board and metro views now align the same way (each
  timeline starts at its first desktop, joined by a spine on the left), and the transient
  flash shares the same camera, so opening the map lands exactly where navigation left it.
  (Design: `docs/design/scene-camera.md`.)
- **`Ctrl+Alt+M` now opens the map** instead of starting the move-windows flow. The map is the surface you reach for far more often, so it gets the dedicated hotkey; move-windows stays a keystroke away as **`m`** on the map. (A rebind you'd set for move-windows in an earlier version is preserved.)
- **Settings apply immediately.** The Save/Cancel buttons are gone — every toggle and rebind takes effect and persists the moment you make it. Close the window (or press Esc) when you're done.
- The settings window scrolls instead of overflowing when it's taller than the screen.
- The overlay's dimmed backdrop now carries a soft **vignette** — darker under the centred board, fading to the usual dim at the edges — so the board (and especially the metro map's thin coloured lines) keeps its contrast over a bright or busy desktop behind it. The transient navigation flash now shares this same backdrop, so it reads at the same weight as the full map.
- **"Show the board before moving" now applies only to diving and surfacing** (the up/down moves between branches), where landing among a fresh set of desktops is disorienting. Moving left/right within a row — which stays in view — now moves immediately instead of costing a reveal press.

### Fixed

- **A desktop switch now hands focus to the destination.** Jumping used to leave the desktop you came from as an invisible, cloaked foreground window that swallowed keystrokes and blocked other tools — single-instance apps, tray launchers, IDE reveal — from focusing their windows until you clicked something. Hypertree now activates a window on the desktop you land on, the way the OS's own switcher does.
- **Settings persist across restarts again.** The settings file is written with string-named enums, but was read back without the matching converter — so once any enum value was present in it (now always, since the map style is stored there) the whole file failed to parse and *every* setting silently reverted to its default on the next launch. Reads now use the same options as writes. (This latently affected saved hotkey rebindings too.)

---

## [v0.2.2] - 2026-07-27

### Added

- **Animated navigation moves**, on by default: a soft gradient wipe passes in the direction you moved — left/right along a row, up/down to dive or surface.
- **Peek at the board** with `Ctrl+Alt+Space` (rebindable): raises the flash where you are and holds it while the modifiers stay down, without moving.
- Pick which edge the wipe starts from in **Settings → Navigation** ("Sweep from the leading edge").
- Turn the wipe off in **Settings → Navigation**; it also follows the Windows *Show animations* setting.

---

## [v0.2.1] - 2026-07-27

### Added

- **Show the board before moving**, on by default: the first `Ctrl+Alt+Arrow` raises the board instead of moving — keep the modifiers held and press again to jump.
- Turn it off in **Settings → Navigation** to move on every press, as before.

---

## [v0.2.0] - 2026-07-27

### The command line

- `htree` drives Hypertree from any terminal, installed alongside the tray and on your PATH.
- `htree status` prints where you are, as `branch/desktop` — cheap enough to sit in a shell prompt.
- `htree list` shows the stack top to bottom, main in its slot, each row at its resume desktop; `--all` expands every desktop.
- `htree goto my-branch` jumps to a branch, `htree goto my-branch/docs` to a desktop on it. Names match by unique prefix; an ambiguous one is refused, not guessed.
- `htree watch` streams your position as it changes, one line per move.
- `--json` on any command, and exit codes on all of them: 0 done, 1 no tray, 2 unknown target, 3 bad usage, 4 tray refused.

### Changed

- Branches carry a stable id, so a jump can't be misdirected by the stack being reordered underneath it. Existing branches are given one on first launch.
- Hypertree publishes its layout and position to `%APPDATA%\hypertree\status.json` for other tools to read.
- Uninstalling removes the PATH entry it added.
- `HYPERTREE_STATE_DIR` relocates the state directory, for a portable install.

### Fixed

- The taskbar pill follows desktop switches made outside Hypertree — `Win+Ctrl+←→`, Task View — instead of showing where Hypertree last left you.
- Starting up inside a branch shows that branch, not the first desktop on main.

---

## [v0.1.5] - 2026-07-27

### Fixed

- In the map, `↑`/`↓` land on each row's own desktop — the one you last selected there, or the branch's resume point.
- A row the selection steps off keeps its place instead of sliding back to the desktop you're on.

---

## [v0.1.4] - 2026-07-27

### Fixed

- `Ctrl+Alt+Arrow` steps from the desktop you're actually on, even when another app switched you there.
- The flash board centres on that desktop too, instead of on where Hypertree last left you.

---

## [v0.1.3] - 2026-07-26

### Added

- `Shift+↑↓` on the map moves the selected branch up or down the stack.
- `Ctrl+←→↑↓` on the map moves the selected desktop along its row, or into the row above or below.
- Or drag: a desktop by its tile, a branch by its box, with a separator line marking where it lands.
- A branch moved across the main timeline re-slots main with it.
- A desktop dropped on the main timeline keeps that position; the OS desktop order is updated to match.
- Taking a branch's last desktop dissolves the branch, the same as deleting it.

---

## [v0.1.2] - 2026-07-26

### Changed

- Branches you're not in drop the "· resting" tag on the map; their boxes no longer stretch to fit it.
- Desktops are optional on the branch card — a blank list gives one desktop called "default".
- The delete-desktop confirm names the branch a desktop sits in, and says when removing it takes the branch too.

---

## [v0.1.1] - 2026-07-26

### Changed

- One Hypertree per session — a second launch opens the running copy's command palette instead of starting a rival.

---

## [v0.1.0] - 2026-07-26

The first cut of Hypertree — virtual desktops you can dive into and surface out of.

### Navigation

- `Ctrl+Alt+↓` dives into a branch, `Ctrl+Alt+↑` surfaces back out — landing exactly where you left off.
- `Ctrl+Alt+←/→` moves along the current row.
- The board flashes on every jump and holds while you keep the chord down, so you can see where you landed before it fades.
- Hypertree remembers the desktop you came from and floats it to the top of the jump list, so hopping back is one keystroke.

### Branches

- A branch is a named set of desktops that hangs *below* an anchor desktop — your day-to-day desktops stay on the usual horizontal axis.
- Build one from the branch card, typing its name and the desktops it holds.
- Only desktops Hypertree created are ever torn down; your own desktops are never touched.

### Map & overlays

- `Ctrl+Alt+P` opens the command palette — jump to a desktop, open the map, manage templates and layouts, reach settings, or quit.
- The map shows the whole arrangement at a glance, with blue marking where you are; every card previews what its action would do before you commit.
- On the map: `r` renames a desktop, `n` adds one, `Del` removes a desktop and `Shift+Del` removes a whole branch — each behind a confirm.
- `Ctrl+F` opens the finder to jump to any desktop, or create one named whatever you typed.

### Move windows

- `Ctrl+Alt+M` picks up the windows on the current desktop; navigate to the destination and drop them there in one move.

### Layouts & templates

- Save the whole desktop-and-branch arrangement as a named layout and restore it later; "Reset to a single desktop" wipes back to a clean slate.
- Reusable branch templates pre-fill the desktop set when you create a branch, so you stop retyping the same recipe.

### Settings & tray

- Lives in the tray: left-click for the command palette, right-click for the menu, with the running version in the header.
- Settings covers start-on-login, the desktop-name pill over the taskbar, and rebinding every global hotkey — click a shortcut and press the new combination.
- A persistent pill names the desktop you're on (in the branch's colour inside a branch) and auto-hides near the cursor so the taskbar stays clickable.

### Plumbing

- Branches, layouts and settings persist under `%APPDATA%\hypertree`; a missing or corrupt file falls back to defaults rather than blocking startup.
- Tray-only lifecycle — Hypertree outlives its windows and only ever closes on an explicit exit.
