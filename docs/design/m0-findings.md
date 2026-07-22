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

## Phase 0.3 — Move window onto desktop  ⏳ next
Plan: use the **documented, stable** `IVirtualDesktopManager.MoveWindowToDesktop(hwnd,
ref Guid)` (no build risk) to move a real window (e.g. Notepad) onto a created desktop,
targeting an hwnd via perch's `EnumWindows`/`GA_ROOTOWNER` walk. This is the last
make-or-break primitive (provisioning a scope's window trio).

## Phase 0.4 — Decision: native vs. komorebi  ⏳
_(pending)_
