---
name: release-next-version
description: Ship the current branch as the next Hypertree release. Runs the build and test suite locally (Hypertree has no PR CI), opens (or reuses) a PR for the current branch, stops and reports a FAIL if the local gate fails, otherwise squash-merges it, then tags the new main head with the csproj version (vX.Y.Z) and pushes the tag, which kicks off the Release workflow — and watches that workflow until the GitHub Release is published or the build fails. Use when the user says "release next version", "release this", "/release-next-version", or wants to merge the current branch and cut the tagged release.
---

# Release Next Version

Takes the current branch from "done" to "published release": local build + tests → PR → squash-merge →
tag the new `main` head → push the tag → watch the Release workflow to completion. Pushing a `v*` tag
triggers `.github/workflows/release.yml`, which builds, packs and publishes the GitHub Release.

Invoking this skill is the user's go-ahead for the outward-facing steps (pushing the branch, opening the
PR, merging, pushing the tag). Anything *not* listed here (force-pushes, bypassing checks, deleting
branches, re-tagging) is out of scope — stop and ask.

## Repo facts this relies on

- Repo is `ArcticGizmo/hypertree`; **squash-merge only** (merge commits and rebase are disabled). The
  remote branch is **not** auto-deleted on merge — leave it; deleting it is the user's call.
- Tags are **lightweight**, `vMAJOR.MINOR.PATCH`, and must match `<Version>` in **both**
  `src/Hypertree.App/Hypertree.App.csproj` and `src/Hypertree.Cli/Hypertree.Cli.csproj` (the release
  build takes its version from the tag; installed copies compare their assembly version against the
  release feed, and `htree --version` must agree with the tray).
- There is **no PR CI** — the only workflow is `release.yml`, so a PR never has checks. The gate before
  merging is the local build + test run in step 2. If a CI workflow is ever added, wait on its checks too
  (see step 4).
- The Release workflow has a single Windows job, `release`: publish the tray app, publish the AOT `htree`
  CLI into the same folder, Velopack pack, generate and verify `SHA256SUMS.txt`, then create the GitHub
  Release. It fails before publishing if `Hypertree-win-Setup.exe` is missing or the checksum doesn't
  match, so a failed run means **no Release at all** — never a half-populated one. A tag-triggered run's
  `headBranch` is the tag name (e.g. `v0.5.8`). A healthy run takes about 2–3 minutes.
- A Debug copy of Hypertree is often running from `src/Hypertree.App/bin/Debug/…` while the user tries
  things out, which locks the Debug output. Build in **Release** so the gate never collides with it, and
  never kill that process.
- Use the Bash tool (Git Bash) for the snippets below.

## Steps

### 1. Preflight — gather everything in one batch

```bash
BRANCH=$(git rev-parse --abbrev-ref HEAD); echo "BRANCH=$BRANCH"
echo "=== status ==="; git status --porcelain
git fetch origin --tags --quiet
VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' src/Hypertree.App/Hypertree.App.csproj | head -1); echo "VERSION=$VERSION"
CLI_VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' src/Hypertree.Cli/Hypertree.Cli.csproj | head -1); echo "CLI_VERSION=$CLI_VERSION"
echo "LAST_TAG=$(git tag --sort=-v:refname | head -1)"
echo "TAG_EXISTS_LOCAL=$(git tag -l "v$VERSION")"
echo "TAG_EXISTS_REMOTE=$(git ls-remote --tags origin "refs/tags/v$VERSION")"
echo "=== commits on $BRANCH not on origin/main ==="
git log origin/main..HEAD --no-merges --pretty=format:'%h %s'
echo; echo "=== changelog head ==="; sed -n '1,20p' CHANGELOG.md
echo "=== existing PR ==="; gh pr view --json number,state,url,title 2>&1
```

