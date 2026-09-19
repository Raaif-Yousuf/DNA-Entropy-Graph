# Onboarding: first hour on a fresh Windows 11 box

Goal: `worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu"`
green, and (once `app/` exists — see the app-half note below)
`dotnet build app/DnaEntropyGraph.sln` green. Both, plus a look at the app
running, is the 10-minute smoke test at the end.

This assumes Windows 11 and a GitHub account with access to
`Raaif-Yousuf/DNA-Entropy-Graph`. No GCP project, no OAuth client, no signing
key is needed for this hour — those are `OWNER_TODO.md` items, not a dev
setup step.

## 1. Clone

```powershell
git clone https://github.com/Raaif-Yousuf/DNA-Entropy-Graph.git
cd DNA-Entropy-Graph
```

## 2. Install `uv` and set up the worker

`uv` is the only sanctioned way to create the worker's venv (Hard Rule 20).

```powershell
winget install --id astral-sh.uv
```

```powershell
cd worker
uv venv --python 3.12
uv pip install -e ".[dev,genes]"
cd ..
```

**The MSYS2-python trap (Hard Rule 20, `msys2-python-on-path` memory
seed):** if this box has an MSYS2 or Git-for-Windows Python earlier on
`PATH` than a real CPython, a bare `python` or `py` call can silently
resolve to a Python build with no usable wheels. `uv venv --python 3.12`
resolves a real interpreter regardless of `PATH`; the trap is only in *using*
a bare `python` afterward instead of `worker\.venv\Scripts\python.exe`
explicitly. Get in the habit of the explicit path from the first command.

## 3. Prove the worker is green

```powershell
worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu" -q
```

This must be green before doing anything else. If it is not, stop and fix
it before touching `app/` — a red worker on a fresh box is a setup problem,
not a code problem, almost always.

## 4. Install the .NET 10 SDK and Visual Studio (for the app half)

**MEASURED on the owner's dev machine, 2026-09-19:** Visual Studio
**Community**, major version **18** (the 2026 release line), was already
installed at `C:\Program Files\Microsoft Visual Studio\18\Community`, and
the **.NET runtimes** 8.0, 9.0 and 10.0 were present
(`dotnet --list-runtimes`), but **no .NET SDK was installed**
(`dotnet --list-sdks` returned empty). A `dotnet build` fails with "No .NET
SDKs were found" until the SDK itself is installed — the runtime alone is
not enough to build anything.

**MEASURED 2026-09-19, later the same day: the winget install does not
work unattended on this box.**

```powershell
winget install Microsoft.DotNet.SDK.10
```

fails with `Installer failed with exit code: 1602` ("You cancelled the
installation"). 1602 here is not a user cancelling anything: the SDK's
machine-wide installer asks for elevation, and a non-interactive session
has nobody to answer the UAC prompt, so the prompt is dismissed and the
installer reports a cancel. Running the same command from an elevated
PowerShell works.

**The unattended route that does work needs no administrator at all**, and
is what is installed on this machine now:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:USERPROFILE\.dotnet" -NoPath
```

This drops the SDK in `%USERPROFILE%\.dotnet` instead of
`C:\Program Files\dotnet`. The shared host already on `PATH` only finds
SDKs beside itself, so a per-user SDK is invisible until `PATH` prefers it.
Both of these are set as **user** environment variables on this machine:

| Variable | Value |
| --- | --- |
| `Path` | `%USERPROFILE%\.dotnet` prepended to the existing value |
| `DOTNET_ROOT` | `%USERPROFILE%\.dotnet` |

MEASURED 2026-09-19 after that: `dotnet --version` reports `10.0.401` and
`dotnet --list-sdks` reports `10.0.401 [%USERPROFILE%\.dotnet\sdk]`.
A shell opened **before** those variables were set still sees no SDK, which
looks exactly like a failed install; open a new one before believing it.

In Visual Studio's Installer, add the **Windows App SDK** / WinUI 3
workload (".NET Desktop Development" plus the Windows App SDK component;
the exact component name may shift release to release — check
`app/global.json` once it exists for the pinned SDK version this repo
targets).

**This step cannot be fully verified yet.** `app/DnaEntropyGraph.sln` does
not exist until issue #61 lands. Once it does, the real verification is:

```powershell
dotnet build app/DnaEntropyGraph.sln -c Debug -p:Platform=x64
dotnet test app/DnaEntropyGraph.sln
```

## 5. The 10-minute smoke test

Today (worker only):

```powershell
worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu" -q
worker\.venv\Scripts\python.exe -m dna_entropy --help
```

Once `app/` exists (issue #61) and `scripts/dev_app.ps1` is written, add:

```powershell
scripts\dev_app.ps1
```

which launches the app against `FakeGcp` (`DEG_FAKE_CLOUD=1`, see
`winui-dev` skill) — no real Google Cloud project needed for this smoke
test. Drop a sample file from `worker/tests/data/` (`sample.fasta`,
`sample.gb`) and confirm a run completes against the fake.

## What this hour does NOT do

- Does not create a Google Cloud project, enable any API, or spend any
  money. That is `OWNER_TODO.md` items 1-2 (#21, #24), owner-only, and
  blocks a *real* cloud run, not this onboarding smoke test.
- Does not install a code-signing certificate. `OWNER_TODO.md` item 3
  (#23) is owner-only and blocks a signed release, not local development.
- Does not run any `gpu`-marked test. The dev laptop's GPU (if any) is not
  assumed CUDA-capable; GPU tests run only via `scripts/cloud_gpu_test.ps1`
  on a labelled VM (Hard Rule 15, `dev-laptop-has-no-cuda` memory seed).

## Related

[`dev_commands.md`](dev_commands.md) (every command, PowerShell
equivalents), [`tests.md`](tests.md) (what runs where),
[`environment.md`](environment.md) (env vars and dev overrides once they
exist).
