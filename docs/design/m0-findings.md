# M0 — Feasibility spike findings

> Durable record of the M0 spike (per `docs/IMPLEMENTATION.md`). The spike code under
> `spike/` is throwaway; **this document + the chosen chord/primitive are the only
> outputs that survive M0.**

Machine: Windows 11 Pro, build 10.0.26200. Test rig: `spike/m0-hotkey` (`RegisterHotKey`
matrix, .NET 10 console).

## Phase 0.1 — Global hotkey capture  ✅ decided

### Acceptance matrix (does `RegisterHotKey` let us claim the chord?)

| Chord layer | Left | Up | Right | Down |
|---|:--:|:--:|:--:|:--:|
| **Win+Ctrl**       | ❌ refused | ✅ accepted | ❌ refused | ✅ accepted |
| **Alt+Ctrl** (= Ctrl+Alt) | ✅ | ✅ | ✅ | ✅ |
| **Win+Alt**        | ❌ | ❌ | ❌ | ❌ |
| **Ctrl+Alt+Shift** | ✅ | ✅ | ✅ | ✅ |
| **Win+Shift**      | ❌ | ❌ | ❌ | ❌ |

### What this tells us
- **`Win+Ctrl+←/→` is reserved** — it *is* Windows' native "switch virtual desktop
  left/right." The OS won't hand it over. `Win+Ctrl+↑/↓`, by contrast, is **free**.
  That aligns neatly with Model P: the new depth axis (dive/surface) is vertical and
  available; horizontal ←/→ *is* native desktop switching, which is what
  move-within-level means at the day-to-day level.
- **`Win+Alt+*` and `Win+Shift+*` are fully reserved** (the latter is native
  "move window to adjacent monitor"). Unusable.
- **`Ctrl+Alt+*` and `Ctrl+Alt+Shift+*` are fully available** — all four arrows each.

### Decision
**Default chord layer: `Ctrl+Alt+Arrow`** (all four directions on one consistent layer).

- `Ctrl+Alt+↓` = dive, `Ctrl+Alt+↑` = surface, `Ctrl+Alt+←/→` = move within level.
- Chosen for a single uniform modifier across all four actions (cleaner to learn than
  a split Win+Ctrl-vertical / native-horizontal scheme).
- **Not yet verified: delivery (FIRES).** Acceptance ≠ delivery — a registered chord can
  still be intercepted by the shell before `WM_HOTKEY` reaches us. The press-test to
  confirm each chord actually fires (and has no side effect) is outstanding; run
  `spike/m0-hotkey` and press each. Low risk for `Ctrl+Alt+*` (no known shell owner),
  but confirm before M1 Phase 1.3 wires them for real.

### ⚠️ Caveat — Intel graphics screen-rotation collision
`Ctrl+Alt+Arrow` is the classic **Intel HD Graphics display-rotation hotkey**
(`Ctrl+Alt+↓` flips the screen upside down) on machines with that driver's hotkeys
enabled. It registered fine here (this box has no active Intel hotkey layer), but on
other hardware it may fight the driver. **This is why per-user rebinding is not
optional** — it's already scheduled (M3 Phase 3.3, perch `SettingsWindow`/`HotkeyBinding`
pattern). Consequence for architecture: **the chord must be config-driven from day one**
— M1 reads it from settings (even if the settings UI comes later), never hard-codes it.

### Note for the implementation
Perch's `GlobalHotkey` only models Alt/Control/Shift. Add **`MOD_WIN` (0x0008)** to
`HotkeyModifiers` when porting, so the Win-layer chords remain selectable for users who
prefer them.

## Phase 0.2 — Desktop create / switch  ✅ proven (native, our own interop)

Test rig: `spike/m0-desktops` — our own C# COM interop against the ImmersiveShell's
undocumented `IVirtualDesktopManagerInternal`. **No third-party DLL is loaded or run**;
we reimplement the public interface *definitions* (GUIDs + vtable) as our own code.

Exercised successfully on build 26200, all through code we own:

| Operation | Result |
|---|---|
| Connect ImmersiveShell → `QueryService` → internal manager | ✅ |
| `GetCount` / `GetCurrentDesktop` / `GetDesktops` (enumerate) | ✅ (read 8 desktops + GUIDs) |
| `CreateDesktop` | ✅ (count 8 → 9) |
| `SetDesktopName` | ✅ (via manual HSTRING — see below) |
| `SwitchDesktop` (+ switch back) | ✅ (no crash; returned) |
| `RemoveDesktop(target, fallback)` | ✅ (count 9 → 8) |

**Verdict: native is viable. We do NOT need VirtualDesktopAccessor.dll or MScholtes'
binary** — the whole thing is ~120 lines of our own interop.

### Build-matched definitions that work on 26200 (25H2)
Record these — they are the single build-fragile thing, and live behind
`IDesktopController` in the real app so an OS-update break is a one-file swap.

