# Branches, pull requests and how work lands

Hard Rule 16 already says a behaviour change writes its changelog fragment
on the branch. This file says what the branch itself is for, and why work on
this repository stopped going straight to `main` on 2026-09-19.

## The rule

**Nothing lands on `main` by a direct push.** Every unit of work becomes a
branch, a pull request, and a merge, including a one-line doc fix, including
work the owner did himself, and including the case where the author and the
merger are the same person and there is nobody to review it.

That last case is the one worth defending, because it is the common one here
and it looks like pure ceremony. Three things it buys, none of which a direct
commit gives you:

1. **A reviewable unit that outlives the commit message.** A PR has a body,
   a diff, a conversation and a link from the issue. `git log` has one
   message written before anyone looked at the result.
2. **A place to put the evidence.** This repo's recurring failure is a claim
   that nobody checked (`a-report-is-not-evidence`, and the `wired-to-nothing`
   skill's whole premise). The PR body is where the command output, the
   measured number and the one differing observable go, and it stays
   attached to the change forever.
3. **A rollback that is one click and one commit**, because a merge commit
   names exactly what came in.

The whole cost is two commands, and what you get back is a record that
someone six months from now can actually read.

## The shape of one PR

**One issue, one PR.** Not one lane, not one night, not one agent. Two issues
that genuinely cannot be separated (the fix for one is the fix for the other)
share a PR and the body says why.

**The branch name is `<type>/<slug>`**, with the type matching the commit
prefix this repo already uses: `feat/`, `fix/`, `docs/`, `ci/`, `refactor/`,
`test/`, `chore/`. The changelog fragment is
`docs/changelog.d/<branch-name-with-slashes-as-dashes>.md`, so
`fix/294-paste-encoding` writes `docs/changelog.d/fix-294-paste-encoding.md`.
Every line of that fragment starts with `- `; a heading makes
`compile_sprint_log.py` skip the whole file without saying so.

**The body carries the evidence, not the intent.** What was measured, the
exact commands, the counts, and for anything reported as working, the one
observable that would differ if it were wired to nothing. What is still
**unverified** gets its own section and says what would prove it. A PR body
that only restates the issue has wasted the one durable place this project
has to put a fact.

## Merging

```powershell
gh pr create --base main --head <branch> --title "..." --body "..."
gh pr merge <n> --merge --delete-branch
git fetch origin; git merge --ff-only origin/main
```

**`--merge`, not `--squash`.** Local `main` usually already holds the branch
commit, because the orchestrator commits in the shared checkout and then
pushes that commit to a branch. A squash rewrites it into a new sha, local
`main` diverges from `origin/main` by a commit that is textually identical to
one already there, and the next `git merge --ff-only` refuses with a conflict
nobody can read. A merge commit keeps the branch commit as a parent, so the
fast-forward afterwards is exact.

**Nothing reliably gates the merge except you.** The owner's 2026-09-19 decision
was on-demand CI; MEASURED 2026-10-02 the workflow files differ (see
`dev_commands.md`, "CI triggers"), and `ci-app.yml` has no automatic trigger at all, so a PR with a
green look has not been checked for `app/`. The step **before `gh pr create`** is one command:

```powershell
worker\.venv\Scripts\python.exe scripts\premerge.py --fast    # per lane, minutes
worker\.venv\Scripts\python.exe scripts\premerge.py           # full, before the merge that closes a wave
```

It runs every `scripts/check_*.py` guard (discovered, so a new one cannot be left out), the
changelog-fragment presence check against `origin/main` (`git fetch` first), the schema check, both
ruff gates and the C# format and guard tests with the right working directory and the venv
interpreter; full mode adds the scripts, worker and C# suites. It exits 1 and names every gate that
failed. Run it **after** the last merge of a batch too, not only before the first: MEASURED
2026-09-19, a literal user-home path sat on main for several PRs because the guards ran before one
lane merged and not after. `dev_commands.md` has the full command and gate list.

To have GitHub check a branch anyway: `gh workflow run ci-worker.yml --ref <branch>`,
then `gh run watch`.

## Landing a lane: `scripts/land_pr.py`

From a shared checkout, `scripts/land_pr.py` (see `dev_commands.md`) does branch, explicit-path commit, push, PR,
wait-for-green, merge and return-to-main in one call, and leaves every other lane's uncommitted edits untouched.
Run `scripts/premerge.py --fast` first, in the working tree before the tool commits: `land_pr.py` refuses without a green
stamp on the current HEAD (`--no-premerge-check` overrides). The `gh pr create` hook cannot see the `gh` call the tool makes
internally, so the tool does the same check itself.

GitHub registers no checks for a few seconds after `gh pr create` (MEASURED 2026-10-02, #472), so an empty check list
inside the grace period (`--grace-seconds`, default 120) is polled again, not reported. "No checks" is only declared
after that wait and the message says how long it waited. `--allow-no-checks` is for a PR that genuinely has no CI; it is
never the workaround for "the checks have not shown up yet" (it also waits the grace period first).

## Landing a worktree branch: `scripts/land_wt.py`

Every lane works in its own worktree and commits there, so the orchestrator lands a **commit**, not a working tree. Given
`<branch> @ <sha>`, `scripts/land_wt.py` (#510) verifies that exact commit in a throwaway worktree and opens the PR; it
never touches any checkout's working tree and never force-pushes.

```powershell
worker\.venv\Scripts\python.exe scripts\land_wt.py --branch feat/510-x --sha <sha> --title "feat: ..." --body-file body.md          # premerge --fast
worker\.venv\Scripts\python.exe scripts\land_wt.py --branch feat/510-x --sha <sha> --title "feat: ..." --body-file body.md --full   # full premerge
gh pr merge <n> --merge                                                                                                                # your call, after CI
```

In order: fetch; the sha must be the branch tip (a stale sha refuses); a control-byte scan of the changed text files
(MEASURED 2026-10-02: an agent's `scripts\v` became `\x0b`); a detached worktree under `%TEMP%\landwt` at the sha; origin/main
merged in if the branch is behind (`docs/ToTest.md` is union-resolved, since two lanes appending rows is routine; **any other
conflict aborts the merge and refuses with the file list**, so the lane resolves it); premerge inside the worktree with
`worker\.venv` reached through a junction and `PYTHONPATH=<wt>\worker\src`; on green, `HEAD` is pushed to
`refs/heads/<branch>` (`--land-branch` to rename) and `gh pr create` runs. Red premerge prints the failing gates and the
log path and pushes nothing.

**The junction is removed with `os.rmdir`, then checked, then `git worktree remove`, on success and on every failure**
(MEASURED: an earlier tool deleted the real venv through a junction). If the link cannot be removed the worktree is left
in place and named, not removed. A worktree left by a killed run is cleaned the same way at the next start. Exit codes: 0
PR opened, 1 refused (nothing pushed), 2 usage, 3 premerge red, 4 failed after work began.

`land_pr.py` stays for the shared-checkout case (uncommitted edits, exact paths or hunks); it is a separate script because its
input and its return-to-main step do not apply here. `--content-dir` is deferred to #576, only needed if a wave goes back to a
shared checkout.
## Closing keywords

A commit message or PR body that says an issue is **not** fixed can still
close it: GitHub's parser matches `fixed: #876` inside "Not fixed: #876, out
of scope here" and ignores the leading "Not". Write "does not fix #876", or
leave the number out of that sentence.

## Agents and branches

In a shared-checkout wave the agents run **no git command at all** and leave their
work uncommitted in the shared checkout; the orchestrator commits with
explicit pathspecs so one lane's half-finished edit cannot ride along with
another's. The PR is therefore always the orchestrator's, and the branch is
created from the orchestrator's commit with
`git push origin <sha>:refs/heads/<branch>`, which needs no checkout switch
and so cannot disturb a working tree three agents are still editing.

The full model, including the worktree-per-agent alternative, is in the
`orchestrating-agents` skill.
