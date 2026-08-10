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

**Anchor:** branch `refactor`, **52 commits** ahead of `main` (tip `702f139`, 2026-08-10). Working tree
clean. Test suite: **311 pass** (was 291 at the start; +4 Tier-3 fixes, +4 diagnostics sink, +5 RowSplice,
+7 CLI commands). `App.axaml.cs` is down from **2,227 → 557** lines; `SpatialOverlay` from **833 → 575**;
`WindowsWindowLayoutController` from **347 → 149** (+ 3 partials).

**The planned refactor is essentially complete.** Tier 1 Steps 1–5 ✅ (all four God classes) · Tier 2 items
1–4, 6 ✅ (item 5 *partial* — colour/hit-cell dedup done, glyph merge consciously declined) · Tier 3 **all
six** ✅ · Tier 4 ✅ except two deliberate leave-alones.

**Remaining — nothing structural left; two documented non-goals + a runtime pass:**
1. **⚠️ Run the pending smoke-test batch** (below) — the biggest outstanding item. Six App/Views/Platform
   changes are build-verified + test-green but never run in a dev build (the App layer has no automated
   coverage).
2. **Two deliberate leave-alones** (full rationale in Tier 4): `VirtualDesktopController` RCW disposal
   (COM identity-unification crash risk, no coverage) and IPC version negotiation (a feature, not a
   maintainability refactor). Don't pick these up as routine cleanups.

### ⚠️ Pending smoke-test (build-verified only — NOT yet run in a dev build)
The user last confirmed a smoke-test through the `SettingsWindow` fix. Everything since is compile-clean
and test-green but **unverified at runtime** (the App/Views layers have no automated coverage):
- **`Palette`** — visual only; every overlay / card / settings surface should look identical.
- **`WindowFx`** — the overlays: flash (click-through + topmost), taskbar label, switcher (clickable +
  on-top), restore curtain, stage dims.
- **`CardContent`** — the five cards (confirm/delete, rename & new-desktop prompt, new branch, new
  template, add/edit custom command): Esc, arrow focus between buttons, Enter / Ctrl+Enter, submit.
- **`ScenePaint`** — all three map styles (board/metro/ascii) in both the row flash and the spatial map:
  cell/room colours, resting-timeline dimming, branch hues, and click (select) / double-click (activate)
  on cells and rooms. (Board is untouched; the shared bits are the metro/ascii/spatial colour + hit-cell.)
- **`SpatialOverlay` split** (`RoomDragController` + `SpatialOverlayChrome`) — on the spatial map: drag a
  room to a new cell; ⇧-drag its contiguous block; with a group selected (⇧G → click a row), drag any of its
  rooms to move the whole group; a sub-cell drag snaps back; plain click selects, double-click switches. And
  the chrome: the legend renders identically, `l` hides/shows it (small "l legend" pill when hidden), the `v`
  row names the next style, ⇧G groups panel — row-click selects the group (cursor jumps into it), a swatch
  opens the palette, picking a colour recolours the group.
- **`WindowsWindowLayoutController` split** — pure partial reorg (no behaviour change), but interop with no
  coverage: dock/undock (or add/remove a monitor) should still auto-capture the arrangement and offer a
  restore, and the monitor-placement debug overlay's "Restore" and "Trace restore → file" should still work.

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

### Step 4 — `SpatialOverlay` ✅ DONE (833 → 575 lines)
- ✅ **`RoomDragController`** — the pointer-drag state machine (press → drag → drop) lifted out; the overlay
  supplies a small callback seam (cursor room, drag-set resolver, host lookup, cell stride, commit/snap-back)
  and keeps the root's pointer wiring + hover. Line-for-line behaviour.
- ✅ **`SpatialOverlayChrome`** — legend (+ hint pill) and groups panel moved to a builder; the ~20 legend
  rows now render from a `(keycap, desc)[]` table in a loop (the `v` row computed from `MapStyle`). The
  interactive groups panel takes a `GroupsPanelCallbacks` record (select / toggle-palette / recolour) — a
  view/builder split, mutation stays in the overlay.
- Build 0/0, 299 green. ⚠️ **Runtime-unverified — smoke-test** (see the pending list): room drag / ⇧-block /
  group drag / snap-back, and the legend + `l` toggle + ⇧G groups panel + swatch recolour.

### Step 5 — `WindowsWindowLayoutController` ✅ DONE (347 → 149-line shell + 3 partials)
Split along its banner regions into partials (the App Step 1 playbook), not separate types:
- ✅ **`.cs`** (149) — the `IWindowLayoutController` production path: `Snapshot`/`Restore`/`Monitors` +
  `ApplyPlacement`/`CurrentStableId`/`ShowOf`.
