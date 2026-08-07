# Spatial map — phased implementation plan

Branch: `spatial-map`. Concept & interactive mockup: [`spatial-mode.html`](./spatial-mode.html)
(also published as an artefact). This plan turns that mockup into a shipping feature.

## The idea in one paragraph

Today the map is a **stack of rows** — `main` in the middle, branches hung above and below, each a
horizontal timeline where *position is structure*. Spatial mode adds a second, equal map **model**:
desktops become **rooms** placed anywhere on a 2-D grid, and organisation moves into **groups** — a
named, stable-coloured set of rooms that is purely logical, together or scattered. The two models are
two lenses on **one underlying arrangement**; `Tab` swaps between them and the user keeps whichever
they prefer.

## The load-bearing architectural insight

**Branches already are groups.** In `NavigationModel` a desktop is either in a `Branch` (grouped) or on
the `main` timeline (ungrouped). That is exactly the group model the mockup asks for, with `main` as the
"default bucket". So spatial mode does **not** need a new organisational data model — it only needs to
*add* two facts on top of the existing one:

1. a **stable colour per group** (per branch id), and
2. a **2-D grid position per desktop** (per OS desktop GUID).

Both are keyed by ids the app already persists (`Branch.Id`, `DesktopId`), so they live in a **separate,
self-contained store** (`spatial.json`) that the row model never reads. This is the key risk-isolation
move: **early milestones cannot destabilise the working row view**, because they don't touch
`NavigationModel`, `Branch`, `Scene`, or the existing painters.

A clean three-way separation of concerns falls out of this:

| Concern | Owner | Persisted in |
|---|---|---|
| Structure & membership (which desktop is in which group) | `NavigationModel` / `Branch` (unchanged) | `state.json` |
| Spatial facts (group colour, desktop position) | new `SpatialState` + `ISpatialStore` | `spatial.json` |
| Which model/theme is showing | `AppSettings` (`MapModel`, `MapStyle`) | `settings.json` |

Move-in-2D, tidy, and delete-leaves-a-hole all touch **only** spatial facts (positions) — never
structure — so they can never corrupt navigation. Membership changes (create group, ungroup, assign)
reuse the existing `NavigationModel` mutation surface (`AddBranch`, `MoveDesktop`, …).

## Rendering: a parallel pipeline, not a fourth theme

The row pipeline is `NavMap → Scene → SceneLayout → MapCamera → IScenePainter` and is inherently 1-D
(rows). Spatial is 2-D, so it gets a **parallel** pipeline that reuses the primitives:

```
NavMap+SpatialState ─▶ SpatialScene ─▶ SpatialLayout ─▶ MapCamera ─▶ SpatialPainter
                       (rooms+groups)  (2-D world rects) (reused)     (rooms, hulls)
```

`MapCamera` is already a **per-axis** dead-zone follow, so it works unchanged in 2-D once `SpatialLayout`
exposes `WorldX() / WorldY() / SelectionRect` like `SceneLayout` does. `MapStyle` (Board/Metro/Ascii)
stays the **row theme**; a new `MapModel { Rows, Spatial }` chooses the *model*. "List" in the mockup ==
`MapModel.Rows` (rendered with whatever `MapStyle` theme is set).

## First-run behaviour (no migration pain)

Spatial state is **sparse**: a desktop with no stored position falls back to a **default layout** derived
from the row model (each group a row, `main` centred) — i.e. spatial mode initially *looks like the rows*,
then the user rearranges and only the deltas persist. Group colours default to the 8-colour palette
(the existing metro palette) by group index; `main` is a neutral near-white. So there is nothing to
migrate — an existing install opens spatial mode already sensibly laid out and coloured.

---

## Milestones

Each milestone is independently shippable and leaves `dotnet build` + `dotnet test` green.

### M0 — Core spatial data model + store  ✅ *(in progress)*
**Goal:** a UI-free, fully-tested foundation. No rendering, no interaction, no risk to the row view.

**Deliverables** (all in `src/Hypertree.Core/Spatial/`, namespace `Hypertree.Spatial`):
- `GridPos(int X, int Y)` — integer grid cell.
- `SpatialPalette` — the 8 group hexes + neutral `main` colour; `For(index)`.
- `SpatialState` (POCO: `GroupColors` by branch-guid-string, `Positions` by desktop-guid-string) +
  `ISpatialStore` / `FileSpatialStore` → `%APPDATA%\hypertree\spatial.json`, best-effort like the others.
- `SpatialSource` / `SpatialGroupSource` / `SpatialDesktop` — id-carrying structural snapshot (NavMap is
  deliberately id-free, so spatial needs its own).
