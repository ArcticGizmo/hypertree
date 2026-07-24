# Sprig integration — delegate worktree truth, own the spatial layout

> Reframes **M2** (see [`IMPLEMENTATION.md`](../IMPLEMENTATION.md)). The original M2 plan was to
> **lift** sprig's git layer (`IGitService`, `WorktreeInfo`, a reconciler) into hypertree and
> reimplement provisioning. This doc proposes the opposite: **delegate** to sprig — ask it what
> workspaces exist and how each should look as desktops, and keep hypertree focused on arranging
> them. Nothing here is built yet. The contract drafts in §3–§4 exist to be **locked before either
> codebase changes**; `OPEN QUESTION` marks the calls still to make.

Current state it builds on: groups are stood up manually (type labels in `ScopeDialog`) or from the
**group templates** just shipped. Sprig (`../sprig`) already owns isolated worktrees, branches,
ports and docker per *workspace*, persists them to `%LOCALAPPDATA%\sprig`, and exposes a
`--json` CLI. Hypertree is Windows-only; the `Core` / `Platform.Windows` split quarantines OS interop.

---

## 1. Why — the assessment

The two apps have non-overlapping hard parts:

| | Owns | Hard part it already solved |
|---|---|---|
| **sprig** | *what work exists* — workspaces, repos, worktree paths, branches, ports, docker, drift | git worktree lifecycle + a `--json` CLI |
| **hypertree** | *spatial arrangement* — a workspace as a group of named desktops you dive through | virtual-desktop control + the map/HUD |

Delegation beats lifting: one copy of worktree logic, not two drifting copies. Hypertree gets M2's
"git ingestion + provisioning" essentially for free, and the seam already exists (§2). Templates
remain the fallback for ad-hoc / non-sprig groups, so **sprig stays optional**.

**Verdict: worth doing.** The real design work is (a) defining the *layout* contract — sprig has no
notion of one today (grep-confirmed) — and (b) hypertree tracking a group's **provenance** so
reconcile/teardown never touch a worktree. This **supersedes** M2's "lift the git layer" phase.

---

## 2. The seam — sprig's `--json` CLI (already exists)

`Sprig.Cli` already accepts `--json` on every read command: `ls`, `info`, `stack show`,
`reconcile`, `repo ls`, `templates`. So *"ask sprig for current workspaces"* needs **zero sprig
changes**: `sprig ls --json` returns `List<InstanceRecord>`, each carrying what we need —

```
Workspace, Stack, CreatedAt, LastStatus,
Repos: [ { Name, SourcePath, WorktreePath, Branch, Inputs } ],
Ports: { <name>: <number> }
```

**Mechanism choice** (how hypertree talks to sprig):

| Mechanism | Coupling | Verdict |
|---|---|---|
| **Shell out to `sprig … --json`** | loose — a text contract, no shared build | ✅ **chosen** — exists today, survives sprig refactors, no version lock, sprig stays a separate app |
| Read `%LOCALAPPDATA%\sprig` JSON directly | medium — couples to on-disk schema | ✗ bypasses sprig's resolution + validation |
| Reference `Sprig.Core` as a library | tight — shared assembly, same TFM/version | ✗ fuses two apps into one build |

---

## 3. The layout definition (sprig side)

`sprig ls --json` says *what* exists; it does not say *how to visualise it*. That mapping is the one
thing to add on the sprig side. It belongs on the **stack**, which already *produces* every value
for its workspaces (ports, inputs) in a one-directional flow — the desktop layout is the same kind
of produced value.

**Default with no authoring:** workspace → group; **one desktop per repo** (label = repo name, cwd =
worktree path). Derivable from `sprig ls --json` alone → hypertree can ship P0 before any sprig work.

### 3.1 Draft schema — an optional block on `StackDefinition`

`stacks/<name>.json` gains an optional `hypertree` block (absent → the per-repo default):

```jsonc
{
  "name": "web+api",
  "repos": ["my-frontend", "my-api"],
  "ports": ["api_port"],
  "bindings": { /* … */ },

  "hypertree": {
    "schema": 1,
    "desktops": [
      { "label": "SPA",  "repo": "my-frontend", "command": "code ." },
      { "label": "API",  "repo": "my-api",      "command": "code ." },
      { "label": "Logs", "cwd": "${sprig.repo.my-api.path}", "command": "pwsh" }
    ]
  }
}
```

Added to `Sprig.Core/Stacks/StackDefinition.cs`:

```csharp
/// <summary>Optional hint for how hypertree should lay this stack's workspaces out as desktops.
/// Null → hypertree derives one desktop per repo. Purely advisory; sprig never acts on it.</summary>
public HypertreeLayout? Hypertree { get; init; }
```