Stop and report (don't try to fix it yourself) if any of these hold:

- `BRANCH` is `main` (or detached `HEAD`) — there's nothing to PR. Say so.
- The working tree is dirty — uncommitted changes wouldn't be in the release. List them and ask
  whether to commit them first.
- There are no commits on the branch ahead of `origin/main`.
- `VERSION` and `CLI_VERSION` differ — the two binaries would ship disagreeing. Suggest `/bump-version`,
  which sets both.
- `v$VERSION` already exists (locally or on the remote), or `VERSION` isn't greater than `LAST_TAG` —
  the version hasn't been bumped. Suggest running `/bump-version` (and committing its two csproj +
  `CHANGELOG.md` edits) first. Never move or overwrite an existing tag.
- `CHANGELOG.md` has no `## [v$VERSION]` section — the post-update "what's new" window would show
  nothing for this release. Suggest `/bump-version`.
- An existing PR for the branch is `CLOSED` or `MERGED` — ask rather than open a second one.

### 2. The local gate — build and test

Hypertree has no PR CI, so this is the check that stands in for it. It takes a minute or two; run it in
the foreground:

```bash
dotnet build hypertree.slnx -c Release -nologo -v q 2>&1 | grep -E " error | warning |rror\(s\)|arning\(s\)" | sort -u
echo "BUILD_EXIT=${PIPESTATUS[0]}"
dotnet test tests/Hypertree.Tests -c Release -nologo -v q 2>&1 | grep -E "FAIL|Passed!|Failed!|error" | head -40
echo "TEST_EXIT=${PIPESTATUS[0]}"
```

- **Both exit 0** — continue to step 3.
- **Anything else** — **stop: this is a FAIL that needs fixing.** Nothing has been pushed yet. Report back
  headed **`RELEASE FAILED — local build/tests did not pass`**, with the compiler errors or failing test
  names (re-run the failing part without the filters if you need more of the output). Don't attempt a fix
  unless the user asks.

A plain build doesn't exercise the release-only steps (the AOT `htree` publish needs the MSVC linker,
which CI has and a dev box may not) — those are covered by the Release workflow itself in step 7.

### 3. Push the branch and open (or reuse) the PR

```bash
git push -u origin HEAD
```

If step 1 found an `OPEN` PR, reuse it. Otherwise create one against `main`:

- **Title:** a concise summary of the branch's net change — if the branch is a single feature, its name,
  matching history (e.g. `Feature/map command`, `Spatial map`); otherwise the headline items.
- **Body:** a short `## Summary` of the user-facing changes (from the commit list in step 1 and the new
  `## [v$VERSION]` section in `CHANGELOG.md`), a `## Test plan` noting the local build + test result from
  step 2 and anything still unverified live, then the PR attribution line.

Write the body to a temp file in the scratchpad and pass it with `--body-file` (avoids quoting trouble):

```bash
gh pr create --base main --head "$BRANCH" --title "<title>" --body-file <file>
```

Record the PR number and URL.

### 4. Checks (only if CI is ever added)

Today this always finds nothing — step 2 was the gate, so continue to step 5. Run it anyway so a future
CI workflow is honoured without anyone having to remember this skill:

```bash
PR=<number>
sleep 10
gh pr checks "$PR" 2>&1 | head -20
```

If it reports `no checks reported`, continue. If checks *are* listed, wait for them with the Bash tool's
`run_in_background: true` and a ~45-minute timeout, then wait for the completion notification — don't
poll:

```bash
gh pr checks "$PR" --watch --fail-fast --interval 30
echo "RESULT=EXIT_$?"
```

Any non-zero exit is a **`RELEASE FAILED — checks did not pass`**: report the PR URL, the failing check,
and `gh run view <id> --log-failed | tail -80`. Leave the PR open; don't merge.

### 5. Squash-merge

```bash
gh pr merge "$PR" --squash
```

Don't pass `--delete-branch`, `--admin` or `--auto`. If the merge is refused (required review, branch
protection, merge conflict), stop and report the message.

Then confirm the merge and capture the squash commit:

```bash
gh pr view "$PR" --json state,mergeCommit -q '.state + " " + .mergeCommit.oid'
git fetch origin --tags --quiet
git checkout main && git pull --ff-only origin main
echo "MAIN_HEAD=$(git rev-parse HEAD)"
```

`state` must be `MERGED`. Normally `MAIN_HEAD` equals the merge commit. If it doesn't, something else
landed on `main` in the meantime — stop and ask which commit to tag rather than guessing.

