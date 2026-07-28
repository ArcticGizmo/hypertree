# Foreground handover on desktop switch

> **Resolution (implemented).** The handover lives in `VirtualDesktopController.SwitchTo`
> (`src/Hypertree.Platform.Windows/VirtualDesktopController.cs`), *not* in `NavigationModel.Commit`
> as suggested below — `Commit` is in `Hypertree.Core`, which is deliberately Win32-free and unit-tested
> against a fake, so it can't call `GetForegroundWindow`. Putting it in the single Windows seam also fixes
> the identical problem for the other four `SwitchTo` callers (desktop removal, snapshot restore, …) for
> free. After `SwitchDesktop`, if the foreground window is no longer on the current desktop, it activates
> the top-most ordinary, non-minimised window on the destination (via `ForegroundActivator.ForceForeground`),
> falling back to the shell window. Per-desktop focus memory (option 1) is not done; this is options 2→3.
> **The open question below is now answered — confirmed live** (see that section): a controlled A/B on this
> machine (build 26200) reproduced the stuck state 5/5 on a pre-fix tray and cleared it 5/5 (twice) with
> the fix, using the same probe.
>
> Findings from an external investigation (July 2026). Hypertree's `SwitchDesktop` moves the
> desktop without moving the foreground window, leaving the window you were on as a **cloaked
> foreground window** on the desktop you left. That state cannot be cleared by any other process,
> so every external "focus my window" call is dead until the user clicks something.

