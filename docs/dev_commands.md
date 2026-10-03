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
uvx ruff check scripts                      # lint only for scripts/ (#430): see the scripts/tests section
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
scripts\dev_app.ps1                          # sets DEG_FAKE_CLOUD=1, launches against FakeGcp
```

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
- **`KNOWN` status.** `dotnet format --verify-no-changes` reports ENDOFLINE on every line of a CRLF checkout
  (`core.autocrlf=true`, #462). Only when autocrlf is true AND every parsed diagnostic is ENDOFLINE is the
  gate `KNOWN` (exit 0, printed with the issue number); any other diagnostic, an unparseable failure or
  autocrlf off stays `FAIL`. THEORY (unverified, written without a dotnet run): the diagnostic line shape is
  `path(line,col): error ENDOFLINE: ...`; if it differs the parse finds nothing and the gate fails safe.
- **The stamp.** A green, unfiltered run writes `<git-dir>/premerge-stamp.json` (HEAD sha, mode, time);
  `scripts/hooks/require_premerge_before_pr.py` refuses `gh pr create` and `gh pr merge` without a fresh
  stamp for the current HEAD (see `scripts/hooks/README.md`). Committing changes HEAD, so: commit, then run
  premerge, then `gh pr create`. Escape hatch (logged): `DEG_SKIP_PREMERGE=<reason> gh pr create ...`.
- **Timing.** A full run exceeds one tool call's 600 s cap; run `--fast` per lane, and launch the
  full run detached with `--log-dir` and poll `summary.txt`.

## Landing a committed worktree branch: `scripts/land_wt.py`

For lanes that work in their own worktree (#510). Flow and refusals: [branching_and_prs.md](branching_and_prs.md).

```powershell
worker\.venv\Scripts\python.exe scripts\land_wt.py --branch feat/510-x --sha <tip-sha> --title "feat: ..." --body-file body.md [--full]
```

Tests: `scripts/tests/test_land_wt.py` (temp repo, bare fake origin, fake venv through a junction, stub gh).

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
  check. **Zero checks is refused** unless `--allow-no-checks` (CI here can be on demand, and "no checks" is not green),
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
prints one `heavy: waiting` line while all are busy (naming each holder's pid and lane, and how to free a
stuck one: stop that PID), passes the child's exit code through, and on Ctrl-C kills the child and exits
130 (the Ctrl-C path has no automated test). Slots are OS-held file locks under `%TEMP%\deg-heavy\`
(`--lock-dir` or `$HEAVY_LOCK_DIR` to move them), so a killed agent frees its slot; each holder also writes
`slot-N.info` (pid, lane, start, first 80 characters of the command), and a leftover info file next to a free
slot is ignored. The lock is **per user**: it lives in that user's `%TEMP%`, so two Windows accounts on one
machine do not share slots.

The machine limit lives in one place, `<lock dir>\slots.txt`, set with `heavy.py --set-slots N`. Precedence:
`--slots` flag (for tests and one-offs only) > `slots.txt` > `$HEAVY_SLOTS` > 2. Callers that disagree on
`$HEAVY_SLOTS` therefore cannot exceed the file's limit.

For `dotnet build|test|run|pack|publish` it adds `--artifacts-path <repo>\app\.artifacts\<lane>` unless one is
given (not for `dotnet format`), so concurrent lanes never share `obj/`. `--lane <name>` is **required** for
those commands (exit 2 without it; a shared default lane would put two agents back in one `obj/`).
The folder is inside the repo on purpose: Cloud.Tests `FixturePaths` and Core.Tests `StartupMetadataTests`
find the repo by walking up from the test binary, which fails under `%TEMP%`. `bin/`, `obj/` and `publish/`
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
python scripts\check_totest_format.py         # docs/ToTest.md row shape, Needs values, real commit shas, and no duplicate issue + Do-this rows (--max-age-days 45 default)
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
python scripts\check_unused_fields.py         # a worker dataclass field that is parsed, stored and never read (the #304/#306 shape)
python scripts\check_app_wiring.py            # the C# half of that same bug class: an unbound command, an unbound [ObservableProperty], a dangling {Binding}, an x:Uid with no .resw entry, a service registered and never resolved
python scripts\<any check_*.py> --self-test   # every one of them supports this
```

