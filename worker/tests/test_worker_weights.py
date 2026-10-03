"""Tests for worker/weights.py: cache/models/<id>/... check/restore/save, LocalBlobstore only.

Issue #75. The cache is complete only when its ``_COMPLETE.json`` marker exists (written
LAST by save_to_cache); a partial mirror is never restored as if complete.
"""

from __future__ import annotations

import json
import os
from pathlib import Path

import pytest

from dna_entropy.worker.blobstore import BlobstoreError, GcsBlobstore, LocalBlobstore
from dna_entropy.worker.weights import (
    MARKER_NAME,
    WeightsCacheError,
    cache_prefix,
    cache_store_for,
    hf_hub_dir,
    is_cached,
    restore_from_cache,
    save_to_cache,
)


def _make_tree(root: Path) -> None:
    (root / "blobs").mkdir(parents=True)
    (root / "blobs" / "abc").write_bytes(b"shard-bytes")
    (root / "refs").mkdir()
    (root / "refs" / "main").write_text("rev1", encoding="utf-8")


def test_cache_prefix_shape() -> None:
    assert cache_prefix("evo2_7b") == "cache/models/evo2_7b/"


def test_is_cached_false_when_nothing_there(tmp_path: Path) -> None:
    assert is_cached(LocalBlobstore(tmp_path), "evo2_7b") is False


def test_files_without_the_marker_are_not_a_complete_cache(tmp_path: Path) -> None:
    """A partial/interrupted mirror (files, no marker) must read as NOT cached."""
    store = LocalBlobstore(tmp_path)
    store.write_text("cache/models/evo2_7b/blobs/abc", "half-uploaded")
    assert is_cached(store, "evo2_7b") is False
    assert restore_from_cache(store, "evo2_7b", tmp_path / "local") == 0
    assert not (tmp_path / "local").exists() or not any((tmp_path / "local").rglob("*"))


def test_is_cached_does_not_confuse_different_models(tmp_path: Path) -> None:
    src = tmp_path / "src"
    _make_tree(src)
    store = LocalBlobstore(tmp_path / "store")
    save_to_cache(store, "evo2_7b_262k", src)
    assert is_cached(store, "evo2_7b_262k") is True
    assert is_cached(store, "evo2_7b") is False


def test_save_writes_marker_last_and_lists_files(tmp_path: Path) -> None:
    src = tmp_path / "src"
    _make_tree(src)
    store = LocalBlobstore(tmp_path / "store")
    order: list[str] = []
    real_upload, real_write = store.upload_file, store.write_text
    store.upload_file = lambda s, p: (order.append(p), real_upload(s, p))[1]  # type: ignore[method-assign]
    store.write_text = lambda p, t: (order.append(p), real_write(p, t))[1]  # type: ignore[method-assign]

    count = save_to_cache(store, "evo2_7b", src)

    assert count == 2
    assert order[-1] == f"cache/models/evo2_7b/{MARKER_NAME}"
    assert is_cached(store, "evo2_7b") is True
    marker = json.loads(store.read_text(f"cache/models/evo2_7b/{MARKER_NAME}"))
    assert marker["files"] == {"blobs/abc": 11, "refs/main": 4}


def test_upload_failure_leaves_no_marker_so_nothing_is_restored(tmp_path: Path) -> None:
    src = tmp_path / "src"
    _make_tree(src)
    store = LocalBlobstore(tmp_path / "store")
    calls = 0
    real_upload = store.upload_file

    def _flaky(s, p):
        nonlocal calls
        calls += 1
        if calls == 2:
            raise BlobstoreError("network went away")
        real_upload(s, p)

    store.upload_file = _flaky  # type: ignore[method-assign]
    with pytest.raises(BlobstoreError):
        save_to_cache(store, "evo2_7b", src)
    assert is_cached(store, "evo2_7b") is False
    assert restore_from_cache(store, "evo2_7b", tmp_path / "local") == 0


def test_save_to_cache_missing_dir_is_zero_and_writes_no_marker(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path / "store")
    assert save_to_cache(store, "evo2_7b", tmp_path / "does_not_exist") == 0
    assert is_cached(store, "evo2_7b") is False


