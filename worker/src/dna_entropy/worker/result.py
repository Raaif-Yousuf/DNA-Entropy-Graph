"""``result.json``'s dataclasses (docs/job_contract.md §7) — deliberately its OWN module.

``JobResult``/``InputResult``/``ResultTiming``/``ResultGpu`` used to live in
``runner.py``, but that module also imports ``dna_entropy.pipeline`` (for
``pipeline.run()``), which imports ``numpy``. ``scripts/gen_manifest_schema.py`` (issue
#39) needs to import ``JobResult`` in ``ci-worker.yml``'s ``contract`` job, which runs
``uv run --with jsonschema python scripts/gen_manifest_schema.py`` **without installing
the worker package's real dependencies at all** — only ``jsonschema`` (and the stdlib) is
available there. Importing ``JobResult`` from ``runner.py`` would transitively require
``numpy`` just to read a dataclass definition, and fail. This module has no dependency on
``runner.py``, ``pipeline.py``, or anything third-party, so it (and
``worker.manifest``/``worker.status``, which have the same constraint) import cleanly in
that minimal environment. ``runner.py`` imports these types from here, same as before.
"""

from __future__ import annotations

import dataclasses
from dataclasses import dataclass, field


@dataclass
class InputResult:
    """One ``result.json`` ``inputs[]`` entry."""

    id: str
    status: str  # "done" | "failed" | "cancelled"
    outputs: list[str] = field(default_factory=list)
    error: dict | None = None


@dataclass
class ResultTiming:
    """``result.json``'s ``timing`` field. Field names are camelCase directly (like
    ``status.StatusDocument``'s) so ``dataclasses.asdict()`` IS the correct wire dict with
    no separate translation layer to keep in sync — see :meth:`JobResult.to_dict`."""

    startedAt: str
    finishedAt: str


@dataclass
class ResultGpu:
    """``result.json``'s ``gpu`` field. ``None``/``False`` off a real GPU VM (design §5.4);
    this worker never ran on one tonight."""

    name: str | None = None
    zone: str | None = None
    spot: bool = False


@dataclass
class JobResult:
    """The parsed shape of ``result.json`` (docs/job_contract.md §7).

    Field names are camelCase directly (matching ``status.StatusDocument``'s choice) so
    this dataclass IS ``worker/schema_gen.py``'s schema source of truth AND
    :meth:`to_dict`'s serialization both, from the same field list — a hand-written
    ``to_dict`` that quietly drifted from the schema (nesting ``timing``/``gpu`` without
    them being real fields) was the bug a prior refactor fixed; see
    ``test_job_result_to_dict_matches_its_own_dataclass_shape`` in test_worker_runner.py.
    """

    schema: int
    jobId: str
    status: str  # "done" | "failed" | "cancelled" (job level; see per-input status too)
    inputs: list[InputResult]
    timing: ResultTiming
    gpu: ResultGpu = field(default_factory=ResultGpu)
    error: dict | None = None

    def to_dict(self) -> dict:
        return dataclasses.asdict(self)
