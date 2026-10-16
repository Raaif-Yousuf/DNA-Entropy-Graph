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
worker\.venv\Scripts\python.exe scripts\land_wt.py --branch feat/510-x --sha <sha> --title "feat: ..." --body-file body.md --merge-when-green   # wait for CI, merge only if green
```

**`--merge-when-green` (#666).** MEASURED 2026-10-15: `main` is unprotected and `scripts tests` was red on PRs #656, #657
and #660, which all merged, because a `gh pr checks --watch; gh pr merge` chain exits 0 even when a check failed. With the
flag, after the PR opens the script polls `gh pr checks <pr> --json name,bucket,state` every `--checks-poll` seconds (default
20) for up to `--checks-timeout` seconds (default 1800) and then:

| Outcome | Exit | Merges? |
| --- | --- | --- |
| every check `pass` or `skipping` | 0 | yes, `gh pr merge <pr> --merge` (a merge commit) |
| any check `fail` or `cancel` (reported at once, naming each) | 5 | no, the PR stays open |
| a check still pending, or no check reported at all, at the timeout | 6 | no, the PR stays open |
| every check green but `gh pr merge` itself fails | 4 | no |

**`--rerun-failed-once` (on by default with the flag; `--no-rerun-failed-once` disables).** MEASURED 2026-10-15: wall-clock
timed tests flaked on loaded CI runners and blocked merges (#664, #673). The first red result re-runs each failed run once
(`gh run rerun <run> --failed`; the run ids come from the check links, and it waits until no other check is still running,
because gh refuses to re-run an unfinished run), appends `<time> pr=<url> checks=<names> runs=<ids>` to
`<git-dir>/flaky-checks.log`, and waits again with a fresh timeout. Still red after the one re-run: exit 5. A red check
with no run link, or a rejected re-run, refuses at once. Read `flaky-checks.log` to see which checks flake and fix their
tests; the re-run is not a licence to leave them.

When gh itself fails on a poll (anything but its normal "no checks reported"), the first failure prints `WARNING: gh pr checks failed (rc=N: ...)` and a timeout says `gh could not read the checks (rc=N: ...)` instead of "no checks were reported" (MEASURED 2026-10-16: PR #811 was all green, but gh was failing on every poll with exit 3221225794). A pending check at the timeout never merges, and an empty check list is "not green yet", never green (gh reports no checks for
a moment after the PR opens). Console output is ASCII. Without the flag nothing changes: the script prints the
`gh pr merge` line and you decide.

In order: fetch; the sha must be the branch tip (a stale sha refuses); a control-byte scan of the changed text files
(MEASURED 2026-10-02: an agent's `scripts\v` became `\x0b`); a detached worktree under `%TEMP%\landwt` at the sha; origin/main
merged in if the branch is behind (`docs/ToTest.md` is union-resolved, since two lanes appending rows is routine; **any other
conflict aborts the merge and refuses with the file list**, so the lane resolves it); premerge inside the worktree with
`worker\.venv` reached through a junction and `PYTHONPATH=<wt>\worker\src`; on green, `HEAD` is pushed to
`refs/heads/<branch>` (`--land-branch` to rename) and `gh pr create` runs. Red premerge prints the failing gates and the
log path and pushes nothing.

**The junction is removed with `os.rmdir`, then checked, then `git worktree remove`, on success and on every failure**
(MEASURED: an earlier tool deleted the real venv through a junction). If the link cannot be removed the worktree is left
in place and named, not removed. A cleanup failure is a `WARNING:` that names the leftover directory; it never changes the
exit code (a green landing stays 0).

Run directories are `<slug>-<YYYYmmddHHMMSS>` under `%TEMP%\landwt`, each with a lock file (`locks\<name>.json`, pid and start
time) written before the worktree is created. The next run sweeps only directories that match that exact pattern for its own
branch and whose pid is dead (a sibling branch whose slug merely starts the same, or a live run, is never touched). A
directory git no longer knows is pruned and deleted by a walker that unlinks junctions instead of entering them; failing to
sweep one is a warning, not a refusal.

Refused before any work: a `--branch` or `--land-branch` of `main`, `master`, `HEAD`, anything starting `refs/` or `-`, or
empty. A merge that fails without conflicts reports git's own stderr (`merge failed: ...`); the merge commit gets an identity
from the repo config or a fallback, so an empty global git config cannot fail it. The control-byte scan reads names with
`-z` (spaces, quotes, tabs are fine) and allows ESC (0x1b, ANSI colour in logs) but refuses VT, FF and the other control
bytes. `--gh` is one executable path (spaces allowed). Exit codes: 0 PR opened, 1 refused (nothing pushed), 2 usage, 3
premerge red, 4 failed after work began; any failure after the push, including a missing `gh`, is 4 and names the pushed
branch.

`land_pr.py` stays for the shared-checkout case (uncommitted edits, exact paths or hunks); it is a separate script because its
input and its return-to-main step do not apply here. `--content-dir` is deferred to #576, only needed if a wave goes back to a
shared checkout.

## Landing many branches: `scripts/land_queue.py`

The orchestrator's queue runner, in the repo. It takes `branch|sha|issue|full|title` lines (optional sixth field
`red=<check>[,<check>]`, a KNOWN_RED override for that item only), runs `land_wt.py --merge-when-green` for each (land_wt waits for the checks, reruns failed runs once and merges; exit 5/6 leave the PR open and
are logged `FAILED rc=5|6 (PR #n stays open)`). An item with `red=` (or the KNOWN_RED env var) lands without the flag and the queue's own merge loop
ignores the named checks, reruns the failed runs once (logged to `flakes.log`) and merges with `gh pr merge --merge`.

```powershell
worker\.venv\Scripts\python.exe scripts\land_queue.py add feat/1-x <sha> 1 "feat: x (#1)" [--full] [--red "build and test"]
worker\.venv\Scripts\python.exe scripts\land_queue.py run          # the loop; `run --once` drains and returns; `run --replace` takes over a live but stuck runner
worker\.venv\Scripts\python.exe scripts\land_queue.py status [--check]  # runner alive/DEAD, pending, in flight, last results; --check exits 1 if not alive
worker\.venv\Scripts\python.exe scripts\land_queue.py retry feat/1-x@<sha>   # an item already taken, again
worker\.venv\Scripts\python.exe scripts\land_queue.py stop         # runner.stop: exits before the next item
```

State is under `--state` (default `%TEMP%\deg-land-queue`): `queue.txt`, `taken.log`, `done.log`, `flakes.log`,
`inflight.json`, `runner.json` (pid + heartbeat), `land-<slug>-<sha7>.log` (one per item), `body-<slug>-<sha7>.md`. `run` refuses to start while
the pid in `runner.json` is alive and exits clean after `stop` (the stop file is consumed). It is built to run detached and survive the session that
started it: a later session reads `status`. Every log is append-only: `retry` appends `RETRY <key>` to `taken.log` and `RETRY <branch> @ <sha>` to
`done.log` (a rewrite would make `tail -F` monitors replay the file). When an item fails, a later queued item whose sha has the failed sha as an
ancestor is logged `SKIPPED-STACK` and never attempted. With `run --ff-main` (default off) the main checkout is fast-forwarded after a merge, only when it is on `main`, clean and has no `index.lock`; the outcome is logged as `FF-MAIN ...` in `done.log`. A DIRTY re-land closes the old PR with `--delete-branch` only when the PR head is a branch this queue created, never the lane's own. A daemon thread beats 
unner.json every 60 s for the life of the runner, so heartbeat age is a real liveness signal (pid stays the primary check). On a retry an existing PR is resumed only when its head is the item's sha or that sha plus merges of main (never unverified commits), a MERGED one is logged MERGED-RECOVERED #n branch @ sha (verified against the PR head or origin/main), and the merge always passes --match-head-commit. Every done line ends with `branch @ sha`. `runner.json` holds `pid`, `started` (the process creation time: a live pid with a different one is a reused pid and counts as dead), `ts` (the main thread's last progress: a subprocess returned or a stage began) and `beat` (the beater's own clock). A runner whose pid is alive is NEVER replaced automatically (`run` refuses); `status` shows `alive, NO PROGRESS for N min - inspect; replace with run --replace` once `ts` is older than the checks timeout plus 30 min, and `run --replace` (a human's call) kills its process tree first (pid and creation time must still match the record, and the process must really exit, else it refuses), then takes over and logs `REPLACED pid N (killed)` (THEORY, unverified: grandchildren whose parent already exited are orphaned and not killed by `taskkill /T`); the killed runner's in-flight item becomes `FAILED interrupted` and `retry` resumes its open PR. The heartbeat only ever replaces a parsed `runner.json` holding its own pid; when the file is gone, empty, unparsable or another pid's, the runner logs `DISPLACED` and returns before taking another item. gh and git calls time out after 600 s (rc 124, logged `TIMEOUT` in `flakes.log`) and a timeout kills the whole process tree; `land_wt.py` bounds each of its own gh/git calls (gh 300 s, git 600 s; a hung `gh pr checks` is a failed read, so its 30 min deadline holds) and the premerge (`--premerge-timeout`, 7200 s); `land_queue.py` bounds `land_wt.py` itself at premerge + 2 x the checks wait + the worst-case git/gh call chain + 30 min (formula beside `LAND_WT_TIMEOUT_SECONDS`). `land_wt.py`'s own merge is pinned to the pushed sha with `--match-head-commit` and refuses if the PR head moved. `merge_green` never merges without `--match-head-commit`: an unreadable PR head is `PENDING` (`FAILED rc=6`). An open PR from an earlier attempt whose head is not this sha plus merges of main is `FAILED open PR #n has unverified commits`, never a second PR. A pending check at the timeout never merges (`FAILED rc=6`). An `inflight.json` with no verdict at start becomes `FAILED interrupted`, so `retry` applies. `add` rejects a bad sha, a `|` or whitespace in the branch, or `|red=` in the title; `stop` refuses when no runner is alive. MEASURED 2026-10-15 defects of the scratchpad scripts it replaces, each with a test:

- Items are keyed `branch@sha`, not by the whole line, so editing a queued title (or a trailing space) never re-takes a landed
  branch; a new sha of the same branch is a new item.
- If `origin` already holds the branch (a closed PR), land_wt gets `--land-branch <branch>-<sha7>` instead of a failing push.
- Before merging, `mergeStateStatus` is read (UNKNOWN is polled). A DIRTY PR (another PR was merged by hand while this one was
  in CI, PR #674) is closed with its branch deleted and the same sha is re-landed once on `<branch>-<sha7>-r2`; DIRTY twice is
  REFUSED.
- `land_wt.py --merge-when-green` (#666) is now on main, so the queue delegates the merge to it; only known-red items keep the loop here.

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
