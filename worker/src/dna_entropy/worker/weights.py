"""Model weights cache: ``cache/models/<id>/...`` at the BUCKET ROOT (docs/job_contract.md §1).

Mirrors a model's weights after the first download so a later job on a fresh VM restores
instead of re-downloading (the ``restoring-cache`` stage, docs/job_contract.md §4). This
module knows only paths and a :class:`~.blobstore.Blobstore`; it never imports
``torch``/``evo2`` (only ``predictors/evo.py`` may, CLAUDE.md hard rule #1) and never does
the download itself, which is the model loader's job.

Completeness (issue #75): ``_COMPLETE.json`` (``complete: true``) is written LAST by
:func:`save_to_cache`, which first overwrites any older marker with ``complete: false``, and
lists every file with its size. :func:`is_cached` is true only when it exists, and
:func:`restore_from_cache` verifies each size, so an interrupted mirror (files, no marker) is
never restored as if complete. A Hugging Face cache stores each file once under ``blobs/``
and links it from ``snapshots/``; symlinks are recorded in the marker and recreated on
restore instead of being uploaded a second time.

The cache lives at the bucket root, NOT under the job prefix: the job store is
``gs://<bucket>/jobs/<id>/``, so :func:`cache_store_for` builds a second store at the bucket
root. A local run has no bucket and no cache (:func:`cache_store_for` returns ``None``).

Known limit: the GCS blobstore reads and writes each file whole in memory, so a multi-GB
shard is held in RAM during a mirror or restore; streaming transfer is issue #496.
"""

from __future__ import annotations

import contextlib
import json
import os
import shutil
from collections.abc import Callable
from pathlib import Path

from .blobstore import Blobstore, BlobstoreError, GcsBlobstore, _checked_relative

MARKER_NAME = "_COMPLETE.json"
_MARKER_SCHEMA = 1


class WeightsCacheError(Exception):
    """A cached weight set failed verification; the caller downloads fresh instead."""


def cache_prefix(model_id: str) -> str:
    """The store-relative prefix a model's cached weight files live under."""
    return f"cache/models/{model_id}/"


def cache_store_for(job_store: Blobstore) -> Blobstore | None:
    """The bucket-root store the cache lives in, or ``None`` when there is no bucket."""
    if isinstance(job_store, GcsBlobstore):
        return GcsBlobstore(
            job_store.bucket,
            "",
            opener=job_store._opener,
            token_provider=job_store._tokens,
            sleep=job_store._sleep,
            rand=job_store._rand,
        )
    return None


def hf_hub_dir() -> Path:
    """Where ``huggingface_hub`` puts model repos: ``HF_HUB_CACHE``, else ``$HF_HOME/hub``,
    else ``~/.cache/huggingface/hub`` (the library's own resolution order)."""
    explicit = os.environ.get("HF_HUB_CACHE") or os.environ.get("HUGGINGFACE_HUB_CACHE")
    if explicit:
        return Path(explicit)
    home = os.environ.get("HF_HOME")
    if home:
        return Path(home) / "hub"
    return Path.home() / ".cache" / "huggingface" / "hub"


def snapshot_tree(local_dir: Path) -> frozenset[str]:
    """Relative posix paths of every file or symlink under ``local_dir`` (empty if absent)."""
    if not local_dir.is_dir():
        return frozenset()
    return frozenset(
        p.relative_to(local_dir).as_posix() for p in local_dir.rglob("*") if p.is_symlink() or p.is_file()
    )


def is_cached(store: Blobstore, model_id: str) -> bool:
    """Whether a COMPLETE cache exists: a readable marker with ``complete: true``, not
    merely some file (or a marker a later, interrupted re-mirror has invalidated)."""
    path = f"{cache_prefix(model_id)}{MARKER_NAME}"
    if not store.exists(path):
        return False
    try:
        return json.loads(store.read_text(path)).get("complete") is True
    except (BlobstoreError, ValueError, AttributeError):
        return False


def _validate_marker_paths(local_dir: Path, files: dict[str, int], symlinks: dict[str, str]) -> None:
    """Refuse a marker naming anything outside ``local_dir`` (``..``, absolute, drive paths).

    A symlink TARGET is legitimately relative with ``..`` (``../../blobs/<sha>``), so it is
    checked by where it would resolve, which must stay inside ``local_dir``.
    """
    try:
        for rel in (*files, *symlinks):
            _checked_relative(rel)
    except BlobstoreError as exc:
        raise WeightsCacheError(f"unsafe path in cache marker: {exc}") from exc
    root = os.path.normpath(local_dir)
    for rel, target in symlinks.items():
        resolved = os.path.normpath(os.path.join(os.path.dirname(os.path.join(root, rel)), target))
        inside = resolved == root or resolved.startswith(root + os.sep)
        if os.path.isabs(target) or target.startswith(("/", "\\")) or not inside:
            raise WeightsCacheError(f"unsafe symlink target in cache marker: {rel} -> {target}")


