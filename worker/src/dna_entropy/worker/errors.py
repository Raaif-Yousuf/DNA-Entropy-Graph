"""The worker's own error-code taxonomy (docs/job_contract.md §4's ``error.code`` field).

Every code any Python module under ``dna_entropy`` — or ``worker/vm/startup.sh``, which
hands off to it — can legitimately produce is enumerated here ONCE. Generated into
``docs/contract/error-codes.json`` by ``scripts/gen_manifest_schema.py``, the same
generate-and-``--check`` pattern as the manifest/status/result JSON schemas (issue #39),
so the worker and a future C# ``ErrorCatalog`` cannot silently drift (issue #254).

**Cross-referenced against ``docs/copy_catalog.md`` section 3** (the app's full 34-code
error catalog, spanning app/OAuth/Compute-API/worker layers). Every code below SHOULD also
appear there — checked by ``test_every_worker_error_code_is_documented_in_copy_catalog``
in ``test_worker_errors.py`` at the time this module was written, and the two gaps that
check found (``MANIFEST_INVALID``, ``WORKER_VERSION_MISMATCH``) are recorded on each
entry's own ``note`` field below rather than silently fixed by inventing catalog copy —
see this module's own closing report on issue #254 for the disposition. The reverse is
NOT required: most of ``copy_catalog.md``'s 34 codes are app/OAuth/Compute-API-layer
failures (``SIGNIN_EXPIRED``, ``NO_BILLING``, ``GPU_STOCKOUT``, the whole zone-ladder
family, ...) that this Python package's own code never raises — those belong to the C#
``DnaEntropyGraph.Cloud`` layer's error taxonomy, a different generation problem for a
different (C#) ``ErrorCatalog``, not this file's job.
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class ErrorCodeSpec:
    """One entry of the worker's error-code taxonomy."""

    code: str
    raised_by: str  # dotted path to the exception class or code site — for a human
    # reading the generated JSON, and for test_worker_errors.py's introspection checks.
    retriable: bool
    note: str = ""


# Adding a NEW code anywhere in the worker means adding it HERE first —
# test_worker_errors.py's source-scanning tests fail otherwise (the mechanical guard
# issue #254 asks for: "worker raises only listed codes").
WORKER_ERROR_CODES: tuple[ErrorCodeSpec, ...] = (
    ErrorCodeSpec(
        "MANIFEST_INVALID",
        "dna_entropy.worker.manifest.ManifestError",
        retriable=False,
        note="Not yet in docs/copy_catalog.md's 34 codes — the manifest is app-written "
        "and app-trusted, so a malformed one is an app-side bug a user should never "
        "see; flagged as a real gap on issue #254 rather than inventing user copy "
        "for a case the design doesn't expect to reach a user.",
    ),
    ErrorCodeSpec(
        "WORKER_VERSION_MISMATCH",
        "dna_entropy.worker.manifest.ManifestSchemaError",
        retriable=False,
        note="Issue #250's own code ('the manifest declares a schema this worker build "
        "does not understand'), with a clear two-versions message already — but not "
        "yet a row in docs/copy_catalog.md. Flagged as a real gap on issue #254.",
    ),
    ErrorCodeSpec(
        "MODEL_NEEDS_HOPPER",
        "dna_entropy.predictors.hardware.ModelNeedsHopperError",
        retriable=False,
        note="Matches docs/copy_catalog.md's ModelNeedsHopper row exactly.",
    ),
    ErrorCodeSpec(
        "MODEL_OOM",
        "dna_entropy.predictors.base.PredictorOOMError",
        retriable=True,
        note="Matches docs/copy_catalog.md's ModelOom row. Retriable: a SECOND OOM (the "
        "first is already handled internally by analysis/direction.py's halve-and-"
        "retry-once) propagates as this code; the app's [Lower context length] / "
        "[Use a bigger computer] actions are themselves the 'retry'.",
    ),
    ErrorCodeSpec(
        "INPUT_INVALID",
        "dna_entropy.validation.validators.ValidationError (also the generic "
        "fallback for dna_entropy.worker.blobstore.BlobstoreError/ManifestError reached per-input)",
        retriable=False,
        note="Matches docs/copy_catalog.md's InputInvalid row.",
    ),
    ErrorCodeSpec(
        "WORKER_CRASH",
        "dna_entropy.worker.runner (any exception with no more specific .code)",
        retriable=False,
        note="Matches docs/copy_catalog.md's WorkerCrash row. Also written directly by "
        "worker/vm/startup.sh's ERR trap for a failure before the container ever "
        "starts.",
    ),
    ErrorCodeSpec(
        "BATCH_LIMIT_EXCEEDED",
        "dna_entropy.worker.batch_limits.BatchLimitError",
        retriable=False,
        note="Not yet in docs/copy_catalog.md's 34 codes — issue #248's own new code, "
        "raised before any per-input work starts when a batch exceeds "
        "manifest.limits.maxInputs/maxTotalNt. Flagged as a real gap, same disposition "
        "as MANIFEST_INVALID/WORKER_VERSION_MISMATCH above (see #248's closing report "
        "for suggested copy).",
    ),
    ErrorCodeSpec(
        "GPU_NOT_VISIBLE",
        "worker/vm/startup.sh (nvidia-smi wait loop)",
        retriable=False,
        note="Matches docs/copy_catalog.md's GpuNotVisible row. Written by the startup "
        "script, not any Python module — the worker container never even starts in "
        "this case.",
    ),
    ErrorCodeSpec(
        "IMAGE_PULL_FAILED",
        "worker/vm/startup.sh (docker pull)",
        retriable=True,
        note="Matches docs/copy_catalog.md's ImagePullFailed row. Written by the startup "
        "script, not any Python module.",
    ),
)

WORKER_ERROR_CODE_INDEX: dict[str, ErrorCodeSpec] = {spec.code: spec for spec in WORKER_ERROR_CODES}
WORKER_ERROR_CODE_SET: frozenset[str] = frozenset(WORKER_ERROR_CODE_INDEX.keys())


def is_retriable(code: str) -> bool:
    """Return whether ``code`` is retriable per the registry; unknown codes are treated as
    NOT retriable (fail closed — never suggest "just try again" for a code the taxonomy
    doesn't even recognize)."""
    spec = WORKER_ERROR_CODE_INDEX.get(code)
    return spec.retriable if spec is not None else False
