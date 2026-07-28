# Scene, layout & camera — one movement model for every map theme

## Why

The map is drawn two ways — the tile **board** (`BoardView`) and the transit-diagram
**metro** (`MetroView`) — and they "feel" different even though they draw the same
`NavMap`. That's because each renderer independently owns three things:

1. **Row ordering** — branches above `TopPosition`, then main, then the rest.
2. **Horizontal placement** — *both* re-centre every row on its own cursor, so the
   selected desktop is pinned to screen-centre and **the map slides under you** as you
   move along a row.
3. **Vertical placement** — and here they *diverge*: the board pins the current row to
   centre and scrolls; metro centres the whole stack and never scrolls.

Duplicated where they agree, divergent where they don't. This document describes the
shared pipeline that replaces all three, so both themes move identically and only the
*pixels* differ.

## The behaviour we want

- **Moving the selection moves the cursor, not the map.** Arrow-select (or Ctrl+Alt+Arrow
  navigation) walks the blue cursor across a *stationary* map.
- **The map follows only when it must.** If the selection would land off the screen — or
  within a marker and a half of the edge — the map pans just enough to keep it in view
  with that much context beyond it. Moving *back* the other way does **not** pan while the
  selection is still comfortably on screen. (A dead-zone / scrolloff camera, with
  hysteresis — the map doesn't lurch back and forth.)
- **Both axes.** Horizontal (along a timeline) and vertical (dive/surface between
  timelines) behave the same way.
- **One camera, everywhere.** The transient flash and the interactive map share the same
  camera state, so navigating with the map closed leaves it framed where the map will
  open, and toggling board↔metro doesn't teleport.

## The pipeline

```
NavMap ──▶ Scene ──▶ SceneLayout ──▶ MapCamera ──▶ world→screen ──▶ Painter ──▶ Canvas
          (order)    (world rects)   (offset)       (transform)     (pixels)
```

Everything left of the painter is **pure geometry with no Avalonia dependency**, so it
lives in `Hypertree.Core` and is unit-tested against synthetic metrics. The painter and
the driver live in `Hypertree.App`.

### Scene (Core) — the normalised structure

Built from `NavMap` in *one* place, so row ordering is no longer duplicated:

- `Scene(Rows, SelectionRow, SelectionCol)`
- `SceneRow(Kind, BranchIndex, Name, Active, Cursor, Cells)` — `Kind` is `Main`/`Branch`.
- `SceneCell(Label, Selected, Here, WindowCount)`

Rows are already in draw order (branches-above / main / branches-below), so a row's list
index is its stack position — the same contract the old `BoardLayout.Rows` had.

### SceneMetrics (Core) — theme sizing, just numbers

The painter supplies the numbers the layout needs; the *algorithm* is shared.

- `CellStride` — distance between adjacent cell centres along a row.
- `CellWidth`, `CellHeight` — the drawable extent of a cell (for hit-testing / framing).
- `RowPitch` — distance between adjacent row centres (uniform; see "Uniform pitch").
- `RowHeight` — a row band's height (for hit-testing / framing).

### SceneLayout (Core) — world coordinates

- **Horizontal:** rows align at their **first desktop** — cell `(r, j)` has centre
  `x = j · CellStride`, so column 0 shares a world column across every row. (Rows no
  longer re-centre on their cursor; that's what kept the map still.)
- **Vertical:** rows stack on a **uniform** `RowPitch`; row `r` centre `y = r · RowPitch`.
- **Spine:** a vertical connector through the shared column-0 world column, joining
  consecutive rows — the left-side successor of the old centre trunk.
- Exposes the **selection's world rect** (from `SelectionRow`/`SelectionCol`) — the one
  thing the camera needs — and every cell/row world rect for hit-testing.

### MapCamera (Core) — dead-zone follow

State: a world **offset** per axis (`screen = world + offset`). One update rule per axis,
given the selection's world span `[lo, hi]`, the viewport length `view`, the content span,
and a `margin`:

- **Fits:** if the whole content span ≤ `view`, centre it and pin — never follow.
- **Follow:** otherwise keep `[lo − margin, hi + margin]` inside the viewport by panning the
  *minimum* needed; if it's already inside, **don't move** (the dead zone).

`margin` = **1.5 markers** — `1.5 · CellStride` horizontally, `1.5 · RowPitch` vertically —
capped per axis to `(view − selectionSpan) / 2` so the dead zone stays satisfiable on a tight
viewport (beyond that it degrades to centring the selection). The camera is created once and
updated in place, so the "don't move unless needed" state persists between renders — that's
the hysteresis.

### Painter (App) — pixels only

`ISceneRenderer`: `Metrics(scale)` and `Paint(cell|rowDecor|spine, screenRect, …)`. Two
implementations — `BoardPainter` (tiles, captions, branch boxes) and `MetroPainter`
(routes, stations, chips, badges). The driver `SceneRenderer` runs the Core pipeline,
applies the camera offset, calls the painter per element, attaches the shared click/drag
hit-cells, and emits the existing `BoardLayout` (screen-space) so `MapOverlay`'s drag code
is unchanged.

## Decisions

- **Uniform row pitch.** The old board measured each branch box to stack rows; metro used a
  fixed pitch. We standardise on a fixed pitch (a Q4 visual reconciliation), which both
  unifies the look and keeps `SceneLayout` pure arithmetic — no Avalonia measurement in the
  layout path.
- **Rows align at column 0, not at the cursor.** The direct consequence of "cursor moves,
  not map". The central interchange trunk becomes a **left spine**; this is a deliberate,
  visible change to both themes (metro most of all) — see metro-map.md open question #2,
  which this supersedes.
- **Camera is shared and central.** Owned above both surfaces so the flash and the map read
  and write the same offset. Stored per-axis in pixels for the current theme; re-framed on
  theme toggle by keeping the selection at the same screen point.

## Phasing

0. ✅ This doc.
1. ✅ Core: `Scene`, `SceneMetrics`, `SceneLayout` + tests.
2. ✅ Core: `MapCamera` + tests (the hysteresis).
3. ✅ App: `IScenePainter` + `BoardPainter`, board through the driver.
4. ✅ App: `MetroPainter`, metro through the driver.
5. ✅ Wire the shared `MapCamera` into `MapOverlay` + `HudWindow`.
6. Visual reconciliation across themes.
7. ✅ Remove dead code (`BoardView`/`MetroView`); update README/CHANGELOG and metro-map.md.

## Files

- `src/Hypertree.Core/Layout/{LayoutRect,Scene,SceneMetrics,SceneLayout,MapCamera}.cs` — new.
- `src/Hypertree.App/Views/Scene/{IScenePainter,SceneRenderer,BoardPainter,MetroPainter,BoardLayout}.cs` — new (BoardLayout moved here from the deleted BoardView).
- `src/Hypertree.App/Views/{BoardView,MetroView,MapSurface,MapOverlay,HudWindow}.cs` — migrated.
- `tests/Hypertree.Tests/{SceneLayoutTests,MapCameraTests}.cs` — new.
