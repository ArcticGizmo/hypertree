# Monitor layout restore

Snapshot where every window sits across your monitors, and put them back after a dock cycle. Undock and
Windows crushes a multi-monitor layout onto the laptop panel; redock and it does *not* undo the damage —
your windows stay piled on one screen. This feature captures the arrangement before it collapses and
restores it when the monitors come back.

It's a second axis of the same promise Hypertree already makes on virtual desktops — *your windows,
arranged, restored where you left off* — moved from the **virtual-desktop axis** onto the **physical-monitor
axis**. Same tray, same "we move foreign top-level windows by handle" competence, different Win32 surface.

## What it does

- **Auto-snapshots on undock.** When a monitor disconnects, Hypertree records the pre-collapse layout —
  every countable window's placement and which monitor it was on — keyed by the monitor set that was
  present. Snapshot happens *before* the shell finishes reshuffling, so it captures intent, not the mess.
- **Offers to restore on redock.** When a monitor set reappears that we have a snapshot for, Hypertree
  restores each window to its remembered monitor and rectangle (including maximised / minimised state).
  Restore is **on offer, not automatic**, by default — a toast/palette action rather than windows leaping
  around unbidden (see *Decisions → Restore is opt-in per event*).
- **Named layouts, on demand.** Beyond the automatic per-dock capture, you can save the current
  arrangement as a named monitor-layout and restore it whenever — the physical-screen sibling of the
  existing virtual-desktop *layouts* feature.
- **Manual capture / restore** from the command palette, so you're never waiting on an event to fire.

## Why Hypertree, and not the Windows 11 built-in

Windows 11 has *Settings → System → Display → Multiple displays → "Remember window locations based on
monitor connection."* It handles the trivial case and should be left on. It is also silent, all-or-nothing,
per-exact-hardware, and gives you no *"restore now"*, no named snapshots, and nothing when you dock to a
*different* set of monitors than last time. The gap it leaves — **named, inspectable, on-demand layouts you
control** — is the exact shape Hypertree already gives virtual desktops. That's the niche this fills, not a
reimplementation of the OS feature.

## Architecture

A new seam, mirroring `IDesktopController` (`Hypertree.Core/Desktops/IDesktopController.cs`): every
build-fragile or OS-specific call lives behind an interface in `Hypertree.Core`, implemented in
`Hypertree.Platform.Windows`. The navigation/desktop logic stays OS-free and testable against a fake; this
does the same for window geometry.

| Piece | Where | What |
|---|---|---|
| `IWindowLayoutController` | `Hypertree.Core/Layout/` | The seam: `Snapshot()`, `Restore(snapshot)`, `CurrentMonitorSet()`. |
| `MonitorLayoutSnapshot`, `WindowPlacement`, `MonitorSet`, `MonitorRef` | `Hypertree.Core/Layout/` | Plain records — the serialisable data model, OS-free. |
| `WindowsWindowLayoutController` | `Hypertree.Platform.Windows/` | The Win32 impl — `EnumWindows`, `GetWindowPlacement`, `SetWindowPlacement`, `QueryDisplayConfig`. |
| `MonitorLayoutService` | `Hypertree.App/` (or Core) | Listens for display changes, debounces, decides snapshot-vs-offer-restore, owns the state file. |
| Palette commands + toast | `Hypertree.App/` | "Save monitor layout", "Restore layout…", the redock offer. |

The window-enumeration filter is **already written and battle-tested**: `IsCountableWindow` in
`VirtualDesktopController.cs` is the alt-tab-ish "real top-level app window" predicate. Factor it out and
reuse it verbatim — a snapshot should cover exactly the windows the map already counts, no more.

## Data model