If `git pull --ff-only` fails (local `main` had diverged), stop and report — don't reset it.

### 6. Tag the new main head and push the tag

```bash
git tag "v$VERSION" "$MAIN_HEAD"
git push origin "v$VERSION"
```

(Lightweight tag, matching the existing ones.) Then find the Release run the tag triggered, filtering on
the tag so a stale or concurrent run can't be picked up. It can take a few seconds to register:

```bash
RUN=""
for i in $(seq 1 24); do
  RUN=$(gh run list --workflow release.yml --branch "v$VERSION" --event push --limit 1 --json databaseId -q '.[0].databaseId')
  [ -n "$RUN" ] && break
  sleep 5
done
echo "RUN=$RUN"
[ -n "$RUN" ] && gh run view "$RUN" --json url -q .url
```

If `RUN` is still empty after ~2 minutes, stop and report: the tag is pushed but no Release run started
(check the Actions tab — the workflow may be disabled). Don't re-push or re-tag.

### 7. Watch the Release workflow

Run the watch with the Bash tool's `run_in_background: true` and a timeout of 20 minutes (`1200000` ms),
then wait for the completion notification — don't poll. The watch output is redirected because
`gh run watch` redraws the job table every interval; the summary after it is what you read:

```bash
RUN=<id>
gh run watch "$RUN" --exit-status --interval 15 > /dev/null 2>&1
echo "RESULT=EXIT_$?"
gh run view "$RUN" --json status,conclusion,url,jobs \
  -q '"run: " + .status + " " + (.conclusion // "-") + "  " + .url, (.jobs[] | "  " + .name + ": " + .status + " " + (.conclusion // "-"))'
```

Then branch on the result:

- **`EXIT_0`** — the run succeeded. Confirm the Release really exists with the assets the install and
  update paths depend on:

  ```bash
  gh release view "v$VERSION" --json url,isDraft,assets \
    -q '.url, ("draft: " + (.isDraft|tostring)), (.assets[] | "  " + .name)'
  ```

  `Hypertree-win-Setup.exe` and `SHA256SUMS.txt` must both be listed (`install.ps1` resolves the first and
  verifies it against the second), as must `releases.win.json` and the `Hypertree-$VERSION-full.nupkg`
  (what installed copies' Velopack update check reads). If any is missing, or the Release is a draft,
  report it as a problem — the run went green but installs or updates can't use this release.
- **Any other exit** — the job failed or was cancelled. **Stop: this is a RELEASE BUILD FAILURE.** The tag
  is pushed but the job fails before its publish step, so **no GitHub Release was published**. Gather the
  evidence:

  ```bash
  gh run view "$RUN" --log-failed | tail -80
  ```

  Report back headed **`RELEASE BUILD FAILED — v$VERSION tagged, no Release published`**, with the run URL,
  which step failed, and the relevant tail of the failed log. Don't re-run, re-tag, delete the tag or push
  a fix on your own — say that the usual recovery once the cause is understood is
  `gh run rerun "$RUN" --failed` (a flaky runner) or a fix plus a new patch version (a real break), and
  let the user choose.
- **The background task timed out** (no `RESULT` line after 20 minutes) — the run is still going or
  stuck. Report the run URL and the current job status (the `gh run view` line above), and don't wait
  further unless asked.

### 8. Report back

Print, plainly:

- **`Released v$VERSION`**
- The local gate result (build clean, N tests passed).
- The PR URL (merged) and the squash commit SHA that was tagged.
- The Release workflow run URL and its outcome.
- The GitHub Release URL and confirmation that `Hypertree-win-Setup.exe`, `SHA256SUMS.txt`,
  `releases.win.json` and the full `.nupkg` are attached.
- That local `main` is checked out and up to date, and that the branch `$BRANCH` still exists both
  locally and on the remote (this repo doesn't auto-delete merged branches).

On any stop in steps 1–7, report exactly which step stopped, why, and what state things were left in
(e.g. "local tests failed — nothing pushed", "PR #8 open, not merged, no tag", or "PR #8 merged, v0.5.8
tagged, Release build failed at Publish htree CLI — no Release published").
