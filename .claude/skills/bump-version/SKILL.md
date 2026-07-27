---
name: bump-version
description: Bump the Hypertree version and refresh the changelog. Determines the next version from the LAST GIT TAG (not the csproj values, which can drift), bumps both src/Hypertree.App/Hypertree.App.csproj and src/Hypertree.Cli/Hypertree.Cli.csproj, backfills changelog sections for any tagged versions that were never documented, summarises every change since the last tag into a new CHANGELOG.md section, clears the Unreleased section, and prints the new version plus its changelog entries. Use when the user says "bump version", "bump the version", "/bump-version", or wants to cut a new release entry.
---

# Bump Version

Bumps Hypertree to the next patch version and brings `CHANGELOG.md` up to date.

## Why the tag, not the csproj

The `<Version>` in the csprojs and the `[Unreleased]` changelog section all drift — sometimes a
version gets tagged without the csprojs or changelog catching up, and the two csprojs can even
disagree with each other. The **last git tag is the source of truth**. Always derive the next
version from it, and set both csprojs to it outright rather than incrementing what's there.

## Steps

### 1. Gather everything in one batch

Run this single block first — it collects the tag, the next version, the new commits, and
the dates for any backfill, so you don't make repeated round-trips:

```bash
LAST_TAG=$(git tag --sort=-v:refname | head -1)
echo "LAST_TAG=$LAST_TAG"
echo "=== all tags (oldest→newest) ==="; git tag --sort=v:refname
echo "=== new commits on HEAD since $LAST_TAG ==="
git log $LAST_TAG..HEAD --no-merges --date=short --pretty=format:'%h %ad %s'
echo; echo "=== current csproj versions (both get bumped) ==="
grep -H '<Version>' src/Hypertree.App/Hypertree.App.csproj src/Hypertree.Cli/Hypertree.Cli.csproj
```

Parse `LAST_TAG` as `vMAJOR.MINOR.PATCH`. The next version is the same with **PATCH + 1**
(e.g. `v0.1.0` → `v0.1.1`). Call it `$NEXT` (the csprojs want it without the `v`, e.g. `0.1.1`).
If there are no tags at all, fall back to the app csproj `<Version>` and bump that, and say so.

**Scope is `HEAD`, deliberately.** Only commits reachable from `HEAD` are releasing. Do **not**
use `--all` — it drags in unmerged worktree/branch WIP that isn't shipping, which is both
wrong and slow. If you know specific work landed on another branch that *is* part of this
release, add that branch explicitly; otherwise stay on `HEAD`.

Most commit subjects are self-explanatory — summarise straight from them. Only `git show
<hash>` a commit when its subject is genuinely opaque (e.g. "tweaks", "first pass", "Attempt2").
Skip noise entirely: stash entries (`WIP on…`, `index on…`), pure-WIP commits, and plumbing a
user would never notice. Collapse related commits into the single *net* change they add up to
(see "What to write" in step 4).

### 2. Bump both csprojs

Hypertree ships **two** binaries, each carrying its own `<Version>`. Set both to `$NEXT` (no `v`
prefix), regardless of what they currently say, and leave everything else in the files untouched:

- `src/Hypertree.App/Hypertree.App.csproj` — the tray app (`hypertree.exe`). The tray header, the
  settings page, and the post-update "what's new" check all read it back off the assembly at
  runtime (`App.AppVersion`).
- `src/Hypertree.Cli/Hypertree.Cli.csproj` — the command line (`htree.exe`). `htree --version` and
  the help header read it off the assembly the same way (`Program.Version`).

They ship together and install together, so **the two must never disagree** — a user running
`htree --version` against a tray on a different number has no way to tell which is right. Check
both files even when one already looks correct; they drift independently.

### 3. Backfill any missing tagged versions

A version can be tagged without ever getting a changelog section. From the tag list in step 1,
find tags with **no** matching `## [v…]` heading in `CHANGELOG.md`. For each missing tag
(oldest first), get its commits and date in one call, scoped to that tag's range:

```bash
# fill in <prev_tag> and <tag>; --date=short on the last line gives the heading date
git log <prev_tag>..<tag> --no-merges --date=short --pretty=format:'%h %ad %s'; \
  git log -1 --format='HEADING_DATE=%cd' --date=short <tag>
```

Insert each backfilled `## [v<tag>] - YYYY-MM-DD` (using its own tag date, not today) in the
correct reverse-chronological slot, applying the same summarising and tone rules below.
Do every missing tag, not just the latest.