```
MonitorLayoutSnapshot
  MonitorSet        set        // the monitors present when captured (the key)
  WindowPlacement[] windows

MonitorSet                     // an order-independent identity for "these screens"
  MonitorRef[] monitors        // sorted; equality = same stable ids present

MonitorRef                     // one physical output, identified stably (NOT by index)
  string  stableId             // device path / EDID-derived key from QueryDisplayConfig
  string  friendly             // "DELL U2720Q" — for the UI only, never for matching
  Rect    bounds               // virtual-desktop coords at capture time
  bool    isPrimary
  uint    dpi

WindowPlacement
  WindowKey key                // how we re-find this window (see below)
  string    monitorStableId    // which MonitorRef it lived on
  Rect      normalRect         // GetWindowPlacement's rcNormalPosition (workarea-relative)
  ShowState show               // Normal | Maximized | Minimized
```

State persists as JSON under `%APPDATA%\hypertree` (honouring `HYPERTREE_STATE_DIR`, as the rest of the app
does), one entry per known monitor set, plus any named layouts. A missing or corrupt file falls back to
"no snapshots" rather than blocking — same posture as the settings loader.

## The four things that are actually hard

Everything else (enumerate, read placement, set placement) Hypertree already does or is a documented one-liner.
These four are the design.

### 1. Monitor identity across a dock cycle

Monitor **indices reshuffle** as displays come and go, so keying a window's target by "monitor 2" is a bug
waiting for the next undock. `MonitorRef.stableId` must come from a stable source — the device path / EDID
via `QueryDisplayConfig` (`DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME`),
not `EnumDisplayMonitors` ordering. Two monitors of the same model report distinguishable target ids; a
genuinely indistinguishable pair (two identical panels, no serial) is a documented tie we break by last-known
position. **This is the make-or-break primitive — the spike proves it first.**

### 2. Window identity across time

For the headline use case this is *easy*, and that's the whole reason it's tractable: unplugging a monitor
**does not restart processes**, so HWNDs survive a dock/undock within one session. `WindowKey` is the HWND
for the live path, and restore is a straight handle match against windows still open.

It only gets fuzzy when we want to survive an app closing/reopening or a reboot — then HWND is dead and we
fall back to a heuristic key (process name + window class + a title fingerprint), matched best-effort, first
unclaimed wins. Phase 1 does HWND-only and says so; heuristic matching is a later phase, explicitly flagged
so we never *silently* mis-restore.

### 3. Timing — don't fight the shell

`DisplaySettingsChanged` / `WM_DISPLAYCHANGE` fires *while* the OS is still moving windows, and it can fire
several times per physical dock event. Two rules:

- **Snapshot on the *disconnect*, debounced, capturing the last-good multi-monitor state** — ideally the
  state from *before* the collapse. In practice we keep a rolling "current layout" updated on a lazy timer
  while docked, so when the undock event lands we already hold the good arrangement and don't have to race
  the shell to read it.
- **Restore on the *connect*, only after the topology settles** — wait for a quiet gap (say ~750 ms with no
  further display change) before touching a single window, or we place windows onto a monitor the shell is
  about to re-lay-out and lose the race.

### 4. DPI, and windows we can't touch

- **DPI.** The process must be **Per-Monitor-V2 DPI aware** or coordinates lie when monitors differ in
  scale. `GetWindowPlacement`'s `rcNormalPosition` is workarea-relative and largely scale-stable, which is
  why we store *that* rather than raw screen rects; on restore, move to the target monitor first, then size.
  Mixed-DPI is the classic corner — the spike must be run on a mixed-DPI rig, not just asserted correct.
- **Untouchable windows.** Elevated windows (from a non-elevated tray), and some UWP/store windows, will
  refuse `SetWindowPlacement`. Best-effort: skip and log, never throw. The map/pill already coexist with
  windows it can't move, so this is a known posture.

## UX flow

1. **Docked, working.** A lazy timer keeps the "current layout" for this monitor set fresh (cheap enumerate;
   no UI).
2. **Undock.** Debounced disconnect → persist the last-good layout under this monitor set's key. No prompt;
   it's a background save.
3. **Redock.** Debounced connect → if we hold a snapshot for the now-present set, surface a **restore
   offer** (toast + palette action). Accept → restore; ignore → it fades. A setting can promote this to
   *auto-restore* for people who want it silent.
