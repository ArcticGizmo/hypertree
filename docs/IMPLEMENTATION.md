# Hypertree — Implementation Plan (milestones & phases)

> Companion to [`PLAN.md`](./PLAN.md). `PLAN.md` is the **design** source of truth
> (the *what* and *why*); this document is the **build** plan (the *how* and *in what
> order*). It maps the M0–M3 milestones onto concrete, gated engineering phases on an
> **Avalonia / .NET 10** stack, reusing patterns already proven in `../perch`.

## 0. Stack decision & why Avalonia holds

Hypertree must live in the tray, capture global hotkeys with no focused window, and
paint a small always-available overlay (the HUD). That is **exactly** what `perch`
already is. So this is not a green-field bet — it is a graft of an existing, working
shape plus **one genuinely new and risky component**: the virtual-desktop controller.

What we inherit, and from where:

| Need | Already solved in | Reuse |
|---|---|---|
| Tray app that outlives its windows | `perch` `App.axaml.cs` (`ShutdownMode.OnExplicitShutdown`, `TrayIcon`/`NativeMenu`) | copy the shell |
| Global hotkey with no focus | `perch` `Perch.Platform.Windows/GlobalHotkey.cs` (`RegisterHotKey`/`WM_HOTKEY` on a dedicated message-loop thread) | copy + extend to arrow-key VKs |
| Owner-drawn always-on overlay | `perch` `OverlayCanvas` + `Rendering/OverlayDraw` + `HeadlessRenderer` | template for the HUD chip |
| Focusing/moving native windows | `perch` `Perch.Platform.Windows/WindowActivator.cs` (EnumWindows, GA_ROOTOWNER, foreground-lock dance) | reuse for "move window to scope desktop" |
| Core / Platform.Windows / App split behind interfaces, `#if WINDOWS` | `perch` layout + `PlatformServices` composition root | copy the topology |
| Velopack release + icon-gen | `perch` (`publish.bat`, `tools/IconGen`) | copy at M3 |

**The only unproven primitive is virtual-desktop control** (create / switch / move a
window onto a desktop), which has *no supported API* and drifts per Windows build.
Every downstream milestone is gated on that. So the phasing front-loads it: M0 is a
throwaway spike, and its whole job is to answer *native or komorebi?* before we build
the graft.

## 1. Solution topology (target)

Mirror `perch`'s multi-project split so OS-specific interop stays behind `Core`
interfaces and a macOS/Linux head remains theoretically possible (we do **not** build
one, but the discipline keeps the desktop interop quarantined — which matters when the
API breaks on a Windows update).

```
hypertree.slnx
src/
  Hypertree.Core/              net10.0  — UI-free, no Win32, no System.Drawing
    Desktops/                  IDesktopController, DesktopId, DesktopLayout   ← the risky seam
    Scopes/                    Scope, Anchor, ScopeState, IScopeStore, NavigationModel  ← Model P lives here
    Git/                       (git integration — deferred, M2+)
    Hotkeys/                   HotkeyBinding, NavAction         (chord → intent mapping)
    Platform/                  IGlobalHotkey, IWindowMover, IHud, ISystemMetrics (interfaces only)
    Store/                     JsonFile, HypertreePaths
  Hypertree.Platform.Windows/  net10.0-windows — Win32 + the desktop-primitive wrapper
    GlobalHotkey.cs            (from perch, extended for arrow VKs + Win+Ctrl chords)
    WindowMover.cs             (from perch WindowActivator + MoveWindowToDesktop)
    VirtualDesktop*.cs         the winner of the M0 spike, isolated here
  Hypertree.App/               net10.0-windows — tray shell + HUD overlay (assembly: hypertree)
    App.axaml(.cs)             tray/HUD/hotkey wiring (from perch App)
    PlatformServices.cs        composition root (#if WINDOWS)
    Views/HudChip.cs           owner-drawn HUD (from perch OverlayCanvas/OverlayDraw)
    Rendering/                 HudDraw mini-PaintKit + HeadlessRenderer (from perch)
  Hypertree.Cli/               net10.0 — `hypertree new/list/anchor`  [M2+]
tests/Hypertree.Tests/         xUnit over Core (NavigationModel is the priority target)
tools/IconGen/                 raster icons from hypertree.svg (from perch)  [M3]
```

