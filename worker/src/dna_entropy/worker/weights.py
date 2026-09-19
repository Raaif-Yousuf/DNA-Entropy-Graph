"""Model weights cache: ``cache/models/<id>/...`` (docs/job_contract.md §1).

Mirrors a model's weights after the first download so a later job on a fresh VM restores
instead of re-downloading (the ``restoring-cache`` stage, docs/job_contract.md §4's stage
list). This module only handles the cache CHECK/RESTORE/SAVE side — staging a cached
weight set locally, and mirroring a freshly downloaded one back — never the download
itself, which is the model loader's own job
(:class:`~dna_entropy.predictors.evo.EvoPredictor`/``evo2``/``huggingface_hub``
internals). This module never imports ``torch``/``evo2`` (only ``predictors/evo.py``
may, per CLAUDE.md hard rule #1) and is fully testable with :class:`~.blobstore.LocalBlobstore`.
"""

from __future__ import annotations

from pathlib import Path

from .blobstore import Blobstore


def cache_prefix(model_id: str) -> str:
    """The store-relative prefix a model's cached weight files live under."""
    return f"cache/models/{model_id}/"


def is_cached(store: Blobstore, model_id: str) -> bool:
    """Return whether any file exists under this model's cache prefix."""
    return bool(store.list_prefix(cache_prefix(model_id)))


def restore_from_cache(store: Blobstore, model_id: str, local_dir: Path) -> int:
    """Copy every cached file for ``model_id`` down to ``local_dir``.

    Returns the number of files restored (``0`` means nothing was cached — the caller
    should let the model loader download fresh, then call :func:`save_to_cache` so the
    NEXT job benefits).
    """
    prefix = cache_prefix(model_id)
    paths = store.list_prefix(prefix)
    for p in paths:
        rel = p[len(prefix) :]
        store.download_file(p, local_dir / rel)
    return len(paths)


def save_to_cache(store: Blobstore, model_id: str, local_dir: Path) -> int:
    """Upload every file under ``local_dir`` to this model's cache prefix.

    Returns the number of files uploaded (``0`` if ``local_dir`` does not exist or is
    empty — never an error, since "nothing to cache yet" is a normal state, not a
    failure).
    """
    prefix = cache_prefix(model_id)
    if not local_dir.exists():
        return 0
    count = 0
    for f in sorted(local_dir.rglob("*")):
        if f.is_file():
            rel = f.relative_to(local_dir).as_posix()
            store.upload_file(f, f"{prefix}{rel}")
            count += 1
    return count