def restore_from_cache(store: Blobstore, model_id: str, local_dir: Path) -> int:
    """Copy a complete cached weight set down into ``local_dir``.

    Returns the number of files restored (``0``: no complete cache, nothing touched; the
    caller lets the loader download, then calls :func:`save_to_cache`). Files land in a
    staging folder beside ``local_dir``'s contents, are size-checked against the marker, and
    only then moved into place. Any failure (a missing object, a size mismatch, an unsafe
    marker path, an OSError during the move) raises :class:`WeightsCacheError` after
    removing the staging folder AND everything this call created, so ``local_dir`` is as it
    was (a file that already existed is overwritten, not restored: HF blobs are
    content-addressed).
    """
    prefix = cache_prefix(model_id)
    if not is_cached(store, model_id):
        return 0
    try:
        marker = json.loads(store.read_text(f"{prefix}{MARKER_NAME}"))
        files: dict[str, int] = dict(marker["files"])
        symlinks: dict[str, str] = dict(marker.get("symlinks", {}))
    except (BlobstoreError, ValueError, KeyError, TypeError) as exc:
        raise WeightsCacheError(f"cache marker unreadable: {exc}") from exc
    _validate_marker_paths(local_dir, files, symlinks)

    staging = local_dir / f".deg-restore-{model_id}"
    shutil.rmtree(staging, ignore_errors=True)
    created_files: list[Path] = []
    created_dirs: list[Path] = []

    def _mkdir(d: Path) -> None:
        missing = []
        while not d.exists():
            missing.append(d)
            d = d.parent
        for m in reversed(missing):
            m.mkdir()
            created_dirs.append(m)

    try:
        for rel, size in files.items():
            dest = staging / rel
            store.download_file(f"{prefix}{rel}", dest)
            if dest.stat().st_size != size:
                raise WeightsCacheError(f"{rel}: {dest.stat().st_size} bytes, marker says {size}")
        for rel in files:
            final = local_dir / rel
            _mkdir(final.parent)
            existed = final.exists()
            os.replace(staging / rel, final)
            if not existed:
                created_files.append(final)
        for rel, target in symlinks.items():
            link = local_dir / rel
            _mkdir(link.parent)
            if link.is_symlink() or link.exists():
                link.unlink()
            try:
                os.symlink(target, link)
            except OSError:
                # no symlink privilege (Windows): a copy of the target is equivalent for a loader
                shutil.copyfile((link.parent / target).resolve(), link)
            created_files.append(link)
    except (BlobstoreError, OSError, WeightsCacheError) as exc:
        for f in created_files:
            f.unlink(missing_ok=True)
        shutil.rmtree(staging, ignore_errors=True)
        for d in reversed(created_dirs):
            with contextlib.suppress(OSError):
                d.rmdir()
        if isinstance(exc, WeightsCacheError):
            raise
        raise WeightsCacheError(f"restore failed and was rolled back: {exc}") from exc
    finally:
        shutil.rmtree(staging, ignore_errors=True)
    return len(files)


def save_to_cache(
    store: Blobstore,
    model_id: str,
    local_dir: Path,
    *,
    should_stop: Callable[[], bool] | None = None,
) -> int:
    """Upload every file under ``local_dir`` to this model's cache prefix, then write the
    completion marker LAST. The marker claims the WHOLE set, so the caller must only pass a
    ``local_dir`` that holds exactly one complete model (the runner mirrors only from an HF
    cache that was empty before the load).

    ``should_stop`` is polled before each file; when it returns true the mirror ends with no
    marker and ``0`` is returned. Returns the number of files uploaded; ``0`` (and no marker)
    if there was nothing to upload. An upload that raises leaves no marker, so the partial
    set is never restored.
    """
    prefix = cache_prefix(model_id)
    if not local_dir.exists():
        return 0
    # Blobstore has no delete, so an existing marker is invalidated by overwriting it with an
    # explicit not-complete one BEFORE any object is replaced: an interrupted re-mirror must
    # never leave the old marker vouching for a mix of old and new objects.
    marker_path = f"{prefix}{MARKER_NAME}"
    if store.exists(marker_path):
        store.write_text(
            marker_path, json.dumps({"schema": _MARKER_SCHEMA, "model": model_id, "complete": False}) + "\n"
        )
    files: dict[str, int] = {}
    symlinks: dict[str, str] = {}
    for f in sorted(local_dir.rglob("*")):
        rel = f.relative_to(local_dir).as_posix()
        if rel.startswith(".deg-restore-"):
            continue
        if f.is_symlink():
            symlinks[rel] = os.readlink(f).replace(os.sep, "/")
        elif f.is_file():
            if should_stop is not None and should_stop():
                return 0
            store.upload_file(f, f"{prefix}{rel}")
            files[rel] = f.stat().st_size
    if not files:
        return 0
    marker = {
        "schema": _MARKER_SCHEMA,
        "model": model_id,
        "complete": True,
        "files": files,
        "symlinks": symlinks,
    }
    store.write_text(marker_path, json.dumps(marker, indent=2) + "\n")
    return len(files)