4. **Any time.** Palette: *Save monitor layout* (name it), *Restore monitor layout…* (pick a named or the
   per-dock capture). Mirrors the virtual-desktop layouts UX so it feels like one app.

## Decisions

- **Restore is opt-in per event by default.** Windows teleporting themselves is startling and, if the
  match is ever wrong, destructive-feeling. An offer costs one keystroke and keeps trust; auto-restore is a
  setting for those who've decided they trust it. (Consistent with the app's *show-before-moving* instinct
  on navigation.)
- **Separate seam, separate state, separate palette entries — not folded into `IDesktopController`.** The
  two axes share a process and a window-filter, nothing else: different APIs (geometry vs the virtual-desktop
  COM), different identity problems, different failure modes. Bolting monitor geometry onto the desktop
  controller would muddy the one interface whose isolation the whole architecture leans on.
- **Store `rcNormalPosition`, not raw screen rects.** It's workarea-relative and survives scale changes far
  better, and it carries the restore-to-maximised case for free (a maximised window still has a sane normal
  rect to fall back to).
- **HWND-first, heuristic-later, and honest about which.** The common case is same-session and HWND-exact;
  shipping that first is correct and low-risk. Cross-session matching is a real feature but a fuzzier one —
  it lands as its own phase with its own confidence signalling, never silently.
- **Reuse `IsCountableWindow` verbatim.** The snapshot's window set must equal the map's counted set, or the
  two features disagree about what "your windows" are. One predicate, shared.

## Spike findings

`spike/monitor-layout/` (throwaway) proves the four hard bits on real hardware. Commands: `list` (monitors
+ stable ids), `snap` / `restore` (all windows), `selftest` (self-contained cross-monitor round-trip),
`watch` (the live dock/undock flow), `diag` (traces the identity chain).

- **#1 monitor identity — proven, and the make-or-break primitive is a minefield of silent failures.** The
  `QueryDisplayConfig` → source GDI name → target *device path* chain yields a stable, EDID-derived id
  (`\\?\DISPLAY#DELF167#…#{guid}`) that survives a dock cycle where `\\.\DISPLAY1/2/3` reshuffle. Two struct
  bugs each made the chain *silently* fall back to the shuffling GDI name rather than error: (a)
  `DISPLAYCONFIG_MODE_INFO` must be exactly 64 bytes (its union is 48, not 64) or `QueryDisplayConfig`
  returns `ERROR_INVALID_PARAMETER`; (b) the device-info type constants are `GET_SOURCE_NAME = 1`,
  `GET_TARGET_NAME = 2` — swapping them mismatches the struct size and every `DisplayConfigGetDeviceInfo`
  returns 87. **Lesson for Phase 1: the impl must treat a fall-back-to-GDI-name as a hard failure and log
  it, never ship it silently.**
- **The "two identical panels" tie is rarer than the design feared.** The dev rig has *two* `DELL P2725DE`
  monitors — same friendly name — yet they carry distinct device paths (`DELF167…UID8263` vs
  `DELF166…UID12615`, different EDID serials). So keying on device path already separates them; the truly
  indistinguishable case needs two panels with *identical* EDID serials, which is genuinely uncommon. The
  laptop's internal panel reports an empty friendly name (`AUOFDB1`), confirming friendly-name is UI-only
  and the GDI-name fallback for the label is needed.
- **#2 + #4 restore — proven.** `selftest` moves a launched charmap window across to another physical
  monitor via the offset math and back, verifying against `MonitorFromWindow` each way: both ✅.
  `SetWindowPlacement` with a monitor-relative `rcNormalPosition` re-anchors correctly; PerMonitorV2 (the
  manifest) makes the coordinates and per-monitor DPI (read as 120 here) truthful.
- **Coordinate model settled.** Store `rcNormalPosition` as an **offset from the window's monitor origin**,
  not raw — restore adds the (possibly moved) target monitor's origin. Round-trip on the same monitor is
  exact because the offset cancels; a subtle raw-vs-offset mismatch here silently double-counts.
