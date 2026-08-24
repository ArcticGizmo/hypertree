# Taskbar "needs attention" flashing on desktop switch

> **Status: fix implemented — pending live confirmation.** Root cause identified by code trace; the fix
> was applied tray-side in `ForegroundActivator.ForceForeground` (see **Resolution** below). This
> document is the companion to [`foreground-handover-on-switch.md`](foreground-handover-on-switch.md) —
> the two describe **the same code** (`VirtualDesktopController.HandoffForeground` →
> `ForegroundActivator.ForceForeground`) seen from opposite sides. The handover was *added* to fix the
> cloaked-foreground bug; the flashing is a side effect of that same handover. The fix **preserves** the
> handover (and so the cloaking fix) — it changes only *how* the handover activates its target.
>
> ## Resolution (implemented)
>
> The fix lives entirely in `ForegroundActivator.ForceForeground`
> (`src/Hypertree.Platform.Windows/ForegroundActivator.cs`), which is the single choke point every
> switch path funnels through. It became two-stage:
>
> 1. **Plain activation first.** `SetForegroundWindow` + `SetFocus`, then check `GetForegroundWindow()
>    == handle`. When the caller already holds foreground standing — the tray does on the **hotkey**
>    path (it received `WM_HOTKEY`) and the **overlay-click** path — this activates cleanly and returns,
>    with none of the `AttachThreadInput` churn that itself could provoke flashing. This is the change
>    from the old code, which *always* did the attach dance.
> 2. **Forced activation as fallback.** Only if the plain call didn't land (the standing-less **CLI**
>    path — `htree goto` asks the tray to switch, and a console `htree.exe` is not the foreground
>    process, so it has nothing to hand over): temporarily zero the foreground-lock timeout
>    (`SPI_SETFOREGROUNDLOCKTIMEOUT`) so `SetForegroundWindow` is *granted* rather than demoted to a
>    flash, plus attach to the current foreground thread's input as a second lever, then restore the
>    timeout and detach in a `finally`. The system setting is neutralised only for the length of the
>    call.
>
> **Why not the originally-recommended CLI-side `AllowSetForegroundWindow` grant?** That API requires
> the *calling* process to already be able to set the foreground window. A `htree` invocation runs as a
> console child of the terminal (Windows Terminal / conhost owns the foreground window), so it has no
> standing to grant onward — the grant would silently no-op. Doing the work tray-side, where the
> activation actually happens, is path-agnostic and self-contained, and needs no cooperation from the
> caller.
>
> **Still to confirm live:** that the flashing is gone on all three trigger paths (re-run the probe in
> `docs/design/foreground-handover-repro.ps1`, extended to note flashing windows), and that the
> cloaking fix still holds (that probe already checks foreground/cloak state after each jump).

Machine: Windows 11 Pro, build 10.0.26200.

## Symptom as reported

> When I navigate with Hypertree, elements in my taskbar show as if they want my attention, even
> though they are on a completely different virtual desktop. It does not happen with normal Windows
> virtual-desktop navigation (Win+Ctrl+Arrow / Task View).

Triggered on both the hotkey and CLI (`htree goto`) paths.

## Mechanism

There is **no flash call anywhere in the tree** — no `FlashWindow` / `FlashWindowEx` / `FLASHWINFO`.
The flashing is Windows' *automatic* fallback for a **denied foreground request**, and it is produced
by the foreground handover Hypertree runs on every switch.

1. Navigation ends at `VirtualDesktopController.SwitchTo`
   (`src/Hypertree.Platform.Windows/VirtualDesktopController.cs:137`):

   ```csharp
   public void SwitchTo(DesktopId id)
   {
       if (TryResolve(id) is not { } vd) return;
       _vdm.SwitchDesktop(vd);   // bare internal switch, from a background process
       HandoffForeground();      // then activate a window on the destination
   }
   ```

2. `HandoffForeground` (`VirtualDesktopController.cs:151`) picks the top-most ordinary window on the
   destination and activates it through `ForegroundActivator.ForceForeground`
   (`src/Hypertree.Platform.Windows/ForegroundActivator.cs:26`), the classic
   `AttachThreadInput` + `SetForegroundWindow` dance.

