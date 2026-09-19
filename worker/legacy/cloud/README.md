# cloud/ legacy reference

`__init__.py`, `gcloud.py`, `keeper.py`, `orchestrator.py`, and `ui.py` in this folder are
copied **verbatim, unmodified**, from `worker/src/dna_entropy/cloud/` as it existed after
the byte-for-byte migration of the `DNA-Entropy-Genbank` prototype (commit
`8026bf5c4afbe3021c1f7e79a17a93af4eaad84b`) into this repo. Issue #280 relocated the whole
`cloud/` package here because it shells out to the user's own `gcloud`/SSH, which the
approved design forbids for the shipped app and worker (`docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md`
section 3: "Direct Google APIs from C#. No gcloud, no SSH. Bucket in, startup script,
bucket out."). **Nothing in this folder is imported or shipped by the worker package** —
`dna_entropy/cloud/` no longer exists under `worker/src/`.

This code is kept purely as the reference implementation for logic the C#
`DnaEntropyGraph.Cloud`/`DnaEntropyGraph.Core.{ErrorCatalog,GpuPlanner}` projects must
reproduce against the Compute/Billing/Service Usage/Cloud Quotas APIs directly. The
exhaustive, function-by-function mapping of what must be preserved (constants, error
buckets, quota metric names, the zone/offer escalation ladder, remediation text, the
keeper's setup-health-check state machine) lives in
`docs/migration/2026-09-19-worker-migration-inventory.md`, section "Prototype behaviours
the new design must preserve" — read that first; this folder is the byte-exact source it
was transcribed from, not a summary.

These behaviours have been re-expressed as machine-readable fixtures for a future C# test
(issue #284, #213 — both `gcloud.py`'s and `keeper.py`'s pieces are now done):
- `tests/contract-fixtures/cloud_error_classification.json` — `classify_create_error`'s
  billing/api_disabled/quota/stockout/already_exists/permission/network/other buckets.
- `tests/contract-fixtures/cloud_quota_parsing.json` — `gpu_quota_metric` and
  `list_region_gpu_quota`'s CSV parsing (including the "quota unknown" vs. "quota is zero
  everywhere" `None`-vs-`{}` distinction).
- `tests/contract-fixtures/keeper_next_action.json` — `_next_action`'s instance-state
  decision table (7 cases). Superseded by design D1 (no shared singleton box), recorded
  as reference for per-job VM state classification only — see the fixture's own
  `port_note`.
- `tests/contract-fixtures/keeper_setup_diagnosis.json` — `_diagnose_setup`'s 7 scenarios
  (project/billing/API/quota checks, including the auto-enable-API and
  unknown-billing-warns-not-blocks cases). The four checks are directly useful for the C#
  `SetupHealthService` (#204); the always-on-keeper calling context they ran in is not —
  see the fixture's own `port_note`.
- `tests/contract-fixtures/keeper_quota_prefilter.json` — `_apply_quota_health`'s 3 cases
  narrowing candidate zones to regions with confirmed GPU quota.

Every fixture above records its source commit, the exact generation method, and what was
verified about it (issue #213's closing report has the full provenance discipline this
followed, matching issue #316's fixture as the model). None of the `keeper_*.json`
fixtures assert on raw captured stdout: the prototype's setup/quota reports are
timestamped per line (wall-clock, machine- and run-dependent), so only booleans, zone
lists, and specific text substrings — the same granularity the prototype's own tests
already used — were frozen.
