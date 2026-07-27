# The metro map view

A transit-diagram rendering of the desktop tree — the "metro map" idea from
[`docs/ideas.md`](../ideas.md). It draws the *same* `NavMap` the board draws, so it's
a pure visual alternative, not a new data model.

> **Status:** working prototype, landed on the `metro-map` branch. Reachable, renders
> to PNG, and passes the suite — but see **Open questions** before taking it further.

## See it

- **In the app:** open the map (`Ctrl+Alt+P` → *Open map*), then press **`v`** to toggle
  between the board and the metro view. `v` again switches back. The legend shows the
  toggle.
- **As a PNG (no tray):** `dotnet run --project src/Hypertree.App -- --shot captures`
  writes `metro-top-row.png`, `metro-dived.png`, and `metro-busy.png` alongside the
  board shots. This is how the look was iterated — `DesignShot` renders `MetroView`
  directly over the real dark ground.

## The mapping

| Desktop-tree concept | Metro vocabulary |
|---|---|
| A timeline (main, or a branch) | A coloured **line** running horizontally |
| A desktop | A **station** — a donut tick on the line |
| The dive/surface (depth) axis | A neutral vertical **interchange trunk** at screen-centre |
| A branch's name | A **route badge** at the line's terminus |
| The desktop you're on | The green **"you are here" train** (a glowing core + a breathing halo) |
| The selection / jump target | A blue **focus ring** around the station |
| Window count | A faint tally above an occupied station |
| An empty desktop (0 windows) | A smaller, hollow "minor" station |

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
- **Keyboard-driven for now.** Arrow-select, Enter-to-switch, and keyboard rearrange all
  work (they run on the model, independent of the renderer). **Click and drag are
  board-only** — `MetroView` doesn't yet emit the `BoardLayout` hit geometry the map's
  pointer code needs, so in metro mode a fresh empty layout leaves them as no-ops.

## Open questions (for the morning)

1. **Entry point & persistence.** Is a `v` toggle on the map the right door, or should
   there also be a command-palette entry ("Open metro map")? Should the chosen style
   **persist** across sessions (an `AppSettings` field), and/or be settable as the
   default in Settings?
2. **Interaction parity.** Should metro grow click-to-select and drag-to-rearrange? That
   means `MetroView` emitting station/line hit rects (reuse `BoardLayout`, or a metro-
   native one) and a drag-caret treatment that fits stations rather than tile strips.
   Or is "beautiful read-only overview, keyboard to drive" actually the right scope?
3. **Branch colours should probably be stable.** Today a line's colour is its branch
   *index* mod the palette, so adding/removing/reordering branches can recolour existing
   lines. Feels wrong for a map you build spatial memory on — "the coral line" should
   stay coral. Options: persist a colour (or palette slot) per branch id; or derive a
   stable colour by hashing the branch id. Also: palette only has 8 entries — what past
   that, and is it colourblind-safe? Should main ever get a colour?
4. **The trunk's meaning.** It runs vertically through each line's *resume* station,
   matching the board's spine. Reads as "one central interchange corridor." Is that the
   right story, or should a branch visibly connect at a specific *anchor* station on main?
   (The data model has no per-branch anchor column today — branches hang off the centre.)
5. **Vertical centring** (see above) — overview-style whole-stack centring, or match the
   board's current-row centring for a seamless toggle?
6. **Window counts** — keep the faint number above each station, or is it noise on an
   overview? Alternative: encode occupancy as station size only.
7. **Long names & big trees.** Station labels can collide at the 156px station pitch if
   names are long (truncate? stagger above/below?), and a very tall tree can overflow
   vertically (scale-to-fit? scroll?). Not handled yet.
8. **Flash/HUD too?** The transient navigation flash still uses the board. Should the
   metro style extend to it, or stay map-only?

## Files touched

- `src/Hypertree.App/Views/MetroView.cs` — the renderer (new).
- `src/Hypertree.App/Views/MapOverlay.cs` — the `v` toggle, `MapStyle`, legend row.
- `src/Hypertree.App/DesignShot.cs` — three metro `--shot` captures.
