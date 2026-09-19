- Fixed #341: `StatusWriter.consecutive_write_failures` counted store write failures and nothing
  escalated. Five consecutive failures now crosses from "blip" to "outage": an `ESCALATED` line on
  stderr, distinguishable from the per-blip line, plus a best-effort local-disk snapshot of the
  current status. A `result.json` write that fails outright writes a local fallback copy too. The
  design call (escalate and leave a breadcrumb, rather than fail the run or retry forever silently)
  is recorded as #379, agent-made and reversible.
- The reason this is not a counter problem: from the app's side a stale `status.json` means the run
  is dead, so the app stops the VM while the worker is alive and burning GPU minutes on a job whose
  result nobody will collect. The sharp end is a run that finishes successfully and cannot say so.
- The local fallback is honest about what it is not. A VM that reaches
  `instanceTerminationAction=DELETE` loses its whole disk, fallback included, so this helps only a
  VM that is merely stopped or one a human inspects via a disk snapshot. Marked
  `THEORY (unverified)` in the code and now a `docs/ToTest.md` row.
- Fixed #344: `manifest.worker.version` was parsed and dropped. A disagreement between the version
  the app declares and the build actually running is now a notice in `progress.jsonl` naming both.
  Visibility only, per the issue's scope: it never refuses or fails the job, and an undeclared
  expectation is not a mismatch against anything.
- Registered `MODEL_UNKNOWN` in the worker's error-code taxonomy and regenerated
  `docs/contract/error-codes.json`. #346's fix in `predictors/hardware.py` added the code without
  the registry entry, and `test_every_code_literal_in_worker_python_source_is_registered` caught it,
  which is the guard doing exactly its job across a lane boundary.
