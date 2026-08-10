# Hypertree refactoring roadmap

A prioritized plan from a code-quality/maintainability review of the codebase (~17.6k lines of C#
across 115 files). The domain logic and Win32/COM interop are mostly *correct* and unusually
well-commented — the problems are **structural**: four God classes, copy-pasted invariants (several
already drifted into latent bugs), and almost no seams above the Core layer.

Guiding principle for the whole effort: **behaviour-preserving refactors, smallest safe diffs, lean on
the existing Core test suite** (`tests/Hypertree.Tests`, ~4.6k lines). Each step should leave the build
green and the app running identically.

---

## 🧭 Handoff — where we are & what's next (read this first)

**Anchor:** branch `refactor`, **36 commits** ahead of `main` (tip `c917b4a`, 2026-08-10). Working tree
clean. Test suite: **295 pass** (was 291 at the start; +4 added by Tier-3 fixes). `App.axaml.cs` is down
from **2,227 → 557** lines.

**Done:** Tier 1 Steps 1–3 ✅ · Tier 2 items 1, 3, 4 ✅ (items 2 & 6 *partially* — Core half done, see
below) · Tier 3 five of six point-bugs ✅ · Tier 4 `Palette` ✅.

**Remaining, in recommended order** (full detail in "Where to go next" at the bottom):
1. **Tier 3 diagnostics sink** — smallest, unblocks visibility for everything else. Not yet started.
2. **Tier 2 item 5 — Scene `DrawGlyph`** — the deepest Views item; `SpatialPainter` re-codes all 3 themes.
3. **Tier 1 Step 4 — `SpatialOverlay`** split (drag engine + legend/chrome).
4. **Tier 1 Step 5 — `WindowsWindowLayoutController`** split (topology / placement / diagnostics).
5. **Finish Tier 2 items 2 & 6** (the non-Core halves) + the Tier-4 tail (primitive obsession, `Teardown`
   naming, COM RCW disposal, dead code, IPC versioning).

### ⚠️ Pending smoke-test (build-verified only — NOT yet run in a dev build)
The user last confirmed a smoke-test through the `SettingsWindow` fix. Everything since is compile-clean
and test-green but **unverified at runtime** (the App/Views layers have no automated coverage):
- **`Palette`** — visual only; every overlay / card / settings surface should look identical.
- **`WindowFx`** — the overlays: flash (click-through + topmost), taskbar label, switcher (clickable +
  on-top), restore curtain, stage dims.
- **`CardContent`** — the five cards (confirm/delete, rename & new-desktop prompt, new branch, new
  template, add/edit custom command): Esc, arrow focus between buttons, Enter / Ctrl+Enter, submit.

### Working conventions (so a fresh context matches what's been done)
- **Build check (App/Views/Platform):** a dev instance of Hypertree often holds the output DLLs locked, so
  build to a scratch dir instead of the repo `bin`:
  `dotnet build src/Hypertree.App/Hypertree.App.csproj -c Debug --output <scratch>/appbin`.
  Treat "only `MSB302x` copy-lock errors, no `CS` errors" as a clean compile; a redirected `--output`
  build gives a true `0 Warning(s) 0 Error(s)`.
- **Tests (Core + CLI):** `dotnet test tests/Hypertree.Tests/Hypertree.Tests.csproj -c Debug`. Note the
  test project does **not** reference `Hypertree.App`, so App/Views/Platform changes are build-verified
  only and must be smoke-tested.
- **Commits:** one focused commit per extraction + a follow-up commit updating this roadmap; end messages
  with the `Co-Authored-By: Claude Opus 4.8` trailer. Flag any runtime-unverified change for smoke-test in
  the commit body.
- **Conscious deviations from the review (do NOT "fix" these):** `NavSync` and `SpatialOverlayPresenter`
  were deliberately *not* extracted — both would relocate coupling / break encapsulation rather than
  reduce it (see Step 2 and Step 3 notes). The pre-existing shared `"update-check"` notice key between
  launcher-failure and update notifications was preserved, not changed.

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
2. 🟡 **The core row-order splice** (`branches[0..slot] / MAIN / branches[slot..]`). **Core half DONE**
   in Step 3: `NavigationModel`'s 3 copies + the divergent-clamping cursor↔row bijection collapsed to
   `RowOrder()` + `CurrentRow()`/`CursorToRow()`. **Remaining:** `SpatialSnapshot.SourceFrom` (Spatial/
   SpatialSnapshot.cs ~:50-52) still hand-rebuilds the same splice — fold it onto the same helper (it's a
   `static` method over a `Snapshot`, so either expose the enumerator or share a small free function).
3. ✅ **Topmost / click-through / tool-window P/Invoke** copied across 5 window classes (`HudWindow`,
   `OverlayStage`, `SwitcherWindow`, `TaskbarLabel`, `RestoreCurtain`). Consolidated into `WindowFx` as
   `SetClickThrough`/`SetNoActivate`/`SetToolWindow`/`LiftTopmost`; each class's wrapper now delegates
   (call sites unchanged), per-class constants + P/Invoke deleted. Build 0/0, 295 green.