3. **`SetForegroundWindow`, called by a process that does not currently own the foreground, does not
   fail loudly — Windows converts it into a taskbar "needs attention" flash on the target window.**
   This is the documented behaviour: rather than let a background app steal focus, the OS flashes the
   button instead and returns `TRUE`. (The `TRUE` return is why the caller can't tell it was denied —
   see the companion doc's "returns TRUE and does nothing".)

### Why it appears on *other* desktops

A window in the attention/flashing state is surfaced onto the taskbar of **whichever desktop you are
currently viewing**, regardless of which virtual desktop the window actually lives on. Windows'
per-desktop taskbar filtering is bypassed for a flashing window — that is exactly what makes flashing
useful (it's meant to be seen). So a denied activation during the switch lights up buttons for apps
that aren't on the current desktop at all, which is the reported symptom.

### Why the native switch (Win+Ctrl+Arrow / Task View) does not do this

The OS shell performs its switch **and** activates a destination window from inside its own process,
which owns the foreground and has full rights. No `SetForegroundWindow` is ever denied, so nothing
enters the flash state. Hypertree's `_vdm.SwitchDesktop` (internal `IVirtualDesktopManagerInternal`
slot 6, `ComInterop.cs:77`) is a *bare* switch issued from a background tray, so the follow-up
activation is the weak link the shell doesn't have.

## Why the three entry paths differ

`SetForegroundWindow` is granted only under specific conditions (the caller is already foreground, or
its process "received the last input event", etc.). Each way of triggering navigation stands
differently against that gate:

| Trigger | Who calls `ForceForeground` | Foreground standing at call time | Expected |
|---|---|---|---|
| **CLI `htree goto`** | the tray, on behalf of a *separate* CLI process (`Ipc/ControlServer.cs`) | none — the CLI holds the input right and passes **nothing** to the tray | **denied → flash** (clearest repro) |
| **Global hotkey** | the tray, which received `WM_HOTKEY` (`GlobalHotkey.cs:74`) | the *process* is granted the foreground-change right by the hotkey | usually granted, but the post-`SwitchDesktop` moment can still deny (below) |
| **Overlay / map click** | the tray, from a click on a `WS_EX_NOACTIVATE` overlay | the click never activates the overlay, so no clean standing | denied → flash |

Even on the hotkey path — where the process *does* hold the right — the handover runs the instant
after `SwitchDesktop`, when the window being left is being shell-cloaked on the old desktop.
`ForceForeground` attaches to *that* thread (`ForegroundActivator.cs:34`, it attaches to whatever
`GetForegroundWindow()` returns, which is the outgoing window) and then calls `SetForegroundWindow` on
a *different* window. Attaching to the cloaking outgoing thread is the least useful attach possible,
so the bypass can be ineffective and the call still gets flashed. This is consistent with the user
seeing it on the hotkey path too.

## Two sub-variants — and how to tell them apart

The mechanism above is certain; which windows flash is not yet observed:

- **(A) The handover target flashes.** `ForceForeground(target)` is denied, so the destination window
  Hypertree tried to activate flashes instead of activating. Mechanically the direct consequence of
  the trace above.
- **(B) A foreground-lock cascade.** The `AttachThreadInput` churn from a background process perturbs
  the system foreground lock, and *other* apps that were quietly trying to raise themselves
  (chat/mail/updater-type apps) get denied-and-flashed as a side effect. These are windows Hypertree
  never touched.

### How to confirm (observation probe)

Instrument the single choke point and read back whether our own activation actually landed. In
`VirtualDesktopController.HandoffForeground`, after the `ForceForeground(target)` call, log:

- `target` hwnd + title,
- `GetForegroundWindow() == target` **immediately after** the call.

`SetForegroundWindow` returns `TRUE` even when it only flashes, so the return value is useless — the
`GetForegroundWindow() == target` check is the real discriminator:

- If `target` did **not** become foreground → our activation was flashed → **variant A** (and `target`
  is the window the user sees flashing).
- If `target` **did** become foreground yet the user still sees *other* apps flashing → **variant B**
  (the cascade), and those apps are unrelated to the handover.

Watching *which* taskbar buttons flash relative to the logged `target` settles it in one run. The
existing harness `docs/design/foreground-handover-repro.ps1` already drives `htree goto` between two
branches and reads `GetForegroundWindow` after each jump; extend it to also record the set of
top-level windows in the flashing state (or simply note by eye whether the flasher is the logged
target) to label A vs B.

## Prevention options

Ranked by how squarely they hit the root cause. The hard constraint: **keep the cloaking fix.** The
handover exists because a bare `SwitchDesktop` strands the outgoing window as a cloaked foreground
window that nothing external can recover (see the companion doc). Options 1–2 preserve that;
option 4 weakens it and is listed only for completeness.

1. **Give the tray genuine foreground standing at the moment of the switch (best).** If the caller
   owns the foreground, `SetForegroundWindow` is *granted*, not flashed — precisely why the shell
   never flashes. Concretely:
   - **CLI path:** have the CLI call `AllowSetForegroundWindow(trayPid)` before sending the IPC
     request (the CLI *is* foreground in its terminal, so it may grant the right onward), and/or have
     the tray briefly foreground its own pinned, all-desktop overlay window before the handover so it
     holds standing when it activates the destination.
   - **Hotkey path:** the process already holds the right; the remaining issue is the ineffective
     attach — see option 3.

2. **Assert the right explicitly around the handover** — `AllowSetForegroundWindow(ASFW_ANY)` from the
   tray immediately before `ForceForeground`, so the subsequent activation is permitted rather than
   demoted to a flash. Smaller change than (1); needs live verification that it actually clears the
   flash in this post-`SwitchDesktop` window.

3. **Fix the attach target in `ForceForeground`.** Today it attaches to the *outgoing* (cloaking)
   foreground thread. Attaching instead to the destination target's own thread — or skipping the
   attach and relying on the standing from (1)/(2) — removes the least-useful attach that currently
   makes the bypass fail. Pairs with (1) or (2); not sufficient alone on the CLI path.

4. **Stop the flash after the fact** — `FlashWindowEx(FLASHW_STOP)` on the affected window post-switch.
   Whack-a-mole (you must know which window flashed), and it papers over the denied activation rather
   than granting it. Only viable for variant B, and even then unreliable. Not recommended.

5. **Suppress the handover when standing can't be obtained.** Skip `ForceForeground` when the tray
   lacks foreground rights, avoiding the denied call entirely — but this reintroduces the cloaked
   outgoing window the handover was built to prevent. Rejected unless paired with an alternative
   recovery; noted so it isn't re-proposed.

## Recommendation → what shipped

The plan below was **superseded during implementation** — see the **Resolution** block at the top for
what actually shipped and why. In short: the fix took **option 1's spirit (activate with standing, no
needless attach churn)** but delivered it entirely **tray-side** rather than via a CLI grant. The
originally-listed "CLI grants the tray `AllowSetForegroundWindow`" step was dropped because a console
`htree.exe` is not the foreground process and so has no right to grant onward; the tray instead falls
back to zeroing `SPI_SETFOREGROUNDLOCKTIMEOUT` for the standing-less path, which needs no cooperation
from the caller.

Original plan (kept for the reasoning trail):

- ~~CLI: grant the tray the foreground right from the CLI (`AllowSetForegroundWindow`) as part of the
  `goto` IPC.~~ — rejected (no standing to grant from a console child).
- All paths: fix `ForegroundActivator.ForceForeground` so the activation lands as an activation instead
  of a flash. — **done** (plain activation first, forced fallback only when it doesn't take).

Whatever ships must be regression-checked against the companion doc's repro so the cloaking fix stays
intact.

## Reproduction

```powershell
# Two branches whose desktops each host at least one ordinary window.
htree goto A        # land on A, focus some ordinary window there
htree goto B        # jump — the tray switches + hands off foreground with no standing
# Observe: a taskbar button flashes for a window that is NOT on desktop B.
```

The CLI path is the most reliable trigger because the tray carries out the switch with zero
foreground standing (`Ipc/ControlServer.cs` marshals the request onto the UI thread and calls in; no
foreground right is transferred from the CLI).