- ✅ **`.Topology.cs`** (84) — `EnumMonitors`/`BuildStableIdMap`/`GdiNameOf` (the DISPLAYCONFIG stable-id chain).
- ✅ **`.Diagnostics.cs`** (74) — `RestoreTraced`/`Probe`/`RectOf`, the debug-overlay path.
- ✅ **`.Interop.cs`** (68) — every DllImport/delegate/struct/const, shared.
- ⛔ **Deliberately did NOT extract `MonitorTopology`/`WindowPlacementApplier` as separate types**: `RECT`
  (shared by `MONITORINFOEX` + `WINDOWPLACEMENT`) and `MonitorFromWindow`/`GdiNameOf` straddle enumeration
  and placement, so separate types would force shared internals + cross-type calls — real interop risk on a
  no-coverage surface. Partial split gets the seams at zero behaviour risk; type extraction is the follow-up.
- Also dropped the dead `SW_RESTORE`/`SW_MINIMIZE` consts (a Tier-4 item). Build 0/0, 299 green.
  ⚠️ **Runtime-unverified — smoke-test** dock/undock restore + the debug overlay's Restore/Trace.

---

## Tier 2 — Duplicated invariants (copies that MUST stay identical, but nothing enforces it)

1. ✅ **Win32 window-enumeration filter copied byte-for-byte** — `IsCountableWindow`/`IsShellWindow`/
   `TitleOf`/`ProcessOf` (+ `ClassOf`) and their DllImports/constants were duplicated between the two
   controllers, held in sync only by a comment; the "counts match layout captures" invariant depended on
   it. Extracted to `internal static class NativeWindows`; each controller keeps only the imports it uses
   directly. Build 0/0, 295 tests green. ⚠️ Interop, no unit coverage — wants a light smoke-test (map
   window counts, move/pull pickers).
2. ✅ **The core row-order splice** (`branches[0..slot] / MAIN / branches[slot..]`). Step 3 collapsed
   `NavigationModel`'s 3 copies + the divergent-clamping cursor↔row bijection; now the last copy —
   `SpatialSnapshot.SourceFrom` — is folded onto the same helper too. Extracted `RowSplice.Order(count,
   mainSlot)` (a pure static yielding branch indices with a `MainMarker` spliced at the clamped slot);
   `NavigationModel.RowOrder()` delegates (`MainRowMarker` forwards to it) and `SourceFrom` maps its indices.
   Test-covered (`RowSpliceTests`, +5). Core — fully verified, 304 green.
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
5. 🟡 **Scene abstraction bypassed** — `SpatialPainter` doesn't implement `IScenePainter` and re-codes all
   three themes by hand; colour math in 3 copies, hit-cell in 4. **The diagnosed duplication is DONE:** a
   new `ScenePaint` helper owns the shared branch palette + `BranchColour`, the opaque `Lerp`, the
   recede-toward-ground `Toward`/`Dim`, and the transparent hit-cell overlay (`HitCell`); Ascii/Metro/Spatial
   now delegate (−55 lines). Board stays out on purpose (fixed colours, click baked into the tile past its
   delete badge). Also cleared the Tier-4 dead code this touched (`SpatialPainter.TileBorder`, stale summary)
   and reconciled Spatial's truncating blend to Metro's rounding (≤1/255 per channel). **Deliberately did NOT
   merge the glyph bodies into a shared `DrawGlyph`** (the review's headline): the row cell and spatial room
   diverge on purpose (width 96 vs 140, stream/delete vs group tint, empty-opacity, ascii inner-width/font,
   metro station sizing), so one glyph method would change pixels or become param-soup — a conscious
   deviation, like `NavSync`. Build 0/0, 299 green. ⚠️ **Runtime-unverified — smoke-test** all three styles in
   both the row flash and the spatial map (colours, resting dim, branch hues, click/double-click).
6. ✅ **Persistence mapping + mutation boilerplate.** Step 3 gave `Save`/`CaptureSnapshot` a shared
   `ToPersisted()`; now the `Save(); Changed?.Invoke();` pair copy-pasted at **11 mutation sites** in
   `NavigationModel` is collapsed into one private `SaveAndNotify()`. Realized as a tail helper, not the
   review's `Mutate(Action)` body-wrapper: every mutator guards with an early return (a no-op must not
   save/notify) and several return values, so a body-wrapper would only fit the two unguarded ones. The
   opt-outs (no-op returns, the reconciling mutators, the ctor backfill) are documented at the helper.
   Behaviour-preserving; Core, 304 green.

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
- ✅ **~20 blank `catch { }` blocks with no logging** in the interop/IPC layer — the one place the
  interesting bugs live. Added `Hypertree.Diagnostics.Swallowed(ex, context)` — a locked, never-throwing
  sink that appends a timestamped record to `diagnostics.log` beside the rest of the state (rolls to a
  single `.1` past 128 KB). Routed ~19 silent swallows through it: every persisted read/write (state,
  snapshot, monitor-layout, spatial, settings, status) and the key interop failures (PATH register,
  startup Run key, app launch, AppsFolder enumeration, desktop reorder). **Conscious scoping:** left the
  high-frequency/expected catches (poll-tick monitor reads, per-subtree registry walk, advisory
  process-name lookup, already-gone deletes) alone to keep the log signal-not-noise; and kept the catches
  **broad** rather than narrowing the types as the review suggested — narrowing would change behaviour
  (unexpected exceptions would newly propagate) against a layer with no runtime coverage, and the log
  already surfaces the exception type + stack, so "real bugs still surface" is met via the record, not via
  propagation. Test-covered (`DiagnosticsTests`, +4: contents, append, rollover, never-throws). Build 0/0,
  299 green. **Behaviour-preserving — no smoke-test needed** (the App/Views layers were untouched).