```csharp
namespace Sprig.Core.Stacks;

public sealed record HypertreeLayout
{
    public int Schema { get; init; } = 1;

    /// <summary>Ordered desktops of the group. Order = left→right within the group.</summary>
    public IReadOnlyList<HypertreeDesktop> Desktops { get; init; } = [];
}

public sealed record HypertreeDesktop
{
    /// <summary>Short caption on the tile, e.g. "SPA". May template over
    /// ${sprig.workspace} / ${sprig.ports.<name>} / ${sprig.repo.<name>.path}.</summary>
    public required string Label { get; init; }

    /// <summary>Registry name of the repo this desktop is "about" (resolves its worktree path). Optional.</summary>
    public string? Repo { get; init; }

    /// <summary>Working dir to open here. Defaults to <see cref="Repo"/>'s worktree path when set.</summary>
    public string? Cwd { get; init; }

    /// <summary>A command hypertree MAY launch on this desktop (phase 2), e.g. "code .", "pwsh".
    /// Advisory — hypertree decides whether/how to honour it.</summary>
    public string? Command { get; init; }
}
```

> **OPEN QUESTION — resolution tokens.** The layout needs a way to reference a repo's worktree path.
> Sprig's `SubstitutionEngine` already resolves `${sprig.workspace}` and `${sprig.ports.<name>}`;
> this proposes adding `${sprig.repo.<name>.path}`. Small, additive — confirm the token name.

### 3.2 Draft command — `sprig layout <workspace> [--json]`

A new read command that returns the **fully-resolved** layout for one workspace: it reads the
instance record + its stack, applies the stack's `hypertree` block (or the default), and substitutes
tokens. Sprig owns resolution; hypertree just renders. Reuses `StackResolver` + `SubstitutionEngine`.

```
sprig layout <workspace> --json
```

```jsonc
{
  "schema": 1,
  "workspace": "feature-x",
  "stack": "web+api",
  "group": "feature-x",
  "desktops": [
    { "label": "SPA",  "repo": "my-frontend", "worktreePath": "C:\\code\\my-frontend--feature-x", "cwd": "C:\\code\\my-frontend--feature-x", "command": "code ." },
    { "label": "API",  "repo": "my-api",      "worktreePath": "C:\\code\\my-api--feature-x",      "cwd": "C:\\code\\my-api--feature-x",      "command": "code ." },
    { "label": "Logs", "repo": null,          "worktreePath": null,                                "cwd": "C:\\code\\my-api--feature-x",      "command": "pwsh" }
  ]
}
```

- **No stack / `--repo` workspace, or no `hypertree` block** → one desktop per repo: `label` = repo
  name, `repo` set, `worktreePath`/`cwd` = the repo's worktree, `command` = null.
- `group` defaults to the workspace name (`OPEN QUESTION` — allow the stack to override the group
  caption, e.g. a `group` field templating `${sprig.workspace}`?).

That's the whole sprig-side surface: **one optional record + one resolver command.** Everything else
(`ls`, `info`) is reused as-is.

---

## 4. Hypertree side

Mirrors the existing `IDesktopController` topology: an interface in `Core`, the CLI-shelling impl in
`Platform.Windows`, a fake in tests.

### 4.1 `ISprigGateway` + DTOs (`Hypertree.Core/Sprig/`)

```csharp
namespace Hypertree.Sprig;

/// <summary>Read-only view of sprig's world, behind an interface so the CLI-shelling impl lives in
/// Platform.Windows and Core/tests use a fake. Best-effort: sprig may be absent (not installed / not
/// on PATH) → <see cref="IsAvailable"/> is false and reads return empty/null rather than throwing.</summary>
public interface ISprigGateway
{
    /// <summary>Whether a usable `sprig` executable was found and answered.</summary>
    bool IsAvailable { get; }

    /// <summary>`sprig ls --json` → the current workspaces (cheap; for the picker).</summary>
    IReadOnlyList<SprigWorkspace> ListWorkspaces();

    /// <summary>`sprig layout <workspace> --json` → the resolved desktop layout, or null if unknown.</summary>
    SprigLayout? GetLayout(string workspace);
}

/// <summary>A row from `sprig ls` — enough to populate the "Add workspace…" palette.</summary>
public sealed record SprigWorkspace(string Name, string? Stack, string? Status, int RepoCount);

/// <summary>A resolved layout for one workspace — the recipe for a group.</summary>
public sealed record SprigLayout(
    int Schema,
    string Workspace,
    string? Stack,
    string Group,
    IReadOnlyList<SprigDesktop> Desktops);

/// <summary>One desktop in a resolved layout. WorktreePath/Cwd/Command feed phase-2 window placement.</summary>
public sealed record SprigDesktop(
    string Label,
    string? Repo,
    string? WorktreePath,
    string? Cwd,
    string? Command);
```