Machine: Windows 11 Pro, build 10.0.26200. Hypertree 0.2.2. Found via [Perch](https://github.com/) —
the tray that focuses a Claude Code session's terminal when you click its row.

## Symptom as reported

> Focus a row in Perch, change desktops using the Hypertree integration, then click the same row
> again — nothing happens.

Clicking a *different* row works. Only the row you focused immediately before the jump is dead.

## Mechanism

1. Perch clicks a row → `SetForegroundWindow(terminalHwnd)`. That terminal is now the foreground
   window, on the desktop you're on.
2. You click a Hypertree row → `htree goto` → `NavigationModel.Commit()`
   (`src/Hypertree.Core/Scopes/NavigationModel.cs:298`) calls `_desktops.SwitchTo(id)` and nothing
   else, which is a bare `_vdm.SwitchDesktop(vd)`
   (`src/Hypertree.Platform.Windows/VirtualDesktopController.cs:161`).

   ```csharp
   private bool Commit()
   {
       DesktopId id = CurrentDesktop().Id;
       if (id == _target) return false;
       _target = id;
       _desktops.SwitchTo(id);   // <- desktop moves; foreground window does not
       Save();
       Changed?.Invoke();
       return true;
   }
   ```

   The terminal is still `GetForegroundWindow()`. It is now DWM-cloaked (`DWMWA_CLOAKED == 2`,
   shell cloak) on the desktop you left, and `IsWindowOnCurrentVirtualDesktop` returns false for it.
3. You click the same row again → `SetForegroundWindow` on the window that is *already* the
   foreground window. Windows returns `TRUE` and does nothing. No activation, no desktop switch.

The overlay Perch is clicked on is `WS_EX_NOACTIVATE`, so the click itself never moves the
foreground either. The state persists until the user clicks an ordinary window.

## Evidence

All runs drive real windows on a live session; "target" is a Windows Terminal window parked on
another branch's desktop. `movedBack` = `IsWindowOnCurrentVirtualDesktop(target)` after the call.

### The state is real and deterministic

Built by: `htree goto <target's branch>` → activate target → `htree goto main`.

| Check | Result |
|---|---|
| `GetForegroundWindow() == target` after the jump | **5/5** |
| `IsWindowOnCurrentVirtualDesktop(target)` | false, 5/5 |
| `DWMWA_CLOAKED` on target | 2 (shell cloak) |
| `IsWindowVisible(target)` | still true |

Sampling `IsWindowOnCurrentVirtualDesktop` every 100 ms for 2.4 s after the activation call shows
the desktop **never switches at all** — it is not a switch-then-revert race. In the same sweep, the
4 trials where the foreground had already moved to a window belonging to the destination desktop all
succeeded; the 2 that failed were exactly the ones where the foreground was still the cloaked target.

### It is not caused by how the caller activates

Same recipe, varying only how the window was activated *before* the jump:

| Activation used before the jump | Left stuck | Row click recovered |
|---|:--:|:--:|
| `AttachThreadInput` + `SetForegroundWindow` (Perch's) | 3/3 | 0/3 |
| Plain `SetForegroundWindow` | 3/3 | 0/3 |
| `SwitchToThisWindow` (what Alt+Tab uses) | 3/3 | 0/3 |

A clean, conventional activation produces the identical stuck state. The caller is not doing
anything wrong.

### The state cannot be cleared from outside the process

Attempts to recover, all from a separate process, all in the confirmed bad state:

| Attempt | Result |
|---|---|
| `SetForegroundWindow(target)` | still stuck |
| `SwitchToThisWindow(target, true)` | still stuck |
| `ShowWindow(SW_MINIMIZE)` then `SW_RESTORE` | still stuck |
| Activate a normal window on the *current* desktop, then retry | still stuck — **and the handover itself failed**, 0/6 |
| Activate the shell window (`GetShellWindow()`), then retry | still stuck — handover failed |
| `htree goto <branch hosting the window>` | **recovers** |

The fourth and fifth rows are the important ones: the foreground could not be moved *away* from the
cloaked window either. Once the foreground window is cloaked on another desktop, an external process
cannot move the foreground anywhere. There is nothing for a downstream app to fix it *with* — the
only thing that worked was asking Hypertree to switch back.

### Ruled out

Read through `Hypertree.Platform.Windows` and `Hypertree.Core` and confirmed by probing live windows:

- Foreign windows are never moved, hidden, cloaked or pinned. `MoveWindowToDesktop` is only reached
  from the explicit Ctrl+Alt+M flow; `PinWindow` only touches Hypertree's own windows; there is no
  `SW_HIDE` or `DwmSetWindowAttribute` cloak call in the repo.
- OS desktops are not created/removed around a switch — rows are a pure partition of the flat
  desktop list, so a target hwnd's desktop GUID stays valid.
- No focus guard, no `LockSetForegroundWindow`, no post-switch foreground re-assertion.
  `PollingDesktopWatcher` (250 ms) only calls `AnchorToCurrent()`, which follows.

Nothing Hypertree does *blocks* the activation. The problem is purely the step it omits.

## Why this is worth fixing in Hypertree

- **Only Hypertree can prevent it.** It creates the state, it acts at the moment of the switch, in
  the process that made it, with foreground standing. From outside, the state is unrecoverable.
- **It is not Perch-specific.** Any external activation is dead while it holds: single-instance app
  activation ("already running, focus the existing window"), notification click handlers, IDE
  reveal-in-terminal, other tray tools.
- **There is a symptom independent of any of that:** after a jump, keystrokes go to the invisible
  window on the desktop you left. That is a plain correctness problem, and a mild hazard when the
  invisible window is a terminal.
- **The OS's own switcher doesn't do this.** Win+Ctrl+Arrow and Task View activate a window on the
  destination desktop. The bare `SwitchDesktop` is an incomplete emulation of the shell's switch.

## Suggested shape of the fix

After `_desktops.SwitchTo(id)` in `Commit()`, if `GetForegroundWindow()` is no longer on the current
desktop, activate one that is:

1. Preferably the destination desktop's last-focused window. Hypertree has no per-desktop focus
   memory today — it tracks desktop cursors only (`Branch.LastUsedIndex`, `_lastVisited`) — so this
   needs a small `DesktopId → hwnd` map maintained as focus changes.
2. Failing that, the top-most visible, uncloaked, non-Hypertree top-level window on the new desktop.
3. Failing that, the shell window — defocusing is less pleasant than restoring focus, but it clears
   the anomaly, which is the part that matters.

`ForegroundActivator.ForceForeground` (`src/Hypertree.Platform.Windows/ForegroundActivator.cs:26`)
already does the `AttachThreadInput` dance and is the natural tool.

Doing the handover *before or as part of* the switch — so the anomalous state never forms — is
likely more robust than repairing it afterwards.

### What not to use

`SwitchDesktopAndMoveForegroundView` (vtable slot 7, declared but never called at
`src/Hypertree.Platform.Windows/ComInterop.cs:78`) is not this. It drags the foreground window
*onto* the destination desktop, which is exactly what a branch manager must never do.

## Open question — RESOLVED (verified live, July 2026)

It was **proven** that the state cannot be cleared from outside the process, but **not** that Hypertree
could clear it from inside — that needed code running in the tray. Now verified on this machine
(Windows 11 build 26200), driving the real tray via `htree goto` across two branches that each host
ordinary windows, with an external probe reading `GetForegroundWindow` / the documented
`IsWindowOnCurrentVirtualDesktop` / `DWMWA_CLOAKED` after each jump. To rule out a probe artefact the
harness waits for the switch to actually complete (the focused source window becomes shell-cloaked,
`== 2`) before measuring, so a "stuck" reading is real rather than premature.

Result of the controlled A/B (5 warm trials each; the very first trial is a cold-start jitter in the
external probe, not the product, and is excluded):

| Tray build | Foreground after the jump | Handover |
|---|---|---|
| **Pre-fix** (`SwitchDesktop` only) | still the source window — now shell-cloaked (`DWMWA_CLOAKED == 2`) on the desktop we left, `IsWindowOnCurrentVirtualDesktop == false`, yet still `GetForegroundWindow()` | **0/5** |
| **With fix** (handover in `SwitchTo`) | a real, uncloaked window on the destination desktop; the source window is cloaked but no longer foreground | **5/5** (repeated twice, different branch pairs) |

The pre-fix column reproduces the exact anomaly this document reported. The fix eliminates it acting at
switch time, from inside the tray — confirming the "very likely" hypothesis.

The probe is kept at `docs/design/foreground-handover-repro.ps1`: it discovers which desktops host
ordinary windows, picks two on different rows, drives `htree goto` between them, and reports the
foreground state after each jump. Re-run it (against a running tray) to regression-check the handover.

## Reproduction

Needs two branches whose desktops both host at least one ordinary window. Substitute a real hwnd for
`$target` (any top-level window on branch `A`'s desktop).

```powershell
# 1. land on the branch hosting $target, and focus it
htree goto A
[Win32]::SetForegroundWindow($target)

# 2. jump away
htree goto B

# 3. observe the anomaly
#    GetForegroundWindow() == $target            -> True   (still foreground)
#    IsWindowOnCurrentVirtualDesktop($target)    -> False  (but on another desktop)
#    DwmGetWindowAttribute(DWMWA_CLOAKED)        -> 2      (shell-cloaked)

# 4. nothing external can recover it
[Win32]::SetForegroundWindow($target)        # returns TRUE, does nothing
[Win32]::SetForegroundWindow($someOtherWin)  # cannot even move the foreground away
```

`IsWindowOnCurrentVirtualDesktop` and `GetWindowDesktopId` here are the **documented**
`IVirtualDesktopManager` (CLSID `aa509086-5ca9-4c25-8f95-589d3c07b48a`), not the internal interface.
Note that `WS_EX_TOOLWINDOW` windows return `GUID_NULL` from `GetWindowDesktopId` and report as
on-current-desktop always, so pick an ordinary window as the probe target.

## Downstream note

Perch will likely add a fallback: detect via `IsWindowOnCurrentVirtualDesktop` that its activation
didn't land and, when a Hypertree tray is running, map the window's desktop GUID against the GUIDs
published in `status.json` (`rows[].desktops[].id`) and issue `htree goto <branch>/<desktop>`. That
self-heals against Hypertree builds without the fix, and is the only recovery that was observed to
work. It is a workaround, not a substitute for the handover.
