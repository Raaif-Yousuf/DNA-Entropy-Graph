---
name: tests-first
description: Use before writing ANY feature, fix or behaviour change in DNA-Entropy-Graph, in C# or Python. Write the failing test first, prove it fails, add the neighbours, then the code, then prove it passes. Also use when a fix "should work" but nothing proves it, or when adding a guard.
---

# Tests first

Before writing a feature or a fix: write the test first, watch it fail, add the
neighbouring tests, then write the code, then prove the tests pass. This is not
about coverage — it is about never being the first reader of your own change.

## The loop, in order

1. **Name the observable.** One sentence: what differs when the change is
   wired to nothing? (See `wired-to-nothing`.) The first test asserts exactly
   that, not a proxy for it.
2. **Write the failing test(s) in the right place.**
   - C# (`app/`): `app/tests/DnaEntropyGraph.<Proj>.Tests/<Class>Tests.cs`,
     mirroring the project under test (`Core` -> `Core.Tests`, `Cloud` ->
     `Cloud.Tests`, `Presentation` -> `Presentation.Tests`). ViewModel and
     runner tests use `FakeGcp` (Hard Rule 7) and a fake/injectable time
     source, never a real `DispatcherQueue` or UI thread.
   - Python (`worker/`): `worker/tests/test_<module>.py`, mirroring
     `worker/src/dna_entropy/`. A new `predictors/` or `analysis/` module
     needs its test file FIRST (Hard Rule 1).
3. **Run it and paste the failure, verbatim.**
   - C#: `dotnet test app/DnaEntropyGraph.sln --filter "FullyQualifiedName~<Class>"`
   - Python: `worker\.venv\Scripts\python.exe -m pytest worker/tests/test_<module>.py -q -x -m "not gpu"`
   A test that passes before the code exists is asserting the wrong thing —
   it proves nothing about the change, only about the fixture. Rewrite it.
4. **Write the neighbouring tests.** The same shape on the adjacent input, not
   just the happy path. For this repo's science and cloud surfaces, the
   standing neighbour set is:
   - an empty contig / zero-length sequence
   - a GenBank record with ambiguity codes (`N`, `R`, `Y`, ...)
   - a multi-record FASTA/GenBank file
   - `L < 2K` (shorter than one window) and `L > W` (many windows)
   - a cloud error of every class the fake can produce: `billing`,
     `api_disabled`, `quota`, `stockout`, `already_exists`, `permission`,
     `org_policy`, `network`, `other` (Critical Pitfalls, `CloudErrorClassifier`)
   - the reverse-complement case, never a merely-reversed string (Hard Rule
     "Reverse means reverse complement")
5. **Write the code.** The smallest change that turns the tests green.
6. **Run the targeted tests again and paste the pass**, then the fast
   structural set for whichever half you touched:
   - C#: `dotnet test app/tests/DnaEntropyGraph.Guards.Tests`
   - Python: `worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu"`
   **Never the full suite from an agent** — the orchestrator runs it, alone,
   because concurrent full suites exhaust the machine (see `orchestrating-agents`).
7. **Report** with both outputs (red, then green), verbatim, not summarised.

## Non-negotiables

- No code before step 3's red output exists in your transcript.
- "I will add tests after" is the exact failure mode this skill exists to stop.
- A guard, ratchet or allowlist (anything under `DnaEntropyGraph.Guards.Tests`
  or a `scripts/check_*.py`) gets a test that plants the violation and proves
  it is caught, plus a vacuity assertion that the scanner sees at least one
  real site. **A mutation arm that stays green is a coverage hole, not a
  pass** — assert the VALUE the check produces, never mere presence
  (`"billing" in classes` still passes if the classifier's real branch was
  deleted and the fake's own default happened to supply the string).
- Rule 18: a mechanism you did not directly observe is `THEORY (unverified):`,
  not stated as fact.
- **Never run a GPU-marked test on the laptop.** GPU tests run only via
  `scripts/cloud_gpu_test.ps1` on a labelled VM in the owner's project (Hard
  Rule 15); the dev laptop's GPU cannot run CUDA workloads.
