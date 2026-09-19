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

Two of the transcribed behaviours have already been re-expressed as machine-readable
fixtures for a future C# test (issue #284, tracked further in #213):
- `worker/tests/contract-fixtures/cloud_error_classification.json` — `classify_create_error`'s
  billing/api_disabled/quota/stockout/already_exists/permission/network/other buckets.
- `worker/tests/contract-fixtures/cloud_quota_parsing.json` — `gpu_quota_metric` and
  `list_region_gpu_quota`'s CSV parsing (including the "quota unknown" vs. "quota is zero
  everywhere" `None`-vs-`{}` distinction).

`keeper.py`'s setup-diagnosis (`_diagnose_setup`), state machine (`_next_action`), and
quota pre-filter (`_apply_quota_health`) are **not yet** re-expressed as fixtures — their
exact behaviour is preserved in prose (with source line references) in the migration
inventory's "cloud/keeper.py" subsection only.
