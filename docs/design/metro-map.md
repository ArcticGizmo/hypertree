# The metro map view

A transit-diagram rendering of the desktop tree — the "metro map" idea from
[`docs/ideas.md`](../ideas.md). It draws the *same* `NavMap` the board draws, so it's
a pure visual alternative, not a new data model.

> **Status:** working, on the `metro-map` branch. It's now a persisted, whole-app
> appearance setting with full interactive parity on the map — see **Settling in** for
> what changed from the first prototype, and **Open questions** for what's still open.

## See it

- **Turn it on:** **Settings → Appearance → "Use the metro map style"**, or press **`v`**
  on the map. It's a persisted, whole-app choice: once on it shows on *every* surface that
  draws a board — the flash, the interactive map, card previews, and the move flow — not
  just the main map. `v` (or the toggle) flips it back.
- **On the map:** fully interactive in metro — arrow-select, Enter to switch, click to
  select, double-click to jump, and drag to rearrange (a desktop by its station, a branch
  by its route badge). Keyboard rearrange (Ctrl/Shift+arrows) works too.
- **As a PNG (no tray):** `dotnet run --project src/Hypertree.App -- --shot captures`
  writes `metro-top-row.png`, `metro-dived.png`, and `metro-busy.png` alongside the
  board shots. This is how the look was iterated — `DesignShot` renders `MetroView`
  directly over the real dark ground.

## The mapping

| Desktop-tree concept | Metro vocabulary |
|---|---|
| A timeline (main, or a branch) | A coloured **line** running horizontally |
| A desktop | A **station** — a donut tick on the line, with a **label chip** below it |
| The dive/surface (depth) axis | A neutral vertical **interchange trunk** at screen-centre |
| A branch's name | A **route badge** at the line's terminus |
| The desktop you're on | The green **"you are here" train** — a glowing core, and the station's chip goes solid green |
| The selection / jump target | A blue **focus ring** on the station, and its chip goes solid blue |
| Window count | A tally inside the station's chip (on the opaque ground, so it stays legible) |
| An empty desktop (0 windows) | A smaller, hollow "minor" station and a dimmer chip |

Everything lives in [`src/Hypertree.App/Views/MetroView.cs`](../../src/Hypertree.App/Views/MetroView.cs).
It mirrors `BoardView.Render`'s signature and consumes the display map `MapOverlay`
already builds (selection → `IsCurrent`, actual position → `IsHere`), which is why the
`v` toggle needed almost no new interaction code.

## Decisions taken

- **Same spatial model as the board.** Lines stack in the same order (branches before
  `TopPosition` above main, the rest below), and each line is centred on its own cursor
  so the trunk is straight through every line's resume station — exactly where the board
  draws its spine. Toggling board↔metro therefore doesn't teleport anything.
- **…except vertical centring.** The board pins the *current* row to the screen centre
  and scrolls the rest; the metro view centres the *whole stack* instead. It reads as an
  overview — you find yourself by the green train, not by a fixed centre line — and it
  removes the large dead margin you'd get sitting on the top line. Deliberate divergence;
  flagged below in case it should match instead.
- **main is the light spine.** Branch lines take saturated palette colours; main is a
  near-white line and only half-fades when you're away from it, so it stays the
  recognisable "home" thread.
- **Glow, not just colour.** The line you're on gets a soft coloured `DropShadow` bloom
  and the train's core glows, so the active route reads as *lit*. This renders in the
  offscreen software path too, so `--shot` shows what you'll see live.
- **The train breathes.** Live only, a `DispatcherTimer` opacity pulse on the halo (the
  app's hand-rolled tween idiom, tied to the halo's visual-tree lifetime so it self-stops
  on re-render). Honours the OS reduce-motion preference.
- **Full interactive parity.** `MetroView` emits the same `BoardLayout` hit-geometry
  `BoardView` does — station "cells" tile the strip (each stride-wide, centred on its
  station), and each line's band runs out to its route badge, which is the branch's drag
  handle. So the map's existing pointer code drives metro unchanged: click-select,
  double-click jump, and drag-rearrange all work, with the drop caret landing on the
  mid-points between stations. Verified by `metro-drag-layout.png` (`--shot`), the metro
  twin of `board-drag-layout.png`. The one board affordance metro drops is the always-on
  `×` delete badge (too noisy on the clean diagram) — `Del` still deletes.