def test_roundtrip_save_then_restore_bytes_identical(tmp_path: Path) -> None:
    src = tmp_path / "src"
    src.mkdir()
    (src / "weights.bin").write_bytes(b"\x00\x01\x02binary-ish")
    store = LocalBlobstore(tmp_path / "store")
    save_to_cache(store, "evo2_7b", src)

    restored = tmp_path / "restored"
    assert restore_from_cache(store, "evo2_7b", restored) == 1
    assert (restored / "weights.bin").read_bytes() == b"\x00\x01\x02binary-ish"
    assert not [p for p in restored.iterdir() if p.name.startswith(".deg-restore")]


def test_save_stops_between_files_without_a_marker_when_asked_to(tmp_path: Path) -> None:
    src = tmp_path / "src"
    _make_tree(src)
    store = LocalBlobstore(tmp_path / "store")
    calls = 0

    def _stop() -> bool:
        nonlocal calls
        calls += 1
        return calls >= 2  # stop before the second file

    assert save_to_cache(store, "evo2_7b", src, should_stop=_stop) == 0
    assert is_cached(store, "evo2_7b") is False
    assert len(store.list_prefix("cache/models/evo2_7b/")) == 1  # first file went, no marker


def _seed_cache(tmp_path: Path) -> LocalBlobstore:
    src = tmp_path / "src"
    _make_tree(src)
    store = LocalBlobstore(tmp_path / "store")
    save_to_cache(store, "evo2_7b", src)
    return store


def _tree_state(root: Path) -> list[str]:
    return sorted(p.relative_to(root).as_posix() + ("/" if p.is_dir() else "") for p in root.rglob("*"))


