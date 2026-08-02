# Session restore — recipes & the staging executor

Give a branch back the *work*, not just the empty rooms. A branch remembers what was
open on each of its desktops, and can put it back on demand.

This note supersedes the first cut (a GUID snapshot + a "switch to each desktop and
hope the window lands there" restore). Two reframes replaced it, both from the design
discussion:

1. **A session is a *recipe*, not a snapshot.** An inspectable, ordered list of
   "launch this, place it there" steps, keyed by desktop **label** — not OS GUID. This
   makes it survive reboots (desktops are recreated, not re-found), makes it diff-able
   and editable, and unifies it with **launch recipes on templates** (ideas §4/§2): a
   saved session and a hand-authored workspace template are the *same* thing, run by the
   *same* executor. "Session restore" is just one way to *author* a recipe.
2. **Launch everything in one place, then *move* — don't hop and hope.** Everything opens
   on a throwaway **staging** desktop where new windows reliably appear; placement is then
   an explicit, observable step (find the window, move it to its target) behind a blocking
   progress overlay. Deterministic where the first cut was probabilistic.

## What carries over

The **capture** half of the first cut is the recipe *generator* and is kept:
`SessionCapture` / `CapturedApp` and the Win32 executable-path read (`WindowInfo.
ExecutablePath`, `QueryFullProcessImageName`). The provisional command-palette
**restore** (the hop-and-hope one) is superseded and will be replaced by the executor
below; capture stays.

## The recipe model

A recipe describes a whole workspace, layered on the existing layout snapshot
(`SnapshotStore`) — a recipe ≈ *layout + per-desktop commands*.

```jsonc
// recipes.json
{
  "Name": "feat-123",
  "Desktops": [                          // ordered; created in this order on restore
    {
      "Label": "api",                    // desktops keyed by LABEL, not GUID (reboot-proof, inspectable)
      "Steps": [
        {
          "Target": "C:\\…\\Code.exe",   // exe path, AUMID (packaged), file, folder, or URL — shell-run
          "Arguments": null,
          "WorkingDirectory": null,
          "Placement": { "Desktop": "api" }     // v1: desktop only. Monitor/geometry: later phases.
        }
      ]
    }
  ]
}
```

- **Keyed by label** so restore *creates* the desktops; a reborn machine with new GUIDs
  is a non-issue.
- **`Target`** reuses the launcher's shell-execute contract, so recipes can hold apps,
  files, folders, or URLs — and packaged apps once we capture AUMIDs.
- **`Placement`** is a growing struct: `{ Desktop }` in v1; `{ Desktop, Monitor, State }`
  and eventually `{ …, Rect }` in later phases (see Phasing). Written so old recipes
  missing the richer fields still run.

## The executor

Restore runs a recipe through a state machine behind a **blocking overlay**.

**Lifecycle**
1. Create the recipe's target desktops (by label) as a new branch, plus one **staging**
   desktop; switch to staging.
2. For each step, **sequentially** (see matching):
   - `not started → creating`: shell-launch `Target` on staging.
   - `creating → placing`: the step's window has appeared (see below) — move it to its
     target desktop (v1) / monitor+state (later).
   - `placing → done`.
   - Any failure → `error/issue` with a reason.
3. When all steps settle, remove the (now-empty) staging desktop and land the user in the
   new branch.

**Matching a launched app to its window** — the crux, because `ShellExecute` won't hand
back a usable handle (packaged apps launch via `explorer.exe`; stub launchers exit at
once):

- **Snapshot** the set of top-level app-window handles *before* the step.
- **Launch**, then **poll** (~150 ms, up to a per-step timeout) for a *new* handle whose
  process **executable path matches** the step's target.
- **First match** → `placing`. **Timeout with no new window** → `error/issue`, reason
  "no window appeared" — which is exactly the single-instance-app case (it focused an
  existing window instead of opening a new one).
- Steps launch **sequentially** so "which new window is which" stays unambiguous.
  (Parallel launches only work when every exe is distinct; serial + the overlay makes the
  wait acceptable.)

## The blocking overlay

A pinned, full-screen overlay (reusing the map's pin-to-all-desktops infra) showing every
step as a card with its state — `not started`, `creating`, `placing`, `done`,
`error/issue` — grouped by target desktop, so the user watches the workspace assemble.

- **Cancel** (button or `Esc`) → confirm prompt, because stopping midway leaves launched
  windows around: "Stopping now leaves some windows open — Hypertree can try to clean up,
  or you can do it by hand."
- On an **`error/issue`**, offer three choices (matches the discussion): **continue**
  (leave that one, carry on), **abort + auto-clean**, **abort + manual**.

## Error & abort semantics

- **"Already open elsewhere"** (window found, but it was already on another desktop, or a
  single-instance app reused its process) is an **info state, not an error**: *skip and
  note it* ("Slack — already running, left where it is"), with an optional "move it here".
  Never yank a pre-existing window into the branch silently.
- **Auto-cleanup is deliberately narrow** (per the decision): it may close a launched
  window **only when we are certain it is still on our staging desktop** — the residue of
  a step that never got placed. Anything already **moved to a target desktop** (`done`),
  and anything we didn't launch, is **never touched**. Plus it removes the staging desktop
  and any empty desktops we created. So auto-clean can only ever close scratch on our own
  scratch desktop — it can't cost a user unsaved work in a window that reached its home.
- **Manual abort** removes only the empty scaffolding and hands back a **list of what was
  launched** for the user to sort out.

## Placement — phased fidelity

| Layer | Captures | Places by |
|---|---|---|
| **v1 (desktop only)** | which desktop | virtual desktop assignment (`MoveWindowToDesktop`) |
| **Monitor** (future) | monitor index + maximized/windowed | desktop + monitor + restore state |
| **Exact geometry** (future) | monitor + window rect + state | desktop + monitor + exact rect |

**Exact geometry is explicitly a future phase, not dropped.** It's the highest-fidelity
option and the most fragile (per-monitor DPI, apps that ignore or override their restore
bounds, splash windows that move themselves), which is why it earns its own phase rather
than riding along in v1. Capturing it means recording each window's monitor, rectangle
and maximized state at save time; placing it means positioning after the desktop move.

## Phasing

- **A — recipe model + generator + inspector.** Turn a branch capture into an inspectable
  `recipes.json`; view a recipe's steps per desktop. No execution. (Builds on the retained
  capture code.)
- **B — the executor + blocking overlay.** Staging lifecycle, sequential launch,
  match-by-new-window, move-to-desktop, the state-machine overlay with cancel and the
  three-way error choice. **Desktop placement only.** Land-in-new-branch + tear down
  staging. Abort/cleanup per the rules above.
- **C — monitor placement.** Capture monitor + maximized state; place onto the right
  screen.
- **D — exact geometry.** Capture and restore window rects. (Preserved here so it isn't
  lost.)

Related surfaces once the engine exists: **launch recipes on templates** (author a recipe
by hand, same executor) and the **resume-card-on-dive** (offer restore when you enter a
branch) — ideas §4.

## Editing steps (working directory & arguments)

Capture only ever knows a window's **executable** — not the document, folder or
command line behind it. That's fine for most apps, but not for the ones keyed to a
location: **VS Code is a singleton per folder**, so relaunching `Code.exe` bare opens a
blank window, not your project; a terminal wants to start in the right directory.

The `RecipeStep` already carries `Arguments` and `WorkingDirectory`, so the answer is to
let the user **specify** them: the "Sessions…" manager opens a recipe into a detail hub
where each step is editable (target / arguments / working directory, reusing the
custom-command form). So a VS Code step gets the folder as an argument, a terminal step
gets its working directory, and restore relaunches them usefully.

*Future nicety:* best-effort **auto-capture** of a process's working directory (via the
PEB) to pre-fill the field at save time — helps terminals and many apps, though not the
VS Code singleton (whose folder isn't in any process's command line), so editing stays
the reliable path.

## Known unknowns

- **Authoring placement.** How a user says "this app on that screen" without it being
  fiddly — the capture path gets it for free, but hand-editing a recipe's placement is an
  open UX question (deferred with Phases C/D).
- **Packaged/Store apps.** Robust relaunch wants the **AUMID**, not the `WindowsApps\…`
  path; the matcher then keys on the packaged process. A capture-AUMID pass slots in
  before/with Phase B.
- **Apps that never make a distinct window** (tray-only, single-instance focus-only): the
  matcher times these out into `error/issue` by design — there may be nothing better to do
  than report them.
