# Hypertree refactoring roadmap

A prioritized plan from a code-quality/maintainability review of the codebase (~17.6k lines of C#
across 115 files). The domain logic and Win32/COM interop are mostly *correct* and unusually
well-commented — the problems are **structural**: four God classes, copy-pasted invariants (several
already drifted into latent bugs), and almost no seams above the Core layer.

Guiding principle for the whole effort: **behaviour-preserving refactors, smallest safe diffs, lean on
the existing Core test suite** (`tests/Hypertree.Tests`, ~4.6k lines). Each step should leave the build
green and the app running identically.

---

## Tier 1 — The four God classes (biggest maintainability drag)

| File | Lines | Responsibilities crammed in |
|---|---|---|
| `src/Hypertree.App/App.axaml.cs` | 2,227 | ~12: startup wiring, desktop+monitor watching, control pipe, navigation, gesture polling, history, spatial-map edits, move/pull, palettes, layouts, templates, settings, updates, tray, teardown |
| `src/Hypertree.Core/Scopes/NavigationModel.cs` | 905 | ladder + branch CRUD + OS reconciliation + persistence + **4 projection builders** |
| `src/Hypertree.App/Views/SpatialOverlay.cs` | 833 | controller + gesture/drag engine + selection state machine + all view construction (24-row legend) |
| `src/Hypertree.Platform.Windows/WindowsWindowLayoutController.cs` | 399 | monitor topology + display-config marshalling + window filter + placement + 2 diagnostic surfaces |

**`App` is the worst offender.** ~30 mutable nullable fields guarded by **61 `if (_x is null) return;`**
statements — a symptom of lazy field-init instead of injected collaborators. The entire App layer has
**zero tests** as a result (contrast Core: `NavigationModel` alone has 709 lines of tests).

### Step 1 — ✅ DONE (no behaviour change)
Split `App.axaml.cs` into `partial` files along its existing `// ──` banner regions, to expose the
seams before extracting types. **`App.axaml.cs`: 2,227 → 712 lines.** Each extraction was a separate
commit, compiled clean (0 warnings/errors), tests stayed green (291 pass). Partials created:
- `App.MonitorLayout.cs` (241) — monitor restore + debug/trace
- `App.Navigation.cs` (225) — nav gesture flow + breadcrumb history
- `App.Layouts.cs` (222) — implode/reset + snapshot save/restore
- `App.Settings.cs` (167) — settings apply/save, map-style toggle, switcher plumbing
- `App.Branches.cs` (250) — branch definition, templates, desktop delete/teardown helpers
- `App.Map.cs` (467) — map open/close, group picker, move/pull, manage-map actions, spotlight, preview
- `App.CommandPalette.cs` (111) — command palette toggle/show + `BuildCommands` registry
- (pre-existing: `App.Launcher.cs`)

What remains in `App.axaml.cs` (712) is a coherent "app shell": fields, lifecycle
(`Initialize`/`OnFrameworkInitializationCompleted`/`Startup`/`Teardown`), the outside-world surface
(watcher/status/control pipe), changelog, hotkeys, updates + notification plumbing, tray/notifier.

> **Note:** the Updates methods were left in the shell file — they interleave with cross-cutting
> notification plumbing (`Notify`/`OnNotificationActivated`/`OnUi`) that genuinely belongs to the shell,
> so they'll be teased out properly in Step 2 as an injected `UpdateController` rather than a partial.

### Step 2
Extract types with **injected dependencies** (not just partials — real seams):
- `MonitorLayoutController` (takes `MonitorLayoutService` + `INotifier`)
- `UpdateController` (takes an update source + `INotifier` + settings)
- `SpatialOverlayPresenter` (owns the ~15 `_spatialOverlay.XxxRequested +=` subscriptions and the
  `DesktopId`→`DesktopSelection` resolution at `App.axaml.cs:240-283`)

### Step 3 — `NavigationModel`
- Extract the 4 projection builders (`BuildMap`/`BuildSpatialSource`/`BuildStatus`/`CaptureSnapshot`)
  into a `NavProjection` mapper over an immutable read model.
- Extract the OS-sync trio (`Reconcile`/`Resync`/`AnchorToCurrent`) into a `NavSync` collaborator.
- Fix the misleading doc: it claims "holds no Win32/UI, fully unit-testable" but `Commit` calls
  `_desktops.SwitchTo`. Either correct the doc or separate pure-cursor-computation from OS-commit.

### Step 4 — `SpatialOverlay`
- Extract the pointer-drag gesture engine (`_grab`/`_dragging`/`_pressAt`/... at `:483-566`) into a
  `RoomDragController`.
- Move legend + groups-panel construction into a `SpatialOverlayChrome` builder; render the 24 legend
  rows from a `(string key, string desc)[]` table in a loop, not 24 imperative `Add` calls.

### Step 5 — `WindowsWindowLayoutController`
- Split into `MonitorTopology` (enum + stable-id map + its structs/DllImports),
  `WindowPlacementApplier`, and move `RestoreTraced`/`Probe` diagnostics to a debug-only partial/type.

---