- **#3 timing — signal source matters; use a top-level window, not a message-only one.** First cut used a
  `HWND_MESSAGE` window and saw *nothing* on undock: `WM_DISPLAYCHANGE` is broadcast to **top-level** windows
  and message-only windows are explicitly excluded from broadcasts. Fix is a real (never-shown) top-level
  window. **Belt-and-braces:** the spike also runs a 2 s poll of the monitor set as a backstop, so detection
  never depends solely on the broadcast arriving — and in the app that same lazy timer is already wanted to
  keep the rolling "current layout" fresh, so it's free. 750 ms debounce + monitor-set comparison then
  decides undock-saves vs redock-offers. Run `watch` and physically undock to confirm end to end.

## Phasing

- **Spike (done).** See findings above — monitor identity (#1), cross-monitor restore (#2/#4), and the
  timing/debounce shape (#3) are proven or plumbed. `watch` awaits a real dock/undock for final confirmation.
- **Phase 1 — implemented.** The seam and its Win32 impl, the service, persistence, app wiring, and the
  palette manager, with same-session HWND matching. Files:
  - `Hypertree.Core/WindowLayout/` — `MonitorLayout.cs` (records: `MonitorRef`, `WindowPlacement`,
    `MonitorLayoutSnapshot`, `NamedMonitorLayout`, `RestoreReport`, `Recti`, `ShowState`, `MonitorSet.Key`),
    `IWindowLayoutController.cs`, `MonitorLayoutService.cs` (the timer-free, testable orchestrator).
  - `Hypertree.Core/Store/MonitorLayoutStore.cs` — `IMonitorLayoutStore` + `FileMonitorLayoutStore`
    (`%APPDATA%\hypertree\monitor-layouts.json`).
  - `Hypertree.Platform.Windows/WindowsWindowLayoutController.cs` — the ported spike Win32 (stable-id chain,
    `GetWindowPlacement`/`SetWindowPlacement`, `IsCountableWindow` kept identical to the desktop controller).
  - `Hypertree.App` — `PlatformServices.CreateWindowLayoutController`, `StartWatchingMonitors` (a 2 s
    background `DispatcherTimer` driving `Tick`, mirroring `PollingDesktopWatcher`), and the redock offer as
    a clickable notification that restores directly. Save/restore is entirely automatic (undock/redock), so
    there's no manual save UI. The palette carries only a **"Monitor placement (debug)"** entry opening the
    diagnose/trace overlay, and it's gated on `DevChrome.Active`, so it appears on a Debug/dev build only and
    never in a release/installed copy. (The named-layout store/service methods remain as the Phase-2 seam and
    file-format support, currently unsurfaced.)
  - `tests/Hypertree.Tests/MonitorLayoutTests.cs` — set-key identity, JSON round-trip, and the
    leave-saves / arrive-offers / two-tick-debounce logic against a fake controller (10 tests).

  The decision logic ended up **count-free and symmetric** (leaving a set saves it, arriving at a known set
  offers it) rather than the count-comparing undock/redock branch the design first sketched — it handles
  undock, redock, and dock-swaps uniformly and is simpler to reason about.

  Two refinements after first use:
  - **No offer when returning to a single screen.** Down to one monitor, every window is forced onto it
    anyway, so there is nothing to spread back — the offer is suppressed (and single-monitor arrangements
    aren't saved). Restore only prompts when arriving at a multi-monitor set.
  - **A visual debug overlay.** `MonitorDebugWindow` (a rough, scrollable standalone window) lays out the
    two axes together: one row per **virtual desktop**, each holding a **box per monitor** that lists the
    windows currently on it, every window captioned with the monitor it's on and the one the saved layout
    wants it on (drifted windows in amber, correctly-placed in green). Desktop grouping comes from
    `IDesktopController.WindowsOn`, current/wanted monitor from the live snapshot vs the saved capture for
    the monitors present now. Reached two ways: the redock **"Restore your window layout?"** notification now
    opens it (review-before-you-leap, in keeping with show-before-moving) with a **Restore** button, and the
    "Monitor layouts…" palette has a **Diagnose window placement** row that opens it directly.

  Remaining before it's proven end-to-end: a live dock-cycle test of the running tray (the underlying Win32
  is already spike-proven).

- **A maximized window can't cross monitors in one `SetWindowPlacement` call** (found via the trace tool
  below, debugging why Slack wouldn't move back). `SetWindowPlacement` with `SW_MAXIMIZE` returns success but
  keeps an already-maximized window maximized on its *current* monitor, only stashing `rcNormalPosition` for
  a later restore — so a maximized window that needs to change monitors silently stays put (`before == after`,
  result `True`). Fix (`WindowsWindowLayoutController.ApplyPlacement`): when a **maximized** window must
  **change monitors**, restore it onto the destination as a normal window first (which *does* move it), then
  re-maximize — so it maximizes on the destination. Guarded on an actual monitor change, so windows already
  in place don't flicker. Normal/minimized windows, and same-monitor maximized windows, keep the single call.

- **Restore doesn't steal focus.** The show commands that maximize a window activate it (there's no
  no-activate maximize), so restoring a layout would pull the user to whichever window it maximized last —
  the cross-monitor one, e.g. Slack landing on the laptop panel, dragging the view with it. Fix: normal and
  minimized windows use the no-activate show commands (`SW_SHOWNOACTIVATE` / `SW_SHOWMINNOACTIVE`), and
  `Restore` captures the foreground window up front and re-asserts it at the end (via `IForegroundActivator`,
  injected into the controller) — so focus stays where the user was, however many windows moved behind.

- **The redock offer applies directly, and only when something's actually out of place.**
  `MonitorLayoutService.PlanRestore` compares the saved layout to the live state and returns how many windows
  are on the wrong monitor (`ToMove`) and whether any of those is a maximized cross-monitor mover
  (`NeedsCurtain`). The offer is suppressed entirely when `ToMove == 0`, its text says how many windows will
  move (not the raw snapshot size), and **clicking the notification restores directly** — the debug overlay
  is no longer in the restore path (it stays available from the "Monitor layouts…" palette for debugging).

- **A loading curtain hides the maximize-trick pop.** The restore→maximize step visibly pops the window as
  it un-maximizes and re-maximizes on the destination. When `PlanRestore.NeedsCurtain` is set, the restore
  runs behind `RestoreCurtain` — a black, non-activating, all-monitor window that fades in, applies the moves
  once fully opaque, then fades out (even if the restore throws). It's skipped when no maximized window
  crosses monitors, since a plain move doesn't pop and needs no cover.

- **A restore-trace debug tool.** `IWindowLayoutController.RestoreTraced` performs the restore and returns a
  per-window trace — before/target/after screen rects, the `SetWindowPlacement` result and last Win32 error,
  class/process — and `Probe` re-reads a window a beat later to catch a move that snapped back. The debug
  overlay's **Trace restore → file** button writes it to `%APPDATA%\hypertree\restore-trace.txt`. This is
  what surfaced the maximized-cross-monitor limitation (every stuck window was `Maximized` + monitor-changing;
  everything else moved or was already in place).
- **Phase 2.** Named monitor-layouts surfaced alongside virtual-desktop layouts; auto-restore setting.
- **Phase 3.** Cross-session / heuristic window matching with visible confidence, for reboot survival.

## Open questions

- **Two identical panels, no serial.** How often does the indistinguishable-monitor tie actually bite on
  real hardware, and is last-known-position a good enough tie-break? (Spike should log target ids on a
  dual-identical rig if one's available.)
- **Per-monitor-set vs single rolling snapshot.** Keep a snapshot per distinct monitor set (dock A vs dock B
  vs café), or just the most recent? Per-set is more useful and not much more code, but grows the state file.
- **Interaction with virtual desktops.** A window's placement is per (monitor × virtual-desktop)? Phase 1
  snapshots the *current* desktop's windows only; whether to snapshot across all desktops is a Phase 2
  question tied to how `WindowsElsewhere` already reaches other desktops.
- **What counts as "the same dock."** Is a monitor set identified by its exact members, or a subset match
  (docked with the external display but the second one's off)? Start exact; revisit if it's annoying.
```
