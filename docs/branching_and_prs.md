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

## Closing keywords

A commit message or PR body that says an issue is **not** fixed can still
close it: GitHub's parser matches `fixed: #876` inside "Not fixed: #876, out
of scope here" and ignores the leading "Not". Write "does not fix #876", or
leave the number out of that sentence.

## Agents and branches

During an agent wave the agents run **no git command at all** and leave their
work uncommitted in the shared checkout; the orchestrator commits with
explicit pathspecs so one lane's half-finished edit cannot ride along with
another's. The PR is therefore always the orchestrator's, and the branch is
created from the orchestrator's commit with
`git push origin <sha>:refs/heads/<branch>`, which needs no checkout switch
and so cannot disturb a working tree three agents are still editing.

The full model, including the worktree-per-agent alternative, is in the
`orchestrating-agents` skill.
