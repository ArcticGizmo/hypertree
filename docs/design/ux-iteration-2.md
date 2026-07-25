# UX iteration 2 — overlay, vertical model, spotlight & command palette

> Feature set requested after the M1.5 map/overlay work. Companion to
> [`IMPLEMENTATION.md`](../IMPLEMENTATION.md) (which gets a milestone pointer to this).
> Some items reshape the navigation model — those carry an **OPEN QUESTION** to confirm
> before building. Nothing here is built yet; this is the spec.

Current state it builds on: top row of ungrouped desktops + a fixed vertical stack of
groups (ladder nav), an interactive dimmed map on `Ctrl+Alt+Space`, a transient nav HUD,
per-tile delete, and APPDATA persistence.

---

## F1 — Standard navigation shows the *centred* map

**What:** regular hotkey navigation should present the board **centred on screen** (the
same presentation as opening the map), not the top-pinned strip added last round.

**Why:** consistency — one visual language for "where am I," whether transient or
interactive.

**Plan:**
- Collapse the transient HUD and the interactive overlay onto **one** presentation:
  full-screen dim backdrop + centred board.
- Two modes over that one view: **transient** (nav flash, auto-hides, click-through,
  non-interactive) and **interactive** (`Ctrl+Alt+Space`: stays open, focusable,
  clickable, pinned across desktops).
- Reuse the pinned-window + dim-backdrop machinery already in `MapOverlay`; the flash is
  the same board with a timer and click-through instead of interactivity.
- Removes the top-pinned `HudWindow` layout added in the last commit.

---

## F2 — Vertical model: main timeline in the middle, groups fixed around it

**What:** groups stay in their **listed order** (no reordering during navigation). The
**main timeline is the pivot**; `↑`/`↓` moves through a vertical sequence that runs
*through* the main timeline. From the example (currently on **feat‑2**):

```
        ┌───────────────────────────┐
        │  feat-1   (resting)        │   ← earlier group, above main
        ├───────────────────────────┤
        │  ▓ MAIN TIMELINE ▓         │   ← the day-to-day desktops (pivot)
        ├───────────────────────────┤
        │  feat-2   (current) ●      │   ← current group, below main
        └───────────────────────────┘
   ↑ from feat-2 → MAIN → feat-1     (you pass *through* the main timeline)
```

**Model — "main above current" (DECIDED):**
- State: `onMain` (bool) + `currentGroup` (index into the fixed list) + per-row cursor.
- Render: the current group sits directly **below** the main timeline; groups listed
  **before** it stack above main (in order); groups listed **after** it stack below the
  current group (in order). So for groups `[A,B,C]` with current `B`: `A / MAIN / B / C`.
- `↑`: in a group → go to the main timeline; on main → move to the *previous* group
  (`currentGroup-1`) and enter it. (no-op past the first group)
- `↓`: on main → re-enter the current group; in a group → go to the *next* group
  (`currentGroup+1`). (no-op past the last group)
- `←/→`: within the current row (main desktops, or the current group's desktops).
- The board is **centred on the current row's cursor** (keeps the existing per-row
  centring), and scrolls vertically so the current row is centred on screen.
- Accepted asymmetry: `↑ B → MAIN → A`, but `↓ B → C` goes straight (main not re-crossed).
  Main is always directly above the current group.

Minor decisions (defaults, not blocking):
- **On the main timeline** (`onMain`), `currentGroup` stays the last group you were in, so
  `↓` re-enters it — the "last-used nearest" idea, consistent with reorder-on-open.
- **Ends clamp** (no wrap) — matches the current ladder.
- This replaces the current `_level` ladder + `PrepareForMapOpen` reorder-on-open: with
  main-above-current the stack no longer reorders at all; main simply renders above the
  current group. (Keep persistence of `currentGroup` + per-group cursor.)

---

## F3 — No bounding box; use the full screen width

**What:** drop the card/box that frames the board. The timeline should span the **entire
screen width**, not be constrained inside a rounded panel.

**Plan:**
- Remove the `MapWindow` card `Border` (and the flash chip border) — render the board
  directly on the dim backdrop.
- `BoardView`: drop the fixed `viewportW` cap (currently ~6 tiles, clipped). Lay the row
  across the full screen width, still **centred on the current tile**; let rows longer
  than the screen extend under the edges (or scroll), rather than clipping inside a box.
- Footer controls (New group / Delete desktop / Remove) reflow to the bottom of the
  screen rather than inside the card.

---

## Shared: a `PaletteWindow` base (F4 & F5 look/feel)

Both F4 and F5 are the same **spotlight** control with different item lists, so build one
reusable base modelled on perch's `SessionSwitcherWindow`
(`perch/src/Perch.App/Windows/SessionSwitcherWindow.cs`). Replication checklist:

- **Chrome:** borderless (`WindowDecorations.None`), `Background = Transparent`,
  `TransparencyLevelHint = Transparent`, `Topmost`, `ShowInTaskbar = false`,
  `CanResize = false`, `Width ≈ 560`, `SizeToContent = Height`,
  `WindowStartupLocation = CenterScreen`. A single clipped rounded `Border` card
  (`CornerRadius 12`, 1px stroke, `ClipToBounds`) is the visible surface — rounded
  corners come from the transparent window + clipped border.
- **Layout:** `StackPanel { searchTextBox, ScrollViewer(list, MaxHeight ≈ 380), footerHint }`.
  Search box: rounded top corners only, bottom-border only, `FontSize 16`.
- **Filter:** `TextBox.TextChanged → ApplyFilter()`; empty query → all items; else
  case-insensitive `Contains`. Rebuild the list `StackPanel` each keystroke; keep rows in a
  parallel list for selection; reset selection to 0.
