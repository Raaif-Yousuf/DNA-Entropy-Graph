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

**ruff (lint/format): pending #285.** `worker/pyproject.toml` has no
`[tool.ruff]` table yet, so `ci-worker.yml` skips lint entirely rather than
failing on an absent config (it detects this and prints a notice). Once
#285 lands:

```powershell
worker\.venv\Scripts\python.exe -m ruff check worker
worker\.venv\Scripts\python.exe -m ruff format --check worker
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

**`app/` does not exist yet.** Everything in this section is what will work
once issue #61 lands; none of it runs today. `ci-app.yml` already checks for
`app/DnaEntropyGraph.sln` and no-ops with a notice if it is absent, which is
the current state.

```powershell
dotnet restore app
dotnet format app --verify-no-changes --no-restore
dotnet build app/DnaEntropyGraph.sln -c Debug -p:Platform=x64
dotnet build app/DnaEntropyGraph.sln -c Release -p:Platform=x64 -warnaserror
dotnet test app/DnaEntropyGraph.sln
dotnet test app/tests/DnaEntropyGraph.Guards.Tests               # fast, seconds
dotnet test app/DnaEntropyGraph.sln --filter "FullyQualifiedName!~UiTests"
```

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
```

## `scripts/check_*.py` (repo/docs guards) — mostly pending

`ci-docs.yml`'s `checks` job runs every `scripts/check_*.py` that exists and
prints a notice if none do — it tightens automatically as each lands rather
than needing a workflow edit per issue. None exist as of this writing.
Expected, per Appendix C's repo layout and various P0 issues: `check_docs_
index.py` (#25 range), `check_changelog_fragments.py`,
`check_version_lockstep.py`, `check_third_party_notices.py` (#34),
`check_totest_format.py`, `gen_third_party_notices.py`,
`gen_manifest_schema.py` (#39).

## `scripts/hooks/` (Claude Code `PreToolUse`/`SessionStart`/`Stop`) — exist today

Not run by hand under normal use — wired into `.claude/settings.json` and
invoked automatically. Useful to run directly when debugging a hook itself:

```powershell
worker\.venv\Scripts\python.exe scripts\hooks\block_git_stash.py
worker\.venv\Scripts\python.exe scripts\hooks\block_recursive_delete.py
worker\.venv\Scripts\python.exe scripts\hooks\block_unlabelled_vm_create.py
worker\.venv\Scripts\python.exe scripts\hooks\block_agent_dispatch_in_worktree.py
```

Each reads the tool-call payload from stdin per `scripts/hooks/README.md`.

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
scripts\cloud_gpu_test.ps1                   # pending: script does not exist yet
```

Once it exists: launches a labelled VM in the owner's GCP project (blocked
on `OWNER_TODO.md` item 2 / issue #24 until a project and GPU quota exist),
runs `pytest -m gpu` on it, and tears the VM down. Read `working-on-gcp`
before running it even once it exists — it creates real, billed cloud
resources. Launch detached and poll rather than blocking a single tool call
on it (a real run is 6-20 minutes; see the `a-tool-call-caps-at-600s`
memory seed).

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