def test_restore_that_fails_while_moving_leaves_the_hf_dir_exactly_as_before(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    store = _seed_cache(tmp_path)
    local = tmp_path / "local"
    (local / "other").mkdir(parents=True)
    (local / "other" / "keep.bin").write_bytes(b"keep")
    before = _tree_state(local)

    real_replace = os.replace
    calls = 0

    def _failing_replace(src, dst):
        nonlocal calls
        if ".deg-restore" in str(dst):  # the blobstore's own staging writes go through os.replace too
            return real_replace(src, dst)
        calls += 1
        if calls == 2:  # the 2nd MOVE into the HF dir
            raise OSError("disk full")
        return real_replace(src, dst)

    monkeypatch.setattr("dna_entropy.worker.weights.os.replace", _failing_replace)
    with pytest.raises(WeightsCacheError):
        restore_from_cache(store, "evo2_7b", local)
    assert _tree_state(local) == before


@pytest.mark.parametrize(
    ("files", "symlinks"),
    [
        ({"../escape.bin": 1}, {}),
        ({"/abs.bin": 1}, {}),
        ({"C:/abs.bin": 1}, {}),
        ({"ok.bin": 1}, {"link": "/etc/passwd"}),
        ({"ok.bin": 1}, {"link": "../../outside"}),
        ({"ok.bin": 1}, {"../link": "ok.bin"}),
    ],
)
def test_restore_refuses_a_marker_with_unsafe_paths(tmp_path: Path, files: dict, symlinks: dict) -> None:
    store = LocalBlobstore(tmp_path / "store")
    store.write_text("cache/models/evo2_7b/ok.bin", "x")  # present, so only validation can refuse
    marker = {"schema": 1, "model": "evo2_7b", "complete": True, "files": files, "symlinks": symlinks}
    store.write_text(f"cache/models/evo2_7b/{MARKER_NAME}", json.dumps(marker))
    local = tmp_path / "local"
    with pytest.raises(WeightsCacheError, match="unsafe"):
        restore_from_cache(store, "evo2_7b", local)
    assert not local.exists() or not any(local.rglob("*"))
    assert not (tmp_path / "escape.bin").exists()


@pytest.mark.skipif(os.name == "nt", reason="HF cache symlinks are a Linux VM concern")
def test_symlinks_are_recorded_not_uploaded_twice_and_are_recreated(tmp_path: Path) -> None:
    src = tmp_path / "src"
    _make_tree(src)
    (src / "snapshots" / "rev1").mkdir(parents=True)
    os.symlink("../../blobs/abc", src / "snapshots" / "rev1" / "model.bin")
    store = LocalBlobstore(tmp_path / "store")

    assert save_to_cache(store, "evo2_7b", src) == 2  # the blob once, not blob + symlink target
    assert "cache/models/evo2_7b/snapshots/rev1/model.bin" not in store.list_prefix("cache/")

    restored = tmp_path / "restored"
    restore_from_cache(store, "evo2_7b", restored)
    link = restored / "snapshots" / "rev1" / "model.bin"
    assert link.is_symlink()
    assert link.read_bytes() == b"shard-bytes"


def test_restore_refuses_a_file_whose_size_disagrees_with_the_marker(tmp_path: Path) -> None:
    src = tmp_path / "src"
    _make_tree(src)
    store = LocalBlobstore(tmp_path / "store")
    save_to_cache(store, "evo2_7b", src)
    store.write_text("cache/models/evo2_7b/blobs/abc", "truncated")  # corrupt after the fact

    local = tmp_path / "local"
    with pytest.raises(WeightsCacheError):
        restore_from_cache(store, "evo2_7b", local)
    # nothing half-restored is left where the model loader would find it
    assert not (local / "blobs" / "abc").exists()
    assert not any(p.name.startswith(".deg-restore") for p in local.iterdir())


def test_hf_hub_dir_resolution_order(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    for var in ("HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE", "HF_HOME"):
        monkeypatch.delenv(var, raising=False)
    monkeypatch.setenv("HF_HOME", str(tmp_path / "home"))
    assert hf_hub_dir() == tmp_path / "home" / "hub"
    monkeypatch.setenv("HF_HUB_CACHE", str(tmp_path / "hub"))
    assert hf_hub_dir() == tmp_path / "hub"


def test_cache_store_is_bucket_root_for_gcs_and_none_for_local(tmp_path: Path) -> None:
    job_store = GcsBlobstore("deg-bucket", "jobs/j1/", opener=lambda r: None)  # type: ignore[arg-type]
    cache = cache_store_for(job_store)
    assert isinstance(cache, GcsBlobstore)
    assert cache.bucket == "deg-bucket"
    assert cache.prefix == ""  # NOT jobs/j1/: the cache must outlive and span jobs
    assert cache_store_for(LocalBlobstore(tmp_path)) is None


def test_startup_script_points_hf_home_at_a_mount_the_worker_user_can_write() -> None:
    """The container runs as uid 10001; the HF cache mount must be owned by it and named by HF_HOME."""
    text = (Path(__file__).parents[1] / "vm" / "startup.sh").read_text(encoding="utf-8")
    assert "-e HF_HOME=/hf-cache" in text
    assert "-v /var/cache/deg-hf:/hf-cache" in text
    assert "chown 10001:10001 /var/cache/deg-hf" in text
    assert "/root/.cache/huggingface" not in text.replace("`/root/.cache/huggingface`", "")
    run = text.index("docker run --rm")
    assert text.index("mkdir -p /var/cache/deg-hf") < run
    assert text.index("chown 10001:10001 /var/cache/deg-hf") < text.index("docker run --rm")


def test_an_interrupted_re_mirror_invalidates_the_old_marker(tmp_path: Path) -> None:
    """A complete set is on the bucket; a later re-mirror over the same prefix dies on its
    2nd file. The OLD marker must not still vouch for the mixed old/new objects."""
    store = _seed_cache(tmp_path)
    assert is_cached(store, "evo2_7b") is True
    src = tmp_path / "src2"
    _make_tree(src)
    calls = 0
    real_upload = store.upload_file

    def _flaky(s, p):
        nonlocal calls
        calls += 1
        if calls == 2:
            raise BlobstoreError("vm stopping")
        real_upload(s, p)

    store.upload_file = _flaky  # type: ignore[method-assign]
    with pytest.raises(BlobstoreError):
        save_to_cache(store, "evo2_7b", src)
    assert is_cached(store, "evo2_7b") is False
    assert restore_from_cache(store, "evo2_7b", tmp_path / "local") == 0


def test_a_marker_without_the_complete_flag_is_not_cached(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path / "store")
    store.write_text(f"cache/models/evo2_7b/{MARKER_NAME}", json.dumps({"schema": 1, "files": {}}))
    assert is_cached(store, "evo2_7b") is False
    store.write_text(f"cache/models/evo2_7b/{MARKER_NAME}", "not json")
    assert is_cached(store, "evo2_7b") is False
