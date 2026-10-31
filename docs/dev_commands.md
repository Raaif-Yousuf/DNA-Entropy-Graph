# Dev commands

Every command a session actually runs, grouped by area. A command marked
**pending #N** does not work yet because the file or script it needs has not
landed — the issue number is where to check current status, not a promise
of when. Everything else on this page was run, or its output otherwise
checked, as part of writing it.

`rtk` (the token-optimizing CLI proxy documented at the user's global
`~/.claude/RTK.md`) transparently rewrites most shell commands via a Claude
Code hook; nothing below changes because of it, and it is not repeated per
command.

## Worker (Python)

```powershell
cd worker
uv venv --python 3.12                       # once, per fresh checkout
uv pip install -e ".[dev,genes]"             # core + dev + gene-boundary extras
cd ..
```

**Which command makes the venv, and is `worker/uv.lock` authoritative?** CI (`ci-worker.yml`, `ci-notices.yml`) builds
from the lock: `uv lock --check` (fails by name when `pyproject.toml` and the lock disagree) then
`uv sync --frozen --python 3.12 --extra dev --extra genes`, and runs pytest with `uv run --frozen` (#466; before that
the test job re-resolved with `uv pip install -e`, so CI tested whatever was newest that day). The lock is authoritative
for CI. The `uv pip install -e` line above is the quick, UNLOCKED dev install and can drift from CI; to match CI exactly
run `uv sync --frozen --python 3.12 --extra dev --extra genes` in `worker/` instead (MEASURED 2026-10-02 in a scratch
copy: it reproduced the laptop venv's versions). After editing a dependency bound, run `uv lock` and commit the lock.

```powershell
worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu" -q
worker\.venv\Scripts\python.exe -m pytest worker/tests/test_<module>.py -q -x
```

**Never a bare `pytest` or `python`** — always the venv's own interpreter,
explicitly (Hard Rule 20). See `msys2-python-on-path` in
`.claude/memory/`.

```powershell
worker\.venv\Scripts\python.exe -m dna_entropy --help
```

**ruff (lint/format): no longer pending, #285 landed.**
`worker/pyproject.toml` now has `[tool.ruff]`/`[tool.ruff.lint]`/
`[tool.ruff.lint.isort]` tables, so `ci-worker.yml`'s lint step actually
runs rather than printing a skip notice. Run via `uvx` (not installed into
the dev venv itself — nothing in `worker/pyproject.toml`'s own dependency
groups pulls `ruff` in, on purpose, so the venv stays exactly what the
package itself needs):

```powershell
uvx ruff check worker
uvx ruff format --check worker
uvx ruff check scripts                      # lint for scripts/ (#430): see the scripts/tests section
uvx ruff format --check scripts             # format gate for scripts/ (premerge ruff-scripts-format, ci-docs)
```

**`[evo]` extras (torch, evo2) — container only.** Do not
`uv pip install -e ".[evo]"` on the laptop; there is no CUDA GPU to use it
with (Hard Rule 15) and the install itself is large. `[evo]` is installed
inside `worker/Dockerfile.cuda` (pending #36/#45-area issues), not on a dev
machine.

```powershell
cd worker
uv build                                     # sdist + wheel, mirrors ci-worker.yml's build job
cd ..
```

## App (C#, WinUI 3) — pending #61

**`app/` exists as of issue #61** (thirteen projects, five test projects, a
DI resolution test). `scripts\dev_app.ps1` below still does not.

The build commands run from the repo root:

```powershell
dotnet restore app
dotnet format app --verify-no-changes --no-restore
dotnet build app/DnaEntropyGraph.sln -c Debug -p:Platform=x64
dotnet build app/DnaEntropyGraph.sln -c Release -p:Platform=x64 -warnaserror
```

**`dotnet test` does not.** MEASURED 2026-09-19: it must run with `app/`, or a
directory inside it, as the **working directory**:

```powershell
cd app
dotnet test tests/DnaEntropyGraph.Guards.Tests/DnaEntropyGraph.Guards.Tests.csproj   # fast, seconds
dotnet test DnaEntropyGraph.sln
```

Run from the repo root instead and it fails with *"Testing with VSTest target
is no longer supported"*, which reads like a broken test project and is not.
On the .NET 10 SDK, `dotnet test` on an xunit.v3 / Microsoft.Testing.Platform
project needs `global.json`'s `test.runner` setting, and that setting is
resolved from the **current working directory**, not from the project path.
`app/global.json` has it; the repo root has no `global.json` at all.

`ci-app.yml` already sets `working-directory: app` on every step, so CI was
never going to hit this. The trap is for a human or an agent running the
command by hand from the repo root, which is exactly where the error reads as
a broken test project rather than a wrong directory.

```powershell
scripts\dev_app.ps1                          # sets DEG_FAKE_CLOUD=1, launches against FakeGcp on a fresh profile
# the same by hand: DnaEntropyGraph.App.exe --fake-cloud --fresh-profile   (or DEG_FAKE_CLOUD=1; --profile <dir> overrides --fresh-profile)
```

A shipped build resolves the real Google gateways (#609); `--fake-cloud` is the only way back to the simulated cloud. In it, Sign in succeeds with no browser, the project is `fake-project`, the worker image is a fixed fake reference, and a run goes to Completed with the network off. The simulated cloud is refused on the real profile: it needs `--fresh-profile`, `--profile` or `DEG_DATA_DIR` pointing away from the real folder, so fake runs never write into the real history (a stray `DEG_FAKE_CLOUD=1` in a shipped build stops the launch with a message). While it runs, a permanent red "Simulated cloud" banner sits in the title bar. There is no Settings toggle yet (the issue asked for a hidden one; it is not built).

### Launching the app on a sandbox data folder (#638)

Every piece of app state (settings.json, installation_id, app.db, auth, logs, run inputs, the
WebView2 profile, the diagnostics source) hangs off one folder, `AppDataRoot`. By default that is
`%LOCALAPPDATA%\DNAEntropyGraph`. An agent or a dev that launches the app to look at it MUST point it
elsewhere, so the owner's real history, sign-in and settings are never touched:

```powershell
$exe = "app\.artifacts\<lane>\bin\DnaEntropyGraph.App\debug_win-x64\DnaEntropyGraph.App.exe"
& $exe --profile "$env:TEMP\deg-sandbox"          # command-line switch
$env:DEG_DATA_DIR = "$env:TEMP\deg-sandbox"; & $exe   # or the environment variable
```

- The command line wins when both are set. A relative path resolves against the current directory.
- A missing folder is created. A path that is a file, or a `--profile` with no folder after it,
  stops the launch with a message box naming one action; it never falls back to the real folder.
- A sandboxed window says so: a "Sandbox profile: <folder>" badge in the title bar and
  "DNA Entropy Graph - sandbox profile" as the OS window title. No badge means the real profile.
- `logs\app.log` appears only when something writes a warning; its absence after a quiet launch is normal.
- New code never calls `Environment.SpecialFolder.LocalApplicationData`: take the `AppDataRoot`
  (Guards.Tests/DataFolderOverrideTests fails the build of any other source file that does).

**A .NET SDK is required, not just a runtime.** MEASURED on the owner's dev
machine, 2026-09-19: `dotnet --list-runtimes` showed 8.0/9.0/10.0 present,
but `dotnet --list-sdks` was empty. See `onboarding.md` step 4.

## One command before a PR: `scripts/premerge.py`

Landing a change used to mean about thirteen commands across two languages and two
working directories (#431). One command now runs every gate and prints a single summary
naming each gate that failed:

```powershell
worker\.venv\Scripts\python.exe scripts\premerge.py --fast   # per lane: guards, lint, schema, fast C# guards
worker\.venv\Scripts\python.exe scripts\premerge.py          # full: also scripts tests, worker pytest, every C# suite
worker\.venv\Scripts\python.exe scripts\premerge.py --list   # every gate, its tier, and why
worker\.venv\Scripts\python.exe scripts\premerge.py --only check_user_home_paths --only ruff-worker-check
worker\.venv\Scripts\python.exe scripts\premerge.py --skip dotnet-format --log-dir <dir>   # keep each gate's full output
worker\.venv\Scripts\python.exe scripts\premerge.py --self-test   # proves it reports PASS, FAIL and ERROR (stub gates only)
```

- **It handles the traps for you.** Every `dotnet` gate runs with `app/` as the working
  directory (the repo root fails with the misleading VSTest error described below). Every
  Python gate runs on `worker/.venv`, never the PATH python; with no venv the gates ERROR and
  print the command that creates one. The scripts test suite runs through `uv run --with pytest
  --with pyyaml` because the venv has no pyyaml.
- **Statuses.** `FAIL` means the gate ran and said no. `ERROR` means it could not run or finish
  (tool missing, timeout). `SKIP` is only for an optional tool that is not installed (`lychee`),
  and is always printed. `FAIL` and `ERROR` both make the exit code 1; selecting zero gates, or
  naming an unknown gate, is exit 2, never a green run.
- **Timeouts (#925).** A gate's timeout is `DEFAULT_TIMEOUT` (900 s) unless set; the three big suites use `LONG_TIMEOUT`
  (1800 s). MEASURED 2026-10-29: `scripts/tests` took `1843 passed, 15 skipped in 1086.03s` under about 3-lane load, which the
  old 900 s default reported as ERROR on a green suite, so `scripts-tests` now has `LONG_TIMEOUT` (kept at 1800 s after #959: the parallel run measured 549 s, 3.3x headroom). A timed-out pytest gate is
  `ERROR` (never `PASS`, never `FAIL`) with its own text, `timed out after Ns with N passed and N failed so far (NOT a pass; ...)`,
  counted from the `-q` progress characters captured before the kill; when tests had already failed it says so. The timeout
  kills the gate's whole process tree (`taskkill /T /F` on Windows, the process group elsewhere), because `uv run ... pytest` is
  uv -> python and a surviving grandchild kept the output pipe open: MEASURED 2026-10-29, the old `subprocess.run` timeout
  returned only after the grandchild (a 120 s sleeper in the test) exited.
  Each gate also runs in a kill-on-close Windows Job Object (`wt_venv.create_kill_job` / `spawn_in_job`, #930), terminated before the
  `taskkill /T`: MEASURED 2026-10-30, a detached grandchild whose parent had already exited survived `taskkill /T` (nothing to walk
  from) and is now gone after the timeout (`test_a_timeout_kills_a_detached_grandchild_whose_parent_already_exited`). The job also
  ends anything a finished gate left behind. Limit: a process started with `CREATE_BREAKAWAY_FROM_JOB` escapes a job that allows
  breakaway, and the job cannot be created on a host whose parent job forbids nesting; then a WARNING line goes to stderr and the
  `taskkill /T` fallback is all there is. `land_wt.py`'s premerge run (`run_bounded(job=True)`) and `heavy.py`'s Ctrl-C use the same helper.
  `premerge.py` turns SIGTERM and SIGHUP into the same interrupt path (each gate runs in its own session on POSIX), so an
  aborted run kills every gate's tree. A gate's stdout and stderr stay separate pipes, joined stdout then stderr.
- **A gate cannot be silently left out.** Every `scripts/check_*.py` and every
  `app/tests/*/*.csproj` is discovered, not listed. An audit fails the run when a workflow runs a
  `scripts/*.py|ps1` that `premerge.py` does not (add a gate in `build_gates()` or an entry with a
  reason in `NOT_A_GATE`).
- **The fragment check needs a base.** `changelog-fragment-present` compares against `--base`
  (default `origin/main`; `git fetch` first). It fails when behaviour files changed and no
  `docs/changelog.d/` fragment came with them (rules in `docs/changelog.d/README.md`).
- **Not in it:** the CPU container smoke (needs Docker) and `shellcheck` (not on the laptop);
  both stay CI-only. `DnaEntropyGraph.App.UiTests` is exempt (WinUI-hosted).
- **Pytest gates get a private `--basetemp`** (a short `%TEMP%\pm-*` dir premerge removes when all gates passed, never under
  `--log-dir`, whose path is only recorded in `<log-dir>basetemp.txt`; #469: a deep basetemp made `git push` fail inside the
  temp bare repos of `test_land_pr`, MEASURED 2026-10-02: reproduced with a plain deep path, "remote unpack failed: unable to create temporary object directory"; `core.longpaths=true` on the bare repo also cures it, but a short basetemp fixes every test at once) and
  `-p no:cacheprovider`, and their verdict follows the TEST OUTCOME. MEASURED 2026-10-02: the first full run
  reported both pytest gates as FAIL although every test passed, because both crashed in pytest's own
  `cleanup_dead_symlinks` on the shared default basetemp. A run that prints only passes and then exits
  non-zero is now an `ERROR` saying it was not a test failure and not a pass.
- **`KNOWN` status and the real fix for #462.** MEASURED 2026-10-15: `.gitattributes` (`* text=auto eol=lf`) makes a
  fresh checkout or worktree LF even with `core.autocrlf=true`, and `dotnet format DnaEntropyGraph.sln
  --verify-no-changes` exits 0 there. ENDOFLINE on every line only appears in a STALE tree (checked out before
  `.gitattributes`, or a tool wrote CRLF). `python scripts/fix_eol.py` (`--check` lists without changing) rewrites
  exactly the files git holds as LF but the tree holds as CRLF/mixed, as bytes; run it once and the gate is real.
  The gate is `KNOWN` (exit 0, printed with the issue number) only when autocrlf is true, every parsed diagnostic is
  ENDOFLINE AND `git ls-files --eol` shows such a stale file; ENDOFLINE in an LF tree, any other diagnostic, an
  unparseable failure or autocrlf off is `FAIL`. THEORY (unverified, written without a dotnet run on a CRLF tree):
  the diagnostic line shape is `path(line,col): error ENDOFLINE: ...`; if it differs the parse finds nothing and the
  gate fails safe.
- **The stamp.** A green, unfiltered run writes `<git-dir>/premerge-stamp.json` (HEAD sha, mode, time);
  `scripts/hooks/require_premerge_before_pr.py` refuses `gh pr create` and `gh pr merge` without a fresh
  stamp for the current HEAD (see `scripts/hooks/README.md`). Committing changes HEAD, so: commit, then run
  premerge, then `gh pr create`. Escape hatch (logged): `DEG_SKIP_PREMERGE=<reason> gh pr create ...`.
- **Timing.** A full run exceeds one tool call's 600 s cap; run `--fast` per lane, and launch the
  full run detached with `--log-dir` and poll `summary.txt`.

## Pre-land check from an agent worktree: `scripts/premerge_wt.py` (#958)

An agent worktree has no `worker\.venv`. From the worktree root, with the PRIMARY checkout's python:

```powershell
<primary checkout>\worker\.venv\Scripts\python.exe scripts\premerge_wt.py [--junction] [--full] [-- <extra premerge args>]
```

It runs `premerge.py --fast` (through `heavy.py`) with `--python <shared venv python>` and `PYTHONPATH=<wt>\worker\src`, so the shared venv's editable install imports THIS worktree's worker code (#937), and forwards premerge's exit code. By default it links nothing (MEASURED 2026-10-30: no gate needs `<wt>\worker\.venv` to exist; premerge's `--python` overrides its venv lookup and `check_pyright.py` uses `sys.executable`). `--junction` instead links `<wt>\worker\.venv` to the shared venv for the run (the `land_wt.py` shape) and unlinks it in `finally`, also on Ctrl-C. Either way: a stale junction from a killed run is unlinked first; a real directory at `worker\.venv` is refused and untouched; afterwards the shared venv's python is asserted to still exist (exit 4 if not). Exit codes: premerge's own; 2 refused (a real `worker\.venv` directory, the primary checkout where you run `premerge.py` directly, a missing `scripts\premerge.py`, git unable to name the primary checkout so pass `--venv`, or `--junction` unable to link); 5 the link could not be removed after the run (the message carries premerge's real exit code; remove it by hand with `rmdir`); 124 timed out (`--timeout`, default 7200 s, heavy.py slot wait included; the gate tree it started is killed by PID first, also on Ctrl-C); 130 interrupted (2 and 130 can also be premerge's or heavy.py's own codes, so they overlap with the above; read the ERROR line); on Windows the gate tree runs in a kill-on-close Job Object owned by this script, so a timeout, Ctrl-C or a hard kill of this script ends heavy.py, premerge, pytest and dotnet together (taskkill /T by PID is the fallback, announced by a WARNING line); `--venv` inside the worktree's own `worker\.venv` path is refused; in default mode a real directory appearing at `worker\.venv` mid-run is left alone with a WARNING; 4 the shared venv is damaged, which overrides 5, 124, 130 and a red premerge. Link removal is `os.rmdir`/`os.unlink` of the reparse point only (`scripts/wt_venv.py`, shared with `land_wt.py`), never a recursive delete. `prune_worktrees.py` also unlinks any junction before `git worktree remove` (`test_venv_junction_does_not_make_a_worktree_dirty_and_is_unlinked_before_git_removes_it`). Tests: `scripts/tests/test_premerge_wt.py`.

## Landing a committed worktree branch: `scripts/land_wt.py`

For lanes that work in their own worktree (#510). Flow and refusals: [branching_and_prs.md](branching_and_prs.md).

```powershell
worker\.venv\Scripts\python.exe scripts\land_wt.py --branch feat/510-x --sha <tip-sha> --title "feat: ..." --body-file body.md [--full] [--merge-when-green [--checks-timeout 1800] [--checks-poll 20] [--no-rerun-failed-once]]
```

`--merge-when-green` waits for the PR checks and merges only if none failed (exit 5 failed, 6 pending at the timeout).

Tests: `scripts/tests/test_land_wt.py` (temp repo, bare fake origin, fake venv through a junction, stub gh), `scripts/tests/test_land_wt_resw.py` (the Resources.resw merge below).

### Pruning landed worktrees: `scripts/prune_worktrees.py`

Removes agent worktrees whose branch already landed (#794; MEASURED 2026-10-16: 151 registered worktrees). Dry run by default.

```powershell
worker\.venv\Scripts\python.exe scripts\prune_worktrees.py                       # dry run: removed / kept-and-why table
worker\.venv\Scripts\python.exe scripts\prune_worktrees.py --apply [--use-gh] [--delete-merged-branches] [--contained-in <refs...>] [--remote] [--min-age-hours 6]
```

Every ambiguity resolves toward KEEP. Removed only when: not the primary checkout or the one it runs from (realpath compare), not locked, not itself a link, does not contain another worktree (agents nest under `.claude/worktrees/`), landed (HEAD an ancestor of origin/main, so detached heads too; with `--use-gh`, a MERGED PR into main whose head contains the worktree HEAD and no OPEN one, from one batched `gh pr list`; a CLOSED or stacked PR does not count), it has a commit of its own or is no longer fresh (a HEAD on origin/main's first-parent history is a lane just started and is kept only while touched within `--fresh-lane-hours`, default 48; older, it is a stale checkout and the other rules decide), no rebase/merge/cherry-pick/revert/bisect in progress, nothing touched within `--min-age-hours` (default 6; two directory levels plus git's own state files; an unreadable timestamp keeps it), no uncommitted/untracked changes, no skip-worktree/assume-unchanged files, no ignored file beyond build output at its known roots (`app\{src,tests}\<project>\{bin,obj}`, `.artifacts`, `__pycache__` under worker/scripts, the pytest/ruff/mypy caches, `worker\*.egg-info`; `worker\.venv` only as a link), nothing under `app\secrets`, and every directory scannable (long-path aware) with no nested repository (a dry run scans too). Every junction is unlinked and verified gone BEFORE `git worktree remove`; if one will not unlink, or git then fails, the links are recreated and the worktree stays. Orphan `%TEMP%\landwt\<slug>-<14 digits>` dirs git no longer lists are deleted only if they hold a `.git` file, no live lock, are old enough, and either git can read them (HEAD in origin/main, clean) or, the real killed-run case where `git worktree prune` already deleted the admin dir, land_wt's lock record (`sha`, written since #794) names a commit that is pushed to origin; older leftovers with no recorded sha are kept for a human (never `locks` or `logs`; the root must be named `landwt` unless `--allow-any-tmp-root`). A dry run does not fetch (`--fetch` to; `--apply` fetches). It ends with `git worktree prune`, and `--delete-merged-branches` drops local branches merged into origin/main whose tip is NOT on its first-parent history (a pre-created lane is; a real merged feature branch is not), or PR-merged into main with `--use-gh`, that no surviving worktree has checked out, never main, master, develop or release/*. Content test (#812): ancestry misses a superseded attempt (`-r1`, `-rs`, `-ea`) whose content landed under another name, so a branch (or a worktree HEAD) also counts as landed when `git merge-tree --write-tree K <tip>` yields K's own tree, for K = origin/main or any `--contained-in` ref (queued or held tips); a tip that IS a `--contained-in` ref is the thing kept and never counts, a tip on main's first-parent line keeps its freshness rule, and a conflict or git error means not contained. A branch checked out in a kept (dirty, recent, locked) worktree is still never deleted. `--remote` also deletes origin branches that pass the same test, with these extra rules: one `gh pr list --state open` (if gh cannot answer, or the list reaches its limit, nothing remote is touched); a branch that is the head OR the base of an open PR is kept (deleting a base auto-closes the stacked PR on top); a branch checked out in a kept worktree is kept; a `--contained-in` tip justifies a remote delete only when `origin/<tip>` is the same commit AND `git ls-remote` confirms origin still has it there (a local-only or stale-tracking tip warns and is skipped, though a local one still justifies local deletes); `gh` is pinned to origin's `--repo owner/name` when origin is a GitHub URL; the delete is `git push origin --force-with-lease=refs/heads/<name>:<sha> --delete refs/heads/<name>` on the sha it judged, so a branch pushed to meanwhile survives (a local delete is the same lease: `git update-ref -d refs/heads/<name> <sha>`, and a branch that moved is reported and kept); `--apply` fetches with `--prune`; never main, master, develop or release/*. Without `--apply` it only lists them, and without `--remote` origin is never touched. Every removed worktree and deleted branch (local or remote) is printed as `name@sha` (undo: `git branch <name> <sha>`, or `git push origin <sha>:refs/heads/<name>`; a detached worktree prints its own `git worktree add --detach <path> <sha>` line). The containment merge runs with `--attr-source=<empty tree>`, no attributes file `merge.renormalize` and `core.autocrlf` off and every configured `merge.<driver>.driver` replaced by a failing command (git older than 2.40 rejects `--attr-source`: one warning, and only ancestry counts), so a `merge=ours`-style rule cannot make an edit look contained; the one hole is a builtin `merge=union` in `.git/info/attributes`. A dry run prints a note that origin was not fetched (`--apply` fetches, so a real run can judge against a newer origin/main; pass `--fetch` to match). Limitation: a live process whose cwd is in a worktree is not detected (Windows has no cheap way); `--min-age-hours` is the protection. Run the dry run at the start of each wave.

Tests: `scripts/tests/test_prune_worktrees.py` (temp repo, bare fake origin, real junction on Windows with a sentinel dir that must survive, stub gh).

### Resources.resw merge driver: `scripts/resw_merge.py`

Nearly every branch appends keys to the end of `Resources.resw`, so a plain text merge conflicts there every time (and `git merge-file --union` is banned for it: overlapping hunks break the XML). `scripts/resw_merge.py %O %A %B` is a git merge driver that merges per `<data name=...>` block: a key added on one side (or on both with identical content) is kept, theirs' new keys go after ours' last block, a key deleted on one side and untouched on the other is deleted, and the same key changed differently on both sides (or deleted vs changed) is a real conflict with markers around only that block. The result must parse as XML with no duplicate names. Every failure exit (real conflict, invalid XML, duplicate name, unterminated block, undecodable bytes) leaves conflict markers in the file (whole-file when it is not one block), so a later `git add` cannot silently drop theirs. Line endings follow ours' first line ending. Reordering of existing keys on theirs is ignored (key set and content only).

`.gitattributes` routes the file to the driver (`merge=resw`), but git only runs a driver that is defined in config. One-time setup per clone, from the primary checkout (every worktree shares that `.git/config`, so once is enough). The script passes the value to git as an argument list, so it behaves the same in Windows PowerShell 5.1, PowerShell 7 and bash (a hand-typed `git config` with nested quotes loses its inner quotes in 5.1):

```powershell
worker\.venv\Scripts\python.exe scripts\setup_git_drivers.py
```

Check with `git config merge.resw.driver` and `git check-attr merge -- app/src/DnaEntropyGraph.App/Strings/en-US/Resources.resw` (must print `merge: resw`). Without the config git silently falls back to the text merge. MEASURED 2026-10-16 (#800): git runs the driver with the MERGED branch's tree checked out, and a branch older than the driver has neither `scripts/resw_merge.py` nor the `merge=resw` line in its `.gitattributes`, so it used to conflict on `Resources.resw` for lack of both. The setup therefore (1) installs origin/main's `scripts/resw_merge.py` (never the running checkout's, which may be a stale branch; run `git fetch` first) as `<git-common-dir>/deg-drivers/resw_merge.py` and points the config at it, (2) runs it with the primary checkout's `worker\.venv` python (falling back to the interpreter running setup when that venv is absent), so removing a lane worktree breaks nothing, and (3) appends the `merge=resw` line to `<git-common-dir>/info/attributes`, keeping every other line byte for byte and warning about a different `merge=` value for the same path. It then checks `git check-attr` and fails if the file is still not routed. `scripts/check_resw_driver.py` (a premerge gate) fails when the configured script is missing and only WARNS (exit 0) when the installed copy differs from origin/main's blob (landing runs premerge in throwaway worktrees sharing the config, so a drift failure would block every landing); it prints the one command that fixes it: re-run the setup after `resw_merge.py` changes on main.

`land_wt.py` does not depend on that setup: for its merge of origin/main it passes the driver (this checkout's script, the running venv python) as `-c merge.resw.driver=...` and the attribute through a unique temp `core.attributesFile` (the user's own global attributes text plus the resw line, deleted after the merge), so a lane branch that predates the `.gitattributes` line still merges and nothing leaks into the shared repo config. A real conflict still refuses and names the file.

## Landing a queue of worktree branches: `scripts/land_queue.py`

```powershell
worker\.venv\Scripts\python.exe scripts\land_queue.py add feat/1-x <sha> 1 "feat: x (#1)" [--full] [--red "<check>"]
worker\.venv\Scripts\python.exe scripts\land_queue.py run [--once] [--replace]     # land_wt.py --merge-when-green, one item at a time; run it detached; --replace takes over a live but stuck runner
worker\.venv\Scripts\python.exe scripts\land_queue.py status [--check] | stop | retry feat/1-x@<sha>   # status: runner alive / alive, NO PROGRESS / DEAD
```

A runner whose pid is alive is never replaced automatically: `run` refuses (a second runner would double-land). `status` shows `alive, NO PROGRESS for N min - inspect; replace with run --replace` when its progress clock is older than the checks timeout plus 30 min; a human inspects it, then `run --replace` kills its process tree (only when the pid AND the creation time recorded in `runner.json` still match; it refuses otherwise or if the process survives), logs `REPLACED pid N (killed)` to `done.log` (THEORY, unverified: `taskkill /T` follows the parent link, so a grandchild whose parent already exited is orphaned and not killed), and the in-flight item is recorded `FAILED interrupted` (then `retry` resumes its open PR). A recorded pid whose process creation time differs from the one in `runner.json` (Windows reuses pids) counts as dead. A runner that finds `runner.json` no longer holds its pid logs `DISPLACED` and returns before taking another item. Every gh/git call in both scripts has a timeout (gh 300 s, git 600 s in `land_wt.py`; 600 s in `land_queue.py`) and a timeout kills the whole process tree (`taskkill /T /F /PID <that pid>` on Windows, the process group elsewhere); a hung `gh pr checks` read counts as a failed read, so the 30 min deadline is enforced. `land_wt.py` also bounds premerge (`--premerge-timeout`, default 7200 s, heavy.py slot wait included; a timeout is a red result, exit 3, nothing pushed) and `land_queue.py` bounds `land_wt.py` at premerge + 2 x checks wait + 20 git calls x 600 s + 11 gh calls x 300 s + 30 min (the formula is next to `LAND_WT_TIMEOUT_SECONDS`). The PR head read before the merge is retried 3 times with a 2 s, 4 s backoff before giving up (exit 6). After a `git push` timeout land_wt asks `git ls-remote` whether the branch arrived and carries on if it did, else says `not on origin` or `branch may exist on origin: <b>`. Its own `gh pr merge` is pinned with `--match-head-commit` to the sha it pushed and refuses (exit 5) if the PR head moved. A lock a killed git leaves in the throwaway worktree goes with `git worktree remove --force` (tested); no lock file is ever deleted by hand.

Tests: `scripts/tests/test_land_queue.py` (fake gh/git runner, no network). Details: `branching_and_prs.md`.

## Landing a PR from the shared checkout: `scripts/land_pr.py`

One command for the seven git/gh steps, safe while other lanes have uncommitted edits in the same tree (#445):

```powershell
worker\.venv\Scripts\python.exe scripts\land_pr.py --list-hunks worker/src/dna_entropy/pipeline.py     # hunk indexes of a file's working-tree diff
worker\.venv\Scripts\python.exe scripts\land_pr.py --branch fix/445-x --paths scripts/a.py docs/b.md --title "fix: ..." --body-file body.md --dry-run
worker\.venv\Scripts\python.exe scripts\land_pr.py --branch fix/412-x --hunks worker/src/dna_entropy/pipeline.py=0 --paths docs/changelog.d/fix-412-x.md --title "fix: ..." --body-file body.md
```

- It refuses (changing nothing) on a directory path, a path with no change, an index that already holds staged
  content it did not stage, an existing branch name, a detached HEAD, or a hunk index that does not exist.
- It waits on `gh pr checks` and merges only when every check passed; a red check stops ON the branch and names the
  check. **Zero checks is refused** unless `--allow-no-checks` (a PR can touch no workflow's area, and "no checks" is not green),
  and only after a grace period (`--grace-seconds`, default 120) in which an empty list is polled again, because GitHub
  registers checks a few seconds after `gh pr create` (#472). `--allow-no-checks` is never the fix for that.
- It returns the tree to main with `git checkout -B main origin/main`, never `git switch main` (MEASURED 2026-10-02:
  `switch` refuses when a landed file is still locally modified for another issue). Other lanes' edits stay byte-identical.
- Hunk staging builds the patch from `git diff` as BYTES and pipes it to `git apply --cached --recount -` (Windows text
  mode corrupts non-ASCII context lines and rewrites `\n`). It is Python, not PowerShell, to avoid the
  `pwsh -Array @(...)` flattening pitfall; list several files after `--paths` separated by spaces.
- Its tests build a bare remote and a clone under a temp dir with a stub `gh`; nothing touches GitHub.

## Heavy commands: `scripts/heavy.py` (#487)

Route every `dotnet build|test`, every full or multi-file `pytest`, and anything else that takes
gigabytes or minutes through it. It takes one of N machine-wide slots before running the command,
prints one `heavy: waiting` line to **stderr** while all are busy (naming each holder's lane, pid and how long it has held the slot, and how
to free a stuck one: stop that PID) and a `heavy: still queued after <t>` line every 60 s, flushed, so a caller with piped stdout can tell
queued from hung (#996). It passes the child's exit code through, and on Ctrl-C kills the command's whole tree
(its own job object, then `taskkill /T`; #930) and exits 130 (tested in-process with the wait stubbed to raise `KeyboardInterrupt`).
`heavy.py --status` lists every slot and exits 0: for a held slot its lane, pid, start, age, whether the pid is alive and the cumulative
CPU of its process tree (ctypes Toolhelp32 on Windows, `/proc` elsewhere, no psutil; exited descendants are not counted, so CPU is a
lower bound); a free slot prints `slot N: free`. All `heavy:` diagnostics (waiting, still queued, note, warning) go to stderr; stdout is the
command's own. `--status` and the waiting line probe a slot by briefly taking its lock, so a launch racing a probe can print one spurious
waiting line, and a slot's `.info` can be stale between a holder taking the lock and writing it: the output is a snapshot, not a
transaction (no redesign planned). Slots are OS-held file locks under `%TEMP%\deg-heavy\`
(`--lock-dir` or `$HEAVY_LOCK_DIR` to move them), so a killed agent frees its slot; each holder also writes
`slot-N.info` (pid, lane, start, first 80 characters of the command), and a leftover info file next to a free
slot is ignored. The lock is **per user**: it lives in that user's `%TEMP%`, so two Windows accounts on one
machine do not share slots.

The machine limit lives in one place, `<lock dir>\slots.txt`, set with `heavy.py --set-slots N`. Precedence:
`--slots` flag (for tests and one-offs only) > `slots.txt` > `$HEAVY_SLOTS` > 2. Callers that disagree on
`$HEAVY_SLOTS` therefore cannot exceed the file's limit.

The build folder follows the CWD, not heavy.py's own location (#874): the default `--artifacts-path` base is `<git toplevel of the CWD>\app\.artifacts\<lane>`, falling back to heavy.py's own repo outside any repo. A worktree lane can therefore run the primary's `heavy.py` (the venv lives there) from its own `app\` and build and test its own tree; no hand-passed `--artifacts-path` is needed. The test helpers that locate the repo (`RepoPaths`, `FixturePaths`, `TestPaths`, `ContractFixtures`) also walk up from their own source file (`CallerFilePath`) before the binary's directory, so Guards.Tests scan the tree that was compiled. Source-first means a test run scans the source tree its binary was compiled from; if that path no longer exists (the binary was moved to another machine or the checkout was deleted) the helpers fall back to walking up from the binary's directory. When the CWD is not inside a repo with `app\DnaEntropyGraph.sln`, heavy.py falls back to its own repo and prints a `heavy: warning:` line to stderr naming both. Until the fixed heavy.py is on the primary, run the worktree's copy: `python <worktree>\scripts\heavy.py ...`.

For `dotnet build|test|run|pack|publish` it adds `--artifacts-path <repo>\app\.artifacts\<lane>` unless one is
given (not for `dotnet format`), so concurrent lanes never share `obj/`. `--lane <name>` is **required** for
those commands (exit 2 without it; a shared default lane would put two agents back in one `obj/`).
The folder is inside the repo as a convenience, no longer a requirement (#495): every test project's repo-root helper
falls back to its own source file's path, so `--artifacts-path "$env:TEMP\deg-art\<lane>"` also works (see tests.md). `bin/`, `obj/` and `publish/`
under it are gitignored; `dotnet pack` would also create `package/`, which is not. `dotnet test` still needs
`app\` as the working directory (see above).

```powershell
# from the repo root
worker\.venv\Scripts\python.exe scripts\heavy.py -- worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu"
worker\.venv\Scripts\python.exe scripts\heavy.py --set-slots 3   # the orchestrator raises the machine limit, once
# dotnet: from app\ as the working directory
cd app
..\worker\.venv\Scripts\python.exe ..\scripts\heavy.py --lane mylane -- dotnet test tests\DnaEntropyGraph.Core.Tests\DnaEntropyGraph.Core.Tests.csproj
```

On Windows the wrapper joins a kill-on-close job object first, so a hard-killed wrapper (`taskkill /F`, a
tool timeout) takes its child with it; if the job cannot be set up it prints one `heavy: note:` line and
carries on. On other platforms a SIGKILLed wrapper frees the slot while its child may keep running
uncounted. The job also ends processes the command left behind when the wrapper exits, so for any `dotnet`
command the child env defaults `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0` and
`UseSharedCompilation=false` (a value you set yourself wins). No MSBuild node or compiler server then outlives
a build for another lane to connect to and lose mid-build. THEORY (unverified): that cross-lane failure was
never reproduced; the defaults make it impossible. Cost: no node reuse, so each build starts its own nodes.

## Repo-wide scripts (exist today)

```powershell
python scripts\issue_precheck.py 267 268 269           # batched, one call for every lane
python scripts\issue_precheck.py --all-open --suspect-only
```

```powershell
scripts\sync_labels.ps1                                 # report only: CREATE/UPDATE/PRUNE vs .github/labels.yml
scripts\sync_labels.ps1 -Apply                          # actually make the GitHub calls
scripts\sync_labels.ps1 -Apply -Prune                   # also delete a label labels.yml no longer names
```

```powershell
scripts\new_issue.ps1 -Title "area: imperative title" -Why "..." -DoneWhen @("...") `
    -Observable "..." -DocsTouched @("docs/x.md") -Tests @("worker/tests/test_x.py") `
    -OutOfScope "..." -CloudMoney "none"                # renders the full Appendix C section 6 shape; add -Apply to file it
```

```powershell
scripts\agent_wave.ps1 -Start -SpecFile wave.json       # plan/brief a wave of concurrent agents on disjoint paths
scripts\agent_wave.ps1 -Status
scripts\agent_wave.ps1 -Down
scripts\agent_wave.ps1 -SelfTest
```

```powershell
python scripts\compile_sprint_log.py                   # fold docs/changelog.d/ into sprint_log.md
python scripts\compile_sprint_log.py --dry-run
python scripts\compile_sprint_log.py --check            # fragment-shape only, what ci-docs.yml runs
python scripts\compile_sprint_log.py --since-tag v0.2.0  # render notes without deleting
```

```powershell
python scripts\sync_memory.py --pull      # SessionStart hook: mirror -> live
python scripts\sync_memory.py --push      # Stop hook: live -> mirror, secret-scanned
python scripts\sync_memory.py --status    # report drift both ways, change nothing
python scripts\sync_memory.py --self-test
python scripts\sync_memory.py --list-patterns
```

```powershell
python scripts\triage_diagnostics.py <bundle.zip>
python scripts\triage_diagnostics.py --data-dir           # every local run under %LOCALAPPDATA%\DNAEntropyGraph\
python scripts\triage_diagnostics.py <bundle.zip> --full  # every progress line, no capping
python scripts\triage_diagnostics.py <bundle.zip> --tail 200
python scripts\triage_diagnostics.py --check-schema       # verify SCHEMA_FIELDS against the real generated schema; a gate, not a triage mode
```

```powershell
python scripts\gen_manifest_schema.py                     # regenerate docs/contract/{manifest,status,result,error-codes}.json from the worker's own dataclasses
python scripts\gen_manifest_schema.py --check              # exit 1 if the checked-in files would differ (what ci-worker.yml's `contract` job runs)
```

## `scripts/check_*.py` (repo/docs guards) — exist today

**Updated 2026-09-19: no longer pending.** `ci-docs.yml`'s `checks` job runs
every `scripts/check_*.py` that exists via a glob loop (`for script in
scripts/check_*.py`) — it tightens automatically as each one lands, with no
workflow edit per issue, which is why grepping the workflow file for a
script's literal name finds nothing even once that script is fully wired in;
read the loop, not the filename, when checking whether a guard is enforced.
Thirteen exist as of this revision (`scripts/premerge.py --list` always has the current set), each with a
`--self-test` flag that runs against synthetic fixtures:

```powershell
python scripts\check_docs_index.py            # every docs/*.md is reachable from docs/README.md's index
python scripts\check_totest_format.py         # docs/ToTest.md row shape, Needs values, real commit shas, no duplicate issue + Do-this rows, and no stray line or row without a closing pipe inside the queue table (--max-age-days 45 default)
python scripts\check_user_home_paths.py       # no literal absolute user-home path in a tracked file (use %USERPROFILE%/$HOME instead)
python scripts\check_third_party_notices.py   # THIRD-PARTY-NOTICES.md freshness (needs dotnet AND the worker venv: runs in premerge full mode and in .github/workflows/ci-notices.yml, #416)
# (vendored web assets, e.g. igv.js, are declared in `scripts/vendored_assets.json`; the generator verifies each sha256 + licence and emits the notices section from it)
python scripts\check_version_lockstep.py      # every version-bearing file agrees
python scripts\check_version_lockstep.py --tag v1.2.3   # release mode (#33): the tag must also equal both versions; a missing app/Directory.Build.props fails. release.yml (#178, not yet written) must call it with "${GITHUB_REF_NAME}" BEFORE building
python scripts\check_changelog_fragments.py   # docs/changelog.d/ fragment shape (what ci-docs.yml runs on every PR; --self-test)
python scripts\check_changelog_fragments.py --base origin/main   # ALSO: behaviour files changed => a fragment exists (#429); rules in docs/changelog.d/README.md
python scripts\check_repo_hygiene.py        # legacy/clair is not tracked; AGENTS.md exists and stays under 20 lines (#449)
python scripts\check_actions_pinned.py       # every workflow `uses:` is a 40-char commit SHA with a `# vX.Y.Z` comment; local and docker:// are exempt (#485). Dependabot's github-actions entry bumps SHA and comment together
python scripts\check_em_dash.py              # no em dash in user-facing copy: docs/user_guide, *.resw, ISSUE_TEMPLATE, README.md (#425)
python scripts\check_write_newline.py        # Hard Rule 5: every text writer in scripts/ and worker/src passes newline="\n" (#444)
python scripts\check_manifest_spec_reads.py  # every manifest spec dataclass field is READ, resolved by class not name (#432)
python scripts\check_guard_drift.py           # the guard scripts themselves haven't silently started passing by checking nothing
python scripts\check_format_arity.py          # string.Format(..., GetString("Key"), args) arity vs the Resources.resw template (#761), a conditional key `GetString(c ? A : B)` checks every branch; unreferenced placeholder templates
python scripts\check_unused_fields.py         # a worker dataclass field that is parsed, stored and never read (the #304/#306 shape)
python scripts\check_pyright.py               # static types: pinned uvx pyright==1.1.407 over worker/src, worker/tests and scripts; a per-file RATCHET (see below). Run with the worker venv's python
python scripts\check_pyright.py --self-test    # plant each violation, no pyright needed
python scripts\check_pyright.py --lower       # bank progress: lower scripts\pyright_baseline.json to the current counts (refuses if anything grew)
python scripts\check_app_wiring.py            # the C# half of that same bug class: an unbound command, an unbound [ObservableProperty], a dangling {Binding}, an x:Uid with no .resw entry, a service registered and never resolved
python scripts\<any check_*.py> --self-test   # every one of them supports this
```

`check_pyright.py` is the static-type gate for the Python side (pyright, MIT, dev-only: nothing ships it, so no
THIRD-PARTY-NOTICES change). It is a RATCHET, not a zero gate. Measured 2026-10-28 (pyright 1.1.407, `typeCheckingMode`
"basic", config in `pyrightconfig.json`): 345 errors in 238 files at the start; `worker/src` was taken to 0 and the rest
is debt in the test suites and `scripts/tests`, recorded per file in `scripts/pyright_baseline.json`:

- `NEW`: a file has more errors than its baseline (an unlisted file is held to 0). Fix the error. Never raise the baseline.
- `STALE`: a file has fewer errors than its baseline, or its entry names a deleted file. Run `--lower` and commit the
  smaller baseline, otherwise the slack lets a new error in unseen (the same self-expiring shape as the unused-fields allowlist).
- `ZERO`: `worker/src` may not be listed in the baseline at all.

`--lower` rewrites the baseline to the per-file minimum and refuses when any count grew or a new file has errors, so it
cannot bank a new error. Pyright is `uvx pyright==<pin>` (pin in `PYRIGHT_VERSION`; bump it in its own commit), pointed at the
interpreter running the script (`--pythonpath`), so a git worktree with no `worker\.venv` of its own still resolves numpy as
long as you launch the gate with the worker venv's python (premerge does). `pythonPlatform` is pinned to "All" because
`heavy.py` uses both `fcntl` and `msvcrt`; with one platform the Windows laptop and the Linux runner would disagree by a few
errors. Runs in `premerge.py --fast` (about 12 s warm) and in `ci-worker.yml`'s `pyright ratchet` job; `ci-docs.yml`'s bare
glob loop skips it (no uv, no numpy there). To look at one file: `uvx pyright==1.1.407 --pythonpath worker\.venv\Scripts\python.exe <file>`.
In the editor, the Claude Code pyright LSP reads the same `pyrightconfig.json` (`venvPath`/`venv` point it at `worker\.venv`).

`check_manifest_spec_reads.py` is the class-resolving companion to `check_unused_fields.py`: that one matches
attribute reads by name (so `WindowPlan.stride` masked the unread `AnalysisSpec.stride`, #345), this one types the
receiver first, but only for the manifest spec dataclasses. Its allowlist is
`scripts/manifest_spec_reads_allowlist.json`, checked in both directions like the others. A receiver it cannot type
contributes no read, so the fix for a false UNREAD is an annotation, not an allowlist entry; its docstring lists
the exact resolution rules.

`check_app_wiring.py` takes `--verbose` (also print what it read and which
pages it skipped), `--json`, and `--root` (default `app`). A run over the real tree
takes about 5 s (MEASURED 2026-10-29; it was 50 to 64 s before #844 indexed the tokens
once per run). Its allowlist is
`scripts/app_wiring_allowlist.json`, keyed `"CODE:Symbol"` with a reason
string, and is checked in both directions: an entry whose finding stopped
firing fails the run, so the file cannot rot into a list of things that used
to be true. Read the script's docstring before adding an entry — it is
name-matched, not type-resolved, and it says so in detail.

**Running `scripts/tests/` itself** — the hooks, the label/memory sync, `compile_sprint_log.py`, `agent_wave.ps1`, `new_issue.ps1`, `sync_labels.ps1`, and every `check_*.py` guard above all have their own test file here, run as one suite (issue #311's own scripts-tests CI job runs exactly this, on Ubuntu, with full git history since some tests exercise git-aware code paths):

```powershell
uv run --with pytest --with pytest-xdist --with pytest-timeout --with pyyaml python -m pytest scripts\tests -q -n auto --timeout=300
```

A hung test now fails after 300 s with every thread's traceback (pytest-timeout, #840), instead of stalling the gate until its 1800 s bound.

**Lint for `scripts/` (#430):** `scripts/ruff.toml` is the config (line length 145, rules E F I UP B SIM, the
worker's family). Run `uvx ruff check scripts`; `ci-docs.yml`'s `scripts-tests` job and `premerge.py` run it.
**Parallel (#959):** `scripts/tests` runs under pytest-xdist (MIT, a dev-only `uv run --with`, like pyyaml, so not in `THIRD-PARTY-NOTICES.md`, which lists the `[dev]` extra only). `premerge.py` passes `-n scripts_test_workers()` (half the CPUs, 2 to 8, so 8 on a 16-core box; the log shows `created: N/N workers`), `ci-docs.yml` passes `-n auto`. MEASURED 2026-10-29: serial `1874 passed, 19 skipped in 1600.06s` vs `-n 8` `1879 passed, 19 skipped in 549.17s` under the same agent load. `--basetemp` becomes per-worker (`popen-gwN`). To debug one test, run it serially without `-n`. Running in parallel exposed a landed-PR lock race in the old O_EXCL `premerge_stamp` lock (PermissionError on a delete-pending lock); it is superseded by the OS byte-range lock of `fix/landed-prs-record-race`.
**Format for `scripts/`:** `uvx ruff format --check scripts` is a gate in `ci-docs.yml` (`scripts-tests`) and in `premerge.py`
(`ruff-scripts-format`). The tree was reformatted in one mechanical commit on 2026-10-15 (56 of 71 files, MEASURED: the full
`scripts/tests` run was 1172 passed, 5 skipped before and after). To fix a red gate run `uvx ruff format scripts`; the
preview is `uvx ruff format --diff scripts`.

## `scripts/hooks/` (Claude Code `PreToolUse`/`SessionStart`/`Stop`) — exist today

Not run by hand under normal use — wired into `.claude/settings.json` and
invoked automatically. Useful to run directly when debugging a hook itself:

```powershell
worker\.venv\Scripts\python.exe scripts\hooks\block_git_stash.py
worker\.venv\Scripts\python.exe scripts\hooks\block_recursive_delete.py
worker\.venv\Scripts\python.exe scripts\hooks\block_unlabelled_vm_create.py
worker\.venv\Scripts\python.exe scripts\hooks\block_agent_dispatch_in_worktree.py
worker\.venv\Scripts\python.exe scripts\hooks\block_primary_checkout_git.py
worker\.venv\Scripts\python.exe scripts\hooks\gcloud_read_only.py
```

Each reads the tool-call payload from stdin per `scripts/hooks/README.md`, so running one
with no input will simply wait.

All seven, including the `run_hook.py` dispatcher, take `--self-test`, which needs no stdin
and exercises both the allow and the deny arm:

```powershell
worker\.venv\Scripts\python.exe scripts\hooks\run_hook.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_git_stash.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_recursive_delete.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_unlabelled_vm_create.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_agent_dispatch_in_worktree.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_primary_checkout_git.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\gcloud_read_only.py --self-test
```

That is the fastest way to tell "the hook is broken" from "the hook is correctly refusing
what I asked for", which is the question you actually have when a command is denied.

## CI triggers, and the local gate that does not depend on them

**CI runs automatically on pull requests.** MEASURED 2026-10-16 from `.github/workflows/*.yml`: `ci-app`, `ci-docs`, `ci-worker` and `ci-notices` each have `pull_request` plus `workflow_dispatch` (`ci-app` and `ci-worker` skip the real work in a `changes` job when nothing in their area changed; `ci-notices` filters by `paths:`); `codeql` runs weekly, on pull requests and on pushes to main touching `app/**`, `worker/**`, `scripts/**` or `.github/workflows/**`; `release` runs on `v*` tags only. The owner's 2026-09-19 on-demand-CI decision was superseded by the sprint log entries that restored the PR triggers (#450 records the doc fix as an agent-made, reversible call). CI is still slow feedback, so run `scripts/premerge.py` locally first. A manual re-run:

```powershell
gh workflow run ci-worker.yml       # pytest, ruff, build, schema, shellcheck, CPU container
gh workflow run ci-docs.yml         # repo guards, check_*.py, scripts tests, link check
gh workflow run ci-app.yml          # skips until app/ exists (issue #61)
gh workflow run codeql.yml          # python, actions and csharp analysis (also runs weekly, on PRs and pushes to main touching app/, worker/, scripts/ or .github/workflows/; config in .github/codeql/codeql-config.yml, #484)
gh run watch                        # follow the run you just started
gh run list --limit 5               # what ran recently and how it went
```

Each workflow file's comment above its `on:` block is the authority for that workflow's triggers.

**What this means in practice:** the local gates are the same code CI runs, so run them before pushing rather than
after:

```powershell
worker\.venv\Scripts\python.exe scripts\premerge.py --fast
```

## GitHub (`gh`)

```powershell
gh issue view <n> --comments
gh issue list --search "<keywords>" --state all --limit 10
gh issue create --title "<area>: <imperative title>" --body-file <path> --label "<labels>"
gh issue close <n> --reason completed --comment "$(Get-Content body.md -Raw)"
gh pr create --title "..." --body-file <path>
gh pr checks
gh run list
gh run view <id>
```

**Wave findings to draft issues: `scripts/wave_findings.py` (#477).** Paste each agent's "Suspected bugs / improvements"
bullets (`- <text> (path:line)`) into a file, then:

```powershell
worker\.venv\Scripts\python.exe scripts\wave_findings.py findings.md      # or pipe the text in; --gh <path> picks another gh
```

Per finding it prints either `DUPLICATE of #N (open|closed): <title>` (an existing issue title shares at least 3 words and
60% of the finding's keywords, searched with `gh issue list --search ... --state all --limit 50`) or a `NEW` block: a
PowerShell `$body = @'...'@` in the house shape and one `gh issue create ... --label 'P3' --label 'area:<guess>'` line.
It only drafts: the single gh call is the read-only `issue list`; nothing is created, edited or closed. The area label
is guessed from the path and P3 is a placeholder, so read each draft before running it. A failed search prints
`WARNING ... unchecked` and still drafts. Tests: `scripts/tests/test_wave_findings.py` (stub gh).

## CloudCli: the console host for cloud resources (#60)

`app/tools/DnaEntropyGraph.CloudCli` is the app's own cloud layer from a console (gcloud is denied in this repo). It uses the real
gateways and **the account the app signed in** (DPAPI token files under `%LOCALAPPDATA%\DNAEntropyGraph\auth`, so run it as the same
Windows user); it never reads a key file and never signs in itself. Run from `app/`:

```powershell
dotnet run --project tools/DnaEntropyGraph.CloudCli -- resources list --project <id> [--install <id>] [--kind vm|bucket|all] [--json]
dotnet run --project tools/DnaEntropyGraph.CloudCli -- bucket describe --project <id> [--name <bucket>] [--json]
dotnet run --project tools/DnaEntropyGraph.CloudCli -- bucket ensure --project <id> [--json]
dotnet run --project tools/DnaEntropyGraph.CloudCli -- object get --project <id> --bucket <bucket> --key <object> [--json]
dotnet run --project tools/DnaEntropyGraph.CloudCli -- vm create --project <id> --zone <zone> --install <id> --service-account <email> [--job <id>] [--model <m>] [--machine-type <t>] [--lifecycle <l>] [--app-version <v>] [--max-run-duration 90|90s|30m|2h] [--worker-image <name@sha256:...> --bucket <bucket> [--startup-mode worker|gpu-test] [--expect-gpu true|false]] [--json]
dotnet run --project tools/DnaEntropyGraph.CloudCli -- vm delete --project <id> --zone <zone> --name deg-<job id> [--json]
```

- `resources list` prints every VM labelled `app=dna-entropy-graph` (or only `--install`'s) and every labelled `deg-` bucket as a plain
  table ending in `OK: <n> resource(s)`; `--json` prints an array of `{kind,name,zone,location,status,installation,job}` and nothing else.
  The results bucket is meant to outlive a run, so a leak scan uses `--kind vm` (that is what `scripts/cloud_gpu_test.ps1` runs).
- `vm create` only builds a `VmSpec`, so the six labels, `maxRunDuration` (default 30m) and termination action DELETE are always set;
  there is no flag to weaken them. A missing `--job` generates one. With `--worker-image` (a full reference pinned by digest, else exit 2)
  and `--bucket` the VM also gets `worker/vm/startup.sh` and its `deg-*` attributes through `StartupMetadata.Build` (lifecycle always
  delete); `--startup-mode gpu-test` adds `deg-mode=gpu-test`, which makes the script run `pytest -m gpu` instead of a job (#759).
  `bucket ensure` creates (idempotently) and names the project's bucket; `object get` prints one object (ASCII, capped at 4 MiB) and a
  missing one is exit 5 ("not there yet"), which is how `cloud_gpu_test.ps1` polls for the result. An object over the cap prints a leading
  `[truncated: only the last 4194304 bytes are shown]` line and then its LAST 4 MiB (a log's verdict, the pytest summary, is at its end),
  plus a `WARNING:` on stderr, and exits 0 so a caller that saves stdout keeps it.
  `--worker-image` is a developer path: CloudCli does NOT check the digest against the pinned worker image list (that list is embedded in the
  WinUI app, which a console host cannot reference), so `vm create` prints a `WARNING:` saying so. Use only a digest you released yourself.
- `vm delete` refuses a name that does not start with `deg-` (before any network call) and a VM that lacks the app label. A name not
  found in the given zone is exit 5 (never "already gone"): the error names the zone it was found in, or says to check
  `resources list --kind vm`. `resources list --install <id>` filters VMs only; the shared results bucket is always listed.
- Output is ASCII: `OK:` lines on stdout, one `ERROR:` line on stderr naming an action.
- Exit codes: `0` ok, `1` Google refused or could not be reached (or Ctrl+C), or any other unexpected failure (one `ERROR:` line naming
  the exception class and an action, never a stack trace), `2` bad command line or a value `VmSpec` rejects, `3` no account signed in
  ("Sign in with the app first") or the sign-in expired (only that: a network failure while refreshing the sign-in is `1`, and a missing
  or invalid OAuth client file is `1` with its own message), `4` a safety guard refused, `5` the named bucket, or the VM in that zone, is not there. A Google call that times out is `1` ("timed out"), not "Cancelled".
- `--json` takes no value, or `--json=true` / `--json=false`; any other value is exit 2.
- There is no GCP access from agent sessions: tests run the same code over a scripted HTTP handler
  (`app/tests/DnaEntropyGraph.Cloud.Tests/CloudCli/`); `docs/ToTest.md` carries the row that needs a real project.

## GPU test recipe

**Never on the laptop.** `pytest -m "not gpu"` is the standing local command;
anything marked `gpu` needs a real CUDA GPU the dev box does not have
(`dev-laptop-has-no-cuda` memory seed).

```powershell
scripts\cloud_gpu_test.ps1                   # dry-run (default): prints the plan, machine type, zone, estimated cost/minutes, creates nothing
scripts\cloud_gpu_test.ps1 -ProjectId <id> -ServiceAccount <worker sa email> -Apply                      # create, delete and leak-scan ONE labelled VM (UNVERIFIED, billed)
scripts\cloud_gpu_test.ps1 -ProjectId <id> -ServiceAccount <email> -Image <name@sha256:...> -Apply        # the same, and run pytest -m gpu on it, pull the log (UNVERIFIED, billed)
scripts\cloud_gpu_test.ps1 -PrintApplyArgs -ProjectId <id> -ServiceAccount <email> [-Image <digest>]       # print every CloudCli argument list as JSON, no cloud call
scripts\cloud_gpu_test.ps1 -SelfTest
```

**Updated 2026-10-16: `-Apply` builds its CloudCli arguments from CloudCli's own flag list (#619) and, with `-Image`, runs the GPU tests
on the VM (#83, #759); both are UNVERIFIED against a real project.** The argument lists are `New-VmCreateArgs` and friends: only flags
`CloudCliParser` accepts. CloudCli builds the six labels, `DELETE` and the duration through `VmSpec`, so the script cannot weaken them.
Consequences: the GPU comes from `-MachineType` (L4 with `g2-standard-8`), `-AcceleratorType` and the `purpose` label are plan-only,
and `-DiskSizeGb` other than 150 is refused. `-Apply` needs `-ProjectId` and `-ServiceAccount` (exit 2 without them) and a built CloudCli
(exit 3 without one; pass `-CloudCliPath` or build `app/tools/DnaEntropyGraph.CloudCli`). It never falls back to a raw cloud CLI call:
Appendix C denies one to agents on purpose. `-MaxRunDurationSeconds` must be 1 to 86400 (24 h), default 3600 (that limit is the only
backstop if the delete fails), or `-Apply` exits 2.

**Without `-Image`**: `vm create` (no startup script, so the VM idles), then `vm delete` in a `finally`, the leak scan, and last of all
CloudCli's resource table, whose final line is `OK: 0 resource(s)` when nothing is left. It prints `pytest -m gpu: NOT RUN`.

**With `-Image <name@sha256:...>`** (a tag instead of a digest is exit 2): `bucket ensure` (or `-Bucket <name>`), `vm create` in gpu-test mode
(`--startup-mode gpu-test --expect-gpu true`), then polling `jobs/<job>/logs/gpu-test-result.json` (`-PollSeconds`, up to
`-GpuTestTimeoutSeconds`, default maxRunDuration minus 60 s), pulling `gpu-test.log` to `-LogDir` (default `%TEMP%\deg-gpu-test\<job>`),
`vm delete` (exit 5 is expected: the VM already deleted itself), the leak scan and the table. The run passes only when pytest exited 0.
On the VM, `startup.sh`'s `gpu_test_run` runs `pytest -m gpu` inside the image on only the test files whose `-m gpu` collection is non-empty
(the image has pytest and `worker/tests` but not the `dev` extra: hypothesis is MPL-2.0, so a file that cannot be imported there and carries no gpu marker is named in
the log and left out; a gpu-marked file that cannot be collected FAILS the run with exit 6 and `error` `GPU_TEST_COLLECTION_ERROR`), under `docker run --init --name deg-gputest-<job>` with `timeout -k 30` counted from boot (maxRunDuration minus 10
minutes minus the uptime) and a `docker kill` by name on a timeout, uploads `logs/gpu-test.log` then `logs/gpu-test-result.json`
(`exitCode`, `timedOut`, `image`, `vm`; see job_contract.md) last, and applies the lifecycle. A failure before the tests can start (GPU not
visible, image pull failed) writes the same two objects with `exitCode` 7 and an `error` code, so the script stops waiting at once. The
script refuses (exit 2) a `-JobId` whose result object already exists, and does not trust a result whose `image` or `vm.name` is not this
run's (a stale PASS). A run whose GPU tests were all
skipped, or none ran, is exit code 6, never a pass (THEORY (unverified), from reading `test_evo_predictor.py`'s `importorskip` fixtures:
without torch/evo2 every gpu test skips and pytest still exits 0). The image must be built from `Dockerfile.cuda` after #759, or it has no tests to run. To build and pin a dev image from a branch (no release), see `release_runbook.md` section 6b (`image-dev.yml`).

**A failed create is never read as clean**: the delete is retried up to 3 times (`-SettleSeconds` apart) while it finds nothing, a delete
exit 5 is reported as "not proof it is gone", the scan and the table still run, and the run exits 1. A failing final table call also exits 1.

The contract is tested twice: `-SelfTest` checks every list against a recorded flag list and runs the whole flow against a stub CloudCli,
and `app/tests/DnaEntropyGraph.Cloud.Tests/CloudCli/CloudGpuTestScriptContractTests.cs` feeds the script's real `-PrintApplyArgs` output to the
real parser over `FakeGcp`. `docs/ToTest.md` carries the rows for a real run. Read `working-on-gcp` before running it for real: it creates
real, billed cloud resources. Launch detached and poll rather than blocking a single tool call on it (see the `a-tool-call-caps-at-600s` memory seed).

## PowerShell equivalents of common Unix commands

Hard Rule 19: PowerShell, not Unix, on the dev box (bash is for
`worker/vm/*.sh` only, which runs on the VM's Ubuntu image).

| Unix | PowerShell |
| --- | --- |
| `tail -n 50 file` | `Get-Content file -Tail 50` |
| `grep pattern file` | `Select-String pattern file` |
| `cat file` | `Get-Content file` |
| `ls` | `Get-ChildItem` |
| `rm -rf dir` | `Remove-Item -Recurse -Force dir` (refused inside the repo by `scripts/hooks/block_recursive_delete.py` when run through Claude Code) |
| `touch file` | `if (-not (Test-Path file)) { New-Item -ItemType File file }` |
| `which cmd` | `(Get-Command cmd).Source` or `where.exe cmd` |
| `wc -l file` | `(Get-Content file | Measure-Object -Line).Lines` |
| `mkdir -p dir` | `New-Item -ItemType Directory -Force dir` |
| `VAR=x cmd` | `$env:VAR = 'x'; cmd` |
