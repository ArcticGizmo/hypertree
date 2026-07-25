# Hypertree — Implementation Plan

> Give git worktrees a **scope**: a named set of Windows virtual desktops you can
> *dive into* and *surface out of*, so switching between worktrees stops meaning
> "hunt through a wall of lookalike windows."

Status: **design locked, feasibility unproven.** This document is the source of
truth for what we decided and what we still need to answer before writing code.

---

## 1. The problem

Heavy virtual-desktop user, one desktop per concern (SPA / API / Mobile). Works
great — until git worktrees multiply. Now every worktree wants its own SPA / API /
Mobile trio, they all live in one flat desktop list, and finding the right
repo/terminal/window becomes the very wading-through-windows the desktops were
meant to prevent.

## 2. The idea, in one line

Windows virtual desktops are a **flat, single axis**. Hypertree adds a **second
axis (depth)**: your day-to-day desktops stay on the horizontal line; each worktree
is a **scope** that hangs *below* an anchor desktop. You dive down into a scope,
work as normal (still moving left/right *within* it), and surface back out.

## 3. The decision — Model "P" (dive / surface)

We compared two models (see `docs/design/p-vs-q.html`, interactive):

- **P — dive / surface:** scope is its own level; `↓` dives in, `↑` surfaces out;
  `←/→` is scoped to the level you're on. **← chosen.**
- **Q — inline expand:** scope splices into the single strip; one axis, no modes,
  but the strip grows without bound and a scope has no felt edges. Rejected — it
  reintroduces the "where does this scope begin/end" problem we're trying to kill.

### Locked sub-decisions (these are what make P feel like a *place*)

1. **Anchor = spatial memory.** A scope hangs beneath exactly one day-to-day
   desktop. "feat-123 lives under my Web desktop" is a location you remember.
2. **Surface returns to the entry point, always.** `↑` from anywhere in a scope
   lands you back on that scope's anchor. Predictable pop, never ambiguous.
3. **Re-entry resumes where you left off.** Diving back into a scope lands on the
   desktop you *last used* inside it, not its first. This is what makes it feel
   persistent rather than freshly launched.
4. **The HUD chip is the source of truth.** Native Task View is 1-D and cannot
   render the second axis, so a small on-screen "where am I" readout
   (`▸ feat-123 · API (2/3)`) is a first-class feature, not a nicety.

### Keymap (illustrative bindings)

| Action | Illustrative key | Notes |
|---|---|---|
| Move within current level | `Win+←` / `Win+→` | same muscle memory as today |
| Dive into scope anchored here | `Win+↓` | **the new axis** |
| Surface back to anchor | `Win+↑` | **the new axis** |
| Jump scope→scope (optional) | `Win+Ctrl+↓` | "scope switcher", skip surfacing |

> `Win+arrow` is really Windows window-snap, so a real build lands on
> `Win+Ctrl+arrow` or a custom hotkey layer. The **shape** (add up/down for depth)
> is what's fixed; the exact chord is an implementation detail.

## 4. Glossary

- **Scope / stream** — one worktree's set of desktops (e.g. SPA, API, Mobile).
- **Anchor** — the single day-to-day desktop a scope hangs beneath.
- **Level** — either "day-to-day" (top) or "inside scope X".
- **Dive / surface** — enter / leave a scope along the vertical axis.
- **HUD** — the on-screen readout of current level + position.

## 5. Open questions to resolve BEFORE building

These are UX-shaping; a couple could still change the model, so answer them first.

- [ ] **Worktree removal.** When `git worktree remove` runs, what happens to the
      scope and its live windows? (Auto-tear-down? Orphan warning? Grace period?)
- [ ] **Scope creation.** How does a *new* worktree get its desktops? On demand at
      first dive? A `hypertree new <branch>` command that provisions + anchors?
- [ ] **Multi-monitor (user has 3+).** Native desktops switch *all* monitors at
      once — which fits "whole array = one section." Confirm that holds for scopes
      too, and decide what a dive does across monitors.
- [ ] **Persistence.** Does the anchor→scope map and "last-used desktop per scope"
      survive reboot? Where is that state stored?
- [ ] **HUD rendering.** Always-on tiny overlay vs. flash-on-switch. Per-monitor?
- [ ] **Empty/edge navigation.** `↑` from day-to-day (no-op?), `↓` from a desktop
      with no scope (no-op / offer to create?), leftmost/rightmost edges.

## 6. Technical feasibility (spike targets — prove or kill)

Windows has **no supported virtual-desktop API**; everything drives an
undocumented COM interface that changes per Windows build. Candidate primitives:

- **`VirtualDesktopAccessor.dll`** (Ciantic) — functions for create/switch/move,
  callable from AutoHotkey / any FFI. Build-specific.
- **MScholtes `VirtualDesktop.exe`** — CLI wrapper, versioned per Windows build.
- **AutoHotkey** — hotkey capture + window moves; pairs with the DLL above.
- **komorebi / GlazeWM** — tiling WMs with their *own* scriptable workspaces (do
  not touch native desktops → immune to the undocumented-API churn). Heavier
  paradigm shift; keep as fallback if native control proves too brittle.

**Spike goal (M0):** from a script, reliably (a) create/name a desktop, (b) switch
to it, (c) move a window onto it, and (d) capture a global hotkey — on *this*
Windows 11 build. If that's solid, native is viable; if flaky, pivot to komorebi.

## 7. Architecture sketch

- **Desktop controller** — thin wrapper over whichever primitive wins the spike.
- **Scope state store** — anchor↔scope map + last-used-desktop-per-scope; persisted.
- **Hotkey layer** — captures the chords, translates to controller calls.
- **HUD overlay** — always-available "where am I" indicator (source of truth).
- **Worktree integration** — `git worktree` list ↔ scopes; create/remove hooks.

## 8. Milestones

- **M0 — Feasibility spike.** Prove the 4 primitives above on this machine. Decide
  native vs. komorebi. *(gate: everything downstream depends on this.)*
- **M1 — Manual MVP.** Hard-coded scopes: dive/surface hotkeys + HUD working for
  one worktree's trio. No git integration yet. Validates the *feel* of Model P.
- **M2 — Worktree integration.** `git worktree` ↔ scope create/anchor/remove.
- **M3 — Persistence + polish.** State survives reboot; resume-last-used; edge
  cases from §5 handled; multi-monitor confirmed.

## 9. Risks

- **Undocumented API breakage** on Windows updates → mitigate by isolating all
  desktop calls behind the controller, or choosing komorebi.
- **Feel doesn't survive contact** — depth may be more mode-load than expected.
  M1 exists specifically to test this cheaply before investing in M2/M3.
- **HUD is load-bearing** — if the overlay is janky, the whole "never lost"
  promise fails. Treat it as core, not chrome.
