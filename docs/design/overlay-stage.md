# Overlay Stage — one persistent surface for every overlay

## Problem

Each overlay was its own top-level `Window` that created and destroyed itself on every
appearance: `MapWindow`, `PaletteWindow`, `MoveWindow`, `HudWindow`, the `OverlayPrompt`
dialogs, and a per-monitor dim `Window` for each. Transitioning between them therefore meant
tearing one window (and its dim backdrop) down and building another up.

The visible symptom: choosing **Open map** from the command palette closed the palette (and its
dim), leaving a bare-desktop frame, before a brand-new map window (and new dim) appeared — a flash.
The same happens entering the move flow. Structurally there was also no seam on which to hang a
transition, and the dim/foreground/pin/topmost/cover-primary boilerplate was copy-pasted across
`MapOverlay`, `MoveWindowsOverlay`, and `OverlayPrompt`.

## Approach

A single persistent **overlay stage** owns the chrome; overlays become swappable **content**.

- **`OverlayStage`** owns one primary-monitor host window plus the per-monitor dim windows. They
  are created once, pinned to all virtual desktops once (so navigation's desktop switches never
  hide them), and shown/hidden rather than created/destroyed. Summon and dismiss are the only
  show/hide; everything *between* modes is a content swap on the already-visible host — no flash.
- **`IStageContent`** is what the stage presents: a built `Control` view plus policy flags
  (`Dim`, `DismissOnDeactivate`, `DismissOnClickAway`) and lifecycle hooks (`OnPresented`,
  `OnRemoved`, `OnKey`). The map, the palettes, and the move flow each implement it. The stage
  routes keyboard and background clicks to the current content per its flags.
- Presenting new content calls the outgoing content's `OnRemoved`, sets the host's content to the
  new view, toggles the dim, and calls `OnPresented` (focus, timers, thumbnail registration). If the
  host is already shown, that is the whole transition.

Behaviour preserved from the old per-window surfaces:
- Palettes dismiss when focus leaves the host (armed only after the foreground dance settles) and
  the centred-card palette dismisses on a click outside the card; the map/move do neither (they must
  survive the deactivation that a desktop switch causes).
- Force-to-foreground on summon (a tray hotkey doesn't grant focus), topmost re-lift after each
  navigation, dim backdrop.

This leaves a clean seam for future micro-transitions (cross-fade the content swap, slide the map
up on present, fly the move cards in) via a `TransitioningContentControl` or composition animations —
none are added yet; only the seam.

## Known limitation — DWM thumbnails don't animate with Avalonia

The move picker's live window previews are **DWM thumbnails**: the OS composites each source window
into a fixed destination rectangle *on top of* everything Avalonia draws there. They are not Avalonia
visuals, so they cannot ride Avalonia's animation/transform system. A "cards fly into frame"
animation would require driving every thumbnail's destination rectangle by hand each frame (a timer
updating `PlaceThumbnails`), and thumbnails can't be clipped to a region, so mid-animation they would
bleed past their card and over the header.

**Decision (accepted trade-off):** we do **not** animate the move-picker cards. Every other overlay
transition can animate freely through the stage; the thumbnail cards simply appear/disappear in place.
The live-preview fidelity is worth more than an entry animation, and hand-driven per-frame rect
updates aren't worth the complexity. If this is ever revisited, the two options are (a) swap thumbnails
for static captured bitmaps (which *are* Avalonia visuals and animate normally) during the transition,
then re-attach live thumbnails once settled, or (b) drive the rects from a timer and hide any card not
fully within its viewport (the same clipping guard `PlaceThumbnails` already uses for scrolling).

## Rollout

1. **Stage + map + palette** — stand up `OverlayStage`; map and palettes become content. Kills the
   command→map flash. (this change)
2. **Move overlay** — becomes stage content; summon and phase-1→phase-2 become content swaps.
3. **Later** — fold in the transient HUD flash and the `OverlayPrompt` dialogs (dialogs as a modal
   layer over the stage), then add the actual transitions.
