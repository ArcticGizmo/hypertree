<h1 align="center">Hypertree</h1>
<p align="center">
 <img src="./landing-icon.png" width="150" />
</p>

<p align="center">
<strong>4D chess of virtual desktops.</strong>
</p>

<br>

Hypertree is a **Windows** tray app that gives a task — a git worktree, a feature, a
side quest — its own **branch**: a named set of virtual desktops that hangs _below_ an
anchor desktop. Your day-to-day desktops stay on the usual horizontal axis. `Ctrl+Alt+↓`
_dives_ into a branch, you work as normal, `Ctrl+Alt+↑` _surfaces_ back out — landing
exactly where you left off.

So switching context stops meaning "hunt through a wall of lookalike windows."

### At a glance

- **Dive and surface** — drop into a branch's desktops and climb back out with a keystroke; a flash of the map shows where you landed.
- **A map of everything** — one overlay of every desktop and branch, with **blue** marking where you are and a live preview of what each action would do.
- **Find or create in one box** — a finder to jump to any desktop, or make a new one named whatever you typed.
- **Move windows across desktops** — pick up the windows on the current desktop, navigate to the destination, and drop them there.
- **Reusable branches** — save a branch recipe as a template, or snapshot the whole arrangement as a layout you can restore later.
- **Your desktops stay yours** — Hypertree only ever tears down desktops it created; the ones you made by hand are never touched.
- **Stays out of the way** — lives in the tray, remembers your branches across restarts, and can start when you log in.

## How it works

Windows already has virtual desktops in a single horizontal row. Hypertree adds a
**second axis**: a _branch_ is a stack of desktops that lives below an anchor on that
row. You keep working in real Windows desktops the whole time — Hypertree just gives
them structure and a fast way to move between them.

- **`Ctrl+Alt+↓` / `Ctrl+Alt+↑`** dive into and surface out of the branch below where you are.
- **`Ctrl+Alt+←` / `Ctrl+Alt+→`** move along the current row.
- Each jump **flashes the map** and holds it while you keep the chord down, so you see the move before it fades. Hypertree remembers where you came from and floats it to the top of the jump list, so hopping back is one keystroke.
- By default the **first press just brings the board up** — you look before you leap. Keep the modifiers held and press again to move. Turn it off in **Settings → Navigation**.

(Every shortcut is rebindable — the defaults avoid `Win+Ctrl+Arrow`, which Windows
reserves for its own desktop switch.)

## Features

### Navigation

- Dive / surface / move-left / move-right on `Ctrl+Alt+Arrow`, driving the whole desktop tree from the keyboard.
- The board flashes on every jump and stays up while the chord is held — a hold-to-keep preview, not a blink.
- **Show before moving** (on by default): the press that raises the board doesn't also move you, so you get your bearings first; every press after it, while the board is still up, navigates as usual.
- The desktop you came from is remembered and offered first, so returning is instant.

### The map

- **`Ctrl+Alt+P`** opens the command palette; **Open map** shows the whole arrangement, with blue marking where you are.
- On the map: **`r`** renames a desktop, **`n`** adds one, **`Del`** removes a desktop and **`Shift+Del`** removes a whole branch — each behind a confirm.
- The map is also where you **rearrange**: **`Ctrl+Arrow`** moves the selected desktop along its row or into the branch above/below, and **`Shift+↑↓`** re-slots the selected branch in the stack. Both work by mouse too — drag a desktop between branches, or drag a branch box to move it.
- Every card previews its result before you commit — a jump highlights where you'd land; a template shows the branch it would build.

### Finder & command palette

- **`Ctrl+F`** (from the map) opens the finder: type to jump to any desktop, or create one named your query.
- The command palette (`Ctrl+Alt+P`) gathers everything — jump to a desktop, open the map, manage templates and layouts, reach settings, or quit — each with the live map behind it.

### Move windows

- **`Ctrl+Alt+M`** picks up every window on the current desktop; navigate to the destination and drop them in one move.

### Branches, templates & layouts

- A **branch** is a named set of desktops hanging below an anchor — build one from the branch card, naming it and the desktops it holds.
- **Templates** are reusable branch recipes that pre-fill the desktop set, so you stop retyping the same one.
- **Layouts** snapshot the whole desktop-and-branch arrangement under a name; restore it later, or reset back to a single clean desktop.

### The desktop-name pill

- A persistent pill over the taskbar names the desktop you're on — prefixed with the branch name, in the branch's colour, when you're inside one.
- It auto-hides while the cursor is near it, so the taskbar underneath stays clickable.

### The command line — `htree`

A small console companion that ships beside the tray, for driving Hypertree from a shell or wiring it
into other tools.

```
htree status            # where you are now      ->  perch/docs
htree list              # the stack, top to bottom, main in its slot
htree list --all        # …expanded to every desktop
htree goto perch        # jump to a branch, at its resume desktop
htree goto perch/docs   # …or to a named desktop on it
htree watch             # stream your position as it changes
```

- `status` is cheap enough to sit in a **shell prompt** — it reads one small file and exits, with no
  round trip to the tray. `htree help` has ready-made PowerShell and bash snippets.
- `--json` on any command, and **exit codes** on all of them (`0` done, `1` no tray, `2` unknown target,
  `3` bad usage, `4` the tray refused), so scripts can branch on what happened.