**Testability rule (borrowed from perch):** all Model-P navigation logic —
dive/surface/move-within-level, anchor resolution, resume-last-used, edge behaviour —
lives in `Hypertree.Core/Scopes/NavigationModel` as **pure state transitions over an
`IDesktopController` interface**. The controller is faked in tests, so we can unit-test
the *entire feel* of the model without touching a real desktop. The Win32/DLL layer
stays dumb: create, switch, move, current-index. That seam is what lets M0's risk not
poison M1's logic.

---

## M0 — Feasibility spike (native vs. komorebi)

**Goal (from `PLAN.md` §6):** on *this* Windows 11 build, from code, reliably
(a) create + name a desktop, (b) switch to it, (c) move a window onto it, and
(d) capture a global hotkey. Decide native-COM vs. komorebi. **Gate: everything
downstream depends on this.**

This is a **throwaway spike**, not scaffolding — a single console/script, no
architecture, deliberately ugly. We are buying an answer, not building a layer.

### Phase 0.1 — Hotkey capture (lowest risk, do first to de-risk the rest)
- Lift `perch/GlobalHotkey.cs` into a tiny console. Register `Win+Ctrl+↓/↑/←/→`
  (VKs `0x28/0x26/0x25/0x27`) and confirm each fires with no focused window.
- **Unknown to kill:** does `Win+Ctrl+Arrow` collide with Windows' own
  desktop-switch chord? If the OS eats it, `RegisterHotKey` returns false — try a
  custom layer (e.g. `Alt+Ctrl+Arrow` or a leader key). Record the winning chord.
- *Exit:* a chord we can own, proven, written down.

### Phase 0.2 — Desktop create / switch (the core risk)
- Spike **`VirtualDesktopAccessor.dll`** (Ciantic) first via P/Invoke: `GetDesktopCount`,
  `CreateDesktop`, `GoToDesktop`, `SetDesktopName`, `GetCurrentDesktopNumber`.
- Fallback candidate in the same spike: **MScholtes `VirtualDesktop.exe`** shelled out.
- **Unknowns to kill:** does the DLL load and match *this* Win11 build (26200)? Does
  switching move **all monitors** together (needed — `PLAN.md` §5 multi-monitor)?
  Is naming persistent? How bad is switch latency (the HUD promise dies if it janks)?
- *Exit:* create+name+switch works and survives a couple of Explorer restarts, **or**
  it's flaky and we flip to the komorebi track (see Phase 0.4).

### Phase 0.3 — Move a window onto a desktop
- `MoveWindowToDesktopNumber(hwnd, n)` via the DLL, targeting a hwnd from perch's
  `EnumWindows`/`GA_ROOTOWNER` walk (reuse `WindowActivator` logic).
- **Unknown to kill:** can we move *someone else's* window (a terminal, VS Code) across
  desktops reliably, including elevated windows? This is the make-or-break for
  "provision a scope's trio."
- *Exit:* a chosen window lands on a chosen desktop, repeatably.

### Phase 0.4 — Decision gate: native or komorebi
- Write a one-page spike report to `docs/design/m0-findings.md`: chord chosen, DLL
  build-match, multi-monitor behaviour, move reliability, latency numbers.
- **Decide:** native-COM controller (default if solid) vs. komorebi/GlazeWM scriptable
  workspaces (fallback — immune to undocumented-API churn but a bigger paradigm shift).
- This decision fixes the single implementation behind `IDesktopController`. **No M1
  code before this gate.**

> Spike output is disposable. The *only* durable artifact from M0 is the findings doc
> and the chosen chord/primitive — the console app gets deleted.

---

## M1 — Manual MVP (prove the *feel* of Model P)

**Goal (`PLAN.md` §8):** hard-coded scopes; dive/surface hotkeys + HUD working for one
worktree's trio; **no git integration**. This milestone exists to test cheaply whether
depth actually feels like a place before investing in M2/M3 (`PLAN.md` §9, risk 2).

### Phase 1.1 — Scaffold the real solution
- Create `hypertree.slnx` and the four projects per §1, copying perch's csproj
  multi-target shape and `PlatformServices` composition root.
- Move the M0-winning primitive into `Hypertree.Platform.Windows` behind
  `IDesktopController` (create/switch/move/current-index/count).