Mapping `SprigLayout` → the existing `Group`: `Group.Name = layout.Group`; one `DesktopRef` per
`SprigDesktop` (label = `desktop.Label`); provision each OS desktop named `<group> · <label>`
(matching today's `CreateGroup`). `WorktreePath`/`Command` are retained on the group for phase 2 and
for provenance.

### 4.2 `SprigCli` (`Hypertree.Platform.Windows/`)

Concrete `ISprigGateway`. Locate `sprig` via: (1) a settings override path, else (2) `PATH`. Run with
a short timeout, capture stdout, deserialize. **Any failure — missing exe, non-zero exit, bad JSON,
timeout — sets `IsAvailable = false` and returns empty/null; never throws into the UI.** Hypertree
needs a tiny process runner (new — it has none today); model it on sprig's `ProcessRunner`. Reads for
the picker run off the UI thread; provisioning is user-initiated so a brief shell is acceptable.

### 4.3 Group provenance (the guardrail)

A group is now either **manual** (templates / typed) or **sprig-backed** (mirrors a workspace).
Teardown and reconcile must know which. Extend the domain + persisted state:

```csharp
// Hypertree.Core/Scopes/Group.cs
public sealed record GroupOrigin(string Kind, string? Workspace); // Kind: "manual" | "sprig"
```

Add `Origin` to `Group` and to `PersistedGroup` (defaulting to `manual` for existing state — safe
migration). **Hypertree only ever removes the OS desktops it created; it never runs `git worktree
remove` or `sprig rm`.** Sprig owns worktree lifecycle.

### 4.4 Commands (command palette)

- **"Add workspace…"** — `ListWorkspaces()` in a palette (reuses the previewed picker). Choosing one
  calls `GetLayout(ws)` → provisions a sprig-backed group. Greyed with a reason when
  `!IsAvailable` ("sprig not found on PATH"), consistent with the disabled-command pattern.
- **"Sync workspaces"** — reconcile all sprig-backed groups against `ListWorkspaces()` (§4.5).
- Replaces the current **"Add branch"** stub; **"Move desktop to group…"** stub is unrelated.

### 4.5 Reconcile against sprig

On map open (and on demand), diff sprig-backed groups vs `sprig ls`:
- workspace present, no group → offer to add it;
- group present, workspace gone (`sprig rm`) → offer to drop the **group** (desktops only), never the
  worktree.

Sprig already has `reconcile`/`doctor` for worktree↔record drift; hypertree mirrors that one level
up (workspace↔group) and defers all worktree truth to sprig.

### 4.6 Phase 2 — window placement (where the value lands)

A freshly provisioned desktop is *empty*. Using each `SprigDesktop.Cwd`/`Command`, hypertree can
launch a terminal/editor at the worktree and move it onto the right desktop via the existing
`IDesktopController.MoveWindowToDesktop`. This is what turns "named empty desktops" into "your
workspace, arranged." Deferred so P0–P2 can ship structure first.

---

## 5. Risks / guardrails

- **Sprig optional.** Everything degrades to templates when `!IsAvailable`. Never a hard dependency.
- **Provenance before teardown.** Without §4.3, reconcile could delete the wrong thing. Non-negotiable.
- **Empty-desktop gap.** Full value needs §4.6; set expectations that P0–P2 deliver structure.
- **Contract versioning.** `schema` on both the layout block and the `sprig layout` payload; sprig
  already versions `StackDefinition`.
- **Don't fight sprig's lifecycle.** Sprig is source of truth; hypertree reconciles *toward* it.
- **CLI is a "dev harness"** by its own comment — stable enough (it already ships `--json`), but the
  contract we depend on (`ls`, `layout`) should be treated as public and kept stable on the sprig side.

---

## 6. Phasing

- **P0 — prove the pipe (no sprig changes).** `ISprigGateway` + `SprigCli` shelling `sprig ls --json`;
  **"Add workspace…"** provisions a group using the **repo-derived default** layout (hypertree
  synthesises it from the `ls` record). Value on day one, zero sprig work.
- **P1 — real layouts (sprig changes).** Add the `hypertree` block to `StackDefinition` + the
  `sprig layout <ws> --json` resolver (§3). Hypertree consumes the resolved layout.
- **P2 — reconcile + provenance.** §4.3 + §4.5.
- **P3 — window placement.** §4.6.

---

## 7. Open questions

- [ ] Substitution token for a repo's worktree path — `${sprig.repo.<name>.path}`? (§3.1)
- [ ] Should a stack override the **group caption** (vs defaulting to the workspace name)? (§3.2)
- [ ] Should **"Add workspace…"** also be able to *create* a workspace (`sprig create`) when none
      exists, or only visualise existing ones? (Ties back to the M2 provisioning artifact — the
      explicit-add model, now sourced from sprig.)
- [ ] Anchoring — does a sprig-backed group hang under a specific main-timeline desktop, or just join
      the stack at the main slot like today's groups?
