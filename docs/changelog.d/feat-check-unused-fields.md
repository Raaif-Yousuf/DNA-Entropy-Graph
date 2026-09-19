- Added `scripts/check_unused_fields.py`, an eighth guard: it parses every `@dataclass` under
  `worker/src/dna_entropy/` and fails on any field that is set and never read, counting an
  attribute load or `getattr` as a read and a plain assignment or a construction keyword
  argument as not one. Reads that happen only inside a serialization method are reported
  separately, because a field that only travels back out to JSON still does nothing. Reads are
  also collected from `scripts/`, since a generator there can legitimately be a field's only
  consumer, as `ErrorCodeSpec.raised_by` is.
- MEASURED 2026-09-19: its first run found nine fields that were parsed, validated,
  schema-checked and ignored, including `Lifecycle.afterTask="keep"` leaving a VM running with
  no expiry at all (a Hard Rule 11 violation worth about twenty dollars a day), and
  `ModelRequirement.min_gpu_count` letting a single-GPU machine past the gate for a model that
  needs two cards. All nine are fixed (#292, #293, #296, #304, #306, #338, #340) or tracked
  (#343, #344, #345, #361).
- The guard documents its own blind spot rather than hiding it: it matches attribute reads by
  name, not by type, so a field whose name collides with a read attribute on another class is
  silently counted as read. `WindowPlan.context` and `StoreSpec.bucket/prefix/root` are masked
  that way today. A clean run means no field with a name nothing reads, not no unread field.
- `scripts/unused_fields_allowlist.json` carries the exceptions, each with a written reason, and
  supports `Class.*` for a document serialized wholesale with `dataclasses.asdict()`. A stale
  entry fails the guard, so the file cannot rot into a list of things that used to be true.
