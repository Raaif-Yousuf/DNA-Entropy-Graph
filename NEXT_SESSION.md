# Handoff: after the 2026-09-19 second overnight wave

> Overwritten every session. Re-check `git log -1 main` and `gh issue list` before trusting anything
> here. Counts are deliberately not written down; run
> `gh issue list --repo Raaif-Yousuf/DNA-Entropy-Graph --limit 500 --json number,labels,milestone`
> for current numbers.

## What this session was

One orchestrator and three Sonnet agents at a time, in one shared checkout, each owning a disjoint
set of paths, with the orchestrator holding every `git` command. Same shape as the first wave, with
two changes.

**Nothing goes to `main` by a direct push any more.** Every unit of work is a branch, a pull
request and a merge, one issue per PR wherever the files allow. The convention and its two
mechanical traps are written down in [`docs/branching_and_prs.md`](docs/branching_and_prs.md); read
it before the first commit. The short version: `--merge`, never `--squash`, and **CI here fires on
demand only, so a pull request that looks green has been checked by nothing.**

**The wave ended abruptly.** All three agents died within seconds of each other on the org monthly
spend limit, mid-edit, exactly as the first wave did. Because no agent had ever run `git`, every
partial change was exactly where the path assignment said it would be, and the orchestrator landed
all of it: two lanes were complete, and the third had written its tests and left them red, which is
the intended state of a test-first lane. Finishing it took a missing import, an error-code registry
entry, a schema regeneration and two ruff fixes. **Nothing was lost.**

## Start here next session

1. **`OWNER_TODO.md`** still, and it has grown. The `DECISION` pile now includes six new
   agent-made calls, each marked reversible and each carrying its reasoning: **#379** (what the
   worker does when the store is unreachable for a long time), **#364** (the model gate fails
   closed on an unknown id), **#353** (the Geneious track bins above 200,000 positions), **#333**
   (per-exon segments for spliced genes), **#332** (the prototype launcher was deleted), and
   **#361** (whether `JobManifest.raw` was meant to be a forward-compatibility hook).

2. **Launch the app.** It has never been run. `app/` builds, 51 tests pass, and none of that says
   a window appears. [`docs/ToTest.md`](docs/ToTest.md) has the row and names the two false passes:
   an unpackaged WinUI 3 app resolves the Windows App SDK bootstrapper **at runtime**, so a failure
   there is a silent exit with no window and no error; and a `.resw` that is not packed as a PRI
   resource gives a window whose every label is **blank**, which reads as an unfinished layout
   rather than a broken build.

3. **Then `app/` in earnest.** The skeleton and all five guards are done (#61, #68). The next
   pieces are #64 (the input validator port, which has golden vectors in `tests/contract-fixtures/`
   produced by running the real worker, so the port has something to be wrong against), #57
   (`CloudErrorClassifier`, whose fixtures were extracted **with their evaluation order recorded**
   because order is load-bearing), and #62 (the shell).

## What exists now that did not this morning

- **`app/` exists.** Thirteen projects, six test projects, 51 tests, 0 warnings. The dependency
  arrows are structurally true before there is a real call to enforce them against: `Core` has no
  `Google.*`, `Presentation` has no WinUI reference, both `.xaml.cs` files are branch-free.
- **All five guards from #68**, each proven able to fail against the real tree, not a fixture.
  Each scanner reports what it actually **read**, not only what it objected to, and has a test for
  its own false pass.
- **`scripts/check_unused_fields.py`**, the eighth guard: a dataclass field that is parsed,
  validated, schema-checked and never read. Its first run found nine real ones.
- **A .NET 10 SDK on the box.** It was missing entirely. `docs/onboarding.md` section 4 has both
  install routes and why the obvious one returns exit code 1602 with nobody at the keyboard.

## What was found that is worth more than the fixes

**`afterTask: "keep"` left a VM running with no worker-side expiry at all.** Hard Rule 11, and
roughly $20 a day for an L4 or $88 for an A100. It now degrades to `afterKeepAlive` until #93
builds the real keep-alive queue.

**`ci-app.yml`'s test step had never run a single test.** It carried `--filter`, `--logger trx` and
`--collect:"XPlat Code Coverage"`, all VSTest options, against xunit.v3 / Microsoft.Testing.Platform
projects: all six assemblies reported `Zero tests ran`, exit code 5. It went red for the right
reason, and an exit code of 0 there would have been a permanently green job testing nothing.

**The malformed-input fuzz corpus was being normalized by git.** `*.fasta text` with `eol=lf` meant
a CRLF fixture was committed with its CRLFs already stripped, while the suite kept passing because
pytest reads the working tree. A fresh clone would have tested a different file and nothing would
have said so.

**`min_gpu_count` was never read**, so a single-GPU machine passed the gate for a model needing two
cards, and would have found out after paying for the VM, the boot and the weight download.

**A guard caught a cross-lane regression no human was watching.** #346 added a `MODEL_UNKNOWN` error
code in one lane without the registry entry, in a PR merged without a full-suite run, and
`test_every_code_literal_in_worker_python_source_is_registered` caught it hours later.

**An agent disproved its own claim.** #350 was filed saying Windows refuses to create `CON.fasta`.
Measured: it does not, in Python, .NET or PowerShell. The issue carries the disproof, and what
survived is the part that was real, which was silent overwrite when two records sanitize to the
same name.

## Still true, still unproven

**Nothing cloud-facing has ever run against real Google Cloud.** `lifecycle.py`, `GcsBlobstore`,
`startup.sh`, `cloud_gpu_test.ps1 -Apply`, `Dockerfile.cuda`, and now the #341 local fallback are
implemented, tested against fakes, and unproven. They are rows in `docs/ToTest.md`, each naming its
own false pass, not claims. The cost estimate for draining that queue is unchanged from the previous
handoff and is in `git log` for `NEXT_SESSION.md`; the number that matters is still that one
forgotten VM over a weekend costs more than the entire test campaign.

**`analysis/surprisal.py` is wired to nothing.** The module and its 13 tests exist; no writer emits
it and no CLI flag turns it on. #123 is open and the remaining work is the wiring.

## Traps this session paid for, beyond the ones already in `CLAUDE.md`

- **`ruff` is a CI gate and a whole wave ran without it.** It is deliberately not in `worker\.venv`;
  run `uvx ruff check worker` and `uvx ruff format --check worker`. Both are in
  `docs/dev_commands.md` and neither had been run.
- **Never give two agents different sections of the same file.** Two lanes both owned parts of
  `docs/science_and_formats.md` and both edited it, so neither lane's work could be committed
  without sweeping in the other's half-finished edit. The recipe that avoids touching a working tree
  three agents are live in is in the `overnight-agent-wave-protocol` memory.
- **`dotnet test` must run from `app/`**, because `global.json`'s `test.runner` setting resolves from
  the current working directory, not the project path. From the repo root it fails with "Testing
  with VSTest target is no longer supported", which reads like a broken test project.
- **Central Package Management refuses an undeclared package before any guard runs** (`NU1010`),
  which is a second layer nobody had counted on when writing the Hard Rule 7 guard.