`check_manifest_spec_reads.py` is the class-resolving companion to `check_unused_fields.py`: that one matches
attribute reads by name (so `WindowPlan.stride` masked the unread `AnalysisSpec.stride`, #345), this one types the
receiver first, but only for the manifest spec dataclasses. Its allowlist is
`scripts/manifest_spec_reads_allowlist.json`, checked in both directions like the others. A receiver it cannot type
contributes no read, so the fix for a false UNREAD is an annotation, not an allowlist entry; its docstring lists
the exact resolution rules.

`check_app_wiring.py` takes `--verbose` (also print what it read and which
pages it skipped), `--json`, and `--root` (default `app`). Its allowlist is
`scripts/app_wiring_allowlist.json`, keyed `"CODE:Symbol"` with a reason
string, and is checked in both directions: an entry whose finding stopped
firing fails the run, so the file cannot rot into a list of things that used
to be true. Read the script's docstring before adding an entry — it is
name-matched, not type-resolved, and it says so in detail.

**Running `scripts/tests/` itself** — the hooks, the label/memory sync, `compile_sprint_log.py`, `agent_wave.ps1`, `new_issue.ps1`, `sync_labels.ps1`, and every `check_*.py` guard above all have their own test file here, run as one suite (issue #311's own scripts-tests CI job runs exactly this, on Ubuntu, with full git history since some tests exercise git-aware code paths):

```powershell
uv run --with pytest --with pyyaml python -m pytest scripts\tests -q
```

**Lint for `scripts/` (#430):** `scripts/ruff.toml` is the config (line length 145, rules E F I UP B SIM, the
worker's family). Run `uvx ruff check scripts`; `ci-docs.yml`'s `scripts-tests` job and `premerge.py` run it.
There is deliberately **no `ruff format --check scripts` gate yet**: MEASURED 2026-10-02, `uvx ruff format --check
scripts` would rewrite 42 of 44 files (about 1,700 changed lines at the 145 limit). A mass reformat is its own
mechanical commit and is not wired until someone lands it; the preview is `uvx ruff format --diff scripts`.

## `scripts/hooks/` (Claude Code `PreToolUse`/`SessionStart`/`Stop`) — exist today

Not run by hand under normal use — wired into `.claude/settings.json` and
invoked automatically. Useful to run directly when debugging a hook itself:

```powershell
worker\.venv\Scripts\python.exe scripts\hooks\block_git_stash.py
worker\.venv\Scripts\python.exe scripts\hooks\block_recursive_delete.py
worker\.venv\Scripts\python.exe scripts\hooks\block_unlabelled_vm_create.py
worker\.venv\Scripts\python.exe scripts\hooks\block_agent_dispatch_in_worktree.py
worker\.venv\Scripts\python.exe scripts\hooks\block_primary_checkout_git.py
```

Each reads the tool-call payload from stdin per `scripts/hooks/README.md`, so running one
with no input will simply wait.

All six, including the `run_hook.py` dispatcher, take `--self-test`, which needs no stdin
and exercises both the allow and the deny arm:

```powershell
worker\.venv\Scripts\python.exe scripts\hooks\run_hook.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_git_stash.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_recursive_delete.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_unlabelled_vm_create.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_agent_dispatch_in_worktree.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_primary_checkout_git.py --self-test
```

That is the fastest way to tell "the hook is broken" from "the hook is correctly refusing
what I asked for", which is the question you actually have when a command is denied.

## CI triggers, and the local gate that does not depend on them

**The owner's 2026-09-19 decision was on-demand CI** (no push, pull request or schedule trigger). MEASURED 2026-10-02: the workflow files no longer match it: `ci-docs.yml` has a `pull_request` trigger and `ci-worker.yml` has one for `worker/**`; `ci-app.yml` is `workflow_dispatch` only; `codeql.yml` (#484) also runs weekly, on pull requests and on pushes to main touching `app/**`, `worker/**`, `scripts/**` or `.github/workflows/**`. Whichever is intended, a green-looking PR is not proof for `app/` (no automatic trigger), so run `scripts/premerge.py` locally. A run on demand:

```powershell
gh workflow run ci-worker.yml       # pytest, ruff, build, schema, shellcheck, CPU container
gh workflow run ci-docs.yml         # repo guards, check_*.py, scripts tests, link check
gh workflow run ci-app.yml          # skips until app/ exists (issue #61)
gh workflow run codeql.yml          # python, actions and csharp analysis (also runs weekly, on PRs and pushes to main touching app/, worker/, scripts/ or .github/workflows/; config in .github/codeql/codeql-config.yml, #484)
gh run watch                        # follow the run you just started
gh run list --limit 5               # what ran recently and how it went
```

Every workflow file carries its original triggers in a comment directly above the `on:`
block, so restoring automatic CI is uncommenting a block rather than reconstructing one.

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

## GPU test recipe

**Never on the laptop.** `pytest -m "not gpu"` is the standing local command;
anything marked `gpu` needs a real CUDA GPU the dev box does not have
(`dev-laptop-has-no-cuda` memory seed).

```powershell
scripts\cloud_gpu_test.ps1                   # dry-run (default): prints the plan, machine type, zone, estimated cost/minutes, creates nothing
scripts\cloud_gpu_test.ps1 -Apply            # NOT functional yet — see below
scripts\cloud_gpu_test.ps1 -SelfTest
```

**Updated 2026-09-19: the script exists (issue #317), dry-run is real,
`-Apply` deliberately is not yet.** The dry-run path (labels, cost/duration
math, the leak-check and interrupt-safety contract) is provable today with
zero cloud spend and is exercised by `-SelfTest`. `-Apply` looks for a built
`CloudCli` (issue #60, itself built from `app/`, issue #61) and fails
clearly, naming #60/#61/#24, rather than silently falling back to a raw
`gcloud` call — Appendix C's permission design denies `gcloud` to an agent
on purpose, and a script that quietly substitutes it under the hood would
defeat that. `docs/ToTest.md` carries the row for exercising `-Apply` for
real once `CloudCli` exists. Once it works: launches a labelled VM in the
owner's GCP project (blocked on `OWNER_TODO.md` item 2 / issue #24 until a
project and GPU quota exist), runs `pytest -m gpu` on it, and tears the VM
down. Read `working-on-gcp` before running it for real even once `-Apply`
works — it creates real, billed cloud resources. Launch detached and poll
rather than blocking a single tool call on it (a real run is 6-20 minutes;
see the `a-tool-call-caps-at-600s` memory seed).

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