- Branch names match exactly, then case-insensitively, then by unique prefix — so `htree goto pe` works.
  An **ambiguous** name is refused rather than guessed at, and lists the ids to pick from.
- Reads come from `%APPDATA%\hypertree\status.json`, which the tray keeps current — **including switches
  made outside Hypertree**. Only `goto` talks to the tray, over a per-session named pipe.
- Installing adds the install directory to your **per-user PATH**, so `htree` and `hypertree` both resolve
  anywhere; uninstalling removes it again. No elevation needed, and terminals already open need
  restarting to see it.

### Changelog

- After the version changes, a **"what's new"** window lists just the releases since the one you were last on.
- View the full changelog any time from **Settings → Changelog**, and turn the post-update pop-up off there (or from the window itself).

### Settings & tray

- Lives in the system tray: **left-click** for the command palette, **right-click** for the menu, with the running version in the header.
- Settings covers **start-on-login**, the **desktop-name pill**, **show-before-moving**, **rebinding every hotkey** (click a shortcut, press the new combination), and the changelog options.
- **One copy at a time** — launching Hypertree again while it's already in the tray opens the running one's command palette instead of starting a rival that would fight it for the hotkeys.
- Branches, layouts, and settings persist under `%APPDATA%\hypertree`; a missing or corrupt file falls back to defaults rather than blocking startup.

## Running it

Hypertree is **Windows-only** and runs from source today (there's no installer yet).

Requirements: **.NET 10 SDK**.

```
run.bat
# or: dotnet run --project src/Hypertree.App/Hypertree.App.csproj
```

It starts in the tray. Turn on **Settings → Startup → "Start Hypertree when I log in"**
to have it come back with Windows.

> **Note:** virtual-desktop control on Windows has no supported public API and shifts
> between builds — it's the one genuinely load-bearing primitive here. If a Windows
> update ever breaks switching or moving desktops, that's the first place to look.

## Development

The solution splits into four projects so the navigation logic stays OS-free and testable:

| Project | What it is |
|---|---|
| `Hypertree.Core` | The UI-free, OS-free navigation model (Model P — dive / surface) and the platform-service _interfaces_. Plain `net10.0`, so it's fully unit-testable against a fake desktop controller. Also owns the two outward-facing contracts: the status file and the control protocol. |
| `Hypertree.Platform.Windows` | The Win32 implementations — virtual-desktop control, global hotkeys, window moving, start-on-login. |
| `Hypertree.App` | The Avalonia tray app: the HUD flash, the map overlay, palettes, prompts, and settings. Composition root is `PlatformServices`. |
| `Hypertree.Cli` | `htree.exe` — a separate console binary, because the tray is a `WinExe` with no console attached. Published ahead-of-time compiled so it starts fast enough to live in a shell prompt. |

Build and test:

```
dotnet build hypertree.slnx
dotnet test  hypertree.slnx
```

To build a real Velopack package locally — the only way to exercise what only exists inside an
installer, namely the PATH entry going on at install and coming back off at uninstall:

```
publish.bat              # pack to releases\
publish.bat --install    # pack, then run the installer
publish.bat 0.2.0        # pack a specific version
```

> **`publish.bat --install` installs over any existing Hypertree** — same `packId`, same
> `%LocalAppData%\Hypertree`. Uninstalling afterwards removes it; reinstall from the GitHub releases
> page when you're done. PATH changes don't reach terminals that are already open.

> **Publishing `htree`** needs the **Desktop development with C++** workload (native AOT uses the MSVC
> linker). A plain `dotnet build` doesn't — only `dotnet publish -r win-x64`. CI's `windows-latest`
> carries it, so the release workflow is unaffected. `publish.bat` falls back to an ordinary
> self-contained build when the linker is missing, so local packaging still works — that `htree` starts
> in ~127 ms rather than single digits, which only matters if you've put it in a shell prompt.

`HYPERTREE_STATE_DIR` relocates `%APPDATA%\hypertree` for both binaries — used by the end-to-end CLI
tests to run against a scratch directory rather than a live tray's state.

The UI is [Avalonia](https://avaloniaui.net/). To render a design surface to PNG without
launching the tray:

```
dotnet run --project src/Hypertree.App -- --shot captures
```

### Icons

The logo lives in a single source-of-truth vector file, [`hypertree.svg`](./hypertree.svg).
Every raster asset — the tray icon, the `.exe` icon, the in-app logo, and this README's
header — is generated from it, so there's only one file to edit.

After changing `hypertree.svg`, regenerate and commit the results:

```
powershell tools/gen-icons.ps1   # PowerShell
tools\gen-icons.cmd              # cmd
# or directly: dotnet run --project tools/IconGen
```

This writes `src/Hypertree.App/Assets/icon.png`, `src/Hypertree.App/Assets/icon.ico`
(multi-resolution), and `landing-icon.png`.

### Docs

- **[docs/PLAN.md](docs/PLAN.md)** — the design source of truth: decisions, open questions, milestones.
- **[docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md)** — the build plan: how the milestones map onto engineering phases.
- **[docs/design/p-vs-q.html](docs/design/p-vs-q.html)** — an interactive walkthrough of the two navigation models we compared (open in a browser). Model P won.
- **[CHANGELOG.md](CHANGELOG.md)** — what's landed, release by release.
