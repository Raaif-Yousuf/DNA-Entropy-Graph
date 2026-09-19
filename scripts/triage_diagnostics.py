"""scripts/triage_diagnostics.py -- read a job diagnostics bundle in seconds.

    python scripts/triage_diagnostics.py <bundle.zip> [<bundle2.zip> ...]
    python scripts/triage_diagnostics.py --data-dir            # every local run under %LOCALAPPDATA%\\DNAEntropyGraph\\runs
    python scripts/triage_diagnostics.py <bundle.zip> --full    # every progress line, no capping
    python scripts/triage_diagnostics.py <bundle.zip> --tail 200
    python scripts/triage_diagnostics.py --check-schema  # SCHEMA_FIELDS vs. the real generated schema

A diagnostics bundle is the owner (or a field report) reaching for evidence
about one cloud or local job. The `fixing-a-bug` skill's own body names this
script explicitly: "the diagnostics zip is the field report" -- read it
before screenshots. It prints app version, worker version, image digest, the
`JobPhase` history, the `CloudError` classes seen, an inputs summary
(ambiguityPolicy per input, per-input result status), a manifest/status/
result jobId consistency check, and the last N `progress.jsonl` lines,
because those are exactly the fields a bug report needs and a human
hand-reading a multi-line JSONL file will miss half of.

Deliberately NOT a gate. It reports and ranks; it never exits non-zero on a
FINDING (a failed job, a stuck stage, an error code), only on a genuine
operational failure (bad usage, an unreadable bundle, a target with no job
data in it at all) -- a false alarm here costs a real investigation, same
reasoning as `route_reachability.py`'s own report-not-gate design in the
donor conventions this repo's tooling is modelled on. `--check-schema` is
the one exception: it IS a gate, on purpose (see below), because a triage
tool reading the wrong field silently produces "nothing found" at exactly
the moment someone needs an answer, and that failure mode deserves to be
loud in CI, not just in a bug report six weeks later.

SCHEMA RECONCILIATION (this file's own history, kept because the next drift
will look exactly like this one)
-----------------------------------------------------------------------------
This script was ported (issue #274) against the manifest/status/result shape
`docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md` sections
3.2-3.6 DOCUMENTED, before `dna_entropy.worker` (issue #278) existed in code
at all -- SCHEMA_FIELDS was one hand-maintained block precisely so that
whenever the real implementation landed, reconciling was "edit one block",
not "re-derive field paths scattered through the report functions".

The worker subpackage now exists, and `worker/src/dna_entropy/worker/
schema_gen.py` generates real JSON Schema from its own dataclasses
(`docs/contract/{manifest,status,result}.schema.json`,
`scripts/gen_manifest_schema.py --check` fails CI on drift). Reconciling
SCHEMA_FIELDS against that generated schema (rather than the design doc)
found: `manifest.limits` gained `cancelPollSeconds`/`heartbeatSeconds`/
`maxInputs`/`maxTotalNt` (issue #248's batch cost guardrails) with no reader
here at all; `manifest.inputs[].ambiguityPolicy` (issue #249, replacing the
never-actually-consulted `allowAmbiguity`) had no reader here at all, so a
report never showed which ambiguity policy a failed input actually ran
under; `result.jobId` had no reader, so a manifest/status/result jobId
mismatch (three files from three different jobs accidentally bundled
together) was invisible; and the worker's own generated error-code registry
(`docs/contract/error-codes.json`, `dna_entropy.worker.errors.
WORKER_ERROR_CODES`) has THREE codes (`MANIFEST_INVALID`,
`WORKER_VERSION_MISMATCH`, `BATCH_LIMIT_EXCEEDED`) the static Appendix B
section 7 cloud taxonomy never listed, so a real job hitting one of them
would have been wrongly flagged "not in the documented taxonomy". Each of
these was a GAP IN THE SCHEMA COVERAGE here, not a bug in a value already
read -- see `_SCHEMA_FIELDS_KNOWN_GAPS` just below SCHEMA_FIELDS for the
opposite finding (four paths this file read that the generated schema
cannot confirm, because the worker's own dataclasses do not model them; a
gap in the SCHEMA, not in this tool).

MAKING THIS IMPOSSIBLE TO SILENTLY DRIFT AGAIN
-------------------------------------------------
Two different mechanisms, chosen per field per "prefer reading the real
schema if that is workable: a check that two things agree is weaker than
not having two things":

  - `KNOWN_ERROR_CODES` (used everywhere `KNOWN_CLOUD_ERROR_CODES` used to
    be) is READ from `docs/contract/error-codes.json` at import time and
    unioned with the static cloud taxonomy -- `load_known_error_codes()`
    genuinely reads the real registry; there is nothing left to drift here
    on the worker-error-code side.
  - `SCHEMA_FIELDS` stays hand-maintained: its keys (`app_version`,
    `predictor_model`, ...) are human-chosen report labels, not mechanically
    derivable from a bare JSON Schema path, so reading the schema cannot
    replace it the way it replaced the error-code list.
    `validate_schema_fields()` (every SCHEMA_FIELDS path must resolve
    through the generated schema) and `find_uncovered_schema_fields()`
    (every generated-schema leaf must have a SCHEMA_FIELDS entry) are the
    "two things agree" check for this half instead, run by `--check-schema`
    and by `scripts/tests/test_triage_diagnostics.py::
    test_schema_fields_reconciles_with_the_real_generated_schema` (part of
    the `scripts/tests` suite CI already runs), so the next rename is a red
    pytest run, not a silent "nothing found" during a real incident.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import zipfile
from pathlib import Path
from typing import Any

# ---------------------------------------------------------------------------
# SCHEMA COUPLING (see the module docstring's "SCHEMA RECONCILIATION" and
# "MAKING THIS IMPOSSIBLE TO SILENTLY DRIFT AGAIN" sections for the current
# state and the mechanism keeping it that way). `_dig()` reads one of these
# tuples out of a parsed JSON dict; nothing else in this file hand-rolls a
# `d.get("x", {}).get("y")` chain, so a schema change is a one-block edit.
# ---------------------------------------------------------------------------

# jobs/<jobId>/... under a cloud bucket (Appendix B section 3.2), or the
# equivalent local `store.root` directory for a `localdir` run. A bundle is
# "one job" when these live directly at the bundle root, and "many jobs"
# when they live under one subdirectory per job (see `_find_job_roots`).
JOB_FILES = {
    "manifest": "manifest.json",
    "status": "status.json",
    "progress": "progress.jsonl",
    "result": "result.json",
    "worker_log": "logs/worker.log",
    "startup_log": "logs/startup.log",
    "provenance": "output/provenance.json",
}

# manifest.json (schema 1) -- reconciled against the GENERATED schema
# (docs/contract/manifest.schema.json, produced by
# worker/src/dna_entropy/worker/schema_gen.py from the worker's own
# dataclasses) as of the round that closed issues #309/#310. Every path
# below is validated against that generated schema by
# `validate_schema_fields()`, and the reverse direction (a real schema leaf
# with no SCHEMA_FIELDS entry pointing at it) is checked by
# `find_uncovered_schema_fields()` -- see "SCHEMA RECONCILIATION" in the
# module docstring above for what disagreed and which direction each was.
SCHEMA_FIELDS: dict[str, tuple[str, tuple[str, ...]]] = {
    "job_id": ("manifest", ("jobId",)),
    "manifest_schema": ("manifest", ("schema",)),
    "created_at": ("manifest", ("createdAt",)),
    "app_version": ("manifest", ("createdBy", "appVersion")),
    "installation_id": ("manifest", ("createdBy", "installationId")),
    "manifest_worker_version": ("manifest", ("worker", "version")),
    "manifest_worker_image": ("manifest", ("worker", "image")),
    "predictor_kind": ("manifest", ("predictor", "kind")),
    "predictor_model": ("manifest", ("predictor", "model")),
    "predictor_precision": ("manifest", ("predictor", "precision")),
    "predictor_device": ("manifest", ("predictor", "device")),
    "predictor_seed": ("manifest", ("predictor", "seed")),
    "analysis_context_length": ("manifest", ("analysis", "contextLength")),
    "analysis_window": ("manifest", ("analysis", "window")),
    "analysis_stride": ("manifest", ("analysis", "stride")),
    "analysis_direction": ("manifest", ("analysis", "direction")),
    "analysis_format": ("manifest", ("analysis", "format")),
    "max_run_seconds": ("manifest", ("limits", "maxRunSeconds")),
    "limits_cancel_poll_seconds": ("manifest", ("limits", "cancelPollSeconds")),
    "limits_heartbeat_seconds": ("manifest", ("limits", "heartbeatSeconds")),
    "limits_max_inputs": ("manifest", ("limits", "maxInputs")),
    "limits_max_total_nt": ("manifest", ("limits", "maxTotalNt")),
    "lifecycle_after_task": ("manifest", ("lifecycle", "afterTask")),
    "lifecycle_keep_alive_minutes": ("manifest", ("lifecycle", "keepAliveMinutes")),
    "lifecycle_after_keep_alive": ("manifest", ("lifecycle", "afterKeepAlive")),
    "store_kind": ("manifest", ("store", "kind")),
    "store_bucket": ("manifest", ("store", "bucket")),
    "store_prefix": ("manifest", ("store", "prefix")),
    "store_root": ("manifest", ("store", "root")),
    # status.json -- Appendix B section 3.4, reconciled against
    # docs/contract/status.schema.json (worker/status.py's StatusDocument).
    "status_schema": ("status", ("schema",)),
    "status_job_id": ("status", ("jobId",)),
    "stage": ("status", ("stage",)),
    "percent": ("status", ("percent",)),
    "detail": ("status", ("detail",)),
    "started_at": ("status", ("startedAt",)),
    "updated_at": ("status", ("updatedAt",)),
    "heartbeat_seq": ("status", ("heartbeatSeq",)),
    "status_worker_version": ("status", ("worker", "version")),
    "status_worker_image": ("status", ("worker", "image")),
    "vm_name": ("status", ("vm", "name")),
    "vm_zone": ("status", ("vm", "zone")),
    "vm_gpu": ("status", ("vm", "gpu")),
    "vm_driver": ("status", ("vm", "driver")),
    "error_code": ("status", ("error", "code")),
    "error_message": ("status", ("error", "message")),
    "error_detail": ("status", ("error", "detail")),
    "error_retriable": ("status", ("error", "retriable")),
    "error_remediation": ("status", ("error", "remediation")),
    # result.json -- reconciled against docs/contract/result.schema.json
    # (worker/result.py's JobResult). Written LAST, per job_contract.md.
    "result_schema": ("result", ("schema",)),
    "result_job_id": ("result", ("jobId",)),
    "result_status": ("result", ("status",)),
    "result_timing": ("result", ("timing",)),
    "result_timing_started_at": ("result", ("timing", "startedAt")),
    "result_timing_finished_at": ("result", ("timing", "finishedAt")),
    "result_gpu": ("result", ("gpu",)),
    "result_gpu_name": ("result", ("gpu", "name")),
    "result_gpu_zone": ("result", ("gpu", "zone")),
    "result_gpu_spot": ("result", ("gpu", "spot")),
    "result_error": ("result", ("error",)),
    "result_error_code": ("result", ("error", "code")),
}

# ---------------------------------------------------------------------------
# SCHEMA RECONCILIATION (this round, issue: triage_diagnostics vs. the now-
# real generated schema). Every SCHEMA_FIELDS path above is checked against
# docs/contract/{manifest,status,result}.schema.json by
# `validate_schema_fields()`, EXCEPT the four below, which disagree with the
# generated schema ON PURPOSE -- each is a documented GAP IN THE SCHEMA, not
# a bug in this tool, and the reason is recorded here so the exemption
# cannot silently grow without a reason attached:
#
#   - created_at, app_version, installation_id (manifest.createdAt /
#     manifest.createdBy.{appVersion,installationId}): job_contract.md
#     section 2 still documents these as fields the APP writes into every
#     manifest.json. worker/src/dna_entropy/worker/manifest.py's own
#     JobManifest dataclass never models them -- the worker has no use for
#     them, and only threads the whole original dict through
#     JobManifest.raw (excluded from the generated schema on purpose, see
#     that field's own metadata={"json_exclude": True}). A REAL manifest.json
#     written by the app (once app/ exists, issue #61) will still carry
#     these fields; the generated schema simply cannot confirm that, because
#     it is generated from the worker's read side, not the app's write side.
#   - result_error_code (result.error.code): JobResult.error is a generic
#     `dict | None`, not a typed ErrorInfo (worker/result.py), so the
#     generated schema types result.json's error as a bare object with no
#     nested properties. In practice the worker always populates it with
#     the same {code, message, ...} shape status.json's ErrorInfo uses, but
#     the schema cannot say so -- a real gap, worth a typed ResultError
#     dataclass someday, flagged here rather than silently worked around.
# ---------------------------------------------------------------------------
_SCHEMA_FIELDS_KNOWN_GAPS: frozenset[str] = frozenset({
    "created_at", "app_version", "installation_id", "result_error_code",
})

# progress.jsonl -- one JSON object per line: {seq, ts, stage, level,
# percent, message, data}. Appendix B section 3.4.
PROGRESS_LINE_KEYS = ("seq", "ts", "stage", "level", "percent", "message", "data")

# The `JobPhase` state machine, in order -- Appendix B section 3.4. Used only
# to flag an OUT-OF-ORDER or UNRECOGNISED stage in a report; the report never
# refuses to show a stage this tuple does not know about (a documented list
# going stale must never make the tool blind to a real, newer stage).
JOB_STAGES: tuple[str, ...] = (
    "queued", "provisioning", "booting", "installing", "restoring-cache",
    "model-loading", "running", "uploading", "done", "failed", "cancelled",
    "idle", "finalizing",
)

# The `CloudError` taxonomy -- Appendix B section 7. Used only to flag a
# CODE this table does not recognise (worth a second look: either a new
# error class that needs a section 7 row, or a typo in the worker's own
# error-raising code); never used to filter or hide a code that IS
# recognised but looks unfamiliar to a human skimming the table.
KNOWN_CLOUD_ERROR_CODES: frozenset[str] = frozenset((
    "SIGNIN_EXPIRED", "CONSENT_INCOMPLETE", "NOT_PROJECT_OWNER", "PROJECT_QUOTA",
    "ORG_POLICY_BLOCK", "NO_BILLING", "BILLING_NO_PERMISSION", "FREE_TRIAL_NO_GPU",
    "API_DISABLED", "PERMISSION", "PERMISSION_ACTAS", "NO_GPU_QUOTA",
    "QUOTA_NOT_ELIGIBLE", "QUOTA_DENIED", "QUOTA_OTHER", "GPU_STOCKOUT",
    "NO_EXTERNAL_IP", "VM_BOOT_TIMEOUT", "GPU_NOT_VISIBLE", "IMAGE_PULL_FAILED",
    "MODEL_DOWNLOAD_FAILED", "MODEL_NEEDS_HOPPER", "MODEL_OOM", "INPUT_INVALID",
    "HEARTBEAT_LOST", "VM_DIED", "SPOT_PREEMPTED", "WORKER_CRASH",
    "RUN_TIME_LIMIT", "CANCELLED", "RETENTION_EXPIRED", "BUCKET_MISSING",
    "DOWNLOAD_FAILED", "NETWORK", "SPEND_CAP",
))

# `--data-dir`'s target: the local run-history directory (Appendix B section
# 3.3's `store: {"kind": "localdir", "root": "...\\runs\\<jobId>"}`), not
# CLAIR's `clair_data`. `%LOCALAPPDATA%` is read from the environment so
# this works on a real Windows machine; the hard-coded fallback only matters
# for a non-Windows dev/test box, which is where this script's own test
# suite runs it from.
def default_local_data_dir() -> Path:
    local_appdata = os.environ.get("LOCALAPPDATA")
    base = Path(local_appdata) if local_appdata else Path.home() / "AppData" / "Local"
    return base / "DNAEntropyGraph"


DEFAULT_TAIL = 50


# ---------------------------------------------------------------------------
# Reading the REAL generated schema at runtime, rather than re-typing it a
# second time by hand. "Prefer reading the real schema if that is workable:
# a check that two things agree is weaker than not having two things" is
# followed here for the error-code registry -- KNOWN_ERROR_CODES below is
# genuinely computed from docs/contract/error-codes.json at import time, not
# merely checked against it. SCHEMA_FIELDS above stays hand-maintained
# (its human-chosen names like "app_version" are not mechanically derivable
# from a bare JSON Schema path), so for it the check-that-two-things-agree
# shape is what `validate_schema_fields()`/`find_uncovered_schema_fields()`
# below provide instead, run by --check-schema and by
# scripts/tests/test_triage_diagnostics.py so a drift is caught by CI, not
# only by someone remembering to run this by hand.
# ---------------------------------------------------------------------------

def _repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def default_contract_dir() -> Path:
    return _repo_root() / "docs" / "contract"


_SCHEMA_DOC_FILENAMES: dict[str, str] = {
    "manifest": "manifest.schema.json",
    "status": "status.schema.json",
    "result": "result.schema.json",
}


def load_known_error_codes(schemas_dir: Path | None = None) -> frozenset[str]:
    """KNOWN_CLOUD_ERROR_CODES (Appendix B section 7's app/cloud-level
    taxonomy -- SIGNIN_EXPIRED, GPU_STOCKOUT, ...) UNIONED with
    docs/contract/error-codes.json's own codes (the WORKER's generated
    registry, `dna_entropy.worker.errors.WORKER_ERROR_CODES` -- MODEL_OOM,
    MANIFEST_INVALID, ...). A status.json/result.json error.code seen in the
    wild can legitimately come from either source, and as of this round the
    worker registry has THREE codes (MANIFEST_INVALID, WORKER_VERSION_MISMATCH,
    BATCH_LIMIT_EXCEEDED) the static cloud taxonomy alone did not know about --
    every one of those would have been wrongly flagged "not in the documented
    taxonomy" before this function existed. Falls back to the static cloud
    taxonomy alone if error-codes.json cannot be read (missing file, bad
    JSON) -- a missing generated artefact should never crash a triage read,
    only narrow what it recognises."""
    codes = set(KNOWN_CLOUD_ERROR_CODES)
    path = (schemas_dir or default_contract_dir()) / "error-codes.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        for entry in data.get("codes", []):
            if isinstance(entry, dict) and isinstance(entry.get("code"), str):
                codes.add(entry["code"])
    except (OSError, json.JSONDecodeError):
        pass
    return frozenset(codes)


# Computed once, from the real repo tree, at import time -- exactly the
# "read the real schema at runtime" mechanism issue asked for. A caller that
# wants a hermetic, synthetic union for a test calls load_known_error_codes()
# directly with its own schemas_dir instead of relying on this constant.
KNOWN_ERROR_CODES: frozenset[str] = load_known_error_codes()


def _load_generated_schemas(schemas_dir: Path) -> dict[str, dict]:
    """{doc_name: parsed JSON Schema}, for whichever of manifest/status/
    result actually parse; a document that cannot be read is simply absent
    from the returned dict rather than raising, so a caller can report which
    SPECIFIC document was unreadable instead of failing opaquely on the
    first one attempted."""
    out: dict[str, dict] = {}
    for doc_name, filename in _SCHEMA_DOC_FILENAMES.items():
        path = schemas_dir / filename
        try:
            out[doc_name] = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
    return out


def _path_exists_in_schema(schema: dict, path: tuple[str, ...]) -> bool:
    """True if `path` resolves through `schema`'s nested "properties" tree
    -- the same shape `_dig()` walks over real JSON data, walked here over a
    JSON Schema document instead."""
    node: Any = schema
    for key in path:
        if not isinstance(node, dict):
            return False
        props = node.get("properties")
        if not isinstance(props, dict) or key not in props:
            return False
        node = props[key]
    return True


def validate_schema_fields(schemas_dir: Path | None = None) -> list[str]:
    """Every SCHEMA_FIELDS path (except `_SCHEMA_FIELDS_KNOWN_GAPS`, each
    with its own documented reason above) must resolve through the REAL
    generated JSON Schema. Returns one problem string per path that does
    not; an empty list means SCHEMA_FIELDS and the generated schema agree.
    This is the "bug in the triage tool" direction: a SCHEMA_FIELDS entry
    naming something the schema does not have."""
    schemas_dir = schemas_dir or default_contract_dir()
    schemas = _load_generated_schemas(schemas_dir)
    problems: list[str] = []
    for name, (doc_name, path) in SCHEMA_FIELDS.items():
        if name in _SCHEMA_FIELDS_KNOWN_GAPS:
            continue
        schema = schemas.get(doc_name)
        if schema is None:
            problems.append(f"{name}: no generated schema readable for document {doc_name!r}")
            continue
        if not _path_exists_in_schema(schema, path):
            problems.append(
                f"{name}: {doc_name}.{'.'.join(path)} is not in the generated schema "
                f"(docs/contract/{_SCHEMA_DOC_FILENAMES[doc_name]})"
            )
    return problems


def _flatten_schema_scalar_paths(schema: dict, prefix: tuple[str, ...] = ()) -> set[tuple[str, ...]]:
    """Every dotted path through `schema`'s "properties" tree that is a real
    LEAF -- not an object with its own nested properties, and not an array
    (SCHEMA_FIELDS' flat dotted-path shape cannot express "one entry per
    array item" at all; `manifest_input_ambiguity_policies()` and
    `result_input_statuses()` are the real tool for those, and this function
    deliberately does not ask them to be covered the same way a scalar is)."""
    props = schema.get("properties")
    if not isinstance(props, dict):
        return set()
    out: set[tuple[str, ...]] = set()
    for key, sub in props.items():
        if not isinstance(sub, dict):
            continue
        path = prefix + (key,)
        raw_type = sub.get("type")
        types = raw_type if isinstance(raw_type, list) else [raw_type]
        if "array" in types:
            continue
        if "object" in types and isinstance(sub.get("properties"), dict):
            out |= _flatten_schema_scalar_paths(sub, path)
        else:
            out.add(path)
    return out


def find_uncovered_schema_fields(schemas_dir: Path | None = None) -> list[str]:
    """Every real scalar leaf in the generated schema that NO SCHEMA_FIELDS
    entry points at. This is the "gap in the schema coverage" direction: the
    schema grew a field (Appendix B's own worked example: manifest.limits
    growing maxInputs/maxTotalNt for issue #248's batch cost guardrails, or
    inputs[].ambiguityPolicy replacing allowAmbiguity for issue #249) and
    nobody taught this tool to read it. Returns one 'doc.path' string per
    uncovered leaf; empty means every leaf the schema can express as a flat
    dotted path is covered by at least one SCHEMA_FIELDS entry."""
    schemas_dir = schemas_dir or default_contract_dir()
    schemas = _load_generated_schemas(schemas_dir)
    covered: set[tuple[str, tuple[str, ...]]] = {(doc, path) for doc, path in SCHEMA_FIELDS.values()}
    uncovered: list[str] = []
    for doc_name, schema in schemas.items():
        for path in sorted(_flatten_schema_scalar_paths(schema)):
            if (doc_name, path) not in covered:
                uncovered.append(f"{doc_name}.{'.'.join(path)}")
    return sorted(uncovered)


# ---------------------------------------------------------------------------
# Array fields: manifest.json's inputs[] and result.json's inputs[] cannot
# be expressed as a single dotted SCHEMA_FIELDS path (one entry per job, not
# per input), so they get their own small readers instead of being forced
# through field()/_dig(). Both are new this round -- inputs[].ambiguityPolicy
# (issue #249, replacing the never-actually-consulted allowAmbiguity) had no
# reader here at all before.
# ---------------------------------------------------------------------------

def manifest_input_ambiguity_policies(bundle: dict[str, Any]) -> dict[str, str]:
    """{input id: ambiguityPolicy} for every manifest.json inputs[] entry,
    in manifest order."""
    inputs = _dig(bundle.get("manifest"), ("inputs",))
    out: dict[str, str] = {}
    if not isinstance(inputs, list):
        return out
    for item in inputs:
        if isinstance(item, dict) and isinstance(item.get("id"), str):
            out[item["id"]] = str(item.get("ambiguityPolicy", "keep"))
    return out


def result_input_statuses(bundle: dict[str, Any]) -> dict[str, str]:
    """{input id: status} for every result.json inputs[] entry, in result
    order."""
    inputs = _dig(bundle.get("result"), ("inputs",))
    out: dict[str, str] = {}
    if not isinstance(inputs, list):
        return out
    for item in inputs:
        if isinstance(item, dict) and isinstance(item.get("id"), str):
            out[item["id"]] = str(item.get("status", "?"))
    return out


def job_id_mismatches(bundle: dict[str, Any]) -> list[str]:
    """Which of manifest.json/status.json/result.json disagree about jobId,
    among whichever of the three are present -- a real cross-file
    consistency check this tool could not do before result_job_id and
    status_job_id/job_id were both readable. Returns a human-readable list
    of mismatches; empty means every present document agrees (or fewer than
    two documents are present to compare)."""
    ids = {
        "manifest": field(bundle, "job_id"),
        "status": field(bundle, "status_job_id"),
        "result": field(bundle, "result_job_id"),
    }
    present = {k: v for k, v in ids.items() if v is not None}
    if len(present) < 2:
        return []
    distinct = set(present.values())
    if len(distinct) <= 1:
        return []
    return [f"{k}.jobId={v!r}" for k, v in present.items()]


def _dig(obj: dict[str, Any] | None, path: tuple[str, ...]) -> Any:
    cur: Any = obj or {}
    for key in path:
        if not isinstance(cur, dict):
            return None
        cur = cur.get(key)
    return cur


def field(bundle: dict[str, Any], name: str) -> Any:
    """Look up one SCHEMA_FIELDS entry against an already-loaded bundle dict
    (bundle["manifest"], bundle["status"], bundle["result"] are the parsed
    JSON documents, or {} if that file was absent)."""
    doc_name, path = SCHEMA_FIELDS[name]
    return _dig(bundle.get(doc_name), path)


def first(bundle: dict[str, Any], *names: str) -> Any:
    """The first non-None value among several SCHEMA_FIELDS lookups, in
    order -- e.g. prefer status.json's live worker version, fall back to
    manifest.json's if status.json is missing or not yet written."""
    for name in names:
        v = field(bundle, name)
        if v is not None:
            return v
    return None


# ---------------------------------------------------------------------------
# Bundle loading: a zip or a directory, containing either one job's files
# directly, or one subdirectory per job (matching the cloud bucket layout
# `jobs/<jobId>/...` or the local layout `runs/<jobId>/...`).
# ---------------------------------------------------------------------------

def _read_json(read_bytes) -> dict[str, Any]:
    try:
        return json.loads(read_bytes.decode("utf-8", "replace"))
    except (json.JSONDecodeError, UnicodeDecodeError):
        return {}


def _load_job_from_dir(root: Path) -> dict[str, Any]:
    bundle: dict[str, Any] = {"_files_present": []}
    for doc_name, rel in JOB_FILES.items():
        p = root / rel
        if not p.exists():
            continue
        bundle["_files_present"].append(rel)
        if rel.endswith(".jsonl"):
            continue  # progress is streamed separately, see _progress_lines
        try:
            bundle[doc_name] = json.loads(p.read_text(encoding="utf-8", errors="replace"))
        except json.JSONDecodeError:
            bundle[doc_name] = {}
    return bundle


def _progress_lines_from_dir(root: Path) -> list[str]:
    p = root / JOB_FILES["progress"]
    if not p.exists():
        return []
    return p.read_text(encoding="utf-8", errors="replace").splitlines()


def _find_job_roots_dir(root: Path) -> list[tuple[str, Path]]:
    """[(label, job_root_dir), ...]. A directory IS a job root if it
    contains manifest.json directly; a bundle may be one job root itself,
    or a tree with many (`jobs/<id>/manifest.json`, `runs/<id>/manifest.json`)."""
    if (root / "manifest.json").exists():
        return [(root.name, root)]
    found = sorted(root.rglob("manifest.json"))
    return [(p.parent.name, p.parent) for p in found]


def _load_bundle_dir(root: Path) -> list[tuple[str, dict[str, Any], list[str]]]:
    jobs = _find_job_roots_dir(root)
    if not jobs:
        # No manifest.json anywhere: still report whatever is directly at
        # the root, rather than refusing outright -- a partial bundle (say,
        # just a worker.log with no manifest yet) is still worth reading.
        return [(root.name, _load_job_from_dir(root), _progress_lines_from_dir(root))]
    return [(label, _load_job_from_dir(jroot), _progress_lines_from_dir(jroot)) for label, jroot in jobs]


def _load_bundle_zip(path: Path) -> list[tuple[str, dict[str, Any], list[str]]]:
    with zipfile.ZipFile(path) as z:
        names = [n for n in z.namelist() if not n.endswith("/")]

        # Group entries by "job directory": the path with the JOB_FILES
        # relative suffix stripped off. A flat zip (files at the archive
        # root) yields one group keyed "".
        by_job: dict[str, dict[str, str]] = {}
        for n in names:
            norm = n.replace("\\", "/")
            for doc_name, rel in JOB_FILES.items():
                if norm == rel or norm.endswith("/" + rel):
                    job_dir = norm[: -len(rel)].rstrip("/")
                    by_job.setdefault(job_dir, {})[doc_name] = n
                    break

        if not by_job:
            return [(path.name, {"_files_present": []}, [])]

        jobs: list[tuple[str, dict[str, Any], list[str]]] = []
        for job_dir, entries in sorted(by_job.items()):
            label = job_dir.rsplit("/", 1)[-1] if job_dir else path.name
            bundle: dict[str, Any] = {"_files_present": sorted(entries.values())}
            progress_lines: list[str] = []
            for doc_name, zname in entries.items():
                if doc_name == "progress":
                    progress_lines = z.read(zname).decode("utf-8", "replace").splitlines()
                    continue
                bundle[doc_name] = _read_json(z.read(zname))
            jobs.append((label, bundle, progress_lines))
        return jobs


def load_bundle(path: Path) -> list[tuple[str, dict[str, Any], list[str]]]:
    """[(job_label, bundle_dict, progress_lines), ...] for one target (a
    zip file or a directory), one entry per job found inside it."""
    if path.is_dir():
        return _load_bundle_dir(path)
    return _load_bundle_zip(path)


# ---------------------------------------------------------------------------
# Derived findings
# ---------------------------------------------------------------------------

def job_phase_history(bundle: dict[str, Any], progress_lines: list[str]) -> list[str]:
    """The distinct, in-order sequence of `stage` values this job passed
    through -- consecutive repeats collapsed (a `running` stage logs many
    progress lines; it is one phase, not N). Falls back to status.json's
    single current stage when there is no progress.jsonl to derive a
    history from."""
    stages: list[str] = []
    for line in progress_lines:
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        stage = obj.get("stage")
        if stage and (not stages or stages[-1] != stage):
            stages.append(stage)
    if not stages:
        current = field(bundle, "stage")
        if current:
            stages = [current]
    return stages


def unrecognised_phases(phases: list[str]) -> list[str]:
    return [p for p in phases if p not in JOB_STAGES]


def cloud_error_classes(bundle: dict[str, Any], progress_lines: list[str]) -> list[str]:
    """Every distinct `CloudError` code seen: status.json's terminal error
    (the authoritative one), plus any `data.code` spotted on an
    `level: "error"` progress line (a transient error the worker logged and
    retried past, which status.json alone would never show since it only
    ever holds the CURRENT/terminal state)."""
    codes: list[str] = []

    terminal = field(bundle, "error_code")
    if terminal:
        codes.append(terminal)

    for line in progress_lines:
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if obj.get("level") != "error":
            continue
        data = obj.get("data")
        code = data.get("code") if isinstance(data, dict) else None
        if code and code not in codes:
            codes.append(code)

    result_code = field(bundle, "result_error_code")
    if result_code and result_code not in codes:
        codes.append(result_code)

    return codes


def _fmt_progress_line(raw: str) -> str:
    try:
        obj = json.loads(raw)
    except json.JSONDecodeError:
        return f"  (unparseable) {raw[:190]}"
    seq = obj.get("seq", "?")
    stage = obj.get("stage", "?")
    level = obj.get("level", "info")
    percent = obj.get("percent")
    pct = f"{percent:>5.1f}%" if isinstance(percent, (int, float)) else "      "
    message = obj.get("message", "")
    tag = f"[{level.upper()}]" if level and level != "info" else "      "
    return f"  #{seq:<5} {stage:<16}{pct} {tag} {message}"


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

def _report_job(target_label: str, job_label: str, bundle: dict[str, Any],
                 progress_lines: list[str], tail: int, full: bool) -> dict[str, Any]:
    header = f"{target_label}" + (f"  job={job_label}" if job_label and job_label != target_label else "")
    print("=" * 78)
    print(header)
    print("-" * 78)

    app_version = field(bundle, "app_version") or "?"
    worker_version = first(bundle, "status_worker_version", "manifest_worker_version") or "?"
    worker_image = first(bundle, "status_worker_image", "manifest_worker_image") or "?"
    job_id = first(bundle, "status_job_id", "job_id") or "?"
    print(f"  app version    : {app_version}")
    print(f"  worker version : {worker_version}")
    print(f"  worker image   : {worker_image}")
    print(f"  job id         : {job_id}")

    mismatches = job_id_mismatches(bundle)
    if mismatches:
        print(f"  ! job id MISMATCH across files: {', '.join(mismatches)}")

    present = bundle.get("_files_present") or []
    if not present:
        print("  (no manifest.json/status.json/progress.jsonl/result.json found here)")

    stage = field(bundle, "stage")
    percent = field(bundle, "percent")
    updated_at = field(bundle, "updated_at")
    if stage:
        pct = f" ({percent:.1f}%)" if isinstance(percent, (int, float)) else ""
        print(f"  current stage  : {stage}{pct}   updated {updated_at or '?'}")

    vm_name = field(bundle, "vm_name")
    if vm_name:
        print(f"  vm             : {vm_name}  zone={field(bundle, 'vm_zone')}  "
              f"gpu={field(bundle, 'vm_gpu')}  driver={field(bundle, 'vm_driver')}")

    phases = job_phase_history(bundle, progress_lines)
    if phases:
        print("\n-- JobPhase history " + "-" * 57)
        print("  " + " -> ".join(phases))
        odd = unrecognised_phases(phases)
        if odd:
            print(f"  ! unrecognised stage name(s), not in the documented JobPhase list: {odd}")

    errors = cloud_error_classes(bundle, progress_lines)
    if errors:
        print("\n-- CloudError classes " + "-" * 55)
        for code in errors:
            flag = "" if code in KNOWN_ERROR_CODES else "  ! not in the documented taxonomy (section 7) or docs/contract/error-codes.json"
            print(f"  * {code}{flag}")
        msg = field(bundle, "error_message")
        detail = field(bundle, "error_detail")
        remediation = field(bundle, "error_remediation")
        retriable = field(bundle, "error_retriable")
        if msg:
            print(f"    message     : {msg}")
        if detail:
            print(f"    detail      : {detail}")
        if remediation:
            print(f"    remediation : {remediation}")
        if retriable is not None:
            print(f"    retriable   : {retriable}")

    policies = manifest_input_ambiguity_policies(bundle)
    statuses = result_input_statuses(bundle)
    if policies or statuses:
        print("\n-- inputs " + "-" * 67)
        # Manifest order first (the order the job actually declared), then
        # any id result.json mentions that manifest.json did not (should not
        # happen on a well-formed pair, but a report tool degrading to
        # "still shows it" beats silently dropping a row).
        seen: set[str] = set()
        for input_id in list(policies.keys()) + [i for i in statuses if i not in policies]:
            if input_id in seen:
                continue
            seen.add(input_id)
            line = f"  {input_id}: ambiguityPolicy={policies.get(input_id, '?')}"
            if input_id in statuses:
                line += f"  result={statuses[input_id]}"
            print(line)

    if progress_lines:
        shown = progress_lines if full else progress_lines[-tail:]
        omitted = 0 if full else max(0, len(progress_lines) - tail)
        print(f"\n-- progress.jsonl tail ({len(shown)} of {len(progress_lines)} line(s)) " + "-" * 20)
        if omitted:
            print(f"  ... {omitted} earlier line(s) omitted (use --full to see all)")
        for raw in shown:
            print(_fmt_progress_line(raw))

    result_status = field(bundle, "result_status")
    if result_status:
        timing = field(bundle, "result_timing")
        gpu = field(bundle, "result_gpu")
        print("\n-- result.json " + "-" * 62)
        print(f"  status: {result_status}" + (f"  timing={timing}" if timing else "")
              + (f"  gpu={gpu}" if gpu else ""))

    return {
        "app_version": app_version, "worker_version": worker_version,
        "worker_image": worker_image, "stage": stage, "updated_at": updated_at,
        "errors": errors, "phases": phases,
    }


def _delta(reports: list[tuple[str, dict[str, Any]]]) -> None:
    print("\n" + "=" * 78)
    print("DELTA across bundles (oldest first)")
    print("=" * 78)
    print("Multiple bundles for the same job are usually sent because something\n"
          "moved -- the stage advanced, stalled, or an error appeared.\n")
    for name, rep in reports:
        print(f"  {name}")
        print(f"    stage={rep.get('stage','?')}  updated={rep.get('updated_at','?')}  "
              f"errors={rep.get('errors') or '[]'}")

    first_rep, last_rep = reports[0][1], reports[-1][1]
    if first_rep.get("stage") == last_rep.get("stage") and first_rep.get("stage") not in (None, "?"):
        print(f"\n  STAGE DID NOT ADVANCE: still {last_rep.get('stage')!r} across every bundle. "
              "See section 3.5's per-stage deadlines -- this may be a hung worker.")
    if not first_rep.get("errors") and last_rep.get("errors"):
        print(f"\n  NEW ERROR appeared: {last_rep.get('errors')}")


def check_schema(schemas_dir: Path | None = None) -> int:
    """`--check-schema`'s body: SCHEMA_FIELDS vs. the real generated schema,
    both directions. Unlike every other code path in this file, THIS one is
    a gate (module docstring's "MAKING THIS IMPOSSIBLE TO SILENTLY DRIFT
    AGAIN") -- exits 1 on any disagreement, 0 clean. Also checked by
    `scripts/tests/test_triage_diagnostics.py`, so a drift is caught by
    `scripts/tests` in CI without anyone remembering to run this by hand."""
    forward = validate_schema_fields(schemas_dir)
    reverse = find_uncovered_schema_fields(schemas_dir)
    if not forward and not reverse:
        print("check_schema: SCHEMA_FIELDS agrees with the generated schema "
              f"({len(SCHEMA_FIELDS)} field(s), {len(_SCHEMA_FIELDS_KNOWN_GAPS)} documented gap(s)).")
        return 0
    for p in forward:
        print(f"ERROR: SCHEMA_FIELDS names a field the generated schema does not have: {p}", file=sys.stderr)
    for p in reverse:
        print(f"ERROR: the generated schema has a field SCHEMA_FIELDS does not read: {p}", file=sys.stderr)
    print(f"{len(forward) + len(reverse)} disagreement(s) between SCHEMA_FIELDS and "
          "the generated schema.", file=sys.stderr)
    return 1


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Read a DNA-Entropy-Graph job diagnostics bundle (zip or directory) in seconds.",
    )
    ap.add_argument("bundles", nargs="*", type=Path,
                     help="diagnostics zip(s), or an unpacked bundle directory")
    ap.add_argument("--data-dir", action="store_true",
                     help="also triage every local run under %%LOCALAPPDATA%%\\DNAEntropyGraph\\runs\\")
    ap.add_argument("--data-dir-path", type=Path, default=None,
                     help="override the local data directory (default: %%LOCALAPPDATA%%\\DNAEntropyGraph)")
    ap.add_argument("--full", action="store_true",
                     help="print every progress.jsonl line instead of a capped tail")
    ap.add_argument("--tail", type=int, default=DEFAULT_TAIL,
                     help=f"how many trailing progress.jsonl lines to show (default {DEFAULT_TAIL})")
    ap.add_argument("--check-schema", action="store_true",
                     help="verify SCHEMA_FIELDS against the real generated schema (docs/contract/*.schema.json) "
                          "and exit -- unlike every other mode here, this one IS a gate")
    args = ap.parse_args()

    if args.check_schema:
        return check_schema()

    targets: list[Path] = list(args.bundles)
    if args.data_dir or args.data_dir_path:
        data_dir = args.data_dir_path or default_local_data_dir()
        runs_dir = data_dir / "runs"
        if runs_dir.is_dir():
            found = sorted(p for p in runs_dir.iterdir() if p.is_dir())
            if found:
                targets.extend(found)
            else:
                print(f"no local runs under {runs_dir}", file=sys.stderr)
        else:
            print(f"no local data dir at {runs_dir}", file=sys.stderr)

    if not targets:
        ap.print_help()
        return 2

    all_reports: list[tuple[str, dict[str, Any]]] = []
    any_loaded = False
    for t in targets:
        if not t.exists():
            print(f"skipping missing {t}", file=sys.stderr)
            continue
        try:
            jobs = load_bundle(t)
        except (zipfile.BadZipFile, OSError) as exc:
            print(f"skipping unreadable {t}: {exc}", file=sys.stderr)
            continue
        for job_label, bundle, progress_lines in jobs:
            any_loaded = True
            rep = _report_job(t.name, job_label, bundle, progress_lines, args.tail, args.full)
            all_reports.append((f"{t.name}:{job_label}" if job_label else t.name, rep))

    if not any_loaded:
        print("no readable diagnostics data in any target", file=sys.stderr)
        return 1

    if len(all_reports) > 1:
        _delta(all_reports)

    print("\nThis is a REPORT, not a gate. It never fails a build: a false alarm here "
          "costs a real investigation.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