- `CLSID_ImmersiveShell` = `C2F03A33-21F5-47FA-B4BB-156362A2F239`
- `CLSID_VirtualDesktopManagerInternal` (service key) = `C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B`
- `IVirtualDesktopManagerInternal` IID = `53F5CA0B-158F-4124-900C-057158060B27` (24H2/25H2 vtable — no `hWndOrMon` params)
- `IVirtualDesktop` IID = `3F07F4BE-B107-441A-AF0F-39D82529072C`
- `IServiceProvider` IID = `6D5140C1-7436-11CE-8034-00AA006009FA`
- Documented, stable: `IVirtualDesktopManager` CLSID `AA509086-…`, IID `A5CD92FF-…`
- Source of definitions: MScholtes/VirtualDesktop `VirtualDesktop11-24H2.cs` (public
  reverse-engineered interface defs; we transcribe, not link/run).

### Gotcha found & solved — HSTRING marshalling
`[MarshalAs(UnmanagedType.HString)]` throws *"Invalid managed/unmanaged type
combination"* on .NET 10 — .NET 5+ removed the built-in WinRT HSTRING marshaller. The
name APIs (`GetName`, `SetDesktopName`) therefore take/return `IntPtr` and we marshal
manually via `combase.dll` (`WindowsCreateString` / `WindowsGetStringRawBuffer` /
`WindowsDeleteString`). This is required for the HUD naming desktops after branches —
bake the `HString` helper into the real Windows platform layer.

### Still observational (needs eyes on the 3-monitor rig — I can't see the screen)
- **Multi-monitor:** confirm `SwitchDesktop` moves **all** monitors together (`PLAN.md`
  §5). Re-run `spike/m0-desktops` and watch.
- **Switch-back exactness:** the captured-then-restored current desktop looked
  consistent per run, but confirm a dive/return lands you *exactly* where you started
  with real windows open.

## Phase 0.3 — Move window onto desktop  ✅ proven (internal API required)

Test rig: `spike/m0-move` — launches a **foreign** window (charmap, a separate process,
the realistic case for provisioning a scope's terminal/editor/browser) and moves it.

| Path | Result |
|---|---|
| **A** — documented `IVirtualDesktopManager.MoveWindowToDesktop(hwnd, guid)` | ❌ `E_ACCESSDENIED` (0x80070005) — the documented API refuses windows the caller doesn't own |
| **B** — `IApplicationViewCollection.GetViewForHwnd(hwnd)` → `IVirtualDesktopManagerInternal.MoveViewToDesktop(view, desktop)` | ✅ window moved — `GetWindowDesktopId` == target, `IsWindowOnCurrentVirtualDesktop` == false |

**Verdict: moving foreign windows works, but *only* via the internal (build-fragile)
`IApplicationView` path** — the documented API is a dead end for other apps' windows.
This raises the surface of build-specific interop: the app depends on
`IApplicationViewCollection` + `IApplicationView` GUIDs too, not just the desktop
manager. All the more reason for the single `IDesktopController` seam.

Note (fixed but educational): Win11's Notepad is a Store-app stub that reparents to
another process, so `Process.MainWindowHandle` never populates — use a classic Win32
app (charmap) or an `EnumWindows`-by-pid scan (perch's `WindowActivator` already does
the latter for real terminals/IDEs).

### Additional build-matched definitions verified on 26200
- `IApplicationViewCollection` IID = `1841C6D7-4F9D-42C0-AF41-8747538F10E5`
  (obtained via `shell.QueryService(iid, iid)`; `GetViewForHwnd` at vtable slot 3)
- `IApplicationView` IID = `372E1D3B-38D3-42E4-A15B-8AB2B178F513` (opaque — only passed through)

## Phase 0.4 — Decision: **NATIVE** ✅

All four M0 primitives (`PLAN.md` §6 spike goal) pass on build 26200 through **our own
COM interop — no third-party DLL**:

| # | Primitive | Status |
|---|---|---|
| a | create + **name** a desktop | ✅ (0.2; naming via manual HSTRING) |
| b | switch to a desktop | ✅ (0.2) |
| c | move a (foreign) window onto a desktop | ✅ (0.3; internal IApplicationView path) |
| d | capture a global hotkey with no focus | ✅ (0.1; `Ctrl+Alt+Arrow`, FIRES press-test outstanding) |

**Go native.** komorebi/GlazeWM stays documented as the fallback (`PLAN.md` §9 risk 1)
but is not needed: native control is solid and the interop is ~150 lines we own. The
whole build-fragile surface — `IVirtualDesktopManagerInternal`, `IVirtualDesktop`,
`IApplicationViewCollection`, `IApplicationView`, and their per-build GUIDs — is
quarantined behind **`IDesktopController`** (M1 Phase 1.1), so a Windows update that
shifts a GUID is a one-file change, and komorebi remains a drop-in alternate
implementation of the same interface if the churn ever gets untenable.

### Residual items to carry into M1 (not blockers)
- **FIRES press-test** for `Ctrl+Alt+Arrow` (0.1) — I can't press keys; confirm before
  Phase 1.3 wires hotkeys.
- **Multi-monitor** (`PLAN.md` §5): confirm `SwitchDesktop` moves all 3 monitors as one.
- **HSTRING helper** + the `IApplicationView` resolve both need to live in the real
  `Hypertree.Platform.Windows` layer behind `IDesktopController`.
- Consider maintaining a **per-build GUID table** (24H2/25H2 done) so a future OS bump
  is a data change, not a code change.

## Phase 0.4 — Decision: native vs. komorebi  ⏳
_(pending)_