- `SpatialScene` + `SpatialScene.From(SpatialSource, SpatialState)` — merges stored colours/positions over
  the defaults (default layout = row layout; stored wins).
- `AppSettings.MapModel` enum `{ Rows, Spatial }` (default `Rows`) + property.
- `NavigationModel.BuildSpatialSource(cameFrom?)` — thin id-carrying analog of `BuildMap` (the one small,
  additive touch to `NavigationModel`).

**Tests** (`tests/Hypertree.Tests/Spatial*`): store round-trip; projection defaults (palette-by-index,
neutral main, unplaced→row layout); stored position/colour override defaults; `MapModel` persistence.

**Exit:** build + all tests green; row view visibly unchanged.

### M1 — Read-only spatial render
`SpatialLayout` (2-D world rects from `GridPos`; `WorldX/WorldY/SelectionRect`) + `SpatialPainter` drawing
rooms with the **Board tile look** (screen thumbnail, count pill, caption) and translucent **group hulls**
(one per contiguous fragment) with a route-badge. `DesignShot` gains spatial captures for iteration. Still
non-interactive.

### M2 — List ⇄ Spatial swap + navigation
Wire `MapModel` through `OverlayStage`/`MapOverlay`; `Tab` + a segmented control swap models with an
animated transition; 2-D cursor navigation (arrows pick the nearest room in that direction); `Enter`
jumps; camera frames the selection in 2-D. Spatial layout is snapshotted so swapping back restores the
hand-placed arrangement.

### M3 — Placement & movement
Drag a room; drag a group (⇧-drag / after `g`); keyboard hierarchy — arrows navigate, `Ctrl`+arrows move
the room, `Ctrl+⇧`+arrows move the contiguous **block**; snap-to-grid; positions persist to `spatial.json`.

### M4 — Groups & stable colours
Groups panel + palette picker (`⇧G`); `g` cycles group selection; **create group from a selection**
(lasso), assign/ungroup a desktop → `main`. Colours are stable across add/remove/reorder (keyed by branch
id). Membership changes go through `NavigationModel`; colour/position through `SpatialState`.

### M5 — Delete + Tidy
`Del` removes a room and **leaves the hole** (drop its position entry, no reflow) with a fading ghost;
`⇧Del` removes a group. **Tidy** (`t`): detect fragments, magnet them together **as rigid blocks** (shapes
preserved), pack groups non-overlapping; animated and reversible. Anchor = largest fragment (see open Q1).

### M6 — Polish & integration
**Done:** Settings → Appearance **Map model** selector (List / Spatial), which also fixed a latent reset of
the persisted `MapModel` on any settings save; the open map re-presents in the chosen model when it changes
(immediately, or when the Settings window closes). Stale room positions are pruned from `spatial.json` when
the spatial map opens. README + CHANGELOG updated.

**Deferred (cosmetic, tracked):** the tidy/magnet **animation** (moves are instant today — the geometry is
correct, the tween is polish and can't be eyeballed headlessly), the **delete ghost** outline, **spatial card
backdrops** (a confirm/prompt over the spatial map still shows the row board behind it), a **colourblind**
audit of the palette, and **flash/HUD** rendering the spatial board on a bare-desktop navigation. None change
behaviour; each is a look-and-feel pass best done against the live app.

---

## Post-review refinements (from live use)
- **Roomier grid** and **whisper-faint hulls** (the grouping should not colour-wash the rooms).
- **Drag** moves the room's visual *host* directly rather than re-rendering the board each cell-crossing — a
  re-render mid-drag dropped the pointer capture, which made the room snap back on release.
- **Overlaps are indicated, not auto-resolved.** An earlier version shoved neighbouring rooms to free cells
  after every move, which made keyboard moves across other groups scatter the map. Now a move never displaces
  anything; if two rooms share a cell the map shows an amber **!** marker on that room. (The auto-resolve
  helper `SpatialPlacement` was removed.) A future *manual* "resolve overlaps" command could layer back on top.

## Open questions (deferred, agreed to revisit)
1. **Tidy determinism** — tidy currently anchors the largest fragment in place. A remembered per-group
   *home anchor* would make tidy land a group in the same spot every time. Revisit in M5.
2. **Ungroup / lasso interaction** — drag-a-room-out-of-a-hull vs. an explicit key; and lasso-to-create.
   Land the plumbing in M4, refine the gesture later.
3. **Palette size / colourblind safety** — 8 slots today; what past 8, and is it safe? M6.

## Testing posture
Core (M0, and the geometry of M1/M5 tidy) is pure and unit-tested against hand-built sources and the
existing `FakeDesktopController` — no Avalonia. Rendering/interaction (M1–M5 UI) is exercised via
`DesignShot` PNG captures under `captures/`, the same way board/metro were iterated.
