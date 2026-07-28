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

## Fun / silly bucket 🎈

- **Minimap "you are here" as a metro map.** Render the desktop tree like a subway
  diagram — branches as coloured lines, your position a blinking train. Fits the "4D
  chess" branding perfectly and would be genuinely gorgeous.
  → **Built** on the `metro-map` branch: a persisted appearance setting (Settings →
  Appearance, or `v` on the map) that applies to every board surface, with a fully
  interactive map. See [`docs/design/metro-map.md`](design/metro-map.md).
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
