# Changelog

All notable changes to Hypertree are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

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