- Port `GlobalHotkey.cs` and the `WindowActivator`→`WindowMover` logic.
- *Exit:* empty tray app launches, sits in the tray, exits cleanly (perch shell).

### Phase 1.2 — NavigationModel (the heart, fully unit-tested)
- Implement Model P as pure transitions in `Core/Scopes/NavigationModel` over the
  faked `IDesktopController`:
  - **State:** current level (day-to-day | inside scope X), current index within level,
    per-scope last-used index, anchor map.
  - **Transitions & the locked sub-decisions (`PLAN.md` §3):**
    - `moveWithin(±1)` — `←/→` scoped to the current level only.
    - `dive()` — from an anchor, enter its scope at its **last-used** index (resume,
      not first — sub-decision 3).
    - `surface()` — from anywhere in a scope, land on that scope's **anchor** (always
      the entry point — sub-decision 2).
    - Edge/empty rules (`PLAN.md` §5): `↑` at day-to-day = no-op; `↓` on an
      anchor-less desktop = no-op (offer-to-create deferred to M2); level edges clamp.
- **This phase is where the design gets validated as code.** Tests cover every
  transition and edge before any hotkey is wired. Target: the model is provably correct
  independent of whether the real desktop layer works.
- *Exit:* `Hypertree.Tests` green across all Model-P transitions + edges.