---

## Tier 4 — Testability & lower-impact

- 🟡 **CLI is untestable static-on-static.** ✅ **`Commands` DONE:** injected `IStatusSource` /
  `IControlTransport` / `ICliOutput` seams (production adapters `StatusFileSource` / `ControlClientTransport`
  / `ConsoleOutput`), made `Commands` an instance wired once in `Program.Main`. Now unit-tested
  (`CliCommandsTests`, +7); the e2e tests still pass. Core/CLI — fully verified, 311 green. **Remaining:**
  `UpdateChecker` seams deferred — it lives in `Hypertree.App`, which the test project doesn't reference, so
  seaming it would be build-verified only (no test payoff until the App layer gets a harness).
- ✅ **Style constants scattered across ~14 files** with divergent names for the same hex → consolidated
  into one `Palette` class (Ink/Muted/Accent/Here/Stroke/CardBg, as both `Color` and cached `IBrush`).
  Named `Palette` not `Theme` (Avalonia's `StyledElement.Theme` would shadow it). Build 0/0, 295 green.
- ✅ **Primitive obsession**: `MoveDesktop(bool,int,int,bool,int,int)` + ad-hoc `(bool,int,int)` tuple →
  `readonly record struct DesktopAddress(bool OnMain, int BranchIndex, int DesktopIndex)`, used for Locate's
  return and MoveDesktop's from/to. Behaviour-preserving (RearrangeTests/NavigationModelTests unchanged);
  Core+App 0/0, 304 green.
- ✅ **Naming trap**: `TearDown(Branch?)` renamed to `TearDownBranch` (the app-lifecycle `Teardown()` keeps
  its name). Pure rename, App 0/0.
- ⛔ **Transient COM RCWs never released in `VirtualDesktopController`** (minted every 250ms poll tick);
  class isn't `IDisposable`. **Deliberately deferred.** The controller is an app-lifetime singleton exposed
  only as `IDesktopController` (not `IDisposable`), so field disposal only matters at process exit (the OS
  reclaims anyway) — low value. The flagged concern is the *transient* desktop RCWs, but aggressively
  `Marshal.ReleaseComObject`-ing them risks RCW **identity-unification** crashes ("COM object separated from
  its RCW") on these undocumented shell interfaces, and .NET's RCW finalizer already reclaims them. Not safe
  to do blind against a no-coverage interop surface that can't be smoke-tested here; wants a dedicated spike
  + smoke-test if pursued.
- ✅ **Dead code**: `SpatialPainter.TileBorder` (ScenePaint pass), `SW_RESTORE`/`SW_MINIMIZE` (Step 5),
  stale `BoardView` mention + `SpatialPainter` stale summary — all cleared.
- ⏭️ **IPC has no version negotiation** despite a separately-PATH-installed `htree.exe` that goes stale
  after a Velopack update. **Not done — this is a feature, not a maintainability refactor**, so it's out of
  scope for this effort; left for a deliberate design pass (it needs a wire-protocol version field + a
  mismatch policy, and a decision on how a stale `htree` should behave).

---

## Where to go next (remaining work, recommended order)

Every structural win in the review is done — the four God classes are split, the duplicated invariants are
collapsed to single sources, the latent bugs are fixed, and the tail cleanups are cleared. What's left is a
runtime pass and a few deliberate non-goals.

1. **Run the smoke-test batch** (the ⚠️ list up top) — six App/Views/Platform changes are build-verified and
   test-green but never run in a dev build. This is the one thing genuinely outstanding; the App layer has
   no automated coverage, so a human pass is how these get confirmed.

**Deliberate non-goals — don't pick these up as routine "cleanups":**
- **Scene `DrawGlyph`** (Tier 2 item 5): the shared colour/hit-cell dedup is done via `ScenePaint`; merging
  the row-cell and spatial-room glyph *bodies* was declined (they diverge on purpose).
- **`MonitorTopology`/`WindowPlacementApplier` types** (Tier 1 Step 5): the controller is split into
  partials; separate types were declined (shared `RECT`/interop straddles topology and placement).
- **`VirtualDesktopController` RCW disposal** (Tier 4): COM identity-unification crash risk on undocumented
  shell interfaces, no coverage — wants its own spike + smoke-test, not a drive-by.
- **IPC version negotiation** (Tier 4): a feature, not a maintainability refactor — a separate design pass.
- **`UpdateChecker` seams** (Tier 4): no test payoff until the App layer has a harness.

**Leave alone** (genuinely well-factored): `SpatialHull`, `SpatialTidy`, `SpatialNavigation`,
`MapCamera`, `NavHistory`, `Branch`, and the `ComInterop`/`DISPLAYCONFIG` marshalling.
