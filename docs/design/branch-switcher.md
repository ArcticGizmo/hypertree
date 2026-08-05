# Branch switcher

A floating "click to switch" panel, borrowed from Perch's Hypertree integration strip and built into
Hypertree itself. It gives the mouse a first-class way to hop between branches, alongside the keyboard
(`Ctrl+Alt+↑/↓`), the map (`Ctrl+Alt+M`) and the finder.

## What it does

- A small draggable window listing **every row of the stack in map order** — main and each branch — one
  narrow line each.
- Each line trails the **desktop a plain click would land on** (the row's resume point), so the result of
  a click is legible before you make it.
- **Click a name** → jump to that row's resume desktop.
- **Click the desktop chip** on a row with more than one desktop → a small picker to choose which desktop
  to land on (the current resume point is ticked). A single-desktop row shows a plain label (no choice).
- The current row takes an **accent gutter bar** and a brighter, semibold name.
- **Header** (logo left, collapse chevron pinned right) is both the **drag handle** and the **collapse
  toggle**: click it to shrink to a lone logo **bubble**; click the bubble (or press the hotkey) to expand
  again.
- **`Ctrl+Alt+W`** toggles collapsed / full (rebindable in Settings → Hotkeys).
- **Right-click** the header (or the bubble) → **Exit Hypertree**.
- **Off by default** — Settings → Switcher → "Show the floating branch switcher".

## Where it lives

- `Hypertree.App/Views/SwitcherWindow.cs` — the window itself.
- `Hypertree.App/App.axaml.cs` — construction, `ApplySwitcher`, `JumpFromSwitcher`,
  `ToggleSwitcherCollapsed`, position/collapse persistence, overlay suppression, teardown.
- `Hypertree.Core/Settings/AppSettings.cs` — `ShowSwitcher`, `SwitcherCollapsed`, `SwitcherX`,
  `SwitcherY`.
- `Hypertree.Core/Platform/HotkeyBinding.cs` — `HotkeyCommand.ToggleSwitcher` (default `Ctrl+Alt+W`).

## Design decisions

- **Reads `NavigationModel.BuildStatus()`.** That already flattens the stack top-to-bottom with main in its
  slot — exactly the draw order the panel needs — and it's the cheap, count-free snapshot (unlike
  `BuildMap`, which walks every window for per-tile counts). The panel re-`Sync`s on every `Changed`, so
  external switches (Win+Ctrl+Arrow, Task View) update the "here" marker too, since those route through the
  ambient watcher → `AnchorToCurrent` → `Changed`.
- **Jumps via `NavigationModel.GoTo(branchId, desktop)`**, `Reconcile`-first, mirroring the map and the CLI
  `goto` path — a branch by its stable **id** (not list index, which shifts under reorders), main when the
  id is null, and the row's resume point when the desktop is null.
- **Takes clicks but doesn't steal focus.** Unlike `TaskbarLabel` (which is click-through,
  `WS_EX_TRANSPARENT`), the switcher must receive clicks, so it is *not* transparent. It sets
  `ShowActivated = false` and `WS_EX_NOACTIVATE` so merely appearing never grabs the foreground; a click
  activates it briefly, which is invisible because the jump switches desktop underneath it. It's a tool
  window (`WS_EX_TOOLWINDOW`, no taskbar/alt-tab), pinned to every desktop, and kept topmost by a slow
  relift timer — the same survival kit as the taskbar label.
- **Suppressed while the map overlay is up.** The map already shows the whole stack, and parking the
  switcher removes the topmost-z fight with the overlay stage (same reason the taskbar label parks).
- **Manual dragging in physical pixels.** Pointer press/move/release on the header drives `Position`
  directly, using `GetCursorPos` deltas so it survives the window moving under the pointer. A press that
  moves less than a few pixels is treated as a click (collapse toggle) rather than a drag.
- **Docks top-right until you move it, per state.** With no saved position it re-docks to the primary
  screen's top-right on every layout change; the first drag sets an explicit position, persisted to
  settings, and auto-docking stops. The **expanded panel and the collapsed bubble keep separate
  coordinates** (`SwitcherX/Y` vs `SwitcherCollapsedX/Y`), so each state lives where it suits you and
  dragging one never moves the other.
- **No drop shadow on the bubble.** On a window sized exactly to the 44×44 circle, a `BoxShadow` spilled
  into the transparent rounded corners and rendered as a smeared gradient; a solid fill plus the border is
  clean. (The panel dropped its shadow too, for the same reason and consistency.)