### Phase 1.3 — Wire hotkeys → model → controller
- Register the M0 chord set; each press maps to a `NavAction`, runs the transition,
  and calls `IDesktopController.SwitchTo(...)`. Callbacks marshal to the UI thread
  (perch's `Dispatcher.UIThread.Post` idiom).
- Hard-code **one** scope's trio (SPA/API/Mobile desktops) + one anchor for the test.
- *Exit:* on the real machine, `Win+Ctrl+↓` dives, `↑` surfaces to anchor, `←/→` moves
  within level, re-dive resumes last-used.

### Phase 1.4 — The HUD chip (load-bearing, `PLAN.md` §9 risk 3)
- Owner-drawn overlay via perch's `OverlayCanvas`/`OverlayDraw` pattern, rendering the
  source-of-truth readout `▸ feat-123 · API (2/3)` (`PLAN.md` §3, sub-decision 4).
- **Placement (decided):** a small chip **centered horizontally over the primary
  monitor's taskbar** (bottom-center), so "where am I" sits where the eye already goes
  for system state. Transparent, click-through, always-on-top, no activation/focus
  steal. Position derives from the primary work-area vs. full-screen delta (taskbar
  height) — recompute on DPI/resolution change. (Multi-monitor placement = M3.)
- Flash-on-switch first (simplest); keep always-on as a config toggle for later.
- Reuse perch's `HeadlessRenderer` `render <outDir>` mode so the HUD can be eyeballed
  at 1×/1.5× without a display — mandatory for any owner-drawn text (perch's line-height
  clipping gotcha applies here).
- *Exit:* HUD updates within one frame of a switch and reads correctly on this DPI.

### Phase 1.5 — Feel review (explicit gate)
- Live-drive it for real work with the hard-coded scope. Answer `PLAN.md` §9 risk 2:
  is depth a *place*, or is it mode-load? Capture the verdict in `docs/design/`.
- **Gate:** if the feel doesn't survive contact, iterate the model here — cheaply —
  before spending M2/M3. If it does, proceed.

> **Note — the model evolved during M1.5.** Live feedback moved Model P well past the
> original anchor description above: anchors gave way to a **top row of ungrouped
> desktops + a vertical stack of groups**; the overlay became a design-matched interactive
> map (`Ctrl+Alt+Space`) with per-tile delete; and state now persists to `%APPDATA%`. The
> phases above are kept as historical intent; the shipped behaviour is what's in the code
> and the `docs/design/` notes + commit history.

### Phase 1.6 — UX iteration 2 (before M2)
Detailed spec: **[`docs/design/ux-iteration-2.md`](design/ux-iteration-2.md)**. Summary:
- **F1** standard navigation shows the *centred* map (not the top strip).
- **F2** vertical model — main timeline as pivot, groups fixed, "main above current"
  (`↑` passes through main); replaces the ladder + reorder-on-open. *(decided)*
- **F3** no bounding box — board uses the full screen width.
- **F4** spotlight `Ctrl+Alt+P` — filter existing desktops, always offer create.
- **F5** command palette `Ctrl+Alt+Shift+P` — same look/feel; bones only.
- New shared `PaletteWindow` (perch `SessionSwitcherWindow` pattern) + an
  `IForegroundActivator` focus-grab primitive.
- Build order in the design doc; do F2 (Core, tested) first, then the rendering unify.

---

## M2 — Git integration (deferred)

**Goal (`PLAN.md` §8):** tie branches to real version-control branches / worktrees —
create, anchor, and remove a branch's desktops in step with the repo.

**Status: intentionally unspecified.** The approach *and the naming* are left open until
we're ready to tackle it — an earlier concrete design was dropped so it doesn't lock us
in prematurely. What's already in place and reusable when we pick this up: branches
(named streams of desktops) with templates, provisioning/teardown of their OS desktops,
persistence, and reconciliation of the branch map against the live desktops
(`NavigationModel.Reconcile`). The remaining open questions from `PLAN.md` §5 — branch
creation, removal semantics (avoid yanking live windows), and desktop reconciliation —
still stand and should be settled here.

---

## M3 — Persistence + polish + ship

**Goal (`PLAN.md` §8):** state survives reboot; resume-last-used; §5 edge cases handled;
multi-monitor confirmed; then package.

### Phase 3.1 — State persistence (`PLAN.md` §5, "persistence")
- `Core/Store` (`JsonFile`, `HypertreePaths`): persist anchor→scope map +
  last-used-index-per-scope under `%APPDATA%/hypertree/`.
- Restore on launch; re-attach to still-present desktops by name; reconcile (Phase 2.4).
- *Exit:* reboot → dive into a scope → land on the desktop you last used there.

### Phase 3.2 — Multi-monitor confirmation (`PLAN.md` §5)
- Confirm native desktops switch *all* monitors together (validated in M0) holds for
  scopes; decide + document what a dive does across a 3-monitor array (user's setup).
- *Exit:* documented, tested behaviour on the user's real multi-monitor rig.

### Phase 3.3 — Edge & HUD polish
- Finish `PLAN.md` §5 edge navigation (leftmost/rightmost clamps, `↓` on empty →
  offer-to-create, optional `Win+Ctrl+↓` scope→scope jump from §3 keymap).
- HUD: settings for always-on vs. flash, per-monitor placement, theming via a shared
  `Palette` (perch idiom).
- Settings window for hotkey rebinding (perch's `SettingsWindow`/`HotkeyBinding` pattern)
  — important because the M0 chord may not suit every machine.
- *Exit:* no dead-end navigation; HUD configurable; chords rebindable.

### Phase 3.4 — Package & release
- Velopack (`vpk`) + `publish.bat`, `tools/IconGen` from `hypertree.svg`, single-instance
  bootstrap, GitHub Actions `v*`-tag release workflow — all copied from perch.
- *Exit:* installable, self-updating tray build.

---

## Dependency & risk map

```
M0 spike ──(gate: native or komorebi)──► M1 MVP ──(gate: feel survives)──► M2 git integration ──► M3 ship
   │                                        │
   └─ isolates ALL desktop interop          └─ NavigationModel unit-tested independent of the interop
      behind IDesktopController                 (design validated as code before hotkeys are wired)
```

- **Undocumented-API breakage (`PLAN.md` §9 risk 1):** contained by `IDesktopController`
  — a Windows update that breaks the DLL is a one-file swap, and komorebi remains a
  drop-in alternate implementation of the same interface.
- **Feel doesn't survive (`PLAN.md` §9 risk 2):** M1.5 is the explicit cheap kill-point,
  reached before any git or persistence work.
- **HUD jank (`PLAN.md` §9 risk 3):** built on perch's battle-tested owner-drawn overlay
  + headless render harness, treated as core in M1 (Phase 1.4), not chrome at the end.

## Immediate next actions

1. Resolve the `PLAN.md` §5 open questions that are model-shaping (removal, creation,
   multi-monitor) — at least provisionally — so M2 isn't blocked later.
2. Start **M0 Phase 0.1** (hotkey chord) — cheapest, de-risks the input layer, and tells
   us immediately whether `Win+Ctrl+Arrow` is even ownable on this build.
3. Then **M0 Phase 0.2/0.3** and write `docs/design/m0-findings.md`. Do not scaffold the
   real solution until the M0 gate is decided.
```