4. ✅ **Five prompt-card classes** duplicated the card scaffold + IStageContent boilerplate + identical
   Esc/arrow key handlers + submit guard, and had drifted (Left/Right-only arrows in one, widths
   360/380/440, a stray #999 muted). → Abstract `CardContent` base owns all of it (+ Cancel/OK + a
   `ButtonRow` helper); subclasses now supply only their field stack + `FocusInitial`/`TryApply`.
   ~700 lines → 374 + a 136-line base. Custom-command arrows unified to Left/Up + Right/Down.
   Build 0/0, 295 green.
5. **Scene abstraction bypassed** — `SpatialPainter` doesn't implement `IScenePainter` and re-codes all
   three themes by hand; colour math in 3 copies, hit-cell in 4. → Give `IScenePainter` a `DrawGlyph`
   both renderers share.
6. 🟡 **Persistence mapping + mutation boilerplate.** **Mapper half DONE** in Step 3: `Save` and
   `CaptureSnapshot` now share `ToPersisted()` helpers. **Remaining:** the `Save(); Changed?.Invoke();`
   pair is still copy-pasted at **11 mutation sites** in `NavigationModel` (some mutators deliberately
   omit it — undocumented which). → A private `Mutate(Action change)` wrapper that runs the change then
   saves+notifies once; the deferring mutators opt out explicitly. (Behaviour-preserving but touches every
   mutator — do it as one careful, test-covered commit.)

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
- ✅ **`SettingsWindow.CurrentSettings()` silently reset unedited settings.** It hand-copied only 8
  pass-through fields, dropping `MapZoom`/`ShowMapLegend`/`PickerZoom` — so toggling any setting reverted
  the map/picker zoom and legend. Made `AppSettings` a record and used `_initial with { …edited… }` so
  every other field carries by construction. Build 0/0, 295 green. ⚠️ UI layer — smoke-test: change zoom/
  legend, toggle an unrelated setting, confirm they survive.
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
- ✅ **Style constants scattered across ~14 files** with divergent names for the same hex → consolidated
  into one `Palette` class (Ink/Muted/Accent/Here/Stroke/CardBg, as both `Color` and cached `IBrush`).
  Named `Palette` not `Theme` (Avalonia's `StyledElement.Theme` would shadow it). Build 0/0, 295 green.
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

## Where to go next (remaining work, recommended order)

The big structural wins are done. What's left is one bug-visibility item, two deep Views/Platform splits,
and a tail of smaller cleanups. Recommended order and how to approach each:

1. **Tier 3 — diagnostics sink** *(start here; smallest, unblocks everything else's field-debuggability)*.
   ~20 blank `catch { }` in the interop/IPC layer swallow the exact exceptions worth seeing. Add a tiny
   `Diagnostics.Swallowed(ex, context)` (write to `%APPDATA%\hypertree\` or stderr) and route the blank
   catches through it; narrow `catch (Exception)` to the expected types (`COMException`, `IOException`,
   `Win32Exception`, `UnauthorizedAccessException`) so real bugs still surface. Low risk, high payoff,
   partly test-coverable (the sink itself).

2. **Tier 2 item 5 — Scene `DrawGlyph`** *(deepest Views item; H1)*. `SpatialPainter` re-implements all
   three themes (board/metro/ascii) instead of participating in `IScenePainter`; colour math is copied 3×,
   the hit-cell 4×. Give `IScenePainter` a per-cell `DrawGlyph`/`PaintCell` both `SceneRenderer` and the
   spatial map call, and hoist `Lerp`/`Dim`/`BranchColour` + the hit-`Border` onto a shared
   `ScenePaint` helper (or an abstract `ScenePainterBase`). Intricate & visual — **needs a smoke-test** of
   all three map styles in both the row flash and the spatial map. Also clears the Tier-4 dead-code items
   (`SpatialPainter.TileBorder`, stale XML summary) while you're in there.

3. **Tier 1 Step 4 — `SpatialOverlay`** (833 lines). Extract the pointer-drag gesture engine
   (`_grab`/`_dragging`/`_pressAt`/… ~:483-566) into a `RoomDragController`; move legend + groups-panel
   construction into a `SpatialOverlayChrome` builder, rendering the 24 legend rows from a
   `(key, desc)[]` table in a loop. **Needs a smoke-test** (map drag, legend toggle, groups panel).

4. **Tier 1 Step 5 — `WindowsWindowLayoutController`** (now ~347 after `NativeWindows`). Split into
   `MonitorTopology` (enum + stable-id map + its structs/DllImports), `WindowPlacementApplier`, and move
   `RestoreTraced`/`Probe` diagnostics to a debug-only partial/type. Interop, no coverage — **smoke-test**
   dock/undock restore.

5. **Finish the partial items & the tail:** Tier 2 item 2 (`SpatialSnapshot` splice), Tier 2 item 6
   (`Mutate` wrapper), then Tier 4 — CLI seams (`IStatusSource`/transport/`TextWriter`), `DesktopAddress`
   record struct (kills `MoveDesktop`'s 6-bool-and-int signature + the ad-hoc tuple), the
   `Teardown()`/`TearDown()` naming trap, `VirtualDesktopController` RCW disposal + `IDisposable`, and IPC
   version negotiation.

**Leave alone** (genuinely well-factored): `SpatialHull`, `SpatialTidy`, `SpatialNavigation`,
`MapCamera`, `NavHistory`, `Branch`, and the `ComInterop`/`DISPLAYCONFIG` marshalling.
