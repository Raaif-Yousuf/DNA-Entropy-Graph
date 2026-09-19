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
Eight exist as of this revision, each with a `--self-test` flag that runs
against synthetic fixtures:

```powershell
python scripts\check_docs_index.py            # every docs/*.md is reachable from docs/README.md's index
python scripts\check_totest_format.py         # docs/ToTest.md row shape, Needs values, and real commit shas (--max-age-days 45 default)
python scripts\check_user_home_paths.py       # no literal absolute user-home path in a tracked file (use %USERPROFILE%/$HOME instead)
python scripts\check_third_party_notices.py   # THIRD-PARTY-NOTICES.md freshness (currently a no-op notice: the file itself doesn't exist yet, #34)
python scripts\check_version_lockstep.py      # every version-bearing file agrees
python scripts\check_changelog_fragments.py   # docs/changelog.d/ fragment shape (what ci-docs.yml runs on every PR; --self-test)
python scripts\check_guard_drift.py           # the guard scripts themselves haven't silently started passing by checking nothing
python scripts\check_unused_fields.py         # a worker dataclass field that is parsed, stored and never read (the #304/#306 shape)
python scripts\<any check_*.py> --self-test   # every one of the eight supports this
```

**Running `scripts/tests/` itself** — the hooks, the label/memory sync, `compile_sprint_log.py`, `agent_wave.ps1`, `new_issue.ps1`, `sync_labels.ps1`, and every `check_*.py` guard above all have their own test file here, run as one suite (issue #311's own scripts-tests CI job runs exactly this, on Ubuntu, with full git history since some tests exercise git-aware code paths):

```powershell
uv run --with pytest --with pyyaml python -m pytest scripts\tests -q
```

## `scripts/hooks/` (Claude Code `PreToolUse`/`SessionStart`/`Stop`) — exist today

Not run by hand under normal use — wired into `.claude/settings.json` and
invoked automatically. Useful to run directly when debugging a hook itself:

```powershell
worker\.venv\Scripts\python.exe scripts\hooks\block_git_stash.py
worker\.venv\Scripts\python.exe scripts\hooks\block_recursive_delete.py
worker\.venv\Scripts\python.exe scripts\hooks\block_unlabelled_vm_create.py
worker\.venv\Scripts\python.exe scripts\hooks\block_agent_dispatch_in_worktree.py
```

Each reads the tool-call payload from stdin per `scripts/hooks/README.md`, so running one
with no input will simply wait.

All five, including the `run_hook.py` dispatcher, take `--self-test`, which needs no stdin
and exercises both the allow and the deny arm:

```powershell
worker\.venv\Scripts\python.exe scripts\hooks\run_hook.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_git_stash.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_recursive_delete.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_unlabelled_vm_create.py --self-test
worker\.venv\Scripts\python.exe scripts\hooks\block_agent_dispatch_in_worktree.py --self-test
```

That is the fastest way to tell "the hook is broken" from "the hook is correctly refusing
what I asked for", which is the question you actually have when a command is denied.

## CI is on demand, not automatic

**Owner's decision, 2026-09-19.** None of the four workflows fires on a push, a pull
request or a schedule. Each is `workflow_dispatch` only, so a run happens when someone
asks for one:

```powershell
gh workflow run ci-worker.yml       # pytest, ruff, build, schema, shellcheck, CPU container
gh workflow run ci-docs.yml         # repo guards, check_*.py, scripts tests, link check
gh workflow run ci-app.yml          # skips until app/ exists (issue #61)
gh workflow run codeql.yml          # python and actions analysis
gh run watch                        # follow the run you just started
gh run list --limit 5               # what ran recently and how it went
```

Every workflow file carries its original triggers in a comment directly above the `on:`
block, so restoring automatic CI is uncommenting a block rather than reconstructing one.

**What this means in practice:** nothing checks a commit unless you ask it to. The local
guards are the same code CI runs, so run them before pushing rather than after:

```powershell
worker\.venv\Scripts\python.exe -m pytest worker\tests -m "not gpu" -q
worker\.venv\Scripts\python.exe -m pytest scripts\tests -q
Get-ChildItem scripts\check_*.py | ForEach-Object { worker\.venv\Scripts\python.exe $_.FullName }
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