## Settling in (what changed after the first prototype)

The first cut was a map-only `v` toggle, keyboard-driven. Feedback was to make it a real
theme. So now:

- It's a **persisted `AppSettings.MapStyle`** (default `Board`), set in Settings →
  Appearance or with `v` (which just flips the setting).
- It applies **everywhere a board renders**, via `MapSurface.Render` (the dispatch point
  for non-interactive surfaces) and `OverlayStage.MapStyle` (the shared source of truth
  the stage, map, and move flow all read). The flash takes the style as a `Flash` argument.
- The interactive map gained **full click/drag parity** (see above), so metro is safe to
  live in as a default, not just a peek.
- The overlay dim became a **centre-weighted vignette** (`StageWindow.BuildDim`) — darker
  under the board, fading to the old flat dim at the edges. The host is semi-transparent
  over the live desktop, and a bright screen behind was washing out the thin metro lines;
  pooling the dark under the content restores contrast without making the overlay heavier.
  `metro-backdrop-{flat,vignette}.png` (`--shot`) show it over a faked busy desktop.
- **Desktop labels became chips** (`AddChip`), the same rounded pill as the branch route
  badge. Floating text over the semi-transparent dim was still low-contrast even with the
  vignette; an opaque chip guarantees a readable ground whatever's behind. Resting chips
  are dark and line-tinted (the line's colour as the text + a faint colour border);
  selected/here invert to a solid blue/green fill, so "dimmed unless selected" reads at a
  glance and matches the branch marker. The window count rides inside the chip.
- **Dimming is by colour, never opacity** (`Dim` fades a colour toward the ground). The
  overlay is semi-transparent over the live desktop, so a resting line/donut/badge drawn
  at reduced alpha let the desktop bleed through and got lost against a bright screen.
  Fading toward the ground keeps every element opaque, so a dimmed thing recedes but stays
  legible over anything behind. (Route badges keep a floor so their dark text still reads.)

## Open questions (still open)

1. **Branch colours should probably be stable.** Today a line's colour is its branch
   *index* mod the palette, so adding/removing/reordering branches can recolour existing
   lines. Feels wrong for a map you build spatial memory on — "the coral line" should
   stay coral. Options: persist a colour (or palette slot) per branch id; or derive a
   stable colour by hashing the branch id. Also: palette only has 8 entries — what past
   that, and is it colourblind-safe? Should main ever get a colour?
2. **The trunk's meaning.** It runs vertically through each line's *resume* station,
   matching the board's spine. Reads as "one central interchange corridor." Is that the
   right story, or should a branch visibly connect at a specific *anchor* station on main?
   (The data model has no per-branch anchor column today — branches hang off the centre.)
3. **Vertical centring.** The metro view centres the whole stack (overview); the board
   pins the current row. Deliberate divergence — keep it, or match the board so toggling
   is seamless?
4. **Long names & big trees.** Station chips can collide at the 156px station pitch if
   names are long (truncate? stagger above/below?), and a very tall tree can overflow
   vertically (scale-to-fit? scroll?). Not handled yet.
5. **A command-palette entry?** You can reach the style from Settings and `v`; a "Switch
   to metro / board map" palette command would be a third door. Worth it, or clutter?

## Files touched

- `src/Hypertree.App/Views/MetroView.cs` — the renderer (new), with click/drag geometry.
- `src/Hypertree.App/Views/MapSurface.cs` — style dispatch for non-interactive surfaces (new).
- `src/Hypertree.Core/Settings/AppSettings.cs` — `MapStyle` enum + persisted setting.
- `src/Hypertree.App/Views/SettingsWindow.cs` — the Appearance region.
- `src/Hypertree.App/Views/{OverlayStage,HudWindow,MoveWindowsOverlay,MapOverlay}.cs` — read/apply the style.
- `src/Hypertree.App/App.axaml.cs` — keeps the stage style in sync; passes it to the flash.
- `src/Hypertree.App/DesignShot.cs` — metro captures incl. `metro-drag-layout.png`.
