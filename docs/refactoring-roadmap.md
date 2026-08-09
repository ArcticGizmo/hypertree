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

### Step 2 — injected types (real seams, not just partials)
- ✅ **`MonitorLayoutController`** — owns its `MonitorLayoutService` + poll timer + debug overlay; notifier
  read via `Func<INotifier?>` accessor (built later). **Smoke-tested & confirmed.**
- ✅ **`UpdateController`** — owns the check/apply flow, last-result state, and the tray update menu item;
  notifier + transient Settings window read via accessors; `dismissOverlay` callback for the one
  `_stage.Dismiss()`. ⚠️ **Needs a runtime smoke-test** (check / up-to-date / apply, tray retitle, Settings
  inline status). Also surfaced a pre-existing quirk: launcher-failure and update notifications share the
  `"update-check"` notice key (so they replace each other) — preserved and flagged, not changed here.
- ✅ **Spatial-map wiring** — extracted the ~45-line subscription block into `WireSpatialOverlay()` in
  App.Map.cs and deduped the resolution with `WithSelection(id, act)` / `WithBranch(guid, act)`.
  ⛔ **Deliberately did NOT create a `SpatialOverlayPresenter` class** as the review suggested: every handler
  is thin glue onto a distinct App command, so a presenter would carry ~18 delegates back into App —
  relocating coupling without a real seam (the param-obsession the review warns against). Conscious
  deviation, like `NavSync`. ⚠️ Map edits want a smoke-test.

`App.axaml.cs`: 712 → 557 with the two controllers + map wiring out.

**Step 2 complete** (as an injected-seam pass; two review items consciously declined where a class would
have relocated coupling rather than reduced it).

### Step 3 — `NavigationModel` (Core, test-covered)
Done so far (each a commit, 291 tests green throughout) — prioritized the duplicated-invariant
de-duplication first, since that's where the latent off-by-one risk lived:
- ✅ **One source of truth for row order + cursor⇄row mapping.** The "branches-above / main /
  branches-below" splice was hand-rebuilt in 3 places and the cursor↔row bijection in 3 more (two using
  raw `_mainSlot`, one a clamped slot — a divergence flagged in review). Collapsed to `RowOrder()` +
  `CurrentRow()`/`CursorToRow()`.
- ✅ **Single persistence mapper.** `Save` and `CaptureSnapshot` shared `ToPersisted()` helpers instead of
  two hand-written `Branch`→`PersistedBranch` projections.
- ✅ **`Resync` reuses `Locate`** instead of re-scanning (matches `AnchorToCurrent`).

> Note: the line count barely moved (905 → 911) — the win here is removing duplication and the
> divergent-clamping bug risk, not shrinking the file.

Then (done — completing the step):
- ✅ **Extracted `NavProjection`** — the 3 projection builders (`BuildMap`/`BuildSpatialSource`/`BuildStatus`)
  moved to a pure `NavProjection` over an immutable `NavLayout` read-model; the model keeps thin delegators
  so callers/tests are unchanged. NavigationModel 911 → 843; NavProjection 123. 291 green.
- ✅ **Fixed the misleading class doc** (it commands the OS via `IDesktopController`; it isn't side-effect-free).
- ⛔ **Deliberately did NOT extract `NavSync`** from `Reconcile`/`Resync`/`AnchorToCurrent`. Unlike the pure
  projections, those all mutate the same private cursor state; a separate class would force breaking
  `NavigationModel`'s encapsulation and reduce cohesion — following the review here would make it worse.
  Recording this as a conscious deviation from the review, not an omission.

**Step 3 complete.**

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

1. ✅ **Win32 window-enumeration filter copied byte-for-byte** — `IsCountableWindow`/`IsShellWindow`/
   `TitleOf`/`ProcessOf` (+ `ClassOf`) and their DllImports/constants were duplicated between the two
   controllers, held in sync only by a comment; the "counts match layout captures" invariant depended on
   it. Extracted to `internal static class NativeWindows`; each controller keeps only the imports it uses
   directly. Build 0/0, 295 tests green. ⚠️ Interop, no unit coverage — wants a light smoke-test (map
   window counts, move/pull pickers).
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

- ✅ **State directory split + non-atomic writes.** `HYPERTREE_STATE_DIR` was honoured only by `StatusFile`;
  the five file stores hardcoded `%APPDATA%\hypertree`, splitting a redirected/portable install's state
  across two dirs, and wrote in-place (crash mid-write could corrupt `state.json`). Fixed: one
  `StateDirectory` resolver + `WriteAtomic`, used by `StatusFile` (now a delegator, public API unchanged)
  and all five stores. Test-covered (`StateDirectoryTests`, +3). **Fully verified — no smoke-test needed.**
- ✅ **`Args.UnknownFlags` dead code.** The documented typo guard was never called; misspelled flags were
  silently dropped. Wired into `Program.Main` (per-command known set → `BadUsage`). Test-covered (e2e
  `status --jsonn` → exit 3, +1). **Fully verified.**
- ⏳ **`SettingsWindow.CurrentSettings()` hand-copies 9 pass-through fields** (`SettingsWindow.cs:240`);
  any new `AppSettings` field this window doesn't edit gets **reset to default on the next toggle**.
  → Use a record `with`-expression. (Remaining; Views/UI layer — not covered by the suite, so needs a
  smoke-test, unlike the two above.)
- ✅ **`HString.Create` ignored the `WindowsCreateString` HRESULT.** Now returns a clean null handle on any
  non-success HRESULT (explicit best-effort contract; throwing would crash the tray). Build-verified.
- ✅ **`ControlClient.ReadLine` had no 64KB cap** (the server copy did). Added the matching bound so a tray
  streaming an endless newline-less reply can't make `htree` buffer forever. Build-verified.
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
