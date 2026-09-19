"""``provenance.json``: what produced these files (issue #82).

Reproducibility record: model, worker version, run configuration, per-contig
windowing/direction derivation (including the OOM-halving retry's FINAL W/S, issue #81),
input identity, and wall time. Two runs of the same input on the same GPU class must
produce identical entropy files and provenance differing ONLY in :data:`GENERATED_AT_KEY`
and :data:`WALL_TIME_KEY` (issue #82's own named Observable) -- every other field is a
pure function of the run configuration and the input, never of the clock.

**Scope, deliberately drawn at the pipeline/worker boundary:** this module (``writers/``,
pipeline-layer) builds every field the PIPELINE can know without touching a GPU: worker
version, predictor kind/model/device/seed, K/W/S/direction/seam per contig, wall time,
sequence identity. It cannot and must not import ``torch`` (Hard Rule 1: all Evo-specific
code lives in ``predictors/evo.py``) and has no visibility into the container image digest
or the input file's original bytes (the worker layer downloads those before ``pipeline.run()``
ever sees them). Those fields (``gpu``, ``versions.torch``/``.evo2``/``.flash_attn``,
``image_digest``, ``input_sha256``) are therefore left ``None`` here and filled in by
whichever caller HAS that context, via the ``extra`` parameter below -- ``pipeline.run()``
accepts a ``provenance_extra`` dict for exactly this (see its own docstring) and merges it
in unconditionally, so a worker-layer caller (``worker/runner.py``) can supply real GPU/
version/digest/hash data without this module needing to know any of it exists.
"""

from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from .. import __version__
from .base import write_text_lf

PROVENANCE_SCHEMA_VERSION = 1

# The only two top-level keys allowed to differ between two provenance.json files
# produced from the SAME input under the SAME configuration (issue #82's Observable).
GENERATED_AT_KEY = "generated_at"
WALL_TIME_KEY = "wall_time_seconds"

# Worker-layer-only fields (see module docstring): always present, `None` unless a caller
# supplies them via `extra`, so a reader can tell "not yet known" from "genuinely absent".
_WORKER_LAYER_DEFAULTS: dict[str, Any] = {
    "gpu": None,
    "versions": {"torch": None, "evo2": None, "flash_attn": None},
    "image_digest": None,
    "input_sha256": None,
}


def contig_provenance(
    *,
    name: str,
    length: int,
    context_length: int,
    window: int,
    stride: int,
    ceiling: int,
    direction: str,
    seam: int | None,
    reduced_context_count: int,
) -> dict[str, Any]:
    """One contig's windowing/direction record.

    ``context_length``/``window``/``stride`` here are the run's NOMINAL configuration
    (``RunConfig.context_length`` and the ``(window, stride)`` a real
    :class:`~dna_entropy.analysis.direction.DirectionResult` actually ran with) -- after an
    OOM-halving retry (issue #81), ``window``/``stride`` already reflect the FINAL, halved
    values (:class:`~dna_entropy.analysis.direction.DirectionResult.window`/``.stride`` are
    threaded straight from the executed :class:`~dna_entropy.analysis.windowing.WindowPlan`,
    never the pre-retry one), and ``k_used`` below is derived as ``window - stride`` rather
    than trusted from the nominal ``context_length``.

    MEASURED 2026-09-19 (issue #407, disproven-then-fixed the same session): the
    original THEORY here was that ``DirectionResult.context_length`` "going stale" after a
    halving retry was itself the bug. Reproduced by hand and confirmed narrower: reporting
    the nominal, configured K in ``context_length`` is legitimate and NOT a bug --
    ``analysis/direction.py::_combine`` USING that same shared, nominal value as BOTH
    directions' qualification threshold was the real bug (a one-sided halving could then
    permanently lock the halved direction out of "qualifies", silently collapsing
    combined/averaged mode to the other direction). Fixed in ``_combine`` itself, which now
    takes each direction's own actually-achieved ``window - stride``; this function's
    ``k_used`` derivation was already correct and needed no change.
    """
    return {
        "name": name,
        "length": length,
        "context_length": context_length,
        "window": window,
        "stride": stride,
        "k_used": window - stride,
        "ceiling": ceiling,
        "direction": direction,
        "seam": seam,
        "reduced_context_count": reduced_context_count,
    }


def build_run_provenance(
    *,
    name: str,
    predictor_kind: str,
    model: str,
    device: str,
    seed: int,
    direction: str,
    ambiguity_policy: str,
    rna: bool,
    contigs: list[dict[str, Any]],
    wall_time_seconds: float,
    generated_at: str | None = None,
    extra: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Assemble the full provenance dict. ``contigs`` is a list of
    :func:`contig_provenance` dicts, one per analyzed contig, in run order.

    ``generated_at`` defaults to the current UTC time in ISO 8601
    (``datetime.now(UTC).isoformat()``); a caller may pass a fixed value for a
    deterministic test. ``extra``, when given, overlays :data:`_WORKER_LAYER_DEFAULTS`
    (a caller passing an unrecognized key gets it merged in as-is, additively -- this
    function never rejects an extra key, since the worker layer may know about fields
    this module was written before).
    """
    data: dict[str, Any] = {
        "schema": PROVENANCE_SCHEMA_VERSION,
        "worker_version": __version__,
        GENERATED_AT_KEY: generated_at or datetime.now(UTC).isoformat(),
        "run": {
            "name": name,
            "direction": direction,
            "ambiguity_policy": ambiguity_policy,
            "rna": rna,
        },
        "predictor": {
            "kind": predictor_kind,
            "model": model,
            "device": device,
            "seed": seed,
        },
        "contigs": contigs,
        WALL_TIME_KEY: wall_time_seconds,
    }
    data.update(_WORKER_LAYER_DEFAULTS)
    if extra:
        data.update(extra)
    return data


class ProvenanceWriter:
    """Writes ``provenance.json`` (issue #82). One per pipeline run, alongside every
    other output in ``cfg.out_dir`` -- deliberately named without a ``<name>.`` prefix
    (unlike every other writer here), matching ``docs/job_contract.md`` section 2's own
    ``output/<name>/provenance.json`` layout literally.
    """

    def write(self, *, out_dir: str, data: dict[str, Any]) -> str:
        text = json.dumps(data, indent=2, sort_keys=True) + "\n"
        return write_text_lf(Path(out_dir) / "provenance.json", text)