- **Keyboard:** `AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel)` so
  Up/Down/Tab/Enter/Esc win before the TextBox. Down/Tab → move +1 (modulo), Up/Shift+Tab
  → −1, Enter → choose selected, Esc → close. Focus the TextBox in `Opened`.
- **Focus grab (IMPORTANT):** a tray-hotkey window must *steal* keyboard focus, which
  Windows blocks for a background process. Perch does the `AttachThreadInput` +
  `SetForegroundWindow`/`SetFocus` dance (`WindowChrome.ForceForeground`,
  `perch/src/Perch.Platform.Windows/WindowChrome.cs:86`). **New work:** add an
  `IForegroundActivator.ForceForeground(nint hwnd)` to `Hypertree.Core/Platform` + a
  Windows impl. (Distinct from the HUD, which uses `WS_EX_NOACTIVATE` to *avoid* focus —
  the palette is the opposite.)
- **Dismiss:** `Deactivated → Close()` guarded by a `_ready` flag (armed after the
  foreground dance so it doesn't self-close on open); Esc; re-press the hotkey toggles.
- **Instance:** one reused field per palette; toggle-closed on re-press; clear on `Closed`.
- **Rows:** control-based (`Grid` + label + optional glyph), hover selects, click chooses,
  hand cursor. Selected-row background highlight + `BringIntoView`.
- Palette/fonts: reuse the board's dark colours so it reads as one app.

Generic shape: `PaletteWindow` takes a list of items with a display string + an `onChoose`
callback, and an optional **synthetic "create" row** (see F4).

## F4 — Spotlight: jump-to / create desktop (`Ctrl+Alt+P`)

**What:** a spotlight that filters **every desktop that exists** (top-row ungrouped +
every group's desktops) as you type; Enter/click **jumps** to it. If the query matches
nothing (or as an always-present last row), offer **"Create desktop «query»"**.

**Plan:**
- Items come from the model: each desktop as `{ label, kind }` where kind is either
  top-row index or `(groupIndex, desktopIndex)`. Include the group name in the match text
  (e.g. `feat-2 · SPA`) so typing a group name filters to its desktops.
- Choose → `GoToTop` / `GoToGroupDesktop` (reuse existing model methods), then close.
- **Create affordance:** always append a `Create desktop "«query»"` row when the query is
  non-empty and no exact-label match exists. Choosing it creates a new **ungrouped**
  desktop named the query (`IDesktopController.Create`), `SyncTopRow`, and jumps to it.
  (Creating *inside a group* is a later nicety; ungrouped-by-default keeps it simple.)
- Needs the focus-grab activator (palette must accept typing).

## F5 — Command palette (`Ctrl+Alt+Shift+P`)

**What:** same spotlight look/feel, but the items are **commands**, not desktops. This
iteration is just the **bones** — a command registry + wiring — with a few representative
(possibly stubbed) commands. The exact command set is not the point yet.

**Plan:**
- A `Command` = `{ Name, Run() }` in a small in-app registry. Filter by name; Enter runs it.
- Seed with representative commands (stubs where the backing feature doesn't exist yet):
  `New group…`, `Delete current desktop`, `Remove current group`, `Snapshot layout`
  (stub), `Add branch` (stub → M2 git), `Move desktop to group…` (stub). Reuse existing
  handlers where they exist; log "not implemented yet" for stubs.
- Later, commands become the single home for actions currently scattered across the map
  footer/tray — but that consolidation is out of scope for the bones.

---

## Hotkeys to add

| Chord | Action | Note |
|---|---|---|
| `Ctrl+Alt+P` | Spotlight (jump/create desktop) | add `HotkeyKey.P`; verify it registers |
| `Ctrl+Alt+Shift+P` | Command palette | Shift modifier already supported |

(Per the message; note the on-image sketch labelled these `Ctrl+Shift+P` /
`Ctrl+Shift+Alt+P` — using the message's `Ctrl+Alt(+Shift)+P`. Rebinding is M3.)

---

## Build order (milestone M1.6)

Grouped so each step builds + verifies (`--shot` for board changes, launch for hotkeys):

1. **F2 vertical model** (Core, pure + tested): replace the `_level` ladder with
   `onMain` + `currentGroup` "main-above-current"; update `BuildMap` to emit the vertical
   sequence (earlier groups, main, current group, later groups); drop `PrepareForMapOpen`.
   Rewrite/extend `NavigationModel` tests. *No UI yet — prove the transitions first.*
2. **F1 + F3 rendering:** one centred, full-screen, box-less presentation; `BoardView`
   lays the vertical sequence (main pivot) using full screen width, centred on the current
   cursor; delete the top-pinned `HudWindow` layout and the `MapWindow` card border. Flash
   = transient/click-through mode of the same view; interactive = pinned/clickable mode.
   Verify with `--shot`.
3. **Focus-grab primitive:** `IForegroundActivator` + Windows `AttachThreadInput` impl.
4. **F4 spotlight** (`Ctrl+Alt+P`): `PaletteWindow` base + desktop items + create row.
5. **F5 command palette** (`Ctrl+Alt+Shift+P`): reuse `PaletteWindow`; command registry
   with a few real + stubbed commands.

Risks / watch-items:
- **Full-width board** must still center the current tile and not overflow the screen
  horizontally in a broken way — clip or letterbox gracefully when a row exceeds the width.
- **Two focus policies coexisting:** HUD/board stay non-activating + click-through; the
  palettes must force-foreground and take focus. Keep them separate windows.
- `Ctrl+Alt+P` / `Ctrl+Alt+Shift+P` registration must be confirmed on this build (like the
  M0 chord matrix) — `P` isn't reserved, but verify FIRES, not just ACCEPTED.