> Concrete example: tag `v0.1.1` exists but the changelog's newest section is `v0.1.0` — so
> `v0.1.1` needs backfilling from `v0.1.0..v0.1.1` before the new version goes on top.

### 4. Rewrite the changelog

In `CHANGELOG.md`:

- **Create a new section** directly under `## [Unreleased]`, titled
  `## [v$NEXT] - YYYY-MM-DD` using today's date.
- Fill it with the summarised, user-facing bullets from step 1. **Fold in** anything already
  sitting in the `[Unreleased]` section — those changes are part of this release.
- **Clear the `[Unreleased]` section** so it sits empty (keep the `## [Unreleased]` heading
  and the `---` separators; just remove its bullets).
- Preserve the existing file format exactly: heading style, the `---` separators, and the
  reverse-chronological order (newest version on top).

#### The changelog is shipped UI — stay inside the format

`CHANGELOG.md` is embedded in the app (`Hypertree.CHANGELOG.md`) and rendered twice: in the
post-update "what's new" window and in Settings → Changelog. So the file is parsed, not just
read by humans:

- `ChangelogParser` splits on `## ` headings and pulls the version out of `[v0.1.1]`. Keep the
  `## [vX.Y.Z] - YYYY-MM-DD` shape exactly — a heading it can't parse is silently dropped from
  "what's new".
- The renderer (`ChangelogMarkdown`) handles a deliberate subset: `##`, `###`, `- ` bullets,
  and `> ` quotes, with inline `**bold**` / `*italic*` / `` `code` `` / links flattened to plain
  text. **Nested bullets, tables, code fences and images don't render** — don't introduce them.
- Group bullets under `### ` subheadings the way the existing sections do (Navigation, Branches,
  Map & overlays, …) when a release is big enough to warrant it. A small release can be a flat
  bullet list, or a single `### Changed` / `### Fixed` group — match what the release actually is
  rather than padding it into categories.

#### What to write

- **Write for the end user, not the developer.** Describe what changed for someone *using*
  Hypertree. Mention implementation details only when they genuinely matter to the user
  (e.g. "existing branches carry over"). Internal refactors, file moves, and plumbing don't
  get a bullet at all.
- **Lead with the keystroke where there is one.** This app is driven by chords, and the existing
  sections read that way (`` `Ctrl+Alt+M` picks up the windows on the current desktop… ``). If a
  change is reachable by a shortcut, name it.
- **Keep bullets snappy — this is the rule that gets ignored most, so enforce it hard.** One
  line, never two; cut "now", "the ability to", "you can now". Drop the trailing "so you can…"
  rationale unless it says something the change itself doesn't. Prefer "Rebindable hotkeys" over
  "You can now rebind the hotkeys so you can change the shortcuts". After drafting, reread every
  bullet and shorten any that runs long.
- **Describe the cumulative change, not the commit trail.** A version's section is the net
  difference from the previous version — the end state, not the journey. If a feature was
  added, then fixed, then tweaked across several commits, that's *one* bullet describing the
  finished feature. Never list intra-version fixes to something that didn't exist in the last
  release; the user never saw the broken version.

#### Tone

Hypertree's changelog is plain and declarative — it states what the app does now, in the
product's own vocabulary (dive, surface, branch, map, board, pill). No marketing, no emoji, no
exclamation marks, and no jokes; the existing entries earn their character from being specific,
not from being funny. Match that. Never invent a change that didn't happen to round out a
section.

### 5. Report back

Print, plainly:

- The next version number (e.g. `v0.1.1`).
- That both csprojs were set to it (naming them), and what each was on before.
- The exact changelog entries you wrote for that version.
- If you backfilled any missing tagged versions, list which ones and show their entries too.

## Notes

- This skill does **not** commit, tag, or push — it only edits the three files and reports.
- Releases are cut by **pushing a `v*` tag**, which triggers `.github/workflows/release.yml`
  (Velopack build → GitHub Release). So the flow is: run this skill, commit the three files, then
  tag `v$NEXT` and push the tag when you're ready to release.
- The tag must match the csproj versions. Installed copies compare their assembly version against
  the release feed, so a tag that disagrees with `<Version>` makes the update check misreport.
- Only the patch component is bumped. If the user wants a minor/major bump, they'll say so —
  follow their instruction instead of auto-incrementing patch.