## Tier 2 — Duplicated invariants (copies that MUST stay identical, but nothing enforces it)

1. **Win32 window-enumeration filter copied byte-for-byte** — `IsCountableWindow`/`IsShellWindow`/
   `TitleOf`/`ProcessOf` + ~9 DllImports duplicated between `VirtualDesktopController.cs:143-177` and
   `WindowsWindowLayoutController.cs:295-355`. The "window counts match layout captures" invariant
   depends on these never diverging. → Extract `internal static class NativeWindows`.
2. **The core row-order splice** (`branches[0..slot] / MAIN / branches[slot..]`) hand-rebuilt **6×**
   across `NavigationModel` + `SpatialSnapshot`; the branch-index↔row mapping has **3 copies with
   divergent clamping**. → One `RowsInDrawOrder()` enumerator + one `RowOfCursor()`/`CursorForRow()` pair.
3. **Topmost / click-through / tool-window P/Invoke** copied across 5 window classes (`HudWindow`,
   `OverlayStage`, `SwitcherWindow`, `TaskbarLabel`, `RestoreCurtain`). → Consolidate into `WindowFx`.
4. **Five prompt-card classes** duplicate the entire card scaffold + an identical arrow-key focus
   handler, and have already drifted (widths 360/380/440). → Abstract `CardContent` base.
5. **Scene abstraction bypassed** — `SpatialPainter` doesn't implement `IScenePainter` and re-codes all
   three themes by hand; colour math in 3 copies, hit-cell in 4. → Give `IScenePainter` a `DrawGlyph`
   both renderers share.
6. **Persistence mapping** (`Branch`↔`PersistedBranch`↔`PersistedDesktop`) hand-written 3×; the
   `Save(); Changed?.Invoke();` pair copy-pasted at ~12 mutation sites (some omit it, undocumented).
   → `Branch.ToPersisted()`/`FromPersisted()` + a private `Mutate(change)` wrapper.

---

## Tier 3 — Latent bugs surfaced by the review (fix regardless of refactoring)

- **State directory splits under redirection.** `HYPERTREE_STATE_DIR` is honoured only by `StatusFile`;
  the other four stores (`state.json`/`settings.json`/`spatial.json`/`snapshots.json`) compute
  `%APPDATA%\hypertree` inline. Also those four lack `StatusFile`'s atomic temp-file+move, so a crash
  mid-write corrupts `state.json`. → Shared `JsonFileStore<T>` / `StateDirectory` resolver.
- **`Args.UnknownFlags` is dead code** — the documented typo guard (so `--jsonn` errors instead of
  silently emitting human output) is never called.
- **`SettingsWindow.CurrentSettings()` hand-copies 9 pass-through fields** (`SettingsWindow.cs:240`);
  any new `AppSettings` field this window doesn't edit gets **reset to default on the next toggle**.
  → Use a record `with`-expression.
- **`HString.Create` ignores the `WindowsCreateString` HRESULT** → silent empty desktop name.
- **`ControlClient.ReadLine` has no 64KB cap** (the server copy does) → unbounded buffering.
- **~20 blank `catch { }` blocks with no logging** in the interop/IPC layer — the one place the
  interesting bugs live. → A tiny `Diagnostics.Swallowed(ex, context)` sink + narrow the catch types.

---

## Tier 4 — Testability & lower-impact

- **CLI is untestable static-on-static** (`Commands.cs` reads `StatusFile`, calls static
  `ControlClient.Send`, writes static `Output`); `UpdateChecker` too. → Inject seams.
- **Style constants scattered across 14 files** with divergent names for the same hex. → A `Theme` static.
- **Primitive obsession**: `MoveDesktop(bool,int,int,bool,int,int)` + ad-hoc `(bool,int,int)` tuple.
  → `readonly record struct DesktopAddress`.
- **Naming trap**: `Teardown()` (app shutdown) vs `TearDown(Branch?)` (destroy branch desktops).
- Transient COM RCWs never released in `VirtualDesktopController` (minted every 250ms poll tick);
  class isn't `IDisposable`.
- Dead code: `SpatialPainter.TileBorder`, `SW_RESTORE`/`SW_MINIMIZE`, stale `<see cref="BoardView"/>`,
  a stale XML summary on `SpatialPainter`.
- **IPC has no version negotiation** despite a separately-PATH-installed `htree.exe` that goes stale
  after a Velopack update.

---

## Suggested sequence

1. `NativeWindows` extraction + shared `JsonFileStore` — smallest diffs, each kills a latent bug,
   covered by existing Core tests.
2. Break `App` into partials by banner region (Tier 1 Step 1), then extract
   `MonitorLayoutController`/`UpdateController` — unlocks testing the App layer at all.
3. `CardContent` base + `Theme` static — high-visibility Views cleanup, low risk.
4. Fold Tier 3 point-bugs in opportunistically as each file is touched.

**Leave alone** (genuinely well-factored): `SpatialHull`, `SpatialTidy`, `SpatialNavigation`,
`MapCamera`, `NavHistory`, `Branch`, and the `ComInterop`/`DISPLAYCONFIG` marshalling.
