# Hypertree — Where next?

A grab-bag of directions for the project, grouped by ambition. Not a commitment —
a menu. The fun/silly stuff at the end is there on purpose.

---

## 1. Close the loop on the original vision: git worktrees

The name is *Hypertree*, the founding problem in [`PLAN.md`](PLAN.md) was worktree
sprawl — but M2 ("`git worktree` ↔ scope create/anchor/remove") never landed. Right
now it's a polished general-purpose desktop organiser that's forgotten its origin
story. Highest-value direction, because it's the thing nothing else does.

- **`htree branch <path>`** — point at a repo, auto-provision a branch of desktops
  (editor / terminal / browser), named after the git branch.
- **Live git awareness in the pill/map** — show the current git branch + dirty/clean
  state on the desktop hosting a worktree. Ahead/behind arrows. A branch box tracking
  a *deleted* worktree flags itself for teardown (answers the open question in §5 of
  the plan).
- **`htree open` from inside a repo** — dive to the branch anchored to *this*
  worktree, or offer to create one. Makes the shell-prompt integration bidirectional
  instead of read-only.

## 2. Make the CLI a real automation surface

The named-pipe protocol and exit codes are already there — lean into it.

- **`htree exec <branch> -- <cmd>`** — run a command "on" a desktop (launch VS Code +
  terminal on the API desktop). Turns branches into reproducible *workspaces*, not
  just empty rooms.
- **Launch recipes on templates** — today templates pre-fill desktop *names*; let them
  also carry "apps to open." Restoring a layout could re-launch the work, not just the
  empty desktops.
- **Watch → webhook / event hooks** — `htree watch` already streams position; let
  people hook "on enter branch X, do Y" (mute Slack, start a timer, flip Do Not
  Disturb).

## 3. Focus & time (the productivity-tool adjacency)

- **Per-branch time tracking** — we know exactly when someone dives/surfaces. That's a
  free, accurate "time spent per feature" log. Export to CSV, or just show "3h 20m in
  `feat-123` today."
- **Focus mode** — diving into a branch optionally hides other desktops from Task View
  / suppresses notifications until you surface.

## 4. Make branches carry their state (workspace, not geometry)

Everything today is about **geometry** — where desktops sit and how you move
between them. A workspace is **context**: the apps, the windows, the note-to-self,
the git state. Templates/layouts snapshot names and arrangement; they don't snapshot
the *work*. This is the highest-leverage direction — it turns a "desktop organiser"
into a "workspace manager", and most of it builds on things already shipped (the
launcher, custom commands).

- **Session restore** — a branch remembers which apps/windows lived on each of its
  desktops. Restart, dive back in, and Hypertree offers to relaunch them onto the
  right desktops. The fusion of launcher + templates + layouts. *(Starting here.)*
- **Launch recipes on templates** (see §2) — a template carries "apps to open", so
  restoring builds the work, not empty rooms. Nearly free now the launcher exists.
- **Per-branch scratchpad** — a tiny note tied to each branch, surfaced on dive:
  "where was I / what's next." The pill already knows the branch; a one-line resume
  note is cheap and disproportionately valuable against context-switch cost.
- **Resume card on dive** — instead of only flashing the map, optionally show a small
  card: branch note, last-active time, git branch, apps present. A "you are here,
  here's what this was" beat.

## 5. Finding & moving windows (the founding pain)

The README opens with "hunt through a wall of lookalike windows", but the tools for
*windows specifically* are thin — move-all-windows is the only one.

- **Global window finder** — a chord listing every window across *all* desktops with
  previews; pick one, jump to its desktop. Answers "where's my Figma window?" better
  than anything Windows offers.
- **Send a single window to a branch** — the granular counterpart to move-windows
  (which grabs everything on a desktop). A "send this window to branch X" chord or
  right-click people reach for constantly.
- **Sticky / follow-me windows** — pin Slack, music, or a notes window so it's present
  on every desktop, or follows you as you navigate. Windows can't do this well.

## 6. Cheap, high-delight adjacencies

- **Assignment rules** — "always open Slack on the comms desktop." New windows of an
  app auto-route. Branches that maintain themselves.
- **Number-jump** — `Ctrl+Alt+1..9` leaps straight to branch N, faster than the finder
  for the 3–4 branches you touch daily.
- **CLI event hooks** (pulled forward from §2) — `on-enter <branch> → run <cmd>`. Where
  DND, timers and "mute Slack in deep-work branches" become user-scriptable rather than
  features to build one by one.
- **Sync-friendly state** — store branches/templates/layouts as a human-readable file
  you can commit or drop in OneDrive: the same workspace on two machines.

## Fun / silly bucket 🎈

- **Sound design.** A subtle *dive* whoosh (descending) and *surface* pop (ascending)
  on the vertical axis. Optional, off by default, but spatial audio reinforces the
  depth metaphor better than any HUD.
- **Achievements / stats-wrapped.** "You dove 47 times today." "Deepest branch: 5
  desktops." A year-in-review "Hypertree Wrapped." Pure dopamine, near-zero risk.
- **Konami code** on the map that briefly renders the whole tree in glorious ASCII, or
  does a barrel roll (Avalonia render-transform, ~15 lines).
- **Breadcrumb trail / "undo my navigation."** A back-button history of desktops
  visited, so `htree back` retraces your steps like a browser.
- **Boss key** — one chord that instantly surfaces to a clean day-to-day desktop. The
  oldest trick in the book, still delightful.

## Recommendation

Do **#1 (worktrees)** for real — it's the differentiator and the promise the README's
own tagline makes. Pair it with **launch recipes (#2)** since they compound, and
sprinkle in **stats/Wrapped** as the cheap-but-delightful win.
